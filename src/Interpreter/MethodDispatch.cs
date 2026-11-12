namespace ScriptLangCore;

// Source: extracted from Interpreter.cs, Method dispatch pipeline
internal partial class Interpreter
{

    /// <summary>The tail of `TryGetValue(key, out value)`, shared by a script dictionary and a raw CLR one (a dict
    /// literal), so the two representations cannot drift: the caller's cell is written on EVERY path, null when
    /// the key is missing, which is `default(TValue)` here, and a second argument that is not a variable (no cell
    /// was captured for it) is refused rather than silently ignored.</summary>
    private bool WriteDictionaryTryGetValue(object?[] argValues, bool found, object? value, int line, int column)
    {
        if (argValues[1] is not RefCell outCell)
        {
            RuntimeError("TryGetValue's second argument must be a variable (`d.TryGetValue(k, out v)`).", line, column);
            return found;
        }
        outCell.Set(found ? value : null);
        return found;
    }

    private static bool IsTypeBinding(object? value) => value is ClassInfo or DelegateInfo or StructDeclStmt or Type;


    /// <summary>Script-facing statics with no CLR equivalent, shared by every dispatch path.
    /// `CancellationToken.Create(a, b, …)` is the concise linked-token form: it returns the TOKEN
    /// and the engine owns the backing source (see TrackLinkedToken).</summary>
    private bool TryShortFormStatic(object? receiver, string methodName, object?[] argValues,
        int line, int column, out object? result)
    {
        result = null;
        if (receiver is not Type staticType) return false;
        if (staticType == typeof(CancellationToken) && methodName.Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            if (argValues.Length == 0)
                RuntimeError<object?>("CancellationToken.Create expects at least one CancellationToken.", line, column);
            var tokens = new CancellationToken[argValues.Length];
            for (var i = 0; i < argValues.Length; i++)
            {
                if (argValues[i] is CancellationToken tk)
                {
                    tokens[i] = tk;
                    continue;
                }
                RuntimeError<object?>($"CancellationToken.Create expects CancellationToken arguments (argument {i + 1} was {argValues[i]?.GetType().Name ?? "null"}).", line, column);
                return false;   // unreachable: RuntimeError never returns
            }
            result = TrackLinkedToken(CancellationTokenSource.CreateLinkedTokenSource(tokens)).Token;
            return true;
        }
        return false;
    }

