namespace ScriptLangCore;

/// <summary>The `Avro` namespace: Avro 1.12.0 schemas, their three encodings and the schema a script type
/// generates. It is a TOP-LEVEL namespace rather than a child of `Json`: a schema is JSON, but the surface is
/// Avro's own, and `Avro.Serialize(...)` says what a reader needs without a breadcrumb to decode first.
///
/// Out of scope, deliberately and by name: Avro RPC/protocols (a script here is not an RPC peer) and
/// `avrogen` code generation (it emits C# source, which a scripting engine has no use for).</summary>
internal partial class Interpreter
{
    private void RegisterAvroNamespace()
    {
        var avroNs = new Namespace(_comparer)
        {
            Name = "Avro"
        };

        // ── Avro.Parse(schema), the schema as normalized .avsc JSON text ──
        avroNs.AddMethod("Parse", args =>
        {
            var schema = AvroSchemaParser.From(args.Length > 0 ? args[0] : null, "Avro.Parse");
            return AvroSchemaWriter.ToJson(schema);
        });

        // ── Avro.Validate(schema), whether the text is a schema this engine accepts ──
        avroNs.AddMethod("Validate", args =>
        {
            try
            {
                _ = AvroSchemaParser.From(args.Length > 0 ? args[0] : null, "Avro.Validate");
                return true;
            }
            catch (ScriptException)
            {
                return false;
            }
        });

        // ── Avro.Canonical(schema), the Parsing Canonical Form ──
        avroNs.AddMethod("Canonical", args =>
        {
            var schema = AvroSchemaParser.From(args.Length > 0 ? args[0] : null, "Avro.Canonical");
            return AvroCanonical.Form(schema);
        });

        // ── Avro.Fingerprint(schema [, algorithm]), default crc-64-avro ──
        avroNs.AddMethod("Fingerprint", args =>
        {
            var schema = AvroSchemaParser.From(args.Length > 0 ? args[0] : null, "Avro.Fingerprint");
            var algorithm = args.Length > 1 && args[1] is not null ? args[1] : "crc-64-avro";
            if (algorithm is not string name)
                throw new ScriptException(
                    $"Avro.Fingerprint: the algorithm is a string (\"crc-64-avro\", \"sha-256\" or " +
                    $"\"md5\"), not '{AvroOptions.FriendlyNameOf(algorithm)}'.");
            return AvroCanonical.Fingerprint(schema, name, "Avro.Fingerprint");
        });

        // ── The two binary doors, stated ONCE, so RoundTrip composes them rather than repeating either ──
        byte[] EncodeValue(object? value, AvroSchema schema, string member)
        {
            var writer = new AvroBinaryWriter();
            AvroValueMap.Write(writer, schema, value, member, _caseSensitive);
            return writer.ToArray();
        }

        object? DecodeValue(byte[] bytes, AvroSchema schema, object? typedAs, string member)
        {
            var reader = new AvroBinaryReader(bytes, member);
            var decoded = AvroValueMap.Read(ref reader, schema, member);

            // TRAILING BYTES ARE REFUSED: a payload longer than the schema consumes means the caller passed the
            // wrong schema (or two values'), and answering the first would hide that silently.
            if (!reader.AtEnd)
                throw new ScriptException(
                    $"{member}: {reader.Remaining} byte(s) are left after the value was read (at offset " +
                    $"{reader.Position} of {bytes.Length}). The schema read less than the payload holds, so it " +
                    "is not the schema this data was written with.");

            // A type argument routes through the engine's ONE typed reconstruction, so a record can come back as
            // the class or struct the caller names, exactly as Json.Deserialize does it.
            return typedAs is not null
                ? AvroValueMap.ImposeType(decoded, AvroValueMap.TypeNameOf(typedAs, member), _caseSensitive)
                : decoded;
        }

        // ── Avro.Serialize(value, schema), the Avro binary encoding ──
        avroNs.AddMethod("Serialize", args => EncodeValue(args.Length > 0 ? args[0] : null,
            AvroSchemaParser.From(args.Length > 1 ? args[1] : null, "Avro.Serialize"), "Avro.Serialize"));

        // ── Avro.Deserialize([type,] data, schema), the Avro binary decoding of ONE value. THE TYPE LEADS,
        //    exactly as `Json.Deserialize(type, payload)` spells it, and the ARITY says whether it is there:
        //    two arguments are (data, schema), three are (type, data, schema). The data can never be mistaken
        //    for a type name (it is bytes), so the leading slot is unambiguous. A type argument makes a record
        //    come back as a script class or struct instead of a dictionary. ──
        avroNs.AddMethod("Deserialize", args =>
        {
            var typed = args.Length > 2;
            var data = args.Length > (typed ? 1 : 0) ? args[typed ? 1 : 0] : null;

            // THE JSON SHAPE IS REFUSED BY NAME: `Avro.Deserialize("Iterable", file)` is Json's spelling, so it
            // arrives here as a type followed by bytes. One value cannot be decoded without its schema, and a
            // container carries its own schema AND many records, so the answer names the door that does what
            // that call was asking for instead of complaining about a string where bytes were expected.
            if (!typed && args.Length == 2 && args[1] is byte[])
                throw new ScriptException(
                    $"Avro.Deserialize: '{AvroOptions.FriendlyNameOf(args[0])}' leads, so this reads as a TYPE " +
                    "followed by bytes. ONE value needs its schema (Avro.Deserialize(type, data, schema)); every " +
                    "record of a container file is Avro.Read(type, data), and " +
                    "Avro.Read(\"Iterable\", data) is the lazy walk over one.");

            var bytes = data switch
            {
                byte[] raw => raw,
                null => throw new ScriptException(
                    "Avro.Deserialize: the data is required (the byte array Avro.Serialize answered)."),
                _ => throw new ScriptException(
                    $"Avro.Deserialize: the data is a byte array, not '{AvroOptions.FriendlyNameOf(data)}'. " +
                    "Avro's binary encoding is bytes; use Avro.DeserializeJson for the JSON encoding.")
            };
            var schema = AvroSchemaParser.From(args.Length > (typed ? 2 : 1) ? args[typed ? 2 : 1] : null,
                "Avro.Deserialize");
            return DecodeValue(bytes, schema, typed ? args[0] : null, "Avro.Deserialize");
        });

        // ── Avro.Deserialize<T>(data, schema), the SAME typed door spelled with a type argument, which is how
        //    Json.Deserialize<T> spells it: the shape a reader gets is written at the call, and a name that does
        //    not exist fails where it is written rather than as a string a script can mistype. ──
        avroNs.AddTypedMethod("Deserialize", (args, typeArgs) =>
        {
            var schema = AvroSchemaParser.From(args.Length > 1 ? args[1] : null, "Avro.Deserialize<T>");
            var data = args.Length > 0 ? args[0] : null;
            var bytes = data switch
            {
                byte[] raw => raw,
                null => throw new ScriptException(
                    "Avro.Deserialize<T>: the data is required (the byte array Avro.Serialize answered)."),
                _ => throw new ScriptException(
                    $"Avro.Deserialize<T>: the data is a byte array, not '{AvroOptions.FriendlyNameOf(data)}'. " +
                    "Avro's binary encoding is bytes; use Avro.DeserializeJson for the JSON encoding.")
            };
            return DecodeValue(bytes, schema, typeArgs[0], "Avro.Deserialize<T>");
        });

        // ── Avro.RoundTrip([type,] value, schema), what the engine reads back from a value it wrote. It is
        //    the COMPOSITION of the two doors above, never a third implementation: a value that does not fit the
        //    schema is refused by Encode's own rule, and a type argument is Decode's own typed door. The answer is
        //    therefore the schema's reading of the value (a whole number reads back whole, a class instance reads
        //    back as its dictionary, a logical type as its script type), which is what a compatibility check wants
        //    and what saves a caller from writing Encode(Decode(...)) in the right order. THE TYPE LEADS here too,
        //    so the third argument is always the type and the arity is what says it is present: `Avro.RoundTrip(
        //    "hi", '"string"')` is a string VALUE, `Avro.RoundTrip(Row, {...}, schema)` is a typed read. ──
        avroNs.AddMethod("RoundTrip", args =>
        {
            var typed = args.Length > 2;
            var value = args.Length > (typed ? 1 : 0) ? args[typed ? 1 : 0] : null;
            var schema = AvroSchemaParser.From(args.Length > (typed ? 2 : 1) ? args[typed ? 2 : 1] : null,
                "Avro.RoundTrip");
            var bytes = EncodeValue(value, schema, "Avro.RoundTrip");
            return DecodeValue(bytes, schema, typed ? args[0] : null, "Avro.RoundTrip");
        });

        // ── Avro.SerializeJson(value, schema), the Avro JSON encoding as text. Its unions are the one place it
        //    differs from the field-default form: a bare null for the null branch, else {"typeName": value}. ──
        avroNs.AddMethod("SerializeJson", args =>
        {
            var schema = AvroSchemaParser.From(args.Length > 1 ? args[1] : null, "Avro.SerializeJson");
            return AvroJsonCodec.ToText(schema, args.Length > 0 ? args[0] : null, "Avro.SerializeJson",
                _caseSensitive);
        });

        // ── Avro.DeserializeJson([type,] json, schema), reading the Avro JSON encoding back. The type leads,
        //    exactly as `Avro.Deserialize` spells it. ──
        avroNs.AddMethod("DeserializeJson", args =>
        {
            var typed = args.Length > 2;
            var json = args.Length > (typed ? 1 : 0) ? args[typed ? 1 : 0] : null;
            var schema = AvroSchemaParser.From(args.Length > (typed ? 2 : 1) ? args[typed ? 2 : 1] : null,
                "Avro.DeserializeJson");
            if (json is not (string or byte[]))
                throw new ScriptException(
                    $"Avro.DeserializeJson: the payload is JSON text (a string or a byte array), not " +
                    $"'{AvroOptions.FriendlyNameOf(json)}'. Use Avro.Deserialize for the binary encoding.");
            var decoded = AvroJsonCodec.FromValue(json, schema, "Avro.DeserializeJson", _caseSensitive);
            return typed && args[0] is not null
                ? AvroValueMap.ImposeType(decoded, AvroValueMap.TypeNameOf(args[0], "Avro.DeserializeJson"),
                    _caseSensitive)
                : decoded;
        });

        // ── Avro.Schema(type [, options]), the .avsc text GENERATED from a script type: a class or struct
        //    becomes a record, an enum becomes an enum, and a `T?` becomes ["null", T] with a null default. ──
        avroNs.AddMethod("Schema", args =>
        {
            var options = AvroOptions.Read(args.Length > 1 ? args[1] : null, "Avro.Schema");
            var schema = AvroSchemaGenerator.From(args.Length > 0 ? args[0] : null, options, "Avro.Schema");
            return AvroSchemaWriter.ToJson(schema);
        });

        // ── Avro.Write(records, schema [, options]), a whole Object Container File as bytes. This is
        //    where a CODEC applies: Encode writes one value's bytes, this writes a stream of them and says
        //    which compression the blocks use. ──
        // ── Avro.OpenReader(source [, type]), ONE RECORD AT A TIME: the door for a container too big to hold. The
        //    source is a file path, a container's bytes, or the HttpContext of a request whose body is that container
        //    (which is a stream that cannot seek, read block by block), and the optional type imposes that class on
        //    each record as it is decoded, exactly as Read's leading type does. The value is an AvroReader:
        //    HasNext(), ReadNext(), Schema, Codec, Count, Blocks, Position, Close(). It is forward-only, so `foreach`
        //    over it consumes it, and it is registered as a declared resource so a forgotten one is closed when the
        //    run ends. ──
        avroNs.AddMethod("OpenReader", args =>
        {
            var source = args.Length > 0 ? args[0] : null;
            var typeName = args.Length > 1 && args[1] is not null
                ? AvroValueMap.TypeNameOf(args[1], "Avro.OpenReader")
                : null;
            return OpenAvroReader(source, typeName, "Avro.OpenReader");
        });

        AvroReader OpenAvroReader(object? source, string? typeName, string member)
        {
            var self = Current ?? this;
            Stream stream;
            // Whether the reader disposes this stream, and whether it drains it on the way out. A body the PROGRAM set
            // is a MemoryStream this call made, so the reader owns it; the request's own stream belongs to the server,
            // so the reader owns only the buffer it puts over it and must drain what it did not read.
            var ownsStream = true;
            var drainOnClose = false;
            var abort = default(CancellationToken);
            switch (source)
            {
                case HttpContext ctx:
                {
                    var take = ctx.TakeRequestBodyStream(member);
                    stream = take.Stream;
                    ownsStream = take.OwnsStream;
                    drainOnClose = take.DrainOnClose;
                    abort = ctx.RequestAborted;
                    break;
                }
                case byte[] bytes:
                    stream = new MemoryStream(bytes, writable: false);
                    break;
                case string path when path.Length > 0:
                {
                    var resolved = self.ResolvePath(path);
                    if (!File.Exists(resolved))
                        throw new ScriptException($"{member}: file not found: '{resolved}'.");
                    try
                    {
                        stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new ScriptException($"{member}: cannot read '{resolved}': {FriendlyExceptionMessage(ex)}");
                    }
                    break;
                }
                default:
                    throw new ScriptException(
                        $"{member}: the source is a file path, the bytes of a container (the array " +
                        "Avro.Write or File.ReadAllBytes answers), or the HttpContext a handler was given " +
                        $"(so a posted body streams), not '{AvroOptions.FriendlyNameOf(source)}'.");
            }

            // A container this engine cannot read is refused by the constructor, and the stream it was handed must
            // not outlive that refusal: the file handle is what a suite's cleanup would then fail to delete. The
            // request's stream is the server's, so it is DRAINED instead, which is what lets the connection be reused.
            try
            {
                var reader = new AvroReader(stream, ownsStream, typeName, _caseSensitive, member, abort, drainOnClose);
                TrackDeclaredResource(reader);
                return reader;
            }
            catch
            {
                if (ownsStream) stream.Dispose();
                else if (drainOnClose) AvroReader.DrainRemainder(stream, abort);
                throw;
            }
        }

        // ── Avro.WriteStream(records, target, schema [, options]), a container written INCREMENTALLY: the header once,
        //    then one block per `block` records as the source hands them over. The target is a file path (created or
        //    replaced) or an open FileByteWriter, which stays open, so a caller can put a container into a handle it
        //    already has. It answers the number of records written. The records are the engine's own stream source
        //    kinds, so a list, a generator, an Events feed or an async sequence all work. ──
        avroNs.AddMethod("WriteStream", args =>
        {
            var records = args.Length > 0 ? args[0] : null;
            var target = args.Length > 1 ? args[1] : null;
            var schema = AvroSchemaParser.From(args.Length > 2 ? args[2] : null, "Avro.WriteStream");
            var options = AvroStreamOptions.Read(args.Length > 3 ? args[3] : null, "Avro.WriteStream");
            var self = Current ?? this;
            var source = StreamSourceFor(records, "Avro.WriteStream");
            try
            {
                switch (target)
                {
                    case string path when path.Length > 0:
                    {
                        var resolved = self.ResolvePath(path);
                        try
                        {
                            using var file = new FileStream(resolved, FileMode.Create, FileAccess.Write,
                                FileShare.Read, 64 * 1024);
                            return AvroStreamWriter.Write(file, source, schema, options, _caseSensitive,
                                "Avro.WriteStream");
                        }
                        // A RECORD that does not fit the schema is a ScriptException and must keep its own message, so
                        // only the file system's failures are converted here.
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            throw new ScriptException(
                                $"Avro.WriteStream: cannot write '{resolved}': {FriendlyExceptionMessage(ex)}");
                        }
                    }
                    case FileByteWriter writer:
                    {
                        var stream = writer.Stream
                            ?? throw new ScriptException(
                                "Avro.WriteStream: this byte writer was closed, so nothing more can be written " +
                                "through it.");
                        return AvroStreamWriter.Write(stream, source, schema, options, _caseSensitive,
                            "Avro.WriteStream");
                    }
                    default:
                        throw new ScriptException(
                            "Avro.WriteStream: the target is a file path or an open FileByteWriter " +
                            $"(File.OpenWriteBytes), not '{AvroOptions.FriendlyNameOf(target)}'.");
                }
            }
            finally
            {
                source.Dispose();
            }
        });

