namespace ScriptLangCore;

/// <summary>How a subject hands a value it is given to the subscribers. See <see cref="EventSubject"/>.
///
/// The two modes exist because the engine has two audiences: a value published from SCRIPT code is expected to have
/// reached its subscriber by the next line (C# and Rx both deliver synchronously), while a value published from a
/// SOCKET thread, a `Process.Start` output callback or a COM event must reach a subscriber that writes a CONTROL, and
/// this repository records that a control write from such a thread does not land (a ListBox reported 0 rows). Choosing
/// the mode at creation is the whole point: neither default is right for both.</summary>
internal enum SubjectDelivery
{
    /// <summary>Called on the PUBLISHING thread, before `Next` returns. The default.</summary>
    Inline,

    /// <summary>Queued, and delivered on the thread the RUN began on (<see cref="Interpreter.RunThreadId"/>), inside
    /// the engine's own pump. The engine already drains ONE main-thread queue (<see cref="Task.MainThreadQueue"/>) at
    /// every statement (the VM's `LOC` arm and the tree's per-statement loop), inside `Application.Run`'s message
    /// loop, inside `Task.Wait` and inside `sleep`, so this mode needs no second pump: a delivery is one entry in that
    /// queue, coalesced to one per batch, and a publisher on another thread is woken by a single `WM_NULL` post to the
    /// run thread so an IDLE window still delivers.</summary>
    RunThread,
}

/// <summary>What a subject does when a subscriber's queue is full, which is the backpressure question a push API has
/// to answer rather than hide. `Block` never waits for ever: after <see cref="SubjectCore.BlockCeilingMs"/> it degrades
/// to <see cref="DropOldest"/> and reports, because a publisher that dies waiting for a consumer is a worse outcome
/// than a dropped value (and a publisher ON the run thread never waits at all, since the consumer it would be waiting
/// for is itself).</summary>
internal enum SubjectOverflow
{
    DropOldest,
    DropNewest,
    Block,
}

/// <summary>The state behind a <see cref="EventSubject"/>, shared by the publishing object and its read-only view so
/// `subject.ReadOnly` needs no second set of observers and no second queue. It is a separate class rather than fields
/// on <see cref="EventSubject"/> for exactly one reason: the script-facing members are resolved by REFLECTION, so
/// anything internal would be reachable by name if it lived on the public type. Nothing here is script-visible.
///
/// THREE DESIGN DECISIONS, each from a measurement recorded in AGENTS.md rather than from taste:
///  1. ONE non-generic type with `object?` payloads, not `Subject&lt;T&gt;`. A generic engine type multiplies the AOT
///     image per instantiation (the recorded lesson: `ScriptUnboxedDictionary&lt;,&gt;` measured 1.27 MB over 13,473
///     nodes, about 75 kB per instantiation, and specialising 8 value types instead of 17 removed 3.1 MB), and a script
///     value is boxed anyway, so the generic would buy nothing a script can see.
///  2. NO `ImmutableArray`. `src` references `System.Collections.Immutable` ZERO times today, and a copy-on-write
///     `EventSubscriber[]` swapped under a lock gives the same snapshot iteration with no allocation per publish.
///  3. A SUBSCRIBER'S FAILURE IS OURS TO CONTAIN. The textbook subject lets an observer's exception reach whoever
///     published; here that is the worst possible default, because a publisher can be a WINDOW PROCEDURE or a CLR
///     callback, where an escaping exception is process death (the whole `StaticWndProc` is wrapped for that reason).
///     So each subscriber is invoked in its own try/catch, the failure is reported through the engine's ONE message
///     map, and the subscriber is REMOVED so one bug cannot poison every later publish. Repeats are said once.</summary>
internal sealed class SubjectCore
{
    internal const int DefaultCapacity = 1024;

    /// <summary>How many deliveries one pass may make before it re-queues itself. A pass that keeps going while a
    /// producer outruns it would starve the statement boundary it was called from (the message loop, the VM's `LOC`).</summary>
    private const int MaxDeliveriesPerPass = 512;

    /// <summary>How deep an INLINE publish may nest before it defers. A subscriber that publishes to the same subject
    /// from inside its own callback is legitimate (that is a pipeline), but an unbounded version is a stack the script
    /// can overflow, and "the shipped binary must never be killed by script input" applies to a recursive publish as
    /// much as to a runaway recursion. Past the cap the value is queued and one kick is posted, so the depth stops
    /// growing and the delivery order is preserved.</summary>
    private const int MaxInlineDepth = 8;

    internal const int BlockCeilingMs = 5000;

