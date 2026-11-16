namespace ScriptLangCore;

internal partial class Interpreter
{
    private static readonly TypeInfo OpenApiStringType = new(TokenType.String, false, 0, 0, null);
    private static readonly TypeInfo OpenApiObjectType = new(TokenType.Object, true, 0, 0, null);

    /// <summary>`Json.SchemaOf`'s answer: the inferred schema in the engine's OWN collections, so a script can
    /// index it, walk it and serialize it with no second parse. The keys are the spec's (lowercase), because the
    /// same schema is what `Json.ToOpenApi` embeds, and two spellings of one document is the drift this
    /// repository deletes rather than adds.
    ///
    /// This is the INLINE form always: `extract` names components in a DOCUMENT, and a schema a script holds has
    /// no document around it to reference.</summary>
    private object? ToScriptSchema(OpenApiSchemaNode node, bool markRequired)
    {
        var schema = CreateScriptDictionary(OpenApiStringType, OpenApiObjectType);
        switch (node.Kind)
        {
            case OpenApiSchemaKind.Boolean:
                schema.AddRaw("type", "boolean");
                break;

            case OpenApiSchemaKind.Integer:
                schema.AddRaw("type", "integer");
                schema.AddRaw("format", node.FitsInt32 ? "int32" : "int64");
                break;

            case OpenApiSchemaKind.Number:
                schema.AddRaw("type", "number");
                schema.AddRaw("format", "double");
                break;

            case OpenApiSchemaKind.String:
                schema.AddRaw("type", "string");
                if (node.Format is not null) schema.AddRaw("format", node.Format);
                break;

            case OpenApiSchemaKind.Array:
                schema.AddRaw("type", "array");
                schema.AddRaw("items", node.Items is null
                    ? CreateScriptDictionary(OpenApiStringType, OpenApiObjectType)
                    : ToScriptSchema(node.Items, markRequired));
                break;

            case OpenApiSchemaKind.Object:
                schema.AddRaw("type", "object");
                var properties = CreateScriptDictionary(OpenApiStringType, OpenApiObjectType);
                foreach (var property in node.Properties)
                    properties.AddRaw(property.Name, ToScriptSchema(property.Node, markRequired));
                schema.AddRaw("properties", properties);

                if (markRequired && node.ObjectSamples > 0)
                {
                    var required = CreateScriptList(OpenApiStringType);
                    foreach (var property in node.Properties)
                        if (property.Present == node.ObjectSamples) required.AddRaw(property.Name);
                    if (required.Count > 0) schema.AddRaw("required", required);
                }

                break;
        }

        if (node.Nullable && node.Kind != OpenApiSchemaKind.Any) schema.AddRaw("nullable", true);
        return schema;
    }

    // ── Route enforcement: a contract a registration publishes is also one it keeps ────────────────────────────
    // The FIRST half is a refusal, because the handler has not run yet and would otherwise store data the document
    // promised it would not accept. The SECOND is a warning, because the answer has already been produced and a
    // client is waiting for it: rewriting it would mean answering something nobody wrote.

    /// <summary>Checks a request body against the shape its route published, and REFUSES it with a 400 whose body is
    /// the sentence naming the first mismatch. Answers true when it refused, so the middleware stops there.
    ///
    /// THREE THINGS IT DELIBERATELY DOES NOT CHECK, each measured rather than assumed: a body whose CONTENT TYPE is
    /// not JSON (a route taking form-encoded or binary data would be refused for a shape it never claimed), a body an
    /// earlier middleware already read as BYTES or LINE BY LINE (the text door refuses a second read, and that is the
    /// program's design rather than a mismatch), and anything at all when the route said <c>"validate": false</c>.
    /// An EMPTY body is checked like any other value, so a route whose schema wants an object refuses a request that
    /// carried none, which is what `requestBody: required` promises.</summary>
    internal bool RefuseRequestBody(HttpContext ctx, OpenApiRouteDescription description, string method, string path)
    {
        if (!description.Validate || description.Request is null) return false;
        if (description.Side is not ("request" or "both")) return false;

        var contentType = HeaderValue(ctx.RequestHeaders, "Content-Type");
        if (contentType.Length > 0 && !IsJsonMediaType(contentType)) return false;

        string? body;
        try
        {
            body = ctx.Body;
        }
        catch (ScriptException)
        {
            return false;
        }

        var sample = default(OpenApiSample);
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                sample = OpenApiSampleReader.Read(body, $"{method} {path}");
            }
            catch (ScriptException ex)
            {
                return RefuseBody(ctx, method, path, $"the request body is not JSON ({ex.Message})");
            }
        }

        var mismatch = OpenApiSchemaValidator.Check(description.Request, sample, description.MarkRequired,
            description.AdditionalProperties);
        return mismatch is { } bad
            && RefuseBody(ctx, method, path, $"the request body does not match this route's schema: {bad.Sentence}");
    }

    /// <summary>Checks the ANSWER against the response shape its route published, and REPORTS a mismatch instead of
    /// changing it. Only a JSON answer is checked: a text or HTML answer is the handler's own decision and not what
    /// the schema describes, and a bytes or stream answer is framed by the engine rather than serialized here.</summary>
    internal void CheckAnswer(HttpContext ctx, OpenApiRouteDescription description, string method, string path)
    {
        if (!description.Validate || description.Response is null) return;
        if (description.Side is not ("response" or "both")) return;
        if (ctx.ResponseBytes != null || ctx.ResponseStream != null) return;
        if (!IsJsonMediaType(ctx.ResponseContentType ?? "")) return;

        if (ctx.ResponseBody is null)
        {
            Logging.Log.Warning("MCP",
                $"{method} {path} answered no body, and its documented response is a shape.");
            return;
        }

        OpenApiSample sample;
        try
        {
            sample = OpenApiSampleReader.Read(ctx.ResponseBody, $"{method} {path}");
        }
        catch (ScriptException ex)
        {
            Logging.Log.Warning("MCP", $"{method} {path} answered a body that is not JSON ({ex.Message}).");
            return;
        }

        var mismatch = OpenApiSchemaValidator.Check(description.Response, sample, description.MarkRequired,
            description.AdditionalProperties);
        if (mismatch is { } bad)
            Logging.Log.Warning("MCP",
                $"{method} {path} answered a body that does not match its documented schema: {bad.Sentence}");
    }

    private bool RefuseBody(HttpContext ctx, string method, string path, string sentence)
    {
        Logging.Log.Warning("MCP", $"{method} {path} refused a request body: {sentence}");
        ctx.ResponseStatus = 400;
        ctx.ResponseBody = sentence;
        ctx.ResponseContentType = "text/plain; charset=utf-8";
        return true;
    }

    /// <summary>A header by name, case-insensitively: HTTP header names are case-insensitive by spec, and the
    /// dictionary the server fills keeps the spelling the client sent, so an exact lookup would miss
    /// <c>content-type</c>.</summary>
    private static string HeaderValue(Dictionary<string, string> headers, string name)
    {
        if (headers.TryGetValue(name, out var exact)) return exact;
        foreach (var (key, value) in headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return "";
    }

    /// <summary>Whether a content type is JSON, including the <c>+json</c> structured-suffix spellings.</summary>
    private static bool IsJsonMediaType(string contentType) =>
        contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
}
