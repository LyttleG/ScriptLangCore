using System.Collections;
using System.Text;

namespace ScriptLangCore;

/// <summary>Reads an Avro Object Container File ONE RECORD AT A TIME: `Avro.OpenReader(path)` answers this, the
/// script-facing counterpart of AvroConvert's `OpenDeserializer&lt;T&gt;(stream)`, and the difference from
/// `Avro.Read` is where the memory goes rather than what is decoded.
///
/// WHAT IT DOES NOT HOLD: the file is read as its own format describes it, so the header is parsed once (magic,
/// `avro.schema`, `avro.codec`, sync marker) and then ONE BLOCK at a time is read, decompressed and decoded from a
/// cursor. A record is decoded when it is asked for, so a million-record file costs its header, the block currently
/// being walked, and the record in hand. `Avro.Read("Iterable", data)` is the lazy view over BYTES (the whole
/// file is still in memory); this is the door for a file big enough that its bytes should not be. The two agree about
/// what a record is because both decode through <see cref="AvroValueMap"/> and the same block framing.
///
/// THE TYPE, WHEN ONE IS GIVEN, IS IMPOSED ON EACH RECORD AS IT IS DECODED, exactly as the lazy
/// `"Iterable&lt;Row&gt;"` read imposes its element name, so a class comes back as the class rather than as a
/// dictionary.
///
/// IT IS FORWARD-ONLY: `ReadNext()` walks one cursor, so what a caller has read is never offered again, and
/// `Count` and `Blocks` are a cheap HEADER scan (the block headers are read and their payloads skipped without
/// decompressing anything), so they are available before the first record and do not consume it.
///
/// IT ALSO READS A STREAM THAT CANNOT SEEK, such as an HTTP request body: the header and the block walk are
/// sequential, so the only thing a non-seekable source loses is that header scan. There `Count` and `Blocks` answer
/// `-1` until the last block has been read and the real totals after it, because nothing before the end can know how
/// many blocks follow. `Position` (records handed out so far) is answered from a counter either way.
///
/// `Close` (or `using`) releases the file handle. Reading after it is REFUSED by name rather than answered from a
/// closed stream.</summary>
public sealed class AvroReader : IDisposable
{
    private static ReadOnlySpan<byte> Magic => [0x4F, 0x62, 0x6A, 0x01]; // "Obj" + 0x01, the spec's first four bytes

    /// <summary>The buffer a non-seekable source is read through, in bytes. It is `StreamCursor`'s need rather than an
    /// optimisation: a varint is read one BYTE at a time, so on a socket-backed stream an unbuffered read is one
    /// request per byte.</summary>
    private const int BufferSize = 64 * 1024;

    private readonly Stream _source;
    private readonly bool _ownsSource;
    private readonly bool _seekable;
    private readonly AvroSchema _schema;
    private readonly string _schemaJson;
    private readonly string _codec;
    private readonly byte[] _sync;
    private readonly long _firstBlock;
    private readonly string? _typeName;
    private readonly bool _caseSensitive;
    private readonly string _member;
    private readonly CancellationToken _abort;
    private readonly bool _drainOnClose;

    /// <summary>What the blocks READ have declared and how many of them there were: on a stream that cannot seek this
    /// is the only count there is, and it is complete once the container has ended.</summary>
    private long _declared;
    private long _blocksRead;

    /// <summary>How many bytes of the source this reader has consumed, which is what a refusal about an aborted
    /// request can name (`Stream.Position` is not available on a stream that cannot seek).</summary>
    private long _consumed;

    /// <summary>The current block's DECOMPRESSED payload, its cursor, and how many of its records are left. A block
    /// is decompressed when the reader reaches it and dropped when its last record has been handed out, which is what
    /// keeps the memory to one block.</summary>
    private byte[]? _payload;
    private int _cursor;
    private long _leftInBlock;

    private bool _hasBuffered;
    private object? _buffered;
    private bool _finished;
    private bool _closed;

    private long _read;
    private long _count = -1;
    private long _blocks = -1;

