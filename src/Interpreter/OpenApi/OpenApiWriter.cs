namespace ScriptLangCore;

/// <summary>One operation a document describes: the method, its path as the route table holds it, and what the
/// registration SAID about its shapes. A null description is an endpoint the engine knows exists and knows nothing
/// else about, which is still documented (a path, a method, and a response that says so) rather than dropped: a spec
/// that hides an endpoint the server answers is a spec a client author will trip over.</summary>
internal sealed record OpenApiOperation(string Method, string Path, OpenApiRouteDescription? Description);

/// <summary>Writes an inferred schema as an OpenAPI 3.0.3 document.
///
/// 3.0.3 rather than 3.1 on purpose: that is the version Swagger UI, generators and Postman all read without a
/// dialect flag. 3.1 replaces <c>nullable: true</c> with a type array, so supporting it is a second emitter and
/// not a string change, and it is refused by name for now rather than half-supported.
///
/// The keys are the SPEC's (lowercase: <c>openapi</c>, <c>info</c>, <c>paths</c>, <c>properties</c>,
/// <c>$ref</c>), which is the one place a document here departs from the engine's PascalCase convention: those
/// names are not this engine's to choose. Every group is written in a FIXED order and an absent member is
/// omitted, so the same sample yields the same bytes and two documents can be diffed.</summary>
internal static class OpenApiWriter
{
    private const string JsonMediaType = "application/json";
    private const string ComponentsPrefix = "#/components/schemas/";

    /// <summary>What a described shape's description says, and it is the SAME sentence `Json.ToOpenApi` writes, so a
    /// document built by the table and one built by hand read alike.</summary>
    private const string Inferred = "inferred from a JSON sample";

    /// <summary>A route nobody described still gets its response entry, because the spec requires `responses` and a
    /// guessed schema would be a claim the server cannot keep.</summary>
    private const string NoResponseSample = "no sample describes this response";

    /// <summary>A component, and the options it must be written with: `required` and `additionalProperties` are
    /// per-ROUTE statements, so a document's components block is not written with one set of options.</summary>
    private readonly record struct Component(string Name, OpenApiSchemaNode Node, OpenApiOptions Options);

