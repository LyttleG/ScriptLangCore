namespace ScriptLangCore.Logging;

/// <summary>Zero-allocation static logging facade. Writes to stderr with timestamp, level, color, and optional location.</summary>
internal static class Log
{
    private static LogLevel _minLevel = LogLevel.Information;

    public static void SetLevel(LogLevel level) => _minLevel = level;
    public static LogLevel GetLevel() => _minLevel;

    // ── With location ──

    public static void Debug(string source, string msg, int line, int col)
    {
        if (_minLevel > LogLevel.Debug) return;
        Write(LogLevel.Debug, source, msg, line, col);
    }

    public static void Info(string source, string msg, int line, int col)
    {
        if (_minLevel > LogLevel.Information) return;
        Write(LogLevel.Information, source, msg, line, col);
    }

    public static void Warning(string source, string msg, int line, int col)
    {
        if (_minLevel > LogLevel.Warning) return;
        Write(LogLevel.Warning, source, msg, line, col);
    }

    public static void Error(string source, string msg, int line, int col)
    {
        if (_minLevel > LogLevel.Error) return;
        Write(LogLevel.Error, source, msg, line, col);
    }

    // ── Without location ──

    public static void Debug(string source, string msg)
    {
        if (_minLevel > LogLevel.Debug) return;
        Write(LogLevel.Debug, source, msg, 0, 0);
    }

    public static void Info(string source, string msg)
    {
        if (_minLevel > LogLevel.Information) return;
        Write(LogLevel.Information, source, msg, 0, 0);
    }

    public static void Warning(string source, string msg)
    {
        if (_minLevel > LogLevel.Warning) return;
        Write(LogLevel.Warning, source, msg, 0, 0);
    }

    public static void Error(string source, string msg)
    {
        if (_minLevel > LogLevel.Error) return;
        Write(LogLevel.Error, source, msg, 0, 0);
    }

    // ── Raw with custom color ──

    public static void Raw(LogLevel level, string msg, ConsoleColor color)
    {
        if (_minLevel > level) return;
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var levelText = level.ToString().ToUpperInvariant();
        if (Interpreter.Current?.Err is { } sink)
        {
            sink.WriteLine($"[{ts}] [{levelText}] {SingleLine(msg)}");
            return;
        }
        ConsoleSafe.SetColor(color);
        Console.Error.Write($"[{ts}] [{levelText}] ");
        ConsoleSafe.ResetColor();
        Console.Error.WriteLine(SingleLine(msg));
    }

    // ── Helpers ──

    /// <summary>Format message with location for caller embedding: "[L{line}:C{col}] {msg}".</summary>
    public static string Fmt(string msg, int line, int col) => $"[L{line}:C{col}] {msg}";

    // ── Attribution for a NESTED script ──

    /// <summary>When a script runs ANOTHER script (RunScript with source text), a diagnostic raised inside it
    /// naturally carries a position in ITS OWN text, and that position means nothing to the reader of the outer
    /// file: `[L1:C8]` sends them to line 1 of the file they ran, where the nested source is a string literal
    /// (reported 2026-10-06: a suite whose own line 1 is a comment printed a warning at L1:C8, which was the
    /// nested text's line 1). While such a run is active the position PRINTED is the CALL's instead, which is the
    /// place a reader can act on, and the nested position is not lost: a refusal's message carries its own
    /// `(at line N, column N)`. A nested FILE keeps its own positions, since that file can be opened.</summary>
    [ThreadStatic] private static (int Line, int Column)? _attribution;

    /// <summary>The position diagnostics should use until the returned scope is disposed, for a run whose own
    /// positions are in text the reader cannot see. A no-op for a caller with no position (0), and the OUTERMOST
    /// scope wins, because that is the file being read when scripts nest.</summary>
    public static IDisposable Attribution(int line, int column) => new AttributionScope(line, column);

    private sealed class AttributionScope : IDisposable
    {
        private readonly bool _applied;

        public AttributionScope(int line, int column)
        {
            _applied = line > 0 && _attribution is null;
            if (_applied) _attribution = (line, column);
        }

        public void Dispose()
        {
            if (_applied) _attribution = null;
        }
    }

    private static void Write(LogLevel level, string source, string msg, int line, int col)
    {
        // A nested script's diagnostic is attributed to the CALL that ran it (see Attribution above).
        if (_attribution is { } origin) { line = origin.Line; col = origin.Column; }
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var color = level switch
        {
            LogLevel.Trace => ConsoleColor.DarkGray,
            LogLevel.Debug => ConsoleColor.DarkYellow,
            LogLevel.Information => ConsoleColor.Gray,
            LogLevel.Warning => ConsoleColor.Yellow,
            LogLevel.Error => ConsoleColor.Red,
            _ => ConsoleColor.Gray,
        };

        var loc = line > 0 || col > 0 ? $"[L{line}:C{col}] " : "";

        // A HOST THAT EMBEDS THE ENGINE OWNS ITS OWN CONSOLE, so the sink of the interpreter that is CURRENT on this
        // thread wins over the process's stderr: an embedded run's warnings and errors go to the host's callback
        // rather than into somebody else's terminal. A run with no sink (the CLI, a suite) keeps stderr exactly as it
        // was, colours included, so nothing about the shipped command line moved for this.
        if (Interpreter.Current?.Err is { } sink)
        {
            var levelText = level.ToString().ToUpperInvariant();
            sink.WriteLine($"[{ts}] [{levelText}] {loc}{source}: {SingleLine(msg)}");
            return;
        }

        ConsoleSafe.SetColor(color);
        Console.Error.Write($"[{ts}] [{level.ToString().ToUpperInvariant()}] {loc}");
        ConsoleSafe.ResetColor();
        Console.Error.Write($"{source}: ");
        Console.Error.WriteLine(SingleLine(msg));
    }

    private static string SingleLine(string msg) => msg.Replace('\n', ' ').Replace('\r', ' ');
}

/// <summary>Mirrors Microsoft.Extensions.Logging.LogLevel for zero-dependency compatibility.</summary>
internal enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    None = 6,
}
