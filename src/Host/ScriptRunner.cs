namespace ScriptLangCore;

/// <summary>WHAT A RUN ANSWERED, as DATA rather than as console output.
///
/// WHY IT EXISTS: running a file was a method on the CLI (`Program.RunFile`), and it both RAN and PRINTED: the
/// engine's own log lines, the frames under an error, the fatal path's crash ring. A host that embeds the engine
/// cannot accept that, because the console it would write to belongs to the host (and an embedding ABI has to hand
/// the message back so the host can put it in its own pane). So a run now REPORTS, and the CLI is one of two
/// reporters.
///
/// THE FIELDS ARE THE CLI'S EXISTING ARMS, one per line it prints, and nothing more: <see cref="Status"/> is what
/// the process would have exited with (0 clean, 1 failed, or the script's own `exit(n)` code), <see cref="HasError"/>
/// plus <see cref="ErrorCode"/>/<see cref="Message"/>/<see cref="Line"/>/<see cref="Column"/>/<see cref="Frames"/>
/// are the one error line and the frames under it, and <see cref="Fatal"/> is the engine's unexpected failure, which
/// is the arm that also owes `Vm.DumpCrashRing()`.</summary>
internal readonly struct RunOutcome
{
    /// <summary>What the process exits with: `ExitCode.Ok` (0) for a clean run, `ExitCode.Failed` (1) for a
    /// failure, and the SCRIPT's own code for an `exit(n)`, which is this language's rule (a script's exit IS its
    /// run's verdict). A run stopped by Ctrl+C reports `ExitCode.Failed` with no message, which is the verdict that
    /// arm has always left behind.</summary>
    public int Status { get; init; }

    /// <summary>A script or parse failure: one report line, with the position and the frames the engine produced.</summary>
    public bool HasError { get; init; }

    /// <summary>An UNEXPECTED CLR failure rather than the script's own (the arm that also dumps the VM's crash
    /// ring). Kept apart from <see cref="HasError"/> because its line reads `Fatal: <message>` and, unlike a script
    /// error, it is never the author's fault.</summary>
    public bool Fatal { get; init; }

    /// <summary>The run was STOPPED by its token rather than failing, which a host must be able to tell apart from an
    /// error: `ErrorCode.Cancelled` is uncatchable by a script on purpose, and a runner that reports it as "the script
    /// did something wrong" would be lying. Mirrors `ScriptResult.Cancelled`, which is the engine's own answer.</summary>
    public bool Cancelled { get; init; }

    /// <summary>The engine's error code name, or `Script` where the result carried only a message. It is what the
    /// CLI prints inside the brackets of its log line, and a string rather than an enum so a reporter does not have
    /// to know the enum.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The message, already the engine's own wording (never a raw CLR sentence for a script's mistake).</summary>
    public string? Message { get; init; }

    public int Line { get; init; }

    public int Column { get; init; }

    /// <summary>Script frames, innermost first, and only when the error came from inside a script function.</summary>
    public IReadOnlyList<string>? Frames { get; init; }

    /// <summary>WHAT THE CHECK FAMILY REPORTED during this run, or null when it reported no check at all. It exists
    /// because a suite's own verdict is weak without it: `exit(CheckFailed())` is the script's decision, so the status
    /// says 1 and nothing else, while this names the check and where it was written.</summary>
    internal CheckReport? Checks { get; init; }

    /// <summary>The run's ANSWER came from <see cref="Checks"/> rather than from the script, which happens only when the
    /// script ran checks and left no answer of its own. A host reads it to tell an engine-authored answer from one the
    /// script wrote, because the two share the `result` member.</summary>
    internal bool ResultFromChecks { get; init; }
}

