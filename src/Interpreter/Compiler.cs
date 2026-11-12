namespace ScriptLangCore;
/// <summary>Single-pass top-down AST → bytecode compiler.</summary>
internal static class Compiler
{
    public static Bytecode? Compile(FuncDecl decl, HashSet<string> builtins, bool isMethod = false, bool caseSensitive = true,
        IReadOnlySet<string>? eventNames = null, IReadOnlySet<string>? fieldNames = null,
        IReadOnlySet<string>? staticFieldNames = null, string? className = null,
        IReadOnlySet<string>? staticMethodNames = null, IReadOnlyList<string>? capturedLocals = null,
        IReadOnlyDictionary<string, FuncDecl>? coroutineDecls = null, string? receiverAlias = null,
        IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls = null, IReadOnlySet<string>? instanceMethodNames = null,
        bool allowNestedDecls = false)
    {
        var code = new List<byte>();
        var consts = new List<object?>();
        var locals = new List<string>();
        var constMap = new Dictionary<object, int>();
        var localMap = new Dictionary<string, int>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        // An `async` body COMPILES now, and the three things that made its call contract unsafe are handled: an
        // `await` emits AWAIT (the resume continues at the next instruction instead of re-entering the statement),
        // the VM's CALL arms refuse to flat-call or tail-call an async callee (its caller must receive the Task, not
        // the value, which is the regression this line existed to prevent), and IsVmBackedCoroutine accepts an async
        // body so the coroutine drive runs it on the VM.

        // An `async` body holding a `try` COMPILES now: the decline that stood here is GONE, and its premise is
        // refuted by measurement rather than by argument. A fault raised by an awaited task inside a compiled body's
        // try region used to escape that body's own `catch` ("InvalidOperation: inner boom"), and the cause was one
        // line in the VM, not a shape the compiler cannot model: `pendingAwaitFault` is parked by the resume block and
        // thrown at `restart:`, which is ALSO where a `catch` arm jumps back to, so the field was still set and the
        // identical exception was raised a second time inside the catch, trampling it. `Vm.ExecuteCoreInner` now
        // clears the field as it throws it, so the handler walk owns the exception exactly once; `async_try_catch_test`
        // (79 checks) is green with these bodies compiled, and the shapes it covers are the ones this decline was
        // hiding: an await in a try, in a catch, in a finally, and a fault caught by the body that awaited it.

        // Register parameters as locals
        foreach (var p in decl.Params)
            if (!localMap.ContainsKey(p.Name)) { localMap[p.Name] = locals.Count; locals.Add(p.Name); }

        // Pre-register "this" right after the params, method dispatch appends the instance at
        // vmArgs[Params.Count]; on-demand registration would place it wherever ThisExpr first appears.
        localMap["this"] = locals.Count; locals.Add("this");

        // An extension block may NAME its receiver (`extension(string s)`): the name is a second spelling of
        // `this`, so it maps to the SAME slot as `this` rather than becoming a local of its own, the receiver
        // already arrives there (the method layout appends it after the params). A member's own parameter or
        // local of that name is registered above, so it still shadows the alias, as it does on the tree.
        if (receiverAlias != null && !localMap.ContainsKey(receiverAlias))
            localMap[receiverAlias] = localMap["this"];

        // LOCAL functions (a FuncDecl nested inside another function): the interpreter materializes
        // the enclosing captured locals (incl. the function's own name for recursion) as VM locals
        // AFTER params + "this", the same layout MAKE_LAMBDA uses for lambda captures. Body
        // references to them compile as LOAD_VAR/STORE_VAR instead of LOAD_GLOBAL_FAST, and the
        // VM-direct path binds them as extra args (VmCaptures) exactly like lambda captures.
        // Top-level functions pass null, their free names stay global references (unchanged).
        if (capturedLocals != null)
            foreach (var cn in capturedLocals)
                if (!localMap.ContainsKey(cn)) { localMap[cn] = locals.Count; locals.Add(cn); }

        // A class/struct method writing an instance field with a BARE name (e.g. `X = v` for this.X)
        // used to be miscompiled as STORE_GLOBAL_FAST, but with the enclosing class's field-name
        // set, bare writes to known fields compile as `this.X = v` (LOAD_VAR this + SET_PROP), and
        // bare READS as `this.X` (GET_PROP). Only a bare write whose name is NOT a known field is
        // ambiguous (global vs instance), the tree resolves it via the instance, so fall back.
        // Top-level functions (isMethod=false) legitimately write globals by bare name, untouched.
        // Params and body-declared locals are matched with the interpreter's comparer (localMap
        // follows caseSensitive), so a bare write to field `X` is flagged in case-sensitive mode but
        // treated as param `x` in case-insensitive mode.
        if (isMethod && HasBareNameFieldWrite(decl.Body, CollectParamNames(decl.Params, caseSensitive), CollectDeclaredLocals(decl.Body, caseSensitive), fieldNames)) return null;
        // A REF-RETURNING body COMPILES now: `return ref x;` is a call to one of the engine's `__ref_*` helpers, which
        // answers the cell, and the RETURN hands it back uncoerced (the tree's return path, CoerceFlatCallResult and
        // MethodReturn all skip a cell for a declaration that says IsRefReturn). A plain READ of such a call dereferences,
        // so a compiled caller flat-calls it like any other function; the two shapes that want the REFERENCE instead
        // (`F(x) = 7;` and `ref int r = ref F(x);`) are still declined, because the VM has no lvalue path for a call
        // result (see IsRefReturnShape).
        // Event subscription/unsubscription (`this.OnTransform += handler`) compiles to
        // EVENT_ADD/EVENT_SUB (see CompileCompoundMember), no longer a tree fallback.

        // ref/out params: the VM binds them as RefCell slots (CALL wraps the caller's local in a
        // RefCell; LOAD_VAR/STORE_VAR deref/write-through), no longer a compile-time fallback.
        var ctx = new Ctx(code, consts, constMap, localMap, locals, builtins, caseSensitive)
        {
            IsMethod = isMethod,
            AllowNestedDecls = allowNestedDecls,
            IsAsyncBody = decl.IsAsync,
            FieldNames = fieldNames,
            StaticFieldNames = staticFieldNames,
            EventNames = eventNames,
            IsStaticMethod = decl.IsStatic,
            ClassName = className,
            StaticMethodNames = staticMethodNames,
            CoroutineDecls = coroutineDecls,
            CalleeDecls = calleeDecls,
            TypeParamNames = decl.TypeParameters.Count > 0 ? decl.TypeParameters : null,
            TypeParamBindings = decl.TypeParamBindings,
            InstanceMethodNames = instanceMethodNames
        };

        CompileStmtList(ctx, decl.Body);

        if (!ctx.Success) return null;

        if (decl.Body.Count == 0 || decl.Body[^1] is not ReturnStmt)
            code.Add((byte)Opcode.RETURN);

        // A DEFAULT VALUE needs a compiled form as well: a bundle's shell carries no AST, so each default
        // expression travels as a zero-parameter body (see CompiledDefaultExpr). An expression the compiler
        // declines stays null there, and the EXPORT refuses it by name, because a bundle that silently dropped
        // a default would answer "Expected between N and N arguments" for a call the source accepts.
        CompileDefaultThunks(decl, builtins, caseSensitive, calleeDecls);

        return Seal(ctx, code, consts, locals);
    }

    /// <summary>One default parameter value as a zero-parameter body that leaves the value on the stack, so a
    /// bundle can carry it (see CompiledDefaultExpr). Compiled as an ORDINARY function body rather than a
    /// method body: a thunk has no `this` and no enclosing locals, so a default that reads one cannot be
    /// carried, and the export refuses it by name instead of letting the loaded run answer null.</summary>
    public static Bytecode? CompileDefaultValue(Expr value, HashSet<string> builtins, bool caseSensitive = true,
        IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls = null, IReadOnlySet<string>? instanceMethodNames = null,
        bool allowNestedDecls = false)
    {
        var code = new List<byte>();
        var consts = new List<object?>();
        var locals = new List<string>();
        var constMap = new Dictionary<object, int>();
        var localMap = new Dictionary<string, int>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var ctx = new Ctx(code, consts, constMap, localMap, locals, builtins, caseSensitive)
        {
            CalleeDecls = calleeDecls
        };
        CompileExpr(ctx, value);
        if (!ctx.Success) return null;
        ctx.Code.Add((byte)Opcode.RETURN);
        return Seal(ctx, ctx.Code, ctx.Consts, ctx.Locals);
    }

    /// <summary>Compiles a declaration's default parameter values in place (FuncDecl.DefaultThunks, aligned
    /// with Params). This is the ONLY place a default is compiled: the declaration's own body never contains
    /// the expression, and a bundle's shell has no AST to carry it. An expression the compiler declines stays
    /// null, which the EXPORT reads as "this default cannot travel".</summary>
    private static void CompileDefaultThunks(FuncDecl decl, HashSet<string> builtins, bool caseSensitive,
        IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls)
    {
        if (decl.DefaultValues == null) return;
        var thunks = new Bytecode?[decl.Params.Count];
        for (var i = 0; i < decl.DefaultValues.Count && i < thunks.Length; i++)
        {
            var value = decl.DefaultValues[i];
            if (value == null) continue;
            thunks[i] = CompileDefaultValue(value, builtins, caseSensitive, calleeDecls);
        }
        decl.DefaultThunks = thunks;
    }

    /// <summary>Compiles a file's TOP-LEVEL statements as one zero-parameter body: the ENTRY a bytecode bundle
    /// carries so a .slcbc runs like the script it came from. Its direct declarations are GLOBALS (see the
    /// ProgramBody branch in CompileStmt's VarDeclStmt arm), which is what the interpreter's top level defines, so
    /// the file's functions and lambdas keep resolving the same names.
    /// Null when the body declines, exactly as a function body does: a shape the compiler does not model, a slot
    /// past MaxByte, or a top-level `const` (whose const-ness the entry has no way to register yet). The caller
    /// reports the decline rather than bundling an entry that would behave differently.</summary>
    public static Bytecode? CompileProgram(IEnumerable<Stmt> statements, HashSet<string> builtins,
        bool caseSensitive, IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls, out string? declineReason)
    {
        var code = new List<byte>();
        var consts = new List<object?>();
        var locals = new List<string>();
        var constMap = new Dictionary<object, int>();
        var localMap = new Dictionary<string, int>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var body = statements as IReadOnlyList<Stmt> ?? statements.ToList();
        // THE LAST STATEMENT IS THE PROGRAM'S ANSWER WHEN IT IS AN EXPRESSION. A source run reports the value of its
        // last top-level expression statement, which is what `ScriptResult.ReturnValue` carries and what `slc_file` in
        // SQLite hands back, while the compiler emits a POP for an expression statement (stack balance). So a stripped
        // bundle answered nothing where its own source answered a value: measured, `var v = 7; v + 1;` answered 8 from
        // the file and no `ReturnValue` at all from its `--source=strip` bundle. The wrapping is the SAME trick the
        // per-statement top-level path already uses (Interpreter.TryExecuteStatementOnVm), so the two cannot drift.
        if (body.Count > 0 && body[^1] is ExprStmt lastExpr)
        {
            var rewritten = new List<Stmt>(body.Count);
            for (var i = 0; i < body.Count - 1; i++) rewritten.Add(body[i]);
            rewritten.Add(ReturnStmt.Create(lastExpr.Expression, lastExpr.Line, lastExpr.Column));
            body = rewritten;
        }
        var ctx = new Ctx(code, consts, constMap, localMap, locals, builtins, caseSensitive)
        {
            ProgramBody = true,
            CalleeDecls = calleeDecls,
        };
        CompileStmtList(ctx, body);
        if (!ctx.Success)
        {
            declineReason = ctx.DeclineReason ?? "a construct the compiler does not model";
            return null;
        }
        declineReason = null;
        if (body.Count == 0 || body[^1] is not ReturnStmt)
            code.Add((byte)Opcode.RETURN);
        return Seal(ctx, code, consts, locals);
    }