    internal AvroReader(Stream source, bool ownsSource, string? typeName, bool caseSensitive, string member,
        CancellationToken abort = default, bool drainOnClose = false)
    {
        // The header and the block walk are SEQUENTIAL, so a source that cannot seek reads exactly the same way: the
        // one thing it loses is the header scan behind `Count`/`Blocks`, which answers -1 until the container ends.
        _seekable = source.CanSeek;
        _abort = abort;
        _drainOnClose = drainOnClose;
        // A non-seekable source is buffered BEFORE the header is read (see BufferSize). The reader owns the buffer it
        // makes; a source it was told not to own (the server owns a request body) is shielded by a wrapper the
        // buffer's Dispose cannot close, so `Close` releases the buffer and never the connection.
        _source = _seekable
            ? source
            : new BufferedStream(ownsSource ? source : new NonClosingStream(source), BufferSize);
        _ownsSource = _seekable ? ownsSource : true;
        _typeName = typeName;
        _caseSensitive = caseSensitive;
        _member = member;

        var cursor = new StreamCursor(_source, member);
        if (!Magic.SequenceEqual(cursor.ReadExactly(4)))
            throw new ScriptException(
                $"{member}: this is not an Avro container file (it does not start with the magic 'Obj' + 1). " +
                "A reader walks a container; one value's binary encoding is Avro.Deserialize.");

        var meta = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var entries = cursor.ReadLong();
        if (entries < 0)
            throw new ScriptException($"{member}: the header's metadata map declares {entries} entries.");
        for (var i = 0; i < entries; i++)
        {
            var key = cursor.ReadString();
            meta[key] = cursor.ReadBytesValue();
        }
        var tail = cursor.ReadLong();
        if (tail != 0)
            throw new ScriptException(
                $"{member}: the header's metadata map does not end with a 0 block count (it declares {tail} " +
                "more entries), so the file is truncated or not a container.");

        if (!meta.TryGetValue("avro.schema", out var schemaBytes))
            throw new ScriptException(
                $"{member}: the container carries no \"avro.schema\" entry, so its values cannot be decoded.");
        _codec = meta.TryGetValue("avro.codec", out var codecBytes)
            ? Encoding.UTF8.GetString(codecBytes)
            : "null";
        _schemaJson = Encoding.UTF8.GetString(schemaBytes);
        _schema = AvroSchemaParser.Parse(_schemaJson, member);
        _sync = cursor.ReadExactly(AvroContainer.SyncSize);
        _consumed = cursor.Consumed;
        // Where the first block starts, which is what the header scan seeks back to. A source that cannot seek never
        // scans, so it needs no anchor and must not ask for its position (that call THROWS on such a stream).
        _firstBlock = _seekable ? _source.Position : 0;
    }

    /// <summary>The `.avsc` text the FILE carries, verbatim: what `Avro.ContainerSchema` answers for a container, with
    /// no second read.</summary>
    public string Schema => _schemaJson;

    /// <summary>The compression the file declares (`null`, `deflate`, `snappy`, `zstandard`, `gzip`, `brotli`). An
    /// unsupported one is refused when the first block is read, naming the codec.</summary>
    public string Codec => _codec;

    /// <summary>How many records the container declares, in total. On a source that can seek this is a HEADER scan:
    /// every block's count is read and its payload is skipped without decompressing anything, so it costs one pass
    /// over the block framing rather than a decode. On a stream that cannot seek, nothing before the end can know what
    /// follows, so this answers `-1` until the last block has been read and the real total after it.</summary>
    public long Count
    {
        get
        {
            if (!_seekable) return _finished ? _declared : -1;
            ScanBlockHeaders();
            return _count;
        }
    }

    /// <summary>How many BLOCKS the container holds, from the same header scan, and with the same `-1` on a stream
    /// that cannot seek. A writer that took `{ "block": n }` produces `ceil(records / n)` of them, which is the
    /// observable a streaming writer is judged by.</summary>
    public long Blocks
    {
        get
        {
            if (!_seekable) return _finished ? _blocksRead : -1;
            ScanBlockHeaders();
            return _blocks;
        }
    }

    /// <summary>How many records this reader has handed out so far, so a progress line needs no counter of its
    /// own.</summary>
    public long Position => _read;

    /// <summary>Whether another record is available. It pulls and decodes ONE record, which is what lets the answer be
    /// known before anything is returned; a caller that only wants to count what is left reads <see cref="Count"/>
    /// instead.</summary>
    public bool HasNext()
    {
        if (_closed)
            throw new ScriptException($"{_member}: this reader was closed after {_read} record(s): nothing more can be read.");
        return EnsureBuffered();
    }

    /// <summary>The next record, decoded with the file's own schema and imposed on the type the read asked for, when it
    /// asked for one. After the last record it is REFUSED by name: an answer of null would be indistinguishable from a
    /// record whose whole value is null, which this format allows.</summary>
    public object? ReadNext()
    {
        if (!HasNext())
            throw new ScriptException(
                $"{_member}: there is no next record: the container holds {Count} record(s) and this reader has " +
                $"handed out {_read}. Test HasNext() before ReadNext().");
        var value = _buffered;
        _buffered = null;
        _hasBuffered = false;
        return value;
    }

