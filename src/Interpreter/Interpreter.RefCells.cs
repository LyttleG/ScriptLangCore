namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>True when two declarations would bind the SAME call: the same parameter count and, for each
    /// parameter, the same declared type name and the same ref flag. A REdeclaration (what a REPL does when a
    /// line is retyped, and what a re-run of an edited file does) answers true; an OVERLOAD answers false. ONE
    /// statement of "same signature", so the warning at the declaration site and a future overload set cannot
    /// disagree about what counts as a duplicate.</summary>
    internal static bool SameSignature(FuncDecl a, FuncDecl b)
    {
        if (a.Params.Count != b.Params.Count) return false;
        for (var i = 0; i < a.Params.Count; i++)
        {
            var an = a.Params[i].Type.TypeName ?? a.Params[i].Type.BaseType.ToString();
            var bn = b.Params[i].Type.TypeName ?? b.Params[i].Type.BaseType.ToString();
            if (!string.Equals(an, bn, StringComparison.Ordinal)) return false;
            var aRef = a.IsRefParams != null && i < a.IsRefParams.Count && a.IsRefParams[i];
            var bRef = b.IsRefParams != null && i < b.IsRefParams.Count && b.IsRefParams[i];
            if (aRef != bRef) return false;
        }
        return true;
    }

    /// <summary>True when two declarations are the SAME signature except that a by-reference position names the OTHER
    /// keyword: `P(ref int)` beside `P(out int)`. C# makes `ref` and `out` part of the signature and still refuses that
    /// pair (a call could not tell the two apart), so this engine refuses it too, rather than accepting both and sending
    /// EVERY call to whichever declaration came FIRST. Measured before this rule: `void P(ref int v)` then
    /// `void P(out int v)` was accepted, `P(ref q)` answered `q=1` and `P(out q)` was refused with "must be passed with
    /// the 'ref' keyword"; reversing the two declarations swapped which call worked. A pair that differs at a BY-VALUE
    /// position is an ordinary overload and is left alone (`F(int)` beside `F(ref int)`, which `vm_params` 7a to 7d pin).
    /// </summary>
    internal static bool DiffersOnlyByRefOutKeyword(FuncDecl a, FuncDecl b)
    {
        if (a.Params.Count != b.Params.Count) return false;
        var keywordDiffers = false;
        for (var i = 0; i < a.Params.Count; i++)
        {
            var an = a.Params[i].Type.TypeName ?? a.Params[i].Type.BaseType.ToString();
            var bn = b.Params[i].Type.TypeName ?? b.Params[i].Type.BaseType.ToString();
            if (!string.Equals(an, bn, StringComparison.Ordinal)) return false;
            if (ScriptOverloadSet.IsByRefPosition(a, i) != ScriptOverloadSet.IsByRefPosition(b, i)) return false;
            if (ScriptOverloadSet.IsByRefPosition(a, i) && IsOutPos(a, i) != IsOutPos(b, i)) keywordDiffers = true;
        }
        return keywordDiffers;
    }

    private static bool IsOutPos(FuncDecl d, int i)
        => d.IsOutParams != null && i < d.IsOutParams.Count && d.IsOutParams[i];

    /// <summary>Refuse a declaration whose only difference from one already registered is the `ref`/`out` keyword.</summary>
    internal void RefuseRefOutOnlyOverload(string name, FuncDecl decl, FuncDecl prior, string where, int line, int column)
    {
        if (!DiffersOnlyByRefOutKeyword(prior, decl)) return;
        RuntimeError($"'{name}' cannot declare both a 'ref' and an 'out' overload for the same parameters{where}: in C# they are ONE signature, because a call cannot tell the two apart, so the second declaration would never be reached. Give one of them a different name or different parameter types, or pass the value by value.", line, column);
    }

    private RefCell CaptureVariableReference(VariableExpr varExpr, int line, int column, bool allowUndefined = false, bool? byOut = null)
    {
        var name = varExpr.Name;
        foreach (var (scope, constSet) in CurrentFrame.Scopes.GetScopesWithConstLive())
        {
            if (!scope.TryGetValue(name, out var existing)) continue;
            if (existing.Value is RefCell existingRc)
                return existingRc;

            return new RefCell(
                Getter: () => scope[name].Value,
                Setter: value =>
                {
                    if (constSet.Contains(name))
                        RuntimeError($"Cannot assign to constant variable '{name}'.", line, column);
                    scope[name] = new ScopeStack.VariableInfo(value!, line, column);
                },
                ByOut: byOut
            );
        }
        // NOT a local of THIS frame, a captured variable or a global reachable from here. A `ref` argument
        // naming one used to fail with "Undefined variable 'x'" while a READ of the same name, and a plain
        // `x = …` on the very next line, both resolved it: the walk above stops at the current frame's scopes,
        // and a nested function or a lambda keeps its outer/global names elsewhere (the closure snapshot's
        // scopes, and the live global).
        //
        // Which of those can back a WRITE is the whole question, so it is asked explicitly rather than inferred
        // from "does the name resolve":
        //   * a global, the closure snapshot copies it (reads prefer that copy) and `Set` is what knows a write
        //     must reach the writable global AND that copy, so the cell goes through `TryGet`/`Set`, the ONE
        //     read and the ONE write path, instead of re-deriving where a name lives;
        //   * a captured local the closure PROMOTED to a RefCell, the cell itself is the live storage;
        //   * a captured local the snapshot kept as a plain VALUE, a copy. A cell over it would swallow the
        //     write, so this is refused with the reason, the way the extension registry refuses a by-ref
        //     parameter: there is no caller variable here to write back to.
        if (CurrentFrame.Scopes.ParentSnapshot is { } parent)
        {
            foreach (var localScope in parent.LocalScopes)
            {
                if (!localScope.TryGetValue(name, out var captured)) continue;
                if (captured is RefCell capturedCell) return capturedCell;

                RuntimeError(
                    $"Cannot pass '{name}' by reference from here: this frame holds a snapshot COPY of it "
                    + "(its own function cannot write the caller's variable back), so pass the value and "
                    + "return the result instead.", line, column);
                return null;
            }

            if (parent.GlobalScope.ContainsKey(name))
            {
                var frameScopes = CurrentFrame.Scopes;
                return new RefCell(
                    Getter: () => frameScopes.TryGet(name, out var current) ? current : null,
                    Setter: value => frameScopes.Set(name, value, line, column),
                    ByOut: byOut);
            }
        }

        if (allowUndefined)
        {
            // An `out` argument for a name that exists NOWHERE is defined HERE. Deliberately AFTER the
            // reachability test above: an `out` to an outer or global variable that already exists is a WRITE to
            // it, and defining a local of the same name instead, which is what this branch did while it came
            // first, made the callee's value vanish at scope exit (measured: `Set7(out g)` inside a lambda left
            // the global at 0, silently, the same nothing as the `ref` case above).
            var scope = CurrentFrame.Scopes.GetCurrentScopeDictionary();
            return new RefCell(
                Getter: () => scope[name].Value,
                Setter: value =>
                {
                    scope[name] = new ScopeStack.VariableInfo(value!, line, column);
                },
                ByOut: byOut
            );
        }

        RuntimeError($"Undefined variable '{name}'.", line, column);
        return null;
    }

    // ── Safe read a RefCell's current value, returning null on failure ──
    private static object? SafeGet(RefCell rc)
    {
        try { return rc.Get(); }
        catch { return null; }
    }

    /// <summary>True when the value a `ref`/`out` ARGUMENT's cell already holds can stand in for the declared parameter
    /// type WITHOUT a conversion. The predicate is the engine's own (`IsKnownCompatibleType`, the one a by-value argument
    /// is judged by), and what differs is what happens when it answers no: a by-value argument is COERCED (silently for
    /// a numeric widening, with a warning otherwise), while a reference cannot be, because the cell addresses the
    /// CALLER's variable and a conversion would retype it without the caller asking. Measured before this rule:
    /// `void P(ref int v)` called as `ref s` where `s` held "abc" gave the callee the string, then left `s` holding 1
    /// with `typeof(s)` "int", and nothing was reported on either engine.
    /// `null` is never judged: a script declares an `out` target that way (`var a;`), so there is no type to compare, and
    /// a numeric FAMILY is compatible by that same predicate, which is what keeps `ref double` accepting an `int`
    /// variable (measured: `crash_test` relies on it).</summary>
    private bool RefValueFitsDeclared(object? value, TypeInfo type)
        // A NAME THE ENGINE DOES NOT KNOW IS A TYPE PARAMETER, and a type parameter imposes no runtime check this can
        // make: a LOADED generic method's `ref T` arrives with `TypeName = "T"` (the wire carries the TEMPLATE), so
        // comparing the value's CLR name against it refused a call the file accepts, where the tree's copy is already
        // instantiated (`T` is `int`). Measured on crash_out_ref_stress from a bundle: 14 of 14 checks passed and the
        // run then died with "Argument for parameter 'val' was passed by ref but its variable holds Int32, and 'val' is
        // declared T". The rule is `ValidateOutAssignment`'s, which states it for an `out` position.
        => value == null
            || (type.TypeName is { Length: > 0 } declaredRefName && !IsKnownTypeName(declaredRefName))
            || IsKnownCompatibleType(value, type);

    /// <summary>The rule for a FIXED parameter, in ONE place: the ARGUMENT and the DECLARATION must agree about
    /// `ref`/`out`, and C# refuses every disagreement rather than passing a copy (CS1615 for a keyword the parameter
    /// does not declare, CS1620 for a keyword the CALL omitted, which this method gains below). Shared by BindParam
    /// (every tree path) and the VM's own argument normalizer, so the two engines cannot disagree about the rule or
    /// the message, and so a cell never reaches CoerceTo, which would report the internal type name `RefCell`.
    /// The OMITTED-keyword half is measured: `int Inc(ref int x) { x = x + 1; return x; }` called as `Inc(a)`
    /// returned 2 and left `a` at 1 (a SILENT COPY, the defect this whole rule argues against), while a compiled body
    /// BAILED instead, so a bundle reported an engine-gap message rather than a refusal.</summary>
    internal static void RefuseRefCellOnByValueParam(string name, object? arg, bool declaredRef, bool declaredOut, int line, int col, TypeInfo? declared = null)
    {
        if (arg is not RefCell cell)
        {
            // The call passed the argument BY VALUE where the parameter demands a keyword. The ctor paths pass
            // false/false deliberately (a constructor's ref-ness is not threaded), so this arm cannot fire there.
            // The wording has to serve all three shapes the bind can see, because it cannot tell them apart: a VARIABLE
            // read by value (pass it with the keyword), a literal (no keyword can make it a reference) and a FIELD
            // (this engine passes only variables by reference). Measured: before this arm the tree bound a silent COPY
            // for the variable case (`Inc(a)` returned 2 and left `a` at 1) and a compiled body BAILED instead, so a
            // bundle reported an engine-gap message.
            if (declaredRef || declaredOut)
                RuntimeError($"Argument for parameter '{name}' cannot be passed by value: '{name}' is declared {(declaredRef ? "ref" : "out")}, so the call must pass a VARIABLE with the '{(declaredRef ? "ref" : "out")}' keyword (a literal, a field or an element cannot be passed by reference here).", line, col);
            return;
        }
        if (!declaredRef && !declaredOut)
            RuntimeError($"Argument for parameter '{name}' was passed by ref or out, but '{name}' is not declared ref or out: the callee would receive a copy, so a write inside it could never reach the caller. Drop the keyword at the call, or declare the parameter `ref {name}` so the callee can write back.", line, col);
        // The DECLARATION governs, and C# also makes the call site agree with it (CS1620: "must be passed with the
        // '{wanted}' keyword"), so the two keywords cannot be swapped. Only a cell that KNOWS which keyword built it
        // can be judged: a cell the engine built for its own reasons carries none (ByOut is null) and is left alone.
        if (cell.ByOut == true && !declaredOut)
            RuntimeError($"Argument for parameter '{name}' must be passed with the 'ref' keyword: '{name}' is declared ref, and the call passed it by out. Write `ref {name}` at the call, or declare the parameter out.", line, col);
        if (cell.ByOut == false && !declaredRef)
            RuntimeError($"Argument for parameter '{name}' must be passed with the 'out' keyword: '{name}' is declared out, and the call passed it by ref. Write `out {name}` at the call, or declare the parameter ref.", line, col);
        // AND THE VALUE MUST ALREADY BE THE DECLARED TYPE, because a reference is never converted: the cell writes into
        // the CALLER's variable, so a conversion here would retype it silently. Asked only when the caller has the
        // declaration in hand (`declared`), which every bind path does; a cell already accepted above is judged now.
        if (declared != null && Current is { } owner)
        {
            var held = SafeGet(cell);
            if (!owner.RefValueFitsDeclared(held, declared))
                RuntimeError($"Argument for parameter '{name}' was passed by {(cell.ByOut == true ? "out" : "ref")} but its variable holds {(held == null ? "null" : held.GetType().Name)}, and '{name}' is declared {DeclaredTypeLabel(declared)}: a reference is never converted, so pass a variable that already holds that type, or declare the parameter 'ref object'.", line, col);
        }
    }

    // ── DRY helper: initialize out-param assignment tracking ──
    // ── Bridges for Vm.cs: RuntimeError and ValidateOutAssignment are private to this class, and the
    //    VM's flat-call out-param tracking needs both so the flat path raises byte-identical errors. ──
    /// <summary>Validate an out/ref assignment exactly as the tree's InitOutTracking does (coercion plus
    /// type-name compatibility, same message and location).</summary>
    internal object? ValidateOutAssignmentFromVm(object? value, TypeInfo targetType, string paramName, int line, int column)
        => ValidateOutAssignment(value, targetType, paramName, line, column);

    /// <summary>Shared out/ref assignment validation: coerce the value to the target type and, for named
    /// types, verify the runtime type name is compatible, throws on mismatch. Used by both the tree's
    /// InitOutTracking setter and the VM's out-param tracking wrapper so the semantics stay identical.</summary>
    private object? ValidateOutAssignment(object? value, TypeInfo targetType, string paramName, int line, int column)
    {
        var coerced = CoerceTo(value, targetType, line, column);
        // A NAME THE ENGINE DOES NOT KNOW IS A TYPE PARAMETER, and a type parameter imposes no runtime check this can
        // make: a LOADED generic method's `out T` arrives as `TypeName = "T"`, so comparing the value's CLR name against
        // it refused every assignment (`Type mismatch: cannot assign value of type 'Int32' to out/ref parameter 'val' of
        // type 'T'`, measured on crash_out_ref_stress from a bundle). `CoerceTo` above is the whole rule for such a
        // name, which is what the TREE does too: its call is generic-instantiated, so `T` is already `int` there.
        if (coerced != null && targetType.TypeName is { Length: > 0 } declaredOutName && IsKnownTypeName(declaredOutName))
        {
            var valTypeName = coerced.GetType().Name;
            var ok = valTypeName.Equals(targetType.TypeName, StringComparison.Ordinal) ||
                     valTypeName.StartsWith(targetType.TypeName + "`", StringComparison.Ordinal);
            if (!ok && coerced is ClassInstance ci)
                ok = ci.Class.Name.Equals(targetType.TypeName, StringComparison.Ordinal);
            if (!ok)
            {
                var valType = coerced.GetType();
                if (valType.IsGenericType)
                {
                    var genDef = valType.GetGenericTypeDefinition();
                    if (genDef == typeof(ScriptUnboxedList<>) || genDef == typeof(ScriptBoxedList))
                        ok = targetType.TypeName.Equals("List", StringComparison.Ordinal);
                    else if (genDef == typeof(ScriptUnboxedDictionary<,>) || genDef == typeof(ScriptBoxedDictionary))
                        ok = targetType.TypeName.Equals("Dictionary", StringComparison.Ordinal);
                    else if (genDef == typeof(UnboxedScriptHashSet<>) || genDef == typeof(BoxedScriptHashSet))
                        ok = targetType.TypeName.Equals("HashSet", StringComparison.Ordinal);
                }
                if (!ok && valTypeName.Equals("ScriptBoxedDictionary", StringComparison.Ordinal))
                    ok = targetType.TypeName.Equals("Dictionary", StringComparison.Ordinal);
                if (!ok && valTypeName.Equals("ScriptBoxedList", StringComparison.Ordinal))
                    ok = targetType.TypeName.Equals("List", StringComparison.Ordinal);
                if (!ok && valTypeName.Equals("BoxedScriptHashSet", StringComparison.Ordinal))
                    ok = targetType.TypeName.Equals("HashSet", StringComparison.Ordinal);
            }
            if (!ok)
                RuntimeError(
                    $"Type mismatch: cannot assign value of type '{valTypeName}' " +
                    $"to out/ref parameter '{paramName}' of type '{targetType.TypeName}'. " +
                    $"Expected a value compatible with '{targetType.TypeName}'.",
                    line, column, ErrorCode.TypeMismatch);
        }
        return coerced;
    }

    private bool[]? InitOutTracking(IReadOnlyList<bool>? isOutParams,
        IReadOnlyList<(TypeInfo Type, string Name)> @params, int line, int col)
    {
        if (isOutParams == null) return null;
        var arr = new bool[isOutParams.Count];
        for (var i = 0; i < isOutParams.Count; i++)
        {
            if (!isOutParams[i]) continue;
            var name = @params[i].Name;
            var targetType = @params[i].Type;
            var capture = i;
            if (CurrentFrame.Scopes.TryGet(name, out var existing) && existing is RefCell rc)
            {
                var originalSetter = rc.Setter;
                CurrentFrame.Scopes.Set(name,
                    new RefCell(rc.Getter, value =>
                    {
                        var coerced = ValidateOutAssignment(value, targetType, name, line, col);
                        // Warn when out parameter changes the variable's existing type
                        if (!arr[capture])
                        {
                            var existingTypeName = "";
                            try { existingTypeName = rc.Getter()?.GetType().Name ?? ""; }
                            catch
                            {
                                // ignored
                            }

                            if (existingTypeName.Length > 0)
                            {
                                var coercedTypeName = coerced?.GetType().Name ?? "null";
                                var targetDisplay = targetType.TypeName ??
                                    TypeSystem.Name(targetType.BaseType);
                                if (targetType.TypeName != null && existingTypeName != coercedTypeName && !string.Equals(TypeSystem.FromClrName(existingTypeName), targetDisplay, StringComparison.OrdinalIgnoreCase))
                                {
                                    Logging.Log.Warning("Type mismatch",
                                        $"'out' parameter '{name}' expected '{targetDisplay}' but found '{existingTypeName}'",
                                        line, col);
                                    // expected 'String', found 'int'.
                                }
                            }
                        }

                        arr[capture] = true;
                        originalSetter(coerced);
                    }),
                    line, col);
            }
        }
        return arr;
    }

    // ── DRY helper: verify all out params were assigned ──

    /// <summary>The ONE wording for an unassigned `out` parameter, asked by all three refusal sites: this file's
    /// VerifyOutParams, its VM-direct entry for a compiled function, and the VM's flat-frame RETURN. It was TWO
    /// wordings, and the split was by RECEIVER rather than by rule: a free function and a CLASS METHOD answered
    /// "out parameter 'x' was not assigned." while a struct method and a constructor answered "The 'out' parameter
    /// 'x' must be assigned before the function returns.", so the same mistake read two ways. Measured on a
    /// five-shape matrix, then pinned by vm_call_parity section 9 and by crash_error_messages check 3 (which had been
    /// RED on the class-method shape since before the round that unified them, and whose own comment says it wants
    /// the message the tree produces).</summary>
    internal static string OutNotAssignedMessage(string name)
        => $"The 'out' parameter '{name}' must be assigned before the function returns.";

    private void VerifyOutParams(bool[]? outAssigned, IReadOnlyList<bool>? isOutParams,
        IReadOnlyList<(TypeInfo Type, string Name)> @params, int line, int col)
    {
        if (outAssigned == null || isOutParams == null) return;
        for (var i = 0; i < outAssigned.Length; i++)
        {
            if (isOutParams[i] && !outAssigned[i])
                RuntimeError(OutNotAssignedMessage(@params[i].Name), line, col);
        }
    }

    /// <summary>True when the ctor declares any ref or out params. The VM path passes raw argument
    /// values, so a body write to a ref/out param would not reach the caller's variable (the tree
    /// wraps them in RefCells via BindParam and writes back). Such ctors stay on the tree-walker.</summary>
    private static bool HasRefOrOutCtorParams(ConstructorDecl ctor)
    {
        if (ctor.IsRefParams.Any(isRef => isRef))
            return true;

        return ctor.IsOutParams != null && ctor.IsOutParams.Any(isOut => isOut);
    }

    /// <summary>Wraps each `out` position of a constructor's arguments in a TRACKING cell, so that a COMPILED body's
    /// write to that parameter marks it assigned, and answers the tracker (null when there is nothing to track, in
    /// which case the tree's own tracker answers). The live scope's tracker (InitOutTracking) cannot see such a
    /// write: a compiled body writes to its own SLOT, never to the scope NAME the tracker wrapped, which is what the
    /// class constructor path learned the hard way (a stripped bundle of a ctor that assigns its `out` parameter
    /// answered "The 'out' parameter 'c' must be assigned before the function returns" for an assignment it HAD
    /// made). The wrapper's shape is TryExecuteVmBody's, one implementation of the same idea.</summary>
    private bool[]? WrapCtorOutArgs(ConstructorDecl ctor, ref object?[] args, int line, int column)
        => WrapOutArgs(ctor.IsOutParams, ctor.Params, ref args, line, column);

    /// <summary>The body of the helper above, over the two declarations a ctor and a method both expose, so the
    /// class-method and struct-method fast paths can ask the SAME rule a compiled body needs: a LOADED (shell) method
    /// body has no AST, so refusing it was the only other outcome, and testing it on the VM with its `out` positions
    /// wrapped keeps the assignment rule while making the call work.</summary>
    private bool[]? WrapOutArgs(IReadOnlyList<bool>? isOutParams, IReadOnlyList<(TypeInfo Type, string Name)> parameters,
        ref object?[] args, int line, int column)
    {
        if (isOutParams == null || isOutParams.Count == 0) return null;
        // BOUNDED BY THE FLAG LIST, not by the parameters: `IsOutParams` is EMPTY when a function has no `out`
        // position (the wire writes the empty list back the same way), so indexing it per parameter threw
        // ArgumentOutOfRangeException for every `out`-free method of a LOADED class or struct. Measured: a struct
        // `operator+` whose body builds a `new` failed from a bundle with "Index was out of range ... (Parameter
        // 'index')", 5 suites' worth (vm_struct_op, vm_struct_ctor, _test_struct_ops, crash_operator_overload,
        // crash_10features_test).
        var anyOut = false;
        for (var i = 0; i < args.Length && i < parameters.Count && i < isOutParams.Count; i++)
            if (isOutParams[i]) { anyOut = true; break; }
        if (!anyOut) return null;

        var tracker = new bool[parameters.Count];
        var wrapped = new object?[args.Length];
        Array.Copy(args, wrapped, args.Length);
        for (var i = 0; i < wrapped.Length && i < parameters.Count && i < isOutParams.Count; i++)
        {
            if (!isOutParams[i]) continue;
            if (wrapped[i] is not RefCell oc) continue;
            var flag = i;
            var outType = parameters[i].Type;
            var outName = parameters[i].Name;
            wrapped[i] = new RefCell(() => oc.Get(), v =>
            {
                var coerced = ValidateOutAssignment(v, outType, outName, line, column);
                tracker[flag] = true;
                oc.Set(coerced);
            }, ByOut: true);
        }
        args = wrapped;
        return tracker;
    }

    // -----------------------------------------------------------------------------
    // Statement visitors (updated to return ExecutionResult)
    // -----------------------------------------------------------------------------

    // ============================================================================
    // 16. INTERPRETER - METHODES D'ASSISTANCE
    // ============================================================================

    /// <summary>The ONE struct-constructor matcher, shared by the tree and by the VM. It takes a COUNT and not
    /// expressions because it only ever read their Count. A match of EXACT arity wins; a `params` tail is the
    /// fallback that absorbs everything past its fixed parameters, so its empty and its one-array forms both
    /// match; and a call that fits neither is refused in the caller's own words.</summary>
    /// <summary>True when the call's ref/out SHAPE can satisfy this declaration. Only ONE direction is refused: a
    /// declared ref/out parameter needs an argument passed by reference, because a VALUE cannot fill it. The other
    /// direction (a `ref`/`out` argument at a PLAIN parameter) deliberately keeps the constructor, so the keyword
    /// rule can refuse it by name: `new S(ref x)` on `S(int a)` must still say the parameter is not declared by
    /// reference, rather than that no constructor exists.</summary>
    private static int RefOutShapeRank(ConstructorDecl ctor, IReadOnlyList<bool>? byRef)
    {
        var rank = 0;
        for (var i = 0; i < ctor.Params.Count; i++)
        {
            var declaredRefOrOut = (ctor.IsRefParams != null && i < ctor.IsRefParams.Count && ctor.IsRefParams[i])
                                || (ctor.IsOutParams != null && i < ctor.IsOutParams.Count && ctor.IsOutParams[i]);
            var passedByRef = byRef != null && i < byRef.Count && byRef[i];
            if (declaredRefOrOut && !passedByRef) return 2;   // IMPOSSIBLE: a value cannot fill a ref/out parameter
            if (!declaredRefOrOut && passedByRef) rank = 1;  // tolerable: the keyword rule refuses it BY NAME
        }
        return rank;
    }

    /// <summary>Resolves a compound-assignment target's sub-expressions exactly once (see TargetSlot).</summary>
    private TargetSlot ResolveTarget(Expr target, int line, int column)
    {
        switch (target)
        {
            case VariableExpr v:
                {
                    if (!CurrentFrame.Scopes.TryGet(v.Name, out var existing))
                    {
                        // The static-field fallback the ++/-- path always had, kept for every operator.
                        if (_currentClass?.StaticFields?.TryGetValue(v.Name, out var sf) == true)
                        {
                            var cls = _currentClass;
                            return new TargetSlot(sf, val => cls.StaticFields![v.Name] = val, null);
                        }
                        RuntimeError($"Undefined variable '{v.Name}'.", line, column);
                    }
                    if (existing is RefCell rc) return new TargetSlot(rc.Get(), rc.Set, null);
                    return new TargetSlot(existing, val => CurrentFrame.Scopes.Set(v.Name, val), null);
                }

            case PropertyAccessExpr p:
                {
                    var receiver = Evaluate(p.Target);
                    return ResolveMemberSlot(receiver, p.PropertyName, line, column);
                }

            case ArrayAccessExpr a:
                {
                    if (a.Indices.Any(i => i is RangeExpr))
                        RuntimeError("Invalid target for compound assignment.", line, column);
                    var container = Evaluate(a.Array);
                    // The indices are evaluated ONCE and handed to the same value-level core the VM uses, so
                    // the read and the write cannot disagree about which element they mean.
                    var idxVals = new object?[a.Indices.Count];
                    for (var i = 0; i < a.Indices.Count; i++) idxVals[i] = Evaluate(a.Indices[i]);
                    return ResolveElementSlot(container, idxVals, a.Line, a.Column);
                }

            default:
                RuntimeError("Invalid target for compound assignment.", line, column);
                return default;
        }
    }

    /// <summary>The FIELD, PROPERTY or backing field a member target names, resolved ONCE for the compound-assignment
    /// path and for a `ref`/`out` cell: inheritance, the access check, an auto-property's backing field and a CLR
    /// property through reflection are the same questions, so they are asked here and nowhere else. The `Read` it hands
    /// back is a LIVE read of the same storage, which is what a reference needs.</summary>
    private TargetSlot ResolveMemberSlot(object? receiver, string propertyName, int line, int column)
    {
        if (receiver == null)
            RuntimeError($"Cannot access property '{propertyName}' on null.", line, column);

        switch (receiver)
        {
            case StructInstance si:
                {
                    if (!si.Fields.TryGetValue(propertyName, out var curS))
                        RuntimeError($"Field '{propertyName}' not found in struct.", line, column);
                    var owner = si;
                    return new TargetSlot(curS, val => owner.Fields[propertyName] = val, null,
                        () => owner.Fields[propertyName]);
                }

            case ClassInstance ci:
                {
                    var evt = GetEventInfo(ci, propertyName, line, column);
                    if (evt != null) return new TargetSlot(null, _ => { }, evt);

                    var (declaring, field) = FindFieldInHierarchy(ci.Class, propertyName);
                    if (field == null)
                    {
                        // Auto-property: read and write through its backing field.
                        var backing = FindPropertyBackingField(ci.Class, propertyName);
                        if (backing == null)
                            RuntimeError($"Field '{propertyName}' not found in class '{ci.Class.Name}'.", line, column);
                        var (_, bf) = FindFieldInHierarchy(ci.Class, backing);
                        if (bf == null)
                            RuntimeError($"Field '{propertyName}' not found in class '{ci.Class.Name}'.", line, column);
                        var ownerB = ci;
                        return new TargetSlot(ci.FieldValues[bf.Name],
                            val => ownerB.FieldValues[bf.Name] = CoerceTo(val, bf.Type, line, column), null,
                            () => ownerB.FieldValues[bf.Name]);
                    }
                    CheckAccess(field.Access, declaring, line, column);
                    var ownerC = ci;
                    return new TargetSlot(ci.FieldValues[field.Name],
                        val => ownerC.FieldValues[field.Name] = CoerceTo(val, field.Type, line, column), null,
                        () => ownerC.FieldValues[field.Name]);
                }

            default:
                {
                    // A .NET property through reflection: ONE member INFO for the read and the write.
                    var cacheKey = (receiver.GetType(), propertyName);
                    if (!PropertyCache.TryGetValue(cacheKey, out var propInfo))
                    {
                        propInfo = receiver.GetType()
                                       .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                                   ?? RuntimeError<PropertyInfo>($"Property '{propertyName}' not found.", line, column);
                        PropertyCache[cacheKey] = propInfo;
                    }
                    var ownerClr = receiver;
                    return new TargetSlot(propInfo.GetValue(receiver), val => propInfo.SetValue(ownerClr, val), null,
                        () => propInfo.GetValue(ownerClr));
                }
        }
    }

    /// <summary>The ELEMENT an index target names, with its indices already evaluated. One implementation for the
    /// compound-assignment path and for a `ref`/`out` cell, so a reference and `a[i] += 1` cannot mean two different
    /// elements, and the indices are evaluated ONCE (a `ref a[i++]` increments the counter once).</summary>
    private TargetSlot ResolveElementSlot(object? container, object?[] idxVals, int line, int column)
    {
        var current = GetArrayElementValues(container, idxVals, line, column);
        return new TargetSlot(current, val => SetArrayElementValues(container, idxVals, val, line, column), null,
            () => GetArrayElementValues(container, idxVals, line, column));
    }

    /// <summary>The CELL a `ref`/`out` ARGUMENT gets when it names a field, a property or an element instead of a
    /// <summary>The write a `F(..) = value;` statement performs, in ONE place: the call answered a REFERENCE (a cell), and
    /// the write goes through it, so the storage written is whatever the callee answered a reference to. The tree's
    /// VisitRefAssignExpr and the compiled `__ref_assign__` helper both ask this, so the wording of its refusal cannot
    /// drift between the two engines.</summary>
    internal object? AssignThroughRefCell(object? target, object? value, int line, int column)
    {
        if (target is not RefCell cell)
        {
            RuntimeError(
                "Cannot assign through this call: it did not answer a reference. Only a function declared `ref <type> F(...)` "
                + "(a ref return) can be assigned through, and its body must end in `return ref <target>`.",
                line, column);
            return null;
        }
        cell.Set(value);
        return value;
    }

    /// variable. The read is LIVE and the write goes through the same slot a compound assignment writes through, so a
    /// reference and `x.F = v` are one storage. An EVENT is refused (a handler list is not a value), which is what the
    /// slot's own `Event` marker says.</summary>
    internal RefCell RefCellForTarget(Expr target, bool? byOut, int line, int column)
    {
        switch (target)
        {
            case PropertyAccessExpr p:
                {
                    var receiver = Evaluate(p.Target);
                    return CellFromSlot(ResolveMemberSlot(receiver, p.PropertyName, line, column), byOut, p.PropertyName, line, column);
                }

            case ArrayAccessExpr a:
                {
                    if (a.Indices.Any(i => i is RangeExpr))
                        RuntimeError("A range cannot be passed by ref/out: it names a copy, not an element.", line, column);
                    var container = Evaluate(a.Array);
                    var idxVals = new object?[a.Indices.Count];
                    for (var i = 0; i < a.Indices.Count; i++) idxVals[i] = Evaluate(a.Indices[i]);
                    return CellFromSlot(ResolveElementSlot(container, idxVals, a.Line, a.Column), byOut, null, line, column);
                }

            default:
                RuntimeError("Only a variable, a field or an element can be passed by ref/out.", line, column);
                return NullRefCell;
        }
    }

    /// <summary>The same cell for a target the VM has ALREADY evaluated: the receiver and the member name, or the
    /// container and its index values. Registered as `__ref_field__` and `__ref_elem__`, the shape the global case
    /// established (`__ref_global__`), because the VM's own ref encoding names a local slot and nothing else.</summary>
    internal RefCell RefCellForMemberValue(object? receiver, string propertyName, bool? byOut, int line, int column)
        => CellFromSlot(ResolveMemberSlot(receiver, propertyName, line, column), byOut, propertyName, line, column);

    internal RefCell RefCellForElementValue(object? container, object? index, bool? byOut, int line, int column)
        => CellFromSlot(ResolveElementSlot(container, [index], line, column), byOut, null, line, column);

    /// <summary>The cell a `ref &lt;name&gt;` expression answers, for a target the parser could not classify. Three cases,
    /// and each is a MEASURED one: an existing cell (a ref parameter, a promoted capture) is shared, so `ref p` and
    /// `ref x` over one name reach one storage; a name bound in a NON-global scope is a local or a capture, and its
    /// scope entry is its storage; and anything else is a GLOBAL, which every engine writes through ONE door
    /// (`SetGlobalValue`), because a cell over the frame's scope would write a COPY. That last case is not theoretical:
    /// `ref int rg = ref g; rg = 5;` answered `g=1` from a bundle where the file answered `g=5`.</summary>
    internal RefCell RefCellForName(string name, int line, int column)
    {
        if (CurrentFrame.Scopes.TryGet(name, out var existing) && existing is RefCell existingCell)
            return existingCell;
        if (IsBoundOutsideTheGlobalScope(name))
            return CaptureVariableReference(VariableExpr.Create(name, line, column), line, column,
                allowUndefined: false, byOut: false);
        if (ResolveGlobalValue(name) is not VmFallback)
            // A GLOBAL: the cell reads and writes the live global. A TREE-WALKED caller does not see a write made this way
            // (measured, and recorded in AGENTS: `ref int SlotG() { return ref g; }` with `int ViaCell() { SlotG() = 7;
            // return g; }` answers the value a PREVIOUS call left, 5, on the tree leg where the VM answers 7), which is the
            // tree's per-invocation global model rather than this cell: writing the frame's own scope dicts on top changed
            // nothing measurable (the frame's scopes hold neither the name nor a copy of it), so the shape is recorded and
            // left alone rather than half-fixed.
            return new RefCell(() => ResolveGlobalValue(name), v => SetGlobalValue(name, v, line, column));
        return CaptureVariableReference(VariableExpr.Create(name, line, column), line, column,
            allowUndefined: false, byOut: false);
    }

    /// <summary>True when the name is bound in a scope other than the GLOBAL one. `Scopes` enumerates INNERMOST first and
    /// the last entry is the global scope, which is the order `CaptureClosureSnapshotWithGlobal` documents.</summary>
    private bool IsBoundOutsideTheGlobalScope(string name)
    {
        var all = CurrentFrame.Scopes.Scopes.ToArray();
        for (var i = 0; i < all.Length - 1; i++)
            if (all[i].ContainsKey(name)) return true;
        return false;
    }

    private RefCell CellFromSlot(TargetSlot slot, bool? byOut, string? memberName, int line, int column)
    {
        if (slot.Event != null)
            RuntimeError($"'{memberName}' is an event, and an event cannot be passed by ref/out: it holds a handler LIST, not a value.", line, column);
        var read = slot.Read ?? (() => slot.Current);
        return new RefCell(read, slot.Write, byOut);
    }

    private object? EvalCompoundAssign(CompoundAssignExpr c)
    {
        var baseOp = c.Operator.Type switch
        {
            TokenType.PlusEqual => TokenType.Plus,
            TokenType.MinusEqual => TokenType.Minus,
            TokenType.StarEqual => TokenType.Star,
            TokenType.SlashEqual => TokenType.Slash,
            TokenType.PercentEqual => TokenType.Percent,
            TokenType.AmpersandEqual => TokenType.Ampersand,
            TokenType.PipeEqual => TokenType.Pipe,
            TokenType.CaretEqual => TokenType.Caret,
            TokenType.LeftShiftEqual => TokenType.LeftShift,
            TokenType.RightShiftEqual => TokenType.RightShift,
            // ??= needs no binary operator: it never computes, it either keeps or stores the RHS.
            TokenType.QuestionEqual => TokenType.QuestionQuestion,
            _ => RuntimeError<TokenType>($"Unsupported compound operator {c.Operator.Type}", c.Line, c.Column)
        };

        // The target first, ONCE, which also tells an event subscription apart from a numeric compound.
        var slot = ResolveTarget(c.Target, c.Line, c.Column);
        if (slot.Event != null && c.Operator.Type is TokenType.PlusEqual or TokenType.MinusEqual)
        {
            var handler = Evaluate(c.Value);
            SubscribeClassEvent(slot.Event, handler, remove: c.Operator.Type == TokenType.MinusEqual, c.Line, c.Column);
            return null;   // an event assignment has no value
        }

        if (c.Operator.Type == TokenType.QuestionEqual)
        {
            // `??=` short-circuits: what the target already holds stays, and the RHS is never evaluated.
            if (slot.Current != null) return slot.Current;
            var rhs = Evaluate(c.Value);
            slot.Write(rhs);
            return rhs;
        }

        // C#'s order: the target is read, then the right operand is evaluated, then the store.
        var result = ApplyBinaryOp(slot.Current, Evaluate(c.Value), baseOp, c.Line, c.Column);
        slot.Write(result);
        return result;
    }
}
