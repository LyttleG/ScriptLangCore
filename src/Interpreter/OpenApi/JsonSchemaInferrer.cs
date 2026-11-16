namespace ScriptLangCore;

/// <summary>The shapes a JSON sample can establish. <see cref="Any"/> is also what a CONFLICT collapses to:
/// guessing between two shapes produces a schema that rejects payloads which are valid for the API, so the
/// honest answer is "any value here". <c>integer</c> plus <c>number</c> is the one pair that widens instead,
/// because every integer is a number.</summary>
internal enum OpenApiSchemaKind { Any, Boolean, Integer, Number, String, Array, Object }

/// <summary>One member of an inferred object. <see cref="Present"/> is how many of the merged object samples
/// contained it, which is the only definition of "required" this can honestly offer: present in EVERY sample.</summary>
internal sealed class OpenApiSchemaProperty(string name)
{
    internal string Name { get; } = name;

    /// <summary>The property's own schema. Settable because a SCHEMA INPUT builds the node from the script's
    /// dictionary rather than by merging samples, while an inferred one fills the node it was initialised with.</summary>
    internal OpenApiSchemaNode Node { get; set; } = new();
    internal int Present;
}

/// <summary>The schema of one value, built by merging every sample that reached it.</summary>
internal sealed class OpenApiSchemaNode
{
    internal OpenApiSchemaKind Kind { get; set; }

    /// <summary>Whether a non-null sample established <see cref="Kind"/>. A null-only value stays <c>Any</c>
    /// (serialized as <c>{}</c>), because a null states nothing about what a real value would be.</summary>
    internal bool SeenValue { get; set; }

    /// <summary>Set when two shapes disagreed, so further samples cannot narrow it back.</summary>
    internal bool Conflicted { get; set; }

    internal bool Nullable { get; set; }
    internal bool FitsInt32 { get; set; } = true;
    internal string? Format { get; set; }
    internal bool FormatSettled { get; set; }
    internal OpenApiSchemaNode? Items { get; set; }
    internal List<OpenApiSchemaProperty> Properties { get; } = [];

    /// <summary>How many object samples were merged here, which <see cref="OpenApiSchemaProperty.Present"/> is
    /// compared against.</summary>
    internal int ObjectSamples { get; set; }

    /// <summary>Names a SCHEMA INPUT stated as required, in the order it stated them. A single sample cannot prove a
    /// field mandatory, so `required` has two sources and this is the second one: the writer unions this list with
    /// what the merged samples imply, and the validator asks the same two questions, so what a route publishes and
    /// what it enforces cannot disagree.</summary>
    internal List<string> ExplicitRequired { get; } = [];

    /// <summary>A SCHEMA INPUT's own `additionalProperties`, which beats the document's option because it was stated
    /// about this object rather than about every object in the document. Null means the input said nothing.</summary>
    internal bool? AdditionalProperties { get; set; }

    internal bool IsRoot { get; set; }
}

/// <summary>Infers a schema from a sample by MERGING every value that reaches a node, which is what makes an
/// array of objects honest: properties are unioned across all elements, and a member present in only some of
/// them is not required. Reads only the sample, never a script type: a payload is data, and a type would state
/// what the payload does not.</summary>
internal static class JsonSchemaInferrer
{
    internal static OpenApiSchemaNode Infer(OpenApiSample sample, OpenApiOptions options)
    {
        var root = new OpenApiSchemaNode { IsRoot = true };
        Merge(root, sample, options);
        return root;
    }