    /// <summary>Extracts parameter types from a FuncDecl/ConstructorDecl param list without LINQ allocations.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TypeInfo[] GetParameterTypes(IReadOnlyList<(TypeInfo Type, string Name)> @params)
    {
        var result = new TypeInfo[@params.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = @params[i].Type;
        return result;
    }


    private static bool IsTypeCompatible(Type? argType, TypeInfo expectedType)
    {
        if (argType == null)
            return expectedType.IsNullable || expectedType.BaseType == TokenType.Object;

        var actualTypeInfo = MapTypeInfo(argType);
        if (actualTypeInfo.BaseType == expectedType.BaseType)
            return true;

        // Numeric widening: script ints box as long, so an int-typed param accepts a
        // long/any numeric runtime value; float/double mixes too. Mirrors TypeSystem.CanConvert.
        if (TypeSystem.IsNumeric(expectedType.BaseType))
            return TypeSystem.IsNumeric(actualTypeInfo.BaseType);

        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    // P2-T2, arg-type-signature overload resolution + cache
    // ════════════════════════════════════════════════════════════════════

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object? EffectiveArgValue(object? a) => a is RefCell rc ? rc.Get() : a;

    private static ClassInfo? FindDeclaringClassForMethod(ClassInfo startClass, FuncDecl method)
    {
        var current = startClass;
        while (current != null)
        {
            foreach (var m in current.Methods)
                if (ReferenceEquals(m, method)) return current;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>Arg-type-aware script-method resolution (cached). Preserves the P2-T1
    /// single-overload fast path verbatim; multi-overload classes resolve against the
    /// runtime argument types, mirroring FindMatchingConstructor's pattern.</summary>
    private (ClassInfo? declaringClass, FuncDecl? method) ResolveScriptMethod(
        ClassInfo startClass, string methodName, object?[] args)
    {
        // P2-T1 fast path: exactly ONE same-name method in the START class → direct use,
        // zero allocation, no cache (identical to FindMethodInHierarchy).
        FuncDecl? single = null;
        foreach (var m in startClass.Methods)
        {
            if (!m.Name.Equals(methodName, Comparison)) continue;
            if (single != null) { single = null; break; }
            single = m;
        }
        if (single != null)
            return (startClass, single);

        // Collect same-name candidates across the hierarchy (derived first).
        List<(ClassInfo Owner, FuncDecl Method)>? candidates = null;
        var current = startClass;
        while (current != null)
        {
            foreach (var m in current.Methods)
            {
                if (!m.Name.Equals(methodName, Comparison)) continue;
                (candidates ??= [with(4)]).Add((current, m));
            }
            current = current.Parent;
        }

        if (candidates == null || candidates.Count == 0)
            return (null, null);

        // A single inherited method needs no resolution and no cache.
        if (candidates.Count == 1)
            return (candidates[0].Owner, candidates[0].Method);

        // P2-T2 cache probe. A hit is a PROBE only, full type-array equality is
        // verified inside TryGetOverload; mismatch/miss falls through to resolution.
        // 1-arg fast path: tuple key is the full signature → exact hit, no string build.
        if (args.Length == 1)
        {
            var argType = MapTokenType(EffectiveArgValue(args[0])?.GetType() ?? typeof(object));
            if (_overloadCache1.TryGetValue((startClass.Name, methodName, argType), out var hit1))
                return (hit1.DeclaringClass, hit1.Method);
        }
        var key = OverloadCacheKey(startClass.Name, methodName, args);
        if (TryGetOverload(key, args, out var cached, out var cachedOwner))
            return (cachedOwner!, cached!);

        // Resolution: parameter-count + type-assignability match.
        FuncDecl? best = null;
        ClassInfo? bestOwner = null;
        var bestScore = int.MaxValue;
        foreach (var (owner, m) in candidates)
        {
            if (!ArityCompatible(m, args.Length)) continue;
            var score = 0;
            var ok = true;
            for (var j = 0; j < args.Length; j++)
            {
                var d = ArgMatchDistance(EffectiveArgValue(args[j]), m.Params[j].Type);
                if (d < 0) { ok = false; break; }
                score += d;
            }
            if (!ok) continue;
            if (score >= bestScore) continue;
            bestScore = score; best = m; bestOwner = owner;
        }

        if (best == null)
        {
            // No overload matches the runtime args, keep the old first-by-name method so
            // the existing binding/coercion error paths behave exactly as before.
            var (owner0, m0) = candidates[0];
            CacheOverload(key, m0, owner0, args);
            return (owner0, m0);
        }

        CacheOverload(key, best, bestOwner!, args);
        return (bestOwner, best);
    }

    private static bool ArityCompatible(FuncDecl m, int argCount)
    {
        var total = m.Params.Count;
        var hasParams = m.IsParams.Count > 0 && m.IsParams[^1];
        if (hasParams) return argCount >= total - 1;
        if (argCount == total) return true;
        if (argCount < total && m.DefaultValues != null)
        {
            var required = 0;
            for (var i = 0; i < m.DefaultValues.Count; i++)
                if (m.DefaultValues[i] == null) required = i + 1;
            return argCount >= required;
        }
        return false;
    }

    /// <summary>Per-argument match distance. Negative = incompatible.</summary>
    private int ArgMatchDistance(object? value, TypeInfo paramType)
    {
        if (value == null)
            return IsReferenceLikeParam(paramType) ? 0 : -1;

        var argTi = MapTypeInfo(value.GetType());

        if (argTi.IsArray || paramType.IsArray)
        {
            if (!argTi.IsArray || !paramType.IsArray) return -1;
            if (argTi.ArrayRank != paramType.ArrayRank || argTi.JaggedDepth != paramType.JaggedDepth) return -1;
            return argTi.BaseType == paramType.BaseType ? 0 : -1;
        }

        if (paramType.TypeName != null)
        {
            if (value is ClassInstance ci)
                return ClassIsAssignable(ci.Class, paramType.TypeName) ? 0 : -1;
            if (value is StructInstance si)
                return si.StructName.Equals(paramType.TypeName, Comparison) ? 0 : -1;
            return -1;
        }

        if (paramType.BaseType == TokenType.Object)
            return 1;

        if (argTi.BaseType == paramType.BaseType)
            return 0;

        if (TypeSystem.IsNumeric(argTi.BaseType) && TypeSystem.IsNumeric(paramType.BaseType))
        {
            var d = NumericWidenDistance(argTi.BaseType, paramType.BaseType);
            return d >= 0 ? d : -1;
        }

        return -1;
    }

    private static bool IsReferenceLikeParam(TypeInfo t)
    {
        if (t.IsNullable || t.IsArray || t.TypeName != null) return true;
        return t.BaseType is TokenType.Object or TokenType.StringType;
    }

    private static int NumericWidenDistance(TokenType from, TokenType to)
    {
        var rf = NumericRank(from);
        var rt = NumericRank(to);
        if (rf < 0 || rt < 0) return -1;
        if (from == to) return 0;
        if (rt >= rf)
        {
            var d = rt - rf;
            return d == 0 ? 1 : d; // same-width cross-signedness → distance 1
        }
        return -1; // narrowing
    }

    private static int NumericRank(TokenType t) => t switch
    {
        TokenType.SByte or TokenType.Byte => 1,
        TokenType.Short or TokenType.UShort => 2,
        TokenType.Int or TokenType.UInt => 3,
        TokenType.Long or TokenType.ULong => 4,
        TokenType.Float => 5,
        TokenType.Double => 6,
        TokenType.Decimal => 7,
        _ => -1
    };

    private bool ClassIsAssignable(ClassInfo cls, string typeName)
    {
        var c = cls;
        while (c != null)
        {
            if (c.Name.Equals(typeName, Comparison)) return true;
            c = c.Parent;
        }
        return false;
    }


    private object? InvokeMethod(object? receiver, string methodName, object?[] argValues, int line, int column,
        IReadOnlyList<TypeInfo>? typeArgs = null, IReadOnlyList<string?>? argNames = null)
    {
        if (receiver == null)
            RuntimeError($"Cannot call method '{methodName}' on null.", line, column);
        if (methodName.Equals("Dispose", Comparison))
        {
            // Uniform "virtual dispose" semantics: IDisposable receivers dispose for real;
            // Coroutine has its own Dispose (handled by its case below); everything else is a
            // safe no-op, script-facing Dispose() never throws (mirrors the no-op IDisposable
            // added to every script-exposed internal type).
            if (receiver is IDisposable disposable)
            {
                disposable.Dispose();
                return null;
            }
            if (receiver is not Coroutine)
                return null;
        }

        switch (receiver)
        {
            // 1. Namespaces
            case Namespace ns:
                try
                {
                    return typeArgs is { Count: > 0 }
                        ? ns.InvokeTypedMethod(methodName, argValues, typeArgs)
                        : ns.InvokeMethod(methodName, argValues);
                }
                catch (ScriptException ex) { throw WithLocation(ex, line, column); }
                catch (Exception ex)
                {
                    // The single friendly map (TypeHelpers). This used to be a partial COPY of it, which is
                    // exactly how a raw CLR message could still reach a script.
                    ThrowException($"[{methodName}]: {FriendlyExceptionMessage(ex)}", line, column);
                    return null;
                }

            // 2. Script lists
            case IScriptList list:
                switch (methodName)
                {
                    case "Add":
                        if (argValues.Length != 1) RuntimeError("Add expects one argument.", line, column);
                        // A struct stored in a collection is BOXED in C#, which COPIES it: the element must not share
                        // the variable's instance, or `list[0].X = 42` writes through to the variable. Measured: it
                        // did, answering `a.X=42`.
                        list.Add(argValues[0] is StructInstance siAdd ? Interpreter.CloneStructForStore(siAdd) : argValues[0], line, column);
                        return null;

                    case "AddRaw":
                        if (argValues.Length != 1) RuntimeError("AddRaw expects one argument.", line, column);
                        list.AddRaw(argValues[0]!);
                        return null;

                    case "Remove":
                        if (argValues.Length != 1) RuntimeError("Remove expects one argument.", line, column);
                        list.Remove(argValues[0]);
                        return null;

                    case "RemoveAt":
                        if (argValues.Length != 1) RuntimeError("RemoveAt expects one argument.", line, column);
                        list.RemoveAt(Convert.ToInt32(argValues[0]));
                        return null;

                    case "ToArray":
                        if (argValues.Length != 0) RuntimeError("ToArray expects no arguments.", line, column);
                        return list.ToArray();

                    case "ToList":
                        if (argValues.Length != 0) RuntimeError("ToList expects no arguments.", line, column);
                        return IterableToList([list]);

                    case "Clear":
                        if (argValues.Length != 0) RuntimeError("Clear expects no arguments.", line, column);
                        list.Clear();
                        return null;

                    case "Sort":
                        // ONE implementation: ScriptCollectionBase.Sort, reached through the interface so both list
                        // flavours (boxed and unboxed) and both dispatch arms behave identically.
                        list.Sort(argValues, line, column);
                        return null;

                    default:
                        return MissingMemberOrExtension(receiver, methodName, argValues, line, column,
                        MissingMemberText(receiver, methodName, argValues.Length));
                }

            // 3. Script dictionaries
            case IScriptDictionary dict:
                switch (methodName)
                {
                    case "Add":
                        if (argValues.Length != 2) RuntimeError("Add expects two arguments.", line, column);
                        dict.Add(argValues[0], argValues[1], line, column);
                        return null;

                    case "AddRaw":
                        if (argValues.Length != 2) RuntimeError("AddRaw expects two arguments.", line, column);
                        dict.AddRaw(argValues[0]!, argValues[1]);
                        return null;

                    case "Remove":
                        if (argValues.Length != 1) RuntimeError("Remove expects one argument.", line, column);
                        dict.Remove(argValues[0]!);
                        return null;

                    case "ContainsKey":
                        if (argValues.Length != 1) RuntimeError("ContainsKey expects one argument.", line, column);
                        return dict.ContainsKey(argValues[0]!);

                    case "TryGetValue":
                        {
                            // C#'s shape: `d.TryGetValue(key, out value)`, the second argument is a VARIABLE the
                            // caller captures, written on every path (null when the key is missing, which is what
                            // default(TValue) is here), so the author's variable is never left unassigned.
                            if (argValues.Length != 2)
                                RuntimeError("TryGetValue expects two arguments: the key, then the variable to "
                                             + "receive the value (`d.TryGetValue(k, out v)`).", line, column);
                            var key = argValues[0]!;
                            return WriteDictionaryTryGetValue(argValues, dict.ContainsKey(key), dict[key], line, column);
                        }

                    case "Keys":
                        if (argValues.Length != 0) RuntimeError("Keys expects no arguments.", line, column);
                        return dict.Keys();

                    case "Values":
                        if (argValues.Length != 0) RuntimeError("Values expects no arguments.", line, column);
                        return dict.Values();

                    case "GetKey":
                        if (argValues.Length != 1) RuntimeError("GetKey expects one argument.", line, column);
                        return dict.GetKey(Convert.ToInt32(argValues[0]));

                    case "GetValue":
                        if (argValues.Length != 1) RuntimeError("GetValue expects one argument.", line, column);
                        return dict.GetValue(Convert.ToInt32(argValues[0]));

                    case "Clear":
                        if (argValues.Length != 0) RuntimeError("Clear expects no arguments.", line, column);
                        dict.Clear();
                        return null;

                    default:
                        return MissingMemberOrExtension(receiver, methodName, argValues, line, column,
                        MissingMemberText(receiver, methodName, argValues.Length));
                }

            // 3b. Script hash sets
            case IScriptHashSet hashSet:
                switch (methodName)
                {
                    case "Add":
                        if (argValues.Length != 1) RuntimeError("Add expects one argument.", line, column);
                        hashSet.Add(argValues[0], line, column);
                        return null;

                    case "AddRaw":
                        if (argValues.Length != 1) RuntimeError("AddRaw expects one argument.", line, column);
                        hashSet.AddRaw(argValues[0]!);
                        return null;

                    case "Remove":
                        if (argValues.Length != 1) RuntimeError("Remove expects one argument.", line, column);
                        return hashSet.Remove(argValues[0]!);

                    case "Contains":
                        if (argValues.Length != 1) RuntimeError("Contains expects one argument.", line, column);
                        return hashSet.Contains(argValues[0]!);

                    case "ToArray":
                        if (argValues.Length != 0) RuntimeError("ToArray expects no arguments.", line, column);
                        return hashSet.ToArray();

                    case "ToList":
                        if (argValues.Length != 0) RuntimeError("ToList expects no arguments.", line, column);
                        return IterableToList([hashSet]);

                    case "Clear":
                        if (argValues.Length != 0) RuntimeError("Clear expects no arguments.", line, column);
                        hashSet.Clear();
                        return null;

                    default:
                        return MissingMemberOrExtension(receiver, methodName, argValues, line, column,
                        MissingMemberText(receiver, methodName, argValues.Length));
                }

            // 4. ClassInfo, static method dispatch
            case ClassInfo staticClass:
                {
                    var staticMethod = staticClass.StaticMethods?.FirstOrDefault(m =>
                        m.Name.Equals(methodName, Comparison));
                    if (staticMethod != null)
                    {
                        var mainFrame = _rootFrame;
                        var realGlobalScope = mainFrame.Scopes.GetGlobalScopeDictionary();
                        var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(realGlobalScope);
                        var staticParamTypes = GetParameterTypes(staticMethod.Params);
                        var sf = new ScriptFunction(staticMethod, snapshot, staticMethod.ReturnType, staticParamTypes, staticMethod.IsRefParams, IsOutParams: staticMethod.IsOutParams);
                        var prevClass = _currentClass;
                        _currentClass = staticClass;
                        try
                        {
                            return InvokeScriptFunction(sf, argValues, line, column);
                        }
                        finally
                        {
                            _currentClass = prevClass;
                        }
                    }
                    RuntimeError($"Static method '{methodName}' not found in class '{staticClass.Name}'.", line, column);
                    break;
                }

            // 5. SuperProxy (method calls on base class)
            case SuperProxy super:
                {
                    var methodR = FindMethod(super.ParentClass, methodName)
                                  ?? RuntimeError<FuncDecl>($"Method '{methodName}' not found in parent class chain.", line, column);
                    return InvokeMethodOnInstance((ClassInstance)super.Instance, methodR, argValues, line, column);
                }

            // 6. Class instances (script-defined classes)
            case ClassInstance instance:
                {
                    var methodClass = FindMethod(instance.Class, methodName);
                    if (methodClass == null)
                    {
                        // The reflection verbs are the LAST resort, after the class's own members, so a class that
                        // declares Get/Set/Keys/Call keeps them.
                        if (TryReflectVerbs(instance, methodName, argValues, line, column, out var reflectedClass))
                            return reflectedClass;
                        RuntimeError<FuncDecl>($"Method '{methodName}' not found in class '{instance.Class.Name}' or its parents.", line, column);
                    }
                    return InvokeMethodOnInstance(instance, methodClass, argValues, line, column);
                }

            // 7. Coroutine ï¿½ explicit dispatch (so optional parameters work)
            case Coroutine co:
                {
                    if (methodName.Equals("Resume", Comparison))
                    {
                        if (argValues.Length == 0)
                            return co.Resume();
                        else if (argValues.Length == 1)
                            return co.Resume(argValues[0]);
                        else
                            RuntimeError("Resume expects 0 or 1 argument.", line, column);
                    }
                    else if (methodName.Equals("Dispose", Comparison))
                    {
                        co.Dispose();
                        return null;
                    }
                    else if (methodName.Equals("Continue", Comparison))
                    {
                        return co.Continue();
                    }
                    else if (methodName.Equals("Abort", Comparison))
                    {
                        co.Abort();
                        return null;
                    }
                    else if (methodName.Equals("Reset", Comparison))
                    {
                        co.Reset();
                        return null;
                    }
                    return MissingMemberOrExtension(receiver, methodName, argValues, line, column,
                        MissingMemberText(receiver, methodName, argValues.Length));
                }

            // 8. DynamicLibrary, resolve function pointer and call it
            // Built-in methods (Free, Dispose, GetFunctionPointer, ToString) fall through to .NET reflection below
            case DynamicLibrary lib when !DynamicLibrary.BuiltInMembers.Contains(methodName):
                // The same shared step as the other two sites: this copy used to Convert.ToInt64
                // every argument, so a string argument behaved differently depending on which
                // dispatch path happened to handle the call.
                return NativeCall.Invoke(lib.GetFunctionPointer(methodName), argValues);
        }


        // 9. .NET reflection fallback (String, StringBuilder, etc.), resolved, coerced and invoked
        // by the single CLR dispatch shared with the dynamic-receiver path.
        return InvokeClrMethod(receiver, methodName, argValues, line, column, argNames);
    }


    /// <summary>The ONE CLR reflection dispatch behind every path (the script-dispatch fallback in
    /// InvokeMethod and the dynamic-receiver path in InvokeMethodDispatch): short-form statics,
    /// overload resolution, named-argument reordering, coercion to the parameter types (params
    /// arrays, optional parameters, missing-argument error), invocation with void → VoidMarker, and
    /// one exception mapping. Callers that already resolved the method from a faster cache hand it in
    /// as `preResolved`; a caller holding a type-token overload cache passes `overloadCacheSlot` so a
    /// miss still warms that cache.</summary>
    [UnconditionalSuppressMessage("Trim", "IL2060", Justification = "MethodInfo.MakeGenericMethod(Type[]), script-requested generic method instantiation, the same policy the AotHints list states; a closed instantiation NativeAOT did not generate is reported as the engine's own error, never the runtime's text")]
    private object? InvokeClrMethod(object? receiver, string methodName, object?[] argValues, int line, int column,
        IReadOnlyList<string?>? argNames = null,
        (MethodInfo Method, ParameterInfo[] Params)? preResolved = null,
        (nint Token, string Name, int ArgCount, int ArgHash)? overloadCacheSlot = null,
        IReadOnlyList<TypeInfo>? typeArgs = null)
    {
        if (TryShortFormStatic(receiver, methodName, argValues, line, column, out var shortResult))
            return shortResult;

        // A raw CLR dictionary, a `{k: v}` LITERAL is one, gets the same three members as a script dictionary,
        // and this is the ONE place to give them to it: a CLR receiver takes the dynamic-receiver path straight
        // into this reflection dispatch, so the arm that serves those members for a script dictionary never sees
        // it (measured: `{a:1}.Keys()` answered "Method 'Keys' not found on type Dictionary<object, object>"
        // while `new Dictionary<string,int>().Keys()` worked, and its CLR property form came back as a
        // KeyCollection with no indexer). Any other name falls through to the ordinary resolution below, so
        // `ContainsKey` and the rest are untouched.
        if (receiver is IDictionary rawDict and not IScriptDictionary)
        {
            switch (methodName)
            {
                case "Keys":
                    if (argValues.Length != 0) RuntimeError("Keys expects no arguments.", line, column);
                    // An ARRAY, like the script dictionary's method: the PROPERTY is the list form, and the
                    // corpus's `.Length` on this spelling is what that keeps working.
                    return DictionaryProjectionOfRaw(rawDict, keys: true).ToArray();

                case "Values":
                    if (argValues.Length != 0) RuntimeError("Values expects no arguments.", line, column);
                    return DictionaryProjectionOfRaw(rawDict, keys: false).ToArray();

                case "TryGetValue":
                    if (argValues.Length != 2)
                        RuntimeError("TryGetValue expects two arguments: the key, then the variable to receive "
                                     + "the value (`d.TryGetValue(k, out v)`).", line, column);
                    {
                        var rawKey = argValues[0]!;
                        return WriteDictionaryTryGetValue(argValues, rawDict.Contains(rawKey), rawDict[rawKey], line, column);
                    }
            }
        }

        // AN ARRAY'S COLLECTION MEMBERS WITHOUT REFLECTION, and this is the compiled twin of the property arm that
        // answers `Count` (see GetMemberValue). Both members are what an array implements through IList<T> /
        // ICollection<T> EXPLICITLY, so the reflection path could reach them on the development build while the
        // trimmer removed them from the published one: measured, `[1, 2, 3].IndexOf(2)` answered 1 on JIT and
        // "Method 'IndexOf' not found on type int[]" on the AOT publish. Rooting the collection interfaces was
        // measured and rejected (1,178,624 bytes of artifact for two members).
        if (receiver is Array arrayReceiver)
        {
            switch (methodName)
            {
                case "IndexOf" when argValues.Length == 1:
                    return Array.IndexOf(arrayReceiver, argValues[0]);
                case "Clone" when argValues.Length == 0:
                    return arrayReceiver.Clone();
                // `ToArray()` on an array is the same COPY `Clone()` answers (measured: it was missing, while the same
                // call on a script list worked, since the list dispatch has its own arm).
                case "ToArray" when argValues.Length == 0:
                    return arrayReceiver.Clone();
                // A script LIST literal IS a typed array here (`[3, 1, 2]` is an int[]), so "sort this" arrives at the
                // array arm for the most ordinary spelling of it. Array.Sort is the same implementation the list
                // dispatch uses (InterpreterTypes, ScriptCollectionBase.Sort), with the same two shapes: nothing, or a
                // script comparer. A ScriptException out of the comparer is rethrown as itself, because Array.Sort
                // wraps it and this repository never reports a script's own failure through the CLR's text.
                case "Sort" when argValues.Length == 0:
                    try { Array.Sort(arrayReceiver); }
                    catch (Exception ex) { RuntimeError(FriendlyExceptionMessage(ex), line, column); }
                    return null;
                case "Sort" when argValues.Length == 1:
                    {
                        if (!Interpreter.IsScriptCallable(argValues[0]))
                            RuntimeError("Sort takes a function that compares two elements, not a value.", line, column);
                        try { Array.Sort(arrayReceiver, new ScriptComparer(argValues[0], line, column)); }
                        catch (Exception ex)
                        {
                            if ((ex as ScriptException ?? ex.InnerException as ScriptException) is { } scriptFailure)
                                throw scriptFailure;
                            RuntimeError(FriendlyExceptionMessage(ex), line, column);
                        }
                        return null;
                    }
            }
        }

        var isStaticCall = receiver is Type;
        var actualType = isStaticCall ? (Type)receiver! : receiver!.GetType();

        MethodInfo method;
        ParameterInfo[] parameters;
        if (preResolved is { } pr)
        {
            (method, parameters) = (pr.Method, pr.Params);
        }
        else
        {
            // One pass with the pooled builder. The key shape is the one both paths already used
            // ("static:" prefix, type, name, comma-joined arg types), so existing cache entries stay
            // valid and the two caches remain interchangeable. A call that names TYPE ARGUMENTS says which
            // instantiation it wants, so they are part of the key, otherwise `M<int>()` and `M<string>()`
            // would share one entry and the first caller would decide for the other.
            var argTypes = new Type[argValues.Length];
            string cacheKey;
            {
                // The key is materialized right away: StringBuilderPool.Rent() hands out a shared
                // thread-static builder, so it must not be held across other engine calls.
                var sb = StringBuilderPool.Rent();
                if (isStaticCall) sb.Append("static:");
                sb.Append(actualType.FullName).Append(':').Append(methodName);
                if (typeArgs is { Count: > 0 })
                {
                    sb.Append('<');
                    for (var i = 0; i < typeArgs.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(typeArgs[i].ToString());
                    }
                    sb.Append('>');
                }
                sb.Append(':');
                for (var i = 0; i < argValues.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    var t = argValues[i]?.GetType() ?? typeof(object);
                    argTypes[i] = t;
                    sb.Append(t.FullName);
                }
                cacheKey = sb.ToString();
            }

            if (!_methodCache.TryGetValue(cacheKey, out var cached))
            {
                var flags = isStaticCall
                    ? BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase
                    : BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;

                // A script that names type arguments gets the method it asked for: the generic DEFINITION of
                // that name and arity, CLOSED with the script's own types. Nothing did this before, the
                // arguments were dropped before they arrived here, so `Array.Empty<int>()` invoked the OPEN
                // definition and the CLR refused it ("Late bound operations cannot be performed on types or
                // methods for which ContainsGenericParameters is true"), while a call that also matched a
                // non-generic overload silently ignored the argument. When the type has no such definition the
                // ordinary resolution below reports the miss in its own terms (and still finds a non-generic
                // overload, which is what makes the wrapper accessors' `Get<object>(…)` spellings work).
                MethodInfo? resolved = null;
                if (typeArgs is { Count: > 0 })
                {
                    var wanted = new Type[typeArgs.Count];
                    for (var i = 0; i < typeArgs.Count; i++)
                    {
                        var ti = typeArgs[i];
                        var named = ti.TypeName is { Length: > 0 } n ? n : null;
                        // A NAME resolves the way every other type name does, NOT through GetSystemType, which maps
                        // any named type to `object`, right for binder matching, wrong for instantiation
                        // (`Array.Empty<ComNode>()` would have closed as Array.Empty<object>`). ONE lookup, the same
                        // one `is`, `typename` and the identifier fallback use, so a name cannot be a type in one
                        // place and not in another.
                        Type? clr = named is null ? null : ResolveTypeToClr(named, lax: !_caseSensitive);
                        // `object` is the right mapping for a script-DECLARED class or struct, it has no CLR
                        // type at all, but a name the engine does not know anywhere is a typo and must not
                        // silently instantiate one: `Array.Empty<NotAType>()` was accepted as Empty<object>().
                        if (clr is null && named is not null && !IsKnownType(named))
                            throw new ScriptException(
                                $"Unknown type argument '{named}': no type of that name is defined.");
                        wanted[i] = clr ?? TypeTokenHelper.GetSystemType(ti);
                    }
                    foreach (var candidate in actualType.GetMethods(flags))
                    {
                        if (!candidate.IsGenericMethodDefinition) continue;
                        if (!candidate.Name.Equals(methodName, Comparison)) continue;
                        if (candidate.GetGenericArguments().Length != typeArgs.Count) continue;
                        var cp = candidate.GetParameters();
                        if (cp.Length != argValues.Length
                            && !(cp.Length > 0 && cp[^1].IsDefined(typeof(ParamArrayAttribute), false))) continue;
                        try { resolved = candidate.MakeGenericMethod(wanted); }
                        catch (Exception ex)
                        {
                            // NativeAOT can only close an instantiation its compiler already generated. Report
                            // that as the engine's own error, never the runtime's text, and do NOT fall back to
                            // a different method: that would silently run something the script did not ask for.
                            throw new ScriptException(
                                $"Cannot instantiate '{methodName}<{string.Join(", ", wanted.Select(t => t.Name))}>' "
                                + $"on this build ({ex.GetType().Name}).");
                        }
                        break;
                    }
                }

                resolved ??= FindBestMethod(actualType, methodName, argValues.Length, argTypes, flags);
                if (resolved == null)
                {
                    // The reflection verbs are the LAST resort here too, so a real member of any name always wins.
                    if (TryReflectVerbs(receiver, methodName, argValues, line, column, out var reflectedClr))
                        return reflectedClr;
                    resolved = RuntimeError<MethodInfo>(NoOverloadText(actualType, methodName, argValues, flags, line, column), line, column);
                }
                var ps = resolved.GetParameters();
                _methodCache[cacheKey] = (resolved, ps);
                cached = (resolved, ps);
            }

            (method, parameters) = (cached.Method, cached.Params);

            // The overload fast path is keyed WITHOUT the type arguments, so a typed call must not warm it: a
            // hit would hand a later untyped call this instantiation.
            if (typeArgs is not { Count: > 0 } && overloadCacheSlot is { Token: not 0 } slot)
                _methodOverloadCache[(slot.Token, slot.Name, slot.ArgCount, slot.ArgHash)] = (method, parameters);
        }

        if (argNames is { Count: > 0 })
        {
            var typedParams = new (TypeInfo Type, string Name)[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                typedParams[i] = (MapTypeInfo(parameters[i].ParameterType), parameters[i].Name ?? "");
            argValues = ReorderNamedArgs(argValues, argNames, typedParams, line, column);
        }

        var paramCount = parameters.Length;
        var hasParamsParam = paramCount > 0 && parameters[paramCount - 1].IsDefined(typeof(ParamArrayAttribute), false);
        var fixedParamCount = hasParamsParam ? paramCount - 1 : paramCount;
        var finalArgs = ArrayPool.Rent(paramCount);
        try
        {
            for (var i = 0; i < fixedParamCount; i++)
            {
                var expectedType = parameters[i].ParameterType;
                if (i < argValues.Length)
                {
                    var arg = argValues[i];
                    finalArgs[i] = (arg != null && arg.GetType() == expectedType) ? arg : CoerceArgument(arg, expectedType, line, column);
                }
                else if (parameters[i].IsOptional)
                {
                    finalArgs[i] = parameters[i].DefaultValue;
                }
                else
                {
                    RuntimeError($"Missing required argument for parameter '{parameters[i].Name}'.", line, column);
                }
            }

            // Excess arguments collect into the params array, the dynamic path supported this, the
            // script-dispatch fallback did not; unifying gives both the same behaviour.
            if (hasParamsParam)
            {
                var elemType = parameters[fixedParamCount].ParameterType.GetElementType()!;
                var excessCount = Math.Max(0, argValues.Length - fixedParamCount);
                var paramArray = Array.CreateInstance(elemType, excessCount);
                for (var i = 0; i < excessCount; i++)
                    paramArray.SetValue(CoerceArgument(argValues[fixedParamCount + i], elemType, line, column), i);
                finalArgs[fixedParamCount] = paramArray;
            }

            try
            {
                var result = method.Invoke(isStaticCall ? null : receiver, finalArgs);
                // A CLR task or VALUE task becomes the engine's own Task so ONE await serves both; measured,
                // `await en.MoveNextAsync()` refused with "await expects a Task" while the member resolved,
                // because MoveNextAsync returns ValueTask<bool> (the same reason the static arm at
                // VisitExpressions.cs bridges its own result).
                return method.ReturnType == typeof(void) ? VoidMarker.Instance
                    : AsClrTask(result, out var clrTask) ? ToScriptTask(clrTask!)
                    : result;
            }
            catch (TargetInvocationException ex)
            {
                // A single mapping for both paths: the friendly message (which already covers
                // FormatException as "Invalid format: ..."), never a raw CLR exception.
                ThrowException($"[{methodName}]: {FriendlyExceptionMessage(ex.InnerException ?? ex)}", line, column);
                return null;
            }
            catch (NotSupportedException) when (typeArgs is { Count: > 0 })
            {
                // A TYPED call whose instantiation this build does not contain. NativeAOT only generates the
                // instantiations its compiler saw, so one it never saw fails HERE, when the closed method is
                // invoked, rather than when it is closed. (An exception from inside the callee itself arrives
                // wrapped in TargetInvocationException and is handled above; this is the RUNTIME refusing the
                // dispatch.) Name the method and the instantiation, and say what does work, instead of letting
                // the friendly map report a bare "operation not supported".
                ThrowException($"[{methodName}]: this build contains no code for "
                    + $"'{methodName}<{string.Join(", ", typeArgs)}>', the AOT compiler only generates the "
                    + "instantiations it can see, so a REFERENCE type (string, a class) works while a value type "
                    + "works only when something in the binary already asks for that exact instantiation.", line, column);
                return null;
            }
            catch (Exception ex)
            {
                // Also through the friendly map: a raw CLR/Win32 message ("DllNotFound_Windows, sqlite3,
                // <localized>") is not something a script should have to parse.
                ThrowException($"[{methodName}]: {FriendlyExceptionMessage(ex)}", line, column);
                return null;
            }
        }
        finally { ArrayPool.Return(finalArgs); }
    }

    /// <summary>Task 4: wraps a class/struct method whose body contains yield (IsCoroutine) in a
    /// Coroutine+Generator, mirroring InvokeScriptFunction's coroutine branch for free functions.
    /// The VM fast paths are gated off for such methods (!method.HasYield, a flat VM run would
    /// return the first yielded value as the method result), and the tree method paths must not
    /// execute the body inline (`yield` outside a coroutine crashes). The Coroutine carries the
    /// bound receiver: the VM path (Generator.MoveNext) receives `args + instance` so the compiled
    /// `this` local (index params.Count) binds; the tree path binds `this` in the coroutine's own
    /// scope via ThisInstance/ThisClass (InvokeCoroutineBody). Same VmBacked decision as
    /// InvokeScriptFunction: VM only when compiled + VmPathEnabled + HasYield and the body is
    /// proven free of CoroutineYieldRequest producers (IsYieldRequestBody gate).</summary>
    private object? WrapCoroutineMethod(FuncDecl method, object?[] args, object? instance, ClassInfo? declClass, int line, int column)
    {
        var snapshot = CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(_rootFrame.Scopes.GetGlobalScopeDictionary());
        var sf = new ScriptFunction(method, snapshot, method.ReturnType, GetParameterTypes(method.Params), method.IsRefParams, IsOutParams: method.IsOutParams);
        var vmBacked = method.Compiled != null && VmPathEnabled(method) && method.HasYield && !IsYieldRequestBody(method);
        // Same Task-4/CR fix as the free-function coroutine branch: a VM-backed method generator's
        // body runs on the bytecode VM, which binds co.Args positionally with no defaults/params/
        // coercion, bake them into the parameter args here (BEFORE appending the receiver), so the
        // VM sees the same parameter values the tree's InvokeCoroutineBody would bind.
        if (vmBacked) args = NormalizeArgsForVm(method, args, line, column);
        var coArgs = new object?[args.Length + 1];
        Array.Copy(args, coArgs, args.Length);
        coArgs[args.Length] = instance;   // `this` local, the compiler indexes it after the params
        var co = new Coroutine(this, sf, coArgs)
        {
            VmBacked = vmBacked,
            ThisInstance = instance,
            ThisClass = declClass,
        };
        return new Generator<object?>(co);
    }

    private object? InvokeMethodOnInstance(ClassInstance instance, FuncDecl method, object?[] args, int line, int column,
        IReadOnlyList<TypeInfo>? typeArgs = null)
    {
        // Generic method instantiation for instance methods
        if (method.TypeParameters.Count > 0 && typeArgs != null)
        {
            var concrete = InstantiateGenericFunction(method, args, line, column, typeArgs, asMethod: true);
            if (concrete != null)
            {
                var previousThis = _currentThis;
                var previousClass = _currentClass;
                _currentThis = instance;
                var (methodDeclClass, _) = FindMethodInHierarchy(instance.Class, method.Name);
                _currentClass = methodDeclClass ?? instance.Class;
                try
                {
                    // Task 4: a generic instance-method coroutine (body contains yield) must go
                    // through WrapCoroutineMethod, exactly like the non-generic path below.
                    // InvokeScriptFunction's coroutine branch would build `new Coroutine(this, fn, args)`
                    // with NO trailing `this` local and null ThisInstance/ThisClass, the generator
                    // body's `this` would be unbound on both the VM and tree paths. WrapCoroutineMethod
                    // appends the receiver as the trailing `this` local (compiler indexes it after the
                    // params) and binds ThisInstance/ThisClass for the tree path (InvokeCoroutineBody).
                    // Generic concrete methods are never pre-compiled (the clone's Compiled is null), so
                    // this always takes the tree path, which binds `this` via the Coroutine.
                    if (concrete.Declaration.IsAsync)
                        return BeginAsyncMethodCall(concrete.Declaration, args, instance, _currentClass);
                    if (concrete.IsCoroutine)
                        return WrapCoroutineMethod(concrete.Declaration, args, instance, _currentClass, line, column);
                    // The clone is an ordinary CONCRETE method now, no type parameters survive it, so hand
                    // it to the ordinary path, which owns the VM fast path (receiver in the trailing `this`
                    // local) and the tree fallback. Compiling the clone with a method's context is what makes
                    // that possible: a `this`-using body is refused by the plain function path.
                    return InvokeMethodOnInstance(instance, concrete.Declaration!, args, line, column);
                }
                finally { _currentThis = previousThis; _currentClass = previousClass; }
            }
        }

        // P2-T2 arg-type-signature overload cache: when the class exposes >1 same-name
        // method, re-resolve against the RUNTIME argument types (cached). This is the
        // authoritative resolution point, explicit receiver calls, implicit-this
        // (BoundMethod) and base-proxy calls all funnel through InvokeMethodOnInstance.
        ClassInfo? resolvedDeclClass = null;
        if (method.TypeParameters.Count == 0)
        {
            var resolutionScope = FindDeclaringClassForMethod(instance.Class, method) ?? instance.Class;
            var (resolvedOwner, resolvedMethod) = ResolveScriptMethod(resolutionScope, method.Name, args);
            if (resolvedMethod != null)
            {
                method = resolvedMethod;
                resolvedDeclClass = resolvedOwner;
            }
        }

        var methodName = $"{instance.Class.Name}.{method.Name}";
        _callStack.Push(methodName);

        try
        {
            // ---- pure-cache check (class methods) ----
            if (method.IsPure)
            {
                var key = (method.Name, args);
                if (_pureMemoCache.TryGetValue(key, out var cachedResult))
                    return Equals(cachedResult, VoidMarker.Instance) ? null : cachedResult;
            }

            var previousThis = _currentThis;
            var previousClass = _currentClass;
            _currentThis = instance;
            // Set _currentClass to the class that DECLARES the method, not the instance's runtime type
            // This ensures private members in the declaring class are accessible from its own methods
            if (resolvedDeclClass == null)
            {
                var (foundDeclClass, _) = FindMethodInHierarchy(instance.Class, method.Name);
                resolvedDeclClass = foundDeclClass;
            }
            _currentClass = resolvedDeclClass ?? instance.Class;

            // Task 4: a class-method coroutine (body contains yield, IsCoroutine) must return a
            // Generator, exactly like InvokeScriptFunction does for free functions. The VM fast
            // path above is gated off (!method.HasYield); executing the body inline here would hit
            // a `yield` outside any coroutine (crash). Wrap it instead, the Coroutine carries the
            // receiver: VM path appends it as the trailing `this` local; tree path binds it via
            // ThisInstance/ThisClass (InvokeCoroutineBody).
            if (method.IsAsync)
                return BeginAsyncMethodCall(method, args, instance, resolvedDeclClass ?? instance.Class);
            if (method.IsCoroutine)
                return WrapCoroutineMethod(method, args, instance, resolvedDeclClass ?? instance.Class, line, column);

            // Use current call frame: just push a new scope for the method body
            var paramsBindEnd = method.Params.Count;
            var hasParams = method.IsParams.Count > 0 && method.IsParams[^1];
            if (hasParams) paramsBindEnd = method.Params.Count - 1;

            // The lower bound is the declaration's OWN rule, and it is the one computation the free-function
            // call path uses: a `params` tail is zero-or-more and a defaulted parameter is not required. The
            // two paths disagreed, measured: `S(1)` for `int S(int a, params int[] rest)` was refused with
            // "Expected at least 2 arguments, got 1" while the same shape as a FUNCTION answered 1.
            var methodMinRequired = MinRequiredArgs(method);

            if (args.Length < methodMinRequired)
                RuntimeError($"Expected at least {methodMinRequired} arguments, got {args.Length}.", line, column);

            // The arguments in the layout the CALLEE reads, built ONCE and kept beside the caller's own list:
            // a `params` tail becomes the array its last slot holds, and a LOADED method (a bundle's shell)
            // has no AST, so the TREE cannot fill a default for it either. The tree bindings below keep
            // reading the ORIGINAL arguments, so a body the VM hands back still sees what the caller passed.
            // Measured before this: every tail method in a stripped bundle was refused as "bytecode-only and
            // cannot run on the tree walker", and the flat call would otherwise bind the raw last argument
            // into the tail slot (`Property 'Length' not found on type int`).
            var vmArgValues = args;
            if (hasParams || (method.IsVmShell && args.Length != method.Params.Count))
                vmArgValues = NormalizeArgsForFlatCall(method, args, line, column);
            // ── VM fast path: pre-compiled method body (params + this as locals) ──
            // Task 4: `!method.HasYield`, a coroutine method (body contains yield) must NOT
            // flat-swap into YIELD bytecode here (the VM would return the first yielded value as
            // the method result). It falls through to the coroutine wrap below.
            // A LOADED (shell) method body is the ONE case where a ref/out signature takes this path: it has no
            // AST, so the tree walker below could only REFUSE it, and the VM handles a cell argument (deref on
            // read, write through on store). Its `out` positions are wrapped so the assignment rule still holds
            // (WrapOutArgs), which is what InitOutTracking does for a body the tree runs. A SOURCE method keeps
            // the exclusion, so which engine runs a source method does not move.
            // Measured end to end on a clean build: a CLASS and a STRUCT with an `out`/`ref` method, called from a
            // tree-walked caller inside a stripped bundle, answer exactly what their files answer (`go=903` against
            // `go=903`, and `go=974` against `go=974`), vm_params' bundle goes from 73 passed / 9 failed to 79 / 3
            // while its source run stays 84 / 0, and forcing this clause false puts the bundle back at 73 / 9.
            // The struct path below asks the same rule (smShellRefOut), so both are covered by those numbers.
            var shellRefOut = method.IsVmShell;
            if (method.Compiled != null && VmPathEnabled(method)
                && (method.DefaultValues == null || method.IsVmShell)
                && !method.HasYield
                && vmArgValues.Length == method.Params.Count
                && (shellRefOut || (method.IsRefParams == null || !method.IsRefParams.Any(b => b)))
                && (shellRefOut || (method.IsOutParams == null || !method.IsOutParams.Any(b => b))))
            {
                // Tuple parameter element names: the bind loop below attaches them for the TREE, and a
                // VM body reads its parameters from vmArgs instead, so they are attached HERE for it,
                // exactly as the flat call branches do before they fill the callee's locals. Without
                // this, running the VM path before the bind loop left `p.v` unresolved inside a method
                // the VM runs (measured: tuple_params_test).
                if (method.TupleParamNames is { Count: > 0 })
                    for (var tpi = 0; tpi < method.Params.Count && tpi < vmArgValues.Length; tpi++)
                        BindTupleParam(method, tpi, vmArgValues[tpi], line, column);

                // The class context this call sets (saved above) must come back with it. The restore
                // used to sit in the scope's finally, which a VM-path return now precedes, so it is
                // done here instead. A VmFallback is the ONE case that must NOT restore: the tree
                // path below runs with the context still set.
                var fellThrough = false;
                var vmArgs = ArrayPool.Rent(method.Params.Count + 1);
                try
                {
                    // Each argument is COERCED to its declared parameter type here, through the SAME helper the
                    // flat call path uses. Vm.Execute binds what it is handed RAW (its prologue is
                    // `locals[i] = VmValue.From(args[i])`), so a method's argument skipped its declared type
                    // entirely while the identical free function applied it: measured,
                    // `double Half(double x) { return x / 2; }` as a method answered 1 for Half(3), the
                    // free function answered 1.5, and only the RETURN was coerced in the method. The tree's
                    // BindParam loop coerces too, but into the SCOPE, which a VM body never reads.
                    for (var i = 0; i < vmArgValues.Length; i++)
                        vmArgs[i] = Interpreter.CoerceFlatCallArg(method, vmArgValues[i], i, line, column);
                    vmArgs[method.Params.Count] = instance;   // "this" local (compiler indexes it after params)
                                                              // The same rule as the free-function boundary (TryExecuteVmBody): a body the VM STOPPED PART-WAY
                                                              // through is only safe to hand to the tree when it committed nothing a call cannot undo, because the
                                                              // tree runs it from its first statement. VmBodyCommitted is set by the VM's own global/property writes.
                    var classVmArgs = vmArgs;
                    var classOutTrack = shellRefOut
                        ? WrapOutArgs(method.IsOutParams, method.Params, ref classVmArgs, line, column)
                        : null;
                    var classCommitBaseline = VmBodyCommitted;
                    VmBodyCommitted = false;
                    object? vmResult;
                    var classCommittedHere = false;
                    try
                    {
                        vmResult = Vm.Execute(method.Compiled, classVmArgs, this, methodName);
                    }
                    finally
                    {
                        classCommittedHere = VmBodyCommitted;
                        VmBodyCommitted = classCommitBaseline || classCommittedHere;
                    }
                    if (vmResult is VmFallback && classCommittedHere)
                        RuntimeError($"Method '{method.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
                    if (vmResult is not VmFallback)
                    {
                        VmInvocations++;
                        // The `out` assignment rule for a LOADED body: its writes go to the wrapper cells
                        // WrapOutArgs installed, not to the live scope's tracker, so the tracker answers here.
                        if (classOutTrack != null)
                            VerifyOutParams(classOutTrack, method.IsOutParams, method.Params, method.Line, method.Column);
                        if (method.ReturnType == TypeInfo.Void)
                        {
                            if (vmResult != null)
                                RuntimeError($"Void method '{method.Name}' cannot return a value.", line, column);
                            return null;
                        }
                        if (vmResult == null && !method.ReturnType.IsNullable && method.ReturnType.BaseType != TokenType.Object)
                            RuntimeError($"Method '{method.Name}' must return a value.", line, column);
                        return MethodReturn(method, vmResult, line, column);
                    }
                    fellThrough = true;   // VmFallback: the tree path below continues with the context set
                }
                finally
                {
                    ArrayPool.Return(vmArgs);
                    if (!fellThrough)
                    {
                        _currentThis = previousThis;
                        _currentClass = previousClass;
                    }
                }
                // VmFallback → fall through to the tree-walker below
            }

            CurrentFrame.Scopes.PushScope();
            try
            {
                // Define 'this' in the method scope
                CurrentFrame.Scopes.Define("this", instance, method.Line, method.Column);


                for (var i = 0; i < paramsBindEnd; i++)
                {
                    object? paramVal;
                    if (i < args.Length)
                        paramVal = args[i];
                    else if (method.DefaultValues != null && i < method.DefaultValues.Count && method.DefaultValues[i] != null)
                        paramVal = Evaluate(method.DefaultValues[i]!);
                    else
                        paramVal = RuntimeError<object?>($"Missing argument for parameter '{method.Params[i].Name}'.", line, column);

                    BindTupleParam(method, i, paramVal, line, column);
                    // The flags are read through the SAME guards the struct path uses (its smIsRef/smIsOut
                    // locals) rather than indexing IsRefParams directly: a declaration whose flags are absent
                    // or shorter than its parameters then binds BY VALUE instead of throwing. That is what
                    // CS8602 pointed at here, and the struct twin never had it because it always guarded.
                    var isRefParam = method.IsRefParams != null && i < method.IsRefParams.Count && method.IsRefParams[i];
                    var isOutParam = method.IsOutParams != null && i < method.IsOutParams.Count && method.IsOutParams[i];
                    BindParam(method.Params[i].Name, paramVal, method.Params[i].Type,
                        isRefParam,
                        isOutParam,
                        line, column);
                }

                // The tail rule (and its ref/out refusal) lives in ONE place: AssembleParamsTail.
                if (hasParams)
                    CurrentFrame.Scopes.Define(method.Params[paramsBindEnd].Name,
                        AssembleParamsTail(method.Params, args, line, column), method.Line, method.Column);

                // ── out-param assignment tracking ──
                var outAssigned = InitOutTracking(method.IsOutParams, method.Params, method.Line, method.Column);

                // Bytecode-only method bodies (IsVmShell) have no AST: if the VM fast path
                // produced no result (unsupported signature/VmFallback) there is no tree
                // fallback, fail clearly instead of executing an empty body.
                if (method.IsVmShell)
                    RuntimeError($"Method '{method.Name}' is bytecode-only and cannot run on the tree walker.", line, column);

                object? returnValue = null;
                var flow = ControlFlow.Continue;

                PrebindDeclaredFunctions(method.Body);
                foreach (var stmt in method.Body)
                {
                    var result = Execute(stmt);
                    flow = result.Flow;
                    if (flow == ControlFlow.Return)
                    {
                        returnValue = result.Value;
                        break;
                    }
                    if (flow == ControlFlow.Throw)
                    {
                        var ex = _thrownException;
                        _thrownException = null;
                        throw ThrowPayloadException(ex, _currentLine, _currentColumn);
                    }
                    if (flow != ControlFlow.Continue)
                    {
                        RuntimeError($"Unexpected control flow in method '{method.Name}'.", line, column);
                    }
                }

                // ── out-param verification ──
                VerifyOutParams(outAssigned, method.IsOutParams, method.Params, method.Line, method.Column);

                // ---- pure-cache store ----
                if (method.IsPure)
                {
                    _pureMemoCache[(method.Name, [.. args])] = returnValue ?? VoidMarker.Instance;
                }

                if (flow == ControlFlow.Return)
                {
                    if (method.ReturnType == TypeInfo.Void)
                    {
                        if (returnValue != null)
                            RuntimeError($"Void method '{method.Name}' cannot return a value.", line, column);
                        return null;
                    }
                    else
                    {
                        // Same ONE rule as a free function's return (see Interpreter.InvokeScriptFunction):
                        // CoerceTo decides whether this type may hold null. The pre-check that used to sit here
                        // refused a legitimate `return null;` for a reference return type.
                        return MethodReturn(method, returnValue, line, column);
                    }
                }
                else
                {
                    if (method.ReturnType != TypeInfo.Void)
                        RuntimeError($"Method '{method.Name}' must return a value.", line, column);
                    return null;
                }
            }
            finally
            {
                CurrentFrame.Scopes.PopScope();
                _currentThis = previousThis;
                _currentClass = previousClass;
            }
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ThrowException(FriendlyExceptionMessage(ex), line, column);
            return null;
        }
        finally
        {
            _callStack.TryPop(out _);
        }
    }


    private object? InvokeMethodOnStructInstance(StructInstance instance, FuncDecl method, object?[] args, int line, int column)
    {
        var methodName = $"{instance.StructName}.{method.Name}";
        _callStack.Push(methodName);
        try
        {
            var previousThis = _currentThis;
            var previousClass = _currentClass;
            _currentThis = instance;
            _currentClass = null;

            // Task 4: a struct-method coroutine (body contains yield) must return a Generator, the
            // VM fast path below is gated off (!method.HasYield) and inline tree execution would hit
            // a `yield` outside any coroutine. Same wrap as class methods; structs have no ClassInfo.
            if (method.IsAsync)
                return BeginAsyncMethodCall(method, args, instance, null);
            if (method.IsCoroutine)
                return WrapCoroutineMethod(method, args, instance, null, line, column);

            var smHasParams = method.IsParams.Count > 0 && method.IsParams[^1];
            var paramsBindEnd = smHasParams ? method.Params.Count - 1 : method.Params.Count;
            // The same two rules the class-method path states once, for the same reasons: a LOADED method has
            // no AST for the tree to fill a default or assemble a tail with, and the VM flat call binds by
            // INDEX, so it needs the callee's own layout. Measured on this path before the fix: `W(1, 2)` for
            // `int W(int a, params int[] rest) { return a + rest.Length; }` flat-called the raw 2 into the
            // tail slot ("Property 'Length' not found on type int"), and the same method in a stripped bundle
            // was refused as bytecode-only. A genuinely missing REQUIRED parameter still raises the
            // per-parameter error below, because the fill only happens when nothing required is missing.
            var vmArgValues = args;
            if ((smHasParams || (method.IsVmShell && args.Length != method.Params.Count))
                && args.Length >= MinRequiredArgs(method))
                vmArgValues = NormalizeArgsForFlatCall(method, args, line, column);
            // VM fast path for struct methods (operators, bound methods): mirrors the class
            // fast path, body pre-compiled, `this` as trailing local, VmFallback → tree below.
            // Task 4: `!method.HasYield`, coroutine methods must not flat-swap into YIELD
            // bytecode; they fall through to the coroutine wrap below.
            // A LOADED (shell) method body is the ONE case where a ref/out signature takes this path: it has no
            // AST, so the tree walker below could only REFUSE it, and the VM handles a cell argument (deref on
            // read, write through on store). Its `out` positions are wrapped so the assignment rule still holds
            // (WrapOutArgs), which is what InitOutTracking does for a body the tree runs. A SOURCE method keeps
            // the exclusion, so which engine runs a source method does not move.
            var smShellRefOut = method.IsVmShell;
            if (method.Compiled != null && VmPathEnabled(method)
                && (method.DefaultValues == null || method.IsVmShell)
                && !method.HasYield
                && vmArgValues.Length == method.Params.Count
                && (smShellRefOut || (method.IsRefParams == null || !method.IsRefParams.Any(b => b)))
                && (smShellRefOut || (method.IsOutParams == null || !method.IsOutParams.Any(b => b))))
            {
                // Tuple parameter element names, the same rule the class path states: a VM body reads
                // its parameters from vmArgs, so the names the bind loop below attaches for the TREE
                // have to be attached here too.
                if (method.TupleParamNames is { Count: > 0 })
                    for (var tpi = 0; tpi < method.Params.Count && tpi < vmArgValues.Length; tpi++)
                        BindTupleParam(method, tpi, vmArgValues[tpi], line, column);

                // The struct context this call sets (saved above) must come back with it: the
                // restore sits in the scope's finally, which a VM-path return now precedes. A
                // VmFallback is the ONE case that must NOT restore, because the tree path below
                // runs with the context still set.
                var smFellThrough = false;
                var vmArgs = ArrayPool.Rent(method.Params.Count + 1);
                try
                {
                    // Each argument is COERCED to its declared parameter type here, through the SAME helper the
                    // flat call path uses. Vm.Execute binds what it is handed RAW (its prologue is
                    // `locals[i] = VmValue.From(args[i])`), so a method's argument skipped its declared type
                    // entirely while the identical free function applied it: measured,
                    // `double Half(double x) { return x / 2; }` as a method answered 1 for Half(3), the
                    // free function answered 1.5, and only the RETURN was coerced in the method. The tree's
                    // BindParam loop coerces too, but into the SCOPE, which a VM body never reads.
                    for (var i = 0; i < vmArgValues.Length; i++)
                        vmArgs[i] = Interpreter.CoerceFlatCallArg(method, vmArgValues[i], i, line, column);
                    vmArgs[method.Params.Count] = instance;   // "this" local (compiler indexes it after params)
                                                              // Same rule as the class-method site above.
                    var smVmArgs = vmArgs;
                    var structOutTrack = smShellRefOut
                        ? WrapOutArgs(method.IsOutParams, method.Params, ref smVmArgs, line, column)
                        : null;
                    var structCommitBaseline = VmBodyCommitted;
                    VmBodyCommitted = false;
                    object? vmResult;
                    var structCommittedHere = false;
                    try
                    {
                        vmResult = Vm.Execute(method.Compiled, smVmArgs, this, methodName);
                    }
                    finally
                    {
                        structCommittedHere = VmBodyCommitted;
                        VmBodyCommitted = structCommitBaseline || structCommittedHere;
                    }
                    if (vmResult is VmFallback && structCommittedHere)
                        RuntimeError($"Method '{method.Name}' stopped part-way through its compiled body, at an instruction the VM cannot run here. Running it on the tree from the start would repeat everything it had already done, so it is refused: this is an engine gap rather than a mistake in the script.", line, column);
                    if (vmResult is not VmFallback)
                    {
                        VmInvocations++;
                        // Same rule as the class path above: a LOADED body's `out` writes land on the wrapper
                        // cells WrapOutArgs installed, so its tracker is what answers.
                        if (structOutTrack != null)
                            VerifyOutParams(structOutTrack, method.IsOutParams, method.Params, method.Line, method.Column);
                        // The `IsPure` memo was stored by the COPY this helper replaced, so it moved here with
                        // the rest of the invocation.
                        if (method.IsPure)
                            _pureMemoCache[(method.Name, [.. args])] = vmResult ?? VoidMarker.Instance;
                        if (method.ReturnType == TypeInfo.Void)
                        {
                            if (vmResult != null)
                                RuntimeError($"Void method '{method.Name}' cannot return a value.", line, column);
                            return null;
                        }
                        return MethodReturn(method, vmResult, line, column);
                    }
                    smFellThrough = true;   // VmFallback: the tree path below continues with the context set
                }
                finally
                {
                    ArrayPool.Return(vmArgs);
                    if (!smFellThrough)
                    {
                        _currentThis = previousThis;
                        _currentClass = previousClass;
                    }
                }
            }

            CurrentFrame.Scopes.PushScope();
            try
            {
                CurrentFrame.Scopes.Define("this", instance, method.Line, method.Column);
                for (var i = 0; i < paramsBindEnd; i++)
                {
                    object? paramVal;
                    if (i < args.Length)
                        paramVal = args[i];
                    else if (method.DefaultValues != null && i < method.DefaultValues.Count && method.DefaultValues[i] != null)
                        paramVal = Evaluate(method.DefaultValues[i]!);
                    else
                        paramVal = RuntimeError<object?>($"Missing argument for parameter '{method.Params[i].Name}'.", line, column);
                    BindTupleParam(method, i, paramVal, line, column);
                    // A struct METHOD declares ref/out parameters like any other callable, so the ONE rule is asked
                    // here too: a cell on a parameter not declared by reference is REFUSED, and a declared one is
                    // BOUND as the cell (which is what writes back to the caller). A plain value keeps the previous
                    // Define path untouched, so the other caller of this helper (a struct operator, which passes
                    // values) behaves exactly as before.
                    var smIsRef = method.IsRefParams != null && i < method.IsRefParams.Count && method.IsRefParams[i];
                    var smIsOut = method.IsOutParams != null && i < method.IsOutParams.Count && method.IsOutParams[i];
                    RefuseRefCellOnByValueParam(method.Params[i].Name, paramVal, smIsRef, smIsOut, line, column, method.Params[i].Type);
                    if (smIsRef || smIsOut)
                        BindParam(method.Params[i].Name, paramVal, method.Params[i].Type, smIsRef, smIsOut, method.Line, method.Column);
                    else
                        CurrentFrame.Scopes.Define(method.Params[i].Name, paramVal, method.Line, method.Column);
                }

                // Out-parameter tracking, the same the class-method and function paths do, and asked AFTER the bind
                // loop: the tracker wraps the cell it finds under the parameter's name in the LIVE scope, which is
                // the ordering the constructor path had to learn (measured there: an assignment was refused for a
                // body that made it). Measured here: a struct method that never assigned its `out` parameter
                // returned SILENTLY and the caller's variable kept its old value, while a class method and a free
                // function both refuse.
                var outAssigned = InitOutTracking(method.IsOutParams, method.Params, method.Line, method.Column);

                // A struct METHOD's tail was never assembled here, so the parameter received the first argument
                // (measured: "Expected an array, got Int32" from inside the body). Same ONE rule as everywhere else.
                if (smHasParams)
                    CurrentFrame.Scopes.Define(method.Params[paramsBindEnd].Name,
                        AssembleParamsTail(method.Params, args, line, column), method.Line, method.Column);

                // Bytecode-only method bodies (IsVmShell) have no AST: if the VM fast path
                // produced no result (unsupported signature/VmFallback) there is no tree
                // fallback, fail clearly instead of executing an empty body.
                if (method.IsVmShell)
                    RuntimeError($"Method '{method.Name}' is bytecode-only and cannot run on the tree walker.", line, column);

                // The semantics the copy this helper replaced used, because this one had TWO defects that only its
                // other caller could hide: it treated the NORMAL `ControlFlow.Continue` as an error (a struct
                // operator body is a single `return`, which breaks before the check, so no multi-statement body had
                // ever reached it), and it accumulated the last non-null statement value, so a method ending in
                // `return null;` could yield a stale one. `flow != Continue` is the correct test: only control
                // statements divert, and a Return assigns the value it returns.
                object? returnValue = null;
                var flow = ControlFlow.Continue;
                PrebindDeclaredFunctions(method.Body);
                foreach (var stmt in method.Body)
                {
                    var result = Execute(stmt);
                    flow = result.Flow;
                    if (flow == ControlFlow.Return) { returnValue = result.Value; break; }
                    if (flow == ControlFlow.Throw)
                    {
                        var thrown = _thrownException;
                        _thrownException = null;
                        throw ThrowPayloadException(thrown, _currentLine, _currentColumn);
                    }
                    if (flow != ControlFlow.Continue)
                        RuntimeError($"Unexpected control flow in method '{method.Name}'.", line, column);
                }

                VerifyOutParams(outAssigned, method.IsOutParams, method.Params, method.Line, method.Column);

                // Same `IsPure` memo as the VM path above: it belongs to the invocation, not to a caller.
                if (method.IsPure)
                    _pureMemoCache[(method.Name, [.. args])] = returnValue ?? VoidMarker.Instance;

                if (flow == ControlFlow.Return)
                {
                    if (method.ReturnType == TypeInfo.Void)
                    {
                        if (returnValue != null)
                            RuntimeError($"Void method '{method.Name}' cannot return a value.", line, column);
                        return null;
                    }
                    // CoerceTo unconditionally, exactly like the class path and a free function: it skips a null
                    // value only for the types that may hold one, so the three tree paths agree on `return null;`.
                    return MethodReturn(method, returnValue, line, column);
                }

                if (method.ReturnType != TypeInfo.Void)
                    RuntimeError($"Method '{method.Name}' must return a value.", line, column);
                return null;
            }
            finally
            {
                CurrentFrame.Scopes.PopScope();
                _currentThis = previousThis;
                _currentClass = previousClass;
            }
        }
        catch (ScriptException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The engine's map owns the wording, like every other member-failure site (the class path beside this one
            // already did): this one passed the CLR's own sentence through, which is how a bundle reported
            // "Index was out of range ... (Parameter 'index')" to a script.
            ThrowException(FriendlyExceptionMessage(ex), line, column); return null;
        }
        finally
        {
            _callStack.TryPop(out _);
        }
    }

    /// <summary>
    /// The message for "this member EXISTS but no overload takes these arguments", which is a different question
    /// from "there is no such member" and used to be answered as if it were the same one: measured,
    /// `Math.Abs(null)` and `Math.Abs("hi")` both reported "Method 'Abs' not found on type Math with 1
    /// argument(s)", which sends a reader looking for a spelling mistake in a call whose only problem is the
    /// argument it was given.
    ///
    /// The REASON is composed by the same conversion funnel a CLR argument goes through (`CoerceArgument`), so
    /// this sentence cannot drift from what an assignment or a script parameter says for the same fault, and the
    /// null case reads exactly <see cref="NullNotAssignableText"/>. When NO candidate even has this arity, "not
    /// found" is the truthful answer and is kept.
    /// </summary>
    private string NoOverloadText(Type actualType, string name, object?[] args, BindingFlags flags, int line, int column)
    {
        var arity = args.Length;
        var offered = new List<string>(4);
        // A reflected parameter may be UNNAMED (`cp[i].Name` is then null), so the tuple's first slot accepts null: the
        // message interpolates it, and an empty name reads better than a nullability warning nobody acts on.
        (string? Param, string Type, string Reason)? refusal = null;
        foreach (var candidate in actualType.GetMethods(flags))
        {
            if (!string.Equals(candidate.Name, name, Comparison)) continue;
            var cp = candidate.GetParameters();
            var takesArity = cp.Length == arity
                || (cp.Length > 0 && cp[^1].IsDefined(typeof(ParamArrayAttribute), false) && arity >= cp.Length - 1);
            if (!takesArity) continue;
            var spelled = "(" + string.Join(", ", cp.Select(p => ReportedTypeName(p.ParameterType))) + ")";
            if (!offered.Contains(spelled)) offered.Add(spelled);
            if (refusal != null) continue;
            // The FIRST candidate of this arity explains WHY, but only a member with ONE candidate gets to say it:
            // naming `parameter 'value' expects short` for `Math.Abs(null)` (seven candidates, every one refusing)
            // is truthful about the refusal and arbitrary about the overload, so a member with several candidates
            // lists what it TAKES and claims no more than that.
            for (var i = 0; i < cp.Length && i < args.Length; i++)
            {
                if (cp[i].ParameterType.IsByRef) continue;
                try
                {
                    CoerceArgument(args[i], cp[i].ParameterType, line, column);
                }
                catch (ScriptException ex)
                {
                    refusal = (cp[i].Name, ReportedTypeName(cp[i].ParameterType), ex.Message);
                    break;
                }
            }
        }
        if (offered.Count == 0)
            return $"Method '{name}' not found on type {ReportedTypeName(actualType)} with {arity} argument(s).";
        if (offered.Count == 1 && refusal is { } only)
            return $"Method '{name}' has no overload that takes these arguments: parameter '{only.Param}' expects "
                + $"{only.Type}, and {only.Reason}";
        var given = "(" + string.Join(", ", args.Select(a => a == null ? "null" : ReportedTypeName(a))) + ")";
        return $"Method '{name}' has no overload for {given}: it takes {string.Join(", ", offered)}.";
    }

    /// <summary>
    /// The declarations a script bound to ONE free-function name when their signatures DIFFER: a free function IS
    /// an overload set here, as in C#. A declaration whose signature MATCHES an existing member REPLACES it (what
    /// a REPL line re-typed and a re-run of an edited file need), and one that differs joins the set, so
    /// `string Ar(int)` beside `string Ar(int, int)` keeps both and each call resolves by argument types.
    /// </summary>
    internal sealed class ScriptOverloadSet
    {
        internal readonly List<ScriptFunction> Members = [with(4)];

        internal void AddOrReplace(ScriptFunction fn)
        {
            for (var i = 0; i < Members.Count; i++)
            {
                var prior = Members[i].Declaration;
                if (prior != null && fn.Declaration != null && SameSignature(prior, fn.Declaration))
                {
                    Members[i] = fn;
                    return;
                }
            }
            Members.Add(fn);
        }

        /// <summary>The candidates as a script can write them, for a refusal that names what the name holds.
        /// A by-reference position is spelled with its keyword: without it `F(int)` and `F(ref int)` printed the
        /// same `(int)` twice, so a refusal that lists the candidates said the two were identical.</summary>
        internal string Spell() => string.Join(", ", Members.Select(m => m.Declaration == null
            ? "?"
            : "(" + string.Join(", ", m.Declaration.Params.Select((p, i) =>
                (IsByRefPosition(m.Declaration, i) ? "ref " : "")
                + (p.Type.TypeName ?? TypeSystem.FromClrName(p.Type.BaseType.ToString())))) + ")"));

        /// <summary>True when this declaration takes parameter `index` by reference, `ref` or `out` (the two share
        /// every rule that matters here: the caller must pass a variable, and the callee writes through it).</summary>
        internal static bool IsByRefPosition(FuncDecl d, int index)
            => (index < d.IsRefParams.Count && d.IsRefParams[index])
               || (d.IsOutParams != null && index < d.IsOutParams.Count && d.IsOutParams[index]);

        /// <summary>The first member, for the places that only need ONE declaration to answer a question.</summary>
        internal ScriptFunction First => Members[0];
    }

    /// <summary>How much a candidate LOSES for a by-reference position whose keyword the call site did not write,
    /// or the other way round. It is a penalty rather than a rejection because the call site can still be
    /// reported by the BIND, with the sentence a script can act on ("must be passed with the 'ref' keyword"),
    /// which is the answer a name holding only a by-value member has always given. It only has to lose against a
    /// candidate that AGREES, which is what `F(ref n)` needs to reach `F(ref int)` in a set that holds both.</summary>
    private const int RefAffinityPenalty = 64;

    /// <summary>
    /// The ONE free-function overload picker, used by both engines: candidates by ARITY (through the same
    /// `MinRequiredArgs` and `params` tail the arity refusal uses, so a defaulted or variadic member is not
    /// skipped), then `ArgMatchDistance` per position, lowest total wins and a tie keeps the FIRST declaration.
    /// That is the class-method rule (`ResolveMethod`) applied to a name, which is the point: one rule, two
    /// receivers.
    ///
    /// THREE SHAPES ARE WEIGHED HERE RATHER THAN SKIPPED, each measured as a member a call could never select:
    ///  * a NAMED call maps its arguments onto EACH candidate's parameter list, which is C#'s rule, so the member
    ///    the names fit is the one chosen. Before this the pick ran on positional order and the names were applied
    ///    to whatever won: `P(b: 1, a: "s")` against `P(int, string)`/`P(string, int)` refused from a compiled body
    ///    with the other member's coercion error (`Cannot convert string 's' to int`) and answered the other
    ///    member's text from a walked one.
    ///  * a `ref`/`out` position arrives as a CELL (the tree) or as a value plus the call site's flag (the VM, see
    ///    `ArgFlagsMarker`), so the distance is asked about the value the argument holds, and a candidate whose
    ///    by-reference-ness AGREES with the call site outscores one whose does not. Before this an overload set with
    ///    a `ref` member answered `No overload ... that fit` for EVERY call, on both engines.
    ///  * a `params` TAIL is weighed against the ELEMENT type (or against the array itself, which C# also allows);
    ///    weighing the tail's arguments against the ARRAY parameter rejected every variadic member (`S(1, 2, 3)`
    ///    against `S(params int[])` beside `S(int, int)`).
    /// </summary>
    internal ScriptFunction PickOverload(ScriptOverloadSet set, object?[] args, string name, int line, int column,
        IReadOnlyList<string?>? argNames = null, IReadOnlyList<bool>? refFlags = null)
    {
        var named = false;
        if (argNames != null)
            foreach (var n in argNames) if (n != null) { named = true; break; }

        ScriptFunction? best = null;
        var bestScore = int.MaxValue;
        foreach (var member in set.Members)
        {
            var d = member.Declaration;
            if (d == null) continue;
            if (!ArityFits(d, args.Length)) continue;

            int[]? map = null;
            if (named)
            {
                map = Interpreter.MapArgsToParams(args.Length, argNames, d.Params, out _, out _);
                if (map == null) continue;   // these names do not fit THIS candidate; another may
            }

            var score = 0;
            var ok = true;
            for (var i = 0; i < args.Length; i++)
            {
                var slot = map != null ? map[i] : i;
                var inTail = HasParamsTail(d) && slot >= d.Params.Count - 1;
                var paramIndex = slot < d.Params.Count ? slot : d.Params.Count - 1;
                if (paramIndex < 0) { ok = false; break; }
                var isCell = args[i] is RefCell;
                var value = isCell ? ((RefCell)args[i]!).Get() : args[i];
                var distance = inTail
                    ? TailArgumentDistance(value, d.Params[paramIndex].Type)
                    : ArgMatchDistance(value, d.Params[paramIndex].Type);
                if (distance < 0) { ok = false; break; }
                var callSaysRef = (refFlags != null && i < refFlags.Count && refFlags[i]) || isCell;
                if (inTail || ScriptOverloadSet.IsByRefPosition(d, paramIndex) != callSaysRef)
                    distance += RefAffinityPenalty;
                score += distance;
            }
            if (!ok || score >= bestScore) continue;
            bestScore = score;
            best = member;
        }
        if (best != null) return best;
        // No candidate was chosen. A MISTYPED or REPEATED NAME is the caller's first thing to fix, and it is the one
        // fault the arity test cannot explain: `P(c: 1)` against `(int, string)`/`(string, int)` reads "no overload
        // that takes 1 argument" when the real mistake is that no parameter is called `c`. TooMany is DELIBERATELY
        // not reported here, because a wrong count is a different question and the sentence below answers it.
        if (named && set.Members.Count > 0
            && MapArgsToParams(args.Length, argNames, set.Members[0].Declaration?.Params ?? [], out var offending,
                out var fault) == null
            && fault is NamedArgFault.UnknownName or NamedArgFault.DuplicateName)
            RefuseNamedArgFault(fault, offending, line, column);
        throw new ScriptException(ErrorCode.Unspecified,
            $"No overload of '{name}' takes {args.Length} argument(s) that fit: the name holds {set.Spell()}.",
            line, column);
    }

    /// <summary>Whether this declaration ends in a `params` tail: the one shape whose last parameter stands for
    /// every argument from its position on.</summary>
    private static bool HasParamsTail(FuncDecl d) => d.IsParams is { Count: > 0 } flags && flags[^1];

    /// <summary>The distance for one argument that fills a `params` tail: the ARRAY itself when the caller passed
    /// one (C# allows both spellings), otherwise an element of it.</summary>
    private int TailArgumentDistance(object? value, TypeInfo tailType)
    {
        var direct = ArgMatchDistance(value, tailType);
        if (direct >= 0) return direct;
        return ArgMatchDistance(value, tailType with { ArrayRank = 0, JaggedDepth = 0 });
    }

    /// <summary>Arity compatibility for the picker: a missing trailing argument may take its compiled default, and
    /// a `params` tail absorbs the rest, which is the rule <see cref="CheckScriptCallArity"/> states.</summary>
    private static bool ArityFits(FuncDecl d, int argCount)
    {
        if (argCount < MinRequiredArgs(d)) return false;
        if (argCount <= d.Params.Count) return true;
        return d.IsParams is { Count: > 0 } flags && flags[^1];
    }

    /// <summary>The overload set a free-function NAME holds, or null. The VM's `CALL` arm asks this after
    /// `ResolveGlobalFunction` answers null, because a set is not a single `ScriptFunction`.</summary>
    internal ScriptOverloadSet? ResolveGlobalOverloadSet(string name)
        => ResolveGlobalValue(name) as ScriptOverloadSet;
}
