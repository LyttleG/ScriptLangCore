using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ScriptLangCore;

/// <summary>THE GLUE: ONE loaded plugin, wired into ONE interpreter so that a SCRIPT reaches it.
///
/// The two halves are already the same document (<see cref="PluginEnvelope.Protocol"/> is what
/// <see cref="Interpreter.BridgeProtocol"/> aliases), so this file translates nothing. A script's
/// `Bridge.Call(request)` reaches the session's <see cref="Interpreter.HostBridge"/>, which is the plugin's own DOOR:
/// the request text crosses unchanged, the plugin answers its envelope, and the engine parses that envelope into the
/// dictionary the script reads (`ok`, then `r`, or `e.code` / `e.message`). Nothing about the plugin's verbs is known
/// here, which is the door's whole point.
///
/// WHAT THE SCRIPT IS GIVEN, and the two doors it can use:
///   script -> plugin   Bridge.Call(requestText)      answers a parsed envelope (the door this file opens)
///   plugin -> script   Bridge.Handle(requestText)    answers text, from the script that called Bridge.Serve
/// The second one needs no glue: a host reaches it through `Interpreter.BridgeHandle` (which `slc_invoke` and the COM
/// `Invoke` member also drive), so a plugin that wants script code calls appends that call on its own side.
///
/// WHO OPENS IT, TWO DOORS AND ONE LOADER: the command-line prefix `--plugin <dll> <Type>` opens it from the HOST,
/// and `Bridge.LoadJitPlugin(<dll>[, "<Type>"])` or `Bridge.LoadNativePlugin(<dll>)` opens the same thing from the
/// SCRIPT (every one of them ends in <see cref="Load"/>/<see cref="LoadJit"/>/<see cref="LoadNative"/>, so nethost,
/// the assembly-qualified type name and the plugin's own runtimeconfig cannot be decided two ways). The old
/// rule was "the host decides and never a script", and the correction is worth stating: a script already reaches
/// `LoadLibrary("any.dll")` and `Process.Start("any.exe")`, so starting a second CoreCLR is the SAME trust class
/// rather than a new one. What it costs is honest and unfixable in-process: the plugin shares the host's address
/// space and crash domain (a plugin that throws across its own entry point ends the PROCESS, measured), so a plugin
/// that must be stoppable or untrusted belongs in a CHILD PROCESS.
///
/// AND NOTHING UNLOADS A MANAGED PLUGIN. `Bridge.DetachPlugin()` drops the DOOR (`Bridge.Hosted` goes false,
/// `Bridge.Call` refuses by name, another plugin may be loaded), while the second CoreCLR and the plugin's memory live
/// as long as this process: measured, `hostfxr_close` leaves coreclr loaded, the module count unchanged and the entry
/// points working. That is why there is no `UnloadPlugin`, and why a child process is the answer for anything that
/// must go away.
///
/// A NATIVE PLUGIN IS THE OTHER CASE, and the difference is the loader: it is an ordinary DLL put in this process by
/// `LoadLibrary`, so `DetachPlugin` releases the MODULE too (the door goes first, then the table, then `FreeLibrary`)
/// and the file can be deleted, replaced or loaded again. Two consequences are stated where they are decided: the
/// module is registered as DOORED so a script's own `FreeLibrary` on it is refused while the door lives
/// (<see cref="PluginModules"/>, which is what closed a measured crash: a script's load-and-free of the plugin's own
/// file unmapped a live door and the next `Bridge.Call` faulted), and what a release does NOT do is stop anything the
/// plugin started (its threads, its callbacks), because nothing in this stack can.
///
/// THE LIMITS ARE THE CONTRACT'S, stated rather than hidden: nothing unloads a managed runtime, one call at a time, the
/// plugin shares the host's address space, privileges and crash domain, and it runs on the calling thread (so the
/// host's stack size and apartment decide what it can do).</summary>
internal static class PluginSession
{
    /// <summary>THE TWO KINDS OF PLUGIN, ONE DOOR, and the SCRIPT states which kind it is loading. The command line
    /// cannot (`--plugin`'s second slot is the entry type, which a native plugin does not have), so THIS overload is
    /// the one place the FOLDER decides: does `<name>.runtimeconfig.json` sit beside the file? It has to, for a managed
    /// plugin (the loader starts its runtime FROM it), and it must not, for a native one (the OS loads it and no
    /// runtime is started). Measured with `objdump`: the managed fixture carries a CLR Runtime Header and NO export
    /// table, while a native one exports `PluginInit` and carries no CLR header, so the two shapes cannot be confused
    /// once the folder says which it is.
    ///
    /// <see cref="LoadJit"/> and <see cref="LoadNative"/> are the explicit spellings, and there the folder stops being
    /// a guess and becomes a CHECK: asking the wrong member is refused by name, naming the member that fits.</summary>
    internal static bool IsManaged(string assemblyPath) =>
        File.Exists(Path.ChangeExtension(Path.GetFullPath(assemblyPath), ".runtimeconfig.json"));