/// <summary>WHAT A RUN REPORTED AND WHAT IT ANSWERED, as ONE value: the pair every door that reports a verdict needs.
///
/// WHY IT IS A TYPE rather than a tuple: the two halves travel together through nine methods and four doors (the CLI,
/// the three ABI verbs, the SQL door and the dispatch), and as a tuple every one of them restated
/// `(RunOutcome Outcome, object? Value)` while four `X`/`XValue` twins existed only to drop the second half. Naming it
/// removes all of that, and it gives the SERIALIZATION one home: <see cref="ValueJson"/> is the verdict's `Result`
/// member, which used to be spelled `value is null ? null : Interpreter.SerializeToText(value)` at every door (three
/// copies in `EmbedApi` alone, one of which a packaged exe never reached because the bytes arm hardcoded the value to
/// null).
///
/// `Value` is the script's own answer: a top-level `return <value>`, the checks document for a run that reported checks
/// (see <see cref="RunOutcome.ResultFromChecks"/>), or null when the run answered nothing. It stays an `object` because
/// the engine's own values are not JSON until a host asks, and `Deconstruct` keeps `var (outcome, value) = ...` working
/// at the call sites that were already written that way.</summary>
internal readonly struct RunAnswer
{
    public RunOutcome Outcome { get; init; }

    public object? Value { get; init; }

    /// <summary>The verdict's `Result` member, or null when the run answered nothing: the ONE place a run's answer is
    /// serialized for a host, so two doors cannot format it differently.</summary>
    public string? ValueJson => Value is null ? null : Interpreter.SerializeToText(Value);

    public static RunAnswer Of(RunOutcome outcome, object? value = null) => new() { Outcome = outcome, Value = value };

    public void Deconstruct(out RunOutcome outcome, out object? value)
    {
        outcome = Outcome;
        value = Value;
    }
}

/// <summary>THE ONE WAY TO RUN A SCRIPT, shared by the CLI and the embedding ABI.
///
/// WHAT IT DOES, in the order a run needs: read the file through the pooled reader, apply the dot-command rule, build
/// an interpreter, parse, run the ONE precompile pass, interpret, and settle the verdict. What it deliberately does
/// NOT do is print: no banner, no `Environment.Exit`, no `Environment.ExitCode`, no `Console` write at all. That is
/// the whole difference from the CLI method it was extracted from, and it is what makes the same run usable behind a
/// C ABI (the values go back to the host instead of into somebody else's console).
///
/// LIFETIME, TWO SHAPES, and the caller picks:
///   - <see cref="RunFile"/> creates an interpreter, runs, and disposes it, which is the CLI's lifetime (one
///     interpreter per file, and disposing it closes anything the script left behind: its forms, its `Events` feeds).
///   - <see cref="RunFileIn"/> and <see cref="RunTextIn"/> run inside an interpreter the CALLER owns forever, which is
///     what an embedding session wants: its globals survive between calls, its sink stays put, and a host can run a
///     file and then call a function that file declared. The pipeline is the same one, so the two lifetimes cannot
///     disagree about what a run IS.
///
/// ONE DEPENDENCY POINTS UPWARD, and it is named here so it is not mistaken for an accident:
/// `Program.PrecompileFunctions` is the single precompile pass (five callers share it, and moving it beside the
/// interpreter is its own round), so a run calls it from `Program`. Nothing calls back the other way: `Program` only
/// asks this class to run and then reports what it answered.</summary>
internal static class ScriptRunner
{
    /// <summary>Runs a `.slc` file in an interpreter of its own. <paramref name="fullPath"/> must already be
    /// absolute, because it is also the path the run reports as its own (`Script.Directory()` and the `./` rule depend
    /// on it). <paramref name="context"/> names the run in the interpreter-leak report, so a leaked one says who
    /// created it.</summary>
    public static RunAnswer RunFile(string fullPath, string context = "RunFile")
    {
        using var handle = InterpreterManager.Create(caseSensitive: true, context: context);
        return RunFileIn(handle.Instance, fullPath);
    }

    /// <summary>Runs a `.slc` file inside an interpreter the caller owns, and leaves that interpreter alive. The
    /// caller is responsible for its disposal, which is the same `using` a session already keeps. Answers BOTH halves
    /// (see <see cref="RunAnswer"/>): the verdict, and what the script returned. The ABI's file verb needs the pair
    /// because a run's answer is part of the verdict (`Result`), and a run that reported checks and left no answer of
    /// its own takes the checks document as that answer (<see cref="WithChecks"/>). ONE pipeline, so a file and a text
    /// cannot disagree about either half.</summary>
    public static RunAnswer RunFileIn(Interpreter itp, string fullPath, CancellationToken token = default, bool resumable = false)
    {
        var (sourceMem, _) = FileReader.ReadFile(fullPath);
        // The dot-command toggles belong to whoever OWNS the console session (the CLI's `_timingMode` and
        // `_echoMode`), so a run reports them into throwaways: a file's `.time` line must not silently switch the
        // caller's own timing mode.
        var timing = false;
        var echo = false;
        sourceMem = FileReader.StripDotCommands(sourceMem, ref timing, ref echo);
        return RunSourceIn(itp, sourceMem, fullPath, token, resumable);
    }