    /// <summary>How long a delivery that found the run thread's stack too deep waits before trying again (see
    /// <see cref="ArmRetry"/>): short enough that a feed still looks live, long enough that the retry lands after the
    /// statement which caused it.</summary>
    private const int RetryDelayMs = 5;

    /// <summary>The inline nesting depth of THIS thread. A static per thread rather than a field on the subject,
    /// because the recursion that matters chains through several subjects (A's subscriber publishes to B whose
    /// subscriber publishes to A) and each subject would see depth 1.</summary>
    [ThreadStatic] private static int _inlineDepth;

    private readonly object _gate = new();
    private EventSubscriber[] _subs = [];
    private readonly SubjectDelivery _delivery;
    private readonly int _capacity;
    private readonly SubjectOverflow _overflow;
    private bool _closed;
    private string? _endReason;
    private int _dropped;
    private int _kick;
    private int _retryArmed;
    private int _nextId;
    private string? _lastReport;

    internal SubjectCore(SubjectDelivery delivery, int capacity, SubjectOverflow overflow)
    {
        _delivery = delivery;
        _capacity = capacity;
        _overflow = overflow;
    }

    internal SubjectDelivery Delivery => _delivery;
    internal int Capacity => _capacity;
    internal SubjectOverflow Overflow => _overflow;
    internal bool Closed { get { lock (_gate) return _closed; } }
    internal int Dropped => Volatile.Read(ref _dropped);
    internal int SubscriberCount { get { lock (_gate) return _subs.Length; } }

    /// <summary>Values waiting to be delivered to a PUSH subscriber. A pull subscriber (an `await foreach` enumerator)
    /// owns its own queue and is not counted, because its owner is the one who knows when it is ready.</summary>
    internal int Pending
    {
        get
        {
            EventSubscriber[] subs;
            lock (_gate) subs = _subs;
            var total = 0;
            foreach (var sub in subs) if (!sub.Pull) total += sub.Pending;
            return total;
        }
    }

    private static bool OnRunThread =>
        Interpreter.RunThreadId != 0 && Environment.CurrentManagedThreadId == Interpreter.RunThreadId;

    internal void NoteDropped() => Interlocked.Increment(ref _dropped);

    // ── publishing ──────────────────────────────────────────────────────────

    /// <summary>Broadcasts one value. A publish to a closed subject is IGNORED rather than refused, which is what Rx
    /// does and what a producer thread that is a moment behind its consumer needs: refusing would turn an ordinary
    /// shutdown race into a script error on somebody else's thread.</summary>
    internal void Publish(object? value)
    {
        EventSubscriber[] subs;
        lock (_gate)
        {
            if (_closed) return;
            subs = _subs;
        }
        if (subs.Length == 0) return;

        var queued = false;
        var canBlock = !OnRunThread;   // waiting for a consumer that is this very thread would deadlock
        foreach (var sub in subs) if (sub.Enqueue(value, canBlock)) queued = true;
        if (!queued) return;

        if (_delivery == SubjectDelivery.Inline || OnRunThread) DeliverNow();
        else Kick();
    }

    /// <summary>Posts one delivery pass onto the engine's main-thread queue, COALESCED: a producer that publishes a
    /// thousand lines while the run thread is busy posts ONE entry, and the pass drains what has accumulated. That is
    /// what keeps a chatty source from growing the shared queue without bound.</summary>
    private void Kick()
    {
        if (Interlocked.Exchange(ref _kick, 1) != 0) return;
        var runThread = Interpreter.RunThreadId;
        Task.PostToRunQueue(DrainOnRunThread);
        // Off the run thread only, and with the NATIVE id: `PostThreadMessageW` rejects a managed one while
        // `GetLastError` stays 0, which reads as success (measured: with the managed id the post answered false and the
        // delivery came 169 ms late; with the native one, 9 ms).
        if (runThread != 0 && Environment.CurrentManagedThreadId != runThread)
            Interpreter.WakeRunThread(Interpreter.RunThreadNativeId);
    }

