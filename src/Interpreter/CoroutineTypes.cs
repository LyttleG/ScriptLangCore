namespace ScriptLangCore;

public enum CoroutineStatus { NotStarted, Suspended, Dead, Waiting, Completed }

/// <summary>A request a coroutine yields to ask the pump for something (a sleep, an event, an HTTP call, a line of
/// stdin). `closed` is load bearing and not decoration: its four subtypes all live in this assembly, and the ONE
/// place a new kind must be handled is `Interpreter.HandleYieldRequest`, which is a switch EXPRESSION so the
/// compiler can check it for exhaustiveness. The boundary was measured before it was relied on: with a missing arm
/// a `closed` base warns CS8509 on a switch EXPRESSION (naming the missed pattern) and says NOTHING about a switch
/// STATEMENT, so the statement this started as would have kept the check silent.</summary>
internal closed class CoroutineYieldRequest { }

internal sealed class HttpRequest(string method, string url, string? jsonBody = null, int timeoutMs = 30000,
    CancellationToken token = default) : CoroutineYieldRequest
{
    /// <summary>Effective token for this call (explicit, else the ambient token captured at call time).</summary>
    public CancellationToken Token { get; } = token;
    public string Method { get; } = method;
    public string Url { get; } = url;
    public string? JsonBody { get; } = jsonBody;
    public int TimeoutMs { get; } = timeoutMs;
}

internal sealed class SleepRequest(int ms) : CoroutineYieldRequest
{
    public int Milliseconds { get; } = ms;
}

internal sealed class EventWaitRequest(Interpreter.EventInfo evt) : CoroutineYieldRequest
{
    public Interpreter.EventInfo Event { get; } = evt;
}

/// <summary>Yield request for non-blocking stdin read. A background thread reads a line and resumes the coroutine.</summary>
internal sealed class ReadLineRequest : CoroutineYieldRequest
{
    public string? Result { get; set; }
    public readonly ManualResetEventSlim Completed = new(false);
}

/// <summary>Yield request for `await`: the enclosing async body suspends until the engine <see cref="Task"/>
/// completes, then resumes with it. The REQUEST is the value the pump wakes the coroutine with, and the awaiting
/// side reads the awaited outcome straight off <see cref="Task"/> (another field here would be a second copy of
/// the result and of the fault rule the Task already owns).</summary>
internal sealed class AwaitRequest(Task task) : CoroutineYieldRequest
{
    public Task Task { get; } = task;
}

/// <summary>Tree-interpreted coroutine state. Null for VM-backed generators, the bytecode VM
/// captures its own suspend state (VmSuspendState) and never touches these fields. Keeping them in
/// a separate nullable sub-object means VM-backed Coroutines carry no dead weight (~5 fields ≈
/// 40+ B/level in deep generator recursion). Allocated lazily, only on the tree path.</summary>
internal sealed class TreeState
{
    public CoroutineState? State;
    public int LoopBaseSp;  // base index in the shared loop stack for THIS coroutine's frames
    public ScopeStack.SavedScopeStack? SavedScope;
    public object? LastHttpResult;
    public object? PendingResumeValue;

    /// <summary>Set when this coroutine suspended INSIDE a `try` region: the try, which catch, and the thrown value.
    /// The resume then re-enters that catch instead of the whole try, because re-running the try body would make its
    /// own awaited statement consume the value the catch's await is waiting for (measured: an await in the try, a
    /// throw, then an await in the catch answered "Coroutine 'G' must return a value"). It lives here rather than in
    /// an interpreter field so two coroutines suspending in the same statement cannot see each other's record.</summary>
    public (TryStmt Node, int CatchIndex, object? Thrown)? SuspendedCatch;

    /// <summary>The statement index whose CURRENT pass already consumed an awaited value, or -1. See the refusal in
    /// VisitAwaitExpr: a body resumes by re-entering its statement, so a second await in the same pass would re-issue
    /// the first for ever. It lives here, per coroutine, because two coroutines in the same statement must not see
    /// each other's pass (the same reason SuspendedCatch does).</summary>
    public int AwaitConsumedStatement = -1;
}

