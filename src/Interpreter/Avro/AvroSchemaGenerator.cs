namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>GENERATES an Avro schema from a script type — the other direction from <see cref="AvroSchemaParser"/>,
    /// and the reason `Json.Avro` is worth having at all: a script declares a class or a struct once and gets the
    /// .avsc text that describes it, instead of writing the same shape twice and letting the two drift.
    ///
    /// The mapping is the engine's own registries, never System.Reflection: a class or a struct becomes a record
    /// whose fields are its own, an enum becomes an enum whose symbols are its members, and a `T?` becomes the
    /// union ["null", T] with a null default — the spec's own nullable spelling, and the reason the null branch
    /// is written FIRST, which every reader accepts.
    ///
    /// A recursive type is generated as the spec's recursive form rather than walked forever: a named type is
    /// registered by full name BEFORE its fields are built, so a second reference answers the SAME model instance
    /// (the same rule AvroSchemaParser applies), and the writer emits a repeat use as its full name.</summary>
    internal static class AvroSchemaGenerator
    {
        /// <summary>What a script may hand to `Avro.Schema`: a type (class, struct, enum, or a type
        /// reference) or an INSTANCE of one. Text is refused on purpose — a schema string belongs to `Parse`,
        /// which round-trips it, and accepting it here would make one member mean two opposite things.</summary>
        internal static AvroSchema From(object? target, AvroOptions options, string member)
        {
            var byFullName = new Dictionary<string, AvroSchema>(StringComparer.Ordinal);
            var names = new Names(options.RecordName, member);

            var schema = target switch
            {
                ClassInfo ci => ForClass(ci, options, byFullName, names, member),
                ClassInstance instance => ForClass(instance.Class, options, byFullName, names, member),
                StructDeclStmt sd => RecordOf(sd.Name, FieldsOf(sd), options, byFullName, names, member),
                StructInstance si => NamedType(si.StructName, options, byFullName, names, member),
                EnumDeclStmt ed => EnumOf(ed.Name, ed.Members.Select(m => m.Name).ToArray(), options, byFullName,
                    names, member),
                TypeInfo ti => TypeOf(ti, ti.IsNullable, options, byFullName, names, member),
                string text => throw new ScriptException(
                    $"{member}: the argument is a type or an instance, not schema text. Use Avro.Parse for " +
                    $"a schema written as text, or Avro.Schema({Trim(text)}), naming the TYPE itself rather " +
                    "than its name."),
                null => throw new ScriptException(
                    $"{member}: the argument is required (a class, a struct, an enum, a type reference or an " +
                    "instance of one)."),
                _ => throw new ScriptException(
                    $"{member}: the argument is a class, a struct, an enum, a type reference or an instance of one, " +
                    $"not '{AvroOptions.FriendlyNameOf(target)}'.")
            };

            // The ROOT's namespace comes from the options; a nested named type inherits it through the walk, which
            // is what makes `{"namespace": "user"}` name the whole document rather than only its first record.
            return schema;
        }

        /// <summary>The record names the options supply, in depth-first order, so a script can name a generated
        /// record something the C#-style type name would not.</summary>
        private sealed class Names(IReadOnlyList<string> given, string member)
        {
            private int _index;

            internal string Next(string fallback)
            {
                if (_index < given.Count) return given[_index++];
                _index++;
                return fallback;
            }

            internal string Member => member;
        }

        private static AvroSchema NamedType(string typeName, AvroOptions options,
            Dictionary<string, AvroSchema> byFullName, Names names, string member)
        {
            var declaration = Resolve(typeName);
            return declaration switch
            {
                ClassInfo ci => ForClass(ci, options, byFullName, names, member),
                StructDeclStmt sd => RecordOf(sd.Name, FieldsOf(sd), options, byFullName, names, member),
                EnumDeclStmt ed => EnumOf(ed.Name, ed.Members.Select(m => m.Name).ToArray(), options, byFullName,
                    names, member),
                _ => throw new ScriptException(
                    $"{member}: '{typeName}' is not a class, struct or enum this session declares, so no schema " +
                    "can be generated for it. Declare the type, or write the schema as text and use " +
                    "Avro.Parse.")
            };
        }

        /// <summary>Resolves a type NAME the way the engine's own dispatch does. A class or enum lives in the
        /// scope, but a STRUCT resolves through the interpreter's own registry — the same `Structs` table its
        /// static-method dispatch reads — because a struct's name in scope is a TYPE REFERENCE rather than the
        /// declaration, so asking only the scope reported "the session does not declare" a struct it plainly
        /// declares.</summary>
        private static object? Resolve(string typeName)
        {
            if (Current is not { } interpreter) return null;
            if (interpreter.CurrentFrame.Scopes.TryGet(typeName, out var declaration)
                && declaration is ClassInfo or StructDeclStmt or EnumDeclStmt)
                return declaration;
            return interpreter.Structs.TryGetValue(typeName, out var structDecl) ? structDecl : null;
        }

        /// <summary>A script `enum` reaches this engine as a CLASS whose only members are static (the shape
        /// `Json.Deserialize` also special-cases), so the question "record or enum" is asked HERE rather than by
        /// the caller: a class with no instance fields and static members IS an enum, and generating a record for
        /// it produced `{"type":"record","name":"Color","fields":[]}`, a schema that describes nothing.
        /// </summary>
        private static AvroSchema ForClass(ClassInfo ci, AvroOptions options,
            Dictionary<string, AvroSchema> byFullName, Names names, string member)
        {
            var symbols = EnumSymbolsOf(ci);
            return symbols is null
                ? RecordOf(ci.Name, FieldsOf(ci), options, byFullName, names, member)
                : EnumOf(ci.Name, symbols, options, byFullName, names, member);
        }

        /// <summary>The symbols of an enum-shaped class, in DECLARATION order (StaticFieldDecls carries the
        /// order; the dictionary's keys are the fallback for a class built without declarations), or null when the
        /// class is a real record.</summary>
        private static string[]? EnumSymbolsOf(ClassInfo ci)
        {
            if (ci.Fields.Any(f => !f.IsStatic)) return null;
            if (ci.StaticFieldDecls is { Count: > 0 } decls) return [.. decls.Select(d => d.Name)];
            if (ci.StaticFields is { Count: > 0 } fields) return [.. fields.Keys];
            return null;
        }

        private static List<(string Name, TypeInfo Type)> FieldsOf(ClassInfo ci)
        {
            var fields = new List<(string, TypeInfo)>(ci.Fields.Count);
            foreach (var field in ci.Fields)
            {
                if (field.IsStatic) continue;
                // The name a script SEES, which is the property's when the field backs one.
                fields.Add((ResolvePropertyName(ci.Properties, field.Name), field.Type));
            }
            return fields;
        }

        private static List<(string Name, TypeInfo Type)> FieldsOf(StructDeclStmt sd)
        {
            var fields = new List<(string, TypeInfo)>(sd.Fields.Count);
            foreach (var field in sd.Fields)
            {
                if (field.IsStatic) continue;
                fields.Add((ResolvePropertyName(sd.Properties, field.Name), field.Type));
            }
            return fields;
        }

        private static AvroSchema RecordOf(string scriptName, List<(string Name, TypeInfo Type)> fields,
            AvroOptions options, Dictionary<string, AvroSchema> byFullName, Names names, string member)
        {
            var name = names.Next(scriptName);
            var schema = new AvroSchema
            {
                Kind = AvroKind.Record,
                Name = name,
                // A nested record INHERITS the namespace from the options rather than from the enclosing record,
                // because that is the single namespace a script asked for.
                Namespace = options.Namespace,
                FullName = options.Namespace is { } ns ? $"{ns}.{name}" : name
            };

            // Registered BEFORE the fields, so a field that uses the record itself answers this SAME instance and
            // the walk terminates: the spec's recursive form, not an unbounded expansion.
            if (schema.FullName is { } fullName && !byFullName.TryAdd(fullName, schema)) return byFullName[fullName];

            foreach (var (fieldName, fieldType) in fields)
            {
                var type = TypeOf(fieldType, fieldType.IsNullable, options, byFullName, names, member);
                // A NULLABLE field carries the null default the union exists for, which is what makes an absent
                // key readable: ["null", T] with no default would still refuse a document that omits it.
                var nullable = fieldType.IsNullable && type.Kind == AvroKind.Union;
                schema.Fields.Add(new AvroField
                {
                    Name = fieldName,
                    Type = type,
                    HasDefault = nullable,
                    Default = nullable ? null : null
                });
            }
            return schema;
        }

        private static AvroSchema EnumOf(string scriptName, string[] symbols, AvroOptions options,
            Dictionary<string, AvroSchema> byFullName, Names names, string member)
        {
            var name = names.Next(scriptName);
            var schema = new AvroSchema
            {
                Kind = AvroKind.Enum,
                Name = name,
                Namespace = options.Namespace,
                FullName = options.Namespace is { } ns ? $"{ns}.{name}" : name
            };
            if (schema.FullName is { } fullName && !byFullName.TryAdd(fullName, schema)) return byFullName[fullName];
            schema.Symbols.AddRange(symbols);
            return schema;
        }

        /// <summary>One script type as an Avro schema, with `T?` becoming ["null", T].</summary>
        private static AvroSchema TypeOf(TypeInfo type, bool nullable, AvroOptions options,
            Dictionary<string, AvroSchema> byFullName, Names names, string member)
        {
            var bare = TypeOfCore(type, options, byFullName, names, member);
            if (!nullable) return bare;
            var union = new AvroSchema { Kind = AvroKind.Union };
            union.Branches.Add(new AvroSchema { Kind = AvroKind.Null });
            union.Branches.Add(bare);
            return union;
        }

        private static AvroSchema TypeOfCore(TypeInfo type, AvroOptions options,
            Dictionary<string, AvroSchema> byFullName, Names names, string member)
        {
            // An ARRAY is the array kind; `byte[]` is the exception, because Avro has a bytes kind and reading a
            // buffer as an array of ints would be a different value. The ONE-dimensional test asks the SUM of the
            // two depths, and that is not tidiness: the parser records `T[]` as JaggedDepth 1 (ArrayRank is the
            // `T[,]` spelling, one per comma), so an `ArrayRank == 1 && JaggedDepth == 0` test never fired for a
            // declared field and a `byte[]` came out as `array<int>` while this comment promised bytes.
            if (type.ArrayRank > 0 || type.JaggedDepth > 0)
            {
                if (type.ArrayRank + type.JaggedDepth == 1 && type.BaseType == TokenType.Byte
                    && type.TypeName is null && type.GenericArgs is not { Count: > 0 })
                    return new AvroSchema { Kind = AvroKind.Bytes };
                var element = type with { ArrayRank = Math.Max(0, type.ArrayRank - 1),
                    JaggedDepth = type.JaggedDepth > 0 ? type.JaggedDepth - 1 : 0, IsNullable = false };
                return new AvroSchema { Kind = AvroKind.Array, Items = TypeOf(element, element.IsNullable, options,
                    byFullName, names, member) };
            }

            // A generic container is an array or a map; Avro has no other collections.
            if (type.GenericArgs is { Count: > 0 } args && type.TypeName is { } container)
            {
                var name = container.Split('<')[0];
                if (name.Equals("List", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("HashSet", StringComparison.OrdinalIgnoreCase))
                    return new AvroSchema { Kind = AvroKind.Array, Items = TypeOf(args[0], args[0].IsNullable,
                        options, byFullName, names, member) };
                if (name.Equals("Dictionary", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Count == 2 && !IsStringType(args[0]))
                        throw new ScriptException(
                            $"{member}: a map's key is a string in Avro, and this dictionary's key is " +
                            $"'{TypeInfo.DeclaredSpelling(args[0])}'. Declare Dictionary<string, T> for a map " +
                            "schema.");
                    return new AvroSchema
                    {
                        Kind = AvroKind.Map,
                        Values = TypeOf(args.Count == 2 ? args[1] : args[0], false, options, byFullName, names,
                            member)
                    };
                }
                throw new ScriptException(
                    $"{member}: '{TypeInfo.DeclaredSpelling(type)}' has no Avro schema. Avro models lists, maps " +
                    "and records, so a script type that is none of those cannot be described.");
            }

            // A NAMED type is either a type this engine already knows (Guid, DateTime, …) or one the session
            // declares. The known ones are asked FIRST, because a name lookup would not find them and the refusal
            // would claim the session does not declare a type that is a language builtin.
            if (type.TypeName is { } named && WellKnown(named) is { } known) return known;

            // A named type is a record or an enum this session declares.
            if (type.TypeName is { } named2) return NamedType(named2, options, byFullName, names, member);

            return type.BaseType switch
            {
                TokenType.Bool => new AvroSchema { Kind = AvroKind.Boolean },
                TokenType.Int or TokenType.Short or TokenType.Byte or TokenType.SByte or TokenType.UShort =>
                    new AvroSchema { Kind = AvroKind.Int },
                TokenType.Long => new AvroSchema { Kind = AvroKind.Long },
                // An unsigned 32/64-bit value does not fit Avro's signed int, so it is widened to long rather than
                // written into an int it could overflow.
                TokenType.UInt or TokenType.ULong => new AvroSchema { Kind = AvroKind.Long },
                TokenType.Float => new AvroSchema { Kind = AvroKind.Float },
                TokenType.Double => new AvroSchema { Kind = AvroKind.Double },
                TokenType.Decimal => new AvroSchema
                {
                    Kind = AvroKind.Bytes,
                    LogicalType = "decimal",
                    Precision = options.DecimalPrecision,
                    Scale = options.DecimalScale
                },
                TokenType.Char or TokenType.StringType or TokenType.String => new AvroSchema
                {
                    Kind = AvroKind.String
                },
                TokenType.DateTime or TokenType.DateTimeOffset => new AvroSchema
                {
                    Kind = AvroKind.Long,
                    LogicalType = "timestamp-millis"
                },
                TokenType.TimeSpan => new AvroSchema
                {
                    Kind = AvroKind.Long,
                    LogicalType = "time-micros"
                },
                TokenType.Object => throw new ScriptException(
                    $"{member}: a field of type 'object' has no Avro schema. Avro needs a type to write, so a " +
                    "schema generated from a script type cannot carry an untyped member: declare the type, or " +
                    "write the schema as text and use Avro.Parse."),
                _ => throw new ScriptException(
                    $"{member}: '{TypeInfo.DeclaredSpelling(type)}' has no Avro schema. Avro's primitive types " +
                    "are boolean, int, long, float, double, bytes and string, and the rest of a schema is records, " +
                    "enums, arrays, maps, unions and fixed.")
            };
        }

        /// <summary>The CLR types a script spells by NAME but this engine already knows how to encode. Avro has a
        /// natural mapping for each, and refusing them would mean no schema could be generated for a class that
        /// holds a Guid or a DateTime.</summary>
        private static AvroSchema? WellKnown(string name) => name switch
        {
            "Guid" => new AvroSchema { Kind = AvroKind.String, LogicalType = "uuid" },
            // A URI is text on the wire; Avro has no URI type, and inventing a logical type no reader knows would
            // make the schema unreadable elsewhere.
            "Uri" => new AvroSchema { Kind = AvroKind.String },
            "DateTime" => new AvroSchema { Kind = AvroKind.Long, LogicalType = "timestamp-millis" },
            "DateTimeOffset" => new AvroSchema { Kind = AvroKind.Long, LogicalType = "timestamp-millis" },
            "TimeSpan" => new AvroSchema { Kind = AvroKind.Long, LogicalType = "time-micros" },
            _ => null
        };

        private static bool IsStringType(TypeInfo type)
            => type.TypeName is null && type.ArrayRank == 0 && type.JaggedDepth == 0
               && type.BaseType is TokenType.String or TokenType.StringType;

        private static string Trim(string text)
            => text.Length <= 32 ? text : text[..32] + "…";

    }
}
