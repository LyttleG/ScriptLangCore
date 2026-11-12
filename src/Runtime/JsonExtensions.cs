namespace ScriptLangCore;

internal static class JsonParseExtensions
{
    public static object? ParseJson(byte[] utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        return !reader.Read()
            ? throw new ScriptException("Empty or invalid JSON.")
            : ReadValue(ref reader);
    }

    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => GetNumber(ref reader),
            JsonTokenType.StartObject => ReadObject(ref reader),
            JsonTokenType.StartArray => ReadArray(ref reader),
            _ => throw new ScriptException($"Unexpected token: {reader.TokenType}")
        };

        static Dictionary<string, object?> ReadObject(ref Utf8JsonReader reader)
        {
            var dict = new Dictionary<string, object?>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return dict;

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new ScriptException("Expected property name.");

                var key = reader.GetString()!;
                if (!reader.Read())
                    throw new ScriptException("Unexpected end of JSON object.");
                var value = ReadValue(ref reader);
                dict[key] = value;
            }
            throw new ScriptException("Unterminated JSON object.");
        }
    }

    private static object GetNumber(ref Utf8JsonReader reader)
    {
        if (reader.TryGetInt64(out var l))
            return l;
        if (reader.TryGetDouble(out var d))
            return d;
        return decimal.Parse(Encoding.UTF8.GetString(reader.ValueSpan), CultureInfo.InvariantCulture);
    }

    private static List<object?> ReadArray(ref Utf8JsonReader reader)
    {
        var list = new List<object?>(64);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return list;

            var value = ReadValue(ref reader);
            list.Add(value);
        }
        throw new ScriptException("Unterminated JSON array.");
    }
}
