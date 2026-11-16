namespace ScriptLangCore;

/// <summary>A schema written by a script, read into the engine's node, so that ONE description serves both the
/// published document and the request check: what a route says it accepts is what it enforces.
///
/// It is INLINE, on purpose. A route is registered before any document exists, so a <c>$ref</c> would point at
/// nothing, and a component map carried on a single route would be a second document to keep in step with the
/// first. A keyword this cannot honour (<c>oneOf</c>, <c>enum</c>, <c>minimum</c>, <c>pattern</c>) is REFUSED BY
/// NAME rather than dropped, because a dropped constraint is a contract the route publishes and does not keep,
/// which is the one outcome this feature exists to prevent.
///
/// The subset is exactly what the inferrer emits and the writer writes, so a <c>Json.SchemaOf</c> answer
/// round-trips: <c>type</c>, <c>format</c>, <c>properties</c>, <c>required</c>, <c>items</c>, <c>nullable</c>,
/// <c>additionalProperties</c>.</summary>
internal static class OpenApiSchemaFromScript
{
    internal const string ValidKeys =
        "type, format, properties, required, items, nullable, additionalProperties";

    /// <summary>Keywords that ARE real JSON Schema and are deliberately not carried, so a refusal can say why
    /// instead of reading like a typo.</summary>
    private static readonly string[] NotCarried =
    [
        "$ref", "allOf", "oneOf", "anyOf", "enum", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum",
        "minLength", "maxLength", "pattern", "minItems", "maxItems", "default", "example"
    ];

    private static readonly string[] Types =
        ["object", "array", "string", "integer", "number", "boolean"];

    internal static OpenApiSchemaNode Read(object? raw, string member, string hint, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Every member is READ BEFORE ANYTHING IS BUILT, because the dictionary's order is the author's and
        // `format` may arrive before `type`: interpreting as it goes would make the two orders different schemas.
        string? typeText = null;
        string? formatText = null;
        object? propertiesRaw = null;
        object? requiredRaw = null;
        object? itemsRaw = null;
        var nullable = false;
        bool? additionalProperties = null;
        var seen = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in OpenApiOptions.Pairs(raw, member))
        {
            if (!seen.Add(name))
                throw new ScriptException($"{member}: {hint} states \"{name}\" twice.");

            if (name.Equals("type", comparison)) typeText = OpenApiOptions.Text(name, value, member).Trim();
            else if (name.Equals("format", comparison)) formatText = OpenApiOptions.Text(name, value, member).Trim();
            else if (name.Equals("properties", comparison)) propertiesRaw = value;
            else if (name.Equals("required", comparison)) requiredRaw = value;
            else if (name.Equals("items", comparison)) itemsRaw = value;
            else if (name.Equals("nullable", comparison)) nullable = OpenApiOptions.Flag(name, value, member);
            else if (name.Equals("additionalProperties", comparison))
                additionalProperties = OpenApiOptions.Flag(name, value, member);
            else if (NotCarried.Contains(name, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))
                throw new ScriptException($"{member}: {hint} carries no \"{name}\" member: a route's schema states what "
                    + $"a sample can establish ({ValidKeys}), and a constraint this engine cannot honour is refused "
                    + "rather than dropped, because a published contract it does not enforce is worse than no "
                    + "contract.");
            else
                throw new ScriptException($"{member}: {hint} has an unknown member \"{name}\". Valid members: "
                    + $"{ValidKeys}.");
        }