        avroNs.AddMethod("Write", args =>
        {
            var options = AvroOptions.Read(args.Length > 2 ? args[2] : null, "Avro.Write");
            var schema = AvroSchemaParser.From(args.Length > 1 ? args[1] : null, "Avro.Write");
            var records = args.Length > 0 ? args[0] : null;
            if (records is not System.Collections.IEnumerable rows)
                throw new ScriptException(
                    "Avro.Write: the records are a list or an array of values, not " +
                    $"'{AvroOptions.FriendlyNameOf(records)}'. A container holds a sequence of objects, so use " +
                    "Avro.Serialize for one value's binary encoding.");
            return AvroContainer.Write(rows, schema, options.Codec, _caseSensitive, "Avro.Write");
        });

        // ── The doors that take a container's BYTES read them the same way, and one helper states it ──
        byte[] ContainerBytes(object? data, string member) => data switch
        {
            byte[] raw => raw,
            // A READER is a stream over a container whose header it has already parsed, so it answers no bytes. The
            // refusal therefore says what the reader DOES have instead of asking for a copy of the file, and
            // ContainerSchema names its exact twin there: `Schema` is that same .avsc text, read once.
            AvroReader => throw new ScriptException(member == "Avro.ContainerSchema"
                ? $"{member}: this is an AvroReader, which already carries the file's schema: use reader.Schema, " +
                  "which is that same .avsc text with no second read."
                : $"{member}: this is an AvroReader, which is a stream over the container rather than its bytes: " +
                  "pass the byte array (reader.Schema answers the schema a reader carries)."),
            null => throw new ScriptException(
                $"{member}: the data is required (the byte array Avro.Write answered)."),
            _ => throw new ScriptException(
                $"{member}: the data is a byte array, not '{AvroOptions.FriendlyNameOf(data)}'.")
        };

