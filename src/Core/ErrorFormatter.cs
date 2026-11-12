namespace ScriptLangCore;

public static partial class ErrorFormatter
{
    private static readonly Regex LineColRegex = CreateLineColRegex();

    public static string FormatStackTrace(Exception ex, string? basePath = null)
    {
        if (ex.StackTrace is not { } stackTrace)
            return string.Empty;

        var sb = new StringBuilder();

        // Split into individual lines
        var lines = stackTrace.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            // Skip compiler-generated and infrastructure frames
            if (line.Contains("<>c__DisplayClass") ||
                line.Contains("<>c.<") ||
                line.Contains("NativeFunctionAdapter.Invoke") ||
                line.Contains("CallExpr.Accept") ||
                line.Contains("ExprStmt.Accept") ||
                line.Contains("Interpreter.Execute") ||
                line.Contains("Interpreter.Evaluate") ||
                line.Contains("EvaluateCallWithPooledArgs") ||
                line.Contains("VisitCallExpr") ||
                line.Contains("VisitMethodCallExpr") ||
                line.Contains("VisitExprStmt") ||
                line.Contains("VisitCompoundAssignExpr") ||
                line.Contains("VisitIncrementDecrementExpr") ||
                line.Contains("InvokeDelegate") ||
                line.Contains("InvokeScriptFunction") ||
                line.Contains("InvokeMethodOnInstance"))
                continue;

            // Keep only lines that belong to our project (optional stricter filter)
            if (!line.Contains(nameof(ScriptLangCore)))
                continue;

            // Remove the base path prefix
            if (basePath != null && line.Contains(basePath))
            {
                line = line.Replace(basePath, "");
            }

            // Clean up the "in ..." part: keep only the file name
            var inIndex = line.IndexOf(" in ", StringComparison.Ordinal);
            if (inIndex >= 0)
            {
                var prefix = line[..inIndex];
                var filePart = line[(inIndex + 4)..];
                var fileName = Path.GetFileName(filePart);
                line = $"{prefix} in {fileName}";
            }

            sb.Append("  ");
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    public static void FormatError(Exception ex, ReadOnlySpan<char> sourceCode, IReadOnlyList<string>? scriptCallStack = null)
    {
        var match = LineColRegex.Match(ex.Message);
        int line = -1, column = -1;
        if (match.Success)
        {
            line = int.Parse(match.Groups[1].ValueSpan);
            column = int.Parse(match.Groups[2].ValueSpan);
        }

        if (line >= 1)
        {
            ReadOnlySpan<char> targetLine = default;
            var currentLine = 1;
            var start = 0;
            for (var i = 0; i <= sourceCode.Length; i++)
            {
                if (i == sourceCode.Length || sourceCode[i] == '\n')
                {
                    if (currentLine == line)
                    {
                        targetLine = sourceCode[start..i];
                        if (!targetLine.IsEmpty && targetLine[^1] == '\r')
                            targetLine = targetLine[..^1];
                        break;
                    }
                    currentLine++;
                    start = i + 1;
                }
            }

            if (!targetLine.IsEmpty)
            {
                var adjustedColumn = column;
                var expandedLength = 0;
                foreach (var t in targetLine)
                    expandedLength += t == '\t' ? 4 : 1;

                if (column > 0)
                {
                    var col = 0;
                    for (var i = 0; i < targetLine.Length && i < column - 1; i++)
                        col += targetLine[i] == '\t' ? 4 : 1;
                    adjustedColumn = col + 1;
                }

                var expanded = expandedLength <= 1024
                    ? stackalloc char[expandedLength]
                    : new char[expandedLength];

                var pos = 0;
                foreach (var c in targetLine)
                {
                    if (c == '\t')
                    {
                        expanded[pos++] = ' ';
                        expanded[pos++] = ' ';
                        expanded[pos++] = ' ';
                        expanded[pos++] = ' ';
                    }
                    else expanded[pos++] = c;
                }

                ConsoleSafe.SetColor(ConsoleColor.Red);
                Console.WriteLine($"[L{line}:C{adjustedColumn}] Script stopped:");
                ConsoleSafe.ResetColor();
                Console.Out.WriteLine(expanded);

                if (adjustedColumn > 0 && adjustedColumn <= expandedLength + 1)
                {
                    var underscores = adjustedColumn - 1;
                    var caret = underscores <= 512
                        ? stackalloc char[underscores + 1]
                        : new char[underscores + 1];
                    caret[..underscores].Fill('_');
                    caret[underscores] = '^';

                    ConsoleSafe.SetColor(ConsoleColor.DarkRed);
                    Console.Out.Write(caret[..underscores]);
                    ConsoleSafe.SetColor(ConsoleColor.Red);
                    Console.Out.WriteLine(caret[underscores..]);
                    ConsoleSafe.ResetColor();
                }
            }
        }

        ConsoleSafe.SetColor(ConsoleColor.Red);
        Console.WriteLine($"Reason: {ex.Message}");
        ConsoleSafe.ResetColor();

        // --- Script call stack (user functions) ---
        if (scriptCallStack is { Count: > 0 })
        {
            ConsoleSafe.SetColor(ConsoleColor.Yellow);
            Console.WriteLine("Script call stack:");
            ConsoleSafe.ResetColor();
            foreach (var frame in scriptCallStack)
                Console.WriteLine($"  at {frame}");
            Console.WriteLine();
        }

        // --- Cleaned .NET stack trace ---
        ConsoleSafe.SetColor(ConsoleColor.DarkGreen);
        Console.WriteLine("Internal trace:");
        ConsoleSafe.ResetColor();

        var basePath = @"D:\DEV\NET\Wasm\ScriptLang\";   // adjust to your project root
        var cleanTrace = FormatStackTrace(ex, basePath);
        if (string.IsNullOrWhiteSpace(cleanTrace))
            Console.WriteLine("  (no relevant frames)");
        else
            Console.Write(cleanTrace);
    }

    [GeneratedRegex(@"at line (\d+), column (\d+)")]
    private static partial Regex CreateLineColRegex();
}