    /// <summary>Runs SOURCE TEXT inside an interpreter the caller owns, answering both halves. No dot-command pass: that
    /// rule belongs to a FILE session (a pasted snippet is code, and stripping `.something` off it would be the tool
    /// guessing). A <paramref name="sourceName"/> that is a path also sets the run's directory, which is what makes `./`
    /// work for a snippet a host says came from somewhere; null means the run has no directory of its own.</summary>
    public static RunAnswer RunTextIn(Interpreter itp, string source, string? sourceName = null, CancellationToken token = default, bool resumable = false)
        => RunSourceIn(itp, source.AsMemory(), sourceName, token, resumable);

    /// <summary>THE SAME RUN, WITH ITS COMPLETION AS A TASK. The work is POSTED to the engine's run queue rather than
    /// started on the caller's thread, and the returned Task answers the <see cref="RunAnswer"/>. A run happens on the
    /// thread that OWNS the interpreter (every table here is thread-affine), so this twin cannot start it: the poster
    /// is that owner, and the queue is drained at a statement boundary, inside `sleep`, inside `Task.Wait` and by a
    /// message loop, which is the same door every script continuation takes. A CLI therefore blocks ONCE at its entry
    /// (<c>RunFileInAsync(...).Wait()</c>) and a host awaits the same Task.</summary>
    internal static Task RunFileInAsync(Interpreter itp, string fullPath, CancellationToken token = default)
        => Posted(itp, () => RunFileIn(itp, fullPath, token, resumable: true));

    /// <summary>See <see cref="RunFileInAsync"/>: the text twin of it.</summary>
    internal static Task RunTextInAsync(Interpreter itp, string source, string? sourceName = null, CancellationToken token = default)
        => Posted(itp, () => RunTextIn(itp, source, sourceName, token, resumable: true));

    /// <summary>A FILE run in an interpreter of its own, POSTED and waited for ONCE: this is the CLI's entry
    /// (`Program.RunFile`). Owning the interpreter is the runner's business here as it is for <see cref="RunFile"/>,
    /// and an interpreter HANDLE is a `ref struct` (it cannot travel into a continuation, measured as CS8175), so the
    /// wait and the release both happen here, on the thread that owns it.</summary>
    internal static RunAnswer RunFileAwaiting(string fullPath, string context = "RunFile")
    {
        using var handle = InterpreterManager.Create(caseSensitive: true, context: context);
        return Await(RunFileInAsync(handle.Instance, fullPath));
    }

    /// <summary>Blocks once on a POSTED run's task and answers its <see cref="RunAnswer"/>. `Task.Result` drives the run
    /// queue, which is how the posted work item gets to run at all: the queue is drained by `PumpPending` inside that
    /// wait, and by every statement boundary of a run already in flight.
    ///
    /// THE WAIT NAMES ITSELF in the parked-wait census (`run entry`), so the numbers a script reads through
    /// `Diagnostics.AsyncParks()` answer "which SCRIPT shape parked" rather than mixing in the engine's own one block
    /// per process. The `Result` read after it is free: the task is complete by then.</summary>
    internal static RunAnswer Await(Task task)
    {
        task.WaitBlocking("run entry", 0, 0);
        return task.Result is RunAnswer answer
            ? answer
            : RunAnswer.Of(new RunOutcome
            {
                Status = ExitCode.Failed,
                HasError = true,
                ErrorCode = "Script",
                Message = "the run answered nothing",
            });
    }