        // The one option `Json` takes: a bare boolean, or an options value carrying `pretty`, because the
        // answer is TEXT destined for a human or for a JSON parser that does not care, and pretty is the only choice
        // that changes. The SHAPES an options value can have are `AvroOptions.Pairs`, so a `{ }` literal (a CLR
        // dictionary) and a JSON text work exactly as they do for every other Avro member.
        static bool JsonOption(object? options, string member)
        {
            if (options is null) return false;
            if (options is bool flag) return flag;
            var pretty = false;
            foreach (var (key, item) in AvroOptions.Pairs(options, member, "for example { \"pretty\": true }"))
            {
                if (key != "pretty")
                    throw new ScriptException(
                        $"{member}: unknown option \"{key}\". The only option is pretty.");
                pretty = item is bool on
                    ? on
                    : throw new ScriptException(
                        $"{member}: \"pretty\" is a boolean, not '{AvroOptions.FriendlyNameOf(item)}'.");
            }
            return pretty;
        }

        // ── Avro.Read([type,] data), every object of a container, in order. The schema comes from
        //    the file's own avro.schema, so a reader needs nothing but the bytes. THE TYPE LEADS, as above, and
        //    a type that NAMES THE SEQUENCE (Iterable/IEnumerable) asks for the LAZY read: the header is parsed
        //    once and the block boundaries are recorded without decompressing anything, so a record is decoded
        //    only when it is read. Any other type is a record type, and every record is imposed on it eagerly. ──
        avroNs.AddMethod("Read", args =>
        {
            var typeArg = args.Length > 1 ? args[0] : null;
            var bytes = ContainerBytes(args.Length > 1 ? args[1] : (args.Length > 0 ? args[0] : null),
                "Avro.Read");
            if (typeArg is null) return AvroContainer.Read(bytes, null, _caseSensitive, "Avro.Read");
            var typeName = AvroValueMap.TypeNameOf(typeArg, "Avro.Read");
            return AvroLazyContainer.NamesLazyRead(typeName, out var declared, out var elementName)
                ? AvroLazyContainer.Read(bytes, declared, elementName, _caseSensitive, "Avro.Read")
                : AvroContainer.Read(bytes, typeName, _caseSensitive, "Avro.Read");
        });

