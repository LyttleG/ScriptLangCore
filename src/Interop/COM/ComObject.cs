using ScriptLangCore; // ScriptException, the engine's own script-facing text
using static DispatchVtbl.NativeMethods;
using static DispatchVtbl.VariantConverter;

namespace DispatchVtbl;

/// <summary>
/// Wraps an IDispatch COM interface pointer and provides strongly-typed
/// late-bound access to properties, methods, and collections.
/// All IDispatch calls go through the raw vtable for AOT compatibility.
/// </summary>
public sealed unsafe class ComObject : IDisposable
{
    /// <summary>Release what an ARGUMENT variant owns. A VARIANT passed to IDispatch::Invoke is the CALLER's to
    /// free (the callee must copy anything it keeps), and this bridge builds the array-valued ones itself, in
    /// ConvertToVariant's SAFEARRAY arms, so nothing else will. `SetRangeValues` owns its array right here; the
    /// property PUT is the one that cannot, because the server may still read that array once this returns, so it
    /// hands the array to the scope and the OUTERMOST scope's Dispose destroys it (ComScope.DeferArrayFree). Both
    /// arrive at DestroyArray, which is where a failed destroy stays uncounted.
    /// Deliberately NARROW: only a VT_ARRAY is destroyed. A BSTR is freed where it was allocated, and a
    /// VT_DISPATCH must be left alone, VariantClear would Release an interface the engine still holds, and the
    /// whole point of a wrapper is that the engine owns that reference.</summary>
    private static void FreeArgVariant(ref Variant v)
    {
        if (v.vt == (ComConst.VT_ARRAY | ComConst.VT_VARIANT) && v.llVal != IntPtr.Zero)
        {
            DestroyArray(v.llVal);
            v = default;
        }
    }

    /// <summary>The ONE place a SAFEARRAY we created is destroyed and counted, the argument path and the scope's
    /// deferred drain both come through here, so the create/destroy pair the regression check reads cannot drift
    /// between call sites. A destroy that FAILS is deliberately NOT counted: created != destroyed is exactly what a
    /// leak looks like.</summary>
    internal static bool DestroyArray(IntPtr psa)
    {
        // HRESULT convention, so NEGATIVE is failure. Testing `!= 0` instead would count a NON-ZERO SUCCESS code
        // (SafeArrayDestroy answers S_FALSE on one path) as a failure, and a FAILED destroy must stay uncounted:
        // created != destroyed is exactly what a leak looks like, which is the whole point of the pair.
        if (psa == IntPtr.Zero || SafeArrayDestroy(psa) < 0)
            return false;
        Interlocked.Increment(ref SafeArraysDestroyed);
        return true;
    }
    /// <summary>How many argument VARIANTs (and BSTR slots) a call may keep on the STACK before the bridge falls
    /// back to one unmanaged block. Both types are blittable, so the common call, a handful of arguments, costs
    /// NO allocation at all, managed or unmanaged, while a huge call still has a path that cannot overflow the
    /// stack. The free sites already guard on IntPtr.Zero, so both paths share them unchanged.
    /// (A stackalloc expression only converts to a pointer in a DECLARATION, measured, an assignment to an
    /// already-declared pointer is CS8346, which is why the stack branch declares and then assigns.)</summary>
    private const int StackArgCap = 8;
    private readonly DispatchInvoker _invoker = null!;
    private IntPtr _pDispatch;
    private readonly Dictionary<string, int> _dispIdCache = [];

    /// <summary>Dispids per TYPE, shared by every instance of one. A dispid belongs to the CLASS, not to an
    /// instance, and a per-OBJECT cache cannot serve the objects a server hands out FRESH on every access,
    /// which is most of Excel automation. Measured before this existed: `ws.Range("A1").Value` cost 4,131 and
    /// 4,171 ticks per read over two runs against 1,111 and 1,139 for the same read on a warm object, a ratio
    /// of exactly 3.0 both times, three round trips against one, the extra two being the fresh object's
    /// GetIDsOfNames. The KEY is the object's vtable pointer: one class shares it, and, like an IID, it is
    /// class-level, so this caches what a type-level cache may and nothing an instance may vary.
    /// A null value means "this type does not have that name", so a capability probe costs one round trip too.
    /// Keys are Ordinal, like the per-object sets: a cache is an index, and a case-insensitive one would
    /// silently accept a wrong-case member in strict mode.</summary>
    private static readonly ConcurrentDictionary<IntPtr, ConcurrentDictionary<string, int?>> TypeDispIds = new();

    /// <summary>Caps for the caches above. Static state lives as long as the PROCESS, and a script probing
    /// generated names could otherwise grow a type's map without bound, so past the cap the caches simply
    /// stop recording. A cache that refuses to grow is always correct: a miss only costs a round trip, which
    /// is why this needs no eviction policy, and why the whole map is dropped (not trimmed) when the type
    /// COUNT runs away: clearing a cache is the one operation that can never be wrong.</summary>
    private const int TypeCacheCap = 256;      // distinct types kept
    private const int TypeNameCap = 4096;      // names kept per type
    private const int ObjectNameCap = 1024;    // names kept per object, in both per-object caches

    // ── instrumentation (Com.Stats()) ─────────────────────────────────────────────────────────────────────
    /// <summary>What this bridge actually ISSUES. Counted here rather than guessed at, because the two
    /// questions that keep coming up, how many round trips a shape costs, and whether a generator re-enters
    /// the collection, are answerable from these four numbers and from nothing else in the script surface.
    /// Increments are Interlocked: ~20 ns each against a ~110 us round trip.</summary>
    internal static int NameLookups;          // GetIDsOfNames calls this process issued
    internal static int NameHits;             // satisfied by the per-object or per-type cache instead
    internal static int ParameterLookups;     // GetTypeInfo + ITypeInfo::GetNames pairs issued for a NAMED argument
    internal static int ParameterHits;        // satisfied by the per-object parameter cache instead
    internal static int SafeArraysCreated;    // SafeArrayCreate calls: every one is built in VariantConverter
    internal static int SafeArraysDestroyed;  // the ONE destroy+count site is DestroyArray, so they cannot drift
    internal static int Enumerations;         // COM collections for which an enumerator was created
    internal static int TypeCacheEntries => TypeDispIds.Count;
    // Names this object does NOT expose. Cached so a typo costs one round-trip rather than one per access,
    // and so a CALLER can ask "is this name mine?" without paying for the same failed lookup twice.
    private readonly HashSet<string> _dispIdMisses = [with(StringComparer.Ordinal)];

