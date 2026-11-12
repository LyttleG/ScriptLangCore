namespace ScriptLangCore;

/// <summary>Shared file-reading helpers that use pooled char buffers to avoid string allocations.</summary>
internal static class FileReader
{
    [ThreadStatic]
    private static char[]? _pendingStripBuffer;

    /// <summary>Reads a file into a pooled char buffer (no dot-command stripping).</summary>
    public static (ReadOnlyMemory<char> Memory, char[]? Buffer) ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var charCount = Encoding.UTF8.GetCharCount(bytes);
        var buffer = CharBufferPool.Rent(charCount);
        var count = Encoding.UTF8.GetChars(bytes, 0, bytes.Length, buffer, 0);
        return (buffer.AsMemory(0, count), buffer);
    }

    /// <summary>Release a buffer returned by ReadFile.</summary>
    public static void ReleaseBuffer(char[]? buffer)
    {
        if (buffer != null)
            CharBufferPool.Return(buffer);
        if (_pendingStripBuffer == null) return;
        CharBufferPool.Return(_pendingStripBuffer);
        _pendingStripBuffer = null;
    }

    /// <summary>Removes the REPL DOT COMMANDS a file may carry (a pasted session, and the `.time`/`.echo` toggles a
    /// caller honours), so such a file still runs. A line is a command ONLY when it starts at COLUMN 1 with a name
    /// this REPL knows, and both halves are load-bearing, each for a defect measured on this tree: "any line whose
    /// first non-space character is a dot" silently DELETED the CSS rules inside the HTMX template's raw string (the
    /// page was served with holes in its stylesheet) AND the continuation of a chained call written the way C#
    /// allows (`s.Replace(a, b)` followed by an indented `.Replace(c, d)`), which ended the statement at the first
    /// call and made the parser fail several lines later, naming a `return` that was perfectly fine.
    ///
    /// IT LIVES HERE, ONCE, because two copies existed and this file held the BROKEN one: `Program` had this rule
    /// while `FileReader` kept a twin that removed every line whose first non-space character was a dot. Nothing
    /// called the twin, which is exactly what made it dangerous, a later caller would silently have got the rule the
    /// two fixes above replaced. The toggles travel as `ref` arguments rather than as fields, so this stays a pure
    /// text operation and the CLI keeps its own `_timingMode`/`_echoMode`.</summary>
    public static ReadOnlyMemory<char> StripDotCommands(ReadOnlyMemory<char> source, ref bool timingMode, ref bool echoMode)
    {
        var src = source.Span;
        var dotCmd = ".time".AsSpan();
        var dotEcho = ".echo".AsSpan();
        var onSpan = "on".AsSpan();
        var anyRemoved = false;

        // ONE rule, asked by both passes: this line is a dot command rather than code.
        static bool IsCommand(ReadOnlySpan<char> line)
        {
            if (line.Length < 2 || line[0] != '.') return false;   // COLUMN 1 only: an indented `.member` is a chain
            if (line.IndexOf('{') >= 0) return false;              // a CSS rule is not a command
            var end = 1;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_')) end++;
            if (end == 1) return false;                            // a bare '.', or '..'
            return Repl.DotCommands.IsKnownCommand(line[..end]);
        }

        // ONE boundary rule, asked by both passes and stated ONCE in TextLines: a line ends at CR, LF or CRLF, and the
        // splitter reports which terminator it found so the rebuild below can put the same bytes back.
        foreach (var line in new LineSplitter(src))
        {
            if (IsCommand(line.Text))
            {
                anyRemoved = true;
                if (line.Text.StartsWith(dotCmd, StringComparison.Ordinal)) timingMode = !timingMode;
                else if (line.Text.StartsWith(dotEcho, StringComparison.Ordinal)) echoMode = line.Text.Contains(onSpan, StringComparison.Ordinal);
            }
        }

        // Fast path: no dot commands, return original memory
        if (!anyRemoved) return source;

        // Slow path: rebuild by scanning again into a pooled char buffer, writing each kept line and the SAME terminator
        // it carried, so a CRLF source stays CRLF and a source with no final break keeps having none.
        var buffer = CharBufferPool.Rent(source.Length);
        var pos = 0;
        foreach (var line in new LineSplitter(src))
        {
            if (IsCommand(line.Text)) continue;

            line.Text.CopyTo(buffer.AsSpan(pos));
            pos += line.Text.Length;
            switch (line.Break)
            {
                case LineBreak.CrLf: buffer[pos++] = '\r'; buffer[pos++] = '\n'; break;
                case LineBreak.Lf: buffer[pos++] = '\n'; break;
                case LineBreak.Cr: buffer[pos++] = '\r'; break;
            }
        }

        _pendingStripBuffer = buffer;
        return buffer.AsMemory(0, pos);
    }
}
