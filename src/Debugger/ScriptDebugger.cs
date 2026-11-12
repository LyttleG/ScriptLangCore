namespace ScriptLangCore;

internal class ScriptDebugger
{
    private readonly HashSet<int> _breakpoints = [];
    private DebuggerState _state = DebuggerState.Running;
    private Interpreter? _interpreter;
    private readonly Lock _lock = new();
    private int _stepOverDepth;  // call-depth at which StepOver started
    private int _stepOutDepth;   // call-depth at which StepOut started

    public event Action<Interpreter, Stmt>? Paused;

    public bool IsEnabled { get; set; } = true;
    public bool BreakOnFirstStatement { get; init; }
    private bool _firstStatementSeen;

    public ScriptDebugger()
    {
        var consoleAvailable = GetConsoleWindow() != IntPtr.Zero;
        if (!consoleAvailable)
        {
            // Disable interactive features; breakpoints will be ignored.
            IsEnabled = false;
        }
    }

    public bool ShouldBreak(Stmt stmt)
    {
        if (!IsEnabled) return false;
        lock (_lock)
        {
            if (BreakOnFirstStatement && !_firstStatementSeen)
            {
                _firstStatementSeen = true;
                _state = DebuggerState.Paused;
                return true;
            }

            switch (_state)
            {
                case DebuggerState.Running when _breakpoints.Contains(stmt.Line):
                case DebuggerState.StepInto:
                    _state = DebuggerState.Paused;
                    return true;

                case DebuggerState.StepOver:
                    {
                        // Break only when we are back at or above the caller's depth
                        var currentDepth = _interpreter?.CallDepth ?? 0;
                        if (currentDepth <= _stepOverDepth)
                        {
                            _state = DebuggerState.Paused;
                            return true;
                        }
                        return false;
                    }

                case DebuggerState.StepOut:
                    {
                        // Break only when we've exited the current function (depth < entry depth)
                        var currentDepth = _interpreter?.CallDepth ?? 0;
                        if (currentDepth < _stepOutDepth)
                        {
                            _state = DebuggerState.Paused;
                            return true;
                        }

                        break;
                    }
            }

            return false;
        }
    }

    public void Break(Interpreter interpreter, Stmt stmt)
    {
        lock (_lock)
        {
            _interpreter = interpreter;
            _state = DebuggerState.Paused;
        }

        Paused?.Invoke(interpreter, stmt);

        ConsoleSafe.SetColor(ConsoleColor.Yellow);
        Console.WriteLine($"\n[Break] Line {stmt.Line}: {GetStatementPreview(stmt)}");
        ConsoleSafe.ResetColor();

        // Synchronous command loop – runs on the interpreter thread
        CommandLoop();
    }

