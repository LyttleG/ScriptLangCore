namespace DispatchVtbl;

/// <summary>
/// Thin safe wrapper over an IDispatch vtable pointer.
/// Provides raw vtable dispatch (Invoke, GetIDsOfNames, ref counting)
/// without duplicating argument marshaling or buffer management.
/// This is the only file that directly touches COM function pointers.
/// </summary>
internal sealed unsafe class DispatchInvoker
{
    private readonly IntPtr _dispatchPtr;
    private DispatchVtable* Vtbl => *(DispatchVtable**)_dispatchPtr;

    /// <summary>The raw IDispatch pointer.</summary>
    internal IntPtr DispatchPtr => _dispatchPtr;

    /// <summary>The vtable pointer as IntPtr (for helpers that need it).</summary>
    internal IntPtr VtablePtr => new(*(DispatchVtable**)_dispatchPtr);

    internal DispatchInvoker(IntPtr dispatchPtr)
    {
        _dispatchPtr = dispatchPtr;
    }

    internal uint AddRef() => Vtbl->AddRef(_dispatchPtr);
    internal uint Release() => Vtbl->Release(_dispatchPtr);

    /// <summary>IDispatch::GetTypeInfoCount(pctinfo), the cheapest call EVERY IDispatch must answer, with no side
    /// effect on the object's own model and no member name involved. That combination is what makes it the liveness
    /// probe: a marshalled proxy whose server is gone fails it, and a name the server does not have cannot be
    /// mistaken for death the way a name lookup could. Its slot is already typed in DispatchVtable (3), so nothing
    /// here guesses a vtable layout.</summary>
    internal int GetTypeInfoCount(out uint count)
    {
        var n = 0u;
        var hr = Vtbl->GetTypeInfoCount(_dispatchPtr, &n);
        count = n;
        return hr;
    }

    internal int GetIDsOfNames(void* pGuid, void* pNamesArray, uint cNames, uint lcid, void* pDispId)
        => Vtbl->GetIDsOfNames(_dispatchPtr, pGuid, pNamesArray, cNames, lcid, pDispId);

    /// <summary>IDispatch::GetTypeInfo(iTInfo, lcid, ppTInfo), the ONE call that reaches the object's TYPE
    /// LIBRARY, which is where PARAMETER NAMES live. Measured to work on Excel (a non-null pointer), so the
    /// caller owns it and must Release it. See TypeInfoVtable.GetNames for what the pointer is used for.</summary>
    internal int GetTypeInfo(uint iTInfo, uint lcid, out IntPtr ppTypeInfo)
    {
        var info = IntPtr.Zero;
        var hr = Vtbl->GetTypeInfo(_dispatchPtr, iTInfo, lcid, &info);
        ppTypeInfo = info;
        return hr;
    }

    internal int Invoke(int dispId, Guid* pIid, uint lcid, ushort flags, DispParams* pDp,
        Variant* pResult, ExceptionInfo* pExcep, uint* pArgErr)
    {
        return Vtbl->Invoke(_dispatchPtr, dispId, pIid, lcid, flags, pDp, pResult, pExcep, pArgErr);
    }
}
