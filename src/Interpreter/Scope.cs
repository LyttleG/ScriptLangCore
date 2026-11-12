namespace ScriptLangCore;

public sealed record ClosureSnapshot(
    IReadOnlyList<Dictionary<string, object?>> LocalScopes,
    IReadOnlyList<HashSet<string>> LocalConsts,
    Dictionary<string, object?> GlobalScope,
    HashSet<string> GlobalConsts
);

public record struct GlobalVarInfo(string Name, string? Type, int Line, int Column);

internal sealed record ScopeStack
{
    // Global lock for concurrent access to scope stack operations
    internal static readonly Lock GlobalScopeLock = new();

    internal readonly record struct VariableInfo(object Value, int Line, int Column, string? TypeName = null);

    internal readonly Stack<Dictionary<string, VariableInfo>> Scopes = new();
    public Dictionary<string, VariableInfo> GetFirstScopeDictionary() => Scopes.Peek();
    private readonly Stack<HashSet<string>> _constScopes = new();
    /// <summary>Dispose-on-exit callbacks registered by `using var x = expr;`, drained (reverse order)
    /// when the enclosing scope pops. C# declaration-form using semantics.</summary>
    private readonly Stack<List<Action>> _disposeOnExit = new();
    private ConcurrentDictionary<string, VariableInfo>? _writableGlobalScope;
    /// <summary>Reference to the actual root global scope dictionary (not a copy). Writes go here too.</summary>
    private Dictionary<string, VariableInfo>? _actualGlobalScope;
    private readonly StringComparer _comparer;
    private readonly StringComparison _comparison;
    internal ClosureSnapshot? ParentSnapshot;

    /// <summary>Lightweight save, captures references to scope dictionaries without copying them.
    /// Safe because the coroutine is suspended and nobody modifies its scope stack.</summary>
    internal SavedScopeStack SaveLiveStack()
    {
        return new SavedScopeStack
        {
            Scopes = [.. Scopes],
            ConstScopes = [.. _constScopes],
            DisposeOnExit = [.. _disposeOnExit],
            WritableGlobalScope = _writableGlobalScope,
            ParentSnapshot = ParentSnapshot
        };
    }

    internal void RestoreLiveStack(in SavedScopeStack saved)
    {
        lock (GlobalScopeLock)
        {
            Scopes.Clear();
            _constScopes.Clear();
            _disposeOnExit.Clear();
            // Scopes.ToArray() on a Stack is top-first (LIFO), push in reverse so the
            // original bottom->top order is restored. Without this, every coroutine
            // resume reversed the scope stack, scrambling nested-scope shadowing.
            for (var i = saved.Scopes.Length - 1; i >= 0; i--)
                Scopes.Push(saved.Scopes[i]);
            for (var i = saved.ConstScopes.Length - 1; i >= 0; i--)
                _constScopes.Push(saved.ConstScopes[i]);
            for (var i = saved.DisposeOnExit.Length - 1; i >= 0; i--)
                _disposeOnExit.Push(saved.DisposeOnExit[i]);
            _writableGlobalScope = saved.WritableGlobalScope;
            ParentSnapshot = saved.ParentSnapshot;
        }
    }

    public struct SavedScopeStack
    {
        public Dictionary<string, VariableInfo>[] Scopes;
        public HashSet<string>[] ConstScopes;
        public List<Action>[] DisposeOnExit;
        public ConcurrentDictionary<string, VariableInfo>? WritableGlobalScope;
        public ClosureSnapshot? ParentSnapshot;
    }

    public ScopeStack(StringComparer comparer)
    {
        _comparer = comparer;
        _comparison = _comparer == StringComparer.Ordinal
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        PushScope();
    }

    public void SetWritableGlobalScope(ConcurrentDictionary<string, VariableInfo> globalScope, Dictionary<string, VariableInfo>? actualScope = null)
    {
        _writableGlobalScope = globalScope;
        _actualGlobalScope = actualScope;
    }

    public void SetParentSnapshot(ClosureSnapshot? snapshot)
    {
        ParentSnapshot = snapshot;
    }

    internal Dictionary<string, VariableInfo> GetCurrentScopeDictionary() => Scopes.Peek();
    internal HashSet<string> GetCurrentConstSet() => _constScopes.Peek();

    public void PushScope()
    {
        lock (GlobalScopeLock)
        {
            Scopes.Push(new Dictionary<string, VariableInfo>(_comparer));
            _constScopes.Push([with(_comparer)]);
            _disposeOnExit.Push([]);
        }
    }

