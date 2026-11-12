namespace ScriptLangCore;

/// <summary>THE C ABI: what a native host calls after loading this engine as a DLL. Twenty-two exports, one revision,
/// and five rules that decide everything else. `docs/embed/abi.json` is the same set written down for a binding
/// author, and `tools/check-abi-manifest.sh` fails when this file and that manifest disagree.
///
/// THE RULES
///   1. NOTHING THROWS ACROSS THE BOUNDARY. Every export wraps its body and turns a failure into a status plus a
///      message readable through `slc_last_error`; a host never sees a CLR exception, a stack trace, or a resource
///      key (the engine's ONE message map composes the text, which is the rule this repository applies everywhere).
///   2. STRINGS ARE UTF-8, NUL-TERMINATED, AND NEVER POINT AT ENGINE MEMORY. An input is the caller's; an output
///      goes through a CALLER-OWNED buffer with the `snprintf` convention: the call returns the length it NEEDS
///      (excluding the terminator), writes at most `capacity - 1` bytes plus the terminator, and answers the same
///      length when `capacity` is 0, so a host can size its buffer with one probe. That is how a value crosses two
///      heaps without an ownership rule either side can get wrong.
///   3. STATUS VALUES ARE THE ENGINE'S EXIT CODES (`ExitCode`: 0 ok, 1 failed, 2 usage, 3 refused), so a host
///      reads the same numbers the CLI exits with, and `slc_free` returning 3 means "still busy, retry" rather
///      than "broken". A run that ended with the script's own `exit(n)` answers n.
///   4. ONE CALL AT A TIME PER HANDLE. A run is marshalled to the thread that owns the interpreter and the host waits
///      for it, so a second verb arriving meanwhile is REFUSED by name rather than queued or interleaved.
///      `slc_cancel` is the one exception: a token is a signal from any thread, and a cancel that queued behind the run
///      it cancels would never arrive. A callback that calls back into its own session is refused too, because that
///      would be the same thread waiting on itself.
///   5. THE HOST'S CONSOLE IS NEVER TOUCHED. A run's `print` and the engine's own log lines go to the session's sink,
///      which invokes <see cref="SlcSetOutput"/>'s callback on the run's thread; with no callback they are dropped.
///
/// HANDLES are opaque positive int64 that are never reused (a counter, not a slot), so a stale handle answers
/// "no session with that handle" instead of hitting a new session. Handle 0 is the calling THREAD's own error, which
/// is what a host reads after `slc_create` failed (there is no handle yet to read it from).
///
/// WHAT A HOST DOES, in the order a session needs: `slc_create` (options JSON), `slc_set_output` (a line callback),
/// `slc_run_file` / `slc_run_text` / `slc_run_bundle`, then `slc_call` for the functions those runs left behind,
/// `slc_last_result` after any of them, and `slc_free` at the end. A returned handle of 0 means the session did not
/// start, and `slc_last_error(0, ...)` says why.</summary>
internal static unsafe class EmbedApi
{
    /// <summary>The ABI revision. A host that sees a number it does not know should refuse to call rather than hope:
    /// the exports above are the contract, and a later one may add a parameter or change a meaning. Revision 1 was
    /// create/free/version/thread mode/stats/error; revision 2 added the verbs and the output callback; revision 3
    /// names every member of the verdict JSON in PascalCase (`Status`, `Ok`, `Result`, `Checks`) instead of the
    /// camelCase it started with, and added `Checks`/`ResultFrom`, so a host that parses the verdict must read the new
    /// spelling; revision 4 added adoption and the asynchronous verbs; revision 5 added `slc_abi_revision` and
    /// `slc_capabilities`, so a binding can ask the two questions it actually has (`which contract`, `what does this
    /// build answer`) without parsing a sentence. It travels in `slc_version`'s text (so one call tells a host
    /// everything a log needs), in `slc_abi_revision` (the number alone), and in `slc_stats`' JSON.</summary>
    internal const int AbiRevision = 5;

    private static readonly ConcurrentDictionary<long, EmbedSession> Sessions = new();

    /// <summary>The failure of the LAST `slc_create` on this thread, since there is no handle to read it from. Per
    /// thread, because two hosts may create sessions concurrently and one host's error must never look like the
    /// other's.</summary>
    [ThreadStatic] private static string? _createError;

    /// <summary>Set while a host callback is running, so a verb called from INSIDE one is refused: the session's own
    /// thread is occupied by the callback, and a run posted from there would wait on itself for ever.</summary>
    [ThreadStatic] private static bool _inHostCallback;

    /// <summary>`slc_version(buffer, capacity) -> needed length`: the engine's version, its runtime and the ABI
    /// revision. A host checks the revision before anything else, which is why this is the one call that needs no
    /// handle.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_version")]
    public static int SlcVersion(byte* buffer, int capacity)
    {
        try
        {
            return CopyOut(VersionText(), buffer, capacity);
        }
        catch (Exception ex)
        {
            return CopyOut(FriendlyExceptionMessage(ex), buffer, capacity);
        }
    }

