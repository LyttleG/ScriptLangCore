namespace ScriptLangCore;

internal partial class Interpreter
{
    private readonly record struct ScriptDelegateWrapper(Interpreter Interpreter, ScriptFunction Function)
    {
        private readonly Interpreter _interpreter = Interpreter;
        private readonly ScriptFunction _function = Function;

        public object? Invoke(object?[] args) => _interpreter.InvokeScriptFunction(_function, args, 0, 0);
        public override string ToString() => _function.ToString();
    }

    // ═══════════════════════════════════════════════════════════════
    // Delegate adapter types, bridge ScriptFunction to CLR delegates
    // Used by CoerceArgument when passing a script function where a
    // .NET delegate (Action<T>, Func<T,R>, etc.) is expected.
    // Each adapter closes over the ScriptFunction + Interpreter and
    // forwards Invoke to InvokeScriptBodyFromClr with CoerceTo on return.
    //
    // `fireAndForget` is the phrase a failure is REPORTED under when the CLR hands this delegate to a thread of its
    // own, and null when the caller can observe the failure (see FireAndForgetPhrase). It is decided per delegate
    // TYPE at AsDelegate, so the eight adapters do not each decide what a refusal means.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>THE DELEGATE TYPES THE CLR RUNS FIRE AND FORGET, with the phrase a body failure is reported under, or
    /// null for every type whose failure a script CAN observe. The difference was measured, not assumed:
    ///
    ///   - `ThreadPool.QueueUserWorkItem` (a `WaitCallback`) threw out of the engine and became `Unhandled exception.
    ///     ScriptLangCore.ScriptException: Division by zero ... at ThreadPoolWorkQueue.Dispatch()`, printed with a CLR
    ///     stack trace, and the run answered 0. Nothing captures that exception, so the engine must.
    ///   - `Task.Run(fun () { throw "boom"; })` throws INTO the Task, and `await` (and `Wait()`) hand it back to the
    ///     script: measured, `try { await Task.Run(fun () { throw "boom"; }); } catch (e) { ... }` prints
    ///     `caught: boom`. Swallowing at that boundary would take away a catch that works, which is why an `Action`
    ///     is deliberately NOT in this list.
    ///
    /// `TimerCallback` and `ThreadStart`/`ParameterizedThreadStart` are the same shape (a thread of the CLR's own, no
    /// observer). Neither is reachable from a script TODAY, because `new System.Threading.Timer(...)` and
    /// `new Thread(...)` are refused at construction ("Constructor on type ... not found", measured), and they stay in
    /// the list so that gap closing does not reopen this one.</summary>
    private static string? FireAndForgetPhrase(Type delegateType) =>
        delegateType == typeof(WaitCallback) ? "a ThreadPool work item failed"
        : delegateType == typeof(TimerCallback) ? "a timer callback failed"
        : delegateType == typeof(ThreadStart) || delegateType == typeof(ParameterizedThreadStart) ? "a thread body failed"
        : null;

    internal sealed class ActionAdapter0(ScriptFunction fn, Interpreter itp, int line, int col, string? fireAndForget)
    {
        public void Invoke() => itp.InvokeScriptBodyFromClr(fn, [], line, col, fireAndForget);
    }

    internal sealed class ActionAdapter1<T1>(ScriptFunction fn, Interpreter itp, int line, int col, string? fireAndForget)
    {
        public void Invoke(T1 a1) => itp.InvokeScriptBodyFromClr(fn, [a1], line, col, fireAndForget);
    }

    internal sealed class ActionAdapter2<T1, T2>(ScriptFunction fn, Interpreter itp, int line, int col, string? fireAndForget)
    {
        public void Invoke(T1 a1, T2 a2) => itp.InvokeScriptBodyFromClr(fn, [a1, a2], line, col, fireAndForget);
    }

    internal sealed class ActionAdapter3<T1, T2, T3>(ScriptFunction fn, Interpreter itp, int line, int col, string? fireAndForget)
    {
        public void Invoke(T1 a1, T2 a2, T3 a3) => itp.InvokeScriptBodyFromClr(fn, [a1, a2, a3], line, col, fireAndForget);
    }

    internal sealed class FuncAdapter0<TR>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public TR Invoke()
        {
            var r = itp.InvokeScriptFunction(fn, [], line, col); if (r is TR rr)
                return rr;
            return (TR)Convert.ChangeType(r!, typeof(TR));
        }
    }

    internal sealed class FuncAdapter1<T1, TR>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public TR Invoke(T1 a1)
        {
            var r = itp.InvokeScriptFunction(fn, [a1], line, col);
            if (r is TR rr)
                return rr;
            return (TR)Convert.ChangeType(r!, typeof(TR));
        }
    }

    internal sealed class FuncAdapter2<T1, T2, TR>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public TR Invoke(T1 a1, T2 a2)
        {
            var r = itp.InvokeScriptFunction(fn, [a1, a2], line, col);
            if (r is TR rr)
                return rr;
            return (TR)Convert.ChangeType(r!, typeof(TR));
        }
    }
    internal sealed class FuncAdapter3<T1, T2, T3, TR>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public TR Invoke(T1 a1, T2 a2, T3 a3)
        {
            var r = itp.InvokeScriptFunction(fn, [a1, a2, a3], line, col);
            if (r is TR rr)
                return rr;
            return (TR)Convert.ChangeType(r!, typeof(TR));
        }
    }
}