    /// <summary>THE COMMAND LINE'S DOOR (`--plugin <dll> <Type>`): a host names a path, so the folder decides and the
    /// work is handed to the member a script would have called. Two things the loader cannot guess are resolved here:
    ///   - `nethost.dll` sits beside the ENGINE (where a deployment puts it) or beside the PLUGIN (which is how a
    ///     plugin folder is copied around), and both are searched before refusing;
    ///   - the type name must be ASSEMBLY-QUALIFIED, and the assembly's name IS the file's name for anything MSBuild
    ///     built, so `JitPluginFixture.Entry` becomes `JitPluginFixture.Entry, JitPluginFixture`. A name that already
    ///     carries its assembly is used as written, because a host may ship a file under another name.
    /// The plugin's `runtimeconfig.json` is its sibling, which is the one fact that makes its framework and
    /// roll-forward ITS OWN business rather than the host's (<see cref="JitPluginLoader"/>: the `coreclr_*` route
    /// cannot honour it, measured).</summary>
    internal static IPluginBridge Load(string assemblyPath, string typeName) =>
        IsManaged(assemblyPath) ? LoadJit(assemblyPath, typeName) : LoadNative(assemblyPath);

    /// <summary>THE MANAGED ROUTE, stated by the caller (`Bridge.LoadJitPlugin`). A managed plugin cannot start
    /// without its own `runtimeconfig.json`, which is the one fact that makes its framework and roll-forward ITS OWN
    /// business rather than the host's (<see cref="JitPluginLoader"/>: the `coreclr_*` route cannot honour it,
    /// measured). Its absence is therefore REFUSED here, by name, instead of being routed elsewhere.</summary>
    internal static IPluginBridge LoadJit(string assemblyPath, string? typeName = null)
    {
        var assembly = Path.GetFullPath(assemblyPath);
        // CHECKED BEFORE THE CONFIG, because the assembly is the file the caller NAMED: with the two reversed, a
        // mistyped path answered "is a NATIVE plugin", which sends a reader looking at the wrong kind of problem.
        if (!File.Exists(assembly))
            throw new PluginLoadException($"the plugin assembly was not found at '{assembly}'");
        var runtimeConfig = RequireManaged(assembly);
        var entryType = typeName ?? DiscoverEntryType(assembly);
        return JitPluginLoader.Load(FindNethost(assembly), runtimeConfig, assembly, QualifyTypeName(assembly, entryType));
    }

    /// <summary>THE MANAGED KIND'S ONE PRECONDITION, in one place so `LoadJit` and the entry-type discovery cannot
    /// disagree about which folder shape is a managed plugin: a `<name>.runtimeconfig.json` beside the DLL is what a
    /// managed plugin's framework and roll-forward policy come from, so its ABSENCE is what makes a DLL native.</summary>
    private static string RequireManaged(string assembly)
    {
        var runtimeConfig = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        if (!File.Exists(runtimeConfig))
            throw new PluginLoadException($"'{Path.GetFileName(assembly)}' is a NATIVE plugin: no '{Path.GetFileName(runtimeConfig)}' sits beside it, and a managed plugin's framework is named by that file. Load it with Bridge.LoadNativePlugin(\"{Path.GetFileName(assembly)}\"), or with --plugin <dll> native <script> from the command line.");
        return runtimeConfig;
    }

