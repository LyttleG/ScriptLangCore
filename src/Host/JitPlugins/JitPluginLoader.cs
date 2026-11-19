using System.Runtime.InteropServices;

namespace ScriptLangCore;

/// <summary>Raised when a plugin cannot be loaded or does not speak this host's contract. Host-facing rather than
/// script-visible, so the sentence is meant for whoever wrote the host: it names the step, the file or the number.
/// Nothing here ever reaches a script, which is why it does not go through the engine's message map (the map exists
/// to keep a CLR resource key out of a SCRIPT's error; a host reads a host sentence).</summary>
internal sealed class PluginLoadException : Exception
{
    internal PluginLoadException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>THE LOADER: nethost finds hostfxr, hostfxr starts a runtime for the PLUGIN'S OWN runtimeconfig, one call
/// gives us `PluginInit`, and the plugin fills the versioned table.
///
/// WHY THIS AND NOT `coreclr_initialize` (the hand-rolled route an earlier lab used, and it works): `coreclr_*`
/// makes the HOST build the platform assembly list, so the host also owns framework resolution and version
/// selection, and it cannot honour the plugin's `runtimeconfig.json` at all. Measured in the lab: a `net10.0`
/// plugin ran on the `11.0.0` runtime that the host had guessed, silently. With hostfxr the plugin declares its
/// framework and roll-forward policy in its own config, and the host has no list to keep in step.
///
/// THE FOUR STEPS AND THE TRAP IN EACH, all measured:
///   1. `get_hostfxr_path` fills a `char_t` buffer (UTF-16 on Windows, so reading it as UTF-8 answers "C"), and it
///      reports the needed size by failing with HostApiBufferTooSmall (0x80008098), not by answering 0.
///   2. `hostfxr_initialize_for_runtime_config` takes the plugin's config path as `char_t` too: UTF-8 there is
///      answered with mixed letters and NULs and the runtime says the file does not exist.
///   3. `hostfxr_get_runtime_delegate` takes `hdt_load_assembly_and_get_function_pointer`, which is 5: the enum
///      STARTS AT 0, and 6 is `hdt_get_function_pointer`, whose signature has an extra parameter and answers
///      0x80131502 for a well-formed request.
///   4. `load_assembly_and_get_function_pointer` needs the type name ASSEMBLY-QUALIFIED (`Namespace.Type,
///      AssemblyName`) and the delegate type `UNMANAGEDCALLERSONLY_METHOD`, and it answers 0x80131522 for a bare
///      type name. The loader refuses a bare name itself, because that HRESULT names nothing a reader can act on.
///
/// WHAT IT DOES NOT DO: nothing unloads a MANAGED plugin (its second runtime lives as long as the process, measured
/// twice), so a plugin that must be stoppable belongs in a CHILD PROCESS, and a plugin that throws across its own entry
/// point ends the process (measured: an `Init` that throws killed the host with exit 127). Those are limits of hosting
/// IL from NativeAOT, not of this file, which is why they are stated on the contract rather than hidden here. The
/// NATIVE route below is the one exception in this stack, because the OS loaded it and the OS can take it back.</summary>
internal static unsafe class JitPluginLoader
{
    /// <summary>`HostApiBufferTooSmall`: how `get_hostfxr_path` reports the size it needs.</summary>
    private const int HostApiBufferTooSmall = unchecked((int)0x80008098);

    /// <summary>`hdt_load_assembly_and_get_function_pointer`. The enum starts at 0, so this is 5 (see the summary).</summary>
    private const int LoadAssemblyAndGetFunctionPointer = 5;

