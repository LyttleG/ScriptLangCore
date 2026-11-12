namespace ScriptLangCore;

internal static partial class Program
{
    /// <summary>Runs a bytecode bundle as a program: load it and invoke the entry it declares, with no
    /// arguments, exactly as the `.run -bc` load path does. The entry is the synthetic program body a bundle
    /// written since Compiler.CompileProgram carries, so this runs the file's top level. A bundle with no entry
    /// is the function library every bundle was before that existed, and is refused by name rather than doing
    /// nothing. Exit code: the run's own exit() code when the program ended with one (0 included, which is
    /// what `exit(CheckFailed())` answers for a suite that passed), 1 refused (no entry) or the run failed,
    /// 2 no such file.</summary>
    private static void RunBytecodeBundle(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: file not found: {path}");
            Environment.ExitCode = ExitCode.Usage;
            return;
        }

        using var handle = InterpreterManager.Create(caseSensitive: true, context: "BundleRun");
        // The LOAD can refuse a bundle: a body that references a type this environment does not have (measured:
        // a stripped bundle of a program declaring an interface died here with "type 'IShape' referenced by the
        // bytecode is not available in this environment"), a body the verifier rejects, a blob from another
        // version. It goes through the same one report path as a failed entry, because a shipped binary must
        // never answer script input with an unhandled exception and a stack trace.
        ScriptFunction? entry;
        int count;
        try { (entry, count) = handle.Instance.LoadBundleFromFile(fullPath, replayImports: false); }
        catch (Exception ex) { ReportBundleFailure(ex); return; }
        Console.WriteLine($"Loaded {count} function(s) from {Path.GetFileName(fullPath)}");
        // The run's directory for the compiled entry below. A bundle that carries its source gets it from its
        // own Interpret (which is handed this same path); a SOURCE-LESS program has no text to get it from, so
        // it is seeded here, or the entry would run with no directory at all and every `./` path in it would
        // refuse (see SetRunDirectoryForBundle).
        handle.Instance.SetRunDirectoryForBundle(fullPath);
        // A bundle that carries its program as SOURCE runs it the way a .slc file runs, and needs no
        // compiled entry at all: the source IS the entry.
        if (TryRunCarriedSource(handle.Instance, fullPath)) return;

        if (entry == null)
        {
            Console.Error.WriteLine("This bundle declares no entry, so it holds functions only and there is nothing to run.");
            Console.Error.WriteLine("A bundle written by --compile carries its source and runs without one: `--compile <script>`;");
            Console.Error.WriteLine("otherwise load it in the REPL and call a function: `.loadslcbc <file>.slcbc` then `.functions`.");
            Environment.ExitCode = ExitCode.Failed;
            return;
        }

