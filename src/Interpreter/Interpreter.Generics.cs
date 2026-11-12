namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The type-parameter bindings of the generic instantiation that is RUNNING, or null outside one. Set
    /// for the whole of a body by InvokeScriptFunction (with a `using`, so an exception cannot leak it) and refreshed
    /// per use by the VM's own type-position arms, which read their frame's declaration. ONE reader:
    /// ResolveTypeToClr, the chain `is`, a cast, `typeof`, `new` and IsKnownType all ask.</summary>
    internal IReadOnlyDictionary<string, TypeInfo>? ActiveTypeBindings { get; set; }

    /// <summary>Makes a generic body's bindings active, or does nothing when there are none to make active: a plain
    /// nested call must KEEP the enclosing generic's bindings in force, which is what makes a helper called from a
    /// generic body resolve that body's `T`.</summary>
    private IDisposable? PushTypeBindings(IReadOnlyDictionary<string, TypeInfo>? bindings)
    {
        if (bindings == null) return null;
        var previous = ActiveTypeBindings;
        ActiveTypeBindings = bindings;
        return new TypeBindingPopper(this, previous);
    }

    /// <summary>Instantiate a generic class by substituting type arguments.</summary>
    internal ClassInfo InstantiateGenericClass(ClassDeclStmt genericTemplate, TypeArgList typeArgs, int line, int column)
    {
        // Build substitution mapping from type parameter names to concrete types
        var typeParams = genericTemplate.TypeParameters;
        var mapping = new Dictionary<string, TypeInfo>(_comparer);
        for (var i = 0; i < typeParams.Count && i < typeArgs.Count; i++)
            mapping[typeParams[i]] = typeArgs[i];

        // Generate a unique concrete name
        // Named the way a script spells it (`Box<int>`, not `Box<Int>`) - TypeLabel renders a type exactly
        // as typeof does, so an instantiation's name now agrees with every other reported name.
        var concreteName = genericTemplate.Name + "<" + string.Join(",", typeArgs.Select(TypeLabel)) + ">";

        // Check cache
        if (_instantiatedClasses.TryGetValue(concreteName, out var cached))
            return cached;

        // Clone and substitute fields (instance fields only, static fields are cloned separately)
        var substitutedFields = genericTemplate.Fields.Where(f => !f.IsStatic).Select(f =>
        {
            var newType = f.Type.Substitute(mapping);
            // The compiled thunk (a bundle's carried initializer) comes along: without it a generic clone of a
            // loaded class would build instances that read their defaults.
            return new FieldDecl(f.Access, newType, f.Name, f.Initializer) { InitializerThunk = f.InitializerThunk };
        }).ToList();

        // Clone and substitute methods
        var substitutedMethods = genericTemplate.Methods.Select(m =>
        {
            var newParams = m.Params.Select(p => (Type: p.Type.Substitute(mapping), Name: p.Name)).ToList();
            var newReturnType = m.ReturnType.Substitute(mapping);
            var clone = FuncDecl.Create(m.Name, newParams, m.IsRefParams, m.IsParams, m.Body, newReturnType,
                m.OperatorToken, m.Access, m.Line, m.Column);
            // Carry the coroutine flags so the cloned concrete method stays gated out of the VM
            // path (HasYield) and is invoked as a Generator (IsCoroutine) exactly like the template.
            clone.IsCoroutine = m.IsCoroutine;
            clone.IsAsync = m.IsAsync;
            clone.HasYield = m.HasYield;
            return clone;
        }).ToList();

        // Clone and substitute constructors
        var substitutedCtors = genericTemplate.Constructors.Select(c =>
        {
            var newParams = c.Params.Select(p => (Type: p.Type.Substitute(mapping), Name: p.Name)).ToList();
            return new ConstructorDecl(newParams, c.IsRefParams, c.Body, c.Access, c.Line, c.Column, c.IsOutParams, IsParams: c.IsParams);
        }).ToList();

        // Clone static fields from template so each concrete type gets its own copy
        Dictionary<string, object?>? staticFields = null;
        List<FieldDecl>? staticFieldDecls = null;
        List<FuncDecl>? staticMethods = null;
        if (Classes.TryGetValue(genericTemplate.Name, out var templateCi) && templateCi.StaticFields != null)
        {
            staticFields = new Dictionary<string, object?>(templateCi.StaticFields, _comparer);
            staticFieldDecls = templateCi.StaticFieldDecls;
            staticMethods = templateCi.StaticMethods;
        }

        // Create the concrete ClassInfo
        var concreteInfo = new ClassInfo(concreteName, genericTemplate.ParentName,
            substitutedFields, substitutedMethods, substitutedCtors,
            genericTemplate.NestedClasses, genericTemplate.Events,
            genericTemplate.OperatorMethods, genericTemplate.Line, genericTemplate.Access)
        {
            Parent = null,
            Destructor = genericTemplate.Destructor,
            StaticFields = staticFields,
            StaticFieldDecls = staticFieldDecls,
            StaticMethods = staticMethods,
            TypeArguments = typeArgs
        };

        Classes[concreteName] = concreteInfo;
        _instantiatedClasses[concreteName] = concreteInfo;
        return concreteInfo;
    }

    /// <summary>Walk the body AST and substitute generic type parameters in MethodCallExpr / CallExpr TypeArgs.</summary>
    private static IReadOnlyList<Stmt> SubstituteTypeArgsInBody(IReadOnlyList<Stmt> body, Dictionary<string, TypeInfo> mapping)
    {
        if (mapping.Count == 0) return body;

        var changed = false;
        var result = new List<Stmt>(body.Count);
        result.AddRange(body.Select(stmt => SubstituteStmtTypeArgs(stmt, mapping, ref changed)));
        return changed ? result : body;
    }

    private static Stmt SubstituteStmtTypeArgs(Stmt stmt, Dictionary<string, TypeInfo> mapping, ref bool changed)
    {
        switch (stmt)
        {
            case ReturnStmt { Value: not null } ret:
                {
                    var newExpr = SubstituteExprTypeArgs(ret.Value, mapping, ref changed);
                    if (changed)
                        return ReturnStmt.Create(newExpr, ret.Line, ret.Column);
                    break;
                }
            case VarDeclStmt varDecl when varDecl.Initializer != null || varDecl.Type != null:
                {
                    var newInit = varDecl.Initializer == null
                        ? null
                        : SubstituteExprTypeArgs(varDecl.Initializer, mapping, ref changed);
                    var newType = varDecl.Type?.Substitute(mapping);
                    if (changed || newType != null && !ReferenceEquals(newType, varDecl.Type))
                    {
                        changed = true;
                        return VarDeclStmt.Create(varDecl.Name, newInit, newType, varDecl.IsConst,
                            varDecl.WasEscaped, varDecl.Line, varDecl.Column);
                    }
                    break;
                }
        }
        return stmt;
    }

    private static Expr SubstituteExprTypeArgs(Expr expr, Dictionary<string, TypeInfo> mapping, ref bool changed)
    {
        switch (expr)
        {
            case MethodCallExpr { TypeArgs.Count: > 0 } mc:
                {
                    var needsSub = false;
                    var newTypeArgs = new TypeInfo[mc.TypeArgs.Count];
                    for (var i = 0; i < mc.TypeArgs.Count; i++)
                    {
                        newTypeArgs[i] = mc.TypeArgs[i].Substitute(mapping);
                        if (!ReferenceEquals(newTypeArgs[i], mc.TypeArgs[i])) needsSub = true;
                    }

                    if (needsSub)
                    {
                        changed = true;
                        return MethodCallExpr.Create(mc.Receiver, mc.MethodName,
                            mc.Arguments, mc.IsRefArgs, mc.Line, mc.Column, newTypeArgs);
                    }
                    break;
                }

            case CallExpr { TypeArgs.Count: > 0 } ce:
                {
                    var needsSub = false;
                    var newTypeArgs = new TypeInfo[ce.TypeArgs.Count];
                    for (var i = 0; i < ce.TypeArgs.Count; i++)
                    {
                        newTypeArgs[i] = ce.TypeArgs[i].Substitute(mapping);
                        if (!ReferenceEquals(newTypeArgs[i], ce.TypeArgs[i])) needsSub = true;
                    }

                    if (needsSub)
                    {
                        changed = true;
                        return CallExpr.Create(ce.Callee, ce.Arguments, ce.IsRefArgs,
                            ce.Line, ce.Column, newTypeArgs);
                    }
                    break;
                }

            case NewInstanceExpr { TypeInfo: not null } ne:
                {
                    // e.g. new List<T>() inside a generic body, substitute the element type so the
                    // runtime collection is concretely typed (typeof reports List<int>, Add/ToArray
                    // behave unchanged). TypeInfo.Substitute handles the GenericArgs branch.
                    var sub = ne.TypeInfo.Substitute(mapping);
                    if (!ReferenceEquals(sub, ne.TypeInfo))
                    {
                        changed = true;
                        return NewInstanceExpr.Create(ne.ConstructorExpr, ne.Arguments, sub,
                            ne.Line, ne.Column, isRefArgs: ne.IsRefArgs, isOutArgs: ne.IsOutArgs,
                            argNames: ne.ArgNames);
                    }
                    break;
                }
        }

        return expr;
    }

    /// <summary>Instantiate a generic function by inferring type arguments from the call arguments.</summary>
    internal ScriptFunction? InstantiateGenericFunction(FuncDecl genericFunc, object?[] args, int line, int column,
        IReadOnlyList<TypeInfo>? explicitTypeArgs = null, bool asMethod = false, string? receiverAlias = null,
        bool skipVmBody = false)
    {
        var typeParams = genericFunc.TypeParameters;
        if (typeParams.Count == 0) return null;

        CheckGenericArity(genericFunc, args, line, column);
        var mapping = BuildGenericMapping(genericFunc, args, explicitTypeArgs);
        WarnGenericTypeMismatches(genericFunc, args, mapping, line, column);
        var key = GenericInstantiationKey(genericFunc, mapping);

        // Check cache
        if (_instantiatedFuncs.TryGetValue((genericFunc, asMethod, key, receiverAlias), out var cached))
            return MakeGenericCallable(cached, cached.CapturedNames);

        var concreteFunc = CloneGenericDeclaration(genericFunc, mapping, key, asMethod, receiverAlias, skipVmBody);
        _instantiatedFuncs[(genericFunc, asMethod, key, receiverAlias)] = concreteFunc;

        return MakeGenericCallable(concreteFunc);
    }

    private void CheckGenericArity(FuncDecl genericFunc, object?[] args, int line, int column)
    {
        var hasParams = genericFunc.IsParams.Count > 0 && genericFunc.IsParams[^1];
        if (hasParams)
        {
            if (args.Length < genericFunc.Params.Count - 1)
                RuntimeError($"Function '{genericFunc.Name}' expects at least {genericFunc.Params.Count - 1} arguments, got {args.Length}.", line, column);
        }
        else if (args.Length < genericFunc.Params.Count)
            RuntimeError($"Function '{genericFunc.Name}' expects at least {genericFunc.Params.Count} arguments, got {args.Length}.", line, column);
    }

    // The mapping from type parameter name to concrete TypeInfo: the call site's EXPLICIT arguments when it named
    // any (`Identity<long>(42)`), otherwise inferred from the argument VALUES.
    private Dictionary<string, TypeInfo> BuildGenericMapping(FuncDecl genericFunc, object?[] args,
        IReadOnlyList<TypeInfo>? explicitTypeArgs)
    {
        var typeParams = genericFunc.TypeParameters;
        var mapping = new Dictionary<string, TypeInfo>(_comparer);
        if (explicitTypeArgs != null)
        {
            // Use explicit type arguments from the call site: Identity<long>(42)
            for (var i = 0; i < typeParams.Count && i < explicitTypeArgs.Count; i++)
                mapping[typeParams[i]] = explicitTypeArgs[i];
        }
        else
        {
            // Infer type arguments from the call arguments
            for (var i = 0; i < typeParams.Count; i++)
            {
                var paramType = genericFunc.Params[i].Type;
                if (!paramType.IsGenericParameter || !string.Equals(paramType.TypeName, typeParams[i], Comparison))
                    continue;
                if (i < args.Length && args[i] != null)
                {
                    var argType = args[i]!.GetType();
                    var inferred = MapTypeInfo(argType);
                    mapping[typeParams[i]] = inferred;
                }
                else
                    mapping[typeParams[i]] = new TypeInfo(TokenType.Object, true, 0, 0, null);
            }
        }
        return mapping;
    }

    // ── Generic type-argument mismatch warning (call-site, warning only) ──
    // Each type parameter bound to a concrete TypeInfo is compared against the
    // runtime (element) type of every argument whose parameter type involves T.
    // Mirrors BindParam's IsKnownCompatibleType rule: numeric widening and object
    // passthrough stay silent. Warning only, never throws.
    private void WarnGenericTypeMismatches(FuncDecl genericFunc, object?[] args,
        Dictionary<string, TypeInfo> mapping, int line, int column)
    {
        var typeParams = genericFunc.TypeParameters;
        for (var tp = 0; tp < typeParams.Count; tp++)
        {
            if (!mapping.TryGetValue(typeParams[tp], out var bound)) continue;
            // Skip the object fallback / explicit object T (accepts anything) and
            // unresolved generic markers (T mapped to another generic parameter).
            if (bound.BaseType == TokenType.Object && bound.TypeName == null) continue;
            if (bound.IsGenericParameter) continue;

            var tName = typeParams[tp];
            for (var i = 0; i < genericFunc.Params.Count && i < args.Length; i++)
            {
                var paramType = genericFunc.Params[i].Type;
                var arg = args[i];
                if (arg == null) continue;

                TypeInfo? runtimeType;
                if (paramType.TypeName == tName && !paramType.IsArray)
                {
                    // Direct T-typed param (T x): compare the value's runtime type.
                    // Function/delegate values are unrelated to T, skip.
                    if (arg is ScriptFunction or CombinedDelegate or Delegate or BoundMethod) continue;
                    // MapTypeInfo throws on rank>1 (multi-dimensional) arrays, nothing
                    // comparable there; skip rather than turn a warning pass into a throw.
                    TypeInfo mapped;
                    try { mapped = MapTypeInfo(arg.GetType()); }
                    catch (ScriptException) { continue; }
                    runtimeType = mapped;
                }
                else if (paramType.TypeName == tName || ParamTypeReferences(paramType, tName))
                {
                    // T[] / List<T> param: compare the collection's element type.
                    runtimeType = GetRuntimeElementType(arg);
                }
                else if (paramType.TypeName is "Array" or "Iterable"
                         || (paramType.TypeName == null && paramType.BaseType == TokenType.Object))
                {
                    // Array / Iterable / object param (the 'Array items' case):
                    // compare the element type of a typed-collection argument.
                    runtimeType = GetRuntimeElementType(arg);
                }
                else
                    continue;

                if (runtimeType == null) continue;
                // Mixed/object element type, nothing reliable to compare.
                if (runtimeType.BaseType == TokenType.Object && runtimeType.TypeName == null) continue;

                if (IsTypeArgCompatible(runtimeType, bound)) continue;

                var tiName = bound.TypeName ?? TypeSystem.Name(bound.BaseType);
                var runtimeName = runtimeType.TypeName ?? TypeSystem.Name(runtimeType.BaseType);
                Logging.Log.Warning("Type mismatch",
                    $"Type argument '{tiName}' for '{tName}' does not match the runtime type '{runtimeName}' of argument {i + 1}.",
                    line, column);
            }
        }
    }

    // Generate the clone's NAME, it labels the instantiation in error messages, so it stays readable, and
    // the CACHE key, which must also carry the DECLARING declaration and the compile mode. A name alone is
    // not an identity: two same-named generic members (`Who<U>` on `List<T>` and on `HashSet<T>`) produced
    // the SAME key `Who<int>`, so the second one RAN THE FIRST ONE'S BODY, measured, `set.Who(1)` answered
    // "list" after `list.Who(1)` had cached it, a silently wrong value with no error anywhere. `asMethod`
    // belongs in the key for the same reason: it decides whether the clone's body is compiled with a `this`
    // slot (CompileEngineStyle), so the two forms are different instantiations of the same declaration.
    private string GenericInstantiationKey(FuncDecl genericFunc, Dictionary<string, TypeInfo> mapping)
    {
        return genericFunc.Name + "<" + string.Join(",", genericFunc.TypeParameters.Select(tp
            => mapping.TryGetValue(tp, out var ti) ? (ti.TypeName ?? ti.BaseType.ToString()) : tp)) + ">";
    }

    // Clone and substitute the FuncDecl: concrete parameter and return types, the substituted body, and the flags
    // an instantiation must keep (pure, coroutine, async, yield, tuple names and the type-parameter BINDINGS).
    private FuncDecl CloneGenericDeclaration(FuncDecl genericFunc, Dictionary<string, TypeInfo> mapping, string key,
        bool asMethod, string? receiverAlias, bool skipVmBody)
    {
        var clonedParams = genericFunc.Params.Select(p =>
            (Type: p.Type.Substitute(mapping), Name: p.Name)).ToList();
        var clonedReturnType = genericFunc.ReturnType.Substitute(mapping);
        var clonedBody = SubstituteTypeArgsInBody(genericFunc.Body, mapping);

        var concreteFunc = FuncDecl.Create(
            key, clonedParams, genericFunc.IsRefParams, genericFunc.IsParams, clonedBody, clonedReturnType,
            genericFunc.OperatorToken, genericFunc.Access, genericFunc.Line, genericFunc.Column,
            isOutParams: genericFunc.IsOutParams);

        concreteFunc.IsPure = genericFunc.IsPure;
        concreteFunc.IsCoroutine = genericFunc.IsCoroutine;
        concreteFunc.IsAsync = genericFunc.IsAsync;
        concreteFunc.HasYield = genericFunc.HasYield;
        // A TUPLE's element names are carried by the declaration, not by its TypeInfo, so a clone has to inherit
        // them or a loaded tuple parameter answers "Property 'x' not found on tuple" (measured: a generic function
        // taking `(int nx, int ny)` lost its names through the instantiation).
        concreteFunc.TupleParamNames = genericFunc.TupleParamNames;
        concreteFunc.TupleElementNames = genericFunc.TupleElementNames;

        // The BINDING of each type parameter travels with the clone, so a type position the substitution could not
        // rewrite still resolves it at RUN time (Interpreter.ResolveTypeToClr asks this map). The walk above is
        // deliberately not the only mechanism: it rewrites three node shapes, and measured, `o is IEnumerable<T>`
        // answered False from a return, a print, an if, a `&&`, a var initializer, a lambda and a foreach body.
        concreteFunc.TypeParamBindings = mapping;

        // The clone is CONCRETE - no type parameters survive it - so the VM can take its body, and compiling it
        // here is what lets a generic call run flat instead of always falling to the tree. CompileToVm is the
        // right gate rather than an arbitrary one: its expression whitelist refuses a body that depends on a
        // receiver (`this`), exactly the shape whose VM invocation would need a `this` local that a generic
        // method call does not pass. Those stay interpreted; every other body now runs on the VM.
        if (concreteFunc.Compiled == null)
        {
            // A LOADED template has no AST body to compile, so the clone inherits the TEMPLATE's bytecode: it is
            // declaration-independent (the locals and slots are the template's), and the only thing an
            // instantiation changes is the PARAMETER TYPES, which live on the clone's declaration and are what the
            // call site coerces against. Measured without it: `Identity<long>(42)` from a stripped bundle answered
            // 42 as an `int` even with the type parameters on the wire, and compiling the EMPTY body instead gave a
            // void one, which the caller then refused as "Cannot assign null to non-nullable type".
            concreteFunc.Compiled = skipVmBody ? null
                : genericFunc.Body.Count == 0 && genericFunc.Compiled != null
                    ? genericFunc.Compiled
                    : CompileEngineStyle(concreteFunc, asMethod, receiverAlias);
        }

        return concreteFunc;
    }

    // The ScriptFunction an instantiation hands its caller, from BOTH branches of the cache so the two cannot
    // drift. `capturedNames` is what the CACHED branch passes (the clone's own declaration records them); a fresh
    // clone captures nothing of its own, and MakeInstantiatedFunction is what says a capture-free compiled body
    // gets an empty capture array rather than null.
    private ScriptFunction MakeGenericCallable(FuncDecl decl, List<string>? capturedNames = null)
    {
        var realGlobalScope = _rootFrame.Scopes.GetGlobalScopeDictionary();
        var snapshot = capturedNames != null
            ? CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(realGlobalScope, capturedNames)
            : CurrentFrame.Scopes.CaptureClosureSnapshotWithGlobal(realGlobalScope);
        var paramTypes = GetParameterTypes(decl.Params);
        var flags = FunctionFlags.Generic
                  | (decl.IsPure ? FunctionFlags.Pure : FunctionFlags.None)
                  | (decl.IsCoroutine ? FunctionFlags.Coroutine : FunctionFlags.None);
        return MakeInstantiatedFunction(decl, snapshot, paramTypes, flags);
    }

    /// <summary>The ScriptFunction an instantiation hands its caller, from BOTH branches of the cache (so the two
    /// cannot drift). A CAPTURE-FREE clone with a compiled body gets an EMPTY capture array rather than null, and
    /// that is load-bearing: `InvokeScriptFunction`'s VM-direct gate accepts a body with no local scopes OR one whose
    /// captures are already materialized, so a clone invoked from a TREE frame that HAS local scopes fell through to
    /// the tree walker, which has no statements for a loaded body and refused "Function 'Identity&lt;Long&gt;' must
    /// return a value" (measured on `vm_nosource_test` 134, whose subject is a generic call inside a function). An
    /// empty array says what is true: this body declares no captures, so it needs no cells. A template that DOES
    /// declare captures keeps null and stays on the tree path, unchanged.</summary>
    private static ScriptFunction MakeInstantiatedFunction(FuncDecl concrete, ClosureSnapshot snapshot,
        IReadOnlyList<TypeInfo> paramTypes, FunctionFlags flags)
    {
        var fn = new ScriptFunction(concrete, snapshot, concrete.ReturnType, paramTypes, concrete.IsRefParams,
            Flags: flags, IsOutParams: concrete.IsOutParams);
        if (concrete.Compiled != null && concrete.VmCaptureNames == null && concrete.CapturedNames is not { Count: > 0 })
            fn.VmCaptures = Array.Empty<object?>();
        return fn;
    }

    /// <summary>True when the declared type's generic arguments (nested included) reference the given type parameter.</summary>
    private static bool ParamTypeReferences(TypeInfo type, string typeParam)
    {
        if (type.GenericArgs is not { Count: > 0 }) return false;
        foreach (var arg in type.GenericArgs)
        {
            if (arg.TypeName == typeParam) return true;
            if (arg.GenericArgs is { Count: > 0 } && ParamTypeReferences(arg, typeParam)) return true;
        }
        return false;
    }

    /// <summary>Extracts the comparable element TypeInfo of a collection argument, arrays and script
    /// list/hashset. Strings, dictionaries, scalars, functions, and mixed/object-element collections
    /// yield null (nothing reliable to compare).</summary>
    private static TypeInfo? GetRuntimeElementType(object arg)
    {
        // Strings are not element-typed collections, skip (a string arg to 'Array items'
        // must not warn about char vs the bound type).
        if (arg is string) return null;
        if (arg is Array arr)
        {
            var elementType = arr.GetType().GetElementType();
            if (elementType == null || elementType == typeof(object)) return null; // mixed/object elements
            try { return MapTypeInfo(elementType); }
            catch (ScriptException) { return null; } // exotic jagged-of-multidim element, nothing comparable
        }
        if (arg is IScriptList scriptList) return scriptList.ElementType;
        if (arg is IScriptHashSet scriptSet) return scriptSet.ElementType;
        return null; // dictionaries, functions, scalars, no comparable element type
    }

    /// <summary>True when the runtime type matches the bound type argument, mirroring BindParam's rule:
    /// numeric widening stays silent, object passthrough stays silent, named types must match by name.</summary>
    private bool IsTypeArgCompatible(TypeInfo runtime, TypeInfo bound)
    {
        // object accepts anything, object passthrough stays silent.
        if (bound.BaseType == TokenType.Object && bound.TypeName == null) return true;
        // Named types (script classes/structs/delegates): an exact name match is compatible.
        if (runtime.TypeName != null || bound.TypeName != null)
        {
            var rn = runtime.TypeName ?? TypeSystem.Name(runtime.BaseType);
            var bn = bound.TypeName ?? TypeSystem.Name(bound.BaseType);
            return string.Equals(rn, bn, StringComparison.OrdinalIgnoreCase);
        }
        // Primitives/arrays, BindParam's IsKnownCompatibleType rule (numeric widening silent).
        // Exact base-type equality first: IsKnownCompatibleType's switch has no arm for
        // decimal/DateTime/DateTimeOffset/TimeSpan, so a matched value would otherwise warn.
        // (Arrays fall through, their representative path in IsKnownCompatibleType handles them.)
        if (!runtime.IsArray && !bound.IsArray && runtime.BaseType == bound.BaseType) return true;
        return IsKnownCompatibleType(RepresentativeValue(runtime), bound);
    }

    /// <summary>Builds a representative value for a runtime TypeInfo so the value-based
    /// IsKnownCompatibleType rule can be reused for primitives/arrays.</summary>
    private static object RepresentativeValue(TypeInfo type)
    {
        if (type.IsArray)
        {
            var elementType = TypeTokenHelper.GetSystemType(type).GetElementType() ?? typeof(object);
            return Array.CreateInstance(elementType, 0);
        }
        return type.BaseType switch
        {
            TokenType.Int => 0,
            TokenType.Long => 0L,
            TokenType.Short => (short)0,
            TokenType.Byte => (byte)0,
            TokenType.SByte => (sbyte)0,
            TokenType.UInt => 0u,
            TokenType.UShort => (ushort)0,
            TokenType.ULong => 0ul,
            TokenType.Float => 0f,
            TokenType.Double => 0d,
            TokenType.Decimal => 0m,
            TokenType.Bool => false,
            TokenType.Char => '\0',
            TokenType.StringType or TokenType.String => "",
            TokenType.DateTime => default(DateTime),
            TokenType.DateTimeOffset => default(DateTimeOffset),
            TokenType.TimeSpan => default(TimeSpan),
            _ => new object(),
        };
    }
}
