namespace ScriptLangCore;

/// <summary>The options `Json.SchemaOf` and `Json.ToOpenApi` take, read from a script dictionary, a
/// <c>{ ... }</c> literal (a raw CLR dictionary) or a JSON object written as text. ONE reader for the two
/// members, so an option cannot mean one thing here and another there.
///
/// An UNKNOWN KEY IS REFUSED BY NAME rather than warned about and dropped, which is the opposite of
/// <c>ReadSerializeOptions</c>: that one serves display flags, where a dropped typo costs a formatting choice,
/// while these two produce a DOCUMENT another program consumes. A misspelled <c>path</c> there would yield a
/// spec that is perfectly valid and describes the wrong endpoint.
///
/// <c>required</c> defaults to <c>none</c> because a single sample cannot prove a field mandatory: marking
/// everything present as required produces a spec that rejects payloads the API accepts, which is the same
/// failure the type-conflict rule avoids. <c>"sample"</c> asks for the sample's own shape (with several samples,
/// the intersection).</summary>
internal sealed class OpenApiOptions
{
    internal const string ValidKeys =
        "title, version, description, path, method, rootName, side, status, extract, formats, required, " +
        "additionalProperties, pretty";

    private static readonly string[] Methods =
        ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    private static readonly string[] Sides = ["response", "request", "both"];

    private static readonly string[] RequiredModes = ["none", "sample"];

    internal string Title = "";
    internal string Version = "1.0.0";
    internal string? Description;
    internal string Path = "/";
    internal string Method = "get";
    internal string RootName = "Payload";
    internal string Side = "response";
    internal int Status = 200;
    internal bool Extract;
    internal bool DetectFormats = true;
    internal bool MarkRequired;
    internal bool? AdditionalProperties;
    internal bool Pretty = true;

    internal static OpenApiOptions Read(object? raw, string member, bool caseSensitive)
    {
        var options = new OpenApiOptions();

        if (raw is string text)
        {
            try
            {
                raw = ParseJsonInput(text);
            }
            catch (ScriptException ex)
            {
                throw new ScriptException($"{member}: the options text is not a JSON object: {ex.Message}");
            }
        }

        // Normalise BEFORE anything else can return, because the DEFAULTS are normalised too: called with no
        // options at all, a document used to state an EMPTY title, and a path or method a caller wrote would
        // never have been canonicalised. (Measured while writing the first probe: `Json.ToOpenApi(sample)`
        // answered `"title": ""`.)
        if (raw is not null)
        {
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var seen = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

            foreach (var (name, value) in Pairs(raw, member))
            {
                if (!seen.Add(name))
                    throw new ScriptException($"{member}: the option \"{name}\" is given twice.");

                if (name.Equals("title", comparison)) options.Title = Text(name, value, member);
                else if (name.Equals("version", comparison)) options.Version = Text(name, value, member);
                else if (name.Equals("description", comparison)) options.Description = Text(name, value, member);
                else if (name.Equals("path", comparison)) options.Path = Text(name, value, member);
                else if (name.Equals("method", comparison)) options.Method = Text(name, value, member);
                else if (name.Equals("rootName", comparison)) options.RootName = Text(name, value, member);
                else if (name.Equals("side", comparison)) options.Side = Choice(name, value, Sides, member);
                else if (name.Equals("status", comparison)) options.Status = (int)Whole(name, value, member);
                else if (name.Equals("extract", comparison)) options.Extract = Flag(name, value, member);
                else if (name.Equals("formats", comparison)) options.DetectFormats = Flag(name, value, member);
                else if (name.Equals("required", comparison))
                    options.MarkRequired = Choice(name, value, RequiredModes, member) == "sample";
                else if (name.Equals("additionalProperties", comparison))
                    options.AdditionalProperties = Flag(name, value, member);
                else if (name.Equals("pretty", comparison)) options.Pretty = Flag(name, value, member);
                else
                    throw new ScriptException(
                        $"{member}: unknown option \"{name}\". Valid options: {ValidKeys}.");
            }
        }

        options.Normalise(member);
        return options;
    }

