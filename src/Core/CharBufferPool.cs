namespace ScriptLangCore;

/// <summary>Thread-local pool for char[] buffers used during file-to-lexer conversion.
/// Reduces GC pressure from File.ReadAllText allocations.</summary>
internal static class CharBufferPool
{
    [ThreadStatic] private static List<char[]>? _pool;
    private const int MaxPoolSize = 8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char[] Rent(int minimumLength)
    {
        var pool = _pool;
        if (pool != null)
        {
            for (var i = 0; i < pool.Count; i++)
            {
                if (pool[i].Length < minimumLength) continue;
                var buf = pool[i];
                pool.RemoveAt(i);
                return buf;
            }
        }
        // Round up to next power of 2 for better reuse
        var size = minimumLength;
        size--;
        size |= size >> 1; size |= size >> 2; size |= size >> 4;
        size |= size >> 8; size |= size >> 16;
        return new char[++size];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(char[] buffer)
    {
        var pool = _pool;
        if (pool == null)
        {
            _pool = [with(MaxPoolSize)];
            pool = _pool;
        }
        if (pool.Count < MaxPoolSize && !pool.Contains(buffer))
            pool.Add(buffer);
    }
}