    /// <summary>Compiles a program body the ordinary compile DECLINED, by handing each statement it cannot model
    /// to the TREE instead of giving up on the whole body (see
    /// <see cref="Interpreter.RunCarriedStatementHelperName"/>). A statement is compiled ALONE first, in a throwaway
    /// context shaped exactly like this body, and one that fails there is emitted as a call carrying its own SOURCE
    /// TEXT; every other statement compiles as usual, so the body is the ordinary one minus the statements the tree
    /// owns.
    /// IT CAN ONLY EVER HELP: a statement that compiles alone but NOT in the body's own context (a duplicate local,
    /// a const pool that grew past its width) leaves Success false and this returns null, and the caller keeps its
    /// previous answer, which is the file carrying its whole source. A statement with no source text is never
    /// carried either, so nothing is emitted for a shape the loader could not re-parse.</summary>
    public static Bytecode? CompileProgramWithCarries(IReadOnlyList<Stmt> statements, HashSet<string> builtins,
        bool caseSensitive, IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls, out string? declineReason)
    {
        var code = new List<byte>();
        var consts = new List<object?>();
        var locals = new List<string>();
        var constMap = new Dictionary<object, int>();
        var localMap = new Dictionary<string, int>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var ctx = new Ctx(code, consts, constMap, localMap, locals, builtins, caseSensitive)
        {
            ProgramBody = true,
            CalleeDecls = calleeDecls
        };
        var carried = 0;
        // The names the CARRIED statements declare at their own top level: running one binds them in the global
        // scope, which is what lets the strip's call check see a name a later compiled statement uses (see
        // Bytecode.CarriedDeclaredNames). Collected here because this loop is the only place that knows WHICH
        // statements were carried.
        List<string>? carriedDeclared = null;
        // The program body's own top level is BodyDepth 0, the convention CompileStmtList establishes for its
        // outermost list; this loop IS that list, so it establishes the same thing.
        var savedDepth = ctx.BodyDepth;
        ctx.BodyDepth = savedDepth + 1;
        try
        {
            foreach (var stmt in statements)
            {
                // A DECLARATION is skipped exactly as the ordinary body skips it: it is registered at LOAD (from its
                // own text, or from a descriptor), never executed by the entry.
                if (!Interpreter.IsDeclarationStatement(stmt) && stmt.SourceText is { Length: > 0 } text
                    && !CompilesAlone(stmt, builtins, caseSensitive, calleeDecls, ctx.CallAssignedGlobals, ctx))
                {
                    EmitLoc(ctx, stmt.Line, stmt.Column);
                    EmitLoadConst(ctx, Interpreter.RunCarriedStatementHelperName);
                    EmitLoadConst(ctx, text);
                    ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(1)]);
                    EmitPop(ctx);
                    carried++;
                    switch (stmt)
                    {
                        case VarDeclStmt vd:
                            (carriedDeclared ??= []).Add(vd.Name);
                            break;
                        case DestructuringStmt ds:
                            foreach (var n in ds.VarNames)
                                if (n != "_") (carriedDeclared ??= []).Add(n);
                            break;
                    }
                    continue;
                }
                CompileStmt(ctx, stmt);
                if (!ctx.Success)
                {
                    declineReason = ctx.DeclineReason ?? "a construct the compiler does not model";
                    return null;
                }
            }
        }
        finally { ctx.BodyDepth = savedDepth; }
        declineReason = null;
        // Nothing was carried, so the ordinary compile's own answer (and its decline reason) is the better one.
        if (carried == 0) return null;
        code.Add((byte)Opcode.RETURN);
        var built = Seal(ctx, code, consts, locals);
        if (carriedDeclared != null)
            built = new Bytecode(built.Code, built.Constants, built.Locals, built.NestedLambdas, built.ConstLocals,
                built.GlobalSlotNames, built.BareCalls, built.NestedDecls, carriedDeclared);
        return built;
    }

    /// <summary>True when this statement compiles on its own, in a throwaway context shaped like the program body
    /// (so a top-level `var` is a GLOBAL and BodyDepth is 0, exactly as in the real body). It answers the one
    /// question the carrier needs: can the VM model this statement at all.
    /// `callAssigned` is the flow the BODY has accumulated so far, and seeding it is load-bearing: a `foreach`
    /// over `var coro = Coroutine.Start(...)` compiles fine in a fresh context and would then NOT be carried, so the
    /// body would decline again and the file would carry its whole source (measured on mega_crash_test).</summary>

    private static bool CompilesAlone(Stmt stmt, HashSet<string> builtins, bool caseSensitive,
        IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls, HashSet<string>? callAssigned, Ctx? outer = null)
    {
        // THE PRECHECK ASKS WHAT THE REAL COMPILE WOULD ASK, so it is seeded with the names the body has ALREADY
        // declared (a copy: the throwaway compile must not allocate slots in the real one). With an empty map every
        // statement whose compilability depended on an earlier declaration declined and was CARRIED, and a carried
        // statement writes the GLOBAL scope while the compiled body reads its LOCAL slot: measured on
        // crash_out_ref, whose `try { var _; ... }` blocks declare `_` and whose later `ProduceValue(out _)`
        // therefore compiled in a whole-body compile (writing the local, so `_ == 99`) and was CARRIED under the
        // carrier (writing the global, so `_` answered empty and checks 29/30b failed).
        var localMap = new Dictionary<string, int>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var locals = new List<string>();
        if (outer != null)
        {
            foreach (var kv in outer.LocalSlots) localMap[kv.Key] = kv.Value;
            locals.AddRange(outer.LocalNames);
        }
        var ctx = new Ctx([], [], [], localMap, locals, builtins, caseSensitive)
        {
            ProgramBody = true,
            CalleeDecls = calleeDecls
        };
        if (outer != null)
            foreach (var g in outer.GlobalSlotNames) ctx.GlobalSlot(g);
        if (callAssigned != null && ctx.CallAssignedGlobals != null)
            foreach (var name in callAssigned) ctx.CallAssignedGlobals.Add(name);
        var savedDepth = ctx.BodyDepth;
        ctx.BodyDepth = savedDepth + 1;
        try { CompileStmt(ctx, stmt); }
        finally { ctx.BodyDepth = savedDepth; }
        return ctx.Success;
    }

    /// <summary>Compiles a constructor body to bytecode. Constructors are method-like
    /// (params + body; `this` pre-registered), so this mirrors Compile(FuncDecl) with
    /// isMethod semantics for field writes.</summary>
    public static Bytecode? Compile(ConstructorDecl decl, HashSet<string> builtins, bool caseSensitive = true,
        IReadOnlySet<string>? eventNames = null, IReadOnlySet<string>? fieldNames = null,
        IReadOnlySet<string>? staticFieldNames = null, IReadOnlyDictionary<string, FuncDecl>? coroutineDecls = null,
        IReadOnlyDictionary<string, FuncDecl[]>? calleeDecls = null, IReadOnlySet<string>? instanceMethodNames = null, string? className = null, IReadOnlySet<string>? staticMethodNames = null)
    {
        var code = new List<byte>();
        var consts = new List<object?>();
        var locals = new List<string>();
        var constMap = new Dictionary<object, int>();
        var localMap = new Dictionary<string, int>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        foreach (var p in decl.Params)
            if (!localMap.ContainsKey(p.Name)) { localMap[p.Name] = locals.Count; locals.Add(p.Name); }
        localMap["this"] = locals.Count; locals.Add("this");

        // A ctor writing an instance field with a BARE name (e.g. `X = x` for this.X) compiles as
        // `this.X = x` (LOAD_VAR this + SET_PROP) when the name is a known field; a bare write to
        // an unknown name is ambiguous (global vs instance) → fall back to the tree. Params
        // (registered above) and `this` are excluded; nested statements are scanned recursively.
        if (HasBareNameFieldWrite(decl.Body, CollectParamNames(decl.Params, caseSensitive), CollectDeclaredLocals(decl.Body, caseSensitive), fieldNames)) return null;
        // Event subscription in a ctor body (`this.OnTransform += handler`), EVENT_ADD/EVENT_SUB
        // (see CompileCompoundMember), no longer a tree fallback.

        var ctx = new Ctx(code, consts, constMap, localMap, locals, builtins, caseSensitive)
        {
            IsMethod = true,
            FieldNames = fieldNames,
            StaticFieldNames = staticFieldNames,
            EventNames = eventNames,
            CoroutineDecls = coroutineDecls,
            CalleeDecls = calleeDecls,
            ClassName = className,
            InstanceMethodNames = instanceMethodNames,
            StaticMethodNames = staticMethodNames
        };

        CompileStmtList(ctx, decl.Body);

        if (!ctx.Success) return null;

        if (decl.Body.Count == 0 || decl.Body[^1] is not ReturnStmt)
            code.Add((byte)Opcode.RETURN);

        return Seal(ctx, code, consts, locals);
    }

    /// <summary>Pre-scans a ctor/method body for BARE-name WRITES to names that are neither params
    /// nor body-declared locals, i.e. instance fields of `this`. The VM compiler's AssignExpr /
    /// CompoundAssignExpr / IncrementDecrementExpr handling treats a non-local bare name as a GLOBAL
    /// write (STORE_GLOBAL_FAST), but in a ctor/method a bare name resolves to an instance field via
    /// the tree interpreter, so such bodies must fall back to the tree. Bare-name READS are
    /// intentionally NOT scanned (the record-ctor regression only needs writes; keep blast radius
    /// minimal). Nested statements (if/for/while/block/try/switch/…) are scanned recursively.
    /// Matching follows the interpreter's comparer, localMap is built with `caseSensitive ?
    /// StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase`, so in case-sensitive mode a field
    /// `X` is not matched to param `x` (flagged as a bare field write) and in case-insensitive mode
    /// `X` and `x` are the same identifier (not flagged). Returns true on the FIRST bare-name write
    /// found.</summary>
    /// <summary>True when a member body writes a bare name the compiler cannot attribute: neither a parameter nor a
    /// body-declared local, and not a known instance field. Such a write is ambiguous (a global the file declares, or an
    /// instance field), so every member compile DECLINES the body and the tree resolves it live. It is one question with
    /// three askers: the two class/constructor funnels and, since a setter that wrote a global declared AFTER its block
    /// answered "Undefined variable 'seen13'" (crash_extension_generics 13d), the extension-member compile.</summary>
    internal static bool HasAmbiguousBareNameWrite(FuncDecl member, IReadOnlySet<string>? fieldNames, bool caseSensitive)
        => HasBareNameFieldWrite(member.Body, CollectParamNames(member.Params, caseSensitive),
            CollectDeclaredLocals(member.Body, caseSensitive), fieldNames);

    static bool HasBareNameFieldWrite(IReadOnlyList<Stmt> body, HashSet<string> paramNames, HashSet<string> declaredLocals,
        IReadOnlySet<string>? fieldNames)
    {
        var found = false;

        WalkStmts(body);
        return found;

        // A bare write is an instance-field write iff its name is not a param or a body-declared
        // local, both sets use the interpreter's comparer (Ordinal when caseSensitive, otherwise
        // OrdinalIgnoreCase), matching how localMap resolves the same names during compilation.
        // A name in `fieldNames` is a KNOWN instance field (compiles as this.X); a bare write to
        // an unknown name is ambiguous (global vs a field the compiler can't see) → flag it.
        bool IsBareWrite(string name) => !paramNames.Contains(name) && !declaredLocals.Contains(name)
            && (fieldNames == null || !fieldNames.Contains(name));

        void WalkStmt(Stmt s)
        {
            if (found) return;
            switch (s)
            {
                case ExprStmt es: WalkExpr(es.Expression); return;
                case VarDeclStmt vd: if (vd.Initializer != null) WalkExpr(vd.Initializer); return;
                case ReturnStmt rs: if (rs.Value != null) WalkExpr(rs.Value); return;
                case IfStmt ifs: WalkExpr(ifs.Condition); WalkStmts(ifs.ThenBranch); WalkStmts(ifs.ElseBranch); return;
                case WhileStmt ws: WalkExpr(ws.Condition); WalkStmts(ws.Body); return;
                case ForStmt fs:
                    if (fs.Init != null) WalkStmt(fs.Init);
                    WalkExpr(fs.Condition);
                    if (fs.Increment != null) WalkExpr(fs.Increment);
                    WalkStmts(fs.Body); return;
                case ForeachStmt fes: WalkExpr(fes.Collection); WalkStmts(fes.Body); return;
                case BlockStmt bs: WalkStmts(bs.Statements); return;
                case UsingStmt us: WalkExpr(us.Initializer); WalkStmt(us.Body); return;
                case UsingDeclStmt uds: WalkExpr(uds.Initializer); return;
                case TryStmt ts:
                    WalkStmt(ts.Body);
                    foreach (var c in ts.Catches) WalkStmt(c.Body);
                    if (ts.Finally != null) WalkStmt(ts.Finally); return;
                case DestructuringStmt ds: WalkExpr(ds.Source); return;
                case SwitchStmt ss:
                    WalkExpr(ss.Subject);
                    foreach (var c in ss.Cases)
                    {
                        if (c.Guard != null) WalkExpr(c.Guard);
                        WalkStmts(c.Body);
                    }
                    return;
                case ThrowStmt th: if (th.Expression != null) WalkExpr(th.Expression); return;
                default:
                    return; // leaf statements (break/continue/class decl/…) cannot contain bare writes
            }
        }

        void WalkStmts(IReadOnlyList<Stmt>? stmts)
        {
            if (stmts == null || found) return;
            foreach (var s in stmts) WalkStmt(s);
        }

        void WalkExpr(Expr? e)
        {
            if (found || e == null) return;
            switch (e)
            {
                case AssignExpr { Name: not null } ae when IsBareWrite(ae.Name):
                    found = true; return;
                case AssignExpr ae2: WalkExpr(ae2.Value); return;
                case CompoundAssignExpr { Target: VariableExpr tv } ca when IsBareWrite(tv.Name):
                    found = true; WalkExpr(ca.Value); return;
                case CompoundAssignExpr ca2: WalkExpr(ca2.Value); WalkExpr(ca2.Target); return;
                case IncrementDecrementExpr { Target: VariableExpr tv2 } when IsBareWrite(tv2.Name):
                    found = true; return;
                case IncrementDecrementExpr ide2: WalkExpr(ide2.Target); return;
                case BinaryExpr be: WalkExpr(be.Left); WalkExpr(be.Right); return;
                case LogicalExpr le: WalkExpr(le.Left); WalkExpr(le.Right); return;
                case UnaryExpr ue: WalkExpr(ue.Right); return;
                case CallExpr ce:
                    foreach (var a in ce.Arguments) WalkExpr(a);
                    WalkExpr(ce.Callee); return;
                case MethodCallExpr mce:
                    foreach (var a in mce.Arguments) WalkExpr(a);
                    WalkExpr(mce.Receiver); return;
                case PropertyAccessExpr pe: WalkExpr(pe.Target); return;
                case PropertyAssignExpr pae: WalkExpr(pae.Target); WalkExpr(pae.Value); return;
                case ArrayAccessExpr aae:
                    foreach (var i in aae.Indices) WalkExpr(i);
                    WalkExpr(aae.Array); return;
                case ArrayAssignExpr aaae:
                    foreach (var i in aaae.Indices) WalkExpr(i);
                    WalkExpr(aaae.Array); WalkExpr(aaae.Value); return;
                case ConditionalExpr ce3: WalkExpr(ce3.Condition); WalkExpr(ce3.ThenExpr); WalkExpr(ce3.ElseExpr); return;
                case CastExpr cast: WalkExpr(cast.Expression); return;
                case IsExpr ise: WalkExpr(ise.Left); return;
                case IndexExpr ie: WalkExpr(ie.Value); return;
                case NewInstanceExpr nie:
                    foreach (var a in nie.Arguments ?? []) WalkExpr(a);
                    WalkExpr(nie.ConstructorExpr); return;
                case NewArrayExpr nae:
                    foreach (var s in nae.Sizes ?? []) WalkExpr(s);
                    foreach (var i in nae.Initializer ?? []) WalkExpr(i); return;
                case NewStructExpr nse:
                    foreach (var fv in nse.FieldValues ?? []) WalkExpr(fv); return;
                case SwitchExpr sex:
                    WalkExpr(sex.Subject);
                    foreach (var arm in sex.Arms) { WalkExpr(arm.Guard); WalkExpr(arm.Value); }
                    return;
                case TupleExpr te:
                    foreach (var it in te.Items) WalkExpr(it);
                    return;
                case ListLiteralExpr lle:
                    foreach (var el in lle.Elements) WalkExpr(el);
                    return;
                case DictLiteralExpr dle:
                    foreach (var (k, v) in dle.Entries) { WalkExpr(k); WalkExpr(v); }
                    return;
                case ComprehensionExpr cme:
                    WalkExpr(cme.ElementExpr); WalkExpr(cme.Collection); WalkExpr(cme.Filter); return;
                case MatchExpr mx:
                    WalkExpr(mx.Subject);
                    foreach (var arm in mx.Arms) { if (arm.Guard != null) WalkExpr(arm.Guard); WalkExpr(arm.Value); }
                    return;
                case ObjectInitExpr oie:
                    WalkExpr(oie.Inner);
                    foreach (var (_, v) in oie.Initializers) WalkExpr(v);
                    return;
                case WithExpr we:
                    WalkExpr(we.Base);
                    foreach (var (_, v) in we.Fields) WalkExpr(v);
                    return;
                case NullCoalescingExpr nce: WalkExpr(nce.Left); WalkExpr(nce.Right); return;
                case AsExpr ase: WalkExpr(ase.Left); return;
                case RangeExpr re: WalkExpr(re.From); WalkExpr(re.To); return;
                case LambdaExpr:
                    // Lambda bodies compile through their own FuncDecl path, a bare write there is
                    // that function's concern, not this ctor/method's. Treat as opaque.
                    return;
                default:
                    return; // leaf/unknown shapes cannot contain a bare-name write
            }
        }
    }

    /// <summary>Pre-scans a ctor/method body for EVENT subscription/unsubscription
    /// (`this.OnTransform += handler` / `-= handler`) where the target property name is a declared
    /// event of the enclosing class. The VM compiler's CompileCompoundMember emits
    /// GET_PROP + ADD/SET_PROP, and the ADD opcode on an EventInfo + ScriptFunction throws, the
    /// tree interpreter's dedicated event branch is the only correct execution path. Nested
    /// statements are scanned recursively; lambdas are opaque (they compile through their own
    /// FuncDecl path). Matches property names case-insensitively (script member lookup is
    /// case-insensitive). Returns true on the FIRST event subscription/unsubscription found.</summary>
    static bool HasEventCompoundWrite(IReadOnlyList<Stmt> body, IReadOnlySet<string> eventNames, bool caseSensitive)
    {
        var found = false;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        WalkStmts(body);
        return found;

        bool IsEvent(string name) => eventNames.Contains(name, comparer);

        void WalkStmt(Stmt s)
        {
            if (found) return;
            switch (s)
            {
                case ExprStmt es: WalkExpr(es.Expression); return;
                case VarDeclStmt vd: if (vd.Initializer != null) WalkExpr(vd.Initializer); return;
                case ReturnStmt rs: if (rs.Value != null) WalkExpr(rs.Value); return;
                case IfStmt ifs: WalkExpr(ifs.Condition); WalkStmts(ifs.ThenBranch); WalkStmts(ifs.ElseBranch); return;
                case WhileStmt ws: WalkExpr(ws.Condition); WalkStmts(ws.Body); return;
                case ForStmt fs:
                    if (fs.Init != null) WalkStmt(fs.Init);
                    WalkExpr(fs.Condition);
                    if (fs.Increment != null) WalkExpr(fs.Increment);
                    WalkStmts(fs.Body); return;
                case ForeachStmt fes: WalkExpr(fes.Collection); WalkStmts(fes.Body); return;
                case BlockStmt bs: WalkStmts(bs.Statements); return;
                case UsingStmt us: WalkExpr(us.Initializer); WalkStmt(us.Body); return;
                case UsingDeclStmt uds: WalkExpr(uds.Initializer); return;
                case TryStmt ts:
                    WalkStmt(ts.Body);
                    foreach (var c in ts.Catches) WalkStmt(c.Body);
                    if (ts.Finally != null) WalkStmt(ts.Finally); return;
                case DestructuringStmt ds: WalkExpr(ds.Source); return;
                case SwitchStmt ss:
                    WalkExpr(ss.Subject);
                    foreach (var c in ss.Cases)
                    {
                        if (c.Guard != null) WalkExpr(c.Guard);
                        WalkStmts(c.Body);
                    }
                    return;
                case ThrowStmt th: if (th.Expression != null) WalkExpr(th.Expression); return;
                default:
                    return;
            }
        }

        void WalkStmts(IReadOnlyList<Stmt>? stmts)
        {
            if (stmts == null || found) return;
            foreach (var s in stmts) WalkStmt(s);
        }

        void WalkExpr(Expr? e)
        {
            if (found || e == null) return;
            switch (e)
            {
                case CompoundAssignExpr { Operator.Type: TokenType.PlusEqual or TokenType.MinusEqual, Target: PropertyAccessExpr pae } ca
                    when IsEvent(pae.PropertyName):
                    found = true; return;
                case CompoundAssignExpr ca2: WalkExpr(ca2.Value); WalkExpr(ca2.Target); return;
                case AssignExpr ae: WalkExpr(ae.Value); return;
                case PropertyAssignExpr pae2: WalkExpr(pae2.Target); WalkExpr(pae2.Value); return;
                case IncrementDecrementExpr ide: WalkExpr(ide.Target); return;
                case BinaryExpr be: WalkExpr(be.Left); WalkExpr(be.Right); return;
                case LogicalExpr le: WalkExpr(le.Left); WalkExpr(le.Right); return;
                case UnaryExpr ue: WalkExpr(ue.Right); return;
                case CallExpr ce:
                    foreach (var a in ce.Arguments) WalkExpr(a);
                    WalkExpr(ce.Callee); return;
                case MethodCallExpr mce:
                    foreach (var a in mce.Arguments) WalkExpr(a);
                    WalkExpr(mce.Receiver); return;
                case PropertyAccessExpr pe: WalkExpr(pe.Target); return;
                case ArrayAccessExpr aae:
                    foreach (var i in aae.Indices) WalkExpr(i);
                    WalkExpr(aae.Array); return;
                case ArrayAssignExpr aaae:
                    foreach (var i in aaae.Indices) WalkExpr(i);
                    WalkExpr(aaae.Array); WalkExpr(aaae.Value); return;
                case ConditionalExpr ce3: WalkExpr(ce3.Condition); WalkExpr(ce3.ThenExpr); WalkExpr(ce3.ElseExpr); return;
                case CastExpr cast: WalkExpr(cast.Expression); return;
                case IsExpr ise: WalkExpr(ise.Left); return;
                case IndexExpr ie: WalkExpr(ie.Value); return;
                case NewInstanceExpr nie:
                    foreach (var a in nie.Arguments ?? []) WalkExpr(a);
                    WalkExpr(nie.ConstructorExpr); return;
                case NewArrayExpr nae:
                    foreach (var s in nae.Sizes ?? []) WalkExpr(s);
                    foreach (var i in nae.Initializer ?? []) WalkExpr(i); return;
                case NewStructExpr nse:
                    foreach (var fv in nse.FieldValues ?? []) WalkExpr(fv); return;
                case SwitchExpr sex:
                    WalkExpr(sex.Subject);
                    foreach (var arm in sex.Arms) { WalkExpr(arm.Guard); WalkExpr(arm.Value); }
                    return;
                case TupleExpr te:
                    foreach (var it in te.Items) WalkExpr(it);
                    return;
                case ListLiteralExpr lle:
                    foreach (var el in lle.Elements) WalkExpr(el);
                    return;
                case DictLiteralExpr dle:
                    foreach (var (k, v) in dle.Entries) { WalkExpr(k); WalkExpr(v); }
                    return;
                case ComprehensionExpr cme:
                    WalkExpr(cme.ElementExpr); WalkExpr(cme.Collection); WalkExpr(cme.Filter); return;
                case MatchExpr mx:
                    WalkExpr(mx.Subject);
                    foreach (var arm in mx.Arms) { if (arm.Guard != null) WalkExpr(arm.Guard); WalkExpr(arm.Value); }
                    return;
                case ObjectInitExpr oie:
                    WalkExpr(oie.Inner);
                    foreach (var (_, v) in oie.Initializers) WalkExpr(v);
                    return;
                case WithExpr we:
                    WalkExpr(we.Base);
                    foreach (var (_, v) in we.Fields) WalkExpr(v);
                    return;
                case NullCoalescingExpr nce: WalkExpr(nce.Left); WalkExpr(nce.Right); return;
                case AsExpr ase: WalkExpr(ase.Left); return;
                case RangeExpr re: WalkExpr(re.From); WalkExpr(re.To); return;
                case LambdaExpr:
                    return; // opaque, compiles through its own FuncDecl path
                default:
                    return;
            }
        }
    }

    /// <summary>Set of a ctor/method's parameter names (comparer per the interpreter's caseSensitive
    /// flag), the write-detection pre-scan must decide membership the same way localMap does, so a
    /// bare write to field `X` is flagged in case-sensitive mode but treated as the param `x` in
    /// case-insensitive mode.</summary>
    static HashSet<string> CollectParamNames(IReadOnlyList<(TypeInfo Type, string Name)> @params, bool caseSensitive)
    {
        var names = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var p in @params) names.Add(p.Name);
        return names;
    }

    /// <summary>Walks a body and collects every locally-declared name (var/foreach/using/
    /// destructuring/catch) into a set using the interpreter's comparer (Ordinal when caseSensitive,
    /// OrdinalIgnoreCase otherwise). The VM resolves body locals by name, so a bare write
    /// to one of these is a LOCAL write, not an instance-field write, the detection pre-scan must
    /// know about them BEFORE it walks (it runs before body statements are compiled, when the caller's
    /// localMap holds only params + `this`). Scoping is deliberately flat: a name declared anywhere
    /// in the body shadows a same-named instance field for VM purposes. Params are NOT included (the
    /// caller passes them separately); lambda bodies are skipped (their locals belong to the lambda's
    /// own FuncDecl compile path).</summary>
    static HashSet<string> CollectDeclaredLocals(IReadOnlyList<Stmt> body, bool caseSensitive)
    {
        var locals = new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

        void WalkStmts(IReadOnlyList<Stmt>? stmts)
        {
            if (stmts == null) return;
            foreach (var s in stmts) WalkStmt(s);
        }

        void WalkStmt(Stmt s)
        {
            switch (s)
            {
                case VarDeclStmt vd:
                    locals.Add(vd.Name);
                    break;
                case ForeachStmt fes:
                    if (fes.LoopVarNames is { Count: > 0 } fesLoopNames)
                        foreach (var ln in fesLoopNames) locals.Add(ln);
                    else
                        locals.Add(fes.VarName);
                    WalkStmts(fes.Body);
                    break;
                case UsingStmt us:
                    locals.Add(us.VarName);
                    WalkStmt(us.Body);
                    break;
                case UsingDeclStmt uds:
                    locals.Add(uds.VarName);
                    break;
                case DestructuringStmt ds:
                    foreach (var n in ds.VarNames) locals.Add(n);
                    break;
                case TryStmt ts:
                    WalkStmt(ts.Body);
                    foreach (var c in ts.Catches)
                    {
                        if (c.VarName.Length > 0) locals.Add(c.VarName);
                        WalkStmt(c.Body);
                    }
                    if (ts.Finally != null) WalkStmt(ts.Finally);
                    break;
                case IfStmt ifs:
                    WalkStmts(ifs.ThenBranch);
                    WalkStmts(ifs.ElseBranch);
                    break;
                case WhileStmt ws:
                    WalkStmts(ws.Body);
                    break;
                case ForStmt fs:
                    if (fs.Init != null) WalkStmt(fs.Init); // Init may itself be a VarDeclStmt
                    WalkStmts(fs.Body);
                    break;
                case BlockStmt bs:
                    WalkStmts(bs.Statements);
                    break;
                case SwitchStmt ss:
                    foreach (var c in ss.Cases) WalkStmts(c.Body);
                    break;
            }
        }

        WalkStmts(body);
        return locals;
    }

    private sealed class Ctx(List<byte> code, List<object?> consts, Dictionary<object, int> constMap,
        Dictionary<string, int> localMap, List<string> locals, HashSet<string> builtinsSet,
        bool caseSensitive)
    {
        /// <summary>True when the body being compiled is an `async` one, the only place an `await` may suspend: anywhere else, a file's top level included, the body declines and the tree runs it with its driving wait.</summary>
        public bool IsAsyncBody;



        private bool _success = true;        /// <summary>False once the compiler gives up on this body. A property rather than a field so the reason can
                                             /// be recorded centrally: the ~40 decline sites set it and would otherwise report nothing a reader can act
                                             /// on.</summary>
        public bool Success
        {
            get => _success;
            set
            {
                if (!value && DeclineReason == null)
                    DeclineReason = $"while compiling a {CurrentStatement ?? "construct"}"
                        + (DeclineNote is { Length: > 0 } note ? $": {note}" : "");
                _success = value;
            }
        }
        /// <summary>The innermost statement or expression CompileStmt/CompileExpr is working on, so a decline that
        /// does not name itself still names its construct.</summary>
        public string? CurrentStatement;

        /// <summary>WHAT WOULD FIX THE DECLINE, for a site whose reason alone leaves a reader guessing. The
        /// <see cref="Success"/> setter appends it, so one statement of the advice reaches the dump's reason AND the
        /// strip's refusal, which are the two places a reader meets it.</summary>
        public string? DeclineNote;
        /// <summary>Bare-name calls emitted for this body, so the strip can refuse one the bundle cannot register.</summary>
        public List<string>? BareCalls;
        public List<FuncDecl>? NestedDecls;
        /// <summary>The functions THIS statement list declares whose SLOT exists already but whose declaration has not run
        /// yet (see PreallocateDeclSlots), keyed by name: a lambda capturing one of them captures the SLOT as a cell, and
        /// the declaration fills that cell at its statement. An entry that is still here when a nested declaration compiles
        /// means that declaration captures a sibling it has not reached, which is the one shape a compiled body cannot
        /// express, so the body declines and the tree runs it.</summary>
        public Dictionary<string, int>? PendingDeclSlots;
        /// <summary>True while a CONDITION (if/while/for) is being compiled. The tree allows a pattern that BINDS names only
        /// there (`_condBinding`, whose refusal is `Pattern variable '<name>' is only allowed in if/while conditions.`), and
        /// the classic inline `expr is Type name` form is the one exception it binds anywhere. The compiler mirrors the rule
        /// by DECLINING the other shapes outside a condition, so the tree raises its own sentence instead of a second one.
        /// </summary>
        public bool InCondition;
        /// <summary>True when a NESTED declaration in this body may take a local slot: set for a LAMBDA body, and
        /// propagated into a declaration a lambda body compiles (`deep` inside `mid` inside a lambda). A plain
        /// function body keeps the DECL-TEXT carrier, which is where a 16-deep `ref`/`out` chain was measured to
        /// misbehave when its bodies were compiled instead.
        public bool AllowNestedDecls;
        /// <summary>The concrete type each of THIS declaration's type parameters is bound to, when it is a generic
        /// CLONE being compiled (FuncDecl.TypeParamBindings). A type REFERENCE the body still spells as `T` (the
        /// substitution walk rewrites three node shapes only) is emitted with the binding instead, which is the
        /// compile-time half of Interpreter.ResolveTypeToClr's binding consult (the tree's half).</summary>
        public Dictionary<string, TypeInfo>? TypeParamBindings;
        /// <summary>The name of the `var` whose INITIALIZER is being compiled right now, so a lambda inside it that
        /// captures that same name can stay interpreted: a compiled lambda captures a cell the enclosing store fills
        /// afterwards, while an interpreted one reads the snapshot taken at creation and raises
        /// "Undefined variable" (see the lambda compile site).</summary>
        public string? DeclaringVar;
        // Compiling a file's TOP-LEVEL statements as one body (see the CompileProgram method below): this body's
        // direct declarations are GLOBALS rather than locals, which is what the interpreter's top level defines.
        public bool ProgramBody;
        /// <summary>The type-parameter names THIS declaration declares. A type argument that names one of them
        /// cannot travel as a VM opcode (its binding lives where the tree closes the value), so the compiler
        /// declines it; a type argument naming anything else is an ORDINARY type and compiles. Null for a
        /// program body, which declares none.</summary>
        public IReadOnlyList<string>? TypeParamNames;
        /// <summary>Set by the FIRST construct this body cannot compile, so a declined PROGRAM BODY can name it.
        /// The bundle writer prints this: a refusal that only says "declined" gives a reader nothing to act on.</summary>
        public string? DeclineReason;
        public bool IsMethod;   // compiling a class/struct method, bare-name callees resolve via class context, not the global scope
        public bool CaseSensitive => caseSensitive;

        /// <summary>True when `name` is a VM local of this body: a parameter, `this`, a captured local or a
        /// variable the body declared. A ref/out ARGUMENT naming anything else has no bytecode form, because the
        /// tree CREATES the variable on capture (that is what an inline `out var x` declaration is) while the VM
        /// would compile a read of a name it never declared.</summary>
        public bool IsLocal(string name) => localMap.ContainsKey(name);

        /// <summary>The local slot table, READ ONLY, for a caller that has to compile a statement in a THROWAWAY
        /// context and must see the same names the real body has declared (CompilesAlone's precheck). A copy is
        /// taken by the caller, so nothing it does can allocate a slot here.</summary>
        public IReadOnlyDictionary<string, int> LocalSlots => localMap;
        /// <summary>The local names in slot order, beside <see cref="LocalSlots"/> and for the same caller.</summary>
        public IReadOnlyList<string> LocalNames => locals;

        /// <summary>The names a PROGRAM BODY assigned from a CALL at its own top level (`var coro = Coroutine.Start(...)`),
        /// so a `foreach` over one can be DECLINED. A collection that came from a call may be a COROUTINE or an async
        /// sequence, which the VM's folded drive cannot iterate (CreateForEachState answers null) and the statement
        /// then BAILS, and a bundle's ONE-body entry cannot re-run that statement on the tree the way a source run
        /// does. Declining hands the whole statement to the tree, which is exactly what a source run does with the
        /// bail. Null unless the statement carrier is ON, because declining is only useful when something can carry
        /// the statement (with it off, declining would make the file carry its whole source instead).</summary>
        public readonly HashSet<string>? CallAssignedGlobals = Interpreter.StatementCarrierEnabled
            ? new HashSet<string>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            : null;
        // Enclosing class's instance-field names (own + inherited), a bare name in this set is
        // an instance-field access (`X` ≡ `this.X`), compiled as GET_PROP/SET_PROP. Static-field
        // names are tracked separately (bare static writes fall back, the VM has no _currentClass).
        public IReadOnlySet<string>? FieldNames;
        public IReadOnlySet<string>? StaticFieldNames;
        // Enclosing class's event names (own + inherited), `this.Event += h` compiles to
        // EVENT_ADD/EVENT_SUB; a non-event property name with +=/-= stays a numeric compound.
        public IReadOnlySet<string>? EventNames;
        /// <summary>True when compiling a STATIC method, no `this` local value is supplied at
        /// dispatch, so implicit-this bare callees (`Factorial(...)` in a static method) must NOT
        /// compile as LOAD_VAR this + CALL_METHOD; they resolve via the class's static fields.</summary>
        public bool IsStaticMethod;
        /// <summary>True for a LAMBDA body compiled inside a class member: it resolves bare sibling names through
        /// the enclosing class exactly as a method body does, but it is not a method (IsMethod also decides a
        /// generic type-parameter question, and a lambda must not flip that).</summary>
        public bool IsLambda;
        /// <summary>How deep inside nested statement bodies this compile is: 0 at the function own top level and
        /// more inside any nested block/loop/if body. A local declared BELOW the top level is a binding the
        /// tree recreates on every execution of that declaration, so its slot is given a fresh capture cell
        /// (RENEW_LOCAL) and closures from different executions stop sharing one.</summary>
        public int BodyDepth = -1;
        /// <summary>How deep inside NESTED EXPRESSIONS this compile is, for the depth guard below: the walk is native
        /// recursion, so a tree nested deeper than <see cref="MaxExprDepth"/> has to be declined rather than walked.</summary>
        public int ExprDepth;
        /// <summary>Enclosing class name, static bare callees dispatch as CALL_METHOD on the
        /// ClassInfo value (e.g. `Factorial(n-1)` in MathUtils → `MathUtils.Factorial(...)`).</summary>
        public string? ClassName;
        /// <summary>The enclosing class's INSTANCE method names, the twin of StaticMethodNames: a bare-name
        /// callee that names one resolves through the class (the tree via FindMethodInHierarchy, the VM as
        /// `this.Name(...)`). A field-name set could not answer this: a class with NO fields (every one of the
        /// designer's fifteen control kinds) passed null, so the VM emitted a plain CALL and a source-less
        /// bundle had no tree to fall back to: "Method 'FactoryName' is bytecode-only".</summary>
        public IReadOnlySet<string>? InstanceMethodNames;
        /// <summary>Enclosing class's STATIC METHOD names (own + inherited), a bare call to one in
        /// a static method dispatches via the ClassInfo (ClassName.Method).</summary>
        public IReadOnlySet<string>? StaticMethodNames;
        /// <summary>Global bare-name callees safe to drive in-place via FOREACH_CALL (Task 4): name →
        /// the coroutine's FuncDecl for registered coroutines that compiled to VM bytecode and are free
        /// of yield-request producers. A bare-name call to one of these as a foreach collection compiles
        /// to FOREACH_CALL instead of CALL + FOREACH_INIT (skips the Coroutine+Generator wrappers),
        /// but ONLY when the decl also satisfies the FOREACH_CALL arg-binding contract (no default
        /// values, no trailing params, no ref/out params, and the call passes exactly the declared
        /// param count); see CompileStmt's ForeachStmt gate, which mirrors the VM's own re-check.
        /// Null when the caller supplied no map, FOREACH_CALL is then never emitted and every foreach
        /// keeps the existing path.</summary>
        public IReadOnlyDictionary<string, FuncDecl>? CoroutineDecls;
        /// <summary>The bare-name GLOBAL callees, by name, so a call can be gated on the CALLEE's declaration
        /// rather than on its name alone. Only the caller knows them (the interpreter reads its global scope,
        /// `Program.cs` its top-level declarations), exactly like CoroutineDecls, and null means "this caller
        /// supplied none", which simply skips the gate below.</summary>
        public IReadOnlyDictionary<string, FuncDecl[]>? CalleeDecls;
        public readonly HashSet<int> ConstLocals = [];
        public readonly List<FuncDecl> NestedLambdas = [];
        // Per-scope using-decl local indices, the enclosing CompileStmtList wrapper's finally
        // disposes them in reverse order at scope exit. Innermost scope is Peek().
        public readonly Stack<List<int>> UsingLocals = [];
        // Global fast-slot table (spec A): per-function slot index → global name. LOAD_GLOBAL_FAST /
        // STORE_GLOBAL_FAST carry the slot index as their operand; the name table ships on Bytecode
        // so the VM can resolve/cache globals per invocation. The comparer mirrors the interpreter's
        // case sensitivity (Compiler.Compile's caseSensitive param, fed from Interpreter.CaseSensitive):
        // an Ordinal table in a case-INSENSITIVE host would split `Foo`/`foo` into separate slots for
        // the SAME global, a store through one slot never updates the other → stale reads.
        private readonly Dictionary<string, int> _globalSlots = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _globalSlotNames = [];

        /// <summary>255, the largest value a 1-byte operand can carry: constant, local, slot and table
        /// indices, and every count.</summary>
        private const int MaxByte = 255;

        /// <summary>A CONSTANT index, capped by the width the format declares for it, ONE statement, so widening
        /// the const operand (v10 made it 2 bytes because a growing program body crossed 255 and the 1-byte operand
        /// declined the body silently) is a change here and in <see cref="BytecodeFormat.ConstWidthBytes"/>, not at
        /// the twelve executor arms that read a const. `MaxByte` above stays for the operands the format cannot
        /// widen: counts, and the slot and local indices.
        /// See BytecodeFormat.ConstWidthBytes.</summary>
        public static readonly int MaxConstIndex = (1 << (8 * BytecodeFormat.ConstWidthBytes)) - 1;

        /// <summary>A 1-byte operand the format cannot widen, a count (arguments, elements, pairs,
        /// variables, captures). Above 255 the value cannot be represented, so the function bails to the
        /// tree instead of emitting a TRUNCATED count: 300 elements read back as 44. That failure mode is
        /// a wrong value, and it was measured, a 300-element literal made a valid program fail with a
        /// bogus format error rather than a clear one.</summary>
        public byte Count8(int n)
        {
            if ((uint)n > MaxByte) { Success = false; return 0; }
            return (byte)n;
        }

        /// <summary>Returns the slot index for a global name, assigning a new one on first use. The operand
        /// is one byte, so slot 256 and beyond cannot be named: the function bails to the tree, which
        /// resolves the name without a slot table, rather than emitting a truncated slot index.</summary>
        public int GlobalSlot(string name)
        {
            if (_globalSlots.TryGetValue(name, out var idx)) return idx;
            idx = _globalSlotNames.Count;
            _globalSlots[name] = idx;
            _globalSlotNames.Add(name);
            if (idx > MaxByte) Success = false;   // 1-byte operand: a slot cannot be named past 255
            return idx;
        }

        /// <summary>Slot table for this function, appended to Bytecode for the VM's per-invocation slots.</summary>
        public string[] GlobalSlotNames => [.. _globalSlotNames];
        /// <summary>Whether this body already uses a global slot for that name, WITHOUT creating one: the question
        /// a ref/out argument asks, because a name this body never declares is either an `out var` DECLARATION (which
        /// the tree creates) or a typo (which the tree refuses), and neither has a bytecode form.</summary>
        public bool HasGlobalSlot(string name) => _globalSlots.ContainsKey(name);
        public List<byte> Code => code;
        /// <summary>How many constants this body's pool holds, for a caller that needs to roll it back.</summary>
        public int ConstCount => consts.Count;

        /// <summary>Drops the constants added since <paramref name="mark"/>. A fold DELETES the code that pushed
        /// them, so nothing can point at them any more, and the pool has to shrink with the code: it is indexed by
        /// a fixed-width operand (<see cref="BytecodeFormat.ConstWidthBytes"/>, hence
        /// <see cref="Compiler.MaxConstIndex"/>), and a chain of literals folds one step at a time, adding a
        /// DISTINCT value per step (2, 3, 4, ...), so without this a long chain walks the index past the operand's
        /// ceiling and <c>Success</c> goes false. Measured at one byte: 255 terms compiled and 256 did not, which
        /// is what silently demoted a 1,000-term `return` to the tree.</summary>
        public void TrimConstsTo(int mark)
        {
            for (var i = consts.Count - 1; i >= mark; i--) constMap.Remove(consts[i] ?? NullSentinel);
            consts.RemoveRange(mark, consts.Count - mark);
        }

        public int AddConst(object? val)
        {
            var key = val ?? NullSentinel;
            if (!constMap.TryGetValue(key, out var idx))
            { idx = consts.Count; consts.Add(val); constMap[key] = idx; }
            if (idx > MaxConstIndex) Success = false;   // the CONST operand's width decides (BytecodeFormat.ConstWidthBytes)
            return idx;
        }

        public int LocalIdx(string name)
        {
            if (localMap.TryGetValue(name, out var idx)) return idx;
            idx = locals.Count; localMap[name] = idx; locals.Add(name);
            if (idx > MaxByte) Success = false;   // 1-byte operand: a local cannot be named past 255
            return idx;
        }

        /// <summary>A CONDITION'S PATTERN NAMES ARE SCOPED TO ITS STATEMENT, and this pair is the COMPILE-TIME half of
        /// that scoping. The names a condition binds live in frame slots either way, so nothing about storage changes;
        /// what changes is whether a later READ still resolves to one. The tree scopes a pattern binding to the
        /// statement's branches, so a read after the statement is `Undefined variable '<name>'`; the compiled path left
        /// the name mapped for the rest of the body, and a bundle therefore answered a value where its file refused.
        /// Measured on `tuple_is_patterns` 12b (`if (coords is (int x2, int y2)) { ... }` then `var z = x2 + y2;`):
        /// the file raises, the bundle printed 7. This drops a branch's own locals too, which is what the tree's branch
        /// scope does.
        ///
        /// NARROW ON PURPOSE: it removes what the map GAINED and leaves every existing mapping alone, because a `for`
        /// loop rebinds its init variable to a fresh slot and puts the name back at exit itself (see the ForStmt arm),
        /// and re-applying the snapshot would undo that.</summary>
        public Dictionary<string, int> LocalsBefore() => new(localMap, localMap.Comparer);

        public void DropLocalsSince(Dictionary<string, int> snapshot)
        {
            foreach (var name in localMap.Keys.ToArray())
                if (!snapshot.ContainsKey(name)) localMap.Remove(name);
        }
        /// <summary>Declared type per local slot (null = dynamic/var/unknown). Recorded at the
        /// VarDeclStmt so later AssignExprs can widen a narrower numeric value to the declared
        /// type (e.g. `long sum = 0; sum = 0;` must keep sum as long, WIDEN_NUM).</summary>
        public readonly List<TypeInfo?> LocalDeclaredTypes = [];
        public void SetLocalDeclaredType(int idx, TypeInfo? ti)
        {
            while (LocalDeclaredTypes.Count <= idx) LocalDeclaredTypes.Add(null);
            LocalDeclaredTypes[idx] = ti;
        }
        private int _tempCounter;
        public int AllocTemp() => LocalIdx("__temp_" + (_tempCounter++));

        /// <summary>True for numeric declared types that widen narrower numeric values on assignment
        /// (long/double/decimal/float). `var`/object/string/int etc. are not widening targets.</summary>
        internal static bool IsWideningNumericDecl(TypeInfo? ti) => ti?.BaseType switch
        {
            TokenType.Long or TokenType.Double or TokenType.Decimal or TokenType.Float => true,
            _ => false
        };

        /// <summary>The name `typeof` must produce for an expression the compiler can type STATICALLY, or
        /// null when it cannot (the caller then emits the runtime call). Only the integral family is resolved,
        /// and only where the static answer provably equals the tree's runtime answer, measured, see
        /// assets/language/repl/vm/vm_typeof_parity.slc:
        ///   * a local declared int or long → that name;
        ///   * +, - or * over integral operands → CLR integral promotion, i.e. "long" if either side is long,
        ///     else "int" (`short + short` is "int"; the VM's runtime path said "long").
        /// Deliberately NOT resolved, each for a measured reason: / and %, uint/ulong (the CLR promotes those
        /// to themselves, not to int/long), a BARE short/byte/uint local and a whole-valued literal like `1.0`
        /// (both engines call the latter "int", a shared quirk), double and decimal literals, decimal
        /// arithmetic, and every non-numeric shape (fields, calls, casts, object-typed operands). Those keep the
        /// runtime call, which is the parity-preserving default: the tree names them by their runtime type.
        /// Float IS resolved even though it is not integral, because a float slot on the compiled path boxes as
        /// a double, so the NAME can only come from here while the tree says "float".</summary>
        internal static string? StaticNumericTypeName(Ctx ctx, Expr e) => e switch
        {
            VariableExpr v => LocalDeclaredName(ctx, v.Name),
            NumberExpr n when n.Type.BaseType == TokenType.Float => "float",
            BinaryExpr b when IsNumericArithmetic(b.Operator.Type) => PromotedNumericName(ctx, b),
            _ => null,
        };

        /// <summary>A local's declared numeric name, or null when this does not resolve it: a bare short/byte/
        /// uint local and a bare double are left to the runtime call, since their two engines are measured to
        /// agree there already.</summary>
        private static string? LocalDeclaredName(Ctx ctx, string name) => LocalDeclaredBase(ctx, name) switch
        {
            TokenType.Int => "int",
            TokenType.Long => "long",
            TokenType.Float => "float",
            _ => null,
        };

        /// <summary>The tree's own promotion, measured: both float → "float"; otherwise anything float or
        /// double drags the result to "double"; otherwise the wider integral wins, and anything narrower than
        /// int promotes to int as CLR arithmetic does. Null when either side is not resolvable.</summary>
        private static string? PromotedNumericName(Ctx ctx, BinaryExpr b)
        {
            var l = NumericOperandName(ctx, b.Left);
            var r = NumericOperandName(ctx, b.Right);
            if (l == null || r == null) return null;
            if (l == "float" && r == "float") return "float";
            if (l is "float" or "double" || r is "float" or "double") return "double";
            return l == "long" || r == "long" ? "long" : "int";
        }

        private static string? NumericOperandName(Ctx ctx, Expr e) => e switch
        {
            VariableExpr v => LocalDeclaredBase(ctx, v.Name) switch
            {
                TokenType.Int or TokenType.Short or TokenType.Byte or TokenType.SByte or TokenType.UShort => "int",
                TokenType.Long => "long",
                TokenType.Float => "float",
                TokenType.Double => "double",
                _ => null,
            },
            NumberExpr n when n.Type.BaseType == TokenType.Float => "float",
            BinaryExpr b when IsNumericArithmetic(b.Operator.Type) => PromotedNumericName(ctx, b),
            _ => null,
        };

        private static TokenType? LocalDeclaredBase(Ctx ctx, string name)
            => ctx.LocalMap.TryGetValue(name, out var idx) && idx < ctx.LocalDeclaredTypes.Count
                ? ctx.LocalDeclaredTypes[idx]?.BaseType
                : null;

        private static bool IsNumericArithmetic(TokenType t)
            => t is TokenType.Plus or TokenType.Minus or TokenType.Star;

        // Analyzes a capturing lambda body: returns the set of CAPTURED names that are written
        // (Assign/CompoundAssign/IncDec targets) and whether the body contains a shape the VM
        // compiler doesn't model (unknown node → conservative tree fallback).
        internal static void AnalyzeCaptures(LambdaExpr le, IReadOnlyList<Stmt> body,
            out HashSet<string>? written, out bool unknownShape, out List<string>? called)
        {
            written = null;
            unknownShape = false;
            called = null;
            var caps = le.CapturedNames;
            if (caps is not { Count: > 0 }) return;
            var capSet = new HashSet<string>(caps, StringComparer.OrdinalIgnoreCase);
            var writtenList = new List<string>();
            // Names the body CALLS, which is the shape a compiled lambda answers differently from an interpreted one
            // (see the lambda arm): the call resolves through the cell a compiled body shares with the enclosing store,
            // while an interpreted lambda reads the snapshot taken at creation and raises "Undefined variable". A plain
            // READ of the same name agrees on both legs, so it must not be treated as a divergence.
            var calledList = new List<string>();
            var unknownLocal = false;

            void WalkExpr(Expr? e)
            {
                if (unknownLocal || e == null) return;
                switch (e)
                {
                    case AssignExpr { Name: not null } ae when capSet.Contains(ae.Name):
                        writtenList.Add(ae.Name); return;
                    case CompoundAssignExpr { Target: VariableExpr tv } ca when capSet.Contains(tv.Name):
                        writtenList.Add(tv.Name); WalkExpr(ca.Target); WalkExpr(ca.Value); return;
                    case IncrementDecrementExpr { Target: VariableExpr tv2 } ide when capSet.Contains(tv2.Name):
                        writtenList.Add(tv2.Name); WalkExpr(ide.Target); return;
                    case AssignExpr ae2: WalkExpr(ae2.Value); return;
                    case CompoundAssignExpr ca2: WalkExpr(ca2.Value); WalkExpr(ca2.Target); return;
                    case IncrementDecrementExpr ide2: WalkExpr(ide2.Target); return;
                    case BinaryExpr be: WalkExpr(be.Left); WalkExpr(be.Right); return;
                    case LogicalExpr le2: WalkExpr(le2.Left); WalkExpr(le2.Right); return;
                    case UnaryExpr ue: WalkExpr(ue.Right); return;
                    // Read-only expressions, a bare variable READ cannot write a capture (only
                    // AssignExpr/CompoundAssignExpr/IncrementDecrementExpr write), so these never
                    // affect the written-set; recurse into their sub-expressions for nested writes.
                    case VariableExpr: return;                       // bare read, no write possible
                    case NumberExpr:
                    case StringExpr:
                    case BoolExpr:
                    case CharExpr:
                    case NullExpr:
                    case DiscardExpr:
                    case ThisExpr:
                    case SuperExpr:
                    case BaseExpr: return;
                    case TupleExpr te2: foreach (var i in te2.Items) WalkExpr(i); return;
                    case ListLiteralExpr lle2: foreach (var i in lle2.Elements) WalkExpr(i); return;
                    case DictLiteralExpr dle2: foreach (var (k, v) in dle2.Entries) { WalkExpr(k); WalkExpr(v); } return;
                    case SwitchExpr swe2: WalkExpr(swe2.Subject); foreach (var a in swe2.Arms) { if (a.Guard != null) WalkExpr(a.Guard); WalkExpr(a.Value); } return;
                    case MatchExpr me2: WalkExpr(me2.Subject); foreach (var arm in me2.Arms) { if (arm.Guard != null) WalkExpr(arm.Guard); WalkExpr(arm.Value); } return;
                    case NullCoalescingExpr nce2: WalkExpr(nce2.Left); WalkExpr(nce2.Right); return;
                    case IsNullExpr inle2: WalkExpr(inle2.Left); return;
                    case AsExpr ase2: WalkExpr(ase2.Left); return;
                    case RangeExpr re2: if (re2.From != null) WalkExpr(re2.From); if (re2.To != null) WalkExpr(re2.To); return;
                    case ObjectInitExpr oie2: WalkExpr(oie2.Inner); foreach (var (n, v) in oie2.Initializers) WalkExpr(v); return;
                    case WithExpr we2: WalkExpr(we2.Base); foreach (var (fn, fv) in we2.Fields) WalkExpr(fv); return;
                    case LambdaExpr le2: return;                     // nested lambda, its own captures resolved separately
                    case ComprehensionExpr ce4: WalkExpr(ce4.Collection); if (ce4.Filter != null) WalkExpr(ce4.Filter); WalkExpr(ce4.ElementExpr); return;
                    case YieldExpr ye: if (ye.Value != null) WalkExpr(ye.Value); return;
                    case CallExpr ce:
                        foreach (var a in ce.Arguments) WalkExpr(a);
                        if (ce.Callee is VariableExpr ceCallee && capSet.Contains(ceCallee.Name))
                            calledList.Add(ceCallee.Name);
                        WalkExpr(ce.Callee); return;
                    case MethodCallExpr mce:
                        foreach (var a in mce.Arguments) WalkExpr(a);
                        WalkExpr(mce.Receiver); return;
                    case PropertyAccessExpr pe: WalkExpr(pe.Target); return;
                    case PropertyAssignExpr pae: WalkExpr(pae.Target); WalkExpr(pae.Value); return;
                    // `F(..) = 7;` writes through the REFERENCE the call answered, so it cannot write a captured name
                    // itself (a capture is a NAME); its two operands are walked for the same reason. Without this case the
                    // shape counted as unknown and declined every lambda that held one.
                    case RefAssignExpr rae2: WalkExpr(rae2.CellSource); WalkExpr(rae2.Value); return;
                    case ArrayAssignExpr aae2:
                        foreach (var ix in aae2.Indices) WalkExpr(ix);
                        WalkExpr(aae2.Array); WalkExpr(aae2.Value); return;
                    case ArrayAccessExpr aae:
                        foreach (var i in aae.Indices) WalkExpr(i);
                        WalkExpr(aae.Array); return;
                    case ConditionalExpr ce3: WalkExpr(ce3.Condition); WalkExpr(ce3.ThenExpr); WalkExpr(ce3.ElseExpr); return;
                    case CastExpr cast: WalkExpr(cast.Expression); return;
                    case IsExpr ise: WalkExpr(ise.Left); return;
                    case IndexExpr ie: WalkExpr(ie.Value); return;
                    case NewInstanceExpr nie:
                        foreach (var a in nie.Arguments ?? []) WalkExpr(a);
                        WalkExpr(nie.ConstructorExpr); return;
                    case NewArrayExpr nae:
                        foreach (var s in nae.Sizes ?? []) WalkExpr(s);
                        foreach (var i in nae.Initializer ?? []) WalkExpr(i); return;
                    default:
                        // Unknown expression shape, conservative: treat as unsafe → tree path
                        unknownLocal = true; return;
                }
            }

            void WalkStmt(Stmt s)
            {
                if (unknownLocal) return;
                switch (s)
                {
                    case ExprStmt es: WalkExpr(es.Expression); return;
                    case VarDeclStmt vd: if (vd.Initializer != null) WalkExpr(vd.Initializer); return;
                    case ReturnStmt rs: if (rs.Value != null) WalkExpr(rs.Value); return;
                    case IfStmt ifs: WalkExpr(ifs.Condition); WalkStmts(ifs.ThenBranch); WalkStmts(ifs.ElseBranch); return;
                    case WhileStmt ws: WalkExpr(ws.Condition); WalkStmts(ws.Body); return;
                    case ForStmt fs:
                        if (fs.Init != null) WalkStmt(fs.Init);
                        WalkExpr(fs.Condition);
                        if (fs.Increment != null) WalkExpr(fs.Increment);
                        WalkStmts(fs.Body); return;
                    case ForeachStmt fes: WalkExpr(fes.Collection); WalkStmts(fes.Body); return;
                    case BlockStmt bs: WalkStmts(bs.Statements); return;
                    case UsingStmt us: WalkExpr(us.Initializer); WalkStmt(us.Body); return;
                    case UsingDeclStmt: return; // tree-only, scope-exit disposal isn't a VM frame concept
                    case SwitchStmt sws2:                   // mirrors the SwitchExpr arm above: subject, guards, bodies
                        WalkExpr(sws2.Subject);
                        foreach (var cse in sws2.Cases) { if (cse.Guard != null) WalkExpr(cse.Guard); WalkStmts(cse.Body); }
                        return;
                    case BreakStmt: return;                 // loop control: the VM's own loop opcodes do it
                    case ContinueStmt: return;              // same, and neither carries a sub-expression to walk
                    case DebuggerStmt: return;              // same
                    case ThrowStmt ths: if (ths.Expression != null) WalkExpr(ths.Expression); return;
                    case TryStmt ts:
                        WalkStmt(ts.Body);
                        foreach (var c in ts.Catches) WalkStmt(c.Body);
                        if (ts.Finally != null) WalkStmt(ts.Finally); return;
                    case DestructuringStmt ds: WalkExpr(ds.Source); return;
                    case FuncDecl: return;   // a declaration, not a write
                    default:
                        unknownLocal = true; return;
                }
            }

            void WalkStmts(IReadOnlyList<Stmt>? stmts)
            {
                if (stmts == null || unknownLocal) return;
                foreach (var s in stmts) WalkStmt(s);
            }

            WalkStmts(body);
            if (writtenList.Count > 0) written = [with(writtenList, StringComparer.OrdinalIgnoreCase)];
            if (calledList.Count > 0) called = calledList;
            unknownShape = unknownLocal;
        }

        public static bool ContainsLoopJump(Stmt stmt) => stmt switch
        {
            BreakStmt or ContinueStmt => true,
            BlockStmt b => b.Statements.Any(ContainsLoopJump),
            IfStmt i => ContainsLoopJump(i.ThenBranch[^1]) || (i.ElseBranch.Count > 0 && ContainsLoopJump(i.ElseBranch[^1])),
            WhileStmt w => w.Body.Any(ContainsLoopJump),
            ForStmt f => f.Body.Any(ContainsLoopJump),
            ForeachStmt fe => fe.Body.Any(ContainsLoopJump),
            _ => false
        };

        /// <summary>Deferred jump patches for break/continue inside a loop body. Flow patches are for
        /// THROW_FLOW_BREAK/CONTINUE, whose 2-byte operand is followed by a 1-byte unwind count, so
        /// their relative offsets are measured from (p + BytecodeFormat.ShapeSize(OperandKind.TargetU16UnwindU8)) instead of (p + BytecodeFormat.OperandSize(OperandKind.TargetU16)).</summary>
        public sealed class LoopFrame
        {
            public readonly List<int> BreakPatches = [];
            public readonly List<int> ContinuePatches = [];
            public readonly List<int> FlowBreakPatches = [];
            public readonly List<int> FlowContinuePatches = [];
            public int ContinueTarget = -1;
            /// <summary>True for a switch's frame, a `continue` inside a switch-in-loop must route
            /// past it to the enclosing real loop (the tree treats switch as transparent to continue).</summary>
            public bool IsSwitch;
            /// <summary>Handler-frames active when this loop was pushed, a break/continue only unwinds
            /// handlers entered AFTER the loop (targets inside the loop's enclosing trys keep their
            /// handlers; their finallys run via the normal completion path).</summary>
            public int HandlerFrameCountAtEntry;
        }
        public readonly Stack<LoopFrame> Loops = new();
        public void PatchLoopEnds(LoopFrame frame, int breakTarget)
        {
            foreach (var p in frame.BreakPatches)
                PatchJump(this, p, breakTarget);
            foreach (var p in frame.ContinuePatches)
                PatchJump(this, p, frame.ContinueTarget);
            foreach (var p in frame.FlowBreakPatches)
                PatchUnwindJump(this, p, breakTarget);
            foreach (var p in frame.FlowContinuePatches)
                PatchUnwindJump(this, p, frame.ContinueTarget);
        }

        /// <summary>One try/finally/using region's live VM handler accounting. `Active` mirrors the
        /// runtime handler stack ON THE CODE PATH BEING EMITTED (a catch handler is popped by the
        /// exception unwind before the catch selector runs; the finally handler is popped by every
        /// path before the finally body runs).
        /// </summary>
        public sealed class HandlerFrame
        {
            public int Active;   // handler entries currently on the VM stack on this path
        }
        public readonly Stack<HandlerFrame> HandlerFrames = new();

        /// <summary>Total active handler entries across all open regions (the unwind count a return
        /// escaping the function needs, every pending finally must run).</summary>
        public int TotalActiveHandlers
        {
            get
            {
                var n = 0;
                foreach (var f in HandlerFrames) n += f.Active;
                return n;
            }
        }

        /// <summary>Active handler entries for regions entered after `frameCount` (used for
        /// break/continue: only handlers pushed inside the loop body need flow unwinding).</summary>
        public int ActiveHandlersAfter(int frameCount)
        {
            if (HandlerFrames.Count <= frameCount) return 0;
            var n = 0;
            var top = HandlerFrames.ToArray();   // top-first (innermost first)
            var count = HandlerFrames.Count - frameCount;
            for (var i = 0; i < count; i++) n += top[i].Active;
            return n;
        }

        /// <summary>Converts the set of const local indices into a per-slot bool[] for Bytecode.ConstLocals.</summary>
        internal static bool[] MakeConstFlags(HashSet<int> constIdx, int localCount)
        {
            var flags = new bool[localCount];
            foreach (int i in constIdx.Where(i => i >= 0 && i < localCount)) flags[i] = true;
            return flags;
        }

        public void CompileArgument(CallExpr call, int index)
            => CompileArgument(call.Arguments, call.IsRefArgs, call.IsOutArgs, index);

        /// <summary>Compiles ONE argument of a call: the REF/OUT form when that position is flagged by the call
        /// site (a local becomes a LOAD_REF slot reference, a GLOBAL becomes a cell over it through the engine's
        /// helper, since the VM's own ref encoding names a local slot), and the ordinary value expression otherwise.
        /// The flag is read from the CALL because this runs for every argument, not only the flagged ones: an earlier
        /// version picked the form by the argument alone and turned every global argument of every call into a cell,
        /// which broke a dozen suites at once (measured). It takes the argument list and the two flag lists rather
        /// than a node, because a METHOD call carries the same three things and must emit the same shapes (its arm
        /// declined every ref/out argument outright until it reached this core).</summary>
        public void CompileArgument(IReadOnlyList<Expr> args, IReadOnlyList<bool>? refs, IReadOnlyList<bool>? outs,
            int index)
        {
            var a = args[index];
            var byRef = IsRefOrOutArg(refs, outs, index);
            // Which keyword the CALL SITE wrote: a helper-built cell carries it, so the bind can judge a swapped
            // `ref`/`out` exactly as it does for a local slot (where the VM reads the ArgFlagsMarker) and for a global.
            var cellByOut = outs != null && index < outs.Count && outs[index];
            if (byRef && a is VariableExpr vArg && LocalMap.ContainsKey(vArg.Name))
            {
                Code.Add((byte)Opcode.LOAD_REF);
                Code.Add((byte)LocalIdx(vArg.Name));
                return;
            }
            // A ref/out target that is NOT a local: at the top level every variable is a GLOBAL, and the VM's
            // LOAD_REF names a local SLOT, so the argument travels as a CELL over the global instead, built by the
            // engine's own helper and read/written through by the callee like any other ref param. Measured before
            // this: ONE `Swap(ref x, ref y)` at the top level declined the whole program body (21 suites).
            // Only a name this body ALREADY declares as a global qualifies: a name nothing declares is either an
            // `out var` DECLARATION (which the tree CREATES, so it must keep declining, measured on
            // vm_params 11a/11b) or a typo the tree refuses, and neither has a bytecode form.
            if (byRef && a is VariableExpr vGlobal && !LocalMap.ContainsKey(vGlobal.Name)
                && HasGlobalSlot(vGlobal.Name))
            {
                EmitLoadConst(this, Interpreter.RefGlobalHelperName);
                EmitLoadConst(this, vGlobal.Name);
                EmitLoadConst(this, a.Line);
                EmitLoadConst(this, a.Column);
                Code.AddRange([(byte)Opcode.CALL, Count8(3)]);
                return;
            }
            // A FIELD/PROPERTY or an ELEMENT target travels as a cell too, built by the engine's own resolver over the
            // container this body evaluates: `__ref_field__(container, "name", byOut, line, column)` and
            // `__ref_elem__(container, index, byOut, line, column)`. The keyword travels so a swapped `ref`/`out` is
            // judged by the bind, exactly as the local and global shapes do. Measured before this: the strip refused
            // any body holding `Bump(ref h.F)` or `Slot(ref a[i])`, because the compiler declined the whole body.
            if (byRef && a is PropertyAccessExpr pAccess)
            {
                EmitLoadConst(this, Interpreter.RefFieldHelperName);
                CompileExpr(this, pAccess.Target);
                EmitLoadConst(this, pAccess.PropertyName);
                EmitLoadConst(this, cellByOut);
                EmitLoadConst(this, a.Line);
                EmitLoadConst(this, a.Column);
                Code.AddRange([(byte)Opcode.CALL, Count8(5)]);
                return;
            }
            if (byRef && a is ArrayAccessExpr arrAccess)
            {
                // ONE index only: the engine's element cell names a single index, and a chained access (`a[i][j]`) is
                // an element of an element, which the target resolver would copy for a struct. The declaration is
                // left to the tree in that case (RefOutArgNeedsTree asks this same question).
                EmitLoadConst(this, Interpreter.RefElemHelperName);
                CompileExpr(this, arrAccess.Array);
                CompileExpr(this, arrAccess.Indices[0]);
                EmitLoadConst(this, cellByOut);
                EmitLoadConst(this, a.Line);
                EmitLoadConst(this, a.Column);
                Code.AddRange([(byte)Opcode.CALL, Count8(5)]);
                return;
            }
            CompileExpr(this, a);
        }
        public bool IsBuiltin(string name) => builtinsSet.Contains(name);
        public HashSet<string> Builtins => builtinsSet;
        public List<object?> Consts => consts;
        public List<string> Locals => locals;
        public Dictionary<string, int> LocalMap => localMap;

        /// <summary>Constant folding for a binary op whose two operands were just emitted as two adjacent
        /// LOAD_CONST instructions (together exactly TWO instructions from foldStart).
        ///
        /// The rule set is NOT a table any more: it asks the EXECUTOR (Vm.Arith / Vm.Bitwise / Vm.Compare), so a
        /// folded constant is by construction the value the runtime would have computed. It used to be three
        /// hand-written tables that "mirrored" those methods, and when the shift mask and the non-integral
        /// modulo rule were corrected the mirror was left behind (measured: `1 << 33` folded to 8589934592
        /// typed long, `7.5 % 2` to 1, both against the tree).
        ///
        /// Two deliberate non-folds, both to PRESERVE a runtime error rather than delete it: a zero divisor
        /// (the executor throws "Division/Modulo by zero.") and an integral overflow (the runtime wraps, as
        /// today, so there is nothing to preserve and the fold is skipped as before). Mixed kinds never fold,
        /// and a double pair is folded only for ADD/SUB/MUL/DIV. Measured by bench_vm_ab's constant-folding
        /// section, which puts MOD and the bitwise/shift family inside a hot loop precisely because those were
        /// the uncovered forms.</summary>
        public bool TryFoldLastTwoConsts(int foldStart, Opcode op, out object? folded)
        {
            folded = null;
            // Two adjacent LOAD_CONST instructions, each an opcode byte plus its CONST operand, so their
            // combined span and each one's const come from the SHAPE. Written as a literal 4 with a one-byte
            // read, this guard rejected EVERY fold once the const operand was widened, silently: the span
            // became 6, no pair matched, and a chain of literals stopped folding (which is also what keeps
            // the pool from growing a distinct entry per step).
            var span = BytecodeFormat.InstructionSize(OperandKind.ConstIdx);
            int ConstOf(int opcodePos) => BytecodeFormat.ReadConstIdx(Code, opcodePos + BytecodeFormat.OpcodeWidthBytes);
            var n = Code.Count;
            if (n - foldStart != 2 * span) return false;
            if (Code[foldStart] != (byte)Opcode.LOAD_CONST || Code[foldStart + span] != (byte)Opcode.LOAD_CONST) return false;
            var c1 = Consts[ConstOf(foldStart)];
            var c2 = Consts[ConstOf(foldStart + span)];
            // FOLD THROUGH THE EXECUTOR: the VM's own operation decides, so a folded constant cannot differ
            // from the value the runtime would compute. It used to differ, twice: the tables this replaces
            // mirrored Vm.Arith and Vm.Bitwise by hand, and when the shift mask and the modulo rule were
            // corrected the mirror was left behind (`1 << 33` folded to 8589934592 typed long, `7.5 % 2` to 1).
            // A zero divisor still DECLINES the fold, because the runtime raises there and folding it away
            // would delete a catchable error.
            if (op is Opcode.LT or Opcode.GT or Opcode.EQ or Opcode.LE or Opcode.GE or Opcode.NE)
            {
                // Integral comparisons only: a double EQ/NE routes through Interpreter.AreEqual in the
                // executor (NaN and ±0), so it is not the same question and stays unfolded.
                if (!AsIntegral(c1, out _) || !AsIntegral(c2, out _)) return false;
                folded = Vm.Compare(op, VmValue.From(c1), VmValue.From(c2));
                return true;
            }
            var bothIntegral = AsIntegral(c1, out _) && AsIntegral(c2, out _);
            if (!bothIntegral && !(c1 is double && c2 is double)) return false;
            try
            {
                var a = VmValue.From(c1);
                var b = VmValue.From(c2);
                var value = op is Opcode.ADD or Opcode.SUB or Opcode.MUL or Opcode.DIV
                    ? Vm.Arith(op, a, b, 0, 0)
                    : Vm.Bitwise(op, a, b, 0, 0);
                folded = value.ToObject();
                return true;
            }
            catch (ScriptException) { return false; }   // a zero divisor: the runtime raises it, keep that
        }

        private static bool AsIntegral(object? c, out long v)
        {
            switch (c)
            {
                case long l: v = l; return true;
                case int i: v = i; return true;
                case short s: v = s; return true;
                case byte b: v = b; return true;
                case sbyte sb: v = sb; return true;
                case ushort us: v = us; return true;
                case uint ui: v = ui; return true;
                default: v = 0; return false;
            }
        }

        private static readonly object NullSentinel = new();
    }

    /// <summary>The ONE place a context becomes a <see cref="Bytecode"/>, so a section added to a body (the
    /// bare calls, the nested declarations) cannot reach five of the six compile funnels and miss one: they all
    /// end here.</summary>
    static Bytecode Seal(Ctx ctx, List<byte> code, List<object?> consts, List<string> locals)
        => new([.. code], [.. consts], [.. locals],
            ctx.NestedLambdas.Count > 0 ? [.. ctx.NestedLambdas] : null,
            ctx.ConstLocals.Count > 0 ? Ctx.MakeConstFlags(ctx.ConstLocals, locals.Count) : null,
            ctx.GlobalSlotNames, ctx.BareCalls, ctx.NestedDecls);

    /// <summary>Compiles a statement list, wrapping it in a TRY_FINALLY when it contains
    /// `using var` declarations so they dispose at scope exit (reverse order, incl. early
    /// return/break/continue/exception, the VM unwinds handler frames and runs the finally).
    /// Nested statement lists (blocks, if branches, loop bodies) compile through this same
    /// helper, so each scope disposes exactly its own using-decls (tree parity with
    /// Scope.RegisterDisposeOnExit). Zero overhead when the list has no using-decls.</summary>
    static void CompileStmtList(Ctx ctx, IReadOnlyList<Stmt> stmts)
    {
        // SAVED and restored in a finally: this method returns early for many statement kinds, and a bare
        // ++/-- left the depth raised after the first such return, which quietly pushed later TOP-LEVEL
        // declarations onto the local path (measured: a source-less designer answered "Undefined variable
        // toolbox", because its program body stored its globals as locals).
        var savedDepth = ctx.BodyDepth;
        ctx.BodyDepth = savedDepth + 1;
        // THE NESTED DECLARATIONS THIS LIST DECLARES GET THEIR SLOT BEFORE ANY STATEMENT IS COMPILED, so a closure written
        // above one captures that slot and the declaration fills it when it runs: that is what makes C#'s rule (a local
        // function is in scope for the whole block) work on the COMPILED path too, not only on the tree's. The map is a
        // LAYER over the enclosing list's (a declaration inside an inner block is only in scope there), and an entry
        // still present when a nested declaration compiles is a sibling it has not reached, which declines the body.
        // ONLY A DECLARATION THAT TAKES A LOCAL SLOT COUNTS: the program body's OWN list (depth 0, which CompileProgram
        // enters from -1) declares GLOBALS, registered before the body runs and resolved live, so a lambda capturing one
        // needs no slot at all.
        var savedPending = ctx.PendingDeclSlots;
        ctx.PendingDeclSlots = new Dictionary<string, int>(savedPending ?? [],
            ctx.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        PreallocateDeclSlots(ctx, stmts);
        try
        {
            var usingLocal = -1;
            foreach (var s in stmts)
                // A PROGRAM BODY's own top level is the one scope the body does NOT leave: its using-decls are
                // released when the RUN ends, through Scope.RunDisposeOnExit (the UsingDeclStmt arm below registers
                // them), so this wrapper must not claim them. That list is BodyDepth 0 (the convention the
                // VarDeclStmt arm states), and a NESTED list is 1 or more and keeps the wrapper, which is what
                // makes a `using var` inside a bare block dispose at the block's exit.
                if (s is UsingDeclStmt && !(ctx.ProgramBody && ctx.BodyDepth == 0)) { usingLocal = 0; break; }
            if (usingLocal < 0)
            {
                foreach (var s in stmts) CompileStmt(ctx, s);
                return;
            }

            // Scope-exit disposal wrapper: TRY_FINALLY ... POP_FINALLY; JMP finally; finally: dispose.
            ctx.HandlerFrames.Push(new Ctx.HandlerFrame()); ctx.HandlerFrames.Peek().Active = 1;
            ctx.UsingLocals.Push([]);
            ctx.Code.Add((byte)Opcode.TRY_FINALLY);
            var usingFin = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            foreach (var s in stmts) CompileStmt(ctx, s);
            ctx.Code.Add((byte)Opcode.POP_FINALLY);
            ctx.Code.Add((byte)Opcode.JMP);
            var usingJmp = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            PatchJump(ctx, usingFin, ctx.Code.Count);
            PatchJump(ctx, usingJmp, ctx.Code.Count);
            var scopeLocals = ctx.UsingLocals.Pop();
            for (var i = scopeLocals.Count - 1; i >= 0; i--)
            {
                ctx.Code.Add((byte)Opcode.LOAD_VAR); ctx.Code.Add((byte)scopeLocals[i]);
                ctx.Code.Add((byte)Opcode.DISPOSE);
            }
            ctx.Code.Add((byte)Opcode.END_FINALLY);
            ctx.Code.Add(0); ctx.Code.Add(0);
            ctx.HandlerFrames.Peek().Active = 0;
            ctx.HandlerFrames.Pop();
        }
        finally
        {
            ctx.BodyDepth = savedDepth;
            ctx.PendingDeclSlots = savedPending;
        }
    }

    /// <summary>True when THIS body takes a nested declaration into a LOCAL SLOT. One predicate, asked by the declaration
    /// arm and by the pre-allocation, so a slot is never reserved for a declaration the arm would not compile.</summary>
    static bool SupportsNestedDecls(Ctx ctx) =>
        ctx.ProgramBody ? ctx.BodyDepth > 0 : (ctx.IsLambda || ctx.AllowNestedDecls);

    /// <summary>Reserves a LOCAL SLOT for every function this statement list declares directly, before any statement is
    /// compiled, and records it in PendingDeclSlots until the declaration runs. That is what lets a closure written ABOVE
    /// the declaration capture the name on the COMPILED path: it captures the SLOT as a cell (the lambda arm marks such a
    /// capture as written) and the declaration's store fills that cell, exactly as the tree binds the name at the body's
    /// start and fills it at the declaration (Interpreter.PrebindDeclaredFunctions).</summary>
    static void PreallocateDeclSlots(Ctx ctx, IReadOnlyList<Stmt> stmts)
    {
        if (!SupportsNestedDecls(ctx)) return;
        foreach (var s in stmts)
        {
            if (s is not FuncDecl { TypeParameters.Count: 0 } fd || fd.Name is not { Length: > 0 }) continue;
            if (ctx.LocalMap.ContainsKey(fd.Name)) continue;
            var slot = ctx.Locals.Count;
            ctx.LocalMap[fd.Name] = slot;
            ctx.Locals.Add(fd.Name);
            (ctx.PendingDeclSlots ??= new Dictionary<string, int>(
                ctx.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase))[fd.Name] = slot;
        }
    }

    /// <summary>True when a declaration of this name has its slot reserved but has not compiled yet, i.e. the name is in
    /// scope but the function does not exist until the declaration runs.</summary>
    static bool IsPendingDecl(Ctx ctx, string name) =>
        ctx.PendingDeclSlots != null && ctx.PendingDeclSlots.ContainsKey(name);

    /// <summary>True when the call passes any ref/out argument (those need RefCell wiring that
    /// FOREACH_CALL does not model, such calls keep the CALL + FOREACH_INIT path).</summary>
    /// <summary>The bare-name global callees, keyed by name, so the gate below can read a CALLEE's declaration.
    /// ONE builder, so every caller (the interpreter from its global scope, `Program.cs` from the top-level
    /// declarations) hands the compiler the same map.</summary>
    internal static Dictionary<string, FuncDecl[]>? BuildCalleeDecls(IEnumerable<FuncDecl> decls, bool caseSensitive)
    {
        Dictionary<string, List<FuncDecl>>? collecting = null;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        // EVERY declaration of a name, not just the last: a free function is an OVERLOAD SET here, so a question
        // asked of "the callee's declaration" (the ref/out flag check below) must be asked of the whole set, or a
        // call that one member can honour would be declined on the strength of another one.
        foreach (var d in decls)
        {
            if (collecting == null) collecting = new Dictionary<string, List<FuncDecl>>(comparer);
            if (!collecting.TryGetValue(d.Name, out var list)) collecting[d.Name] = list = [with(2)];
            list.Add(d);
        }
        if (collecting == null) return null;
        var map = new Dictionary<string, FuncDecl[]>(collecting.Count, comparer);
        foreach (var kv in collecting) map[kv.Key] = kv.Value.ToArray();
        return map;
    }

    /// <summary>True when NO declaration of the callee can honour the call's `ref`/`out` flags: a flag where the
    /// parameter is not declared that way (C# CS1615/CS1620), which includes every flag at or past the fixed
    /// parameters, where a `params` tail would have to absorb it (see AssembleParamsTail). Then the body must run on
    /// the tree: it still has the call site's flags, and BindParam refuses against the declaration in ONE place for
    /// every call shape.
    ///
    /// IT ASKS THE WHOLE SET (every declaration of the name), which is why a call to an OVERLOADED name with a
    /// `ref`/`out` argument used to be declined even though one member accepts it: the check used to read the ONE
    /// declaration the map kept (the last one written), so `Bump(ref n)` against `Bump(ref int)`/`Bump(int, int)`
    /// saw `(int, int)`, found no by-reference position, and gave the whole program body to the tree (measured: a
    /// `--source=strip` bundle then refused the file by name).</summary>
    static bool RefOutArgUnhonourable(CallExpr ce, IReadOnlyList<FuncDecl> callees)
    {
        foreach (var callee in callees)
            if (!RefOutArgMismatch(ce, callee)) return false;   // one member takes the call as it is written
        return true;
    }

    /// <summary>True when THIS declaration contradicts the call's `ref`/`out` flags (the per-member half of
    /// <see cref="RefOutArgUnhonourable"/>).</summary>
    static bool RefOutArgMismatch(CallExpr ce, FuncDecl callee)
    {
        if (ce.IsRefArgs != null)
            for (var i = 0; i < ce.IsRefArgs.Count; i++)
                if (ce.IsRefArgs[i] && !(i < callee.IsRefParams.Count && callee.IsRefParams[i])) return true;
        if (ce.IsOutArgs != null)
            for (var i = 0; i < ce.IsOutArgs.Count; i++)
                if (ce.IsOutArgs[i]
                    && !(callee.IsOutParams != null && i < callee.IsOutParams.Count && callee.IsOutParams[i])) return true;
        return false;
    }

    /// <summary>Whether a call site flagged this argument `ref` or `out`, for EITHER call shape (a function call and
    /// a method call carry the same pair of lists). ONE reader, because the flag decides two things that must agree:
    /// what the compiler EMITS for the argument (a slot reference or a cell over a global) and whether the body can
    /// be compiled at all (see RefOutArgNeedsTree).</summary>
    static bool IsRefOrOutArg(IReadOnlyList<bool>? refs, IReadOnlyList<bool>? outs, int i)
        => (refs != null && i < refs.Count && refs[i]) || (outs != null && i < outs.Count && outs[i]);

    static bool IsRefOrOutArg(CallExpr ce, int i) => IsRefOrOutArg(ce.IsRefArgs, ce.IsOutArgs, i);

    static bool IsRefOrOutArg(MethodCallExpr mce, int i) => IsRefOrOutArg(mce.IsRefArgs, mce.IsOutArgs, i);

    /// <summary>True when a ref/out argument has no bytecode form: a DISCARD (`out _`), or an inline DECLARATION
    /// (`out var c`), which the parser builds as a plain variable expression. The tree CREATES such a variable when
    /// its capture finds none, and the VM would instead compile a read of a name it never declared: measured,
    /// `SetVal(out var c)` returned 42 on the tree and refused with "Undefined variable 'c'" as soon as the body
    /// was compiled, and `SetVal(out _)` behaved the same way. An argument that names an EXISTING local keeps
    /// compiling, because that is the shape the VM's CALL wraps in a cell for a declared ref/out parameter.
    /// A METHOD call carries the same three things, which is why its arm now asks this instead of declining every
    /// ref/out argument outright (`Interlocked.Increment(ref counter)`, `c3.Bad(out v3)`).</summary>
    static bool RefOutArgNeedsTree(CallExpr ce, Ctx ctx)
        => RefOutArgNeedsTree(ce.Arguments, ce.IsRefArgs, ce.IsOutArgs, ctx);

    static bool RefOutArgNeedsTree(MethodCallExpr mce, Ctx ctx)
        => RefOutArgNeedsTree(mce.Arguments, mce.IsRefArgs, mce.IsOutArgs, ctx);

    static bool RefOutArgNeedsTree(IReadOnlyList<Expr> args, IReadOnlyList<bool>? refs, IReadOnlyList<bool>? outs,
        Ctx ctx)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (!IsRefOrOutArg(refs, outs, i)) continue;
            if (args[i] is DiscardExpr) return true;
            // A FIELD/PROPERTY travels as a cell through `__ref_field__`, and an ELEMENT through `__ref_elem__`, both
            // built by the engine's own target resolver, so neither declines any more. A VARIABLE that is not a local
            // is a GLOBAL at the top level, and it travels as a cell too (see Ctx.CompileArgument), so the
            // `!ctx.IsLocal` clause that used to decline it is gone: it was what made 21 suites carry their source for
            // one `Swap(ref x, ref y)`.
            if (args[i] is PropertyAccessExpr) continue;
            // An ELEMENT target compiles only for ONE plain index: the helper names a single index, and a chained
            // access (`a[i][j]`) is an element of an element, which the resolver would hand back a COPY of for a struct.
            if (args[i] is ArrayAccessExpr arrRef)
            {
                if (arrRef.Indices.Count != 1 || arrRef.Indices[0] is RangeExpr) return true;
                continue;
            }
            if (args[i] is not VariableExpr vRef) return true;
            // A name this body never declares is an `out var` DECLARATION (the tree creates it, so the body must
            // stay on the tree: measured on vm_params 11a/11b) or a typo the tree refuses. Asking about the slot
            // gives the same answer CompileArgument uses to decide what to emit, which is why both read it there.
            if (!ctx.IsLocal(vRef.Name) && !ctx.HasGlobalSlot(vRef.Name)) return true;
        }
        return false;
    }

    /// <summary>True when a declaration's initializer is a `ref &lt;name&gt;` over a LOCAL slot, which a compiled body
    /// cannot express: the parser desugars `ref x` into a call to the engine's `__ref_name__`, and only the compiler
    /// knows whether `x` is a slot or a global at that point.</summary>
    /// <summary>A REF-RETURNING shape the VM has no lvalue use for: a ref local bound to a CALL (`ref int r = ref
    /// F(x);`) and an assignment THROUGH a call (`F(x) = 7;`). Both need the REFERENCE the callee answered, while the
    /// VM dereferences a call result (a body it runs has no lvalue consumer), so the tree owns them.
    /// </summary>

    /// <summary>True for the three helpers a `ref <target>` desugars to. Each ANSWERS a cell, and a compiled body has a
    /// consumer for one now: a ref local stores the cell (the VM replaces the slot's cell rather than nesting two) and a
    /// ref argument hands it on. A ref-RETURNING call is the different shape, and it is what declines a body.</summary>
    /// <summary>Marks a call whose result is used as a REFERENCE (`F(..) = 7;`, `ref int r = ref F(..);`). Emitted at the
    /// BOTTOM of the call layout, where the other markers go: the VM's arms pop the callee and its arguments, so the
    /// marker is the stack top, and a call carrying it is never inlined and asks the callee for its CELL instead of a
    /// value. Nothing is emitted for an ordinary call, so no existing body moves.</summary>
    static void EmitRefResultMarker(Ctx ctx, CallExpr call)
    {
        if (call.WantsRefResult) EmitLoadConst(ctx, RefResultMarker.Instance);
    }
    /// <summary>True when every declaration a bare callee name can resolve to is a REF RETURN, which is the only callee
    /// shape whose cell a `ref int r = ref F(..);` can bind to. A local or expression callee is unknown here, so it stays
    /// the tree's, where its own declaration check refuses a call that answered a value.</summary>
    static bool IsProvenRefReturnCallee(Ctx ctx, CallExpr call)
    {
        if (call.Callee is not VariableExpr callee) return false;
        if (ctx.CalleeDecls == null || !ctx.CalleeDecls.TryGetValue(callee.Name, out var set)) return false;
        var any = false;
        foreach (var m in set) { any = true; if (!m.IsRefReturn) return false; }
        return any;
    }

    static bool IsRefHelperCall(CallExpr call)
        => call.Callee is VariableExpr callee
           && (callee.Name == Interpreter.RefNameHelperName
               || callee.Name == Interpreter.RefFieldHelperName
               || callee.Name == Interpreter.RefElemHelperName);

    static bool IsRefReturnShape(Stmt s) => s switch
    {
        BlockStmt b => b.Statements.Any(IsRefReturnShape),
        ExprStmt es => es.Expression is RefAssignExpr,
        IfStmt i => i.ThenBranch.Any(IsRefReturnShape) || i.ElseBranch.Any(IsRefReturnShape),
        WhileStmt w => w.Body.Any(IsRefReturnShape),
        ForStmt f => f.Body.Any(IsRefReturnShape),
        ForeachStmt fe => fe.Body.Any(IsRefReturnShape),
        TryStmt t => IsRefReturnShape(t.Body) || t.Catches.Any(c => IsRefReturnShape(c.Body))
                    || (t.Finally != null && IsRefReturnShape(t.Finally)),
        _ => false,
    };


    static bool HasRefOrOutArgs(CallExpr ce)
    {
        if (ce.IsRefArgs != null)
            foreach (var r in ce.IsRefArgs) if (r) return true;
        if (ce.IsOutArgs != null)
            foreach (var o in ce.IsOutArgs) if (o) return true;
        return false;
    }

    /// <summary>Pushes the ARG-FLAGS marker under a call that passes arguments by ref/out, so the VM can tell
    /// which ones they are: its own argument marker (K=4) only says "came from a local slot", which a plain local
    /// argument pushes as well. Emitted only for a call the compiler KEEPS, a named-arg call with a ref argument
    /// already declines, so the two markers never meet on one call. Nothing is emitted for an ordinary call.</summary>
    static void EmitArgFlags(Ctx ctx, CallExpr ce)
        => EmitArgFlags(ctx, ce.Arguments.Count, ce.IsRefArgs, ce.IsOutArgs);

    /// <summary>The same marker for a METHOD call, which is the only other place a CALL SITE's keyword can travel:
    /// the bytecode says `LOAD_REF` for a local and nothing about which of the two keywords the author wrote, while
    /// the bind judges a SWAP (`ref` at an `out` parameter) by exactly that. It takes the pieces rather than a node
    /// because a method call is not a `CallExpr`.</summary>
    static void EmitArgFlags(Ctx ctx, int argCount, IReadOnlyList<bool>? refs, IReadOnlyList<bool>? outs)
    {
        var anyRef = refs != null && refs.Any(b => b);
        var anyOut = outs != null && outs.Any(b => b);
        if (!anyRef && !anyOut) return;
        var refFlags = new bool[argCount];
        var outFlags = new bool[argCount];
        for (var i = 0; i < argCount; i++)
        {
            refFlags[i] = refs != null && i < refs.Count && refs[i];
            outFlags[i] = outs != null && i < outs.Count && outs[i];
        }
        EmitLoadConst(ctx, new ArgFlagsMarker(refFlags, outFlags));
    }

    /// <summary>True when the function DECLARES any ref/out parameter. FOREACH_CALL binds arguments
    /// positionally with no RefCell wiring, so such coroutines must keep the CALL + FOREACH_INIT
    /// path, mirrors the flat CALL gate's `!hasRef` requirement.</summary>
    static bool HasRefOrOutParams(FuncDecl d)
        => d.IsRefParams is { Count: > 0 } && d.IsRefParams.Contains(true)
           || d.IsOutParams is { Count: > 0 } && d.IsOutParams.Contains(true);

    /// <summary>True when a bare-name callee inside a method context resolves through the CLASS
    /// (instance method dispatch or a static method on the class), the CALL_METHOD path owns those;
    /// FOREACH_CALL only handles plain global bare-name coroutines.</summary>
    /// <summary>True for a bare-name callee that names one of the ENCLOSING CLASS's instance methods. The
    /// tree resolves it via FindMethodInHierarchy on _currentClass; the VM dispatches the same call as
    /// `this.Name(...)` through CALL_METHOD. This is the twin of the StaticMethodNames branch below, and
    /// it replaced a `FieldNames is { Count: > 0 }` proxy that failed for a class with NO fields (every one
    /// of the designer's fifteen control kinds): the call was emitted as a plain CALL, the VM could not
    /// resolve it, and a source-less bundle has no tree to fall back to, so a form built from one threw
    /// "Method 'FactoryName' is bytecode-only and cannot run on the tree walker".</summary>
    static bool IsInstanceMethodContextBareCall(Ctx ctx, string name)
        => (ctx.IsMethod || ctx.IsLambda) && !ctx.IsStaticMethod
           && ctx.InstanceMethodNames is { Count: > 0 } && ctx.InstanceMethodNames.Contains(name)
           && !ctx.IsBuiltin(name);

    static bool IsMethodContextBareCall(Ctx ctx, string name)
        => IsInstanceMethodContextBareCall(ctx, name)
        || (ctx.IsMethod || ctx.IsLambda) && ctx.ClassName != null
              && ctx.StaticMethodNames is { Count: > 0 } && ctx.StaticMethodNames.Contains(name);

    /// <summary>Conservative side-effect-freedom check for a FOREACH_CALL argument. The FOREACH_CALL
    /// loop head re-evaluates the callee-name const and the args on EVERY iteration (so a re-entered
    /// loop re-drives fresh), so only pure expressions may be repeated there, anything that can
    /// observe or mutate state (calls, index/property access, increments, new) keeps the old path.</summary>
    static bool IsPureForeachCallArg(Expr e) => e switch
    {
        NumberExpr or StringExpr or BoolExpr or NullExpr => true,
        VariableExpr => true,
        BinaryExpr be => IsPureForeachCallArg(be.Left) && IsPureForeachCallArg(be.Right),
        UnaryExpr ue => ue.Operator.Type == TokenType.Minus && IsPureForeachCallArg(ue.Right),
        TupleExpr tu => tu.Items.All(IsPureForeachCallArg),
        _ => false
    };

    /// <summary>True when a type argument is, or contains, a TYPE PARAMETER. Such an argument is only
    /// meaningful once an instantiation binds it, and the binding lives where the tree closes the value
    /// (a method clone, a class instance), so the compiler leaves those instantiations to the tree.
    /// Nested arguments are walked too: List&lt;T&gt; and Dictionary&lt;string, T&gt; are the same question.</summary>
    private static bool MentionsTypeParameter(TypeInfo ti)
    {
        if (ti == null) return false;
        if (ti.IsGenericParameter) return true;
        if (ti.GenericArgs is { Count: > 0 })
            foreach (var a in ti.GenericArgs)
                if (a != null && MentionsTypeParameter(a)) return true;
        if (ti.TupleElementTypes is { Count: > 0 })
            foreach (var a in ti.TupleElementTypes)
                if (a != null && MentionsTypeParameter(a)) return true;
        return false;
    }

    static void CompileStmt(Ctx ctx, Stmt stmt)
    {
        ctx.CurrentStatement = stmt.GetType().Name + " at line " + stmt.Line;
        // A PROGRAM BODY (Compiler.CompileProgram) has nothing to do for a top-level DECLARATION: the interpreter
        // registers functions and types before the body runs, a bundle carries its functions in its own table and
        // its loader registers them, and the body compiled here is the CODE around them. Without this the switch's
        // default arm declines every real script, because every real script declares something.
        // A namespace block, an import or any other statement the compiler does not model still declines the body,
        // which the bundle writer reports rather than writing an entry that would run a different program.
        // One predicate, in one place: the bundle load asks the same question of the same statements.
        if (stmt is FuncDecl declStmt2 && declStmt2.Name is { Length: > 0 }
            && (SupportsNestedDecls(ctx) || IsPendingDecl(ctx, declStmt2.Name))
            && (IsPendingDecl(ctx, declStmt2.Name) || !ctx.LocalMap.ContainsKey(declStmt2.Name)))
        {
            {
                var nestedDecl = declStmt2;
                (ctx.NestedDecls ??= []).Add(nestedDecl);
                // THE SLOT WAS RESERVED WHEN THE LIST STARTED (PreallocateDeclSlots), so an earlier closure already captured
                // it; only a declaration the pre-allocation skipped (a name that is already a local, or a list it did not
                // scan) reserves one here.
                var pendingSlot = ctx.PendingDeclSlots != null && ctx.PendingDeclSlots.Remove(nestedDecl.Name);
                int nestedSlot;
                if (!pendingSlot || !ctx.LocalMap.TryGetValue(nestedDecl.Name, out nestedSlot))
                {
                    nestedSlot = ctx.Locals.Count;
                    ctx.LocalMap[nestedDecl.Name] = nestedSlot;
                    ctx.Locals.Add(nestedDecl.Name);
                }
                // A compiled block has NO scope dictionary, so the function's closure cannot be looked up: the captured
                // locals are PASSED as (name, value) pairs (measured: a snapshot lookup read `snapLocals=0`, and the
                // body's block local arrived null). A name that is not a local here is a global and stays a live
                // reference in the body, the same rule a lambda follows.
                var passed = new List<string>();
                nestedDecl.CapturedNames ??= FreeVariableAnalyzer.Analyze(nestedDecl);
                if (nestedDecl.CapturedNames != null)
                    foreach (var cn in nestedDecl.CapturedNames)
                    {
                        // A SIBLING DECLARATION THIS ONE HAS NOT REACHED has a slot but no value yet, and a value pair is
                        // what this statement hands over (a cell cannot travel through LOAD_VAR, which dereferences one),
                        // so the body declines and the tree runs it with the name bound from the body's start.
                        if (IsPendingDecl(ctx, cn)) { ctx.Success = false; return; }
                        if (!string.Equals(cn, "this", StringComparison.Ordinal) && ctx.LocalMap.ContainsKey(cn))
                            passed.Add(cn);
                    }
                // THE BODY IS COMPILED HERE, with exactly the names this statement passes, so the layout it reads and
                // the cells it is handed agree BY CONSTRUCTION: compiling at precompile time misclassified a top-level
                // variable as an enclosing local (nothing has run yet), and the bundle then read null (measured on
                // vm_nosource_test check 31).
                // RECOMPILE when the body exists but its capture LAYOUT is unknown: a body compiled elsewhere (by
                // `Program.PrecompileFunctions`, which cannot know this statement's local map) was compiled with a
                // DIFFERENT capture list, and a body reads fixed slots. Measured: crash_360_latest's two sibling nested
                // functions ran into each other until the depth guard fired, because a stale body met a fresh layout.
                if (nestedDecl.Compiled == null || nestedDecl.VmCaptureNames == null)
                {
                    nestedDecl.Compiled = Compile(nestedDecl, ctx.Builtins, ctx.CaseSensitive,
                        capturedLocals: passed, calleeDecls: ctx.CalleeDecls,
                        allowNestedDecls: ctx.IsLambda || ctx.AllowNestedDecls);
                    nestedDecl.VmCaptureNames = passed.Count > 0 ? passed : null;   // only where WE compiled it
                }
                if (nestedDecl.Compiled?.NestedDecls is { Count: > 0 } deeper) (ctx.NestedDecls ??= []).AddRange(deeper);
                EmitLoadConst(ctx, Interpreter.NestedFunctionHelperName);
                EmitLoadConst(ctx, nestedDecl.Name);
                // (line, column) FIRST: a name is not unique across bodies, so the helper picks THIS declaration by its
                // position (Interpreter.NestedFunctionDeclsAt), and the capture pairs follow.
                EmitLoadConst(ctx, (long)nestedDecl.Line);
                EmitLoadConst(ctx, (long)nestedDecl.Column);
                foreach (var cn in passed)
                {
                    EmitLoadConst(ctx, cn);
                    EmitLoadVar(ctx, ctx.LocalMap[cn]);
                }
                ctx.Code.Add((byte)Opcode.CALL);
                ctx.Code.EmitU16(3 + passed.Count * 2);
                ctx.Code.Add((byte)Opcode.STORE_VAR);
                ctx.Code.EmitLocalIdx(nestedSlot);
            }
            return;
        }
        if (ctx.ProgramBody && Interpreter.IsDeclarationStatement(stmt))
            return;
        // Emit source location for error reporting
        EmitLoc(ctx, stmt.Line, stmt.Column);

        switch (stmt)
        {
            case ExprStmt es:
                CompileExprStmt(ctx, es);
                break;
            case VarDeclStmt vd:
                CompileVarDeclStmt(ctx, vd);
                break;
            case ReturnStmt rs:
                CompileReturnStmt(ctx, rs);
                break;
            case IfStmt ifs:
                CompileIfStmt(ctx, ifs);
                break;
            case WhileStmt ws:
                CompileWhileStmt(ctx, ws);
                break;
            case ForeachStmt fe:
                CompileForeachStmt(ctx, fe);
                break;
            case BlockStmt bs:
                CompileBlockStmt(ctx, bs);
                break;
            case ForStmt fs:
                CompileForStmt(ctx, fs);
                break;
            case ThrowStmt ts:
                CompileThrowStmt(ctx, ts);
                break;
            case TryStmt tryStmt:
                CompileTryStmt(ctx, tryStmt);
                break;
            case UsingDeclStmt uds:
                CompileUsingDeclStmt(ctx, uds);
                break;
            case ImportStmt im when ctx.ProgramBody && ctx.BodyDepth == 0:
                CompileImportStmt(ctx, im);
                break;
            case UsingStmt us:
                CompileUsingStmt(ctx, us);
                break;
            case DestructuringStmt ds:
                CompileDestructuringStmt(ctx, ds);
                break;
            case BreakStmt:
                CompileBreakStmt(ctx);
                break;
            case ContinueStmt:
                CompileContinueStmt(ctx);
                break;
            case SwitchStmt sws:
                CompileSwitchStmt(ctx, sws);
                break;
            case NamespaceDeclStmt nsd:
                CompileNamespaceDeclStmt(ctx, nsd);
                break;
            default:
                ctx.Success = false;
                ctx.DeclineReason ??= $"while compiling a {stmt.GetType().Name}: it has no bytecode form";
                return;
        }
    }
    // The cases of CompileStmt's switch, one method per kind, in the order the
    // switch dispatched them. Each body is the case body verbatim: the arm-level `break` became a `return`,
    // which is the same exit (the switch sits at the end of its method, inside the try whose finally restores
    // the depth counter).
    private static void CompileExprStmt(Ctx ctx, ExprStmt es)
    {
        if (es.Expression is YieldExpr stYield)
        {
            // Bare `yield expr;`, STATEMENT position: emit YIELD directly (consumes the
            // value). Expression-position yields (var x = yield, f(yield), arr[yield i])
            // are TREE-ONLY, the VM has no resume-value injection, so they compile as
            // ctx.Success = false and the coroutine stays tree (Compiled == null).
            if (stYield.Value != null) CompileExpr(ctx, stYield.Value);
            else EmitLoadConst(ctx, null);
            ctx.Code.Add((byte)Opcode.YIELD);
            return;
        }
        CompileExpr(ctx, es.Expression);
        // ALWAYS POP, and the invariant behind it is worth stating because a LIST here was a defect generator: every
        // expression the compiler emits leaves EXACTLY ONE operand (a store leaves its DUP'd copy, a call leaves
        // its result, an increment leaves its new value, `await t;` leaves the awaited value, `F(..) = 7;` leaves
        // `__ref_assign__`'s value). The rule used to enumerate the kinds that "leave a value", and the original
        // version of it asked only for a LOCAL assign or a field, on the belief that a global store balanced to
        // zero; it does not. Measured consequences of getting it wrong, both from that list: `int C(int by) {
        // gg = gg + by; return gg; }` left an operand, so `var r = 1 + C(5);` answered 10 instead of 6, and a
        // function whose body was `Slot() = 12; return g;` handed a FLAT caller two operands, so the caller's
        // next CALL mis-read its callee and a stripped bundle bailed with "stopped part-way through its compiled
        // body". A list has to be extended for every new expression kind (RefAssignExpr was missed); an
        // unconditional POP cannot be forgotten, and `tools/dump-bytecode.sh` proves it over the whole corpus
        // because the verifier checks each body's operand balance (a statement that underflowed would unbalance
        // the body and be REFUSED).
        ctx.Code.Emit(Opcode.POP);
        return;
    }

    private static void CompileVarDeclStmt(Ctx ctx, VarDeclStmt vd)
    {
        // A REF LOCAL OVER A LOCAL VARIABLE is an ALIAS, which is exactly C#'s semantics: `ref int r = ref x;`
        // makes r the SAME storage as x, so the name maps to x's SLOT and NOTHING is emitted (no cell, no
        // RENEW_LOCAL, no store, and no declared type, which belongs to the target). A cell here would need the
        // caller's slot address, which the VM's ref encoding has only for ARGUMENTS (`LOAD_REF <slot>`).
        if (vd.Initializer is CallExpr { Callee: VariableExpr { Name: Interpreter.RefNameHelperName } } aliasDecl
            && aliasDecl.Arguments is { Count: > 0 } aliasArgs
            && aliasArgs[0] is StringExpr aliasTarget
            && ctx.IsLocal(aliasTarget.Value))
        {
            ctx.LocalMap[vd.Name] = ctx.LocalMap[aliasTarget.Value];
            return;
        }

        // `ref int r = ref F(..);` binds the LOCAL to the cell F answers, so it is only meaningful when F IS a ref
        // return. The tree refuses the other shape by name at the declaration, and a compiled body would silently
        // bind the VALUE it got instead, so the body is declined whenever the callee's declarations do not prove
        // it (every member a ref return, for a bare name; anything else is unknown at compile time).
        if (vd.Initializer is CallExpr { WantsRefResult: true } refInit
            && !IsRefHelperCall(refInit)
            && !IsProvenRefReturnCallee(ctx, refInit))
        {
            ctx.Success = false;
            return;
        }
        // A declaration BELOW the top level is a new BINDING every time it executes (the tree makes a
        // fresh VariableInfo), so a lambda capturing it must not share the slot cell with a closure
        // made by an earlier execution. Emitted BEFORE the initializer, so the initializer's own
        // reads see the new binding too. A const stays a value copy and needs no cell.
        if (ctx.BodyDepth > 0 && !vd.IsConst)
        {
            if (!ctx.LocalMap.ContainsKey(vd.Name)) { ctx.LocalMap[vd.Name] = ctx.Locals.Count; ctx.Locals.Add(vd.Name); }
            ctx.Code.Add((byte)Opcode.RENEW_LOCAL);
            ctx.Code.Add((byte)ctx.LocalIdx(vd.Name));
        }
        // A top-level CONST in a PROGRAM BODY is compiled, not declined: the value goes where a global
        // read finds it and the name joins the current const set, which is exactly what the tree does at
        // the top level (a root-scope entry plus GetCurrentConstSet). Both halves are one internal call,
        // because a compiled STORE_GLOBAL_FAST cannot populate the const set, and the name is pushed
        // FIRST so the helper receives (name, value). Before this, --no-source REFUSED such a file by
        // name rather than writing an entry that would accept a write the tree refuses.
        if (ctx.ProgramBody && vd.IsConst)
        {
            // The callee goes FIRST: the VM's CALL pops the args (from the top) and then the callee, so the
            // pushed order is [callee, arg0, arg1], the engine's own comment says [marker, calleeName,
            // args…]. Pushing the name first made "K" the callee and the two other values its arguments
            // (measured: `callee.kind=3 name=K args=2`, and the body bailed with "loaded from bytecode for
            // a different environment").
            EmitLoadConst(ctx, DefineConstHelperName);
            EmitLoadConst(ctx, vd.Name);
            if (vd.Initializer != null) CompileExpr(ctx, vd.Initializer);
            else EmitLoadConst(ctx, null);
            if (vd.TupleElementNames is { Count: > 0 })
            {
                var tupleNamesC = ctx.AddConst(vd.TupleElementNames);
                ctx.Code.Add((byte)Opcode.LOAD_CONST);
                ctx.Code.EmitConstIdx(tupleNamesC);
                ctx.Code.Add((byte)Opcode.ATTACH_TUPLE_NAMES);
                ctx.Code.EmitConstIdx(tupleNamesC);
            }
            ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(2)]);
            ctx.Code.Add((byte)Opcode.POP);   // the helper answers null, and a call leaves its result
            // A CONST's own declared type, for the reason the plain declaration states below: the definition
            // writes a value and no type, so `typeof(K)` answered the VALUE's type from a bundle ("int") where
            // the file answers the declaration ("long?").
            EmitDeclaredGlobalType(ctx, vd);
            return;
        }
        if (vd.Initializer != null)
        {
            // A PROGRAM BODY's own top-level `var` assigned from a CALL is remembered, so a later
            // `foreach` over it declines (see CallAssignedGlobals): the collection may be a coroutine.
            if (ctx.CallAssignedGlobals != null && ctx.ProgramBody && ctx.BodyDepth == 0
                && vd.Initializer is CallExpr or MethodCallExpr)
                ctx.CallAssignedGlobals.Add(vd.Name);
            // An EXPLICITLY TYPED declaration coerces its initializer, which the tree does in
            // VisitVarDeclStmt and a compiled body did not: measured inside a function, `short v = 7`
            // left an int (`typeof(v)` "int", `v is short` False) where the tree answers "short"/True,
            // so the declared type was not honoured at all. The callee goes FIRST because the VM's CALL
            // pops the args from the top and then the callee, and the type travels as the TypeInfo the
            // compiler already pushes for `is`/`as`. An inferred `var` is skipped by the SAME predicate
            // the top-level path uses to decide a declaration is the tree's.
            var coerceDecl = vd.Type is { } dt && !(dt.BaseType == TokenType.Object && dt.TypeName == null);
            if (coerceDecl) EmitLoadConst(ctx, CoerceDeclHelperName);
            var savedDeclaring = ctx.DeclaringVar;
            ctx.DeclaringVar = vd.Name;   // read by the lambda compile site: a self-capturing lambda there
            // A collection expression is TARGET-TYPED (C# 12): `int?[] a = [1, null, 3]` is legal because
            // the elements convert to the DECLARED element type, and the literal's own inference (`int`,
            // from 1 and 3) refuses the null before the coercion below could run. `int[] a = [1, null, 3]`
            // still refuses, which is the C# rule as well, and an inferred `var` keeps the inference.
            // ARRAY targets only, for the reason spelled out in VisitVarDeclStmt: the literal builds an
            // array, and a `List<T>` target is filled by the coercion, which accepts an `object[]` but
            // not a typed array.
            if (coerceDecl && vd.Initializer is ListLiteralExpr targetLit && vd.Type is { IsArray: true }
                && DeclaredContainerElementType(vd.Type) is { } targetElem)
                CompileListLiteral(ctx, targetLit, targetElem);
            else
                CompileExpr(ctx, vd.Initializer);
            ctx.DeclaringVar = savedDeclaring;
            if (coerceDecl)
            {
                EmitLoadConst(ctx, vd.Type!);
                EmitLoadConst(ctx, vd.Line);
                EmitLoadConst(ctx, vd.Column);
                ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(4)]);
            }
            // VarDeclStmt already does STORE_VAR
        }
        else
        { ctx.Code.Add((byte)Opcode.LOAD_CONST); ctx.Code.EmitConstIdx(ctx.AddConst(null)); }
        // A PROGRAM BODY's direct declarations are GLOBALS, exactly as the interpreter's top level
        // defines them: the store names the global in THIS body's slot table, and the VM resolves that
        // slot back to the name and writes the interpreter's real global scope (STORE_GLOBAL_FAST calls
        // SetGlobalValue), so the file's functions and lambdas keep seeing the same value. Slot tables
        // are per body, the NAMES are shared: a function that reads `G` compiles its own slot for it.
        // A declaration NESTED inside a block of the program body is NOT one of the top level definitions
        // this branch exists for: it is a local of that block, exactly as it would be in a function, and
        // its slot is what a closure captures (RENEW_LOCAL gives it a fresh binding per execution, which
        // is what the tree does). Storing it as a GLOBAL made every closure over it read the value left
        // by the LAST execution: measured, a loop of `var k = ...; arms.Add(fun() { picked = k; })` placed
        // the last element for every arm, which a source-less bundle showed as a drag-and-drop tool that
        // always created the palette's last kind.
        // A name the compile has ALREADY bound to a local slot is the same case one step in: a `for`'s
        // init var is pre-bound as a fresh local (CompileForStmt, so the loop's scope shadows an outer
        // name), and this branch would store the GLOBAL while every read and the increment used the
        // LOCAL: measured, a top-level `for (var i = 0; i < 3; i = i + 1)` printed null, 1, 2 from BOTH
        // a program body and a --no-source bundle, because the local was never written.
        if (ctx.ProgramBody && ctx.BodyDepth == 0 && !ctx.LocalMap.ContainsKey(vd.Name))
        {
            // Tuple-typed declaration: attach the declared element names before the store, for the
            // reason the local path below states (MAKE_TUPLE alone carries no element names).
            if (vd.TupleElementNames is { Count: > 0 })
            {
                var tupleNamesG = ctx.AddConst(vd.TupleElementNames);
                ctx.Code.Add((byte)Opcode.LOAD_CONST);
                ctx.Code.EmitConstIdx(tupleNamesG);
                ctx.Code.Add((byte)Opcode.ATTACH_TUPLE_NAMES);
                ctx.Code.EmitConstIdx(tupleNamesG);
            }
            var globalSlot = ctx.GlobalSlot(vd.Name);
            ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)globalSlot]);
            // THE DECLARED TYPE, which the store above cannot carry: a `--no-source` bundle answered
            // `typeof(n)` "long" for `long? n = 1;` and "null" for `long? n;`, where the file (whose top level
            // TREE-walks that declaration) answers "long?" for both. The spelling is the one the tree records in
            // `VisitVarDeclStmt`, and the helper only sets the TYPE, because the store has already written the
            // value and a later assignment keeps the type (`RewriteKeepingType`).
            EmitDeclaredGlobalType(ctx, vd);
            return;
        }
        var vdIdx = ctx.LocalIdx(vd.Name);
        ctx.SetLocalDeclaredType(vdIdx, vd.Type);
        // Tuple-typed declaration: (int i, int x) p = (0, 0), MAKE_TUPLE builds the value
        // WITHOUT element names, so attach the declared names before the store (tree parity:
        // VisitVarDeclStmt attaches TupleElementNames at bind time; without this, p.i
        // fails on the VM with "Property 'i' not found on tuple").
        if (vd.TupleElementNames is { Count: > 0 })
        {
            var tupleNamesCi = ctx.AddConst(vd.TupleElementNames);
            ctx.Code.Add((byte)Opcode.LOAD_CONST);
            ctx.Code.EmitConstIdx(tupleNamesCi);
            ctx.Code.Add((byte)Opcode.ATTACH_TUPLE_NAMES);
            ctx.Code.EmitConstIdx(tupleNamesCi);
        }
        // A const declaration's own initializing store must not trip the const check, use STORE_CONST.
        ctx.Code.AddRange([(byte)(vd.IsConst ? Opcode.STORE_CONST : Opcode.STORE_VAR), (byte)vdIdx]);
        if (vd.IsConst) ctx.ConstLocals.Add(vdIdx);
        return;
    }

    private static void CompileReturnStmt(Ctx ctx, ReturnStmt rs)
    {
        if (rs.Value != null) CompileExpr(ctx, rs.Value);
        else { ctx.Code.Add((byte)Opcode.LOAD_CONST); ctx.Code.EmitConstIdx(ctx.AddConst(null)); }
        // Return escaping a try/finally region must run every pending finally, route it
        // through THROW_FLOW_RETURN so the VM unwinds the handlers (running finallys) first.
        var rUnwind = ctx.TotalActiveHandlers;
        if (rUnwind > 0)
        {
            ctx.Code.Add((byte)Opcode.THROW_FLOW_RETURN);
            ctx.Code.Add((byte)rUnwind);
        }
        else ctx.Code.Add((byte)Opcode.RETURN);
        return;
    }

    private static void CompileIfStmt(Ctx ctx, IfStmt ifs)
    {
        {
            // A CONDITION'S PATTERN NAMES ARE SCOPED TO THE STATEMENT, so this snapshot covers the condition and
            // both branches and is restored on every exit from this arm (see Ctx.LocalsBefore for the measured
            // divergence: `tuple_is_patterns` 12b).
            var localsBeforeCond = ctx.LocalsBefore();
            // Constant-condition elimination: a condition that folded to a bare bool literal, and
            // emitted NOTHING else, so no short-circuit jump is in play, is decidable right here, so
            // the untaken side is never emitted and the load-plus-branch disappears entirely. This is
            // the peephole bench_vm_ab's cmpWork section measures: the comparison folds to
            // `LOAD_CONST <bool>` (see IntegralCompares) and this removes that load and the branch it
            // feeds, instead of leaving the VM to test a value the compiler already knew.
            //
            // Removing the whole condition is safe for the same reason TryFoldLastTwoConsts' own
            // RemoveRange is: a jump can only target the START of a condition (an instruction
            // boundary), and after the removal a jump to that start still lands on the first thing
            // that runs once the condition is known, the taken branch. Nothing patches to a position
            // INSIDE a condition.
            var condStart = ctx.Code.Count;
            CompileConditionExpr(ctx, ifs.Condition);
            // `LOAD_CONST <bool>` is ONE instruction, whose size the shape decides (see
            // TryFoldLastTwoConsts for why a literal span here is the silent kind of stale).
            var condSize = BytecodeFormat.InstructionSize(OperandKind.ConstIdx);
            if (ctx.Code.Count - condStart == condSize && ctx.Code[condStart] == (byte)Opcode.LOAD_CONST
                && ctx.Consts[BytecodeFormat.ReadConstIdx(ctx.Code, condStart + BytecodeFormat.OpcodeWidthBytes)]
                    is bool condValue)
            {
                ctx.Code.RemoveRange(condStart, condSize);
                CompileStmtList(ctx, condValue ? ifs.ThenBranch : ifs.ElseBranch);
                // A condition's pattern names die with the statement (see Ctx.LocalsBefore).
                ctx.DropLocalsSince(localsBeforeCond);
                return;
            }
        ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
        var elseOff = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        CompileStmtList(ctx, ifs.ThenBranch);
        if (ifs.ElseBranch.Count > 0)
        {
            ctx.Code.Add((byte)Opcode.JMP);
            var endOff = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            PatchJump(ctx, elseOff, ctx.Code.Count);
            CompileStmtList(ctx, ifs.ElseBranch);
            PatchJump(ctx, endOff, ctx.Code.Count);
        }
        else
            PatchJump(ctx, elseOff, ctx.Code.Count);
        // Both branches are compiled, so the condition's pattern names (and the branches' own locals) stop
        // resolving here, which is the tree's scope rule.
        ctx.DropLocalsSince(localsBeforeCond);
        return;
        }
    }

    private static void CompileWhileStmt(Ctx ctx, WhileStmt ws)
    {
        var wFrame = new Ctx.LoopFrame { HandlerFrameCountAtEntry = ctx.HandlerFrames.Count };
        var wLocalsBeforeCond = ctx.LocalsBefore();
        ctx.Loops.Push(wFrame);
        var loopStart = ctx.Code.Count;
        CompileConditionExpr(ctx, ws.Condition);
        ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
        var breakOff = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        wFrame.ContinueTarget = loopStart;
        CompileStmtList(ctx, ws.Body);
        ctx.Code.Add((byte)Opcode.JMP);
        var backOff = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        PatchJump(ctx, backOff, loopStart);
        PatchJump(ctx, breakOff, ctx.Code.Count);
        ctx.PatchLoopEnds(wFrame, ctx.Code.Count);
        ctx.Loops.Pop();
        // The condition's pattern names (and the body's own locals) stop resolving with the loop, as the
        // tree's scope rule has them.
        ctx.DropLocalsSince(wLocalsBeforeCond);
        return;
    }

    private static void CompileForeachStmt(Ctx ctx, ForeachStmt fe)
    {
        // `await foreach` stays the TREE's: an async sequence's element costs one MoveNextAsync the engine
        // must await, and the VM's folded drive (FOREACH_INIT/FOREACH_NEXT) has no state for an async
        // enumerator, so the statement declines and the tree drives it (see RunAwaitForeach).
        if (fe.IsAwait) { ctx.Success = false; return; }
        // A COLLECTION THAT CAME FROM A CALL is the tree's, in a PROGRAM BODY. The VM's folded drive
        // answers null for a coroutine and for an async sequence (CreateForEachState), so the statement
        // BAILS, and a bundle's ONE-body entry has no tree to re-run it on: measured, `mega_crash_test`
        // (`foreach (var v in coro)`, coro from `Coroutine.Start`) and `async_stream_test`
        // (`foreach (var line in File.ReadLinesAsync(p))`) stripped and then stopped part-way. A source run
        // is unaffected: it compiles ONE statement at a time, so it has no prior assignment to consult and
        // keeps the VM, exactly as a per-statement compile does today.
        // TWO SHAPES, and they COMPOSE: the collection is a `var` assigned from a call (the flow
        // CallAssignedGlobals carries), OR the collection IS a call, bare or on a receiver, which is how
        // `Coroutine.Start(...)` and `File.ReadLinesAsync(...)` are written. Both clauses are needed: the
        // wider half alone drops the narrow one, and `mega_crash_test`'s `foreach (var v in coro)` then goes
        // back to the VM, bails on a request-producing coroutine, and its bundle's 80 passed / 0 failed
        // becomes a refusal (measured).
        // A CALL'S RESULT IS NOT KNOWABLE STATICALLY, so a list-returning call pays a tree-driven loop at
        // the top level too. That is the conservative direction on purpose: the statement is only ever
        // carried when the body did NOT compile as one unit anyway (see CompileProgramWithCarries), and a
        // bundle that behaves differently from its script is the one outcome the strip exists to refuse.
        if (Interpreter.StatementCarrierEnabled && ctx.ProgramBody
            && (fe.Collection is CallExpr or MethodCallExpr
                || fe.Collection is VariableExpr feCollVar
                && ctx.CallAssignedGlobals?.Contains(feCollVar.Name) == true))
        {
            // The advice travels with the reason: a declined top level still RUNS (each statement falls back
            // to the tree), so this is not an error a script sees, and a reader meeting it in a dump or a
            // strip refusal is the one who needs to know what to change.
            ctx.DeclineNote = "the collection comes from a call (directly, or through a variable assigned one), "
                + "so the loop is carried and driven by the tree; put the loop in a function when the body must "
                + "compile";
            ctx.Success = false;
            return;
        }
        // A destructuring loop head (`foreach (var (k, v) in ...)`) binds each element through the
        // SAME DESTRUCT opcode a DestructuringStmt emits (see EmitForeachBinding), so the two
        // forms share this arm and the engine matcher's own cases and refusals are what run. It
        // used to decline the whole statement, which made such a file carry its source.
        var feFrame = new Ctx.LoopFrame { HandlerFrameCountAtEntry = ctx.HandlerFrames.Count };
        ctx.Loops.Push(feFrame);
        // ── FOREACH_CALL (Task 4): the collection is a bare-name call to a STATICALLY-known
        // coroutine (its result only this foreach consumes) → drive the callee's frame in-place
        // with no Coroutine/Generator wrappers. Required: the name is in the caller-supplied
        // CoroutineDecls map (a registered coroutine that compiled to VM bytecode and is free of
        // yield-request producers), the callee is NOT a method-context bare call (those resolve
        // through the class and keep CALL_METHOD + FOREACH_INIT), no named/ref/out args, and every
        // arg is side-effect-free, the args are re-emitted at the loop head so a re-entered loop
        // re-evaluates them fresh, so only pure expressions are safe to repeat.
        // FOREACH_CALL binds args POSITIONALLY with no defaults/params/RefCell handling, so the
        // callee decl must additionally have NO default values, NO trailing params, NO ref/out
        // params, and the call must pass exactly the declared param count, otherwise the args
        // would mis-bind (skipped defaults/params, missing coercion). This mirrors the flat CALL
        // gate (Vm.cs: d2.DefaultValues == null && !IsParams[^1] && !hasRef) and the VM's own
        // runtime re-check in the FOREACH_CALL handler.
        if (fe.LoopVarNames is not { Count: > 0 }   // the fast path binds one name; DESTRUCT needs the general arm
            && fe.Collection is CallExpr feCall
            && feCall.ArgNames is not { Count: > 0 }
            && feCall.Callee is VariableExpr feCv
            && !ctx.LocalMap.ContainsKey(feCv.Name)
            && !ctx.IsBuiltin(feCv.Name)
            && ctx.CoroutineDecls is not null && ctx.CoroutineDecls.TryGetValue(feCv.Name, out var fcDecl)
            && fcDecl.Compiled != null
            && fcDecl.DefaultValues == null
            && !(fcDecl.IsParams.Count > 0 && fcDecl.IsParams[^1])
            && !HasRefOrOutParams(fcDecl)
            && fcDecl.Params.Count == feCall.Arguments.Count
            && !HasRefOrOutArgs(feCall)
            && !IsMethodContextBareCall(ctx, feCv.Name)
            && feCall.Arguments.All(IsPureForeachCallArg))
        {
            // Layout: (loop head) LOAD_CONST(calleeName); args...; FOREACH_CALL(argCount,
            // stateLocal, varTypeConstIdx, endOff); STORE_VAR loopVar; body; JMP back to the
            // loop head, each iteration re-pushes the (side-effect-free) args so a re-entered
            // loop re-drives fresh; the state local holds the generator's locals array as its
            // identity (null → first visit); FOREACH_END clears it.
            var fcLoop = ctx.Code.Count;   // loop head, args are re-pushed here each iteration
            var fcState = ctx.AllocTemp();
            EmitLoadConst(ctx, feCv.Name);
            for (var ai = 0; ai < feCall.Arguments.Count; ai++) ctx.CompileArgument(feCall, ai);
            var fcCallPos = ctx.Code.Count;
            ctx.Code.Add((byte)Opcode.FOREACH_CALL);
            ctx.Code.Add(ctx.Count8(feCall.Arguments.Count));
            ctx.Code.Add((byte)fcState);
            ctx.Code.EmitConstIdx(ctx.AddConst(fe.VarType));
            ctx.Code.Add(0); ctx.Code.Add(0);        // end offset (patched after body)
            feFrame.ContinueTarget = fcLoop;
            EmitForeachBinding(ctx, fe);
            CompileStmtList(ctx, fe.Body);
            ctx.Code.Add((byte)Opcode.JMP);
            var fcBack = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            PatchJump(ctx, fcBack, fcLoop);
            // FOREACH_CALL's operand shape is U8U8ConstU16 (argCount, stateLocal, varType,
            // endLo+endHi), its endOff field and the instruction's total both come from the shape,
            // because the const field's width is not fixed.
            Patch(ctx, fcCallPos + BytecodeFormat.FieldAt(OperandKind.U8U8ConstU16, 3),
                  ctx.Code.Count - (fcCallPos + BytecodeFormat.InstructionSize(OperandKind.U8U8ConstU16)));
            ctx.PatchLoopEnds(feFrame, ctx.Code.Count);
            ctx.Code.Add((byte)Opcode.FOREACH_END);
            ctx.Code.Add((byte)fcState);
            ctx.Loops.Pop();
            return;
        }
        CompileExpr(ctx, fe.Collection);
        var feArr = ctx.AllocTemp();
        var feIdx = ctx.AllocTemp();
        ctx.Code.Add((byte)Opcode.FOREACH_INIT);
        ctx.Code.Add((byte)feArr);
        ctx.Code.Add((byte)feIdx);
        ctx.Code.Add(0); // allowEnumerable flag, retained for instruction layout; the VM classifies lazily at runtime
        var feLoop = ctx.Code.Count;
        feFrame.ContinueTarget = feLoop;
        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)feArr]);
        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)feIdx]);
        var feNext = ctx.Code.Count;
        ctx.Code.Add((byte)Opcode.FOREACH_NEXT);
        ctx.Code.Add(0); ctx.Code.Add(0);                 // end offset (patched after body)
        ctx.Code.EmitConstIdx(ctx.AddConst(fe.VarType));     // loop var type (null = as-is)
        EmitForeachBinding(ctx, fe);
        ctx.Code.AddRange([(byte)Opcode.INC, (byte)feIdx]);
        ctx.Code.Emit(Opcode.POP);                   // drop INC's pushed value, consumed nowhere in the loop
        CompileStmtList(ctx, fe.Body);
        ctx.Code.Add((byte)Opcode.JMP);
        var feBack = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        PatchJump(ctx, feBack, feLoop);
        // endOff is measured from the END of the instruction, so both the endOff field's offset and
        // the instruction's total come from the shape: a const-bearing operand is not a fixed width.
        Patch(ctx, feNext + BytecodeFormat.FieldAt(OperandKind.U16Const, 0),
              ctx.Code.Count - (feNext + BytecodeFormat.InstructionSize(OperandKind.U16Const)));
        ctx.PatchLoopEnds(feFrame, ctx.Code.Count);
        ctx.Code.Add((byte)Opcode.FOREACH_END);           // dispose enumerator-based state (also reached by break)
        ctx.Code.Add((byte)feArr);
        ctx.Loops.Pop();
        return;
    }

    private static void CompileBlockStmt(Ctx ctx, BlockStmt bs)
    {
        CompileStmtList(ctx, bs.Statements);
        return;
    }

    private static void CompileForStmt(Ctx ctx, ForStmt fs)
    {
        // ── For-loop init-var scope shadowing ──
        // The tree pops the for-init scope at loop exit (the loop var is Undefined
        // afterwards; an outer same-named local resurfaces). The VM's flat locals cannot
        // pop, so compile-time shadowing does it: bind the init var to a FRESH slot for
        // the loop, then restore the name→slot mapping at loop exit. Post-loop references
        // then resolve to the outer local (6c) or, with no outer binding, fall through to
        // LOAD_GLOBAL_FAST → Undefined variable (2d/5c). The shadowed slot stays in the
        // locals table (allocated, unreachable, harmless).
        int? fsShadowSlot = null;
        string? fsShadowName = null;
        var fsHadOuter = false;
        var fsOuterSlot = -1;
        if (fs.Init is VarDeclStmt fsVd && fsVd.Initializer != null)
        {
            fsShadowName = fsVd.Name;
            if (ctx.LocalMap.TryGetValue(fsVd.Name, out var fsOld))
            {
                fsHadOuter = true;
                fsOuterSlot = fsOld;
            }
            fsShadowSlot = ctx.Locals.Count;
            ctx.LocalMap[fsVd.Name] = fsShadowSlot.Value;
            ctx.Locals.Add(fsVd.Name);
        }
        // Compile as: init; while(cond) { body; inc; }
        if (fs.Init != null) CompileStmt(ctx, fs.Init);
        // The CONDITION's pattern names (and the body's own locals) die with the loop, which is the tree's
        // scope rule. The loop VARIABLE keeps its own rule: the shadow block above and its restore below.
        var fLocalsBeforeCond = ctx.LocalsBefore();
        var fFrame = new Ctx.LoopFrame { HandlerFrameCountAtEntry = ctx.HandlerFrames.Count };
        ctx.Loops.Push(fFrame);
        var fLoopStart = ctx.Code.Count;
        CompileConditionExpr(ctx, fs.Condition);
        ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
        var fBreak = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        CompileStmtList(ctx, fs.Body);
        fFrame.ContinueTarget = ctx.Code.Count;   // continue → increment step
        if (fs.Increment != null) { CompileExpr(ctx, fs.Increment); ctx.Code.Emit(Opcode.POP); }
        ctx.Code.Add((byte)Opcode.JMP);
        var fBack = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        PatchJump(ctx, fBack, fLoopStart);
        PatchJump(ctx, fBreak, ctx.Code.Count);
        ctx.PatchLoopEnds(fFrame, ctx.Code.Count);
        ctx.Loops.Pop();
        // Restore the outer name→slot mapping (or drop the name entirely) now that the
        // loop is over, post-loop references must NOT see the loop's slot.
        if (fsShadowSlot != null && fsShadowName != null)
        {
            if (fsHadOuter) ctx.LocalMap[fsShadowName] = fsOuterSlot;
            else ctx.LocalMap.Remove(fsShadowName);
        }
        ctx.DropLocalsSince(fLocalsBeforeCond);
        return;
    }

    private static void CompileThrowStmt(Ctx ctx, ThrowStmt ts)
    {
        if (ts.Expression != null) CompileExpr(ctx, ts.Expression);
        else { EmitLoadConst(ctx, null); }
        ctx.Code.Add((byte)Opcode.THROW);
        return;
    }

    private static void CompileTryStmt(Ctx ctx, TryStmt tryStmt)
    {
        {
            // Bare try (no catch, no finally) is semantically transparent, just a block.
            if (tryStmt.Catches.Count == 0 && tryStmt.Finally == null)
            { CompileStmt(ctx, tryStmt.Body); return; }

            ctx.HandlerFrames.Push(new Ctx.HandlerFrame());
            var hf = ctx.HandlerFrames.Peek();
            var hasFinally = tryStmt.Finally != null;
            var hasCatches = tryStmt.Catches.Count > 0;
            hf.Active = (hasFinally ? 1 : 0) + (hasCatches ? 1 : 0);
            var endJumps = new List<int>();

            // Handler entries: finally pushed FIRST (outer), catch SECOND (inner), the VM
            // exception unwind pops innermost first, so the catch binds before the finally
            // runs, matching the tree-walker.
            var finOffPos = -1;
            if (hasFinally)
            {
                ctx.Code.Add((byte)Opcode.TRY_FINALLY);
                finOffPos = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            }
            var catchOffPos = -1;
            if (hasCatches)
            {
                ctx.Code.Add((byte)Opcode.TRY);
                catchOffPos = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            }

            CompileStmt(ctx, tryStmt.Body);

            if (hasFinally)
            {
                // Normal path: pop the finally entry, then run the finally body as plain
                // code (END_FINALLY with no pending just continues past it).
                ctx.Code.Add((byte)Opcode.POP_FINALLY);
                ctx.Code.Add((byte)Opcode.JMP);
                endJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
            }
            else if (hasCatches)
            {
                ctx.Code.Add((byte)Opcode.JMP);   // skip the catch chain
                endJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
            }

            // Catch chain: DUP the bound value, test the exception type, bind + body on
            // match, else try the next clause. No clause matching → rethrow: the exception
            // unwinds the still-pending finally entry (running it once) then propagates.
            var prevCatchFail = -1;
            if (hasCatches)
            {
                for (var ci = 0; ci < tryStmt.Catches.Count; ci++)
                {
                    var cc = tryStmt.Catches[ci];
                    if (ci == 0) PatchJump(ctx, catchOffPos, ctx.Code.Count);
                    else PatchJump(ctx, prevCatchFail, ctx.Code.Count);
                    ctx.Code.Emit(Opcode.DUP);
                    ctx.Code.Add((byte)Opcode.CATCH_MATCH);
                    ctx.Code.EmitConstIdx(ctx.AddConst(cc.ExceptionType));
                    ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                    prevCatchFail = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                    if (!string.IsNullOrEmpty(cc.VarName))
                        ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(cc.VarName)]);
                    else
                        ctx.Code.Emit(Opcode.POP);
                    CompileStmt(ctx, cc.Body);
                    if (hasFinally)
                    {
                        ctx.Code.Add((byte)Opcode.POP_FINALLY);   // pop finE on the catch path
                        ctx.Code.Add((byte)Opcode.JMP);
                        endJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    }
                    else
                    {
                        ctx.Code.Add((byte)Opcode.JMP);
                        endJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    }
                }
                // Last clause's "no match" path: rethrow the bound value.
                PatchJump(ctx, prevCatchFail, ctx.Code.Count);
                ctx.Code.Add((byte)Opcode.THROW);
            }

            if (hasFinally)
            {
                // Finally body (finOff target); the normal/catch JMPs land here too.
                PatchJump(ctx, finOffPos, ctx.Code.Count);
                foreach (var jp in endJumps) PatchJump(ctx, jp, ctx.Code.Count);
                CompileStmt(ctx, tryStmt.Finally!);
                ctx.Code.Add((byte)Opcode.END_FINALLY);
                var endFinPos = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                if (hasCatches) ctx.Code.Add((byte)Opcode.ENDTRY);
                // afterOff = instruction right after ENDTRY (END_FINALLY's ip already
                // advanced past the 2 offset bytes, so this is 1 for ENDTRY, else 0).
                PatchJump(ctx, endFinPos, ctx.Code.Count);
            }
            else
            {
                foreach (var jp in endJumps) PatchJump(ctx, jp, ctx.Code.Count);
                if (hasCatches) ctx.Code.Add((byte)Opcode.ENDTRY);
            }

            hf.Active = 0;
            ctx.HandlerFrames.Pop();
            return;
        }
    }

    private static void CompileUsingDeclStmt(Ctx ctx, UsingDeclStmt uds)
    {
        // `using var x = init;`.
        //
        // AN `await using var` DECLINES, for the reason `await using` does (see the UsingStmt arm): the
        // disposal is a CLR ValueTask awaited from inside a statement, so the tree drives it.
        if (uds.IsAwait) { ctx.Success = false; return; }
        //
        // INSIDE a block or a function the enclosing statement list's CompileStmtList wrapper emits a
        // TRY_FINALLY whose finally disposes this local at scope exit (reverse declaration order, incl.
        // early return / break / continue / exception, the VM unwinds handler frames).
        //
        // AT A PROGRAM BODY's own top level there is no scope to leave: the RUN's end is the exit, so the
        // value goes to the engine's ONE registry through `__defer_dispose__` and `Scope.RunDisposeOnExit`
        // releases it, the same mechanism the tree's VisitUsingDeclStmt uses. Measured before it: a
        // top-level `using var` never disposed on either engine, because the wrapper's disposal is
        // local-slot based and that scope never ran it.
        CompileExpr(ctx, uds.Initializer);
        ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(uds.VarName)]);
        if (uds.VarName == null)
            return;
        if (ctx.ProgramBody && ctx.BodyDepth == 0)
        {
            EmitLoadConst(ctx, DeferDisposeHelperName);
            ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx(uds.VarName)]);
            ctx.Code.Add((byte)Opcode.CALL);
            ctx.Code.Add(1);
            ctx.Code.Add((byte)Opcode.POP);   // the helper answers the value; the statement does not want it
            return;
        }
        ctx.UsingLocals.Peek().Add(ctx.LocalIdx(uds.VarName));
        return;
    }

    private static void CompileImportStmt(Ctx ctx, ImportStmt im)
    {
        // A TOP-LEVEL IMPORT runs where the file wrote it (see __run_import__). It used to be filtered
        // out of the body and replayed by the LOADER, which is right for a declaration-only load and
        // wrong for a run: a file that exports a library and then imports it failed at load with
        // "Module 'x' not found", because the replay ran before anything had written the file.
        // A NESTED import (inside a block) still declines the body, which is the documented boundary:
        // replaying one at load would make it unconditional.
        EmitLoadConst(ctx, Interpreter.RunImportHelperName);
        // The PATH is an EXPRESSION: a literal compiles to the same const it always did, so this arm is
        // byte-identical for `import "./x.inc"`, while a computed one (`Application.Path + "/m.inc"`) is
        // evaluated HERE, at the import's position, because the value only exists at run time.
        CompileExpr(ctx, im.PathExpression);
        EmitLoadConst(ctx, im.Alias);
        EmitLoadConst(ctx, im.Line);
        EmitLoadConst(ctx, im.Column);
        ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(4)]);
        EmitPop(ctx);
        return;
    }

    private static void CompileUsingStmt(Ctx ctx, UsingStmt us)
    {
        // try/finally dispose, the VM unwinds handler frames on return/break/continue and runs
        // the finally (DISPOSE), so flow-exiting a using body is safe now.
        // AN `await using` DECLINES: its disposal is a CLR ValueTask the VM would have to await from
        // inside a statement, which is the same gap `await foreach` has (see RunAwaitForeach), so the tree
        // drives it and a body holding one is carried rather than stripped.
        if (us.IsAwait) { ctx.Success = false; return; }
        ctx.HandlerFrames.Push(new Ctx.HandlerFrame()); ctx.HandlerFrames.Peek().Active = 1;
        CompileExpr(ctx, us.Initializer);
        ctx.Code.Add((byte)Opcode.TRY_FINALLY);
        var usFin = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(us.VarName)]);
        CompileStmt(ctx, us.Body);
        ctx.Code.Add((byte)Opcode.POP_FINALLY);
        ctx.Code.Add((byte)Opcode.JMP);
        var usJmp = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
        // finallyIp = here; normal path jumps into it (END_FINALLY falls through)
        PatchJump(ctx, usFin, ctx.Code.Count);
        PatchJump(ctx, usJmp, ctx.Code.Count);
        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx(us.VarName)]);
        ctx.Code.Add((byte)Opcode.DISPOSE);
        ctx.Code.Add((byte)Opcode.END_FINALLY);
        ctx.Code.Add(0); ctx.Code.Add(0);   // fall-through (no pending → continue)
        ctx.HandlerFrames.Peek().Active = 0;
        ctx.HandlerFrames.Pop();
        return;
    }

    private static void CompileDestructuringStmt(Ctx ctx, DestructuringStmt ds)
    {
        CompileExpr(ctx, ds.Source);
        ctx.Code.Add((byte)Opcode.DESTRUCT);
        ctx.Code.Add(ctx.Count8(ds.VarNames.Count));
        foreach (var vn in ds.VarNames)
        {
            // A DISCARD BINDS NOTHING, exactly as the tree's DestructureBind does (`if (names[i] == "_")
            // continue;`): its value is popped instead of stored, so `var x = _;` stays the undefined name it is
            // in a source run. Measured: `_tuple` check 3c read `_` as a DEFINED local from a bundle.
            if (vn == "_") { ctx.Code.Add((byte)Opcode.POP); continue; }
            // A PROGRAM BODY's top-level destructure defines GLOBALS, exactly as its `var` declarations do
            // (see the VarDeclStmt arm's program-body branch, and its two reasons): the tree's top level
            // defines them in the root scope, so a function or a CARRIED statement reading one must find it
            // there. Measured on `tuple_in_generics`: its `var (nx, ny) = n2[0];` became VM LOCALS while the
            // following `Check(..., nx is (int, int) ...)` was CARRIED and ran on the tree, which answered
            // `Undefined variable 'nx'`.
            if (ctx.ProgramBody && ctx.BodyDepth == 0 && !ds.IsConst && !ctx.LocalMap.ContainsKey(vn))
            {
                ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(vn)]);
                continue;
            }
            var di = ctx.LocalIdx(vn);
            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)di]);
            if (ds.IsConst) ctx.ConstLocals.Add(di);
        }
        return;
    }

    private static void CompileBreakStmt(Ctx ctx)
    {
        if (ctx.Loops.Count == 0) { ctx.Success = false; return; }
        EmitLoopJump(ctx, Opcode.THROW_FLOW_BREAK, ctx.Loops.Peek().BreakPatches,
            ctx.Loops.Peek().FlowBreakPatches);
        return;
    }

    private static void CompileContinueStmt(Ctx ctx)
    {
        // A switch pushes its own LoopFrame (for break) marked IsSwitch; a `continue` inside
        // a switch-in-loop must route to the ENCLOSING real loop's continue target (the tree
        // treats switch as transparent to continue). Walk the stack past switch frames.
        {
            var contFrame = ctx.Loops.Count == 0 ? null : ctx.Loops.Peek();
            if (contFrame != null && contFrame.IsSwitch)
            {
                contFrame = null;
                foreach (var f in ctx.Loops.ToArray())
                    if (!f.IsSwitch) { contFrame = f; break; }
            }
            if (contFrame == null) { ctx.Success = false; return; }
            EmitLoopJump(ctx, Opcode.THROW_FLOW_CONTINUE, contFrame.ContinuePatches,
                contFrame.FlowContinuePatches);
        }
        return;
    }

    private static void CompileSwitchStmt(Ctx ctx, SwitchStmt sws)
    {
        {
            // Constant/discard patterns + when-guards compile to VM; break exits the switch
            // (the switch's own LoopFrame). `continue` routes to the enclosing real loop
            // (ContinueStmt walks past switch frames), also VM-safe. Only a continue with
            // NO enclosing loop is invalid (both tree and VM error).
            var swTmp = ctx.LocalIdx("\u0000switch");
            CompileExpr(ctx, sws.Subject);
            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)swTmp]);
            var swFrame = new Ctx.LoopFrame { HandlerFrameCountAtEntry = ctx.HandlerFrames.Count, IsSwitch = true };
            ctx.Loops.Push(swFrame);
            var endPatches = new List<int>();

            // The subject stays on the stack for the whole clause chain, the same model as
            // match and switch EXPRESSIONS, so every clause is compiled by the single shared
            // CompileArmPattern emitter (no duplicated constant/type/var/relational code, and
            // logical/property/list/tuple patterns work here too instead of forcing a fallback).
            ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)swTmp]);
            foreach (var clause in sws.Cases)
            {
                var bodyEntryJumps = new List<int>();
                // Failures of the LAST pattern in the clause can only be patched once the body
                // has been emitted (their target is the NEXT clause). Failures of the earlier
                // patterns are alternatives: they fall into the next pattern's test, which is
                // emitted right after this pattern's jump-to-body.
                var clauseFailJumps = new List<int>();
                for (var pi = 0; pi < clause.Patterns.Count; pi++)
                {
                    var pat = clause.Patterns[pi];
                    var failHere = new List<int>();
                    if (pat is not DiscardPattern && !CompileArmPattern(ctx, pat, failHere))
                    {
                        ctx.Success = false;
                        break;
                    }
                    // Fall-through = matched (subject still on the stack): jump to guard/body.
                    ctx.Code.Add((byte)Opcode.JMP);
                    bodyEntryJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    if (pi + 1 < clause.Patterns.Count)
                    {
                        foreach (var f in failHere)
                            PatchJump(ctx, f, ctx.Code.Count);   // -> next pattern
                    }
                    else clauseFailJumps.AddRange(failHere);
                }
                if (!ctx.Success) break;

                // Match JMPs land on the GUARD when present (it reads the bindings the emitter
                // stored and can reject the clause), else directly on the body. No DUP before
                // the guard: a failing guard must leave the stack exactly as it was (subject
                // only), or the leaked copy drifts the stack.
                var matchTarget = ctx.Code.Count;
                var nextClauseJumps = new List<int>();
                if (clause.Guard != null)
                {
                    CompileExpr(ctx, clause.Guard);
                    ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                    nextClauseJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                }
                foreach (var j2 in bodyEntryJumps)
                    PatchJump(ctx, j2, matchTarget);
                ctx.Code.Emit(Opcode.POP);   // drop the subject, then run the body
                foreach (var st in clause.Body) CompileStmt(ctx, st);
                ctx.Code.Add((byte)Opcode.JMP);
                endPatches.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                // Now that the body is emitted, this clause's failures (last pattern) and any
                // failed guard go to the still-unemitted code = the next clause.
                clauseFailJumps.AddRange(nextClauseJumps);
                foreach (var n in clauseFailJumps)
                    PatchJump(ctx, n, ctx.Code.Count);
                foreach (var n in nextClauseJumps)
                    PatchJump(ctx, n, ctx.Code.Count);
            }
            // No clause matched: drop the subject that stayed on the stack.
            ctx.Code.Emit(Opcode.POP);
            var swEnd = ctx.Code.Count;
            foreach (var e in endPatches) PatchJump(ctx, e, swEnd);
            foreach (var p in swFrame.BreakPatches) PatchJump(ctx, p, swEnd);
            foreach (var p in swFrame.FlowBreakPatches)
                ctx.Code.PatchU16(p, swEnd - p);   // the U16 field's bytes, packed by the one writer
            ctx.Loops.Pop();
            return;
        }
    }

    private static void CompileNamespaceDeclStmt(Ctx ctx, NamespaceDeclStmt nsd)
    {
        foreach (var s in nsd.Body) CompileStmt(ctx, s);
        return;
    }


    /// <summary>Binds the foreach element that is on the operand stack to the loop variable. The
    /// single-name form stores it straight into the var's local; the destructuring form
    /// (`foreach (var (k, v) in ...)`) unpacks it with the SAME DESTRUCT opcode a DestructuringStmt
    /// emits, so both engines bind through one rule (the opcode's array/list/tuple/KeyValuePair
    /// cases, and its own refusal for a HashSet or a scalar).</summary>
    private static void EmitForeachBinding(Ctx ctx, ForeachStmt fe)
    {
        if (fe.LoopVarNames is not { Count: > 0 } names)
        {
            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(fe.VarName)]);
            return;
        }
        ctx.Code.Add((byte)Opcode.DESTRUCT);
        ctx.Code.Add(ctx.Count8(names.Count));
        foreach (var vn in names)
        {
            // A DISCARD BINDS NOTHING (see the DestructuringStmt arm): popped, not stored.
            if (vn == "_") { ctx.Code.Add((byte)Opcode.POP); continue; }
            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(vn)]);
        }
    }

    /// <summary>Compiles a list/tuple arm pattern by delegating the match to the engine matcher:
    /// the subject is popped, the engine's PatternMatches decides, and any bindings are written to
    /// the VM locals the compiler allocated for them. Guarantees parity with the tree.</summary>
    private static bool CompileEnginePattern(Ctx ctx, Pattern pattern, List<int> failJumps)
    {
        var names = PatternBindingNames(pattern);
        var localIdxs = new byte[names.Count];
        for (var i = 0; i < names.Count; i++) localIdxs[i] = (byte)ctx.LocalIdx(names[i]);
        var descriptor = new PatternEngineDescriptor(pattern, [.. names], localIdxs, pattern.SourceText);

        // DUP: PAT_LIST consumes a copy, so the arm's subject survives for the next arm / guard / POP.
        ctx.Code.Emit(Opcode.DUP);
        ctx.Code.Add((byte)Opcode.PAT_LIST);
        ctx.Code.EmitConstIdx(ctx.AddConst(descriptor));
        ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
        failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
        return true;
    }

    /// <summary>Compiles one arm pattern against the subject currently on the VM stack.
    /// Pattern emitters DUP the subject before consuming a copy, so the subject survives for the
    /// next arm; "no match" jump sites are appended to <paramref name="failJumps"/>.
    /// Returns false when the pattern kind is not compilable (caller falls back to the tree).</summary>
    private static bool CompileArmPattern(Ctx ctx, Pattern pattern, List<int> failJumps)
    {
        switch (pattern)
        {
            case DiscardPattern:
                return true;   // always matches, nothing to emit

            case ConstantPattern cp:
                ctx.Code.Emit(Opcode.DUP);
                CompileExpr(ctx, cp.Expr);
                ctx.Code.Add((byte)Opcode.MATCH_EQ);
                ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                return true;

            case TypePattern tp:
                ctx.Code.Emit(Opcode.DUP);
                EmitLoadConst(ctx, EmittedType(ctx, tp.Type));
                ctx.Code.Add((byte)Opcode.IS_TYPE);
                ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                if (tp.BindingName != null)
                {
                    // subject must survive (guard + shared POP): DUP the subject again so
                    // STORE_VAR consumes the copy.
                    ctx.Code.Emit(Opcode.DUP);
                    ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(tp.BindingName)]);
                }
                return true;

            case VarPattern vp:
                ctx.Code.Emit(Opcode.DUP);
                ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(vp.BindingName)]);
                return true;

            case RelationalPattern rp:
                ctx.Code.Emit(Opcode.DUP);
                CompileExpr(ctx, rp.Right);
                ctx.Code.Add((byte)Opcode.REL_PATTERN);
                ctx.Code.Add((byte)(rp.Op switch
                {
                    TokenType.Less => 0,
                    TokenType.Greater => 1,
                    TokenType.LessEqual => 2,
                    _ => 3
                }));
                ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                return true;

            case GroupPattern gp:
                return CompileArmPattern(ctx, gp.Inner, failJumps);

            case LogicalPattern { Kind: LogicalKind.And } ap:
                // both sides must match: failures of either jump to the next arm
                return CompileArmPattern(ctx, ap.Left, failJumps)
                    && CompileArmPattern(ctx, ap.Right!, failJumps);

            case LogicalPattern { Kind: LogicalKind.Not } np:
                {
                    // `not p` succeeds exactly when p fails: p's failures become this arm's
                    // fall-through, and p's success jumps to the next arm.
                    var innerFails = new List<int>();
                    if (!CompileArmPattern(ctx, np.Left, innerFails)) return false;
                    ctx.Code.Add((byte)Opcode.JMP);
                    failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    foreach (var f in innerFails)
                        PatchJump(ctx, f, ctx.Code.Count);
                    return true;
                }

            case LogicalPattern { Kind: LogicalKind.Or } op:
                {
                    // Either side may match: left success jumps past the right side.
                    var leftFails = new List<int>();
                    if (!CompileArmPattern(ctx, op.Left, leftFails)) return false;
                    ctx.Code.Add((byte)Opcode.JMP);
                    var skipRight = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                    foreach (var f in leftFails)
                        PatchJump(ctx, f, ctx.Code.Count);
                    if (!CompileArmPattern(ctx, op.Right!, failJumps)) return false;
                    PatchJump(ctx, skipRight, ctx.Code.Count);
                    return true;
                }

            case PropertyPattern pp:
                // Members: read each one (non-match when absent) and match its nested pattern
                // against the value left on the stack by TRY_GET_PROP.
                foreach (var (memberName, memberPattern) in pp.Members)
                {
                    // Stack before the read: [.. subject]; TRY_GET_PROP leaves [.. subject, value, found].
                    // BOTH failure paths (member absent, sub-pattern mismatch) must drop the member value, so
                    // they route through a cleanup stub that pops it and jumps to the arm-fail target.
                    var memberFails = new List<int>();
                    var absentJumps = new List<int>();
                    var successJumps = new List<int>();
                    ctx.Code.Emit(Opcode.DUP);          // instance copy; subject stays below
                    EmitLoadConst(ctx, memberName);
                    ctx.Code.Add((byte)Opcode.TRY_GET_PROP);
                    ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                    absentJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    if (!CompileArmPattern(ctx, memberPattern, memberFails)) return false;
                    ctx.Code.Emit(Opcode.POP);          // success: drop the member value
                    ctx.Code.Add((byte)Opcode.JMP);
                    successJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);

                    var stub = ctx.Code.Count;               // cleanup stub
                    foreach (var f in absentJumps) PatchJump(ctx, f, stub);
                    foreach (var f in memberFails) PatchJump(ctx, f, stub);
                    ctx.Code.Emit(Opcode.POP);          // drop the lingering member value
                    ctx.Code.Add((byte)Opcode.JMP);          // -> arm failure (patched by the caller)
                    failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);

                    var after = ctx.Code.Count;
                    foreach (var sj in successJumps) PatchJump(ctx, sj, after);
                }
                return true;

            case ListPattern lp:
                return CompileEnginePattern(ctx, lp, failJumps);
            case TupleArmPattern tap:
                return CompileEnginePattern(ctx, tap, failJumps);

            default:
                // Nothing left uncompiled; anything new must force the tree fallback.
                return false;
        }
    }

    /// <summary>The nesting depth an expression may have before the compiler DECLINES the body, so the tree walker
    /// runs it instead. This is native recursion over an AST, and a deep one kills the process UNCATCHABLY on the
    /// shipped build: measured, one `return 1 + 1 + ... + 1;` with 20,000 terms overflowed the 32 MB stack, which is
    /// the "~23k frames physical" the csproj's stack note predicts, while 5,000 terms compiled in 53 ms. Declining is
    /// not a loss: the SAME 20,000 terms at the TOP LEVEL, which the tree walker evaluates, answer correctly, so a
    /// declined body runs there. 5000 is the depth that same csproj note validates for this stack ("guard=5000
    /// survives its own throw-unwind"), and it leaves a wide margin under the physical limit.</summary>
    private const int MaxExprDepth = 5000;

    static void CompileExpr(Ctx ctx, Expr expr)
    {
        // A call asked for as a REFERENCE from a METHOD (`ref int r = ref box.F();`, `box.F() = 7;`) is still the TREE's:
        // carrying the want through the method dispatch would have to reach MethodDispatch's own invocation, and its flat
        // method swap dereferences, so the shape would arrive as a value. A FREE function IS compiled now (the marker
        // below tells the VM to hand the CELL back), and a ref ARGUMENT (`Bump(ref box.F)`) was already compiled by
        // CompileArgument.

        // Depth guard FIRST, so a tree too deep to walk is declined before any of it is compiled. The counter is
        // saved and restored in a finally, the idiom this file already uses for BodyDepth, because a switch like
        // this has many returns and one of them would otherwise leak the increment.
        var savedExprDepth = ctx.ExprDepth;
        ctx.ExprDepth = savedExprDepth + 1;
        if (ctx.ExprDepth > MaxExprDepth)
        {
            ctx.Success = false;
            ctx.ExprDepth = savedExprDepth;
            return;
        }
        try
        {
            ctx.CurrentStatement = expr.GetType().Name + " at line " + expr.Line;
            switch (expr)
            {
                case AwaitExpr awe:
                    CompileAwaitExpr(ctx, awe);
                    break;
                case TypeRefExpr tre:
                    CompileTypeRefExpr(ctx, tre);
                    break;
                case NumberExpr ne:
                    CompileNumberExpr(ctx, ne);
                    break;
                case StringExpr se:
                    CompileStringExpr(ctx, se);
                    break;
                case CharExpr ce:
                    CompileCharExpr(ctx, ce);
                    break;
                case IncrementDecrementExpr ide:
                    CompileIncrementDecrementExpr(ctx, ide);
                    break;
                case BoolExpr be:
                    CompileBoolExpr(ctx, be);
                    break;
                case VariableExpr ve:
                    CompileVariableExpr(ctx, ve);
                    break;
                case NewArrayExpr nae:
                    CompileNewArrayExpr(ctx, nae);
                    break;
                case NewStructExpr nse:
                    CompileNewStructExpr(ctx, nse);
                    break;
                case CastExpr ce:
                    CompileCastExpr(ctx, ce);
                    break;
                case AsExpr ae:
                    CompileAsExpr(ctx, ae);
                    break;
                case LogicalExpr le:
                    CompileLogicalExpr(ctx, le);
                    break;
                case IsNullExpr ine:
                    CompileIsNullExpr(ctx, ine);
                    break;
                case IsPatternExpr ipe:
                    CompileIsPatternExpr(ctx, ipe);
                    break;
                case IndexExpr ie:
                    CompileIndexExpr(ctx, ie);
                    break;
                case RangeExpr re:
                    CompileRangeExpr(ctx, re);
                    break;
                case ThisExpr:
                    CompileThisExpr(ctx);
                    break;
                case MatchExpr me:
                    CompileMatchExpr(ctx, me);
                    break;
                case SwitchExpr swe:
                    CompileSwitchExpr(ctx, swe);
                    break;
                case ComprehensionExpr ce2:
                    CompileComprehensionExpr(ctx, ce2);
                    break;
                case ObjectInitExpr oie:
                    CompileObjectInitExpr(ctx, oie);
                    break;
                case WithExpr we:
                    CompileWithExpr(ctx, we);
                    break;
                case SuperExpr:
                    CompileSuperExpr(ctx);
                    break;
                case ConditionalExpr ce:
                    CompileConditionalExpr(ctx, ce);
                    break;
                case NullCoalescingExpr nce:
                    CompileNullCoalescingExpr(ctx, nce);
                    break;
                case BinaryExpr be:
                    CompileBinaryExpr(ctx, be);
                    break;
                case UnaryExpr { Operator.Type: TokenType.Minus } ue:
                    CompileUnaryMinusExpr(ctx, ue);
                    break;
                case UnaryExpr { Operator.Type: TokenType.Tilde } ue:
                    CompileUnaryTildeExpr(ctx, ue);
                    break;
                case UnaryExpr { Operator.Type: TokenType.Bang } ue:
                    CompileUnaryBangExpr(ctx, ue);
                    break;
                case UnaryExpr { Operator.Type: TokenType.PlusPlus } ue:
                    CompileUnaryPlusPlusExpr(ctx, ue);
                    break;
                case UnaryExpr { Operator.Type: TokenType.MinusMinus } ue:
                    CompileUnaryMinusMinusExpr(ctx, ue);
                    break;
                case AssignExpr ae:
                    CompileAssignExpr(ctx, ae);
                    break;
                case RefAssignExpr rae:
                    CompileRefAssignExpr(ctx, rae);
                    break;
                case CallExpr ce:
                    CompileCallExpr(ctx, ce);
                    break;
                case CompoundAssignExpr ca:
                    CompileCompoundAssignExpr(ctx, ca);
                    break;
                case IsExpr ise:
                    CompileIsExpr(ctx, ise);
                    break;
                case LambdaExpr le:
                    CompileLambdaExpr(ctx, le);
                    break;
                case TupleExpr tup:
                    CompileTupleExpr(ctx, tup);
                    break;
                case ListLiteralExpr lle:
                    CompileListLiteralExpr(ctx, lle);
                    break;
                case DictLiteralExpr dle:
                    CompileDictLiteralExpr(ctx, dle);
                    break;
                case NullExpr:
                    CompileNullExpr(ctx);
                    break;
                case ArrayAccessExpr ae:
                    CompileArrayAccessExpr(ctx, ae);
                    break;
                case ArrayAssignExpr aae:
                    CompileArrayAssignExpr(ctx, aae);
                    break;
                case PropertyAccessExpr pae:
                    CompilePropertyAccessExpr(ctx, pae);
                    break;
                case PropertyAssignExpr pase:
                    CompilePropertyAssignExpr(ctx, pase);
                    break;
                case MethodCallExpr mce:
                    CompileMethodCallExpr(ctx, mce);
                    break;
                case NewInstanceExpr nie:
                    CompileNewInstanceExpr(ctx, nie);
                    break;
                case YieldExpr:
                    CompileYieldExpr(ctx);
                    break;
                default:
                    ctx.Success = false;
                    return;
            }
        }
        finally { ctx.ExprDepth = savedExprDepth; }
    }
    // The cases of CompileExpr's switch, one method per kind, in the order the
    // switch dispatched them. Each body is the case body verbatim: the arm-level `break` became a `return`,
    // which is the same exit (the switch sits at the end of its method, inside the try whose finally restores
    // the depth counter).
    private static void CompileAwaitExpr(Ctx ctx, AwaitExpr awe)
    {
            if (!ctx.IsAsyncBody) { ctx.Success = false; return; }
            CompileExpr(ctx, awe.Operand);
            ctx.Code.Add((byte)Opcode.AWAIT);
            return;
        // A type reference - the `List<int>` of `List<int>.Create()`. Pushing the TypeInfo is all
        // the VM needs: the const pool already carries one (IS_TYPE pushes exactly this), and the
        // member lookup then has the type ARGUMENTS it must bind T from. Without this case the whole
        // enclosing function fell back to the tree walker.
    }

    private static void CompileTypeRefExpr(Ctx ctx, TypeRefExpr tre)
    {
            EmitLoadConst(ctx, EmittedType(ctx, tre.Type));
            return;
    }

    private static void CompileNumberExpr(Ctx ctx, NumberExpr ne)
    {
            // A float literal must be parsed AS a float. long.TryParse(…, NumberStyles.Any) ACCEPTS a
            // decimal point and truncates, "1.0f" parses to 1, so every N.0f literal compiled to an
            // integer: `float h = 1.0f; h / 3` was 0 where the tree evaluates 0.3333…, and typeof(1.0f)
            // said "long" against the tree's "float". Fractional literals escaped only because "0.5" fails
            // to parse as a long and fell through to the floating branch below, which is why a `m`
            // literal then came out a DOUBLE on the compiled path, against the tree's decimal.
            // Only these two types are routed here: the suffix-less `1.0` is MEASURED as "int" in BOTH
            // engines (a shared quirk of the numeric literal handling), so sending it down the double
            // branch would trade a real bug for a new divergence.
            // See assets/language/repl/vm/vm_float_suffix.slc.
            if (ne.Type.BaseType is TokenType.Float or TokenType.Decimal)
            {
                object? lit = ne.Type.BaseType == TokenType.Float
                    ? float.TryParse(ne.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var fLit) ? fLit : null
                    : decimal.TryParse(ne.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var mLit) ? mLit : null;
                if (lit != null)
                {
                    EmitLoadConst(ctx, lit);
                    return;
                }
            }
            if (long.TryParse(ne.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var lv))
            {
                // Mirror the tree's narrowest-fit cast (matches VisitNumberExpr type hints)
                var bt = ne.Type.BaseType;
                if (bt == TokenType.Int && lv is >= int.MinValue and <= int.MaxValue)
                    EmitLoadConst(ctx, (int)lv);
                else if (bt == TokenType.Short && lv is >= short.MinValue and <= short.MaxValue)
                    EmitLoadConst(ctx, (short)lv);
                else if (bt == TokenType.Byte && lv is >= byte.MinValue and <= byte.MaxValue)
                    EmitLoadConst(ctx, (byte)lv);
                else if (bt == TokenType.SByte && lv is >= sbyte.MinValue and <= sbyte.MaxValue)
                    EmitLoadConst(ctx, (sbyte)lv);
                else if (bt == TokenType.UShort && lv is >= ushort.MinValue and <= ushort.MaxValue)
                    EmitLoadConst(ctx, (ushort)lv);
                else if (bt == TokenType.UInt && lv is >= uint.MinValue and <= uint.MaxValue)
                    EmitLoadConst(ctx, (uint)lv);
                else
                    EmitLoadConst(ctx, lv);
            }
            else if (double.TryParse(ne.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var dv))
                EmitLoadConst(ctx, dv);
            else
                EmitLoadConst(ctx, ne.Value);
            return;
    }

    private static void CompileStringExpr(Ctx ctx, StringExpr se)
    {
            EmitLoadConst(ctx, se.Value);
            return;
    }

    private static void CompileCharExpr(Ctx ctx, CharExpr ce)
    {
            EmitLoadConst(ctx, ce.Value);
            return;
    }

    private static void CompileIncrementDecrementExpr(Ctx ctx, IncrementDecrementExpr ide)
    {
            var deltaConst = ctx.AddConst(ide.Operator.Type == TokenType.PlusPlus ? 1 : -1);
            switch (ide.Target)
            {
                case VariableExpr tv when ctx.LocalMap.ContainsKey(tv.Name):
                    var tvIdx = ctx.LocalIdx(tv.Name);
                    if (ide.IsPrefix)
                    {
                        // push new value: cur + delta; DUP; store
                        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)tvIdx]);
                        ctx.Code.AddRange([(byte)Opcode.LOAD_CONST, .. ctx.Code.ConstBytes(deltaConst)]);
                        ctx.Code.Add((byte)Opcode.ADD);
                        ctx.Code.Emit(Opcode.DUP);
                        ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)tvIdx]);
                    }
                    else
                    {
                        // push old value: DUP; cur + delta; store
                        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)tvIdx]);
                        ctx.Code.Emit(Opcode.DUP);
                        ctx.Code.AddRange([(byte)Opcode.LOAD_CONST, .. ctx.Code.ConstBytes(deltaConst)]);
                        ctx.Code.Add((byte)Opcode.ADD);
                        ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)tvIdx]);
                    }
                    break;
                case VariableExpr fv when ctx.IsMethod && ctx.FieldNames is { Count: > 0 } && ctx.FieldNames.Contains(fv.Name):
                    // Bare instance-field inc/dec (`X++` ≡ `this.X++`). SET_PROP pops
                    // (value, target, name) with value on TOP, park the new value in a temp,
                    // then [this, name, temp]. Postfix captures the OLD value first.
                    {
                        var fvTmp = ctx.AllocTemp();
                        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                        EmitLoadConst(ctx, fv.Name);
                        ctx.Code.Add((byte)Opcode.GET_PROP);
                        if (ide.IsPrefix)
                        {
                            // stack: [old] → ADD delta → [new]; park it; SET_PROP re-pushes it.
                            ctx.Code.AddRange([(byte)Opcode.LOAD_CONST, .. ctx.Code.ConstBytes(deltaConst)]);
                            ctx.Code.Add((byte)Opcode.ADD);
                            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)fvTmp]);
                        }
                        else
                        {
                            // stack: [old] → DUP (keep old for result), ADD delta → [old, new]
                            ctx.Code.Emit(Opcode.DUP);
                            ctx.Code.AddRange([(byte)Opcode.LOAD_CONST, .. ctx.Code.ConstBytes(deltaConst)]);
                            ctx.Code.Add((byte)Opcode.ADD);
                            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)fvTmp]);
                        }
                        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                        EmitLoadConst(ctx, fv.Name);
                        ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)fvTmp]);
                        ctx.Code.Add((byte)Opcode.SET_PROP);
                        if (!ide.IsPrefix) ctx.Code.Emit(Opcode.POP);   // drop SET's pushed new; keep OLD
                        break;
                    }
                case VariableExpr gv:
                    // Global inc/dec, read-modify-write against the real global scope
                    if (ide.IsPrefix)
                    {
                        ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(gv.Name)]);
                        ctx.Code.AddRange([(byte)Opcode.LOAD_CONST, .. ctx.Code.ConstBytes(deltaConst)]);
                        ctx.Code.Add((byte)Opcode.ADD);
                        ctx.Code.Emit(Opcode.DUP);
                        ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(gv.Name)]);
                    }
                    else
                    {
                        ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(gv.Name)]);
                        ctx.Code.Emit(Opcode.DUP);
                        ctx.Code.AddRange([(byte)Opcode.LOAD_CONST, .. ctx.Code.ConstBytes(deltaConst)]);
                        ctx.Code.Add((byte)Opcode.ADD);
                        ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(gv.Name)]);
                    }
                    break;
                default:
                    // property / field / array-element targets, GET target+key, ADD delta,
                    // then SET target+key+newvalue. SET_PROP/SET_INDEX push the stored value,
                    // so the postfix case must capture the OLD value first (DUP before the ADD)
                    // and then discard the pushed new value (POP after the SET).
                    CompileIncDecMember(ctx, ide, deltaConst);
                    break;
            }
            return;
    }

    private static void CompileBoolExpr(Ctx ctx, BoolExpr be)
    {
            EmitLoadConst(ctx, be.Value);
            return;
    }

    private static void CompileVariableExpr(Ctx ctx, VariableExpr ve)
    {
            if (ctx.LocalMap.ContainsKey(ve.Name))
            {
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx(ve.Name)]);
            }
            else if (ctx.IsMethod && ctx.FieldNames is { Count: > 0 } && ctx.FieldNames.Contains(ve.Name))
            {
                // Bare instance-field READ (`X` ≡ `this.X`), GET_PROP pops (name, target).
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                EmitLoadConst(ctx, ve.Name);
                ctx.Code.Add((byte)Opcode.GET_PROP);
            }
            else
            {
                // Global reference, read-only LOAD_GLOBAL_FAST
                ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(ve.Name)]);
            }
            return;
    }

    private static void CompileNewArrayExpr(Ctx ctx, NewArrayExpr nae)
    {
            EmitLoadConst(ctx, nae.ElementType);
            if (nae.Sizes != null) foreach (var s in nae.Sizes) CompileExpr(ctx, s);
            if (nae.Initializer != null) foreach (var iv in nae.Initializer) CompileExpr(ctx, iv);
            ctx.Code.Add((byte)Opcode.NEW_ARRAY);
            ctx.Code.Add(ctx.Count8(nae.Sizes?.Count ?? 0));
            ctx.Code.Add(ctx.Count8(nae.Initializer?.Count ?? 0));
            return;
    }

    private static void CompileNewStructExpr(Ctx ctx, NewStructExpr nse)
    {
            EmitLoadConst(ctx, nse.StructName);
            if (nse.FieldValues != null) foreach (var fv in nse.FieldValues) CompileExpr(ctx, fv);
            ctx.Code.Add((byte)Opcode.NEW_STRUCT);
            ctx.Code.Add(ctx.Count8(nse.FieldValues?.Count ?? 0));
            return;
    }

    private static void CompileCastExpr(Ctx ctx, CastExpr ce)
    {
            CompileExpr(ctx, ce.Expression);
            ctx.Code.Add((byte)Opcode.CAST);
            ctx.Code.EmitConstIdx(ctx.AddConst(EmittedType(ctx, ce.Type)));
            return;
    }

    private static void CompileAsExpr(Ctx ctx, AsExpr ae)
    {
            CompileExpr(ctx, ae.Left);
            ctx.Code.Add((byte)Opcode.AS);
            ctx.Code.EmitConstIdx(ctx.AddConst(EmittedType(ctx, ae.Type)));
            return;
    }

    private static void CompileLogicalExpr(Ctx ctx, LogicalExpr le)
    {
            // Short-circuit value semantics mirroring the tree: && returns left when falsey else right;
            // || returns left when truthy else right.
            if (le.Operator.Type == TokenType.AndAnd || le.Operator.Type == TokenType.And)
            {
                CompileExpr(ctx, le.Left);
                ctx.Code.Emit(Opcode.DUP);
                ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                var lgKeep = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                ctx.Code.Emit(Opcode.POP);
                CompileExpr(ctx, le.Right);
                ctx.Code.Add((byte)Opcode.JMP);
                var lgEnd = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                PatchJump(ctx, lgKeep, ctx.Code.Count);
                PatchJump(ctx, lgEnd, ctx.Code.Count);
            }
            else
            {
                CompileExpr(ctx, le.Left);
                ctx.Code.Emit(Opcode.DUP);
                ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                var lgRight = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                ctx.Code.Add((byte)Opcode.JMP);
                var lgEnd = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                PatchJump(ctx, lgRight, ctx.Code.Count);   // jump-to-right lands after the JMP
                ctx.Code.Emit(Opcode.POP);
                CompileExpr(ctx, le.Right);
                PatchJump(ctx, lgEnd, ctx.Code.Count);
            }
            return;
    }

    private static void CompileIsNullExpr(Ctx ctx, IsNullExpr ine)
    {
            CompileExpr(ctx, ine.Left);
            EmitLoadConst(ctx, null);
            ctx.Code.Add((byte)Opcode.EQ);
            return;
    }

    private static void CompileIsPatternExpr(Ctx ctx, IsPatternExpr ipe)
    {
            {
                // `x is <pattern>`: the BINDING form (`is T name`) and the general one (`is > 5`, `is string or
                // int`, `is (1, 2)`, `is { A: 1 }`). The emitters are the ones a `match` ARM already uses
                // (CompileArmPattern), so the two spellings cannot disagree about what a pattern means, and the
                // engine's own matcher keeps the tree's semantics (MATCH_EQ's value-aware equality,
                // REL_PATTERN's relational rule, PAT_LIST going through PatternMatches).
                // Measured before this arm: every one of these sent the WHOLE enclosing body to the tree walker,
                // while `is null`, `is not null` and a bare `is T` compiled, so `if (o is string s)` was the
                // most expensive line a script could write.
                CompileExpr(ctx, ipe.Left);   // the subject, evaluated ONCE and left on the stack
                var isPattern = ipe.MatchPattern
                    ?? new TypePattern(ipe.Type, ipe.VarName is { Length: > 0 } ? ipe.VarName : null);
                var patternFails = new List<int>();
                // THE TREE'S OWN RULE for a pattern that BINDS names: allowed only in an if/while/for condition,
                // except the classic inline `expr is Type name` form, which the tree binds anywhere. Measured on
                // tuple_is_patterns: `var r2 = values is (string kind, int nn);` must be REFUSED, and the tree is
                // what says so. The compiler declines the other shapes, so ONE sentence names the mistake.
                var patternNames = PatternBindingNames(isPattern);
                var inlineBind = isPattern is TypePattern { BindingName: not null } && patternNames.Count == 1;
                if (patternNames.Count > 0 && !inlineBind && !ctx.InCondition)
                {
                    ctx.Success = false;
                    ctx.DeclineReason ??= "while compiling a named pattern outside a condition (the tree refuses it by name)";
                    return;
                }
                if (!CompileArmPattern(ctx, isPattern, patternFails))
                {
                    ctx.Success = false;   // a kind the pattern compiler does not model stays on the tree
                    ctx.DeclineReason ??= "while compiling a pattern: it has no bytecode form";
                    return;
                }
                // Pattern emitters DUP the subject and consume copies, so it is still there on both paths.
                ctx.Code.Emit(Opcode.POP);
                EmitLoadConst(ctx, true);
                ctx.Code.Add((byte)Opcode.JMP);
                var isMatched = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                foreach (var f in patternFails) PatchJump(ctx, f, ctx.Code.Count);
                ctx.Code.Emit(Opcode.POP);
                EmitLoadConst(ctx, false);
                PatchJump(ctx, isMatched, ctx.Code.Count);
                return;
            }
    }

    private static void CompileIndexExpr(Ctx ctx, IndexExpr ie)
    {
            CompileExpr(ctx, ie.Value);
            ctx.Code.Add((byte)Opcode.MAKE_INDEX);
            ctx.Code.Add((byte)(ie.FromEnd ? 1 : 0));
            return;
    }

    private static void CompileRangeExpr(Ctx ctx, RangeExpr re)
    {
            if (re.From != null) CompileExpr(ctx, re.From);
            else { EmitLoadConst(ctx, 0L); }
            if (re.To != null) CompileExpr(ctx, re.To);
            else { EmitLoadConst(ctx, 0L); }
            ctx.Code.Add((byte)Opcode.MAKE_RANGE);
            ctx.Code.Add((byte)(re.To != null ? 1 : 0));
            return;
    }

    private static void CompileThisExpr(Ctx ctx)
    {
            ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
            return;
    }

    private static void CompileMatchExpr(Ctx ctx, MatchExpr me)
    {
            {
                CompileExpr(ctx, me.Subject);
                var endJumps = new List<int>();
                foreach (var arm in me.Arms)
                {
                    var failJumps = new List<int>();
                    if (!CompileArmPattern(ctx, arm.Pattern, failJumps))
                    {
                        ctx.Success = false;   // property/list/tuple arm → whole match stays on the tree
                        break;
                    }
                    if (arm.Guard != null)
                    {
                        // Bindings are already stored in locals by the pattern emitters.
                        // NOTE: no DUP here, a guard failure must leave the stack exactly as
                        // it was (subject only), or the leaked copy drifts the stack.
                        CompileExpr(ctx, arm.Guard);
                        ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                        failJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    }
                    ctx.Code.Emit(Opcode.POP);   // drop subject
                    CompileExpr(ctx, arm.Value);
                    ctx.Code.Add((byte)Opcode.JMP);
                    endJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    foreach (var f in failJumps)
                        PatchJump(ctx, f, ctx.Code.Count);
                }
                if (!ctx.Success) return;
                // no match (last arm wasn't wildcard), drop subject + error
                ctx.Code.Emit(Opcode.POP);
                EmitLoadConst(ctx, "No matching arm in match expression.");
                ctx.Code.Add((byte)Opcode.THROW);
                foreach (var p in endJumps)
                    PatchJump(ctx, p, ctx.Code.Count);
                return;
            }
    }

    private static void CompileSwitchExpr(Ctx ctx, SwitchExpr swe)
    {
            {
                CompileExpr(ctx, swe.Subject);
                var endJumps = new List<int>();
                foreach (var arm in swe.Arms)
                {
                    var skipToNextArm = new List<int>();
                    // Same shared emitter as match and the switch statement: ONE implementation
                    // of the pattern semantics for every construct. The subject copy survives a
                    // pattern failure (the emitters DUP before consuming), so a failed test simply
                    // falls through to the next arm.
                    if (arm.Pattern is not DiscardPattern
                        && !CompileArmPattern(ctx, arm.Pattern, skipToNextArm))
                        ctx.Success = false;
                    if (!ctx.Success) break;
                    if (arm.Guard != null)
                    {
                        // (no DUP: a failing guard must not leave an extra copy on the stack)
                        CompileExpr(ctx, arm.Guard);
                        ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                        // Guard failure also skips to the next arm (a failing guard must NOT
                        // fall through to this arm's value), patch both pattern-fail and
                        // guard-fail jumps at the end of the arm.
                        skipToNextArm.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    }
                    ctx.Code.Emit(Opcode.POP);   // drop subject
                    CompileExpr(ctx, arm.Value);
                    ctx.Code.Add((byte)Opcode.JMP);
                    endJumps.Add(ctx.Code.Count); ctx.Code.Add(0); ctx.Code.Add(0);
                    foreach (var s in skipToNextArm)
                        PatchJump(ctx, s, ctx.Code.Count);
                }
                if (!ctx.Success) return;
                // No arm matched (last arm wasn't a discard), drop subject + error
                ctx.Code.Emit(Opcode.POP);
                EmitLoadConst(ctx, "No matching arm in switch expression.");
                ctx.Code.Add((byte)Opcode.THROW);
                foreach (var p in endJumps)
                    PatchJump(ctx, p, ctx.Code.Count);
                return;
            }
    }

    private static void CompileComprehensionExpr(Ctx ctx, ComprehensionExpr ce2)
    {
            {
                CompileExpr(ctx, ce2.Collection);
                var cArr = ctx.AllocTemp();
                var cIdx = ctx.AllocTemp();
                var cRes = ctx.AllocTemp();
                ctx.Code.Add((byte)Opcode.FOREACH_INIT);
                ctx.Code.Add((byte)cArr);
                ctx.Code.Add((byte)cIdx);
                ctx.Code.Add(1); // allowEnumerable flag, retained for instruction layout; VM iterates lazily via the live state
                ctx.Code.Add((byte)Opcode.NEW_LIST);
                ctx.Code.Add((byte)cRes);
                var cLoop = ctx.Code.Count;
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)cArr]);
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)cIdx]);
                var cNext = ctx.Code.Count;
                ctx.Code.Add((byte)Opcode.FOREACH_NEXT);
                ctx.Code.Add(0); ctx.Code.Add(0);
                ctx.Code.EmitConstIdx(ctx.AddConst(null));   // no element coercion (var)
                ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(ce2.IterVar)]);
                var cSkip = -1;
                if (ce2.Filter != null)
                {
                    CompileExpr(ctx, ce2.Filter);
                    ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
                    cSkip = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                }
                CompileExpr(ctx, ce2.ElementExpr);
                ctx.Code.Add((byte)Opcode.LIST_APPEND);
                ctx.Code.Add((byte)cRes);
                if (ce2.Filter != null)
                    PatchJump(ctx, cSkip, ctx.Code.Count);
                ctx.Code.AddRange([(byte)Opcode.INC, (byte)cIdx]);
                ctx.Code.Emit(Opcode.POP);   // drop INC's pushed value (loop consumes it nowhere)
                ctx.Code.Add((byte)Opcode.JMP);
                var cBack = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                PatchJump(ctx, cBack, cLoop);
                Patch(ctx, cNext + BytecodeFormat.FieldAt(OperandKind.U16Const, 0),
                      ctx.Code.Count - (cNext + BytecodeFormat.InstructionSize(OperandKind.U16Const)));
                ctx.Code.Add((byte)Opcode.FOREACH_END);   // dispose enumerator-based state (IEnumerable comprehensions)
                ctx.Code.Add((byte)cArr);
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)cRes]);
                ctx.Code.Add((byte)Opcode.COMP_FINISH);
                return;
            }
    }

    private static void CompileObjectInitExpr(Ctx ctx, ObjectInitExpr oie)
    {
            CompileExpr(ctx, oie.Inner);
            foreach (var (pn, pv) in oie.Initializers)
            {
                EmitLoadConst(ctx, pn);
                CompileExpr(ctx, pv);
            }
            ctx.Code.Add((byte)Opcode.OBJ_INIT);
            ctx.Code.Add(ctx.Count8(oie.Initializers.Count));
            return;
    }

    private static void CompileWithExpr(Ctx ctx, WithExpr we)
    {
            CompileExpr(ctx, we.Base);
            foreach (var (fn, fv) in we.Fields)
            {
                EmitLoadConst(ctx, fn);
                CompileExpr(ctx, fv);
            }
            ctx.Code.Add((byte)Opcode.WITH);
            ctx.Code.Add(ctx.Count8(we.Fields.Count));
            return;
    }

    private static void CompileSuperExpr(Ctx ctx)
    {
            ctx.Code.Add((byte)Opcode.LOAD_SUPER);
            return;
    }

    private static void CompileConditionalExpr(Ctx ctx, ConditionalExpr ce)
    {
            CompileExpr(ctx, ce.Condition);
            ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);
            var ceElse = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            CompileExpr(ctx, ce.ThenExpr);
            ctx.Code.Add((byte)Opcode.JMP);
            var ceEnd = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            PatchJump(ctx, ceElse, ctx.Code.Count);
            CompileExpr(ctx, ce.ElseExpr);
            PatchJump(ctx, ceEnd, ctx.Code.Count);
            return;
    }

    private static void CompileNullCoalescingExpr(Ctx ctx, NullCoalescingExpr nce)
    {
            CompileExpr(ctx, nce.Left);
            ctx.Code.Emit(Opcode.DUP);
            EmitLoadConst(ctx, null);
            ctx.Code.Add((byte)Opcode.EQ);
            ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);   // left != null → keep left
            var ncKeep = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            ctx.Code.Emit(Opcode.POP);            // drop the null left
            CompileExpr(ctx, nce.Right);
            ctx.Code.Add((byte)Opcode.JMP);
            var ncEnd = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            PatchJump(ctx, ncKeep, ctx.Code.Count);
            PatchJump(ctx, ncEnd, ctx.Code.Count);
            return;
    }

    private static void CompileBinaryExpr(Ctx ctx, BinaryExpr be)
    {
            // Short-circuit logical operators (&&, ||, and keyword and/or) are parsed as
            // LogicalExpr and never reach BinaryExpr. The old emission here emitted JMP_IF_FALSE
            // whose 2-byte operand was never patched, a latent miscompile. Defensive: if one ever
            // reaches this case, tree-walk instead of emitting broken code.
            if (be.Operator.Type == TokenType.AndAnd
                || be.Operator.Type == TokenType.And
                || be.Operator.Type == TokenType.OrOr
                || be.Operator.Type == TokenType.Or)
            {
                ctx.Success = false;
                return;
            }
            // A chain of operators is LEFT-NESTED, and compiling it by recursing into the left operand costs one
            // NATIVE frame per operator, so a deep chain kills the process with a stack overflow NativeAOT cannot
            // catch (measured: one `return 1 + 1 + ... + 1;` with 20,000 terms ended the shipped binary). The VM
            // needs no such stack, its operand stack is FLAT, so a chain belongs on the VM at any depth: the left
            // spine is walked with a LOOP here, its operators are collected, the innermost operand compiles once,
            // and each saved operator is emitted outward in turn. That is exactly the sequence the recursion
            // produced, so the emitted bytes and the const folding are unchanged.
            var chain = new List<BinaryExpr>();
            var node = be;
            while (true)
            {
                // Short-circuit operators are parsed as LogicalExpr and never reach BinaryExpr; this is the same
                // defensive check the single-node version carried, applied to every node of the chain.
                if (node.Operator.Type == TokenType.AndAnd
                    || node.Operator.Type == TokenType.And
                    || node.Operator.Type == TokenType.OrOr
                    || node.Operator.Type == TokenType.Or)
                {
                    ctx.Success = false;
                    return;
                }
                chain.Add(node);
                var left = node.Left as BinaryExpr;
                if (left == null) break;
                node = left;
            }
            // The fold anchor is where the chain's LEFTMOST operand is pushed, and it stays right for every step:
            // a folded step leaves ONE constant in its place, so the next step still sees exactly two constants
            // (its left, its right) from here, which is the same anchor the recursive version computed per node.
            var foldStart = ctx.Code.Count;
            // The POOL mark travels with the code anchor: a fold DELETES the code that referenced the constants
            // its steps added, so those pool entries have to go with it. Without this, a chain of literals adds
            // one DISTINCT folded value per step (2, 3, 4, ...) and the 1-byte operand index walks past 255,
            // which sets Success false and silently demotes the whole body to the tree walker (measured: 255
            // terms compiled, 256 did not).
            var constMark = ctx.ConstCount;
            CompileExpr(ctx, chain[chain.Count - 1].Left);   // the innermost operand, which is not a chain node
            var chainStep = chain.Count - 1;
            while (chainStep >= 0)
            {
                var step = chain[chainStep];
                CompileExpr(ctx, step.Right);
                var opByte = step.Operator.Type switch
                {
                    TokenType.Plus => (byte)Opcode.ADD,
                    TokenType.Minus => (byte)Opcode.SUB,
                    TokenType.Star => (byte)Opcode.MUL,
                    TokenType.Slash => (byte)Opcode.DIV,
                    TokenType.Less => (byte)Opcode.LT,
                    TokenType.Greater => (byte)Opcode.GT,
                    TokenType.EqualEqual => (byte)Opcode.EQ,
                    TokenType.LessEqual => (byte)Opcode.LE,
                    TokenType.GreaterEqual => (byte)Opcode.GE,
                    TokenType.BangEqual => (byte)Opcode.NE,
                    TokenType.Percent => (byte)Opcode.MOD,
                    TokenType.Ampersand => (byte)Opcode.BITAND,
                    TokenType.Pipe => (byte)Opcode.BITOR,
                    TokenType.Caret => (byte)Opcode.BITXOR,
                    TokenType.LeftShift => (byte)Opcode.SHL,
                    TokenType.RightShift => (byte)Opcode.SHR,
                    // Bitwise/shift/modulo not VM-safe yet, fall back to tree-walker
                    _ => -1
                };
                if (opByte < 0) { ctx.Success = false; return; }
                var op = (Opcode)opByte;
                if (ctx.TryFoldLastTwoConsts(foldStart, op, out var foldedValue))
                {
                    // Both operands were exactly one LOAD_CONST each, remove them and emit the folded
                    // constant in their place (checked; on overflow the helper reports no-fold instead).
                    ctx.Code.RemoveRange(foldStart, ctx.Code.Count - foldStart);
                    // Roll the pool back with the code: the two LOAD_CONSTs just removed were the only users of
                    // the entries their steps added, and the pool is indexed by a fixed-width operand. A value the
                    // REST of the body already holds keeps its slot, because the pool is deduped and rebuilding
                    // the same value returns the earlier index rather than appending a second one.
                    ctx.TrimConstsTo(constMark);
                    EmitLoadConst(ctx, foldedValue);
                }
                else
                {
                    ctx.Code.Add((byte)op);
                }
                chainStep = chainStep - 1;
            }
            return;
    }

    private static void CompileUnaryMinusExpr(Ctx ctx, UnaryExpr ue)
    {
            CompileExpr(ctx, ue.Right);
            ctx.Code.Add((byte)Opcode.NEG);
            return;
    }

    private static void CompileUnaryTildeExpr(Ctx ctx, UnaryExpr ue)
    {
            CompileExpr(ctx, ue.Right);
            ctx.Code.Add((byte)Opcode.BITNOT);
            return;
    }

    private static void CompileUnaryBangExpr(Ctx ctx, UnaryExpr ue)
    {
            CompileExpr(ctx, ue.Right);
            ctx.Code.Add((byte)Opcode.NOT);
            return;
    }

    private static void CompileUnaryPlusPlusExpr(Ctx ctx, UnaryExpr ue)
    {
            CompileIncDecUnary(ctx, ue, +1);
            return;
    }

    private static void CompileUnaryMinusMinusExpr(Ctx ctx, UnaryExpr ue)
    {
            CompileIncDecUnary(ctx, ue, -1);
            return;
    }

    private static void CompileAssignExpr(Ctx ctx, AssignExpr ae)
    {
            if (!ctx.LocalMap.ContainsKey(ae.Name))
            {
                if (ctx.IsMethod && ctx.FieldNames is { Count: > 0 } && ctx.FieldNames.Contains(ae.Name))
                {
                    // Bare instance-field WRITE (`X = v` ≡ `this.X = v`), SET_PROP pops
                    // (value, target, name-const) with TARGET deepest: push this, name, value.
                    ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                    EmitLoadConst(ctx, ae.Name);
                    CompileExpr(ctx, ae.Value);
                    ctx.Code.Add((byte)Opcode.SET_PROP);
                    return;
                }
                // Global write: STORE_GLOBAL_FAST instead of falling back
                CompileExpr(ctx, ae.Value);
                // The assignment VALUE survives the store, exactly as in the local branch below: an
                // assignment emits one value and the expression statement around it emits one POP to
                // discard it. Measured: this branch used to leave nothing, so a GLOBAL assignment used
                // as an expression (a for increment, `iter = iter + 1`) came out one value short and
                // the verifier refused the whole body of stress_dict_perf.
                ctx.Code.Emit(Opcode.DUP);
                ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(ae.Name)]);
                return;
            }
            CompileExpr(ctx, ae.Value);
            var aeIdx = ctx.LocalIdx(ae.Name);
            if (aeIdx < ctx.LocalDeclaredTypes.Count && Ctx.IsWideningNumericDecl(ctx.LocalDeclaredTypes[aeIdx]))
            {
                // Keep the declared numeric type on reassignment (e.g. `long sum = 0; sum = 0;`):
                // widen a narrower runtime numeric to the declared type before the store. The
                // TypeInfo const rides as the opcode's operand (WIDEN_NUM <constIdx>).
                ctx.Code.AddRange([(byte)Opcode.WIDEN_NUM, .. ctx.Code.ConstBytes(ctx.AddConst(ctx.LocalDeclaredTypes[aeIdx]))]);
            }
            ctx.Code.Emit(Opcode.DUP);
            ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)aeIdx]);
            return;
    }

    private static void CompileRefAssignExpr(Ctx ctx, RefAssignExpr rae)
    {
            // `F(..) = 7;`: the call answers a REFERENCE (its node carries WantsRefResult, so the emission below
            // pushes the marker), the value is evaluated, and ONE helper performs the write (the same
            // interpreter method the tree's arm asks, so the refusal's wording cannot drift).
            // The callee const goes FIRST (the VM pops the args from the top and then the callee), then the
            // reference, the value, and the position the refusal reports.
            EmitLoadConst(ctx, Interpreter.RefAssignHelperName);
            CompileExpr(ctx, rae.CellSource);
            CompileExpr(ctx, rae.Value);
            EmitLoadConst(ctx, rae.Line);
            EmitLoadConst(ctx, rae.Column);
            ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(4)]);
            return;
    }

    private static void CompileCallExpr(Ctx ctx, CallExpr ce)
    {
            // A call that NAMES ITS TYPE ARGUMENTS cannot be compiled, for ANY callee shape a CallExpr
            // reaches (a global name, a local holding the function, or an expression): the compiler emits
            // NOTHING for them, and the marker exists for CALL_METHOD and for `new` only. Measured, file
            // against its stripped bundle: `Identity<long>(42)` answered `typeof` "long" from the file and
            // "int" from the bundle, and `var f = Identity; f<long>(42)` the same, because the bundle ran
            // the generic TEMPLATE (no coercion) while the file's tree walk read the arguments off the AST.
            // Declining keeps a source run exactly as it is (the tree already resolved this shape) and
            // makes a stripped bundle REFUSE the file by name instead of shipping a silently different
            // answer, which is the outcome this engine refuses.
            if (ce.TypeArgs is { Count: > 0 }) { ctx.Success = false; return; }
            // `typeof(<a LOCAL with a declared type>)` is answered from the DECLARATION, as a const. The runtime
            // helper reads a BINDING, and a compiled body's local is a VM SLOT with none, so the answer fell back
            // to the value's type: measured inside a function, `long? n = 3; typeof(n)` answered "long" on BOTH
            // legs (a function body is compiled either way), against the same declaration answering "long?" at the
            // top level. Only a name this body holds WITH a recorded declaration takes this path, so `var`, an
            // explicit `object` and a parameter keep the helper's own answer (the two pinned limits).
            if (TryEmitTypeofLocalDeclaration(ctx, ce)) return;
            // Named arguments: a bare-name GLOBAL callee compiles to the VM, the args are
            // emitted in SOURCE order under a NamedArgsMarker const and the VM reorders them
            // against the callee's declared param names (mirrors the tree's ReorderNamedArgs).
            // Local/param callees, method-context bare names, and ref/out-arg calls fall back
            // to the tree (the VM can't resolve those callees, or would misalign ref cells).
            if (ce.ArgNames is { Count: > 0 })
            {
                if (ce.Callee is VariableExpr cvNamed && !ctx.LocalMap.ContainsKey(cvNamed.Name)
                    && !(ctx.IsMethod && !ctx.IsBuiltin(cvNamed.Name)))
                {
                    var hasRefArg = false;
                    if (ce.IsRefArgs != null)
                        foreach (var r in ce.IsRefArgs) if (r) { hasRefArg = true; break; }
                    if (!hasRefArg && ce.IsOutArgs != null)
                        foreach (var o in ce.IsOutArgs) if (o) { hasRefArg = true; break; }
                    if (hasRefArg) { ctx.Success = false; return; }
                    // Stack: [marker, calleeName, arg0, …, argN-1]. The marker is pushed FIRST
                    // (deepest) so the VM's "pop args, then pop callee" sequence leaves it as
                    // the new top; the CALL operand counts ARGS ONLY (never the marker).
                    EmitRefResultMarker(ctx, ce);
                    EmitLoadConst(ctx, new NamedArgsMarker(ce.ArgNames));
                    EmitLoadConst(ctx, cvNamed.Name);
                    for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                    ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(ce.Arguments.Count)]);
                    return;
                }
                // Named args on a LOCAL/PARAM-held callee (a function or delegate value in a
                // variable, e.g. `var f = greet; f(who: "x")`): push the marker below the
                // VALUE and CALL_VAL, the VM reorders against the resolved callee's declared
                // params at runtime (see the CALL_VAL handler).
                if (ce.Callee is VariableExpr cvLocal && ctx.LocalMap.ContainsKey(cvLocal.Name))
                {
                    var hasRefArg = false;
                    if (ce.IsRefArgs != null)
                        foreach (var r in ce.IsRefArgs) if (r) { hasRefArg = true; break; }
                    if (!hasRefArg && ce.IsOutArgs != null)
                        foreach (var o in ce.IsOutArgs) if (o) { hasRefArg = true; break; }
                    if (hasRefArg) { ctx.Success = false; return; }
                    EmitRefResultMarker(ctx, ce);
                    EmitLoadConst(ctx, new NamedArgsMarker(ce.ArgNames));
                    CompileExpr(ctx, cvLocal);
                    for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                    ctx.Code.AddRange([(byte)Opcode.CALL_VAL, ctx.Count8(ce.Arguments.Count)]);
                    return;
                }
                ctx.Success = false;   // named args on a method/ref-out callee → tree
                return;
            }
            // Push callee name as constant; VM resolves builtin OR user function
            if (ce.Callee is VariableExpr cv3)
            {
                // Self-recursion is VM-safe: deep non-tail recursion hits MaxCallDepth (catchable),
                // and tail calls loop in the VM (no C# stack growth).
                // Calling a param/local by name can't be resolved from the global scope, but the
                // VALUE can be pushed and invoked via CALL_VAL (InvokeCallableValue handles
                // ScriptFunction / ScriptDelegateWrapper / Delegate / CombinedDelegate; a
                // non-callable value makes the VM fall back and the tree raises its error).
                if (ctx.LocalMap.ContainsKey(cv3.Name))
                {
                    EmitRefResultMarker(ctx, ce);
                    EmitArgFlags(ctx, ce);
                    CompileExpr(ctx, cv3);
                    for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                    ctx.Code.AddRange([(byte)Opcode.CALL_VAL, ctx.Count8(ce.Arguments.Count)]);
                    return;
                }
                // Inside a CLASS METHOD, a bare-name callee that names one of the class's own methods is
                // by the tree (self-recursion, sibling methods, static lambda fields) when
                // `this` is a ClassInstance. The VM dispatches an implicit-this call
                // (`Method(...)` ≡ `this.Method(...)`) via CALL_METHOD on the `this` local,
                // mirroring the tree's FindMethodInHierarchy on _currentClass. fieldNames != null
                // distinguishes class methods from struct methods (struct bare calls are invalid,
                // the tree errors "Undefined variable").
                // A BARE CALL WHOSE NAME IS THE CLASS'S OWN EVENT is an INVOKE OF THE DELEGATE that event holds
                // (`public event Action OnBroadcast;` … `OnBroadcast();`). The tree resolves it through
                // _currentClass; the VM has no such notion, so this compiled to a plain CALL, the const NAME
                // arrived as the callee and the arm bailed ("not a valid callee", measured on
                // crash_out_ref_stress), which refuses a bundle's whole entry. DECLINING the body is this
                // compiler's rule for a shape it cannot model: the tree runs the member and resolves the event.
                if ((ctx.IsMethod || ctx.IsLambda) && ctx.EventNames is { Count: > 0 } classEvents
                    && classEvents.Contains(cv3.Name))
                {
                    ctx.Success = false;
                    return;
                }
                if (IsInstanceMethodContextBareCall(ctx, cv3.Name))
                {
                    // A want-reference bare call resolves through the class's own method, i.e. CALL_METHOD,
                    // whose dispatch dereferences the cell: the tree keeps that shape (see CompileExpr).
                    if (ce.WantsRefResult) { ctx.Success = false; return; }
                    var hasRef = ce.IsRefArgs != null && ce.IsRefArgs.Any(r => r)
                        || ce.IsOutArgs != null && ce.IsOutArgs.Any(r => r);
                    if (hasRef) { ctx.Success = false; return; }
                    ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                    for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                    EmitLoadConst(ctx, cv3.Name);
                    ctx.Code.AddRange([(byte)Opcode.CALL_METHOD, ctx.Count8(ce.Arguments.Count)]);
                    return;
                }
                // STATIC method bare callee (`Factorial(n-1)` inside a static method): the tree
                // resolves it via _currentClass.StaticFields, dispatch through the ClassInfo
                // value (`ClassName.Method(...)`), which the VM's CALL_METHOD handles. The class
                // name resolves via LOAD_GLOBAL_FAST (the ClassInfo is in the global scope), not
                // a string const.
                if ((ctx.IsMethod || ctx.IsLambda) && ctx.ClassName != null
                    && ctx.StaticMethodNames is { Count: > 0 } && ctx.StaticMethodNames.Contains(cv3.Name))
                {
                    if (ce.WantsRefResult) { ctx.Success = false; return; }   // same reason: CALL_METHOD derefs
                    var hasRef = ce.IsRefArgs != null && ce.IsRefArgs.Any(r => r)
                        || ce.IsOutArgs != null && ce.IsOutArgs.Any(r => r);
                    if (hasRef) { ctx.Success = false; return; }
                    ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(ctx.ClassName)]);
                    for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                    EmitLoadConst(ctx, cv3.Name);
                    ctx.Code.AddRange([(byte)Opcode.CALL_METHOD, ctx.Count8(ce.Arguments.Count)]);
                    return;
                }
                // `typeof(<integral expression>)` is resolved HERE, at compile time, because the VM's
                // integral values are long slots: a computed `int + int` boxes System.Int64, so the runtime
                // typeof builtin named it "long" while the tree, which computes in CLR int, named it
                // "int". typeof is a compile-time operator in the language it mirrors, so the static answer
                // is the right one, and for every shape resolved here it is also the answer the tree gives
                // (measured; see vm_typeof_parity.slc). Anything not resolvable keeps the runtime call,
                // which is what preserves parity for object-typed operands, the tree names those by their
                // runtime type, so both engines must ask at runtime.
                if (cv3.Name == "typeof" && ce.Arguments.Count == 1
                    && Ctx.StaticNumericTypeName(ctx, ce.Arguments[0]) is { } staticTypeName)
                {
                    EmitLoadConst(ctx, staticTypeName);
                }
                else
                {
                    // A `ref`/`out` argument the callee cannot honour: a flag inside its `params` tail (a fresh
                    // array of by-value elements, refused by AssembleParamsTail) or a flag at a parameter it does
                    // not declare by reference (C# CS1615, refused by BindParam). The VM can see NEITHER: bytecode
                    // carries no per-argument flags and evaluating an argument yields a VALUE, so the flag was
                    // dropped silently (measured: `Sum(ref a, 2, 3)` returned 6; `SetIt(ref x)` left the caller's
                    // `x` alone). Whether a position is declared by reference is the CALLEE's business, so the
                    // gate needs its declaration: decline the body and let the tree refuse it. This is the ONE
                    // callee shape whose ref/out decline was missing (a bare call to a global function): every
                    // sibling branch above (named args, method context, static method) already declines.
                    // The gate asks the WHOLE SET of declarations for that name (RefOutArgUnhonourable), since
                    // a free function is an overload set: it declines only when NO member can honour the call
                    // as written. And the call site's FLAGS now travel with the call (EmitArgFlags below), so
                    // the VM's overload picker can weigh `ref`-ness and pick the member the flags fit.
                    if (ctx.CalleeDecls != null && ctx.CalleeDecls.TryGetValue(cv3.Name, out var calleeSet)
                        && RefOutArgUnhonourable(ce, calleeSet))
                    { ctx.Success = false; return; }
                    if (RefOutArgNeedsTree(ce, ctx)) { ctx.Success = false; return; }
                    (ctx.BareCalls ??= []).Add(cv3.Name);
                    // A call carrying `ref`/`out` arguments pushes the ArgFlagsMarker at the BOTTOM of its
                    // layout ([marker, calleeName, args…]), so the VM can tell which positions the author
                    // wrote with a keyword: its own K=4 argument only says "came from a local slot", which a
                    // plain by-value local pushes too. Nothing is emitted for an ordinary call.
                    EmitRefResultMarker(ctx, ce);
                    if (HasRefOrOutArgs(ce)) EmitArgFlags(ctx, ce);
                    EmitLoadConst(ctx, cv3.Name);
                    for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                    ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(ce.Arguments.Count)]);
                }
            }
            else
            {
                // Expression callee (e.g. getFn()(), fns[0]()), push the callee value, CALL_VAL.
                if (RefOutArgNeedsTree(ce, ctx)) { ctx.Success = false; return; }
                EmitArgFlags(ctx, ce);
                CompileExpr(ctx, ce.Callee);
                for (var ai = 0; ai < ce.Arguments.Count; ai++) ctx.CompileArgument(ce, ai);
                ctx.Code.Add((byte)Opcode.CALL_VAL);
                ctx.Code.Add(ctx.Count8(ce.Arguments.Count));
            }
            return;
    }

    private static void CompileCompoundAssignExpr(Ctx ctx, CompoundAssignExpr ca)
    {
            if (ca.Target is not VariableExpr cv)
            { CompileCompoundMember(ctx, ca); return; }
            var caIsGlobal = !ctx.LocalMap.ContainsKey(cv.Name);
            // Bare instance-field compound (`X += v` ≡ `this.X += v`), read this.X, apply
            // the op, store back. Only for non-??= (??= on a field would need a null check
            // against the instance member; rare, falls through to the member path below).
            var caIsField = caIsGlobal && ctx.IsMethod && ctx.FieldNames is { Count: > 0 } && ctx.FieldNames.Contains(cv.Name);
            if (caIsField && ca.Operator.Type != TokenType.QuestionEqual)
            {
                // this.X = this.X <op> value. SET_PROP pops (value, target, name) with value
                // on TOP, so compute the new value, park it in a temp local, then emit
                // [this, name, temp] (target deepest, value top). Reusing the temp across
                // the store keeps the value's own expression side effects once.
                var caFieldTmp = ctx.AllocTemp();
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                EmitLoadConst(ctx, cv.Name);
                ctx.Code.Add((byte)Opcode.GET_PROP);
                CompileExpr(ctx, ca.Value);
                var caOpF = ca.Operator.Type switch
                {
                    TokenType.PlusEqual => (byte)Opcode.ADD,
                    TokenType.MinusEqual => (byte)Opcode.SUB,
                    TokenType.StarEqual => (byte)Opcode.MUL,
                    TokenType.SlashEqual => (byte)Opcode.DIV,
                    TokenType.PercentEqual => (byte)Opcode.MOD,
                    TokenType.AmpersandEqual => (byte)Opcode.BITAND,
                    TokenType.PipeEqual => (byte)Opcode.BITOR,
                    TokenType.CaretEqual => (byte)Opcode.BITXOR,
                    TokenType.LeftShiftEqual => (byte)Opcode.SHL,
                    TokenType.RightShiftEqual => (byte)Opcode.SHR,
                    _ => -1
                };
                if (caOpF < 0) { ctx.Success = false; return; }
                ctx.Code.Add((byte)caOpF);
                ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)caFieldTmp]);   // park new value
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx("this")]);
                EmitLoadConst(ctx, cv.Name);
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)caFieldTmp]);
                ctx.Code.Add((byte)Opcode.SET_PROP);
                return;
            }
            if (ca.Operator.Type == TokenType.QuestionEqual)
            {
                // x ??= rhs, short-circuit: if x is non-null, skip evaluating rhs entirely.
                //   LOAD x; DUP; LOAD null; EQ; JMP_IF_FALSE keep; POP; <rhs>; DUP; STORE; JMP end;
                //   keep: end:  (the copy stays: it IS the value)
                if (caIsGlobal)
                    ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(cv.Name)]);
                else
                    ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx(cv.Name)]);
                ctx.Code.Emit(Opcode.DUP);
                EmitLoadConst(ctx, null);
                ctx.Code.Add((byte)Opcode.EQ);
                ctx.Code.Add((byte)Opcode.JMP_IF_FALSE);   // x != null → keep x
                var qeKeep = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                ctx.Code.Emit(Opcode.POP);            // drop the null x copy
                CompileExpr(ctx, ca.Value);                // <rhs>, only when x is null
                                                           // The assignment VALUE must survive the store: an expression statement emits exactly one
                                                           // POP to discard it, and a nested use (z = x ??= (y ??= v)) reads it. The store opcodes
                                                           // pop their value, so it is DUPlicated here; the keep path already holds the copy,
                                                           // because JMP_IF_FALSE takes the boolean and leaves it.
                ctx.Code.Emit(Opcode.DUP);
                if (caIsGlobal)
                    ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(cv.Name)]);
                else
                    ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(cv.Name)]);
                ctx.Code.Add((byte)Opcode.JMP);
                var qeEnd = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
                PatchJump(ctx, qeKeep, ctx.Code.Count);
                // keep: the DUPed copy IS the value, so nothing is popped here
                PatchJump(ctx, qeEnd, ctx.Code.Count);
                return;
            }
            if (caIsGlobal)
                ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(cv.Name)]);
            else
                ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)ctx.LocalIdx(cv.Name)]);
            CompileExpr(ctx, ca.Value);
            var caOp = ca.Operator.Type switch
            {
                TokenType.PlusEqual => (byte)Opcode.ADD,
                TokenType.MinusEqual => (byte)Opcode.SUB,
                TokenType.StarEqual => (byte)Opcode.MUL,
                TokenType.SlashEqual => (byte)Opcode.DIV,
                TokenType.PercentEqual => (byte)Opcode.MOD,
                TokenType.AmpersandEqual => (byte)Opcode.BITAND,
                TokenType.PipeEqual => (byte)Opcode.BITOR,
                TokenType.CaretEqual => (byte)Opcode.BITXOR,
                TokenType.LeftShiftEqual => (byte)Opcode.SHL,
                TokenType.RightShiftEqual => (byte)Opcode.SHR,
                _ => -1
            };
            if (caOp < 0) { ctx.Success = false; return; }
            ctx.Code.Add((byte)caOp);
            // The assignment VALUE must survive the store, for the same two reasons the ??= branch
            // above spells out: an expression statement emits exactly one POP to discard it, and a
            // nested use reads it. Measured with the generic-instantiation lift in place, the first
            // time these bodies were compiled at all: `z = (z <<= 3) + 1;` (global_recent) and an
            // `x += 1` in a loop (stress_dict_perf) each came out one value short, and the verifier
            // refused both bodies ("POP needs 1 value(s) on the operand stack, but 0 is/are there").
            ctx.Code.Emit(Opcode.DUP);
            if (caIsGlobal)
                ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(cv.Name)]);
            else
                ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)ctx.LocalIdx(cv.Name)]);
            return;
    }

    private static void CompileIsExpr(Ctx ctx, IsExpr ise)
    {
            CompileExpr(ctx, ise.Left);
            EmitLoadConst(ctx, EmittedType(ctx, ise.Type));
            ctx.Code.Add((byte)Opcode.IS_TYPE);
            return;
    }

    private static void CompileLambdaExpr(Ctx ctx, LambdaExpr le)
    {
            {
                // Capture-free lambdas run in the VM with no closure snapshot.
                // Read-only-capturing lambdas run too, captured values become hidden locals
                // ([params, this?, captures...]); MAKE_LAMBDA fills them from the enclosing locals.
                // Lambdas that WRITE a capture keep the tree RefCell path (mutation propagation).
                IReadOnlyList<Stmt> body;
                if (le.BodyExpr != null)
                    body = [ReturnStmt.Create(le.BodyExpr, le.Line, le.Column)];
                else
                    body = le.BodyStmts ?? [];

                // A lambda written inside a class member shares that member's class context: a bare sibling
                // call in its body resolves through the class (the tree reads it from the enclosing scope,
                // the VM emits `this.Method(...)`), and a bare instance field reads as `this.X`. Both need
                // `this` in the lambda's frame, exactly as an explicit `this.X` does, so it is captured
                // whenever a class context exists, which is the condition under which an implicit member
                // access inside the body can resolve at all.
                if (ctx.ClassName != null)
                {
                    le.CapturedNames ??= new List<string>();
                    if (!le.CapturedNames.Contains("this", StringComparer.OrdinalIgnoreCase))
                        le.CapturedNames.Add("this");
                }
                var isCapturing = le.CapturedNames is { Count: > 0 };
                HashSet<string>? written = null;
                List<string>? calledDeclaring = null;
                var unknownShape = false;
                if (isCapturing)
                    Ctx.AnalyzeCaptures(le, body, out written, out unknownShape, out calledDeclaring);
                // Writes to a captured local propagate via a RefCell shared with the enclosing frame
                // (the VM wraps the slot at MAKE_LAMBDA); writes to captured GLOBALS resolve live.
                if (isCapturing && unknownShape)
                {
                    ctx.Success = false;
                    return;
                }
                // A CAPTURE OF A NESTED FUNCTION NEEDS NO GUARD ANY MORE: the declaration is bound into a
                // LOCAL SLOT by the program-body arm, so it IS in LocalMap here and MAKE_LAMBDA emits a real
                // capture pair for it. This block used to DECLINE such a body, which cost 11 suites their
                // `--no-source`; measured after the slot landed, the decline is unnecessary and the strip
                // survey recovers those suites.
                //
                // A CLOSURE ABOVE A DECLARATION CAPTURES ITS SLOT, not a missing name: the slot is reserved when the
                // body starts (PreallocateDeclSlots) and the capture pair is marked WRITTEN below, so MAKE_LAMBDA
                // installs a CELL over that slot and the declaration's store fills it when it runs. That is C#'s
                // rule (a local function is in scope for the whole block) on the COMPILED path, where the tree
                // implements the same rule by binding the name at the body's start
                // (Interpreter.PrebindDeclaredFunctions). The one shape still declined is a NESTED DECLARATION
                // capturing a sibling it has not reached (the declaration arm), because a value pair cannot carry
                // a cell.
                var lambdaDecl = FuncDecl.Create(
                    name: "",
                    le.Parameters,
                    le.IsRefParams,
                    le.IsParams,
                    body,
                    // The permissive Object return type is deliberate (the VM skips return coercion), but it
                    // is ALSO what the delegate signature check reads, and `void` demands a void handler: a
                    // compiled `x.E += fun () { hits = hits + 1; }` was refused with "Handler signature does
                    // not match delegate 'Ping'" while the tree accepted it. A BLOCK body with no
                    // value-returning `return` ANYWHERE is VOID, which is exactly what VisitLambdaExpr
                    // declares (both read `BodyReturnsValue`, the ONE walk of that question, so the two
                    // engines cannot drift). It looks INSIDE a try, an if and a loop on purpose: reading only
                    // the body's top level declared a lambda VOID when its only `return "x"` sat in a `try`,
                    // and the engine then refused that lambda's own return ("Void function '' cannot return a
                    // value") or answered null through the engine's own invocation path.
                    le.BodyExpr == null && !BodyReturnsValue(body)
                        ? TypeInfo.Void
                        : TypeInfo.LambdaValueReturn,
                    null,
                    AccessModifier.Public,
                    le.Line,
                    le.Column,
                    isOutParams: le.IsOutParams);

                // A lambda's parameter DEFAULTS reach its declaration the same way (a property), which is
                // what the tree does too: the arity every call site reads comes from the DECLARATION.
                lambdaDecl.DefaultValues = le.DefaultValues;
                // AND EACH ONE NEEDS A COMPILED FORM, exactly as a function's does: the funnel calls this for
                // every other body, but a lambda's declaration is built HERE from its own `sub` context, so
                // without this call a --no-source bundle dropped the lambda's defaults and answered
                // "Expected between 2 and 2 arguments, got 1" for a call its file accepts (measured on
                // `var Lm = fun (string a, string b = "d") { ... }; Lm("x")`, which the file answered and the
                // stripped bundle refused). The wire already carries them (BundleFunction.DefaultThunks, via
                // BuildShell), so no format change is owed.
                CompileDefaultThunks(lambdaDecl, ctx.Builtins, ctx.CaseSensitive, ctx.CalleeDecls);

                // Tuple parameters: carry the per-param element names onto the compiled decl
                // so the shared bind path attaches them to the coerced ScriptTuple.
                lambdaDecl.TupleParamNames = le.TupleParamNames;

                // THE SAME tuple-name rules VisitLambdaExpr applies, because a lambda BUILT BY MAKE_LAMBDA
                // is the one a compiled body hands back: the declared return names, else the inference
                // fallback (a returned tuple of the same arity as the parameters takes THEIR names).
                // Measured: `var Test = (int i, string s) => { return (10, "hello"); }; Test(0, "").s`
                // answered "Property 's' not found on tuple" from a compiled body and worked from the tree
                // (_tup3, _reg, _lambdanames, _lambdatuple, t4_struct, tuple_params_test, crash_360_latest).
                lambdaDecl.TupleElementNames = le.TupleElementNames?.Select(n => n ?? "").ToList();
                if (lambdaDecl.TupleElementNames == null)
                {
                    TupleExpr? retTuple = le.BodyExpr as TupleExpr;
                    if (retTuple == null && body is [ReturnStmt { Value: TupleExpr te }]) retTuple = te;
                    if (retTuple != null && retTuple.Items.Count == le.Parameters.Count)
                        lambdaDecl.TupleElementNames = [.. le.Parameters.Select(p => p.Name)];
                }

                // A lambda whose body contains a yield is a coroutine: compiling it emits YIELD
                // bytecode, and the VM has no YIELD handler until Task 2, MAKE_LAMBDA/flat-call
                // would crash. Propagate the flags (so every invocation gate sees HasYield) and
                // refuse to synthesize it into the VM bytecode: the whole enclosing function
                // falls back to the tree, where VisitLambdaExpr builds the coroutine correctly.
                lambdaDecl.IsCoroutine = le.IsCoroutine;
                lambdaDecl.IsAsync = le.IsAsync;
                lambdaDecl.HasYield = le.IsCoroutine;
                if (le.IsCoroutine || le.IsAsync)
                {
                    ctx.Success = false;
                    return;
                }

                // Recursively compile the lambda body
                var sub = new Ctx([], [],
                    new Dictionary<object, int>(), new Dictionary<string, int>(ctx.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase),
                    [], ctx.Builtins, ctx.CaseSensitive)
                {
                    // A lambda body is a member-like context for NAME RESOLUTION (see IsLambda) and carries
                    // the enclosing member's class names with it, so a bare sibling call inside it resolves
                    // the same way it does in the member itself.
                    IsLambda = true,
                    FieldNames = ctx.FieldNames,
                    StaticFieldNames = ctx.StaticFieldNames,
                    EventNames = ctx.EventNames,
                    ClassName = ctx.ClassName,
                    StaticMethodNames = ctx.StaticMethodNames,
                    InstanceMethodNames = ctx.InstanceMethodNames,
                    // A LAMBDA INSIDE A GENERIC BODY resolves that body's type parameters too: the map travels
                    // with the sub-context, or `var f = fun(object x) { return x is T; }; return f(o);`
                    // answers False while the same `is T` written directly in the body answers right
                    // (measured, matrix position 8).
                    TypeParamBindings = ctx.TypeParamBindings,
                    // The CALLEE DECLARATIONS too, so a `ref`/`out` argument the callee cannot honour makes
                    // the lambda body DECLINE, exactly as it does in a function body. Without them the
                    // sub-context had no declaration to ask, the flag was compiled as a plain by-value
                    // argument and the binding was dropped silently: measured, `SumTail(ref a, 2, 3)` and
                    // `IdIt(ref x)` inside a LAMBDA returned a value where the tree REFUSES them, while the
                    // same calls in a FUNCTION refused correctly (vm_params 7a/7e, 8a).
                    CalleeDecls = ctx.CalleeDecls,
                };
                foreach (var p in le.Parameters)
                    if (!sub.LocalMap.ContainsKey(p.Name)) { sub.LocalMap[p.Name] = sub.Locals.Count; sub.Locals.Add(p.Name); }
                // Capturing lambda layout: [params, this?, read-only-local-captures...]
                // Captured GLOBALS are NOT registered, the body resolves them live (LOAD_GLOBAL_FAST/STORE_GLOBAL_FAST).
                if (isCapturing && le.CapturedNames!.Contains("this", StringComparer.OrdinalIgnoreCase))
                {
                    sub.LocalMap["this"] = sub.Locals.Count; sub.Locals.Add("this");
                }
                if (isCapturing)
                    foreach (var cn in le.CapturedNames!)
                        if (cn != "this" && ctx.LocalMap.ContainsKey(cn) && !sub.LocalMap.ContainsKey(cn))
                        { sub.LocalMap[cn] = sub.Locals.Count; sub.Locals.Add(cn); }
                // THE NESTED DECLARATIONS THIS LAMBDA BODY DECLARES get their slot before any statement compiles,
                // exactly as CompileStmtList does for a block, so a closure inside this body that was written above
                // one of them captures the slot and the declaration fills it.
                PreallocateDeclSlots(sub, body);
                foreach (var s in body) CompileStmt(sub, s);
                // A lambda that CALLS the name of the `var` it is being assigned to is the one shape where a
                // compiled lambda and an interpreted one disagree, so it stays interpreted, like the tree's.
                // A compiled lambda captures a CELL that the enclosing store fills on completion, so a recursive
                // call resolves; an interpreted one reads the snapshot taken at creation, where the binding does
                // not exist yet, and raises "Undefined variable". Measured on the reference repro below: the
                // one-liner, the named `fun`, and the binding declared earlier in the SAME statement all answer
                // `Undefined variable` on the tree and succeed with the top-level path on.
                // A plain READ of the same name is NOT that shape and must not decline: `menu_test` (a lambda
                // calling `shower.Stop()` where `shower` is the `var` being declared), `scheduler_sample`
                // (`task2.IsRunning`) and `ffmpeg_test` (`process.Kill()`) all read it, and all three behave the
                // same on both legs, so declining them only cost their bundles the source. The bisect that
                // separated the two shapes is in the plan's item 6.
                if (ctx.DeclaringVar is { Length: > 0 } declaring && calledDeclaring is { Count: > 0 } calledNames
                    && calledNames.Any(n => n != null && string.Equals(n, declaring,
                           ctx.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)))
                    sub.Success = false;
                if (sub.Success && (body.Count == 0 || body[^1] is not ReturnStmt))
                    sub.Code.Add((byte)Opcode.RETURN);
                if (sub.Success)
                {
                    lambdaDecl.Compiled = Seal(sub, sub.Code, sub.Consts, sub.Locals);
                    ctx.NestedLambdas.Add(lambdaDecl);
                    if (sub.NestedDecls is { Count: > 0 } sd) (ctx.NestedDecls ??= []).AddRange(sd);
                    // Capture source indices: "this" (if captured) + LOCAL captures (read-only AND
                    // written, written captures are marked so MAKE_LAMBDA can share a RefCell).
                    // Captured GLOBALS are excluded, the lambda body resolves them live via FAST slots.
                    var capIdx = new List<int>();
                    var capWritten = new List<bool>();
                    if (isCapturing)
                    {
                        if (le.CapturedNames!.Contains("this", StringComparer.OrdinalIgnoreCase))
                        { capIdx.Add(ctx.LocalIdx("this")); capWritten.Add(false); }

                        foreach (string cn in le.CapturedNames!.Where(cn => cn != "this" && ctx.LocalMap.ContainsKey(cn)))
                        {
                            capIdx.Add(ctx.LocalIdx(cn));
                            // A declaration this body has not reached yet is captured as a CELL even though the
                            // lambda only reads it: the ENCLOSING body writes that slot later (the declaration's
                            // store), and MAKE_LAMBDA is the only place a shared cell can be installed, so the
                            // write goes through it and the closure reads the function when it is called.
                            capWritten.Add((written != null && written.Contains(cn, StringComparer.OrdinalIgnoreCase))
                                || IsPendingDecl(ctx, cn));
                        }
                    }
                    ctx.Code.Add((byte)Opcode.MAKE_LAMBDA);
                    ctx.Code.Add(ctx.Count8(ctx.NestedLambdas.Count - 1));
                    ctx.Code.Add(ctx.Count8(capIdx.Count));
                    for (var ci = 0; ci < capIdx.Count; ci++)
                    {
                        ctx.Code.Add((byte)capIdx[ci]);
                        ctx.Code.Add(capWritten[ci] ? (byte)1 : (byte)0);
                    }
                    return;
                }
                ctx.Success = false;
                return;
            }
    }

    private static void CompileTupleExpr(Ctx ctx, TupleExpr tup)
    {
            {
                // Push items left-to-right, then MAKE_TUPLE with the count.
                foreach (var it in tup.Items) CompileExpr(ctx, it);
                ctx.Code.AddRange([(byte)Opcode.MAKE_TUPLE, ctx.Count8(tup.Items.Count)]);
                return;
            }
    }

    private static void CompileListLiteralExpr(Ctx ctx, ListLiteralExpr lle)
    {
            {
                CompileListLiteral(ctx, lle, lle.ElementType);
                return;
            }
    }

    private static void CompileDictLiteralExpr(Ctx ctx, DictLiteralExpr dle)
    {
            {
                foreach (var (k, v) in dle.Entries) { CompileExpr(ctx, k); CompileExpr(ctx, v); }
                ctx.Code.AddRange([(byte)Opcode.MAKE_DICT, ctx.Count8(dle.Entries.Count)]);
                return;
            }
    }

    private static void CompileNullExpr(Ctx ctx)
    {
            EmitLoadConst(ctx, null);
            return;
    }

    private static void CompileArrayAccessExpr(Ctx ctx, ArrayAccessExpr ae)
    {
            {
                CompileExpr(ctx, ae.Array);
                if (ae.Indices is [RangeExpr re])
                {
                    // Slice: push From (or null), To (or null)
                    if (re.From != null) CompileExpr(ctx, re.From);
                    else EmitLoadConst(ctx, null);
                    if (re.To != null) CompileExpr(ctx, re.To);
                    else EmitLoadConst(ctx, null);
                    ctx.Code.Add((byte)Opcode.SLICE);
                }
                else if (ae.Indices.Count == 1 && ae.Indices[0] is not RangeExpr)
                {
                    // Fast single-index path (ARRAY_GET): unboxed numeric index + CLR-array
                    // dispatch in the VM, avoids boxing the index and per-element ToObject.
                    CompileExpr(ctx, ae.Indices[0]);
                    ctx.Code.Add((byte)Opcode.ARRAY_GET);
                }
                else
                {
                    foreach (var idx in ae.Indices) CompileExpr(ctx, idx);
                    ctx.Code.Add((byte)Opcode.GET_INDEX);
                    ctx.Code.Add(ctx.Count8(ae.Indices.Count));
                }
                return;
            }
    }

    private static void CompileArrayAssignExpr(Ctx ctx, ArrayAssignExpr aae)
    {
            {
                CompileExpr(ctx, aae.Array);
                foreach (var idx in aae.Indices) CompileExpr(ctx, idx);
                CompileExpr(ctx, aae.Value);
                ctx.Code.Add((byte)Opcode.SET_INDEX);
                ctx.Code.Add(ctx.Count8(aae.Indices.Count));
                return;
            }
    }

    private static void CompilePropertyAccessExpr(Ctx ctx, PropertyAccessExpr pae)
    {
            {
                CompileExpr(ctx, pae.Target);
                EmitLoadConst(ctx, pae.PropertyName);
                ctx.Code.Add((byte)Opcode.GET_PROP);
                return;
            }
    }

    private static void CompilePropertyAssignExpr(Ctx ctx, PropertyAssignExpr pase)
    {
            {
                // A member write whose TARGET is an indexer asks the ENGINE for the target instead of
                // declining, because only the runtime knows which container this is: a `List<T>` indexer is
                // NOT a variable, so `list[0].X = 42` on a struct element is refused (CS1612) while a class
                // element is written through and an ARRAY element is a variable that may be modified. The
                // helper runs the same rule the tree runs and answers the ELEMENT for SET_PROP, and it is
                // called BEFORE the value expression, which is the tree's own order (the tree refuses
                // before evaluating anything), so a refused write never runs its value expression.
                if (pase.Target is ArrayAccessExpr tae && IsCheckedIndexerTarget(tae))
                {
                    EmitLoadConst(ctx, IndexedWriteTargetHelperName);
                    CompileExpr(ctx, tae.Array);
                    CompileExpr(ctx, tae.Indices[0]);
                    EmitLoadConst(ctx, tae.Line);
                    EmitLoadConst(ctx, tae.Column);
                    ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(4)]);
                    EmitLoadConst(ctx, pase.PropertyName);
                    CompileExpr(ctx, pase.Value);
                    ctx.Code.Add((byte)Opcode.SET_PROP);
                    return;
                }
                // Any other indexer shape is compiled the ORDINARY way below, which is what the tree does for
                // it too: its CS1612 check resolves only a plain variable container with a literal or
                // variable index, so for every other shape it runs NO check and takes the element as the
                // receiver, which is exactly this sequence. Declining here was wrong in the other direction:
                // it made `--no-source` refuse a program the source run executes, and the generated skeleton
                // (`form['textbox1'].Click = ...`) is that shape.
                CompileExpr(ctx, pase.Target);
                EmitLoadConst(ctx, pase.PropertyName);
                CompileExpr(ctx, pase.Value);
                ctx.Code.Add((byte)Opcode.SET_PROP);
                return;
            }
    }

    private static void CompileMethodCallExpr(Ctx ctx, MethodCallExpr mce)
    {
            {
                // A ref/out argument HAS a bytecode form now (the same three shapes a function call emits:
                // a local slot reference, or a cell over a global), so the arm asks the one question that
                // decides it rather than declining the whole body. Measured: the four ordinary shapes
                // (`Interlocked.Increment(ref counter)`, `ds.TryGetValue("b", out v)`, `c3.Bad(out v3)`,
                // `"abc".SetIt(out outSlot)`) all run correctly from source, and this arm was the only
                // reason they could not be stripped.
                if (RefOutArgNeedsTree(mce, ctx)) { ctx.Success = false; return; }
                // A NAMED call whose argument is also ref/out keeps declining: the marker's reorder happens
                // in the VM against the resolved method's declared parameters, and a cell must not travel
                // through a reorder (the names would meet cells, not values). Rare enough to leave on the
                // tree, and refusing is what the one implementation of that reorder already assumes.
                if (mce.ArgNames is { Count: > 0 })
                {
                    for (var i = 0; i < mce.Arguments.Count; i++)
                        if (IsRefOrOutArg(mce, i)) { ctx.Success = false; return; }
                }
                // A GENERIC call with a ref/out argument keeps declining too: the marker for the call SITE's
                // keyword (below) is read straight after the receiver, and the type-arguments marker would
                // be sitting on top of it instead. The tree handles the shape, which is rare enough.
                if (mce.TypeArgs is { Count: > 0 })
                {
                    for (var i = 0; i < mce.Arguments.Count; i++)
                        if (IsRefOrOutArg(mce, i)) { ctx.Success = false; return; }
                }
                // The call site's KEYWORD, which the bytecode otherwise loses for a local target: the bind
                // judges a `ref`-at-`out` swap by it, and for a method there is no declaration to compare
                // against at compile time (a bare call has one, which is why the function path declines a
                // mismatch here instead of at run time). Pushed at the BOTTOM of the layout, like the other
                // two markers.
                EmitArgFlags(ctx, mce.Arguments.Count, mce.IsRefArgs, mce.IsOutArgs);
                // Type arguments travel the way named arguments do, a marker const at the BOTTOM of the call
                // layout, since the bytecode has no other slot for them. They must NOT be dropped: compiled
                // without them, `Array.Empty<int>()` resolved the OPEN definition (a guaranteed CLR failure)
                // and `Array.Empty<ComNode>()` would have closed as Array.Empty<object>(). The CALL_METHOD
                // handler pops the marker and passes the arguments to the interpreter's dispatch, the ONE
                // place that closes a generic definition with the script's own types.
                if (mce.TypeArgs is { Count: > 0 })
                    EmitLoadConst(ctx, new TypeArgsMarker(mce.TypeArgs));
                // Named args: push a NamedArgsMarker BELOW the receiver so the VM reorders
                // against the resolved method's params (see the CALL_METHOD handler). The RECEIVER is computed
                // first and parked in a TEMP LOCAL, because a marker must never sit directly under another
                // instruction's operand: whatever the receiver's own emission does last would find THIS
                // marker below its operands and act on it. Measured: `new StringBuilder().Append(value: "x")`
                // emitted [marker, CALL_NEW, args, name], so CALL_NEW read the marker as a named-CTOR marker,
                // found no script ctor params for a CLR type, and returned VmFallback, which aborted a
                // stripped bundle's program body. An ARGUMENT holding a `new` is safe either way, because the
                // marker then sits several slots lower rather than immediately under that operand.
                if (mce.ArgNames is { Count: > 0 })
                {
                    var namedReceiver = ctx.AllocTemp();
                    CompileExpr(ctx, mce.Receiver);
                    ctx.Code.AddRange([(byte)Opcode.STORE_VAR, (byte)namedReceiver]);
                    EmitLoadConst(ctx, new NamedArgsMarker(mce.ArgNames));
                    ctx.Code.AddRange([(byte)Opcode.LOAD_VAR, (byte)namedReceiver]);
                    for (var ai = 0; ai < mce.Arguments.Count; ai++)
                        ctx.CompileArgument(mce.Arguments, mce.IsRefArgs, mce.IsOutArgs, ai);
                    EmitLoadConst(ctx, mce.MethodName);
                    ctx.Code.Add((byte)Opcode.CALL_METHOD);
                    ctx.Code.Add(ctx.Count8(mce.Arguments.Count));
                    return;
                }
                // A call whose result is used as a REFERENCE (`box.F() = 7;`, `ref int r = ref box.F();`): the
                // marker goes directly under the RECEIVER, so it is the stack top once the callee's name,
                // arguments and receiver are popped, and the VM then asks the dispatch for the CELL instead of
                // inlining the method (the flat swap dereferences a ref return). The two shapes whose layout puts
                // ANOTHER marker under the receiver (named arguments, type arguments) keep declining.
                if (mce.WantsRefResult)
                {
                    if (mce.ArgNames is { Count: > 0 } || mce.TypeArgs is { Count: > 0 })
                    {
                        ctx.Success = false;
                        return;
                    }
                    EmitLoadConst(ctx, RefResultMarker.Instance);
                }
                CompileExpr(ctx, mce.Receiver);
                for (var ai = 0; ai < mce.Arguments.Count; ai++)
                    ctx.CompileArgument(mce.Arguments, mce.IsRefArgs, mce.IsOutArgs, ai);
                EmitLoadConst(ctx, mce.MethodName);
                // No finer LOC here: a refusal reports the STATEMENT (see Refusal), and the statement's
                // own LOC is already the current position, so this call needs none and a bundle carries
                // five fewer bytes per call.
                ctx.Code.Add((byte)Opcode.CALL_METHOD);
                ctx.Code.Add(ctx.Count8(mce.Arguments.Count));
                return;
            }
    }

    private static void CompileNewInstanceExpr(Ctx ctx, NewInstanceExpr nie)
    {
            {
                // A ref/out CTOR argument HAS a bytecode form now, the same three shapes a function or
                // method call emits (a local slot reference, or a cell over a global built by the engine's
                // helper), and the ctor's own bind takes the cell: InstantiateClass's bind loop reads the
                // DECLARATION's ref/out flags and BindParam writes through, which is why the method-ref
                // round's machinery is all this needed. Two combinations keep declining, for the reasons
                // the METHOD arm records, and both are marker-ordering questions: a NAMED `new` with a
                // ref/out argument (the reorder would meet cells) and one whose TYPE ARGUMENTS carry a
                // marker (the flags marker has to sit under it).
                var anyCtorRef = nie.IsRefArgs != null && nie.IsRefArgs.Any(b => b);
                var anyCtorOut = nie.IsOutArgs != null && nie.IsOutArgs.Any(b => b);
                if (anyCtorRef || anyCtorOut)
                {
                    // The SAME question the function and method arms ask, and for the same reason: only a
                    // VARIABLE has a cell shape the VM can carry, so a discard, a field, an element or a
                    // name this body never declares stays the tree's job (measured: `new Animal(5)` at a
                    // `ref int` parameter bailed from a compiled body, which a BUNDLE cannot recover from,
                    // where the check declines the body and the file carries its source instead).
                    if (RefOutArgNeedsTree(nie.Arguments ?? [], nie.IsRefArgs, nie.IsOutArgs, ctx))
                    { ctx.Success = false; return; }
                    if (nie.ArgNames is { Count: > 0 }) { ctx.Success = false; return; }
                    if (nie.TypeInfo?.GenericArgs is { Count: > 0 }) { ctx.Success = false; return; }
                    // The call SITE's keyword, at the BOTTOM of the layout (see ArgFlagsMarker), like the
                    // named and type-args markers: the bind judges a `ref`-at-`out` swap by it, and a ctor
                    // has no declaration the compiler could compare against.
                    EmitArgFlags(ctx, nie.Arguments?.Count ?? 0, nie.IsRefArgs, nie.IsOutArgs);
                }
                // A BAG `new` (List / Dictionary / HashSet) with an argument USED to be declined here,
                // because the VM resolved those types by the argument's VALUE against .NET's constructors:
                // `new List<int>([1, 2])` came out EMPTY and `new List<int>("x")` answered a CLR framing
                // instead of the tree's refusal. The VM's bag arms now ask the SAME helper the tree's arms
                // ask (CreateBagForNew: a capacity, or a collection to fill from, with the tree's own
                // refusal for anything else), so there is nothing left to decline, and the decline this
                // comment used to justify is gone with it.
                string? newTypeName;
                if (nie.TypeInfo == null)
                {
                    // No-paren object-init (new Foo { ... }) builds the inner NewInstanceExpr with
                    // TypeInfo == null, derive the type name from the constructor expr instead of
                    // falling back to the tree.
                    var ctorName = nie.ConstructorExpr switch
                    {
                        VariableExpr ve => ve.Name,
                        _ => null
                    };
                    if (ctorName == null) { ctx.Success = false; return; }
                    newTypeName = ctorName;
                }
                else
                {
                    // A generic instantiation carries its type arguments in the SAME marker a generic
                    // METHOD call uses: the marker sits below the type name, the VM CALL_NEW pops it
                    // and closes the type through the interpreter own resolution. Pushed HERE so it
                    // ends up under the named-args marker emitted below, the order the handler reads.
                    // This stayed a DECLINE until the three divergences it first exposed were fixed, and each of them
                    // was measured with the lift in place: `typeof` of a value handed to a callee read RefCell on the VM,
                    // because LOAD_REF pushed the cell where the CALL contract says the VALUE (fixed in Vm.cs); a nested
                    // ??= chain emitted a body one value short for the outer store, which the verifier refused,
                    // correctly; and a generic SCRIPT class was looked up on the OPEN class kept under the plain name,
                    // so its instance had no fields ("Field 'X' not found in class 'Vec'", the template name, while the
                    // tree ran the same code). That last one is why the template is closed BEFORE the name lookups in
                    // InstantiateForVm, and why an argument that MENTIONS a type parameter still declines below: its
                    // binding lives where the tree closes the value (a method clone, a class instance), not in a VM
                    // opcode. Corpus after landing: 431 suites, 351 program bodies compiled where 292 compiled before,
                    // 0 refused.
                    if (nie.TypeInfo.GenericArgs is { Count: > 0 })
                    {
                        var mentionsParameter = false;
                        foreach (var ta in nie.TypeInfo.GenericArgs)
                            if (ta != null && MentionsTypeParameter(ta)) { mentionsParameter = true; break; }
                        // A PROGRAM BODY cannot sit inside a generic declaration, so a type argument
                        // there can never BE a type parameter. `TypeInfo.IsGenericParameter` cannot tell
                        // the two apart (it means "has a name, and is not an array, nullable or generic"),
                        // so `new List<Control>()`, `new List<UiEntry>()` and any other instantiation over
                        // a CLASS read as "mentions a type parameter" and declined the whole body: measured
                        // with `--compile --no-source`, `List<string>` stripped while `List<Control>`,
                        // `List<ScriptClass>` and `Dictionary<string, ScriptClass>` all refused with
                        // "the top level did not compile (while compiling a NewInstanceExpr)".
                        // A type argument can only BE a type parameter when the declaration being
                        // compiled declares one. `TypeInfo.IsGenericParameter` cannot tell the two apart
                        // (it means "has a name, and is not an array, nullable or generic"), so
                        // `new List<Control>()`, `new List<UiEntry>()` and `Dictionary<string, ScriptClass>`
                        // all read as "mentions a type parameter" and declined the WHOLE body: measured with
                        // --compile --no-source, `List<string>` stripped while those three refused with
                        // "the top level did not compile (while compiling a NewInstanceExpr)". A program body
                        // declares none, so it can never hit this; a plain function is answered from its own
                        // list; a METHOD keeps declining, because its enclosing class may declare the
                        // parameter and the VM's concrete clone is where that binding lives.
                        var realTypeParam = ctx.IsMethod;
                        if (mentionsParameter && !realTypeParam && ctx.TypeParamNames != null)
                            foreach (var ta in nie.TypeInfo.GenericArgs)
                            {
                                if (ta?.TypeName is not { } tn) continue;
                                foreach (var tp in ctx.TypeParamNames)
                                    if (string.Equals(tp, tn, StringComparison.Ordinal)) { realTypeParam = true; break; }
                                if (realTypeParam) break;
                            }
                        if (mentionsParameter && realTypeParam) { ctx.Success = false; return; }
                        EmitLoadConst(ctx, new TypeArgsMarker(nie.TypeInfo.GenericArgs));
                    }
                    newTypeName = nie.TypeInfo.TypeName ?? TypeTokenHelper.ScriptNameOf(nie.TypeInfo);
                }
                // Named ctor args: push a NamedArgsMarker BELOW the type name so the CALL_NEW
                // handler reorders against the resolved ctor's params (tree ReorderNamedArgs).
                if (nie.ArgNames is { Count: > 0 })
                    EmitLoadConst(ctx, new NamedArgsMarker(nie.ArgNames));
                EmitLoadConst(ctx, newTypeName);
                if (nie.Arguments != null)
                    for (var ai = 0; ai < nie.Arguments.Count; ai++)
                    {
                        // The ref-aware core, the same one a function and a method call use: a flagged
                        // position whose target is a local becomes LOAD_REF, one whose target is a global
                        // becomes a cell through the engine's helper, and anything else compiles as a value
                        // (the ctor's bind refuses it with the tree's own words).
                        ctx.CompileArgument(nie.Arguments, nie.IsRefArgs, nie.IsOutArgs, ai);
                        // An arg failed to compile → discard this emit; the whole function
                        // falls back to the tree-walker (Compile returns null when !Success).
                        if (!ctx.Success) return;
                    }
                // The `new` node's position, which is the tree's for "Type 'X' not defined".
                ctx.Code.Add((byte)Opcode.CALL_NEW);
                ctx.Code.Add(ctx.Count8(nie.Arguments?.Count ?? 0));
                return;
            }
    }

    private static void CompileYieldExpr(Ctx ctx)
    {
            // EXPRESSION-position yield (`var x = yield …`, `f(yield …)`, `arr[yield i]`):
            // the VM has no resume-value injection (the boundary YIELD consumes the value and
            // resumes with a clean stack), so such bodies are TREE-ONLY. Failing the compile
            // keeps the coroutine out of the VM path via Compiled == null.
            ctx.Success = false;
            return;
    }


    /// <summary>Prefix ++/-- : local variable → INC/DEC; global variable → read+ADD+store.
    /// Non-variable targets (property/index) are handled by IncrementDecrementExpr, not here.</summary>
    private static void CompileIncDecUnary(Ctx ctx, UnaryExpr ue, long delta)
    {
        if (ue.Right is VariableExpr incVar)
        {
            if (ctx.LocalMap.ContainsKey(incVar.Name))
            {
                ctx.Code.Add((byte)(delta > 0 ? Opcode.INC : Opcode.DEC));
                ctx.Code.Add((byte)ctx.LocalIdx(incVar.Name));
                return;
            }
            // Global: push new value and leave it on the stack (prefix semantics)
            ctx.Code.AddRange([(byte)Opcode.LOAD_GLOBAL_FAST, (byte)ctx.GlobalSlot(incVar.Name)]);
            EmitLoadConst(ctx, delta);
            ctx.Code.Add((byte)Opcode.ADD);
            ctx.Code.Emit(Opcode.DUP);
            ctx.Code.AddRange([(byte)Opcode.STORE_GLOBAL_FAST, (byte)ctx.GlobalSlot(incVar.Name)]);
            return;
        }
        ctx.Success = false;   // property/index targets → tree (Task 2 lifts some)
    }

    /// <summary>++/-- on property/field/index targets. The computed NEW value is parked in a
    /// synthetic temp local so the store can be emitted in the VM's operand order: SET_PROP pops
    /// (value, target, name) and SET_INDEX pops (value, target, idxs...), VALUE on top, which
    /// re-pushing target/name after the ADD cannot produce. Both SET ops push the stored value
    /// back: postfix keeps the DUP'd OLD value (discarding the pushed new with POP), prefix keeps
    /// the pushed NEW value. Exactly ONE value is left on the stack. GET_INDEX/SET_INDEX each take
    /// a trailing count byte (see Vm.cs).</summary>
    /// <summary>++/-- on a property/field/index target, with the target resolved ONCE (see
    /// ResolveTargetIntoTemps) so the read and the store act on the same receiver and element.</summary>
    private static void CompileIncDecMember(Ctx ctx, IncrementDecrementExpr ide, int deltaConst)
    {
        int ownerSlot;
        int[] indexSlots;
        string? member;
        switch (ide.Target)
        {
            case PropertyAccessExpr pae:
                ownerSlot = NewTemp(ctx, "inc_owner");
                indexSlots = ResolveTargetIntoTemps(ctx, ownerSlot, pae.Target, []);
                member = pae.PropertyName;
                break;
            case ArrayAccessExpr aae:
                if (aae.Indices.Any(i => i is RangeExpr))
                {
                    ctx.Success = false;   // slice targets stay on the tree
                    return;
                }
                ownerSlot = NewTemp(ctx, "inc_owner");
                indexSlots = ResolveTargetIntoTemps(ctx, ownerSlot, aae.Array, aae.Indices);
                member = null;
                break;
            default:
                ctx.Success = false;   // other targets stay on the tree
                return;
        }
        if (!ctx.Success) return;

        var oldSlot = NewTemp(ctx, "inc_old");
        var newSlot = NewTemp(ctx, "inc_new");
        if (!ctx.Success) return;
        EmitResolvedRead(ctx, ownerSlot, indexSlots, member);              // [old]
        EmitStoreVar(ctx, oldSlot);
        EmitLoadVar(ctx, oldSlot);
        // `deltaConst` is an ALREADY-INTERNED pool index (the caller's AddConst), not a value: going through
        // EmitLoadConst would intern the NUMBER and add that instead (measured: `g.N++` stored g.N + 0).
        ctx.Code.Emit(Opcode.LOAD_CONST);
        ctx.Code.EmitConstIdx(deltaConst);
        ctx.Code.Emit(Opcode.ADD);                                        // [new]
        EmitStoreVar(ctx, newSlot);
        EmitResolvedWrite(ctx, ownerSlot, indexSlots, member, newSlot);    // leaves [new]
        if (ide.IsPrefix) return;                                         // prefix answers the new value
        EmitPop(ctx);                                                     // postfix answers the OLD value
        EmitLoadVar(ctx, oldSlot);
    }

    /// <summary>Compound assign on property/index targets (a.b += v, arr[i] *= v). The computed NEW
    /// value is parked in a synthetic temp local so the store can be emitted in the VM's operand
    /// order: SET_PROP pops (value, target, name) and SET_INDEX pops (value, target, idxs...),
    /// VALUE on top, which re-pushing target/name after the op cannot produce. Both SET ops push
    /// the stored value back, which IS the compound-assign result, exactly ONE value is left on the
    /// stack (no POP). GET_INDEX/SET_INDEX each take a trailing count byte (see Vm.cs).</summary>
    /// <summary>`op=` and `??=` on a property/field/index target, with the target resolved ONCE (see
    /// ResolveTargetIntoTemps): the receiver and the indices are evaluated once, then the old value is read,
    /// then the right operand is evaluated, then the store, which is C#'s order and the tree's.
    /// </summary>
    private static void CompileCompoundMember(Ctx ctx, CompoundAssignExpr ca)
    {
        // Event subscription/unsubscription: `this.OnTransform += handler` (or -=) where the property is a
        // declared event of the enclosing class. The name travels as the instruction's OPERAND only: the
        // verifier's stack effect for EVENT_ADD/EVENT_SUB is (2, 1) with the VM popping handler THEN instance,
        // so a pushed name is what the VM would read as the INSTANCE (measured: `this.OnProtected += h`
        // subscribed to a String and the arm bailed).
        if (ctx.EventNames is { Count: > 0 } && ca.Target is PropertyAccessExpr evPae
            && (ca.Operator.Type == TokenType.PlusEqual || ca.Operator.Type == TokenType.MinusEqual)
            && ctx.EventNames.Contains(evPae.PropertyName))
        {
            CompileExpr(ctx, evPae.Target);                                    // [instance]
            CompileExpr(ctx, ca.Value);                                        // [i, handler]
            ctx.Code.Add(ca.Operator.Type == TokenType.PlusEqual ? (byte)Opcode.EVENT_ADD : (byte)Opcode.EVENT_SUB);
            ctx.Code.EmitConstIdx(ctx.AddConst(evPae.PropertyName));            // name const idx (operand)
            return;
        }

        int ownerSlot;
        int[] indexSlots;
        string? member;
        switch (ca.Target)
        {
            case PropertyAccessExpr pae:
                ownerSlot = NewTemp(ctx, "cmp_owner");
                indexSlots = ResolveTargetIntoTemps(ctx, ownerSlot, pae.Target, []);
                member = pae.PropertyName;
                break;
            case ArrayAccessExpr aae:
                if (aae.Indices.Any(i => i is RangeExpr))
                {
                    ctx.Success = false;   // slice targets stay on the tree
                    return;
                }
                ownerSlot = NewTemp(ctx, "cmp_owner");
                indexSlots = ResolveTargetIntoTemps(ctx, ownerSlot, aae.Array, aae.Indices);
                member = null;
                break;
            default:
                ctx.Success = false;   // other targets stay on the tree
                return;
        }
        if (!ctx.Success) return;

        var newSlot = NewTemp(ctx, "cmp_new");
        if (!ctx.Success) return;
        EmitResolvedRead(ctx, ownerSlot, indexSlots, member);              // [old]

        if (ca.Operator.Type == TokenType.QuestionEqual)
        {
            // `??=`: keep what is there, or store the RHS. The RHS is NOT evaluated on the keep path, and the
            // old value IS the expression's value there, so that path leaves it alone.
            EmitDup(ctx);                                                 // [old, old]
            EmitLoadConst(ctx, null);
            ctx.Code.Emit(Opcode.EQ);                                     // [old, isNull]
            ctx.Code.Emit(Opcode.JMP_IF_FALSE);
            var keepAt = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            EmitPop(ctx);                                                 // drop the null copy -> []
            CompileExpr(ctx, ca.Value);                                   // [v]
            EmitStoreVar(ctx, newSlot);
            EmitResolvedWrite(ctx, ownerSlot, indexSlots, member, newSlot);   // leaves [v]
            ctx.Code.Emit(Opcode.JMP);
            var endAt = ctx.Code.Count; ctx.Code.Add(0); ctx.Code.Add(0);
            PatchJump(ctx, keepAt, ctx.Code.Count);                       // keep: [old] IS the value
            PatchJump(ctx, endAt, ctx.Code.Count);
            return;
        }

        var op = ca.Operator.Type switch
        {
            TokenType.PlusEqual => (byte)Opcode.ADD,
            TokenType.MinusEqual => (byte)Opcode.SUB,
            TokenType.StarEqual => (byte)Opcode.MUL,
            TokenType.SlashEqual => (byte)Opcode.DIV,
            TokenType.PercentEqual => (byte)Opcode.MOD,
            TokenType.AmpersandEqual => (byte)Opcode.BITAND,
            TokenType.PipeEqual => (byte)Opcode.BITOR,
            TokenType.CaretEqual => (byte)Opcode.BITXOR,
            TokenType.LeftShiftEqual => (byte)Opcode.SHL,
            TokenType.RightShiftEqual => (byte)Opcode.SHR,
            _ => (byte)0xFF
        };
        if (op == 0xFF) { ctx.Success = false; return; }
        // C#'s order: the target was read above, then the right operand, then the operation, then the store.
        CompileExpr(ctx, ca.Value);                                       // [old, v]
        ctx.Code.Add(op);                                                 // [new]
        EmitStoreVar(ctx, newSlot);
        EmitResolvedWrite(ctx, ownerSlot, indexSlots, member, newSlot);    // leaves [new]
    }

    /// <summary>Named helpers for the emission sequences that were duplicated across this file: the
    /// LOAD_CONST pair (21 sites), LOAD_VAR/STORE_VAR (50), POP/DUP (48), JMP/JMP_IF_FALSE (48). They need
    /// Ctx, private to this class, which is why they live here rather than beside BytecodeWriter, which
    /// owns the bytes. The relative-target convention is the compiler's own: the operand holds
    /// (target - operandPos), as in the switch-arm patch at :1510.</summary>
    /// <summary>Emits a list literal: each element, then the element TypeInfo, then MAKE_LIST. The element type
    /// is a PARAMETER because a declaration's declared type TARGET-TYPES its literal
    /// (`int?[] a = [1, null, 3]`, a C# 12 collection expression): the literal's own inference would refuse the
    /// null first, so the caller that knows the target hands it over.</summary>
    static void CompileListLiteral(Ctx ctx, ListLiteralExpr lle, TypeInfo? elementType)
    {
        foreach (var el in lle.Elements) CompileExpr(ctx, el);
        EmitLoadConst(ctx, elementType);
        ctx.Code.AddRange([(byte)Opcode.MAKE_LIST, ctx.Count8(lle.Elements.Count)]);
    }

    /// <summary>The TypeInfo a type REFERENCE should be emitted with: a type parameter bound by the instantiation
    /// being compiled becomes its concrete binding (`T` becomes `string` for `P1&lt;string&gt;`). This is the
    /// compile-time half of the binding resolution, whose other half is Interpreter.ResolveTypeToClr's consult for the
    /// TREE, and it lives at the EMISSION rather than in a body rewrite because that walk only knows three node
    /// shapes: measured, `o is IEnumerable&lt;T&gt;` answered False from a return, a print, an if, a `&amp;&amp;`, a var
    /// initializer, a lambda and a foreach body alike. `TypeInfo.Substitute` is the same mapping the instantiation
    /// already applies to a declaration's parameter and return types, so the two agree by construction.</summary>
    static TypeInfo EmittedType(Ctx ctx, TypeInfo type)
        => ctx.TypeParamBindings is { Count: > 0 } bindings ? type.Substitute(bindings) : type;

    static void EmitLoadConst(Ctx ctx, object? value)
    { ctx.Code.Emit(Opcode.LOAD_CONST); ctx.Code.EmitConstIdx(ctx.AddConst(value)); }

    /// <summary>Records the type a TOP-LEVEL declaration was declared with, in the compiled path where the store itself
    /// carries only a value (`STORE_GLOBAL_FAST`, and `__define_const__` for a const). The spelling is the one the tree
    /// records in `VisitVarDeclStmt` (`TypeInfo.DeclaredSpelling`), so `typeof(<a name>)` cannot answer two names for one
    /// declaration depending on which engine ran it: measured, a stripped bundle answered "long" for `long? n = 1;` and
    /// "null" for `long? n;` where the file answers "long?" for both. An INFERRED `var` and an explicit `object` record
    /// nothing, which is the same predicate this arm's own coercion test asks, because recording that placeholder would
    /// make `typeof(x)` answer "object" for every inferred variable.</summary>
    static void EmitDeclaredGlobalType(Ctx ctx, VarDeclStmt vd)
    {
        if (vd.Type is not { } declType) return;
        if (declType.BaseType == TokenType.Object && declType.TypeName == null) return;
        EmitLoadConst(ctx, DeclareVarHelperName);
        EmitLoadConst(ctx, vd.Name);
        EmitLoadConst(ctx, TypeInfo.DeclaredSpelling(declType));
        ctx.Code.AddRange([(byte)Opcode.CALL, ctx.Count8(2)]);
        ctx.Code.Add((byte)Opcode.POP);   // the helper answers null, and a call leaves its result
    }

    /// <summary>Answers `typeof(<a LOCAL name>)` from the local's DECLARED type, when this body holds one, by emitting the
    /// spelling as a const instead of calling the runtime helper. The helper reads a BINDING (`DeclaredTypeNameOf`) and a
    /// compiled body's local is a VM SLOT with none, so the answer fell back to the value's type: `long? n = 3; typeof(n)`
    /// answered "long" inside a function on BOTH legs, where the same declaration answers "long?" at the top level (a
    /// function body is compiled whichever engine runs the top level). The operand is a bare name the parser rewrote, so
    /// nothing is evaluated that the const skips, and a name with NO recorded declaration (an inferred `var`, an explicit
    /// `object`, a parameter) keeps the helper's answer, which is the suite's two pinned limits.</summary>
    static bool TryEmitTypeofLocalDeclaration(Ctx ctx, CallExpr ce)
    {
        if (ce.Callee is not VariableExpr { Name: TypeofNamedHelperName }) return false;
        if (ce.Arguments is not { Count: >= 2 } args || args[0] is not StringExpr { Value: var localName }) return false;
        if (!ctx.LocalMap.TryGetValue(localName, out var slot)) return false;
        if (slot >= ctx.LocalDeclaredTypes.Count) return false;
        if (ctx.LocalDeclaredTypes[slot] is not { } declared) return false;
        if (declared.BaseType == TokenType.Object && declared.TypeName == null) return false;
        EmitLoadConst(ctx, TypeInfo.DeclaredSpelling(EmittedType(ctx, declared)));
        return true;
    }

    /// <summary>A synthetic local slot for an emission sequence that must KEEP a value (a resolved receiver, an
    /// evaluated index, a computed result). Deliberately NOT in LocalMap: no user name can collide, and the VM
    /// indexes it by the raw byte. A body past the operand's 1-byte range DECLINES rather than emitting a
    /// truncated slot, the rule GlobalSlot and LocalIdx already follow.</summary>
    static int NewTemp(Ctx ctx, string what)
    {
        // The local operand is ONE byte (BytecodeFormat.EmitLocalIdx truncates), so a body already holding 256
        // locals DECLINES rather than storing into a truncated slot, the rule LocalIdx and GlobalSlot follow.
        if (ctx.Locals.Count > byte.MaxValue) { ctx.Success = false; return 0; }
        var idx = ctx.Locals.Count;
        ctx.Locals.Add($"${what}{idx}");
        return idx;
    }

    /// <summary>Fills an owner slot and one slot per index of a RESOLVED target, evaluating each expression ONCE
    /// and in source order (receiver, then the indices left to right). See Interpreter.ResolveTarget, the tree's
    /// half of the same rule.</summary>
    static int[] ResolveTargetIntoTemps(Ctx ctx, int ownerSlot, Expr owner, IReadOnlyList<Expr> indices)
    {
        CompileExpr(ctx, owner);
        EmitStoreVar(ctx, ownerSlot);
        var slots = new int[indices.Count];
        for (var i = 0; i < indices.Count; i++)
        {
            slots[i] = NewTemp(ctx, "tgt_idx");
            CompileExpr(ctx, indices[i]);
            EmitStoreVar(ctx, slots[i]);
        }
        return slots;
    }

    /// <summary>Emits the READ half of a compound/incdec target from the slots a caller filled, so nothing is
    /// evaluated twice. Leaves [old] on the stack.</summary>
    static void EmitResolvedRead(Ctx ctx, int ownerSlot, int[] indexSlots, string? member)
    {
        EmitLoadVar(ctx, ownerSlot);
        if (member != null)
        {
            EmitLoadConst(ctx, member);
            ctx.Code.Emit(Opcode.GET_PROP);
            return;
        }
        foreach (var s in indexSlots) EmitLoadVar(ctx, s);
        ctx.Code.Emit(Opcode.GET_INDEX);
        ctx.Code.Add(ctx.Count8(indexSlots.Length));
    }

    /// <summary>The STORE half: pushes the resolved target and the value, so the write cannot disagree with the
    /// read about which member or element it means. Both SET ops leave the stored value on the stack.</summary>
    static void EmitResolvedWrite(Ctx ctx, int ownerSlot, int[] indexSlots, string? member, int valueSlot)
    {
        EmitLoadVar(ctx, ownerSlot);
        if (member != null)
        {
            EmitLoadConst(ctx, member);
            EmitLoadVar(ctx, valueSlot);
            ctx.Code.Emit(Opcode.SET_PROP);
            return;
        }
        foreach (var s in indexSlots) EmitLoadVar(ctx, s);
        EmitLoadVar(ctx, valueSlot);
        ctx.Code.Emit(Opcode.SET_INDEX);
        ctx.Code.Add(ctx.Count8(indexSlots.Length));
    }

    static void EmitLoadVar(Ctx ctx, int idx) { ctx.Code.Emit(Opcode.LOAD_VAR); ctx.Code.EmitLocalIdx(idx); }
    static void EmitStoreVar(Ctx ctx, int idx) { ctx.Code.Emit(Opcode.STORE_VAR); ctx.Code.EmitLocalIdx(idx); }
    static void EmitPop(Ctx ctx) => ctx.Code.Emit(Opcode.POP);
    static void EmitDup(Ctx ctx) => ctx.Code.Emit(Opcode.DUP);

    /// <summary>Emits a source location for error reporting. The VM reports a refusal at the LAST location it saw,
    /// so a COMPILED body has to say where inside a statement it is: the tree walker reports a member call's own
    /// line/column, and a compiled body reported the enclosing STATEMENT's, which is the divergence a three-case
    /// measurement pinned (`"ab".Nope()` inside a function AGREES because both legs compile that body, while at the
    /// top level and inside a lambda the walking leg reported column 30 and 34 against the compiled leg's 25 and
    /// 23). The call sites below emit this immediately before their CALL_METHOD, so the position that reaches the
    /// refusal is the call's, exactly as the walker's is.</summary>
    static void EmitLoc(Ctx ctx, int line, int column)
    {
        ctx.Code.Emit(Opcode.LOC);
        // LocU8x4 is two U16 FIELDS (line, column), so the writer names the field rather than the bytes.
        ctx.Code.EmitU16(line);
        ctx.Code.EmitU16(column);
    }

    /// <summary>Writes a jump's relative offset over an operand already emitted, or bails the function when
    /// the offset cannot be carried: the operand is read back as a SIGNED short, so an offset outside the
    /// signed 2-byte range cannot be represented, and a function too large for it bails to the tree rather
    /// than jumping into the middle of an instruction.</summary>
    /// <summary>Compiles a CONDITION with the flag the pattern rule reads: a pattern that BINDS names is allowed only here,
    /// which is the tree's `_condBinding` rule (VisitExpressions.VisitIsPatternExpr refuses `Pattern variable '<name>' is only
    /// allowed in if/while conditions.` otherwise, and its classic inline `expr is Type name` form is exempt).
    /// Saved and restored, because a nested condition (an `if` inside an `if` condition, a call inside a condition) must not
    /// leave the flag set for the body it belongs to.</summary>
    static void CompileConditionExpr(Ctx ctx, Expr cond)
    {
        var saved = ctx.InCondition;
        ctx.InCondition = true;
        CompileExpr(ctx, cond);
        ctx.InCondition = saved;
    }

    static void Patch(Ctx ctx, int pos, int offset)
    {
        if (offset < short.MinValue || offset > short.MaxValue) { ctx.Success = false; return; }
        ctx.Code.PatchU16(pos, offset);
    }

    /// <summary>Points a JUMP's operand at <paramref name="target"/>: the relative offset a TargetU16
    /// operand carries, measured from the end of the OPERAND (the executor's JMP adds the operand's own size
    /// itself). Every call site used to spell that size (`target - (pos + 2)`), which is the same restated
    /// width the shape matrix removed from the reads, so the arithmetic lives here once.</summary>
    static void PatchJump(Ctx ctx, int operandPos, int target)
        => Patch(ctx, operandPos, target - (operandPos + BytecodeFormat.OperandSize(OperandKind.TargetU16)));

    /// <summary>The same for a break/continue emitted as THROW_FLOW_BREAK/CONTINUE: that instruction carries
    /// a handler-unwind count AFTER the target, and the executor adds the offset when it is already past the
    /// WHOLE instruction, so this offset is measured from the shape's end rather than from the target.</summary>
    static void PatchUnwindJump(Ctx ctx, int operandPos, int target)
        => Patch(ctx, operandPos, target - (operandPos + BytecodeFormat.ShapeSize(OperandKind.TargetU16UnwindU8)));

    /// <summary>Whether a member write's indexer target is one a COMPILED body may resolve through
    /// <see cref="Interpreter.IndexedWriteTargetHelperName"/>: exactly the shape the tree's own CS1612 check
    /// resolves from the AST (a plain VARIABLE container and a single literal or variable index). Any other
    /// shape keeps declining, because the tree does not check it either: the point is that both engines agree
    /// about which writes are LEGAL, not merely that they run the same rule.</summary>
    static bool IsCheckedIndexerTarget(ArrayAccessExpr tae)
        => tae.Indices.Count == 1
           && Interpreter.IsEffectFreeIndexShape(tae.Array)
           && Interpreter.IsEffectFreeIndexShape(tae.Indices[0]);

    /// <summary>Emits a break/continue jump. If try/finally handlers are active inside the loop
    /// body, the jump becomes THROW_FLOW_* (2-byte target + 1-byte handler-unwind count) so the VM
    /// runs every pending finally before resuming the flow; otherwise a plain JMP is used.</summary>
    static void EmitLoopJump(Ctx ctx, Opcode flowOp, List<int> plainPatches, List<int> flowPatches)
    {
        var unwind = ctx.ActiveHandlersAfter(ctx.Loops.Peek().HandlerFrameCountAtEntry);
        if (unwind > 0)
        {
            ctx.Code.Add((byte)flowOp);
            flowPatches.Add(ctx.Code.Count);   // p = first OPERAND byte (PatchLoopEnds patches p as an unwind jump)
            ctx.Code.Add(0); ctx.Code.Add(0);   // 2-byte relative target
            ctx.Code.Add((byte)unwind);          // 1-byte handler unwind count
        }
        else
        {
            ctx.Code.Add((byte)Opcode.JMP);
            plainPatches.Add(ctx.Code.Count);   // p = first OPERAND byte (PatchLoopEnds patches p as a plain jump)
            ctx.Code.Add(0); ctx.Code.Add(0);
        }
    }
}
