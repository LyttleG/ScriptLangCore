#if SLC_EMBED_COM
namespace ScriptLangCore;

using System.Runtime.InteropServices;
using DispatchVtbl;

/// <summary>THE ENGINE AS AN IN-PROC COM SERVER: what `CreateObject("ScriptLangCore.Session")` gets in VBA.
///
/// This file is compiled ONLY into the `Embed` configuration (`SLC_EMBED_COM`), which is the one that publishes a
/// native SHARED library (`NativeLib=Shared` -> `publish-dll/ScriptLangCore.dll`). The exe legs are untouched: they
/// have no use for a class factory, and they must stay byte-identical.
///
/// IT ADDS NO ENGINE LOGIC. Every member is a marshalling shell over the same run bodies the C ABI uses
/// (`EmbedApi.RunTextBody` / `RunFileBody` / `CallBody`) and the same session object (`EmbedSession`), so a snippet
/// sent from Excel cannot behave differently from the same snippet sent by a C host. The verdict JSON a VBA caller
/// reads through `LastResult` is `EmbedApi.OutcomeJson`'s, not a second rendering of it.
///
/// THE FOUR RULES THAT DECIDE THE SHAPE
///   1. NOTHING THROWS ACROSS THE BOUNDARY. Every member body is wrapped, and a failure becomes a status plus a
///      sentence in `LastError`: an exception escaping into Excel would take the host's process with it.
///   2. THE SESSION OWNS ITS THREAD, NOT THE CALLER'S. The first draft of this leg created the session with
///      `{"thread":"host"}`, pinning the interpreter to the COM caller's apartment. MEASURED: `EmbedSession.Create`
///      refuses a host thread below 16 MB of stack, and Excel's main thread offers 1 MB, so every `CreateObject`
///      would have failed with "let the engine own a thread". The session is therefore created `"own"`: the engine
///      runs a 32 MB thread and a member call marshals to it and waits, which is exactly what a synchronous STA verb
///      means to the caller anyway.
///   3. NO TYPE LIBRARY, SO LATE BINDING ONLY. `GetTypeInfoCount` answers 0, which is why VBA writes
///      `Dim o As Object` rather than `As ScriptLangCore.Session`. Seven names are resolved case-insensitively.
///   4. THE OBJECT IS AN IDENTITY OVER A SESSION. One object, one `EmbedSession`, created lazily on first use and
///      torn down by `Free` (or by the last Release). A managed reference is NOT stored in the native allocation: the
///      object holds a `GCHandle` to its managed state, because a managed pointer written into `NativeMemory.Alloc`
///      memory is invisible to the GC and would be collected under the host.
///
/// The CLSID and ProgID below are the ONE source of truth: `tools/check-com-registration.sh` compares them with
/// `setup/com-register.reg` and `setup/ScriptLangCore.iss` and fails when the three disagree.</summary>
internal static unsafe class ComServer
{
    /// <summary>The ProgID Office writes in `CreateObject(...)`.</summary>
    internal const string ProgId = "ScriptLangCore.Session";

    /// <summary>The CLSID the ProgID points at. Never reuse it for a different engine image: a stale registration on a
    /// machine would then resolve to a DLL that answers half the members.</summary>
    internal const string ClsidText = "4A9D69C4-D9E6-4914-B7DF-ABC442BF5096";

    internal static readonly Guid Clsid = new(ClsidText);

    private static readonly Guid IID_IUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IID_IDispatch = new("00020400-0000-0000-C000-000000000046");
    private static readonly Guid IID_IClassFactory = new("00000001-0000-0000-C000-000000000046");

