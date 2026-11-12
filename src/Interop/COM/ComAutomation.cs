using static DispatchVtbl.NativeMethods;

namespace DispatchVtbl;

/// <summary>
/// Factory methods for creating and binding COM objects via IDispatch.
/// COM initialization is handled by <see cref="ComInit"/>.
/// All vtable dispatches go through <see cref="VtableCall"/>.
/// </summary>
internal static class ComAutomation
{
    public static ComObject CreateObject(string progId, uint clsCtx = ComConst.CLSCTX_SERVER)
    {
        ComInit.EnsureInitialized();

        var localIUnknown = ComConst.IidIUnknown;
        var localIDispatch = ComConst.IidIDispatch;

        var hr = ClsidFromProgId(progId, out var clsid);
        if (hr < 0)
            throw new COMException($"ProgID '{progId}' is not registered.", hr);

        hr = CoCreateInstance(in clsid, IntPtr.Zero, clsCtx, in localIUnknown, out var pUnk);
        if (hr < 0)
            throw new COMException(FailedToStart(progId, hr), hr);

        hr = VtableCall.QueryInterface(pUnk, ref localIDispatch, out var pDispatch);
        VtableCall.Release(pUnk);

        if (hr < 0 || pDispatch == IntPtr.Zero)
            throw new COMException("Object instance does not implement IDispatch.", hr);

        return new ComObject(pDispatch);
    }

    /// <summary>Why <c>CoCreateInstance</c> failed, in words a reader can act on.
    /// <para>
    /// This is the system refusing to LAUNCH the server, so the code says which kind of refusal it was. Measured
    /// 2026-10-31 from a report: this site said <c>"CoCreateInstance allocation fault."</c> for every failure, so
    /// <c>0x80080005</c> (CO_E_SERVER_EXEC_FAILURE, i.e. the application could not be started at all) read as an
    /// out-of-memory problem and sent the reader after the heap. The HRESULT itself is added by the message map, so
    /// these sentences never repeat the code.
    /// </para></summary>
    private static string FailedToStart(string progId, int hr) => hr switch
    {
        // Excel answers this while a killed instance is still shutting down, while it shows a modal dialog, and when
        // its server registration needs a repair. Measured: after stray EXCEL.EXE processes were killed, activation
        // answered this for about twenty seconds and then worked again.
        unchecked((int)0x80080005) =>
            $"{progId} could not be started: the system could not launch it. Check for an instance that is still "
            + "shutting down (wait and retry), a dialog it is showing, or an install that needs a repair",
        unchecked((int)0x80040154) =>
            $"{progId} could not be started: no server is registered for it, so the application may not be installed",
        unchecked((int)0x80070005) =>
            $"{progId} could not be started: access denied, so a security policy or an elevation prompt refused it",
        unchecked((int)0x8007000E) => $"there was not enough memory to start {progId}",
        unchecked((int)0x8000401A) =>
            $"{progId} could not be started: it runs under another user account, which this process cannot reach",
        _ => $"{progId} could not be started"
    };

    /// <summary>Does the ROT still hand out that class? A PROBE, and it RELEASES what it takes.
    /// <para>
    /// This exists because resolving to ask the question WRAPS the instance in a tracked ComObject, and a wrapper
    /// pins the server: the run-end report polled through <c>GetObject</c>, so every poll extended the life of the
    /// very server it was waiting to see go. Measured: a script whose last statement was <c>app.Quit()</c> was still
    /// reported after a THREE SECOND grace, and its Excel only exited once the run had ended and the nodes were
    /// released. <c>Com.IsAlive("Excel.Application")</c> shares this probe, so asking it cannot delay a shutdown
    /// either.
    /// </para>
    /// Nothing here outlives the call, so asking the question cannot change the answer.</summary>
    internal static bool IsRegisteredInRot(string progId)
    {
        ComInit.EnsureInitialized();

        Guid clsid;
        if (progId.Contains('{', StringComparison.Ordinal) && progId.Contains('}', StringComparison.Ordinal))
        {
            clsid = Guid.Parse(progId);
        }
        else if (ClsidFromProgId(progId, out clsid) < 0)
        {
            return false;
        }

        if (GetActiveObject(clsid, IntPtr.Zero, out var pUnk) < 0 || pUnk == IntPtr.Zero)
            return false;
        VtableCall.Release(pUnk);
        return true;
    }

    public static ComObject GetObject(string? pathName = null, string? progId = null)
    {
        ComInit.EnsureInitialized();

        int hr;
        var localIUnknown = ComConst.IidIUnknown;

        if (string.IsNullOrEmpty(pathName))
        {
            if (string.IsNullOrEmpty(progId))
                throw new ArgumentException("At least one of pathName or progId must be provided.");

            Guid clsid;
            if (progId.Contains('{', StringComparison.Ordinal) && progId.Contains('}', StringComparison.Ordinal))
            {
                clsid = Guid.Parse(progId);
            }
            else
            {
                hr = ClsidFromProgId(progId, out clsid);
                if (hr < 0)
                    throw new COMException($"ProgID '{progId}' is not registered.", hr);
            }

            // Fast path: GetActiveObject, direct CLSID lookup in the ROT (no enumeration)
            hr = GetActiveObject(clsid, IntPtr.Zero, out var pUnkResult);
            if (hr < 0 || pUnkResult == IntPtr.Zero)
                throw new COMException($"No running instance of '{progId}' found.");

            return WrapIUnknownAsComObject(pUnkResult);
        }

        hr = CreateBindCtx(0, out var pbc2);
        if (hr < 0)
            throw new COMException("CreateBindCtx failed.", hr);

        try
        {
            hr = MkParseDisplayName(pbc2, pathName, out _, out var pMk);
            if (hr < 0)
                throw new COMException($"Cannot parse moniker from '{pathName}'.", hr);

            try
            {
                hr = BindMoniker(pMk, 0, in localIUnknown, out var pUnkBound);
                return hr < 0
                    ? throw new COMException($"BindMoniker failed for '{pathName}'.", hr)
                    : WrapIUnknownAsComObject(pUnkBound);
            }
            finally
            {
                VtableCall.Release(pMk);
            }
        }
        finally
        {
            VtableCall.Release(pbc2);
        }
    }

    private static ComObject WrapIUnknownAsComObject(IntPtr pUnk)
    {
        var iid = ComConst.IidIDispatch;
        var hr = VtableCall.QueryInterface(pUnk, ref iid, out var pDispatch);
        VtableCall.Release(pUnk);

        if (hr < 0 || pDispatch == IntPtr.Zero)
            throw new COMException("Object does not implement IDispatch.", hr);

        return new ComObject(pDispatch, addRef: false);
    }
}
