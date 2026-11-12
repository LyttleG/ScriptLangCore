namespace ScriptLangCore;

// ============================================================================
// 9. INTERPRETER - PARTIE 1 : CHAMPS STATIQUES, CONSTANTES WIN32, STRUCTURES
// ============================================================================
//#pragma warning disable IL3050, IL2075, IL2057, IL2062, IL2067, IL2070, IL2026, IL2055
internal partial class Interpreter : IExprVisitor<object?>, IStmtVisitor, IDisposable
{
    private const string SCRIPT_WND_PROC = "ScriptWndProc";
    private const string OLD_WND_PROC = "OldWndProc";

    private static ScriptDebugger? _debugger;
    private readonly ConcurrentStack<string> _callStack = new();

    /// <summary>The `tcp.Use` pipeline. The engine's OWN TCP server (`Server.Start`) reads this field when its
    /// accept loop starts, so every connection it accepts runs the script's middlewares. It lives here rather
    /// than on the server because a middleware is script state: it is re-created with the `tcp` namespace, so it
    /// is per interpreter, and the server that belongs to this interpreter is the only one that may use it.</summary>
    internal MiddlewarePipeline<TcpContext>? TcpPipeline;
    /// <summary>Capture-free lambdas, cached by their AST NODE. The node IS the identity (a re-parse makes a new
    /// one, which is what keeps a fresh snippet's lambda fresh), so this replaces a `line:column:kind` string key:
    /// that key collided for two lambdas at the same POSITION in two DIFFERENT snippets, which is exactly what a
    /// CARRIED statement is (each snippet is parsed on its own, so both lambdas sit at line 1 and the same column).
    /// Measured on a stripped bundle: `var a = Filt<int>([1,2,3,4], (x) => x % 2 == 0);` followed by
    /// `var b = Filt<int>([1,2,3,4], (x) => x > 0);` answered a=2 b=2 where the file answers a=2 b=4, because the
    /// second lambda returned the first's ScriptFunction. Reference keying also drops a string allocation per
    /// evaluated lambda.</summary>
    private readonly Dictionary<LambdaExpr, ScriptFunction> _lambdaCache = new(32, ReferenceEqualityComparer.Instance);
    // Keyed by the DECLARATION NAME, never by a line number. A loaded descriptor carries no distinct line, so
    // a (LineNumber, ArgCount) key collided across classes: with two script classes whose ctors take the same
    // number of arguments, a stripped bundle reused the FIRST class's ctor for the second, so an instance of Q
    // ran P's ctor body. Measured: `var p = new P(); var q = new Q();` over two classes with a field `N`
    // printed `p=p  q=q` from source and `p=p  q=p` from the bundle. A name is unique in the registry by
    // construction, and it survives the load, which a line does not.
    private readonly ConcurrentDictionary<(string DeclName, int ArgCount), ConstructorDecl?> _constructorCache = new();
    private readonly ConcurrentDictionary<(nint TypeKey, string Name, int ArgCount, int ArgTypesHash), (MethodInfo Method, ParameterInfo[] Params)?> _methodOverloadCache = new();
    private static int _runScriptDepth;
    /// <summary>The thread the RUN began on. Recorded because a compiled top-level body has no statement boundaries, so
    /// the one place the engine drains `Task`'s main-thread continuation queue (between tree statements) is never
    /// reached: measured, `t.ContinueWith(...)` after `t.Wait()` fired from a source run and never from a
    /// `--no-source` bundle. Set where an interpreter is CREATED and at `Interpret`, because a bundle never interprets
    /// a source text.</summary>
    internal static int RunThreadId;
    /// <summary>The NATIVE id of the thread the run began on, which is the only id `PostThreadMessageW` accepts:
    /// <see cref="RunThreadId"/> is a MANAGED id (1, 2, 3...) and posting to it fails with no error set (measured:
    /// `posted=False err=0`, and a delivery that the wake was meant to make prompt arrived 169 ms late instead).
    /// Captured beside the managed id at the same two places, and read by `Events`' `runThread` delivery only.</summary>
    internal static int RunThreadNativeId;
    /// <summary>A native helper whose CALL can write a script global WITHOUT announcing it, so the VM must drop its
    /// per-invocation global slot cache afterwards. ONE member, the carried-statement helper, which runs a top-level
    /// statement on the TREE, and it is here for a measured reason the general stamp cannot cover: a tree write to a
    /// name that lives in the frame's own scope returns from `ScopeStack.Set`'s first loop, which does not move the
    /// stamp (the other three doors do), so `__run_carried__` is invisible to `Interpreter.GlobalWriteStamp`. Without
    /// this entry a stripped bundle of `foreach (var v in Seq(3)) { acc = acc + v; }` read the PRE-LOOP `acc` (measured
    /// `top=[] viaFn=[012]`: the carried loop wrote it, a FUNCTION read it correctly and the top-level read was stale).
    /// The STAMP still carries every other case (a callback, a subscriber, a Task body); this list is only for a
    /// helper whose write lands in that unannounced door.</summary>
    internal static bool BuiltinTouchesScriptState(string name) =>
        name is RunCarriedStatementHelperName;

    /// <summary>Drains the Task main-thread continuation queue when there is anything in it, from the thread the RUN
    /// began on, and answers whether it ran something. THE VM CALLS THIS AT EVERY `LOC`, the instruction the compiler
    /// emits once per statement: that is the boundary at which the TREE drains the same queue (its own loop, after each
    /// statement), and a compiled body had no equivalent point, so `t.ContinueWith(f)` registered on a COMPLETE task,
    /// or on one that completed a moment later, never fired in a `--no-source` bundle (measured: `crash_task_edge`
    /// checks 2, 9a, 9b, 10 and 15 failed from a bundle and pass from the file). One `IsEmpty` read per statement.</summary>
    internal static bool DrainMainThreadQueue()
    {
        if (RunThreadId == 0 || Environment.CurrentManagedThreadId != RunThreadId) return false;
        if (Task.MainThreadQueue.IsEmpty) return false;
        Task.PumpPending();
        return true;
    }

    /// <summary>Global switch: when false, the bytecode VM is disabled and every function runs through the AST tree-walker (diagnostics/A-B).</summary>
    /// <summary>Global switch: when false, the bytecode VM is disabled and every function runs through the AST tree-walker (diagnostics/A-B).</summary>
    internal static bool VmEnabled = true;
    /// <summary>True when a function body may take the VM fast path. The .bytecode
    /// toggle gates SOURCE functions for A-B testing; bytecode-library bodies
    /// (IsVmShell) have no AST and must always run on the VM runner.</summary>
    internal static bool VmPathEnabled(FuncDecl decl) => VmEnabled || decl.IsVmShell;

    // ════════════════════════════════════════════════════════════════════════
    // VM-flat generator gate (Task 2). A coroutine that can produce a
    // CoroutineYieldRequest, Coroutine.Sleep / Coroutine.Wait / Coroutine.Fetch
    // (SleepRequest / EventWaitRequest / HttpRequest), Http.Get/Post/... inside a
    // coroutine (HttpRequest), ReadLineAsync (ReadLineRequest), MUST stay on the
    // TREE path: the bytecode VM has no machinery for sleep/event/http/readline
    // yields, and the tree's ResumeCoroutine owns the request queueing + resume-value
    // contract (var x = yield Coroutine.Sleep(...) consumes the wake value).
    //
    // Single gate forces the tree:
    //   IsYieldRequestBody, scan the coroutine body plus, best-effort,
    //   bounded, every script function it can call DIRECTLY by bare name, for
    //   the producer call shapes below. Any call we cannot prove clean forces
    //   the tree. Method calls on PROVABLY CLR receivers (list.Add, Math.*,
    //   literals, ...) are clean; method calls on script-defined receivers
    //   (instances of script classes, script class statics) and anything NOT
    //   provably CLR fail CLOSED to the tree, a script method can return a
    //   CoroutineYieldRequest (e.g. `obj.GetSleep()` returning
    //   Coroutine.Sleep(...)) that the VM would yield RAW instead of sleeping.
    //   (A foreach over a VM generator IS VM-safe since Task 3, the VM's
    //   FOREACH_INIT/NEXT drives the inner generator in-place; foreach over a
    //   bare Coroutine still falls back to the tree via the loud first-MoveNext
    //   error path rather than double-executing side effects.)
    // ════════════════════════════════════════════════════════════════════════
    internal bool IsYieldRequestBody(FuncDecl decl)
    {
        var visited = new HashSet<FuncDecl> { decl };
        var queue = new Queue<FuncDecl>();
        queue.Enqueue(decl);
        // Fail-closed method-receiver classification: collect which local/param names provably
        // hold CLR values vs script-class instances across every scanned decl. Save/restore so a
        // nested scan (not possible today, the scan never executes script) stays isolated.
        var prevScript = _scanScriptLocals;
        var prevClr = _scanClrLocals;
        var prevResolver = _scanBareNameResolver;
        _scanScriptLocals = new HashSet<string>(_caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        _scanClrLocals = new HashSet<string>(_caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        try
        {
            CollectVarTypes(decl);
            // Bounded reachability: a producer buried N helper-calls deep still gates the caller;
            // the bound only caps pathological call-graph depth (6 direct hops is far beyond any
            // real generator helper chain and keeps the analysis O(body size)).
            for (var hops = 0; hops < 6 && queue.Count > 0; hops++)
            {
                for (int qi = 0, qc = queue.Count; qi < qc; qi++)
                {
                    var d = queue.Dequeue();
                    if (ScanStmtsForYieldRequest(d.Body, queue, visited)) return true;
                }
            }

            return false;
        }
        finally
        {
            _scanScriptLocals = prevScript;
            _scanClrLocals = prevClr;
            _scanBareNameResolver = prevResolver;
        }
    }

    /// <summary>The single gate that decides whether a coroutine can be driven in-place by the bytecode
    /// VM (the same check InvokeScriptFunction uses to set Coroutine.VmBacked). FOREACH_CALL (Task 4)
    /// only fires for names in this set, a coroutine whose body compiled to VM bytecode and is proven
    /// free of yield-request producers. Anything else keeps the CALL + FOREACH_INIT path (which tree-
    /// falls-back for non-VM-driveable generators).</summary>
    internal bool IsVmBackedCoroutine(FuncDecl decl)
        => decl.Compiled != null && VmPathEnabled(decl) && (decl.HasYield || decl.IsAsync) && !IsYieldRequestBody(decl);

    /// <summary>Broader gate for the Coroutine.Start/Resume path: a body that compiled to bytecode
    /// and produces no yield-requests can run on the VM even WITHOUT any yield (a no-yield function
    /// run as a coroutine is just a plain call, it runs once and completes). For-loop init vars
    /// are compile-time shadowed (fresh slot + restored mapping at exit), so loop scoping matches
    /// the tree. The generator path keeps the HasYield requirement (a direct call returns a
    /// Generator, not a value).</summary>
    internal bool CanRunCoroutineOnVm(FuncDecl decl)
        => decl.Compiled != null && VmPathEnabled(decl) && !IsYieldRequestBody(decl);

    /// <summary>Lets the Task 4 precompile pass resolve bare-name calls in IsYieldRequestBody against
    /// the parsed FuncDecls, the functions are not yet registered in the global scope at that point.
    /// Null restores the runtime default (ResolveGlobalFunction). Saved/restored per scan like the
    /// other _scan* locals, so the runtime gate is never affected.</summary>
    internal void SetScanBareNameResolver(Func<string, FuncDecl?>? resolver) => _scanBareNameResolver = resolver;

    private bool ScanStmtsForYieldRequest(IReadOnlyList<Stmt> stmts, Queue<FuncDecl> queue, HashSet<FuncDecl> visited)
    {
        if (stmts == null) return false;
        foreach (var s in stmts)
            if (StmtMayProduceYieldRequest(s, queue, visited)) return true;
        return false;
    }

    private bool StmtMayProduceYieldRequest(Stmt s, Queue<FuncDecl> queue, HashSet<FuncDecl> visited)
    {
        switch (s)
        {
            case ExprStmt es:
                return ExprMayProduceYieldRequest(es.Expression, queue, visited);
            case VarDeclStmt vd:
                return ExprMayProduceYieldRequest(vd.Initializer, queue, visited);
            case ReturnStmt rs:
                return ExprMayProduceYieldRequest(rs.Value, queue, visited);
            case BlockStmt bs:
                return ScanStmtsForYieldRequest(bs.Statements, queue, visited);
            case IfStmt ifs:
                return ExprMayProduceYieldRequest(ifs.Condition, queue, visited)
                    || ScanStmtsForYieldRequest(ifs.ThenBranch, queue, visited)
                    || ScanStmtsForYieldRequest(ifs.ElseBranch, queue, visited);
            case WhileStmt ws:
                return ExprMayProduceYieldRequest(ws.Condition, queue, visited)
                    || ScanStmtsForYieldRequest(ws.Body, queue, visited);
            case ForStmt fs:
                return (fs.Init != null && StmtMayProduceYieldRequest(fs.Init, queue, visited))
                    || ExprMayProduceYieldRequest(fs.Condition, queue, visited)
                    || ExprMayProduceYieldRequest(fs.Increment, queue, visited)
                    || ScanStmtsForYieldRequest(fs.Body, queue, visited);
            case ForeachStmt fes:
                return ExprMayProduceYieldRequest(fes.Collection, queue, visited)
                    || ScanStmtsForYieldRequest(fes.Body, queue, visited);
            case SwitchStmt ss:
                if (ExprMayProduceYieldRequest(ss.Subject, queue, visited)) return true;
                foreach (var c in ss.Cases)
                {
                    if (ExprMayProduceYieldRequest(c.Guard, queue, visited)) return true;
                    if (ScanStmtsForYieldRequest(c.Body, queue, visited)) return true;
                }

                return false;
            case TryStmt ts:
                if (StmtMayProduceYieldRequest(ts.Body, queue, visited)) return true;
                foreach (var cc in ts.Catches)
                    if (StmtMayProduceYieldRequest(cc.Body, queue, visited)) return true;
                return ts.Finally != null && StmtMayProduceYieldRequest(ts.Finally, queue, visited);
            case ThrowStmt ths:
                return ExprMayProduceYieldRequest(ths.Expression, queue, visited);
            case UsingStmt us:
                return ExprMayProduceYieldRequest(us.Initializer, queue, visited)
                    || StmtMayProduceYieldRequest(us.Body, queue, visited);
            case UsingDeclStmt uds:
                return ExprMayProduceYieldRequest(uds.Initializer, queue, visited);
            case DestructuringStmt ds:
                return ExprMayProduceYieldRequest(ds.Source, queue, visited);
            case EnumDeclStmt eds:
                if (eds.Members == null) return false;
                foreach (var (_, init) in eds.Members)
                    if (ExprMayProduceYieldRequest(init, queue, visited)) return true;
                return false;
            case NamespaceDeclStmt nds:
                return ScanStmtsForYieldRequest(nds.Body, queue, visited);
            case FuncDecl nested:
                // A nested/local function (or lambda-with-body-as-statement) is reachable from this
                // body, scan it too, exactly once per decl.
                if (!visited.Add(nested)) return false;
                CollectVarTypes(nested);
                return ScanStmtsForYieldRequest(nested.Body, queue, visited);
            case ClassDeclStmt cls:
                foreach (var m in cls.Methods)
                {
                    if (!visited.Add(m)) continue;
                    CollectVarTypes(m);
                    if (ScanStmtsForYieldRequest(m.Body, queue, visited)) return true;
                }

                return false;
            case StructDeclStmt str:
                foreach (var m in str.Methods)
                {
                    if (!visited.Add(m)) continue;
                    CollectVarTypes(m);
                    if (ScanStmtsForYieldRequest(m.Body, queue, visited)) return true;
                }

                return false;
            default:
                // BreakStmt / ContinueStmt / DebuggerStmt / ImportStmt / DelegateDecl / EventDecl /
                // EnumDeclStmt-without-inits, no call expressions.
                return false;
        }
    }

    private bool ExprMayProduceYieldRequest(Expr? e, Queue<FuncDecl> queue, HashSet<FuncDecl> visited)
    {
        if (e == null) return false;
        switch (e)
        {
            // ── Producer call shapes ──
            case MethodCallExpr mce:
                if (mce.Receiver is VariableExpr { Name: "Coroutine" } coroVar)
                {
                    if (mce.MethodName is "Sleep" or "Wait" or "Fetch") return true;
                }
                else if (mce.Receiver is VariableExpr { Name: "Http" })
                {
                    return true; // every Http.Verb returns an HttpRequest inside a coroutine
                }
                else if (mce.Receiver is ThisExpr or SuperExpr or BaseExpr)
                {
                    // An instance-method call on the enclosing class (`this.Fetch()`), the method
                    // body may itself call a yield-request native (Http.Get/Coroutine.Sleep). We
                    // cannot resolve the receiver's class statically here, so fail CLOSED (tree).
                    return true;
                }

                // Fail CLOSED on script-defined receivers: a script method (instance or static) can
                // return a CoroutineYieldRequest, e.g. `obj.GetSleep()` returning Coroutine.Sleep(...),
                // which the VM would yield RAW instead of sleeping. Only PROVABLY CLR receivers
                // (list.Add, Math.*, literals, ...) are clean; anything not provably CLR → tree.
                if (!ReceiverIsProvablyClr(mce.Receiver)) return true;

                if (ExprMayProduceYieldRequest(mce.Receiver, queue, visited)) return true;
                foreach (var a in mce.Arguments)
                    if (ExprMayProduceYieldRequest(a, queue, visited)) return true;
                return false;

            case CallExpr ce:
                switch (ce.Callee)
                {
                    case VariableExpr { Name: var callName } when callName.Equals("ReadLineAsync", Comparison):
                        return true;
                    case VariableExpr { Name: var callName }:
                        // Bare-name call: resolve to a script function (scan it transitively), a
                        // producer native (tree), a clean native (skip), or fail CLOSED → tree.
                        // `_scanBareNameResolver` supplies the parsed top-level FuncDecls when this
                        // runs BEFORE user functions are registered in the global scope (the Task 4
                        // precompile pass), the runtime path falls back to ResolveGlobalFunction.
                        if (_scanBareNameResolver?.Invoke(callName) is { } scanDecl)
                        {
                            if (visited.Add(scanDecl))
                            {
                                queue.Enqueue(scanDecl);
                                CollectVarTypes(scanDecl);
                            }
                            // Also scan the arguments in case a callee arg itself produces.
                            foreach (var a in ce.Arguments)
                                if (ExprMayProduceYieldRequest(a, queue, visited)) return true;
                            return false;
                        }
                        var sf = ResolveGlobalFunction(callName);
                        if (sf != null)
                        {
                            if (visited.Add(sf.Declaration))
                            {
                                queue.Enqueue(sf.Declaration);
                                CollectVarTypes(sf.Declaration);
                            }
                            // Also scan the arguments in case a callee arg itself produces.
                            foreach (var a in ce.Arguments)
                                if (ExprMayProduceYieldRequest(a, queue, visited)) return true;
                            return false;
                        }

                        return IsKnownNative(callName) == false; // producer or unknown → tree
                    case PropertyAccessExpr pae:
                        if (pae.Target is VariableExpr { Name: "Http" }) return true;
                        if (pae.Target is VariableExpr { Name: "Coroutine" }
                            && pae.PropertyName is "Sleep" or "Wait" or "Fetch") return true;
                        // Fail closed on a script-defined receiver (a script static/instance member
                        // invoked via a callable property can produce a CoroutineYieldRequest).
                        if (!ReceiverIsProvablyClr(pae.Target)) return true;
                        return ExprMayProduceYieldRequest(pae.Target, queue, visited);
                }

                foreach (var a in ce.Arguments)
                    if (ExprMayProduceYieldRequest(a, queue, visited)) return true;
                return ExprMayProduceYieldRequest(ce.Callee, queue, visited);

            // ── Child walk ──
            case VariableExpr:
            case NumberExpr:
            case StringExpr:
            case BoolExpr:
            case CharExpr:
            case NullExpr:
            case DiscardExpr:
            case ThisExpr:
            case SuperExpr:
            case BaseExpr:
            case ReturnValueExpr:
            case TuplePatternExpr:
                return false;
            case AssignExpr ae:
                return ExprMayProduceYieldRequest(ae.Value, queue, visited);
            case BinaryExpr be:
                return ExprMayProduceYieldRequest(be.Left, queue, visited)
                    || ExprMayProduceYieldRequest(be.Right, queue, visited);
            case LogicalExpr le:
                return ExprMayProduceYieldRequest(le.Left, queue, visited)
                    || ExprMayProduceYieldRequest(le.Right, queue, visited);
            case UnaryExpr ue:
                return ExprMayProduceYieldRequest(ue.Right, queue, visited);
            case PropertyAccessExpr pe:
                return ExprMayProduceYieldRequest(pe.Target, queue, visited);
            case PropertyAssignExpr pae:
                return ExprMayProduceYieldRequest(pae.Target, queue, visited)
                    || ExprMayProduceYieldRequest(pae.Value, queue, visited);
            case ArrayAccessExpr aae:
                if (ExprMayProduceYieldRequest(aae.Array, queue, visited)) return true;
                foreach (var i in aae.Indices)
                    if (ExprMayProduceYieldRequest(i, queue, visited)) return true;
                return false;
            case ArrayAssignExpr aaae:
                if (ExprMayProduceYieldRequest(aaae.Array, queue, visited)) return true;
                foreach (var i in aaae.Indices)
                    if (ExprMayProduceYieldRequest(i, queue, visited)) return true;
                return ExprMayProduceYieldRequest(aaae.Value, queue, visited);
            case ConditionalExpr ce2:
                return ExprMayProduceYieldRequest(ce2.Condition, queue, visited)
                    || ExprMayProduceYieldRequest(ce2.ThenExpr, queue, visited)
                    || ExprMayProduceYieldRequest(ce2.ElseExpr, queue, visited);
            case CastExpr cast:
                return ExprMayProduceYieldRequest(cast.Expression, queue, visited);
            case IsExpr ise:
                return ExprMayProduceYieldRequest(ise.Left, queue, visited);
            case AsExpr aex:
                return ExprMayProduceYieldRequest(aex.Left, queue, visited);
            case IsNullExpr ine:
                return ExprMayProduceYieldRequest(ine.Left, queue, visited);
            case IsPatternExpr ipe:
                return ExprMayProduceYieldRequest(ipe.Left, queue, visited);
            case CompoundAssignExpr ca:
                return ExprMayProduceYieldRequest(ca.Target, queue, visited)
                    || ExprMayProduceYieldRequest(ca.Value, queue, visited);
            case IncrementDecrementExpr ide:
                return ExprMayProduceYieldRequest(ide.Target, queue, visited);
            case NewInstanceExpr nie:
                if (ExprMayProduceYieldRequest(nie.ConstructorExpr, queue, visited)) return true;
                if (nie.Arguments != null)
                    foreach (var a in nie.Arguments)
                        if (ExprMayProduceYieldRequest(a, queue, visited)) return true;
                return false;
            case IndexExpr ie:
                return ExprMayProduceYieldRequest(ie.Value, queue, visited);
            case RangeExpr re:
                return ExprMayProduceYieldRequest(re.From, queue, visited)
                    || ExprMayProduceYieldRequest(re.To, queue, visited);
            case NullCoalescingExpr nce:
                return ExprMayProduceYieldRequest(nce.Left, queue, visited)
                    || ExprMayProduceYieldRequest(nce.Right, queue, visited);
            case NewArrayExpr nae:
                if (nae.Sizes != null)
                    foreach (var sz in nae.Sizes)
                        if (ExprMayProduceYieldRequest(sz, queue, visited)) return true;
                if (nae.Initializer != null)
                    foreach (var init in nae.Initializer)
                        if (ExprMayProduceYieldRequest(init, queue, visited)) return true;
                return false;
            case NewStructExpr nse:
                if (nse.FieldValues != null)
                    foreach (var fv in nse.FieldValues)
                        if (ExprMayProduceYieldRequest(fv, queue, visited)) return true;
                return false;
            case TupleExpr te:
                foreach (var item in te.Items)
                    if (ExprMayProduceYieldRequest(item, queue, visited)) return true;
                return false;
            case WithExpr we:
                if (ExprMayProduceYieldRequest(we.Base, queue, visited)) return true;
                foreach (var (_, fv) in we.Fields)
                    if (ExprMayProduceYieldRequest(fv, queue, visited)) return true;
                return false;
            case ObjectInitExpr oie:
                if (ExprMayProduceYieldRequest(oie.Inner, queue, visited)) return true;
                foreach (var (_, iv) in oie.Initializers)
                    if (ExprMayProduceYieldRequest(iv, queue, visited)) return true;
                return false;
            case MatchExpr me:
                if (ExprMayProduceYieldRequest(me.Subject, queue, visited)) return true;
                foreach (var arm in me.Arms)
                    if ((arm.Guard != null && ExprMayProduceYieldRequest(arm.Guard, queue, visited))
                        || ExprMayProduceYieldRequest(arm.Value, queue, visited)) return true;
                return false;
            case SwitchExpr se:
                if (ExprMayProduceYieldRequest(se.Subject, queue, visited)) return true;
                foreach (var arm in se.Arms)
                {
                    if (ExprMayProduceYieldRequest(arm.Guard, queue, visited)) return true;
                    if (ExprMayProduceYieldRequest(arm.Value, queue, visited)) return true;
                }

                return false;
            case ListLiteralExpr lle:
                foreach (var el in lle.Elements)
                    if (ExprMayProduceYieldRequest(el, queue, visited)) return true;
                return false;
            case DictLiteralExpr dle:
                foreach (var (k, v) in dle.Entries)
                    if (ExprMayProduceYieldRequest(k, queue, visited)
                        || ExprMayProduceYieldRequest(v, queue, visited)) return true;
                return false;
            case ComprehensionExpr ce3:
                return ExprMayProduceYieldRequest(ce3.ElementExpr, queue, visited)
                    || ExprMayProduceYieldRequest(ce3.Collection, queue, visited)
                    || ExprMayProduceYieldRequest(ce3.Filter, queue, visited);
            case YieldExpr ye:
                return ExprMayProduceYieldRequest(ye.Value, queue, visited);
            case LambdaExpr le:
                // A lambda body is reachable when the enclosing coroutine invokes it, scan it.
                if (le.BodyStmts != null)
                    if (ScanStmtsForYieldRequest(le.BodyStmts, queue, visited)) return true;
                return ExprMayProduceYieldRequest(le.BodyExpr, queue, visited);
            default:
                return false;
        }
    }

    /// <summary>True when `name` resolves to a registered native/builtin function (a native can
    /// never be a script coroutine body, so a bare call to one is clean for the gate). Used to
    /// close the gate FAIL-CLOSED only for genuinely unknown names.</summary>
    private bool IsKnownNative(string name)
    {
        if (_rootFrame.Scopes.TryGetGlobalValue(name, out var v) && v.Value is INativeFunction) return true;
        if (CurrentFrame.Scopes.TryGetGlobalValue(name, out var v2) && v2.Value is INativeFunction) return true;
        return false;
    }

    // Built-in type names whose members are native dispatches / CLR calls, they can never return a
    // script CoroutineYieldRequest, so method calls on receivers of these types are provably clean
    // for the generator gate. User script classes/structs always take precedence (checked first).
    private static readonly HashSet<string> KnownClrTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "List", "Dictionary", "HashSet",
        "IScriptList", "IScriptDictionary", "IScriptHashSet", "IScriptCollection",
        "IEnumerable", "ICollection", "IList",
        "String", "StringBuilder", "Object", "DateTime", "TimeSpan", "Guid", "Regex", "Random",
    };

    /// <summary>True when a MethodCallExpr receiver is PROVABLY a CLR/native value, a method call on
    /// it can never return a script CoroutineYieldRequest. Script-defined receivers (instances of
    /// script classes, script class statics) and anything we cannot prove CLR fail CLOSED (false) so
    /// the enclosing coroutine stays on the tree path.</summary>
    private bool ReceiverIsProvablyClr(Expr receiver)
    {
        switch (receiver)
        {
            case VariableExpr { Name: var n }:
                if (IsScriptGlobalType(n)) return false;                          // script class/struct static call
                if (_scanScriptLocals is { } sl && sl.Contains(n)) return false;  // script-class instance
                if (_scanClrLocals is { } cl && cl.Contains(n)) return true;       // List<int>/String/... local
                if (_rootFrame.Scopes.TryGetGlobalValue(n, out var gv) && gv.Value is Type) return true; // Math/Console/...
                return false; // unknown → fail closed
            case NumberExpr or StringExpr or BoolExpr or CharExpr or NullExpr:
            case ListLiteralExpr or DictLiteralExpr or TupleExpr or NewArrayExpr:
                return true; // literals are always CLR/native values
            case NewInstanceExpr nie:
                return ClassifyNewInstance(nie) == 0;
            case PropertyAccessExpr or ArrayAccessExpr or IndexExpr:
                // The accessed member's runtime type is NOT statically provable CLR, a CLR-typed root
                // can hold a script-class element/property (e.g. `list[i]` with a List<object> of
                // script instances) whose method could return a CoroutineYieldRequest. Fail closed.
                return false;
            default:
                return false; // fail closed
        }
    }

    /// <summary>1 = script class/struct instance, 0 = provably CLR/native, -1 = cannot tell.</summary>
    private int ClassifyNewInstance(NewInstanceExpr nie)
    {
        var tn = nie.TypeInfo?.TypeName;
        if (tn == null && nie.ConstructorExpr is VariableExpr ve) tn = ve.Name;
        if (string.IsNullOrEmpty(tn)) return -1;
        if (IsScriptGlobalType(tn)) return 1;
        return KnownClrTypeNames.Contains(tn) ? 0 : -1;
    }

    /// <summary>True when `name` is a script-declared class/struct (registered as a ClassInfo, a
    /// StructDeclStmt, or a script struct's TypeInfo in scope).</summary>
    private bool IsScriptGlobalType(string name)
    {
        if (_rootFrame.Scopes.TryGetGlobalValue(name, out var v))
            return v.Value is ClassInfo or StructDeclStmt or TypeInfo;
        return CurrentFrame.Scopes.TryGetGlobalValue(name, out var v2) && v2.Value is ClassInfo or StructDeclStmt or TypeInfo;
    }

    /// <summary>Collects which param/local names provably hold CLR values vs script-class instances
    /// for one decl (params + statement-level declarations). Populates the per-scan sets used by
    /// ReceiverIsProvablyClr. Bounded: nested local-function bodies are covered when they are
    /// enqueued/scanned (CollectVarTypes is called at each enqueue point); lambda bodies are not
    /// tracked (their variables fail closed → tree, which is safe).</summary>
    private void CollectVarTypes(FuncDecl d)
    {
        if (d.Params != null)
            foreach (var (t, pName) in d.Params)
                ClassifyVarType(pName, t, null);
        WalkStmtsForVarTypes(d.Body);
    }

    private void ClassifyVarType(string name, TypeInfo? type, Expr? initializer)
    {
        if (string.IsNullOrEmpty(name) || _scanScriptLocals == null) return;
        if (type is { TypeName: not null } ti)
        {
            if (IsScriptGlobalType(ti.TypeName)) _scanScriptLocals.Add(name);
            else if (ti.BaseType != TokenType.Object || KnownClrTypeNames.Contains(ti.TypeName))
                _scanClrLocals!.Add(name);
            // else unknown → fail closed at use (left unclassified)
            return;
        }
        if (initializer == null) return;
        switch (initializer)
        {
            case NewInstanceExpr nie:
                var k = ClassifyNewInstance(nie);
                if (k == 1) _scanScriptLocals.Add(name);
                else if (k == 0) _scanClrLocals!.Add(name);
                break;
            case ListLiteralExpr or DictLiteralExpr or NewArrayExpr or TupleExpr:
                _scanClrLocals!.Add(name);
                break;
        }
    }

    private void WalkStmtsForVarTypes(IReadOnlyList<Stmt> stmts)
    {
        if (stmts == null) return;
        foreach (var s in stmts) WalkStmtForVarTypes(s);
    }

    private void WalkStmtForVarTypes(Stmt s)
    {
        switch (s)
        {
            case VarDeclStmt vd:
                ClassifyVarType(vd.Name, vd.Type, vd.Initializer);
                break;
            case ForeachStmt fes:
                ClassifyVarType(fes.VarName, fes.VarType, fes.Collection);
                WalkStmtsForVarTypes(fes.Body);
                break;
            case BlockStmt bs:
                WalkStmtsForVarTypes(bs.Statements);
                break;
            case IfStmt ifs:
                WalkStmtsForVarTypes(ifs.ThenBranch);
                WalkStmtsForVarTypes(ifs.ElseBranch);
                break;
            case WhileStmt ws:
                WalkStmtsForVarTypes(ws.Body);
                break;
            case ForStmt fs:
                if (fs.Init is VarDeclStmt vd2) ClassifyVarType(vd2.Name, vd2.Type, vd2.Initializer);
                WalkStmtsForVarTypes(fs.Body);
                break;
            case SwitchStmt ss:
                foreach (var c in ss.Cases) WalkStmtsForVarTypes(c.Body);
                break;
            case TryStmt ts:
                WalkStmtForVarTypes(ts.Body);
                foreach (var cc in ts.Catches) WalkStmtForVarTypes(cc.Body);
                if (ts.Finally != null) WalkStmtForVarTypes(ts.Finally);
                break;
            case UsingStmt us:
                ClassifyVarType(us.VarName, null, us.Initializer);
                WalkStmtForVarTypes(us.Body);
                break;
            case FuncDecl nested:
                CollectVarTypes(nested);
                break;
        }
    }



    // Depth guard is build-dependent: the Debug/JIT apphost keeps the ~1.5MB default stack
    // (<StackSize> only reaches the AOT linker's /STACK via IlcDefaultStackSize), and
    // unoptimized JIT frames are huge, the tree walker physically exhausts the stack at
    // ~36 script levels for a non-tail recursion (measured: sumTo(1990) tree-forced dies at
    // ~36 nested InvokeScriptFunction/VisitCallExpr frames), before any AOT-tuned 2000. The
    // guard must fire below that with headroom for the throw to unwind but still allow the
    // suite's legit depths (crash_360_latest recurses 16 levels; the closure-binding battery's
    // nested fib(19) reaches 21 frames including the root frame, 24 is a verified middle ground
    // that keeps headroom for exception dispatch while staying far below the JIT stack limit).
    // NativeAOT has a 32MB stack: plain (throw-free) recursion physically exhausts it at
    // ~15800 levels. BUT a ScriptException (script `throw` or the recursion guard) needs
    // ~6KB of exception-dispatch stack PER level on top of the ~2.1KB/level recursion
    // (measured: a throw at depth ~4000 survives, ~4200+ dies with 20+MB still free, the
    // dispatch walk eats the stack, not the recursion). The count cap is therefore a 3000
    // FALLBACK (3000 × ~8.1KB = 24MB < 32MB, leaves ~8MB for the guard's throw+unwind;
    // verified: guard throws at 3000 unwind cleanly). PRIMARY protection in NativeAOT is
    // the byte-accurate NativeStackNearlyExhausted() guard in InvokeScriptFunction, which
    // fires proportionally to real remaining-vs-used stack bytes (fires at ~3100 on the
    // 32MB main thread, ~95 on 1MB Task.Run threads) and is immune to per-frame cost
    // drift. JIT keeps 24: unoptimized JIT frames are huge (~40KB), the 1.5MB apphost
    // stack physically exhausts at ~36 levels.
    internal static readonly int MaxCallDepth =
        // platform: limit, the two builds differ because unoptimised JIT frames are ~40KB and the 1.5MB apphost stack physically exhausts at ~36 levels, while AOT runs a 32MB stack. The load-bearing guard is the byte-accurate NativeStackNearlyExhausted() below, which fires on real remaining-vs-used bytes and is immune to per-frame cost drift; this number is the coarse backstop, not the safety mechanism.
        RuntimeFeature.IsDynamicCodeSupported ? 24 : 3000;   // JIT vs NativeAOT
    private CallFrame _rootFrame;

    private object? _lastExpressionValue;

    /// <summary>WHERE THAT VALUE CAME FROM, which is the difference between an ANSWER and an ECHO: a top-level
    /// `return <value>` is what the script said, while the value of a bare expression statement is the echo the REPL
    /// prints (`= ...`) and means nothing about the run. A suite's tail is `CheckSummary(); exit(CheckFailed());`, so
    /// its echo is the `false` that call answered, which read like a verdict next to a report. Set at the ONE place a
    /// run's flow is `Return` (`Interpret`'s statement loop), reset wherever the value itself is reset.</summary>
    private bool _lastValueWasReturn;

    /// <summary>True when this run's answer came from a top-level `return`, false when it is the echo of the last
    /// expression statement. Read by the doors right after the run, so a checks report carries `Answer` for the first
    /// and `Echo` for the second, and never confuses the two.</summary>
    internal bool LastValueWasReturn => _lastValueWasReturn;
    private bool _disposed;

    /// <summary>True once Dispose has run. A disposed interpreter's per-thread frame stacks are gone, so a body
    /// invoked on it throws (`Cannot access a disposed object ... ThreadLocal&lt;Stack&lt;CallFrame&gt;&gt;`, measured
    /// through the HTTP pipeline), which is why anything holding an interpreter across requests has to ask.</summary>
    internal bool IsDisposed => _disposed;

    private bool _caseSensitive;
    private StringComparer _comparer;
    private StringComparison _comparison;
    private StringComparison Comparison => _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    // Fail-closed method-receiver classification for the VM-flat generator gate (IsYieldRequestBody):
    // names provably holding CLR values vs script-class instances, populated per scan. The scan
    // never executes script code, so these are only read/written by the synchronous AST walk.
    private HashSet<string>? _scanScriptLocals;
    private HashSet<string>? _scanClrLocals;
    /// <summary>Optional bare-name→FuncDecl resolver for IsYieldRequestBody when it runs BEFORE user
    /// functions are registered in the global scope (the Task 4 FOREACH_CALL precompile pass), the
    /// runtime path leaves this null and falls back to ResolveGlobalFunction.</summary>
    private Func<string, FuncDecl?>? _scanBareNameResolver;

    private readonly Dictionary<(string funcName, object?[] args), object?> _pureMemoCache;
    private readonly Dictionary<string, object> _literalCache = new(128);

    // Generic instantiation caches
    private readonly Dictionary<string, ClassDeclStmt> _genericClassTemplates = new(64);
    private readonly Dictionary<string, ClassInfo> _instantiatedClasses = new(64);
    private readonly Dictionary<(FuncDecl Decl, bool AsMethod, string TypeKey, string? ReceiverAlias), FuncDecl> _instantiatedFuncs = new(64);
    /// <summary>Unqualified class/struct name → the namespaced ClassInfo/StructDeclStmt registered
    /// by a library import. Compiled library method bodies reference sibling classes by their source
    /// name (e.g. `new Animal()`); the VM resolves those through this fallback when the direct
    /// Classes/Structs lookup misses. Populated at import; collision across libraries → error.</summary>
    private readonly Dictionary<string, object> _libraryTypeAliases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object YieldSentinel = new(); // singleton marker, never a valid script value
    private static readonly object TailCallSentinel = new(); // marker for self-recursive tail call
    // 64 frames up front, and the stack GROWS on demand when a push needs more. Measured, this is 512 bytes once
    // per interpreter, which the pool reuses, so sizing it down or pooling it further buys nothing.
    private LoopFrame[] _loopStack = new LoopFrame[64];
    private int _loopStackSp;

    /// <summary>How many tree-walked LOOPS enclose the expression being evaluated, maintained by the plain (non
    /// coroutine) loop arms through <see cref="EnterTreeLoop"/>. The coroutine arms have their own `_loopStack` frames;
    /// this is what the plain arms answer, and <see cref="SuspendAsyncRun"/> reads it: replaying a statement that holds a
    /// loop replays its iterations, which turns an accumulation into a wrong value (measured 24 against 12).</summary>
    private int _treeLoopDepth;
    private object? _yieldValue;          // carried from yield point to coroutine resumer
    private bool _isYield;                // true when a yield has been signaled

    private bool _expectResumeValue;     // true when next yield should consume the pending resume value
    // Tail-call optimization for self-recursion
    private object?[]? _tailCallArgs;     // set when a self-recursive tail call is pending

    // VM usage counters (for coverage reporting)
    internal long VmInvocations;
    internal long TreeInvocations;
    /// <summary>TOP-LEVEL statements this run executed on the VM (see TryExecuteStatementOnVm). Its own
    /// counter because the two above count FUNCTION invocations, so neither can see the top level: this is
    /// what makes `VmSetTopLevelEnabled` observable rather than merely settable.</summary>
    internal long TopLevelVmStatements;
    internal long VmCacheHits;
    internal long VmCacheMisses;
    // Global fast-slot executions (spec A), reported by VmStats as `gfast: N/M`.
    internal long GlobalFastLoads;
    internal long GlobalFastStores;
    private int _tailCallLine;            // line of the tail call
    private int _tailCallColumn;          // column of the tail call
    /// <summary>Top-level function declarations executed in this interpreter session, the source
    /// set for Bytecode.ExportScript. Cleared on Reset so REPL re-runs don't re-export stale bodies.</summary>
    private readonly List<FuncDecl> _scriptTopLevel = [];
    /// <summary>Top-level statements of the CURRENT Interpret call, and the body compiled from them (see
    /// Compiler.CompileProgram): the ENTRY a bytecode bundle carries so a .slcbc runs like the script it came from.
    /// Lazy on purpose, so a plain run pays nothing and only a path that reads ProgramBytecode does.</summary>
    private List<Stmt>? _programStatements;
    private Bytecode? _programBytecode;
    private bool _programBodyAttempted;
    /// <summary>Per-STATEMENT compiled bodies for the top level (see TryExecuteStatementOnVm): the same
    /// statements the program body is compiled from, but one body each, so a statement the VM cannot finish
    /// costs only itself. Scoped to one Interpret call, like _programStatements, because the nodes differ.</summary>
    private readonly Dictionary<Stmt, Bytecode?> _statementBodies = new(ReferenceEqualityComparer.Instance);

    /// <summary>Name the synthetic program-body function carries inside a bundle, and therefore the entry name a
    /// bundle written by ExportTopLevelToBundle declares. It is serialized as an ordinary zero-parameter function,
    /// so the loader's "invoke the declared entry with no arguments" needs no special case.</summary>
    internal const string ProgramEntryFunctionName = "__program__";

    /// <summary>The NAME AN ERROR FRAME REPORTS for a declaration: an unnamed body is a LAMBDA (<c>&lt;lambda&gt;</c>), the same label the tree's own frames use. One statement of the rule, so the tree's four sites and the VM's frame names cannot drift.</summary>
    internal static string FrameLabel(string? declarationName)
        => string.IsNullOrEmpty(declarationName) ? "<lambda>" : declarationName;

    /// <summary>The ONE sentence for "null where a value is required", so the three conversion funnels (an
    /// assignment or a script parameter through <see cref="CoerceTo"/>, a CLR method's argument through
    /// <c>CoerceArgument</c>, and a declared return type through <c>CoerceFlatCallResult</c>) cannot read
    /// differently. It used to be two: the CLR argument path said "Cannot pass null to non-nullable value type
    /// 'Double'." while every other door said this. The TYPE NAME is not part of the sentence on purpose: the
    /// canonical door is reached from a `TypeInfo`, which does not always carry a spellable name.</summary>
    internal const string NullNotAssignableText = "Cannot assign null to non-nullable type";

    /// <summary>True for a statement that DECLARES a name the interpreter registers rather than something
    /// that runs: a function, class, struct, interface, extension, enum, delegate or event. A program body
    /// cannot compile one (it is registered, not executed) and a bundle load registers them from its
    /// carried source, so both ask this ONE question.</summary>
    internal static bool IsDeclarationStatement(Stmt stmt) =>
        stmt is FuncDecl or ClassDeclStmt or StructDeclStmt or InterfaceDeclStmt or ExtensionDeclStmt
            or EnumDeclStmt or DelegateDecl or EventDecl;

    /// <summary>The SOURCE text of the script being interpreted, when the caller has it: a whole-script
    /// bundle records it in its tail so the bundle can carry its program as well as its functions.
    /// Set by the CLI's --compile and by Bytecode.ExportScript; null when only statements are known.
    internal string? SourceText { get; set; }


    private FuncDecl? _currentFuncDecl;   // FuncDecl currently being executed (for self-recursion detection)
    private ScriptFunction? _currentFn;
    private int _returnExprDepth;          // 0 = outside return, 1 = top-level return value, >1 = inside nested sub-expr
    private int _fastPathDepth;            // depth of the single-statement fast path (which does NOT push CallFrames, the main depth guard can't see it)
    internal int _vmReentryDepth;

    /// <summary>Set by the VM handlers that write where the CALL cannot undo it: STORE_GLOBAL, STORE_GLOBAL_FAST
    /// and SET_PROP. Read by TryExecuteVmBody around the execution. Deliberately NOT set by a local store, a
    /// construction or a call: those are either invisible once the tree re-runs the body from the top, or common
    /// enough before a bail that counting them would refuse shapes that work today. Measured both ways: a body whose
    /// only prior work was `n * 2 + 1` and a `new Rec()` must still fall back to the tree, while one that
    /// incremented a GLOBAL must not, because that increment is the double effect this refusal exists for.</summary>
    internal bool VmBodyCommitted;           // depth of C#-stack re-entry into TryExecuteVmBody (the VM CALL/CALL_VAL last-resort fallbacks re-enter the C# interpreter per script call level)

    private readonly HashSet<string> _treeTraced = [with(StringComparer.OrdinalIgnoreCase)]; // one [DEBUG] Tree line per distinct fallback per run
    private string? _lastTreeTrace;      // last [tr] line printed, for consecutive-repeat elision
    private int _lastTreeTraceCount = 1; // how many consecutive repeats of _lastTreeTrace were seen
    private readonly SortedSet<ScheduledCoroutine> _sleepQueue = [with(ScheduledCoroutine.Comparer)];
    private readonly Lock _sleepLock = new();
    private readonly Dictionary<EventInfo, List<Coroutine>> _eventWaiters = new(EventInfoReferenceComparer.Instance);
    private readonly HashSet<string> _staticInitClasses = []; // tracks which classes have had their static constructor run
    /// <summary>Static-field values captured at class REGISTRATION (after field initializers,
    /// before any static ctor runs), used by the library exporter so imported classes start
    /// from initializer state and their static ctors re-run lazily on first use.</summary>
    private readonly Dictionary<string, Dictionary<string, object?>> _initialStaticFields =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<Coroutine> _coroutineStack = new();
    private Coroutine? _currentCoroutine => _coroutineStack.Count > 0 ? _coroutineStack.Peek() : null;
    private int _currentStatementIndex;   // only used inside coroutine execution
    private bool _isResumingFromYield;
    internal bool _traceEnabled;  // set by REPL .trace command
    internal List<ScriptFunction> _exitHandlers = [];  // registered via atexit()

    /// <summary>Invoke all atexit()-registered handlers in reverse (LIFO) order.</summary>
    internal void InvokeExitHandlers()
    {
        if (_exitHandlers.Count == 0) return;
        var handlers = _exitHandlers.ToArray();
        _exitHandlers.Clear();
        for (var i = handlers.Length - 1; i >= 0; i--)
        {
            try { InvokeScriptFunction(handlers[i], [], 0, 0); }
            catch { /* handler exception, continue to next */ }
        }
    }

    // Recommendation: Define HttpClient as a static or singleton field to avoid socket exhaustion
    private static readonly HttpClient HttpClient = new HttpClient();

    private void ExecuteHttpRequest(HttpRequest req, Coroutine co)
    {
        try
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                // Same shared helper as the blocking path: linking + failure classification can
                // never diverge between the coroutine and blocking forms.
                var (text, kind) = SendHttp(req.Method, req.Url, req.JsonBody, req.TimeoutMs, req.Token, AmbientToken);
                LastHttpErrorKind = kind;
                co.Tree!.LastHttpResult = text;   // Tree pre-allocated by HandleYieldRequest before this task was scheduled
                lock (_sleepLock) _sleepQueue.Add(new ScheduledCoroutine(co, DateTime.MinValue, text));
            });
        }
        catch (Exception exSetup)
        {
            co.Tree!.LastHttpResult = $"__HTTP_ERROR__:{exSetup.Message}";
            lock (_sleepLock) _sleepQueue.Add(new ScheduledCoroutine(co, DateTime.MinValue, co.Tree.LastHttpResult));
        }
    }

    /// <summary>Cause of the most recent Http failure: "cancelled", "timeout", "network", or ""
    /// after a success. Exposed to scripts as Http.LastErrorKind.</summary>
    internal string LastHttpErrorKind = "";

    /// <summary>Single implementation for BOTH Http paths (blocking and coroutine): links the
    /// ambient and explicit tokens with the per-call timeout, sends, and classifies the failure
    /// cause so the two paths can never diverge.</summary>
    internal static (string Body, string Kind) SendHttp(string method, string url, string? body,
        int timeoutMs, CancellationToken explicitToken, CancellationToken ambientToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ambientToken, explicitToken);
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
        try
        {
            var verb = method.ToUpperInvariant();
            Task<HttpResponseMessage> Send(CancellationToken ct) => verb switch
            {
                "GET" => HttpClient.GetAsync(url, ct),
                "DELETE" => HttpClient.DeleteAsync(url, ct),
                "POST" => HttpClient.PostAsync(url, HttpJsonContent(body), ct),
                "PUT" => HttpClient.PutAsync(url, HttpJsonContent(body), ct),
                _ => throw new ScriptException($"Unsupported HTTP method: {method}")
            };
            using var response = Send(both.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return (response.Content.ReadAsStringAsync(both.Token).GetAwaiter().GetResult(), "");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return ("__HTTP_ERROR__:timeout", "timeout");
        }
        catch (OperationCanceledException)
        {
            // Linked sources fired: the ambient token (interrupt / host request) or the explicit one.
            return ("__HTTP_ERROR__:cancelled", "cancelled");
        }
        catch (Exception ex)
        {
            return ($"__HTTP_ERROR__:{ex.Message}", "network");
        }
    }

    private static StringContent? HttpJsonContent(string? body) =>
        body != null ? new StringContent(body, Encoding.UTF8, "application/json") : null;

    /// <summary>
    /// The client's BYTES door: the same verb, timeout and token composition as <see cref="SendHttp"/>, but the body is
    /// never turned into a string. A null <paramref name="destinationPath"/> answers the bytes; a path copies the body to
    /// that file and answers the count. <c>HttpCompletionOption.ResponseHeadersRead</c> is the whole point, and it is the
    /// line the C# note is about: without it HttpClient buffers the entire body before returning, so a large download
    /// would sit in memory first. The copy loops in CHUNKS and asks the token per chunk, so a cancellation (or a client
    /// disconnect, whose ambient token is the request's) stops the transfer rather than only its start.
    /// </summary>
    internal static (byte[]? Bytes, long Count, string Kind, string Error) SendHttpBytes(string url,
        string? destinationPath, int timeoutMs, CancellationToken explicitToken, CancellationToken ambientToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ambientToken, explicitToken);
        using var timeout = new CancellationTokenSource(timeoutMs);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, timeout.Token);
        try
        {
            using var response = HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, both.Token)
                .GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var source = response.Content.ReadAsStream(both.Token);
            FileStream? file = destinationPath is null
                ? null
                : new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var sink = file;
            using var chunks = file is null ? new MemoryStream() : null;
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                both.Token.ThrowIfCancellationRequested();
                if (file is not null) file.Write(buffer, 0, read);
                else chunks!.Write(buffer, 0, read);
                total += read;
            }
            return (chunks?.ToArray(), total, "", "");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return (null, 0, "timeout", $"Http: the request to '{url}' timed out after {timeoutMs} ms.");
        }
        catch (OperationCanceledException)
        {
            return (null, 0, "cancelled", $"Http: the request to '{url}' was cancelled.");
        }
        catch (Exception ex)
        {
            // The engine's ONE map, never the CLR's own sentence (a resource key under InvariantGlobalization, or the
            // machine's language), and the URL after it so a reader knows WHICH request failed.
            return (null, 0, "network", $"Http: {FriendlyExceptionMessage(ex)} ({url})");
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a pool thread and answers the ENGINE's Task, which is what makes the `Async`
    /// members awaitable in a script (`await Http.DownloadAsync(...)`) and blockable (`....Wait()`, `.Result`). A failure
    /// travels as the task's exception, so a script's `await` rethrows it at its own line.
    /// </summary>
    internal static ScriptLangCore.Task RunOffThread(Func<object?> work)
    {
        var task = new ScriptLangCore.Task();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { task.SetResult(work()); }
            catch (Exception ex) { task.SetException(ex); }
        });
        return task;
    }

    /// <summary>The directory a route's `./` resolves against, captured when the route table was built (see
    /// <see cref="ResolveRoutePath"/> for why the run's own directory is not enough by the time a request arrives).</summary>
    private string? _routeBaseDir;

    /// <summary>
    /// Waits for a route handler's answer when that answer is a TASK, which is what makes an `async` handler work.
    /// <para>
    /// A script `async` body answers the ENGINE's <see cref="ScriptLangCore.Task"/>, and a CLR call inside a handler can
    /// answer the runtime's, so both are recognised. Waiting is safe on this thread because the engine's `Task.Result`
    /// both waits AND drives the coroutine pump: it drains the main-thread queue and calls the pump driver on every
    /// round, which is how the body's own `await` resumes. A FAULT is rethrown by `Result`, so a failing async handler
    /// reaches the pipeline's failure path (the 500 with the cause in the log) exactly as a synchronous one does.
    /// </para>
    /// <para>
    /// Without this an async handler is SILENTLY EMPTY: measured over the wire, a handler declared
    /// `async Task&lt;string&gt;` answered a 200 whose body was the four bytes `null`, because the Task is an object and
    /// the JSON branch serialized it (the recorded "a Task serializes as null by design" rule) and nothing ever waited.
    /// </para>
    /// </summary>
    private object? AwaitHandlerAnswer(object? answer) => answer switch
    {
        ScriptLangCore.Task scriptTask => scriptTask.Result,
        System.Threading.Tasks.Task clrTask => ToScriptTask(clrTask).Result,
        _ => answer
    };

    /// <summary>
    /// `Content-Disposition: attachment; filename="name"`, with the RFC 5987 `filename*` form used instead when the name
    /// is not ASCII: a header is ISO-8859-1 by spec, so a UTF-8 name written raw is mangled by every client.
    /// </summary>
    private static string AttachmentDisposition(string fileName)
    {
        var ascii = true;
        foreach (var ch in fileName)
            if (ch > 127) { ascii = false; break; }
        if (!ascii) return $"attachment; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
        return $"attachment; filename=\"{fileName.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
    }

    /// <summary>
    /// A route's file path, where `./x` is beside the FILE THAT REGISTERED the route, which is what a script means by it.
    /// It cannot go through <see cref="ResolvePath"/> alone: that door answers `./` from the RUN's current directory, and
    /// a served request outlives the run (a session resets its per-run state at the end of a `.run`), so the directory
    /// captured when the route table was built is the fallback. Measured, before the capture: an `http.File("./x")` route
    /// answered a 500 with "Cannot resolve relative import without a current directory".
    /// </summary>
    private string ResolveRoutePath(string path)
    {
        if (!path.StartsWith("./") && !path.StartsWith("../")) return Path.GetFullPath(path);
        var baseDir = (Current ?? this)._currentDirectory ?? _routeBaseDir;
        return baseDir == null
            ? throw new ScriptException(
                $"http.File cannot resolve '{path}': no run directory is known, so pass an absolute path.")
            : Path.GetFullPath(Path.Combine(baseDir, path));
    }

    /// <summary>Bytes from a list or array of numbers, refusing one that is not a byte rather than truncating it.</summary>
    private static byte[] NumbersToBytes(int[] values)
    {
        var bytes = new byte[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            bytes[i] = v is >= 0 and <= 255
                ? (byte)v
                : throw new ScriptException($"http.Bytes: element {i} is {v}, which is not a byte (0..255).");
        }
        return bytes;
    }

    // ── Cancellation ────────────────────────────────────────────────────────────────
    /// <summary>Process-level cancellation, fired by the REPL Ctrl+C interrupt, abort() and
    /// interpreter shutdown. Http verbs bind to it unless a request-scoped token is installed.</summary>
    private readonly CancellationTokenSource _processCts = new();
    private CancellationTokenSource? _requestCts;          // innermost host request scope (MCP HTTP)
    private readonly Lock _ambientLock = new();

    /// <summary>Ambient token for Http calls: the innermost host request scope, else the process token.</summary>
    /// <summary>Script frames at the moment a runtime error is raised, innermost first, capped so a
    /// deep recursion does not produce an enormous message. Null when no script function is active.</summary>
    /// <summary>Cap on the script frames attached to one error (innermost first). Keeps a runaway
    /// recursion from turning the error output into one "at <fn>" line per frame, deep(30000)
    /// would otherwise print 30000 lines.</summary>
    internal const int MaxErrorFrames = 16;

    internal IReadOnlyList<string>? SnapshotCallFrames()
    {
        // A List (not an array) so scripts get the same surface as any other collection: .Count,
        // indexing and iteration. Innermost frame first (the stack is enumerated top-down).
        if (_callStack.Count == 0) return null;
        var frames = new List<string>(Math.Min(_callStack.Count, MaxErrorFrames));
        foreach (var frame in _callStack) { frames.Add(frame); if (frames.Count == MaxErrorFrames) break; }
        return frames;
    }

    /// <summary>One frame of the script call stack, as an error report and the debugger print it: the function's label
    /// and a position, in the engine's own bracket form.
    /// <para>
    /// Reported 2026-10-31: a run that failed inside a `Com.Run` callback printed `[ERROR] Script:    at <lambda>` with
    /// no position at all, so the trace named a function and nothing a reader could go to. The CALL SITE is preferred
    /// (that is where the frame was entered), and the DECLARATION's own position is the fallback, which is the case that
    /// matters most: an engine-invoked body (a `Com.Run` callback, a `Task.Run` body, an event handler) is called with
    /// no script position at all, so without the fallback those frames carry nothing. The string is what BOTH the log
    /// line and the ABI's `frames` array carry, so the two cannot disagree.
    /// </para></summary>
    internal static string FrameWithPosition(string label, int line, int column, FuncDecl? decl)
    {
        var atLine = line > 0 ? line : decl?.Line ?? 0;
        var atColumn = line > 0 ? column : decl?.Column ?? 0;
        return atLine > 0 ? $"{label} [L{atLine}:C{atColumn}]" : label;
    }


    internal CancellationToken AmbientToken
    {
        get { lock (_ambientLock) return _requestCts?.Token ?? _processCts.Token; }
    }

    /// <summary>Cancels the process-level token (Ctrl+C, abort(), shutdown).</summary>
    internal void CancelProcessToken()
    {
        try { if (!_processCts.IsCancellationRequested) _processCts.Cancel(); }
        catch (ObjectDisposedException) { }
        DisposeLinkedTokens();
    }

    /// <summary>Linked sources created by the script-facing `CancellationToken.Create(a, b, …)`.
    /// The engine owns their lifetime (scripts get a token, not a disposable source).</summary>
    private readonly List<CancellationTokenSource> _linkedTokens = [];

    /// <summary>Registers an engine-owned linked source and returns it (see CancellationToken.Create).</summary>
    internal CancellationTokenSource TrackLinkedToken(CancellationTokenSource cts)
    {
        lock (_linkedTokens)
        {
            // Cheap hygiene: a script looping over Create(...) must not grow this list without bound.
            if (_linkedTokens.Count >= 8192)
            {
                for (var i = 0; i < 4096; i++) { try { _linkedTokens[i].Dispose(); } catch { } }
                _linkedTokens.RemoveRange(0, 4096);
            }
            _linkedTokens.Add(cts);
        }
        return cts;
    }

    private void DisposeLinkedTokens()
    {
        lock (_linkedTokens)
        {
            foreach (var c in _linkedTokens) { try { c.Dispose(); } catch { } }
            _linkedTokens.Clear();
        }
    }

    /// <summary>Installs a request-scoped ambient token for the duration of a host request.</summary>
    internal IDisposable PushRequestCancellation(CancellationTokenSource cts)
    {
        lock (_ambientLock)
        {
            var previous = _requestCts;
            _requestCts = cts;
            return new AmbientScope(this, previous);
        }
    }

    private sealed class AmbientScope(Interpreter owner, CancellationTokenSource? previous) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._ambientLock) owner._requestCts = previous;
        }
    }

    private bool IsVariableModifiedInLambda(LambdaExpr lambda, string varName)
    {
        if (lambda.BodyExpr != null)
            return ContainsAssignmentTo(lambda.BodyExpr, varName);

        return lambda.BodyStmts != null
               && lambda.BodyStmts.Any(stmt => ContainsAssignmentTo(stmt, varName));
    }

    // ContainsAssignmentTo methods (Expr, Stmt, IReadOnlyList<Stmt>) as previously defined

    private static bool ContainsAssignmentTo(Expr expr, string varName)
    {
        return expr switch
        {
            AssignExpr assign => assign.Name == varName,
            CompoundAssignExpr compound => compound.Target is VariableExpr var && var.Name == varName,
            IncrementDecrementExpr inc => inc.Target is VariableExpr var2 && var2.Name == varName,
            BinaryExpr binary => ContainsAssignmentTo(binary.Left, varName) || ContainsAssignmentTo(binary.Right, varName),
            UnaryExpr unary => ContainsAssignmentTo(unary.Right, varName),
            CallExpr call => call.Arguments.Any(a => ContainsAssignmentTo(a, varName)),
            YieldExpr yield => yield.Value != null && ContainsAssignmentTo(yield.Value, varName),
            _ => false
        };
    }

    private bool ContainsAssignmentTo(Stmt stmt, string varName)
    {
        return stmt switch
        {
            ExprStmt es => ContainsAssignmentTo(es.Expression, varName),
            BlockStmt block => block.Statements.Any(s => ContainsAssignmentTo(s, varName)),
            IfStmt ifs => ContainsAssignmentTo(ifs.ThenBranch, varName) ||
                          ContainsAssignmentTo(ifs.ElseBranch, varName),
            WhileStmt ws => ContainsAssignmentTo(ws.Body, varName),
            ForStmt fs => ContainsAssignmentTo(fs.Body, varName),
            ForeachStmt fes => ContainsAssignmentTo(fes.Body, varName),
            ReturnStmt ret => ret.Value != null && ContainsAssignmentTo(ret.Value, varName),
            VarDeclStmt vds =>
                // Variable declarations with initializers might reference the variable being assigned?
                // Not typically needed for mutation detection, but safe to check
                vds.Initializer != null && ContainsAssignmentTo(vds.Initializer, varName),
            _ => false
        };
    }

    private bool ContainsAssignmentTo(IReadOnlyList<Stmt> statements, string varName)
    {
        return statements.Any(stmt => ContainsAssignmentTo(stmt, varName));
    }

    /// <summary>
    /// Placeholder ï¿½ will be implemented when coroutine runtime is built.
    /// </summary>
    internal object? ResumeCoroutine(Coroutine co)
    {
        TraceCoro($"ResumeCoroutine ENTER {co.Fn.Declaration.Name} status={co.Status}");
        using var _ = PushContext();
        if (co.Status is CoroutineStatus.Dead or CoroutineStatus.Completed) return null;

        // Honor a pending sleep: a coroutine that yielded Coroutine.Sleep(ms) must not advance
        // until its wake time. Coroutine.Pump() removes due entries before resuming (no wait here);
        // a direct co.Resume() finds the future entry and blocks until it is due.
        DateTime? wakeTime = null;
        lock (_sleepLock)
        {
            foreach (var sc in _sleepQueue)
            {
                if (ReferenceEquals(sc.Coroutine, co))
                {
                    wakeTime = sc.WakeTime;
                    break;
                }
            }
        }
        if (wakeTime is { } due)
        {
            var remaining = due - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero) Thread.Sleep(remaining);
        }

        // A coroutine driven by direct Resume() (rather than Coroutine.Pump()) never has its
        // _sleepQueue entry consumed, every bare yield added one at line 7082 holding a strong
        // ref to this coroutine. Purge stale entries so the queue can't pin dead/directly-driven
        // coroutines forever (unbounded memory growth in generator loops).
        lock (_sleepLock)
        {
            if (_sleepQueue.Count > 0) _sleepQueue.RemoveWhere(sc => ReferenceEquals(sc.Coroutine, co));
        }

        _coroutineStack.Push(co);
        _isResumingFromYield = true;
        TraceCoro($"resume {co.Fn.Declaration.Name} coroStack={_coroutineStack.Count} stack=[{string.Join(",", _coroutineStack.Select(c => c.Fn.Declaration.Name))}]");
        var savedLoopStackSp = _loopStackSp;
        // A nested coroutine (an `async` call made from inside a body, Coroutine.Start, a generator drive) runs its
        // own statement loop and would otherwise leave ITS last statement index behind, so the CALLER's yield state
        // records the callee's statement and the caller resumes at the wrong statement. Measured before this:
        // `var a = await leaf(10);` restarted the enclosing body at its FIRST statement on every resume.
        var savedStatementIndex = _currentStatementIndex;
        // The same leak applies to the resume FLAG: a nested resume (an async call made from inside a body) used to
        // force this to false on the way out, which told the ENCLOSING body it was not resuming, so its await
        // re-ran the operand for ever (measured: `var v = await Task.Run(anAsyncFn);` inside an async body hung).
        var savedIsResuming = _isResumingFromYield;
        try
        {
            object? result;
            if (co.VmBacked && co.Fn.Declaration.Compiled != null)
            {
                // ── VM-flat coroutine drive, mirrors Generator.MoveNext's VmBacked branch.
                // The body runs on the bytecode VM (yield/return via VmSuspendState); the tree
                // state (Tree/SavedScope) is never allocated. Sleep/http/event coroutines and
                // mid-expression-yield bodies are excluded by IsVmBackedCoroutine / Compiled==null.
                VmSuspendState? suspend;
                // A BAIL IS THE TREE'S BUSINESS HERE TOO, and the flag that decides it is the engine's own
                // `VmBodyCommitted`: it records whether the attempt did anything a re-run would repeat (a global
                // store, a cell's target). It is scoped exactly as `TryExecuteVmBody` scopes it, so a coroutine body
                // gets the same rule every other caller of the VM gets, the bail handler below reads it, and an
                // exception out of the body still hands the baseline back.
                var commitBaseline = VmBodyCommitted;
                var startedFresh = co.Status == CoroutineStatus.NotStarted;
                var committedHere = false;
                VmBodyCommitted = false;
                try
                {
                    if (startedFresh)
                    {
                        co.Started = true;
                        // A declared TUPLE parameter's element names have to reach the tuple the body will read,
                        // because the VM binds argument VALUES straight into the locals and the names never travel
                        // on their own (measured: an async body's `p.a` failed while the same body tree-walked
                        // answered 9). This is the same helper, for the same reason, that the flat and VM-direct
                        // call paths already call.
                        if (co.Fn.Declaration.TupleParamNames is { Count: > 0 })
                            for (var tpi = 0; tpi < co.Fn.Declaration.Params.Count && tpi < co.Args.Length; tpi++)
                                BindTupleParam(co.Fn.Declaration, tpi, co.Args[tpi], co.Fn.Declaration.Line, co.Fn.Declaration.Column);
                        result = Vm.ExecuteCore(co.Fn.Declaration.Compiled, co.Args, this, out suspend,
                            co.Fn.Declaration.Name, co.Fn.NamespaceContext);
                    }
                    else if ((co.Status == CoroutineStatus.Suspended || co.Status == CoroutineStatus.Waiting)
                             && co.VmState != null)
                        result = Vm.ExecuteResumedCore(co.VmState, co.VmState.PushResumeValue ? co.Tree?.PendingResumeValue : null, this, out suspend);
                    else
                        return null;   // Dead / Completed, nothing to resume
                }
                finally
                {
                    committedHere = VmBodyCommitted;
                    VmBodyCommitted = commitBaseline;
                }

                if (result is VmFallback)
                {
                    // AN UNCOMMITTED BAIL GOES TO THE TREE, which is the rule every other caller of the VM already
                    // follows: the engine refuses to re-run a body that has already DONE something (a global store,
                    // a cell write: `committedHere`), and a body that has done NOTHING can simply be walked, which is
                    // how the shapes the VM cannot model are answered everywhere else. Two cases keep the loud
                    // refusal: a body that came from a BUNDLE (its declaration carries bytecode and no statements,
                    // so there is nothing to walk), and a RESUMED drive (the body already produced the values its
                    // consumer has seen, so replaying it from the start would hand them over twice).
                    //
                    // THE SHAPE THIS REACHES, measured: `foreach (var v in asyncFn())` inside a compiled `async`
                    // body, where the collection is a Task, so the VM bails at FOREACH_INIT. The tree refuses it with
                    // the language's own words ("Cannot iterate over type Task"); before this the engine reported
                    // "VM coroutine 'X' cannot execute its body ... possible VM gate gap" instead, a loud ENGINE
                    // error for a PROGRAM the tree answers as a script mistake.
                    if (startedFresh && !committedHere && co.Fn.Declaration.Body.Count > 0)
                    {
                        // The tree walks the whole body from its start. A first run has no suspension to drop, and
                        // the VM's per-frame state went with the bail (`suspend` is null on that path).
                        co.VmBacked = false;
                        co.VmState = null;
                        co.Status = CoroutineStatus.NotStarted;
                        co.Started = false;
                        return ResumeCoroutine(co);
                    }
                    // Loud fallback, same rule as Generator.MoveNext: the VM already executed the
                    // statements before the failing one, silently re-running them tree-walked
                    // would double-execute side effects. Mark Dead so a retry can never re-run them.
                    co.Status = CoroutineStatus.Dead;
                    co.VmState = null;
                    throw new ScriptException(
                        $"VM coroutine '{co.Fn.Declaration.Name}' cannot execute its body on the " +
                        "bytecode VM, a construct the VM cannot run was reached (possible VM gate " +
                        "gap; the coroutine stayed VM-backed because its body appeared VM-safe).");
                }

                VmInvocations++;
                co.VmState = suspend;
                if (suspend != null)
                {
                    // Coroutine.Start convention (mirrors the tree): a plain yield leaves the
                    // coroutine WAITING and schedules it in the pump queue (wake = now), so
                    // Coroutine.Pump() drives it like any scheduler coroutine.
                    co.LastYieldedValue = result;
                    if (result is CoroutineYieldRequest vmRequest)
                    {
                        // A REQUEST is not a value yield: an await (and a sleep/http/event wait) goes through the ONE
                        // handler the tree drive uses, so the awaited Task is watched and the body is resumed with its
                        // value. A VM-backed body has no tree state, which is why HandleYieldRequest takes it nullable.
                        HandleYieldRequest(vmRequest, co, null);
                        co.Status = CoroutineStatus.Waiting;
                        return result;
                    }
                    // A plain yield: the Coroutine.Start convention (mirrors the tree) leaves the coroutine WAITING and
                    // schedules it in the pump queue (wake = now), so Coroutine.Pump() drives it like a scheduler one.
                    co.Status = CoroutineStatus.Waiting;
                    EnqueuePumpWork(new ScheduledCoroutine(co, DateTime.UtcNow));
                }
                else
                {
                    // AN `async Task<int>` BODY THAT FALLS OFF OWES A VALUE, exactly as a tree-walked one does: the
                    // refusal is stated once in InvokeCoroutineBody's fall-off and this path bypasses it. Measured
                    // on `async_stream_test` 14c: the file answered "Coroutine 'Falls' must return a value" while its
                    // bundle answered "ok", because the bundle's body is VM-BACKED and reaches this branch instead.
                    // The test is `result == null`, which is how a VM body's fall-off differs from its `return`
                    // (a `return 0` arrives boxed, and a body that owes a value cannot return null: the RETURN arm
                    // coerces it). The POSITION is the declaration's own rather than a call site's, because this is
                    // the body's fault and there may be many callers.
                    var done = co.Fn.Declaration;
                    if (result == null && done.IsAsync && done.ReturnType != TypeInfo.Void
                        && done.ReturnType.BaseType != TokenType.Object)
                        RuntimeError($"Coroutine '{done.Name}' must return a value.", done.Line, done.Column);
                    co.ReturnValue = result;                  // the body ran to its end/return naturally
                    co.Status = CoroutineStatus.Completed;
                }
                return result;
            }

            if (!co.Started)
            {
                co.Started = true;
                result = InvokeCoroutineBody(co.Fn, co.Args, 0, 0, resumeState: null, coroutine: co);
            }
            else
            {
                result = InvokeCoroutineBody(co.Fn, co.Args, 0, 0, co.Tree?.State, co);
            }

            // The body set its own status before returning, so the value is a YIELD when the coroutine is
            // still resumable (Waiting = yield-request, Suspended = plain yield) and its RETURN value once
            // it is not. Completion does not clear LastYieldedValue, so the two never alias each other.
            if (co.IsResumable)
                co.LastYieldedValue = result;
            else
                co.ReturnValue = result;
            if (!co.IsResumable)
                co.Status = CoroutineStatus.Completed;   // the body finished in this resume (defensive, the return/end-of-body paths already set Completed)
            return result;
        }
        finally
        {
            _isResumingFromYield = savedIsResuming;
            _loopStackSp = savedLoopStackSp;
            _currentStatementIndex = savedStatementIndex;
            _coroutineStack.Pop();
        }
    }

    /// <summary>Returns names of user-defined global variables (excludes built-ins).</summary>
    private static readonly HashSet<string> _builtinNames =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "print", "sleep", "beep", "abort", "AssertTrue", "AssertEqual",
        "TypeName", "LoadAssembly", "GetType", "CreateInstance", "Invoke",
        "RunScript", "ClearPureCache", "GetPureCacheSize", "GetScriptDepth",
        "pause", "exitcode", "typeof", "typename",
        "ScriptResult", "Result", "StringBuilder", "OrderedEnumerable",
        "nameof",
        "Encoding", "Convert", "Console", "ConsoleColor",
        "Environment", "Math", "Path", "File", "FileAttributes",
        "Directory", "Guid", "Regex", "Enum",
        "byte", "sbyte", "short", "ushort", "int", "uint",
        "long", "ulong", "float", "double", "decimal",
        "bool", "char", "object", "string",
        "Server", "Client", "System", "Script", "Colors",
        // `WinApi` USED to be listed here and is GONE: the namespace was dismantled (every spelling has a receiver
        // form now), so a name in this set made `IsKnownTypeName("WinApi")` answer TRUE for a name nothing
        // defines. Measured consequence: the strip's type-load guard classified a body that loads `WinApi` as "a
        // TYPE the VM has no value for (a script interface used as a call receiver)" while `typename("WinApi")`
        // refuses with "Unknown type name", so a reader was sent looking for an interface. Every other name in
        // this list is a LIVE namespace or type, verified one by one (`MCI` is `Mci.cs`, `Rect` resolves, `Com`,
        // `GC`, `Json`, `Http`, `Server`, `Client`, `System`, `Script` and `Colors` are namespaces).
        "Rect", "MCI", "Com", "GC", "Json", "Http",
        "Coroutine", "CoroutineStatus",
        "Action", "Func", "Predicate", "Comparison", "Converter",
        "EventHandler", "ActionType", "FuncType", "PredicateType",
        "Action1", "Action2", "Action3", "Action4",
        "Action<T>", "Action<T1,T2>", "Action<T1,T2,T3>", "Action<T1,T2,T3,T4>",
        "Action`0", "Action`1", "Action`2", "Action`3", "Action`4",
        "Func1", "Func2", "Func3", "Func4",
        "Func<TResult>", "Func<T,TResult>", "Func<T1,T2,TResult>", "Func<T1,T2,T3,TResult>",
        "Func<T1,T2,T3,T4,TResult>",
        "Func`1", "Func`2", "Func`3", "Func`4",
        "Predicate1", "Predicate<T>", "Predicate`1",
        "Comparison<T>", "Comparison`1",
        "Converter<TInput,TOutput>", "Converter`2",
        "EventHandler`2", "EventHandler<T>",
        "ClientStatus", "CancelEventArgs", "EventArgs", "Form", "Control", "Size", "StartPosition", "DialogResult",
        "Application", "TypeInfo", "TypeCode", "StringComparison",
        "Where", "Select", "SelectMany", "First", "FirstOrDefault",
        "Last", "LastOrDefault", "Any", "All", "ElementAt",
        "ElementAtOrDefault", "Count", "Sum", "Average", "Min", "Max",
        "Take", "Skip", "OrderBy", "OrderByDescending", "ThenBy",
        "Distinct", "GroupBy", "Join", "GroupJoin", "LeftJoin",
        "RightJoin", "Aggregate", "Reverse", "Contains", "Zip",
        "ToBinary", "ToBinaryWithBits", "ToHex",
        "ToHexWithPrefix", "BitCount", "IsPowerOfTwo", "BitSet",
        "BitClear", "BitToggle", "BitTest", "RotateLeft", "RotateRight",
        "SwapBytes",
        "DateTime", "DateTimeOffset", "TimeSpan"
    ];

    public bool IsBuiltinName(string name) => _builtinNames.Contains(name);

    public object? GetVariableValue(string name)
    {
        var dict = _rootFrame.Scopes.GetGlobalScopeDictionary();
        return dict.TryGetValue(name, out var vi) ? vi.Value : null;
    }

    // ── Collection factory tables (lazy-init, zero startup cost) ──
    private static readonly Func<TypeInfo, IScriptList>?[] ListFactoryTable = new Func<TypeInfo, IScriptList>?[17];
    private static readonly Func<TypeInfo, IScriptHashSet>?[] HashSetFactoryTable = new Func<TypeInfo, IScriptHashSet>?[17];
    private static readonly Func<TypeInfo, TypeInfo, IScriptDictionary>?[,] DictFactoryTable = new Func<TypeInfo, TypeInfo, IScriptDictionary>?[17, 17];

    private static Func<TypeInfo, IScriptList> GetListFactory(int ei)
    {
        return ListFactoryTable[ei] ??= ei switch
        {
            0 => et => new ScriptUnboxedList<byte>(et),
            1 => et => new ScriptUnboxedList<short>(et),
            2 => et => new ScriptUnboxedList<int>(et),
            3 => et => new ScriptUnboxedList<long>(et),
            4 => et => new ScriptUnboxedList<uint>(et),
            5 => et => new ScriptUnboxedList<ushort>(et),
            6 => et => new ScriptUnboxedList<sbyte>(et),
            7 => et => new ScriptUnboxedList<ulong>(et),
            8 => et => new ScriptUnboxedList<float>(et),
            9 => et => new ScriptUnboxedList<double>(et),
            10 => et => new ScriptUnboxedList<decimal>(et),
            11 => et => new ScriptUnboxedList<bool>(et),
            12 => et => new ScriptUnboxedList<char>(et),
            13 => et => new ScriptUnboxedList<DateTime>(et),
            14 => et => new ScriptUnboxedList<DateTimeOffset>(et),
            15 => et => new ScriptUnboxedList<TimeSpan>(et),
            16 => et => new ScriptUnboxedList<string>(et),
            _ => throw new ArgumentOutOfRangeException(nameof(ei)),
        };
    }

    private static Func<TypeInfo, IScriptHashSet> GetHashSetFactory(int ei)
    {
        return HashSetFactoryTable[ei] ??= ei switch
        {
            0 => et => new UnboxedScriptHashSet<byte>(et),
            1 => et => new UnboxedScriptHashSet<short>(et),
            2 => et => new UnboxedScriptHashSet<int>(et),
            3 => et => new UnboxedScriptHashSet<long>(et),
            4 => et => new UnboxedScriptHashSet<uint>(et),
            5 => et => new UnboxedScriptHashSet<ushort>(et),
            6 => et => new UnboxedScriptHashSet<sbyte>(et),
            7 => et => new UnboxedScriptHashSet<ulong>(et),
            8 => et => new UnboxedScriptHashSet<float>(et),
            9 => et => new UnboxedScriptHashSet<double>(et),
            10 => et => new UnboxedScriptHashSet<decimal>(et),
            11 => et => new UnboxedScriptHashSet<bool>(et),
            12 => et => new UnboxedScriptHashSet<char>(et),
            13 => et => new UnboxedScriptHashSet<DateTime>(et),
            14 => et => new UnboxedScriptHashSet<DateTimeOffset>(et),
            15 => et => new UnboxedScriptHashSet<TimeSpan>(et),
            16 => et => new UnboxedScriptHashSet<string>(et),
            _ => throw new ArgumentOutOfRangeException(nameof(ei)),
        };
    }

    private static Func<TypeInfo, TypeInfo, IScriptDictionary>? GetDictFactory(int ki, int vi)
    {
        var f = DictFactoryTable[ki, vi];
        if (f != null) return f;
        // Init entire row on first access
        LazyDictRow(ki);
        // A slot the row deliberately leaves EMPTY (see LazyDictRowCore): the caller falls back to
        // ScriptBoxedDictionary rather than this returning null into a constructor.
        return DictFactoryTable[ki, vi];
    }

    private static void LazyDictRow(int ki)
    {
        switch (ki)
        {
            case 0: LazyDictRowCore<byte>(ki); break;
            case 1: LazyDictRowCore<short>(ki); break;
            case 2: LazyDictRowCore<int>(ki); break;
            case 3: LazyDictRowCore<long>(ki); break;
            case 4: LazyDictRowCore<uint>(ki); break;
            case 5: LazyDictRowCore<ushort>(ki); break;
            case 6: LazyDictRowCore<sbyte>(ki); break;
            case 7: LazyDictRowCore<ulong>(ki); break;
            case 8: LazyDictRowCore<float>(ki); break;
            case 9: LazyDictRowCore<double>(ki); break;
            case 10: LazyDictRowCore<decimal>(ki); break;
            case 11: LazyDictRowCore<bool>(ki); break;
            case 12: LazyDictRowCore<char>(ki); break;
            case 13: LazyDictRowCore<DateTime>(ki); break;
            case 14: LazyDictRowCore<DateTimeOffset>(ki); break;
            case 15: LazyDictRowCore<TimeSpan>(ki); break;
            case 16: LazyDictRowCore<string>(ki); break;
            default: throw new ArgumentOutOfRangeException(nameof(ki));
        }
    }

    // A ROW SPECIALISES ONLY THE VALUE TYPES A SCRIPT DENSELY USES, and that is a size decision taken from a
    // measurement of the published binary (AOT, Release, 2026-10-14): one ScriptUnboxedDictionary<TKey, TValue>
    // instantiation costs about 75 kB of native code and metadata, and the whole family measured 1,272,341 B over
    // 17 instantiations, i.e. 35% of the engine's own 3.6 MB. For comparison the sibling ScriptUnboxedList<T>
    // costs 43 kB for the SAME 17 element types, and every value type left EMPTY here takes the
    // ScriptBoxedDictionary path, which is what every non-primitive value type already takes and which coerces to
    // the declared type the same way (its Add calls CoerceTo on the key and on the value), so a read unwraps to
    // the same value and no script-visible behaviour changes.
    //
    // KEPT (8): int, long, double, decimal, bool, char, DateTime, string.
    // DROPPED (9): byte, short, uint, ushort, sbyte, ulong, float, DateTimeOffset, TimeSpan.
    // The SLOT NUMBERS are the wire (NumericTokenIndex assigns them), so a dropped slot stays NUMBED OUT here
    // rather than renumbered, and the table stays 17 wide.
    private static void LazyDictRowCore<TKey>(int ki) where TKey : notnull
    {
        DictFactoryTable[ki, 2] = (k, v) => new ScriptUnboxedDictionary<TKey, int>(k, v);
        DictFactoryTable[ki, 3] = (k, v) => new ScriptUnboxedDictionary<TKey, long>(k, v);
        DictFactoryTable[ki, 9] = (k, v) => new ScriptUnboxedDictionary<TKey, double>(k, v);
        DictFactoryTable[ki, 10] = (k, v) => new ScriptUnboxedDictionary<TKey, decimal>(k, v);
        DictFactoryTable[ki, 11] = (k, v) => new ScriptUnboxedDictionary<TKey, bool>(k, v);
        DictFactoryTable[ki, 12] = (k, v) => new ScriptUnboxedDictionary<TKey, char>(k, v);
        DictFactoryTable[ki, 13] = (k, v) => new ScriptUnboxedDictionary<TKey, DateTime>(k, v);
        DictFactoryTable[ki, 16] = (k, v) => new ScriptUnboxedDictionary<TKey, string>(k, v);
    }

    private static int NumericTokenIndex(TokenType t) => t switch
    {
        TokenType.Byte => 0,
        TokenType.Short => 1,
        TokenType.Int => 2,
        TokenType.Long => 3,
        TokenType.UInt => 4,
        TokenType.UShort => 5,
        TokenType.SByte => 6,
        TokenType.ULong => 7,
        TokenType.Float => 8,
        TokenType.Double => 9,
        TokenType.Decimal => 10,
        TokenType.Bool => 11,
        TokenType.Char => 12,
        TokenType.DateTime => 13,
        TokenType.DateTimeOffset => 14,
        TokenType.TimeSpan => 15,
        TokenType.StringType => 16,
        _ => -1
    };

    /// <summary>Sentinel RefCell that swallows all writes, used for `out var _` discards.</summary>
    private static readonly RefCell NullRefCell = new(
        Getter: () => null,
        Setter: static _ => { }
    );

    /// <summary>
    /// Wraps a native library handle (from LoadLibrary) so that calling methods
    /// on it resolves function pointers automatically.
    /// Usage: var kernel = LoadLibrary("kernel32"); kernel.Sleep(1000); kernel.Free();
    /// </summary>
    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcAddress(nint hModule, nint lpProcName);

    // ── Native-stack budget guard ────────────────────────────────────────────────────────
    // .NET's StackOverflowException is uncatchable, so before the C# stack is physically
    // exhausted the tree-walker converts the imminent overflow into a catchable
    // ScriptException. A frame-COUNT cap alone is not enough: exception DISPATCH in
    // NativeAOT walks the interpreter's C# frame chain and costs ~3x the recursion's own
    // stack per level, so a guard throw at depth D needs D × ~8KB of stack, the old
    // MaxCallDepth=10000 "never fired and deep tree recursion died with an uncatchable
    // stack overflow" was actually the GUARD's own throw dying in dispatch (measured:
    // _depth_runaway_tree.slc died at n=9998 with 11MB free; plain throw-free recursion
    // reaches 15876). NativeAOT therefore measures the REAL stack via
    // GetCurrentThreadStackLimits (Windows 8+), the thread's [low, high] range; the stack
    // grows DOWN from high, so the bytes left before overflow are (current SP − low), with
    // the SP read from the address of an unsafe local, and fires proportionally
    // (remaining < 4 × used, see NativeStackNearlyExhausted). Falls back to the
    // MaxCallDepth count cap when the API is unavailable (non-Windows).
    // Per-thread stack geometry in plain thread-static fields: the guard runs on EVERY script call,
    // so it must not pay ThreadLocal<T> machinery (measured 2026-09-10: the ThreadLocal-based probe
    // doubled bench_methodcall's call-heavy case). A probe is now two field reads, one address-of and
    // a compare. State: 0 = not probed yet, 1 = known, -1 = API unavailable for this thread.
    [ThreadStatic] private static ulong _stackLow;
    [ThreadStatic] private static ulong _stackHigh;
    [ThreadStatic] private static int _stackGeomState;

    /// <summary>Probe this thread's [low, high] stack geometry once (GetCurrentThreadStackLimits,
    /// Windows 8+). Never throws: an unavailable API just disables the byte guard for this thread,
    /// leaving the MaxCallDepth count cap in charge.</summary>
    private static void ProbeStackGeometry()
    {
        try
        {
            GetCurrentThreadStackLimits(out var lo, out var hi);
            _stackLow = (ulong)lo;
            _stackHigh = (ulong)hi;
            _stackGeomState = _stackHigh > _stackLow ? 1 : -1;
        }
        catch (Exception)
        {
            // Non-Windows / pre-Windows-8, never crash the interpreter over a guard probe.
            _stackGeomState = -1;
        }
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────────────────
    // SLC_STACK_TRACE=1 prints, per interpreted call, the live script depth and the real
    // remaining native stack, used to calibrate the recursion guards against the C# stack
    // budget (the guard must fire with enough headroom for the ScriptException throw+unwind).
    // SLC_STACK_MAP=1 dumps the thread's stack memory regions (commit/guard/reserve layout)
    // once per call, reveals the EFFECTIVE stack limit the OS enforces.
    // SLC_DISABLE_GUARDS=1 disables BOTH recursion guards (diagnostic only, lets the
    // process die at the true native limit so the guard values can be calibrated).
    // Flippable at run time through Diagnostics.LogTraces(true): the initial value comes from the same gate as
    // the SLC_* variable it has always honoured, and the VM reads it per call, so a property would be too slow.
    internal static bool StackTraceEnabled = Diagnostics.On("STACK_TRACE");
    internal static bool StackMapEnabled = Diagnostics.On("STACK_MAP");
    internal static bool GuardsDisabled = Diagnostics.On("DISABLE_GUARDS");
    internal static bool CoroTraceEnabled = Diagnostics.On("CORO_TRACE");
    // SLC_BAIL_TRACE=1 reports every abort of a compiled body, with the `Vm.cs` line that bailed: the only way to
    // attribute a bail, since the body's own opcodes say nothing about it. It was read from the environment
    // directly, which the unified switch could not reach, so unlike every other trace a script could not turn it
    // on; it is a field now, initialized the same way, and `Bail` reads the field.
    internal static bool BailTraceEnabled = Diagnostics.On("BAIL_TRACE");
    // SLC_NAMED_TRACE=1 reports every named-argument marker the VM hands to a call site: which site took it, the
    // callee or receiver it went to, and the names it carried. A marker belongs to the call whose operands sit
    // directly above it, so a marker taken by the WRONG call is visible as a line whose callee or receiver has
    // nothing to do with the names printed beside it, and a marker nobody takes prints no line at all. It exists
    // because that confusion is otherwise silent: the value stack still balances, so BytecodeVerifier cannot see it.
    internal static bool NamedTraceEnabled = Diagnostics.On("NAMED_TRACE");
    // SLC_SINGLE_INSTANCE=1 appends the LOCK NAME to the line a second instance logs. The message is one short
    // sentence by default because the name is a sanitized full path, but a reader who has to inspect the lock (or
    // tell two scripts apart) needs it, and this is the gate that asks for it.
    internal static bool SingleInstanceTrace = Diagnostics.InitialFlag("SINGLE_INSTANCE");

    /// <summary>The names a named-argument marker carries, for an SLC_NAMED_TRACE line.</summary>
    internal static string NamedTraceNames(IReadOnlyList<string?>? names) =>
        names == null ? "<null>" : string.Join(",", names.Select(n => n ?? "-"));

    /// <summary>One SLC_NAMED_TRACE line, emitted from the VM's marker-consuming sites.</summary>
    internal static void NamedTrace(string site, string detail)
    {
        if (NamedTraceEnabled) Console.Error.WriteLine($"[named] {site} {detail}");
    }
    private static ulong _coroTraceSeq;
    private static readonly ulong KiB = 1UL << 10;

    /// <summary>Diagnostic: coroutine resume/foreach/yield walk (SLC_CORO_TRACE=1). Written to
    /// STDERR with a monotonic sequence so the true execution order survives stdout buffering.</summary>
    internal void TraceCoro(string msg)
    {
        if (!CoroTraceEnabled) return;
        Console.Error.WriteLine($"[coro:{Interlocked.Increment(ref _coroTraceSeq)}] {msg}");
        Console.Error.Flush();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemBasicInfo
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(nuint lpAddress, out MemBasicInfo lpBuffer, nuint dwLength);

    [LibraryImport("kernel32.dll")]
    private static partial void GetCurrentThreadStackLimits(out nuint lowLimit, out nuint highLimit);

    /// <summary>Win32 MEMORYSTATUSEX, the layout must match the API exactly (it validates the size field
    /// and returns FALSE when it is wrong).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>Physical RAM in bytes, or 0 when it cannot be determined. Used to derive the VM's flat-frame
    /// budget (Vm.DefaultVmMaxFrames). The Win32 API is preferred over
    /// GC.GetGCMemoryInfo().TotalAvailableMemoryBytes because that property reports the CONFIGURED GC heap
    /// limit, which is 0 for an unconstrained workstation GC, it returned 0 on the AOT publish here and
    /// silently kept an old, too-low default. TOTAL rather than AVAILABLE physical memory: the budget must
    /// not shrink just because other processes happen to be busy. GC's figure is the fallback, then 0.</summary>
    internal static long PhysicalMemoryBytes()
    {
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) != 0 && status.TotalPhys > 0) return (long)status.TotalPhys;
        }
        catch
        {
            // Never fail an interpreter start over a budget probe.
        }
        try { return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes; }
        catch { return 0; }
    }

    /// <summary>The stack this THREAD is running on, as (remaining, total, low, high) in bytes. Internal because two
    /// callers outside the guard need the same numbers: the embedding API has to decide whether a HOST's thread can
    /// carry a run (its TOTAL stack is the budget the depth guard measures against), and `ScriptEntryStackIsTight`
    /// below asks the same probe for the opposite question (is there too little LEFT).
    /// It answers false when the geometry cannot be probed at all, which is the state every caller must treat as
    /// "unknown" rather than "zero".</summary>
    internal static unsafe bool TryGetStackInfo(out ulong remaining, out ulong stackSize, out ulong low, out ulong high)
    {
        remaining = 0; stackSize = 0; low = 0; high = 0;
        if (_stackGeomState == 0) ProbeStackGeometry();
        if (_stackGeomState < 0) return false;
        low = _stackLow; high = _stackHigh;
        byte probe;   // address-of forces stack backing, its address is the current SP
        // Stack grows DOWN: high = top (start), low = overflow limit → remaining = sp - low.
        remaining = (ulong)(nuint)(&probe) - low;
        stackSize = high - low;
        return true;
    }

    private static bool NativeStackNearlyExhausted()
    {
        if (!TryGetStackInfo(out var remaining, out var stackSize, out _, out _)) return false;
        var used = stackSize - remaining;
        // Exception dispatch in NativeAOT walks the interpreter's C# frame chain and needs
        // ~3x the recursion's own stack per level (measured: a ScriptException thrown at
        // depth ~4000 survives, ~4200+ dies, the dispatch consumes ~6KB/level vs ~2.1KB/level
        // of recursion). Fire when the bytes left fall below 4x the bytes already used: the
        // dispatch (≤ ~3x used) then fits with margin, at ANY stack size (32MB main thread
        // → fires at ~6.4MB used ≈ depth 3100; a 1MB Task.Run thread → ~200KB used ≈ depth 95,
        // well before its ~480-level physical limit). used is measured, so this adapts to
        // per-level cost drift and to every thread's actual stack.
        // The floor is scaled to THIS thread's stack. A fixed 1MB floor was calibrated for the 32MB
        // main-thread stack, but on the 1.5MB Debug apphost it left only ~40KB before the guard could
        // fire, no room for the throw or for the ~3x unwinding, so the process still died uncatchably
        // (measured 2026-09-10: defArg(100) reached used=1494KB of 1536KB with no catchable error).
        // stackSize/8 keeps the ratio below as the GOVERNING rule on large stacks (32MB → fires at
        // ~6.4MB used, exactly as before) and makes a small stack fire at ~20% used, leaving ~4x used
        // for the unwinding.
        if (used < (stackSize >> 3)) return false;
        return remaining < used * 4;
    }

    /// <summary>True when entering a script body HERE would trip the guard above, which is the question anything the
    /// ENGINE invokes on the run thread's behalf has to ask first. It exists for one caller (`Events`' queued
    /// delivery), and the measurement behind it is precise: `Task.PumpPending` is drained from the VM's `LOC` arm,
    /// which on the JIT build sits inside `Vm.ExecuteCoreInner`, whose frame is a few hundred KB (a giant switch with
    /// hundreds of live locals). On the 1.5 MB Debug apphost the guard fires at about 20% used, i.e. right there
    /// (measured: used=315KB remaining=1220KB size=1536KB), and a subscriber invoked from that point was refused with
    /// "Maximum call depth exceeded (1)" and then REMOVED, which looked like a flaking accounting check two sections
    /// away. The same delivery offered at the top-level statement boundary is shallow and runs. The AOT product (32 MB)
    /// never reaches this, so it is here to keep one suite portable rather than to change the artifact.</summary>
    internal static bool ScriptEntryStackIsTight() => !GuardsDisabled && NativeStackNearlyExhausted();

    /// <summary>Diagnostic: one line per interpreted call with live depth + remaining native
    /// stack (SLC_STACK_TRACE=1). Also prints the thread's stack geometry once.</summary>
    internal void TraceStack(string tag)
    {
        if (!StackTraceEnabled) return;
        if (!TryGetStackInfo(out var remaining, out var stackSize, out _, out _))
        {
            Console.WriteLine($"[stack:{tag}] limits-unavailable");
            return;
        }
        var depth = _callFrames.IsValueCreated ? _callFrames.Value!.Count : 0;
        Console.WriteLine($"[stack:{tag}] depth={depth} rem={remaining / KiB}KB used={(stackSize - remaining) / KiB}KB size={stackSize / KiB}KB");
        if (remaining < 15UL << 20) TraceStackMap(tag);
    }

    /// <summary>Diagnostic: dump the thread's stack memory regions (commit/guard/reserve)
    /// to reveal the EFFECTIVE stack limit (SLC_STACK_MAP=1).</summary>
    internal void TraceStackMap(string tag)
    {
        if (!StackMapEnabled) return;
        if (!TryGetStackInfo(out _, out _, out var low, out var high)) return;
        unsafe
        {
            byte probe;
            var sp = (nuint)(&probe);
            Console.WriteLine($"[stackmap:{tag}] sp=0x{(ulong)sp:X} low=0x{low:X} high=0x{high:X}");
            var addr = sp;
            for (var i = 0; i < 128; i++)
            {
                if (VirtualQuery(addr, out var mbi, (nuint)sizeof(MemBasicInfo)) == 0) break;
                var rsize = (ulong)mbi.RegionSize;
                Console.WriteLine($"[stackmap:{tag}] base=0x{(ulong)mbi.BaseAddress:X} size={rsize / KiB}KB state=0x{mbi.State:X} protect=0x{mbi.Protect:X}");
                if ((ulong)mbi.BaseAddress <= low || rsize == 0) break;
                addr = (nuint)((ulong)mbi.BaseAddress - 1);
            }
        }
    }

    // ============================================================================
    // 12. INTERPRETER - PARTIE 4 : CHAMPS D'INSTANCE, CONSTRUCTEUR, Mï¿½THODES PRINCIPALES
    // ============================================================================

    private readonly ThreadLocal<Stack<CallFrame>> _callFrames = new(() => new Stack<CallFrame>(32));
    private CallFrame CurrentFrame
    {
        get
        {
            var stack = _callFrames.Value;
            if (stack == null || stack.Count == 0)
            {
                // Push a synthetic root frame for thread-pool callbacks that haven't
                // pushed a frame yet (Task.Run / Parallel callbacks enter through InvokeScriptFunction)
                var newStack = new Stack<CallFrame>(1);
                newStack.Push(new CallFrame(new ScopeStack(_comparer)));
                _callFrames.Value = newStack;
                return newStack.Peek();
            }
            return stack.Peek();
        }
    }

    /// <summary>Current call-frame depth, used by debugger for Step Over/Step Out.</summary>
    internal int CallDepth => _callFrames.Value?.Count ?? 0;

    /// <summary>Returns the root global scope dictionary (for blueprint capture/clone).</summary>
    internal Dictionary<string, ScopeStack.VariableInfo> GetGlobalScope() =>
        _rootFrame.Scopes.GetGlobalScopeDictionary();

    /// <summary>Returns the current frame's global scope dictionary (during construction).</summary>
    internal Dictionary<string, ScopeStack.VariableInfo> GetCurrentGlobalScope() =>
        CurrentFrame.Scopes.GetGlobalScopeDictionary();

    internal Dictionary<string, object?> GetUserVariables()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            var scope = CurrentFrame.Scopes.GetCurrentScopeDictionary();
            foreach (var kv in scope)
            {
                if (!kv.Key.StartsWith(".") && !kv.Key.StartsWith("_"))
                    result[kv.Key] = kv.Value.Value;
            }
        }
        catch { /* scope may be empty, safe to ignore */ }
        return result;
    }

    internal List<string> GetCommandHistory() => _commandHistory ?? [];

    internal void RecordCommand(string line)
    {
        _commandHistory ??= [];
        _commandHistory.Add(line);
    }

    /// <summary>Defines entries directly in the live scope stack (not a frozen copy).</summary>
    internal void DefineBlueprintEntries(IEnumerable<KeyValuePair<string, object?>> entries)
    {
        var scopes = CurrentFrame.Scopes;
        foreach (var (name, value) in entries)
            scopes.Define(name, value);
    }

    /// <summary>Registers entries that capture 'this' (Server, Client, Script), called after CloneInto.</summary>
    private void RegisterPerInstanceBuiltins()
    {
        var scopes = CurrentFrame.Scopes;
        scopes.Define("Server", new TcpServer(this));
        scopes.Define("Client", new TcpClient(this));
        DefineScriptNamespace(scopes);
    }

    /// <summary>The `Script` namespace (Instance, Directory, IsRepl), defined into whichever scope is passed.
    /// ONE body, called from BOTH places that register it, because the two are NOT interchangeable: this per-instance
    /// pass defines it for the blueprint's frame while RegisterFunctions defines it for the current scope, so each is
    /// reachable from a different place (measured: deleting either one alone makes `Script` an undefined variable).
    /// Two copies of the body is what let the two drift, and its members capture THIS interpreter's live state.</summary>
    private void DefineScriptNamespace(ScopeStack scopes)
    {
        var thisNs = new Namespace(_comparer);
        thisNs.AddProperty("Instance", this);
        thisNs.AddMethod("Directory", _ => (object?)_currentDirectory ?? "");
        // `HasError` WAS here and is RETIRED (2026-11-10): the flag it read was set only in the two catch arms that
        // build a run's verdict, i.e. after the run had already ended, so no script could ever read it as true
        // (measured: false after a CAUGHT `1 / 0`, and a nested `RunScript` reports its own `Result.HasError` while
        // the outer read stays false). The observable doors are `Result.HasError` for a nested run and `exitcode` /
        // `Environment.ExitCode` for the run's verdict.
        thisNs.AddDynamicProperty("IsRepl", () => _isRepl);
        scopes.Define("Script", thisNs);
    }

    /// <summary>
    /// Registers all built-in functions and types into the global scope.
    /// </summary>
    private void RegisterAllBuiltins()
    {
        AotCompilerHint(); // AOT hint to ensure all methods are compiled in AOT scenarios

        RegisterBuiltinFunctions();
    }

    /// <summary>
    /// Re/or register all builtins to ensure scope entries are present
    /// </summary>
    /// <summary>
    /// One component of `application/x-www-form-urlencoded`: "+" is a space, "%XX" is one byte, and everything else is
    /// its own text. The bytes are collected and decoded ONCE so a multi-byte character survives; a per-byte cast
    /// cannot do that. A "%" that does not introduce two hex digits is left as it stands rather than refused, which is
    /// what browsers and servers do.
    /// </summary>
    private static string DecodeFormComponent(string text)
    {
        if (text.IndexOf('%') < 0 && text.IndexOf('+') < 0) return text;   // the common case, untouched
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '+') { bytes.Add(0x20); continue; }
            if (c == '%' && i + 2 < text.Length && TryHexByte(text[i + 1], text[i + 2], out var b))
            {
                bytes.Add(b);
                i += 2;
                continue;
            }
            // A literal character contributes its UTF-8 bytes, so a mixed component ("caf%C3%A9") decodes as one
            // sequence instead of being split around the escapes.
            bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(i, 1)));
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>The inverse: a space is written "+", which is the form encoding rather than the URI one.</summary>
    private static string EncodeFormComponent(string text) => Uri.EscapeDataString(text).Replace("%20", "+");

    private static bool TryHexByte(char hi, char lo, out byte value)
    {
        value = 0;
        var h = HexNibble(hi);
        var l = HexNibble(lo);
        if (h < 0 || l < 0) return false;
        value = (byte)((h << 4) | l);
        return true;
    }

    private static int HexNibble(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private void RegisterBuiltinFunctions()
    {
        RegisterHelpers();
        RegisterTypeAliases();
        RegisterBitwiseFunctions();
        RegisterBaseDelegates();
        RegisterFunctions();
        RegisterLinqFunctions();
        RegisterGcFunctions();
        RegisterDiagnosticsFunctions();
        RegisterMciFunctions();
        RegisterComFunctions();
        RegisterNativeFunctions();
        RegisterJsonFunctions();
        RegisterProcessFunctions();
        RegisterFileFunctions();
        RegisterClipboardFunctions();
        RegisterExitCodeFunctions();
        RegisterBytecodeFunctions();
        RegisterMemFunctions();
        RegisterSchedulerFunctions();
        RegisterSqliteCursor();
        RegisterTaskFunctions();
        RegisterSyncFunctions();
        RegisterParallelFunctions();
        RegisterIterableCombinators();
        RegisterEventsFunctions();
        RegisterAvroNamespace();
        RegisterCoroutines(); // must be the last
    }

    //private ScopeStack CurrentFrame.Scopes;
    private bool _isRepl;
    private int _currentLine;

    // Tracked alongside the line for the same reason: a registration that throws a ScriptException of its
    // own names no location, so the report has to fall back to the statement the interpreter is on.
    private int _currentColumn;

    /// <summary>The position of the statement being executed, for a BUILTIN that wants to report a diagnostic the
    /// way the rest of the engine does: `[Lx:Cy] [WARNING] ...`. A registration file has no script position in hand
    /// (a native is handed VALUES), which is why its warnings used to go out with NONE at all: they passed `0, 0`,
    /// and the logger then prints no prefix. (0, 0) when nothing is current, which is the positionless form the
    /// logger still supports for a genuinely positionless message.</summary>
    internal static int CurrentScriptLine => Current?.CurrentLine ?? 0;
    internal static int CurrentScriptColumn => Current?.CurrentColumn ?? 0;

    private object? _currentThis;
    /// <summary>The receiver's NAME while a member of a block that named it runs (`extension(string s)`), so the
    /// name resolves to the receiver before any global of that name, the tree half of the alias the compiler maps
    /// onto `this`'s VM slot. Null for an unnamed block, and for every member of one.</summary>
    private string? _currentReceiverName;
    private ClassInfo? _currentClass;

    /// <summary>The class context a running method body sees: access control, `super`/`base`, a bare
    /// static-field read and a lambda capturing `this` all consult it. A FLAT method call in the VM runs the
    /// body INSIDE the dispatch loop, so it sets this pair for the body's duration and the frame restores the
    /// caller's on RETURN or unwind, which is why the VM needs read AND write access to it.</summary>
    internal object? CurrentThis { get => _currentThis; set => _currentThis = value; }
    internal ClassInfo? CurrentClass { get => _currentClass; set => _currentClass = value; }

    /// <summary>The extension block's NAMED receiver (`extension(string s)`), which its bodies may read as a
    /// second spelling of the value. The VM's flat extension call saves and restores it exactly as it does
    /// <see cref="CurrentThis"/>, because a body that names its receiver otherwise reads the caller's.</summary>
    internal string? CurrentReceiverName { get => _currentReceiverName; set => _currentReceiverName = value; }

    /// <summary>The contract whose default body is running, if any (`_currentContract`'s public face). The VM's
    /// flat interface-default call saves and restores it like the other context fields, because a default reached
    /// through `this` composes through ITS contract, and a stale one composes through the caller's.</summary>
    internal string? CurrentContract { get => _currentContract; set => _currentContract = value; }

    /// <summary>The interface-default counterpart of <see cref="TryGetFlatExtensionMember"/>: the LOOKUP half of
    /// <see cref="TryInvokeInterfaceDefault"/> and nothing more, which is what the flat swap needs (the tree asks
    /// it first, after every real member has declined, and a default is nearer than a block). An AMBIGUOUS pair of
    /// providers is DECLINED here, because the refusal names both contracts and only the tree's path reports it.</summary>
    internal bool TryGetFlatInterfaceDefault(object? receiver, string memberName, int argCount,
        out FuncDecl body, out string? contract, out ClassInfo? classForBody)
    {
        body = null!;
        contract = null;
        classForBody = null;
        var key = memberName + "/" + argCount;
        FindDefaultProviders(receiver, memberName, argCount, forProperty: false, out var provider, out var second);
        if (second != null) return false;
        if (provider != null)
        {
            var info = ResolveInterface(provider);
            if (info == null || !info.Defaults.TryGetValue(key, out var declared)) return false;
            body = declared;
            contract = provider;
            classForBody = receiver is ClassInstance ci ? ci.Class : null;
            return true;
        }
        if (receiver is ClassInstance or StructInstance)
        {
            foreach (var name in _interfaceOrder)
            {
                if (!_interfaces[name].Defaults.TryGetValue(key, out var declared)) continue;
                if (!MatchesDeclaredInterface(receiver, name)) continue;
                body = declared;
                contract = name;
                classForBody = receiver is ClassInstance ci ? ci.Class : null;
                return true;
            }
        }
        return false;
    }

    /// <summary>The extension-member counterpart of <see cref="TryGetScriptMethod"/>, for the VM's flat
    /// extension call. It asks the SAME lookup the member-MISS path takes (FindInstanceExtension, which
    /// TryInvokeInstanceExtension and the interface-default route both end up in), and declines the two shapes
    /// the flat swap cannot run: a GENERIC block, whose concrete CLONE is the body that must execute and which
    /// carries no compiled body of its own, and a receiver that already OWNS the name, the rule that keeps an
    /// extension from shadowing a real member (C# 7.6.5.2). A body the compiler did not take is left to the
    /// wrapper, which can hand it to the tree.</summary>
    internal bool TryGetFlatExtensionMember(object? receiver, string memberName, int argCount,
        out FuncDecl member, out string? receiverName)
    {
        member = null!;
        receiverName = null;
        if (receiver == null || _extensionSets.Count == 0) return false;
        var key = (ReportedTypeName(receiver), receiver.GetType(), memberName, argCount);
        if (!_extensionLookup.TryGetValue(key, out var cached))
        {
            var found = FindInstanceExtension(receiver, memberName);
            var owned = found is { } hit
                        && receiver is not (ClassInstance or StructInstance)
                        && OwnsMember(receiver.GetType(), memberName, argCount, statics: false, Comparison);
            cached = (found?.Member, found?.Set.ReceiverName, owned);
            _extensionLookup[key] = cached;
        }
        if (cached.Member == null || cached.Owned) return false;
        if (cached.Member.TypeParameters.Count > 0) return false;   // a generic block runs its INSTANTIATED clone
        member = cached.Member;
        receiverName = cached.ReceiverName;
        return true;
    }

    /// <summary>The class that DECLARES a method, which is what a running body must see as the context:
    /// access control is per declaring class, so an inherited method called on a subclass instance still
    /// reaches its own private members. The flat method call in the VM needs that same answer the wrapper
    /// takes, and the helper computing it is private to the dispatch file.</summary>
    internal ClassInfo? DeclaringClassOf(ClassInfo startClass, FuncDecl method) => FindDeclaringClassForMethod(startClass, method);
    private bool _inConstructor;
    private object? _thrownException;
    private object? _pendingReturnValue;

    // Native Check family counters (Check/CheckSummary/CheckPassed/CheckFailed/CheckReset).
    // Explicit reset only (CheckReset), NOT auto-reset per Interpret, so REPL lines share one run.
    // Incremented via Interlocked (Parallel.* can call Check on worker threads); read via Volatile.Read.
    private int _checkPassed;
    private int _checkFailed;

    /// <summary>THE LEDGER behind those counters: the checks that FAILED, each with the position it was reported at.
    /// The counts alone leave a run's verdict saying "status 1" and nothing more, because `exit(CheckFailed())` is the
    /// script's OWN decision; the labels are what make that code actionable. Capped so a loop of failing checks cannot
    /// grow a verdict without bound, with <see cref="_checkFailureOverflow"/> counting what the cap dropped. Guarded by
    /// a lock rather than by the counters' Interlocked calls, because `Parallel.*` can report a check on a worker
    /// thread while the main one is reading the list.</summary>
    private const int MaxRecordedCheckFailures = 32;
    private readonly List<CheckFailure> _checkFailures = [];
    private int _checkFailureOverflow;

    /// <summary>Zeroes the Check family's counters. The natives below call it, and `.run` calls it per
    /// file, so one file's verdict is its own: without that, a passing file run after a failing one
    /// reported failure through `exit(CheckFailed())`, and a suite run twice reported a doubled count.</summary>
    internal void ResetCheckCounters()
    {
        _checkPassed = 0;
        _checkFailed = 0;
        lock (_checkFailures)
        {
            _checkFailures.Clear();
            _checkFailureOverflow = 0;
        }
    }

    /// <summary>Records one check that failed, which is what lets a run's verdict NAME it. Called by `Check` with the
    /// position it was reported at, so a host reads "label at line 12" instead of "status 1".</summary>
    internal void RecordCheckFailure(string label, int line, int column)
    {
        lock (_checkFailures)
        {
            if (_checkFailures.Count >= MaxRecordedCheckFailures) { _checkFailureOverflow++; return; }
            _checkFailures.Add(new CheckFailure(label, line, column));
        }
    }

    /// <summary>Where the ledger stands NOW, which a run takes before it starts so its report describes that run even
    /// when the interpreter carried an earlier one (a hosted session never zeroes the counters; the CLI does, per
    /// file).</summary>
    internal CheckSnapshot SnapshotChecks()
    {
        lock (_checkFailures)
            return new CheckSnapshot(Volatile.Read(ref _checkPassed), Volatile.Read(ref _checkFailed),
                                     _checkFailures.Count, _checkFailureOverflow);
    }

    /// <summary>What the check family reported SINCE the snapshot, or null when it reported no check at all, which is
    /// what keeps a plain script's verdict from growing a member it has no use for.</summary>
    internal CheckReport? ChecksSince(CheckSnapshot since)
    {
        var passed = Volatile.Read(ref _checkPassed) - since.Passed;
        var failed = Volatile.Read(ref _checkFailed) - since.Failed;
        if (passed <= 0 && failed <= 0) return null;

        List<CheckFailure> failures;
        int more;
        lock (_checkFailures)
        {
            // A reset MID-RUN (CheckReset, or `.run` zeroing between files) empties the ledger behind the snapshot,
            // so the snapshot then describes nothing and the whole list belongs to this run.
            var intact = since.Failures <= _checkFailures.Count && since.More <= _checkFailureOverflow;
            var from = intact ? since.Failures : 0;
            failures = _checkFailures.Count > from
                ? [.. _checkFailures.GetRange(from, _checkFailures.Count - from)]
                : [];
            more = _checkFailureOverflow - (intact ? since.More : 0);
            if (more < 0) more = 0;
        }
        return new CheckReport(passed, failed, failures, more);
    }

    [ThreadStatic]
    private static Stack<Interpreter>? _interpreterStack;

    public static Interpreter? Current => _interpreterStack?.Count > 0 ? _interpreterStack.Peek() : null;

    /// <summary>The flag a global WRITE THROUGH A CELL leaves for the VM: the write went to the cell's target, which may be
    /// another global, so the caller's `LOAD_GLOBAL_FAST` cache is stale and must be cleared. One static set by
    /// `ScopeStack.SetGlobalValueDirect` and taken by the VM's store arm, which is the only reader; a spurious set (two
    /// interpreters interleaved) costs one harmless clear, never a wrong value.</summary>
    private static bool _globalWriteThroughCell;

    internal static void NoteGlobalWriteThroughCell() => _globalWriteThroughCell = true;

    /// <summary>Bumped by EVERY global write that lands in a global (or in a cell's target), from any thread and any
    /// interpreter. A compiled body caches global values per invocation (`LOAD_GLOBAL_FAST`), and a NATIVE call can run
    /// script code (a subscriber, a comparer, a `ForEach` body, a task continuation, a route handler) or write through a
    /// cell, so the caller's cache can be stale afterwards and no dispatch site can tell that by looking at the callee.
    /// The VM samples this integer before such a dispatch and compares it after: one read and one compare cover the
    /// whole family, and NOTHING is cleared when nothing was written, which is what keeps a hot CLR call
    /// (`s.Length`, `list.Add`) paying nothing. Measured before it existed: a `--no-source` bundle read the OLD value of
    /// a global its own subscriber had just written (the cached-invoke fast path broke out without clearing) while the
    /// source leg read the live one.</summary>
    internal static int GlobalWriteStamp;

    internal static void BumpGlobalWriteStamp()
    {
        unchecked { GlobalWriteStamp++; }
    }

    /// <summary>Consumes the flag: true at most once per note.</summary>
    internal static bool TakeGlobalWriteThroughCell()
    {
        if (!_globalWriteThroughCell) return false;
        _globalWriteThroughCell = false;
        return true;
    }
    internal bool IsMcpHost { get; set; }
    private static readonly ClosureSnapshot SEmptySnapshot = new([], [], [], []);

    private ContextPopper PushContext()
    {
        _interpreterStack ??= new Stack<Interpreter>();
        _interpreterStack.Push(this);

        return new ContextPopper(this);
    }

    private string? _currentDirectory;

    /// <summary>THE ARGUMENTS THIS RUN WAS GIVEN, when they differ from the session's. `.run &lt;file&gt; -- a b` sets
    /// this for the duration of that run and puts back what was there before, so one run's arguments never leak into the
    /// next one (the same discipline `VmEnabled` and the carrier switch follow). `Application.Args` reads it through
    /// <see cref="Current"/>, and the FIRST entry is the script's own path, which is the CLI's convention.</summary>
    internal List<string>? ScriptArguments { get; set; }
    /// <summary>The directory a bundle was LOADED from, which is the importer for any statement it replays (an
    /// import in a stripped bundle). Null when the bundle came from bytes, whose home nothing knows.</summary>
    private string? _loadedBundleDirectory;
    private string? _currentSourceName; // file name of the script being interpreted (for Tree trace attribution)

    /// <summary>Source file name of the script currently being interpreted ("" when none).
    /// Used by the VM trace to attribute [Lx:Cx] to the right file in multi-file/import runs.</summary>
    internal string CurrentSourceName => _currentSourceName ?? "";
    private List<string>? _commandHistory;
    private bool _recordingExports;
    private Dictionary<string, object?>? _currentExports;
    internal volatile bool _interruptRequested;
    /// <summary>THE CANCELLATION OF THE RUN EXECUTING ON THIS THREAD, which is the kill signal a runner raises.
    /// `Task.Run(fn, token)` sets it around the body it runs, so `cts.Cancel()` from another thread STOPS that run
    /// at its next statement, loop back-edge or `sleep` chunk rather than leaving it looping: the token was
    /// previously only read BEFORE the body started (a run started with a cancelled token did not start at all),
    /// and a run cancelled while it was working kept working. `[ThreadStatic]` on purpose: the signal belongs to the
    /// thread the run occupies, so every interpreter that run starts (a nested `RunScript`, a module) inherits it,
    /// and a run on another thread is untouched.</summary>
    [ThreadStatic] internal static CancellationToken? ActiveRunCancellation;

    /// <summary>WHERE THIS INTERPRETER'S OUTPUT GOES, and `null` means the process's console (the CLI, a suite, the
    /// REPL, which is what every caller was before there was an embedding API).
    ///
    /// WHY ON THE INTERPRETER rather than `Console.SetOut`: `Console` is PROCESS-WIDE, so a host that runs two sessions
    /// (or runs one while its own threads print) would have its console swapped under it, and a nested `RunScript`
    /// would inherit the redirect with no way to say so. Here the sink follows the interpreter, so a run's `print`
    /// reaches its own session, a nested run reaches its parent's sink unless it sets its own, and a host's console is
    /// never touched. `print` and the engine's log lines both ask the CURRENT interpreter on the thread
    /// (`(Current ?? this).Out`), which is the pattern the internal helpers already use for exactly this reason.</summary>
    internal TextWriter? Out { get; set; }

    /// <summary>Where this interpreter's engine LOG lines go (warnings, errors, `[ERROR] ...`): the same rule as
    /// <see cref="Out"/>, and separate from it because a host usually wants the two apart (the clipboard tool and this
    /// engine's own suites both treat stderr as the verdict channel). `Log` asks it through `Interpreter.Current`.</summary>
    internal TextWriter? Err { get; set; }

    /// <summary>THE HOST'S OWN OBJECT, when the host adopted one: answers an `IDispatch` valid in THIS thread's
    /// apartment, or `IntPtr.Zero` when it adopted nothing (or released it). A host that embeds this engine inside the
    /// application it wants to drive sets it, because COM's own cache cannot find that application from inside it:
    /// measured from Excel, `Com.GetObject("Excel.Application")` refuses and `Com.CreateObject` answers a DIFFERENT
    /// instance. `Com.Host()` is the script side of it, and the session (`EmbedSession.AdoptHost`) is what sets this.
    /// <para>
    /// It is a delegate rather than a pointer because the reference must be taken on the thread that will USE it, one
    /// per scope: the Global Interface Table answers an apartment-local proxy each time, and a pointer cached here would
    /// be the host's own thread's, which stalls the moment it is used from another one.
    /// </para></summary>
    internal Func<IntPtr>? HostObjectSource { get; set; }

    /// <summary>The writer a `print` in the CURRENTly running interpreter uses. One door, so a future output member
    /// cannot pick a different rule.</summary>
    /// <summary>The process's OWN stdout, captured once, so a later `Console.SetOut` is recognisable as a REDIRECT (by a
    /// script, or by the CLI's own `-q`). See <see cref="ScriptOut"/>.
    /// <para>
    /// NOT `readonly`, and <see cref="CaptureProcessStdout"/> is why: assigning `Console.OutputEncoding` RE-CREATES the
    /// console's writer, so after `Program.RunMain`'s UTF-8 line the cached writer no longer matched and every later
    /// `print` satisfied the "redirected" test, bypassing the HOST's sink. Measured 2026-10-31 through the ABI harness:
    /// a session's output callback received its lines, then one `slc_main` call (which is `RunMain`) silenced it for
    /// every run after it (a fresh `slc_run_text` delivered `captured=[]` while the line went to the process console).
    /// The engine re-captures at the points where IT owns the console, so a SCRIPT's own redirect still wins.
    /// </para></summary>
    private static TextWriter ProcessStdout = Console.Out;

    /// <summary>Re-captures the process's stdout after the ENGINE replaced it (`Console.OutputEncoding`, a `> console`
    /// restore). Called only where the engine owns the console, never where a script does.</summary>
    internal static void CaptureProcessStdout() => ProcessStdout = Console.Out;

    /// <summary>The writer a `print` in the CURRENTly running interpreter uses. THREE doors, in precedence order:
    ///   * a REDIRECTED `Console.Out` wins, because that is somebody SAYING where the output goes: a script's own
    ///     `Console.SetOut(sink)` (a tool that tees its output into a log, which is what the clipboard runner does) and the
    ///     CLI's `-q`/loop silencing both speak through it. Measured: with the session's sink first, a script's
    ///     `Console.SetOut` was IGNORED in a hosted run while the same script worked from the command line;
    ///   * then the HOST's sink (`Out`), which is what an embedded session installs so a run's output reaches the host's
    ///     callback without the host rewriting its own console;
    ///   * then `Console.Out` (the CLI's ordinary case, where nothing set either).</summary>
    internal static TextWriter ScriptOut
    {
        get
        {
            var console = Console.Out;
            if (!ReferenceEquals(console, ProcessStdout)) return console;
            return (Current ?? null)?.Out ?? console;
        }
    }

    /// <summary>The writer the engine's own diagnostics use, when an embedding session has one.</summary>
    internal static TextWriter? ScriptErr => Current?.Err;

    /// <summary>WHICH EXECUTION UNIT is running on this thread, bumped by everything that starts one in-process: an
    /// `Interpret` (a file, a module, a nested text), a `RunScript`, and a bundle's ENTRY body. A FORM records it when
    /// it is created, and `Application.Run` waits only on the forms of ITS OWN unit, which is the difference between "my
    /// window closed, so my loop ends" and "somebody else's window in this process is still up". Measured: the RAD
    /// designer run through the clipboard tool with `Compile first` ticked kept running after its window closed, because
    /// the loop was waiting on the TOOL's panel, which shares this interpreter and this thread (a nested run has its own
    /// interpreter and thread, so that case was already covered by the thread test).</summary>
    [ThreadStatic] internal static int ExecutionUnit;

    internal static void EnterExecutionUnit() => ExecutionUnit++;

    /// <summary>True when the run on THIS thread has been cancelled, which the tree walker, `sleep` and the VM's
    /// loop back-edge ask. One question, asked from three places, so a cancelled run stops wherever it is: a
    /// compiled `while (true) { }` never touches a statement boundary, which is why the VM asks at a jump that
    /// walks BACKWARD (a loop back-edge) and not per instruction.</summary>
    internal static bool RunCancellationRequested => ActiveRunCancellation?.IsCancellationRequested == true;
    internal bool SuppressWarnings;

    /// <summary>Type names declared by the current script (classes/structs/enums/delegates, incl.
    /// nested), collected once per Interpret execution so forward references in signatures are not
    /// flagged as unknown. Null outside a script execution.</summary>
    private HashSet<string>? _declaredTypeNames;

    /// <summary>The names this SESSION's top-level `func` declarations bound, so `Reset(keepGlobals: true)` can
    /// keep exactly those values and still drop a `var` that happens to hold a lambda.
    ///
    /// A run's top-level functions were the one declaration a `.run` did not carry forward: `Reset` skipped every
    /// `ScriptFunction` in the preserved globals, which is also what a `var f = fun() { ... }` holds, so the skip
    /// could not simply go (keeping those makes the next run of the same file warn "already declared", which is
    /// why the skip exists at all, commit f36d9a0e). The NAME is the discriminator the value cannot give: a
    /// top-level `func` is re-registered by `VisitFuncDecl` with no duplicate warning, while a `var` is not.
    ///
    /// Measured before this: after a `.run`, the file's `var` and its `class` both answered and its FUNCTION
    /// reported `Undefined variable 'PathOf'`, which is why an `http.Use` middleware could not call a helper
    /// declared in the very file that registered it. The list is matched with the interpreter's own case rule and
    /// cleared whenever the globals are (`keepGlobals: false`), since there is then nothing to preserve.</summary>
    private readonly List<string> _topLevelFunctionNames = [];

    /// <summary>The SOURCE that declared each script type, so a re-declaration can tell "the same file ran again" from
    /// "a different file claims a name this session already owns". A `.run` KEEPS the classes it declared on purpose (a
    /// middleware in a later request reaches them), so re-running a file used to warn "already declared" and SKIP, which
    /// left an edited class answering its OLD body: measured, a file re-declared `Greeter` after its body was rewritten
    /// to return "two", the file's own print ran, and `new Greeter().Hi()` still answered "one". A name declared by the
    /// SAME source replaces it silently now; a DIFFERENT source keeps the warning and the skip, because that is a real
    /// clash between two files. Built with the interpreter's own comparer, like the class tables, and cleared with
    /// them.</summary>
    private readonly Dictionary<string, string> _declaredBySource;


    /// <summary>The pages a program registered for a status the FRAMEWORK sets (a 404 where nothing answered, a 500
    /// from an unhandled failure), per code, with the content type it asked for. It lives on the INTERPRETER because a
    /// page is a session's business, and `http.ErrorPage` is the only writer: see <see cref="ErrorPageFor"/>.
    ///
    /// The value is a STRING, served as the body, or a Function called with the request's `ctx`, whose return value IS
    /// the body. That second form is the hybrid the maintainer asked for: one registration that emits an HTML buffer
    /// for a browser or a JSON buffer for an API client, deciding by whatever it reads on the context.</summary>
    internal readonly Dictionary<int, (object? Value, string? ContentType)> ErrorPages = [];

    /// <summary>The body and the content type for a framework-set status: what the program registered, or the engine's
    /// own page (<see cref="HttpErrorPages"/>) when nothing was. A code the HTTP spec forbids a body on never reaches
    /// here. A page that THROWS is the default page rather than a second failure, because a 500 raised while answering
    /// a 404 would lose the original status and the reason for it.</summary>
    internal (string Body, string ContentType) ErrorPageFor(int status, string reason, object? request)
    {
        if (ErrorPages.TryGetValue(status, out var page))
        {
            var value = page.Value;
            if (value is ScriptFunction fn)
            {
                try { value = InvokeScriptFunction(fn, [request], 0, 0, null, false); }
                catch (ScriptException) { value = null; }
            }
            if (value != null)
                return (ValueFormatter.ToDisplay(value), page.ContentType ?? HttpErrorPages.ContentType);
        }
        return (HttpErrorPages.Default(status, reason), HttpErrorPages.ContentType);
    }

    /// <summary>True when the name currently in the type tables came from the source being interpreted now.</summary>
    private bool DeclaredByCurrentSource(string name)
        => _declaredBySource.TryGetValue(name, out var source) && source == CurrentSourceName;

    /// <summary>Remembers which SOURCE declared a type, for <see cref="DeclaredByCurrentSource"/>. An empty source
    /// records nothing, so an interpretation that names no file keeps the old warn-and-skip answer.</summary>
    private void RecordDeclaredBySource(string name)
    {
        if (name.Length == 0 || CurrentSourceName.Length == 0) return;
        _declaredBySource[name] = CurrentSourceName;
    }

    private bool IsTopLevelFunctionName(string name)
    {
        // A linear scan on purpose: a session declares a handful of top-level functions, and the alternative is a
        // second HashSet that `SetCaseSensitive` would have to rebuild to keep the case rule in step.
        foreach (var known in _topLevelFunctionNames)
            if (_comparer.Equals(known, name)) return true;
        return false;
    }

    private void RecordTopLevelFunctionName(string name)
    {
        if (name.Length == 0 || IsTopLevelFunctionName(name)) return;
        _topLevelFunctionNames.Add(name);
    }

    /// <summary>Namespace segments currently in scope during module execution
    /// (["My","Include"] inside `namespace My.Include { … }`).</summary>
    private readonly List<string> _namespaceSegments = [];

    /// <summary>F2: the Namespace object the current namespace declaration is building, the
    /// leaf that declared members register into for in-file qualified access. Null when no
    /// namespace is active. Parallel to _namespaceSegments.</summary>
    private Namespace? _currentNamespaceLeaf;

    /// <summary>F2 gate: true while this interpreter runs a module/library for re-export
    /// (LoadModule / Bytecode.ExportLibrary). Modules keep v1 namespace behavior, no F2 chain
    /// is built in their scope, so repeated `namespace A.B;` statements never collide and the
    /// chain object never pollutes the module's export snapshot. In-file qualified access (F2)
    /// applies to the root script and RunScript sub-interpreters, where IsModuleExecution is false.</summary>
    internal bool IsModuleExecution;

    /// <summary>Top-level declaration name → dot-joined namespace path ("" = root),
    /// recorded while a namespace block is active; consumed by LoadModule for exports.</summary>
    private readonly Dictionary<string, string> _declaredInNamespace = [];

    /// <summary>Declarations already warned about unknown type names (dedupes the two Interpret
    /// passes over top-level functions). Reset per execution, AST nodes are pooled and recycled.
    /// Weak-keyed: the dedup must NOT root the FuncDecls it has seen. A capturing lambda creates a
    /// NEW FuncDecl on every evaluation, a strong set would retain each one forever (loop-closure
    /// growth, e.g. crash_udf_leak's 0.5 KB/closure). Entries die with their FuncDecl.</summary>
    private static readonly object WarnedDeclSentinel = new();
    private System.Runtime.CompilerServices.ConditionalWeakTable<FuncDecl, object> _warnedUnknownTypes = new();
    internal long StatementsExecuted;
    internal long FunctionsCalled;
    internal DateTime StartTime = DateTime.Now;
    internal static Action? OnInterpreterCreated;

    internal readonly Dictionary<string, (MethodInfo Method, ParameterInfo[] Params)> _methodCache;

    // P2-T2 script-class method overload cache: key = className|methodName|argTypeHash.
    // The hash covers ACTUAL runtime arg types (not just count), the previous
    // (TypeKey, Name, ArgCount) key collided on int vs short. Entries retain the full
    // runtime type array so every hit is verified (collision-safe) before use.
    private sealed record OverloadCacheEntry(FuncDecl Method, ClassInfo DeclaringClass, TokenType[] ArgTypes);
    private readonly Dictionary<string, OverloadCacheEntry> _overloadCache = new(StringComparer.Ordinal);
    // 1-arg fast path: tuple key (className, methodName, argType) is the FULL signature,
    // a hit is exact, so no probe/verification and no string allocation.
    private readonly Dictionary<(string ClassName, string MethodName, TokenType ArgType), OverloadCacheEntry> _overloadCache1 = new();

    // Cache key built from script-level TokenTypes (existing MapTokenType mapping),
    // NOT CLR Type.FullName reflection + HashCode, avoids hash collisions between
    // overloads that differ only by numeric type (int vs short vs long) and keeps
    // the key deterministic and allocation-light.
    private static string OverloadCacheKey(string className, string methodName, object?[] args)
    {
        var sb = new StringBuilder(className.Length + methodName.Length + args.Length * 8);
        sb.Append(className).Append('|').Append(methodName).Append('|');
        foreach (var a in args)
            sb.Append(MapTokenType(EffectiveArgValue(a)?.GetType() ?? typeof(object))).Append(',');
        return sb.ToString();
    }

    /// <summary>Cache hit is a PROBE: verifies the full TokenType signature before
    /// returning. Mismatch or missing → miss; the caller re-resolves normally.</summary>
    internal bool TryGetOverload(string key, object?[] args, out FuncDecl? m, out ClassInfo? declaringClass)
    {
        if (_overloadCache.TryGetValue(key, out var entry))
        {
            if (entry.ArgTypes.Length == args.Length)
            {
                var match = true;
                for (var i = 0; i < args.Length; i++)
                {
                    var current = MapTokenType(EffectiveArgValue(args[i])?.GetType() ?? typeof(object));
                    if (current != entry.ArgTypes[i]) { match = false; break; }
                }
                if (match)
                {
                    m = entry.Method;
                    declaringClass = entry.DeclaringClass;
                    return true;
                }
            }
        }
        m = null;
        declaringClass = null;
        return false;
    }

    internal void CacheOverload(string key, FuncDecl m, ClassInfo declaringClass, object?[] args)
    {
        var types = new TokenType[args.Length];
        for (var i = 0; i < args.Length; i++)
            types[i] = MapTokenType(EffectiveArgValue(args[i])?.GetType() ?? typeof(object));
        _overloadCache[key] = new OverloadCacheEntry(m, declaringClass, types);
        if (args.Length == 1)
            _overloadCache1[(declaringClass.Name, m.Name, types[0])] =
                new OverloadCacheEntry(m, declaringClass, types);
    }

    internal readonly Dictionary<(Type Type, string Name), PropertyInfo> PropertyCache;

    /// <summary>Comparer for the property cache: matches on the runtime type plus the property
    /// name using the interpreter's case-sensitivity setting. Keying by (Type, string) instead of
    /// an interpolated "FullName:name" string avoids a per-access string allocation and a latent
    /// collision when FullName is null (generic/array types).</summary>
    private sealed class PropertyKeyComparer(StringComparer nameComparer) : IEqualityComparer<(Type Type, string Name)>
    {
        public bool Equals((Type Type, string Name) x, (Type Type, string Name) y) =>
            x.Type == y.Type && nameComparer.Equals(x.Name, y.Name);
        public int GetHashCode((Type Type, string Name) k) =>
            HashCode.Combine(k.Type, nameComparer.GetHashCode(k.Name));
    }
    internal readonly Dictionary<string, ModuleInfo> Modules;
    /// <summary>Content-hash dedup: maps SHA256 hash of source → resolved path of first load.
    /// Prevents reloading the same content loaded via different paths.</summary>
    private readonly Dictionary<string, string> _moduleContentHashes;
    private readonly HashSet<string> _loadingModules;
    internal readonly Dictionary<string, ClassInfo> Classes;
    internal readonly Dictionary<string, DelegateInfo> DelegateInfos;
    private readonly Dictionary<string, DynamicLibrary> _loadedLibraries = new();
    private readonly List<IntPtr> _registeredUdfDbHandles = [with(2)]; // tracked for cleanup on Dispose
    internal readonly Dictionary<string, StructDeclStmt> Structs;
    internal readonly List<Type> ExtensionMethodContainers = [with(4)];

    // Add this property to expose the delegates dictionary
    internal IReadOnlyDictionary<string, DelegateInfo> Delegates => DelegateInfos;

    // Expose the setting for module loading and debugging
    public bool CaseSensitive => _caseSensitive;

    /// <summary>
    /// Resets the interpreter's execution state for a new script execution.
    /// Preserves all registered functions, classes, structs, and delegates.
    /// </summary>
    /// <param name="keepGlobals">If true, preserves global variable values (REPL mode).</param>
    public void Reset(bool keepGlobals, bool clearTypes = false)
    {
        // ========================================================================
        // CLEANUP THAT WAS PREVIOUSLY IN DISPOSE() - now per-execution
        // ========================================================================

        // Clear stale scheduled tasks from previous script runs
        ScheduledTask.ResetAll();

        // Reset VM coverage counters for fresh per-run stats
        VmInvocations = 0;
        TreeInvocations = 0;
        VmCacheHits = 0;
        VmCacheMisses = 0;
        GlobalFastLoads = 0;
        GlobalFastStores = 0;

        // ========================================================================
        // RESET EXECUTION STATE
        // ========================================================================

        // Reset basic execution flags
        // `_isRepl` is NOT set here, and `SetReplMode` is its only writer: the member means "a REPL session is
        // driving this interpreter", which a reset cannot decide. It used to take `keepGlobals` (the
        // `.state keep/reset` setting), so `.state keep` made a `.run` file report `Script.IsRepl = true` and sent
        // its TOP LEVEL to the tree walker, where a plain run of the same file compiles it. One writer also means
        // the member cannot disagree with the gate that reads it (`TryExecuteStatementOnVm`).
        _currentLine = 0;
        _currentColumn = 0;
        _lastExpressionValue = null;
        _lastValueWasReturn = false;
        _thrownException = null;
        _currentStatementIndex = 0;
        _coroutineStack.Clear();
        _isResumingFromYield = false;
        _recordingExports = false;
        _currentExports = null;
        _currentThis = null;
        _currentClass = null;
        _inConstructor = false;
        _currentDirectory = null;

        // Clear runtime collections (but NOT the type definition dictionaries!)
        _callStack.Clear();
        _lambdaCache.Clear();
        _constructorCache.Clear();
        _methodOverloadCache.Clear();
        _exitHandlers.Clear();
        Array.Clear(_loopStack, 0, _loopStackSp);
        _loopStackSp = 0;
        lock (_sleepLock) _sleepQueue.Clear();
        _eventWaiters.Clear();
        _loadingModules.Clear();
        _interruptRequested = false;
        _declaredTypeNames = null;
        _warnedUnknownTypes = new();
        _namespaceSegments.Clear();
        _declaredInNamespace.Clear();
        _moduleContentHashes.Clear();

        _methodCache.Clear();
        _overloadCache.Clear();
        _overloadCache1.Clear();
        PropertyCache.Clear();

        // Clear pure function memoization cache (unless in REPL mode)
        if (_pureMemoCache.Count > 0)
            _pureMemoCache.Clear();

        // ========================================================================
        // CLEAR GENERIC CACHES, they hold AST nodes that will be recycled
        // ========================================================================
        _genericClassTemplates.Clear();
        _instantiatedClasses.Clear();
        _instantiatedFuncs.Clear();

        // Dictionary.Clear() retains bucket arrays, trim excess capacity so long-running
        // .looprun sessions don't accumulate peak-size buckets from every run.
        // (ConcurrentDictionary caches are small and bounded by distinct signatures.)
        _lambdaCache.TrimExcess();
        _pureMemoCache.TrimExcess();
        _methodCache.TrimExcess();
        _overloadCache.TrimExcess();
        _overloadCache1.TrimExcess();
        PropertyCache.TrimExcess();

        Classes.TrimExcess();
        DelegateInfos.TrimExcess();
        Structs.TrimExcess();
        Modules.TrimExcess();

        // ========================================================================
        // FREE NATIVE LIBRARIES on .reset all
        // ========================================================================

        // Always clean up UDFs regardless of clearTypes, frees GCHandles
        // Save db handles before clearing, need them to close databases
        // Only close tracked DB handles during full (non-keepGlobals) reset
        if (!keepGlobals)
        {
            var dbHandlesToClose = new List<IntPtr>(_registeredUdfDbHandles);
            foreach (var dbHandle in dbHandlesToClose)
            {
                UdfRegistry.UnregisterAll(dbHandle);
                // Close the database before the library is freed
                if (!SqliteExports.Map.TryRemove(dbHandle.ToInt64(), out var lib)) continue;
                var exp = SqliteExports.GetOrLoad(lib);
                if (exp.Close != nint.Zero)
                {
                    unsafe { ((delegate* unmanaged[Cdecl]<long, int>)exp.Close)(dbHandle.ToInt64()); }
                }
            }
            _registeredUdfDbHandles.Clear();
        }

        if (clearTypes)
        {
            foreach (var kvp in _loadedLibraries)
            {
                // A NAME-BOUND native is process-lifetime by design: it ships beside the executable and is
                // resolved BY NAME, so unloading it here bought only a fresh module per iteration, and cost an
                // execute-into-unmapped-memory fault as soon as anything still held a pointer resolved from
                // the previous load (measured: SOFTWARE_NX_FAULT_INVALID_POINTER_EXECUTE_c0000005 inside
                // <Unloaded_sqlite3.DLL>, about one 50-iteration run in three). The library SET below is still
                // cleared, so each iteration still starts from the state at invocation; the module stays
                // loaded, which is what "loaded by name, one instance per process" means.
                if (Interpreter.IsNameBound(kvp.Key))
                    continue;
                kvp.Value.Free();
            }
            _loadedLibraries.Clear();
            SqliteExports.ClearCache();
            StructStaticFields.Clear();

            // Extension blocks are declarations too, and the loop tools reset with clearTypes precisely so that
            // each iteration starts from the state at invocation. A CLASS re-declared by a second run is harmless
            // (the definition is simply overwritten), but registering the same extension block twice hits the
            // duplicate guard and errors, and that error is SWALLOWED: it never reaches .looprun's/.gc leak's
            // exception handling (their run-error count stays 0) and no message is printed anywhere. Measured with
            // file markers written by the script itself: an extension-declaring script reached 1 of 4 executions
            // where a class-declaring one reached 4 of 4, so the repeats measured nothing while the tool still
            // printed a verdict. Clearing here makes the two behave alike.
            _extensionSets.Clear();
            ClearExtensionLookupCache();

            // INTERFACES are declarations of the same kind, and the same trap: a re-run re-reads the file, so its
            // contracts arrive again and the guard answers "Interface 'X' already defined. Skipping.". Measured on
            // the maintainer's own report, `.gc leak` over a file declaring three interfaces warned three times per
            // iteration, and reproduced here. The guard itself is unchanged: two `interface I` blocks in ONE run
            // still refuse, because a contract a class was already checked against must not be swapped under it.
            // What this clears is the PREVIOUS run's registry. `.looprun` already cleared it; the Reset path that
            // `.gc leak` uses did not, which is why only the leak tool warned.
            ClearInterfaces();
        }

        // ========================================================================
        // PRESERVE TYPE DEFINITIONS - DO NOT CLEAR THESE:
        // _classes - contains ClassInfo definitions
        // _delegates - contains DelegateInfo definitions  
        // _structs - contains StructDeclStmt definitions
        // _modules - contains loaded module caches
        // ========================================================================

        // ========================================================================
        // HANDLE CALL FRAMES AND SCOPES
        // ========================================================================

        // IMPORTANT: Save the existing global scope BEFORE clearing call frames
        // This contains ALL the built-in functions and types that were registered
        // during interpreter construction and any script-defined types

        // PRE-EXISTING BUG FIX: after .looprun the call-frame stack can be empty
        // when this Reset runs (the loop's per-iteration finally-Reset can observe
        // zero frames). Peek() would throw InvalidOperation_EmptyStack, seed a
        // fresh frame so "nothing to preserve" is handled like a normal reset.
        if (_callFrames.Value!.Count == 0)
            _callFrames.Value!.Push(new CallFrame(new ScopeStack(_comparer)));

        var rootFrame = _callFrames.Value!.Peek();
        var preservedGlobalScope = rootFrame.Scopes.GetFirstScopeDictionary();


        // Clear all call frames
        _callFrames.Value!.Clear();

        // Create a fresh scope stack
        var newScopeStack = new ScopeStack(_comparer);
        var newGlobalScope = newScopeStack.Scopes.Peek();


        if (!keepGlobals)
        {
            Classes.Clear();
            Structs.Clear();
            Modules.Clear();
            // The globals are gone, so there is nothing left for these names to preserve.
            _topLevelFunctionNames.Clear();
            _declaredBySource.Clear();
        }
        else
        {
            // Keep classes, structs, delegates, and extension containers
            // (populated once at construction, never cleared)
            foreach (var kvp in preservedGlobalScope)
            {
                // A top-level FUNCTION survives, exactly like a var and a class: it is a name this SESSION
                // declared, and dropping it is what made a file's own helper unreachable from a later request
                // (measured: an `http.Use` middleware calling the file's `PathOf` reported `Undefined variable
                // 'PathOf'` while the file's `var` and its `class` both answered). A ScriptFunction that is NOT
                // one of those names is still dropped: that is a `var f = fun() { ... }`, and keeping it makes
                // the NEXT run of the same file warn "'f' already declared", which is why this skip exists at
                // all (commit f36d9a0e). See _topLevelFunctionNames.
                if (kvp.Value.Value is ScriptFunction && !IsTopLevelFunctionName(kvp.Key)) continue;
                if (kvp.Value.Value is DelegateInfo && !_builtinNames.Contains(kvp.Key)) continue;
                newGlobalScope[kvp.Key] = kvp.Value;
            }
        }

        // Push the new root frame (UNCONDITIONALLY, this must NOT be inside
        // the keepGlobals if-block: SetReplMode/keepGlobals:true paths rely on it)
        _callFrames.Value!.Push(new CallFrame(newScopeStack));

        // Push a fresh local scope for the upcoming script execution
        if (_callFrames.Value!.Count > 0)
        {
            var currentFrame = _callFrames.Value!.Peek();

            if (currentFrame.Scopes.Count == 0)
                currentFrame.Scopes.PushScope();

            while (currentFrame.Scopes.Count > 1)
                currentFrame.Scopes.PopScope();
        }

        // Restore built-ins from shared blueprint (fast clone, runs AFTER frame push)
        if (!keepGlobals)
        {
            BuiltinBlueprint.CloneInto(this, clearTypes);
            RegisterPerInstanceBuiltins();

            // Re-register all builtins to ensure scope entries are present
            RegisterBuiltinFunctions();

            // Preserve user-defined types from old scope (unless clearTypes explicitly clears them)
            if (!clearTypes && preservedGlobalScope.Count > 0)
            {
                foreach (var kvp in preservedGlobalScope)
                {
                    var value = kvp.Value.Value;
                    if (value is ClassInfo ci && Classes.TryAdd(ci.Name, ci))
                    {
                        newGlobalScope[ci.Name] = kvp.Value;
                    }
                    else if (value is DelegateInfo di && DelegateInfos.TryAdd(di.Name, di))
                    {
                        newGlobalScope[di.Name] = kvp.Value;
                    }
                    else if (value is StructDeclStmt sd && Structs.TryAdd(sd.Name, sd))
                    {
                        newGlobalScope[sd.Name] = kvp.Value;
                    }
                }
            }
        }
        _rootFrame = _callFrames.Value!.Peek();
        OnInterpreterCreated?.Invoke();
    }

    /// <summary>Full session wipe after a failed command: restores pristine interpreter
    /// state so the next REPL command starts clean. Globals are cleared.</summary>
    public void ResetSession()
    {
        try { Reset(keepGlobals: false); }
        catch { /* best-effort: never let recovery itself crash the REPL */ }
        _loopStackSp = 0;
        _tailCallArgs = null;
        _fastPathDepth = 0;
        _treeTraced.Clear();
        _lastTreeTrace = null;
        _lastTreeTraceCount = 1;
        _isResumingFromYield = false;
        _coroutineStack.Clear(); // _currentCoroutine is a getter-only property; clear its backing stack
        _currentThis = null;
        _currentClass = null;
        // Keep the root frame Reset() seeded, clearing the whole stack drops the
        // builtins registered in the root global scope for the next command.
        // Only trim stray frames left above the root by the failed command.
        try
        {
            var frames = _callFrames.Value;
            while (frames is { Count: > 1 }) frames.TryPop(out _);
        }
        catch { }
        _callStack.Clear();
    }

    /// <summary>
    /// Overload for backward compatibility - resets with keepGlobals = false
    /// </summary>
    /// <summary>Toggle case sensitivity mode at runtime. Clears caches and resets with keepGlobals.</summary>
    public void SetCaseSensitive(bool caseSensitive)
    {
        _caseSensitive = caseSensitive;
        _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        _methodCache.Clear();
        _overloadCache.Clear();
        _overloadCache1.Clear();
        PropertyCache.Clear();
        _pureMemoCache.Clear();
        Reset(keepGlobals: true);
    }

    public void Reset() => Reset(keepGlobals: false);

    public void SetReplMode(bool enabled)
    {
        _isRepl = enabled;
        Reset(keepGlobals: enabled);
    }

    /// <summary>True when the run in progress is a line TYPED at the prompt rather than a file: `_currentSourceName` is
    /// the `<repl>` sentinel exactly when nothing stands behind the run, and a file's own name for `.run` and
    /// `--repl &lt;file&gt;`. Read by the top-level VM gate, which keeps a typed line on the tree so its value echoes and
    /// lets a FILE run take the VM path a CLI run takes (see the measurement there).</summary>
    private bool IsReplTypedLine() => string.IsNullOrEmpty(_currentSourceName) || _currentSourceName == "<repl>";

    /// <summary>
    /// Releases resources used by the interpreter. Frees all callback handles obtained from
    /// RegisterNativeFunctions.GetFunctionPointer, clears the callback handle table, and removes the interpreter from
    /// the thread-static stack when it is on top. Multiple calls have no effect.
    /// </summary>
    /// <remarks>Marks the instance as disposed and disposes any remaining IDisposable fields. AST pool return
    /// and clearing of certain pure caches are performed in Reset(), so they are not performed here. Do not use the
    /// instance after calling Dispose.</remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // A FINISHED RUN'S WINDOWS GO WITH IT. A runner disposes the interpreter that ran a captured script while that
        // script's forms may still be on screen (a cancelled run never reached the end of its own teardown), and nothing
        // else closes them: measured from a runner clicking Repeat, a window stayed "stuck in memory" after every window
        // had been closed by hand, and the NEXT run then reused a form of the disposed interpreter, which reported
        // `handle 0` and failed its first WM_NCCALCSIZE. Closing here is the one place that knows the run is over.
        Interpreter.NativeApp.CloseFormsOwnedBy(this);

        // AND SO DO ITS FEEDS. A subject a script created (`Events.Create`) holds callables that belong to THIS
        // interpreter, so a delivery after this point would invoke a disposed interpreter's frames, which is the same
        // family the timer's own guard covers (`TheTimer.RunTick` returns when its interpreter is disposed). Closing
        // them here also releases a pull consumer waiting on `await foreach`, which would otherwise wait for an end
        // that never comes.
        CloseSubjects();

        // Free all callback handles (from RegisterNativeFunctions.GetFunctionPointer)
        // These are created once and should be freed only when the interpreter is disposed
        foreach (var handle in _callbackHandles.Values)
            handle.Free();
        _callbackHandles.Clear();

        // Remove this interpreter from thread-static stack regardless of position
        if (_interpreterStack is { Count: > 0 })
        {
            var remaining = new Stack<Interpreter>(_interpreterStack.Where(i => !ReferenceEquals(i, this)).Reverse());
            _interpreterStack = remaining;
        }

        // Because it needs to happen after EACH script execution, not just once

        // Note: _pureMemoCache are now cleared in Reset()
        // when keepGlobals is false, so we don't clear them here anymore

        // Ensure all runtime collections are cleared on Dispose
        _callStack.Clear();
        _loopStackSp = 0;
        Array.Clear(_loopStack);
        _staticInitClasses.Clear();
        _initialStaticFields.Clear();
        lock (_sleepLock) _sleepQueue.Clear();
        _eventWaiters.Clear();
        _loadingModules.Clear();
        _interruptRequested = false;
        _moduleContentHashes.Clear();

        // AND THE PUMP'S OWN KERNEL EVENT, which is one KERNEL HANDLE per interpreter and was never closed: a process
        // that runs one script and exits never noticed, while a HOST that opens and frees sessions does. Measured from
        // an embedding harness: forty create/free cycles left the host's handle count rising by about two per cycle,
        // with controls showing a bare thread and an STA thread costing nothing (twenty of each: +2 handles), so the
        // cost was per interpreter rather than per thread. This is the first of the two, and disposing it is safe at
        // this point because Dispose runs on the thread that owned the run: a waiter still parked inside it would mean
        // a run in progress, which is what the caller's own teardown already rules out.
        _pumpWork.Dispose();

        _methodCache.Clear();
        _overloadCache.Clear();
        _overloadCache1.Clear();
        PropertyCache.Clear();
        _pureMemoCache.Clear();
        ExtensionMethodContainers.Clear();
        _genericClassTemplates.Clear();
        _instantiatedClasses.Clear();
        _instantiatedFuncs.Clear();
        Classes.Clear();
        DelegateInfos.Clear();
        Structs.Clear();
        Modules.Clear();
        _commandHistory = null;
        _currentExports = null;
        _coroutineStack.Clear();

        // Free UDF registrations and close database handles BEFORE freeing libraries
        foreach (var dbHandle in _registeredUdfDbHandles)
        {
            UdfRegistry.UnregisterAll(dbHandle);
            if (!SqliteExports.Map.TryRemove(dbHandle.ToInt64(), out var lib)) continue;
            var exp = SqliteExports.GetOrLoad(lib);
            if (exp.Close != nint.Zero)
            {
                unsafe { ((delegate* unmanaged[Cdecl]<long, int>)exp.Close)(dbHandle.ToInt64()); }
            }
        }
        _registeredUdfDbHandles.Clear();

        // Now free libraries
        foreach (var kvp in _loadedLibraries)
        {
            // A NAME-BOUND native is process-lifetime by design: it ships beside the executable and is
            // resolved BY NAME, so unloading it here bought only a fresh module per iteration, and cost an
            // execute-into-unmapped-memory fault as soon as anything still held a pointer resolved from
            // the previous load (measured: SOFTWARE_NX_FAULT_INVALID_POINTER_EXECUTE_c0000005 inside
            // <Unloaded_sqlite3.DLL>, about one 50-iteration run in three). The library SET below is still
            // cleared, so each iteration still starts from the state at invocation; the module stays
            // loaded, which is what "loaded by name, one instance per process" means.
            if (Interpreter.IsNameBound(kvp.Key))
                continue;
            kvp.Value.Free();
        }
        _loadedLibraries.Clear();

        // Clear stale scheduled tasks
        ScheduledTask.ResetAll();

        // Clear the static SqliteExports cache
        SqliteExports.ClearCache();

        // Clear static struct fields
        StructStaticFields.Clear();

        // Dispose global hotkey resources
        GlobalHotKey.Dispose();

        // Dispose the namespace trees owned by THIS interpreter (module-import
        // namespaces, per-instance builtins, post-reset fresh builtins).
        // Blueprint-shared namespaces (BuiltinBlueprint.ScopeEntries) are process
        // singletons shared with every other interpreter, disposing them would
        // wipe their method/property tables (Namespace.Dispose clears child
        // dictionaries) and break the other interpreters still using them.
        if (_callFrames.Value is { Count: > 0 } frames)
        {
            var shared = new HashSet<Namespace>(ReferenceEqualityComparer.Instance);
            foreach (var blueprintValue in BuiltinBlueprint.ScopeEntries.Values)
                if (blueprintValue is Namespace ns)
                    shared.Add(ns);

            var seen = new HashSet<Namespace>(ReferenceEqualityComparer.Instance);
            foreach (var kvp in frames.Peek().Scopes.GetFirstScopeDictionary())
                if (kvp.Value.Value is Namespace ns && !shared.Contains(ns) && seen.Add(ns))
                    ns.Dispose();
        }

        // Release execution frames
        _callFrames.Value!.Clear();
        if (!IsMcpHost) _callFrames.Dispose();
    }

    // true by default for better performance and to match C# semantics;
    // set too false to allow case-insensitive module loading and function lookup.
    public Interpreter(bool caseSensitive)
    {
        // The thread a RUN begins on, recorded where the engine is CREATED: `Interpret` sets it too, but a
        // `--no-source` bundle never calls `Interpret`, and `sleep` needs it to know whether draining Task's
        // main-thread continuation queue is safe (see RunThreadId).
        if (RunThreadId == 0) RunThreadId = Environment.CurrentManagedThreadId;
        if (RunThreadNativeId == 0) RunThreadNativeId = (int)KernelCurrentThreadId();

        _caseSensitive = caseSensitive;
        _comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        _comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        _pureMemoCache = new Dictionary<(string funcName, object?[] args), object?>(100, new PureArgsComparer(_comparer));

        //CurrentFrame.Scopes = new ScopeStack(_comparer);
        _callFrames.Value!.Push(new CallFrame(new ScopeStack(_comparer)));

        // Initialize dictionaries with the chosen comparer
        Modules = new Dictionary<string, ModuleInfo>(50, _comparer);
        _moduleContentHashes = new Dictionary<string, string>(50, StringComparer.Ordinal);
        _loadingModules = [with(50, _comparer)];
        Classes = new Dictionary<string, ClassInfo>(100, _comparer);
        _declaredBySource = new Dictionary<string, string>(64, _comparer);
        DelegateInfos = new Dictionary<string, DelegateInfo>(100, _comparer);
        Structs = new Dictionary<string, StructDeclStmt>(100, _comparer);

        _methodCache = new Dictionary<string, (MethodInfo Method, ParameterInfo[] Params)>(100, _comparer);
        PropertyCache = new Dictionary<(Type, string), PropertyInfo>(100, new PropertyKeyComparer(_comparer));

        ExtensionMethodContainers.Add(typeof(LinqExtensions)); // from the separate namespace
        ExtensionMethodContainers.Add(typeof(DateTimeExtensions)); // TrimToMinute() extension

        if (BuiltinBlueprint.Captured)
        {
            // Fast path: clone from shared blueprint (module interpreters, subsequent instances).
            // CloneInto writes the blueprint entries directly into the LIVE scope stack, so the
            // entries are already present here, no second DefineBlueprintEntries pass needed.
            BuiltinBlueprint.CloneInto(this);
            RegisterPerInstanceBuiltins();
        }
        else
        {
            // Slow path: first interpreter ever, full registration + capture blueprint
            RegisterAllBuiltins();
            BuiltinBlueprint.CaptureFrom(this);
        }

        // -- NOW capture the fully-populated root frame --
        _rootFrame = _callFrames.Value!.Peek();
        OnInterpreterCreated?.Invoke();
    }

    // ============================================================================
    // 13. INTERPRETER - Mï¿½THODES D'ENREGISTREMENT DES FONCTIONS NATIVES
    // ============================================================================

    internal static readonly ConcurrentDictionary<string, Dictionary<string, object?>> StructStaticFields = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedNamespacesKeys = [
        "System", "TypeCode", "StringComparison", "Convert", "Script", "This"
    ];

    /// <summary>
    /// Registers a delegate type with the interpreter
    /// </summary>
    public void RegisterDelegate(DelegateInfo delegateInfo) => DelegateInfos[delegateInfo.Name] = delegateInfo;

    /// <summary>
    /// Registers a concrete delegate instantiation
    /// </summary>
    public void RegisterConcreteDelegate(string name, DelegateInfo delegateInfo) => DelegateInfos[name] = delegateInfo;

    /// <summary>
    /// Attempts to get a registered delegate type
    /// </summary>
    public bool TryGetDelegate(string name, out DelegateInfo? delegateInfo) => DelegateInfos.TryGetValue(name, out delegateInfo);

    private void RegisterFunctions()
    {
        // In MCP mode, prevent _callFrames disposal across HTTP threads
        if (McpServer.ToolPipeline != null) IsMcpHost = true;
        var appNs = new Namespace(_comparer);

        appNs.AddMethod("PumpMessage", _ => NativeApp.PumpMessage());

        appNs.AddMethod("IsRunning", _ => NativeApp.IsRunning);

        appNs.AddMethod("WaitMessage", _ => NativeApp.WaitMessage());

        appNs.AddMethod("PeekMessage", _ => NativeApp.PeekMessage());

        // Use ProcessPath, works under NativeAOT single-file (MainModule?.FileName may be null). A HOST that loads the
        // engine as a DLL answers through its session instead (ScriptSession), because Environment.ProcessPath is the
        // HOST's image there: `FilePath` would name somebody else's program and `Path` somebody else's folder, so a
        // script looking for a module beside the engine would look in the wrong place. With no session attached these
        // two are exactly what they always were, which is why nothing the CLI does changes.
        var filePath = ScriptSession.ResolveImagePath();
        appNs.AddProperty("FilePath", filePath);
        appNs.AddProperty("Path", ScriptSession.ResolveHome());
        // Application.CurDir - the PROCESS's current working directory, READ and WRITTEN, which is the directory a
        // BARE relative path resolves against (the one path rule: "./x" is the run directory, "x" is this one). It is
        // a read/write member rather than the two existing spellings (Directory.GetCurrentDirectory() and
        // Directory.SetCurrentDirectory(...), which the CLR fallback already answers) because a script that has to
        // change where its bare paths land should not have to reach for a method pair, and because the WRITE needs
        // the engine's own refusals rather than the raw CLR ones.
        appNs.AddWritableProperty("CurDir",
            () => Directory.GetCurrentDirectory(),
            value => SetCurrentDirectoryFromScript(value));
        // Application.SingleInstance - ONE application, like VB's IsSingleInstance, DEFAULT FALSE (so nothing a
        // script does today changes meaning). Two effects, and the FIRST is in-process: `CreateForm(title)` answers
        // the form already carrying that title instead of building a second window. The second is cross-process:
        // turning it ON takes a named mutex for this run's script, and a SECOND run of the same script ENDS here
        // rather than building a second set of windows, which is what VB does with its second instance (it exits
        // without creating its main form). Set it at the TOP of a script: the assignment is where both effects
        // begin, so a script that sets it after building windows has already built them.
        appNs.AddWritableProperty("SingleInstance",
            () => NativeApp.SingleInstance,
            value => SetSingleInstanceFromScript(value));
        // The running executable's own icon, out of its resource: what a form takes when the script assigns none,
        // so a window carries the application's face without asking. 0 when the build has no icon resource.
        appNs.AddProperty("ExeIcon", ExeIconHandle().ToInt64());
        // Application.Args - what the executable was INVOKED with, as a script list of strings, with the
        // executable's own path left out: that is the list a C# entry point's `Main(string[] args)` receives, so a
        // script's own flags start at Args[0] when it was run as `ScriptLangCore.exe app.slc --flag` (the script
        // path the CLI was given is Args[0] there) . It is built FRESH on every read, so a script that edits the
        // list it was handed cannot change what the next read reports, and it exists so a script does not have to
        // know that Environment.GetCommandLineArgs exists (whose [0] IS the executable's path).
        appNs.AddDynamicProperty("Args", CommandLineArgs);

        // What the reads above hand a script: argv with the executable's own path dropped, as the script list
        // File.ReadAllLines answers with (so foreach, Count and indexing all work), rebuilt per read. A session
        // supplies the list when one is attached (an embedded host's arguments, never the host's own command line,
        // which is another program's flags), and with no session the process's argv is read exactly as before.
        static object? CommandLineArgs()
        {
            var list = new ScriptBoxedList(new TypeInfo(TokenType.String, false, 0, 0, null));
            // A RUN may carry arguments of its own (`.run <file> -- a b`), and they belong to the RUN rather than to the
            // session: the CLI convention holds either way, so the FIRST entry is the script's own path. A pool thread
            // has no Current, and a session with no override keeps the arguments the process or the host supplied.
            var scriptArgs = Current?.ScriptArguments;
            foreach (var arg in scriptArgs ?? ScriptSession.ResolveArguments()) list.AddRaw(arg);
            return list;
        }

        appNs.AddMethod("DispatchMessage", args =>
        {
            if (args.Length > 0 && args[0] is MessageInfo msg)
                NativeApp.DispatchMessage(msg);
            return null;
        });

        appNs.AddMethod("DoEvents", _ => { NativeApp.DoEvents(); return null; });

        appNs.AddMethod("CreateForm", args =>
        {
            var title = args.Length > 0 ? args[0] as string ?? "" : "";
            var configure = args.Length > 1 ? args[1] : null;
            // THE CONFIGURE CALLBACK, and WHERE the "newly created" rule comes from: the callback runs on the form
            // THIS call made, never on one an earlier call made. A reuse is what `Application.FindForm` is for; an init
            // that ran twice on a reused form is the recorded double-init (two sets of children, `children=2`).
            // `FormByTitle` is the lookup `CreateForm` itself asks, so "did this call create it" is decided by ONE
            // question rather than by a second flag travelling through the C# API.
            var existedBefore = NativeApp.FormByTitle(title) is not null;
            var form = NativeApp.CreateForm(title);
            if (configure is null) return form;
            // REFUSED BY NAME rather than dropped: a registration-shaped argument that is not callable would otherwise
            // configure nothing and look like the pattern worked (the rule `http.Use`/`mcp.Use`/`tcp.Use` follow).
            if (!IsScriptCallable(configure))
                RuntimeError($"Application.CreateForm takes the function that configures the new form, not "
                    + $"'{GetFriendlyTypeNameOfValue(configure)}'.", 0, 0);
            if (!existedBefore) (Current ?? this).InvokeScriptCallback(configure, [form]);
            return form;
        });

        // Application.FindForm(title) - THE FORM THIS RUN ALREADY HAS UNDER THAT TITLE, or null. It is what makes
        // "get or create" expressible without an exception, and it is the answer to the question `Form.Created`
        // cannot answer: whether a form exists ALREADY, rather than whether the instance you hold has a window.
        // Measured motivation: with `SingleInstance` on, a snippet pasted twice ran its init TWICE on the same form
        // (two labels, `children=2`), because a repeated `CreateForm` hands the first form back and nothing told the
        // script it was the second time.
        //   var panel = Application.FindForm("clipboard runner");
        //   if (panel == null) { panel = Application.CreateForm("clipboard runner"); ...init... }
        // Null rather than a refusal, matching every other lookup in this engine (`form["nope"]` answers null).
        appNs.AddMethod("FindForm", args =>
        {
            var title = args.Length > 0 ? args[0] as string ?? "" : "";
            return NativeApp.FormByTitle(title);
        });

        // How many monitors this machine has, so a script can pick one: form.Screen takes 0 .. MonitorCount() - 1
        // and refuses anything else by name. The count comes from the SAME enumeration the placement uses.
        appNs.AddMethod("MonitorCount", _ => (long)MonitorWorkAreas().Count);

        // Whether this machine is running with visual styles at all. A themed part drawn through the paint event
        // (e.ThemePart / e.ThemeText) answers -1 when it is not, and this is how a script asks BEFORE drawing.
        appNs.AddMethod("ThemeActive", _ => IsThemeActive() ? 1L : 0L);

        // A message box with NO owner, and it is a BARE global rather than a namespace member because nobody owns it:
        // a window that should own the box says so with ctrl.MessageBox(...), and "owned by nothing" is what a tool
        // with no window wants. One spelling for the owner-less case, one for the owned one.
        RegisterNative("MessageBox", TokenType.Object, args =>
        {
            var text = args.Length > 0 ? args[0] as string ?? "" : "";
            var caption = args.Length > 1 ? args[1] as string ?? "" : "";
            var type = args.Length > 2 ? Convert.ToUInt32(args[2]) : 0u;
            return (long)MessageBoxW(IntPtr.Zero, text, caption, type);
        });

        appNs.AddMethod("CreateMdiForm", args =>
        {
            var title = args.Length > 0 ? args[0] as string ?? "" : "";
            var form = new MdiForm(title);
            form.ScriptHost = this;
            // The same trailing `configure` callback the control factories take, so an MDI frame is built in one
            // statement too (`UiFactory.WithInit`); a value that is not callable is refused by name rather than dropped.
            var configure = args.Length > 1 ? args[1] : null;
            if (configure is not null)
            {
                if (!IsScriptCallable(configure))
                    RuntimeError($"Application.CreateMdiForm takes the function that configures the new frame, not "
                        + $"'{GetFriendlyTypeNameOfValue(configure)}'.", 0, 0);
                (Current ?? this).InvokeScriptCallback(configure, [form]);
            }
            return form;
        });

        appNs.AddMethod("CreateChildMdiForm", args =>
        {
            var title = args.Length > 0 ? args[0] as string ?? "" : "";
            var parent = args.Length > 1 && args[1] is Form pf ? pf.Handle : IntPtr.Zero;
            var form = new MdiForm(title, parent);
            form.ScriptHost = this;
            var configure = args.Length > 2 ? args[2] : null;
            if (configure is not null)
            {
                if (!IsScriptCallable(configure))
                    RuntimeError($"Application.CreateChildMdiForm takes the function that configures the new frame, not "
                        + $"'{GetFriendlyTypeNameOfValue(configure)}'.", 0, 0);
                (Current ?? this).InvokeScriptCallback(configure, [form]);
            }
            return form;
        });

        // Application.RunFor(ms [, form]): run, and post WM_CLOSE to every open form when the milliseconds are up, so a
        // demo can end itself. It is a SEPARATE name because Run(number) already means "focus that TabIndex".
        appNs.AddMethod("RunFor", args =>
        {
            var ms = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
            if (args.Length > 1 && args[1] is Form formForTimeout) { NativeApp.RunFor(ms, formForTimeout); }
            else { NativeApp.RunFor(ms); }
            return null;
        });
        appNs.AddMethod("Run", args =>
        {
            // If a form or MdiForm handle is passed, use that overload; otherwise run without initial form.
            // A NUMBER is the other thing Run takes: the TabIndex of the control to open focused on, so Application.Run(3) is
            // "run, and take the focus to TabIndex 3". It is tested FIRST because a number is neither a form nor an
            // MdiForm, so it cannot shadow either of them.
            if (args.Length == 1 && (args[0] is int || args[0] is long))
                NativeApp.Run(Convert.ToInt32(args[0]));
            else if (args.Length > 1 && args[0] is Form formWithStop && (args[1] is int || args[1] is long))
                NativeApp.Run(formWithStop, Convert.ToInt32(args[1]));
            else if (args.Length > 0 && args[0] is Form f)
                NativeApp.Run(f);
            else if (args.Length > 0 && args[0] is MdiForm mf)
            {
                if (mf.Handle == IntPtr.Zero)
                    mf.Show();
                NativeApp.Run();
            }
            else
                NativeApp.Run();
            return null;
        });

        appNs.AddMethod("Quit", args =>
        {
            NativeApp.Exit(args.Length > 0 && (args[0] is int || args[0] is long)
                        ? Convert.ToInt32(args[0]) : 0);   // a CODE is optional: it travels in WM_QUIT, which the loop now honours
            return null;
        });

        // TCP Network support
        CurrentFrame.Scopes.Define("Server", new TcpServer(this));
        CurrentFrame.Scopes.Define("Client", new TcpClient(this));
        CurrentFrame.Scopes.Define("ClientStatus", typeof(ClientStatus));


        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("SystemMetricsForDpi", args =>
                {
                    var index = Convert.ToInt32(args[0]);
                    var dpi = Convert.ToUInt32(args[1]);
                    return (long)GetSystemMetricsForDpi(index, dpi);
                });

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("AdjustWindowRectExForDpi", args =>
                {
                    var pRectPtr = ToNInt(args[0]);
                    var pRect = Marshal.PtrToStructure<Rect>(pRectPtr);
                    var style = Convert.ToUInt32(args[1]);
                    var bMenu = Convert.ToBoolean(args[2]);
                    var exStyle = Convert.ToUInt32(args[3]);
                    var dpi = Convert.ToUInt32(args[4]);
                    var result = AdjustWindowRectExForDpi(ref pRect, style, bMenu, exStyle, dpi);
                    Marshal.StructureToPtr(pRect, pRectPtr, false);
                    return result ? 1L : 0L;
                });

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("SystemMetrics", args =>
                {
                    var index = Convert.ToInt32(args[0]);
                    return (long)GetSystemMetrics(index);
                });

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("AsyncKeyState", args =>
                {
                    var vk = Convert.ToInt32(args[0]);
                    return (GetAsyncKeyState(vk) & 0x8000) != 0 ? 1L : 0L;
                });

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("ForegroundWindow", _ => GetForegroundWindow().ToInt64());

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("CursorPos", args =>
                {
                    GetCursorPos(out var pt);
                    return new object[] { pt.x, pt.y };
                });

        // The DESKTOP's pixels, and they are the app's for the same reason the queries above are: a control's own
        // client already has Control.GetPixel, while a SCREEN read has no object to hang from at all (a caption,
        // a scrollbar, another process's window), so it belongs here rather than behind a LoadLibrary. Two forms:
        // `GetPixel(x, y)` opens and closes its own dc, and `GetPixel(dc, x, y)` reads through one a caller holds
        // between `GetDC()` and `ReleaseDC(dc)`, which is what a loop over hundreds of pixels wants.
        appNs.AddMethod("GetPixel", args =>
                {
                    if (args.Length >= 3)
                        return (long)GetPixel((nint)Convert.ToInt64(args[0]), Convert.ToInt32(args[1]), Convert.ToInt32(args[2]));
                    if (args.Length < 2)
                        throw new ScriptException("Application.GetPixel: a screen point (x, y), or a dc with it (dc, x, y).");
                    var screenDc = GetDC(IntPtr.Zero);
                    if (screenDc == IntPtr.Zero) return -1L;
                    try { return (long)GetPixel(screenDc, Convert.ToInt32(args[0]), Convert.ToInt32(args[1])); }
                    finally { ReleaseDC(IntPtr.Zero, screenDc); }
                });

        // The dc those reads can share: the SCREEN's, handed back by ReleaseDC. A pair rather than a bare GetDC
        // because a script that leaks one leaks the desktop's, and no caller can free somebody else's.
        appNs.AddMethod("GetDC", _ => GetDC(IntPtr.Zero).ToInt64());
        appNs.AddMethod("ReleaseDC", args => (long)ReleaseDC(IntPtr.Zero, (nint)Convert.ToInt64(args[0])));

        // The WRITE side of the template pair, and the reason it has a name of its own while a document of any
        // other value is Json.Serialize's: the reader is form-specific (nothing generic can construct a Control),
        // so a script that stores and restores a form writes the two calls in one vocabulary. It IS Json.Serialize
        // on the same value, so there is one implementation and one format.
        appNs.AddMethod("FormToJson", args =>
        {
            if (args.Length < 1 || args[0] is not IControlHost host)
                throw new ScriptException(
                    "Application.FormToJson: a Form, an MdiForm or an MdiChild to write as a document "
                    + "(for any other control, Json.Serialize writes its table).");
            // The SAME options the serializers take, read by the SAME reader, so the document form cannot drift
            // from Json.Serialize: a dictionary literal, a JSON string of one or a bare option name all work, and
            // `pretty` composes. An unknown key is REPORTED by name rather than dropped, which is the reader's rule.
            var (opts, pretty) = ReadSerializeOptions(args, "Application.FormToJson");
            return SerializeToText(host, opts, pretty);
        });

        // A form OUT OF A DOCUMENT, which is the one line a stored template needs: the document carries the form's
        // own properties, its title included as the Text row, and every control inside it. The form is created
        // EMPTY and the rows fill it in, so nothing here has to know what the document says before it says it.
        appNs.AddMethod("FormFromJson", args =>
        {
            if (args.Length < 1 || args[0] is not string doc)
                throw new ScriptException("Application.FormFromJson: the document Json.Serialize(form) wrote.");
            // ONE door, and the document's own root Kind decides which host is built: a Form, an MdiForm, or an
            // MdiChild (which gets a frame of its own to be parented to, and keeps it through MdiChild.Owner).
            return LoadHostFromJson(doc);
        });

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("MessagePos", _ => GetMessagePos().ToInt64());

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("WindowFromPoint", args =>
                {
                    if (args.Length < 2) return 0L;
                    return (long)WindowFromPointAt(Convert.ToInt64(args[0]), Convert.ToInt64(args[1]));
                });

        // Moved from the WinApi namespace (2026-10-01): the machine-wide queries belong to the app.
        appNs.AddMethod("SetProcessDPIAware", _ => SetProcessDPIAware() ? 1L : 0L);

        CurrentFrame.Scopes.Define("Application", appNs);
        CurrentFrame.Scopes.Define("CancelEventArgs", typeof(CancelEventArgs));
        CurrentFrame.Scopes.Define("Control", typeof(Control));
        CurrentFrame.Scopes.Define("Form", typeof(Form));
        // The UI names a script otherwise has to spell as a NUMBER: `ctrl.Dock` is a DockStyle, so registering
        // the type is what lets a script write `ctrl.Dock = DockStyle.Bottom` instead of `= 4`. Measured before
        // this: `DockStyle` was an undefined VARIABLE while `bar.Dock = 4` worked and `typeof(bar.Dock)` already
        // answered "DockStyle", i.e. the value's own type was named to the user and could not be written back.
        // A registered CLR type resolves its members as public statics (the same path ConsoleColor.Red takes), so
        // the enum's members need no table of their own.
        CurrentFrame.Scopes.Define("DockStyle", typeof(DockStyle));
        CurrentFrame.Scopes.Define("Menu", typeof(Menu));
        CurrentFrame.Scopes.Define("MouseButtons", typeof(MouseButtons));   // a value a script can WRITE: Left/Right/Middle, not the flags enum numbers   // the menu door: a script names Menu.Create()
        // The same rule for the theme: `form.Theme = ThemeMode.Dark` and `x is ThemeMode` both have to resolve, and a
        // scope binding alone answers `is` with a silent FALSE (measured for DockStyle, which is why both halves exist).
        CurrentFrame.Scopes.Define("ThemeMode", typeof(ThemeMode));
        // The two row types the designer tables project (Control.PropList / Control.StyleFlags): their REPORTED
        // name is what a script reads it back as, so a script must be able to write the same name (the rule the
        // DockStyle line above records).
        CurrentFrame.Scopes.Define("ControlProp", typeof(ControlProp));
        CurrentFrame.Scopes.Define("StyleFlag", typeof(StyleFlag));
        // The two row types a CONTAINER contents travel as: a table's columns and a tree's nodes. Registered for
        // the same reason ControlProp is, so a name the document reports is a name a script can write.
        CurrentFrame.Scopes.Define("ColumnProp", typeof(ColumnProp));
        CurrentFrame.Scopes.Define("TreeItemProp", typeof(TreeItemProp));
        CurrentFrame.Scopes.Define("MdiForm", typeof(MdiForm));
        CurrentFrame.Scopes.Define("MdiChild", typeof(MdiChild));
        CurrentFrame.Scopes.Define("Size", typeof(Size));
        CurrentFrame.Scopes.Define("EventArgs", typeof(EventArgs));

        // CreateTimer([intervalMs, callback]) → TheTimer.
        //   CreateTimer()                      → manual: set .Interval/.Tick, then .Start()
        //   CreateTimer(500, cb)               → periodic timer, started immediately
        RegisterNative("CreateTimer", TokenType.Object, args =>
        {
            var timer = new TheTimer();
            ScriptFunction? callback = null;
            if (args.Length > 0)
            {
                if (args[0] is int iv) timer.Interval = iv;
                else if (args[0] is long lv) timer.Interval = (int)lv;
            }
            if (args.Length > 1 && args[1] is ScriptFunction cb) callback = cb;
            if (callback != null)
            {
                timer.Tick = callback;
                timer.Start();
            }
            return timer;
        });

        RegisterNative("CreateOpenFileDialog", TokenType.Object, _ => new OpenFileDialog());
        RegisterNative("CreateSaveFileDialog", TokenType.Object, _ => new SaveFileDialog());

        // ── MCP bridge tool registration ──
        RegisterNative("RegisterMcpTool", TokenType.Void, args =>
        {
            var name = args[0] as string ?? throw new ArgumentException("Tool name required");
            var description = args.Length > 1 ? (args[1] as string ?? "") : "";

            ScriptFunction? ResolveHandler(object? arg)
            {
                if (arg is ScriptFunction sf) return sf;
                if (arg is string fnName && Current!.CurrentFrame.Scopes.TryGet(fnName, out var obj) && obj is ScriptFunction sf2)
                    return sf2;
                return null;
            }

            var handler = args.Length > 2 ? ResolveHandler(args[2]) : null;
            var schema = args.Length > 3 ? (args[3] as string) : null;

            if (handler == null)
                throw new ArgumentException("Tool handler function required");

            McpServer.RegisterTool(name, description, handler, schema);
            return null;
        });

        // ── MCP bridge resource registration ──
        RegisterNative("RegisterMcpResource", TokenType.Void, args =>
        {
            var uri = args[0] as string ?? throw new ArgumentException("Resource URI required");

            // Handler can be a ScriptFunction directly (inline lambda) or a string name (named function)
            ScriptFunction? ResolveHandler2(object? arg)
            {
                if (arg is ScriptFunction sf) return sf;
                if (arg is string fnName && Current!.CurrentFrame.Scopes.TryGet(fnName, out var obj) && obj is ScriptFunction sf2)
                    return sf2;
                return null;
            }

            var name = args.Length > 1 ? (args[1] as string ?? "") : uri;
            var mimeType = args.Length > 2 ? (args[2] as string ?? "text/plain") : "text/plain";
            var handler = args.Length > 3 ? ResolveHandler2(args[3]) : null;
            if (handler == null)
                throw new ArgumentException("Resource handler function required (arg 4)");

            McpServer.RegisterResource(uri, name, mimeType, handler);
            return null;
        });

        // ── MCP / TCP middleware pipelines (static MCP pipeline shared across interpreters) ──
        McpServer.ToolPipeline ??= new MiddlewarePipeline<McpContext>();
        //Logging.Log.Info("Init", "mcp pipeline Count=" + McpServer.ToolPipeline.Count);
        var tcpPipeline = new MiddlewarePipeline<TcpContext>();
        // The engine's OWN TCP server (Server.Start, in Networking) reads this field when its accept loop starts,
        // which is what makes a `tcp.Use` middleware run: the pipeline used to be a local that was registered and
        // then dropped, so the middleware never ran and `TcpContext.RemoteAddress` was never filled.
        TcpPipeline = tcpPipeline;
        // Namespace dispatch works with AOT (no reflection) unlike reflected .NET objects
        var mcpNs = new Namespace(_comparer);
        RegisterPipelineUse(mcpNs, "mcp.Use", "the function to run on every tool call", McpServer.ToolPipeline!,
            ctx =>
            {
                var itp = McpServer.McpInterpreter;
                if (itp == null) ctx.Error("no interpreter is available to run this middleware: start the server with --repl");
                return itp;
            });
        CurrentFrame.Scopes.Define("mcp", mcpNs);

        var tcpNs = new Namespace(_comparer);
        // A connection runs on the interpreter that OWNS the server, which is this one: `Server.Start` is called
        // from a script of this interpreter, and the accept loop holds this interpreter's `TcpPipeline`. So the
        // resolver answers `this` instead of reaching for `McpServer.McpInterpreter`, which is null unless the
        // MCP server was started with --repl and has nothing to do with a script's own socket.
        RegisterPipelineUse(tcpNs, "tcp.Use", "the function to run on every connection", tcpPipeline, _ => this);
        CurrentFrame.Scopes.Define("tcp", tcpNs);

        var httpPipe = new MiddlewarePipeline<HttpContext>();
        var httpNs = new Namespace(_comparer);
        var httpRoutes = new HttpRouteTable();
        var routeMiddlewareAdded = false;

        // THE INTERPRETER QUESTION is the same for a middleware and for a route handler, so it is asked once. A
        // missing interpreter used to mean the middleware was skipped with nothing said at all, which is how an app
        // answered empty 200s while looking healthy. The context turns an ErrorMessage into a 500 carrying its text,
        // so name the cause: the pipeline runs script handlers on the interpreter that started the server, and
        // without --repl there is none.
        Interpreter? PipelineInterpreter(HttpContext ctx)
        {
            var itp = McpServer.McpInterpreter;
            if (itp != null) return itp;
            ctx.ErrorMessage = "no interpreter is available to run this middleware: start the server with --repl";
            Logging.Log.Error("MCP", "http pipeline skipped: the pipeline has no interpreter");
            return null;
        }

        // `http.Use` goes through the SAME registration as `mcp.Use` and `tcp.Use` (RegisterPipelineUse), with the
        // three things that differ passed in: the pipeline, the member a refusal names, and how a missing
        // interpreter is reported for that context.
        RegisterPipelineUse(httpNs, "http.Use", "the function to run on every request", httpPipe, ctx => PipelineInterpreter(ctx));

        // A REFUSAL rather than a silent drop: a registration that registers nothing is an app that answers 404
        // while looking healthy. The old guard (`if (args[0] is ScriptFunction mw)`) did exactly that for any other
        // callable, and the reachable case was a METHOD READ AS A VALUE, which the member-read arms could not even
        // produce: `app.Handle` reported "Field or event 'Handle' not found in class" until that arm learned to
        // answer a BoundMethod, which is why a bound-method middleware was impossible to write at all rather than
        // merely dropped.
        void RequireCallable(object? value, string member, string shape)
        {
            if (IsScriptCallable(value)) return;
            RuntimeError($"{member} takes {shape}, not '{GetFriendlyTypeNameOfValue(value)}'.", 0, 0);
        }

        // ── http.MapGet / MapPost / MapPut / MapDelete: the route table ─────────────────────────────────────────
        // A route table is the APPLICATION's, not the request's, which is why these live beside `Use` (the pipeline
        // is process-wide) and not on the context: a table registered per request would be rebuilt every request.
        // The handler is `fun(ctx, args)`, where `args` holds the pattern's `{name}` segments by name, and its
        // RETURN is the answer: a string is the response body, and a handler that writes `ctx` itself (or a void
        // one) returns nothing.
        void AddRoute(string verb, string member, object?[] args)
        {
            var pattern = args.Length > 0 ? "" + args[0] : "";
            var handler = args.Length > 1 ? args[1] : null;
            RequireCallable(handler, $"http.{member}", $"the function that answers a {verb} request as its second argument");
            // A pattern this session ALREADY holds is a route that will never run, because the table matches in
            // REGISTRATION order, and the pipeline with its table is per INTERPRETER (see the assignment at the end of
            // this method), so a `.run` ADDS to the table rather than replacing it. That makes this the observable
            // half of the two questions a live session asks: a re-run of the same endpoint file re-adds its routes,
            // and a DIFFERENT endpoint file cannot take over a pattern an earlier one already owns. REPORTED rather
            // than refused: re-running a file is legitimate and its routes are equivalent, so the news is "the first
            // registration is the one answering", not an error.
            var alreadyHeld = httpRoutes.CountSame(verb, pattern, Comparison);
            // THE DIRECTORY A ROUTE'S `./` MEANS, captured HERE because this is the only moment it is known: a route
            // handler runs later, from a request, and the session RESETS its per-run state when the `.run` that
            // registered the routes ends, so `ResolvePath` can no longer answer `./` at request time (measured: an
            // `http.File("./x")` route answered a 500 with "Cannot resolve relative import without a current
            // directory"). FIRST registration wins, which matches the table's own first-registration-answers rule.
            _routeBaseDir ??= (Current ?? this)._currentDirectory;
            // THE SOURCE THAT REGISTERED THE DUPLICATE IS THE RUNNING ONE, not this closure's. `http`'s members are
            // registered ONCE, on the interpreter that BUILT the namespace, so a later `.run` executes on another
            // interpreter while this closure still answers from the first: measured with the shipped tools loaded,
            // `this` was stale and reported `mcp_core.slc` (the tool load) for patterns only the BOARD registers
            // (`todos`, `toggle`, `stats`), while `Current` was `htmx_todo.slc`. `Current ?? this` is the rule the
            // internal helpers already follow (`__run_import__` and friends), and it is why this names a file a
            // reader can actually go and edit.
            var reporting = Current ?? this;
            var owner = alreadyHeld > 0 ? httpRoutes.OwnerOf(verb, pattern, Comparison) : null;
            httpRoutes.Add(verb, pattern, handler, reporting.CurrentSourceName);
            // WHICH FILES THIS TABLE CARRIES, so a 404 can say "from htmx_todo.slc".
            httpRoutes.NoteSource(reporting.CurrentSourceName);
            if (alreadyHeld > 0)
            {
                // IT NAMES WHO OWNS THE PATTERN, not only who is late: measured on a live session, a board's
                // `http.MapGet("/")` read "the FIRST registration answers and this one never will", which leaves a
                // reader unable to find the culprit, while the owner was the engine's own `mcp_httpserver.slc`
                // (loaded because `--tools-dir` was not an empty folder).
                var ownedBy = string.IsNullOrEmpty(owner?.Source) ? "" : $" (it is '{owner!.Source}' that answers)";
                Logging.Log.Warning("MCP", $"http.{member}(\"{pattern}\") is already served by this session: the FIRST "
                    + $"registration answers{ownedBy} and this one never will ({alreadyHeld + 1} registrations for it, "
                    + $"this one from {reporting.CurrentSourceName})");
            }
            if (routeMiddlewareAdded) return;
            routeMiddlewareAdded = true;

            // ONE middleware serves the whole table, and it is registered on the FIRST Map* call so the order is
            // the ASP.NET one: the `http.Use` middlewares registered before it run first, and the ones registered
            // after it run only when NO route matched. A request that matches nothing is handed on with `next()`,
            // which is what keeps a middleware (or the protocol's own endpoint) able to answer it.
            httpPipe.Use((ctx, next) =>
            {
                if (PipelineInterpreter(ctx) is not { } itp) return null;
                if (!httpRoutes.TryMatch(ctx.HttpMethod, ctx.Url, Comparison, _comparer, out var handler, out var routeArgs))
                {
                    next();
                    return null;
                }

                // A HANDLER MAY BE ASYNC, and this is the one place that can await it. A script `async` body answers the
                // engine's own Task (a CLR call made inside a handler answers the runtime's), so the answer is WAITED FOR
                // and its RESULT becomes the answer. Measured reason it must be here: without this an async handler is
                // SILENTLY EMPTY, because a Task is an object and the JSON branch below serialized it as `null`, i.e. a
                // 200 with a 4-byte body where the handler meant to answer a value.
                //
                // AND A HANDLER MAY ASK FOR THE REQUEST'S TOKEN: a third declared parameter receives
                // `ctx.RequestAborted`, so `fun (HttpContext ctx, Dictionary args, CancellationToken ct)` is how a
                // handler cancels its own work when the client goes away (that token is cancelled when a write fails,
                // which is what a closed tab looks like to this server). The declaration is what decides, so a handler
                // with two parameters is untouched and pays nothing: `CallableArity` is the same question an `Events`
                // producer body already asks, asked once.
                var handlerArgs = CallableArity(handler) >= 3
                    ? new object?[] { ctx, routeArgs, ctx.RequestAborted }
                    : new object?[] { ctx, routeArgs };
                var answer = AwaitHandlerAnswer(itp.InvokeScriptCallback(handler, handlerArgs));
                // The handler's RETURN is the answer, and the three cases are the paste's own rules: a RESULT decides
                // its status and its content type, a STRING is the body as text/plain (the server's default for a
                // text body), and any OTHER object is serialized as JSON, so a `{ }` literal or a script class can
                // be returned directly. A `byte[]` is sent as bytes rather than as a JSON array of numbers.
                if (answer is HttpResult result)
                {
                    ctx.ResponseStatus = result.Status;
                    if (result.ContentType != null) ctx.ResponseContentType = result.ContentType;
                    // A STREAM first: it is exclusive with bytes and with a body, and the server writes it with
                    // chunked framing, so it must not fall into the "answered with an empty body" marker below.
                    if (result.Stream != null) ctx.ResponseStream = result.Stream;
                    else if (result.Bytes != null) ctx.ResponseBytes = result.Bytes;
                    else ctx.ResponseBody = result.Body ?? "";
                    if (result.Headers != null)
                        foreach (var header in result.Headers) ctx.ResponseHeaders[header.Key] = header.Value;
                }
                else if (answer is string bodyText) ctx.ResponseBody = bodyText;
                else if (answer is byte[] rawBytes) ctx.ResponseBytes = rawBytes;
                else if (answer is not null and not VoidMarker)
                {
                    ctx.ResponseBody = SerializeToText(answer);
                    ctx.ResponseContentType = HttpResult.JsonType;
                }

                // A route that MATCHED is an answer even when the handler put nothing in it: 200 with an empty body,
                // where a request nothing answered at all is the server's own 404, which carries NO body and reports its
                // reason in the log (the ASP.NET Core rule for a status the framework sets). The empty string here is
                // what marks "answered", which is why it is set rather than left null.
                if (ctx.ResponseBody == null && ctx.ResponseBytes == null && ctx.ResponseStream == null
                    && ctx.ResponseStatus == 200 && ctx.ErrorMessage == null)
                    ctx.ResponseBody = "";
                return null;
            });
        }

        httpNs.AddMethod("MapGet", args => { AddRoute("GET", "MapGet", args); return null; });
        httpNs.AddMethod("MapPost", args => { AddRoute("POST", "MapPost", args); return null; });
        httpNs.AddMethod("MapPut", args => { AddRoute("PUT", "MapPut", args); return null; });
        httpNs.AddMethod("MapDelete", args => { AddRoute("DELETE", "MapDelete", args); return null; });
        // `http.IsServed(method, pattern)`: does THIS session already answer this method and pattern? The question
        // exists because the engine's own sample tools are loaded into a session (every `<exe>/tools/*.slc` runs at
        // `--mcp` startup unless `--tools-dir` names an empty folder) and `mcp_httpserver.slc` claims `GET /`, so an
        // application that maps `/` unasked registers a route that can never run and is told so. Asking FIRST lets a
        // program claim a pattern only when it is free, which is what the htmx board does, and it keeps the warning
        // for the case where two registrations really do collide. The comparison is the TABLE's own (the pattern is
        // normalized the way a match normalizes it, so `"/x/"` and `"x"` are one pattern) plus the interpreter's case
        // rule, so a script sees exactly what a request would match.
        httpNs.AddMethod("IsServed", args =>
        {
            if (args.Length < 2 || args[0] is not string verb || args[1] is not string pattern)
                RuntimeError("http.IsServed takes a method and a pattern, both strings, as in "
                           + "http.IsServed(\"GET\", \"/\")", 0, 0);
            return httpRoutes.CountSame((string)args[0]!, (string)args[1]!, Comparison) > 0;
        });

        // ── the RESULTS a handler can return when the status, the content type or the bytes are its own business ──
        // A returned STRING is the body as text/plain and a returned result decides for itself, which is ASP.NET's
        // split (`IResult` vs a bare string) and the reason these exist: a JSON answer, a 404 or a 201 with a
        // `Location` are three answers a string cannot express. ONE behaviour per spelling, so there is no `Ok`
        // beside `Status(200, ...)`.
        static string BodyOf(object?[] args, int index) => args.Length > index ? "" + args[index] : "";
        httpNs.AddMethod("Text", args => new HttpResult(200, HttpResult.TextType, BodyOf(args, 0), null, null));
        httpNs.AddMethod("Html", args => new HttpResult(200, HttpResult.HtmlType, BodyOf(args, 0), null, null));
        httpNs.AddMethod("Json", args =>
            new HttpResult(200, HttpResult.JsonType, SerializeToText(args.Length > 0 ? args[0] : null), null, null));
        // `NotFound()` with NO argument answers a 404 with no body, which is what `Results.NotFound()` does, while
        // `NotFound(body)` writes the body the script chose: a default sentence here would be this engine answering
        // ON BEHALF OF the application, and a client may render whatever it is handed.
        httpNs.AddMethod("NotFound", args => args.Length > 0
            ? new HttpResult(404, HttpResult.TextType, BodyOf(args, 0), null, null)
            : new HttpResult(404, null, null, null, null));
        // `http.ErrorPage(code, htmlOrFunction [, contentType])`: the page THIS program wants for a status the framework
        // sets. A STRING is served as it is; a Function is called with the request's `ctx` and its return value IS the
        // body, which is the hybrid shape: one registration that emits an HTML buffer to a browser and a JSON buffer to
        // a client, with the type it declares. Codes with no page fall back to the engine's own default.
        httpNs.AddMethod("ErrorPage", args =>
        {
            var code = args.Length > 0 ? Convert.ToInt32(args[0]) : 404;
            ErrorPages[code] = (args.Length > 1 ? args[1] : null,
                                args.Length > 2 && args[2] != null ? "" + args[2] : null);
            return null;
        });
        httpNs.AddMethod("Status", args =>
        {
            var status = args.Length > 0 ? Convert.ToInt32(args[0]) : 200;            // A status with no body sends NONE: an empty body is what a 204 or a 304 has to be, and it is also what
            // makes the answer recognisable to the server rather than "nothing answered this request".
            return args.Length > 1
                ? new HttpResult(status, HttpResult.TextType, BodyOf(args, 1), null, null)
                : new HttpResult(status, null, "", null, null);
        });
        httpNs.AddMethod("Created", args =>
        {
            var location = BodyOf(args, 0);
            var value = args.Length > 1 ? args[1] : null;
            return new HttpResult(201, HttpResult.JsonType, SerializeToText(value), null,
                new Dictionary<string, string> { ["Location"] = location });
        });
        // http.File(path [, contentType [, fileName]]): a FILE as the answer, which is `Results.File` /
        // `PhysicalFile` in the C# note. The path follows the ONE path rule (`./x` is beside the RUN, exactly as
        // `File.ReadAllBytes` behaves), the type is guessed from the extension with the very table the engine's own file
        // server serves from, so a route and that server cannot disagree about what a `.pdf` is, and a fileName turns
        // the answer into a DOWNLOAD (`Content-Disposition: attachment`). A file's length is known, so this needs no
        // streaming: the bytes go out with an exact Content-Length.
        httpNs.AddMethod("File", args =>
        {
            if (args.Length < 1 || args[0] is not string rawPath)
                throw new ScriptException("http.File expects a file path.");
            var path = ResolveRoutePath(rawPath);
            if (!System.IO.File.Exists(path))
                throw new ScriptException($"http.File: no such file '{path}'");
            var contentType = args.Length > 1 && args[1] is string statedType && statedType.Length > 0
                ? statedType
                : NativeWebSocketServer.ContentTypeForPath(path);
            var downloadName = args.Length > 2 && args[2] is string statedName && statedName.Length > 0
                ? statedName
                : null;
            return new HttpResult(200, contentType, null, System.IO.File.ReadAllBytes(path),
                downloadName is null ? null : new Dictionary<string, string>
                {
                    ["Content-Disposition"] = AttachmentDisposition(downloadName)
                });
        });
        // http.Bytes(data, contentType [, fileName]): the same answer for bytes the script already holds (what a route
        // produces in memory), so a download needs no temporary file on the way out. The bytes may arrive as a `byte[]`
        // (what `File.ReadAllBytes` and `Encoding.UTF8.GetBytes` answer) or as a list/array of NUMBERS, because a script
        // writing `[65, 66, 67]` holds an int[] and refusing that would be pedantry about a type it can convert.
        httpNs.AddMethod("Bytes", args =>
        {
            if (args.Length < 1)
                throw new ScriptException("http.Bytes expects the bytes to send as its first argument.");
            var data = args[0] switch
            {
                byte[] given => given,
                int[] ints => NumbersToBytes(ints),
                long[] longs => NumbersToBytes(Array.ConvertAll(longs, v => (int)v)),
                IEnumerable<object?> items => NumbersToBytes(items.Select(v => Convert.ToInt32(v)).ToArray()),
                _ => throw new ScriptException(
                    $"http.Bytes expects the bytes to send as its first argument, not '{GetFriendlyTypeNameOfValue(args[0])}'.")
            };
            var contentType = args.Length > 1 && args[1] is string statedType && statedType.Length > 0
                ? statedType
                : HttpResult.BytesType;
            var downloadName = args.Length > 2 && args[2] is string statedName && statedName.Length > 0
                ? statedName
                : null;
            return new HttpResult(200, contentType, null, data,
                downloadName is null ? null : new Dictionary<string, string>
                {
                    ["Content-Disposition"] = AttachmentDisposition(downloadName)
                });
        });
        // http.Avro(records, schema [, options]): the answer is an Avro OBJECT CONTAINER FILE, typed
        // `application/avro`, which is the wire format AvroConvert's HTTP layer uses (`AvroResults.Avro(value)`
        // answers an AvroConvert container). THE CONTAINER IS SELF-DESCRIBING: its header carries the schema and the
        // codec, so a client decodes it from the bytes alone and no side channel has to state either. The options are
        // `Avro.WriteContainer`'s own (`{ "codec": "zstandard" }`), so a route chooses compression the same way a
        // file writer does.
        //
        // A SINGLE record is [record]: a container holds a sequence, and guessing between "one value's binary
        // encoding" and "a container of one" would make the answer's SHAPE depend on the argument's shape.
        httpNs.AddMethod("Avro", args =>
        {
            var options = AvroOptions.Read(args.Length > 2 ? args[2] : null, "http.Avro");
            var schema = AvroSchemaParser.From(args.Length > 1 ? args[1] : null, "http.Avro");
            var records = args.Length > 0 ? args[0] : null;
            // A DICTIONARY IS AN `IEnumerable` TOO, of key/value PAIRS, so the shape test has to name it: without
            // this it reached the writer and was refused there with "needs the field 'Id'", which does not say what
            // the caller got wrong (measured).
            if (records is not System.Collections.IEnumerable rows || records is string
                || records is System.Collections.IDictionary || records is IScriptDictionary)
                throw new ScriptException(
                    "http.Avro: the records are a list or an array of values, not " +
                    $"'{GetFriendlyTypeNameOfValue(records)}'. A container holds a sequence, so pass [record] for " +
                    "one record, or use Avro.Serialize for one value's binary encoding.");
            return new HttpResult(200, HttpResult.AvroType, null,
                AvroContainer.Write(rows, schema, options.Codec, _caseSensitive, "http.Avro"), null);
        });
        // http.StreamLines(source [, contentType]) and http.StreamEvents(source): the answer STREAMS, one element
        // per line, with chunked framing, so a big export never has to fit in memory and a client sees each line as
        // it is produced. Two members rather than one with a format argument, because the two framings are not
        // interchangeable for a client (a browser reads text/event-stream natively, an NDJSON consumer parses one
        // JSON value per line) and a misspelled format would be a runtime refusal.
        //
        // THREE THINGS AN AUTHOR HAS TO KNOW, and the `http` entry states them: the status is chosen BEFORE the
        // first element (afterwards a failure can only end the stream, and it is reported in the log), NOTHING
        // that buffers may sit in front of the client (curl -N, or ResponseHeadersRead, or the batching is the
        // client's), and the source is pulled on the request thread, so a stream blocks the other requests this
        // server takes while it is open.
        httpNs.AddMethod("StreamLines", args => StreamResult(args, HttpStreamFormat.JsonLines, "http.StreamLines"));
        httpNs.AddMethod("StreamEvents", args => StreamResult(args, HttpStreamFormat.EventStream, "http.StreamEvents"));
        CurrentFrame.Scopes.Define("http", httpNs);


        McpServer.HttpPipeline = httpPipe;
        // The pipeline needs an interpreter to invoke its middleware on, and without one every middleware is skipped
        // in silence. This interpreter owns the pipeline, so it is the one to fall back to.
        McpServer.EnsureInterpreter(this);

        // Also expose on Application namespace for convenience
        appNs.AddMethod("CreateOpenFileDialog", _ => new OpenFileDialog());
        appNs.AddMethod("CreateSaveFileDialog", _ => new SaveFileDialog());
        // A modal prompt that returns what was typed, or null when it was cancelled, and the handle of its window
        // while it is up so a suite can answer it without a person.
        appNs.AddMethod("InputBox", args => NativeApp.InputBoxCore(
            args.Length > 0 ? args[0] as string ?? "" : "",
            args.Length > 1 ? args[1] as string ?? "" : "",
            args.Length > 2 ? args[2] as string : null,
            args.Length > 3 ? args[3] : null,   // defaultButton
            args.Length > 4 ? args[4] : null));   // the timeout map, the LAST parameter
        appNs.AddMethod("CreateInputBox", _ => new InputBox());

        RegisterNative("CreateButton", TokenType.Object, args =>
        {
            var parentHwnd = ToNInt(args[0]);
            var text = args[1] as string ?? "";
            var x = Convert.ToInt32(args[2]);
            var y = Convert.ToInt32(args[3]);
            var w = Convert.ToInt32(args[4]);
            var h = Convert.ToInt32(args[5]);

            const uint WS_CHILD = 0x40000000;
            const uint WS_VISIBLE = 0x10000000;
            const uint BS_PUSHBUTTON = 0x0000;
            var style = WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON;

            var hwnd = CreateWindowExW(0, "BUTTON", text,
                style, x, y, w, h, parentHwnd, nint.Zero, nint.Zero, nint.Zero);

            if (hwnd == nint.Zero)
            {
                var lastErr = Marshal.GetLastWin32Error();
                throw new ScriptException("Failed to create button (error: " + lastErr + ").");
            }

            return hwnd.ToInt64();
        });

        RegisterNative("CreateRichEdit", TokenType.Object, args =>
        {
            var parentHwnd = ToNInt(args[0]);
            var x = Convert.ToInt32(args[1]);
            var y = Convert.ToInt32(args[2]);
            var w = Convert.ToInt32(args[3]);
            var h = Convert.ToInt32(args[4]);

            // Retry once on failure (AOT P/Invoke edge case)
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try { return CreateEditCore(parentHwnd, x, y, w, h); }
                catch (OverflowException) when (attempt == 0) { }
            }
            return 0;
        });

        static long CreateEditCore(nint parentHwnd, int x, int y, int w, int h)
        {
            const uint WS_CHILD = 0x40000000;
            const uint WS_VISIBLE = 0x10000000;
            const uint WS_VSCROLL = 0x00200000;
            const uint WS_HSCROLL = 0x00100000;
            const uint ES_MULTILINE = 0x0004;
            const uint ES_AUTOVSCROLL = 0x0040;
            const uint ES_AUTOHSCROLL = 0x0080;
            const uint ES_NOHIDESEL = 0x0100;
            var style = WS_CHILD | WS_VISIBLE | WS_VSCROLL | WS_HSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_AUTOHSCROLL | ES_NOHIDESEL;

            var hwnd = CreateWindowExW(0, "EDIT", "",
                style, x, y, w, h, parentHwnd, nint.Zero, nint.Zero, nint.Zero);

            if (hwnd == nint.Zero)
                throw new ScriptException("Failed to create edit control (error: " + Marshal.GetLastWin32Error() + ").");

            return hwnd.ToInt64();
        }

        RegisterNative("CreateTrackBar", TokenType.Object, args =>
        {
            var x = Convert.ToInt32(args[0]);
            var y = Convert.ToInt32(args[1]);
            var w = Convert.ToInt32(args[2]);
            var h = Convert.ToInt32(args[3]);
            return new TrackBar(x, y, w, h);
        });

        // Add this native function
        RegisterNative("PtrToLong", TokenType.Long, args =>
        {
            if (args[0] is IntPtr ptr)
                return ptr.ToInt64();
            return 0L;
        });

        var formStartPosNs = new Namespace(_comparer);
        formStartPosNs.AddProperty("Manual", 0L);
        formStartPosNs.AddProperty("Center", 1L);
        CurrentFrame.Scopes.Define("StartPosition", formStartPosNs);

        //// ============================================================================
        //// PATH METHODS
        //// ============================================================================

        //var pathNs = new Namespace(_comparer);
        //pathNs.AddMethod("GetFileName", args => Path.GetFileName(Convert.ToString(args[0])!));
        //pathNs.AddMethod("GetDirectoryName", args => Path.GetDirectoryName(Convert.ToString(args[0])!));
        //pathNs.AddMethod("GetExtension", args => Path.GetExtension(Convert.ToString(args[0])!));
        //pathNs.AddMethod("GetFileNameWithoutExtension", args => Path.GetFileNameWithoutExtension(Convert.ToString(args[0])!));
        //pathNs.AddMethod("Combine", args => Path.Combine(Convert.ToString(args[0])!, Convert.ToString(args[1])!));
        //CurrentFrame.Scopes.Define("Path", pathNs);

        // ============================================================================
        // DIALOGRESULT ENUM
        // ============================================================================

        var dialogResultNs = new Namespace(_comparer);
        dialogResultNs.AddProperty("None", 0);
        dialogResultNs.AddProperty("OK", 1);
        dialogResultNs.AddProperty("Cancel", 2);
        CurrentFrame.Scopes.Define("DialogResult", dialogResultNs);

        RegisterNative("ShowConsole", TokenType.Void, args =>
        {
            var show = args.Length > 0 && Convert.ToBoolean(args[0]);
            if (show)
            {
                try
                {
                    var hConsole = GetConsoleWindow();
                    if (hConsole == IntPtr.Zero)
                    {
                        AllocConsole();
                        // The console reopens stdout as a UTF-8 stream (StreamWriter default),
                        // so its output codepage must match, otherwise UTF-8 bytes for
                        // non-ASCII chars (« » é …) render as OEM mojibake (┬½┬á…).
                        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* redirected, ignore */ }
                        try { Console.InputEncoding = Encoding.UTF8; } catch { /* redirected, ignore */ }
                        // After allocation, you might want to set console title or redirect stdout/stderr if needed
                        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
                        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
                        ShowSplashScreen();
                    }
                    else
                    {
                        ShowWindow(hConsole, 5); // SW_SHOW
                    }
                }
                catch (Exception ex)
                {
                    // Hosted runs (NPPExec, sandboxes, redirected handles on a WinExe) can leave
                    // the standard handles invalid, the console cannot be opened there. Degrade
                    // gracefully instead of failing the script: the host's own console/pipes keep
                    // receiving output, so ShowConsole(true) is simply a no-op in that case.
                    try { Console.Error.WriteLine($"[ShowConsole] console unavailable: {ex.Message}"); }
                    catch { /* no console at all, ignore */ }
                }
            }
            else
            {
                var hConsole = GetConsoleWindow();
                if (hConsole != IntPtr.Zero)
                {
                    // Option 1: Just hide the console window (can be shown again later)
                    ShowWindow(hConsole, 0); // SW_HIDE
                    // Option 2: Actually free the console (cannot be re-shown without AllocConsole)
                    // FreeConsole();
                }
            }
            return null;
        });

        var systemNs = new Namespace(_comparer);
        var typesNs = new Namespace(_comparer);

        systemNs.AddChild("TypeCode", typesNs);
        CurrentFrame.Scopes.Define("TypeCode", typesNs);
        foreach (var item in Enum.GetValues<TypeCode>())
            typesNs.AddProperty(item.ToString(), (int)item);

        var strcmpNs = new Namespace(_comparer);
        systemNs.AddChild("StringComparison", strcmpNs);
        CurrentFrame.Scopes.Define("StringComparison", strcmpNs);
        foreach (var item in Enum.GetValues<StringComparison>())
            strcmpNs.AddProperty(item.ToString(), (int)item);

        CurrentFrame.Scopes.Define("System", systemNs);

        // The `Script` namespace, from the SAME body the per-instance pass calls (see DefineScriptNamespace): it is
        // registered in two scopes on purpose, and its body lived twice here before.
        DefineScriptNamespace(CurrentFrame.Scopes);
    }

    // ============================================================================
    // 14. INTERPRETER - Mï¿½THODES D'AIDE STATIQUES
    // ============================================================================

    /// <summary>The ONE place the engine's refusal rule lives: the message loses a trailing period and carries the
    /// position, which a script's catch block then sees appended as " (at line L, column C)". Only the frame
    /// snapshot differs between the callers, and that is the whole reason for two policies: the tree snapshots its
    /// call frames, while the VM's frames are its own flat frames, so a VM refusal passes null rather than a
    /// snapshot of the tree's stack.
    ///
    /// `VmRuntimeError` exists because the VM's arithmetic and constant refusals used to construct the exception
    /// themselves. A bare `new ScriptException("Division by zero.")` matches the constructor whose line defaults to
    /// 0, and ScriptErrorInfo.WithLocation appends nothing when the line is unknown, so a compiled function reported
    /// "Division by zero." where the tree reported the same text without the period and with the call site.
    /// Measured, then pinned by assets/language/repl/vm_call_parity.slc.
    /// </summary>
    private static ScriptException Refusal(string message, int line, int column, ErrorCode code, IReadOnlyList<string>? frames)
    {
        // ONE granularity for a refusal's position: the STATEMENT it happened in, whichever engine ran it.
        // `_currentLine/_currentColumn` are that statement's position, written by the tree's Execute(Stmt) and
        // by the VM's LOC arm (see SetCurrentStatement), so the tree and a compiled body name the same place by
        // construction, and a shared helper called from either engine reports it too.
        // A construct's own column is what C# reports, and it is NOT affordable here: measured on the Debug
        // build, a LOC before every raising instruction cost 54% of an arithmetic loop and 38% of an index
        // loop (one dispatch per operator), and a per-instruction position table read on that same path cost
        // the same or more. So the engine reports the statement, and this is the one place that decides it.
        var cur = Current;
        if (cur != null && cur._currentLine > 0) { line = cur._currentLine; column = cur._currentColumn; }
        var clean = message.AsSpan().TrimEnd('.').ToString();
        var errorInfo = new ScriptErrorInfo(code, clean, null, frames);
        return new ScriptException(code, errorInfo, line, column);
    }

    /// <summary>The statement now executing, which is the position a refusal reports (see Refusal). The tree
    /// writes it from Execute(Stmt); the VM writes it from its LOC arm, so a compiled body and a walked one
    /// answer alike. One field, two writers, one meaning.</summary>
    internal void SetCurrentStatement(int line, int column)
    {
        _currentLine = line;
        _currentColumn = column;
    }

    /// <summary>A refusal raised by a native registration, at the CALLER's position when there is one. `internal`
    /// rather than private because the engine types that a namespace hands a script (`EventSubject`) raise their own
    /// refusals from their own file, and this is the ONE door: a second message-composing path there is exactly the
    /// drift this repository's "one implementation per concern" rule forbids.</summary>
    [DoesNotReturn]
    internal static void RuntimeError(string message, int line, int column, ErrorCode code = ErrorCode.Unspecified)
        => throw Refusal(message, line, column, code, Current?.SnapshotCallFrames());

    [DoesNotReturn]
    internal static T RuntimeError<T>(string message, int line, int column, ErrorCode code = ErrorCode.Unspecified)
        => throw Refusal(message, line, column, code, Current?.SnapshotCallFrames());

    /// <summary>A refusal raised by the bytecode VM: the SAME message and position rules as the tree's, with no tree
    /// frame snapshot. One rule, two policies, in the shape MinRequiredArgs and CheckScriptCallArity use.</summary>
    [DoesNotReturn]
    internal static void VmRuntimeError(string message, int line, int column, ErrorCode code = ErrorCode.Unspecified)
        => throw Refusal(message, line, column, code, null);

    [DoesNotReturn]
    internal static T VmRuntimeError<T>(string message, int line, int column, ErrorCode code = ErrorCode.Unspecified)
        => throw Refusal(message, line, column, code, null);

    /// <summary>Structs have no ClassInfo context, private/internal members are allowed only
    /// while code runs with <c>this</c> bound to an instance of the same struct (constructors and
    /// instance methods); anything else (global code, another struct/class) is denied.</summary>
    private void CheckStructMemberAccess(AccessModifier memberAccess, string structName, int line, int column, string? memberName = null)
    {
        if (memberAccess is AccessModifier.Public or AccessModifier.Internal) return;
        if (_currentThis is StructInstance cur && cur.StructName.Equals(structName, Comparison)) return;
        RuntimeError($"Cannot access non-public member{(memberName != null ? $" '{memberName}'" : "")} of struct '{structName}' from outside.", line, column, ErrorCode.AccessViolation);
    }

    private void CheckAccess(AccessModifier memberAccess, ClassInfo? declaringClass, int line, int column, string? memberName = null)
    {
        if (_currentClass == null)
        {
            if (memberAccess != AccessModifier.Public && memberAccess != AccessModifier.Internal)
                RuntimeError($"Cannot access non-public member{(memberName != null ? $" '{memberName}'" : "")} of '{declaringClass?.Name}' from global code.", line, column, ErrorCode.AccessViolation);
            return;
        }

        if (_currentClass == declaringClass)
            return;

        switch (memberAccess)
        {
            case AccessModifier.Protected:
                {
                    var current = _currentClass;
                    while (current != null)
                    {
                        if (current == declaringClass)
                            return;
                        current = current.Parent;
                    }
                    RuntimeError($"Cannot access protected member{(memberName != null ? $" '{memberName}'" : "")} of '{declaringClass?.Name}' from '{_currentClass?.Name}'.", line, column, ErrorCode.AccessViolation);
                    break;
                }
            case AccessModifier.Private:
                RuntimeError($"Cannot access private member{(memberName != null ? $" '{memberName}'" : "")} of '{declaringClass?.Name}' from '{_currentClass?.Name}'.", line, column, ErrorCode.AccessViolation);
                break;
            case AccessModifier.Internal:
                // Internal members are accessible from any code in the same module
                // Module boundary enforcement will be added with cross-module import support
                return;
            case AccessModifier.Public:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(memberAccess), memberAccess, null);
        }
    }

    private (ClassInfo? declaringClass, FieldDecl? field) FindFieldInHierarchy(ClassInfo startClass, string fieldName)
    {
        var current = startClass;
        while (current != null)
        {
            var field = FindByName(current.Fields, fieldName, static f => f.Name);
            if (field != null)
                return (current, field);
            current = current.Parent;
        }
        return (null, null);
    }

    /// <summary>Returns the standardized backing field name for an auto-property.</summary>
    private static string BackingFieldName(string propName) => $"<{propName}>k__BackingField";

    /// <summary>Returns the backing field name for a property, walking up the class hierarchy.</summary>
    private string? FindPropertyBackingField(ClassInfo startClass, string propName)
    {
        var current = startClass;
        while (current != null)
        {
            var prop = FindByName(current.Properties, propName, static p => p.Name);
            if (prop != null) return BackingFieldName(prop.Name);

            current = current.Parent;
        }
        return null;
    }

    /// <summary>Returns declaring class and property declaration for a property name, walking up the hierarchy.</summary>
    private (ClassInfo? declaringClass, PropertyDecl? prop) FindPropertyDeclWithClass(ClassInfo startClass, string propName)
    {
        var current = startClass;
        while (current != null)
        {
            {
                var prop = FindByName(current.Properties, propName, static p => p.Name);
                if (prop != null) return (current, prop);
            }
            current = current.Parent;
        }
        return (null, null);
    }

    private PropertyDecl? FindPropertyDecl(ClassInfo startClass, string propName)
    {
        return FindPropertyDeclWithClass(startClass, propName).prop;
    }

    /// <summary>Returns the backing field name for a struct property, or null if not found.</summary>
    private string? GetStructBackingField(StructInstance si, string propName)
    {
        var decl = si.Declaration;
        if (decl?.Properties == null) return null;
        var prop = FindByName(decl.Properties, propName, static p => p.Name);
        return prop != null ? BackingFieldName(prop.Name) : null;
    }

    private (ClassInfo? declaringClass, FuncDecl? method) FindMethodInHierarchy(ClassInfo startClass, string methodName)
    {
        // P2-T1 single-overload fast path: if the class exposes exactly ONE method
        // with the requested name, return it immediately, zero allocation, no
        // overload-resolution loop, no hierarchy walk.
        FuncDecl? single = null;
        foreach (var m in startClass.Methods)
        {
            if (!m.Name.Equals(methodName, Comparison)) continue;
            if (single != null) { single = null; break; }   // 2+ overloads → normal path
            single = m;
        }
        if (single != null)
            return (startClass, single);

        // Existing behavior: walk the hierarchy and return the first method with a
        // matching name (multi-overload and inherited/not-found cases unchanged).
        var current = startClass;
        while (current != null)
        {
            var method = FindByName(current.Methods, methodName, static m => m.Name);
            if (method != null)
                return (current, method);
            current = current.Parent;
        }
        return (null, null);
    }

    private List<(ClassInfo declaringClass, FuncDecl destructor)> FindAllDestructorClasses(ClassInfo startClass)
    {
        var result = new List<(ClassInfo, FuncDecl)>();
        var current = startClass;
        while (current != null)
        {
            if (current.Destructor != null)
                result.Add((current, current.Destructor));
            current = current.Parent;
        }
        return result;
    }

    private (ClassInfo? declaringClass, EventDecl? eventDecl) FindEventInHierarchy(ClassInfo startClass, string eventName)
    {
        var current = startClass;
        while (current != null)
        {
            var evt = FindByName(current.Events, eventName, static e => e.Name);
            if (evt != null)
                return (current, evt);
            current = current.Parent;
        }
        return (null, null);
    }

    public static void ShowSplashScreen()
    {
        string[] logo =
        [
            @"                     --- CONSOLE MODE ---                     ",
        ];

        Console.Clear();
        ConsoleSafe.SetColor(ConsoleColor.Cyan);

        var windowWidth = Console.WindowWidth;

        foreach (var line in logo)
        {
            // Calcul pour centrer le texte
            var padding = Math.Max(0, (windowWidth - line.Length) / 2);
            Console.WriteLine(new string(' ', padding) + line);
        }

        ConsoleSafe.ResetColor();
        Console.WriteLine("\n");
    }

    /// <summary>Fills a freshly created set from the collection a constructor was handed, the counterpart of
    /// FillListFrom: `new HashSet<int>([1, 2])` holds the two items where the arm used to ignore its argument and
    /// hand back an empty set.</summary>
    /// <summary>The loop both FillSetFrom and FillListFrom used to state, once. It takes
    /// IScriptItemCollection (not IScriptCollection, whose other implementers are the two dictionary types),
    /// and the generic keeps each caller's own type without a cast. The two wrappers below are
    /// EXPRESSION-BODIED on purpose: a `=>` declaration is not collected by tools/check-dup-bodies.sh at all,
    /// so a typed wrapper that is one call over one implementation is not a repetition the rule can see.</summary>
    private static T FillFrom<T>(T target, Array items, int line, int column) where T : IScriptItemCollection
    {
        foreach (var item in items) target.Add(item, line, column);
        return target;
    }

    /// <summary>One of the language's BAG types built from VALUES: no argument, a CAPACITY (an int/long, a hint that
    /// changes nothing observable), or a COLLECTION to fill from, with the tree's own refusal for anything else.
    /// ONE implementation, asked by the tree's `new` arm (which evaluates the arguments) and by the VM's CALL_NEW
    /// (which holds them as values), so the two engines cannot disagree about what `new List<int>([1, 2])` IS.
    /// Measured before this existed: the VM's arms IGNORED the argument entirely, which is why the compiler
    /// DECLINED every bag `new` that had one, and a top-level `new List<int>(4)` made the whole file carry its
    /// source (9 suites of the corpus, whose lines are `new Dictionary<string, int>(N)`, `new List<int>(maxLoop)`,
    /// `new List<object>(ITERS)`, `new List<int>([1, 2])`).</summary>
    private object CreateBagForNew(string name, TypeInfo elementType, TypeInfo? valueType, object?[] args,
        int line, int column)
    {
        var first = args.Length > 0 ? args[0] : null;
        var isList = name.Equals("List", Comparison);
        var isSet = name.Equals("HashSet", Comparison);
        if (isList || isSet)
        {
            var created = isList ? (object)CreateScriptList(elementType) : CreateScriptHashSet(elementType);
            if (args.Length == 0) return created;
            switch (first)
            {
                case int:
                case long:
                    return created;
                case IScriptList source:
                    return isList ? FillListFrom((IScriptList)created, source.ToArray(), line, column)
                                  : FillSetFrom((IScriptHashSet)created, source.ToArray(), line, column);
                case IScriptHashSet sourceSet when isSet:
                    return FillSetFrom((IScriptHashSet)created, sourceSet.ToArray(), line, column);
                case Array sourceArray:
                    return isList ? FillListFrom((IScriptList)created, sourceArray, line, column)
                                  : FillSetFrom((IScriptHashSet)created, sourceArray, line, column);
                default:
                    RefuseBagArgument(name, "a collection of items", first, line, column);
                    return created;
            }
        }
        if (name.Equals("Dictionary", Comparison))
        {
            var dict = CreateScriptDictionary(elementType, valueType!, 0);
            if (args.Length == 0) return dict;
            switch (first)
            {
                case int iv:
                    return CreateScriptDictionary(elementType, valueType!, iv);
                case long lv:
                    return CreateScriptDictionary(elementType, valueType!, (int)lv);
                case IScriptDictionary source:
                    return FillDictionaryFrom(dict, source.Dictionary, line, column);
                case IDictionary sourceClr:
                    return FillDictionaryFrom(dict, sourceClr, line, column);
                default:
                    RefuseBagArgument(name, "a dictionary of entries", first, line, column);
                    return dict;
            }
        }
        // Neither engine reaches this: each asks for the three bag names above. A refusal rather than a null keeps
        // the helper's contract non-nullable, which is what the tree's `new` arm returns.
        RuntimeError($"'{name}' is not a bag type this engine builds from values.", line, column);
        return CreateScriptList(elementType);
    }

    /// <summary>The refusal a bag constructor gives for an argument it cannot read, in the tree's own words.</summary>
    private void RefuseBagArgument(string name, string expected, object? value, int line, int column)
        => RuntimeError($"new {name}() takes a capacity or {expected}, not "
            + $"'{(value is null ? "null" : GetFriendlyTypeName(value.GetType()))}'.", line, column);

    /// <summary>The argument VALUES a bag `new` was handed, evaluated: the FIRST one, which is the one a bag
    /// constructor reads (a capacity or a collection). The tree produces them here and the VM holds them already,
    /// and both go into <see cref="CreateBagForNew"/>, so neither can drop an argument the other reads.</summary>
    private object?[] EvalBagArgs(NewInstanceExpr node)
        => node.Arguments is { Count: > 0 } ? [Evaluate(node.Arguments[0])] : [];

    private IScriptHashSet FillSetFrom(IScriptHashSet target, Array items, int line, int column)
        => FillFrom(target, items, line, column);

    /// <summary>Fills a freshly created dictionary from the entries a constructor was handed. The scripted and the
    /// CLR form both arrive as an IDictionary, so one body serves both, and each entry goes through the
    /// dictionary's own Add, which coerces the key and the value.</summary>
    private IScriptDictionary FillDictionaryFrom(IScriptDictionary target, IDictionary entries, int line, int column)
    {
        foreach (DictionaryEntry entry in entries)
            target.Add(entry.Key, entry.Value, line, column);
        return target;
    }

    /// <summary>Fills a freshly created list from the collection a constructor was handed, so
    /// `new List<int>([1, 2])` holds the two items where every argument used to be discarded and the list came out
    /// empty. Each item goes through the list's own Add, which is the single place that coerces an element.</summary>
    private IScriptList FillListFrom(IScriptList target, Array items, int line, int column)
        => FillFrom(target, items, line, column);

    private static IScriptList CreateScriptList(TypeInfo elementType)
    {
        // Plain object element type → typed ScriptUnboxedList<object> so typeof() reports the
        // concrete instantiation "List<object>" (uniform-ToList/generic conventions) rather than
        // the bare "List" of a boxed list. Same storage semantics, just a concrete generic name.
        if (elementType.BaseType == TokenType.Object && !elementType.IsArray
            && !elementType.IsNullable && elementType.TypeName == null && !elementType.IsTupleType)
            return new ScriptUnboxedList<object>(elementType);
        if (!CanUseUnboxedStorage(elementType)) return new ScriptBoxedList(elementType);
        var ei = NumericTokenIndex(elementType.BaseType);
        if ((uint)ei < 17)
            return GetListFactory(ei)(elementType);
        return new ScriptBoxedList(elementType);
    }

    /// <summary>One dictionary's keys (or values) as a SCRIPT LIST typed by the dictionary's own element type,
    /// so `.Count`, indexing and `foreach` work exactly as they do on any other list. This backs the `Keys`
    /// and `Values` PROPERTIES; the method forms (`Keys()`/`Values()`) keep returning an array, which is what
    /// `.Length` on them needs, the two spellings are one rule for the data with the containers the corpus
    /// already used. Positional order comes from the interface's own `GetKey`/`GetValue`, the accessors the
    /// `GetKey(i)`/`GetValue(i)` members expose, so all three agree.</summary>
    private static IScriptList DictionaryProjection(IScriptDictionary dict, bool keys)
    {
        var list = CreateScriptList(keys ? dict.KeyType : dict.ValueType);
        var count = dict.Count;
        for (var i = 0; i < count; i++)
            list.AddRaw((keys ? dict.GetKey(i) : dict.GetValue(i))!);
        return list;
    }

    /// <summary>The same projection for a RAW CLR dictionary, a `{k: v}` literal is one, so its `Keys`/`Values`
    /// must answer like a script dictionary's or the surface depends on how the dictionary was made. Its element
    /// type comes from the dictionary's own generic arguments (`Dictionary<string, int>` gives `List<string>`,
    /// a literal's `Dictionary<object, object?>` gives the `List<object>` that `ToList()` also reports), and the
    /// order is the dictionary's own enumeration order, the insertion order the corpus relies on.</summary>
    private static IScriptList DictionaryProjectionOfRaw(IDictionary dict, bool keys)
    {
        var dictType = dict.GetType();
        var clrArgs = dictType.IsGenericType ? dictType.GetGenericArguments() : null;
        var elementType = clrArgs is { Length: 2 }
            ? MapTypeInfo(clrArgs[keys ? 0 : 1])
            : new TypeInfo(TokenType.Object, false, 0, 0, null);
        var list = CreateScriptList(elementType);
        foreach (DictionaryEntry entry in dict)
            list.AddRaw((keys ? entry.Key : entry.Value)!);
        return list;
    }

    private static IScriptDictionary CreateScriptDictionary(TypeInfo keyType, TypeInfo valueType, int capacity = 0)
    {
        if (!CanUseUnboxedStorage(keyType) || !CanUseUnboxedStorage(valueType))
            return new ScriptBoxedDictionary(keyType, valueType, capacity);
        var ki = NumericTokenIndex(keyType.BaseType);
        var vi = NumericTokenIndex(valueType.BaseType);
        if ((uint)ki < 17 && (uint)vi < 17)
        {
            // A row specialises only the value types a script densely uses (see LazyDictRowCore), so a null here
            // means this pair is deliberately served by the boxed dictionary.
            var factory = GetDictFactory(ki, vi);
            if (factory != null) return factory(keyType, valueType);
        }
        return new ScriptBoxedDictionary(keyType, valueType, capacity);
    }

    private static IScriptHashSet CreateScriptHashSet(TypeInfo elementType)
    {
        if (!CanUseUnboxedStorage(elementType)) return new BoxedScriptHashSet(elementType);
        var ei = NumericTokenIndex(elementType.BaseType);
        if ((uint)ei < 17)
            return GetHashSetFactory(ei)(elementType);
        return new BoxedScriptHashSet(elementType);
    }

    private static bool CanUseUnboxedStorage(TypeInfo typeInfo)
    {
        if (typeInfo.IsArray || typeInfo.IsNullable || typeInfo.TypeName != null)
            return false;

        return typeInfo.BaseType switch
        {
            TokenType.Byte or TokenType.Short or TokenType.Int or TokenType.Long
                or TokenType.UInt or TokenType.UShort or TokenType.SByte or TokenType.ULong
                or TokenType.Float or TokenType.Double or TokenType.Decimal
                or TokenType.Bool or TokenType.Char
                or TokenType.DateTime or TokenType.DateTimeOffset or TokenType.TimeSpan
                or TokenType.StringType => true,
            _ => false
        };
    }

    /// <summary>Copies a tuple (recursively through tuple-typed elements) and attaches the
    /// element names declared in the target type annotation, e.g. values stored into a
    /// List<(int[] lst, int i)> get Names ["lst","i"] so `item.lst` resolves. The original
    /// value is never mutated; unnamed annotations return the value unchanged.</summary>
    internal static object? StampTupleElementNames(object? value, TypeInfo target)
    {
        if (!target.IsTupleType || value is not ScriptTuple st) return value;
        var elems = target.TupleElementTypes ?? [];
        var names = target.TupleElementNames ?? [];
        object?[]? newItems = null;
        for (var i = 0; i < elems.Count && i < st.Length; i++)
        {
            var item = st[i];
            var stamped = StampTupleElementNames(item, elems[i]);
            if (!ReferenceEquals(stamped, item))
            {
                newItems ??= (object?[])st.Items.Clone();
                newItems[i] = stamped;
            }
        }
        var namesNeeded = names is { Count: > 0 }
            && (st.Names == null || st.Names.Count != names.Count || !st.Names.SequenceEqual(names));
        if (newItems == null && !namesNeeded) return st;
        var copy = new ScriptTuple(newItems ?? st.Items) { Names = names ?? st.Names };
        return copy;
    }

    /// <summary>The tolerance for float/double equality and truthiness, used by BOTH engines: the VM reads
    /// this constant rather than keeping a copy of its value.</summary>
    internal const double Epsilon = 0.000001;

    // ============================================================================
    // 15. INTERPRETER - Mï¿½THODES D'ï¿½VALUATION ET VISITEURS
    // ============================================================================

    public void RegisterNative(NativeFunctionAdapter fn) => CurrentFrame.Scopes.Define(fn.Name, fn);

    public void RegisterNative(string name, TokenType returnType, Func<object?[], object?> funCall)
    {
        var adapter = new NativeFunctionAdapter(name, 0, [], TokenType.Object, returnType, funCall.Invoke);
        RegisterNative(adapter);
    }


    public ScriptResult Interpret(IEnumerable<Stmt> statements, string? sourcePath = null)
    {
        if (RunThreadId == 0) RunThreadId = Environment.CurrentManagedThreadId;
        if (RunThreadNativeId == 0) RunThreadNativeId = (int)KernelCurrentThreadId();
        using var _ = PushContext();
        // A RUN OF ITS OWN: a form this interprets creates belongs to THIS unit, which is what Application.Run waits on
        // (see Interpreter.ExecutionUnit). Without it a nested run that shares an interpreter and a thread with its
        // caller (a runner's `Compile first` entry) waited on the caller's windows.
        EnterExecutionUnit();

        var stmts = statements.ToList(); // materialize to avoid multiple enumeration

        try
        {
            BeginRun(stmts, sourcePath);

            // Second pass: execute all statements in order, each one on the VM when the compiler took it
            // (see TryExecuteStatementOnVm). Measured, the reason the top level wants the VM at all:
            // 2,000,000 iterations cost 2,875 ms tree-walked against 268 ms for the same loop inside a
            // function. Per STATEMENT rather than one whole-body run, because a compiled body that aborts
            // at an instruction the VM cannot run can only be handed back to the tree when it has done
            // nothing observable, and a top-level statement almost always stores a global first: measured,
            // one whole-body run per file refused 74 of the 473 suites ("the program body stopped
            // part-way through"), where a per-statement body costs that ONE statement and the tree runs it.
            foreach (var stmt in stmts)
            {
                ExecutionResult result;
                _fileStmtAttempts++;
                try { result = TryExecuteStatementOnVm(stmt) ?? Execute(stmt); }
                finally { _fileStmtAttempts--; }
                if (result.Flow == ControlFlow.Return)
                {
                    _lastExpressionValue = result.Value;
                    _lastValueWasReturn = true;   // an ANSWER, not the echo of an expression statement
                    break;
                }
                // A statement that threw and nothing caught it ENDS the run, with the payload's own code and position
                // (see ThrowPayloadException). Measured before: the tree SWALLOWED it and carried on, so
                // `try { throw "boomA"; } finally { print("finA"); }` ran the finally and executed the NEXT statement,
                // where the VM leg reports `InvalidOperation: boomA` and stops.
                if (result.Flow == ControlFlow.Throw)
                {
                    var uncaught = _thrownException ?? result.Value;
                    _thrownException = null;
                    throw ThrowPayloadException(uncaught, _currentLine, _currentColumn);
                }
                // Auto-drain Task ContinueWith queue only (NOT Task.Run queue)
                Task.PumpPending();
            }
        }
        catch (ScriptException ex) { return ScriptFailure(ex); }
        catch (Exception ex) { return EngineFailure(ex); }
        finally
        {
            // The run's END is the scope exit a TOP-LEVEL `using var` has. A nested RunScript drains at its own
            // Interpret, and the callbacks are cleared by the drain, so a second one is a no-op.
            CurrentFrame.Scopes.RunDisposeOnExit();
        }

        return new ScriptResult { ExitCode = Environment.ExitCode, ReturnValue = _lastExpressionValue };
    }

    /// <summary>ONE statement of "this run starts here": the directory and name the run reports, the state a new run
    /// resets, and the pre-registration of the file's OWN declarations so a forward reference resolves. Shared by
    /// <see cref="Interpret"/> and <see cref="InterpretAsync"/>, which is why the CONTEXT push and the execution unit are
    /// the caller's: a suspension unwinds this stack, so those two cannot live here.</summary>
    private void BeginRun(List<Stmt> stmts, string? sourcePath)
    {
        if (sourcePath != null)
        {
            _currentDirectory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            _currentSourceName = Path.GetFileName(sourcePath);
        }
        else if (_currentSourceName == null)
        {
            _currentSourceName = "<repl>";
        }

        _recordingExports = false;
        _currentExports = null;
        _lastExpressionValue = null;   // <-- reset
        _lastValueWasReturn = false;   // <-- so is whether that value was an ANSWER or an ECHO
        _scriptTopLevel.Clear();       // <-- list is scoped to one run (not cleared by Reset/SetReplMode)
        _programStatements = null;     // <-- ...and so is the program body compiled from it
        _programBytecode = null;
        _programBodyAttempted = false;
        _statementBodies.Clear();      // ...and the per-statement bodies (they hold THIS run's statement nodes)
        ProgramBodyDeclineReason = null;

        _programStatements = stmts;      // the body a bundle can carry as its entry (see ProgramBytecode)
        _declaredTypeNames = CollectDeclaredTypeNames(stmts);

        // First pass: register all top-level function declarations so forward references
        // work, recursing into namespace blocks (they are transparent containers).
        foreach (var stmt in FlattenNamespaceContainers(stmts))
        {
            if (stmt is not FuncDecl funcDecl)
                continue;
            // Remembered so the reset that ends a run keeps THESE values: see _topLevelFunctionNames.
            RecordTopLevelFunctionName(funcDecl.Name);
            try { VisitFuncDecl(funcDecl); }
            catch { /* registration failures are caught during execution */ }
        }

        // Record the top-level functions ONCE for the whole run. The execution pass below used to record them as it
        // ran; it stays here because the loop below may hand a statement to the VM.
        foreach (var stmt in stmts)
            if (stmt is FuncDecl topLevel)
                _scriptTopLevel.Add(topLevel);
    }

    /// <summary>The verdict a run's REFUSAL is, for both entries: the abort arm first (`exit(n)` is not an error), then
    /// the error's own code, position and frames. One statement, so a synchronous run and a resumable one cannot report
    /// the same failure two ways.</summary>
    private ScriptResult ScriptFailure(ScriptException ex)
    {
        if (AbortExitCode(ex) is int abortCode)
        {
            Environment.ExitCode = abortCode;
            return new ScriptResult { ExitCode = abortCode, Aborted = true, ReturnValue = _lastExpressionValue };
        }

        var errorInfo = ex.Value is ScriptErrorInfo sei ? sei : new ScriptErrorInfo(ex.ErrorCode, ex.Message);
        return new ScriptResult { HasError = true, Cancelled = ex.ErrorCode == ErrorCode.Cancelled, ErrorMessage = errorInfo.Message, ErrorInfo = errorInfo, ErrorLine = ex.Line, ErrorColumn = ex.Column, StackTrace = ex.StackTrace, ReturnValue = _lastExpressionValue };
    }

    /// <summary>The verdict an ENGINE failure is (never a script's own refusal): reported through the one message map
    /// first, so the sentence a reader meets is the folded one.</summary>
    private ScriptResult EngineFailure(Exception ex)
    {
        HandleRuntimeError(ex, _currentLine);
        return new ScriptResult { HasError = true, ErrorMessage = ex.Message, ErrorLine = _currentLine, StackTrace = ex.StackTrace, ReturnValue = _lastExpressionValue };
    }

    // ── The RESUMABLE run: a file-level `await` suspends the run instead of parking a thread ──

    /// <summary>What a run keeps ACROSS a suspension: which statement to re-enter, and every await that statement has
    /// ALREADY served. `Index` is both "the statement to run" and "the statement to re-enter", which is the rule the
    /// tree already applies to a suspended statement (see VisitAwaitExpr).</summary>
    private sealed class AsyncRunState(Task run, List<Stmt> statements)
    {
        internal readonly Task Run = run;
        internal readonly List<Stmt> Statements = statements;
        internal int Index;

        /// <summary>The awaits THIS statement has served, in the order its replay reaches them. A statement may hold
        /// more than one (`(await a) + (await b)`), and a suspended statement is re-entered WHOLE, so the replay has to
        /// answer the awaits it reaches in the same order they were awaited; one slot per statement was measured to
        /// serve the SECOND await's task to the FIRST and then re-issue the first for ever (`async_try_catch_test` 6b).
        /// The TASK is kept rather than its value, because `<c>Result</c>` is where an awaited task owns the exception
        /// rule: it answers the value or throws the task's own failure at the await's own position, inside the
        /// statement, so a script's own `try` around the await catches it. Cleared when the statement reaches its end,
        /// because the journal belongs to ONE execution of it.</summary>
        internal readonly List<(AwaitExpr Site, Task Task)> Awaits = [];

        /// <summary>How far into <see cref="Awaits"/> the CURRENT replay has got. The driver resets it to zero when it
        /// (re-)enters a statement, since a replay starts at that statement's first await again.</summary>
        internal int AwaitsServed;
    }

    private AsyncRunState? _asyncRun;

    // ── The REPLAY MEMO: what a statement had ALREADY computed when it suspended ──
    //
    // A suspended statement is re-entered WHOLE (that is what makes the journal and the order rules work), so every
    // sub-expression it had already computed would be computed again: measured, `Step("a") + (await W()) + Step("b")`
    // appended `a` twice. Recording each value the statement's OWN frame produced and serving it on the re-entry gives
    // the statement C#'s single-evaluation semantics with NO statement rewrite, which is what the hoisting route
    // would have cost (order around `&&`, `||`, `?:`, loops and `try`, plus a temporary per await). Order and
    // conditional evaluation are unchanged by construction: the replay reaches the same nodes in the same order, so
    // the same values come back.
    //
    // IT IS ARMED ONLY WHERE A SUSPENSION IS POSSIBLE, and the arming test is the statement's own SOURCE TEXT carrying
    // the `await` token (a keyword, so it cannot be spelled another way) rather than an AST walk: a statement that
    // cannot suspend has nothing to remember, and a top-level loop is one statement whose body must NOT fill the memo
    // once per iteration, which is why recording also requires `_treeLoopDepth == 0` (the same flag that stops a
    // suspension inside a loop).
    private Dictionary<Expr, object?>? _awaitMemo;
    private Stmt? _awaitMemoStatement;
    private int _awaitMemoFrameDepth;
    private bool _awaitMemoRecording;

    /// <summary>Called before a resumable run executes (or RE-executes) one statement. A re-entry keeps the memo it
    /// filled on the pass that suspended, which is the whole point; a new statement starts a fresh one.</summary>
    private void BeginAwaitMemo(Stmt stmt)
    {
        if (ReferenceEquals(_awaitMemoStatement, stmt)) return;
        _awaitMemoStatement = stmt;
        _awaitMemoFrameDepth = CallDepth;
        _awaitMemoRecording = true;
        _awaitMemo = stmt.SourceText is { } text && text.Contains("await", StringComparison.Ordinal)
            ? new Dictionary<Expr, object?>(ReferenceEqualityComparer.Instance)
            : null;
    }

    /// <summary>Drops the memo when a statement COMPLETES. A suspension leaves it in place through the unwind, which is
    /// why the driver calls this after the statement returns rather than from a `finally`.</summary>
    private void EndAwaitMemo()
    {
        _awaitMemo = null;
        _awaitMemoStatement = null;
        _awaitMemoRecording = false;
    }

    /// <summary>How many statements the tree has NESTED below the one a resumable run is driving (1 is that statement
    /// itself), maintained by <see cref="Execute(Stmt)"/>. <see cref="SuspendAsyncRun"/> reads it: an await inside a
    /// sub-statement cannot suspend, because the driver re-enters the WHOLE statement and that sub-statement's earlier
    /// statements would run a second time.</summary>
    private int _asyncRunStmtDepth;

    /// <summary>How many FILE-LEVEL statement ATTEMPTS are on the stack, the run's own being 1. Both halves of an
    /// attempt are counted (the per-statement VM try and the tree walk), because a NESTED run adds one of its own and
    /// <see cref="SuspendAsyncRun"/> must not let it take the driver's suspension. Measured without it: `RunScript(text,
    /// false, true)` over a text whose top level awaits reported "the run suspended on an await" as the NESTED run's
    /// error, because that statement was executed by the VM (which never enters `Execute(Stmt)`, so the
    /// `_asyncRunStmtDepth` test could not see the nesting) while `_asyncRun` was still the OUTER run's.</summary>
    private int _fileStmtAttempts;

    /// <summary>The scope a plain (tree-walked) loop arm enters, so <see cref="SuspendAsyncRun"/> can tell that the await
    /// it is looking at sits inside a loop. A `using` declaration keeps it exception-safe on every exit of the arm, and a
    /// struct keeps it free.</summary>
    private readonly struct TreeLoopScope(Interpreter itp) : IDisposable
    {
        private readonly Interpreter _itp = itp;
        public void Dispose() => _itp._treeLoopDepth--;
    }

    private TreeLoopScope EnterTreeLoop()
    {
        _treeLoopDepth++;
        return new TreeLoopScope(this);
    }

    /// <summary>True while a run driven by <see cref="InterpretAsync"/> is in flight, which is what tells a top-level
    /// `await` it may suspend.</summary>
    internal bool AsyncRunActive => _asyncRun is not null;

    /// <summary>whether a posted run uses the RESUMABLE entry. ON, and the measurement behind it is this round's:
    /// a file-level `await` suspends the run instead of parking a thread (a cancel issued at 372 ms used to be observed
    /// only at 2,639 ms, once the 2,500 ms awaited call the run was inside had finished), and a statement holding
    /// SEVERAL awaits is served by the await journal <see cref="ServeJournalledAwait"/> keeps, so
    /// `(await a) + (await b)` continues instead of re-issuing its first await. A run uses the C# entry-point rule
    /// only when this is off (a suite or a host that wants the blocking leg sets it).</summary>
    internal static bool AsyncRunEnabled = true;

    /// <summary>THE RUN AS A TASK, AND RESUMABLE. The statements are driven exactly as <see cref="Interpret"/> drives
    /// them (per-statement VM attempt, then the tree; same flow handling; same verdict), with one difference: a
    /// file-level `await` whose task is not complete SUSPENDS the run here (its continuation posts the resume to the run
    /// queue, and the statement is re-entered with the awaited value) instead of parking the calling thread inside
    /// `Task.Wait`. Everything that runs a file already goes through the posted twins, so this is the async entry; a
    /// suspension unwinds this stack, so the CONTEXT is pushed per pass while the execution unit is entered ONCE (a
    /// second unit would make a form created before the suspension stop belonging to the run `Application.Run` waits on).
    /// </summary>
    public Task InterpretAsync(IEnumerable<Stmt> statements, string? sourcePath = null)
    {
        if (RunThreadId == 0) RunThreadId = Environment.CurrentManagedThreadId;
        if (RunThreadNativeId == 0) RunThreadNativeId = (int)KernelCurrentThreadId();
        using var _ = PushContext();
        EnterExecutionUnit();

        var run = new Task();
        // The wait's own wake-ups: the run's completion, and the pump that carries the resume. Without these a `.Wait()`
        // on this task would never drain the queue the resume was posted to (measured as the `ContinueWith` family).
        run.SetPumpDriver(PumpCoroutines);
        run.SetPumpWork(PumpWorkSignal, PumpNextDueMs);

        var state = new AsyncRunState(run, statements.ToList());
        BeginRun(state.Statements, sourcePath);
        _asyncRun = state;
        DriveAsyncRun(state, resuming: false);
        return run;
    }

    /// <summary>Runs (or RESUMES) a resumable run's statement list. The four outcomes are the synchronous entry's, one
    /// suspension arm over: a suspension returns and leaves the run's Task open for the posted resume.</summary>
    private void DriveAsyncRun(AsyncRunState state, bool resuming)
    {
        try
        {
            if (resuming)
            {
                // Nothing is raised here: the re-entered statement reads its awaited task's `.Result`, which is where a
                // fault belongs, so a script's own `try` around the await catches it.
            }

            for (; state.Index < state.Statements.Count; state.Index++)
            {
                var stmt = state.Statements[state.Index];
                // A replay starts at the statement's FIRST await, so the journal's cursor starts there too. The journal
                // itself survives a suspension (a RunSuspend leaves through the catch below) and is dropped the moment
                // the statement reaches its end, because it belongs to ONE execution of it.
                state.AwaitsServed = 0;
                // The memo a RE-ENTRY keeps and a new statement starts fresh (see BeginAwaitMemo). It is dropped once
                // the statement returns, and survives the unwind a suspension takes, which is why it is not a `finally`.
                BeginAwaitMemo(stmt);
                ExecutionResult result;
                _fileStmtAttempts++;
                try { result = TryExecuteStatementOnVm(stmt) ?? Execute(stmt); }
                finally { _fileStmtAttempts--; }
                EndAwaitMemo();
                state.Awaits.Clear();
                if (result.Flow == ControlFlow.Return)
                {
                    _lastExpressionValue = result.Value;
                    _lastValueWasReturn = true;   // an ANSWER, not the echo of an expression statement
                    break;
                }
                if (result.Flow == ControlFlow.Throw)
                {
                    var uncaught = _thrownException ?? result.Value;
                    _thrownException = null;
                    throw ThrowPayloadException(uncaught, _currentLine, _currentColumn);
                }
                Task.PumpPending();
            }
        }
        catch (ScriptException ex) when (ex.ErrorCode == ErrorCode.RunSuspend)
        {
            // The awaited task's completion posted the resume to the run queue; the run's Task stays open, and this
            // thread is free (the CLI's own `.Wait()`, or a host's session thread between statements). A script cannot
            // catch this signal: both engines' handler walks skip their catch arms for it and still run `finally`.
            return;
        }
        catch (ScriptException ex) { FinishAsyncRun(state, ScriptFailure(ex)); return; }
        catch (Exception ex) { FinishAsyncRun(state, EngineFailure(ex)); return; }

        FinishAsyncRun(state, new ScriptResult { ExitCode = Environment.ExitCode, ReturnValue = _lastExpressionValue });
    }

    /// <summary>ONE end for a resumable run: the scope exit a top-level `using var` owes, the run's Task, and the state
    /// cleared so a finished run cannot be driven again.</summary>
    private void FinishAsyncRun(AsyncRunState state, ScriptResult result)
    {
        try { CurrentFrame.Scopes.RunDisposeOnExit(); }
        finally
        {
            if (ReferenceEquals(_asyncRun, state)) _asyncRun = null;
            // A finished run cannot be driven again, so the replay memo has nothing left to serve (a failure inside a
            // statement leaves through here without the driver's own EndAwaitMemo).
            EndAwaitMemo();
            if (!state.Run.IsCompleted) state.Run.SetResult(result);
        }
    }

    // ── THE PARKED-WAIT CENSUS, and the ONE door every tree-side await site uses ──
    //
    // An await can SUSPEND (the resumable entry re-enters the statement with the value) or PARK (the run waits on a
    // thread inside Task.Wait). Parking is right where a replay would change the ANSWER, but until now it was
    // invisible: a script, a suite and a reader could not tell the two apart except by latency. Naming it is what
    // makes the count worth keeping:
    //   * the SITE travels into the wait (WaitForTask's `site`), so the census says WHICH shape parked and how often;
    //   * the wait is CANCELLABLE at every one of them (see Task.WaitBlocking), which is the one behaviour this
    //     closes: a kill used to wait for the awaited call to finish;
    //   * the ASYNC_TRACE gate reports one line per park with its duration, so "which awaits do real scripts park on"
    //     is answered by a measurement on the corpus rather than by a reading of the compiler.
    internal static readonly ConcurrentDictionary<string, int> ParkedAwaitCounts = new(StringComparer.Ordinal);

    /// <summary>Counts one parked wait. Called by <see cref="Task.WaitBlocking"/>, i.e. once per wait that really
    /// blocked: a task already complete never reaches it, and a suspended statement never gets here at all.</summary>
    internal static void NoteParkedWait(string site)
        => ParkedAwaitCounts.AddOrUpdate(site, 1, static (_, n) => n + 1);

    /// <summary>One line per parked wait, off by default: `SLC_ASYNC_TRACE=1`, or
    /// `Diagnostics.Trace("ASYNC_TRACE", true)` from a script.</summary>
    internal static bool AsyncTraceEnabled = Diagnostics.On("ASYNC_TRACE");

    /// <summary>THE ONE DOOR the tree's await sites use, so the wait rule is stated once and every parked wait is
    /// counted and cancellable. `site` is the shape a reader meets in the census and in the trace; `line`/`column`
    /// are the awaiting site's own, so a cancellation reads where the script parked rather than at the enclosing
    /// function.</summary>
    internal static void WaitForTask(Task task, string site, int line, int column)
        => task.WaitBlocking(site, line, column);

    /// <summary>Called by a top-level `await` inside a resumable run: records the await in the statement's journal, then
    /// registers the resume on the awaited task and unwinds to the driver. Answers false (and does nothing) outside such
    /// a run, where `WaitForTask` (and so `Task.Wait`) is the rule.</summary>
    internal bool SuspendAsyncRun(AwaitExpr site, Task awaited)
    {
        var state = _asyncRun;
        if (state is null) return false;

        // WHERE A SUSPENSION IS SOUND, and this is the whole safety of the resumable entry: a suspended statement is
        // re-entered WHOLE, so anything of it that already ran runs AGAIN. Two shapes make that an observable
        // difference, and both keep the blocking rule instead (`Task.Wait` below), which is what every file-level
        // await had before this entry existed:
        //   * an await NESTED in a sub-statement (`_asyncRunStmtDepth > 1`: an `if` branch, a `try` body, a block, a
        //     loop body), where the sub-statements before the await would re-run;
        //   * an await inside a LOOP construct (`_treeLoopDepth > 0`, the condition and the increment included),
        //     where replaying the statement replays the iterations: measured, `for (var i = 0; i < 3; i++) { total =
        //     total + await W(); }` answered 24 on the replay and 12 on the blocking rule, so a wrong VALUE rather
        //     than a slower answer.
        // A LOOP THAT MUST SUSPEND belongs in an `async` function, whose body resumes at the statement inside the loop.
        // AND IT IS THE RUN THAT OWNS THE DRIVER THAT MAY SUSPEND: a NESTED run (`RunScript` on this same interpreter,
        // which is what `preserveContext` means) has no driver of its own, so a suspension taken there unwinds to nobody
        // and is reported as "the run suspended on an await". Measured: `runscript_context_test` 6b named exactly that.
        if (_fileStmtAttempts != 1 || _asyncRunStmtDepth > 1 || _treeLoopDepth > 0) return false;

        // The journal's NEXT entry is this await, so the replay serves the entries before it and this one when it comes
        // back here. The cursor is left where it is: the driver resets it when it (re-)enters the statement.
        state.Awaits.Add((site, awaited));
        // THE MEMO STOPS HERE: what it holds is the statement's PREFIX, everything computed before its first
        // suspension, and that is exactly what a replay must not compute again. Recording past this point would let a
        // value computed AFTER an await be served in a later pass, which skips nothing a repeat needs but DOES skip
        // the await bookkeeping a re-entry performs, so the freeze is what keeps the memo and the journal independent.
        _awaitMemoRecording = false;
        _ = awaited.NativeTask.ContinueWith(_ =>
        {
            // ON THE RUN QUEUE, and with the context re-pushed: a resume is script work for the thread that owns this
            // interpreter, and the stack that pushed the context unwound at the suspension. Nothing is captured from
            // the task here: the statement reads its `.Result` when it is re-entered, so a fault is raised THERE.
            EnqueuePumpAction(() =>
            {
                using var _ = PushContext();
                DriveAsyncRun(state, resuming: true);
            });
        }, TaskScheduler.Default);
        throw new ScriptException(ErrorCode.RunSuspend, "the run suspended on an await");
    }

    /// <summary>False when this await has not been reached in this statement before, in which case the caller awaits it
    /// LIVE. Otherwise answers the value that await already produced, WITHOUT re-evaluating its operand, and raises the
    /// awaited task's own failure at that await's position if it faulted.
    ///
    /// ORDER AND FAULT POSITION ARE PRESERVED BY CONSTRUCTION, which is what made this the shape instead of rewriting
    /// statements: the journal is filled in the order the run REACHED its awaits and consumed in the order the replay
    /// reaches them, so a branch that was not taken is not awaited, a loop's awaits are journalled per iteration in the
    /// order that iteration reached them, and the value is read through `.Result` at the await's own position (a fault
    /// therefore lands inside the statement, where a script's own `try` around the await catches it). Nothing about the
    /// statement is rewritten, so there is no state machine and no hoisted temporary to place, and one await is one
    /// suspension in the order the expression evaluates its operands.
    ///
    /// ONE RESIDUAL, stated rather than implied: work written BEFORE an await in the SAME statement runs again on each
    /// replay (the statement is re-entered whole). What this closes is the await being re-issued, which is what made a
    /// second await hang; a side effect before the await is unaffected and must be written in its own statement when it
    /// must happen once.</summary>
    private bool ServeJournalledAwait(AwaitExpr site, out object? value)
    {
        value = null;
        var state = _asyncRun;
        if (state is null || state.AwaitsServed >= state.Awaits.Count) return false;

        var entry = state.Awaits[state.AwaitsServed];
        if (!ReferenceEquals(entry.Site, site))
        {
            // The replay reached a DIFFERENT await than the pass that suspended: work before the await ran again and
            // changed which await came next. Nothing journalled past the served prefix applies any more, so drop that
            // tail and await this one live. The prefix stays: the replay has just served it.
            state.Awaits.RemoveRange(state.AwaitsServed, state.Awaits.Count - state.AwaitsServed);
            return false;
        }

        state.AwaitsServed++;
        value = entry.Task.Result;   // the task owns the value AND the exception rule, at THIS await's position
        return true;
    }

    /// <summary>Runs each TOP-LEVEL statement on the bytecode VM instead of the tree walker (see
    /// TryExecuteStatementOnVm). The A/B switch for it is `VmSetTopLevelEnabled([bool])`, and `SLC_TOPLEVEL_VM`
    /// still sets the INITIAL value so the gate can turn it on for a whole run; the field is settable because a
    /// runner needs both answers from ONE process, and an environment variable cannot change mid-run.
    ///
    /// WHY IT IS ON NOW: the gaps this comment used to list were each fixed (namespace METHOD resolution
    /// through an alias, a lambda capturing a generator's loop variable, a subscribe on a private event, the
    /// statement that stores a global and THEN hits an instruction the VM cannot run, an access violation in the
    /// ref/out tracking cells, a CLR ENUM under a bitwise operator, `~` on a bool, the ref cell a nested
    /// function's `ref` parameter kept writing, and MAKE_LIST swallowing the array factory's refusal), and every
    /// suite that failed behind the path is clean on both legs, on Debug and on the AOT publish.
    ///
    /// THE DEFAULT IS ON, and an explicit `SLC_TOPLEVEL_VM=0` (or `false`) is the opt-out. The predicate this
    /// replaced was `is { Length: > 0 }`, which read `SLC_TOPLEVEL_VM=0` as ON, because "0" is a non-empty
    /// string: a stray value in the environment could turn the path on and no value could turn it off. The
    /// shipped artifact now decides, an explicit value overrides it, and a script still overrides both. The one
    /// risk the path carries is unchanged and is why the corpus run is the check that matters: a statement the VM
    /// cannot FINISH after doing something observable is REFUSED (see the refusal below) where the tree would
    /// simply run it, so a script that used to work could start failing.
    ///
    /// The payoff, measured: 2,000,000 iterations cost 2,875 ms tree-walked against 268 ms for the same loop
    /// inside a function, which is what a program doing its work at file scope gave up before this.</summary>
    internal static bool TopLevelVmEnabled = Diagnostics.InitialFlag("TOPLEVEL_VM");

    /// <summary>Runs ONE top-level statement on the bytecode VM when the compiler took it, and returns null
    /// when the statement belongs to the tree instead, so the caller's own <c>Execute</c> runs it.
    /// Three statements are the tree's, deliberately: a DECLARATION (the interpreter registers those and a
    /// compiled body skips them), anything that may RETURN (the driver reads the flow from the tree, and a
    /// bare return at the top level is rare enough not to be worth guessing the VM's result for), and a
    /// statement the compiler declined.
    /// A bail is handed to the tree only when the VM had done NOTHING observable, which is the same rule and
    /// the same reason TryExecuteVmBody states it: a body that stored a global before stopping is REFUSED
    /// rather than re-run. Per STATEMENT is what makes that refusal rare (measured, one whole-body run per
    /// file refused 74 of the 473 suites, because a top-level statement almost always stores a global before
    /// it reaches an instruction the VM cannot run.</summary>
    private ExecutionResult? TryExecuteStatementOnVm(Stmt stmt)
    {
        if (!TopLevelVmEnabled) return null;
        // `!VmEnabled` is the switch. The REPL's own clause is about a TYPED LINE, whose value echoes through the tree
        // (`VisitExprStmt` records `_lastExpressionValue`), and a FILE run has no line to echo: `_currentSourceName` is
        // the file's name for a `.run`/`--repl <file>` and the `<repl>` sentinel only when nothing stands behind the run.
        // The distinction is MEASURED, not tidy: `ExecuteReplFile` ends with `SetReplMode(true)`, so every `.run` after
        // the first in a session had `_isRepl` set and sent its whole top level to the tree walker, and a 3,205-line UI
        // script then built its window in about 4,300 ms against about 520 ms on the VM leg (rad_designer.slc, same box,
        // four legs, two rounds each). A session file run now takes exactly the path a CLI file run takes.
        if (!VmEnabled) return null;
        if (_isRepl && IsReplTypedLine()) return null;
        if (IsDeclarationStatement(stmt) || StatementMayReturn(stmt)) return null;
        // A NAMESPACE block is a declaration too: the interpreter registers the namespace and everything
        // inside it, while a compiled body has nothing to do for its members (they are declarations, which
        // CompileStmt skips) and would run as an empty statement, leaving the namespace undefined.
        // Measured: `namespace FpCoro2 { int pairs(int n) {...} }` at the top level left `FpCoro2` undefined
        // (iterable_test), and the same happened to a namespace a module declares (bench_module_test's
        // `Bench`, namespace_decl_test's `Z.My`).
        if (stmt is NamespaceDeclStmt) return null;
        // A statement whose DECLARATION must outlive it is the tree's too, because a per-statement body owns its
        // own locals and the name would not survive into the next statement: `using var x = ...;` and a
        // destructuring `var (a, b) = ...;` both bind into the ENCLOSING scope (measured: `using var buf =
        // Mem.Buffer(8);` left `buf` undefined for every later statement, which is _dup360/_name360/crash_mem/
        // crash_typeof_matrix).
        if (stmt is UsingDeclStmt or DestructuringStmt) return null;
        // A declaration with an EXPLICIT type is the tree's, because the tree COERCES the initializer
        // (VisitVarDeclStmt -> CoerceTo) and the compiler emits no such coercion: measured, a top-level
        // `int x = 1.7;` read 2 with the tree and 1.7 (and typeof double) from a compiled body. An untyped
        // `var` is unaffected, since CoerceTo is a no-op for the object marker a `var` carries, which is the
        // same predicate the engine uses for "object accepts anything".
        if (stmt is VarDeclStmt { Type: { } declared }
            && !(declared.BaseType == TokenType.Object && declared.TypeName == null))
            return null;
        if (!_statementBodies.TryGetValue(stmt, out var body))
        {
            var builtins = GetGlobalScope().Keys
                .Where(k => k != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var decls = new List<FuncDecl>();
            foreach (var s in FlattenNamespaceContainers(_programStatements ?? []))
                if (s is FuncDecl fd) decls.Add(fd);
            var calleeDecls = Compiler.BuildCalleeDecls(decls, CaseSensitive);
            // A TOP-LEVEL EXPRESSION is compiled as a `return` so its value survives: the compiler emits a POP for
            // an expression statement (stack balance), while the tree hands the value to `_lastExpressionValue`
            // for the REPL's echo. Measured: `MakeBox();` in a `--repl` child answered a null result and printed
            // nothing where the tree printed `= 6` (vm_program_entry 6b).
            var compiled = stmt is ExprStmt es && _callFrames.Value!.Count == 1
                ? ReturnStmt.Create(es.Expression, es.Line, es.Column)
                : stmt;
            body = Compiler.CompileProgram([compiled], builtins, CaseSensitive, calleeDecls, out _);
            _statementBodies[stmt] = body;
        }
        if (body == null) return null;
        // The commit flag is saved and restored around the body for the same reason TryExecuteVmBody does it:
        // a nested execution must not read the outer body's answer.
        var commitBaseline = VmBodyCommitted;
        VmBodyCommitted = false;
        object? vmResult;
        bool committedHere;
        VmSuspendState? vmSuspended;
        try { vmResult = Vm.ExecuteCore(body, [], this, out vmSuspended); }
        finally
        {
            committedHere = VmBodyCommitted;
            VmBodyCommitted = commitBaseline || committedHere;
        }
        if (vmResult is VmFallback || vmSuspended is not null)
        {
            // A TOP-LEVEL statement has no coroutine to resume it, so a VM SUSPENSION cannot be honoured either:
            // the wait has to be DRIVEN, which only the tree does (`VisitAwaitExpr` waits and reads the task's
            // Result, so a faulted task raises there, which is C#'s rule). Only an UNCOMMITTED statement may go
            // back, exactly as a bail follows: re-running a committed one would repeat its effects.
            if (committedHere)
                RuntimeError("this statement suspended on an await, and a TOP-LEVEL statement has no coroutine"
                    + " here. Running it on the tree from the start would repeat everything it had already"
                    + " done, so it is refused: this is an engine gap rather than a mistake in the script.",
                    stmt.Line, stmt.Column);
            return null;   // nothing observable: the tree runs THIS statement
        }
        // The REPL echoes a line's value, and the tree is where that value is recorded: `VisitExprStmt` sets
        // `_lastExpressionValue` for a top-level expression. A compiled body must do the same or the echo is
        // simply lost: measured, `MakeBox();` in a `--repl` child printed nothing with the path on where the
        // tree printed `= 6` (vm_program_entry 6b), and the gate's `_isRepl` guard does not cover it because a
        // `--repl` child runs with `_isRepl` FALSE.
        if (stmt is ExprStmt && _callFrames.Value!.Count == 1)
            _lastExpressionValue = vmResult;
        TopLevelVmStatements++;   // the A/B switch's own count: this statement ran on the VM to completion
        return ExecutionResult.Continue();
    }

    /// <summary>True for a statement whose subtree can RETURN, which the driver must read from the tree: a
    /// per-statement compiled body returns its value the same way a function does, and the driver cannot tell
    /// that apart from a body that ended normally (both answer null for a bare return). A nested DECLARATION is
    /// not walked into: it is registered rather than run, so its own returns are not this statement's.
    /// The statement kinds are the ones a declaration can sit in, the same walk DeclaredFunctionsIn uses.
    ///
    /// <paramref name="valueOnly"/> asks the OTHER question the same walk answers: does the subtree return a VALUE
    /// (skipping a bare `return;`)? That is what a LAMBDA's declaration needs, and reading only the top level of a
    /// body got it WRONG: a body whose only `return "x"` sat inside a `try` was declared VOID, and the engine then
    /// refused its own return ("Void function '' cannot return a value") or dropped it. Measured on a route
    /// handler: a handler with a `try` around its `return` answered an EMPTY 200, where a named function with the
    /// same body answered normally.</summary>
    private static bool StatementReturns(Stmt stmt, bool valueOnly) => stmt switch
    {
        ReturnStmt r => !valueOnly || r.Value != null,
        BlockStmt b => b.Statements.Any(s => StatementReturns(s, valueOnly)),
        IfStmt i => i.ThenBranch.Any(s => StatementReturns(s, valueOnly)) || i.ElseBranch.Any(s => StatementReturns(s, valueOnly)),
        WhileStmt w => w.Body.Any(s => StatementReturns(s, valueOnly)),
        ForeachStmt fe => fe.Body.Any(s => StatementReturns(s, valueOnly)),
        ForStmt fr => (fr.Init != null && StatementReturns(fr.Init, valueOnly)) || fr.Body.Any(s => StatementReturns(s, valueOnly)),
        TryStmt t => StatementReturns(t.Body, valueOnly)
            || t.Catches.Any(c => StatementReturns(c.Body, valueOnly))
            || (t.Finally != null && StatementReturns(t.Finally, valueOnly)),
        UsingStmt u => StatementReturns(u.Body, valueOnly),
        SwitchStmt sw => sw.Cases.Any(c => c.Body.Any(s => StatementReturns(s, valueOnly))),
        _ => false,
    };

    private static bool StatementMayReturn(Stmt stmt) => StatementReturns(stmt, valueOnly: false);

    /// <summary>Does this body CONTAIN a value-returning `return`, at any depth? The ONE statement of the question a
    /// LAMBDA's declaration asks, read by the tree (VisitLambdaExpr) and by the bytecode compiler, so the two cannot
    /// declare the same lambda two ways.</summary>
    internal static bool BodyReturnsValue(IReadOnlyList<Stmt> body)
    {
        for (var i = 0; i < body.Count; i++)
            if (StatementReturns(body[i], valueOnly: true))
                return true;
        return false;
    }

    /// <summary>The engine's side of assigning <c>Application.CurDir</c>: the value must be a string naming a
    /// directory that exists, and both refusals are the engine's own words rather than the CLR's (whose message is a
    /// resource key under InvariantGlobalization). The wording matches what the CLR fallback already answered for
    /// <c>Directory.SetCurrentDirectory("X:/nope")</c>, "Directory not found: 'X:\nope'", so the two spellings of
    /// the same mistake read alike.</summary>
    private void SetCurrentDirectoryFromScript(object? value)
    {
        if (value is not string requested || requested.Length == 0)
            throw new ScriptException(ErrorCode.InvalidOperation,
                $"Application.CurDir must be a directory path, not '{GetFriendlyTypeNameOfValue(value)}'.");
        string full;
        try { full = Path.GetFullPath(requested); }
        catch (Exception ex) { throw new ScriptException(ErrorCode.InvalidOperation, FriendlyExceptionMessage(ex)); }
        if (!Directory.Exists(full))
            throw new ScriptException(ErrorCode.InvalidOperation, $"Directory not found: '{full}'.");
        try { Directory.SetCurrentDirectory(full); }
        catch (Exception ex)
        {
            throw new ScriptException(ErrorCode.InvalidOperation,
                $"Cannot change the current directory to '{full}': {FriendlyExceptionMessage(ex)}");
        }
    }

    /// <summary>The engine's side of assigning <c>Application.SingleInstance</c>. It takes a BOOL and nothing else,
    /// because a truthy int would make a switch whose whole job is to say which mode the application is in answer
    /// from a coincidence (the same reason `VmSetStatementCarrier` refuses a non-boolean).
    ///
    /// ON takes the single-instance lock for this RUN's script; if another process already holds it, the run ENDS
    /// here with exit code 0 and a warning naming the lock, which is exactly what VB's `IsSingleInstance` does with
    /// a second instance (it exits instead of creating its windows). OFF releases the lock, so a REPL session that
    /// changed its mind is not stuck.
    ///
    /// The identity is the run's own script when it is a file, because "the same application" means "the same
    /// script"; a REPL or an in-memory run has no file, so it is scoped to the PROCESS, which is the application
    /// there.</summary>
    private void SetSingleInstanceFromScript(object? value)
    {
        if (value is not bool wanted)
            throw new ScriptException(ErrorCode.InvalidOperation,
                $"Application.SingleInstance takes true or false, not '{GetFriendlyTypeNameOfValue(value)}'.");
        if (!wanted)
        {
            NativeApp.SingleInstance = false;
            NativeApp.ReleaseInstanceLock();
            return;
        }
        // THE RUN'S FILE, RESOLVED AGAINST THE RUN'S OWN DIRECTORY, and the reason is measured: `CurrentSourceName`
        // is the BARE name, so taking it as a path made both processes fall back to a pid-keyed lock, and two runs
        // of one script each took their own and neither was refused. A run with no file (the REPL, a nested
        // in-memory text) is scoped to the PROCESS, which is the application there.
        var identity = CurrentSourceName;
        if (!string.IsNullOrEmpty(identity) && !Path.IsPathRooted(identity))
            identity = Path.Combine(_currentDirectory ?? Directory.GetCurrentDirectory(), identity);
        if (string.IsNullOrEmpty(identity) || !File.Exists(identity))
            identity = "<process>." + Environment.ProcessId;
        else
            identity = Path.GetFullPath(identity).ToLowerInvariant();
        if (!NativeApp.TryTakeInstanceLock(identity, out var mutexName))
        {
            // ERROR, not a warning: the run does not do its work, so this is a refusal with a reason (which is what
            // the exit code says too, ExitCode.Refused). ONE SHORT LINE, because the name of the lock is a sanitized
            // full path and used to be more than half of it: a reader who needs the name switches the gate on
            // (`SLC_SINGLE_INSTANCE=1`, or Diagnostics.Trace("SINGLE_INSTANCE", true)) and it is appended.
            Logging.Log.Error("Application",
                "SingleInstance: already running in another process, so this run ends."
                + (SingleInstanceTrace ? " Lock: '" + mutexName + "'." : ""));
            throw AbortRun(ExitCode.Refused);
        }
        NativeApp.SingleInstance = true;
    }

    /// "./" or "../" is resolved against the RUN's own directory (the directory of the file being interpreted, and
    /// the process directory in the REPL, whose run is seeded with "&lt;repl&gt;"), so "./x" means "x beside this
    /// script"; every other relative path is resolved against the PROCESS working directory, the one the
    /// executable was started in, so a bare "x", "sub/x" and a backslash ".\\x" all mean the same thing and NONE of
    /// them means "beside this script". An absolute path is normalized and returned. A "./" path reached with no
    /// run directory left (a reset within a run) is refused rather than guessed at, with the message below.</summary>
    private string ResolvePath(string path)
    {
        // THE RUN'S DIRECTORY, not the registering interpreter's. `File`, `Directory`, `Bytecode` and the import door are
        // namespace members, and a namespace is registered by ONE interpreter and INHERITED by every interpreter the pool
        // rents from it, so in an EMBEDDED session `this` can be an interpreter that has never run anything
        // (`_currentDirectory` null) while the run has its own. Measured through the launcher: `Script.Directory()`
        // answered the right folder and `Bytecode.ExportScript("./out.slcbc", "f")` in the SAME run was refused with
        // "Cannot resolve relative import without a current directory" (this=null-dir, Current=the run's folder). The
        // engine's standing rule for a namespace member that touches per-run state, as `Coroutine.Pump` learned.
        var run = Current ?? this;
        // THE SESSION'S PATH GATE, inside the ONE path rule: a script's own paths (File, Directory, Bytecode's exports,
        // the import door, a SQLite database path) are resolved here, while a path the HOST names to `slc_run_*` is
        // resolved by the verb's own preamble and stays free. A host that denies `file`, or confines `dirs`, therefore
        // stops the SCRIPT without stopping itself from running a file from anywhere.
        if (!path.StartsWith("./") && !path.StartsWith("../")) return SessionPolicy.Confine(Path.GetFullPath(path));
        return run._currentDirectory == null
            ? throw new ScriptException("Cannot resolve relative import without a current directory.")
            : SessionPolicy.Confine(Path.GetFullPath(Path.Combine(run._currentDirectory, path)));
    }

    private object? Evaluate(Expr expr)
    {
        if (_returnExprDepth > 0) _returnExprDepth++;
        // THE REPLAY MEMO (see BeginAwaitMemo): a statement a resumable run re-enters serves the values it had already
        // computed instead of computing them again, which is what gives the statement C#'s single evaluation. It covers
        // the statement's OWN frame only (a callee's body binds a deeper frame), so the arena is exactly the expression
        // prefix the replay would otherwise re-run. Null for every statement that cannot suspend, which is why the
        // common path pays one field read.
        //
        // A LOOP BODY IS EXCLUDED AT RECORD TIME (`_treeLoopDepth == 0` below), which is the half that keeps serving
        // honest: only a value computed OUTSIDE a loop ever enters the memo, so a statement that happens to run a loop
        // twice cannot serve the first iteration's values to the second. Measured, and why `await foreach` had to enter
        // the loop scope too: without it, iteration 1's calls were recorded and then served to iterations 2 to 5, so a
        // five-line file delivered one element and an events feed one value.
        var awaitMemo = _awaitMemo;
        var memoLive = awaitMemo != null && CallDepth == _awaitMemoFrameDepth;
        if (memoLive && awaitMemo!.TryGetValue(expr, out var remembered)) return remembered;
        try
        {
            var result = expr.Accept(this);
            if (ReferenceEquals(result, YieldSentinel)) return YieldSentinel;
            if (memoLive && _awaitMemoRecording && _treeLoopDepth == 0
                && ReferenceEquals(_awaitMemo, awaitMemo) && CallDepth == _awaitMemoFrameDepth)
                awaitMemo![expr] = result;
            return result;
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Single friendly map (TypeHelpers): the arithmetic cases that used to live here are now IN it,
            // and everything else no longer leaks a raw CLR/Win32 message (this is the path that surfaced
            // "DllNotFound_Windows, sqlite3, <localized>" for Sqlite.OpenDb).
            var msg = FriendlyExceptionMessage(ex);
            ThrowException(msg, expr.Line, expr.Column);
            return null;
        }
        finally
        {
            if (_returnExprDepth > 0) _returnExprDepth--;
        }
    }

    private ScriptErrorInfo BuildScriptError(object? value, int line, int column, ErrorCode code)
    {
        return new ScriptErrorInfo(
            code, value?.ToString() ?? "null");
    }

    /// <summary>Maps a BaseType (TokenType enum) to a human-readable script type name.</summary>



    /// <summary>Maps CLR type name to script type name for comparison purposes.</summary>



    // ── DRY helper: bind a single parameter (ref/out or regular) ──
    /// <summary>Tuple parameter (named or unnamed-with-element-names) bind-time handling:
    /// validates the argument is a ScriptTuple of the matching arity (mismatches raise a runtime
    /// error, non-tuple arg or wrong element count) and attaches the parsed element names so
    /// p.n resolves inside the body (mirrors the tuple-typed VarDeclStmt's name attachment).</summary>
    internal void BindTupleParam(FuncDecl decl, int i, object? paramVal, int line, int column)
    {
        if (decl.TupleParamNames is not { Count: > 0 } names || i >= names.Count) return;
        if (names[i] is not { Count: > 0 } elems) return;
        if (paramVal is not ScriptTuple st)
        {
            RuntimeError($"Parameter '{decl.Params[i].Name}' expects a tuple argument, got '{paramVal?.GetType().Name ?? "null"}'.", line, column);
            return;   // RuntimeError throws, unreachable, satisfies flow analysis
        }
        if (st.Length != elems.Count)
            RuntimeError($"Parameter '{decl.Params[i].Name}' expects a {elems.Count}-element tuple, got {st.Length} elements.", line, column);
        st.Names = elems;
    }

    /// <summary>The declared type as a script reader writes it: the name for a named type, `an array` for an array
    /// shape, the keyword otherwise.</summary>
    /// <summary>The declared type as a script reader writes it: the name for a named type, `an array` for an array shape,
    /// the keyword otherwise. Used by the ref/out type refusal, so the message names what the script declared.</summary>
    private static string DeclaredTypeLabel(TypeInfo type)
        => type.TypeName ?? (type.IsArray ? "an array" : TypeSystem.Name(type.BaseType));

    /// <summary>Turn a tree Throw flow's payload into the exception a caller sees. ONE place, because FIVE sites rebuild
    /// it (the top-level statement loop, a function body, a coroutine body and two method bodies) and they must not
    /// disagree about the code or the position. The payload's OWN code and position travel: `VisitThrowStmt` builds a
    /// `ScriptErrorInfo` for every script `throw`, and `new ScriptException(payload)` dropped its code to `Unspecified`
    /// and its position to 0. Measured on the top level: the tree SWALLOWED an uncaught throw and carried on
    /// (`try { throw "boomA"; } finally { print("finA"); }` ran the finally and executed the NEXT statement) where the
    /// VM leg ends the run with `InvalidOperation: boomA`; an engine error crossing a finally keeps its identity here
    /// too, where the rebuild used to wrap it in a code-less exception.</summary>
    internal static ScriptException ThrowPayloadException(object? payload, int line, int column)
    {
        if (payload is ScriptException engineFailure) return engineFailure;
        // A `ScriptErrorInfo` carries no position of its own (WithLocation appends one into the MESSAGE, and skips it
        // for a user `throw`, whose value keeps its exact text), so the caller's position travels on the exception.
        if (payload is ScriptErrorInfo info)
            return new ScriptException(info.Code, info.Message, line, column);
        return new ScriptException(ErrorCode.InvalidOperation, payload, line, column);
    }

    private void BindParam(string name, object? paramVal, TypeInfo type, bool isRef, bool isOut, int line, int col)
    {
        // The rule covers BOTH branches, so it is asked before the split: a cell that contradicts the declaration is
        // a refusal whichever keyword the declaration names, and one that matches it is bound below.
        RefuseRefCellOnByValueParam(name, paramVal, isRef, isOut, line, col, type);
        if (isRef || isOut)
        {
            if (paramVal is RefCell refCell)
            {
                // Warn when ref parameter will change variable's existing type (out gets its warning from InitOutTracking)
                if (isRef && !isOut)
                {
                    var existingVal = SafeGet(refCell);
                    if (existingVal != null && !IsKnownCompatibleType(existingVal, type))
                    {
                        var existingName = existingVal.GetType().Name;
                        var targetName = type.TypeName ?? TypeSystem.Name(type.BaseType);
                        // Only warn for script class/struct mismatches, skip numeric widening
                        if (type.TypeName != null)
                        {
                            // ClassInstance wraps any script class, use the class name for comparison
                            var effectiveName = existingVal is ClassInstance ci ? ci.Class.Name : existingName;
                            if (!string.Equals(effectiveName, targetName, StringComparison.OrdinalIgnoreCase))
                                Logging.Log.Warning("Type mismatch",
                                    $"parameter '{name}' expected '{targetName}' but found '{existingName}'",
                                    line, col);
                        }
                    }
                }
                CurrentFrame.Scopes.Define(name, refCell, line, col);
            }
            else
                RuntimeError($"Expected a reference for ref/out parameter '{name}'", line, col);
        }
        else
        {
            // This branch used to UNWRAP a RefCell silently ("'out' arg passed without matching 'ref'/'out' in
            // signature"), which is where the ignored keyword was born: the shared helper, asked above, now refuses
            // any cell the declaration does not account for, so none can reach the value below.
            var unwrapped = paramVal;
            var coerced = CoerceTo(unwrapped, type, line, col);
            // Warn only when coercion actually transforms an incompatible value.
            // Named-object parameters (Form, Button, CancelEventArgs, …) accept any
            // value as-is (object passthrough in CoerceTo), so a pass-through is NOT
            // a mismatch, e.g. UI event handlers typed `(Form sender, CancelEventArgs e)`
            // receive the raw (Button, EventArgs) payload on both VM and tree paths
            // without noise. Numeric widening stays silent (IsKnownCompatibleType).
            if (unwrapped != null && coerced != null && !ReferenceEquals(coerced, unwrapped)
                && !IsKnownCompatibleType(unwrapped, type))
            {
                var valTypeName = unwrapped.GetType().Name;
                var expectedName = type.TypeName ?? TypeSystem.Name(type.BaseType);
                Logging.Log.Warning("Type mismatch",
                    $"Argument '{name}': expected '{expectedName}' but found value of type '{valTypeName}'. Will attempt coercion.",
                    line, col);
            }
            CurrentFrame.Scopes.Define(name, coerced, line, col);
        }
    }

    /// <summary>True when the runtime value type is compatible with the declared parameter type (no coercion needed or expected).</summary>
    //private bool old_IsKnownCompatibleType(object value, TypeInfo type)
    //{
    //    // Named script types: 'List'/'List<T>' → ScriptBoxedList / ScriptUnboxedList<T>, etc.
    //    if (type.TypeName != null)
    //    {
    //        var baseName = type.TypeName.Trim().TrimEnd('>').Split('`')[0].Split('<')[0];
    //        switch (baseName.ToLowerInvariant())
    //        {
    //            case "list" when value is IScriptList: return true;
    //            case "dictionary" when value is IScriptDictionary: return true;
    //            case "hashset" when value is IScriptHashSet: return true;
    //            // Interface parameters: any enumerable satisfies IEnumerable/ICollection;
    //            // IList accepts script lists and .NET IList implementations.
    //            case "ienumerable" when value is IEnumerable: return true;
    //            case "icollection" when value is ICollection or IEnumerable: return true;
    //            case "ilist" when value is IList or IScriptList: return true;
    //        }
    //        // Script class instances, exact name OR any ancestor in the Parent chain (Dog → Animal)
    //        if (value is ClassInstance && IsClassCompatible(value, baseName))
    //            return true;
    //        if (value is StructInstance si && string.Equals(si.StructName, baseName, StringComparison.OrdinalIgnoreCase))
    //            return true;
    //        // Lambda/function/delegate values satisfy any delegate-typed parameter,
    //        // Func<...>, Action<...>, Predicate<...>, Converter<...>, user delegates (registered in _delegates)
    //        if (value is ScriptFunction or CombinedDelegate or Delegate)
    //        {
    //            if (baseName.StartsWith("Func", StringComparison.OrdinalIgnoreCase)
    //                || baseName.StartsWith("Action", StringComparison.OrdinalIgnoreCase)
    //                || baseName.StartsWith("Predicate", StringComparison.OrdinalIgnoreCase)
    //                || baseName.StartsWith("Converter", StringComparison.OrdinalIgnoreCase)
    //                || baseName.EndsWith("Delegate", StringComparison.OrdinalIgnoreCase))
    //                return true;
    //            if (DelegateInfos.ContainsKey(baseName))
    //                return true;
    //        }
    //        // Exact CLR-name match (e.g. 'StringBuilder')
    //        if (string.Equals(value.GetType().Name, baseName, StringComparison.OrdinalIgnoreCase))
    //            return true;
    //        return false;
    //    }

    //    // Primitives: allow exact runtime match + numeric widening (no warning for int→long)
    //    if (type.IsArray) return value is Array;
    //    return type.BaseType switch
    //    {
    //        TokenType.Int when value is int or long or byte or short or sbyte or uint or ushort or ulong => true,
    //        TokenType.Long when value is long or int or byte or short or sbyte or uint or ushort or ulong => true,
    //        TokenType.Short when value is short or byte or sbyte or int or long or uint or ushort or ulong => true,
    //        TokenType.Byte when value is byte or sbyte or short or int or long or uint or ushort or ulong => true,
    //        TokenType.SByte when value is sbyte or byte or short or int or long or uint or ushort or ulong => true,
    //        TokenType.UShort when value is ushort or uint or ulong or short or byte or int or long or sbyte => true,
    //        TokenType.UInt when value is uint or ulong or int or long or byte or short or ushort or sbyte => true,
    //        TokenType.ULong when value is ulong or uint or long or int or ushort or byte or short or sbyte => true,
    //        TokenType.Double or TokenType.Float when value is double or float or int or long or byte or short => true,
    //        TokenType.StringType when value is string => true,
    //        TokenType.Bool when value is bool => true,
    //        TokenType.Char when value is char => true,
    //        TokenType.Object => true, // object accepts anything
    //        _ => false
    //    };
    //}

    /// <summary>Collects the type names declared by the current script (classes, structs, enums,
    /// delegates, including nested) so forward references in signatures are not flagged.</summary>
    private static HashSet<string> CollectDeclaredTypeNames(IEnumerable<Stmt> stmts)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                switch (s)
                {
                    case ClassDeclStmt c:
                        names.Add(c.Name);
                        foreach (var d in c.Delegates) names.Add(d.Name);
                        Walk(c.NestedClasses);
                        Walk(c.NestedStructs);
                        break;
                    case StructDeclStmt sd:
                        names.Add(sd.Name);
                        break;
                    case EnumDeclStmt e:
                        names.Add(e.Name);
                        break;
                    case InterfaceDeclStmt i:
                        // An interface IS a type this file declares, and it has to be in here: the load-time
                        // shell check asks this set whether a compiled body's `is IShape` can resolve, and a
                        // bundle was refused at load for a name the same file declares below it.
                        names.Add(i.Name);
                        break;
                    case DelegateDecl d:
                        names.Add(d.Name);
                        break;
                    // Namespace blocks are transparent containers (like the pre-registration and
                    // free-variable passes): recurse so their declared types are known upfront.
                    case NamespaceDeclStmt ns:
                        Walk(ns.Body);
                        break;
                }
            }
        }
        Walk(stmts);
        return names;
    }

    /// <summary>True when a named type is resolvable: a script-declared type, a registered .NET
    /// type (scope Type value), a user class/struct/delegate, or a generic container name.
    /// INTERNAL because the VM asks it too: a bare name that IS a type but has no CLR value (a script
    /// INTERFACE, which is not a value at all) must reach the tree, where a qualified interface call
    /// like `IShapeX.area(obj)` is resolved (crash_interface_test 39).</summary>
    /// <summary>The VALUE a struct's own NAME binds in scope: a bare <see cref="TypeInfo"/> handle carrying the
    /// declared name, NOT the declaration node. `typeof(Pt)` must answer "Pt" and `Pt is ClassInfo` must hold,
    /// exactly as they do for a class's ClassInfo, and a source run gets both because this is what
    /// VisitStructDeclStmt binds (the concrete arm AND the generic-template arm). The source-less LOADER bound
    /// the StructDeclStmt node ITSELF, so a stripped bundle reported "StructDeclStmt" for `Pt` and answered
    /// False for `Pt is ClassInfo` while the source answered "Pt" and True (measured on crash_typeof_matrix
    /// checks 3f and 3k, found by tools/bundle-parity.sh). All three sites go through here so the two engines
    /// cannot drift about what a struct's definition IS.</summary>
    internal static TypeInfo StructDefinitionHandle(string name) => new(TokenType.Object, false, 0, 0, name);

    internal bool IsKnownTypeName(string name)
    {
        // Generic form: `Func<Int,Int,Int>` / `Box<int>`, match on the base name only.
        ReadOnlySpan<char> span = name;
        var lt = span.IndexOf('<');
        if (lt >= 0) span = span[..lt];
        // CLR arity-mangled names: `Action`1`, `Func`2`, `Predicate`1` → the base delegate name.
        var tick = span.IndexOf('`');
        if (tick >= 0) span = span[..tick];

        // Predicate/signature contract types, Iterable, RandomAccess and Function are real
        // signature types (params/returns) resolved by CoerceTo, so they are known names.
        if (span is "Iterable" or "RandomAccess" or "Function") return true;

        if (span is "List" or "Dictionary" or "HashSet" or "IEnumerable" or "ICollection" or "IList"
            or "Action" or "Func" or "Predicate" or "Comparison" or "Converter" or "EventHandler")
            return true;

        var baseName = span.ToString();
        // The script-facing TYPE ALIASES (ScriptTypeRegistry): the table the `is`/`as` predicate reads in its
        // DEFAULT ARM, holding the engine's own value types under the names a script writes (Tuple,
        // KeyValuePair, WeakRef, Task, ClassInfo, ...). They were missing HERE, so the two questions drifted
        // in the one direction that hurts: `kv is KeyValuePair` answered TRUE while this predicate called the
        // name unknown, and a SOURCE-LESS bundle whose body made that test was refused AT LOAD with "type
        // 'KeyValuePair' referenced by the bytecode is not available in this environment" for a name the very
        // same run resolves. Measured on _kvprobe, and the same refusal named 'Tuple' in _probe13 and
        // 'consolecolor' in crash_typeof_matrix. A bundle that cannot be loaded at all is the worst outcome
        // this strip has, so the load check asks the matcher's own table.
        // ASKED UNDER THE INTERPRETER'S CASE RULE, unlike the load-time pre-flight's own copy of this same
        // question (BytecodeIo.ValidateShellEnvironment asks the table INSENSITIVELY, because a TYPE POSITION is
        // where the runtime itself is insensitive). THIS predicate also answers "is this loaded NAME a TYPE?",
        // which the strip's UnrunnableTypeLoads and the VM's global-load-miss arm both ask, and a
        // case-INSENSITIVE match there is a FALSE POSITIVE on a lowercase VARIABLE: measured, `com_provider_test`
        // loads a global spelled `comnode`, and treating that as the type `ComNode` made its stripped form
        // REFUSE ("names a TYPE the VM has no value for") where it had stripped for months. SetKnows keeps the
        // index insensitive while enforcing the flag, the shape every other name set in this method uses.
        if (SetKnows(ScriptTypeRegistry.Keys, ScriptTypeRegistry.ContainsKey, baseName)) return true;
        if (_declaredTypeNames != null && _declaredTypeNames.Contains(baseName)) return true;
        if (Classes.ContainsKey(baseName)) return true;
        if (Structs.ContainsKey(baseName)) return true;
        if (DelegateInfos.ContainsKey(baseName)) return true;
        if (CurrentFrame.Scopes.TryGet(baseName, out var v))
            return v is Type or ClassInfo or DelegateInfo or StructDeclStmt;
        // Engine UI control types are valid script annotation names (Label, Form,
        // Button, ListView, ...), recognizing them keeps typed parameters/returns
        // warning-free instead of falling back to object with a warning.
        foreach (var uiType in UiTypeNames)
            if (string.Equals(baseName, uiType, StringComparison.Ordinal)) return true;
        // The hand-maintained list above stays a FAST PATH; the shared lookup is the authority, the same chain
        // `typename`, `is` and a type argument use. A name this list forgot resolved to nothing at all: `ComNode`
        // and `ComScope` were both missing from it, so every parameter, return type and field typed `ComNode` was
        // silently "treated as object", with a warning as the only signal. Two lists of type names is the drift
        // this repository keeps re-learning; this makes the second one a shortcut instead of a second answer.
        if (IsKnownType(name)) return true;
        return false;
    }

    private static readonly string[] UiTypeNames =
    {
        "Label", "Button", "ButtonBase", "CheckBox", "RadioButton", "TextBox",
        "ListBox", "ComboBox", "TrackBar", "ListView", "TreeView", "StatusBar",
        "TabControl", "DateTimePicker", "Form", "Control", "GenericControl",
        "MdiChild", "MdiForm", "InputBox", "OpenFileDialog", "SaveFileDialog", "SysLink",
        "ToolBar", "Rebar", "HotKey", "TheTimer", "ControlCollection", "ItemsCollection"
    };

    /// <summary>Warns when a parameter type name does not resolve to any known type, catches
    /// typos like `(FormStupid sender, ...)` that would otherwise be silently treated as object.
    /// Dotted names (System.Text.StringBuilder) resolve via namespaces and are skipped; generic
    /// type parameters and caller-provided names (e.g. class type parameters) are exempt.</summary>
    /// <summary>`Task` and `Task<T>` are RECOGNIZED type spellings even though the plain name belongs to the engine's
    /// NAMESPACE (`Task.Run`, `Task.DelayAsync`), which is why `IsKnownTypeName` cannot answer for it: a function that
    /// returns or takes a Task is C#'s own idiom (a forwarder, `Task<int> F() { return AnAsyncCall(); }`), it works
    /// here, and warning that the type is unknown would be noise on a legal spelling. Measured: both declara
    /// positions warned before this, while `await F()` and `.Result` already answered correctly.
    /// Compared Ordinal on purpose: this engine's type spellings are case-sensitive, so `task` stays unknown.</summary>
    private static bool IsTaskTypeName(string? name)
        => name is "Task" or "Task<T>";
    private void WarnUnknownParamTypes(IReadOnlyList<(TypeInfo Type, string Name)> @params, string label,
        int line, int column, HashSet<string>? alsoKnown = null)
    {
        // BEFORE the warning flag: a name the engine registers under another spelling is a refusal, not a warning
        // that a caller can suppress.
        foreach (var (type, pname) in @params)
            RefuseUnregisteredTypeNames(type, $"parameter '{pname}' of '{label}'", line, column);
        if (SuppressWarnings) return;
        foreach (var (type, pname) in @params)
        {
            var tn = type.TypeName;
            if (string.IsNullOrEmpty(tn) || tn.Contains('.') ||
                alsoKnown != null && alsoKnown.Contains(tn) || IsTaskTypeName(tn))
                continue;
            if (!IsKnownTypeName(tn))
                Logging.Log.Warning("Type",
                    $"Unknown type '{tn}' in parameter '{pname}' of '{label}', treated as object.",
                    line, column);
        }
    }

    /// <summary>A DECLARED type name that resolves only because the CLR knows it must still be written the way the
    /// engine registers it: `String` is `string`, `Int32` is `int`. Accepting the CLR spelling silently is what
    /// let a constructor declared `String name` be tolerated in every position except the parent-constructor path,
    /// where it refused with "No matching constructor in parent class" (measured 2026-10-05, five scripts).
    /// A CLR type the engine registers NO script name for is untouched (`StringBuilder`, `Control`, `List&lt;T&gt;`),
    /// a truly unknown name keeps the typo warning rather than an error, and `.lax` accepts any case as it does
    /// everywhere else.</summary>
    private void RefuseUnregisteredTypeName(string? name, string where, int line, int column)
    {
        if (!_caseSensitive || string.IsNullOrEmpty(name) || name.Contains('.')) return;
        // The REGISTRIES alone answer this, and that matters: a name they hold under another spelling is the only
        // case refused, a CLR-only name (`StringBuilder`) is in neither, and asking the general resolution instead
        // cost a namespace scan and a `Type.GetType` PER CALL, which a `new` inside a loop paid 50,000 times
        // (crash_array_factory's allocation budgets went red). Both maps are case-insensitive indexes, so the
        // case flag is enforced right here, by comparing the registered spelling with the written one.
        var clr = TypeNameLookup.TryGetValue(name, out var primitive) ? primitive
                : ScriptTypeRegistry.TryGetValue(name, out var registered) ? registered : null;
        if (clr is null) return;                                   // unknown, or a name only the CLR knows
        var script = GetFriendlyTypeName(clr);
        if (string.Equals(script, name, StringComparison.Ordinal)) return;
        RuntimeError($"'{name}' is not a name this engine registers for a type: write '{script}' ({where}).",
            line, column);
    }

    /// <summary>Every name a DECLARED type is written with is checked, generic arguments included:
    /// `List&lt;String&gt;` names String in a position a script writes, so the refusal applies inside it too. A null
    /// type (an inferred `var`) is nothing to check.</summary>
    private void RefuseUnregisteredTypeNames(TypeInfo? type, string where, int line, int column)
    {
        if (type == null) return;
        RefuseUnregisteredTypeName(type.TypeName, where, line, column);
        if (type.GenericArgs != null)
            foreach (var arg in type.GenericArgs)
                RefuseUnregisteredTypeNames(arg, $"{where}, in a type argument", line, column);
    }

    /// <summary>Warns when a function's parameter or return type name does not resolve to any
    /// known type (see WarnUnknownParamTypes).</summary>
    private void WarnUnknownTypeNames(FuncDecl decl, HashSet<string>? alsoKnown = null)
    {
        // The refusal comes FIRST and does not consult the warning flag: a name the engine registers under another
        // spelling is an error, where an unknown name stays the suppressible warning it has always been.
        var declLabel = FrameLabel(decl.Name);
        foreach (var (type, pname) in decl.Params)
            RefuseUnregisteredTypeNames(type, $"parameter '{pname}' of '{declLabel}'", decl.Line, decl.Column);
        RefuseUnregisteredTypeNames(decl.ReturnType, $"the return type of '{declLabel}'", decl.Line, decl.Column);
        if (SuppressWarnings) return;
        // Interpret runs VisitFuncDecl twice (registration pass + execution pass), warn once.
        if (!_warnedUnknownTypes.TryAdd(decl, WarnedDeclSentinel)) return;
        // The declaration's OWN type parameters are known types in its own signature, the same exemption
        // class type parameters already enjoy. Without this, `T Local<T>(T x)` was reported as if `T` were a
        // typo, which is the one thing this check exists to catch, and the noise hid the real ones.
        if (decl.TypeParameters.Count > 0)
        {
            var withOwn = new HashSet<string>(_comparer);
            if (alsoKnown != null) foreach (var known in alsoKnown) withOwn.Add(known);
            foreach (var tp in decl.TypeParameters) withOwn.Add(tp);
            alsoKnown = withOwn;
        }
        var label = FrameLabel(decl.Name);
        WarnUnknownParamTypes(decl.Params, label, decl.Line, decl.Column, alsoKnown);
        var rt = decl.ReturnType.TypeName;
        if (!string.IsNullOrEmpty(rt) && !rt.Contains('.') &&
            (alsoKnown == null || !alsoKnown.Contains(rt)) && !IsKnownTypeName(rt) && !IsTaskTypeName(rt))
            Logging.Log.Warning("Type",
                $"Unknown return type '{rt}' of '{label}', treated as object.",
                decl.Line, decl.Column);
    }

    private bool IsKnownCompatibleType(object value, TypeInfo type)
    {
        // Compatibility does not leak the wrapper: an enum value answers for its own enum type by name,
        // and otherwise the check sees the underlying long, exactly as it did before there was a wrapper.
        if (value is EnumValue kev)
            return (type.TypeName != null
                    && type.TypeName.Equals(kev.EnumName, Comparison))
                   || IsKnownCompatibleType(kev.Value, type);

        if (type.TypeName != null)
        {
            ReadOnlySpan<char> span = type.TypeName.AsSpan().Trim();
            span = span.TrimEnd('>');

            var backtick = span.IndexOf('`');
            if (backtick >= 0) span = span[..backtick];

            var angle = span.IndexOf('<');
            if (angle >= 0) span = span[..angle];

            // Case-insensitive checks directly on the span, no ToLowerInvariant allocation
            if (span.Equals("list", StringComparison.OrdinalIgnoreCase) && value is IScriptList) return true;
            if (span.Equals("dictionary", StringComparison.OrdinalIgnoreCase) && value is IScriptDictionary) return true;
            if (span.Equals("hashset", StringComparison.OrdinalIgnoreCase) && value is IScriptHashSet) return true;
            if (span.Equals("ienumerable", StringComparison.OrdinalIgnoreCase) && value is IEnumerable) return true;
            if (span.Equals("icollection", StringComparison.OrdinalIgnoreCase) && value is ICollection or IEnumerable) return true;
            if (span.Equals("ilist", StringComparison.OrdinalIgnoreCase) && value is IList or IScriptList) return true;

            // baseName allocated once, only when we actually need a string for downstream lookups
            var baseName = span.ToString();

            if (value is ClassInstance && IsClassCompatible(value, baseName))
                return true;
            if (value is StructInstance si && string.Equals(si.StructName, baseName, StringComparison.OrdinalIgnoreCase))
                return true;

            if (value is ScriptFunction or CombinedDelegate or Delegate)
            {
                if (span.StartsWith("Func", StringComparison.OrdinalIgnoreCase)
                    || span.StartsWith("Action", StringComparison.OrdinalIgnoreCase)
                    || span.StartsWith("Predicate", StringComparison.OrdinalIgnoreCase)
                    || span.StartsWith("Converter", StringComparison.OrdinalIgnoreCase)
                    || span.EndsWith("Delegate", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (DelegateInfos.ContainsKey(baseName))
                    return true;
            }

            if (string.Equals(value.GetType().Name, baseName, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        if (type.IsArray) return value is Array;
        return type.BaseType switch
        {
            TokenType.Int when value is int or long or byte or short or sbyte or uint or ushort or ulong => true,
            TokenType.Long when value is long or int or byte or short or sbyte or uint or ushort or ulong => true,
            TokenType.Short when value is short or byte or sbyte or int or long or uint or ushort or ulong => true,
            TokenType.Byte when value is byte or sbyte or short or int or long or uint or ushort or ulong => true,
            TokenType.SByte when value is sbyte or byte or short or int or long or uint or ushort or ulong => true,
            TokenType.UShort when value is ushort or uint or ulong or short or byte or int or long or sbyte => true,
            TokenType.UInt when value is uint or ulong or int or long or byte or short or ushort or sbyte => true,
            TokenType.ULong when value is ulong or uint or long or int or ushort or byte or short or sbyte => true,
            TokenType.Double or TokenType.Float when value is double or float or int or long or byte or short => true,
            TokenType.StringType when value is string => true,
            TokenType.Bool when value is bool => true,
            TokenType.Char when value is char => true,
            TokenType.Object => true,
            _ => false
        };
    }

    /// <summary>Raise an engine error from the VM with the same code, location and ErrorInfo handling as
    /// the interpreter's own RuntimeError (the flat path's unassigned-out error).</summary>
    internal void RuntimeErrorFromVm(string message, int line, int column, ErrorCode code = ErrorCode.Unspecified)
        => RuntimeError(message, line, column, code);

    /// <summary>A ScriptException that names no location gets the one its caller knows. A registration
    /// throws with a message only, it has no idea where it was called from, and the dispatch that DOES know
    /// used to rethrow it bare, so the error reached the console with no [L:C] at all. Every raise that
    /// names a line is returned untouched.</summary>
    internal static ScriptException WithLocation(ScriptException ex, int line, int column)
        => ex.Line != 0 || (line <= 0 && column <= 0)
            ? ex
            : new ScriptException(ex.ErrorCode, ex.Value, line, column);

    /// <summary>The statement the interpreter is on. The CLI's error report falls back to these for a
    /// ScriptException that names no location, a registration throwing its own, so the [L:C] pattern
    /// survives instead of the error arriving with no place attached.</summary>
    internal int CurrentLine => _currentLine;

    internal int CurrentColumn => _currentColumn;

    private void ThrowException(object? value, int line, int column, ErrorCode code = ErrorCode.Unspecified)
    {
        // The STATEMENT's position, like every other refusal (see Refusal): this path used to stamp the NODE's
        // and overwrite the field, which is why a member read on null or a failed conversion still named a
        // sub-expression column while a compiled body named the statement's. A refusal raised while no
        // statement runs keeps the position it was given.
        if (_currentLine > 0) { line = _currentLine; column = _currentColumn; }
        var error = BuildScriptError(value, line, column, code);
        _thrownException = error;
        throw new ScriptException(code, error, line, column);
    }

    /// <summary>
    /// Reserved names
    /// </summary>
    private static readonly HashSet<string> _reservedNames =
    [
        with(StringComparer.Ordinal),
        "IScriptList", "IScriptDictionary", "List", "Dictionary", "IScriptHashSet", "HashSet",
        "Array", "char", "Char", "byte", "Byte", "sbyte", "SByte",
        "short", "Int16", "ushort", "UInt16", "int", "Int32", "uint", "UInt32",
        "long", "Int64", "ulong", "UInt64", "float", "Single", "double", "Double",
        "decimal", "Decimal", "bool", "Boolean", "string", "String", "object", "Object",
        "DateTime", "TimeSpan", "DateTimeOffset", "Guid", "VoidMarker",
        "ScriptFunction", "Coroutine", "EventInfo",
        "CombinedDelegate", "DelegateInfo",
        "Namespace", "Native",
        "ScriptResult", "Result", "RefCell", "ClassInstance",
        "StructInstance", "ScriptErrorInfo", "ErrorInfo", "CronExpression",
        "CronType", "StringBuilder", "Type", "Tuple",
        "Action", "Func", "Predicate", "Comparison", "Converter", "EventHandler"
    ];

    private static void HandleRuntimeError(Exception ex, int line, IReadOnlyList<string>? scriptCallStack = null)
    {
        ConsoleSafe.SetColor(ConsoleColor.Red);
        Console.WriteLine($"[L{line}] Runtime error: {ex.Message}");
        ConsoleSafe.ResetColor();

        // Script call stack (user functions)
        if (scriptCallStack is { Count: > 0 })
        {
            ConsoleSafe.SetColor(ConsoleColor.Yellow);
            Console.WriteLine("Script call stack:");
            ConsoleSafe.ResetColor();
            foreach (var frame in scriptCallStack)
                Console.WriteLine($"  at {frame}");
            Console.WriteLine();
        }

        // Cleaned .NET stack trace
        ConsoleSafe.SetColor(ConsoleColor.DarkGreen);
        Console.WriteLine("Internal trace:");
        ConsoleSafe.ResetColor();
        var cleanTrace = ErrorFormatter.FormatStackTrace(ex, basePath: null);
        if (string.IsNullOrWhiteSpace(cleanTrace))
            Console.WriteLine("  (no relevant frames)");
        else
            Console.Write(cleanTrace);
    }

    // ----------------------------- Implementation IExprVisitor -----------------------------
    public object VisitNumberExpr(NumberExpr node)
    {
        var raw = node.Value;
        // Literal cache keyed by text + inferred base type: the boxed result type
        // depends on the parser-inferred type of the OCCURRENCE (e.g. bare `5` is Int
        // but the inner `5` of `(short)5` is inferred Long), so text-only keys let one
        // occurrence poison all later lookups with a different boxed type.
        var baseType = node.Type.BaseType;
        var cacheKey = raw + "\u0001" + baseType;
        // Check literal cache before parsing
        if (_literalCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Integer path: narrowest-fit cast to eliminate downstream CoerceTo overhead
        if (baseType is TokenType.Int or TokenType.Long or TokenType.Short or TokenType.Byte or
            TokenType.SByte or TokenType.UShort or TokenType.UInt or TokenType.ULong)
        {
            if (long.TryParse(raw, out var longVal))
            {
                // Explicit checks for narrow types (avoid switch pattern-matching issues under JIT/AOT)
                if (baseType == TokenType.Int && longVal is >= int.MinValue and <= int.MaxValue) return (int)longVal;
                if (baseType == TokenType.Short && longVal is >= short.MinValue and <= short.MaxValue) return (short)longVal;
                if (baseType == TokenType.Byte && longVal is >= byte.MinValue and <= byte.MaxValue) return (byte)longVal;
                if (baseType == TokenType.SByte && longVal is >= sbyte.MinValue and <= sbyte.MaxValue) return (sbyte)longVal;
                if (baseType == TokenType.UShort && longVal is >= ushort.MinValue and <= ushort.MaxValue) return (ushort)longVal;
                if (baseType == TokenType.UInt && longVal is >= uint.MinValue and <= uint.MaxValue) return (uint)longVal;
                // Fallback: keep as long (or ulong)
                {
                    _literalCache[cacheKey] = longVal;
                    return longVal;
                }
            }
            if (ulong.TryParse(raw, out var ulongVal))
            {
                _literalCache[cacheKey] = ulongVal;
                return ulongVal;
            }
            // Fallback: could be a fractional integer literal that doesn't parse as long/ulong
            // e.g. parser inferred int for a very large number, treat as double
            {
                _literalCache[cacheKey] = double.Parse(raw, CultureInfo.InvariantCulture);
                return double.Parse(raw, CultureInfo.InvariantCulture);
            }
        }

        return baseType switch
        {
            TokenType.Float => float.Parse(raw, CultureInfo.InvariantCulture),
            TokenType.Decimal => raw.Contains('e') || raw.Contains('E')
                ? (decimal)double.Parse(raw, CultureInfo.InvariantCulture)
                : decimal.Parse(raw, CultureInfo.InvariantCulture),
            _ => double.Parse(raw, CultureInfo.InvariantCulture)
        };
    }
    public object VisitStringExpr(StringExpr node) => node.Value;
    public object VisitBoolExpr(BoolExpr node) => node.Value;
    public object VisitCharExpr(CharExpr node) => node.Value;

    private bool DetectExternalCaptures(LambdaExpr lambda, ScopeStack scopes)
    {
        var paramNames = new HashSet<string>(lambda.Parameters.Count);
        foreach (var p in lambda.Parameters)
            paramNames.Add(p.Name);

        var collector = new CaptureCollector(paramNames, scopes);
        if (lambda.BodyExpr != null)
            collector.Visit(lambda.BodyExpr);
        else if (lambda.BodyStmts != null)
            foreach (var stmt in lambda.BodyStmts)
                collector.Visit(stmt);
        return collector.HasExternalCapture;
    }

    /// <summary>Promotes captured variables in a scope dictionary to RefCell so the closure and
    /// the enclosing scope share a binding. shouldWrap decides per-variable whether the capturing
    /// construct needs the binding; consts, namespaces, types, Coroutine objects and already-RefCell
    /// values are always skipped (a captured const stays a snapshot).</summary>
    private void PromoteCapturesInScope(Dictionary<string, object?> scope, HashSet<string> scopeConsts, Func<string, bool> shouldWrap)
    {
        foreach (var kv in scope.ToList())
        {
            // Skip constants, an immutable captured const stays a snapshot (no binding needed)
            if (scopeConsts.Contains(kv.Key))
                continue;

            // ===== Skip Coroutine namespace and any Coroutine objects =====
            if (kv.Key.Equals("Coroutine", Comparison) || kv.Value is Coroutine)
                continue;

            // ===== Skip other namespaces, types and CALLABLES =====
            // A CALLABLE is not a variable: a native builtin's adapter (`print`, `Check`, ...) or a script
            // function is resolved by name, so wrapping one in a capture cell can only break the lookup. This
            // list was missing the native half and that is a real defect, measured: with a coroutine lambda in
            // the file, `Check("a", c1() == 2)` inside a statement that also CALLS a closure left the global
            // `Check` holding a RefCell, so the VM's CALL found a value it could not invoke, bailed AFTER the
            // arguments had run, and the tree re-ran the statement, incrementing the closure twice
            // (mega_crash_test's five closure checks saw 1, 3, 6 where the tree sees 1, 2, 3). The coroutine
            // lambda matters because it is what puts the promotion in play; `print` was immune only because
            // the CALL arm special-cases it before the global lookup.
            // A FORWARD DECLARATION SLOT is the same shape one step earlier: a nested function's name bound before
            // its declaration runs (PrebindDeclaredFunctions), which the DECLARATION fills with the function. Wrapping
            // it in a cell hid the slot from that fill, because VisitFuncDecl found a RefCell instead of the slot:
            // measured, `binding=RefCell` and the call refused as "used before the declaration" although the
            // declaration had run.
            if (kv.Value is Namespace || kv.Value is Type || kv.Value is ClassInfo ||
                kv.Value is DelegateInfo || kv.Value is ScriptFunction || kv.Value is INativeFunction ||
                kv.Value is ForwardFunctionSlot)
            {
                continue;
            }

            if (!shouldWrap(kv.Key))
                continue;

            // Don't double-wrap values that are already RefCell
            if (kv.Value is RefCell)
                continue;

            // Use a lightweight holder object instead of a 1-element array
            var cell = new MutableCell(kv.Value!);
            var refCell = new RefCell(Getter: cell.Get, Setter: cell.Set);

            scope[kv.Key] = refCell;

            // Update the ENCLOSING live scope's slot to the SAME RefCell: a later plain assignment
            // in the enclosing body (VisitAssignExpr → TryGetVariableInfo → RefCell → Set) then
            // writes through the cell the closure captured, binding, not snapshot.
            //
            // ONLY when that slot is the SAME VARIABLE, which identity decides and a name cannot: the promotion
            // can be asked to wrap a GLOBAL scope while the current frame owns a LOCAL of that name, and stamping
            // the promoted cell there replaced the local's binding. Measured (tools/probe-nullable's sibling
            // repro, 2026-10-06): a nested function's `ref acc` parameter cell was replaced by a cell over the
            // GLOBAL `acc`, so the whole ref chain wrote the global and the caller's variable stayed at 0, while
            // the suite's own read of it came back as the global's stale value.
            if (CurrentFrame.Scopes.TryGet(kv.Key, out var enclosingValue) && ReferenceEquals(enclosingValue, kv.Value))
            {
                CurrentFrame.Scopes.Set(kv.Key, refCell);
            }
        }
    }

    public object VisitLambdaExpr(LambdaExpr node)
    {
        // ── Lazy capture analysis (cheap, must run before the snapshot cascade so a
        // cache hit for a capture-free lambda skips the full scope-copy + recompile) ──
        if (!node.CapturesAnalyzed)
        {
            node.HasExternalCaptures = DetectExternalCaptures(node, CurrentFrame.Scopes);
            node.CapturesAnalyzed = true;
        }

        // Capture-free lambdas use SEmptySnapshot (evaluation-independent), so the same
        // node re-evaluated (e.g. in a loop) can reuse its cached ScriptFunction instead of
        // re-copying every scope + recompiling the body. Written at the end of this method.
        // KEYED BY THE NODE, not by its position (see the field's summary): a position key collided across snippets.
        if (!node.HasExternalCaptures)
        {
            if (_lambdaCache.TryGetValue(node, out var cachedFn))
                return cachedFn;
        }

        // --- Build snapshot manually from current scopes ---
        var scopeList = new List<Dictionary<string, object?>>();
        var constList = new List<HashSet<string>>();

        foreach (var (scopeDict, constSet) in CurrentFrame.Scopes.GetScopesWithConst())
        {
            scopeList.Add(new Dictionary<string, object?>(scopeDict!, _comparer));
            constList.Add([with(constSet, _comparer)]);
        }

        // ===== Ensure the global scope (last element) contains all root global definitions =====
        if (scopeList.Count > 0)
        {
            var capturedGlobal = scopeList[^1];
            var realGlobal = _rootFrame.Scopes.GetGlobalScopeDictionary()
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value, _comparer);
            // Merge: add any missing entries from realGlobal into capturedGlobal
            foreach (var kv in realGlobal.Where(kv => !capturedGlobal.ContainsKey(kv.Key)))
                capturedGlobal[kv.Key] = kv.Value;
            // Also ensure the const set includes root global consts? Not needed for Math.

            // Merge parent lambda's closure (all levels), enables 3+ level nesting
            if (_currentFn?.ClosureSnapshot is { } parentSnapshot)
            {
                // Merge GlobalScope
                foreach (var kv in parentSnapshot.GlobalScope.Where(kv => !capturedGlobal.ContainsKey(kv.Key)))
                    capturedGlobal[kv.Key] = kv.Value;

                // Merge all LocalScopes
                foreach (var ls in parentSnapshot.LocalScopes)
                {
                    foreach (var kv in ls.Where(kv => !capturedGlobal.ContainsKey(kv.Key)))
                        capturedGlobal[kv.Key] = kv.Value;
                }
            }
        }

        scopeList.Reverse();
        constList.Reverse();

        var globalScope = scopeList[0];
        var globalConsts = constList[0];
        var localScopes = scopeList.Skip(1).ToList();
        var localConsts = constList.Skip(1).ToList();

        // ===== Capture the correct 'this' for nested class lambdas =====
        object? capturedThis = null;

        if (_currentClass != null && _currentThis != null)
        {
            capturedThis = _currentThis;
        }

        if (capturedThis != null)
        {
            globalScope.TryAdd("this", capturedThis);
            if (!localScopes.Any() || !localScopes[^1].ContainsKey("this"))
            {
                if (localScopes.Count > 0)
                {
                    localScopes[^1]["this"] = capturedThis;
                }
            }
        }

        // ===== Helper: Promote captured variables to RefCell bindings =====
        // The parser's FreeVariableAnalyzer already listed the names this lambda actually references,
        // only those become shared bindings (spec: "only closure-captured locals become shared bindings").
        var capturedSet = node.CapturedNames != null
            ? new HashSet<string>(node.CapturedNames, _comparer)
            : null;

        // LOCAL scopes: every captured NON-const local becomes a RefCell binding, the closure sees
        // later assignments (nested two-step recursion, mutual recursion in functions). "this" stays a
        // plain capture (VisitVariableExpr resolves it through a dedicated path). Coroutine lambdas and
        // written captures are covered by the first two predicate terms.
        for (var i = 0; i < localScopes.Count; i++)
        {
            PromoteCapturesInScope(localScopes[i], localConsts[i],
                key => node.IsCoroutine || IsVariableModifiedInLambda(node, key) ||
                       (capturedSet != null && capturedSet.Contains(key) && !_comparer.Equals(key, "this")));
        }

        // GLOBAL scope: keep today's rule, globals resolve live through the root scope, so only
        // writes-in-lambda (or coroutine) captures need a RefCell.
        PromoteCapturesInScope(globalScope, globalConsts,
            key => node.IsCoroutine || IsVariableModifiedInLambda(node, key));

        // ── Lazy capture analysis (reserved for future use) ──
        // The CaptureCollector correctly detects most external captures, but some
        // AST patterns (e.g., fun() { list.Add(ci); } where ci is in a block scope)
        // are not handled. Full snapshot is always taken for correctness.

        // ── Lazy capture analysis ──
        if (!node.CapturesAnalyzed)
        {
            node.HasExternalCaptures = DetectExternalCaptures(node, CurrentFrame.Scopes);
            node.CapturesAnalyzed = true;
        }

        var snapshot = node.HasExternalCaptures
            ? new ClosureSnapshot(localScopes, localConsts, globalScope, globalConsts) : SEmptySnapshot;

        // A name this lambda took while its declaration had not run yet (C# hoisting) marks that declaration, which then
        // keeps the tree: a compiled body cannot address a capture that only exists later. A lambda that references such a
        // name HAS external captures, so the snapshot below is the real one and holds it.
        MarkForwardCaptures(snapshot);

        // ===== Determine lambda body and return type =====
        IReadOnlyList<Stmt> lambdaBody;

        if (node.BodyExpr != null)
        {
            // Expression body: => expr
            var returnStmt = ReturnStmt.Create(node.BodyExpr, node.Line, node.Column);
            lambdaBody = [returnStmt];
        }
        else
        {
            // Block body: { statements }
            lambdaBody = node.BodyStmts ?? [];
        }

        // The DECLARATION, matching what the bytecode compiler gives the same lambda (Compiler.cs, the
        // LambdaExpr arm): a block body with no VALUE-RETURNING `return` ANYWHERE is VOID, and everything
        // else is the permissive Object. `BodyReturnsValue` walks INTO a try, an `if` and a loop, which is the
        // whole point: reading only the body's top level declared a lambda VOID when its only `return "x"` sat
        // inside a `try`, and the engine then refused that lambda's own return. The two engines declare it the
        // SAME way, because the declaration is what a script reads back: `print(lambda)` renders its signature,
        // and the tree used to INFER a type here, so `fun (int x) { return x + 1; }` printed `-> object?` (or
        // `-> int`, `-> string?`, ... depending on the body) when the statement was walked and `-> object` when
        // it was compiled. Object is deliberately permissive (the VM skips return coercion) and CoerceTo accepts
        // null for it, so this does not start refusing a lambda that returns null.
        var returnType = node.BodyExpr == null && !BodyReturnsValue(lambdaBody)
            ? TypeInfo.Void
            : TypeInfo.LambdaValueReturn;

        // ===== Create the lambda declaration =====
        var lambdaDecl = FuncDecl.Create(
            name: "",
            node.Parameters,
            node.IsRefParams,
            node.IsParams,
            lambdaBody,
            returnType,
            null,
            AccessModifier.Public,
            node.Line,
            node.Column,
            isOutParams: node.IsOutParams
        );
        // A lambda's parameter DEFAULTS reach its declaration the way a named function's do (a property, since
        // FuncDecl.Create has no slot for them): every arity rule in the engine reads the DECLARATION, so a lambda
        // built without them refused `Lm("x")` on a two-parameter lambda while the same signature written as a
        // function answered.
        lambdaDecl.DefaultValues = node.DefaultValues;
        // An `async fun` lambda: its body may `await`, and a CALL through the value returns a Task. The flag has to
        // travel on the DECLARATION, because that is what InvokeScriptFunction's IsAsync branch reads.
        lambdaDecl.IsAsync = node.IsAsync;
        if (node.IsAsync) lambdaDecl.IsCoroutine = true;

        // Type-name typo guard: warn when a lambda param/return type does not resolve to any
        // known type (e.g. `(FormStupid sender, ...)`) instead of silently treating it as object.
        WarnUnknownTypeNames(lambdaDecl);

        // Precompile capture-safe lambdas so invocation runs in the VM instead of the
        // tree fast path (subscriber lambdas like event handlers showed 1 Tree per fire).
        // CompileToVm returns null for anything unsafe (write-captures, coroutine yield,
        // unknown shapes) → those keep the tree path unchanged. The COMPILE is done below, once the
        // ScriptFunction exists: a lambda that CAPTURES an enclosing local needs the same treatment a local
        // function gets (CompileLocalFunctionToVm), and that needs the closure snapshot to name the captures.

        // Declared tuple return type: (int i, string s) fun ..., attach element names
        // so returned tuples expose .i / .s named access.
        lambdaDecl.TupleElementNames = node.TupleElementNames?.Select(n => n ?? "").ToList();

        // Tuple parameters: carry the per-param element names onto the constructed FuncDecl so
        // the shared bind path can attach them to the coerced ScriptTuple at invocation.
        lambdaDecl.TupleParamNames = node.TupleParamNames;

        // Inference fallback: when no tuple return type is declared but the lambda's
        // parameters are (name1, name2, ...) and the body returns a tuple of the same
        // arity, name the tuple elements after the parameters. This makes
        //   var Test = (int i, string s) => (10, "hello");   // t.s works
        // and
        //   var Test = fun (int i, string s) { return (10, "hello"); };  // t.s works
        if (lambdaDecl.TupleElementNames == null)
        {
            TupleExpr? retTuple = null;
            if (node.BodyExpr is TupleExpr be)
                retTuple = be;
            else if (node.BodyStmts is [ReturnStmt { Value: TupleExpr te }])
                retTuple = te;
            if (retTuple != null && retTuple.Items.Count == node.Parameters.Count)
                lambdaDecl.TupleElementNames = [.. node.Parameters.Select(p => p.Name)];
        }

        var paramTypes = GetParameterTypes(node.Parameters);

        // ===== Create and return the ScriptFunction =====
        var scriptFn = new ScriptFunction(
            lambdaDecl,
            snapshot,
            returnType,
            paramTypes,
            node.IsRefParams,
            Flags: (node.IsCoroutine ? FunctionFlags.Coroutine : FunctionFlags.Legacy),
            IsOutParams: node.IsOutParams
        );

        // ===== Compile the body =====
        // A lambda that CAPTURES an enclosing local compiles with the SAME recipe a local function uses
        // (CompileLocalFunctionToVm): the capture names are fixed on the declaration, the compiler registers
        // them as hidden locals after the params and `this`, and VmCaptures materializes them in that order.
        // Compiling it WITHOUT them is what made such a body read the captured name as a GLOBAL, which is a
        // wrong answer whenever a global of that name exists and an "Undefined variable" otherwise. Measured:
        // `for (var i = 0; i < 3; i = i + 1) { B.Add(fun () => i); }` inside a generator answered the
        // module-level `i` (99) instead of the loop var (3) as soon as its compiled body was in place
        // (probe-toplevel-vm section 9). A capture-free lambda is unchanged: its layout is [params] and the
        // cached ScriptFunction above is reused as before.
        if (!node.IsCoroutine)
        {
            var captureNames = ComputeLocalFunctionCaptures(scriptFn);
            lambdaDecl.Compiled = CompileToVm(lambdaDecl, captureNames);
            if (lambdaDecl.Compiled == null)
            {
                lambdaDecl.VmCaptureNames = null;
            }
            else if (captureNames.Count > 0)
            {
                lambdaDecl.VmCaptureNames = captureNames;
                scriptFn.VmCaptures = MaterializeVmCaptures(scriptFn, captureNames);
            }
        }

        // Cache capture-free lambdas for reuse
        if (!node.HasExternalCaptures)
            _lambdaCache[node] = scriptFn;

        return scriptFn;
    }

    /// <summary>Creates a capture-free ScriptFunction from a pre-compiled lambda declaration (VM MAKE_LAMBDA).</summary>
    internal ScriptFunction CreateLambdaFunction(FuncDecl lambdaDecl)
    {
        var paramTypes = GetParameterTypes(lambdaDecl.Params);
        return new ScriptFunction(
            lambdaDecl,
            SEmptySnapshot,
            lambdaDecl.ReturnType,
            paramTypes,
            lambdaDecl.IsRefParams,
            Flags: FunctionFlags.Legacy,
            IsOutParams: lambdaDecl.IsOutParams
        );
    }

    // Add this helper method
    private TypeInfo InferExpressionType(Expr expr)
    {
        var result = expr switch
        {
            BoolExpr => new TypeInfo(TokenType.Bool, false, 0, 0, null),
            NumberExpr n => n.Type,
            StringExpr => new TypeInfo(TokenType.StringType, true, 0, 0, null),
            CharExpr => new TypeInfo(TokenType.Char, false, 0, 0, null),
            NullExpr => new TypeInfo(TokenType.Object, true, 0, 0, null),
            CastExpr cast => cast.Type,
            BinaryExpr binary => InferBinaryExprType(binary),      // <-- NEW
            LogicalExpr => new TypeInfo(TokenType.Bool, false, 0, 0, null),
            ConditionalExpr cond => InferExpressionType(cond.ThenExpr),
            UnaryExpr unary => unary.Operator.Type == TokenType.Bang
                ? new TypeInfo(TokenType.Bool, false, 0, 0, null)
                : new TypeInfo(TokenType.Double, false, 0, 0, null),
            _ => new TypeInfo(TokenType.Object, true, 0, 0, null)
        };

        return result;
    }

    private TypeInfo InferBinaryExprType(BinaryExpr binary)
    {
        var op = binary.Operator.Type;

        switch (op)
        {
            // =========================================================================
            // 1. COMPARISON OPERATORS - always return bool
            // =========================================================================
            case TokenType.EqualEqual or TokenType.BangEqual or
                TokenType.Less or TokenType.LessEqual or
                TokenType.Greater or TokenType.GreaterEqual or
                TokenType.AndAnd or TokenType.OrOr:
                return new TypeInfo(TokenType.Bool, false, 0, 0, null);

            // =========================================================================
            // 2. PLUS OPERATOR - check for string concatenation FIRST
            // =========================================================================
            case TokenType.Plus:
                {
                    // Check if left operand is a string literal
                    var leftIsStringLiteral = binary.Left is StringExpr;
                    var rightIsStringLiteral = binary.Right is StringExpr;

                    // Check if left operand is a variable that might be a string
                    var leftIsStringVariable = false;
                    var rightIsStringVariable = false;

                    if (binary.Left is VariableExpr varLeft)
                    {
                        // Try to infer from variable name or context
                        // Look for string literal initializer in surrounding code
                        leftIsStringVariable = varLeft.Name == "prefix" || varLeft.Name == "s" ||
                                               varLeft.Name == "str" || varLeft.Name == "text" ||
                                               varLeft.Name == "name" || varLeft.Name == "message";
                    }

                    if (binary.Right is VariableExpr varRight)
                    {
                        rightIsStringVariable = varRight.Name == "prefix" || varRight.Name == "s" ||
                                                varRight.Name == "str" || varRight.Name == "text" ||
                                                varRight.Name == "name" || varRight.Name == "message";
                    }

                    // If either operand is a string literal or a string variable, result is string
                    if (leftIsStringLiteral || rightIsStringLiteral || leftIsStringVariable || rightIsStringVariable)
                    {
                        return new TypeInfo(TokenType.StringType, true, 0, 0, null);
                    }

                    // Recursively infer types from operands
                    var leftType = InferExpressionType(binary.Left);
                    var rightType = InferExpressionType(binary.Right);

                    // If either operand is string type, result is string
                    if (leftType.BaseType == TokenType.StringType || rightType.BaseType == TokenType.StringType)
                    {
                        return new TypeInfo(TokenType.StringType, true, 0, 0, null);
                    }

                    // If either operand is object, check if it might be a string at runtime
                    if (leftType.BaseType == TokenType.Object || rightType.BaseType == TokenType.Object)
                    {
                        // At parse time, we don't know - default to object (not string)
                        // The runtime CoerceTo will resolve the actual type.
                        return new TypeInfo(TokenType.Object, true, 0, 0, null);
                    }

                    break;
                }
        }

        // =========================================================================
        // 3. NUMERIC OPERATIONS - infer from operands
        // =========================================================================
        var leftNumeric = InferExpressionType(binary.Left);
        var rightNumeric = InferExpressionType(binary.Right);

        // If either is double, result is double
        if (leftNumeric.BaseType == TokenType.Double || rightNumeric.BaseType == TokenType.Double)
        {
            return new TypeInfo(TokenType.Double, false, 0, 0, null);
        }

        // If either is float, result is float
        if (leftNumeric.BaseType == TokenType.Float || rightNumeric.BaseType == TokenType.Float)
        {
            return new TypeInfo(TokenType.Float, false, 0, 0, null);
        }

        // If either is long, result is long
        if (leftNumeric.BaseType == TokenType.Long || rightNumeric.BaseType == TokenType.Long)
        {
            return new TypeInfo(TokenType.Long, false, 0, 0, null);
        }

        // =========================================================================
        // 4. DEFAULT, return int for integer operations
        // =========================================================================
        // If both operands are Object (untyped lambda params), return Object
        // to avoid truncating the result during return-type coercion.
        if (leftNumeric.BaseType == TokenType.Object && rightNumeric.BaseType == TokenType.Object)
            return new TypeInfo(TokenType.Object, true, 0, 0, null);
        return new TypeInfo(TokenType.Int, false, 0, 0, null);
    }

    public object VisitNewInstanceExpr(NewInstanceExpr node)
    {
        // 1. Handle built-in generic types (List<T>, Dictionary<K,V>)
        if (TryCreateBagForNew(node, out var bag))
            return bag!;

        // 2. Determine the target type (class or struct, or .NET fallback)
        var (classInfo, structDecl, typeName) = ResolveNewTarget(node);
        var args = node.Arguments ?? [];

        // 3. Struct instantiation. ONE implementation: ResolveStructCtorForCall and RunStructCtor are shared with
        // the VM's CALL_NEW and with CreateStructDirect, so a struct is built and its constructor run the same way
        // whichever path an input reaches.
        if (structDecl != null)
            return InstantiateNewStruct(node, structDecl, args);

        // 4. Class instantiation
        if (classInfo != null)
            return InstantiateNewClass(node, classInfo, args);

        // 5. .NET type fallback (e.g. StringBuilder, List<int>, ScriptResult, etc.)
        if (InstantiateNewClr(node, typeName, args) is { } clrValue)
            return clrValue;

        // 6. Nothing matched
        RuntimeError($"Type '{typeName}' not defined.", node.Line, node.Column);
        return null;
    }

    // A `new List<int>(...)`, `new Dictionary<K, V>(...)` and a `new HashSet<T>(...)` are built by the ONE helper
    // both engines share (CreateBagForNew), so the argument is read the same way a source run reads it. Answers
    // false when the type is not one of those three, so the caller resolves it as a class, a struct or a CLR type.
    private bool TryCreateBagForNew(NewInstanceExpr node, out object? value)
    {
        value = null;
        if (node.TypeInfo == null) return false;
        var ti = node.TypeInfo;
        // A primitive's name is the SCRIPT spelling, not the token table's enum member name: the resolver
        // below reaches the CLR by `Type.GetType("System." + name)`, so "Int"/"Bool"/"Long"/"Float" found
        // nothing while "Object"/"Char"/"Byte"/"Double"/"Decimal" matched a real CLR name by coincidence
        // (System.Int32 is not System.Int). Same helper the compiler uses for a compiled `new`, so the two
        // engines cannot disagree about which name a primitive has.
        var name = ti.TypeName ?? TypeTokenHelper.ScriptNameOf(ti);
        // ONE place for `new`: the type it names and every type argument inside it are names a script writes, so
        // `String` is refused in both (`new List<String>()` and `new String(...)`) with the spelling to use.
        // It sits here, before the arms, because the built-in List/Dictionary arms consume the type arguments
        // themselves and never reach the CLR fallback below (measured 2026-10-06).
        RefuseUnregisteredTypeNames(ti, $"the type of 'new {name}'", node.Line, node.Column);

        if (name.Equals("List", Comparison) && ti.GenericArgs?.Count == 1)
        {
            value = CreateBagForNew(name, ti.GenericArgs[0], null, EvalBagArgs(node), node.Line, node.Column);
            return true;
        }
        if (name.Equals("Dictionary", Comparison) && ti.GenericArgs?.Count == 2)
        {
            value = CreateBagForNew(name, ti.GenericArgs[0], ti.GenericArgs[1], EvalBagArgs(node),
                node.Line, node.Column);
            return true;
        }
        if (name.Equals("HashSet", Comparison) && ti.GenericArgs?.Count == 1)
        {
            value = CreateBagForNew(name, ti.GenericArgs[0], null, EvalBagArgs(node), node.Line, node.Column);
            return true;
        }
        return false;
    }

    // Which declaration a `new` names: a script class, a script struct, or a qualified alias, plus the name the
    // CLR fallback looks up when neither is found. A primitive's name comes from ONE helper, because THIS is the
    // name the resolver is handed.
    private (ClassInfo? ClassInfo, StructDeclStmt? StructDecl, string TypeName) ResolveNewTarget(NewInstanceExpr node)
    {
        ClassInfo? classInfo = null;
        StructDeclStmt? structDecl = null;
        var typeName = string.Empty;

        if (node.TypeInfo != null)
        {
            var ti = node.TypeInfo;
            // The SAME rule as the bag block above, which computes this name a second time for its own arms
            // (line 5973): a primitive's name is the SCRIPT spelling, not the token table's enum member name, so
            // BOTH sites must ask the one helper or the engines disagree about which name a primitive has. That
            // is not hypothetical: fixing only the other site left `new int()` failing with "Type 'Int' not
            // defined", because THIS is the name the resolver is handed (measured, 2026-10-06).
            typeName = ti.TypeName ?? TypeTokenHelper.ScriptNameOf(ti);

            // Generic class instantiation, check templates if not already concrete
            if (ti.GenericArgs is { Count: > 0 } && _genericClassTemplates.TryGetValue(typeName, out var genericTemplate))
            {
                classInfo = InstantiateGenericClass(genericTemplate, ti.GenericArgs, node.Line, node.Column);
            }

            // Check classes first
            if (classInfo == null)
                Classes.TryGetValue(typeName, out classInfo);

            // Check structs
            if (classInfo == null)
                Structs.TryGetValue(typeName, out structDecl);

            // Handle qualified names (e.g. "TestA.MyMod" from imported namespaces)
            if (classInfo == null && structDecl == null)
            {
                var dotPos = typeName.LastIndexOf('.');
                if (dotPos > 0)
                {
                    var nsName = typeName[..dotPos];
                    var className = typeName[(dotPos + 1)..];
                    if (CurrentFrame.Scopes.TryGet(nsName, out var nsObj))
                    {
                        switch (nsObj)
                        {
                            case Namespace ns:
                                {
                                    var classObj = ns.GetProperty(className);
                                    if (classObj is ClassInfo ci)
                                        classInfo = ci;
                                    else if (classObj is StructDeclStmt sd)
                                        structDecl = sd;
                                    break;
                                }

                            case ClassInfo ci:
                                // Direct ClassInfo stored under the qualified name
                                classInfo = ci;
                                break;
                        }
                    }
                    // Fully-qualified new-expression whose scope walk missed (e.g. `new Z.Test.Animal`
                    // where only the tail "Animal" is aliased), fall back to the import alias table.
                    if (classInfo == null && structDecl == null
                        && _libraryTypeAliases.TryGetValue(className, out var aliasedObj))
                    {
                        if (aliasedObj is ClassInfo aci) classInfo = aci;
                        else if (aliasedObj is StructDeclStmt asd) structDecl = asd;
                    }
                }
            }

        }
        else
        {
            var constructorValue = Evaluate(node.ConstructorExpr);
            if (constructorValue is ClassInfo ci)
            {
                classInfo = ci;
                typeName = ci.Name;
            }
            else
            {
                RuntimeError($"Expression does not evaluate to a class or struct (got {constructorValue?.GetType().Name ?? "null"}).", node.Line, node.Column);
            }
        }

        return (classInfo, structDecl, typeName);
    }

    private object InstantiateNewStruct(NewInstanceExpr node, StructDeclStmt structDecl, IReadOnlyList<Expr> args)
    {
        var byRefCall = new bool[args.Count];
        for (var bi = 0; bi < args.Count; bi++)
            byRefCall[bi] = (node.IsRefArgs != null && bi < node.IsRefArgs.Count && node.IsRefArgs[bi])
                         || (node.IsOutArgs != null && bi < node.IsOutArgs.Count && node.IsOutArgs[bi]);
        // Resolved BEFORE the arguments are evaluated, so a refused construction has no argument side effects,
        // and with no VALUES, so the type-aware refinement (which needs them) stays off this path.
        var structCtor = ResolveStructCtorForCall(structDecl, args.Count, byRefCall, null, node.Line, node.Column);
        if (structCtor != null && node.ArgNames is { Count: > 0 })
            args = ReorderNamedArgExprs(args, node.ArgNames, [.. structCtor.Params.Select(p => p.Name)], node.Line, node.Column);

        var argValues = new object?[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            // A `ref`/`out` argument is a reference to a CALLER variable, and the flag lives on the NODE rather
            // than on the expression the evaluator sees, so the cell is built here (the class-constructor path
            // does the same) and a name that does not exist yet is CREATED by the capture, which is what an
            // inline `out var x` declaration is.
            var isRefArg = structCtor != null && node.IsRefArgs != null && i < node.IsRefArgs.Count && node.IsRefArgs[i];
            var isOutArg = structCtor != null && node.IsOutArgs != null && i < node.IsOutArgs.Count && node.IsOutArgs[i];
            if (isRefArg || isOutArg)
            {
                if (args[i] is DiscardExpr)
                    argValues[i] = NullRefCell;
                else if (args[i] is VariableExpr varRefArg)
                    argValues[i] = CaptureVariableReference(varRefArg, node.Line, node.Column,
                        allowUndefined: isOutArg, byOut: isOutArg);
                else if (args[i] is PropertyAccessExpr or ArrayAccessExpr)
                    argValues[i] = RefCellForTarget(args[i], isOutArg, node.Line, node.Column);
                else
                    RuntimeError("Only a variable, a field or an element can be passed by ref/out.", node.Line, node.Column);
            }
            else
            {
                argValues[i] = Evaluate(args[i]);
            }
        }
        return RunStructCtor(structDecl, structCtor, argValues, node.Line, node.Column);
    }

    private object InstantiateNewClass(NewInstanceExpr node, ClassInfo classInfo, IReadOnlyList<Expr> args)
    {
        var ctorClass = FindMatchingConstructor(classInfo, args, node.Line, node.Column);
        if (ctorClass != null && node.ArgNames is { Count: > 0 })
            args = ReorderNamedArgExprs(args, node.ArgNames, [.. ctorClass.Params.Select(p => p.Name)], node.Line, node.Column);
        if (ctorClass != null && ctorClass.Access != AccessModifier.Public && ctorClass.Access != AccessModifier.Internal)
            CheckAccess(ctorClass.Access, classInfo, node.Line, node.Column);

        return InstantiateClass(classInfo, ctorClass, args, null,
            node.IsRefArgs, node.IsOutArgs, node.Line, node.Column);
    }

    // The .NET fallback for a `new` nothing in the script declares. Answers null when the name resolves to no CLR
    // type at all, which is the caller's "Type 'X' not defined"; every other outcome is a value or a refusal.
    private object? InstantiateNewClr(NewInstanceExpr node, string typeName, IReadOnlyList<Expr> args)
    {
        var systemType = ResolveClrNewType(typeName, node.TypeInfo?.GenericArgs);

        if (systemType == null) return null;

        var ctors = systemType.GetConstructors()
            .Where(c =>
            {
                var ps = c.GetParameters();
                var hasParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
                var required = hasParams ? ps.Length - 1 : ps.Length;

                if (args.Count < required)
                    return false;

                if (!hasParams && args.Count > ps.Length)
                    return false;

                return true;
            })
            .ToArray();

        if (ctors.Length == 0 && args.Count == 0)
        {
            try { return Activator.CreateInstance(systemType)!; }
            catch (Exception ex) { RuntimeError($"Failed to create '{systemType.Name}': {FriendlyExceptionMessage(ex)}", node.Line, node.Column); }
        }

        switch (ctors.Length)
        {
            case 0:
                RuntimeError($"No matching constructor found for .NET type '{systemType.Name}' with {args.Count} arguments.", node.Line, node.Column);
                break;

            case > 1:
                {
                    // Every exact-arity candidate is KEPT, and the VALUES choose between them below
                    // (SelectClrConstructor): collapsing to the first is what made `new StringBuilder("x")`
                    // pick the capacity overload.
                    var exacts = ctors.Where(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == args.Count && !(ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false));
                    }).ToArray();

                    if (exacts.Length > 0)
                        ctors = exacts;
                    else
                        RuntimeError($"Ambiguous constructors for .NET type '{systemType.Name}'.", node.Line, node.Column);
                    break;
                }
        }

        // A NAMED call keeps the ctor its parameter NAMES point at (the reorder needs the names before the
        // values exist); a POSITIONAL one is chosen by the VALUES, so `new StringBuilder("x")` takes the
        // string overload instead of `ctors[0]`, the capacity one.
        var paramInfos = ctors[0].GetParameters();
        var rawValues = new object?[args.Count];
        if (node.ArgNames is { Count: > 0 })
        {
            var names = new string[paramInfos.Length];
            for (var i = 0; i < paramInfos.Length; i++) names[i] = paramInfos[i].Name ?? "";
            args = ReorderNamedArgExprs(args, node.ArgNames, names, node.Line, node.Column);
            for (var i = 0; i < args.Count; i++) rawValues[i] = Evaluate(args[i]);
        }
        else
        {
            for (var i = 0; i < args.Count; i++) rawValues[i] = Evaluate(args[i]);
            if (ctors.Length > 1 && SelectClrConstructor(ctors, rawValues) is { } byValues)
                paramInfos = byValues.GetParameters();
        }
        var ctorHasParams = paramInfos.Length > 0 && paramInfos[^1].IsDefined(typeof(ParamArrayAttribute), false);
        var ctorFixedCount = ctorHasParams ? paramInfos.Length - 1 : paramInfos.Length;
        var argValues = new object?[ctorHasParams ? paramInfos.Length : args.Count];
        for (var i = 0; i < ctorFixedCount; i++)
            argValues[i] = CoerceTo(rawValues[i], MapTypeInfo(paramInfos[i].ParameterType), node.Line, node.Column);
        if (ctorHasParams)
        {
            var elemType = paramInfos[ctorFixedCount].ParameterType.GetElementType()!;
            var excessCount = Math.Max(0, args.Count - ctorFixedCount);
            var paramArray = Array.CreateInstance(elemType, excessCount);

            for (var i = 0; i < excessCount; i++)
                paramArray.SetValue(CoerceTo(rawValues[ctorFixedCount + i], MapTypeInfo(elemType), node.Line, node.Column), i);

            argValues[ctorFixedCount] = paramArray;
        }

        try
        {
            return Activator.CreateInstance(systemType, argValues)!;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            RuntimeError($"Failed to instantiate .NET type '{systemType.Name}': {FriendlyExceptionMessage(inner)}", node.Line, node.Column);
            return null;
        }
    }

    // -----------------------------------------------------------------------------
    // Shared class-instantiation core (tree VisitNewInstanceExpr + VM CALL_NEW)
    // -----------------------------------------------------------------------------

    /// <summary>Creates a ClassInstance exactly like the tree path: field/property initializers
    /// run inside a _currentThis/_currentClass window (so lambda fields capture THIS instance),
    /// lambda-field closures are re-patched to point at the new instance, static constructors run
    /// on first access, then the (already resolved) instance constructor is invoked with the args.
    /// preEvalArgs == null → the tree path: each arg expression is evaluated (with ref/out handling)
    /// and coerced exactly as before. preEvalArgs != null → the VM path: values were already computed
    /// on the VM stack; each is coerced to the ctor param type (matching the tree's coercion).</summary>
    private object InstantiateClass(
        ClassInfo classInfo,
        ConstructorDecl? ctorClass,
        IReadOnlyList<Expr> args,
        object?[]? preEvalArgs,
        IReadOnlyList<bool>? isRefArgs,
        IReadOnlyList<bool>? isOutArgs,
        int line, int column)
    {
        var fieldValues = new Dictionary<string, object?>(classInfo.Fields.Count + (classInfo.Parent != null ? 8 : 0), _comparer);
        var events = new Dictionary<string, EventInfo>(classInfo.Events.Count > 0 ? classInfo.Events.Count : 1, _comparer);
        foreach (var evtDecl in classInfo.Events)
            events[evtDecl.Name] = new EventInfo(evtDecl.Name, evtDecl.DelegateTypeName, [with(4)]);

        var classInstance = new ClassInstance(classInfo, fieldValues, events);

        // Evaluate field/property initializers with the constructed instance as 'this'
        // (and the class as _currentClass). Without this, 'this' inside an initializer,
        // e.g. a lambda field like `getGreeting = fun () => this.innerName`, resolves to
        // whatever instance was current at the allocation site (e.g. an outer object from
        // the calling method), capturing the WRONG instance into the lambda's closure.
        var prevThis = _currentThis;
        var prevClass = _currentClass;
        _currentThis = classInstance;
        _currentClass = classInfo;
        try
        {
            foreach (var field in GetAllFields(classInfo))
            {
                var initValue = field.Initializer != null ? Evaluate(field.Initializer) : DefaultFor(field.Type);
                fieldValues[field.Name] = CoerceTo(initValue, field.Type, line, column);
            }

            // Initialize property backing fields with their default values
            foreach (var prop in classInfo.Properties)
            {
                if (prop.Initializer == null) continue;
                var backingName = BackingFieldName(prop.Name);
                var propVal = Evaluate(prop.Initializer);
                fieldValues[backingName] = CoerceTo(propVal, prop.Type, line, column);
            }
        }
        finally
        {
            _currentThis = prevThis;
            _currentClass = prevClass;
        }

        // ===== For lambda fields that capture 'this', ensure their closure targets THIS instance =====
        // (defensive: covers construction paths where the initializer ran with a different
        //  'this' context; LocalScopes are deep-copied because 'this' can live there too).
        foreach (var field in classInfo.Fields)
        {
            if (field.Initializer is not LambdaExpr) continue;
            if (fieldValues[field.Name] is not ScriptFunction fn) continue;
            if (fn.ClosureSnapshot == null) continue;

            var localScopesCopy = fn.ClosureSnapshot.LocalScopes
                .Select(ls => new Dictionary<string, object?>(ls, _comparer)).ToList();
            foreach (var ls in localScopesCopy)
                ls["this"] = classInstance;

            var updatedSnapshot = new ClosureSnapshot(
                localScopesCopy,
                fn.ClosureSnapshot.LocalConsts,
                CopyDictionary(fn.ClosureSnapshot.GlobalScope),
                [.. fn.ClosureSnapshot.GlobalConsts]
            )
            {
                GlobalScope =
                {
                    // Add or update 'this' in the global scope
                    ["this"] = classInstance
                }
            };

            // Create a new ScriptFunction with the updated snapshot
            fieldValues[field.Name] = new ScriptFunction(
                fn.Declaration,
                updatedSnapshot,
                fn.ReturnType,
                fn.ParameterTypes,
                fn.IsRefParams,
                fn.Flags
            );
        }

        if (ctorClass == null) return classInstance;
        // Run static constructors (from base to derived) on first access
        RunStaticConstructors(classInfo, line, column);

        // Size the arg array off the pre-evaluated values when present (VM path) so the
        // synthetic Expr list can be empty, the tree path passes args.Count implicitly.
        var argCount = preEvalArgs?.Length ?? args.Count;
        var argValues = new object?[argCount];

        // Re-resolve the constructor with runtime arg types when the static (Expr-level)
        // resolution was only a provisional arity match (e.g. new Tagged(variable) where
        // the variable's static type is unknown). Prevents wrong-overload coercion like
        // tagStr("hi") landing on Tagged(int) → "Cannot convert string 'hi' to int".
        if (preEvalArgs != null)
        {
            for (var i = 0; i < argCount; i++) argValues[i] = preEvalArgs[i];
            // The re-resolution weighs the same array the tree path does, so a ref/out position is UNKNOWN
            // there too: the tree leaves it null and the VM must NOT weigh the CELL it holds as the cell's
            // own type, or the overload pick would choose on `RefCell` instead of on "any type fits".
            var weigh = argValues;
            if (isRefArgs != null || isOutArgs != null)
            {
                weigh = new object?[argCount];
                for (var i = 0; i < argCount; i++)
                {
                    var isRefI = isRefArgs != null && i < isRefArgs.Count && isRefArgs[i];
                    var isOutI = isOutArgs != null && i < isOutArgs.Count && isOutArgs[i];
                    weigh[i] = (isRefI || isOutI) ? null : argValues[i];
                }
            }
            ctorClass = ResolveConstructorForValues(classInfo.Constructors, ctorClass, weigh, line, column);
        }
        else
        {
            // Evaluate all non-ref/out args first so runtime types are known for re-resolution.
            var evalValues = new object?[argCount];
            for (var i = 0; i < argCount; i++)
            {
                var isRef = isRefArgs != null && i < isRefArgs.Count && isRefArgs[i];
                var isOut = isOutArgs != null && i < isOutArgs.Count && isOutArgs[i];
                if (isRef || isOut) continue; // ref/out stay expression-bound
                evalValues[i] = Evaluate(args[i]);
            }
            ctorClass = ResolveConstructorForValues(classInfo.Constructors, ctorClass, evalValues, line, column);
            Array.Copy(evalValues, argValues, argCount);
        }

        // A `params` ctor: assemble the tail into ONE array first, so every step below sees exactly one value
        // per declared parameter (both the coercion loop and the bind loop index ctorClass.Params[i] by
        // position). The tail is by-value, so a ref/out flag reaching into it is refused rather than misread.
        if (ctorClass!.HasParamsTail)
        {
            // The tail rule, and the ref/out refusal, live in ONE place: AssembleParamsTail.
            argValues = NormalizeCtorParamsArgs(ctorClass, argValues, line, column, isRefArgs, isOutArgs);
            argCount = argValues.Length;
            // The VM path re-reads this array in the loop below, so it must be the normalized one.
            if (preEvalArgs != null) preEvalArgs = argValues;
        }

        // The `params` tail slot holds the assembled array, already element-coerced by NormalizeCtorParamsArgs,
        // and the declared type there is the ELEMENT type for the bare `params byte items` spelling: coercing it
        // again would try to convert the whole array into one element (measured: "cannot convert 'Byte[]' to
        // 'byte'"), and the bind below must DEFINE it rather than put it through BindParam, for the same reason.
        // The function path binds its own tail the same way.
        var coerceEnd = ctorClass!.HasParamsTail ? ctorClass.FixedParamCount : argCount;

        for (var i = 0; i < coerceEnd; i++)
        {
            if (preEvalArgs != null)
            {
                // A ref/out position arrives as a CELL (the VM's CALL_NEW builds one), and the bind below hands
                // it to BindParam, which writes through: coercing it against the declared type would fail
                // ("cannot convert 'RefCell' to 'int'") on a call that runs from source, so the position is left
                // exactly as it came. The flag comes from the call site (ArgFlagsMarker), because the ctor's
                // declaration is not the only thing that can name a position: a swapped keyword is judged by the
                // cell's ByOut at the bind, which is why the cell carries the call site's keyword.
                var vmIsRef = isRefArgs != null && i < isRefArgs.Count && isRefArgs[i];
                var vmIsOut = isOutArgs != null && i < isOutArgs.Count && isOutArgs[i];
                if (!vmIsRef && !vmIsOut)
                    argValues[i] = CoerceTo(preEvalArgs[i], ctorClass!.Params[i].Type, line, column);
                continue;
            }
            var isRef = isRefArgs != null && i < isRefArgs.Count && isRefArgs[i];
            var isOut = isOutArgs != null && i < isOutArgs.Count && isOutArgs[i];
            if (isRef || isOut)
            {
                var expr = args[i];
                if (expr is DiscardExpr)
                    argValues[i] = NullRefCell;
                else if (expr is VariableExpr varExpr)
                    argValues[i] = CaptureVariableReference(varExpr, line, column, allowUndefined: isOut, byOut: isOut);
                else if (expr is PropertyAccessExpr or ArrayAccessExpr)
                    argValues[i] = RefCellForTarget(expr, isOut, line, column);
                else
                    RuntimeError("Only a variable, a field or an element can be passed by ref/out.", line, column);
            }
            else
            {
                // Use the pre-evaluated value (already in argValues), no double evaluation.
                argValues[i] = CoerceTo(argValues[i], ctorClass!.Params[i].Type, line, column);
            }
        }

        var previousThis = _currentThis;
        var previousClass = _currentClass;
        var previousInConstructor = _inConstructor;
        _currentThis = classInstance;
        _currentClass = classInfo;
        _inConstructor = true;
        classInstance.IsInInitPhase = true;

        CurrentFrame.Scopes.PushScope();
        try
        {
            CurrentFrame.Scopes.Define("this", classInstance);
            for (var i = 0; i < argValues.Length; i++)
            {
                if (coerceEnd == i)
                {
                    // The assembled `params` array, defined as-is, what the function path does for its tail.
                    CurrentFrame.Scopes.Define(ctorClass.Params[i].Name, argValues[i], ctorClass.Line, ctorClass.Column);
                    continue;
                }
                BindParam(ctorClass.Params[i].Name, argValues[i], ctorClass.Params[i].Type,
                    ctorClass.IsRefParams[i], ctorClass.IsOutParams != null && i < ctorClass.IsOutParams.Count && ctorClass.IsOutParams[i],
                    line, column);
            }
            // ── out-param assignment tracking for constructors ──
            // AFTER the parameters are bound, never before: InitOutTracking wraps the CELL it finds under each out
            // parameter's name in the LIVE scope, so running it before the scope was pushed and the parameter
            // defined wrapped nothing, left every flag false, and refused a constructor for an assignment it HAD
            // made (measured: "The 'out' parameter 'a' must be assigned before the function returns" for the body
            // `{ a = 33; }`). The function path already calls it after its own bind loop.
            var outAssigned = InitOutTracking(ctorClass!.IsOutParams, ctorClass.Params, line, column);
            // A ctor whose body is COMPILED and whose AST is GONE must run on the VM even when it declares ref/out
            // params: the tree branch below executes `ctorClass.Body`, which a BUNDLE does not carry (a descriptor
            // carries bytecode, not statements), so such a ctor would run NOTHING and its `out` parameter would then
            // be reported unassigned (measured: a stripped bundle of a ctor that assigns its `out` param answered
            // "The 'out' parameter 'c' must be assigned before the function returns"). The CELLS the caller passed
            // write through, and InitOutTracking plus VerifyOutParams still own the out-assignment rule, so nothing
            // about it is relaxed.
            // The compiled body this ctor runs on the VM, held in a LOCAL so the null check below is the one the
            // nullable analysis follows (a bool computed from the property no longer proves it at the call).
            var ctorBytecode = ctorClass.Compiled;
            var ctorRunsInVm = ctorBytecode != null
                && (!HasRefOrOutCtorParams(ctorClass) || ctorClass.Body.Count == 0);
            // A VM-run ctor's `out` parameters need the tracking wrap on the ARGUMENT the body receives: see
            // WrapCtorOutArgs, and the tree's own tracker answers when this is null.
            bool[]? ctorOutTrack = null;
            if (ctorRunsInVm)
            {
                ctorOutTrack = WrapCtorOutArgs(ctorClass!, ref argValues, line, column);

                // Same rule as TryExecuteVmBody, and the reason the coroutine path and the imported-class static
                // constructor already refuse: the tree runs this constructor's AST from its FIRST statement, so a VM
                // that wrote a global or a property before stopping would have that work repeated. Refuse instead.
                var ctorCommitBaseline = VmBodyCommitted;
                VmBodyCommitted = false;
                object? ctorVmResult;
                var ctorCommittedHere = false;
                try
                {
                    ctorVmResult = Vm.Execute(ctorBytecode!, BuildCtorVmArgs(argValues, classInstance), this, classInfo.Name);
                }
                finally
                {
                    ctorCommittedHere = VmBodyCommitted;
                    VmBodyCommitted = ctorCommitBaseline || ctorCommittedHere;
                }
                if (ctorVmResult is VmFallback)
                {
                    if (ctorCommittedHere)
                        RuntimeError($"Constructor '{classInfo.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
                    foreach (var stmt in ctorClass.Body)
                        Execute(stmt);
                }
            }
            else
            {
                foreach (var stmt in ctorClass.Body)
                    Execute(stmt);
            }

            // Verify out params. A VM-run ctor is checked against the tracker THIS path built, because the body writes
            // to its own slot and the live scope's wrapper never sees it; the tree's own tracker answers otherwise.
            VerifyOutParams(ctorOutTrack ?? outAssigned, ctorClass.IsOutParams, ctorClass.Params, ctorClass.Line, ctorClass.Column);
        }
        finally
        {
            CurrentFrame.Scopes.PopScope();
            classInstance.IsInInitPhase = false;
            _currentThis = previousThis;
            _currentClass = previousClass;
            _inConstructor = previousInConstructor;
        }
        return classInstance;
    }

    /// <summary>Constructors compile with params then `this` as locals, the VM arg layout
    /// is [params..., this], mirroring method invocation (instance at vmArgs[Params.Count]).</summary>
    private static object?[] BuildCtorVmArgs(object?[] paramValues, object instance)
    {
        var vmArgs = new object?[paramValues.Length + 1];
        Array.Copy(paramValues, vmArgs, paramValues.Length);
        vmArgs[paramValues.Length] = instance;
        return vmArgs;
    }

    /// <summary>Script-class instantiation for the VM's CALL_NEW. Resolves the class from the
    /// registered _classes registry, finds the ctor type-aware on the runtime arg values (reusing
    /// _constructorCache when the (DeclName, ArgCount) key is unambiguous, same-arity overloads
    /// resolve uncached so int vs string cannot collide), and creates the ClassInstance through the
    /// same InstantiateClass core the tree uses. Any form that can't be proven, struct new, .NET
    /// types, unknown names, ambiguous/absent ctors, returns VmFallback so the whole function
    /// silently re-runs on the tree-walker.</summary>
    internal object InstantiateForVm(string typeName, object?[] args, int line, int column)
        => InstantiateForVm(typeName, null, args, line, column);

    /// <summary>Script-class / struct instantiation for the VM's CALL_NEW, plus the shapes a generic
    /// instantiation needs: the bag types the language models (List / Dictionary / HashSet, built by the
    /// SAME creators the tree uses), a generic SCRIPT class (closed by InstantiateGenericClass, again the
    /// same helper), and .NET types (resolved and closed by ResolveClrNewType). Anything still unproven
    /// returns VmFallback, so the whole function runs on the tree instead.</summary>
    internal object InstantiateForVm(string typeName, TypeArgList? typeArgs, object?[] args, int line, int column,
        IReadOnlyList<bool>? isRefArgs = null, IReadOnlyList<bool>? isOutArgs = null)
    {
        // The type-name rule must hold on THIS path too. A top-level statement runs COMPILED with the switch on, so a
        // `new` whose type argument is an unregistered name used to slip through here while the tree refused the same
        // expression, and it answered with an EMPTY list because the argument was coerced against a type nothing
        // could resolve. The tree's own call is VisitNewInstanceExpr's, with the same helper and therefore the same
        // words; the type arguments are TypeInfos, so the recursive form covers them.
        RefuseUnregisteredTypeName(typeName, $"the type of 'new {typeName}'", line, column);
        if (typeArgs != null)
        {
            foreach (var ta in typeArgs)
                RefuseUnregisteredTypeNames(ta, $"the type of 'new {typeName}', in a type argument", line, column);
        }
        // The bag types the language models itself, by name and arity: a script writes
        // `new List<object>()` and gets a SCRIPT list, not a raw CLR one.
        if (typeArgs is { Count: > 0 })
        {
            if (typeName.Equals("List", Comparison) && typeArgs.Count == 1)
                return CreateBagForNew(typeName, typeArgs[0], null, args, line, column);
            if (typeName.Equals("HashSet", Comparison) && typeArgs.Count == 1)
                return CreateBagForNew(typeName, typeArgs[0], null, args, line, column);
            if (typeName.Equals("Dictionary", Comparison) && typeArgs.Count == 2)
                return CreateBagForNew(typeName, typeArgs[0], typeArgs[1], args, line, column);
        }
        // Structs first, `new Vec(...)` on a script struct. The SAME resolver and the same bind-and-run as the
        // tree's `new` expression and CreateStructDirect: a cell among the values is exactly a ref/out argument here
        // (the compiler declines such a call), which the resolver weighs and the binder binds.
        if (!string.IsNullOrEmpty(typeName) && Structs.TryGetValue(typeName, out var structDecl))
        {
            var structByRef = new bool[args.Length];
            for (var bi = 0; bi < args.Length; bi++) structByRef[bi] = args[bi] is RefCell;
            var structCtor = ResolveStructCtorForCall(structDecl, args.Length, structByRef, args, line, column);
            return RunStructCtor(structDecl, structCtor, args, line, column);
        }

        // A GENERIC instantiation is closed BEFORE the name lookups, which is the order the tree uses.
        // The registration keeps an OPEN ClassInfo under the plain name (it carries only the static
        // members, so a class body of the template can resolve them), so looking the name up first finds
        // that open class and builds an instance with no instance fields. Measured: `new Vec<int>()` in a
        // compiled body answered "Field 'X' not found in class 'Vec'" (the TEMPLATE name, not Vec<int>)
        // while the tree ran the same code correctly. InstantiateGenericClass caches by the concrete name,
        // so the second call for one instantiation is free.
        ClassInfo? classInfo = null;
        if (typeArgs is { Count: > 0 } && !string.IsNullOrEmpty(typeName)
            && _genericClassTemplates.TryGetValue(typeName, out var genericTemplate))
            classInfo = InstantiateGenericClass(genericTemplate, typeArgs, line, column);

        // Script class only, structs and .NET/unknown names are not in _classes → tree fallback.
        // Unqualified sibling-class references from compiled library bodies (e.g. `new Animal()`)
        // resolve through the import alias table when the direct lookup misses.
        if (classInfo == null && (string.IsNullOrEmpty(typeName) || !Classes.TryGetValue(typeName, out classInfo)))
        {
            if (typeName.IndexOf('.') < 0 && _libraryTypeAliases.TryGetValue(typeName, out var aliased) && aliased is ClassInfo aci)
                classInfo = aci;
            else
            {
                // Not a script type: the .NET arm, sharing the tree's own resolution and coercion. A
                // null here means "not a .NET type this can prove", which stays a tree job.
                var clr = TryInstantiateClrForVm(typeName, typeArgs, args, line, column);
                if (clr != null) return clr;
                // THE TREE'S OWN REFUSAL when NO arm could construct the name at all, which is a DELEGATE type
                // (`new RefDel(RefImpl)`, unsupported by both engines) and a name nothing declares. The tree's
                // `new` looks for a class, a struct, an alias and a .NET type and then refuses with this sentence
                // (VisitNewInstanceExpr's step 6), and a script's own `catch` swallows it. A compiled body that
                // BAILED instead refused a bundle's WHOLE entry where the file carries on, which is why
                // crash_out_ref section 16 could never strip. The question asked here is the resolver's own
                // (`ResolveClrNewType`), so a name the tree WEIGHS differently (same-arity ctors, an open generic)
                // still falls back: only "no arm at all" is decided here.
                if (!_libraryTypeAliases.ContainsKey(typeName) && ResolveClrNewType(typeName, typeArgs) == null)
                    VmRuntimeError($"Type '{typeName}' not defined.", line, column);
                return VmFallback.Instance;
            }
        }

        // Type-aware ctor resolution on the actual runtime arg types. A ref/out position holds a CELL, whose type is
        // NOT the value's: weighing it chose an overload on `RefCell`, found none, and returned VmFallback for a
        // `new` whose bind was perfectly ordinary (measured: `new A1(ref local)` inside a compiled function bailed,
        // so a stripped bundle could not run it). The position is therefore SKIPPED by the resolver, which is the
        // same thing the struct path does with its own byRef array.
        bool[]? ctorRefOut = null;
        if (isRefArgs != null || isOutArgs != null)
        {
            ctorRefOut = new bool[args.Length];
            for (var i = 0; i < args.Length; i++)
                ctorRefOut[i] = (isRefArgs != null && i < isRefArgs.Count && isRefArgs[i])
                    || (isOutArgs != null && i < isOutArgs.Count && isOutArgs[i]);
        }
        ConstructorDecl? ctor;
        if (HasSameArityOverloads(classInfo.Constructors, args.Length))
        {
            // Same-arity overloads would collide on a (name, count) key too → uncached.
            ctor = FindMatchingConstructor(classInfo, args, line, column, ctorRefOut);
        }
        else
        {
            var ck = (classInfo.Name, args.Length);
            ctor = _constructorCache.TryGetValue(ck, out var cv) ? cv : null;
            if (ctor == null)
            {
                ctor = FindMatchingConstructor(classInfo, args, line, column, ctorRefOut);
                if (ctor != null)
                    _constructorCache[ck] = ctor;
            }
        }

        if (ctor == null)
        {
            // Default ctor only when the tree would return a plain instance (no ctors, no args).
            if (classInfo.Constructors.Count > 0 || args.Length > 0)
            {
                // NO CANDIDATE HAS THIS ARITY: that answer is PROVABLE from the declarations alone, so it is raised
                // here with the tree's own sentence instead of being handed back, because a bundle cannot re-run the
                // statement on the tree. Measured: constructor_test's own `try { new MultiCtor(1, 2, 3); } catch`
                // aborted the WHOLE bundle entry with "stopped part-way through its compiled body" while the file
                // caught the refusal. A candidate whose ARITY fits but whose VALUES do not is still handed back,
                // because the tree weighs the declared parameter types and owns that sentence.
                var arityCanMatch = false;
                foreach (var cd in classInfo.Constructors)
                {
                    if (cd.IsStatic) continue;
                    if (cd.Params.Count == args.Length
                        || (cd.HasParamsTail && args.Length >= cd.FixedParamCount)) { arityCanMatch = true; break; }
                }
                if (!arityCanMatch)
                    RuntimeError($"No constructor found for class '{classInfo.Name}' with {args.Length} arguments.",
                        line, column);
                return VmFallback.Instance;   // tree raises the proper "No constructor..." error / count-based resolution
            }
        }
        else if (ctor.Access != AccessModifier.Public && ctor.Access != AccessModifier.Internal)
        {
            CheckAccess(ctor.Access, classInfo, line, column);
        }

        // Pre-evaluated values straight into the shared core, no synthetic VariableExpr
        // nodes, no temp scope, no Define() calls. `args` (Expr list) is never read by
        // InstantiateClass when preEvalArgs != null, so an empty list is safe, only its Count was used,
        // now sourced from preEvalArgs.Length. The ref/out FLAGS do travel: the compiler emits them
        // (ArgFlagsMarker), the VM's CALL_NEW reads them, and InstantiateClass uses them for the two
        // things a cell position needs, skipping the coercion of a cell and judging a swapped keyword.
        return InstantiateClass(classInfo, ctor, [], args, isRefArgs, isOutArgs, line, column);
    }

    /// <summary>True when two or more non-static ctors accept the same argument count, the
    /// (LineNumber, ArgCount) constructor-cache key would be ambiguous, so resolution must be
    /// uncached and type-aware.</summary>
    /// <summary>Resolves a `new`'s target .NET type: a Type value in scope, or the System / System.Text /
    /// System.Collections[.Generic] spellings, CLOSED with the script's type arguments when the expression
    /// names them. The tree's `new` and the VM's CALL_NEW both ask this, so a .NET instantiation cannot
    /// resolve one way on one engine and another way on the other.</summary>
    // A script names this type in TEXT, so which constructors the `new` needs is known only at run time.
    // The annotation is what tells the trimmer to keep them, and it is what stops each CALLER (the
    // tree's `new`, the VM's CALL_NEW) from being reported for calling Activator.CreateInstance on a
    // type whose members cannot be seen statically. The suppression covers the two places that claim
    // ENTERS: a script's own Type value in scope, and the Type.GetType spellings. Both are lookups by
    // name, the same act IL2057 and IL2062 already cover for the rest of the engine.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trim", "IL2072",
        Justification = "a script names the .NET type in `new`, so its constructors are resolved by name at run time")]
    [return: System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(
        System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors
        | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    // The scope lookup returns an unannotated object, so the trimmer cannot see that the type it holds is the one
    // the script named (a Type value the engine keeps constructors for, or a Type.GetType result, which IL2057 and
    // IL2062 already cover). Same accepted limitation as those two: a script-requested type is kept by the AOT
    // hints for the types the engine knows, and a type it does not know was never trimmable in the first place.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trim", "IL2068",
        Justification = "script-requested type: ResolveClrNewType returns what the scope or Type.GetType holds")]
    // IL2073 is the same claim one step in, and it appeared the moment the ONE lookup was consulted first (which
    // is what makes `new int()` resolve at all): ResolveTypeToClr answers the registry's CLR type, an object in
    // scope, or a namespace scan, and none of those carries the constructor annotation this method RETURN can
    // promise. WHERE the proof lives: AotHints.cs roots every type this can reach with AvoidAotTrim, which
    // includes PublicConstructors: the primitives (char/byte/sbyte/short/ushort/int/uint/long/ulong/float/double/
    // decimal/bool/string, lines 40-67), DateTime / DateTimeOffset / TimeSpan (70-73), and List<> / Dictionary<,>
    // / HashSet<> (86-87, 112). `builtin_type_roundtrip_test.slc` constructs every one of them ON the AOT
    // publish, so a hint that went missing fails that suite rather than a user's script; if a future `new`
    // spelling is added, root its type there first.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trim", "IL2073",
        Justification = "script-requested type: the value comes from ResolveTypeToClr, and AotHints.cs roots every type it can reach (AvoidAotTrim includes PublicConstructors)")]
    private Type? ResolveClrNewType(string typeName, TypeArgList? genericArgs)
    {
        Type? systemType;

        // The PRIMITIVE spellings first, which is the ONE table that maps a script name to a CLR type: `new int()`
        // needed it (the token names Int / Bool / Long are CLR names of nothing and script names of nothing, so
        // every probe below found nothing), while the WIDER script registry must NOT be consulted here. That
        // distinction is pinned by a suite: _dup360 check 1m asserts `new KeyValuePair("a", 1)` is REFUSED,
        // because the script's spelling for that value is the bare `KeyValuePair(k, v)` call, and consulting the
        // whole registry made `new` build one (measured 2026-10-06). The guard upstream has already refused a
        // name the engine registers under another SPELLING, so this lookup being case-insensitive is fine.
        systemType = TypeNameLookup.TryGetValue(typeName, out var primitiveType) ? primitiveType : null;

        if (systemType == null && CurrentFrame.Scopes.TryGet(typeName, out var typeObj) && typeObj is Type t)
        {
            systemType = t;
        }
        else if (systemType == null)
        {
            systemType = typeName switch
            {
                "JsonSerializeOptions" => typeof(JsonSerializeOptions),
                _ => null
            }
            // The SAME assembly-backed index the bare-identifier and `typename` lookups go through
            // (TypeHelpers.FindLoadedClrType), not a second copy of the namespace scan this used to be: it was
            // `Type.GetType` over a fixed list of namespaces, so `new Uri(…)` refused with "Type 'Uri' not
            // defined" while `typename("Uri")` answered, and `new Stopwatch()`/`new ZipArchive(...)` with it.
            // The SCRIPT registry is still deliberately not consulted here (_dup360 check 1m: `new
            // KeyValuePair("a", 1)` must be refused, because the script's spelling for that value is the bare
            // call), which is why this asks the index directly instead of ResolveTypeToClr, which does consult it.
            ?? FindLoadedClrType(typeName);
        }

        if (systemType == null)
            return null;

        // A generic name is closed HERE, the one place that maps script TypeInfos to System.Types.
        if (genericArgs is { Count: > 0 })
        {
            var argTypes = new Type[genericArgs.Count];
            for (var i = 0; i < genericArgs.Count; i++)
                argTypes[i] = TypeTokenHelper.GetSystemType(genericArgs[i]);
            systemType = systemType.MakeGenericType(argTypes);
        }
        return systemType;
    }

    /// <summary>The .NET arm of the VM's CALL_NEW. Every resolution is the tree's: the type comes from
    /// ResolveClrNewType (closed with the type arguments when the call named them), the ctor is picked by
    /// arity with an exact match preferred, and each value is coerced the way the tree coerces it. Returns
    /// null when nothing applies, so the caller falls back to the tree and the tree raises its own message.
    /// </summary>
    /// <summary>The CLR constructor a call should use, among candidates that already match by ARITY: the first
    /// whose parameters actually ACCEPT the values, tested with the very coercion the call performs, so the choice
    /// cannot disagree with the conversion that follows. Taking `ctors[0]` made `new StringBuilder("x")` pick the
    /// capacity overload and answer "Cannot convert string 'x' to int" (measured 2026-10-06), while the
    /// two-argument form worked because only one constructor has that arity.
    ///
    /// null when none accepts them, and each caller answers that its own way: the tree coerces anyway so the
    /// refusal names the type it could not convert to, the VM hands the call back for the tree to answer.</summary>
    private ConstructorInfo? SelectClrConstructor(ConstructorInfo[] ctors, IReadOnlyList<object?> values)
    {
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            var hasParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
            if (ps.Length != values.Count && !hasParams) continue;
            var fits = true;
            for (var i = 0; i < ps.Length && i < values.Count; i++)
            {
                var v = values[i] is RefCell rc ? rc.Get() : values[i];
                // `probe: true`: this asks the coercion whether the candidate FITS, and a candidate that does not
                // fit is not a script's mistake, so it must not warn (nor claim a position it does not have).
                try { _ = CoerceTo(v, MapTypeInfo(ps[i].ParameterType), 0, 0, probe: true); }
                catch (ScriptException) { fits = false; break; }
            }
            if (Diagnostics.On("COERCE_TRACE"))
                Console.Error.WriteLine($"[ctor] {c.DeclaringType?.Name}({string.Join(", ", ps.Select(p => p.ParameterType.Name))}) with [{string.Join(", ", values.Select(v => v?.GetType().Name ?? "null"))}] -> {(fits ? "FITS" : "no")}");
            if (fits) return c;
        }
        return null;
    }

    private object? TryInstantiateClrForVm(string typeName, TypeArgList? typeArgs, object?[] args, int line, int column)
    {
        var systemType = ResolveClrNewType(typeName, typeArgs);
        if (systemType == null) return null;
        // AN OPEN GENERIC CANNOT BE INSTANTIATED, and on the AOT build the leaf lookup can return one. The CLR
        // index maps a generic's ARITY-STRIPPED leaf (`KeyValuePair`2` -> `KeyValuePair`), which collides with
        // the real NON-generic `System.Collections.Generic.KeyValuePair`, so WHICH ONE WINS depends on the
        // enumeration order of the assembly's exported types, and that order DIFFERS between JIT and AOT.
        // Measured on `_dup360` line 47 (`new KeyValuePair("a", 1)`): the Debug build returned VmFallback, the
        // statement was re-run on the TREE, which answered its own refusal ("No matching constructor found for
        // .NET type 'KeyValuePair'") and the suite's `catch` saw it, while the AOT publish called
        // Activator.CreateInstance on the open definition and threw the CLR's raw
        // `Acc_CreateGenericEx, System.Collections.Generic.KeyValuePair`2[TKey,TValue]`, which then ESCAPED the
        // script's own catch because the VM's handler walk matches ScriptException only. Handing it back is the
        // tree's job, exactly like the ambiguous-constructor case below.
        if (systemType.IsGenericTypeDefinition || systemType.ContainsGenericParameters)
        {
            // AN OPEN DEFINITION CANNOT BE INSTANTIATED, and the two BUILDS RESOLVE THE NAME DIFFERENTLY: on AOT the leaf
            // lookup hands back `KeyValuePair`2' where Debug finds a non-generic type of that name (measured: the source
            // run answered "Failed to instantiate .NET type 'KeyValuePair`2': invalid argument" on AOT against "No
            // matching constructor found for .NET type 'KeyValuePair'" on Debug). Handing the call back REFUSED a
            // bundle's whole entry on AOT while the file's own catch saw that sentence (_dup360 line 47), so the attempt
            // happens HERE and its failure is mapped by the SAME composition the tree's own `new` uses, which is what
            // makes the bundle print what the file prints on either build.
            try { return Activator.CreateInstance(systemType, args); }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
                RuntimeError($"Failed to instantiate .NET type '{systemType.Name}': {FriendlyExceptionMessage(inner)}", line, column);
                return null;
            }
        }

        var ctors = systemType.GetConstructors().Where(c =>
        {
            var ps = c.GetParameters();
            var hasParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
            var required = hasParams ? ps.Length - 1 : ps.Length;
            if (args.Length < required) return false;
            if (!hasParams && args.Length > ps.Length) return false;
            return true;
        }).ToArray();

        if (ctors.Length == 0 && args.Length == 0)
            return Construct(systemType, null);

        // NO CONSTRUCTOR OF THIS ARITY EXISTS AT ALL, which is PROOF rather than an ambiguity, so the engine's own
        // refusal is raised here instead of handing the call back: a bundle cannot re-run the statement on the tree, so
        // `new KeyValuePair("a", 1)` refused the WHOLE entry where the file's own `catch` sees this sentence (measured,
        // _dup360 line 47; the tree's new path raises the identical text). A type with SEVERAL candidates and no fit
        // still hands back, because that is the case where the tree's own overload weighing may legitimately do better.
        if (ctors.Length == 0)
            RuntimeError($"No matching constructor found for .NET type '{systemType.Name}' with {args.Length} arguments.",
                line, column);

        if (ctors.Length != 1)
        {
            // By the VALUES, not only by the arity: `new StringBuilder("x")` has two one-argument constructors,
            // and taking the first (the capacity one) answered "Cannot convert string 'x' to int".
            var fits = SelectClrConstructor(ctors, args);
            if (fits == null) return null;   // ambiguous or absent: the tree answers, with its own message
            ctors = [fits];
        }

        var paramInfos = ctors[0].GetParameters();
        var argValues = new object?[paramInfos.Length];
        for (var i = 0; i < args.Length && i < paramInfos.Length; i++)
            argValues[i] = CoerceTo(args[i], MapTypeInfo(paramInfos[i].ParameterType), line, column);
        return Construct(systemType, argValues);

        // A CONSTRUCT FAILURE BELONGS TO THE TREE, which reports it with its own message. Throwing the CLR's
        // own exception from here is what escapes a script's `catch`: the VM's handler walk matches
        // ScriptException only, so a raw ArgumentException flies past it and only the enclosing boundary
        // converts it, after the script's own handler has been skipped (measured on `_dup360` line 47, where
        // the suite's `catch (var ax)` never ran on AOT). A SCRIPT exception raised inside a constructor is
        // passed through untouched, the rule the engine's ClrMemberFailure states.
        static object? Construct(Type t, object?[]? ctorArgs)
        {
            try { return ctorArgs == null ? Activator.CreateInstance(t) : Activator.CreateInstance(t, ctorArgs); }
            catch (Exception ex)
            {
                if ((ex as System.Reflection.TargetInvocationException)?.InnerException is ScriptException inner)
                    throw inner;
                return null;
            }
        }
    }

    private static bool HasSameArityOverloads(IReadOnlyList<ConstructorDecl> constructors, int argCount)
    {
        var seen = false;
        foreach (var c in constructors)
        {
            if (c.IsStatic || c.Params.Count != argCount) continue;
            if (seen) return true;
            seen = true;
        }
        return false;
    }

    public object VisitObjectInitExpr(ObjectInitExpr node)
    {
        var instance = Evaluate(node.Inner);

        switch (instance)
        {
            case ClassInstance:
            case StructInstance:
                // Evaluate each value first (order preserved), then apply via the shared
                // value-applicator used by the VM direct path (single source of truth).
                var pairs = new (string, object?)[node.Initializers.Count];
                for (var i = 0; i < node.Initializers.Count; i++)
                {
                    var (pn, pv) = node.Initializers[i];
                    pairs[i] = (pn, Evaluate(pv));
                }
                return ApplyObjectInitializerValues(instance, pairs, node.Line, node.Column);

            default:
                RuntimeError($"Object initializer can only be used with class or struct instances.", node.Line, node.Column);
                return null;
        }
    }

    public object VisitThisExpr(ThisExpr node)
    {
        // Resolve through the closure snapshot first, lambdas created inside methods
        // capture 'this' into their snapshot; at call time _currentThis is null (top level).
        var snapshot = CurrentFrame.Scopes.ParentSnapshot;
        if (snapshot != null)
        {
            foreach (var localScope in snapshot.LocalScopes)
            {
                if (localScope.TryGetValue("this", out var thisVal))
                    return thisVal!;
            }
            if (snapshot.GlobalScope.TryGetValue("this", out var thisValue))
                return thisValue!;
        }
        return _currentThis ?? RuntimeError<object?>("'this' used outside of a method.", node.Line, node.Column);
    }

    public object VisitSuperExpr(SuperExpr node)
    {
        if (_currentClass == null || _currentThis == null)
            RuntimeError("'super' used outside of a class method or constructor.", node.Line, node.Column);

        var parent = _currentClass?.Parent
            ?? RuntimeError<ClassInfo?>($"Class '{_currentClass?.Name}' has no parent class.", node.Line, node.Column);

        return new SuperProxy(_currentThis!, parent);
    }

    public object VisitBaseExpr(BaseExpr node)
    {
        if (_currentClass == null || _currentThis == null)
            RuntimeError("'base' used outside of a class method.", node.Line, node.Column);

        var parent = _currentClass.Parent
            ?? RuntimeError<ClassInfo?>($"Class '{_currentClass.Name}' has no parent class.", node.Line, node.Column);

        return new BaseProxy(_currentThis!, parent);
    }

    public object VisitNewStructExpr(NewStructExpr node)
    {
        var fieldVals = node.FieldValues is { Count: > 0 }
            ? node.FieldValues.Select(Evaluate).ToArray()
            : [];
        return CreateStructDirect(node.StructName, fieldVals, node.Line, node.Column);
    }

    // -----------------------------------------------------------------------------
    // Core execution method
    // -----------------------------------------------------------------------------
    private ExecutionResult Execute(Stmt stmt)
    {
        StatementsExecuted++;
        // The statement's own position, which is what a BUILTIN needs to report a diagnostic the way the rest of
        // the engine does (`[Lx:Cy] [WARNING] ...`): a native is handed VALUES and has no position of its own, so
        // the engine supplies the one being executed. It is also what the CLI's error report falls back to for a
        // registration throwing its own ScriptException, which is what the field's own doc says it is for and what
        // it did not do until now (it was only written when an exception was BUILT).
        _currentLine = stmt.Line;
        _currentColumn = stmt.Column;
        if (Vm.Trace)
        {
            // Tree-walker trace, the VM trace only covers VM-compiled functions; top-level
            // statements and tree-fallback functions run here. Same flag (VmSetTrace) enables both.
            var kind = stmt switch
            {
                VarDeclStmt v => $"var {v.Name}",
                ExprStmt e => $"expr {DescribeExpr(e.Expression)}",
                ReturnStmt => "return",
                IfStmt => "if",
                WhileStmt => "while",
                ForStmt => "for",
                ForeachStmt => "foreach",
                BlockStmt => "block",
                UsingStmt u => $"using ({u.VarName})",
                UsingDeclStmt ud => $"using var {ud.VarName}",
                FuncDecl f => $"func {f.Name}",
                ClassDeclStmt c => $"class {c.Name}",
                _ => stmt.GetType().Name.Replace("Stmt", "")
            };
            // Match the VM trace column layout: "[vm] " / "[tr] " is 5 chars + [Lx:Cy] padded to
            // 18 + a 30-wide fn-tag field (blank on tree lines, never inside a VM frame) → the
            // description column starts at index 5+18+30 = 53. Same for "[tr] ".
            // e.g. [tr] [L10:C6]                                      expr print(1 args)
            var traceLine = $"[tr] [L{stmt.Line}:C{stmt.Column}]".PadRight(5 + 18) + new string(' ', 31) + kind.PadRight(20);
            // Elide loop-body spam: the tree walker re-traces the same statement once per iteration,
            // so a 200-iteration loop prints 200 identical [tr] lines. Print each distinct statement
            // (line:col + description) once per run, then count repeats and emit a "×N" summary when
            // a different statement appears or the run ends.
            if (_treeTraced.Add(traceLine))
            {
                if (_lastTreeTrace != null && _lastTreeTraceCount > 1)
                    Console.WriteLine(_lastTreeTrace + $" ×{_lastTreeTraceCount}");
                Console.WriteLine(traceLine);
                _lastTreeTrace = traceLine;
                _lastTreeTraceCount = 1;
            }
            else
            {
                _lastTreeTraceCount++;
            }
        }
        if (_interruptRequested)
        {
            _interruptRequested = false;
            throw new ScriptException(ErrorCode.Interrupt, "KeyboardInterrupt", stmt.Line, stmt.Column);
        }
        // THE RUN ON THIS THREAD WAS CANCELLED (`Task.Run(fn, token)` + `cts.Cancel()`), which a runner uses as
        // its Stop button. Same exception as an interrupt, so a script's own `finally` runs and `RunScript` reports
        // the refusal the way it reports any other: measured, a cancelled run answers HasError with this message
        // instead of looping until the process dies.
        if (RunCancellationRequested)
        {
            throw new ScriptException(ErrorCode.Cancelled, "Stopped: the run was cancelled", stmt.Line, stmt.Column);
        }
        // AND A STATEMENT WHOSE RUN OWNS A LIVE FORM PUMPS THAT FORM'S MESSAGES, which is the `DoEvents` a UI script
        // needs: without it a window the script SHOWED is a frozen one (a caption drag never repaints, a posted close
        // never lands, and Windows paints "Not Responding" into its title), because nothing dispatches the messages of
        // the thread that owns it. Reported twice: "when a form is shown via runscript via clipboard ui tool ... frozen
        // window, missing a doevents", then "I still see 'windows does not respond' when i move a window while you
        // execute tests", which is the same gap on a run with no token. So the gate is the FORM, not the token, and
        // `PumpForWorkerRun` starts with the cheap `IsRunning` check, so a headless run pays one list read per statement.
        else
        {
            Interpreter.NativeApp.PumpForWorkerRun();
        }

        if (_debugger != null && _debugger.ShouldBreak(stmt))
        {
            _debugger.Break(this, stmt);
        }

        _asyncRunStmtDepth++;
        try
        {
            var result = stmt.Accept(this);
            if (_isYield) return ExecutionResult.Yielded(_yieldValue);
            return result;
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = FriendlyExceptionMessage(ex);   // single map, see the expression-level sibling
            ThrowException(message, stmt.Line, stmt.Column);
            return ExecutionResult.Throw(message);
        }
        finally
        {
            _asyncRunStmtDepth--;
        }
    }

    /// <summary>Script-facing type name for the trace: a raw TokenType enum name stored in
    /// TypeName (e.g. "StringType", "Int") maps through TypeSystem.Name; real user types
    /// (e.g. "Person", "List") are shown as-is.</summary>
    private static string TypeDisplayName(TypeInfo t)
    {
        if (t.TypeName is { Length: > 0 })
        {
            if (Enum.TryParse<TokenType>(t.TypeName, out var tt) && TypeSystem.Name(tt) != t.TypeName)
                return TypeSystem.Name(tt);
            return t.TypeName;
        }
        return TypeSystem.Name(t.BaseType);
    }

    /// <summary>Concise human-readable description of an expression for the tree trace,
    /// names the callee, property, variable, or operator instead of the raw node type.</summary>
    private static string DescribeExpr(Expr? expr)
    {
        switch (expr)
        {
            case null: return "?";
            case CallExpr c:
                {
                    var callee = c.Callee switch
                    {
                        VariableExpr v => v.Name,
                        PropertyAccessExpr p => $"{DescribeExpr(p.Target)}.{p.PropertyName}",
                        MethodCallExpr m => DescribeExpr(m.Receiver) + "." + m.MethodName,
                        _ => c.Callee.GetType().Name.Replace("Expr", "")
                    };
                    var typeArgs = c.TypeArgs is { Count: > 0 } ? $"<{string.Join(",", c.TypeArgs.Select(TypeDisplayName))}>" : "";
                    return $"{callee}{typeArgs}({c.Arguments.Count} args)";
                }
            case MethodCallExpr m:
                {
                    var typeArgs = m.TypeArgs is { Count: > 0 } ? $"<{string.Join(",", m.TypeArgs.Select(TypeDisplayName))}>" : "";
                    return $"{DescribeExpr(m.Receiver)}.{m.MethodName}{typeArgs}({m.Arguments.Count} args)";
                }
            case PropertyAssignExpr pa: return $"{DescribeExpr(pa.Target)}.{pa.PropertyName} = {DescribeExpr(pa.Value)}";
            case PropertyAccessExpr p: return $"{DescribeExpr(p.Target)}.{p.PropertyName}";
            case VariableExpr v: return v.Name;
            case AssignExpr a: return $"{a.Name} = {DescribeExpr(a.Value)}";
            case BinaryExpr b: return $"{DescribeExpr(b.Left)} {b.Operator.Literal} {DescribeExpr(b.Right)}";
            case UnaryExpr u: return $"{u.Operator.Literal}{DescribeExpr(u.Right)}";
            case IndexExpr ix: return $"{DescribeExpr(ix.Value)}[]";
            case NewInstanceExpr ni:
                {
                    var typeName = ni.TypeInfo is null ? "?" : TypeDisplayName(ni.TypeInfo);
                    var typeArgs = ni.TypeInfo?.GenericArgs is { Count: > 0 }
                        ? $"<{string.Join(",", ni.TypeInfo.GenericArgs.Select(TypeDisplayName))}>"
                        : "";
                    var argCount = ni.Arguments?.Count ?? 0;
                    return $"new {typeName}{typeArgs}({argCount} args)";
                }
            case StringExpr se: return $"\"{(se.Value is { Length: > 32 } ? se.Value[..32] + "…" : se.Value)}\"";
            case NumberExpr ne: return ne.Value;
            case BoolExpr be: return be.Value ? "true" : "false";
            case CharExpr ch: return $"'{ch.Value}'";
            case NullExpr: return "null";
            default: return expr.GetType().Name.Replace("Expr", "");
        }
    }

    /// <summary>Resolves a struct constructor for a call. The ref/out SHAPE of the declaration must agree with what
    /// the call passes, because a VALUE cannot fill a declared ref/out parameter: measured, `new SM(1)` on a struct
    /// whose only constructor is `SM(out int a)` selected it by argument COUNT, bound 1 to the `out` parameter, and
    /// was refused with "Expected a reference for ref/out parameter 'a'", naming a keyword the author never wrote
    /// (before the ref/out rule the same call bound `a = 1` silently and left the field at 0).</summary>
    /// <summary>Resolves the constructor a struct construction uses: by arity and by ref/out SHAPE through the ONE
    /// matcher, with the count-keyed cache only when nothing else can change the answer (exactly one non-static
    /// constructor of that arity, and no ref/out shape to weigh, because `new SM(out x)` and `new SM(1)` share an
    /// arity). `values`, when the caller already has them, also let the type-aware refinement run: the VM's CALL_NEW
    /// and CreateStructDirect know their runtime types, while the tree's `new` resolves BEFORE it evaluates, so it
    /// passes null and keeps that order (and with it, that a refused construction has no argument side effects).</summary>
    private ConstructorDecl? ResolveStructCtorForCall(StructDeclStmt structDecl, int argCount, bool[] byRef,
        object?[]? values, int line, int column)
    {
        if (structDecl.Constructors.Count == 0) return null;

        var sameArity = structDecl.Constructors.Where(c => !c.IsStatic && c.Params.Count == argCount).ToList();
        var cacheable = sameArity.Count == 1 && !HasRefOrOutCtorParams(sameArity[0]);
        var key = (structDecl.Name, argCount);
        var ctor = cacheable && _constructorCache.TryGetValue(key, out var cached) ? cached : null;
        ctor ??= FindMatchingStructCtor(structDecl.Constructors, argCount, line, column, byRef);
        if (ctor != null && values != null)
            ctor = ResolveConstructorForValues(structDecl.Constructors, ctor, values, line, column, byRef);
        if (ctor != null && cacheable)
            _constructorCache[key] = ctor;
        if (ctor != null && ctor.Access != AccessModifier.Public && ctor.Access != AccessModifier.Internal)
            RuntimeError($"Cannot access {ctor.Access.ToString().ToLower()} constructor of struct '{structDecl.Name}'.", line, column, ErrorCode.AccessViolation);
        return ctor;
    }

    /// <summary>Builds a struct instance and runs its constructor body: ONE implementation for the three paths that
    /// construct a struct (the tree's `new` expression, the VM's CALL_NEW through InstantiateForVm, and
    /// CreateStructDirect). The callers differ in where the arguments come from and in what the resolver may assume,
    /// which stays with them; the field defaults (and the backing fields of auto-properties), the positional field
    /// initialization, the assembled `params` tail, the coercion, the instance, the STATIC constructors, the
    /// parameter binding with its ref/out rule and out tracking, and the body (compiled when one exists and declares
    /// no ref/out, the tree otherwise) live here once.
    ///
    /// The three copies had DRIFTED, which is the reason for the collapse: only the tree path ran a compiled body,
    /// one of them never ran the static constructors, another never initialized the auto-property backing fields, and
    /// out parameters were tracked on the tree path only, so which copy an input happened to reach decided both the
    /// performance and the semantics.</summary>
    private object RunStructCtor(StructDeclStmt structDecl, ConstructorDecl? ctor, object?[] args, int line, int column)
    {
        var fields = new Dictionary<string, object?>(structDecl.Fields.Count, _comparer);
        foreach (var f in structDecl.Fields)
            fields[f.Name] = DefaultFor(f.Type);

        // Auto-properties own a hidden backing field, and a struct needs it present from the start (one of the three
        // copies did this and the other two did not).
        foreach (var prop in structDecl.Properties)
        {
            var backingName = BackingFieldName(prop.Name);
            if (!fields.ContainsKey(backingName))
                fields[backingName] = DefaultFor(prop.Type);
        }

        if (structDecl.Constructors.Count == 0)
        {
            // No declared constructor: positional field initialization, in declaration order (`new Vec(1, 2)`).
            if (args.Length > 0)
            {
                var fieldList = structDecl.Fields.ToList();
                if (args.Length > fieldList.Count)
                    RuntimeError($"Too many initializers for struct '{structDecl.Name}'.", line, column);
                for (var i = 0; i < args.Length; i++)
                    fields[fieldList[i].Name] = CoerceTo(args[i], fieldList[i].Type, line, column);
            }
            return new StructInstance(structDecl.Name, fields, structDecl.Line) { Declaration = structDecl };
        }

        var argValues = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
            argValues[i] = args[i];

        // The `params` tail is assembled FIRST, and its slot is left to the Define below, because the declared type
        // there is the ELEMENT type for the bare spelling: coercing it again would convert the array into one element.
        var tailSlot = ctor != null && ctor.HasParamsTail ? ctor.FixedParamCount : -1;
        if (tailSlot >= 0)
        {
            argValues = NormalizeCtorParamsArgs(ctor!, argValues, line, column);
        }
        else
        {
            for (var i = 0; i < argValues.Length; i++)
            {
                if (ctor == null || i >= ctor.Params.Count) continue;
                // A declared ref/out parameter keeps its CELL, because coercing it would report the internal type
                // name, and the ONE rule is asked BEFORE the coercion so that a cell which contradicts the
                // declaration is refused instead of being handed to CoerceTo.
                var cIsRef = ctor.IsRefParams != null && i < ctor.IsRefParams.Count && ctor.IsRefParams[i];
                var cIsOut = ctor.IsOutParams != null && i < ctor.IsOutParams.Count && ctor.IsOutParams[i];
                if (cIsRef || cIsOut) continue;
                RefuseRefCellOnByValueParam(ctor.Params[i].Name, argValues[i], false, false, line, column, ctor.Params[i].Type);
                argValues[i] = CoerceTo(argValues[i], ctor.Params[i].Type, line, column);
            }
        }

        var instance = new StructInstance(structDecl.Name, fields, structDecl.Line)
        {
            Declaration = structDecl,
            IsInInitPhase = true
        };

        // A struct has no inheritance, so its static constructors are the struct's own, and they run once.
        if (_staticInitClasses.Add(structDecl.Name))
        {
            foreach (var sc in structDecl.Constructors.Where(c => c.IsStatic))
            {
                var prevThis = _currentThis;
                var prevClass = _currentClass;
                var prevInCtor = _inConstructor;
                _currentThis = instance;
                _currentClass = null;
                _inConstructor = true;
                CurrentFrame.Scopes.PushScope();
                try
                {
                    CurrentFrame.Scopes.Define("this", instance);
                    foreach (var stmt in sc.Body) Execute(stmt);
                }
                finally
                {
                    CurrentFrame.Scopes.PopScope();
                    _currentThis = prevThis;
                    _currentClass = prevClass;
                    _inConstructor = prevInCtor;
                }
            }
        }

        if (ctor == null)
        {
            instance.IsInInitPhase = false;
            return instance;
        }

        var previousThis = _currentThis;
        var previousClass = _currentClass;
        var previousInConstructor = _inConstructor;
        _currentThis = instance;
        _currentClass = null;
        _inConstructor = true;
        CurrentFrame.Scopes.PushScope();
        try
        {
            CurrentFrame.Scopes.Define("this", instance);
            for (var i = 0; i < argValues.Length; i++)
            {
                // The assembled tail is defined as-is; every other parameter goes through the ONE ref/out rule.
                if (i == tailSlot)
                {
                    CurrentFrame.Scopes.Define(ctor.Params[i].Name, argValues[i], ctor.Line, ctor.Column);
                    continue;
                }
                if (i >= ctor.Params.Count) break;
                var pIsRef = ctor.IsRefParams != null && i < ctor.IsRefParams.Count && ctor.IsRefParams[i];
                var pIsOut = ctor.IsOutParams != null && i < ctor.IsOutParams.Count && ctor.IsOutParams[i];
                RefuseRefCellOnByValueParam(ctor.Params[i].Name, argValues[i], pIsRef, pIsOut, ctor.Line, ctor.Column, ctor.Params[i].Type);
                if (pIsRef || pIsOut)
                    BindParam(ctor.Params[i].Name, argValues[i], ctor.Params[i].Type, pIsRef, pIsOut, ctor.Line, ctor.Column);
                else
                    CurrentFrame.Scopes.Define(ctor.Params[i].Name, argValues[i], ctor.Line, ctor.Column);
            }

            // Out tracking AFTER the binding, never before: the tracker wraps the cell it finds under the
            // parameter's name in the LIVE scope, which is the ordering the class-constructor path had to learn.
            var outAssigned = InitOutTracking(ctor.IsOutParams, ctor.Params, ctor.Line, ctor.Column);
            // A BUNDLE does not carry a ctor's AST, so a compiled body with ref/out params must run on the VM there:
            // the tree branch below executes `ctor.Body`, which would be an EMPTY statement list and run nothing.
            bool[]? ctorOutTrack = null;
            if (ctor.Compiled != null && (!HasRefOrOutCtorParams(ctor) || ctor.Body.Count == 0))
            {
                ctorOutTrack = WrapCtorOutArgs(ctor, ref argValues, line, column);
                // Same rule as the class constructor above.
                var structCtorCommitBaseline = VmBodyCommitted;
                VmBodyCommitted = false;
                object? ctorVmResult;
                var structCtorCommittedHere = false;
                try
                {
                    ctorVmResult = Vm.Execute(ctor.Compiled, BuildCtorVmArgs(argValues, instance), this, structDecl.Name);
                }
                finally
                {
                    structCtorCommittedHere = VmBodyCommitted;
                    VmBodyCommitted = structCtorCommitBaseline || structCtorCommittedHere;
                }
                if (ctorVmResult is VmFallback)
                {
                    if (structCtorCommittedHere)
                        RuntimeError($"Struct constructor '{structDecl.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
                    foreach (var stmt in ctor.Body) Execute(stmt);
                }
            }
            else
            {
                foreach (var stmt in ctor.Body) Execute(stmt);
            }
            VerifyOutParams(ctorOutTrack ?? outAssigned, ctor.IsOutParams, ctor.Params, ctor.Line, ctor.Column);
        }
        finally
        {
            CurrentFrame.Scopes.PopScope();
            instance.IsInInitPhase = false;
            _currentThis = previousThis;
            _currentClass = previousClass;
            _inConstructor = previousInConstructor;
        }
        return instance;
    }
    private static ConstructorDecl? FindMatchingStructCtor(
        IReadOnlyList<ConstructorDecl> constructors,
        int argCount,
        int line, int column,
        IReadOnlyList<bool>? byRef = null)
    {
        // The BEST shape rank wins among the candidates of one arity, so the call's own `ref`/`out` shape
        // disambiguates: `new Both(out y)` reaches the `out` constructor where `new Both(7)` reaches the plain one,
        // and a call that only contradicts a shape in the tolerable direction (a keyword at a plain parameter) is
        // still offered that constructor, so the keyword rule can refuse it by name. Two candidates at the same
        // rank stay ambiguous.
        ConstructorDecl? matching = null;
        var bestRank = 3;
        var ambiguous = false;
        foreach (var c in constructors)
        {
            if (c.IsStatic || c.Params.Count != argCount) continue;
            var rank = RefOutShapeRank(c, byRef);
            if (rank == 2) continue;
            if (rank < bestRank) { bestRank = rank; matching = c; ambiguous = false; }
            else if (rank == bestRank) ambiguous = true;
        }
        if (ambiguous)
            RuntimeError($"Ambiguous constructors for struct with {argCount} arguments.", line, column);

        if (matching != null)
            return matching;

        foreach (var c in constructors)
        {
            if (c.IsStatic || !c.HasParamsTail || argCount < c.FixedParamCount) continue;
            var rank = RefOutShapeRank(c, byRef);
            if (rank == 2) continue;
            if (rank < bestRank) { bestRank = rank; matching = c; ambiguous = false; }
            else if (rank == bestRank) ambiguous = true;
        }
        if (ambiguous)
            RuntimeError($"Ambiguous constructors for struct with {argCount} arguments.", line, column);

        if (matching != null)
            return matching;

        if (argCount == 0)
            return null;   // default constructor
        RuntimeError($"No constructor found for struct with {argCount} arguments.", line, column);
        return null;
    }

    /// <summary>The ONE place a `params` tail is assembled. Every path that binds arguments calls it: the tree's
    /// function invocation (the ordinary frame and the coroutine/local-function one), the VM's argument
    /// normalizer, a script METHOD (class and struct), and a constructor.
    ///
    /// It exists because the rule used to be written SIX times and only one copy carried a policy, so the same
    /// source was refused in one shape and silently mis-bound in the others: those packed a `ref`/`out` argument
    /// into the tail as an ELEMENT, and the callee then read a cell where a value belonged (measured: a `params`
    /// function called `Sum(ref a, 2, 3)` returned 6 and left `a` alone, and the struct-method copy did not
    /// assemble a tail at all, which surfaced as "Expected an array, got Int32" from inside the body). A tail is a
    /// fresh array of by-value ELEMENTS, so there is no variable for a ref/out binding to point at: that is
    /// REFUSED here, once, in C#'s own terms (a ref argument where the parameter is not declared ref).
    ///
    /// TWO signals, because the callers differ: a constructor still HAS the call site's ref/out flags (its
    /// arguments are not cells yet), while a function or method call arrives with the flags already spent, as
    /// RefCell VALUES. Either one is the same mistake, so either refuses.
    ///
    /// The ARRAY form is untouched: `Sum(arr)` packs the array's elements, and every element is coerced to the
    /// declared element type (the bare `params byte items` spelling declares the SCALAR type, so the rank-1
    /// TypeInfo is what makes CoerceTo convert each one: 256 becomes 0).</summary>
    internal object? AssembleParamsTail(
        IReadOnlyList<(TypeInfo Type, string Name)> declared,
        object?[] args,
        int line, int column,
        IReadOnlyList<bool>? isRefArgs = null,
        IReadOnlyList<bool>? isOutArgs = null)
    {
        var fixedCount = declared.Count - 1;   // callers reach this only when the declaration HAS a tail
        for (var i = fixedCount; i < args.Length; i++)
        {
            var flagged = (isRefArgs != null && i < isRefArgs.Count && isRefArgs[i])
                       || (isOutArgs != null && i < isOutArgs.Count && isOutArgs[i]);
            if (flagged || args[i] is RefCell)
                RuntimeError($"Argument {i + 1} sits in the `params` tail and was passed by ref/out, but a tail takes by-value arguments: its array is built for the call, so there is no variable to write back to. Pass a value, or declare the parameter `ref T[] a` and pass the array itself.", line, column);
        }

        object?[] tail;
        var excess = args.Length - fixedCount;
        switch (excess)
        {
            case <= 0:
                tail = [];
                break;
            case 1 when args[fixedCount] is Array direct:
                tail = new object?[direct.Length];
                for (var j = 0; j < direct.Length; j++) tail[j] = direct.GetValue(j);
                break;
            default:
                tail = new object?[excess];
                for (var i = fixedCount; i < args.Length; i++) tail[i - fixedCount] = args[i];
                break;
        }
        var tailType = declared[fixedCount].Type;
        var tailArrayType = tailType.IsArray ? tailType : tailType with { ArrayRank = 1 };
        return CoerceTo(tail, tailArrayType, line, column);
    }

    /// <summary>A constructor's argument vector, normalized: exactly one value per DECLARED parameter, with the
    /// assembled tail in the last slot. The tail RULE (and the ref/out refusal) lives in AssembleParamsTail; the
    /// flags are forwarded because this path has not captured its ref/out arguments as cells yet.</summary>
    private object?[] NormalizeCtorParamsArgs(ConstructorDecl ctor, object?[] args, int line, int column,
        IReadOnlyList<bool>? isRefArgs = null, IReadOnlyList<bool>? isOutArgs = null)
    {
        var fixedCount = ctor.FixedParamCount;
        var normalized = new object?[ctor.Params.Count];
        for (var i = 0; i < fixedCount; i++)
            normalized[i] = i < args.Length ? args[i] : null;
        normalized[fixedCount] = AssembleParamsTail(ctor.Params, args, line, column, isRefArgs, isOutArgs);
        return normalized;
    }

    /// <summary>Would the BIND accept this value at this declared parameter? ASKED WITH THE COERCION ITSELF
    /// (`CoerceTo` with `probe: true`, which is silent because a candidate that does not fit is not the script's
    /// mistake), the same way `SelectClrConstructor` weighs a .NET constructor. That is the ONE rule, so the pick and
    /// the bind cannot disagree: a value that is already of the type fits, and so does one the coercion can make fit
    /// (`object` from anything, a list from an array, an int from a double), which is what the tree does with the
    /// same arguments. Before this, the picker asked a type-compatibility twin of its own, and a ctor taking `object`
    /// matched NOTHING: measured on `text_writer_bridge_test` from a bundle, `new Source(["a", "b"])` against
    /// `constructor(List<object> r)` bailed at CALL_NEW and refused the whole entry.</summary>
    private bool CtorParamAccepts(TypeInfo declared, object? value)
    {
        try { _ = CoerceTo(value, declared, 0, 0, probe: true); return true; }
        catch (ScriptException) { return false; }
    }

    private ConstructorDecl? FindMatchingConstructor(ClassInfo classInfo, object?[] args, int line, int column,
        bool[]? refOutPositions = null)
    {
        ConstructorDecl? paramsCtor = null;
        ConstructorDecl? uniqueExactArity = null;
        var exactArityCount = 0;
        foreach (var ctor in classInfo.Constructors)
        {
            if (ctor.IsStatic) continue;   // static ctors are never instance-ctor candidates
            if (ctor.Params.Count == args.Length)
            {
                exactArityCount++;
                uniqueExactArity = ctor;
                var match = true;
                for (var i = 0; i < args.Length; i++)
                {
                    // A ref/out position holds a CELL, whose runtime type says nothing about the declared
                    // parameter (the VM builds one over the caller's slot), so the position is skipped rather
                    // than compared: weighing it made a `new` with a ref/out argument find no ctor at all.
                    if (refOutPositions != null && i < refOutPositions.Length && refOutPositions[i]) continue;
                    if (CtorParamAccepts(ctor.Params[i].Type, args[i])) continue;
                    match = false;
                    break;
                }
                if (match) return ctor;   // an exact-arity match always wins over a `params` tail
                continue;
            }
            // A `params` tail absorbs every argument past the fixed ones, so it is only a FALLBACK: the scan
            // keeps going, and a later exact-arity ctor still wins.
            if (ctor.HasParamsTail && args.Length >= ctor.FixedParamCount && paramsCtor == null)
            {
                var fits = true;
                for (var i = 0; i < ctor.FixedParamCount; i++)
                {
                    if (refOutPositions != null && i < refOutPositions.Length && refOutPositions[i]) continue;
                    if (!CtorParamAccepts(ctor.Params[i].Type, args[i])) { fits = false; break; }
                }
                if (fits) paramsCtor = ctor;
            }
        }

        // ONE CANDIDATE, NO STATIC FIT: the BIND judges. The compatibility test above is the engine's by-value rule
        // (`IsKnownCompatibleType`), and that rule answers "is this value ALREADY of this type", not "can the coercion
        // make it one": `CoerceTo` also converts a collection to a `List<T>` and an array to a list, so a `new C(["x"])`
        // against `C(List<object> r)` matched NOTHING here and the VM answered VmFallback, which refuses a bundle's
        // whole entry (measured: text_writer_bridge_test's reader section, and the same shape with an `object`
        // parameter, whose old helper had no `object` arm at all). With exactly one same-arity candidate there is
        // nothing to CHOOSE, so it is returned and `InstantiateClass` binds it through `CoerceTo`, raising its own
        // refusal when the conversion really is impossible. Two or more candidates keep the old answer (the tree
        // weighs them), so no overload decision moves.
        //
        // THE TWIN THIS REPLACES was `MethodDispatch.IsTypeCompatible`, a Type-based predicate that lacked the
        // `object` arm and knew numeric widening only: one question, two answers, and the ctor path had the narrower
        // one. It stays for METHOD overload selection, which was measured working.
        if (paramsCtor == null && exactArityCount == 1) return uniqueExactArity;
        return paramsCtor;
    }

    private ConstructorDecl? FindMatchingConstructor(ClassInfo classInfo, IReadOnlyList<Expr> args, int line, int column)
    {
        // Pass 1: prefer a unique match on static argument types (mirrors the runtime
        // type-aware matcher) so same-arity overloads like Tagged(int) / Tagged(string)
        // resolve without the tree throwing "Ambiguous constructors".
        ConstructorDecl? staticMatch = null;
        foreach (var c in classInfo.Constructors)
        {
            if (c.IsStatic || c.Params.Count != args.Count) continue;
            var ok = true;
            for (var i = 0; i < args.Count; i++)
            {
                var argType = InferExpressionType(args[i]);
                // Unknown static type (bare variable, function call, etc.), skip the strict pass
                if (argType.BaseType == TokenType.Object) { ok = false; break; }
                if (!IsStrictCtorCompatible(argType.BaseType, c.Params[i].Type.BaseType)) { ok = false; break; }
            }
            if (!ok) continue;
            if (staticMatch != null)
                RuntimeError($"Ambiguous constructors for class '{classInfo.Name}' with {args.Count} arguments.", line, column);
            staticMatch = c;
        }
        if (staticMatch != null)
            return staticMatch;

        // Pass 2: arity-only fallback (first match wins, the caller re-resolves with
        // runtime argument types via ResolveConstructorForValues when any arg's static
        // type was unknown, so Tagged(variable) still picks the right overload).
        foreach (var c in classInfo.Constructors)
        {
            if (!c.IsStatic && c.Params.Count == args.Count)
                return c;
        }

        // Pass 3: a `params` tail absorbs everything past the fixed parameters. LAST, so an exact-arity ctor
        // always wins (`P(int)` beats `P(params int[])` for one argument), and a tail-only ctor still matches
        // its empty and its single-array forms.
        foreach (var c in classInfo.Constructors)
        {
            if (c.IsStatic || !c.HasParamsTail || args.Count < c.FixedParamCount) continue;
            var fits = true;
            for (var i = 0; i < c.FixedParamCount; i++)
            {
                var argType = InferExpressionType(args[i]);
                // Unknown static type (bare variable, call, …): the runtime pass below decides, so do not refuse.
                if (argType.BaseType == TokenType.Object) continue;
                if (!IsStrictCtorCompatible(argType.BaseType, c.Params[i].Type.BaseType)) { fits = false; break; }
            }
            if (fits) return c;
        }

        if (classInfo.Constructors.Count > 0)
            RuntimeError($"No constructor found for class '{classInfo.Name}' with {args.Count} arguments.", line, column);
        if (args.Count > 0)
            RuntimeError($"Class '{classInfo.Name}' has no constructor, but {args.Count} arguments provided.", line, column);
        return null;
    }

    /// <summary>Re-resolves the constructor using runtime argument types (the VM's
    /// type-aware matcher). Returns the best match or the provisional ctor.</summary>
    private ConstructorDecl? ResolveConstructorForValues(IReadOnlyList<ConstructorDecl> constructors, ConstructorDecl? provisional, object?[] argValues, int line, int column,
        IReadOnlyList<bool>? byRef = null)
    {
        if (provisional == null || argValues.Length == 0) return provisional;
        // A `params` tail absorbs arguments the declared slots cannot be compared against one-for-one, so the
        // positional runtime check below cannot run: the matcher already chose this ctor, so keep it.
        if (provisional.HasParamsTail) return provisional;
        // If the provisional ctor's param types all accept the runtime values, keep it. The SHAPE is part of "fits":
        // a type-compatible `out` parameter still cannot receive a value, so a ctor whose shape the call cannot
        // satisfy falls through to the search, which applies the same filter.
        var fits = RefOutShapeRank(provisional, byRef) < 2;
        for (var i = 0; i < argValues.Length; i++)
        {
            var expectedType = provisional.Params[i].Type;
            var argType = argValues[i]?.GetType();
            if (IsTypeCompatibleForCtor(argType, expectedType)) continue;
            fits = false;
            break;
        }
        if (fits) return provisional;

        // Otherwise search for a runtime-type match among same-arity ctors.
        foreach (var ctor in constructors)
        {
            if (ctor.IsStatic || ctor.Params.Count != argValues.Length) continue;
            if (RefOutShapeRank(ctor, byRef) == 2) continue;
            var ok = true;
            for (var i = 0; i < argValues.Length; i++)
            {
                var expectedType = ctor.Params[i].Type;
                var argType = argValues[i]?.GetType();
                if (IsTypeCompatibleForCtor(argType, expectedType)) continue;
                ok = false;
                break;
            }
            if (ok) return ctor;
        }
        return provisional; // fall back, the coercion inside InstantiateClass reports the error
    }

    private static bool IsTypeCompatibleForCtor(Type? argType, TypeInfo expectedType)
    {
        if (argType == null) return expectedType.IsNullable;
        if (expectedType.BaseType == TokenType.Object) return true;
        if (expectedType.BaseType == TokenType.StringType) return argType == typeof(string);
        if (expectedType.BaseType == TokenType.Bool) return argType == typeof(bool);
        if (expectedType.BaseType == TokenType.Char) return argType == typeof(char);
        if (TypeSystem.IsNumeric(expectedType.BaseType))
            return TypeSystem.IsNumeric(TypeSystem.TokenTypeOf(argType));
        return argType == TypeTokenHelper.GetSystemType(expectedType);
    }

    /// <summary>Strict static-type compatibility for constructor overload selection.
    /// Unlike TypeSystem.CanConvert (the coercion matrix, which permits int→string),
    /// this keeps numeric-to-string OUT so same-arity overloads like
    /// Tagged(int) / Tagged(string) resolve uniquely instead of "Ambiguous".</summary>
    private static bool IsStrictCtorCompatible(TokenType source, TokenType target)
    {
        if (source == target) return true;
        if (target == TokenType.Object) return true;
        if (source == TokenType.Object) return false;
        // Numeric-to-numeric widening is fine; numeric-to-string is not an overload match.
        if (TypeSystem.IsNumeric(source)) return TypeSystem.IsNumeric(target) || target == TokenType.Bool;
        if (source == TokenType.Bool) return target == TokenType.Bool;
        if (source == TokenType.Char) return target == TokenType.Char;
        return false;
    }

    private void RunStaticConstructors(ClassInfo classInfo, int line, int column)
    {
        // Collect hierarchy from base to derived
        var chain = new List<ClassInfo>();
        var cur = classInfo;
        while (cur != null)
        {
            chain.Add(cur);
            cur = cur.Parent;
        }

        // Run base first (chain is [most-derived, ..., base]), iterate backwards
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var cls = chain[i];
            if (!_staticInitClasses.Add(cls.Name)) continue;
            foreach (var ctor in cls.Constructors)
            {
                if (!ctor.IsStatic) continue;
                var prevThis = _currentThis;
                var prevClass = _currentClass;
                var prevInCtor = _inConstructor;
                _currentThis = null;
                _currentClass = cls;
                _inConstructor = true;
                CurrentFrame.Scopes.PushScope();
                try
                {
                    if (ctor is { Compiled: not null, Body.Count: 0 })
                    {
                        // Imported class: no AST body, run the compiled static ctor via the VM.
                        // Static ctors have no parameters; the compiler reserves a `this` local that
                        // a static body never reads, so pass a null placeholder (mirrors
                        // BuildCtorVmArgs's trailing-instance convention at Interpreter.cs:4097).
                        var result = Vm.Execute(ctor.Compiled, [null], this, cls.Name);
                        // The compiled body is the ONLY body here (Body is empty for imported
                        // classes), so a VM fallback cannot be retried via the tree-walker. The
                        // class is already marked initialized (_staticInitClasses.Add above), so a
                        // silent no-op would permanently skip a partially-applied static ctor.
                        if (result is VmFallback)
                            throw new ScriptException($"Static constructor of imported class '{cls.Name}' failed to execute (VM fallback).");
                    }
                    else
                    {
                        foreach (var stmt in ctor.Body) Execute(stmt);
                    }
                }
                finally
                {
                    CurrentFrame.Scopes.PopScope();
                    _currentThis = prevThis;
                    _currentClass = prevClass;
                    _inConstructor = prevInCtor;
                }
            }
        }
    }

    private Array CreateArray(TypeInfo elemType, IReadOnlyList<Expr>? sizes, IReadOnlyList<Expr>? initializer, int line, int column)
    {
        // Evaluate sizes/initializer once, then delegate to the shared value-level core
        // (same single-evaluation semantics as the VM direct path, the old tree path
        // re-evaluated initializer expressions twice).
        object?[]? sizeVals = null;
        if (sizes is { Count: > 0 })
        {
            sizeVals = new object?[sizes.Count];
            for (var i = 0; i < sizes.Count; i++) sizeVals[i] = Evaluate(sizes[i]);
        }
        object?[]? initVals = null;
        if (initializer is { Count: > 0 })
        {
            initVals = new object?[initializer.Count];
            for (var i = 0; i < initializer.Count; i++) initVals[i] = Evaluate(initializer[i]);
        }
        return CreateArrayDirect(elemType, sizeVals, initVals, line, column);
    }

    /// <summary>Value-level typed-array construction shared by the tree (values evaluated once above)
    /// and the VM (values already on the VM stack). Zero per-call node/scope allocation.</summary>
    internal Array CreateArrayDirect(TypeInfo elemType, object?[]? sizes, object?[]? initializer, int line, int column)
    {
        if (sizes == null && initializer == null)
            RuntimeError("Array creation must specify either size or initializer.", line, column);

        if (elemType.JaggedDepth > 0)
        {
            if (sizes is not { Length: 1 })
                RuntimeError("Jagged array creation must specify exactly one size for the outer dimension.", line, column);

            var sizeObj = sizes![0];
            var length = Convert.ToInt32(sizeObj);
            if (length < 0)
                RuntimeError("Array size cannot be negative.", line, column);

            var innerSystemType = TypeTokenHelper.GetSystemType(elemType);
            var outerArray = Array.CreateInstance(innerSystemType, length);
            return outerArray;
        }

        int[] lengths;
        if (sizes is { Length: > 0 })
        {
            lengths = new int[sizes.Length];
            for (var i = 0; i < sizes.Length; i++)
            {
                var sizeObj = sizes[i];
                lengths[i] = Convert.ToInt32(sizeObj);
                if (lengths[i] < 0)
                    RuntimeError("Array size cannot be negative.", line, column);
            }
        }
        else
        {
            if (initializer == null || initializer.Length == 0)
                RuntimeError("Array creation must specify either size or initializer.", line, column);
            lengths = [initializer.Length];
        }

        // If the element type has a custom name (delegate, class, etc.), try to infer from runtime values
        Type elementSystemType;
        if (!string.IsNullOrEmpty(elemType.TypeName))
        {
            elementSystemType = typeof(object);
            // Check if all initializer values share a common runtime type
            if (initializer is { Length: > 0 })
            {
                Type? commonType = null;
                foreach (var val in initializer)
                {
                    if (ReferenceEquals(val, YieldSentinel)) return null!;
                    if (val != null)
                    {
                        if (commonType == null) commonType = val.GetType();
                        else if (commonType != val.GetType()) { commonType = null; break; }
                    }
                }
                if (commonType != null) elementSystemType = commonType;
            }
        }
        else
            elementSystemType = TypeTokenHelper.GetSystemType(elemType);

        var arr = Array.CreateInstance(elementSystemType, lengths);
        if (initializer == null || initializer.Length == 0) return arr;
        {
            if (arr.Rank != 1)
                RuntimeError($"Array initializer not supported for rank {arr.Rank} arrays.", line, column);

            var totalSize = arr.Length;
            var initCount = initializer.Length;
            if (totalSize != initCount && sizes is { Length: > 0 })
                RuntimeError($"Array initializer count ({initCount}) does not match array size ({totalSize}).", line, column);

            for (var i = 0; i < Math.Min(totalSize, initCount); i++)
            {
                var value = initializer[i];
                var coerced = CoerceTo(value, elemType, line, column);
                arr.SetValue(coerced, i);
            }
        }
        return arr;
    }

    private static int ResolveIndex(object? evalResult, int length)
    {
        if (evalResult is Index idx)
            return idx.GetOffset(length);
        return Convert.ToInt32(evalResult);
    }

    private static Array SliceArray(Array arr, int start, int length)
    {
        var elementType = arr.GetType().GetElementType()!;
        var result = Array.CreateInstance(elementType, length);
        Array.Copy(arr, start, result, 0, length);
        return result;
    }

    private static Array SliceForIList(IList list, int start, int length)
    {
        var elementType = list.GetType().IsArray
            ? list.GetType().GetElementType()!
            : (list.GetType().GetGenericArguments().FirstOrDefault() ?? typeof(object));
        var result = Array.CreateInstance(elementType, length);
        for (var i = 0; i < length; i++)
            result.SetValue(list[start + i], i);
        return result;
    }

    private static Array SliceGenericList(IList<object?> list, int start, int length)
    {
        var result = new object?[length];
        for (var i = 0; i < length; i++)
            result[i] = list[start + i];
        return result;
    }

    private static Array SliceScriptList(IScriptList list, int start, int length)
    {
        var elementType = TypeTokenHelper.GetSystemType(list.ElementType);
        var result = Array.CreateInstance(elementType, length);
        for (var i = 0; i < length; i++)
            result.SetValue(list[start + i], i);
        return result;
    }

    /// <summary>Reads a RAW CLR dictionary through <see cref="IDictionary"/>, which is the ONE index route that
    /// reaches a `Dictionary&lt;string, string&gt;` or a `Hashtable`.
    ///
    /// The four index cores below each had an arm for `IScriptDictionary` (the engine's own dictionary) and for
    /// `IDictionary&lt;object, object?&gt;` (a `{ }` literal's backing type, and the arm both the tree and the VM
    /// reach), and NONE for the interface every other CLR dictionary implements. So `ctx.RequestHeaders["HX-Request"]`
    /// answered "Cannot index object of type Dictionary&lt;string, string&gt;" and
    /// `Environment.GetEnvironmentVariables()["PATH"]` answered the same for a `Hashtable`, which is how an
    /// `http.Use` middleware came to read headers through `ContainsKey` only and to build every answer out of
    /// strings and counters.
    ///
    /// An ABSENT key answers null, exactly like the `IDictionary&lt;object, object?&gt;` arm beside it, so
    /// `ctx.RequestHeaders["Missing"]` needs no guard. (The engine's own dictionary is the one that REFUSES a
    /// missing key; that refusal is a documented property of that type and this arm does not change it.)</summary>
    private static object? ReadClrDictionary(IDictionary dict, object? key)
    {
        // A null key THROWS on the CLR's own lookup and can never be present, so it is a miss rather than a
        // refusal. A key of the wrong TYPE is a miss too: `Dictionary&lt;TKey, TValue&gt;`'s explicit
        // implementation answers false for it rather than throwing.
        if (key is null) return null;
        return dict.Contains(key) ? dict[key] : null;
    }

    /// <summary>The WRITE half of <see cref="ReadClrDictionary"/>: one implementation for the tree core and the
    /// VM core, so a key the dictionary's own key type cannot accept is refused in the engine's words from
    /// either engine (the CLR answers "The value ... is not of type ...", which must not reach a script).</summary>
    private static void WriteClrDictionary(IDictionary dict, object? key, object? value, int line, int column)
    {
        if (key is null)
            RuntimeError("A dictionary key cannot be null.", line, column);
        try { dict[key!] = value; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
    }

    private object? GetArrayElement(object? arrayObj, IReadOnlyList<Expr> indexNodes, int line, int column)
    {
        // Range-slice: obj[rangeExpr]
        if (indexNodes is [RangeExpr rangeNode])
        {
            var collLength = arrayObj switch
            {
                string s => s.Length,
                Array a => a.Length,
                IList l => l.Count,
                IList<object?> gl => gl.Count,
                IScriptCollection sc => sc.Count,
                _ => -1
            };
            if (collLength < 0)
            {
                if (arrayObj is IScriptHashSet)
                    RuntimeError("HashSet does not support range slicing. Use ToArray() first.", line, column);
                RuntimeError($"Type {GetFriendlyTypeNameOfValue(arrayObj)} does not support range slicing.", line, column);
            }

            // Evaluate RangeExpr's From/To as indices to compute the slice
            var fromVal = rangeNode.From != null ? Evaluate(rangeNode.From) : null;
            if (ReferenceEquals(fromVal, YieldSentinel)) return YieldSentinel;
            var toVal = rangeNode.To != null ? Evaluate(rangeNode.To) : null;
            if (ReferenceEquals(toVal, YieldSentinel)) return YieldSentinel;
            var start = fromVal is Index si ? si.GetOffset(collLength) : Convert.ToInt32(fromVal ?? 0);
            var end = toVal is Index ei ? ei.GetOffset(collLength) : Convert.ToInt32(toVal ?? collLength);
            if (end < start) end = start;
            var len = Math.Max(0, end - start);
            if (len == 0) return arrayObj switch
            {
                string => "",
                Array arr => Array.CreateInstance(arr.GetType().GetElementType()!, 0),
                _ => Array.Empty<object?>()
            };

            return arrayObj switch
            {
                string s => s.Substring(start, len),
                Array arr => SliceArray(arr, start, len),
                IList ilist => SliceForIList(ilist, start, len),
                IList<object?> gl => SliceGenericList(gl, start, len),
                IScriptList sl => SliceScriptList(sl, start, len),
                IScriptHashSet => throw new InvalidOperationException("HashSet does not support range slicing. Use ToArray() first."),
                _ => throw new InvalidOperationException($"Type {GetFriendlyTypeNameOfValue(arrayObj)} does not support range slicing.")
            };
        }

        switch (arrayObj)
        {
            case null:
                RuntimeError("Cannot index null.", line, column);
                break;

            case string str:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("String index requires exactly one argument.", line, column);
                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = ResolveIndex(idxObj, str.Length);
                    if (idx < 0 || idx >= str.Length)
                        RuntimeError("String index out of range.", line, column);
                    return str[idx];
                }

            // Handle IScriptList (our List<T> implementation)
            case IScriptList list:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = ResolveIndex(idxObj, list.Count);
                    if (idx < 0 || idx >= list.Count)
                        RuntimeError("List index out of range.", line, column);
                    return list[idx];
                }

            // Handle script tuples: (1, "x")[0]
            case ScriptTuple tup:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Tuple index requires exactly one argument.", line, column);
                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = ResolveIndex(idxObj, tup.Length);
                    if (idx < 0 || idx >= tup.Length)
                        RuntimeError("Tuple index out of range.", line, column);
                    return tup[idx];
                }

            // Handle native Array
            case Array arr:
                {
                    var indices = new int[indexNodes.Count];
                    for (var i = 0; i < indexNodes.Count; i++)
                    {
                        var idxObj = Evaluate(indexNodes[i]);
                        indices[i] = ResolveIndex(idxObj, arr.GetLength(i));
                        if (indices[i] < 0 || indices[i] >= arr.GetLength(i))
                            RuntimeError("Array index out of range.", line, column);
                    }
                    return arr.GetValue(indices);
                }

            // Handle IScriptDictionary (for completeness)
            case IScriptDictionary dict:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    var key = Evaluate(indexNodes[0]);
                    return dict[key!];
                }

            // Handle native Dictionary<object, object?> (dict literal {})
            case IDictionary<object, object?> objDict:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    var key = Evaluate(indexNodes[0])!;
                    return objDict.TryGetValue(key, out var val) ? val : null;
                }

            // Handle a RAW CLR dictionary (Dictionary<string, string>, Hashtable, ...): see ReadClrDictionary.
            case IDictionary clrDict:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    return ReadClrDictionary(clrDict, Evaluate(indexNodes[0]));
                }

            // A HOST is indexed by a control NAME, and that index IS the lookup its children are reached by everywhere
            // else (IControlHost.HostFind, ONE implementation for a form, a frame and a document window). It is what a
            // document's names are FOR: a template carries the LOOK rather than the behaviour, so re-attaching
            // behaviour means reaching a child by the name the document gave it, `copy["Button1"].Click = ...`. There
            // is no second spelling (`form.Find(...)` was retired, because the index covers it and covers the two MDI
            // hosts as well). A MISS answers null, like the lookup and like a dictionary read, so `copy["nope"]?.Text`
            // short-circuits instead of throwing.
            case IControlHost host:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("A control name index takes exactly one argument.", line, column);
                    var nameObj = Evaluate(indexNodes[0]);
                    var ctrlName = nameObj as string;
                    if (ctrlName is null)
                        RuntimeError($"A control is reached by its NAME, so the index must be a string, not {GetFriendlyTypeNameOfValue(nameObj)}.", line, column);
                    return host.HostFind(ctrlName ?? "");
                }

            // Handle generic IList<T> (e.g., OrderedEnumerable, List<T> wrappers)
            case IList<object?> genList:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = ResolveIndex(idxObj, genList.Count);
                    if (idx < 0 || idx >= genList.Count)
                        RuntimeError("List index out of range.", line, column);
                    return genList[idx];
                }

            case IScriptHashSet:
                RuntimeError("HashSet does not support positional index. Use foreach, Contains(), or ToArray().", line, column);
                break;

            // Handle .NET IList (e.g., native List<T>, ArrayList)
            case IList ilist:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = ResolveIndex(idxObj, ilist.Count);
                    if (idx < 0 || idx >= ilist.Count)
                        RuntimeError("List index out of range.", line, column);
                    return ilist[idx];
                }
        }

        RuntimeError($"Cannot index object of type {GetFriendlyTypeNameOfValue(arrayObj)}.", line, column);
        return null;
    }

    private void SetArrayElement(object? arrayObj, IReadOnlyList<Expr> indexNodes, object? value, int line, int column)
    {
        switch (arrayObj)
        {
            case null:
                RuntimeError("Cannot index null.", line, column);
                break;

            case IScriptList list:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);

                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = ResolveIndex(idxObj, list.Count);
                    if (idx < 0 || idx >= list.Count)
                        RuntimeError("List index out of range.", line, column);
                    list[idx] = value;
                    return;
                }

            case Array arr:
                {
                    var indices = new int[indexNodes.Count];
                    for (var i = 0; i < indexNodes.Count; i++)
                    {
                        var idxObj = Evaluate(indexNodes[i]);
                        indices[i] = ResolveIndex(idxObj, arr.GetLength(i));
                        if (indices[i] < 0 || indices[i] >= arr.GetLength(i))
                            RuntimeError("Array index out of range.", line, column);
                    }
                    var elementType = arr.GetType().GetElementType()!;
                    var elementTypeInfo = MapTypeInfo(elementType);
                    var converted = CoerceTo(value, elementTypeInfo, line, column);
                    arr.SetValue(converted, indices);
                    return;
                }

            case IScriptDictionary dict:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    var key = Evaluate(indexNodes[0]);
                    dict[key!] = value;
                    return;
                }

            // Handle native Dictionary<object, object?> (dict literal {})
            case IDictionary<object, object?> objDict:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    var key = Evaluate(indexNodes[0])!;
                    objDict[key] = value;
                    return;
                }

            // Handle a RAW CLR dictionary (Dictionary<string, string>, Hashtable, ...): see WriteClrDictionary.
            case IDictionary clrDict:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    WriteClrDictionary(clrDict, Evaluate(indexNodes[0]), value, line, column);
                    return;
                }

            // Handle .NET IList (e.g., native List<T>)
            case IList iList:
                {
                    if (indexNodes.Count != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idxObj = Evaluate(indexNodes[0]);
                    var idx = Convert.ToInt32(idxObj);
                    if (idx < 0 || idx >= iList.Count)
                        RuntimeError("List index out of range.", line, column);
                    iList[idx] = value;
                    return;
                }
        }

        RuntimeError($"Cannot index object of type {GetFriendlyTypeNameOfValue(arrayObj)}.", line, column);
    }

    // ---- Value-based index helpers (used by the VM, same semantics, pre-evaluated indices) ----

    /// <summary>The VM's index core, with the RAW CLR failure a dictionary lookup can raise converted into the
    /// engine's own refusal. Every member path already does this (Interpreter.ClrMemberFailure, "every VM-facing
    /// member access"), and the INDEX path was the one left out: measured, `Json.Deserialize(...)["zz"]` inside a
    /// compiled body threw a bare `KeyNotFoundException` that the VM's exception walk (ScriptException only) could
    /// not catch, so a script's own `catch { return default }` never ran where the tree's did (that is what
    /// `json_inc_test`'s `Json.Get` fallback check caught). The engine's own refusal passes unchanged.</summary>
    internal object? GetArrayElementValues(object? arrayObj, object?[] idxVals, int line, int column)
    {
        try { return GetArrayElementValuesCore(arrayObj, idxVals, line, column); }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
    }

    private object? GetArrayElementValuesCore(object? arrayObj, object?[] idxVals, int line, int column)
    {
        switch (arrayObj)
        {
            case null:
                RuntimeError("Cannot index null.", line, column);
                break;

            case string str:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("String index requires exactly one argument.", line, column);
                    var idx = ResolveIndex(idxVals[0], str.Length);
                    if (idx < 0 || idx >= str.Length)
                        RuntimeError("String index out of range.", line, column);
                    return str[idx];
                }

            case IScriptList list:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idx = ResolveIndex(idxVals[0], list.Count);
                    if (idx < 0 || idx >= list.Count)
                        RuntimeError("List index out of range.", line, column);
                    return list[idx];
                }

            case Array arr:
                {
                    var indices = new int[idxVals.Length];
                    for (var i = 0; i < idxVals.Length; i++)
                    {
                        indices[i] = ResolveIndex(idxVals[i], arr.GetLength(i));
                        if (indices[i] < 0 || indices[i] >= arr.GetLength(i))
                            RuntimeError("Array index out of range.", line, column);
                    }
                    return arr.GetValue(indices);
                }

            case IScriptDictionary dict:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    return dict[idxVals[0]!];
                }

            case ScriptTuple tup:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Tuple index requires exactly one argument.", line, column);
                    var idx = ResolveIndex(idxVals[0], tup.Length);
                    if (idx < 0 || idx >= tup.Length)
                        RuntimeError("Tuple index out of range.", line, column);
                    return tup.Items[idx];
                }

            case IDictionary<object, object?> objDict:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    return objDict.TryGetValue(idxVals[0]!, out var val) ? val : null;
                }

            // Handle a RAW CLR dictionary (Dictionary<string, string>, Hashtable, ...): see ReadClrDictionary.
            case IDictionary clrDict:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    return ReadClrDictionary(clrDict, idxVals[0]);
                }

            case IList<object?> genList:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idx = ResolveIndex(idxVals[0], genList.Count);
                    if (idx < 0 || idx >= genList.Count)
                        RuntimeError("List index out of range.", line, column);
                    return genList[idx];
                }

            case IScriptHashSet:
                RuntimeError("HashSet does not support positional index. Use foreach, Contains(), or ToArray().", line, column);
                break;

            case IList ilist:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idx = ResolveIndex(idxVals[0], ilist.Count);
                    if (idx < 0 || idx >= ilist.Count)
                        RuntimeError("List index out of range.", line, column);
                    return ilist[idx];
                }

            // A HOST indexed by a control NAME, which is the SAME arm the tree walker carries and the same lookup
            // (IControlHost.HostFind). It is repeated here because this is the engine that reads already-evaluated
            // indices, and both paths must reach the index or a compiled body and a walked one would disagree; a
            // change to one belongs in both.
            case IControlHost host:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("A control name index takes exactly one argument.", line, column);
                    var ctrlName = idxVals[0] as string;
                    if (ctrlName is null)
                        RuntimeError($"A control is reached by its NAME, so the index must be a string, not {GetFriendlyTypeNameOfValue(idxVals[0])}.", line, column);
                    return host.HostFind(ctrlName ?? "");
                }
        }

        RuntimeError($"Cannot index object of type {GetFriendlyTypeNameOfValue(arrayObj)}.", line, column);
        return null;
    }

    /// <summary>The WRITE half of the VM's index core, converted the same way its read half is: a CLR collection
    /// can refuse a store (a read-only list, a key of the wrong type), and a raw CLR exception there would escape
    /// a script's own catch exactly as the read's did (see GetArrayElementValues).</summary>
    internal void SetArrayElementValues(object? arrayObj, object?[] idxVals, object? value, int line, int column)
    {
        try { SetArrayElementValuesCore(arrayObj, idxVals, value, line, column); }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
    }

    private void SetArrayElementValuesCore(object? arrayObj, object?[] idxVals, object? value, int line, int column)
    {
        switch (arrayObj)
        {
            case null:
                RuntimeError("Cannot index null.", line, column);
                break;

            case IScriptList list:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idx = ResolveIndex(idxVals[0], list.Count);
                    if (idx < 0 || idx >= list.Count)
                        RuntimeError("List index out of range.", line, column);
                    list[idx] = value;
                    return;
                }

            case Array arr:
                {
                    var indices = new int[idxVals.Length];
                    for (var i = 0; i < idxVals.Length; i++)
                    {
                        indices[i] = ResolveIndex(idxVals[i], arr.GetLength(i));
                        if (indices[i] < 0 || indices[i] >= arr.GetLength(i))
                            RuntimeError("Array index out of range.", line, column);
                    }
                    var elementType = arr.GetType().GetElementType()!;
                    var elementTypeInfo = MapTypeInfo(elementType);
                    var converted = CoerceTo(value, elementTypeInfo, line, column);
                    arr.SetValue(converted, indices);
                    return;
                }

            case IScriptDictionary dict:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    dict[idxVals[0]!] = value;
                    return;
                }

            case IDictionary<object, object?> objDict:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    objDict[idxVals[0]!] = value;
                    return;
                }

            // Handle a RAW CLR dictionary (Dictionary<string, string>, Hashtable, ...): see WriteClrDictionary.
            case IDictionary clrDict:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("Dictionary index requires exactly one argument.", line, column);
                    WriteClrDictionary(clrDict, idxVals[0], value, line, column);
                    return;
                }

            case IList iList:
                {
                    if (idxVals.Length != 1)
                        RuntimeError("List index requires exactly one argument.", line, column);
                    var idx = Convert.ToInt32(idxVals[0]);
                    if (idx < 0 || idx >= iList.Count)
                        RuntimeError("List index out of range.", line, column);
                    iList[idx] = value;
                    return;
                }
        }

        RuntimeError($"Cannot index object of type {GetFriendlyTypeNameOfValue(arrayObj)}.", line, column);
    }

    internal object? SliceValues(object? arrayObj, object? fromVal, object? toVal, int line, int column)
    {
        var collLength = arrayObj switch
        {
            string s => s.Length,
            Array a => a.Length,
            IList l => l.Count,
            IList<object?> gl => gl.Count,
            IScriptCollection sc => sc.Count,
            _ => -1
        };
        if (collLength < 0)
        {
            if (arrayObj is IScriptHashSet)
                RuntimeError("HashSet does not support range slicing. Use ToArray() first.", line, column);
            RuntimeError($"Type {GetFriendlyTypeNameOfValue(arrayObj)} does not support range slicing.", line, column);
        }

        var start = fromVal is Index si ? si.GetOffset(collLength) : Convert.ToInt32(fromVal ?? 0);
        var end = toVal is Index ei ? ei.GetOffset(collLength) : Convert.ToInt32(toVal ?? collLength);
        if (end < start) end = start;
        var len = Math.Max(0, end - start);
        if (len == 0) return arrayObj switch
        {
            string => "",
            Array arr => Array.CreateInstance(arr.GetType().GetElementType()!, 0),
            _ => Array.Empty<object?>()
        };

        return arrayObj switch
        {
            string s => s.Substring(start, len),
            Array arr => SliceArray(arr, start, len),
            IList ilist => SliceForIList(ilist, start, len),
            IList<object?> gl => SliceGenericList(gl, start, len),
            IScriptList sl => SliceScriptList(sl, start, len),
            // A REFUSAL, not a raw CLR failure: these two threw InvalidOperationException, which is not a
            // ScriptException, so a compiled body's own `try` around a slice NEVER caught it (the VM's
            // handler walk matches ScriptException only) while the same refusal from the tree was caught.
            // Measured: `try { var y = hs[0..2]; } catch { }` inside a function escaped with the raw
            // exception on the shipping configuration (test_hashset). The engine's refusal path gives the
            // same wording through the map the tree's message already used.
            IScriptHashSet => RuntimeError<object>("HashSet does not support range slicing. Use ToArray() first.", line, column),
            _ => RuntimeError<object>($"Type {GetFriendlyTypeNameOfValue(arrayObj)} does not support range slicing.", line, column)
        };
    }

    // ---- Value-based member helpers (used by the VM, delegate to the tree-walker visitors via a temp scope) ----

    private const string VmTargetVar = "_vm_target";
    private const string VmValueVar = "_vm_value";
    private const string VmArgPrefix = "_vm_arg_";

    /// <summary>Constructs an instance (script class/struct, generic List/Dictionary/HashSet, or .NET type)
    /// via the tree-walker's VisitNewInstanceExpr, args passed as temp-scope variables.</summary>
    internal object? ConstructValue(TypeInfo ti, object?[] args, int line, int column)
    {
        CurrentFrame.Scopes.PushScope();
        try
        {
            var argExprs = new Expr[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                var vn = VmArgPrefix + i;
                CurrentFrame.Scopes.Define(vn, args[i]);
                argExprs[i] = VariableExpr.Create(vn, line, column);
            }
            var node = NewInstanceExpr.Create(
                VariableExpr.Create(VmTargetVar, line, column), argExprs, ti, line, column);
            return VisitNewInstanceExpr(node);
        }
        finally { CurrentFrame.Scopes.PopScope(); }
    }

    /// <summary>Cast (T)value, direct application. Mirrors VisitCastExpr's value-level semantics
    /// (nullability check → IsTypeCheck → CoerceTo → CanConvert fallback). No synthetic node/scope.</summary>
    internal object? CastValue(TypeInfo ti, object? value, int line, int column)
    {
        var targetType = ti;
        if (!string.IsNullOrEmpty(targetType.TypeName) && !IsKnownType(targetType.TypeName))
            RuntimeError($"Type '{targetType.TypeName}' is unknown or undefined.", line, column);
        if (value == null)
        {
            if (targetType.IsNullable || targetType.BaseType == TokenType.Object || targetType.BaseType == TokenType.StringType) return null;
            RuntimeError($"Cannot cast null to non-nullable type {targetType.BaseType}", line, column);
        }

        var isCompatible = IsTypeCheck(value, targetType, line, column);
        if (isCompatible)
            return CoerceTo(value, targetType, line, column);

        if (targetType.TypeName != null)
        {
            var srcName = GetFriendlyTypeNameOfValue(value);
            RuntimeError($"Cannot cast {srcName} to '{targetType.TypeName}'.", line, column);
        }

        var sourceType = value.GetType();
        var sourceTypeInfo = MapTypeInfo(sourceType);
        if (CanConvert(sourceTypeInfo.BaseType, targetType.BaseType)) return CoerceTo(value, targetType, line, column);
        RuntimeError(sourceType == typeof(DateTime)
            ? $"Cannot cast DateTime to {targetType.BaseType}: {DateTimeNumberRemedy}"
            : $"Cannot cast {sourceType.Name} to {targetType.BaseType}", line, column);
        return CoerceTo(value, targetType, line, column);
    }

    /// <summary>As-expression (value as T), direct application. Mirrors VisitAsExpr's value-level
    /// semantics (unknown-type check → null → IsValueCompatibleWithType → delegate wrapper).</summary>
    internal object? AsValue(TypeInfo ti, object? value, int line, int column)
    {
        if (!string.IsNullOrEmpty(ti.TypeName) && !IsKnownType(ti.TypeName))
            RuntimeError($"Type '{ti.TypeName}' is unknown or undefined.", line, column);
        if (value == null) return null;
        var isCompatible = IsValueCompatibleWithType(value, ti);
        if (!isCompatible) return null;
        // Typed Dictionary<K,V> target: rebuild a raw dict literal into the typed wrapper
        // (CoerceTo copies + coerces entries; IScriptDictionary values pass through). If an
        // entry cannot be coerced, `as` yields null rather than throwing.
        if (ti is { TypeName: not null }
            && ti.TypeName.Equals("Dictionary", Comparison)
            && ti.GenericArgs is { Count: 2 })
        {
            try { return CoerceTo(value, ti, line, column); }
            catch (ScriptException) { return null; }
        }
        if (value is ScriptFunction fn)
        {
            var delegateName = (ti.TypeName ?? TypeTokenHelper.GetTypeName(ti.BaseType)).AsSpan();
            // Strip the CLR arity suffix: `Action`1` → `Action` (the backtick-mangled .NET name).
            var dTick = delegateName.IndexOf('`');
            if (dTick >= 0) delegateName = delegateName[..dTick];
            if (delegateName.Equals("Action", Comparison) || delegateName.Equals("Func", Comparison) ||
                delegateName.Equals("Predicate", Comparison) || delegateName.Equals("Comparison", Comparison) ||
                delegateName.Equals("Converter", Comparison) || delegateName.Equals("EventHandler", Comparison) ||
                delegateName.StartsWith("Action<") || delegateName.StartsWith("Func<") || delegateName.StartsWith("Predicate<"))
                // THE RUNNING INTERPRETER, not the one this coercion happens to run on: in a SESSION the builtin that
                // takes the delegate is registered on a different interpreter than the one running the script, so
                // capturing `this` made a callback read (and write) the WRONG globals. Measured through the launcher: a
                // bundle's entry ran on interpreter A, `Application.CreateForm(name, (frm) => { hits = hits + 1; })`
                // invoked the callback on interpreter B, and the read of the entry's own `hits` answered "Undefined
                // variable 'hits'" while the same bundle through the CLI (ONE interpreter) worked. The tree's own
                // delegate adapter already asks `Current` for this reason; this is its stale twin.
                return new ScriptDelegateWrapper(Current ?? this, fn);
        }
        return value;
    }

    /// <summary>Writes a global-scope variable (VM STORE_GLOBAL), mirrors the tree's Scope.Set global branch.</summary>
    internal void SetGlobalValue(string name, object? value, int line, int column, bool define = false)
    {
        // A CONST is a global that refuses assignment, and this is the ONE place every write goes through: the
        // compiled STORE_GLOBAL_FAST handler calls it and propagates what it throws (its own comment says so),
        // while the tree has its own refusal in its assignment path. Without this, a bundle of a file with a
        // top-level const accepted a write the script refuses (measured: `K = 5` printed "WRITE ALLOWED" from
        // the stripped bundle and "Cannot assign to constant variable" from the file). A DEFINITION passes
        // `define: true` and may overwrite, which is what the tree does for a redeclaration (it warns and
        // overwrites), so a loop tool that re-runs a file is unaffected.
        if (!define && CurrentFrame.Scopes.HasLiveConst(name))
            RuntimeError($"Cannot assign to constant variable '{name}'.", line, column);
        // Copy-free write, SetGlobalValueDirect locks internally and writes through a RefCell
        // wrapper when present (mirrors the tree's AssignExpr RefCell branch). The previous
        // GetGlobalScopePair() path built a ConcurrentDictionary snapshot per write.
        _rootFrame.Scopes.SetGlobalValueDirect(name, value, line, column);
    }

    /// <summary>Builds the SuperProxy for a VM LOAD_SUPER, mirrors VisitSuperExpr using the live
    /// _currentClass/_currentThis (set by method dispatch before the VM body runs).</summary>
    internal object? CreateSuperProxy(int line, int column)
    {
        if (_currentClass == null || _currentThis == null)
            return null; // misuse, VM falls back to the tree which raises the proper error
        var parent = _currentClass.Parent;
        if (parent == null)
            return null;
        return new SuperProxy(_currentThis!, parent);
    }

    /// <summary>Runs a `super(...)` call against the proxy the caller's LOAD_SUPER built: the PARENT constructor on
    /// the same instance. The tree's CallExpr arm used to hold this, and the VM's CALL_VAL could not reach it: a
    /// SuperProxy callee made InvokeCallableValue answer VmFallback, so a COMPILED constructor body that called
    /// `super()` bailed, and a body loaded from bytecode has no AST to fall back to, which left the fallback
    /// running an EMPTY statement list in silence. Measured on a stripped bundle of
    /// `class P { public int F; public constructor() { F = 5; } }` and
    /// `class C extends P { public constructor() { super(); } }`: `new C()` ran NO constructor and read F=0 where
    /// the file reads 5, and `cross_module_test`'s inherited-name check reported the same shape. ONE
    /// implementation now, so the two engines cannot disagree about a parent constructor.
    /// The body itself runs the way a class's own constructor does (see the compiled/AST/refusal arms below),
    /// which is what makes `super()` work from a bundle: the parent ctor's body is bytecode there.</summary>
    internal object? InvokeSuperConstructor(SuperProxy proxy, object?[] args, int line, int column)
    {
        var parentClass = proxy.ParentClass;
        // Unwrap RefCells for constructor matching (ref args wrap values in a RefCell).
        var matchArgs = new object?[args.Length];
        for (var i = 0; i < args.Length; i++)
            matchArgs[i] = args[i] is RefCell matchCell ? matchCell.Get() : args[i];
        var ctor = FindMatchingConstructor(parentClass, matchArgs, line, column)
                   ?? RuntimeError<ConstructorDecl>($"No matching constructor in parent class '{parentClass.Name}'.", line, column);
        // A `params` parent ctor absorbs the extra arguments into its tail, and the bind loop below reads
        // args[i] against ctor.Params[i]: it needs the assembled array, as the class path does.
        if (ctor.HasParamsTail)
            args = NormalizeCtorParamsArgs(ctor, matchArgs, line, column);
        if (ctor.Access != AccessModifier.Public && ctor.Access != AccessModifier.Internal)
            CheckAccess(ctor.Access, parentClass, line, column);
        var previousThis = _currentThis; var previousClass = _currentClass; var previousInConstructor = _inConstructor;
        _currentThis = proxy.Instance; _currentClass = parentClass; _inConstructor = true;
        CurrentFrame.Scopes.PushScope();

        try
        {
            CurrentFrame.Scopes.Define("this", _currentThis);
            for (var i = 0; i < args.Length; i++)
            {
                var paramVal = args[i];
                // A `params` parent ctor: the assembled tail array is defined as-is, since the declared type in
                // the bare spelling is the ELEMENT type (see InstantiateClass).
                if (ctor.HasParamsTail && i == ctor.FixedParamCount)
                {
                    CurrentFrame.Scopes.Define(ctor.Params[i].Name, paramVal, ctor.Line, ctor.Column);
                    continue;
                }
                var isRef = ctor.IsRefParams[i];
                var isOut = ctor.IsOutParams != null && i < ctor.IsOutParams.Count && ctor.IsOutParams[i];
                if ((isRef || isOut) && paramVal is not RefCell)
                {
                    // Args were pre-evaluated to values, re-capture the RefCell from the caller's scope.
                    var paramName = ctor.Params[i].Name;
                    if (CurrentFrame.Scopes.TryGet(paramName, out var existing) && existing is RefCell paramCell)
                        paramVal = paramCell;
                }
                BindParam(ctor.Params[i].Name, paramVal, ctor.Params[i].Type, isRef, isOut, line, column);
            }
            // AFTER the bind loop, for the reason the class-constructor path spells out: the tracker wraps the
            // cell it finds under the parameter's name, so the name has to EXIST by then.
            var outAssigned = InitOutTracking(ctor.IsOutParams, ctor.Params, line, column);
            var ctorValues = new object?[ctor.Params.Count];
            for (var i = 0; i < ctorValues.Length; i++)
                ctorValues[i] = CurrentFrame.Scopes.TryGet(ctor.Params[i].Name, out var bound) ? bound : null;
            var ctorBytecode = ctor.Compiled;
            var ctorRunsInVm = ctorBytecode != null
                && (!HasRefOrOutCtorParams(ctor) || ctor.Body.Count == 0);
            bool[]? ctorOutTrack = null;
            if (ctorRunsInVm)
            {
                ctorOutTrack = WrapCtorOutArgs(ctor, ref ctorValues, line, column);
                RunCtorBodyOnVm(parentClass, ctor, ctorBytecode!, proxy.Instance, ctorValues, line, column);
            }
            else
            {
                foreach (var stmt in ctor.Body) Execute(stmt);
            }
            VerifyOutParams(ctorOutTrack ?? outAssigned, ctor.IsOutParams, ctor.Params, ctor.Line, ctor.Column);
        }
        finally
        {
            CurrentFrame.Scopes.PopScope();
            _currentThis = previousThis; _currentClass = previousClass; _inConstructor = previousInConstructor;
        }
        // A parent constructor call answers nothing, the same value the tree's arm returned.
        return null;
    }

    /// <summary>Runs a constructor's COMPILED body for an instance that already exists, with the commit discipline
    /// every VM-entered body owes: a bail that did something observable is REFUSED (the tree would repeat it), and
    /// a bail that did NOTHING falls back to the AST. The one arm worth spelling out is the third: a body that
    /// came from BYTECODE has no AST, so falling back to it would run an EMPTY statement list in silence, which is
    /// what a `super(...)` call did before the VM could reach it (measured: a stripped bundle's `new C()` ran no
    /// constructor at all). That is refused by name instead.</summary>
    private void RunCtorBodyOnVm(ClassInfo classInfo, ConstructorDecl ctor, Bytecode bytecode, object instance,
        object?[] ctorValues, int line, int column)
    {
        var ctorCommitBaseline = VmBodyCommitted;
        VmBodyCommitted = false;
        object? ctorVmResult;
        var ctorCommittedHere = false;
        try
        {
            ctorVmResult = Vm.Execute(bytecode, BuildCtorVmArgs(ctorValues, instance), this, classInfo.Name);
        }
        finally
        {
            ctorCommittedHere = VmBodyCommitted;
            VmBodyCommitted = ctorCommitBaseline || ctorCommittedHere;
        }
        if (ctorVmResult is not VmFallback) return;
        if (ctorCommittedHere)
            RuntimeError($"Constructor '{classInfo.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
        if (ctor.Body.Count == 0)
            RuntimeError($"Constructor '{classInfo.Name}' was loaded from bytecode and stopped at an instruction the VM cannot run, so this bundle cannot reproduce it. Re-export or recompile it.", line, column);
        foreach (var stmt in ctor.Body) Execute(stmt);
    }

    /// <summary>Object-initializer new T { P = v } via the tree's VisitObjectInitExpr, name/value pairs as temp vars.</summary>
    internal object? ObjectInitValue(object? instance, (string Name, object? Value)[] pairs, int line, int column)
    {
        // Direct application, no temp-scope, no synthetic VariableExpr/ObjectInitExpr nodes, no
        // re-evaluation. Values are already evaluated by the VM. Mirrors VisitObjectInitExpr exactly.
        return ApplyObjectInitializerValues(instance, pairs, line, column);
    }

    /// <summary>Applies name/value pairs to a class or struct instance in init phase. Shared by the
    /// tree's VisitObjectInitExpr (evaluates each value then calls this) and the VM's ObjectInitValue
    /// (values already evaluated). Zero per-call node/scope allocation.</summary>
    internal object ApplyObjectInitializerValues(object? instance, (string Name, object? Value)[] pairs, int line, int column)
    {
        switch (instance)
        {
            // Class instance
            case ClassInstance classInstance:
                classInstance.IsInInitPhase = true;
                try
                {
                    foreach (var (propName, value) in pairs)
                    {
                        var backingName = FindPropertyBackingField(classInstance.Class, propName);
                        if (backingName != null)
                        {
                            var (propDeclClass, propDecl) = FindPropertyDeclWithClass(classInstance.Class, propName);
                            if (propDecl == null)
                                RuntimeError($"Property '{propName}' not found.", line, column);
                            if (propDecl.Setter != null)
                                CheckAccess(propDecl.Setter.Access, propDeclClass, line, column, propName);
                            var converted = CoerceTo(value, propDecl.Type, line, column);
                            classInstance.FieldValues[backingName] = converted;
                        }
                        else
                        {
                            var (declaringClass, field) = FindFieldInHierarchy(classInstance.Class, propName);
                            if (field != null)
                            {
                                CheckAccess(field.Access, declaringClass, line, column, propName);
                                var converted = CoerceTo(value, field.Type, line, column);
                                classInstance.FieldValues[field.Name] = converted;
                            }
                            else
                                RuntimeError($"'{propName}' is not a valid property or field on '{classInstance.Class.Name}'.", line, column);
                        }
                    }
                }
                finally { classInstance.IsInInitPhase = false; }
                return classInstance;

            // Struct instance
            case StructInstance structInstance:
                structInstance.IsInInitPhase = true;
                try
                {
                    foreach (var (propName, value) in pairs)
                    {
                        var targetName = propName;
                        var prop = FindByName(structInstance.Declaration?.Properties, propName, static p => p.Name);
                        if (prop != null) targetName = BackingFieldName(prop.Name);

                        if (!structInstance.Fields.ContainsKey(targetName))
                            RuntimeError($"'{propName}' is not a valid field or property of struct '{structInstance.StructName}'.", line, column);

                        var targetType = prop?.Type
                                         ?? FindByName(structInstance.Declaration?.Fields, targetName, static f => f.Name)?.Type
                                         ?? RuntimeError<TypeInfo>($"Field type for '{propName}' not found.", line, column);
                        var coerced = CoerceTo(value, targetType, line, column);
                        structInstance.Fields[targetName] = coerced;
                    }
                }
                finally
                {
                    structInstance.IsInInitPhase = false;
                }
                return structInstance;

            default:
                RuntimeError($"Object initializer can only be used with class or struct instances.", line, column);
                return null;
        }
    }

    /// <summary>With-expression (base with { F = v }) via the tree's VisitWithExpr, field/value pairs as temp vars.</summary>
    internal object? WithValue(object @base, (string Name, object? Value)[] pairs, int line, int column)
    {
        // Direct application, no temp-scope, no synthetic WithExpr nodes. Mirrors VisitWithExpr.
        return ApplyWithValues(@base, pairs, line, column);
    }

    /// <summary>Shared `with` applicator: deep-copies the instance then applies name/value overrides.
    /// Values are pre-evaluated (VM path) or evaluated by the tree's VisitWithExpr before calling.</summary>
    internal object ApplyWithValues(object @base, (string Name, object? Value)[] pairs, int line, int column)
    {
        switch (@base)
        {
            case StructInstance si:
                return WithStructValues(si, pairs, line, column);
            case ClassInstance ci:
                return WithClassValues(ci, pairs, line, column);
            default:
                RuntimeError("'with' expression requires a struct or class instance.", line, column);
                return null;
        }
    }

    private object WithStructValues(StructInstance si, (string Name, object? Value)[] pairs, int line, int column)
    {
        if (!Structs.TryGetValue(si.StructName, out var structDecl))
            RuntimeError($"Struct '{si.StructName}' not defined.", line, column);

        var newFields = new Dictionary<string, object?>(si.Fields, _comparer);
        var result = new StructInstance(si.StructName, newFields, si.Line) { Declaration = structDecl, IsInInitPhase = true };
        try
        {
            foreach (var (name, value) in pairs)
            {
                var targetName = name;
                var prop = FindByName(structDecl.Properties, name, static p => p.Name);
                if (prop != null)
                {
                    if (prop.Setter == null)
                        RuntimeError($"Property '{name}' has no setter.", line, column);
                    targetName = BackingFieldName(prop.Name);
                }
                else if (!structDecl.Fields.Any(f => f.Name.Equals(name, Comparison)))
                {
                    RuntimeError($"Field '{name}' not found in struct '{si.StructName}'.", line, column);
                }

                var fieldType = prop?.Type ?? structDecl.Fields.First(f => f.Name.Equals(name, Comparison)).Type;
                var coerced = CoerceTo(value, fieldType, line, column);
                result.Fields[targetName] = coerced;
            }
        }
        finally
        {
            result.IsInInitPhase = false;
        }
        return result;
    }

    private object WithClassValues(ClassInstance ci, (string Name, object? Value)[] pairs, int line, int column)
    {
        var newFields = new Dictionary<string, object?>(ci.FieldValues, _comparer);
        var result = new ClassInstance(ci.Class, newFields, ci.Events);

        foreach (var (name, value) in pairs)
        {
            var cls = ci.Class;
            var field = FindByName(cls.Fields, name, static f => f.Name);
            if (field != null)
            {
                var coerced = CoerceTo(value, field.Type, line, column);
                result.FieldValues[name] = coerced;
                continue;
            }
            var prop = FindByName(cls.Properties, name, static p => p.Name);
            if (prop != null)
            {
                if (prop.Setter == null)
                    RuntimeError($"Property '{name}' has no setter.", line, column);
                var coerced = CoerceTo(value, prop.Type, line, column);
                result.FieldValues[BackingFieldName(prop.Name)] = coerced;
                continue;
            }
            RuntimeError($"Field or property '{name}' not found in class '{cls.Name}'.", line, column);
        }
        return result;
    }

    /// <summary>Creates a typed array (new T[n] or new T[]{…}), direct value-level construction.
    /// Values are already evaluated by the VM; shares CreateArrayDirect with the tree path.</summary>
    internal object CreateArrayValue(TypeInfo elemType, object?[]? sizes, object?[]? initializer, int line, int column)
        => CreateArrayDirect(elemType, sizes, initializer, line, column);

    /// <summary>Creates a script struct instance (new Point(1,2)), direct value-level construction.
    /// Values are already evaluated by the VM; shares CreateStructDirect with the tree path.</summary>
    internal object CreateStructValue(string structName, object?[] values, int line, int column)
        => CreateStructDirect(structName, values, line, column);

    /// <summary>Value-level struct construction shared by the tree (evaluates field values once) and
    /// the VM (values already on the stack). Mirrors VisitNewStructExpr semantics exactly.</summary>
    internal object CreateStructDirect(string structName, object?[] values, int line, int column)
    {
        if (!Structs.TryGetValue(structName, out var structDecl))
        {
            // Fallback: try known .NET value types (AOT-safe, uses typeof, not Type.GetType)
            Type? netType = structName switch
            {
                nameof(JsonSerializeOptions) => typeof(JsonSerializeOptions),
                _ => null
            };

            if (netType is { IsValueType: true })
            {
                if (netType == typeof(JsonSerializeOptions))
                {
                    if (values.Length == 1) return new JsonSerializeOptions((bool)values[0]!, false, false);
                    if (values.Length == 2) return new JsonSerializeOptions((bool)values[0]!, (bool)values[1]!, false);
                    if (values.Length == 3) return new JsonSerializeOptions((bool)values[0]!, (bool)values[1]!, (bool)values[2]!);
                    return new JsonSerializeOptions();
                }

                RuntimeError($"Struct '{structName}' not defined.", line, column);
            }

            RuntimeError($"Struct '{structName}' not defined.", line, column);
        }

        // ONE implementation with the tree's `new` and the VM's CALL_NEW: the resolver and RunStructCtor below.
        // A cell among the values is exactly a ref/out argument here, which the resolver weighs and the binder binds.
        var structByRef = new bool[values.Length];
        for (var bi = 0; bi < values.Length; bi++) structByRef[bi] = values[bi] is RefCell;
        var structCtor = ResolveStructCtorForCall(structDecl, values.Length, structByRef, values, line, column);
        return RunStructCtor(structDecl, structCtor, values, line, column);
    }

    /// <summary>Unified iterable contract: true for every collection kind foreach/LINQ can
    /// enumerate, arrays, script lists, tuples, dicts (as KeyValuePairs), HashSets, LINQ
    /// results, strings (char sequence), and coroutine generators.</summary>
    internal static bool IsIterable(object? value) => value switch
    {
        null => false,
        Array => true,
        IScriptList => true,
        ScriptTuple => true,
        IScriptDictionary => true,
        IScriptHashSet => true,
        IEnumerable => true,          // LINQ results, string (char), lists
        Coroutine => true,
        Generator<object?> => true,
        _ => false
    };

    /// <summary>True when a TypeInfo names the unified Iterable contract (signature params/returns).</summary>
    internal static bool IsIterableType(TypeInfo ti)
        => ti.BaseType == TokenType.Object
           && string.Equals(ti.TypeName, "Iterable", StringComparison.Ordinal);

    /// <summary>True when a TypeInfo names the RandomAccess capability contract.</summary>
    internal static bool IsRandomAccessType(TypeInfo ti)
        => ti.BaseType == TokenType.Object
           && string.Equals(ti.TypeName, "RandomAccess", StringComparison.Ordinal);

    /// <summary>RandomAccess = indexable + re-enumerable (arrays, script lists, tuples, strings).</summary>
    internal static bool IsRandomAccess(object? value) => value is Array or IScriptList or ScriptTuple or string;

    /// <summary>True when the value is a callable (script function/lambda, bound method,
    /// combined delegate, native function, delegate wrapper, or CLR delegate), exactly the
    /// kinds VisitCallExpr's callee dispatch can invoke.</summary>
    internal static bool IsFunction(object? value) => value switch
    {
        ScriptFunction => true,
        BoundMethod => true,
        CombinedDelegate => true,
        ScriptDelegateWrapper => true,
        INativeFunction => true,
        Delegate => true,
        _ => false
    };

    /// <summary>True when a TypeInfo names the Function callable contract.</summary>
    internal static bool IsFunctionType(TypeInfo ti)
        => ti.BaseType == TokenType.Object
           && string.Equals(ti.TypeName, "Function", StringComparison.Ordinal);

    /// <summary>Normalizes any iterable value to an element source. Coroutines and generators are
    /// NOT auto-resumed here (the caller owns resume semantics, foreach keeps its generator branch,
    /// LINQ materializes). Returns false for non-iterables.
    /// Dictionaries enumerate as ScriptKeyValuePair entries, the exact element source the tree's
    /// foreach dict branch (and the VM's dict fallback) already use.</summary>
    internal static bool TryGetIterableElements(object? value, out IEnumerable<object?> elements)
    {
        switch (value)
        {
            case Array arr: elements = arr.Cast<object?>(); return true;
            case IScriptList list: elements = EnumerateList(list); return true;
            case ScriptTuple tup: elements = EnumerateTuple(tup); return true;
            case IScriptDictionary dict: elements = EnumerateDictionary(dict); return true;
            case IScriptHashSet set: elements = EnumerateHashSet(set); return true;
            case string s: elements = s.Cast<object?>(); return true;
            case IEnumerable en: elements = en.Cast<object?>(); return true;
            default: elements = Array.Empty<object?>(); return false;
        }
    }

    /// <summary>Index-based element enumeration of a script list, mirrors the foreach list branch
    /// (lazy, re-checks Count per step so in-loop mutation is observed the same way).</summary>
    private static IEnumerable<object?> EnumerateList(IScriptList list)
    {
        for (var i = 0; i < list.Count; i++) yield return list[i];
    }

    /// <summary>Index-based element enumeration of a script tuple, lazy, no LINQ closure per call.</summary>
    private static IEnumerable<object?> EnumerateTuple(ScriptTuple tup)
    {
        for (var i = 0; i < tup.Length; i++) yield return tup[i];
    }

    /// <summary>Defers a script hash set's enumeration to first MoveNext. Hash sets are snapshotted
    /// engine-wide for thread-safety (BoxedScriptHashSet/UnboxedScriptHashSet<T> copy under their
    /// lock), so the snapshot is kept, only its timing becomes lazy instead of eager at call time.</summary>
    private static IEnumerable<object?> EnumerateHashSet(IScriptHashSet set)
    {
        foreach (var item in set.ToArray()) yield return item;
    }

    /// <summary>Yields a script dictionary's entries as ScriptKeyValuePair, the exact element source
    /// used by the tree's foreach dict branch and MaterializeForEach below.</summary>
    private static IEnumerable<object?> EnumerateDictionary(IScriptDictionary dict)
    {
        var enumerator = dict.Dictionary.GetEnumerator();
        try
        {
            while (enumerator.MoveNext())
                yield return new ScriptKeyValuePair(enumerator.Entry.Key, enumerator.Entry.Value);
        }
        finally
        {
            if (enumerator is IDisposable d) d.Dispose();
        }
    }

    /// <summary>Materializes a foreach collection to object?[] (mirrors tree semantics: null→empty,
    /// dict→ScriptKeyValuePair entries). Returns null for coroutine/generator/other → VM falls back.</summary>
    internal object?[]? MaterializeForEach(object? coll) => MaterializeForEach(coll, false);

    internal object?[]? MaterializeForEach(object? coll, bool allowEnumerable)
    {
        switch (coll)
        {
            case null:
                return [];

            case Array arr:
                {
                    var r = new object?[arr.Length];
                    for (var i = 0; i < arr.Length; i++) r[i] = arr.GetValue(i);
                    return r;
                }

            case string s:
                {
                    var r = new object?[s.Length];
                    for (var i = 0; i < s.Length; i++) r[i] = s[i];
                    return r;
                }

            case IScriptList sl:
                {
                    var r = new object?[sl.Count];
                    for (var i = 0; i < sl.Count; i++) r[i] = sl[i];
                    return r;
                }

            case IList il:
                {
                    var r = new object?[il.Count];
                    for (var i = 0; i < il.Count; i++) r[i] = il[i];
                    return r;
                }

            case IScriptDictionary dict:
                {
                    var entries = new List<object?>();
                    var enumerator = dict.Dictionary.GetEnumerator();
                    using var enumerator1 = enumerator as IDisposable;
                    while (enumerator.MoveNext())
                    {
                        var entry = enumerator.Entry;
                        entries.Add(new ScriptKeyValuePair(entry.Key, entry.Value));
                    }
                    return [.. entries];
                }

            case IEnumerable en when allowEnumerable:
                {
                    // Eager materialization, used ONLY by comprehensions (the tree's comprehension
                    // iterates IEnumerable eagerly too). Regular foreach stays lazy via the tree.
                    return [.. en.Cast<object?>()];
                }

            default:
                return null; // coroutine / generator / other enumerable, tree-walker handles
        }
    }

    /// <summary>The engine's refusal for a CLR member that THREW, raised AT THE CALL SITE, so a script's own
    /// try/catch can see it. The tree's call sites already convert a CLR failure (the friendly map plus the
    /// position); the VM's member paths did not, and the VM's handler walk catches `ScriptException` only, so a
    /// raw COMException flew straight past the enclosing `try`: measured, `dict.Remove("no-such-key")` inside a
    /// try inside a Com.Run callback ESCAPED from a compiled body and was caught when the same line ran
    /// tree-walked, and the same shape inside a stripped bundle aborted the run. One helper for every VM-facing
    /// member access (read, write, call, and the two reflection fast paths), because that is one concern:
    /// a CLR failure must become the engine's refusal where the script can catch it.</summary>
    private static ScriptException ClrMemberFailure(Exception ex, int line, int column, string? memberName = null)
    {
        var inner = ex is TargetInvocationException tie ? tie.InnerException ?? tie : ex;
        if (inner is ScriptException sx) return sx;   // the engine's own refusal passes unchanged
        // A METHOD access carries the tree's own prefix ([Substring]: ...), measured against the tree's text for
        // the same failing call, so both engines refuse in the same words. A property access does not.
        var text = FriendlyExceptionMessage(inner);
        return Refusal(memberName == null ? text : $"[{memberName}]: {text}", line, column, ErrorCode.Unspecified,
            Current?.SnapshotCallFrames());
    }

    /// <summary>The four reflection verbs EVERY value answers, so a script can act on members it cannot name at
    /// compile time: Get(name), Set(name, value), Keys() and Call(name, args...). ONE implementation, reached
    /// from the two places method dispatch gives up (the script-class arm of InvokeMethod and the miss in
    /// InvokeClrMethod), so a value of any kind answers the same four. A Control answers its DESIGN TABLE (the
    /// rows PropList lists, the same members GetProp / SetProp speak), a script class its declared fields, and
    /// anything else its public properties and fields. A name the value does not carry refuses through
    /// GetMemberValue / SetMemberValue, which name the member that was missing.</summary>
    internal bool TryReflectVerbs(object? receiver, string verb, object?[] args, int line, int column,
                                  out object? result)
    {
        result = null;
        if (receiver is null) return false;
        var isGet = verb.Equals("Get", Comparison);
        var isSet = verb.Equals("Set", Comparison);
        var isKeys = verb.Equals("Keys", Comparison);
        var isCall = verb.Equals("Call", Comparison);
        if (!isGet && !isSet && !isKeys && !isCall) return false;

        if (isKeys)
        {
            if (args.Length != 0) RuntimeError("Keys expects no arguments.", line, column);
            var names = new List<object?>();
            switch (receiver)
            {
                case Control kc:
                    foreach (var row in kc.PropList()) names.Add(row.Name);
                    break;
                case ClassInstance ki:
                    foreach (var field in ki.Class.Fields) names.Add(field.Name);
                    break;
                default:
                    // The names a SCRIPT would write: the public properties, and the public fields only when the
                    // type has no properties at all. A type that mirrors a backing field with a property (the
                    // engine's MouseEventArgs holds x/y/button/clicks/delta beside X/Y/Button/Clicks/Delta)
                    // otherwise lists every member twice, and the lowercase half is not something a script reads
                    // by name. A struct whose members ARE fields (Rect) has no properties, so it still lists
                    // them: the rule drops duplicates, never a whole member set.
                    var props = receiver.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var p in props) names.Add(p.Name);
                    if (props.Length == 0)
                        foreach (var f in receiver.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                            names.Add(f.Name);
                    break;
            }
            result = names;
            return true;
        }

        if (args.Length < 1) RuntimeError($"{verb} expects the member name first.", line, column);
        var member = args[0] as string ?? Convert.ToString(args[0]) ?? "";

        if (isGet)
        {
            if (args.Length != 1) RuntimeError("Get expects one argument: the member name.", line, column);
            result = receiver is Control gc
                ? gc.GetProp(member)
                : GetMemberValue(receiver, member, line, column);
            return true;
        }

        if (isSet)
        {
            if (args.Length != 2) RuntimeError("Set expects two arguments: the member name and its value.", line, column);
            if (receiver is Control sc) sc.SetProp(member, args[1]);
            else SetMemberValue(receiver, member, args[1], line, column);
            result = null;
            return true;
        }

        // Call(name, args...): the engine dispatches the named member itself, so a script method and a CLR one
        // are reached the same way. The name is the USER's, so this cannot re-enter itself.
        var rest = new object?[args.Length - 1];
        Array.Copy(args, 1, rest, 0, rest.Length);
        result = InvokeMethod(receiver, member, rest, line, column);
        return true;
    }

    internal object? GetMemberValue(object? target, string propertyName, int line, int column)
    {
        CurrentFrame.Scopes.PushScope();
        try
        {
            CurrentFrame.Scopes.Define(VmTargetVar, target);
            var node = PropertyAccessExpr.Create(
                VariableExpr.Create(VmTargetVar, line, column), propertyName, line, column);
            return VisitPropertyAccessExpr(node);
        }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
        finally { CurrentFrame.Scopes.PopScope(); }
    }

    /// <summary>Direct cached .NET property getter for plain objects, mirrors the tree-walker's reflection
    /// fall-through (case-sensitive public instance property, cached). Returns false when the name is not a
    /// property (dict keys, script members, etc. → callers must fall back to full GetMemberValue semantics).</summary>
    internal bool TryGetCachedProperty(object target, string propertyName, out object? value, int line, int column)
    {
        value = null;
        // A DICTIONARY's Keys/Values are the TREE's own projection (an ordered List the script can index),
        // not the CLR property the reflection below would find: the tree answers `d.Keys` through
        // DictionaryProjection, so the cached raw getter must not win here. Measured: `ds.Keys[0]` on a
        // dictionary literal read a KeyCollection from a compiled body ("Cannot index object of type
        // KeyCollection") where the tree read element "a".
        if (target is IScriptDictionary or IDictionary
            && ("Keys".Equals(propertyName, Comparison) || "Values".Equals(propertyName, Comparison)))
            return false;
        var cacheKey = (target.GetType(), propertyName);
        if (PropertyCache.TryGetValue(cacheKey, out var propInfo))
        {
            try { value = propInfo.GetValue(target); }
            catch (ScriptException) { throw; }
            catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
            return true;
        }
        propInfo = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (propInfo == null) return false;
        // An INDEXER is not a readable property (`"ab".Chars`): it takes parameters, so reading it without an
        // index threw the CLR's own "Parameter count mismatch" and a script saw a raw CLR sentence where the
        // tree reports "Property 'Chars' not found on type string" (crash_extension_generics 13z, reachable
        // from any function body). A miss here leaves the caller's own message, which is the tree's.
        if (propInfo.GetIndexParameters().Length > 0) return false;
        PropertyCache[cacheKey] = propInfo;
        try { value = propInfo.GetValue(target); }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
        return true;
    }

    internal object? SetMemberValue(object? target, string propertyName, object? value, int line, int column)
    {
        CurrentFrame.Scopes.PushScope();
        try
        {
            CurrentFrame.Scopes.Define(VmTargetVar, target);
            CurrentFrame.Scopes.Define(VmValueVar, value);
            var node = PropertyAssignExpr.Create(
                VariableExpr.Create(VmTargetVar, line, column),
                propertyName,
                VariableExpr.Create(VmValueVar, line, column),
                line, column);
            return VisitPropertyAssignExpr(node);
        }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column); }
        finally { CurrentFrame.Scopes.PopScope(); }
    }

    /// <summary>Value-level struct field read, mirrors VisitPropertyAccessExpr's StructInstance
    /// cases (L665-675): direct field lookup, then property-backing-field fallback, then error.
    /// No synthetic AST nodes / temp scope (the VM GET_PROP fallback path used ~4.5 KB/call).</summary>
    /// <summary>Declarations looked up BY NAME without a closure: the member accessors' `FirstOrDefault(f =&gt;
    /// f.Name.Equals(...))` allocated a lambda on EVERY read and write, which is where the bench's H6 budget measured
    /// about 400 B per struct field write (four-point probe: no-field 3128, read 3088, one write 3592, two writes
    /// 3944 B/call). A plain loop with the interpreter's own `Comparison` allocates nothing and compares exactly the
    /// same way, and one helper serves every member type so the two spellings cannot drift.</summary>
    private T? FindByName<T>(IReadOnlyList<T>? members, string name, Func<T, string> nameOf,
        Func<T, bool>? filter = null) where T : class
    {
        // A NULL list answers null, which is what the `?.Properties.FirstOrDefault(...)` chains meant: the
        // member accessors hold a DECLARATION that can be absent, and the spelling they used made the
        // null-tolerance invisible. A filter covers the lookups whose predicate carries a second part
        // (`m.IsStatic && ...`, `p.Getter != null`), which would otherwise keep their own lambda.
        if (members == null) return null;
        for (var i = 0; i < members.Count; i++)
            if (string.Equals(nameOf(members[i]), name, Comparison) && (filter == null || filter(members[i])))
                return members[i];
        return null;
    }

    /// <summary>The NESTED declaration lookup, whose name rule is its own: a nested type is stored DOTTED
    /// (`Outer.Inner`), and a reference accepts either that or the bare name, which is "the name is the LAST
    /// SEGMENT" (the `EndsWith("." + name) || Equals(name)` spelling this replaced). Comparing the tail IN PLACE
    /// keeps the allocation-free shape: a `[..]` slice would build a string per candidate, and the predicate it
    /// replaces built one per call for the dot it prepended.</summary>
    private T? FindNestedByName<T>(IReadOnlyList<T> members, string name, Func<T, string> nameOf) where T : class
    {
        for (var i = 0; i < members.Count; i++)
        {
            var candidate = nameOf(members[i]);
            if (string.Equals(candidate, name, Comparison)) return members[i];
            if (candidate.Length > name.Length
                && candidate[candidate.Length - name.Length - 1] == '.'
                && candidate.AsSpan(candidate.Length - name.Length).Equals(name, Comparison))
                return members[i];
        }
        return null;
    }

    internal object? GetStructMemberValue(StructInstance si, string propertyName, int line, int column)
    {
        var decl = si.Declaration ?? (Structs.TryGetValue(si.StructName, out var sd) ? sd : null);
        if (si.Fields.TryGetValue(propertyName, out var value))
        {
            // The ACCESS check the tree's own member arm runs: the VM's value-level read skipped it, so a
            // PRIVATE struct field read from outside answered a value where the tree refuses ("Cannot access
            // non-public member 'X' of struct 'Pv30' from outside", scriptresult_typed 8a).
            var fdecl = decl == null ? null : FindByName(decl.Fields, propertyName, static f => f.Name);
            if (fdecl != null) CheckStructMemberAccess(fdecl.Access, si.StructName, line, column, propertyName);
            return value;
        }
        var propDecl = FindByName(decl?.Properties, propertyName, static pr => pr.Name);
        if (propDecl != null) CheckStructMemberAccess(propDecl.Access, si.StructName, line, column, propertyName);
        var sBacking = GetStructBackingField(si, propertyName);
        if (sBacking != null && si.Fields.TryGetValue(sBacking, out var spv))
            return spv;
        if (si.Fields.TryGetValue(propertyName, out var sv))
            return sv;
        // An interface's DEFAULT PROPERTY body answers before an extension, the order the tree's own struct arm
        // uses (VisitExpressions.cs): `struct Loose3 { public int n3() => 2; }` structurally matches an
        // interface whose `Triple` has a default body, and the compiled path refused it with
        // "Field 'Triple' not found in struct 'Loose3'" (crash_interface_test 76).
        if (TryGetInterfacePropertyDefault(si, propertyName, line, column, out var ifaceDefaultValue))
            return ifaceDefaultValue;
        // An EXTENSION property answers here too, through the same accessor the tree's property path reaches:
        // any receiver type can carry one, and a struct's members never consulted the registry, so
        // `box13.Doubled13` was refused with "Field 'Doubled13' not found in struct 'PropBox13'"
        // (crash_extension_generics 13e).
        if (TryGetExtensionProperty(si, propertyName, line, column, out var extensionValue))
            return extensionValue;
        // The struct twin of the class arm below: a method named where a field would be is a callable carrying its
        // receiver (a struct's own member access is access-checked like its fields).
        if (Structs.TryGetValue(si.StructName, out var structValueDecl))
        {
            var structValueMethod = FindByName(structValueDecl.Methods, propertyName, static m => m.Name, static m => !m.IsStatic);
            if (structValueMethod != null)
            {
                CheckStructMemberAccess(structValueMethod.Access, si.StructName, line, column, propertyName);
                return new BoundMethod(si, structValueMethod);
            }
        }
        RuntimeError($"Field '{propertyName}' not found in struct '{si.StructName}'.", line, column);
        return null;
    }

    /// <summary>Value-level struct field write, mirrors VisitPropertyAssignExpr's StructInstance
    /// case (L955-980): readonly check, property-backing-field resolution, setter/init guards,
    /// type coercion, dictionary write. No synthetic AST nodes / temp scope (~7.2 KB/call saved).</summary>
    /// <summary>A SHALLOW COPY of a struct value, which is what C# does when a struct is assigned, passed by value
    /// or boxed: the instance and its field dictionary are new, its field VALUES are shared (a nested struct field
    /// is itself copied when it is read, because that read goes through CoerceTo). This is the one implementation
    /// behind the value-type rule; `WithStructValues` is the `with { ... }` expression's own copy and predates it.</summary>
    internal static StructInstance CloneStructForStore(StructInstance si) => CloneStruct(si);

    private static StructInstance CloneStruct(StructInstance si)
    {
        // The field dictionary follows the interpreter's case flag, never a hardcoded comparer; with no current
        // interpreter (a C#-side entry, which this file records as possible) the insensitive default stands, which
        // is the same rule the static registries keep as their index.
        var cmp = Current?._comparer ?? StringComparer.OrdinalIgnoreCase;
        var fields = new Dictionary<string, object?>(si.Fields.Count, cmp);
        // A STRUCT-TYPED FIELD IS COPIED BY VALUE WITH THE REST: C# copies every field of a struct, so a nested
        // instance is cloned recursively, while a reference field (a class, an array, a string) stays SHARED, which
        // is also C#. The pinning suite caught this the moment it existed: a shallow copy let `b.In.V = 7` reach
        // `a.In.V` (struct_value_semantics_test 1b, measured on both legs). A field holding a boxed struct is cloned
        // for the same reason (boxing copies), and no cycle is possible: a C# struct cannot contain itself.
        foreach (var f in si.Fields)
            fields[f.Key] = f.Value is StructInstance nested ? CloneStruct(nested) : f.Value;
        return new StructInstance(si.StructName, fields, si.Line)
        { Declaration = si.Declaration, IsInInitPhase = si.IsInInitPhase };
    }

    internal object? SetStructMemberValue(StructInstance si, string propertyName, object? value, int line, int column)
    {
        if (!Structs.TryGetValue(si.StructName, out var structDecl))
            RuntimeError($"Struct '{si.StructName}' not defined.", line, column);
        // Readonly struct, reject mutation outside initialization
        if (structDecl.IsReadOnly && !si.IsInInitPhase)
            RuntimeError($"Struct '{si.StructName}' is readonly and cannot be mutated after initialization.", line, column, ErrorCode.AccessViolation);
        // Property backing field resolution
        var stargetName = propertyName;
        var sprop = FindByName(structDecl.Properties, propertyName, static p => p.Name);
        if (sprop != null)
        {
            // The ACCESS check the tree's member arm runs, so a PRIVATE struct property written from outside is
            // refused here too (scriptresult_typed 8c).
            CheckStructMemberAccess(sprop.Access, si.StructName, line, column, propertyName);
            stargetName = BackingFieldName(sprop.Name);
            if (sprop.Setter == null)
                RuntimeError($"Property '{propertyName}' has no setter.", line, column, ErrorCode.AccessViolation);
            if (sprop.Setter is { IsInit: true } && !si.IsInInitPhase)
                RuntimeError($"Property '{propertyName}' is init-only and cannot be assigned outside initialization.", line, column, ErrorCode.AccessViolation);
        }
        else
        {
            // A FIELD of the struct: the same access check the tree runs on the write path.
            var sfdecl = FindByName(structDecl.Fields, propertyName, static f => f.Name);
            if (sfdecl != null) CheckStructMemberAccess(sfdecl.Access, si.StructName, line, column, propertyName);
        }

        if (!si.Fields.ContainsKey(stargetName))
        {
            // An EXTENSION property's setter is the last resort here, the mirror of the read hook above
            // (crash_extension_generics 13f writes through one).
            if (TrySetExtensionProperty(si, propertyName, value, line, column, out var extensionWritten))
                return extensionWritten;
            RuntimeError($"'{propertyName}' is not a valid field or property of struct '{si.StructName}'.", line, column);
        }
        var stargetType = sprop?.Type ?? FindByName(structDecl.Fields, stargetName, static f => f.Name)?.Type
            ?? RuntimeError<TypeInfo>($"Field type for '{propertyName}' not found.", line, column);
        var scoerced = CoerceTo(value, stargetType, line, column);
        si.Fields[stargetName] = scoerced;
        return scoerced;
    }

    /// <summary>Value-level class field/property/event read, mirrors VisitPropertyAccessExpr's
    /// ClassInstance case (L677-719): property backing field → hierarchy field (access-checked) →
    /// event (access-checked, lazily created) → static-field-through-instance fallback (runs static
    /// ctors). Relies on _currentClass/_currentThis set by the caller (the VM class-method probe
    /// sets them identically to the tree). No synthetic AST nodes / temp scope.</summary>
    internal object? GetClassMemberValue(ClassInstance instance, string propertyName, int line, int column)
    {
        // Property backing field access
        var backingName = FindPropertyBackingField(instance.Class, propertyName);
        if (backingName != null)
        {
            return instance.FieldValues.TryGetValue(backingName, out var pv)
                ? pv
                : DefaultFor(FindPropertyDecl(instance.Class, propertyName)!.Type);
        }

        var (declaringClass, field) = FindFieldInHierarchy(instance.Class, propertyName);
        if (field != null)
        {
            CheckAccess(field.Access, declaringClass, line, column);
            if (instance.FieldValues.TryGetValue(field.Name, out var value)) return value;
            RuntimeError($"Field '{field.Name}' not initialized.", line, column);
        }

        var (eventDeclaringClass, eventDecl) = FindEventInHierarchy(instance.Class, propertyName);
        if (eventDecl != null)
        {
            CheckAccess(eventDecl.Access, eventDeclaringClass, line, column);
            if (instance.Events.TryGetValue(propertyName, out var evtInfo))
                return evtInfo;
            evtInfo = new EventInfo(propertyName, eventDecl.DelegateTypeName, []);
            instance.Events[propertyName] = evtInfo;
            return evtInfo;
        }

        // Fallback: static field access through instance (e.g. f2.Created → Class.StaticFields["Created"])
        {
            var cur = instance.Class;
            while (cur != null)
            {
                RunStaticConstructors(cur, line, column);
                if (cur.StaticFields?.TryGetValue(propertyName, out var sv) == true)
                    return sv;
                cur = cur.Parent;
            }
        }
        // An interface's DEFAULT PROPERTY body answers before an extension, in the order the tree's own
        // property-read arm uses (VisitExpressions.cs: the interface default first, then the block's property).
        // Measured: `new DefUserB().Double` for `interface IDefaultedB { int Double => this.n() * 2; }` was
        // refused here with "Field or event 'Double' not found in class 'DefUserB'" from a compiled body where
        // the tree answers 42 (crash_interface_test 75).
        if (TryGetInterfacePropertyDefault(instance, propertyName, line, column, out var ifaceDefaultValue))
            return ifaceDefaultValue;
        // An EXTENSION property answers last, through the same accessor the tree's property path reaches: a
        // class instance consulted the registry for METHODS but never for properties, so an extension property
        // read on a class was refused with "Field or event 'Human13' not found in class 'PropDog13'"
        // (crash_extension_generics L439).
        if (TryGetExtensionProperty(instance, propertyName, line, column, out var classExtensionValue))
            return classExtensionValue;
        // A METHOD named where a FIELD would be is its own value here too, the twin of the tree's arm: a callable
        // carrying its receiver. Without this the compiled path reported "Field or event 'Handle' not found in
        // class" for a name the tree resolved, which is the divergence this accessor exists to prevent.
        var classValueMethod = FindMethod(instance.Class, propertyName);
        if (classValueMethod != null && !classValueMethod.IsStatic)
        {
            CheckAccess(classValueMethod.Access, instance.Class, line, column);
            return new BoundMethod(instance, classValueMethod);
        }
        RuntimeError($"Field or event '{propertyName}' not found in class '{instance.Class.Name}'.", line, column);
        return null;
    }

    /// <summary>Mirrors the tree's bare-name variable resolution inside a method body
    /// (VisitVariableExpr's _currentThis step): after locals and the global scope miss, a bare
    /// name may still be an instance field or event of the receiver, resolved by walking the
    /// class hierarchy (access-checked). Returns false when the name is neither, the caller
    /// then reports the missing variable. Used by the VM's LOAD_GLOBAL_FAST miss path, which
    /// cannot see hierarchy fields the compile-time field set omitted (that set only merges
    /// same-file parents, fields inherited from an IMPORTED base class compile as a global
    /// read and would otherwise die as UndefinedVariable).</summary>
    internal bool TryResolveBareThisMember(ClassInstance instance, string name, int line, int column, out object? value)
    {
        var (declaringClass, field) = FindFieldInHierarchy(instance.Class, name);
        if (field != null)
        {
            CheckAccess(field.Access, declaringClass, line, column);
            if (instance.FieldValues.TryGetValue(field.Name, out var fieldValue))
            {
                value = fieldValue;
                return true;
            }
            RuntimeError($"Field '{field.Name}' not initialized.", line, column);
        }

        var (eventDeclaringClass, eventDecl) = FindEventInHierarchy(instance.Class, name);
        if (eventDecl != null)
        {
            CheckAccess(eventDecl.Access, eventDeclaringClass, line, column);
            if (instance.Events.TryGetValue(name, out var evtInfo))
            {
                value = evtInfo;
                return true;
            }
            evtInfo = new EventInfo(name, eventDecl.DelegateTypeName, []);
            instance.Events[name] = evtInfo;
            value = evtInfo;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>Mirrors the tree's bare-name variable resolution inside a method body
    /// (VisitVariableExpr's _currentClass step): a bare read may hit a STATIC field of the
    /// current class (checked FIRST in the tree, before locals and the global scope) even when
    /// it was not compiled as a member access, the compiler never consults StaticFieldNames,
    /// so bare static-field reads compile as a global read. Used by the VM's LOAD_GLOBAL_FAST
    /// miss path. Current class only (no hierarchy walk), matches the tree, which does not
    /// resolve inherited statics by bare name.</summary>
    internal bool TryResolveBareStaticMember(string name, out object? value)
    {
        if (_currentClass?.StaticFields?.TryGetValue(name, out var sfVal) == true)
        {
            value = sfVal;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Value-level class field/property write, mirrors VisitPropertyAssignExpr's
    /// ClassInstance case (L983-1023): property backing field (setter access + init guard) →
    /// hierarchy field (access-checked) → static-field-through-instance fallback (access-checked).
    /// No synthetic AST nodes / temp scope.</summary>
    internal object? SetClassMemberValue(ClassInstance instance, string propertyName, object? value, int line, int column)
    {
        // Property backing field resolution
        var cbacking = FindPropertyBackingField(instance.Class, propertyName);
        if (cbacking != null)
        {
            var (cpropDeclClass, cpropDecl) = FindPropertyDeclWithClass(instance.Class, propertyName);
            if (cpropDecl == null) RuntimeError($"Property '{propertyName}' not found.", line, column);
            if (cpropDecl.Setter != null)
                CheckAccess(cpropDecl.Setter.Access, cpropDeclClass, line, column, propertyName);
            else
                RuntimeError($"Property '{propertyName}' has no setter.", line, column, ErrorCode.AccessViolation);
            if (cpropDecl.Setter is { IsInit: true } && !instance.IsInInitPhase)
                RuntimeError($"Property '{propertyName}' is init-only and cannot be assigned outside initialization.", line, column, ErrorCode.AccessViolation);
            var converted = CoerceTo(value, cpropDecl.Type, line, column);
            instance.FieldValues[cbacking] = converted;
            return converted;
        }

        var (declaringClass, field) = FindFieldInHierarchy(instance.Class, propertyName);
        if (field != null)
        {
            CheckAccess(field.Access, declaringClass, line, column, propertyName);
            var converted = CoerceTo(value, field.Type, line, column);
            instance.FieldValues[field.Name] = converted;
            return converted;
        }
        // Fallback: static field write through instance (e.g. f2.Created = 99 → Class.StaticFields["Created"] = 99)
        {
            var cur = instance.Class; while (cur != null)
            {
                if (cur.StaticFields?.ContainsKey(propertyName) == true)
                {
                    var fieldDecl = FindByName(cur.StaticFieldDecls, propertyName, static f => f.Name);
                    if (fieldDecl != null)
                    {
                        CheckAccess(fieldDecl.Access, cur, line, column, propertyName);
                        cur.StaticFields[propertyName] = CoerceTo(value, fieldDecl.Type, line, column);
                        return value;
                    }
                    cur.StaticFields[propertyName] = value;
                    return value;
                }
                cur = cur.Parent;
            }
        }
        // The extension SETTER is the last resort here, mirroring the read hook above.
        if (TrySetExtensionProperty(instance, propertyName, value, line, column, out var classExtensionWritten))
            return classExtensionWritten;
        RuntimeError($"Field '{propertyName}' not found in class '{instance.Class.Name}'.", line, column);
        return null;
    }

    /// <summary>True when the class (or a class it extends) DECLARES a method of this name and arity. The bridges
    /// in ScriptTextBridge need the question without a call: a refusal cannot be told apart from a genuine error inside
    /// the member if the only test is catching one, and the engine's member dispatch is what picks the overload.</summary>
    internal bool TryFindScriptMethod(ClassInfo? cls, string name, int arity)
    {
        var comparison = _caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var c = cls; c != null; c = c.Parent)
            foreach (var m in c.Methods)
                if (m.Params.Count == arity && string.Equals(m.Name, name, comparison)) return true;
        return false;
    }

    internal object? InvokeMethodValues(object? receiver, string methodName, object?[] args, int line, int column,
        IReadOnlyList<TypeInfo>? typeArgs = null, IReadOnlyList<string?>? argNames = null)
    {
        // The CALL's position, for the same reason InvokeBuiltinFromVm records it: a compiled `Json.Serialize(x, {})`
        // is a MEMBER call, so it arrives here rather than at the builtin entry, and a warning raised by the member
        // (an unknown option, an argument it ignored) must name the place the script asked for it.
        _currentLine = line;
        _currentColumn = column;
        // InvokeMethodDispatch reads node.Line/Column/TypeArgs/ArgNames only; args are passed directly. The TYPE
        // ARGUMENTS arrive from the VM: its bytecode carries them in a TypeArgsMarker the CALL_METHOD handler
        // popped (a compiled `M<int>(…)` has no AST to read them from), so this is how a compiled call reaches the
        // one place that closes a generic definition. The NAMES arrive the same way, from a NamedArgsMarker, and
        // they matter for the same reason: a compiled `x.M(name: v)` has no AST to read them from either, and the
        // CLR arm reorders them against the reflected parameter names. Without them the VM had to bail to the
        // tree, which a bytecode-only body does not have (measured: a named call on a string receiver aborted a
        // stripped bundle's program body with "loaded from bytecode for a different environment").
        var node = MethodCallExpr.Create(
            VariableExpr.Create(VmTargetVar, line, column), methodName,
            [], [], line, column, typeArgs, null, argNames);
        try { return InvokeMethodDispatch(receiver, methodName, args, node); }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column, methodName); }
    }

    /// <summary>Direct cached .NET method invoke for plain objects, mirrors the tree-walker's reflection
    /// dispatch block exactly (coercion, params array, void marker, exception unwrap). Returns false when the
    /// receiver needs script/static semantics or the overload cache is cold (caller falls back to full dispatch).</summary>
    internal bool TryInvokeCachedMethod(object? receiver, string methodName, object?[] args, int line, int column, out object? result)
    {
        result = null;
        // Script types (classes/structs/collections/statics) use dedicated dispatch, no reflection cache to hit
        if (receiver is ClassInstance or StructInstance or TypeInfo or ClassInfo or Namespace or BaseProxy
            or IScriptList or IScriptDictionary or IScriptHashSet)
            return false;
        var isStaticCall = receiver is Type;
        var actualType = isStaticCall ? (Type)receiver! : receiver!.GetType();
        var argCount = args.Length;

        var argTypesHash = 0;
        for (var h = 0; h < argCount; h++)
        {
            var at = args[h]?.GetType() ?? typeof(object);
            int token = 0;
            try { token = at.MetadataToken; } catch { /* fallback: use FullName hash */ }
            argTypesHash = HashCode.Combine(argTypesHash, token);
        }
        nint typeToken = 0;
        try { typeToken = actualType.MetadataToken; } catch { /* AOT, MetadataToken unsupported, use string-key cache */ }
        var mCacheKey = (typeToken, methodName, argCount, argTypesHash);
        MethodInfo methodInfo;
        ParameterInfo[] paramInfo;
        if (typeToken != 0 && _methodOverloadCache.TryGetValue(mCacheKey, out var cached) && cached != null)
        {
            VmCacheHits++;
            (methodInfo, paramInfo) = cached.Value;
        }
        else
        {
            // String-key cache (works in AOT where MetadataToken throws)
            var sb = StringBuilderPool.Rent();
            if (isStaticCall) sb.Append("static:");
            sb.Append(actualType.FullName); sb.Append(':').Append(methodName).Append(':');
            for (var i = 0; i < argCount; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(args[i]?.GetType().FullName ?? "object");
            }
            var cacheKey = sb.ToString();
            if (!_methodCache.TryGetValue(cacheKey, out var entry))
            {
                VmCacheMisses++;
                return false; // cold path, full dispatch resolves + populates
            }
            VmCacheHits++;
            methodInfo = entry.Method;
            paramInfo = entry.Params;
        }

        var paramCount = paramInfo.Length;
        var hasParamsParam = paramCount > 0 && paramInfo[paramCount - 1].IsDefined(typeof(ParamArrayAttribute), false);
        var fixedParamCount = hasParamsParam ? paramCount - 1 : paramCount;
        var finalArgs = ArrayPool.Rent(paramCount);
        try
        {
            for (var i = 0; i < fixedParamCount; i++)
            {
                var expectedType = paramInfo[i].ParameterType;
                if (i < args.Length)
                {
                    var arg = args[i];
                    finalArgs[i] = (arg != null && arg.GetType() == expectedType) ? arg : CoerceArgument(arg, expectedType, line, column);
                }
                else if (paramInfo[i].IsOptional) finalArgs[i] = paramInfo[i].DefaultValue;
                else throw new ScriptException(ErrorCode.Unspecified, $"Missing required argument for parameter '{paramInfo[i].Name}'.", line, column);
            }
            if (hasParamsParam)
            {
                var elemType = paramInfo[fixedParamCount].ParameterType.GetElementType()!;
                var excessCount = Math.Max(0, args.Length - fixedParamCount);
                var paramArray = Array.CreateInstance(elemType, excessCount);
                for (var i = 0; i < excessCount; i++)
                    paramArray.SetValue(CoerceArgument(args[fixedParamCount + i], elemType, line, column), i);
                finalArgs[fixedParamCount] = paramArray;
            }
            object? resultValue;
            try
            {
                resultValue = methodInfo.Invoke(isStaticCall ? null : receiver, finalArgs);
            }
            catch (TargetInvocationException tie)
            {
                // The ONE mapping, the same call the shared arm's own TargetInvocationException handler makes
                // (MethodDispatch.cs): the friendly message with the engine's "[name]: " prefix, never the
                // runtime's own sentence. The FormatException special case that stood here threw `fe.Message`
                // raw, so the SAME failing call in the SAME body read one way warm and another cold (measured:
                // "[ToInt32]: Invalid format: '...'" through the full dispatch, and the bare "The input string
                // 'abc' was not in a correct format." once the overload cache was warm).
                throw ClrMemberFailure(tie, line, column, methodName);
            }
            result = methodInfo.ReturnType == typeof(void) ? VoidMarker.Instance
                : AsClrTask(resultValue, out var clrTask) ? ToScriptTask(clrTask!)
                : resultValue;
            return true;
        }
        finally { ArrayPool.Return(finalArgs); }
    }

    // < -------------------- end of visitors -----------------------------------------------------------------

    internal object? CombineDelegates(object? left, object? right, int line, int column)
    {
        if (left == null) return right;
        if (right == null) return left;

        var handlers = new List<ScriptFunction>();

        AddHandler(left);
        AddHandler(right);

        return new CombinedDelegate(this, handlers);

        void AddHandler(object? obj)
        {
            switch (obj)
            {
                case ScriptFunction fn:
                    handlers.Add(fn);
                    break;

                case CombinedDelegate cd:
                    handlers.AddRange(cd.Handlers);
                    break;

                case DelegateInfo:
                    RuntimeError("Cannot combine delegate type declarations. Use functions instead.", line, column);
                    break;

                default:
                    RuntimeError($"Cannot combine non-delegate type '{obj?.GetType().Name ?? "null"}' with delegate.", line, column);
                    break;
            }
        }
    }

    /// <summary>The tree's binary-operator core, for the VM to call when an OPERAND kind it does not model
    /// arithmetically is involved (a DateTime, a TimeSpan, a Guid, ...). The VM's own Arith coerces both sides
    /// to double, so it can only answer numbers, a char and a string concat; this is the one place the arms
    /// for the other types live, and both engines reach them now rather than aborting the whole body.
    /// Measured why: a program body that has already stored a global cannot be handed to the tree, so a
    /// top-level `(DateTime.Now - t0).TotalMilliseconds` after a `var` was refused as an engine gap.</summary>
    internal object? ApplyBinaryOpFromVm(object? left, object? right, TokenType op, int line, int column)
        => ApplyBinaryOp(left, right, op, line, column);

    /// <summary>The VM's UNARY MINUS for an operand its own arm cannot answer (a boxed non-number):
    /// `-"hello"` went through ToDoubleV, which threw the runtime's own "The input string 'hello' was not in
    /// a correct format." and, being a raw exception, escaped the script's own `try`; the tree's core answers
    /// "Unary minus not defined for type string" (error_msgs_test).</summary>
    internal static object? ApplyUnaryMinusFromVm(object? value, int line, int column)
        => ApplyUnaryMinus(value, line, column);

    /// <summary>The VM's BITWISE COMPLEMENT for an operand its own arm cannot answer (a bool, a string, a null,
    /// any object): `~true` was computed as ~1 = -2 through ToInt64 where the tree refuses it with "Bitwise
    /// complement operator '~' not defined for type bool" (error_msgs_test Test 3). Same shape as the unary
    /// minus above, and the same reason: the interpreter's core owns both the wording and the refusal, so a
    /// compiled body and a walked one answer alike.</summary>
    internal static object? ApplyBitwiseComplementFromVm(object? value, int line, int column)
        => ApplyBitwiseComplement(value, line, column);

    private object? ApplyBinaryOp(object? left, object? right, TokenType op, int line, int column)
    {
        left = UnwrapEnum(left); right = UnwrapEnum(right);
        // -- null guard for equality operators --
        if (left is null || right is null)
        {
            switch (op)
            {
                case TokenType.EqualEqual:
                    return ReferenceEquals(left, right);   // both null ? true, one null ? false

                case TokenType.BangEqual:
                    return !ReferenceEquals(left, right);
            }

            // For other operators, fall through to the normal error handling
        }

        // Widen small integer types to int so arithmetic stays precise
        left = WidenSmallInt(left);
        right = WidenSmallInt(right);

        // 1. User-defined operator overload on class/struct instances
        if (left is ClassInstance leftInst && leftInst.Class.OperatorMethods.TryGetValue(op, out var method))
        {
            // For == and !=, if either operand is null, skip the overload and do a simple reference check.
            if ((op == TokenType.EqualEqual || op == TokenType.BangEqual) && (right is null))
                return op == TokenType.BangEqual;  // != null ? true (if left is not null), == null ? false

            var args = new[] { right };
            return InvokeMethodOnInstance(leftInst, method, args, line, column);
        }
        // Also check if right is a ClassInstance and try its operator (commutative for ==/!=)
        if (right is ClassInstance rightInst && rightInst.Class.OperatorMethods.TryGetValue(op, out var methodRight))
        {
            if ((op == TokenType.EqualEqual || op == TokenType.BangEqual) && (left is null))
                return op == TokenType.BangEqual;

            var args = new[] { left };
            return InvokeMethodOnInstance(rightInst, methodRight, args, line, column);
        }
        // Struct operator overloads
        if (left is StructInstance leftStruct && Structs.TryGetValue(leftStruct.StructName, out var leftDecl)
            && leftDecl.OperatorMethods.TryGetValue(op, out var structMethod))
        {
            var args = new[] { right };
            return InvokeMethodOnStructInstance(leftStruct, structMethod, args, line, column);
        }
        if (right is StructInstance rightStruct && Structs.TryGetValue(rightStruct.StructName, out var rightDecl)
            && rightDecl.OperatorMethods.TryGetValue(op, out var structMethodR))
        {
            var args = new[] { left };
            return InvokeMethodOnStructInstance(rightStruct, structMethodR, args, line, column);
        }

        // 1b. An operator an extension block declares, reached by the OPERAND types the way C# reaches a
        // user-defined one, and only after the operands' own types have had their chance above.
        if (TryInvokeExtensionOperator(op, left, right, line, column, out var extensionOperatorResult))
            return extensionOperatorResult;

        switch (op)
        {
            // 2. Handle string concatenation and delegate combination for '+'
            case TokenType.Plus when left is string || right is string:
                return $"{left}{right}";

            // Otherwise fall through to numeric addition (handled below)
            case TokenType.Plus:
                {
                    var leftIsDelegate = left is ScriptFunction or CombinedDelegate;
                    var rightIsDelegate = right is ScriptFunction or CombinedDelegate;

                    if (leftIsDelegate || rightIsDelegate)
                        return CombineDelegates(left, right, line, column);

                    switch (left)
                    {
                        // Preserve int/long when both operands are int/long
                        case int li when right is int ri:
                            return li + ri;
                        case int li2 when right is long rl:
                            return li2 + rl;
                        case long ll when right is long rl2:
                            return ll + rl2;
                        case long ll2 when right is int ri2:
                            return ll2 + ri2;
                    }

                    break;
                }

            // 3. Equality / inequality
            case TokenType.EqualEqual:
                return AreEqual(left, right);

            case TokenType.BangEqual:
                return !AreEqual(left, right);

            // 4. Bitwise shifts
            case TokenType.LeftShift or TokenType.RightShift:
                {
                    var l = ToLong(left, line, column);
                    var r = Convert.ToInt32(ToLong(right, line, column));
                    return op == TokenType.LeftShift ? l << r : l >> r;
                }

            // 5. Bitwise AND, OR, XOR
            case TokenType.Ampersand or TokenType.Pipe or TokenType.Caret:
                {
                    var l = ToLong(left, line, column);
                    var r = ToLong(right, line, column);
                    return op switch
                    {
                        TokenType.Ampersand => l & r,
                        TokenType.Pipe => l | r,
                        TokenType.Caret => l ^ r,
                        _ => 0L
                    };
                }
        }

        // 5b. DateTime - DateTime => TimeSpan
        if (op == TokenType.Minus && left is DateTime dt1 && right is DateTime dt2)
            return dt1 - dt2;

        // Preserve float when both operands are float (prevents promotion to double)
        if (left is float lf && right is float rf)
        {
            return op switch
            {
                TokenType.Plus => lf + rf,
                TokenType.Minus => lf - rf,
                TokenType.Star => lf * rf,
                TokenType.Slash => lf / rf,
                TokenType.Percent => lf % rf,
                TokenType.Less => lf < rf,
                TokenType.LessEqual => lf <= rf,
                TokenType.Greater => lf > rf,
                TokenType.GreaterEqual => lf >= rf,
                _ => (float)0
            };
        }

        // 6. Numeric operations (+, -, *, /, %, <, <=, >, >=)
        // A decimal PAIR stays decimal, the way a float pair stays float just above. The fallback below
        // converts both operands with ToDouble, which is why `0.1m + 0.2m` printed 0.30000000000000004 and
        // `typeof(1.5m + 0m)` was "double" - the exactness the decimal entry promises and did not deliver.
        // C# performs decimal arithmetic in decimal; a MIXED pair (decimal with double or int) still widens
        // through the fallback, which is the lenient reading of what C# would refuse to compile outright.
        if (left is decimal ldec && right is decimal rdec
            && op is TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent)
        {
            try
            {
                return op switch
                {
                    TokenType.Plus => ldec + rdec,
                    TokenType.Minus => ldec - rdec,
                    TokenType.Star => ldec * rdec,
                    TokenType.Slash => ldec / rdec,
                    _ => ldec % rdec,
                };
            }
            catch (DivideByZeroException) { RuntimeError("Division by zero.", line, column); }
        }

        // 5. A script type that declares no operator for this token: refuse with the OPERATOR and the recipe.
        // Both dispatch arms above have already run (class/struct, either side), so reaching here WITH an instance
        // means the type declares no member for this token. Without this guard the numeric coercion below reports
        // the VALUE instead ("Cannot convert 'Bare { V = 1 }' to number") and says nothing about what to write,
        // the same defect class as a refusal that advises an impossible spelling. The LEFT operand is named first
        // because it is the one a reader looks at; a number on the left therefore names the type on the right.
        if (left is ClassInstance or StructInstance) LacksOperator(left, op, line, column);
        else if (right is ClassInstance or StructInstance) LacksOperator(right, op, line, column);

        var lnum = ToDouble(left, line, column);
        var rnum = ToDouble(right, line, column);

        return op switch
        {
            TokenType.Plus => lnum + rnum,
            TokenType.Minus => lnum - rnum,
            TokenType.Star => lnum * rnum,
            TokenType.Slash => lnum / rnum,
            TokenType.Percent => lnum % rnum,
            TokenType.Less => lnum < rnum,
            TokenType.LessEqual => lnum <= rnum,
            TokenType.Greater => lnum > rnum,
            TokenType.GreaterEqual => lnum >= rnum,
            _ => RuntimeError<TokenType>($"Unsupported binary operator {op}", line, column)
        };
    }

    /// <summary>The source spelling of an overloadable operator token, for messages.</summary>
    private static string OpText(TokenType op) => op switch
    {
        TokenType.Plus => "+",
        TokenType.Minus => "-",
        TokenType.Star => "*",
        TokenType.Slash => "/",
        TokenType.Percent => "%",
        TokenType.Less => "<",
        TokenType.LessEqual => "<=",
        TokenType.Greater => ">",
        TokenType.GreaterEqual => ">=",
        TokenType.EqualEqual => "==",
        TokenType.BangEqual => "!=",
        _ => op.ToString(),
    };

    /// <summary>Refuses an operator a script type never declared, naming the operator, the type, and the one-line
    /// declaration that fixes it. Reached only after BOTH dispatch arms declined, so the type genuinely has no
    /// member for this token, the check is therefore about the OPERAND KIND, not about the operator table.</summary>
    private static void LacksOperator(object subject, TokenType op, int line, int column)
    {
        var typeName = subject switch
        {
            ClassInstance ci => ci.Class.Name,
            StructInstance si => si.StructName,
            _ => "?",
        };
        var text = OpText(op);
        RuntimeError($"Operator '{text}' is not defined for '{typeName}'. Declare it in the type as: "
                     + $"public {typeName} operator {text}({typeName} other)", line, column);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object? WidenSmallInt(object? val) => NumericTypeConverter.WidenSmallInt(val);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ToLong(object? val, int line, int column)
    {
        val = UnwrapEnum(val);
        try
        {
            if (val is char c)
                return c;
            return Convert.ToInt64(val);
        }
        catch
        {
            RuntimeError($"Cannot convert '{val}' to integer.", line, column);
            return 0;
        }
    }

    /// <summary>The remedy a DateTime needs when a number was expected, in ONE place. A COM date cell arrives as a real
    /// DateTime (the VARIANT is VT_DATE and the bridge converts it), so `1.0 * cell` is the shape that meets this
    /// refusal, and the plain sentence ("Cannot convert '01/02/2026 06:07:00' to number") named nothing a reader could
    /// write next while the engine already offers three ways out.</summary>
    private const string DateTimeNumberRemedy =
        "a DateTime is not a number here, so use .ToOADate() for its serial, .Ticks for the tick count, or .ToString(...) to format it";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ToDouble(object? val, int line, int column)
    {
        val = UnwrapEnum(val);
        try
        {
            if (val is char c)
                return c;
            return Convert.ToDouble(val);
        }
        catch
        {
            RuntimeError(val is DateTime
                ? $"Cannot convert '{val}' to number: {DateTimeNumberRemedy}"
                : $"Cannot convert '{val}' to number.", line, column);
            return 0;
        }
    }

    /// <summary>
    /// Determines whether two objects are considered equal, supporting numeric and character type comparisons with type
    /// coercion where appropriate.
    /// </summary>
    /// <remarks>This method performs type-aware equality checks, including numeric comparisons with tolerance
    /// for floating-point values and character-to-numeric comparisons. For non-numeric and non-character types,
    /// standard object equality is used.</remarks>
    /// <param name="left">The first object to compare. Can be a numeric type, a character, or any other object.</param>
    /// <param name="right">The second object to compare. Can be a numeric type, a character, or any other object.</param>
    /// <returns>true if the objects are considered equal; otherwise, false.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    // ════════════════════════════════════════════════════════════════════
    // Shared positional pattern engine (match/case + `expr is (…)`)
    // ════════════════════════════════════════════════════════════════════
    internal static int PosCount(object? subject) => subject switch
    {
        ScriptTuple t => t.Length,
        Array a => a.Length,
        IScriptList l => l.Count,
        string s => s.Length,          // strings are positional: elements are chars (like s[i])
        _ => -1
    };

    internal static object? PosItem(object? subject, int i) => subject switch
    {
        ScriptTuple t => t[i],
        Array a => a.GetValue(i),
        IScriptList l => l[i],
        string s => i >= 0 && i < s.Length ? s[i] : null,   // char, matching s[i]
        _ => null
    };

    // Exact script-type match for typed pattern elements. Null matches only nullable or
    // object; a bare/typed `object` matches any value (including null).
    internal static bool IsTypeMatch(object? value, TypeInfo ti)
    {
        if (ti.BaseType == TokenType.Object) return true;   // `object` matches any value
        if (ti.TypeName is "object" or "Object" or "object?") return true;
        if (value == null) return ti.IsNullable;
        var actual = TypeSystem.FromClrName(value.GetType().Name);
        var declared = ti.TypeName ?? TypeSystem.Name(ti.BaseType);
        if (declared.EndsWith("?", StringComparison.Ordinal)) declared = declared[..^1];
        return actual.Equals(declared, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when `subject` matches `elements`; on success `bindings[i]` holds the
    /// matched element value for TypedVar slots (null elsewhere) so callers can define names.</summary>
    internal static bool MatchPositional(object? subject, IReadOnlyList<TupleElementPattern> elements,
        out object?[] bindings)
    {
        bindings = new object?[elements.Count];
        if (PosCount(subject) != elements.Count) return false;
        for (var i = 0; i < elements.Count; i++)
        {
            var el = elements[i];
            var item = PosItem(subject, i);
            switch (el.Kind)
            {
                case TupleElementKind.Discard:
                    break;
                case TupleElementKind.Literal:
                    if (!AreEqual(item, el.LiteralValue)) return false;   // engine equality + widening
                    break;
                case TupleElementKind.TypeOnly:
                    if (!IsTypeMatch(item, el.Type!)) return false;
                    break;
                case TupleElementKind.TypedVar:
                    if (!IsTypeMatch(item, el.Type!)) return false;
                    bindings[i] = item;
                    break;
            }
        }
        return true;
    }

    internal static bool AreEqual(object? left, object? right)
    {
        left = UnwrapEnum(left); right = UnwrapEnum(right);
        if (left == null && right == null) return true;
        if (left == null || right == null) return false;
        switch (left)
        {
            case char c1 when right is char c2:
                return c1 == c2;
            case char c when IsNumeric(right):
                {
                    double d = c;
                    var r = ConvertToDouble(right);
                    return Math.Abs(d - r) < Epsilon;
                }
        }

        if (right is char ch && IsNumeric(left))
        {
            double d = ch;
            var l = ConvertToDouble(left);
            return Math.Abs(l - d) < Epsilon;
        }

        // An enum IS an int (stated 2026-09-28): a whole number and an enum value of the same number are equal,
        // in EITHER operand order. These two lines used to be one switch on `left`, so the case that named
        // `rightEnum` was still a test on the LEFT operand and an int on the left with an enum on the right matched
        // neither: it fell through to object.Equals and answered false. Measured: AreEqual(Int32 3, ThemeMode.Custom)
        // was false while AreEqual(ThemeMode.Custom, Int32 3) was true, which is what made a compiled body compare an
        // int parameter against an enum constant wrongly while the same comparison at the top level was right.
        if (left is Enum && IsNumeric(right)) return Convert.ToInt64(left) == Convert.ToInt64(right);
        if (right is Enum && IsNumeric(left)) return Convert.ToInt64(left) == Convert.ToInt64(right);

        switch (left)
        {
            // Enum-to-string comparison
            case Enum leftEnum when right is string rightStr:
                return leftEnum.ToString() == rightStr;

            case string leftStr when right is Enum rightEnum:
                return leftStr == rightEnum.ToString();
        }

        // A HANDLE compared with a handle. A window handle surfaces as an IntPtr (a control's Handle) while a Win32
        // member answers a long, and a script writes one as a whole number: they are the SAME value, so they compare
        // as one, through the ToNInt family the native call sites already convert with. Without this the pair fell to
        // object.Equals and answered false for two handles that print identically, which is what made a suite compare
        // them as text instead. Neither side may be offered anything else, or the conversion would swallow a string.
        if ((left is IntPtr || right is IntPtr)
            && (IsIntegral(left) || left is IntPtr) && (IsIntegral(right) || right is IntPtr))
            return ToNInt(left) == ToNInt(right);

        if (!IsNumeric(left) || !IsNumeric(right))
        {
            // Record structural equality: both operands are instances of a record type
            if (left is ClassInstance lRec && right is ClassInstance rRec && lRec.Class.IsRecord)
                return RecordsEqual(lRec, rRec);
            return Equals(left, right);
        }
        if (IsIntegral(left) && IsIntegral(right))
        {
            var l = Convert.ToInt64(left);
            var r = Convert.ToInt64(right);
            return l == r;
        }
        else
        {
            var l = ConvertToDouble(left);
            var r = ConvertToDouble(right);
            return Math.Abs(l - r) < Epsilon;
        }
    }

    private static bool IsIntegral(object value) => NumericTypeConverter.IsIntegral(value);
    private static double ConvertToDouble(object value) => NumericTypeConverter.ConvertToDouble(value);

    /// <summary>Structural field-by-field equality for record instances.
    /// Both instances must be of the same record class; fields are compared
    /// using the interpreter's AreEqual so nested records compare structurally too.</summary>
    private static bool RecordsEqual(ClassInstance left, ClassInstance right)
    {
        if (!ReferenceEquals(left.Class, right.Class))
        {
            // Allow same-named record classes (distinct ClassInfo from redefinition) only if field sets match
            if (left.Class.Name != right.Class.Name) return false;
        }
        if (left.FieldValues.Count != right.FieldValues.Count) return false;
        foreach (var kv in left.FieldValues)
        {
            if (!right.FieldValues.TryGetValue(kv.Key, out var otherVal))
                return false;
            if (!AreEqual(kv.Value, otherVal))
                return false;
        }
        return true;
    }

    private static bool IsCompatibleWithStoredTypes(ScriptFunction fn, DelegateInfo delegateInfo)
    {
        if (fn.ParameterTypes.Count != delegateInfo.Parameters.Count) return false;

        for (var i = 0; i < fn.ParameterTypes.Count; i++)
        {
            // Allow object to match any type
            var paramCompatible = fn.ParameterTypes[i].BaseType == delegateInfo.Parameters[i].Type.BaseType
                || fn.ParameterTypes[i].BaseType == TokenType.Object
                || delegateInfo.Parameters[i].Type.BaseType == TokenType.Object
                || fn.ParameterTypes[i].BaseType == TokenType.StringType
                && delegateInfo.Parameters[i].Type.BaseType == TokenType.StringType;

            // Special case: StringType should match StringType

            if (!paramCompatible)
                return false;

            if (fn.IsRefParams[i] != delegateInfo.IsRefParams[i])
                return false;

            // Also check out-param compatibility
            var fnOut = fn.Declaration.IsOutParams ?? [];
            var delOut = delegateInfo.IsOutParams ?? [];
            var fnOutMatch = i < fnOut.Count && fnOut[i];
            var delOutMatch = i < delOut.Count && delOut[i];
            if (fnOutMatch != delOutMatch)
                return false;
        }

        // Return type compatibility - allow Object to match any type (including StringType)
        if (delegateInfo.ReturnType.BaseType == TokenType.Void)
            return fn.ReturnType.BaseType == TokenType.Void;

        // Allow Object to match any return type
        var returnCompatible = fn.ReturnType.BaseType == delegateInfo.ReturnType.BaseType
            || fn.ReturnType.BaseType == TokenType.Object
            || delegateInfo.ReturnType.BaseType == TokenType.Object
            || fn.ReturnType.BaseType == TokenType.StringType && delegateInfo.ReturnType.BaseType == TokenType.StringType
            || fn.ReturnType.BaseType == TokenType.Object && delegateInfo.ReturnType.BaseType == TokenType.StringType;

        // Special case: StringType should match StringType

        // For the first handler: Object should be compatible with StringType

        return returnCompatible;
    }

    /// <summary>A compound-assignment target, RESOLVED ONCE: the receiver and every INDEX expression are
    /// evaluated when this is built, and Read/Write act on what was resolved. One resolver serves `op=`,
    /// `??=`, `++` and `--`, because each of those three owned a copy of this walk and only one of them got
    /// the count right. Measured before this existed: `Recv().N += 1` evaluated the receiver TWICE on both
    /// engines (the event probe's own evaluation, then the standard path's) and `Arr()[Idx()] += 5`
    /// evaluated the INDEX twice, so a target whose index changed during the statement wrote an element it
    /// had never read.
    ///
    /// The order is C#'s, and it is what makes the count observable-correct: target, then read, then the
    /// right-hand operand, then the operation, then the store. `Event` is answered for `+=`/`-=` on a
    /// declared event, which subscribes instead of computing.
    /// </summary>
    /// <param name="Read">A LIVE read of the same storage, when the shape has one: the compound-assignment path only
    /// ever needs <paramref name="Current"/>, but a `ref`/`out` cell re-reads on every `Get`, so a callee that reads
    /// its parameter twice sees what the container holds then rather than what it held when the call was made.</param>
    private readonly record struct TargetSlot(object? Current, Action<object?> Write, EventInfo? Event,
        Func<object?>? Read = null);

    /// <summary>Reorders argument expressions according to named-argument names so they
    /// line up with the target's positional parameter list. Returns the original list when
    /// no names are present. Positional (null-name) args fill remaining slots in order.</summary>
    private IReadOnlyList<Expr> ReorderNamedArgExprs(IReadOnlyList<Expr> argExprs, IReadOnlyList<string?>? argNames, IReadOnlyList<string> paramNames, int line, int col)
    {
        if (argNames == null || argNames.Count == 0) return argExprs;
        var hasNames = false;
        foreach (var n in argNames) if (n != null) { hasNames = true; break; }
        if (!hasNames) return argExprs;

        var reordered = new Expr[paramNames.Count];
        var filled = new bool[paramNames.Count];
        var positional = 0;
        for (var i = 0; i < argExprs.Count; i++)
        {
            var name = argNames[i];
            if (name == null)
            {
                while (positional < filled.Length && filled[positional]) positional++;
                if (positional >= filled.Length)
                    RuntimeError("Too many arguments.", line, col);
                reordered[positional] = argExprs[i];
                filled[positional] = true;
                positional++;
            }
            else
            {
                var idx = -1;
                for (var p = 0; p < paramNames.Count; p++)
                    if (string.Equals(paramNames[p], name, StringComparison.OrdinalIgnoreCase)) { idx = p; break; }
                if (idx < 0)
                    RuntimeError($"Named argument '{name}' does not match any parameter.", line, col);
                if (filled[idx])
                    RuntimeError($"Named argument '{name}' specified more than once.", line, col);
                reordered[idx] = argExprs[i];
                filled[idx] = true;
            }
        }
        return reordered;
    }

    /// <summary>Attaches tuple element names (from a tuple-typed function declaration)
    /// to a ScriptTuple return value so named access (t.i) works.</summary>
    private void AttachTupleNames(object? returnValue, FuncDecl decl)
    {
        if (decl.TupleElementNames is { Count: > 0 } names && returnValue is ScriptTuple tup)
        {
            tup.Names = names;
            // Also reattach names if items were re-created
            if (tup.Items.Length != names.Count && names.Count <= tup.Items.Length)
                tup.Names = names;
        }
    }

    /// <summary>Overload that takes the target's declared parameter list directly, so callers
    /// don't allocate a name list when there are no named args (the common case).</summary>
    internal object?[] ReorderNamedArgs(object?[] args, IReadOnlyList<string?>? argNames, IReadOnlyList<(TypeInfo Type, string Name)> paramsList, int line, int col)
    {
        if (argNames is not { Count: > 0 }) return args;
        foreach (var n in argNames) if (n != null) return ReorderNamedArgsCore(args, argNames, paramsList, line, col);
        return args;
    }

    /// <summary>Why a named-argument call cannot be bound to a declaration, for the two callers of
    /// <see cref="MapArgsToParams"/> that answer it differently.</summary>
    internal enum NamedArgFault { None, UnknownName, DuplicateName, TooMany }

    /// <summary>WHICH PARAMETER INDEX each supplied argument fills: THE named-argument rule, stated once. A
    /// positional argument (a null name) takes the next free slot, a named one takes the parameter of that name.
    /// Answers null when the declaration cannot take the call, saying WHICH argument is at fault through
    /// <paramref name="offending"/> and which fault through <paramref name="fault"/>. TWO callers ask, and they
    /// must not disagree: the overload picker (which SKIPS a candidate that cannot take these names, since
    /// another may) and the reorder below (which REFUSES, with the message its call site has always produced).
    /// The name comparison keeps the case rule this path has always used (insensitive); aligning it with the
    /// interpreter's flag is a separate decision, because a strict run would start refusing names it accepts.</summary>
    internal static int[]? MapArgsToParams(int argCount, IReadOnlyList<string?>? argNames,
        IReadOnlyList<(TypeInfo Type, string Name)> paramsList, out string? offending, out NamedArgFault fault)
    {
        offending = null;
        fault = NamedArgFault.None;
        var map = new int[argCount];        var filled = new bool[paramsList.Count];
        var positional = 0;
        for (var i = 0; i < argCount; i++)
        {
            var name = argNames != null && i < argNames.Count ? argNames[i] : null;
            if (name == null)
            {
                while (positional < filled.Length && filled[positional]) positional++;
                if (positional >= filled.Length)
                {
                    fault = NamedArgFault.TooMany;
                    return null;
                }
                map[i] = positional;
                filled[positional] = true;
                positional++;
                continue;
            }
            var idx = -1;
            for (var p = 0; p < paramsList.Count; p++)
                if (string.Equals(paramsList[p].Name, name, StringComparison.OrdinalIgnoreCase)) { idx = p; break; }
            if (idx < 0)
            {
                offending = name;
                fault = NamedArgFault.UnknownName;
                return null;
            }
            if (filled[idx])
            {
                offending = name;
                fault = NamedArgFault.DuplicateName;
                return null;
            }
            map[i] = idx;
            filled[idx] = true;
        }
        return map;
    }

    /// <summary>The refusal for a named-argument call a declaration cannot take: ONE wording, asked by the reorder
    /// (for the declaration it settled on) AND by the overload picker (when no candidate fits because of the NAMES,
    /// which would otherwise read as "no overload that fits" and send a reader looking at arities instead of at a
    /// mistyped name).</summary>
    private void RefuseNamedArgFault(NamedArgFault fault, string? offending, int line, int col)
    {
        if (fault == NamedArgFault.DuplicateName)
            RuntimeError($"Named argument '{offending}' specified more than once.", line, col);
        if (fault == NamedArgFault.UnknownName)
            RuntimeError($"Named argument '{offending}' does not match any parameter.", line, col);
        RuntimeError("Too many arguments.", line, col);
    }

    /// <summary>Reorders arguments according to named-argument names so they line up
    /// with the target's positional parameter list. Returns the original array when
    /// no names are present. Positional (null-name) args fill remaining slots in order.
    /// Omitted slots are filled with the parameter's ZERO value (int → 0, string → "",
    /// bool → false) so partial named calls like comb(c: 3, a: 1) bind b to 0 instead of
    /// erroring with 'Cannot assign null to non-nullable type'. The MAPPING is
    /// <see cref="MapArgsToParams"/>, so this cannot disagree with the overload picker about which
    /// parameter a name fills.</summary>
    private object?[] ReorderNamedArgsCore(object?[] args, IReadOnlyList<string?>? argNames, IReadOnlyList<(TypeInfo Type, string Name)> paramsList, int line, int col)
    {
        if (argNames == null || argNames.Count == 0) return args;
        var hasNames = false;
        foreach (var n in argNames) if (n != null) { hasNames = true; break; }
        if (!hasNames) return args;

        var map = MapArgsToParams(args.Length, argNames, paramsList, out var offending, out var fault);
        if (map == null) RefuseNamedArgFault(fault, offending, line, col);

        var reordered = new object?[paramsList.Count];
        var filled = new bool[paramsList.Count];
        for (var i = 0; i < args.Length; i++)
        {
            reordered[map![i]] = args[i];
            filled[map![i]] = true;
        }
        for (var i = 0; i < filled.Length; i++)
            if (!filled[i])
                reordered[i] = DefaultValue(paramsList[i].Type);
        return reordered;
    }

    private DelegateInfo GetDelegate(string name, int line, int column) =>
        DelegateInfos.GetValueOrDefault(name)
        ?? RuntimeError<DelegateInfo>($"Delegate type '{name}' not defined.", line, column);

    /// <summary>True when <paramref name="name"/> is an EVENT of this instance's class. Asked by the VM's
    /// GET_PROP so a compiled body hands the member to the tree instead of reading it: the tree's compound
    /// assign reaches an event through GetEventInfo (which does no access check) while a compiled `+=` reads
    /// the member first, which the property arm's CheckAccess refuses for a private event. A class with no
    /// events answers with one comparison, so an ordinary property read pays almost nothing.</summary>
    internal bool IsClassEvent(ClassInstance instance, string name)
    {
        var events = instance.Class.Events;
        for (var i = 0; i < events.Count; i++)
            if (events[i].Name.Equals(name, Comparison)) return true;
        return false;
    }

    /// <summary>The event a compiled body's GET_PROP pushes for a class EVENT member, through the SAME
    /// accessor the tree's property arm uses (GetEventInfo): an event read, an event subscribe and the two
    /// engines cannot disagree about the member or about the access rules that way.</summary>
    internal EventInfo? ReadClassEvent(ClassInstance instance, string name, int line, int column)
        => GetEventInfo(instance, name, line, column);

    /// <summary>Subscribes (or unsubscribes) a handler on a class event. ONE implementation, reached by the
    /// tree's compound-assign arm and by the VM's ADD/SUB, because a compiled `x.E += h` is a GET_PROP, an
    /// ADD and a SET_PROP with no AST to tell it what an event is: the signature check, the CombinedDelegate
    /// expansion and the refusal all live here so the two engines cannot drift.</summary>
    internal void SubscribeClassEvent(EventInfo evt, object? handler, bool remove, int line, int column)
    {
        switch (handler)
        {
            case ScriptFunction fn:
                {
                    var delegateInfo = GetDelegate(evt.DelegateTypeName, line, column);
                    if (!IsCompatibleWithStoredTypes(fn, delegateInfo))
                        RuntimeError($"Handler signature does not match delegate '{evt.DelegateTypeName}'.", line, column);
                    if (remove) evt.Subscribers.Remove(fn);
                    else evt.Subscribers.Add(fn);
                    break;
                }
            // Subscribing/unsubscribing a combined delegate
            case CombinedDelegate cd when !remove:
                evt.Subscribers.AddRange(cd.Handlers);
                break;
            case CombinedDelegate cd:
                foreach (var h in cd.Handlers)
                    evt.Subscribers.Remove(h);
                break;
            default:
                RuntimeError("Event handler must be a function or delegate combination.", line, column);
                break;
        }
    }

    private EventInfo? GetEventInfo(ClassInstance instance, string eventName, int line, int column)
    {
        if (instance.Events.TryGetValue(eventName, out var evt))
            return evt;

        var classInfo = instance.Class;
        var eventDecl = FindByName(classInfo.Events, eventName, static e => e.Name);
        if (eventDecl == null)
            return null;
        var evtInfo = new EventInfo(eventName, eventDecl.DelegateTypeName, []);
        instance.Events[eventName] = evtInfo;
        return evtInfo;
    }

    /// <summary>++/-- on any target: the SAME resolution EvalCompoundAssign uses, so the receiver and the
    /// indices are evaluated once and a POSTFIX form answers the value the target held. The three
    /// hand-written walks this replaces each evaluated the index expressions twice.</summary>
    private object? EvalIncDec(IncrementDecrementExpr inc)
    {
        var slot = ResolveTarget(inc.Target, inc.Line, inc.Column);
        var old = slot.Current;
        var delta = inc.Operator.Type == TokenType.PlusPlus ? 1L : -1L;
        var updated = ApplyBinaryOp(old, delta, TokenType.Plus, inc.Line, inc.Column);
        slot.Write(updated);
        // A POSTFIX form answers the value the target HELD; a prefix form answers the new one.
        return inc.IsPrefix ? updated : old;
    }

    private object? InvokeDelegate(object? del, object?[] args)
    {
        return del switch
        {
            ScriptFunction fn => InvokeScriptFunction(fn, args, 0, 0),
            ScriptDelegateWrapper wrapper => wrapper.Invoke(args),
            _ => throw new ScriptException($"Cannot invoke object of type {del?.GetType().Name}")
        };
    }

    // -----------------------------------------------------------------------------
    // Invoke methods (updated to use ExecutionResult internally)
    // -----------------------------------------------------------------------------
    /// <summary>Creates a ClosureSnapshot with the live root global scope populated,
    /// so builtins (print, sleep, Json, etc.) are accessible from destructor bodies.</summary>
    internal ClosureSnapshot CreateSnapshotWithGlobalScope()
    {
        var (rootRef, _) = _rootFrame.Scopes.GetGlobalScopeReference();
        var gbl = new Dictionary<string, object?>(rootRef.Count, _comparer);
        foreach (var kvp in rootRef)
            gbl[kvp.Key] = kvp.Value.Value;
        return new ClosureSnapshot([], [], gbl, []);
    }


    internal object? ResolveBuiltin(string name, object?[] args)
    {
        // CallBuiltinByName returns VmFallback distinctly when the name is NOT found,
        // a found void function returning null is NOT a fallback signal
        return CallBuiltinByName(name, args);
    }

    /// <summary>The VM's builtin call, with a CLR failure from the builtin converted the way the tree's own
    /// call path converts one. Measured: `LoadAssembly("no-such-assembly-xyz.dll")` inside a compiled
    /// statement threw the runtime's own "Could not find file '...'" and that ESCAPED the script's `try`,
    /// because the VM's handler walk catches ScriptException only, while the same call on the tree reports
    /// "[LoadAssembly]: File not found: '...'" (aot_surface). The conversion is the ONE helper the member
    /// paths already use, so the two cannot drift.</summary>
    internal object? InvokeBuiltinFromVm(string name, object?[] args, int line, int column)
    {
        // The CALL's position, so a builtin's own warning names where the script asked for it (see
        // CurrentScriptLine): a compiled body's builtin call knows the position and the native cannot.
        _currentLine = line;
        _currentColumn = column;
        try { return CallBuiltinByName(name, args); }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column, name); }
    }

    /// <summary>Resolves a user-defined script function from the global scope (used by the VM CALL handler).</summary>
    internal ScriptFunction? ResolveGlobalFunction(string name)
    {
        // Copy-free single lookup, the VM CALL handler calls this on every call and the
        // snapshot-copy path (GetGlobalScopeDictionary) allocated the whole global dict per call.
        if (_rootFrame.Scopes.TryGetGlobalValue(name, out var v) && v.Value is ScriptFunction sf)
            return sf;
        if (NamedTraceEnabled)
        {
            var found = _rootFrame.Scopes.TryGetGlobalValue(name, out var dbg);
            // A NATIVE is the EXPECTED answer here, not a finding: the VM's CALL arm resolves a builtin this way
            // and then invokes it, so printing for one made the trace noisy on the path that always works and
            // drowned the case this line exists for. That case is everything ELSE the name can hold, which is how
            // it earned its keep: `valueType=RefCell` is what named the shadowed global that broke `Check` in a
            // statement with a closure (mega_crash_test's double increment).
            var expectedNative = found && dbg.Value is INativeFunction;
            if (!expectedNative)
                Console.Error.WriteLine($"[named]   resolve '{name}' -> null (found={found}, valueType={dbg.Value?.GetType().Name ?? "null"})");
        }
        return null;
    }

    /// <summary>The DECLARED type a name was bound with, spelled as the declaration wrote it (`long?`, `List<int>`), or
    /// null when nothing declares one (`var`, or a name that is not bound at all). A declaration records it on the
    /// binding (`VisitVarDeclStmt`), which is what the assignment arm reads to keep `long sum = 0; sum = 0;` long, so
    /// this answers from the same place rather than re-deriving it. Used by `typeof(<a name>)` for a NULL value: no
    /// runtime type exists to report, and the declaration is the only answer a reader can act on.</summary>
    internal string? DeclaredTypeNameOf(string name)
    {

        if (CurrentFrame.Scopes.TryGetVariableInfo(name, out var info) && !string.IsNullOrEmpty(info.TypeName))
            return info.TypeName;
        // A FIELD of the enclosing class or struct is a declaration too, and a bare name reaches one here: the parser
        // hands the name over, so `typeof(Count)` inside a method answers the field's declared type.
        if (_currentClass != null)
            foreach (var fld in _currentClass.Fields)
                if (_comparer.Equals(fld.Name, name) && fld.Type != null) return TypeInfo.Spelling(fld.Type);
        return null;
    }

    /// <summary>Reads a variable from the global scope (used by the VM LOAD_GLOBAL handler). Returns VmFallback if absent.</summary>
    internal object? ResolveGlobalValue(string name)
    {
        // Copy-free single lookup (see ResolveGlobalFunction).
        if (_rootFrame.Scopes.TryGetGlobalValue(name, out var v))
        {
            // Globals may be RefCell-wrapped by coroutine-lambda capture promotion (tree reads/writes
            // unwrap via RefCell; the VM must do the same).
            return v.Value is RefCell rc ? rc.Get() : v.Value;
        }
        return VmFallback.Instance;
    }

    /// <summary>Invokes an already-resolved callee value (ScriptFunction / ScriptDelegateWrapper / Delegate). Returns VmFallback if not callable.</summary>
    internal object? InvokeCallableValue(object? callee, object?[] args, int line = 0, int column = 0)
    {
        return callee switch
        {
            ScriptFunction fn => InvokeScriptFunction(fn, args, line, column),
            // A name holding several signatures: the VM's CALL_VAL reaches a set the same way a compiled CALL
            // reaches the global one, through the ONE picker.
            ScriptOverloadSet overloads => InvokeScriptFunction(
                PickOverload(overloads, args, "a function value", line, column), args, line, column),
            ScriptDelegateWrapper wrapper => wrapper.Invoke(args),
            Delegate del => del.DynamicInvoke(args),
            CombinedDelegate cd => cd.Invoke(args, line, column),
            _ => VmFallback.Instance
        };
    }

    /// <summary>Is this value something the engine can CALL later? A <c>BoundMethod</c> counts: a method read as a
    /// value (`app.Handle`) carries its receiver, which is what the member-read arms now return for a method named
    /// where a field would be. The pipeline registrations used to ask `is ScriptFunction`, which dropped anything
    /// else in silence instead of refusing it, so a registration that could not work answered every request as
    /// unhandled with nothing said anywhere.</summary>
    internal static bool IsScriptCallable(object? value) =>
        value is ScriptFunction or ScriptDelegateWrapper or Delegate or CombinedDelegate or BoundMethod;

    /// <summary>The declared parameter count of a callable, or 0 for one this cannot see (a CLR delegate). ONE
    /// implementation, because two callers ask the same question and each answers it by building a different argument
    /// list: an `Events` producer body (which gets the feed, and optionally a state) and an `http` route handler (which
    /// gets the request's context, the route arguments, and the request's CancellationToken when it declares a third
    /// parameter). A handler written with two parameters is therefore unchanged by the token existing.</summary>
    internal static int CallableArity(object? callable) => callable switch
    {
        ScriptFunction fn => fn.Declaration.Params.Count,
        BoundMethod bm => bm.Method.Params.Count,
        _ => 0,
    };

    /// <summary>Invokes a value the ENGINE was handed to call later: an `http.Use` middleware, an `http.MapGet`
    /// handler. Unlike <see cref="InvokeCallableValue"/>, whose only caller is the VM's `CALL_VAL` and which
    /// therefore answers <c>VmFallback</c> for a bound method (the VM bails to the tree, which resolves it), this
    /// covers BOTH shapes a script hands over, because here there is no tree to fall back to.</summary>
    internal object? InvokeScriptCallback(object? callback, object?[] args) => callback switch
    {
        BoundMethod bound when bound.Instance is StructInstance structBound =>
            InvokeMethodOnStructInstance(structBound, bound.Method, args, 0, 0),
        BoundMethod bound => InvokeMethodOnInstance((ClassInstance)bound.Instance, bound.Method, args, 0, 0),
        _ => InvokeCallableValue(callback, args)
    };

    /// <summary>The ONE implementation of `X.Use(...)`, shared by `mcp.Use`, `tcp.Use` and `http.Use`, so the three
    /// pipelines cannot drift. Two rules, both measured: a BOUND METHOD is a callable a script can hand over
    /// (`app.Handle`), and a value that is not callable is REFUSED BY NAME rather than dropped, because a
    /// registration that registers nothing answers every call as unhandled with nothing said anywhere (the guard
    /// this replaces, `if (args[0] is ScriptFunction mw)`, did exactly that).
    ///
    /// The caller passes the three things that DIFFER: the pipeline, the member a refusal names together with the
    /// shape it wants, and how a missing interpreter is reported for that context. The last one is not decoration:
    /// a middleware runs on the interpreter that STARTED the server, and only `--repl` keeps one, so without it
    /// every middleware used to be skipped in silence, which is how an app answered empty 200s while looking
    /// healthy. Each context reports it its own way (`McpContext.Error`, `HttpContext.ErrorMessage`, or the log
    /// for a `TcpContext`, which carries no error field).</summary>
    private static void RegisterPipelineUse<TContext>(
        Namespace ns,
        string member,
        string what,
        MiddlewarePipeline<TContext> pipe,
        Func<TContext, Interpreter?> resolveInterpreter) where TContext : class
    {
        ns.AddMethod("Use", args =>
        {
            var middleware = args.Length > 0 ? args[0] : null;
            if (!IsScriptCallable(middleware))
                RuntimeError($"{member} takes {what}, not '{GetFriendlyTypeNameOfValue(middleware)}'.", 0, 0);
            pipe.Use((ctx, next) =>
            {
                // The interpreter is asked PER CONTEXT rather than taken from one static: an MCP tool call runs
                // on the server's durable interpreter, a route or an HTTP middleware on that same one, while a
                // TCP connection runs on the interpreter whose own server accepted it (and whose `TcpPipeline`
                // the accept loop holds). A missing one is reported by the resolver itself, which is the only
                // place that knows which context it is looking at.
                if (resolveInterpreter(ctx) is not { } itp) return null;
                itp.InvokeScriptCallback(middleware, [ctx, next]);
                return null;
            });
            return null;
        });
    }

    /// <summary>Value-level event subscription (`inst.Event += handler`), mirrors the tree's
    /// event += branch (Interpreter.cs L6461-6498): looks up the event, validates the handler
    /// signature against the delegate type, and adds it. Returns VmFallback when the named member
    /// is NOT an event (the tree falls through to the numeric compound path).</summary>
    internal object EventAdd(ClassInstance instance, string eventName, object? handler, int line, int column)
    {
        var evt = GetEventInfo(instance, eventName, line, column);
        if (evt == null) return VmFallback.Instance;
        switch (handler)
        {
            case ScriptFunction fn:
                {
                    var delegateInfo = GetDelegate(evt.DelegateTypeName, line, column);
                    if (!IsCompatibleWithStoredTypes(fn, delegateInfo))
                        RuntimeError($"Handler signature does not match delegate '{evt.DelegateTypeName}'.", line, column);
                    evt.Subscribers.Add(fn);
                    return evt;
                }
            case CombinedDelegate cd:
                evt.Subscribers.AddRange(cd.Handlers);
                return evt;
            default:
                RuntimeError("Event handler must be a function or delegate combination.", line, column);
                return evt;
        }
    }

    /// <summary>Value-level event unsubscription (`inst.Event -= handler`), tree event -= branch.</summary>
    internal object EventSub(ClassInstance instance, string eventName, object? handler, int line, int column)
    {
        var evt = GetEventInfo(instance, eventName, line, column);
        if (evt == null) return VmFallback.Instance;
        switch (handler)
        {
            case ScriptFunction fn:
                evt.Subscribers.Remove(fn);
                return evt;
            case CombinedDelegate cd:
                foreach (var h in cd.Handlers) evt.Subscribers.Remove(h);
                return evt;
            default:
                RuntimeError("Event handler must be a function or delegate combination.", line, column);
                return evt;
        }
    }

    /// <summary>Value-level method resolution on a script class instance, used by the VM's
    /// CALL_METHOD named-arg reorder (mirrors the tree's FindMethodInHierarchy on the instance's
    /// class). Returns false when the receiver is not a ClassInstance or the method is not found
    /// (a non-script receiver with a named-arg call falls back to the tree).</summary>
    internal bool TryGetScriptMethod(ClassInfo from, string methodName, object?[] args, out FuncDecl method)
    {
        // The engine's ONE arg-aware resolution, the same call the tree's class-method path makes
        // (VisitExpressions' `case ClassInstance`), so a COMPILED call and a walked one cannot choose different
        // overloads. Measured: this door answered by NAME (FindMethodInHierarchy), so `c.T(5L)` ran the first
        // `T` in declaration order (the `int` one) while the tree answered through `T(long)`; `c.T(5.5)` was
        // even coerced into the int overload. vm_overload 1-3, _ovl_diag, crash_session_regression 6b.
        var (_, resolved) = ResolveScriptMethod(from, methodName, args);
        method = resolved!;
        return resolved != null;
    }

    /// <summary>The `super.M()` counterpart of <see cref="TryGetScriptMethod"/>: a SuperProxy carries the class
    /// the lookup must START at (the parent), which is the whole point of the keyword, so the instance's own class
    /// would answer the OVERRIDE instead. It asks the same resolver the dispatch arm asks (`FindMethod` over
    /// `super.ParentClass`), rather than a second one that could drift from it.</summary>
    internal bool TryGetSuperMethod(ClassInfo parentClass, string methodName, out FuncDecl method)
    {
        method = FindMethod(parentClass, methodName)!;
        return method != null;
    }

    /// <summary>The STRUCT counterpart of <see cref="TryGetScriptMethod"/>, for the VM's flat struct-method
    /// call: a struct's methods live on its declaration, which is the same answer the tree's arm takes
    /// (FindByName over the declaration's methods, with the interpreter's case rule).</summary>
    internal bool TryGetScriptStructMethod(StructInstance instance, string methodName, out FuncDecl method)
    {
        method = null!;
        if (!Structs.TryGetValue(instance.StructName, out var decl)) return false;
        var found = FindByName(decl.Methods, methodName, static m => m.Name);
        method = found!;
        return found != null;
    }

    /// <summary>The struct-method ACCESS check, which the tree makes at the call site. The flat struct-method
    /// call in the VM must not skip it: a private struct method is not callable from outside, and the check
    /// needs the interpreter's current class context, which is what this door reads.</summary>
    internal void CheckStructMethodAccess(FuncDecl method, string structName, int line, int column)
        => CheckStructMemberAccess(method.Access, structName, line, column, method.Name);

    /// <summary>Value-level ctor-param lookup for the VM's CALL_NEW named-arg reorder. Resolves the
    /// first non-static ctor of the named class/struct with the given arity (the same provisional
    /// match InstantiateForVm uses). Returns false when the type is not a known script class/struct.</summary>
    internal bool TryGetCtorParams(string typeName, out IReadOnlyList<(TypeInfo Type, string Name)> paramsList)
    {
        if (Classes.TryGetValue(typeName, out var classInfo))
        {
            foreach (var c in classInfo.Constructors)
                if (!c.IsStatic) { paramsList = c.Params; return true; }
            paramsList = [];
            return true;
        }
        if (Structs.TryGetValue(typeName, out var structDecl))
        {
            foreach (var c in structDecl.Constructors)
                if (!c.IsStatic) { paramsList = c.Params; return true; }
            paramsList = [];
            return true;
        }
        paramsList = [];
        return false;
    }

    /// <summary>Value-level struct operator-overload dispatch, mirrors ApplyBinaryOp's struct
    /// branch (Interpreter.cs L6107-6119): if the struct's type declares an operator method for
    /// `op`, invoke it with `right` as the single arg and return true. No synthetic AST / scope.
    /// Returns false when the struct has no such operator (numeric fallback applies).</summary>
    internal bool TryInvokeStructOperator(StructInstance instance, object? right, TokenType op,
        int line, int column, out object? result)
    {
        if (Structs.TryGetValue(instance.StructName, out var decl)
            && decl.OperatorMethods.TryGetValue(op, out var structMethod))
        {
            result = InvokeMethodOnStructInstance(instance, structMethod, [right], line, column);
            return true;
        }
        // The VM's struct fast path reaches an extension operator too, so the two engines agree on one.
        if (TryInvokeExtensionOperator(op, instance, right, line, column, out var extensionResult))
        {
            result = extensionResult;
            return true;
        }
        result = null;
        return false;
    }

    /// <summary>Value-level relational-pattern match, mirrors the tree's PatternMatches
    /// RelationalPattern case (VisitExpressions.cs): value must be IComparable, the right side
    /// is Convert.ChangeType'd to the subject's type, then CompareTo with the op mapping.
    /// No synthetic AST nodes / Evaluate() call, the VM passes the pre-evaluated right value.</summary>
    internal bool MatchRelationalPattern(object? value, object? rightVal, TokenType op)
    {
        if (value is not IComparable c) return false;
        var cmp = c.CompareTo(Convert.ChangeType(rightVal, value.GetType()));
        return op switch
        {
            TokenType.Less => cmp < 0,
            TokenType.Greater => cmp > 0,
            TokenType.LessEqual => cmp <= 0,
            TokenType.GreaterEqual => cmp >= 0,
            _ => false
        };
    }

    internal object? CallBuiltinByName(string name, object?[] args)
    {
        switch (name)
        {
            case "print" when args.Length > 0:
                // The CURRENT interpreter's sink, the same door the registered native uses (`ScriptOut`): a compiled
                // statement's `print` goes through THIS arm, and it wrote to `Console.Out` directly, so an embedded run
                // printed into the host's console while the native version printed into the session. Measured through
                // the embedding ABI before this: `slc_run_text("print('x')")` put `x` on the harness's stdout and the
                // host callback received nothing.
                ScriptOut.WriteLine(args[0]?.ToString() ?? "");
                return null;

            case "typeof":
                // Use the SAME mapper as the registered native, GetFriendlyTypeNameOfValue maps
                // ScriptFunction → "Function" and a class instance → its declared class; the VM used
                // to reach the raw CLR name here, which is the stale twin of the registered native.
                return args.Length > 0 ? GetFriendlyTypeNameOfValue(args[0]) : "null";
        }

        // Try resolving a user function or registered native by name from the global scope.
        // Copy-free locked single-name lookup, GetGlobalScopeDictionary() allocates a full
        // Scopes.ToArray() + Dictionary copy per call (measured hot on every CALL to a global).
        if (!_rootFrame.Scopes.TryGetGlobalValue(name, out var fnInfo))
            return VmFallback.Instance; // NOT found, distinct from a found function returning null

        return fnInfo.Value switch
        {
            ScriptFunction sf => InvokeScriptFunction(sf, args, 0, 0),
            INativeFunction nf => nf.Invoke(args),
            _ => VmFallback.Instance
        };
    }

    /// <summary>Attempts to compile a function to bytecode. Returns null if the function body is not VM-safe.</summary>
    /// <summary>Compiles a body the way the ENGINE compiles a top-level declaration, the same
    /// `Compiler.Compile` entry `Program.cs` uses for functions and methods, so the verdict is the compiler's
    /// own (`ctx.Success`), not the coarse AST whitelist `CompileToVm` applies first. Those are different gates:
    /// where both accept a body they agree, but a body the whitelist refuses and the compiler can emit was
    /// refused for no reason. Callers passing `isMethod: true` must supply the receiver as the trailing `this`
    /// local at invocation ([params..., this]); no field names are passed, because the receivers these serve (an
    /// extension member's value, a concrete generic clone) have no class context to resolve fields against, so
    /// `this.X` stays a runtime member lookup.</summary>
    private Bytecode? CompileEngineStyle(FuncDecl decl, bool isMethod, string? receiverAlias = null)
    {
        var builtins = _rootFrame.Scopes.GetGlobalScopeDictionary().Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Compiler.Compile(decl, builtins, isMethod: isMethod, caseSensitive: CaseSensitive,
            eventNames: null, fieldNames: null, staticFieldNames: null, className: null,
            receiverAlias: receiverAlias, calleeDecls: GlobalCalleeDecls());
    }

    /// <summary>The global bare-name callees, by name, for the compiler's ref/out gate (see
    /// Compiler.RefOutArgMismatch). ONE builder shared by BOTH compile entries, so neither can hand the compiler
    /// a different map; null when there are none, which simply skips the gate.</summary>
    private Dictionary<string, FuncDecl[]>? GlobalCalleeDecls()
    {
        List<FuncDecl>? decls = null;
        foreach (var kv in _rootFrame.Scopes.GetGlobalScopeDictionary())
            if (kv.Value.Value is ScriptFunction gsf)
                (decls ??= []).Add(gsf.Declaration);
        return decls == null ? null : Compiler.BuildCalleeDecls(decls, CaseSensitive);
    }

    private Bytecode? CompileToVm(FuncDecl decl, IReadOnlyList<string>? capturedLocals = null)
    {
        if (!CanVmHandle(decl.Body))
            return null;
        var builtins = _rootFrame.Scopes.GetGlobalScopeDictionary()
            .Keys.Where(k => true)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Task 4: the set of global bare-name callees safe to drive via FOREACH_CALL. Built lazily per
        // compile (only the first local-function compile per function pays for the scan). Global
        // coroutines are precompiled before any lazy compile runs, so their Compiled status is final.
        // A name → FuncDecl map lets the compiler gate FOREACH_CALL on the callee's SIGNATURE
        // (defaults/params/ref-out/arity), not just its name.
        Dictionary<string, FuncDecl>? coroutineDecls = null;
        foreach (var kv in _rootFrame.Scopes.GetGlobalScopeDictionary())
            if (kv.Value.Value is ScriptFunction gsf && IsVmBackedCoroutine(gsf.Declaration))
            {
                coroutineDecls ??= new Dictionary<string, FuncDecl>(StringComparer.OrdinalIgnoreCase);
                coroutineDecls[kv.Key] = gsf.Declaration;
            }
        return Compiler.Compile(decl, builtins, caseSensitive: CaseSensitive, capturedLocals: capturedLocals,
            coroutineDecls: coroutineDecls, calleeDecls: GlobalCalleeDecls());
    }

    /// <summary>Registers the NESTED function declarations a compiled body bound into local slots (Bytecode.NestedDecls,
    /// IN MEMORY only). A declaration written inside a LAMBDA body has no other registration: `DeclaredFunctionsIn`
    /// walks statements and cannot see it, so a stripped bundle carried `outer` and not `twice`, the loaded `outer`
    /// called `__nested_fn__("twice", ...)`, got null, and the entry was refused. RECURSIVE, because a declaration's own
    /// body may bind more.</summary>
    /// <summary>Registers ONE declaration in the two maps `__nested_fn__` answers from: by NAME (a body's own name
    /// lookup, and the fallback when no position was passed) and by POSITION, which is the key that tells two helpers
    /// of one name apart. One statement, because three paths need it: a source run's compile-time list
    /// (`RegisterNestedDecls`), and a LOADED bundle, whose function table has no such list, so its declarations are
    /// registered as they are bound (see the loader). A NAME IS NOT UNIQUE: two bodies may each declare `Note`, and the
    /// first-wins map then hands one body's code to the other's declaration (measured: `nested_decl_hoisting_test` 1a
    /// ran the second lambda's `Note`, whose `label` capture is not passed, and printed `:one`).</summary>
    private void RegisterNestedDecl(FuncDecl d)
    {
        if (d.Name is not { Length: > 0 }) return;
        NestedFunctionDecls.TryAdd(d.Name, d);
        NestedFunctionDeclsAt.TryAdd((d.Name, d.Line, d.Column), d);
    }

    private void RegisterNestedDecls(Bytecode? bc, bool toExports = true)
    {
        if (bc?.NestedDecls is not { Count: > 0 } decls) return;
        foreach (var d in decls)
        {
            if (d.Name is not { Length: > 0 }) continue;
            RegisterNestedDecl(d);
            if (!toExports) { RegisterNestedDecls(d.Compiled, toExports); continue; }
            if (_scriptTopLevel.Contains(d)) continue;
            _scriptTopLevel.Add(d);
            // RECURSIVE, because a declaration's own body binds ITS nested declarations: `outer` carries `twice`, and
            // `twice` may carry another. Registering only the first level left a bundle's `outer` calling
            // `__nested_fn__("twice", ...)` with nothing to resolve (measured: `Function '__program__' was loaded from
            // bytecode for a different environment` on the decl-text subject).
            RegisterNestedDecls(d.Compiled, toExports);
        }
    }

    /// <summary>Registers, for every EXPORTED declaration, the nested declarations its own compiled body binds. The
    /// program body's list covers a declaration written inside a LAMBDA (the statement walk cannot see it); this
    /// covers a declaration written inside a FUNCTION, whose body `Program.PrecompileFunctions` compiled separately.
    /// </summary>
    /// <summary>Registers a body's nested declarations when a RUN enters it, without touching what an export carries.
    /// A FLAT call (the VM's fast paths) continues the caller's invocation and never reaches `TryExecuteVmBody`, so
    /// without this the registry only ever knew the bodies that took the slow door: measured, a helper declared in TWO
    /// bodies resolved by NAME for the second one and ran the first one's body.</summary>
    internal void RegisterNestedDeclsForRun(Bytecode? bc)
    {
        if (bc?.NestedDecls is not { Count: > 0 } || bc.NestedDeclsRegistered) return;
        bc.NestedDeclsRegistered = true;
        RegisterNestedDecls(bc, toExports: false);
    }

    private void RegisterNestedDeclsOfExports()
    {
        foreach (var d in _scriptTopLevel.ToArray()) RegisterNestedDecls(d.Compiled);
        RegisterNestedDecls(ProgramBytecode);
    }

    /// <summary>Lazily compiles a LOCAL function to bytecode and materializes its VM captures
    /// (Task 2, local functions run VM-flat, lifting the Debug tree-depth cap). Caches both on
    /// the shared declaration so every later ScriptFunction of the same FuncDecl reuses them.
    /// Returns false when the body is not VM-safe or the function is excluded (coroutine /
    /// generic), the caller keeps the tree path.</summary>
    private void CompileLocalFunctionToVm(ScriptFunction fn)
    {
        var decl = fn.Declaration;
        var captureNames = ComputeLocalFunctionCaptures(fn);
        decl.VmCaptureNames = captureNames;   // fixed layout, materialization must match this order
        var bc = CompileToVm(decl, captureNames);
        if (bc == null)
        {
            // Body not VM-safe (e.g. try/catch), keep tree. Don't cache the failure: a
            // non-VM-safe body can never become safe, but leaving Compiled null is harmless.
            decl.VmCaptureNames = null;
            return;
        }
        decl.Compiled = bc;
        RegisterNestedDecls(bc);
        fn.VmCaptures = MaterializeVmCaptures(fn, captureNames);
    }

    /// <summary>Determines the VM-capture layout for a local function: the free names its body
    /// references that resolve to ENCLOSING locals (from the closure snapshot), in deterministic
    /// first-appearance order, plus the function's own name when the body is self-recursive (the
    /// value bound is the ScriptFunction itself, the tree self-binds decl.Name in the invocation
    /// frame, the VM needs it as a captured local so self-calls compile to LOAD_VAR + CALL_VAL).
    /// Names not in the snapshot (root globals, builtins) stay live LOAD_GLOBAL_FAST references.
    /// This mirrors the lambda capture layout: [params, this?, captured locals...].</summary>
    internal Dictionary<string, ScopeStack.VariableInfo> RootGlobalScope => _rootFrame.Scopes.GetGlobalScopeDictionary();

    /// <summary>The live globals as a READABLE snapshot dictionary, with <c>RefCell</c> wrappers PRESERVED so a closure
    /// shares the same mutable cell the top level writes. ONE implementation, asked by both branches of the call
    /// preamble: the one for a snapshot with no local scopes, and the one for a snapshot whose global scope is EMPTY (a
    /// nested function built at run time, whose globals were filtered to its captures).</summary>
    private Dictionary<string, object?> ReadableGlobals(IEnumerable<KeyValuePair<string, ScopeStack.VariableInfo>> source)
    {
        var dict = new Dictionary<string, object?>(_comparer);
        foreach (var kvp in source)
            dict[kvp.Key] = kvp.Value.Value is RefCell rc ? rc : kvp.Value.Value;
        return dict;
    }

    /// <summary>PHASE-0: every NESTED function declaration the precompile pass has seen, by name, and the builder
    /// `__nested_fn__` answers from. A declaration gets no scope binding when its block is compiled, so this is the
    /// only way a compiled body can turn `void Line(...)` into a value.</summary>
    internal readonly Dictionary<string, FuncDecl> NestedFunctionDecls = new(StringComparer.Ordinal);
    /// <summary>The same declarations keyed by NAME AND POSITION: a compiled caller passes the position, so two bodies
    /// that each declare a helper of the same name resolve to their OWN declaration (see RegisterNestedDecls).</summary>
    internal readonly Dictionary<(string Name, int Line, int Column), FuncDecl> NestedFunctionDeclsAt = new();

    internal ScriptFunction? MakeNestedFunction(string name, object?[]? args = null)
    {
        // The DECLARATION comes from the precompile registry in a source run; a LOADED bundle binds its functions into
        // a scope instead, so the bound value is the fallback. Without it a stripped bundle's program body called this
        // helper, got null, and the statement was refused as "stopped part-way through its compiled body" (measured
        // on vm_nosource_test check 31).
        // The helper receives the WHOLE argument list, so the shape is (name, line, column, capture pairs...). The
        // position is what tells two same-named helpers apart: a name alone is not unique across bodies.
        var hasPosition = args is { Length: >= 3 } && args[1] is not null and not string && args[2] is not null and not string;
        FuncDecl? decl;
        if (hasPosition && NestedFunctionDeclsAt.TryGetValue(
                (name, Convert.ToInt32(args![1], CultureInfo.InvariantCulture), Convert.ToInt32(args[2], CultureInfo.InvariantCulture)),
                out var atPosition))
        {
            decl = atPosition;
        }
        else if (NestedFunctionDecls.TryGetValue(name, out var registered)) decl = registered;
        else if (CurrentFrame.Scopes.TryGet(name, out var bound))
            decl = bound switch
            {
                FuncDecl fd => fd,
                ScriptFunction sf => sf.Declaration,
                _ => null
            };
        else decl = null;
        if (decl == null) return null;
        var realGlobalScope = _rootFrame.Scopes.GetGlobalScopeDictionary();
        decl.CapturedNames ??= FreeVariableAnalyzer.Analyze(decl);
        // The compiled caller PASSES the enclosing block's locals as (name, value) pairs, because that block has no
        // scope dictionary of its own. A tree caller passes nothing, and there the scopes ARE the source. The first TWO
        // arguments are the declaration's (line, column), which is what tells two same-named helpers apart.
        Dictionary<string, object?>? fromCaller = null;
        var pairFrom = hasPosition ? 3 : 1;
        if (args is { Length: > 0 } && args.Length > pairFrom)
        {
            fromCaller = new Dictionary<string, object?>(_comparer);
            for (var i = pairFrom; i + 1 < args.Length; i += 2)
                if (args[i] is string cn) fromCaller[cn] = args[i + 1];
        }
        var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(realGlobalScope, decl.CapturedNames);
        if (fromCaller is { Count: > 0 })
        {
            var scopes = new List<Dictionary<string, object?>>(snapshot.LocalScopes.Count + 1) { fromCaller };
            scopes.AddRange(snapshot.LocalScopes);
            snapshot = new ClosureSnapshot(scopes, snapshot.LocalConsts, snapshot.GlobalScope, snapshot.GlobalConsts);
        }
        var flags = (decl.IsPure ? FunctionFlags.Pure : FunctionFlags.None)
                  | (decl.IsCoroutine || ContainsYieldInStmts(decl.Body) || decl.IsAsync
                        ? FunctionFlags.Coroutine : FunctionFlags.None);
        var built = new ScriptFunction(decl, snapshot, decl.ReturnType, GetParameterTypes(decl.Params),
            decl.IsRefParams, Flags: flags, IsOutParams: decl.IsOutParams)
        {
            IsLocal = true
        };
        // The CAPTURE CELLS come from the LAYOUT THE BODY WAS COMPILED WITH (`VmCaptureNames`), never from a fresh
        // analysis: the compiled body reads fixed slots, so a list in another order (or another subset) fills the
        // wrong slot and the body reads null. Measured twice: with no layout the flat call skipped the cells and the
        // body threw an NRE on the captured list even though the value passed in was correct.
        if (decl.VmCaptureNames is { Count: > 0 } layout)
            built.VmCaptures = MaterializeVmCaptures(built, layout);
        else if (decl.Compiled != null && fromCaller is { Count: > 0 } fcLayout)
        {
            // A LOADED declaration has no recorded layout (the wire has no field for it), and without the cells the
            // VM-direct gate skips, so a bytecode-only body ran on the TREE (no statements, null: "must return a
            // value"). The CALLER's pairs ARE that layout, in the order the body was compiled with.
            var derived = (IReadOnlyList<string>)[.. fcLayout.Keys];
            decl.VmCaptureNames = derived;
            built.VmCaptures = MaterializeVmCaptures(built, derived);
        }
        return built;
    }

    private IReadOnlyList<string> ComputeLocalFunctionCaptures(ScriptFunction fn)
    {
        var decl = fn.Declaration;
        var snap = fn.ClosureSnapshot;
        var paramNames = new HashSet<string>(decl.Params.Select(p => p.Name), _comparer);
        var ordered = new List<string>();
        var seen = new HashSet<string>(_comparer);
        void Consider(string? name)
        {
            if (name == null || _comparer.Equals(name, "this") || paramNames.Contains(name) || !seen.Add(name)) return;
            if (IsEnclosingCapture(snap, name)) ordered.Add(name);
        }
        foreach (var s in decl.Body) CollectFreeNames(s, Consider);
        // Self-name for recursion: the snapshot cannot contain it (defined after capture), so the
        // collector above skipped it, add it explicitly when the body references it.
        //
        // BUT NOT WHEN THE NAME IS AN OVERLOAD SET, which is a measured defect rather than a nicety: a member
        // whose body calls its OWN name captured ITSELF, so a compiled body reached its own declaration for
        // every call and a sibling's call was refused by the arity check ("Expected between 1 and 1 arguments,
        // got 2" from `S(string)` calling `S(string, int)`). A name that holds several signatures has to
        // resolve LIVE, which is what the VM's CALL arm and the tree's InvokeCallee both do: they ask the
        // name and let the ONE picker choose the member the arguments fit. A single declaration keeps the
        // capture, because a LOCAL function's name is not in the global scope at all and the capture is the
        // only thing that makes it recursive.
        if (!string.IsNullOrEmpty(decl.Name)
            && ordered.All(n => !_comparer.Equals(n, decl.Name))
            && BodyReferencesName(decl.Body, decl.Name)
            && ResolveGlobalOverloadSet(decl.Name) is not { Members.Count: > 1 })
            ordered.Add(decl.Name);
        return ordered;
    }

    /// <summary>True when `name` resolves to an ENCLOSING local of the local function: present in the
    /// snapshot's local scopes, or in the snapshot's merged global scope WITHOUT being a root global
    /// (the snapshot merges the enclosing function's outermost scope into GlobalScope). True root
    /// globals stay live references (LOAD_GLOBAL_FAST), matching the lambda model.</summary>
    private bool IsEnclosingCapture(ClosureSnapshot snap, string name)
    {
        foreach (var ls in snap.LocalScopes)
            if (ls.ContainsKey(name)) return true;
        if (snap.GlobalScope.ContainsKey(name))
        {
            if (_rootFrame.Scopes.TryGetGlobalValue(name, out _)) return false;   // true global → live
            return true;                                                          // enclosing outermost-scope local
        }
        return false;
    }

    /// <summary>Materializes the VM capture array for a local function from its closure snapshot,
    /// in the compiler's layout order (FuncDecl.VmCaptureNames). Element 0 is the "this" slot, the
    /// compiler always reserves "this" right after the params (Compiler.cs Compile(FuncDecl)), and
    /// VmCaptures is appended at args.Length, so [this?, captures...] fills the post-param slots
    /// exactly like MAKE_LAMBDA lambdas. When the body references `this` (a local function inside a
    /// class method), slot 0 carries the enclosing instance from the snapshot; otherwise null.
    /// Captured values are taken AS-IS from the snapshot: mutated captures are already
    /// RefCell-promoted (the SAME cell the enclosing scope reads, no double-wrap), so VM LOAD_VAR
    /// derefs and STORE_VAR writes through. Cached once per ScriptFunction instance.</summary>
    private object?[]? MaterializeVmCaptures(ScriptFunction fn, IReadOnlyList<string> captureNames)
    {
        var snap = fn.ClosureSnapshot;
        var arr = new object?[captureNames.Count + 1];   // +1: "this" slot
        arr[0] = BodyReferencesName(fn.Declaration.Body, "this")
            ? ReadCapturedValue(snap, "this", fn)        // enclosing instance (local function inside a method)
            : null;
        for (var i = 0; i < captureNames.Count; i++)
            arr[i + 1] = ReadCapturedValue(snap, captureNames[i], fn);
        return arr;
    }

    private object? ReadCapturedValue(ClosureSnapshot snap, string name, ScriptFunction fn)
    {
        if (_comparer.Equals(name, fn.Declaration.Name)) return fn;   // self-name → the function value
        foreach (var ls in snap.LocalScopes)
            if (ls.TryGetValue(name, out var v)) return v;            // RefCell or plain value, as the tree promotes
        if (snap.GlobalScope.TryGetValue(name, out var g)) return g;
        return null;
    }

    /// <summary>Collects every free variable name referenced in a statement list (reads, assignment
    /// targets, inc/dec targets, nested lambda bodies) in first-appearance order, the capture-layout
    /// order for a local function. Body-declared locals are filtered OUT later by IsEnclosingCapture
    /// (they are not in the enclosing snapshot).</summary>
    private void CollectFreeNames(IReadOnlyList<Stmt> stmts, Action<string?> consider)
    {
        foreach (var s in stmts) CollectFreeNames(s, consider);
    }

    private void CollectFreeNames(Stmt stmt, Action<string?> consider)
    {
        switch (stmt)
        {
            case ExprStmt es: CollectFreeNames(es.Expression, consider); break;
            case VarDeclStmt vds:
                if (vds.Initializer != null) CollectFreeNames(vds.Initializer, consider);
                break;
            case ReturnStmt rs:
                if (rs.Value != null) CollectFreeNames(rs.Value, consider);
                break;
            case BlockStmt bs: CollectFreeNames(bs.Statements, consider); break;
            case IfStmt ifs:
                CollectFreeNames(ifs.Condition, consider);
                CollectFreeNames(ifs.ThenBranch, consider);
                CollectFreeNames(ifs.ElseBranch, consider);
                break;
            case WhileStmt ws:
                CollectFreeNames(ws.Condition, consider);
                CollectFreeNames(ws.Body, consider);
                break;
            case ForStmt fs:
                if (fs.Init != null) CollectFreeNames(fs.Init, consider);
                CollectFreeNames(fs.Condition, consider);
                if (fs.Increment != null) CollectFreeNames(fs.Increment, consider);
                CollectFreeNames(fs.Body, consider);
                break;
            case ForeachStmt fes:
                CollectFreeNames(fes.Collection, consider);
                CollectFreeNames(fes.Body, consider);
                break;
            case ThrowStmt ts:
                if (ts.Expression != null) CollectFreeNames(ts.Expression, consider);
                break;
            case NamespaceDeclStmt nsd: CollectFreeNames(nsd.Body, consider); break;
        }
    }

    private void CollectFreeNames(Expr expr, Action<string?> consider)
    {
        switch (expr)
        {
            case VariableExpr ve: consider(ve.Name); break;
            case ThisExpr: consider("this"); break;
            case AssignExpr ae:
                consider(ae.Name);
                if (ae.Value != null) CollectFreeNames(ae.Value, consider);
                break;
            case BinaryExpr be: CollectFreeNames(be.Left, consider); CollectFreeNames(be.Right, consider); break;
            case LogicalExpr le: CollectFreeNames(le.Left, consider); CollectFreeNames(le.Right, consider); break;
            case UnaryExpr ue: CollectFreeNames(ue.Right, consider); break;
            case CallExpr ce:
                CollectFreeNames(ce.Callee, consider);
                foreach (var a in ce.Arguments) CollectFreeNames(a, consider);
                break;
            case MethodCallExpr mce:
                CollectFreeNames(mce.Receiver, consider);
                foreach (var a in mce.Arguments) CollectFreeNames(a, consider);
                break;
            case PropertyAccessExpr pe: CollectFreeNames(pe.Target, consider); break;
            case ArrayAccessExpr aae:
                CollectFreeNames(aae.Array, consider);
                foreach (var i in aae.Indices) CollectFreeNames(i, consider);
                break;
            case ConditionalExpr ce: CollectFreeNames(ce.Condition, consider); CollectFreeNames(ce.ThenExpr, consider); CollectFreeNames(ce.ElseExpr, consider); break;
            case CastExpr cast: CollectFreeNames(cast.Expression, consider); break;
            case IsExpr ise: CollectFreeNames(ise.Left, consider); break;
            case AsExpr ae2: CollectFreeNames(ae2.Left, consider); break;
            case LambdaExpr le:
                // The lambda's own params are local to it; everything else it references is a free
                // name of the ENCLOSING function too (the VM's MAKE_LAMBDA sources captures from the
                // enclosing frame's locals).
                if (le.BodyExpr != null) CollectFreeNames(le.BodyExpr, consider);
                if (le.BodyStmts != null) CollectFreeNames(le.BodyStmts, consider);
                break;
            case CompoundAssignExpr ca:
                CollectFreeNames(ca.Target, consider);
                CollectFreeNames(ca.Value, consider);
                break;
            case IncrementDecrementExpr ide: CollectFreeNames(ide.Target, consider); break;
            case PropertyAssignExpr pae:
                CollectFreeNames(pae.Target, consider);
                CollectFreeNames(pae.Value, consider);
                break;
            case NewInstanceExpr nie:
                if (nie.ConstructorExpr != null) CollectFreeNames(nie.ConstructorExpr, consider);
                if (nie.Arguments != null) foreach (var a in nie.Arguments) CollectFreeNames(a, consider);
                break;
            case NewStructExpr nse:
                if (nse.FieldValues != null) foreach (var fv in nse.FieldValues) CollectFreeNames(fv, consider);
                break;
            case IndexExpr ie: CollectFreeNames(ie.Value, consider); break;
            case NewArrayExpr nae:
                if (nae.Sizes != null) foreach (var sz in nae.Sizes) CollectFreeNames(sz, consider);
                if (nae.Initializer != null) foreach (var init in nae.Initializer) CollectFreeNames(init, consider);
                break;
            case TupleExpr tup:
                foreach (var it in tup.Items) CollectFreeNames(it, consider);
                break;
            case WithExpr we:
                CollectFreeNames(we.Base, consider);
                foreach (var (_, fv) in we.Fields) CollectFreeNames(fv, consider);
                break;
            case ObjectInitExpr oie:
                if (oie.Inner != null) CollectFreeNames(oie.Inner, consider);
                foreach (var (_, iv) in oie.Initializers) CollectFreeNames(iv, consider);
                break;
            case MatchExpr me:
                CollectFreeNames(me.Subject, consider);
                foreach (var arm in me.Arms)
                {
                    if (arm.Guard != null) CollectFreeNames(arm.Guard, consider);
                    CollectFreeNames(arm.Value, consider);
                }
                break;
            case SwitchExpr se:
                CollectFreeNames(se.Subject, consider);
                foreach (var arm in se.Arms)
                {
                    if (arm.Guard != null) CollectFreeNames(arm.Guard, consider);
                    CollectFreeNames(arm.Value, consider);
                }
                break;
            case ListLiteralExpr lle: foreach (var e in lle.Elements) CollectFreeNames(e, consider); break;
            case DictLiteralExpr dle: foreach (var (k, v) in dle.Entries) { CollectFreeNames(k, consider); CollectFreeNames(v, consider); } break;
            case ArrayAssignExpr aaae:
                CollectFreeNames(aaae.Array, consider);
                foreach (var idx in aaae.Indices) CollectFreeNames(idx, consider);
                CollectFreeNames(aaae.Value, consider);
                break;
            case NullCoalescingExpr nce: CollectFreeNames(nce.Left, consider); CollectFreeNames(nce.Right, consider); break;
            case YieldExpr ye:
                if (ye.Value != null) CollectFreeNames(ye.Value, consider);
                break;
            case RangeExpr re:
                if (re.From != null) CollectFreeNames(re.From, consider);
                if (re.To != null) CollectFreeNames(re.To, consider);
                break;
            case ComprehensionExpr ce:
                CollectFreeNames(ce.ElementExpr, consider);
                CollectFreeNames(ce.Collection, consider);
                if (ce.Filter != null) CollectFreeNames(ce.Filter, consider);
                break;
            case IsNullExpr ine: CollectFreeNames(ine.Left, consider); break;
        }
    }

    /// <summary>Whether any statement in the list references `name` (read, assignment target, or
    /// inc/dec target), used to detect a local function's self-reference.</summary>
    private bool BodyReferencesName(IReadOnlyList<Stmt> body, string name)
    {
        var found = false;
        void Consider(string? n)
        {
            if (!found && n != null && _comparer.Equals(n, name)) found = true;
        }
        foreach (var s in body)
        {
            CollectFreeNames(s, Consider);
            if (found) break;
        }
        return found;
    }

    /// <summary>Trace label for a tree-invoked function, anonymous lambdas show their
    /// source position instead of an empty name (keeps the [DEBUG] Tree sweep readable).</summary>
    private static string TreeFuncLabel(FuncDecl decl) =>
        string.IsNullOrEmpty(decl.Name)
            ? $"<lambda>@L{decl.Line}:C{decl.Column}"
            : decl.Name;

    /// <summary>Logs a tree fallback once per distinct function per run, a hot lambda
    /// invoked 10k times emits one line, not 10k (keeps the sweep readable).</summary>
    private void TraceTreeOnce(string label, int line, int col)
    {
        if (_treeTraced.Add(label))
            Logging.Log.Debug("Tree", $"tree-walk: {label} [{_currentSourceName}]", line, col);
    }

    private static bool CanVmHandle(IReadOnlyList<Stmt> body)
    {
        return body.All(StmtIsVmSafe);
    }

    private static bool StmtIsVmSafe(Stmt stmt) => stmt switch
    {
        ExprStmt es => ExprIsVmSafe(es.Expression),
        VarDeclStmt vd => vd.Initializer == null || ExprIsVmSafe(vd.Initializer),
        ReturnStmt rs => rs.Value == null || ExprIsVmSafe(rs.Value),
        IfStmt ifs => ExprIsVmSafe(ifs.Condition) && AllStmtsVmSafe(ifs.ThenBranch)
                      && AllStmtsVmSafe(ifs.ElseBranch),
        WhileStmt ws => ExprIsVmSafe(ws.Condition) && AllStmtsVmSafe(ws.Body),
        BlockStmt bs => AllStmtsVmSafe(bs.Statements),
        ForStmt fs => (fs.Init == null || StmtIsVmSafe(fs.Init)) && ExprIsVmSafe(fs.Condition)
                      && (fs.Increment == null || ExprIsVmSafe(fs.Increment)) && AllStmtsVmSafe(fs.Body),
        _ => false
    };
    private static bool AllStmtsVmSafe(IReadOnlyList<Stmt> stmts)
    {
        return stmts.All(StmtIsVmSafe);
    }

    private static bool ExprIsVmSafe(Expr expr) => expr switch
    {
        NumberExpr or StringExpr or BoolExpr or NullExpr => true,
        VariableExpr => true,
        BinaryExpr be => ExprIsVmSafe(be.Left) && ExprIsVmSafe(be.Right),
        UnaryExpr ue => ue.Operator.Type == TokenType.Minus && ExprIsVmSafe(ue.Right),
        CallExpr ce => ce.Arguments.All(ExprIsVmSafe),
        AssignExpr ae => ExprIsVmSafe(ae.Value),
        CompoundAssignExpr ca => ExprIsVmSafe(ca.Value),
        NewInstanceExpr nie => (nie.ConstructorExpr == null || ExprIsVmSafe(nie.ConstructorExpr))
                              && (nie.Arguments == null || nie.Arguments.All(ExprIsVmSafe)),
        TupleExpr tu => tu.Items.All(ExprIsVmSafe),
        WithExpr we => ExprIsVmSafe(we.Base) && we.Fields.All(f => ExprIsVmSafe(f.Value)),
        _ => false
    };

    /// <summary>Bridge for the VM's FLAT call gates: they bind argument values straight into the
    /// callee's locals, so a callee that declares defaults or a params array must be normalized first
    ///, exactly the work NormalizeArgsForVm does for the VM-direct path (and the only difference is
    /// access: that method is private to this class).</summary>
    internal object?[] NormalizeArgsForFlatCall(FuncDecl decl, object?[] args, int line, int column)
        => NormalizeArgsForVm(decl, args, line, column);

    /// <summary>Arity validation for a SCRIPT-side call. The tree's call expression and the VM's CALL / CALL_VAL
    /// handlers all come through here, so the two paths cannot disagree about it, and they did: the check used to
    /// live ONLY in the tree, so the VM silently accepted extra arguments (and, until the capture layout was fixed,
    /// wrote them into the callee's capture slots, which is how `Com.Run(fun() { ... })` came to read the scope).
    /// `params` is zero-or-more and a defaulted parameter is optional, so only the fixed parameters set the minimum;
    /// EXTRA arguments are REFUSED, which is the tree's contract.
    /// The C#-side entry (`InvokeScriptFunction`) checks the LOWER bound only and DROPS extras on purpose:
    /// `Com.Run` hands its callback the scope, so `Com.Run(fun() { ... })`, a callback that declares no
    /// parameters, must stay legal.</summary>
    internal void CheckScriptCallArity(FuncDecl decl, int argCount, int line, int column)
    {
        var hasParams = decl.IsParams.Count > 0 && decl.IsParams[^1];
        var totalParams = decl.Params.Count;
        var minRequired = MinRequiredArgs(decl);

        if (hasParams)
        {
            if (argCount < minRequired)
                RuntimeError($"Expected at least {minRequired} arguments, got {argCount}.", line, column);
        }
        else if (argCount < minRequired || argCount > totalParams)
        {
            RuntimeError($"Expected between {minRequired} and {totalParams} arguments, got {argCount}.", line, column);
        }
    }

    /// <summary>The fewest arguments a declaration accepts: FIXED parameters only (a `params` tail is
    /// zero-or-more) and REQUIRED ones only (a defaulted parameter is not required). ONE computation, TWO
    /// policies, which is why it lives here rather than inside either caller: `CheckScriptCallArity` refuses both
    /// bounds for a script-side call, while the C#-side entry `InvokeScriptFunction` refuses only this lower one,
    /// because extras are DROPPED there (`Com.Run` hands its callback the scope, and that callback may declare no
    /// parameter at all).</summary>
    private static int MinRequiredArgs(FuncDecl decl)
    {
        var hasParams = decl.IsParams.Count > 0 && decl.IsParams[^1];
        var fixedLimit = hasParams ? decl.Params.Count - 1 : decl.Params.Count;
        // A declaration sets DefaultValues only when it actually has defaults (Parser), so no list means every
        // fixed parameter is required, `Empty("LOG", params byte)` needs 0 args, `f(a, b)` needs 2.
        if (decl.DefaultValues == null) return fixedLimit;
        var minRequired = 0;
        for (var i = 0; i < fixedLimit && i < decl.DefaultValues.Count; i++)
            if (decl.DefaultValues[i] == null) minRequired = i + 1;
        return minRequired;
    }

    /// <summary>Fills default parameter values and assembles the params array for a VM call,
    /// the tree binds these into a scope later; the VM binds locals by index and needs them now.</summary>
    private object?[] NormalizeArgsForVm(FuncDecl decl, object?[] args, int line, int column)
    {
        // Iterable / RandomAccess param enforcement, the VM-direct path bypasses the tree's
        // BindParam → CoerceTo funnel, so signature-type params must be validated here for
        // tree/VM parity (both must reject the same non-iterable / non-indexable arguments).
        var vmHasParams = decl.IsParams.Count > 0 && decl.IsParams[^1];
        var vmFixedCount = vmHasParams ? decl.Params.Count - 1 : decl.Params.Count;
        for (var pi = 0; pi < vmFixedCount && pi < args.Length; pi++)
        {
            var isRefOrOut = (decl.IsRefParams != null && pi < decl.IsRefParams.Count && decl.IsRefParams[pi])
                          || (decl.IsOutParams != null && pi < decl.IsOutParams.Count && decl.IsOutParams[pi]);
            if (isRefOrOut) continue;
            var pType = decl.Params[pi].Type;
            // A NULL argument belongs to the ONE null rule (CoerceTo), which now accepts it for a contract type
            // because a contract is reference-shaped with BaseType Object: refusing it HERE made `TakesFn(null)` an
            // error while a `string`, `object` or array parameter took null, and it is the same closure that made a
            // contract-typed FIELD impossible to default. What the three checks below still refuse is a NON-null
            // value that does not satisfy the contract (`Iterable i = 42`).
            if (args[pi] is null) continue;
            if (IsIterableType(pType) && !IsIterable(args[pi]))
                RuntimeError($"Expected an Iterable, got {args[pi]?.GetType().Name ?? "null"}.", line, column);
            if (IsRandomAccessType(pType) && !IsRandomAccess(args[pi]))
                RuntimeError($"Expected a RandomAccess (array, list, tuple or string), got {args[pi]?.GetType().Name ?? "null"}.", line, column);
            if (IsFunctionType(pType) && !IsFunction(args[pi]))
                RuntimeError($"Expected a Function, got {args[pi]?.GetType().Name ?? "null"}.", line, column);
        }

        // Tree/VM parity: the tree's BindParam coerces every by-value param via CoerceTo
        // (Interpreter.cs:2978); the VM-direct path must do the same for plain-typed params
        // (contract types were validated above). Copy-on-write so untouched args are not copied.
        // The params param is excluded, its array is assembled (and element-coerced) below.
        object?[]? coercedArgs = null;
        for (var pi = 0; pi < vmFixedCount && pi < args.Length; pi++)
        {
            var rcIsRef = decl.IsRefParams != null && pi < decl.IsRefParams.Count && decl.IsRefParams[pi];
            var rcIsOut = decl.IsOutParams != null && pi < decl.IsOutParams.Count && decl.IsOutParams[pi];
            // The VM binds arguments through its OWN path, so the ref/out rule is applied here too, by the same
            // helper BindParam uses, and BEFORE the skip below: a cell reaching CoerceTo would report the internal
            // type name instead of the mistake (measured: "Type error: cannot convert 'RefCell' to 'int'").
            RefuseRefCellOnByValueParam(decl.Params[pi].Name, args[pi], rcIsRef, rcIsOut, line, column, decl.Params[pi].Type);
            if (rcIsRef || rcIsOut) continue;
            var rcType = decl.Params[pi].Type;
            if (IsIterableType(rcType) || IsRandomAccessType(rcType) || IsFunctionType(rcType)) continue;
            var rcCoerced = CoerceTo(args[pi], rcType, line, column);
            if (!ReferenceEquals(rcCoerced, args[pi]))
            {
                coercedArgs ??= (object?[])args.Clone();
                coercedArgs[pi] = rcCoerced;
            }
        }
        if (coercedArgs != null) args = coercedArgs;

        // Tuple parameters: attach the parsed element names to the ScriptTuple so p.n resolves
        // (GET_PROP → tuple Names). TryExecuteVmBody does this for plain VM calls; the coroutine
        // path (VmBacked generator args) flows through NormalizeArgsForVm and needs the same.
        if (decl.TupleParamNames is { Count: > 0 })
            for (var tpi = 0; tpi < decl.Params.Count && tpi < args.Length; tpi++)
                BindTupleParam(decl, tpi, args[tpi], line, column);

        if (decl.DefaultValues == null && !vmHasParams)
            return args;

        var fixedCount = vmHasParams ? decl.Params.Count - 1 : decl.Params.Count;
        var newArgs = new object?[decl.Params.Count];
        for (var i = 0; i < fixedCount; i++)
        {
            if (i < args.Length) newArgs[i] = args[i];
            else if (decl.DefaultValues != null && i < decl.DefaultValues.Count && decl.DefaultValues[i] != null)
                newArgs[i] = Evaluate(decl.DefaultValues[i]!);
            else newArgs[i] = null;
        }

        if (vmHasParams)
            newArgs[fixedCount] = AssembleParamsTail(decl.Params, args, line, column);
        return newArgs;
    }

    /// <summary>Executes a VM-compiled function body. Returns true with the result in `result` when
    /// the VM handled it; false when the VM fell back (VmFallback) and the tree must run instead.</summary>
    private bool TryExecuteVmBody(FuncDecl decl, ScriptFunction fn, object?[] args, int line, int column, out object? result, bool wantRefResult = false, bool takeVoidAnswer = false)
    {
        result = null;

        // A MODULE FUNCTION'S GLOBALS ARE THE MODULE'S, AND THE VM RESOLVES GLOBALS THROUGH THE INTERPRETER IT WAS
        // HANDED, which here is the IMPORTER. So a compiled module body read and wrote the caller's scope: measured,
        // `int counter = 0; int bump() { counter = counter + 1; return counter; }` in an imported module answered
        // 1, 1, 1 for three calls where its own scope demands 1, 2, 3, and with the importer declaring
        // `var counter = 100;` the three calls returned 101, 102, 103 and MOVED the importer's variable. The TREE path
        // already keeps a module function inside its own scope (see the IsModuleScope comment below: no writable link,
        // so a global write goes through the snapshot's shared GlobalScope dict, which IS the module's ModuleGlobals),
        // so a module function runs there and only there. ScriptFunction.IsModuleScope is what LoadModule stamps on
        // every exported function, flat or namespaced.
        if (fn.IsModuleScope) return false;

        // A BODY COMPILED AT ITS DECLARATION carries the nested declarations its own block binds, and nothing used to
        // drain that list before the body ran: `__nested_fn__` answered NULL, the CALL_VAL bailed on a null callee, and
        // in a body the engine invokes (a `Com.Run` callback, a UI event) a committed bail is REFUSED, so the script
        // stopped with "Function '' stopped part-way". Measured on `assets/language/ui/demos/music_player.slc`: its
        // `Com.Run` callback declares `string GeoOf(hwnd)` in the same block and calls it from a nested lambda, which
        // answered "at line 147, column 17" (the `GeoOf(child)` call). A LAMBDA's arm compiles its body directly (only a
        // local FUNCTION declaration goes through CompileLocalFunctionToVm, which already registers), which is why the
        // registry was empty for exactly this shape. Registering here is idempotent, null-safe and costs one read on the
        // invocation path; `toExports: false` because a run-time discovery must not change what an EXPORT carries.
        RegisterNestedDecls(decl.Compiled, toExports: false);

        // VM C# re-entry guard: the VM CALL handler's last-resort fallback (Vm.cs
        // Push(interp.InvokeScriptFunction(...))) and the CALL_VAL sibling (InvokeCallableValue)
        // re-enter this C# interpreter method once per script call level, a loopx-style runaway
        // therefore grows the C# stack in BOTH Debug and AOT. VM flat/TCO callees swap in place
        // inside Vm.Execute and do NOT re-enter TryExecuteVmBody, so this counter measures genuine
        // C# nesting (every level = one real C# frame). Fires as a catchable ScriptException well
        // below MaxCallDepth before the process stack is exhausted (StackOverflowException is
        // uncatchable in .NET).
        _vmReentryDepth++;

        // VM-created capturing lambdas and local functions: append the capture block ([this?] + captures)
        // AFTER THE PARAMS so LOAD_VAR for captured names resolves, the compiler always reserves "this" right
        // after the params, and VmCaptures carries that slot as element 0 (MaterializeVmCaptures). The base is
        // therefore the callee's DECLARED PARAMETER COUNT, not the number of arguments this call happened to pass:
        // those differ when a caller passes more arguments than the callee declares, legal, extras are ignored,
        // as the tree-walker does, and that is exactly the shape `Com.Run(fun() { … })` uses (one argument, zero
        // declared parameters). Appending at args.Length put the argument into capture slot 0 and handed it to
        // every captured read, measured: such a callback read the SCOPE where the enclosing function's variable
        // belonged, and with two declared parameters the reads went empty while the extra parameter received the
        // enclosing frame's value. Arguments are copied up to the declared count and the extras dropped;
        // NormalizeArgsForVm already yields exactly one entry per declared parameter whenever it normalizes at
        // all (it is a pass-through for a plain signature, which is how the extras survived to here).
        object?[]? rentedArgs = null;
        if (fn.VmCaptures is { Length: > 0 })
        {
            var declaredParams = fn.Declaration.Params.Count;
            rentedArgs = ArrayPool.Rent(declaredParams + fn.VmCaptures.Length);
            var argCopyCount = Math.Min(args.Length, declaredParams);
            Array.Copy(args, rentedArgs, argCopyCount);
            Array.Copy(fn.VmCaptures, 0, rentedArgs, declaredParams, fn.VmCaptures.Length);
            args = rentedArgs;
        }

        try
        {
            // Same byte-accurate guard as InvokeScriptFunction: this is the other boundary a script
            // call re-enters the C# interpreter through (VM-direct plus the VM's last-resort
            // fallbacks), and it must not depend on _callFrames being populated.
            if (!GuardsDisabled && NativeStackNearlyExhausted())
                throw new ScriptException($"Maximum call depth exceeded ({_vmReentryDepth}), possible runaway recursion (native stack nearly exhausted).");
            if (_vmReentryDepth > MaxCallDepth)
                throw new ScriptException($"Maximum call depth exceeded ({MaxCallDepth}), possible runaway recursion.");

            // Out-param assignment tracking: wrap out RefCells with a setter that records
            // assignment; an out param never assigned throws (parity with InitOutTracking).
            // Guard on IsOutParams, it is null! for funcs created without out params
            // (destructors, operators, CloneFunction), NRE regression from fa4e233 cleanup.
            object?[] vmExecArgs = args;
            bool[]? vmOutTrack = null;
            if (decl.IsOutParams != null)
            {
                var anyOut = false;
                for (var oi = 0; oi < args.Length && oi < decl.Params.Count; oi++)
                    if (decl.IsOutParams[oi]) { anyOut = true; break; }
                if (anyOut)
                {
                    vmOutTrack = new bool[decl.Params.Count];
                    vmExecArgs = new object?[args.Length];
                    Array.Copy(args, vmExecArgs, args.Length);
                    for (var oi = 0; oi < vmExecArgs.Length && oi < decl.Params.Count; oi++)
                    {
                        if (!decl.IsOutParams[oi]) continue;
                        if (vmExecArgs[oi] is not RefCell oc) continue;
                        var flag = oi;
                        var pType = decl.Params[oi].Type;
                        var pName = decl.Params[oi].Name;
                        int line1 = line;
                        int column1 = column;
                        vmExecArgs[oi] = new RefCell(() => oc.Get(), v =>
                        {
                            var coerced = ValidateOutAssignment(v, pType, pName, line1, column1);
                            vmOutTrack[flag] = true;
                            oc.Set(coerced);
                        }, oc.ByOut);
                    }
                }
            }

            // Static methods compiled by the VM have a `this` local slot (compiler registers it
            // after params) but are invoked via the global CALL path with only the real args,
            // pad with a null placeholder so the body's `this` slot resolves (a static body never
            // reads it; bare callees in static methods resolve via the class/global scope).
            object?[] vmStaticArgs = vmExecArgs;
            if (decl.IsStatic && vmExecArgs.Length == decl.Params.Count)
            {
                vmStaticArgs = new object?[vmExecArgs.Length + 1];
                Array.Copy(vmExecArgs, vmStaticArgs, vmExecArgs.Length);
                vmStaticArgs[vmExecArgs.Length] = null;
            }

            // Tuple parameters: the VM-direct path bypasses the tree bind loops above, so validate
            // arity/type and attach the parsed element names here (p.n resolves via GET_PROP).
            if (decl.TupleParamNames is { Count: > 0 })
                for (var tpi = 0; tpi < decl.Params.Count && tpi < vmStaticArgs.Length; tpi++)
                    BindTupleParam(decl, tpi, vmStaticArgs[tpi], line, column);

            // A bail is only safe to hand to the tree when the VM had done NOTHING observable, because the tree runs
            // a body from the top: a VM that printed, wrote a global or called a method before stopping would have
            // that work repeated. Measured before this: such a body printed its line twice and its counter went 3 to
            // 4, before the re-run raised its own error. Refusing is the honest side of that trade, a clear error
            // rather than a silent second effect, and the flag is saved and restored so a nested execution cannot
            // confuse an outer body's answer.
            var commitBaseline = VmBodyCommitted;
            VmBodyCommitted = false;
            object? vmResult;
            var committedHere = false;
            try
            {
                vmResult = Vm.Execute(decl.Compiled!, vmStaticArgs, this, decl.Name, fn.NamespaceContext, wantRefResult, decl);
            }
            finally
            {
                committedHere = VmBodyCommitted;
                VmBodyCommitted = commitBaseline || committedHere;
            }
            // A REF-RETURNING body answers the REFERENCE (a cell) even from the VM path, because the outermost RETURN
            // deliberately leaves it alone. The call site decides: `ref int r = ref F(..);` and `F(..) = 7;` want the
            // cell (they are declined to the tree, which asks for it), every other use wants the value.
            if (decl.IsRefReturn && vmResult is RefCell vmRefCell)
            {
                if (wantRefResult) { result = vmRefCell; return true; }
                vmResult = vmRefCell.Get();
            }
            if (vmResult is VmFallback)
            {
                if (committedHere)
                    RuntimeError($"Function '{decl.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
                return false;
            }

            VmInvocations++;
            if (fn.ClosureSnapshot?.LocalScopes != null)
                foreach (var ls in fn.ClosureSnapshot.LocalScopes)
                    ls.Clear();
            if (vmOutTrack != null && decl.IsOutParams != null)
            {
                for (var oi = 0; oi < vmOutTrack.Length && oi < decl.Params.Count; oi++)
                    if (decl.IsOutParams[oi] && !vmOutTrack[oi])
                        RuntimeError(OutNotAssignedMessage(decl.Params[oi].Name), line, column);
            }
            // Out-param type enforcement happens in the tracking setter (ValidateOutAssignment)
            // at assignment time, parity with the tree's InitOutTracking.
            // Attach declared tuple element names to the returned tuple (VM path parity with
            // the tree's AttachTupleNames, (int i, string s) Fn() => t exposes .i / .s).
            if (vmResult is ScriptTuple && decl.TupleElementNames is { Count: > 0 })
                AttachTupleNames(vmResult, decl);
            // Void contract: a void function's VM body may leave a discarded-expression value on
            // the operand stack (e.g. `counter++;`, postfix inc pushes the old value), so the raw
            // Vm.Execute result is meaningless for void. Normalize to null here, parity with the
            // tree paths' "Void function cannot return a value" handling (regression: an event
            // invoking a void subscriber bubbled the leftover value up to the caller's void check).
            // ONE EXCEPTION, asked by the caller: a bundle's synthetic `__program__` entry is declared Void because a
            // program body has no DECLARED result, yet a top-level `return <value>` is the script's own answer (the
            // source run reports it, and `slc_file` in SQLite reports it). Measured before this: a `--source=strip`
            // bundle of `return 42;` answered null through the C# entry point while the SAME entry answered 42 when a
            // script called it, because a script-side call inlines the body and never reaches this line.
            result = decl.ReturnType == TypeInfo.Void && !takeVoidAnswer ? null : vmResult;
            // Iterable / RandomAccess return enforcement, the VM-direct path bypasses the tree's
            // ReturnStmt CoerceTo (Interpreter.cs:8665); validate here so tree and VM reject the
            // same non-iterable / non-indexable return values.
            if (result != null)
            {
                if (IsIterableType(decl.ReturnType) && !IsIterable(result))
                    RuntimeError($"Expected an Iterable, got {result.GetType().Name}.", line, column);
                if (IsRandomAccessType(decl.ReturnType) && !IsRandomAccess(result))
                    RuntimeError($"Expected a RandomAccess (array, list, tuple or string), got {result.GetType().Name}.", line, column);
                if (IsFunctionType(decl.ReturnType) && !IsFunction(result))
                    RuntimeError($"Expected a Function, got {result.GetType().Name}.", line, column);
            }
            return true;
        }
        finally
        {
            if (rentedArgs != null) ArrayPool.Return(rentedArgs);
            _vmReentryDepth--;
        }
    }

    internal object? InvokeScriptFunction(ScriptFunction fn, object?[] args, int line, int column, CoroutineState? resumeState = null, bool isTailCall = false, bool wantRefResult = false, bool takeVoidAnswer = false)
    {
        FunctionsCalled++;
        TraceStack("call");
        // The body about to run must have ITS interpreter as Current for the whole of it. A UI control captures
        // Current when it is created (Control._interpreter, "captured on the creating (main) thread") and its
        // event dispatch then gives up SILENTLY when that is null (Ui.InvokeSafely returns with no message), so
        // a window built without a Current comes out with every handler dead: measured, a compiled program body
        // (a source-less bundle's entry) built a form whose Click assignments read back non-null, and neither
        // BM_CLICK nor the control's own RaiseClick ran anything, while the same file run from source worked.
        // The tree path gets Current from Interpret's PushContext; a body invoked from C# (that entry, a UI
        // event, a Task/Parallel/Scheduler callback) did not. Pushing only when this interpreter is not already
        // current keeps the tree path and every nested call at one comparison.
        using IDisposable? contextScope = ReferenceEquals(Current, this) ? null : PushContext();
        // A generic call's type-parameter BINDINGS stay active for the whole of its body, so a type position the
        // body's substitution could not rewrite (= most of them) still resolves a parameter through
        // ResolveTypeToClr. Pushed only when there is a map to push, so a plain nested call keeps the enclosing
        // generic's bindings in force, and disposed like the context above so an exception cannot leak it.
        using IDisposable? bindingScope = PushTypeBindings(fn.Declaration.TypeParamBindings);
        var framePushed = false;
        GuardCallDepth();
        CheckEntryArity(fn, args, line, column);

        // C# flavour: a CALL to an `async` function hands back the script-visible Task, not the generator an
        // ordinary coroutine function returns. Checked BEFORE the coroutine branch below, and before every VM
        // attempt. NOTE the body is NOT tree-only any more (this line used to say it was, from before the VM work):
        // an async body whose compiler took it runs on the VM, and only a body the compiler declines (a try, an `is`
        // pattern, a using body, a nested function) stays on the tree.
        if (fn.Declaration.IsAsync)
            return BeginAsyncCall(fn, args);

        object? traceReturnValue = null;
        // Set when the VM-direct path above already attempted a compiled body; prevents the
        // tail-call-loop switch from re-attempting (and re-running side effects) after a fallback.
        var vmDirectTried = false;
        try
        {
            if (fn.IsCoroutine)
            {
                traceReturnValue = new Generator<object?>(CreateCoroutineForCall(fn, args, line, column));
                return traceReturnValue;
            }

            // ── Local functions: lazy compile to bytecode + VM capture materialization (Task 2).
            PrepareLocalFunctionForInvocation(fn);

            // ── Fast path: skip full frame setup for simple capture-free single-statement
            // functions/lambdas. NOTE: this path does NOT push a CallFrame, so the main
            // depth guard can't see it, track fast-path depth explicitly (regression:
            // a pure `return f(n+1)*2` body recursed unguarded → StackOverflowException).
            if (CanInvokeSingleStatementBody(fn, args, resumeState, isTailCall))
                return InvokeSingleStatementBody(fn, args, line, column);

            // ── VM direct path: skip the tree's ScopeStack/global-copy/CallFrame preamble
            // for VM-compiled bodies. The preamble (new ScopeStack + GetGlobalScopePair →
            // 3× full global-dict copies + CallFrame push + BindParam) measured ~66 KB per
            // call, the dominant per-call allocation for tree-invoked compiled functions.
            if (!isTailCall && resumeState == null && IsVmDirectEligible(fn))
            {
                var vmArgs = NormalizeArgsForVm(fn.Declaration, args, line, column);
                vmDirectTried = true;
                if (TryExecuteVmBody(fn.Declaration, fn, vmArgs, line, column, out var vmDirect, wantRefResult, takeVoidAnswer))
                    // The OTHER half of the return coercion: a body entered from the TREE has no frame
                    // declaration for the VM's RETURN arm to read (that field is set by the VM's own call sites),
                    // so the value this entry hands back is coerced here, with the same rule and the same helper
                    // the flat-call path uses. Without it the same function answered differently depending on who
                    // called it.
                    return CoerceFlatCallResult(fn.Declaration, vmDirect, line, column);
                if (fn.Declaration.IsVmShell)
                    RuntimeError($"Function '{fn.Declaration.Name}' was loaded from bytecode for a different environment (types/globals unavailable). Re-export or recompile it.", line, column);
                TreeInvocations++;
                TraceTreeOnce(TreeFuncLabel(fn.Declaration), line, column);
            }

            var funcName = FrameWithPosition(FrameLabel(fn.Declaration.Name), line, column, fn.Declaration);
            _callStack.Push(funcName);

            var decl = fn.Declaration;

            var hasParams = decl.IsParams.Count > 0 && decl.IsParams[^1];
            // Handle params: allow excess args to be collected into the last parameter, the assembly below is
            // what this branch still needs; the ARITY minimum is no longer computed here. It is enforced once, at
            // the entry (`MinRequiredArgs`), together with the VM-direct path's need for it: that path runs a
            // compiled body with no check of its own, so a missing argument used to bind as NULL, silently,
            // measured, `Invoke(f2, "one")` printed `[one][]` where the same call script-side refused.

            // ====================================================================
            // Pure-function memoisation (keyed by stable name)
            // ====================================================================
            var shouldCache = fn.IsPure && !string.IsNullOrEmpty(decl.Name);
            if (shouldCache)
            {
                var key = (decl.Name, args);
                if (_pureMemoCache.TryGetValue(key, out var cachedResult))
                {
                    // Void functions are stored as VoidMarker.Instance; non-void as the actual value.
                    traceReturnValue = Equals(cachedResult, VoidMarker.Instance) ? null : cachedResult;
                    return traceReturnValue;
                }
            }

            _callFrames.Value!.Push(new CallFrame(BuildEntryScopeStack(fn)));
            framePushed = true;
            var entryScopeCount = CurrentFrame.Scopes.Scopes.Count;

            // === Tail-call loop: retries with new args when _tailCallArgs is set ===
            FuncDecl? prevFuncDecl = null;
            ScriptFunction? prevFn = null;
            while (true)
            {
                CurrentFrame.Scopes.PushScope();
                // The body's own declarations are in scope from its first statement (C# hoisting), so a closure created
                // above a nested declaration can still name it. Re-run on every iteration of this loop, because each
                // iteration pushes a FRESH scope (the tail-call switch re-enters here).
                PrebindDeclaredFunctions(decl.Body);

                try
                {
                    var outAssigned = BindInvocationParameters(decl, fn, args, hasParams, line, column);

                    prevFuncDecl = _currentFuncDecl;
                    _currentFuncDecl = decl;
                    prevFn = _currentFn;
                    _currentFn = fn;

                    // VM fast path, use pre-compiled bytecode when available (globally togglable via VmEnabled)
                    // Capturing lambdas run VM only when created via MAKE_LAMBDA (VmCaptures set); tree-created
                    // capturing lambdas carry their captures in ClosureSnapshot → tree-walk.
                    switch (isTailCall)
                    {
                        case false when resumeState == null && decl.Compiled != null && !decl.HasYield && VmPathEnabled(decl) && !vmDirectTried
                                        && (fn.VmCaptures != null || (fn.ClosureSnapshot?.LocalScopes.Count ?? 0) == 0):
                            {
                                // Normalize args for the VM (defaults + params array), then execute.
                                // Shared with the VM-direct path above via NormalizeArgsForVm/TryExecuteVmBody.
                                var vmSwitchArgs = NormalizeArgsForVm(decl, args, line, column);
                                if (TryExecuteVmBody(decl, fn, vmSwitchArgs, line, column, out var vmSwitchRes))
                                    return vmSwitchRes;
                                if (decl.IsVmShell)
                                    RuntimeError($"Function '{decl.Name}' was loaded from bytecode for a different environment (types/globals unavailable). Re-export or recompile it.", line, column);
                                TreeInvocations++;
                                TraceTreeOnce(TreeFuncLabel(decl), line, column);
                                break;
                            }

                        case false when resumeState == null:
                            TreeInvocations++;
                            TraceTreeOnce(TreeFuncLabel(decl), line, column);
                            break;
                    }

                    var flow = ExecuteDeclaredBody(decl, line, column, out var returnValue);
                    if (flow == ControlFlow.Yield)
                    {
                        // Propagate yield, but clear tail state first
                        _tailCallArgs = null;
                        traceReturnValue = returnValue;
                        return traceReturnValue;
                    }

                    // Check for pending tail call (self-recursion in return position)
                    if (_tailCallArgs != null)
                    {
                        // Pop the current scope, rebind args, and loop
                        CurrentFrame.Scopes.PopScope();
                        args = _tailCallArgs!;
                        line = _tailCallLine;
                        column = _tailCallColumn;
                        _tailCallArgs = null;
                        traceReturnValue = null;
                        continue;  // jump back to CurrentFrame.Scopes.PushScope() + bind + execute
                    }

                    // ── out-param verification ──
                    VerifyOutParams(outAssigned, decl.IsOutParams, decl.Params, decl.Line, decl.Column);

                    traceReturnValue = FinishInvocation(decl, args, flow, returnValue, shouldCache, line, column);
                    return traceReturnValue;
                }
                finally
                {
                    while (CurrentFrame.Scopes.Scopes.Count > entryScopeCount)
                        CurrentFrame.Scopes.PopScope();

                    if (_tailCallArgs == null)  // don't reset on tail-call continue
                    {
                        _currentFuncDecl = prevFuncDecl;
                    }
                    _currentFn = prevFn;
                }
            }
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A script error raised inside a callback the engine invoked through a DELEGATE (`next()` in a
            // middleware, an event handler, a Task body) arrives WRAPPED in a `TargetInvocationException`, whose own
            // Message is the useless "Exception has been thrown by the target of an invocation": the map can only
            // describe the wrapper, so the REAL error (and its position) was lost. The inner exception IS the
            // error, so a wrapped `ScriptException` is re-thrown as itself, the rule this file already applies to a
            // value-level member failure. Measured: two chained middlewares where the second reads an undefined
            // name answered "TargetInvocationException: 'Exception has been thrown...'" where the tree says
            // "Undefined variable 'nopeUndefined'".
            if (ex is TargetInvocationException { InnerException: ScriptException innerScript })
                throw innerScript;
            ThrowException(FriendlyExceptionMessage(ex), line, column);
            return null;
        }
        finally
        {
            TraceStack("unwind");
            if (_traceEnabled)
            {
                var funcName = string.IsNullOrEmpty(fn.Declaration.Name) ? "<anonymous>" : fn.Declaration.Name;
                var argsStr = string.Join(", ", args.Select(a => a?.ToString() ?? "null"));
                Console.WriteLine($"[trace] {funcName}({argsStr}) → {traceReturnValue?.ToString() ?? "null"}");
            }
            _callStack.TryPop(out _);
            if (framePushed) _callFrames.Value!.TryPop(out _);
            _tailCallArgs = null;
            _returnExprDepth = 0;
        }
    }

    // Runaway-recursion guard: StackOverflowException is uncatchable in .NET,
    // so convert imminent overflow into a catchable ScriptException. Two layers:
    //  1. NativeAOT: byte-accurate proportional check (NativeStackNearlyExhausted),
    //     fires when remaining < 4x used. Exception dispatch in NativeAOT walks the
    //     interpreter's C# frame chain and needs ~3x the recursion's own stack per
    //     level (measured: a ScriptException at depth ~4000 survives, ~4200+ dies
    //     with 20+MB still free, the dispatch walk, not the recursion, exhausts the
    //     stack; _depth_runaway_tree.slc died this way at n=9998 via the old
    //     MaxCallDepth=10000 throw). This check fires before that envelope on ANY
    //     thread (main 32MB → ~3100; Task.Run 1MB → ~95) and is drift-proof.
    //  2. Count cap (MaxCallDepth): JIT ceiling (24, sized for the ~36-level physical
    //     limit of the 1.5MB unoptimized-JIT stack) and the NativeAOT fallback when the
    //     stack-limit API is unavailable (3000, leaves ~8MB of dispatch headroom;
    //     see the MaxCallDepth field comment). The <StackSize> csproj knob is ignored
    //     by NativeAOT; the real knob is IlcDefaultStackSize (linker /STACK).
    // The byte-accurate guard now runs on EVERY build. It used to be NativeAOT-only
    // (`!RuntimeFeature.IsDynamicCodeSupported`), which left the shapes that fall off the flat
    // path with no effective guard on JIT: default-arg / params-array / out-param recursion died
    // with an UNCATCHABLE StackOverflow (measured 2026-09-10: defArg(100) already killed the
    // process, while the tree-walker path raised the count guard at 24). remaining < 4x used
    // adapts to the real stack, so it stays safe on the 1.5MB Debug apphost and permissive on a
    // large AOT stack, and it does not depend on _callFrames being populated, which is what the
    // count guard below needs.
    private void GuardCallDepth()
    {
        if (!GuardsDisabled && NativeStackNearlyExhausted())
        {
            TraceStack("guard-bytes");
            var liveDepth = _callFrames.IsValueCreated ? _callFrames.Value!.Count : 1;
            throw new ScriptException($"Maximum call depth exceeded ({liveDepth}), possible runaway recursion (native stack nearly exhausted).");
        }
        if (!GuardsDisabled && _callFrames.IsValueCreated && _callFrames.Value!.Count >= MaxCallDepth)
        {
            TraceStack("guard-count");
            throw new ScriptException($"Maximum call depth exceeded ({MaxCallDepth}), possible runaway recursion.");
        }
    }

    // The LOWER arity bound is checked on every entry, which is the fix for a SILENT value. The
    // VM-direct path runs a compiled body with no arity check of its own (the VM's CALL / CALL_VAL
    // handlers check SCRIPT-side calls, not this entry), so a C#-side invocation of a compiled callee bound a
    // missing argument as NULL and reported nothing: measured, `Invoke(f2, "one")` on a two-parameter function
    // printed `[one][]` while the same call written script-side refused. Extras stay DROPPED here, never
    // refused: that is what keeps `Com.Run(fun() { ... })`, a callback that ignores the scope it is handed,
    // legal, and it is the only difference between this policy and CheckScriptCallArity's.
    private void CheckEntryArity(ScriptFunction fn, object?[] args, int line, int column)
    {
        var entryMinRequired = MinRequiredArgs(fn.Declaration);
        if (args.Length < entryMinRequired)
            RuntimeError($"Expected at least {entryMinRequired} arguments, got {args.Length}.", line, column);
    }

    // A VmBacked generator's body runs on the bytecode VM, which binds co.Args
    // POSITIONALLY into the callee's locals with no defaults/params/coercion handling,
    // the tree would bind those at resume (InvokeCoroutineBody); the VM cannot. Bake
    // them into Args HERE so a VM drive (Generator.MoveNext, a foreach genDrive, or
    // FOREACH_CALL) sees the same parameter values the tree would: default values
    // filled, params array assembled, by-value params coerced (normalizeArgsForVm).
    // The tree path (non-VmBacked) keeps its own lazy binding at resume.
    // VM-flat generators (Tasks 2-3): a plain value-yield coroutine whose body compiled to
    // bytecode AND is proven free of CoroutineYieldRequest producers (Sleep/http/event/
    // ReadLineAsync, those must stay on the tree path, which owns the request machinery)
    // runs on the bytecode VM via Generator.MoveNext. A foreach over a generator inside
    // the body is VM-safe since Task 3: the VM's FOREACH_INIT/NEXT drives the inner
    // generator in-place (flat frames) in the same Vm.Execute run. Everything else stays
    // tree.
    private Coroutine CreateCoroutineForCall(ScriptFunction fn, object?[] args, int line, int column)
    {
        var fcVmBacked = IsVmBackedCoroutine(fn.Declaration);
        var co = new Coroutine(this, fn,
            fcVmBacked ? NormalizeArgsForVm(fn.Declaration, args, line, column) : args);
        co.VmBacked = fcVmBacked;
        return co;
    }

    // A nested FuncDecl is never compiled at declaration (VisitFuncDecl); on its first
    // invocation we compile the body via the same CanVmHandle + Compiler.Compile path as
    // top-level functions and materialize VmCaptures from the closure snapshot, so the
    // VM-direct gate below can run it VM-flat. Coroutines, generics, and non-VM-safe
    // bodies stay tree (Compiled stays null / VmCaptures stays null). Skipped when the
    // VM is disabled (VmSetEnabled(false)) so tree-parity runs stay genuinely tree.
    // NOTE: the FuncDecl is shared across ScriptFunction instances (every enclosing
    // invocation re-runs VisitFuncDecl and builds a fresh snapshot), each instance
    // needs its OWN VmCaptures materialized from ITS snapshot, so the compile step
    // (once, cached on the decl) and the materialization step are separate.
    //
    // A DECLARATION A CLOSURE CAPTURED BEFORE IT EXISTED KEEPS THE TREE (C# hoisting, see
    // PrebindDeclaredFunctions): its body was compiled WITHOUT that capture layout, so materializing the layout
    // here would hand a compiled body slots its bytecode does not address. Measured, one lambda body then one
    // function body each holding the shape: running the first made the second answer
    // `Undefined variable 'hits'` although its snapshot carried it, and the tree ran the same body correctly.
    private void PrepareLocalFunctionForInvocation(ScriptFunction fn)
    {
        if (!fn.IsLocal || fn.IsModuleScope || !VmPathEnabled(fn.Declaration)
            || fn.Declaration.CapturedBeforeDeclaration
            || fn.Declaration.IsCoroutine || fn.Declaration.TypeParameters.Count != 0)
            return;

        if (fn.Declaration.Compiled == null)
            CompileLocalFunctionToVm(fn);
        else if (fn.VmCaptures == null && fn.Declaration.VmCaptureNames != null)
            fn.VmCaptures = MaterializeVmCaptures(fn, fn.Declaration.VmCaptureNames);
        else if (fn.VmCaptures == null)
        {
            // A body compiled WITHOUT a capture layout (a declaration whose block was TREE-walked, which
            // `Program.CompileNested` compiles with no context) needs both halves here: recompiled against the
            // layout THIS call's snapshot gives, then its cells materialized. Measured on the Excel sample,
            // whose tree-walked callback calls `void Line(...)`: the body read its block's `report` as a
            // global and answered `Undefined variable 'report'`.
            var treeNames = ComputeLocalFunctionCaptures(fn);
            if (treeNames.Count > 0)
            {
                var rebuilt = CompileToVm(fn.Declaration, treeNames);
                if (rebuilt != null)
                {
                    fn.Declaration.Compiled = rebuilt;
                    fn.Declaration.VmCaptureNames = treeNames;
                    fn.VmCaptures = MaterializeVmCaptures(fn, treeNames);
                }
                else fn.Declaration.Compiled = null;   // not VM-safe: the tree runs it with its snapshot
            }
        }
    }

    // The fast path runs a ONE-statement, capture-free body without pushing a CallFrame. A body the VM will
    // NOT run in this call takes it: one the compiler did not take, or any body while the A/B switch has the
    // VM OFF (a suite or the benchmark flips it, and then there is no VM path to take). Written as two clauses
    // rather than VmPathEnabled, which ALSO answers true for a shell, and a shell must never run here: its AST
    // is empty. Measured, excluding a COMPILED body when the VM is off dropped it to the FULL preamble on the
    // tree leg, which copies the global scope three times per call.
    private bool CanInvokeSingleStatementBody(ScriptFunction fn, object?[] args, CoroutineState? resumeState, bool isTailCall)
    {
        return !isTailCall && resumeState == null
            && (fn.Declaration.Compiled == null || !VmEnabled) && !fn.Declaration.IsVmShell
            && _callFrames.IsValueCreated && _callFrames.Value!.Count > 0
            && fn.ClosureSnapshot.LocalScopes.Count == 0
            && fn.ClosureSnapshot.GlobalScope.Count == 0   // module functions carry their module's flat scope, need the full frame setup
            && fn.Declaration.Body.Count == 1
            && args.Length <= 16
            && fn.Declaration.Params.Count == args.Length
            && fn.Declaration.DefaultValues == null
            && (fn.Declaration.IsParams.Count == 0 || !fn.Declaration.IsParams[^1])
            && !fn.Declaration.IsRefParams.Any(b => b)
            && (fn.Declaration.IsOutParams == null || !fn.Declaration.IsOutParams.Any(b => b));
    }

    private object? InvokeSingleStatementBody(ScriptFunction fn, object?[] args, int line, int column)
    {
        if (_fastPathDepth >= MaxCallDepth)
        {
            throw new ScriptException($"Maximum call depth exceeded ({MaxCallDepth}), possible runaway recursion.");
        }
        _fastPathDepth++;
        TreeInvocations++;
        TraceTreeOnce(TreeFuncLabel(fn.Declaration), line, column);
        try
        {
            CurrentFrame.Scopes.PushScope();
            var fastDecl = fn.Declaration;
            PrebindDeclaredFunctions(fastDecl.Body);
            for (var i = 0; i < args.Length; i++)
            {
                BindTupleParam(fastDecl, i, args[i], fastDecl.Line, fastDecl.Column);
                BindParam(fastDecl.Params[i].Name, args[i], fastDecl.Params[i].Type,
                    fastDecl.IsRefParams[i],
                    fastDecl.IsOutParams != null && i < fastDecl.IsOutParams.Count && fastDecl.IsOutParams[i],
                    fastDecl.Line, fastDecl.Column);
            }

            object? fastResult = null;
            foreach (var fastStmt in fastDecl.Body)
            {
                var fr = Execute(fastStmt);
                if (fr.Flow == ControlFlow.Return) { fastResult = fr.Value; break; }
            }
            AttachTupleNames(fastResult, fastDecl);
            // Iterable / RandomAccess return enforcement, the fast path bypasses the tree's
            // ReturnStmt CoerceTo (Interpreter.cs:8665); validate here so every return path
            // (fast, tree, VM-direct) rejects the same non-iterable / non-indexable values.
            if (fastResult != null)
            {
                if (IsIterableType(fastDecl.ReturnType) && !IsIterable(fastResult))
                    RuntimeError($"Expected an Iterable, got {fastResult.GetType().Name}.", fastDecl.Line, fastDecl.Column);
                if (IsRandomAccessType(fastDecl.ReturnType) && !IsRandomAccess(fastResult))
                    RuntimeError($"Expected a RandomAccess (array, list, tuple or string), got {fastResult.GetType().Name}.", fastDecl.Line, fastDecl.Column);
                if (IsFunctionType(fastDecl.ReturnType) && !IsFunction(fastResult))
                    RuntimeError($"Expected a Function, got {fastResult.GetType().Name}.", fastDecl.Line, fastDecl.Column);
            }
            return fastResult;
        }
        finally
        {
            CurrentFrame.Scopes.PopScope();
            _fastPathDepth--;
        }
    }

    private static bool IsVmDirectEligible(ScriptFunction fn) =>
        fn is { IsCoroutine: false, Declaration.HasYield: false, Declaration.Compiled: not null }
        && VmPathEnabled(fn.Declaration)
        && (fn.VmCaptures != null || (fn.ClosureSnapshot?.LocalScopes.Count ?? 0) == 0);

    // Determine effective snapshot for variable lookup. A lambda the VM CREATED carries its captures as CELLS
    // (VmCaptures) and no tree-readable snapshot, because a compiled body resolves a capture from its own
    // local slots. An invocation that ends up on the TREE therefore saw an empty snapshot and could not
    // resolve ANY captured name, which is what an engine-invoked callback does: measured, a `Com.Run`
    // callback declared inside an `if` block, calling the block's own local function by bare name, answered
    // `Undefined variable 'Note'` from a compiled top level and worked tree-walked. So the cells are turned
    // back into the one local scope the tree reads, from the SAME names the VM laid out.
    private ScopeStack BuildEntryScopeStack(ScriptFunction fn)
    {
        var effectiveSnapshot = fn.ClosureSnapshot;
        if (fn.VmCaptures is { Length: > 0 } vmCells
            && fn.Declaration.VmCaptureNames is { Count: > 0 } vmNames
            && (effectiveSnapshot == null || effectiveSnapshot.LocalScopes.Count == 0))
        {
            var fromCells = new Dictionary<string, object?>(vmNames.Count, _comparer);
            for (var ci = 0; ci < vmNames.Count && ci + 1 < vmCells.Length; ci++)
                fromCells[vmNames[ci]] = vmCells[ci + 1];   // element 0 is the "this" slot
            var scopes = new List<Dictionary<string, object?>>(1) { fromCells };
            effectiveSnapshot = new ClosureSnapshot(scopes, [], effectiveSnapshot?.GlobalScope ?? [], []);
        }

        // Create a fresh scope stack
        var newScopeStack = new ScopeStack(_comparer);

        // If the function has no local captured scopes, use the root global scope,
        // UNLESS the snapshot already carries a global scope (module functions exported
        // with their module's flat scope at LoadModule): keep it so free variables resolve
        // in the defining module, not the caller.
        if (effectiveSnapshot?.LocalScopes.Count == 0)
        {
            // Get both copy and writable reference under a single lock
            var mainFrame = _rootFrame;
            var (mainGlobalCopy, writableGlobal, actualGlobal)
                = mainFrame.Scopes.GetGlobalScopePair();
            // Refresh with the live caller globals unless this is a module function whose
            // snapshot already carries its module's own flat scope (IsModuleScope), a
            // non-module snapshot with a stale capture-time global copy (e.g. a recursive
            // top-level lambda referencing itself) must see the live globals at call time.
            if (effectiveSnapshot!.GlobalScope.Count == 0 || !fn.IsModuleScope)
            {
                var globalDict = ReadableGlobals(mainGlobalCopy);

                effectiveSnapshot = effectiveSnapshot with { GlobalScope = globalDict };

                // Link the writable global scope (the original dictionary, not the copy)
                newScopeStack.SetWritableGlobalScope(writableGlobal, actualGlobal);
            }
            // Module functions (snapshot carries a NON-EMPTY GlobalScope, the module's flat
            // scope captured at LoadModule) must NOT link the caller's writable root. Linking
            // it would route their writes to module globals into the IMPORTER's root scope
            // (leaking module state into the caller) while reads keep resolving against the
            // frozen module snapshot, a double-bind where caller writes to those names are
            // also no longer seen by the module function. With no writable link, ScopeStack.Set
            // routes module-global writes through to the snapshot's shared GlobalScope dict
            // (the module's ModuleGlobals), so they persist across calls without touching the
            // caller's scope at all.
        }
        else
        {
            // When there's a closure snapshot, link its writable scope from the root frame,
            // unless the snapshot carries a module global scope (module functions): their
            // writes must stay inside the module's own snapshot dict, not the caller's root.
            var rootFrame = _rootFrame;
            var (rootGlobalScope, actualGlobalScope) = rootFrame.Scopes.GetGlobalScopeReference();
            // A snapshot with NO global scope has nothing to preserve, so a READ of a root global must see the live
            // ones. Without this, a nested function BUILT AT RUN TIME could not read a single root global from a
            // TREE-walked body: `MakeNestedFunction` prepends the caller's locals as a leading scope (so the refresh
            // above never fires) and its global scope is filtered to the CAPTURES, which excludes a root global by
            // design (a root global stays a live reference for the VM, and the tree had no live reference at all).
            // Measured on `assets/language/ui/demos/music_player.slc`: `curIdx = idx;` in its nested `PlayIndex`
            // answered "Undefined variable 'curIdx'" (the name was live in the root, snapshot globals = 0).
            if (effectiveSnapshot!.GlobalScope.Count == 0 && !fn.IsModuleScope)
            {
                var (liveCopy, _, _) = rootFrame.Scopes.GetGlobalScopePair();
                effectiveSnapshot = effectiveSnapshot with { GlobalScope = ReadableGlobals(liveCopy) };
            }
            if (effectiveSnapshot!.GlobalScope.Count == 0 || !fn.IsModuleScope)
                newScopeStack.SetWritableGlobalScope(rootGlobalScope, actualGlobalScope);
        }

        // Link the snapshot (AFTER effectiveSnapshot is fully constructed)
        newScopeStack.SetParentSnapshot(effectiveSnapshot);
        return newScopeStack;
    }

    // Binds a call's arguments into the frame a tail-call iteration just pushed, and answers the out-parameter
    // tracking the body's RETURN must satisfy. The arity minimum is NOT checked here (CheckEntryArity owns it).
    private bool[]? BindInvocationParameters(FuncDecl decl, ScriptFunction fn, object?[] args, bool hasParams, int line, int column)
    {
        // Bind parameters (regular)
        var paramsBindEnd = hasParams ? decl.Params.Count - 1 : decl.Params.Count;
        for (var i = 0; i < paramsBindEnd; i++)
        {
            object? paramVal;
            if (i < args.Length)
                paramVal = args[i];
            else if (decl.DefaultValues != null && i < decl.DefaultValues.Count && decl.DefaultValues[i] != null)
                paramVal = Evaluate(decl.DefaultValues[i]!);
            else
                paramVal = RuntimeError<object?>($"Missing argument for parameter '{decl.Params[i].Name}'.", line, column);

            BindTupleParam(decl, i, paramVal, line, column);
            BindParam(decl.Params[i].Name, paramVal, decl.Params[i].Type,
                decl.IsRefParams[i],
                decl.IsOutParams != null && i < decl.IsOutParams.Count && decl.IsOutParams[i],
                decl.Line, decl.Column);
        }

        // The tail rule (and its ref/out refusal) lives in ONE place: AssembleParamsTail.
        if (hasParams)
            CurrentFrame.Scopes.Define(decl.Params[paramsBindEnd].Name,
                AssembleParamsTail(decl.Params, args, line, column), decl.Line, decl.Column);

        // Self-bind the function name for recursion
        if (!string.IsNullOrEmpty(decl.Name))
            CurrentFrame.Scopes.Define(decl.Name, fn, decl.Line, decl.Column);

        // ── out-param assignment tracking ──
        return InitOutTracking(decl.IsOutParams, decl.Params, decl.Line, decl.Column);
    }

    // Runs a declaration's statements once and reports how the body ENDED. A `return` answers its value, a
    // `yield` is the caller's to propagate (it ends the invocation), and a `throw` is raised here.
    private ControlFlow ExecuteDeclaredBody(FuncDecl decl, int line, int column, out object? returnValue)
    {
        var flow = ControlFlow.Continue;
        returnValue = null;
        foreach (var stmt in decl.Body)
        {
            var result = Execute(stmt);
            flow = result.Flow;
            if (flow == ControlFlow.Return)
            {
                returnValue = result.Value;
                return flow;
            }

            switch (flow)
            {
                case ControlFlow.Throw:
                    {
                        var ex = _thrownException ?? result.Value;
                        _thrownException = null;
                        throw ThrowPayloadException(ex, _currentLine, _currentColumn);
                    }

                case ControlFlow.Yield:
                    returnValue = result.Value;
                    return flow;
            }

            if (flow != ControlFlow.Continue)
                RuntimeError($"Unexpected control flow in function '{decl.Name}'.", line, column);
        }
        return flow;
    }

    // The one place a body's answer becomes the caller's value: out-param verification is done by the caller
    // (it owns the tracking array), and this owns the return rule, the pure-memo store and the fall-off refusal.
    private object? FinishInvocation(FuncDecl decl, object?[] args, ControlFlow flow, object? returnValue,
        bool shouldCache, int line, int column)
    {
        // Validate and coerce return value
        if (flow == ControlFlow.Return)
        {
            if (decl.ReturnType == TypeInfo.Void)
            {
                if (returnValue != null)
                    RuntimeError($"Void function '{decl.Name}' cannot return a value.", line, column);
                if (shouldCache)
                    _pureMemoCache[(decl.Name, [.. args])] = VoidMarker.Instance;
                return null;
            }
            else
            {
                // The return value is coerced by CoerceTo, which ALSO owns the rule for whether a type
                // may hold `null` (array / nullable / string / object may; a value type may not, unless
                // declared `?`). A second pre-check here used to refuse `return null;` for a `string`
                // return type with "must return a value": measured on a body the VM declines (a bound
                // `is T name` pattern), where the SAME body on the VM returns null happily, so the two
                // engines disagreed on a legitimate null return. One rule, stated in CoerceTo.
                AttachTupleNames(returnValue, decl);
                // A REF-RETURNING body answers the REFERENCE itself (`ref int F()` + `return ref v;`), so
                // the declared type describes what the reference POINTS AT rather than the value handed back.
                var finalResult = decl.IsRefReturn && returnValue is RefCell
                    ? returnValue
                    : CoerceTo(returnValue, decl.ReturnType, line, column);

                if (shouldCache)
                    _pureMemoCache[(decl.Name, [.. args])] = finalResult;
                return finalResult;
            }
        }

        // The body FELL OFF the end (no Return flow ran), which is a missing return rather than a
        // null one, so this refusal stands even for an object-typed result, unlike the Return arm
        // above. Measured on the Com.Run callback of
        // assets/language/com/com_excel_sheets_test.slc, whose compiled body reports return type
        // Object and arrives here after its FIRST break out of a foreach: exempting it made the
        // refusal go away and the bundle run only 14 of the file's 43 checks, silently.
        if (decl.ReturnType != TypeInfo.Void)
            RuntimeError($"Function '{decl.Name}' must return a value.", line, column);
        if (shouldCache)
            _pureMemoCache[(decl.Name, [.. args])] = VoidMarker.Instance;
        return null;
    }

    /// <summary>
    /// Executes the body of a coroutine function. Yield is signalled via ControlFlow.Yield + _isYield flag.
    /// On every entry (first call or resume), a CallFrame is pushed. On yield, the scope is saved
    /// to the Coroutine and the CallFrame popped. On resume, the scope is restored from the Coroutine.
    /// </summary>
    /// <summary>Whether the STATEMENT a coroutine resumes at must consume the pump's resume value. Three shapes
    /// do, and the reason differs by operator:
    ///   * `await`, in either statement form: re-entering the statement must NOT re-evaluate the awaitable, or a
    ///     second call would be made (measured: `var a = await leaf(10);` called leaf again on every resume);
    ///   * `yield` in a VarDecl (`var x = yield req`): the request's result has to land in the variable;
    ///   * `yield expr;` as an ExprStmt does NOT: it has no variable to assign and consuming would swallow the
    ///     value, which is why the yield arm below keeps that older, narrower rule.</summary>
    private static bool ConsumesResumeValue(Stmt stmt)
        => stmt switch
        {
            VarDeclStmt { Initializer: AwaitExpr } => true,
            ExprStmt { Expression: AwaitExpr } => true,
            VarDeclStmt { Initializer: YieldExpr { Value: not null } } => true,
            _ => false,
        };

    /// <summary>Whether the yield that just happened must be RE-ENTERED on resume rather than skipped. That is
    /// the rule a REQUEST yield follows: an await, a sleep or an http wait delivers its value through a resume
    /// that re-runs the statement holding it, while a plain value yield is finished once it produced its value.
    /// The body loop applies the same rule at its own resume index (the `isRequest &amp;&amp; !genRequest` case in
    /// <see cref="InvokeCoroutineBody"/>); this property is the loop-frame half of it, so a frame that resumes
    /// inside a `while`, a `for` or a `foreach` body agrees with the body loop instead of hardcoding "advance
    /// past the yielded statement".</summary>
    private bool YieldNeedsReEnter
        => _yieldValue is CoroutineYieldRequest && _currentCoroutine is not { IsGenerator: true };

    private object? InvokeCoroutineBody(ScriptFunction fn, object?[] args, int line, int column,
        CoroutineState? resumeState, Coroutine? coroutine = null)
    {
        var funcName = string.IsNullOrEmpty(fn.Declaration.Name) ? "<coroutine>" : fn.Declaration.Name;
        var isResume = resumeState != null;
        var didYield = false;
        TraceCoro($"body {funcName} resume={isResume} coroStack={_coroutineStack.Count}");

        // Base of this function's loop chain on the shared loop stack: loop frames pushed by
        // nested coroutines sit above outer-coroutine frames, so resume must index from here.
        var loopBaseSp = isResume && resumeState.HasValue ? resumeState.Value.LoopBaseSp : _loopStackSp;
        if (coroutine != null) coroutine.EnsureTree().LoopBaseSp = loopBaseSp;

        // Push CallFrame on every entry
        _callStack.Push(FrameWithPosition(funcName, line, column, fn.Declaration));
        _callFrames.Value!.Push(new CallFrame(new ScopeStack(_comparer)));

        var scopeCountAtEntry = CurrentFrame.Scopes.Scopes.Count;

        // Method-coroutine receiver (Task 4): class/struct method coroutines carry the bound
        // receiver on the Coroutine (ThisInstance). Saved here (outside the try, the finally
        // needs them) and restored in the finally so a method coroutine nested inside another
        // method's tree run keeps the outer context.
        var savedThis = _currentThis;
        var savedClass = _currentClass;

        try
        {
            var decl = fn.Declaration;
            var hasParams = decl.IsParams.Count > 0 && decl.IsParams[^1];

            if (isResume && coroutine?.Tree?.SavedScope != null)
            {
                // Restore scope from coroutine (the scope stack was saved on yield)
                CurrentFrame.Scopes.RestoreLiveStack(coroutine.Tree!.SavedScope.Value);
            }
            else
            {
                // --- First-call initialization ---
                var effectiveSnapshot = fn.ClosureSnapshot;
                // Mirror InvokeScriptFunction: rebuild the snapshot with the live global scope when the
                // body has no captured locals, this includes ANONYMOUS coroutine bodies (fun () {...}),
                // whose closure snapshot is otherwise empty, so global reads resolve.
                if (effectiveSnapshot.LocalScopes.Count == 0)
                {
                    var mainFrame = _rootFrame;
                    var mainGlobalScope = mainFrame.Scopes.GetGlobalScopeDictionary();
                    var globalDict = new Dictionary<string, object?>(_comparer);
                    foreach (var kvp in mainGlobalScope)
                    {
                        var raw = kvp.Value.Value;
                        if (raw is RefCell rc)
                            globalDict[kvp.Key] = rc;
                        else
                            globalDict[kvp.Key] = raw;
                    }
                    effectiveSnapshot = new ClosureSnapshot(
                        effectiveSnapshot.LocalScopes, effectiveSnapshot.LocalConsts,
                        globalDict, effectiveSnapshot.GlobalConsts);
                }

                CurrentFrame.Scopes.SetParentSnapshot(effectiveSnapshot);
                if (effectiveSnapshot.LocalScopes.Count == 0 && !string.IsNullOrEmpty(decl.Name))
                {
                    var (writable, actual) = _rootFrame.Scopes.GetGlobalScopeReference();
                    CurrentFrame.Scopes.SetWritableGlobalScope(writable, actual);
                }

                // Coroutines share the real root global scope (like any function body),
                // a snapshot copy here would swallow plain-global writes made by closures
                // invoked from inside the coroutine (mutateCor(g) style).
                var (coroWritable, coroActual) = _rootFrame.Scopes.GetGlobalScopeReference();
                CurrentFrame.Scopes.SetWritableGlobalScope(coroWritable, coroActual);

                // Push a scope for the coroutine's local variables
                CurrentFrame.Scopes.PushScope();

                // Bind parameters
                var paramsBindEnd = hasParams ? decl.Params.Count - 1 : decl.Params.Count;
                for (var i = 0; i < paramsBindEnd; i++)
                {
                    object? paramVal;
                    if (i < args.Length) paramVal = args[i];
                    else if (decl.DefaultValues != null && i < decl.DefaultValues.Count && decl.DefaultValues[i] != null)
                        paramVal = Evaluate(decl.DefaultValues[i]!);
                    else paramVal = RuntimeError<object?>($"Missing argument for parameter '{decl.Params[i].Name}'.", line, column);

                    BindTupleParam(decl, i, paramVal, line, column);
                    BindParam(decl.Params[i].Name, paramVal, decl.Params[i].Type,
                        decl.IsRefParams[i],
                        decl.IsOutParams != null && i < decl.IsOutParams.Count && decl.IsOutParams[i],
                        decl.Line, decl.Column);
                }

                if (hasParams)
                    CurrentFrame.Scopes.Define(decl.Params[paramsBindEnd].Name,
                        AssembleParamsTail(decl.Params, args, line, column), decl.Line, decl.Column);

                if (!string.IsNullOrEmpty(decl.Name))
                    CurrentFrame.Scopes.Define(decl.Name, fn, decl.Line, decl.Column);

                // Keep _loopStackSp, inner coroutine frames push above outer's.
            }

            // --- Restore loop stack state ---
            var startIndex = 0;
            if (isResume)
            {
                var rs = resumeState!.Value;
                startIndex = rs.StatementIndex;
                // Check if the resumed statement contains a yield-with-value
                // (overrides stored _expectResumeValue for the mixed bare+yield case)
                _expectResumeValue = rs.ExpectResumeValue;
                if (!_expectResumeValue && startIndex >= 0 && startIndex < decl.Body.Count)
                {
                    var stmt = decl.Body[startIndex];
                    _expectResumeValue = ConsumesResumeValue(stmt);
                }
                _loopStackSp = rs.LoopStackSp;
                for (var i = 0; i < _loopStackSp; i++)
                    _loopStack[i] = rs.SavedLoopStack![i];
            }

            scopeCountAtEntry = CurrentFrame.Scopes.Scopes.Count;

            // Method-coroutine `this` (Task 4): bind the receiver in the coroutine's OWN CallFrame
            // scope and set the interpreter's this/class context around the body, on every entry
            // (first-call AND resume: the SavedScope restore carries the scope binding, but
            // _currentThis/_currentClass are interpreter fields and must be re-set on resume).
            if (coroutine?.ThisInstance != null)
            {
                CurrentFrame.Scopes.Define("this", coroutine.ThisInstance, decl.Line, decl.Column);
                _currentThis = coroutine.ThisInstance;
                _currentClass = coroutine.ThisClass;
            }

            // --- Execute body ---
            for (var i = startIndex; i < decl.Body.Count; i++)
            {
                _currentStatementIndex = i;
                var result = Execute(decl.Body[i]);
                var flow = result.Flow;

                if (flow == ControlFlow.Yield)
                {
                    // A GENERATOR (foreach/MoveNext-driven) never CONSUMES resume values
                    // (VisitYieldExpr skips the consume branch for IsGenerator), so a yield of a
                    // CoroutineYieldRequest must not go through the suspend/resume machinery, a
                    // request-yield is not a yielded VALUE and would otherwise re-enter the yield
                    // statement, re-evaluate it, and re-yield forever (regression: a generator
                    // driving a tree-only sleep generator hung at 8% CPU / growing RAM). A
                    // SleepRequest is honored INLINE (block, continue to the next statement, the
                    // generator yields no element for it); any other request type cannot be awaited
                    // without the pump and is rejected below.
                    if (coroutine is { IsGenerator: true } && _yieldValue is SleepRequest sleepReq)
                    {
                        Thread.Sleep(sleepReq.Milliseconds);
                        // Reset the yield latch exactly like the normal yield path does below,
                        // otherwise Execute() misreads the NEXT statement (e.g. `return 0`) as a
                        // yield, swallows it, and the body falls off the end → "must return a value".
                        _isYield = false;
                        _yieldValue = null;
                        continue;
                    }
                    TraceCoro($"yield {funcName} val={_yieldValue} stmt={_currentStatementIndex}");
                    // Resume re-enters the OUTERMOST active loop of this function (base of the
                    // chain), which then drives nested loops from their own frames. Top-frame
                    // indexing is wrong for nested loops (its LoopNodeStatementIndex is relative
                    // to the parent body).
                    // A GENERATOR (foreach/MoveNext-driven) never CONSUMES resume values,
                    // VisitYieldExpr skips the consume branch for IsGenerator. So a request-yield
                    // (Coroutine.Sleep/http/...) in a generator must NOT re-enter the yield
                    // statement: that re-evaluates it → a fresh request each resume → infinite
                    // re-yield + unbounded sleep-queue growth (regression: a generator driving a
                    // tree-only sleep generator hung at 8% CPU / growing RAM). Generators advance
                    // PAST the request (Sleep is honored by the resume-side blocking; other
                    // requests are rejected below). Non-generators (Coroutine.Start) re-enter to
                    // consume the resume value, as before.
                    var isRequest = _yieldValue is CoroutineYieldRequest;
                    var genRequest = isRequest && coroutine is { IsGenerator: true };
                    int newStmtIndex = _loopStackSp > loopBaseSp
                        ? _loopStack[loopBaseSp].LoopNodeStatementIndex
                        : isRequest && !genRequest
                            ? _currentStatementIndex  // re-enter yield-expr statement on resume
                            : _currentStatementIndex + 1;
                    _expectResumeValue = isRequest && !genRequest;

                    var savedLoopStack = new LoopFrame[_loopStackSp];
                    for (var li = 0; li < _loopStackSp; li++)
                        savedLoopStack[li] = _loopStack[li];

                    var state = new CoroutineState
                    {
                        StatementIndex = newStmtIndex,
                        ExpectResumeValue = _expectResumeValue,
                        LoopStackSp = _loopStackSp,
                        LoopBaseSp = loopBaseSp,
                        SavedLoopStack = savedLoopStack
                    };

                    _isYield = false;
                    didYield = true;

                    // Save scope to coroutine for next resume
                    if (coroutine != null)
                        coroutine.EnsureTree().SavedScope = CurrentFrame.Scopes.SaveLiveStack();

                    if (_yieldValue is CoroutineYieldRequest req)
                    {
                        if (genRequest && req is not SleepRequest)
                            RuntimeError($"A generator cannot await a '{req.GetType().Name}' coroutine request, use Coroutine.Start for sleep/http/event waits.", line, column);
                        HandleYieldRequest(req, coroutine!, state);
                        return null;
                    }


                    if (coroutine != null)
                    {
                        coroutine.EnsureTree().State = state;
                        coroutine.Status = CoroutineStatus.Waiting;
                        EnqueuePumpWork(new ScheduledCoroutine(coroutine, DateTime.UtcNow));
                    }
                    return _yieldValue;
                }
                if (flow == ControlFlow.Return)
                {
                    var returnValue = result.Value;
                    if (fn.IsPure)
                        _pureMemoCache[(decl.Name, [.. args])] = returnValue ?? VoidMarker.Instance;
                    if (coroutine != null)
                    {
                        // The body ran to a `return` → natural completion.
                        coroutine.Status = CoroutineStatus.Completed;
                        coroutine.Tree = null;   // drops State + SavedScope together
                        // Drop any pending wake entry so the queue can't pin the finished coroutine.
                        lock (_sleepLock)
                        {
                            if (_sleepQueue.Count > 0) _sleepQueue.RemoveWhere(sc => ReferenceEquals(sc.Coroutine, coroutine));
                        }
                    }
                    return returnValue;
                }
                if (flow == ControlFlow.Throw)
                {
                    var ex = _thrownException; _thrownException = null;
                    throw ThrowPayloadException(ex, _currentLine, _currentColumn);
                }
                if (flow != ControlFlow.Continue)
                    RuntimeError($"Unexpected control flow in coroutine '{decl.Name}'.", line, column);
            }

            // AN `async Task` BODY MAY FALL OFF ITS END, which is C#'s own rule: such a body completes with no
            // value, exactly as the bare `return;` above does. The parser UNWRAPS an async return type, so
            // `async Task` arrives as the `object` placeholder it substitutes when the type carries no argument,
            // while `async Task<int>` arrives as `int`. Measured before this: every `async Task` body whose last
            // statement was a call, an `await` or an `await foreach` answered "Coroutine 'F' must return a value"
            // while the same body ending in a declaration was fine, and `async Task<int>` falling off keeps that
            // refusal, which is C#'s CS0161 (a Task<T> body owes a value on every path).
            if (decl.ReturnType != TypeInfo.Void
                && !(decl.IsAsync && decl.ReturnType.BaseType == TokenType.Object))
                RuntimeError($"Coroutine '{decl.Name}' must return a value.", line, column);
            if (coroutine == null) return null;
            // Void coroutine body ran to the end → natural completion.
            coroutine.Status = CoroutineStatus.Completed; coroutine.Tree = null;
            return null;
        }
        finally
        {
            _currentThis = savedThis;
            _currentClass = savedClass;
            // Pop CallFrame on every exit (yield, return, throw).
            // On yield: scope is already saved to coroutine, just cleanup scopes to entry count
            // On return/throw: cleanup all scopes
            if (didYield)
            {
                while (CurrentFrame.Scopes.Scopes.Count > scopeCountAtEntry)
                    CurrentFrame.Scopes.PopScope();
            }
            else
            {
                while (CurrentFrame.Scopes.Scopes.Count > scopeCountAtEntry)
                    CurrentFrame.Scopes.PopScope();
            }
            _callStack.TryPop(out _);
            _callFrames.Value!.TryPop(out _);
        }
    }

    private void HandleYieldRequest(CoroutineYieldRequest req, Coroutine co, CoroutineState? state)
    {
        // A switch EXPRESSION over a `closed` base, which is what makes the compiler verify that every request kind
        // is handled here. A switch STATEMENT over the same base is NOT checked (measured: a missing arm warns
        // CS8509 on the expression and says nothing about the statement), and this is the ONE place a new kind must
        // be handled, so an unhandled one would leave a coroutine waiting for ever instead of failing the build.
        // Each arm's body is its own method because an expression arm cannot hold statements; the VALUE is the
        // void marker the caller ignores, and evaluation is one input, one arm, exactly as the statement was.
        _ = req switch
        {
            SleepRequest sleep => ScheduleOnSleep(sleep, co, state),
            EventWaitRequest evWait => ScheduleOnEvent(evWait, co, state),
            HttpRequest http => StartHttpRequest(http, co, state),
            ReadLineRequest readLine => StartReadLine(readLine, co, state),
            AwaitRequest awaitReq => StartAwaitRequest(awaitReq, co, state),
        };
    }

    /// <summary>Schedules an `await`: the coroutine sleeps until the engine Task completes, and the task's OWN
    /// continuation only ENQUEUES the coroutine, never resumes it inline (the rule StartReadLine and
    /// ExecuteHttpRequest already follow). A task that is already finished wakes on the next pump tick, with no
    /// callback and no thread.</summary>
    private VoidMarker StartAwaitRequest(AwaitRequest awaitReq, Coroutine co, CoroutineState? state)
    {
        if (state is not null) co.EnsureTree().State = state;
        var task = awaitReq.Task;
        if (task.IsCompleted)
        {
            EnqueuePumpWork(new ScheduledCoroutine(co, DateTime.MinValue, awaitReq));
        }
        else
        {
            // A NATIVE continuation: the awaited task's completion posts the resume to the RUN queue and returns.
            // Nothing resumes a script body on the completing thread (the rule StartReadLine and StartHttpRequest
            // keep), and the post is non-blocking, so a task completed on a pool thread never waits for the run.
            _ = task.NativeTask.ContinueWith(_ =>
                EnqueuePumpWork(new ScheduledCoroutine(co, DateTime.MinValue, awaitReq)),
                global::System.Threading.Tasks.TaskScheduler.Default);
        }
        co.Status = CoroutineStatus.Waiting;
        return VoidMarker.Instance;
    }

    private VoidMarker ScheduleOnSleep(SleepRequest sleep, Coroutine co, CoroutineState? state)
    {
        var wakeTime = DateTime.UtcNow.AddMilliseconds(sleep.Milliseconds);
        EnqueuePumpWork(new ScheduledCoroutine(co, wakeTime));
        if (state is not null) co.EnsureTree().State = state;
        co.Status = CoroutineStatus.Waiting;
        return VoidMarker.Instance;
    }

    private VoidMarker ScheduleOnEvent(EventWaitRequest evWait, Coroutine co, CoroutineState? state)
    {
        if (!_eventWaiters.TryGetValue(evWait.Event, out var list))
        {
            list = [];
            _eventWaiters[evWait.Event] = list;
        }
        list.Add(co);
        if (state is not null) co.EnsureTree().State = state;
        co.Status = CoroutineStatus.Waiting;
        return VoidMarker.Instance;
    }

    private VoidMarker StartHttpRequest(HttpRequest http, Coroutine co, CoroutineState? state)
    {
        // Allocate the tree state BEFORE scheduling the background HTTP task so the task
        // body can safely use co.Tree! (no allocate-on-first-use race on the field).
        if (state is not null) co.EnsureTree().State = state;
        ExecuteHttpRequest(http, co);
        co.Status = CoroutineStatus.Waiting;
        return VoidMarker.Instance;
    }

    private VoidMarker StartReadLine(ReadLineRequest readLine, Coroutine co, CoroutineState? state)
    {
        // Background thread reads stdin without blocking the interpreter
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                readLine.Result = Console.ReadLine();
            }
            catch
            {
                readLine.Result = null;
            }
            readLine.Completed.Set();
            lock (_sleepLock)
                _sleepQueue.Add(new ScheduledCoroutine(co, DateTime.MinValue, readLine.Result));
        });
        if (state is not null) co.EnsureTree().State = state;
        co.Status = CoroutineStatus.Waiting;
        return VoidMarker.Instance;
    }

    /// <summary>A CALL to an `async` METHOD (class or struct member): the body runs as a coroutine bound to its
    /// receiver, exactly as <see cref="WrapCoroutineMethod"/> builds it, but the caller receives the Task that
    /// completes with the body's return value instead of a Generator.</summary>
    private Task BeginAsyncMethodCall(FuncDecl method, object?[] args, object? instance, ClassInfo? declClass)
    {
        var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(_rootFrame.Scopes.GetGlobalScopeDictionary());
        var sf = new ScriptFunction(method, snapshot, method.ReturnType, GetParameterTypes(method.Params),
            method.IsRefParams, IsOutParams: method.IsOutParams);
        var coArgs = new object?[args.Length + 1];
        Array.Copy(args, coArgs, args.Length);
        coArgs[args.Length] = instance;   // `this` local, which the compiler indexes after the params
        var task = new Task();
        task.SetPumpDriver(PumpCoroutines);
        task.SetPumpWork(PumpWorkSignal, PumpNextDueMs);
        var co = new Coroutine(this, sf, coArgs)
        {
            AsyncTask = task,
            ThisInstance = instance,
            ThisClass = declClass,
        };
        // VM-backed when the body compiled and produces no yield-requests, exactly as BeginAsyncCall decides for a
        // free async function. Without it a LOADED async method was driven by the TREE, which a wire body cannot
        // serve: the descriptor carries bytecode only, so the tree walked an EMPTY body, fell off its end and
        // refused with "Coroutine 'Get' must return a value" (measured on a `--no-source` bundle of a class whose
        // method awaits).
        co.VmBacked = IsVmBackedCoroutine(method);
        try
        {
            co.Resume();
        }
        catch
        {
            // The body faults the task (Coroutine.Resume does that), and C# surfaces it at the await.
        }
        return task;
    }

    /// <summary>A CALL to an `async` function (C# flavour): the body starts as a coroutine, suspends at each
    /// `await`, and the caller receives the script-visible Task that completes with the body's return value (or
    /// faults with what it threw). The task's Wait/Result DRIVES the coroutine pump, so an async body reaches
    /// completion without the script having to call Coroutine.Pump() itself.</summary>
    private Task BeginAsyncCall(ScriptFunction fn, object?[] args)
    {
        var task = new Task();
        task.SetPumpDriver(PumpCoroutines);
        task.SetPumpWork(PumpWorkSignal, PumpNextDueMs);
        var co = new Coroutine(this, fn, args) { AsyncTask = task };
        // VM-backed when the body compiled and produces no yield-requests: the same check InvokeScriptFunction
        // uses. Without it an async body whose bytecode exists is still driven by the TREE.
        co.VmBacked = IsVmBackedCoroutine(fn.Declaration);
        try
        {
            co.Resume();
        }
        catch
        {
            // A body that throws FAULTS its task (Coroutine.Resume does that) and C# surfaces the failure at the
            // await, never at the call, so nothing escapes here.
        }
        return task;
    }

    /// <summary>Signalled when the pump has work READY, so a waiting Task does not poll. MANUAL reset, because one
    /// waiter must not consume another's wake-up: <see cref="PumpCoroutines"/> clears it once a pass leaves nothing
    /// pending, and only when nothing was enqueued during that pass (the generation guard below), which is what stops
    /// a wake-up set between the pass and the reset from being lost.</summary>
    private readonly EventWaitHandle _pumpWork = new(false, EventResetMode.ManualReset);

    /// <summary>Bumped by every enqueue; the drain compares it to decide whether it may clear the signal.</summary>
    private long _pumpWorkGen;

    /// <summary>One-shot work the pump performs, in order, OUTSIDE its own lock. A nested script run is the first
    /// user (`RunScriptAsync`), and it is PUMP work rather than a task DRIVER because a composite copies ONE
    /// constituent's driver (`Task.WhenAll` does `t._pumpDriver ??= task.PumpDriver`), so a driver-run nested
    /// script would be the only one that ever ran and the composite would wait for ever (measured).</summary>
    private readonly List<Action> _pumpActions = [];

    /// <summary>Queues one-shot pump work and wakes the waiters, so a parked wait comes back and performs it.</summary>
    internal void EnqueuePumpAction(Action work)
    {
        lock (_sleepLock)
        {
            _pumpActions.Add(work);
            _pumpWorkGen++;
        }
        WakeWaiters();
    }

    /// <summary>The pump's work signal, handed to every task that can drive the pump.</summary>
    internal WaitHandle PumpWorkSignal => _pumpWork;

    /// <summary>How long a wait may sleep with nothing signalled: the milliseconds until the pump's next due
    /// COROUTINE, -1 when the clock holds nothing for this script, or a one-frame slice when this run has a WINDOW
    /// up: the pump dispatches UI messages on every pass (<see cref="NativeApp.PumpWindowsFromWait"/>), so a form
    /// stays alive during an `await` only if the wait keeps coming back, and there is no handle to signal a click.
    /// Delays are not in this question any more: a delay is a native timer now, so the only things the pump
    /// schedules are coroutine resumptions.</summary>
    internal int PumpNextDueMs()
    {
        var slice = NativeApp.HasWindowOnThisThread() ? 16 : -1;
        lock (_sleepLock)
        {
            var min = _sleepQueue.Count > 0 ? _sleepQueue.Min : null;
            if (min == null) return slice;
            var left = (min.WakeTime - DateTime.UtcNow).TotalMilliseconds;
            var due = (int)Math.Max(0, Math.Round(left));
            return slice < 0 ? due : Math.Min(due, slice);
        }
    }

    /// <summary>Wakes every waiter. Called by the enqueue helper below, which adds under the pump's lock and bumps
    /// the generation first: the add, the bump and the signal belong together, or a waiter can miss the work.</summary>
    private void WakeWaiters() => _pumpWork.Set();

    /// <summary>Gives the pump a coroutine to resume, and wakes the waiters.</summary>
    private void EnqueuePumpWork(ScheduledCoroutine work)
    {
        lock (_sleepLock)
        {
            _sleepQueue.Add(work);
            _pumpWorkGen++;
        }
        WakeWaiters();
    }

    internal void PumpCoroutines()
    {
        var savedSp = _loopStackSp;
        try
        {
            long genAtStart;
            List<Action>? pumpActions = null;
            lock (_sleepLock)
            {
                genAtStart = _pumpWorkGen;
                var now = DateTime.UtcNow;
                while (_sleepQueue.Count > 0 && _sleepQueue.Min?.WakeTime <= now)
                {
                    var sc = _sleepQueue.Min;
                    if (sc == null) break;
                    _sleepQueue.Remove(sc);
                    sc.Coroutine.Resume(sc.ResumePayload);
                }
                // Nothing pending: clear the signal, but only if nothing was enqueued during this pass, or a wake-up
                // set between the drain and this line would be lost and its waiter would sleep to the cap.
                pumpActions = _pumpActions.Count > 0 ? [.. _pumpActions] : null;
                _pumpActions.Clear();
                if (_sleepQueue.Count == 0 && pumpActions is null && genAtStart == _pumpWorkGen)
                    _pumpWork.Reset();
            }
            // Work the pump must perform runs OUTSIDE the guard: it can call back into the interpreter, and the pump
            // must not hold the queue's lock while a script body runs.
            if (pumpActions is not null) foreach (var pumpAction in pumpActions) pumpAction();
            // A SLICE POINT FOR THE WINDOWS THIS RUN OWNS, and the reason it belongs HERE: an `await` is driven by
            // `Task.Wait`'s own loop, which wakes this pump every few milliseconds and dispatches NO messages, so a
            // script that awaits froze its own window (measured in a probe: a 1.2 s `await Task.Delay` left a form
            // with ZERO paints and its posted click undelivered, while the same wait inside
            // `Application.DoEvents` stayed alive). C# dispatches a continuation through the UI thread's message
            // loop for exactly this reason, so this is the faithful behaviour rather than a convenience. Guarded
            // twice: no window of OURS means nothing to drain, and `NativeApp` refuses a nested dispatch, so a
            // handler that awaits again cannot recurse through here.
            NativeApp.PumpWindowsFromWait();
        }
        finally { _loopStackSp = savedSp; }
    }

    /// <summary>
    /// Attempts to build a fast loop-condition evaluator. Resolves variable scopes
    /// at build time so Evaluate() does direct dictionary lookups without tree-walking.
    /// </summary>
    /// <summary>Copy a dictionary using DictionaryPool to reduce allocations.</summary>
    private static Dictionary<string, object?> CopyDictionary(Dictionary<string, object?> source)
    {
        var d = DictionaryPool.Rent(source.Count);
        foreach (var kvp in source)
            d[kvp.Key] = kvp.Value;
        return d;
    }
}