    // ── statuses ─────────────────────────────────────────────────────────────────────────────────────
    private const int S_OK = 0;
    private const int S_FALSE = 1;
    private const int E_NOINTERFACE = unchecked((int)0x80004002);
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const int E_FAIL = unchecked((int)0x80004005);
    private const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    // ── IDispatch statuses ──────────────────────────────────────────────────────────────────────────
    private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
    private const int DISP_E_TYPEMISMATCH = unchecked((int)0x80020005);
    private const int DISP_E_UNKNOWNNAME = unchecked((int)0x80020006);
    private const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);

    private const int DISPID_UNKNOWN = -1;

    // ── the curated surface: seven members, and no more ─────────────────────────────────────────────
    private const int DispVersion = 1;
    private const int DispRunText = 2;
    private const int DispRunFile = 3;
    private const int DispLastResult = 4;
    private const int DispLastError = 5;
    private const int DispEval = 6;
    private const int DispFree = 7;

    /// <summary>UPPER-CASE = the only spelling a reader has to get right: resolution is case-insensitive, which is
    /// what VBA's own members do. The order is the DISPID order and nothing depends on it.</summary>
    private static readonly (string Name, int Id)[] Members =
    [
        ("Version", DispVersion),
        ("RunText", DispRunText),
        ("RunFile", DispRunFile),
        ("LastResult", DispLastResult),
        ("LastError", DispLastError),
        ("Eval", DispEval),
        ("Free", DispFree)
    ];

    private const ushort VT_BYREF = 0x4000;

    /// <summary>`Dispparams` is declared here rather than reused because `DispatchVtbl` only needed the invoking side
    /// of it; the layout is the one COM defines (`rgvarg` FIRST, arguments in REVERSE order).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Dispparams
    {
        public Variant* rgvarg;
        public int* rgdispidNamedArgs;
        public uint cArgs;
        public uint cNamedArgs;
    }

    /// <summary>The native half of one COM object: a vtable pointer FIRST (COM reads it there), a reference count, and
    /// the `GCHandle` of the managed state. No managed reference is stored in this allocation.</summary>
    private struct ComObject
    {
        public void** Vtbl;
        public int Refs;
        public IntPtr State;
    }

    /// <summary>The managed half: one session, the output of the run in flight, and the sentinel that says a `Free`
    /// already happened. Only reachable through the object's `GCHandle`, so it lives exactly as long as the object.</summary>
    private sealed class ComState
    {
        internal EmbedSession? Session;
        internal readonly System.Text.StringBuilder Output = new();
        internal bool Freed;
    }

    private static int s_objectCount;
    private static int s_lockCount;

    // Built once, in declaration order, because the order IS the vtable. `static readonly` fields are initialised in
    // textual order, and the factory is built after the two tables it points at.
    private static readonly void** s_objectVtbl = BuildObjectVtable();
    private static readonly void** s_factoryVtbl = BuildFactoryVtable();
    private static readonly void** s_factory = BuildFactory();

    // ── exports ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The export COM itself calls in an in-proc server. One CLSID, one interface pair, and nothing else is
    /// registered, so any other CLSID is refused by name rather than by a null pointer.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllGetClassObject(Guid* rclsid, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_FAIL;
        *ppv = null;
        if (rclsid == null || riid == null) return E_FAIL;
        if (*rclsid != Clsid) return CLASS_E_CLASSNOTAVAILABLE;
        if (*riid != IID_IUnknown && *riid != IID_IClassFactory) return E_NOINTERFACE;
        *ppv = s_factory;
        return S_OK;
    }

    /// <summary>`S_OK` only when nothing is alive and no server lock is held: answering `S_OK` with an object out would
    /// let a host unload the image underneath its own calls.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow", CallConvs = [typeof(CallConvStdcall)])]
    public static int DllCanUnloadNow()
        => Volatile.Read(ref s_objectCount) == 0 && Volatile.Read(ref s_lockCount) == 0 ? S_OK : S_FALSE;

    // ── vtables ────────────────────────────────────────────────────────────────────────────────────

    private static void** BuildObjectVtable()
    {
        var vtable = (void**)NativeMemory.Alloc(7, (nuint)sizeof(void*));
        // IUnknown
        vtable[0] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, void**, int>)&ObjectQueryInterface;
        vtable[1] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&ObjectAddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<ComObject*, int>)&ObjectRelease;
        // IDispatch, in the order COM defines
        vtable[3] = (delegate* unmanaged[Stdcall]<ComObject*, uint*, int>)&GetTypeInfoCount;
        vtable[4] = (delegate* unmanaged[Stdcall]<ComObject*, uint, uint, void**, int>)&GetTypeInfo;
        vtable[5] = (delegate* unmanaged[Stdcall]<ComObject*, Guid*, char**, uint, uint, int*, int>)&GetIDsOfNames;
        vtable[6] = (delegate* unmanaged[Stdcall]<ComObject*, int, Guid*, uint, ushort, Dispparams*, Variant*, void*, uint*, int>)&Invoke;
        return vtable;
    }

    private static void** BuildFactoryVtable()
    {
        var vtable = (void**)NativeMemory.Alloc(5, (nuint)sizeof(void*));
        vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&FactoryQueryInterface;
        vtable[1] = (delegate* unmanaged[Stdcall]<void*, int>)&FactoryAddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<void*, int>)&FactoryRelease;
        vtable[3] = (delegate* unmanaged[Stdcall]<void*, void*, Guid*, void**, int>)&FactoryCreateInstance;
        vtable[4] = (delegate* unmanaged[Stdcall]<void*, int, int>)&FactoryLockServer;
        return vtable;
    }

    private static void** BuildFactory()
    {
        var factory = (void**)NativeMemory.Alloc(1, (nuint)sizeof(void*));
        factory[0] = s_factoryVtbl;
        return factory;
    }

    // ── IClassFactory (static: its refcounts are the image's lifetime, so they are constants) ──────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryQueryInterface(void* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_FAIL;
        if (riid != null && (*riid == IID_IUnknown || *riid == IID_IClassFactory))
        {
            *ppv = s_factory;
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryAddRef(void* self) => 2;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryRelease(void* self) => 1;

    /// <summary>Makes ONE object. `pUnkOuter` is refused rather than ignored: aggregation would need the outer object's
    /// `IUnknown` forwarded from every member, which this server does not implement.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryCreateInstance(void* self, void* pUnkOuter, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_FAIL;
        *ppv = null;
        if (pUnkOuter != null) return CLASS_E_NOAGGREGATION;
        if (riid == null) return E_FAIL;
        if (*riid != IID_IUnknown && *riid != IID_IDispatch) return E_NOINTERFACE;

        var state = new ComState();
        var handle = GCHandle.Alloc(state, GCHandleType.Normal);
        var comObject = (ComObject*)NativeMemory.Alloc((nuint)sizeof(ComObject));
        if (comObject == null)
        {
            handle.Free();
            return E_OUTOFMEMORY;
        }

        comObject->Vtbl = s_objectVtbl;
        comObject->Refs = 1;
        comObject->State = GCHandle.ToIntPtr(handle);
        Interlocked.Increment(ref s_objectCount);
        *ppv = comObject;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FactoryLockServer(void* self, int lockServer)
    {
        if (lockServer != 0) Interlocked.Increment(ref s_lockCount);
        else Interlocked.Decrement(ref s_lockCount);
        return S_OK;
    }

    // ── IUnknown on the object ─────────────────────────────────────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ObjectQueryInterface(ComObject* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return E_FAIL;
        if (riid != null && (*riid == IID_IUnknown || *riid == IID_IDispatch))
        {
            Interlocked.Increment(ref self->Refs);
            *ppv = self;
            return S_OK;
        }
        *ppv = null;
        return E_NOINTERFACE;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ObjectAddRef(ComObject* self) => Interlocked.Increment(ref self->Refs);

    /// <summary>On the last release the session is torn down (`TryDispose` runs the engine's own teardown on the
    /// session's thread) and the `GCHandle` is freed, so a host that drops the reference without calling `Free` still
    /// leaves nothing behind.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ObjectRelease(ComObject* self)
    {
        var remaining = Interlocked.Decrement(ref self->Refs);
        if (remaining != 0) return remaining;

        try
        {
            if (TryState(self, out var state))
            {
                TearDown(state);
                GCHandle.FromIntPtr(self->State).Free();
                self->State = IntPtr.Zero;
            }
        }
        catch
        {
            // Nothing may cross: a throw here runs on COM's release path, where there is no caller left to catch it.
        }

        NativeMemory.Free(self);
        Interlocked.Decrement(ref s_objectCount);
        return 0;
    }

    // ── IDispatch ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>0: no type library. That single answer is why Office binds late and sees `Object`.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoCount(ComObject* self, uint* count)
    {
        if (count == null) return E_FAIL;
        *count = 0;
        return S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfo(ComObject* self, uint typeInfo, uint lcid, void** ppTypeInfo)
    {
        if (ppTypeInfo != null) *ppTypeInfo = null;
        return E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIDsOfNames(ComObject* self, Guid* riid, char** names, uint count, uint lcid, int* dispIds)
    {
        try
        {
            if (dispIds == null || (count > 0 && names == null)) return E_FAIL;
            for (uint i = 0; i < count; i++) dispIds[i] = DISPID_UNKNOWN;
            if (count == 0) return S_OK;

            // Named arguments are not supported by any member, so a batch bigger than one name is refused as unknown
            // rather than resolved partially: VBA asks one name at a time.
            if (count != 1) return DISP_E_UNKNOWNNAME;
            var requested = Marshal.PtrToStringUni((IntPtr)names[0]);
            if (requested == null) return DISP_E_UNKNOWNNAME;
            foreach (var (name, id) in Members)
            {
                if (string.Equals(name, requested, StringComparison.OrdinalIgnoreCase))
                {
                    dispIds[0] = id;
                    return S_OK;
                }
            }
            return DISP_E_UNKNOWNNAME;
        }
        catch
        {
            return E_FAIL;
        }
    }

    /// <summary>The one place a member runs. Shape: `pResult` is NULL when the caller used the object in statement
    /// position and wants no value, which is NOT an error; arguments arrive REVERSED (`rgvarg[0]` is the LAST one),
    /// the rule that is easiest to get wrong and the reason this is one reviewed method rather than seven lambdas.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(ComObject* self, int dispId, Guid* riid, uint lcid, ushort flags,
                              Dispparams* parameters, Variant* pResult, void* pExceptionInfo, uint* puArgErr)
    {
        try
        {
            if (pResult != null)
            {
                pResult->vt = ComConst.VT_EMPTY;
                pResult->llVal = IntPtr.Zero;
            }

            if (!TryState(self, out var state)) return E_FAIL;
            if (state.Freed) return Result(pResult, "the session has been freed: one object holds one session, so create another with CreateObject");

            // A DISPID with neither a method nor a property flag is refused by the same rule COM uses.
            if ((flags & (ComConst.DISPATCH_METHOD | ComConst.DISPATCH_PROPERTYGET)) == 0) return DISP_E_MEMBERNOTFOUND;

            var argumentCount = parameters == null ? 0 : (int)parameters->cArgs;
            var arguments = parameters == null || parameters->rgvarg == null ? null : parameters->rgvarg;

            switch (dispId)
            {
                case DispVersion:
                    return Result(pResult, EmbedApi.VersionText());

                case DispLastResult:
                    return Result(pResult, state.Session?.LastResult ?? "");

                case DispLastError:
                    return Result(pResult, state.Session?.LastError ?? "");

                case DispRunText:
                {
                    // REVERSED ORDER, and it bites exactly here: `RunText(source)` has one argument at rgvarg[0],
                    // while `RunText(source, name)` is rgvarg[0]=name, rgvarg[1]=source, because COM puts the LAST
                    // argument first. The source is therefore the LAST slot, never slot 0 unconditionally.
                    if (!Argument(arguments, argumentCount, argumentCount - 1, out var source, puArgErr)) return DISP_E_TYPEMISMATCH;
                    string? name = null;
                    if (argumentCount > 1 && Argument(arguments, argumentCount, 0, out var label, puArgErr)) name = label;
                    return RunAndReport(state, itp => EmbedApi.RunTextBody(itp, source, name, default), pResult);
                }

                case DispRunFile:
                {
                    if (!Argument(arguments, argumentCount, 0, out var path, puArgErr)) return DISP_E_TYPEMISMATCH;
                    return RunAndReport(state, itp => EmbedApi.RunFileBody(itp, path, default), pResult);
                }

                case DispEval:
                {
                    // Eval("name", a, b): the LAST element of the reversed array is the member name.
                    if (argumentCount < 1) return DISP_E_BADPARAMCOUNT;
                    if (!Argument(arguments, argumentCount, argumentCount - 1, out var functionName, puArgErr))
                        return DISP_E_TYPEMISMATCH;

                    var callArguments = new List<object?>();
                    for (var i = argumentCount - 2; i >= 0; i--)
                    {
                        if (!TryConvert(arguments[i], out var value, puArgErr, (uint)(argumentCount - 1 - i)))
                            return DISP_E_TYPEMISMATCH;
                        callArguments.Add(value);
                    }
                    var args = callArguments.ToArray();
                    return RunAndReport(state, itp => EmbedApi.CallBody(itp, functionName, args), pResult, preferValue: true);
                }

                case DispFree:
                    TearDown(state);
                    state.Freed = true;
                    return Result(pResult, "freed");

                default:
                    return DISP_E_MEMBERNOTFOUND;
            }
        }
        catch (Exception ex)
        {
            // Rule 1: the sentence goes to the caller, the exception does not.
            return Result(pResult, "the engine failed: " + Friendly(ex));
        }
    }

    // ── the work ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ensures the object has a session, runs one verb on it, and reports the outcome through the SAME
    /// `EmbedApi.Report` the C ABI uses, so `LastResult` and `LastError` cannot disagree between the two doors.
    ///
    /// The answer handed back to the caller is the run's PRINTED OUTPUT when it printed anything, else the verdict
    /// JSON: a VBA caller's `MsgBox o.RunText("print 1+1")` shows what the script said, and a caller that wants the
    /// structured answer reads `LastResult`.</summary>
    private static int RunAndReport(ComState state, Func<Interpreter, (RunOutcome Outcome, string? ValueJson)> body,
                                    Variant* pResult, bool preferValue = false)
    {
        if (!TrySession(state, out var session, out var refusal)) return Result(pResult, refusal);

        state.Output.Clear();
        string? valueJson = null;
        if (!session.TryRun("com", (itp, token) =>
            {
                var (outcome, json) = body(itp);
                valueJson = json;
                return outcome;
            }, out var answer, out var error))
        {
            state.Session!.LastError = error ?? "the session refused the call";
            return Result(pResult, state.Session.LastError);
        }

        EmbedApi.Report(session, answer, valueJson);

        var printed = state.Output.ToString().TrimEnd('\r', '\n');
        if (printed.Length > 0) return Result(pResult, printed);
        if (preferValue && valueJson is { Length: > 0 }) return Result(pResult, valueJson);
        return Result(pResult, session.LastResult ?? "");
    }

    /// <summary>The session is created on FIRST USE rather than in `CreateInstance`, so `CreateObject` stays cheap and
    /// a machine whose engine cannot start still gets an object that says why, per member, in `LastError`.</summary>
    private static bool TrySession(ComState state, out EmbedSession session, out string refusal)
    {
        refusal = "";
        if (state.Session is { } existing)
        {
            session = existing;
            return true;
        }

        // `own`, not `host`: measured, a COM caller's thread (Excel's is 1 MB) cannot meet the 16 MB floor a pinned
        // session needs, and `EmbedSession.Create` refuses it by name. The engine's own thread also gives the run the
        // 32 MB the depth guard measures against.
        const string optionsJson = "{\"thread\":\"own\"}";
        var options = EmbedOptions.Parse(optionsJson, out var optionError);
        if (options == null)
        {
            refusal = optionError ?? "the COM server's own options were refused";
            session = null!;
            return false;
        }

        var created = EmbedSession.Create(options, out var createError);
        if (created == null)
        {
            refusal = createError ?? "the engine could not start a session";
            session = null!;
            return false;
        }

        // The engine's own `print` goes to this sink while the call waits, which is what the member returns.
        created.LineSink = (channel, line) => state.Output.Append(line).Append('\n');
        state.Session = created;
        session = created;
        return true;
    }

    private static void TearDown(ComState state)
    {
        if (state.Session is not { } session) return;
        state.Session = null;
        try { session.TryDispose(out _); }
        catch { /* nothing may cross on a release path */ }
    }

    private static bool TryState(ComObject* self, out ComState state)
    {
        state = null!;
        if (self == null || self->State == IntPtr.Zero) return false;
        if (GCHandle.FromIntPtr(self->State).Target is not ComState found) return false;
        state = found;
        return true;
    }

    // ── marshalling ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads a VARIANT that arrived BY REFERENCE as the value it points at. MEASURED, and it is why a VBA
    /// `Engine.Eval(name, arguments(0))` answered error 13, "Type mismatch", while the same call from C worked: a
    /// late-bound VBA call passes a `Variant` argument (a ParamArray element, a `Dim v As Variant`) as
    /// `VT_VARIANT|VT_BYREF`, and a TYPED variable as `VT_x|VT_BYREF`, while this server's converter only knew the
    /// by-value shapes, so the invocation answered DISP_E_TYPEMISMATCH. Both shapes are read here, and a by-reference
    /// VARIANT that itself points at a by-reference value is followed, bounded, because a malformed caller could
    /// otherwise point one at itself.</summary>
    private static bool Dereference(Variant value, out Variant result)
    {
        result = value;
        for (var depth = 0; depth < 4; depth++)
        {
            if ((result.vt & VT_BYREF) == 0) return true;
            if (result.llVal == IntPtr.Zero) return false;
            var pointer = result.llVal;
            switch ((ushort)(result.vt & 0x0FFF))
            {
                case ComConst.VT_VARIANT:
                    result = ((Variant*)pointer)[0];
                    continue;
                case ComConst.VT_EMPTY:
                    result = new Variant { vt = ComConst.VT_EMPTY };
                    return true;
                case ComConst.VT_NULL:
                    result = new Variant { vt = ComConst.VT_NULL };
                    return true;
                case ComConst.VT_ERROR:
                    result = new Variant { vt = ComConst.VT_ERROR };
                    return true;
                case ComConst.VT_BSTR:
                    result = new Variant { vt = ComConst.VT_BSTR, bstrVal = *(IntPtr*)pointer };
                    return true;
                case ComConst.VT_I2:
                    result = new Variant { vt = ComConst.VT_I2, sVal = *(short*)pointer };
                    return true;
                case ComConst.VT_I4:
                    result = new Variant { vt = ComConst.VT_I4, iVal = *(int*)pointer };
                    return true;
                case ComConst.VT_I8:
                    result = new Variant { vt = ComConst.VT_I8, lVal = *(long*)pointer };
                    return true;
                case ComConst.VT_R4:
                    result = new Variant { vt = ComConst.VT_R4, fltVal = *(float*)pointer };
                    return true;
                case ComConst.VT_R8:
                    result = new Variant { vt = ComConst.VT_R8, dVal = *(double*)pointer };
                    return true;
                case ComConst.VT_CY:
                    result = new Variant { vt = ComConst.VT_CY, lVal = *(long*)pointer };
                    return true;
                case ComConst.VT_DATE:
                    result = new Variant { vt = ComConst.VT_DATE, dVal = *(double*)pointer };
                    return true;
                case ComConst.VT_BOOL:
                    result = new Variant { vt = ComConst.VT_BOOL, boolVal = *(short*)pointer };
                    return true;
                case ComConst.VT_UI1:
                    result = new Variant { vt = ComConst.VT_UI1, bVal = *(byte*)pointer };
                    return true;
                case ComConst.VT_I1:
                    result = new Variant { vt = ComConst.VT_I1, cVal = *(sbyte*)pointer };
                    return true;
                case ComConst.VT_DISPATCH:
                    result = new Variant { vt = ComConst.VT_DISPATCH, llVal = *(IntPtr*)pointer };
                    return true;
                case ComConst.VT_UNKNOWN:
                    result = new Variant { vt = ComConst.VT_UNKNOWN, llVal = *(IntPtr*)pointer };
                    return true;
                default:
                    return false;
            }
        }
        return false;
    }

    /// <summary>Reads one argument as TEXT, for a parameter that must be a string (a function name, a path). The
    /// by-reference shapes go through `Dereference` first, so a `Variant` holding a BSTR, a `String` variable passed
    /// ByRef and a ByVal literal all answer the same. Anything that is not a BSTR is `DISP_E_TYPEMISMATCH` with the
    /// index in `puArgErr`.</summary>
    private static bool Argument(Variant* arguments, int count, int index, out string text, uint* puArgErr)
    {
        text = "";
        if (arguments == null || index < 0 || index >= count)
        {
            if (puArgErr != null) *puArgErr = (uint)Math.Max(index, 0);
            return false;
        }

        if (!Dereference(arguments[index], out var value) || value.vt != ComConst.VT_BSTR)
        {
            if (puArgErr != null) *puArgErr = (uint)index;
            return false;
        }

        text = value.bstrVal == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(value.bstrVal) ?? "";
        return true;
    }

    /// <summary>A script argument for `Eval`, through the SAME converter the engine's COM client half uses
    /// (`VariantConverter.UnwrapVariant`), so a VARIANT means one thing in both directions. The by-reference shapes
    /// are dereferenced first, which is what a VBA caller sends.</summary>
    private static bool TryConvert(Variant argument, out object? value, uint* puArgErr, uint index)
    {
        value = null;
        try
        {
            if (!Dereference(argument, out var byValue))
            {
                if (puArgErr != null) *puArgErr = index;
                return false;
            }
            value = VariantConverter.UnwrapVariant<object>(byValue);
            return true;
        }
        catch
        {
            if (puArgErr != null) *puArgErr = index;
            return false;
        }
    }

    /// <summary>Hands a string back as a caller-owned `BSTR` (the caller frees it with `SysFreeString`). A NULL result
    /// pointer means statement position, which is not an error: the member still ran.</summary>
    private static int Result(Variant* pResult, string text)
    {
        if (pResult == null) return S_OK;
        pResult->vt = ComConst.VT_BSTR;
        pResult->bstrVal = Marshal.StringToBSTR(text);
        return S_OK;
    }

    private static string Friendly(Exception ex) => ex.GetBaseException().Message;
}
#endif
