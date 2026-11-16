namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    /// Enables the script debugger and configures whether execution should pause at the first statement.
    /// </summary>
    /// <remarks>When the debugger is enabled, users can interact with the debugging session. Type 'h' for
    /// help when the debugger is paused.</remarks>
    /// <param name="breakOnFirst">A value indicating whether the debugger should automatically break on the first statement. <br/> Set to <see
    /// langword="true"/> to pause execution immediately; otherwise, execution continues until a breakpoint is hit.</param>
    public static void EnableDebugger(bool breakOnFirst)
    {
        _debugger = new ScriptDebugger { BreakOnFirstStatement = breakOnFirst };
        //ConsoleSafe.SetColor(ConsoleColor.Cyan);
        //    ? $"> Debugger enabled (auto-pause: {(breakOnFirst ? "yes" : "no")}). Type 'h' or'help' for help when paused."
        //    : "Warning: Debugger disabled ï¿½ no console available.");
        //ConsoleSafe.ResetColor();
    }

    public static void DisableDebugger()
    {
        _debugger = null;
        Console.WriteLine("Debugger disabled.");
    }

    public static bool IsDebuggerEnabled => _debugger?.IsEnabled ?? false;

    public static void SetBreakpoint(int line)
    {
        if (_debugger == null) { Console.WriteLine("Debugger not enabled. Use .debug on first."); return; }
        _debugger.SetBreakpoint(line);
    }

    // Expose for debugger
    public IReadOnlyList<Dictionary<string, object>> GetCurrentScopes() => CurrentFrame.Scopes.CaptureSnapshot();
    public IReadOnlyList<string> GetCallStack() => [.. _callStack];

    /// <summary>
    /// Parses and evaluates a script expression string in the current scope.
    /// Designed for the interactive debugger, assumes the interpreter is
    /// paused at a breakpoint and the current frame/scope is valid.
    /// </summary>
    public object? EvaluateDebugExpression(string expression, int baseLine, int baseCol)
    {
        var lexer = new Lexer(expression.AsMemory());
        var tokens = lexer.GetTokens().ToList();
        var parser = new Parser(tokens, DelegateInfos, _caseSensitive, source: expression);
        var expr = parser.ParseInlineExpression(expression, baseLine, baseCol);
        return Evaluate(expr);
    }

    /// <summary>Returns user-defined global variables with name, type, line, column.</summary>
    public IReadOnlyList<GlobalVarInfo> GetGlobalVariableNames()
    {
        var dict = _rootFrame.Scopes.GetGlobalScopeDictionary();
        var result = new List<GlobalVarInfo>();
        foreach (var kvp in dict)
        {
            if (_builtinNames.Contains(kvp.Key)) continue;
            var vi = kvp.Value;
            var value = vi.Value;
            if (value is Type || value is TypeInfo || value is ClassInfo ||
                value is DelegateInfo || value is Namespace ||
                value is INativeFunction || value is RefCell ||
                value is ScriptFunction || value is StructDeclStmt ||
                value is EventInfo)
                continue;
            var typeName = string.Empty;
            switch (value)
            {
                case int:
                    typeName = "int";
                    break;
                case long:
                    typeName = "long";
                    break;
                case double:
                    typeName = "double";
                    break;
                case float:
                    typeName = "float";
                    break;
                case bool:
                    typeName = "bool";
                    break;
                case string:
                    typeName = "string";
                    break;
                case List<int>:
                    typeName = "List<int>";
                    break;
                case IScriptList sl:
                    {
                        var bt = sl.ElementType.BaseType.ToString();
                        typeName = $"List<{TypeSystem.FromClrName(bt)}>";
                        break;
                    }
                case IScriptDictionary sd:
                    {
                        var kt = sd.KeyType.TypeName ?? sd.KeyType.BaseType.ToString();
                        var vt = sd.ValueType.TypeName ?? sd.ValueType.BaseType.ToString();
                        typeName = $"Dictionary<{TypeSystem.FromClrName(kt)}, {TypeSystem.FromClrName(vt)}>";
                        break;
                    }
                case ClassInstance ci:
                    typeName = ci.Class.Name;
                    break;
                case StructInstance st:
                    typeName = st.StructName;
                    break;
                default:
                    typeName = value.GetType().Name;
                    break;
            }
            result.Add(new GlobalVarInfo(kvp.Key, typeName, vi.Line, vi.Column));
        }
        return result;
    }

    /// <summary>Dumps ALL global scope keys (including built-ins) for diagnostics.</summary>
    public IReadOnlyList<string> DebugGlobalKeys()
    {
        var dict = _rootFrame.Scopes.GetGlobalScopeDictionary();
        return [.. dict.Keys];
    }

    public int DebugScopeCount() => _rootFrame.Scopes.Count;

    public IReadOnlyList<GlobalVarInfo> GetUserDeclarations()
    {
        var dict = _rootFrame.Scopes.GetGlobalScopeDictionary();
        var result = new List<GlobalVarInfo>();
        foreach (var kvp in dict)
        {
            if (_builtinNames.Contains(kvp.Key)) continue;
            var vi = kvp.Value;
            var line = vi.Line;
            var col = vi.Column;

            if (kvp.Value.Value is ScriptFunction fn)
            {
                var rawType = fn.ReturnType?.TypeName ?? fn.ReturnType?.BaseType.ToString() ?? "void";
                var retType = rawType switch
                {
                    "Byte" => "byte",
                    "SByte" => "sbyte",
                    "Short" => "short",
                    "UShort" => "ushort",
                    "Int" => "int",
                    "UInt" => "uint",
                    "Long" => "long",
                    "ULong" => "ulong",
                    "Float" => "float",
                    "Double" => "double",
                    "Decimal" => "decimal",
                    "Bool" => "bool",
                    "Char" => "char",
                    "StringType" => "string",
                    "Object" => "object",
                    "Void" => "void",
                    _ => rawType
                };
                var kind = fn.IsCoroutine
                    ? "coroutine"
                    : (string.IsNullOrEmpty(fn.Declaration.Name) ? "lambda" : "function");

                result.Add(new GlobalVarInfo(kvp.Key, retType + " (" + kind + ")", fn.Declaration.Line, fn.Declaration.Column));
            }
            else if (kvp.Value.Value is ClassInfo ci)
            {
                result.Add(new GlobalVarInfo(kvp.Key, "class", ci.LineNumber, 0));
            }
            else if (kvp.Value.Value is StructDeclStmt sd)
            {
                result.Add(new GlobalVarInfo(kvp.Key, "struct", sd.Line, 0));
            }
        }
        return result;
    }
}