/// <summary>Full captured run state of a VM generator suspended at the outermost boundary, a YIELD</summary>
/// executed in the TOP frame of a Vm.Execute invocation driven by Generator.MoveNext. Every
/// per-invocation local of Vm.Execute is snapshotted so Vm.ExecuteResumed can restore it and
/// continue the loop exactly where YIELD left off.</summary>
internal sealed class VmSuspendState
{
    public Bytecode Bc = null!;
    public byte[] Code = null!;
    /// <summary>Operand stack materialized from the VM's parallel typed arrays (VmValue[i] = [0,i)).</summary>
    public VmValue[] Stack = null!;
    public int Sp;
    public VmValue[] Locals = null!;
    public List<SavedFrame> Frames = null!;
    /// <summary>Exception-handler stack (the `handlers` local of Vm.Execute).</summary>
    public List<(int sp, int catchIp, int finallyIp, bool pendingThrow, VmValue pendingVal, int seq)>? Handlers;
    public int Ip;
    public int HandlerBase;
    public int SpBase;
    public bool PendingFinally;
    public object? PendingFinallyPayload;
    public int Seq;
    public List<(int stateLocal, int seq)>? ForEachStates;
    public int CurLine;
    public int CurCol;
    public Namespace? NsContext;
    public string CurrentFunc = "";
    /// <summary>Set when this suspension is an AWAIT rather than a value yield: the resume must resolve the payload
    /// it is handed (the awaited request) and PUSH its value. A yield leaves it false, which is the older rule that
    /// its resume pushes nothing.</summary>
    public bool PushResumeValue;
}

internal struct CoroutineState
{
    public int StatementIndex;
    public bool ExpectResumeValue;
    public int LoopStackSp;
    public int LoopBaseSp; // index into the shared loop stack where THIS function's frames start
    public LoopFrame[] SavedLoopStack; // pre-allocated copy of loop stack at yield time
    // Scope is preserved on Coroutine.SavedScope, not here
}

internal enum LoopType { While, For, Foreach, Block }

internal struct LoopFrame
{
    public LoopType Type;
    // ── Phase-based state machine (current loop paths) ──
    public int Phase;
    public int ForPhase;
    public int LoopNodeStatementIndex;
    public object? LoopNode;      // AST node ref, unambiguous identity for nested loops with the same statement index
    public Array? ArrayEnumerator;
    public int ArrayIndex;
    public bool HasCurrent;
    public object? CurrentElement;
    public Coroutine? Coroutine;
    public IScriptList? ScriptList;
    public ScriptTuple? Tuple;   // foreach over a bare tuple, indexed element-wise
    public bool ScopeActive;
    public bool InitScopeActive;   // coroutine for: the loop-scope holding the init var
    // ── Position tracking (simplified coroutine path) ──
    public int BodyStatementIndex;
    public int YieldedBodyIndex;   // which body statement to resume at
    /// <summary>Whether the yield at <see cref="YieldedBodyIndex"/> must be RE-ENTERED on resume, which is the
    /// rule a request yield follows (an await or a sleep consumes the pump's value by re-running its statement),
    /// where a plain value yield is finished once it produced its value and the resume goes PAST it. It is the
    /// loop-frame half of the rule the body loop applies at its own resume index.</summary>
    public bool YieldedReEnter;
    public IEnumerator? Iterator;    // foreach: IEnumerator ref
}

public sealed class Coroutine
{
    private Interpreter? _interpreter;
    internal readonly ScriptFunction Fn;
    internal readonly object?[] Args;
    internal bool Started;
    internal bool IsGenerator;  // true when owned by Generator<T>, not user-started via Coroutine.Start
    private bool _inProgress;

    /// <summary>Tree-interpreted coroutine state (State/LoopBaseSp/SavedScope/LastHttpResult/
    /// PendingResumeValue). Null for VM-backed generators, they never touch the tree machinery.</summary>
    internal TreeState? Tree;

    /// <summary>Captured VM run state when this generator suspended at the VM boundary (VmBacked).</summary>
    internal VmSuspendState? VmState;

    /// <summary>True when this coroutine is a plain value-yield generator dispatched to the bytecode
    /// VM (Generator.MoveNext drives Vm.Execute / Vm.ExecuteResumed). Yield-request coroutines
    /// (Sleep/http/event/ReadLineAsync) and Coroutine.Start-driven coroutines stay tree (false).</summary>
    internal bool VmBacked;

    /// <summary>Bound receiver for a class/struct method coroutine body (Task 4). The VM path appends
    /// it as the trailing `this` local (the compiler indexes `this` after the params); the tree path
    /// binds `this` in the coroutine's scope and sets _currentThis/_currentClass around the body via
    /// InvokeCoroutineBody. Null for free-function coroutines (no receiver).</summary>
    internal object? ThisInstance;