    /// <summary>Loads one plugin and hands back the door. <paramref name="typeName"/> must be assembly-qualified;
    /// <paramref name="nethostPath"/> is the `nethost.dll` deployed beside the host (a NativeAOT app does not carry
    /// it), and <paramref name="runtimeConfigPath"/> is the PLUGIN's own `runtimeconfig.json`.</summary>
    internal static IPluginBridge Load(string nethostPath, string runtimeConfigPath, string assemblyPath, string typeName,
                                       int expectedAbi = PluginContract.Abi)
    {
        if (typeName.IndexOf(',') < 0)
            throw new PluginLoadException($"the type name '{typeName}' names no assembly: write 'Namespace.Type, AssemblyName', because this route answers 0x80131522 (TypeLoadException) for a bare type name and says nothing about why");
        if (!File.Exists(nethostPath))
            throw new PluginLoadException($"nethost.dll was not found at '{nethostPath}': copy it beside the host (it ships in the Microsoft.NETCore.App.Host.<rid> pack) or pass its path");
        // THE ASSEMBLY IS CHECKED BEFORE ITS CONFIG, because it is the file the caller NAMED: with the two reversed, a
        // mistyped plugin path answered "the plugin's runtimeconfig.json was not found at 'nope.runtimeconfig.json'",
        // which sends a reader looking for a file they never mentioned (measured through `--plugin nope.dll`).
        if (!File.Exists(assemblyPath))
            throw new PluginLoadException($"the plugin assembly was not found at '{assemblyPath}'");
        if (!File.Exists(runtimeConfigPath))
            throw new PluginLoadException($"the plugin's runtimeconfig.json was not found at '{runtimeConfigPath}': a plugin ships one so its framework and roll-forward are its own business");

        string hostfxrPath = FindHostfxr(nethostPath);
        var exports = Initialise(hostfxrPath, runtimeConfigPath, assemblyPath, typeName, expectedAbi);
        return new LoadedJitPlugin(exports, typeName);
    }

    /// <summary>Step 1: where hostfxr lives, asked of nethost so a machine with several runtimes, or a `DOTNET_ROOT`,
    /// is the runtime's business rather than a folder-sorting guess.</summary>
    private static string FindHostfxr(string nethostPath)
    {
        nint library;
        try { library = NativeLibrary.Load(nethostPath); }
        catch (Exception ex) { throw new PluginLoadException($"nethost.dll at '{nethostPath}' could not be loaded: {ex.GetType().Name}", ex); }
        var getPath = (delegate* unmanaged<byte*, nuint*, nint, int>)NativeLibrary.GetExport(library, "get_hostfxr_path");

        nuint size = 0;
        int probe = getPath(null, &size, 0);
        if (probe != HostApiBufferTooSmall || size == 0)
            throw new PluginLoadException($"get_hostfxr_path answered 0x{probe:X8} with size {size}: nethost reports the size it needs by failing with 0x80008098");
        // `char_t` units, terminator included, so the buffer is size * sizeof(char_t) bytes.
        int unit = NativeChar.Wide ? 2 : 1;
        byte* buffer = (byte*)NativeMemory.Alloc(size * (nuint)unit);
        try
        {
            int hr = getPath(buffer, &size, 0);
            if (hr < 0) throw new PluginLoadException($"get_hostfxr_path answered 0x{hr:X8}: this machine has no .NET runtime for a plugin to use");
            var path = NativeChar.Text((nint)buffer);
            if (path.Length == 0) throw new PluginLoadException("get_hostfxr_path answered an empty path");
            return path;
        }
        finally { NativeMemory.Free(buffer); }
    }

