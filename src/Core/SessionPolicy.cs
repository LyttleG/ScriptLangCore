namespace ScriptLangCore;

/// <summary>WHAT A SESSION MAY TOUCH. An embedded host can name the directories a script's files must live in (`dirs`)
/// and the families of calls it does not trust (`deny`), and every refusal NAMES the capability or the directories,
/// because that sentence is what a host shows its user and what a support log needs.
///
/// THIS IS A GUARDRAIL, NOT A SECURITY BOUNDARY, and the distinction is the honest one: the engine runs INSIDE the
/// host's process with the host's own privileges, and `NativeCalls` can call any function the process can reach. What
/// the policy buys is that a script cannot ACCIDENTALLY read the wrong folder, start a process, fetch a URL, drive
/// Excel or reach the clipboard, and that a host which runs untrusted input gets a refusal by name instead of a silent
/// ability. A security boundary is an out-of-process door; this is the one an in-proc host can have.
///
/// THE DOORS IT CLOSES, one capability each: `file` — the File and Directory namespaces, `Bytecode`'s exports, the
/// import door and a SQLite database path, the first three through the namespace gate and all four through
/// <see cref="Confine"/>; `process` — `Process`; `network` — `Http`; `com` — `Com`; `clipboard` — `Clipboard`; `ui` —
/// `Application.CreateForm`. A capability that is denied refuses EVERY member of its namespace, including the members
/// that only compute (`Http.EncodeForm`), because "this family is off" is a simpler promise than a member list.
///
/// THREADING: `Current` is `[ThreadStatic]` and <see cref="Attach"/> sets it, the same shape `ScriptSession` uses for
/// the host's own facts. The policy therefore applies to the thread a RUN executes on, and a run that posts its work to
/// the session's own thread attaches it there rather than on the caller's.</summary>
internal sealed class SessionPolicy
{
    /// <summary>The capability names a host may put in "deny". ONE statement, because the option's validation, the
    /// refusals and the page must agree about what exists.</summary>
    internal static readonly string[] Known = ["file", "process", "network", "ui", "com", "clipboard"];

    /// <summary>Which namespace members a capability owns. A null member list means EVERY member; `Application` is
    /// listed member by member, because a script must keep reading `Application.Path` and `Application.Args` while
    /// forms are denied.</summary>
    private static readonly Dictionary<string, (string Capability, string[]? Members)> Doors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["File"] = ("file", null),
            ["Directory"] = ("file", null),
            ["Bytecode"] = ("file", null),
            ["Process"] = ("process", null),
            ["Http"] = ("network", null),
            ["Com"] = ("com", null),
            ["Clipboard"] = ("clipboard", null),
            ["Application"] = ("ui", ["CreateForm"]),
        };

    [ThreadStatic] private static SessionPolicy? _current;

    private readonly HashSet<string> _denied;
    private readonly string[] _directories;

    private SessionPolicy(HashSet<string> denied, string[] directories)
    {
        _denied = denied;
        _directories = directories;
    }

    /// <summary>The policy of the run executing on THIS thread, or null (the CLI, every suite, and any session that
    /// named neither option), which is why an unrestricted engine pays one null test per member call.</summary>
    internal static SessionPolicy? Current => _current;

    internal static SessionPolicy Create(IEnumerable<string> denied, IEnumerable<string> directories)
        => new(new HashSet<string>(denied, StringComparer.OrdinalIgnoreCase), [.. directories]);

    internal bool HasDirectories => _directories.Length > 0;

    internal IReadOnlyList<string> Directories => _directories;

    /// <summary>Makes <paramref name="policy"/> the one THIS thread's script calls answer, until the scope is disposed.
    /// Scopes nest and restore what came before, the rule `ScriptSession.Attach` uses for the same reason.</summary>
    internal static IDisposable Attach(SessionPolicy? policy)
    {
        var previous = _current;
        _current = policy;
        return new AttachedScope(previous);
    }

    private sealed class AttachedScope(SessionPolicy? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    /// <summary>THE NAMESPACE GATE, called by `Namespace.InvokeMethod` before a member runs. A denied capability throws
    /// the refusal that names it, with the call's own position so the line the script must change is the one named.</summary>
    internal static void RefuseIfDenied(string? ns, string member, int line, int column)
    {
        var policy = _current;
        if (policy is null || policy._denied.Count == 0 || ns is null) return;
        if (!Doors.TryGetValue(ns, out var door)) return;
        if (door.Members is not null && !Array.Exists(door.Members, m => string.Equals(m, member, StringComparison.OrdinalIgnoreCase))) return;
        if (!policy._denied.Contains(door.Capability)) return;

        var sentence = $"the \"{door.Capability}\" capability is denied for this session (\"deny\" in the session options),"
                       + $" so {ns}.{member} cannot run";
        if (line > 0) throw new ScriptException(ErrorCode.Unspecified, sentence, line, column);
        throw new ScriptException(sentence);
    }

    /// <summary>THE PATH GATE, called by the ONE path rule (`Interpreter.ResolvePath`) after it has resolved a
    /// script-chosen path, and by a SQLite database path before it reaches the native open. Denying `file` refuses
    /// everything; `dirs` refuses what lies outside every named directory; otherwise the path travels unchanged, so a
    /// caller wraps its resolution in one expression.</summary>
    internal static string Confine(string fullPath)
    {
        var policy = _current;
        if (policy is null) return fullPath;
        if (policy._denied.Contains("file"))
        {
            throw new ScriptException($"the \"file\" capability is denied for this session (\"deny\" in the session options),"
                                      + $" so a script cannot touch '{fullPath}'");
        }
        if (policy._directories.Length == 0) return fullPath;

        foreach (var directory in policy._directories)
        {
            if (IsInside(fullPath, directory)) return fullPath;
        }
        throw new ScriptException($"'{fullPath}' is outside the session's \"dirs\" list ({string.Join("; ", policy._directories)}),"
                                  + " so a script cannot touch it");
    }

    /// <summary>Is <paramref name="fullPath"/> the directory itself or something under it? A bare prefix test would let
    /// `C:\\allowed-two` pass `C:\\allowed`, so the separator is part of the test. Case-insensitive on Windows, which is
    /// the only platform this engine ships for, and ordinal elsewhere so a Linux build of the core still compares
    /// truthfully.</summary>
    private static bool IsInside(string fullPath, string directory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullPath, directory, comparison)) return true;
        var root = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, comparison);
    }
}
