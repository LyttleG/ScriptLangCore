namespace ScriptLangCore;

/// <summary>ONE EMBEDDED SESSION: the interpreter a host holds, the THREAD it runs on, the output it writes, and the
/// facts it tells the engine about itself.
///
/// WHY A THREAD IS PART OF THE SESSION. Two measurements from the 2026-10-27 AOT-DLL POC decide it, and neither is
/// negotiable:
///   - a DLL runs on the HOST's stack, so the engine's own `IlcDefaultStackSize` is INERT there. The same script
///     recursed 59,990 frames inside the DLL against 1,392,612 in the AOT exe, and the engine's depth guard measures
///     what is LEFT of the stack a run is on, so a host thread with the managed default of 1 MB refuses the FIRST
///     re-entry ("Maximum call depth exceeded (1)");
///   - a host's thread is MTA unless the host made it otherwise, and this engine's COM Automation requires STA, so
///     `Com.CreateObject` refuses outright on an MTA thread.
/// A session therefore prefers a thread it owns (32 MB, STA), and uses the host's thread only when that thread can
/// actually carry a run. That check is not "does it look roomy" but "how big IS it": `TryGetStackInfo` reports the
/// thread's TOTAL stack, while `ScriptEntryStackIsTight` answers the opposite question and would have been the wrong
/// probe.
///
/// THE THREE MODES:
///   "auto"  the HOST's thread when it is STA and offers at least <see cref="HostThreadMinStackBytes"/>, otherwise a
///           thread the session owns. Reported through `slc_thread_mode`, because a host that needs a 50,000-deep
///           script has to be able to read which budget it got.
///   "own"   always a thread of our own. The only way to get 32 MB on a host whose calling thread is small.
///   "host"  never a thread of ours: the session FAILS if the calling thread cannot carry a run, rather than
///           silently doing something else. A session pinned this way can only be driven from that same thread.
///
/// HOW WORK REACHES THE THREAD: an own-thread session posts each verb as a work item and the host's calling thread
/// waits for it (one at a time per handle, refused rather than queued when a second arrives), because the interpreter
/// is only ever touched on the thread that built it: its call-frame stacks, its cancellation token and the pump's run
/// thread id are all thread-affine. That is also why `slc_cancel` is the ONE verb that is not marshalled: it sets a
/// token, which is a signal from any thread by design.
///
/// LIFETIME: the interpreter is created ON the thread that runs it and disposed on that same thread, which is also what
/// closes anything a script left behind (its forms, its `Events` feeds).</summary>
internal sealed class EmbedSession
{
    /// <summary>The stack a session's own thread is created with: the same 32 MB the shipped AOT binary gives its
    /// main thread, and the same number `Task.RunSta` uses, for the reason in the class comment.</summary>
    internal const int WorkerStackBytes = 32 * 1024 * 1024;

    /// <summary>What a HOST's thread must offer before a session will run a script on it. Half of the 32 MB the
    /// engine's own thread gets, so the depth guard has the same order of magnitude to work with, and small enough
    /// that a host which deliberately created a roomy thread does not have to match our number exactly.</summary>
    internal const ulong HostThreadMinStackBytes = 16UL * 1024 * 1024;

    private static long _nextId;
    private static readonly ConcurrentDictionary<int, long> PinnedThreads = new();

    private readonly ManualResetEventSlim _ready = new(false);
    private readonly ManualResetEventSlim _work = new(false);
    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly EmbedSink _out;
    private readonly EmbedSink _err;
    private IDisposable? _factsScope;
    private Thread? _worker;
    private Interpreter? _engine;
    private CancellationTokenSource? _runCts;
    /// <summary>The run's completion as a Task, for the asynchronous verbs: created where the run is ACCEPTED and
    /// completed where its verdict lands, so a host has one object to await instead of polling. It is also what
    /// `slc_cancel` completes, which is the difference between "the body stopped" and "the run is over": measured, a
    /// top-level `await` blocks the run thread with the token unpolled, so a host that only waits for the body would
    /// learn about a cancel up to one awaited call late.</summary>
    private Task? _runTask;
    /// <summary>Records the verdict, once: the first of {the run's own completion, a cancel} wins, so a cancel that
    /// arrives while the run is already reporting does not overwrite it and a host never reads two verdicts as one
    /// run's.</summary>
    private Action<RunOutcome, string?>? _runCompleted;
    private int _runFinished;
    private volatile bool _stopping;
    private int _busy;
    private bool _disposed;

