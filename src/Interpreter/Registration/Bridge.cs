using System.Text;
using System.Text.Json;

namespace ScriptLangCore;

/// <summary>THE UNIFIED BOUNDARY: one request document in, one response document out, in BOTH directions.
///
/// WHY IT EXISTS. A script value cannot cross a runtime boundary as an object: a NativeAOT engine has its own heap and
/// cannot see a host's managed objects, `Reflection.Emit` does not exist here, and a COM pointer is a per-host story
/// (Excel has one, a Python script does not). What CAN cross every boundary is UTF-8 text, so the contract is one verb
/// on each side and the object graph stays where it lives:
///
///   script -> host   Bridge.Call(requestJson)     answers the host's response document, already parsed
///   host   -> script Bridge.Handle(requestJson)   answers the script's response document as text
///
/// THE SAME TWO DOORS, three carriers: `slc_invoke` / `slc_set_bridge` over the C ABI, an `Invoke` member on the COM
/// server, and a .NET host setting <see cref="HostBridge"/> directly. One envelope, one dispatch, so a failure reads
/// the same in every log.
///
/// THE ENVELOPE (version 1). A request is an object:
///   { "v": 1, "id": "7", "m": "invoice.post", "p": { ... } }
/// `v` is the contract version, `id` is echoed VERBATIM (optional, and absent stays absent), `m` is the verb, and `p`
/// is the parameters as an object or omitted. A response is an object:
///   { "v": 1, "id": "7", "ok": true,  "r": ... }
///   { "v": 1, "id": "7", "ok": false, "e": { "code": "...", "message": "...", "retry": false,
///                                            "at": { "file": "...", "line": 12, "col": 5 },
///                                            "frames": ["Post [L10:C9]"] } }
///
/// THE SEVEN RULES, each of which is a decision rather than a habit:
///   1. ONE VERB SHAPE. Both directions use the same document, so a log or a fixture is readable without knowing
///      which side produced it.
///   2. NO RE-ENTRY ON THE SAME SESSION. A session serves one host request at a time. A handler MAY call the host with
///      `Bridge.Call`; what is REFUSED by name is `Bridge.Handle` on its own session, a verb issued from inside a host
///      callback (a run posted there would wait on itself), and a host that answers `Bridge.Call` by calling back into
///      the same session. Each of those is a loop, which is why the rule is about re-entry rather than about depth.
///   3. NOTHING THROWS ACROSS. A handler that throws becomes `ok:false` with the engine's own code, message, position
///      and frames; a host that throws is refused by name at the `Bridge.Call` that reached it.
///   4. `m` IS ADDITIVE ONLY. Removing or re-meaning a verb is a contract version bump, which is what `v` is for. A
///      reader tolerates members it does not know, so a newer side does not break an older one.
///   5. `p` IS AN OBJECT, ABSENT, OR EXPLICITLY NULL. A null means the same as absent, which is stated on the contract
///      page rather than guessed by a handler. Anything large is paged inside it, because a contract that allows one
///      huge value invites a payload no reader can bound.
///   6. `id` IS ECHOED BY CONSTRUCTION. The dispatcher rewrites the response's `id` from the request, `null` included,
///      so a handler cannot forget it. It is the handle a host matches an answer to its request with in a log; NO
///      carrier allows two requests outstanding at once, so pipelining needs a batch, which version 1 does not define.
///   7. A RESPONSE IS VALIDATED. The WHOLE shape, not only `ok`: text that is not an object, a missing or non-boolean
///      `ok`, an `ok:true` answer carrying `e`, an `e` that is not an object, or an error whose `code` or `message` is
///      missing or not a non-empty string is `HandlerReturnedNonEnvelope`. An `ok:false` answer carrying `r` has the
///      `r` dropped, because exactly one of the two shapes is present. A host never has to guess.
///
/// WHAT IS NOT SHARED. A live object crosses as a REFERENCE the owning side resolves (`acc:17`), never as a value, so
/// lifetime belongs to the host and a dangling reference is a `code` in the error object rather than a freed COM
/// pointer. That is what makes this contract work for a Python or C host at all.
/// </summary>
internal partial class Interpreter
{
    /// <summary>The contract version this build writes and reads. A request that names another one is refused with
    /// `UnsupportedVersion` rather than mis-read, which is the same rule the bundle format follows.
    ///
    /// It ALIASES the plugin door's constant rather than restating 1, because a native plugin answers the SAME
    /// envelope (see <see cref="PluginEnvelope"/>): one number, two carriers, so a bump cannot reach one side and
    /// miss the other.</summary>
    internal const int BridgeProtocol = PluginEnvelope.Protocol;

    /// <summary>The handler a script registered with <c>Bridge.Serve</c>, or null. One per interpreter, which is what
    /// makes it the SESSION's contract rather than the process's.</summary>
    private object? _bridgeHandler;

    /// <summary>THE VERB TABLE <c>Bridge.On</c> fills, or null. It is the SAME serving slot as
    /// <see cref="_bridgeHandler"/>: one of the two is set, never both, so a host can never be answered by an older
    /// registration that the script forgot about.</summary>
    private Dictionary<string, object?>? _bridgeVerbs;

    /// <summary>The id the high-level <c>Bridge.Call(verb, params)</c> assigns, one per interpreter. A raw
    /// <c>Bridge.Call(document)</c> keeps the caller's own id, because that form IS the document.</summary>
    private long _bridgeCallSequence;

    /// <summary>The file that registered the handler, captured AT REGISTRATION because a host's call can arrive after
    /// the run that served it ended (and `CurrentSourceName` is reset then). It is the `at.file` a refusal names, and a
    /// number without a file is what a host would otherwise have to correlate by hand.</summary>
    private string _bridgeSource = "";

    /// <summary>Set while a request is being served, so a handler that calls `Bridge.Handle` on its own session is
    /// refused by name instead of recursing (rule 2).</summary>
    private bool _bridgeServing;

    /// <summary>THE HOST'S SIDE OF THE CONTRACT, installed by the embedding host: a request document in, a response
    /// document out. The C ABI sets it from <c>slc_set_bridge</c>, and a .NET host can set it directly (the same
    /// field, because two implementations of one rule is the defect this repository keeps finding). Null means no
    /// host is listening, and `Bridge.Call` says so by name.
    ///
    /// TWO INSTALLERS, ONE DOOR. A script's load (`Bridge.LoadJitPlugin` or `Bridge.LoadNativePlugin`) writes the same
    /// field from the SCRIPT side (the plugin's door is a <c>Func&lt;string, string?&gt;</c> like any host's), and the
    /// flag below is what keeps the two apart: a door installed HERE came from outside the script, so
    /// `Bridge.DetachPlugin` may not remove it.</summary>
    internal Func<string, string?>? HostBridge
    {
        get => _hostBridge;
        set
        {
            _hostBridge = value;
            _bridgeFromPlugin = false;   // whoever wrote last is not us until a script's load says so one line later
        }
    }

