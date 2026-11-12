using ScriptLangCore;
using ScriptLangCore.Logging;

namespace DispatchVtbl;

public sealed class ComScope : IDisposable
{
    private readonly Dictionary<IntPtr, ComObject> _owned;
    private readonly Stack<IntPtr> _order;
    private readonly Lock _lock;

    private ComScope() { _owned = []; _order = new(); _lock = new(); }

    public static ComScope Create()
    {
        _liveScopes++;
        return new ComScope();
    }

    /// <summary>Starts a FRESH instance, which is unambiguously this process's own work, nobody else could have
    /// started it, so it is recorded for the automatic release at the end of the run. The ROT path (GetObject) is
    /// the ATTACH path and is deliberately NOT recorded: that instance belongs to whoever started it.
    /// Recorded only AFTER the activation succeeded: a ProgID recorded for a creation that threw would name a
    /// server this process never started, and the release would then look that name up and end somebody's.</summary>
    public ComNode CreateObject(string progId)
    {
        var node = Track(ComAutomation.CreateObject(progId));
        RecordCreated(progId);
        return node;
    }

    public ComNode GetObject(string? pathName = null, string? progId = null)
        => Track(ComAutomation.GetObject(pathName, progId));

    /// <summary>ADOPTS AN INTERFACE THE HOST ALREADY HOLDS, which is the one door COM's own cache cannot provide when
    /// this engine runs INSIDE the application it wants to drive: from inside Excel, `GetObject("Excel.Application")`
    /// finds nothing (the ROT holds no entry for the instance that loaded us) and `CreateObject` answers a different
    /// instance. The host hands its own pointer over instead (`slc_adopt`, `Com.Host()`), and the reference is this
    /// scope's to release, exactly like a created or attached one.
    /// <para>
    /// The pointer is apartment-local, so it must come from `HostObject.Resolve` on THIS thread, never directly from
    /// the host's thread: measured, an unmarshalled pointer used across apartments stalls until the owner pumps.
    /// </para></summary>
    public ComNode Adopt(IntPtr dispatch)
        => Track(new ComObject(dispatch, addRef: false));

    public static void Run(Action<ComScope> action)
    {
        using var scope = Create();
        action(scope);
    }

    public static T Run<T>(Func<ComScope, T> func)
    {
        using var scope = Create();
        return func(scope);
    }

    public ComNode Track(ComObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var ptr = obj.NativePointer;
        if (ptr == IntPtr.Zero)
        {
            obj.Dispose();
            return ComNode.Create(obj, this);
        }

        lock (_lock)
        {
            if (_owned.TryGetValue(ptr, out var existing))
            {
                obj.Dispose();
                return ComNode.Create(existing, this);
            }
            _owned[ptr] = obj;
            _order.Push(ptr);
        }
        return ComNode.Create(obj, this);
    }

    /// <summary>SAFEARRAYs a property PUT built and handed to the server, to be destroyed once no scope is left:
    /// the server may still read them after the call returns (destroying one there SEGFAULTS a few iterations later),
    /// and being our heap nobody else can free them. Thread-static, and drained by whichever scope is the LAST one out
    /// (see _liveScopes), an inner scope still has outer scopes with live interfaces, so its Dispose is too early.
    /// The bound is therefore per Com.Run nesting: N puts inside one hold N arrays until it disposes.</summary>
    [ThreadStatic] private static List<IntPtr>? _deferredArrays;

    /// <summary>How many ComScopes are live on this thread. The apartment and the deferred arrays belong to the
    /// THREAD, so they are torn down by the LAST scope out, not by "the scope the thread is in", which is a test
    /// that has to be made idempotent (Com.Run's `using` disposes a scope a script may already have disposed) and
    /// then SKIPS the drain entirely: measured, with an idempotency guard added, an out-of-order disposal left
    /// created 1 / destroyed 0 and leaked the array silently. Counting is oblivious to the order.</summary>
    [ThreadStatic] private static int _liveScopes;

    private bool _disposed;

