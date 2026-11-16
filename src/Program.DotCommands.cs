namespace ScriptLangCore;

internal static partial class Program
{
    /// <summary>THE ONE ENTRY POINT of the dot-command surface: the REPL calls this, and nothing else.
    ///
    /// TWO DISPATCHERS, ONE SURFACE, and the split is by WHAT A COMMAND NEEDS rather than by history: a command that
    /// only touches the INTERPRETER lives in <see cref="Repl.DotCommands"/> (<c>.vars</c>, <c>.gc</c>, <c>.help</c>,
    /// <c>.type</c>, <c>.bench</c>, ...), and a command that needs the SESSION lives here, in the arm that owns the
    /// handle (<c>.run</c>, <c>.reset</c>, <c>.state</c>, <c>.mcp</c>, ...). NO NAME MAY APPEAR IN BOTH, which is the
    /// rule this entry point exists to make checkable: six arms here were DEAD for that reason (<c>.clear</c>,
    /// <c>.gc</c>, <c>.history</c>, <c>.edit</c>, <c>.help</c>, <c>.vars</c>), and the dead <c>.reset</c> arm was the
    /// one that implemented the DOCUMENTED behaviour, so <c>.reset hard</c> never reached it. The dead arms are
    /// deleted rather than kept as a second copy, and <c>tools/check-dot-commands.sh</c> fails the build if a name
    /// grows back into both dispatchers or if a name either dispatcher handles is missing from
    /// <see cref="Repl.DotCommands.CommandHelp"/> (the table <c>.help</c> prints and <c>IsKnownCommand</c> reads).
    ///
    /// An ALIAS is expanded HERE, once, before either dispatcher: the old expansion lived inside the
    /// interpreter-side dispatcher and bridged to this one by REFLECTION with an <c>Interpreter</c> where an
    /// <c>InterpreterHandle</c> belongs, so an alias to a session command (<c>.alias r .run</c>) answered
    /// "Unknown command" instead of running.</summary>
    internal static InterpreterHandle DispatchDotCommand(string line, InterpreterHandle handle)
    {
        var expanded = Repl.DotCommands.ExpandAlias(line);
        if (Repl.DotCommands.Handle(expanded, handle.Instance)) return handle;
        return HandleDotCommand(expanded, handle);
    }

    /// <summary>The SESSION commands: the arms that need the handle (a file run, a handle swap, the MCP server), as
    /// opposed to <see cref="Repl.DotCommands"/>' interpreter-local ones. Reached only through
    /// <see cref="DispatchDotCommand"/>.</summary>
    private static InterpreterHandle HandleDotCommand(string line, InterpreterHandle handle)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();

        Repl.ReplInterrupt.Clear();
        handle.Instance._interruptRequested = false;
        try { ConsoleSafe.ResetColor(); }
        catch
        {
            // ignored
        }