    /// <summary>A path is written <c>/users</c> in every example a reader has seen, so a missing leading slash is
    /// added rather than refused, while an unusable method, side or status IS refused: those change what the
    /// document means, and a path's spelling does not.</summary>
    private void Normalise(string member)
    {
        if (Path.Length == 0) Path = "/";
        else if (Path[0] != '/') Path = "/" + Path;

        Method = Method.ToLowerInvariant();
        if (!Methods.Contains(Method, StringComparer.Ordinal))
            throw new ScriptException(
                $"{member}: the method is one of {string.Join(", ", Methods)}, not '{Method}'.");

        if (Status is < 100 or > 599)
            throw new ScriptException($"{member}: the status is an HTTP status code (100 to 599), not {Status}.");

        if (RootName.Length == 0) RootName = "Payload";
        if (Title.Length == 0) Title = RootName;
    }

    internal static List<KeyValuePair<string, object?>> Pairs(object? raw, string member)
    {
        switch (raw)
        {
            case IScriptDictionary script:
                {
                    var keys = script.Keys();
                    var pairs = new List<KeyValuePair<string, object?>>(keys.Length);
                    foreach (var key in keys) pairs.Add(new(NameOf(key, member), script[key]));
                    return pairs;
                }
            case IDictionary table:
                {
                    var pairs = new List<KeyValuePair<string, object?>>(table.Count);
                    foreach (DictionaryEntry entry in table) pairs.Add(new(NameOf(entry.Key, member), entry.Value));
                    return pairs;
                }
            default:
                throw new ScriptException(
                    $"{member} takes its options as a dictionary or a JSON object written as text, not " +
                    $"'{GetFriendlyTypeNameOfValue(raw)}'.");
        }
    }

    internal static string NameOf(object? key, string member) => key as string
        ?? throw new ScriptException(
            $"{member}: an option is named by a string, not '{GetFriendlyTypeNameOfValue(key)}'.");

    internal static string Text(string key, object? value, string member) => value as string
        ?? throw new ScriptException(
            $"{member}: the \"{key}\" option is a string, not '{GetFriendlyTypeNameOfValue(value)}'.");

    /// <summary>A MEDIA TYPE, as a <c>content</c> key spells it: <c>type/subtype</c>, one slash, no parameter. It is
    /// published VERBATIM, so a parameter is the author's to leave out and this refuses one rather than dropping it
    /// quietly: every tool that reads a document keys on the bare type, and the engine's own answer type for an HTML
    /// route is <c>text/html; charset=utf-8</c>, which is why the option takes the type and not the header.</summary>
    internal static string MediaType(string key, object? value, string member)
    {
        var text = Text(key, value, member).Trim();
        var slash = text.IndexOf('/');
        var valid = slash > 0 && slash < text.Length - 1 && text.IndexOf('/', slash + 1) < 0;
        foreach (var c in text)
            if (char.IsWhiteSpace(c) || c is ';' or ',' or '"' or '\'')
            {
                valid = false;
                break;
            }

        if (!valid)
            throw new ScriptException(
                $"{member}: the \"{key}\" option is a media type, as in \"text/html\" or \"application/json\", not "
                + $"'{text}'.");

        return text;
    }

    internal static bool Flag(string key, object? value, string member) => value is bool flag
        ? flag
        : throw new ScriptException(
            $"{member}: the \"{key}\" option is true or false, not '{GetFriendlyTypeNameOfValue(value)}'.");

    internal static long Whole(string key, object? value, string member) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        uint ui => ui,
        _ => throw new ScriptException(
            $"{member}: the \"{key}\" option is a whole number, not '{GetFriendlyTypeNameOfValue(value)}'.")
    };

    internal static string Choice(string key, object? value, string[] allowed, string member)
    {
        if (value is string text)
        {
            foreach (var candidate in allowed)
                if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase)) return candidate;
        }

        throw new ScriptException(
            $"{member}: the \"{key}\" option is one of {string.Join(", ", allowed)}, not " +
            $"'{GetFriendlyTypeNameOfValue(value)}'.");
    }
}