        var result = InvokeBundleEntry(handle.Instance, entry);
        if (result != null)
            Console.WriteLine($"entry {entry.Declaration.Name}() => {result}");
    }

    /// <summary>Loads ONE compiled tool bundle and RUNS it, so its RegisterMcpTool calls reach the (static)
    /// MCP registry. This is the --mcp-bc arm: the carried source runs when the bundle has one, the compiled
    /// entry otherwise, exactly as the CLI's bundle arm decides. It never exits the process and never sets its
    /// exit code, because a tool that cannot load must not take the server down, and a server that later exits
    /// normally must not inherit an exit code from a bad bundle.</summary>
    private static void RunToolBundle(string bundlePath)
    {
        var fullPath = Path.GetFullPath(bundlePath);
        using var handle = InterpreterManager.Create(caseSensitive: true, context: "McpToolBundle");
        var (entry, count) = handle.Instance.LoadBundleFromFile(fullPath, replayImports: false);
        handle.Instance.SetRunDirectoryForBundle(fullPath);
        if (TryRunCarriedSource(handle.Instance, fullPath, setExitCodeOnFailure: false)) return;
        if (entry == null)
        {
            Logging.Log.Error("Init", $"tool bundle '{Path.GetFileName(fullPath)}' declares no entry and carries"
                + " no source, so it registers no tool");
            return;
        }
        InvokeBundleEntry(handle.Instance, entry, setExitCodeOnFailure: false);
    }

    /// <summary>Reports a failure from a bundle run: the engine's own message, never a CLR stack trace.
    /// A bundle with no source to fall back on reaches here whenever its entry needs the tree.</summary>
    private static void ReportBundleFailure(Exception ex, bool setExitCodeOnFailure = true)
    {
        ConsoleSafe.SetColor(ConsoleColor.Red);
        Console.Error.WriteLine("Error: " + (ex is ScriptException ? ex.Message : Interpreter.FriendlyExceptionMessage(ex)));
        ConsoleSafe.ResetColor();
        // THE TRACE IS AVAILABLE ON DEMAND, and not by default: every report goes through here (the CLI's bundle arm, a
        // REPL dot command that failed, the top-level catch in Main), so `Diagnostics.Trace("STACK_TRACE", true)` or
        // SLC_STACK_TRACE=1 prints it while a user meets one actionable line instead of a native stack.
        if (Diagnostics.On("STACK_TRACE")) Console.Error.WriteLine(ex.ToString());
        if (setExitCodeOnFailure) Environment.ExitCode = ExitCode.Failed;
    }

    /// <summary>Invokes a bundle's compiled entry (no arguments) and returns what it returned, or null when it
    /// aborted or failed (reported here). A script's own exit() is the run's VERDICT rather than a failure:
    /// `exit(CheckFailed())` leaves a suite at 0 when nothing failed, and the abort carries that code in its
    /// VALUE, which the compiled-entry path has no exit-code comparison to read (the distinction
    /// Interpreter.AbortExitCode draws). Reporting it as a failure instead is what made a source-less bundle
    /// exit 1 with "Error: 0" where its .slc exits 0.
    /// The exit code is set even when <paramref name="setExitCodeOnFailure"/> is false, because an abort is
    /// not the failure that flag is about: a .slc run sets it in Interpret's own ScriptAbort arm, and a
    /// bundle has to answer as the file it was built from does.</summary>
    private static object? InvokeBundleEntry(Interpreter itp, ScriptFunction entry, bool setExitCodeOnFailure = true)
    {
        try
        {
            return itp.InvokeScriptFunction(entry, [], 0, 0);
        }
        catch (Exception ex)
        {
            if (ex is ScriptException sx && Interpreter.AbortExitCode(sx) is int abortCode)
            {
                Environment.ExitCode = abortCode;
                return null;
            }
            ReportBundleFailure(ex, setExitCodeOnFailure);
            return null;
        }
    }

    /// <summary>Runs the program a bundle CARRIES, when it carries one: the source is the entry, so it goes
    /// through the same parse + PrecompileFunctions + Interpret a .slc file takes, which is what lets a
    /// top-level const or a generic `new` be part of a runnable bundle. Returns true when it ran (reporting
    /// any failure itself), false when the bundle carries no source, so the caller uses the compiled entry.
    /// Both callers share this, so the CLI and `.run` cannot answer differently.</summary>
    private static bool TryRunCarriedSource(Interpreter itp, string bundlePath, bool setExitCodeOnFailure = true)
    {
        if (string.IsNullOrEmpty(itp.LoadedBundleSource)) return false;
        var statements = ParseBundleSource(itp.LoadedBundleSource!, bundlePath, itp, out var parseError);
        if (statements == null)
        {
            Console.Error.WriteLine($"Error: the source this bundle carries did not parse: {parseError}");
            if (setExitCodeOnFailure) Environment.ExitCode = ExitCode.Refused;
            return true;
        }
        try
        {
            PrecompileFunctions(statements, itp);
            var carried = itp.Interpret(statements, bundlePath);
            if (carried.HasError)
            {
                var info = carried.ErrorInfo;
                if (info != null)
                    Logging.Log.Error($"{info.Value.Code}", info.Value.Message, carried.ErrorLine, carried.ErrorColumn);
                else if (carried.ErrorMessage != null)
                    Console.Error.WriteLine("Error: " + carried.ErrorMessage);
                if (setExitCodeOnFailure) Environment.ExitCode = ExitCode.Failed;
                return true;
            }
            // No notice of its own: the caller reports the load ("Loaded N function(s) from X" in the CLI,
            // the .run summary in the REPL), and the program's own output is what the user asked to see.
        }
        catch (Exception ex) { ReportBundleFailure(ex, setExitCodeOnFailure); }
        return true;
    }

    /// <summary>Parses a bundle's carried source. Returns null and sets <paramref name="parseError"/> when it
    /// does not parse, which is the only failure a bundle's own text can have before it runs.</summary>
    private static List<Stmt>? ParseBundleSource(string sourceText, string sourcePath, Interpreter itp, out string? parseError)
    {
        parseError = null;
        try
        {
            var lexer = new Lexer(sourceText.AsMemory());
            var parser = new Parser(lexer.GetTokens(), itp.Delegates, source: sourceText);
            return parser.GetStatements().ToList();
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
            return null;
        }
    }
}