    /// <summary>One queued pass. It runs from `Task.PumpPending`, which is called by the message loop and by the VM's
    /// statement boundary, so it must never throw: an exception here would leave through a WINDOW PROCEDURE.
    ///
    /// TWO REFUSALS OF ITS OWN, both found by a flaking suite rather than by reading:
    ///  * THE RUN-THREAD CHECK. That queue is drained by WHOEVER calls `Task.PumpPending`, and the engine's pump
    ///    reaches it from a worker thread too (`Task.PumpCoroutines` runs on the body's thread, which for a
    ///    `ThreadPool.QueueUserWorkItem` body is a pool thread), so a pass offered there hands the work back instead of
    ///    running script code on 1 MB of stack.
    ///  * THE STACK ROOM. A delivery is a script body, and the drain point the VM offers is its `LOC` arm, which on
    ///    the JIT build sits inside `Vm.ExecuteCoreInner` (a few hundred KB of frame). On the 1.5 MB Debug apphost the
    ///    engine's own guard fires at about 20% used, i.e. exactly there, and a subscriber was refused with "Maximum
    ///    call depth exceeded (1)" and then removed. So the pass hands itself back while the stack is tight, and lands
    ///    at the top-level statement boundary or the message loop, where it is shallow.
    /// </summary>
    private void DrainOnRunThread()
    {
        if (Interpreter.RunThreadId != 0 && Environment.CurrentManagedThreadId != Interpreter.RunThreadId)
        {
            Interlocked.Exchange(ref _kick, 0);
            Kick();
            return;
        }
        if (Interlocked.Exchange(ref _kick, 0) == 0) return;
        if (Interpreter.ScriptEntryStackIsTight())
        {
            // NOT re-posted onto the same queue, and that is the whole point: `Task.PumpPending` drains it in a
            // `while (TryDequeue)` loop, so an action that re-posts itself spins there for ever and the run thread
            // never returns from the statement (measured: the suite hung between two sections, intermittently).
            // A one-shot retry lands the same pass a few milliseconds later, when the run thread is between
            // statements and the stack is shallow again.
            ArmRetry();
            return;
        }
        try { DeliverNow(); }
        catch (Exception ex) { Report($"delivery stopped: {Interpreter.FriendlyExceptionMessage(ex)}"); }
    }

    /// <summary>Arranges one more delivery attempt, off the pump's drain loop, and never twice at once: the tight
    /// stack lasts as long as one statement does, so a retry may itself find it tight and arm another.</summary>
    private void ArmRetry()
    {
        if (Interlocked.Exchange(ref _retryArmed, 1) != 0) return;
        var retry = new Timer(_ =>
        {
            Interlocked.Exchange(ref _retryArmed, 0);
            Kick();
        }, null, RetryDelayMs, Timeout.Infinite);
        // The timer owns itself until it fires, and a feed that ends first leaves one no-op callback, which is why it
        // is not kept in a field: nothing else ever needs to cancel it.
        GC.KeepAlive(retry);
    }

    private void DeliverNow()
    {
        if (_inlineDepth >= MaxInlineDepth) { Kick(); return; }
        _inlineDepth++;
        try
        {
            var budget = MaxDeliveriesPerPass;
            bool progressed;
            do
            {
                progressed = false;
                foreach (var sub in Volatile.Read(ref _subs))
                {
                    if (!sub.Pull)
                        while (budget > 0 && sub.TryDequeue(out var value))
                        {
                            Deliver(sub, value);
                            budget--;
                            progressed = true;
                        }
                    if (sub.TakeEnd(out var reason)) DeliverEnd(sub, reason);
                }
            }
            while (progressed && budget > 0);

            if (budget == 0) Kick();   // more arrived than one pass may deliver
        }
        finally { _inlineDepth--; }
    }

    private void Deliver(EventSubscriber sub, object? value)
    {
        if (sub.OnNext is not { } callback) return;
        var home = sub.Home;
        if (home is null || home.IsDisposed) { DropSubscriber(sub, "the run that subscribed it has ended"); return; }
        try { home.InvokeScriptCallback(callback, [value]); }
        catch (Exception ex) { DropSubscriber(sub, Interpreter.FriendlyExceptionMessage(ex)); }
    }

    private void DeliverEnd(EventSubscriber sub, object? reason)
    {
        if (sub.OnEnd is not { } callback) return;
        var home = sub.Home;
        if (home is null || home.IsDisposed) { DropSubscriber(sub, "the run that subscribed it has ended"); return; }
        try { home.InvokeScriptCallback(callback, [reason]); }
        catch (Exception ex) { Report($"a subscriber's end handler failed: {Interpreter.FriendlyExceptionMessage(ex)}"); }
    }

    /// <summary>Removes a subscriber AND detaches it, which is what makes the removal stick: a subscriber whose
    /// interpreter is gone, or whose callback threw, would otherwise be re-visited by every later publish.</summary>
    private void DropSubscriber(EventSubscriber sub, string why)
    {
        Unsubscribe(sub);
        Report($"subscriber #{sub.Id} was removed: {why}");
    }

    /// <summary>The engine's ONE message map, and said ONCE per distinct text: a broken subscriber on a 60 Hz source
    /// would otherwise bury the run in one line per value, the trap `TheTimer` already solved for a failing tick.</summary>
    private void Report(string message)
    {
        if (string.Equals(Interlocked.Exchange(ref _lastReport, message), message, StringComparison.Ordinal)) return;
        Logging.Log.Error("Events", message);
    }