    /// <summary>ProgIDs of servers this PROCESS started, through Com.CreateObject, or through the create path of
    /// Com.GetOrCreate, never one it merely attached to. Thread-static and independent of any one scope, because
    /// ownership is "who STARTED it", which is also what makes ending it safe: an instance this process did not
    /// start belongs to its owner, and quitting it would destroy that owner's work.
    /// Held as NAMES, not interfaces: ending them re-resolves each through the ROT, so nothing here can go stale.
    /// Ordinal, because a ProgID is a registry key rather than an identifier the script spells (the same reason the
    /// dispid caches are Ordinal).</summary>
    [ThreadStatic] private static HashSet<string>? _createdProgIds;

    /// <summary>Records that this process started a server, so the run's end can REPORT it if nothing quit it.</summary>
    internal static void RecordCreated(string progId)
    {
        _createdProgIds ??= new HashSet<string>(StringComparer.Ordinal);
        _createdProgIds.Add(progId);
    }

    /// <summary>Forgets one server, or every one, once it has been REPORTED. An entry MUST NOT outlive that: the ROT
    /// is the only handle on "the current instance", so a spent name could otherwise be mistaken for an instance the
    /// USER starts later, which is why a name that no longer resolves is forgotten too.</summary>
    internal static void ForgetCreated(string? progId = null)
    {
        if (progId is null)
            _createdProgIds?.Clear();
        else
            _createdProgIds?.Remove(progId);
    }

    /// <summary>Queues a SAFEARRAY for destruction by the last scope out on this thread. See _deferredArrays.</summary>
    internal static void DeferArrayFree(IntPtr psa)
    {
        _deferredArrays ??= [];
        _deferredArrays.Add(psa);
    }

    /// <summary>How many servers this process started were STILL RUNNING when the run's last scope closed, and their
    /// names. Counted at the site for the same reason ComObject's counters are: a report nobody can assert is a
    /// report nobody can test, and this is the half of it a suite can check, because the stderr line is not
    /// script-visible. Process-wide, like every other counter here.</summary>
    internal static int StillRunning;

    private static readonly List<string> _stillRunningNames = [];
    private static readonly Lock _stillRunningLock = new();

    /// <summary>The ProgIDs reported as still running, in the order they were found, as ONE string a script can read
    /// and print (`"Excel.Application, Word.Application"`, empty when there is none). Not a CLR List: a script-visible
    /// value has to be a name a script can write, and `print(names)` on a raw List<string> shows
    /// `System.Collections.Generic.List\`1[System.String]`, measured, and exactly the rule this repo states.</summary>
    internal static string StillRunningNames
    {
        get { lock (_stillRunningLock) return string.Join(", ", _stillRunningNames); }
    }

    /// <summary>How long the report lets a server have after a QUIT before it says anything, and the poll step.
    /// MEASURED, and the reason this is a REPORT and not a verdict: a Quit() returns long before the server leaves
    /// the ROT, measured at ~250 ms in one case and still registered past 2 s in another, so a check at the run's
    /// end CANNOT tell "quit and shutting down" from "never quit". The grace is short on purpose, and the message is
    /// worded to say so; the alternative (waiting out a shutdown) would stall every run that created a server.</summary>
    private const int ReportGraceMs = 500;
    private const int ReportGracePollMs = 100;

    /// <summary>Is a server still running, RIGHT NOW? With no argument: is anything THIS PROCESS started (through
    /// Com.CreateObject, or GetOrCreate's create path) still resolvable in the ROT? With a ProgID: is anything of that
    /// class running, whoever started it, a legitimate yes that is simply not the script's to quit.
    /// This is the SCRIPT's side of the same lookup the report makes, and it exists so a script can WAIT instead of
    /// guessing: an out-of-process server does not leave the table while this run still holds its interface, so the
    /// wait belongs in a LATER Com.Run. It is also the honest way for a test to assert "gone" rather than racing a
    /// shutdown. The report itself no longer depends on this wait: it releases first and asks after.
    /// The lookup goes through this scope like every other COM call, so it is inside Com.Run() or it refuses.</summary>
    internal bool IsServerAlive(string? progId = null)
    {
        if (progId is not null)
            return Resolves(progId);

        if (_createdProgIds is not { Count: > 0 })
            return false;

        foreach (var id in _createdProgIds)
            if (Resolves(id))
                return true;
        return false;
    }