        // ── The two doors that answer a STORED file's own description, which is what a reader of a BLOB needs and
        //    what the two SQL functions (`slc_avro_schema`, `slc_avro_json`) answer from the same core: the schema
        //    text the container carries, and every record rendered as PURE JSON, which SQLite's own json_extract,
        //    json_each and json_group_array can then read. The codec is the file's, so it is DECOMPRESSED here and an
        //    unsupported one is refused by name rather than handed on as garbage. ──
        avroNs.AddMethod("ContainerSchema", args =>
            AvroContainer.SchemaText(ContainerBytes(args.Length > 0 ? args[0] : null, "Avro.ContainerSchema"),
                "Avro.ContainerSchema"));
        avroNs.AddMethod("Json", args =>
        {
            var pretty = JsonOption(args.Length > 1 ? args[1] : null, "Avro.Json");
            return AvroContainer.ToJson(ContainerBytes(args.Length > 0 ? args[0] : null, "Avro.Json"),
                "Avro.Json", pretty);
        });

        // ── THE THREE EDITS, and the format decides which is which. INSERT is the only edit that happens IN
        //    PLACE: a new block after the last one, ended with the FILE'S OWN sync marker, so nothing already
        //    written is decoded or rewritten. UPDATE and DELETE are REWRITES: a record is variable-length inside
        //    a compressed block with no index, so one cannot be replaced or removed where it sits. Both keep the
        //    file's own HEADER byte for byte, which is the fact a script cannot get for itself (Read
        //    answers records, never the schema or the codec), so a rewrite here needs no schema restated. ──
        avroNs.AddMethod("Append", args =>
        {
            var bytes = ContainerBytes(args.Length > 0 ? args[0] : null, "Avro.Append");
            var appended = args.Length > 1 ? args[1] : null;
            if (appended is not System.Collections.IEnumerable rows)
                throw new ScriptException(
                    "Avro.Append: the records are a list or an array of values, not " +
                    $"'{AvroOptions.FriendlyNameOf(appended)}'. An appended block holds a sequence, so pass " +
                    "[record] for one record, or use Avro.Write to write a whole file.");
            return AvroContainer.Append(bytes, rows, _caseSensitive, "Avro.Append");
        });

