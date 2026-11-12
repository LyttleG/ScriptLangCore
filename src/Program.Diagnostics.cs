namespace ScriptLangCore;

internal static partial class Program
{
    // =========================================================================
    // BANNER & HELP
    // =========================================================================
    private static string FormatTypeName(TypeInfo type)
    {
        if (type.TypeName != null) return type.TypeName;
        return type.BaseType switch
        {
            TokenType.String or TokenType.StringType => "string",
            TokenType.Number or TokenType.Int or TokenType.Long => "int",
            TokenType.Float or TokenType.Double => "float",
            TokenType.Bool => "bool",
            TokenType.Object => "object",
            TokenType.Void => "void",
            _ => type.BaseType.ToString().ToLowerInvariant()
        };
    }

    private static void PrintBanner()
    {
        if (Console.IsOutputRedirected) return;  // no console, skip splash

        var isAdmin = false;
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // ignored
        }

        DrawSplashScreen(isAdmin);
        Console.SetCursorPosition(0, 8);
    }

    static void DrawSplashScreen(bool isAdmin)
    {
        //
        // Define the box components
        var title = $" 💻  ScriptLangCore v{Version}";
        var shellInfo = $" └── Interactive Shell {(isAdmin ? "(Administrator)" : "(User)         ")}";
        var copyright = " └── (c) Gérôme GUILLEMIN 2026    ";

        // The longest line dictates the box width (accounting for emoji quirks)
        // 'title' has an emoji which is 2 chars in .NET but occupies 2 terminal spaces.
        // We normalize the measurement to 30 visible console spaces for all lines.
        var boxWidth = 39;
        var color = isAdmin ? ConsoleColor.DarkRed : ConsoleColor.DarkGreen;

        // Draw Box Top
        WriteCentered($"╔{new string('═', boxWidth)}╗", ConsoleColor.DarkGray);

        // Draw Header Line (Title)
        WriteCenteredLine(title, boxWidth - 1, ConsoleColor.DarkCyan);

        // Draw Divider
        WriteCentered($"╠{new string('═', boxWidth)}╣", ConsoleColor.DarkGray);

        // Draw Data Lines
        WriteCenteredLine(shellInfo, boxWidth, color);
        WriteCenteredLine(copyright, boxWidth, ConsoleColor.DarkGray);

        // Draw Box Bottom
        WriteCentered($"╚{new string('═', boxWidth)}╝", ConsoleColor.DarkGray);
        Console.WriteLine();

        // Left-aligned interactive user prompt
        ConsoleSafe.ResetColor();
        Console.WriteLine("💡 Type .help for help, .exit to quit ScriptLangCore");
    }

    /// <summary>
    /// Centers simple structural elements (like borders) perfectly.
    /// </summary>
    static void WriteCentered(string text, ConsoleColor color)
    {
        ConsoleSafe.SetColor(color);
        var pad = Math.Max(0, (Console.WindowWidth - text.Length) / 6);
        Console.SetCursorPosition(pad, Console.CursorTop);
        Console.WriteLine(text);
    }

    /// <summary>
    /// Centers content lines inside the box safely, handling internal content length.
    /// </summary>
    static void WriteCenteredLine(string content, int targetWidth, ConsoleColor textColor)
    {
        // Compensate for .NET string length vs visual console width due to emoji surrogate pairs
        var visualLength = content.Contains("💻") ? content.Length - 1 : content.Length;
        var trailingSpaces = targetWidth - visualLength;

        // Build the localized row string
        var fullRow = "║" + content + new string(' ', Math.Max(0, trailingSpaces)) + "║";

        // Center the entire calculated row on the screen
        var pad = Math.Max(0, (Console.WindowWidth - fullRow.Length) / 6);
        Console.SetCursorPosition(pad, Console.CursorTop);

        // Print components with distinct colors
        ConsoleSafe.SetColor(ConsoleColor.DarkGray); // Box Border Color
        Console.Write("║");
        ConsoleSafe.SetColor(textColor);             // Text Content Color
        Console.Write(content + new string(' ', Math.Max(0, trailingSpaces)));
        ConsoleSafe.SetColor(ConsoleColor.DarkGray); // Box Border Color
        Console.WriteLine("║");
    }

    /// <summary>The message a SESSION shows for a script that failed. `Interpret` folds a script failure into the
    /// `ScriptResult` it returns instead of throwing, so every caller that RUNS something has to ask; forgetting is
    /// silent, which is why this is one rule asked by both session paths. The location is appended through the
    /// error's OWN rule (`ScriptErrorInfo.WithLocation`, which skips a message that already carries one), and only
    /// where a reader needs it: a FILE says "at line N" because its lines are not on screen, a REPL line does not.</summary>
    private static string ScriptFailureText(ScriptResult result, bool withLocation)
    {
        if (result.ErrorInfo is { } info)
            return withLocation ? info.WithLocation(result.ErrorLine, result.ErrorColumn).Message : info.Message;
        return result.ErrorMessage ?? "Script error";
    }

    private static void PrintTree(string name, object? value, string indent, bool isLast)
    {
        var prefix = indent + (isLast ? "  └── " : "  ├── ");
        switch (value)
        {
            case null:
                Console.WriteLine($"{prefix}{name}: null");
                break;
            case IScriptList list:
                {
                    Console.WriteLine($"{prefix}{name}: List [{list.Count}]");
                    var childIndent = indent + (isLast ? "     " : "  │  ");
                    for (var i = 0; i < list.Count && i < 10; i++)
                        PrintTree($"[{i}]", list[i], childIndent, i == list.Count - 1 || i == 9);
                    if (list.Count > 10)
                        Console.WriteLine($"{childIndent}  └── ... ({list.Count - 10} more)");
                    break;
                }

            case IScriptDictionary dict:
                {
                    Console.WriteLine($"{prefix}{name}: Dictionary ({dict.Count} entries)");
                    var childIndent = indent + (isLast ? "     " : "  │  ");
                    var keys = dict.Keys();
                    for (var i = 0; i < keys.Length && i < 10; i++)
                    {
                        var k = keys[i];
                        PrintTree(k.ToString() ?? "?", dict[k], childIndent, i == keys.Length - 1 || i == 9);
                    }
                    if (keys.Length > 10)
                        Console.WriteLine($"{childIndent}  └── ... ({keys.Length - 10} more)");
                    break;
                }

            case IList ilist:
                {
                    Console.WriteLine($"{prefix}{name}: List [{ilist.Count}]");
                    var childIndent = indent + (isLast ? "     " : "  │  ");
                    for (var i = 0; i < ilist.Count && i < 10; i++)
                        PrintTree($"[{i}]", ilist[i], childIndent, i == ilist.Count - 1 || i == 9);
                    if (ilist.Count > 10)
                        Console.WriteLine($"{childIndent}  └── ... ({ilist.Count - 10} more)");
                    break;
                }

            default:
                {
                    switch (value)
                    {
                        case Array arr:
                            {
                                Console.WriteLine($"{prefix}{name}: {value.GetType().Name} [{arr.Length}]");
                                var childIndent = indent + (isLast ? "     " : "  │  ");
                                for (var i = 0; i < arr.Length && i < 10; i++)
                                    PrintTree($"[{i}]", arr.GetValue(i), childIndent, i == arr.Length - 1 || i == 9);
                                break;
                            }
                        case IDictionary iDictionary:
                            {
                                Console.WriteLine($"{prefix}{name}: Dictionary ({iDictionary.Count} entries)");
                                var childIndent = indent + (isLast ? "     " : "  │  ");
                                var keys = iDictionary.Keys;
                                var idx = 0;
                                foreach (var k in keys)
                                {
                                    if (idx >= 10)
                                        break;
                                    if (k != null)
                                        PrintTree(k.ToString() ?? "?", iDictionary[k], childIndent, idx == keys.Count - 1 || idx == 9);
                                    idx++;
                                }
                                if (keys.Count > 10)
                                    Console.WriteLine($"{childIndent}  └── ... ({keys.Count - 10} more)");
                                break;
                            }
                        default:
                            Console.WriteLine($"{prefix}{name}: {value}");
                            break;
                    }

                    break;
                }
        }
    }

    private static string CategorizeValue(object? val, string name = "", bool isBuiltin = false)
    {
        switch (val)
        {
            case null:
                return "null";
            case Type:
                return "types";
            case IScriptList:
                return isBuiltin ? "lists" : "user";
            case IScriptDictionary:
                return isBuiltin ? "dictionaries" : "user";
            case ClassInfo:
                return "classes";
            case ScriptFunction:
                return isBuiltin ? "functions" : "user";
            case Namespace:
                return "namespaces";
            case DelegateInfo:
                return "delegates";
            case StructDeclStmt:
                return isBuiltin ? "structs" : "user";
            case INativeFunction:
                return "native";
            case RefCell:
                return "ref cells";
            case Interpreter.EventInfo:
                return "events";
            case ClassInstance:
            case StructInstance:
                return "user";
        }

        var typeName = val.GetType().Name;
        if (typeName.EndsWith("List")) return isBuiltin ? "lists" : "user";
        if (typeName.EndsWith("Dictionary")) return isBuiltin ? "dictionaries" : "user";
        return isBuiltin ? "other" : "user";
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes / (1024.0 * 1024.0):F2} MB"
        };
    }

    private static void PrintInfos()
    {
        string? fileVer = null;
        var buildTime = DateTime.MinValue;
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (Directory.Exists(baseDir))
            {
                var exePath = Path.Combine(baseDir, AppDomain.CurrentDomain.FriendlyName + ".exe");
                if (File.Exists(exePath))
                {
                    var fvi = FileVersionInfo.GetVersionInfo(exePath);
                    fileVer = fvi.FileVersion;
                }
                buildTime = File.GetLastWriteTime(baseDir);
            }
        }
        catch
        {
            // ignored
        }

        ConsoleSafe.SetColor(ConsoleColor.Yellow);
        Console.WriteLine("ScriptLangCore, Informations");
        ConsoleSafe.ResetColor();
        Console.WriteLine($"  Version:       {Version}");
        Console.WriteLine($"  File version:  {fileVer ?? "N/A"}");
        Console.WriteLine($"  Build date:    {buildTime:yyyy-MM-dd HH:mm:ss}");
#if DEBUG
        Console.WriteLine($"  Configuration: Debug");
#else
        Console.WriteLine($"  Configuration: Release");
#endif
        // platform: diagnostic, reports which build is running so a divergence is observable rather than inferred. The differential gate compares per-suite statuses via --json, and normalises this banner.
        var isDynamic = RuntimeFeature.IsDynamicCodeSupported;
        Console.WriteLine($"  AOT compiled:  {(isDynamic ? "No" : "Yes")}");
        Console.WriteLine($"  Runtime:       {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"  OS:            {RuntimeInformation.OSDescription}");
        Console.WriteLine($"  Process:       {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            var isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            Console.WriteLine($"  Admin:         {(isAdmin ? "Yes" : "No")}");
        }
        catch { Console.WriteLine($"  Admin:         N/A"); }
        Console.WriteLine();
        ConsoleSafe.SetColor(ConsoleColor.DarkGray);
        Console.WriteLine("  ScriptLangCore is the copyrighted property of Gerome GUILLEMIN (2026).");
        ConsoleSafe.ResetColor();
        Console.WriteLine();
    }
}
