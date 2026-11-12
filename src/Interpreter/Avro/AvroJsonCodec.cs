using System.Text;
using System.Text.Json;

namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The AVRO JSON ENCODING: the spec's second representation, which is the field-default form except
    /// for unions. It exists beside the binary one because a JSON payload can be read by eye and diffed, and the
    /// two must agree about the schema, so the value mapping is the SAME <see cref="AvroValueMap"/> walk — only
    /// how a value is spelled differs.
    ///
    /// The spec's rules this follows: bytes and fixed are JSON STRINGS whose characters are the bytes as code
    /// points U+0000..U+00FF (not base64, which is a different protocol's choice), an enum is its symbol, and a
    /// union is JSON null for the null branch and otherwise an OBJECT of one member, whose name is the branch's
    /// type name (the user-given full name for a record, enum or fixed, and the type word otherwise).</summary>
    internal static class AvroJsonCodec
    {
        /// <summary>How deep a value may nest, for the same reason the binary walk has a limit: a recursive record
        /// is legal, so depth is the only bound on a value that holds itself.</summary>
        private const int MaxDepth = 512;

        // ── script value → Avro JSON text ──

        internal static string ToText(AvroSchema schema, object? value, string member, bool caseSensitive)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
                Write(writer, schema, value, member, caseSensitive, 0);
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void Write(Utf8JsonWriter writer, AvroSchema schema, object? value, string member,
            bool caseSensitive, int depth)
        {
            if (depth > MaxDepth)
                throw new ScriptException(
                    $"{member}: the value nests more than {MaxDepth} levels deep. A recursive record is legal, " +
                    "so depth is the only bound on a value that holds itself.");

            // The JSON encoding is defined over the BASE types: a logical type's own conversion is the binary
            // codec's, and the spec gives the JSON form as the base type's, so this refuses by name rather than
            // guessing which of the two the caller meant.
            if (schema.LogicalType is { } logical)
                throw new ScriptException(
                    $"{member}: the logical type '{logical}' has no JSON form in this engine. Its base type is " +
                    $"'{PrimitiveWord(schema.Kind)}', so encode that value directly, or use Avro.Serialize, " +
                    "which maps logical types onto script types.");

            switch (schema.Kind)
            {
                case AvroKind.Null:
                    if (value is not null) throw NotAValue(member, schema, value, "null");
                    writer.WriteNullValue();
                    return;

                case AvroKind.Boolean:
                    if (value is not bool flag) throw NotAValue(member, schema, value, "a bool");
                    writer.WriteBooleanValue(flag);
                    return;

                case AvroKind.Int:
                    writer.WriteNumberValue(AvroValueMap.ToInt(value, schema, member));
                    return;

                case AvroKind.Long:
                    writer.WriteNumberValue(AvroValueMap.ToLong(value, schema, member));
                    return;

                case AvroKind.Float:
                case AvroKind.Double:
                {
                    var number = AvroValueMap.ToDouble(value, schema, member);
                    if (double.IsNaN(number) || double.IsInfinity(number))
                        throw new ScriptException(
                            $"{member}: {number} has no JSON number. JSON spells no NaN and no infinity, so this " +
                            "value needs the binary encoding (Avro.Serialize), which is IEEE bits and holds " +
                            "both.");
                    writer.WriteNumberValue(number);
                    return;
                }

                case AvroKind.String:
                    if (value is not string text) throw NotAValue(member, schema, value, "a string");
                    writer.WriteStringValue(text);
                    return;

                case AvroKind.Bytes:
                case AvroKind.Fixed:
                {
                    if (value is not byte[] bytes) throw NotAValue(member, schema, value, "a byte array");
                    if (schema.Kind == AvroKind.Fixed && bytes.Length != schema.FixedSize)
                        throw new ScriptException(
                            $"{member}: fixed '{schema.DisplayName}' is exactly {schema.FixedSize} byte(s), but " +
                            $"the value is {bytes.Length}.");
                    writer.WriteStringValue(BytesAsText(bytes));
                    return;
                }

                case AvroKind.Enum:
                {
                    if (value is not string symbol) throw NotAValue(member, schema, value, "a symbol name");
                    if (!schema.Symbols.Contains(symbol))
                        throw new ScriptException(
                            $"{member}: '{symbol}' is not a symbol of enum '{schema.DisplayName}'. Its symbols " +
                            $"are {string.Join(", ", schema.Symbols.Select(s => $"\"{s}\""))}.");
                    writer.WriteStringValue(symbol);
                    return;
                }

                case AvroKind.Array:
                {
                    var items = AvroValueMap.ItemsOf(value) ?? throw NotAValue(member, schema, value, "a list");
                    writer.WriteStartArray();
                    foreach (var item in items) Write(writer, schema.Items!, item, member, caseSensitive, depth + 1);
                    writer.WriteEndArray();
                    return;
                }

                case AvroKind.Map:
                {
                    var pairs = AvroValueMap.PairsOf(value) ?? throw NotAValue(member, schema, value, "a dictionary");
                    writer.WriteStartObject();
                    foreach (var (key, item) in pairs)
                    {
                        writer.WritePropertyName(key as string ?? key?.ToString()
                            ?? throw new ScriptException(
                                $"{member}: a map key is a string, not null. Map '{schema.DisplayName}' has no " +
                                "key schema."));
                        Write(writer, schema.Values!, item, member, caseSensitive, depth + 1);
                    }
                    writer.WriteEndObject();
                    return;
                }

                case AvroKind.Record:
                {
                    var names = AvroValueMap.MemberNamesOf(value)
                        ?? throw NotAValue(member, schema, value, "a dictionary, a class instance or a struct instance");
                    writer.WriteStartObject();
                    foreach (var field in schema.Fields)
                    {
                        if (!AvroValueMap.TryReadMember(value, field.Name, caseSensitive, out var fieldValue))
                            throw new ScriptException(
                                $"{member}: record '{schema.DisplayName}' needs the field '{field.Name}', and the " +
                                $"value given has no such member (it has {(names.Count == 0 ? "none" : string.Join(", ", names))}). " +
                                "A writer supplies every field in schema order.");
                        writer.WritePropertyName(field.Name);
                        Write(writer, field.Type, fieldValue, member, caseSensitive, depth + 1);
                    }
                    writer.WriteEndObject();
                    return;
                }

                case AvroKind.Union:
                {
                    var index = AvroValueMap.FirstFittingBranch(schema, value, caseSensitive);
                    if (index < 0)
                        throw new ScriptException(
                            $"{member}: {AvroValueMap.DescribeValue(value)} fits no branch of the union (" +
                            $"{string.Join(", ", schema.Branches.Select(b => b.DisplayName))}). A union writes the " +
                            "first branch that fits.");
                    var branch = schema.Branches[index];
                    // THE NULL BRANCH IS BARE null; every other branch is wrapped in an object named for its type,
                    // which is the one place the JSON encoding differs from the field-default form.
                    if (branch.Kind == AvroKind.Null)
                    {
                        writer.WriteNullValue();
                        return;
                    }
                    writer.WriteStartObject();
                    writer.WritePropertyName(BranchName(branch));
                    Write(writer, branch, value, member, caseSensitive, depth + 1);
                    writer.WriteEndObject();
                    return;
                }

                default:
                    throw new ScriptException($"{member}: Avro kind '{schema.Kind}' has no JSON form.");
            }
        }

        // ── Avro JSON text → script value ──

        internal static object? FromValue(object? json, AvroSchema schema, string member, bool caseSensitive)
        {
            var parsed = ParseJsonInput(json);
            return Read(parsed, schema, member, caseSensitive, 0);
        }

        private static object? Read(object? json, AvroSchema schema, string member, bool caseSensitive, int depth)
        {
            if (depth > MaxDepth)
                throw new ScriptException(
                    $"{member}: the payload nests more than {MaxDepth} levels deep, so depth is what bounds the walk.");

            if (schema.LogicalType is { } logical)
                throw new ScriptException(
                    $"{member}: the logical type '{logical}' has no JSON form in this engine. Its base type is " +
                    $"'{PrimitiveWord(schema.Kind)}', so decode the base value, or use Avro.Deserialize, which " +
                    "maps logical types onto script types.");

            switch (schema.Kind)
            {
                case AvroKind.Null:
                    if (json is not null) throw NotJson(member, schema, "null");
                    return null;

                case AvroKind.Boolean:
                    if (json is not bool flag) throw NotJson(member, schema, "true or false");
                    return flag;

                case AvroKind.Int:
                    return (long)AvroValueMap.ToInt(json, schema, member);

                case AvroKind.Long:
                    // A JSON number parses to long where it fits, so this is the parser's own integer.
                    return json is long whole
                        ? whole
                        : throw NotJson(member, schema, "a whole number");

                case AvroKind.Float:
                case AvroKind.Double:
                    return AvroValueMap.ToDouble(json, schema, member);

                case AvroKind.String:
                    return json is string text ? text : throw NotJson(member, schema, "a string");

                case AvroKind.Bytes:
                case AvroKind.Fixed:
                {
                    if (json is not string encoded) throw NotJson(member, schema, "a string of the bytes as characters");
                    var bytes = TextAsBytes(encoded, member, schema);
                    if (schema.Kind == AvroKind.Fixed && bytes.Length != schema.FixedSize)
                        throw new ScriptException(
                            $"{member}: fixed '{schema.DisplayName}' carries exactly {schema.FixedSize} byte(s), " +
                            $"and the payload's string holds {bytes.Length}.");
                    return bytes;
                }

                case AvroKind.Enum:
                {
                    if (json is not string symbol) throw NotJson(member, schema, "a symbol name");
                    if (!schema.Symbols.Contains(symbol))
                        throw new ScriptException(
                            $"{member}: '{symbol}' is not a symbol of enum '{schema.DisplayName}'. Its symbols are " +
                            $"{string.Join(", ", schema.Symbols.Select(s => $"\"{s}\""))}.");
                    return symbol;
                }

                case AvroKind.Array:
                {
                    var items = AvroValueMap.ItemsOf(json)
                        ?? throw NotJson(member, schema, "a JSON array");
                    var list = new ScriptBoxedList(new TypeInfo(TokenType.Object, true, 0, 0, null));
                    foreach (var item in items)
                        list.AddRaw(Read(item, schema.Items!, member, caseSensitive, depth + 1)!);
                    return list;
                }

                case AvroKind.Map:
                {
                    var pairs = AvroValueMap.PairsOf(json) ?? throw NotJson(member, schema, "a JSON object");
                    var map = CreateScriptDictionary(
                        new TypeInfo(TokenType.String, false, 0, 0, null),
                        new TypeInfo(TokenType.Object, true, 0, 0, null));
                    foreach (var (key, item) in pairs)
                    {
                        var name = key as string ?? key?.ToString() ?? "";
                        map.AddRaw(name, Read(item, schema.Values!, member, caseSensitive, depth + 1));
                    }
                    return map;
                }

                case AvroKind.Record:
                {
                    if (AvroValueMap.MemberNamesOf(json) is null) throw NotJson(member, schema, "a JSON object");
                    var record = CreateScriptDictionary(
                        new TypeInfo(TokenType.String, false, 0, 0, null),
                        new TypeInfo(TokenType.Object, true, 0, 0, null), schema.Fields.Count);
                    foreach (var field in schema.Fields)
                    {
                        if (!AvroValueMap.TryReadMember(json, field.Name, caseSensitive, out var fieldValue))
                            throw new ScriptException(
                                $"{member}: record '{schema.DisplayName}' needs the field '{field.Name}', and the " +
                                "payload has no such member. The schema is the shape of the document, so every " +
                                "field must be present.");
                        record.AddRaw(field.Name, Read(fieldValue, field.Type, member, caseSensitive, depth + 1));
                    }
                    return record;
                }

                case AvroKind.Union:
                {
                    // A bare null is the null branch; anything else is the one-member wrapper naming its branch.
                    if (json is null)
                    {
                        if (schema.Branches.Any(b => b.Kind == AvroKind.Null)) return null;
                        throw new ScriptException(
                            $"{member}: the payload is null, but this union has no null branch " +
                            $"({string.Join(", ", schema.Branches.Select(b => b.DisplayName))}).");
                    }
                    var names = AvroValueMap.MemberNamesOf(json);
                    if (names is not { Count: 1 } || names[0] is not { } branchName)
                        throw new ScriptException(
                            $"{member}: a union is JSON null or an object of exactly ONE member naming its branch, " +
                            $"and the payload has {(names is null ? "no member" : $"{names.Count} member(s)")}. The " +
                            $"branches are {string.Join(", ", schema.Branches.Select(BranchName))}.");
                    var index = schema.Branches.FindIndex(b =>
                        BranchName(b).Equals(branchName, caseSensitive ? StringComparison.Ordinal
                            : StringComparison.OrdinalIgnoreCase));
                    if (index < 0)
                        throw new ScriptException(
                            $"{member}: '{branchName}' names no branch of this union " +
                            $"({string.Join(", ", schema.Branches.Select(BranchName))}). The member's name IS the " +
                            "branch, so a misspelt one is refused rather than guessed at.");
                    AvroValueMap.TryReadMember(json, branchName, caseSensitive, out var branchValue);
                    return Read(branchValue, schema.Branches[index], member, caseSensitive, depth + 1);
                }

                default:
                    throw new ScriptException($"{member}: Avro kind '{schema.Kind}' has no JSON form.");
            }
        }

        // ── the spec's bytes/fixed string rule, stated once per direction ──

        /// <summary>A byte string as the JSON string the spec defines: each byte is ONE character, its value taken
        /// as the code point. Not base64, which would be a different protocol's spelling.</summary>
        private static string BytesAsText(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }

        private static byte[] TextAsBytes(string text, string member, AvroSchema schema)
        {
            var bytes = new byte[text.Length];
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] > 0xFF)
                    throw new ScriptException(
                        $"{member}: a bytes string carries one character per byte, so every character is " +
                        $"U+00FF or below, and this one is U+{(int)text[i]:X4} at index {i}. The payload is not " +
                        $"the JSON form of '{schema.DisplayName}'.");
                bytes[i] = (byte)text[i];
            }
            return bytes;
        }

        // ── names ──

        /// <summary>The name a union member is wrapped under: the FULL name for a named type (a record, an enum or
        /// a fixed), and the type word otherwise, which is what the spec means by "user-given name for
        /// record/fixed/enum, type name otherwise".</summary>
        private static string BranchName(AvroSchema branch)
            => branch.IsNamed ? branch.FullName ?? branch.Name ?? PrimitiveWord(branch.Kind)
                : PrimitiveWord(branch.Kind);

        private static string PrimitiveWord(AvroKind kind) => kind switch
        {
            AvroKind.Null => "null",
            AvroKind.Boolean => "boolean",
            AvroKind.Int => "int",
            AvroKind.Long => "long",
            AvroKind.Float => "float",
            AvroKind.Double => "double",
            AvroKind.Bytes => "bytes",
            AvroKind.String => "string",
            AvroKind.Array => "array",
            AvroKind.Map => "map",
            AvroKind.Union => "union",
            _ => kind.ToString().ToLowerInvariant()
        };

        private static ScriptException NotAValue(string member, AvroSchema schema, object? value, string expected)
            => new($"{member}: {schema.DisplayName} takes {expected}, not {AvroValueMap.DescribeValue(value)}.");

        private static ScriptException NotJson(string member, AvroSchema schema, string expected)
            => new($"{member}: '{schema.DisplayName}' is {expected} in the JSON encoding. The schema is the shape " +
                   "of the document, so the payload must match it.");
    }
}
