namespace ScriptLangCore;

/// <summary>
/// Test-only bytecode dump (the <c>--dump-bytecode</c> flag): compile a script and print every
/// VM-compiled function's bytecode and tables, WITHOUT running it.
///
/// Why not reuse an existing export: every script-visible export path (Bytecode.ExportScript,
/// Bytecode.ExportLibrary, the .export -bc dotcommand) EXPORTS THE SESSION'S ALREADY-RUN state,
/// _scriptTopLevel is filled during Interpret (Interpreter.cs:3770-3793), so an export only sees what a
/// run produced. That makes it useless as a refactor baseline: every suite here ends in exit(), which
/// aborts the export, and a benchmark suite costs minutes. PrecompileFunctions is the same compile step
/// the normal CLI path performs after parsing (Program.cs:325), so dumping after it measures the real
/// emitter on the real input.
///
/// What is printed, and why in this shape: the CODE bytes are the artifact that must not change across
/// the format consolidation, and the tables are printed by name because a byte change made by adding a
/// constant is only interpretable next to the table it indexes. Object constants are summarised by type
/// name on purpose: their ToString is not a stable serialization, and a dump that varies run to run
/// cannot serve as a baseline.
/// </summary>
internal static partial class Program
{
    // The two codes `--dump-bytecode` answers with are the ENGINE's own, named once in ExitCodes.cs: ExitCode.Usage
    // when the input could not be read at all (tools/dump-bytecode.sh reads 2 as "did not dump at all", beside 124
    // meaning a timeout) and ExitCode.Refused when the input was refused (a body the verifier rejects, or a file
    // that does not parse; the script counts exactly 3 as `refused`, so that number is a contract with it).

    private static int DumpBytecode(string scriptPath, bool includeProgram = false)
    {
        var fullPath = Path.GetFullPath(scriptPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: file not found, {scriptPath}");
            return ExitCode.Usage;
        }

        var (sourceMem, _) = FileReader.ReadFile(fullPath);
        sourceMem = FileReader.StripDotCommands(sourceMem, ref _timingMode, ref _echoMode);

        using var itpHandle = InterpreterManager.Create(caseSensitive: true, context: "DumpBytecode");
        var itp = itpHandle.Instance;

        // The CLI runs case-sensitively; the dump must compile exactly what the CLI would compile.
        List<Stmt> statements;
        try
        {
            // The Parser's CONSTRUCTOR is what enumerates the lexer, so this one guard covers a lexer refusal (an
            // unterminated literal, an unknown escape) and a parser refusal alike.
            var lexer = new Lexer(sourceMem);
            var parser = new Parser(lexer.GetTokens(), itp.Delegates);
            statements = parser.GetStatements().ToList();
        }
        catch (ScriptException ex)
        {
            // A refusal is a verdict about the input, not a crash of the tool. Measured before this guard: a file whose
            // interpolation broke killed the dump with an unhandled exception and a stack trace, which turns "this file
            // does not parse" into a tool failure and takes the corpus sweep down with it.
            Console.Error.WriteLine($"[dump-bytecode] REFUSED {Path.GetFileName(fullPath)}: {ex.Message}");
            return ExitCode.Refused;
        }

        PrecompileFunctions(statements, itp);

        // The PROGRAM BODY is the file's top level compiled as the entry a bundle can carry (Compiler.CompileProgram).
        // It is reported in the summary so "does my file's top level compile?" has an answer without writing a
        // bundle, and dumped in full only with --program: the ordinary dump is the per-function baseline that the
        // corpus sweep compares byte for byte.
        itp.SetProgramStatements(statements);
        var programBody = itp.ProgramBytecode;

        var w = Console.Out;
        var dumped = 0;
        var refused = 0;
        foreach (var (name, bc) in CollectCompiled(statements))
        {
            DumpOne(w, name, bc);
            dumped++;
            // This dump is the corpus-wide check that BytecodeFormat's operand SIZES agree with real
            // emitted streams: the verifier walks each body through that table, so a wrong size shows up
            // here as a mis-decoded stream instead of waiting for someone to corrupt a bundle, which is
            // exactly how GET_PROP/SET_PROP were found.
            var reason = BytecodeVerifier.Verify(bc);
            if (reason != null)
            {
                Console.Error.WriteLine($"[dump-bytecode] REFUSED {name}: {reason}");
                refused++;
            }
        }
        if (programBody != null && includeProgram)
        {
            DumpOne(w, "__program__", programBody);
            dumped++;
            var bodyReason = BytecodeVerifier.Verify(programBody);
            if (bodyReason != null)
            {
                Console.Error.WriteLine($"[dump-bytecode] REFUSED __program__: {bodyReason}");
                refused++;
            }
        }
        var programNote = programBody == null
            ? $"program body: declined ({itp.ProgramBodyDeclineReason ?? "no statements"})"
            : $"program body: compiled (code={programBody.Code.Length} globalSlots={programBody.GlobalSlotNames?.Length ?? 0})";
        Console.Error.WriteLine($"[dump-bytecode] {Path.GetFileName(fullPath)}: {dumped} compiled function(s), {programNote}");
        return refused > 0 ? ExitCode.Refused : ExitCode.Ok;
    }