    /// <summary>Steps 2 to 4, then the contract check: a plugin that speaks another version, or fills less of the
    /// table than this host reads, is refused BY NAME before a single slot is called.</summary>
    private static PluginExports Initialise(string hostfxrPath, string runtimeConfigPath, string assemblyPath, string typeName, int expectedAbi)
    {
        var fx = NativeLibrary.Load(hostfxrPath);
        var initForConfig = (delegate* unmanaged<byte*, nint, nint*, int>)NativeLibrary.GetExport(fx, "hostfxr_initialize_for_runtime_config");
        var getDelegate = (delegate* unmanaged<nint, int, nint*, int>)NativeLibrary.GetExport(fx, "hostfxr_get_runtime_delegate");
        var close = (delegate* unmanaged<nint, int>)NativeLibrary.GetExport(fx, "hostfxr_close");

        byte* config = NativeChar.Alloc(runtimeConfigPath);
        nint handle = 0;
        int hr;
        try { hr = initForConfig(config, 0, &handle); }
        finally { NativeChar.Free(config); }
        if (hr < 0) throw new PluginLoadException($"hostfxr_initialize_for_runtime_config answered 0x{hr:X8} for '{runtimeConfigPath}': the framework that config names is not installed, or the path is wrong");

        nint loadAndGet;
        try { hr = getDelegate(handle, LoadAssemblyAndGetFunctionPointer, &loadAndGet); }
        finally { close(handle); }
        if (hr < 0) throw new PluginLoadException($"hostfxr_get_runtime_delegate({LoadAssemblyAndGetFunctionPointer}) answered 0x{hr:X8}: 5 is hdt_load_assembly_and_get_function_pointer, and 6 is a different entry point with another signature");

        nint entry = ResolveEntry(loadAndGet, assemblyPath, typeName);
        var exports = default(PluginExports);
        int rc = ((delegate* unmanaged<PluginExports*, int>)entry)(&exports);
        return Checked(exports, rc, $"{typeName}.{PluginContract.InitMethodName}", expectedAbi);
    }

    /// <summary>THE NATIVE ROUTE: a plugin that is an ordinary DLL, loaded by the OS instead of by a second CoreCLR.
    /// Same table, same four checks (`Checked`), same door: the contract was always a C ABI, so a plugin written in C
    /// or in Rust is not a second kind of plugin, only a second way of arriving.
    ///
    /// WHAT THIS ROUTE CANNOT DO, and the sentence a caller meets: a native plugin has no entry TYPE (the table is
    /// filled by the exported function itself), so `label` is only what a message names; and nothing is started, so
    /// there is no framework of the plugin's own to roll forward (the DLL links what it links).
    ///
    /// THE MODULE HANDLE IS KEPT, which is the one difference from the managed route beyond the loader: `LoadLibrary`
    /// really does put an image in this process, so `FreeLibrary` really does take it out, and the handle is the only
    /// thing that can do it. It rides on the <see cref="LoadedJitPlugin"/> (`Unload`) INSTEAD of being a local here,
    /// and a load that REFUSES the plugin releases it before throwing, because nothing holds the table and a module
    /// nobody can reach is a file that cannot be replaced for the life of the process.</summary>
    internal static IPluginBridge LoadNative(string assemblyPath, string label, int expectedAbi = PluginContract.Abi)
    {
        if (!File.Exists(assemblyPath))
            throw new PluginLoadException($"the plugin assembly was not found at '{assemblyPath}'");
        nint library;
        try { library = NativeLibrary.Load(assemblyPath); }
        catch (Exception ex)
        {
            throw new PluginLoadException($"'{assemblyPath}' could not be loaded as a native plugin: {ex.GetType().Name}. This route is taken when '<name>.runtimeconfig.json' is absent, so a MANAGED plugin needs that file beside it", ex);
        }
        nint entry;
        try { entry = NativeLibrary.GetExport(library, PluginContract.InitMethodName); }
        catch (Exception)
        {
            NativeLibrary.Free(library);
            throw new PluginLoadException($"'{assemblyPath}' exports no '{PluginContract.InitMethodName}': a native plugin declares it as `__declspec(dllexport) int PluginInit(Table* table)` (C names are not decorated), and a MANAGED plugin needs its '<name>.runtimeconfig.json' beside it instead");
        }
        var exports = default(PluginExports);
        int rc = ((delegate* unmanaged<PluginExports*, int>)entry)(&exports);
        PluginExports checked_;
        try { checked_ = Checked(exports, rc, $"{PluginContract.InitMethodName} of '{label}'", expectedAbi); }
        catch (PluginLoadException) { NativeLibrary.Free(library); throw; }
        return new LoadedJitPlugin(checked_, label, library);
    }

