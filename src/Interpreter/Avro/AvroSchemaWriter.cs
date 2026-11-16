using System.Text;

namespace ScriptLangCore;

/// <summary>Writes a parsed schema back out as the .avsc JSON a script (or a container file's `avro.schema`
/// metadata) can carry. This is NOT the canonical form: it keeps what canonical form strips, i.e. defaults,
/// aliases, docs and the logical-type annotation, so a schema read from a file and written back still describes
/// the same data. A named type is DEFINED on its first visit and written by FULL NAME afterwards, which is how
/// Avro's JSON form stays finite for a recursive record.</summary>
internal static class AvroSchemaWriter
{
    internal static string ToJson(AvroSchema schema)
    {
        var builder = new StringBuilder(256);
        Write(builder, schema, new HashSet<AvroSchema>(ReferenceEqualityComparer.Instance));
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, AvroSchema schema, HashSet<AvroSchema> written)
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
                // A logical type needs the object form, because the annotation has nowhere to live on a bare name.
                if (schema.LogicalType is not { } logical)
                {
                    Quote(builder, PrimitiveName(schema.Kind));
                    return;
                }
                builder.Append("{\"type\":");
                Quote(builder, PrimitiveName(schema.Kind));
                AppendLogical(builder, schema, logical);
                builder.Append('}');
                return;

            case AvroKind.Union:
                builder.Append('[');
                for (var i = 0; i < schema.Branches.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Write(builder, schema.Branches[i], written);
                }
                builder.Append(']');
                return;

            case AvroKind.Array:
                builder.Append("{\"type\":\"array\",\"items\":");
                Write(builder, schema.Items!, written);
                builder.Append('}');
                return;

            case AvroKind.Map:
                builder.Append("{\"type\":\"map\",\"values\":");
                Write(builder, schema.Values!, written);
                builder.Append('}');
                return;

            case AvroKind.Record:
            {
                if (!written.Add(schema))
                {
                    Quote(builder, schema.FullName ?? schema.Name ?? "record");
                    return;
                }

                builder.Append("{\"type\":\"record\",\"name\":");
                Quote(builder, schema.Name ?? "record");
                if (schema.Namespace is { } ns)
                {
                    builder.Append(",\"namespace\":");
                    Quote(builder, ns);
                }
                AppendAliases(builder, schema);
                if (schema.Doc is { } doc)
                {
                    builder.Append(",\"doc\":");
                    Quote(builder, doc);
                }
                builder.Append(",\"fields\":[");
                for (var i = 0; i < schema.Fields.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    var field = schema.Fields[i];
                    builder.Append("{\"name\":");
                    Quote(builder, field.Name);
                    builder.Append(",\"type\":");
                    Write(builder, field.Type, written);
                    if (field.HasDefault)
                    {
                        builder.Append(",\"default\":");
                        builder.Append(Interpreter.SerializeToText(field.Default));
                    }
                    if (field.Aliases.Count > 0)
                    {
                        builder.Append(",\"aliases\":[");
                        for (var a = 0; a < field.Aliases.Count; a++)
                        {
                            if (a > 0) builder.Append(',');
                            Quote(builder, field.Aliases[a]);
                        }
                        builder.Append(']');
                    }
                    if (field.Doc is { } fieldDoc)
                    {
                        builder.Append(",\"doc\":");
                        Quote(builder, fieldDoc);
                    }
                    builder.Append('}');
                }
                builder.Append("]}");
                return;
            }

            case AvroKind.Enum:
            {
                if (!written.Add(schema))
                {
                    Quote(builder, schema.FullName ?? schema.Name ?? "enum");
                    return;
                }

                builder.Append("{\"type\":\"enum\",\"name\":");
                Quote(builder, schema.Name ?? "enum");
                if (schema.Namespace is { } enumNs)
                {
                    builder.Append(",\"namespace\":");
                    Quote(builder, enumNs);
                }
                AppendAliases(builder, schema);
                builder.Append(",\"symbols\":[");
                for (var i = 0; i < schema.Symbols.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    Quote(builder, schema.Symbols[i]);
                }
                builder.Append(']');
                if (schema.EnumDefault is { } enumDefault)
                {
                    builder.Append(",\"default\":");
                    Quote(builder, enumDefault);
                }
                builder.Append('}');
                return;
            }

            case AvroKind.Fixed:
            {
                if (!written.Add(schema))
                {
                    Quote(builder, schema.FullName ?? schema.Name ?? "fixed");
                    return;
                }

                builder.Append("{\"type\":\"fixed\",\"name\":");
                Quote(builder, schema.Name ?? "fixed");
                if (schema.Namespace is { } fixedNs)
                {
                    builder.Append(",\"namespace\":");
                    Quote(builder, fixedNs);
                }
                builder.Append(",\"size\":").Append(schema.FixedSize);
                AppendAliases(builder, schema);
                if (schema.LogicalType is { } fixedLogical)
                    AppendLogical(builder, schema, fixedLogical);
                builder.Append('}');
                return;
            }
        }
    }

    private static void AppendLogical(StringBuilder builder, AvroSchema schema, string logical)
    {
        builder.Append(",\"logicalType\":");
        Quote(builder, logical);
        if (logical != "decimal") return;
        builder.Append(",\"precision\":").Append(schema.Precision);
        builder.Append(",\"scale\":").Append(schema.Scale);
    }

    private static void AppendAliases(StringBuilder builder, AvroSchema schema)
    {
        if (schema.Aliases.Count == 0) return;
        builder.Append(",\"aliases\":[");
        for (var i = 0; i < schema.Aliases.Count; i++)
        {
            if (i > 0) builder.Append(',');
            Quote(builder, schema.Aliases[i]);
        }
        builder.Append(']');
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

    private static void Quote(StringBuilder builder, string text)
    {
        builder.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
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
