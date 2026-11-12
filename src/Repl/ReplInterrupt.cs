namespace ScriptLangCore.Repl;

/// <summary>
/// Durable Ctrl+C state shared by the REPL loop and the dotcommands.
///
/// The console CancelKeyPress handler raises the flag; long-running
/// dotcommands (`.run`, `.looprun`, `.bench`, `.gc leak`) poll it between
/// iterations so they all abort the same way and report
/// "Interrupted by user …". It deliberately lives OUTSIDE the Interpreter:
/// Interpreter.Reset() clears its own `_interruptRequested`, so an interrupt
/// raised between (or during) reset cycles would be lost if that were the
/// only signal. This flag is only raised while the REPL is active
/// (RunRepl registers the handler).
/// </summary>
internal static class ReplInterrupt
{
    private static volatile bool _requested;

    /// <summary>Raised by the Ctrl+C handler (runs on the console thread).</summary>
    internal static void Set() => _requested = true;

    /// <summary>True while an interrupt is pending (does not clear it).</summary>
    internal static bool Pending => _requested;

    /// <summary>True if an interrupt is pending; clears it in that case.</summary>
    internal static bool Take()
    {
        var pending = _requested;
        _requested = false;
        return pending;
    }

    /// <summary>Clears any pending interrupt without reporting it.</summary>
    internal static void Clear() => _requested = false;
}