        switch (cmd)
        {
            case ".exit":
            case ".quit":
                // Clean up I/O redirect if active
                if (_redirectOutWriter != null)
                {
                    _redirectOutWriter.Dispose();
                    _redirectOutWriter = null;
                    _redirectOutFile = null;
                    Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                    Interpreter.CaptureProcessStdout();
                }
                _exitRequested = true;
                return handle;

            case ".vm":
                var vmSub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";
                switch (vmSub)
                {
                    case "trace":
                        if (parts.Length < 3)
                        {
                            Console.WriteLine($"  VM trace: {(Vm.Trace ? "ON" : "OFF")}   (.vm trace on|off)");
                            break;
                        }
                        Vm.Trace = parts[2].Equals("on", StringComparison.OrdinalIgnoreCase);
                        Console.WriteLine($"  VM trace: {(Vm.Trace ? "ON" : "OFF")}");
                        break;
                    default:
                        Console.WriteLine($"  VM trace: {(Vm.Trace ? "ON" : "OFF")}   (.vm trace on|off)");
                        Console.WriteLine($"  VM enabled: {(VmEnabled ? "ON" : "OFF")}   (.vm off re-enables the tree-walker for A-B tests)");
                        break;
                }
                return handle;

            case ".cd":
                if (parts.Length < 2)
                {
                    Console.WriteLine($"  Base directory: {_scriptBaseDir}");
                    return handle;
                }
                var newDir = string.Join(" ", parts.Skip(1));
                newDir = Environment.ExpandEnvironmentVariables(newDir);
                if (Directory.Exists(newDir))
                {
                    _scriptBaseDir = Path.GetFullPath(newDir);
                    Console.WriteLine($"  Base directory set to: {_scriptBaseDir}");
                }
                else
                {
                    Console.WriteLine($"Directory not found: {newDir}");
                }
                return handle;

            case ".echo":
                switch (parts.Length)
                {
                    case > 1 when parts[1] == "on":
                        _echoMode = true; Console.WriteLine("Echo mode: ON");
                        break;
                    case > 1 when parts[1] == "off":
                        _echoMode = false; Console.WriteLine("Echo mode: OFF");
                        break;
                    default:
                        Console.WriteLine($"Echo mode: {(_echoMode ? "ON" : "OFF")}");
                        break;
                }
                return handle;

            case ".time":
                _timingMode = !_timingMode;
                Console.WriteLine($"Timing mode: {(_timingMode ? "ON" : "OFF")}");
                return handle;

            case ".functions":
                var funcs = handle.Instance.GetUserDeclarations().OrderBy(f => f.Line).ThenBy(f => f.Column).ThenBy(f => f.Name).ToList();
                if (funcs.Count == 0) { Console.WriteLine("No user declarations."); return handle; }
                var fnNamePad = funcs.Max(f => f.Name.Length) + 2;
                var fnTypePad = funcs.Max(f => (f.Type ?? "").Length) + 2;
                Console.WriteLine($"  {"Name".PadRight(fnNamePad)} {"Type".PadRight(fnTypePad)} {"Line",4}  Col");
                Console.WriteLine($"  {new string('\u2500', fnNamePad + fnTypePad + 12)}");
                foreach (var f in funcs)
                    Console.WriteLine($"  {f.Name.PadRight(fnNamePad)} {f.Type?.PadRight(fnTypePad) ?? "".PadRight(fnTypePad)} {f.Line,4}  {f.Column}");
                return handle;

            case ".tree":
                if (parts.Length < 2) { Console.WriteLine("Usage: .tree <variable_name>"); return handle; }
                var tvName = parts[1];
                var tvVal = handle.Instance.GetVariableValue(tvName);
                if (tvVal == null) { Console.WriteLine($"Variable '{tvName}' is null or not found."); return handle; }
                PrintTree(tvName, tvVal, "", true);
                return handle;

            case ".import":
            case ".include":
                if (parts.Length < 2) { Console.WriteLine("Usage: .import <file>"); return handle; }
                var importArg = string.Join(" ", parts.Skip(1));
                var importFiles = ResolveRunFiles(importArg);
                if (importFiles.Count == 0) { Console.WriteLine($"No files matched: {importArg}"); return handle; }
                foreach (var impFile in importFiles)
                    ExecuteReplFile(impFile, handle.Instance, resetAfter: false);
                Console.WriteLine($"  Imported: {importFiles.Count} file(s)");
                return handle;

            case ".infos":
                PrintInfos();
                return handle;

            case ".debug":
                switch (parts.Length)
                {
                    case > 1 when parts[1] == "on":
                        EnableDebugger(false);
                        Console.WriteLine("  Debugger enabled. Use .break <line> to set breakpoints.");
                        break;
                    case > 1 when parts[1] == "off":
                        DisableDebugger();
                        break;
                    default:
                        Console.WriteLine($"Debugger is {(IsDebuggerEnabled ? "ON" : "OFF")}");
                        break;
                }
                return handle;

            case ".break":
                if (parts.Length < 2 || !int.TryParse(parts[1], out var bLine))
                {
                    Console.WriteLine("Usage: .break <line>");
                    return handle;
                }
                SetBreakpoint(bLine);
                return handle;

            case ".mcp":
                {
                    var tools = McpServer.ListTools();
                    if (tools.Count == 0)
                        Console.WriteLine("No MCP tools registered.");
                    else
                    {
                        Console.WriteLine($"Registered MCP tools ({tools.Count}):");
                        foreach (var t in tools)
                        {
                            var sig = new StringBuilder();
                            sig.Append("  ").Append(t.Name).Append('(');
                            var first = true;
                            foreach (var p in t.Handler.Declaration.Params)
                            {
                                if (!first) sig.Append(", ");
                                first = false;
                                sig.Append(p.Name).Append(": ").Append(FormatTypeName(p.Type));
                            }
                            sig.Append(")");
                            Console.WriteLine($"{sig,-45} {t.Description}");
                        }
                    }
                    Console.WriteLine("  [Protocol methods: ping, initialize, logging/setLevel, resources/list]");
                }
                return handle;

            case ".call":
                {
                    if (parts.Length < 3)
                    {
                        Console.WriteLine("Usage: .call <tool_name> <json_args>");
                        Console.WriteLine("Example: .call test_echo {\"message\":\"hi\"}");
                        return handle;
                    }
                    var toolName = parts[1];
                    var jsonArgs = string.Join(" ", parts.Skip(2));
                    var request = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{toolName}\",\"arguments\":{jsonArgs}}}}}";
                    var response = McpServer.Dispatch(request);
                    // Pretty-print, use JsonParseExtensions (AOT-safe)
                    try
                    {
                        var parsed = JsonParseExtensions.ParseJson(Encoding.UTF8.GetBytes(response));
                        if (parsed is Dictionary<string, object?> dict)
                        {
                            if (dict.TryGetValue("result", out var r) && r is Dictionary<string, object?> rd
                                && rd.TryGetValue("content", out var c) && c is IList cl)
                            {
                                foreach (var item in cl)
                                {
                                    if (item is Dictionary<string, object?> idict && idict.TryGetValue("text", out var txt))
                                        Console.WriteLine(txt);
                                }
                            }
                            else if (dict.TryGetValue("error", out var er) && er is Dictionary<string, object?> ed)
                            {
                                Console.WriteLine($"Error: {ed.GetValueOrDefault("message")}");
                            }
                            else Console.WriteLine(response);
                        }
                        else Console.WriteLine(response);
                    }
                    catch { Console.WriteLine(response); }
                }
                return handle;

            case ".log":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .log <level>  (debug, info, notice, warning, error)");
                    Console.WriteLine($"  Current level: {McpServer.LogLevel ?? "disabled"}");
                    return handle;
                }
                var newLevel = parts[1].ToLowerInvariant();
                if (newLevel == "off") { McpServer.SetLogLevel(null); Console.WriteLine("Logging disabled."); }
                else { McpServer.SetLogLevel(newLevel); Console.WriteLine($"Log level set to: {newLevel}"); }
                return handle;

