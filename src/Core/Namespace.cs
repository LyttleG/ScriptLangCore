namespace ScriptLangCore;

internal sealed class Namespace : IDisposable
{
    public string? Name { get; set; }

    /// <summary>The name a SCRIPT sees when it prints a namespace. Without this it printed the CLR type name
    /// (`ScriptLangCore.Namespace`), which is a name no script can write, and `print(Math)` answered `Math` beside
    /// it because `Math` is a CLR Type, and a Type already renders by its own name.</summary>
    public override string ToString() => string.IsNullOrEmpty(Name) ? "Namespace" : Name!;

    /// <summary>Parent namespace (back-link). Set automatically by AddChild so the F4 bare-call
    /// fallback can walk up from a leaf to the root (ancestor siblings and flat members at the
    /// bundle root). Resolution-only, ownership/disposal is unchanged (parents own children;
    /// this is a read-only reference for lookup).</summary>
    internal Namespace? Parent { get; set; }

    /// <summary>F1: true for builtin namespaces shared across interpreters (blueprint). Extending
    /// a shared namespace clones it first (copy-on-write) so the extension never leaks into
    /// other interpreters. Clones are owned (false).</summary>
    internal bool IsShared { get; set; }

    private readonly Dictionary<string, Func<object?[], object?>> _methods;
    private readonly Dictionary<string, Func<object?[], IReadOnlyList<TypeInfo>, object?>> _typedMethods;
    private readonly Dictionary<string, Namespace> _children;
    private readonly Dictionary<string, object?> _properties;
    /// <summary>Properties whose value is resolved on every read (e.g. Http.Token, which must
    /// reflect the ambient token of the *current* host request, not a captured value).</summary>
    private readonly Dictionary<string, Func<object?>> _dynamicProperties;

    /// <summary>Setters for the properties a script may WRITE (e.g. Application.CurDir, which is the process
    /// working directory: a read must be live and a write must do something, so neither a stored value nor a getter
    /// alone can express it).</summary>
    private readonly Dictionary<string, Action<object?>> _propertySetters;

    /// <summary>The members registered through <see cref="AddMethodAsync"/>, i.e. the ones whose value is a Task a
    /// script may `await`. Dispatch never asks: the stored delegate IS the adapter, so there is one table. This is
    /// introspection (and the record a test reads to prove the door was the one used).</summary>
    private readonly HashSet<string> _asyncMethods;

    /// <summary>What an assignment to a namespace member did, asked by the ONE assign path so a namespace and its
    /// members cannot disagree: <c>Set</c> a setter ran, <c>ReadOnly</c> a DYNAMIC property has no setter (storing
    /// the value would be INVISIBLE, because the getter answers on every read), and <c>Store</c> means nothing owns
    /// the name yet, which keeps a namespace body's own members writable the way they always were.</summary>
    internal enum SetOutcome { Set, ReadOnly, Store }

    private bool _disposed;

    /// <summary>Asked when a member this namespace never registered is CALLED: the engine's own CLR fallback for a
    /// CURATED facade over a CLR type (`File` over System.IO.File, so `File.ReadAllTextAsync` works without the
    /// engine restating the whole surface). It answers false when the type has no such member, so the caller keeps
    /// its own refusal (the miss text that names what the namespace DOES hold). Supplied by the registration, not
    /// built here: the invocation path (the member-name prefix of a failure, the path rule a `File` member obeys)
    /// lives with the interpreter, and Core does not reach up.</summary>
    public delegate bool ClrMemberFallback(string name, object?[] args, int line, int column, out object? result);

    public ClrMemberFallback? ClrFallback { get; set; }

    public Namespace(IEqualityComparer<string> comparer, string? name = null)
    {
        Name = name;
        _methods = new(comparer);
        _typedMethods = new(comparer);
        _children = new(comparer);
        _properties = new(comparer);
        _dynamicProperties = new(comparer);
        _propertySetters = new(comparer);
        _asyncMethods = new(comparer);
    }