    private Func<string, string?>? _hostBridge;

    /// <summary>True when the door `Bridge.Call` uses was installed by a SCRIPT's load (`Bridge.LoadJitPlugin` or
    /// `Bridge.LoadNativePlugin`) rather than by a host. Read by `Bridge.Plugin`, that load's "already there" refusal
    /// and `Bridge.DetachPlugin`.</summary>
    private bool _bridgeFromPlugin;

    /// <summary>WHICH plugin this session holds: the resolved assembly path and, for a MANAGED one, the
    /// assembly-qualified entry type (in the form the loader itself uses, <see cref="PluginSession.QualifyTypeName"/>).
    /// A native plugin has no entry type, so its null is what says which kind is loaded. A second load compares
    /// against these to tell "the same plugin, asked for twice" (a warning, nothing starts) from "a different plugin
    /// while one is loaded" (an error, because a managed runtime cannot be unloaded and a native module has to be
    /// released first). Only meaningful while <see cref="_bridgeFromPlugin"/> is true.</summary>
    private string? _pluginAssembly;
    private string? _pluginType;

    /// <summary>THE LOADED PLUGIN ITSELF, kept because the DOOR is not enough to give it back: `DetachPlugin` has to
    /// reach the loaded record to release a NATIVE plugin's module (<see cref="IPluginBridge.Unload"/>), and the door
    /// is only a delegate into it. Null while no script-installed plugin is loaded.</summary>
    private IPluginBridge? _plugin;

