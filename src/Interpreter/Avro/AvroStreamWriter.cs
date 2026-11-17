using System.Security.Cryptography;

namespace ScriptLangCore;

/// <summary>The two options the STREAMING doors take, and only those two: `codec` is the container's compression (the
/// same spellings `Avro.Write` accepts) and `block` is how many records one block holds, which is what decides
/// how much a reader receives per flush. They are a separate reader from <see cref="AvroOptions"/> because the
/// schema-generation options (`namespace`, `decimalPrecision`, `recordName`) have nothing to say here, and accepting
/// one silently is the kind of option this repository refuses by name.</summary>
internal readonly record struct AvroStreamOptions(string Codec, int Block)
{
    /// <summary>1000 records per block: one flush per thousand records is a stream a client sees moving without a
    /// block header per record, and it is small enough that the buffer a writer holds is a few hundred kilobytes at
    /// most.</summary>
    internal const int DefaultBlock = 1000;

    /// <summary>The most records one block may hold. A block is decompressed whole by every reader, so a cap keeps one
    /// typo from asking a reader to hold a whole file at once; a million is far above anything a stream needs.</summary>
    internal const int MaxBlock = 1_000_000;

    internal static AvroStreamOptions Read(object? value, string member)
    {
        var codec = "null";
        var block = DefaultBlock;
        if (value is not null)
        {
            foreach (var (key, item) in AvroOptions.Pairs(value, member,
                "for example { \"codec\": \"deflate\", \"block\": 1000 }"))
            {
                switch (key)
                {
                    case "codec":
                        codec = item as string
                            ?? throw new ScriptException(
                                $"{member}: \"codec\" is a string (\"null\", \"deflate\", \"snappy\", " +
                                $"\"zstandard\", \"gzip\" or \"brotli\"), not '{AvroOptions.FriendlyNameOf(item)}'.");
                        break;
                    case "block":
                        block = item switch
                        {
                            int count => count,
                            long count when count is > 0 and <= MaxBlock => (int)count,
                            _ => throw new ScriptException(
                                $"{member}: \"block\" is a whole number of records per block (1 to {MaxBlock}), " +
                                $"not '{AvroOptions.FriendlyNameOf(item)}'.")
                        };
                        if (block < 1)
                            throw new ScriptException(
                                $"{member}: \"block\" is at least 1 record, not {block}: a block of no records " +
                                "would make a writer that never ends.");
                        break;
                    default:
                        throw new ScriptException(
                            $"{member}: unknown option \"{key}\". The streaming options are codec and block.");
                }
            }
        }
        AvroContainer.ValidateCodec(codec, member);
        return new AvroStreamOptions(codec, block);
    }
}

/// <summary>Writes an Avro Object Container File INCREMENTALLY: the header once, then one block per
/// <see cref="AvroStreamOptions.Block"/> records as the source hands them over. That is the whole difference from
/// `Avro.Write`, which answers the file's bytes and therefore holds every record and every byte at once.
///
/// WHERE THE RECORDS COME FROM IS THE ENGINE'S OWN STREAM ADAPTER, so this writer accepts exactly what
/// `http.StreamLines` accepts (a list, a generator, an `Events` feed, an async sequence) and pulls them one at a time
/// on the caller's thread. The adapter owns what it opened, so the CALLER disposes it, which the two registered doors
/// do in a `finally`.
///
/// IT NEVER CLOSES THE TARGET: a file the caller named is closed by its own `using`, and a `FileByteWriter` stays
/// usable after the call, which is what makes "write the container into the handle I already have" work.</summary>
internal static class AvroStreamWriter
{
    /// <summary>Writes the header and then a block per <paramref name="options"/>' block size, flushing after each one
    /// so a client or a reader sees progress. Answers the record count written.</summary>
    internal static long Write(Stream target, IHttpStreamSource source, AvroSchema schema, AvroStreamOptions options,
        bool caseSensitive, string member)
    {
        var sync = new byte[AvroContainer.SyncSize];
        RandomNumberGenerator.Fill(sync);
        target.Write(AvroContainer.HeaderBytes(schema, options.Codec, sync));
        target.Flush();

        var buffer = new List<object?>(Math.Min(options.Block, 1024));
        long total = 0;
        while (source.MoveNext(out var record))
        {
            buffer.Add(record);
            if (buffer.Count < options.Block) continue;
            WriteBlock(target, buffer, schema, options.Codec, sync, caseSensitive, member);
            total += buffer.Count;
            buffer.Clear();
        }
        if (buffer.Count > 0)
        {
            WriteBlock(target, buffer, schema, options.Codec, sync, caseSensitive, member);
            total += buffer.Count;
        }
        target.Flush();
        return total;
    }

    private static void WriteBlock(Stream target, List<object?> records, AvroSchema schema, string codec,
        byte[] sync, bool caseSensitive, string member)
    {
        target.Write(AvroContainer.BuildBlock(records, schema, codec, sync, caseSensitive, member));
        target.Flush();
    }
}

/// <summary>The HTTP half of the streaming writer: a route answers `http.AvroStream(...)` and this is the sequence the
/// chunked writer pulls. FIRST CHUNK is the container header, then one chunk per block, so the first byte a client
/// receives already says what the rest is (schema and codec included) and no length has to be known up front.
///
/// It adapts the SAME source kinds `http.StreamLines` does (`Interpreter.StreamSourceFor`), so a route can stream a
/// script generator, a list, an `Events` feed or an async sequence into an Avro container without materializing
/// it.</summary>
internal sealed class AvroStreamSource : IHttpStreamSource
{
    private readonly IHttpStreamSource _records;
    private readonly AvroSchema _schema;
    private readonly string _codec;
    private readonly int _block;
    private readonly bool _caseSensitive;
    private readonly string _member;
    private readonly byte[] _sync = new byte[AvroContainer.SyncSize];
    private readonly List<object?> _buffer;
    private bool _headerSent;

    internal AvroStreamSource(IHttpStreamSource records, AvroSchema schema, string codec, int block,
        bool caseSensitive, string member)
    {
        _records = records;
        _schema = schema;
        _codec = codec;
        _block = block;
        _caseSensitive = caseSensitive;
        _member = member;
        _buffer = new List<object?>(Math.Min(block, 1024));
        RandomNumberGenerator.Fill(_sync);
    }

    public bool MoveNext(out object? item)
    {
        if (!_headerSent)
        {
            _headerSent = true;
            item = AvroContainer.HeaderBytes(_schema, _codec, _sync);
            return true;
        }
        _buffer.Clear();
        while (_buffer.Count < _block && _records.MoveNext(out var record))
            _buffer.Add(record);
        if (_buffer.Count == 0)
        {
            item = null;
            return false;
        }
        item = AvroContainer.BuildBlock(_buffer, _schema, _codec, _sync, _caseSensitive, _member);
        return true;
    }

    /// <summary>Releases the producer the adapter opened (a generator's enumerator, an async sequence's handle), on
    /// every exit, which is the contract <see cref="IHttpStreamSource"/> states.</summary>
    public void Dispose() => _records.Dispose();
}