    /// <summary>`slc_abi_revision() -> int`: THE NUMBER ALONE, for a binding that wants to compare a contract rather
    /// than parse a sentence. `slc_version` already carries it, so this adds no fact; it adds a QUESTION, which is what
    /// a binding needs (`if (slc_abi_revision() &lt; 5) drop the async verbs`). Like `slc_version` it takes no handle,
    /// because it is the one call a host makes before it has a session to ask.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_abi_revision")]
    public static int SlcAbiRevision() => AbiRevision;

    /// <summary>`slc_capabilities() -> int`: the bitmask of what THIS build answers, from
    /// <see cref="EmbedCapabilities"/> (the one place the bits live). A binding reads it once and drops what an older
    /// library lacks instead of calling `GetProcAddress` and treating a null as a crash in the user's program. It
    /// answers the same for the whole image rather than per session, because a capability is a property of the build:
    /// the one build-dependent bit is the COM server, which only the `Embed` configuration compiles.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_capabilities")]
    public static int SlcCapabilities() => EmbedCapabilities.Bits();

    /// <summary>`slc_create(optionsJson) -> handle`, or 0 with the reason in `slc_last_error(0, ...)`.
    /// <paramref name="optionsJson"/> may be NULL (every default), and its keys are documented on
    /// <see cref="EmbedOptions"/>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_create")]
    public static long SlcCreate(byte* optionsJson)
    {
        try
        {
            var options = EmbedOptions.Parse(ReadUtf8(optionsJson), out var parseError);
            if (options == null)
            {
                _createError = parseError;
                return 0;
            }

            var session = EmbedSession.Create(options, out var createError);
            if (session == null)
            {
                _createError = createError;
                return 0;
            }

            Sessions[session.Id] = session;
            _createError = null;
            return session.Id;
        }
        catch (Exception ex)
        {
            _createError = "creating a session failed: " + FriendlyExceptionMessage(ex);
            return 0;
        }
    }