    private void CommandLoop()
    {
        Span<Range> ranges = stackalloc Range[8];
        while (true)
        {
            lock (_lock)
            {
                if (_state == DebuggerState.Running || _state == DebuggerState.StepOver || _state == DebuggerState.StepInto)
                    break;
            }

            ConsoleSafe.SetColor(ConsoleColor.Cyan);
            Console.Write("debug>");
            ConsoleSafe.ResetColor();

            var line = Console.ReadLine();
            if (line == null) continue;
            var trimmed = line.AsSpan().Trim();
            if (trimmed.IsEmpty) continue;

            var count = trimmed.Split(ranges, ' ', StringSplitOptions.RemoveEmptyEntries);
            if (count == 0) continue;

            var cmdSpan = trimmed[ranges[0]];
            try
            {
                if (cmdSpan.Equals("b", StringComparison.OrdinalIgnoreCase) ||
                    cmdSpan.Equals("break", StringComparison.OrdinalIgnoreCase))
                {
                    if (count >= 2 && int.TryParse(trimmed[ranges[1]], out var bLine))
                        SetBreakpoint(bLine);
                    else
                        Console.WriteLine("Usage: b <line>");
                }
                else if (cmdSpan.Equals("c", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("continue", StringComparison.OrdinalIgnoreCase))
                {
                    Continue();
                }
                else if (cmdSpan.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("step", StringComparison.OrdinalIgnoreCase))
                {
                    StepOver();
                }
                else if (cmdSpan.Equals("si", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("stepinto", StringComparison.OrdinalIgnoreCase))
                {
                    StepInto();
                }
                else if (cmdSpan.Equals("so", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("stepout", StringComparison.OrdinalIgnoreCase))
                {
                    StepOut();
                }
                else if (cmdSpan.Equals("p", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("print", StringComparison.OrdinalIgnoreCase))
                {
                    PrintVariables();
                }
                else if (cmdSpan.Equals("e", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("eval", StringComparison.OrdinalIgnoreCase))
                {
                    if (count >= 2)
                    {
                        var exprText = trimmed[(ranges[1].Start.Value)..].ToString();
                        EvalExpression(exprText);
                    }
                    else
                        Console.WriteLine("Usage: e <expression>  |  eval <expression>");
                }
                else if (cmdSpan.Equals("bt", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("backtrace", StringComparison.OrdinalIgnoreCase))
                {
                    PrintCallStack();
                }
                else if (cmdSpan.Equals("lb", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    ListBreakpoints();
                }
                else if (cmdSpan.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("quit", StringComparison.OrdinalIgnoreCase))
                {
                    var exitCode = Environment.ExitCode;
                    ConsoleSafe.SetColor(ConsoleColor.Cyan);
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    Console.WriteLine($"< Debug session terminated with code {exitCode}.");
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    ConsoleSafe.ResetColor();
                    Environment.Exit(exitCode);
                }
                else if (cmdSpan.Equals("h", StringComparison.OrdinalIgnoreCase) ||
                         cmdSpan.Equals("help", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Debugger Commands:");
                    Console.WriteLine("  b <line>      - Set a breakpoint at the specified line number.");
                    Console.WriteLine("  lb            - List all active breakpoints.");
                    Console.WriteLine("  c / continue  - Continue execution until next breakpoint or end.");
                    Console.WriteLine("  s / step      - Step over the current statement (do not enter function calls).");
                    Console.WriteLine("  si / stepinto - Step into function calls.");
                    Console.WriteLine("  so / stepout  - Continue until the current function returns.");
                    Console.WriteLine("  p / print     - Print user variables in the current scope.");
                    Console.WriteLine("  e / eval <expr> - Evaluate a script expression and print the result.");
                    Console.WriteLine("  bt            - Display the current call stack (backtrace).");
                    Console.WriteLine("  q / quit      - Quit the debugger and terminate the application.");
                    Console.WriteLine("  h / help      - Show this help message.");
                }
                else
                {
                    Console.Write("Unknown command: ");
                    Console.WriteLine(cmdSpan);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            NativeApp.DoEvents();
        }
    }

    public void SetBreakpoint(int line)
    {
        lock (_lock) _breakpoints.Add(line);
        Console.WriteLine($"Breakpoint set at line {line}");
    }

    public void RemoveBreakpoint(int line)
    {
        lock (_lock) _breakpoints.Remove(line);
        Console.WriteLine($"Breakpoint removed at line {line}");
    }

    private void ListBreakpoints()
    {
        lock (_lock)
        {
            if (_breakpoints.Count == 0)
                Console.WriteLine("No breakpoints set.");
            else
                Console.WriteLine("Breakpoints: " + string.Join(", ", _breakpoints.OrderBy(x => x)));
        }
    }

    private void Continue()
    {
        lock (_lock) _state = DebuggerState.Running;
    }

    private void StepOver()
    {
        lock (_lock)
        {
            _state = DebuggerState.StepOver;
            _stepOverDepth = _interpreter?.CallDepth ?? 0;
        }
    }

    private void StepInto()
    {
        lock (_lock) _state = DebuggerState.StepInto;
    }

    private void StepOut()
    {
        lock (_lock)
        {
            _state = DebuggerState.StepOut;
            _stepOutDepth = _interpreter?.CallDepth ?? 0;
        }
    }

    private void PrintVariables(bool showSystem = false)
    {
        var scopes = _interpreter?.GetCurrentScopes();
        if (scopes == null || scopes.Count == 0)
        {
            Console.WriteLine("No variables in scope.");
            return;
        }

        Console.WriteLine("Variables:");
        var level = 0;
        foreach (var scope in scopes.Reverse())
        {
            level++;
            var entries = scope
                .Where(kv => showSystem || !SystemNames.Contains(kv.Key))
                .ToList();

            // Categorise
            var namespaces = entries.Where(kv => kv.Value is Namespace).OrderBy(kv => kv.Key).ToList();
            var natives = entries.Where(kv => kv.Value is NativeFunctionAdapter).OrderBy(kv => kv.Key).ToList();
            //List<KeyValuePair<string, object>> others = entries.Except(namespaces).Except(natives)/*.OrderBy(kv => kv.Key)*/.ToList();
            var namespaceKeys = namespaces.Select(e => e.Key).ToHashSet();
            var nativeKeys = natives.Select(e => e.Key).ToHashSet();
            var others = entries
                .Where(e => !namespaceKeys.Contains(e.Key) && !nativeKeys.Contains(e.Key))
                .ToList();

            PrintGroup(namespaces);
            PrintGroup(natives);
            PrintGroup(others);
            continue;

            void PrintGroup(IEnumerable<KeyValuePair<string, object>> group)
            {
                var i = 0;
                foreach (var kv in group)
                {
                    var typeName = GetTypeNameFromValue(kv.Value, kv.Key);
                    var valueStr = FormatValue(kv.Value);
                    var lineCol = GetLineColumnSuffix(kv.Value);

                    // System items: Namespace, Native, and built-in Types
                    var isSystemItem = kv.Value is Namespace or NativeFunctionAdapter or Type;

                    ConsoleSafe.SetColor(i++ % 2 == 0 ? ConsoleColor.DarkGreen : ConsoleColor.DarkYellow); // color;

                    if (!isSystemItem && !IsConstantName(kv.Key))
                        Console.WriteLine($"  [{level}] Type: {typeName}, Name: {kv.Key}, Value: {valueStr}{lineCol}");

                    ConsoleSafe.ResetColor();
                }
            }
        }
    }

    private void EvalExpression(string expr)
    {
        try
        {
            var result = _interpreter?.EvaluateDebugExpression(expr, 1, 0);
            PrintEvalResult(expr, result);
        }
        catch (ScriptException ex)
        {
            ConsoleSafe.SetColor(ConsoleColor.Red);
            Console.WriteLine($"Error: {ex.Message}");
            ConsoleSafe.ResetColor();
        }
        catch (Exception ex)
        {
            ConsoleSafe.SetColor(ConsoleColor.Red);
            Console.WriteLine($"Unexpected error: {ex.Message}");
            ConsoleSafe.ResetColor();
        }
    }

    private void PrintEvalResult(string expr, object? result)
    {
        // Print expression in default color
        Console.Write(expr);
        Console.Write(" → ");

        // Print formatted value in green
        ConsoleSafe.SetColor(ConsoleColor.Green);

        var formatted = result switch
        {
            null => "null",
            string s => FormatEvalString(s),
            bool b => b ? "true (bool)" : "false (bool)",
            int i => $"{i} (int)",
            long l => $"{l} (long)",
            float f => $"{f} (float)",
            double d => $"{d} (double)",
            decimal m => $"{m} (decimal)",
            Array a => FormatEvalArray(a),
            IList list => FormatEvalList(list),
            IDictionary dict => FormatEvalDictionary(dict),
            ClassInstance ci => FormatEvalClassInstance(ci),
            StructInstance si => FormatEvalStructInstance(si),
            ScriptFunction sf => string.IsNullOrEmpty(sf.Declaration.Name) ? "<lambda>" : $"Function: {sf.Declaration.Name} (line {sf.Declaration.Line})",
            DelegateInfo di => $"Delegate: {di.Name} (line {di.Line})",
            Namespace => "[Namespace]",
            NativeFunctionAdapter nf => $"[Native: {nf.Name}]",
            Type t => GetCleanTypeName(t),
            _ => result.ToString() ?? "null"
        };

        Console.WriteLine(formatted);
        ConsoleSafe.ResetColor();
    }

    private static string FormatEvalString(string s)
    {
        const int maxLen = 80;
        if (s.Length <= maxLen)
            return $"\"{s}\" (string, len={s.Length})";

        var truncated = s[..(maxLen - 3)] + "...";
        return $"\"{truncated}\" (string, len={s.Length})";
    }

    private static string FormatEvalArray(Array arr)
    {
        var elemType = arr.GetType().GetElementType()?.Name ?? "?";
        var preview = string.Join(", ", arr.Cast<object?>().Take(5).Select(FormatEvalElement));
        if (arr.Length > 5) preview += ", ...";
        return $"{elemType}[] (length={arr.Length}): [{preview}]";
    }

    private static string FormatEvalList(IList list)
    {
        var typeName = list.GetType().Name;
        var preview = string.Join(", ", list.Cast<object?>().Take(5).Select(FormatEvalElement));
        if (list.Count > 5) preview += ", ...";
        return $"{typeName} (count={list.Count}): [{preview}]";
    }

    private static string FormatEvalDictionary(IDictionary dict)
    {
        var typeName = dict.GetType().Name;
        var items = dict.Keys.Cast<object?>().Take(5).Select(k =>
        {
            var v = k == null ? default : dict[k];
            return $"{FormatEvalElement(k)}={FormatEvalElement(v)}";
        });
        var preview = string.Join(", ", items);
        if (dict.Count > 5) preview += ", ...";
        return $"{typeName} (count={dict.Count}): {{{preview}}}";
    }

    private static string FormatEvalClassInstance(ClassInstance ci)
    {
        var fields = ci.FieldValues?
            .Take(5)
            .Select(kv => $"{kv.Key} = {FormatEvalElement(kv.Value)}");
        var preview = fields != null ? string.Join(", ", fields) : "";
        if (ci.FieldValues?.Count > 5) preview += ", ...";
        return $"{ci.Class.Name} {{{preview}}}";
    }

    private static string FormatEvalStructInstance(StructInstance si)
    {
        var fields = si.Fields?
            .Take(5)
            .Select(kv => $"{kv.Key} = {FormatEvalElement(kv.Value)}");
        var preview = fields != null ? string.Join(", ", fields) : "";
        if (si.Fields?.Count > 5) preview += ", ...";
        return $"{si.StructName} {{{preview}}}";
    }

    private static string FormatEvalElement(object? value) => value switch
    {
        null => "null",
        string s => s.Length > 40 ? $"\"{s[..37]}...\"" : $"\"{s}\"",
        bool b => b ? "true" : "false",
        _ => value.ToString() ?? "null"
    };

    private static bool IsConstantName(string name)
    {
        if (name.All(c => char.IsUpper(c) || c == '_'))
            return true;

        ReadOnlySpan<string> constantPrefixes =
        [
            "WM_", "WS_", "VK_", "MB_", "ID_",
            "LVM_", "TVM_", "TCM_", "SB_", "TB_", "DTM_", "NM_", "LVN_",
            "TVN_", "TCN_", "TBN_", "DTN_", "COLOR_", "CS_", "ES_", "BS_",
            "LBS_", "CBS_", "SS_", "ICC_", "WC_"
        ];

        foreach (var prefix in constantPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) // don't touch at it
                return true;
        }

        return false;
    }

    private static string GetLineColumnSuffix(object? value)
    {
        (int? line, int? col) = value switch
        {
            ScriptFunction sf => (sf.Declaration.Line, sf.Declaration.Column),
            StructInstance si => (si.Line, null)!,
            DelegateInfo di => (di.Line, di.Column),
            ClassInfo ci => (ci.LineNumber, null)!,
            ClassInstance ci => (ci.Class.LineNumber, null)!,
            _ => ((int?)null, (int?)null)
        };

        return col is > 0
            ? $", Line: {line}, Column: {col}"
            : $", Line: {line}";
    }

    private void PrintCallStack()
    {
        var stack = _interpreter?.GetCallStack();
        if (stack == null || stack.Count == 0)
        {
            Console.WriteLine("Call stack empty.");
            return;
        }

        Console.WriteLine("Call Stack:");
        for (var i = 0; i < stack.Count; i++)
        {
            Console.WriteLine($"  {i}: {stack[i]}");
        }
    }

    private static string GetStatementPreview(Stmt stmt) => stmt switch
    {
        ExprStmt e => GetExpressionPreview(e.Expression),
        VarDeclStmt v => $"var {v.Name} = {GetExpressionPreview(v.Initializer)}",
        IfStmt => "if (...)",
        WhileStmt => "while (...)",
        ReturnStmt r => r.Value != null ? $"return {GetExpressionPreview(r.Value)}" : "return",
        DebuggerStmt => "debugger;",
        _ => stmt.GetType().Name
    };

    private static string GetExpressionPreview(Expr? expr) => expr switch
    {
        null => "null",
        CallExpr c => $"{GetExpressionPreview(c.Callee)}({FormatArguments(c.Arguments)})",
        VariableExpr v => v.Name,
        StringExpr s => $"\"{s.Value}\"",
        NumberExpr n => n.Value.ToString(CultureInfo.InvariantCulture),
        BoolExpr b => b.Value ? "true" : "false",
        NullExpr => "null",
        BinaryExpr b => $"{GetExpressionPreview(b.Left)} {OperatorToString(b.Operator)} {GetExpressionPreview(b.Right)}",
        UnaryExpr u => $"{OperatorToString(u.Operator)}{GetExpressionPreview(u.Right)}",
        PropertyAccessExpr p => $"{GetExpressionPreview(p.Target)}.{p.PropertyName}",
        _ => expr.GetType().Name
    };

    private static string FormatArguments(IReadOnlyList<Expr> args)
    {
        if (args.Count == 0) return "";
        return string.Join(", ", args.Select(GetExpressionPreview));
    }

    private static string OperatorToString(Token op) => op.Type switch
    {
        TokenType.Plus => "+",
        TokenType.Minus => "-",
        TokenType.Star => "*",
        TokenType.Slash => "/",
        TokenType.EqualEqual => "==",
        TokenType.BangEqual => "!=",
        TokenType.Less => "<",
        TokenType.Greater => ">",
        TokenType.LessEqual => "<=",
        TokenType.GreaterEqual => ">=",
        TokenType.AndAnd => "&&",
        TokenType.OrOr => "||",
        _ => op.Literal
    };

    private static string FormatValue(object? value)
    {
        var baseValue = value switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            Type t => GetCleanTypeName(t),
            Control c => string.IsNullOrEmpty(c.Text) ? "(empty)" : c.Text,
            ScriptFunction sf => string.IsNullOrEmpty(sf.Declaration.Name) ? "<lambda>" : sf.Declaration.Name,
            StructInstance si => $"{si.StructName} {{ {FormatStructFields(si.Fields)} }}",
            ClassInstance ci => $"{ci.Class.Name} {{...}}",
            ClassInfo ci => ci.Name,
            DelegateInfo di => di.Name,
            Namespace => "[Namespace]",
            NativeFunctionAdapter nf => $"[Native: {nf.Name}]",
            _ => value.ToString() ?? "null"
        };

        return baseValue;
    }

    private static string FormatStructFields(Dictionary<string, object?> fields)
    {
        var items = fields.Select(kv => $"{kv.Key} = {FormatValue(kv.Value)}");
        return string.Join(", ", items);
    }

    private static string GetTypeNameFromValue(object? value, string variableName) => value switch
    {
        null => IsSystemName(variableName) ? "null" : "Object?",
        Control c => c.GetType().Name,
        Namespace => nameof(Namespace),
        NativeFunctionAdapter => "Native",
        ScriptFunction sf => string.IsNullOrEmpty(sf.Declaration.Name) ? "Lambda" : "Function",
        Delegate => nameof(Delegate),
        Type t => GetCleanTypeName(t),
        StructInstance => "Struct",
        ClassInfo => "Class",
        _ => value.GetType().Name
    };

    private static bool IsSystemName(string name) => SystemNames.Contains(name);

    private static string GetCleanTypeName(Type t)
    {
        var name = t.Name;

        // Remove outer class qualifier (e.g., Interpreter+Form -> Form)
        var plusIndex = name.IndexOf('+');
        if (plusIndex >= 0)
            name = name[(plusIndex + 1)..];

        // Remove generic arity marker (e.g., List`1 -> List)
        var backtick = name.IndexOf('`');
        if (backtick >= 0)
            name = name[..backtick];

        return name;
    }

    /// <summary>The names a scope listing hides unless the caller asks for system entries. TWO of them were
    /// STALE and are corrected here: `WinApi` (the namespace was DISMANTLED, so nothing binds that name any more)
    /// and `String` (a CLR-cased spelling the engine does not register: the script name is `string`, which is what
    /// this list carries now, the same way `File`/`Math`/`System` are the names a script writes).</summary>
    private static readonly HashSet<string> SystemNames =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "Application", "CancelEventArgs", "Control", "Form", "Size", "StartPosition",
        "ShowConsole", "System", "Math", "File", "DateTime", "DateTimeOffset",
        "TypeCode", "StringComparison", "string", "Convert", "Script",
        "Sleep", "print", "Pause", "exit", "exitcode", "typeof"
    ];
}

internal enum DebuggerState
{
    Running,
    Paused,
    StepOver,
    StepInto,
    StepOut
}