    /// <summary>The host object the host adopted, as a Global Interface Table cookie (0 when it adopted none), and
    /// whether one is currently registered.</summary>
    private uint _hostCookie;
    private volatile bool _hostAdopted;
    /// <summary>What the host handed over in `slc_adopt`, kept as the CALLER's pointer: it is registered only while a run
    /// uses it, because a registration held between runs keeps the host application from shutting down (measured).</summary>
    private IntPtr _hostPointer;

    private EmbedSession()
    {
        _out = new EmbedSink(0, this);
        _err = new EmbedSink(1, this);
    }

    internal long Id { get; } = Interlocked.Increment(ref _nextId);

    /// <summary>What the engine answers this session about itself (`Application.Path`, `Application.FilePath`,
    /// `Application.Args`). Attached on the owning thread for the session's whole life, because the `Application`
    /// namespace reads it when the interpreter is CONSTRUCTED.</summary>
    internal ScriptSession Facts { get; } = new();

    /// <summary>0 = a thread of our own, 1 = the host's thread. Read back through `slc_thread_mode`.</summary>
    internal int Mode { get; private set; }

    /// <summary>The apartment the owning thread reported when the session was created, for `slc_stats`.</summary>
    internal string Apartment { get; private set; } = "Unknown";

    /// <summary>The owning thread's TOTAL stack in bytes, for `slc_stats` (a depth failure has to be attributable to
    /// the budget rather than to look like an engine defect).</summary>
    internal ulong StackBytes { get; private set; }

    /// <summary>How many global names the freshly built interpreter had, i.e. the engine really is alive and its
    /// namespaces are registered. A cheap "is this a working session" number for a host's log.</summary>
    internal int Globals { get; private set; }

    /// <summary>The last thing that went wrong for THIS handle, readable through `slc_last_error`.</summary>
    internal string? LastError { get; set; }

    /// <summary>The last verb's answer as JSON, readable through `slc_last_result`: the status, the error, and for a
    /// call the script's own return value. One door, so a host does not have to know which verb wrote which field.</summary>
    internal string? LastResult { get; set; }

    /// <summary>The sandbox this session's options named (`dirs`/`deny`), or null. Attached around every run BELOW,
    /// on the thread the run executes on, so a member call or a path resolves under this session's policy and no other
    /// session's (the policy is thread-static, the same shape the host's facts use).</summary>
    internal SessionPolicy? Policy { get; set; }

    /// <summary>Where a line of script output goes, or null when the host registered no callback (output is then
    /// dropped rather than written into somebody else's console). Set by `slc_set_output`.</summary>
    internal Action<int, string>? LineSink { get; set; }

    /// <summary>The thread that currently owns the interpreter, so the API can refuse a call from another thread
    /// when that would be a lie (a host-pinned session cannot be marshalled to).</summary>
    internal Thread? OwnerThread { get; private set; }

    internal static EmbedSession? Create(EmbedOptions options, out string? error)
    {
        error = null;
        var session = new EmbedSession();
        session.Facts.Home = options.Home;
        session.Facts.ImagePath = options.ImagePath;
        // A HOST's command line is another program's flags, so an embedded session starts with NO arguments unless
        // the host gave some: leaving this null would mean "read the process's own argv", which is the wrong answer
        // here and the whole reason `ScriptSession` exists.
        session.Facts.Arguments = options.Arguments ?? [];
        session.Policy = options.Policy;

        var wantsHost = options.ThreadMode == "host";
        if (options.ThreadMode == "auto")
        {
            var (apartment, stack) = CurrentThreadShape();
            wantsHost = apartment == "STA"
                        && stack >= HostThreadMinStackBytes
                        && !PinnedThreads.ContainsKey(Environment.CurrentManagedThreadId);
        }

        if (wantsHost)
        {
            var (apartment, stack) = CurrentThreadShape();
            if (apartment != "STA")
            {
                error = "this thread is " + apartment + ", and this engine's COM surface requires an STA thread: "
                        + "create the session on an STA thread, or let the engine own one (\"thread\":\"auto\")";
                return null;
            }
            if (stack < HostThreadMinStackBytes)
            {
                error = "this thread offers " + (stack / (1024 * 1024)) + " MB of stack, and a script needs at least "
                        + (HostThreadMinStackBytes / (1024 * 1024)) + " MB because the depth guard measures what is left "
                        + "of it: let the engine own a thread (\"thread\":\"auto\" or \"own\")";
                return null;
            }
            session.Mode = 1;
            session.OwnerThread = Thread.CurrentThread;
            if (!session.StartOnThisThread(out error)) return null;
            PinnedThreads[Environment.CurrentManagedThreadId] = session.Id;
            return session;
        }

        session.Mode = 0;
        if (!session.StartWorker(out error)) return null;
        return session;
    }