    // Exposed for VariantConverter (access to the raw IDispatch pointer)
    internal IntPtr NativePointer => _pDispatch;

#if DEBUG
    private readonly int _creationThreadId = Environment.CurrentManagedThreadId;
#endif

    [Conditional("DEBUG")]
    private void CheckThreadAffinity()
    {
#if DEBUG
        if (Environment.CurrentManagedThreadId != _creationThreadId)
            Debug.WriteLine(
                $"[WARNING] ComObject accessed from thread {Environment.CurrentManagedThreadId}, "
                + $"but created on thread {_creationThreadId}. COM STA objects are thread-affine.");
#endif
    }

    // All variants are heap-allocated via CoTaskMem (stackalloc→pointer conversion
    // is disabled in this C#/runtime version, and COM call overhead dwarfs allocation cost).

    internal ComObject(IntPtr pDispatch, bool addRef = false)
    {
        _pDispatch = pDispatch;
        if (pDispatch != IntPtr.Zero)
        {
            _invoker = new DispatchInvoker(pDispatch);
            if (addRef)
                _invoker.AddRef();
        }
    }

    /// <summary>The DISPID this object exposes for a name, or null when it exposes no such name, the
    /// distinction a script provider needs in order to answer "not mine" instead of claiming every name.
    ///
    /// GetIDsOfNames' HRESULT used to be discarded, so a name the object does not expose stayed at dispId 0
    /// (DISPID_VALUE) and the call silently read the object's DEFAULT member; the failure only surfaced later
    /// as a confusing "Invoke failed", or, for a method call, not at all. A miss is now a miss: the wrapper's
    /// script provider declines it (so the engine's own member-miss report runs and a script extension member
    /// gets its turn), and the wrapper's own accessors report it by name.</summary>
    internal int? FindDispId(string name)
    {
        // Check cache first, saves a COM round-trip (~500µs) per unique property name.
        if (_dispIdCache.TryGetValue(name, out var cached))
        {
            Interlocked.Increment(ref NameHits);
            return cached;
        }
        if (_dispIdMisses.Contains(name))
            return null;

        // Second level: the same name on a DIFFERENT object of the same class. This is what a freshly handed-out
        // object (Range, Cells, Columns, …) needs, since its own cache is empty by construction.
        if (TypeDispIds.Count > TypeCacheCap) TypeDispIds.Clear();   // see the cap note: clearing is never wrong
        var typeCache = TypeDispIds.GetOrAdd(_invoker.VtablePtr,
            static _ => new ConcurrentDictionary<string, int?>(StringComparer.Ordinal));
        if (typeCache.TryGetValue(name, out var typed))
        {
            Interlocked.Increment(ref NameHits);
            if (typed is null) { if (_dispIdMisses.Count < ObjectNameCap) _dispIdMisses.Add(name); return null; }
            if (_dispIdCache.Count < ObjectNameCap) _dispIdCache[name] = typed.Value;   // warm this instance too
            return typed;
        }

        var localNull = ComConst.IidNull;
        var bstrName = Marshal.StringToBSTR(name);
        var dispId = 0;

        void* pGuid = &localNull;
        void* pNamesArray = &bstrName;
        void* pDispIdOut = &dispId;

        Interlocked.Increment(ref NameLookups);
        int hr;
        try { ThrowIfReleased(); hr = _invoker.GetIDsOfNames(pGuid, pNamesArray, 1, 0, pDispIdOut); }
        finally { Marshal.FreeBSTR(bstrName); }

        if (hr >= 0)
        {
            if (_dispIdCache.Count < ObjectNameCap) _dispIdCache[name] = dispId;
            if (typeCache.Count < TypeNameCap) typeCache[name] = dispId;
            return dispId;
        }

        // Only "no such name" is a miss. A disconnected server or a failed allocation is NOT a typo, so it
        // keeps its own error and is never cached as a permanent miss.
        if (hr is ComConst.DISP_E_UNKNOWNNAME or ComConst.DISP_E_MEMBERNOTFOUND)
        {
            if (_dispIdMisses.Count < ObjectNameCap) _dispIdMisses.Add(name);
            if (typeCache.Count < TypeNameCap) typeCache[name] = null;    // the CLASS does not have it, so no instance will
            return null;
        }
        throw new COMException($"Name lookup failed (HRESULT: 0x{hr:X})", hr);
    }

    /// <summary>The DISPID, or the engine's own error naming the member. The wrapper accessors below were
    /// handed one member name by the script, so "no such member" is the right report for them.</summary>
    private int GetDispId(string name)
        => FindDispId(name)
           ?? throw new ScriptException($"'{name}' is not a member of this COM object.");

    // -----------------------------------------------------------------
    // Core invocation with optional named argument (for property put)
    // -----------------------------------------------------------------
    /// <summary>Refuses to touch the server when this handle is OUT OF SERVICE, which means the scope already
    /// released it. For an IN-PROCESS server the interface is then freed memory, so ANY dispatch is the invisible
    /// crash the docs warn about: reproduced 2026-10-06 by calling a named method on a `Com.Run`-created
    /// `Scripting.FileSystemObject` held past its scope, which printed NOTHING and died with
    /// STATUS_STACK_BUFFER_OVERRUN. EVERY place that dereferences `_invoker` asks this first, and the HRESULT is
    /// the one the message map already turns into "the COM object is disconnected: its server is gone".</summary>
    private void ThrowIfReleased()
    {
        if (_pDispatch == IntPtr.Zero)
            throw new COMException("the object was released", unchecked((int)0x800401FD));
    }

