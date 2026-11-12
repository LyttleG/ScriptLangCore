namespace ScriptLangCore;

/// <summary>
/// Immutable snapshot of all built-in registration data, captured once on first
/// interpreter construction. Shared across all Interpreter instances (main + module).
/// Eliminates ~250 allocations per Reset() and per module-interpreter construction.
/// </summary>
internal static class BuiltinBlueprint
{
    internal static readonly Dictionary<string, object?> ScopeEntries = [];
    internal static readonly HashSet<string> PerInstanceKeys = ["Server", "Client", "Script"];

    internal static IReadOnlyDictionary<string, DelegateInfo> Delegates => _delegates;
    internal static IReadOnlyDictionary<string, ClassInfo> Classes => _classes;
    internal static IReadOnlyDictionary<string, StructDeclStmt> Structs => _structs;
    internal static IReadOnlyDictionary<string, (MethodInfo Method, ParameterInfo[] Params)> MethodCache => _methodCache;
    internal static IReadOnlyDictionary<(Type Type, string Name), PropertyInfo> PropertyCache => _propertyCache;
    internal static IReadOnlyList<Type> ExtensionMethodContainers => _extMethods;

    private static readonly Dictionary<string, DelegateInfo> _delegates = [];
    private static readonly Dictionary<string, ClassInfo> _classes = [];
    private static readonly Dictionary<string, StructDeclStmt> _structs = [];
    private static readonly Dictionary<string, (MethodInfo, ParameterInfo[])> _methodCache = [];
    private static readonly Dictionary<(Type, string), PropertyInfo> _propertyCache = [];
    private static readonly List<Type> _extMethods = [with(4)];

    internal static bool Captured { get; private set; }

    /// <summary>Called once after the first interpreter's constructor finishes RegisterAllBuiltins().</summary>
    internal static void CaptureFrom(Interpreter source)
    {
        if (Captured) return;
        lock (_delegates)
        {
            if (Captured) return;

            var globalScope = source.GetCurrentGlobalScope();
            foreach (var kv in globalScope)
            {
                if (PerInstanceKeys.Contains(kv.Key)) continue;
                if (kv.Value.Value is Namespace ns) ns.IsShared = true;   // F1: shared builtin namespaces
                ScopeEntries[kv.Key] = kv.Value.Value;
            }

            foreach (var kv in source.DelegateInfos) _delegates[kv.Key] = kv.Value;
            foreach (var kv in source.Classes) _classes[kv.Key] = kv.Value;
            foreach (var kv in source.Structs) _structs[kv.Key] = kv.Value;
            foreach (var kv in source._methodCache) _methodCache[kv.Key] = kv.Value;
            foreach (var kv in source.PropertyCache) _propertyCache[kv.Key] = kv.Value;
            foreach (var t in source.ExtensionMethodContainers) _extMethods.Add(t);

            Captured = true;
        }
    }

    /// <summary>Clone blueprint into a fresh interpreter scope and dictionaries.</summary>
    internal static void CloneInto(Interpreter target, bool clearTypes = false)
    {
        // Define entries directly into the LIVE scope stack. GetCurrentGlobalScope() returns a
        // frozen copy (see ScopeStack.GetGlobalScopeDictionary), so writing through it would be
        // a no-op on the real scope, pooled module interpreters (InterpreterPool.Rent) would
        // then lack every builtin. DefineBlueprintEntries writes to the actual global scope.
        target.DefineBlueprintEntries(ScopeEntries);

        foreach (var kv in Delegates)
            target.DelegateInfos[kv.Key] = kv.Value;

        if (!clearTypes)
        {
            foreach (var kv in Classes)
                target.Classes[kv.Key] = kv.Value;
            foreach (var kv in Structs)
                target.Structs[kv.Key] = kv.Value;
        }
    }

}
