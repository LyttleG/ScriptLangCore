namespace ScriptLangCore;

/// <summary>The Avro type kinds this engine models. `Reference` is not an Avro kind: it is how a schema that
/// names an already-defined type (a recursive record, or a second use of a named type) points at the ONE model
/// instance, so equals-by-instance is the spec's equals-by-fullname.</summary>
internal enum AvroKind
{
    Null,
    Boolean,
    Int,
    Long,
    Float,
    Double,
    Bytes,
    String,
    Record,
    Enum,
    Array,
    Map,
    Union,
    Fixed
}

/// <summary>One field of a record, in declaration order (which is the order both encodings write).</summary>
internal sealed class AvroField
{
    public required string Name { get; init; }
    public required AvroSchema Type { get; init; }
    public bool HasDefault { get; init; }
    public object? Default { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? Doc { get; init; }
}

/// <summary>An Avro schema, as the spec models it: one node per defined type, and a named type is shared by
/// reference so a recursive record is a cycle in this graph rather than a name to resolve again.</summary>
internal sealed class AvroSchema
{
    public required AvroKind Kind { get; init; }

    /// <summary>The simple name of a named type (record, enum, fixed), else null.</summary>
    public string? Name { get; init; }

    /// <summary>The namespace declared on this node, or inherited from the enclosing named type, else null.</summary>
    public string? Namespace { get; init; }

    /// <summary>The full name (`namespace.name`) a named type is registered and referenced by, else null.</summary>
    public string? FullName { get; init; }

    public IReadOnlyList<string> Aliases { get; init; } = [];
    public string? Doc { get; init; }

    /// <summary>Record fields, in declaration order. Empty for every other kind.</summary>
    public List<AvroField> Fields { get; } = [];

    /// <summary>Enum symbols, in declaration order (their index is what the binary encoding writes).</summary>
    public List<string> Symbols { get; } = [];

    /// <summary>The reader-side fallback symbol for a writer symbol this enum does not declare (1.12).</summary>
    public string? EnumDefault { get; init; }

    /// <summary>The element schema of an array, or the value schema of a map.</summary>
    public AvroSchema? Items { get; init; }
    public AvroSchema? Values { get; init; }

    /// <summary>Union branches, in declaration order: the first one that matches a value is what encoding picks.</summary>
    public List<AvroSchema> Branches { get; } = [];

    /// <summary>The byte size of a fixed.</summary>
    public int FixedSize { get; init; }

    /// <summary>The logical-type annotation (`decimal`, `uuid`, `date`, ...), or null for a plain schema. The
    /// underlying type is still <see cref="Kind"/>, which is what is encoded; this only changes the mapping.</summary>
    public string? LogicalType { get; set; }

    public int Precision { get; set; }
    public int Scale { get; set; }

    public bool IsNamed => Kind is AvroKind.Record or AvroKind.Enum or AvroKind.Fixed;

    public string DisplayName => FullName ?? Name ?? Kind.ToString().ToLowerInvariant();

    public override string ToString() => DisplayName;
}

/// <summary>The options a `Json.Avro` member reads, from a script dictionary, a JSON text or a bool, through ONE
/// reader so two members cannot disagree about a key's spelling or its type.</summary>
internal sealed class AvroOptions
{
    public const int DefaultDecimalPrecision = 18;
    public const int DefaultDecimalScale = 0;

    public string? Namespace { get; private set; }
    public int DecimalPrecision { get; private set; } = DefaultDecimalPrecision;
    public int DecimalScale { get; private set; } = DefaultDecimalScale;
    public string Codec { get; private set; } = "null";
    public IReadOnlyList<string> RecordName { get; private set; } = [];