    private T InvokeCore<T>(int dispId, ushort flags, ReadOnlySpan<Variant> args, int namedArgDispid = 0,
        ReadOnlySpan<int> namedArgDispids = default)
    {
        ThrowIfReleased();
        var localNull = ComConst.IidNull;

        var pArgsFree = IntPtr.Zero;
        Variant* pArgs = null;
        if (args.Length > 0)
        {
            pArgsFree = Marshal.AllocCoTaskMem(args.Length * Variant.SIZE);
            pArgs = (Variant*)pArgsFree;
            for (var i = 0; i < args.Length; i++)
                pArgs[i] = args[args.Length - 1 - i];
        }

        var dp = new DispParams
        {
            rgvarg = ((IntPtr)pArgs)!,
            cArgs = (uint)args.Length
        };

        // Named arguments as PARAMETER dispids. IDispatch correlates them positionally, the first cNamedArgs
        // entries of rgvarg ARE the named ones, in this same order, which is why InvokeMethodNamedValue orders
        // the argument array to match. The single-dispid form below is the property-put slot (DISPID_PROPERTYPUT).
        const int MaxNamedArgs = 16;
        var pNamedDispid = stackalloc int[MaxNamedArgs];
        var namedCount = 0;
        if (!namedArgDispids.IsEmpty)
        {
            if (namedArgDispids.Length > MaxNamedArgs)
                throw new ScriptException($"too many named arguments ({namedArgDispids.Length}); the limit is {MaxNamedArgs}.");
            for (var i = 0; i < namedArgDispids.Length; i++) pNamedDispid[i] = namedArgDispids[i];
            namedCount = namedArgDispids.Length;
        }
        else if (namedArgDispid != 0 && args.Length > 0)
        {
            pNamedDispid[0] = namedArgDispid;
            namedCount = 1;
        }
        if (namedCount > 0)
        {
            dp.rgdispidNamedArgs = (IntPtr)pNamedDispid;
            dp.cNamedArgs = (uint)namedCount;
        }

        Variant result;
        ExceptionInfo excep;
        uint argErr;
        var hr = _invoker.Invoke(dispId, &localNull, 0, flags, &dp, &result, &excep, &argErr);
        if (hr < 0)
        {
            // ExceptionInfo is only valid when hr == DISP_E_EXCEPTION, the one failure whose text is the COM
            // SERVER's own description, the words an author can act on, so it is the message (empty when the
            // server offers none, which the friendly-message map renders as the sentence alone). Every other
            // HRESULT keeps its code as the message: the map turns that into a sentence, and the raw
            // "COMException: 'Invoke failed (HRESULT: 0x80020003)'" never reaches a script.
            if (hr == ComConst.DISP_E_EXCEPTION)
                throw new COMException(excep.bStrDescription != IntPtr.Zero
                    ? Marshal.PtrToStringBSTR(excep.bStrDescription) ?? string.Empty
                    : string.Empty, hr);
            throw new COMException($"HRESULT 0x{hr:X}", hr);
        }

        try
        {
            return UnwrapVariant<T>(result);
        }
        finally
        {
            VariantClear(new IntPtr(&result));
            if (pArgsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pArgsFree);
        }
    }

    /// <summary>A method call whose arguments include NAMED parameters, `obj.SaveAs(p, AccessMode: 3)`. Each name
    /// is resolved to the dispid the SERVER uses for that parameter, and the argument array is then ordered with
    /// the named ones first, the order IDispatch correlates rgdispidNamedArgs against. A name the server will not
    /// resolve is REFUSED, because the alternative is what this replaces: the value landing on the next parameter
    /// (`AccessMode: 3` becoming FileFormat = xlCSV and writing a CSV named .xlsx, measured).</summary>
    internal object? InvokeMethodNamedValue(string name, object?[] args, IReadOnlyList<string?>? argNames)
    {
        CheckThreadAffinity();
        var memberDispId = GetDispId(name);

        // Resolve everything BEFORE building anything: a refusal must have no side effects.
        var namedAt = new List<int>();
        var namedDispIds = new List<int>();
        for (var i = 0; i < args.Length; i++)
        {
            var parameter = i < (argNames?.Count ?? 0) ? argNames![i] : null;
            if (string.IsNullOrEmpty(parameter)) continue;
            var parameterDispId = FindParameterDispId(parameter!, name);
            if (parameterDispId is null)
                throw new ScriptException(
                    $"'{parameter}' is not a parameter of '{name}' that this COM server publishes. Pass the "
                    + "arguments POSITIONALLY instead, with null for every parameter to skip (VBA's Missing), "
                    + "e.g. wb.SaveAs(path, null, null, null, null, null, 3, 2).");
            namedAt.Add(i);
            namedDispIds.Add(parameterDispId.Value);
        }
        if (namedAt.Count == 0) return InvokeMethodValue(name, args);

        var count = args.Length;
        Variant* variants;
        IntPtr* allocatedBstrs;
        IntPtr variantsFree = IntPtr.Zero;
        IntPtr bstrsFree = IntPtr.Zero;
        if (count <= StackArgCap)
        {
            var stackVariants = stackalloc Variant[StackArgCap];
            var stackBstrs = stackalloc IntPtr[StackArgCap];
            variants = stackVariants;
            allocatedBstrs = stackBstrs;
        }
        else
        {
            variantsFree = Marshal.AllocCoTaskMem(count * Variant.SIZE);
            bstrsFree = Marshal.AllocCoTaskMem(count * sizeof(IntPtr));
            variants = (Variant*)variantsFree;
            allocatedBstrs = (IntPtr*)bstrsFree;
        }

        // The originals are kept aside only so the buffer can be REORDERED. The free walks the same two sets either
        // way, every allocated BSTR, every array-valued variant, so a permutation can neither leak nor double-free.
        var originals = new Variant[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                variants[i] = ConvertToVariant(args[i], out var bstr);
                allocatedBstrs[i] = bstr;
                originals[i] = variants[i];
            }

            var next = 0;
            foreach (var at in namedAt) variants[next++] = originals[at];
            for (var i = 0; i < count; i++)
                if (!namedAt.Contains(i)) variants[next++] = originals[i];

            // Hand it over REVERSED, because InvokeCore writes rgvarg in reverse (the COM convention, which the
            // positional path depends on). Without this the two orderings cancel and the WIRE order becomes
            // positional-first, while IDispatch correlates the FIRST cNamedArgs entries of rgvarg against
            // rgdispidNamedArgs, so the named dispid met the wrong value. Measured on a real Excel: that put the
            // filename BSTR on AccessMode and SaveAs answered "the method failed" with no file written, while the
            // same call handed over in wire order writes a real workbook. (The buffer is a POINTER, so the swap is
            // by hand, Array.Reverse takes a managed array.)
            for (int lo = 0, hi = count - 1; lo < hi; lo++, hi--)
                (variants[lo], variants[hi]) = (variants[hi], variants[lo]);

            return InvokeCore<object>(memberDispId, ComConst.DISPATCH_METHOD,
                new ReadOnlySpan<Variant>(variants, count),
                namedArgDispids: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(namedDispIds));
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (allocatedBstrs[i] != IntPtr.Zero)
                    Marshal.FreeBSTR(allocatedBstrs[i]);
                FreeArgVariant(ref variants[i]);
            }