    /// <summary>Declaring class of a method-coroutine's receiver (static-field / super resolution).</summary>
    internal ClassInfo? ThisClass;

    /// <summary>Set only on a coroutine an `async` function CALL created: the script-visible Task the caller
    /// holds, completed (or faulted) when this body finishes. Null for every other coroutine, so Coroutine.Start,
    /// the generator path and the VM drive are untouched.</summary>
    internal Task? AsyncTask;

    internal Interpreter? Interpreter => _interpreter;

    /// <summary>Allocate the tree-state sub-object on first tree-path use (lazy, VM-backed
    /// coroutines keep Tree == null).</summary>
    internal TreeState EnsureTree() => Tree ??= new TreeState();

    internal object? TakePendingResumeValue()
    {
        var t = Tree;
        var v = t?.PendingResumeValue;
        if (t != null) t.PendingResumeValue = null;
        return v;
    }

    public CoroutineStatus Status { get; internal set; } = CoroutineStatus.NotStarted;

    /// <summary>The last value the body YIELDED (null until it yields). Completion does NOT clear it and
    /// does NOT overwrite it, it keeps the last yielded value, which is also what an iterator's
    /// <see cref="Current"/> reads. The body's return value lives in <see cref="ReturnValue"/>.</summary>
    public object? LastYieldedValue { get; internal set; }

    /// <summary>The value the body RETURNED, set when it completes NATURALLY (Status Completed). Null while
    /// it is still running/waiting, and null when it never completed, disposed, abandoned or errored
    /// coroutines have no return value. This used to share LastYieldedValue's backing value, so one name
    /// carried two meanings: "last value produced" plus, silently, "the return value once Dead".</summary>
    public object? ReturnValue { get; internal set; }

    /// <summary>True while the coroutine can still run or produce a value: NotStarted (not yet
    /// started), Suspended (yielded a value), Waiting (yielded a request). False once it completed
    /// NATURALLY (Completed, its body ran to the end / a return) or was disposed, abandoned or
    /// errored (Dead). Script-visible: hand-driven loops should use <code>while (co.IsResumable)</code>
    /// instead of <code>while (co.Status != CoroutineStatus.Dead)</code> so a naturally-completed
    /// coroutine (Completed) stops the loop too.</summary>
    public bool IsResumable => Status is CoroutineStatus.NotStarted or CoroutineStatus.Suspended or CoroutineStatus.Waiting;

    internal Coroutine(Interpreter interpreter, ScriptFunction fn, object?[] args)
    {
        _interpreter = interpreter;
        Fn = fn;
        Args = [.. args];   // own a private copy, callers may rent from a clearing ArrayPool
    }

    public object? Resume(object? arg = null)
    {
        var itp = _interpreter;
        itp?.TraceCoro($"Coroutine.Resume {Fn.Declaration.Name} inProgress={_inProgress} status={Status}");
        if (_inProgress) throw new ScriptException("Cannot resume a coroutine that is already executing.");

        _inProgress = true;
        EnsureTree().PendingResumeValue = arg;
        try
        {
            var result = _interpreter?.ResumeCoroutine(this);
            SettleAsyncTask();
            return result;
        }
        catch (Exception ex)
        {
            // An ASYNC body's fault belongs to the Task its caller holds (C#'s rule), so it must NOT escape the
            // resume: a resume driven by the coroutine pump runs inside whatever called the pump, and a fault
            // thrown from there poisoned an UNRELATED later wait (measured: an unread faulted task made the next
            // `Ok().Wait()` report that task's "boom"). A PLAIN coroutine keeps the old behaviour and rethrows, so
            // such a fault still surfaces at Coroutine.Start / Resume.
            var task = AsyncTask;
            if (task is null) throw;
            AsyncTask = null;
            task.SetException(ex);
            return null;   // the fault went to the Task, and a faulted resume yields nothing
        }
        finally { _inProgress = false; }
    }

    /// <summary>Completes the Task an `async` CALL handed back, once this body is no longer resumable. A no-op
    /// for every other coroutine (AsyncTask is null), so Coroutine.Start, the generator drive and the VM path
    /// see nothing.</summary>
    private void SettleAsyncTask()
    {
        var task = AsyncTask;
        if (task is null || Status is not (CoroutineStatus.Completed or CoroutineStatus.Dead)) return;
        AsyncTask = null;
        if (Status == CoroutineStatus.Completed)
            // The declared return type is the contract, and the parser maps `async Task<T>` to T, so a named tuple
            // comes back with its ELEMENT NAMES the way a plain return's does. The stamp is the same helper the
            // coercion funnel uses, applied here because a coroutine's result is deliberately not coerced.
            task.SetResult(Interpreter.StampTupleElementNames(ReturnValue, Fn.Declaration.ReturnType));
        else
            task.SetException(new ScriptException($"The async body '{Fn.Declaration.Name}' ended without completing."));
    }