    /// <summary>Every VM-compiled declaration reachable from the parsed statements, in source order.
    /// The traversal mirrors PrecompileFunctions' own walk (top-level functions, class methods, nested
    /// classes) rather than reimplementing name resolution: a function the compiler declined to compile
    /// has no bytecode and is skipped, so the dump shows exactly what the VM would execute.</summary>
    private static IEnumerable<(string Name, Bytecode Code)> CollectCompiled(IEnumerable<Stmt> statements)
    {
        foreach (var stmt in Interpreter.FlattenNamespaceContainers(statements))
        {
            switch (stmt)
            {
                case FuncDecl f when f.Compiled != null:
                    yield return (f.Name, f.Compiled);
                    break;
                case ClassDeclStmt c:
                    foreach (var m in c.Methods)
                        if (m.Compiled != null) yield return ($"{c.Name}.{m.Name}", m.Compiled);
                    foreach (var nc in c.NestedClasses)
                        foreach (var entry in CollectCompiled([nc]))
                            yield return entry;
                    break;
            }
        }
    }

    private static void DumpOne(TextWriter w, string name, Bytecode bc)
    {
        var lambdas = bc.NestedLambdas?.Length ?? 0;
        var slots = bc.GlobalSlotNames?.Length ?? 0;
        w.WriteLine($"== {name} code={bc.Code.Length} consts={bc.Constants.Length} " +
                    $"locals={bc.Locals.Length} " +
                    $"constLocals={(bc.ConstLocals?.Length ?? 0)} lambdas={lambdas} globalSlots={slots} ==");

        w.WriteLine("CODE:");
        var sb = new StringBuilder(bc.Code.Length * 3);
        for (var i = 0; i < bc.Code.Length; i++)
        {
            sb.Append(bc.Code[i].ToString("x2", CultureInfo.InvariantCulture));
            if ((i & 15) == 15) { w.WriteLine(sb.ToString()); sb.Clear(); }
            else sb.Append(' ');
        }
        if (sb.Length > 0) w.WriteLine(sb.ToString().TrimEnd());

        w.WriteLine("CONSTS:");
        for (var i = 0; i < bc.Constants.Length; i++)
            w.WriteLine($"  [{i}] {FormatConst(bc.Constants[i])}");

        w.WriteLine("LOCALS: " + string.Join(", ", bc.Locals));
        if (bc.ConstLocals != null)
        {
            var names = new List<string>();
            for (var i = 0; i < bc.ConstLocals.Length && i < bc.Locals.Length; i++)
                if (bc.ConstLocals[i]) names.Add(bc.Locals[i]);
            w.WriteLine("CONSTLOCALS: " + string.Join(", ", names));
        }
        if (bc.GlobalSlotNames != null)
            w.WriteLine("GLOBALSLOTS: " + string.Join(", ", bc.GlobalSlotNames));
        if (bc.NestedLambdas != null)
        {
            var names = bc.NestedLambdas.Select(l => l.Name);
            w.WriteLine("NESTEDLAMBDAS: " + string.Join(", ", names));
        }
        w.WriteLine();
    }

    /// <summary>Deterministic, culture-free rendering of one constant. Primitives print their value;
    /// anything else prints its type name only, an engine object's ToString is not a stable
    /// serialization, and a dump that varies between runs is worthless as a baseline.</summary>
    private static string FormatConst(object? value)
    {
        switch (value)
        {
            case null: return "null";
            case string s: return "string \"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            case bool b: return "bool " + (b ? "true" : "false");
            case char ch: return "char '" + ch + "'";
            case int i: return "int " + i.ToString(CultureInfo.InvariantCulture);
            case long l: return "long " + l.ToString(CultureInfo.InvariantCulture);
            case double d: return "double " + d.ToString("R", CultureInfo.InvariantCulture);
            case float f: return "float " + f.ToString("R", CultureInfo.InvariantCulture);
            case decimal m: return "decimal " + m.ToString(CultureInfo.InvariantCulture);
            case byte by: return "byte " + by.ToString(CultureInfo.InvariantCulture);
            case short sh: return "short " + sh.ToString(CultureInfo.InvariantCulture);
            case uint ui: return "uint " + ui.ToString(CultureInfo.InvariantCulture);
            case ulong ul: return "ulong " + ul.ToString(CultureInfo.InvariantCulture);
            default: return "[" + value.GetType().Name + "]";
        }
    }
}
