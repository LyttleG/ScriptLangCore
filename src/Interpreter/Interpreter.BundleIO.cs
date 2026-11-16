namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The source carried by the most recently loaded bundle, if any. The CLI runs it as the
    /// program when it is present, through the SAME path a .slc file takes.
    internal string? LoadedBundleSource { get; private set; }

    /// <summary>Prepares a parsed file for EXPORT without running it: the same registration pass Interpret does
    /// (so forward references resolve), the same record of which top-level functions an export may take, and the
    /// program body's input. Interpret only fills _scriptTopLevel while EXECUTING, which is why an export used to
    /// need a run; this is the same two steps without Execute, so --compile works for a script that opens a
    /// window, serves, or never ends.</summary>
    internal void PrepareForExport(IEnumerable<Stmt> statements, string? sourcePath = null)
    {
        // The file being compiled is the IMPORTER for a path its imports resolve (`./x` needs a current
        // directory, and every other relative path resolves against the process one), which is exactly what a RUN
        // of the same file sets up, so the strip's own import handling agrees with the run's.
        if (sourcePath != null)
        {
            _currentDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            _currentSourceName = Path.GetFileName(sourcePath);
        }
        var stmts = statements as IReadOnlyList<Stmt> ?? statements.ToList();
        _scriptTopLevel.Clear();
        _declaredTypeNames = CollectDeclaredTypeNames(stmts);
        foreach (var stmt in FlattenNamespaceContainers(stmts))
        {
            if (stmt is not FuncDecl funcDecl) continue;
            try { _ = VisitFuncDecl(funcDecl); }
            catch { /* a registration failure surfaces when the bundle runs */ }
        }
        // Execution records these; here nothing executes, so the record is taken directly.
        foreach (var stmt in stmts)
            if (stmt is FuncDecl topLevel) _scriptTopLevel.Add(topLevel);
        // A NESTED declaration too, but only when it already has a compiled body (Program.PrecompileFunctions
        // compiles those now): the bundle's function table is the only place a loaded run can find it, and the
        // loader writes that table into the root global scope. Recording one WITHOUT a body would turn "cannot be
        // serialized" into a strip refusal for a program that merely declares a function inside a block, so an
        // uncompiled one is left to the bare-call check, which names it instead.
        foreach (var nestedFunc in DeclaredFunctions(stmts))
            if (nestedFunc.Compiled != null && !_scriptTopLevel.Contains(nestedFunc))
                _scriptTopLevel.Add(nestedFunc);
        SetProgramStatements(stmts);
    }

    /// <summary>Every function a program DECLARES, at any depth: the top level's own and any written inside a
    /// block. A NESTED function is the one the tree registers in that block's scope, and a loaded bundle has no
    /// block scope to register it in, so it must travel in the bundle's function table. Measured without it:
    /// `if (true) { void Inner() {...} var cb = fun() { Inner(); }; cb(); }` stripped, then the bundle bailed at
    /// the callback's CALL with "loaded from bytecode for a different environment", because nothing had stored
    /// Inner anywhere. The walk covers the container statements a declaration can sit in; a kind it does not know
    /// simply contributes nothing, which is the previous behaviour rather than a new failure.</summary>
    private IEnumerable<FuncDecl> DeclaredFunctions(IEnumerable<Stmt>? statements)
    {
        if (statements == null) yield break;
        foreach (var stmt in FlattenNamespaceContainers(statements))
            foreach (var found in DeclaredFunctionsIn(stmt))
                yield return found;
    }

    private IEnumerable<FuncDecl> DeclaredFunctionsIn(Stmt stmt)
    {
        switch (stmt)
        {
            case FuncDecl fd:
                yield return fd;
                break;
            case BlockStmt b:
                foreach (var f in DeclaredFunctions(b.Statements)) yield return f;
                break;
            case IfStmt i:
                foreach (var f in DeclaredFunctions(i.ThenBranch)) yield return f;
                foreach (var f in DeclaredFunctions(i.ElseBranch)) yield return f;
                break;
            case WhileStmt w:
                foreach (var f in DeclaredFunctions(w.Body)) yield return f;
                break;
            case ForeachStmt fe:
                foreach (var f in DeclaredFunctions(fe.Body)) yield return f;
                break;
            case ForStmt fr:
                if (fr.Init != null) foreach (var f in DeclaredFunctionsIn(fr.Init)) yield return f;
                foreach (var f in DeclaredFunctions(fr.Body)) yield return f;
                break;
            case TryStmt t:
                foreach (var f in DeclaredFunctionsIn(t.Body)) yield return f;
                foreach (var c in t.Catches) foreach (var f in DeclaredFunctionsIn(c.Body)) yield return f;
                if (t.Finally != null) foreach (var f in DeclaredFunctionsIn(t.Finally)) yield return f;
                break;
            case UsingStmt u:
                foreach (var f in DeclaredFunctionsIn(u.Body)) yield return f;
                break;
            case SwitchStmt sw:
                foreach (var c in sw.Cases) foreach (var f in DeclaredFunctions(c.Body)) yield return f;
                break;
        }
    }

    /// <summary>Compiles the CURRENT Interpret call's top level into the body a bundle carries as its entry.
    /// Null when there is nothing to compile or the body declined (a top-level `const`, a shape the compiler does
    /// not model, a slot past MaxByte), which the bundle writer reports rather than writing an entry that would
    /// behave differently from the script.</summary>
    internal string? ProgramBodyDeclineReason { get; private set; }

    /// <summary>Whether a program body the compiler could not compile as ONE unit is retried with its uncompilable
    /// top-level statements handed to the TREE through <see cref="Compiler.CompileProgramWithCarries"/>, which is
    /// what a source run does with the statement. ON by default since 2026-10-20, when its gate was measured to
    /// zero: `SLC_CARRIED_STATEMENTS=0` (or `VmSetStatementCarrier(false)`) turns it off for an A/B.
    ///
    /// WHAT IT BUYS, measured: the strip frontier went from 471 stripped / 43 refused to 501 / 13 of 514 suites,
    /// because the shapes only it can reach (a top-level `is` pattern, a top-level `await`, a top-level `CallExpr`,
    /// a self-recursive `var` lambda) now travel as their own source text and run on the tree exactly as in a source
    /// run. The 13 that still refuse are the documented families: a generic extension block (2), a namespace
    /// block's effect (3), a constructor body the compiler did not take (2), a nested `import` (1), a body that
    /// loads a type the VM has no value for (1), and a name a carried body calls which the bundle cannot carry (4),
    /// each reported by name in the page's `--no-source` table.
    ///
    /// WHY IT IS A SWITCH as well as a default: the two STATES differ in one respect a reader must be able to
    /// compare in one process (`VmSetStatementCarrier`), and an environment variable cannot change mid-run.
    /// The remaining bundle differences the carrier exposes are PRE-EXISTING engine facts rather than gaps in the
    /// carrier, each measured and reasoned in `tools/bundle-parity-known.txt` (`by construction:`): a suite that
    /// exports or loads a bundle from inside a bundle, a suite that asserts VM-vs-tree ROUTING (a bundle's entry is
    /// one compiled body with no tree phase), and a suite whose own printed reading is a wall clock or a rate.
    ///
    /// A CARRIED STATEMENT DECLARES its own globals: `var g = fun(...) { ... g ... }` runs on the tree with the root
    /// frame pushed, so `g` lands in the interpreter's global scope, and a LATER compiled statement resolves it.
    /// That is why `Bytecode.CarriedDeclaredNames` exists: the strip's call check cannot see a store a carried
    /// statement performs when it RUNS, and without that list it refused such a file by name (measured: the file
    /// answered `g=24` from both legs once the list was wired, where it had refused to strip).
    ///
    /// THE WIDER `foreach` RULE IS PART OF IT and the two halves COMPOSE: a collection that is a top-level `var`
    /// ASSIGNED FROM A CALL (how `mega_crash_test`'s `foreach (var v in coro)` reaches a coroutine) and a
    /// collection that IS a call, bare or on a receiver (how `async_stream_test` reaches `File.ReadLinesAsync` and
    /// `crash_udf_leak` reaches `Sqlite.Query(...)`). The narrow half alone leaves `mega_crash_test`'s bundle
    /// refusing, because its `foreach (var v in coro)` returns to the VM, which bails on a request-producing
    /// coroutine; both are carried now, so the tree drives the loop as a source run does.</summary>
    internal static bool StatementCarrierEnabled = Diagnostics.InitialFlag("CARRIED_STATEMENTS");

    internal Bytecode? ProgramBytecode
    {
        get
        {
            if (_programBodyAttempted || _programStatements == null) return _programBytecode;
            _programBodyAttempted = true;
            var builtins = GetGlobalScope().Keys
                .Where(k => k != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var decls = new List<FuncDecl>();
            foreach (var stmt in FlattenNamespaceContainers(_programStatements))
                if (stmt is FuncDecl fd) decls.Add(fd);
            var calleeDecls = Compiler.BuildCalleeDecls(decls, CaseSensitive);
            // An IMPORT compiles now, as a call that runs it AT ITS POSITION (__run_import__): a file may export a
            // library and then import it further down, and one end-of-file replay of it ran before anything had
            // written the file (measured: such a bundle failed at LOAD with "Module 'x' not found"). The LOADER
            // still replays imports for a DECLARATION-ONLY load, where no top level ever runs and a loaded
            // function must still find its module's names (see RegisterLoadedDeclarations).
            var bodyStatements = _programStatements;
            _programBytecode = Compiler.CompileProgram(bodyStatements, builtins, CaseSensitive, calleeDecls,
                out var declineReason);
            if (_programBytecode == null && StatementCarrierEnabled)
            {
                // The body did not compile as ONE unit. A top-level statement the VM cannot model is handed to the
                // TREE instead, by its own source text, which is exactly what a source run does for that statement,
                // and everything else compiles as usual. Measured: this is what turns "no entry, so the file carries
                // its whole source" into a runnable entry for the `LambdaExpr` and `IsPatternExpr` families.
                _programBytecode = Compiler.CompileProgramWithCarries(bodyStatements, builtins, CaseSensitive,
                    calleeDecls, out var carriedDecline);
                if (_programBytecode != null) declineReason = carriedDecline;
            }
            ProgramBodyDeclineReason = declineReason;
            return _programBytecode;
        }
    }

    /// <summary>Seeds the program body's input WITHOUT interpreting: the bytecode dump parses a file to compile it
    /// and must not run it. Mirrors what Interpret does for the same field.</summary>
    internal void SetProgramStatements(IEnumerable<Stmt> statements)
    {
        _programStatements = statements as List<Stmt> ?? statements.ToList();
        _programBytecode = null;
        _programBodyAttempted = false;
    }

    internal void ResetModuleScope()
    {
        var comparer = _caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var freshStack = new ScopeStack(comparer);
        _callFrames.Value!.Clear();
        _callFrames.Value!.Push(new CallFrame(freshStack));
    }

    /// <summary>Per-run state cleanup for module interpreters reused from the pool. The pool's
    /// Rent path deliberately skips the full Reset() (perf), it must still clear every per-run
    /// field or a reused interpreter retains the previous module's parsed declarations (AST
    /// trees), caches and namespace state, a linear leak proportional to the module's
    /// declaration count (~1-8 KB/run in .gc leak, growing forever across imports).</summary>
    internal void ResetModuleInterpreterState()
    {
        _warnedUnknownTypes = new();
        _declaredInNamespace.Clear();
        _namespaceSegments.Clear();
        _declaredTypeNames = null;
        _moduleContentHashes.Clear();
        Modules.Clear();
        _callStack.Clear();
        _exitHandlers.Clear();
        _loadingModules.Clear();

        _lambdaCache.Clear();
        _methodCache.Clear();
        _overloadCache.Clear();
        _overloadCache1.Clear();
        PropertyCache.Clear();
        _pureMemoCache.Clear();
        _currentExports = null;
        _constructorCache.Clear();
        _methodOverloadCache.Clear();
        _genericClassTemplates.Clear();
        _instantiatedClasses.Clear();
        _instantiatedFuncs.Clear();
        _staticInitClasses.Clear();
        _initialStaticFields.Clear();
        _currentDirectory = null;
    }

    /// <summary>Yields every statement, expanding NamespaceDeclStmt bodies inline so
    /// analysis/compilation treats namespaces as transparent containers.</summary>
    internal static IEnumerable<Stmt> FlattenNamespaceContainers(IEnumerable<Stmt> stmts)
    {
        foreach (var s in stmts)
        {
            if (s is NamespaceDeclStmt nsd)
            {
                foreach (var inner in FlattenNamespaceContainers(nsd.Body))
                    yield return inner;
            }
            else
                yield return s;
        }
    }

    /// <summary>Seeds the RUN's directory for a bundle that is about to be RUN as a program, because nothing
    /// else can. A source run gets it from <c>Interpret(sourcePath)</c>, and a bundle that CARRIES its source
    /// gets it the same way (the carried text is interpreted with the bundle's path), but a SOURCE-LESS program
    /// has no text to interpret, so the whole run had no directory at all. Two consequences were both visible:
    /// <c>Script.Directory()</c> answered "" (empty) and every `./` or `../` path was REFUSED with "Cannot
    /// resolve relative import without a current directory", which breaks the one path rule this engine
    /// documents (`./x` means "x beside this script"). Measured with tools/bundle-parity.sh on
    /// path_resolution_test: the FILE passes its 9 checks, while its stripped bundle printed `scriptDir = `
    /// (empty) and died at its first `./` read. The CLI and `.run -bc` call this at their RUN sites rather than
    /// the LOAD doing it, because `Bytecode.LoadScript(path)` loads a bundle for a CALLER that is still
    /// interpreting its own file, and that caller's `./` must keep meaning beside IT.</summary>
    internal void SetRunDirectoryForBundle(string bundlePath)
        => _currentDirectory = Path.GetDirectoryName(Path.GetFullPath(bundlePath));

    /// <summary>The ONE rule for an IMPORT's path VALUE, asked by both doors that run one: the tree's
    /// <c>VisitImportStmt</c> after evaluating the expression, and the compiled <c>__run_import__</c> helper, which
    /// is what a top-level import becomes. Two wordings would be two behaviours for one mistake, and the helper's
    /// generic guard ("a path, an alias, a line and a column are required") tells a reader nothing about the value
    /// they actually wrote.</summary>
    internal string RequireImportPathText(object? value, int line, int column)
    {
        if (value is string text && text.Length > 0) return text;
        if (value is string)
            RuntimeError("The import path is an empty string.", line, column);
        else
            RuntimeError($"The import path must be a string, not '{GetFriendlyTypeNameOfValue(value)}'.", line, column);
        return "";   // unreachable: RuntimeError throws
    }

    /// <summary>An IMPORT's path resolution, which is a SEARCH rather than a single answer: an import names a file
    /// the script DEPENDS on, so a suite must be runnable from its own directory as well as from the executable's,
    /// and the two spellings a suite actually uses must both work from either place. The order is ADDITIVE, so
    /// nothing that resolves today can change: the ONE general path rule first (<see cref="ResolvePath"/>, where
    /// "./x" is beside the run and a bare path is against the process directory), then the RUN's own directory, then
    /// each ANCESTOR of it, nearest first. That last step is what makes a repository-root-relative spelling work
    /// from anywhere (`import "assets/language/repl/x.inc"` is found at the root whether the run starts there, in the
    /// deployed copy beside the executable, or in the suite's own directory). When no candidate exists the general
    /// rule's answer is returned, so a refusal still names the path it always named.</summary>
    private string ResolveImportPath(string path)
    {
        var run = Current ?? this;   // the RUN's directory, for the same reason ResolvePath states
        var direct = ResolvePath(path);
        if (Path.IsPathRooted(path) || File.Exists(direct)) return direct;
        if (run._currentDirectory is { Length: > 0 } runDir)
        {
            var beside = Path.GetFullPath(Path.Combine(runDir, path));
            if (File.Exists(beside)) return beside;
            for (var dir = Directory.GetParent(Path.GetFullPath(runDir)); dir != null; dir = dir.Parent)
            {
                var candidate = Path.GetFullPath(Path.Combine(dir.FullName, path));
                if (File.Exists(candidate)) return candidate;
            }
        }
        return direct;
    }

    private ModuleInfo LoadModule(string fullPath, int lineNumber, int column)
    {
        if (!File.Exists(fullPath))
            RuntimeError($"Module '{fullPath}' not found.", lineNumber, column);

        var fileBytes = File.ReadAllBytes(fullPath);

        // Content-hash dedup: hash from raw bytes, no string allocation
        var hashBytes = SHA256.HashData(fileBytes);
        var hash = Convert.ToHexString(hashBytes);
        if (_moduleContentHashes.TryGetValue(hash, out var existingPath))
        {
            Modules[fullPath] = Modules[existingPath];
            return Modules[existingPath];
        }

        // Transcode file bytes to pooled char buffer for lexing
        var charCount = Encoding.UTF8.GetCharCount(fileBytes);
        var buffer = CharBufferPool.Rent(charCount);
        try
        {
            if (fullPath.EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase))
                return LoadModuleFromBundle(fullPath, hash);

            var actualChars = Encoding.UTF8.GetChars(fileBytes, 0, fileBytes.Length, buffer, 0);
            var lexer = new Lexer(buffer.AsMemory(0, actualChars));
            var tokens = lexer.GetTokens();

            // Create module interpreter first (reuse from pool, no Reset, blueprint scope already populated)
            var moduleInterpreter = InterpreterPool.Rent(_caseSensitive);

            // Share namespace objects with parent (now reference types)
            var parentGlobalScope = _rootFrame.Scopes.GetGlobalScopeDictionary();
            var moduleGlobalScope = moduleInterpreter._rootFrame.Scopes.GetGlobalScopeDictionary();
            foreach (var kv in parentGlobalScope)
            {
                if (kv.Value.Value is Namespace)
                    moduleGlobalScope[kv.Key] = kv.Value;
            }

            // Create parser with module interpreter's delegates
            var parser = new Parser(tokens, moduleInterpreter.Delegates, _caseSensitive,
                source: new string(buffer, 0, actualChars));
            var statements = parser.GetStatements();//.ToList();
            var moduleDir = Path.GetDirectoryName(fullPath);
            moduleInterpreter._currentDirectory = moduleDir;

            _loadingModules.Add(fullPath);
            try
            {
                moduleInterpreter.IsModuleExecution = true;   // F2 gate: modules re-export via paths, no scope chain
                moduleInterpreter.Interpret(statements, fullPath);
            }
            finally
            {
                moduleInterpreter.IsModuleExecution = false;
                _loadingModules.Remove(fullPath);
            }

            var exports = new Dictionary<string, object?>(8);
            var globalScope = moduleInterpreter.CurrentFrame.Scopes.CaptureSnapshot()[0];
            // A MODULE's top level runs in a scope VIEW that `CurrentFrame` names, while a compiled body stores
            // through `_rootFrame` (the frame the module interpreter was built with), and the two are not the same
            // frame during a module run (measured: `sameFrame=False`, with DIFFERENT scope dictionaries). The
            // tree's own declaration writes the CURRENT scope, which is why the tree's export list is complete; a
            // compiled statement writes the root one, so a module came back short by exactly its own `var`s and
            // the alias was missing every member it declares (measured: `my_=[]` from the path against
            // `my_=[my_add,my_concat]` from the tree, forward_ref_test L183). The module's real globals live in
            // BOTH (its functions resolve free variables against the flat scope), so read them together. FIRST
            // WINS: the current view is what the tree fills and what the module's own declarations mean, so the
            // root scope may only CONTRIBUTE the names it is missing. Measured the hard way, this merge without
            // that rule broke a passing suite: `namespace_decl_test` answered "Method 'answer' not found in
            // namespace" at L129 because a name the view already had was overwritten by the flat scope's entry
            // for it (the flat scope holds the parent's shared namespace objects).
            foreach (var kvp in moduleInterpreter._rootFrame.Scopes.CaptureSnapshot()[0])
                if (!globalScope.ContainsKey(kvp.Key)) globalScope[kvp.Key] = kvp.Value;
            foreach (var kvp in globalScope.Where(kvp => !ExcludedNamespacesKeys.Any(set => set.Contains(kvp.Key, _comparison))))
            {
                // Skip non-public exports (private by default)
                if (kvp.Value is INativeFunction) continue;  // built-in native functions use parent interpreter's context
                if (kvp.Value is FuncDecl func && func.Access != AccessModifier.Public && func.Access != AccessModifier.Internal) continue;
                if (kvp.Value is ClassInfo cls && cls.Access != AccessModifier.Public && cls.Access != AccessModifier.Internal) continue;
                exports[kvp.Key] = kvp.Value;
            }

            // Namespace paths recorded during module execution ("" = root, absent = root).
            var exportPaths = new Dictionary<string, string>(8);
            foreach (var name in exports.Keys)
                if (moduleInterpreter._declaredInNamespace.TryGetValue(name, out var nsPath) && nsPath.Length > 0)
                    exportPaths[name] = nsPath;

            // Module functions resolve free variables (sibling functions, module globals) against
            // the module's OWN flat global scope. Attach a snapshot of it to every exported
            // ScriptFunction so cross-function calls work when invoked from the importing
            // interpreter, its scope stack only holds the nested export view, not the module's
            // flat members. FuncDecl exports carry the same dict via ModuleInfo.ModuleGlobals.
            var moduleGlobals = new Dictionary<string, object?>(globalScope.Count, _comparer);
            foreach (var kvp in globalScope)
                moduleGlobals[kvp.Key] = kvp.Value;
            foreach (var key in exports.Keys.ToList())
            {
                if (exports[key] is ScriptFunction sf)
                {
                    var modFn = sf with { ClosureSnapshot = sf.ClosureSnapshot with { GlobalScope = moduleGlobals } };
                    modFn.IsModuleScope = true;   // module fns keep their own flat scope (see ScriptFunction.IsModuleScope)
                    exports[key] = modFn;
                }
            }

            var moduleInfo = new ModuleInfo(new ReadOnlyDictionary<string, object?>(exports), true,
                exportPaths.Count > 0 ? new ReadOnlyDictionary<string, string>(exportPaths) : null,
                moduleGlobals.Count > 0 ? new ReadOnlyDictionary<string, object?>(moduleGlobals) : null);
            Modules[fullPath] = moduleInfo;
            _moduleContentHashes.TryAdd(hash, fullPath);
            InterpreterPool.Return(moduleInterpreter);
            return moduleInfo;
        }
        finally { CharBufferPool.Return(buffer); }
    }

    /// <summary>Loads a bytecode bundle (.slcbc) as a module: the bundle's top-level functions are
    /// defined into a fresh module interpreter and exported under the import alias, e.g.
    /// import "lib.slcbc" as lib; lib.sumTo(10). Bodies are materialized eagerly here so the
    /// functions are callable from the parent interpreter without sharing lazy-body state.</summary>
    private ModuleInfo LoadModuleFromBundle(string fullPath, string hash)
    {
        var moduleInterpreter = InterpreterPool.Rent(_caseSensitive);
        try
        {
moduleInterpreter.LoadBundleFromFile(fullPath);
            var exports = new Dictionary<string, object?>(8);
            var globalScope = moduleInterpreter.CurrentFrame.Scopes.CaptureSnapshot()[0];
            foreach (var kvp in globalScope)
            {
                if (kvp.Value is Namespace) continue;
                if (kvp.Value is INativeFunction) continue;
                if (kvp.Value is ScriptFunction { Declaration.Access: not (AccessModifier.Public or AccessModifier.Internal) }) continue;
                exports[kvp.Key] = kvp.Value;
            }
            var moduleInfo = new ModuleInfo(new ReadOnlyDictionary<string, object?>(exports), true);
            Modules[fullPath] = moduleInfo;
            _moduleContentHashes.TryAdd(hash, fullPath);
            return moduleInfo;
        }
        finally
        {
            InterpreterPool.Return(moduleInterpreter);
        }
    }

    /// <summary>Compiles a SCRIPT TEXT in a fresh interpreter and writes it as a program bundle that
    /// carries no source: the top level's bytecode becomes the entry and its declarations travel as
    /// descriptors. Refused, naming the statement kind, when the top level cannot be compiled: a bundle
    /// that could not run its own program would silently differ from the script it came from.</summary>
    internal string ExportProgramWithoutSource(string outPath, string source, string? entryName,
        bool stripSource = true, bool requireStrip = false)
    {
        var (data, report) = ExportProgramWithoutSourceBytes(source, entryName, stripSource, requireStrip);
        File.WriteAllBytes(outPath, data);
        return report;
    }

    /// <summary>The bytes and report behind ExportProgramWithoutSource. Both forms share the compile in a fresh
    /// interpreter, which is the part that must not drift: a temp interpreter is what keeps the caller's session
    /// untouched. The source mode defaults to the strip, since that is what makes this method different from
    /// ExportTopLevelToBundle; "carry" here is the same compile with the program text kept.</summary>
    internal (byte[] Data, string Report) ExportProgramWithoutSourceBytes(string source, string? entryName,
        bool stripSource = true, bool requireStrip = false)
    {
        List<Stmt> statements;
        try
        {
            statements = new Parser(new Lexer(source.AsMemory()).GetTokens(), Delegates, source: source).GetStatements().ToList();
        }
        catch (ScriptException ex)
        {
            // THE POSITION IS THE PARSER'S, and it is a line of the TEXT BEING COMPILED. The alternative, leaving the
            // error position-less so the CALLER's statement adopts it, reads as nonsense about a snippet: reported from
            // a live session, a ONE-LINE French capture answered "Unexpected character 'é' (at line 2190, column 25)",
            // where 2190 is the line of the tool's own loop statement that asked for the compile, so the reader went
            // looking for an encoding problem that does not exist. The ERROR CODE travels too (this is a ParseError, not
            // an Unspecified one), because a caller may classify on it.
            // The position travels INTO the text as well as into the fields, because a caller that shows `ex.Message`
            // (the clipboard tool does) would otherwise show neither, and `WithLocation` cannot add it: that rule skips
            // an error carrying a Value, which a message-shaped one always does.
            var text = "Bytecode.ExportProgram: the source did not parse: " + ex.Message;
            if (ex.Line > 0 && !text.Contains("at line ", StringComparison.Ordinal))
                text += $" (at line {ex.Line}, column {ex.Column})";
            throw new ScriptException(ex.ErrorCode, text, ex.Line, ex.Column);
        }
        catch (Exception ex)
        {
            // The engine's ONE message map, never a raw CLR sentence: this arm is for a failure that is not the parser's.
            throw new ScriptException("Bytecode.ExportProgram: the source did not parse: " + FriendlyExceptionMessage(ex));
        }
        using var handle = InterpreterManager.Create(caseSensitive: CaseSensitive, context: "Bytecode.ExportProgram");
        var temp = handle.Instance;
        Program.PrecompileFunctions(statements, temp);
        temp.PrepareForExport(statements);   // records the top level, which is what an entry is built from
        temp.SourceText = source;
        var prevExitCode = Environment.ExitCode;
        try { return temp.ExportTopLevelToBundleBytes(entryName, stripSource, requireStrip); }
        finally { Environment.ExitCode = prevExitCode; }   // the temp interpreter must not leak an exit code
    }

    /// <summary>The reason a carried EXPRESSION cannot travel as a thunk, or null when it can: the compiler did not
    /// take it, it reads the INSTANCE (`this`/`super`/`base`, which a thunk has none of), or it reads a name a
    /// loaded body cannot resolve. ONE implementation, asked by a parameter's DEFAULT VALUE and by an instance
    /// field's INITIALIZER: the two are the same problem (an expression the wire must carry and a thunk body is the
    /// only shape that can), and the callers differ only in what a reader is told about the owner.</summary>
    private string? UncarriableThunkReason(Expr value, Bytecode? thunk, HashSet<string>? carriedGlobals,
        Dictionary<string, ScopeStack.VariableInfo>? globals, IReadOnlyList<FuncDecl> exported)
    {
        if (thunk == null) return "the compiler did not take its expression";
        var names = new List<string>();
        CollectFreeNames(value, n => { if (n != null) names.Add(n); });
        var instance = names.FirstOrDefault(n => _comparer.Equals(n, "this")
            || _comparer.Equals(n, "super") || _comparer.Equals(n, "base"));
        if (instance != null)
            return $"its expression reads '{instance}', which a loaded body has no instance for";
        var unresolved = names.Where(n => !_builtinNames.Contains(n)
            && !(globals?.ContainsKey(n) ?? false)
            && !(carriedGlobals?.Contains(n) ?? false)
            && !exported.Any(f => _comparer.Equals(f.Name, n))).Distinct().ToList();
        return unresolved.Count > 0
            ? "its expression reads " + string.Join(", ", unresolved.Select(n => $"'{n}'"))
                + ", which the bundle cannot resolve"
            : null;
    }

    /// <summary>The INSTANCE FIELD INITIALIZERS a bundle cannot carry, one entry per field, or an empty list when
    /// every one can travel (FieldDecl.InitializerThunk, the same thunk shape a parameter default uses). A STATIC
    /// field needs nothing here: its value is registered and the descriptor carries the value itself. A struct's
    /// initializer is deliberately absent too: the interpreter ignores one (measured, `struct S { public int V = 5; }`
    /// reads 0 from SOURCE on both engines), so carrying it would build an instance the file does not.</summary>
    private List<string> UncarriableInitializers(IReadOnlyList<FuncDecl> exported, HashSet<string>? carriedGlobals)
    {
        var bad = new List<string>();
        var globals = GetGlobalScope();
        foreach (var ci in Classes.Values)
            foreach (var f in ci.Fields)
            {
                if (f.IsStatic || f.Initializer == null) continue;
                if (UncarriableThunkReason(f.Initializer, f.InitializerThunk, carriedGlobals, globals, exported)
                    is { } why)
                    bad.Add($"'{ci.Name}.{f.Name}' (an instance field's initializer, and " + why + ")");
            }
        return bad;
    }

    /// <summary>The DEFAULT VALUES a bundle cannot answer, one entry per parameter, or an empty list when every
    /// default of every declaration it carries can travel. A default travels as a compiled zero-parameter body
    /// (FuncDecl.DefaultThunks), and three shapes do not: an expression the compiler did not take, one that
    /// reads the INSTANCE (`this`, and a thunk has no instance), and one that reads a name a loaded body cannot
    /// resolve (an enclosing local, a library's module variable, or a global the bundle does not populate). The
    /// source run resolves all three, so a bundle that dropped them would refuse a call the source accepts,
    /// which is the divergence this list exists to make loud. `carriedGlobals` is what the bundle itself
    /// populates (a stripped program's own top-level vars); null when the caller has none to add.
    /// `countLiveGlobals` is false for the LIBRARY routes: their caller is another run, so a name that only
    /// exists in this session (or in the exported module) does not travel, which is measured on the module's own
    /// variables: a library function body that reads one answers "Undefined variable", snapshot or not.</summary>
    private List<string> UncarriableDefaults(IReadOnlyList<FuncDecl> exported, HashSet<string>? carriedGlobals,
        bool countLiveGlobals = true)
    {
        var bad = new List<string>();
        var globals = countLiveGlobals ? GetGlobalScope() : null;
        void Check(string owner, FuncDecl? decl)
        {
            if (decl?.DefaultValues == null) return;
            var thunks = decl.DefaultThunks;
            for (var i = 0; i < decl.DefaultValues.Count && i < decl.Params.Count; i++)
            {
                var value = decl.DefaultValues[i];
                if (value == null) continue;
                var label = $"'{owner}.{decl.Params[i].Name}'";
                var thunk = thunks != null && i < thunks.Length ? thunks[i] : null;
                if (UncarriableThunkReason(value, thunk, carriedGlobals, globals, exported) is { } why)
                    bad.Add(label + " (" + why + ")");
            }
        }
        foreach (var f in exported) Check(f.Name, f);
        foreach (var ci in Classes.Values)
        {
            foreach (var m in ci.Methods) Check(ci.Name + "." + m.Name, m);
            if (ci.StaticMethods != null) foreach (var m in ci.StaticMethods) Check(ci.Name + "." + m.Name, m);
            foreach (var op in ci.OperatorMethods.Values) Check(ci.Name + "." + op.Name, op);
        }
        foreach (var sd in Structs.Values)
        {
            foreach (var m in sd.Methods) Check(sd.Name + "." + m.Name, m);
            foreach (var op in sd.OperatorMethods.Values) Check(sd.Name + "." + op.Name, op);
        }
        // Extension members are declaration-level (the v6 section carries them), so their defaults travel with
        // them: the walk reads the file's own statements, exactly like the refusal list that names them.
        foreach (var stmt in FlattenNamespaceContainers(_programStatements ?? []))
            if (stmt is ExtensionDeclStmt ext)
                foreach (var member in ext.Members) Check(ext.TargetName + "." + member.Name, member);
        return bad;
    }

    /// <summary>Exports all VM-compiled top-level functions of this interpreter session to a bundle
    /// file (shared by the Bytecode.ExportScript native and the REPL -bc dotcommands).
    /// Returns a "exported N, skipped M (names)" report.</summary>
    internal string ExportTopLevelToBundle(string path, string? entryName, bool stripSource = false, bool requireStrip = false)
    {
        var (data, report) = ExportTopLevelToBundleBytes(entryName, stripSource, requireStrip);
        File.WriteAllBytes(path, data);
        return report;
    }

    /// <summary>Builds the bytes the file form would write, and the report it returns: the memory form and the
    /// file form are ONE implementation, so they cannot drift (the file form adds only the write).</summary>
    internal (byte[] Data, string Report) ExportTopLevelToBundleBytes(string? entryName, bool stripSource = false, bool requireStrip = false)
    {
        // Set by the strip branch below: whether the source really was left out, and, when it was asked
        // for and could not be, why the source was carried instead (the report states which happened).
        var strippedSource = false;
        string? carriedInstead = null;
        var exported = new List<FuncDecl>();
        var skipped = new List<string>();
        // TOP-LEVEL functions whose body the compiler did not take but whose DECLARATION this parse holds the text
        // for. They travel as that text (see FuncDecl.SourceText): the wire has no slot for a body that was never
        // compiled, so registering the declaration at load is what gives a compiled caller a TREE-BACKED function to
        // call, which is exactly what the source run had. A skipped function with NO text (a synthesized parse, or a
        // declaration written inside a block) still refuses, because nothing about it could be carried.
        var textCarriedFuncs = new List<(string Name, string Text, int Line, int Column)>();
        RegisterNestedDeclsOfExports();
        foreach (var d in _scriptTopLevel)
        {
            // A COROUTINE is exportable when the VM can back it: a plain value-yield body (no yield REQUEST) is
            // compiled to bytecode like any other, and the loader derives HasYield from that bytecode so the call
            // path wraps it and the drive steps it (measured: gen_while's bundle prints seq: 012, where before the
            // whole run ended SILENTLY at the first YIELD). A yield-REQUEST body (sleep/http/event) stays tree-only
            // and its declaration travels as text; any other body the compiler declined either travels the same way
            // or, with no text to carry, is skipped and reported.
            if (d.Compiled != null && (!d.HasYield || IsVmBackedCoroutine(d))) { exported.Add(d); continue; }
            if (d.SourceText is { Length: > 0 } declText)
            {
                textCarriedFuncs.Add((d.Name, declText, d.Line, d.Column));
                continue;
            }
            skipped.Add(d.Name);
        }
        // The bundle's ENTRY is the file's OWN TOP LEVEL when it compiled: a synthetic zero-parameter function
        // whose bytecode is the body Compiler.CompileProgram produced. The loader's ordinary behaviour, invoking
        // the declared entry with no arguments, then RUNS the script the bundle came from, which is what makes a
        // .slcbc a program rather than a function library. A declined body still writes the function table and
        // says so in the report: that is the library every bundle was before this existed.
        var programBody = ProgramBytecode;
        var bundleEntry = entryName;
        if (programBody != null)
        {
            var synthetic = FuncDecl.Create(ProgramEntryFunctionName, [], [], [], [], TypeInfo.Void, null,
                AccessModifier.Public, 0, 0);
            synthetic.Compiled = programBody;
            exported.Add(synthetic);
            bundleEntry ??= synthetic.Name;
        }
        if (bundleEntry != null &&
            !exported.Any(f => StringComparer.OrdinalIgnoreCase.Equals(f.Name, bundleEntry)))
            throw new ScriptException($"Bytecode.ExportScript: entry function '{bundleEntry}' is not VM-compiled or not found in this script.");
        // A bundle can carry its program as SOURCE too, so a file whose top level the compiler
        // declines (a top-level const, a generic `new`) is still runnable: the CLI runs the carried
        // text through the same path a .slc takes, while the functions stay bytecode. The
        // property is set by --compile; the REPL and Bytecode.ExportScript reach here without it,
        // so fall back to the file this interpreter was interpreting.
        var entrySource = SourceText;
        if (entrySource == null && _currentSourceName != null)
        {
            var srcPath = Path.Combine(_currentDirectory ?? ".", _currentSourceName);
            try { if (File.Exists(srcPath)) entrySource = File.ReadAllText(srcPath); } catch { }
        }
        byte[] data;
        // Whether the SOURCE can be left out is decided here rather than by the caller, because only this
        // method knows why it cannot. Two shapes a class DESCRIPTOR cannot reproduce are refused, because a
        // stripped bundle that silently differed from its script is the one outcome worth an error: an instance
        // field's initializer, and a generic class. With --no-source a refusal FAILS the compile; without it,
        // the source is carried instead and the report says which happened.
        string? stripRefusal = null;
        // The classes that travel as their own SOURCE TEXT: a generic class, or a class nesting one (see
        // ClassDeclStmt.SourceText). They are registered from that text at LOAD, so the class DESCRIPTOR sections
        // leave them out and their members' compiled-ness is not the descriptors' business either. Computed once
        // because the refusal bookkeeping and the descriptor list both ask.
        // ONE collector answers both sets, and it takes a text-carried declaration's WHOLE SUBTREE: a struct travels the
        // same way as a class (its descriptor carries bytecode, and a member the compiler did not take has none), and a
        // nested type travels with the outer declaration's text rather than as its own descriptor.
        var textCarriedClasses = new HashSet<string>(_comparer);
        var textCarriedStructs = new HashSet<string>(_comparer);
        CollectTextCarriedTypeNames(_programStatements, textCarriedClasses, textCarriedStructs);
        // The imports the stripped form carries, collected before the checks so the bare-call one can ask what the
        // loaded run will have (see ImportedNamesOfCarriedImports), and written into the declarations below.
        IReadOnlyList<VmBytecodeCodec.LibraryImport> carriedImports = [];
        VmBytecodeCodec.LibraryDeclarations? strippedDeclarations = null;
        var strippedClassCount = 0;
        if (stripSource)
        {
            if (programBody == null)
                stripRefusal = "the top level did not compile"
                    + (ProgramBodyDeclineReason is { Length: > 0 } why ? " (" + why + ")" : "");
            else
            {
                // The descriptors need the classes and structs REGISTERED, and --compile runs nothing, so
                // nothing has registered them yet: the file's own declarations supply them. They are
                // interpreted on their own, so the program's statements still do not run.
                if (_programStatements != null)
                    RegisterDeclarationStatements(_programStatements);
                carriedImports = CollectProgramDeclarations(_programStatements).Imports;
                {
                    // A generic class travels as its own SOURCE TEXT now, so this list is what is left: an
                    // INSTANTIATION whose TEMPLATE the file does not declare, which nothing in the bundle could
                    // bring back. A class this file declares is excluded (its text re-registers the template at
                    // load, and `new Box<int>()` instantiates again at run time, exactly as a source run does).
                    var generic = Classes.Values
                        .Where(ci => ci.TypeArguments.Count > 0 || ci.Name.Contains('<'))
                        .Where(ci => !textCarriedClasses.Contains(TemplateOf(ci.Name)))
                        .Select(ci => ci.Name).ToList();
                    if (generic.Count > 0)
                        stripRefusal = "a class descriptor does not reproduce a generic class, so "
                            + string.Join(", ", generic.Select(n => $"'{n}'")) + " would lose members at run time";
                    else
                    {
                        // The stripped form IS the library layout. Its descriptor sections carry classes, structs,
                        // variable snapshots and (since v6) the enum, delegate, interface and extension-block
                        // declarations, so those are CARRIED now rather than refused. What a descriptor still
                        // cannot reproduce is refused BY NAME, because a bundle that silently differs from its
                        // script is the one outcome worth an error.
                        var uncarried = new List<string>();
                        // A NAME DECLARED AS BOTH A CLASS AND A STRUCT is one outcome a bundle cannot reproduce: a
                        // source run resolves names in DECLARATION ORDER (the later declaration shadows the earlier one
                        // from its own line on), while a bundle registers every declaration before its entry runs, so
                        // the later one wins from the first statement and the earlier one's members are unreachable.
                        // Measured: `class Dup { public static int A = 1; } print(Dup.A); struct Dup { public static int
                        // B = 2; }` answers 1 then 2 from the file and "Static member 'A' not found in struct 'Dup'"
                        // from a bundle. `crash_static_keyword` declares exactly that pair, and its own refusal named an
                        // unrelated CONSTRUCTOR, which is what masked this until the constructor carrier landed.
                        {
                            var declaredClasses = new HashSet<string>(_comparer);
                            foreach (var st in FlattenNamespaceContainers(_programStatements ?? []))
                                if (st is ClassDeclStmt cd) declaredClasses.Add(TemplateOf(cd.Name));
                            foreach (var st in FlattenNamespaceContainers(_programStatements ?? []))
                                if (st is StructDeclStmt sdName && declaredClasses.Contains(TemplateOf(sdName.Name)))
                                    uncarried.Add($"the name '{sdName.Name}', which is declared as BOTH a class and a"
                                        + " struct, because a bundle registers every declaration before its entry runs,"
                                        + " so the later declaration wins from the first statement and the earlier one's"
                                        + " members are unreachable");
                        }
                        foreach (var stmt in FlattenNamespaceContainers(_programStatements ?? []))
                        {
                            switch (stmt)
                            {
                                case InterfaceDeclStmt:
                                    // A default body and a property accessor are BODIES, and a body travels as a
                                    // shell now (see BuildInterfaceShells), so this arm names nothing. What can
                                    // still refuse them is the "the compiler did not take it" check further down.
                                    break;
                                // A GENERIC block's members are compiled per instantiation, so they have no
                                // bytecode to carry: measured, the engine compiles only a non-generic block's
                                // members (ExtensionMembers.CompileExtensionMemberBody is called under
                                // `typeParameters.Count == 0`).
                                case ExtensionDeclStmt ext when ext.TypeParameters.Count > 0:
                                    uncarried.Add($"the generic extension block on '{ext.TargetName}'");
                                    break;
                                case ExtensionDeclStmt ext when ext.Properties.Count > 0:
                                    uncarried.Add($"the extension property on '{ext.TargetName}'");
                                    break;
                                case ExtensionDeclStmt ext when ext.Operators.Count > 0:
                                    uncarried.Add($"the extension operator on '{ext.TargetName}'");
                                    break;
                                case ExtensionDeclStmt ext:
                                    foreach (var member in ext.Members)
                                        if (member.Compiled == null)
                                            uncarried.Add($"the extension member '{ext.TargetName}.{member.Name}',"
                                                + " whose body the compiler did not take (a coroutine, or a `ref`/`out`"
                                                + " parameter), so a stripped bundle has no body to run");
                                    break;
                                // A GENERIC class travels as its own SOURCE TEXT now (see
                                // ClassDeclStmt.SourceText), which is the ONE thing that can reproduce it: measured,
                                // an INSTANTIATION clones the template's AST and carries no bytecode at all, so its
                                // members run on the TREE even in a source run (`VM: 0` for `new Box<int>().Get()`
                                // against `VM: 1` for a plain class's method) while a class descriptor holds
                                // bytecode. What is still refused is a generic class this parse holds NO text for (a
                                // synthesized parse), since then nothing about it could be carried.
                                case ClassDeclStmt cls when cls.DeclaresGenericClass
                                    && string.IsNullOrEmpty(cls.SourceText):
                                    uncarried.Add($"the generic class '{cls.Name}', whose declaration this parse"
                                        + " holds no source text for, so nothing about it could be carried");
                                    break;
                            }
                        }
                        // A CARRIED CLASS or STRUCT member whose body the compiler did not take has no bytecode for
                        // the descriptor to carry either, and the skip report cannot see it (that counts TOP-LEVEL
                        // functions only): measured on assets/language/com/com_excel_sheets_test.slc, where the
                        // coroutine method `XlBook.SheetNames()` was reported as `skipped 0`, the strip succeeded,
                        // and the loaded callback then bailed at its CALL with "was loaded from bytecode for a
                        // different environment". Same rule as the members above: name it, and let the default
                        // carry the source, which re-declares the coroutine.
                        void NoteUncarried(string owner, IReadOnlyList<FuncDecl>? members)
                        {
                            if (members == null) return;
                            foreach (var m in members)
                                if (m.Compiled == null)
                                    uncarried.Add($"the member '{owner}.{m.Name}'"
                                        + (m.IsCoroutine
                                            ? ", a coroutine, which the VM cannot call from a compiled body, so a stripped bundle could not run it"
                                            : ", whose body the compiler did not take (a body it left to the tree), so a stripped bundle has no body to run"));
                        }
                        foreach (var ci in Classes.Values)
                        {
                            // A class that travels as its OWN SOURCE TEXT is not the descriptor sections' business:
                            // its text re-registers every member at load (which is what gives a declined body a
                            // tree-backed owner), so the member list here must not refuse it.
                            if (textCarriedClasses.Contains(TemplateOf(ci.Name))) continue;
                            NoteUncarried(ci.Name, ci.Methods);
                            NoteUncarried(ci.Name, ci.StaticMethods);
                            NoteUncarried(ci.Name, [.. ci.OperatorMethods.Values]);
                            NoteUncarried(ci.Name, ci.Destructor == null ? null : [ci.Destructor]);
                            foreach (var ctor in ci.Constructors)
                                if (ctor.Compiled == null)
                                    uncarried.Add($"a constructor of '{ci.Name}', whose body the compiler did not"
                                        + " take (a coroutine, or a body it left to the tree), so a stripped bundle"
                                        + " has no body to run");
                        }
                        foreach (var sd in Structs.Values)
                        {
                            // The same rule a class above follows: a struct that travels as its own SOURCE TEXT is
                            // re-registered from that text, bodies included, so this list must not refuse it.
                            if (textCarriedStructs.Contains(TemplateOf(sd.Name))) continue;
                            NoteUncarried(sd.Name, sd.Methods);
                            NoteUncarried(sd.Name, [.. sd.OperatorMethods.Values]);
                        }
                        // An INTERFACE's default bodies and property accessors are bodies the wire carries as
                        // shells now, so what is left to refuse is the one the compiler did not take: without it a
                        // class that does not implement the member would have nothing to run.
                        foreach (var itf in FlattenNamespaceContainers(_programStatements ?? [])
                            .OfType<InterfaceDeclStmt>())
                        {
                            foreach (var (key, decl) in itf.Defaults)
                                if (decl.Compiled == null)
                                    uncarried.Add($"the default body '{itf.Name}.{key}', which the compiler did not"
                                        + " take (a body it left to the tree)");
                            foreach (var prop in itf.Properties)
                            {
                                if (prop.Getter != null && prop.Getter.Compiled == null)
                                    uncarried.Add($"the getter of the interface property '{itf.Name}.{prop.Name}',"
                                        + " which the compiler did not take");
                                if (prop.Setter != null && prop.Setter.Compiled == null)
                                    uncarried.Add($"the setter of the interface property '{itf.Name}.{prop.Name}',"
                                        + " which the compiler did not take");
                            }
                        }
                        if (uncarried.Count > 0)
                            stripRefusal = "the stripped form carries no declaration for "
                                + string.Join(", ", uncarried) + ", so it would be missing at run time";
                    }
                }
            }
        }
        // A SKIPPED function is one whose body the export could not carry (a coroutine, or any body the compiler
        // left to the tree). The wire has no slot for it, so a compiled caller of it has nothing to run, which is
        // exactly how assets/language/com/com_excel_sheets_test.slc failed after 1b: `G` (a coroutine) was
        // skipped, its caller called it, and the run stopped with "was loaded from bytecode for a different
        // environment". Measured with the VM's opcode trace: the loaded lambda ran to `CALL` and bailed there.
        // A strip is only honest when it is the program, so name what is missing and let the default carry the
        // source, which re-declares it.
        if (stripSource && stripRefusal == null && skipped.Count > 0)
            stripRefusal = "the stripped form carries no body for " + string.Join(", ", skipped)
                + " (a coroutine or a tree-only body cannot be serialized), so a compiled caller of it would have"
                + " nothing to run";
        // A BARE CALL the bundle cannot resolve. The compiler recorded every bare-name call it emitted for the
        // program body, and the strip asks whether the BUNDLE carries that name: a builtin, or an exported
        // function. Anything else is a name only the LIVE run could resolve, which is what a block-scoped local is
        // (its declaration lives in that block, and the loader hands a loaded body an empty closure snapshot).
        // Measured: `if (true) { void Line() {} Line(); }` strips and then fails at the call with "loaded from
        // bytecode for a different environment", while the same declaration left UNCALLED strips and runs, so the
        // refusal belongs here, on the CALL. Asking the live global scope instead is WRONG: it holds the
        // block-scoped name too, and the check reads the bundle's own side for exactly that reason.
        // A BARE CALL the bundle could not resolve. The compiler recorded every bare-name call it emitted for the
        // program body, and the strip asks whether the BUNDLE could resolve that name: a builtin, an exported
        // function, or a GLOBAL binding. Measured on the sample, whose own entry calls the test harness: `Check`,
        // `CheckSummary`, `VmStats`, `exit` and `CheckFailed` are all `builtin=False exported=False global=True`
        // with a NativeFunctionAdapter there, while a block-scoped local measures `global=False kind=none`. An
        // earlier version of this check asked only the first two and refused the sample outright, which is a
        // false positive: a harness name resolves for the loaded run exactly as it does for the source run. The
        // rule is therefore "would the bundle resolve this name differently from the source run", and the global
        // scope is the part a loaded body keeps, because the strip carries the program's own declarations.
        // A non-callable global is deliberately accepted: calling it fails identically from source and bundle.
        // EVERY body the bundle carries is asked, not just the program's own: a call written inside a nested
        // lambda is compiled into that lambda's body, with its own recorder, and the sample measured the gap
        // exactly (`Com.Run(fun() { ... })` at line 437 fails in a stripped bundle because the callback calls the
        // block-scoped `Line`, which the program body's own call list never mentions).
        var carriedBodies = new List<Bytecode>();
        void CollectCarried(Bytecode? bc)
        {
            if (bc == null) return;
            carriedBodies.Add(bc);
            if (bc.NestedLambdas == null) return;
            foreach (var nested in bc.NestedLambdas) CollectCarried(nested.Compiled);
        }
        CollectCarried(ProgramBytecode);
        foreach (var f in exported) CollectCarried(f.Compiled);
        var carriedCalls = carriedBodies.SelectMany(b => b.BareCalls ?? []).Distinct().ToList();
        // The names the CARRIED BODIES THEMSELVES store as globals are resolvable too: a program body stores its
        // top-level vars with STORE_GLOBAL_FAST as it runs, so they are absent from the compile-time global scope
        // and present in the loaded run. Measured: the repro `var cb = fun() { Inner(); }; cb();` was refused
        // naming 'cb', while its program body reports `globals=[tag,cb]`, and the sample's own entry reports the
        // same shape for `passed`/`failed`. Without this the check names a name the bundle populates.
        var carriedGlobals = carriedBodies.SelectMany(b => b.GlobalSlotNames ?? []).ToHashSet(_comparer);
        // A BODY THE VM CANNOT RUN AT ALL, in the one place that has no fallback: the ENTRY (and the functions a
        // bundle carries), which is a single compiled body with no tree behind it. The measured shape is a
        // `LOAD_GLOBAL_FAST` of a name that IS a type but resolves to NO VALUE: a script INTERFACE used as a call
        // receiver (`IShapeX.area(obj)`) compiles fine, the VM bails at that instruction, and the body has already
        // done work, so the run refuses mid-way. Measured on crash_interface_test: its stripped bundle printed 38
        // of the suite's 105 checks and then stopped, where the file passes 105.
        if (stripSource && stripRefusal == null)
        {
            var unrunnableTypes = UnrunnableTypeLoads();
            if (unrunnableTypes.Count > 0)
                stripRefusal = "the stripped form cannot run a body that loads "
                    + string.Join(", ", unrunnableTypes.Select(n => $"'{n}'"))
                    + ": it names a TYPE the VM has no value for (a script interface used as a call receiver), and a"
                    + " bundle's body is compiled only, so the instruction would stop it part-way";
        }
        if (stripSource && stripRefusal == null)
        {
            // A namespace ROOT is the second name a compiled body cannot resolve (see UnrunnableNamespaceLoads),
            // and it gets its own message because the cause differs from a type's: the block's MEMBERS compile as
            // ordinary globals, while the namespace VALUE (and a member the block ADDS to a builtin namespace, which
            // is why the root resolving at compile time does not make this safe) is created only when the block RUNS.
            var unrunnableNamespaces = UnrunnableNamespaceLoads();
            if (unrunnableNamespaces.Count > 0)
                stripRefusal = "the stripped form cannot run a body that loads "
                    + string.Join(", ", unrunnableNamespaces.Select(n => $"'{n}'"))
                    + ": a namespace block's EFFECT (the namespace VALUE, and any member it adds to a builtin one) is"
                    + " created when the block RUNS, while a compiled body carries its members as plain globals, so a"
                    + " qualified access through that name would fail at run time";
        }
        if (stripSource && stripRefusal == null && carriedCalls.Count > 0)
        {
            var globals = GetGlobalScope();
            // A name a CARRIED (tree-executed) statement declares belongs here too: running that statement binds it
            // in the global scope, so a compiled statement that calls it resolves at run time. It cannot be in
            // `carriedGlobals` (those are the names COMPILED carried bodies store), which is why the compiler hands
            // its list over. Measured on `var g = fun(int n) { ... g(n - 1) ... }; print(g(4));`, whose first
            // statement is carried and whose second one calls `g` from the compiled entry.
            var carriedDeclared = programBody?.CarriedDeclaredNames;
            var unresolved = carriedCalls
                .Where(n => !_builtinNames.Contains(n)
                    && !exported.Any(f => _comparer.Equals(f.Name, n))
                    // A declaration that travels as TEXT registers at load, so the loaded run HAS this name.
                    && !textCarriedFuncs.Any(f => _comparer.Equals(f.Name, n))
                    && !globals.ContainsKey(n)
                    && !carriedGlobals.Contains(n)
                    && (carriedDeclared == null || !carriedDeclared.Contains(n, _comparer)))
                .Distinct().ToList();
            // A name a carried BARE IMPORT binds: the bundle replays that import at load (see LibraryImport), so
            // the loaded run HAS the name while the compile-time scope does not. Asked ONLY when something is
            // unresolved, so an ordinary compile never evaluates a dependency's top level: measured on
            // `import "./mod.inc"; print("m " + helper(2));`, whose own module name this check refused once the
            // import stopped declining the body.
            if (unresolved.Count > 0)
            {
                var imported = ImportedNamesOfCarriedImports(carriedImports);
                // A COMPUTED import (`Application.Path + "/m.inc" as Z`) cannot be carried as load-time data, so
                // its module names are unknown here, but its ALIAS is in the source: the entry binds that name when
                // the import runs, in order, before any statement that uses it. Without this the strip refused a
                // file the bundle runs perfectly (the alias read as an unresolvable call).
                var computedAliases = (_programStatements ?? []).OfType<ImportStmt>()
                    .Where(i => i.LiteralPath == null && i.Alias != null)
                    .Select(i => i.Alias!)
                    .ToHashSet(_comparer);
                if (imported.Count > 0 || computedAliases.Count > 0)
                    unresolved = unresolved.Where(n => !imported.Contains(n) && !computedAliases.Contains(n)).ToList();
            }
            if (unresolved.Count > 0)
                stripRefusal = "the stripped form carries no declaration for "
                    + string.Join(", ", unresolved.Select(n => $"'{n}'"))
                    + ", which a body the bundle carries CALLS, so the call could not resolve at run time";
        }
        // A COROUTINE THAT A CARRIED BODY CALLS NO LONGER REFUSES THE STRIP, and neither does a coroutine MEMBER of a
        // declaration that travels as text. Both refusals existed because the VM drove a `foreach` IN PLACE and
        // answered null for a generator it does not back; the drive now STEPS such a coroutine through the TREE, one
        // `Resume()` per element (`VmForEachState.KindTreeCoroutine`, the same call a tree `foreach` makes, which is
        // what made the Excel sample's COM-driven generator work from a bundle), so a compiled body can iterate one.
        // MEASURED with both refusals bypassed (2026-10-19), file against bundle: `_corofor` 4/0, `global_recent`
        // 42/0, `_vm_gen` 52/0 (with its `SLC_VM_MAXFRAMES` knob, without which BOTH sides hit the 30,000 cap), and a
        // class whose coroutine member travels as text prints `sum=6` from both. The declarations still travel as
        // text exactly as before; only the refusal is gone.
        if (stripSource && stripRefusal == null)
        {
            // A DEFAULT VALUE the bundle could not answer: the third way a strip can differ from its script,
            // beside an unresolvable CALL and a declaration the descriptor cannot carry. Measured before this
            // check existed: `int F(int a, int b = 3)` stripped fine and its bundle then answered "Expected
            // between 2 and 2 arguments, got 1" for the call the source accepts, blaming the caller.
            var uncarriedDefaults = UncarriableDefaults(exported, carriedGlobals);
            if (uncarriedDefaults.Count > 0)
                stripRefusal = "the stripped form carries no default value for "
                    + string.Join(", ", uncarriedDefaults)
                    + ", so a call that omits it would be refused at run time instead of using the default";
        }
        if (stripSource && stripRefusal == null)
        {
            // An INSTANCE field's initializer travels as a compiled THUNK as well (FieldDecl.InitializerThunk), so
            // what is left to refuse is the expression that thunk cannot carry, BY NAME, exactly as a parameter's
            // default is refused. Measured on the shape this exists for: `public int W = this.V + 1;` compiles to a
            // thunk that reads a NULL instance, and the bundle then failed at run time with "Cannot access property
            // 'V' on null" while the source answers 6.
            var uncarriedInitializers = UncarriableInitializers(exported, carriedGlobals);
            if (uncarriedInitializers.Count > 0)
                stripRefusal = "the stripped form cannot carry the initializer of "
                    + string.Join(", ", uncarriedInitializers)
                    + ", so an instance it builds would not hold the value the script gives it";
        }
        if (stripSource && stripRefusal == null)
        {
            // An ENUM's registry entry is a ClassInfo whose static fields are EnumValue objects, which the class
            // descriptor's value writer cannot serialize (that was the raw "value of type EnumValue cannot be
            // serialized" refusal a stripped enum program used to fail with). It travels in the v6 DECLARATIONS
            // section instead, with its members' resolved values, so it is left out of the class list here.
            var declarations = CollectProgramDeclarations(_programStatements);
            // A top-level function whose body the compiler did not take rides the same TEXT-CARRIED declaration
            // entry a generic class uses (the loader only ever re-parses and executes one), appended AFTER the
            // classes so a function naming a class finds it registered.
            if (textCarriedFuncs.Count > 0)
                declarations = declarations with
                {
                    DeclTexts =
                    [
                        .. declarations.DeclTexts,
                        .. textCarriedFuncs.Select(f => new VmBytecodeCodec.LibraryDeclText(f.Text, f.Line, f.Column)),
                    ],
                };
            // A class that travels as its own SOURCE TEXT is registered from that text at load, and so is every
            // INSTANTIATION of it (the text re-registers the template, and `new Box<int>()` instantiates again at
            // run time, as a source run does), so the descriptor sections leave both out. Measured why neither can
            // be carried any other way: an instantiation clones the template's AST and has no bytecode.
            var carriedClasses = Classes.Values
                .Where(ci => !declarations.Enums.Any(e => _comparer.Equals(e.Name, ci.Name)))
                .Where(ci => !textCarriedClasses.Contains(ci.Name)
                    && ci.TypeArguments.Count == 0 && !ci.Name.Contains('<'))
                .ToList();
            data = VmBytecodeCodec.SerializeLibrary(exported, null, null, carriedClasses,
                [.. Structs.Values.Where(sd => !textCarriedStructs.Contains(TemplateOf(sd.Name)))], null,
                bundleEntry, declarations);
            strippedSource = true;
            strippedDeclarations = declarations;
            strippedClassCount = carriedClasses.Count;
        }
        else
        {
            if (stripSource && requireStrip)
                throw new ScriptException($"Bytecode: cannot strip the source: {stripRefusal}. Compile without"
                    + " --no-source, which carries the source and behaves as the script does.");
            data = VmBytecodeCodec.SerializeBundle(exported, bundleEntry, entrySource);
            carriedInstead = stripSource ? stripRefusal : null;
        }
        var report = $"exported {exported.Count}, skipped {skipped.Count}";
        if (skipped.Count > 0) report += " (" + string.Join(", ", skipped) + ")";
        report += programBody != null
            ? $", entry {bundleEntry}"
            : $", no entry: the top level did not compile ({ProgramBodyDeclineReason ?? "declined"}), so this bundle is a function library";
        if (strippedSource)
        {
            // The class count is what was WRITTEN (an enum travels in the declarations section, not as a class
            // descriptor), and the declarations are named, so the report says what the bundle carries.
            report += $", source stripped, {strippedClassCount} class(es), {Structs.Count} struct(s)";
            var d = strippedDeclarations;
            var kinds = new List<string>();
            if (d is { Enums.Count: > 0 } e0) kinds.Add($"{e0.Enums.Count} enum(s)");
            if (d is { Delegates.Count: > 0 } d0) kinds.Add($"{d0.Delegates.Count} delegate(s)");
            if (d is { Interfaces.Count: > 0 } i0) kinds.Add($"{i0.Interfaces.Count} interface(s)");
            if (d is { Extensions.Count: > 0 } x0) kinds.Add($"{x0.Extensions.Count} extension block(s)");
            // A top-level function whose body the compiler did not take travels as its own text in the same
            // declaration list, so the report names it rather than letting `skipped 0` imply nothing was declined.
            if (textCarriedFuncs.Count > 0) kinds.Add($"{textCarriedFuncs.Count} function(s) as text");
            if (kinds.Count > 0) report += ", declaration(s): " + string.Join(", ", kinds);
        }
        if (carriedInstead != null) report += ", source carried instead (" + carriedInstead + ")";
        return (data, report);
    }

    /// <summary>Exports all VM-compiled top-level functions of this interpreter session to a
    /// LIBRARY bundle file (shared by the REPL .export -bc dotcommand). Every function gets the
    /// same namespace; the meta name comes from the caller. Returns an "exported N, skipped M (names)"
    /// report (skipped = bodies the compiler did not take: a coroutine the VM cannot back, or a body
    /// left to the tree).</summary>
    internal string ExportTopLevelToLibrary(string outPath, string metaName, string ns)
    {
        var (data, report) = ExportTopLevelToLibraryBytes(metaName, ns);
        File.WriteAllBytes(outPath, data);
        return report;
    }

    /// <summary>The bytes and report behind ExportTopLevelToLibrary, so the memory form and the file form are
    /// ONE implementation (see ExportTopLevelToBundleBytes).</summary>
    internal (byte[] Data, string Report) ExportTopLevelToLibraryBytes(string metaName, string ns)
    {
        var exported = new List<FuncDecl>();
        var skipped = new List<string>();
        RegisterNestedDeclsOfExports();
        foreach (var d in _scriptTopLevel)
        {
            // Same gate as ExportTopLevelToBundle, which states it once: a value-yield coroutine IS exportable
            // when the VM can back it, and a yield-REQUEST one is not.
            if (d.Compiled != null && (!d.HasYield || IsVmBackedCoroutine(d))) exported.Add(d);
            else skipped.Add(d.Name);
        }
        // The same refusal as the strip's, for the same reason: a library is loaded by ANOTHER run, so a default
        // it dropped would be discovered there as an arity refusal for a call this session accepts.
        var uncarriedDefaults = UncarriableDefaults(exported, null, countLiveGlobals: false);
        if (uncarriedDefaults.Count > 0)
            throw new ScriptException("Bytecode.ExportLibrary: the library cannot carry the default value of "
                + string.Join(", ", uncarriedDefaults)
                + ", so a call that omits it would be refused at run time rather than using the default.");
        var nss = new string[exported.Count];
        Array.Fill(nss, ns);
        var meta = new VmBytecodeCodec.LibraryMeta(metaName, "", "", nss);
        var data = VmBytecodeCodec.SerializeLibrary(exported, nss, meta);
        var report = $"exported {exported.Count}, skipped {skipped.Count}";
        if (skipped.Count > 0) report += " (" + string.Join(", ", skipped) + ")";
        return (data, report);
    }

    /// <summary>Whether a spec's TEXT is really a path: no line break and no statement punctuation, which a script
    /// always has and a path never does. Used only to EXPLAIN a parse failure, never to reinterpret a spec.</summary>
    private static bool LooksLikeAPath(ReadOnlyMemory<char> text)
    {
        if (text.Length == 0) return false;
        foreach (var c in text.Span)
        {
            if (c is '\n' or '\r' or '{' or ';') return false;
        }
        return true;
    }

    /// <summary>Collects everything a library can carry from one script, given as a PATH or as the script's TEXT
    /// in bytes: VM-compiled top-level functions, class/struct declarations (with compiled methods/ctors), and
    /// top-level variable value snapshots. Each module is parsed AND executed in a FRESH temporary interpreter so its
    /// top-level statements run (var initializers, class static-field initializers) before the snapshot, without
    /// polluting this interpreter's globals/Classes. NamespaceDeclStmt bodies are transparent: functions declared
    /// inside `namespace A.B { }` keep their declared path in FuncNs (parallel to Funcs), falling back to the
    /// caller's `ns` for flat functions.</summary>
    internal (List<FuncDecl> Funcs, List<string> FuncNs, List<ClassInfo> Classes, List<StructDeclStmt> Structs,
        List<(string Name, object? Value)> Variables, List<string> Skipped) CollectLibraryContent(object scriptOrBytes, string ns)
    {
        var funcs = new List<FuncDecl>();
        var funcNs = new List<string>();
        var classes = new List<ClassInfo>();
        var structs = new List<StructDeclStmt>();
        var vars = new List<(string Name, object? Value)>();
        // A spec is a PATH (a string) or the SCRIPT TEXT AS BUFFERS (bytes): the TYPE decides, so a spec never has to
        // be guessed at. Bytes are what makes a library buildable with nothing on disk, and they decode exactly as a
        // file's bytes do, which is why both cases share one reader below.
        byte[]? specBytes = scriptOrBytes as byte[];
        char[]? rented = null;
        ReadOnlyMemory<char> source;
        var specLabel = "";
        string sourceName;
        if (specBytes != null)
        {
            rented = CharBufferPool.Rent(specBytes.Length);
            var chars = Encoding.UTF8.GetChars(specBytes, 0, specBytes.Length, rented, 0);
            source = rented.AsMemory(0, chars);
            specLabel = "the script bytes";
            sourceName = "<bytes>";
        }
        else
        {
            var specPath = (string)scriptOrBytes;
            var fullPath = ResolvePath(specPath);
            if (!File.Exists(fullPath))
            {
                // A `./` spec is resolved against the RUN's directory (the one path rule), which a reader cannot
                // derive from the spec alone: `./x.slc` in a bundle compiled into TEMP looks for TEMP's x.slc, and
                // the bare message sent a whole debugging round hunting the engine instead of the directory. So
                // WHEN THE SPEC IS A `./` OR `../` PATH the resolved path is named too; a bare name keeps its own
                // wording (and its pin), because there the rule is the process directory and nothing is ambiguous.
                var resolvedNote = specPath.StartsWith("./") || specPath.StartsWith("../")
                    ? $" (resolved to '{fullPath}')"
                    : "";
                throw new ScriptException($"Bytecode.ExportLibrary: script not found '{specPath}'{resolvedNote}.");
            }
            var fileBytes = File.ReadAllBytes(fullPath);
            rented = CharBufferPool.Rent(fileBytes.Length);
            var chars = Encoding.UTF8.GetChars(fileBytes, 0, fileBytes.Length, rented, 0);
            source = rented.AsMemory(0, chars);
            specLabel = $"'{specPath}'";
            sourceName = fullPath;
        }
        try
        {
            List<Stmt> statements;
            try
            {
                var lexer = new Lexer(source);
                var tokens = lexer.GetTokens();
                var parser = new Parser(tokens, Delegates, _caseSensitive, source: source.ToString());
                // Materialize: GetStatements() is a lazy iterator, and re-enumerating it after
                // PrecompileFunctions/Interpret would re-parse into fresh, uncompiled nodes.
                statements = parser.GetStatements().ToList();
            }
            catch (ScriptException ex) when (specBytes != null && LooksLikeAPath(source))
            {
                // Keep the ERROR CODE and the position, so the engine reports this the same way it reports the
                // original and only the text changes. MEASURED LIMIT: for the PARSER's own errors this sentence is
                // dropped downstream, because that report is rendered from its structured fields ("expected
                // 'Semicolon', found 'Eof'"), while a LEXER error keeps it. The pre-flight in the registration is what
                // covers the real mistake; this is the explanation for the rest.
                throw new ScriptException(ex.ErrorCode, ex.Message
                    + " (the text is a PATH: Bytecode.ExportLibrary reads a spec as a path, while"
                    + " Bytecode.ExportLibraryBytes reads it as the script TEXT.)", ex.Line, ex.Column);
            }

            // Run the module in a FRESH interpreter so its top-level statements EXECUTE (var
            // initializers, class static-field initializers) before snapshotting. Isolated: the
            // module's globals/Classes never touch this exporter's interpreter. Handle disposed
            // via using (try/finally), same pattern as Program.cs RunFile.
            using var handle = InterpreterManager.Create(caseSensitive: CaseSensitive,
                context: "Bytecode.ExportLibrary");
            var temp = handle.Instance;
            Program.PrecompileFunctions(statements, temp);
            var prevExitCode = Environment.ExitCode;
            temp.IsModuleExecution = true;   // F2 gate: libraries keep v1 behavior (paths, no scope chain)
            var result = temp.Interpret(statements, sourceName);
            temp.IsModuleExecution = false;
            // The explicit Aborted flag catches ANY abort via exit(), including exit(0),
            // which the exit-code comparison could not distinguish from normal completion
            // when the host's exit code was already 0.
            if (result.HasError || result.Aborted)
            {
                Environment.ExitCode = prevExitCode;   // temp interpreter's exit() must not leak to the host
                throw new ScriptException(
                    $"Bytecode.ExportLibrary: module {specLabel} failed to execute: {result.ErrorMessage ?? "exited with code " + result.ExitCode}");
            }

            // Top-level funcs compiled against temp (FuncDecl.Compiled bytecode is
            // interpreter-independent, but derive from the statements that actually ran).
            // NamespaceDeclStmt bodies are transparent containers; each function keeps its
            // declared namespace path (recorded during execution) in FuncNs.
            var flattened = FlattenNamespaceContainers(statements).ToList();
            var skippedNames = new List<string>();
            foreach (var fd in flattened.OfType<FuncDecl>())
            {
                if (fd.Compiled == null)
                {
                    // Tree-only / coroutine bodies cannot be serialized, skipped (parity with
                    // the previous top-level-only behavior, which reported them the same way).
                    skippedNames.Add(fd.Name);
                    continue;
                }
                var declared = temp._declaredInNamespace.TryGetValue(fd.Name, out var d) ? d : "";
                funcs.Add(fd);
                funcNs.Add(declared.Length > 0 ? declared : ns);
            }

            // Snapshot the temp interpreter's LIVE global scope once. The dictionary is built
            // with the temp interpreter's own comparer, so TryGetValue matches vd.Name with the
            // correct case-sensitivity. Filter out funcs/classes/structs that also live there.
            var globalScope = temp.GetCurrentGlobalScope();
            foreach (var stmt in flattened)
            {
                switch (stmt)
                {
                    case ClassDeclStmt cls when temp.Classes.TryGetValue(cls.Name, out var ci):
                        // Snapshot static fields at INITIALIZER state (pre-ctor) so imported classes re-run
                        // their static ctors from the same starting point as source code. Fields the ctor
                        // writes are then set by the import-time ctor execution.
                        var exportedCi = temp._initialStaticFields.TryGetValue(cls.Name, out var initState)
                            ? ci with { StaticFields = initState.Count > 0 ? initState : null }
                            : ci;
                        classes.Add(exportedCi);
                        break;
                    case StructDeclStmt str:
                        structs.Add(str);
                        break;
                    case VarDeclStmt vd:
                        if (globalScope.TryGetValue(vd.Name, out var info)
                            && info.Value is not ScriptFunction and not ClassInfo and not StructDeclStmt)
                            vars.Add((vd.Name, info.Value));
                        break;
                }
            }
            // Static ctors must carry Compiled bytecode, imported classes have no AST Body, so a
            // tree-only ctor (e.g. bare-name field writes) would silently never run at import.
            foreach (var ci in classes)
                foreach (var ctor in ci.Constructors.Where(c => c.IsStatic))
                    if (ctor.Compiled == null)
                        throw new ScriptException(
                            $"Bytecode.ExportLibrary: static constructor of class '{ci.Name}' is not VM-compilable, " +
                            "qualify static-field writes with the class name (e.g. X.field = ...).");
            // A DEFAULT VALUE the library cannot carry is refused BY NAME here rather than written: an imported
            // function is called by ANOTHER run, so a dropped default would surface there as an arity refusal for
            // a call THIS script accepts. The names resolve against the temp interpreter's live scope, which is
            // what the imported library re-creates (its functions plus its variable snapshots).
            var uncarriedDefaults = temp.UncarriableDefaults(funcs, null, countLiveGlobals: false);
            if (uncarriedDefaults.Count > 0)
                throw new ScriptException("Bytecode.ExportLibrary: the library cannot carry the default value of "
                    + string.Join(", ", uncarriedDefaults)
                    + ", so a call that omits it would be refused at run time rather than using the default.");
            return (funcs, funcNs, classes, structs, vars, skippedNames);
        }
        finally
        {
            if (rented != null) CharBufferPool.Return(rented);
        }
    }

    /// <summary>Loads a bundle file into this interpreter's global scope (shared by the
    /// Bytecode.LoadScript native and the REPL .loadslcbc dotcommand). Returns the entry
    /// function (if the bundle declares one) and the number of functions defined.</summary>
    internal (ScriptFunction? Entry, int Count) LoadBundleFromFile(string path, bool replayImports = true)
    {
        var full = Path.GetFullPath(path);
        VerifyBundleManifest(full);
        return LoadBundleFromBytes(File.ReadAllBytes(full), Path.GetDirectoryName(full), replayImports);
    }

    /// <summary>The suffix of the manifest a `--compile` writes beside a bundle: the bundle's own path plus this.</summary>
    internal const string BundleManifestSuffix = ".sha256";

    /// <summary>
    /// Refuses a bundle that no longer matches the manifest written beside it, and does nothing when there is none.
    /// THE VERIFIER CHECKS THE FORMAT, NOT THE AUTHOR: `BytecodeVerifier` proves every body is well formed and the header
    /// proves the version and the opcode set, and neither says whether somebody EDITED the artifact after it was built.
    /// A SHA-256 beside the file makes "this is the bundle I compiled" checkable, and `--compile` writes it, so the common
    /// case needs no ceremony. Measured before this existed: a `.slcbc` with a byte changed still loaded and ran, which is
    /// right for a loader that checks format only and wrong for a shipped artifact. A manifest that cannot be read, or is
    /// empty, is treated as ABSENT rather than as a mismatch: its presence is the claim, and a half-written sidecar must
    /// not brick a bundle.
    /// </summary>
    internal static void VerifyBundleManifest(string path)
    {
        var manifest = path + BundleManifestSuffix;
        string claimed;
        try
        {
            if (!File.Exists(manifest)) { return; }
            claimed = File.ReadAllText(manifest).Trim();
        }
        catch (Exception) { return; }   // unreadable: nothing to check
        if (claimed.Length == 0) { return; }
        // `sha256sum` writes "<hex>  <name>" and marks binary mode with '*'; only the first token is the digest.
        var expected = claimed.Split([' ', '\t'], 2)[0].Trim();
        var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) { return; }
        throw new ScriptException(ErrorCode.Unspecified,
            $"Bytecode.LoadScript: '{Path.GetFileName(path)}' does not match its manifest "
            + $"'{Path.GetFileName(manifest)}': the file changed after it was hashed "
            + $"(expected {ShortDigest(expected)}, found {ShortDigest(actual)}). "
            + "Run --compile again to refresh the manifest, or delete the manifest to run the file unchecked.", 0, 0);

        static string ShortDigest(string hex) => hex.Length <= 16 ? hex : hex[..16] + "...";
    }

    /// <summary>Writes the manifest `VerifyBundleManifest` checks, in `sha256sum`'s own shape so `sha256sum -c` accepts it
    /// too. Called by `--compile` after a successful export, which is why a bundle built by that door is checked by
    /// default while one a script exported into TEMP is not.</summary>
    internal static string WriteBundleManifest(string path)
    {
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
        var manifest = path + BundleManifestSuffix;
        File.WriteAllText(manifest, $"{digest}  {Path.GetFileName(path)}\n");
        return manifest;
    }

    /// <summary>The bytes form of LoadBundleFromFile: the file form adds only the read AND its own directory, so
    /// the two cannot diverge in how a bundle is interpreted or registered. The directory is the IMPORTER for any
    /// statement the bundle replays (see RegisterLoadedDeclarations), and null for a bytes load, whose home is
    /// unknown (a `./` import then refuses rather than guessing).</summary>
    internal (ScriptFunction? Entry, int Count) LoadBundleFromBytes(byte[] data,
        string? directory = null, bool replayImports = true)
    {
        _loadedBundleDirectory = directory;
        if (data.Length >= 4 && BitConverter.ToInt32(data, 0) == VmBytecodeCodec.LibraryMagic) return LoadSourceLessProgram(data, replayImports);   // a program compiled with its source stripped
        var funcs = VmBytecodeCodec.DeserializeBundle(data, out var err, out var entryName, out var source);
        LoadedBundleSource = source;   // the program text this bundle carries, if any (see ProgramBytecode / SourceText)
        // The carried SOURCE is this bundle's program, and the types it declares are what its compiled bodies
        // refer to. The load-time shell check below runs BEFORE that text does, so a program whose body makes an
        // `is IShape` test against an interface the same file declares was refused AT LOAD ("type 'IShape'
        // referenced by the bytecode is not available in this environment") even though it could run, which made
        // that shape bundleable in neither form. Record the names the carried text declares, exactly as running
        // it would, so the check asks the question the run will answer.
        // NOTHING IS REGISTERED here: the run declares these itself, and registering early would make its own
        // class and extension declarations duplicates.
        if (!string.IsNullOrEmpty(source))
        {
            var carriedStatements = ParseCarriedSourceStatements();
            if (carriedStatements != null) _declaredTypeNames = CollectDeclaredTypeNames(carriedStatements);
        }
        if (funcs == null)
            throw new ScriptException("Bytecode.LoadScript: " + err);

        ScriptFunction? entry = null;
        foreach (var d in funcs)
        {
            // checkTypeNames: false, because a PROGRAM bundle's unresolved type name is what the RUN answers with
            // the engine's own catchable refusal, exactly as the file does (see ValidateShellEnvironment).
            ValidateShellEnvironment(d, "Bytecode.LoadScript", checkTypeNames: false);
            var fn = new ScriptFunction(d, SEmptySnapshot, d.ReturnType, GetParameterTypes(d.Params),
                d.IsRefParams, Flags: FunctionFlags.Legacy, IsOutParams: d.IsOutParams);
            CurrentFrame.Scopes.Define(d.Name, fn, d.Line, d.Column);
            if (entryName != null && StringComparer.OrdinalIgnoreCase.Equals(d.Name, entryName))
                entry = fn;
        }
        if (entryName != null && entry == null)
            throw new ScriptException($"Bytecode.LoadScript: entry function '{entryName}' not found in bundle.");
        return (entry, funcs.Count);
    }
    /// <summary>Loads a bundle that carries NO source: a program compiled with its source stripped, written
    /// as a library blob that names an entry (see VmBytecodeCodec.SerializeLibrary). Its declarations travel
    /// as DESCRIPTORS rather than as text, so they are registered here: classes and structs in the live
    /// registries under their plain names, every declaration also in the root global scope (the scope a
    /// compiled body's global loads read), and the entry, which the caller invokes.</summary>
    private (ScriptFunction? Entry, int Count) LoadSourceLessProgram(byte[] data, bool replayImports)
    {
        var funcs = VmBytecodeCodec.DeserializeLibrary(data, out var err, out _, out _,
            out var classes, out var structs, out var vars, out var entryName, out var declarations);
        LoadedBundleSource = null;   // there is no program text to replay: the entry runs from bytecode
        if (funcs == null)
            throw new ScriptException("Bytecode.LoadScript: " + err);            // A library bundle names NO entry, and LoadScript refuses one: a library is what `import` and
                                                                                 // `.loadslcbc` load, by alias, into a namespace. Only a program that names its entry is a program
                                                                                 // bundle, so that is what makes the two separable, and nothing is registered before this check.
        if (string.IsNullOrEmpty(entryName))
            throw new ScriptException("Bytecode.LoadScript: not a ScriptLangCore bytecode blob: this library"
                + " names no entry, so load it with `import` or `.loadslcbc`.");

        // Classes before functions: a body may reach one, and a static ctor runs on first touch. Linking
        // repeats until no class waits for a parent, so a parent this bundle also declares resolves on
        // whichever pass reaches it, and one declared elsewhere resolves as it already stands.
        // A PENDING parent is not a final one: ClassInfo is a record, so linking a class creates a NEW record
        // under its name, and a child that linked against the OLD (parent-less) copy keeps a chain that stops
        // one level early. That is measured: `class A { public int Pub; }` / `class B extends A` /
        // `class C extends B { int Read() { return this.Pub; } }` read A's field through B in a source run and
        // reported "Field or event 'Pub' not found in class 'C'" from a stripped bundle, which is the same
        // defect `depth16_test` (16 levels) and `cross_module_test` (an imported parent) each showed. So a
        // class whose parent is STILL PENDING waits: the loop is a topological drain, and its pass bound is
        // what leaves a genuinely cyclic declaration to the final sweep below.
        var pending = classes is { Count: > 0 } ? new List<ClassInfo>(classes) : [];
        var pendingNames = new HashSet<string>(pending.Select(c => c.Name), _comparer);
        // The pass bound is the ORIGINAL count, and that is not cosmetic: the bound used to be the list's
        // CURRENT length, which SHRINKS as classes are linked, so the deepest classes never got their pass and
        // the final sweep below registered them UNLINKED. Measured on a three-class chain: `C -> (null) name=B`
        // after the drain, so `this.Pub` (declared in the grandparent) was refused from a stripped bundle while
        // the source answered, and the same one-level-short chain is what `depth16_test` (16 levels) and
        // `cross_module_test` (an imported parent) each reported.
        var pendingCount = pending.Count;
        for (var pass = 0; pass <= pendingCount; pass++)
        {
            var progressed = false;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var ci = pending[i];
                if (ci.ParentName is { Length: > 0 } pn)
                {
                    // Not declared at all yet: it may arrive from a later slice of this same bundle.
                    if (!Classes.TryGetValue(pn, out _)) continue;
                    // Declared, but ITS OWN link is still to come: linking now would bind this class to the
                    // parent-less copy, which is the one-level-short chain this drain exists to prevent.
                    if (pendingNames.Contains(pn)) continue;
                }
                var linked = ci.ParentName is { Length: > 0 } p ? ci with { Parent = Classes[p] } : ci;
                if (Classes.TryAdd(linked.Name, linked)) BindLoadedName(linked.Name, linked);
                pending.RemoveAt(i);
                pendingNames.Remove(ci.Name);
                progressed = true;
            }
            if (!progressed) break;
        }
        foreach (var ci in pending) if (Classes.TryAdd(ci.Name, ci)) BindLoadedName(ci.Name, ci);
        if (structs is { Count: > 0 })
            foreach (var sd in structs)
                if (Structs.TryAdd(sd.Name, sd))
                    // A struct's NAME binds the same TypeInfo handle a source run binds (see
                    // StructDefinitionHandle): binding the declaration NODE made a stripped bundle report
                    // "StructDeclStmt" for `Pt` and answer False for `Pt is ClassInfo`.
                    BindLoadedName(sd.Name, StructDefinitionHandle(sd.Name));
        if (vars is { Count: > 0 })
            foreach (var (name, value) in vars) BindLoadedName(name, value);
        // v6: the declarations come before the function shells are validated, because a body may reference an
        // enum, a delegate or an interface this bundle declares, and the validation asks the registry.
        RegisterLoadedDeclarations(declarations, replayImports);
        ScriptFunction? entry = null;
        foreach (var d in funcs)
        {
            ValidateShellEnvironment(d, "Bytecode.LoadScript", checkTypeNames: false);
            // compile time (`RegisterNestedDecls` fills both maps from `Bytecode.NestedDecls`). A bundle has no such
            // in-memory list, so the maps stayed EMPTY and `__nested_fn__(name, line, column, ...)` fell through to the
            // SCOPE, which keeps only ONE of two same-named helpers (the bind below replaces a same-signature binding).
            // Measured on `nested_decl_hoisting_test` 1a: each lambda declares `void Note(string s)`, the first call
            // resolved to the SECOND lambda's body, whose `label` capture is not passed, and `label + ":" + s` printed
            // `:one` (null coerces to "") instead of `a:one`, so both 1a and 1b failed from the bundle only.
            RegisterNestedDecl(d);
            var fn = new ScriptFunction(d, SEmptySnapshot, d.ReturnType, GetParameterTypes(d.Params),
                d.IsRefParams, Flags: FunctionFlags.Legacy, IsOutParams: d.IsOutParams);
            // A bundle's function table can hold the SAME name twice, which a source run binds as an overload set
            // (RULES.md): two declarations of one free function whose signatures differ. Binding only the last one
            // made the stripped bundle refuse a call the file answers (measured: `Ar(7)` with `Ar(int)` and
            // `Ar(int,int)` in the same file answered "Expected between 2 and 2 arguments" from the bundle), so
            // this site applies the same rule the declaration does: same signature replaces, different joins.
            var bound = CurrentFrame.Scopes.TryGet(d.Name, out var alreadyBound) ? alreadyBound : null;
            if (bound is ScriptFunction prior)
            {
                if (SameSignature(prior.Declaration, d))
                    BindLoadedName(d.Name, fn, d.Line, d.Column);
                else
                {
                    var set = new ScriptOverloadSet();
                    set.AddOrReplace(prior);
                    set.AddOrReplace(fn);
                    BindLoadedName(d.Name, set, d.Line, d.Column);
                }
            }
            else if (bound is ScriptOverloadSet priorSet)
                priorSet.AddOrReplace(fn);
            else
                BindLoadedName(d.Name, fn, d.Line, d.Column);
            if (entryName != null && StringComparer.OrdinalIgnoreCase.Equals(d.Name, entryName))
                entry = fn;
        }
        if (entryName != null && entry == null)
            throw new ScriptException($"Bytecode.LoadScript: entry function '{entryName}' not found in bundle.");
        return (entry, funcs.Count);
    }

    /// <summary>The TYPE names a carried body LOADS as a global and the VM cannot turn into a value (a name that IS
    /// a type but has none: a script INTERFACE), which would BAIL at that instruction. A bundle's body is compiled
    /// only, so a bail in the ENTRY ends the run where the file answers. The walk reads the EMITTED bytecode with
    /// the format table's own operand sizes, so a decoded stream cannot disagree with what was written, and it
    /// descends into nested lambdas because their bodies travel in the same table.</summary>
    private List<string> UnrunnableTypeLoads()
    {
        var names = new List<string>();
        CarriedBodyGlobals(out var stored, out var loaded);
        foreach (var name in loaded)
        {
            // The VM's own order, as far as a static reader can answer it, and each clause is measured:
            //  - a name the carried bodies STORE is a value in the loaded run (asking ResolveGlobalValue alone
            //    named every top-level `var`, because --compile runs no statement, so none of them is bound yet);
            //  - a name already resolved to a VALUE here is one the VM pushes (a class, a struct, an enum, a CLR
            //    type: this is what keeps `Box.S` out, since a class binding is a value while an interface's is not);
            //  - a TYPE VALUE is pushed too, and a name that is not a type at all reports Undefined.
            // What is left is exactly what the VM BAILS on, and the registry index answers a type
            // case-insensitively, which is why a `var impl = new Impl();` beside `class Impl` needs the stored set.
            if (stored.Contains(name)) continue;
            if (ResolveGlobalValue(name) is not VmFallback) continue;
            if (ResolveTypeNameAsValue(name) != null) continue;
            if (!IsKnownTypeName(name)) continue;
            names.Add(name);
        }
        return names.Distinct().ToList();
    }

    /// <summary>The NAMESPACE ROOT names a carried body LOADS, which a stripped bundle cannot resolve however its
    /// bodies compile: a namespace's VALUE is created by the INTERPRETER when the block RUNS (a `Namespace` bound in
    /// the declaring scope), while a compiled body registers only the block's MEMBERS. Measured on the shape this
    /// exists for: `namespace NsProbe1 { int f() { return 1; } }` with `NsProbe1.f()` stripped fine and its bundle
    /// then answered "Undefined variable 'NsProbe1'" where the file answers 1, while the BARE `f()` route works from
    /// the bundle too (the members do compile), which is why this refuses the FILE by name instead of declining the
    /// block and losing that half.
    ///
    /// A root that IS a builtin namespace is refused as well, and that is measured rather than cautious: with the
    /// check absent, `namespace Json { int answer() { return 42; } }` beside `Json.Parse("7") + Json.answer()`
    /// stripped fine and its bundle answered "Method 'answer' not found in namespace 'Json'" where the file answers
    /// 49. So the load resolving in the loaded run does not make the block's EFFECT travel, which is why this asks
    /// for a LOAD of the root and not for the root to be unresolvable.</summary>
    private List<string> UnrunnableNamespaceLoads()
    {
        var roots = TopLevelNamespaceRoots();
        if (roots.Count == 0) return [];
        CarriedBodyGlobals(out var stored, out var loaded);
        return loaded.Where(n => !stored.Contains(n) && roots.Contains(n)).Distinct().ToList();
    }

    /// <summary>The root segment of every TOP-LEVEL `namespace` declaration (a nested `namespace A { namespace B
    /// { } }` binds A), which is the name the declaring scope holds and a carried body can therefore load.</summary>
    private HashSet<string> TopLevelNamespaceRoots()
    {
        var roots = new HashSet<string>(_comparer);
        void Scan(IEnumerable<Stmt> stmts)
        {
            foreach (var s in stmts)
            {
                if (s is not NamespaceDeclStmt ns || ns.Segments.Count == 0) continue;
                roots.Add(ns.Segments[0]);
                Scan(ns.Body);
            }
        }
        if (_programStatements != null) Scan(_programStatements);
        return roots;
    }

    /// <summary>The two halves a carried-body name check needs, from ONE walk: the names the bodies STORE as globals
    /// (so the loaded run HAS them as values) and the names they LOAD. The body's own GlobalSlotNames cannot answer
    /// the first question, and the measurement is why: it lists a name that is only ever LOADED, so the first
    /// version of that check excluded the very shape it exists for (`IShapeX` came back as "carried"). What it
    /// cannot see is a name stored by a body that runs LATER; that direction is a missed refusal, not a wrong one.
    /// The walk reads the EMITTED bytecode with the format table's own operand sizes, so a decoded stream cannot
    /// disagree with what was written, and it descends into nested lambdas because their bodies travel in the same
    /// table.</summary>
    private void CarriedBodyGlobals(out HashSet<string> stored, out List<string> loaded)
    {
        var storedSet = new HashSet<string>(_comparer);
        var loadedList = new List<string>();
        var seen = new HashSet<Bytecode>();
        void Walk(Bytecode? bc)
        {
            if (bc == null || !seen.Add(bc)) return;
            var code = bc.Code;
            for (var ip = 0; ip + BytecodeFormat.OpcodeWidthBytes <= code.Length;)
            {
                var op = (Opcode)code[ip];
                OperandKind kind;
                try { kind = BytecodeFormat.Kind(op); }
                catch (ArgumentOutOfRangeException)
                {
                    // A byte that is not an opcode means the emitter and the format table disagree, or the body is
                    // corrupt. Refuse in the ENGINE's words rather than letting the format table's own
                    // ArgumentOutOfRangeException reach whoever asked for the bundle: measured before this guard, a
                    // walk that mis-decoded a body reported "opcode has no declared operand shape, add it here and to
                    // tools/check-opcode-coverage.sh's expectations" plus "Actual value was 135" to a caller of
                    // --compile (see the strip's own instruction-walk note on InstructionSizeAt for that bug).
                    throw new ScriptException(
                        $"the compiled body does not decode: 0x{code[ip]:x2} at ip {ip} is not an opcode, which is an"
                        + " engine defect rather than a script's (the emitter and the format table disagree)");
                }
                if (bc.GlobalSlotNames is { } slots
                    && op is Opcode.LOAD_GLOBAL_FAST or Opcode.STORE_GLOBAL_FAST)
                {
                    var slot = code[ip + BytecodeFormat.FieldAt(kind, 0)];
                    if (slot < slots.Length)
                    {
                        if (op == Opcode.STORE_GLOBAL_FAST) storedSet.Add(slots[slot]);
                        else loadedList.Add(slots[slot]);
                    }
                }
                // The CODE-aware size, never the fixed prefix: a MAKE_LAMBDA carries one (local index, written flag)
                // pair PER CAPTURE, so the prefix under-measures every capturing lambda and the walk then reads the
                // capture list as opcodes. Measured on `crash_udf_leak`, whose two capturing lambdas walked into
                // "bad op=108 at ip=1704 of 1947" and made a strippable file refuse.
                ip += BytecodeFormat.InstructionSizeAt(kind, code, ip + BytecodeFormat.OpcodeWidthBytes);
            }
            foreach (var lam in bc.NestedLambdas ?? []) Walk(lam.Compiled);
        }
        Walk(ProgramBytecode);
        foreach (var f in _scriptTopLevel) Walk(f.Compiled);
        stored = storedSet;
        loaded = loadedList;
    }

    /// <summary>The names the carried BARE imports will BIND at load, so the strip's bare-call check can see what
    /// the loaded run sees. It asks the module the same way the import does (<see cref="LoadModule"/>, which caches
    /// it in <c>Modules</c>) and reads its export NAMES: the BINDING is the import visitor's job, and doing it here
    /// would register a dependency's classes among the descriptors this export is writing. Only a BARE import can
    /// bind a name a bare call reaches (an alias binds a NAMESPACE, and a `.slcbc` import requires an alias), and a
    /// module that cannot be loaded answers nothing rather than failing the compile, which is what the default
    /// compile would otherwise start doing for a file it used to carry the source of.</summary>
    private IReadOnlySet<string> ImportedNamesOfCarriedImports(IReadOnlyList<VmBytecodeCodec.LibraryImport> imports)
    {
        var names = new HashSet<string>(_comparer);
        foreach (var im in imports)
        {
            if (im.Alias != null) continue;                                                 // an alias binds a namespace
            if (im.Path.EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase)) continue;    // a library needs an alias
            try
            {
                var resolved = ResolvePath(im.Path);
                if (!Modules.TryGetValue(resolved, out var moduleInfo))
                {
                    if (!File.Exists(resolved)) continue;
                    moduleInfo = LoadModule(resolved, im.Line, im.Column);
                }
                foreach (var name in moduleInfo.Exports.Keys) names.Add(name);
            }
            catch { /* nothing to add: the strip then refuses by name, or carries the source, as it did before */ }
        }
        return names;
    }

    /// <summary>Registers what a bundle's v6 DECLARATIONS section carries: enums, delegates, interfaces and
    /// extension blocks. It goes through the SAME registration the interpreter's visitors use, so a loaded
    /// declaration and a declared one cannot drift, and it binds each name where a compiled body reads it
    /// (the loader's own BindLoadedName, first binding wins).</summary>
    private void RegisterLoadedDeclarations(VmBytecodeCodec.LibraryDeclarations? decls, bool replayImports = true)
    {
        if (decls == null || decls.IsEmpty) return;
        // IMPORTS FIRST: the declaration registrations below may name a type an imported module brings in, and the
        // statement must run where a script's own would, so the replay goes through the SAME visitor
        // (VisitImportStmt) rather than a second implementation. The importer is the bundle's own directory, which
        // is what the carried-source route already gives an import (measured: a bundle whose source is carried
        // resolves `./mod.inc` beside the BUNDLE, and a `./` path refuses outright without a current directory).
        if (replayImports && decls.Imports.Count > 0)
        {
            var nodes = new List<Stmt>(decls.Imports.Count);
            foreach (var im in decls.Imports)
                nodes.Add(ImportStmt.Create(im.Path, im.Alias, im.Line, im.Column));
            var savedDirectory = _currentDirectory;
            if (_loadedBundleDirectory != null) _currentDirectory = _loadedBundleDirectory;
            try
            {
                // ONE statement at a time with THIS interpreter's frame current, NOT through Interpret: Interpret
                // pushes a frame whose scope is a COPY of the globals, so the names the visitor binds (its own
                // `CurrentFrame.Scopes.Define`) would be discarded with the frame, while a loaded body reads its
                // globals from the store `BindLoadedName` writes. Measured with the copy: the global scope gained
                // NOTHING and the bundle failed with "loaded from bytecode for a different environment" on the
                // module's own name; executing the statements directly puts `helper`/`M` in the global scope and
                // the bundle prints what the file prints.
                //
                // A FAILING import is NOT swallowed (the declaration registration above swallows, and defers to the
                // call): the entry cannot run without its module, so the load fails with the module's own message
                // ("Module 'x' not found") instead of letting the entry fail later with an undefined name.
                foreach (var node in nodes) Execute(node);
            }
            finally { _currentDirectory = savedDirectory; }
        }
        foreach (var e in decls.Enums)
        {
            var enumClass = RegisterEnumCore(e.Name, e.Members, e.Line, e.Column, e.Access);
            if (enumClass != null) BindLoadedName(e.Name, enumClass);
        }
        foreach (var d in decls.Delegates)
        {
            _ = VisitDelegateDecl(DelegateDecl.Create(d.Name, d.ReturnType, d.Parameters, d.IsRefParams,
                d.IsOutParams ?? [], d.Access, d.Line, d.Column));
            BindLoadedName(d.Name, d);
        }
        foreach (var i in decls.Interfaces)
            RegisterInterface(InterfaceDeclStmt.Create(i.Name, i.Parents, i.Members,
                i.Defaults.Count == 0 ? EmptyInterfaceDefaults : i.Defaults.ToDictionary(d => d.Key, d => d.Decl),
                properties: i.Properties.Count == 0 ? null : i.Properties,
                typeParameters: i.TypeParameters, line: 0, col: 0));
        foreach (var x in decls.Extensions)
            RegisterExtensionMembers(x.TypeParameters, x.Target, x.Members, x.ContainerName, x.ReceiverName);
        // A TEXT-CARRIED declaration arrives as its own SOURCE TEXT and is registered LAST, so that the enums,
        // delegates, interfaces and extensions above exist if its declaration names them (`implements`, a field
        // type, a parameter). TWO declarations travel this way: a GENERIC class, which no descriptor can reproduce
        // (an instantiation clones the template's AST and carries no bytecode at all), and a TOP-LEVEL FUNCTION
        // whose body the compiler did not take (a request-yielding coroutine, or any other body left to the tree),
        // for which the wire has no body to carry. The text is handed to the engine's OWN parser and the statements
        // it yields run through the same visitor a source run reaches, which is what makes the template, its fields,
        // its constructors, its nested classes and its events arrive exactly as written, and what makes a
        // text-carried function a TREE-BACKED body the loaded run can call exactly as the source run did. ONE
        // statement at a time with THIS interpreter's frame current, for the reason the import replay states
        // (Interpret would push a frame whose scope is a copy).
        foreach (var ct in decls.DeclTexts)
        {
            // The export REFUSES a generic class it has no text for, so a blob cannot carry an empty one; this
            // is defensive rather than a supported shape.
            if (string.IsNullOrEmpty(ct.Text)) continue;
            var parsed = new Parser(new Lexer(ct.Text.AsMemory()).GetTokens(), Delegates, _caseSensitive,
                source: ct.Text).GetStatements().ToList();
            foreach (var node in parsed)
            {
                Execute(node);

                // RUNNING the declaration registers the type but does not put its NAME where a LOADED BODY reads one:
                // the body loads globals, while the visitor binds the declaration into the current scope. The enum arm
                // above binds explicitly for the same reason, so a text-carried class/struct does it too. Measured:
                // a class text-carried for a callable static answered `Undefined variable 'Lambdas'` from its bundle
                // until this line, while `Execute` alone had registered it (`crash_static_edge_cases`).
                if (node is ClassDeclStmt cdText && Classes.TryGetValue(cdText.Name, out var classValueText))
                    BindLoadedName(cdText.Name, classValueText);
                else if (node is StructDeclStmt sdText && Structs.TryGetValue(sdText.Name, out var structValueText))
                    BindLoadedName(sdText.Name, StructDefinitionHandle(sdText.Name));
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, FuncDecl> EmptyInterfaceDefaults =
        new Dictionary<string, FuncDecl>();

    /// <summary>Registers an enum as the ClassInfo its members live on, with their RESOLVED values: the ONE
    /// place an enum becomes registry state. The visitor evaluates its member expressions and hands them here;
    /// a v6 bundle load hands the values its descriptor carries. Null when the name is already taken, the same
    /// warning-and-skip the visitor has always done.</summary>
    private ClassInfo? RegisterEnumCore(string name, IReadOnlyList<(string Name, long Value)> members,
        int line, int column, AccessModifier access)
    {
        if (Classes.ContainsKey(name))
        {
            Logging.Log.Warning("Script", $"Enum '{name}' already defined. Skipping.", line, column);
            return null;
        }
        var staticFields = new Dictionary<string, object?>(_comparer);
        foreach (var (memberName, value) in members)
            staticFields[memberName] = new EnumValue(name, value);
        var enumClass = new ClassInfo(name, null, [], [], [], [], [],
            new Dictionary<TokenType, FuncDecl>(), line, access)
        {
            StaticFields = staticFields
        };
        Classes[name] = enumClass;
        CurrentFrame.Scopes.Define(name, enumClass);
        return enumClass;
    }

    /// <summary>Collects what a v6 DECLARATIONS section carries from a program's statements: enum members with
    /// their RESOLVED values, delegate signatures, interface contracts and extension blocks whose members all
    /// carry bytecode. The enum values are read from the REGISTRY the visitor filled (an enum's members become
    /// EnumValue objects the moment it is declared), never by re-evaluating an expression here. A shape this
    /// cannot reproduce is not dropped silently: the strip check refuses it by name before the writer runs.</summary>
    private VmBytecodeCodec.LibraryDeclarations CollectProgramDeclarations(IEnumerable<Stmt>? statements)
    {
        var enums = new List<VmBytecodeCodec.LibraryEnum>();
        var delegates = new List<DelegateInfo>();
        var interfaces = new List<VmBytecodeCodec.LibraryInterface>();
        var extensions = new List<VmBytecodeCodec.LibraryExtension>();
        var imports = new List<VmBytecodeCodec.LibraryImport>();
        var declTexts = new List<VmBytecodeCodec.LibraryDeclText>();
        if (statements != null)
            foreach (var stmt in FlattenNamespaceContainers(statements))
            {
                switch (stmt)
                {
                    // An IMPORT travels as the statement's own data, and the compiler emits nothing for it in a
                    // program body, so this list is what makes such a file strippable at all: measured, 23 of the
                    // corpus's 122 non-strippable suites were blocked by nothing but imports, and each one carried
                    // its whole source for a line or two.
                    // A LITERAL path travels as load-time data (LibraryImport), which is what lets a
                    // declaration-only load still bind the module's names. A COMPUTED path (`Application.Path +
                    // "/m.inc"`) is a run-time value, so it is deliberately NOT carried here: the entry runs the
                    // import at its own position through __run_import__, and only that path can evaluate it.
                    case ImportStmt im when im.LiteralPath is { } literalImportPath:
                        imports.Add(new VmBytecodeCodec.LibraryImport(literalImportPath, im.Alias, im.Line, im.Column));
                        break;
                    case ImportStmt:
                        break;
                    case EnumDeclStmt e:
                        {
                            var pairs = new List<(string, long)>();
                            if (Classes.TryGetValue(e.Name, out var info) && info.StaticFields != null)
                                foreach (var (memberName, _) in e.Members)
                                    if (info.StaticFields.TryGetValue(memberName, out var memberValue)
                                        && memberValue is EnumValue ev)
                                        pairs.Add((memberName, ev.Value));
                            enums.Add(new VmBytecodeCodec.LibraryEnum(e.Name, e.Access, e.Line, e.Column, pairs));
                            break;
                        }
                    case DelegateDecl d:
                        delegates.Add(new DelegateInfo(d.Name, d.ReturnType, d.Parameters, d.IsRefParams, d.Access,
                            d.Line, d.Column, IsOutParams: d.IsOutParams));
                        break;
                    case InterfaceDeclStmt i:
                        // A DEFAULT BODY and a PROPERTY ACCESSOR are bodies, so they travel as shells (the key and
                        // the accessor list are what the registration keys them by). The order is ORDINAL by key so
                        // a dump of the same file is comparable, since the AST holds the defaults in a dictionary.
                        interfaces.Add(new VmBytecodeCodec.LibraryInterface(i.Name, i.TypeParameters, i.Parents,
                            i.Members,
                            [.. i.Defaults.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                .Select(kv => (kv.Key, kv.Value))],
                            i.Properties));
                        break;
                    case ExtensionDeclStmt x:
                        extensions.Add(new VmBytecodeCodec.LibraryExtension(x.Target, x.TypeParameters,
                            x.ContainerName, x.ReceiverName, x.Members));
                        break;
                    case ClassDeclStmt c:
                        // A class body may declare extension blocks too (the nested static container rule), and
                        // they register exactly like a top-level one, with the container's name. The wire list
                        // is FLAT, so they belong here or they are lost: measured on
                        // assets/language/com/com_excel_sheets_test.slc, whose XlExt class declares the block
                        // holding XlType, XlName and friends; the source run passed 42/42 while the stripped
                        // bundle failed with "Method 'XlType' not found on type ComNode".
                        // A class whose declaration travels as TEXT registers these itself, so the descriptor sections
                        // must NOT also carry them: measured, an extension block was declared twice and the load
                        // answered "Extension member 'Tag' for 'string' (static) is already declared"
                        // (vm_nosource_test check 26).
                        if (!TravelsAsClassText(c))
                        {
                            CollectNestedExtensions(c, extensions);
                            // Its own delegates too, and for the same reason plus one more: they are what an EVENT is
                            // declared with, so a missing one makes a carried event unusable.
                            CollectNestedDelegates(c, delegates);
                        }
                        // A class travels as its own SOURCE TEXT in two cases, and the reason is the same for both: a
                        // class DESCRIPTOR carries bytecode, and neither a GENERIC class's instantiation nor a member
                        // whose body the compiler declined has any. The text re-registers the declaration at load,
                        // which gives the template its instantiation path back and a declined body an owner.
                        if (TravelsAsClassText(c))
                            declTexts.Add(new VmBytecodeCodec.LibraryDeclText(c.SourceText, c.Line, c.Column));
                        break;
                    case StructDeclStmt s:
                        // The same rule one door over: a struct descriptor carries bytecode, so it cannot carry a
                        // member the compiler declined (measured on null_return_pattern_test, whose SNuller.M is the
                        // shape). The text re-registers the struct at load, member bodies included.
                        if (TravelsAsStructText(s))
                            declTexts.Add(new VmBytecodeCodec.LibraryDeclText(s.SourceText, s.Line, s.Column));
                        break;
                }
            }
        return new VmBytecodeCodec.LibraryDeclarations(enums, delegates, interfaces, extensions, imports, declTexts);
    }

    /// <summary>The extension blocks a class body declares, flat, deepest first is not needed: registration is
    /// name based. Recurses because a class can nest a class, and that inner body may declare a block too.</summary>
    /// <summary>The names of every class that travels as its OWN SOURCE TEXT when a bundle is stripped: a class
    /// declaring TYPE PARAMETERS, a class declaring a MEMBER whose body the compiler did not take, and every class in
    /// whose subtree one of those is (see ClassDeclStmt.SourceText). The reason is the same for both: a class
    /// descriptor carries BYTECODE, and neither a generic class's instantiation nor a declined body has any, so the
    /// descriptor would register a member a loaded caller cannot run. The OUTER name is what matters for the
    /// descriptor sections, because registering the outer declaration recurses into its nested classes, so the walk
    /// adds the outer name and then descends. The set follows the interpreter's case rule.</summary>
    private void CollectTextCarriedTypeNames(IEnumerable<Stmt>? statements, HashSet<string> classNames,
        HashSet<string> structNames)
    {
        if (statements == null) return;
        foreach (var stmt in FlattenNamespaceContainers(statements))
            if (stmt is ClassDeclStmt c)
                CollectClass(c);
            else if (stmt is StructDeclStmt s)
                CollectStruct(s);
        return;

        // A text-carried declaration takes its WHOLE SUBTREE with it: its own text re-registers the outer and every
        // type it nests, so a nested type must not ALSO travel as a descriptor, or the load registers it twice and the
        // descriptor copy (with no nesting, no callable statics) is the one that can win.
        void CollectClass(ClassDeclStmt c)
        {
            foreach (var nested in c.NestedClasses) { classNames.Add(nested.Name); CollectClass(nested); }
            foreach (var nestedStruct in c.NestedStructs) { structNames.Add(nestedStruct.Name); CollectStruct(nestedStruct); }
            if (TravelsAsClassText(c)) classNames.Add(c.Name);
        }

        void CollectStruct(StructDeclStmt s)
        {
            foreach (var nested in s.NestedClasses) { classNames.Add(nested.Name); CollectClass(nested); }
            foreach (var nestedStruct in s.NestedStructs) { structNames.Add(nestedStruct.Name); CollectStruct(nestedStruct); }
            if (TravelsAsStructText(s)) structNames.Add(s.Name);
        }
    }

    /// <summary>True when a class declares a member whose BODY the compiler did not take, so a class descriptor
    /// (which carries bytecode) would register a member the loaded run cannot run. Such a class travels as its own
    /// source text instead, exactly as a generic class does, and the loader re-registers every member it declares.
    /// A CONSTRUCTOR is deliberately NOT one of the triggers: a compiled `new` reaches the constructor through the
    /// VM, which needs its bytecode, so text-carrying a constructor would not help (measured on constructor_test,
    /// whose bundle stopped part-way). An uncompiled constructor therefore keeps refusing, as it always did.</summary>
    /// <summary>The ONE question both halves ask: does this class travel as its own SOURCE TEXT when a bundle is
    /// stripped? The emission site and the descriptor-exclusion list disagreed by one condition once (a class whose
    /// STATIC FIELD holds a lambda was excluded from the descriptors but no text was written for it), which lost the
    /// declaration entirely: measured, `Lambdas.Add(3, 4)` answered `Undefined variable 'Lambdas'` from the bundle
    /// while every other class in the file loaded. One predicate, two callers, so they cannot drift.</summary>
    private static bool TravelsAsClassText(ClassDeclStmt c)
        => c.DeclaresGenericClass || HasUncompiledMemberBody(c) || HasCallableFieldInitializer(c)
           // NESTED TYPES: a descriptor travels ONE entry per class and carries no link to what a class nests
           // (`ClassInfo.NestedClasses` is empty for a loaded class), so `Level1.Level2.L2` answered "Static member
           // 'Level2' not found in class 'Level1'" from a bundle (crash_static_edge_cases section 5). The declaration's
           // own TEXT re-registers the outer and everything it nests, which is why nesting sends it down this road.
           || c.NestedClasses.Count > 0 || c.NestedStructs.Count > 0
           // A CLR BASE IS A BRIDGE THE DESCRIPTOR CANNOT CARRY (ScriptTextBridge): the parent link travels, the
           // bridge does not, so a loaded copy of such a class would no longer be a `TextWriter` and `Console.SetOut`
           // would refuse it. The declaration text re-registers it with its base, which is why this sends it down
           // the same road as nesting.
           || c.ParentName is { } cn && ScriptTextBridge.BridgedBase(FindLoadedClrType(cn)) is not null;

    /// <summary>True when a class declares a member whose BODY the compiler did not take, CONSTRUCTORS included. The
    /// ctor entry is the round that closed the last refusal of this family: the strip's own member check refuses
    /// `a constructor of 'X', whose body the compiler did not take`, while this predicate did not look at
    /// constructors at all, so the pair disagreed and two suites were refused BY NAME for a shape the text carrier
    /// could have carried (`constructor_test`'s `Shape` and `crash_static_keyword`'s `Database`, both STATIC
    /// constructors whose body writes a static field by bare name and therefore declines: see
    /// HasBareNameFieldWrite). A class is what the text re-registers, constructors included, so the predicate and the
    /// refusal now ask one question.</summary>
    private static bool HasUncompiledMemberBody(ClassDeclStmt c)
        => c.Methods.Any(m => m.Compiled == null)
        || c.OperatorMethods.Values.Any(m => m.Compiled == null)
        || c.Constructors.Any(x => x.Compiled == null)
        || (c.Destructor != null && c.Destructor.Compiled == null);

    /// <summary>True when a FIELD's initializer is a LAMBDA, which a class descriptor cannot carry: the descriptor
    /// writes a field's initializer as a compiled THUNK, and a lambda has no such body (it is a nested-lambda record the
    /// class section has no slot for). Measured: `public static var Add = fun(int a, int b) { … };` called as
    /// `Lambdas.Add(3, 4)` answered "Static method 'Add' not found in class 'Lambdas'" from a bundle whose loaded class
    /// reported `StaticFields = null` (crash_static_edge_cases), while the file answers 7. Such a class travels as its
    /// own SOURCE TEXT, so the loader re-registers the field and its lambda together.</summary>
    private static bool HasCallableFieldInitializer(ClassDeclStmt c)
        => HasCallableFieldInitializer(c.Fields);

    private static bool HasCallableFieldInitializer(IEnumerable<FieldDecl> fields)
        => fields.Any(f => f.Initializer is LambdaExpr);

    /// <summary>The struct twin of <see cref="TravelsAsClassText"/>, and it exists for the same reason: the emission
    /// site and the descriptor-exclusion list must ask ONE question. Measured on the shape below it: a struct declaring
    /// `public static var Add = fun(int a, int b) { … };` answered "Static method 'Add' not found in struct 'Calc'"
    /// from a bundle once the class case was fixed and the struct case was not.</summary>
    private static bool TravelsAsStructText(StructDeclStmt s)
        => HasUncompiledStructMemberBody(s) || HasCallableFieldInitializer(s.Fields)
           // A STATIC FIELD with an INITIALIZER: a struct descriptor carries the field's NAME and TYPE (and its
           // `IsStatic` flag) but NOT its value, and a struct's statics live in the engine's per-struct table rather
           // than in a ClassInfo snapshot, so a loaded copy registered the field with no value at all. Measured:
           // `struct Config { public static string AppName = "StructApp"; }` answered "Static member 'AppName' not
           // found in struct 'Config'" from a stripped bundle while the file answered "StructApp", and the strip had
           // shipped that bundle because nothing refused it (crash_static_keyword, whose own refusal named an
           // unrelated CONSTRUCTOR and so masked this). The declaration's TEXT re-registers the struct and evaluates
           // its initializers, which is the same route the two conditions above take.
           || s.Fields.Any(f => f.IsStatic && f.Initializer != null)
           // The nested-types condition of TravelsAsClassText, one door over: `struct OuterStruct { public struct
           // Inner { … } }` needs the same treatment (crash_static_edge_cases section 6).
           || s.NestedClasses.Count > 0 || s.NestedStructs.Count > 0;

    /// <summary>The names of every STRUCT that travels as its own SOURCE TEXT when a bundle is stripped: one
    /// declaring a member whose body the compiler did not take (see StructDeclStmt.SourceText). A struct descriptor
    /// carries bytecode and a declined body has none, so the descriptor would register a member a loaded caller
    /// cannot run. The set follows the interpreter's case rule.</summary>


    /// <summary>True when a struct declares a member whose BODY the compiler did not take. The two lists are the ones
    /// the strip's own member check refuses a struct for, so this predicate and that refusal cannot disagree.</summary>
    private static bool HasUncompiledStructMemberBody(StructDeclStmt s)
        => s.Methods.Any(m => m.Compiled == null)
        || s.OperatorMethods.Values.Any(m => m.Compiled == null);

    /// <summary>The TEMPLATE name an instantiation's registry name carries (`Box<int>` is `Box`). A text-carried
    /// declaration is registered under the TEMPLATE's name and a later `new Box<int>()` instantiates again at run
    /// time, so a descriptor section must exclude both the template and every instantiation of it, which is what the
    /// callers filter on.</summary>
    private static string TemplateOf(string name)
    {
        var lt = name.IndexOf('<');
        return lt < 0 ? name : name[..lt];
    }

    private static void CollectNestedExtensions(ClassDeclStmt cls, List<VmBytecodeCodec.LibraryExtension> extensions)
    {
        foreach (var x in cls.Extensions)
            extensions.Add(new VmBytecodeCodec.LibraryExtension(x.Target, x.TypeParameters, x.ContainerName,
                x.ReceiverName, x.Members));
        foreach (var nested in cls.NestedClasses) CollectNestedExtensions(nested, extensions);
    }

    /// <summary>A CLASS's own delegate travels by name, like a top-level one. `VisitDelegateDecl` registers it
    /// GLOBALLY (`DelegateInfos[name]` plus a scope binding), so collecting it into the same declarations list a
    /// top-level delegate uses is the whole fix, and it is what makes an EVENT usable from a bundle: measured, a
    /// class declaring `delegate void ValueChanged(int v);` beside `event ValueChanged OnChange;` answered
    /// "Delegate type 'ValueChanged' not defined" from a stripped bundle once the event itself travelled, because
    /// nothing had carried the type the event is declared with. A nested class's delegates are collected too, the
    /// shape `CollectNestedExtensions` uses and for the same reason: the wire list is FLAT.</summary>
    private static void CollectNestedDelegates(ClassDeclStmt cls, List<DelegateInfo> delegates)
    {
        foreach (var d in cls.Delegates)
            delegates.Add(new DelegateInfo(d.Name, d.ReturnType, d.Parameters, d.IsRefParams, d.Access,
                d.Line, d.Column, IsOutParams: d.IsOutParams));
        foreach (var nested in cls.NestedClasses) CollectNestedDelegates(nested, delegates);
    }

    /// <summary>Binds one name a source-less bundle declares, both where an ordinary name read walks the
    /// scopes and in the root global scope the VM's global loads read. The GLOBAL write is unconditional, because
    /// a script's own declaration SHADOWS a builtin in a source run and a bundle has to do the same: that scope
    /// already holds the builtin's adapter under names like `Sum`, `Min` or `Max`, so a first-wins write left the
    /// bundle resolving its OWN function to the builtin. Measured: a file declaring `int Sum(int,int)` answered 3
    /// from source and "Sum expects a collection" from its stripped bundle, and the same function called by name
    /// aborted the body with "loaded from bytecode for a different environment". The ACTIVE scope keeps first-wins,
    /// which is where an existing binding is the caller's own, and a name the bundle repeats still cannot clobber
    /// the one this same load already bound.</summary>
    private void BindLoadedName(string name, object? value, int line = 0, int column = 0)
    {
        if (!CurrentFrame.Scopes.TryGet(name, out _))
            CurrentFrame.Scopes.Define(name, value, line, column);
        SetGlobalValue(name, value, line, column);
    }

    /// <summary>Loads a bytecode-library bundle into a fresh Namespace so `import "lib.slcbc" as Z`
    /// can bind it. Every library function becomes ns.method + ns.property; metadata (if present)
    /// is exposed as ns.__meta. Mirrors the source-import Namespace binding pattern.</summary>
    internal Namespace LoadBundleToNamespace(string path, int line, int column, string alias)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            throw new ScriptException(ErrorCode.Unspecified,
                $"import (bytecode library): cannot read '{path}', {ex.Message}",
                line, column);
        }
        return LoadBundleToNamespaceBytes(data, line, column, alias);
    }

    /// <summary>The bytes form of LoadBundleToNamespace: a bundle already in memory becomes an importable
    /// namespace with no file involved. The file form adds only the read (with its own message naming the path,
    /// which is the one thing a caller can still get wrong).</summary>
    internal Namespace LoadBundleToNamespaceBytes(byte[] data, int line, int column, string alias)
    {
        var funcs = VmBytecodeCodec.DeserializeLibrary(data, out var err, out var meta,
            out var namespaces, out var classes, out var structs, out var vars, out _, out var decls);
        if (funcs == null)
            throw new ScriptException(ErrorCode.Unspecified, "import (bytecode library): " + err, line, column);
        // A library's own declaration entries (v6) register exactly as a program's do, so an imported library
        // that declares an enum, a delegate, an interface or an extension brings those with it.
        RegisterLoadedDeclarations(decls);

        var ns = new Namespace(_comparer);
        // The alias is the LABEL this namespace is reached by: what `import ... alias` binds, and what this
        // member's own comment promises ("the same string `import` passes"). It used to reach only the
        // qualified CLASS keys below, so a library of plain FUNCTIONS ignored it entirely: measured,
        // `LoadNamespace(bytes, "What")` left `print(Z)` answering `Namespace` (the ToString fallback for an
        // unnamed namespace) and `Z.What.A.add(2, 3)` raising `Property 'What' not found in namespace`.
        // The label is reachable THROUGH the value (`Z.What` IS `Z`) rather than as a name invented in scope:
        // an expression does not define a global, and `What.A.add(...)` is the mistake the maintainer's rule
        // names, since the path starts at the value you already hold.
        if (alias.Length > 0)
        {
            ns.Name = alias;
            ns.AddProperty(alias, ns);
        }
        for (var i = 0; i < funcs.Count; i++)
        {
            var d = funcs[i];
            ValidateShellEnvironment(d, "import");
            var fn = new ScriptFunction(d, SEmptySnapshot, d.ReturnType, GetParameterTypes(d.Params),
                d.IsRefParams, Flags: FunctionFlags.Legacy, IsOutParams: d.IsOutParams);
            // Walk the function's namespace path ("A.B" → Z.A.B), creating children as needed.
            var target = ns;
            var nsPath = namespaces != null && i < namespaces.Count ? namespaces[i] : "";
            if (nsPath.Length > 0)
                foreach (var seg in nsPath.Split('.'))
                {
                    if (seg.Length == 0) continue;   // defensive: ignore empty segments
                    target = target.GetChild(seg) ?? CreateNamespaceChild(target, seg);
                }
            fn.NamespaceContext = target;   // F4: bare sibling calls resolve via this leaf
            // A declaration's DEFAULT VALUES are bodies of their own, and a bare sibling call inside one has to
            // resolve the same way the declaration's body does: through the leaf the declaration was bound into.
            // Measured without this, on an imported library: `int libH(int a, int e = D4())` answered "loaded
            // from bytecode for a different environment" while the identical call in a body worked.
            foreach (var value in d.DefaultValues ?? [])
                if (value is CompiledDefaultExpr cd) cd.NamespaceContext = target;
            target.AddMethod(d.Name, args => InvokeScriptFunction(fn, args, line, column));
            target.AddProperty(d.Name, fn);
        }
        // Register library classes/structs under their qualified names (module namespace) and bind
        // them into the namespace. A single-module library puts its types under namespaces[0].
        var nsPathForClasses = namespaces is { Count: > 0 } ? namespaces[0] : "";
        var aliasPrefix = alias.Length == 0 ? "" : alias + ".";
        // Class-only bundles have NO marker function, so namespaces is empty and nsPathForClasses
        // is "". Prepend the alias without a trailing dot in that case, aliasPrefix + "" would
        // otherwise yield "PA..Point" (double dot) and the qualified key / `is` / `typeof`
        // lookups could never resolve it. Using the bare alias gives the clean "PA.Point".
        var classNsPath = nsPathForClasses.Length > 0 ? aliasPrefix + nsPathForClasses : alias;
        if (classes is { Count: > 0 })
        {
            var nsClassRoot = WalkNamespacePath(ns, nsPathForClasses);
            foreach (var ci in classes)
            {
                var qualified = BuildQualifiedName(ci.Name, classNsPath);
                var qualifiedCi = ci with
                {
                    Name = qualified,
                    ParentName = ci.ParentName is { Length: > 0 } ? BuildQualifiedName(ci.ParentName, classNsPath) : null
                };
                if (Classes.TryGetValue(qualifiedCi.Name, out var existingAtKey)
                    && !ReferenceEquals(existingAtKey, qualifiedCi))
                    throw new ScriptException($"import (bytecode library): class '{ci.Name}' collides with an already-imported class of the same name under this alias ('{qualifiedCi.Name}').");
                Classes[qualifiedCi.Name] = qualifiedCi;
                if (!_libraryTypeAliases.ContainsKey(qualifiedCi.Name))
                    _libraryTypeAliases[qualifiedCi.Name] = qualifiedCi;
                if (!_libraryTypeAliases.ContainsKey(ci.Name))
                    _libraryTypeAliases[ci.Name] = qualifiedCi;   // bare name: first-wins
                nsClassRoot.AddProperty(ci.Name, qualifiedCi);
                // Bind the unqualified source class name into the live global scope so compiled
                // static-ctor bodies that reference the class by its SOURCE name (LOAD_GLOBAL_FAST)
                // can resolve it, mirrors source class registration (VisitStatements.cs:381-385).
                // Bare names are FIRST-WINS (F5): only the first bundle that exports a name binds
                // it; a later bundle exporting the same bare class name coexists under its own
                // alias without clobbering the earlier binding. The qualified-key guard above
                // catches genuine same-alias duplicates.
                if (!CurrentFrame.Scopes.TryGet(ci.Name, out _))
                    CurrentFrame.Scopes.Define(ci.Name, qualifiedCi);
                var (_, actualGlobalScope) = _rootFrame.Scopes.GetGlobalScopeReference();
                if (!actualGlobalScope.TryGetValue(ci.Name, out _))
                    actualGlobalScope[ci.Name] = new ScopeStack.VariableInfo(qualifiedCi, 0, 0);
            }
            // Second pass, wire Parent links once every class is registered (needed by
            // ResolveScriptMethod/GetAllFields/RunStaticConstructors/IsClassCompatible). ClassInfo
            // is a record with an init-only Parent, so rebuild each class with the link. The linked
            // record is stored back under the ALIAS-QUALIFIED key (F5): `new Z.My.Include.Dog`
            // resolves Classes["Z.My.Include.Dog"] directly, so that entry must carry the Parent link.
            foreach (var ci in classes)
            {
                if (ci.ParentName is not { Length: > 0 } pn) continue;
                var linkedName = BuildQualifiedName(ci.Name, classNsPath);
                var parentQualified = BuildQualifiedName(pn, classNsPath);
                if (!Classes.TryGetValue(parentQualified, out var parent))
                {
                    // FIX (cross-bundle inheritance): the alias-qualified parent key (e.g.
                    // PB.Geo.Animal) misses when the base class lives in a DIFFERENT bundle
                    // (registered there as PA.Geo.Animal). Fall back to the bare-name first-wins
                    // record in _libraryTypeAliases (the pre-F5 equivalent) and link to that
                    // record's qualified Name. If neither resolves, leave Parent null as before.
                    if (!_libraryTypeAliases.TryGetValue(pn, out var aliasParent) || aliasParent is not ClassInfo aliasParentCi)
                        continue;
                    parent = aliasParentCi;
                    parentQualified = aliasParentCi.Name;
                }
                var linked = ci with { Name = linkedName, Parent = parent, ParentName = parentQualified };
                Classes[linkedName] = linked;
                _libraryTypeAliases[linkedName] = linked;
                WalkNamespacePath(ns, nsPathForClasses).AddProperty(ci.Name, linked);
                // Refresh the BARE-name bindings (frame scope, bare alias, global dict) to the
                // Parent-linked record ONLY when they are this bundle's OWN pass-1 binding, the
                // actualGlobalScope guard is the single decision point, so a later bundle whose
                // class merely shares a bare name never clobbers an earlier bundle's first-wins
                // binding (F5). RunStaticConstructors etc. then see the Parent link through global
                // resolution without overwriting a different bundle's entry.
                var (_, actualGlobalScope) = _rootFrame.Scopes.GetGlobalScopeReference();
                if (actualGlobalScope.TryGetValue(ci.Name, out var currentGlobal)
                    && currentGlobal.Value is ClassInfo currentClass
                    && currentClass.Name == linked.Name)
                {
                    CurrentFrame.Scopes.Define(ci.Name, linked);
                    if (_libraryTypeAliases.TryGetValue(ci.Name, out var currentAlias)
                        && currentAlias is ClassInfo currentAliasCi
                        && currentAliasCi.Name == linked.Name)
                        _libraryTypeAliases[ci.Name] = linked;
                    actualGlobalScope[ci.Name] = new ScopeStack.VariableInfo(linked, 0, 0);
                }
            }
        }
        if (structs is { Count: > 0 })
        {
            var nsStructRoot = WalkNamespacePath(ns, nsPathForClasses);
            foreach (var sd in structs)
            {
                var qualified = BuildQualifiedName(sd.Name, classNsPath);
                // StructDeclStmt.Name has a private setter, rebuild with the qualified name.
                var qualifiedSd = StructDeclStmt.Create(qualified, sd.Fields, sd.Access, sd.Line, sd.Column);
                qualifiedSd.Constructors = sd.Constructors;
                qualifiedSd.Methods = sd.Methods;
                qualifiedSd.OperatorMethods = sd.OperatorMethods;
                qualifiedSd.Properties = sd.Properties;
                qualifiedSd.NestedClasses = sd.NestedClasses;
                qualifiedSd.NestedStructs = sd.NestedStructs;
                qualifiedSd.IsReadOnly = sd.IsReadOnly;
                Structs[qualified] = qualifiedSd;
                if (!_libraryTypeAliases.ContainsKey(qualified))
                    _libraryTypeAliases[qualified] = qualifiedSd;
                if (!_libraryTypeAliases.ContainsKey(sd.Name))
                    _libraryTypeAliases[sd.Name] = qualifiedSd;   // bare name: first-wins
                nsStructRoot.AddProperty(sd.Name, qualifiedSd);
            }
        }
        // Top-level variable snapshots bind at the import alias root (Z.APP_NAME, Z.CONFIG).
        if (vars is { Count: > 0 })
        {
            var nsVarRoot = WalkNamespacePath(ns, "");
            foreach (var (varName, varValue) in vars)
                nsVarRoot.AddProperty(varName, varValue);
        }
        if (meta != null) ns.AddProperty("__meta", meta);
        return ns;
    }

    /// <summary>Loads a .slcbc file into the REPL session, detecting the bundle type: library
    /// bundles become a Namespace under the given alias (default = file stem); single-script
    /// bundles load flat into the global scope as before. Used by the .loadslcbc dotcommand.</summary>
    internal string LoadSlcbcFileToRepl(string path, string? alias)
    {
        var data = File.ReadAllBytes(path);
        var isLibrary = data.Length >= 4 && BitConverter.ToInt32(data, 0) == VmBytecodeCodec.LibraryMagic;
        if (isLibrary)
        {
            var name = alias ?? Path.GetFileNameWithoutExtension(path);
            var ns = LoadBundleToNamespace(path, 0, 0, name);
            CurrentFrame.Scopes.Define(name, ns);
            return $"Imported library as namespace '{name}'.";
        }
        var (entry, count) = LoadBundleFromFile(path);
        RegisterCarriedDeclarations();
        var entryName = entry?.Declaration.Name;
        return $"Loaded {count} function(s)" + (entryName != null ? $", entry '{entryName}'" : "");
    }

    /// <summary>Registers what the source a just-loaded bundle carries DECLARES (classes, structs,
    /// interfaces, extensions, enums, delegates, functions), WITHOUT running its top level. A class a
    /// loaded function builds has to exist, but the program's own statements are the caller's business:
    /// the CLI runs them, `.loadslcbc` does not. Measured before this existed: `.loadslcbc` on a bundle
    /// whose only function did `var b = new Box(); return b.Twice();` answered "Function 'MakeBox' was
    /// loaded from bytecode for a different environment (types/globals unavailable)".</summary>
    private void RegisterCarriedDeclarations()
    {
        var statements = ParseCarriedSourceStatements();
        if (statements == null) return;
        RegisterDeclarationStatements(statements);
    }

    /// <summary>Parses the source a bundle CARRIES, for the two things that need it before the text runs: the
    /// declaration registration (.loadslcbc) and the type names the load-time shell check must accept. Null when
    /// there is no carried source or it does not parse, which surfaces when it RUNS rather than at load, so both
    /// callers share ONE parse.</summary>
    private List<Stmt>? ParseCarriedSourceStatements()
    {
        if (string.IsNullOrEmpty(LoadedBundleSource)) return null;
        try
        {
            var parser = new Parser(new Lexer(LoadedBundleSource.AsMemory()).GetTokens(), Delegates, source: LoadedBundleSource);
            return parser.GetStatements().ToList();
        }
        catch { return null; }
    }

    /// <summary>Registers what a list of statements DECLARES (classes, structs, interfaces, extensions,
    /// enums, delegates, functions) WITHOUT running the program's own top level. Two callers need exactly
    /// that: a load of a bundle whose source was stripped, and a --compile that has to write the classes
    /// and structs into the bundle as descriptors. Shared so the two cannot drift.</summary>
    private void RegisterDeclarationStatements(IEnumerable<Stmt> statements)
    {
        var declarations = FlattenNamespaceContainers(statements)
            .Where(IsDeclarationStatement).ToList();
        if (declarations.Count == 0) return;
        RunPreloadStatements(declarations);
    }

    /// <summary>Runs statements for their PRELOAD side effects (registering declarations, replaying imports)
    /// without letting them become the program body.</summary>
    /// <remarks>The call is an Interpret, and Interpret RECORDS ITS OWN list as the program body (`_programStatements =
    /// stmts`), so registering a SUBSET must put the file's top level back afterwards. Measured without this:
    /// `--compile --no-source` on a program with classes compiled the program body TWICE, the second time from
    /// the six DECLARATIONS alone (its var decls and its `if` gone), and the strip exported that second body as
    /// the bundle's entry. Restore all three together, and only when there was a record to preserve, so the
    /// carried-source load (which has none) keeps recording what it registers.</remarks>
    private void RunPreloadStatements(List<Stmt> statements)
    {
        var savedStatements = _programStatements;
        if (savedStatements != null)
        {
            var savedBytecode = _programBytecode;
            var savedAttempted = _programBodyAttempted;
            try { Interpret(statements); }
            catch { }
            finally
            {
                _programStatements = savedStatements;
                _programBytecode = savedBytecode;
                _programBodyAttempted = savedAttempted;
            }
            return;
        }
        try { Interpret(statements); } catch { }
    }

    /// <summary>Creates and registers a child namespace, used when walking a library function's
    /// dotted namespace path (e.g. "A.B" → Z.A.B).</summary>
    private Namespace CreateNamespaceChild(Namespace parent, string name)
    {
        var child = new Namespace(_comparer, name);
        parent.AddChild(name, child);
        return child;
    }

    /// <summary>Walks (or creates) the child-namespace chain for a dotted path rooted at
    /// <paramref name="root"/> ("A.B" → root.A.B). An empty path returns the root itself.</summary>
    private Namespace WalkNamespacePath(Namespace root, string nsPath)
    {
        var target = root;
        if (nsPath.Length > 0)
            foreach (var seg in nsPath.Split('.'))
            {
                if (seg.Length == 0) continue;
                target = target.GetChild(seg) ?? CreateNamespaceChild(target, seg);
            }
        return target;
    }

    private static string BuildQualifiedName(string className, string nsPath)
        => nsPath.Length == 0 ? className : $"{nsPath}.{className}";
}
