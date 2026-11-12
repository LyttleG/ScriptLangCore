namespace DispatchVtbl;

/// <summary>
/// Converts between .NET objects and COM VARIANT structures.
/// All methods are AOT-safe, no reflection, no dynamic codegen.
/// </summary>
internal static unsafe class VariantConverter
{
    // -----------------------------------------------------------------
    // Convert .NET object to VARIANT
    // -----------------------------------------------------------------
    /// <summary>The `Missing` variant: VT_ERROR carrying DISP_E_PARAMNOTFOUND. This is how an OPTIONAL COM
    /// parameter is skipped, `Worksheets.Add(Before, After, …)` written as `Add(null, anchor)`. A script could
    /// not express it before: `null` became VT_EMPTY, which Excel rejects. Measured, `Sheets.Add(null, anchor)`
    /// failed with "Impossible de lire la propriété Add de la classe Sheets" while `Add(anchor)`, the FIRST
    /// optional, worked. Method ARGUMENTS go through ConvertArgToVariant; a PROPERTY value does not, because
    /// there `null` genuinely means empty.</summary>
    internal static Variant MissingVariant() =>
        new Variant { vt = ComConst.VT_ERROR, iVal = unchecked((int)0x80020004) };

    /// <summary>An argument in a COM METHOD call: `null` means "not supplied", not "empty".</summary>
    internal static Variant ConvertArgToVariant(object? val, out IntPtr bStr)
    {
        if (val == null) { bStr = IntPtr.Zero; return MissingVariant(); }
        return ConvertToVariant(val, out bStr);
    }
    /// <summary>The CLR-jagged shape, a script's `[[1],[2],[3]]`, over the same cell write. It READS rows
    /// (GetValue) instead of rebuilding one object[] per row, which is what it used to do.</summary>
    internal static Variant ArrayToSafeArrayVariant(Array jaggedFlat)
    {
        var rows = jaggedFlat.Length;
        var cols = 1;
        for (var i = 0; i < rows; i++)
        {
            var inner = (Array)jaggedFlat.GetValue(i)!;
            if (inner.Length > cols) cols = inner.Length;
        }
        if (rows == 0 || cols == 0) return new Variant { vt = ComConst.VT_EMPTY };

        var bounds = stackalloc SafeArrayBound[2];
        bounds[0] = new SafeArrayBound { cElements = (uint)rows, lLbound = 1 };
        bounds[1] = new SafeArrayBound { cElements = (uint)cols, lLbound = 1 };
        var psa = NativeMethods.SafeArrayCreate(ComConst.VT_VARIANT, 2, in bounds[0]);
        if (psa == IntPtr.Zero) throw new COMException("SafeArrayCreate failed.");
        Interlocked.Increment(ref ComObject.SafeArraysCreated);

        var indices = stackalloc int[2];
        for (var r = 0; r < rows; r++)
        {
            var inner = (Array)jaggedFlat.GetValue(r)!;
            for (var c = 0; c < inner.Length; c++)
                PutCell(psa, r + 1, c + 1, inner.GetValue(c), indices);
        }
        return new Variant { vt = ComConst.VT_ARRAY | ComConst.VT_VARIANT, llVal = psa };
    }