    public void AddMethod(string name, Func<object?[], object?> method)
    {
        if (_methods.ContainsKey(name))
        {
            ConsoleSafe.SetColor(ConsoleColor.Yellow);
            Console.WriteLine($"Warning: {Name ?? "?"}.{name} already exists. Overwriting.");
            ConsoleSafe.ResetColor();
        }
        _methods[name] = method;
    }

    /// <summary>THE ASYNC DOOR: a member whose work finishes LATER, so the value a script receives is the engine's
    /// <see cref="Task"/> and an `await` on it composes with the run's pump instead of parking the caller's thread
    /// inside the member. `Task.WhenAll` is registered this way, which is why calling it returns the composite at once
    /// (C#'s shape) and the WAIT is the caller's `await`/`.Wait()` rather than the call's own.
    ///
    /// WHAT STAYS SYNCHRONOUS: an argument refusal. A handler that throws before it answers a Task still surfaces at
    /// the CALL, with the call's position, because that is a mistake in the program rather than work in flight, and
    /// every refusal this engine writes is raised that way (a bad list here reads the same as a bad list anywhere).
    ///
    /// The name is recorded, so <see cref="IsAsyncMethod"/> can answer; nothing in DISPATCH treats the two doors
    /// differently, because there is one table (`AddMethod` stores the adapter, so every existing path finds it).</summary>
    public void AddMethodAsync(string name, Func<object?[], Task> handler)
    {
        AddMethod(name, args => handler(args));
        _asyncMethods.Add(name);
    }

    /// <summary>True for a member registered through <see cref="AddMethodAsync"/>: this namespace answers a Task, so a
    /// caller may `await` it. Read-only introspection; dispatch does not ask it.</summary>
    internal bool IsAsyncMethod(string name) => _asyncMethods.Contains(name);

    public void AddTypedMethod(string name, Func<object?[], IReadOnlyList<TypeInfo>, object?> method)
    {
        if (_typedMethods.ContainsKey(name))
        {
            ConsoleSafe.SetColor(ConsoleColor.Yellow);
            Console.WriteLine($"Warning: {Name ?? "?"}.{name} already exists. Overwriting.");
            ConsoleSafe.ResetColor();
        }
        _typedMethods[name] = method;
    }

    /// <summary>True if a script method with this name is registered (not a CLR instance method).</summary>
    internal bool HasMethod(string name) => _methods.ContainsKey(name);

    /// <summary>True if a typed (generic) script method with this name is registered.</summary>
    internal bool HasTypedMethod(string name) => _typedMethods.ContainsKey(name);

    /// <summary>The names this namespace answers as a CALL. A reader asking what a namespace IS wants its
    /// surface rather than one member: the JSON description used `GetProperties` and got nothing, so every
    /// namespace serialized as the CLR type name.</summary>
    internal List<string> MethodNames()
    {
        var names = new List<string>();
        foreach (var k in _methods.Keys) names.Add(k);
        foreach (var k in _typedMethods.Keys) if (!_methods.ContainsKey(k)) names.Add(k);
        return names;
    }

    /// <summary>The names it answers as VALUES: a property, a dynamic property, or the LABEL a load gave it.
    /// A name that is ALSO a method is left to <see cref="MethodNames"/>, so a script function is listed once,
    /// and the label being a property is what keeps a description from walking back into itself.</summary>
    internal List<string> PropertyNames()
    {
        var names = new List<string>();
        foreach (var k in _properties.Keys) if (!_methods.ContainsKey(k)) names.Add(k);
        foreach (var k in _dynamicProperties.Keys) if (!_methods.ContainsKey(k) && !_properties.ContainsKey(k)) names.Add(k);
        return names;
    }

    /// <summary>The names of its nested namespaces.</summary>
    internal List<string> ChildNames()
    {
        var names = new List<string>();
        foreach (var k in _children.Keys) names.Add(k);
        return names;
    }

