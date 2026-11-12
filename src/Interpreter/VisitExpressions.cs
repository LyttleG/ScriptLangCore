//#pragma warning disable IL2075, IL2067, IL3050, IL2057, IL2070 // Dynamic reflection, inherent to scripting runtime

namespace ScriptLangCore;

// Source: extracted from Program.cs section 9, IExprVisitor implementation
internal partial class Interpreter
{
    public object? VisitNullExpr(NullExpr node) => null;

    public object? VisitDiscardExpr(DiscardExpr node) => null;

    public object? VisitReturnValueExpr(ReturnValueExpr node) => _pendingReturnValue;

    /// <summary>A default parameter value that came from a bundle: the shell carried the expression as a
    /// compiled zero-parameter body, and this runs it. Every default-binding site reaches the value through
    /// Evaluate, so nothing else had to learn about the loaded form.</summary>
    public object? VisitCompiledDefaultExpr(CompiledDefaultExpr node)
    {
        var decl = node.Declaration;
        var fn = node.Thunk ??= new ScriptFunction(decl, SEmptySnapshot, decl.ReturnType,
            GetParameterTypes(decl.Params), decl.IsRefParams, IsOutParams: decl.IsOutParams)
        {
            NamespaceContext = node.NamespaceContext
        };
        return InvokeScriptFunction(fn, [], node.Line, node.Column);
    }

    public object VisitYieldExpr(YieldExpr node)
    {
        // On resume, consume the caller's resume value if one was provided
        // (skip for generators, their yield produces values, not consumes them)
        if (_isResumingFromYield && _currentCoroutine != null && _expectResumeValue
            && _currentCoroutine.Status != CoroutineStatus.NotStarted
            && !_currentCoroutine.IsGenerator)
        {
            _isResumingFromYield = false;  // consume once only, subsequent yields in same resume are fresh
            return _currentCoroutine.TakePendingResumeValue()!;
        }

        var value = node.Value != null ? Evaluate(node.Value) : null;
        if (ReferenceEquals(value, YieldSentinel)) return YieldSentinel; // propagate inner yield // propagate inner yield
        // Only update the top loop frame if it belongs to the CURRENT coroutine (nested coroutines
        // yield from within an outer's foreach/while, the shared loop stack's frames above our
        // LoopBaseSp are ours; frames below belong to outer coroutines and must not be mutated).
        var baseSp = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        if (_loopStackSp > baseSp)
            _loopStack[_loopStackSp - 1].BodyStatementIndex = _currentStatementIndex + 1;
        _yieldValue = value;
        _isYield = true;
        return YieldSentinel;
    }

    /// <summary>`await expr` (C# flavour). It yields an awaitable to the coroutine pump and consumes the value the
    /// pump resumes it with, which is the SAME suspension `yield req` uses; what is new is that the awaitable may be
    /// a Task, so a real async operation (Task.DelayAsync, or another async body) composes with the cooperative pump
    /// instead of blocking a thread.</summary>
    public object? VisitAwaitExpr(AwaitExpr node)
    {
        // A RESUMED RUN continues with the value its await answered, WITHOUT re-evaluating the operand: the statement is
        // re-entered (the coroutine rule), and the value comes from the await JOURNAL the resumable run's driver keeps,
        // so a fault the awaited task raised is raised here, at this await's own position, inside the statement.
        if (_currentCoroutine is null && ServeJournalledAwait(node, out var served))
            return served;

        // Consume the pump's outcome on resume WITHOUT re-evaluating the operand: the coroutine re-enters this
        // statement exactly as it does for VisitYieldExpr, and the value must come from the resume channel.
        if (_isResumingFromYield && _currentCoroutine != null && _expectResumeValue
            && _currentCoroutine.Status != CoroutineStatus.NotStarted
            && !_currentCoroutine.IsGenerator)
        {
            _isResumingFromYield = false;
            if (_currentCoroutine is { Tree: { } awaitedState }) awaitedState.AwaitConsumedStatement = _currentStatementIndex;
            return AwaitOutcome(_currentCoroutine.TakePendingResumeValue());
        }

        var value = Evaluate(node.Operand);
        if (ReferenceEquals(value, YieldSentinel)) return YieldSentinel;

        if (value is Task task)
        {
            // A task that is ALREADY finished costs no suspension: await reads its result directly, and a faulted
            // task throws here, which is the C# rule.
            if (task.IsCompleted) return task.Result;

            // A FILE'S TOP LEVEL cannot suspend: it is the outermost frame and nothing would resume it. So `await`
            // there WAITS and drives the pump, which is what `.Result` does on the same task and what a C# entry
            // point does with an async Main's Task. That is what makes the top level an async-capable entry: a script
            // may await there, and it may just as well not, the way an `async Main` still runs ordinary code.
            if (_currentCoroutine is null)
            {
                // A RESUMABLE RUN SUSPENDS INSTEAD OF WAITING: the awaited task's completion posts the resume to the
                // run queue and THIS statement is re-entered with the value, so no thread is parked inside `Task.Wait`
                // (measured, the cost of parking: a cancel issued at 372 ms was observed only at 2,639 ms, once the
                // 2,500 ms awaited call it was inside had finished). Outside such a run this stays the C# entry-point
                // rule: the top level WAITS and drives the pump.
                SuspendAsyncRun(node, task);
                WaitForTask(task, "await", node.Line, node.Column);
                return task.Result;
            }

            value = new AwaitRequest(task);
        }
        else if (value is not CoroutineYieldRequest)
        {
            RuntimeError("await expects a Task (or an awaitable request such as Coroutine.Sleep).", node.Line, node.Column);
        }

        // ONE await per statement, refused at the moment the loop would BEGIN rather than by a static rule: a body is
        // resumed by RE-ENTERING its statement, so a second await reached in the same pass re-issues the first and
        // suspends for ever (measured: `(await a) + (await b)` never finished, and the first await is re-issued on
        // every pass). Refusing here means a shape whose second await sits in a branch that is not taken keeps
        // working, since that pass never reaches it.
        if (_currentCoroutine is { IsGenerator: false, Tree: { } currentPass } && currentPass.AwaitConsumedStatement == _currentStatementIndex)
            RuntimeError("'await' can be used once per statement: this statement already consumed an awaited value, and a second await in the same pass would re-issue the first for ever, because a suspended statement is resumed by re-entering itself. Bind each to a local instead: 'var x = await a; var y = await b;'", node.Line, node.Column);

        // The enclosing frame must RE-ENTER this statement on resume: an `await` consumes the pump's value by
        // re-executing the statement (that is what _expectResumeValue means), so a frame that recorded "the next
        // statement" skipped the assignment entirely, which is why `if (c) { got = await f(); }` left `got`
        // untouched. VisitYieldExpr records +1 because a plain VALUE yield is finished once it produced its value.
        var baseSp = _currentCoroutine?.Tree?.LoopBaseSp ?? 0;
        if (_loopStackSp > baseSp)
            _loopStack[_loopStackSp - 1].BodyStatementIndex = _currentStatementIndex;
        _yieldValue = value;
        _isYield = true;
        return YieldSentinel;
    }

    /// <summary>The value an `await` resumed with. The awaited Task owns the result AND the exception rule, so a
    /// faulted await rethrows the task's own exception exactly as `.Result` would, at the await's own position.</summary>
    internal static object? AwaitOutcome(object? payload)
    {
        return payload is AwaitRequest aw ? aw.Task.Result : payload;
    }

    public object? VisitVariableExpr(VariableExpr node)
    {
        if (node.Name == "this")
        {
            foreach (var (scope, _) in CurrentFrame.Scopes.GetScopesWithConst())
            {
                if (scope.TryGetValue("this", out var thisValue))
                    return thisValue;
            }

            var snapshot = CurrentFrame.Scopes.ParentSnapshot;
            if (snapshot != null)
            {
                foreach (var localScope in snapshot.LocalScopes)
                {
                    if (localScope.TryGetValue("this", out var thisVal))
                        return thisVal;
                }
                if (snapshot.GlobalScope.TryGetValue("this", out var thisValue))
                    return thisValue;
            }

            if (_currentThis != null)
                return _currentThis;

            RuntimeError("'this' used outside of a method.", node.Line, node.Column);
            return null;
        }

        // Static field access takes priority over scope (prevents RefCell capture issues)
        if (_currentClass?.StaticFields?.TryGetValue(node.Name, out var sfVal) == true)
            return sfVal;

        // Struct static field access (structs don't use ClassInfo)
        if (_currentClass == null && _currentThis == null)
        {
            foreach (var kv in StructStaticFields)
                if (kv.Value.TryGetValue(node.Name, out var ssv))
                    return ssv;
        }

        if (CurrentFrame.Scopes.TryGet(node.Name, out var value))
            return ReadBinding(value, node.Name, node.Line, node.Column);

        // A block that NAMED its receiver (`extension(string s)`) makes that name a second spelling of `this`
        // inside every member of the block, so it resolves before the closure and before any global of the same
        // name, a member's own parameter or local of that name was found by the lookup above, and still wins.
        if (_currentReceiverName != null && _currentThis != null
            && node.Name.Equals(_currentReceiverName, Comparison))
            return _currentThis;

        var parentSnapshot = CurrentFrame.Scopes.ParentSnapshot;
        if (parentSnapshot != null)
        {
            for (var i = parentSnapshot.LocalScopes.Count - 1; i >= 0; i--)
            {
                var localScope = parentSnapshot.LocalScopes[i];
                if (localScope.TryGetValue(node.Name, out var snapshotVal))
                    return ReadBinding(snapshotVal, node.Name, node.Line, node.Column);
            }

            if (parentSnapshot.GlobalScope.TryGetValue(node.Name, out var snapshotValue))
                return ReadBinding(snapshotValue, node.Name, node.Line, node.Column);
        }

        if (_currentThis is ClassInstance instance)
        {
            var (declaringClass, field) = FindFieldInHierarchy(instance.Class, node.Name);
            if (field != null)
            {
                CheckAccess(field.Access, declaringClass, node.Line, node.Column);
                if (instance.FieldValues.TryGetValue(field.Name, out var fieldValue))
                    return fieldValue;
                RuntimeError($"Field '{field.Name}' not initialized.", node.Line, node.Column);
            }

            // A PROPERTY of the class answers by its bare name too, which the FIELD branch above does not cover: a
            // bare `X` reported "Undefined variable 'X'" for an auto-property inside the class's own method
            // (measured, both engines). The shared member getter resolves it through the backing field.
            if (FindPropertyDecl(instance.Class, node.Name) != null)
                return GetClassMemberValue(instance, node.Name, node.Line, node.Column);

            var (eventDeclaringClass, eventDecl) = FindEventInHierarchy(instance.Class, node.Name);
            if (eventDecl != null)
            {
                CheckAccess(eventDecl.Access, eventDeclaringClass, node.Line, node.Column);
                if (instance.Events.TryGetValue(node.Name, out var evtInfo)) return evtInfo;
                evtInfo = new EventInfo(node.Name, eventDecl.DelegateTypeName, []);
                instance.Events[node.Name] = evtInfo;
                return evtInfo;
            }
        }

        // A STRUCT's own member reads its fields by name too, a class did, a struct reported "Undefined variable
        // 'A'" (measured), which is C#-inconsistent and was the tree half of a divergence: the VM's compiled body
        // had no field set either (Program.cs passed none for a struct). Both halves are fixed together.
        if (_currentThis is StructInstance structInstance)
        {
            var structDecl = structInstance.Declaration
                             ?? (Structs.TryGetValue(structInstance.StructName, out var sd) ? sd : null);
            var structField = FindByName(structDecl?.Fields, node.Name, static f => f.Name);
            if (structField != null)
            {
                CheckStructMemberAccess(structField.Access, structInstance.StructName, node.Line, node.Column, node.Name);
                if (structInstance.Fields.TryGetValue(structField.Name, out var structFieldValue))
                    return structFieldValue;
                RuntimeError($"Field '{structField.Name}' not initialized.", node.Line, node.Column);
            }
            // A PROPERTY of the struct answers by its bare name too, through its backing field: `this.X` resolved
            // while a bare `X` reported "Undefined variable 'X'" inside the struct's own method (measured), which is
            // the same gap the WRITE path below had. The value of an auto-property IS its backing field.
            var bareProperty = FindByName(structDecl?.Properties, node.Name, static p => p.Name);
            if (bareProperty != null)
            {
                CheckStructMemberAccess(bareProperty.Access, structInstance.StructName, node.Line, node.Column, node.Name);
                var backingName = BackingFieldName(bareProperty.Name);
                if (structInstance.Fields.TryGetValue(backingName, out var propertyValue))
                    return propertyValue;
                RuntimeError($"Property '{bareProperty.Name}' not initialized.", node.Line, node.Column);
            }
        }

        // Nested class/struct resolution by short name (e.g. PrivateInner inside its containing class)
        var containingClass = _currentClass ?? (_currentThis is ClassInstance ci ? ci.Class : null);
        if (containingClass != null)
        {
            var nested = FindNestedByName(containingClass.NestedClasses, node.Name, static nc => nc.Name);
            if (nested != null)
            {
                CheckAccess(nested.Access, containingClass, node.Line, node.Column, node.Name);
                if (Classes.TryGetValue(nested.Name, out var nestedCi))
                    return nestedCi;
                return nested;
            }
            var nestedStruct = FindNestedByName(containingClass.NestedStructs, node.Name, static ns => ns.Name);
            if (nestedStruct != null)
            {
                CheckAccess(nestedStruct.Access, containingClass, node.Line, node.Column, node.Name);
                if (CurrentFrame.Scopes.TryGet(nestedStruct.Name, out var tiVal) && tiVal is TypeInfo)
                    return tiVal;
                return nestedStruct;
            }
        }

        // Class name fallback, static methods may reference their own class
        if (Classes.TryGetValue(node.Name, out var classVal))
            return classVal;

        // Type-name fallback, the LAST resort, so nothing above can be shadowed by it: a name that resolves to
        // nothing but IS a registered type is a type value, which is what lets `ComNode.Make(...)` or
        // `SqliteRow.Empty()` reach a static member of a type the script does not own. (`is`/`as`/`typename` go
        // through the same gated lookup, so a misspelling still reports and strict mode still demands the
        // registered spelling.) The VM's global-load miss calls the same helper, see ResolveTypeNameAsValue.
        if (ResolveTypeNameAsValue(node.Name) is { } bareTypeValue)
            return bareTypeValue;

        // THE ENGINE'S SINGLE GLOBAL READER, consulted LAST so that nothing which resolves above can change meaning:
        // `Scopes.TryGet` walks the live chain and the closure snapshot is the tree's own view, while this is the
        // door the VM's LOAD_GLOBAL_FAST miss uses (the largest-scope single-name lookup). The two views are NOT the
        // same dictionary once a frame holds more than one scope, and a binding published by a compiled top-level
        // statement is in this one only: measured, a module function reading a namespace its own module had imported
        // answered `Undefined variable 'My'` from a compiled top level and worked from a tree-walked one, because the
        // tree leg's live scope happened to hold it. Asking the same reader the VM asks makes the two engines agree
        // about what a GLOBAL is, without touching the precedence of a local, a parameter, a field, a class or a type.
        if (ResolveGlobalValue(node.Name) is { } resolvedGlobal && resolvedGlobal is not VmFallback)
            return resolvedGlobal;

        // (No namespace fallback here: namespace members are also bound in the root global
        // scope and tree functions rebuild from it, the VM CALL fallback covers bundles.)
        RuntimeError($"Undefined variable '{node.Name}'.", node.Line, node.Column, ErrorCode.UndefinedVariable);
        return null;
    }

    private static readonly HashSet<string> ObjectTypeNames = ["Object", "object", "object?"];

    public object? VisitAssignExpr(AssignExpr node)
    {
        // Static field write takes priority over scope (prevents RefCell capture issues)
        if (_currentClass?.StaticFields?.ContainsKey(node.Name) == true)
        {
            var val = Evaluate(node.Value);
            if (ReferenceEquals(val, YieldSentinel))
                return YieldSentinel;

            _currentClass.StaticFields![node.Name] = val;
            return val;
        }

        // Struct static field write
        if (_currentClass == null)
        {
            foreach (var kv in StructStaticFields)
                if (kv.Value.ContainsKey(node.Name))
                {
                    var val = Evaluate(node.Value);
                    if (ReferenceEquals(val, YieldSentinel))
                        return YieldSentinel;

                    kv.Value[node.Name] = val;
                    return val;
                }
        }

        if (CurrentFrame.Scopes.TryGetVariableInfo(node.Name, out var info))
        {
            var val = Evaluate(node.Value);
            if (ReferenceEquals(val, YieldSentinel))
                return YieldSentinel;

            // Re-fetch after evaluating the RHS: the RHS (e.g. a capturing lambda) may have promoted
            // the target local to a RefCell during its creation, the write must go through that cell,
            // not clobber it with a raw store (two-step binding: fibPair8 = (n) => fibPair8(n-1)).
            if (CurrentFrame.Scopes.TryGetVariableInfo(node.Name, out var freshInfo))
                info = freshInfo;

            if (info.Value is RefCell rc)
            {
                rc.Set(val);
                return val;
            }

            // Warn when assigning a value of incompatible type to a strongly-typed variable
            var typeName = info.TypeName;
            if (!string.IsNullOrEmpty(typeName) && !ObjectTypeNames.Contains(typeName))
            {
                var valTypeName = val?.GetType().Name ?? "null";
                // Map CLR type names to script type names for comparison
                valTypeName = TypeSystem.FromClrName(valTypeName);

                // Keep the declared numeric type on reassignment (`long sum = 0; sum = 0;` must
                // stay long): widen a narrower runtime numeric to the declared type. Mirrors the
                // VM's WIDEN_NUM and the field/param CoerceTo behavior.
                switch (typeName)
                {
                    case "long" when val is int or byte or short or uint or ushort:
                        val = Convert.ToInt64(val); break;
                    case "double" when val is int or byte or short or uint or ushort or long or ulong:
                        val = Convert.ToDouble(val); break;
                    case "decimal" when val is int or byte or short or uint or ushort or long or ulong:
                        val = Convert.ToDecimal(val); break;
                    case "float" when val is int or byte or short or uint or ushort or long or ulong:
                        val = Convert.ToSingle(val); break;
                }
            }

            CurrentFrame.Scopes.Set(node.Name, val, node.Line, node.Column);
            return val;
        }

        if (_currentThis is ClassInstance instance)
        {
            var (declaringClass, field) = FindFieldInHierarchy(instance.Class, node.Name);
            if (field != null)
            {
                CheckAccess(field.Access, declaringClass, node.Line, node.Column);
                var val = Evaluate(node.Value);
                if (ReferenceEquals(val, YieldSentinel))
                    return YieldSentinel;

                var coerced = CoerceTo(val, field.Type, node.Line, node.Column);
                instance.FieldValues[field.Name] = coerced;
                return coerced;
            }

            // ...and a bare WRITE reaches a PROPERTY the same way, through the shared member setter, which owns the
            // coercion, the setter check and the init-only guard. Measured: `X = v` inside the class's own method
            // reported "Undefined variable 'X'" on both engines.
            if (FindPropertyDecl(instance.Class, node.Name) != null)
            {
                var propertyVal = Evaluate(node.Value);
                if (ReferenceEquals(propertyVal, YieldSentinel))
                    return YieldSentinel;
                return SetClassMemberValue(instance, node.Name, propertyVal, node.Line, node.Column);
            }
        }

        // A STRUCT's own member writes its fields by name too, which is the mirror of the read path above and was
        // MISSING: a bare `V = a` inside a struct method fell through to the error below, while the VM's compiled
        // body resolved the same name as `this.V` through SET_PROP, so one source worked or failed by ENGINE.
        // Measured: a bare field write in a struct method failed with a plain parameter, with no parameter at all,
        // and with a ref parameter (which keeps the body off the VM), while a struct CONSTRUCTOR's field write
        // worked. The member setter is the shared one, so the access check and the coercion are its own.
        if (_currentThis is StructInstance structInstance)
        {
            var structDecl = structInstance.Declaration
                             ?? (Structs.TryGetValue(structInstance.StructName, out var sd) ? sd : null);
            // A FIELD or a PROPERTY of the struct, by its bare name: the shared member setter resolves a property
            // through its backing field (with the setter and init-only checks), so only the test was missing a
            // property here, which is why a bare `X = v` reported "Undefined variable 'X'" for an auto-property.
            var isStructMember = structDecl != null
                              && (structDecl.Fields.Any(f => f.Name.Equals(node.Name, Comparison))
                               || structDecl.Properties.Any(p => p.Name.Equals(node.Name, Comparison)));
            if (isStructMember)
            {
                var val = Evaluate(node.Value);
                if (ReferenceEquals(val, YieldSentinel))
                    return YieldSentinel;
                return SetStructMemberValue(structInstance, node.Name, val, node.Line, node.Column);
            }
        }

        RuntimeError($"Undefined variable '{node.Name}'.", node.Line, node.Column, ErrorCode.UndefinedVariable);
        return null;
    }

    public object? VisitBinaryExpr(BinaryExpr node)
    {
        var left = Evaluate(node.Left);
        if (ReferenceEquals(left, YieldSentinel))
            return YieldSentinel;

        var right = Evaluate(node.Right);
        if (ReferenceEquals(right, YieldSentinel))
            return YieldSentinel;

        // String concatenation, check before widening so chars stay as chars. Both sides go through the ONE
        // display rule (ValueFormatter.ToDisplay) rather than ToString, so a `Type` operand shows the name a
        // script can write here exactly as it does in print(); interpolation desugars into this same node.
        if (node.Operator.Type == TokenType.Plus && (left is string || right is string))
            return string.Concat(ValueFormatter.ToDisplay(left), ValueFormatter.ToDisplay(right));

        // Widen small integer types to int so arithmetic stays precise
        left = WidenSmallInt(left);
        right = WidenSmallInt(right);

