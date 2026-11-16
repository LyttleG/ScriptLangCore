using System.Buffers.Binary;
using System.Text;

namespace ScriptLangCore;

/// <summary>The Avro binary encoding, as a writer and a reader over one growable buffer. The spec's rules, each
/// stated where it is implemented: int and long are zig-zag varints, float and double are 4 and 8 raw IEEE bytes
/// little-endian, bytes and string are a long length then the payload, an array or a map is a sequence of blocks
/// (a long count, then that many items, and a 0 count ends it), a union writes the branch's index as a long, and a
/// fixed is exactly its size with no length.</summary>
internal sealed class AvroBinaryWriter
{
    private byte[] _buffer = new byte[256];
    private int _length;

    internal int Length => _length;

    internal void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    internal void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    /// <summary>Zig-zag varint: non-negative values are doubled, negative ones are mirrored, so a small magnitude
    /// is one byte in both directions (0 -> 00, -1 -> 01, 1 -> 02).</summary>
    internal void WriteLong(long value)
    {
        var zigzag = (ulong)((value << 1) ^ (value >> 63));
        while (zigzag >= 0x80)
        {
            WriteByte((byte)(zigzag | 0x80));
            zigzag >>= 7;
        }
        WriteByte((byte)zigzag);
    }

    internal void WriteInt(int value) => WriteLong(value);

    internal void WriteBoolean(bool value) => WriteByte(value ? (byte)1 : (byte)0);

    internal void WriteFloat(float value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(scratch, value);
        WriteRaw(scratch);
    }

    internal void WriteDouble(double value)
    {
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(scratch, value);
        WriteRaw(scratch);
    }

    internal void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteLong(value.Length);
        WriteRaw(value);
    }

    internal void WriteString(string value)
    {
        var count = Encoding.UTF8.GetByteCount(value);
        WriteLong(count);
        Ensure(count);
        Encoding.UTF8.GetBytes(value, _buffer.AsSpan(_length));
        _length += count;
    }

    /// <summary>One block of an array or a map: a long count, then that many items. This writer always emits ONE
    /// block per collection and ends it with <see cref="WriteBlockEnd"/>, which is the shape the spec allows and
    /// the one every reader accepts; the reader still accepts several blocks, because a Java writer may split.</summary>
    internal void WriteBlockCount(long count) => WriteLong(count);

    /// <summary>The 0 that ends an array or a map (a block header with no items).</summary>
    internal void WriteBlockEnd() => WriteLong(0);

    /// <summary>A fixed is exactly its declared size: no length prefix and no count, so the schema is the only
    /// thing that says how many bytes to read.</summary>
    internal void WriteFixed(ReadOnlySpan<byte> value) => WriteRaw(value);

    /// <summary>An enum writes the SYMBOL's index, in declaration order, as an int. The caller resolves the symbol
    /// to its index and so refuses an unknown symbol before reaching here; this only writes the index.</summary>
    internal void WriteEnumIndex(int index) => WriteInt(index);

    /// <summary>A union writes the branch's index, in declaration order, as a long. The caller resolves WHICH
    /// branch by the spec's "first matching branch" rule; this only writes the answer.</summary>
    internal void WriteUnionIndex(int index) => WriteLong(index);

    internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        var size = _buffer.Length;
        while (size < _length + extra) size *= 2;
        Array.Resize(ref _buffer, size);
    }
}

