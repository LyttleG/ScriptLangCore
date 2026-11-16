namespace ScriptLangCore;

/// <summary>What one <c>http.Map*</c> route states about its shapes: the registration's THIRD argument, read once at
/// registration because the published document and the request check both read it, so what a route says it accepts
/// and what it enforces cannot disagree.
///
/// The sample is what a shape is inferred from, the way <c>Json.ToOpenApi</c> infers from one, and a schema may be
/// stated instead for what a sample cannot express (a required field, an int64). <c>side</c> decides which half the
/// shape documents, and its DEFAULT follows the verb: a POST, PUT or PATCH describes its request body, and every
/// other method describes its answer, which is the reading a registration usually means.
///
/// A route with no third argument is registered and never described: it still appears in the document, with its
/// method and path and a response that says its shape is undocumented, which is the honest entry for an endpoint the
/// engine knows exists but knows nothing about.</summary>
internal sealed class OpenApiRouteDescription
{
    internal const string ValidKeys =
        "sample, request, response, schema, side, status, summary, description, tags, rootName, required, formats, "
        + "additionalProperties, validate, requestContentType, responseContentType";

    private static readonly string[] Sides = ["request", "response", "both"];

    private static readonly string[] RequiredModes = ["none", "sample"];

    private static readonly string[] BodyVerbs = ["post", "put", "patch"];

    /// <summary>The schema of the request body, or null when the route documents no request shape.</summary>
    internal OpenApiSchemaNode? Request;

    /// <summary>The schema of the answer, or null when the route documents no response shape.</summary>
    internal OpenApiSchemaNode? Response;

    /// <summary>The response code the sample describes, 200 unless stated.</summary>
    internal int Status = 200;

    internal string? Summary;
    internal string? Description;

    /// <summary>The OpenAPI tags this operation belongs to, in the order they were given.</summary>
    internal List<string> Tags = [];

    /// <summary>The base component name: the response schema is called this, the request one gets <c>Request</c>
    /// appended, and a collision is resolved by the writer's own suffix rule. EMPTY until the reader fills it from the
    /// pattern, because a field that started at "Payload" would make every route in a document share that name and
    /// the pattern that names one would never be consulted (measured: three routes came out Payload, Payload2,
    /// Payload3 before this).</summary>
    internal string RootName = "";

    /// <summary>Whether the merged samples imply `required` (the document option `required: sample`). A SCHEMA INPUT
    /// states its own list, which is carried either way.</summary>
    internal bool MarkRequired;

    internal bool DetectFormats = true;

    /// <summary>This route's own `additionalProperties` claim, which beats the document's because it was made about
    /// this object.</summary>
    internal bool? AdditionalProperties;

    /// <summary>The media type the request body is in, or null when the route states none (a sample describes a JSON
    /// shape, so JSON is what the document assumes). A route that takes a FORM states this, which is also the type the
    /// engine never validates against a JSON sample.</summary>
    internal string? RequestContentType;

    /// <summary>The media type the answer is in, or null when the route states none. Stating ONE is what lets a route
    /// that answers HTML, an image or plain text say so at all: the writer publishes a stated type even with no shape,
    /// where without it a text answer could only be published as application/json or not published.</summary>
    internal string? ResponseContentType;

    /// <summary>Whether a request that contradicts the request schema is REFUSED before the handler runs. On by
    /// default: a route that publishes a contract and then accepts anything is the shape this exists to prevent, and
    /// the opt-out is for a handler that reads its body some other way (form-encoded, binary).</summary>
    internal bool Validate = true;

    internal string Side = "response";

    /// <summary>The method, lowercase, which is both the document's operation key and what the default side is
    /// derived from.</summary>
    internal string Verb = "get";