    /// <summary>Closes the reader and releases the file handle it owns. Closing twice is a no-op, so a `catch` that
    /// closes on the way out cannot fail there. A reader over a request body also drains what the container did not
    /// consume, because a kept-alive connection with an unread payload is a broken one.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _payload = null;
        if (_drainOnClose) DrainRemainder(_source, _abort);
        if (_ownsSource) _source.Dispose();
    }

    public void Dispose() => Close();

    public override string ToString() => "AvroReader";

    /// <summary>The one header scan behind `Count` and `Blocks` on a source that can seek: every block's object count
    /// and size are read, the payload is SKIPPED (a seek, so nothing is decompressed), the sync marker is checked, and
    /// the stream is put back where it was. A file whose framing disagrees is refused here with the same words the
    /// block reader uses. A stream that cannot seek never calls this: it must not be asked to seek, and its `Length`
    /// does not exist at all.</summary>
    private void ScanBlockHeaders()
    {
        if (!_seekable) return;
        if (_count >= 0) return;
        var save = _source.Position;
        try
        {
            _source.Position = _firstBlock;
            var cursor = new StreamCursor(_source, _member);
            long total = 0;
            long blocks = 0;
            while (cursor.TryReadLong(out var objects))
            {
                if (objects < 0)
                    throw new ScriptException(
                        $"{_member}: a block declares {objects} object(s), so the file is truncated or corrupt.");
                var size = cursor.ReadLong();
                if (size < 0 || size > _source.Length - _source.Position - AvroContainer.SyncSize)
                    throw new ScriptException(
                        $"{_member}: a block declares {size} byte(s), which runs past the end of the container, so " +
                        "the file is truncated or corrupt.");
                cursor.Skip(size);
                var marker = cursor.ReadExactly(AvroContainer.SyncSize);
                if (!_sync.AsSpan().SequenceEqual(marker))
                    throw new ScriptException(
                        $"{_member}: a block does not end with the file's sync marker, so the file is corrupt or " +
                        "the reader is out of step.");
                total += objects;
                blocks++;
            }
            _count = total;
            _blocks = blocks;
        }
        finally
        {
            _source.Position = save;
        }
    }

    /// <summary>Decodes the next record into the buffer, pulling blocks until one has a record left. False when the
    /// container has ended, which is a clean end and not an error.</summary>
    private bool EnsureBuffered()
    {
        if (_hasBuffered) return true;
        while (true)
        {
            if (_leftInBlock > 0)
            {
                _buffered = DecodeOne();
                _hasBuffered = true;
                return true;
            }
            if (_payload != null)
            {
                // Every record the block declared has been decoded, so the payload must be exactly consumed: a block
                // whose schema disagrees with its bytes is refused rather than left half-read.
                if (_cursor != _payload.Length)
                    throw new ScriptException(
                        $"{_member}: a block's payload ended with {_payload.Length - _cursor} byte(s) left after its " +
                        "declared record count, so the file's schema and its blocks disagree.");
                _payload = null;
            }
            if (!LoadNextBlock()) return false;
        }
    }

    /// <summary>Reads one block's framing and payload, verifies its sync marker and decompresses it. An EMPTY block is
    /// legal and is skipped, which is how a writer that flushed nothing is read rather than counted as a record. The
    /// request token is checked BETWEEN blocks, so a client that went away stops the read at a block boundary rather
    /// than after the whole container.</summary>
    private bool LoadNextBlock()
    {
        if (_finished) return false;
        var cursor = new StreamCursor(_source, _member);
        while (true)
        {
            if (_abort.IsCancellationRequested)
                throw new ScriptException(
                    $"{_member}: the request was aborted after {_read} record(s) and {_consumed + cursor.Consumed} " +
                    "byte(s) of the container: the client went away, so the container is incomplete.");
            if (!cursor.TryReadLong(out var objects))
            {
                _finished = true;
                _consumed += cursor.Consumed;
                return false;
            }
            if (objects < 0)
                throw new ScriptException(
                    $"{_member}: a block declares {objects} object(s), so the file is truncated or corrupt.");
            var size = cursor.ReadLong();
            if (size < 0 || size > int.MaxValue)
                throw new ScriptException(
                    $"{_member}: a block declares {size} byte(s), which cannot be a payload of this container.");
            var compressed = cursor.ReadExactly((int)size);
            var marker = cursor.ReadExactly(AvroContainer.SyncSize);
            if (!_sync.AsSpan().SequenceEqual(marker))
                throw new ScriptException(
                    $"{_member}: a block does not end with the file's sync marker, so the file is corrupt or the " +
                    "reader is out of step.");
            _declared += objects;
            _blocksRead++;
            if (objects == 0) continue;
            _payload = AvroCodecs.Decompress(_codec, compressed, _member);
            _cursor = 0;
            _leftInBlock = objects;
            _consumed += cursor.Consumed;
            return true;
        }
    }

    /// <summary>Reads and discards whatever the container did not consume, so the connection a request body arrived on
    /// can be kept alive: the server drains only a body NOBODY touched (<c>BodyWasTouched</c>), and a body this reader
    /// took is touched, so the drain belongs here. The request token is checked between chunks and stops the drain
    /// quietly, because a client that went away leaves nothing worth draining.</summary>
    internal static void DrainRemainder(Stream source, CancellationToken abort)
    {
        try
        {
            var buffer = new byte[BufferSize];
            while (!abort.IsCancellationRequested)
            {
                if (source.Read(buffer, 0, buffer.Length) <= 0) return;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // A connection that broke mid-drain has nothing left to keep alive, so this is not the caller's failure.
        }
    }

    /// <summary>One record from the current block's cursor, imposed on the asked-for type when there is one.</summary>
    private object? DecodeOne()
    {
        var reader = new AvroBinaryReader((_payload ?? []).AsSpan(_cursor), _member);
        var value = AvroValueMap.Read(ref reader, _schema, _member);
        _cursor += reader.Position;
        _leftInBlock--;
        _read++;
        return _typeName is null ? value : AvroValueMap.ImposeType(value, _typeName, _caseSensitive);
    }
}

