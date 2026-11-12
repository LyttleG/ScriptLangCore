namespace DispatchVtbl;
// ───────────────────────────────────────────────────────────────────────────
// Minimal OLE in-place hosting for ActiveX controls (the "EmbedBrowserObject" pattern,
// adapted to this codebase's raw-vtable COM style).
//
// A script creates an ActiveX control (e.g. `Com.CreateObject("Shell.Explorer")`
// → IWebBrowser2 IDispatch), then calls the host helper (Com.HostActiveX) with a
// Win32 parent window. This file:
//   1. OleSite, a COM object implementing IOleClientSite + IOleInPlaceSite +
//      IOleControlSite + an ambient-properties IDispatch (the WebBrowser control
//      queries all of them during DoVerb), so the control can be in-place
//      activated inside the host window.
//   2. OleEmbed.Activate, QI IOleObject → SetClientSite → DoVerb(INPLACEACTIVATE)
//      → QI IOleInPlaceObject → GetWindow → returns the control's child HWND.
//      The script parents that HWND into its window and drives the control
//      through its IDispatch automation (Navigate/GoBack/...).
// ───────────────────────────────────────────────────────────────────────────

internal static class OleGuids
{
    internal static Guid IidIOleObject => new("00000112-0000-0000-C000-000000000046");
    internal static Guid IidIOleClientSite => new("00000118-0000-0000-C000-000000000046");
    internal static Guid IidIOleInPlaceSite => new("00000119-0000-0000-C000-000000000046");
    internal static Guid IidIOleInPlaceObject => new("00000113-0000-0000-C000-000000000046");
    internal static Guid IidIOleControlSite => new("6d5140c1-7436-11ce-8034-00aa006009fa");
    internal static Guid IidIDispatch => ComConst.IidIDispatch;
    internal static Guid IidIUnknown => ComConst.IidIUnknown;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OleRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public OleRect(int x, int y, int w, int h) { Left = x; Top = y; Right = x + w; Bottom = y + h; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct OleInPlaceFrameInfo
{
    public uint Cb;
    public int FmdiApp;
    public IntPtr HwndFrame;
    public IntPtr Haccel;
    public uint CaccelEntries;
}

// ── Vtable of the CONTROL we call into (fields only through the slots we use) ──
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OleObjectVtable
{
    public delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int> SetClientSite;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int> GetClientSite;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int> SetHostNames;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, int> Close;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int> SetMoniker;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int> GetMoniker;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int> InitFromData;
    public delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int> GetClipboardData;
    // iVerb, lpmsg, pClientSite, lindex, hwndParent, lprcPosRect
    public delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, int, IntPtr, OleRect*, int> DoVerb;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct OleInPlaceObjectVtable
{
    public delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int> QueryInterface;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> AddRef;
    public delegate* unmanaged[Stdcall]<IntPtr, uint> Release;
    public delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int> GetWindow; // IOleWindow slot 3
    public delegate* unmanaged[Stdcall]<IntPtr, int, int> ContextSensitiveHelp; // slot 4
    public delegate* unmanaged[Stdcall]<IntPtr, int> InPlaceDeactivate;        // slot 5
}

// ═══════════════════════════════════════════════════════════════════════════
// OleSite, the COM host object the control talks to during activation.
// ═══════════════════════════════════════════════════════════════════════════
//
// Unmanaged layout (one AllocHGlobal block; interface pointers are the block
// itself or +8/+0x10/+0x18 so the control can QI between them):
//   +0x00  IntPtr clientVtbl        <- IOleClientSite / IUnknown identity (base)
//   +0x08  IntPtr inPlaceVtbl       <- IOleInPlaceSite  (base + 0x08)
//   +0x10  IntPtr controlSiteVtbl   <- IOleControlSite  (base + 0x10)
//   +0x18  IntPtr dispatchVtbl      <- IDispatch ambients (base + 0x18)
//   +0x20  int    refCount
//   +0x28  IntPtr hostHwnd
//
// Everything is [UnmanagedCallersOnly] statics on the raw block, no managed
// state, nothing to GC-root. The control's own refs keep the block alive; when
// the count reaches zero the block frees itself.
// ═══════════════════════════════════════════════════════════════════════════
internal static unsafe partial class OleSite
{
    private const int OLEIVERB_INPLACEACTIVATE = 5;
    private const int OLEIVERB_SHOW = -1;
    private const int S_OK = 0;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int DISP_E_UNKNOWNNAME = ComConst.DISP_E_UNKNOWNNAME;
    private const int DISP_E_MEMBERNOTFOUND = ComConst.DISP_E_MEMBERNOTFOUND;
    private const int DISP_E_TYPEMISMATCH = ComConst.DISP_E_TYPEMISMATCH;

    // Ambient property dispids we answer (backcolor etc. are not ambient dispids in
    // the OC96 range unless requested by name, the control asks BY NAME via
    // GetIDsOfNames, and we map names to these internal ids).
    private const int AMB_BACKCOLOR = -701;
    private const int AMB_FORECOLOR = -702;
    private const int AMB_USERMODE = -719;
    private const int AMB_DISPLAYNAME = -725;
    private const int AMB_LOCALEID = -715;
    private const int AMB_RIGHTTOLEFT = -717;
    private const int AMB_MESSAGEREFLECT = -704;
    private const int AMB_SUPPORTSMNEMONICS = -718;

    private const int OFF_CLIENT_VTBL = 0x00;
    private const int OFF_INPLACE_VTBL = 0x08;
    private const int OFF_CONTROL_VTBL = 0x10;
    private const int OFF_DISPATCH_VTBL = 0x18;
    private const int OFF_CONTAINER_VTBL = 0x20;
    private const int OFF_FRAME_VTBL = 0x28;
    private const int OFF_REFCOUNT = 0x30;
    private const int OFF_HOSTHWND = 0x38;
    private const int BLOCK_SIZE = 0x40;

    // Built once (static ctor): the four vtables as pinned unmanaged blocks.
    private static readonly IntPtr ClientVtbl;
    private static readonly IntPtr InPlaceVtbl;
    private static readonly IntPtr ControlSiteVtbl;
    private static readonly IntPtr DispatchVtbl;
    private static readonly IntPtr FrameVtbl;
    private static readonly IntPtr ContainerVtbl;

    static OleSite()
    {
        // IOleContainer (IOleClientSite::GetContainer returns it; the classic minimal
        // embedder provides a real one): IParseDisplayName.ParseDisplayName +
        // IOleContainer.EnumObjects, LockContainer = 6 slots.
        ContainerVtbl = BuildVtable(6, i => i switch
        {
            0 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QiCoreEntry,
            1 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefAny,
            2 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseAny,
            3 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr*, IntPtr*, int>)&Stub_E_NOTIMPL_6,
            4 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&Stub_E_NOTIMPL_QI,
            5 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            _ => IntPtr.Zero
        });
        // IOleInPlaceFrame: IUnknown(3) + IOleWindow(2: GetWindow, ContextSensitiveHelp)
        // + IOleInPlaceUIWindow(4: GetBorder, RequestBorderSpace, SetBorderSpace,
        //   SetActiveObject) + InsertMenus, SetMenu, RemoveMenus, SetStatusText,
        //   EnableModeless, TranslateAccelerator = 15 slots. Mostly benign stubs.
        FrameVtbl = BuildVtable(15, i => i switch
        {
            0 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QiCoreEntry,
            1 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefAny,
            2 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseAny,
            3 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&InPlace_GetWindow,
            4 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            5 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&Stub_S_OK_3,
            6 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&Stub_S_OK_3,
            7 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&Stub_S_OK_3,
            8 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)&Stub_S_OK_MIX,
            9 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)&Stub_S_OK_4,
            10 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, IntPtr, int>)&Stub_S_OK_4,
            11 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            12 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&Stub_S_OK_PTR,
            13 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            14 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void*, void*, int>)&Stub_S_FALSE,
            _ => IntPtr.Zero
        });

        // IOleClientSite: IUnknown(3) + SaveObject, GetMoniker, GetContainer,
        // ShowObject, OnShowWindow, RequestNewObjectLayout = 9 slots.
        ClientVtbl = BuildVtable(12, i => i switch
        {
            0 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QiCoreEntry,
            1 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefAny,
            2 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseAny,
            3 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,
            4 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)&Stub_E_NOTIMPL_4,
            5 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&Client_GetContainer,
            6 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,
            7 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            8 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_E_NOTIMPL_1,
            9 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,     // control-site slot spill-over
            10 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            11 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_E_NOTIMPL_1,
            _ => IntPtr.Zero
        });
        // IOleInPlaceSite: IUnknown(3) + IOleWindow(GetWindow, ContextSensitiveHelp)
        // + CanInPlaceActivate, OnInPlaceActivate, OnUIActivate, GetWindowContext,
        // Scroll, OnUIDeactivate, OnInPlaceDeactivate, DiscardUndoState,
        // DeactivateAndUndo, OnPosRectChange = 15 slots.
        InPlaceVtbl = BuildVtable(15, i => i switch
        {
            0 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QiCoreEntry,
            1 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefAny,
            2 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseAny,
            3 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&InPlace_GetWindow,
            4 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            5 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,                 // CanInPlaceActivate
            6 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,                 // OnInPlaceActivate
            7 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,                 // OnUIActivate
            8 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, IntPtr*, OleRect*, OleRect*, OleInPlaceFrameInfo*, int>)&InPlace_GetWindowContext,
            9 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)&Stub_S_OK_3,                 // Scroll(SIZE)
            10 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,                // OnUIDeactivate
            11 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,                // OnInPlaceDeactivate
            12 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,                // DiscardUndoState
            13 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,                // DeactivateAndUndo
            14 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, OleRect*, int>)&Stub_S_OK_RECT,             // OnPosRectChange
            _ => IntPtr.Zero
        });
        // IOleControlSite: IUnknown(3) + OnControlInfoChanged, LockInPlaceActive,
        // GetExtendedControl, TransformCoords, TranslateAccelerator, OnFocus,
        // ShowPropertyFrame = 10 slots.
        ControlSiteVtbl = BuildVtable(10, i => i switch
        {
            0 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QiCoreEntry,
            1 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefAny,
            2 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseAny,
            3 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,
            4 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_S_OK_1,
            5 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&Stub_E_NOTIMPL_3,
            6 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, IntPtr*, int>)&Stub_E_NOTIMPL_4_4,
            7 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, void*, void*, void*, int>)&Stub_E_NOTIMPL_5,
            8 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Stub_S_OK_2,
            9 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&Stub_E_NOTIMPL_1,
            _ => IntPtr.Zero
        });
        // IDispatch (ambients): IUnknown(3) + GetTypeInfoCount, GetTypeInfo,
        // GetIDsOfNames, Invoke = 7 slots.
        DispatchVtbl = BuildVtable(7, i => i switch
        {
            0 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QiCoreEntry,
            1 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRefAny,
            2 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseAny,
            3 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&Ambient_GetTypeInfoCount,
            4 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)&Ambient_GetTypeInfo,
            5 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, void*, uint, uint, void*, int>)&Ambient_GetIDsOfNames,
            6 => (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, void*, Variant*, void*, uint*, int>)&Ambient_Invoke,
            _ => IntPtr.Zero
        });
    }

    private static IntPtr BuildVtable(int slots, Func<int, IntPtr> slotAt)
    {
        var p = Marshal.AllocHGlobal(slots * IntPtr.Size);
        for (var i = 0; i < slots; i++)
            Marshal.WriteIntPtr(p, i * IntPtr.Size, slotAt(i));
        return p;
    }

    // ── Allocation / lifetime ─────────────────────────────────────────────

    /// <summary>Creates the site for a host window. Returns the IOleClientSite
    /// pointer (the IUnknown identity).</summary>
    internal static IntPtr Create(IntPtr hostHwnd)
    {
        var block = Marshal.AllocHGlobal(BLOCK_SIZE);
        Marshal.WriteIntPtr(block, OFF_CLIENT_VTBL, ClientVtbl);
        Marshal.WriteIntPtr(block, OFF_INPLACE_VTBL, InPlaceVtbl);
        Marshal.WriteIntPtr(block, OFF_CONTROL_VTBL, ControlSiteVtbl);
        Marshal.WriteIntPtr(block, OFF_DISPATCH_VTBL, DispatchVtbl);
        Marshal.WriteIntPtr(block, OFF_FRAME_VTBL, FrameVtbl);
        Marshal.WriteIntPtr(block, OFF_CONTAINER_VTBL, ContainerVtbl);
        Marshal.WriteInt32(block, OFF_REFCOUNT, 1);     // creator reference
        Marshal.WriteIntPtr(block, OFF_HOSTHWND, hostHwnd);
        return block;
    }

    /// <summary>Releases the creator reference after handing the site to the control
    /// (SetClientSite / DoVerb have AddRef'd it by then).</summary>
    internal static void ReleaseCreator(IntPtr clientSitePtr) => ReleaseCore(clientSitePtr);

    private static IntPtr ToBase(IntPtr p)
    {
        var vt = Marshal.ReadIntPtr(p);
        if (vt == InPlaceVtbl) return IntPtr.Subtract(p, OFF_INPLACE_VTBL);
        if (vt == ControlSiteVtbl) return IntPtr.Subtract(p, OFF_CONTROL_VTBL);
        if (vt == DispatchVtbl) return IntPtr.Subtract(p, OFF_DISPATCH_VTBL);
        if (vt == FrameVtbl) return IntPtr.Subtract(p, OFF_FRAME_VTBL);
        if (vt == ContainerVtbl) return IntPtr.Subtract(p, OFF_CONTAINER_VTBL);
        return p;   // client site / identity
    }

    private static IntPtr HostOf(IntPtr p) => Marshal.ReadIntPtr(ToBase(p), OFF_HOSTHWND);
    private static ref int RefOf(IntPtr p) => ref *(int*)IntPtr.Add(ToBase(p), OFF_REFCOUNT);

    private static uint AddRefCore(IntPtr p) => (uint)Interlocked.Increment(ref RefOf(p));

    private static uint ReleaseCore(IntPtr p)
    {
        var b = ToBase(p);
        var rc = (uint)Interlocked.Decrement(ref RefOf(p));
        if (rc == 0)
            Marshal.FreeHGlobal(b);
        return rc;
    }

    // ── IUnknown entry points (shared by every interface view) ────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRefAny(IntPtr p) => AddRefCore(p);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseAny(IntPtr p) => ReleaseCore(p);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QiCoreEntry(IntPtr p, Guid* iid, IntPtr* ppv)
    {
        var b = ToBase(p);
        var g = *iid;
        if (g == OleGuids.IidIUnknown || g == OleGuids.IidIOleClientSite)
        {
            AddRefCore(b);
            *ppv = b;
            return S_OK;
        }
        if (g == OleGuids.IidIOleInPlaceSite)
        {
            AddRefCore(b);
            *ppv = IntPtr.Add(b, OFF_INPLACE_VTBL);
            return S_OK;
        }
        // IOleControlSite is deliberately refused: handing it out (S_OK) crashes the
        // WebBrowser control natively during SetClientSite in this environment, while
        // refusing it is fine, activation succeeds via DoVerb(OLEIVERB_SHOW).
        if (g == OleGuids.IidIOleControlSite)
        {
            *ppv = IntPtr.Zero;
            return E_NOINTERFACE;
        }
        if (g == OleGuids.IidIDispatch)
        {
            AddRefCore(b);
            *ppv = IntPtr.Add(b, OFF_DISPATCH_VTBL);
            return S_OK;
        }
        *ppv = IntPtr.Zero;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Client_GetContainer(IntPtr p, IntPtr* ppContainer)
    {
        var b = ToBase(p);
        if (ppContainer != null)
        {
            AddRefCore(b);
            *ppContainer = IntPtr.Add(b, OFF_CONTAINER_VTBL);
        }
        return S_OK;
    }

    // ── IOleInPlaceSite behaviours ────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InPlace_GetWindow(IntPtr p, IntPtr* phwnd)
    {
        *phwnd = HostOf(p);
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InPlace_GetWindowContext(IntPtr p, IntPtr* ppFrame, IntPtr* ppDoc,
        OleRect* prcPosRect, OleRect* prcClipRect, OleInPlaceFrameInfo* pFrameInfo)
    {
        var host = HostOf(p);
        var b = ToBase(p);
        // Provide the in-place frame (caller Releases it, AddRef the returned view).
        if (ppFrame != null)
        {
            AddRefCore(b);
            *ppFrame = IntPtr.Add(b, OFF_FRAME_VTBL);
        }
        if (ppDoc != null) *ppDoc = IntPtr.Zero;

        GetClientRect(host, out var rc);
        if (prcPosRect != null) *prcPosRect = new OleRect(0, 0, rc.right, rc.bottom);
        if (prcClipRect != null) *prcClipRect = new OleRect(0, 0, rc.right, rc.bottom);
        if (pFrameInfo != null)
        {
            pFrameInfo->Cb = (uint)sizeof(OleInPlaceFrameInfo);
            pFrameInfo->FmdiApp = 0;
            pFrameInfo->HwndFrame = host;
            pFrameInfo->Haccel = IntPtr.Zero;
            pFrameInfo->CaccelEntries = 0;
        }
        return S_OK;
    }

    // ── Ambient-properties IDispatch (GetIDsOfNames/Invoke) ───────────────

    private static int AmbientDispidOf(string name) => name.ToLowerInvariant() switch
    {
        "backcolor" => AMB_BACKCOLOR,
        "forecolor" => AMB_FORECOLOR,
        "usermode" => AMB_USERMODE,
        "displayname" => AMB_DISPLAYNAME,
        "localeid" => AMB_LOCALEID,
        "righttoleft" => AMB_RIGHTTOLEFT,
        "messagereflect" => AMB_MESSAGEREFLECT,
        "supports mnemonics" or "supportsmnemonics" => AMB_SUPPORTSMNEMONICS,
        _ => int.MinValue
    };

    private static void WriteAmbient(int dispid, Variant* outVal)
    {
        if (outVal == null) return;
        outVal->vt = 0;
        outVal->iVal = 0;
        switch (dispid)
        {
            case AMB_BACKCOLOR:
                outVal->vt = ComConst.VT_I4;
                outVal->iVal = unchecked((int)0x8000000F);   // COLOR_WINDOW
                break;
            case AMB_FORECOLOR:
                outVal->vt = ComConst.VT_I4;
                outVal->iVal = 0;                            // black
                break;
            case AMB_USERMODE:
            case AMB_MESSAGEREFLECT:
            case AMB_SUPPORTSMNEMONICS:
                outVal->vt = ComConst.VT_BOOL;
                outVal->boolVal = -1;                        // VARIANT_TRUE
                break;
            case AMB_RIGHTTOLEFT:
                outVal->vt = ComConst.VT_BOOL;
                outVal->boolVal = 0;
                break;
            case AMB_DISPLAYNAME:
                outVal->vt = ComConst.VT_BSTR;
                outVal->bstrVal = Marshal.StringToBSTR("ScriptLangCore");
                break;
            case AMB_LOCALEID:
                outVal->vt = ComConst.VT_I4;
                outVal->iVal = 0x0409;                       // en-US
                break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Ambient_GetTypeInfoCount(IntPtr p, uint* pctinfo)
    {
        if (pctinfo != null) *pctinfo = 0;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Ambient_GetTypeInfo(IntPtr p, uint iTInfo, uint lcid, IntPtr* ppTInfo)
    {
        if (ppTInfo != null) *ppTInfo = IntPtr.Zero;
        return E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Ambient_GetIDsOfNames(IntPtr p, Guid* riid, void* rgszNames, uint cNames, uint lcid, void* rgDispId)
    {
        if (cNames == 0 || rgszNames == null || rgDispId == null)
            return DISP_E_TYPEMISMATCH;   // "arguments not usable", see the constant's note (the comment here
                                          // named BADPARAMCOUNT, which is a different code)
        var name = Marshal.PtrToStringBSTR(((IntPtr*)rgszNames)[0]) ?? "";
        var dispid = AmbientDispidOf(name);
        if (dispid == int.MinValue)
        {
            ((int*)rgDispId)[0] = -1;                       // DISPID_UNKNOWN
            return DISP_E_UNKNOWNNAME;
        }
        ((int*)rgDispId)[0] = dispid;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Ambient_Invoke(IntPtr p, int dispidMember, Guid* riid, uint lcid, ushort wFlags,
        void* pDispParams, Variant* pVarResult, void* pExcepInfo, uint* puArgErr)
    {
        if (dispidMember is AMB_BACKCOLOR or AMB_FORECOLOR or AMB_USERMODE or AMB_DISPLAYNAME
            or AMB_LOCALEID or AMB_RIGHTTOLEFT or AMB_MESSAGEREFLECT or AMB_SUPPORTSMNEMONICS)
        {
            WriteAmbient(dispidMember, pVarResult);
            return S_OK;
        }
        return DISP_E_MEMBERNOTFOUND;
    }

    // ── Tolerated stubs ───────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_1(IntPtr _) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_2(IntPtr _, int __) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_3(IntPtr _, int __, int ___) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_RECT(IntPtr _, OleRect* __) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_MIX(IntPtr _, int __, IntPtr ___) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_4(IntPtr _, IntPtr __, IntPtr ___, IntPtr ____) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_OK_PTR(IntPtr _, IntPtr __) => S_OK;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_6(IntPtr _, IntPtr __, IntPtr ___, IntPtr* ____, IntPtr* _____) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_QI(IntPtr _, Guid* __, IntPtr* ___) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_S_FALSE(IntPtr _, void* __, void* ___) => 1;   // S_FALSE: not translated

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_1(IntPtr _) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_3(IntPtr p, IntPtr* pp)
    {
        if (pp != null) *pp = IntPtr.Zero;
        return E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_4(IntPtr _, uint __, uint ___, IntPtr* ____) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_4_4(IntPtr _, uint __, uint ___, uint ____, IntPtr* _____) => E_NOTIMPL;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Stub_E_NOTIMPL_5(IntPtr p, void* m, void* g, void* x)
    {
        return E_NOTIMPL;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int left, top, right, bottom; }

    [LibraryImport("user32.dll")]
    private static partial int GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [LibraryImport("ole32.dll")]
    private static partial int OleRun(IntPtr pUnknown);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out NativeMsg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMsg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }

    private static void PumpMessages()
    {
        const uint PM_REMOVE = 0x0001;
        var guard = 0;
        while (guard++ < 200 && PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            // (TranslateMessage is deliberately omitted: some environments' user32
            // shim does not export it, and it only synthesizes WM_CHAR.)
            DispatchMessage(ref msg);
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial IntPtr DispatchMessage(ref NativeMsg lpMsg);

    // ── Activation (used by OleEmbed) ─────────────────────────────────────

    internal static IntPtr Activate(IntPtr pDispatch, IntPtr hostHwnd, int x, int y, int w, int h, int verbHint = 0)
    {
        var iidOleObject = OleGuids.IidIOleObject;
        var hr = VtableCall.QueryInterface(pDispatch, ref iidOleObject, out var pOleObject);
        if (hr < 0 || pOleObject == IntPtr.Zero)
            throw new COMException($"ActiveX object does not implement IOleObject (0x{hr:X}).", hr);

        try
        {
            var vt = *(OleObjectVtable**)pOleObject;

            var site = Create(hostHwnd);
            try
            {
                hr = vt->SetClientSite(pOleObject, site);
                if (hr < 0)
                    throw new COMException($"IOleObject.SetClientSite failed (0x{hr:X}).", hr);

                // In-place activate: the site passed is the IOleClientSite; the control
                // QIs it for IOleInPlaceSite / IOleControlSite / ambients as needed.
                // OleRun first, mirrors the standard host sequence (WinForms/AxHost), and its HRESULT is CHECKED like
                // the two calls around it: a control that cannot be put into the running state cannot be activated, so
                // reporting it here names the real failure instead of leaving the DoVerb refusal below to describe it.
                hr = OleRun(pOleObject);
                if (hr < 0)
                    throw new COMException($"OleRun failed (0x{hr:X}).", hr);
                PumpMessages();
                var rc = new OleRect(x, y, w, h);

                // verb semantics: 0 = AUTO, try OLEIVERB_INPLACEACTIVATE (the standard for
                // visual OCX), and fall back to OLEIVERB_SHOW when the control refuses it
                // (the WebBrowser control's INPLACEACTIVATE returns E_FAIL against a minimal
                // site, while SHOW activates it in place). Callers may pin a verb instead
                // (5 = INPLACEACTIVATE only, -1 = SHOW only).
                var verb = verbHint == 0 ? OLEIVERB_INPLACEACTIVATE : verbHint;
                hr = vt->DoVerb(pOleObject, verb, IntPtr.Zero, site, 0, hostHwnd, &rc);
                if (hr < 0 && verbHint == 0)
                {
                    // Re-run the object before the fallback verb, a failed INPLACEACTIVATE
                    // may have left the control mid-initialization. This re-run is BEST EFFORT on purpose: the SHOW
                    // verb below is what decides, and its own result is checked, so a failed re-run here would only
                    // mask the refusal the caller needs.
                    _ = OleRun(pOleObject);
                    PumpMessages();
                    hr = vt->DoVerb(pOleObject, OLEIVERB_SHOW, IntPtr.Zero, site, 0, hostHwnd, &rc);
                }
                if (hr < 0)
                    throw new COMException($"IOleObject.DoVerb failed (0x{hr:X}).", hr);
            }
            finally
            {
                ReleaseCreator(site);   // the control's own refs keep the site alive
            }
            var iidInPlaceObject = OleGuids.IidIOleInPlaceObject;
            var hr2 = VtableCall.QueryInterface(pDispatch, ref iidInPlaceObject, out var pInPlaceObject);
            if (hr2 < 0 || pInPlaceObject == IntPtr.Zero)
                throw new COMException("ActiveX control does not implement IOleInPlaceObject.", hr2);
            try
            {
                IntPtr child = IntPtr.Zero;
                var gv = (*(OleInPlaceObjectVtable**)pInPlaceObject)->GetWindow(pInPlaceObject, &child);
                if (gv < 0 || child == IntPtr.Zero)
                    throw new COMException("IOleInPlaceObject.GetWindow returned no window.", gv);
                return child;
            }
            finally { VtableCall.Release(pInPlaceObject); }
        }
        finally { VtableCall.Release(pOleObject); }
    }
}

/// <summary>Thin wrapper used by the interpreter registration: activates a hosted
/// site for a control and returns the child HWND.</summary>
internal static class OleEmbed
{
    internal static IntPtr HostActiveX(IntPtr pDispatch, IntPtr hostHwnd, int x, int y, int w, int h, int verb = 0)
        => OleSite.Activate(pDispatch, hostHwnd, x, y, w, h, verb);
}

/// <summary>Remembers, per hosted child HWND, the COM node + activation parameters,
/// so a control can be torn down and re-activated (e.g. when the host window
/// crosses a DPI boundary, per-monitor hosts must re-host system-DPI-aware
/// ActiveX controls so they lay out at the new monitor's DPI).</summary>
internal static class OleHostRegistry
{
    private sealed class Rec
    {
        public required object Node;        // strong ref: keeps the managed RCW / IDispatch alive
        public required IntPtr Dispatch;
        public required int Verb;
    }

    private static readonly System.Collections.Generic.Dictionary<IntPtr, Rec> Map = new();

    internal static void Register(IntPtr child, object node, IntPtr dispatch, int verb)
    {
        if (child == IntPtr.Zero) return;
        lock (Map)
        {
            Map[child] = new Rec { Node = node, Dispatch = dispatch, Verb = verb };
        }
    }

    internal static void Move(IntPtr from, IntPtr to)
    {
        if (from == to) return;
        lock (Map)
        {
            if (Map.TryGetValue(from, out var rec))
            {
                Map.Remove(from);
                if (to != IntPtr.Zero) Map[to] = rec;
            }
        }
    }

    internal static bool TryGet(IntPtr child, out object node, out IntPtr dispatch, out int verb)
    {
        lock (Map)
        {
            if (child != IntPtr.Zero && Map.TryGetValue(child, out var rec))
            {
                node = rec.Node;
                dispatch = rec.Dispatch;
                verb = rec.Verb;
                return true;
            }
        }
        node = null!;
        dispatch = IntPtr.Zero;
        verb = 0;
        return false;
    }

    internal static void Remove(IntPtr child)
    {
        lock (Map) { if (child != IntPtr.Zero) Map.Remove(child); }
    }
}

internal static unsafe partial class OleEmbedRehost
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr DestroyWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);

    /// <summary>Tears down the in-place session of the hosted control on
    /// <paramref name="childHwnd"/> and activates it again with the given rect.
    /// Returns the new child HWND, or IntPtr.Zero when nothing was re-hosted.</summary>
    internal static unsafe IntPtr RehostChild(IntPtr childHwnd, IntPtr parentHwnd, int x, int y, int w, int h)
    {
        if (childHwnd == IntPtr.Zero
            || !OleHostRegistry.TryGet(childHwnd, out var node, out var dispatch, out var verb))
            return IntPtr.Zero;

        // 1) end the current in-place session (best effort, some controls have no
        //    IOleInPlaceObject or refuse, in which case we keep the old child).
        try
        {
            var iidInPlace = OleGuids.IidIOleInPlaceObject;
            var hr = VtableCall.QueryInterface(dispatch, ref iidInPlace, out var pIoip);
            if (hr >= 0 && pIoip != IntPtr.Zero)
            {
                try
                {
                    var vt = *(OleInPlaceObjectVtable**)pIoip;
                    if (vt->InPlaceDeactivate != null)
                        vt->InPlaceDeactivate(pIoip);
                }
                finally { VtableCall.Release(pIoip); }
            }
        }
        catch { /* best effort */ }

        // 2) re-activate against the same parent at the new rect
        IntPtr newChild;
        try
        {
            newChild = OleSite.Activate(dispatch, parentHwnd, x, y, w, h, verb);
        }
        catch { return IntPtr.Zero; }
        if (newChild == IntPtr.Zero) return IntPtr.Zero;

        // 3) drop the stale child window (the control usually destroys it itself
        //    during InPlaceDeactivate; destroy it if it survived) and re-point the
        //    registry at the fresh window.
        if (newChild != childHwnd)
        {
            try { if (IsWindow(childHwnd)) DestroyWindow(childHwnd); } catch { }
            OleHostRegistry.Move(childHwnd, newChild);
        }
        return newChild;
    }
}
