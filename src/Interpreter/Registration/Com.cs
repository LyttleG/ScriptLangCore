namespace ScriptLangCore;

using DispatchVtbl; // ComScope, ComNode, ComAutomation

internal partial class Interpreter
{
    [ThreadStatic] private static ComScope? _currentScope;

    private void RegisterComFunctions()
    {
        var comNs = new Namespace(_comparer);

        // No Define of the wrapper types here: EVERY name the type registry knows is usable as an expression,
        // and the same in the VM, so `ComNode.Make(...)` reaches a static extension member with no per-name
        // list to keep in step. See ResolveTypeNameAsValue.

        // ── Com.Run(callback [, extraArgs...]), STA apartment + scoped disposal ──
        // The callback receives the SCOPE first, then every extra argument Com.Run was given, so a helper can
        // hand values to its COM work without relying on capture:
        //     Com.Run(fun(scope, progId, path) { var app = scope.CreateObject(progId); … }, "Excel.Application", p);
        // This is a C#-side invocation, on purpose: it is what keeps `Com.Run(fun() { … })`, a callback that
        // ignores the scope and declares no parameters, legal, even though a SCRIPT-side call of a zero-parameter
        // function with arguments is refused by the tree ("Expected between 0 and 0 arguments"). Before the VM's
        // capture layout was fixed, forwarding anything here was impossible anyway: the VM appended its capture
        // block at the passed-argument count, so ONE argument (the scope) landed in capture slot 0 and every
        // captured read inside such a callback returned the scope instead of the enclosing value.
        comNs.AddMethod("Run", args =>
        {
            if (args.Length < 1 || args[0] is not ScriptFunction callback)
                throw new ScriptException("Com.Run requires a callback function.");

            var previous = _currentScope;
            try
            {
                using var scope = ComScope.Create();
                _currentScope = scope;

                // Scope first, then whatever the caller passed after the callback. Built as a fresh array so the
                // callback never sees this method's own `args` buffer.
                var callbackArgs = new object?[args.Length];
                callbackArgs[0] = scope;
                if (args.Length > 1)
                    Array.Copy(args, 1, callbackArgs, 1, args.Length - 1);

                try
                {
                    // THE INTERPRETER THAT IS RUNNING, not the one that REGISTERED this namespace. A namespace object is
                    // shared through the interpreter blueprint, so a nested `RunScript` reaches the OUTER interpreter's
                    // `Com` and the callback was then invoked against the OUTER interpreter's globals: measured, a
                    // top-level `var mediaFolder` of a captured file answered `Undefined variable 'mediaFolder'` from
                    // inside its own `Com.Run` callback (four-line repro: `var g = "hello"; Com.Run((s) => print(g));`
                    // failed through `RunScript` on BOTH legs and worked when the file ran directly). `Current ?? this`
                    // is the rule every other internal helper in this engine already follows (see `__ref_global__`),
                    // and the SCOPE stays a `[ThreadStatic]`, so `Com.CreateObject` and its siblings need no change.
                    var self = Current ?? this;
                    return self.InvokeScriptFunction(callback, callbackArgs, 0, 0);
                }
                catch (ScriptException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new ScriptException($"[Com.Run]: {ex.Message}");
                }
            }
            finally
            {
                _currentScope = previous;
            }
        });

        // ── Com.CreateObject(progId) → ComNode ──
        comNs.AddMethod("CreateObject", args =>
        {
            if (args.Length < 1 || args[0] is not string progId)
                throw new ScriptException("Com.CreateObject requires a ProgID string.");
            var scope = _currentScope ?? throw new ScriptException("Com.CreateObject must be called inside Com.Run().");
            return scope.CreateObject(progId);
        });

        // ── Com.GetObject(progId), ROT lookup ──
        // ── Com.GetObject(path, progId), moniker path + fallback ProgID ──
        comNs.AddMethod("GetObject", args =>
        {
            var scope = _currentScope ?? throw new ScriptException("Com.GetObject must be called inside Com.Run().");
            var path = args.Length > 0 && args[0] is string s ? s : null;
            var progId = args.Length > 1 && args[1] is string p ? p : null;

            if (path != null && progId == null)
                return scope.GetObject(progId: path);   // single arg = ProgID for ROT
            if (path == null && progId != null)
                return scope.GetObject(progId: progId);
            return scope.GetObject(pathName: path, progId: progId);
        });

        // ── Com.Host([strict]), the object of the application THIS ENGINE IS RUNNING INSIDE ──
        // The one instance COM's own cache cannot name: measured from inside Excel, `Com.GetObject("Excel.Application")`
        // refuses (the ROT holds no entry for the instance that loaded us) and `Com.CreateObject` answers a DIFFERENT
        // instance (a different window handle, 1249132 against the host's 4000928). So the host hands its own object
        // over instead (`slc_adopt`, which the VBA module's EngineAdoptHost calls with `ObjPtr(Application)`), and this
        // member is where a script reaches it.
        //
        // A MISS ANSWERS NULL, which is this engine's rule for a lookup (a control name nothing holds, an absent
        // dictionary key, `ctx.Body` on a GET): a script writes `var app = Com.Host(); if (app == null) { ... }` and the
        // flow decides. `Com.Host(true)` is the strict spelling for a script that would rather be told why, and it
        // names the reason: no `Com.Run()` scope, nothing adopted, or an adopted object that is gone.
        //
        // NO PROPERTY ON THE SCOPE, deliberately: the pointer that arrives is apartment-local, so it is taken on the
        // thread that will use it, per Com.Run scope, and tracked by that scope like any other reference.
        comNs.AddMethod("Host", args =>
        {
            var strict = false;
            if (args.Length > 0)
            {
                if (args[0] is not bool wanted)
                    throw new ScriptException("Com.Host takes one optional boolean (true to refuse instead of answering "
                                              + "null), not '" + GetFriendlyTypeNameOfValue(args[0]) + "'");
                strict = wanted;
            }

            var scope = _currentScope;
            if (scope is null)
            {
                return strict
                    ? throw new ScriptException("Com.Host() needs a Com.Run() scope to hold the reference: write "
                                                + "Com.Run(fun(scope) { ... Com.Host() ... })")
                    : (object?)null;
            }

            var host = (Current ?? this).HostObjectSource;
            var dispatch = host?.Invoke() ?? IntPtr.Zero;
            if (dispatch == IntPtr.Zero)
            {
                return strict
                    ? throw new ScriptException("Com.Host() has nothing to answer: this session's host adopted no "
                                                + "object, or the one it adopted is gone. A host that embeds the engine inside "
                                                + "the application it wants to drive hands its own pointer over first "
                                                + "(slc_adopt, or the VBA module's EngineAdoptHost with ObjPtr(Application)).")
                    : (object?)null;
            }

            return scope.Adopt(dispatch);
        });


        // ── This surface ENDS NOTHING: it reports,
        // Quitting an application is the SCRIPT's job, in VBA's own shape: `Quit()` on it inside the callback,
        // the only place its handles are alive. An engine that ends one is guessing at somebody else's lifetime,
        // and one that closes documents to make a stubborn server let go is writing the script's teardown for it.
        // What the engine owes the author is a REPORT: `ComScope.ReportUnquitServers` names, on stderr and at the
        // run's last scope out, every server this process STARTED that nothing quit, and counts it in
        // `Com.Stats().StillRunning` / `.StillRunningNames`. No engine-side disposal call exists, by design.
        // is guessing at somebody else's lifetime, and an engine that closes documents to make a stubborn server
        // let go is writing the script's teardown for it. What the engine owes the author instead is a REPORT, and
        // that is `ComScope.ReportUnquitServers` at the run's last scope out: a stderr line naming any server this
        // process STARTED that nothing quit, plus `Com.Stats().StillRunning` / `.StillRunningNames` for a suite.
        // (The rest of this file ends for one case only: NOTHING. Quitting an application is the script's
        // line to write, `Quit()` inside the callback, and what the engine does instead is REPORT, at the end
        // of the run, whatever this process started and nothing quit.)
        // ── Com.GetOrCreate(progId), attach to the running instance, else create one ──

        // The ROT is COM's own cache, and that makes this the safe way to reuse ONE server across a loop:

        // measured with cdb, a loop calling CreateObject per iteration leaks one connection handle (an ALPC

        // Port) per iteration, while attaching leaks none. Deliberately NOT a sticky pointer: a lookup

        // re-resolves the CURRENT instance or fails cleanly, whereas a cached one would outlive a script's

        // Quit() as a dead handle, the exact crash class this bridge already paid for once.

        comNs.AddMethod("GetOrCreate", args =>

        {

            if (args.Length < 1 || args[0] is not string progId)

                throw new ScriptException("Com.GetOrCreate requires a ProgID string.");

            var scope = _currentScope ?? throw new ScriptException("Com.GetOrCreate must be called inside Com.Run().");

            try

            {

                return scope.GetObject(progId: progId);   // registered in the ROT → reuse it

            }

            catch (Exception)

            {

                // Nothing of that class is running → one fresh instance. CreateObject records it as this
                // process's own work, so the run's end REPORTS it if nothing quit it; ATTACHING is never recorded,
                // so an instance somebody else started is neither reported nor touched.
                return scope.CreateObject(progId);
            }
        });
        // ── Com.IsAlive([progId | node]), is it still there, or still usable? ──
        // The script's own tool for what it cannot see, and the ARGUMENT decides which question is asked:
        //   * a NODE: "is this handle still usable?", answered WITHOUT dispatching when the scope that owned it has
        //     released it, which is what makes it safe on a handle held past its run (an in-process server would
        //     otherwise be freed memory, and calling into it kills the process silently);
        //   * a ProgID: "is anything of that class running?", a yes for an instance the script did NOT start is
        //     simply not its business to undo;
        //   * nothing: "has this process started anything still up?", which is the report's own question.
        // It is also how a script waits out a real shutdown instead of guessing a sleep:
        //     app.Quit(); while (Com.IsAlive("Excel.Application")) { sleep(250); }
        //, and that wait must live in a LATER Com.Run, because an application cannot leave the table while THIS run
        // still holds its interface (measured: polling inside the creating run never saw it go, 101 polls over 11 s).
        // The end-of-run report does not need the wait any more: it releases the interfaces FIRST and asks after, so a
        // script whose last statement was Quit() is not reported (measured 115 ms to leave, inside the 500 ms grace).
        // The lookup keeps NO reference (ComAutomation.IsRegisteredInRot), so asking cannot delay the shutdown.
        comNs.AddMethod("IsAlive", args =>
        {
            var scope = _currentScope ?? throw new ScriptException("Com.IsAlive must be called inside Com.Run().");
            if (args.Length == 0)
                return scope.IsServerAlive(null);
            if (args[0] is ComNode aliveNode)
                return aliveNode.IsAlive();
            if (args[0] is string aliveProgId)
                return scope.IsServerAlive(aliveProgId);
            throw new ScriptException("Com.IsAlive expects nothing, a ComNode, or a ProgID string.");
        });

        // ── Com.HostActiveX(node, parentHwnd [, x, y, w, h]) → child HWND ──
        // In-place activates an ActiveX control (Com.CreateObject'd, e.g.
        // "Shell.Explorer") inside an existing Win32 parent window and returns the
        // control's child window handle. The script parents that handle (SetParent /
        // form child hosting) and drives the control via its IDispatch automation.
        // Must be called inside Com.Run() so the control and its OLE site share the
        // STA thread that pumps the parent window's messages.
        comNs.AddMethod("HostActiveX", args =>
        {
            if (args.Length < 2 || args[0] is not ComNode node)
                throw new ScriptException("Com.HostActiveX expects (node, parentHwnd [, x, y, w, h, verb]).");
            var parentHwnd = ToNInt(args[1]);
            var x = args.Length > 2 ? Convert.ToInt32(args[2]) : 0;
            var y = args.Length > 3 ? Convert.ToInt32(args[3]) : 0;
            var w = args.Length > 4 ? Convert.ToInt32(args[4]) : 640;
            var h = args.Length > 5 ? Convert.ToInt32(args[5]) : 480;
            // verb: 0 = AUTO (INPLACEACTIVATE, fallback SHOW), 5 = INPLACEACTIVATE only,
            // -1 = SHOW only.
            var verb = args.Length > 6 ? Convert.ToInt32(args[6]) : 0;
            if (parentHwnd == IntPtr.Zero)
                throw new ScriptException("Com.HostActiveX: parentHwnd is zero.");
            try
            {
                var child = OleEmbed.HostActiveX(node.NativePointer, parentHwnd, x, y, w, h, verb);
                // Remember the activation so the control can be re-hosted later
                // (e.g. Form.AdoptChild + DPI change across monitors).
                OleHostRegistry.Register(child, node, node.NativePointer, verb);
                return child.ToInt64();
            }
            catch (Exception ex)
            {
                throw new ScriptException($"Com.HostActiveX failed: {ex.Message}");
            }
        });

        // ── Com.Stats() → what the bridge actually ISSUES ──
        // The counters, taken at the sites rather than inferred from timing: they answer the questions that keep
        // coming up, how many round trips a shape really costs, whether a generator re-enters a collection, how
        // often a NAMED argument pays for the type library, and whether the run's teardown found anything to end
        // (waiting for a server to actually leave its process takes 10-15 s, so a test asserts the DELTA of a
        // number instead of racing a shutdown). Each property is DYNAMIC, so a read always sees the live counters.
        // They are process-wide: the bridge has no per-scope counters, and the interesting values (name lookups,
        // enumerations, the automatic release) are whole-process facts anyway.
        comNs.AddMethod("Stats", args =>
        {
            var stats = new Namespace(_comparer, "ComStats");
            stats.AddDynamicProperty("NameLookups", () => ComObject.NameLookups);
            stats.AddDynamicProperty("NameHits", () => ComObject.NameHits);
            stats.AddDynamicProperty("ParameterLookups", () => ComObject.ParameterLookups);
            stats.AddDynamicProperty("ParameterHits", () => ComObject.ParameterHits);
            stats.AddDynamicProperty("SafeArraysCreated", () => ComObject.SafeArraysCreated);
            stats.AddDynamicProperty("SafeArraysDestroyed", () => ComObject.SafeArraysDestroyed);
            stats.AddDynamicProperty("Enumerations", () => ComObject.Enumerations);
            // The process handle count, next to the counters: handles are what a COM/native leak consumes and they are
            // invisible to every managed figure here. Read on demand, .gc handles prints it too.
            stats.AddDynamicProperty("Handles", () =>
            {
                try
                {
                    using var self = System.Diagnostics.Process.GetCurrentProcess();
                    return self.HandleCount;
                }
                catch { return 0; }
            });
            stats.AddDynamicProperty("TypeCacheEntries", () => ComObject.TypeCacheEntries);
            // What the run's REPORT found: a server this process STARTED that was still running when the run's last
            // scope closed. The engine does not end it, that is the script's `Quit()`, so this counter is the only
            // script-visible half of the report (the finding itself goes to stderr), and the half a suite asserts.
            stats.AddDynamicProperty("StillRunning", () => ComScope.StillRunning);
            stats.AddDynamicProperty("StillRunningNames", () => ComScope.StillRunningNames);
            return stats;
        });
        CurrentFrame.Scopes.Define("Com", comNs);
    }
}