    /// <summary>The refusal for a member this namespace does not have. It names what the namespace DOES hold,
    /// because a miss is nearly always a spelling: `Json.Nope` and a wrong alias path used to read the same way,
    /// with nothing to type next. And when the name asked for IS this namespace's own name the reader has taken
    /// the LABEL for a path segment (`Z.What.A`, where the value in `Z` already is `What`), which no lookup can
    /// resolve, so the message says so instead of leaving them to guess.</summary>
    internal string MemberMissText(string name, bool isCall)
    {
        var kind = isCall ? "Method" : "Property";
        var where = string.IsNullOrEmpty(Name) ? "" : $" '{Name}'";
        // The comparer is the interpreter's own, so the hint respects its case flag like every other lookup.
        var hint = !string.IsNullOrEmpty(Name) && _methods.Comparer.Equals(name, Name)
            ? $": '{name}' is this namespace's OWN name, not a member: the label names the namespace you already"
              + $" hold, so call through it directly, {name}.<member>"
            : "";
        var seen = new HashSet<string>(_methods.Comparer);
        foreach (var n in _methods.Keys) seen.Add(n);
        foreach (var n in _typedMethods.Keys) seen.Add(n);
        foreach (var n in _children.Keys) seen.Add(n);
        foreach (var n in _properties.Keys) seen.Add(n);
        foreach (var n in _dynamicProperties.Keys) seen.Add(n);
        var holds = "";
        if (seen.Count > 0)
        {
            const int Max = 8;
            var text = new System.Text.StringBuilder();
            var i = 0;
            foreach (var n in seen)
            {
                if (i == Max) break;
                if (i > 0) text.Append(", ");
                text.Append(n);
                i++;
            }
            holds = seen.Count > Max
                ? $" (it holds: {text}, and {seen.Count - Max} more)"
                : $" (it holds: {text})";
        }
        return $"{kind} '{name}' not found in namespace{where}{hint}{holds}";
    }

    public object? InvokeMethod(string name, object?[] args)
    {
        SessionPolicy.RefuseIfDenied(Name, name, 0, 0);
        if (_methods.TryGetValue(name, out var method)) return method(args);
        if (ClrFallback != null && ClrFallback(name, args, 0, 0, out var clrResult)) return clrResult;
        throw new ScriptException(ErrorCode.UndefinedVariable, MemberMissText(name, true));
    }

    public object? InvokeMethod(string name, object?[] args, int line, int column)
    {
        // THE SESSION'S OWN GATE, before anything runs: a capability the host denied refuses BY NAME here, with the
        // call's position, rather than at each member's body (which would leave the CLR fallback and every future
        // member uncovered).
        SessionPolicy.RefuseIfDenied(Name, name, line, column);
        if (_methods.TryGetValue(name, out var method)) return method(args);
        // A curated namespace's CLR fallback, BEFORE the refusal: the engine's members win (they carry the path
        // rule and the engine's message), and everything else is looked up on the type the namespace wraps.
        if (ClrFallback != null && ClrFallback(name, args, line, column, out var clrResult)) return clrResult;
        throw new ScriptException(ErrorCode.UndefinedVariable, MemberMissText(name, true), line, column);
    }

    public object? InvokeTypedMethod(string name, object?[] args, IReadOnlyList<TypeInfo> typeArgs)
    {
        SessionPolicy.RefuseIfDenied(Name, name, 0, 0);
        return _typedMethods.TryGetValue(name, out var method)
            ? method(args, typeArgs) : throw new ScriptException(ErrorCode.UndefinedVariable, MemberMissText(name, true));
    }

    public object? InvokeTypedMethod(string name, object?[] args, IReadOnlyList<TypeInfo> typeArgs, int line, int column)
    {
        SessionPolicy.RefuseIfDenied(Name, name, line, column);
        return _typedMethods.TryGetValue(name, out var method)
            ? method(args, typeArgs) : throw new ScriptException(ErrorCode.UndefinedVariable, MemberMissText(name, true), line, column);
    }

