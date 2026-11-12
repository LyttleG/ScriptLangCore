namespace ScriptLangCore;

internal sealed record ScriptFunction(
    FuncDecl Declaration,
    ClosureSnapshot ClosureSnapshot,
    TypeInfo ReturnType,
    IReadOnlyList<TypeInfo> ParameterTypes,
    IReadOnlyList<bool> IsRefParams,
    FunctionFlags Flags = FunctionFlags.Legacy,
    IReadOnlyList<bool>? IsOutParams = null
)
{
    /// <summary>VM-created capturing lambdas: values for the hidden locals after the params
    /// ([this?] + captures, in the compiler's layout order). Null for capture-free / tree lambdas.
    /// For local functions (IsLocal) it carries a "this" placeholder + the captured values in the
    /// FuncDecl's VmCaptureNames order, the VM-direct path binds them as extra args like MAKE_LAMBDA.</summary>
    public object?[]? VmCaptures;

    /// <summary>True for a nested (local) function declared inside another function's body
    /// (VisitFuncDecl with _currentFuncDecl != null). Local functions are lazily compiled to
    /// bytecode on first invocation and run VM-flat with VM capture materialization.</summary>
    public bool IsLocal;

    /// <summary>True for functions exported from a source module (LoadModule) whose snapshot's
    /// GlobalScope is the module's own flat scope. InvokeScriptFunction must NOT refresh that
    /// scope with the caller's live globals or link the caller's writable root, module free
    /// variables resolve in the defining module, and module-global writes stay in the module's
    /// shared snapshot (never leaking into the importer).</summary>
    public bool IsModuleScope;

    /// <summary>F4: the namespace leaf this function is bound into, used as a fallback for
    /// bare-name sibling calls (VM and tree). Null for flat functions. (Field added in Task 1,
    /// used in Task 6.)</summary>
    public Namespace? NamespaceContext;

    public bool IsPure => (Flags & FunctionFlags.Pure) != 0;
    /// <summary>True when invoking this returns a Generator. The DECLARATION is authoritative: a loaded body has no
    /// AST to carry the flag, so the loader derives it from the bytecode (a YIELD), and a caller that passed only
    /// FunctionFlags.Legacy would otherwise run the body eagerly. Measured: without this the loaded generator
    /// was flat-called, reached its first YIELD, and the run ended SILENTLY with exit 0.</summary>
    public bool IsCoroutine => (Flags & FunctionFlags.Coroutine) != 0 || Declaration.IsCoroutine;

    /// <summary>The rendering a SCRIPT sees, because this is what the value formatter uses for `print` and for
    /// concatenation: the signature, the return type, and the two flags a script can act on. It deliberately does
    /// NOT carry the declaration's line and column, which it used to: a value's rendering is not a diagnostic, and
    /// `print(pred)` answered `func <lambda>(int) → object (L72:C23)`, where the position is data a reader has to
    /// decode (reported from assets/language/misc/38_delegates_full-E.slc, 2026-10-06). A refusal that needs a
    /// position carries its own `(at line N, column N)`, and the debugger has its own renderings, so nothing lost
    /// information it needed.</summary>
    public override string ToString()
    {
        var name = Interpreter.FrameLabel(Declaration.Name);
        var sig = new StringBuilder(256);
        sig.Append("func ").Append(name).Append('(');
        for (var i = 0; i < Declaration.Params.Count; i++)
        {
            if (i > 0) sig.Append(", ");
            sig.Append(Declaration.Params[i].Type);
        }
        sig.Append(')');
        if (ReturnType.BaseType != TokenType.Void)
            sig.Append(" -> ").Append(ReturnType);
        if (IsPure) sig.Append(" [pure]");
        if (IsCoroutine) sig.Append(" [coroutine]");
        return sig.ToString();
    }
}
