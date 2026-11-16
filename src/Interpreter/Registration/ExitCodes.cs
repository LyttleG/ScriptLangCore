namespace ScriptLangCore;

/// <summary>The exit codes the ENGINE itself uses, in ONE place, so the command line and a script agree about what
/// a number means. They were bare digits before this: the CLI returned 2 and 3 from several sites, the
/// <c>--dump-bytecode</c> path named its pair privately, and a script had nothing to write but a literal. Naming
/// them changes NO number, which is why the tools that read them keep working: <c>tools/dump-bytecode.sh</c>
/// counts exactly <see cref="Refused"/> as `refused` and reads <see cref="Usage"/> as "did not dump at all",
/// beside 124 meaning a timeout.
///
/// The four meanings, and the suite convention they line up with: a suite ends with
/// <c>exit(CheckFailed())</c>, which answers 0 or 1, so <see cref="Failed"/> is the same 1 a failed run exits
/// with. A script that wants one of these writes it by name, see
/// <see cref="Interpreter.RegisterExitCodeFunctions"/>.</summary>
internal static class ExitCode
{
    /// <summary>A normal end: the work asked for was done.</summary>
    public const int Ok = 0;

    /// <summary>A failure: a script error, a child that did not do its job, a check that failed.</summary>
    public const int Failed = 1;

    /// <summary>A usage or input problem: a file that could not be read or does not exist, an output path or a
    /// flag the caller got wrong. The engine says which on stderr.</summary>
    public const int Usage = 2;

    /// <summary>A REFUSAL: the engine declined to do what was asked and named the reason on stderr (a body the
    /// verifier rejects, a file that does not parse, a strip that cannot be honoured in strict mode).</summary>
    public const int Refused = 3;
}

internal partial class Interpreter
{
    /// <summary>The namespace a script writes those codes through, so an exit code carries its meaning at the
    /// call site: <c>exit(ExitCode.Usage)</c> rather than <c>exit(2)</c>. It is a namespace of its own rather
    /// than a member of an existing one because the codes belong to the RUN, not to a form, a file or the
    /// clipboard, and a script that exits with a REFUSAL is the one place the distinction matters.</summary>
    private void RegisterExitCodeFunctions()
    {
        var codes = new Namespace(_comparer);
        codes.AddProperty("Ok", ExitCode.Ok);
        codes.AddProperty("Failed", ExitCode.Failed);
        codes.AddProperty("Usage", ExitCode.Usage);
        codes.AddProperty("Refused", ExitCode.Refused);
        CurrentFrame.Scopes.Define("ExitCode", codes);
    }
}