    /// <summary>The session's own thread: attach the facts, build the interpreter THERE, then serve work until the
    /// session is freed. Everything the interpreter will ever touch happens on this thread, which is what keeps the
    /// engine's thread-affine state coherent and what makes the 32 MB real.</summary>
    private bool StartWorker(out string? error)
    {
        error = null;
        var worker = new Thread(() =>
        {
            try
            {
                using var facts = Facts.Attach();
                using var handle = InterpreterManager.Create(caseSensitive: true, context: "EmbedSession");
                _engine = handle.Instance;
                ApplySinks(handle.Instance);
                Capture(handle.Instance);
                _ready.Set();

                while (true)
                {
                    _work.Wait();
                    _work.Reset();
                    if (_stopping) break;
                    while (_queue.TryDequeue(out var item))
                    {
                        if (_stopping) break;
                        item();
                    }
                }

                _engine = null;   // the `using` above disposes it on THIS thread, which is the point
            }
            catch (Exception ex)
            {
                LastError = FriendlyExceptionMessage(ex);
                _ready.Set();
            }
        }, WorkerStackBytes)
        {
            IsBackground = true,
            Name = "SLC embed",
        };
        // Windows is where this engine's whole UI and COM surface lives; elsewhere the apartment call is not
        // supported and the thread simply runs as it is (the same rule `Task.RunSta` states).
        if (OperatingSystem.IsWindows()) worker.SetApartmentState(ApartmentState.STA);
        _worker = worker;
        worker.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(30)))
        {
            error = "the session's thread did not become ready within 30 s";
            return false;
        }
        if (LastError is { } failed)
        {
            error = failed;
            return false;
        }
        return true;
    }

    /// <summary>The host's own thread: attach the facts, build the interpreter here, and keep it until the session is
    /// freed. The `InterpreterHandle` is NOT used as a scope because the interpreter has to outlive this method, so
    /// the disposal is done through <see cref="DisposeEngine"/>, which unregisters from `InterpreterManager` exactly
    /// as the handle would have.</summary>
    private bool StartOnThisThread(out string? error)
    {
        error = null;
        try
        {
            _factsScope = Facts.Attach();
            var handle = InterpreterManager.Create(caseSensitive: true, context: "EmbedSession (host thread)");
            _engine = handle.Instance;
            ApplySinks(handle.Instance);
            Capture(handle.Instance);
            return true;
        }
        catch (Exception ex)
        {
            error = FriendlyExceptionMessage(ex);
            _factsScope?.Dispose();
            _factsScope = null;
            return false;
        }
    }

    /// <summary>Points the interpreter's output at this session, so `print` and the engine's own log lines reach the
    /// host's callback instead of the process's console (see `Interpreter.Out`/`Err` for why the sink lives there and
    /// not in `Console.SetOut`).</summary>
    private void ApplySinks(Interpreter engine)
    {
        engine.Out = _out;
        engine.Err = _err;
    }

    private void Capture(Interpreter engine)
    {
        var (apartment, stack) = CurrentThreadShape();
        Apartment = apartment;
        StackBytes = stack;
        Globals = engine.GetGlobalScope().Count;

        // THE HOST'S OBJECT, if it ever adopts one: asked through the session so a later adoption (or a release) is
        // visible to the script without rebuilding anything. `HostObject.Resolve` runs on THIS thread, which is the one
        // that will make the call, so the reference is valid in this apartment by construction.
        engine.HostObjectSource = () => _hostAdopted ? DispatchVtbl.HostObject.Resolve(_hostCookie) : IntPtr.Zero;
    }

    /// <summary>ADOPTS THE HOST'S OWN OBJECT, from the thread that owns it: the host hands over an interface it already
    /// holds (VBA: `ObjPtr(Application)`) and the engine keeps the POINTER, registering it in the Global Interface Table
    /// only for the duration of a run. Answers false with a reason when there is nothing to hand over.
    /// <para>
    /// ON THE HOST'S THREAD, deliberately, and never marshalled through the queue: the pointer is only meaningful as the
    /// CALLER holds it, which is the whole point. A second adoption replaces the first.
    /// </para>
    /// <para>
    /// WHY THE REGISTRATION IS NOT HELD HERE. Measured on a workbook that adopted the application and then quit: a
    /// registration kept for the session's lifetime holds a COM reference to `Application`, and Excel's own quit does not
    /// complete while one exists, so the process stayed alive invisibly (no window, 25 s and counting) until something
    /// released it. Calling `EngineRelease` first made it leave in 0.5 s, which is what a session that never adopted does.
    /// So the reference now exists only while a run is actually using it (`BeginHostUse`/`EndHostUse`), which is also the
    /// honest COM semantics: a script that still holds a `Com.Host()` value keeps it alive through that value, and an idle
    /// engine keeps nothing.
    /// </para></summary>
    internal bool AdoptHost(IntPtr dispatch, out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "this session has been freed";
            return false;
        }
        if (dispatch == IntPtr.Zero)
        {
            error = "slc_adopt needs the host object's pointer (in VBA: ObjPtr(Application))";
            return false;
        }

        EndHostUse();                       // a previous registration cannot outlive the adoption it belonged to
        _hostPointer = dispatch;
        return true;
    }

    /// <summary>Registers the adopted object for the run that is starting, ON THE CALLING THREAD, which is the one that
    /// holds the pointer. Called by both verbs before the work is posted, so a script's `Com.Host()` finds it.</summary>
    private void BeginHostUse()
    {
        if (_hostPointer == IntPtr.Zero || _hostCookie != 0) return;
        var cookie = DispatchVtbl.HostObject.Register(_hostPointer);
        if (cookie == 0) return;            // Com.Host() then answers null, which is the documented miss
        _hostCookie = cookie;
        _hostAdopted = true;
    }

    /// <summary>Releases the registration a run used. Safe from any thread (the GIT is a process-wide singleton) and
    /// idempotent, which matters because the synchronous verb ends on the caller's thread and the asynchronous one on the
    /// session's.</summary>
    private void EndHostUse()
    {
        _hostAdopted = false;
        var cookie = _hostCookie;
        _hostCookie = 0;
        if (cookie != 0) DispatchVtbl.HostObject.Revoke(cookie);
    }

    /// <summary>Runs one verb on the thread that owns the interpreter, or says why it cannot. <paramref name="work"/>
    /// receives the interpreter and the run's token; <paramref name="outcome"/> is what the run answered.</summary>
    internal bool TryRun(string verb, Func<Interpreter, CancellationToken, RunOutcome> work, out RunOutcome outcome, out string? error)
    {
        outcome = default;
        error = null;
        if (_disposed)
        {
            error = "this session has been freed";
            return false;
        }
        if (ImportedFromAnotherThread())
        {
            error = ThreadRefusal();
            return false;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            error = "this session is already running a " + verb + ": one call at a time per handle";
            return false;
        }

        try
        {
            using var cts = new CancellationTokenSource();
            _runCts = cts;

            // THE ADOPTED HOST OBJECT IS REGISTERED FOR THIS RUN ONLY, on this thread (the one that holds the pointer),
            // and released in the finally below: a registration kept between runs holds a COM reference to the host
            // application, which stops Excel's own quit from completing (measured, see AdoptHost).
            BeginHostUse();

            if (Mode == 1)
            {
                using var policy = SessionPolicy.Attach(Policy);
                outcome = work(Engine, cts.Token);
                return true;
            }

            if (ReferenceEquals(Thread.CurrentThread, _worker))
            {
                error = "a session cannot be driven from inside its own run (a callback may not call back into the "
                        + "session: post the work to another thread)";
                return false;
            }

            // The interpreter lives on the worker, so the verb is POSTED and the calling thread waits for it. One
            // completion event per verb, disposed immediately, because two such kernel events held per session was
            // measured as a handle leak in the previous round.
            RunOutcome result = default;
            Exception? failure = null;
            using var done = new ManualResetEventSlim(false);
            _queue.Enqueue(() =>
            {
                using var policy = SessionPolicy.Attach(Policy);
                try { result = work(Engine, cts.Token); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            });
            _work.Set();
            done.Wait();
            if (failure is not null)
            {
                error = FriendlyExceptionMessage(failure);
                return false;
            }
            outcome = result;
            return true;
        }
        finally
        {
            EndHostUse();                   // the run is over, so the host application holds no reference from here
            _runCts = null;
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    /// <summary>THE SAME RUN, POSTED AND NOT WAITED FOR, the shape a host needs when the script drives the host itself.
    /// Measured: while this session's caller is inside a synchronous verb, the host application cannot answer a COM
    /// call (an STA does not pump while it is inside a call), so `Com.Host()` would stall. Returning at once lets the
    /// host go back to its message loop (Excel: `DoEvents`), and the script's calls then arrive.
    /// <para>
    /// The completion is the session's, exactly as a synchronous verb's: <paramref name="completed"/> runs ON THE
    /// SESSION'S THREAD and is what records `LastResult` and `LastError`, so `slc_last_result` reads the same verdict
    /// either way and `slc_poll` is the only new question a host has to ask.
    /// </para></summary>
    internal bool TryRunAsync(string verb,
                              Func<Interpreter, CancellationToken, (RunOutcome Outcome, string? ResultJson)> work,
                              Action<RunOutcome, string?> completed,
                              out string? error)
    {
        error = null;
        if (_disposed)
        {
            error = "this session has been freed";
            return false;
        }
        if (ImportedFromAnotherThread())
        {
            error = ThreadRefusal();
            return false;
        }
        if (Mode == 1)
        {
            // On the host's own thread there is nothing to return to: the caller IS the run thread, so an asynchronous
            // verb would block exactly like the synchronous one. Refused by name rather than silently waiting.
            error = "an asynchronous run needs a session thread of the engine's own: create the session with "
                    + "thread:\"auto\" or thread:\"own\" (thread:\"host\" runs on the calling thread, so it cannot return "
                    + "to the host before the script ends)";
            return false;
        }
        if (ReferenceEquals(Thread.CurrentThread, _worker))
        {
            error = "a session cannot be driven from inside its own run (a callback may not call back into the "
                    + "session: post the work to another thread)";
            return false;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            error = "this session is already running a " + verb + ": one call at a time per handle";
            return false;
        }

        var cts = new CancellationTokenSource();
        _runCts = cts;
        _runCompleted = completed;
        var runTask = new Task();
        _runTask = runTask;
        Interlocked.Exchange(ref _runFinished, 0);
        BeginHostUse();                     // registered on THIS thread, the one that holds the adopted pointer
        _queue.Enqueue(() =>
        {
            using var policy = SessionPolicy.Attach(Policy);
            try
            {
                var (outcome, resultJson) = work(Engine, cts.Token);
                FinishRun(outcome, resultJson);
            }
            catch (Exception ex)
            {
                var failed = new RunOutcome
                {
                    Status = ExitCode.Failed,
                    HasError = true,
                    ErrorCode = "Unspecified",
                    Message = FriendlyExceptionMessage(ex)
                };
                FinishRun(failed, null);
            }
            finally
            {
                // The asynchronous run ends HERE, on the session's thread, so the registration it used is released here:
                // the host application must hold no reference from the engine once the script is done (a workbook's own
                // quit could not complete otherwise, measured).
                EndHostUse();
                _runCts = null;
                Interlocked.Exchange(ref _busy, 0);
                // A run that ended WITHOUT a verdict (a cancel already finished it, or the completion path itself
                // failed) still ends its Task here, so a waiting host is never left on a run that is over.
                if (_runTask is { IsCompleted: false } pending) pending.SetResult();
                _runTask = null;
                _runCompleted = null;
                cts.Dispose();
            }
        });
        _work.Set();
        return true;
    }

    /// <summary>Stops the run in flight, if any. The one verb that is NOT marshalled to the owning thread: a token is a
    /// signal from any thread by design, and a cancel that had to queue behind the run it is cancelling would never
    /// arrive. Answers false when nothing is running.</summary>
    internal bool Cancel()
    {
        var cts = _runCts;
        if (cts is null) return false;
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { return false; }
        // THE RUN IS OVER AS FAR AS A HOST CAN SEE, at once: the token is polled at a statement boundary, sleep chunk
        // or VM back-edge, so a run blocked inside a top-level `await` learns about it one awaited call late (measured:
        // a cancel issued at 372 ms reached a 2,500 ms await as `cancelled=True` at 2,639 ms). Completing the run's
        // task here is what a host awaits, and the verdict is the one the run itself will report, because a cancelled
        // run answers exactly this shape.
        FinishRun(CancelledOutcome(), null);
        return true;
    }

    /// <summary>The verdict a cancel is: the same shape <see cref="ScriptRunner"/> builds for a run its token stopped,
    /// so a host cannot tell which of the two reported it.</summary>
    private static RunOutcome CancelledOutcome() => new()
    {
        Status = ExitCode.Failed,
        HasError = true,
        Cancelled = true,
        ErrorCode = nameof(ErrorCode.Cancelled),
        Message = "Stopped: the run was cancelled",
    };

    /// <summary>Records a run's verdict ONCE and completes its Task, whichever of {the run's own completion, a cancel}
    /// arrives first. One verdict per run is what keeps `slc_last_result` readable: a cancel that lands while the run is
    /// already reporting must not overwrite what the run answered, and a late cancel must not resurrect a finished
    /// run's Task.</summary>
    private void FinishRun(RunOutcome outcome, string? resultJson)
    {
        if (Interlocked.Exchange(ref _runFinished, 1) != 0) return;
        var report = _runCompleted;
        try { report?.Invoke(outcome, resultJson); }
        catch (Exception ex) { LastError = "recording the run's verdict failed: " + FriendlyExceptionMessage(ex); }
        if (_runTask is { IsCompleted: false } pending) pending.SetResult();
    }

    /// <summary>True while a verb is in flight, so `slc_free` can refuse instead of waiting or killing.</summary>
    internal bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>True while an ASYNCHRONOUS run's task is incomplete, which is the question `slc_poll` exists for. It
    /// differs from <see cref="IsBusy"/> for exactly one moment: a cancelled run's task is over as soon as the cancel
    /// lands, while the body may still be finishing the call it was inside (`IsBusy` keeps refusing a new verb until
    /// it has).</summary>
    internal bool RunActive => _runTask is { IsCompleted: false };

    private Interpreter Engine => _engine ?? throw new InvalidOperationException("this session has no interpreter (it failed to build or was already freed)");

    internal bool ImportedFromAnotherThread()
        => Mode == 1 && OwnerThread is { } owner && !ReferenceEquals(Thread.CurrentThread, owner);

    internal string ThreadRefusal()
        => "this session runs on the thread that created it (it was opened with \"thread\":\"host\"), so it cannot be "
           + "driven from another thread: open it with \"auto\" or \"own\" to have the engine own a thread that calls "
           + "can be marshalled to";

    /// <summary>Releases the session. True when it is really gone; false (with a reason) when a run is still inside a
    /// native call that cannot be interrupted, in which case the handle STAYS VALID and freeing it can be retried.
    /// That is the honest limit of an in-process engine: a thread cannot be aborted, which is why untrusted scripts
    /// belong in a child process.</summary>
    internal bool TryDispose(out string? error)
    {
        error = null;
        if (_disposed) return true;

        if (IsBusy)
        {
            error = "a run is in progress on this session, so it cannot be freed: wait for it, or cancel it first";
            return false;
        }

        if (Mode == 1)
        {
            DisposeEngine();
            ReleaseHostObject();
            _factsScope?.Dispose();
            _factsScope = null;
            PinnedThreads.TryRemove(Environment.CurrentManagedThreadId, out _);
            ReleaseWaitHandles();
            _disposed = true;
            return true;
        }

        if (_worker is { } worker && ReferenceEquals(Thread.CurrentThread, worker))
        {
            error = "a session cannot free itself from inside its own run";
            return false;
        }

        _stopping = true;
        _work.Set();
        if (_worker is { } running && !running.Join(TimeSpan.FromSeconds(10)))
        {
            error = "the session's thread did not stop within 10 s: a run is still inside a native call that cannot be "
                    + "interrupted, so this handle stays valid and freeing it can be retried";
            return false;
        }

        ReleaseWaitHandles();
        ReleaseHostObject();
        _disposed = true;
        return true;
    }

    /// <summary>Releases the host object's registration, so an adopted Application is not held for the life of the
    /// process. Called on every way out of `TryDispose`, and idempotent.</summary>
    private void ReleaseHostObject()
    {
        EndHostUse();
        _hostPointer = IntPtr.Zero;
    }

    /// <summary>Releases the wait events, which is not bookkeeping: `ManualResetEventSlim` creates a KERNEL event the
    /// first time it actually blocks, so a session that never disposed them left handles behind per create/free cycle.
    /// Measured before this: forty cycles took the process from 294 handles to 380, i.e. 2.15 per cycle, which is
    /// exactly these two and nothing else. A host that opens a session per script run would have climbed without bound,
    /// so this is the difference between "works" and "works in a loop".</summary>
    private void ReleaseWaitHandles()
    {
        _ready.Dispose();
        _work.Dispose();
    }

    private void DisposeEngine()
    {
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        // What `InterpreterHandle.Dispose` does, spelled out because this interpreter outlives the method that
        // created it: without the unregister, `InterpreterManager.ReportLeaks` would name a session that is gone.
        InterpreterManager.Unregister(engine);
        engine.Dispose();
    }

    /// <summary>This thread's apartment and TOTAL stack. An apartment of "Unknown" (COM never initialised here) is
    /// never treated as STA, and a stack the probe cannot measure is 0, which fails the same way.</summary>
    private static (string Apartment, ulong Stack) CurrentThreadShape()
    {
        var apartment = "Unknown";
        if (OperatingSystem.IsWindows())
        {
            try { apartment = Thread.CurrentThread.GetApartmentState().ToString(); }
            catch (PlatformNotSupportedException) { /* not a platform that has apartments */ }
        }
        var stack = Interpreter.TryGetStackInfo(out _, out var size, out _, out _) ? size : 0UL;
        return (apartment, stack);
    }

    /// <summary>One output channel (`print` on 0, the engine's log lines on 1) as a `TextWriter`, assembled a line at a
    /// time because that is the unit a host callback carries: `print` writes its arguments and then a newline, and a
    /// host should not have to re-implement that joining. Lines are flushed on the writer's own newline, and a run that
    /// ends mid-line still delivers what it wrote.</summary>
    private sealed class EmbedSink : TextWriter
    {
        private readonly int _channel;
        private readonly EmbedSession _session;
        private readonly StringBuilder _line = new();
        private readonly Lock _gate = new();

        internal EmbedSink(int channel, EmbedSession session)
        {
            _channel = channel;
            _session = session;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                if (value == '\n') FlushLine();
                else if (value != '\r') _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_gate)
            {
                foreach (var ch in value)
                {
                    if (ch == '\n') FlushLine();
                    else if (ch != '\r') _line.Append(ch);
                }
            }
        }

        public override void WriteLine(string? value) => Write((value ?? "") + "\n");

        public override void WriteLine() => Write('\n');

        public override void Flush()
        {
            // Nothing to do: a line is delivered as it completes. Kept overridden so a caller's Flush is not a surprise.
        }

        private void FlushLine()
        {
            var text = _line.ToString();
            _line.Clear();
            _session.LineSink?.Invoke(_channel, text);
        }
    }
}
