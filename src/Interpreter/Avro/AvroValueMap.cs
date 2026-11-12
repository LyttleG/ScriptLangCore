namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The mapping between a SCRIPT value and Avro's data model, in both directions. It is one table
    /// because the two directions must agree: a value this class encodes must be a value it can decode, and the
    /// places they could drift (a union's branch order, the case rule for a field name, what a record becomes)
    /// are stated once here rather than once per direction.
    ///
    /// A script value is read through the SAME shapes its own language gives it — a script dictionary, a
    /// <see cref="ClassInstance"/>, a <see cref="StructInstance"/>, a CLR dictionary, a list or an array — and a
    /// record decodes to a script dictionary unless the caller names a type, in which case the engine's existing
    /// typed reconstruction builds the class or struct. Nothing here uses System.Reflection: every lookup goes
    /// through the interpreter's own registries, which is what keeps the AOT artifact root-free.</summary>
    internal static class AvroValueMap
    {
        /// <summary>How deep a value may nest before the engine refuses. A recursive record is legal in Avro, so
        /// the DEPTH is the thing that bounds the walk: without a limit a cyclic script value (a dictionary that
        /// holds itself) would recurse until the stack ended, which is a crash rather than a refusal.</summary>
        private const int MaxDepth = 512;

        /// <summary>How many zero-byte items one array or map block may declare. An item that encodes to no bytes
        /// at all (a null, an empty record) cannot be bounded by the payload's own length, so a block count is the
        /// only thing saying how many to materialise: this is the ceiling, and it is refused by name rather than
        /// allocated blindly.</summary>
        private const long MaxZeroByteItems = 1_048_576;

        // ── script value → Avro bytes ──

        internal static void Write(AvroBinaryWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive)
            => WriteCore(writer, schema, value, member, caseSensitive, 0);

        private static void WriteCore(AvroBinaryWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive, int depth)
        {
            if (depth > MaxDepth)
                throw new ScriptException(
                    $"{member}: the value nests more than {MaxDepth} levels deep. Avro allows a recursive record, " +
                    "so depth is what bounds the walk; a value that holds itself never ends.");

            // A LOGICAL TYPE IS THE BASE TYPE'S BYTES with a promise about them, so it is converted here and the
            // encoding below is never restated: one branch, before the kind switch, or a timestamp would need an
            // arm in every kind it could be written as.
            if (schema.LogicalType is not null)
            {
                AvroLogicalTypes.Write(writer, schema, value, member, caseSensitive);
                return;
            }

            switch (schema.Kind)
            {
                case AvroKind.Null:
                    if (value is not null)
                        throw NotAValue(member, schema, value, "null");
                    break;   // null writes no bytes at all

                case AvroKind.Boolean:
                    if (value is not bool flag) throw NotAValue(member, schema, value, "a bool");
                    writer.WriteBoolean(flag);
                    break;

                case AvroKind.Int:
                    writer.WriteInt(ToInt(value, schema, member));
                    break;

                case AvroKind.Long:
                    writer.WriteLong(ToLong(value, schema, member));
                    break;

                case AvroKind.Float:
                    writer.WriteFloat(ToFloat(value, schema, member));
                    break;

                case AvroKind.Double:
                    writer.WriteDouble(ToDouble(value, schema, member));
                    break;

                case AvroKind.Bytes:
                    if (value is not byte[] bytes) throw NotAValue(member, schema, value, "a byte array");
                    writer.WriteBytes(bytes);
                    break;

                case AvroKind.String:
                    if (value is not string text) throw NotAValue(member, schema, value, "a string");
                    writer.WriteString(text);
                    break;

                case AvroKind.Fixed:
                {
                    if (value is not byte[] fixedBytes) throw NotAValue(member, schema, value, "a byte array");
                    if (fixedBytes.Length != schema.FixedSize)
                        throw new ScriptException(
                            $"{member}: fixed '{schema.DisplayName}' is exactly {schema.FixedSize} byte(s), but " +
                            $"the value is {fixedBytes.Length}. A fixed carries no length, so the schema is the " +
                            "only thing that says how many bytes it is.");
                    writer.WriteFixed(fixedBytes);
                    break;
                }

                case AvroKind.Enum:
                {
                    if (value is not string symbol) throw NotAValue(member, schema, value, "a symbol name");
                    var index = schema.Symbols.IndexOf(symbol);
                    if (index < 0)
                        throw new ScriptException(
                            $"{member}: '{symbol}' is not a symbol of enum '{schema.DisplayName}'. Its symbols are " +
                            $"{DescribeSymbols(schema)}.");
                    writer.WriteEnumIndex(index);
                    break;
                }

                case AvroKind.Array:
                    WriteArray(writer, schema, value, member, caseSensitive, depth);
                    break;

                case AvroKind.Map:
                    WriteMap(writer, schema, value, member, caseSensitive, depth);
                    break;

                case AvroKind.Record:
                    WriteRecord(writer, schema, value, member, caseSensitive, depth);
                    break;

                case AvroKind.Union:
                {
                    // THE SPEC'S RULE, and the reason branch order is not cosmetic: the FIRST branch that fits
                    // wins, so ["null","string"] writes a null, never "".
                    var index = FirstFittingBranch(schema, value, caseSensitive);
                    if (index < 0)
                        throw new ScriptException(
                            $"{member}: {DescribeValue(value)} fits no branch of the union " +
                            $"({DescribeBranches(schema)}). A union writes the first branch that fits, so the value " +
                            "must be one the union declares.");
                    writer.WriteUnionIndex(index);
                    WriteCore(writer, schema.Branches[index], value, member, caseSensitive, depth + 1);
                    break;
                }

                default:
                    throw new ScriptException($"{member}: Avro kind '{schema.Kind}' cannot be encoded.");
            }
        }

        private static void WriteArray(AvroBinaryWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive, int depth)
        {
            var items = ItemsOf(value) ?? throw NotAValue(member, schema, value, "a list or an array");
            var count = items.Count;
            // ONE block, then the 0 that ends it. A count of 0 is the end marker itself, so an EMPTY collection
            // writes exactly one long and no block header (writing both would be two blocks: an empty one, then
            // the terminator, which some readers reject).
            if (count > 0)
            {
                writer.WriteBlockCount(count);
                foreach (var item in items)
                    WriteCore(writer, schema.Items!, item, member, caseSensitive, depth + 1);
            }
            writer.WriteBlockEnd();
        }

        private static void WriteMap(AvroBinaryWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive, int depth)
        {
            var pairs = PairsOf(value) ?? throw NotAValue(member, schema, value, "a dictionary");
            if (pairs.Count > 0)
            {
                writer.WriteBlockCount(pairs.Count);
                foreach (var (key, item) in pairs)
                {
                    // A map's key is always a string in Avro: there is no key schema to consult.
                    var name = key as string ?? key?.ToString()
                        ?? throw new ScriptException(
                            $"{member}: a map key is a string, not null. Map '{schema.DisplayName}' has no key " +
                            "schema, so every key is written as a string.");
                    writer.WriteString(name);
                    WriteCore(writer, schema.Values!, item, member, caseSensitive, depth + 1);
                }
            }
            writer.WriteBlockEnd();
        }

        private static void WriteRecord(AvroBinaryWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive, int depth)
        {
            if (value is null || !TryMembers(value, out var members))
                throw NotAValue(member, schema, value, "a dictionary, a class instance or a struct instance");

            foreach (var field in schema.Fields)
            {
                if (!members.TryGet(field.Name, caseSensitive, out var fieldValue))
                    throw new ScriptException(
                        $"{member}: record '{schema.DisplayName}' needs the field '{field.Name}', and the value " +
                        $"given has no such member (it has {DescribeMembers(members)}). A writer supplies every " +
                        "field in schema order; a default is what a READER fills in, not what a writer omits.");
                WriteCore(writer, field.Type, fieldValue, member, caseSensitive, depth + 1);
            }

            // AN UNKNOWN MEMBER IS REFUSED rather than dropped: silently discarding it would write a payload that
            // does not hold what the caller passed, and the caller would have no way to notice.
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in schema.Fields) declared.Add(field.Name);
            foreach (var name in members.Names)
            {
                var known = caseSensitive
                    ? declared.Contains(name)
                    : declared.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (known) continue;
                throw new ScriptException(
                    $"{member}: record '{schema.DisplayName}' has no field '{name}' (its fields are " +
                    $"{DescribeFields(schema)}). A member the schema does not declare would be written nowhere, " +
                    "so it is refused instead of dropped.");
            }
        }

        // ── Avro bytes → script value ──

        internal static object? Read(ref AvroBinaryReader reader, AvroSchema schema, string member)
            => ReadCore(ref reader, schema, member, 0);

        private static object? ReadCore(ref AvroBinaryReader reader, AvroSchema schema, string member, int depth)
        {
            if (depth > MaxDepth)
                throw new ScriptException(
                    $"{member}: the payload nests more than {MaxDepth} levels deep. Avro allows a recursive " +
                    "record, so depth is what bounds the walk.");

            if (schema.LogicalType is not null)
                return AvroLogicalTypes.Read(ref reader, schema, member);

            switch (schema.Kind)
            {
                case AvroKind.Null: return null;
                case AvroKind.Boolean: return reader.ReadBoolean("a boolean");
                // A number answers the LONG the engine's own JSON reader answers for an integer, so the two
                // payload formats produce the same script value for the same schema.
                case AvroKind.Int: return (long)reader.ReadInt("an int");
                case AvroKind.Long: return reader.ReadLong("a long");
                case AvroKind.Float: return reader.ReadFloat("a float");
                case AvroKind.Double: return reader.ReadDouble("a double");
                case AvroKind.Bytes: return reader.ReadBytes("bytes");
                case AvroKind.String: return reader.ReadString("a string");
                case AvroKind.Fixed: return reader.ReadFixed(schema.FixedSize, $"fixed '{schema.DisplayName}'");

                case AvroKind.Enum:
                    return schema.Symbols[reader.ReadEnumIndex(schema.Symbols.Count, $"enum '{schema.DisplayName}'")];

                case AvroKind.Array:
                {
                    // The SAME container the engine's JSON reader builds for an array, so the two payload formats
                    // hand a script the same kind of object for the same schema.
                    var list = new ScriptBoxedList(new TypeInfo(TokenType.Object, true, 0, 0, null));
                    var bytesPerItem = MinimumEncodedSize(schema.Items!);
                    while (true)
                    {
                        var count = reader.ReadBlockCount("array");
                        if (count == 0) break;
                        GuardBlockCount(count, bytesPerItem, reader.Remaining, member, schema);
                        for (var i = 0L; i < count; i++)
                            list.AddRaw(ReadCore(ref reader, schema.Items!, member, depth + 1)!);
                    }
                    return list;
                }

                case AvroKind.Map:
                {
                    var stringType = new TypeInfo(TokenType.String, false, 0, 0, null);
                    var map = CreateScriptDictionary(stringType, new TypeInfo(TokenType.Object, true, 0, 0, null));
                    // A map item is a KEY and a value, and the key is a string: at least its one-byte length. Counting
                    // only the value would under-state the block, which is the unsafe direction for a bound.
                    var bytesPerItem = MinimumEncodedSize(schema.Values!) + 1;
                    while (true)
                    {
                        var count = reader.ReadBlockCount("map");
                        if (count == 0) break;
                        GuardBlockCount(count, bytesPerItem, reader.Remaining, member, schema);
                        for (var i = 0L; i < count; i++)
                        {
                            var key = reader.ReadString($"a key of map '{schema.DisplayName}'");
                            map.AddRaw(key, ReadCore(ref reader, schema.Values!, member, depth + 1));
                        }
                    }
                    return map;
                }

                case AvroKind.Record:
                {
                    var stringType = new TypeInfo(TokenType.String, false, 0, 0, null);
                    var record = CreateScriptDictionary(
                        stringType, new TypeInfo(TokenType.Object, true, 0, 0, null), schema.Fields.Count);
                    foreach (var field in schema.Fields)
                        record.AddRaw(field.Name, ReadCore(ref reader, field.Type, member, depth + 1));
                    return record;
                }

                case AvroKind.Union:
                {
                    var index = reader.ReadUnionIndex(schema.Branches.Count, "union");
                    return ReadCore(ref reader, schema.Branches[index], member, depth + 1);
                }

                default:
                    throw new ScriptException($"{member}: Avro kind '{schema.Kind}' cannot be decoded.");
            }
        }

        /// <summary>Stops a block count from becoming a huge allocation. The payload's own length bounds any item
        /// that costs a byte or more; an item that costs NOTHING needs the separate ceiling, or a payload could ask
        /// for a billion nulls.</summary>
        private static void GuardBlockCount(long count, int bytesPerItem, int remaining, string member,
            AvroSchema schema)
        {
            if (bytesPerItem > 0)
            {
                if (count * bytesPerItem <= remaining) return;
                throw new ScriptException(
                    $"{member}: a block of '{schema.DisplayName}' declares {count} item(s) needing at least " +
                    $"{count * bytesPerItem} byte(s), but {remaining} byte(s) remain. The payload is truncated or " +
                    "the schema does not match it.");
            }
            if (count <= MaxZeroByteItems) return;
            throw new ScriptException(
                $"{member}: a block of '{schema.DisplayName}' declares {count} item(s) that each encode to no " +
                $"bytes, and this engine materialises at most {MaxZeroByteItems}. Nothing in the payload bounds " +
                "that count, so it is refused rather than allocated.");
        }

        /// <summary>The fewest bytes an item of this schema can take, which is what makes a block count checkable.
        /// Only null and a field-less record reach 0; every other kind writes at least one byte. A recursive record
        /// would walk forever, so the DEPTH bounds it and a schema at the limit answers 1 — the conservative
        /// direction, since a larger minimum only rejects payloads that could not have held the items anyway.</summary>
        private static int MinimumEncodedSize(AvroSchema schema, int depth = 0) => schema.Kind switch
        {
            AvroKind.Null => 0,
            AvroKind.Record when depth < 64 => schema.Fields.Count == 0
                ? 0
                : schema.Fields.Sum(f => MinimumEncodedSize(f.Type, depth + 1)),
            _ => 1
        };

        // ── which union branch fits ──

        /// <summary>The index of the first branch <paramref name="value"/> fits, or -1. This is the spec's
        /// "first matching branch" rule, so its ORDER is the behaviour: a null is written to a null branch even
        /// when a string branch would also accept a "" this engine could coerce.</summary>
        internal static int FirstFittingBranch(AvroSchema union, object? value, bool caseSensitive)
        {
            for (var i = 0; i < union.Branches.Count; i++)
                if (Fits(union.Branches[i], value, caseSensitive)) return i;
            return -1;
        }

        private static bool Fits(AvroSchema schema, object? value, bool caseSensitive)
        {
            // A LOGICAL TYPE DECIDES FIRST, in its own terms: a date branch must not claim a Guid merely because
            // its base type is an int, and a union whose branches differ ONLY by logical type must still be
            // resolvable by the value the caller passed.
            if (schema.LogicalType is not null) return AvroLogicalTypes.Fits(schema, value, caseSensitive);

            switch (schema.Kind)
            {
                case AvroKind.Null: return value is null;
                case AvroKind.Boolean: return value is bool;
                case AvroKind.Int:
                    return value is int || value is long l && l is >= int.MinValue and <= int.MaxValue;
                case AvroKind.Long: return value is int or long;
                case AvroKind.Float:
                case AvroKind.Double:
                    return value is float or double or int or long;
                case AvroKind.Bytes: return value is byte[];
                case AvroKind.Fixed: return value is byte[] b && b.Length == schema.FixedSize;
                case AvroKind.String: return value is string;
                case AvroKind.Enum: return value is string s && schema.Symbols.Contains(s);
                case AvroKind.Array: return ItemsOf(value) is not null;
                case AvroKind.Map: return PairsOf(value) is not null;
                case AvroKind.Record:
                    // A record branch fits when the value HAS the members, not merely when it is a dictionary: a
                    // union of two records is decided by their fields, which is what makes the order meaningful.
                    return value is not null && TryMembers(value, out var members)
                        && schema.Fields.All(f => members.TryGet(f.Name, caseSensitive, out _));
                case AvroKind.Union: return FirstFittingBranch(schema, value, caseSensitive) >= 0;
                default: return false;
            }
        }

        // ── the shapes a script value can have ──

        /// <summary>A record member lookup, which is where the interpreter's CASE RULE lives: a class's
        /// <c>FieldValues</c> and a dictionary's keys are compared the way every other name comparison in this
        /// engine is, so a script in its default mode must spell a field the way the schema does, and a `.lax`
        /// session need not.</summary>
        private readonly struct MemberSource(Func<string, bool, (bool Found, object? Value)> lookup,
            IReadOnlyList<string> names)
        {
            internal IReadOnlyList<string> Names => names;

            internal bool TryGet(string name, bool caseSensitive, out object? value)
            {
                var (found, hit) = lookup(name, caseSensitive);
                value = hit;
                return found;
            }
        }

        /// <summary>The member names a record-shaped value carries, for the JSON encoding's unknown-member check.
        /// Null when the value is not a dictionary, class or struct at all.</summary>
        internal static IReadOnlyList<string>? MemberNamesOf(object? value)
            => value is not null && TryMembers(value, out var members) ? members.Names : null;

        /// <summary>Reads ONE member of a dictionary, class instance or struct instance, for the logical-type layer,
        /// which needs a value's fields without encoding them.</summary>
        internal static bool TryReadMember(object? value, string name, bool caseSensitive, out object? member)
        {
            if (value is not null && TryMembers(value, out var members))
                return members.TryGet(name, caseSensitive, out member);
            member = null;
            return false;
        }

        private static bool TryMembers(object value, out MemberSource members)
        {
            switch (value)
            {
                case ClassInstance ci:
                {
                    var names = MemberNames(ci);
                    members = new MemberSource((name, caseSensitive) =>
                    {
                        if (TryKey(ci.FieldValues, name, caseSensitive, out var hit)) return (true, hit);
                        // The Avro field may be spelled as the PROPERTY a script sees, while FieldValues holds the
                        // backing field: asking only for the backing name would miss the name the caller wrote.
                        foreach (var property in ci.Class.Properties ?? [])
                        {
                            if (!property.Name.Equals(name, caseSensitive ? StringComparison.Ordinal
                                    : StringComparison.OrdinalIgnoreCase)) continue;
                            if (TryKey(ci.FieldValues, BackingFieldName(property.Name), caseSensitive, out var viaProperty))
                                return (true, viaProperty);
                        }
                        return (false, null);
                    }, names);
                    return true;
                }
                case StructInstance si:
                {
                    members = new MemberSource(
                        (name, caseSensitive) => TryKey(si.Fields, name, caseSensitive, out var hit)
                            ? (true, hit) : (false, null),
                        si.Declaration?.Fields.Select(f => f.Name).ToArray() ?? [.. si.Fields.Keys]);
                    return true;
                }
                case IScriptDictionary dict:
                {
                    var keys = dict.Keys();
                    var names = new List<string>(keys.Length);
                    foreach (var key in keys) names.Add(key?.ToString() ?? "");
                    members = new MemberSource((name, caseSensitive) =>
                    {
                        foreach (var key in keys)
                        {
                            var text = key?.ToString() ?? "";
                            if (!text.Equals(name, caseSensitive ? StringComparison.Ordinal
                                    : StringComparison.OrdinalIgnoreCase)) continue;
                            return (true, dict[key!]);
                        }
                        return (false, null);
                    }, names);
                    return true;
                }
                case IDictionary<string, object?> typed:
                    members = DictionaryMembers(typed);
                    return true;
                case System.Collections.IDictionary raw:
                {
                    var pairs = new List<(object Key, object? Value)>(raw.Count);
                    foreach (System.Collections.DictionaryEntry entry in raw) pairs.Add((entry.Key, entry.Value));
                    members = new MemberSource((name, caseSensitive) =>
                    {
                        foreach (var (key, item) in pairs)
                            if ((key?.ToString() ?? "").Equals(name, caseSensitive ? StringComparison.Ordinal
                                    : StringComparison.OrdinalIgnoreCase))
                                return (true, item);
                        return (false, null);
                    }, [.. pairs.Select(p => p.Key?.ToString() ?? "")]);
                    return true;
                }
                default:
                    members = default;
                    return false;
            }
        }

        private static MemberSource DictionaryMembers(IDictionary<string, object?> source)
        {
            var keys = new List<string>(source.Keys);
            return new MemberSource((name, caseSensitive) =>
            {
                foreach (var key in keys)
                    if (key.Equals(name, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                        return (true, source[key]);
                return (false, null);
            }, keys);
        }

        /// <summary>A dictionary lookup honouring the case rule. The exact spelling is tried first, so a dictionary
        /// that holds BOTH `Id` and `id` answers the one the schema actually names.</summary>
        private static bool TryKey(Dictionary<string, object?> source, string name, bool caseSensitive,
            out object? value)
        {
            if (source.TryGetValue(name, out value)) return true;
            if (caseSensitive) return false;
            foreach (var pair in source)
            {
                if (!pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                value = pair.Value;
                return true;
            }
            return false;
        }

        private static IReadOnlyList<string> MemberNames(ClassInstance ci)
        {
            var names = new List<string>(ci.Class.Fields.Count);
            foreach (var field in ci.Class.Fields)
            {
                if (field.IsStatic) continue;
                names.Add(ResolvePropertyName(ci.Class.Properties, field.Name));
            }
            return names;
        }

        /// <summary>The items of a list or an array, or null when the value is not a sequence. A STRING is not a
        /// sequence here even though it enumerates chars: an Avro array is a collection, and treating text as one
        /// would silently encode "abc" as three numbers.</summary>
        internal static List<object?>? ItemsOf(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                // Text and BYTES are not sequences here: both enumerate, but an Avro array is a collection, and
                // treating either as one would encode "abc" as three numbers or a payload as N ints — a silent
                // conversion of a value that has its own Avro kind.
                case string:
                case byte[]:
                    return null;
                case IScriptList list:
                    return [.. list.ToArray().Cast<object?>()];
                case Array array:
                {
                    var items = new List<object?>(array.Length);
                    foreach (var item in array) items.Add(item);
                    return items;
                }
                case System.Collections.IEnumerable sequence:
                {
                    var items = new List<object?>();
                    foreach (var item in sequence) items.Add(item);
                    return items;
                }
                default:
                    return null;
            }
        }

        /// <summary>The key/value pairs of a dictionary, or null when the value is not one.</summary>
        internal static List<(object Key, object? Value)>? PairsOf(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case IScriptDictionary dict:
                {
                    var pairs = new List<(object, object?)>(dict.Count);
                    foreach (var key in dict.Keys()) pairs.Add((key!, dict[key!]));
                    return pairs;
                }
                case IDictionary<string, object?> typed:
                    return [.. typed.Select(pair => ((object)pair.Key, pair.Value))];
                case System.Collections.IDictionary raw:
                {
                    var pairs = new List<(object, object?)>(raw.Count);
                    foreach (System.Collections.DictionaryEntry entry in raw) pairs.Add((entry.Key, entry.Value));
                    return pairs;
                }
                default:
                    return null;
            }
        }

        // ── numbers, with the refusal a schema's own range needs ──

        internal static int ToInt(object? value, AvroSchema schema, string member) => value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            long l => throw new ScriptException(
                $"{member}: {l} does not fit an Avro int, which is 32 bits ({int.MinValue}..{int.MaxValue}). " +
                "Declare the field \"long\" to hold it."),
            _ => throw NotAValue(member, schema, value, "a whole number")
        };

        internal static long ToLong(object? value, AvroSchema schema, string member) => value switch
        {
            int i => i,
            long l => l,
            _ => throw NotAValue(member, schema, value, "a whole number")
        };

        private static float ToFloat(object? value, AvroSchema schema, string member) => value switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            _ => throw NotAValue(member, schema, value, "a number")
        };

        internal static double ToDouble(object? value, AvroSchema schema, string member) => value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => throw NotAValue(member, schema, value, "a number")
        };

        // ── the typed read: a record back as a script class or struct ──

        /// <summary>Imposes a type NAME on a decoded value through the engine's existing typed reconstruction, so
        /// a record comes back as the class or struct the name declares instead of a dictionary. One path with
        /// <c>Json.Deserialize</c>, which is what keeps the two readers agreeing about a class's fields.</summary>
        internal static object? ImposeType(object? decoded, string typeName, bool caseSensitive)
            => DeserializeTyped(decoded, typeName, caseSensitive);

        /// <summary>The NAME a type argument denotes, spelled the way <c>Json.Deserialize</c> spells it: the type
        /// itself (a class/struct definition or an instance), a TYPE REFERENCE, or a string. One switch serves both
        /// members, so `Avro.Deserialize` and `Json.Deserialize` cannot disagree about what a type argument means;
        /// a reference is spelled WHOLE through <see cref="TypeInfo.Spelling"/>, because `List&lt;string?&gt;` keeps
        /// its name in GenericArgs while TypeName holds only `List`.</summary>
        internal static string TypeNameOf(object? argument, string member) => argument switch
        {
            ClassInfo ci => ci.Name,
            StructDeclStmt sd => sd.Name,
            StructInstance si => si.StructName,
            ClassInstance ci => ci.Class.Name,
            TypeInfo ti => TypeInfo.Spelling(ti),
            string s => s,
            _ => throw new ScriptException(
                $"{member}: the type argument must be a type name, a class/struct definition, an instance or a " +
                $"type reference, not '{AvroOptions.FriendlyNameOf(argument)}'.")
        };

        // ── refusals ──

        private static ScriptException NotAValue(string member, AvroSchema schema, object? value, string expected)
            => new($"{member}: {schema.DisplayName} takes {expected}, not {DescribeValue(value)}.");

        internal static string DescribeValue(object? value) => value switch
        {
            null => "null",
            string => "a string",
            bool => "a bool",
            byte[] bytes => $"{bytes.Length} byte(s)",
            IScriptDictionary => "a dictionary",
            ClassInstance ci => $"a {ci.Class.Name} instance",
            StructInstance si => $"a {si.StructName} instance",
            IScriptList => "a list",
            Array array => $"a {array.Length}-element array",
            _ => AvroOptions.FriendlyNameOf(value)
        };

        private static string DescribeSymbols(AvroSchema schema)
            => schema.Symbols.Count == 0 ? "empty" : string.Join(", ", schema.Symbols.Select(s => $"\"{s}\""));

        private static string DescribeFields(AvroSchema schema)
            => schema.Fields.Count == 0 ? "none" : string.Join(", ", schema.Fields.Select(f => f.Name));

        private static string DescribeMembers(MemberSource members)
            => members.Names.Count == 0 ? "none" : string.Join(", ", members.Names);

        private static string DescribeBranches(AvroSchema schema)
            => string.Join(", ", schema.Branches.Select(b => b.DisplayName));
    }
}