    public void AddChild(string name, Namespace child)
    {
        child.Parent = this;   // F4 ancestor walk needs the back-link on every child attach
        // The child's OWN name, so it can render as the name a script writes (`Com`, not the CLR type name):
        // a child's name otherwise lives only in its parent's key, and `print(Com)` answered
        // `ScriptLangCore.Namespace`. First write wins, so an alias or a later re-attach cannot rename it.
        if (string.IsNullOrEmpty(child.Name)) child.Name = name;
        _children[name] = child;
    }

    public Namespace? GetChild(string name)
    {
        _children.TryGetValue(name, out var child);
        return child;
    }

    public void AddProperty(string name, object? value)
        => _properties[name] = value;

    /// <summary>Adds a property whose value is computed on every read.</summary>
    public void AddDynamicProperty(string name, Func<object?> provider)
        => _dynamicProperties[name] = provider;

    /// <summary>Adds a property a script may READ and WRITE: the getter runs on every read, and the setter is what an
    /// assignment runs, so a write is never a stored value the getter would shadow.</summary>
    public void AddWritableProperty(string name, Func<object?> get, Action<object?> set)
    {
        _dynamicProperties[name] = get;
        _propertySetters[name] = set;
    }

    /// <summary>Runs an assignment to a member this namespace owns, in the one order that cannot hide a write: a
    /// SETTER first, then the refusal for a DYNAMIC property that has none (storing it would be invisible, because its
    /// getter answers on every read), and <see cref="SetOutcome.Store"/> for a name nothing registered, which keeps a
    /// namespace body's own members writable the way they always were.</summary>
    internal SetOutcome TrySetProperty(string name, object? value)
    {
        if (_propertySetters.TryGetValue(name, out var set))
        {
            set(value);
            return SetOutcome.Set;
        }
        if (_dynamicProperties.ContainsKey(name)) return SetOutcome.ReadOnly;
        return SetOutcome.Store;
    }

    public object? GetProperty(string name)
    {
        if (_dynamicProperties.TryGetValue(name, out var provider)) return provider();
        _properties.TryGetValue(name, out var value);
        return value;
    }

    /// <summary>F1: deep copy used for copy-on-write when a script extends a shared builtin
    /// namespace. Members (methods/typed methods/properties) share their values; children are
    /// cloned recursively so the whole subtree is owned by the declaring interpreter and can be
    /// disposed without touching the shared original.</summary>
    internal Namespace Clone()
    {
        var copy = new Namespace(_methods.Comparer, Name);
        foreach (var kv in _methods) copy._methods[kv.Key] = kv.Value;
        foreach (var kv in _typedMethods) copy._typedMethods[kv.Key] = kv.Value;
        foreach (var kv in _properties) copy._properties[kv.Key] = kv.Value;
        foreach (var kv in _dynamicProperties) copy._dynamicProperties[kv.Key] = kv.Value;
        foreach (var kv in _propertySetters) copy._propertySetters[kv.Key] = kv.Value;
        foreach (var n in _asyncMethods) copy._asyncMethods.Add(n);
        foreach (var kv in _children)
        {
            var childCopy = kv.Value.Clone();
            childCopy.Parent = copy;
            copy._children[kv.Key] = childCopy;
        }
        // FIX: cloned roots are always scope roots (EnterNamespaceRoot clones SHARED builtins
        // and binds them in the scope), so carrying the original's Parent over would let the F4
        // ancestor walk escape the cloned subtree into the original shared tree. Children are
        // re-parented to `copy` above (the caller/loop does the binding).
        copy.Parent = null;
        return copy;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Children are owned by their parent namespace, recurse.
        // Property VALUES are NOT owned by the namespace: scripts can store
        // shared objects there (class instances, COM objects, libraries, even
        // the interpreter itself via "Script"), so they must never be disposed
        // as a side effect of disposing the namespace.
        if (_children.Values.Count > 0)
        {
            foreach (var child in _children.Values)
            {
                child._methods.Clear();
                child._typedMethods.Clear();
                child._children.Clear();
                child._properties.Clear();
                child.Dispose();
            }

            _children.Clear();
        }

        _disposed = true;
    }
}
