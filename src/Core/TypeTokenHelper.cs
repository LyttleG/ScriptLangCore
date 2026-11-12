namespace ScriptLangCore;

internal static class TypeTokenHelper
{
    private static readonly string[] TokenTypeNames = Enum.GetNames<TokenType>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetTypeName(TokenType type) => TokenTypeNames[(int)type];
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification = "Runtime type resolution required for dynamic language")]
    public static Type GetSystemType(TypeInfo typeInfo)
    {
        Type? elementType;

        // If this is a custom named type (class, delegate, struct), return object
        if (!string.IsNullOrEmpty(typeInfo.TypeName))
        {
            elementType = typeof(object);
            for (var i = 0; i < typeInfo.JaggedDepth; i++)
                elementType = elementType.MakeArrayType();
            if (typeInfo.ArrayRank > 0)
                elementType = elementType.MakeArrayType(typeInfo.ArrayRank);
            return elementType;
        }

        // Original base type mapping
        elementType = typeInfo.BaseType switch
        {
            TokenType.Byte => typeof(byte),
            TokenType.Short => typeof(short),
            TokenType.Int => typeof(int),
            TokenType.Long => typeof(long),
            TokenType.UInt => typeof(uint),
            TokenType.UShort => typeof(ushort),
            TokenType.SByte => typeof(sbyte),
            TokenType.ULong => typeof(ulong),
            TokenType.Float => typeof(float),
            TokenType.Double => typeof(double),
            TokenType.Decimal => typeof(decimal),
            TokenType.Bool => typeof(bool),
            TokenType.Char => typeof(char),
            TokenType.StringType or TokenType.String => typeof(string),
            TokenType.Object => typeof(object),
            TokenType.DateTime => typeof(DateTime),
            TokenType.DateTimeOffset => typeof(DateTimeOffset),
            TokenType.TimeSpan => typeof(TimeSpan),
            _ => throw new ArgumentException($"Unsupported base type {typeInfo.BaseType}")
        };

        // Handle arrays and nullable
        for (var i = 0; i < typeInfo.JaggedDepth; i++)
            elementType = elementType.MakeArrayType();

        if (typeInfo.ArrayRank > 0)
            elementType = elementType.MakeArrayType(typeInfo.ArrayRank);

        if (typeInfo is { IsNullable: true, IsArray: false } && elementType.IsValueType)
            elementType = typeof(Nullable<>).MakeGenericType(elementType);

        return elementType;
    }

    public static bool IsBaseType(TokenType t) => TypeSystem.IsBaseType(t);

    /// <summary>The name a SCRIPT writes for a TypeInfo that holds no written name, i.e. a primitive: the
    /// lowercase spelling the engine reports, never the token table's ENUM member name ("Object", "StringType",
    /// "Int"). Both engines need it in the same breath.
    ///
    /// The compiled `new` carries it into InstantiateForVm, where the type-name guard compares the name against
    /// the registered spelling: carrying "Object" made the guard REFUSE `new object()` and tell the author to
    /// write what they had already written. The tree's `new` resolves it as a TYPE NAME, and its CLR fallback is
    /// `Type.GetType("System." + name)`, so the enum names worked exactly where they coincide with the CLR simple
    /// name (System.Object / Char / Byte / SByte / Double / Decimal exist; System.Int / Bool / Long / Float do
    /// not, the CLR says Int32 / Boolean / Int64 / Single), which is why `new int()` failed while
    /// `new double()` worked (measured, both engines, 2026-10-06).
    ///
    /// A name the script WROTE is returned untouched by the caller, so this is only the fallback. Void is
    /// excluded because it is the one base type GetSystemType does not map, and nothing constructs it.</summary>
    public static string ScriptNameOf(TypeInfo ti)
        => TypeSystem.IsBaseType(ti.BaseType) && ti.BaseType != TokenType.Void
            ? Interpreter.GetFriendlyTypeName(GetSystemType(ti))
            : GetTypeName(ti.BaseType);
}
