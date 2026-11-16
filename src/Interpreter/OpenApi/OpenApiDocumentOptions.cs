namespace ScriptLangCore;

/// <summary>The options a WHOLE document takes, which are the ones that belong to the document rather than to one
/// route: its identity (`info`), where it is served from, and two decisions that apply to every schema in it.
///
/// `required` and `formats` are deliberately NOT here: both are statements about a SAMPLE, and a document has no
/// sample. They belong to the route that described one, so the refusal names where to put them instead of accepting
/// a key that would do nothing, which is the same rule the route reader follows for the document's keys.</summary>
internal sealed class OpenApiDocumentOptions
{
    internal const string ValidKeys = "title, version, description, server, extract, additionalProperties, pretty";

    /// <summary>`info.title`. A document with no name still needs one, so this is the placeholder rather than an
    /// error: an OpenAPI document whose title is empty is invalid, and a refusal here would make `http.OpenApi()`
    /// unusable without an argument.</summary>
    internal string Title = "API";

    internal string Version = "1.0.0";

    internal string? Description;

    /// <summary>The base URL a client should use, written as `servers[0].url`. Absent means the spec's own default:
    /// the paths resolve against the URL the document was served from, which is the one address this engine does not
    /// have to be told.</summary>
    internal string? Server;

    /// <summary>Whether a nested object becomes a component referenced with `$ref`. ON by default, unlike
    /// `Json.ToOpenApi`'s, because a document with several operations would repeat every shape in every operation
    /// that mentions it: `extract: false` inlines everything, which is readable for a one-route document.</summary>
    internal bool Extract = true;

    /// <summary>The document's `additionalProperties` default, which a route's own statement overrides.</summary>
    internal bool? AdditionalProperties;

    internal bool Pretty = true;

    internal static OpenApiDocumentOptions Read(object? raw, string member, bool caseSensitive)
    {
        var options = new OpenApiDocumentOptions();
        if (raw is null) return options;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var seen = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in OpenApiOptions.Pairs(raw, member))
        {
            if (!seen.Add(name))
                throw new ScriptException($"{member}: the option \"{name}\" is given twice.");

            if (name.Equals("title", comparison)) options.Title = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("version", comparison)) options.Version = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("description", comparison)) options.Description = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("server", comparison)) options.Server = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("extract", comparison)) options.Extract = OpenApiOptions.Flag(name, value, member);
            else if (name.Equals("additionalProperties", comparison))
                options.AdditionalProperties = OpenApiOptions.Flag(name, value, member);
            else if (name.Equals("pretty", comparison)) options.Pretty = OpenApiOptions.Flag(name, value, member);
            else if (name.Equals("required", comparison) || name.Equals("formats", comparison)
                     || name.Equals("sample", comparison) || name.Equals("schema", comparison))
                throw new ScriptException($"{member}: \"{name}\" is a ROUTE's statement about its own sample, so it "
                    + "belongs in the third argument of http.MapGet/MapPost/MapPut/MapDelete, not in the document's "
                    + $"options. Valid document options: {ValidKeys}.");
            else
                throw new ScriptException($"{member}: unknown option \"{name}\". Valid options: {ValidKeys}.");
        }

        if (options.Title.Length == 0) options.Title = "API";
        if (options.Version.Length == 0) options.Version = "1.0.0";
        return options;
    }
}

/// <summary>The handler of a route `http.MapOpenApi` registered: not a script function, but the REQUEST for the
/// session's document, which the route middleware answers from the live table. It is a route like any other so that
/// `http.IsServed`, the duplicate-route warning and the source a 404 names all keep working for it.</summary>
internal sealed class OpenApiDocumentRoute(OpenApiDocumentOptions options)
{
    /// <summary>The document options given at registration, frozen there: the ROUTES are read live, the document's
    /// own identity is what the program asked for when it mapped it.</summary>
    internal OpenApiDocumentOptions Options { get; } = options;
}