            case ".varsall":
                var allK = handle.Instance.DebugGlobalKeys().ToList();
                // Parse filter categories: ".varsall [types][native]" or ".varsall types native"
                var filterCats = new HashSet<string>();
                for (var pi = 1; pi < parts.Length; pi++)
                {
                    var raw = parts[pi].ToLowerInvariant();
                    // Extract categories from bracket groups or whole words
                    foreach (var cat in raw.Split('[', ']', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (cat.Length == 0) continue;
                        // Map common aliases to actual category names
                        var mapped = cat switch
                        {
                            "namespace" => "namespaces",
                            "native" => "native",
                            "delegate" => "delegates",
                            "type" => "types",
                            "class" => "classes",
                            "struct" => "structs",
                            "function" => "functions",
                            "list" => "lists",
                            "dictionary" => "dictionaries",
                            "refcell" => "ref cells",
                            "ref" => "ref cells",
                            "event" => "events",
                            "instance" => "instances",
                            _ => cat
                        };
                        filterCats.Add(mapped);
                    }
                }
                var groups = new Dictionary<string, List<string>>();
                foreach (var k in allK.OrderBy(x => x))
                {
                    if (k.StartsWith("`") || k.Contains("`")) continue;
                    var val = handle.Instance.GetVariableValue(k);
                    var kind = CategorizeValue(val, k, handle.Instance.IsBuiltinName(k));
                    if (filterCats.Count > 0 && !filterCats.Contains(kind)) continue;
                    if (!groups.ContainsKey(kind)) groups[kind] = [];
                    groups[kind].Add(k);
                }
                if (groups.Count == 0) { Console.WriteLine($"No matching entries."); return handle; }
                Console.WriteLine(filterCats.Count > 0
                    ? $"Categories: {string.Join(", ", groups.Keys.OrderBy(k => k))} ({groups.Values.Sum(l => l.Count)} entries):"
                    : $"All global scope keys ({allK.Count}):");
                var lineC = 0;
                var catOrder = new[] { "types", "delegates", "namespaces", "native", "classes", "structs", "functions", "user", "lists", "dictionaries", "ref cells", "instances", "events", "null", "other" };
                foreach (var g in groups.OrderBy(g => Array.IndexOf(catOrder, g.Key) >= 0 ? Array.IndexOf(catOrder, g.Key) : int.MaxValue))
                {
                    ConsoleSafe.SetColor(ConsoleColor.DarkYellow);
                    Console.WriteLine($"  [{g.Key}] ({g.Value.Count}):");
                    ConsoleSafe.ResetColor();
                    foreach (var name in g.Value)
                    {
                        Console.Write("    " + name.PadRight(24));
                        lineC++;
                        if (lineC % 3 == 0) Console.WriteLine();
                    }
                    if (g.Value.Count % 3 != 0) Console.WriteLine();
                    lineC = 0;
                    // Pause between groups if many
                    if (groups.Count <= 3) continue;
                    var saved = Console.ForegroundColor;
                    ConsoleSafe.SetColor(ConsoleColor.DarkGray);
                    Console.Write("  -- more --");
                    ConsoleSafe.SetColor(saved);
                    Console.ReadKey(true);
                    Console.WriteLine();
                }
                return handle;

            case ".strict":
                handle.Instance.SetCaseSensitive(true);
                Console.WriteLine("Case-sensitive mode: ON (strict)");
                return handle;

            case ".lax":
                handle.Instance.SetCaseSensitive(false);
                Console.WriteLine("Case-sensitive mode: OFF (lax)");
                return handle;

            case ".state":
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .state keep/reset  (default: keep)");
                    return handle;
                }
                if (string.Equals(parts[1], "keep", StringComparison.OrdinalIgnoreCase))
                { _keepGlobals = true; Console.WriteLine("State: KEPT"); }
                else if (string.Equals(parts[1], "reset", StringComparison.OrdinalIgnoreCase))
                { _keepGlobals = false; Console.WriteLine("State: RESET"); }
                else
                    Console.WriteLine("Unknown. Use 'keep' or 'reset'.");
                return handle;