    // ── subscribing ─────────────────────────────────────────────────────────

    internal EventSubscription AddSubscriber(object? onNext, object? onEnd, Interpreter? home)
    {
        var sub = new EventSubscriber(this, Interlocked.Increment(ref _nextId), onNext, onEnd, home,
            _capacity, _overflow, pull: false);
        string? lateEnd = null;
        lock (_gate)
        {
            if (_closed) lateEnd = _endReason ?? "";
            else _subs = [.. _subs, sub];
        }
        // A subscriber that arrives after the end is told ONCE, immediately, which is what the interface contract
        // promises; it happens outside the lock so the handler cannot run while the subject is held. Marking it ended
        // FIRST is what makes `subscription.Closed` answer true and its Dispose a no-op, which is the difference a
        // script checking its own handle would otherwise read as "still live".
        if (lateEnd is not null)
        {
            sub.MarkEnd(lateEnd, discard: true);
            DeliverEnd(sub, lateEnd);
        }
        return new EventSubscription(sub);
    }

    /// <summary>The pull side: an `await foreach` consumer is a subscriber with its own queue and no callback. It is
    /// registered like any other so a value published before the loop starts is not lost, and it is marked `Pull` so a
    /// delivery pass never consumes the items its own consumer is waiting for.</summary>
    internal EventSubjectEnumerator OpenEnumerator()
    {
        var sub = new EventSubscriber(this, Interlocked.Increment(ref _nextId), null, null, null,
            _capacity, _overflow, pull: true);
        lock (_gate)
        {
            if (!_closed) _subs = [.. _subs, sub];
            else sub.MarkEnd(_endReason ?? "", discard: true);
        }
        return new EventSubjectEnumerator(this, sub);
    }

    internal void Unsubscribe(EventSubscriber sub)
    {
        lock (_gate)
        {
            if (Array.IndexOf(_subs, sub) is var at && at >= 0)
            {
                var rest = new EventSubscriber[_subs.Length - 1];
                if (at > 0) Array.Copy(_subs, 0, rest, 0, at);
                if (at < _subs.Length - 1) Array.Copy(_subs, at + 1, rest, at, _subs.Length - at - 1);
                _subs = rest;
            }
        }
        sub.MarkEnd(null, discard: true);
    }

    // ── ending ──────────────────────────────────────────────────────────────

    /// <summary>Terminal state. `discard` is the difference between `Complete`/`Fail` and `Dispose`: a completed
    /// subject lets a subscriber finish what it already has and then tells it, while a disposed one drops the backlog
    /// and says nothing (which is what `Dispose` means everywhere else in this engine).</summary>
    internal void Close(string? reason, bool discard)
    {
        EventSubscriber[] subs;
        lock (_gate)
        {
            if (discard && _closed && _subs.Length == 0) return;
            _closed = true;
            if (reason is not null) _endReason = reason;
            subs = _subs;
        }
        foreach (var sub in subs) sub.MarkEnd(reason, discard);
        if (subs.Length == 0) return;
        if (_delivery == SubjectDelivery.Inline || OnRunThread) DeliverNow();
        else Kick();
    }
}

/// <summary>One subscriber's state: a bounded queue, the callable to invoke, and the run that owns that callable. A
/// plain `object` gate rather than a <see cref="Lock"/>, because `Block` needs `Monitor.Wait`, which `Lock` does not
/// support.</summary>
internal sealed class EventSubscriber
{
    private readonly object _gate = new();
    private readonly Queue<object?> _items = new();
    private readonly SubjectCore _owner;
    private readonly int _capacity;
    private readonly SubjectOverflow _overflow;
    private bool _closed;
    private bool _endTaken;
    private string? _endReason;
    private TaskCompletionSource<bool>? _waiter;

    internal EventSubscriber(SubjectCore owner, int id, object? onNext, object? onEnd, Interpreter? home,
        int capacity, SubjectOverflow overflow, bool pull)
    {
        _owner = owner;
        _capacity = capacity;
        _overflow = overflow;
        Id = id;
        OnNext = onNext;
        OnEnd = onEnd;
        Home = home;
        Pull = pull;
    }

    internal int Id { get; }

    /// <summary>The feed this subscriber belongs to. Read by the subscription handle at construction, so the handle
    /// needs one argument and one assignment rather than a second copy of the enumerator's constructor.</summary>
    internal SubjectCore Owner => _owner;
    internal object? OnNext { get; }
    internal object? OnEnd { get; }
    internal Interpreter? Home { get; }
    internal bool Pull { get; }
    internal int Pending { get { lock (_gate) return _items.Count; } }
    internal bool Finished { get { lock (_gate) return _closed && _items.Count == 0; } }

