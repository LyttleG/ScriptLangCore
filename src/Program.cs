namespace ScriptLangCore;

internal static partial class Program
{
    private static string _scriptBaseDir = AppDomain.CurrentDomain.BaseDirectory;
    private static readonly List<string> ReplHistory = [];
    private static bool _echoMode;
    private static bool _timingMode;
    private static int _replLineOffset;
    private static bool _exitRequested;
    private static volatile bool _replInterrupted;

    // Set by the Ctrl+C handler, printed by the REPL loop, which is the thread that owns the console. Writing the
    // notice from the handler raced the loop: the key aborts the console read, so the loop re-printed its prompt
    // almost immediately and the handler's lines landed UNDER that fresh prompt, on screen as "`>>> >>>`" with the
    // notice beneath it. The reader then had to press Enter to get a prompt of their own.
    private static volatile string? _pendingInterruptNotice;
    private static bool _keepGlobals = true;
    private static int _pendingEditLine = -1;
    // I/O redirection
    private static TextWriter? _redirectOutWriter;
    private static string? _redirectOutFile;

    [LibraryImport("shcore.dll", SetLastError = true)]
    private static partial int SetProcessDpiAwareness(int value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessDpiAwarenessContext(int dpiContext);

    private const int DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    private static readonly Assembly Assembly = Assembly.GetExecutingAssembly();
    internal static readonly string? Version = Assembly.GetName().Version?.ToString() ?? "N/A";

    private static void CheckDpi()
    {
        if (!SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            SetProcessDpiAwareness(2);
    }

    // The ENTRY POINT is SYNCHRONOUS, and that is a correctness requirement rather than a style choice: this is a
    // console app whose whole model is thread-affine (COM is initialised on an STA, the interpreter's stacks are
    // [ThreadStatic], the pump's queue is drained by the main thread), so the main thread must BE the STA thread.
    // `[STAThread]` is honoured on the ENTRY POINT and an `async Main` is not it, the compiler synthesizes one from
    // it, so an async entry silently lost the attribute: measured in a lab outside the repo,
    // `[STAThread] static async Task<int> Main()` reports `Thread.CurrentThread.GetApartmentState() == MTA`, and
    // this engine then refuses every COM call with "COM Automation requires an STA thread".
    // The alternative (an async entry plus a single-threaded SynchronizationContext pumping continuations here) was
    // rejected rather than built: the only C#-level await this entry ever had was the shutdown flush, so the context
    // would have existed to keep one `return` off a pool thread. A path that genuinely needs its own STA isolation
    // (a blocking native call that must not own the main thread) belongs on a dedicated `Thread` with
    // `SetApartmentState(ApartmentState.STA)` and a `Join`, which is the shape this engine uses for the main thread
    // itself, and the interpreter's own await machinery never leaves this thread.
    [STAThread]
    private static int Main(string[] args)
    {
        int exitCode;
        try
        {
            exitCode = RunMain(args);
        }
        catch (Exception ex)
        {
            // NOTHING SHIPPED MAY END IN AN UNHANDLED EXCEPTION WITH A STACK TRACE, and a script is input: the measured
            // case was `.run <a .slcbc from another version>`, which printed `Unhandled exception. ... at
            // ScriptLangCore.Program.HandleDotCommand` and killed the REPL. The one report path is the bundle one
            // (`Error: <message>`, through the engine's own map for a non-script failure), and it prints the trace when
            // the STACK_TRACE gate asks for it, so a genuine engine bug stays debuggable without being the default.
            ReportBundleFailure(ex);
            exitCode = ExitCode.Failed;
        }

        // A SHUTDOWN PATH, so a failed flush must not replace the exit code: the work is done and the code IS the
        // verdict (a suite's pass or fail, a script's exit). The filter keeps that promise narrow: a broken pipe or a
        // disposed console is swallowed, while a genuine bug still throws. Reported nowhere on purpose, since stdout is
        // what failed and stderr may be the same closed pipe.
        try { Console.Out.Flush(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }

        return exitCode;
    }

    /// <summary>The exe's dispatch. <paramref name="adopt"/> is the ONE thing a host changes: when it is given, the
    /// REPL arms run on THAT interpreter instead of creating one, which is what `slc_main` needs (a session's thread,
    /// apartment and sinks, and a host that keeps its handle afterwards). Every other arm is the exe's own, unchanged:
    /// a file runs in an interpreter of its own, exactly as the CLI always has.</summary>
    /// <summary>THE ARGUMENTS THIS EXECUTABLE'S DISPATCH OWNS, spelled once, so a front end can ask rather than keep a
    /// copy. `slc_main_owns` answers from this list, and a PACKAGED executable uses it to decide whether its first
    /// argument means "act as the CLI" or "this is my own argument": that is the whole rule a packaged app needs, and
    /// putting the spellings in the host instead would be the stale twin this repository keeps paying for.
    ///
    /// KEEP IN STEP WITH THE SWITCH IN <see cref="RunMain"/>: these are exactly its `args[0]` arms. A suite cannot call
    /// this predicate (no script surface), so `tools/embed-check.sh`'s host pins each spelling and a few negatives.</summary>
    internal static readonly string[] EngineArguments = ["--mcp", "--mcp-bc", "--repl", "--dump-bytecode", "--compile"];

    /// <summary>Would the engine's own dispatch act on this FIRST argument? Case-sensitive, like the switch it mirrors,
    /// and a path or a bare word is never an engine argument.</summary>
    internal static bool OwnsArgument(string? argument)
        => argument is not null && Array.IndexOf(EngineArguments, argument) >= 0;

    private static int RunMain(string[] args, Interpreter? adopt = null)
    {
        // Force UTF-8 encoding to ensure the emoji renders correctly. THAT ASSIGNMENT RE-CREATES Console.Out, so the
        // engine's ProcessStdout baseline is re-captured right after it: otherwise every later print looks like a
        // REDIRECT and a HOST's output sink is bypassed (measured through the ABI harness, 2026-10-31).
        Console.OutputEncoding = Encoding.UTF8;
        Interpreter.CaptureProcessStdout();
        CheckDpi();

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        TrimWorkingSet();

        switch (args.Length)
        {
            // ── MCP server mode ──
            case > 0 when args[0] == "--mcp" || args[0] == "--mcp-bc":
                {
                    // --mcp-bc loads COMPILED tool bundles (.slcbc) where --mcp loads tool sources (.slc).
                    // The MCP registry is static, so a bundle's RegisterMcpTool call registers beside a
                    // source-loaded one. What must NOT be reused is the `.loadslcbc` route: it registers a
                    // bundle's declarations WITHOUT running its top level, and a tool IS a top-level call, so
                    // that route would register no tool at all.
                    var loadBundles = args[0] == "--mcp-bc";
                    string? url = null;
                    var allowedDirs = new List<string>(args.Length);
                    var toolsDirs = new List<string>();
                    var withRepl = false;

                    for (var i = 1; i < args.Length; i++)
                    {
                        if (args[i] == "--url" && i + 1 < args.Length)
                            url = args[++i];
                        else if (args[i] == "--port" && i + 1 < args.Length)
                            url = $"http://localhost:{int.Parse(args[++i])}/";
                        else if (args[i] == "--allowed-dir" && i + 1 < args.Length)
                            allowedDirs.Add(args[++i]);
                        else if (args[i] == "--tools-dir" && i + 1 < args.Length)
                            toolsDirs.Add(args[++i]);
                        else if (args[i] == "--repl")
                            withRepl = true;
                    }

                    // Load the tool scripts. The default is <exe>/tools; --tools-dir names one or more
                    // directories INSTEAD (a relative one resolves against the current directory).
                    //
                    // A tool script is EXECUTED here, so whatever ELSE the directory holds is a hazard: a
                    // script that blocks hangs startup, and a native break kills the process outright.
                    // Measured 2026-10-08 on this repository's own tools/ directory: cdb-handles-probe.slc's
                    // DebugBreak ended the server with STATUS_BREAKPOINT (0x80000003), and
                    // control-behavior-probe.slc never returned. That is why the instruments now live in
                    // assets/language/probes/, and why one script failing is reported rather than fatal.
                    if (toolsDirs.Count == 0)
                        toolsDirs.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tools"));
                    foreach (var dir in toolsDirs)
                    {
                        var toolsDir = Path.GetFullPath(dir);
                        Logging.Log.Info("Init", $"toolsDir={toolsDir} exists={Directory.Exists(toolsDir)}");
                        if (!Directory.Exists(toolsDir)) continue;
                        foreach (var script in Directory.GetFiles(toolsDir, loadBundles ? "*.slcbc" : "*.slc"))
                        {
                            Logging.Log.Info("Init", $"loading tool={Path.GetFileName(script)}");
                            try
                            {
                                if (loadBundles) RunToolBundle(script);
                                else RunFile(script, quiet: true);
                            }
                            catch (Exception ex)
                            {
                                // A tool that cannot load must not take the server down with it. A native
                                // fault inside one still can: that is not catchable from here.
                                Logging.Log.Error("Init", $"tool '{Path.GetFileName(script)}' failed: {ex.Message}");
                            }
                        }
                    }

                    // --repl without --port would conflict: both REPL and MCP server read stdin.
                    // Auto-assign default port to use SSE transport instead.
                    if (withRepl && url == null)
                        url = "http://localhost:8765/";

                    if (withRepl)
                    {
                        // Start MCP server on a background thread
                        var mcpThread = new Thread(() => McpServer.Run(url, [.. allowedDirs]))
                        {
                            IsBackground = true,
                            Name = "MCP-Server"
                        };
                        mcpThread.Start();

                        // Enter the interactive REPL, scripts can test tool functions directly
                        Console.WriteLine("[MCP] Server running in background. REPL ready for testing.");
                        if (url != null)
                            Console.WriteLine($"[MCP] HTTP+SSE listening on {url}");
                        Console.WriteLine("[MCP] Type .mcp to list registered tools.");
                        RunRepl(adopt: adopt);
                    }
                    else
                    {
                        // In pure stdio MCP mode, stdout is the JSON-RPC channel,
                        // write the splash screen to stderr instead.
                        var savedOut = Console.Out;
                        Console.SetOut(Console.Error);
                        PrintBanner();
                        Console.SetOut(savedOut);
                        McpServer.Run(url, [.. allowedDirs]);
                    }
                    break;
                }
            case > 0 when args[0] == "--repl" && args.Length > 1:
                {
                    // REPL mode with auto-run: start REPL then .run the specified script
                    var scriptToRun = args[1];
                    RunRepl(afterRunScript: scriptToRun, adopt: adopt);
                    break;
                }
            case > 0 when args[0] == "--dump-bytecode" && args.Length > 1:
                // Test-only instrument (Program.BytecodeDump.cs): compile the file and print every
                // VM-compiled function's bytecode WITHOUT running it, so a format refactor can be proven
                // byte-identical by diffing that output. Running is what makes a dump useless as a
                // baseline, a suite ends in exit(), and a benchmark suite costs minutes.
                Environment.ExitCode = DumpBytecode(args[1], includeProgram: args.Length > 2 && args[2] == "--program");
                break;
            case > 0 when args[0] == "--compile" && args.Length > 1:
                // Compile-only: parse, precompile, compile the program body and write the bundle, WITHOUT running
                // the script. Same compile step as --dump-bytecode, so a script that opens a window or never ends
                // compiles like any other: nothing executes. Unlike `.run <file> -bc`, this always BUILDS (that
                // dot command means load-or-build, so a second invocation loads the stale bundle).
                // Usage: --compile <file.slc> [out.slcbc] [--source=carry|strip|strict] [--no-source]
                if (!SourceModeFromArgs(args, out var stripSource, out var requireStrip, out var modeError))
                {
                    Console.Error.WriteLine(modeError);
                    return ExitCode.Usage;
                }
                Environment.ExitCode = CompileToBundle(args[1], OutPathFromArgs(args), stripSource, requireStrip);
                break;
            case > 0 when args[0].EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase):
                // CLI mode: run a compiled bytecode bundle and exit. Without this arm a .slcbc matched nothing,
                // so the process fell through to the REPL: no output, no error, just a prompt waiting for input,
                // which reads as a hang.
                RunBytecodeBundle(args[0]);
                break;
            case > 0 when args[0].EndsWith(".slc", StringComparison.OrdinalIgnoreCase):
                // CLI mode: execute a script file and exit. A HOST that adopted an interpreter (`slc_main`) runs it on
                // THAT one, which is what makes every namespace member see the run's own tables and directory.
                RunFile(args[0], quiet: false, adopt: adopt);
                break;
            default:
                // REPL mode: interactive prompt
                RunRepl(adopt: adopt);
                break;
        }

        return Environment.ExitCode;
    }

    // =========================================================================
    // CLI MODE, run a single script file
    // =========================================================================
    /// <summary>The outcome of the last FILE this process ran, so a HOST can report it.
    /// <para>
    /// `slc_main` runs the executable's own dispatch and answers the code the process would exit with, which was ALL a
    /// host could see: reported 2026-10-31 from the launcher, a script that failed inside a `Com.Run` callback answered
    /// <c>{"status":1,"ok":false,"error":false,"fatal":false,"cancelled":false}</c>, with no message, no position and no
    /// frames, because the `.slc` arm built its verdict from the exit code alone while the message had already been
    /// printed to a console the host does not own. `RunFile` records it here and `ScriptRunner.RunMainIn` hands it to
    /// the ABI, which is the same value the CLI prints, from the same run.
    /// </para>
    /// <para>
    /// PER THREAD, because a session's verbs run on the session's own thread: two sessions in one process must not be
    /// able to report each other's failure, and a CLI run is one thread by construction.
    /// </para></summary>
    [ThreadStatic]
    private static RunOutcome? _lastRunOutcome;

    internal static RunOutcome? LastRunOutcome
    {
        get => _lastRunOutcome;
        private set => _lastRunOutcome = value;
    }

    /// <summary>WHAT THE LAST FILE RUN ANSWERED, for a host that reports a verdict (`slc_main`'s file arm). Kept beside
    /// the outcome so the two cannot come from different runs, and null for an arm that answers no value (the REPL, a
    /// compile, a dump), where the dispatch reports a status alone.</summary>
    [ThreadStatic]
    private static object? _lastRunValue;

    internal static object? LastRunValue
    {
        get => _lastRunValue;
        private set => _lastRunValue = value;
    }

    private static void RunFile(string scriptPath, bool quiet = false, Interpreter? adopt = null)
    {
        var fullPath = Path.GetFullPath(scriptPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: file not found, {scriptPath}");
            Environment.Exit(1);
            return;
        }

        //if (!quiet)
        //    Console.WriteLine($"[ScriptLangCore {Version}] Running: {Path.GetFileName(fullPath)}");

        OnInterpreterCreated += Repl.DotCommands.ResetGcBaseline;

        // THE RUN IS ScriptRunner's, and this method only REPORTS it: the same lines to the same stream, with the
        // same exit code. That split is what an embedding ABI needs (a host gets the values back instead of the
        // engine writing to a console it does not own), and it is also what keeps ONE runner rather than a CLI copy
        // and a host copy that would drift the moment either changed.
        //
        // A HOST'S SESSION RUNS THE FILE ON THE INTERPRETER IT ADOPTED. A fresh interpreter inherits its namespaces
        // from `BuiltinBlueprint`, and a namespace member keeps the interpreter that REGISTERED it, so a second
        // interpreter in one process makes every such member answer from the FIRST one's empty tables: measured
        // through the launcher (526-suite corpus), `Bytecode.ExportScript("f")` refused with "entry function 'f' is
        // not VM-compiled or not found in this script" for a file that had just declared it, `Sqlite.Query<T>` answered
        // "Class 'X' not found", `./x` had no current directory, and `Application.SingleInstance`'s cross-process lock
        // never fired. With ONE interpreter those all agree by construction, which is what a session means.
        // THE RUN IS POSTED, AND THE CLI BLOCKS ONCE ON ITS TASK, which is the async entry the plan asks for:
        // `RunFileAsync`/`RunFileInAsync` hand back the run's OWN completion, and `.Result` here drains the run queue
        // until that completion lands (the pump `Task.Wait` has always driven), so the observable behaviour of this
        // entry is unchanged while a host gets the identical Task from the ABI's asynchronous verbs.
        var answer = adopt is null
            ? ScriptRunner.RunFileAwaiting(fullPath)
            : ScriptRunner.Await(ScriptRunner.RunFileInAsync(adopt, fullPath));
        var (outcome, value) = (answer.Outcome, answer.Value);
        LastRunOutcome = outcome;
        LastRunValue = value;
        if (outcome.Fatal)
        {
            Logging.Log.Error("Script", $"Fatal: {outcome.Message}");
            // If VM tracing was on, dump the last ~64 executed instructions, gives the
            // [Lx:Cx]/ip context for fatal errors that escaped with no usable location.
            Vm.DumpCrashRing();
        }
        else if (outcome.HasError)
        {
            Logging.Log.Error(outcome.ErrorCode ?? "Script", outcome.Message ?? "Script error", outcome.Line, outcome.Column);
            // Script frames (innermost first), only present when the error was raised inside
            // a script function, so top-level errors keep their previous single line.
            if (outcome.Frames is { Count: > 0 } frames)
                foreach (var frame in frames)
                    Logging.Log.Error("Script", "   at " + frame, 0, 0);
        }

        // The verdict, exactly as before: a failure is 1, a script's own `exit(n)` is n, and a clean run leaves the
        // process code alone (the engine has already put a non-zero `exitcode` into `result.ExitCode`).
        if (outcome.Status != ExitCode.Ok) Environment.ExitCode = outcome.Status;
    }

    // =========================================================================
    // REPL MODE, interactive prompt
    // =========================================================================
    private static int BraceDepth(string s)
    {
        var depth = 0;
        foreach (var c in s)
        {
            if (c == '{' || c == '(' || c == '[') depth++;
            else if (c == '}' || c == ')' || c == ']') depth--;
        }
        return depth;
    }

    /// <summary>THE EXECUTABLE'S OWN ENTRY POINT, callable from a host: the same dispatch `Main` performs, on an
    /// interpreter the caller owns where a REPL is what an argument list selects. This is what `slc_main` is: a front
    /// end that wants to be a SHORTCUT onto this engine inherits every arm for free (`--repl [script]`, `--compile`,
    /// `--dump-bytecode`, a `.slc`, a `.slcbc`, `--mcp`), so nothing it does can drift from what the exe does.
    ///
    /// It answers the code the process would exit with, and it CANNOT THROW: this is `Main`'s own catch
    /// (<see cref="ReportBundleFailure"/>) reporting one line through the engine's map, because a host that received an
    /// exception across the ABI boundary would have no idea what to do with it.</summary>
    internal static int RunMainIn(Interpreter itp, string[] args)
    {
        // Cleared here so a HOST's verdict can only describe THIS invocation: an arm that runs no file (a compile, a
        // dump, the REPL) must not report the outcome of a file an earlier call ran, nor the value that file answered.
        LastRunOutcome = null;
        LastRunValue = null;
        try { return RunMain(args, adopt: itp); }
        catch (Exception ex)
        {
            ReportBundleFailure(ex);
            return ExitCode.Failed;
        }
    }

    private static List<string> ResolveRunFiles(string spec)
    {
        var expanded = Environment.ExpandEnvironmentVariables(spec);
        if (expanded.Contains('*') || expanded.Contains('?'))
        {
            var result = new HashSet<string>();
            var pattern = Path.GetFileName(expanded);
            // Search spec dir, configured base directory, then base/assets/language/
            var dirs = new List<string>();
            var specDir = Path.GetDirectoryName(expanded);
            if (!string.IsNullOrEmpty(specDir)) dirs.Add(specDir);
            dirs.Add(_scriptBaseDir);
            dirs.Add(Path.Combine(_scriptBaseDir, "assets", "language"));
            foreach (var f in dirs.Where(Directory.Exists)
                         .SelectMany(d => Directory.GetFiles(d, pattern, SearchOption.TopDirectoryOnly)))
                result.Add(Path.GetFullPath(f));
            var sorted = result.ToList();
            sorted.Sort();
            return sorted;
        }
        // Try direct path, then configured base directory, then base/assets/language/
        if (File.Exists(expanded))
            return [Path.GetFullPath(expanded)];
        var inBase = Path.Combine(_scriptBaseDir, expanded);
        if (File.Exists(inBase))
            return [Path.GetFullPath(inBase)];

        var inAssets = Path.Combine(_scriptBaseDir, "assets", "language", expanded);
        if (File.Exists(inAssets))
            return [Path.GetFullPath(inAssets)];

        return [];
    }
}
