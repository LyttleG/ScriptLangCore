namespace ScriptLangCore;

/// <summary>WHAT A HOST TELLS THE ENGINE ABOUT ITSELF, and the ONE place the answers `Application.FilePath`,
/// `Application.Path`, `Application.Args` and the native beside-the-engine lookup come from.
///
/// WHY IT EXISTS: the engine ships as a NativeAOT EXE, where `Environment.ProcessPath` and
/// `Environment.GetCommandLineArgs()` ARE the engine's image and its own argv. Loaded as a DLL into somebody
/// else's process both answer the HOST: `Application.Path` would name the host's folder, so a script looking for
/// a module beside the engine looks in the wrong place, and `Application.Args` would report the host's command
/// line, which is another program's flags. Those two facts are two of the items the 2026-10-27 AOT-DLL POC
/// listed as "what else changes", and a session is the smallest shape that answers them WITHOUT a second code
/// path inside the interpreter: with no session attached every resolver here answers exactly what the engine
/// answered before, which is what keeps the CLI, the REPL and every suite on their own answers by default.
///
/// WHAT IT IS NOT: a run pipeline. A session carries what a host can know BEFORE a script runs and nothing else.
/// Running a file, a snippet or a bundle, and reporting its verdict, belongs to the caller (the CLI today, the
/// embedding ABI next), because a run is where console redirection, the exit code and the lifetime of a script's
/// forms are decided, and those are the host's business rather than the engine's.
///
/// THREADING: `Current` is `[ThreadStatic]`, and `Attach` is what sets it. That is this engine's own rule for
/// anything interpreter-adjacent (the call-frame stacks, the cancellation token and the pump's run-thread id are
/// all thread-affine), so a host that drives one session from several threads attaches it on each of them rather
/// than expecting one static to be visible everywhere. The embedding ABI attaches it on the thread a run executes
/// on, which is the only thread that ever reads these answers.</summary>
internal sealed class ScriptSession
{
    [ThreadStatic] private static ScriptSession? _current;

    /// <summary>The session attached to THIS thread, or null, which is the plain CLI case where every answer below
    /// keeps its historical meaning.</summary>
    public static ScriptSession? Current => _current;

    /// <summary>The engine image a script should treat as "the executable": `Application.FilePath`'s answer, and
    /// the parent of `Application.Path`. Null means `Environment.ProcessPath`, which is what the CLI wants and what
    /// a DLL run must override, because there it names the host.</summary>
    public string? ImagePath { get; set; }

    /// <summary>The folder the engine keeps its own things in (`assets/`, `tools/`, the name-bound natives such as
    /// sqlite3.dll): `Application.Path`'s answer. Null means the image's own directory, which is the deployed
    /// layout rule this repository already documents (``assets/`` and ``tools/`` sit beside the executable).
    /// A caller that fills this from a host's option validates it first, which is why the normalisation below is
    /// allowed to throw: an invalid path is a caller's programming error, refused by name before it reaches here,
    /// and never something a script handed over.</summary>
    public string? Home { get; set; }

    /// <summary>What `Application.Args` reports, in the shape the CLI already gives a script: the arguments AFTER
    /// the executable's own path, so a script's own flags start at `Args[0]`. Null means the process's own command
    /// line, which is right for the EXE and wrong for a DLL host (it is the host's flags). An EMPTY list is a real
    /// answer and is kept as one, so a host with no arguments cannot be mistaken for no session at all.</summary>
    public IReadOnlyList<string>? Arguments { get; set; }

    /// <summary>Makes this session the one THIS thread's reads answer, until the returned scope is disposed.
    /// Scopes NEST: the dispose restores whatever was attached before, so an embedding ABI can wrap a single run
    /// without disturbing an outer one, which is the same rule the nested-run family of bugs settled for the
    /// call-frame stacks.</summary>
    public IDisposable Attach()
    {
        var previous = _current;
        _current = this;
        return new AttachedScope(previous);
    }

    private sealed class AttachedScope(ScriptSession? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    /// <summary>`Application.FilePath`'s answer: the session's image when there is one, otherwise the process's own
    /// path, and empty rather than null when the platform cannot answer, which is the shape the property always
    /// had.</summary>
    public static string ResolveImagePath() => Current?.ImagePath ?? Environment.ProcessPath ?? "";

    /// <summary>`Application.Path`'s answer: the session's home, otherwise the image's directory. A session's home
    /// is normalised to a full path, so the two spellings a script combines (`Application.Path + "/x.slc"` and a
    /// `Path.Combine` the engine does itself) cannot disagree about `..` or a trailing separator.</summary>
    public static string ResolveHome()
    {
        if (Current?.Home is { Length: > 0 } home) return Path.GetFullPath(home);
        var image = ResolveImagePath();
        return image.Length == 0 ? "" : Path.GetDirectoryName(image) ?? "";
    }

    /// <summary>`Application.Args`'s answer, WITHOUT the executable's own path: the session's list when it has one,
    /// otherwise the process's command line read the way the CLI always read it. The process's list is COPIED rather
    /// than handed over as the runtime's array, because the script-facing property is rebuilt per read and an
    /// owner's array is not ours to expose.</summary>
    public static IReadOnlyList<string> ResolveArguments()
    {
        if (Current?.Arguments is { } explicitArgs) return explicitArgs;
        var argv = Environment.GetCommandLineArgs();
        if (argv.Length <= 1) return [];
        var rest = new string[argv.Length - 1];
        Array.Copy(argv, 1, rest, 0, rest.Length);
        return rest;
    }
}
