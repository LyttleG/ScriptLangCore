namespace ScriptLangCore;

/// <summary>
/// Thread-static pool of reusable module interpreter instances.
/// Module interpreters share the same BuiltinBlueprint, we avoid
/// Dispose/construction overhead by reusing them across imports.
/// </summary>
internal static class InterpreterPool
{
    [ThreadStatic]
    private static Stack<Interpreter>? _pool;

    private static Stack<Interpreter> Pool => _pool ??= new Stack<Interpreter>(16);

    internal static Interpreter Rent(bool caseSensitive)
    {
        if (!Pool.TryPop(out var itp))
            return new Interpreter(caseSensitive);

        // Clear stale module type registrations and reset scope
        itp.Classes.Clear();
        itp.DelegateInfos.Clear();
        itp.Structs.Clear();
        itp.ResetModuleScope();
        BuiltinBlueprint.CloneInto(itp, clearTypes: true);

        // Per-run state the light path above does NOT clear (see Interpreter.
        // ResetModuleInterpreterState), without it a reused interpreter retains the
        // previous import's parsed declarations, caches and namespace state (linear leak).
        itp.ResetModuleInterpreterState();
        return itp;
    }

    internal static void Return(Interpreter itp)
    {
        var pool = Pool;
        const int maxPoolSize = 16;
        if (pool.Count < maxPoolSize)
            pool.Push(itp);
        else
            itp.Dispose();
    }

    // ── retention diagnostic (DbgRetention native) ──
    internal static int DebugPoolCount() => _pool?.Count ?? 0;

    internal static Interpreter[] DebugPoolSnapshot() => _pool?.ToArray() ?? [];
}