    /// <summary>`slc_free(handle) -> status`: 0 when the session is really gone, 2 for a handle that was never
    /// handed out (or was already freed), and 3 when a run is still inside a native call that cannot be interrupted,
    /// in which case the handle STAYS VALID and freeing it can be retried. Releasing a session runs the engine's own
    /// teardown on the thread that owns the interpreter: a script's forms are closed and its `Events` feeds ended,
    /// which is the same `Interpreter.Dispose` the CLI reaches at the end of a run.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_free")]
    public static int SlcFree(long handle)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return ExitCode.Usage;
            if (session.ImportedFromAnotherThread())
            {
                session.LastError = session.ThreadRefusal();
                return ExitCode.Usage;
            }
            if (!session.TryDispose(out var reason))
            {
                session.LastError = reason;
                return ExitCode.Refused;
            }
            Sessions.TryRemove(handle, out _);
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Remember(handle, "freeing the session failed: " + FriendlyExceptionMessage(ex));
            return ExitCode.Failed;
        }
    }

    /// <summary>`slc_thread_mode(handle) -> 0 own thread, 1 host thread, -1 no such handle`. A host reads this
    /// because the two modes have different budgets: a session on the engine's own thread has 32 MB, one pinned to
    /// the host's thread has whatever the host gave it (at least 16 MB, checked at create).</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_thread_mode")]
    public static int SlcThreadMode(long handle)
        => Sessions.TryGetValue(handle, out var session) ? session.Mode : -1;

    /// <summary>`slc_last_error(handle, buffer, capacity) -> needed length`. Handle 0 answers the calling thread's
    /// last `slc_create` failure. An empty answer (length 0) means the last call on that handle succeeded.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_last_error")]
    public static int SlcLastError(long handle, byte* buffer, int capacity)
    {
        try
        {
            if (handle == 0) return CopyOut(_createError, buffer, capacity);
            return CopyOut(Sessions.TryGetValue(handle, out var session) ? session.LastError : NoSuchSession(handle), buffer, capacity);
        }
        catch (Exception ex)
        {
            return CopyOut(FriendlyExceptionMessage(ex), buffer, capacity);
        }
    }

    /// <summary>`slc_last_result(handle, buffer, capacity) -> needed length`: the last run's or call's verdict as JSON
    /// (`status`, `ok`, `error`, `fatal`, `cancelled`, `code`, `message`, `line`, `column`, `frames`, and `result` for
    /// a call). One door for everything a verb answered, so a host does not have to know which export wrote which
    /// field; `slc_last_error` stays the short sentence for a log.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_last_result")]
    public static int SlcLastResult(long handle, byte* buffer, int capacity)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return CopyOut(NoSuchSession(handle), buffer, capacity);
            return CopyOut(session.LastResult, buffer, capacity);
        }
        catch (Exception ex)
        {
            return CopyOut(FriendlyExceptionMessage(ex), buffer, capacity);
        }
    }

    /// <summary>`slc_stats(handle, buffer, capacity) -> needed length`: a small JSON snapshot of the session, for a
    /// host's own log rather than for control flow. It carries the numbers a support question needs: which thread
    /// mode and apartment the session got, the stack budget the depth guard will measure against, and how many global
    /// names the interpreter's namespaces registered (a session that failed to build would show 0).</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_stats")]
    public static int SlcStats(long handle, byte* buffer, int capacity)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return CopyOut(NoSuchSession(handle), buffer, capacity);
            var json = "{\"Abi\":" + AbiRevision
                       + ",\"Capabilities\":" + EmbedCapabilities.Bits()
                       + ",\"Version\":\"" + (Program.Version ?? "N/A") + "\""
                       + ",\"Runtime\":\"" + RuntimeName() + "\""
                       + ",\"Id\":" + session.Id
                       + ",\"Thread\":\"" + (session.Mode == 1 ? "host" : "own") + "\""
                       + ",\"Apartment\":\"" + session.Apartment + "\""
                       + ",\"StackMB\":" + (session.StackBytes / (1024 * 1024))
                       + ",\"Globals\":" + session.Globals
                       + "}";
            return CopyOut(json, buffer, capacity);
        }
        catch (Exception ex)
        {
            return CopyOut(FriendlyExceptionMessage(ex), buffer, capacity);
        }
    }

    /// <summary>`slc_set_output(handle, callback, user) -> status`. The callback is
    /// `void (*)(int64_t handle, int32_t channel, const char* utf8, void* user)`, with channel 0 for `print` and 1 for
    /// the engine's own log lines; the string is NUL-terminated UTF-8 valid ONLY for the duration of the call, and it
    /// is invoked on the thread that runs the script. NULL removes the callback, and with none registered the output is
    /// dropped: a host that wants silence gets silence rather than its own console rewritten.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_set_output")]
    public static int SlcSetOutput(long handle, nint callback, nint user)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return ExitCode.Usage;
            session.LineSink = callback == 0
                ? null
                : (channel, line) => EmitToHost(handle, callback, user, channel, line);
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Remember(handle, "installing the output callback failed: " + FriendlyExceptionMessage(ex));
            return ExitCode.Failed;
        }
    }

    /// <summary>`slc_run_file(handle, pathUtf8) -> status`: runs a `.slc` file in the SESSION's interpreter, so what it
    /// declares stays callable afterwards. A relative path resolves against the HOST's current directory, which is the
    /// rule every other file the host passes to any library follows; `./x` keeps the engine's own meaning (beside the
    /// file being run). The verdict is on `slc_last_result`, the sentence on `slc_last_error`.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_run_file")]
    public static int SlcRunFile(long handle, byte* pathUtf8)
    {
        var path = ReadUtf8(pathUtf8);
        return Run(handle, "file", (session, itp, token) => RunFileBody(itp, path, token));
    }

    /// <summary>THE FILE VERB'S BODY, one implementation with two doors: the `slc_run_file` export and the COM server's
    /// `RunFile` member (`src/Host/ComServer.cs`). Everything it decides (the path preamble, the bundle refusal, the
    /// answer travelling in the verdict) therefore cannot differ between a C host and Excel.</summary>
    internal static (RunOutcome Outcome, string? ValueJson) RunFileBody(Interpreter itp, string? path, CancellationToken token)
    {
        if (!TryRunPath(path, "slc_run_file", out var full, out var refusal)) return refusal;
        // A BUNDLE IS NOT SOURCE, and this used to answer a control-character ParseError naming a byte offset in a
        // binary file: `slc_run_file` on a `.slcbc` read its header as text and refused `Unexpected character`
        // (the second byte of the magic) at "line 1, column 5", which names neither the mistake nor the verb that
        // works. Reported from the shipped launcher, whose usage line did not say the two verbs existed either.
        if (IsCompiledBundle(full))
            return Refusal($"'{full}' is a compiled bundle, not source text: run it with slc_run_bundle");
        var answer = ScriptRunner.RunFileIn(itp, full, token);
        // THE RUN'S ANSWER TRAVELS TOO (`Result` in the verdict), because a run that reported checks and left no
        // answer of its own ANSWERS with the checks document (`ScriptRunner.WithChecks`), and that is the whole
        // point of the ledger: a host reads what failed instead of a status and nothing else. `ValueJson` is the
        // ONE place an answer becomes the verdict's `Result`.
        return (answer.Outcome, answer.ValueJson);
    }

    /// <summary>Is this file a compiled bundle? Answered from the first four bytes, which is the ONE fact that decides
    /// it (the wire writes `ProgramMagic` or `LibraryMagic` first, so an extension cannot lie and no suffix test is
    /// needed). A short or unreadable file answers false and the caller's own refusal stands.
    /// <para>
    /// `internal` because the SQLITE door asks the same question: `slc_file` used to hand a `.slcbc` to the LEXER, which
    /// answered `Unexpected character '\u001E'` because the bundle's header is binary. One test, so the two doors cannot
    /// disagree about what a bundle is.
    /// </para></summary>
    internal static bool IsCompiledBundle(string fullPath)
    {
        Span<byte> head = stackalloc byte[4];
        int read;
        try
        {
            using var stream = File.OpenRead(fullPath);
            read = stream.Read(head);
        }
        catch (Exception) { return false; }
        if (read < 4) return false;
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head);
        return magic == VmBytecodeCodec.ProgramMagic || magic == VmBytecodeCodec.LibraryMagic;
    }

    /// <summary>`slc_run_text(handle, sourceUtf8, nameUtf8) -> status`: runs SOURCE TEXT in the session's interpreter.
    /// <paramref name="nameUtf8"/> is optional (`null` means "no name"): when it is a path it becomes the run's own
    /// path, which is what makes `Script.Directory()` and `./` mean something for a snippet a host says came from
    /// somewhere. No dot-command pass, because that rule belongs to a file session.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_run_text")]
    public static int SlcRunText(long handle, byte* sourceUtf8, byte* nameUtf8)
    {
        var source = ReadUtf8(sourceUtf8);
        var name = ReadUtf8(nameUtf8);
        return Run(handle, "text", (session, itp, token) => RunTextBody(itp, source, name, token));
    }

    /// <summary>THE TEXT VERB'S BODY, shared with the COM server's `RunText` member, so a snippet behaves the same
    /// whether a C host or VBA sent it.</summary>
    internal static (RunOutcome Outcome, string? ValueJson) RunTextBody(Interpreter itp, string? source, string? name, CancellationToken token)
    {
        if (source is null) return Refusal("slc_run_text needs source text");
        // A NAME IS OPTIONAL and need not exist (a snippet may name where it came from), so it goes through the same
        // normalisation the file verb uses, with existence not required and its own wording for a bad path.
        if (!TryRunPath(name, "slc_run_text", out var path, out var refusal, mustExist: false, optional: true)) return refusal;
        var answer = ScriptRunner.RunTextIn(itp, source, path.Length == 0 ? null : path, token);
        return (answer.Outcome, answer.ValueJson);
    }

    /// <summary>`slc_adopt(handle, dispatch) -> status`: THE HOST HANDS ITS OWN OBJECT OVER. 0 when it is registered,
    /// 2 when the pointer is missing or the table refused it.
    /// <para>
    /// WHY IT EXISTS: a host that loads this engine INSIDE the application it wants to drive cannot reach that
    /// application through COM. Measured from real Excel: `Com.GetObject("Excel.Application")` refuses (the ROT holds no
    /// entry for the instance that loaded us), `Com.GetOrCreate` refuses with it, and `Com.CreateObject` answers a
    /// DIFFERENT instance (a different window handle). The host does hold the pointer, so it hands it over here and the
    /// script reaches it with `Com.Host()`.
    /// </para>
    /// <para>
    /// CALLED ON THE HOST'S THREAD, and that is not a limitation to work around: the pointer is valid in the caller's
    /// apartment, so registering it there is the only correct moment. The engine makes it usable on its own thread
    /// through the Global Interface Table (a lab measurement: the same `Application` read its `Hwnd` from another
    /// apartment, and the value matched the host's own), and the registration is released by `slc_free`.
    /// </para>
    /// <para>
    /// A SCRIPT THAT USES IT MUST BE RUN ASYNCHRONOUSLY (`slc_run_text_async`): an STA host cannot answer a COM call
    /// while it is inside the synchronous verb, so `Com.Host()` would stall until the run it belongs to is over.
    /// </para></summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_adopt")]
    public static int SlcAdopt(long handle, IntPtr dispatch)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return ExitCode.Usage;
            if (!session.AdoptHost(dispatch, out var error))
            {
                Remember(handle, error ?? "the host object could not be adopted");
                return ExitCode.Usage;
            }
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Remember(handle, "slc_adopt failed: " + FriendlyExceptionMessage(ex));
            return ExitCode.Failed;
        }
    }

    /// <summary>`slc_poll(handle) -> 1 running, 0 idle, -1 unknown handle`: what a host asks after starting an
    /// asynchronous run. The verdict of the run that just finished is in `slc_last_result`, as it is for a synchronous
    /// verb, because the same completion records it.
    /// <para>
    /// IT READS THE RUN'S TASK, so a CANCELLED run reads 0 the moment `slc_cancel` lands rather than when the body has
    /// finished the call it was inside: a run blocked in a top-level `await` would otherwise report "running" for as
    /// long as the awaited call takes (measured: 2,639 ms for a cancel issued at 372 ms). A new verb is still refused
    /// while the body is finishing, which is the session's own rule and not this verb's answer.
    /// </para></summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_poll")]
    public static int SlcPoll(long handle)
    {
        if (!Sessions.TryGetValue(handle, out var session)) return -1;
        return session.IsBusy || session.RunActive ? 1 : 0;
    }

    /// <summary>`slc_run_text_async(handle, sourceUtf8, nameUtf8) -> status`: the same run as `slc_run_text`, STARTED and
    /// not waited for (0 accepted, 2 refused). The answer is `slc_poll` until 0, then `slc_last_result`.
    /// <para>
    /// THE REASON THIS VERB EXISTS, measured: while an Office host is inside a synchronous verb its STA cannot answer a
    /// COM call, so a script that drives the host through `Com.Host()` would stall until the run ended, which is the
    /// same run. Returning at once lets the host pump (`DoEvents` in VBA) and the script's calls then arrive.
    /// </para></summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_run_text_async")]
    public static int SlcRunTextAsync(long handle, byte* sourceUtf8, byte* nameUtf8)
    {
        var source = ReadUtf8(sourceUtf8);
        var name = ReadUtf8(nameUtf8);
        return RunAsync(handle, "text", (itp, token) =>
        {
            if (source is null) return Refusal("slc_run_text_async needs source text");
            if (!TryRunPath(name, "slc_run_text_async", out var path, out var refusal, mustExist: false, optional: true)) return refusal;
            var answer = ScriptRunner.RunTextIn(itp, source, path.Length == 0 ? null : path, token);
            return (answer.Outcome, answer.ValueJson);
        });
    }

    /// <summary>`slc_run_file_async(handle, pathUtf8) -> status`: `slc_run_file`, started and not waited for. Same rules,
    /// same refusal for a compiled bundle (it is not source text).</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_run_file_async")]
    public static int SlcRunFileAsync(long handle, byte* pathUtf8)
    {
        var path = ReadUtf8(pathUtf8);
        return RunAsync(handle, "file", (itp, token) =>
        {
            if (!TryRunPath(path, "slc_run_file_async", out var full, out var refusal)) return refusal;
            if (IsCompiledBundle(full))
            {
                return Refusal($"'{full}' is a compiled bundle, not source text: run it with slc_run_bundle");
            }
            var answer = ScriptRunner.RunFileIn(itp, full, token);
            return (answer.Outcome, answer.ValueJson);
        });
    }

    /// <summary>`slc_run_bundle_bytes(handle, dataUtf8Bytes, count) -> status`: runs a compiled bundle held in MEMORY,
    /// which is what a PACKAGED executable needs (`ScriptLangCore.Launcher.exe` can carry a `.slcbc` in its own
    /// resource, so there is no file to name). The rules are `slc_run_bundle`'s, one input type over: a bundle carrying
    /// its program as source runs that source, a source-less one runs its compiled entry, and a load refusal comes back
    /// as `ExitCode.Refused` with the engine's sentence. The run's directory is the session's `home`, so a packaged
    /// script's `./` means "beside the exe" rather than "wherever the process happens to stand".</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_run_bundle_bytes")]
    public static int SlcRunBundleBytes(long handle, byte* data, int count)
    {
        return Run(handle, "bundle-bytes", (session, itp, token) =>
        {
            if (data == null || count <= 0) return Refusal("slc_run_bundle_bytes needs the bundle's bytes");
            var bytes = new byte[count];
            Marshal.Copy((nint)data, bytes, 0, count);
            var answer = ScriptRunner.RunBundleBytesIn(itp, bytes, session.Facts.Home, token);
            return (answer.Outcome, answer.ValueJson);
        });
    }
    /// <summary>`slc_main_owns(argumentUtf8) -> 0/1`: does the engine's own argument dispatch act on this FIRST
    /// argument? It is the predicate a PACKAGED executable needs to tell its own arguments from the CLI's, and it lives
    /// here rather than in the host so the spellings have ONE statement (`Program.EngineArguments`, the same list the
    /// dispatch's arms are written from). Case-sensitive, like the dispatch; a null or empty argument answers 0.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_main_owns")]
    public static int SlcMainOwns(byte* argumentUtf8)
        => Program.OwnsArgument(ReadUtf8(argumentUtf8)) ? 1 : 0;
    /// <summary>`slc_main(handle, argvJsonUtf8) -> status`: runs THE EXECUTABLE'S OWN ENTRY POINT, i.e. the dispatch
    /// `ScriptLangCore.exe` performs, with one JSON array of strings standing in for its command line. This is the verb
    /// a thin front end wants, because it inherits EVERY arm the exe has and cannot drift from it: no arguments (or
    /// `null`) opens the interactive REPL with the same prompt, dot commands, history and Ctrl+C behaviour,
    /// `--repl [script]` opens it and runs a file, `--compile` and `--dump-bytecode` do their work, a `.slc` runs, a
    /// `.slcbc` is routed to its bundle path, and `--mcp` starts the server.
    ///
    /// WHAT IS THE SESSION'S AND WHAT IS THE EXE'S, because a host should know: this call runs on the session's THREAD
    /// (so the apartment and the stack budget are the session's), and a REPL runs on the session's INTERPRETER, adopted
    /// rather than replaced, so the host keeps its handle and `.reset hard` is refused with a line saying why. A FILE
    /// runs the way the CLI runs one, in an interpreter of its own, which is exactly what makes this the exe rather
    /// than a fourth way to reach a run: for a run in the SESSION's interpreter use
    /// `slc_run_file`/`slc_run_text`/`slc_run_bundle`.
    ///
    /// The answer is the code the process would exit with, and nothing throws across the boundary.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_main")]
    public static int SlcMain(long handle, byte* argvJsonUtf8)
    {
        var argvJson = ReadUtf8(argvJsonUtf8);
        return Run(handle, "main", (session, itp, token) =>
        {
            string[] argv;
            try { argv = ParseArgv(argvJson); }
            catch (Exception ex)
            {
                return Refusal("the arguments must be a JSON array of strings: " + FriendlyExceptionMessage(ex));
            }
            // THE DISPATCH ANSWERS THE PAIR ITSELF (`ScriptRunner.RunMainIn`), so this arm FORWARDS what it decided
            // rather than reassembling it from the Program statics: that reassembly was the twin of the rule inside the
            // runner, and it let a stale value ride on an arm that runs no file.
            var answer = ScriptRunner.RunMainIn(itp, argv);
            return (answer.Outcome, answer.ValueJson);
        });
    }

    /// <summary>The argument list of <see cref="SlcMain"/>: a JSON array of strings, and `null` (or an empty string)
    /// means NO arguments, which is the REPL. Each element must be a string rather than being coerced, because a
    /// command line is text: a number in it would be a caller's mistake, not a shortcut.</summary>
    private static string[] ParseArgv(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var values = ParseArgsArray(json);
        var argv = new string[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            argv[i] = values[i] as string
                      ?? throw new ScriptException($"argument {i + 1} of the command line must be a string, not '{values[i]?.GetType().Name ?? "null"}'");
        }
        return argv;
    }

    /// <summary>`slc_run_bundle(handle, pathUtf8) -> status`: runs a `.slcbc` in the session's interpreter, with the
    /// CLI's own rules (a bundle carrying its program as source runs that source; a source-less one runs its compiled
    /// entry) and a load refusal reported as `ExitCode.Refused` with the engine's sentence.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_run_bundle")]
    public static int SlcRunBundle(long handle, byte* pathUtf8)
    {
        var path = ReadUtf8(pathUtf8);
        return Run(handle, "bundle", (session, itp, token) =>
        {
            if (!TryRunPath(path, "slc_run_bundle", out var full, out var refusal)) return refusal;
            var answer = ScriptRunner.RunBundleIn(itp, full, token);
            return (answer.Outcome, answer.ValueJson);
        });
    }

    /// <summary>THE PREAMBLE EVERY RUN VERB SHARES, because it was copied into `slc_run_file` and `slc_run_bundle`
    /// verbatim (and into `slc_run_text` with a different message), which is how one of them came to answer "that name is
    /// not a usable path" for a mistake its sibling called "that path is not usable".
    ///
    /// Answers false with the refusal to return. <paramref name="mustExist"/> is the file verbs' rule; a NAME (the text
    /// verb's optional label, which may point at nothing) passes false and <paramref name="optional"/>, which turns the
    /// blank case into "no path" rather than a refusal and leaves <paramref name="full"/> empty.</summary>
    private static bool TryRunPath(
        string? text, string verb, out string full, out (RunOutcome Outcome, string? ValueJson) refusal,
        bool mustExist = true, bool optional = false)
    {
        refusal = default;
        full = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            if (optional) return true;
            refusal = Refusal(verb + " needs a path");
            return false;
        }
        try { full = Path.GetFullPath(text); }
        catch (Exception ex)
        {
            refusal = Refusal((mustExist ? "that path is not usable: " : "that name is not a usable path: ")
                              + FriendlyExceptionMessage(ex));
            return false;
        }
        if (mustExist && !File.Exists(full))
        {
            refusal = Refusal("file not found: " + full);
            return false;
        }
        return true;
    }

    /// <summary>`slc_call(handle, nameUtf8, argsJsonUtf8) -> status`: calls a script FUNCTION the session already has,
    /// with the arguments as a JSON array (`NULL` or `""` means none). The answer is the script's own return value,
    /// serialized into `slc_last_result`'s `result` field, so a host of any language reads it without knowing this
    /// engine's types. A name nothing declares is `ExitCode.Usage` with the reason, not a silent success.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_call")]
    public static int SlcCall(long handle, byte* nameUtf8, byte* argsJsonUtf8)
    {
        var name = ReadUtf8(nameUtf8);
        var argsJson = ReadUtf8(argsJsonUtf8);
        return Run(handle, "call", (session, itp, token) =>
        {
            object?[] args;
            try { args = ParseArgsArray(argsJson); }
            catch (Exception ex) { return Refusal("the arguments must be a JSON array: " + FriendlyExceptionMessage(ex)); }
            return CallBody(itp, name, args);
        });
    }

    /// <summary>THE CALL VERB'S BODY, shared with the COM server's `Eval` member (which builds its argument array from
    /// the caller's VARIANTs instead of JSON), so "no such function" and a script exception read the same from both.</summary>
    internal static (RunOutcome Outcome, string? ValueJson) CallBody(Interpreter itp, string? name, object?[] args)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Refusal("slc_call needs the function's name");
        var function = itp.ResolveGlobalFunction(name);
        if (function is null)
            return Refusal($"no script function named '{name}' in this session: run a file or some text first, so the name exists");

        try
        {
            var value = itp.InvokeScriptFunction(function, args, 0, 0);
            return (new RunOutcome { Status = ExitCode.Ok }, Interpreter.SerializeToText(value));
        }
        catch (Exception ex)
        {
            return (ScriptRunner.Failure(ex, itp), null);
        }
    }

    /// <summary>`slc_cancel(handle) -> status`: asks the run in flight to stop, which the tree walker answers between
    /// statements, `sleep` between chunks, and the VM at a loop back-edge, so a script that never reaches a statement
    /// still stops at its next loop turn. 0 means the request was made, 2 means nothing was running (or no such
    /// handle), and the run itself reports `cancelled` on its own result. The ONE verb callable while a run is in
    /// flight, from another thread, on purpose.</summary>
    [UnmanagedCallersOnly(EntryPoint = "slc_cancel")]
    public static int SlcCancel(long handle)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session))
            {
                _createError = NoSuchSession(handle);
                return ExitCode.Usage;
            }
            if (session.Cancel()) return ExitCode.Ok;
            session.LastError = "nothing was running on this session, so there was nothing to cancel";
            return ExitCode.Usage;
        }
        catch (Exception ex)
        {
            Remember(handle, "cancelling the run failed: " + FriendlyExceptionMessage(ex));
            return ExitCode.Failed;
        }
    }

    /// <summary>The ONE door every verb takes: handle, reentrancy, the session's thread, the verdict, and the JSON that
    /// `slc_last_result` reports. Split out because six verbs must answer the same way about a bad handle, a busy
    /// session and a run inside a callback.</summary>
    private static int Run(long handle, string verb, Func<EmbedSession, Interpreter, CancellationToken, (RunOutcome Outcome, string? ResultJson)> work)
    {
        // Written by the work item on the session's thread and read here AFTER `TryRun` has waited for it, which is
        // the same happens-before the completion event gives everything else a verb answers.
        string? lastResultJson = null;
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return ExitCode.Usage;
            if (_inHostCallback)
            {
                session.LastError = "this call came from inside the session's own output callback: a callback may not "
                                    + "drive the session it belongs to (post the work to another thread)";
                return ExitCode.Usage;
            }

            if (!session.TryRun(verb, (itp, token) =>
                {
                    var (outcome, resultJson) = work(session, itp, token);
                    lastResultJson = resultJson;
                    return outcome;
                }, out var answer, out var error))
            {
                Remember(handle, error ?? ("the session refused the " + verb));
                return ExitCode.Usage;
            }

            Report(session, answer, lastResultJson);
            return answer.Status;
        }
        catch (Exception ex)
        {
            Remember(handle, verb + " failed: " + FriendlyExceptionMessage(ex));
            return ExitCode.Failed;
        }
    }

    /// <summary>THE ASYNCHRONOUS FORM OF <see cref="Run"/>: accepted (0) or refused (2), and the verdict lands in
    /// `slc_last_result` when the run finishes, recorded by the SAME <see cref="Report"/> the synchronous verbs use, so
    /// a host cannot tell the two apart when it reads the result. `slc_poll` is the only new question.
    /// <para>
    /// WHY A HOST WANTS IT: an Office host is an STA, and an STA does not answer a COM call while it is inside one, so a
    /// script that drives its host through `Com.Host()` stalls under `slc_run_text` until the run it belongs to is over.
    /// Measured before this verb existed: a call made from the engine's thread to the host while the host was inside the
    /// synchronous verb answered nothing within five seconds, and the same call answered correctly once the host had
    /// returned to its message loop.
    /// </para></summary>
    private static int RunAsync(long handle, string verb,
                                Func<Interpreter, CancellationToken, (RunOutcome Outcome, string? ResultJson)> work)
    {
        try
        {
            if (!Sessions.TryGetValue(handle, out var session)) return ExitCode.Usage;
            if (_inHostCallback)
            {
                session.LastError = "this call came from inside the session's own output callback: a callback may not "
                                    + "drive the session it belongs to (post the work to another thread)";
                return ExitCode.Usage;
            }

            if (!session.TryRunAsync(verb, work, (outcome, resultJson) => Report(session, outcome, resultJson),
                                     out var error))
            {
                Remember(handle, error ?? ("the session refused the " + verb));
                return ExitCode.Usage;
            }

            // Accepted: the answer is not known yet, and saying otherwise would hand the host the PREVIOUS run's verdict.
            session.LastError = null;
            session.LastResult = null;
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Remember(handle, verb + " failed: " + FriendlyExceptionMessage(ex));
            return ExitCode.Failed;
        }
    }

    /// <summary>What a finished run leaves behind: the verdict as JSON and, when it failed, the sentence a host shows.
    /// ONE place, because a synchronous verb, an asynchronous one and the COM door must not report differently.</summary>
    internal static void Report(EmbedSession session, RunOutcome outcome, string? resultJson)
    {
        session.LastResult = OutcomeJson(outcome, resultJson);
        session.LastError = outcome.HasError ? outcome.Message ?? "the run failed" : null;
    }

    private static (RunOutcome, string?) Refusal(string message)
        => (new RunOutcome { Status = ExitCode.Usage, HasError = true, ErrorCode = "Usage", Message = message }, null);

    private static object?[] ParseArgsArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            throw new ScriptException("the arguments must be a JSON array, for example [1, \"two\", true]");
        var values = new List<object?>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            values.Add(Interpreter.ParseJsonValue(ref reader));
        return [.. values];
    }

    /// <summary>THE VERDICT, as JSON. Every member is PascalCase, which is this engine's pattern for JSON a host reads
    /// (`ScriptResult`, a form document, `slc_stats`): one convention, so a caller that reads one object can read the
    /// next. <see cref="AbiRevision"/> tracks this shape, because a member's name is part of the contract.
    ///
    /// WHAT EACH MEMBER MEANS: `Status` is what the process would exit with, `Ok` is a clean run, `Error` is a script or
    /// parse failure, `Fatal` an unexpected CLR one, `Cancelled` a run stopped by its token. `Checks` appears only when
    /// the Check family reported something during THIS run, and `ResultFrom` says the answer came from it rather than
    /// from the script.</summary>
    internal static string OutcomeJson(RunOutcome outcome, string? resultJson)
    {
        var sb = new StringBuilder(160);
        sb.Append("{\"Status\":").Append(outcome.Status)
          .Append(",\"Ok\":").Append(outcome.Status == ExitCode.Ok && !outcome.HasError ? "true" : "false")
          .Append(",\"Error\":").Append(outcome.HasError ? "true" : "false")
          .Append(",\"Fatal\":").Append(outcome.Fatal ? "true" : "false")
          .Append(",\"Cancelled\":").Append(outcome.Cancelled ? "true" : "false");
        // WHAT THE CHECK FAMILY SAW, which is the part a suite's verdict was missing: `exit(CheckFailed())` is the
        // script's own decision, so `Status` alone said 1 and nothing else. Absent when the run reported no check, so a
        // plain script's verdict does not grow.
        if (outcome.Checks is { } checks)
            sb.Append(",\"Checks\":").Append(Interpreter.SerializeToText(checks.ToDocument()));
        if (outcome.ErrorCode is { } code) sb.Append(",\"Code\":").Append(JsonString(code));
        if (outcome.Message is { } message) sb.Append(",\"Message\":").Append(JsonString(message));
        if (outcome.Line > 0) sb.Append(",\"Line\":").Append(outcome.Line).Append(",\"Column\":").Append(outcome.Column);
        if (outcome.Frames is { Count: > 0 } frames)
        {
            sb.Append(",\"Frames\":[");
            for (var i = 0; i < frames.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonString(frames[i]));
            }
            sb.Append(']');
        }
        if (resultJson is not null)
        {
            sb.Append(",\"Result\":").Append(resultJson);
            // The answer came from the checks rather than from the script (`ScriptRunner.WithChecks`), so a host can
            // tell the two apart even though they share this member.
            if (outcome.ResultFromChecks) sb.Append(",\"ResultFrom\":\"checks\"");
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Escapes a string for the JSON this ABI writes, and only that: the engine's own serializer exists
    /// (`JsonConverter`) but a host-visible status object is not a value a script wrote, so it stays here where the
    /// wire shape is stated once.</summary>
    private static string JsonString(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        sb.Append('"');
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Calls the host's line callback, with the reentrancy flag set so a verb invoked from inside it is
    /// refused rather than deadlocked. A failure from the callback itself is recorded and swallowed: nothing may throw
    /// across an `[UnmanagedCallersOnly]` boundary, and a host's broken callback must not end a script run.</summary>
    private static void EmitToHost(long handle, nint callback, nint user, int channel, string line)
    {
        var previous = _inHostCallback;
        _inHostCallback = true;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\0");
            fixed (byte* text = bytes)
                ((delegate* unmanaged<long, int, byte*, nint, void>)callback)(handle, channel, text, user);
        }
        catch (Exception ex)
        {
            Remember(handle, "the host's output callback failed: " + FriendlyExceptionMessage(ex));
        }
        finally
        {
            _inHostCallback = previous;
        }
    }

    /// <summary>Reads a NUL-terminated UTF-8 input, tolerating NULL (which means "not given" rather than an empty
    /// string, because a path and a source text are different empty cases).</summary>
    private static string? ReadUtf8(byte* text) => text == null ? null : Marshal.PtrToStringUTF8((nint)text);

    internal static string VersionText()
        => "ScriptLangCore " + (Program.Version ?? "N/A") + " (" + RuntimeName() + "), embed ABI " + AbiRevision;

    /// <summary>Which build is running, as one word a host can log. One statement because two callers report it
    /// (the version line and the stats JSON) and a divergence between them would be a support question of its own.
    /// The marker is one line because the checker reads the line ABOVE the branch and a continued comment is not it.</summary>
    // platform: diagnostic, it reports which build loaded so a host log answers "why did this differ", and it guards nothing.
    private static string RuntimeName() => RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT";

    /// <summary>Records a failure against a handle that may or may not still exist, because an export's catch must
    /// leave the reason somewhere a host can read even when the session itself is the thing that broke.</summary>
    private static void Remember(long handle, string message)
    {
        if (Sessions.TryGetValue(handle, out var session)) session.LastError = message;
        else _createError = message;
    }

    private static string NoSuchSession(long handle)
        => "no session with handle " + handle + " (a handle is never reused, so this one was freed or was never created)";

    /// <summary>Writes <paramref name="text"/> into a caller-owned buffer and answers the length it NEEDS, the
    /// `snprintf` convention: `capacity` counts the terminator, so at most `capacity - 1` bytes of text are written
    /// and the buffer is always terminated when `capacity` is positive. A NULL buffer with a 0 capacity is the
    /// sizing probe, and it answers the same number.</summary>
    private static int CopyOut(string? text, byte* buffer, int capacity)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (buffer != null && capacity > 0) buffer[0] = 0;
            return 0;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        if (buffer != null && capacity > 0)
        {
            var written = Math.Min(bytes.Length, capacity - 1);
            bytes.AsSpan(0, written).CopyTo(new Span<byte>(buffer, written));
            buffer[written] = 0;
        }
        return bytes.Length;
    }
}