        avroNs.AddMethod("Update", args =>
        {
            var typed = args.Length > 3;
            var bytes = ContainerBytes(args[typed ? 1 : 0], "Avro.Update");
            var predicate = args[typed ? 2 : 1];
            var transform = args[typed ? 3 : 2];
            if (!IsScriptCallable(predicate))
                throw new ScriptException(
                    "Avro.Update: the predicate is the function that decides which records change, " +
                    $"not '{AvroOptions.FriendlyNameOf(predicate)}'.");
            if (!IsScriptCallable(transform))
                throw new ScriptException(
                    "Avro.Update: the transform is the function that answers the new record, not " +
                    $"'{AvroOptions.FriendlyNameOf(transform)}'.");
            var self = Current ?? this;
            var records = AvroContainer.Read(bytes, typed ? args[0] : null, _caseSensitive,
                "Avro.Update");
            var kept = new List<object?>(records.Count);
            foreach (var record in records)
                kept.Add(IsTruthy(self.InvokeScriptCallback(predicate, [record]))
                    ? self.InvokeScriptCallback(transform, [record])
                    : record);
            return AvroContainer.Rewrite(bytes, kept, _caseSensitive, "Avro.Update");
        });

        avroNs.AddMethod("Delete", args =>
        {
            var typed = args.Length > 2;
            var bytes = ContainerBytes(args[typed ? 1 : 0], "Avro.Delete");
            var predicate = args[typed ? 2 : 1];
            if (!IsScriptCallable(predicate))
                throw new ScriptException(
                    "Avro.Delete: the predicate is the function that decides which records are removed, " +
                    $"not '{AvroOptions.FriendlyNameOf(predicate)}'.");
            var self = Current ?? this;
            var records = AvroContainer.Read(bytes, typed ? args[0] : null, _caseSensitive,
                "Avro.Delete");
            var kept = new List<object?>(records.Count);
            foreach (var record in records)
                if (!IsTruthy(self.InvokeScriptCallback(predicate, [record]))) kept.Add(record);
            return AvroContainer.Rewrite(bytes, kept, _caseSensitive, "Avro.Delete");
        });

        CurrentFrame.Scopes.Define("Avro", avroNs);
    }
}