    /// <summary>Reads the third argument of a registration. A null description means "no description", which is a
    /// route that is registered and documented only by name and path.</summary>
    internal static OpenApiRouteDescription? Read(object? raw, string verb, string pattern, string member,
        bool caseSensitive)
    {
        if (raw is null) return null;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var options = new OpenApiRouteDescription { Verb = verb.ToLowerInvariant() };
        var seen = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        object? sample = null;
        object? requestSample = null;
        object? responseSample = null;
        object? schemaRaw = null;
        string? side = null;

        foreach (var (name, value) in OpenApiOptions.Pairs(raw, member))
        {
            if (!seen.Add(name))
                throw new ScriptException($"{member}: the option \"{name}\" is given twice.");

            if (name.Equals("sample", comparison)) sample = value;
            else if (name.Equals("request", comparison)) requestSample = value;
            else if (name.Equals("response", comparison)) responseSample = value;
            else if (name.Equals("schema", comparison)) schemaRaw = value;
            else if (name.Equals("side", comparison)) side = OpenApiOptions.Choice(name, value, Sides, member);
            else if (name.Equals("status", comparison)) options.Status = (int)OpenApiOptions.Whole(name, value, member);
            else if (name.Equals("summary", comparison)) options.Summary = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("description", comparison)) options.Description = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("responseContentType", comparison))
                options.ResponseContentType = OpenApiOptions.MediaType(name, value, member);
            else if (name.Equals("requestContentType", comparison))
                options.RequestContentType = OpenApiOptions.MediaType(name, value, member);
            else if (name.Equals("tags", comparison)) ReadTags(options, value, member);
            else if (name.Equals("rootName", comparison)) options.RootName = OpenApiOptions.Text(name, value, member);
            else if (name.Equals("required", comparison))
                options.MarkRequired = OpenApiOptions.Choice(name, value, RequiredModes, member) == "sample";
            else if (name.Equals("formats", comparison)) options.DetectFormats = OpenApiOptions.Flag(name, value, member);
            else if (name.Equals("additionalProperties", comparison))
                options.AdditionalProperties = OpenApiOptions.Flag(name, value, member);
            else if (name.Equals("validate", comparison)) options.Validate = OpenApiOptions.Flag(name, value, member);
            else
                throw new ScriptException($"{member}: unknown option \"{name}\". Valid options: {ValidKeys}.");
        }

        if (options.Status is < 100 or > 599)
            throw new ScriptException(
                $"{member}: the \"status\" option is an HTTP status code (100 to 599), not {options.Status}.");

        options.Side = side ?? (BodyVerbs.Contains(options.Verb, StringComparer.Ordinal) ? "request" : "response");
        if (options.RootName.Length == 0) options.RootName = DefaultRootName(pattern);
        RefuseUnusedMediaType(options, member);

        // A SCHEMA input is ONE shape, so both sides of a route that documents both share it: the writer names it
        // once and both halves reference it, which is what the document would say if a script wrote it by hand.
        var stated = schemaRaw is null
            ? null
            : OpenApiSchemaFromScript.Read(schemaRaw, member, $"the \"schema\" of http.{TitleCase(options.Verb)}"
                                                              + $"(\"{pattern}\")", caseSensitive);

        if (options.Side is "request" or "both")
            options.Request = stated ?? Infer(requestSample ?? sample, member, options, options.RootName + "Request");
        if (options.Side is "response" or "both")
            options.Response = stated ?? Infer(responseSample ?? sample, member, options, options.RootName);

        return options;
    }

    /// <summary>A media type stated for a side the route does not publish is REFUSED rather than ignored, because it is
    /// a sentence the author wrote expecting the document to say it, and `side` is what decides whether the document
    /// can. "request" and "both" publish a request body, "response" and "both" publish an answer.</summary>
    private static void RefuseUnusedMediaType(OpenApiRouteDescription options, string member)
    {
        if (options.RequestContentType != null && options.Side == "response")
            throw new ScriptException($"{member}: \"requestContentType\" describes a request body, and this route's "
                + "\"side\" is \"response\", so nothing would publish it. Say \"side\": \"both\" (or \"request\").");

        if (options.ResponseContentType != null && options.Side == "request")
            throw new ScriptException($"{member}: \"responseContentType\" describes the answer, and this route's "
                + "\"side\" is \"request\", so nothing would publish it. Say \"side\": \"both\" (or \"response\").");
    }

    private static OpenApiSchemaNode? Infer(object? sample, string member, OpenApiRouteDescription options,
        string rootName)
    {
        if (sample is null) return null;
        var inference = new OpenApiOptions();
        inference.DetectFormats = options.DetectFormats;
        inference.RootName = rootName;
        return JsonSchemaInferrer.Infer(OpenApiSampleReader.Read(sample, member), inference);
    }

    private static void ReadTags(OpenApiRouteDescription options, object? raw, string member)
    {
        switch (raw)
        {
            case string single:
                if (single.Length > 0) options.Tags.Add(single);
                return;

            case IEnumerable sequence when !(raw is IDictionary) && !(raw is IScriptDictionary):
                foreach (var item in sequence)
                    options.Tags.Add(OpenApiSchemaFromScript.NameString(item, member, "a \"tags\" entry"));
                return;

            default:
                throw new ScriptException($"{member}: the \"tags\" option is a list of strings, not "
                    + $"'{Interpreter.GetFriendlyTypeNameOfValue(raw)}'.");
        }
    }

    /// <summary>The base component name a route gets when it does not state one: its pattern's segments, Pascal-cased
    /// and joined, with a slot contributing the name it captures (`/users/{id}` is <c>UsersId</c>, so two routes
    /// under one prefix do not collide on the prefix alone). The writer's suffix rule resolves two routes that still
    /// land on the same name.</summary>
    internal static string DefaultRootName(string pattern)
    {
        var builder = new StringBuilder();
        foreach (var segment in HttpRouteTable.SegmentsOf(pattern))
        {
            var name = segment.Length > 2 && segment[0] == '{' && segment[^1] == '}' ? segment[1..^1] : segment;
            if (name.Length == 0) continue;
            var safe = new StringBuilder(name.Length);
            foreach (var c in name)
                if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') safe.Append(c);
            if (safe.Length == 0) continue;
            safe[0] = char.ToUpperInvariant(safe[0]);
            builder.Append(safe);
        }

        return builder.Length == 0 ? "Payload" : builder.ToString();
    }

    /// <summary>`POST` from `post`, which is how a refusal names the member a script actually called.</summary>
    private static string TitleCase(string verb) => verb switch
    {
        "get" => "MapGet",
        "post" => "MapPost",
        "put" => "MapPut",
        "delete" => "MapDelete",
        _ => "Map" + verb
    };
}
