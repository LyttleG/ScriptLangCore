namespace DispatchVtbl;

/// <summary>
/// Static helpers for calling COM vtable methods via IntPtr object references.
/// All unsafe pointer casts are isolated here.
/// </summary>
internal static unsafe class VtableCall
{
    // ── IUnknown ─────────────────────────────────────────────────────────

    internal static int QueryInterface(IntPtr obj, ref Guid iid, out IntPtr ppv)
    {
        var vt = *(UnknownVtable**)obj;
        IntPtr localPpv;
        int hr;
        fixed (Guid* pIid = &iid)
            hr = vt->QueryInterface(obj, pIid, &localPpv);
        ppv = localPpv;
        return hr;
    }

    internal static uint AddRef(IntPtr obj)
        => (*(UnknownVtable**)obj)->AddRef(obj);

    internal static uint Release(IntPtr obj)
        => (*(UnknownVtable**)obj)->Release(obj);

}
