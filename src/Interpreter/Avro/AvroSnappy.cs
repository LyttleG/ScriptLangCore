using System.Text;

namespace ScriptLangCore;

/// <summary>The `snappy` codec's Avro framing: a Snappy BLOCK (the format google/snappy defines, not the
/// frame format) followed by the CRC-32 of the UNCOMPRESSED data as four big-endian bytes. Avro's spec states
/// that trailer, so a reader here can check the payload it just decompressed instead of trusting it.
///
/// Managed on purpose: this engine ships one NativeAOT image with no NuGet dependency, so the block codec is
/// implemented here rather than brought in. The decoder accepts every tag the format defines (all four copy
/// forms and every literal length), and the encoder writes literals plus back-references from a small hash
/// table, which is the same shape the reference compressor uses.</summary>
internal static class AvroSnappy
{
    // ── the block codec ────────────────────────────────────────────────────────────────────────────────

    internal static byte[] Compress(byte[] input, string member)
    {
        var writer = new AvroBinaryWriter();
        WriteVarint(writer, input.Length);
        if (input.Length < 4)
        {
            EmitLiteral(writer, input, 0, input.Length);
        }
        else
        {
            var table = new int[1 << 14];
            Array.Fill(table, -1);
            var ip = 0;
            var literalStart = 0;
            while (ip + 4 <= input.Length)
            {
                var hash = Hash(input, ip);
                var candidate = table[hash];
                table[hash] = ip;
                if (candidate >= 0 && ip - candidate <= 65535 && Matches4(input, candidate, ip))
                {
                    var length = 4;
                    while (ip + length < input.Length && input[candidate + length] == input[ip + length]) length++;
                    EmitLiteral(writer, input, literalStart, ip - literalStart);
                    EmitCopy(writer, ip - candidate, length);
                    ip += length;
                    literalStart = ip;
                }
                else
                {
                    ip++;
                }
            }
            EmitLiteral(writer, input, literalStart, input.Length - literalStart);
        }

        var block = writer.ToArray();
        var framed = new byte[block.Length + 4];
        block.CopyTo(framed, 0);
        var crc = Crc32(input);
        framed[block.Length] = (byte)(crc >> 24);
        framed[block.Length + 1] = (byte)(crc >> 16);
        framed[block.Length + 2] = (byte)(crc >> 8);
        framed[block.Length + 3] = (byte)crc;
        return framed;
    }

    internal static byte[] Decompress(byte[] payload, string member)
    {
        if (payload.Length < 5)
            throw new ScriptException(
                $"{member}: a \"snappy\" block is a Snappy block plus a 4-byte CRC, and this one is " +
                $"{payload.Length} byte(s) long.");
        var block = payload.AsSpan(0, payload.Length - 4);
        var expected = (uint)(payload[^4] << 24 | payload[^3] << 16 | payload[^2] << 8 | payload[^1]);

        var ip = 0;
        var length = ReadVarint(block, ref ip, member);
        if (length < 0 || length > 1 << 30)
            throw new ScriptException($"{member}: a \"snappy\" block declares {length} byte(s) of output.");
        var output = new byte[length];
        var op = 0;
        while (op < length)
        {
            if (ip >= block.Length)
                throw new ScriptException(
                    $"{member}: a \"snappy\" block ends after {op} of {length} byte(s), so it is truncated.");
            var tag = block[ip++];
            switch (tag & 0x3)
            {
                case 0: // literal
                {
                    var x = tag >> 2;
                    int count;
                    if (x < 60) count = x + 1;
                    else
                    {
                        var bytes = x - 59;
                        if (ip + bytes > block.Length)
                            throw new ScriptException($"{member}: a \"snappy\" literal length runs past the block.");
                        var n = 0;
                        for (var i = 0; i < bytes; i++) n |= block[ip++] << (8 * i);
                        count = n + 1;
                    }
                    if (ip + count > block.Length || op + count > length)
                        throw new ScriptException($"{member}: a \"snappy\" literal runs past its declared output.");
                    block.Slice(ip, count).CopyTo(output.AsSpan(op));
                    ip += count;
                    op += count;
                    break;
                }
                case 1: // copy, 1-byte offset
                {
                    if (ip >= block.Length)
                        throw new ScriptException($"{member}: a \"snappy\" copy offset is missing.");
                    var count = 4 + ((tag >> 2) & 0x7);
                    var offset = ((tag >> 5) & 0x7) << 8 | block[ip++];
                    CopyWithin(output, op, offset, count, length, member);
                    op += count;
                    break;
                }
                case 2: // copy, 2-byte offset
                {
                    if (ip + 2 > block.Length)
                        throw new ScriptException($"{member}: a \"snappy\" copy offset is truncated.");
                    var count = 1 + (tag >> 2);
                    var offset = block[ip] | block[ip + 1] << 8;
                    ip += 2;
                    CopyWithin(output, op, offset, count, length, member);
                    op += count;
                    break;
                }
                default: // copy, 4-byte offset
                {
                    if (ip + 4 > block.Length)
                        throw new ScriptException($"{member}: a \"snappy\" copy offset is truncated.");
                    var count = 1 + (tag >> 2);
                    var offset = block[ip] | block[ip + 1] << 8 | block[ip + 2] << 16 | block[ip + 3] << 24;
                    ip += 4;
                    CopyWithin(output, op, offset, count, length, member);
                    op += count;
                    break;
                }
            }
        }

        var actual = Crc32(output);
        if (actual != expected)
            throw new ScriptException(
                $"{member}: the \"snappy\" block's CRC-32 does not match its payload (it says {expected:x8}, " +
                $"the bytes say {actual:x8}), so the file is corrupt.");
        return output;
    }

