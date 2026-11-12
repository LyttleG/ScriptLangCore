using ClrTask = System.Threading.Tasks.Task;

namespace ScriptLangCore;

public enum TaskStatusEnum { Running, Completed, Faulted }

/// <summary>What a script awaits. It is a thin shell over <see cref="TaskCompletionSource{TResult}"/>, which is
/// the completion machinery .NET already has, so this class owns no lock, no continuation list and no delay queue
/// of its own.
///
/// WHAT IT REPLACED, measured: the old shape kept <c>_continuation</c>/<c>_continuations</c>/<c>_mainContinuation</c>
/// slots under a <c>Lock</c>, a <c>DateTime</c>-polled delay list, and a <c>Wait()</c> loop that armed a
/// high-resolution timer to beat the system tick (1.55 ms measured for a 1 ms delay against 15.55 ms for the CLR's
/// own). Its completion was observed only by whoever pumped. The completion source removes all of that: a
/// continuation is registered with <see cref="ClrTask.ContinueWith(TaskContinuation)"/> (asynchronously, so it can
/// never run inline inside the setter), a delay is a native timer, and <see cref="Wait"/> keeps only the ONE thing
/// that is this engine's business: driving the interpreter's coroutine pump while the script waits.</summary>
public sealed class Task
{
    private readonly TaskCompletionSource<object?> _completion =
        new(global::System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Optional driver the Wait loop calls while this task is incomplete. Set ONLY on a task an `async`
    /// script CALL or a delay created, so it can drive the interpreter's coroutine pump; every other task leaves
    /// it null and Wait behaves exactly as it always did.</summary>
    private Action? _pumpDriver;
    /// <summary>The pump's work signal, so a waiter wakes when the pump gains work (a coroutine became due, an
    /// action was posted) instead of at a fixed poll interval.</summary>
    private WaitHandle? _pumpWork;
    /// <summary>How long a wait may sleep with nothing signalled: the pump's next due moment, or -1 when the clock
    /// holds nothing for this script.</summary>
    private Func<int>? _pumpNextDue;

    /// <summary>The underlying .NET task, which is what a native continuation and a host await attach to.</summary>
    internal ClrTask NativeTask => _completion.Task;

    /// <summary>Attaches the coroutine pump this task's Wait loop must call while it waits.</summary>
    internal void SetPumpDriver(Action driver) => _pumpDriver = driver;

    /// <summary>Hands this task the pump's work signal and its own idea of when the clock holds something. The
    /// deadline of a DELAY is no longer the pump's business (a native timer completes it), so the only scheduled
    /// thing left is a coroutine's wake time, and a `sleep` in a script body has no handle to signal, so this
    /// question IS the wait's timeout.</summary>
    internal void SetPumpWork(WaitHandle work, Func<int> nextDueMs)
    {
        _pumpWork = work;
        _pumpNextDue = nextDueMs;
    }

    internal WaitHandle? PumpWork => _pumpWork;

    /// <summary>The pump's next-due question, inherited by a composite the same way its driver is.</summary>
    internal Func<int>? PumpNextDue => _pumpNextDue;

    /// <summary>The driver a COMPOSITE task inherits from its constituents: waiting on a `Task.WhenAll` of two
    /// async calls has to be able to drive their bodies too, or the wait would never end.</summary>
    internal Action? PumpDriver => _pumpDriver;

    // ── Awaiter (for a host that awaits this type) ──
    public readonly struct Awaiter(Task t) : INotifyCompletion
    {
        public Awaiter GetAwaiter() => this;
        public bool IsCompleted => t.IsCompleted;
        public void OnCompleted(Action continuation) => t.ContinueWithCallback(continuation);
        public object? GetResult() => t.Result;
    }

    public Awaiter GetAwaiter() => new(this);

    // ── Properties ──
    public bool IsCompleted => _completion.Task.IsCompleted;

    public TaskStatusEnum Status =>
        !_completion.Task.IsCompleted ? TaskStatusEnum.Running
        : _completion.Task.IsFaulted || _completion.Task.IsCanceled ? TaskStatusEnum.Faulted
        : TaskStatusEnum.Completed;

    public object? Result
    {
        get
        {
            Wait();
            return _completion.Task.GetAwaiter().GetResult();
        }
    }

    // ── Completion ──
    public void SetResult(object? result = null) => _completion.TrySetResult(result);

    public void SetException(Exception exception)
    {
        // Unwrap TargetInvocationException (a reflected call wraps what the callee threw).
        while (exception is TargetInvocationException tie)
            exception = tie.InnerException ?? tie;
        _completion.TrySetException(exception);
    }

    /// <summary>C#'s <c>ConfigureAwait(bool)</c>, accepted as a NO-OP so ported code reads unchanged: this engine
    /// has one cooperative pump and no synchronisation context to marshal a continuation away from.</summary>
    public Task ConfigureAwait(bool continueOnCapturedContext)
    {
        _ = continueOnCapturedContext;
        return this;
    }

    // ── Wait (blocking, and it drives the script's pump) ──
    /// <summary>C#'s <c>Wait()</c>: block until the task completes, then raise its failure at this call. A script
    /// reaches it as a member of the engine's <c>Task</c>; the tree's own await sites go through
    /// <see cref="Interpreter.WaitForTask"/> so every parked wait is NAMED, COUNTED and CANCELLABLE.</summary>
    public void Wait() => WaitBlocking(site: null, line: 0, column: 0);

    /// <summary>THE wait, and the only one: every site that cannot suspend (an `await` the replay would make wrong, an
    /// `await foreach` step, an `await using` disposal, the HTTP stream source, and a script's own `t.Wait()`) goes
    /// through here, so the rule is stated once.
    ///
    /// It drives the interpreter's pump while it waits, which is what makes it the engine's wait rather than a thread
    /// park: a plain CLR wait at any of those sites deadlocks, because the awaited body resumes on that same pump.
    ///
    /// IT IS CANCELLABLE NOW. The run's kill signal (`Interpreter.ActiveRunCancellation`) is one of the handles, and it
    /// is re-asked each wake-up, so a `cts.Cancel()` lands within one wake instead of when the awaited call finishes.
    /// Measured before this: a cancel issued while a file-level `for` waited on a 900 ms delay was observed only when
    /// that delay completed, because the token was polled at statement boundaries only (and the loop's await is one
    /// statement). Uncatchable on purpose: `ErrorCode.Cancelled` is skipped by both engines' catch arms and still runs
    /// `finally`, the rule every other poll site follows. The position is the awaiting site's, so the refusal reads
    /// where the script parked rather than at the enclosing function.
    ///
    /// The caller's `site` names the shape (`await`, `await foreach`, `await using`, `http stream`) and feeds the
    /// census <see cref="Interpreter.ParkedAwaitCounts"/>; a script's own `t.Wait()` passes null and is counted as
    /// `Task.Wait`.</summary>
    internal void WaitBlocking(string? site, int line, int column)
    {
        var completion = _completion.Task;
        if (!completion.IsCompleted)
        {
            Interpreter.NoteParkedWait(site ?? "Task.Wait");
            var tracing = Interpreter.AsyncTraceEnabled && site is not null;
            var started = tracing ? Environment.TickCount64 : 0;
            PumpPending();
            var completionHandle = ((IAsyncResult)completion).AsyncWaitHandle;
            // THE RUN QUEUE'S SIGNAL IS ALWAYS IN THE SET. A script continuation (`ContinueWith`, a composite
            // settling, an event kick) can only arrive through that queue, so a waiter that did not watch it would
            // park with work sitting one call away: measured, that is exactly what `ContinueWith(..).Wait()` did.
            var handles = new List<WaitHandle>(4) { completionHandle, TaskPump.ReadySignal };
            if (_pumpWork is not null) handles.Add(_pumpWork);
            // THE KILL SIGNAL IS A HANDLE TOO. `WaitAny` on it is what makes a cancel wake a parked await instead of
            // leaving it to the awaited call's own duration; `CanBeCanceled` keeps a token-less run from creating a
            // wait handle it could never observe.
            var cancelHandle = Interpreter.ActiveRunCancellation is { CanBeCanceled: true } runToken
                ? runToken.WaitHandle
                : null;
            if (cancelHandle is not null) handles.Add(cancelHandle);
            var waitSet = handles.ToArray();
            while (!completion.IsCompleted)
            {
                _pumpDriver?.Invoke();
                PumpPending();
                if (completion.IsCompleted) break;
                if (Interpreter.RunCancellationRequested)
                    throw new ScriptException(ErrorCode.Cancelled, "Stopped: the run was cancelled", line, column);
                // NO FIXED CEILING. The wait ends for exactly four reasons, and each one is a fact rather than a
                // poll: the completion signals, the run queue gains script work, the pump gains work, or the pump's
                // NEXT DUE moment arrives. That last one is not a safety net: a `sleep` in a script body is due by
                // the CLOCK, so no handle can ever signal for it: the due time IS its wake source (the 50 ms cap
                // this replaces was padding for it, and it cost 50 wake-ups per idle second). With nothing due the
                // wait is unbounded, which is sound because no other way for a script to make progress exists.
                var due = _pumpNextDue?.Invoke() ?? -1;
                WaitHandle.WaitAny(waitSet, due < 0 ? Timeout.Infinite : due);
            }
            if (tracing)
                Logging.Log.Info("async", $"parked {site} for {Environment.TickCount64 - started} ms: "
                    + "the run waits on a thread here instead of suspending (Diagnostics.AsyncParks() counts these)");
        }
        // A fault takes the same road out as .Result: the task's own exception, at this call.
        if (completion.IsFaulted || completion.IsCanceled)
            completion.GetAwaiter().GetResult();
    }

    // ── ContinueWith (marshals the script callback to the run thread) ──
    internal void ContinueWithCallback(Action action)
        => _ = _completion.Task.ContinueWith(_ => PostToRunQueue(action),
            global::System.Threading.Tasks.TaskScheduler.Default);

    /// <summary>Runs the callback on the RUN thread, as this member always has: a script continuation is script code,
    /// so it must be observed at a statement boundary (the run queue is drained there, and <see cref="Wait"/> drains
    /// it too). Measured: running it directly on the completing pool thread made `crash_task`'s "ContinueWith fires"
    /// check read the flag before the callback had run, because the two are separate statements with a boundary
    /// between them.</summary>
    public Task ContinueWith(Func<Task, object?> callback)
    {
        var t = new Task();
        // A CONTINUATION IS SCRIPT WORK FOR THE SAME RUN, so a WAIT on it must be able to drive the body it waits
        // for, which is the rule WhenAll and WhenAny already follow. Measured without it: `AsyncFn().ContinueWith(..)
        // .Wait()` parked for ever, because the awaited body needs the coroutine pump to resume and the continuation
        // task carried no driver, so its wait had nothing to drive (it could only see the run queue, never the due
        // clock the body was suspended on).
        t._pumpDriver ??= _pumpDriver;
        t._pumpWork ??= _pumpWork;
        t._pumpNextDue ??= _pumpNextDue;
        ContinueWithCallback(() =>
        {
            try { t.SetResult(callback(this)); }
            catch (Exception e) { t.SetException(e); }
        });
        return t;
    }

    // ── Static helpers ──
    internal static ConcurrentQueue<Action> MainThreadQueue = new();

    internal static void ClearMainQueue() { while (MainThreadQueue.TryDequeue(out _)) { } }

    /// <summary>Hands a script continuation to the RUN thread AND raises the run queue's signal. The signal is not
    /// bookkeeping: a `ContinueWith(..).Wait()` parks on the task it is waiting for, and the continuation it needs
    /// can only arrive through this queue: measured, without the signal such a wait parked for ever (the fixed
    /// 20 ms wake-up that used to hide it is gone).</summary>
    internal static void PostToRunQueue(Action action)
    {
        MainThreadQueue.Enqueue(action);
        TaskPump.SignalReady();
    }

    /// <summary>Runs queued script continuations (ContinueWith marshalling) on the CALLING thread. The run thread
    /// calls this at its statement boundaries, and <see cref="Wait"/> calls it while it waits.</summary>
    internal static void PumpPending()
    {
        while (MainThreadQueue.TryDequeue(out var action))
            action();
        // The run queue is drained here too: a composite task settling posts there, and every boundary that
        // used to drain only the marshalling queue now services both in one pass.
        TaskPump.PumpPending();
    }

    public static Task CompletedTask
    {
        get
        {
            var t = new Task();
            t.SetResult();
            return t;
        }
    }

    public static Task FromException(Exception exception)
    {
        var t = new Task();
        t.SetException(exception);
        return t;
    }

    // ── Run ──
    public static Task Run(Action action) => Run(() => { action(); return null; });

    /// <summary>Starts the work on the thread pool and RETURNS IMMEDIATELY: a script can keep going while the body
    /// runs, which is what <c>Task.Run</c> means in C#. It replaces a call into the old single pump thread whose
    /// enqueue BLOCKED the caller until the item had run.</summary>
    public static Task Run(Func<object?> action)
    {
        var t = new Task();
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                var result = action();
                if (result is Task inner)
                    SettleFromInner(t, inner);
                else
                    t.SetResult(result);
            }
            catch (Exception e)
            {
                t.SetException(e);
            }
        }, null);
        return t;
    }

    /// <summary>Completes <paramref name="outer"/> from <paramref name="inner"/>, carrying its value or its fault,
    /// which is what makes the task a work function returned look like C#'s unwrapped one.</summary>
    private static void SettleFromInner(Task outer, Task inner)
        => _ = inner._completion.Task.ContinueWith(done =>
        {
            if (done.IsCanceled) { outer.SetException(CanceledRefusal()); return; }
            if (done.IsFaulted)
            {
                var fault = done.Exception is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
                    ? aggregate.InnerExceptions[0]
                    : done.Exception;
                outer.SetException(fault!);
                return;
            }
            outer.SetResult(done.Result);
        }, global::System.Threading.Tasks.TaskScheduler.Default);

    /// <summary>The exception a collection of failures becomes: the failure ITSELF when there is one, so a
    /// single-fault task keeps the behaviour it always had, and an <see cref="AggregateException"/> when there are
    /// several, so a `Task.WhenAll` of failures names every one of them instead of completing as a success.</summary>
    internal static Exception AggregateOrSingle(List<Exception> faults)
        => faults.Count == 1 ? faults[0] : new AggregateException(faults);

    // ── Yield (C#'s Task.Yield, on this engine's scheduler) ──
    /// <summary>C#'s <c>Task.Yield()</c>: an awaitable that is INCOMPLETE when it is read and completes on a later
    /// pass of the scheduler, never inline, so <c>await Task.Yield()</c> hands control back before the body
    /// continues. C# posts the continuation to the current <c>SynchronizationContext</c> (or the pool); this engine
    /// has no context, its scheduler is the RUN QUEUE, so the completion is posted there
    /// (<see cref="PostToRunQueue"/>, which also raises the queue's signal, so a parked wait wakes for it). The
    /// effect a script can see is C#'s: everything already queued for the run thread is served before this body
    /// resumes, and a loop that yields keeps the run responsive instead of holding it statement to statement.
    ///
    /// IT IS REGISTERED THROUGH <c>Namespace.AddMethodAsync</c>, the door for a member that ANSWERS a Task: the
    /// await is the caller's, and this member returns at once. The value it completes with is null, which is the
    /// same nothing C#'s <c>YieldAwaitable</c> answers, so <c>var v = await Task.Yield();</c> reads null here while
    /// C# refuses the shape at compile time (its GetResult is void).</summary>
    public static Task Yield()
    {
        var t = new Task();
        PostToRunQueue(() => t.SetResult());
        return t;
    }

    // ── Delay (native, no interpreter required) ──
    public static Task Delay(int milliseconds) => DelayAsync(milliseconds);

    /// <summary>A NATIVE delay: <see cref="ClrTask.Delay(int)"/> awaited by an `async` engine method, so the
    /// suspension is lowered by the runtime (.NET 11's runtime-async) and the timer belongs to the platform. It
    /// replaced a pump-completed entry that could not exist without a running script ("Task.DelayAsync needs a
    /// running script") and that poll-armed a timer to beat the system tick. The token FAULTS the task, as an
    /// `await` on it then sees.</summary>
    public static Task DelayAsync(int milliseconds, CancellationToken token = default)
    {
        var t = new Task();
        if (token.IsCancellationRequested) { t.SetException(CanceledRefusal()); return t; }
        if (milliseconds < 0) { t.SetException(RefuseNegativeDelay(milliseconds)); return t; }
        if (milliseconds == 0) { t.SetResult(0); return t; }
        _ = DelayCoreAsync(t, milliseconds, token);
        return t;
    }

    private static async ClrTask DelayCoreAsync(Task completed, int milliseconds, CancellationToken token)
    {
        try
        {
            await ClrTask.Delay(milliseconds, token).ConfigureAwait(false);
            completed.SetResult(milliseconds);
        }
        catch (OperationCanceledException)
        {
            completed.SetException(CanceledRefusal());
        }
        catch (Exception ex)
        {
            completed.SetException(ex);
        }
    }

    /// <summary>The fault a negative duration takes. In C# a negative delay means "wait for ever", and completing
    /// instantly instead would be silent, so the refusal names the engine's endless wait.</summary>
    private static ScriptException RefuseNegativeDelay(int milliseconds)
        => new($"Task.DelayAsync takes a duration of zero or more milliseconds, and {milliseconds} is not 'wait for ever' here: Coroutine.Sleep is the engine's endless wait.");

    /// <summary>A task that is already cancelled, which is what a pre-cancelled token produces.</summary>
    public static Task CanceledTask(CancellationToken token)
    {
        _ = token;
        return FromException(CanceledRefusal());
    }

    /// <summary>The one refusal a cancellation raises, so every cancelled operation reads the same way and no CLR
    /// resource string reaches a script.</summary>
    internal static ScriptException CanceledRefusal()
        => new("the awaited operation was cancelled before it completed");

    // ── WhenAll / WhenAny (native composites) ──
    public static Task WhenAll(List<Task> tasks)
    {
        var t = new Task();
        if (tasks.Count == 0) { t.SetResult(); return t; }
        var natives = new ClrTask[tasks.Count];
        for (var i = 0; i < tasks.Count; i++) natives[i] = tasks[i]._completion.Task;
        t._pumpDriver ??= tasks[0].PumpDriver;
        t._pumpWork ??= tasks[0].PumpWork;
        _ = SettleWhenAllAsync(t, natives);
        return t;
    }

    private static async ClrTask SettleWhenAllAsync(Task composite, ClrTask[] tasks)
    {
        var faults = new List<Exception>();
        foreach (var task in tasks)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception ex)
            {
                faults.Add(ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1
                    ? aggregate.InnerExceptions[0]
                    : ex);
            }
        }
        if (faults.Count > 0) composite.SetException(AggregateOrSingle(faults));
        else composite.SetResult();
    }

    /// <summary>C#'s <c>Task.WhenAny</c>: the composite completes with the ENGINE Task that finished first, so a
    /// script reads <c>Task.WhenAny(list).Result</c> and gets an awaitable task. C# returns
    /// <c>Task&lt;Task&gt;</c>; this engine's Task is untyped, so the winner itself is the value.</summary>
    public static Task WhenAny(List<Task> tasks)
    {
        var t = new Task();
        if (tasks.Count == 0)
        {
            t.SetException(new ScriptException("Task.WhenAny expects at least one Task."));
            return t;
        }
        var natives = new ClrTask[tasks.Count];
        for (var i = 0; i < tasks.Count; i++) natives[i] = tasks[i]._completion.Task;
        t._pumpDriver ??= tasks[0].PumpDriver;
        t._pumpWork ??= tasks[0].PumpWork;
        _ = SettleWhenAnyAsync(t, tasks, natives);
        return t;
    }

    private static async ClrTask SettleWhenAnyAsync(Task composite, List<Task> sources, ClrTask[] natives)
    {
        var winner = await ClrTask.WhenAny(natives).ConfigureAwait(false);
        var index = Array.IndexOf(natives, winner);
        composite.SetResult(index >= 0 ? sources[index] : null);
    }
}