    private static void Merge(OpenApiSchemaNode node, OpenApiSample sample, OpenApiOptions options)
    {
        if (sample.Kind == OpenApiSampleKind.Null)
        {
            node.Nullable = true;
            return;
        }

        if (node.Conflicted) return;

        if (!node.SeenValue)
        {
            node.SeenValue = true;
            node.Kind = KindOf(sample);
            if (sample.Kind == OpenApiSampleKind.Integer) node.FitsInt32 = FitsInt32(sample.Integer);
            if (sample.Kind == OpenApiSampleKind.String)
            {
                node.Format = DetectFormat(sample.Text ?? "", options.DetectFormats);
                node.FormatSettled = true;
            }
        }
        else if (node.Kind != KindOf(sample))
        {
            if (node.Kind is OpenApiSchemaKind.Integer or OpenApiSchemaKind.Number
                && sample.Kind is OpenApiSampleKind.Integer or OpenApiSampleKind.Number)
            {
                // integer + number → number, the one widening that cannot reject a value.
                node.Kind = OpenApiSchemaKind.Number;
                node.Format = null;
            }
            else
            {
                node.Kind = OpenApiSchemaKind.Any;
                node.Format = null;
                node.Conflicted = true;
                return;
            }
        }

        switch (node.Kind)
        {
            case OpenApiSchemaKind.Integer:
                node.FitsInt32 &= FitsInt32(sample.Integer);
                break;

            case OpenApiSchemaKind.String when node.FormatSettled:
                // A format is a claim about EVERY value, so one string that does not match withdraws it, and
                // it stays withdrawn: a later matching value cannot re-establish a claim the data disproved.
                if (!string.Equals(DetectFormat(sample.Text ?? "", options.DetectFormats), node.Format,
                        StringComparison.Ordinal))
                    node.Format = null;
                break;

            case OpenApiSchemaKind.Array:
                node.Items ??= new OpenApiSchemaNode();
                foreach (var item in sample.Items ?? []) Merge(node.Items, item, options);
                break;

            case OpenApiSchemaKind.Object:
                node.ObjectSamples++;
                foreach (var (name, value) in sample.Members ?? [])
                {
                    var property = node.Properties
                        .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (property is null)
                    {
                        property = new OpenApiSchemaProperty(name);
                        node.Properties.Add(property);
                    }

                    property.Present++;
                    Merge(property.Node, value, options);
                }

                break;
        }
    }

    private static OpenApiSchemaKind KindOf(OpenApiSample sample) => sample.Kind switch
    {
        OpenApiSampleKind.Boolean => OpenApiSchemaKind.Boolean,
        OpenApiSampleKind.Integer => OpenApiSchemaKind.Integer,
        OpenApiSampleKind.Number => OpenApiSchemaKind.Number,
        OpenApiSampleKind.String => OpenApiSchemaKind.String,
        OpenApiSampleKind.Array => OpenApiSchemaKind.Array,
        OpenApiSampleKind.Object => OpenApiSchemaKind.Object,
        _ => OpenApiSchemaKind.Any
    };

    private static bool FitsInt32(long value) => value is >= int.MinValue and <= int.MaxValue;

    /// <summary>Only the three shapes a sample can establish honestly are detected, and none of them by a regex:
    /// <c>date-time</c>, <c>date</c> and <c>uuid</c> have a fixed length and fixed separators. <c>email</c> and
    /// <c>uri</c> are deliberately absent, because one sample cannot disprove them and a wrong <c>format</c> is
    /// read by generators as a promise (an id that looks like a URI becomes a URI type in every client).</summary>
    internal static string? DetectFormat(string text, bool enabled)
    {
        if (!enabled) return null;
        if (IsUuid(text)) return "uuid";
        if (IsDate(text)) return "date";
        if (IsDateTime(text)) return "date-time";
        return null;
    }

    private static bool IsUuid(string text)
    {
        if (text.Length != 36) return false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-') return false;
            }
            else if (!Uri.IsHexDigit(c)) return false;
        }

        return true;
    }

    private static bool IsDate(string text) => text.Length == 10
        && text[4] == '-' && text[7] == '-'
        && Digits(text, 0, 4) && Digits(text, 5, 2) && Digits(text, 8, 2)
        && Month(text) is >= 1 and <= 12 && Day(text) is >= 1 and <= 31;

    private static bool IsDateTime(string text)
    {
        if (text.Length < 19) return false;
        if (text[4] != '-' || text[7] != '-' || (text[10] != 'T' && text[10] != 't')) return false;
        if (text[13] != ':' || text[16] != ':') return false;
        if (!Digits(text, 0, 4) || !Digits(text, 5, 2) || !Digits(text, 8, 2)) return false;
        if (!Digits(text, 11, 2) || !Digits(text, 14, 2) || !Digits(text, 17, 2)) return false;
        return Month(text) is >= 1 and <= 12 && Day(text) is >= 1 and <= 31
            && Number(text, 11) <= 23 && Number(text, 14) <= 59 && Number(text, 17) <= 60;
    }

    private static bool Digits(string text, int start, int count)
    {
        for (var i = start; i < start + count; i++)
            if (text[i] is < '0' or > '9') return false;
        return true;
    }

    private static int Month(string text) => Number(text, 5);

    private static int Day(string text) => Number(text, 8);

    /// <summary>Two ASCII digits at <paramref name="start"/>, which every caller has already proven are digits.</summary>
    private static int Number(string text, int start) =>
        (text[start] - '0') * 10 + (text[start + 1] - '0');
}