    /// <summary>Does the ROT still hand out that class? ONE lookup site, shared by the report and by Com.IsAlive, so
    /// the two can never disagree about what "running" means. It is a REFERENCE-FREE probe (ComAutomation.
    /// IsRegisteredInRot releases what it takes), which is the property that makes both work: resolving through
    /// GetObject wrapped the instance in a tracked node, so asking whether the server was gone kept it alive, and a
    /// script that had just called Quit() was still reported after a three second grace.</summary>
    private bool Resolves(string progId) => ComAutomation.IsRegisteredInRot(progId);

    /// <summary>Reports what the ROT still resolves for the CLASSES this process started, when the run's last scope
    /// The engine does NOT end them and does NOT decide whether they were ended: quitting an application is the
    /// SCRIPT's job, in VBA's own shape, `app.Quit()` inside the callback, the only place its handles are alive.
    /// What the engine does here is the one thing a script cannot do for itself: look up, after the fact, whether the
    /// application it started is still there, and say so. A forgotten `Quit()` is otherwise invisible, an
    /// out-of-process server outlives the process as a window nobody notices.
    /// Only names recorded at CREATION are checked: an instance this process merely ATTACHED to belongs to its owner
    /// and is none of our business (nothing records it). The record is cleared afterwards, so one name is never
    /// reported twice, and a spent name can never be mistaken for an instance the USER starts later.
    /// Runs at the LAST scope out, for the same reason the deferred arrays do: an inner Com.Run is not the end of the
    /// run, and its exit must not turn a live application into a report.
    /// Stderr carries the finding, with the engine's other observations ([coro:…], [message-quality]); the counters
    /// carry the testable half.</summary>
    private void ReportUnquitServers()
    {
        if (_createdProgIds is not { Count: > 0 })
            return;

        foreach (var progId in _createdProgIds)
        {
            // NO timestamp can settle this, so it polls for a short grace and then says its piece HEDGED: a rare
            // false positive on a correct script, instead of stalling every run that created a server until a
            // shutdown finishes, and instead of silencing real leaks entirely.
            // The grace is enough because the release above comes FIRST: with this scope's interfaces still live (the
            // order this used to run in) an out-of-process server CANNOT exit, so the lookup could only answer "still
            // running" and a script whose last statement was Quit() was reported anyway. Measured after the
            // relocation: the ROT stops resolving 115 ms after the release, so 500 ms has room and the warning is
            // left for the case it exists for.
            var deadline = Environment.TickCount64 + ReportGraceMs;
            while (true)
            {
                if (!Resolves(progId))
                    break;                            // a QUIT server, or an IN-PROCESS one, which has no ROT
                                                      // presence at all and dies with the last reference
                if (Environment.TickCount64 >= deadline)
                {
                    StillRunning++;
                    lock (_stillRunningLock)
                        _stillRunningNames.Add(progId);

                    // Through the engine's LOG channel, at Warning level, and SHORT: this is read in a suite's log
                    // among hundreds of lines, so the explanation lives in the docs instead. It names only what the
                    // lookup can show, the ROT matches by CLASS, so it cannot tell this process's instance from a
                    // stranger's, and a Quit() a moment ago leaves the server registered for seconds (both measured).
                    // The counter is the exact half.
                    // The position is the LAST statement the run executed, since this report is made after the
                    // run and belongs to no statement of its own: measured, it keeps the uniform `[Lx:Cy]
                    // [WARNING] ...` pattern the rest of the engine prints instead of arriving with none.
                    Log.Warning("Com", $"'{progId}' may still be running; Quit() it inside the callback "
                        + "(Com.Stats().StillRunning)",
                        Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);
                    break;
                }
                Thread.Sleep(ReportGracePollMs);
            }
        }
        ForgetCreated();
    }