            case ".trace":
                if (parts.Length < 2 || (parts[1] != "on" && parts[1] != "off"))
                    Console.WriteLine("Usage: .trace on|off");
                else
                {
                    handle.Instance._traceEnabled = parts[1] == "on";
                    Console.WriteLine($"Trace: {(parts[1] == "on" ? "ON" : "OFF")}");
                }
                return handle;

            case ".reset":
                var resetHard = parts.Length > 1 && parts[1].Equals("hard", StringComparison.OrdinalIgnoreCase);
                if (resetHard)
                {
                    // A HOST'S INTERPRETER IS NOT THIS REPL'S TO REPLACE. `.reset hard` disposes the handle and makes
                    // a fresh interpreter, which is the CLI's behaviour (nothing outside holds that interpreter); an
                    // embedded session's interpreter is held by its host through the ABI handle, so the swap would
                    // leave the host driving a dead one. `.reset all` clears it in place, which is the same intent.
                    if (!handle.Owns)
                    {
                        ConsoleSafe.SetColor(ConsoleColor.Yellow);
                        Console.WriteLine(".reset hard replaces the interpreter, and this one belongs to the embedding host: use `.reset all` to clear it in place.");
                        ConsoleSafe.ResetColor();
                        return handle;
                    }
                    handle.Dispose();
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    GC.WaitForPendingFinalizers();
                    handle = InterpreterManager.Create(caseSensitive: true, context: "REPL (after reset)");
                    ReplHistory.Clear();
                    _pendingEditLine = -1;
                    Console.WriteLine("Hard reset, new Interpreter instance created.");
                    return handle;
                }
                var resetAll = parts.Length > 1 && parts[1].Equals("all", StringComparison.OrdinalIgnoreCase);
                handle.Instance.Reset(keepGlobals: false, clearTypes: resetAll);
                ReplHistory.Clear();
                _pendingEditLine = -1;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Console.WriteLine(resetAll
                    ? "Full reset, types, classes, and modules also cleared."
                    : "Interpreter reset, all state cleared.");
                return handle;