    /// <summary>Reads the options dictionary. `member` is the spelling a refusal names, so the message says which
    /// call was given the wrong shape rather than "options".</summary>
    internal static AvroOptions Read(object? value, string member)
    {
        var options = new AvroOptions();
        if (value is null) return options;

        var pairs = Pairs(value, member,
            "for example { \"namespace\": \"user\", \"decimalPrecision\": 18 }");

        foreach (var (key, item) in pairs)
        {
            switch (key)
            {
                case "namespace":
                    options.Namespace = item as string
                        ?? throw new ScriptException(
                            $"{member}: \"namespace\" is a string, not '{FriendlyNameOf(item)}'.");
                    break;
                case "decimalPrecision":
                    options.DecimalPrecision = ReadInt(item, "decimalPrecision", member);
                    break;
                case "decimalScale":
                    options.DecimalScale = ReadInt(item, "decimalScale", member);
                    break;
                case "codec":
                    options.Codec = item as string
                        ?? throw new ScriptException(
                            $"{member}: \"codec\" is a string (\"null\", \"deflate\", \"snappy\", " +
                            $"\"zstandard\", \"gzip\" or \"brotli\"), not '{FriendlyNameOf(item)}'.");
                    break;
                case "recordName":
                    options.RecordName = item switch
                    {
                        string one => [one],
                        IScriptList list => AvroNames.NamesFrom(list),
                        _ => throw new ScriptException(
                            $"{member}: \"recordName\" is a string or a list of strings, not '{FriendlyNameOf(item)}'.")
                    };
                    break;
                default:
                    throw new ScriptException(
                        $"{member}: unknown option \"{key}\". The options are namespace, decimalPrecision, " +
                        "decimalScale, codec and recordName.");
            }
        }

        options.Validate(member);
        return options;
    }

    private void Validate(string member)
    {
        if (DecimalPrecision <= 0)
            throw new ScriptException(
                $"{member}: \"decimalPrecision\" must be a positive whole number, not {DecimalPrecision}.");
        if (DecimalScale < 0 || DecimalScale > DecimalPrecision)
            throw new ScriptException(
                $"{member}: \"decimalScale\" is {DecimalScale}, which is outside 0..{DecimalPrecision}: a scale " +
                "larger than the precision cannot represent a value, so the schema would be invalid.");
        if (Codec is not ("null" or "deflate" or "snappy" or "zstandard" or "gzip" or "brotli"))
            throw new ScriptException(
                $"{member}: codec '{Codec}' is not supported. This engine writes \"null\", \"deflate\", " +
                "\"snappy\", \"zstandard\", \"gzip\" and \"brotli\"; bzip2 and xz are refused by name " +
                "rather than written unreadable.");
        if (Namespace is not null && !AvroNames.IsValidNamespace(Namespace))
            throw new ScriptException(
                $"{member}: namespace '{Namespace}' is not a valid Avro namespace (dot-separated names matching " +
                "[A-Za-z_][A-Za-z0-9_]*).");
    }

    /// <summary>The key/value pairs an options value carries, in the FOUR shapes a script can actually hand one
    /// over: the engine's own script dictionary (`[].ToDictionary()` and friends), a `{ }` literal's CLR dictionary,
    /// any other `IDictionary`, and an options JSON TEXT. ONE reader, because the shapes are the engine's and not this
    /// namespace's: `Avro.Schema`, `Avro.WriteContainer` and `Avro.ContainerJson` all take options, and a shape one
    /// accepted and another refused would be a trap of this namespace's own making (measured: a `{ "pretty": true }`
    /// literal is `IDictionary`, not an `IScriptDictionary`).
    ///
    /// `example` is appended to the refusal the caller gets for a value that is no dictionary at all, so the message
    /// can show the keys THAT member knows.</summary>
    internal static List<(string Key, object? Value)> Pairs(object? value, string member, string? example = null)
    {
        switch (value)
        {
            case IScriptDictionary dict:
            {
                var pairs = new List<(string Key, object? Value)>();
                foreach (var key in dict.Keys()) pairs.Add((key.ToString() ?? "", dict[key]));
                return pairs;
            }
            case IDictionary<string, object?> typed:
                return [.. typed.Select(entry => (entry.Key, entry.Value))];
            case System.Collections.IDictionary raw:
            {
                var pairs = new List<(string Key, object? Value)>();
                foreach (var key in raw.Keys) pairs.Add((key?.ToString() ?? "", raw[key!]));
                return pairs;
            }
            case string text:
                return OptionsFromJson(text, member);
            default:
                throw new ScriptException(
                    $"{member} takes an options dictionary" +
                    (example is null ? ", not " : $" ({example}), not ") +
                    $"'{FriendlyNameOf(value)}'.");
        }
    }