    internal bool Enqueue(object? value, bool canBlock)
    {
        lock (_gate)
        {
            if (_closed) return false;
            var waited = 0;
            while (_items.Count >= _capacity)
            {
                if (_overflow == SubjectOverflow.Block && canBlock && waited < SubjectCore.BlockCeilingMs)
                {
                    var before = Environment.TickCount64;
                    Monitor.Wait(_gate, SubjectCore.BlockCeilingMs - waited);
                    waited += (int)(Environment.TickCount64 - before);
                    if (_closed) return false;
                    continue;
                }
                if (_overflow == SubjectOverflow.DropNewest) { _owner.NoteDropped(); return false; }
                _items.Dequeue();
                _owner.NoteDropped();
            }
            _items.Enqueue(value);
        }
        Wake();
        return true;
    }

    internal bool TryDequeue(out object? value)
    {
        lock (_gate)
        {
            var taken = TryDequeueNoLock(out value);
            if (taken) Monitor.PulseAll(_gate);   // a blocked publisher may now have room
            return taken;
        }
    }

    // ── the pull side, which owns its gate (see GateForPull) ─────────────────
    // A pull consumer cannot hold the lock across an await, so it takes it in two short spans: one to look, one to
    // park. These three are the lock-free halves that make that possible.

    internal object GateForPull => _gate;

    internal bool TryDequeueNoLock(out object? value)
    {
        if (_items.Count > 0)
        {
            value = _items.Dequeue();
            Monitor.PulseAll(_gate);
            return true;
        }
        value = null;
        return false;
    }

    internal bool FinishedNoLock => _closed && _items.Count == 0;

    /// <summary>Hands out the terminal notification ONCE, and only after everything already queued has been
    /// delivered, so `onEnd` never overtakes a value the subscriber has not seen.</summary>
    internal bool TakeEnd(out object? reason)
    {
        lock (_gate)
        {
            if (!_closed || _endTaken || _items.Count > 0) { reason = null; return false; }
            _endTaken = true;
            reason = _endReason ?? "";
            return true;
        }
    }

    internal void MarkEnd(string? reason, bool discard)
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate)
        {
            _closed = true;
            _endReason = reason ?? _endReason;
            if (discard) { _items.Clear(); _endTaken = true; }
            Monitor.PulseAll(_gate);
            waiter = _waiter;
            _waiter = null;
        }
        waiter?.TrySetResult(true);
    }

    /// <summary>The pull side's park: ONE waiter per subscriber, because an enumerator has one reader. Created under
    /// the lock so a value published on another thread cannot be missed between the check and the wait.</summary>
    internal TaskCompletionSource<bool> ParkNoLock() =>
        _waiter ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake()
    {
        TaskCompletionSource<bool>? waiter;
        lock (_gate) { waiter = _waiter; _waiter = null; }
        waiter?.TrySetResult(true);
    }
}

/// <summary>What `Subscribe` answers, and the ONE way a script unsubscribes: `using var sub = feed.Subscribe(...)` or
/// `sub.Dispose()`. Idempotent, like every other handle in this engine.</summary>
public sealed class EventSubscription : IDisposable
{
    private SubjectCore? _core;
    private EventSubscriber? _sub;

    /// <summary>Built from the subscriber alone, which already knows its feed: a two-argument version would be an exact
    /// copy of <see cref="EventSubjectEnumerator"/>'s constructor body, and the duplicate-body checker is right to
    /// refuse that (these are two different things, not one collapsed thing).</summary>
    internal EventSubscription(EventSubscriber sub)
    {
        _sub = sub;
        _core = sub.Owner;
    }

    /// <summary>True once this subscription has been disposed, or the subject has ended it.</summary>
    public bool Closed => _sub is not { } sub || sub.Finished;

    public void Dispose()
    {
        var sub = Interlocked.Exchange(ref _sub, null);
        var core = Interlocked.Exchange(ref _core, null);
        if (core is not null && sub is not null) core.Unsubscribe(sub);
        GC.SuppressFinalize(this);
    }
}

/// <summary>The pull side of a subject: what `await foreach (var v in feed)` drives. It is an ordinary
/// `IAsyncEnumerable&lt;object?&gt;` implementation, so the engine's existing await-foreach drive finds it by REFLECTION
/// (`GetInterfaces`), which is why this type's interface list and its three members are kept live by
/// `AotHints`.</summary>
internal sealed class EventSubjectEnumerator : IAsyncEnumerator<object?>, IAsyncDisposable, IDisposable
{
    private readonly SubjectCore _core;
    private readonly EventSubscriber _sub;
    private object? _current;
    private bool _disposed;

