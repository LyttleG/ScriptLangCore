using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ScriptLangCore;

/// <summary>The Avro Object Container File (spec 1.12, "Object Container Files"): the magic, a metadata map, a
/// 16-byte sync marker, then blocks of `count` objects followed by that same marker. It is the shape a `.avro`
/// file has, and the only place a CODEC applies: `Encode` writes one value's bytes, this writes a stream of them.
///
/// The codecs are the spec's own four, all written from the runtime with no dependency outside it:
///   * `null`       the block payload is the concatenation of its objects' encodings;
///   * `deflate`    the payload is a raw DEFLATE stream (RFC 1951, NO zlib header), which System.IO.Compression's
///                  DeflateStream produces and consumes;
///   * `snappy`     the payload is a Snappy block followed by the CRC-32 (big-endian) of the UNCOMPRESSED data, which
///                  is the spec's own framing (AvroSnappy implements both halves);
///   * `zstandard`  the payload is a standard Zstandard frame, written and read by the runtime's own
///                  `ZstandardStream` (System.IO.Compression, in this runtime since .NET 11), which is the frame
///                  google/zstd writes, so a container crosses with any Avro reader that has zstd.
/// bzip2 and xz are refused by name rather than written in an encoding no reader here could open.
///
/// Reading trusts NOTHING it can name: a bad magic, a missing `avro.schema`, a sync marker that does not match,
/// a block whose size overruns the blob, and a payload the codec cannot decode are each refused with the fact an
/// author can act on.</summary>
internal static class AvroContainer
{
    private static ReadOnlySpan<byte> Magic => [0x4F, 0x62, 0x6A, 0x01]; // "Obj" + 0x01, verbatim from the spec

    internal const int SyncSize = 16;

    /// <summary>Writes one container. `records` is a script list/array of values; every one is encoded with the
    /// SAME schema, which is what a container says once in its header.</summary>
    internal static byte[] Write(System.Collections.IEnumerable records, AvroSchema schema, string codec,
        bool caseSensitive, string member)
    {
        if (codec is not ("null" or "deflate" or "snappy" or "zstandard" or "gzip" or "brotli"))
            throw new ScriptException(
                $"{member}: codec '{codec}' is not supported. This engine writes \"null\", \"deflate\", " +
                "\"snappy\", \"zstandard\", \"gzip\" and \"brotli\"; bzip2 and xz are refused by name " +
                "rather than written unreadable.");

        var sync = new byte[SyncSize];
        RandomNumberGenerator.Fill(sync);

        var writer = new AvroBinaryWriter();
        writer.WriteRaw(Magic.ToArray());
        var meta = new AvroBinaryWriter();
        meta.WriteLong(2);
        WriteMeta(meta, "avro.schema", Encoding.UTF8.GetBytes(AvroSchemaWriter.ToJson(schema)));
        WriteMeta(meta, "avro.codec", Encoding.UTF8.GetBytes(codec));
        meta.WriteLong(0);
        writer.WriteRaw(meta.ToArray());
        writer.WriteRaw(sync);
        writer.WriteRaw(BuildBlock(records, schema, codec, sync, caseSensitive, member));
        return writer.ToArray();
    }

    /// <summary>Appends ONE block to a container, which is the only edit this format allows IN PLACE: records are
    /// read back in block order, so a new record goes after the last one, and nothing already written is decoded,
    /// decompressed or re-encoded. The block is ended by the FILE'S OWN sync marker, never a fresh one, because a
    /// reader compares each block's trailer against the header's: an append with a new marker would make the file
    /// refuse itself. Appending nothing answers the bytes unchanged rather than writing an empty block.</summary>
    internal static byte[] Append(byte[] data, System.Collections.IEnumerable records, bool caseSensitive,
        string member)
    {
        var header = Scan(data, member);
        var block = BuildBlock(records, header.Schema, header.Codec, header.Sync, caseSensitive, member);
        if (block.Length == 0) return data;
        var result = new byte[data.Length + block.Length];
        Buffer.BlockCopy(data, 0, result, 0, data.Length);
        Buffer.BlockCopy(block, 0, result, data.Length, block.Length);
        return result;
    }

