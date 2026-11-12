namespace ScriptLangCore;

/// <summary>
/// Retention diagnostic, `print(DbgRetention())` dumps the size of every cross-run rooted
/// structure (interpreter dicts, caches, the module-interpreter pool, InterpreterManager
/// live count). Use after `.gc leak` or long sessions to see which structure grows with run
/// count and correlates with the retained bytes (kept as a permanent leak-hunting tool).
/// </summary>
internal partial class Interpreter
{
    internal string DbgRetentionDump()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"heap={GC.GetTotalMemory(true)}");
        sb.AppendLine($"Modules={Modules.Count} hashes={_moduleContentHashes.Count} Classes={Classes.Count} Delegates={DelegateInfos.Count} Structs={Structs.Count}");
        sb.AppendLine($"lambdaCache={_lambdaCache.Count} methodCache={_methodCache.Count} ctorCache={_constructorCache.Count} methOvl={_methodOverloadCache.Count} ovlCache={_overloadCache.Count} ovl1={_overloadCache1.Count} propCache={PropertyCache.Count} pureMemo={_pureMemoCache.Count}");
        sb.AppendLine($"callStack={_callStack.Count} genTemplates={_genericClassTemplates.Count} instClasses={_instantiatedClasses.Count} instFuncs={_instantiatedFuncs.Count} loopStackSp={_loopStackSp}");
        sb.AppendLine($"declNs={_declaredInNamespace.Count} nsSegs={_namespaceSegments.Count} warnedTypes=(weak-table)");
        sb.AppendLine($"cmdHistory={(int?)_commandHistory?.Count ?? -1} callbackHandles={_callbackHandles.Count} staticInit={_staticInitClasses.Count} initStatic={_initialStaticFields.Count} exitHandlers={_exitHandlers.Count} eventWaiters={_eventWaiters.Count} sleepQ={_sleepQueue.Count}");
        sb.AppendLine($"loadingModules={_loadingModules.Count} loadedLibs={_loadedLibraries.Count} udfHandles={_registeredUdfDbHandles.Count} tailCall={_tailCallArgs != null}");
        sb.AppendLine($"managerLive={InterpreterManager.LiveCount} pool={InterpreterPool.DebugPoolCount()}");
        var pooled = InterpreterPool.DebugPoolSnapshot();
        long mc = 0, pc = 0, lc = 0, pm = 0, mod = 0, hash = 0, nss = 0, dn = 0, wt = 0, cs = 0, eh = 0, cc = 0, mo = 0;
        foreach (var itp in pooled)
        {
            mc += itp._methodCache.Count; pc += itp.PropertyCache.Count; lc += itp._lambdaCache.Count;
            pm += itp._pureMemoCache.Count; mod += itp.Modules.Count; hash += itp._moduleContentHashes.Count;
            nss += itp._namespaceSegments.Count; dn += itp._declaredInNamespace.Count; // wt: weak-keyed table, count not exposed
            cs += itp._callStack.Count; eh += itp._exitHandlers.Count; cc += itp._constructorCache.Count; mo += itp._methodOverloadCache.Count;
        }
        sb.AppendLine($"poolCaches n={pooled.Length} method={mc} prop={pc} lambda={lc} pureMemo={pm} modules={mod} hashes={hash} nsSegs={nss} declNs={dn} warned={wt} callStack={cs} exitHandlers={eh} ctor={cc} methOvl={mo}");
        sb.AppendLine($"typeNameCache={DebugTypeNameCacheCount()}");
        return sb.ToString();
    }
}