    /// <summary>A 1-D sibling of JaggedToSafeArrayVariant (see the note at its call site for why the dimension
    /// matters to Excel).</summary>
    internal static Variant FlatToSafeArrayVariant(Array flat)
    {
        var n = flat.Length;
        if (n == 0) return new Variant { vt = ComConst.VT_EMPTY };

        var bounds = stackalloc SafeArrayBound[1];
        bounds[0] = new SafeArrayBound { cElements = (uint)n, lLbound = 1 };
        var psa = NativeMethods.SafeArrayCreate(ComConst.VT_VARIANT, 1, in bounds[0]);
        if (psa == IntPtr.Zero) throw new COMException("SafeArrayCreate failed.");
        Interlocked.Increment(ref ComObject.SafeArraysCreated);

        var index = stackalloc int[1];
        for (var i = 0; i < n; i++)
        {
            var vCell = ConvertToVariant(flat.GetValue(i), out var bstrAlloc);
            index[0] = i + 1;
            var hr = NativeMethods.SafeArrayPutElement(psa, (IntPtr)index, (IntPtr)(&vCell));
            if (bstrAlloc != IntPtr.Zero) Marshal.FreeBSTR(bstrAlloc);
            if (hr < 0) throw new COMException("SafeArrayPutElement failed.", hr);
        }
        return new Variant { vt = ComConst.VT_ARRAY | ComConst.VT_VARIANT, llVal = psa };
    }
    /// <summary>Write ONE cell into a SAFEARRAY: the only place a cell is converted and put, so every shape
    /// shares it. A POINTER, not a delegate: an accessor callback would allocate a closure per write and dispatch
    /// indirectly for every cell, exactly the GC pressure and the extra failure mode this bridge avoids.</summary>
    private static void PutCell(IntPtr psa, int row, int col, object? value, int* indices)
    {
        var vCell = ConvertToVariant(value, out var bstrAlloc);
        indices[0] = row;
        indices[1] = col;
        var hr = NativeMethods.SafeArrayPutElement(psa, (IntPtr)indices, (IntPtr)(&vCell));
        if (bstrAlloc != IntPtr.Zero) Marshal.FreeBSTR(bstrAlloc);
        if (hr < 0) throw new COMException("SafeArrayPutElement failed.", hr);
    }
    /// <summary>Build a 2-D VT_ARRAY|VT_VARIANT from jagged data. ONE implementation: SetRangeValues and the
    /// generic value converter both come through here. Measured need, a script that READ a multi-cell
    /// Range.Value (which arrives as an array-backed VARIANT, unwrapped to object[][]) could not assign it
    /// back: "operation not supported: Type Object[][] not supported for VARIANT conversion", which is the
    /// freeze-values trick from the Excel tutorial and a common shape in real scripts.</summary>
    internal static Variant JaggedToSafeArrayVariant(IReadOnlyList<IReadOnlyList<object?>> jagged)
    {
        var rows = jagged.Count;
        var cols = 0;
        for (var i = 0; i < rows; i++)
            if (jagged[i].Count > cols) cols = jagged[i].Count;
        if (rows == 0 || cols == 0) return new Variant { vt = ComConst.VT_EMPTY };

        var bounds = stackalloc SafeArrayBound[2];
        bounds[0] = new SafeArrayBound { cElements = (uint)rows, lLbound = 1 };
        bounds[1] = new SafeArrayBound { cElements = (uint)cols, lLbound = 1 };
        var psa = NativeMethods.SafeArrayCreate(ComConst.VT_VARIANT, 2, in bounds[0]);
        if (psa == IntPtr.Zero) throw new COMException("SafeArrayCreate failed.");
        Interlocked.Increment(ref ComObject.SafeArraysCreated);

        var indices = stackalloc int[2];
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < jagged[r].Count; c++)
                PutCell(psa, r + 1, c + 1, jagged[r][c], indices);
        return new Variant { vt = ComConst.VT_ARRAY | ComConst.VT_VARIANT, llVal = psa };
    }
    internal static Variant ConvertToVariant(object? val, out IntPtr bStr)
    {
        bStr = IntPtr.Zero;

        // A 2-D array goes back as a SAFEARRAY, the write half of reading a multi-cell Range.Value.
        if (val is object?[][] jagged)
            return JaggedToSafeArrayVariant(jagged);

        // A 1-D sequence goes back as a 1-D SAFEARRAY, NOT as a 1xN one: Excel maps a 1-D array to a range's
        // own shape, while a 2-D array must match that shape exactly, so `Range("A1:A3").Value = [1, 2, 3]`
        // needs this arm and would be rejected as a 1x3. The element conversion is the same call the 2-D
        // builder makes; only the dimension differs, which is why this is a sibling rather than a flag.
        // ...and ANY 1-D array satisfies it, not only object[]: a script's `[1, 2, 3]` arrives as Int32[], which
        // reported "operation not supported: Type Int32[] not supported for VARIANT conversion".
        if (val is Array flat && flat.Rank == 1)
        {
            // A JAGGED array, a rank-1 array whose elements are arrays, is how a script writes a 2-D range
            // (`[[1], [2], [3]]` for a column). Excel needs the 2-D shape for that: measured, a plain 1-D list
            // assigned to a COLUMN range was accepted but broadcast the first value into every cell, because a
            // 1-D array maps to a ROW. Both shapes are therefore built, from what the value actually is.
            if (flat.Length > 0 && flat.GetValue(0) is Array)
                return ArrayToSafeArrayVariant(flat);   // reads rows; allocates no row objects
            return FlatToSafeArrayVariant(flat);
        }
        // A script LIST, the engine's ScriptUnboxedList/ScriptBoxedList, or a plain List<object>, reaches a
        // COM value position as an IList and had NO arm at all: measured, "operation not supported: 'Type
        // ScriptUnboxedList`1 not supported for VARIANT conversion'", so `Range.Value = someList` was refused
        // while the literal `[1, 2, 3]` worked (that arrives as Int32[]). ONE array for the whole list, not one
        // per cell, then the same 1-D builder; the boxing is inherent, because a VARIANT element is converted
        // from an object anyway. Arrays never reach here, the arm above takes them first.
        // The ENGINE's script lists, ScriptUnboxedList<T> / ScriptBoxedList. They expose IScriptList and
        // IEnumerable<T>, but NOT IList, which is why the arm above never saw them: a script's own list fell
        // through to the throw at the end of this switch ("Type ScriptUnboxedList`1 not supported for VARIANT
        // conversion") for the most natural thing a script can write, `Range.Value = myList`.
        // ToArray() is the list's own conversion, a typed T[] for an unboxed list, object[] for a boxed one,
        // so the Array arms above do the rest: ONE array per call, never one per cell. A list OF LISTS is the
        // 2-D case and is built here, because its elements are lists and not arrays.
        if (val is IScriptList scriptList)
        {
            var flatList = scriptList.ToArray();
            if (flatList.Length > 0 && flatList.GetValue(0) is IScriptList)
            {
                var rows = new object?[flatList.Length][];
                for (var i = 0; i < flatList.Length; i++)
                {
                    var inner = ((IScriptList)flatList.GetValue(i)!).ToArray();
                    var row = new object?[inner.Length];
                    for (var j = 0; j < inner.Length; j++) row[j] = inner.GetValue(j);
                    rows[i] = row;
                }
                return ArrayToSafeArrayVariant(rows);
            }
            return ConvertToVariant(flatList, out bStr);
        }

        if (val is System.Collections.IList list)
        {
            var flatList = new object?[list.Count];
            for (var i = 0; i < list.Count; i++) flatList[i] = list[i];
            return FlatToSafeArrayVariant(flatList);
        }

        if (val == null)
            return new Variant { vt = ComConst.VT_EMPTY };

        switch (val)
        {
            case bool b:
                return new Variant { vt = ComConst.VT_BOOL, boolVal = (short)(b ? -1 : 0) };

            case short s:
                return new Variant { vt = ComConst.VT_I2, sVal = s };

            case int i:
                return new Variant { vt = ComConst.VT_I4, iVal = i };

            case uint ui:
                return new Variant { vt = ComConst.VT_UI4, uiVal = ui };

            case byte b:
                return new Variant { vt = ComConst.VT_UI1, bVal = b };

            case sbyte sb:
                return new Variant { vt = ComConst.VT_I1, cVal = sb };

            case float f:
                return new Variant { vt = ComConst.VT_R4, fltVal = f };

            case long l:
                return new Variant { vt = ComConst.VT_I8, lVal = l };

            case double d:
                return new Variant { vt = ComConst.VT_R8, dVal = d };

            case decimal dec:
                var cyValue = decimal.ToInt64(dec * 10000m);
                return new Variant { vt = ComConst.VT_CY, llVal = checked((IntPtr)cyValue) };

            case DateTime dt:
                return new Variant { vt = ComConst.VT_DATE, dVal = dt.ToOADate() };

            case string s:
                bStr = Marshal.StringToBSTR(s);
                return new Variant { vt = ComConst.VT_BSTR, llVal = bStr };

            case ComObject obj:
                return new Variant { vt = ComConst.VT_DISPATCH, llVal = obj.NativePointer };

            case ComNode node:
                return new Variant { vt = ComConst.VT_DISPATCH, llVal = node.NativePointer };

            default:
                throw new NotSupportedException($"Type {val.GetType().Name} not supported for VARIANT conversion.");
        }
    }

    // -----------------------------------------------------------------
    // Unwrap VARIANT to .NET type T
    // -----------------------------------------------------------------
    internal static T UnwrapVariant<T>(Variant v)
    {
        // 1. Instant Exit Conditions
        if (v.vt is ComConst.VT_EMPTY or ComConst.VT_NULL)
            return default!;

        // 2. Direct Target Type Mapping (Fast Paths)
        var targetType = typeof(T);

        // Dynamic Object Mapping (Excel variant sniff)
        if (targetType == typeof(object))
        {
            return v.vt switch
            {
                ComConst.VT_BSTR => v.llVal == IntPtr.Zero ? (T)(object)string.Empty : (T)(object)Marshal.PtrToStringBSTR(v.llVal),
                ComConst.VT_BOOL => (T)(object)(v.boolVal != 0),

                ComConst.VT_R8 when Math.Abs(v.dVal % 1.0) < double.Epsilon && v.dVal is >= int.MinValue and <= int.MaxValue
                    => (T)(object)(int)v.dVal,

                ComConst.VT_R8 when Math.Abs(v.dVal % 1.0) < double.Epsilon && v.dVal is >= long.MinValue and <= long.MaxValue
                    => (T)(object)(long)v.dVal,

                ComConst.VT_R8 => (T)(object)v.dVal,

                ComConst.VT_I4 => (T)(object)v.iVal,
                ComConst.VT_I2 => (T)(object)v.sVal,
                ComConst.VT_UI1 => (T)(object)v.bVal,
                ComConst.VT_I1 => (T)(object)v.cVal,
                ComConst.VT_R4 => (T)(object)v.fltVal,
                ComConst.VT_DATE => (T)(object)DateTime.FromOADate(v.dVal),
                ComConst.VT_CY => (T)(object)(v.llVal / 10000m),
                // COM object results (e.g. ie.Document, obj.Application) unwrap to a ComObject
                // so the caller can Track() it into a script-callable ComNode.
                ComConst.VT_DISPATCH or ComConst.VT_UNKNOWN => (T)(object)UnwrapRawCom(v),

                // VT_ERROR carrying DISP_E_PARAMNOTFOUND is `Missing`: a callee handing BACK an optional
                // parameter it was not given. Measured on Excel's UsedRange.Value, "invalid cast: Automatic
                // unwrapping for VT_8204 to Object is not supported". It is the round-trip partner of
                // ConvertArgToVariant: sending Missing without accepting it turns a freeze-values assignment
                // into a cast error.
                // An ARRAY-backed VARIANT: `Range.Value` over more than ONE cell comes back as
                // VT_ARRAY|VT_VARIANT (vt 0x200C = decimal 8204). Measured, Excel's freeze-values trick
                // (UsedRange.Value = UsedRange.Value) died here on the throw below. The machinery for reading
                // such an array already existed, UnwrapSafeArrayToJagged, the typed unwrap simply never
                // looked for it, so a multi-cell read was refused while a single cell worked.
                _ when (v.vt & ComConst.VT_ARRAY) != 0 => (T)(object)UnwrapSafeArrayToJagged(v)!,
                ComConst.VT_ERROR => (T)(object?)null!,
                _ => throw new InvalidCastException($"Automatic unwrapping for VT_{v.vt} to Object is not supported.")
            };
        }

        // Direct Typed Conversions
        if (targetType == typeof(string) && v.vt == ComConst.VT_BSTR)
            return (T)(object)Marshal.PtrToStringBSTR(v.llVal);

        if (targetType == typeof(bool) && v.vt == ComConst.VT_BOOL)
            return (T)(object)(v.boolVal != 0);

        if (targetType == typeof(double) && v.vt == ComConst.VT_R8)
            return (T)(object)v.dVal;

        if (targetType == typeof(DateTime) && v.vt == ComConst.VT_DATE)
            return (T)(object)DateTime.FromOADate(v.dVal);

        if (targetType == typeof(byte) && v.vt == ComConst.VT_UI1)
            return (T)(object)v.bVal;

        if (targetType == typeof(sbyte) && v.vt == ComConst.VT_I1)
            return (T)(object)v.cVal;

        if (targetType == typeof(float))
        {
            switch (v.vt)
            {
                case ComConst.VT_R4:
                    return (T)(object)v.fltVal;
                case ComConst.VT_R8:
                    return (T)(object)(float)v.dVal;
                case ComConst.VT_I4:
                    return (T)(object)(float)v.lVal;
            }
        }

        if (targetType == typeof(decimal))
        {
            switch (v.vt)
            {
                case ComConst.VT_CY:
                    return (T)(object)(v.llVal / 10000m);
                case ComConst.VT_R8:
                    return (T)(object)(decimal)v.dVal;
                case ComConst.VT_R4:
                    return (T)(object)(decimal)v.fltVal;
                case ComConst.VT_I4:
                    return (T)(object)(decimal)v.lVal;
            }
        }

        // 3. Complex Component Handling (Dispatched via Local Functions)
        if (targetType == typeof(ComObject))
            return UnwrapComObject(v);

        return IsIntegerType(targetType)
            ? ConvertToIntegerType(v, targetType)
            : throw new InvalidCastException($"Cannot convert VT_{v.vt} to {targetType.Name}");

        // ── Local Helper Functions ───────────────────────────────────────

        static bool IsIntegerType(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) ||
            t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) ||
            t == typeof(byte) || t == typeof(sbyte);

        static T UnwrapComObject(Variant sourceVariant)
        {
            var pUnknown = sourceVariant.vt is ComConst.VT_DISPATCH or ComConst.VT_UNKNOWN ? sourceVariant.llVal : IntPtr.Zero;
            if (pUnknown == IntPtr.Zero)
                throw new InvalidCastException($"Cannot convert VT_{sourceVariant.vt} to ComObject");

            var iid = ComConst.IidIDispatch;
            var hr = VtableCall.QueryInterface(pUnknown, ref iid, out var pDispatch);
            if (hr < 0 || pDispatch == IntPtr.Zero)
                throw new COMException("Object does not support IDispatch", hr);

            return (T)(object)new ComObject(pDispatch, addRef: false);
        }

        // Non-generic twin of UnwrapComObject for the typeof(object) branch: a VT_DISPATCH /
        // VT_UNKNOWN result becomes a ComObject the ComScope can Track into a ComNode.
        static ComObject UnwrapRawCom(Variant sourceVariant)
        {
            var pUnknown = sourceVariant.vt is ComConst.VT_DISPATCH or ComConst.VT_UNKNOWN ? sourceVariant.llVal : IntPtr.Zero;
            if (pUnknown == IntPtr.Zero)
                throw new InvalidCastException($"Cannot convert VT_{sourceVariant.vt} to ComObject");

            var iid = ComConst.IidIDispatch;
            var hr = VtableCall.QueryInterface(pUnknown, ref iid, out var pDispatch);
            if (hr < 0 || pDispatch == IntPtr.Zero)
                throw new COMException("Object does not support IDispatch", hr);

            return new ComObject(pDispatch, addRef: false);
        }

        static T ConvertToIntegerType(Variant sourceVariant, Type target)
        {
            var rawValue = sourceVariant.vt switch
            {
                ComConst.VT_I1 => (ulong)sourceVariant.cVal,
                ComConst.VT_UI1 => sourceVariant.bVal,
                ComConst.VT_I2 => (ulong)sourceVariant.sVal,
                ComConst.VT_UI2 => sourceVariant.uiVal,
                ComConst.VT_I4 => (ulong)sourceVariant.iVal,
                ComConst.VT_UI4 => sourceVariant.uiVal,
                ComConst.VT_I8 => (ulong)sourceVariant.lVal,
                ComConst.VT_UI8 => sourceVariant.ullVal,
                ComConst.VT_R8 => (ulong)sourceVariant.dVal,
                ComConst.VT_BOOL => (ulong)(sourceVariant.boolVal != 0 ? 1 : 0),
                ComConst.VT_BSTR => ParseNumericString(Marshal.PtrToStringBSTR(sourceVariant.llVal)),
                _ => throw new InvalidCastException($"Cannot convert VT_{sourceVariant.vt} to a numeric type.")
            };

            return target switch
            {
                _ when target == typeof(int) => (T)(object)(int)rawValue,
                _ when target == typeof(long) => (T)(object)(long)rawValue,
                _ when target == typeof(short) => (T)(object)(short)rawValue,
                _ when target == typeof(byte) => (T)(object)(byte)rawValue,
                _ when target == typeof(sbyte) => (T)(object)(sbyte)rawValue,
                _ when target == typeof(uint) => (T)(object)(uint)rawValue,
                _ when target == typeof(ulong) => (T)(object)rawValue,
                _ when target == typeof(ushort) => (T)(object)(ushort)rawValue,
                _ => throw new InvalidCastException()
            };
        }

        static ulong ParseNumericString(string? str)
        {
            if (string.IsNullOrEmpty(str))
                throw new InvalidCastException("Empty string cannot be converted to numeric");

            var culture = CultureInfo.InvariantCulture;
            var styles = NumberStyles.Any;

            if (ulong.TryParse(str, styles, culture, out var ul))
                return ul;

            if (long.TryParse(str, styles, culture, out var l))
                return (ulong)l;

            throw new InvalidCastException($"Cannot parse numeric string '{str}'");
        }
    }

    // -----------------------------------------------------------------
    // Unwrap SAFEARRAY(VARIANT) → object[][]
    // Converts a 2D VARIANT array (as returned by Excel Range.Value2)
    // into a managed jagged array.
    // -----------------------------------------------------------------
    internal static object?[][]? UnwrapSafeArrayToJagged(Variant arrayVar)
    {
        if ((arrayVar.vt & ComConst.VT_ARRAY) == 0)
            return null;

        var psa = arrayVar.llVal;
        if (psa == IntPtr.Zero)
            return null;

        var hr = NativeMethods.SafeArrayGetVartype(psa, out var _);
        if (hr < 0)
            throw new COMException("SafeArrayGetVartype failed", hr);

        hr = NativeMethods.SafeArrayGetLBound(psa, 1, out var lb1);
        if (hr < 0) throw new COMException("SafeArrayGetLBound dim1 failed", hr);

        hr = NativeMethods.SafeArrayGetUBound(psa, 1, out var ub1);
        if (hr < 0) throw new COMException("SafeArrayGetUBound dim1 failed", hr);

        hr = NativeMethods.SafeArrayGetLBound(psa, 2, out var lb2);
        if (hr < 0) throw new COMException("SafeArrayGetLBound dim2 failed", hr);

        hr = NativeMethods.SafeArrayGetUBound(psa, 2, out var ub2);
        if (hr < 0) throw new COMException("SafeArrayGetUBound dim2 failed", hr);

        var rows = ub1 - lb1 + 1;
        var cols = ub2 - lb2 + 1;

        if (rows <= 0 || cols <= 0)
            return [];

        var result = new object?[rows][];
        var indices = stackalloc int[2];
        // A VARIANT scratch for one cell. Variant is blittable, so it lives on the STACK: no unmanaged allocation
        // per call, and, the safety half, no free to pair with it, which is the kind of lifetime a bridge
        // should not be able to get wrong. Created ONCE per call, never inside the cell loop, so the stack does
        // not grow with the range; it is cleared before each use (VariantClear / ClearBuffer).
        Variant* elementScratch = stackalloc Variant[1];
        var elementBuf = (IntPtr)elementScratch;

        try
        {
            for (var r = 0; r < rows; r++)
            {
                result[r] = new object?[cols];
                indices[0] = lb1 + r;

                for (var c = 0; c < cols; c++)
                {
                    indices[1] = lb2 + c;
                    ClearBuffer(elementBuf, Variant.SIZE);

                    hr = NativeMethods.SafeArrayGetElement(psa, (IntPtr)indices, elementBuf);
                    if (hr != 0)
                        continue;

                    var cellVar = *(Variant*)elementBuf;
                    try
                    {
                        result[r][c] = cellVar.vt switch
                        {
                            ComConst.VT_EMPTY or ComConst.VT_NULL or ComConst.VT_ERROR => null,
                            ComConst.VT_BSTR => cellVar.llVal == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(cellVar.llVal),
                            ComConst.VT_BOOL => cellVar.boolVal != 0,
                            ComConst.VT_UI1 => cellVar.bVal,
                            ComConst.VT_I1 => cellVar.cVal,
                            ComConst.VT_I4 => cellVar.iVal,
                            ComConst.VT_I2 => (int)cellVar.sVal,
                            ComConst.VT_R4 => (double)cellVar.fltVal,
                            ComConst.VT_UI4 => (long)cellVar.uiVal,
                            ComConst.VT_R8 => cellVar.dVal,
                            ComConst.VT_CY => cellVar.llVal / 10000m,
                            ComConst.VT_DATE => DateTime.FromOADate(cellVar.dVal),
                            ComConst.VT_I8 => cellVar.lVal,
                            ComConst.VT_UI8 => (long)cellVar.ullVal,
                            _ => UnwrapVariant<object>(cellVar)
                        };
                    }
                    finally { NativeMethods.VariantClear(elementBuf); }
                }
            }
        }
        finally { /* nothing to free: elementBuf is a stack buffer */ }

        return result;
    }

    /// <summary>Convert a single cell Variant to a .NET object (for lazy row-by-row reading).</summary>
    internal static object? UnwrapCellVariant(Variant cellVar)
    {
        return cellVar.vt switch
        {
            ComConst.VT_EMPTY or ComConst.VT_NULL or ComConst.VT_ERROR => null,
            ComConst.VT_BSTR => cellVar.llVal == IntPtr.Zero ? null : Marshal.PtrToStringBSTR(cellVar.llVal),
            ComConst.VT_BOOL => cellVar.boolVal != 0,
            ComConst.VT_UI1 => cellVar.bVal,
            ComConst.VT_I1 => cellVar.cVal,
            ComConst.VT_I2 => (int)cellVar.sVal,
            ComConst.VT_I4 => cellVar.lVal,
            ComConst.VT_UI2 => (int)cellVar.uiVal,
            ComConst.VT_UI4 => (long)cellVar.uiVal,
            ComConst.VT_R4 => (double)cellVar.fltVal,
            ComConst.VT_R8 => cellVar.dVal,
            ComConst.VT_CY => cellVar.llVal / 10000m,
            ComConst.VT_DATE => DateTime.FromOADate(cellVar.dVal),
            ComConst.VT_I8 => cellVar.lVal,
            ComConst.VT_UI8 => (long)cellVar.ullVal,
            _ => UnwrapVariant<object>(cellVar)
        };
    }

    private static void ClearBuffer(IntPtr ptr, int size)
    {
        for (var i = 0; i < size; i++)
            Marshal.WriteByte(ptr, i, 0);
    }
}