    /// <summary>Registers the Bridge namespace. Nothing here decides how a call travels: that is the host's choice of
    /// carrier, and swapping a COM host for a C one changes no script.</summary>
    private void RegisterBridgeFunctions()
    {
        var bridgeNs = new Namespace(_comparer);

        bridgeNs.AddDynamicProperty("Version", () => BridgeProtocol);
        bridgeNs.AddDynamicProperty("Served", () =>
        {
            var self = Current ?? this;
            return self._bridgeHandler is not null || self._bridgeVerbs is not null;
        });
        bridgeNs.AddDynamicProperty("Hosted", () => (Current ?? this).HostBridge is not null);
        bridgeNs.AddDynamicProperty("Plugin", () => (Current ?? this)._bridgeFromPlugin);

        // ── a script SERVES verbs to its host ────────────────────────────────────────────────────────────────────
        // The handler takes ONE parameter, the request as a script dictionary (`req["m"]`, `req["p"]`), and answers a
        // response document built with Bridge.Ok or Bridge.Fail. A handler that throws is not a bug in the contract:
        // the dispatcher turns it into the error object, which is what "nothing throws across" means here.
        bridgeNs.AddMethod("Serve", args =>
        {
            var self = Current ?? this;
            if (args.Length < 1)
                throw new ScriptException("Bridge.Serve expects the function that answers a request, or null to stop serving.");
            if (args[0] is null)
            {
                self._bridgeHandler = null;
                self._bridgeVerbs = null;
                self._bridgeSource = "";
                return true;
            }
            if (self._bridgeVerbs is not null)
                throw new ScriptException("Bridge.Serve and Bridge.On are one serving slot, and this session already serves verbs registered with Bridge.On. Call Bridge.Serve(null) first.");
            if (!IsScriptCallable(args[0]))
                throw new ScriptException($"Bridge.Serve expects a function, not '{args[0]!.GetType().Name}'.");
            self._bridgeHandler = args[0];
            self._bridgeSource = self.CurrentSourceName;
            return true;
        });

        // ── a verb table, the SAME serving slot with the envelope built for the script ───────────────────────────
        // The handler takes ONE parameter, the request's `p` (null when the request has none), and answers a VALUE
        // that becomes `r`; the engine writes `v`, `id`, `ok` and `e`. `Bridge.Error` is how a handler reports a
        // failure with its own code, and any other throw keeps the engine's code and position. One `On` per verb, and
        // a second registration of the same name replaces the first; `Bridge.Serve(null)` clears the whole table.
        bridgeNs.AddMethod("On", args =>
        {
            var self = Current ?? this;
            if (args.Length != 2 || args[0] is not string verb || verb.Length == 0 || args[1] is null)
                throw new ScriptException("Bridge.On expects (verb, handler), where verb is a non-empty name such as \"invoice.post\" and handler is a function of one parameter, the request's p.");
            if (!IsScriptCallable(args[1]))
                throw new ScriptException($"Bridge.On expects a function for \"{verb}\", not '{args[1]!.GetType().Name}'.");
            if (self._bridgeHandler is not null)
                throw new ScriptException("Bridge.On and Bridge.Serve are one serving slot, and this session already serves a handler registered with Bridge.Serve. Call Bridge.Serve(null) first.");
            self._bridgeVerbs ??= new Dictionary<string, object?>(StringComparer.Ordinal);
            self._bridgeVerbs[verb] = args[1];
            return bridgeNs;
        });

        // ── the HOST -> SCRIPT door, as text ─────────────────────────────────────────────────────────────────────
        // This is what the ABI export and the COM member call, and it is also what a stub host in a suite calls, so
        // the contract is provable without any host technology at all.
        bridgeNs.AddMethod("Handle", args =>
        {
            var self = Current ?? this;
            if (args.Length < 1 || args[0] is not string request)
                throw new ScriptException("Bridge.Handle expects the request document as text.");
            return self.BridgeHandle(request);
        });

        // ── the SCRIPT -> HOST door, in two shapes ────────────────────────────────────────────────────────────────
        // ONE ARGUMENT is the raw request document, which stays the escape hatch (a batch, a hand-built id, a verb the
        // engine does not know). TWO ARGUMENTS are a verb and its parameters: the engine writes `v`, assigns an id and
        // returns `r`, and an `ok:false` answer is thrown as a catchable error.
        bridgeNs.AddMethod("Call", args =>
        {
            var self = Current ?? this;
            if (args.Length == 2 && args[0] is string verb)
                return self.BridgeCallVerb("Call", verb, args[1], throwOnFailure: true);
            if (args.Length == 1 && args[0] is string request)
                return self.BridgeCall(request);
            throw new ScriptException("Bridge.Call expects a request document as text, or a verb and its optional parameters, as in Bridge.Call(\"customer.lookup\", { \"id\": 42 }).");
        });

        // ── the SAME call, answering the whole envelope instead of r ─────────────────────────────────────────────
        // THE NAME SAYS WHAT DIFFERS: you get the envelope, not the value. `Try` was the first spelling and it read
        // like .NET's Try* (a bool plus an out), which NEITHER member follows (this engine's other Try members are
        // `Monitor.TryEnter` and `TryGetValue(k, out v)`), so the pair is `Call` / `CallEnvelope` and a reader who has
        // met those two is not misled.
        bridgeNs.AddMethod("CallEnvelope", args =>
        {
            var self = Current ?? this;
            if (args.Length is < 1 or > 2 || args[0] is not string verb)
                throw new ScriptException("Bridge.CallEnvelope expects (verb [, parameters]), as in Bridge.CallEnvelope(\"customer.lookup\", { \"id\": 42 }).");
            return self.BridgeCallVerb("CallEnvelope", verb, args.Length == 2 ? args[1] : null, throwOnFailure: false);
        });

        // ── a SCRIPT opens a plugin, the door the `--plugin` prefix opens for a host ──────────────────────────────
        // TWO MEMBERS WITH ONE KIND EACH, because the CALLER knows what it has: `LoadJitPlugin` for an assembly plus its
        // own `runtimeconfig.json`, `LoadNativePlugin` for a DLL that exports `PluginInit` and needs nothing beside it.
        // A managed load may state its entry type or leave it out; left out, it is the assembly's ONE public static
        // [UnmanagedCallersOnly] PluginInit, found in the DLL's metadata (`PluginSession.DiscoverEntryType`), and a
        // plugin with none or several is refused by name asking for the second argument.
        // The command line cannot be told which (`--plugin`'s second slot is the entry type), so there the FOLDER
        // decides; here the folder is a CHECK, and asking the wrong member is refused by name with the one that fits.
        //
        // What the two share is EVERYTHING ELSE, and it is stated once below: the path goes through the engine's ONE
        // path rule first (`./Plugin.dll` means "beside the script that wrote it", which the command-line prefix cannot
        // say because a host's path resolves against the process directory), the plugin's identity is compared in the
        // LOADER's own form, and the three "already loaded" answers are one implementation because a runtime is not
        // unloadable.
        //
        // WHY THIS IS NOT A SANDBOX HOLE, stated because the loader's own header used to say a script never opens a
        // plugin: a script already reaches `LoadLibrary("any.dll")` and `Process.Start("any.exe")`, so opening a second
        // CoreCLR is the SAME trust class and not a new one. What it does add is that the plugin shares the engine's
        // address space and crash domain, which is why `PluginSession` still says an untrusted plugin belongs in a
        // child process. A host that must forbid it keeps the command line to itself (the `--plugin` prefix) and the
        // script finds neither member answering anything the loader would not.
        bool InstallPluginDoor(string member, string assemblyPath, string? entryType, bool native)
        {
            var self = Current ?? this;
            // THE PATH IS RESOLVED FIRST because it reads nothing: `ResolvePath` applies the engine's ONE path rule,
            // while the ENTRY TYPE is resolved where it is actually needed (the "already loaded" comparison, or the
            // load). That keeps the refusal a HOST's door deserves ahead of any read of the named DLL: what a host
            // installed is not a script's to replace, whatever the path points at.
            string assembly;
            try { assembly = self.ResolvePath(assemblyPath); }
            catch (PluginLoadException ex) { throw new ScriptException(member + ": " + ex.Message); }
            // THREE ANSWERS, because a runtime is not unloadable and so there is no fourth door to offer.
            if (self.HostBridge is not null)
            {
                if (!self._bridgeFromPlugin)
                {
                    // A HOST'S DOOR. The script may use it and may not replace it: the host owns what it installed.
                    throw new ScriptException(member + ": a HOST installed this session's bridge (slc_set_bridge, or a .NET host setting HostBridge), and the contract is one door per session, so a script may not replace it.");
                }
                // THE COMPARISON'S FORM IS THE LOADER'S: a stated type and a discovered one have to read as ONE, so
                // this branch resolves exactly what a load would have (`PluginSession.ResolveEntryType`).
                string? named = null;
                try { if (!native) named = PluginSession.ResolveEntryType(assembly, entryType); }
                catch (PluginLoadException ex) { throw new ScriptException(member + ": " + ex.Message); }
                var sameAssembly = string.Equals(self._pluginAssembly, assembly, StringComparison.OrdinalIgnoreCase);
                var samePlugin = native
                    ? sameAssembly && self._pluginType is null
                    : sameAssembly && string.Equals(self._pluginType, named, StringComparison.Ordinal);
                if (samePlugin)
                {
                    // THE SAME PLUGIN, ASKED FOR TWICE. A WARNING rather than an error, because the request is already
                    // satisfied: nothing is started, nothing is wasted, and the line says so instead of leaving a
                    // reader to wonder why a second runtime did not appear. What it takes to load the SAME file again
                    // differs by kind, so the sentence does: a native module can be released, a managed runtime cannot.
                    Logging.Log.Warning("Bridge", $"{member}: '{Path.GetFileName(assembly)}' is already loaded in this session, so nothing was started. "
                        + (self._pluginType is null
                            ? "Its module is still mapped: call Bridge.DetachPlugin() to release it, then load it again."
                            : "Nothing unloads a managed plugin runtime, so the door that already exists is the only one."));
                    return true;
                }
                // A DIFFERENT PLUGIN while one is loaded. That is an ERROR, because the session carries one door: a second
                // runtime would be started and the first would stay, so the honest instruction is to drop the DOOR
                // first, and for a NATIVE plugin that release is a real unload of the module.
                var what = self._pluginType is null
                    ? "nothing closes a native module while its door is open, so this one stays until you detach: Bridge.DetachPlugin() releases the module, and then this load starts fresh"
                    : "nothing unloads a managed runtime, so the first plugin stays resident for the life of the process";
                throw new ScriptException($"{member}: this session already carries a plugin door ('{Path.GetFileName(self._pluginAssembly ?? "")}'), and a second one is not started. Call Bridge.DetachPlugin() first if the verbs should go somewhere else: {what}.");
            }
            string? qualified = null;
            IPluginBridge plugin;
            try
            {
                if (native) plugin = PluginSession.LoadNative(assembly);
                else
                {
                    qualified = PluginSession.ResolveEntryType(assembly, entryType);
                    plugin = PluginSession.LoadJit(assembly, qualified);
                }
            }
            catch (PluginLoadException ex) { throw new ScriptException(member + ": " + ex.Message); }
            catch (Exception ex) { throw new ScriptException(member + " could not open the plugin: " + FriendlyExceptionMessage(ex)); }
            self.HostBridge = plugin.Dispatch;
            self._bridgeFromPlugin = true;
            self._plugin = plugin;
            self._pluginAssembly = assembly;
            self._pluginType = qualified;
            return true;
        }

        bridgeNs.AddMethod("LoadJitPlugin", args =>
        {
            if (args.Length is < 1 or > 2 || args[0] is not string assemblyPath || (args.Length == 2 && args[1] is not string))
                throw new ScriptException("Bridge.LoadJitPlugin expects a MANAGED plugin's path, and optionally its entry type, as in Bridge.LoadJitPlugin(\"./Plugin.dll\") or Bridge.LoadJitPlugin(\"./Plugin.dll\", \"Namespace.Type\"). A native DLL takes no type, so it is Bridge.LoadNativePlugin(\"./Plugin.dll\").");
            var entryType = args.Length == 2 ? (string)args[1]! : null;
            return InstallPluginDoor("Bridge.LoadJitPlugin", assemblyPath, entryType, native: false);
        });

        bridgeNs.AddMethod("LoadNativePlugin", args =>
        {
            if (args.Length != 1 || args[0] is not string assemblyPath)
                throw new ScriptException("Bridge.LoadNativePlugin expects a NATIVE plugin's path, as in Bridge.LoadNativePlugin(\"./Plugin.dll\"). A managed plugin is Bridge.LoadJitPlugin(\"./Plugin.dll\"), with its entry type added when it is not the one type the DLL declares.");
            return InstallPluginDoor("Bridge.LoadNativePlugin", assemblyPath, null, native: true);
        });

        // ── stop routing to the plugin, and GIVE BACK WHAT CAN BE GIVEN BACK ──────────────────────────────────────
        // TWO KINDS, TWO HONEST ANSWERS, and the difference is the loader rather than a policy: a NATIVE plugin is an
        // ordinary DLL this process loaded, so the module is released and the file can be deleted, replaced or loaded
        // again; a MANAGED plugin lives in a second CoreCLR, and `hostfxr_close` does not unload it (measured: coreclr
        // stays loaded, the module count is unchanged and the plugin's entry points keep working), so its runtime and
        // memory live as long as this process. What BOTH do is the half a script can have: `Bridge.Hosted` goes false,
        // `Bridge.Call` is refused by name again, and either member may install a different plugin.
        // ORDER MATTERS. The door is dropped FIRST, so no call can be in flight through a table that is about to be
        // discarded; then the loaded record releases (a native module's registration and image, in that order, which is
        // what lets its own `FreeLibrary` through). A plugin that must be STOPPED rather than released, or one that is
        // untrusted, belongs in a CHILD PROCESS: nothing here stops a thread or a callback the plugin started.
        bridgeNs.AddMethod("DetachPlugin", args =>
        {
            var self = Current ?? this;
            if (!self._bridgeFromPlugin)
                throw new ScriptException(self.HostBridge is null
                    ? "Bridge.DetachPlugin: no plugin is loaded in this session."
                    : "Bridge.DetachPlugin: this session's bridge is the HOST's, so only the host can remove it.");
            self.HostBridge = null;          // the setter clears the "from a plugin" flag with it
            self._plugin?.Unload();          // a NATIVE module is really released; a managed route answers false and stays
            self._plugin = null;
            self._pluginAssembly = null;     // so a later load is not compared against a door that is gone
            self._pluginType = null;
            return true;
        });

        // ── the ways a handler answers ───────────────────────────────────────────────────────────────────────────
        // EACH ONE HAS A REQUEST-SIDE TWIN (`req.Ok`, `req.Fail`, `req.FromError`), because the id a `Serve` handler
        // would have to restate is the REQUEST's own: `Bridge.Fail(req.Id, ...)` reads as if the handler had a choice,
        // and hand-writing the envelope is what a handler reaches for when it does not know the builders exist. The
        // twins share the three builders below, so the two spellings cannot answer different documents.
        bridgeNs.AddMethod("Ok", args =>
            BridgeOkText(args.Length > 0 ? args[0] : null, args.Length > 1 ? args[1] : null));

        bridgeNs.AddMethod("Fail", args =>
        {
            if (args.Length < 3 || args[1] is not string code || args[2] is not string message)
                throw new ScriptException("Bridge.Fail expects (id, code, message [, retry | options]), where code and message are non-empty strings.");
            return BridgeFailText("Bridge.Fail", args[0], code, message, args.Length > 3 ? args[3] : null);
        });

        // ── a failure a handler THROWS, rather than one it builds as text ─────────────────────────────────────────
        // The value it answers is what `throw` carries: the dispatcher reads the payload as the same
        // { code, message, retry, retryAfter, details } shape a handler's `e` has, so a script's own code survives
        // instead of being replaced by the engine's enum name.
        bridgeNs.AddMethod("Error", args =>
        {
            if (args.Length is < 2 or > 3 || args[0] is not string code || code.Length == 0
                || args[1] is not string message || message.Length == 0)
                throw new ScriptException("Bridge.Error expects (code, message [, options]), where code and message are non-empty strings and options may carry retry, retryAfter and details.");
            var source = new Dictionary<string, object?> { ["code"] = code, ["message"] = message, ["retry"] = false };
            if (args.Length == 3 && args[2] is not null)
                ReadBridgeErrorOptions(args[2], "Bridge.Error", source);
            return new ScriptErrorInfo(ErrorCode.InvalidOperation, message, source);
        });

        // ── a caught error, shaped as the error object ────────────────────────────────────────────────────────────
        // A script catches a failure and wants to answer it without inventing a second shape. `ErrorInfo` carries the
        // code, the message and the frames; it carries NO position (that lives in the message the engine composed),
        // which is why an `at` filled from a caught value is left out rather than guessed.
        bridgeNs.AddMethod("FromError", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Bridge.FromError expects (id, error).");
            return BridgeFromErrorText("Bridge.FromError", args[0], args[1]);
        });

