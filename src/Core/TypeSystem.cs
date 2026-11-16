namespace ScriptLangCore;

/// <summary>Central registry for type-system mappings. Single source of truth.</summary>
internal static class TypeSystem
{
    // ════════════════════════════════════════════════════════════════════
    // TokenType → script keyword
    // ════════════════════════════════════════════════════════════════════
    public static string Name(TokenType t) => t switch
    {
        TokenType.Int => "int",
        TokenType.Long => "long",
        TokenType.Short => "short",
        TokenType.Byte => "byte",
        TokenType.UInt => "uint",
        TokenType.UShort => "ushort",
        TokenType.SByte => "sbyte",
        TokenType.ULong => "ulong",
        TokenType.Float => "float",
        TokenType.Double => "double",
        TokenType.Decimal => "decimal",
        TokenType.Bool => "bool",
        TokenType.Char => "char",
        TokenType.StringType => "string",
        TokenType.Object => "object",
        TokenType.Void => "void",
        TokenType.DateTime => "DateTime",
        TokenType.DateTimeOffset => "DateTimeOffset",
        TokenType.TimeSpan => "TimeSpan",
        _ => t.ToString()
    };

    // ════════════════════════════════════════════════════════════════════
    // CLR Type → TokenType
    // ════════════════════════════════════════════════════════════════════
    public static TokenType TokenTypeOf(Type t) => TypeMap.GetValueOrDefault(t, TokenType.Object);

    private static readonly Dictionary<Type, TokenType> TypeMap = new()
    {
        [typeof(void)] = TokenType.Void,
        [typeof(byte)] = TokenType.Byte,
        [typeof(short)] = TokenType.Short,
        [typeof(int)] = TokenType.Int,
        [typeof(long)] = TokenType.Long,
        [typeof(uint)] = TokenType.UInt,
        [typeof(ushort)] = TokenType.UShort,
        [typeof(sbyte)] = TokenType.SByte,
        [typeof(ulong)] = TokenType.ULong,
        [typeof(float)] = TokenType.Float,
        [typeof(double)] = TokenType.Double,
        [typeof(decimal)] = TokenType.Decimal,
        [typeof(bool)] = TokenType.Bool,
        [typeof(char)] = TokenType.Char,
        [typeof(string)] = TokenType.StringType,
        [typeof(object)] = TokenType.Object,
        [typeof(DateTime)] = TokenType.DateTime,
        [typeof(DateTimeOffset)] = TokenType.DateTimeOffset,
        [typeof(TimeSpan)] = TokenType.TimeSpan,
    };

    // ════════════════════════════════════════════════════════════════════
    // CLR type-name string → script keyword (e.g. "Int32"→"int")
    // ════════════════════════════════════════════════════════════════════
    public static string FromClrName(string name) => name switch
    {
        "Byte" => "byte",
        "SByte" => "sbyte",
        "Short" or "Int16" => "short",
        "UShort" or "UInt16" => "ushort",
        "Int" or "Int32" => "int",
        "UInt" or "UInt32" => "uint",
        "Long" or "Int64" => "long",
        "ULong" or "UInt64" => "ulong",
        "Float" or "Single" => "float",
        "Double" => "double",
        "Decimal" => "decimal",
        "Bool" or "Boolean" => "bool",
        "Char" => "char",
        "String" => "string",
        "Object" => "object",
        "Void" => "void",
        "StringType" => "string",
        "DateTime" => "DateTime",
        "TimeSpan" => "TimeSpan",
        "DateTimeOffset" => "DateTimeOffset",
        _ => name
    };

    // ════════════════════════════════════════════════════════════════════
    // TokenType queries
    // ════════════════════════════════════════════════════════════════════
    public static bool IsNumeric(TokenType t) => t switch
    {
        TokenType.Byte or TokenType.SByte or TokenType.Short or TokenType.UShort
        or TokenType.Int or TokenType.UInt or TokenType.Long or TokenType.ULong
        or TokenType.Float or TokenType.Double or TokenType.Decimal => true,
        _ => false
    };

    public static bool IsBaseType(TokenType t) => IsNumeric(t) || t switch
    {
        TokenType.Bool or TokenType.Char or TokenType.StringType or TokenType.Object
        or TokenType.DateTime or TokenType.DateTimeOffset or TokenType.TimeSpan
        or TokenType.Void => true,
        _ => false
    };

    // ════════════════════════════════════════════════════════════════════
    // Cast compatibility, can value of sourceType be cast to targetType?
    // ════════════════════════════════════════════════════════════════════
    public static bool CanConvert(TokenType source, TokenType target)
    {
        if (source == target) return true;
        if (source == TokenType.Object || target == TokenType.Object) return true;

        var sourceNumeric = IsNumeric(source);
        var targetNumeric = IsNumeric(target);

        if (source == TokenType.StringType)
            return targetNumeric || target is TokenType.Bool or TokenType.Char or TokenType.StringType;

        if (target == TokenType.StringType)
            return sourceNumeric || source is TokenType.Bool or TokenType.Char;

        if (sourceNumeric && (targetNumeric || target is TokenType.Bool or TokenType.Char))
            return true;

        if (source == TokenType.Bool && targetNumeric) return true;
        if (source == TokenType.Char && (targetNumeric || target == TokenType.Char)) return true;

        return false;
    }
}