    public void Dispose()
    {
        Tree = null;
        VmState = null;
        Status = CoroutineStatus.Dead;
        _interpreter = null;
    }

    /// <summary>Continue execution to the next yield / request / return, alias of
    /// <see cref="Resume"/>. Safe on any status: on a Completed/Dead coroutine it returns null.
    /// Script-visible, so hand-driven loops can read <code>co.Continue()</code> like
    /// <code>co.Resume()</code> (MoveNext's Coroutine-side counterpart).</summary>
    public object? Continue() => Resume();

    /// <summary>Abort the coroutine: mark it Dead and free its state, alias of <see cref="Dispose"/>.
    /// A continued-but-abandoned (Suspended) coroutine becomes Dead; a later Continue/Resume returns
    /// null and never re-runs the body.</summary>
    public void Abort() => Dispose();

    /// <summary>Safe destroy, alias of <see cref="Abort"/>. Marks the coroutine Dead and frees its
    /// state; a later Continue/Resume returns null instead of throwing. (Symmetry with
    /// <c>Generator.Reset</c>.)</summary>
    public void Reset() => Abort();

    public override string ToString() => Fn.ToString();
}

/// <summary>Lightweight lazy iterable wrapping a Coroutine. Implements IEnumerator/IEnumerable for use in foreach.</summary>
internal sealed class Generator<T> : IEnumerator<T>
{
    private readonly Coroutine _co;

    public Generator(Coroutine co)
    {
        _co = co;
        co.IsGenerator = true;
    }
    public T Current => (T?)_co.LastYieldedValue!;
    object? IEnumerator.Current => _co.LastYieldedValue;
    /// <summary>Coroutine status of the underlying generator: NotStarted → Suspended/Waiting while
    /// iterating → Dead once exhausted. Script-visible, so `g.Status == CoroutineStatus.Dead`
    /// works directly (the raw Coroutine is also reachable via <see cref="InnerCoroutine"/>).</summary>
    public CoroutineStatus Status => _co.Status;
    /// <summary>The last value the generator YIELDED, also what <see cref="Current"/> reads. After the
    /// generator is exhausted this keeps that last yielded value; the body's return value is
    /// <see cref="ReturnValue"/>.</summary>
    public object? LastYieldedValue => _co.LastYieldedValue;