        CurrentFrame.Scopes.Define("Bridge", bridgeNs);
    }

    /// <summary>Serves ONE request document and answers ONE response document. Every refusal the contract states is
    /// decided here, in order, so a host and a script cannot disagree about what a malformed request means.</summary>
    internal string BridgeHandle(string requestJson)
    {
        // Rule 2's script half. A host cannot reach this twice at once (the ABI marshals one call at a time), but a
        // handler that calls Bridge.Handle on its own session is a loop, and a named refusal is what it deserves.
        if (_bridgeServing)
            throw new ScriptException("Bridge.Handle is already serving a request: the contract is no re-entry on the same session, so a handler cannot call back into its own session.");
        _bridgeServing = true;
        try { return BridgeServe(requestJson); }
        finally { _bridgeServing = false; }
    }

    /// <summary>THE RAW SCRIPT -> HOST DOOR: one request document in, the host's parsed envelope out. It keeps the
    /// caller's document untouched, including the id, which is why the verb form is built on it rather than beside
    /// it.</summary>
    private IScriptDictionary BridgeCall(string request)
    {
        var self = Current ?? this;
        var host = self.HostBridge;
        if (host is null)
            throw new ScriptException("Bridge.Call needs a host bridge: the host installs one with slc_set_bridge, or a .NET host sets HostBridge on the interpreter.");
        string? answer;
        try { answer = host(request); }
        catch (ScriptException) { throw; }
        catch (Exception ex) { throw new ScriptException("the host bridge failed: " + FriendlyExceptionMessage(ex)); }
        var parsed = ParseBridgeDocument(answer);
        if (parsed is not IScriptDictionary response)
            throw new ScriptException("the host answered something that is not a JSON object, so it does not follow the Bridge contract.");
        return response;
    }

    /// <summary>THE VERB FORM OF THE SCRIPT -> HOST DOOR: the engine writes `v`, an id, `m` and `p`, and reads the
    /// answer's side. `r` is the value on success; an `ok:false` answer is thrown as a catchable error carrying the
    /// host's `e`, unless the caller is `Bridge.CallEnvelope`, which ANSWERS a typed <see cref="BridgeEnvelope"/>
    /// (`Ok`, `Value`, `Error`, and the parsed `Document`) for every failure a well-formed call can meet, so a verb
    /// call is never wrapped in a `try`. `member` is which of the two was called, so a refusal names the spelling the
    /// author wrote.
    ///
    /// THE ENVELOPE FORM IS TOTAL, and that is the whole reason it exists: no host bridge, an empty verb, parameters
    /// that are not an object, a host answer that is not a contract document, and an answer carrying another request's
    /// id each come back as `Ok: false` with a code (`NoHost`, `BadVerb`, `BadParams`, `BadAnswer`, `IdMismatch`),
    /// where the `r`-answering form throws because it has no member to carry a refusal.</summary>
    private object? BridgeCallVerb(string member, string verb, object? parameters, bool throwOnFailure)
    {
        var self = Current ?? this;
        // A FAILURE IS A VALUE ON THE ENVELOPE FORM. One local builder, so the codes and the sentence shapes are in
        // one place; the throwing form below keeps the very same sentences, which is why a caller can move between the
        // two members without the wording moving under it.
        BridgeEnvelope Refusal(string code, string message, IScriptDictionary? document) =>
            new(false, null, new BridgeError(code, message, false, 0, null), document);

        if (verb.Length == 0)
        {
            var message = $"Bridge.{member} expects a non-empty verb name.";
            if (!throwOnFailure) return Refusal("BadVerb", message, null);
            throw new ScriptException(message);
        }
        if (parameters is not null && parameters is not IScriptDictionary && parameters is not System.Collections.IDictionary)
        {
            var message = $"Bridge.{member} takes a JSON object for a verb's parameters, not '{parameters.GetType().Name}'.";
            if (!throwOnFailure) return Refusal("BadParams", message, null);
            throw new ScriptException(message);
        }
        var id = ++self._bridgeCallSequence;
        var request = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["id"] = id, ["m"] = verb };
        if (parameters is not null) request["p"] = parameters;
        // WHAT `BridgeCall` REFUSES IS A VALUE HERE: no host bridge (the common case, and the one a caller tests
        // without reading a sentence) and a host answer that is not a JSON object.
        IScriptDictionary response;
        try
        {
            response = self.BridgeCall(SerializeToText(request));
        }
        catch (ScriptException ex) when (!throwOnFailure)
        {
            return Refusal(self.HostBridge is null ? "NoHost" : "BadAnswer", ex.Message, null);
        }
        // THE ID IS CHECKED WHEN THE HOST SENDS ONE: the engine's dispatcher echoes it, while a host that predates this
        // form (the plugin fixtures among them) omits it. A WRONG id cannot be the answer to this request; an absent
        // one is not a contradiction.
        if (response.ContainsKey("id"))
        {
            var received = response["id"];
            if (BridgeWholeNumber(received) != id)
            {
                var message = $"the host's answer carries id {received}, not the {id} this call sent, so it cannot be the answer to this request.";
                if (!throwOnFailure) return Refusal("IdMismatch", message, response);
                throw new ScriptException(message);
            }
        }
        if (!response.ContainsKey("ok") || response["ok"] is not bool ok)
        {
            const string message = "the host's answer has no boolean \"ok\", so it does not follow the Bridge contract.";
            if (!throwOnFailure) return Refusal("BadAnswer", message, response);
            throw new ScriptException(message);
        }
        if (ok)
        {
            var value = response.ContainsKey("r") ? response["r"] : null;
            return throwOnFailure ? value : new BridgeEnvelope(true, value, null, response);
        }
        if (!response.ContainsKey("e"))
        {
            const string message = "the host's answer is ok false with no \"e\", so it does not follow the Bridge contract.";
            if (!throwOnFailure) return Refusal("BadAnswer", message, response);
            throw new ScriptException(message);
        }
        var error = BridgeErrorFor(response["e"], out var problem);
        if (problem is not null)
        {
            var message = "the host's failure is not a shape the contract can serve: " + problem + ".";
            if (!throwOnFailure) return Refusal("BadAnswer", message, response);
            throw new ScriptException(message);
        }
        if (!throwOnFailure) return new BridgeEnvelope(false, null, BridgeErrorOf(error), response);
        throw new ScriptException(ErrorCode.InvalidOperation,
            new ScriptErrorInfo(ErrorCode.InvalidOperation, (string)error["message"]!, error), 0, 0);
    }

    /// <summary>The TYPED `e`, built from the NORMALISED error dictionary — the same one the throwing path above hands
    /// to <see cref="ScriptErrorInfo"/>, so a refusal read as an object and a refusal thrown cannot disagree about its
    /// code, its message or its retry fields.</summary>
    private static BridgeError BridgeErrorOf(Dictionary<string, object?> error) => new(
        error.TryGetValue("code", out var code) && code is string codeText && codeText.Length > 0 ? codeText : "Failed",
        error.TryGetValue("message", out var message) && message is string messageText ? messageText : "",
        error.TryGetValue("retry", out var retry) && retry is true,
        error.TryGetValue("retryAfter", out var after) && after is long milliseconds ? milliseconds : 0,
        error.TryGetValue("details", out var details) ? details : null);

    private string BridgeServe(string requestJson)
    {
        var parsed = ParseBridgeDocument(requestJson);
        if (parsed is not IScriptDictionary request)
            return BridgeErrorText(false, null, "BadRequest", "the request is not a JSON object", null);
        if (!TryReadBridgeVersion(request, out var version, out var problem))
            return BridgeErrorText(HasId(request), IdOf(request), "UnsupportedVersion", problem, null);
        if (!request.ContainsKey("m") || request["m"] is not string verb || verb.Length == 0)
            return BridgeErrorText(HasId(request), IdOf(request), "MissingMethod", "the request names no verb: a caller writes \"m\": \"verb.name\"", null);
        var parameters = request.ContainsKey("p") ? request["p"] : null;
        if (parameters is not null && parameters is not IScriptDictionary)
            return BridgeErrorText(HasId(request), IdOf(request), "BadParams", "p must be a JSON object, or left out", null);
        // THE TYPED REQUEST a Serve handler receives is built by the SAME reading the dispatcher just did, so its
        // members cannot disagree with the document a raw handler would parse.
        var typedRequest = new BridgeRequest(verb, HasId(request) ? request["id"] : null, (int)version, parameters, request);
        // A VERB TABLE BUILDS THE ENVELOPE ITSELF: the handler answers a VALUE, so the engine writes `ok`, `v`, `id`
        // and `r`. The raw Serve path below keeps its text envelope, which is the escape hatch.
        if (_bridgeVerbs is { } verbs)
        {
            if (!verbs.TryGetValue(verb, out var verbHandler))
                return BridgeErrorText(HasId(request), IdOf(request), "NoSuchVerb", $"no verb '{verb}' is registered with Bridge.On", null);
            object? value;
            try { value = InvokeScriptCallback(verbHandler, [parameters]); }
            catch (ScriptException ex) { return BridgeFailureText(HasId(request), IdOf(request), ex); }
            catch (Exception ex) { return BridgeErrorText(HasId(request), IdOf(request), "Failed", FriendlyExceptionMessage(ex), null); }
            var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = true, ["r"] = value };
            return BridgeNormalize(SerializeToText(response), HasId(request), IdOf(request));
        }
        var handler = _bridgeHandler;
        if (handler is null)
            return BridgeErrorText(HasId(request), IdOf(request), "NoHandler", "the script has not called Bridge.Serve, so no verb can be served", null);

        object? answer;
        try { answer = InvokeScriptCallback(handler, [typedRequest]); }
        catch (ScriptException ex) { return BridgeFailureText(HasId(request), IdOf(request), ex); }
        catch (Exception ex) { return BridgeErrorText(HasId(request), IdOf(request), "Failed", FriendlyExceptionMessage(ex), null); }

        if (answer is not string document)
            return BridgeErrorText(HasId(request), IdOf(request), "HandlerReturnedNothing",
                answer is null
                    ? "the handler answered nothing, so a verb must end with Bridge.Ok or Bridge.Fail"
                    : $"the handler answered a {answer.GetType().Name}, so a verb must answer the text Bridge.Ok or Bridge.Fail builds", null);
        return BridgeNormalize(document, HasId(request), IdOf(request));
    }

    /// <summary>Reads a document into the engine's own values. One door, so the request, the handler's answer and the
    /// host's answer are all read the same way (and a malformed one becomes a contract error rather than an exception
    /// naming a JSON token).</summary>
    private static object? ParseBridgeDocument(string? document)
    {
        if (string.IsNullOrWhiteSpace(document)) return null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(document));
            if (!reader.Read()) return null;
            return ParseJsonValue(ref reader);
        }
        catch (JsonException) { return null; }
        catch (ScriptException) { return null; }
    }

    private static bool HasId(IScriptDictionary request) => request.ContainsKey("id");

    private static object? IdOf(IScriptDictionary request) => request.ContainsKey("id") ? request["id"] : null;

    /// <summary>`v` must be a whole number equal to this build's contract, and the message says which one it read:
    /// a host that asked for version 2 gets a sentence it can act on instead of mis-read rows.</summary>
    private static bool TryReadBridgeVersion(IScriptDictionary request, out long version, out string problem)
    {
        problem = "";
        version = BridgeProtocol;
        // AN OMITTED `v` IS THE VERSION THIS ENGINE SPEAKS, so a caller writes one only when it means one: the field is
        // what protects a NEXT version (a request naming 9 is refused by name rather than mis-read), and a caller that
        // states nothing asks for the current contract. An explicit number is held to it, and an explicit null asks for
        // the default for the same reason `"p": null` means no parameters. The ANSWER always states `v`, so a caller can
        // still tell which contract replied.
        if (!request.ContainsKey("v") || request["v"] is null) return true;
        version = BridgeWholeNumber(request["v"]) ?? 0;
        if (version == 0)
        {
            problem = "v is not a whole number";
            return false;
        }
        if (version != BridgeProtocol)
        {
            problem = $"this engine speaks contract version {BridgeProtocol}, and the request names {version}";
            return false;
        }
        return true;
    }

    private static long? BridgeWholeNumber(object? value) => value switch
    {
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint ui => ui,
        ulong ul when ul <= long.MaxValue => (long)ul,
        double d when d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d => (long)d,
        _ => null,
    };

    /// <summary>Rule 6 and rule 7 in one place: the answer must be an envelope whose WHOLE shape is readable, and its
    /// `id` and `v` are the dispatcher's, not the handler's, so a client cannot be handed somebody else's answer.</summary>
    private string BridgeNormalize(string document, bool hasId, object? id)
    {
        var parsed = ParseBridgeDocument(document);
        if (parsed is not IScriptDictionary response)
            return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler answered text that is not a JSON object: build it with Bridge.Ok or Bridge.Fail", null);
        if (!response.ContainsKey("ok") || response["ok"] is not bool ok)
            return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's answer has no boolean \"ok\" member: build it with Bridge.Ok or Bridge.Fail", null);

        // Rule 7 is the WHOLE shape, not only the discriminator. An answer says ok TRUE with an `r` (or nothing) or
        // ok FALSE with an `e`. An `e` beside `ok:true` is refused, because it contradicts the verdict, while an `r`
        // beside `ok:false` is DROPPED rather than refused: the answer already says the call failed, so the value is
        // stale, and a host that reads `r` on a failure is reading something the contract does not promise.
        if (ok)
        {
            if (response.ContainsKey("e"))
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's answer is ok true AND carries \"e\": answer the result as \"r\", or report a failure with \"ok\": false", null);
        }
        else
        {
            if (response.ContainsKey("r")) response.Remove("r");
            if (!response.ContainsKey("e"))
                response["e"] = new Dictionary<string, object?>
                {
                    ["code"] = "Failed",
                    ["message"] = "the handler reported a failure without a reason: answer Bridge.Fail(id, code, message)",
                    ["retry"] = false,
                };
            // The error is read through the same two-shape helper the builders use, because the dispatcher's own
            // filler is a CLR dictionary while a handler's parsed answer is a script one: requiring one type here
            // would refuse the other, which is exactly what it did to the filler before this was written.
            var error = response["e"]!;
            if (error is not IScriptDictionary && error is not System.Collections.IDictionary)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e\" is not a JSON object: answer Bridge.Fail(id, code, message) or Bridge.FromError(id, error)", null);
            if (!TryBridgeMember(error, "code", out var codeValue) || codeValue is not string code || code.Length == 0)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's error has no non-empty string \"code\": a host branches on that name", null);
            if (!TryBridgeMember(error, "message", out var messageValue) || messageValue is not string message || message.Length == 0)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's error has no non-empty string \"message\": state what to fix", null);
            if (TryBridgeMember(error, "retry", out var retryValue))
            {
                if (retryValue is not bool)
                    return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e.retry\" is not a boolean", null);
            }
            else
            {
                SetBridgeMember(error, "retry", false);
            }
            if (TryBridgeMember(error, "retryAfter", out var afterValue)
                && !(BridgeWholeNumber(afterValue) is long retryAfter && retryAfter >= 0))
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e.retryAfter\" is not a whole number of milliseconds", null);
            if (TryBridgeMember(error, "details", out var detailsValue)
                && detailsValue is not IScriptDictionary && detailsValue is not System.Collections.IDictionary)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e.details\" is not a JSON object: a host reads it as data", null);
            SetBridgeMember(error, "origin", "handler");
        }

        response["v"] = BridgeProtocol;
        if (hasId) response["id"] = id;
        else if (response.ContainsKey("id")) response.Remove("id");
        return SerializeToText(response);
    }

    /// <summary>The failure the ENGINE raises, with the position the exception carries and the file the handler was
    /// registered from. That is the half a host otherwise has to correlate by hand, which is why it is part of the
    /// contract and not of a host's log line.</summary>
    private string BridgeFailureText(bool hasId, object? id, ScriptException ex)
    {
        ScriptErrorInfo? info = ex.Value is ScriptErrorInfo sei ? sei : null;
        // A THROWN Bridge.Error carries its own contract error in a payload dictionary; a raw `throw "text"` carries a
        // string there and keeps the engine's code below. Only a dictionary is read as the error object.
        if (info is { } thrown && thrown.Value is IScriptDictionary or System.Collections.IDictionary)
        {
            var chosen = BridgeErrorFor(thrown.Value, out var problem);
            if (problem is null)
            {
                var position = ex.Line > 0
                    ? new Dictionary<string, object?> { ["file"] = _bridgeSource, ["line"] = ex.Line, ["col"] = ex.Column }
                    : null;
                return BridgeErrorText(hasId, id, chosen, position, thrown.Frames);
            }
        }
        var code = (info?.Code ?? ex.ErrorCode).ToString();
        var message = info?.Message ?? ex.Message;
        var at = ex.Line > 0
            ? new Dictionary<string, object?>
            {
                ["file"] = _bridgeSource,
                ["line"] = ex.Line,
                ["col"] = ex.Column,
            }
            : null;
        return BridgeErrorText(hasId, id, code, message, at, info?.Frames);
    }

    /// <summary>THE SUCCESS DOCUMENT, built once for `Bridge.Ok(id, result)` and its request-side twin `req.Ok(result)`:
    /// the id is echoed when the caller has one, and an absent result leaves `r` out rather than writing a null over it.
    /// Two spellings, ONE builder, so the pair cannot answer different documents.</summary>
    internal static string BridgeOkText(object? id, object? result)
    {
        var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = true };
        if (id is not null) response["id"] = id;
        if (result is not null) response["r"] = result;
        return SerializeToText(response);
    }

    /// <summary>THE FAILURE DOCUMENT, the same pair for `Bridge.Fail` and `req.Fail`. `member` is the spelling the author
    /// wrote, so a refused option names `Bridge.Fail` or `req.Fail` rather than one of them guessing at the other, which
    /// is the rule every refusal in this namespace follows.</summary>
    internal static string BridgeFailText(string member, object? id, string code, string message, object? retryOrOptions)
    {
        if (code.Length == 0 || message.Length == 0)
            throw new ScriptException($"{member} expects non-empty strings for the code and the message: a host branches on the code and reads the message.");
        var retry = false;
        long? retryAfter = null;
        object? details = null;
        if (retryOrOptions is bool flag)
        {
            retry = flag;
        }
        else if (retryOrOptions is not null)
        {
            var options = new Dictionary<string, object?>();
            ReadBridgeErrorOptions(retryOrOptions, member, options);
            if (options.TryGetValue("retry", out var retryOption) && retryOption is bool retryFlag) retry = retryFlag;
            if (options.TryGetValue("retryAfter", out var afterOption) && afterOption is long afterMs) retryAfter = afterMs;
            if (options.TryGetValue("details", out var detailsOption)) details = detailsOption;
        }
        var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = false };
        if (id is not null) response["id"] = id;
        var error = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message,
            ["retry"] = retry,
            ["origin"] = "handler",
        };
        if (retryAfter is long after) error["retryAfter"] = after;
        if (details is not null) error["details"] = details;
        response["e"] = error;
        return SerializeToText(response);
    }

    /// <summary>THE FAILURE DOCUMENT BUILT FROM A CAUGHT VALUE, shared by `Bridge.FromError(id, error)` and
    /// `req.FromError(error)`: the script's own code and retry fields travel, and a malformed value is refused by name
    /// instead of guessed at.</summary>
    internal static string BridgeFromErrorText(string member, object? id, object? error)
    {
        var shaped = BridgeErrorFor(error, out var problem);
        if (problem is not null)
            throw new ScriptException(member + ": " + problem + ".");
        var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = false };
        if (id is not null) response["id"] = id;
        response["e"] = shaped;
        return SerializeToText(response);
    }

    /// <summary>ONE builder for every response the dispatcher writes, so no two refusals can differ in shape.</summary>
    private string BridgeErrorText(bool hasId, object? id, string code, string message, Dictionary<string, object?>? at, IReadOnlyList<string>? frames = null) =>
        BridgeErrorText(hasId, id,
            new Dictionary<string, object?> { ["code"] = code, ["message"] = message, ["retry"] = false, ["origin"] = "engine" },
            at, frames);

    /// <summary>The same builder for an error object that is ALREADY shaped (a thrown `Bridge.Error`, or a host's
    /// `e`): the code, message, retry and details are the caller's, while `at` and `frames` are the dispatcher's,
    /// because only it knows where the failure was caught.</summary>
    private string BridgeErrorText(bool hasId, object? id, Dictionary<string, object?> error, Dictionary<string, object?>? at, IReadOnlyList<string>? frames)
    {
        if (at is not null) error["at"] = at;
        if (frames is { Count: > 0 }) error["frames"] = frames;
        var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = false, ["e"] = error };
        if (hasId) response["id"] = id;
        return SerializeToText(response);
    }

    /// <summary>THE OPTIONS `Bridge.Fail` AND `Bridge.Error` SHARE: `retry`, `retryAfter` and `details`, read by name
    /// and refused by name. One implementation, so the two error builders cannot drift about which key is allowed or
    /// what a negative delay means.</summary>
    private static void ReadBridgeErrorOptions(object? options, string member, Dictionary<string, object?> source)
    {
        if (options is null) return;
        if (options is not IScriptDictionary && options is not System.Collections.IDictionary)
            throw new ScriptException($"{member} takes an options object with retry, retryAfter or details, not '{options.GetType().Name}'.");
        foreach (var key in BridgeKeysOf(options))
        {
            var name = key as string;
            if (name is null || (name != "retry" && name != "retryAfter" && name != "details"))
                throw new ScriptException($"{member} does not know the option '{key}'. Valid options: retry, retryAfter, details.");
        }
        if (TryBridgeMember(options, "retry", out var retryValue))
        {
            if (retryValue is bool retry) source["retry"] = retry;
            else throw new ScriptException($"{member} takes a boolean for \"retry\", not '{retryValue?.GetType().Name ?? "null"}'.");
        }
        if (TryBridgeMember(options, "retryAfter", out var afterValue))
        {
            var milliseconds = BridgeWholeNumber(afterValue);
            if (milliseconds is null || milliseconds < 0)
                throw new ScriptException($"{member} takes a whole number of milliseconds for \"retryAfter\", not a negative value or a fraction.");
            source["retryAfter"] = milliseconds;
        }
        if (TryBridgeMember(options, "details", out var detailsValue))
        {
            if (detailsValue is IScriptDictionary || detailsValue is System.Collections.IDictionary) source["details"] = detailsValue;
            else throw new ScriptException($"{member} takes a JSON object for \"details\", not a value a host cannot read as one.");
        }
    }

    /// <summary>A caught value as the contract's error object: `ErrorInfo` when the script threw one, a script
    /// dictionary when the script BUILT one (`{ code: ..., message: ... }`, which is how a handler reports a failure of
    /// its own without inventing a shape), and the engine's friendly text otherwise. No `at` from a caught value, and
    /// that is stated rather than guessed: a caught value carries no position (the engine appends it to the message it
    /// composes), so the dispatcher fills `at` only for a failure IT caught. `problem` is set when the script's own
    /// dictionary is not a shape the contract can serve, so the builder refuses by name instead of guessing.</summary>
    private static Dictionary<string, object?> BridgeErrorFor(object? value, out string? problem)
    {
        problem = null;
        if (value is ScriptErrorInfo info)
        {
            var error = new Dictionary<string, object?>
            {
                ["code"] = info.Code.ToString(),
                ["message"] = info.Message,
                ["retry"] = false,
                ["origin"] = "handler",
            };
            if (info.Frames is { Count: > 0 }) error["frames"] = info.Frames;
            return error;
        }
        // A script-built error travels as the dictionary it is: a `{ code: ..., message: ... }` literal is a
        // `Dictionary<object, object>` rather than a script dictionary, so both shapes are read by name.
        if (value is IScriptDictionary || value is System.Collections.IDictionary)
        {
            var source = value!;
            var error = new Dictionary<string, object?>
            {
                ["code"] = "Failed",
                ["message"] = "",
                ["retry"] = false,
                ["origin"] = "handler",
            };
            if (!TryBridgeMember(source, "code", out var codeValue) || codeValue is not string code || code.Length == 0)
            {
                problem = "code must be a non-empty string a host can branch on";
                return error;
            }
            error["code"] = code;
            if (!TryBridgeMember(source, "message", out var messageValue) || messageValue is not string message || message.Length == 0)
            {
                problem = "message must be a non-empty string";
                return error;
            }
            error["message"] = message;
            if (TryBridgeMember(source, "retry", out var retryValue))
            {
                if (retryValue is not bool retry)
                {
                    problem = "retry must be a boolean";
                    return error;
                }
                error["retry"] = retry;
            }
            if (TryBridgeMember(source, "retryAfter", out var afterValue))
            {
                var milliseconds = BridgeWholeNumber(afterValue);
                if (milliseconds is null || milliseconds < 0)
                {
                    problem = "retryAfter must be a whole number of milliseconds";
                    return error;
                }
                error["retryAfter"] = milliseconds;
            }
            if (TryBridgeMember(source, "details", out var detailsValue))
            {
                if (detailsValue is not IScriptDictionary && detailsValue is not System.Collections.IDictionary)
                {
                    problem = "details must be a JSON object";
                    return error;
                }
                error["details"] = detailsValue;
            }
            return error;
        }
        return new Dictionary<string, object?>
        {
            ["code"] = "Failed",
            ["message"] = value?.ToString() ?? "the handler failed",
            ["retry"] = false,
            ["origin"] = "handler",
        };
    }

    /// <summary>Reads one member of a script dictionary or a CLR one by NAME, the pair `BridgeErrorFor` handles.
    /// Answers false when the member is ABSENT, so a caller can tell absent from present-and-null.</summary>
    private static bool TryBridgeMember(object dictionary, string name, out object? value)
    {
        if (dictionary is IScriptDictionary scripted && scripted.ContainsKey(name))
        {
            value = scripted[name];
            return true;
        }
        if (dictionary is System.Collections.IDictionary raw && raw.Contains(name))
        {
            value = raw[name];
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Writes one member of a script dictionary or a CLR one by NAME, the write half of `TryBridgeMember`.
    /// The dispatcher uses it to force the members it owns (`retry`'s default and `origin`) without caring which shape
    /// the handler's answer arrived in.</summary>
    private static void SetBridgeMember(object dictionary, string name, object? value)
    {
        if (dictionary is IScriptDictionary scripted) scripted[name] = value;
        else if (dictionary is System.Collections.IDictionary raw) raw[name] = value;
    }

    /// <summary>The keys of a script dictionary or a CLR one, so a builder can refuse a member it does not know rather
    /// than dropping it. The two shapes are the same pair `TryBridgeMember` reads.</summary>
    private static IEnumerable<object> BridgeKeysOf(object dictionary)
    {
        if (dictionary is IScriptDictionary scripted) return scripted.Keys();
        if (dictionary is System.Collections.IDictionary raw)
        {
            var keys = new List<object>(raw.Keys.Count);
            foreach (var key in raw.Keys) keys.Add(key!);
            return keys;
        }
        return [];
    }
}
