namespace ScriptLangCore;

/// <summary>Thread-local pool for small Dictionary{string, object?} instances.
/// Reduces GC pressure from scope dictionary allocations per function call.</summary>
internal static class DictionaryPool
{
    [ThreadStatic] private static List<Dictionary<string, object?>>? _pool;
    private const int MaxPoolSize = 16;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<string, object?> Rent()
    {
        var pool = _pool;
        if (pool is { Count: > 0 })
        {
            var last = pool.Count - 1;
            var d = pool[last];
            pool.RemoveAt(last);
            return d;
        }
        return new Dictionary<string, object?>(8);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Dictionary<string, object?> Rent(int capacity)
    {
        var pool = _pool;
        if (pool is { Count: > 0 })
        {
            var last = pool.Count - 1;
            var d = pool[last];
            pool.RemoveAt(last);
            return d;
        }
        return new Dictionary<string, object?>(capacity);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(Dictionary<string, object?> dict)
    {
        dict.Clear();
        var pool = _pool;
        if (pool == null)
        {
            _pool = [with(MaxPoolSize)];
            pool = _pool;
        }
        if (pool.Count < MaxPoolSize)
            pool.Add(dict);
    }
}
