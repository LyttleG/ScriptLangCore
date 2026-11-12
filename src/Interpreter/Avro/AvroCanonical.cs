using System.Security.Cryptography;
using System.Text;

namespace ScriptLangCore;

/// <summary>The Parsing Canonical Form and the three fingerprints the spec defines. Canonical form is what makes
/// "the same schema" decidable, so it is written once here and every fingerprint is taken over it.
///
/// The transformations, in the spec's own names: PRIMITIVES (a bare type name stays a quoted name),
/// FULLNAMES (a short name becomes the full name, `namespace` disappears), STRIP (only type, name, fields,
/// symbols, items, values, size survive; doc, aliases, default, order and logicalType are dropped), ORDER
/// (name, then type, then the rest), STRINGS (escapes become UTF-8), INTEGERS (no quotes or leading zeros),
/// WHITESPACE (none outside strings).</summary>
internal static class AvroCanonical
{
    /// <summary>The canonical text of a schema, which two schemas share exactly when they are "the same" for a
    /// reader.</summary>
    internal static string Form(AvroSchema schema)
    {
        var builder = new StringBuilder(128);
        Write(builder, schema, new HashSet<AvroSchema>(ReferenceEqualityComparer.Instance));
        return builder.ToString();
    }

    /// <summary>A fingerprint of the canonical form, as lowercase hex. `algorithm` accepts the spec's names with
    /// any dash or case spelling ("crc-64-avro", "crc64", "SHA-256", "md5").</summary>
    internal static string Fingerprint(AvroSchema schema, string algorithm, string member)
    {
        var canonical = Form(schema);
        var normalized = Normalize(algorithm);
        switch (normalized)
        {
            case "crc64avro":
            case "rabin":
                return Fingerprint64(canonical).ToString("x16");
            case "sha256":
            {
                var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
                return Convert.ToHexStringLower(hash);
            }
            case "md5":
            {
                // Avro's 128-bit fingerprint IS MD5 by definition, so this is the protocol's choice rather than a
                // security decision (the same reason the WebSocket handshake keeps SHA1). A fingerprint identifies
                // a schema; it authenticates nothing, and the default algorithm here is crc-64-avro.
#pragma warning disable CA5351
                var hash = MD5.HashData(Encoding.UTF8.GetBytes(canonical));
#pragma warning restore CA5351
                return Convert.ToHexStringLower(hash);
            }
            default:
                throw new ScriptException(
                    $"{member}: '{algorithm}' is not a fingerprint algorithm. The three the spec defines are " +
                    "\"crc-64-avro\" (the default), \"sha-256\" and \"md5\".");
        }
    }

    /// <summary>The 64-bit Rabin fingerprint (CRC-64-AVRO), which is what a single-object encoding carries.</summary>
    internal static ulong Fingerprint64(AvroSchema schema) => Fingerprint64(Form(schema));

    internal static ulong Fingerprint64(string canonical)
    {
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var fingerprint = Empty;
        foreach (var b in bytes)
            fingerprint = (fingerprint >> 8) ^ Table[(int)((fingerprint ^ b) & 0xff)];
        return fingerprint;
    }

    private static string Normalize(string algorithm)
    {
        Span<char> buffer = stackalloc char[algorithm.Length];
        var length = 0;
        foreach (var c in algorithm)
        {
            if (c is '-' or '_' or ' ') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer[..length]);
    }

    // The spec's one constant for CRC-64-AVRO: it is BOTH the initial value and the polynomial the table is
    // built from, which is why a table is all this needs.
    private const ulong Empty = 0xc15d213aa4d7a795UL;

    private static readonly ulong[] Table = BuildTable();

    private static ulong[] BuildTable()
    {
        var table = new ulong[256];
        for (var i = 0; i < 256; i++)
        {
            var fingerprint = (ulong)i;
            for (var bit = 0; bit < 8; bit++)
                fingerprint = (fingerprint >> 1) ^ (Empty & (ulong)-(long)(fingerprint & 1));
            table[i] = fingerprint;
        }
        return table;
    }

    private static void Write(StringBuilder builder, AvroSchema schema, HashSet<AvroSchema> opened)
    {
        switch (schema.Kind)
        {
            case AvroKind.Null:
            case AvroKind.Boolean:
            case AvroKind.Int:
            case AvroKind.Long:
            case AvroKind.Float:
            case AvroKind.Double:
            case AvroKind.Bytes:
            case AvroKind.String:
                AppendQuoted(builder, PrimitiveName(schema.Kind));
                return;

            case AvroKind.Union:
                builder.Append('[');
                for (var i = 0; i < schema.Branches.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Write(builder, schema.Branches[i], opened);
                }
                builder.Append(']');
                return;

            case AvroKind.Array:
                builder.Append("{\"type\":\"array\",\"items\":");
                Write(builder, schema.Items!, opened);
                builder.Append('}');
                return;

            case AvroKind.Map:
                builder.Append("{\"type\":\"map\",\"values\":");
                Write(builder, schema.Values!, opened);
                builder.Append('}');
                return;

            case AvroKind.Record:
            {
                // A self-referencing record: the first visit writes the definition, later ones would write it
                // again, which is not canonical. Avro's canonical form is a TREE of definitions, and the spec's
                // own canonicaliser writes each named type once, so a repeat is written by name.
                if (!opened.Add(schema))
                {
                    AppendQuoted(builder, schema.FullName ?? schema.Name ?? "record");
                    return;
                }

                builder.Append("{\"name\":");
                AppendQuoted(builder, schema.FullName ?? schema.Name ?? "record");
                builder.Append(",\"type\":\"record\",\"fields\":[");
                for (var i = 0; i < schema.Fields.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append("{\"name\":");
                    AppendQuoted(builder, schema.Fields[i].Name);
                    builder.Append(",\"type\":");
                    Write(builder, schema.Fields[i].Type, opened);
                    builder.Append('}');
                }
                builder.Append("]}");
                return;
            }

            case AvroKind.Enum:
                builder.Append("{\"name\":");
                AppendQuoted(builder, schema.FullName ?? schema.Name ?? "enum");
                builder.Append(",\"type\":\"enum\",\"symbols\":[");
                for (var i = 0; i < schema.Symbols.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    AppendQuoted(builder, schema.Symbols[i]);
                }
                builder.Append("]}");
                return;

            case AvroKind.Fixed:
                builder.Append("{\"name\":");
                AppendQuoted(builder, schema.FullName ?? schema.Name ?? "fixed");
                builder.Append(",\"type\":\"fixed\",\"size\":");
                builder.Append(schema.FixedSize);
                builder.Append('}');
                return;
        }
    }

    private static string PrimitiveName(AvroKind kind) => kind switch
    {
        AvroKind.Null => "null",
        AvroKind.Boolean => "boolean",
        AvroKind.Int => "int",
        AvroKind.Long => "long",
        AvroKind.Float => "float",
        AvroKind.Double => "double",
        AvroKind.Bytes => "bytes",
        _ => "string"
    };

    /// <summary>A JSON string, with the escapes canonical form requires: only the characters JSON cannot hold
    /// literally are escaped, and everything else is written as UTF-8 (the spec's STRINGS rule).</summary>
    private static void AppendQuoted(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
    }
}
