namespace ScriptLangCore;

internal partial class Interpreter
{
    private void RegisterJsonFunctions()
    {
        var jsonNs = new Namespace(_comparer)
        {
            Name = "Json"
        };

        // ── Json.Stringify(value[, pretty]), serialize to JSON string ─────────
        jsonNs.AddMethod("Stringify", args =>
        {
            if (args.Length == 0) return "{}";
            var pretty = args.Length > 1 && args[1] is bool and true;

            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, JsonWriterOpts(pretty));
            WriteJsonValue(writer, args[0]);
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        });

        // ── Json.Serialize(value[, optionsDict]), serialize with options ──────
        jsonNs.AddMethod("Serialize", args =>
        {
            if (args.Length == 0) return "{}";

            var (opts, pretty) = ReadSerializeOptions(args, "Json.Serialize");

            return SerializeToText(args[0], opts, pretty);
        });

        // ── Json.Parse(jsonString), deserialize JSON string ───────────────────
        jsonNs.AddMethod("Parse", args =>
        {
            if (args.Length == 0 || args[0] is not string json)
                throw new ScriptException("Json.Parse expects a JSON string.");

            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            return !reader.Read()
                ? throw new ScriptException("Empty or invalid JSON.")
                : ParseJsonValue(ref reader);
        });

        // ── Json.Validate(jsonString), check if string is valid JSON ──────────
        jsonNs.AddMethod("Validate", args =>
        {
            if (args.Length == 0 || args[0] is not string json)
                return false;
            try
            {
                var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
                return reader.Read();
            }
            catch
            {
                return false;
            }
        });

