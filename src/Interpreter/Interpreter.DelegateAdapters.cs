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
    // forwards Invoke to InvokeScriptFunction with CoerceTo on return.
    // ═══════════════════════════════════════════════════════════════

    internal sealed class ActionAdapter0(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public void Invoke() => itp.InvokeScriptFunction(fn, [], line, col);
    }

    internal sealed class ActionAdapter1<T1>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public void Invoke(T1 a1) => itp.InvokeScriptFunction(fn, [a1], line, col);
    }

    internal sealed class ActionAdapter2<T1, T2>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public void Invoke(T1 a1, T2 a2) => itp.InvokeScriptFunction(fn, [a1, a2], line, col);
    }

    internal sealed class ActionAdapter3<T1, T2, T3>(ScriptFunction fn, Interpreter itp, int line, int col)
    {
        public void Invoke(T1 a1, T2 a2, T3 a3) => itp.InvokeScriptFunction(fn, [a1, a2, a3], line, col);
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
