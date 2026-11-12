namespace ScriptLangCore;

internal static partial class Program
{
    /// <summary>The SOURCE MODE of a <c>--compile</c> invocation, from its flags. All three modes the engine has are
    /// spellable, which is what the page documents and what the middle one was missing:
    ///   no flag                  carry  : the bundle carries the program text (the default, and unchanged);
    ///   <c>--source=strip</c>    strip  : strip it when the strip is honest, carry it (the report saying so) when not;
    ///   <c>--source=strict</c>   strict : strip it or REFUSE, naming what could not be carried, which is also what
    ///                                     <c>--no-source</c> means (kept as the documented shorthand).
    /// A conflicting pair, a bare <c>--source</c> and an unknown spelling are REFUSED by name rather than resolved
    /// to something plausible: which of two modes a caller meant is exactly what this exists to stop being a
    /// guess, and guessing wrong means either a bigger bundle than asked for or a silently carried source.
    /// `args[0]` is `--compile` and `args[1]` is the source; every remaining argument that starts with `--` is a
    /// flag, and the output path is the first one that does not (see <see cref="OutPathFromArgs"/>).</summary>
    internal static bool SourceModeFromArgs(string[] args, out bool stripSource, out bool requireStrip, out string? error)
    {
        stripSource = false;
        requireStrip = false;
        error = null;
        var noSource = false;
        string? mode = null;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--no-source") { noSource = true; continue; }
            if (a == "--source")
            {
                error = "--source takes its value in the same argument: --source=carry|strip|strict";
                return false;
            }
            if (a.StartsWith("--source=", StringComparison.Ordinal))
                mode = a["--source=".Length..];
        }
        if (noSource && mode != null && !StringComparer.OrdinalIgnoreCase.Equals(mode, "strict"))
        {
            error = $"--no-source and --source={mode} disagree: --no-source IS --source=strict, so name one of them";
            return false;
        }
        switch ((mode ?? "").ToLowerInvariant())
        {
            case "": stripSource = noSource; requireStrip = noSource; return true;
            case "carry": stripSource = false; requireStrip = false; return true;
            case "strip": stripSource = true; requireStrip = false; return true;
            case "strict": stripSource = true; requireStrip = true; return true;
            default:
                error = $"--source={mode} is not one of carry, strip or strict";
                return false;
        }
    }

    /// <summary>The bundle path a <c>--compile</c> invocation names, or null for the default (beside the source).
    /// It is the FIRST argument after the source that is not a flag, so <c>--compile a.slc --source=strip out.slcbc</c>
    /// writes to <c>out.slcbc</c>: measured before this, the path was read from <c>args[2]</c> ALONE, so a flag in
    /// that slot made the read give null and the compile wrote a file beside the source that the caller had not
    /// named (silently, exit 0). Anything starting with <c>--</c> is a flag, which is also why an unknown one is
    /// ignored rather than taken for a path, and a second non-flag argument is not a second output: the first one
    /// wins, so `--compile a.slc one.slcbc two.slcbc` writes `one.slcbc`.</summary>
    internal static string? OutPathFromArgs(string[] args)
    {
        for (var i = 2; i < args.Length; i++)
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                return args[i];
        return null;
    }

    /// <summary>Writes a bytecode bundle for a script WITHOUT running it (the --compile flag). Exit code: 0 the
    /// bundle was written, 2 the file, the output path or a flag could not be used, 3 the file did not parse or the
    /// export refused it (a strict strip that is not honest).</summary>
    internal static int CompileToBundle(string scriptPath, string? outPath, bool stripSource = false, bool requireStrip = false)
    {
        var fullPath = Path.GetFullPath(scriptPath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"Error: file not found: {scriptPath}");
            return ExitCode.Usage;
        }

        var (sourceMem, _) = FileReader.ReadFile(fullPath);
        sourceMem = FileReader.StripDotCommands(sourceMem, ref _timingMode, ref _echoMode);
        var outFull = Path.GetFullPath(outPath ?? Path.ChangeExtension(fullPath, ".slcbc"));

        using var handle = InterpreterManager.Create(caseSensitive: true, context: "CompileBundle");
        var itp = handle.Instance;
        List<Stmt> statements;
        try
        {
            var lexer = new Lexer(sourceMem);
            var parser = new Parser(lexer.GetTokens(), itp.Delegates, source: sourceMem.ToString());
            statements = parser.GetStatements().ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {Path.GetFileName(fullPath)} did not parse: {ex.Message}");
            return ExitCode.Refused;
        }

        try
        {
            PrecompileFunctions(statements, itp);
            itp.PrepareForExport(statements, fullPath);
            itp.SourceText = sourceMem.ToString();   // the entry the bundle carries as text
            var report = itp.ExportTopLevelToBundle(outFull, null, stripSource, requireStrip);
            // NO SIDECAR ANY MORE: the bundle carries its own digest in its header, stamped by the serializer, so an edit
            // is caught with ONE file. `WriteBundleManifest` stays for a caller that wants the stronger two-file check
            // (a digest it can keep somewhere else), and the loader still honours a `<name>.slcbc.sha256` when one exists.
            Console.WriteLine($"[bc] {Path.GetFileName(fullPath)} -> {Path.GetFileName(outFull)}: {report}");
            // A SIDECAR DESCRIBING THE PREVIOUS BUILD IS STALE BY CONSTRUCTION, and this door is what writes that path, so it
            // clears it rather than leaving a manifest that can only disagree with the bundle it sits beside (measured: 73
            // checks in vm_nosource_test refused over leftover manifests the digest moved out of).
            var staleSidecar = outFull + Interpreter.BundleManifestSuffix;
            if (File.Exists(staleSidecar)) {
                try {
                    File.Delete(staleSidecar);
                    Console.WriteLine($"[bc] removed the stale manifest {Path.GetFileName(staleSidecar)} (the digest travels in the bundle)");
                } catch (IOException) { }
            }
            return ExitCode.Ok;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: could not write {Path.GetFileName(outFull)}: {ex.Message}");
            return ExitCode.Refused;
        }
    }

    /// <summary>Pre-compiles all FuncDecl statements to bytecode (if VM-safe) after parsing.</summary>
    internal static void PrecompileFunctions(IEnumerable<Stmt> statements, Interpreter interp,
        IReadOnlyDictionary<string, FuncDecl[]>? outerCalleeDecls = null)
    {
        var builtins = interp.GetGlobalScope().Keys
            .Where(k => k != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Class name → event names (own + inherited, shadowed by own). The VM compiler's
        // HasEventCompoundWrite must tree-fall methods that subscribe an event, including events
        // inherited from a base class (e.g. Derived.SubscribeProtected does `this.OnProtected += h`
        // where OnProtected is declared on AccessTest). Resolved in two passes so a derived class
        // declared before its base still gets the merged set.
        // The DICTIONARIES are indexes (a class is found by its declared spelling), so they stay insensitive.
        // The SETS are compared against names as the SCRIPT wrote them, that is the name rule, so they follow
        // the interpreter's case flag. Hardcoded insensitive here made the compiler treat a wrong-case bare name
        // as a class field in strict mode, and the two engines then answered differently: measured, `return a;`
        // for a field `A` reported "Field or event 'a' not found in class 'K'" on the VM (which is where the
        // compiler had routed it) while the tree - comparing with `Comparison` - said "Undefined variable 'a'".
        var nameComparer = interp.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var classEventNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var classFieldNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var classStaticFieldNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var classStaticMethodNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        // The INSTANCE twin of the line above: a bare-name call to a sibling method resolves through the
        // class (the tree via FindMethodInHierarchy, the VM as `this.Name(...)`), and a class with no
        // fields used to pass no context at all, so those bodies were emitted as plain CALLs.
        var classInstanceMethodNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var classDecls = new List<ClassDeclStmt>();
        foreach (var stmt in Interpreter.FlattenNamespaceContainers(statements))
        {
            if (stmt is ClassDeclStmt c) classDecls.Add(c);
        }
        // Pass 1: own events + own fields (instance + static) + static methods.
        foreach (var cls in classDecls)
        {
            classEventNames[cls.Name] = new HashSet<string>(
                cls.Events.Select(e => e.Name), nameComparer);
            // A PROPERTY is a bare member name too: `X` inside the class's own method must compile as `this.X`, and
            // with only the FIELDS in this set it compiled as a global load, so the VM reported "Undefined variable
            // 'X'" for an auto-property while a field worked (measured, both directions).
            classFieldNames[cls.Name] = new HashSet<string>(
                cls.Fields.Where(f => !f.IsStatic).Select(f => f.Name)
                    .Concat(cls.Properties.Where(p => !p.IsStatic).Select(p => p.Name)), nameComparer);
            classStaticFieldNames[cls.Name] = new HashSet<string>(
                cls.Fields.Where(f => f.IsStatic).Select(f => f.Name), nameComparer);
            classStaticMethodNames[cls.Name] = new HashSet<string>(
                cls.Methods.Where(m => m.IsStatic).Select(m => m.Name), nameComparer);
            classInstanceMethodNames[cls.Name] = new HashSet<string>(
                cls.Methods.Where(m => !m.IsStatic).Select(m => m.Name), nameComparer);
        }
        // Pass 2: merge inherited events + instance/static fields + static methods (parent's merged
        // set, minus names the class shadows, a derived member of the same name hides the base's).
        // Fixpoint loop: a derived class declared before its base needs a second pass, and deep
        // chains (D→C→B→A) converge only after the parents' sets are complete, iterate until no
        // set grows.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var cls in classDecls)
            {
                if (cls.ParentName == null) continue;
                if (classEventNames.TryGetValue(cls.ParentName, out var parentEvents))
                    foreach (var ev in parentEvents)
                        if (classEventNames[cls.Name].Add(ev)) changed = true;
                if (classFieldNames.TryGetValue(cls.ParentName, out var parentFields))
                    foreach (var f in parentFields)
                        if (classFieldNames[cls.Name].Add(f)) changed = true;
                if (classStaticFieldNames.TryGetValue(cls.ParentName, out var parentStatics))
                    foreach (var f in parentStatics)
                        if (classStaticFieldNames[cls.Name].Add(f)) changed = true;
                if (classStaticMethodNames.TryGetValue(cls.ParentName, out var parentStatMethods))
                    foreach (var m in parentStatMethods)
                        if (classStaticMethodNames[cls.Name].Add(m)) changed = true;
                if (classInstanceMethodNames.TryGetValue(cls.ParentName, out var parentInstMethods))
                    foreach (var m in parentInstMethods)
                        if (classInstanceMethodNames[cls.Name].Add(m)) changed = true;
            }
        }

        // The compiler's ref/out gate needs EVERY declaration of the callee's NAME to tell a fixed parameter from a
        // tail one AND to know whether any member of an overload set can honour the call as written (see
        // Compiler.RefOutArgUnhonourable), and only this layer knows the top-level declarations. A nested class's
        // members are compiled by the recursive call below, which passes this same map down.
        IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls = outerCalleeDecls ?? Compiler.BuildCalleeDecls(
            Interpreter.FlattenNamespaceContainers(statements).OfType<FuncDecl>(), interp.CaseSensitive);

        // A NESTED function declaration (one written inside a block, an if, a loop) needs the same compile the loop
        // below gives a top-level one, and that loop reaches only the top level and class members. Measured
        // without it: `if (true) { void Inner() {...} var cb = fun() { Inner(); }; cb(); }` leaves Inner.Compiled
        // null, so a stripped bundle cannot carry it and the strip refuses by name; the sample's own `Line` is in
        // exactly that state, which is the one thing between a stripped bundle and the program running.
        void CompileNested(IEnumerable<Stmt> list)
        {
            foreach (var nestedStmt in list)
            {
                if (nestedStmt is FuncDecl nested)
                {
                    // The registry lets a compiled body turn this declaration into a value (`__nested_fn__`). The BODY
                    // is compiled at its DECLARATION SITE, where the compiling context knows which names are enclosing
                    // LOCALS; this pass stays as the fallback for a declaration whose block is tree-walked.
                    nested.CapturedNames ??= FreeVariableAnalyzer.Analyze(nested);
                    if (nested.Name is { Length: > 0 }) interp.NestedFunctionDecls.TryAdd(nested.Name, nested);
                    nested.Compiled ??= Compiler.Compile(nested, builtins,
                        caseSensitive: interp.CaseSensitive, calleeDecls: calleeDecls);
                    CompileNested(nested.Body);
                    continue;
                }
                switch (nestedStmt)
                {
                    case BlockStmt b: CompileNested(b.Statements); break;
                    case IfStmt i: CompileNested(i.ThenBranch); CompileNested(i.ElseBranch); break;
                    case WhileStmt w: CompileNested(w.Body); break;
                    case ForeachStmt fe: CompileNested(fe.Body); break;
                    case ForStmt fr: CompileNested(fr.Body); break;
                    case TryStmt t:
                        CompileNested([t.Body]);
                        foreach (var cc in t.Catches) CompileNested([cc.Body]);
                        if (t.Finally != null) CompileNested([t.Finally]);
                        break;
                    case UsingStmt u: CompileNested([u.Body]); break;
                    case SwitchStmt sw: foreach (var cc in sw.Cases) CompileNested(cc.Body); break;
                }
            }
        }
        CompileNested(statements);

        foreach (var stmt in Interpreter.FlattenNamespaceContainers(statements))
        {
            if (stmt is FuncDecl fd)
                fd.Compiled = Compiler.Compile(fd, builtins, caseSensitive: interp.CaseSensitive, calleeDecls: calleeDecls);
            else if (stmt is ClassDeclStmt cls)
            {
                var eventNames = classEventNames.TryGetValue(cls.Name, out var evs) && evs.Count > 0 ? evs : null;
                var fieldNames = classFieldNames.TryGetValue(cls.Name, out var flds) && flds.Count > 0 ? flds : null;
                var staticFieldNames = classStaticFieldNames.TryGetValue(cls.Name, out var sfs) && sfs.Count > 0 ? sfs : null;
                var staticMethodNames = classStaticMethodNames.TryGetValue(cls.Name, out var sms) && sms.Count > 0 ? sms : null;
                var instanceMethodNames = classInstanceMethodNames.TryGetValue(cls.Name, out var ims) && ims.Count > 0 ? ims : null;
                foreach (var m in cls.Methods)
                    m.Compiled = Compiler.Compile(m, builtins, isMethod: true, caseSensitive: interp.CaseSensitive, eventNames, fieldNames, staticFieldNames, cls.Name, staticMethodNames, calleeDecls: calleeDecls, instanceMethodNames: instanceMethodNames);
                foreach (var om in cls.OperatorMethods.Values)
                    om.Compiled = Compiler.Compile(om, builtins, isMethod: true, caseSensitive: interp.CaseSensitive, eventNames, fieldNames, staticFieldNames, cls.Name, staticMethodNames, calleeDecls: calleeDecls, instanceMethodNames: instanceMethodNames);
                if (cls.Destructor != null)
                    cls.Destructor.Compiled = Compiler.Compile(cls.Destructor, builtins, isMethod: true, caseSensitive: interp.CaseSensitive, eventNames, fieldNames, staticFieldNames, calleeDecls: calleeDecls, instanceMethodNames: instanceMethodNames);
                foreach (var c in cls.Constructors)
                    c.Compiled = Compiler.Compile(c, builtins, caseSensitive: interp.CaseSensitive, eventNames, fieldNames, staticFieldNames, calleeDecls: calleeDecls, instanceMethodNames: instanceMethodNames, className: cls.Name, staticMethodNames: staticMethodNames);
                // An INSTANCE field's initializer travels as a compiled THUNK, the shape a parameter default
                // already uses, so a stripped bundle builds the same instance the file does. A thunk is an ORDINARY
                // body with no `this`, so an initializer that reads the INSTANCE compiles to nothing and the strip
                // refuses that one by name (measured: `public int W = this.V + 1;` runs from source). A STATIC
                // field needs nothing here: its value is registered, and the descriptor carries the value itself.
                foreach (var fld in cls.Fields)
                    if (!fld.IsStatic && fld.Initializer != null)
                        fld.InitializerThunk = Compiler.CompileDefaultValue(fld.Initializer, builtins,
                            interp.CaseSensitive, calleeDecls, instanceMethodNames);
                foreach (var nc in cls.NestedClasses) PrecompileFunctions([nc], interp, calleeDecls);
            }
            else if (stmt is InterfaceDeclStmt itf)
            {
                // A DEFAULT BODY and a PROPERTY ACCESSOR are ordinary bodies: a class that does not implement the
                // member RUNS them, and a bundle carries them as shells, so they compile here like any other member.
                // Measured before this arm existed: `--no-source` refused such an interface by name ("the default
                // body an interface 'X' declares"), and the file carried its whole source for it.
                foreach (var (_, decl) in itf.Defaults)
                    decl.Compiled = Compiler.Compile(decl, builtins, caseSensitive: interp.CaseSensitive,
                        calleeDecls: calleeDecls);
                foreach (var prop in itf.Properties)
                {
                    if (prop.Getter != null)
                        prop.Getter.Compiled = Compiler.Compile(prop.Getter, builtins,
                            caseSensitive: interp.CaseSensitive, calleeDecls: calleeDecls);
                    if (prop.Setter != null)
                        prop.Setter.Compiled = Compiler.Compile(prop.Setter, builtins,
                            caseSensitive: interp.CaseSensitive, calleeDecls: calleeDecls);
                }
            }
            else if (stmt is StructDeclStmt str)
            {
                // A struct's OWN members resolve its fields by bare name, exactly as a class's do, so the same
                // field sets are passed. Without them a bare `A` in a struct method compiled as a global load and
                // the VM reported "Undefined variable 'A'", while the tree did the same thing (VisitIdentifierExpr
                // now resolves it). The comparer is the INTERPRETER's case flag, like every other name lookup.
                var strComparer = interp.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                var strFieldNames = str.Fields.Where(f => !f.IsStatic).Select(f => f.Name)
                    .Concat(str.Properties.Where(p => !p.IsStatic).Select(p => p.Name)).ToHashSet(strComparer);
                var strStaticFields = str.Fields.Where(f => f.IsStatic).Select(f => f.Name).ToHashSet(strComparer);
                // A STRUCT's bare sibling CALL is an implicit `this.Method(...)` exactly as a class's is, and the
                // compiler needs the instance-method NAMES to emit CALL_METHOD: without them it emitted a plain CALL,
                // which the VM cannot resolve (it has no `_currentClass`), so the body bailed and a source-less bundle
                // refused it ("Function '__program__' stopped part-way", generic_struct_test). The class arm has passed
                // these names since the implicit-this work; the struct arm passed only the field sets.
                var strInstanceMethods = str.Methods.Where(m => !m.IsStatic).Select(m => m.Name).ToHashSet(strComparer);
                var strInstMethodArg = strInstanceMethods.Count > 0 ? strInstanceMethods : null;
                foreach (var m in str.Methods)
                    m.Compiled = Compiler.Compile(m, builtins, isMethod: true, caseSensitive: interp.CaseSensitive,
                        eventNames: null, fieldNames: strFieldNames,
                        staticFieldNames: strStaticFields.Count > 0 ? strStaticFields : null, className: str.Name,
                        calleeDecls: calleeDecls, instanceMethodNames: strInstMethodArg);
                foreach (var om in str.OperatorMethods.Values)
                    om.Compiled = Compiler.Compile(om, builtins, isMethod: true, caseSensitive: interp.CaseSensitive,
                        eventNames: null, fieldNames: strFieldNames,
                        staticFieldNames: strStaticFields.Count > 0 ? strStaticFields : null, className: str.Name, calleeDecls: calleeDecls,
                        instanceMethodNames: strInstMethodArg);
                foreach (var c in str.Constructors)
                    // The ConstructorDecl overload: a constructor is already a method (no isMethod), and it takes
                    // neither a class name nor static method names, the class path passes exactly these sets.
                    c.Compiled = Compiler.Compile(c, builtins, caseSensitive: interp.CaseSensitive,
                        eventNames: null, fieldNames: strFieldNames,
                        staticFieldNames: strStaticFields.Count > 0 ? strStaticFields : null, calleeDecls: calleeDecls,
                        instanceMethodNames: strInstMethodArg);
            }
        }

        // ── Task 4 pass 2 (FOREACH_CALL): a bare-name foreach over a DIRECT coroutine call can
        // drive the callee's frame in-place (no Coroutine/Generator wrappers), but the compiler can
        // only know a callee is VM-driveable once EVERY function's Compiled status is final, pass 1
        // just produced it. Compute the safe-coroutine set and recompile the bodies that foreach over
        // such a call. (Set empty → no function can use FOREACH_CALL → skip the recompile pass.)
        // The user functions are NOT yet registered in the global scope, so IsYieldRequestBody's
        // bare-name scan resolves against the parsed FuncDecls via a temporary resolver.
        var declMap = new Dictionary<string, FuncDecl>(StringComparer.OrdinalIgnoreCase);
        foreach (var stmt in Interpreter.FlattenNamespaceContainers(statements))
            if (stmt is FuncDecl fd)
                declMap[fd.Name] = fd;
        interp.SetScanBareNameResolver(name => declMap.TryGetValue(name, out var d) ? d : null);
        try
        {
            var coroutineDecls = new Dictionary<string, FuncDecl>(StringComparer.OrdinalIgnoreCase);
            foreach (var stmt in Interpreter.FlattenNamespaceContainers(statements))
                if (stmt is FuncDecl fd && interp.IsVmBackedCoroutine(fd))
                    coroutineDecls[fd.Name] = fd;
            if (coroutineDecls.Count > 0)
                foreach (var stmt in Interpreter.FlattenNamespaceContainers(statements))
                {
                    if (stmt is FuncDecl fd2)
                        fd2.Compiled = Compiler.Compile(fd2, builtins, caseSensitive: interp.CaseSensitive, coroutineDecls: coroutineDecls, calleeDecls: calleeDecls);
                    else if (stmt is ClassDeclStmt cls2)
                    {
                        var eventNames2 = classEventNames.TryGetValue(cls2.Name, out var evs2) && evs2.Count > 0 ? evs2 : null;
                        var fieldNames2 = classFieldNames.TryGetValue(cls2.Name, out var flds2) && flds2.Count > 0 ? flds2 : null;
                        var staticFieldNames2 = classStaticFieldNames.TryGetValue(cls2.Name, out var sfs2) && sfs2.Count > 0 ? sfs2 : null;
                        var staticMethodNames2 = classStaticMethodNames.TryGetValue(cls2.Name, out var sms2) && sms2.Count > 0 ? sms2 : null;
                        var instanceMethodNames2 = classInstanceMethodNames.TryGetValue(cls2.Name, out var ims2) && ims2.Count > 0 ? ims2 : null;
                        foreach (var m in cls2.Methods)
                            m.Compiled = Compiler.Compile(m, builtins, isMethod: true, caseSensitive: interp.CaseSensitive, eventNames2, fieldNames2, staticFieldNames2, cls2.Name, staticMethodNames2, coroutineDecls: coroutineDecls, calleeDecls: calleeDecls);
                        foreach (var om in cls2.OperatorMethods.Values)
                            om.Compiled = Compiler.Compile(om, builtins, isMethod: true, caseSensitive: interp.CaseSensitive, eventNames2, fieldNames2, staticFieldNames2, cls2.Name, staticMethodNames2, coroutineDecls: coroutineDecls, calleeDecls: calleeDecls);
                        if (cls2.Destructor != null)
                            cls2.Destructor.Compiled = Compiler.Compile(cls2.Destructor, builtins, isMethod: true, caseSensitive: interp.CaseSensitive, eventNames2, fieldNames2, staticFieldNames2, calleeDecls: calleeDecls, instanceMethodNames: instanceMethodNames2);
                        foreach (var c in cls2.Constructors)
                            c.Compiled = Compiler.Compile(c, builtins, caseSensitive: interp.CaseSensitive, eventNames2, fieldNames2, staticFieldNames2, coroutineDecls, calleeDecls: calleeDecls, instanceMethodNames: instanceMethodNames2, className: cls2.Name, staticMethodNames: staticMethodNames2);
                        foreach (var nc in cls2.NestedClasses) PrecompileFunctions([nc], interp, calleeDecls);
                    }
                    else if (stmt is StructDeclStmt str2)
                    {
                        // The SAME field sets the first pass built for this struct. Dropping them here made
                        // every struct's bare member name compile as a GLOBAL load in a program that declares a
                        // coroutine ANYWHERE: measured, a struct operator body's bare `X` answered "Undefined
                        // variable 'X'" as soon as a coroutine function preceded it, while the same struct
                        // compiled correctly without one (the class branch above always passed its own sets).
                        var strComparer2 = interp.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
                        var strFieldNames2 = str2.Fields.Where(f => !f.IsStatic).Select(f => f.Name)
                            .Concat(str2.Properties.Where(p => !p.IsStatic).Select(p => p.Name)).ToHashSet(strComparer2);
                        var strStaticFields2 = str2.Fields.Where(f => f.IsStatic).Select(f => f.Name).ToHashSet(strComparer2);
                        var strStaticFieldNames2 = strStaticFields2.Count > 0 ? strStaticFields2 : null;
                        foreach (var m in str2.Methods)
                            m.Compiled = Compiler.Compile(m, builtins, isMethod: true, caseSensitive: interp.CaseSensitive,
                                eventNames: null, fieldNames: strFieldNames2, staticFieldNames: strStaticFieldNames2,
                                className: str2.Name, coroutineDecls: coroutineDecls, calleeDecls: calleeDecls);
                        foreach (var om in str2.OperatorMethods.Values)
                            om.Compiled = Compiler.Compile(om, builtins, isMethod: true, caseSensitive: interp.CaseSensitive,
                                eventNames: null, fieldNames: strFieldNames2, staticFieldNames: strStaticFieldNames2,
                                className: str2.Name, coroutineDecls: coroutineDecls, calleeDecls: calleeDecls);
                        foreach (var c in str2.Constructors)
                            c.Compiled = Compiler.Compile(c, builtins, caseSensitive: interp.CaseSensitive,
                                eventNames: null, fieldNames: strFieldNames2, staticFieldNames: strStaticFieldNames2,
                                coroutineDecls: coroutineDecls, calleeDecls: calleeDecls);
                    }
                }
        }
        finally
        {
            interp.SetScanBareNameResolver(null);
        }
    }
}