    public void Dispose()
    {
        // Idempotent. Com.Run's `using` disposes the scope its callback received even when the script already
        // disposed it, and a second pass must not count the scope out twice and tear the thread down early.
        if (_disposed)
            return;
        _disposed = true;

        // Another scope is still live on this thread, so its interfaces, and the apartment they live in, are in
        // use: everything below waits for the last one out, whoever that turns out to be. Counting is oblivious to
        // the order the scopes are disposed in, which is why it is a count and not "the scope the thread is in".
        // (Guarded, so a scope disposed from another thread cannot drive the count negative.)
        var lastOut = false;
        if (_liveScopes > 0)
        {
            _liveScopes--;
            lastOut = _liveScopes == 0;
        }

        // LAST scope out: release this scope's interfaces FIRST, then REPORT the servers this process started that
        // nothing quit, both BEFORE ComInit.Shutdown, which ends the apartment they live in.
        // THE ORDER IS THE WHOLE THING, and it was the other way round: the report used to run while these
        // interfaces were still live, on the reasoning that the ROT lookup needed them. A live interface is exactly
        // what keeps an out-of-process server from exiting, so the lookup could only answer "still running" and a
        // script whose last statement was `app.Quit()` was reported anyway: measured, a 3 s grace did not clear it,
        // the runs took 4 s, and the Excel process only went away once the run had ended. With the release first, the
        // same probe answers "gone" 115 ms later, so the 500 ms grace is enough and the warning is left for the case
        // it exists for (a server nothing quit). Relocating the report also requires the REFERENCE-FREE lookup in
        // Resolves: the old one tracked a node into _owned, which would now re-populate a scope that has released.
        lock (_lock)
        {
            while (_order.TryPop(out var ptr))
                try { _owned[ptr].Dispose(); } catch { /* Ignore disposal errors */ }
            _owned.Clear();
        }

        if (!lastOut)
            return;

        ReportUnquitServers();

        // Every scope that put an array is done, so this is the moment the server is provably done with them.
        // After the releases above, and BEFORE ComInit.Shutdown, the destroy is an OLE call.
        if (_deferredArrays is { Count: > 0 })
        {
            foreach (var psa in _deferredArrays)
                ComObject.DestroyArray(psa);
            _deferredArrays.Clear();
        }

        ComInit.Shutdown();
    }

    /// <summary>A scope prints as the name a script can write, the rule every other script-facing wrapper
    /// already follows (ScriptTuple, DynamicLibrary, WeakRef, FileStreamReader all report ScriptNameOf(...)).
    /// Inheriting object.ToString showed "DispatchVtbl.ComScope", a name no script can write.</summary>
    public override string ToString() => ScriptNameOf(typeof(ComScope)) ?? "ComScope";
}

public sealed class ComNode : IDynamicPropertyProvider
{
    private readonly ComObject _obj;
    private readonly ComScope _scope;

    internal IntPtr NativePointer => _obj.NativePointer;

    /// <summary>Script-side: is THIS handle still usable? See ComObject.IsUsable, it answers without dispatching
    /// when the handle is out of service, which is what makes it safe to ask about a node held past its run (the
    /// documented dangling-handle hazard, where an in-process server would otherwise be freed memory).</summary>
    internal bool IsAlive() => _obj.IsUsable();

    public static ComNode Create(ComObject obj, ComScope scope) => new(obj, scope);

    private ComNode(ComObject obj, ComScope scope)
    {
        _obj = obj;
        _scope = scope;
    }

    /// <summary>A node prints as its script-facing type name, like every other wrapper here (see
    /// ComScope.ToString). Deliberately NOT the VBScript behaviour of printing the object's DEFAULT member:
    /// that would make printing a hidden COM invoke, re-entrancy, thread affinity and a possible error inside
    /// a print, and no other type in the language fetches its content to be printed.</summary>
    public override string ToString() => ScriptNameOf(typeof(ComNode)) ?? "ComNode";

    // -----------------------------------------------------------------
    // The script-facing accessors: every one of them DECODES
    // -----------------------------------------------------------------
    // Each accessor hands the script what the COM object actually returned, decoded by the VARIANT, a
    // dispatched object becomes a tracked node, a scalar comes back as itself. The alternative, asking for
    // <ComObject>, throws for every scalar: "Cannot convert VT_8 to ComObject". That is why `o.Get("Name")`
    // and `o.Invoke("Method", …)` failed while `o.Name` (the provider, which decodes) worked, and why
    // `Get<string>`/`Invoke<string>` LOOKED like a problem with the script's type argument.
    //
    // They are not: the language does not close a CLR generic method from the script's type argument
    // (`Array.Empty<int>()` invokes the OPEN definition and .NET refuses it), so `o.Get<string>(name)` resolves
    // to the non-generic overload below and lands here, which is deliberate now, and the generic accessors it
    // used to compete with are gone, having never been selectable. A chained call still works, because the
    // decoded value IS a ComNode whenever the object returned one: `o.Get("Drives").Count` and
    // `o.Invoke("GetFolder", …).Path` both walk on.