    /// <summary>A resumable run's own completion, waited and labelled the same way: the CLI blocks on the POSTED run's
    /// task and this posted work blocks on the run's, so a reader of the census sees `run entry` twice per CLI run and
    /// nothing else from the engine.</summary>
    private static ScriptResult? AwaitRun(Task run)
    {
        run.WaitBlocking("run entry", 0, 0);
        return run.Result is ScriptResult result ? result : null;
    }

    /// <summary>ONE statement of "a run as a Task", so the two twins above cannot disagree about the posting, the
    /// completion or the failure rule. The answer travels as the Task's RESULT, because a run that failed is still an
    /// answer (its verdict is in the <see cref="RunAnswer"/>); only a defect inside the runner itself would be a fault,
    /// and it is folded into the verdict the same way the synchronous door folds it.</summary>
    private static Task Posted(Interpreter itp, Func<RunAnswer> run)
    {
        var task = new Task();
        TaskPump.Post(() =>
        {
            RunAnswer answer;
            try { answer = run(); }
            catch (Exception ex) { answer = RunAnswer.Of(Failure(ex, itp)); }
            task.SetResult(answer);
        });
        return task;
    }

    /// <summary>Runs the EXECUTABLE'S OWN ENTRY POINT on an interpreter the caller owns, which is `slc_main`: the same
    /// argument dispatch `Main` performs, so a front end that wants to be a shortcut onto this engine inherits every
    /// arm (`--repl [script]`, `--compile`, `--dump-bytecode`, a `.slc`, a `.slcbc`, `--mcp`) with nothing to keep in
    /// step. An argument list with no arguments opens the interactive REPL exactly as the exe does, on THIS interpreter
    /// (<see cref="Program.RunReplOn"/>), so a session's thread, apartment and sinks are the ones it runs with.
    ///
    /// The verdict is the code the process would exit with, AND what the run said: a `.slc` arm hands back the outcome
    /// `Program.RunFile` recorded (its message, code, position and frames) together with the value it answered, so a
    /// host that owns no console still learns why a run failed. Answering BOTH here is what keeps the pair to one
    /// place: `slc_main` used to reassemble it from `Program.LastRunOutcome` + `Program.LastRunValue` itself, which
    /// duplicated the rule below and let a stale value ride on an arm that runs no file. Nothing throws: the dispatch
    /// carries its own catch.</summary>
    public static RunAnswer RunMainIn(Interpreter itp, string[] args)
    {
        try
        {
            var status = Program.RunMainIn(itp, args);
            // The recorded pair describes THIS call only (Program.RunMainIn clears both), and it is used only when the
            // outcome agrees with the code the dispatch answered: a REPL that ended cleanly after a failed `.run`, or a
            // `--compile` refusal, must not report some earlier file's error or its answer.
            return Program.LastRunOutcome is { } ran && ran.Status == status
                ? RunAnswer.Of(ran, Program.LastRunValue)
                : RunAnswer.Of(new RunOutcome { Status = status });
        }
        catch (Exception ex)
        {
            return RunAnswer.Of(new RunOutcome
            {
                Status = ExitCode.Refused,
                HasError = true,
                Message = FriendlyExceptionMessage(ex),
            });
        }
    }

    /// <summary>Runs a compiled bundle held in MEMORY, which is what a packaged executable does with the payload it
    /// carries in its own resource (`slc_run_bundle_bytes`). The rules are <see cref="RunBundleIn"/>'s, one input type
    /// over: a bundle carrying its program as source runs that source, a source-less one runs its compiled entry, and a
    /// load refusal comes back as `ExitCode.Refused` with the engine's own sentence. Answers both halves, which this
    /// door did NOT until the two bundle doors became one pipeline: a packaged payload's answer had no `Result` at all.
    ///
    /// <paramref name="directory"/> is the run's directory, and it matters: a packaged executable has no script PATH,
    /// so `./` has to mean "beside the exe" and not "wherever the process happens to stand". Null means the load has no
    /// home, and a `./` import then refuses rather than guessing.</summary>
    public static RunAnswer RunBundleBytesIn(Interpreter itp, byte[] data, string? directory, CancellationToken token = default)
        => RunBundleCore(
            itp,
            () => itp.LoadBundleFromBytes(data, directory, replayImports: false),
            // THE RUN'S DIRECTORY IS THE EXE'S FOLDER, and it has to be set for a source-less entry too: nothing here
            // interpreted a path, so `Script.Directory()` would read EMPTY and every `./` would refuse. The engine takes
            // the directory from a bundle PATH, so a packaged run names the file it would have been.
            runFile: directory is null ? null : Path.Combine(directory, "packaged.slcbc"),
            sourcePath: directory,
            token);