    /// <summary>THE TABLE'S FOUR RULES, asked ONCE because both routes end here, and each refusal names what to
    /// rebuild: the plugin's own refusal to start, another ABI, a table shorter than this host reads (the reason the
    /// two version fields come first), and an empty verb door.</summary>
    private static PluginExports Checked(PluginExports exports, int rc, string what, int expectedAbi)
    {
        if (rc != 0) throw new PluginLoadException($"{what} answered {rc}, which is the plugin's own refusal to start");
        if (exports.Abi != expectedAbi)
            throw new PluginLoadException($"plugin speaks abi {exports.Abi}, this host speaks {expectedAbi}: rebuild the plugin or update the host");
        uint needed = (uint)sizeof(PluginExports);
        if (exports.Size < needed)
            throw new PluginLoadException($"plugin filled {exports.Size} byte(s) of the table, this host needs {needed}: rebuild the plugin");
        if (exports.Invoke is null)
            throw new PluginLoadException("the plugin filled the table but left the verb door (Invoke) empty: a host calls nothing else once the typed slots are proven");
        return exports;
    }

    /// <summary>Step 4: the one entry point, bound as an unmanaged function pointer rather than through a delegate
    /// type (which is what `UNMANAGEDCALLERSONLY_METHOD` asks for).</summary>
    private static nint ResolveEntry(nint loadAndGet, string assemblyPath, string typeName)
    {
        var load = (delegate* unmanaged<byte*, byte*, byte*, nint, nint, nint*, int>)loadAndGet;
        byte* assembly = NativeChar.Alloc(assemblyPath);
        byte* type = NativeChar.Alloc(typeName);
        byte* method = NativeChar.Alloc(PluginContract.InitMethodName);
        nint entry = 0;
        int hr;
        try { hr = load(assembly, type, method, PluginContract.UnmanagedCallersOnlyMethod, 0, &entry); }
        finally { NativeChar.Free(assembly); NativeChar.Free(type); NativeChar.Free(method); }
        if (hr < 0) throw new PluginLoadException($"load_assembly_and_get_function_pointer answered 0x{hr:X8} for '{typeName}.{PluginContract.InitMethodName}': the method must be public, static and [UnmanagedCallersOnly]");
        return entry;
    }
}

/// <summary>ONE LOADED PLUGIN. It owns the table and calls through it; every call is the host's own thread (contract
/// rule 4), and nothing here throws for a plugin's verdict, because a verdict is an answer.
///
/// THE MODULE, when there is one. A MANAGED plugin arrives through hostfxr, which starts a second CoreCLR that cannot
/// be stopped (`hostfxr_close` leaves it loaded, measured), so there is nothing to release and no handle to keep. A
/// NATIVE plugin arrives through `NativeLibrary.Load`, which is `LoadLibrary`, so the image IS this process's to
/// remove and the handle is kept here (<see cref="Unload"/>). The table is mutable for that one case: its four
/// pointers live inside the image, so releasing the image must also discard them, or a stray call would run into
/// memory the loader has already taken away.</summary>
internal sealed unsafe class LoadedJitPlugin : IPluginBridge
{
    private PluginExports _exports;
    private readonly string _typeName;

    /// <summary>The module this plugin arrived in, or zero for the managed route (see the class note). Zero is what
    /// `Unload` reads to decide whether there is anything to release.</summary>
    private readonly nint _library;

    private bool _unloaded;

    internal LoadedJitPlugin(PluginExports exports, string typeName, nint library = default)
    {
        _exports = exports;
        _typeName = typeName;
        _library = library;
        // THE DOOR'S MODULE IS REGISTERED WITH THE SESSION, which is what lets a script's `FreeLibrary` be REFUSED while
        // this door lives: the same module has one handle, so a script that loaded the plugin's own file and freed it
        // used to take the reference the loader held with it and the next call ran into unmapped memory (measured:
        // 0xC0000005 in `Dispatch` on the JIT build, SIGSEGV on the AOT one). Registration and release are one pair.
        if (library != nint.Zero) PluginModules.Register(library);
    }

