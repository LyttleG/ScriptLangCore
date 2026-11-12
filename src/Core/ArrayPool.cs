namespace ScriptLangCore;

/// <summary>Simple thread-local pool for small object?[] arrays used as argument buffers.
/// Reduces GC pressure from the many short-lived arrays allocated per script call.</summary>
internal static class ArrayPool
{
    [ThreadStatic] private static SmallArrayPool? _pool;

    private class SmallArrayPool
    {
        // One slot per common size: 0,1,2,3,4,5,6,7,8
        internal readonly object?[]?[] Slots = new object?[9][];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object?[] Rent(int size)
    {
        var pool = _pool;
        if (pool == null)
        {
            pool = new SmallArrayPool();
            _pool = pool;
        }

        if ((uint)size > 8) return new object?[size];
        var arr = pool.Slots[size];
        if (arr == null) return new object?[size];
        pool.Slots[size] = null;
        return arr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Return(object?[] arr)
    {
        var size = arr.Length;
        if ((uint)size > 8) return;
        var pool = _pool ??= new SmallArrayPool();
        if (pool.Slots[size] != null) return;
        Array.Clear(arr, 0, size);
        pool.Slots[size] = arr;
    }
}