    /// <summary>Runs a compiled bundle (`.slcbc`) inside an interpreter the caller owns, with the SAME rules the CLI
    /// uses: a bundle that carries its program as source runs that source, and a source-less one runs its compiled
    /// entry. Answers both halves (see <see cref="RunAnswer"/>), so a door that reports a verdict reports the entry's
    /// answer with it, and a load refusal (a body from another version, a type this environment lacks) comes back as
    /// `ExitCode.Refused` with the engine's own sentence.</summary>
    public static RunAnswer RunBundleIn(Interpreter itp, string fullPath, CancellationToken token = default)
        => RunBundleCore(itp, () => itp.LoadBundleFromFile(fullPath, replayImports: false), runFile: fullPath, sourcePath: fullPath, token);

    /// <summary>THE ONE BUNDLE PIPELINE, for both inputs a host can hand this engine: a `.slcbc` on disk and the BYTES a
    /// packaged executable carries in its own resource. The two doors differ in exactly two things, and both are
    /// arguments: how the bundle is LOADED (<c>LoadBundleFromFile</c> / <c>LoadBundleFromBytes</c>) and what the run's
    /// DIRECTORY is (<c>runFile</c>, the path the load would have come from). Everything else is stated once here: the
    /// load refusal, the carried-source branch, the "no entry" refusal, the check ledger, and the entry invocation with
    /// its `exitcode` and its `takeVoidAnswer` value. Written this way because the two copies had drifted twice: the
    /// bytes door lost the answered value (`slc_run_bundle_bytes` reported no `Result` where `slc_run_bundle` did) and
    /// its no-entry sentence had already diverged from the file door's.</summary>
    private static RunAnswer RunBundleCore(
        Interpreter itp,
        Func<(ScriptFunction? Entry, int Count)> load,
        string? runFile,
        string? sourcePath,
        CancellationToken token)
    {
        ScriptFunction? entry;
        try { (entry, _) = load(); }
        catch (Exception ex)
        {
            return RunAnswer.Of(new RunOutcome
            {
                Status = ExitCode.Refused,
                HasError = true,
                ErrorCode = "Bytecode",
                Message = ex is ScriptException ? ex.Message : Interpreter.FriendlyExceptionMessage(ex),
            });
        }

        // The run's directory, so a source-less entry can resolve `./` at all (a bundle carrying its source gets it
        // from its own Interpret, which is handed this same path).
        if (runFile is not null) itp.SetRunDirectoryForBundle(runFile);

        if (!string.IsNullOrEmpty(itp.LoadedBundleSource))
            return RunSourceIn(itp, itp.LoadedBundleSource!.AsMemory(), sourcePath, token);

        if (entry == null)
        {
            return RunAnswer.Of(new RunOutcome
            {
                Status = ExitCode.Failed,
                HasError = true,
                ErrorCode = "Bytecode",
                Message = "this bundle declares no entry, so it holds functions only and there is nothing to run: "
                          + "load it and call a function, or compile a program with --compile (which carries its source)",
            });
        }

        // The ledger's baseline is taken BEFORE the run (not as an argument beside it), so the report cannot depend on
        // argument evaluation order in a path this delicate.
        var since = itp.SnapshotChecks();
        return WithChecks(itp, since, WithCancellation(token, () => InProcessState(() =>
        {
            try
            {
                // `takeVoidAnswer`: the entry is declared Void (a program body has no DECLARED result) yet a top-level
                // `return <value>` IS the script's answer, which the source run and the SQL door both report. Without
                // it the VM-direct exit normalises the answer to null and `select slc_file('x.slcbc')` loses it while
                // `select slc_file('x.slc')` keeps it.
                var value = itp.InvokeScriptFunction(entry, [], 0, 0, takeVoidAnswer: true);
                itp.InvokeExitHandlers();
                // THE SCRIPT'S OWN `exitcode` IS THE VERDICT, as it is for a source run: `Interpret` reports
                // `ExitCode = Environment.ExitCode`, and `InProcessState` started this run from 0 and puts the host's
                // value back on the way out, so reading it HERE is the script's answer (a later `atexit` handler may
                // still have set it, which is why this sits after InvokeExitHandlers). Measured on the maintainer's own
                // sqlite demo, whose tail is `exitcode = 150; return result;`: the file answered
                // `{"ExitCode":150,"ReturnValue":{...}}` while its `--source=strip` bundle answered `ExitCode: 0` with
                // the same document, so a caller reading `$.ExitCode` as "did it run" got two answers for one script.
                return RunAnswer.Of(new RunOutcome { Status = Environment.ExitCode }, value);
            }
            catch (Exception ex)
            {
                return RunAnswer.Of(Failure(ex, itp));
            }
        })));
    }