            if (variantsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(variantsFree);
            if (bstrsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(bstrsFree);
        }
    }

    /// <summary>The dispid IDispatch wants for a NAMED parameter: the parameter's POSITION, which is what a
    /// typelib-driven binder sends and the reason `AccessMode:=` works at all in VBA. The names come from the TYPE
    /// LIBRARY's GetNames(memberDispid), which returns the member and then its parameters IN ORDER, measured on
    /// Excel, where the other documented route (ITypeInfo::GetIDsOfNames) answers E_NOTIMPL. IDispatch's own
    /// GetIDsOfNames is deliberately NOT consulted: it refuses parameter names while answering MEMBER names, so a
    /// parameter called FileFormat or ConflictResolution would come back as that MEMBER's dispid, 288 and 1175
    /// exist on a Workbook, which is exactly the kind of silent wrong call this whole path exists to prevent.
    /// Cached per member, keyed so the two namespaces cannot mix. A name past the buffer is REFUSED rather than
    /// guessed at: measured, Excel DOES have a member with more names than the buffer (Application.Run, 32) and
    /// GetNames CLAMPS its answer to cMaxNames, so the last parameters of such a member cannot be named, a loud
    /// refusal, never a wrong dispid.</summary>
    private int? FindParameterDispId(string parameterName, string memberName)
    {
        var key = memberName + "\u0000" + parameterName;
        if (_paramDispIds is not null && _paramDispIds.TryGetValue(key, out var cached))
        {
            Interlocked.Increment(ref ParameterHits);
            return cached;
        }

        var memberDispId = GetDispId(memberName);      // the MEMBER, which IDispatch does answer
        Interlocked.Increment(ref ParameterLookups);   // the round trips below, once per member+name per object
        IntPtr typeInfo;
        ThrowIfReleased();
        var hrType = _invoker.GetTypeInfo(0, 0, out typeInfo);
        if (hrType < 0 || typeInfo == IntPtr.Zero) return null;
        var vtbl = *(TypeInfoVtable**)typeInfo;

        const int MaxNames = 24;                       // the member plus its parameters
        var got = 0;
        var pBstrs = stackalloc IntPtr[MaxNames];
        for (var i = 0; i < MaxNames; i++) pBstrs[i] = IntPtr.Zero;
        try
        {
            uint count = 0;
            var hr = vtbl->GetNames(typeInfo, memberDispId, (IntPtr)pBstrs, MaxNames, &count);
            if (hr < 0) return null;
            got = (int)Math.Min(count, MaxNames);      // never free past the buffer, whatever the server claims
            for (var i = 1; i < got; i++)               // [0] is the member itself
            {
                var name = Marshal.PtrToStringBSTR(pBstrs[i]);
                if (name is not null && name.Equals(parameterName, StringComparison.Ordinal))
                {
                    var position = i - 1;               // the parameter's POSITION is the dispid to send
                    _paramDispIds ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    if (_paramDispIds.Count < ObjectNameCap) _paramDispIds[key] = position;
                    return position;
                }
            }
            return null;
        }
        finally
        {
            for (var i = 0; i < got; i++)
                if (pBstrs[i] != IntPtr.Zero) Marshal.FreeBSTR(pBstrs[i]);
            vtbl->Release(typeInfo);
        }
    }

    private Dictionary<string, int>? _paramDispIds;

    /// <summary>The value of a property, decoded from the variant the object actually returned. Asking for
    /// &lt;ComObject&gt; instead, as the script provider used to, throws for every SCALAR, so a string, a
    /// number or a bool read back as a failure rather than as the value.</summary>
    internal object? GetPropertyValue(string name)
        => GetProperty<object>(name);

    /// <summary>The result of a method call, decoded the same way (see GetPropertyValue): the VARIANT the
    /// object returned decides, so a scalar result costs the caller nothing.</summary>
    internal object? InvokeMethodValue(string name, params object?[]? args)
        => InvokeMethodResult<object>(name, args);

    // -----------------------------------------------------------------
    // Simple property setter (unified)
    // -----------------------------------------------------------------
    public void SetProperty(string name, object value)
    {
        CheckThreadAffinity();
        var variant = ConvertToVariant(value, out var bstr);

        try
        {
            SetPropertyRaw(name, variant);
        }
        finally
        {
            if (bstr != IntPtr.Zero)
                Marshal.FreeBSTR(bstr);
            // An array-valued put is NOT destroyed here, and this IS the ~14 MB a 200,000-cell put used to leak
            // per call (counted: three puts created 3, destroyed 0). The array is ours, an out-of-process server
            // cannot free our heap, but the server may still read it after this returns: destroying it at this
            // point SEGFAULTS a few iterations later, which is how that was learned. So it is handed to the scope,
            // which destroys it in Dispose once no scope is left, i.e. after every interface has been released.
            if (variant.vt == (ComConst.VT_ARRAY | ComConst.VT_VARIANT) && variant.llVal != IntPtr.Zero)
            {
                ComScope.DeferArrayFree(variant.llVal);
                variant = default;
            }
        }
    }

    private void SetPropertyRaw(string name, Variant value)
    {
        var dispId = GetDispId(name);

        // Single argument for put, with DISPID_PROPERTYPUT = -3
        var args = new ReadOnlySpan<Variant>(&value, 1);
        _ = InvokeCore<object>(dispId, ComConst.DISPATCH_PROPERTYPUT, args, namedArgDispid: -3);
    }

