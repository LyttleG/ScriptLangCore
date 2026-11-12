namespace DispatchVtbl;

// ───────────────────────────────────────────────────────────────────────────
// COM vtable definitions, each struct maps one COM interface's vtable
// layout as a sequential struct of function pointers.
// ───────────────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct UnknownVtable
{
    public delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;
}

/// <summary>THE GLOBAL INTERFACE TABLE, which is how an interface the HOST holds becomes usable on another thread of
/// this process. Slots 0 to 2 are IUnknown's; the three this engine calls are 3, 4 and 5.
/// <para>
/// It exists here because of one measured case: a DLL loaded inside Excel must reach the Excel instance hosting it, and
/// that instance's pointer belongs to Excel's apartment. `IDispatch` cannot be used from another apartment without
/// marshalling (measured: it stalls until the owning thread pumps), while a reference registered here can.
/// </para></summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct GlobalInterfaceTableVtable
{
    public delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;

    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, uint*, int> RegisterInterfaceInGlobal;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, int> RevokeInterfaceFromGlobal;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int> GetInterfaceFromGlobal;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DispatchVtable
{
    public delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;

    public delegate* unmanaged[Stdcall]<IntPtr, uint*, int> GetTypeInfoCount;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int> GetTypeInfo;
    public delegate* unmanaged[Stdcall]<IntPtr, void*, void*, uint, uint, void*, int> GetIDsOfNames;
    public delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, DispParams*, Variant*, ExceptionInfo*, uint*, int> Invoke;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct TypeInfoVtable
{
    // Every vtable entry is one pointer wide, so ONLY the count matters for the offsets and the unused slots can
    // stay placeholders. Deliberately only slot 7 is typed: it is the one call whose behaviour is MEASURED, and
    // nothing here calls a slot whose signature has not been verified.
    public IntPtr QueryInterface;                                   // 0
    public IntPtr AddRef;                                           // 1
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;      // 2
    public IntPtr GetTypeAttr;                                      // 3
    public IntPtr GetTypeComp;                                      // 4
    public IntPtr GetFuncDesc;                                      // 5
    public IntPtr GetVarDesc;                                       // 6

    /// <summary>ITypeInfo::GetNames(memid, rgBstrNames, cMaxNames, pcNames), a member's name followed by its
    /// PARAMETER NAMES, IN ORDER. Measured on Excel: `GetNames(SaveAs = 3174)` returns 14 names, `AccessMode`
    /// among them at parameter position 6, and a named-only `SaveAs(Filename:=…, AccessMode:=3)` through a
    /// typelib-driven binder wrote a REAL workbook (FileFormat 51, not the 6 a mis-passed position gives). So the
    /// parameter's POSITION is the dispid IDispatch wants in rgdispidNamedArgs, and this ONE call is the whole
    /// missing piece. The other documented route, ITypeInfo::GetIDsOfNames (slot 10), answers E_NOTIMPL on Excel
    ///, measured, which is why it is not used here.</summary>
    public delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, uint, uint*, int> GetNames;   // 7
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EnumVariantVtable
{
    public delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;

    public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint*, int> Next;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, int> Skip;
    public delegate* unmanaged[Stdcall]<IntPtr, int> Reset;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> Clone;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SafeArrayBound
{
    public uint cElements;
    public int lLbound;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DispParams
{
    public IntPtr rgvarg;
    public IntPtr rgdispidNamedArgs;
    public uint cArgs;
    public uint cNamedArgs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ExceptionInfo
{
    public ushort wCode;
    public ushort wReserved;
    public IntPtr bStrSource;
    public IntPtr bStrDescription;
    public IntPtr bStrHelpFile;
    public uint dwHelpContext;
    public IntPtr pvReserved;
    public IntPtr pfnDeferredFillIn;
    public int sCode;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    /// <summary>Size of VARIANT on x64 (used instead of sizeof(Variant) to avoid unsafe).</summary>
    internal const int SIZE = 24;

    // ── Header (vt + reserved, 8 bytes) ───────────────────────────────
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(2)] public ushort wReserved1;
    [FieldOffset(4)] public ushort wReserved2;
    [FieldOffset(6)] public ushort wReserved3;

    // ── 8-byte data union (all fields overlap at offset 8) ─────────────

    // All ORIGINAL field names kept exactly as they were
    [FieldOffset(8)] public int iVal;       // VT_I4   (original, keep)
    [FieldOffset(8)] public double dVal;       // VT_R8   (original, keep)
    [FieldOffset(8)] public long lVal;       // VT_I8   (original, keep)
    [FieldOffset(8)] public ulong ullVal;     // VT_UI8  (original, keep)
    [FieldOffset(8)] public short sVal;       // VT_I2   (original, keep)
    [FieldOffset(8)] public uint uiVal;      // VT_UI4  (original, keep as uint)
    [FieldOffset(8)] public short boolVal;    // VT_BOOL (original, keep)
    [FieldOffset(8)] public IntPtr llVal;      // generic  (original, keep as IntPtr)
    [FieldOffset(8)] public IntPtr bstrVal;    // VT_BSTR  (original, keep)

    // NEW fields (added without changing existing names)
    [FieldOffset(8)] public byte bVal;       // VT_UI1  (new)
    [FieldOffset(8)] public sbyte cVal;       // VT_I1   (new)
    [FieldOffset(8)] public float fltVal;     // VT_R4   (new)
    [FieldOffset(8)] public IntPtr pdispVal;   // VT_DISPATCH (new alias)
    [FieldOffset(8)] public IntPtr punkVal;    // VT_UNKNOWN  (new alias)
}

// ───────────────────────────────────────────────────────────────────────────
// COM constants and well-known GUIDs
// ───────────────────────────────────────────────────────────────────────────

internal static class ComConst
{
    internal const uint CLSCTX_SERVER = 0x5;
    internal const ushort DISPATCH_METHOD = 0x1;
    internal const ushort DISPATCH_PROPERTYGET = 0x2;
    internal const ushort DISPATCH_PROPERTYPUT = 0x4;

    // IDispatch failure codes the wrapper has to tell apart: "no such name" is a MISS, the script provider
    // declines it, so the engine reports the member in its own terms and a script extension member gets its
    // turn, while every other HRESULT is a real failure the friendly-message map renders as a sentence.
    internal const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    internal const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    internal const int DISP_E_EXCEPTION = unchecked((int)0x80020009);
    // The value the ambient IDispatch of the OLE host answers an unusable GetIDsOfNames call with. Its comment
    // used to name DISP_E_BADPARAMCOUNT, which is 0x8002000E, this is the TYPEMISMATCH code, and the value is
    // kept as it always was: that path needs a hosted ActiveX control to exercise, so a semantic correction
    // there would be an untested interop change rather than a tidy-up.
    internal const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);

    internal const ushort VT_EMPTY = 0;
    /// <summary>VT_ERROR, with DISP_E_PARAMNOTFOUND in the data field this IS `Missing`: the argument was
    /// not supplied. VBA's `After:=` is exactly this, and without it an optional parameter cannot be skipped.</summary>
    internal const ushort VT_ERROR = 10;
    internal const ushort VT_NULL = 1;
    internal const ushort VT_I2 = 2;
    internal const ushort VT_I4 = 3;
    internal const ushort VT_R4 = 4;
    internal const ushort VT_R8 = 5;
    internal const ushort VT_CY = 6;
    internal const ushort VT_DATE = 7;
    internal const ushort VT_BSTR = 8;
    internal const ushort VT_DISPATCH = 9;
    internal const ushort VT_I1 = 16;
    internal const ushort VT_UI1 = 17;
    internal const ushort VT_BOOL = 11;
    internal const ushort VT_UNKNOWN = 13;
    internal const ushort VT_UI2 = 18;
    internal const ushort VT_UI4 = 19;
    internal const ushort VT_I8 = 20;
    internal const ushort VT_UI8 = 21;
    internal const ushort VT_ARRAY = 0x2000;
    internal const ushort VT_VARIANT = 0xC;

    internal const uint COINIT_APARTMENTTHREADED = 0x2;
    internal const uint APTTYPE_STA = 0;
    internal const uint APTTYPE_MAINSTA = 3;
    internal const int CO_E_NOTINITIALIZED = unchecked((int)0x800401F0);

    /// <summary>The thread already chose an incompatible apartment, so it cannot be changed.
    /// Measured: <c>OleInitialize</c> on a thread that called <c>CoInitializeEx(MTA)</c> answers this.</summary>
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    internal static Guid IidIUnknown => new("00000000-0000-0000-C000-000000000046");
    internal static Guid IidIDispatch => new("00020400-0000-0000-C000-000000000046");
    internal static Guid IidNull => Guid.Empty;
    internal static Guid IidEnumVariant => new("00020404-0000-0000-C000-000000000046");
}