    /// <summary>THE SAME RULE for a door that runs the interpreter DIRECTLY: the SQL `slc_file` arm parses its own way
    /// (case-sensitively), so it never passes through <see cref="RunSourceIn"/> and would otherwise answer a run's
    /// echo instead of its report. `ReturnValue` becomes the checks report when the run reported any check, with the
    /// run's value inside it as `Answer` (a `return`) or `Echo` (the last expression statement); a run that reported
    /// none is returned untouched, so an ordinary `slc_file` answer does not change.</summary>
    internal static ScriptResult WithChecks(Interpreter itp, CheckSnapshot since, ScriptResult result)
    {
        if (itp.ChecksSince(since) is not { } report) return result;
        return result with { ReturnValue = ReportWith(itp, report, result.ReturnValue) };
    }

    /// <summary>A RUN OWNS ITS OWN VERDICT, AND `exitcode` IS PROCESS STATE. `Interpret` reports a clean run as
    /// `ExitCode = Environment.ExitCode` (the engine's own line), which is right for a process that runs ONE script and
    /// exits, and wrong for a host that runs several in one process: measured through the embedding ABI, a script that
    /// ended with `exit(7)` made every LATER run report 7 as its status, because nothing reset the ambient value. So a
    /// run starts from 0 and puts the host's value back on the way out, which is the same save-run-restore the engine's
    /// own temporary interpreters already do ("a temp interpreter's exit() must not leak to the host").
    ///
    /// `internal` because the SQLite door runs script code directly (its own parse, its own `slc_eval` invocations) and
    /// needed the same rule: measured, after one `slc_file` of a subject ending `exit(3)`, EVERY later `slc_file` in
    /// that connection answered `ExitCode: 3`, including a subject with no exit at all.</summary>
    internal static T InProcessState<T>(Func<T> body)
    {
        var ambient = Environment.ExitCode;
        Environment.ExitCode = 0;
        try { return body(); }
        finally { Environment.ExitCode = ambient; }
    }

    /// <summary>The pipeline every entry shares once the text is in hand: parse, the ONE precompile pass, interpret,
    /// and settle. Split out because four callers (a file, a text, a bundle's carried source, and the session variants
    /// of all three) must not each restate it. It answers BOTH halves, which is what a caller that reports the run's
    /// ANSWER needs: `slc_file` in SQLITE reports `ReturnValue`, so a compiled bundle reaching that door has to answer
    /// the same shape a source run does.</summary>
    private static RunAnswer RunSourceIn(Interpreter itp, ReadOnlyMemory<char> sourceMem, string? sourcePath, CancellationToken token, bool resumable = false)
    {
        var since = itp.SnapshotChecks();
        return WithChecks(itp, since, InProcessState(() => RunSourceCore(itp, sourceMem, sourcePath, token, resumable)));
    }

