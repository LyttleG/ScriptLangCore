namespace DispatchVtbl;

/// <summary>
/// Centralized P/Invoke declarations for COM and OLE Automation native APIs.
/// All COM interop calls route through here for AOT compatibility.
/// </summary>
internal static partial class NativeMethods
{
    // ── ole32.dll ─────────────────────────────────────────────────────────

    [LibraryImport("ole32.dll", EntryPoint = "CLSIDFromProgID", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int ClsidFromProgId(string lpszProgId, out Guid lpClsid);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rClsid, IntPtr pUnkOuter, uint dwClsContext, in Guid rIid, out IntPtr ppv);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial void CoUninitialize();

    // OLE initialization, required to HOST ActiveX controls (OleSite/DoVerb).
    // OleInitialize initializes COM as STA internally (a superset of CoInitializeEx),
    // so it is safe for every Com.Run session.
    [LibraryImport("ole32.dll")]
    internal static partial int OleInitialize(IntPtr pvReserved);

    [LibraryImport("ole32.dll")]
    internal static partial void OleUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int CreateBindCtx(uint reserved, out IntPtr pbc);

    [LibraryImport("ole32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int MkParseDisplayName(IntPtr pbc, string szUserName, out uint pchEaten, out IntPtr ppmk);

    [LibraryImport("ole32.dll")]
    internal static partial int CoGetApartmentType(out uint pAptType, out uint pAptQualifier);

    [LibraryImport("ole32.dll")]
    internal static partial int BindMoniker(IntPtr pmk, uint grfOpt, in Guid iidResult, out IntPtr ppvObj);

    [LibraryImport("ole32.dll")]
    internal static partial int GetRunningObjectTable(uint reserved, out IntPtr pprot);

    // ── oleaut32.dll ──────────────────────────────────────────────────────

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial void VariantClear(IntPtr pvarg);

    [LibraryImport("oleaut32.dll")]
    internal static partial int GetActiveObject(in Guid rclsid, IntPtr pvReserved, out IntPtr ppunk);

    [LibraryImport("oleaut32.dll")]
    internal static partial IntPtr SafeArrayCreate(ushort vt, uint cDims, in SafeArrayBound rGsaBound);

    [LibraryImport("oleaut32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SafeArrayPutElement(IntPtr psa, IntPtr rgIndices, IntPtr pv);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayDestroy(IntPtr psa);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetElement(IntPtr psa, IntPtr rgIndices, IntPtr pv);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetLBound(IntPtr psa, uint nDim, out int lLbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetUBound(IntPtr psa, uint nDim, out int lUbound);

    [LibraryImport("oleaut32.dll")]
    internal static partial int SafeArrayGetVartype(IntPtr psa, out ushort vt);
}
