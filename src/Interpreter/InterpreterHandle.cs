namespace ScriptLangCore;

internal ref struct InterpreterHandle : IDisposable
{
    private Interpreter _instance;

    /// <summary>Does this handle OWN the interpreter? A created one does, and disposing the handle disposes the
    /// interpreter with it (the CLI's lifetime). An ADOPTED one does not: it belongs to whoever made it and outlives
    /// the handle, which is what lets a host-driven REPL run on a session's interpreter and end without killing the
    /// session. The flag exists because `.reset hard` disposes the handle before making a fresh interpreter, and with
    /// an adopted one that call would otherwise dispose the HOST's interpreter from inside a dot command.</summary>
    private readonly bool _owned;

    public Interpreter Instance =>
        _instance ?? throw new ObjectDisposedException(nameof(InterpreterHandle));

    /// <summary>True when this handle owns its interpreter, so a caller can refuse an operation that would replace
    /// it. False for an adopted one.</summary>
    public readonly bool Owns => _owned;

    public void Dispose()
    {
        if (_instance == null) return;
        var itp = _instance;
        _instance = null!;
        if (!_owned) return;
        InterpreterManager.Unregister(itp);
        itp.Dispose();
    }

    internal InterpreterHandle(Interpreter itp)
    {
        _instance = itp;
        _owned = true;
    }

    private InterpreterHandle(Interpreter itp, bool owned)
    {
        _instance = itp;
        _owned = owned;
    }

    /// <summary>A handle over an interpreter somebody else owns: disposing it forgets the reference and touches
    /// nothing else. Used by the REPL when it runs on a host's session interpreter.</summary>
    internal static InterpreterHandle Adopt(Interpreter itp) => new(itp, owned: false);
}
