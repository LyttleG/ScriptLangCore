
namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>Enters a statement of a body: records its index for the resume logic AND starts a fresh pass for the
    /// once-per-statement await rule, because a resumed statement is re-entered through here (VisitAwaitExpr explains
    /// why a second await in one pass has to be refused rather than allowed to loop for ever).</summary>
    private void EnterStatement(int index)
    {
        _currentStatementIndex = index;
        if (_currentCoroutine is { Tree: { } enteredState }) enteredState.AwaitConsumedStatement = -1;
    }
    public ExecutionResult VisitExprStmt(ExprStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        var result = Evaluate(node.Expression);
        if (_callFrames.Value!.Count == 1)
            _lastExpressionValue = result;
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitDebuggerStmt(DebuggerStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        if (_debugger is { IsEnabled: true })
            _debugger.Break(this, node);
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitThrowStmt(ThrowStmt node)
    {
        var raw = node.Expression != null ? Evaluate(node.Expression) : null;
        var value = raw is Exception or ScriptErrorInfo ? raw : new ScriptErrorInfo(ErrorCode.InvalidOperation, raw?.ToString() ?? "", raw);
        _thrownException = value;
        _currentLine = node.Line;
        _currentColumn = node.Column;
        TraceStack("throw");
        throw new ScriptException(ErrorCode.InvalidOperation, value, node.Line, node.Column);
    }

    public ExecutionResult VisitTryStmt(TryStmt node)
    {
        // A suspension inside a CATCH body resumes THERE. Re-entering the whole try would re-run its body, and the
        // awaited statement in that body would consume the value this catch's await is waiting for (measured: an
        // await in the try, a throw, then an await in the catch answered "Coroutine 'G' must return a value"). The
        // scope the catch opened is still live (the scope stack is saved and restored with the suspension), so the
        // catch's own variable is still bound and the catch body resumes where its frame says.
        if (_currentCoroutine?.Tree?.SuspendedCatch is { } sc && ReferenceEquals(sc.Node, node))
        {
            var tryIdx = _currentStatementIndex;   // this try statement's own index (see tryStmtIndex below)
            _currentCoroutine.Tree.SuspendedCatch = null;
            var resumedClause = node.Catches[sc.CatchIndex];
            try
            {
                var resumed = Execute(resumedClause.Body);
                if (resumed.Flow == ControlFlow.Yield)
                {
                    _currentCoroutine.Tree.SuspendedCatch = (node, sc.CatchIndex, sc.Thrown);
                    return resumed;
                }
                CurrentFrame.Scopes.PopScope();   // the scope the suspension kept open
                if (node.Finally != null)
                {
                    if (resumed.Flow == ControlFlow.Return) _pendingReturnValue = resumed.Value;
                    _currentStatementIndex = tryIdx;
                    var resumedFinally = Execute(node.Finally);
                    _pendingReturnValue = null;
                    if (resumedFinally.Flow != ControlFlow.Continue) return resumedFinally;
                }
                return resumed;
            }
            catch
            {
                CurrentFrame.Scopes.PopScope();
                throw;
            }
        }

        object? thrownValue = null;
        // The index of THIS try statement in its own statement list, restored before the catch and the finally
        // bodies run: those bodies are not statements of that list, so their own block frames would otherwise carry
        // whatever `_currentStatementIndex` happened to hold (the try body's LAST index), and a resume inside a
        // catch would start the coroutine body PAST the try instead of re-entering it. That is what an await in a
        // catch used to do: "Coroutine 'G' must return a value", with the catch's continuation abandoned.
        var tryStmtIndex = _currentStatementIndex;
        ExecutionResult tryResult;
        try
        {
            tryResult = Execute(node.Body);
            if (tryResult.Flow == ControlFlow.Throw)
            {
                thrownValue = _thrownException;
                _thrownException = null;
            }
        }
        catch (ScriptException ex)
        {
            TraceStack("catch-scriptex");
            // AN INTERRUPT AND A KILL ARE NOT THE SCRIPT'S TO CATCH: `Ctrl+C` and `cts.Cancel()` both land here, and a
            // `try { <a long loop> } catch (var e) { }` around them must not swallow them (it would defeat a runner's
            // Stop button and the REPL's own interrupt). A `finally` still runs, because that is the outer handler.
            if (ex.ErrorCode is ErrorCode.Interrupt or ErrorCode.Cancelled or ErrorCode.RunSuspend)
                throw;
            // A script's own exit() is the run's VERDICT: AbortExitCode is the one place that question is
            // asked, so a catch that wraps a call answers `exitcode` for an abort the call site rerolled
            // (a reroll keeps the code in the value but loses the exception's own type) exactly as it does
            // for an intact one.
            if (AbortExitCode(ex) is int abortCode)
                Environment.ExitCode = abortCode;
            thrownValue = ex.Value is ScriptErrorInfo sei
                ? sei.WithLocation(ex.Line, ex.Column)
                : new ScriptErrorInfo(ex.ErrorCode, ex.Message);
            tryResult = ExecutionResult.Throw(thrownValue);
        }
        catch (Exception ex)
        {
            TraceStack("catch-ex");
            // A non-ScriptException reaching a script's catch: go through the SAME friendly map the rest of
            // the engine uses, instead of handing the script a raw CLR message, those are resource keys
            // under InvariantGlobalization ("Arg_OutOfMemoryException") or Win32 text in the OS language
            // ("DllNotFound_Windows, sqlite3, Le module spécifié est introuvable.").
            thrownValue = new ScriptErrorInfo(ErrorCode.Unspecified, FriendlyExceptionMessage(ex));
            tryResult = ExecutionResult.Throw(thrownValue);
        }

        if (tryResult.Flow == ControlFlow.Throw)
        {
            for (var catchIndex = 0; catchIndex < node.Catches.Count; catchIndex++)
            {
                var catchClause = node.Catches[catchIndex];
                var matches = (string.IsNullOrEmpty(catchClause.VarName) &&
                               catchClause.ExceptionType is { BaseType: TokenType.Object, TypeName: null, IsNullable: true }) || IsExceptionMatch(thrownValue, catchClause.ExceptionType);
                if (matches)
                {
                    CurrentFrame.Scopes.PushScope();
                    var catchYielded = false;
                    try
                    {
                        if (!string.IsNullOrEmpty(catchClause.VarName))
                            CurrentFrame.Scopes.Define(catchClause.VarName, thrownValue, catchClause.Line, catchClause.Column);
                        // Run the catch body with THIS try statement's index, so a frame it pushes names the try (see
                        // the note on tryStmtIndex) and an await inside the catch resumes at the try, not past it.
                        _currentStatementIndex = tryStmtIndex;
                        var catchResult = Execute(catchClause.Body);
                        tryResult = catchResult;
                        // A suspension inside this catch: remember WHICH catch, and KEEP the scope this arm opened,
                        // so the resume re-enters the catch body with its own variable still bound (the resume path
                        // at the top of this method closes that scope when the catch finally completes).
                        if (catchResult.Flow == ControlFlow.Yield && _currentCoroutine != null)
                        {
                            catchYielded = true;
                            _currentCoroutine.EnsureTree().SuspendedCatch = (node, catchIndex, thrownValue);
                        }
                    }
                    finally { if (!catchYielded) CurrentFrame.Scopes.PopScope(); }
                    break;
                }
            }
        }

        if (node.Finally != null)
        {
            if (tryResult.Flow == ControlFlow.Return)
                _pendingReturnValue = tryResult.Value;
            _currentStatementIndex = tryStmtIndex;
            var finallyResult = Execute(node.Finally);
            _pendingReturnValue = null;
            if (finallyResult.Flow != ControlFlow.Continue)
                return finallyResult;
        }
        return tryResult;
    }

    public ExecutionResult VisitUsingStmt(UsingStmt node)
    {
        CurrentFrame.Scopes.PushScope();
        try
        {
            var value = Evaluate(node.Initializer);
            if (ReferenceEquals(value, YieldSentinel)) return default;
            CurrentFrame.Scopes.Define(node.VarName, value, node.Line, node.Column);
            var result = Execute(node.Body);
            if (result.Flow == ControlFlow.Throw)
            {
                // `await using` AWAITS the disposal, a plain `using` disposes synchronously.
                if (node.IsAwait) DisposeAsyncResource(value, node.Line, node.Column);
                else TryDispose(value);
            }
            return result;
        }
        finally
        {
            // THE SCOPE IS POPPED EVEN WHEN THE DISPOSAL RAISES. A refusal thrown from the disposal used to
            // abort the rest of this `finally`, so PopScope never ran, the using's scope stayed on the stack, and
            // the NEXT statement was bound into it: measured on the top-level VM leg, a TYPED declaration right
            // after an `await using` whose disposal refused answered "Undefined variable 'zz'" (a `var` was fine,
            // because that leg compiles it, and a plain `using` was fine, because the VM compiles it and disposes
            // through the DISPOSE opcode, which never reaches here).
            try
            {
                if (CurrentFrame.Scopes.TryGet(node.VarName, out var disposed))
                {
                    if (node.IsAwait) DisposeAsyncResource(disposed, node.Line, node.Column);
                    else TryDispose(disposed);
                }
            }
            finally
            {
                CurrentFrame.Scopes.PopScope();
            }
        }
    }

    public ExecutionResult VisitUsingDeclStmt(UsingDeclStmt node)
    {
        var value = Evaluate(node.Initializer);
        if (ReferenceEquals(value, YieldSentinel)) return default;
        CurrentFrame.Scopes.Define(node.VarName, value, node.Line, node.Column);
        // C#-style: dispose when the ENCLOSING scope exits (reverse declaration order), awaited for `await using`.
        if (node.IsAwait) CurrentFrame.Scopes.RegisterDisposeOnExit(() => DisposeAsyncResource(value, node.Line, node.Column));
        else CurrentFrame.Scopes.RegisterDisposeOnExit(() => TryDispose(value));
        return ExecutionResult.Continue();
    }

    internal static void TryDispose(object? obj)
    {
        if (obj is IDisposable d) { d.Dispose(); return; }
        // A resource that only implements IAsyncDisposable was SILENTLY LEFT OPEN here (measured: `using (var seq
        // = File.ReadLinesAsync(p)) { }` disposed nothing and the file handle stayed held), so the sync path now
        // says which spelling disposes it. C# refuses this shape at compile time for the same reason.
        if (obj is IAsyncDisposable)
        {
            // The interpreter's CURRENT position: this helper is reached from the several places a scope disposes
            // (a using's finally, a scope exit, the VM's DISPOSE), and a refusal without a position is a message a
            // reader cannot place.
            var where = Current;
            RuntimeError($"{ReportedTypeName(obj)} implements IAsyncDisposable, not IDisposable: "
                + "write `await using` so the disposal is awaited.", where?._currentLine ?? 0, where?._currentColumn ?? 0);
            return;
        }
        // Script class instances, call Dispose() via the interpreter's method dispatch
        if (obj is ClassInstance ci)
        {
            var interpreter = Current;
            if (interpreter != null)
            {
                try
                {
                    var dtorInfos = interpreter.FindAllDestructorClasses(ci.Class);
                    if (dtorInfos.Count == 0) return;
                    var prevThis = interpreter._currentThis;
                    var prevClass = interpreter._currentClass;
                    interpreter._currentThis = ci;
                    // Create a simple snapshot with only the root global scope (no local captures)
                    // so InvokeScriptFunction uses the live root scope, print and all builtins work
                    var snapshot = interpreter.CreateSnapshotWithGlobalScope();
                    try
                    {
                        for (var i = dtorInfos.Count - 1; i >= 0; i--)
                        {
                            interpreter._currentClass = dtorInfos[i].declaringClass;
                            var disposeFn = new ScriptFunction(dtorInfos[i].destructor, snapshot, TypeInfo.Void, [], [], Flags: FunctionFlags.None);
                            interpreter.InvokeScriptFunction(disposeFn, [ci], 0, 0);   // trailing arg = 'this' local for VM-compiled destructors
                        }
                    }
                    finally { interpreter._currentThis = prevThis; interpreter._currentClass = prevClass; }
                }
                catch { /* best-effort disposal */ }
            }
            return;
        }
        // Duck-typing: try to find and call Dispose() via reflection
        try
        {
            var disposeMethod = obj?.GetType().GetMethod("Dispose", Type.EmptyTypes);
            if (disposeMethod != null && disposeMethod.ReturnType == typeof(void))
                disposeMethod.Invoke(obj, null);
        }
        catch { /* ignore, best-effort disposal */ }
    }

    public ExecutionResult VisitVarDeclStmt(VarDeclStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        // A LOCAL declaration names a type too, and this position was outside the guard as well (`String loc = "x";`
        // was accepted silently). A declaration written `var` carries no type name, which the helper skips.
        RefuseUnregisteredTypeNames(node.Type, $"variable '{node.Name}'", node.Line, node.Column);
        RecordNamespacePath(node.Name, node.Line, node.Column);
        var targetScope = CurrentFrame.Scopes.GetCurrentScopeDictionary();
        // A collection expression is TARGET-TYPED (C# 12), and the target has to be applied BEFORE the build:
        // `int?[] a = [1, null, 3]` is legal because the elements convert to the DECLARED element type, while
        // without it the literal infers `int` from 1 and 3 and refuses the null during its own construction.
        // `int[] a = [1, null, 3]` still refuses, which is the C# rule too (null does not convert to int), and a
        // `var` declares no element type, so it keeps the inference, exactly as in C#.
        //
        // ARRAY targets only, deliberately: the literal already BUILDS an array, so an array declared type needs
        // nothing beyond the element type. A `List<T>` target is filled by the declaration's own coercion, which
        // converts an `object[]` but NOT a typed array, so handing it one here would leave `List<int> l = [1, 2]`
        // holding an int[] (measured: `Property 'Count' not found on type int[]`). A List target with a null
        // element therefore stays refused for now, and closing THAT is one change across CoerceTo and this
        // position, not two halves of one.
        object? initializer;
        if (node.Initializer is ListLiteralExpr targetLit && node.Type is { IsArray: true }
            && DeclaredContainerElementType(node.Type) is { } targetElem)
            initializer = CreateArray(targetElem, null, targetLit.Elements, targetLit.Line, targetLit.Column);
        else
            initializer = node.Initializer != null ? Evaluate(node.Initializer) : DefaultFor(node.Type!);
        if (ReferenceEquals(initializer, YieldSentinel)) return default;
        // A REF LOCAL (`ref int r = ref x;`) holds a CELL, and its declared type describes what the cell POINTS AT, so
        // coercing the cell to that type would refuse it ("cannot convert 'RefCell' to 'int'") while coercing the value
        // it holds would break the write-through. The cell is stored as it is: reads dereference it and assignments
        // write through, which is exactly how a ref PARAMETER already behaves on both engines.
        // A ref local bound to a CALL: only a ref-RETURNING declaration answers a reference, so a call to an ordinary
        // function in that position is refused here, naming what it needs (C#'s CS8156: this expression may not be
        // returned by reference). Without the check the value would be stored as a plain local and the write-through
        // would silently do nothing.
        if ((node.Initializer is CallExpr { WantsRefResult: true } || node.Initializer is MethodCallExpr { WantsRefResult: true })
            && initializer is not RefCell)
        {
            RuntimeError(
                "`ref` on a call needs a REF-RETURNING declaration: `" + node.Name + "` was bound to a call that answered a "
                + "value, so there is no storage to write through. Declare the callee `ref <type> F(...)` and return "
                + "`ref <target>` from it, or drop the `ref` from this declaration.", node.Line, node.Column);
        }
        var coerced = initializer is RefCell ? initializer : CoerceTo(initializer, node.Type!, node.Line, node.Column);

        // Tuple-typed declaration: (int c, int d) name = (expr, expr), attach element names
        if (node.TupleElementNames is { Count: > 0 } && coerced is ScriptTuple tup2)
            tup2.Names = node.TupleElementNames;

        if (!node.WasEscaped && CurrentFrame.Scopes.TryGetWithKey(node.Name, out var matchedKey, out var existing))
        {
            if (IsTypeBinding(existing) && string.Equals(matchedKey, node.Name, _comparison))
                RuntimeError($"Cannot declare variable '{node.Name}' because it shadows an existing type. " +
                             $"Use '@{node.Name}' to explicitly shadow the type.", node.Line, node.Column);

            // Warn when variable already exists with different casing (lax mode)
            if (!string.Equals(matchedKey, node.Name, StringComparison.Ordinal))
            {
                Logging.Log.Warning("Script",
                    $"Variable '{matchedKey}' already exists and will be reused as '{node.Name}'.",
                    node.Line, node.Column);
            }
            else
            {
                // Only warn about redeclaration in the SAME scope (same { } block).
                // Shadowing a variable from an outer scope or different loop is valid.
                var currentScope = CurrentFrame.Scopes.GetCurrentScopeDictionary();
                // In coroutines, the scope stays alive across loop iterations; redeclaration is expected.
                if (_currentCoroutine == null && !SuppressWarnings && currentScope.TryGetValue(node.Name, out var origInfo) && origInfo.Line != 0 && origInfo.Column != 0 && !(origInfo.Line == node.Line && origInfo.Column == node.Column))
                    Logging.Log.Warning("Variable",
                        $"'{node.Name}' already declared at [L{origInfo.Line}:C{origInfo.Column}]",
                        node.Line, node.Column);
            }
        }

        // THE DECLARED TYPE, SPELLED AS THE SCRIPT WROTE IT, because two readers want the whole thing and the base name
        // was not enough: the assignment arm recognises its numeric names (`long sum = 0; sum = 0;` must stay long) and
        // `typeof(<a name>)` reports it for a NULL value, where `List<int> d;` used to answer the bare "object" and a
        // generic's arguments were lost.
        // A `var` declaration carries the OBJECT placeholder (BaseType Object with no name), which is the SAME shape the
        // compiler's own predicate treats as "nothing declared" and the type-name guard documents: recording "object"
        // for it would make `typeof(x)` answer "object" for every inferred variable.
        // AN INFERRED `var` CARRIES NO TYPE NAME, which is the same predicate the compiler's own `coerceDecl` asks and
        // the one the type-name guard documents: recording the inferred placeholder would make `typeof(x)` answer
        // "object" for every inferred variable. An explicit `object x;` is the SAME shape to the compiler (so it also
        // records nothing, and `typeof` reports its value's type), which is the one pinned limit; every OTHER typed
        // declaration is recorded and `typeof(<that name>)` answers it even when the value is null or a narrower type.
        var typeName = node.Type is { } declType
            && !(declType.BaseType == TokenType.Object && declType.TypeName == null)
            ? TypeInfo.DeclaredSpelling(declType)
            : null;
        targetScope[node.Name] = new ScopeStack.VariableInfo(coerced!, node.Line, node.Column, typeName);
        if (_currentNamespaceLeaf != null && _currentFuncDecl == null && _currentClass == null)
            _currentNamespaceLeaf.AddProperty(node.Name, coerced);   // initial value (v1: like the module snapshot, later reassignments are not live)
        if (node.IsConst)
            CurrentFrame.Scopes.GetCurrentConstSet().Add(node.Name);
        if (_recordingExports && _currentExports != null)
            _currentExports.TryAdd(node.Name, coerced);
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitClassDeclStmt(ClassDeclStmt node)
    {
        // Blocks written inside the class body register the same way a top-level one does.
        foreach (var ext in node.Extensions)
            RegisterExtensionMembers(ext.TypeParameters, ext.Target, ext.Members, ext.ContainerName);
        RecordNamespacePath(node.Name, node.Line, node.Column);
        foreach (var nested in node.NestedClasses)
            VisitClassDeclStmt(nested);

        // Register delegate types declared inside the class body
        foreach (var del in node.Delegates)
            VisitDelegateDecl(del);

        // Generic class template, store without registering as concrete
        if (node.TypeParameters.Count > 0)
        {
            if (!_genericClassTemplates.TryAdd(node.Name, node))
            {
                Logging.Log.Warning("Script",
                    $"Generic class '{node.Name}' already defined. Skipping.",
                    node.Line, node.Column);
                return ExecutionResult.Continue();
            }

            // Create a ClassInfo for the open generic type that holds only static members,
            // so that Tracker.Count inside the class body resolves via ClassInfo.StaticFields
            // instead of failing on a bare ClassDeclStmt (which has no StaticFields).
            var templateStaticFields = new Dictionary<string, object?>(_comparer);
            var templateStaticFieldDecls = new List<FieldDecl>();
            var templateStaticMethods = new List<FuncDecl>();
            foreach (var f in node.Fields.Where(f => f.IsStatic))
            {
                var val = f.Initializer != null ? Evaluate(f.Initializer) : DefaultFor(f.Type);
                templateStaticFields[f.Name] = CoerceTo(val, f.Type, node.Line, node.Column);
                templateStaticFieldDecls.Add(f);
            }
            foreach (var m in node.Methods.Where(m => m.IsStatic))
            {
                var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(
                    _rootFrame.Scopes.GetGlobalScopeDictionary());
                var sf = new ScriptFunction(m, snapshot, m.ReturnType,
                    GetParameterTypes(m.Params), m.IsRefParams, IsOutParams: m.IsOutParams);
                templateStaticMethods.Add(m);
                CurrentFrame.Scopes.Define(node.Name + "." + m.Name, sf);
            }

            var templateClassInfo = new ClassInfo(node.Name, node.ParentName,
                [], [], [], node.NestedClasses, node.Events, node.OperatorMethods, node.Line, node.Access)
            {
                StaticFields = templateStaticFields.Count > 0 ? templateStaticFields : null,
                StaticFieldDecls = templateStaticFieldDecls.Count > 0 ? templateStaticFieldDecls : null,
                StaticMethods = templateStaticMethods.Count > 0 ? templateStaticMethods : null,
                NestedStructs = node.NestedStructs
            };

            Classes[node.Name] = templateClassInfo;
            CurrentFrame.Scopes.Define(node.Name, templateClassInfo);
            if (_recordingExports && _currentExports != null)
                _currentExports.TryAdd(node.Name, templateClassInfo);

            return ExecutionResult.Continue();
        }

        if (Classes.ContainsKey(node.Name))
        {
            // A re-declaration from the SAME SOURCE replaces the class silently: `.run <file>` twice must install the
            // file's CURRENT body (see _declaredBySource for the measured case this fixes). A name another source
            // declared keeps the warning and the skip, because the session may still need that class: this is the
            // deliberate "a `.run` keeps what it declared" rule, which a middleware in a later request relies on.
            if (!DeclaredByCurrentSource(node.Name))
            {
                if (!SuppressWarnings)
                    Logging.Log.Warning("Script",
                        $"Class '{node.Name}' already declared. Skipping redefinition.",
                        node.Line, node.Column);
                return ExecutionResult.Continue();
            }
        }

        ClassInfo? parentClass = null;
        // A CLR BASE IS A BRIDGE, NOT A PARENT: `class Sink extends TextWriter` keeps the class a script class and
        // gives it a CLR FACE (ScriptTextBridge), because a ClassInstance cannot become a CLR subtype here (NativeAOT
        // has no Reflection.Emit). The name is resolved through the engine's own type index first, so the spelling
        // follows it, and only the bases the engine can bridge are accepted.
        Type? clrBase = null;
        if (node.ParentName != null && !Classes.TryGetValue(node.ParentName, out parentClass))
        {
            clrBase = ScriptTextBridge.BridgedBase(ResolveTypeToClr(node.ParentName, !_caseSensitive));
            if (clrBase is null)
                RuntimeError($"Parent class '{node.ParentName}' not found for class '{node.Name}'. A class may extend a script class, or one of the CLR bases this engine bridges: TextWriter and TextReader.", node.Line, node.Column);
        }
        var allEvents = new List<EventDecl>(node.Events);
        if (parentClass != null)
        {
            var existingNames = new HashSet<string>(allEvents.Select(e => e.Name), _comparer);
            foreach (var baseEvent in parentClass.Events.Where(e => !existingNames.Contains(e.Name)))
            {
                allEvents.Insert(0, baseEvent);
                existingNames.Add(baseEvent.Name);
            }
        }

        // Separate static fields/methods from instance members
        var staticFields = new Dictionary<string, object?>(_comparer);
        var staticFieldDecls = new List<FieldDecl>();
        var staticMethods = new List<FuncDecl>();
        var instanceFields = new List<FieldDecl>(node.Fields.Where(f => !f.IsStatic));
        var instanceMethods = new List<FuncDecl>(node.Methods.Where(m => !m.IsStatic));

        // Evaluate static field initializers (with _currentClass set so cross-references resolve)
        var prevClass = _currentClass;
        var partialClassInfo = new ClassInfo(node.Name, node.ParentName, instanceFields, instanceMethods,
            node.Constructors, node.NestedClasses, node.Events, node.OperatorMethods, node.Line, node.Access,
            Properties: node.Properties)
        {
            StaticFields = staticFields,
            StaticFieldDecls = staticFieldDecls,
            NestedStructs = node.NestedStructs
        };
        _currentClass = partialClassInfo;
        // Register class name in scope BEFORE creating static methods so their closures can capture it
        CurrentFrame.Scopes.Define(node.Name, partialClassInfo);
        foreach (var f in node.Fields.Where(f => f.IsStatic))
        {
            var val = f.Initializer != null ? Evaluate(f.Initializer) : DefaultFor(f.Type);
            // If the initializer produces a ScriptFunction, keep it callable regardless of declared field type
            staticFields[f.Name] = val is ScriptFunction ? val : CoerceTo(val, f.Type, node.Line, node.Column);
            staticFieldDecls.Add(f);
        }
        _currentClass = prevClass;
        foreach (var m in node.Methods.Where(m => m.IsStatic))
        {
            var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(
                _rootFrame.Scopes.GetGlobalScopeDictionary());
            var paramTypes = GetParameterTypes(m.Params);
            var sf = new ScriptFunction(m, snapshot, m.ReturnType, paramTypes, m.IsRefParams, IsOutParams: m.IsOutParams);
            staticMethods.Add(m);
            // Register by bare name for static method dispatch; qualified name for direct call
            staticFields[m.Name] = sf;
            CurrentFrame.Scopes.Define(node.Name + "." + m.Name, sf);
        }

        // Type-name typo guard for class member signatures (params + return types).
        var classTypeParams = node.TypeParameters.Count > 0
            ? new HashSet<string>(node.TypeParameters, _comparer)
            : null;
        foreach (var m in node.Methods) WarnUnknownTypeNames(m, classTypeParams);
        // A `ref` overload beside an `out` one is ONE signature to a call, so the second is refused (see
        // Interpreter.DiffersOnlyByRefOutKeyword). A by-value position differing is an ordinary overload and passes.
        for (var mi = 0; mi < node.Methods.Count; mi++)
            for (var mj = 0; mj < mi; mj++)
                if (string.Equals(node.Methods[mi].Name, node.Methods[mj].Name, Comparison))
                    RefuseRefOutOnlyOverload(node.Methods[mi].Name, node.Methods[mi], node.Methods[mj],
                        $" in class '{node.Name}'", node.Methods[mi].Line, node.Methods[mi].Column);
        foreach (var c in node.Constructors)
            WarnUnknownParamTypes(c.Params, node.Name + " ctor", c.Line, c.Column, classTypeParams);
        foreach (var op in node.OperatorMethods.Values) WarnUnknownTypeNames(op, classTypeParams);
        if (node.Destructor != null) WarnUnknownTypeNames(node.Destructor, classTypeParams);
        // A FIELD and a PROPERTY are declarations too, and they were the two positions this guard missed: a field
        // typed `String` was accepted silently while a parameter was refused (measured 2026-10-06). FieldDecl and
        // PropertyDecl carry no position of their own, so the refusal points at the class that declares them.
        foreach (var fld in node.Fields)
            RefuseUnregisteredTypeNames(fld.Type, $"field '{fld.Name}' of '{node.Name}'", node.Line, node.Column);
        foreach (var prop in node.Properties)
            RefuseUnregisteredTypeNames(prop.Type, $"property '{prop.Name}' of '{node.Name}'", node.Line, node.Column);

        // Generate backing fields for auto-properties
        foreach (var prop in node.Properties)
        {
            var backingName = BackingFieldName(prop.Name);
            var bf = new FieldDecl(prop.Access, prop.Type, backingName, null, prop.IsStatic);
            if (prop.IsStatic)
            {
                var initVal = DefaultFor(prop.Type);
                staticFields[backingName] = CoerceTo(initVal, prop.Type, node.Line, node.Column);
                staticFieldDecls.Add(bf);
            }
            else
            {
                instanceFields.Add(bf);
            }
        }

        // Library export needs the PRE-ctor (field-initializer) static state: capture it now,
        // before any static ctor can run. Copy the dictionary (values are shared references,
        // snapshot semantics match how source classes start from initializer values).
        _initialStaticFields[node.Name] = new Dictionary<string, object?>(staticFields, _comparer);

        var classInfo = new ClassInfo(node.Name, node.ParentName, instanceFields, instanceMethods,
            node.Constructors, node.NestedClasses, allEvents, node.OperatorMethods, node.Line, node.Access,
            Properties: node.Properties)
        {
            Parent = parentClass,
            ClrBase = clrBase,
            Destructor = node.Destructor,
            StaticFields = staticFields.Count > 0 ? staticFields : null,
            StaticFieldDecls = staticFieldDecls.Count > 0 ? staticFieldDecls : null,
            StaticMethods = staticMethods.Count > 0 ? staticMethods : null,
            NestedStructs = node.NestedStructs,
            IsRecord = node.IsRecord
        };

        // An EXPLICIT implementation leaves the name table here: it is reached through its contract, and the
        // bare name must therefore refuse with the qualified spelling as the recipe.
        classInfo = classInfo with
        {
            Methods = classInfo.Methods.Where(m => m.Qualifier == null).ToList(),
            ExplicitMembers = classInfo.Methods.Where(m => m.Qualifier != null).ToList()
        };
        classInfo = ApplyClassInterfaces(node, classInfo);
        Classes[node.Name] = classInfo;
        RecordDeclaredBySource(node.Name);   // so a re-run of THIS file replaces it silently

        CurrentFrame.Scopes.Define(node.Name, classInfo);
        if (_currentNamespaceLeaf != null && _currentFuncDecl == null && _currentClass == null)
        {
            var nsPath = string.Join(".", _namespaceSegments);
            string? qualifiedParent = null;
            if (classInfo.ParentName is { Length: > 0 } pn)
            {
                // FIX B: if the parent is recorded with a non-empty path use {pnPath}.{pn};
                // else fall back to the flat parent name (always present, carries the base).
                if (_declaredInNamespace.TryGetValue(pn, out var pnPath) && pnPath.Length > 0)
                    qualifiedParent = $"{pnPath}.{pn}";
                else
                    qualifiedParent = pn;
            }
            var qualifiedCi = classInfo with { Name = $"{nsPath}.{node.Name}", ParentName = qualifiedParent };
            Classes[qualifiedCi.Name] = qualifiedCi;
            _currentNamespaceLeaf.AddProperty(node.Name, qualifiedCi);
        }
        // Ensure the class name reaches the interpreter's live global scope (used by InvokeScriptFunction)
        var (_, actualGlobalScope) = _rootFrame.Scopes.GetGlobalScopeReference();
        if (!actualGlobalScope.ContainsKey(node.Name))
            actualGlobalScope[node.Name] = new ScopeStack.VariableInfo(classInfo, 0, 0);
        if (_recordingExports && _currentExports != null)
            _currentExports.TryAdd(node.Name, classInfo);

        // Register nested structs
        foreach (var ns in node.NestedStructs)
        {
            var result = VisitStructDeclStmt(ns);
            if (result.Flow != ControlFlow.Continue) return result;
        }

        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitEnumDeclStmt(EnumDeclStmt node)
    {
        RecordNamespacePath(node.Name, node.Line, node.Column);
        if (Classes.ContainsKey(node.Name))
        {
            Logging.Log.Warning("Script",
                $"Enum '{node.Name}' already defined. Skipping.",
                node.Line, node.Column);
            return ExecutionResult.Continue();
        }

        // Evaluate member values, auto-incrementing from last explicit value
        var staticFields = new Dictionary<string, object?>(_comparer);
        long nextVal = 0;
        foreach (var (memberName, init) in node.Members)
        {
            if (init != null)
            {
                var val = Evaluate(init);
                nextVal = Convert.ToInt64(val);
            }
            staticFields[memberName] = new EnumValue(node.Name, nextVal);
            nextVal++;
        }

        var enumClass = new ClassInfo(node.Name, null, [], [], [], [], [],
            new Dictionary<TokenType, FuncDecl>(), node.Line, AccessModifier.Public)
        {
            StaticFields = staticFields
        };
        Classes[node.Name] = enumClass;
        RecordDeclaredBySource(node.Name);
        CurrentFrame.Scopes.Define(node.Name, enumClass);
        if (_recordingExports && _currentExports != null)
            _currentExports.TryAdd(node.Name, enumClass);
        return ExecutionResult.Continue();
    }

    /// <summary>FP: bind `source` into the given names, destructure Array/ScriptTuple/IScriptList
    /// elements or ScriptKeyValuePair Key/Value (lenient arity: extra names stay unbound, missing
    /// elements are skipped). Shared by destructuring statements and foreach (var (a, b) in ...).
    /// Underscore ("_") names are discards and are not bound. Mirrors the pre-existing
    /// VisitDestructuringStmt semantics.</summary>
    private void DestructureBind(object? source, IReadOnlyList<string> names, int line, int col)
    {
        if (source is Array arr)
        {
            for (var i = 0; i < names.Count && i < arr.Length; i++)
            {
                if (names[i] == "_") continue;
                CurrentFrame.Scopes.Define(names[i], arr.GetValue(i), line, col);
            }
        }
        else if (source is ScriptTuple tup)
        {
            for (var i = 0; i < names.Count && i < tup.Length; i++)
            {
                if (names[i] == "_") continue;
                CurrentFrame.Scopes.Define(names[i], tup[i], line, col);
            }
        }
        else if (source is IScriptList list)
        {
            for (var i = 0; i < names.Count && i < list.Count; i++)
            {
                if (names[i] == "_") continue;
                CurrentFrame.Scopes.Define(names[i], list[i], line, col);
            }
        }
        else if (source is ScriptKeyValuePair kvp)
        {
            if (names.Count >= 1 && names[0] != "_")
                CurrentFrame.Scopes.Define(names[0], kvp.Key, line, col);
            if (names.Count >= 2 && names[1] != "_")
                CurrentFrame.Scopes.Define(names[1], kvp.Value, line, col);
        }
        else if (source is IScriptHashSet)
        {
            RuntimeError("Cannot destructure a HashSet. Use foreach to enumerate elements.", line, col);
        }
        else
        {
            RuntimeError($"Cannot destructure type '{source?.GetType().Name ?? "null"}'. Expected array, list, tuple or KeyValuePair.", line, col);
        }
    }

    public ExecutionResult VisitDestructuringStmt(DestructuringStmt node)
    {
        var source = Evaluate(node.Source);
        if (ReferenceEquals(source, YieldSentinel)) return default;

        DestructureBind(source, node.VarNames, node.Line, node.Column);
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitSwitchStmt(SwitchStmt node)
    {
        var subject = Evaluate(node.Subject);
        if (ReferenceEquals(subject, YieldSentinel)) return default;

        // Track switch nesting for break semantics

        try
        {
            foreach (var clause in node.Cases)
            {
                foreach (var pattern in clause.Patterns)
                {
                    if (!PatternMatches(pattern, subject, out var bindings)) continue;
                    if (clause.Guard != null)
                    {
                        CurrentFrame.Scopes.PushScope();
                        try
                        {
                            foreach (var (name, value) in bindings)
                                CurrentFrame.Scopes.Define(name, value);
                            if (!IsTruthy(Evaluate(clause.Guard)))
                                goto nextClause;
                        }
                        finally { CurrentFrame.Scopes.PopScope(); }
                    }

                    CurrentFrame.Scopes.PushScope();
                    try
                    {
                        foreach (var (name, value) in bindings)
                            CurrentFrame.Scopes.Define(name, value);

                        foreach (var t in clause.Body)
                        {
                            var result = Execute(t);
                            switch (result.Flow)
                            {
                                case ControlFlow.Return or ControlFlow.Throw:
                                    return result;

                                case ControlFlow.Break:
                                    return ExecutionResult.Continue();
                            }

                            if (result.Flow == ControlFlow.ContinueLoop)
                                break;
                        }
                    }
                    finally { CurrentFrame.Scopes.PopScope(); }
                    return ExecutionResult.Continue();
                }
            nextClause:;
            }
        }
        finally
        {
        }

        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitStructDeclStmt(StructDeclStmt node)
    {
        RecordNamespacePath(node.Name, node.Line, node.Column);
        // Generic struct template, store without registering as concrete
        if (node.TypeParameters.Count > 0)
        {
            if (_genericClassTemplates.ContainsKey(node.Name))
            {
                Logging.Log.Warning("Script",
                    $"Generic struct '{node.Name}' already defined. Skipping.",
                    node.Line, node.Column);
                return ExecutionResult.Continue();
            }
            CurrentFrame.Scopes.Define(node.Name, StructDefinitionHandle(node.Name));
            return ExecutionResult.Continue();
        }

        // The promise a struct declares is checked when the declaration EXECUTES, before any value exists, and an
        // explicit implementation leaves the struct name table here (see FindExplicitMember). The merge makes a
        // second pass over an already-split struct harmless rather than wiping what the first pass collected.
        ApplyStructInterfaces(node);
        node.ExplicitMembers = node.ExplicitMembers.Concat(node.Methods.Where(m => m.Qualifier != null)).ToList();
        node.Methods = node.Methods.Where(m => m.Qualifier == null).ToList();
        if (Structs.TryAdd(node.Name, node))
        {
            // Generate backing fields for auto-properties on structs
            var expandedFields = new List<FieldDecl>(node.Fields);
            expandedFields.AddRange(node.Properties.Select(prop
                => new FieldDecl(prop.Access, prop.Type, BackingFieldName(prop.Name), null, prop.IsStatic)));
            node.Fields = expandedFields;

            // Evaluate static field initializers for structs
            var staticFields = new Dictionary<string, object?>(_comparer);
            foreach (var f in node.Fields.Where(f => f.IsStatic))
            {
                var val = f.Initializer != null ? Evaluate(f.Initializer) : DefaultFor(f.Type);
                staticFields[f.Name] = val is ScriptFunction ? val : CoerceTo(val, f.Type, node.Line, node.Column);
            }
            if (staticFields.Count > 0)
                StructStaticFields[node.Name] = staticFields;

            CurrentFrame.Scopes.Define(node.Name, StructDefinitionHandle(node.Name));
            if (_recordingExports && _currentExports != null)
                _currentExports.TryAdd(node.Name, node);

            // Register nested classes and structs
            foreach (var nc in node.NestedClasses)
                VisitClassDeclStmt(nc);
            foreach (var ns in node.NestedStructs)
                VisitStructDeclStmt(ns);

            return ExecutionResult.Continue();
        }
        Logging.Log.Warning("Script",
            $"Struct '{node.Name}' already defined. Skipping.",
            node.Line, node.Column);
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitImportStmt(ImportStmt node)
    {
        // A LITERAL is taken as it was written; a COMPUTED path is evaluated HERE, where the import runs, which is
        // what makes `import Application.Path + "/m.inc" as Z;` possible: the executable's directory is only known
        // at run time. The VALUE is then checked by the ONE rule both doors ask (see RequireImportPathText): a
        // top-level import is compiled into __run_import__ on the VM leg, so a rule stated here alone would leave
        // that leg reporting the helper's generic guard instead of the engine's message.
        var pathText = node.LiteralPath ?? RequireImportPathText(Evaluate(node.PathExpression), node.Line, node.Column);
        // An import resolves as a SEARCH (see ResolveImportPath), which is what makes a suite's own directory a
        // usable place to run it from, whichever spelling its import lines use.
        var resolvedPath = ResolveImportPath(pathText);
        if (resolvedPath.EndsWith(".slcbc", StringComparison.OrdinalIgnoreCase))
        {
            if (node.Alias == null)
                throw new ScriptException(ErrorCode.Unspecified,
                    "import (bytecode library): a bytecode library must be imported with an alias, 'import \"lib.slcbc\" as Z'.",
                    node.Line, node.Column);
            var ns = LoadBundleToNamespace(resolvedPath, node.Line, node.Column, node.Alias!);
            // The STATEMENT binds its alias in scope; the loader cannot, since `Bytecode.LoadNamespace` is an
            // expression and a call does not define names. What both share is the label itself: the loader names
            // the namespace and makes it reachable as that name through the value (`Z.What` IS `Z`).
            CurrentFrame.Scopes.Define(node.Alias!, ns);
            return ExecutionResult.Continue();
        }
        if (!Modules.TryGetValue(resolvedPath, out var moduleInfo))
        {
            if (_loadingModules.Contains(resolvedPath))
            {
                Logging.Log.Warning("Script",
                    $"Circular import detected for '{resolvedPath}'.",
                    node.Line, 0);
                moduleInfo = new ModuleInfo(new Dictionary<string, object?>(), false);
                Modules[resolvedPath] = moduleInfo;
            }
            else
                moduleInfo = LoadModule(resolvedPath, node.Line, node.Column);
        }

        if (!moduleInfo.IsEvaluated && !_loadingModules.Contains(resolvedPath))
            return ExecutionResult.Continue();

        if (node.Alias != null)
        {
            var ns = new Namespace(_comparer);
            foreach (var (name, value) in moduleInfo.Exports)
            {
                // Declared namespace path ("A.B") → nest under the alias: Z.A.B.member.
                var nsPath = moduleInfo.ExportPaths != null
                    && moduleInfo.ExportPaths.TryGetValue(name, out var p) && p.Length > 0 ? p : null;
                var target = ns;
                if (nsPath != null)
                    foreach (var seg in nsPath.Split('.'))
                        target = target.GetChild(seg) ?? CreateNamespaceChild(target, seg);

                // Qualified class/struct/delegate registration: Z.<path>.<Name>.
                var qualifiedName = nsPath != null
                    ? $"{node.Alias}.{nsPath}.{name}"
                    : $"{node.Alias}.{name}";
                // Classes carry their qualified registration name (mirrors the bytecode-library
                // import path): ClassInfo.Name matches the Classes key, so static-member and
                // inheritance lookups keyed on ClassInfo.Name stay consistent.
                var classValue = value is ClassInfo aci
                    ? QualifyImportedClass(aci, qualifiedName, node.Alias + ".", moduleInfo)
                    : value!;

                if (value is ScriptFunction fn)
                {
                    target.AddMethod(name, args => InvokeScriptFunction(fn, args, node.Line, node.Column));
                    target.AddProperty(name, fn);
                }
                else if (value is FuncDecl decl)
                {
                    var globals = new Dictionary<string, object?>(
                        moduleInfo.ModuleGlobals ?? new Dictionary<string, object?>(_comparer), _comparer);
                    var snapshot = new ClosureSnapshot(
                        new List<Dictionary<string, object?>>(),
                        new List<HashSet<string>>(),
                        globals,
                        [with(_comparer)]);
                    var paramTypes = GetParameterTypes(decl.Params);
                    var fnt = new ScriptFunction(decl, snapshot, decl.ReturnType, paramTypes,
                        decl.IsRefParams, IsOutParams: decl.IsOutParams);
                    target.AddMethod(name, args => InvokeScriptFunction(fnt, args, node.Line, node.Column));
                    target.AddProperty(name, fnt);
                }
                else
                    target.AddProperty(name, classValue);

                switch (value)
                {
                    case ClassInfo:
                        Classes[qualifiedName] = (ClassInfo)classValue;
                        break;
                    case StructDeclStmt sd:
                        Structs[qualifiedName] = sd;
                        break;
                    case DelegateInfo di:
                        DelegateInfos[qualifiedName] = di;
                        break;
                }
            }
            CurrentFrame.Scopes.Define(node.Alias!, ns);
        }
        else
        {
            foreach (var (name, value) in moduleInfo.Exports)
            {
                var nsPath = moduleInfo.ExportPaths != null
                    && moduleInfo.ExportPaths.TryGetValue(name, out var p) && p.Length > 0 ? p : null;
                if (nsPath != null)
                {
                    // Walk/create nested namespaces in the importer scope: My → Include.
                    Namespace current = null!;
                    var segments = nsPath.Split('.');
                    for (var si = 0; si < segments.Length; si++)
                    {
                        var seg = segments[si];
                        Namespace? child = null;
                        if (si == 0)
                        {
                            if (CurrentFrame.Scopes.TryGet(seg, out var rootVal) && rootVal is Namespace rootNs)
                                // F1: a root that collides with a shared builtin namespace must be
                                // cloned copy-on-write BEFORE members are bound into it, otherwise
                                // AddMethod/AddProperty land on the process-wide shared object (e.g.
                                // Json) and leak into every other interpreter. The clone covers the
                                // whole subtree and rebinds the importer scope, so it is deterministic
                                // regardless of which export walks this path first.
                                child = EnterNamespaceRoot(rootNs, seg);
                            else
                            {
                                child = new Namespace(_comparer);
                                CurrentFrame.Scopes.Define(seg, child);
                            }
                        }
                        else if (current.GetChild(seg) is { } existingChild)
                            child = existingChild;
                        else
                            child = CreateNamespaceChild(current, seg);

                        current = child!;
                    }
                    var nsQualified = $"{nsPath}.{name}";
                    var classValue = value is ClassInfo nci
                        ? QualifyImportedClass(nci, nsQualified, "", moduleInfo)
                        : value!;
                    BindModuleValue(current, name, classValue, node, moduleInfo.ModuleGlobals);
                    switch (value)
                    {
                        case ClassInfo:
                            Classes[nsQualified] = (ClassInfo)classValue;
                            break;
                        case StructDeclStmt sd:
                            Structs[nsQualified] = sd;
                            break;
                        case DelegateInfo di:
                            DelegateInfos[nsQualified] = di;
                            break;
                    }
                    continue;
                }

                BindModuleValue(CurrentFrame.Scopes, name, value, node, moduleInfo.ModuleGlobals);
                switch (value)
                {
                    case ClassInfo ci: Classes[name] = ci; break;
                    case StructDeclStmt sd:
                        // The promise is checked when the declaration executes, and an explicit member leaves the
                        // struct name table here, exactly as it does for one declared inside a class.
                        Structs[name] = sd;
                        break;
                    case DelegateInfo di: DelegateInfos[name] = di; break;
                }
            }
        }
        return ExecutionResult.Continue();
    }

    /// <summary>Binds one module export into a namespace target (the alias branch and the
    /// no-alias nested-namespace path). Script functions are registered both as a callable
    /// method and as a value, each dispatched through InvokeScriptFunction with the import
    /// site's line/column.</summary>
    private void BindModuleValue(Namespace target, string name, object? value, ImportStmt node,
        IReadOnlyDictionary<string, object?>? moduleGlobals)
    {
        if (value is ScriptFunction fn)
        {
            fn.NamespaceContext = target;
            target.AddMethod(name, args => InvokeScriptFunction(fn, args, node.Line, node.Column));
            target.AddProperty(name, fn);
        }
        else if (value is FuncDecl decl)
        {
            // FuncDecl exports (generic templates) get the module's flat scope so their free
            // variables resolve in the defining module too.
            var globals = new Dictionary<string, object?>(
                moduleGlobals ?? new Dictionary<string, object?>(_comparer), _comparer);
            var snapshot = new ClosureSnapshot(
                new List<Dictionary<string, object?>>(),
                new List<HashSet<string>>(),
                globals,
                [with(_comparer)]);
            var paramTypes = GetParameterTypes(decl.Params);
            var fnt = new ScriptFunction(decl, snapshot, decl.ReturnType, paramTypes,
                decl.IsRefParams, IsOutParams: decl.IsOutParams);
            fnt.IsModuleScope = true;   // FuncDecl exports: module's flat scope is authoritative
            fnt.NamespaceContext = target;
            target.AddMethod(name, args => InvokeScriptFunction(fnt, args, node.Line, node.Column));
            target.AddProperty(name, fnt);
        }
        else
            target.AddProperty(name, value);
    }

    /// <summary>Binds one module export directly into the importer scope (flat no-alias
    /// import). Scope-stored ScriptFunctions are dispatched through InvokeScriptFunction at
    /// call time, so the binding semantics match the Namespace overload.</summary>
    private void BindModuleValue(ScopeStack target, string name, object? value, ImportStmt node,
        IReadOnlyDictionary<string, object?>? moduleGlobals)
    {
        target.DefineConst(name, value);
    }

    /// <summary>Mirrors the bytecode-library import path (Interpreter.LoadBundleToNamespace):
    /// a class registered under a qualified name carries that qualified name on its ClassInfo,
    /// and its ParentName is qualified when the parent is a class/struct exported by the same
    /// module (under the same import prefix), so static-member and inheritance lookups keyed on
    /// ClassInfo.Name stay consistent with the Classes dictionary keys built by this import.
    /// <paramref name="prefix"/> is the alias + '.' (alias import) or "" (no-alias import).</summary>
    private ClassInfo QualifyImportedClass(ClassInfo ci, string qualifiedName, string prefix, ModuleInfo moduleInfo)
    {
        string? qualifiedParent = null;
        if (ci.ParentName is { Length: > 0 } pn
            && moduleInfo.Exports.TryGetValue(pn, out var pv)
            && (pv is ClassInfo or StructDeclStmt))
        {
            var parentNsPath = moduleInfo.ExportPaths != null
                && moduleInfo.ExportPaths.TryGetValue(pn, out var pp) && pp.Length > 0 ? pp : null;
            qualifiedParent = parentNsPath is { Length: > 0 }
                ? prefix + parentNsPath + "." + pn
                : prefix + pn;
        }
        return ci with { Name = qualifiedName, ParentName = qualifiedParent };
    }

    public ExecutionResult VisitNamespaceDeclStmt(NamespaceDeclStmt node)
    {
        // Guard: a namespace root must not collide with a NON-namespace member. Colliding with
        // a Namespace (builtin or user) is a merge, shared builtins are cloned copy-on-write
        // via EnterNamespaceRoot; user namespaces are entered directly.
        if (node.Segments.Count > 0 && _currentNamespaceLeaf == null
            && CurrentFrame.Scopes.TryGet(node.Segments[0], out var existing) && existing != null
            && existing is not Namespace)
            RuntimeError($"Namespace '{node.Name}' collides with existing member '{node.Segments[0]}'.",
                node.Line, node.Column);

        // F2 gate: modules/libraries keep v1 behavior, members are re-exported through
        // RecordNamespacePath and the import/export machinery (no scope chain, no root
        // binding), so a module with several `namespace A.B;`/`namespace A.C;` statements
        // never collides and the chain object never leaks into the module's export snapshot.
        // F1: a root that collides with an existing Namespace (builtin shared or user) is a
        // MERGE in module mode too, shared builtins are cloned copy-on-write so the module's
        // extension registers into a module-private copy (via the leaf) that never leaks into
        // the importer's scope. New roots keep v1 (path recording only).
        if (IsModuleExecution)
        {
            var savedLeafModule = _currentNamespaceLeaf;
            if (savedLeafModule == null && node.Segments.Count > 0
                && CurrentFrame.Scopes.TryGet(node.Segments[0], out var existingRoot) && existingRoot is Namespace existingNs)
            {
                var leaf = EnterNamespaceRoot(existingNs, node.Segments[0]);
                for (var i = 1; i < node.Segments.Count; i++)
                    leaf = leaf.GetChild(node.Segments[i]) ?? CreateNamespaceChild(leaf, node.Segments[i]);
                _currentNamespaceLeaf = leaf;
            }
            var saved = _namespaceSegments.Count;
            foreach (var seg in node.Segments) _namespaceSegments.Add(seg);
            try
            {
                foreach (var stmt in node.Body) Execute(stmt);
            }
            finally
            {
                _namespaceSegments.RemoveRange(saved, _namespaceSegments.Count - saved);
                _currentNamespaceLeaf = savedLeafModule;
            }
            return ExecutionResult.Continue();
        }

        // F2: build the namespace chain in the declaring scope so qualified access resolves
        // in the declaring file. Nested declarations continue the enclosing leaf.
        var savedSegments = _namespaceSegments.Count;
        var savedLeaf = _currentNamespaceLeaf;
        try
        {
            var leaf = _currentNamespaceLeaf;
            var start = 0;
            if (leaf == null && node.Segments.Count > 0)
            {
                if (CurrentFrame.Scopes.TryGet(node.Segments[0], out var existingRoot) && existingRoot is Namespace ns0)
                    leaf = EnterNamespaceRoot(ns0, node.Segments[0]);
                else
                {
                    leaf = new Namespace(_comparer, node.Segments[0]);
                    CurrentFrame.Scopes.Define(node.Segments[0], leaf);
                }
                start = 1;
            }
            for (var i = start; i < node.Segments.Count; i++)
            {
                var seg = node.Segments[i];
                var child = leaf!.GetChild(seg);
                if (child == null)
                {
                    child = new Namespace(_comparer, seg);
                    leaf!.AddChild(seg, child);
                }
                leaf = child;
            }

            _currentNamespaceLeaf = leaf;
            foreach (var seg in node.Segments) _namespaceSegments.Add(seg);
            foreach (var stmt in node.Body) Execute(stmt);
        }
        finally
        {
            _namespaceSegments.RemoveRange(savedSegments, _namespaceSegments.Count - savedSegments);
            _currentNamespaceLeaf = savedLeaf;
        }
        return ExecutionResult.Continue();
    }

    /// <summary>F1: the Namespace object a top-level namespace root resolves to. Shared
    /// builtin roots are cloned first (copy-on-write) so extending `namespace Json` never
    /// mutates the process-wide shared object; user namespaces are entered directly.</summary>
    private Namespace EnterNamespaceRoot(Namespace existing, string name)
    {
        if (!existing.IsShared) return existing;
        var clone = existing.Clone();
        CurrentFrame.Scopes.Define(name, clone);
        return clone;
    }

    /// <summary>Records a top-level declaration's namespace path ("" when no namespace is
    /// active). Only fires for top-level declarations, class members and local functions
    /// have _currentClass/_currentFuncDecl set and are skipped.</summary>
    private void RecordNamespacePath(string name, int line, int column)
    {
        if (_namespaceSegments.Count == 0 || _currentFuncDecl != null || _currentClass != null)
            return;
        if (name.Length == 0) return;
        _declaredInNamespace[name] = string.Join(".", _namespaceSegments);
    }

    public ExecutionResult VisitDelegateDecl(DelegateDecl node)
    {
        RecordNamespacePath(node.Name, node.Line, node.Column);
        var delegateInfo = new DelegateInfo(node.Name, node.ReturnType, node.Parameters, node.IsRefParams, node.Access, node.Line, node.Column, IsOutParams: node.IsOutParams);
        DelegateInfos[node.Name] = delegateInfo;
        RecordDeclaredBySource(node.Name);
        CurrentFrame.Scopes.Define(node.Name, delegateInfo);
        if (_currentNamespaceLeaf != null && _currentFuncDecl == null && _currentClass == null)
            _currentNamespaceLeaf.AddProperty(node.Name, delegateInfo);
        if (_recordingExports && _currentExports != null)
            _currentExports.TryAdd(node.Name, delegateInfo);
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitEventDecl(EventDecl node) => ExecutionResult.Continue();

    public ExecutionResult VisitReturnStmt(ReturnStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        _returnExprDepth = 1;
        var value = node.Value != null ? Evaluate(node.Value) : null;
        _returnExprDepth = 0;
        if (_callFrames.Value!.Count == 1)
            _lastExpressionValue = value;
        return ExecutionResult.Return(value);
    }

    public ExecutionResult VisitBlockStmt(BlockStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        if (_currentCoroutine == null)
        {
            CurrentFrame.Scopes.PushScope();
            try
            {
                // Declarations are in scope for the WHOLE block, not only from their statement (C# hoisting): a closure
                // assigned to a control above `void Helper(...)` must still be able to name it.
                PrebindDeclaredFunctions(node.Statements);
                foreach (var stmt in node.Statements)
                {
                    var result = Execute(stmt);
                    if (result.Flow != ControlFlow.Continue)
                        return result;
                }
            }
            finally { CurrentFrame.Scopes.PopScope(); }
            return ExecutionResult.Continue();
        }

        // Coroutine-aware path: a `yield` inside a plain { } block must resume INSIDE the block,
        // so the block gets a LoopType.Block frame (mirroring the loop machinery). Resume-identity
        // is the AST node, the shared _loopStack carries frames of OUTER coroutines below our
        // LoopBaseSp and inner frames above, so scan from the top down to our own base.
        var scanBase = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        var isResuming = false;
        int loopIdx = -1;
        if (_isResumingFromYield && _loopStackSp > scanBase)
        {
            for (var si = _loopStackSp - 1; si >= scanBase; si--)
            {
                if (_loopStack[si].Type == LoopType.Block && ReferenceEquals(_loopStack[si].LoopNode, node))
                {
                    isResuming = true;
                    loopIdx = si;
                    break;
                }
            }
        }

        if (!isResuming)
        {
            if (_loopStackSp >= _loopStack.Length)
                RuntimeError("Loop nesting too deep.", node.Line, node.Column);
            loopIdx = _loopStackSp++;
            _loopStack[loopIdx] = new LoopFrame
            {
                Type = LoopType.Block,
                Phase = 0,
                BodyStatementIndex = 0,
                LoopNodeStatementIndex = _currentStatementIndex,
                LoopNode = node,
                ScopeActive = false
            };
        }

        ref var currentLoop = ref _loopStack[loopIdx];

        try
        {
            while (true)
            {
                if (currentLoop.Phase == 0)
                {
                    if (!currentLoop.ScopeActive)
                    {
                        CurrentFrame.Scopes.PushScope();
                        currentLoop.ScopeActive = true;
                    }
                    currentLoop.Phase = 1;
                }

                if (currentLoop.Phase == 1)
                {
                    for (var i = currentLoop.BodyStatementIndex; i < node.Statements.Count; i++)
                    {
                        EnterStatement(i);
                        var result = Execute(node.Statements[i]);
                        switch (result.Flow)
                        {
                            case ControlFlow.Yield:
                                if (loopIdx == _loopStackSp - 1)
                                {
                                    // leaf yield: VisitYieldExpr already saved BodyStatementIndex = statement + 1
                                    currentLoop.Phase = 1;
                                }
                                else
                                {
                                    // Nested frame yielded through us: remember where in OUR list we
                                    // were so the resume re-executes that statement (which drives the
                                    // nested frame) instead of replaying earlier statements.
                                    currentLoop.BodyStatementIndex = i;
                                }
                                return result;

                            case ControlFlow.Return:
                            case ControlFlow.Throw:
                            case ControlFlow.Break:
                            case ControlFlow.ContinueLoop:
                                return result;
                        }
                    }
                    if (currentLoop.Phase == 1) currentLoop.Phase = 3;
                }
                if (currentLoop.Phase == 3) break;
            }
        }
        finally
        {
            // Pop the block frame on ANY non-yield exit, including when the block completes
            // during a resume. A stale Block frame left on the stack would keep _loopStackSp
            // above LoopBaseSp and make a LATER yield resume at this block's
            // LoopNodeStatementIndex forever (mirrors the while/for fix).
            if (!_isYield && _loopStackSp > 0)
            {
                if (currentLoop.ScopeActive)
                {
                    CurrentFrame.Scopes.PopScope();
                    currentLoop.ScopeActive = false;
                }
                _loopStackSp--;
            }
        }
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitIfStmt(IfStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        if (_currentCoroutine == null)
        {
            // The condition may NEST: an inner if that binds a pattern used to leave _condBinding false for the
            // ENCLOSING condition, so its branch saw binding state that was not its own and a value-returning body
            // fell off its end (measured: the same body works when the pattern sits at the top level of the function).
            // The flag is saved and put back; the PENDING arrays are left alone on purpose, because the branch reads
            // them AFTER this returns and the consumer clears them.
            var savedCondBinding = _condBinding;
            _condBinding = true;
            var condResult = Evaluate(node.Condition);
            _condBinding = savedCondBinding;
            if (IsTruthy(condResult))
            {
                CurrentFrame.Scopes.PushScope();
                try
                {
                    ApplyPatternBindings(node.Line, node.Column);
                    PrebindDeclaredFunctions(node.ThenBranch);
                    foreach (var stmt in node.ThenBranch)
                    {
                        var result = Execute(stmt);
                        if (result.Flow != ControlFlow.Continue)
                            return result;
                    }
                }
                finally { CurrentFrame.Scopes.PopScope(); }
            }
            else if (node.ElseBranch.Count > 0)
            {
                CurrentFrame.Scopes.PushScope();
                try
                {
                    PrebindDeclaredFunctions(node.ElseBranch);
                    foreach (var stmt in node.ElseBranch)
                    {
                        var result = Execute(stmt);
                        if (result.Flow != ControlFlow.Continue)
                            return result;
                    }
                }
                finally { CurrentFrame.Scopes.PopScope(); }
            }
            return ExecutionResult.Continue();
        }

        // Coroutine-aware path: `if (c) { yield ... }` must resume INSIDE the chosen branch
        // (InvokeCoroutineBody resumes at this if's LoopNodeStatementIndex, not past it). The
        // branch gets a LoopType.Block frame keyed on the IfStmt node. Phase records which
        // branch is active (1 = then, 2 = else) so a resume continues the SAME branch without
        // re-evaluating the condition; the branch scope survives the yield via ScopeActive.
        var scanBase = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        var isResuming = false;
        int loopIdx = -1;
        if (_isResumingFromYield && _loopStackSp > scanBase)
        {
            for (var si = _loopStackSp - 1; si >= scanBase; si--)
            {
                if (_loopStack[si].Type == LoopType.Block && ReferenceEquals(_loopStack[si].LoopNode, node))
                {
                    isResuming = true;
                    loopIdx = si;
                    break;
                }
            }
        }

        if (!isResuming)
        {
            if (_loopStackSp >= _loopStack.Length)
                RuntimeError("Loop nesting too deep.", node.Line, node.Column);
            loopIdx = _loopStackSp++;
            _loopStack[loopIdx] = new LoopFrame
            {
                Type = LoopType.Block,
                Phase = 0,
                BodyStatementIndex = 0,
                LoopNodeStatementIndex = _currentStatementIndex,
                LoopNode = node,
                ScopeActive = false
            };
        }

        ref var currentLoop = ref _loopStack[loopIdx];

        try
        {
            // Phase 0 = branch not yet selected, evaluate the condition (fresh visit, or a
            // resume after a yield inside the condition itself, where no branch statement has
            // ever run, see the _isYield re-arm below).
            if (currentLoop.Phase == 0)
            {
                // The condition may NEST: an inner if that binds a pattern used to leave _condBinding false for the
                // ENCLOSING condition, so its branch saw binding state that was not its own and a value-returning body
                // fell off its end (measured: the same body works when the pattern sits at the top level of the function).
                // The flag is saved and put back; the PENDING arrays are left alone on purpose, because the branch reads
                // them AFTER this returns and the consumer clears them.
                var savedCondBinding = _condBinding;
                _condBinding = true;
                var condResult = Evaluate(node.Condition);
                _condBinding = savedCondBinding;
                if (IsTruthy(condResult)) currentLoop.Phase = node.ThenBranch.Count > 0 ? 1 : 3;
                else if (node.ElseBranch.Count > 0) currentLoop.Phase = 2;
                else currentLoop.Phase = 3;
                // The condition itself yielded (yield-as-expression) before any branch
                // statement ran. VisitYieldExpr wrote _currentStatementIndex + 1, an index
                // into the ENCLOSING statement list, into this frame's BodyStatementIndex;
                // that index is meaningless for the branch lists and would make a resume skip
                // the branch entirely. Re-arm it to 0 so the resume runs the chosen branch
                // from its FIRST statement (mirrors the loop visitors, which also reset
                // BodyStatementIndex=0 after the condition step). The condition result is
                // gone, so the branch is whichever the sentinel's truthiness picked (then,
                // since the sentinel is non-null); we deliberately do NOT re-evaluate the
                // condition on resume, that would re-run the yield's side effects (and, for
                // a generator, re-yield the same value). Same trade-off the loop visitors
                // accept for a yielding condition.
                if (_isYield) currentLoop.BodyStatementIndex = 0;
            }

            // Yield inside the condition: suspend now, the branch must not run on this pass.
            // The frame keeps the branch phase chosen above and BodyStatementIndex = 0, so the
            // next resume starts the branch at its first statement instead of skipping it.
            if (_isYield) return ExecutionResult.Yielded(_yieldValue);

            while (true)
            {
                if (currentLoop.Phase == 1)
                {
                    if (!currentLoop.ScopeActive)
                    {
                        CurrentFrame.Scopes.PushScope();
                        currentLoop.ScopeActive = true;
                        ApplyPatternBindings(node.Line, node.Column);
                    }
                    for (var i = currentLoop.BodyStatementIndex; i < node.ThenBranch.Count; i++)
                    {
                        EnterStatement(i);
                        var result = Execute(node.ThenBranch[i]);
                        switch (result.Flow)
                        {
                            case ControlFlow.Yield:
                                if (loopIdx == _loopStackSp - 1)
                                {
                                    // leaf yield: VisitYieldExpr already saved BodyStatementIndex = statement + 1
                                    currentLoop.Phase = 1;
                                }
                                else
                                {
                                    // Nested frame yielded through us: resume re-executes this statement
                                    currentLoop.BodyStatementIndex = i;
                                }
                                return result;

                            case ControlFlow.Return:
                            case ControlFlow.Throw:
                            case ControlFlow.Break:
                            case ControlFlow.ContinueLoop:
                                return result;
                        }
                    }
                    if (currentLoop.Phase == 1) currentLoop.Phase = 3;
                }
                else if (currentLoop.Phase == 2)
                {
                    if (!currentLoop.ScopeActive)
                    {
                        CurrentFrame.Scopes.PushScope();
                        currentLoop.ScopeActive = true;
                    }
                    for (var i = currentLoop.BodyStatementIndex; i < node.ElseBranch.Count; i++)
                    {
                        EnterStatement(i);
                        var result = Execute(node.ElseBranch[i]);
                        switch (result.Flow)
                        {
                            case ControlFlow.Yield:
                                if (loopIdx == _loopStackSp - 1)
                                {
                                    currentLoop.Phase = 2;
                                }
                                else
                                {
                                    currentLoop.BodyStatementIndex = i;
                                }
                                return result;

                            case ControlFlow.Return:
                            case ControlFlow.Throw:
                            case ControlFlow.Break:
                            case ControlFlow.ContinueLoop:
                                return result;
                        }
                    }
                    if (currentLoop.Phase == 2) currentLoop.Phase = 3;
                }
                if (currentLoop.Phase == 3) break;
            }
        }
        finally
        {
            // Pop on any non-yield exit (mirrors the block/while/for gate), a stale frame would
            // keep the next yield in this function resuming at this if's LoopNodeStatementIndex.
            if (!_isYield && _loopStackSp > 0)
            {
                if (currentLoop.ScopeActive)
                {
                    CurrentFrame.Scopes.PopScope();
                    currentLoop.ScopeActive = false;
                }
                _loopStackSp--;
            }
        }
        return ExecutionResult.Continue();
    }

    public ExecutionResult VisitWhileStmt(WhileStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        var isCoroutine = _currentCoroutine != null;

        if (!isCoroutine)
        {
            using var loopScope = EnterTreeLoop();
            // Fast path: flat for-loop body, scope per iteration
            var body = node.Body;
            // The condition may NEST: an inner if that binds a pattern used to leave _condBinding false for the
            // ENCLOSING condition, so its branch saw binding state that was not its own and a value-returning body
            // fell off its end (measured: the same body works when the pattern sits at the top level of the function).
            // The flag is saved and put back; the PENDING arrays are left alone on purpose, because the branch reads
            // them AFTER this returns and the consumer clears them.
            var savedCondBinding = _condBinding;
            _condBinding = true;
            var runIter = IsTruthy(Evaluate(node.Condition));
            _condBinding = savedCondBinding;
            while (runIter)
            {
                CurrentFrame.Scopes.PushScope();
                try
                {
                    ApplyPatternBindings(node.Line, node.Column);
                    PrebindDeclaredFunctions(body);
                    foreach (var t in body)
                    {
                        var result = Execute(t);
                        switch (result.Flow)
                        {
                            case ControlFlow.Return:
                            case ControlFlow.Throw:
                                return result;

                            case ControlFlow.Break:
                                return ExecutionResult.Continue();
                        }

                        if (result.Flow == ControlFlow.ContinueLoop) break;
                    }
                }
                finally { CurrentFrame.Scopes.PopScope(); }
                // The condition may NEST: an inner if that binds a pattern used to leave _condBinding false for the
                // ENCLOSING condition, so its branch saw binding state that was not its own and a value-returning body
                // fell off its end (measured: the same body works when the pattern sits at the top level of the function).
                // The flag is saved and put back; the PENDING arrays are left alone on purpose, because the branch reads
                // them AFTER this returns and the consumer clears them.
                var savedCondBinding2 = _condBinding;
                _condBinding = true;
                runIter = IsTruthy(Evaluate(node.Condition));
                _condBinding = savedCondBinding2;
            }
            return ExecutionResult.Continue();
        }

        // Resume-identity: match the resumed while by AST node (see VisitForStmt note,
        // nested loops at the same body position share statement indices but never the node).
        var scanBase = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        var isResuming = false;
        int loopIdx = -1;
        if (_isResumingFromYield && _loopStackSp > scanBase)
        {
            // Scan only THIS coroutine's frames (index >= LoopBaseSp): the shared _loopStack
            // carries outer-coroutine frames below, and a recursive generator's first while
            // must not false-match an outer coroutine's frame (same misrouting class as the
            // foreach fix).
            for (var si = _loopStackSp - 1; si >= scanBase; si--)
            {
                if (_loopStack[si].Type == LoopType.While && ReferenceEquals(_loopStack[si].LoopNode, node))
                {
                    isResuming = true;
                    loopIdx = si;
                    break;
                }
            }
        }

        if (!isResuming)
        {
            if (_loopStackSp >= _loopStack.Length)
                RuntimeError("Loop nesting too deep.", node.Line, node.Column);
            loopIdx = _loopStackSp++;
            _loopStack[loopIdx] = new LoopFrame
            {
                Type = LoopType.While,
                Phase = 0,
                BodyStatementIndex = 0,
                YieldedBodyIndex = -1,
                LoopNodeStatementIndex = _currentStatementIndex,
                LoopNode = node,
                ScopeActive = false
            };
        }

        ref var currentLoop = ref _loopStack[loopIdx];

        try
        {
            while (true)
            {
                if (currentLoop.Phase == 0)
                {
                    // The condition may NEST: an inner if that binds a pattern used to leave _condBinding false for the
                    // ENCLOSING condition, so its branch saw binding state that was not its own and a value-returning body
                    // fell off its end (measured: the same body works when the pattern sits at the top level of the function).
                    // The flag is saved and put back; the PENDING arrays are left alone on purpose, because the branch reads
                    // them AFTER this returns and the consumer clears them.
                    var savedCondBinding = _condBinding;
                    _condBinding = true;
                    var wCond = IsTruthy(Evaluate(node.Condition));
                    _condBinding = savedCondBinding;
                    if (!wCond) break;
                    currentLoop.Phase = 1;
                    if (currentLoop.YieldedBodyIndex >= 0)
                    {
                        // A request yield (an await, a sleep) re-enters the statement that holds it, because the
                        // resume's value is delivered by re-running it; a plain value yield resumes PAST it.
                        currentLoop.BodyStatementIndex = currentLoop.YieldedReEnter
                            ? currentLoop.YieldedBodyIndex
                            : currentLoop.YieldedBodyIndex + 1;
                        currentLoop.YieldedBodyIndex = -1;
                        currentLoop.YieldedReEnter = false;
                    }
                    else currentLoop.BodyStatementIndex = 0;
                    if (!currentLoop.ScopeActive)
                    {
                        CurrentFrame.Scopes.PushScope();
                        currentLoop.ScopeActive = true;
                        ApplyPatternBindings(node.Line, node.Column);
                    }
                }

                if (currentLoop.Phase == 1)
                {
                    try
                    {
                        for (var i = currentLoop.BodyStatementIndex; i < node.Body.Count; i++)
                        {
                            EnterStatement(i);
                            var result = Execute(node.Body[i]);
                            switch (result.Flow)
                            {
                                case ControlFlow.Yield:
                                    if (loopIdx == _loopStackSp - 1)
                                    {
                                        currentLoop.YieldedBodyIndex = _currentStatementIndex;
                                        currentLoop.YieldedReEnter = YieldNeedsReEnter;
                                        currentLoop.Phase = 2;
                                    }
                                    else
                                    {
                                        // Nested loop yielded through us: remember where in our body we
                                        // were so the resume re-executes the nested loop statement instead
                                        // of replaying earlier statements (`i` survives nested execution).
                                        currentLoop.BodyStatementIndex = i;
                                    }
                                    return result;

                                case ControlFlow.Return:
                                case ControlFlow.Throw:
                                    return result;

                                case ControlFlow.Break:
                                    {
                                        if (currentLoop.ScopeActive)
                                        {
                                            CurrentFrame.Scopes.PopScope();
                                            currentLoop.ScopeActive = false;
                                        }
                                        currentLoop.Phase = 3;
                                        return ExecutionResult.Continue();
                                    }
                            }

                            if (result.Flow != ControlFlow.ContinueLoop)
                                continue;

                            if (currentLoop.ScopeActive)
                            {
                                CurrentFrame.Scopes.PopScope();
                                currentLoop.ScopeActive = false;
                            }
                            currentLoop.BodyStatementIndex = 0;
                            currentLoop.Phase = 2;
                            break;
                        }
                    }
                    finally { }
                    if (currentLoop.Phase == 1) currentLoop.Phase = 2;
                }

                if (currentLoop.Phase == 2)
                {
                    currentLoop.Phase = 0;
                    currentLoop.BodyStatementIndex = 0;
                }
                if (currentLoop.Phase == 3) break;
            }
        }
        finally
        {
            // Pop the loop frame on ANY non-yield exit, including when the while completes
            // during a resume. Mirrors the for visitor fix: a stale frame left behind would
            // be re-matched by node identity on a later re-entry of the same statement.
            if (!_isYield && _loopStackSp > 0)
            {
                if (currentLoop.ScopeActive) CurrentFrame.Scopes.PopScope();
                _loopStackSp--;
            }
        }
        return ExecutionResult.Continue();
    }

    /// <summary>True when the value is an ASYNC sequence, answering the item type it yields. NO ENUMERATOR IS
    /// CREATED, which is what a PLAIN foreach needs: the refusal must not OPEN a sequence it is about to reject
    /// (measured by this feature's own suite: opening one leaked its file handle, so the file could not be deleted
    /// afterwards, and the failure surfaced two sections later as an I/O error on the NEXT write).</summary>
    private static bool IsAsyncEnumerable(object? value, out string itemType)
    {
        itemType = "";
        if (value == null) return false;
        foreach (var candidate in value.GetType().GetInterfaces())
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            {
                itemType = GetFriendlyTypeName(candidate.GetGenericArguments()[0]);
                return true;
            }
        return false;
    }

    /// <summary>Invokes something the engine reached by REFLECTION, unwrapping the wrapper: a
    /// TargetInvocationException's own name says NOTHING (measured on the AOT publish, where a step of the async
    /// stream answered exactly that and nothing else), so every reflected call in this feature goes through here and
    /// reports the real failure through the engine's ONE message map.</summary>
    private static object? InvokeReflected(MethodInfo method, object target, object?[]? args, int line, int column,
        string what)
    {
        try { return method.Invoke(target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        { throw ClrMemberFailure(tie.InnerException, line, column, what); }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column, what); }
    }

    /// <summary>The property-read twin of <see cref="InvokeReflected"/>, for the enumerator's `Current`.</summary>
    private static object? ReadReflected(PropertyInfo property, object target, int line, int column, string what)
    {
        try { return property.GetValue(target); }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        { throw ClrMemberFailure(tie.InnerException, line, column, what); }
        catch (Exception ex) { throw ClrMemberFailure(ex, line, column, what); }
    }

    /// <summary>Opens an async sequence's enumerator and answers its IAsyncEnumerator&lt;T&gt; INTERFACE. Both
    /// lookups go through the interfaces: a compiler-generated async iterator implements IAsyncEnumerable&lt;T&gt;
    /// and its enumerator's MoveNextAsync EXPLICITLY, so a lookup by name on the concrete type finds nothing
    /// (measured on File.ReadLinesAsync, whose value's own name is &lt;IterateFileLinesAsync&gt;d__126).
    /// GetAsyncEnumerator is invoked with CancellationToken.None, which is what C# passes when a loop declares no
    /// token. The caller OWNS the enumerator and must dispose it.</summary>
    private static bool TryOpenAsyncEnumerator(object? value, out object enumerator, out Type enumeratorInterface,
        int line, int column)
    {
        enumerator = null!;
        enumeratorInterface = null!;
        if (value == null) return false;
        Type? enumerableInterface = null;
        foreach (var candidate in value.GetType().GetInterfaces())
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>))
            { enumerableInterface = candidate; break; }
        if (enumerableInterface == null) return false;

        var getEnumerator = enumerableInterface.GetMethod("GetAsyncEnumerator");
        if (getEnumerator == null) return false;
        var created = InvokeReflected(getEnumerator, value, [CancellationToken.None], line, column, "GetAsyncEnumerator");
        if (created == null) return false;
        foreach (var candidate in created.GetType().GetInterfaces())
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>))
            { enumeratorInterface = candidate; break; }
        if (enumeratorInterface == null)
        {
            // The sequence opened something we cannot drive: dispose it rather than leak it.
            DisposeAsyncResource(created, line, column);
            return false;
        }
        enumerator = created;
        return true;
    }

    /// <summary>Drives an `await foreach`: one MoveNextAsync per element, WAITED through the engine's own task bridge
    /// (the same one a CLR member's task takes, so ONE wait mechanism serves both), then the body with that element.
    /// The enumerator is disposed on EVERY exit, which is what C# does with an async iterator (`await using`), and a
    /// step's failure is unwrapped so the script sees the real exception rather than the reflection wrapper.</summary>
    private ExecutionResult RunAwaitForeach(ForeachStmt node, object enumerator, Type enumeratorInterface)
    {
        var moveNext = enumeratorInterface.GetMethod("MoveNextAsync")!;
        var current = enumeratorInterface.GetProperty("Current")!;
        // THE BODY IS A LOOP BODY, so this drive enters the loop scope exactly as a plain `foreach` arm does. Two rules
        // read it and both are load-bearing here: the replay memo must not RECORD inside a loop (a value computed on the
        // first iteration would be served to the next, which measured as a body that ran once for a five-element
        // stream), and an `await` inside the body keeps the blocking rule, because the driver's replay would re-run the
        // iterations this drive has already consumed.
        using var loopScope = EnterTreeLoop();
        try
        {
            while (true)
            {
                var step = InvokeReflected(moveNext, enumerator, null, node.Line, node.Column, "MoveNextAsync");
                if (step == null || !AwaitMoveNextStep(step, node.Line, node.Column))
                    return ExecutionResult.Continue();
                var element = ReadReflected(current, enumerator, node.Line, node.Column, "Current");
                var result = ExecuteForeachBody(node, element, node.Line, node.Column);
                if (result.Flow is ControlFlow.Yield or ControlFlow.Return or ControlFlow.Throw) return result;
                if (result.Flow == ControlFlow.Break) return ExecutionResult.Continue();
            }
        }
        finally
        {
            DisposeAsyncResource(enumerator, node.Line, node.Column);
        }
    }

    /// <summary>Waits the step's `ValueTask&lt;bool&gt;` and answers it. The CAST IS COMPILED on purpose, and that is
    /// the whole lesson of this feature's AOT leg: a member reached ONLY by reflection can be a STUB the AOT compiler
    /// left behind for a body its scanner removed, while a compiled call keeps the implementation. Measured on the
    /// AOT publish: the reflected route answered "[DisposeAsync]: operation not supported" (a NotSupportedException
    /// with an EMPTY message, which is what that stub throws) while the JIT build read the file and the same call
    /// compiled by hand worked on AOT too. `MoveNextAsync`'s contract says exactly which ValueTask arrives, so the
    /// cast is as precise as the interface.</summary>
    private bool AwaitMoveNextStep(object step, int line, int column)
    {
        if (step is not ValueTask<bool> valueTask) return false;
        try
        {
            var scriptTask = ToScriptTask(valueTask.AsTask());
            // PARKED, and named: the drive below has no resume point for a step, so this waits (driving the pump) and
            // counts under its own site. See Interpreter.WaitForTask for what that wait now does.
            WaitForTask(scriptTask, "await foreach", line, column);
            return scriptTask.Result is bool hasNext && hasNext;
        }
        catch (Exception ex) when (ex is not ScriptException)
        {
            throw ClrMemberFailure(ex, line, column, "MoveNextAsync");
        }
    }

    /// <summary>The disposal an `await using` performs, and the one an async enumerator's teardown performs: C#'s
    /// rule is that only an IAsyncDisposable may be awaited, so an IDisposable-only value is REFUSED by name
    /// rather than disposed synchronously (that is what plain `using` is for), and a value implementing neither is
    /// refused too. The await itself is the same wait a top-level `await` performs on a CLR task, through a
    /// COMPILED cast (see AwaitMoveNextStep for why the reflected route is not enough on AOT).</summary>
    private static void DisposeAsyncResource(object? resource, int line, int column)
    {
        if (resource is IAsyncDisposable disposable)
        {
            try
            {
                WaitForTask(ToScriptTask(disposable.DisposeAsync().AsTask()), "await using", line, column);
            }
            catch (Exception ex) when (ex is not ScriptException)
            {
                throw ClrMemberFailure(ex, line, column, "DisposeAsync");
            }
            return;
        }
        var name = ReportedTypeName(resource);
        if (resource is IDisposable)
            RuntimeError($"{name} implements IDisposable, not IAsyncDisposable: `using` disposes it synchronously, "
                + "so write `using` rather than `await using`.", line, column);
        RuntimeError($"{name} cannot be disposed: it implements neither IDisposable nor IAsyncDisposable.", line, column);
    }



    public ExecutionResult VisitForeachStmt(ForeachStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        var insideCoroutine = _currentCoroutine != null;

        // AN ASYNC STREAM (`await foreach`) is driven HERE, in EITHER context and BEFORE the coroutine machinery:
        // its elements arrive one MoveNextAsync at a time, and the drive WAITS each step (the same wait a top-level
        // `await` performs on a CLR task), so it needs no suspension and cannot be the resumable loop the coroutine
        // path builds. `await foreach` over something that is NOT an async sequence is refused rather than silently
        // iterated, which is C#'s own rule (CS8411) and stops a stray `await` from hiding a typo.
        if (node.IsAwait)
        {
            var awaited = Evaluate(node.Collection);
            if (!IsAsyncEnumerable(awaited, out _)
                || !TryOpenAsyncEnumerator(awaited, out var awaitedEnumerator, out var awaitedIface, node.Line, node.Column))
            {
                RuntimeError($"`await foreach` needs a sequence whose elements arrive asynchronously (an "
                    + $"IAsyncEnumerable): {GetFriendlyTypeNameOfValue(awaited)} has no GetAsyncEnumerator.",
                    node.Line, node.Column);
                return ExecutionResult.Continue();
            }
            return RunAwaitForeach(node, awaitedEnumerator, awaitedIface);
        }

        if (!insideCoroutine)
        {
            using var loopScope = EnterTreeLoop();
            var collection = Evaluate(node.Collection);

            switch (collection)
            {
                case null:
                    return ExecutionResult.Continue();

                case Coroutine co:
                    {
                        if (co.Status == CoroutineStatus.NotStarted) co.Resume();
                        while (co.IsResumable)
                        {
                            var result = ExecuteForeachBody(node, co.LastYieldedValue, node.Line, node.Column);
                            if (result.Flow is ControlFlow.Yield or ControlFlow.Return or ControlFlow.Throw)
                                return result;
                            if (result.Flow == ControlFlow.Break)
                                return ExecutionResult.Continue();
                            co.Resume();
                        }
                        return ExecutionResult.Continue();
                    }
            }

            switch (_currentCoroutine)
            {
                case null when collection is Generator<object?> generator:
                    {
                        // Drive via Generator.MoveNext so a VM-backed generator (Task 2-3: a plain
                        // value-yield generator with a compiled body) dispatches to the bytecode VM,
                        // the tree foreach becomes the VM's outer boundary and the generator runs
                        // VM-flat. A tree generator takes the same route (MoveNext → _co.Resume →
                        // tree) with identical semantics. Existing generator-resume semantics
                        // preserved: a yield inside the body becomes the generator's next yielded
                        // value and suppresses the resume, leaving the generator suspended for its
                        // own consumer to step.
                        var gco = generator.InnerCoroutine;
                        while (generator.MoveNext())
                        {
                            var result = ExecuteForeachBody(node, generator.Current, node.Line, node.Column);
                            if (result.Flow == ControlFlow.Yield)
                            {
                                gco.LastYieldedValue = result.Value;
                            }
                            else if (result.Flow is ControlFlow.Return or ControlFlow.Throw)
                            {
                                return result;
                            }
                            else if (result.Flow == ControlFlow.Break)
                            {
                                return ExecutionResult.Continue();
                            }
                        }
                        return ExecutionResult.Continue();
                    }
                case null when collection is Coroutine foreachCo:
                    {
                        if (foreachCo.Status == CoroutineStatus.NotStarted) foreachCo.Resume();
                        while (foreachCo.IsResumable)
                        {
                            var result = ExecuteForeachBody(node, foreachCo.LastYieldedValue, node.Line, node.Column);
                            if (result.Flow is ControlFlow.Yield or ControlFlow.Return or ControlFlow.Throw)
                                return result;
                            if (result.Flow == ControlFlow.Break)
                                return ExecutionResult.Continue();
                            foreachCo.Resume();
                        }
                        return ExecutionResult.Continue();
                    }
            }

            // Shared element-source path, every non-coroutine collection kind enumerates here.
            if (TryGetIterableElements(collection, out var elements))
            {
                foreach (var element in elements)
                {
                    var result = ExecuteForeachBody(node, element, node.Line, node.Column);
                    if (result.Flow is ControlFlow.Yield or ControlFlow.Return or ControlFlow.Throw)
                        return result;
                    if (result.Flow == ControlFlow.Break)
                        return ExecutionResult.Continue();
                }
                return ExecutionResult.Continue();
            }

            // AN ASYNC STREAM with a PLAIN foreach is refused WITH THE FIX in the message, and this test is LAST on
            // purpose: it walks the value's interface list (an array, so an allocation), and every ORDINARY foreach in
            // the language passes through here. Measured with the test FIRST: `crash_array_factory`'s allocation
            // budgets, which is the suite that exists to catch a per-loop allocation. The refusal used to name the
            // compiler-generated class ("Cannot iterate over type <IterateFileLinesAsync>d__126"), a name no script can
            // write and one that says nothing about what to do; the test does NOT open the sequence (see
            // IsAsyncEnumerable), so a rejected stream is not consumed.
            if (IsAsyncEnumerable(collection, out var asyncItemType))
            {
                RuntimeError($"Cannot iterate over an asynchronous sequence of {asyncItemType} with a plain "
                    + "foreach: its elements arrive one await at a time, so write `await foreach (var item in …)`.",
                    node.Line, node.Column);
                return ExecutionResult.Continue();
            }

            RuntimeError($"Cannot iterate over type {GetFriendlyTypeNameOfValue(collection)}", node.Line, node.Column);
            return ExecutionResult.Continue();
        }

        // Coroutine-aware path
        // A resume must target THIS coroutine's own loop frames. Nested coroutines share the
        // interpreter-wide _loopStack above their LoopBaseSp, and _isResumingFromYield stays
        // true for the whole duration of the OUTER resume, without an ownership check a
        // nested coroutine's FIRST foreach is misread as a resume of the outer's frame, its
        // init phase is skipped, and the while(true) state machine spins at Phase 0 forever
        // (recursive generators hung at nesting depth 2, spec 2026-08-29).
        var scanBase = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        var isResuming = false;
        int loopIdx = -1;
        if (_isResumingFromYield && _loopStackSp > scanBase)
        {
            // Match by AST node within THIS coroutine's frames (index >= scanBase). A resume
            // re-enters the OUTERMOST frame of the function, a nested Block/loop frame pushed
            // by a yield in the foreach BODY sits ABOVE the foreach frame, so a top-of-stack
            // test fails to match and the loop re-initializes, skipping its remaining elements.
            for (var si = _loopStackSp - 1; si >= scanBase; si--)
            {
                if (_loopStack[si].Type == LoopType.Foreach && ReferenceEquals(_loopStack[si].LoopNode, node))
                {
                    isResuming = true;
                    loopIdx = si;
                    break;
                }
            }
        }

        if (!isResuming)
        {
            if (_loopStackSp >= _loopStack.Length)
                RuntimeError("Loop nesting too deep.", node.Line, node.Column);
            loopIdx = _loopStackSp++;
            _loopStack[loopIdx] = new LoopFrame
            {
                Type = LoopType.Foreach,
                Phase = 0,
                BodyStatementIndex = 0,
                YieldedBodyIndex = -1,
                LoopNodeStatementIndex = _currentStatementIndex,
                LoopNode = node,
                ScopeActive = false,
                ArrayEnumerator = null,
                ArrayIndex = 0,
                Iterator = null,
                ScriptList = null,
                Tuple = null,
                Coroutine = null,
                HasCurrent = false,
                CurrentElement = null
            };
        }

        ref var currentLoop = ref _loopStack[loopIdx];

        try
        {
            if (!isResuming && currentLoop.Phase == 0)
            {
                var collection = Evaluate(node.Collection);
                if (collection == null) { currentLoop.Phase = 3; return ExecutionResult.Continue(); }

                if (collection is Coroutine co)
                {
                    currentLoop.Coroutine = co;
                    if (co.Status is CoroutineStatus.NotStarted or CoroutineStatus.Waiting)
                    {
                        var first = co.Resume();
                        currentLoop.HasCurrent = (co.IsResumable);
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = first;
                    }
                    else if (co.IsResumable)
                    {
                        currentLoop.HasCurrent = true;
                        currentLoop.CurrentElement = co.LastYieldedValue;
                    }
                    else currentLoop.HasCurrent = false;
                }
                else if (collection is Generator<object?> generator)
                {
                    var gco = generator.InnerCoroutine;
                    currentLoop.Coroutine = gco;
                    TraceCoro($"foreach-gen {gco.Fn.Declaration.Name} status={gco.Status}");
                    if (gco.Status is CoroutineStatus.NotStarted or CoroutineStatus.Waiting)
                    {
                        var first = gco.Resume();
                        currentLoop.HasCurrent = (gco.IsResumable);
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = first;
                    }
                    else if (gco.IsResumable)
                    {
                        currentLoop.HasCurrent = true;
                        currentLoop.CurrentElement = gco.LastYieldedValue;
                    }
                    else currentLoop.HasCurrent = false;
                }
                else if (collection is IScriptList list)
                {
                    currentLoop.ScriptList = list;
                    currentLoop.ArrayIndex = 0;
                    currentLoop.HasCurrent = currentLoop.ArrayIndex < list.Count;
                    if (currentLoop.HasCurrent) currentLoop.CurrentElement = list[currentLoop.ArrayIndex++];
                }
                else if (collection is Array arr)
                {
                    currentLoop.ArrayEnumerator = arr;
                    currentLoop.ArrayIndex = 0;
                    currentLoop.HasCurrent = currentLoop.ArrayIndex < arr.Length;
                    if (currentLoop.HasCurrent) currentLoop.CurrentElement = arr.GetValue(currentLoop.ArrayIndex++);
                }
                else if (collection is ScriptTuple tup)
                {
                    currentLoop.Tuple = tup;
                    currentLoop.ArrayIndex = 0;
                    currentLoop.HasCurrent = currentLoop.ArrayIndex < tup.Length;
                    if (currentLoop.HasCurrent) currentLoop.CurrentElement = tup[currentLoop.ArrayIndex++];
                }
                else if (collection is IEnumerable enumerable)
                {
                    currentLoop.Iterator = enumerable.GetEnumerator();
                    currentLoop.HasCurrent = currentLoop.Iterator.MoveNext();
                    if (currentLoop.HasCurrent) currentLoop.CurrentElement = currentLoop.Iterator.Current;
                }
                else RuntimeError($"Cannot iterate over type {collection.GetType().Name}", node.Line, node.Column);

                currentLoop.Phase = 1;
            }

            while (true)
            {
                if (currentLoop.Phase == 1)
                {
                    if (!currentLoop.HasCurrent)
                    {
                        if (currentLoop.ScopeActive) { CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false; }
                        currentLoop.Phase = 3; break;
                    }

                    if (!currentLoop.ScopeActive) { CurrentFrame.Scopes.PushScope(); currentLoop.ScopeActive = true; }

                    var elemType = node.VarType ?? new TypeInfo(TokenType.Object, true, 0, 0, null);
                    if (currentLoop.ScriptList != null) elemType = currentLoop.ScriptList.ElementType;

                    try
                    {
                        if (currentLoop.YieldedReEnter)
                        {
                            // Re-entering the body after a REQUEST yield: the element is the one this iteration is
                            // already on and its loop var is bound, so only the position moves.
                            currentLoop.YieldedReEnter = false;
                        }
                        else
                        {
                            var element = currentLoop.CurrentElement;
                            if (node.LoopVarNames is { Count: > 0 } ln)
                                DestructureBind(element, ln, node.Line, node.Column);
                            else
                                CurrentFrame.Scopes.Define(node.VarName,
                                    CoerceTo(element, elemType, node.Line, node.Column),
                                    node.Line, node.Column);
                        }

                        for (var i = currentLoop.BodyStatementIndex; i < node.Body.Count; i++)
                        {
                            EnterStatement(i);
                            var result = Execute(node.Body[i]);
                            switch (result.Flow)
                            {
                                case ControlFlow.Yield:
                                    if (loopIdx == _loopStackSp - 1)
                                    {
                                        if (YieldNeedsReEnter)
                                        {
                                            // A REQUEST yield (an await, a sleep) resumes by RE-RUNNING the statement
                                            // that holds it, for the SAME element: the iterator must not advance and
                                            // the loop var must not be re-bound (Phase 1's own guard reads the flag).
                                            // A plain value yield takes the advance-and-rebind path below, which is
                                            // what one yield per iteration means.
                                            currentLoop.BodyStatementIndex = _currentStatementIndex;
                                            currentLoop.YieldedReEnter = true;
                                        }
                                        else
                                        {
                                            // Leaf value yield: resume advances the iterator and re-runs the body.
                                            currentLoop.YieldedReEnter = false;
                                            currentLoop.Phase = 2;
                                        }
                                    }
                                    else
                                    {
                                        // Nested frame (loop/if/block in our body) yielded through us:
                                        // the resume re-executes THIS body statement, which drives the
                                        // nested frame, advancing the iterator now would leave the
                                        // nested frame stale and skip its continuation.
                                        currentLoop.BodyStatementIndex = i;
                                    }
                                    return result;

                                case ControlFlow.Return or ControlFlow.Throw:
                                    return result;

                                case ControlFlow.Break:
                                    {
                                        if (currentLoop.ScopeActive)
                                        {
                                            CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false;
                                        }
                                        currentLoop.Phase = 3; return ExecutionResult.Continue();
                                    }
                            }

                            if (result.Flow != ControlFlow.ContinueLoop)
                                continue;

                            if (currentLoop.ScopeActive)
                            {
                                CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false;
                            }
                            currentLoop.BodyStatementIndex = 0; currentLoop.Phase = 2;
                            break;
                        }
                    }
                    finally { }

                    if (currentLoop.ScopeActive) { CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false; }
                    if (currentLoop.Phase == 1) currentLoop.Phase = 2;
                }

                if (currentLoop.Phase == 2)
                {
                    if (currentLoop.Coroutine != null)
                    {
                        TraceCoro($"foreach-resume {currentLoop.Coroutine.Fn.Declaration.Name} status={currentLoop.Coroutine.Status}");
                        var next = currentLoop.Coroutine.Resume();
                        currentLoop.HasCurrent = (currentLoop.Coroutine.IsResumable);
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = next;
                    }
                    else if (currentLoop.ScriptList != null)
                    {
                        currentLoop.HasCurrent = currentLoop.ArrayIndex < currentLoop.ScriptList.Count;
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = currentLoop.ScriptList[currentLoop.ArrayIndex++];
                    }
                    else if (currentLoop.ArrayEnumerator != null)
                    {
                        currentLoop.HasCurrent = currentLoop.ArrayIndex < currentLoop.ArrayEnumerator.Length;
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = currentLoop.ArrayEnumerator.GetValue(currentLoop.ArrayIndex++);
                    }
                    else if (currentLoop.Tuple != null)
                    {
                        currentLoop.HasCurrent = currentLoop.ArrayIndex < currentLoop.Tuple.Length;
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = currentLoop.Tuple[currentLoop.ArrayIndex++];
                    }
                    else if (currentLoop.Iterator != null)
                    {
                        currentLoop.HasCurrent = currentLoop.Iterator.MoveNext();
                        if (currentLoop.HasCurrent) currentLoop.CurrentElement = currentLoop.Iterator.Current;
                    }
                    else currentLoop.HasCurrent = false;

                    currentLoop.BodyStatementIndex = 0;
                    currentLoop.Phase = 1;
                }
                if (currentLoop.Phase == 3) break;
            }
        }
        finally
        {
            // Pop the foreach frame on ANY non-yield exit, including when the loop completes
            // during a resume (_isResumingFromYield). The _isResumingFromYield guard is
            // redundant for yields (they set _isYield) and harmful for resume-exits: it leaves
            // a stale foreach frame so a LATER yield in the same function resumes at this
            // loop's LoopNodeStatementIndex forever (mirrors the for-visitor fix).
            if (!_isYield && _loopStackSp > 0)
            {
                if (currentLoop.ScopeActive) CurrentFrame.Scopes.PopScope();
                if (currentLoop.Iterator is IDisposable d) d.Dispose();
                _loopStackSp--;
            }
        }
        return ExecutionResult.Continue();
    }

    /// <summary>One iteration of a foreach body over `element`, shared by every iterable kind
    /// (destructuring form, coercion, and flow control are identical across kinds). Break is
    /// surfaced to the caller (which stops iterating) rather than collapsed to Continue, the
    /// pre-refactor branches returned from VisitForeachStmt on break, so the shared helper
    /// must let its caller distinguish "break" from a completed iteration.</summary>
    private ExecutionResult ExecuteForeachBody(ForeachStmt node, object? element, int line, int col)
    {
        CurrentFrame.Scopes.PushScope();
        try
        {
            if (node.LoopVarNames is { Count: > 0 } ln)
                DestructureBind(element, ln, node.Line, node.Column);
            else
                CurrentFrame.Scopes.Define(node.VarName,
                    CoerceTo(element, node.VarType ?? new TypeInfo(TokenType.Object, true, 0, 0, null),
                        node.Line, node.Column), node.Line, node.Column);

            PrebindDeclaredFunctions(node.Body);
            foreach (var t in node.Body)
            {
                var result = Execute(t);
                switch (result.Flow)
                {
                    case ControlFlow.Yield:
                    case ControlFlow.Return:
                    case ControlFlow.Throw:
                        return result;

                    case ControlFlow.Break:
                        return result;
                }

                if (result.Flow == ControlFlow.ContinueLoop)
                    break;
            }
        }
        finally { CurrentFrame.Scopes.PopScope(); }
        return ExecutionResult.Continue();
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    // C# HOISTING FOR DECLARATIONS IN A BODY, and the one place it is stated.
    //
    // A function declared directly in a body or a block is IN SCOPE for the whole of it, not only from its declaration
    // statement downwards. This engine bound the name when that statement EXECUTED, which a closure created earlier in
    // the same body cannot capture: measured in `assets/language/ui/demos/music_player.slc`, where
    // `listBox.DropFiles = (_, e) => AfterAdd(...)` sits above `void AfterAdd(...)`, the drop arrived and the handler
    // answered `[UI] Event error: Undefined variable 'AfterAdd'`, because a lambda's closure snapshot is taken at
    // creation and held no such entry.
    //
    // WHAT IS BOUND HERE IS THE NAME, NOT THE FUNCTION. Creating the ScriptFunction at body entry would snapshot the
    // enclosing scopes too early, and the very shape that motivates this reads locals declared FURTHER DOWN the same
    // body (`AfterAdd` reads `infoLabel`, `listBox`, the top-level `media`): a function built at entry could not see
    // them. So the name holds a ForwardFunctionSlot, the declaration FILLS that slot when it runs (VisitFuncDecl), and
    // a closure that captured it sees the real function at call time. The read path unwraps a slot exactly as it
    // unwraps a capture cell (ReadBinding), so no call site had to learn anything.
    //
    // THE SCOPE RULE IS THE BLOCK'S, as in C#: a declaration inside an inner `if` is bound when that inner block starts,
    // so a closure outside it never sees it. An existing binding WINS (a parameter, a local, a re-declaration), which is
    // what keeps a shadowing name behaving as it did.
    // ══════════════════════════════════════════════════════════════════════════════════════════════════════════════
    private void PrebindDeclaredFunctions(IReadOnlyList<Stmt> body)
    {
        var decls = HoistableDeclarationsOf(body);
        if (decls.Length == 0) return;
        var scope = CurrentFrame.Scopes.Scopes.Peek();
        foreach (var fd in decls)
        {
            if (scope.ContainsKey(fd.Name)) continue;
            CurrentFrame.Scopes.Define(fd.Name, new ForwardFunctionSlot(fd.Name), fd.Line, fd.Column);
        }
    }

    /// <summary>Which functions a statement list declares directly, computed once per LIST. A generic declaration is
    /// skipped: its name binds a template rather than a callable, and the template path already resolves by name. The
    /// cache is keyed by the list because an `if` branch and a loop body are raw statement lists rather than blocks, they
    /// are entered once per iteration, and the AST outlives the run, so a weak-keyed table costs no node field and no
    /// leak.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<Stmt>, FuncDecl[]> _hoistedDecls = new();

    private static FuncDecl[] HoistableDeclarationsOf(IReadOnlyList<Stmt> body)
        => _hoistedDecls.GetValue(body, static list =>
        {
            List<FuncDecl>? found = null;
            foreach (var stmt in list)
                if (stmt is FuncDecl { TypeParameters.Count: 0 } fd && !string.IsNullOrEmpty(fd.Name))
                    (found ??= []).Add(fd);
            return found is null ? [] : [.. found];
        });

    /// <summary>Records, on every forward declaration slot a closure TOOK, that it was taken before its declaration ran:
    /// such a declaration keeps the tree, because a compiled body has no slot for a capture that only exists later.</summary>
    private static void MarkForwardCaptures(ClosureSnapshot snapshot)
    {
        foreach (var scope in snapshot.LocalScopes)
            foreach (var kv in scope)
                if (kv.Value is ForwardFunctionSlot { Value: null } slot) slot.CapturedEarly = true;
        foreach (var kv in snapshot.GlobalScope)
            if (kv.Value is ForwardFunctionSlot { Value: null } slot) slot.CapturedEarly = true;
    }

    /// <summary>What a READ of a scope entry or closure-snapshot entry sees: a binding cell is dereferenced and a
    /// forward declaration slot answers the function its declaration created. Both shapes exist because a capture can be
    /// a cell (promoted) or a name bound before its declaration ran (C# hoisting).</summary>
    private static object? ReadBinding(object? value, string name, int line, int column)
    {
        while (true)
        {
            switch (value)
            {
                case RefCell rc:
                    var celled = rc.Get();
                    if (ReferenceEquals(celled, value)) return value;   // a self-referential cell cannot make progress
                    value = celled;
                    break;
                case ForwardFunctionSlot slot:
                    var declared = slot.Read(line, column);
                    if (ReferenceEquals(declared, value)) return declared;
                    value = declared;
                    break;
                default:
                    return value;
            }
        }
    }

    public ExecutionResult VisitFuncDecl(FuncDecl node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;

        // Generic function template, store without creating ScriptFunction
        if (node.TypeParameters.Count > 0)
        {
            CurrentFrame.Scopes.Define(node.Name, node);
            if (_recordingExports && _currentExports != null)
                _currentExports.TryAdd(node.Name, node);
            return ExecutionResult.Continue();
        }

        RecordNamespacePath(node.Name, node.Line, node.Column);

        // Type-name typo guard: warn when a param/return type does not resolve to any known type.
        WarnUnknownTypeNames(node);

        var mainFrame = _rootFrame;
        var realGlobalScope = mainFrame.Scopes.GetGlobalScopeDictionary();
        // A NESTED FUNCTION's free names are analysed HERE, once, because nothing else ever set them: the parser fills
        // `CapturedNames` for every LAMBDA form and not for a `FuncDecl`, so a local function's snapshot captured NO
        // local scope at all. That is invisible while the enclosing frame is alive (a tree invocation resolves the
        // name from the live chain) and fatal the moment it is not: measured, a function declared inside a
        // top-level block and CALLED from an engine-invoked `Com.Run` callback answered
        // `Undefined variable 'report'` on the compiled leg, because the block's own locals were gone by then and the
        // snapshot carried nothing. Computing them from the declaration is the same analysis the parser runs for a
        // lambda, applied to the shape it was missing.
        node.CapturedNames ??= FreeVariableAnalyzer.Analyze(node);
        var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(realGlobalScope, node.CapturedNames);

        var paramTypes = GetParameterTypes(node.Params);
        var autoCoroutine = ContainsYieldInStmts(node.Body);
        var isCoroutine = node.IsCoroutine || autoCoroutine || node.IsAsync;

        var flags = (node.IsPure ? FunctionFlags.Pure : FunctionFlags.None)
                  | (isCoroutine ? FunctionFlags.Coroutine : FunctionFlags.None);

        // Local (nested) function: capture enclosing locals and promote mutated captures
        // to RefCell so writes propagate back to the enclosing scope (like lambdas).
        if (_currentFuncDecl != null)
        {
            var localScopeList = new List<Dictionary<string, object?>>();
            var localConstList = new List<HashSet<string>>();
            foreach (var (scopeDict, constSet) in CurrentFrame.Scopes.GetScopesWithConst())
            {
                localScopeList.Add(new Dictionary<string, object?>(scopeDict!, _comparer));
                localConstList.Add([with(constSet, _comparer)]);
            }
            localScopeList.Reverse();
            localConstList.Reverse();
            // Root global scope is NOT part of the current function frame's scope stack,
            // merge it in so builtins (print, sleep, ...) resolve inside the local function.
            var localGlobal = new Dictionary<string, object?>(_comparer);
            var mainFrame2 = _rootFrame;
            var realGlobalScope2 = mainFrame2.Scopes.GetGlobalScopeDictionary();
            foreach (var kvp in realGlobalScope2)
                localGlobal[kvp.Key] = kvp.Value.Value;
            if (localScopeList.Count > 0)
            {
                foreach (var kvp in localScopeList[0])
                    if (!localGlobal.ContainsKey(kvp.Key)) localGlobal[kvp.Key] = kvp.Value;
            }
            var localGlobalConsts = localConstList.Count > 0 ? localConstList[0] : [with(_comparer)];
            var localScopes2 = localScopeList.Skip(1).ToList();

            var localConsts2 = localConstList.Skip(1).ToList();

            // A nested function's capture of an ENCLOSING LOCAL must be a BINDING, not a copy, the rule the
            // lambda path already applies to its own locals (`capturedSet.Contains(key)` there). The promotion
            // here used to fire only for names this body ASSIGNS, so a read-only capture kept a copy frozen at
            // declaration: measured, `void Show() { print(q); }` still printed 5 after the enclosing scope wrote
            // 6 or 42, while its assignment reached the enclosing variable, one storage for writes, another
            // for reads. Promoting every name the body REFERENCES (the same walk the VM capture layout uses)
            // makes both go through the one cell, which is also what lets a `ref` to such a local work.
            // The MERGED GLOBAL scope keeps the narrower rule: a global resolves live through the root scope,
            // so only the names this body writes need a binding (exactly like the lambda path's global term).
            var referencedNames = new HashSet<string>(_comparer);
            foreach (var s in node.Body) CollectFreeNames(s, n => { if (n != null) referencedNames.Add(n); });

            for (var li = 0; li < localScopes2.Count; li++)
                PromoteCapturesInScope(localScopes2[li], localConsts2[li],
                    key => referencedNames.Contains(key));
            PromoteCapturesInScope(localGlobal, localGlobalConsts,
                key => node.Body.Any(s => ContainsAssignmentTo(s, key)));

            snapshot = new ClosureSnapshot(localScopes2, [.. localConstList.Skip(1)], localGlobal, localGlobalConsts);
        }

        var scriptFn = new ScriptFunction(node, snapshot, node.ReturnType, paramTypes, node.IsRefParams, Flags: flags, IsOutParams: node.IsOutParams);
        // A function that CAPTURES something needs the lazy VM compile + capture materialization wherever it was
        // declared. Gating on `_currentFuncDecl` alone missed a declaration inside a TOP-LEVEL block, which is where a
        // script puts a helper beside the code that uses it: measured on the Excel sample, whose `void Line(...)`
        // reads its block's `report` and answered `Undefined variable 'report'` on the leg where the callback is
        // tree-walked, because the body had been compiled with no capture layout and read the name as a global.
        if (_currentFuncDecl != null || node.CapturedNames is { Count: > 0 })
            scriptFn.IsLocal = true;
        // A free function IS an overload set (RULES.md). A declaration whose signature MATCHES an existing one
        // REPLACES it, which is what a REPL line re-typed and a re-run of an edited file need; one whose signature
        // DIFFERS joins the set, so both bodies survive and each call resolves by argument types. Before this a
        // second declaration simply replaced the first, silently losing a body: measured, `string Ar(int)` beside
        // `string Ar(int, int)` left only the pair and `Ar(1)` reported an arity error.
        var binding = CurrentFrame.Scopes.Scopes.Peek().TryGetValue(node.Name, out var priorBind) ? priorBind.Value : null;
        object? bindValue = scriptFn;
        if (binding is ScriptOverloadSet priorSet)
        {
            foreach (var prior in priorSet.Members)
                if (prior.Declaration != null) RefuseRefOutOnlyOverload(node.Name, node, prior.Declaration, "", node.Line, node.Column);
            priorSet.AddOrReplace(scriptFn);
            bindValue = priorSet;
        }
        else if (binding is ScriptFunction priorFn && priorFn.Declaration != null
                 && !SameSignature(priorFn.Declaration, node))
        {
            RefuseRefOutOnlyOverload(node.Name, node, priorFn.Declaration, "", node.Line, node.Column);
            var newSet = new ScriptOverloadSet();
            newSet.AddOrReplace(priorFn);
            newSet.AddOrReplace(scriptFn);
            bindValue = newSet;
        }
        // THE HOISTED NAME THIS DECLARATION FILLS (see PrebindDeclaredFunctions): the body bound it before any of its
        // statements ran, so every closure created ABOVE this line holds that slot, and filling it is what makes the
        // function reachable for them. Filled with whatever the name finally means, which for an OVERLOAD SET is the set
        // (so a call through the slot picks the member by its arguments, as a call of the name does).
        if (binding is ForwardFunctionSlot forward)
        {
            forward.Value = bindValue;
            if (forward.CapturedEarly) node.CapturedBeforeDeclaration = true;
        }
        CurrentFrame.Scopes.Define(node.Name, bindValue);
        if (_currentNamespaceLeaf != null && _currentFuncDecl == null && _currentClass == null)
        {
            scriptFn.NamespaceContext = _currentNamespaceLeaf;
            var leafFn = scriptFn;
            _currentNamespaceLeaf.AddMethod(node.Name, args => InvokeScriptFunction(leafFn, args, node.Line, node.Column));
            _currentNamespaceLeaf.AddProperty(node.Name, leafFn);
        }
        if (_recordingExports && _currentExports != null)
            _currentExports.TryAdd(node.Name, scriptFn);
        return ExecutionResult.Continue();
    }

    private static bool ContainsYieldInStmts(IReadOnlyList<Stmt> body)
        => body.Any(ContainsYieldInStmt);

    private static bool ContainsYieldInStmt(Stmt stmt)
    {
        return stmt switch
        {
            ExprStmt es => ContainsYieldInExpr(es.Expression),
            ReturnStmt rs => rs.Value != null && ContainsYieldInExpr(rs.Value),
            BlockStmt block => ContainsYieldInStmts(block.Statements),
            IfStmt ifs => ContainsYieldInStmts(ifs.ThenBranch) || ContainsYieldInStmts(ifs.ElseBranch),
            WhileStmt ws => ContainsYieldInStmts(ws.Body),
            ForStmt fs => ContainsYieldInStmts(fs.Body),
            ForeachStmt fes => ContainsYieldInStmts(fes.Body),
            _ => false
        };
    }

    /// <summary>True when this subtree contains a <c>yield</c>, asked through the PARSER's walk: this was a
    /// second copy of it (run from <c>VisitFuncDecl</c> rather than while parsing), byte-identical, and
    /// tools/check-dup-bodies.sh reported the pair the moment its collector stopped counting a brace inside
    /// a literal as structure. One implementation now, and the spine-loop fix the copy carried came with it.</summary>
    private static bool ContainsYieldInExpr(Expr expr) => Parser.ContainsYieldInExpr(expr);


    public ExecutionResult VisitForStmt(ForStmt node)
    {
        _currentLine = node.Line;
        _currentColumn = node.Column;
        var isCoroutine = _currentCoroutine != null;

        if (!isCoroutine)
        {
            using var loopScope = EnterTreeLoop();
            // The for-init variable is scoped to the whole loop (C# semantics), without this
            // outer scope, `for (var i = ...)` leaks `i` into the enclosing scope and a second
            // top-level `for (var i = ...)` warns "already declared".
            CurrentFrame.Scopes.PushScope();
            try
            {
                if (node.Init != null) _ = Execute(node.Init);
                var body = node.Body;
                while (IsTruthy(Evaluate(node.Condition)))
                {
                    CurrentFrame.Scopes.PushScope();
                    try
                    {
                        PrebindDeclaredFunctions(body);
                        foreach (var t in body)
                        {
                            var result = Execute(t);
                            switch (result.Flow)
                            {
                                case ControlFlow.Return or ControlFlow.Throw:
                                    return result;

                                case ControlFlow.Break:
                                    return ExecutionResult.Continue();
                            }

                            if (result.Flow == ControlFlow.ContinueLoop)
                                break;
                        }
                    }
                    finally { CurrentFrame.Scopes.PopScope(); }
                    if (node.Increment != null) Evaluate(node.Increment);
                }
            }
            finally { CurrentFrame.Scopes.PopScope(); }
            return ExecutionResult.Continue();
        }

        // Resume-identity: the resumed for is the ACTIVE frame whose LoopNodeStatementIndex
        // matches the statement being (re)entered. The top frame may belong to a NESTED loop
        // from the pre-yield execution (e.g. the outer for resumes while the inner for frame
        // is still above it), matching by statement index rather than stack position is what
        // makes nested for-yield resume correctly.
        var scanBase = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        var isResuming = false;
        int loopIdx = -1;
        if (_isResumingFromYield && _loopStackSp > scanBase)
        {
            // Scan only THIS coroutine's frames (index >= LoopBaseSp), the shared _loopStack
            // carries outer-coroutine frames below, so a recursive generator's first for must
            // not false-match an outer coroutine's frame (same misrouting class as the foreach
            // fix).
            for (var si = _loopStackSp - 1; si >= scanBase; si--)
            {
                // Match by AST node identity, not statement index: nested loops at the
                // same body position share LoopNodeStatementIndex, but never the node.
                if (_loopStack[si].Type == LoopType.For && ReferenceEquals(_loopStack[si].LoopNode, node))
                {
                    isResuming = true;
                    loopIdx = si;
                    break;
                }
            }
        }

        if (!isResuming)
        {
            if (_loopStackSp >= _loopStack.Length)
                RuntimeError("Loop nesting too deep.", node.Line, node.Column);
            loopIdx = _loopStackSp++;
            _loopStack[loopIdx] = new LoopFrame
            {
                Type = LoopType.For,
                Phase = 0,
                ForPhase = 0,
                BodyStatementIndex = 0,
                YieldedBodyIndex = -1,
                LoopNodeStatementIndex = _currentStatementIndex,
                LoopNode = node,
                ScopeActive = false,
                InitScopeActive = false
            };
        }

        ref var currentLoop = ref _loopStack[loopIdx];

        try
        {
            if (!isResuming && currentLoop.ForPhase == 0)
            {
                CurrentFrame.Scopes.PushScope();
                currentLoop.InitScopeActive = true;
                if (node.Init != null)
                {
                    _ = Execute(node.Init);
                    if (node.Init is VarDeclStmt varDecl)
                        PromoteToRefCell(varDecl.Name, varDecl.Line, varDecl.Column);
                }
                currentLoop.ForPhase = 1;
            }

            while (true)
            {
                if (currentLoop.ForPhase == 1)
                {
                    if (!IsTruthy(Evaluate(node.Condition))) break;
                    currentLoop.ForPhase = 2;
                    // Resume after a leaf yield: skip the yielded statement once (mirrors the while path).
                    if (currentLoop.YieldedBodyIndex >= 0)
                    {
                        // See the while path: a request yield re-enters its statement, a value yield goes past it.
                        currentLoop.BodyStatementIndex = currentLoop.YieldedReEnter
                            ? currentLoop.YieldedBodyIndex
                            : currentLoop.YieldedBodyIndex + 1;
                        currentLoop.YieldedBodyIndex = -1;
                        currentLoop.YieldedReEnter = false;
                    }
                    else currentLoop.BodyStatementIndex = 0;
                    if (!currentLoop.ScopeActive) { CurrentFrame.Scopes.PushScope(); currentLoop.ScopeActive = true; }
                }

                if (currentLoop.ForPhase == 2)
                {
                    try
                    {
                        for (var i = currentLoop.BodyStatementIndex; i < node.Body.Count; i++)
                        {
                            EnterStatement(i);
                            var result = Execute(node.Body[i]);
                            switch (result.Flow)
                            {
                                case ControlFlow.Yield:
                                    if (loopIdx == _loopStackSp - 1)
                                    {
                                        currentLoop.YieldedBodyIndex = _currentStatementIndex;
                                        currentLoop.YieldedReEnter = YieldNeedsReEnter;
                                        currentLoop.Phase = 2;
                                        currentLoop.ForPhase = 1; // route resume through the condition phase (leaf-yield skip)
                                    }
                                    else
                                    {
                                        // Nested loop yielded through us: remember WHERE in our body
                                        // we were, so the resume re-executes the nested loop statement
                                        // instead of replaying earlier body statements (e.g. counter
                                        // re-inits like `var i = 0`). `i` is the C# loop counter, it
                                        // survives the nested execution that clobbers _currentStatementIndex.
                                        currentLoop.BodyStatementIndex = i;
                                    }
                                    return result;

                                case ControlFlow.Return or ControlFlow.Throw:
                                    return result;

                                case ControlFlow.Break:
                                    {
                                        if (currentLoop.ScopeActive) { CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false; }
                                        currentLoop.ForPhase = 4; return ExecutionResult.Continue();
                                    }
                            }

                            if (result.Flow != ControlFlow.ContinueLoop)
                                continue;

                            if (currentLoop.ScopeActive)
                            {
                                CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false;
                            }
                            currentLoop.BodyStatementIndex = 0; currentLoop.ForPhase = 3;
                            break;
                        }
                    }
                    finally { }
                    if (currentLoop.ScopeActive) { CurrentFrame.Scopes.PopScope(); currentLoop.ScopeActive = false; }
                    if (currentLoop.ForPhase == 2) currentLoop.ForPhase = 3;
                }

                if (currentLoop.ForPhase == 3)
                {
                    if (node.Increment != null) Evaluate(node.Increment);
                    currentLoop.ForPhase = 1;
                    currentLoop.BodyStatementIndex = 0;
                }
                if (currentLoop.ForPhase == 4) break;
            }
        }
        finally
        {
            // Pop the loop frame on ANY non-yield exit, including when the loop completes
            // during a resume (_isResumingFromYield). The _isResumingFromYield guard was
            // redundant for yields (they set _isYield) and harmful for resume-exits: it left a
            // stale loop frame so a SECOND for-loop in the same generator skipped its init.
            if (!_isYield && _loopStackSp > 0)
            {
                if (currentLoop.ScopeActive) CurrentFrame.Scopes.PopScope();
                if (currentLoop.InitScopeActive) { CurrentFrame.Scopes.PopScope(); currentLoop.InitScopeActive = false; }
                _loopStackSp--;
            }
        }
        return ExecutionResult.Continue();
    }

    private void PromoteToRefCell(string name, int line, int column)
    {
        if (!CurrentFrame.Scopes.TryGet(name, out var existing))
            return;

        if (existing is RefCell) return;
        var capturedValue = existing;
        var cell = new RefCell(Getter: () => capturedValue, Setter: val => capturedValue = val);
        CurrentFrame.Scopes.Set(name, cell, line, column);
    }

    public ExecutionResult VisitBreakStmt(BreakStmt node)
        => ExecutionResult.Break();

    public ExecutionResult VisitContinueStmt(ContinueStmt node)
        => ExecutionResult.ContinueLoop();
}