    /// <summary>Read a property, in VBA: x = obj.Property.</summary>
    public object? Get(string name) => AsScriptValue(_obj.GetPropertyValue(name));

    /// <summary>Read an indexed property, in VBA: x = obj.Cells(1,1).Value.</summary>
    public object? Get(string name, params object?[] indices)
        => AsScriptValue(_obj.GetPropertyIndexed<object>(name, indices));

    /// <summary>Read a property as a type named at RUNTIME, in VBA: x = obj.Property with GetType().</summary>
    public object? Get(Type type, string name) => ConvertTo(type, _obj.GetPropertyValue(name));

    /// <summary>Read an indexed property as a type named at runtime.</summary>
    public object? Get(Type type, string name, params object?[] indices)
        => ConvertTo(type, _obj.GetPropertyIndexed<object>(name, indices));

    /// <summary>Set a property, in VBA: obj.Visible = True. Returns <c>this</c> for chaining.</summary>
    public ComNode Set(string name, object value)
    {
        _obj.SetProperty(name, value);
        return this;
    }

    /// <summary>Set an indexed property, in VBA: obj.Cells(1,1).Value = "Hello". Returns <c>this</c> for chaining.</summary>
    public ComNode Set(string name, object value, params object[] indices)
    {
        _obj.SetPropertyIndexed(name, value, indices);
        return this;
    }

    /// <summary>
    /// Invoke any method: a COM object result is tracked, a SCALAR is returned as its own value, and a void
    /// result returns <c>this</c> so the chain continues.
    /// VBA: Set wb = Workbooks.Open  or  Call obj.Save
    /// </summary>
    public object? Invoke(string name, params object?[] args)
        => AsScriptValue(_obj.InvokeMethodValue(name, args)) ?? this;

    /// <summary>The conversion the runtime-typed getters share, so they cannot drift: nothing to do when the
    /// value is null or already the asked-for type, otherwise the CLR's own conversion (and its error).</summary>
    private static object? ConvertTo(Type type, object? value)
        => value is null || value.GetType() == type ? value : Convert.ChangeType(value, type);

    /// <summary>Enumerate a COM collection with a callback, in VBA: For Each item In collection</summary>
    public ComNode ForEach(Action<ComNode> action)
    {
        // A plain loop over the same body ComObject.ForEach walks. The callback form allocated a delegate AND a
        // closure per enumeration, and the semantics are identical: items arrive AddRef'd, the caller takes
        // ownership, so each is Track()ed here exactly as the lambda did.
        foreach (var item in _obj.EnumerateCollection())
            action(_scope.Track(item));
        return this;
    }

    /// <summary>Enumerate a COM collection lazily with Func � returns IEnumerable of extracted values</summary>
    public IEnumerable<T> ForEach<T>(Func<ComNode, T> func)
    {
        foreach (var item in _obj.EnumerateCollection())
        {
            // item is tracked by the scope � do NOT dispose it here
            yield return func(_scope.Track(item));
        }
    }

    /// <summary>
    /// Enumerate an ADODB Recordset row by row � VBA: Do While Not rs.EOF ... rs.MoveNext
    /// Yields the Recordset node at each cursor position. Read fields inside the loop.
    /// </summary>
    public IEnumerable<ComNode> ForEachRow(int maxRows = 0)
    {
        var count = 0;
        while (!_obj.GetProperty<bool>("EOF"))
        {
            if (maxRows > 0 && count++ >= maxRows)
                yield break;
            yield return this;
            _obj.InvokeMethod("MoveNext");
        }
    }

    /// <summary>Enumerate a COM collection as IEnumerable � use with foreach / LINQ</summary>
    public IEnumerable<ComNode> AsEnumerable()
    {
        foreach (var item in _obj.EnumerateCollection())
        {
            // item is tracked by the scope � do NOT dispose it here
            yield return _scope.Track(item);
        }
    }