    public void PopScope()
    {
        lock (GlobalScopeLock)
        {
            // using-var disposal: reverse registration order (innermost declared first)
            if (_disposeOnExit.TryPop(out var disposers))
                for (var i = disposers.Count - 1; i >= 0; i--)
                    disposers[i]();
            Scopes.Pop();
            _constScopes.Pop();
        }
    }

    /// <summary>Registers a dispose callback to run when the current scope exits (`using var x = ...`).</summary>
    public void RegisterDisposeOnExit(Action disposer)
    {
        lock (GlobalScopeLock)
        {
            if (_disposeOnExit.Count > 0)
                _disposeOnExit.Peek().Add(disposer);
        }
    }

    /// <summary>Runs the TOP scope's dispose-on-exit callbacks (its `using var` declarations) and clears them,
    /// WITHOUT popping the scope: the callbacks are done, the scope's variables are not. Called when a RUN ends,
    /// which is the scope exit a top-level `using var` has. Measured before this: neither engine disposed one, the
    /// tree registered the callback and never ran it, and the VM's wrapper works on per-scope LOCAL slots while a
    /// program body stores its top level as GLOBALS.</summary>
    public void RunDisposeOnExit()
    {
        lock (GlobalScopeLock)
        {
            if (_disposeOnExit.Count == 0) return;
            var disposers = _disposeOnExit.Peek();
            for (var i = disposers.Count - 1; i >= 0; i--)
                disposers[i]();
            disposers.Clear();
        }
    }