            case ".run":
                // A session launched to serve goes on serving: the `.run` a reader reaches for after an interrupt is
                // what brings the listener back (see McpServer.EnsureHttpServerServing).
                McpServer.EnsureHttpServerServing();
                var runQuiet = parts.Contains("--quiet") || parts.Contains("-q");
                if (parts.Length < 2)
                {
                    Console.WriteLine("Usage: .run <file|pattern>[|<file2>..] [-q|--quiet] [-bc [out.slcbc]] [-- <script args>]");
                    Console.WriteLine("  .run test.slc              single file");
                    Console.WriteLine("  .run stress_*.slc          glob pattern");
                    Console.WriteLine("  .run a.slc|b.slc        pipe-separated");
                    Console.WriteLine("  -bc: load the .slcbc sibling if present, else run the source and build it");
                    Console.WriteLine("  .run tool.slc -- a b          the script's Application.Args = [its path, a, b]");
                    Console.WriteLine("  .run file.slcbc               load a bytecode bundle directly");
                    return handle;
                }
                var wantBc = false;
                string? bcOut = null;
                var runArgs = new List<string>();
                // EVERYTHING AFTER `--` IS THE SCRIPT'S ARGUMENTS, not another file. Without that, a tool whose input is its
                // command line (`slc-pack.slc <launcher> <payload> ...`) could only be driven from a shell: `.run
                // slc-pack.slc` answered the tool's own usage line, reported from a live session. A separator rather than
                // "the first token is the file and the rest are its arguments", because `.run` already takes SEVERAL files
                // (`|`-separated) plus `-bc` and `-q`, so a positional rule would have to guess which token is which.
                var scriptArgs = SplitScriptArguments(line);
                var afterSeparator = false;
                for (var ai = 1; ai < parts.Length; ai++)
                {
                    var p = parts[ai];
                    if (afterSeparator) { continue; }   // taken from the raw line, quotes and all
                    if (p == "--") { afterSeparator = true; continue; }
                    if (p == "-bc")
                    {
                        wantBc = true;
                        if (ai + 1 < parts.Length && parts[ai + 1].EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase)) bcOut = parts[++ai];
                        continue;
                    }
                    if (p is "--quiet" or "-q") continue;
                    runArgs.Add(p);
                }
                var filteredArgs = string.Join(" ", runArgs);
                var specs = filteredArgs.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var runTimer = Stopwatch.StartNew();
                var runFileCount = 0;
                // A file the engine could not load never RAN, so it decides no verdict, but it must not read as a plain
                // success either: `Done: 1 file(s)` was printed for a bundle nothing could read, which is the one line a
                // reader trusts. Counted here and named on that line (the Error line above it says what was wrong).
                var runFailedCount = 0;
                var savedRunOut = runQuiet ? Console.Out : null; var savedRunSink = runQuiet ? handle.Instance.Out : null;   // both doors: a session prints to Interpreter.Out
                if (runQuiet) { Console.SetOut(TextWriter.Null); handle.Instance.Out = TextWriter.Null; }
                // WHAT WAS THERE BEFORE, so a run's arguments cannot leak into the next run (or into a plain statement typed
                // afterwards): restored in the `finally` below, whichever way the loop ends.
                var savedScriptArgs = handle.Instance.ScriptArguments;
                try
                {
                    foreach (var spec in specs)
                    {
                        var files = ResolveRunFiles(spec);
                        if (files.Count == 0)
                        {
                            Console.WriteLine($"No files matched: {spec}");
                            continue;
                        }
                        foreach (var f in files)
                        {
                            // A FILE'S failure is that file's verdict, never the session's end: a bundle this engine cannot
                            // LOAD (a `.slcbc` from another version, a truncated or foreign blob) used to throw out of this
                            // loop, through HandleDotCommand and RunRepl, and end the process with an unhandled
                            // ScriptException plus a stack trace (reported from a live session, `.run <x>.slcbc`). The
                            // report is the engine's one bundle path (ReportBundleFailure: `Error: <message>`), and the
                            // loop keeps going so a `.run a|b|c` still runs b and c and still prints its verdict line.
                            try
                            {
                                handle.Instance.Reset(keepGlobals: _keepGlobals, clearTypes: !_keepGlobals);
                                // Each file's verdict is its own: the Check counters are per interpreter and
                                // `.run` used to leave them, so a suite run twice in one session doubled its
                                // count and a passing file after a failing one inherited the failure.
                                handle.Instance.ResetCheckCounters();
                                // THE RUN'S OWN ARGUMENTS, the CLI's convention: [the file itself, ...what followed `--`].
                                // Set even when nothing followed the separator, so a script reads ITS path where the REPL's
                                // own flags used to appear.
                                var fileArgs = new List<string> { f };
                                foreach (var extra in scriptArgs) fileArgs.Add(extra);
                                handle.Instance.ScriptArguments = fileArgs;
                                if (!_keepGlobals) handle.Instance.SuppressWarnings = true;

                                // Bytecode-side resolution: an explicit .slcbc arg, or -bc with an
                            // existing sibling, loads the bundle instead of parsing the source.
                            var isBcArg = f.EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase);
                            var bcPath = isBcArg ? f : (wantBc ? (bcOut ?? Path.ChangeExtension(f, ".slcbc")) : null);
                            if (bcPath != null && File.Exists(bcPath))
                            {
                                var (loadedEntry, loadedCount) = handle.Instance.LoadBundleFromFile(bcPath);
                                handle.Instance.SetRunDirectoryForBundle(bcPath);
                                // No "loaded N function(s)" line: `.run` prints what the program prints, the same as
                                // for a .slc, and the load notice is `.loadslcbc`'s to give.
                                // A carried source IS the program: run it the way a .slc file runs. Only a
                                // bundle with no source falls through to the compiled entry, and that entry is
                                // invoked under the same never-print-a-stack-trace rule as the CLI's arm.
                                if (TryRunCarriedSource(handle.Instance, bcPath, setExitCodeOnFailure: false))
                                {
                                }
                                else if (loadedEntry != null)
                                {
                                    // A bundle declared an entry function, so .run invokes it (no args).
                                    var entryResult = InvokeBundleEntry(handle.Instance, loadedEntry, setExitCodeOnFailure: false);
                                    if (entryResult != null)
                                        Console.WriteLine($"  entry {loadedEntry.Declaration.Name}() => {entryResult}");
                                }
                            }
                            else
                            {
                                // -bc keeps the just-defined functions in the session (same semantics as
                                // loading a bundle); plain .run keeps the historical reset behavior.
                                ExecuteReplFile(f, handle.Instance, resetAfter: !wantBc);
                                if (bcPath != null)
                                {
                                    var rep = handle.Instance.ExportTopLevelToBundle(bcPath, null);
                                    Console.WriteLine($"  [bc] {Path.GetFileName(f)} -> {Path.GetFileName(bcPath)}: {rep}");
                                }
                            }
                            }
                            catch (Exception ex)
                            {
                                // The session, the interpreter and the remaining files all survive a file the engine could
                                // not load; the one line says what was wrong. No exit code is set, because `exitcode` is the
                                // SCRIPT's verdict (an `exit(N)` sets it, a script error does not) and a bundle that never
                                // loaded ran no script: the count below is what tells the reader this file failed.
                                handle.Instance.SuppressWarnings = false;
                                runFailedCount++;
                                ReportBundleFailure(ex, setExitCodeOnFailure: false);
                            }
                            handle.Instance.SuppressWarnings = false;
                            runFileCount++;
                            if (Repl.ReplInterrupt.Pending) break;
                        }
                    }
                }
                finally
                {
                    if (runQuiet && savedRunOut != null) Console.SetOut(savedRunOut);
                    if (runQuiet) handle.Instance.Out = savedRunSink;
                    handle.Instance.ScriptArguments = savedScriptArgs;
                }
                runTimer.Stop();
                var rms = runTimer.ElapsedMilliseconds;
                Console.WriteLine($"  Done: {runFileCount} file(s) in {rms / 60000}:{(rms / 1000) % 60:D2} ({rms} ms)"
                    + (runFailedCount > 0 ? $" ({runFailedCount} failed to load or run)" : ""));
                return handle;

            case ".looprun":
                McpServer.EnsureHttpServerServing();
                if (parts.Length < 3)
                {
                    Console.WriteLine("Usage: .looprun <count> <file> [-bc [out.slcbc]] [-- <script args>]");
                    Console.WriteLine("  .looprun 100 test.slc       run file 100 times");
                    Console.WriteLine("  .looprun 1000 stress_*.slc  1000x with glob");
                    Console.WriteLine("  .looprun 100 lib.slcbc         load bytecode bundle 100 times");
                    Console.WriteLine("  .looprun 5 bench.slc -- 1000   the script reads Application.Args = [its path, 1000]");
                    Console.WriteLine("  -bc: after the loop, export the VM-compiled functions to .slcbc");
                    return handle;
                }
                if (!int.TryParse(parts[1], out var loopCount) || loopCount < 1)
                {
                    Console.WriteLine($"Invalid count: '{parts[1]}'. Must be a positive integer.");
                    return handle;
                }
                var lrWantBc = false;
                string? lrBcOut = null;
                var loopSpecArgs = new List<string>();
                // THE SAME SEPARATOR `.run` TAKES: everything after `--` is the script's command line, so a benchmark
                // driven by its arguments can be looped from a session. The tail comes from the RAW line, which is why
                // quotes survive (`.looprun 5 tool.slc -- "C:\Program Files\app.exe"`).
                var loopScriptArgs = SplitScriptArguments(line);
                var lrAfterSeparator = false;
                for (var li = 2; li < parts.Length; li++)
                {
                    var p = parts[li];
                    if (lrAfterSeparator) { continue; }
                    if (p == "--") { lrAfterSeparator = true; continue; }
                    if (p == "-bc")
                    {
                        lrWantBc = true;
                        if (li + 1 < parts.Length && parts[li + 1].EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase)) lrBcOut = parts[++li];
                        continue;
                    }
                    loopSpecArgs.Add(p);
                }
                var loopSpec = string.Join(" ", loopSpecArgs);
                var loopFiles = ResolveRunFiles(loopSpec);
                if (loopFiles.Count == 0)
                {
                    Console.WriteLine($"No files matched: {loopSpec}");
                    return handle;
                }
                var loopTimer = Stopwatch.StartNew();
                // what the session had before the loop, restored at the end of this arm
                var savedLoopScriptArgs = handle.Instance.ScriptArguments;
                var totalLoops = loopFiles.Count * loopCount;
                Console.WriteLine($"  Running {Path.GetFileName(loopFiles[0])} × {loopCount} = {totalLoops} total executions...");
                var savedOut = Console.Out;
                // AND THE ENGINE'S OWN DOOR: `print` writes to `Interpreter.Out` when a host set one (a SESSION does,
                // see ScriptOut), so `Console.SetOut` alone silenced nothing there and every iteration of `.looprun`
                // flooded the host's pane instead of showing the first one plus a percentage. Both are set, and both
                // restored, so one mechanism covers the CLI and an embedded session.
                var savedLoopSink = handle.Instance.Out;
                int iter;
                handle.Instance.SuppressWarnings = true;
                for (iter = 0; iter < loopCount; iter++)
                {
                    handle.Instance.Reset(keepGlobals: _keepGlobals, clearTypes: !_keepGlobals);
                    // The source is re-parsed below, so the file's extension blocks arrive again. Forgetting the
                    // previous iteration's is what makes that a redefinition rather than a duplicate declaration:
                    // with state KEPT (the default) the guard fired on the script's own declaration, the error was
                    // swallowed, and the iterations executed nothing while still reporting a rate.
                    handle.Instance.ClearExtensionSets();
                    handle.Instance.ClearInterfaces();
                    if (!_keepGlobals) handle.Instance.SuppressWarnings = true;
                    Console.SetOut(iter == 0 ? savedOut : TextWriter.Null);
                    handle.Instance.Out = iter == 0 ? savedLoopSink : TextWriter.Null;
                    if (iter > 0 && iter % 50 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
                    foreach (var f in loopFiles)
                    {
                        // THE LOOP'S ARGUMENTS, the CLI's convention: [the file itself, ...what followed `--`], set
                        // per iteration because each one resets the interpreter.
                        var loopFileArgs = new List<string> { f };
                        foreach (var extra in loopScriptArgs) loopFileArgs.Add(extra);
                        handle.Instance.ScriptArguments = loopFileArgs;
                        if (f.EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase))
                        {
                            // Bytecode bundle: each iteration loads it (no source parse).
                            try { handle.Instance.LoadBundleFromFile(f); }
                            catch (Exception ex)
                            {
                                ConsoleSafe.SetColor(ConsoleColor.Red);
                                Console.WriteLine($"  [iter {iter + 1}] Error: {ex.Message}");
                                ConsoleSafe.ResetColor();
                            }
                            finally
                            {
                                if (!(lrWantBc && iter == loopCount - 1))
                                    handle.Instance.Reset(keepGlobals: _keepGlobals, clearTypes: !_keepGlobals);
                            }
                            if (Repl.ReplInterrupt.Pending) break;
                            continue;
                        }
                        var (sourceMem, fileBuf) = FileReader.ReadFile(f);
                        sourceMem = FileReader.StripDotCommands(sourceMem, ref _timingMode, ref _echoMode);
                        try
                        {
                            var lexer = new Lexer(sourceMem);
                            var tokens = lexer.GetTokens();
                            var parser = new Parser(tokens, handle.Instance.Delegates, source: sourceMem.ToString());
                            var statements = parser.GetStatements().ToList();
                            PrecompileFunctions(statements, handle.Instance);
                            handle.Instance.Interpret(statements, f);
                        }
                        catch (Exception ex)
                        {
                            ConsoleSafe.SetColor(ConsoleColor.Red);
                            Console.WriteLine($"  [iter {iter + 1}] Error: {ex.Message}");
                            ConsoleSafe.ResetColor();
                        }
                        finally
                        {
                            // Keep the last iteration's state when -bc needs to export after the loop.
                            if (!(lrWantBc && iter == loopCount - 1))
                                handle.Instance.Reset(keepGlobals: _keepGlobals, clearTypes: !_keepGlobals);
                        }
                        if (Repl.ReplInterrupt.Pending) break;
                    }
                    // Progress indicator on stderr (does not interfere with script output)
                    var pct = (iter + 1) * 100 / loopCount;
                    Console.Error.Write($"\r  Progress: {pct}% ({iter + 1}/{loopCount})");
                    if (iter == loopCount - 1) Console.Error.WriteLine();
                    if (Repl.ReplInterrupt.Pending) break;
                }
                loopTimer.Stop();
                Console.SetOut(savedOut);
                handle.Instance.Out = savedLoopSink;
                handle.Instance.ScriptArguments = savedLoopScriptArgs;
                if (lrWantBc && loopFiles.Count > 0)
                {
                    var outPath = lrBcOut ?? Path.ChangeExtension(loopFiles[0], ".slcbc");
                    try
                    {
                        var rep = handle.Instance.ExportTopLevelToBundle(outPath, null);
                        Console.WriteLine($"  [bc] {Path.GetFileName(loopFiles[0])} -> {Path.GetFileName(outPath)}: {rep}");
                    }
                    catch (Exception ex) { Console.WriteLine($"  [bc] error: {ex.Message}"); }
                }
                // Force GC to release committed segments back to the OS
                if (loopCount > 1)
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
                handle.Instance.SetReplMode(true);
                var elapsedMs = loopTimer.ElapsedMilliseconds;
                var min = elapsedMs / 60000;
                var sec = (elapsedMs / 1000) % 60;
                var avgMs = elapsedMs / Math.Max(totalLoops, 1);
                if (Repl.ReplInterrupt.Take())
                {
                    Console.WriteLine($"  Interrupted by user after {iter} iterations.");
                }
                Console.WriteLine($"  Done: {totalLoops} runs in {min}:{sec:D2} ({avgMs} ms/run, {totalLoops * 1000 / Math.Max(elapsedMs, 1)} runs/s)");
                return handle;

            default:
                Console.WriteLine($"Unknown command: {cmd}. Type .help for help.");
                return handle;
        }
    }
}
