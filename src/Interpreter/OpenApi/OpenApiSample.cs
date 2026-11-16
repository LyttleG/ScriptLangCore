namespace ScriptLangCore;

/// <summary>What one value of a sample is, as inference needs it. Nullability is data here rather than a kind,
/// so <see cref="OpenApiSampleKind"/> has no <c>Null</c> member: a null sample is the DEFAULT value, which is
/// what makes <c>default</c> a null sample everywhere it is convenient.</summary>
internal enum OpenApiSampleKind { Null, Boolean, Integer, Number, String, Array, Object }

/// <summary>One sample value, normalised so that inference has ONE shape to walk whatever the caller handed over:
/// JSON text, UTF-8 bytes, an already-parsed value (<c>Json.Parse</c>), or any other value the engine can
/// serialize. Objects keep their members in DOCUMENT ORDER, which is what makes the emitted schema's property
/// order deterministic; a value read from a <c>JsonElement</c> is exact there, while one read from a dictionary
/// keeps the dictionary's own enumeration order (which for the engine's dictionaries is insertion order, and is
/// the best a dictionary can promise).</summary>
internal readonly struct OpenApiSample
{
    internal OpenApiSampleKind Kind { get; init; }
    internal bool Boolean { get; init; }
    internal long Integer { get; init; }
    internal double Number { get; init; }
    internal string? Text { get; init; }
    internal IReadOnlyList<OpenApiSample>? Items { get; init; }
    internal IReadOnlyList<KeyValuePair<string, OpenApiSample>>? Members { get; init; }
}

/// <summary>The three doors onto a sample, and the ONE normalisation they share.</summary>
internal static class OpenApiSampleReader
{
    /// <summary>A string is the JSON TEXT (the usual case, a captured API response); a <c>byte[]</c> is that
    /// text in UTF-8, which is the same reading <c>Json.Deserialize</c> gives it. Anything else is a VALUE:
    /// the engine's own structures are walked, and any other object is read as what
    /// <c>Json.Serialize</c> would write for it, which is the only definition of "the JSON this value is"
    /// that cannot drift from the serializer.</summary>
    internal static OpenApiSample Read(object? sample, string member)
    {
        if (sample is string text) return FromText(text, member);
        if (sample is byte[] bytes) return FromBytes(bytes, member);
        return FromValue(sample, member);
    }

    private static OpenApiSample FromText(string text, string member)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ScriptException($"{member}: {FriendlyExceptionMessage(ex)}");
        }

        using (document) return FromElement(document.RootElement);
    }

    private static OpenApiSample FromBytes(byte[] bytes, string member)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes));
        }
        catch (JsonException ex)
        {
            throw new ScriptException($"{member}: {FriendlyExceptionMessage(ex)}");
        }

        using (document) return FromElement(document.RootElement);
    }

    private static OpenApiSample FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var members = new List<KeyValuePair<string, OpenApiSample>>();
                    foreach (var property in element.EnumerateObject())
                        members.Add(new(property.Name, FromElement(property.Value)));
                    return new OpenApiSample { Kind = OpenApiSampleKind.Object, Members = members };
                }
            case JsonValueKind.Array:
                {
                    var items = new List<OpenApiSample>();
                    foreach (var item in element.EnumerateArray()) items.Add(FromElement(item));
                    return new OpenApiSample { Kind = OpenApiSampleKind.Array, Items = items };
                }
            case JsonValueKind.String:
                return new OpenApiSample { Kind = OpenApiSampleKind.String, Text = element.GetString() ?? "" };
            case JsonValueKind.Number:
                // Whole numbers are 64-bit here, which is the widest a JSON number can be before it stops being
                // exact; the schema's int32/int64 choice is made from the VALUE, since the parse has already
                // erased any narrower width the producer wrote.
                return element.TryGetInt64(out var integer)
                    ? new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = integer }
                    : new OpenApiSample { Kind = OpenApiSampleKind.Number, Number = element.GetDouble() };
            case JsonValueKind.True:
                return new OpenApiSample { Kind = OpenApiSampleKind.Boolean, Boolean = true };
            case JsonValueKind.False:
                return new OpenApiSample { Kind = OpenApiSampleKind.Boolean, Boolean = false };
            default:
                return default;
        }
    }

    private static OpenApiSample FromValue(object? value, string member)
    {
        switch (value)
        {
            case null: return default;
            case string text: return new OpenApiSample { Kind = OpenApiSampleKind.String, Text = text };
            case char character: return new OpenApiSample { Kind = OpenApiSampleKind.String, Text = character.ToString() };
            case bool flag: return new OpenApiSample { Kind = OpenApiSampleKind.Boolean, Boolean = flag };
            case long l: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = l };
            case int i: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = i };
            case short s: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = s };
            case byte b: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = b };
            case sbyte sb: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = sb };
            case ushort us: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = us };
            case uint ui: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = ui };
            case ulong ul when ul <= long.MaxValue: return new OpenApiSample { Kind = OpenApiSampleKind.Integer, Integer = (long)ul };
            case ulong ul: return new OpenApiSample { Kind = OpenApiSampleKind.Number, Number = ul };
            case double d: return new OpenApiSample { Kind = OpenApiSampleKind.Number, Number = d };
            case float f: return new OpenApiSample { Kind = OpenApiSampleKind.Number, Number = f };
            case decimal m: return new OpenApiSample { Kind = OpenApiSampleKind.Number, Number = (double)m };
            case IScriptDictionary dictionary: return FromScriptDictionary(dictionary, member);
            case IDictionary table: return FromRawDictionary(table, member);
            case IEnumerable sequence: return FromSequence(sequence, member);
            default:
                return FromText(SerializeToText(value), member);
        }
    }

    private static OpenApiSample FromScriptDictionary(IScriptDictionary dictionary, string member)
    {
        var keys = dictionary.Keys();
        var members = new List<KeyValuePair<string, OpenApiSample>>(keys.Length);
        foreach (var key in keys)
            members.Add(new(key as string ?? key.ToString() ?? "", FromValue(dictionary[key], member)));
        return new OpenApiSample { Kind = OpenApiSampleKind.Object, Members = members };
    }

    private static OpenApiSample FromRawDictionary(IDictionary table, string member)
    {
        var members = new List<KeyValuePair<string, OpenApiSample>>(table.Count);
        foreach (DictionaryEntry entry in table)
            members.Add(new(entry.Key as string ?? entry.Key?.ToString() ?? "", FromValue(entry.Value, member)));
        return new OpenApiSample { Kind = OpenApiSampleKind.Object, Members = members };
    }

    private static OpenApiSample FromSequence(IEnumerable sequence, string member)
    {
        var items = new List<OpenApiSample>();
        foreach (var item in sequence) items.Add(FromValue(item, member));
        return new OpenApiSample { Kind = OpenApiSampleKind.Array, Items = items };
    }
}