    /// <summary>The value the generator's body RETURNED once it completed naturally (null while it is still
    /// suspended and null when it never completed, e.g. disposed or errored).</summary>
    public object? ReturnValue => _co.ReturnValue;
    public bool MoveNext()
    {
        // ── VM-backed generator (plain value yields only): the first MoveNext starts Vm.Execute,
        // every later one resumes the captured VmSuspendState. The VM returns the yielded value on
        // a boundary suspension; a completion returns the function's return value with no suspend
        // state. A VmFallback on the FIRST MoveNext means the VM could not execute part of the body
        // AFTER already running earlier statements (side effects), this THROWS loudly (see below);
        // silently re-running the body tree-walked would double-execute those side effects. The
        // IsYieldRequestBody gate (Interpreter.cs) is supposed to make that
        // unreachable. Yield-request coroutines (Sleep/http/event/ReadLineAsync) are never VmBacked
        // (IsYieldRequestBody gate), so their resume-value consumption stays on the tree path.
        if (_co.VmBacked)
        {
            var itp = _co.Interpreter;
            if (itp == null || _co.Fn.Declaration.Compiled == null) return false;
            VmSuspendState? suspend;
            object? result;
            // A throw from here, the recursion/OOM guard raising ScriptException, any error in the body,
            // must not leave the generator NotStarted. Reported 2026-09-11: after a caught OOM from
            // MoveNext, `gr.Status` read NotStarted, so the caller could neither tell that the generator had
            // run and failed nor retry safely (a retry would re-run the body). Errored => Dead, the rule this
            // file already applies in the VM-fallback path below and CoroutineStatus documents.
            try
            {
                if (_co.Status == CoroutineStatus.NotStarted)
                    result = Vm.ExecuteCore(_co.Fn.Declaration.Compiled, _co.Args, itp, out suspend,
                        _co.Fn.Declaration.Name, _co.Fn.NamespaceContext);
                else if (_co.Status == CoroutineStatus.Suspended && _co.VmState != null)
                    result = Vm.ExecuteResumedCore(_co.VmState, null, itp, out suspend);
                else
                    return false; // Dead / Waiting / no VM state, nothing to resume
            }
            catch
            {
                _co.Status = CoroutineStatus.Dead;
                _co.VmState = null;
                throw;
            }

            if (result is VmFallback)
            {
                if (_co.Status == CoroutineStatus.NotStarted)
                {
                    // The VM fell back on the FIRST MoveNext, the generator body contains a
                    // construct the bytecode VM cannot execute. The VM has ALREADY executed the
                    // statements before the failing one, so silently discarding the VM backing and
                    // re-running the body tree-walked would DOUBLE-EXECUTE their side effects
                    // (prints, global mutation, native calls). Fail LOUDLY instead. The
                    // IsYieldRequestBody gate must make this unreachable; if
                    // it still fires, the language gained a construct the gates do not yet know
                    // about (the old silent path is exactly the double-execution bug this replaces).
                    // Mark dead so a script that catches and retries MoveNext can never re-run the
                    // body's already-executed side effects.
                    _co.Status = CoroutineStatus.Dead;
                    _co.VmState = null;
                    throw new ScriptException(
                        $"VM generator '{_co.Fn.Declaration.Name}' cannot execute its body on the " +
                        "bytecode VM, a construct the VM cannot run was reached before the first " +
                        "yield (possible VM gate gap; the generator stayed VM-backed because its " +
                        "body appeared VM-safe).");
                }

                // The VM already suspended this generator and cannot resume it, the body hit a
                // VM-incompatible construct AFTER a yield (the tree has no state for a VM-suspended
                // generator). Failing loudly beats silently re-running the body with wrong values.
                throw new ScriptException(
                    $"VM generator '{_co.Fn.Declaration.Name}' cannot be resumed after the bytecode VM fell back " +
                    "(a construct the VM cannot execute was reached after a yield).");
            }

            itp.VmInvocations++;
            _co.VmState = suspend;
            if (suspend != null)
            {
                _co.LastYieldedValue = result;      // suspended: this is a YIELDED value
                _co.Status = CoroutineStatus.Suspended;
                return true;
            }

            // The VM run finished without suspending → the body ran to completion naturally, so this is
            // the body's RETURN value, not a yielded one.
            _co.ReturnValue = result;
            _co.Status = CoroutineStatus.Completed;
            return false;
        }

        // The tree path's sibling of the guard above: a resume that dies (a script error inside a
        // tree-interpreted generator body) must leave the generator Dead, not NotStarted. Same rule, same
        // reason, a caller that catches could otherwise retry and re-run the body.
        try
        {
            if (_co.Status == CoroutineStatus.NotStarted) _co.Resume();
            else if (_co.Status == CoroutineStatus.Suspended || _co.Status == CoroutineStatus.Waiting) _co.Resume();
        }
        catch
        {
            _co.Status = CoroutineStatus.Dead;
            _co.VmState = null;
            throw;
        }
        return _co.Status == CoroutineStatus.Suspended || _co.Status == CoroutineStatus.Waiting;
    }
    /// <summary>Safe destroy instead of a hard failure: the generator's coroutine is aborted
    /// (marked Dead, state freed), so a subsequent <see cref="MoveNext"/> returns False instead of
    /// throwing. The generator cannot be re-started afterwards, re-create it for a fresh run.
    /// (IEnumerator contract: Reset is meant to rewind; this engine's generators are one-shot,
    /// so the closest safe behaviour is to terminate the run.)</summary>
    public void Reset() => _co.Dispose();
    public void Dispose() => _co.Dispose();
    /// <summary>Continue to the generator's next yield or completion, alias of <see cref="MoveNext"/>.
    /// After a <code>break</code>-abandoned foreach the generator is Suspended; <code>Continue()</code>
    /// resumes it from the yield point.</summary>
    public bool Continue() => MoveNext();
    /// <summary>Abort the generator: mark its coroutine Dead and free its state, alias of
    /// <see cref="Dispose"/>.</summary>
    public void Abort() => _co.Dispose();
    public Coroutine InnerCoroutine => _co;
}