    public IEnumerable<(Dictionary<string, object> Scope, HashSet<string> ConstSet)> GetScopesWithConst()
    {
        var scopeList = Scopes.ToList();
        var constList = _constScopes.ToList();
        for (var i = 0; i < scopeList.Count; i++)
        {
            var plainScope = scopeList[i].ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.Value);
            yield return (plainScope, constList[i]);
        }
    }

    /// <summary>Same as GetScopesWithConst but returns the REAL (mutable) scope dictionaries and const sets.
    /// Use this when the caller needs to write back through a RefCell, not read a snapshot.</summary>
    /// <summary>Is `name` a LIVE const in any scope on the stack? The ONE statement of that question for a
    /// write, as a plain two-stack walk that allocates nothing. The guard runs on every global STORE, and the
    /// enumerable form below cost an iterator state machine per write (measured: 128 B of the 216 B a global
    /// write pays, against 88 B for a read), which is why this exists beside it rather than replacing it: a
    /// caller that needs the scopes THEMSELVES keeps the enumerable.</summary>
    internal bool HasLiveConst(string name)
    {
        using var constEnum = _constScopes.GetEnumerator();
        foreach (var scope in Scopes)
        {
            if (!constEnum.MoveNext()) break;
            if (constEnum.Current.Contains(name) && scope.ContainsKey(name)) return true;
        }
        return false;
    }

    internal IEnumerable<(Dictionary<string, VariableInfo> Scope, HashSet<string> ConstSet)> GetScopesWithConstLive()
    {
        // The caller only WALKS this, so it must not copy either stack. Two ToList() calls per enumeration is
        // what a global WRITE paid, because the store path runs the const guard on every one: measured, a call
        // that writes a global cost 560 B against 128 B for one that does not. Same parallel walk as the
        // const lookup above (the two stacks hold one entry each per scope).
        using var constEnum = _constScopes.GetEnumerator();
        foreach (var scope in Scopes)
        {
            if (!constEnum.MoveNext()) break;
            yield return (scope, constEnum.Current);
        }
    }

    public void Define(string name, object? value, int line = 0, int column = 0)
    {
        // A NAMESPACE bound to a name LEARNS that name, so it can render as the name a script writes: without it
        // `print(Com)` answered the CLR type name (`ScriptLangCore.Namespace`), which is a name no script can
        // write, while `print(Math)` answered `Math` because `Math` is a CLR Type and a Type renders by name. A
        // builtin namespace is bound here (`Com`, `File`, `Http`), which is why AddChild's own copy is not
        // enough on its own. First write wins, so a re-bind under an alias cannot rename it.
        if (value is Namespace ns && string.IsNullOrEmpty(ns.Name)) ns.Name = name;
        var peek = Scopes.Peek();
        peek[name] = new VariableInfo(value!, line, column);
    }

    public void DefineConst(string name, object? value, int line = 0, int column = 0)
    {
        Define(name, value, line, column);
        _constScopes.Peek().Add(name);
    }

    public object Get(string name)
    {
        if (TryGet(name, out var value))
            return value!;
        throw new ScriptException(ErrorCode.UndefinedVariable, $"Undefined variable '{name}'.");
    }

    public bool TryGetVariableInfo(string name, out VariableInfo info)
    {
        foreach (var scope in Scopes)
        {
            if (scope.TryGetValue(name, out info))
                return true;
        }
        var snapshot = ParentSnapshot;
        while (snapshot != null)
        {
            foreach (var localScope in snapshot.LocalScopes)
            {
                if (localScope.TryGetValue(name, out var val))
                {
                    info = new VariableInfo(val!, 0, 0);
                    return true;
                }
            }
            if (snapshot.GlobalScope.TryGetValue(name, out var gVal))
            {
                info = new VariableInfo(gVal!, 0, 0);
                return true;
            }
            break;
        }
        info = default;
        return false;
    }

    public bool TryGet(string name, out object? value)
    {
        var result = TryGetInternal(name, out value, 0);
        return result;
    }

    private bool TryGetInternal(string name, out object? value, int depth)
    {
        foreach (var scope in Scopes)
        {
            if (!scope.TryGetValue(name, out var info)) continue;
            value = info.Value;
            return true;
        }

        var snapshot = ParentSnapshot;
        if (snapshot != null)
        {
            foreach (var localScope in snapshot.LocalScopes)
            {
                if (localScope.TryGetValue(name, out value))
                    return true;
            }
            if (snapshot.GlobalScope.TryGetValue(name, out value))
                return true;
        }

        value = null;
        return false;
    }

    public bool TryGetWithKey(string name, out string matchedKey, out object? value)
    {
        foreach (var kvp in Scopes.SelectMany(scope => scope.Where(kvp => _comparer.Equals(kvp.Key, name))))
        {
            matchedKey = kvp.Key;
            value = kvp.Value.Value;
            return true;
        }

        var snapshot = ParentSnapshot;
        while (snapshot != null)
        {
            foreach (var localScope in snapshot.LocalScopes)
            {
                foreach (var kvp in localScope.Where(kvp => _comparer.Equals(kvp.Key, name)))
                {
                    matchedKey = kvp.Key;
                    value = kvp.Value;
                    return true;
                }
            }
            foreach (var kvp in snapshot.GlobalScope.Where(kvp => _comparer.Equals(kvp.Key, name)))
            {
                matchedKey = kvp.Key;
                value = kvp.Value;
                return true;
            }
            break;
        }

        matchedKey = name;
        value = null;
        return false;
    }

    public void Set(string name, object? value, int line = 0, int column = 0)
    {
        // Walk both stacks in parallel without ToList() allocation
        using var scopeEnum = Scopes.GetEnumerator();
        using var constEnum = _constScopes.GetEnumerator();
        while (scopeEnum.MoveNext() && constEnum.MoveNext())
        {
            if (!scopeEnum.Current.ContainsKey(name)) continue;
            // Membership, not substring. The set holds the exact names this scope made constant
            // (imported symbols and `const` declarations), so the test must be set lookup. It used
            // to be `constEnum.Current.Any(set => set.Contains(name, _comparison))`, and since
            // _constScopes holds HashSet<string>, that lambda tested string containment, freezing
            // every name that merely OCCURRED INSIDE an imported one: after any import, a
            // single-letter variable like `i` could not be assigned at all, while `zz` could.
            if (constEnum.Current.Contains(name))
            {
                var errInfo = new ScriptErrorInfo(ErrorCode.ConstAssignment, $"Cannot assign to constant variable '{name}'");
                throw new ScriptException(ErrorCode.ConstAssignment, errInfo, line, column);
            }
            var oldType = scopeEnum.Current[name].TypeName;
            scopeEnum.Current[name] = new VariableInfo(value!, line, column, oldType);
            return;
        }

        var snapshot = ParentSnapshot;
        while (snapshot != null)
        {
            if (snapshot.LocalScopes.Any(localScope => localScope.ContainsKey(name)))
            {
                lock (GlobalScopeLock)
                {
                    var peekScope = Scopes.Peek();
                    peekScope[name] = peekScope.TryGetValue(name, out var peekPrior)
                        ? RewriteKeepingType(peekPrior, value!, line, column)
                        : new VariableInfo(value!, line, column);
                }
                // The peek scope is the GLOBAL one on this path (a name a nested block wrote through the global scope),
                // so the VM's cache stamp moves with it, for the same reason `SetGlobalValueDirect` announces its own.
                Interpreter.BumpGlobalWriteStamp();
                return;
            }
            if (snapshot.GlobalScope.ContainsKey(name))
            {
                if (_writableGlobalScope != null)
                {
                    lock (GlobalScopeLock)
                    {
                        _writableGlobalScope[name] = _writableGlobalScope.TryGetValue(name, out var wrPrior)
                            ? RewriteKeepingType(wrPrior, value!, line, column)
                            : new VariableInfo(value!, line, column);
                        if (_actualGlobalScope != null)
                            _actualGlobalScope[name] = _actualGlobalScope.TryGetValue(name, out var actPrior)
                                ? RewriteKeepingType(actPrior, value!, line, column)
                                : new VariableInfo(value!, line, column);
                        // NOTE: intentionally NO Scopes.Peek() write here. Polluting the current local scope
                        // shadows the global on the NEXT write (Set's first loop finds the name locally and
                        // returns without reaching the global branch), dropping a "write -> read -> write"
                        // second write. Reads already resolve the new value via snapshot.GlobalScope
                        // (TryGetInternal L226); writable/actual globals keep top-level reads correct.
                        // Write-through to the closure snapshot copy, the read path prefers it over the
                        // live global, so a write inside a nested block (popped on exit) must reach it too
                        snapshot.GlobalScope[name] = value;
                    }
                    Interpreter.BumpGlobalWriteStamp();
                }
                else
                {
                    lock (GlobalScopeLock)
                    {
                        // No writable global linked, module functions run against their own
                        // module snapshot (InvokeScriptFunction skips the caller's writable root
                        // for them). Route the write into the snapshot's shared GlobalScope dict
                        // (the module's ModuleGlobals) so it persists across calls; reads already
                        // resolve it via TryGetInternal's snapshot lookup. Deliberately NO
                        // Scopes.Peek() write here: a local-scope copy would shadow the global on
                        // the next write (Set's first loop early-returns there), dropping the
                        // write-through on a "write → read → write" sequence, the same reasoning
                        // as the writable-global branch above.
                        snapshot.GlobalScope[name] = value;
                    }
                    Interpreter.BumpGlobalWriteStamp();
                }
                return;
            }
            break;
        }

        throw new ScriptException(ErrorCode.UndefinedVariable, $"Undefined variable '{name}'.");
    }

    public IReadOnlyList<Dictionary<string, object>> CaptureSnapshot()
    {
        return
        [
            .. Scopes.Reverse().Select(scope => scope.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value, _comparer))
        ];
    }

    public IReadOnlyList<HashSet<string>> CaptureConstSnapshot() => [.. _constScopes.Reverse()];
    public int Count => Scopes.Count;

    public ClosureSnapshot CaptureClosureSnapshotWithGlobal(Dictionary<string, VariableInfo> realGlobalScope, List<string>? capturedNames = null)
    {
        var allScopes = Scopes.ToList();
        var allConsts = _constScopes.ToList();

        var globalScopeDict = new Dictionary<string, object?>(0);
        // Global scope is NOT copied into the closure snapshot.
        // RestoreClosureSnapshot will use the interpreter's live global scope instead.
        // This eliminates the ~236 B/closure allocation from copying the entire global scope.
        HashSet<string>? globalConsts = null;

        var localScopes = new List<Dictionary<string, object?>>();
        var localConsts = new List<HashSet<string>>();

        // THE INDEX ORDER IS INNERMOST-FIRST (`Stack.ToList()` enumerates the top first) and the LAST entry is the
        // GLOBAL scope, which is deliberately not copied. So the local scopes are `0 .. Count - 2`, and the old
        // bounds were inverted: `1 .. Count - 1` SKIPPED the innermost scope and captured the GLOBAL one. Measured on
        // the Excel sample, where a `void Line(...)` in a top-level `if` reads that block's `report`:
        // `[p2] captured=report scopes=2 [0:4+ | 1:287-]` (scope 0 is the block and HOLDS the name, scope 1 is the
        // global), and `snapLocals=1 hasReport=False`, i.e. the snapshot carried the global and lost the block. That
        // is why a closure declared in a block could not see its own block's locals on either engine.
        for (var i = 0; i < allScopes.Count - 1; i++)
        {
            if (capturedNames != null)
            {
                // Only copy variables that are actually captured
                var scopeDict = new Dictionary<string, object?>(capturedNames.Count, _comparer);
                foreach (var name in capturedNames)
                    if (allScopes[i].TryGetValue(name, out var vi))
                    {
                        // A closure taking a name whose declaration has NOT run yet (C# hoisting) is recorded, because
                        // that declaration then keeps the tree: a compiled body would have no slot for a capture that
                        // only exists later.
                        if (vi.Value is ForwardFunctionSlot forwarded && forwarded.Value is null) forwarded.CapturedEarly = true;
                        scopeDict[name] = CloneValue(vi.Value);
                    }
                localScopes.Add(scopeDict);
            }
            else
            {
                var scopeDict = allScopes[i].ToDictionary(
                    kvp => kvp.Key,
                    kvp => CloneValue(kvp.Value.Value),
                    _comparer);
                localScopes.Add(scopeDict);
            }
            localConsts.Add([with(allConsts[i], _comparer)]);
        }

        localScopes.Reverse();
        localConsts.Reverse();

        return new ClosureSnapshot(localScopes, localConsts, globalScopeDict, globalConsts!);
    }

    private static object? CloneValue(object? value)
    {
        if (value is RefCell rc)
            return rc;
        return value;
    }

    public Dictionary<string, VariableInfo> GetGlobalScopeDictionary()
    {
        lock (GlobalScopeLock)
        {
            var scopeArray = Scopes.ToArray();
            var largest = scopeArray[0];
            for (var i = 1; i < scopeArray.Length; i++)
            {
                if (scopeArray[i].Count > largest.Count)
                    largest = scopeArray[i];
            }
            // Return a FROZEN COPY, concurrent writes by other threads won't affect this snapshot
            return new Dictionary<string, VariableInfo>(largest, _comparer);
        }
    }

    /// <summary>Returns a tuple of (frozen copy, writable reference) under a single lock acquisition.</summary>
    internal (Dictionary<string, VariableInfo> Copy, ConcurrentDictionary<string, VariableInfo> Writable, Dictionary<string, VariableInfo> Actual) GetGlobalScopePair()
    {
        lock (GlobalScopeLock)
        {
            var scopeArray = Scopes.ToArray();
            var largest = scopeArray[0];
            for (var i = 1; i < scopeArray.Length; i++)
            {
                if (scopeArray[i].Count > largest.Count)
                    largest = scopeArray[i];
            }
            return (new Dictionary<string, VariableInfo>(largest, _comparer),
                    new ConcurrentDictionary<string, VariableInfo>(largest, _comparer),
                    largest);
        }
    }

    /// <summary>Returns the actual writable global scope dictionary (not a copy).
    /// Used by SetWritableGlobalScope to link the original scope for mutation propagation.</summary>
    internal (ConcurrentDictionary<string, VariableInfo> Writable, Dictionary<string, VariableInfo> Actual) GetGlobalScopeReference()
    {
        lock (GlobalScopeLock)
        {
            var largest = Scopes.Peek();
            foreach (var scope in Scopes)
            {
                if (scope.Count > largest.Count)
                    largest = scope;
            }
            return (new ConcurrentDictionary<string, VariableInfo>(largest, _comparer), largest);
        }
    }

    /// <summary>Copy-free locked single-name lookup against the live global scope, no snapshot
    /// allocation, unlike GetGlobalScopeDictionary(). Safe because the caller only reads the
    /// VariableInfo struct (immutable) and never holds the dictionary reference.</summary>
    internal bool TryGetGlobalValue(string name, out VariableInfo info)
    {
        lock (GlobalScopeLock)
        {
            var largest = Scopes.Peek();
            foreach (var scope in Scopes)
            {
                if (scope.Count > largest.Count)
                    largest = scope;
            }
            return largest.TryGetValue(name, out info);
        }
    }

    /// <summary>Copy-free locked single-name write to the live global scope, no ConcurrentDictionary
    /// snapshot, unlike GetGlobalScopePair(). Mirrors SetGlobalValue semantics: writes through a
    /// RefCell wrapper when the slot holds one (coroutine-lambda capture promotion).</summary>
    internal void SetGlobalValueDirect(string name, object? value, int line, int column)
    {
        // Every global write is announced to the VM's cache stamp, whatever thread or interpreter it came from: a native
        // call can run script code that writes a global, and the CALLER's `LOAD_GLOBAL_FAST` cache has no other way to
        // learn about it (see `Interpreter.GlobalWriteStamp`).
        Interpreter.BumpGlobalWriteStamp();
        lock (GlobalScopeLock)
        {
            var largest = Scopes.Peek();
            foreach (var scope in Scopes)
            {
                if (scope.Count > largest.Count)
                    largest = scope;
            }
            if (largest.TryGetValue(name, out var cur) && cur.Value is RefCell rc)
            {
                rc.Set(value);
                // The write landed on the CELL's target, which may be ANOTHER global (a `ref` local or parameter bound to
                // one), so a compiled body's LOAD_GLOBAL_FAST cache for that other name is now stale. Measured:
                // `ref int rg = ref g; rg = 5;` answered `top=1 fn=5` from a bundle where the file answered 5. The VM arm
                // asks this ONE flag after the store, instead of clearing its cache on every global write.
                Interpreter.NoteGlobalWriteThroughCell();
            }
            else
                largest[name] = largest.TryGetValue(name, out var glPrior)
                    ? RewriteKeepingType(glPrior, value!, line, column)
                    : new VariableInfo(value!, line, column);
        }
    }

    /// <summary>Rewrites a binding's VALUE while KEEPING the declared type it was declared with. A typed LValue keeps its
    /// declaration across every assignment (`long? db;` stays "long?" after `db = Sqlite.OpenDb(...)`), which is what the
    /// tree's own assignment arm already does by carrying `TypeName` over; the compiled store built a FRESH record and
    /// lost it, so one variable answered two names in a single run (`typeof(db)` was "long?" and then "long").</summary>
    private static VariableInfo RewriteKeepingType(VariableInfo prior, object value, int line, int column)
        => new VariableInfo(value, line, column, prior.TypeName);

    /// <summary>Records the type a global was DECLARED with, without touching its value, and answers false when no
    /// binding of that name is live. The compiled top-level declaration cannot carry its type on the store (a bare
    /// `STORE_GLOBAL_FAST` has no room for a string), so it calls the `__declare_var__` helper after the store and lands
    /// here: the binding keeps the spelling across later assignments (`RewriteKeepingType`), exactly as the tree's
    /// `VisitVarDeclStmt` records it. Measured why it exists: `long? n = 1;` answered "long" from a stripped bundle and
    /// "long?" from the file, and `long? n;` answered "null" against "long?".</summary>
    internal bool SetGlobalTypeName(string name, string typeName)
    {
        lock (GlobalScopeLock)
        {
            var largest = Scopes.Peek();
            foreach (var scope in Scopes)
            {
                if (scope.Count > largest.Count)
                    largest = scope;
            }
            if (!largest.TryGetValue(name, out var info)) return false;
            largest[name] = info with { TypeName = typeName };
            return true;
        }
    }

    public void RestoreClosureSnapshot(ClosureSnapshot snapshot)
    {
        lock (GlobalScopeLock)
        {
            // Save the interpreter's live global scope before clearing.
            // Used when the snapshot's global scope is empty (optimization: skip copy at capture time).
            Dictionary<string, VariableInfo>? liveGlobal = null;
            if (snapshot.GlobalScope.Count == 0 && Scopes.Count > 0)
            {
                var scopesArr = Scopes.ToArray();
                liveGlobal = new Dictionary<string, VariableInfo>(scopesArr[0], _comparer);
            }

            Scopes.Clear();
            _constScopes.Clear();

            var globalInfo = (liveGlobal ?? snapshot.GlobalScope.ToDictionary(
                kvp => kvp.Key,
                kvp => new VariableInfo(kvp.Value!, 0, 0), _comparer))!;
            Scopes.Push(globalInfo);
            _constScopes.Push(snapshot.GlobalConsts);

            for (var i = snapshot.LocalScopes.Count - 1; i >= 0; i--)
            {
                var localInfo = snapshot.LocalScopes[i].ToDictionary(
                    kvp => kvp.Key,
                    kvp => new VariableInfo(kvp.Value!, 0, 0),
                    _comparer);
                Scopes.Push(localInfo);
                _constScopes.Push(snapshot.LocalConsts[i]);
            }

            PushScope();
            _writableGlobalScope = new ConcurrentDictionary<string, VariableInfo>(globalInfo, _comparer);
            _actualGlobalScope = null;
        }
    }
}