    private static int ReadInt(object? value, string key, string member)
    {
        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            _ => throw new ScriptException(
                $"{member}: \"{key}\" is a whole number, not '{FriendlyNameOf(value)}'.")
        };
    }

    private static List<(string Key, object? Value)> OptionsFromJson(string text, string member)
    {
        try
        {
            var parsed = ParseJsonInput(text);
            return parsed switch
            {
                IScriptDictionary dict => [.. dict.Keys().Select(k => (k.ToString() ?? "", dict[k]))],
                _ => throw new ScriptException(
                    $"{member}: the options text must be a JSON object, not {FriendlyNameOf(parsed)}.")
            };
        }
        catch (ScriptException) { throw; }
        catch (Exception ex)
        {
            throw new ScriptException($"{member}: the options text is not valid JSON: {FriendlyExceptionMessage(ex)}");
        }
    }

    /// <summary>The name a refusal uses for a value the caller passed, in script terms: 'int', 'string',
    /// 'List&lt;object&gt;'. A CLR name here would be a name a script cannot write.</summary>
    internal static string FriendlyNameOf(object? value) => value switch
    {
        null => "null",
        string => "string",
        bool => "bool",
        int or long or short or byte or sbyte or ushort or uint or ulong => "a whole number",
        float or double or decimal => "a number",
        IScriptList => "a list",
        IScriptDictionary => "a dictionary",
        // A DICTIONARY LITERAL in a script is a CLR Dictionary, not an IScriptDictionary (measured: `{ "x": 1 }`
        // reports `Dictionary<object, object>`), so without these arms a refusal about one said "Dictionary`2",
        // a name no script can write and which leaks the generic arity as well.
        IDictionary<string, object?> => "a dictionary",
        System.Collections.IDictionary => "a dictionary",
        ClassInstance ci => ci.Class.Name,
        StructInstance si => si.StructName,
        _ => ScriptNameOf(value.GetType())
    };

    /// <summary>A CLR type's name with any generic arity suffix removed, which is the least a refusal can say about
    /// a value this engine has no script word for: `Dictionary`2` becomes `Dictionary`, never a backtick name.</summary>
    private static string ScriptNameOf(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick > 0 ? name[..tick] : name;
    }
}

/// <summary>The Avro naming rules, stated once: a name matches [A-Za-z_][A-Za-z0-9_]*, a namespace is
/// dot-separated names, and a full name is a namespace plus a name. 1.12 accepts an ALIAS that is not a valid
/// name, on purpose (it is how an old illegal name is carried), so aliases are checked for uniqueness only.</summary>
internal static class AvroNames
{
    internal static bool IsValidName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) return false;
        if (!(char.IsAsciiLetter(name[0]) || name[0] == '_')) return false;
        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    internal static bool IsValidNamespace(string ns)
    {
        if (ns.Length == 0) return false;
        foreach (var part in ns.Split('.'))
            if (!IsValidName(part)) return false;
        return true;
    }

    /// <summary>The names of a script list, for the `recordName` option (one name per generated record, in order).</summary>
    internal static IReadOnlyList<string> NamesFrom(IScriptList list)
    {
        var names = new List<string>(list.Count);
        foreach (var item in list.ToArray()) names.Add(item?.ToString() ?? "");
        return names;
    }

    internal static bool IsPrimitive(string name) => name switch
    {
        "null" or "boolean" or "int" or "long" or "float" or "double" or "bytes" or "string" => true,
        _ => false
    };

    internal static AvroKind PrimitiveKind(string name) => name switch
    {
        "null" => AvroKind.Null,
        "boolean" => AvroKind.Boolean,
        "int" => AvroKind.Int,
        "long" => AvroKind.Long,
        "float" => AvroKind.Float,
        "double" => AvroKind.Double,
        "bytes" => AvroKind.Bytes,
        _ => AvroKind.String
    };
}
