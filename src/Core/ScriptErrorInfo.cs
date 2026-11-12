namespace ScriptLangCore;

/// <summary>Rich error information produced when a script error occurs.</summary>
public readonly record struct ScriptErrorInfo(
    ErrorCode Code,
    string Message,
    object? Value = null,
    /// <summary>Script call frames at the moment the error was raised, innermost first (function
    /// names, `&lt;lambda&gt;` for anonymous ones). Captured AT THROW TIME because the interpreter pops
    /// its frames as the exception unwinds; null for errors raised outside a script function
    /// (top-level statements, user `throw`, host errors).</summary>
    IReadOnlyList<string>? Frames = null
) : IDisposable
{
    public override string ToString() => Message;
    public void Dispose() { } // pure data, IDisposable for uniform script using/is

    /// <summary>
    /// Appends the source location to the message (" (at line L, column C)") so
    /// script-level catch blocks can see where a runtime error occurred. Only
    /// applied to engine-generated errors: skipped when the location is unknown,
    /// the info carries a raw throw value, the code marks a user throw
    /// (InvalidOperation / Interrupt), or the location was already appended
    /// (e.g. a caught-and-rethrown error), user `throw` values keep their exact
    /// message semantics.
    /// </summary>
    internal ScriptErrorInfo WithLocation(int line, int column)
    {
        if (line <= 0 || Value != null ||
            Code is ErrorCode.InvalidOperation or ErrorCode.Interrupt ||
            Message.Contains("at line ", StringComparison.Ordinal))
            return this;
        return this with { Message = $"{Message} (at line {line}, column {column})" };
    }
}

/// <summary>Categorizes script errors for programmatic handling.</summary>
public enum ErrorCode
{
    Success, // no error (== exit code 0)
    Unspecified = 100,
    UndefinedVariable,
    TypeMismatch,
    ArgumentCount,
    DivisionByZero,
    IndexOutOfRange,
    NullReference,
    InvalidOperation,
    ConstAssignment,
    ScriptAbort,
    AssertionFailed,
    ParseError,
    IoError,
    StackOverflow,
    Interrupt,
    AccessViolation,
    /// <summary>THE RUN WAS CANCELLED by its token (`RunScript(..., token)`), which is a runner's kill signal: the tree
    /// walker, `sleep` and the VM's loop back-edge ask the token, and NEITHER engine lets a script catch this one (a
    /// `try { &lt;a long loop&gt; } catch (var e) { }` must not defeat a Stop button), while a `finally` still runs.</summary>
    Cancelled,
    /// <summary>A FILE-LEVEL `await` SUSPENDED THE RUN (see `Interpreter.InterpretAsync`): the awaited task's completion
    /// posts the resume to the run queue, and the statement is re-entered with the value. It is a control signal between
    /// the await and its driver, never an error, so neither engine lets a script catch it while a `finally` still runs
    /// (the same rule `Cancelled` keeps, for the same reason: a handler must not be able to swallow the suspension).</summary>
    RunSuspend
}