    internal static string Write(OpenApiSchemaNode root, OpenApiOptions options)
    {
        var components = new List<Component>();
        var names = new Dictionary<OpenApiSchemaNode, string>();
        if (options.Extract) Register(root, options.RootName, options, names, components, new HashSet<string>(StringComparer.Ordinal));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Interpreter.JsonWriterOpts(options.Pretty)))
        {
            writer.WriteStartObject();
            writer.WriteString("openapi", "3.0.3");
            WriteInfo(writer, options.Title, options.Version, options.Description);

            writer.WritePropertyName("paths");
            writer.WriteStartObject();
            writer.WritePropertyName(options.Path);
            writer.WriteStartObject();
            writer.WritePropertyName(options.Method);
            writer.WriteStartObject();

            if (options.Side is "request" or "both")
            {
                writer.WritePropertyName("requestBody");
                writer.WriteStartObject();
                writer.WriteBoolean("required", true);
                WriteContent(writer, root, options, names, JsonMediaType);
                writer.WriteEndObject();
            }

            if (options.Side is "response" or "both")
            {
                writer.WritePropertyName("responses");
                writer.WriteStartObject();
                writer.WritePropertyName(options.Status.ToString(CultureInfo.InvariantCulture));
                writer.WriteStartObject();
                writer.WriteString("description", Inferred);
                WriteContent(writer, root, options, names, JsonMediaType);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject(); // the operation
            writer.WriteEndObject(); // the path item
            writer.WriteEndObject(); // paths

            WriteComponents(writer, components, names);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>A WHOLE document from what the routes said: one `paths` entry per registered method and path, in
    /// registration order (so two calls answer the same bytes), and one `components` block shared by every operation,
    /// which is what makes a shape two routes document reference ONE schema instead of being duplicated.
    ///
    /// The first registration of a method and path wins, which is the route table's own rule: a re-run of an endpoint
    /// file re-adds its routes and the first one answering is the one described.</summary>
    internal static string WriteDocument(IReadOnlyList<OpenApiOperation> operations, OpenApiDocumentOptions options)
    {
        var components = new List<Component>();
        var names = new Dictionary<OpenApiSchemaNode, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        // Path items are grouped because two methods on one path share ONE path item; the order is registration
        // order inside a group too, so a GET registered before a POST is written before it.
        var paths = new List<(string Path, List<OpenApiOperation> Operations)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            var key = operation.Method.ToLowerInvariant() + " " + operation.Path;
            if (!seen.Add(key)) continue;
            var index = paths.FindIndex(p => string.Equals(p.Path, operation.Path, StringComparison.Ordinal));
            if (index < 0) paths.Add((operation.Path, [operation]));
            else paths[index].Operations.Add(operation);
        }

        // EVERY referenced shape is named before anything is written: JSON is sequential, so a $ref cannot be
        // emitted before its target has a name. `extract: false` skips the pre-pass entirely, which is what makes
        // every schema INLINE: a name with no components block to live in would be a $ref to nothing.
        if (options.Extract)
        foreach (var (_, groupOperations) in paths)
        {
            foreach (var operation in groupOperations)
            {
                if (operation.Description is not { } description) continue;
                var side = SideOptions(description);
                // The RESPONSE first, so a schema a route states once (and shares between its halves) takes the
                // route's own name rather than the `Request` one.
                if (description.Response != null) Register(description.Response, description.RootName, side, names, components, used);
                if (description.Request != null) Register(description.Request, description.RootName + "Request", side, names, components, used);
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Interpreter.JsonWriterOpts(options.Pretty)))
        {
            writer.WriteStartObject();
            writer.WriteString("openapi", "3.0.3");
            WriteInfo(writer, options.Title, options.Version, options.Description);

            // An absent `servers` member is the spec's own answer: a client resolves the paths against the URL the
            // document was served from, which is the one address this engine does not have to be told.
            if (!string.IsNullOrEmpty(options.Server))
            {
                writer.WritePropertyName("servers");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("url", options.Server);
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WritePropertyName("paths");
            writer.WriteStartObject();
            foreach (var (path, groupOperations) in paths)
            {
                writer.WritePropertyName(path);
                writer.WriteStartObject();
                foreach (var operation in groupOperations) WriteOperation(writer, operation, names);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();

            WriteComponents(writer, components, names);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The options one route's shapes are written with: `required` and `additionalProperties` come from the
    /// route's own description, which is what makes a component's body say what that route said and not what another
    /// route said.</summary>
    private static OpenApiOptions SideOptions(OpenApiRouteDescription description)
    {
        var options = new OpenApiOptions();
        options.RootName = description.RootName;
        options.MarkRequired = description.MarkRequired;
        options.AdditionalProperties = description.AdditionalProperties;
        return options;
    }

    private static void WriteOperation(Utf8JsonWriter writer, OpenApiOperation operation,
        Dictionary<OpenApiSchemaNode, string> names)
    {
        writer.WritePropertyName(operation.Method.ToLowerInvariant());
        writer.WriteStartObject();

        if (operation.Description is not { } description)
        {
            // THE HONEST ENTRY for a route nobody described: the endpoint exists, its shape is unknown, and the spec
            // requires `responses`, so the default status carries a sentence rather than a guessed schema.
            writer.WritePropertyName("responses");
            writer.WriteStartObject();
            writer.WritePropertyName("200");
            writer.WriteStartObject();
            writer.WriteString("description", NoResponseSample);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        if (!string.IsNullOrEmpty(description.Summary)) writer.WriteString("summary", description.Summary);
        if (!string.IsNullOrEmpty(description.Description)) writer.WriteString("description", description.Description);
        if (description.Tags.Count > 0)
        {
            writer.WritePropertyName("tags");
            writer.WriteStartArray();
            foreach (var tag in description.Tags) writer.WriteStringValue(tag);
            writer.WriteEndArray();
        }

        var side = SideOptions(description);
        if (description.Side is "request" or "both"
            && (description.Request != null || description.RequestContentType != null))
        {
            writer.WritePropertyName("requestBody");
            writer.WriteStartObject();
            writer.WriteBoolean("required", true);
            WriteContent(writer, description.Request, side, names, description.RequestContentType ?? JsonMediaType);
            writer.WriteEndObject();
        }

        // `responses` is REQUIRED by the spec, so a route that documents only its request still answers one entry:
        // the status it answers, with the sentence that its shape is undocumented.
        writer.WritePropertyName("responses");
        writer.WriteStartObject();
        writer.WritePropertyName(description.Status.ToString(CultureInfo.InvariantCulture));
        writer.WriteStartObject();
        writer.WriteString("description", ResponseSentence(description));
        if (description.Side is "response" or "both"
            && (description.Response != null || description.ResponseContentType != null))
            WriteContent(writer, description.Response, side, names, description.ResponseContentType ?? JsonMediaType);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>What the answer's `description` says: the shape's own sentence, or the media type when only that was
    /// stated, or the honest placeholder. A route that states a media type and NO shape gets a sentence naming the
    /// type, because the placeholder alone would read as if the registration had said nothing about its answer.</summary>
    private static string ResponseSentence(OpenApiRouteDescription description) =>
        description.Response != null ? Inferred
        : description.ResponseContentType is { } mediaType
            ? $"answered as {mediaType}, and no sample describes its shape"
            : NoResponseSample;

    private static void WriteInfo(Utf8JsonWriter writer, string title, string version, string? description)
    {
        writer.WritePropertyName("info");
        writer.WriteStartObject();
        writer.WriteString("title", title);
        writer.WriteString("version", version);
        if (!string.IsNullOrEmpty(description)) writer.WriteString("description", description);
        writer.WriteEndObject();
    }

    private static void WriteComponents(Utf8JsonWriter writer, List<Component> components,
        Dictionary<OpenApiSchemaNode, string> names)
    {
        if (components.Count == 0) return;
        writer.WritePropertyName("components");
        writer.WriteStartObject();
        writer.WritePropertyName("schemas");
        writer.WriteStartObject();
        foreach (var component in components)
        {
            writer.WritePropertyName(component.Name);
            // The component's own body is written INLINE, or every one of them would be a $ref to itself.
            WriteSchema(writer, component.Node, component.Options, names, refAllowed: false);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>One side's `content`: the media type the route STATED (`application/json` when it stated none) and the
    /// shape when there is one. A stated media type with NO shape is still published, as an empty media entry, because
    /// it is the one thing a route that answers HTML, an image or plain text can say: an empty entry claims the type
    /// and nothing else, where omitting the block would claim the route answered nothing.</summary>
    private static void WriteContent(Utf8JsonWriter writer, OpenApiSchemaNode? node, OpenApiOptions options,
        Dictionary<OpenApiSchemaNode, string> names, string mediaType)
    {
        writer.WritePropertyName("content");
        writer.WriteStartObject();
        writer.WritePropertyName(mediaType);
        writer.WriteStartObject();
        if (node != null)
        {
            writer.WritePropertyName("schema");
            WriteSchema(writer, node, options, names, refAllowed: true);
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Names one root and every object under it, into the DOCUMENT's shared name table: a document with
    /// several operations has one components block, so a name must be unique across all of them, and a shape two
    /// operations share is named ONCE (which is what makes two routes that document the same shape reference one
    /// component instead of duplicating it).</summary>
    private static void Register(OpenApiSchemaNode root, string hint, OpenApiOptions options,
        Dictionary<OpenApiSchemaNode, string> names, List<Component> components, HashSet<string> used)
    {
        if (names.ContainsKey(root)) return;
        var rootName = Unique(Pascal(hint, "Payload"), used);
        names[root] = rootName;
        components.Add(new Component(rootName, root, options));
        Walk(root, hint, options, names, components, used);
    }

    /// <summary>Names every object the document will reference, in traversal order, before anything is written:
    /// JSON is sequential, so a <c>$ref</c> cannot be emitted before its target has a name.</summary>
    private static void Walk(OpenApiSchemaNode node, string hint, OpenApiOptions options,
        Dictionary<OpenApiSchemaNode, string> names, List<Component> components, HashSet<string> used)
    {
        if (node.Kind == OpenApiSchemaKind.Array)
        {
            var element = ObjectInside(node.Items);
            if (element is null) return;
            if (!names.ContainsKey(element))
            {
                var elementName = Unique(Pascal(hint + "Item", "Schema"), used);
                names[element] = elementName;
                components.Add(new Component(elementName, element, options));
            }

            Walk(element, hint + "Item", options, names, components, used);
            return;
        }

        foreach (var property in node.Properties)
        {
            var target = ObjectInside(property.Node);
            if (target is null) continue;
            if (!names.ContainsKey(target))
            {
                var name = Unique(Pascal(property.Name, "Schema"), used);
                names[target] = name;
                components.Add(new Component(name, target, options));
            }

            Walk(target, property.Name, options, names, components, used);
        }
    }

    /// <summary>The object a value's schema stands for, looking through any number of array levels: an array of
    /// objects is named after its property, and what is named is the ELEMENT.</summary>
    private static OpenApiSchemaNode? ObjectInside(OpenApiSchemaNode? node)
    {
        while (node is { Kind: OpenApiSchemaKind.Array }) node = node.Items;
        return node is { Kind: OpenApiSchemaKind.Object } ? node : null;
    }

    private static void WriteSchema(Utf8JsonWriter writer, OpenApiSchemaNode node, OpenApiOptions options,
        Dictionary<OpenApiSchemaNode, string> names, bool refAllowed)
    {
        if (refAllowed && names.TryGetValue(node, out var reference))
        {
            writer.WriteStartObject();
            if (node.Nullable)
            {
                // A $ref has no siblings in 3.0.x: nullability has to travel through allOf, which is the only
                // shape that both references the schema AND states that null is allowed.
                writer.WritePropertyName("allOf");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("$ref", ComponentsPrefix + reference);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteBoolean("nullable", true);
            }
            else
            {
                writer.WriteString("$ref", ComponentsPrefix + reference);
            }

            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        switch (node.Kind)
        {
            case OpenApiSchemaKind.Boolean:
                writer.WriteString("type", "boolean");
                break;

            case OpenApiSchemaKind.Integer:
                writer.WriteString("type", "integer");
                writer.WriteString("format", node.FitsInt32 ? "int32" : "int64");
                break;

            case OpenApiSchemaKind.Number:
                writer.WriteString("type", "number");
                writer.WriteString("format", "double");
                break;

            case OpenApiSchemaKind.String:
                writer.WriteString("type", "string");
                if (node.Format is not null) writer.WriteString("format", node.Format);
                break;

            case OpenApiSchemaKind.Array:
                writer.WriteString("type", "array");
                writer.WritePropertyName("items");
                if (node.Items is null)
                {
                    writer.WriteStartObject();
                    writer.WriteEndObject();
                }
                else
                {
                    WriteSchema(writer, node.Items, options, names, refAllowed: true);
                }

                break;

            case OpenApiSchemaKind.Object:
                writer.WriteString("type", "object");
                writer.WritePropertyName("properties");
                writer.WriteStartObject();
                foreach (var property in node.Properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteSchema(writer, property.Node, options, names, refAllowed: true);
                }

                writer.WriteEndObject();

                // `required` has two sources and they are UNIONED: what a merged sample implies (only when the route
                // or document asked for that) and what a stated schema SAID. A name one source states is required
                // whatever the other one thinks, because a sample cannot withdraw a statement.
                var required = new List<string>();
                foreach (var property in node.Properties)
                {
                    var implied = options.MarkRequired && node.ObjectSamples > 0 && property.Present == node.ObjectSamples;
                    if (implied || node.ExplicitRequired.Contains(property.Name, StringComparer.Ordinal))
                        required.Add(property.Name);
                }

                if (required.Count > 0)
                {
                    writer.WritePropertyName("required");
                    writer.WriteStartArray();
                    foreach (var name in required) writer.WriteStringValue(name);
                    writer.WriteEndArray();
                }

                // The node's own claim wins over the document's, because it was made about THIS object.
                if ((node.AdditionalProperties ?? options.AdditionalProperties) is { } additional)
                    writer.WriteBoolean("additionalProperties", additional);
                break;
        }

        // Nullable without a type states nothing in 3.0, so an Any value stays {} rather than claiming a sibling.
        if (node.Nullable && node.Kind != OpenApiSchemaKind.Any) writer.WriteBoolean("nullable", true);
        writer.WriteEndObject();
    }

    /// <summary>A component name is made of the spec's own alphabet (<c>a-z A-Z 0-9 . - _</c>), so a property
    /// named <c>clé</c> or <c>first name</c> contributes what it can and never an invalid identifier.</summary>
    private static string Pascal(string name, string fallback)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')
                builder.Append(c);
        }

        if (builder.Length == 0) return fallback;
        if (builder[0] is >= 'a' and <= 'z') builder[0] = (char)(builder[0] - 32);
        return builder.ToString();
    }

    private static string Unique(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = name + suffix.ToString(CultureInfo.InvariantCulture);
            if (used.Add(candidate)) return candidate;
        }
    }
}