    private static void CopyWithin(byte[] output, int op, int offset, int count, int length, string member)
    {
        if (offset <= 0 || offset > op)
            throw new ScriptException(
                $"{member}: a \"snappy\" copy names offset {offset} at position {op}, which is not behind it.");
        if (op + count > length)
            throw new ScriptException($"{member}: a \"snappy\" copy runs past its declared output.");
        for (var i = 0; i < count; i++) output[op + i] = output[op - offset + i];
    }

    // ── the emitters ───────────────────────────────────────────────────────────────────────────────────

    private static void EmitLiteral(AvroBinaryWriter writer, byte[] input, int start, int count)
    {
        if (count == 0) return;
        var n = count - 1;
        if (n < 60)
        {
            writer.WriteByte((byte)(n << 2));
        }
        else
        {
            var bytes = n < 1 << 8 ? 1 : n < 1 << 16 ? 2 : n < 1 << 24 ? 3 : 4;
            writer.WriteByte((byte)((59 + bytes) << 2));
            for (var i = 0; i < bytes; i++) writer.WriteByte((byte)(n >> (8 * i)));
        }
        writer.WriteRaw(input.AsSpan(start, count));
    }

    private static void EmitCopy(AvroBinaryWriter writer, int offset, int length)
    {
        while (length > 0)
        {
            var chunk = Math.Min(length, 64);
            if (offset < 65536)
            {
                writer.WriteByte((byte)(((chunk - 1) << 2) | 0x2));
                writer.WriteByte((byte)(offset & 0xFF));
                writer.WriteByte((byte)(offset >> 8));
            }
            else
            {
                writer.WriteByte((byte)(((chunk - 1) << 2) | 0x3));
                writer.WriteByte((byte)(offset & 0xFF));
                writer.WriteByte((byte)((offset >> 8) & 0xFF));
                writer.WriteByte((byte)((offset >> 16) & 0xFF));
                writer.WriteByte((byte)((offset >> 24) & 0xFF));
            }
            length -= chunk;
        }
    }

    private static int Hash(byte[] input, int ip)
    {
        var value = (uint)(input[ip] | input[ip + 1] << 8 | input[ip + 2] << 16 | input[ip + 3] << 24);
        return (int)((value * 0x1e35a7bdu) >> 18) & 0x3FFF;
    }

    private static bool Matches4(byte[] input, int a, int b)
        => input[a] == input[b] && input[a + 1] == input[b + 1]
            && input[a + 2] == input[b + 2] && input[a + 3] == input[b + 3];

    private static void WriteVarint(AvroBinaryWriter writer, int value)
    {
        var v = (uint)value;
        while (v >= 0x80)
        {
            writer.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        writer.WriteByte((byte)v);
    }

    private static int ReadVarint(ReadOnlySpan<byte> block, ref int ip, string member)
    {
        var result = 0;
        var shift = 0;
        while (true)
        {
            if (ip >= block.Length)
                throw new ScriptException($"{member}: a \"snappy\" block's length prefix is truncated.");
            var b = block[ip++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift > 28)
                throw new ScriptException($"{member}: a \"snappy\" length prefix is not a 32-bit varint.");
        }
    }

    // ── CRC-32 (IEEE 802.3), the trailer Avro's snappy codec appends ──────────────────────────────────

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