    internal EventSubjectEnumerator(SubjectCore core, EventSubscriber sub)
    {
        _core = core;
        _sub = sub;
    }

    public object? Current => _current;

    public ValueTask<bool> MoveNextAsync()
    {
        TaskCompletionSource<bool> waiter;
        lock (_sub.GateForPull)
        {
            if (_sub.TryDequeueNoLock(out var ready)) { _current = ready; return new ValueTask<bool>(true); }
            if (_sub.FinishedNoLock) return new ValueTask<bool>(false);
            waiter = _sub.ParkNoLock();
        }
        return AwaitItemAsync(waiter);
    }

    private async ValueTask<bool> AwaitItemAsync(TaskCompletionSource<bool> waiter)
    {
        while (true)
        {
            // ConfigureAwait(false): the continuation runs where the producer woke it, never on a captured context,
            // because the engine's `await foreach` waits on the run thread while its own pump delivers.
            await waiter.Task.ConfigureAwait(false);
            lock (_sub.GateForPull)
            {
                if (_sub.TryDequeueNoLock(out var ready)) { _current = ready; return true; }
                if (_sub.FinishedNoLock) return false;
                waiter = _sub.ParkNoLock();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _core.Unsubscribe(_sub);
        GC.SuppressFinalize(this);
    }
}

/// <summary>The script-facing subject. See <see cref="SubjectCore"/> for the three decisions behind it and
/// `docs/language/index.html`'s `Events` entry for the spelling a script uses.
///
/// A READ-ONLY VIEW (`subject.ReadOnly`) is what lets a script hand a feed to somebody else without handing over the
/// right to publish on it: a script has no access modifiers, so the hiding has to be an object.</summary>
public sealed class EventSubject : IDisposable, IAsyncEnumerable<object?>
{
    private readonly SubjectCore _core;
    private readonly bool _readOnly;
    private readonly Interpreter? _home;

    internal EventSubject(SubjectCore core, bool readOnly = false, Interpreter? home = null)
    {
        _core = core;
        _readOnly = readOnly;
        _home = home;
    }

    // ── subscribing ─────────────────────────────────────────────────────────

    public EventSubscription Subscribe(object? onNext) => Subscribe(onNext, null);

    /// <summary>`onEnd` is called ONCE, with an empty string for a normal end and with the reason for a failure, which
    /// is the whole terminal contract: a script that needs to tell the two apart compares against "". A separate
    /// end object would be a second type a script has to know for two facts.
    ///
    /// A late subscriber (one that arrives after the end) is told immediately, once.</summary>
    public EventSubscription Subscribe(object? onNext, object? onEnd)
    {
        if (!Interpreter.IsScriptCallable(onNext))
            Interpreter.RuntimeError(
                $"EventSubject.Subscribe takes the function to call for each value, not '{GetFriendlyTypeNameOfValue(onNext)}'.",
                0, 0);
        if (onEnd is not null && !Interpreter.IsScriptCallable(onEnd))
            Interpreter.RuntimeError(
                $"EventSubject.Subscribe takes the function to call when the feed ends as its second argument, not '{GetFriendlyTypeNameOfValue(onEnd)}'.",
                0, 0);
        var home = Interpreter.Current;
        return _core.AddSubscriber(onNext, onEnd, home);
    }

    // ── publishing ──────────────────────────────────────────────────────────

    public void Next(object? value)
    {
        RefuseWrite(nameof(Next));
        _core.Publish(value);
    }

    public void Complete()
    {
        RefuseWrite(nameof(Complete));
        _core.Close(null, discard: false);
    }

    public void Fail(object? reason)
    {
        RefuseWrite(nameof(Fail));
        var text = Interpreter.ValueFormatter.ToDisplay(reason);
        _core.Close(text.Length == 0 ? "the feed failed" : text, discard: false);
    }

    // ── reading the state ───────────────────────────────────────────────────

    /// <summary>A view that can subscribe and cannot publish. Refuses `Next`, `Complete` and `Fail`; its `Dispose` is a
    /// no-op rather than a refusal, because a handle that throws at the end of a `using` block is a trap.</summary>
    public EventSubject ReadOnly => _readOnly ? this : new EventSubject(_core, readOnly: true, home: _home);

    /// <summary>Values queued for a subscriber that has not been given them yet. Zero with an inline subject whose
    /// subscriber is keeping up, and the number a script can watch to see a push API falling behind.</summary>
    public int Count => _core.Pending;

    public int Subscribers => _core.SubscriberCount;

    public bool Closed => _core.Closed;

    public int Dropped => _core.Dropped;

    public string Mode => _core.Delivery == SubjectDelivery.RunThread ? "runThread" : "inline";

    /// <summary>True for a view answered by <see cref="ReadOnly"/>, which is a fact a JSON snapshot carries because a
    /// reader cannot tell an owner from a view otherwise.</summary>
    internal bool ReadOnlyView => _readOnly;

    public int Capacity => _core.Capacity;

    public string Overflow => _core.Overflow switch
    {
        SubjectOverflow.DropNewest => "dropNewest",
        SubjectOverflow.Block => "block",
        _ => "dropOldest",
    };

    // ── producing ───────────────────────────────────────────────────────────

    public EventSubject Start(object? work) => Start(work, null);

    /// <summary>Runs `work` as the feed's producer on its own background thread, handing the feed to the body as its
    /// FIRST argument and the caller's `state` as the second when there is one. A body of no parameters is accepted too
    /// (it closes over the feed instead), so the three shapes are `fun () { }`, `fun (Subject feed) { }` and
    /// `fun (Subject feed, object state) { }`; anything wider is refused by name.
    ///
    /// WHAT THIS REPLACES, and each part is a measured defect of the spelling it replaces
    /// (`ThreadPool.QueueUserWorkItem(fun (object o) { ... })`):
    ///   * A SCRIPT ERROR IN THE BODY NO LONGER KILLS THE PROCESS. Measured on the pool spelling: an escaping
    ///     `ScriptException` ended the run with -532462766 and a stack trace, because .NET reports an unhandled
    ///     exception on a pool thread as fatal and no script can catch it. Here it is reported once through the engine's
    ///     log and turned into `Fail(reason)`, so every consumer learns WHY instead of waiting for an end that never
    ///     comes.
    ///   * THE FEED ENDS ITSELF: a normal return means `Complete()`, which is idempotent, so a body that already ended
    ///     the feed is unaffected, and a failure sends `Fail` instead (the only two terminal states this feed has).
    ///   * A DEDICATED THREAD WITH 32 MB OF STACK rather than a pool thread with 1 MB. The engine's depth guard measures
    ///     what is LEFT of the stack a script runs on, and inside a 1 MB budget it refused the first re-entry of a
    ///     bundle entry (the `Task.RunSta` measurement, which uses the same constant for the same reason).
    /// It returns the feed, so `Events.Create("runThread").Start(work)` chains.
    ///
    /// WHAT IT DOES NOT DO: it never aborts the body. A long-lived producer guards itself with `while (!feed.Closed)`,
    /// and a consumer that ends the feed (or a `Dispose`) stops it cooperatively at its next check. That is stated
    /// rather than hidden behind a `Stop()` that could not stop anything on .NET Core.</summary>
    /// <summary>
    /// `Start(work, state, options)`: the same producer with OPTIONS, and one option so far.
    ///
    /// `sta: true` runs the body in a SINGLE-THREADED APARTMENT. That is not a detail: a capture or a job that
    /// drives COM (`CreateObject`) refuses on an MTA thread ("COM Automation requires an STA thread"), which is
    /// why a tool that runs script bodies off its UI thread needs it. `Task.RunSta` is the engine's other door for
    /// the same thing, and it BLOCKS its caller; this one returns the feed at once and reports through the feed.
    ///
    /// The STATE may be null when only options are wanted, and the options dictionary is read at the call:
    /// `Start(work, null, { sta: true })`.
    /// </summary>
    public EventSubject Start(object? work, object? state, object? options)
    {
        RefuseWrite(nameof(Start));
        if (!Interpreter.IsScriptCallable(work))
            Interpreter.RuntimeError(
                $"EventSubject.Start takes the function to run as the feed's producer, not '{Interpreter.GetFriendlyTypeNameOfValue(work)}'.",
                0, 0);
        if (_core.Closed)
            Interpreter.RuntimeError(
                "EventSubject.Start cannot produce into a feed that has already ended: create the feed, or check Closed first.",
                0, 0);
        var arity = Interpreter.CallableArity(work);
        if (arity > MaxProducerParams)
            Interpreter.RuntimeError(
                $"EventSubject.Start takes a function of the feed alone, or of the feed and a state value; this one declares {arity} parameter(s).",
                0, 0);
        var home = _home ?? Interpreter.Current;
        if (home is null)
            Interpreter.RuntimeError("EventSubject.Start needs the interpreter the feed belongs to, and none is in scope.", 0, 0);

        var sta = false;
        if (options is not null)
        {
            // `SubjectOptionsOf` answers null when the argument is not an option carrier at all, and its own refusal
            // has already fired by then; an empty read here cannot hide a mistake.
            var opts = Interpreter.SubjectOptionsOf(options) ?? [];
            foreach (var (key, value) in opts)
            {
                if (key is not "sta")
                    Interpreter.RuntimeError($"EventSubject.Start takes one option, \"sta\" (a boolean), not \"{key}\".", 0, 0);
                if (value is not bool)
                    Interpreter.RuntimeError($"EventSubject.Start's \"sta\" option is a boolean (true or false), not '{Interpreter.GetFriendlyTypeNameOfValue(value)}'.", 0, 0);
                sta = value is bool flag && flag;
            }
        }

        var feed = this;
        var thread = new Thread(() => RunProducer(home, feed, work, state, arity), ProducerStackBytes)
        {
            IsBackground = true,
            Name = sta ? "event-producer-sta" : "event-producer",
        };
        if (sta && OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return this;
    }

    public EventSubject Start(object? work, object? state)
    {
        RefuseWrite(nameof(Start));
        if (!Interpreter.IsScriptCallable(work))
            Interpreter.RuntimeError(
                $"EventSubject.Start takes the function to run as the feed's producer, not '{Interpreter.GetFriendlyTypeNameOfValue(work)}'.",
                0, 0);
        if (_core.Closed)
            Interpreter.RuntimeError(
                "EventSubject.Start cannot produce into a feed that has already ended: create the feed, or check Closed first.",
                0, 0);
        var arity = Interpreter.CallableArity(work);
        if (arity > MaxProducerParams)
            Interpreter.RuntimeError(
                $"EventSubject.Start takes a function of the feed alone, or of the feed and a state value; this one declares {arity} parameter(s).",
                0, 0);
        var home = _home ?? Interpreter.Current;
        if (home is null)
            Interpreter.RuntimeError("EventSubject.Start needs the interpreter the feed belongs to, and none is in scope.", 0, 0);

        var feed = this;
        var thread = new Thread(() => RunProducer(home, feed, work, state, arity), ProducerStackBytes)
        {
            IsBackground = true,
            Name = "event-producer",
        };
        thread.Start();
        return this;
    }

    /// <summary>At most the feed plus a state value, which is the whole signature a producer body can have here.</summary>
    private const int MaxProducerParams = 2;

    /// <summary>The stack a producer body gets. `Task.RunSta` passes the same number for the same measured reason.</summary>
    private const int ProducerStackBytes = 32 * 1024 * 1024;

    /// <summary>The producer body's thread. Two jobs, and the second is why this is not the pool: a normal return ends
    /// the feed, and a failure is REPORTED and ends it with `Fail` rather than escaping, because an exception leaving a
    /// pool thread is fatal to the process and no script can catch it.</summary>
    private static void RunProducer(Interpreter? home, EventSubject feed, object? work, object? state, int arity)
    {
        var args = arity switch
        {
            0 => Array.Empty<object?>(),
            1 => new object?[] { feed },
            _ => new object?[] { feed, state },
        };
        try
        {
            home!.InvokeScriptCallback(work, args);
            feed._core.Close(null, discard: false);
        }
        catch (Exception ex)
        {
            // A script error carries its own message and position, which is the engine's own text; anything else goes
            // through the ONE map so no CLR sentence reaches a reader (the rule this repository applies everywhere).
            var why = ex is ScriptException script ? script.Message : Interpreter.FriendlyExceptionMessage(ex);
            if (why.Length == 0) why = "the producer failed";
            Logging.Log.Error("Events", $"a producer body failed, so the feed ends with it: {why}");
            feed._core.Close(why, discard: false);
        }
    }

    // ── the pull side ───────────────────────────────────────────────────────

    public IAsyncEnumerator<object?> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        _core.OpenEnumerator();

    // ── lifecycle ───────────────────────────────────────────────────────────

    /// <summary>Drops whatever is queued and ends the feed without a terminal notification, which is what `Dispose`
    /// means everywhere else here. Called by the engine when the run that created the subject ends, so a feed cannot
    /// outlive the interpreter its subscribers were built on.</summary>
    public void Dispose()
    {
        if (_readOnly) return;
        _core.Close(null, discard: true);
    }

    /// <summary>Closes a feed from the ENGINE's side, for a streaming answer that pulled from one. A read-only view's
    /// own `Dispose` is deliberately a no-op, so this is the door a transport has: a client that disconnects must stop
    /// the producer, which is what `while (!feed.Closed)` checks, or a disconnected browser leaves a thread producing
    /// for ever.</summary>
    internal void CloseForStream() => _core.Close(null, discard: true);

    private void RefuseWrite(string member)
    {
        if (_readOnly)
            Interpreter.RuntimeError(
                $"EventSubject.{member} is not available on a read-only view: subscribe to it, and publish through the object Events.Create answered.",
                0, 0);
    }
}