    /// <summary>The contract version the plugin reported at load, kept for a host that logs which build answered.</summary>
    public int Abi => (int)_exports.Abi;

    /// <summary>The typed slot: no document, one call. An empty answer is reported as empty rather than refused,
    /// because this slot's payload is a label and not a contract.</summary>
    public string Ping()
    {
        RequireLoaded();
        int needed = _exports.Ping(null, 0);
        if (needed <= 0) return "";
        var buffer = new byte[needed + 1];
        fixed (byte* answer = buffer)
        {
            int wrote = _exports.Ping(answer, needed + 1);
            return wrote <= 0 ? "" : NativeChar.DecodeAnswer(answer, Math.Min(wrote, needed));
        }
    }

    /// <summary>The door: the request is pinned for the call (the plugin keeps no pointer), the answer is sized by a
    /// PROBE and then read by the LENGTH the plugin returned. A host-side refusal is answered in the envelope's own
    /// shape, so a caller reads one kind of document whatever happened.</summary>
    public string Dispatch(string requestJson)
    {
        RequireLoaded();
        var request = NativeChar.EncodeRequest(requestJson);
        fixed (byte* payload = request)
        {
            int needed = _exports.Invoke(payload, null, 0);
            if (needed < 0)
                return PluginEnvelope.FailureText("PluginRefused", $"the plugin refused the call with status {needed} on '{_typeName}'");
            var answer = new byte[needed + 1];
            fixed (byte* buffer = answer)
            {
                int wrote = _exports.Invoke(payload, buffer, needed + 1);
                if (wrote < 0)
                    return PluginEnvelope.FailureText("PluginRefused", $"the plugin refused the call with status {wrote} on '{_typeName}'");
                if (wrote != needed)
                    return PluginEnvelope.FailureText("PluginInconsistentLength", $"the plugin answered {needed} byte(s) to the probe and then {wrote} to the same request on '{_typeName}': the door's length must be a property of the request");
                return NativeChar.DecodeAnswer(buffer, wrote);
            }
        }
    }

    /// <summary>The door with the envelope built and the answer read, which is what a host normally calls.</summary>
    public PluginCall Call(string verb, string? parametersJson = null)
        => PluginEnvelope.ReadAnswer(Dispatch(PluginEnvelope.Request(verb, parametersJson)));

    /// <summary>Releases the module a NATIVE plugin arrived in, and answers whether there was one to release.
    ///
    /// THREE THINGS HAPPEN IN THIS ORDER, because each one makes the next safe: the session stops treating the module
    /// as doored (so the release below is not the very thing `DynamicLibrary.Free` refuses), the table is discarded
    /// (its four pointers live inside the image), and the module is freed. A MANAGED plugin answers FALSE and keeps its
    /// table, because its runtime is still there and its entry points still work: releasing nothing is the honest
    /// answer, not a refusal. A second call is a no-op, so `Bridge.DetachPlugin` twice cannot double-free a handle.</summary>
    public bool Unload()
    {
        if (_unloaded) return false;
        _unloaded = true;
        _exports = default;
        if (_library == nint.Zero) return false;
        PluginModules.Release(_library);
        NativeLibrary.Free(_library);
        return true;
    }

    /// <summary>The one refusal a released plugin owes a caller: there is no table to call through any more, so the
    /// answer is the envelope's own failure shape rather than a call into memory the loader has taken away.</summary>
    private void RequireLoaded()
    {
        if (_unloaded)
            throw new InvalidOperationException($"plugin '{_typeName}' has been unloaded: its module is no longer mapped, so there is no table to call through");
    }
}