    /// <summary>THE NATIVE ROUTE, stated by the caller (`Bridge.LoadNativePlugin`): `LoadLibrary` plus
    /// `GetProcAddress("PluginInit")`, and no runtime started at all. A `runtimeconfig.json` beside the DLL means the
    /// caller asked the wrong member, which is refused by name rather than silently starting a second CoreCLR.</summary>
    internal static IPluginBridge LoadNative(string assemblyPath)
    {
        var assembly = Path.GetFullPath(assemblyPath);
        var runtimeConfig = Path.ChangeExtension(assembly, ".runtimeconfig.json");
        if (File.Exists(runtimeConfig))
            throw new PluginLoadException($"'{Path.GetFileName(assembly)}' is a MANAGED plugin, because '{Path.GetFileName(runtimeConfig)}' sits beside it: that file names its framework, so a runtime has to be started for it. Load it with Bridge.LoadJitPlugin(\"{Path.GetFileName(assembly)}\", \"Namespace.Type\"), or with --plugin <dll> <Namespace.Type> <script> from the command line.");
        return JitPluginLoader.LoadNative(assembly, Path.GetFileName(assembly));
    }

    /// <summary>The ASSEMBLY-QUALIFIED entry type, which is the form `load_assembly_and_get_function_pointer` needs
    /// and the form a plugin's identity is compared by: `Namespace.Type` becomes `Namespace.Type, AssemblyName`
    /// (the assembly's name IS the file's name for anything MSBuild built), and a name that already carries its
    /// assembly is used as written, because a host may ship a file under another name.
    ///
    /// ONE IMPLEMENTATION, THREE CALLERS: <see cref="Load"/> qualifies what it hands the loader, the discovery
    /// qualifies the entry type it found, and the script door compares what a session already holds against what a
    /// second load asks for. Two copies of this rule would let the same plugin read as two.</summary>
    internal static string QualifyTypeName(string assemblyPath, string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            throw new PluginLoadException("the plugin's entry type is required, as in 'Namespace.Type' (the loader qualifies it with the assembly name) or 'Namespace.Type, AssemblyName'");
        return typeName.Contains(',', StringComparison.Ordinal)
            ? typeName.Trim()
            : typeName.Trim() + ", " + Path.GetFileNameWithoutExtension(assemblyPath);
    }

    /// <summary>THE ENTRY TYPE A LOAD WILL USE: the caller's when it stated one, the assembly's own when the call
    /// named only a path (`Bridge.LoadJitPlugin("./Plugin.dll")`). Kept in ONE place so the script door's "this
    /// plugin is already loaded" comparison and the load itself cannot disagree about which type was opened.</summary>
    internal static string ResolveEntryType(string assemblyPath, string? typeName) =>
        QualifyTypeName(assemblyPath, typeName ?? DiscoverEntryType(assemblyPath));