    /// <summary>Rewrites the RECORDS of a container and keeps its header BYTE FOR BYTE: the magic, the metadata
    /// map (`avro.schema`, `avro.codec`, and anything else the file carries) and the sync marker are copied
    /// verbatim, and only the blocks are encoded again. That is how an update or a delete is expressed here: a
    /// record is variable-length inside a compressed block with no index, so one cannot be replaced or removed
    /// where it sits, and the honest answer is ONE new block (this writer's own shape) holding the records the
    /// caller kept. An empty answer is the header alone, which is a container with no records.</summary>
    internal static byte[] Rewrite(byte[] data, System.Collections.IEnumerable records, bool caseSensitive,
        string member)
    {
        var header = Scan(data, member);
        var block = BuildBlock(records, header.Schema, header.Codec, header.Sync, caseSensitive, member);
        var result = new byte[header.FirstBlockOffset + block.Length];
        Buffer.BlockCopy(data, 0, result, 0, header.FirstBlockOffset);
        Buffer.BlockCopy(block, 0, result, header.FirstBlockOffset, block.Length);
        return result;
    }

    /// <summary>One block's framing: the object count, the compressed size, the payload, the sync marker.
    /// EMPTY for no records, and what that means is each caller's decision (a fresh container has no block, an
    /// append adds nothing, a rewrite writes the header alone).</summary>
    private static byte[] BuildBlock(System.Collections.IEnumerable records, AvroSchema schema, string codec,
        byte[] sync, bool caseSensitive, string member)
    {
        var payload = new AvroBinaryWriter();
        var count = 0L;
        foreach (var record in records)
        {
            AvroValueMap.Write(payload, schema, record, member, caseSensitive);
            count++;
        }
        if (count == 0) return [];
        var compressed = AvroCodecs.Compress(codec, payload.ToArray(), member);
        var block = new AvroBinaryWriter();
        block.WriteLong(count);
        block.WriteLong(compressed.Length);
        block.WriteRaw(compressed);
        block.WriteRaw(sync);
        return block.ToArray();
    }

    /// <summary>Where one block's COMPRESSED payload sits, and how many objects it declares. That is all a
    /// caller needs to decode the block, and it is what lets a read be LAZY: the boundaries are found without
    /// decompressing anything, and a record is decoded only when somebody reads it.</summary>
    internal readonly record struct BlockSpan(int Offset, int Length, long Count);

    /// <summary>Everything the header STATES, which is everything an edit of the file needs: the parsed schema,
    /// the codec, the file's OWN sync marker (an appended block must end with it, or every reader refuses the
    /// whole file as corrupt), where the first block starts (so a rewrite keeps the header's bytes VERBATIM
    /// rather than re-serializing a schema it only parsed), and where each block sits.</summary>
    internal readonly record struct Header(AvroSchema Schema, string Codec, byte[] Sync, int FirstBlockOffset,
        List<BlockSpan> Blocks, string SchemaJson);

    /// <summary>The header's ONE parse and the block BOUNDARIES, without decoding a single record. The eager
    /// read, the lazy one and the three edits all ask this, so a file cannot be accepted by one and refused by
    /// another, and the header's refusals (magic, metadata, sync) live in one place.</summary>
    internal static Header Scan(byte[] data, string member)
    {
        var reader = new AvroBinaryReader(data, member);
        if (reader.Remaining < 4 || !Magic.SequenceEqual(reader.ReadFixed(4, member)))
            throw new ScriptException(
                $"{member}: this is not an Avro container file (it does not start with the magic 'Obj' + 1). " +
                "Use Avro.Deserialize for one value's binary encoding.");

        var meta = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var count = reader.ReadLong(member);
        if (count < 0)
            throw new ScriptException($"{member}: the header's metadata map declares {count} entries.");
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString(member);
            meta[key] = reader.ReadBytes(member);
        }
        var tail = reader.ReadLong(member);
        if (tail != 0)
            throw new ScriptException(
                $"{member}: the header's metadata map does not end with a 0 block count (it declares {tail} " +
                "more entries), so the file is truncated or not a container.");