    /// <summary>ATTACHES the check family's report to a finished run, and lets it ANSWER for the run. Both halves exist
    /// for one measured shape: a suite's tail is `CheckSummary(); exit(CheckFailed());`, so the verdict said `Status 1`
    /// with nothing else in it, and the only place the failing check existed was the stdout the host may not own.
    ///
    /// ONE RULE, NO HEURISTIC: when the run reported any check, its answer IS the report, and the value the run ended
    /// with rides INSIDE that document. WHICH MEMBER carries it depends on where the value came from, because the two
    /// are different facts: a top-level `return <value>` is what the script SAID (`Answer`), while the value of a bare
    /// expression statement is the echo the REPL prints (`Echo`), and a suite's tail `CheckSummary(); exit(...)` echoes
    /// that call's `false`, which read like a verdict next to a report. Substituting only when the script's value is
    /// null was the alternative, and it fails on exactly that shape, while also leaving the SQL door with the echo,
    /// since `ReturnValue` is all it can carry. <see cref="RunOutcome.ResultFromChecks"/> says the answer came from
    /// here, so a host that wants only what the script returned reads `Answer`.</summary>
    private static RunAnswer WithChecks(
        Interpreter itp, CheckSnapshot since, RunAnswer run)
    {
        if (itp.ChecksSince(since) is not { } report) return run;

        var outcome = run.Outcome with { Checks = report, ResultFromChecks = true };
        return RunAnswer.Of(outcome, ReportWith(itp, report, run.Value));
    }

    /// <summary>The report plus the run's value, named for its ORIGIN (<see cref="Interpreter.LastValueWasReturn"/>):
    /// `Answer` for a `return`, `Echo` for the last expression statement, and neither when the run ended with no value
    /// at all.</summary>
    internal static Dictionary<string, object?> ReportWith(Interpreter itp, CheckReport report, object? value)
    {
        var answer = report.ToDocument();
        if (value is null) return answer;
        answer[itp.LastValueWasReturn ? "Answer" : "Echo"] = value;
        return answer;
    }

    private static RunAnswer RunSourceCore(Interpreter itp, ReadOnlyMemory<char> sourceMem, string? sourcePath, CancellationToken token, bool resumable = false)
    {
        List<Stmt> statements;
        try
        {
            var lexer = new Lexer(sourceMem);
            var tokens = lexer.GetTokens();
            var parser = new Parser(tokens, itp.Delegates, source: sourceMem.ToString());
            statements = parser.GetStatements().ToList();
        }
        catch (Exception ex)
        {
            // A parse refusal is the author's, not the engine's, and it reaches a host as one line with a position
            // (never a stack trace, never a raw CLR sentence for a script's mistake).
            return RunAnswer.Of(Failure(ex, itp));
        }

        return WithCancellation(token, () =>
        {
            try
            {
                Program.PrecompileFunctions(statements, itp);
                // THE TWO ENTRIES DIFFER IN THIS ONE CALL: the resumable entry suspends a file-level `await` and is
                // resumed from the run queue (see Interpreter.InterpretAsync), while the synchronous one keeps the C#
                // entry-point rule (the top level waits and drives the pump). Every verdict path below is shared, which
                // is what keeps the two from reporting the same run two ways. The resumable half is GATED, because it
                // cannot serve a second await in one statement yet (see Interpreter.AsyncRunEnabled).
                var result = resumable && Interpreter.AsyncRunEnabled
                    ? AwaitRun(itp.InterpretAsync(statements, sourcePath))
                      ?? new ScriptResult { HasError = true, ErrorMessage = "the run answered nothing" }
                    : itp.Interpret(statements, sourcePath);
                if (result.HasError)
                {
                    if (result.Cancelled)
                    {
                        return RunAnswer.Of(new RunOutcome
                        {
                            Status = ExitCode.Failed,
                            HasError = true,
                            Cancelled = true,
                            ErrorCode = nameof(ErrorCode.Cancelled),
                            Message = result.ErrorMessage ?? "Stopped: the run was cancelled",
                            Line = result.ErrorLine,
                            Column = result.ErrorColumn,
                        });
                    }

                    // The position and the message both come from the RESULT, which is where `Interpret` folds a script
                    // failure (it does not throw one), and the code is the error's own; a result carrying only a message
                    // is reported under `Script`, which is the label the CLI has always used for that case.
                    return RunAnswer.Of(result.ErrorInfo is { } info
                        ? new RunOutcome
                        {
                            Status = ExitCode.Failed,
                            HasError = true,
                            ErrorCode = info.Code.ToString(),
                            Message = info.Message,
                            Line = result.ErrorLine,
                            Column = result.ErrorColumn,
                            Frames = info.Frames,
                        }
                        : new RunOutcome
                        {
                            Status = ExitCode.Failed,
                            HasError = true,
                            ErrorCode = "Script",
                            Message = result.ErrorMessage ?? "Script error",
                            Line = result.ErrorLine,
                            Column = result.ErrorColumn,
                        });
                }

                // A run that ENDS (cleanly, or with a non-zero `exitcode` the script set) still owes its `atexit`
                // handlers, which is what the CLI has always done here. A FAILED run does not run them, which is the
                // other half of that measured behaviour and is why this sits after the error return.
                itp.InvokeExitHandlers();
                return RunAnswer.Of(new RunOutcome { Status = result.ExitCode }, result.ReturnValue);
            }
            catch (Exception ex)
            {
                return RunAnswer.Of(Failure(ex, itp));
            }
        });
    }

