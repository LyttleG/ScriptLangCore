namespace ScriptLangCore;

/// <summary>THE MODULES A LIVE DOOR POINTS INTO: loaded here, released here, and NOT a script's to release.
///
/// This exists because of a MEASURED crash and not a theory. A script that held a native plugin's own path could sit in
/// the same process as the loader's handle, so `LoadLibrary(path)` answered the module that was already there and the
/// following `FreeLibrary` dropped the reference the LOADER held (Windows keeps ONE module per file, whichever call
/// asked), the plugin's image was unmapped under the door, and the next `Bridge.Call` ran into the hole. Measured on
/// both builds before this guard: Debug `0xC0000005` inside `LoadedJitPlugin.Dispatch`, NativeAOT `Segmentation fault`
/// with rc 139. A door whose module is already gone cannot be diagnosed afterwards, so the refusal has to happen at
/// the FREE, which is what <see cref="IsDoored"/> is asked at (`DynamicLibrary.Free` and the `FreeLibrary` global's
/// raw-handle arm, the two ways a script reaches `NativeLibrary.Free`).
///
/// WHY IT LIVES BESIDE THE LOADER rather than in `PluginSession`: the AOT harness
/// (`src/jitplugins/Check/JitPluginCheck.csproj`) compiles `src/Host/JitPlugins/*.cs` IN by link, and the loader's own
/// file references this one. A guard the shipped loader needs cannot sit in a file that harness does not compile.
///
/// KEYED BY HANDLE AND NOT BY PATH, because the handle is what a free carries and what `NativeLibrary.Load` answered;
/// the set is written by the loaded plugin's constructor, cleared the instant the module is released
/// (<see cref="LoadedJitPlugin.Unload"/>), and locked because two sessions in one process may each hold a plugin and a
/// host may run them on two threads.</summary>
internal static class PluginModules
{
    private static readonly HashSet<nint> _doored = new();
    private static readonly HashSet<nint> _released = new();
    private static readonly Lock _dooredLock = new();

    /// <summary>HOW MANY RELEASED HANDLES ARE REMEMBERED, and it is bounded on purpose: a script that loads and detaches
    /// a plugin in a loop must not grow a set forever. Past this many, the oldest answers are dropped, which can only
    /// matter to a handle a script kept from a release that long ago, and such a handle was freed by its own
    /// `DynamicLibrary` long before.</summary>
    private const int ReleasedMemory = 32;

    /// <summary>Registers the module a NATIVE plugin arrived in, once per load, from the loaded plugin's own
    /// constructor: there is no way to have a native door without the registration that protects it. A handle that was
    /// released earlier is FORGOTTEN here, because the loader may hand the same value back for a fresh load of the same
    /// file and this handle is live again.</summary>
    internal static void Register(nint handle)
    {
        lock (_dooredLock)
        {
            _doored.Add(handle);
            _released.Remove(handle);
        }
    }

    /// <summary>Drops the registration, and it comes FIRST in the release: after this the engine's own `FreeLibrary` is
    /// allowed to take the module away, which is exactly what the guard would otherwise refuse. The handle is remembered
    /// as RELEASED, because a script may still hold a `DynamicLibrary` for that file: its `Free` has nothing left to
    /// release, and calling `FreeLibrary` on a module the engine has already unmapped is a double free.</summary>
    internal static void Release(nint handle)
    {
        lock (_dooredLock)
        {
            if (!_doored.Remove(handle)) return;
            if (_released.Count >= ReleasedMemory) _released.Clear();
            _released.Add(handle);
        }
    }

    /// <summary>True when releasing this module would unmap code a live door calls through.</summary>
    internal static bool IsDoored(nint handle)
    {
        if (handle == nint.Zero) return false;
        lock (_dooredLock) return _doored.Contains(handle);
    }

    /// <summary>True when the ENGINE has already released this module (a native plugin's, at the detach): the handle a
    /// script holds for that file now names nothing, so a free of it is a no-op rather than a second `FreeLibrary`.</summary>
    internal static bool WasReleased(nint handle)
    {
        if (handle == nint.Zero) return false;
        lock (_dooredLock) return _released.Contains(handle);
    }
}