        if (!meta.TryGetValue("avro.schema", out var schemaBytes))
            throw new ScriptException(
                $"{member}: the container carries no \"avro.schema\" entry, so its values cannot be decoded.");
        var codec = meta.TryGetValue("avro.codec", out var codecBytes)
            ? Encoding.UTF8.GetString(codecBytes)
            : "null";
        var schemaJson = Encoding.UTF8.GetString(schemaBytes);
        var schema = AvroSchemaParser.Parse(schemaJson, member);
        var sync = reader.ReadFixed(SyncSize, member);
        var firstBlock = reader.Position;
        var blocks = new List<BlockSpan>();
        while (reader.Remaining > 0)
        {
            var objects = reader.ReadLong(member);
            var size = reader.ReadLong(member);
            if (objects < 0)
                throw new ScriptException(
                    $"{member}: a block declares {objects} object(s), so the file is truncated or corrupt.");
            if (size < 0 || size > reader.Remaining)
                throw new ScriptException(
                    $"{member}: a block declares {size} byte(s) while {Math.Max(0, reader.Remaining)} remain, so " +
                    "the file is truncated or corrupt.");
            var offset = reader.Position;
            _ = reader.ReadFixed((int)size, member);
            if (!sync.AsSpan().SequenceEqual(reader.ReadFixed(SyncSize, member)))
                throw new ScriptException(
                    $"{member}: a block does not end with the file's sync marker, so the file is corrupt or " +
                    "the reader is out of step.");
            blocks.Add(new BlockSpan(offset, (int)size, objects));
        }
        return new Header(schema, codec, sync, firstBlock, blocks, schemaJson);
    }

    /// <summary>The schema TEXT the container carries in its own header (`avro.schema`), which is the `.avsc` a
    /// stored file can hand back without a separate copy. It is the file's own spelling, not a re-serialization, so a
    /// compare against what was written is exact.
    ///
    /// This and <see cref="ToJson"/> are the two halves a reader of a STORED file needs, and they are also the two
    /// SQL functions this engine exposes (`slc_avro_schema`, `slc_avro_json`), so a script and a query answer the same
    /// question from the same code.</summary>
    internal static string SchemaText(byte[] data, string member) => Scan(data, member).SchemaJson;

    /// <summary>The container's records as PURE JSON text: a JSON ARRAY, one element per record, decoded with the
    /// schema the file carries and DECOMPRESSED with the codec it declares. "Pure" is the point rather than a
    /// detail: this is what SQLite's own `json_extract`, `json_each` and `json_group_array` can read, so the bytes
    /// of a container never have to leave SQL's hands to be queried.
    ///
    /// The codec is the file's own, so an unsupported one is refused BY NAME here (the same message
    /// `Avro.ReadContainer` gives) rather than producing JSON from a payload nobody decompressed. A `bytes` or `fixed`
    /// field renders as a JSON array of numbers, which is what this engine's serializer does with a `byte[]`; a union
    /// renders as its chosen branch's value, because that is what the value IS once decoded.
    ///
    /// EAGER on purpose: the answer is one string, so a caller that wants to walk records without materializing them
    /// has `Avro.ReadContainer("Iterable", data)`, and SQL has a JSON value rather than a cursor.</summary>
    internal static string ToJson(byte[] data, string member, bool pretty = false)
        => Interpreter.SerializeToText(Read(data, null, true, member), pretty: pretty);

    /// <summary>Reads every object of a container, in order. The schema comes from the file's own
    /// `avro.schema`, so a reader needs nothing but the bytes. `Avro.ReadContainer("Iterable", data)` is the
    /// LAZY door onto the same file (see <see cref="AvroLazyContainer"/>): this one decodes every record before
    /// it answers, which is why a caller that only walks the records once can let the reader do it.</summary>
    internal static List<object?> Read(byte[] data, object? typedAs, bool caseSensitive, string member)
    {
        var header = Scan(data, member);
        var schema = header.Schema;
        var values = new List<object?>();
        foreach (var block in header.Blocks)
        {
            var decoded = AvroCodecs.Decompress(header.Codec, data.AsSpan(block.Offset, block.Length).ToArray(),
                member);
            var blockReader = new AvroBinaryReader(decoded, member);
            for (long i = 0; i < block.Count; i++)
                values.Add(AvroValueMap.Read(ref blockReader, schema, member));
            if (!blockReader.AtEnd)
                throw new ScriptException(
                    $"{member}: a block declares {block.Count} object(s) and {blockReader.Remaining} byte(s) of its " +
                    "payload are left, so the file's schema and its blocks disagree.");
        }

        if (typedAs is null) return values;
        var typeName = AvroValueMap.TypeNameOf(typedAs, member);
        var typed = new List<object?>(values.Count);
        foreach (var value in values) typed.Add(AvroValueMap.ImposeType(value, typeName, caseSensitive));
        return typed;
    }

    private static void WriteMeta(AvroBinaryWriter writer, string key, byte[] value)
    {
        writer.WriteString(key);
        writer.WriteLong(value.Length);
        writer.WriteRaw(value);
    }
}