    /// <summary>Write a 2D array to a Range value, in VBA: Range.Value = data</summary>
    public ComNode SetRangeValues(Array? data)
    {
        if (data == null) { _obj.SetRangeValues(null); return this; }
        var rows = new List<IReadOnlyList<object?>>(data.Length);
        for (var i = 0; i < data.Length; i++)
        {
            var row = data.GetValue(i) as Array ?? throw new ArgumentException("SetRangeValues expects a 2D jagged array");
            var cells = new List<object?>(row.Length);
            for (var j = 0; j < row.Length; j++) cells.Add(row.GetValue(j));
            rows.Add(cells);
        }
        _obj.SetRangeValues(rows);
        return this;
    }

    public ComNode SetRangeValues(IReadOnlyList<IReadOnlyList<object?>>? data)
    {
        _obj.SetRangeValues(data);
        return this;
    }

    /// <summary>Read a Range value row by row, in VBA: data = Range.Value2</summary>
    public IEnumerable<IEnumerable<object?>>? GetRangeValues()
        => _obj.GetRangeValues();

    // ── IDynamicPropertyProvider ──

    // Native ComNode C# methods that should NOT be dispatched to COM. A shared, case-insensitive INDEX: like the
    // engine's static name sets, it cannot read a per-interpreter flag, so membership must NOT be the rule, the
    // rule belongs at the lookup, IsWrapperMember below (AGENTS: "names follow the interpreter's case flag", and a
    // hardcode reads as a bug).
    private static readonly HashSet<string> ComNodeMethods =
    [
        with(StringComparer.OrdinalIgnoreCase),
        nameof(Get), nameof(Set), nameof(Invoke), nameof(ForEach),
        nameof(ForEachRow), nameof(AsEnumerable),
        nameof(SetRangeValues), nameof(GetRangeValues)
    ];

    /// <summary>True when `name` is one of the wrapper's OWN members, decided under the INTERPRETER's case flag:
    /// the wrapper's own members win over a COM member, so which names they are has to follow the same rule as
    /// every other name lookup. In `.lax` any case names them; in the default C#-strict mode only the spelling the
    /// wrapper is compiled with does, and that matters for a COM object that exposes a member differing from one
    /// of these only by case, which the strict mode must hand to COM.</summary>
    private static bool IsWrapperMember(string name)
        => ComNodeMethods.Contains(name)
           && (Current is not { CaseSensitive: true } || ComNodeMethods.Contains(name, StringComparer.Ordinal));

    bool IDynamicPropertyProvider.TryGetProperty(string name, out object? value)
    {
        // A name this object does not expose is NOT ours to answer. Claiming every name made a typo read the
        // object's DEFAULT member (the DISPID lookup failed silently to 0), and it also pre-empted the engine's
        // extension scan, which runs BELOW this arm, which is why an `extension(ComNode)` instance member
        // could never be reached. Declining hands the name to the engine's own member-miss report.
        if (_obj.FindDispId(name) is null)
        {
            value = null;
            return false;
        }
        // ONE round-trip, decoded from the variant the object returned. The old shape asked for <ComObject>
        // and re-read as <object> on InvalidCastException: two invocations of a getter that need not be
        // idempotent, to work around having asked for the wrong type.
        value = AsScriptValue(_obj.GetPropertyValue(name));
        return true;
    }

    bool IDynamicPropertyProvider.TrySetProperty(string name, object? value)
    {
        // Same rule: a name the object does not expose is not written through dispId 0, i.e. the object's
        // default property, with no error at all.
        if (_obj.FindDispId(name) is null) return false;
        _obj.SetProperty(name, value!);
        return true;
    }

    /// <summary>A COM result as a script value: a dispatched object becomes a tracked node; a scalar (string,
    /// number, bool, date) is already a script value and passes straight through.</summary>
    private object? AsScriptValue(object? value)
        => value is ComObject raw ? _scope.Track(raw) : value;

    /// <summary>Argument arrays for the callback form of ForEach, one per NESTING DEPTH. The argument is a
    /// single boxed node, so one array per ITEM was pure garbage on a hot path, but one shared array would be
    /// clobbered the moment a callback enumerated another collection, which is a real script shape. Indexing by
    /// depth keeps the reuse safe (a nest takes its own slot) and needs no lock: COM Automation here runs on an
    /// STA thread, which ComInit refuses to proceed without, so a scope is single-threaded by construction.
    /// [ThreadStatic] on top of that makes the state per-thread even if that ever changes.</summary>
    [ThreadStatic] private static object?[][]? _forEachArgsByDepth;
    [ThreadStatic] private static int _forEachDepth;