/// <summary>The reader that mirrors <see cref="AvroBinaryWriter"/>. Every read is bounded and refuses in the
/// engine's own words, naming WHAT ran out: a truncated payload must not surface as a CLR index exception.</summary>
internal ref struct AvroBinaryReader(ReadOnlySpan<byte> data, string member)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    internal int Position => _position;
    internal int Remaining => _data.Length - _position;
    internal bool AtEnd => _position >= _data.Length;

    private void Need(int count, string what)
    {
        if (count < 0 || _position + count > _data.Length)
            throw new ScriptException(
                $"{member}: the payload ended before {what} was read ({Remaining} byte(s) left at offset " +
                $"{_position}). The writer's data and the schema disagree, or the payload is truncated.");
    }

    internal byte ReadByte(string what)
    {
        Need(1, what);
        return _data[_position++];
    }

    internal long ReadLong(string what)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var b = ReadByte(what);
            value |= (ulong)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 63)
                throw new ScriptException(
                    $"{member}: a variable-length integer at offset {_position} is too long, so the payload does " +
                    "not match the schema.");
        }
        return (long)((value >> 1) ^ (ulong)-(long)(value & 1));
    }

    internal int ReadInt(string what) => checked((int)ReadLong(what));

    internal bool ReadBoolean(string what)
    {
        var b = ReadByte(what);
        return b != 0;
    }

    internal float ReadFloat(string what)
    {
        Need(4, what);
        var value = BinaryPrimitives.ReadSingleLittleEndian(_data[_position..]);
        _position += 4;
        return value;
    }

    internal double ReadDouble(string what)
    {
        Need(8, what);
        var value = BinaryPrimitives.ReadDoubleLittleEndian(_data[_position..]);
        _position += 8;
        return value;
    }

    /// <summary>Reads a length-prefixed byte string. A negative length is refused: Avro has no null bytes value,
    /// so a negative length means the payload is out of phase.</summary>
    internal byte[] ReadBytes(string what)
    {
        var length = ReadLong(what);
        if (length < 0)
            throw new ScriptException(
                $"{member}: a {what} declares a negative length ({length}) at offset {_position}, so the payload " +
                "does not match the schema.");
        Need(checked((int)length), what);
        var value = _data.Slice(_position, (int)length).ToArray();
        _position += (int)length;
        return value;
    }

    internal string ReadString(string what) => Encoding.UTF8.GetString(ReadBytes(what));

    /// <summary>The next block of an array or a map: 0 means the collection ended, and a NEGATIVE count means
    /// `-count` items are preceded by a long byte size, which is how a writer may split a large collection. The
    /// size is read and discarded (the items are decoded by the schema anyway), but it must be consumed, or the
    /// next read would land inside it.</summary>
    internal long ReadBlockCount(string what)
    {
        var count = ReadLong(what);
        if (count < 0)
        {
            var size = ReadLong(what);
            if (size < 0)
                throw new ScriptException(
                    $"{member}: the {what} block declares a negative byte size ({size}) at offset {_position}, so " +
                    "the payload does not match the schema.");
            return -count;
        }
        return count;
    }

    /// <summary>A fixed is its declared size with no prefix, so the SCHEMA is the only bound: a payload shorter
    /// than that size cannot hold the value.</summary>
    internal byte[] ReadFixed(int size, string what)
    {
        Need(size, what);
        var value = _data.Slice(_position, size).ToArray();
        _position += size;
        return value;
    }

    /// <summary>An enum writes the symbol's index; an index outside the declared symbols names a symbol this
    /// schema does not have, which is a schema/payload disagreement rather than a value to guess at.</summary>
    internal int ReadEnumIndex(int symbolCount, string what)
    {
        var index = ReadInt(what);
        if (index < 0 || index >= symbolCount)
            throw new ScriptException(
                $"{member}: a {what} index is {index}, but the enum declares {symbolCount} symbol(s) (0.." +
                $"{(symbolCount == 0 ? "none" : (symbolCount - 1).ToString())}). The writer's schema and this one " +
                "disagree about the symbols.");
        return index;
    }

    /// <summary>A union writes the branch's index as a long; an index past the branch list is the same
    /// disagreement as an out-of-range enum index.</summary>
    internal int ReadUnionIndex(int branchCount, string what)
    {
        var index = ReadLong(what);
        if (index < 0 || index >= branchCount)
            throw new ScriptException(
                $"{member}: a {what} branch index is {index}, but the union has {branchCount} branch(es). The " +
                "writer's schema and this one disagree about the branches.");
        return (int)index;
    }

    internal void Skip(int count, string what)
    {
        Need(count, what);
        _position += count;
    }
}
