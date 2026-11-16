namespace ScriptLangCore;

internal static partial class Program
{
    private static void RunRepl(string? afterRunScript = null, Interpreter? adopt = null) => RunReplCore(afterRunScript, adopt);

    /// <summary>THE SAME REPL, on an interpreter the CALLER owns, for the embedding ABI's `slc_repl`. Answers the code
    /// the session would exit with (a `.exit`/EOF leaves `Environment.ExitCode`, and a script's own `exit(n)` inside a
    /// REPL line is that run's verdict). The interpreter is ADOPTED rather than created, so ending the REPL does not
    /// dispose the host's session, and a dot command that would REPLACE it is refused with a line saying so, because
    /// the host holds a handle to that interpreter.</summary>
    internal static int RunReplOn(Interpreter itp)
    {
        RunReplCore(afterRunScript: null, adopt: itp);
        return Environment.ExitCode;
    }

    private static void RunReplCore(string? afterRunScript, Interpreter? adopt)
    {
        try { Console.Clear(); } catch { /* Ignore console clear errors */ }
        PrintBanner();

        _replLineOffset = 0;
        // Attached ONCE: a host may run this REPL more than a session's worth of times, and `+=` on a static event
        // from a long-lived process would otherwise accumulate handlers (the CLI happens to call it once).
        OnInterpreterCreated -= Repl.DotCommands.ResetGcBaseline;
        OnInterpreterCreated += Repl.DotCommands.ResetGcBaseline;
        _replInterrupted = false;
        var replHandle = adopt is null
            ? InterpreterManager.Create(caseSensitive: true, context: "REPL")
            : InterpreterHandle.Adopt(adopt);
        var itp = replHandle.Instance;

        // The HTTP pipeline invokes `http.Use` middleware on one interpreter across requests, and this is the durable
        // one: it lives as long as the session, where a tool's interpreter is disposed once the tool has been loaded.
        // Handing it over explicitly is what makes a middleware registered here (or by `.run`) survive a request.
        McpServer.UseInterpreter(itp);

        var buffer = new StringBuilder();

        // Handle Ctrl+C, print message, interrupt running script, stay in REPL (like Python)
        var itp1 = itp;
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Repl.ReplInterrupt.Set();

            // Ctrl+C must NOT end an interactive REPL. It ends the SERVER this session was serving on, stops whatever
            // script is running, and hands the prompt back. The flag below is read by ONE place, the stdin-EOF branch,
            // and it is set only for a PIPED session (stdin redirected): there a null ReadLine really is end of input,
            // and a prompt that cannot be stopped is worse than one that stops. An interactive console read is ABORTED
            // by the key and answers null too, which is exactly why pressing it used to end the session.
            if (Console.IsInputRedirected) _replInterrupted = true;
            var stoppedServer = McpServer.StopHttpServer();
            _pendingInterruptNotice = stoppedServer
                ? "the MCP HTTP server has been stopped; load a script again (.run) to serve again."
                : "";
            itp1._interruptRequested = true;
        };

        // Auto-run a script if specified (used by -repl flag)
        if (afterRunScript != null)
            ExecuteReplFile(afterRunScript, itp);

        // Pre-populate history with common dotcommands for quick Up-arrow recall
        if (ReplHistory.Count == 0)
        {
            foreach (var cmd in new[] {
                ".help", ".vars", ".vars -v", ".run", ".looprun 10",
                ".reset", ".reset hard", ".gc collect", ".gc stats", ".state",
                ".type ", ".stats", ".search ", ".alias", ".edit ",
                ".eval ", ".export", ".export -bc", ".loadslcbc ", ".bench "
            })
                ReplHistory.Add(cmd);
        }

