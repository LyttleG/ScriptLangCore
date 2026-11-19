using System.Runtime.InteropServices;

namespace ScriptLangCore;

/// <summary>THE PLUGIN CONTRACT: a VERSIONED table of function pointers plus ONE verb door.
///
/// WHY THIS SHAPE. A NativeAOT host cannot host IL, and a plugin needs its own JIT runtime (measured: nethost finds
/// hostfxr, `hostfxr_initialize_for_runtime_config` starts a second CoreCLR, +4.9 MB of private bytes, and NOTHING
/// unloads it). What crosses between the two runtimes is a table of `[UnmanagedCallersOnly]` entry points, so the
/// contract is a C ABI and nothing else:
///
///   { uint Abi; uint Size; Ping; Invoke }
///
/// `Abi` is the CONTRACT version (<see cref="PluginContract.Abi"/>) and `Size` is how much of the table the plugin
/// filled. Both are first for one reason: the first field ever added to this struct is a silent out-of-bounds WRITE
/// in an older plugin otherwise. A host refuses an unknown `Abi`, and refuses a `Size` shorter than the fields it
/// reads, BY NAME. Measured in the lab: `plugin speaks abi 1, this host speaks 2: rebuild the plugin or update the
/// host`, and `plugin filled 24 byte(s) of the table, this host needs 40`.
///
/// THE TWO KINDS OF SLOT, and why both exist:
///   - <see cref="PluginExports.Ping"/> is a TYPED slot: no documents, no allocation, one `call [reg]`. It is the
///     hot path, and it also proves the typed half of the ABI.
///   - <see cref="PluginExports.Invoke"/> is the VERB DOOR: a request document in, an answer document out, so a plugin
///     adds a verb without the host learning anything or recompiling. That is what keeps the table at two slots
///     instead of one per operation.
///
/// THE DOOR'S FOUR RULES, each of them inherited from the engine's own C ABI rather than invented here:
///   1. THE PAYLOAD IS UTF-8 ON EVERY PLATFORM, which is NOT what `char_t` means (see <see cref="NativeChar"/>).
///   2. THE REQUEST IS NUL-TERMINATED and caller-owned; the plugin reads it during the call and keeps no pointer.
///   3. THE ANSWER IS LENGTH-DELIMITED with the `snprintf` convention: the call returns the bytes it NEEDS, writes at
///      most `capacity` of them, and answers the same length when `capacity` is 0, so a host sizes its buffer with
///      one probe. A NUL terminator is allowed and not required, and the host reads exactly the returned length.
///   4. A NEGATIVE RETURN IS A REFUSAL from the plugin, refused by name by the host rather than read as a length.
///
/// WHAT IS NOT HERE, deliberately: no unloading of a RUNTIME (nothing in this stack unloads the second CoreCLR,
/// measured twice: `hostfxr_close` leaves it loaded and its entry points working), no sandbox (the plugin runs with
/// the host's privileges, in the host's address space, so a crash in it is a crash of the process), and no per-verb
/// host API (that is the door's whole point).
///
/// A NATIVE PLUGIN IS THE ONE THING THAT CAN GO AWAY, because the OS loaded it: it is a plain DLL, so releasing the
/// module really does unmap it, and <see cref="IPluginBridge.Unload"/> is that release. What it does NOT do is stop
/// anything the plugin started (a thread of its own, a registered callback), which is the reason the advice for a
/// plugin with a life of its own stays "put it in a child process".</summary>
internal static class PluginContract
{
    /// <summary>The contract this build writes and reads. A plugin that answers another one is refused by name, the
    /// same rule the bundle format and the <see cref="PluginEnvelope"/> follow.</summary>
    internal const int Abi = 1;

    /// <summary>The one entry point a plugin exports, resolved by name through
    /// `load_assembly_and_get_function_pointer`.</summary>
    internal const string InitMethodName = "PluginInit";

    /// <summary>`UNMANAGEDCALLERSONLY_METHOD`, the sentinel that tells the runtime to bind the method directly
    /// instead of through a managed delegate type. It is `(const char_t *)-1`, so the value is the same on every
    /// platform even though a real string there would be `char_t`.</summary>
    internal const nint UnmanagedCallersOnlyMethod = -1;
}

/// <summary>The table a plugin fills. Field ORDER and WIDTH are the wire, so everything is fixed width and the two
/// version fields come first (see <see cref="PluginContract"/>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PluginExports
{
    /// <summary>The contract version the plugin implements.</summary>
    internal uint Abi;

    /// <summary>`sizeof` of the table as the plugin sees it. A host reads nothing past it.</summary>
    internal uint Size;

    /// <summary>The typed slot: writes the plugin's identity into the caller's UTF-8 buffer and returns the bytes it
    /// needed, the same convention as the door.</summary>
    internal delegate* unmanaged<byte*, int, int> Ping;

    /// <summary>The verb door: request UTF-8 JSON, answer UTF-8 JSON, caller-owned buffer.</summary>
    internal delegate* unmanaged<byte*, byte*, int, int> Invoke;
}

/// <summary>One answer from a plugin, already read. A host branches on <see cref="Ok"/>, and
/// <see cref="ResultJson"/> or the error pair is meaningful, never both (the envelope's rule 7).</summary>
internal readonly record struct PluginCall(bool Ok, string ResultJson, string ErrorCode, string ErrorMessage)
{
    /// <summary>The envelope a plugin's failure is reported in, when the failure is the HOST's reading of the
    /// answer rather than the plugin's own verdict.</summary>
    internal static PluginCall Refused(string code, string message) => new(false, "null", code, message);
}

/// <summary>What the host codes against. One instance is one loaded plugin, and every member is safe to call from
/// the host's own thread: a plugin may not be called from two threads at once unless it says so, which this contract
/// does not (rule 4 of the engine's ABI, one call at a time).</summary>
internal interface IPluginBridge
{
    /// <summary>The contract version the plugin reported when it was initialised.</summary>
    int Abi { get; }

    /// <summary>The typed slot, as text: a plugin's identity, with no document in sight.</summary>
    string Ping();

    /// <summary>The verb door, unparsed: the answer ENVELOPE as text. Never throws for a plugin's verdict, only for
    /// a host-side mistake (a plugin that is not loaded, which the loader has already refused).</summary>
    string Dispatch(string requestJson);

    /// <summary>The door with the envelope built and the answer read, which is what a host normally calls.</summary>
    PluginCall Call(string verb, string? parametersJson = null);

    /// <summary>Releases the OS MODULE this plugin arrived in, when it arrived as one.
    ///
    /// TWO ANSWERS, and they are a fact about the route rather than a failure: TRUE means the module was unmapped, so
    /// the file can be deleted, replaced or loaded again (a NATIVE plugin is an ordinary DLL the OS loaded, so
    /// `FreeLibrary` really does take it out of the process); FALSE means there was nothing the engine could release,
    /// because the plugin is MANAGED and lives in a second CoreCLR that `hostfxr_close` does not unload (measured).
    ///
    /// THE CALLER DROPS THE DOOR FIRST. After TRUE the table and every pointer into it are gone with the image, so
    /// `Dispatch` refuses by name instead of calling into unmapped memory; the table is discarded here as well, so an
    /// answer cannot be read out of a module that is no longer there.</summary>
    bool Unload();
}
