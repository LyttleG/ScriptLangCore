namespace ScriptLangCore;

/// <summary>Checks a JSON value against one of the schemas this engine can WRITE, so a contract a route publishes is
/// also one it keeps. The subset is the writer's own (<c>type</c>, <c>properties</c>, <c>required</c>, <c>items</c>,
/// <c>nullable</c>, <c>additionalProperties</c>), because a check that understood more than the document could say
/// would be a promise the document never made.
///
/// A <c>format</c> is deliberately NOT enforced. <c>date-time</c>, <c>date</c> and <c>uuid</c> are shapes a sample's
/// text SUGGESTED, and a generator reads them as advice; refusing a payload for a format the engine merely guessed
/// would reject data the API accepts, which is the failure the inferrer's own documentation already records.
///
/// The answer is the FIRST mismatch, as a path and a reason (<c>$.lines[2].qty</c> and <c>expected a number</c>), or
/// null when the value matches. Both halves are kept apart rather than pre-joined because a script answering a 400
/// wants the field, while a log line wants the sentence.</summary>
internal readonly record struct OpenApiMismatch(string Path, string Reason)
{
    /// <summary>The one sentence a refusal prints.</summary>
    internal string Sentence => $"{Path}: {Reason}";
}

internal static class OpenApiSchemaValidator
{
    /// <summary>The root's path, in the notation a JSON reader already knows.</summary>
    internal const string Root = "$";

    /// <param name="requiredFromSample">Whether the merged sample implies `required` (the document or route option
    /// `required: sample`). A schema's own stated list is checked either way.</param>
    /// <param name="additionalProperties">The document's or route's `additionalProperties` claim, which a member
    /// stated on the schema itself overrides.</param>
    internal static OpenApiMismatch? Check(OpenApiSchemaNode node, OpenApiSample sample, bool requiredFromSample,
        bool? additionalProperties, string path = Root)
    {
        if (node.Kind == OpenApiSchemaKind.Any) return null;

        if (sample.Kind == OpenApiSampleKind.Null)
            return node.Nullable ? null : new OpenApiMismatch(path, $"expected {Describe(node.Kind)}, and null is not allowed here");

        switch (node.Kind)
        {
            case OpenApiSchemaKind.Boolean:
                return sample.Kind == OpenApiSampleKind.Boolean ? null : new OpenApiMismatch(path, "expected a boolean");

            case OpenApiSchemaKind.Integer:
                // A fractional value is NOT an integer here even when it happens to be whole: the sample reader has
                // already separated the two, so 1.0 was written as a JSON number and the payload said "number".
                return sample.Kind == OpenApiSampleKind.Integer ? null : new OpenApiMismatch(path, "expected an integer");

            case OpenApiSchemaKind.Number:
                return sample.Kind is OpenApiSampleKind.Integer or OpenApiSampleKind.Number
                    ? null
                    : new OpenApiMismatch(path, "expected a number");

            case OpenApiSchemaKind.String:
                return sample.Kind == OpenApiSampleKind.String ? null : new OpenApiMismatch(path, "expected a string");

            case OpenApiSchemaKind.Array:
                {
                    if (sample.Kind != OpenApiSampleKind.Array) return new OpenApiMismatch(path, "expected an array");
                    if (node.Items is null) return null;
                    var items = sample.Items ?? [];
                    for (var i = 0; i < items.Count; i++)
                    {
                        var mismatch = Check(node.Items, items[i], requiredFromSample, additionalProperties,
                            path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                        if (mismatch != null) return mismatch;
                    }

                    return null;
                }

            case OpenApiSchemaKind.Object:
                {
                    if (sample.Kind != OpenApiSampleKind.Object) return new OpenApiMismatch(path, "expected an object");

                    // `required` has the same two sources the writer unions, asked the same way, so what the
                    // document says is mandatory is what the check enforces.
                    var members = sample.Members ?? [];
                    foreach (var name in RequiredNames(node, requiredFromSample))
                    {
                        if (!members.Any(m => string.Equals(m.Key, name, StringComparison.Ordinal)))
                            return new OpenApiMismatch(Child(path, name), "required, and the payload does not carry it");
                    }

                    var allowsAdditional = node.AdditionalProperties ?? additionalProperties;
                    foreach (var (name, value) in members)
                    {
                        var declared = node.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                        if (declared is null)
                        {
                            if (allowsAdditional == false)
                                return new OpenApiMismatch(Child(path, name), "an additional property, and the schema allows none");
                            continue;
                        }

                        var mismatch = Check(declared.Node, value, requiredFromSample, additionalProperties,
                            Child(path, name));
                        if (mismatch != null) return mismatch;
                    }

                    return null;
                }

            default:
                return null;
        }
    }

    /// <summary>The names a payload must carry: what the schema STATED, plus (only when asked) what every merged
    /// sample carried. One implementation, so the writer and the check cannot disagree about "required".</summary>
    internal static IEnumerable<string> RequiredNames(OpenApiSchemaNode node, bool requiredFromSample)
    {
        if (node.ExplicitRequired.Count > 0)
            foreach (var name in node.ExplicitRequired) yield return name;
        if (!requiredFromSample || node.ObjectSamples == 0) yield break;
        foreach (var property in node.Properties)
            if (property.Present == node.ObjectSamples
                && !node.ExplicitRequired.Contains(property.Name, StringComparer.Ordinal))
                yield return property.Name;
    }

    private static string Child(string path, string name) =>
        path == Root ? Root + "." + name : path + "." + name;

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
}
