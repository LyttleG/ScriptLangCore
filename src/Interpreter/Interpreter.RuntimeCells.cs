namespace ScriptLangCore;

internal partial class Interpreter
{
    internal sealed class ScheduledCoroutine(Coroutine co, DateTime wakeTime, object? payload = null)
    {
        public Coroutine Coroutine { get; } = co;
        public DateTime WakeTime { get; } = wakeTime;
        public object? ResumePayload { get; } = payload;

        public static readonly IComparer<ScheduledCoroutine> Comparer =
            Comparer<ScheduledCoroutine>.Create((a, b) =>
            {
                var cmp = a.WakeTime.CompareTo(b.WakeTime);
                if (cmp != 0) return cmp;

                // Use reference identity to break ties ï¿½ never treat different coroutines as equal
                return ReferenceEquals(a.Coroutine, b.Coroutine) ? 0 :
                       a.Coroutine.GetHashCode() > b.Coroutine.GetHashCode() ? 1 : -1;
            });
    }


    private readonly record struct PureArgsComparer(StringComparer Comparer) : IEqualityComparer<(string funcName, object?[] args)>
    {
        public bool Equals((string funcName, object?[] args) x, (string funcName, object?[] args) y)
        {
            if (!Comparer.Equals(x.funcName, y.funcName))
                return false;
            if (x.args.Length != y.args.Length)
                return false;
            for (var i = 0; i < x.args.Length; i++)
            {
                if (!Equals(x.args[i], y.args[i]))
                    return false;
            }
            return true;
        }

        public int GetHashCode((string funcName, object?[] args) obj)
        {
            var hash = Comparer.GetHashCode(obj.funcName);
            foreach (var a in obj.args)
                hash = HashCode.Combine(hash, a);
            return hash;
        }
    }


    // ============================================================================
    // 10. INTERPRETER - PARTIE 2 : TYPES D'EXï¿½CUTION (RECORDS/CLASSES INTERNES)
    // ============================================================================
    private sealed class MutableCell(object? value)
    {
        private object? _value = value;
        public object? Get() => _value;
        public void Set(object? val) => _value = val;
    }

    /// <summary>A reference to a variable, built for a `ref`/`out` argument (or a capture promotion).
    /// <paramref name="ByOut"/> records WHICH keyword the call site wrote: true for `out`, false for `ref`, and null
    /// when the engine built the cell for its own reasons (a capture, a wrap, an inferred reference). A rule that
    /// must tell the two keywords apart can then refuse a contradiction without accusing a cell that never named
    /// one.</summary>
    internal sealed record RefCell(Func<object?> Getter, Action<object?> Setter, bool? ByOut = null)
    {
        public object? Get() => Getter();
        public void Set(object? value) => Setter(value);
    }

    /// <summary>THE NAME OF A FUNCTION DECLARED LATER IN THE SAME BODY, bound there from the body's start (C#'s hoisting
    /// rule: a local function is in scope for the whole of its enclosing block, not only from its declaration downwards).
    /// <para>The engine bound the name when the declaration EXECUTED, so a closure created earlier in that body could not
    /// capture it: measured, `listBox.DropFiles = (_, e) => AfterAdd(...)` assigned above `void AfterAdd(...)` answered
    /// `Undefined variable 'AfterAdd'` when the handler ran, because the lambda's closure snapshot is taken at creation and
    /// had no such entry.</para>
    /// <para>WHAT IS BOUND IS A SLOT FOR THE NAME, NOT THE FUNCTION: the declaration fills it (see VisitFuncDecl), because
    /// a function built at body entry would snapshot the enclosing scope before the locals it reads exist (measured shape:
    /// the hoisted `AfterAdd` reads `infoLabel`, declared further down the same body). A closure that captured the slot
    /// therefore sees the real function at call time, and a read that arrives BEFORE the declaration is a refusal naming
    /// the variable, which is the engine's own wording for a name nothing has initialized.</para></summary>
    internal sealed class ForwardFunctionSlot(string name)
    {
        /// <summary>The function (or the overload set) once the declaration has run, else null.</summary>
        public object? Value;

        /// <summary>True when a closure took this name while it was still unfilled, i.e. the closure was created ABOVE the
        /// declaration. Such a declaration keeps the TREE (see InvokeScriptFunction): its body was compiled without that
        /// capture layout, so a compiled run would address slots its bytecode does not have.</summary>
        public bool CapturedEarly;

        public object? Read(int line, int column) => Value ?? throw new ScriptException(
            ErrorCode.UndefinedVariable,
            $"The function '{name}' is used before the declaration that defines it in this block (a nested declaration "
            + "is bound when its body starts, and it becomes callable once its declaration has run).",
            line, column);

        public override string ToString() => $"<declaration of {name}>";
    }

    /// <summary>A script-level tuple value, heterogeneous fixed-length item list.
    /// Supports Item1..ItemN access, integer indexing, Length/Count, and destructuring.</summary>
    internal sealed class ScriptTuple(object?[] items) : IDisposable
    {
        public object?[] Items { get; } = items;
        /// <summary>Optional element names from a tuple-typed declaration, e.g. (int i, string s) → ["i","s"].</summary>
        public IReadOnlyList<string>? Names { get; set; }
        public void Dispose() { } // pure data, nothing to release; IDisposable for uniform script using/is

        public int Length => Items.Length;
        public int Count => Items.Length;
        public object? this[int index] => index >= 0 && index < Items.Length ? Items[index] : null;
        public object? Item1 => Items.Length > 0 ? Items[0] : null;
        public object? Item2 => Items.Length > 1 ? Items[1] : null;
        public object? Item3 => Items.Length > 2 ? Items[2] : null;
        public object? Item4 => Items.Length > 3 ? Items[3] : null;
        public object? Item5 => Items.Length > 4 ? Items[4] : null;
        public object? Item6 => Items.Length > 5 ? Items[5] : null;
        public object? Item7 => Items.Length > 6 ? Items[6] : null;
        public object? Item8 => Items.Length > 7 ? Items[7] : null;

        public override string ToString() => ScriptNameOf(typeof(ScriptTuple)) ?? "Tuple";
    }
}