        var node = new OpenApiSchemaNode();
        if (typeText != null)
        {
            node.Kind = typeText.Length == 0
                ? OpenApiSchemaKind.Any
                : Types.Contains(typeText, caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
                    ? KindOf(typeText)
                    : throw new ScriptException($"{member}: {hint} states a \"type\" of '{typeText}', which is not one "
                        + $"of {string.Join(", ", Types)}, and an empty string means any value.");
        }
        else if (propertiesRaw != null) node.Kind = OpenApiSchemaKind.Object;
        else if (itemsRaw != null) node.Kind = OpenApiSchemaKind.Array;

        node.SeenValue = node.Kind != OpenApiSchemaKind.Any;
        node.Nullable = nullable;
        node.AdditionalProperties = additionalProperties;

        if (formatText != null)
        {
            switch (node.Kind)
            {
                case OpenApiSchemaKind.String:
                    node.Format = formatText.Length == 0 ? null : formatText;
                    node.FormatSettled = node.Format != null;
                    break;

                case OpenApiSchemaKind.Integer:
                    if (!string.Equals(formatText, "int32", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(formatText, "int64", StringComparison.OrdinalIgnoreCase))
                        throw new ScriptException($"{member}: {hint} states an integer \"format\" of '{formatText}'; "
                            + "this engine writes int32 or int64.");
                    node.FitsInt32 = !string.Equals(formatText, "int64", StringComparison.OrdinalIgnoreCase);
                    break;

                case OpenApiSchemaKind.Number:
                    if (!string.Equals(formatText, "double", StringComparison.OrdinalIgnoreCase))
                        throw new ScriptException($"{member}: {hint} states a number \"format\" of '{formatText}'; "
                            + "this engine writes double.");
                    break;

                default:
                    throw new ScriptException($"{member}: {hint} states a \"format\", which says nothing for a value "
                        + "of type " + (node.Kind == OpenApiSchemaKind.Any ? "any" : Describe(node.Kind)) + ".");
            }
        }

        if (propertiesRaw != null)
        {
            if (node.Kind != OpenApiSchemaKind.Object)
                throw new ScriptException($"{member}: {hint} has a \"properties\" member, so it describes an object, "
                    + $"not {Describe(node.Kind)}.");
            foreach (var (name, value) in OpenApiOptions.Pairs(propertiesRaw, member))
            {
                var property = new OpenApiSchemaProperty(name) { Node = Read(value, member, $"{hint}'s \"{name}\"", caseSensitive) };
                node.Properties.Add(property);
            }
        }

        if (itemsRaw != null)
        {
            if (node.Kind != OpenApiSchemaKind.Array)
                throw new ScriptException($"{member}: {hint} has an \"items\" member, so it describes an array, "
                    + $"not {Describe(node.Kind)}.");
            node.Items = Read(itemsRaw, member, $"{hint}'s \"items\"", caseSensitive);
        }

        if (requiredRaw != null)
        {
            if (node.Kind != OpenApiSchemaKind.Object)
                throw new ScriptException($"{member}: {hint} states \"required\", which only an object can.");
            foreach (var name in Names(requiredRaw, member, hint))
            {
                if (node.Properties.All(p => !string.Equals(p.Name, name, StringComparison.Ordinal)))
                    throw new ScriptException($"{member}: {hint} states \"required\" for '{name}', which its "
                        + "\"properties\" do not declare: a name with no schema is a requirement the document could "
                        + "not write.");
                node.ExplicitRequired.Add(name);
            }

            // The sample-derived counts are set from the statement, so a writer whose `required` option is left at
            // its default still emits what the schema said.
            node.ObjectSamples = 1;
            foreach (var property in node.Properties)
                property.Present = node.ExplicitRequired.Contains(property.Name, StringComparer.Ordinal) ? 1 : 0;
        }

        return node;
    }

    private static OpenApiSchemaKind KindOf(string text) => text.ToLowerInvariant() switch
    {
        "object" => OpenApiSchemaKind.Object,
        "array" => OpenApiSchemaKind.Array,
        "string" => OpenApiSchemaKind.String,
        "integer" => OpenApiSchemaKind.Integer,
        "number" => OpenApiSchemaKind.Number,
        _ => OpenApiSchemaKind.Boolean
    };

    private static string Describe(OpenApiSchemaKind kind) => kind switch
    {
        OpenApiSchemaKind.Boolean => "a boolean",
        OpenApiSchemaKind.Integer => "an integer",
        OpenApiSchemaKind.Number => "a number",
        OpenApiSchemaKind.String => "a string",
        OpenApiSchemaKind.Array => "an array",
        OpenApiSchemaKind.Object => "an object",
        _ => "any value"
    };

    private static List<string> Names(object? raw, string member, string hint)
    {
        var names = new List<string>();
        switch (raw)
        {
            case string single:
                names.Add(single);
                break;

            case IScriptDictionary dictionary:
                foreach (var key in dictionary.Keys()) names.Add(NameString(key, member, hint));
                break;

            case IDictionary table:
                foreach (DictionaryEntry entry in table) names.Add(NameString(entry.Key, member, hint));
                break;

            case IEnumerable sequence:
                foreach (var item in sequence) names.Add(NameString(item, member, hint));
                break;

            default:
                throw new ScriptException($"{member}: {hint}'s \"required\" is a list of property names, not "
                    + $"'{Interpreter.GetFriendlyTypeNameOfValue(raw)}'.");
        }

        return names;
    }

    internal static string NameString(object? value, string member, string hint) => value as string
        ?? throw new ScriptException($"{member}: {hint} is named by a string, not "
            + $"'{Interpreter.GetFriendlyTypeNameOfValue(value)}'.");
}