    private static object?[] ForEachArgs()
    {
        var slots = _forEachArgsByDepth;
        if (slots is null || _forEachDepth >= slots.Length)
        {
            var want = Math.Max((slots?.Length ?? 0) * 2, _forEachDepth + 1);
            Array.Resize(ref slots, want);
            _forEachArgsByDepth = slots;
        }
        return slots[_forEachDepth] ??= new object?[1];
    }

    bool IDynamicPropertyProvider.TryInvokeNamedMethod(string name, object?[] args,
        IReadOnlyList<string?>? argNames, out object? result)
    {
        if (argNames is null || !HasAnyName(argNames))
            return ((IDynamicPropertyProvider)this).TryInvokeMethod(name, args, out result);

        // A name the caller wrote is a PARAMETER name. It is resolved to the dispid the SERVER uses for that
        // parameter and passed through: dropping it re-points the value at the next POSITION, which is how
        // `wb.SaveAs(p, AccessMode: 3)` once wrote a CSV named .xlsx. A name the server will not resolve is
        // refused by the bridge rather than mis-passed, a wrong file is worse than an error.
        result = AsScriptValue(_obj.InvokeMethodNamedValue(name, args, argNames)) ?? this;
        return true;
    }

    private static bool HasAnyName(IReadOnlyList<string?> names)
    {
        for (var i = 0; i < names.Count; i++)
            if (!string.IsNullOrEmpty(names[i])) return true;
        return false;
    }

    bool IDynamicPropertyProvider.TryInvokeMethod(string name, object?[] args, out object? result)
    {
        // ForEach(Action<ComNode>), handle directly to avoid COM re-entrancy
        // from ScriptFunction→Delegate wrapping + InvokeScriptFunction during enumeration
        if (name.Equals("ForEach", StringComparison.Ordinal) && args is [ScriptFunction sf])
        {
            var itp = Current ?? throw new InvalidOperationException("No active interpreter");
            // A plain loop: the callback form allocated a delegate AND a closure per call, on the hot path for
            // `collection.ForEach(fun(item) { … })`. The argument array is now reused per DEPTH (ForEachArgs)
            // instead of per item, the same shape of fix as the delegate: allocation removed, semantics
            // unchanged. It does NOT move the iteration cost, and the line below used to imply it might:
            // Com.Stats() shows every idiom issuing identical COM traffic (one enumeration and zero name lookups
            // for a whole walk), so the 1,870 vs 2,650 ticks/item is resuming an iterator versus CALLING a script
            // function once per element, engine work, and inherent. The per-item array was pure GC pressure.
            _forEachDepth++;
            var cbArgs = ForEachArgs();
            try
            {
                foreach (var obj in _obj.EnumerateCollection())
                {
                    cbArgs[0] = _scope.Track(obj);
                    itp.InvokeScriptFunction(sf, cbArgs, 0, 0);
                }
            }
            finally { cbArgs[0] = null; _forEachDepth--; }   // drop the last node: the scope owns it, not this array
            result = this;
            return true;
        }

        // Native C# methods on ComNode resolve via reflection, decided under the interpreter's case flag, not by
        // set membership (see IsWrapperMember: the set is a case-insensitive index, shared across interpreters).
        if (IsWrapperMember(name))
        {
            result = null;
            return false;
        }

        // Not a name this object exposes: not ours to answer (see TryGetProperty), this is what turned a typo
        // into a successful call.
        if (_obj.FindDispId(name) is null)
        {
            result = null;
            return false;
        }

        // Invoke ONCE and let the variant the object RETURNED decide. The old shape asked for <ComObject> and
        // turned every conversion failure into `this`, so three different outcomes looked identical: a method
        // returning a string/number/bool, a call with the wrong argument count, and a dead server all came back
        // as the receiver. Only a genuinely empty result still keeps the receiver, that is what makes the
        // fluent VBA-style chains work (`excel.Workbooks.Add().ActiveSheet`, `sheet.Select()`), while a real
        // failure now propagates and reaches the author as a readable message.
        result = AsScriptValue(_obj.InvokeMethodValue(name, args)) ?? this;
        return true;
    }
}