    /// <summary>The ONE exception to a verdict, shared by every arm: `exit(n)` ends a run deliberately and its code is
    /// the verdict (teardown still runs), a cancel is reported as a cancel rather than as the author's mistake, a
    /// script refusal keeps its own message and position, and everything else is the engine's unexpected failure.</summary>
    internal static RunOutcome Failure(Exception ex, Interpreter itp)
    {
        if (ex is ScriptException sx && Interpreter.AbortExitCode(sx) is int abortCode)
        {
            // `exit(n)`: the run ends deliberately, so its teardown runs and its CODE is the verdict. The engine has
            // already put that code in `Environment.ExitCode`; it also travels here, because a host embedding the
            // engine must be able to read the verdict without reading process state.
            itp.InvokeExitHandlers();
            return new RunOutcome { Status = abortCode };
        }

        if (ex is ScriptException cancelled && cancelled.ErrorCode == ErrorCode.Cancelled)
        {
            return new RunOutcome
            {
                Status = ExitCode.Failed,
                HasError = true,
                Cancelled = true,
                ErrorCode = nameof(ErrorCode.Cancelled),
                Message = cancelled.Message,
                Line = cancelled.Line,
                Column = cancelled.Column,
            };
        }

        if (ex is ScriptException script)
        {
            // A Ctrl+C reports NOTHING (the CLI prints its own line about it) and still exits 1, which is what this
            // arm has always produced.
            if (script.ErrorCode == ErrorCode.Interrupt) return new RunOutcome { Status = ExitCode.Failed };

            // A raise that names a line keeps it; a registration's own ScriptException carries none, and is
            // attributed to the statement the interpreter is on, which is the one report site every registration
            // reaches rather than each of them inventing a position.
            var line = script.Line > 0 ? script.Line : itp.CurrentLine;
            var column = script.Line > 0 ? script.Column : itp.CurrentColumn;
            return new RunOutcome
            {
                Status = ExitCode.Failed,
                HasError = true,
                ErrorCode = script.ErrorCode.ToString(),
                Message = script.Message,
                Line = line,
                Column = column,
                Frames = script.Value is ScriptErrorInfo sei ? sei.Frames : null,
            };
        }

        // The engine's own unexpected failure. The CLI prints this as `Fatal: <message>` and dumps the VM's crash
        // ring; both stay the reporter's business, so this arm only says that it happened and what was said.
        return new RunOutcome
        {
            Status = ExitCode.Failed,
            Fatal = true,
            Message = ex is ScriptException s2 ? s2.Message : Interpreter.FriendlyExceptionMessage(ex),
        };
    }

    /// <summary>Installs a run's cancellation token for the duration of the run, the way `RunScript` and
    /// `Task.Run(fn, token)` do, so `slc_cancel` from another thread stops the run at its next statement, loop
    /// back-edge or `sleep` chunk instead of letting it finish first. `[ThreadStatic]`, so a nested run inherits it and
    /// another thread's run is untouched.</summary>
    private static T WithCancellation<T>(CancellationToken token, Func<T> body)
    {
        if (!token.CanBeCanceled) return body();
        var previous = Interpreter.ActiveRunCancellation;
        Interpreter.ActiveRunCancellation = token;
        try { return body(); }
        finally { Interpreter.ActiveRunCancellation = previous; }
    }
}