        return left switch
        {
            int li when right is int ri => IntOp(li, ri, node.Operator.Type, node.Line, node.Column),
            int li when right is long rl => LongOp(li, rl, node.Operator.Type, node.Line, node.Column),
            long ll when right is long rl => LongOp(ll, rl, node.Operator.Type, node.Line, node.Column),
            long ll when right is int ri => LongOp(ll, ri, node.Operator.Type, node.Line, node.Column),
            _ => ApplyBinaryOp(left, right, node.Operator.Type, node.Line, node.Column)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object? IntOp(int a, int b, TokenType op, int line, int column) => op switch
    {
        TokenType.Plus => a + b,
        TokenType.Minus => a - b,
        TokenType.Star => a * b,
        TokenType.Slash => b == 0
            ? RuntimeError<object>("Division by zero.", line, column)
            : a / b,
        TokenType.Percent => b == 0
            ? RuntimeError<object>("Modulo by zero.", line, column)
            : a % b,
        TokenType.EqualEqual => a == b,
        TokenType.BangEqual => a != b,
        TokenType.Less => a < b,
        TokenType.Greater => a > b,
        TokenType.LessEqual => a <= b,
        TokenType.GreaterEqual => a >= b,
        TokenType.Ampersand => a & b,
        TokenType.Pipe => a | b,
        TokenType.Caret => a ^ b,
        TokenType.LeftShift => a << b,
        TokenType.RightShift => a >> b,
        _ => null
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object? LongOp(long a, long b, TokenType op, int line, int column) => op switch
    {
        TokenType.Plus => a + b,
        TokenType.Minus => a - b,
        TokenType.Star => a * b,
        TokenType.Slash => b == 0
            ? RuntimeError<object>("Division by zero.", line, column)
            : a / b,
        TokenType.Percent => b == 0
            ? RuntimeError<object>("Modulo by zero.", line, column)
            : a % b,
        TokenType.EqualEqual => a == b,
        TokenType.BangEqual => a != b,
        TokenType.Less => a < b,
        TokenType.Greater => a > b,
        TokenType.LessEqual => a <= b,
        TokenType.GreaterEqual => a >= b,
        TokenType.Ampersand => a & b,
        TokenType.Pipe => a | b,
        TokenType.Caret => a ^ b,
        TokenType.LeftShift => a << (int)b,
        TokenType.RightShift => a >> (int)b,
        _ => null
    };

    public object? VisitUnaryExpr(UnaryExpr node)
    {
        var right = Evaluate(node.Right);
        if (ReferenceEquals(right, YieldSentinel))
            return YieldSentinel;

        return node.Operator.Type switch
        {
            TokenType.Minus => ApplyUnaryMinus(right, node.Line, node.Column),
            TokenType.Bang => !IsTruthy(right),
            TokenType.Tilde => ApplyBitwiseComplement(right, node.Line, node.Column),
            _ => RuntimeError<object>($"Unsupported unary operator {node.Operator.Type}", node.Line, node.Column)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object ApplyUnaryMinus(object? value, int line, int column)
    {
        if (value == null)
            RuntimeError("Cannot apply unary minus to null.", line, column);

        return Type.GetTypeCode(value.GetType()) switch
        {
            TypeCode.Int32 => -(int)value,
            TypeCode.Int64 => -(long)value,
            TypeCode.Single => -(float)value,
            TypeCode.Double => -(double)value,
            TypeCode.Decimal => -(decimal)value,
            TypeCode.Int16 => -(short)value,
            TypeCode.Byte => -(byte)value,
            TypeCode.SByte => -(sbyte)value,
            _ => RuntimeError<object>($"Unary minus not defined for type {GetFriendlyTypeNameOfValue(value)}.", line, column)
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object ApplyBitwiseComplement(object? value, int line, int column)
    {
        if (value == null)
            RuntimeError("Cannot apply bitwise complement to null.", line, column);

        return Type.GetTypeCode(value.GetType()) switch
        {
            TypeCode.Int32 => ~(int)value,
            TypeCode.UInt32 => ~(uint)value,
            TypeCode.Int64 => ~(long)value,
            TypeCode.UInt64 => ~(ulong)value,
            TypeCode.Int16 => ~(short)value,
            TypeCode.UInt16 => ~(ushort)value,
            TypeCode.Byte => ~(byte)value,
            TypeCode.SByte => ~(sbyte)value,
            TypeCode.Char => ~(char)value,
            _ => RuntimeError<object>($"Bitwise complement operator '~' not defined for type {GetFriendlyTypeNameOfValue(value)}.", line, column)
        };
    }

    /// <summary>Task 4: wraps a non-IEnumerable value that satisfies the unified Iterable contract
    /// (tuple, dict, HashSet, coroutine generator) into an IEnumerable&lt;object?&gt; so the LINQ
    /// extension methods resolve on it. Generator/coroutine sequences are materialized once,
    /// LINQ over a generator enumerates the full sequence (documented behavior).</summary>
    private static IEnumerable<object?> WrapIterableForLinq(object? value)
    {
        if (TryGetIterableElements(value, out var elements)) return elements;
        switch (value)
        {
            case Generator<object?> gen: return MaterializeCoroutine(gen.InnerCoroutine);
            case Coroutine co: return MaterializeCoroutine(co);
            default: throw new ArgumentException("value is not iterable");
        }
    }

    /// <summary>Resumes a coroutine to its end, yielding each yielded value, the LINQ materialization
    /// for generator/coroutine receivers (mirrors the tree-walker's foreach generator branch semantics).</summary>
    private static IEnumerable<object?> MaterializeCoroutine(Coroutine co)
    {
        if (co.Status == CoroutineStatus.NotStarted) co.Resume();
        while (co.IsResumable)
        {
            yield return co.LastYieldedValue;
            co.Resume();
        }
    }

    public object? VisitCallExpr(CallExpr node)
    {
        var value = VisitCallExprCore(node);
        // A ref-RETURNING call answers a REFERENCE (a cell); every use of it as a VALUE dereferences, exactly as a read
        // of a local that holds a cell does. `ref F(x)` and `F(x) = 7;` ask for the reference itself (WantsRefResult).
        if (!node.WantsRefResult && value is RefCell returned) return returned.Get();
        return value;
    }

    private object? VisitCallExprCore(CallExpr node)
    {
        // No `_currentLine` stamp here: that field means ONE thing, the STATEMENT being executed (written by
        // Execute(Stmt) and by the VM's LOC arm), because that is the position a refusal reports. Stamping the
        // call node for its duration made a bare call to an undefined name answer with the call's column where
        // every other refusal answered with the statement's, i.e. two rules for one question.
        {
            // A QUALIFIED call names the contract: `IShape.area(value, ...)`. An explicit implementation is
            // deliberately absent from the receiver's name table, so the contract is how it is reached, and the
            // receiver arrives as the first argument. A plain member is still called on the value itself.
            object? callee = null;
            var isImplicitThisCall = false;

            if (node.Callee is VariableExpr varExpr &&
                _currentClass != null &&
                _currentThis is ClassInstance currentInstance &&
                !CurrentFrame.Scopes.TryGet(varExpr.Name, out _))
            {
                var (declaringClass, method) = FindMethodInHierarchy(_currentClass, varExpr.Name);
                if (method != null)
                {
                    CheckAccess(method.Access, declaringClass, node.Line, node.Column);
                    isImplicitThisCall = true;
                    callee = new BoundMethod(currentInstance, method);
                }
            }

            // A STRUCT'S SIBLING METHOD, by bare name, which a class already had above and a struct did not: the suite's
            // Counter.CallSibling reported "Undefined variable 'Total'" while its FIELDS resolved (that fix is separate) and
            // a class's method worked, and that asymmetry is what hid it. It binds the SAME BoundMethod(Instance, Method)
            // record the class path binds, so there is one shape and one dispatch rather than a struct-shaped twin, which is
            // why the arm that invokes it needed a GUARD: that arm casts Instance to ClassInstance, and a StructInstance is
            // not one. One branch covers both engines, because the VM refuses such a call and falls back here. No access
            // check: the caller IS the struct, so a bare call to its own member is reachable by construction.
            if (!isImplicitThisCall &&
                node.Callee is VariableExpr structCall &&
                _currentThis is StructInstance structThis &&
                !CurrentFrame.Scopes.TryGet(structCall.Name, out _))
            {
                var structMethod = FindByName(structThis.Declaration?.Methods, structCall.Name, static m => m.Name);
                if (structMethod != null)
                {
                    isImplicitThisCall = true;
                    callee = new BoundMethod(structThis, structMethod);
                }
            }

            if (!isImplicitThisCall)
            {
                callee = Evaluate(node.Callee);
                if (ReferenceEquals(callee, YieldSentinel)) return YieldSentinel;
                if (node.Callee is PropertyAccessExpr propAccess)
                {
                    var receiver = Evaluate(propAccess.Target);
                    if (ReferenceEquals(receiver, YieldSentinel)) return YieldSentinel;
                    if (receiver is ClassInstance instance)
                    {
                        var evtx = GetEventInfo(instance, propAccess.PropertyName, node.Line, node.Column);
                        if (evtx != null) callee = evtx;
                    }
                }
            }

            bool hasRefArgs = false, hasOutArgs = false;
            if (node.IsRefArgs.Any(t => !t))
            {
                hasRefArgs = true;
            }

            if (!hasRefArgs)
            {
                // GUARDED, because `IsOutArgs` is null for a call that declares none (every reader but this one tests for
                // it, see CallExpr.Initialize): an unguarded `Any` threw ArgumentNullException("source") from the LINQ
                // overload, which reached a script as "invalid argument 'source'". Measured on a call the PARSER builds
                // without the flag list (the `typeof(<a name>)` rewrite).
                if (node.IsOutArgs is { } outs && outs.Any(t => !t))
                {
                    hasOutArgs = true;
                }
            }
            if (!hasRefArgs && !hasOutArgs)
            {
                var count = node.Arguments.Count;
                if (count == 0) return InvokeCallee(callee, [], node);
                var exactArgs = ArrayPool.Rent(count);
                try
                {
                    for (var i = 0; i < count; i++) { var a = Evaluate(node.Arguments[i]); if (ReferenceEquals(a, YieldSentinel)) return YieldSentinel; exactArgs[i] = a; }
                    return InvokeCallee(callee, exactArgs, node);
                }
                finally { ArrayPool.Return(exactArgs); }
            }

            var argCount = node.Arguments.Count;
            var exactArgs2 = new object?[argCount];
            for (var i = 0; i < argCount; i++)
            {
                if (node.IsRefArgs[i] || (node.IsOutArgs != null && i < node.IsOutArgs.Count && node.IsOutArgs[i]))
                {
                    var expr = node.Arguments[i];
                    switch (expr)
                    {
                        case DiscardExpr:
                            exactArgs2[i] = NullRefCell;
                            break;

                        case VariableExpr varExprArg:
                            exactArgs2[i] = CaptureVariableReference(varExprArg, node.Line, node.Column,
                                allowUndefined: node.IsOutArgs != null && i < node.IsOutArgs.Count && node.IsOutArgs[i],
                                byOut: node.IsOutArgs != null && i < node.IsOutArgs.Count && node.IsOutArgs[i]);
                            break;

                        // A FIELD/PROPERTY or an ELEMENT is a reference target too (spec 3: the lvalue may be a
                        // variable, a field or an element). One resolver builds the cell, so a reference and a
                        // compound assignment write through the same storage, the indices are evaluated ONCE, and
                        // an access check or an event is judged in that one place.
                        case PropertyAccessExpr:
                        case ArrayAccessExpr:
                            exactArgs2[i] = RefCellForTarget(expr, node.IsOutArgs != null && i < node.IsOutArgs.Count && node.IsOutArgs[i],
                                node.Line, node.Column);
                            break;

                        default:
                            RuntimeError("Only a variable, a field or an element can be passed by ref/out: a literal, a call and an operator result have no storage to write back to.", node.Line, node.Column);
                            break;
                    }
                }
                else
                {
                    var a = Evaluate(node.Arguments[i]);
                    if (ReferenceEquals(a, YieldSentinel)) return YieldSentinel;
                    exactArgs2[i] = a;
                }
            }
            return InvokeCallee(callee, exactArgs2, node);
        }
    }

    private object? InvokeCallee(object? callee, object?[] args, CallExpr node)
    {
        // A free-function NAME holding several signatures: ONE picker, the same one the VM's CALL arm asks, so a
        // walked body and a compiled one resolve an overload identically. The call's NAMES travel with the
        // arguments, because which member the names fit is part of choosing one (C#'s rule): with a set holding
        // `P(int, string)` and `P(string, int)`, `P(b: 1, a: "s")` is the second, and picking positionally chose
        // the first and then coerced against it.
        if (callee is ScriptOverloadSet overloads)
        {
            var overloadName = node.Callee is VariableExpr ve ? ve.Name : "?";
            var picked = PickOverload(overloads, args, overloadName, node.Line, node.Column, node.ArgNames);
            // A NAMED call was weighed BY NAME to choose the member, so it must also be BOUND by name: the same
            // mapping, asked of the declaration that won (the VM's CALL arm reorders at the same point, for the
            // same reason). Without this the arguments went in positionally against a member chosen by name,
            // which coerced `P(b: 1, a: "s")` against `(int, string)` and refused.
            var boundArgs = node.ArgNames is { Count: > 0 }
                ? ReorderNamedArgs(args, node.ArgNames, picked.Declaration.Params, node.Line, node.Column)
                : args;
            return InvokeScriptFunction(picked, boundArgs, node.Line, node.Column, wantRefResult: node.WantsRefResult);
        }

        // Pass explicit type arguments if the call expression carries them
        var typeArgs = node.TypeArgs;

        switch (callee)
        {
            case SuperProxy superProxy when _inConstructor:
                // ONE implementation, shared with the VM's CALL_VAL: a compiled constructor body that calls
                // `super()` used to bail there, and a body loaded from bytecode has no AST to fall back to.
                return InvokeSuperConstructor(superProxy, args, node.Line, node.Column);

            case EventInfo evtDirect:
                {
                    object? lastResult = null;
                    foreach (var subscriber in evtDirect.Subscribers)
                        lastResult = InvokeScriptFunction(subscriber, args, node.Line, node.Column, wantRefResult: node.WantsRefResult);

                    if (!_eventWaiters.TryGetValue(evtDirect, out var waiters))
                        return lastResult;

                    foreach (var waitingCoroutine in waiters.ToList())
                    {
                        var wt = waitingCoroutine.Tree;
                        if (wt != null) wt.PendingResumeValue = null;
                        waitingCoroutine.Resume();
                    }
                    waiters.Clear();
                    return lastResult;
                }
            // A struct's bound method arrives in the same record a class's does, so the guard is where the two part: the
            // arm below casts to ClassInstance, which a StructInstance is not.
            case BoundMethod bound when bound.Instance is StructInstance structBound:
                return InvokeMethodOnStructInstance(structBound, bound.Method,
                    ReorderNamedArgs(args, node.ArgNames, bound.Method.Params, node.Line, node.Column), node.Line, node.Column);

            case BoundMethod bound:
                return InvokeMethodOnInstance((ClassInstance)bound.Instance, bound.Method, ReorderNamedArgs(args, node.ArgNames, bound.Method.Params, node.Line, node.Column), node.Line, node.Column);

            case CombinedDelegate combined:
                return combined.Invoke(args, node.Line, node.Column);

            case ScriptDelegateWrapper wrapper:
                return wrapper.Invoke(args);

            case ScriptFunction fn:
                {
                    var fnDecl = fn.Declaration;
                    var callArgs = ReorderNamedArgs(args, node.ArgNames, fnDecl.Params, node.Line, node.Column);
                    // Tail-call detection: self-recursion in tail position (depth == 2 = return's direct child via Evaluate)
                    if (_returnExprDepth == 2 && _currentFuncDecl != null && ReferenceEquals(fnDecl, _currentFuncDecl))
                    {
                        _tailCallArgs = callArgs;
                        _tailCallLine = node.Line;
                        _tailCallColumn = node.Column;
                        return TailCallSentinel;
                    }
                    // ONE arity check, shared with the VM's CALL / CALL_VAL handlers (Interpreter.CheckScriptCallArity):
                    // the two paths used to disagree, the tree refused extra arguments here while the VM accepted
                    // them silently, which is how a callback's arguments ended up in its capture slots.
                    CheckScriptCallArity(fnDecl, callArgs.Length, node.Line, node.Column);
                    // A GENERIC declaration reached through a ScriptFunction still instantiates: the FuncDecl arm a
                    // few cases below does this for a name bound to the declaration itself (which is what a source
                    // run binds), while a LOADED function, and a function held in a VARIABLE, arrive here. Measured
                    // without it: `Identity<long>(42)` answered `typeof` "int" from a stripped bundle where the file
                    // answers "long", because the template ran with no coercion at all.
                    if (node.TypeArgs is { Count: > 0 } && fnDecl.TypeParameters.Count > 0)
                    {
                        var genericConcrete = InstantiateGenericFunction(fnDecl, callArgs, node.Line, node.Column,
                            node.TypeArgs);
                        if (genericConcrete != null)
                            return InvokeScriptFunction(genericConcrete, callArgs, node.Line, node.Column, wantRefResult: node.WantsRefResult);
                    }
                    return InvokeScriptFunction(fn, callArgs, node.Line, node.Column, wantRefResult: node.WantsRefResult);
                }

            case INativeFunction native:
                try
                {
                    var result = native.Invoke(args);
                    if (native.ReturnType == TokenType.Void && result != null)
                        RuntimeError($"Native function '{native.Name}' declared void but returned a value.", node.Line,
                            node.Column);
                    return result;
                }
                catch (ScriptException ex) when (AbortExitCode(ex) is not null)
                {
                    // A script's own exit() must not be turned into an error by the arm below: the ONE
                    // question that decides it is AbortExitCode, which also recognizes an abort a call site
                    // rerolled (that one is no longer a ScriptAbortException).
                    throw;
                }
                catch (Exception ex)
                {
                    ThrowException($"[{native.Name}]: {FriendlyExceptionMessage(ex)}", node.Line, node.Column); return null;
                }

            case Delegate del:
                try
                {
                    return del.DynamicInvoke(args);
                }
                catch (TargetInvocationException ex)
                {
                    ThrowException($"[Delegate]: {ex.InnerException?.Message ?? ex.Message}", node.Line, node.Column);
                    return null;
                }
                catch (Exception ex)
                {
                    ThrowException($"[Delegate]: {FriendlyExceptionMessage(ex)}", node.Line, node.Column); return null;
                }

            // Generic function instantiation, callee is a FuncDecl template
            case FuncDecl { TypeParameters.Count: > 0 } genericFunc:
                {
                    var concrete = InstantiateGenericFunction(genericFunc, args, node.Line, node.Column, typeArgs);
                    if (concrete != null)
                        return InvokeScriptFunction(concrete, args, node.Line, node.Column, wantRefResult: node.WantsRefResult);
                    break;
                }
        }

        // Static method call, FuncDecl without type params
        if (callee is FuncDecl { TypeParameters.Count: 0 } staticFunc)
        {
            var mainFrame = _rootFrame;
            var realGlobalScope = mainFrame.Scopes.GetGlobalScopeDictionary();
            var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(realGlobalScope);
            var paramTypes = GetParameterTypes(staticFunc.Params);
            var flags = (staticFunc.IsPure ? FunctionFlags.Pure : FunctionFlags.None)
                      | (staticFunc.IsCoroutine ? FunctionFlags.Coroutine : FunctionFlags.None);
            var sf = new ScriptFunction(staticFunc, snapshot, staticFunc.ReturnType, paramTypes, staticFunc.IsRefParams, Flags: flags, IsOutParams: staticFunc.IsOutParams);
            return InvokeScriptFunction(sf, args, node.Line, node.Column, wantRefResult: node.WantsRefResult);
        }

        var calleeName = callee is Type t ? t.Name : callee?.GetType().Name ?? "null";
        var prefix = callee is Type ? "Type " : "";
        RuntimeError($"{prefix}'{calleeName}' is not a function.", node.Line, node.Column);
        return null;
    }

    /// <remarks>A script can read a <c>Type</c> value's OWN members (<c>s.GetType().Name</c> resolves against
    /// <c>typeof(Type)</c>, see the lookup below), which is reflection over <c>System.Type</c> whose members carry
    /// <see cref="DynamicallyAccessedMembersAttribute"/> on parameters or return values that the trimmer cannot
    /// verify: that is IL2111, and rooting <c>typeof(Type)</c> instead makes it WORSE rather than better (measured:
    /// it reports one warning per annotated member of <c>Type</c>, GetConstructor, GetConstructors, GetEvent and
    /// friends). The requirement cannot be satisfied from here in general, because the types a script reflects over
    /// are the script's own, which is the same reason the IL2075 and IL2070 entries in <c>AotHints</c> carry.</remarks>
    [UnconditionalSuppressMessage("Trim", "IL2111",
        Justification = "Type.GetProperty/GetField over typeof(Type): script-visible Type members on a script-supplied Type value")]
    public object? VisitPropertyAccessExpr(PropertyAccessExpr node)
    {
        var target = Evaluate(node.Target);
        if (ReferenceEquals(target, YieldSentinel))
            return YieldSentinel;

        switch (target)
        {
            case null:
                RuntimeError($"Cannot access property '{node.PropertyName}' on null.", node.Line, node.Column);
                break;

            case Type type:
                {
                    var prop = type.GetProperty(node.PropertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (prop != null) return prop.GetValue(null);
                    var field = type.GetField(node.PropertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (field != null) return field.GetValue(null);
                    var nestedType = type.GetNestedType(node.PropertyName, BindingFlags.Public | BindingFlags.IgnoreCase);
                    if (nestedType != null) return nestedType;
                    // A STATIC extension property is the last resort here, as it is on the value path.
                    if (TryGetExtensionProperty(type, node.PropertyName, node.Line, node.Column, out var staticExt))
                        return staticExt;
                    // The CLR's OWN view of the type, before refusing. A script that holds a Type value
                    // (x.GetType()) can read nothing else about it: Type.Name, Type.FullName, Type.BaseType,
                    // Type.IsEnum and the rest are properties of the Type OBJECT, not static members of the type
                    // it describes, so the two lookups above can never find them. Measured: `s.GetType()` printed
                    // Button while `s.GetType().Name` refused with "Static member 'Name' not found on type
                    // Button". The statics are tried FIRST, so a type that really has a static of that name (a
                    // DockStyle.Bottom, an enum value) keeps the meaning it always had.
                    var typeOwnProp = typeof(Type).GetProperty(node.PropertyName,
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                    if (typeOwnProp != null) return typeOwnProp.GetValue(type);
                    RuntimeError($"Static member '{node.PropertyName}' not found on type {ReportedTypeName(type)}.", node.Line, node.Column);
                    break;
                }

            case Namespace ns:
                {
                    var prop = ns.GetProperty(node.PropertyName);
                    if (prop != null) return prop;
                    var child = ns.GetChild(node.PropertyName);
                    if (child != null) return child;
                    RuntimeError(ns.MemberMissText(node.PropertyName, false), node.Line, node.Column,
                        ErrorCode.UndefinedVariable);
                    break;
                }

            case StructInstance si:
                {
                    // Enforce field access modifiers (private struct fields are not public)
                    if (Structs.TryGetValue(si.StructName, out var sd1))
                    {
                        var sf1 = FindByName(sd1.Fields, node.PropertyName, static f => f.Name);
                        if (sf1 != null)
                            CheckStructMemberAccess(sf1.Access, si.StructName, node.Line, node.Column, node.PropertyName);
                    }
                    var sBacking = GetStructBackingField(si, node.PropertyName);
                    if (sBacking != null && si.Fields.TryGetValue(sBacking, out var spv)) return spv;
                    if (si.Fields.TryGetValue(node.PropertyName, out var sv)) return sv;
                    // A script struct is a receiver like any other: its own members win, then a block's property.
                    if (TryGetInterfacePropertyDefault(si, node.PropertyName, node.Line, node.Column, out var siIfaceProp)) return siIfaceProp;
                    if (TryGetExtensionProperty(si, node.PropertyName, node.Line, node.Column, out var siExt))
                        return siExt;
                    // A METHOD named where a FIELD would be is its OWN VALUE: a callable that carries its receiver, so
                    // a method can be HANDED somewhere (`http.Use(app.Handle)`, a route handler, an event handler).
                    // It is the LAST resort, after every field, backing field, interface default and block property,
                    // so nothing that resolved before changed meaning, and it mirrors the `base.Method` arm (the one
                    // place a bound method was already reachable from a script). Measured before it: `obj.Handle`
                    // reported "Field or event 'Handle' not found in class", which is what made a bound method
                    // impossible to write at all.
                    if (Structs.TryGetValue(si.StructName, out var sdM))
                    {
                        var structMethod = FindByName(sdM.Methods, node.PropertyName, static m => m.Name, static m => !m.IsStatic);
                        if (structMethod != null)
                        {
                            CheckStructMemberAccess(structMethod.Access, si.StructName, node.Line, node.Column, node.PropertyName);
                            return new BoundMethod(si, structMethod);
                        }
                    }
                    RuntimeError($"Field '{node.PropertyName}' not found in struct '{si.StructName}'.", node.Line, node.Column);
                    break;
                }
            case ClassInstance instance:
                {
                    // Property backing field access
                    var backingName = FindPropertyBackingField(instance.Class, node.PropertyName);
                    if (backingName != null)
                    {
                        return instance.FieldValues.TryGetValue(backingName, out var pv)
                            ? pv
                            : DefaultFor(FindPropertyDecl(instance.Class, node.PropertyName)!.Type);
                    }

                    var (declaringClass, field) = FindFieldInHierarchy(instance.Class, node.PropertyName);
                    if (field != null)
                    {
                        CheckAccess(field.Access, declaringClass, node.Line, node.Column);
                        if (instance.FieldValues.TryGetValue(field.Name, out var value)) return value;
                        RuntimeError($"Field '{field.Name}' not initialized.", node.Line, node.Column);
                    }

                    // An EVENT member reads through the SAME accessor the tree's compound assign uses
                    // (GetEventInfo), and it is ONE place now: this arm used to build the EventInfo itself AND
                    // run CheckAccess, which refused a private event even though `x.E += h` (the only thing the
                    // language lets a caller DO with an event) ignores the check through the other path. Measured
                    // through tools/probe-toplevel-vm.slc section 8, where a subscribe on a private event was
                    // refused by a compiled body and accepted by the tree.
                    var evtInfo = GetEventInfo(instance, node.PropertyName, node.Line, node.Column);
                    if (evtInfo != null) return evtInfo;

                    // Fallback: static field access through instance (e.g. f2.Created → Class.StaticFields["Created"])
                    {
                        var cur = instance.Class;
                        while (cur != null)
                        {
                            RunStaticConstructors(cur, node.Line, node.Column);
                            if (cur.StaticFields?.TryGetValue(node.PropertyName, out var sv) == true)
                                return sv;
                            cur = cur.Parent;
                        }
                    }
                    if (TryGetInterfacePropertyDefault(instance, node.PropertyName, node.Line, node.Column, out var ciIfaceProp)) return ciIfaceProp;
                    if (TryGetExtensionProperty(instance, node.PropertyName, node.Line, node.Column, out var ciExt)) return ciExt;
                    // The CLASS twin of the struct arm above, and the same rule: a method named where a field would be
                    // is its own value, a callable carrying its receiver. Last, so every field, event, static field,
                    // interface default and extension property still wins, and `obj.M()` is untouched (a CALL is a
                    // different node). A STATIC method is not a bound method: it has no receiver, and `C.M` already
                    // resolves through the class-name path.
                    var instanceMethod = FindMethod(instance.Class, node.PropertyName);
                    if (instanceMethod != null && !instanceMethod.IsStatic)
                    {
                        CheckAccess(instanceMethod.Access, instance.Class, node.Line, node.Column);
                        return new BoundMethod(instance, instanceMethod);
                    }
                    RuntimeError($"Field or event '{node.PropertyName}' not found in class '{instance.Class.Name}'.", node.Line, node.Column);
                    break;
                }

            case ClassInfo classInfo:
                {
                    // Ensure static constructors have run
                    RunStaticConstructors(classInfo, node.Line, node.Column);
                    // Static property access
                    var sbacking = FindPropertyBackingField(classInfo, node.PropertyName);
                    if (sbacking != null && classInfo.StaticFields?.TryGetValue(sbacking, out var spv) == true)
                        return spv;

                    // Static field access (walk parent chain)
                    object? sf = null;
                    var sfFound = false;
                    {
                        var cur = classInfo; while (cur != null)
                        {
                            if (cur.StaticFields?.TryGetValue(node.PropertyName, out sf) == true)
                            { sfFound = true; break; }
                            cur = cur.Parent;
                        }
                    }

                    if (sfFound)
                    {
                        // Find the static field's declaration for access check
                        FieldDecl? staticFieldDecl = null;
                        ClassInfo? declClass = null;
                        {
                            var cur = classInfo; while (cur != null)
                            {
                                staticFieldDecl = cur.StaticFieldDecls?.FirstOrDefault(f =>
                                    f.Name.Equals(node.PropertyName, Comparison));
                                if (staticFieldDecl != null) { declClass = cur; break; }
                                cur = cur.Parent;
                            }
                        }
                        if (staticFieldDecl != null)
                            CheckAccess(staticFieldDecl.Access, declClass, node.Line, node.Column);
                        return sf;
                    }

                    // Static method access, return as BoundMethod-like wrapper
                    var staticMethod = classInfo.StaticMethods?.FirstOrDefault(m =>
                        m.Name.Equals(node.PropertyName, Comparison));
                    if (staticMethod != null)
                        return staticMethod;

                    // Nested class access (check short name since nested classes are stored with fully-qualified names)
                    var nested = FindNestedByName(classInfo.NestedClasses, node.PropertyName, static nc => nc.Name);
                    if (nested != null)
                    {
                        CheckAccess(nested.Access, classInfo, node.Line, node.Column, node.PropertyName);
                        // Return the ClassInfo if registered, otherwise the ClassDeclStmt
                        if (Classes.TryGetValue(nested.Name, out var nestedCi))
                            return nestedCi;
                        return nested;
                    }

                    // Nested struct access
                    var nestedStruct = FindNestedByName(classInfo.NestedStructs, node.PropertyName, static ns => ns.Name);
                    if (nestedStruct != null)
                    {
                        CheckAccess(nestedStruct.Access, classInfo, node.Line, node.Column, node.PropertyName);
                        // Return TypeInfo from scope for subsequent property access
                        if (CurrentFrame.Scopes.TryGet(nestedStruct.Name, out var tiVal) && tiVal is TypeInfo)
                            return tiVal;
                        return nestedStruct;
                    }

                    if (TryGetExtensionProperty(classInfo, node.PropertyName, node.Line, node.Column, out var clsExt)) return clsExt;
                    RuntimeError($"Static member '{node.PropertyName}' not found in class '{classInfo.Name}'.", node.Line, node.Column);
                    break;
                }

            case TypeInfo { TypeName: not null } typeInfo:
                {
                    // Struct, check static fields
                    if (StructStaticFields.TryGetValue(typeInfo.TypeName, out var ssf) && ssf.TryGetValue(node.PropertyName, out var sfVal))
                        return sfVal;

                    // Struct, check static methods
                    if (Structs.TryGetValue(typeInfo.TypeName, out var sd))
                    {
                        var sm = FindByName(sd.Methods, node.PropertyName, static m => m.Name, static m => m.IsStatic);
                        if (sm != null)
                            return sm;
                    }
                    // Struct, check nested classes (e.g. OuterStruct.InnerClass)
                    if (Structs.TryGetValue(typeInfo.TypeName, out var sdn))
                    {
                        var nestedClass = FindNestedByName(sdn.NestedClasses, node.PropertyName, static nc => nc.Name);
                        if (nestedClass != null)
                        {
                            if (Classes.TryGetValue(nestedClass.Name, out var nestedCi))
                                return nestedCi;
                            return nestedClass;
                        }
                    }

                    // Struct, check nested structs (e.g. OuterStruct.InnerStruct)
                    if (Structs.TryGetValue(typeInfo.TypeName, out var sds))
                    {
                        var nestedStruct = FindNestedByName(sds.NestedStructs, node.PropertyName, static ns => ns.Name);
                        if (nestedStruct != null)
                        {
                            // Return TypeInfo from scope if registered (preferred for further property access)
                            if (CurrentFrame.Scopes.TryGet(nestedStruct.Name, out var tiVal) && tiVal is TypeInfo)
                                return tiVal;
                            return nestedStruct;
                        }
                    }

                    // A STATIC extension property of the target type is the last resort, after the struct's own
                    // fields, methods and nested types.
                    if (TryGetExtensionProperty(typeInfo, node.PropertyName, node.Line, node.Column, out var extStaticProp))
                        return extStaticProp;

                    RuntimeError($"Static member '{node.PropertyName}' not found in struct '{typeInfo.TypeName}'.", node.Line, node.Column);
                    break;
                }

            case BaseProxy baseProxy:
                {
                    var method = FindMethod(baseProxy.BaseClass, node.PropertyName);
                    if (method != null)
                    {
                        CheckAccess(method.Access, baseProxy.BaseClass, node.Line, node.Column); return new BoundMethod(baseProxy.Instance, method);
                    }
                    RuntimeError($"Method '{node.PropertyName}' not found in base class '{baseProxy.BaseClass.Name}'.", node.Line, node.Column);
                    break;
                }

            case ScriptTuple tup:
                {
                    // Item1..Item8 / Length / Count access
                    switch (node.PropertyName)
                    {
                        case "Length": return tup.Length;
                        case "Count": return tup.Count;
                        case "Item1": return tup.Item1;
                        case "Item2": return tup.Item2;
                        case "Item3": return tup.Item3;
                        case "Item4": return tup.Item4;
                        case "Item5": return tup.Item5;
                        case "Item6": return tup.Item6;
                        case "Item7": return tup.Item7;
                        case "Item8": return tup.Item8;
                    }
                    // Named element access: (int i, string s) t → t.i
                    if (tup.Names is { Count: > 0 } names)
                    {
                        for (var ni = 0; ni < names.Count; ni++)
                        {
                            if (string.Equals(names[ni], node.PropertyName, StringComparison.OrdinalIgnoreCase))
                                return ni < tup.Items.Length ? tup.Items[ni] : null;
                        }
                    }
                    RuntimeError($"Property '{node.PropertyName}' not found on tuple.", node.Line, node.Column);
                    break;
                }
        }

        // AN ARRAY ANSWERS ITS COLLECTION MEMBERS WITHOUT REFLECTION. `Count` (and `IndexOf`) are what an array
        // implements through ICollection<T>/IList<T> EXPLICITLY, so they were reached by asking the interfaces,
        // and the trimmer removed those members: measured, `[1, 2, 3].Count` answered 3 on the JIT build and
        // "Property 'Count' not found on type int[]" on the AOT publish, while `Length` survived. A COMPILED call
        // is what the engine's own doctrine asks for here, and the roots route was measured and rejected: the five
        // collection interfaces cost 1,178,624 bytes of artifact (ICollection<>/IList<>/IReadOnlyList<> and the
        // non-generic pair), for two members.
        if (target is Array arrayValue)
        {
            switch (node.PropertyName)
            {
                case "Count":
                case "Length":
                    return arrayValue.Length;
                case "IsReadOnly":
                    return false;
            }
        }

        // A script dictionary's Keys/Values are PROPERTIES as well as methods, the same data, and the same kind
        // of member `Count` has always been (a data accessor, not an operation), while the method forms stay for
        // the array they return. Measured before: `d.Keys` was refused ("Property 'Keys' not found on type
        // Dictionary") and, on the method's array, `d.Keys().Count` failed ("Property 'Count' not found on type
        // object[]"), so both spellings an author reaches for failed while `d.Count` worked. The dictionary's
        // own accessors win over a KEY of that name, exactly as the CLR `Count` already did.
        if (target is IScriptDictionary scriptDict)
        {
            if (node.PropertyName.Equals("Keys", Comparison)) return DictionaryProjection(scriptDict, keys: true);
            if (node.PropertyName.Equals("Values", Comparison)) return DictionaryProjection(scriptDict, keys: false);
        }

        if (target is IDictionary rawDict)
        {
            if (node.PropertyName.Equals("Keys", Comparison)) return DictionaryProjectionOfRaw(rawDict, keys: true);
            if (node.PropertyName.Equals("Values", Comparison)) return DictionaryProjectionOfRaw(rawDict, keys: false);
        }

        var cacheKey = (target.GetType(), node.PropertyName);
        if (PropertyCache.TryGetValue(cacheKey, out var propInfo))
        {
            try { return propInfo.GetValue(target); }
            catch (TargetInvocationException tie) { throw tie.InnerException ?? tie; }
        }
        // Case-sensitive CLR property lookup first. An INDEXER is not a property a script can read by NAME:
        // `"ab".Chars` needs an index, and reading one anyway surfaced a raw CLR TargetParameterCountException,
        // which no script can act on. Skipped, so the friendly "not found" (or an extension property of that
        // name) answers instead, and the same rule lets a block extend such a name.
        propInfo = target.GetType().GetProperty(node.PropertyName, BindingFlags.Public | BindingFlags.Instance);
        if (propInfo != null && propInfo.GetIndexParameters().Length > 0) propInfo = null;

        // Fallback: dictionary key lookup for IScriptDictionary (BEFORE case-insensitive match)
        if (propInfo == null && target is IScriptDictionary dict && dict.ContainsKey(node.PropertyName))
            return dict[node.PropertyName];

        // Case-insensitive CLR property lookup as fallback
        if (propInfo == null)
        {
            propInfo = target.GetType().GetProperty(node.PropertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (propInfo != null && propInfo.GetIndexParameters().Length > 0) propInfo = null;
        }

        // Fallback: IDynamicPropertyProvider for COM objects, etc.
        // Fallback: if GetProperty fails, try GetField (e.g .NET struct fields)
        if (propInfo == null)
        {
            var fieldInfo = target.GetType().GetField(node.PropertyName, BindingFlags.Public | BindingFlags.Instance);
            if (fieldInfo != null)
                return fieldInfo.GetValue(target);
        }

        // Fallback: an EXPLICIT INTERFACE IMPLEMENTATION is not a public property of the type. Measured: the
        // enumerator a script takes by hand implements IAsyncEnumerator<T>.Current EXPLICITLY, so `en.Current`
        // answered "Property 'Current' not found on type <IterateFileLinesAsync>d__126" while the ENGINE's own
        // await foreach reads it by casting to the interface, which a script cannot name for itself. The same
        // step FindBestMethod takes for a method, and the interface's OWN PropertyInfo reads the explicit slot.
        if (propInfo == null)
        {
            var ifaces = target.GetType().GetInterfaces();
            foreach (var iface in ifaces)
                if (InterfaceProperty(iface, node.PropertyName, false) is { } exact)
                {
                    propInfo = exact;
                    break;
                }
            if (propInfo == null)
                foreach (var iface in ifaces)
                    if (InterfaceProperty(iface, node.PropertyName, true) is { } loose)
                    {
                        propInfo = loose;
                        break;
                    }
        }

        if (propInfo == null && target is IDynamicPropertyProvider dpp && dpp.TryGetProperty(node.PropertyName, out var dppVal))
            return dppVal;

        // An extension property is the LAST resort, exactly as an extension METHOD is on the call path: every real
        // member (property, field, dictionary key, dynamic provider) has already had its chance above.
        if (propInfo == null
            && TryGetExtensionProperty(target, node.PropertyName, node.Line, node.Column, out var extensionValue))
            return extensionValue;

        if (propInfo == null)
            RuntimeError($"Property '{node.PropertyName}' not found on type {ReportedTypeName(target)}.", node.Line, node.Column);
        PropertyCache[cacheKey] = propInfo;
        try
        {
            return propInfo.GetValue(target);
        }
        catch (TargetInvocationException tie)
        {
            throw tie.InnerException ?? tie;
        }
    }

    /// <summary>THE CS1612 RULE, and the reason it is a REFUSAL rather than a copy: a `List` INDEXER IS NOT A
    /// VARIABLE. C# refuses `list[0].X = 42` when the element is a STRUCT ("cannot modify the return value ... because
    /// it is not a variable"), because the indexer hands back a copy and the write would be discarded; it ALLOWS the
    /// same line for a CLASS element (the indexer returns the reference, so the real object changes) and for an ARRAY
    /// element (an array access IS a variable, so a struct element may be modified in place). Only the TREE can tell
    /// which container this is, which is why the compiler declines the member-write-on-an-indexer shape and this
    /// method applies the rule for both engines.
    /// A container reached through anything but a plain VARIABLE is left alone (its expression would be evaluated
    /// twice to inspect it), and so is an index that is not a value we can read without effects.</summary>
    /// <summary>CS1612's rule stated ONCE, from VALUES: a StructInstance reached through a LIST indexer is
    /// refused (the indexer hands back a copy, so the change would be discarded) while an ARRAY element and a
    /// class element are legal. The tree's AST-side check resolves the container, the index and the element
    /// itself and then asks THIS; a compiled body's `__indexed_target__` helper asks it with the values it
    /// already holds, so the two engines cannot disagree about which writes are legal, only about how they
    /// found the values.</summary>
    private void RefuseStructElementWriteFromValues(object? container, object? element, object? index, int line, int column)
    {
        if (container is not IScriptList) return;      // an ARRAY element is a variable: allowed
        if (element is not StructInstance) return;     // a class element, or anything else: allowed
        RuntimeError($"Cannot modify the return value of a list element because it is not a variable: a list's "
            + "indexer hands back a copy, so the change would be discarded. Take the element into a variable, modify "
            + $"it, and store it back with `list[{index}] = item`.", line, column);
    }

    /// <summary>The tree's side of that rule: it resolves the shape the rule can be asked about (a plain
    /// VARIABLE container, a single literal or variable index, in range) and hands the values to
    /// <see cref="RefuseStructElementWriteFromValues"/>. A shape it does not resolve is not checked at all,
    /// which the compiler mirrors by declining it rather than compiling it.</summary>
    private void RefuseStructElementWriteThroughIndexer(PropertyAssignExpr node)
    {
        if (node.Target is not ArrayAccessExpr { Indices.Count: 1 } elem) return;
        // The shape is inspected BEFORE the assignment evaluates it, so ONLY an effect-free shape may be resolved:
        // a call on the way to the container or the index would run twice. That is why this is a shape test rather
        // than a "read the value" one, and why `F()[0].X = 1` stays unchecked.
        if (!IsEffectFreeIndexShape(elem.Array) || !IsEffectFreeIndexShape(elem.Indices[0])) return;
        object? container;
        object? indexValue;
        try { container = Evaluate(elem.Array); indexValue = Evaluate(elem.Indices[0]); }
        catch { return; }                                 // let the normal path report it, at the assignment's position
        if (container is not IScriptList list) return;    // an ARRAY element is a variable: allowed
        if (indexValue is null || !int.TryParse(indexValue.ToString(), out var index)) return;
        if (index < 0 || index >= list.Count) return;     // let the normal path report the range
        RefuseStructElementWriteFromValues(list, list[index], index, node.Line, node.Column);
    }

    /// <summary>True when an index target, its CONTAINER or its INDEX can be read without a side effect, which is
    /// what makes it safe to look at the shape before the assignment is evaluated. A call, a member access, or an
    /// index of an impure expression is not, and those shapes are left to the normal path. SHARED with the compiler,
    /// which must decline exactly the shapes this resolves so both engines agree about which writes are legal.</summary>
    internal static bool IsEffectFreeIndexShape(Expr e) => e switch
    {
        NumberExpr or StringExpr or CharExpr or BoolExpr or NullExpr or VariableExpr => true,
        BinaryExpr b => IsEffectFreeIndexShape(b.Left) && IsEffectFreeIndexShape(b.Right),
        UnaryExpr u => IsEffectFreeIndexShape(u.Right),
        ArrayAccessExpr a => IsEffectFreeIndexShape(a.Array) && a.Indices.All(IsEffectFreeIndexShape),
        _ => false,
    };

    public object? VisitPropertyAssignExpr(PropertyAssignExpr node)
    {
        if (node.Target is ReturnValueExpr)
            RuntimeError("Cannot assign to 'return.Value', it is read-only.", node.Line, node.Column);

        RefuseStructElementWriteThroughIndexer(node);
        var target = Evaluate(node.Target);
        if (ReferenceEquals(target, YieldSentinel))
            return YieldSentinel;

        if (target == null) RuntimeError($"Cannot assign property '{node.PropertyName}' on null.", node.Line, node.Column);
        var value = Evaluate(node.Value);
        if (ReferenceEquals(value, YieldSentinel))
            return YieldSentinel;

        switch (target)
        {
            case Type type:
                {
                    var prop = type.GetProperty(node.PropertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase);
                    if (prop != null && prop.CanWrite) { var converted = CoerceTo(value, MapTypeInfo(prop.PropertyType), node.Line, node.Column); prop.SetValue(null, converted); return converted; }
                    // A STATIC extension property's setter, after the real static member above.
                    if (prop == null
                        && TrySetExtensionProperty(type, node.PropertyName, value, node.Line, node.Column, out var staticSet))
                        return staticSet;
                    RuntimeError($"Static property '{node.PropertyName}' not found or not writable on type '{type.Name}'.", node.Line, node.Column);
                    break;
                }

            case TypeInfo { TypeName: not null } typeInfo:
                {
                    // Struct static field write
                    if (StructStaticFields.TryGetValue(typeInfo.TypeName, out var ssf) && ssf.ContainsKey(node.PropertyName))
                    {
                        ssf[node.PropertyName] = value;
                        return value;
                    }
                    // A STATIC extension property's setter, after the struct's own static field.
                    if (TrySetExtensionProperty(typeInfo, node.PropertyName, value, node.Line, node.Column, out var extStaticWrite))
                        return extStaticWrite;
                    RuntimeError($"Static field '{node.PropertyName}' not found in struct '{typeInfo.TypeName}'.", node.Line, node.Column);
                    break;
                }

            case StructInstance si:
                {
                    if (!Structs.TryGetValue(si.StructName, out var structDecl)) RuntimeError($"Struct '{si.StructName}' not defined.", node.Line, node.Column);
                    // Readonly struct, reject mutation outside initialization
                    if (structDecl.IsReadOnly && !si.IsInInitPhase)
                        RuntimeError($"Struct '{si.StructName}' is readonly and cannot be mutated after initialization.", node.Line, node.Column, ErrorCode.AccessViolation);
                    // Property backing field resolution
                    var stargetName = node.PropertyName;
                    var sprop = FindByName(structDecl.Properties, node.PropertyName, static p => p.Name);
                    if (sprop != null)
                    {
                        stargetName = BackingFieldName(sprop.Name);
                        if (sprop.Setter == null)
                            RuntimeError($"Property '{node.PropertyName}' has no setter.", node.Line, node.Column, ErrorCode.AccessViolation);
                        if (sprop.Setter is { IsInit: true } && !si.IsInInitPhase)
                            RuntimeError($"Property '{node.PropertyName}' is init-only and cannot be assigned outside initialization.", node.Line, node.Column, ErrorCode.AccessViolation);
                        if (sprop.Setter != null)
                            CheckStructMemberAccess(sprop.Setter.Access, si.StructName, node.Line, node.Column, node.PropertyName);
                    }
                    else if (FindByName(structDecl.Fields, stargetName, static f => f.Name) is { } sfd)
                    {
                        CheckStructMemberAccess(sfd.Access, si.StructName, node.Line, node.Column, node.PropertyName);
                    }

                    if (!si.Fields.ContainsKey(stargetName))
                    {
                        // A script struct's own field or property wins; an extension property is the last resort.
                        if (TrySetExtensionProperty(si, node.PropertyName, value, node.Line, node.Column, out var siSet))
                            return siSet;
                        RuntimeError($"'{node.PropertyName}' is not a valid field or property of struct '{si.StructName}'.", node.Line, node.Column);
                    }
                    var stargetType = sprop?.Type ?? FindByName(structDecl?.Fields, stargetName, static f => f.Name)?.Type
                        ?? RuntimeError<TypeInfo>($"Field type for '{node.PropertyName}' not found.", node.Line, node.Column);
                    var scoerced = CoerceTo(value, stargetType, node.Line, node.Column);
                    si.Fields[stargetName] = scoerced;
                    return scoerced;
                }

            case ClassInstance instance:
                {
                    // Property backing field resolution
                    var cbacking = FindPropertyBackingField(instance.Class, node.PropertyName);
                    if (cbacking != null)
                    {
                        var (cpropDeclClass, cpropDecl) = FindPropertyDeclWithClass(instance.Class, node.PropertyName);
                        if (cpropDecl == null) RuntimeError($"Property '{node.PropertyName}' not found.", node.Line, node.Column);
                        if (cpropDecl.Setter != null)
                            CheckAccess(cpropDecl.Setter.Access, cpropDeclClass, node.Line, node.Column, node.PropertyName);
                        else
                            RuntimeError($"Property '{node.PropertyName}' has no setter.", node.Line, node.Column, ErrorCode.AccessViolation);
                        if (cpropDecl.Setter is { IsInit: true } && !instance.IsInInitPhase)
                            RuntimeError($"Property '{node.PropertyName}' is init-only and cannot be assigned outside initialization.", node.Line, node.Column, ErrorCode.AccessViolation);
                        var converted = CoerceTo(value, cpropDecl.Type, node.Line, node.Column);
                        instance.FieldValues[cbacking] = converted;
                        return converted;
                    }

                    var (declaringClass, field) = FindFieldInHierarchy(instance.Class, node.PropertyName);
                    if (field != null) { CheckAccess(field.Access, declaringClass, node.Line, node.Column, node.PropertyName); var converted = CoerceTo(value, field.Type, node.Line, node.Column); instance.FieldValues[field.Name] = converted; return converted; }
                    // Fallback: static field write through instance (e.g. f2.Created = 99 → Class.StaticFields["Created"] = 99)
                    {
                        var cur = instance.Class; while (cur != null)
                        {
                            if (cur.StaticFields?.ContainsKey(node.PropertyName) == true)
                            {
                                var fieldDecl = FindByName(cur.StaticFieldDecls, node.PropertyName, static f => f.Name);
                                if (fieldDecl != null)
                                {
                                    CheckAccess(fieldDecl.Access, cur, node.Line, node.Column, node.PropertyName);
                                    cur.StaticFields[node.PropertyName] = CoerceTo(value, fieldDecl.Type, node.Line, node.Column);
                                    return value;
                                }
                                cur.StaticFields[node.PropertyName] = value;
                                return value;
                            }
                            cur = cur.Parent;
                        }
                    }
                    if (TrySetExtensionProperty(instance, node.PropertyName, value, node.Line, node.Column, out var ciSet)) return ciSet;
                    RuntimeError($"Field '{node.PropertyName}' not found in class '{instance.Class.Name}'.", node.Line, node.Column); break;
                }

            case ClassInfo classInfo:
                {
                    // Static property backing field resolution
                    var sback = FindPropertyBackingField(classInfo, node.PropertyName);
                    if (sback != null && classInfo.StaticFields != null && classInfo.StaticFields.ContainsKey(sback))
                    {
                        var (spropDeclClass, spropDecl) = FindPropertyDeclWithClass(classInfo, node.PropertyName);
                        if (spropDecl == null) RuntimeError($"Property '{node.PropertyName}' not found.", node.Line, node.Column);
                        if (spropDecl.Setter != null) CheckAccess(spropDecl.Setter.Access, spropDeclClass, node.Line, node.Column, node.PropertyName);
                        var sconverted = CoerceTo(value, spropDecl.Type, node.Line, node.Column);
                        classInfo.StaticFields[sback] = sconverted;
                        return sconverted;
                    }
                    if (classInfo.StaticFields != null && classInfo.StaticFields.ContainsKey(node.PropertyName))
                    {
                        classInfo.StaticFields[node.PropertyName] = value;
                        return value;
                    }
                    // Walk parent chain for inherited static fields
                    var cur = classInfo.Parent;
                    while (cur != null)
                    {
                        if (cur.StaticFields?.ContainsKey(node.PropertyName) == true)
                        {
                            cur.StaticFields[node.PropertyName] = value;
                            return value;
                        }
                        cur = cur.Parent;
                    }
                    if (TrySetExtensionProperty(classInfo, node.PropertyName, value, node.Line, node.Column, out var clsSet)) return clsSet;
                    RuntimeError($"Static field '{node.PropertyName}' not found in class '{classInfo.Name}'.", node.Line, node.Column);
                    break;
                }

            case Namespace ns:
                {
                    // What the namespace OWNS decides first: a member registered with a setter (Application.CurDir)
                    // runs it, and a DYNAMIC property with no setter is REFUSED, because storing the value would be
                    // invisible (its getter answers on every read: measured, `Script.IsRepl = true` answered nothing
                    // and changed nothing). A name nothing registered still stores, which is how a namespace body's
                    // own members and a script-invented name stay writable.
                    Namespace.SetOutcome outcome;
                    try
                    {
                        outcome = ns.TrySetProperty(node.PropertyName, value);
                    }
                    catch (ScriptException ex) when (ex.Line == 0 && AbortExitCode(ex) is null)
                    {
                        // AN ABORT IS NOT A REFUSAL and must keep its exit code: a setter may END the run (`exit()`,
                        // and `Application.SingleInstance` on a second instance), and rebuilding that into a
                        // positioned error turned "this run stops quietly with code 0" into code 1 (measured: the
                        // second instance exited 1 before this clause). AbortExitCode is the one decoder for it.
                        //
                        // A setter is a delegate with no statement of its own, so its refusal carries no position:
                        // the ASSIGNMENT's position is attached here, through the same RuntimeError every other
                        // refusal goes through, so the message reads like its neighbours (and its text is the
                        // engine's own, never a CLR message).
                        RuntimeError(ex.Message, node.Line, node.Column, ex.ErrorCode);
                        throw;   // unreachable: RuntimeError throws
                    }
                    switch (outcome)
                    {
                        case Namespace.SetOutcome.Set:
                            return value;
                        case Namespace.SetOutcome.ReadOnly:
                            RuntimeError($"Cannot assign to '{node.PropertyName}' in namespace "
                                + $"'{ns.Name}': it is read only.", node.Line, node.Column, ErrorCode.AccessViolation);
                            break;
                    }
                    if (value is ScriptFunction sf)
                    {
                        // Wrap ScriptFunction in a native-compatible delegate that invokes it
                        var sfCapture = sf;
                        ns.AddMethod(node.PropertyName, args => InvokeScriptFunction(sfCapture, args, node.Line, node.Column));
                    }
                    else
                    {
                        ns.AddProperty(node.PropertyName, value);
                    }
                    return value;
                }

            case ScriptTuple tup:
                {
                    // Named element write: (int i, int x) p → p.i = v, mirrors the read path's
                    // Item1..Item8 / named-element lookup (tuples are mutable containers).
                    switch (node.PropertyName)
                    {
                        case "Item1": tup.Items[0] = value; return value;
                        case "Item2": tup.Items[1] = value; return value;
                        case "Item3": tup.Items[2] = value; return value;
                        case "Item4": tup.Items[3] = value; return value;
                        case "Item5": tup.Items[4] = value; return value;
                        case "Item6": tup.Items[5] = value; return value;
                        case "Item7": tup.Items[6] = value; return value;
                        case "Item8": tup.Items[7] = value; return value;
                    }
                    if (tup.Names is { Count: > 0 } names)
                    {
                        for (var ni = 0; ni < names.Count; ni++)
                        {
                            if (string.Equals(names[ni], node.PropertyName, StringComparison.OrdinalIgnoreCase))
                            {
                                tup.Items[ni] = value;
                                return value;
                            }
                        }
                    }
                    RuntimeError($"Property '{node.PropertyName}' not found on tuple.", node.Line, node.Column);
                    break;
                }
        }

        // Fallback: IDynamicPropertyProvider (COM objects, etc.)
        if (target is IDynamicPropertyProvider dpp && dpp.TrySetProperty(node.PropertyName, value))
            return value;

        var cacheKey = (target.GetType(), node.PropertyName);
        if (!PropertyCache.TryGetValue(cacheKey, out var propInfo))
            propInfo = target.GetType().GetProperty(node.PropertyName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.IgnoreCase);
        // An indexer cannot be written by name either, for the same reason the read path skips one.
        if (propInfo != null && propInfo.GetIndexParameters().Length > 0) propInfo = null;
        // An extension property's SETTER is the last resort here too, and it raises its own message when the
        // property is declared without one, so a miss only reaches the CLR's "not found" when no block declares it.
        if (propInfo == null
            && TrySetExtensionProperty(target, node.PropertyName, value, node.Line, node.Column, out var setByExtension))
            return setByExtension;
        if (propInfo == null)
            RuntimeError($"Property '{node.PropertyName}' not found on type {ReportedTypeName(target)}.", node.Line, node.Column);
        PropertyCache[cacheKey] = propInfo;

        var convertedValue = value;
        if (propInfo.PropertyType != typeof(object) && value != null)
        {
            var targetTypeInfo = MapTypeInfo(propInfo.PropertyType);
            convertedValue = CoerceTo(value, targetTypeInfo, node.Line, node.Column);
        }

        try
        {
            propInfo.SetValue(target, convertedValue);
        }
        catch (Exception ex)
        {
            // A property setter is invoked by reflection, so the engine's OWN refusal arrives wrapped in a
            // TargetInvocationException: unwrapping it keeps the message the setter wrote (measured: `Failed to
            // set property 'Screen': TargetInvocationException: 'Exception has been thrown by the target of an
            // invocation.'` for a refusal whose text named the screens and their count). The position is still
            // THIS access, which is where the script wrote the assignment.
            var inner = ex is System.Reflection.TargetInvocationException tie ? tie.InnerException ?? tie : ex;
            if (inner is ScriptException refusal)
                RuntimeError(refusal.Message, node.Line, node.Column);
            RuntimeError($"Failed to set property '{node.PropertyName}': {FriendlyExceptionMessage(inner)}", node.Line, node.Column);
        }
        return convertedValue;
    }

    public object? VisitMethodCallExpr(MethodCallExpr node)
    {
        // A QUALIFIED call names the contract: `IShape.area(value, ...)`. The receiver here is the interface
        // name itself, and the value to call the member on arrives as the first argument.
        if (node.Receiver is VariableExpr qualifiedReceiver && IsDeclaredInterfaceName(qualifiedReceiver.Name))
        {
            return InvokeQualifiedInterfaceMember(qualifiedReceiver.Name, node.MethodName,
                node.Arguments, node.Line, node.Column);
        }
        var receiver = Evaluate(node.Receiver);
        if (ReferenceEquals(receiver, YieldSentinel)) return YieldSentinel;
        if (receiver == null) RuntimeError($"Cannot call method '{node.MethodName}' on null.", node.Line, node.Column);

        // A named tuple element call (`t.c()`) is handled by the dispatch core, which is the ONE place both
        // engines reach; the copy that used to sit here only served the tree, so a compiled body refused it.

        bool hasRefArgs = false, hasOutArgs = false;
        if (node.IsRefArgs.Any(t => !t))
        {
            hasRefArgs = true;
        }

        if (!hasRefArgs)
        {
            if (node.IsOutArgs.Any(t => !t))
            {
                hasOutArgs = true;
            }
        }

        if (!hasRefArgs && !hasOutArgs)
        {
            var count = node.Arguments.Count;
            if (count == 0) return InvokeMethodDispatch(receiver, node.MethodName, [], node);
            var exactArgs = ArrayPool.Rent(count);
            try
            {
                for (var i = 0; i < count; i++)
                {
                    var a = Evaluate(node.Arguments[i]);
                    if (ReferenceEquals(a, YieldSentinel))
                        return YieldSentinel; exactArgs[i] = a;
                }
                return InvokeMethodDispatch(receiver, node.MethodName, exactArgs, node);
            }
            finally { ArrayPool.Return(exactArgs); }
        }

        var argCount = node.Arguments.Count;
        var exactArgsRef = ArrayPool.Rent(argCount);
        try
        {
            for (var i = 0; i < argCount; i++)
            {
                var isOutArg = node.IsOutArgs != null && i < node.IsOutArgs.Count && node.IsOutArgs[i];
                if (node.IsRefArgs[i] || isOutArg)
                {
                    var expr = node.Arguments[i];
                    if (expr is DiscardExpr)
                    {
                        exactArgsRef[i] = NullRefCell;
                    }
                    else
                    {
                        switch (expr)
                        {
                            case VariableExpr varExpr:
                                // CAPTURED, never evaluated first. Evaluating it is what broke an inline `out var e`
                                // declaration on a method call: the name does not exist yet, the capture is what
                                // CREATES it (with allowUndefined), and an evaluation refused it before the capture
                                // could. The function path already ordered it this way; the two now agree.
                                exactArgsRef[i] = CaptureVariableReference(varExpr, node.Line, node.Column,
                                    allowUndefined: isOutArg, byOut: isOutArg);
                                break;

                            // A field, a property or an element is a reference target here too, through the SAME
                            // resolver the function path uses, so the two call shapes cannot disagree.
                            case PropertyAccessExpr:
                            case ArrayAccessExpr:
                                exactArgsRef[i] = RefCellForTarget(expr, isOutArg, node.Line, node.Column);
                                break;

                            default:
                                // An expression that EVALUATES to a cell (e.g. a call returning a reference) keeps
                                // its previous handling.
                                var evaluated = Evaluate(expr);
                                if (ReferenceEquals(evaluated, YieldSentinel)) return YieldSentinel;
                                if (evaluated is RefCell rc) exactArgsRef[i] = rc;
                                else RuntimeError("Only a variable, a field or an element can be passed by ref/out: a literal, a call and an operator result have no storage to write back to.", node.Line, node.Column);
                                break;
                        }
                    }
                }
                else
                {
                    var a = Evaluate(node.Arguments[i]);
                    if (ReferenceEquals(a, YieldSentinel)) return YieldSentinel;
                    exactArgsRef[i] = a;
                }
            }
            return InvokeMethodDispatch(receiver, node.MethodName, exactArgsRef, node);
        }
        finally { ArrayPool.Return(exactArgsRef); }
    }

    private object? InvokeMethodDispatch(object? receiver, string methodName, object?[] args, MethodCallExpr node)
    {
        try
        {
            return InvokeMethodDispatchCore(receiver, methodName, args, node);
        }
        catch (ScriptException ex)
        {
            // A registration reports with a message and no location; this is where the call site is known.
            throw WithLocation(ex, node.Line, node.Column);
        }
        catch (Exception ex)
        {
            // The refusal the VM's member paths already raise, from the ONE helper, so the engines cannot
            // drift: a CLR failure from a member that is not a CLR member (a dynamic provider, a COM node)
            // used to leave this dispatch unconverted and be reported at the ENCLOSING function's boundary,
            // with neither the member's name nor the call's position. Measured on one failing COM call: the
            // tree said "the COM object raised an error" at the Com.Run line while the VM said
            // "[Remove]: the COM object raised an error" at the call site.
            throw ClrMemberFailure(ex, node.Line, node.Column, methodName);
        }
    }

    private object? InvokeMethodDispatchCore(object? receiver, string methodName, object?[] args, MethodCallExpr node)
    {
        // Script-declared extension members of a TYPE receiver: a static extension member is reached
        // through the extended type itself, either as a type reference (List<int>.Create()) or as the
        // type keyword (string.Awake()). One lookup serves both, before the receiver switch.
        if (receiver is TypeInfo typeRef)
        {
            if (TryInvokeStaticExtensionOnTypeRef(typeRef, methodName, args, node.Line, node.Column, out var typeRefResult))
                return typeRefResult;
            // A SCRIPT-DECLARED type's own static members must get their chance before the loud extension
            // message. A struct name arrives here as a TypeInfo, so throwing unconditionally meant every static
            // CALL on a struct failed with "No static extension member 'Add' for 'Calc'" while a static field
            // READ still worked - a read is a property access and never came through this path. Anything else (a
            // CLR or generic target with no script declaration) still reports the extension message, which is
            // what crash_extension_members pins.
            var declaredHere = typeRef.TypeName != null
                               && (Structs.ContainsKey(typeRef.TypeName) || Classes.ContainsKey(typeRef.TypeName));
            if (!declaredHere)
                throw new ScriptException(NoStaticExtensionMessage(TypeLabel(typeRef), methodName));
        }
        if (receiver is Type clrType
            && TryInvokeStaticExtensionOnType(clrType, methodName, args, node.Line, node.Column, out var typeResult))
            return typeResult;
        switch (receiver)
        {
            // A NAMED TUPLE ELEMENT that holds a function is CALLABLE by its element name: `(int c, int d) t =
            // (action, action); t.c();`. This arm is the ONE implementation: `VisitMethodCallExpr` used to carry
            // its own copy of it, and the VM's `InvokeMethodValues` reaches only this switch, so a compiled body
            // refused the call ("Method 'c' not found on type Tuple with 0 argument(s)") while the tree answered
            // it. Measured through one probe run twice, `tools/probe-toplevel-vm.slc` section 2.
            case ScriptTuple { Names: { Count: > 0 } tupNames } tupCall:
                {
                    for (var ti = 0; ti < tupNames.Count; ti++)
                    {
                        if (!string.Equals(tupNames[ti], methodName, StringComparison.OrdinalIgnoreCase)) continue;
                        var elem = ti < tupCall.Items.Length ? tupCall.Items[ti] : null;
                        if (elem is ScriptFunction elemFn)
                            return InvokeScriptFunction(elemFn, args, node.Line, node.Column);
                        if (elem is Delegate elemDel)
                            return elemDel.DynamicInvoke(args);
                        RuntimeError($"Tuple element '{methodName}' is not callable.", node.Line, node.Column);
                    }
                    RuntimeError($"Tuple has no element named '{methodName}'.", node.Line, node.Column);
                    break;
                }

            case Namespace ns:
                if (node.TypeArgs is { Count: > 0 })
                {
                    if (ns.HasTypedMethod(methodName))
                        return ns.InvokeTypedMethod(methodName, args, node.TypeArgs, node.Line, node.Column);
                }

                // The namespace's OWN resolution decides, including its refusal: asking it rather than gating on
                // HasMethod is what lets a CURATED namespace answer a member it never registered through the CLR
                // type behind it (`File.ReadAllTextAsync`), and the refusal still names what the namespace holds.
                // The IDisposable rule is the one name that is NOT the namespace's (see below), so it is tried
                // first: a namespace this interpreter OWNS is disposable, and for one with nothing to release the
                // call is a NO-OP, which is what being IDisposable means and what the caller asks for by that name.
                if (methodName.Equals("Dispose", StringComparison.Ordinal) && args.Length == 0 && ns is IDisposable disposableNs)
                {
                    disposableNs.Dispose();
                    return null;
                }

                return ns.InvokeMethod(methodName, args, node.Line, node.Column);

            // A struct's bound method arrives in the same record a class's does, so the guard is where the two part: the
            // arm below casts to ClassInstance, which a StructInstance is not.
            case BoundMethod bound when bound.Instance is StructInstance structBound:
                return InvokeMethodOnStructInstance(structBound, bound.Method,
                    ReorderNamedArgs(args, node.ArgNames, bound.Method.Params, node.Line, node.Column), node.Line, node.Column);

            case BoundMethod bound:
                return InvokeMethodOnInstance((ClassInstance)bound.Instance, bound.Method, ReorderNamedArgs(args, node.ArgNames, bound.Method.Params, node.Line, node.Column), node.Line, node.Column);

            // Struct static method dispatch, TypeInfo receiver
            case TypeInfo { TypeName: not null } ti when Structs.TryGetValue(ti.TypeName, out var stDecl):
                {
                    var staticMethod = FindByName(stDecl.Methods, methodName, static m => m.Name, static m => m.IsStatic);
                    if (staticMethod != null)
                    {
                        // Same rule as the class arm below: a static method is not a closure over its caller, so it
                        // is called through the canonical empty snapshot and its globals resolve live.
                        var staticParamTypes = GetParameterTypes(staticMethod.Params);
                        var sf = new ScriptFunction(staticMethod, SEmptySnapshot, staticMethod.ReturnType, staticParamTypes, staticMethod.IsRefParams, IsOutParams: staticMethod.IsOutParams);
                        return InvokeScriptFunction(sf, args, node.Line, node.Column);
                    }
                    // Also check static fields (lambda fields stored as ScriptFunction)
                    if (StructStaticFields.TryGetValue(ti.TypeName, out var ssf) && ssf.TryGetValue(methodName, out var sfVal))
                    {
                        if (sfVal is ScriptFunction sLambda)
                            return InvokeScriptFunction(sLambda, args, node.Line, node.Column);
                        if (sfVal is Delegate del)
                            return del.DynamicInvoke(args);
                    }
                    RuntimeError($"Static method '{methodName}' not found in struct '{ti.TypeName}'.", node.Line, node.Column);
                    break;
                }

            // Static method dispatch, ClassInfo receiver
            case ClassInfo staticClass:
                {
                    var staticMethod = staticClass.StaticMethods?.FirstOrDefault(m =>
                        m.Name.Equals(methodName, Comparison));
                    if (staticMethod != null)
                    {
                        // A STATIC METHOD IS NOT A CLOSURE OVER ITS CALLER, so it is called through the canonical
                        // empty snapshot: no captured scopes, and the file's globals resolve LIVE at call time
                        // (InvokeScriptFunction refreshes them because LocalScopes is empty).
                        //
                        // This used to capture the caller's own frames (`CaptureClosureSnapshotWithGlobal` copies
                        // every scope between the top and the bottom), and that is not a cosmetic difference:
                        // InvokeScriptFunction refreshes the READABLE globals ONLY for a body with no local
                        // scopes, while the snapshot's own GlobalScope is empty BY CONTRACT, so a captured scope
                        // left the method unable to read ANY top-level variable. Measured with the arm's own
                        // counters, a static call from inside an `http.Use` middleware: `scopes=3 captured=1
                        // globals=0`, and the method reported `Undefined variable 'Sqlite'` (the htmx board's
                        // `Store.Insert`, called inside a `try`) while the identical call one statement UP, at the
                        // caller's body level, worked. A namespace read inside the method kept working, which is
                        // why this hid: `Path` resolves as the CLR type `System.IO.Path`, not through the scope.
                        var staticParamTypes = GetParameterTypes(staticMethod.Params);
                        var sf = new ScriptFunction(staticMethod, SEmptySnapshot, staticMethod.ReturnType, staticParamTypes, staticMethod.IsRefParams, IsOutParams: staticMethod.IsOutParams);
                        var prevClass = _currentClass;
                        _currentClass = staticClass;
                        try
                        {
                            return InvokeScriptFunction(sf, args, node.Line, node.Column);
                        }
                        finally
                        {
                            _currentClass = prevClass;
                        }
                    }

                    // Fallback: check static fields for callable values (e.g. lambda field)
                    if (staticClass.StaticFields?.TryGetValue(methodName, out var sfVal) == true && sfVal is ScriptFunction sfLambda)
                    {
                        return InvokeScriptFunction(sfLambda, args, node.Line, node.Column);
                    }
                    if (staticClass.StaticFields?.TryGetValue(methodName, out var sfVal2) == true && sfVal2 is Delegate del)
                    {
                        return del.DynamicInvoke(args);
                    }
                    RuntimeError($"Static method '{methodName}' not found in class '{staticClass.Name}'.", node.Line, node.Column);
                    break;
                }

            case BaseProxy baseProxy:
                {
                    var method = FindMethod(baseProxy.BaseClass, methodName)
                                 ?? RuntimeError<FuncDecl>($"Method '{methodName}' not found in base class '{baseProxy.BaseClass.Name}'.", node.Line, node.Column);
                    CheckAccess(method.Access, baseProxy.BaseClass, node.Line, node.Column);
                    return InvokeMethodOnInstance((ClassInstance)baseProxy.Instance, method, args, node.Line, node.Column);
                }

            case ClassInstance classInstance:
                {
                    // P2-T2: arg-type-signature overload resolution (cached). Replaces
                    // first-by-name lookup so multi-overload classes dispatch on runtime
                    // arg types; single-overload classes keep the P2-T1 fast path.
                    var methodResult = ResolveScriptMethod(classInstance.Class, methodName, args);
                    if (methodResult is (var methodDeclClass, { } methodFound))
                    {
                        CheckAccess(methodFound.Access, methodDeclClass, node.Line, node.Column);
                        var reordered = ReorderNamedArgs(args, node.ArgNames, methodFound.Params, node.Line, node.Column);
                        return InvokeMethodOnInstance(classInstance, methodFound, reordered, node.Line, node.Column, node.TypeArgs);
                    }

                    var evt = GetEventInfo(classInstance, methodName, node.Line, node.Column);
                    if (evt != null)
                    {
                        object? lastResult = null;
                        foreach (var subscriber in evt.Subscribers)
                        {
                            // A void subscriber must not contribute a return value: its VM body may
                            // leave a discarded-expression value on the operand stack (e.g. `counter++;`),
                            // which the event invocation would otherwise bubble up as "the event's result"
                            // and trip the CALLER's void check ("Void method 'X' cannot return a value").
                            var subResult = InvokeScriptFunction(subscriber, args, node.Line, node.Column);
                            if (subscriber.Declaration == null || subscriber.Declaration.ReturnType != TypeInfo.Void)
                                lastResult = subResult;
                        }
                        if (_eventWaiters.TryGetValue(evt, out var waiters))
                        {
                            var snapshot = waiters.ToList(); _eventWaiters.Remove(evt);
                            foreach (var waitingCoroutine in snapshot)
                            {
                                var wt = waitingCoroutine.Tree;
                                if (wt != null) wt.PendingResumeValue = null;
                                waitingCoroutine.Resume();
                            }
                        }
                        return lastResult;
                    }

                    var (declaringClass, field) = FindFieldInHierarchy(classInstance.Class, methodName);
                    if (field != null)
                    {
                        CheckAccess(field.Access, declaringClass, node.Line, node.Column);
                        if (classInstance.FieldValues.TryGetValue(field.Name, out var fieldValue))
                        {
                            switch (fieldValue)
                            {
                                case ScriptFunction fn: return InvokeScriptFunction(fn, args, node.Line, node.Column);
                                case ScriptDelegateWrapper wrapper: return wrapper.Invoke(args);
                                case Delegate del: return del.DynamicInvoke(args);
                            }
                        }
                    }

                    // Destructor via explicit Dispose() call, chains all destructors in hierarchy
                    if (methodName.Equals("Dispose", StringComparison.Ordinal))
                    {
                        var dtorInfos = FindAllDestructorClasses(classInstance.Class);
                        if (dtorInfos.Count > 0)
                        {
                            var prevThis = _currentThis;
                            var prevClass = _currentClass;
                            _currentThis = classInstance;
                            try
                            {
                                object? lastResult = null;
                                foreach (var (dtorOwner, dtor) in dtorInfos)
                                {
                                    _currentClass = dtorOwner;
                                    var snapshot = CreateSnapshotWithGlobalScope();
                                    var disposeFn = new ScriptFunction(dtor, snapshot, TypeInfo.Void, [], [], Flags: FunctionFlags.None);
                                    lastResult = InvokeScriptFunction(disposeFn, [classInstance], node.Line, node.Column);   // trailing arg = 'this' local
                                }
                                return lastResult;
                            }
                            finally { _currentThis = prevThis; _currentClass = prevClass; }
                        }
                    }

                    // A member an interface the class implements gives a DEFAULT body for answers before an
                    // extension does: the contract is nearer than a block, and every real member has declined.
                    if (TryInvokeInterfaceDefault(classInstance, methodName, args, node.Line, node.Column, out var defaultClassResult))
                        return defaultClassResult;

                    // A script-declared instance extension member is reached through the VALUE, so it
                    // answers here - after the class's own methods, events and fields, which win.
                    if (TryInvokeInstanceExtension(classInstance, methodName, args, node.Line, node.Column, out var extClassResult))
                        return extClassResult;
                    // An EXPLICIT implementation lives OUTSIDE the name table, so a bare call must say what to
                    // write instead: the contract's own spelling, with the receiver as the first argument.
                    // A DEFAULT body running in a contract composes through THAT contract first: `this.area()`
                    // inside `IShape.describe` means IShape's meaning of `area`, which the class may implement
                    // explicitly. Without this the default could not reach the member it was written against.
                    if (_currentContract != null)
                    {
                        var contracted = FindExplicitMember(classInstance, _currentContract, methodName, args.Length);
                        if (contracted != null)
                            return InvokeInterfaceBody(contracted, classInstance, args, node.Line, node.Column,
                                                       _currentContract);
                    }
                    var contractRecipe = ExplicitContractRecipe(classInstance, methodName, args.Length);
                    if (contractRecipe != null)
                        RuntimeError($"'{methodName}/{args.Length}' is an EXPLICIT implementation on class "
                                     + $"'{classInstance.Class.Name}', so it is not in the class name table. Name the "
                                     + $"contract you mean: {contractRecipe}.", node.Line, node.Column);                    // The reflection verbs are the LAST resort, after the class's own members (and after the
                    // explicit-implementation check above), so a class that declares Get/Set/Keys/Call keeps them.
                    if (TryReflectVerbs(classInstance, methodName, args, node.Line, node.Column, out var reflectedTree))
                        return reflectedTree;
                    RuntimeError($"Method or event '{methodName}' not found in class '{classInstance.Class.Name}'.", node.Line, node.Column);
                    break;
                }

            case StructInstance structInst:
                {
                    if (Structs.TryGetValue(structInst.StructName, out var structDecl))
                    {
                        var method = FindByName(structDecl.Methods, methodName, static m => m.Name);
                        if (method != null)
                        {
                            CheckStructMemberAccess(method.Access, structInst.StructName, node.Line, node.Column, methodName);
                            if (method.IsPure && _pureMemoCache.TryGetValue((method.Name, args), out var cached))
                                return Equals(cached, VoidMarker.Instance) ? null : cached;

                            // ONE implementation: this is InvokeMethodOnStructInstance, the same helper a struct
                            // OPERATOR call uses. The branch here was a second copy, and the two had drifted: the copy
                            // had no arity check (a missing argument left the parameter UNDEFINED, so the body reported
                            // "Undefined variable 'a'" instead of an arity refusal), no `params` tail (measured:
                            // "Expected an array, got Int32" for `s.Sum(1, 2, 3)`), no ref/out rule (a `ref` argument
                            // reached CoerceTo and leaked the internal type name), and the IsPure memo lived only there.
                            CheckScriptCallArity(method, args.Length, node.Line, node.Column);
                            return InvokeMethodOnStructInstance(structInst, method, args, node.Line, node.Column);
                        }
                    }
                    // An interface DEFAULT the struct MATCHES answers first: the contract is nearer than a
                    // block, the same order the class path uses.
                    if (TryInvokeInterfaceDefault(structInst, methodName, args, node.Line, node.Column, out var siDefault))
                        return siDefault;
                    // Then a struct value's instance extension member, once the struct's own members have not.
                    if (TryInvokeInstanceExtension(structInst, methodName, args, node.Line, node.Column, out var extStructResult))
                        return extStructResult;
                    if (_currentContract != null)
                    {
                        var contractedStruct = FindExplicitMember(structInst, _currentContract, methodName, args.Length);
                        if (contractedStruct != null)
                            return InvokeInterfaceBody(contractedStruct, structInst, args, node.Line, node.Column,
                                                       _currentContract);
                    }
                    var structRecipe = ExplicitContractRecipe(structInst, methodName, args.Length);
                    if (structRecipe != null)
                        RuntimeError($"'{methodName}/{args.Length}' is an EXPLICIT implementation on struct "
                                     + $"'{structInst.StructName}', so it is not in the struct name table. Name the "
                                     + $"contract you mean: {structRecipe}.", node.Line, node.Column); RuntimeError($"Method '{methodName}' not found in struct '{structInst.StructName}'.", node.Line, node.Column);
                    break;
                }
        }

        if (receiver != null)
        {
            var receiverType = receiver.GetType();
            // Task 4 LINQ adapter: any value satisfying the unified Iterable contract that is not
            // itself IEnumerable (tuples, dicts, HashSets, coroutine generators) is wrapped into an
            // IEnumerable<object?> so the LINQ extension methods resolve on it. Genuine non-iterables
            // (int, bool, …) are left untouched and fall through to the existing "not found" error.
            object? effectiveReceiver = receiver;
            var effectiveReceiverType = receiverType;
            if (!typeof(IEnumerable).IsAssignableFrom(receiverType) && IsIterable(receiver))
            {
                effectiveReceiver = WrapIterableForLinq(receiver);
                effectiveReceiverType = effectiveReceiver.GetType();
            }
            foreach (var container in ExtensionMethodContainers)
            {
                var candidates = container.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
                    .Where(m => m.Name.Equals(methodName, Comparison) && m.IsDefined(typeof(ExtensionAttribute), false));
                foreach (var method in candidates)
                {
                    var prms = method.GetParameters();
                    if (prms.Length != args.Length + 1 ||
                        !prms[0].ParameterType.IsAssignableFrom(effectiveReceiverType))
                        continue;

                    // Instance methods take priority over extension methods (C# spec §7.6.5.2)
                    if (receiverType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                        .Any(m => m.Name.Equals(methodName, Comparison)))
                        continue;

                    var allArgs = new object?[args.Length + 1]; allArgs[0] = effectiveReceiver;
                    Array.Copy(args, 0, allArgs, 1, args.Length);
                    try
                    {
                        return method.Invoke(null, allArgs);
                    }
                    catch (TargetInvocationException ex)
                    {
                        ThrowException($"[{methodName}]: {ex.InnerException?.Message ?? ex.Message}", node.Line, node.Column);
                        return null;
                    }
                }
            }
        }

        switch (receiver)
        {
            // DynamicLibrary, resolve function pointer and call it
            // Known built-in methods, handled without reflection (AOT-safe)
            case DynamicLibrary lib when methodName.Equals("Free", StringComparison.Ordinal):
                lib.Free();
                return null;

            case DynamicLibrary lib when methodName.Equals("Dispose", StringComparison.Ordinal):
                lib.Dispose();
                return null;

            case DynamicLibrary lib when methodName.Equals("ToString", StringComparison.Ordinal):
                return lib.ToString();

            case DynamicLibrary lib when methodName.Equals("GetFunctionPointer", StringComparison.Ordinal) && args.Length > 0:
                {
                    // Dispatch to the correct overload based on argument type
                    if (args[0] is string fnName) return lib.GetFunctionPointer(fnName).ToInt64();
                    var ord = Convert.ToInt32(args[0]);
                    return lib.GetFunctionPointer(ord).ToInt64();
                }

            // Function(name|ordinal) -> an INVOCABLE handle. Without this case the export-name
            // fallback below would look for an export literally called "Function".
            case DynamicLibrary lib when methodName.Equals("Function", StringComparison.Ordinal) && args.Length == 1:
                return lib.Function(args[0]);

            // Ordinal call: method name like "_42" resolves by ordinal
            // Usage: kernel._42(800, 200)  → calls ordinal 42 with arguments
            case DynamicLibrary lib when methodName.Length > 1 && methodName[0] == '_' && int.TryParse(methodName.AsSpan(1), out var ordinal):
                {
                    var ptr = lib.GetFunctionPointer(ordinal);
                    if (ptr == nint.Zero)
                        return RuntimeError<object?>($"Ordinal {ordinal} not found in library '{lib.Name}'.", node.Line, node.Column);

                    // One implementation for every native call, see NativeCall.
                    return NativeCall.Invoke(ptr, args);
                }

            // Everything else resolves as a function pointer by name, like user32.MessageBoxW(0, "Hello", "Title", 0).
            // DynamicLibrary.BuiltInMembers is the ONE list of real members that must not be treated as
            // exports; anything on it falls through to the CLR dispatch at the end of this method.
            case DynamicLibrary lib when !DynamicLibrary.BuiltInMembers.Contains(methodName):
                // One implementation for every native call, see NativeCall. All args as a single
                // array is still supported: lib.Func([arg1, arg2, ...]).
                return NativeCall.Invoke(lib.GetFunctionPointer(methodName), SpreadFirstArrayArg(args));

            case Array arr when methodName.Equals("Clear", StringComparison.Ordinal):
                {
                    if (args.Length != 0)
                        RuntimeError("Clear expects 0 arguments.", node.Line, node.Column);
                    Array.Clear(arr);
                    return null;
                }

            case Array arr when methodName.Equals("ToList", StringComparison.Ordinal):
                {
                    if (args.Length != 0)
                        RuntimeError("ToList expects 0 arguments.", node.Line, node.Column);
                    return IterableToList([arr]);
                }

            case Namespace:
            case IScriptList:
            case IScriptDictionary:
            case SuperProxy:
            case IScriptHashSet:
            case Coroutine:
                return InvokeMethod(receiver, methodName, args, node.Line, node.Column, node.TypeArgs, node.ArgNames);

            // Fallback: IDynamicPropertyProvider (COM objects, etc.). The names the caller wrote travel with the
            // call: dropping them silently re-points a named value at the next POSITION (see TryInvokeNamedMethod).
            case IDynamicPropertyProvider dyn when dyn.TryInvokeNamedMethod(methodName, args, node.ArgNames, out var dynResult):
                return dynResult;
        }

        // ScriptResult typed accessors: result.As<T>() / result.Return<T>(), resolve the
        // script type argument through CoerceTo (no generic reflection needed). As<T>
        // swallows a mismatch and yields null; Return<T> lets CoerceTo's error propagate
        // (the native-method error pattern).
        if (receiver is ScriptResult sr && node.TypeArgs is { Count: > 0 })
        {
            if (methodName.Equals("As", StringComparison.Ordinal))
            {
                try { return CoerceTo(sr.ReturnValue, node.TypeArgs[0], node.Line, node.Column); }
                catch
                {
                    // C# default(T) semantics, derive it from the declared script
                    // type via TypeSystem, no duplicated type-name lists: numeric ->
                    // 0, bool -> false, nullable/reference/other -> null.
                    if (node.TypeArgs[0].IsNullable) return null;
                    if (TypeSystem.IsNumeric(node.TypeArgs[0].BaseType)) return 0;
                    if (node.TypeArgs[0].BaseType == TokenType.Bool) return false;
                    return null;
                }
            }
            if (methodName.Equals("Return", StringComparison.Ordinal))
                return CoerceTo(sr.ReturnValue, node.TypeArgs[0], node.Line, node.Column);
        }

        // Uniform "virtual dispose": Dispose() is callable on every value. IDisposable receivers
        // handled by the reflection path below (real Dispose); script ClassInstance has its own
        // destructor chain above; anything else (string, int, Dictionary, ...) is a safe no-op.
        if (methodName.Equals("Dispose", StringComparison.Ordinal)
            && receiver is not IDisposable
            && receiver is not Type
            && receiver is not ClassInstance
            && receiver is not StructInstance)
            return null;

        // Script-declared INSTANCE extension members: reached through a VALUE of the extended type, and
        // only now, every arm above has had its chance and the reflection path below is the last resort,
        // so a value's own member still wins (C# spec 7.6.5.2).
        if (TryInvokeInstanceExtension(receiver, methodName, args, node.Line, node.Column, out var extInstanceResult))
            return extInstanceResult;

        var isStaticCall = receiver is Type;
        var actualType = isStaticCall ? (Type)receiver! : receiver!.GetType();
        var argCount = args.Length;

        // Fast path: resolved-method cache keyed by type token + typed arg hash, no allocation on a
        // hit. It stays ahead of the shared dispatch because it is the tree's hottest lookup: the hit
        // is handed to the core, and a miss warms this cache through overloadCacheSlot.
        var argTypesHash = 0;
        for (var h = 0; h < argCount; h++)
        {
            var at = args[h]?.GetType() ?? typeof(object);
            int token = 0;
            try { token = at.MetadataToken; } catch { /* fallback: use FullName hash */ }
            argTypesHash = HashCode.Combine(argTypesHash, token);
        }
        nint typeToken = 0;
        try { typeToken = actualType.MetadataToken; } catch { /* skip cache for types without metadata */ }

        (MethodInfo Method, ParameterInfo[] Params)? preResolved = null;
        var typedCall = node.TypeArgs is { Count: > 0 };
        // A call that names type arguments skips this cache: its key does not carry them, so a hit would hand
        // back the method resolved for an untyped call.
        if (!typedCall
            && typeToken != 0 && _methodOverloadCache.TryGetValue((typeToken, methodName, argCount, argTypesHash), out var cachedOverload) && cachedOverload != null)
            preResolved = cachedOverload;

        return InvokeClrMethod(receiver, methodName, args, node.Line, node.Column, node.ArgNames, preResolved,
            (typeToken, methodName, argCount, argTypesHash), node.TypeArgs);
    }

    public object VisitIsNullExpr(IsNullExpr node) { var left = Evaluate(node.Left); if (ReferenceEquals(left, YieldSentinel)) return YieldSentinel; return left == null; }

    public object VisitIsPatternExpr(IsPatternExpr node)
    {
        var leftValue = Evaluate(node.Left);
        if (ReferenceEquals(leftValue, YieldSentinel)) return YieldSentinel;

        if (node.MatchPattern != null)
        {
            var names = PatternBindingNames(node.MatchPattern);
            // Compatibility: the classic `expr is Type name` form binds the name in the CURRENT
            // scope when used outside an if/while condition (so `x is string s && s.Length > 0`
            // works). Other pattern kinds keep the if/while-only rule.
            if (names.Count == 1 && node.MatchPattern is TypePattern { BindingName: { } inlineName })
            {
                var inlineOk = PatternMatches(node.MatchPattern, leftValue, out _);
                if (!CurrentFrame.Scopes.TryGet(inlineName, out _))
                    CurrentFrame.Scopes.Define(inlineName, null, node.Line, node.Column);
                CurrentFrame.Scopes.Set(inlineName, inlineOk ? leftValue : null, node.Line, node.Column);
                return inlineOk;
            }
            if (names.Count > 0 && !_condBinding)
                RuntimeError($"Pattern variable '{names[0]}' is only allowed in if/while conditions.",
                    node.Line, node.Column);
            var okPattern = PatternMatches(node.MatchPattern, leftValue, out var patternBinds);
            _lastIsSubjectValue = leftValue;
            if (_condBinding)
            {
                _pendingPatternNames = new string?[patternBinds.Count];
                _pendingPatternValues = new object?[patternBinds.Count];
                for (var i = 0; i < patternBinds.Count; i++)
                {
                    _pendingPatternNames[i] = patternBinds[i].Name;
                    _pendingPatternValues[i] = patternBinds[i].Value;
                }
            }
            return okPattern;
        }

        var isCompatible = (bool)VisitIsExpr(IsExpr.Create(node.Left, node.Type, node.Line, node.Column));
        if (!CurrentFrame.Scopes.TryGet(node.VarName, out _))
            CurrentFrame.Scopes.Define(node.VarName, null, node.Line, node.Column);
        if (!isCompatible) { CurrentFrame.Scopes.Set(node.VarName, null, node.Line, node.Column); return false; }
        CurrentFrame.Scopes.Set(node.VarName, leftValue, node.Line, node.Column);
        return true;
    }

    private object? _lastIsSubjectValue;   // subject of the most recent `is` evaluation (if/while binding)
    private bool _condBinding;              // true while evaluating an if/while condition
    private string?[]? _pendingPatternNames;   // pattern TypedVar names awaiting branch binding
    private object?[]? _pendingPatternValues;  // matched element values aligned with the names

    public object VisitIsTuplePatternExpr(IsTuplePatternExpr node)
    {
        var subject = Evaluate(node.Subject);
        if (ReferenceEquals(subject, YieldSentinel)) return YieldSentinel;
        _lastIsSubjectValue = subject;
        // Pattern variables may only be bound by an enclosing if/while condition.
        foreach (var el in node.Pattern.Elements)
            if (el.Kind == TupleElementKind.TypedVar && !_condBinding)
                RuntimeError($"Tuple pattern variable '{el.Name}' is only allowed in if/while conditions.",
                    node.Line, node.Column);
        var matched = MatchPositional(subject, node.Pattern.Elements, out var binds);
        if (_condBinding)
        {
            _pendingPatternNames = new string?[node.Pattern.Elements.Count];
            _pendingPatternValues = binds;
            for (var i = 0; i < node.Pattern.Elements.Count; i++)
                _pendingPatternNames[i] = node.Pattern.Elements[i].Name;
        }
        return matched;
    }

    /// <summary>Slice capture keeps the subject's kind: a List yields a List (with the subject's
    /// element type), a string yields char[], an array or tuple yields object?[].</summary>
    private static object? BuildSliceCapture(object? subject, int start, int count)
    {
        switch (subject)
        {
            case IScriptList list:
                {
                    var captured = CreateScriptList(list.ElementType);
                    for (var i = 0; i < count; i++) captured.AddRaw(list[start + i]!);
                    return captured;
                }
            case string s:
                {
                    var chars = new char[count];
                    for (var i = 0; i < count; i++) chars[i] = s[start + i];
                    return chars;
                }
            default:
                {
                    var items = new object?[count];
                    for (var i = 0; i < count; i++) items[i] = PosItem(subject, start + i);
                    return items;
                }
        }
    }

    /// <summary>Shared with the VM's TRY_GET_PROP: resolves a property-pattern member using the
    /// same order as the tree matcher (dictionary key, named-tuple element, instance member).</summary>
    internal bool TryResolvePatternMember(object? target, string name, out object? value)
        => TryGetPatternMember(target, name, out value);

    /// <summary>Resolves one member of a property pattern. Returns false (a non-match, never an
    /// error) when the value has no such member: unknown instance member, absent dictionary key,
    /// or an unnamed/unknown tuple element. Instances reuse the same member lookup as `x.Member`.</summary>
    private bool TryGetPatternMember(object? value, string name, out object? memberValue)
    {
        memberValue = null;
        switch (value)
        {
            case null:
                return false;

            // Dictionaries: key lookup (script wrappers and raw dict literals).
            case IScriptDictionary dict:
                if (!dict.ContainsKey(name)) return false;
                memberValue = dict[name];
                return true;

            case IDictionary objDict:
                if (!objDict.Contains(name)) return false;
                memberValue = objDict[name];
                return true;

            // Named tuples: element name → index.
            case ScriptTuple { Names: { Count: > 0 } names } tup:
                for (var i = 0; i < names.Count && i < tup.Length; i++)
                {
                    if (!string.Equals(names[i], name, StringComparison.Ordinal)) continue;
                    memberValue = tup[i];
                    return true;
                }
                return false;

            case ClassInstance or StructInstance:
                try { memberValue = GetMemberValue(value, name, 0, 0); return true; }
                catch (ScriptException) { return false; }   // unknown member ⇒ no match

            default:
                return false;
        }
    }

    /// <summary>Names bound by a pattern (used to enforce the if/while-only binding rule).</summary>
    internal static List<string> PatternBindingNames(Pattern pattern)
    {
        var names = new List<string>();
        switch (pattern)
        {
            case VarPattern vp: names.Add(vp.BindingName); break;
            case TypePattern tp when tp.BindingName != null: names.Add(tp.BindingName); break;
            case TupleArmPattern tap:
                foreach (var el in tap.Elements)
                    if (el.Name != null) names.Add(el.Name);
                break;
            case GroupPattern gp: names.AddRange(PatternBindingNames(gp.Inner)); break;
            case PropertyPattern pp:
                foreach (var (_, memberPattern) in pp.Members)
                    names.AddRange(PatternBindingNames(memberPattern));
                break;
            case ListPattern lp:
                foreach (var element in lp.Elements) names.AddRange(PatternBindingNames(element));
                if (lp.Slice?.CaptureName != null) names.Add(lp.Slice.CaptureName);
                break;
            case LogicalPattern { Kind: LogicalKind.And } ap:
                names.AddRange(PatternBindingNames(ap.Left));
                names.AddRange(PatternBindingNames(ap.Right!));
                break;
            case LogicalPattern { Kind: LogicalKind.Or } op: names.AddRange(PatternBindingNames(op.Left)); break;
            case LogicalPattern: break;   // `not` discards bindings
        }
        return names;
    }

    // Called right after the if/while branch scope is pushed; defines the pending pattern
    // variables into that scope, then clears them for the next condition evaluation.
    private void ApplyPatternBindings(int line, int column)
    {
        var names = _pendingPatternNames;
        var values = _pendingPatternValues;
        _pendingPatternNames = null;
        _pendingPatternValues = null;
        if (names == null) return;
        for (var i = 0; i < names.Length; i++)
            if (names[i] != null)
                CurrentFrame.Scopes.Define(names[i]!, values![i], line, column);
    }

    public object VisitIsExpr(IsExpr node)
    {
        var left = Evaluate(node.Left);
        if (ReferenceEquals(left, YieldSentinel)) return YieldSentinel;
        return IsTypeCheck(left, node.Type, node.Line, node.Column);
    }

    /// <summary>Shared 'is' type check, used by the tree-walker AND the bytecode VM.</summary>
    internal bool IsTypeCheck(object? left, TypeInfo targetType, int line, int column)
    {
        if (!string.IsNullOrEmpty(targetType.TypeName) && !IsKnownType(targetType.TypeName))
            RuntimeError($"Type '{targetType.TypeName}' is unknown or undefined.", line, column);
        // An enum member names its enum, exactly as a CLR enum member does. Two comparisons, two rules, each
        // matching what this method already does: the enum OWN name is a script-declared name, so it follows
        // the interpreter comparer like a script class or struct name does, and the unknown-type guard just
        // above has already refused a name no declaration answers to. "Enum" is a CLR type name, which the
        // registry resolves case-insensitively on purpose. The underlying long STILL answers for the numeric
        // types, recursively, so `is long` is unchanged from when a member was a bare long.
        if (left is EnumValue evl)
        {
            if (targetType.TypeName != null
                && (targetType.TypeName.Equals(evl.EnumName, Comparison)
                    || targetType.TypeName.Equals("Enum", Comparison)))
                return true;
            return IsTypeCheck(evl.Value, targetType, line, column);
        }
        if (left == null)
        {
            if (targetType.IsNullable || targetType.BaseType == TokenType.StringType) return true;
            return targetType is { BaseType: TokenType.Object, TypeName: null };
        }

        // An array SHAPE is part of the type a script writes, so it is answered before the scalar branch
        // below: `int[]` arrives as (BaseType=Int, JaggedDepth=1, TypeName=null), so the scalar switch compared
        // the ELEMENT, and said that an int[] was no int[] while a string WAS a string[].
        if (targetType.IsArray) return ArrayTypeMatches(left, targetType);

        if (targetType.TypeName == null) return targetType.BaseType switch
        {
            TokenType.Int => left is int or long,
            TokenType.Long => left is long or int,
            TokenType.Short => left is short or long or int,
            TokenType.UShort => left is ushort or int or long,
            TokenType.Byte => left is byte or long or int,
            TokenType.SByte => left is sbyte or int or long,
            TokenType.UInt => left is uint or long or int,
            TokenType.ULong => left is ulong or long,
            TokenType.Float => left is float or double or long or int,
            TokenType.Double => left is double or float or long or int,
            TokenType.Decimal => left is decimal,
            TokenType.Bool => left is bool,
            TokenType.Char => left is char,
            TokenType.StringType => left is string,
            TokenType.Object => true,
            TokenType.DateTime => left is DateTime,
            TokenType.DateTimeOffset => left is DateTimeOffset,
            TokenType.TimeSpan => left is TimeSpan,
            _ => false,
        };

        var typeName = targetType.TypeName;
        if (typeName.Equals("Type", Comparison))
            return left is Type;

        switch (left)
        {
            case ScriptFunction fn when IsDelegateCompatible(fn, typeName):
            case ClassInstance classInstance when IsClassCompatible(classInstance, TypeInfo.Spelling(targetType)):
            // A struct value answers to its OWN name (`p is Pt`), the case the `as` copy had and this one
            // did not, which is why the two disagreed before they were collapsed into one implementation.
            case StructInstance structInstance when typeName.Equals(structInstance.StructName, Comparison):
                return TypeArgumentsMatch(left, targetType);
            // Design C: a struct answers a DECLARED interface STRUCTURALLY too, the way a class does.
            case StructInstance si when MatchesDeclaredInterface(si, TypeInfo.Spelling(targetType)):
                return TypeArgumentsMatch(left, targetType);

            default:
                return TryMatchKnownType(left, typeName, targetType, line, column) && TypeArgumentsMatch(left, targetType);
        }
    }

    /// <summary>Does a value satisfy an array SHAPE? `int[]` arrives as (BaseType=Int, JaggedDepth=1,
    /// TypeName=null), so a shape has to be answered BEFORE any scalar comparison: comparing the element made
    /// an int[] fail its own type while a string passed for `string[]`.</summary>
    private bool ArrayTypeMatches(object? value, TypeInfo targetType)
    {
        if (value is not Array array) return false;
        var element = targetType with
        {
            ArrayRank = 0,
            JaggedDepth = targetType.JaggedDepth > 0 ? targetType.JaggedDepth - 1 : 0,
        };
        var actualElement = array.GetType().GetElementType();
        return actualElement != null && TypeLabel(element).Equals(GetFriendlyTypeName(actualElement), Comparison);
    }

    /// <summary>The name a script sees for a type, `int[]`, `List<int>`, `Animal`. The shape matters and
    /// TypeInfo.ToString drops adjacency depth, so brackets and arguments are rendered here, which is what the
    /// comparisons against a value's reported name need.</summary>
    private static string TypeLabel(TypeInfo t)
    {
        var name = t.TypeName ?? TypeInfo.SimpleTypeName(t.BaseType);
        if (t.GenericArgs is { Count: > 0 } args) name += "<" + string.Join(", ", args.Select(TypeLabel)) + ">";
        for (var i = 0; i < t.ArrayRank + t.JaggedDepth; i++) name += "[]";
        if (t.IsNullable) name += "?";
        return name;
    }

    /// <summary>The type arguments a value REPORTS, read from the name typeof renders for it, a list, a dict,
    /// a set and a script generic all answer through the same name. Null when the value reports none.</summary>
    private static IReadOnlyList<string>? ReportedTypeArguments(object? value)
    {
        if (value == null) return null;
        var name = GetFriendlyTypeNameOfValue(value);
        var open = name.IndexOf('<');
        if (open < 0 || !name.EndsWith(">", StringComparison.Ordinal)) return null;
        // A NULLABLE MARK IS A DECLARATION, NOT AN ARGUMENT a resolver can use. `typeof` keeps the value's own
        // spelling (`List<object?>`), and this, the one place that turns a name back into type ARGUMENTS, strips
        // the mark so every consumer sees the bare name it can resolve. That is the bridge a display name must not
        // cross unchanged.
        var inner = name[(open + 1)..^1];   // the mark is KEPT: these arguments are COMPARED against a target label
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '<': depth++; break;
                case '>': depth--; break;
                case ',' when depth == 0:
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        parts.Add(inner[start..].Trim());
        return parts;
    }

    /// <summary>Compares a generic spelling's ARGUMENTS (`List<int>`) with the ones the value reports, the
    /// name-based match alone answered `xs is List<string>` with True for a List&lt;int&gt;. When the value
    /// reports no arguments (a delegate, a CLR wrapper) there is nothing to compare and the name-based answer
    /// stands, which keeps `fn is Func&lt;int,int&gt;` behaving as documented.</summary>
    private bool TypeArgumentsMatch(object? value, TypeInfo targetType)
    {
        if (targetType.GenericArgs is not { Count: > 0 } targets) return true;
        var actual = ReportedTypeArguments(value);
        if (actual == null || actual.Count != targets.Count) return true;
        for (var i = 0; i < targets.Count; i++)
            if (!TypeArgumentsAgree(actual[i], ArgumentLabel(targets[i]))) return false;
        return true;
    }

    /// <summary>The label of a type ARGUMENT a script wrote, with a NAME THE RUNNING INSTANTIATION BOUND replaced by
    /// its binding's label. This is the comparison half of the same rule <c>ResolveTypeToClr</c> states for a type
    /// POSITION: measured, `o is IEnumerable<T>` inside a generic body answered FALSE from a bundle for a value that
    /// IS one, because the name-based test passed and then this comparison refused it: the target's argument was
    /// still spelled `T` while the value reports `string`. A name no instantiation bound is rendered as written.</summary>
    private string ArgumentLabel(TypeInfo t, int depth = 0)
    {
        if (depth < 4 && t.TypeName is { Length: > 0 } n && ActiveTypeBindings is { } active
            && active.TryGetValue(n, out var bound) && !string.Equals(bound.TypeName, n, Comparison))
            return ArgumentLabel(bound, depth + 1);
        var name = t.TypeName ?? TypeInfo.SimpleTypeName(t.BaseType);
        if (t.GenericArgs is { Count: > 0 } args) name += "<" + string.Join(", ", args.Select(a => ArgumentLabel(a, depth + 1))) + ">";
        for (var i = 0; i < t.ArrayRank + t.JaggedDepth; i++) name += "[]";
        if (t.IsNullable) name += "?";
        return name;
    }

    /// <summary>Whether a value's reported argument agrees with a target's label. Equal, with ONE exception:
    /// `object` and `object?` name the same element (an untyped element always admits null), so a target written
    /// either way matches the value's spelling. Every other mark is significant, and blurring it is what made
    /// `is List<string?>` answer false while `is List<string>` answered true on the same value.</summary>
    private bool TypeArgumentsAgree(string actual, string target)
    {
        var (actualName, actualMarked) = SplitNullabilityMark(actual);
        var (targetName, targetMarked) = SplitNullabilityMark(target);
        if (!actualName.Equals(targetName, Comparison)) return false;
        if (actualMarked == targetMarked) return true;
        // The marks differ, which is the C# rule for a REFERENCE element: nullability is ERASED there, so
        // `List<string?>` IS `List<string>` at runtime and a target written either way matches. A VALUE element
        // keeps it (`int?` is a `Nullable<int>`, a different type), so `int` and `int?` stay distinct. A NAME
        // cannot express `Nullable<T>`, so a named type is treated as a reference element and its marks blur,
        // which is the one approximation here.
        return !IsValueElementName(actualName);
    }

    private static (string Name, bool Marked) SplitNullabilityMark(string name)
        => name.EndsWith('?') ? (name[..^1], true) : (name, false);

    private static bool IsValueElementName(string name) => name is
        "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong"
        or "float" or "double" or "decimal" or "char";

    private bool IsClassCompatible(object obj, string className)
    {
        if (obj is not ClassInstance classInstance) return false;
        if (classInstance.Class.Name.Equals(className, Comparison)) return true;
        var current = classInstance.Class.Parent;
        while (current != null) { if (current.Name.Equals(className, Comparison)) return true; current = current.Parent; }
        if (ImplementsInterface(classInstance, className)) return true;
        // Design C: `is` also answers STRUCTURALLY for a DECLARED interface, so a value whose class never wrote
        // `implements` still matches when it provides every member the contract demands. The name is looked up in
        // the interface registry, so a class name, a struct name or a library name is unaffected.
        if (MatchesDeclaredInterface(classInstance, className)) return true;
        return false;
    }

    public object? VisitAsExpr(AsExpr node)
    {
        var left = Evaluate(node.Left);
        if (ReferenceEquals(left, YieldSentinel)) return YieldSentinel;
        return AsValue(node.Type, left, node.Line, node.Column);
    }

    /// <summary>True when a value satisfies a written type, asked by `as`. ONE implementation: it asks
    /// <see cref="IsTypeCheck"/>, the copy this method used to be is how the two drifted (the old twin even
    /// carried a StructInstance case that `is` did not have). The only difference is that an UNKNOWN name does
    /// not throw here, because `as` reports a failed conversion as null rather than as an error.
    private bool IsValueCompatibleWithType(object? value, TypeInfo targetType)
    {
        if (targetType.TypeName is { Length: > 0 } name && !IsKnownType(name)) return false;
        return IsTypeCheck(value, targetType, 0, 0);
    }

    private bool TryMatchKnownType(object? value, string typeName, TypeInfo targetType, int line, int column) => typeName switch
    {
        "IScriptList" => value is IScriptList,
        "IScriptDictionary" => value is IScriptDictionary,
        "List" => value is IScriptList,
        // A dict literal is a raw Dictionary<object,object?> (not an IScriptDictionary wrapper),
        // so `is`/`as`/cast on Dictionary must accept the raw CLR dictionary too.
        "Dictionary" => value is IScriptDictionary or IDictionary,
        "IDictionary" => value is IDictionary,
        "Array" => value is Array,
        "string" or "String" => value is string,
        "int" or "Int32" => value is int or long,
        "long" or "Int64" => value is long or int,
        "double" or "Double" => value is double or float,
        "float" or "Single" => value is float or double,
        "bool" or "Boolean" => value is bool,
        "byte" or "Byte" => value is byte or long or int,
        "sbyte" or "SByte" => value is sbyte or long or int,
        "short" or "Int16" => value is short or long or int,
        "ushort" or "UInt16" => value is ushort or long or int,
        "uint" or "UInt32" => value is uint or long,
        "ulong" or "UInt64" => value is ulong or long,
        "decimal" or "Decimal" => value is decimal,
        "char" or "Char" => value is char,
        "DateTime" => value is DateTime,
        "TimeSpan" => value is TimeSpan,
        "object" or "Object" => true,
        "Coroutine" => value is Coroutine,
        // A generator, what a `yield` function's call returns, and what `typeof` reports as "Generator". The
        // registry holds the OPEN definition (Generator<T>), which IsInstanceOfType can never match, so this arm
        // is what keeps the name `typeof` reports in agreement with `is`: without it the answer was a silent
        // False, the failure shape this switch's default arm documents.
        "Generator" => value is not null && value.GetType().IsGenericType
                       && value.GetType().GetGenericTypeDefinition() == typeof(Generator<>),
        "EventInfo" => value is EventInfo,
        "CombinedDelegate" or "delegate" => value is CombinedDelegate,
        "DelegateInfo" => value is DelegateInfo,
        "Namespace" => value is Namespace,
        "Native" => value is NativeFunctionAdapter,
        "ScriptResult" or "Result" => value is ScriptResult,
        "Tuple" => value is ScriptTuple,
        "MemBuffer" => value is MemBuffer,
        "VoidMarker" => value is VoidMarker,
        "RefCell" => value is RefCell,
        "WeakRef" => value is WeakRef,
        "Class" or "ClassInstance" => value is ClassInstance,
        "Struct" or "StructInstance" => value is StructInstance,
        // A type DEFINITION used as a value, a class, record, struct or enum NAME. Class, record and enum
        // bind a ClassInfo; a struct binds a bare TypeInfo handle, so both CLR types answer to the one name.
        "ClassInfo" => value is ClassInfo or TypeInfo,
        "ErrorInfo" => value is ScriptErrorInfo,
        "HashSet" or "IScriptHashSet" => value is IScriptHashSet,
        "CronExpression" => value is CronExpression,
        "CronType" => value is CronType,
        "Iterable" => Interpreter.IsIterable(value),   // unified contract, every foreach/LINQ-enumerable kind
        "RandomAccess" => Interpreter.IsRandomAccess(value),   // indexable + re-enumerable: arrays, lists, tuples, strings
        "Function" => Interpreter.IsFunction(value),   // callable contract, script functions/lambdas, bound methods, delegates, natives
        "Delegate" => value is Delegate,
        "Regex" => value is Regex,
        "Predicate" or "Predicate<T>" => value is Delegate,
        "Func" => value is Delegate,
        "Action" => value is Delegate,
        // A name the SCRIPT registry does not hold may still be a CLR type the type index resolves (`Uri`,
        // `Stopwatch`, `Hashtable`, `Version`), and the answer for one of those was a silent FALSE: the guard
        // above accepts the name through `IsKnownType`, which asks ResolveTypeToClr, so nothing refused and this
        // arm then read the registry ALONE. That is the failure shape this default arm's own comment warns about
        // (the same one the DockStyle binding recorded), so the index is asked here too.
        _ => TryMatchClrOrRegistryType(value, typeName, targetType, line, column),
    };

    /// <summary>The script registry, then the CLR type index. A CLR GENERIC resolves to its OPEN DEFINITION, and
    /// the type arguments a script wrote have to be APPLIED, or the instance test can never succeed: measured,
    /// `"a" is IEquatable<string>` and `x is IAsyncEnumerable<string>` answered False for values that ARE those
    /// interfaces, and `"a" is IComparable<string>` passed only because a string also implements the NON-generic
    /// `IComparable`, whose arguments were being ignored. An open generic written WITHOUT arguments is C#'s CS0308,
    /// so it is refused by name rather than answered false (the silent-False outcome this repository refuses).</summary>
    private bool TryMatchClrOrRegistryType(object? value, string typeName, TypeInfo targetType, int line, int column)
    {
        if (ScriptTypeRegistry.TryGetValue(typeName, out var regType) && regType.IsInstanceOfType(value)) return true;
        if (value is null) return false;
        if (ResolveTypeToClr(typeName, lax: !_caseSensitive) is not { } clrType) return false;
        // A SCRIPT CLASS EXTENDING A BRIDGED CLR BASE answers through the FACE it is handed to the CLR as, which is
        // what makes `sink is TextWriter` true for the sink a script declared.
        if (value is ClassInstance bridgeValue && ScriptTextBridge.Is(bridgeValue, clrType)) return true;
        if (!clrType.IsGenericTypeDefinition) return clrType.IsInstanceOfType(value);
        var args = targetType.GenericArgs;
        if (args is not { Count: > 0 })
        {
            // A NAME THAT ALREADY CARRIES ITS ARITY (`Action`2`) IS COMPLETE, so this is not the
            // forgot-the-arguments case: the arity IS the identity a script can write, which is exactly what the
            // delegate arms upstream rely on (`a2 is Action`2` answers True). Reaching here means those arms said
            // NO, so the answer is False rather than a refusal. Measured: `a1 is Action`2` threw
            // "'Action`2' is a generic type: write its type arguments" where the answer is False, on the
            // maintainer's own 41_test_step1_delegate_types.slc (L17), and False is also what the CLR itself
            // answers for a value against an open generic definition.
            if (typeName.Contains('`')) return false;
            // A name WITHOUT an arity is C#'s CS0308, and answering it False is the silent-False outcome this
            // repository refuses, so it stays a refusal that names the fix.
            RuntimeError($"'{typeName}' is a generic type: write its type arguments, as in '{typeName}<...>'.", line, column);
            throw new InvalidOperationException("unreachable");
        }
        var closed = TryCloseClrGeneric(clrType, args, line, column);
        return closed.IsInstanceOfType(value);
    }

    /// <summary>Closes a CLR generic definition with the type arguments a script wrote, resolving each of them
    /// (and nesting, so `IEnumerable<KeyValuePair<string, int>>` works). The refusal names what could not be used
    /// rather than answering false.</summary>
    private Type TryCloseClrGeneric(Type definition, IReadOnlyList<TypeInfo> args, int line, int column)
    {
        var typeParams = definition.GetGenericArguments();
        if (typeParams.Length != args.Count)
            RuntimeError($"'{GetFriendlyTypeName(definition)}' takes {typeParams.Length} type argument(s), "
                + $"{args.Count} given.", line, column);
        var closed = new Type[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var argName = args[i].TypeName;
            // A NAME NOTHING DEFINES IS REFUSED HERE TOO, which is what `is` already does for the OUTER name
            // ("Type 'X' is unknown or undefined") and what makes the two agree: without this the argument
            // silently became `object` and the whole check answered false. Measured: `x is
            // IAsyncEnumerable<NoSuchTypeXyz>` answered False while C# refuses the name, and the CAST spelling of
            // the same expression already refused.
            if (argName is { Length: > 0 } && !IsKnownTypeName(argName))
                RuntimeError($"Type '{argName}' is unknown or undefined, so it cannot be a type argument.", line, column);
            // A KEYWORD type (`string`, `int`) carries no TypeName: its CLR type comes from the token, which is
            // what TypeTokenHelper does for every other declaration the engine resolves.
            // A TYPE PARAMETER bound by the instantiation that is RUNNING resolves through the ONE chain that knows
            // the binding (ResolveTypeToClr), and it is asked BEFORE TypeTokenHelper, which answers `object` for a
            // name that is not a keyword: measured, `o is IEnumerable<T>` inside a generic body answered FALSE from
            // a bundle (`CnGen<string>(new List<string>())`) because the argument became `object`, while `o is T`
            // looked right only because `object` matches everything.
            Type? resolvedArg = null;
            if (argName is { Length: > 0 } && ActiveTypeBindings is { } bindings
                && bindings.TryGetValue(argName, out var boundArg) && !boundArg.IsGenericParameter)
                resolvedArg = ResolveTypeToClr(argName, lax: !_caseSensitive);
            resolvedArg ??= TypeTokenHelper.GetSystemType(args[i])
                ?? (argName is { Length: > 0 } ? ResolveTypeToClr(argName, lax: !_caseSensitive) : null);
            if (resolvedArg == null)
            {
                RuntimeError($"'{TypeInfo.Spelling(args[i])}' is not a type argument this engine can use for "
                    + $"'{GetFriendlyTypeName(definition)}'.", line, column);
                throw new InvalidOperationException("unreachable");
            }
            if (resolvedArg.IsGenericTypeDefinition && args[i].GenericArgs is { Count: > 0 } inner)
                resolvedArg = TryCloseClrGeneric(resolvedArg, inner, line, column);
            closed[i] = resolvedArg;
        }
        try { return definition.MakeGenericType(closed); }
        catch (Exception)
        {
            RuntimeError($"'{GetFriendlyTypeName(definition)}' cannot be used with those type arguments here.", line, column);
            throw;
        }
    }

    public object? VisitCastExpr(CastExpr node)
    {
        var value = Evaluate(node.Expression);
        if (ReferenceEquals(value, YieldSentinel)) return YieldSentinel;
        return CastValue(node.Type, value, node.Line, node.Column);
    }

    private static bool CanConvert(TokenType sourceType, TokenType targetType) => TypeSystem.CanConvert(sourceType, targetType);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumericType(TokenType type) => TypeSystem.IsNumeric(type);

    private bool IsDelegateCompatible(ScriptFunction fn, string delegateTypeName) => IsDelegateCompatibleInternal(fn, delegateTypeName);

    private bool IsDelegateCompatibleInternal(ScriptFunction fn, string delegateTypeName)
    {
        var fnReturn = fn.ReturnType; var fnParams = fn.ParameterTypes; var span = delegateTypeName.AsSpan();

        if (span.Equals("Action".AsSpan(), Comparison)) return fnReturn.BaseType == TokenType.Void;
        if (span.Equals("Func".AsSpan(), Comparison)) return fnReturn.BaseType != TokenType.Void;
        if (span.Equals("Predicate".AsSpan(), Comparison)) return fnReturn.BaseType switch { TokenType.Bool => fnParams.Count == 1, TokenType.Object => fnParams.Count == 1 && ReturnsBool(fn), _ => false };
        if (span.Equals("Comparison".AsSpan(), Comparison)) return fnReturn.BaseType switch { TokenType.Int => fnParams.Count == 2, TokenType.Object => fnParams.Count == 2 && ReturnsInt(fn), _ => false };
        if (span.Equals("Converter".AsSpan(), Comparison)) return fnParams.Count == 1 && fnReturn.BaseType != TokenType.Void;
        if (span.Equals("EventHandler".AsSpan(), Comparison)) return fnReturn.BaseType == TokenType.Void && fnParams.Count == 2;

        var backtickIndex = span.IndexOf('`');
        if (backtickIndex >= 0)
        {
            var baseSpan = span[..backtickIndex]; var aritySpan = span[(backtickIndex + 1)..];
            if (!int.TryParse(aritySpan, out var arity)) return false;
            if (baseSpan.Equals("Action".AsSpan(), Comparison)) return fnReturn.BaseType == TokenType.Void && fnParams.Count == arity;
            if (baseSpan.Equals("Func".AsSpan(), Comparison)) return fnReturn.BaseType != TokenType.Void && fnParams.Count == arity - 1;
            if (baseSpan.Equals("Predicate".AsSpan(), Comparison)) return fnReturn.BaseType switch { TokenType.Bool => fnParams.Count == 1, TokenType.Object => fnParams.Count == 1 && ReturnsBool(fn), _ => false };
            if (baseSpan.Equals("Comparison".AsSpan(), Comparison)) return fnReturn.BaseType switch { TokenType.Int => fnParams.Count == 2, TokenType.Object => fnParams.Count == 2 && ReturnsInt(fn), _ => false };
            if (baseSpan.Equals("Converter".AsSpan(), Comparison)) return fnParams.Count == 1 && fnReturn.BaseType != TokenType.Void;
            if (baseSpan.Equals("EventHandler".AsSpan(), Comparison)) return fnReturn.BaseType == TokenType.Void && fnParams.Count == 2;
            return false;
        }

        var angleStart = span.IndexOf('<');
        if (angleStart >= 0)
        {
            var angleEnd = span.LastIndexOf('>');
            if (angleEnd < 0) angleEnd = span.Length - 1;
            var baseSpan = span[..angleStart]; var argsSpan = span[(angleStart + 1)..angleEnd];
            var typeArgCount = 1; foreach (var c in argsSpan) if (c == ',') typeArgCount++;
            if (baseSpan.Equals("Action".AsSpan(), Comparison)) return fnReturn.BaseType == TokenType.Void && fnParams.Count == typeArgCount;
            if (baseSpan.Equals("Func".AsSpan(), Comparison)) return fnReturn.BaseType != TokenType.Void && fnParams.Count == typeArgCount - 1;
            if (baseSpan.Equals("Predicate".AsSpan(), Comparison)) return fnReturn.BaseType switch { TokenType.Bool => fnParams.Count == 1, TokenType.Object => fnParams.Count == 1 && ReturnsBool(fn), _ => false };
            if (baseSpan.Equals("Comparison".AsSpan(), Comparison)) return fnReturn.BaseType switch { TokenType.Int => fnParams.Count == 2, TokenType.Object => fnParams.Count == 2 && ReturnsInt(fn), _ => false };
            if (baseSpan.Equals("Converter".AsSpan(), Comparison)) return fnParams.Count == 1 && fnReturn.BaseType != TokenType.Void;
            if (baseSpan.Equals("EventHandler".AsSpan(), Comparison)) return fnReturn.BaseType == TokenType.Void && fnParams.Count == 2;
        }

        if (!DelegateInfos.TryGetValue(delegateTypeName, out var dInfo))
            return false;

        if (dInfo.Parameters.Count != fnParams.Count) return false;
        var returnCompatible = dInfo.ReturnType.BaseType == fnReturn.BaseType || fnReturn.BaseType == TokenType.Object || dInfo.ReturnType.BaseType == TokenType.Object;
        if (!returnCompatible)
            return false;

        // ref/out count must match to avoid index-out-of-range in the loop below
        if (dInfo.IsRefParams.Count != fn.IsRefParams.Count)
            return false;

        for (var i = 0; i < fnParams.Count; i++)
        {
            var paramCompatible = dInfo.Parameters[i].Type.BaseType == fnParams[i].BaseType || fnParams[i].BaseType == TokenType.Object || dInfo.Parameters[i].Type.BaseType == TokenType.Object;
            if (!paramCompatible)
                return false;

            if (dInfo.IsRefParams[i] != fn.IsRefParams[i])
                return false;
        }
        return true;
    }

    private static bool ReturnsBool(ScriptFunction fn)
    {
        foreach (var stmt in fn.Declaration.Body)
            if (stmt is ReturnStmt { Value: not null } ret && !IsBooleanExpression(ret.Value)) return false;
        return true;
    }

    private static bool ReturnsInt(ScriptFunction fn)
    {
        foreach (var stmt in fn.Declaration.Body)
            if (stmt is ReturnStmt { Value: NumberExpr }) return true;
        return false;
    }

    private static bool IsBooleanExpression(Expr expr) => expr switch
    {
        BoolExpr => true,
        BinaryExpr binary => binary.Operator.Type is TokenType.EqualEqual or TokenType.BangEqual or TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual,
        LogicalExpr => true,
        UnaryExpr unary => unary.Operator.Type == TokenType.Bang,
        VariableExpr => true,
        CallExpr => true,
        PropertyAccessExpr => true,
        ConditionalExpr cond => IsBooleanExpression(cond.ThenExpr) || IsBooleanExpression(cond.ElseExpr),
        _ => false
    };

    private MethodInfo? FindBestMethod(Type receiverType, string methodName, int argCount, Type[] argTypes, BindingFlags bindingFlags)
    {
        // ONE scoring implementation, asked of the receiver type and then of the INTERFACES it implements.
        MethodInfo? SearchOn(Type type)
        {
            var receiverType = type;
            while (true)
            {
                var methods = receiverType.GetMethods(bindingFlags);
                MethodInfo? best = null;
                var bestIsExact = false;
                var bestDiff = int.MaxValue;
                var bestNameLen = int.MaxValue;
                var bestObjPenalty = int.MaxValue;
                var bestExactCnt = -1;

                foreach (var m in methods)
                {
                    if (!m.Name.Equals(methodName, Comparison)) continue;
                    // An OPEN generic definition is never the answer for a call that did not name its type arguments:
                    // invoking it always fails with the CLR's "Late bound operations cannot be performed on types or
                    // methods for which ContainsGenericParameters is true", a sentence that names neither the member
                    // nor what to do. A call that DID name them has already been closed before reaching here, so
                    // skipping the definition turns a guaranteed failure into the ordinary member-miss report.
                    if (m.IsGenericMethodDefinition) continue;
                    var ps = m.GetParameters();
                    var hasParams = ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false);
                    var totalFixedParams = hasParams ? ps.Length - 1 : ps.Length;
                    var requiredParams = 0;
                    for (var j = 0; j < totalFixedParams; j++)
                        if (!ps[j].IsOptional)
                            requiredParams++;
                    if (requiredParams > argCount) continue;
                    // Reject non-params methods when given more args than parameters
                    if (!hasParams && argCount > ps.Length) continue;

                    var compatible = true;
                    for (var i = 0; i < argCount; i++)
                    {
                        var paramType = i < totalFixedParams ? ps[i].ParameterType : hasParams ? ps[totalFixedParams].ParameterType.GetElementType()! : typeof(object);
                        if (IsTypeCompatibleForInvoke(argTypes[i], paramType))
                            continue;

                        compatible = false;
                        break;
                    }

                    if (!compatible) continue;
                    for (var i = argCount; i < totalFixedParams; i++)
                    {
                        if (ps[i].IsOptional)
                            continue;
                        compatible = false;
                        break;
                    }

                    if (!compatible) continue;

                    var isExact = ps.Length == argCount || (hasParams && argCount >= totalFixedParams);
                    var diff = ps.Length - argCount;

                    // Count args with an exact parameter-type match. Math.Abs has single-arg
                    // overloads for every numeric type incl. nint/nuint; without this tie-break
                    // the first compatible overload in metadata order wins, so Math.Abs(1.5)
                    // could bind Abs(nint) and fail with "Double -> IntPtr" on CoerceArgument.
                    var exactCnt = 0;

                    // Penalty for delegate params with 'object' type arg, prefer more specific delegates
                    var objPenalty = 0;
                    for (var i = 0; i < argCount; i++)
                    {
                        var pt = i < totalFixedParams ? ps[i].ParameterType : (hasParams ? ps[totalFixedParams].ParameterType.GetElementType()! : typeof(object));
                        if (argTypes[i] == pt) exactCnt++;
                        if (typeof(Delegate).IsAssignableFrom(pt) && pt.IsGenericType)
                        {
                            objPenalty += pt.GetGenericArguments().Count(ga => ga == typeof(object));
                        }
                    }

                    // Strictly better = better at the first differing key:
                    //   exact-type match count > arity exactness > extra-param diff > objPenalty > name length.
                    var better = best == null
                        || exactCnt > bestExactCnt
                        || (exactCnt == bestExactCnt && isExact && !bestIsExact)
                        || (exactCnt == bestExactCnt && isExact == bestIsExact && diff < bestDiff)
                        || (exactCnt == bestExactCnt && isExact == bestIsExact && diff == bestDiff && objPenalty < bestObjPenalty)
                        || (exactCnt == bestExactCnt && isExact == bestIsExact && diff == bestDiff && objPenalty == bestObjPenalty && m.Name.Length < bestNameLen);
                    if (!better)
                        continue;

                    best = m;
                    bestIsExact = isExact;
                    bestDiff = diff;
                    bestNameLen = m.Name.Length;
                    bestObjPenalty = objPenalty;
                    bestExactCnt = exactCnt;
                }

                if (best != null)
                    return best;

                if (!receiverType.IsNested)
                    return null;

                receiverType = receiverType.DeclaringType!;
            }
        }

        var direct = SearchOn(receiverType);
        if (direct != null) return direct;

        // AN EXPLICIT INTERFACE IMPLEMENTATION IS NOT A PUBLIC MEMBER OF THE TYPE, so a name that a script can
        // write THROUGH the interface was unreachable here. Measured before this: a compiler-generated async
        // iterator implements IAsyncEnumerable<T>.GetAsyncEnumerator and IAsyncDisposable.DisposeAsync
        // EXPLICITLY, and every one of those calls answered "Method 'GetAsyncEnumerator' not found on type
        // <IterateFileLinesAsync>d__126" on JIT and on the AOT publish, while `await foreach` worked because the
        // ENGINE's own drive casts to the interface (the script could not do it for itself). Asking the
        // interfaces closes the gap, and the interface's OWN MethodInfo invokes straight onto the instance,
        // which is how an explicit slot is reached at all. A STATIC lookup has no instance to dispatch on, and
        // the receiver's own members always win, so nothing that resolved before resolves differently here.
        if ((bindingFlags & BindingFlags.Static) != 0) return null;
        foreach (var iface in receiverType.GetInterfaces())
        {
            var viaInterface = SearchOn(iface);
            if (viaInterface != null) return viaInterface;
        }
        return null;
    }

    /// <summary>A readable, non-indexer property an INTERFACE declares, which is how an explicit
    /// implementation of it is reached (see the interface steps in FindBestMethod and the property read).</summary>
    private static PropertyInfo? InterfaceProperty(Type iface, string name, bool ignoreCase)
    {
        var flags = BindingFlags.Public | BindingFlags.Instance | (ignoreCase ? BindingFlags.IgnoreCase : 0);
        var prop = iface.GetProperty(name, flags);
        return prop != null && prop.GetIndexParameters().Length == 0 && prop.GetGetMethod() != null ? prop : null;
    }

    private static bool IsIntegerType(Type t) => t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong);

    /// <summary>Returns 0 for identity, 1 for widening, 2 for narrowing, 3 for other compatible.</summary>
    private static void ConversionDistance(Type argType, Type paramType)
    {
        if (argType == paramType) return;

        if (paramType.IsAssignableFrom(argType)) return;
    }

    /// <summary>The parameter names a `File`/`Directory` member spells a PATH with, for the CLR fallback below.
    /// Reflection gives the name, and that name is what separates a path from CONTENT (`contents`, `text`), which
    /// must travel untouched: resolving a content argument would rewrite what the caller meant to write.</summary>
    private static readonly HashSet<string> PathParameterNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "path", "paths", "file", "fileName", "sourceFileName", "destFileName", "source", "destination",
        "newPath", "oldPath", "path1", "path2", "searchPath", "searchPattern",
    };

    private static bool IsPathParameter(string? parameterName)
        => parameterName != null && PathParameterNames.Contains(parameterName);

    /// <summary>A member a CURATED namespace never registered, resolved on the CLR type behind it: `File` wraps
    /// System.IO.File, so `File.ReadAllTextAsync(p)` and `File.GetLastWriteTime(p)` work without the engine
    /// restating a 109-member surface. The curated members always win (Namespace.InvokeMethod asks its own table
    /// first), this only fills a gap, and a name the type does not have answers false so the namespace keeps its
    /// own refusal, which names what it DOES hold.
    ///
    /// TWO THINGS THIS OWES THE ENGINE'S CONTRACT, both measured requirements rather than taste:
    /// * THE PATH RULE, on the parameters that ARE a path. Every curated member runs its path through ResolvePath
    ///   (18 sites in FileIo.cs), which is the rule where `./x` means "beside the script". A fallback member that
    ///   skipped it would make `File.ReadAllTextAsync("./x")` and `File.ReadAllText("./x")` read DIFFERENT files,
    ///   so the parameter's NAME decides (see PathParameterNames).
    /// * THE MESSAGE MAP, through ClrMemberFailure, so a CLR refusal reads like every other member refusal
    ///   (`[ReadAllTextAsync]: ...` at the call's own position) rather than as the runtime's own sentence.
    ///
    /// A `Task` answers like any other CLR return, so `await File.ReadAllTextAsync(p)` works through the await
    /// opcode. What is deliberately NOT served: a member with a by-ref parameter (a namespace holds no cell to
    /// write back through; none of the three facades declares one) and a params-array method (its tail would have
    /// to be assembled here, and the curated member that shares the name answers first).
    /// </summary>
    internal bool TryInvokeClrNamespaceMember(Type clrType, string name, object?[] args, int line, int column,
        out object? result)
    {
        result = null;
        var argTypes = new Type[args.Length];
        for (var i = 0; i < args.Length; i++) argTypes[i] = args[i]?.GetType() ?? typeof(object);
        var method = FindBestMethod(clrType, name, args.Length, argTypes, BindingFlags.Public | BindingFlags.Static);
        if (method == null) return false;
        var ps = method.GetParameters();
        if (ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute), false)) return false;
        foreach (var p in ps)
            if (p.ParameterType.IsByRef) return false;

        // One slot per DECLARED parameter, so an omitted OPTIONAL one travels as Type.Missing exactly as the CLR
        // expects it (`File.ReadAllLinesAsync(path)` against `(path, CancellationToken ct = default)`).
        var invokeArgs = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            if (i >= args.Length) { invokeArgs[i] = Type.Missing; continue; }
            var value = args[i];
            if (value is string pathArg && IsPathParameter(ps[i].Name)) value = ResolvePath(pathArg);
            invokeArgs[i] = CoerceArgument(value, ps[i].ParameterType, line, column);
        }
        try
        {
            var returned = method.Invoke(null, invokeArgs);
            // A CLR task becomes the ENGINE's own Task, so ONE await mechanism serves both. The script's Task is the
            // engine's completion record (Task.cs) and a System.Threading.Tasks.Task is a different type, which the
            // await arm cannot recognize: measured, `await File.ReadAllTextAsync(p)` refused with "await expects a
            // Task" while the member itself resolved. Handing the script the same record its own async calls produce
            // also keeps `typeof` and `is Task` agreeing with them. A ValueTask travels the same way (see
            // AsClrTask): measured, `await en.MoveNextAsync()` refused while the member resolved, because
            // MoveNextAsync returns ValueTask<bool>.
            result = AsClrTask(returned, out var clrTask) ? ToScriptTask(clrTask!) : returned;
            return true;
        }
        catch (Exception ex)
        {
            throw ClrMemberFailure(ex, line, column, name);
        }
    }

    /// <summary>The CLR task a value IS, when it is one. A Task is matched directly, a ValueTask too, and a
    /// ValueTask&lt;T&gt; has to go through its own type because a pattern cannot name a generic struct without its
    /// argument (and a script reaches one whenever a CLR member returns it: Stream.DisposeAsync, an enumerator's
    /// MoveNextAsync, a channel's WriteAsync).</summary>
    private static bool AsClrTask(object? value, out System.Threading.Tasks.Task? clrTask)
    {
        switch (value)
        {
            case System.Threading.Tasks.Task t:
                clrTask = t;
                return true;
            case System.Threading.Tasks.ValueTask vt:
                clrTask = vt.AsTask();
                return true;
            case null:
                clrTask = null;
                return false;
        }
        var rt = value.GetType();
        if (rt.IsValueType && rt.IsGenericType
            && rt.GetGenericTypeDefinition() == typeof(System.Threading.Tasks.ValueTask<>)
            && rt.GetMethod("AsTask", Type.EmptyTypes)?.Invoke(value, null) is System.Threading.Tasks.Task generic)
        {
            clrTask = generic;
            return true;
        }
        clrTask = null;
        return false;
    }

    /// <summary>A CLR task as the engine's own Task: finished here when it already is (the common case), and
    /// otherwise completed by a CLR continuation, which is what wakes an awaiting coroutine because SetResult
    /// signals the pump's waiters. A fault travels as the engine's own exception, so the script's `catch` sees the
    /// mapped failure rather than an AggregateException.</summary>
    private static Task ToScriptTask(System.Threading.Tasks.Task clrTask)
    {
        var record = new Task();
        if (clrTask.IsCompleted)
        {
            CompleteScriptTask(record, clrTask);
            return record;
        }
        clrTask.ContinueWith(t => CompleteScriptTask(record, t), TaskContinuationOptions.ExecuteSynchronously);
        return record;
    }

    private static void CompleteScriptTask(Task record, System.Threading.Tasks.Task clrTask)
    {
        if (clrTask.IsFaulted)
        {
            record.SetException(clrTask.Exception?.InnerException ?? clrTask.Exception!);
            return;
        }
        // A Task<T> carries its value on `Result` and a plain Task carries none. The property is read off the
        // ACTUAL type rather than cast, because the runtime's own async task (System.Runtime.CompilerServices.
        // AsyncHelpers+RuntimeAsyncTask<T>, what a framework async method returns under runtime-async=on) is not a
        // Task<T> a cast can name, while it does inherit the property.
        object? value = null;
        var resultProperty = clrTask.GetType().GetProperty("Result");
        if (resultProperty != null)
        {
            try { value = resultProperty.GetValue(clrTask); }
            catch (Exception) { value = null; }
        }
        record.SetResult(value);
    }

    private static bool IsTypeCompatibleForInvoke(Type? argType, Type paramType)
    {
        if (argType == null)
            return !paramType.IsValueType || Nullable.GetUnderlyingType(paramType) != null;

        // A SCRIPT CLASS EXTENDING A BRIDGED CLR BASE fits that base's parameter, which is what lets the OVERLOAD
        // PICKER accept `Console.SetOut(sink)`: selection runs before any coercion, so teaching only the coercion funnel
        // left the call reporting that no overload existed (measured: "Method 'SetOut' has no overload that takes these
        // arguments: parameter 'newOut' expects TextWriter").
        if (argType == typeof(ClassInstance) && ScriptTextBridge.BridgedBase(paramType) is not null) return true;

        if (paramType.IsAssignableFrom(argType))
            return true;

        // A NUMERIC argument is accepted for an ENUM parameter. A plain number is how a script writes a .NET
        // or COM enum, and the coercion arm turns it into the enum's value, so without this a call that names
        // the enum's own spelling ran while the same call with the number reported that no such member existed
        // (measured: `"ab".Equals("ab", 0)` -> "Method 'Equals' not found on type string with 2 argument(s)",
        // because the only two-argument instance overload takes a StringComparison). The script-side twin of
        // this rule is ArgMatchDistance, which ranks a number LOW against an enum so an exact match still wins;
        // the scoring below keeps that ordering here too.
        if ((Nullable.GetUnderlyingType(paramType) ?? paramType).IsEnum
            && IsIntegerType(Nullable.GetUnderlyingType(argType) ?? argType))
            return true;

        // A STRING argument is accepted for a `char` parameter, a one-character string being the script's own
        // spelling of a character (the coercion arm takes exactly that and refuses a longer one). Selection has to
        // accept it for the same reason as the enum above: measured, `"x".PadLeft(3, "0")` reported that no such
        // member existed while the coercion was already written to convert precisely that argument.
        if (paramType == typeof(char) && argType == typeof(string))
            return true;

        // ScriptFunction is compatible with any delegate type, CoerceArgument handles wrapping
        if (typeof(Delegate).IsAssignableFrom(paramType) && (argType == typeof(ScriptFunction) || typeof(ScriptFunction).IsAssignableFrom(argType)))
            return true;

        if ((argType == typeof(double) || argType == typeof(float)) && IsIntegerType(paramType))
            return false;

        if (paramType == typeof(char) && argType != typeof(char) && argType != typeof(string))
            return false;

        if (paramType == typeof(bool) && argType != typeof(bool))
            return false;

        var underlyingParam = Nullable.GetUnderlyingType(paramType) ?? paramType;
        var underlyingArg = Nullable.GetUnderlyingType(argType) ?? argType;

        if (underlyingParam.IsPrimitive && underlyingArg.IsPrimitive)
        {
            // Allow widening conversions (int->long, float->double) and identity
            if (underlyingParam == underlyingArg) return true;
            return underlyingParam.IsAssignableFrom(underlyingArg) || true;
            // Narrowing allowed but scored lower via ConversionDistance
        }

        if (underlyingParam == typeof(string) || underlyingArg == typeof(string))
            return underlyingParam == typeof(string) && (underlyingArg == typeof(string) || underlyingArg == typeof(object));

        try
        {
            _ = Convert.ChangeType(Activator.CreateInstance(underlyingArg), underlyingParam);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? CoerceArgument(object? arg, Type targetType, int line, int column)
    {
        // A STRUCT IS A VALUE TYPE and C# BOXES it whenever it is handed to something taking an `object` or an
        // interface, which COPIES. A struct reaching this funnel is being boxed (a script struct is not a CLR type,
        // so it can never be a CLR parameter's declared type), and the early `IsAssignableFrom` return below is
        // exactly where the copy was missing: `object.IsAssignableFrom(StructInstance)` is true, so the argument
        // went through untouched. Measured: `list.Add(a); list[0].X = 42;` answered `a.X=42`, i.e. the element held
        // the SAME instance as the variable.
        if (arg is StructInstance siArg)
            arg = CloneStructForStore(siArg);

        if (arg == null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                RuntimeError<object?>(NullNotAssignableText, line, column);
            return null;
        }
        // A SCRIPT CLASS EXTENDING A BRIDGED CLR BASE arrives here as an instance and is handed over as its FACE
        // (a `TextWriter` sink is the case this exists for), before the plain assignability test, which can never match.
        if (arg is ClassInstance bridgeArg && ScriptTextBridge.FacadeFor(bridgeArg, targetType) is { } bridgeFace)
            return bridgeFace;

        var argType = arg.GetType();
        if (targetType.IsAssignableFrom(argType))
            return arg;

        // ScriptFunction → Delegate wrapping (supports Action<T…>, Func<T…,R>)
        if (typeof(Delegate).IsAssignableFrom(targetType) && arg is ScriptFunction sf)
            return AsDelegate(sf, targetType, line, column);

        try
        {
            var underlyingTarget = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (underlyingTarget == typeof(char) && arg is string { Length: 1 } s)
                return s[0];

            if (underlyingTarget.IsPrimitive && argType.IsPrimitive)
                return Convert.ChangeType(arg, underlyingTarget);

            // An ENUM target takes the script's own spelling of its values: a whole number IS an enum value, which
            // is how a script writes one wherever a .NET or COM member asks for it (measured: `"ab".Equals("AB",
            // 4)` is StringComparison.OrdinalIgnoreCase, and 0 is Ordinal). The method SELECTION already accepts an
            // integer argument for an enum parameter (IsTypeCompatibleForInvoke), so this is the other half of that
            // one rule; without it the selection found the member and then the coercion refused with the runtime's
            // own "Invalid cast from 'System.Int32' to 'System.StringComparison'".
            if (underlyingTarget.IsEnum && IsIntegerType(argType))
                return Enum.ToObject(underlyingTarget, arg);

            if (underlyingTarget == typeof(string))
                return arg.ToString();

            return Convert.ChangeType(arg, underlyingTarget);
        }
        catch (Exception ex)
        {
            RuntimeError<object?>($"Cannot convert argument of type '{argType.Name}' to '{targetType.Name}': {FriendlyExceptionMessage(ex)}", line, column);
            return null;
        }
    }

    /// <summary>Wraps a ScriptFunction as a .NET delegate of the given type using pre-compiled adapter types.</summary>
    [UnconditionalSuppressMessage("TrimAnalysis", "IL2077", Justification = "Adapter types are preserved in AotHints.cs via [DynamicDependency]")]
    private static Delegate AsDelegate(ScriptFunction fn, Type delegateType, int line, int col)
    {
        var invokeMethod = delegateType.GetMethod("Invoke")!;
        var paramTypes = invokeMethod.GetParameters();
        var returnType = invokeMethod.ReturnType;
        var hasReturn = returnType != typeof(void);

        // Determine the adapter generic type definition
        Type adapterDef;
        Type[] typeArgs;
        if (!hasReturn)
        {
            (adapterDef, typeArgs) = paramTypes.Length switch
            {
                0 => (typeof(ActionAdapter0), Type.EmptyTypes),
                1 => (typeof(ActionAdapter1<>), [paramTypes[0].ParameterType]),
                2 => (typeof(ActionAdapter2<,>), [paramTypes[0].ParameterType, paramTypes[1].ParameterType]),
                3 => (typeof(ActionAdapter3<,,>), new[] { paramTypes[0].ParameterType, paramTypes[1].ParameterType, paramTypes[2].ParameterType }),
                _ => throw new ScriptException($"ScriptFunction→delegate wrapping supports up to 3 parameters (Action). Got {paramTypes.Length}.")
            };
        }
        else
        {
            (adapterDef, typeArgs) = paramTypes.Length switch
            {
                0 => (typeof(FuncAdapter0<>), [returnType]),
                1 => (typeof(FuncAdapter1<,>), [paramTypes[0].ParameterType, returnType]),
                2 => (typeof(FuncAdapter2<,,>), [paramTypes[0].ParameterType, paramTypes[1].ParameterType, returnType]),
                3 => (typeof(FuncAdapter3<,,,>), new[] { paramTypes[0].ParameterType, paramTypes[1].ParameterType, paramTypes[2].ParameterType, returnType }),
                _ => throw new ScriptException($"ScriptFunction→delegate wrapping supports up to 3 parameters (Func). Got {paramTypes.Length}.")
            };
        }

        var itp = Current ?? throw new ScriptException("No active interpreter for delegate wrapping.");
        var adapterType = adapterDef.MakeGenericType(typeArgs);
        var adapter = Activator.CreateInstance(adapterType, fn, itp, line, col)
                      ?? throw new ScriptException($"Failed to create adapter for delegate type '{delegateType.Name}'.");
        var invoke = adapter.GetType().GetMethod("Invoke")!;
        return Delegate.CreateDelegate(delegateType, adapter, invoke);
    }

    public object? VisitConditionalExpr(ConditionalExpr node) => IsTruthy(Evaluate(node.Condition)) ? Evaluate(node.ThenExpr) : Evaluate(node.ElseExpr);

    public object VisitTupleExpr(TupleExpr node)
    {
        var items = new object?[node.Items.Count];
        for (var i = 0; i < node.Items.Count; i++)
        {
            var v = Evaluate(node.Items[i]);
            if (ReferenceEquals(v, YieldSentinel)) return YieldSentinel;
            items[i] = v;
        }
        return new ScriptTuple(items);
    }

    public object? VisitNullCoalescingExpr(NullCoalescingExpr node)
    {
        var left = Evaluate(node.Left);
        if (ReferenceEquals(left, YieldSentinel))
            return YieldSentinel;

        if (left != null)
            return left;

        return Evaluate(node.Right);
    }

    public object VisitNewArrayExpr(NewArrayExpr node) => CreateArray(node.ElementType, node.Sizes, node.Initializer, node.Line, node.Column);

    public object? VisitArrayAccessExpr(ArrayAccessExpr node)
    {
        var target = Evaluate(node.Array); if (ReferenceEquals(target, YieldSentinel))
            return YieldSentinel; return GetArrayElement(target, node.Indices, node.Line, node.Column);
    }

    public object? VisitArrayAssignExpr(ArrayAssignExpr node)
    {
        var target = Evaluate(node.Array);
        if (ReferenceEquals(target, YieldSentinel))
            return YieldSentinel; var value = Evaluate(node.Value);
        if (ReferenceEquals(value, YieldSentinel))
            return YieldSentinel; SetArrayElement(target, node.Indices, value, node.Line, node.Column);
        return value;
    }

    public object? VisitRefAssignExpr(RefAssignExpr node)
    {
        // `F(x) = 7;` on a ref-RETURNING call. The callee answers the REFERENCE (its call node carries WantsRefResult),
        // and the write goes through it, so the storage written is whatever the callee answered a reference to.
        var target = Evaluate(node.CellSource);
        if (ReferenceEquals(target, YieldSentinel))
            return YieldSentinel;
        var value = Evaluate(node.Value);
        if (ReferenceEquals(value, YieldSentinel))
            return YieldSentinel;
        return AssignThroughRefCell(target, value, node.Line, node.Column);
    }

    public object? VisitLogicalExpr(LogicalExpr node)
    {
        var left = Evaluate(node.Left);
        if (ReferenceEquals(left, YieldSentinel))
            return YieldSentinel;
        var isLeftTruthy = IsTruthy(left);
        if (node.Operator.Type == TokenType.OrOr)
        {
            if (isLeftTruthy)
                return left;
        }
        else
        {
            if (!isLeftTruthy)
                return left;
        }
        return Evaluate(node.Right);
    }

    public object? VisitCompoundAssignExpr(CompoundAssignExpr node) => EvalCompoundAssign(node);

    public object? VisitIncrementDecrementExpr(IncrementDecrementExpr node) => EvalIncDec(node);

    public object VisitListLiteralExpr(ListLiteralExpr node)
    {
        // A list literal [a, b, c] creates a native array T[]
        if (node.Elements.Count == 0)
            return Array.Empty<object>();
        return CreateArray(node.ElementType, null, node.Elements, node.Line, node.Column);
    }

    /// <summary>True when a member's body is nothing but a returned list literal, the one shape whose value
    /// the DECLARED return type governs (C# collection expressions). A literal that reaches a `var` first
    /// loses the expectation, exactly as in C#.</summary>
    private static bool IsDirectLiteralReturn(FuncDecl member) =>
        member.Body.Count == 1 && member.Body[0] is ReturnStmt { Value: ListLiteralExpr };

    /// <summary>Rebuilds a value a `=> [...]` body produced as the container its return type DECLARES: the
    /// declared type decides both the KIND and the element type, so `List<T> Create() => []` hands back a
    /// List<int> rather than an empty object[], and `T[] None() => []` an int[]. When the declared type is not
    /// a container we can build, the value comes back untouched.</summary>
    /// <summary>The element type a DECLARED container wants its items to be, or null when the declared type is
    /// not one of the containers a collection expression can fill. ONE statement of the rule, because three
    /// places ask it: a `=> [ ... ]` body (TargetTypedLiteralResult), a declaration's initializer
    /// (VisitVarDeclStmt) and the compiled declaration (the Compiler's VarDeclStmt arm). C# 12 collection
    /// expressions are target-typed, so `int?[] a = [1, null, 3]` is legal while `int[] a = [1, null, 3]` is
    /// not: the elements convert to the DECLARED element type, and it is the declaration that decides.</summary>
    internal static TypeInfo? DeclaredContainerElementType(TypeInfo? declared)
    {
        if (declared == null) return null;
        if (declared.IsArray)
            return declared.JaggedDepth > 0
                ? declared with { JaggedDepth = declared.JaggedDepth - 1 }
                : declared with { ArrayRank = 0 };

        var name = declared.TypeName;
        if (name == null) return null;
        if (!name.Equals("List", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("IScriptList", StringComparison.OrdinalIgnoreCase)) return null;
        return declared.GenericArgs is { Count: > 0 } args
            ? args[0]
            : new TypeInfo(TokenType.Object, false, 0, 0, null);
    }

    private object? TargetTypedLiteralResult(TypeInfo declared, object? value, int line, int column)
    {
        if (value is not Array source) return value;
        if (declared.IsArray)
        {
            var elementType = DeclaredContainerElementType(declared)!;
            return CreateArrayDirect(elementType, [source.Length], [.. source.Cast<object?>()], line, column);
        }

        var name = declared.TypeName;
        if (name == null || (!name.Equals("List", StringComparison.OrdinalIgnoreCase)
                             && !name.Equals("IScriptList", StringComparison.OrdinalIgnoreCase))) return value;

        var element = DeclaredContainerElementType(declared)!;
        var list = CreateScriptList(element);
        foreach (var item in source) list.Add(item, line, column);
        return list;
    }

    public object VisitDictLiteralExpr(DictLiteralExpr node)
    {
        var dict = new Dictionary<object, object?>();
        foreach (var (keyExpr, valueExpr) in node.Entries)
        {
            var key = Evaluate(keyExpr);
            if (ReferenceEquals(key, YieldSentinel))
                return YieldSentinel;
            var value = Evaluate(valueExpr);
            if (ReferenceEquals(value, YieldSentinel))
                return YieldSentinel;
            dict[key!] = value;
        }
        return dict;
    }

    public object VisitComprehensionExpr(ComprehensionExpr node)
    {
        // Evaluate the collection
        var collection = Evaluate(node.Collection);
        if (ReferenceEquals(collection, YieldSentinel))
            return YieldSentinel;

        // Build result list from comprehension
        var results = new List<object?>();

        switch (collection)
        {
            // Iterate based on collection type
            case Array arr:
                {
                    for (var i = 0; i < arr.Length; i++)
                        ProcessElement(arr.GetValue(i));
                    break;
                }

            case IScriptList list:
                {
                    for (var i = 0; i < list.Count; i++)
                        ProcessElement(list[i]);
                    break;
                }

            case string str:
                {
                    foreach (var t in str)
                        ProcessElement(t);
                    break;
                }

            case IEnumerable enumerable:
                {
                    foreach (var item in enumerable)
                        ProcessElement(item);
                    break;
                }

            default:
                RuntimeError($"Cannot iterate over type '{collection?.GetType().Name ?? "null"}'.", node.Line, node.Column);
                break;
        }

        // Convert results to object[]
        var resultArray = new object?[results.Count];
        for (var i = 0; i < results.Count; i++)
            resultArray[i] = results[i];
        return resultArray;

        void ProcessElement(object? elem)
        {
            CurrentFrame.Scopes.PushScope();
            try
            {
                CurrentFrame.Scopes.Define(node.IterVar, elem);
                if (node.Filter != null)
                {
                    var cond = Evaluate(node.Filter);
                    if (ReferenceEquals(cond, YieldSentinel))
                        return;
                    if (!IsTruthy(cond))
                        return;
                }

                var val = Evaluate(node.ElementExpr);
                if (!ReferenceEquals(val, YieldSentinel))
                    results.Add(val);
            }
            finally
            {
                CurrentFrame.Scopes.PopScope();
            }
        }
    }

    public object? VisitMatchExpr(MatchExpr node)
    {
        var subject = Evaluate(node.Subject);
        if (ReferenceEquals(subject, YieldSentinel))
            return YieldSentinel;

        foreach (var arm in node.Arms)
        {
            if (!PatternMatches(arm.Pattern, subject, out var binds)) continue;
            CurrentFrame.Scopes.PushScope();
            try
            {
                foreach (var (name, value) in binds)
                    CurrentFrame.Scopes.Define(name, value, node.Line, node.Column);
                if (arm.Guard != null && !IsTruthy(Evaluate(arm.Guard))) continue;
                return Evaluate(arm.Value);
            }
            finally { CurrentFrame.Scopes.PopScope(); }
        }

        // Should not reach here if last arm is a wildcard (validated by parser)
        RuntimeError("No matching arm in match expression.", node.Line, node.Column);
        return null;
    }

    public object? VisitTuplePatternExpr(TuplePatternExpr node)
    {
        RuntimeError("Tuple pattern is only valid in match arms.", node.Line, node.Column);
        return null;
    }

    public object VisitIndexExpr(IndexExpr node)
    {
        var val = Evaluate(node.Value);
        if (ReferenceEquals(val, YieldSentinel))
            return YieldSentinel;
        var intVal = Convert.ToInt32(val);
        return new Index(intVal, node.FromEnd);
    }

    public object VisitRangeExpr(RangeExpr node)
    {
        // Evaluate From (default 0 if open)
        var fromVal = node.From != null ? Evaluate(node.From) : null;
        if (ReferenceEquals(fromVal, YieldSentinel))
            return YieldSentinel;
        // Evaluate To (default 0 if omitted, produces empty range)
        var toVal = node.To != null ? Evaluate(node.To) : null;
        if (ReferenceEquals(toVal, YieldSentinel))
            return YieldSentinel;

        var from = Convert.ToInt64(fromVal ?? 0);
        var to = node.To != null ? Convert.ToInt64(toVal!) : from - 1; // empty range if no To

        if (from > to)
            RuntimeError($"Invalid range {from}..{to}: start must be <= end.", node.Line, node.Column);

        if (to - from >= 1000000)
            RuntimeError($"Range {from}..{to} is too large (max 1,000,000 elements).", node.Line, node.Column);

        var count = (int)(to - from + 1);
        var result = new long[count];
        for (var i = 0; i < count; i++)
            result[i] = from + i;
        return result;
    }

    public object? VisitSwitchExpr(SwitchExpr node)
    {
        var subject = Evaluate(node.Subject);
        if (ReferenceEquals(subject, YieldSentinel))
            return YieldSentinel;

        foreach (var arm in node.Arms)
        {
            if (!PatternMatches(arm.Pattern, subject, out var bindings))
                continue;

            if (arm.Guard != null)
            {
                CurrentFrame.Scopes.PushScope();
                try
                {
                    foreach (var (name, value) in bindings)
                        CurrentFrame.Scopes.Define(name, value);

                    if (!IsTruthy(Evaluate(arm.Guard)))
                    {
                        CurrentFrame.Scopes.PopScope();
                        continue;
                    }
                }
                catch
                {
                    CurrentFrame.Scopes.PopScope();
                    throw;
                }
            }

            // Body evaluation, define bindings in a fresh scope
            CurrentFrame.Scopes.PushScope();
            try
            {
                foreach (var (name, value) in bindings)
                    CurrentFrame.Scopes.Define(name, value);

                // If guard already pushed bindings, pop them first
                if (arm.Guard != null)
                    CurrentFrame.Scopes.PopScope();
                return Evaluate(arm.Value);
            }
            finally { CurrentFrame.Scopes.PopScope(); }
        }

        RuntimeError("No matching arm in switch expression.", node.Line, node.Column);
        return null;
    }

    internal bool PatternMatches(Pattern pattern, object? value, out List<(string Name, object? Value)> bindings)
    {
        bindings = [];
        switch (pattern)
        {
            case ConstantPattern cp:
                // AreEqual, the engine's value-aware equality (numeric widening AND enums), exactly
                // what the VM's MATCH_EQ uses. Plain object.Equals dropped enum constants, so
                // `HttpStatus.NotFound match { 404 => "Not Found" }` never matched on the tree.
                return AreEqual(value, Evaluate(cp.Expr));

            case TypePattern tp:
                var isMatch = IsValueCompatibleWithType(value, tp.Type);
                if (isMatch && tp.BindingName != null)
                    bindings.Add((tp.BindingName, value));
                return isMatch;

            case VarPattern vp:
                bindings.Add((vp.BindingName, value));
                return true;

            case DiscardPattern:
                return true;

            case TupleArmPattern tap:
                {
                    if (PosCount(value) != tap.Elements.Count) return false;
                    if (!MatchPositional(value, tap.Elements, out var posBinds)) return false;
                    for (var i = 0; i < tap.Elements.Count; i++)
                    {
                        var name = tap.Elements[i].Name;
                        if (name != null) bindings.Add((name, posBinds[i]));
                    }
                    return true;
                }

            case GroupPattern gp:
                return PatternMatches(gp.Inner, value, out bindings);

            case ListPattern lp:
                {
                    var count = PosCount(value);
                    if (count < 0) return false;
                    var fixedCount = lp.Elements.Count;
                    if (lp.Slice == null ? count != fixedCount : count < fixedCount) return false;
                    var sliceIndex = lp.Slice?.Index ?? fixedCount;
                    var afterCount = fixedCount - sliceIndex;

                    for (var i = 0; i < sliceIndex; i++)
                    {
                        if (!PatternMatches(lp.Elements[i], PosItem(value, i), out var beforeBinds)) return false;
                        bindings.AddRange(beforeBinds);
                    }
                    for (var j = 0; j < afterCount; j++)
                    {
                        if (!PatternMatches(lp.Elements[sliceIndex + j], PosItem(value, count - afterCount + j), out var afterBinds)) return false;
                        bindings.AddRange(afterBinds);
                    }
                    if (lp.Slice is { CaptureName: { } captureName })
                        bindings.Add((captureName, BuildSliceCapture(value, sliceIndex, count - fixedCount)));
                    return true;
                }

            case PropertyPattern pp:
                foreach (var (memberName, memberPattern) in pp.Members)
                {
                    if (!TryGetPatternMember(value, memberName, out var memberValue)) return false;
                    if (!PatternMatches(memberPattern, memberValue, out var memberBinds)) return false;
                    bindings.AddRange(memberBinds);
                }
                return true;

            case LogicalPattern lp:
                switch (lp.Kind)
                {
                    case LogicalKind.Not:
                        // A negated sub-pattern contributes no bindings.
                        bindings = [];
                        return !PatternMatches(lp.Left, value, out _);
                    case LogicalKind.And:
                        {
                            if (!PatternMatches(lp.Left, value, out var leftBinds)) return false;
                            if (!PatternMatches(lp.Right!, value, out var rightBinds)) return false;
                            bindings = [.. leftBinds, .. rightBinds];
                            return true;
                        }
                    default:   // Or, both sides bind the same names (validated at parse time)
                        if (PatternMatches(lp.Left, value, out var orBinds)) { bindings = orBinds; return true; }
                        return PatternMatches(lp.Right!, value, out bindings);
                }

            case RelationalPattern rp:
                if (value is not IComparable c)
                    return false;
                var rightVal = Evaluate(rp.Right);
                var cmp = c.CompareTo(Convert.ChangeType(rightVal, value.GetType()));
                return rp.Op switch
                {
                    TokenType.Less => cmp < 0,
                    TokenType.Greater => cmp > 0,
                    TokenType.LessEqual => cmp <= 0,
                    TokenType.GreaterEqual => cmp >= 0,
                    _ => false
                };

            default:
                return false;
        }
    }

    public object VisitWithExpr(WithExpr node)
    {
        var baseVal = Evaluate(node.Base);
        if (ReferenceEquals(baseVal, YieldSentinel))
            return YieldSentinel;

        switch (baseVal)
        {
            case StructInstance:
            case ClassInstance:
                // Evaluate all field values first (yield checks preserved), then apply via the
                // shared values-applicator used by the VM direct path.
                var pairs = new (string, object?)[node.Fields.Count];
                for (var i = 0; i < node.Fields.Count; i++)
                {
                    var (fn, fv) = node.Fields[i];
                    var v = Evaluate(fv);
                    if (ReferenceEquals(v, YieldSentinel)) return YieldSentinel;
                    pairs[i] = (fn, v);
                }
                return ApplyWithValues(baseVal, pairs, node.Line, node.Column);

            default:
                RuntimeError("'with' expression requires a struct or class instance.", node.Line, node.Column);
                return null;
        }
    }
}