        // ── Json.ValidateAgainst(schema, value), the contract a schema states ─────────────────────────────────────
        // The pair of `Json.SchemaOf`: that member answers the schema a sample implies, this one answers whether a
        // value MATCHES a schema, so a script can check a payload before storing it or after receiving it, and a
        // route can enforce what its document publishes. A STRING is the JSON TEXT, the reading the whole Json
        // namespace gives a string; any other value IS the value, read the way `Json.Serialize` would write it. The
        // answer is one dictionary: `ok`, the `path` of the first mismatch (`$`, `$.id`, `$.lines[2].qty`) and the
        // `message` naming it, both empty when the value matches.
        jsonNs.AddMethod("ValidateAgainst", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Json.ValidateAgainst takes a schema and a value, as in "
                    + "Json.ValidateAgainst(Json.SchemaOf(sample), payload).");
            var schema = OpenApiSchemaFromScript.Read(args[0], "Json.ValidateAgainst", "the schema", _caseSensitive);
            var payload = OpenApiSampleReader.Read(args[1], "Json.ValidateAgainst");
            var mismatch = OpenApiSchemaValidator.Check(schema, payload, requiredFromSample: false,
                schema.AdditionalProperties);
            var answer = CreateScriptDictionary(OpenApiStringType, OpenApiObjectType);
            answer.AddRaw("ok", mismatch is null);
            answer.AddRaw("path", mismatch?.Path ?? "");
            answer.AddRaw("message", mismatch?.Sentence ?? "");
            return answer;
        });

        // ── Json.SerializeToUtf8Bytes(value[, options]): serialize to UTF-8 bytes ──
        jsonNs.AddMethod("SerializeToUtf8Bytes", args =>
        {
            if (args.Length == 0) return Array.Empty<byte>();
            var (opts, pretty) = ReadSerializeOptions(args, "Json.SerializeToUtf8Bytes");
            return SerializeToUtf8Bytes(args[0], opts, pretty);
        });

        // ── Json.Deserialize([type,] jsonOrBytes), deserialize JSON ───────────
        jsonNs.AddMethod("Deserialize", args =>
        {
            // Overloads:
            //   Deserialize(json)             , universal from string
            //   Deserialize(bytes)            , universal from byte[]
            //   Deserialize(Type, jsonOrBytes), typed from string or byte[]
            //   Deserialize("TypeName", jsonOrBytes), typed by name
            if (args.Length == 0)
                throw new ScriptException("Json.Deserialize expects (json) or (type, json).");

            switch (args.Length)
            {
                // Single argument: Deserialize(jsonOrBytes), universal
                case 1:
                    {
                        var raw = ParseJsonInput(args[0]);
                        // Auto-dispatch: if top-level dict has __type, reconstruct the CLR object. The interpreter's
                        // case flag governs, as it does for every other name comparison.
                        if (raw is ScriptBoxedDictionary topDict && topDict.ContainsKey("__type") && topDict["__type"] is string topType)
                            return DeserializeFromDict(topDict, topType, _caseSensitive);
                        // Recursively walk the result to reconstruct nested __type objects
                        return ReconstructNested(raw, _caseSensitive);
                    }
                // Two arguments: Deserialize(type, jsonOrBytes[, caseSensitive])
                // The DEFAULT follows the interpreter's case flag, like every other name comparison: a JSON key
                // must be spelled the way the script declares the field, unless the interpreter is in `.lax` mode.
                // An explicit third argument still overrides it, `Json.Deserialize(T, json, false)` asks for
                // tolerant key matching, the one place a document's spelling is allowed to differ. (This defaulted
                // to insensitive while the reference documented strict, so the two disagreed; the reference was
                // right about the rule, the code was wrong about the default.)
                case < 2:
                    throw new ScriptException("Json.Deserialize expects (type, json).");
            }

            var deserCaseSensitive = _caseSensitive;
            if (args is [_, _, bool cs, ..])
                deserCaseSensitive = cs;

            var typeName = args[0] switch
            {
                ClassInfo { Fields.Count: 0 } ci => ci.Name,
                ClassInfo ci => ci.Name,
                StructDeclStmt sd => sd.Name,
                StructInstance si => si.StructName,
                ClassInstance ci => ci.Class.Name,
                // A type REFERENCE is spelled WHOLE, not by its head: `List<string?>` carries its name in
                // GenericArgs while TypeName holds only `List`, so reading TypeName alone asked the reader for a
                // bare list. Spelling is the one renderer of a TypeInfo, so the reference form and the string form
                // of the same type cannot disagree, and a nullable argument erases the way C# erases it at runtime
                // (`List<string?>` reads as `List<string>`). It also covers a PRIMITIVE reference, which has no
                // TypeName at all and used to throw here.
                TypeInfo ti => TypeInfo.Spelling(ti),
                string s => s,
                _ => throw new ScriptException("Json.Deserialize first argument must be a type name, class/struct definition, or instance.")
            };

            // args[1] is JSON text (a string or bytes) in the documented overload, but a VALUE is accepted
            // too and is serialized first: Deserialize(Type, instance) is the round-trip cast that
            // Deserialize(Type, Json.Serialize(instance)) spells out, and that idiom relies on it. Without
            // this the instance reached the typed path as though it had already been parsed.
            //
            // A BLANK payload is an EMPTY payload, not a parse failure. `Deserialize(name, "")` used to reach the
            // reader, which threw `JsonReaderException: 'ExpectedJsonTokens LineNumber: 0 …'` at the script: a
            // message that names a CLR type and a byte offset, for an input a caller can act on. Blank text
            // answers null here and DeserializeTyped gives the empty value for the name's shape.
            object? parsed;
            if (args[1] is string blank && string.IsNullOrWhiteSpace(blank) || args[1] is byte[] { Length: 0 })
            {
                parsed = null;
            }
            else
            {
                try
                {
                    parsed = args[1] is string or byte[]? ParseJsonInput(args[1]) : ParseJsonInput(SerializeToText(args[1]));
                }
                catch (Exception ex) when (ex is not ScriptException)
                {
                    // ANY payload the reader rejects is reported through the engine's own map, never the reader's
                    // sentence: a raw `JsonReaderException` is a resource key plus a byte offset, which this
                    // engine does not put in front of a script. The map owns the wording (its own arm composes the
                    // position), so this call adds only the member a script called.
                    throw new ScriptException($"Json.Deserialize: {FriendlyExceptionMessage(ex)}");
                }
            }

            // Enums (ClassInfo with only StaticFields, no instance Fields): return raw dict
            if (args[0] is ClassInfo { Fields.Count: 0, StaticFields.Count: > 0 })
                return parsed;

            // One typed path, shared with Json.Deserialize<T>(json), see DeserializeTyped, which also
            // holds the name-must-agree-with-the-payload rule.
            return DeserializeTyped(parsed, typeName, deserCaseSensitive);
        });

        // ── Json.Deserialize<T>(jsonOrBytes[, caseSensitive]), the same typed path, spelled with a type
        //    argument instead of a name string that can be misspelled. ──
        jsonNs.AddTypedMethod("Deserialize", (args, typeArgs) =>
        {
            if (args.Length == 0)
                throw new ScriptException("Json.Deserialize<T> expects a JSON string or byte array.");
            var genericName = ScriptTypeName(typeArgs[0]);
            var genericParsed = ParseJsonInput(args[0]);
            // Same rule as the named overload: the interpreter's case flag unless the caller overrides it.
            return DeserializeTyped(genericParsed, genericName,
                args.Length > 1 && args[1] is bool cs ? cs : _caseSensitive, typeArgs[0]);
        });

        jsonNs.AddMethod("Format", args =>
        {
            if (args.Length == 0 || args[0] is not string json)
                throw new ScriptException("Json.Format expects a JSON string.");

            var indent = args.Length > 1 && args[1] is long i ? (int)i : 2;
            if (indent < 0) indent = 0;
            if (indent > 8) indent = 8;

            try
            {
                using var doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, JsonWriterOpts(true));
                doc.WriteTo(writer);
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (Exception ex) when (ex is not ScriptException)
            {
                throw new ScriptException($"Json.Format failed: {FriendlyExceptionMessage(ex)}");
            }
        });

        jsonNs.AddMethod("Minify", args =>
        {
            if (args.Length == 0 || args[0] is not string json)
                throw new ScriptException("Json.Minify expects a JSON string.");

            try
            {
                using var doc = JsonDocument.Parse(json);
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, JsonWriterOpts(false));
                doc.WriteTo(writer);
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (Exception ex) when (ex is not ScriptException)
            {
                throw new ScriptException($"Json.Minify failed: {FriendlyExceptionMessage(ex)}");
            }
        });

        // ── Json.ToFile(path, value[, pretty]), write JSON to file ────────────
        jsonNs.AddMethod("ToFile", args =>
        {
            if (args.Length < 2 || args[0] is not string path)
                throw new ScriptException("Json.ToFile expects a file path and a value.");

            var pretty = args.Length > 2 && args[2] is bool and true;
            var fullPath = ResolveJsonPath(path);

            try
            {
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, JsonWriterOpts(pretty));
                WriteJsonValue(writer, args[1]);
                writer.Flush();
                var json = Encoding.UTF8.GetString(stream.ToArray());
                File.WriteAllText(fullPath, json);
                return fullPath;
            }
            catch (Exception ex) when (ex is not ScriptException)
            {
                throw new ScriptException($"Json.ToFile failed: {FriendlyExceptionMessage(ex)}");
            }
        });

        // ── Json.FromFile(path), read and parse JSON file ─────────────────────
        jsonNs.AddMethod("FromFile", args =>
        {
            if (args.Length == 0 || args[0] is not string path)
                throw new ScriptException("Json.FromFile expects a file path.");

            var fullPath = ResolveJsonPath(path);

            try
            {
                // Use FileStream + JsonDocument for chunked/streaming reads
                // Avoids loading the entire file as a string (ReadAllText)
                using var stream = File.OpenRead(fullPath);
                using var doc = JsonDocument.Parse(stream);
                return ParseJsonElement(doc.RootElement);
            }
            catch (Exception ex) when (ex is not ScriptException)
            {
                throw new ScriptException($"Json.FromFile failed: {FriendlyExceptionMessage(ex)}");
            }
        });

        // ── Json.Merge(a, b), deep-merge two dictionaries ────────────────────
        jsonNs.AddMethod("Merge", args =>
        {
            if (args.Length < 2 || args[0] is not IScriptDictionary a || args[1] is not IScriptDictionary b)
                throw new ScriptException("Json.Merge expects two dictionaries.");

            var result = new ScriptBoxedDictionary(
                new TypeInfo(TokenType.String, false, 0, 0, null),
                new TypeInfo(TokenType.Object, true, 0, 0, null));

            // Copy all keys from 'a'
            foreach (var key in a.Keys())
            {
                var keyStr = key.ToString() ?? "";
                result.AddRaw(keyStr, a[key]);
            }

            // Merge keys from 'b' (overwriting)
            foreach (var key in b.Keys())
            {
                var keyStr = key.ToString() ?? "";
                var bVal = b[key];
                if (bVal is IScriptDictionary bDict && a[key] is IScriptDictionary aDict)
                    result.AddRaw(keyStr, JsonMergeRecursive(aDict, bDict));
                else
                    result.AddRaw(keyStr, bVal);
            }

            return result;
        });

        // ── Json.Select(json, "path.to.field"), dot-path access ──────────────
        jsonNs.AddMethod("Select", args =>
        {
            if (args.Length < 2 || args[0] is not IScriptDictionary root || args[1] is not string path)
                throw new ScriptException("Json.Select expects a dictionary and a dot-path string.");

            var parts = path.Split('.');
            object? current = root;

            foreach (var part in parts)
            {
                switch (current)
                {
                    case IScriptDictionary dict when !dict.ContainsKey(part):
                        return null;
                    case IScriptDictionary dict:
                        current = dict[part];
                        break;
                    case IScriptList list:
                        {
                            if (!int.TryParse(part, out var idx) || idx < 0 || idx >= list.Count)
                                return null;
                            current = list[idx];
                            break;
                        }
                    default:
                        return null;
                }
            }

            return current;
        });

        // ── Json.RoundTrip(value), one-call serialize+deserialize round-trip ──
        jsonNs.AddMethod("RoundTrip", args =>
        {
            if (args.Length == 0) return null;
            var opts = new JsonSerializeOptions { ShowType = true };
            if (args.Length > 1 && args[1] is ScriptBoxedDictionary optDict)
            {
                foreach (var key in optDict.Keys())
                {
                    if (key is not string k) continue;
                    var v = optDict[k];
                    switch (k)
                    {
                        case "includeNonPublic":
                            opts = opts with { IncludeNonPublic = IsTruthy(v) };
                            break;
                        case "includeType":
                            opts = opts with { IncludeType = IsTruthy(v) };
                            break;
                        case "showType":
                            opts = opts with { ShowType = IsTruthy(v) };
                            break;
                        case "pretty": /* handled separately below */
                            break;
                    }
                }
            }

            // Serialize
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteJsonValue(writer, args[0], options: opts);
                writer.Flush();
            }
            var json = Encoding.UTF8.GetString(stream.ToArray());

            // Deserialize
            var parsed = ParseJsonElement(JsonDocument.Parse(json).RootElement);
            return ReconstructNested(parsed, _caseSensitive);
        });

        // ── Json.SchemaOf(sample [, options]), the schema a JSON sample implies ──
        // The sample is JSON text, UTF-8 bytes, or a value the engine already holds (Json.Parse's answer, a
        // dictionary literal, a class instance: anything Json.Serialize can write). Both members read the same
        // sample the same way and infer it with one implementation, so a schema a script inspects cannot differ
        // from the one the document embeds.
        jsonNs.AddMethod("SchemaOf", args =>
        {
            if (args.Length == 0)
                throw new ScriptException(
                    "Json.SchemaOf takes the JSON sample as its first argument: the text, the bytes, or a value " +
                    "the engine already holds.");
            var options = OpenApiOptions.Read(args.Length > 1 ? args[1] : null, "Json.SchemaOf", _caseSensitive);
            var sample = OpenApiSampleReader.Read(args[0], "Json.SchemaOf");
            return ToScriptSchema(JsonSchemaInferrer.Infer(sample, options), options.MarkRequired);
        });

        // ── Json.ToOpenApi(sample [, options]), an OpenAPI 3.0.3 document from a JSON sample ──
        jsonNs.AddMethod("ToOpenApi", args =>
        {
            if (args.Length == 0)
                throw new ScriptException(
                    "Json.ToOpenApi takes the JSON sample as its first argument: the text, the bytes, or a value " +
                    "the engine already holds.");
            var options = OpenApiOptions.Read(args.Length > 1 ? args[1] : null, "Json.ToOpenApi", _caseSensitive);
            var sample = OpenApiSampleReader.Read(args[0], "Json.ToOpenApi");
            return OpenApiWriter.Write(JsonSchemaInferrer.Infer(sample, options), options);
        });

        CurrentFrame.Scopes.Define("Json", jsonNs);
    }

    /// <summary>Recursively merge two IScriptDictionary for Json.Merge.</summary>
    private static ScriptBoxedDictionary JsonMergeRecursive(IScriptDictionary a, IScriptDictionary b)
    {
        var result = new ScriptBoxedDictionary(
            new TypeInfo(TokenType.String, false, 0, 0, null),
            new TypeInfo(TokenType.Object, true, 0, 0, null));

        foreach (var key in a.Keys())
        {
            var keyStr = key.ToString() ?? "";
            result.AddRaw(keyStr, a[key]);
        }

        foreach (var key in b.Keys())
        {
            var keyStr = key.ToString() ?? "";
            var bVal = b[key];
            if (bVal is IScriptDictionary bDict && a[key] is IScriptDictionary aDict)
                result.AddRaw(keyStr, JsonMergeRecursive(aDict, bDict));
            else
                result.AddRaw(keyStr, bVal);
        }
        return result;
    }

    /// <summary>Resolve a JSON file path relative to the script base directory.</summary>
    private static string ResolveJsonPath(string path)
    {
        return Path.GetFullPath(path);
    }

    /// <summary>Lazily deserializes elements of a JSON array on demand, yielding elements without materializing the full list.</summary>
    private sealed class ScriptLazyDeserializedList(IScriptList source, string typeName, bool caseSensitive, string declaredName) : IScriptList, IReleaseNothing
    {
        public void Sort(object?[] args, int line, int column) => ScriptListSorting.Sort(this, args, line, column);
        /// <summary>The name the read was ASKED for (`List<int?>`, `string[]`, `IList`), which is what this value
        /// STANDS FOR and therefore what `typeof` and `print` report. It is a different string from the element name
        /// for an array or an interface declaration, which is why both travel: the element name drives the
        /// per-element read, and this one names the value.</summary>
        internal string DeclaredName => declaredName;
        private readonly Dictionary<int, object?> _cache = new();

        public void Dispose() { } // pure managed, IDisposable for uniform script using/is
        public override string ToString() => declaredName;

        public int Count => source.Count;
        // The element's nullable flag is CARRIED, never defaulted: the name this list was built from is what a
        // reader recreated it with, so `string?` must report nullable rather than false. A TypeInfo rebuilt with a
        // hardcoded false is how a nullable element loses that half of its declaration.
        private bool ElementIsNullable => typeName.EndsWith("?", StringComparison.Ordinal);

        public TypeInfo ElementType => new TypeInfo(TokenType.Object, ElementIsNullable, 0, 0,
            typeName.Length > 0 ? typeName : null);

        public object? this[int index]
        {
            get
            {
                if (_cache.TryGetValue(index, out var cached)) return cached;
                var raw = source[index];
                // An EMPTY element name means the payload named no element type (a bare `List`): the parsed value
                // IS the answer, exactly as a top-level `object` read leaves it. Reading it as the container's own
                // name is what threw.
                var deserialized = typeName.Length > 0 ? DeserializeTyped(raw, typeName, caseSensitive) : raw;
                _cache[index] = deserialized;
                return deserialized;
            }
            set { source[index] = value; _cache.Remove(index); }
        }

        public void Add(object? item, int line, int column) => source.Add(item, line, column);

        public void AddRaw(object item) => source.AddRaw(item);

        public void Remove(object? item) { source.Remove(item); _cache.Clear(); }

        public void RemoveAt(int index) { source.RemoveAt(index); _cache.Clear(); }

        public Array ToArray()
        {
            var result = new object?[source.Count];
            for (var i = 0; i < source.Count; i++)
                result[i] = this[i]; // lazy deserialization per element
            return result;
        }

        public void Clear() { source.Clear(); _cache.Clear(); }
    }
}