    // -----------------------------------------------------------------
    // Indexed property setter (e.g. Range["A1"] = something)
    // -----------------------------------------------------------------
    public void SetPropertyIndexed(string name, object value, params object[]? indices)
    {
        CheckThreadAffinity();
        indices ??= [];

        // Build arguments: first all indices, then the value
        var totalArgs = indices.Length + 1;
        var variantsFree = Marshal.AllocCoTaskMem(totalArgs * Variant.SIZE);
        var bstrsFree = Marshal.AllocCoTaskMem(totalArgs * sizeof(IntPtr));
        var variants = (Variant*)variantsFree;
        var allocatedBstrs = (IntPtr*)bstrsFree;

        for (var i = 0; i < totalArgs; i++)
            allocatedBstrs[i] = IntPtr.Zero;

        try
        {
            // Fill indices (position 0…indices.Length-1)
            for (var i = 0; i < indices.Length; i++)
            {
                variants[i] = ConvertToVariant(indices[i], out var bstr);
                allocatedBstrs[i] = bstr;
            }

            // Fill value at last position
            variants[indices.Length] = ConvertToVariant(value, out var valueBstr);
            allocatedBstrs[indices.Length] = valueBstr;

            var dispId = GetDispId(name);
            var args = new ReadOnlySpan<Variant>(variants, totalArgs);
            _ = InvokeCore<object>(dispId, ComConst.DISPATCH_PROPERTYPUT, args, namedArgDispid: -3);
        }
        finally
        {
            for (var i = 0; i < totalArgs; i++)
            {
                if (allocatedBstrs[i] != IntPtr.Zero)
                    Marshal.FreeBSTR(allocatedBstrs[i]);
                FreeArgVariant(ref variants[i]);
            }

            if (variantsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(variantsFree);
            if (bstrsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(bstrsFree);
        }
    }

    // -----------------------------------------------------------------
    // Indexed property getter (generic)
    // -----------------------------------------------------------------
    /// <summary>Note the ELEMENT nullability: an index is allowed to be null, and it means the COM "missing"
    /// argument (see MissingVariant), which is how a script skips an optional index. The array itself may also be
    /// null. Annotating the elements as non-null was simply wrong, and made every caller that passes an
    /// <c>object?[]</c>, ComNode.Get, for one, report CS8620.</summary>
    public T GetPropertyIndexed<T>(string name, params object?[]? indices)
    {
        CheckThreadAffinity();
        indices ??= [];

        var count = indices.Length;
        Variant* variants;
        IntPtr* allocatedBstrs;
        IntPtr variantsFree = IntPtr.Zero;
        IntPtr bstrsFree = IntPtr.Zero;
        if (count <= StackArgCap)
        {
            Variant* stackVariants = stackalloc Variant[count];
            IntPtr* stackBstrs = stackalloc IntPtr[count];
            variants = stackVariants;
            allocatedBstrs = stackBstrs;
        }
        else
        {
            variantsFree = Marshal.AllocCoTaskMem(count * Variant.SIZE);
            bstrsFree = Marshal.AllocCoTaskMem(count * sizeof(IntPtr));
            variants = (Variant*)variantsFree;
            allocatedBstrs = (IntPtr*)bstrsFree;
        }

        for (var i = 0; i < count; i++)
            allocatedBstrs[i] = IntPtr.Zero;

        try
        {
            for (var i = 0; i < count; i++)
            {
                variants[i] = ConvertToVariant(indices[i], out var bstr);
                allocatedBstrs[i] = bstr;
            }

            var dispId = GetDispId(name);
            var args = new ReadOnlySpan<Variant>(variants, count);

            return InvokeCore<T>(dispId, ComConst.DISPATCH_PROPERTYGET | ComConst.DISPATCH_METHOD, args);
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (allocatedBstrs[i] != IntPtr.Zero)
                    Marshal.FreeBSTR(allocatedBstrs[i]);
                FreeArgVariant(ref variants[i]);
            }

            if (variantsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(variantsFree);
            if (bstrsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(bstrsFree);
        }
    }

    // Non‑generic overload for convenience
    public ComObject GetPropertyIndexed(string name, params object[] indices)
        => GetPropertyIndexed<ComObject>(name, indices);

    // ── ComCollectionEnumerator, helper that encapsulates the unsafe
    //    IDispatch::Invoke + IEnumVARIANT setup so the public iterator
    //    (which uses yield return) can be free of &-on-locals.
    // ───────────────────────────────────────────────────────────────────
    private sealed class ComCollectionEnumerator : IDisposable
    {
        /// <summary>Items fetched per IEnumVARIANT::Next call. One item per call cost a cross-apartment
        /// round trip PER ELEMENT, measured, that was ~1,650 of the ~1,700 ticks an enumerated item spends,
        /// with the script side at ~35. Batching trades a BOUNDED read-ahead (at most BatchSize-1 items are
        /// fetched and not yet consumed, and Dispose clears them) for one call per batch. The lazy contract
        /// the class documented still holds: a caller that breaks early reads at most one batch ahead.</summary>
        private const int BatchSize = 16;

        private IntPtr VtblPtr { get; init; }
        private IntPtr EnumPtr { get; set; }
        private IntPtr Buffer { get; set; }
        private int _have;   // items in the buffer
        private int _next;   // index of the next unconsumed one

        public static ComCollectionEnumerator Create(IntPtr vTablePtr, IntPtr pDispatch)
        {
            Interlocked.Increment(ref Enumerations);
            var localNull = ComConst.IidNull;
            var bstrName = Marshal.StringToBSTR("_NewEnum");
            var dispId = 0;
            void* pGuid = &localNull;
            void* pNamesArray = &bstrName;
            void* pDispIdOut = &dispId;

            try { _ = ((DispatchVtable*)vTablePtr)->GetIDsOfNames(pDispatch, pGuid, pNamesArray, 1, 0, pDispIdOut); }
            finally { Marshal.FreeBSTR(bstrName); }


            var dp = new DispParams { rgvarg = IntPtr.Zero, cArgs = 0 };
            Variant result = default;
            ExceptionInfo excep = default;
            uint argErr = 0;

            var hr = ((DispatchVtable*)vTablePtr)->Invoke(pDispatch, dispId, &localNull, 0,
                ComConst.DISPATCH_PROPERTYGET | ComConst.DISPATCH_METHOD,
                &dp, &result, &excep, &argErr);

            if (hr < 0)
                throw new COMException("Failed to get _NewEnum property", hr);

            if (result.vt != ComConst.VT_UNKNOWN && result.vt != ComConst.VT_DISPATCH)
            {
                VariantClear(new IntPtr(&result));
                throw new COMException($"_NewEnum returned unexpected variant type: {result.vt}");
            }

            var pEnumUnknown = result.llVal;
            if (pEnumUnknown == IntPtr.Zero)
            {
                VariantClear(new IntPtr(&result));
                throw new InvalidOperationException("_NewEnum returned null");
            }

            var iidEnumVariant = ComConst.IidEnumVariant;
            var pEnumVariant = IntPtr.Zero;
            var unk = (UnknownVtable*)*(IntPtr*)pEnumUnknown;

            hr = unk->QueryInterface(pEnumUnknown, &iidEnumVariant, &pEnumVariant);
            VariantClear(new IntPtr(&result));

            if (hr < 0 || pEnumVariant == IntPtr.Zero)
                throw new COMException("Failed to obtain IEnumVARIANT", hr);

            var vt = (EnumVariantVtable*)*(IntPtr*)pEnumVariant;
            var buffer = Marshal.AllocCoTaskMem(BatchSize * Variant.SIZE);

            return new ComCollectionEnumerator
            {
                VtblPtr = (IntPtr)vt,
                EnumPtr = pEnumVariant,
                Buffer = buffer
            };
        }

        public bool TryGetNext(out ComObject? item)
        {
            while (true)
            {
                if (_next < _have)
                {
                    var slot = Buffer + _next * Variant.SIZE;
                    var pVar = (Variant*)slot;
                    var dispatched = pVar->vt == ComConst.VT_DISPATCH && pVar->llVal != IntPtr.Zero;
                    item = dispatched ? new ComObject(pVar->llVal, addRef: true) : null;
                    VariantClear(slot);          // release each item as it is consumed, as before
                    _next++;
                    if (dispatched) return true;
                    continue;                    // a non-dispatch item is skipped, not surfaced
                }

                _have = 0;
                _next = 0;
                uint fetched = 0;
                // No `fixed` here: the buffer is unmanaged, and a LOCAL needs no pinning (the old code fixed a
                // field, which is why it could). Taking a local's address is fine in this unsafe class.
                var hr = ((EnumVariantVtable*)VtblPtr)->Next(EnumPtr, BatchSize, Buffer, &fetched);
                _have = (int)fetched;

                // The COM contract: S_FALSE with items means a SHORT last batch, not the end; only zero items
                // ends the enumeration. A failing HRESULT is the enumerator's error, and swallowing it, which
                // `hr != 0` did for celt=1, where it could only mean "end", would hide a real failure now.
                if (hr < 0) throw new COMException("IEnumVARIANT::Next failed", hr);
                if (fetched == 0) { item = null; return false; }
            }
        }

        public void Dispose()
        {
            if (Buffer != IntPtr.Zero)
            {
                // A batch can be left half consumed: each buffered VARIANT owns a BSTR or an interface.
                for (var i = _next; i < _have; i++)
                    VariantClear(Buffer + i * Variant.SIZE);
                Marshal.FreeCoTaskMem(Buffer);
                Buffer = IntPtr.Zero;
            }
            if (EnumPtr != IntPtr.Zero)
            {
                _ = ((EnumVariantVtable*)VtblPtr)->Release(EnumPtr);
                EnumPtr = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Returns a lazy IEnumerable that wraps the COM IEnumVARIANT interface.
    /// Each MoveNext() calls IEnumVARIANT::Next(), the caller controls
    /// enumeration pace and can break early without reading all items.
    /// </summary>
    internal IEnumerable<ComObject> EnumerateCollection()
    {
        CheckThreadAffinity();
        // Cast the pointer field to IntPtr in a non-iterator context,
        // then delegate to the yield-based private helper.
        return EnumerateCollectionImpl(_invoker.VtablePtr, _pDispatch);
    }

    private static IEnumerable<ComObject> EnumerateCollectionImpl(IntPtr vp, IntPtr dp)
    {
        using var enumerator = ComCollectionEnumerator.Create(vp, dp);
        while (enumerator.TryGetNext(out var item))
        {
            if (item != null)
                yield return item;
        }
    }

    // -----------------------------------------------------------------
    // Collection property iterator (callback-based, delegates to EnumerateCollection)
    // -----------------------------------------------------------------
    public void ForEach(Action<ComObject> action)
    {
        // Items from EnumerateCollection() are AddRef'd. The caller (typically
        // ComNode.ForEach → _scope.Track) takes ownership. We must NOT dispose
        // items here, that would null _pDispatch while the ComNode still wraps it.
        foreach (var item in EnumerateCollection())
            action(item);
    }

    // -----------------------------------------------------------------
    // Simple property getter
    // -----------------------------------------------------------------
    public T GetProperty<T>(string name)
    {
        CheckThreadAffinity();
        return InvokeCore<T>(GetDispId(name), ComConst.DISPATCH_PROPERTYGET | ComConst.DISPATCH_METHOD, []);
    }

    // -----------------------------------------------------------------
    // Method invocation (void return)
    // -----------------------------------------------------------------
    /// <summary>
    /// Invokes a method and discards its return value (void-call semantics).
    /// Unlike InvokeCore{T}, this never tries to convert the result variant,
    /// so it works for any return type, COM object, scalar, or VT_EMPTY.
    /// </summary>
    private void InvokeCoreDiscard(int dispId, ushort flags, ReadOnlySpan<Variant> args, int namedArgDispid = 0)
    {
        var localNull = ComConst.IidNull;

        Variant* pArgs = null;
        var pArgsFree = IntPtr.Zero;
        if (args.Length > 0)
        {
            pArgsFree = Marshal.AllocCoTaskMem(args.Length * Variant.SIZE);
            pArgs = (Variant*)pArgsFree;
            for (var i = 0; i < args.Length; i++)
                pArgs[i] = args[args.Length - 1 - i];
        }

        var dp = new DispParams
        {
            rgvarg = ((IntPtr)pArgs)!,
            cArgs = (uint)args.Length
        };

        var pNamedDispid = stackalloc int[1];
        if (namedArgDispid != 0 && args.Length > 0)
        {
            pNamedDispid[0] = namedArgDispid;
            dp.rgdispidNamedArgs = (IntPtr)pNamedDispid;
            dp.cNamedArgs = 1;
        }

        Variant vResult; ExceptionInfo vExcep; uint vArgErr;
        ThrowIfReleased();
        var hr = _invoker.Invoke(dispId, &localNull, 0, flags, &dp, &vResult, &vExcep, &vArgErr);
        if (hr < 0)
        {
            var msg = $"Invoke failed (HRESULT: 0x{hr:X})";
            if (hr == unchecked((int)0x80020009) && vExcep.bStrDescription != IntPtr.Zero)
                msg += $": {Marshal.PtrToStringBSTR(vExcep.bStrDescription)}";
            throw new COMException(msg, hr);
        }

        // Discard the result, no type conversion attempted
        VariantClear(new IntPtr(&vResult));

        if (pArgsFree != IntPtr.Zero)
            Marshal.FreeCoTaskMem(pArgsFree);
    }

    public void InvokeMethod(string name, params object[]? args)
    {
        CheckThreadAffinity();
        args ??= [];

        var count = args.Length;
        Variant* variants;
        IntPtr* allocatedBstrs;
        IntPtr variantsFree = IntPtr.Zero;
        IntPtr bstrsFree = IntPtr.Zero;
        if (count <= StackArgCap)
        {
            Variant* stackVariants = stackalloc Variant[count];
            IntPtr* stackBstrs = stackalloc IntPtr[count];
            variants = stackVariants;
            allocatedBstrs = stackBstrs;
        }
        else
        {
            variantsFree = Marshal.AllocCoTaskMem(count * Variant.SIZE);
            bstrsFree = Marshal.AllocCoTaskMem(count * sizeof(IntPtr));
            variants = (Variant*)variantsFree;
            allocatedBstrs = (IntPtr*)bstrsFree;
        }

        for (var i = 0; i < count; i++)
            allocatedBstrs[i] = IntPtr.Zero;

        try
        {
            for (var i = 0; i < count; i++)
            {
                variants[i] = ConvertArgToVariant(args[i], out var bstr);   // null = an omitted optional
                allocatedBstrs[i] = bstr;
            }

            var dispId = GetDispId(name);
            InvokeCoreDiscard(dispId, ComConst.DISPATCH_METHOD, new ReadOnlySpan<Variant>(variants, count));
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (allocatedBstrs[i] != IntPtr.Zero)
                    Marshal.FreeBSTR(allocatedBstrs[i]);
                FreeArgVariant(ref variants[i]);
            }

            if (variantsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(variantsFree);
            if (bstrsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(bstrsFree);
        }
    }

    // Legacy convenience
    public ComObject InvokeMethodResult(string name, params object[] args)
    {
        CheckThreadAffinity();
        return InvokeMethodResult<ComObject>(name, args);
    }

    // -----------------------------------------------------------------
    // Method invocation with return value (generic)
    // -----------------------------------------------------------------
    internal T InvokeMethodResult<T>(string name, params object?[]? args)
    {
        args ??= [];

        var count = args.Length;
        Variant* variants;
        IntPtr* allocatedBstrs;
        IntPtr variantsFree = IntPtr.Zero;
        IntPtr bstrsFree = IntPtr.Zero;
        if (count <= StackArgCap)
        {
            Variant* stackVariants = stackalloc Variant[count];
            IntPtr* stackBstrs = stackalloc IntPtr[count];
            variants = stackVariants;
            allocatedBstrs = stackBstrs;
        }
        else
        {
            variantsFree = Marshal.AllocCoTaskMem(count * Variant.SIZE);
            bstrsFree = Marshal.AllocCoTaskMem(count * sizeof(IntPtr));
            variants = (Variant*)variantsFree;
            allocatedBstrs = (IntPtr*)bstrsFree;
        }

        for (var i = 0; i < count; i++)
            allocatedBstrs[i] = IntPtr.Zero;

        try
        {
            for (var i = 0; i < count; i++)
            {
                variants[i] = ConvertArgToVariant(args[i], out var bstr);   // null = an omitted optional
                allocatedBstrs[i] = bstr;
            }

            var dispId = GetDispId(name);
            return InvokeCore<T>(dispId, ComConst.DISPATCH_METHOD | ComConst.DISPATCH_PROPERTYGET,
                new ReadOnlySpan<Variant>(variants, count));
        }
        finally
        {
            for (var i = 0; i < count; i++)
            {
                if (allocatedBstrs[i] != IntPtr.Zero)
                    Marshal.FreeBSTR(allocatedBstrs[i]);
                FreeArgVariant(ref variants[i]);
            }

            if (variantsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(variantsFree);
            if (bstrsFree != IntPtr.Zero)
                Marshal.FreeCoTaskMem(bstrsFree);
        }
    }

    // -----------------------------------------------------------------
    // Bulk range setter via SAFEARRAY
    // -----------------------------------------------------------------
    public void SetRangeValues(IReadOnlyList<IReadOnlyList<object?>>? jaggedData)
    {
        CheckThreadAffinity();
        if (jaggedData == null || jaggedData.Count == 0)
            return;

        // The SAFEARRAY build lives in VariantConverter now: a script assigning back a range it READ needs the
        // same conversion, and two copies of it is how the twins drift.
        var arrayVariant = VariantConverter.JaggedToSafeArrayVariant(jaggedData);
        if (arrayVariant.vt == ComConst.VT_EMPTY)
            return;
        try { SetPropertyRaw("Value", arrayVariant); }
        // Free it through the ONE helper rather than by hand. FreeArgVariant does not merely destroy the array:
        // it then sets the whole Variant to default, so vt reads VT_EMPTY and every alias of the payload,
        // llVal among them, reads IntPtr.Zero, which is the test the guard above it uses. A bare
        // SafeArrayDestroy left arrayVariant holding a pointer to freed memory: harmless only because this
        // local dies on the next statement, and exactly the "released but not cleared" shape that becomes a
        // double free the moment the code grows a second use. (The helper's check is vt-typed and
        // JaggedToSafeArrayVariant builds exactly VT_ARRAY|VT_VARIANT, so it destroys what the old line did
        // and nothing else.)
        finally { FreeArgVariant(ref arrayVariant); }
    }

    // ── SafeArrayReader, encapsulates unsafe SAFEARRAY setup for iterator safety
    private sealed class SafeArrayReader : IDisposable
    {
        private IntPtr Psa { get; set; }
        private int Lb1 { get; }
        private int Ub1 { get; }
        private int Lb2 { get; }
        private int Ub2 { get; }
        public int Rows => Ub1 - Lb1 + 1;
        private int Cols => Ub2 - Lb2 + 1;
        private IntPtr ElementBuf { get; set; }

        private SafeArrayReader(IntPtr psa, int lb1, int ub1, int lb2, int ub2)
        {
            Psa = psa;
            Lb1 = lb1; Ub1 = ub1; Lb2 = lb2; Ub2 = ub2;
            ElementBuf = Marshal.AllocCoTaskMem(Variant.SIZE);
        }

        public static SafeArrayReader? Create(IntPtr vTablePtr, IntPtr pDispatch)
        {
            var localNull = ComConst.IidNull;
            int dispId;
            {
                var bstrName = Marshal.StringToBSTR("Value2");
                dispId = 0;
                void* pGuid = &localNull;
                void* pNamesArray = &bstrName;
                void* pDispIdOut = &dispId;
                try { _ = ((DispatchVtable*)vTablePtr)->GetIDsOfNames(pDispatch, pGuid, pNamesArray, 1, 0, pDispIdOut); }
                finally { Marshal.FreeBSTR(bstrName); }
            }

            var dp = new DispParams { rgvarg = IntPtr.Zero, cArgs = 0 };
            Variant result = default;
            ExceptionInfo excep = default;
            uint argErr = 0;

            var hr = ((DispatchVtable*)vTablePtr)->Invoke(pDispatch, dispId, &localNull, 0,
                ComConst.DISPATCH_PROPERTYGET | ComConst.DISPATCH_METHOD,
                &dp, &result, &excep, &argErr);

            if (hr < 0)
                throw new COMException($"GetRangeValues, reading Value2 failed (HRESULT: 0x{hr:X}).", hr);

            if ((result.vt & ComConst.VT_ARRAY) == 0)
            {
                VariantClear(new IntPtr(&result));
                return null;
            }

            var psa = result.llVal;
            if (psa == IntPtr.Zero)
            {
                VariantClear(new IntPtr(&result));
                return null;
            }

            hr = SafeArrayGetLBound(psa, 1, out var lb1);
            if (hr < 0) { VariantClear(new IntPtr(&result)); return null; }
            hr = SafeArrayGetUBound(psa, 1, out var ub1);
            if (hr < 0) { VariantClear(new IntPtr(&result)); return null; }
            hr = SafeArrayGetLBound(psa, 2, out var lb2);
            if (hr < 0) { VariantClear(new IntPtr(&result)); return null; }
            hr = SafeArrayGetUBound(psa, 2, out var ub2);
            if (hr < 0) { VariantClear(new IntPtr(&result)); return null; }

            // Clear the variant header WITHOUT destroying the SAFEARRAY.
            // VariantClear on VT_ARRAY would call SafeArrayDestroy on psa,
            // but we want the array to stay alive (SafeArrayReader owns it now).
            result.vt = 0;
            VariantClear(new IntPtr(&result));
            return new SafeArrayReader(psa, lb1, ub1, lb2, ub2);
        }

        public object?[] ReadRow(int rowIndex)
        {
            var cols = Cols;
            var row = new object?[cols];
            var indices = stackalloc int[2];
            indices[0] = Lb1 + rowIndex;

            for (var c = 0; c < cols; c++)
            {
                indices[1] = Lb2 + c;
                ClearBuffer(ElementBuf, Variant.SIZE);

                var hr = SafeArrayGetElement(Psa, (IntPtr)indices, ElementBuf);
                if (hr == 0)
                {
                    var cellVar = *(Variant*)ElementBuf;
                    try { row[c] = UnwrapCellVariant(cellVar); }
                    finally { VariantClear(ElementBuf); }
                }
            }


            // Helper: zero-fill a block of memory
            static void ClearBuffer(IntPtr ptr, int size)
            {
                for (var i = 0; i < size; i++)
                    Marshal.WriteByte(ptr, i, 0);
            }

            return row;
        }

        public void Dispose()
        {
            if (ElementBuf != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ElementBuf);
                ElementBuf = IntPtr.Zero;
            }
            if (Psa != IntPtr.Zero)
            {
                _ = SafeArrayDestroy(Psa);
                Interlocked.Increment(ref SafeArraysDestroyed);
                Psa = IntPtr.Zero;
            }
        }
    }

    // -----------------------------------------------------------------
    // Bulk range reader, lazy row-by-row
    // Reads Range.Value2 and yields rows one at a time from the SAFEARRAY.
    // Caller controls enumeration, break early without reading the whole range.
    // -----------------------------------------------------------------
    public IEnumerable<IEnumerable<object?>>? GetRangeValues()
    {
        CheckThreadAffinity();
        return GetRangeValuesImpl(_invoker.VtablePtr, _pDispatch);
    }

    private static IEnumerable<IEnumerable<object?>>? GetRangeValuesImpl(IntPtr vp, IntPtr dp)
    {
        using var reader = SafeArrayReader.Create(vp, dp);
        if (reader == null)
            yield break;

        var rows = reader.Rows;
        for (var r = 0; r < rows; r++)
            yield return reader.ReadRow(r);
    }

    // Variant conversion methods (ConvertToVariant, UnwrapVariant) now live in
    // VariantConverter.cs, imported via using static DispatchVtbl.VariantConverter.

    // ── Dispose / Finalizer ─────────────────────────────────────────────
    //
    // IMPORTANT: CoUninitialize is NOT called here. COM apartment
    // shutdown must happen on the SAME thread that called CoInitializeEx.
    // Finalizers run on a separate GC thread, calling CoUninitialize
    // there would corrupt the COM apartment state.
    //
    // Callers should explicitly invoke ComInit.Shutdown() on the STA
    // thread after all COM objects are disposed if they need to tear
    // down COM before the thread exits.
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Is the object behind this handle still USABLE? There are two ways to be dead, and both are cheap to
    /// test because neither touches the server's own model:
    ///   * the handle is OUT OF SERVICE, this scope already released it (see Dispose below), so any dispatch now
    ///     goes to freed memory for an IN-PROCESS server. Checking the pointer FIRST is the whole point: that
    ///     dereference is the silent crash the docs warn about, and answering without it is what makes this callable
    ///     on a handle held across a run;
    ///   * the mandatory IDispatch call fails, a marshalled proxy whose server quit answers an error, which the
    ///     bridge already maps to "the COM object is disconnected: its server is gone".
    /// So this answers "can I still use it?", not "is the application running?" (that is Com.IsAlive([progId])).
    /// Any failing call counts as a no on purpose: the question the caller is about to ask is whether to use it.</summary>
    internal bool IsUsable()
    {
        if (_pDispatch == IntPtr.Zero)
            return false;

        try { return _invoker.GetTypeInfoCount(out _) >= 0; }
        catch { return false; }
    }

    /// <summary>Releases the IDispatch reference. Does NOT call CoUninitialize.</summary>
    public void Dispose()
    {
        CheckThreadAffinity();

        if (_pDispatch != IntPtr.Zero)
        {
            _invoker.Release();
            _pDispatch = IntPtr.Zero;
        }

        GC.SuppressFinalize(this);
    }
}
