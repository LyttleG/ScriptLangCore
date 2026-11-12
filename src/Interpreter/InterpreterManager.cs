namespace ScriptLangCore;

internal static class InterpreterManager
{
    private static readonly ConcurrentDictionary<long, string> Live = new();

    public static InterpreterHandle Create(bool caseSensitive = true, string? context = null)
    {
        var itp = new Interpreter(caseSensitive);
        Live[RuntimeHelpers.GetHashCode(itp)] = context ?? Environment.StackTrace;
        return new InterpreterHandle(itp);
    }

    public static int LiveCount => Live.Count;

    public static void ReportLeaks()
    {
        if (Live.IsEmpty) return;
        Console.Error.WriteLine($"[InterpreterManager] {Live.Count} undisposed Interpreter(s):");
        foreach (var kv in Live)
            Console.Error.WriteLine($"  0x{kv.Key:X8}: {kv.Value}");
    }

    internal static void Unregister(Interpreter itp)
    {
        Live.TryRemove(RuntimeHelpers.GetHashCode(itp), out _);
    }
}