/// <summary>The spec's codec framing over the binary encoding: `null` passes the payload through, `deflate` is a
/// raw DEFLATE stream, `snappy` is a Snappy block plus the CRC-32 of the uncompressed data, big-endian, and
/// `zstandard` is a standard Zstandard frame.</summary>
internal static class AvroCodecs
{
    internal static byte[] Compress(string codec, byte[] payload, string member) => codec switch
    {
        "null" => payload,
        "deflate" => Deflate(payload),
        "snappy" => AvroSnappy.Compress(payload, member),
        "zstandard" => Zstandard(payload),
        // GZip and Brotli are NOT Avro spec codecs: AvroConvert defines them as its own extensions, and its
        // own enum says so ("possibly not supported outside AvroConvert"). They are written here under the
        // names that library writes, so a container crosses between the two, and the docs say what that means.
        "gzip" => GZip(payload),
        "brotli" => Brotli(payload),
        _ => throw new ScriptException(
            $"{member}: codec '{codec}' is not supported. This engine writes \"null\", \"deflate\", " +
            "\"snappy\", \"zstandard\", \"gzip\" and \"brotli\"; bzip2 and xz are refused by name rather " +
            "than written unreadable.")
    };

    internal static byte[] Decompress(string codec, byte[] payload, string member) => codec switch
    {
        "null" => payload,
        "deflate" => Inflate(payload, member),
        "snappy" => AvroSnappy.Decompress(payload, member),
        "zstandard" => ExpandZstandard(payload, member),
        "gzip" => ExpandGZip(payload, member),
        "brotli" => ExpandBrotli(payload, member),
        _ => throw new ScriptException(
            $"{member}: the container's codec '{codec}' is not supported. This engine reads \"null\", " +
            "\"deflate\", \"snappy\", \"zstandard\", \"gzip\" and \"brotli\".")
    };


    private static byte[] Zstandard(byte[] payload)
    {
        // A STANDARD Zstandard frame, not a wrapper of our own: ZstandardStream is the runtime's zstd binding
        // (System.IO.Compression, in this runtime since .NET 11) and writes what google/zstd writes, which is
        // exactly what the Avro spec names, so a container crosses with Java, Go and Python readers too.
        using var output = new MemoryStream();
        using (var zstandard = new ZstandardStream(output, CompressionMode.Compress, leaveOpen: true))
            zstandard.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private static byte[] ExpandZstandard(byte[] payload, string member)
    {
        // A zstd failure is an IOException or an InvalidDataException (ZstandardUtils.CreateExceptionForError),
        // and it reaches the script through the engine's one message map, never as a CLR sentence.
        try
        {
            using var input = new MemoryStream(payload);
            using var zstandard = new ZstandardStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            zstandard.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ScriptException(
                $"{member}: a block labelled \"zstandard\" is not a valid Zstandard frame " +
                $"({Interpreter.FriendlyExceptionMessage(ex)}), so its payload cannot be read.");
        }
    }

    private static byte[] GZip(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private static byte[] ExpandGZip(byte[] payload, string member)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ScriptException(
                $"{member}: a block labelled \"gzip\" is not a valid gzip stream " +
                $"({Interpreter.FriendlyExceptionMessage(ex)}), so its payload cannot be read.");
        }
    }

    private static byte[] Brotli(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
            brotli.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private static byte[] ExpandBrotli(byte[] payload, string member)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ScriptException(
                $"{member}: a block labelled \"brotli\" is not a valid Brotli stream " +
                $"({Interpreter.FriendlyExceptionMessage(ex)}), so its payload cannot be read.");
        }
    }

    private static byte[] Deflate(byte[] payload)
    {
        // RAW DEFLATE, no zlib wrapper: DeflateStream is exactly RFC 1951, which is what Avro names.
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(payload, 0, payload.Length);
        return output.ToArray();
    }

    private static byte[] Inflate(byte[] payload, string member)
    {
        try
        {
            using var input = new MemoryStream(payload);
            using var inflate = new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            inflate.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new ScriptException(
                $"{member}: a block labelled \"deflate\" is not a valid DEFLATE stream " +
                $"({Interpreter.FriendlyExceptionMessage(ex)}), so its payload cannot be read.");
        }
    }
}
