namespace ScriptLangCore.Repl;

using ScriptLangCore;

internal static class DotCommands
{
    private static volatile int _gcBase0;
    private static volatile int _gcBase1;
    private static volatile int _gcBase2;
    private static long _gcLastManaged;
    private static int _gcLastHandles;
    private static volatile int _gcLastGen0;
    private static volatile int _gcLastGen1;
    private static volatile int _gcLastGen2;

    internal static void ResetGcBaseline()
    {
        _gcBase0 = GC.CollectionCount(0);
        _gcBase1 = GC.CollectionCount(1);
        _gcBase2 = GC.CollectionCount(2);
        _gcLastManaged = GC.GetTotalMemory(true);
        _gcLastGen0 = 0;
        _gcLastGen1 = 0;
        _gcLastGen2 = 0;
    }

    private static readonly Dictionary<string, string> CommandHelp = new(StringComparer.OrdinalIgnoreCase)
    {
        [".exit"] = "Exit the REPL",
        [".quit"] = "Exit the REPL",
        [".clear"] = "Clear the screen",
        [".cls"] = "Clear the screen",
        [".cd"] = "Change current directory",
        [".echo"] = "Echo text to console",
        [".time"] = "Toggle execution timing on/off",
        [".help"] = "Show this help or .help <cmd> for details",
        [".history"] = "Show command history",
        [".vars"] = "List user variables",
        [".varsall"] = "List all variables including builtins",
        [".run"] = "Run a script file or pattern  (-bc: load/build .slcbc sibling; -- args: the script's own argv)",
        [".looprun"] = "Run a script file N times  (-bc: also export .slcbc; -- args: each iteration's argv)",
        [".reset"] = "Reset interpreter state  (.reset all, .reset hard)",
        [".gc"] = "GC operations: collect, stats, history, info, diff, top, leak, handles, handletypes",
        [".bytecode"] = "Toggle bytecode VM: .bytecode on|off|reset",
        [".vm"] = "VM trace: .vm trace on|off (no argument reports the state)",
        [".state"] = "Set state persistence: keep|reset",
        [".type"] = "Show type of an expression",
        [".stats"] = "Show execution metrics",
        [".search"] = "Search command history",
        [".alias"] = "Create command aliases",
        [".edit"] = "Open a file in external editor",
        [".eval"] = "Evaluate an expression silently",
        [".export"] = "Export variables, or -bc: run + export a bytecode LIBRARY (.slcbc); -bundle: write a PROGRAM bundle, nothing runs, --source=carry|strip|strict",
        [".save"] = "Export session state to a script file (.save [--all] [file])",
        [".trace"] = "Enable/disable execution call tracing (.trace on|off)",
        [".loadslcbc"] = "Load a bytecode bundle (.slcbc): library → namespace alias, bundle → flat",
        [".bench"] = "Benchmark a script  (-bc: also export .slcbc)",
        [".functions"] = "List available script functions",
        [".tree"] = "Show directory tree",
        [".import"] = "Import a script file into this session (keeps state); .include is the same command",
        [".include"] = "Import a script file into this session (keeps state); the old name of .import",
        [".infos"] = "Show interpreter information",
        [".debug"] = "Toggle script debugger on/off",
        [".break"] = "Set a breakpoint at line",
        [".mcp"] = "MCP server controls",
        [".call"] = "Call an MCP tool",
        [".log"] = "Set log level",
        [".strict"] = "Enable case-sensitive mode",
        [".lax"] = "Disable case-sensitive mode",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when a name WITH its leading dot is a command this REPL knows. The file stripper asks it so
    /// that a line which merely LOOKS like a command is not deleted: an indented `.Replace(...)` continues a
    /// chained call, and `.row .text { ... }` is a CSS rule inside a template. CommandHelp is the registry, so the
    /// two cannot drift.</summary>
    internal static bool IsKnownCommand(ReadOnlySpan<char> name)
    {
        foreach (var key in CommandHelp.Keys)
            if (name.Equals(key.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Expands an alias ONCE, before dispatch: a name in the table (with or without its leading dot) becomes
    /// that command line, with the caller's own arguments appended.
    ///
    /// ONE implementation, called by <see cref="Program.DispatchDotCommand"/> and by nothing else. The previous copy
    /// lived INSIDE <see cref="Handle"/> and bridged to the session dispatcher by REFLECTION, passing an
    /// <c>Interpreter</c> where an <c>InterpreterHandle</c> belongs, so <c>.alias r .run</c> then <c>.r file.slc</c>
    /// answered "Unknown command" while an alias to an interpreter-local command worked. An alias is a property of
    /// the SURFACE, so it is resolved where the surface begins.</summary>
    internal static string ExpandAlias(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return line;
        var key = parts[0];
        if (!Aliases.TryGetValue(key, out var expanded) &&
            !(key.StartsWith(".") && Aliases.TryGetValue(key.Substring(1), out expanded)))
            return line;
        return parts.Length == 1 ? expanded : expanded + " " + string.Join(" ", parts.Skip(1));
    }

    internal static bool Handle(string line, Interpreter itp)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        // Clear stale interrupt flags that may have been set during previous command
        itp._interruptRequested = false;
        ReplInterrupt.Clear();

        switch (parts[0])
        {
            case ".clear":
            case ".cls":
                // A SESSION WITHOUT A CONSOLE IS NOT AN ERROR. `Console.Clear` throws `IOException, Descripteur non
                // valide` when stdout is a pipe (a child REPL, a redirected gate run), and the uncaught throw made
                // `.clear` -- the first command a reader types -- answer an I/O error. The check used to live in the
                // PROGRAM-side arm that never ran (see Program.DispatchDotCommand), so it is restored here, where the
                // command actually is.
                try { Console.Clear(); }
                catch
                {
                    // no console to clear: nothing to do, and nothing to report
                }

                return true;

            case ".help":
                if (parts.Length > 1 && CommandHelp.TryGetValue(parts[1], out var desc))
                {
                    Console.WriteLine($"{parts[1]} \u2014 {desc}");
                }
                else
                {
                    Console.WriteLine("Available commands:");
                    foreach (var kv in CommandHelp)
                        Console.WriteLine($"  {kv.Key} \u2014 {kv.Value}");
                    // THE SHORTHANDS THAT ARE NOT COMMANDS, and they stay out of the table on purpose: these are
                    // line SHAPES the REPL parses before any dispatch, and IsKnownCommand (the rule that decides
                    // whether a column-1 dot line in a SCRIPT is a directive) must not answer yes for them.
                    Console.WriteLine();
                    Console.WriteLine("Accepted at the prompt, and not commands:");
                    Console.WriteLine("  > file             Redirect stdout to a file (> console restores it)");
                    Console.WriteLine("  < file             Run a script file, the same as .run");
                    Console.WriteLine("  !<N>               Run history entry N again");
                    Console.WriteLine("  <expression>       Any script expression or statement; globals persist across inputs");
                }

                return true;

            case ".history":
                var history = itp.GetCommandHistory();
                if (parts.Length > 2 && parts[1] == "search")
                {
                    var histPattern = string.Join(" ", parts.Skip(2));
                    var matches = history.Where(h => h.Contains(histPattern, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    foreach (var m in matches) Console.WriteLine($"  {m}");
                }
                else if (parts.Length > 1 && int.TryParse(parts[1], out var n))
                {
                    var lastN = history.TakeLast(n).ToList();
                    foreach (var h in lastN) Console.WriteLine($"  {h}");
                }
                else
                {
                    foreach (var h in history) Console.WriteLine($"  {h}");
                }

                return true;

            case ".type":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .type <expression>");
                    return true;
                }

                var typeExpr = string.Join(" ", parts.Skip(1));
                // If the input starts with '.', check if it's an alias first
                if (typeExpr.StartsWith("."))
                {
                    var aliasKey = typeExpr.TrimStart('.');
                    if (Aliases.TryGetValue(aliasKey, out var aliasVal))
                    {
                        Console.WriteLine($"Alias: .{aliasKey} => {aliasVal}");
                        return true;
                    }
                }

                try
                {
                    var expr = new Parser([], itp.Delegates).ParseInlineExpression(typeExpr);
                    var value = EvaluateOnInterpreter(itp, expr);
                    // The type the expression HAS, which is what a reader of `.type` (and of `.vars -v` below) asks
                    // for: a `Type` value names the type it denotes (`int`, `StringBuilder`) rather than the word
                    // "Type", that is `typeof`'s answer about the concept, and the value column prints the ToString.
                    Console.WriteLine(value == null ? "null" : $"{ReportedTypeName(value)} => {value}");
                }
                catch (TargetInvocationException tie)
                {
                    Console.WriteLine($"Error: {tie.InnerException?.Message ?? tie.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                return true;

            case ".stats":
                {
                    var elapsed = DateTime.Now - itp.StartTime;
                    Console.WriteLine($"  Uptime:      {elapsed.ToString()}");
                    Console.WriteLine($"  Statements:  {itp.StatementsExecuted}");
                    Console.WriteLine($"  Function calls: {itp.FunctionsCalled}");
                    Console.WriteLine($"  Managed memory: {GC.GetTotalMemory(true) / 1024.0:F1} KB");
                    return true;
                }

            case ".vars":
                {
                    var showValues = parts.Contains("-v");
                    var filter = parts.FirstOrDefault(p => !p.StartsWith("-") && p != ".vars");
                    var allVars = itp.GetUserVariables();
                    var count = 0;
                    foreach (var kv in allVars)
                    {
                        if (filter != null && !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                        Console.WriteLine(showValues
                            ? $"  {kv.Key} = {kv.Value ?? "(null)"} ({ReportedTypeName(kv.Value)})"
                            : $"  {kv.Key}");
                        count++;
                    }

                    Console.WriteLine($"  ({count} variable(s))");
                    return true;
                }

            case ".search":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .search <pattern>");
                    return true;
                }

                var pattern = string.Join(" ", parts.Skip(1));
                var matching = itp.GetCommandHistory()
                    .Where(h => h.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matching.Count == 0)
                    Console.WriteLine("  No matches found.");
                else
                    foreach (var m in matching)
                        Console.WriteLine($"  {m}");
                return true;

            case ".gc":
                if (parts.Length > 1)
                {
                    switch (parts[1])
                    {
                        case "handletypes":
                            {
                                // The tally by OBJECT TYPE, read-only, no debugger: the in-process counterpart of cdb's !handle. Compare
                                // two calls, the delta per type is the signal, since absolute counts are the process's own business.
                                var tally = HandleTypes.Tally();
                                if (tally.Count == 0)
                                    Console.WriteLine("  (unavailable: NtQuerySystemInformation refused)");
                                foreach (var (type, count) in tally)
                                    Console.WriteLine($"  {count,6}  {type}");
                                return true;
                            }
                        case "handles":
                            {
                                // The OS-side counterpart of .gc collect: handles and private bytes are invisible to
                                // GC.GetTotalMemory, so a COM/native leak shows up here and nowhere else.
                                long hPriv; int hCount;
                                try
                                {
                                    using var self = System.Diagnostics.Process.GetCurrentProcess();
                                    hPriv = self.PrivateMemorySize64; hCount = self.HandleCount;
                                }
                                catch { hPriv = 0; hCount = 0; }
                                var hDelta = _gcLastHandles == 0 ? 0 : hCount - _gcLastHandles;
                                var hSince = _gcLastHandles == 0 ? ""
                                    : $"  ({(hDelta >= 0 ? "+" : "")}{hDelta} since the last .gc handles/.gc collect)";
                                Console.WriteLine($"  Handles:      {hCount}{hSince}");
                                Console.WriteLine($"  Private:      {hPriv / 1048576.0:F1} MB");
                                Console.WriteLine("  An OS resource GC.GetTotalMemory cannot see: a climb across repeated calls is a leak.");
                                Console.WriteLine("  COM connection handles (one ALPC Port per out-of-process activation) ARE reclaimed by");
                                Console.WriteLine("  the runtime within minutes of that server exiting (measured 9 -> 5 over a 4-minute");
                                Console.WriteLine("  idle), so read deltas, not absolutes.");
                                _gcLastHandles = hCount;
                                _gcLastManaged = GC.GetTotalMemory(true);
                                return true;
                            }
                        case "collect":
                            var before = GC.GetTotalMemory(true);
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            var after = GC.GetTotalMemory(true);
                            Console.WriteLine($"  Before: {before / 1024.0:F1} KB");
                            Console.WriteLine($"  After:  {after / 1024.0:F1} KB");
                            Console.WriteLine(
                                $"  Freed:  {(before - after) / 1024.0:F1} KB ({(before > 0 ? (before - after) * 100.0 / before : 0):F1}%)");
                            // Trim the working set: .gc collect compacts the managed heap, but the
                            // GC's reserved/committed segments keep the process working set high
                            // after bursty runs (e.g. 1M-element benches). EmptyWorkingSet asks the
                            // OS to page those pages out, the managed heap is untouched.
                            if (TrimWorkingSet())
                                Console.WriteLine("  Working set trimmed (EmptyWorkingSet)");
                            return true;
                        case "stats":

                            Console.WriteLine($"  Managed:     {GC.GetTotalMemory(true) / 1024.0:F1} KB");
                            Console.WriteLine($"  Gen0:        {GC.CollectionCount(0) - _gcBase0} collections");
                            Console.WriteLine($"  Gen1:        {GC.CollectionCount(1) - _gcBase1} collections");
                            Console.WriteLine($"  Gen2:        {GC.CollectionCount(2) - _gcBase2} collections");
                            return true;
                        case "history":
                            Console.WriteLine($"  Gen0: {GC.CollectionCount(0) - _gcBase0} collections");
                            Console.WriteLine($"  Gen1: {GC.CollectionCount(1) - _gcBase1} collections");
                            Console.WriteLine($"  Gen2: {GC.CollectionCount(2) - _gcBase2} collections");
                            Console.WriteLine($"  Memory: {GC.GetTotalMemory(true) / 1024.0:F1} KB");
                            return true;
                        case "diff":
                            {
                                var managed = GC.GetTotalMemory(true);
                                var g0 = GC.CollectionCount(0) - _gcBase0;
                                var g1 = GC.CollectionCount(1) - _gcBase1;
                                var g2 = GC.CollectionCount(2) - _gcBase2;
                                var deltaMem = managed - _gcLastManaged;
                                var sign = deltaMem >= 0 ? "+" : "";
                                Console.WriteLine(
                                    $"  Managed:   {sign}{deltaMem / 1024.0:F1} KB  (delta since last .gc call)");
                                Console.WriteLine($"  Gen0:      {g0 - _gcLastGen0:+0;-0}");
                                Console.WriteLine($"  Gen1:      {g1 - _gcLastGen1:+0;-0}");
                                Console.WriteLine($"  Gen2:      {g2 - _gcLastGen2:+0;-0}");
                                _gcLastManaged = managed;
                                _gcLastGen0 = g0;
                                _gcLastGen1 = g1;
                                _gcLastGen2 = g2;
                                return true;
                            }
                        case "top":
                            {
                                var n = 5;
                                if (parts.Length > 2 && int.TryParse(parts[2], out var parsed) && parsed > 0)
                                    n = parsed;

                                var counts = new Dictionary<string, int>(StringComparer.Ordinal);

                                void CountObj(object? obj)
                                {
                                    if (obj == null) return;
                                    var typeName = obj is Type rt ? rt.Name : obj.GetType().Name;
                                    if (typeName.Contains("+"))
                                        typeName = typeName.Substring(typeName.LastIndexOf('+') + 1);
                                    counts.TryGetValue(typeName, out var c);
                                    counts[typeName] = c + 1;
                                }

                                // Walk _classes
                                foreach (var kv in itp.Classes)
                                {
                                    CountObj(kv.Value);
                                }

                                // Walk _delegates
                                foreach (var kv in itp.DelegateInfos)
                                {
                                    CountObj(kv.Value);
                                }

                                // Walk _structs
                                foreach (var kv in itp.Structs)
                                {
                                    CountObj(kv.Value);
                                }

                                // Walk _modules
                                foreach (var kv in itp.Modules)
                                {
                                    CountObj(kv.Value);
                                }

                                var sorted = counts.OrderByDescending(kv => kv.Value).Take(n).ToList();
                                var rank = 1;
                                foreach (var kv in sorted)
                                    Console.WriteLine($"  {rank++,2}. {kv.Key,-30} {kv.Value,6:N0}");
                                if (sorted.Count == 0)
                                    Console.WriteLine("  (no objects tracked)");
                                return true;
                            }
                        case "leak":
                            {
                                // .gc leak <file> [count] [thresholdKB]
                                // One-shot leak check: warmup → baseline → run N times → forced
                                // collect → per-run retained delta + PASS/LEAK verdict.
                                // The warmup pass absorbs one-time costs (script globals, type
                                // registration, cache warmup) so the verdict measures growth
                                // BETWEEN runs, not the first-run startup jump, small counts
                                // no longer false-positive on legitimate fixed state.
                                if (parts.Length < 3)
                                {
                                    Console.WriteLine("Usage: .gc leak <file> [count=100] [thresholdKB=64]");
                                    Console.WriteLine("  Runs <file> <count> times; if retained memory grows by");
                                    Console.WriteLine("  > <thresholdKB> per run after a forced full collection,");
                                    Console.WriteLine("  flags a leak (like .looprun + .gc collect, in one step).");
                                    return true;
                                }

                                var leakFile = parts[2];
                                var leakCount = 100;
                                if (parts.Length > 3 && int.TryParse(parts[3], out var lc) && lc > 0) leakCount = lc;
                                var leakThresholdKb = 64.0;
                                if (parts.Length > 4 && double.TryParse(parts[4], out var lt) && lt > 0)
                                    leakThresholdKb = lt;

                                // Guard: a threshold below the engine's GC noise floor (~16 KB) makes
                                // the verdict meaningless, any workload trips "LEAK" on allocator
                                // variance, not real retention. Warn and keep the user-chosen value.
                                if (leakThresholdKb < 16.0)
                                    Console.WriteLine($"  [warning] threshold {leakThresholdKb} KB is below the GC noise floor, " +
                                                      "use >= 16 KB (recommended 64 KB) for a meaningful verdict.");

                                var savedLeakOut = Console.Out;
                                // AND THE ENGINE'S OWN DOOR (`Interpreter.Out`): a SESSION sets it, so `Console.SetOut`
                                // alone silenced nothing there and every iteration's `print` reached the host's pane,
                                // where the rule is one iteration's output and then a percentage.
                                var savedLeakSink = itp.Out;
                                var leakErrors = 0;

                                // Warmup pass(es): absorb one-time fixed state so the measured
                                // delta reflects growth BETWEEN runs. For tiny counts, still warm
                                // up at least once (measured runs = count).
                                var warmupRuns = leakCount >= 3 ? 1 : 1;
                                for (var wi = 0; wi < warmupRuns; wi++)
                                {
                                    if (ReplInterrupt.Pending) break;
                                    RunLeakIteration(wi, quiet: true);
                                }

                                // Baseline after warmup + forced full collection.
                                GC.Collect(2, GCCollectionMode.Forced, true, true);
                                GC.WaitForPendingFinalizers();
                                GC.Collect(2, GCCollectionMode.Forced, true, true);
                                var leakBase = GC.GetTotalMemory(true);
                                var leakBaseG0 = GC.CollectionCount(0);
                                var (leakBaseNative, leakBaseHandles) = CurrentFootprint();

                                // Measured runs (progress on stderr, like .looprun).
                                var measuredRuns = 0;
                                for (var li = 0; li < leakCount; li++)
                                {
                                    if (ReplInterrupt.Pending) break;
                                    RunLeakIteration(li, quiet: li > 0);
                                    measuredRuns++;
                                    Console.Error.Write($"\r  Progress: {measuredRuns * 100 / leakCount}% ({measuredRuns}/{leakCount})");
                                }

                                Console.Error.WriteLine();
                                Console.SetOut(savedLeakOut);
                                itp.Out = savedLeakSink;

                                // Ctrl+C: stop the loop and skip the verdict, partial data
                                // cannot support a meaningful PASS/LEAK verdict.
                                if (ReplInterrupt.Take())
                                {
                                    Console.WriteLine(
                                        $"  Interrupted by user after {measuredRuns} of {leakCount} runs \u2014 verdict skipped.");
                                    return true;
                                }

                                // Forced full collection, surviving growth is rooted.
                                GC.Collect(2, GCCollectionMode.Forced, true, true);
                                GC.WaitForPendingFinalizers();
                                GC.Collect(2, GCCollectionMode.Forced, true, true);
                                var leakAfter = GC.GetTotalMemory(true);
                                var (leakAfterNative, leakAfterHandles) = CurrentFootprint();
                                var leakDelta = leakAfter - leakBase;
                                var leakPerRunKB = leakDelta / 1024.0 / leakCount;
                                var leakG0 = GC.CollectionCount(0) - leakBaseG0;

                                Console.WriteLine(
                                    $"  File:    {Path.GetFileName(leakFile)} × {leakCount}{(leakErrors > 0 ? " (" + leakErrors + " run errors)" : "")}");
                                Console.WriteLine($"  Base:    {leakBase / 1024.0:F1} KB");
                                Console.WriteLine($"  After:   {leakAfter / 1024.0:F1} KB");
                                Console.WriteLine(
                                    $"  Retained delta: {(leakDelta >= 0 ? "+" : "")}{leakDelta / 1024.0:F1} KB");
                                Console.WriteLine(
                                    $"  Per run: {leakPerRunKB:F2} KB  (threshold {leakThresholdKb:F0} KB)  gen0={leakG0}");
                                // The figures above are the MANAGED heap, and they cannot see COM at all: measured, a
                                // 200,000-cell block write leaked ~14 MB PER RUN in native memory while the managed
                                // side stayed flat. Private bytes are read through the managed API on purpose, no
                                // child process to race, and no COM query of its own, since measuring a COM leak with
                                // a COM call would confound the reading.
                                var leakNativeDelta = leakAfterNative - leakBaseNative;
                                Console.WriteLine($"  Private bytes:  {leakBaseNative / 1048576.0:F1} MB -> "
                                    + $"{leakAfterNative / 1048576.0:F1} MB  ({(leakNativeDelta >= 0 ? "+" : "")}"
                                    + $"{leakNativeDelta / 1024.0:F1} KB"
                                    + (measuredRuns > 0 ? $", {leakNativeDelta / 1024.0 / measuredRuns:F1} KB/run" : "")
                                    + ")");
                                var leakHandleDelta = leakAfterHandles - leakBaseHandles;
                                Console.WriteLine($"  Handles:        {leakBaseHandles} -> {leakAfterHandles}  "
                                    + $"({(leakHandleDelta >= 0 ? "+" : "")}{leakHandleDelta}"
                                    + (measuredRuns > 0 ? $", {(double)leakHandleDelta / measuredRuns:F2}/run" : "")
                                    + ")  [an OS resource GC.GetTotalMemory cannot see: +1 per run is a LEAK]");
                                if (leakNativeDelta > 1024 * 1024)
                                    Console.WriteLine("  ^ for COM/native work THIS is the line to watch: the managed "
                                        + "figures above cannot see that memory at all.");
                                ConsoleSafe.SetColor(leakPerRunKB > leakThresholdKb
                                    ? ConsoleColor.Red
                                    : ConsoleColor.Green);
                                Console.WriteLine(leakPerRunKB > leakThresholdKb
                                    ? "  VERDICT: LEAK, retained memory grows per run and survives full collection"
                                    : "  VERDICT: PASS, no significant per-run retention");
                                ConsoleSafe.ResetColor();
                                return true;

                                // This process's private bytes AND its kernel-handle count, from the OS, without a
                                // child process or a COM call. Handles matter for the same reason private bytes do:
                                // they are invisible to GC.GetTotalMemory, and a count that climbs by exactly one per
                                // run is an unreleased OS resource, the loop is the only place that shows it.
                                (long Private, int Handles) CurrentFootprint()
                                {
                                    try
                                    {
                                        using var self = System.Diagnostics.Process.GetCurrentProcess();
                                        return (self.PrivateMemorySize64, self.HandleCount);
                                    }
                                    catch { return (0, 0); }
                                }

                                // Helper: one run of the script (fresh parse, like .looprun).
                                void RunLeakIteration(int iterIndex, bool quiet)
                                {
                                    itp.Reset(keepGlobals: false, clearTypes: true);
                                    Console.SetOut(quiet ? TextWriter.Null : savedLeakOut);
                                    itp.Out = quiet ? TextWriter.Null : savedLeakSink;
                                    // Isolate VM state per iteration: a script may toggle the static
                                    // VmEnabled (e.g. VmSetEnabled(false) for tree-parity groups), the
                                    // top-level path (VmSetTopLevelEnabled) or the parked statement carrier
                                    // (VmSetStatementCarrier), and none must leak across measured runs: every
                                    // iteration starts from the state at .gc leak invocation.
                                    var savedVmLeak = VmEnabled;
                                    var savedTopLevelLeak = TopLevelVmEnabled;
                                    var savedCarrierLeak = StatementCarrierEnabled;
                                    try
                                    {
                                        var (lMem, _) = FileReader.ReadFile(leakFile);
                                        var lTokens = new Lexer(lMem).GetTokens().ToList();
                                        var lStmts = new Parser(lTokens, itp.Delegates, source: lMem.ToString()).GetStatements().ToList();
                                        Program.PrecompileFunctions(lStmts, itp);
                                        itp.Interpret(lStmts, leakFile);
                                    }
                                    catch (Exception lex)
                                    {
                                        leakErrors++;
                                        if (iterIndex == 0)
                                        {
                                            ConsoleSafe.SetColor(ConsoleColor.Red);
                                            Console.WriteLine($"  [warmup] Error: {lex.Message}");
                                            ConsoleSafe.ResetColor();
                                        }
                                    }
                                    finally
                                    {
                                        VmEnabled = savedVmLeak;
                                        TopLevelVmEnabled = savedTopLevelLeak;
                                        StatementCarrierEnabled = savedCarrierLeak;
                                    }
                                }
                            }
                        case "info":
                        case "infos":
                            var memInfo2 = GC.GetGCMemoryInfo();
                            var gen0 = GC.CollectionCount(0) - _gcBase0;
                            var gen1 = GC.CollectionCount(1) - _gcBase1;
                            var gen2 = GC.CollectionCount(2) - _gcBase2;
                            Console.WriteLine($"  Heap:         {memInfo2.HeapSizeBytes / 1024.0,10:F1} KB");
                            Console.WriteLine($"  Fragmented:   {memInfo2.FragmentedBytes / 1024.0,10:F1} KB");
                            Console.WriteLine(
                                $"  Gen0 size:    {memInfo2.GenerationInfo[0].SizeAfterBytes / 1024.0,10:F1} KB");
                            Console.WriteLine(
                                $"  Gen1 size:    {memInfo2.GenerationInfo[1].SizeAfterBytes / 1024.0,10:F1} KB");
                            Console.WriteLine(
                                $"  Gen2 size:    {memInfo2.GenerationInfo[2].SizeAfterBytes / 1024.0,10:F1} KB");
                            Console.WriteLine(
                                $"  Paused:       {memInfo2.PauseDurations[^1].TotalMilliseconds,10:F1} ms  (last GC pause)");
                            Console.WriteLine(
                                $"  Frag ratio:   {memInfo2.FragmentedBytes * 100.0 / (memInfo2.HeapSizeBytes > 0 ? memInfo2.HeapSizeBytes : 1),10:F1} %");
                            Console.WriteLine(
                                $"  Memory load:  {memInfo2.MemoryLoadBytes / (1024.0 * 1024),10:F1} MB  (OS)");
                            Console.WriteLine($"  Gen0: {gen0}  Gen1: {gen1}  Gen2: {gen2}");
                            return true;
                        default:
                            Console.WriteLine(
                                "Usage: .gc collect|stats|diff|history|info|leak <file> [count] [thresholdKB]");
                            return true;
                    }
                }

                Console.WriteLine("Usage: .gc collect|stats|diff|history|leak <file> [count] [thresholdKB]");
                return true;

            case ".bytecode":
                if (parts.Length > 1)
                {
                    switch (parts[1].ToLowerInvariant())
                    {
                        case "on":
                            VmEnabled = true;
                            Console.WriteLine(
                                "  Bytecode VM: ON, functions with compiled bytecode run through the VM.");
                            return true;
                        case "off":
                            VmEnabled = false;
                            Console.WriteLine("  Bytecode VM: OFF, all functions run through the AST tree-walker.");
                            return true;
                        case "reset":
                            itp.VmInvocations = 0;
                            itp.TreeInvocations = 0;
                            Console.WriteLine("  VM/tree invocation counters reset.");
                            return true;
                        default:
                            Console.WriteLine("Usage: .bytecode on|off|reset");
                            return true;
                    }
                }

                Console.WriteLine($"  Bytecode VM: {(VmEnabled ? "ON" : "OFF")}  (usage: .bytecode on|off)");
                return true;

            case ".alias":
                switch (parts.Length)
                {
                    case 1:
                        {
                            if (Aliases.Count == 0)
                                Console.WriteLine(
                                    "  No aliases defined. Create one with: .alias <shortcut>=<expanded dotcommand>");
                            else
                                foreach (var a in Aliases)
                                    Console.WriteLine($"  {a.Key} =\u003e {a.Value}");
                            return true;
                        }

                    case 2 when parts[1].Contains('='):
                        {
                            var eq = parts[1].IndexOf('=');
                            var name = parts[1][..eq];
                            var value = parts[1][(eq + 1)..];
                            Aliases[name] = value;
                            Console.WriteLine($"  Alias '{name}' => '{value}'");
                            return true;
                        }
                }

                if (parts.Length == 2 && Aliases.Remove(parts[1]))
                {
                    Console.WriteLine($"  Alias '{parts[1]}' removed.");
                    return true;
                }

                Console.WriteLine("Usage: .alias           \u2014 list aliases");
                Console.WriteLine("       .alias ll=.looprun \u2014 create alias");
                Console.WriteLine("       .alias ll          \u2014 remove alias");
                return true;

            case ".edit":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .edit <filepath>");
                    return true;
                }

                var editor = Environment.GetEnvironmentVariable("EDITOR")
                             ?? Environment.GetEnvironmentVariable("VISUAL")
                             ?? "notepad";
                var filePath = string.Join(" ", parts.Skip(1));
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"File not found: {filePath}");
                    return true;
                }

                Console.WriteLine($"  Opening '{filePath}' with {editor}...");
                var psi = new ProcessStartInfo(editor, $"\"{filePath}\"") { UseShellExecute = true };
                var proc = Process.Start(psi);
                proc?.WaitForExit();
                Console.WriteLine("  Editor closed.");
                return true;

            case ".eval":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .eval <expression>");
                    return true;
                }

                var evalExpr = string.Join(" ", parts.Skip(1));
                try
                {
                    var parser2 = new Parser([], itp.Delegates);
                    try
                    {
                        var expr = parser2.ParseInlineExpression(evalExpr);
                        var method = typeof(Interpreter).GetMethod("Evaluate",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        var result = method?.Invoke(itp, [expr]);
                        if (result != null)
                            Console.WriteLine(result);
                    }
                    catch
                    {
                        var stmts = parser2.GetStatements(); //.ToList();
                        itp.Interpret(stmts, ".eval");
                    }
                }
                catch (TargetInvocationException tie)
                {
                    Console.WriteLine($"Error: {tie.InnerException?.Message ?? tie.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                return true;

            case ".export":
                {
                    if (parts.Contains("-bundle"))
                    {
                        // .export -bundle <file.slc> [out.slcbc] [--source=carry|strip|strict], the REPL's twin of
                        // the CLI's --compile: it writes a PROGRAM bundle (the kind the CLI or `.run -bc` runs)
                        // and RUNS NOTHING, so a script that opens a window or never ends can still be compiled.
                        // It reuses the CLI's own two readers and its writer, so the flag spelling, the three
                        // refusals and the output-path rule are the same in both places, and it compiles in a
                        // FRESH interpreter the way --compile does, so a session's own state cannot reach the
                        // artifact. Contrast `-bc` below, which RUNS the script and writes a LIBRARY.
                        var bundleArgs = new List<string> { "--compile" };
                        foreach (var p in parts.Skip(1))
                            if (p != "-bundle")
                                bundleArgs.Add(p);
                        if (bundleArgs.Count < 2)
                        {
                            Console.WriteLine("Usage: .export -bundle <file.slc> [out.slcbc] [--source=carry|strip|strict]");
                            return true;
                        }

                        var argv = bundleArgs.ToArray();
                        if (!Program.SourceModeFromArgs(argv, out var bundleStrip, out var bundleRequire, out var modeError))
                        {
                            Console.WriteLine($"  {modeError}");
                            return true;
                        }

                        // The exit code belongs to the CLI's caller and not to a session: a refusal is reported by
                        // CompileToBundle on stderr, and the session carries on rather than inheriting a code.
                        Program.CompileToBundle(argv[1], Program.OutPathFromArgs(argv), bundleStrip, bundleRequire);
                        return true;
                    }

                    if (parts.Contains("-bc"))
                    {
                        // .export -bc <file.slc> [out.slcbc] [ns], run the script and
                        // export its VM-compiled top-level functions to a bytecode library bundle.
                        var bcArgs = parts.Skip(1).Where(p => p != "-bc").ToArray();
                        if (bcArgs.Length < 1)
                        {
                            Console.WriteLine("Usage: .export -bc <file.slc> [out.slcbc] [ns]");
                            return true;
                        }
                        // A LIBRARY carries no source at all (it names functions and namespaces for `import`), so a
                        // source mode has nothing to act on here: refused BY NAME rather than ignored, with the
                        // spelling that does take one. Any OTHER flag-prefixed word is refused with it, because the
                        // slots below take a path and a namespace, so a mistyped flag would become a namespace name.
                        foreach (var p in bcArgs)
                            if (p.StartsWith("--", StringComparison.Ordinal))
                            {
                                if (p.StartsWith("--source", StringComparison.Ordinal) || p == "--no-source")
                                    Console.WriteLine($"  .export -bc writes a bytecode LIBRARY, which carries no source at all, so {p} has nothing to act on: use .export -bundle for a program bundle, where --source=carry|strip|strict decides what it carries.");
                                else
                                    Console.WriteLine($"  .export -bc takes a file, an output path and a namespace name, not {p}: a library carries no source, so a source flag belongs to .export -bundle.");
                                return true;
                            }

                        var srcFile = bcArgs[0];
                        if (!File.Exists(srcFile))
                        {
                            Console.WriteLine($"File not found: {srcFile}");
                            return true;
                        }

                        string outFile;
                        string? ns;
                        if (bcArgs.Length > 1 && bcArgs[1].EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase))
                        {
                            outFile = bcArgs[1];
                            ns = bcArgs.Length > 2 ? bcArgs[2] : null;
                        }
                        else
                        {
                            outFile = Path.ChangeExtension(srcFile, ".slcbc");
                            ns = bcArgs.Length > 1 ? bcArgs[1] : null;
                        }
                        try
                        {
                            itp.Reset(keepGlobals: false, clearTypes: true);
                            itp.SuppressWarnings = true;
                            var (srcMem, srcBuf) = FileReader.ReadFile(srcFile);
                            try
                            {
                                var bcTokens = new Lexer(srcMem).GetTokens().ToList();
                                var bcParser = new Parser(bcTokens, itp.Delegates, source: srcMem.ToString());
                                var bcStmts = bcParser.GetStatements().ToList();
                                Program.PrecompileFunctions(bcStmts, itp);
                                var r = itp.Interpret(bcStmts, srcFile);
                                if (r.HasError)
                                {
                                    Console.WriteLine($"  Export aborted: script had errors.");
                                    return true;
                                }

                                var metaName = Path.GetFileNameWithoutExtension(srcFile);
                                var report = itp.ExportTopLevelToLibrary(outFile, metaName, ns ?? "");
                                Console.WriteLine($"  {Path.GetFileName(srcFile)} -> {outFile}: {report}");
                            }
                            finally
                            {
                                CharBufferPool.Return(srcBuf!);
                            }
                        }
                        finally
                        {
                            itp.SuppressWarnings = false;
                        }

                        return true;
                    }

                    // A flag-shaped word that is neither -bc nor -bundle used to become a FILE NAME, because this
                    // form's argument is a file name and it was read positionally: `.export --bogus` wrote a file
                    // called "--bogus". Refused by name instead, which is the same silent surprise the compile
                    // path's output-path rule exists for.
                    if (parts.Length > 1 && parts[1].StartsWith("-", StringComparison.Ordinal))
                    {
                        Console.WriteLine($"  .export takes a file name, -bc or -bundle, not {parts[1]}: .export <filename> dumps this session's variables, .export -bc writes a bytecode LIBRARY, and .export -bundle writes a PROGRAM bundle.");
                        return true;
                    }

                    var exportFile = parts.Length > 1 ? parts[1] : "session_state.slc";
                    var vars = itp.GetUserVariables();
                    var exported = 0;
                    using (var sw = new StreamWriter(exportFile))
                    {
                        foreach (var kv in vars)
                        {
                            switch (kv.Value)
                            {
                                case string s:
                                    sw.WriteLine($"var {kv.Key} = \"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";");
                                    exported++;
                                    break;

                                case int or long or double or float:
                                    sw.WriteLine($"var {kv.Key} = {kv.Value};");
                                    exported++;
                                    break;

                                case bool b:
                                    sw.WriteLine($"var {kv.Key} = {(b ? "true" : "false")};");
                                    exported++;
                                    break;

                                case null:
                                    sw.WriteLine($"var {kv.Key} = null;");
                                    exported++;
                                    break;
                            }
                        }
                    }

                    Console.WriteLine($"  Exported {exported} variable(s) to {exportFile}");
                    return true;
                }

            case ".save":
                {
                    var saveFile = "session_state.slc";
                    var exportAll = false;
                    for (var i = 1; i < parts.Length; i++)
                    {
                        if (parts[i] == "--all") exportAll = true;
                        else saveFile = parts[i];
                    }

                    var vars = itp.GetUserVariables();
                    var saved = 0;
                    using (var sw = new StreamWriter(saveFile))
                    {
                        // Save variables (same as .export)
                        foreach (var kv in vars)
                        {
                            if (kv.Value is string s)
                            {
                                sw.WriteLine($"var {kv.Key} = \"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\";");
                                saved++;
                            }
                            else if (kv.Value is int or long or double or float)
                            {
                                sw.WriteLine($"var {kv.Key} = {kv.Value};");
                                saved++;
                            }
                            else if (kv.Value is bool b)
                            {
                                sw.WriteLine($"var {kv.Key} = {(b ? "true" : "false")};");
                                saved++;
                            }
                            else if (kv.Value == null)
                            {
                                sw.WriteLine($"var {kv.Key} = null;");
                                saved++;
                            }
                        }

                        // With --all, also export function/class/struct declarations
                        if (exportAll)
                        {
                            var decls = itp.GetUserDeclarations();
                            foreach (var decl in decls)
                            {
                                var kind = decl.Type switch
                                {
                                    not null when decl.Type.EndsWith("(function)") => "function",
                                    "class" => "class",
                                    "struct" => "struct",
                                    _ => "declaration"
                                };
                                sw.WriteLine($"// {kind} {decl.Name} : {decl.Type}");
                                saved++;
                            }
                        }
                    }

                    Console.WriteLine($"  Saved {saved} item(s) to {saveFile}");
                    return true;
                }

            case ".loadslcbc":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .loadslcbc <file.slcbc> [alias]");
                    return true;
                }

                var bundleFile = parts[1];
                if (!File.Exists(bundleFile))
                {
                    Console.WriteLine($"File not found: {bundleFile}");
                    return true;
                }

                var alias = parts.Length > 2 ? parts[2] : null;

                try
                {
                    var report = itp.LoadSlcbcFileToRepl(bundleFile, alias);
                    Console.WriteLine($"  {report}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }

                return true;

            case ".bench":
                {
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("Usage: .bench <file> [N=10] [-q|--quiet] [-gc N] [-bc [out.slcbc]]");
                        return true;
                    }

                    var quietMode = parts.Contains("--quiet") || parts.Contains("-q");
                    var gcPeriod = 0;
                    for (var pi = 1; pi < parts.Length; pi++)
                    {
                        if ((parts[pi] == "--gc" || parts[pi] == "-gc") && pi + 1 < parts.Length &&
                            int.TryParse(parts[pi + 1], out var gp) && gp > 0)
                        {
                            gcPeriod = gp;
                            break;
                        }
                    }

                    // Filter bench args: skip -q/--quiet, -gc/--gc (+ its number), -bc (+ optional .slcbc)
                    var benchArgs = new List<string>();
                    var wantBc = false;
                    string? bcOut = null;
                    for (var ai = 1; ai < parts.Length; ai++)
                    {
                        var p = parts[ai];
                        switch (p)
                        {
                            case "--quiet" or "-q" or "--gc" or "-gc":
                                {
                                    if (p is "--gc" or "-gc") ai++;
                                    continue;
                                }

                            case "-bc":
                                {
                                    wantBc = true;
                                    if (ai + 1 < parts.Length &&
                                        parts[ai + 1].EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase))
                                        bcOut = parts[++ai];
                                    continue;
                                }

                            default:
                                benchArgs.Add(p);
                                break;
                        }
                    }

                    var benchFile = string.Join(" ", benchArgs.TakeWhile(p => !int.TryParse(p, out _)));
                    if (string.IsNullOrEmpty(benchFile)) benchFile = benchArgs.FirstOrDefault();
                    if (!File.Exists(benchFile))
                    {
                        Console.WriteLine("File not found: " + benchFile);
                        return true;
                    }

                    var benchN = benchArgs.Count > 1 && int.TryParse(benchArgs.Last(), out var bn) ? bn : 10;
                    if (benchN < 1) benchN = 1;
                    var isBc = benchFile.EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase);

                    var (benchMem, benchBuf) = FileReader.ReadFile(benchFile);
                    var benchSource = benchMem;

                    var times = new List<long>(1);
                    ReplInterrupt.Clear();
                    var savedOut = quietMode ? Console.Out : null; var savedBenchSink = quietMode ? itp.Out : null;   // both doors: a session prints to Interpreter.Out
                    if (quietMode) { Console.SetOut(TextWriter.Null); itp.Out = TextWriter.Null; }
                    var benchT0 = DateTime.Now.Ticks;
                    var actualRuns = 0;
                    try
                    {
                        itp.SuppressWarnings = true;
                        List<Token>? benchTokens = null;
                        if (!isBc) benchTokens = [.. new Lexer(benchSource).GetTokens()];
                        for (var i = 0; i < benchN; i++)
                        {
                            if (ReplInterrupt.Pending) break;

                            itp.Reset(keepGlobals: true, clearTypes: false);

                            ScriptResult? r = null;
                            if (isBc)
                            {
                                // Bytecode bundle: each iteration loads it (no source parse).
                                itp.LoadBundleFromFile(benchFile);
                            }
                            else
                            {
                                var benchParser = new Parser(benchTokens!, itp.Delegates);
                                var benchStmts = benchParser.GetStatements().ToList();
                                Program.PrecompileFunctions(benchStmts, itp);
                                r = itp.Interpret(benchStmts, benchFile);
                            }

                            actualRuns++;
                            switch (gcPeriod)
                            {
                                case > 0 when i % gcPeriod == gcPeriod - 1:
                                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                                    GC.WaitForPendingFinalizers();
                                    try
                                    {
                                        GC.RefreshMemoryLimit();
                                    }
                                    catch
                                    {
                                        // ignored
                                    }
                                    break;

                                case 0 when i > 0 && i % 500 == 0:
                                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                                    GC.WaitForPendingFinalizers();
                                    break;
                            }

                            if (!ReplInterrupt.Pending && (isBc || r?.HasError != true)) continue;
                            break;
                        }
                    }
                    finally
                    {
                        itp.SuppressWarnings = false;
                        if (quietMode) { Console.SetOut(savedOut!); itp.Out = savedBenchSink; }
                        CharBufferPool.Return(benchBuf!);
                    }

                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    var benchElapsed = (DateTime.Now.Ticks - benchT0) / 10000;
                    if (quietMode) { Console.SetOut(savedOut!); itp.Out = savedBenchSink; }
                    if (wantBc)
                    {
                        var outPath = bcOut ?? Path.ChangeExtension(benchFile, ".slcbc");
                        try
                        {
                            var rep = itp.ExportTopLevelToBundle(outPath, null);
                            Console.WriteLine(
                                $"  [bc] {Path.GetFileName(benchFile)} -> {Path.GetFileName(outPath)}: {rep}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  [bc] error: {ex.Message}");
                        }
                    }

                    times.Clear();
                    times.Add(benchElapsed);

                    if (ReplInterrupt.Take())
                        Console.WriteLine($"  Interrupted by user after {actualRuns} run(s).");

                    var total = times.Count > 0 ? times[0] : 0;
                    var avg = actualRuns > 0 ? total / actualRuns : 0;
                    Console.WriteLine($"  File: {benchFile}");
                    Console.WriteLine($"  Runs: {actualRuns}");
                    Console.WriteLine($"  Total: {total} ms  Avg: {avg} ms");
                    Console.WriteLine("  Exit code: {0}", Environment.ExitCode);
                    if (total <= 0) return true;
                    var thru = actualRuns * 100_000 / total;
                    Console.WriteLine($"  Throughput: {thru / 100}.{thru % 100:D2} runs/s");
                    return true;
                }

            default:
                return false;
        }
    }

    private static object? EvaluateOnInterpreter(Interpreter itp, Expr expr)
    {
        var method = typeof(Interpreter).GetMethod("Evaluate", BindingFlags.NonPublic | BindingFlags.Instance);
        return method?.Invoke(itp, [expr]);
    }
}