/// <summary>The minimal reader an Avro container needs FROM A STREAM: bounded reads that refuse in the engine's own
/// words, and a varint decoder that can tell "the file ended" from "the file ended inside a number". It exists because
/// <see cref="AvroBinaryReader"/> works over one buffer, which is the whole point of not holding the file.</summary>
internal sealed class StreamCursor(Stream stream, string member)
{
    private readonly Stream _stream = stream;
    private readonly string _member = member;
    private long _consumed;

    /// <summary>How many bytes this cursor has read. It exists because <c>Stream.Position</c> THROWS on a stream that
    /// cannot seek, so "how far did the read get" is a count the cursor keeps rather than one it asks for.</summary>
    internal long Consumed => _consumed;

    /// <summary>Exactly `count` bytes, or a refusal naming how many are missing. A partial read is retried, because a
    /// stream is free to answer less than asked.</summary>
    internal byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        var got = 0;
        while (got < count)
        {
            var read = _stream.Read(buffer, got, count - got);
            if (read <= 0)
                throw new ScriptException(
                    $"{_member}: the container ended in the middle of a value: {count - got} of {count} byte(s) " +
                    "are missing.");
            got += read;
            _consumed += read;
        }
        return buffer;
    }

    /// <summary>A zig-zag varint, or FALSE when the stream ended cleanly before its first byte. That distinction is
    /// what lets the block walk end where the file does and refuse everywhere else.</summary>
    internal bool TryReadLong(out long value)
    {
        value = 0;
        ulong acc = 0;
        var shift = 0;
        while (true)
        {
            var raw = _stream.ReadByte();
            if (raw < 0)
            {
                if (shift == 0) return false;
                throw new ScriptException($"{_member}: the container ended in the middle of a number.");
            }
            var b = (byte)raw;
            _consumed++;
            acc |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63)
                throw new ScriptException($"{_member}: a number in the container is not a valid varint.");
        }
        value = (long)(acc >> 1) ^ -(long)(acc & 1);
        return true;
    }

    internal long ReadLong()
    {
        if (!TryReadLong(out var value))
            throw new ScriptException($"{_member}: the container ended where a number was expected.");
        return value;
    }

    /// <summary>A `bytes` value: its length, then exactly that many bytes. The length is checked before allocating, so
    /// a corrupt length cannot ask for the whole address space.</summary>
    internal byte[] ReadBytesValue()
    {
        var length = ReadLong();
        if (length < 0 || length > int.MaxValue)
            throw new ScriptException($"{_member}: a value declares {length} byte(s), which cannot be read.");
        return ReadExactly((int)length);
    }

    internal string ReadString() => Encoding.UTF8.GetString(ReadBytesValue());

    /// <summary>Skips a block payload. A seek when the stream can do it, which is the case this reader is built for;
    /// a read-and-discard otherwise, so the code is honest about a stream it happened to be handed.</summary>
    internal void Skip(long count)
    {
        if (count <= 0) return;
        if (_stream.CanSeek)
        {
            _stream.Seek(count, SeekOrigin.Current);
            _consumed += count;
            return;
        }
        var buffer = new byte[64 * 1024];
        var left = count;
        while (left > 0)
        {
            var ask = (int)Math.Min(buffer.Length, left);
            var read = _stream.Read(buffer, 0, ask);
            if (read <= 0)
                throw new ScriptException($"{_member}: the container ended in the middle of a value.");
            left -= read;
            _consumed += read;
        }
    }
}

/// <summary>A stream the reader does NOT own, shielded so a buffer over it can be disposed without closing it: a
/// request body belongs to the server, and disposing it would break the connection. `Dispose` flushes and stops there,
/// which is all a <see cref="BufferedStream"/> does to its inner stream.</summary>
internal sealed class NonClosingStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        // The inner stream is someone else's: closing it here is what would break a kept-alive connection.
        if (disposing) Flush();
        base.Dispose(disposing);
    }
}
