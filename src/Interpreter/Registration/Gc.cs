namespace ScriptLangCore;

internal partial class Interpreter
{

    [System.Runtime.InteropServices.DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>P/Invoke EmptyWorkingSet on this process, the OS pages out pages not recently
    /// used (GC-reserved segments after a bursty run), dropping the working set without touching
    /// the managed heap. Safe to call anytime; returns whether the trim was requested.</summary>
    internal static bool TrimWorkingSet()
    {
        try { return EmptyWorkingSet(GetCurrentProcess()); }
        catch { return false; }
    }

    private void RegisterGcFunctions()
    {
        // ============================================================================
        // GARBAGE COLLECTOR FUNCTIONS
        // ============================================================================

        var gcNs = new Namespace(_comparer);



        // GC.Collect() - Forces garbage collection
        gcNs.AddMethod("Collect", args =>
        {
            var generation = args.Length > 0 ? Convert.ToInt32(args[0]) : GC.MaxGeneration;

            if (args.Length == 0)
                GC.Collect();
            else
                GC.Collect(generation);
            return null;
        });

        // GC.CollectAsync() - Non-blocking collection (fires and forgets)
        gcNs.AddMethod("CollectAsync", args =>
        {
            var generation = args.Length > 0 ? Convert.ToInt32(args[0]) : GC.MaxGeneration;

            System.Threading.Tasks.Task.Run(() =>
            {
                if (args.Length == 0)
                    GC.Collect();
                else
                    GC.Collect(generation);
            });

            return null;
        });

        // GC.WaitForPendingFinalizers() - Wait for finalizers to complete
        gcNs.AddMethod("WaitForFinalizers", _ =>
        {
            GC.WaitForPendingFinalizers();
            return null;
        });

        // GC.TrimMemory() - Trim the process working set (EmptyWorkingSet): the managed heap is
        // unchanged, but the OS pages out the GC's reserved segments, the process RAM drops after
        // bursty allocations. .gc collect calls this automatically too.
        gcNs.AddMethod("TrimMemory", _ =>
        {
            TrimWorkingSet();
            return null;
        });

        // GC.GetTotalMemory() - Get current memory usage
        gcNs.AddMethod("GetTotalMemory", args =>
        {
            var forceFullCollection = args.Length > 0 && Convert.ToBoolean(args[0]);
            var bytes = GC.GetTotalMemory(forceFullCollection);
            return bytes;
        });

        // GC.GetTotalMemoryFormatted() - Human-readable memory usage
        gcNs.AddMethod("GetTotalMemoryFormatted", args =>
        {
            var forceFullCollection = args.Length > 0 && Convert.ToBoolean(args[0]);
            var bytes = GC.GetTotalMemory(forceFullCollection);
            return FormatBytes(bytes);
        });

        // GC.GetGeneration() - Get generation of an object
        gcNs.AddMethod("GetGeneration", args =>
        {
            if (args.Length == 0)
                throw new ScriptException("GetGeneration expects an object argument");

            var obj = args[0];
            if (obj == null)
                return -1;

            return GC.GetGeneration(obj);
        });

        // GC.MaxGeneration - Maximum generation number
        gcNs.AddProperty("MaxGeneration", GC.MaxGeneration);

        // GC.GetAllocatedBytesForCurrentThread() - Thread allocation tracking
        gcNs.AddMethod("GetAllocatedBytes", _ => GC.GetAllocatedBytesForCurrentThread());

        // GC.GetAllocatedBytesFormatted() - Human-readable thread allocation
        gcNs.AddMethod("GetAllocatedBytesFormatted", _ =>
        {
            var bytes = GC.GetAllocatedBytesForCurrentThread();
            return FormatBytes(bytes);
        });

        // GC.SuppressFinalize() - Suppress finalization for an object
        gcNs.AddMethod("SuppressFinalize", args =>
        {
            if (args.Length == 0)
                throw new ScriptException("SuppressFinalize expects an object argument");

            ref var obj = ref args[0];
            if (obj == null) return null;
            GC.SuppressFinalize(obj);
            obj = null;
            return null;
        });

        // GC.ReRegisterForFinalize() - Re-register for finalization
        gcNs.AddMethod("ReRegisterForFinalize", args =>
        {
            if (args.Length == 0)
                throw new ScriptException("ReRegisterForFinalize expects an object argument");

            var obj = args[0];
            if (obj == null) return null;
            GC.ReRegisterForFinalize(obj);
            return null;
        });

        // GC.GetGenerationCount() - Number of collections per generation
        gcNs.AddMethod("GetCollectionCount", args =>
        {
            var generation = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
            return GC.CollectionCount(generation);
        });

        // GC.GetGCMemoryInfo() - Detailed memory information
        gcNs.AddMethod("GetMemoryInfo", _ =>
        {
            var info = GC.GetGCMemoryInfo();

            // Pre-size exactly, no rehash, no wasted buckets
            var raw = new Dictionary<object, object?>(10)
            {
                ["TotalAvailableMemoryBytes"] = info.TotalAvailableMemoryBytes,
                ["HeapSizeBytes"] = info.HeapSizeBytes,
                ["FragmentedBytes"] = info.FragmentedBytes,
                ["TotalCommittedBytes"] = info.TotalCommittedBytes,
                ["PromotedBytes"] = info.PromotedBytes,
                ["PauseTimePercentage"] = info.PauseTimePercentage,
                ["Generation"] = info.Generation,
                ["Compacted"] = info.Compacted,
                ["Concurrent"] = info.Concurrent,
                ["Index"] = info.Index,
            };

            return ScriptBoxedDictionary.FromExisting(
                raw,
                new TypeInfo(TokenType.StringType, false, 0, 0, null),
                new TypeInfo(TokenType.Object, true, 0, 0, null));
        });

        // GC.GetGCMemoryInfoFormatted() - Human-readable memory info
        gcNs.AddMethod("GetMemoryInfoFormatted", _ =>
        {
            var info = GC.GetGCMemoryInfo();
            var sb = new StringBuilder();

            sb.AppendLine("=== GC Memory Info ===");
            sb.AppendLine($"Total Available: {FormatBytes(info.TotalAvailableMemoryBytes)}");
            sb.AppendLine($"Heap Size:       {FormatBytes(info.HeapSizeBytes)}");
            sb.AppendLine($"Fragmented:      {FormatBytes(info.FragmentedBytes)}");
            sb.AppendLine($"Total Committed: {FormatBytes(info.TotalCommittedBytes)}");
            sb.AppendLine($"Promoted:        {FormatBytes(info.PromotedBytes)}");
            sb.AppendLine($"Pause Time:      {info.PauseTimePercentage:F2}%");
            sb.AppendLine($"Generation:      {info.Generation}");
            sb.AppendLine($"Compacted:       {info.Compacted}");
            sb.AppendLine($"Concurrent:      {info.Concurrent}");

            return sb.ToString();
        });

        // GC.GetTotalPauseDuration() - Total GC pause time
        gcNs.AddMethod("GetTotalPauseDuration", _ =>
        {
            var duration = GC.GetTotalPauseDuration();
            return duration.TotalMilliseconds;
        });

        // Convenience method: Force full cleanup
        gcNs.AddMethod("Cleanup", _ =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            return null;
        });

        // GC.WorkingSet() / GC.PrivateMemory(), OS process metrics. Unmanaged allocations
        // (Mem.Alloc / Marshal.AllocHGlobal) are NOT visible to GC.GetTotalMemory, these are the
        // only reliable leak signals for unmanaged buffers.
        gcNs.AddMethod("WorkingSet", _ => Process.GetCurrentProcess().WorkingSet64);
        gcNs.AddMethod("PrivateMemory", _ => Process.GetCurrentProcess().PrivateMemorySize64);

        CurrentFrame.Scopes.Define("GC", gcNs);
    }

    // Helper method for formatting bytes
    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {sizes[order]}";
    }
}