    /// <summary>THE ENTRY TYPE, FOUND RATHER THAN STATED: the one type in a managed plugin that declares a public
    /// static `[UnmanagedCallersOnly]` method named `PluginInit`, which is exactly what
    /// `load_assembly_and_get_function_pointer` resolves. Read from the assembly's METADATA, not by loading it: the
    /// plugin's runtime has not started at this point, and `System.Reflection.Metadata` reads a DLL without executing
    /// anything.
    ///
    /// NONE OR SEVERAL IS A REFUSAL BY NAME. A plugin with no such method has no entry point to resolve, and one with
    /// several has no single answer, so both ask the caller for the second argument instead of guessing. The name is
    /// answered in the CLR's own form (`Namespace.Type`, or `Outer+Inner` for a nested type), so
    /// <see cref="QualifyTypeName"/> appends the assembly exactly as it does for a stated one.</summary>
    internal static string DiscoverEntryType(string assemblyPath)
    {
        var assembly = Path.GetFullPath(assemblyPath);
        if (!File.Exists(assembly))
            throw new PluginLoadException($"the plugin assembly was not found at '{assembly}'");
        _ = RequireManaged(assembly);
        try
        {
            using var stream = File.OpenRead(assembly);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                throw new PluginLoadException($"'{Path.GetFileName(assembly)}' carries no managed metadata, so it has no entry type to find: a native plugin exports '{PluginContract.InitMethodName}' and is loaded with Bridge.LoadNativePlugin(\"{Path.GetFileName(assembly)}\")");
            var reader = pe.GetMetadataReader();
            var found = new List<string>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = reader.GetMethodDefinition(methodHandle);
                    if (reader.GetString(method.Name) != PluginContract.InitMethodName) continue;
                    if (!IsPluginEntryMethod(reader, method)) continue;
                    found.Add(TypeFullName(reader, handle));
                    break;
                }
            }
            if (found.Count == 1) return found[0];
            var file = Path.GetFileName(assembly);
            if (found.Count == 0)
                throw new PluginLoadException($"'{file}' declares no public static [UnmanagedCallersOnly] method named '{PluginContract.InitMethodName}', so its entry type cannot be discovered. Pass it as the second argument, as in Bridge.LoadJitPlugin(\"./{file}\", \"Namespace.Type\").");
            throw new PluginLoadException($"'{file}' declares more than one type with a public static [UnmanagedCallersOnly] {PluginContract.InitMethodName} ({string.Join(", ", found)}), so its entry type cannot be discovered. Pass the one this load should use, as in Bridge.LoadJitPlugin(\"./{file}\", \"Namespace.Type\").");
        }
        catch (PluginLoadException) { throw; }
        catch (Exception ex)
        {
            throw new PluginLoadException($"'{Path.GetFileName(assembly)}' could not be read to discover its entry type: {ex.GetType().Name}", ex);
        }
    }

    /// <summary>THE THREE THINGS `UNMANAGEDCALLERSONLY_METHOD` ASKS OF THE METHOD: public, static and carrying
    /// `[UnmanagedCallersOnly]`. Checking the attribute here keeps a same-named helper in the plugin from being
    /// mistaken for the entry point; a method that lacks it is refused by the load itself, with the HRESULT that names
    /// the rule.</summary>
    private static bool IsPluginEntryMethod(MetadataReader reader, MethodDefinition method)
    {
        if ((method.Attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public) return false;
        if ((method.Attributes & MethodAttributes.Static) == 0) return false;
        foreach (var attributeHandle in method.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                {
                    var reference = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (reference.Parent.Kind != HandleKind.TypeReference) break;
                    var referenceType = reader.GetTypeReference((TypeReferenceHandle)reference.Parent);
                    if (IsUnmanagedCallersOnly(reader.GetString(referenceType.Namespace), reader.GetString(referenceType.Name))) return true;
                    break;
                }
                case HandleKind.MethodDefinition:
                {
                    var constructor = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    if (TypeFullName(reader, constructor.GetDeclaringType()) == UnmanagedCallersOnlyAttribute) return true;
                    break;
                }
            }
        }
        return false;
    }

    private const string UnmanagedCallersOnlyAttribute = "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute";

    private static bool IsUnmanagedCallersOnly(string ns, string name) =>
        ns == "System.Runtime.InteropServices" && name == "UnmanagedCallersOnlyAttribute";

    /// <summary>The CLR's spelling of a type's full name, nested types included (`Outer+Inner`), which is the form
    /// `load_assembly_and_get_function_pointer` resolves.</summary>
    private static string TypeFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return TypeFullName(reader, declaring) + "+" + name;
        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>Points ONE session at ONE plugin: the script to plugin door. `Bridge.Hosted` then answers true, and a
    /// `Bridge.Call` that no plugin answers is refused by the door's own reader rather than thrown. One interpreter
    /// per session is this engine's rule, so attaching to the interpreter that RUNS the script is what makes every
    /// `.run` of that session reach the plugin.</summary>
    internal static void Attach(Interpreter interpreter, IPluginBridge plugin) => interpreter.HostBridge = plugin.Dispatch;

    /// <summary>`nethost.dll` beside the engine, then beside the plugin, and a refusal that names both places. The
    /// loader would refuse a missing file by name too, but only for the one path it was handed, and a message naming
    /// one path out of two is the kind that sends a reader looking in the wrong folder.</summary>
    private static string FindNethost(string assemblyPath)
    {
        var besideEngine = Path.Combine(AppContext.BaseDirectory, "nethost.dll");
        var besidePlugin = Path.Combine(Path.GetDirectoryName(assemblyPath) ?? ".", "nethost.dll");
        if (File.Exists(besideEngine)) return besideEngine;
        if (File.Exists(besidePlugin)) return besidePlugin;
        throw new PluginLoadException($"nethost.dll was not found beside the engine ('{besideEngine}') or beside the plugin ('{besidePlugin}'): copy it there from the Microsoft.NETCore.App.Host.<rid> pack, or deploy it with the plugin");
    }
}
