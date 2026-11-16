namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    /// Represents an exception that is thrown to indicate that script execution was aborted with a specific exit code.
    /// </summary>
    private sealed class ScriptAbortException(int exitCode) : ScriptException(ErrorCode.ScriptAbort, exitCode, line: 0, column: 0)
    {
        public int ExitCode { get; } = exitCode;
    }

    /// <summary>The VALUE a VM bail carries, which is what <see cref="IsVmBailSignal"/> reads, and the reason it
    /// reads the value rather than the type: a call site that REBUILDS a ScriptException (ExecuteCore attaches the
    /// script call frames) replaces the value with a ScriptErrorInfo, so a type test would be lost, which is the same
    /// lesson AbortExitCode records for the exit code.</summary>
    private sealed class VmBailMarker
    {
        internal static readonly VmBailMarker Instance = new();
        private VmBailMarker() { }
    }

    /// <summary>THE BAIL SIGNAL: the bytecode VM cannot execute the instruction it is at, so this invocation has to
    /// hand the body back to the tree. It travels as an EXCEPTION rather than as VmFallback.Instance because the
    /// dispatch loop's own handler walk is what runs a script's pending `finally` blocks, and a bail used to return
    /// straight out of it: measured, `try { marker = 2; <an instruction the VM cannot run>; } finally { marker = 4; }`
    /// printed marker=2, so a teardown a script wrote to run unconditionally never ran (that is how the Excel sample
    /// left an invisible EXCEL.EXE behind while its `finally` held Quit()).
    ///
    /// It is NEVER swallowed by a script's own `catch` (the walk skips its catch arms for this signal), so the
    /// commitment rule is unchanged: the BOUNDARY turns it back into VmFallback.Instance, and the existing sixteen
    /// `is VmFallback` sites then refuse a committed body or re-run an uncommitted one exactly as before.</summary>
    private sealed class VmBailException() : ScriptException(ErrorCode.Unspecified, VmBailMarker.Instance, line: 0, column: 0);

    /// <summary>Is this exception the VM's own bail (see <see cref="VmBailException"/>)? Asked by the VM's handler
    /// walk (run a finally, never a catch), by the frame-enrichment arm (never rebuild it) and by the boundary (turn
    /// it back into VmFallback.Instance).</summary>
    internal static bool IsVmBailSignal(ScriptException ex)
        => ex is VmBailException || ReferenceEquals(ex.Value, VmBailMarker.Instance);

    /// <summary>A NEW bail signal for the dispatch loop's Bail(). One allocation per bail, and a bail is a
    /// compilation or modelling gap rather than a hot path, which is why it may allocate.</summary>
    internal static ScriptException NewVmBail() => new VmBailException();

    /// <summary>Ends the RUN the way a script's own <c>exit()</c> does, for ENGINE code that has to stop a script:
    /// today <c>Application.SingleInstance</c> set by a second instance, which is VB's rule (the second instance
    /// exits instead of creating its windows). It throws the same abort the builtin throws, so the exit code
    /// travels and the run is NOT reported as an error.</summary>
    internal static ScriptException AbortRun(int exitCode) => new ScriptAbortException(exitCode);

    /// <summary>The exit code a script's own exit() carries, or null when <paramref name="ex"/> is not an
    /// abort. An `exit(0)` IS an abort, and a run that ends that way is not an error: comparing the code
    /// against 0 cannot tell it from normal completion, which is why the flag exists (CollectLibraryContent
    /// draws the same distinction). One decoder, so a .slc file and a bundle built from it agree on what
    /// `exit(CheckFailed())` means, and a reroll that kept the VALUE (Interpreter.WithLocation adds a call
    /// site) still answers with the code it carried.</summary>
    internal static int? AbortExitCode(ScriptException ex)
    {
        if (ex is VmBailException || ReferenceEquals(ex.Value, VmBailMarker.Instance)) return null;
        if (ex.ErrorCode != ErrorCode.ScriptAbort) return null;
        return ex is ScriptAbortException sae ? sae.ExitCode
            : ex.Value is int carried ? carried   // rerolled without the code object, value intact
            : 1;                                  // an abort whose code was lost: the engine's own default
    }
}
