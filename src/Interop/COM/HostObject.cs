// ───────────────────────────────────────────────────────────────────────────
// The host's own object, adopted by pointer.
// ───────────────────────────────────────────────────────────────────────────
//
// WHY THIS EXISTS, measured from inside real Excel on this box:
//   * a DLL loaded into Excel cannot reach the instance hosting it through COM's own cache: `Com.GetObject
//     ("Excel.Application")` REFUSES from inside Excel (the ROT holds no entry for it), `Com.GetOrCreate` refuses with
//     it, and `Com.CreateObject` answers a DIFFERENT instance (a different window handle, 1249132 against the host's
//     4000928);
//   * the host does hold the pointer (`Application` in VBA, `ObjPtr(Application)`), and that pointer CAN be made usable
//     on another thread: a lab DLL marshalled it with `CoMarshalInterThreadInterfaceInStream` on Excel's thread and read
//     the host's `Hwnd` from a thread of its own, answering the value VBA itself has;
//   * but a call made WHILE the host is inside the engine's own `slc_run_*` call STALLS (nothing within five seconds),
//     because an STA cannot answer while it is not pumping. That is why adoption is paired with the asynchronous verbs:
//     the host returns from its call, pumps its own message loop, and only then do the script's calls arrive.
//
// SO THE CHANNEL IS THE GLOBAL INTERFACE TABLE, not a raw pointer and not a stream: the host registers its interface
// once, from the thread that owns it (`Register`), any thread of this process asks for an apartment-local reference
// (`Resolve`), and the registration is released when the session is freed (`Revoke`). A stream serves exactly one
// consumer, and a raw pointer across apartments is undefined behaviour.

using System.Runtime.InteropServices;

namespace DispatchVtbl;

internal static class HostObject
{
    /// <summary>CLSID_StdGlobalInterfaceTable, the process-wide table COM implements for us, and the interface it
    /// answers.</summary>
    private static readonly Guid ClsidStdGlobalInterfaceTable = new("00000323-0000-0000-C000-000000000046");

    private static readonly Guid IidGlobalInterfaceTable = new("00000146-0000-0000-C000-000000000046");

    /// <summary>IID_IDispatch: what a host object is adopted AS, because `IDispatch` is the interface the engine's COM
    /// bridge drives. A `static` field rather than a `const`, so its ADDRESS is stable and the GIT calls can take it.
    /// </summary>
    private static Guid _iidDispatch = new("00020400-0000-0000-C000-000000000046");

    /// <summary>One GIT pointer per thread, created on first use. The table is per PROCESS and the interface it hands
    /// out is per APARTMENT, which is what makes a cookie registered on one thread readable from another.</summary>
    [ThreadStatic] private static IntPtr _git;

    /// <summary>The table COM gives this thread, or `IntPtr.Zero` when it cannot be created (no COM apartment yet, or a
    /// table that refuses), which the callers report by name rather than crashing on.</summary>
    private static IntPtr Git()
    {
        if (_git != IntPtr.Zero) return _git;
        var hr = NativeMethods.CoCreateInstance(in ClsidStdGlobalInterfaceTable, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */,
                                                in IidGlobalInterfaceTable, out var table);
        if (hr < 0 || table == IntPtr.Zero) return IntPtr.Zero;
        _git = table;
        return table;
    }

    /// <summary>Registers an interface THE HOST HOLDS, from the thread that owns it, and answers the cookie any thread
    /// of this process can resolve. 0 means it could not be registered, and the caller says so.</summary>
    internal static unsafe uint Register(IntPtr dispatch)
    {
        if (dispatch == IntPtr.Zero) return 0;
        var table = Git();
        if (table == IntPtr.Zero) return 0;
        var vtbl = *(GlobalInterfaceTableVtable**)table;
        uint cookie = 0;
        fixed (Guid* iid = &_iidDispatch)
        {
            var hr = vtbl->RegisterInterfaceInGlobal(table, dispatch, iid, &cookie);
            return hr < 0 ? 0 : cookie;
        }
    }

    /// <summary>An apartment-local reference to what a cookie names, for THIS thread's apartment, or `IntPtr.Zero`.
    /// The caller owns the reference: the bridge releases it when the scope that tracked it does.</summary>
    internal static unsafe IntPtr Resolve(uint cookie)
    {
        if (cookie == 0) return IntPtr.Zero;
        var table = Git();
        if (table == IntPtr.Zero) return IntPtr.Zero;
        var vtbl = *(GlobalInterfaceTableVtable**)table;
        fixed (Guid* iid = &_iidDispatch)
        {
            // A function pointer has no `out` slot, so the result comes back through a local's address.
            IntPtr resolved = IntPtr.Zero;
            var hr = vtbl->GetInterfaceFromGlobal(table, cookie, iid, &resolved);
            return hr < 0 ? IntPtr.Zero : resolved;
        }
    }

    /// <summary>Releases the registration, when the session is freed. Safe for a cookie that was never registered.
    /// </summary>
    internal static unsafe void Revoke(uint cookie)
    {
        if (cookie == 0) return;
        var table = Git();
        if (table == IntPtr.Zero) return;
        var vtbl = *(GlobalInterfaceTableVtable**)table;
        _ = vtbl->RevokeInterfaceFromGlobal(table, cookie);
    }
}