        try
        {
            while (true)
            {
                Repl.ReplInterrupt.Clear();

                // The interrupt notice is printed HERE, before the prompt, so it cannot race the prompt: this thread
                // wrote both lines, in order, and the newline closes the `>>> ` that was on screen when the key came.
                var notice = _pendingInterruptNotice;
                if (notice != null)
                {
                    _pendingInterruptNotice = null;
                    Console.WriteLine();
                    ConsoleSafe.SetColor(ConsoleColor.Yellow);
                    Console.WriteLine("⚠️  Interrupted by user, back at the prompt (type .exit to leave).");
                    if (notice.Length > 0) Console.WriteLine("   " + notice);
                    ConsoleSafe.ResetColor();
                }

                var prompt = buffer.Length == 0 ? ">>> " : "... ";
                ConsoleSafe.SetColor(ConsoleColor.Magenta);
                if (!Console.IsInputRedirected) Console.Write(prompt);   // a piped session is not a conversation
                ConsoleSafe.ResetColor();

                // Read line with Tab completion support
                var readLine = Console.ReadLine();
                if (readLine == null)
                {
                    // EOF on stdin: a piped session that has run its commands, or a closed console. This used to be
                    // read as an EMPTY line, so the loop printed a prompt for ever and (in a Debug build) echoed it:
                    // measured, a piped `--repl --mcp` run wrote megabytes of ">>> " and burned a core. An interpreter
                    // that is SERVING something has to stay up, so it waits here; with nothing left to read and
                    // nothing serving, the REPL ends, which is what a pipe expects.
                    // An interpreter that may be SERVING something has to stay up, and this loop never ended on its
                    // own before either (it spun, which is what the prompt spam and the busy core were), so waiting
                    // here changes nothing about liveness. Ctrl+C DOES have to end it, though: with nothing left to
                    // read there is no script to interrupt, and a prompt that cannot be stopped is worse than one
                    // that spins (measured: three "Interrupted by user" lines and no way out).
                    // INTERACTIVE: this is not end of input, it is the Ctrl+C key. Drop whatever was half typed and
                    // ask again, which is what a REPL does (reported: "CTRL+C stops the server but it also ends the
                    // REPL"). Only a piped session treats a null read as EOF, and waits below for the interrupt.
                    if (!Console.IsInputRedirected) { buffer.Clear(); continue; }

                    while (!_replInterrupted) System.Threading.Thread.Sleep(250);
                    break;
                }
                var line = readLine;

                // A Ctrl+C that arrives while a line is IN FLIGHT must not eat that line. The flag is raised by the
                // console handler, and by the time this runs a pasted line (or one typed just after the key) is a
                // COMPLETE line, so discarding it loses a command the user asked for: reported from a live session,
                // two `.run` lines came back as "Interrupted by user, back at the prompt" instead of running and only
                // the third took effect. What an interrupt is FOR is stopping what RUNS (the interpreter's own flag,
                // which `.run` and `sleep` poll) and stopping the server; the REPL only has to drop a HALF-TYPED line,
                // and an empty read with nothing accumulated is the one shape that is not a command. The flag is
                // always consumed, so it cannot leak into a later line either.
                var interrupted = Repl.ReplInterrupt.Take();
                if (interrupted && line.Length == 0 && buffer.Length == 0) { buffer.Clear(); continue; }

                // History recall: !N re-executes history entry N
                var isRecall = false;
                if (buffer.Length == 0 && line.StartsWith("!") && int.TryParse(line.AsSpan(1), out var histIdx) && histIdx >= 1 && histIdx <= ReplHistory.Count)
                {
                    line = ReplHistory[histIdx - 1];
                    ConsoleSafe.SetColor(ConsoleColor.Magenta);
                    Console.Write(">>> " + line);
                    ConsoleSafe.ResetColor();
                    Console.WriteLine();
                    _pendingEditLine = histIdx - 1;
                    isRecall = true;
                }

                // I/O redirection: > file (output) and < file (run script)

                if (buffer.Length == 0)
                {
                    var trimmedRedirect = line.Trim();

                    if (trimmedRedirect.Length > 0 && trimmedRedirect[0] == '>')
                    {
                        var filename = trimmedRedirect.Substring(1).Trim();

                        if (string.IsNullOrEmpty(filename) || filename.Equals("console", StringComparison.OrdinalIgnoreCase))
                        {
                            if (_redirectOutWriter != null)
                            {
                                _redirectOutWriter.Dispose();
                                _redirectOutWriter = null;
                                _redirectOutFile = null;
                                Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                                Interpreter.CaptureProcessStdout();
                                Console.WriteLine("[I/O] Output restored to console.");
                            }
                            else Console.WriteLine("[I/O] Output is already on console.");
                        }
                        else
                        {
                            _redirectOutWriter?.Dispose();
                            _redirectOutWriter = new StreamWriter(filename, append: false) { AutoFlush = true };
                            _redirectOutFile = filename;
                            Console.SetOut(_redirectOutWriter);
                            Console.WriteLine("[I/O] Output redirected to: " + filename + " (use > console to restore)");
                        }
                        continue;
                    }

                    if (trimmedRedirect.Length > 0 && trimmedRedirect[0] == '<')
                    {
                        var filename = trimmedRedirect.Substring(1).Trim();
                        if (string.IsNullOrEmpty(filename))
                        {
                            Console.WriteLine("[I/O] Usage: < filename  (executes the file in the REPL)");
                            continue;
                        }

                        if (!File.Exists(filename))
                        {
                            Console.WriteLine("[I/O] File not found: " + filename);
                            continue;
                        }

                        // Delegate to .run
                        line = ".run " + filename;
                        buffer.Clear();
                        buffer.Append(line);
                        continue;
                    }
                }

                // Dot commands only on fresh prompt
                if (buffer.Length == 0 && line.Trim().StartsWith("."))
                {
                    ConsoleSafe.SetColor(ConsoleColor.Cyan);
                    try
                    {
                        itp.RecordCommand(line.Trim());
                        if (ReplHistory.Count > 500) ReplHistory.RemoveAt(0);
                        ReplHistory.Remove(line.Trim());
                        ReplHistory.Add(line.Trim());
                        ConsoleSafe.ResetColor();
                        var trimmedLine = line.Trim();
                        replHandle = DispatchDotCommand(trimmedLine, replHandle);
                        itp = replHandle.Instance;
                        if (_exitRequested) break;
                    }
                    catch (Exception ex)
                    {
                        // A DOT COMMAND MUST NEVER END THE SESSION, whatever it was asked to do: the measured case was
                        // `.run <a .slcbc from another version>`, which threw out of HandleDotCommand and killed the
                        // process with an unhandled ScriptException and a stack trace. One line, the engine's own
                        // wording, and the prompt comes back. `.exit` is not affected (it sets a flag, it does not throw).
                        ReportBundleFailure(ex, setExitCodeOnFailure: false);
                    }
                    finally
                    {
                        ConsoleSafe.ResetColor();
                    }
                    continue;
                }

                buffer.AppendLine(line);

                // Check if braces are balanced
                if (BraceDepth(buffer.ToString()) > 0)
                    continue;

                var code = buffer.ToString();
                buffer.Clear();

                code = code.TrimEnd();
                if (code.Length == 0)
                {
                    // Still count blank lines for line offset
                    _replLineOffset++;
                    continue;
                }

                // How many LINES this block held, for the offset the next block's positions are shifted by. It asks
                // TextLines.Lines (no phantom last line, and the member path_resolution_test pins) rather than
                // TextLines.Count (the SEGMENT convention a RichEdit agrees with): the code above already trimmed the
                // trailing break, so the two agree here, and picking the one that means what this line wants is what
                // keeps the arithmetic honest if that trim ever moves.
                var lineCount = TextLines.Lines(code);

                // Track history (before offset blanks are prepended)
                var cleanCode = code.TrimEnd('\n', '\r');
                if (cleanCode.Length == 0) continue;
                if (!isRecall)
                {
                    if (_pendingEditLine >= 0 && _pendingEditLine < ReplHistory.Count)
                    {
                        ReplHistory[_pendingEditLine] = cleanCode;
                        _pendingEditLine = -1;
                    }
                    else
                        ReplHistory.Remove(cleanCode);
                    ReplHistory.Add(cleanCode);
                }

                // Prepend blank lines to offset line numbers from original paste
                if (_replLineOffset > 0)
                    code = new string('\n', _replLineOffset) + code;
                if (ReplHistory.Count > 500) ReplHistory.RemoveAt(0);

                // Echo mode
                if (_echoMode)
                {
                    ConsoleSafe.SetColor(ConsoleColor.Magenta);
                    Console.Write(">>> ");
                    ConsoleSafe.ResetColor();
                    Console.WriteLine(code);
                }

                var sw = _timingMode ? Stopwatch.StartNew() : null;

                // ReplGuard, boundary guard for a single REPL command: any exception
                // escaping the command body is caught here, reported as ONE concise
                // error line (no stack trace), the interpreter session is wiped via
                // ResetSession(), and the REPL loop continues instead of exiting.
                try
                {
                    try
                    {
                        var lexer = new Lexer(code.AsMemory());
                        var tokens = lexer.GetTokens();
                        var parser = new Parser(tokens, itp.Delegates, source: code);
                        var statements = parser.GetStatements();//.ToList();
                        var result = itp.Interpret(statements, "<repl>");

                        // Capture the return value so we can use it after Reset
                        object? returnValue = null;

                        if (result.HasError)
                        {
                            // Interpret folds script exceptions into ScriptResult.HasError
                            // rather than throwing, report concisely and wipe the whole
                            // session (ResetSession) so a failed command can't leave stale
                            // globals or call-stack state behind for the next command.
                            var msg = ScriptFailureText(result, withLocation: false);
                            ConsoleSafe.SetColor(ConsoleColor.Red);
                            Console.WriteLine("Error: " + msg);
                            ConsoleSafe.ResetColor();
                            itp.ResetSession();
                        }
                        else if (result.ReturnValue != null && result.ReturnValue is not ScriptResult)
                        {
                            returnValue = result.ReturnValue;
                            ConsoleSafe.SetColor(ConsoleColor.DarkGreen);
                            Console.WriteLine($"= {result.ReturnValue}");
                            ConsoleSafe.ResetColor();

                            if (itp._traceEnabled)
                                Console.WriteLine($"[trace] <repl> → {result.ReturnValue}");
                        }

                        _replLineOffset += lineCount;

                        var elapsed = sw?.Elapsed;
                        if (elapsed is { TotalMilliseconds: >= 0.01 })
                            Console.WriteLine($"  ({elapsed.Value.TotalMilliseconds:F2} ms)");

                        if (!result.HasError)
                        {
                            itp.Reset(keepGlobals: true);

                            // Store _last / _ for next expression use (after Reset so Define hits the live global scope)
                            if (returnValue != null)
                            {
                                var entries = new List<KeyValuePair<string, object?>>();
                                var prev = itp.GetVariableValue("_");
                                if (prev != null)
                                    entries.Add(new KeyValuePair<string, object?>("_last", prev));
                                entries.Add(new KeyValuePair<string, object?>("_", returnValue));
                                itp.DefineBlueprintEntries(entries);
                            }
                        }
                    }
                    catch (ScriptException ex)
                    {
                        if (ex.ErrorCode != ErrorCode.Interrupt)
                        {
                            ConsoleSafe.SetColor(ConsoleColor.Red);
                            Console.WriteLine("Error: " + ex.Message);
                            ConsoleSafe.ResetColor();
                            itp.ResetSession();
                        }
                        else
                        {
                            // Ctrl+C interrupt, already reported by the CancelKeyPress
                            // handler; just reset execution state (keep globals).
                            itp.Reset(keepGlobals: true);
                        }
                        if (_timingMode) Console.WriteLine($"  ({sw?.Elapsed.TotalMilliseconds:F2} ms)");
                    }
                    catch (Exception ex)
                    {
                        ConsoleSafe.SetColor(ConsoleColor.Red);
                        Console.WriteLine("Error: " + ex.GetType().Name + ": " + ex.Message);
                        ConsoleSafe.ResetColor();
                        itp.ResetSession();
                        if (_timingMode) Console.WriteLine($"  ({sw?.Elapsed.TotalMilliseconds:F2} ms)");
                    }

                    // Drain pending Task marshalling queue
                    Task.PumpPending();
                }
                catch (ScriptException sex)
                {
                    // ReplGuard outer boundary, only reached if an exception escapes the
                    // inner handling above; same concise report + full session wipe.
                    ConsoleSafe.SetColor(ConsoleColor.Red);
                    Console.WriteLine("Error: " + sex.Message);
                    ConsoleSafe.ResetColor();
                    itp.ResetSession();
                }
                catch (Exception ex)
                {
                    ConsoleSafe.SetColor(ConsoleColor.Red);
                    Console.WriteLine("Error: " + ex.GetType().Name + ": " + ex.Message);
                    ConsoleSafe.ResetColor();
                    itp.ResetSession();
                }
            }
        }
        finally
        {
            // A LEAK REPORT IS ABOUT THIS REPL'S OWN INTERPRETER. When one is ADOPTED the host still owns it, so
            // listing it as undisposed would accuse the host of the engine's own arrangement (measured: the launcher
            // printed `1 undisposed Interpreter(s): ... EmbedSession` for the interpreter it was about to free).
            var reportLeaks = replHandle.Owns;
            replHandle.Dispose();
            if (reportLeaks) InterpreterManager.ReportLeaks();
            Console.WriteLine();
            ConsoleSafe.SetColor(ConsoleColor.DarkYellow);
            Console.WriteLine("\U0001F44B Goodbye!");
            ConsoleSafe.ResetColor();
        }
    }

    // =========================================================================
    // DOT COMMANDS
    // =========================================================================
    /// <summary>THE ARGUMENTS AFTER `--` ON A `.run` LINE, split so a QUOTED path survives: `.run tool.slc -- "C:\Program
    /// Files\a.exe" b` reaches the script as two arguments, because a program's command line is text a shell would have
    /// quoted. Only the TAIL is treated this way, so nothing about `.run a.slc|b.slc -bc` changed.</summary>
    private static List<string> SplitScriptArguments(string line)
    {
        var args = new List<string>();
        var at = line.IndexOf(" -- ", StringComparison.Ordinal);
        var start = at >= 0 ? at + 4 : (line.EndsWith(" --", StringComparison.Ordinal) ? line.Length : -1);
        if (start < 0 || start >= line.Length) return args;
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var any = false;
        for (var i = start; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == quote) { quote = '\0'; } else { current.Append(ch); }
                any = true;
                continue;
            }
            if (ch == '"' || ch == '\'') { quote = ch; any = true; continue; }
            if (ch == ' ' || ch == '\t')
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(ch);
            any = true;
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    /// <summary>Reads a file into a pooled char buffer, applies StripDotCommands, and returns the memory.
    /// Caller must call ReleaseFileBuffer() after the Lexer is done.</summary>

    private static void ExecuteReplFile(string scriptPathFile, Interpreter itp, bool resetAfter = true)
    {
        var scriptPath = scriptPathFile.Replace('\\', '/');

        var (sourceMem, fileBuf) = FileReader.ReadFile(scriptPath);
        sourceMem = FileReader.StripDotCommands(sourceMem, ref _timingMode, ref _echoMode);
        ConsoleSafe.ResetColor();
        //Console.WriteLine($"  {Path.GetFileName(scriptPath)}");
        try
        {
            var lexer = new Lexer(sourceMem);
            var tokens = lexer.GetTokens();
            var parser = new Parser(tokens, itp.Delegates, source: sourceMem.ToString());
            var statements = parser.GetStatements().ToList();
            PrecompileFunctions(statements, itp);
            // A runtime failure is RETURNED, not thrown: `Interpret` folds it into the ScriptResult, so the catch
            // below only ever sees a LEXER or PARSER failure. Discarding that result is what made a `.run` file
            // print NOTHING for a failure the CLI reports for the same file, which is how a probe whose subject
            // named an undefined variable read as a broken test harness rather than as a failing script.
            var result = itp.Interpret(statements, scriptPath);
            if (result.HasError)
            {
                ConsoleSafe.SetColor(ConsoleColor.Red);
                Console.WriteLine("Error: " + ScriptFailureText(result, withLocation: true));
                ConsoleSafe.ResetColor();
            }
            if (resetAfter) itp.SetReplMode(true);
        }
        catch (Exception ex)
        {
            ConsoleSafe.SetColor(ConsoleColor.Red);
            Console.WriteLine($"Error: {ex.Message}");
            ConsoleSafe.ResetColor();
            if (resetAfter) itp.SetReplMode(true);
        }
    }
}
