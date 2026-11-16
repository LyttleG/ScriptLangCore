namespace ScriptLangCore;

/// <summary>Parses an Avro schema (1.12.0) from JSON text, or from a script value that serializes to it, into the
/// engine's model. One parser, so `Avro.Parse`, `Validate`, `Canonical`, `Fingerprint`, `Encode` and `Decode`
/// cannot disagree about what a schema text means.
///
/// The rules this enforces, each with the side it protects:
/// - a named type is registered BEFORE its fields are parsed, so a record may reference itself (a linked list);
/// - a name that is not defined above it is refused (Avro has no forward references, and a lazy resolver would
///   accept a schema the spec rejects);
/// - a duplicate fullname, a duplicate field name and a duplicate enum symbol are refused, which 1.12 states
///   explicitly;
/// - aliases are accepted as ANY string (1.12) but must be unique;
/// - a logical type must sit on the underlying type the spec names, else it is dropped as unknown (readers must
///   fall back to the base type).</summary>
internal static class AvroSchemaParser
{
    /// <summary>Parses schema text. `member` is the calling member's spelling, so a refusal names the call.</summary>
    internal static AvroSchema Parse(string json, string member)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ScriptException(
                $"{member}: the schema is not valid JSON: {FriendlyExceptionMessage(ex)}");
        }

        using (document)
        {
            var context = new Context();
            return ParseNode(document.RootElement, context, null, member);
        }
    }

    /// <summary>Parses a schema a script passed: text, or a value that serializes to the JSON form (a dictionary
    /// literal is the natural spelling, since Avro schemas ARE JSON).</summary>
    internal static AvroSchema From(object? schema, string member)
    {
        switch (schema)
        {
            case null:
                throw new ScriptException($"{member}: a schema is required (the .avsc text or a dictionary).");
            case string text when text.AsSpan().Trim().IsEmpty:
                throw new ScriptException($"{member}: the schema text is empty.");
            case string text:
                return Parse(text, member);
            case IScriptDictionary:
            case IScriptList:
            case ClassInstance:
            case StructInstance:
            // A `{ }` literal is a plain CLR Dictionary<object, object> and a `[ ... ]` literal is a typed ARRAY,
            // so neither is one of the engine's own shapes above: measured, both were refused with the message
            // that promises them ("a schema is JSON text or a dictionary, not 'a dictionary'"). Serializing them
            // is what the arm above already does for the engine's spellings, and it is what makes the natural
            // spelling of a union schema (`["null", "int"]`) parse.
            case System.Collections.IDictionary:
            case System.Collections.IEnumerable:
            {
                var text = Interpreter.SerializeToText(schema);
                return Parse(text, member);
            }
            default:
                throw new ScriptException(
                    $"{member}: a schema is JSON text or a dictionary, not '{AvroOptions.FriendlyNameOf(schema)}'.");
        }
    }

    private sealed class Context
    {
        internal readonly Dictionary<string, AvroSchema> Named = new(StringComparer.Ordinal);
    }

    private static AvroSchema ParseNode(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return ParseTypeName(element.GetString()!, context, enclosingNamespace, member);

            case JsonValueKind.Array:
                return ParseUnion(element, context, enclosingNamespace, member);

            case JsonValueKind.Object:
                return ParseObject(element, context, enclosingNamespace, member);

            default:
                throw new ScriptException(
                    $"{member}: a schema is a type name, an array (a union) or an object, not " +
                    $"{DescribeJson(element)}.");
        }
    }

    private static AvroSchema ParseTypeName(string name, Context context, string? enclosingNamespace, string member)
    {
        if (AvroNames.IsPrimitive(name)) return new AvroSchema { Kind = AvroNames.PrimitiveKind(name) };

        // A named reference: a dotted spelling is already a fullname; a bare one looks in the enclosing
        // namespace first, then at the root, which is the resolution rule applied in reverse.
        if (context.Named.TryGetValue(ResolveReference(name, enclosingNamespace), out var named)) return named;

        throw new ScriptException(
            $"{member}: '{name}' is not a type defined above it. Avro has no forward references, so a named type " +
            "must be declared (record, enum or fixed) before the field that uses it.");
    }

    private static string ResolveReference(string name, string? enclosingNamespace)
    {
        if (name.Contains('.')) return name;
        if (enclosingNamespace is not null && name.Length > 0)
            return enclosingNamespace + "." + name;
        return name;
    }

    private static AvroSchema ParseUnion(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        if (element.GetArrayLength() == 0)
            throw new ScriptException($"{member}: a union must have at least one branch.");

        var union = new AvroSchema { Kind = AvroKind.Union };
        var seen = new List<AvroSchema>();

        foreach (var branchElement in element.EnumerateArray())
        {
            var branch = ParseNode(branchElement, context, enclosingNamespace, member);

            if (branch.Kind == AvroKind.Union)
                throw new ScriptException(
                    $"{member}: a union cannot contain another union directly. Flatten " +
                    "[A, [B, C]] into [A, B, C].");

            // Avro: no two branches of the same type, except named types with distinct names. Comparing the
            // INSTANCE is how a repeated named reference is caught, and comparing the kind catches two strings,
            // two arrays, two maps and two primitives of one kind.
            foreach (var other in seen)
            {
                var duplicate = branch.IsNamed
                    ? ReferenceEquals(branch, other) ||
                      (other.IsNamed && other.FullName == branch.FullName)
                    : other.Kind == branch.Kind;
                if (duplicate)
                {
                    throw new ScriptException(
                        $"{member}: a union cannot hold two branches of the same type (" +
                        $"{DescribeSchema(branch)}). Wrap a repeated shape in a named type, or drop the duplicate.");
                }
            }

            seen.Add(branch);
            union.Branches.Add(branch);
        }

        return union;
    }

    private static AvroSchema ParseObject(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        if (!element.TryGetProperty("type", out var typeElement))
            throw new ScriptException($"{member}: a schema object needs a \"type\" attribute.");

        // `{"type": {"type": "array", ...}}` is legal Avro: the type attribute may itself be a schema.
        if (typeElement.ValueKind == JsonValueKind.Object)
        {
            var inner = ParseObject(typeElement, context, enclosingNamespace, member);
            var wrapperLogical = ReadLogicalType(element, inner, member);
            return wrapperLogical ?? inner;
        }

        if (typeElement.ValueKind != JsonValueKind.String)
            throw new ScriptException(
                $"{member}: \"type\" is a type name or a schema object, not {DescribeJson(typeElement)}.");

        var typeName = typeElement.GetString()!;
        return typeName switch
        {
            "record" or "error" => ParseRecord(element, context, enclosingNamespace, member),
            "enum" => ParseEnum(element, context, enclosingNamespace, member),
            "array" => ParseArray(element, context, enclosingNamespace, member),
            "map" => ParseMap(element, context, enclosingNamespace, member),
            "fixed" => ParseFixed(element, context, enclosingNamespace, member),
            _ when AvroNames.IsPrimitive(typeName) => ParsePrimitiveObject(element, typeName, member),
            _ => throw new ScriptException(
                $"{member}: \"{typeName}\" is not an Avro type. The type names are null, boolean, int, long, " +
                "float, double, bytes, string, record, enum, array, map, fixed, or a named type defined above it.")
        };
    }

    private static AvroSchema ParsePrimitiveObject(JsonElement element, string typeName, string member)
    {
        var primitive = new AvroSchema { Kind = AvroNames.PrimitiveKind(typeName) };
        return ReadLogicalType(element, primitive, member) ?? primitive;
    }

    private static AvroSchema ParseRecord(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        var (simple, ns, full) = ReadName(element, enclosingNamespace, member);
        Register(context, full, simple, member);

        var record = new AvroSchema
        {
            Kind = AvroKind.Record,
            Name = simple,
            Namespace = ns,
            FullName = full,
            Aliases = ReadAliases(element),
            Doc = element.TryGetProperty("doc", out var doc) && doc.ValueKind == JsonValueKind.String
                ? doc.GetString()
                : null
        };

        // Registered before the fields are walked: a record that names itself is legal (a linked list).
        context.Named[full] = record;

        if (!element.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            throw new ScriptException($"{member}: record '{full}' needs a \"fields\" array.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fieldElement in fields.EnumerateArray())
        {
            if (fieldElement.ValueKind != JsonValueKind.Object)
                throw new ScriptException(
                    $"{member}: record '{full}' has a field that is not an object ({DescribeJson(fieldElement)}).");

            if (!fieldElement.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
                throw new ScriptException($"{member}: a field of record '{full}' has no \"name\" string.");

            var fieldName = nameElement.GetString()!;
            if (!AvroNames.IsValidName(fieldName))
                throw new ScriptException(
                    $"{member}: field name '{fieldName}' in record '{full}' is invalid: a name matches " +
                    "[A-Za-z_][A-Za-z0-9_]*. Alias the old name if the data was written under it.");

            if (!names.Add(fieldName))
                throw new ScriptException(
                    $"{member}: record '{full}' declares the field '{fieldName}' twice. 1.12 states duplicate " +
                    "field names are an error, and a reader cannot tell which one a writer wrote.");

            if (!fieldElement.TryGetProperty("type", out var fieldTypeElement))
                throw new ScriptException($"{member}: field '{fieldName}' of record '{full}' has no \"type\".");

            var fieldType = ParseNode(fieldTypeElement, context, ns, member);
            var hasDefault = fieldElement.TryGetProperty("default", out var defaultElement);
            object? defaultValue = null;
            if (hasDefault) defaultValue = Interpreter.ParseJsonElement(defaultElement);

            record.Fields.Add(new AvroField
            {
                Name = fieldName,
                Type = fieldType,
                HasDefault = hasDefault,
                Default = defaultValue,
                Aliases = ReadAliases(fieldElement),
                Doc = fieldElement.TryGetProperty("doc", out var fieldDoc) &&
                      fieldDoc.ValueKind == JsonValueKind.String
                    ? fieldDoc.GetString()
                    : null
            });
        }

        return record;
    }

    private static AvroSchema ParseEnum(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        var (simple, ns, full) = ReadName(element, enclosingNamespace, member);
        Register(context, full, simple, member);

        if (!element.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array)
            throw new ScriptException($"{member}: enum '{full}' needs a \"symbols\" array of names.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var symbol in symbols.EnumerateArray())
        {
            if (symbol.ValueKind != JsonValueKind.String)
                throw new ScriptException(
                    $"{member}: enum '{full}' has a symbol that is not a string ({DescribeJson(symbol)}).");

            var name = symbol.GetString()!;
            if (!AvroNames.IsValidName(name))
                throw new ScriptException(
                    $"{member}: enum '{full}' declares the symbol '{name}', which is not a valid name " +
                    "([A-Za-z_][A-Za-z0-9_]*).");

            if (!names.Add(name))
                throw new ScriptException(
                    $"{member}: enum '{full}' declares the symbol '{name}' twice. 1.12 states duplicate symbols " +
                    "are an error, and a writer's index would be ambiguous.");

            list.Add(name);
        }

        if (list.Count == 0)
            throw new ScriptException($"{member}: enum '{full}' needs at least one symbol.");

        var enumDefault = element.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String
            ? def.GetString()
            : null;
        if (enumDefault is not null && !names.Contains(enumDefault))
            throw new ScriptException(
                $"{member}: enum '{full}' has \"default\": \"{enumDefault}\", which is not one of its symbols. " +
                "The default is the symbol a reader uses for a writer symbol it does not declare.");

        var schema = new AvroSchema
        {
            Kind = AvroKind.Enum,
            Name = simple,
            Namespace = ns,
            FullName = full,
            Aliases = ReadAliases(element),
            Doc = element.TryGetProperty("doc", out var doc) && doc.ValueKind == JsonValueKind.String
                ? doc.GetString()
                : null,
            EnumDefault = enumDefault
        };
        schema.Symbols.AddRange(list);
        context.Named[full] = schema;
        return schema;
    }

    private static AvroSchema ParseArray(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        if (!element.TryGetProperty("items", out var items))
            throw new ScriptException($"{member}: an array schema needs an \"items\" schema.");
        return new AvroSchema
        {
            Kind = AvroKind.Array,
            Items = ParseNode(items, context, enclosingNamespace, member)
        };
    }

    private static AvroSchema ParseMap(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        if (!element.TryGetProperty("values", out var values))
            throw new ScriptException($"{member}: a map schema needs a \"values\" schema.");
        return new AvroSchema
        {
            Kind = AvroKind.Map,
            Values = ParseNode(values, context, enclosingNamespace, member)
        };
    }

    private static AvroSchema ParseFixed(JsonElement element, Context context, string? enclosingNamespace,
        string member)
    {
        var (simple, ns, full) = ReadName(element, enclosingNamespace, member);
        Register(context, full, simple, member);

        if (!element.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number ||
            !sizeElement.TryGetInt32(out var size) || size <= 0)
            throw new ScriptException($"{member}: fixed '{full}' needs a positive whole \"size\".");

        var schema = new AvroSchema
        {
            Kind = AvroKind.Fixed,
            Name = simple,
            Namespace = ns,
            FullName = full,
            Aliases = ReadAliases(element),
            Doc = element.TryGetProperty("doc", out var doc) && doc.ValueKind == JsonValueKind.String
                ? doc.GetString()
                : null,
            FixedSize = size
        };
        context.Named[full] = schema;
        return ReadLogicalType(element, schema, member) ?? schema;
    }

    /// <summary>Reads and validates a logical-type annotation. Unknown or invalid annotations are DROPPED, which
    /// is what the spec requires of a reader (use the base type), so the caller's schema stays usable either way.
    /// The three the spec ships but C# cannot map today are kept on the model so the value map can refuse them BY
    /// NAME rather than encoding something a reader would misread.</summary>
    private static AvroSchema? ReadLogicalType(JsonElement element, AvroSchema baseSchema, string member)
    {
        if (!element.TryGetProperty("logicalType", out var logicalElement) ||
            logicalElement.ValueKind != JsonValueKind.String)
            return null;

        var logical = logicalElement.GetString()!;
        var kind = baseSchema.Kind;

        var valid = logical switch
        {
            "decimal" => kind is AvroKind.Bytes or AvroKind.Fixed,
            "uuid" => kind == AvroKind.String || (kind == AvroKind.Fixed && baseSchema.FixedSize == 16),
            "date" or "time-millis" => kind == AvroKind.Int,
            "time-micros" or "timestamp-millis" or "timestamp-micros" or "timestamp-nanos" or
                "local-timestamp-millis" or "local-timestamp-micros" or "local-timestamp-nanos" =>
                kind == AvroKind.Long,
            "duration" => kind == AvroKind.Fixed && baseSchema.FixedSize == 12,
            "big-decimal" => kind == AvroKind.Bytes,
            _ => false
        };

        if (!valid)
        {
            // An annotation that cannot apply is DROPPED, not refused: the spec says a reader falls back to the
            // base type, and a schema a writer produced must stay readable here.
            return null;
        }

        if (logical != "decimal") { baseSchema.LogicalType = logical; return baseSchema; }

        var precision = 0;
        var scale = 0;
        if (!element.TryGetProperty("precision", out var precisionElement) ||
            precisionElement.ValueKind != JsonValueKind.Number || !precisionElement.TryGetInt32(out precision) ||
            precision <= 0)
            throw new ScriptException(
                $"{member}: a decimal logical type needs a positive whole \"precision\" (the number of digits it " +
                "can hold). Set it in the schema, or use Avro.Schema with decimalPrecision.");

        if (element.TryGetProperty("scale", out var scaleElement))
        {
            if (scaleElement.ValueKind != JsonValueKind.Number || !scaleElement.TryGetInt32(out scale) || scale < 0)
                throw new ScriptException(
                    $"{member}: decimal \"scale\" is a whole number of digits after the point, 0 or more.");
        }

        if (scale > precision)
            throw new ScriptException(
                $"{member}: decimal scale {scale} is larger than its precision {precision}, so the schema cannot " +
                "represent a value. Lower the scale or raise the precision.");

        if (kind == AvroKind.Fixed)
        {
            // floor(log10(2^(8n-1) - 1)) digits, which for n >= 1 is floor((8n-1) * log10(2)) because
            // 2^k - 1 is odd and can never be a power of ten.
            var capacity = (int)Math.Floor((8.0 * baseSchema.FixedSize - 1) * Math.Log10(2.0));
            if (precision > capacity)
                throw new ScriptException(
                    $"{member}: decimal precision {precision} does not fit fixed({baseSchema.FixedSize}): it holds " +
                    $"at most {capacity} base-10 digits.");
        }

        baseSchema.LogicalType = "decimal";
        baseSchema.Precision = precision;
        baseSchema.Scale = scale;
        return baseSchema;
    }

    private static (string Simple, string? Namespace, string Full) ReadName(JsonElement element,
        string? enclosingNamespace, string member)
    {
        if (!element.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
            throw new ScriptException(
                $"{member}: a named type (record, enum, fixed) needs a \"name\" string.");

        var declared = nameElement.GetString()!;
        var declaredNamespace = element.TryGetProperty("namespace", out var nsElement) &&
                                nsElement.ValueKind == JsonValueKind.String
            ? nsElement.GetString()
            : null;

        if (declared.Contains('.'))
        {
            var cut = declared.LastIndexOf('.');
            var simpleFromFull = declared[(cut + 1)..];
            var nsFromFull = declared[..cut];
            ValidateName(simpleFromFull, declaredNamespace ?? nsFromFull, member);
            return (simpleFromFull, nsFromFull, declared);
        }

        var ns = declaredNamespace ?? enclosingNamespace;
        ValidateName(declared, ns, member);
        return (declared, ns, ns is null ? declared : ns + "." + declared);
    }

    private static void ValidateName(string simple, string? ns, string member)
    {
        if (!AvroNames.IsValidName(simple))
            throw new ScriptException(
                $"{member}: '{simple}' is not a valid Avro name ([A-Za-z_][A-Za-z0-9_]*).");
        if (ns is not null && !AvroNames.IsValidNamespace(ns))
            throw new ScriptException(
                $"{member}: '{ns}' is not a valid Avro namespace (dot-separated names).");
    }

    private static void Register(Context context, string full, string simple, string member)
    {
        if (context.Named.ContainsKey(full))
            throw new ScriptException(
                $"{member}: the type '{full}' is defined twice. 1.12 states two types with the same full name " +
                "are an error (alias the old one instead of redefining it).");
        _ = simple;
    }

    private static IReadOnlyList<string> ReadAliases(JsonElement element)
    {
        if (!element.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array)
            return [];

        var list = new List<string>();
        foreach (var alias in aliases.EnumerateArray())
        {
            if (alias.ValueKind != JsonValueKind.String) continue;
            var text = alias.GetString()!;
            // Any string is accepted (1.12), but a duplicate alias would make the mapping ambiguous.
            var seen = false;
            foreach (var existing in list)
                if (existing == text) { seen = true; break; }
            if (!seen) list.Add(text);
        }
        return list;
    }

    private static string DescribeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "\"" + (element.GetString() ?? "") + "\"",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        _ => "an object"
    };

    private static string DescribeSchema(AvroSchema schema) => schema.Kind switch
    {
        AvroKind.Union => "a union",
        AvroKind.Array => "an array",
        AvroKind.Map => "a map",
        _ => schema.DisplayName
    };
}
