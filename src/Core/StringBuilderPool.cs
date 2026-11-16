namespace ScriptLangCore;

/// <summary>Thread-local pool for StringBuilder instances. Reduces allocations in hot code paths.</summary>
internal static class StringBuilderPool
{
    [ThreadStatic] private static StringBuilder? _shared;
    private const int DefaultCapacity = 256;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringBuilder Rent()
    {
        var sb = _shared ??= new StringBuilder(DefaultCapacity);
        sb.Clear();
        return sb;
    }

}
