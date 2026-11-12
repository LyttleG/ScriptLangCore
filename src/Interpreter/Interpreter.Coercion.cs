namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    /// The CLR type a declared type NAMES, when it names one: the keywords and the registry through
    /// <see cref="TypeTokenHelper.GetSystemType"/>, and then the engine's own type index (`TextWriter` is a name a
    /// script writes and no registry holds). ONE resolver, because the coercion funnels need the same answer for the
    /// same `TypeInfo`, and a null answer means "not a CLR type" (a script class, a contract, an array).
    /// </summary>
    /// <summary>True when this RUN declared a script type of that name (a class, a struct, an interface, an enum or a
    /// delegate), which SHADOWS a CLR type of the same name: the engine binds a few CLR types as globals (`Form`,
    /// `Control`, `Size` for the UI), so a file's own `class Form` must win in a type position. The declared-name set is
    /// consulted as well as the registries, because a class declared BELOW the member that names it is not registered
    /// yet and would otherwise resolve to the CLR alias. Asked through the interpreter's own case rule (every set here is
    /// built with `_comparer`).</summary>
    internal bool DeclaresScriptType(string name) =>
        (_declaredTypeNames?.Contains(name) ?? false)
        || Classes.ContainsKey(name) || Structs.ContainsKey(name) || DelegateInfos.ContainsKey(name);
    internal static Type? ClrTypeOfTypeInfo(TypeInfo type)
    {
        var direct = TypeTokenHelper.GetSystemType(type);
        // `GetSystemType` answers OBJECT for a name it does not know, which is documented and load-bearing elsewhere,
        // so an object from it means "ask the type index" rather than "this is object". Measured: with the direct answer
        // trusted first, `TextWriter` resolved to Object and the refusal below never fired for `TextWriter w = 42;`.
        if (direct is not null && direct != typeof(object)) return direct;
        if (type.TypeName is not { Length: > 0 } name) return direct;    // a genuinely unnamed type IS object
        if (type.GenericArgs is { Count: > 0 }) return null;             // a generic definition is closed elsewhere
        // A DECLARED SCRIPT CLASS OR STRUCT SHADOWS THE CLR TYPE OF THE SAME NAME. The engine binds a handful of CLR
        // types as globals (`Form`, `Control`, `Size` for the UI), so a script that declares its own `class Form` must
        // still be able to write `Form sender`: without this, the name resolved to `System.Windows.Forms.Form`, the
        // coercion found no bridge for the instance and REFUSED a program that ran before the bridge round (reported
        // from the maintainer's own corpus: `12_ref_class_event.slc` answered "Form is not a Form: a script class
        // reaches the CLR only through a base this engine bridges"). A shadowing class has no CLR type, which is what
        // a null answer means here.
        if (Current is { } self && self.DeclaresScriptType(name)) return null;
        return Current?.ResolveTypeToClr(name, lax: false);
    }

    internal static object? CoerceTo(object? value, TypeInfo targetType, int line, int column, bool probe = false)
    {
        // ONE FUNNEL, ONE METHOD PER FAMILY. Each step answers "is this value mine to convert", in the order the
        // rules depend on: the copy and the bridge come first because they CHANGE the value, a specific target
        // type outranks a generic one, and the System.Type conversion is the last resort. Every body below in this
        // file is the arm that used to sit here, moved verbatim, so the decisions did not move with it.
        object? coerced;
        value = CopyStructForCoercion(value);
        if (TryHandOverBridgedFace(value, targetType, line, column, out coerced)) return coerced;
        value = UnwrapEnumForTarget(value, targetType);
        if (TryCoerceNull(value, targetType, line, column, out coerced)) return coerced;
        var item = value!;   // the null gate answered false, so this is not null
        if (TryCoerceContract(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoercePointer(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceNumericFastPath(item, targetType, out coerced)) return coerced;
        if (TryCoerceArray(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceNullable(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceArrayClassTarget(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceDictionary(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceTuple(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceObjectPassthrough(item, targetType, out coerced)) return coerced;
        if (TryCoerceBoolean(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceCharacter(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceString(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceSameType(item, targetType, out coerced)) return coerced;
        if (TryCoerceNumeric(item, targetType, line, column, out coerced)) return coerced;
        if (TryCoerceDateTimeFamily(item, targetType, line, column, out coerced)) return coerced;
        return CoerceToSystemType(item, targetType, line, column, probe);
    }

    private static object? CopyStructForCoercion(object? value)
    {
        // A STRUCT IS A VALUE TYPE (the maintainer's ruling: assignments and variable references follow C#), so
        // every coercion produces a COPY of the value. This funnel is where assignment, argument binding and return
        // coercion all pass on both engines, so cloning here gives C#'s answers for `var b = a;`, for a by-value
        // parameter and for boxing a struct into an `object` (which copies in C# too). The RECEIVER and a
        // `ref`/`out` argument never come through here, which is exactly right: C# passes `ref this` to a method
        // and shares a `ref` parameter, so both must keep mutating the caller's instance. Before this, the probe
        // `var a = new P(); a.X = 5; var b = a; b.X = 99;` answered `a.X=99` on BOTH legs, i.e. no copy at all.
        if (value is StructInstance siv)
            value = CloneStruct(siv);

        return value;
    }

    private static bool TryHandOverBridgedFace(object? value, TypeInfo targetType, int line, int column, out object? result)
    {
        // A SCRIPT CLASS THAT EXTENDS A BRIDGED CLR BASE IS HANDED OVER AS ITS FACE (ScriptTextBridge): that is the
        // only way a script object can be a `TextWriter` here, and it has to happen at BOTH coercion funnels because a
        // declared script type arrives here while a reflected CLR parameter arrives at CoerceArgument.
        if (value is ClassInstance bridgeInstance && ClrTypeOfTypeInfo(targetType) is { } bridgeTarget
            && bridgeTarget != typeof(object))
        {
            if (ScriptTextBridge.FacadeFor(bridgeInstance, bridgeTarget) is { } bridgeFace) { result = bridgeFace; return true; }
            // A SCRIPT CLASS IS NOT A CLR TYPE, except through a bridged base, so this is a REFUSAL rather than a silent
            // accept. It used to be silent: `TextWriter w = 42;` and `TextWriter w = new Plain();` both bound, which is the
            // failure shape this repository refuses a value for. Only a ClassInstance is refused here, because the widths
            // further down (a script dictionary to a CLR one, an engine instance to its CLR type) are legitimate coercion.
            RuntimeError($"{bridgeInstance.Class.Name} is not a {GetFriendlyTypeName(bridgeTarget)}: a script class reaches the CLR only through a base this engine bridges (TextWriter, TextReader).", line, column);
        }

        result = null;
        return false;
    }

    private static object? UnwrapEnumForTarget(object? value, TypeInfo targetType)
    {
        // An enum member keeps its identity only while it is being assigned to its own enum type;
        // anything else (a long, an int, arithmetic) wants the underlying value.
        if (value is EnumValue cev
            && (targetType.TypeName == null
                || !targetType.TypeName.Equals(cev.EnumName, StringComparison.OrdinalIgnoreCase)))
            value = cev.Value;

        return value;
    }

    private static bool TryCoerceNull(object? value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // NULL HANDLING, FIRST, because ONE rule decides what may hold null. It used to sit BELOW the
        // contract arms, which is why no contract type could: `Function f = null`, a nullable `Function?`,
        // and every field with no initializer (whose default IS null, see the class-construction fill) were
        // all refused, so a class with a `Function` field could not even be constructed, and a nullable one
        // was a contradiction. A contract type is reference-shaped with BaseType Object, so it takes null
        // here. What the arms below still refuse is a NON-null value that does not satisfy the contract
        // (`Iterable i = 42`).
        // ========================================================================
        if (value == null)
        {
            if (targetType.IsArray || targetType.IsNullable ||
                targetType.BaseType == TokenType.StringType ||
                targetType.BaseType == TokenType.Object)
                { result = null; return true; }

            RuntimeError(NullNotAssignableText, line, column);
        }

        result = null;
        return false;
    }

    private static bool TryCoerceContract(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // ITERABLE / RANDOMACCESS / FUNCTION CONTRACT TYPES (a non-null value that does not satisfy the
        // contract is a typed error: the strict contract, and now strict about the VALUE rather than about
        // the absence of one)
        // ========================================================================
        // A CONTRACT names a VALUE's shape, never an ARRAY's: `Function[]` is an array whose ELEMENTS are functions,
        // and the array arm below hands it back as it stands (coercing a contract element would be meaningless:
        // every element either is a function or is not). Without this guard the array itself was tested against
        // the contract, so `Function[] fns = [F1, F2]` was refused with "Expected a Function, got
        // ScriptFunction[], a name no script can write, where the same two functions bound to a plain `Function`
        // fine. The same reasoning covers `Iterable[]` and `RandomAccess[]`.
        var contractApplies = !targetType.IsArray;
        if (contractApplies && IsIterableType(targetType))
        {
            if (IsIterable(value)) { result = value; return true; }
            RuntimeError($"Expected an Iterable, got {value?.GetType().Name ?? "null"}.", line, column);
        }
        if (contractApplies && IsRandomAccessType(targetType))
        {
            if (IsRandomAccess(value)) { result = value; return true; }
            RuntimeError($"Expected a RandomAccess (array, list, tuple or string), got {value?.GetType().Name ?? "null"}.", line, column);
        }
        if (contractApplies && IsFunctionType(targetType))
        {
            if (IsFunction(value)) { result = value; return true; }
            RuntimeError($"Expected a Function, got {value?.GetType().Name ?? "null"}.", line, column);
        }

        result = null;
        return false;
    }

    private static bool TryCoercePointer(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // INTPTR / UINTPTR HANDLING
        // ========================================================================
        if (value is IntPtr ptr)
        {
            // Allow assignment to Object or String without conversion
            if (targetType.BaseType == TokenType.Object || targetType.BaseType == TokenType.StringType)
                { result = ptr; return true; } // IntPtr will be boxed as a regular object

            var longValue = ptr.ToInt64();
            { result = targetType.BaseType switch
            {
                TokenType.Byte => (byte)longValue,
                TokenType.Short => (short)longValue,
                TokenType.Int => (int)longValue,
                TokenType.Long => longValue,
                TokenType.UInt => (uint)longValue,
                TokenType.UShort => (ushort)longValue,
                TokenType.SByte => (sbyte)longValue,
                TokenType.ULong => (ulong)longValue,
                TokenType.Float => (float)longValue,
                TokenType.Double => (double)longValue,
                TokenType.Decimal => (decimal)longValue,
                _ => RuntimeError<object?>($"Cannot convert IntPtr to {TypeSystem.Name(targetType.BaseType)}.", line, column)
            }; return true; }
        }

        if (value is UIntPtr uptr)
        {
            var ulongValue = uptr.ToUInt64();
            { result = targetType.BaseType switch
            {
                TokenType.Byte => (byte)ulongValue,
                TokenType.Short => (short)ulongValue,
                TokenType.Int => (int)ulongValue,
                TokenType.Long => (long)ulongValue,
                TokenType.UInt => (uint)ulongValue,
                TokenType.UShort => (ushort)ulongValue,
                TokenType.SByte => (sbyte)ulongValue,
                TokenType.ULong => ulongValue,
                TokenType.Float => (float)ulongValue,
                TokenType.Double => (double)ulongValue,
                TokenType.Decimal => (decimal)ulongValue,
                _ => RuntimeError<object?>($"Cannot convert UIntPtr to {TypeSystem.Name(targetType.BaseType)}.", line, column)
            }; return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceNumericFastPath(object value, TypeInfo targetType, out object? result)
    {
        // ========================================================================
        // NUMERIC FAST PATH, avoid Convert.ToDouble round-trip for common conversions
        // ========================================================================
        if (targetType.BaseType is >= TokenType.Byte and <= TokenType.Decimal)
        {
            object? fastResult;
            if (value is long lv)
            {
                fastResult = targetType.BaseType switch
                {
                    TokenType.Int => (int)lv,
                    TokenType.Short => (short)lv,
                    TokenType.Byte => (byte)lv,
                    TokenType.SByte => (sbyte)lv,
                    TokenType.UShort => (ushort)lv,
                    TokenType.UInt => (uint)lv,
                    TokenType.ULong => (ulong)lv,
                    TokenType.Float => (float)lv,
                    TokenType.Double => (double)lv,
                    TokenType.Decimal => (decimal)lv,
                    TokenType.Long => lv,
                    _ => null
                };
                if (fastResult != null) { result = fastResult; return true; }
            }
            if (value is int iv)
            {
                fastResult = targetType.BaseType switch
                {
                    TokenType.Long => (long)iv,
                    TokenType.Short => (short)iv,
                    TokenType.Byte => (byte)iv,
                    TokenType.SByte => (sbyte)iv,
                    TokenType.UShort => (ushort)iv,
                    TokenType.UInt => (uint)iv,
                    TokenType.ULong => (ulong)iv,
                    TokenType.Float => (float)iv,
                    TokenType.Double => (double)iv,
                    TokenType.Decimal => (decimal)iv,
                    TokenType.Int => iv,
                    _ => null
                };
                if (fastResult != null) { result = fastResult; return true; }
            }
            if (value is double dv)
            {
                fastResult = targetType.BaseType switch
                {
                    TokenType.Int => (int)dv,
                    TokenType.Long => (long)dv,
                    TokenType.Float => (float)dv,
                    TokenType.Decimal => (decimal)dv,
                    TokenType.Double => dv,
                    _ => null
                };
                if (fastResult != null) { result = fastResult; return true; }
            }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceArray(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // ARRAY HANDLING
        // ========================================================================
        if (targetType.IsArray)
        {
            if (value is Array arr)
            {
                if (targetType.JaggedDepth > 0 && arr.Rank != 1)
                    RuntimeError($"Expected a single-dimensional array for jagged assignment.", line, column);
                if (targetType.ArrayRank > 0 && arr.Rank != targetType.ArrayRank)
                    RuntimeError($"Array rank mismatch. Expected {targetType.ArrayRank}, got {arr.Rank}.", line, column);
                // Element coercion for flat scalar-typed arrays (`params byte items`, typed-array
                // params/assignments): convert every element to the declared element type,
                // parity with the .NET-constructor params path and plain-typed params
                // (Plain(256) coerces to 0, so params byte must too). Jagged / multi-dim
                // targets (int[][], int[,]) are skipped, their elements are arrays.
                // When the runtime array is already the target element type, return it
                // AS-IS so aliasing survives (setArr(arr2,...) mutates the caller's array).
                if (targetType.JaggedDepth == 0 && targetType.ArrayRank == 1
                    && (IsNumericType(targetType.BaseType)
                        || targetType.BaseType is TokenType.Bool or TokenType.Char or TokenType.StringType or TokenType.String))
                {
                    var elemInfo = new TypeInfo(targetType.BaseType, false, 0, 0, null);
                    var clrElem = TypeTokenHelper.GetSystemType(elemInfo);
                    if (arr.GetType().GetElementType() == clrElem) { result = arr; return true; }
                    var typed = Array.CreateInstance(clrElem, arr.Length);
                    for (var i = 0; i < arr.Length; i++)
                        typed.SetValue(CoerceTo(arr.GetValue(i), elemInfo, line, column), i);
                    { result = typed; return true; }
                }
                { result = arr; return true; }
            }
            // ScriptBoxedList (e.g. List<T>) can be converted to a typed Array so both
            // call patterns work: db.Fn(new string[] {...}) and db.Fn(new List<string> {...})
            if (value is IScriptList list)
                { result = list.ToArray(); return true; }
            if (value is IScriptHashSet hashSet)
                { result = hashSet.ToArray(); return true; }
            RuntimeError($"Expected an array, got {value.GetType().Name}.", line, column);
        }

        if (targetType.JaggedDepth > 0)
        {
            if (value is Array { Rank: 1 } arr)
                { result = arr; return true; }
            RuntimeError($"Expected a jagged array of depth {targetType.JaggedDepth}.", line, column);
        }

        result = null;
        return false;
    }

    private static bool TryCoerceNullable(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // NULLABLE TYPES
        // ========================================================================
        if (targetType is { IsNullable: true, IsScalar: true })
        {
            var underlyingType = new TypeInfo(targetType.BaseType, false, 0, 0, null);
            { result = CoerceTo(value, underlyingType, line, column); return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceArrayClassTarget(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // ARRAY TYPE (System.Array base class)
        // ========================================================================
        if (targetType is { BaseType: TokenType.Object, TypeName: not null } &&
            (targetType.TypeName.Equals("Array", StringComparison.OrdinalIgnoreCase)))
        {
            if (value is Array) { result = value; return true; }
            RuntimeError($"Expected an array, got {value.GetType().Name}.", line, column);
        }

        result = null;
        return false;
    }

    private static bool TryCoerceDictionary(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // DICTIONARY, typed Dictionary<K,V> target
        // ========================================================================
        // A dict literal is a RAW Dictionary<object,object?> (the tree and the VM both build
        // one), which the named-object passthrough below would otherwise return unchanged,
        // so `{ "b": 25 } as Dictionary<string, int>` came back null / threw, and a cast to a
        // typed dictionary never coerced its entries. Rebuild the raw dict into the typed
        // wrapper the target asks for, coercing every key/value (delegated to the wrapper's
        // Add). IScriptDictionary values pass through unchanged, they are already typed
        // wrappers and aliasing must survive (same reference, like C# `as`).
        if (targetType is { BaseType: TokenType.Object, TypeName: not null }
            && targetType.TypeName.Equals("Dictionary", StringComparison.OrdinalIgnoreCase)
            && targetType.GenericArgs is { Count: 2 })
        {
            if (value is IScriptDictionary) { result = value; return true; }
            if (value is IDictionary rawDict)
            {
                var typedDict = CreateScriptDictionary(targetType.GenericArgs[0], targetType.GenericArgs[1], rawDict.Count);
                foreach (DictionaryEntry e in rawDict)
                    typedDict.Add(e.Key, e.Value, line, column);
                { result = typedDict; return true; }
            }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceTuple(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // TUPLE TYPE, typed tuple target (e.g. List<(string s, int i)> elements)
        // ========================================================================
        if (targetType.IsTupleType)
        {
            var elems = targetType.TupleElementTypes!;
            var n = PosCount(value);
            var shape = "(" + string.Join(", ", elems.Select(e => e.ToString())) + ")";
            if (n < 0)
                RuntimeError($"Cannot convert {value?.GetType().Name ?? "null"} to tuple {shape}: not a tuple.", line, column);
            if (n != elems.Count)
                RuntimeError($"Cannot convert {value?.GetType().Name ?? "null"} to tuple {shape}: expected {elems.Count} element{(elems.Count == 1 ? "" : "s")}, got {n}.", line, column);
            // Validate each element with the same coercion rules containers use (numeric
            // widening, nullable, nested tuples). The tuple value itself is returned as-is.
            for (var i = 0; i < elems.Count; i++)
                CoerceTo(PosItem(value, i), elems[i], line, column);
            { result = StampTupleElementNames(value, targetType); return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceObjectPassthrough(object value, TypeInfo targetType, out object? result)
    {
        // ========================================================================
        // OBJECT TYPE (PASSTHROUGH)
        // ========================================================================
        if (targetType is { BaseType: TokenType.Object, IsArray: false })
            { result = value; return true; }

        result = null;
        return false;
    }

    private static bool TryCoerceBoolean(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // BOOLEAN CONVERSIONS
        // ========================================================================
        if (targetType.BaseType == TokenType.Bool)
        {
            switch (value)
            {
                case bool b:
                    { result = b; return true; }

                case string s when bool.TryParse(s, out var boolResult):
                    { result = boolResult; return true; }

                case string s when double.TryParse(s, out var numResult):
                    { result = numResult != 0; return true; }
            }

            if (IsNumeric(value))
            {
                // A NUMBER IS NOT A BOOL, and C# says so: `(bool)0` does not compile, while this funnel answered
                // `false` for 0 and `true` for anything else (measured: 0, 1 and 2 all accepted, and a string
                // still refused). A cast C# rejects is not a nicety this engine should keep, and the refusal names
                // the keystrokes that work. The two STRING arms above stay, because `(bool)"true"` is a parse this
                // engine documents, and a char keeps its own arm below.
                var shown = ValueFormatter.ToDisplay(value);
                RuntimeError($"A number is not a bool: write `{shown} != 0` instead of casting it, or `false` / "
                    + "`true` for a constant.", line, column);
            }

            if (value is char c)
            {
                { result = c != '\0'; return true; }
            }

            RuntimeError($"Cannot convert {value.GetType().Name} to bool.", line, column);
        }

        // ========================================================================
        // NUMERIC CONVERSIONS FROM BOOLEAN
        // ========================================================================
        if (value is bool boolVal && IsNumericType(targetType.BaseType))
        {
            var numericValue = boolVal ? 1 : 0;
            { result = ConvertNumeric(numericValue, targetType.BaseType, line, column); return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceCharacter(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // CHAR CONVERSIONS
        // ========================================================================
        if (targetType.BaseType == TokenType.Char)
        {
            switch (value)
            {
                case char c:
                    { result = c; return true; }
                case string { Length: 1 } s:
                    { result = s[0]; return true; }
                case string s:
                    RuntimeError($"Cannot convert string of length {s.Length} to char.", line, column);
                    break;
            }

            if (IsNumeric(value))
            {
                var intVal = Convert.ToInt32(value);
                if (intVal is >= 0 and <= 65535)
                    { result = (char)intVal; return true; }
                RuntimeError($"Numeric value {intVal} out of range for Char.", line, column);
            }

            RuntimeError($"Cannot convert {value.GetType().Name} to char.", line, column);
        }

        // ========================================================================
        // CHAR TO OTHER TYPES
        // ========================================================================
        if (value is char charVal)
        {
            if (targetType.BaseType == TokenType.StringType)
                { result = charVal.ToString(); return true; }

            if (IsNumericType(targetType.BaseType))
            {
                var intVal = (int)charVal;
                { result = ConvertNumeric(intVal, targetType.BaseType, line, column); return true; }
            }

            if (targetType.BaseType == TokenType.Bool)
                { result = charVal != '\0'; return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceString(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // STRING CONVERSIONS
        // ========================================================================
        if (targetType.BaseType == TokenType.StringType)
        {
            // The ENGINE's rendering, not the CLR's. `value.ToString()` on a script list answered
            // "System.Object[]" while `"" + []` answered `[]`, so a `string` parameter and a concatenation
            // disagreed about the same value, and the CLR name is one no script can write (measured 2026-10-06).
            // It also answers for null the way `+` does (""), where `ToString()` on null would throw.
            { result = ValueFormatter.ToDisplay(value); return true; }
        }

        if (value is string str)
        {
            if (IsNumericType(targetType.BaseType))
            {
                if (double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var doubleResult))
                {
                    { result = ConvertNumeric(doubleResult, targetType.BaseType, line, column); return true; }
                }
                RuntimeError($"Cannot convert string '{str}' to {TypeSystem.Name(targetType.BaseType)}.", line, column);
            }

            switch (targetType.BaseType)
            {
                case TokenType.DateTime when DateTime.TryParse(str, out var dateResult):
                    { result = dateResult; return true; }
                case TokenType.DateTime:
                    RuntimeError($"Cannot convert string '{str}' to DateTime.", line, column);
                    break;
                case TokenType.TimeSpan when TimeSpan.TryParse(str, out var timeResult):
                    { result = timeResult; return true; }
                case TokenType.TimeSpan:
                    RuntimeError($"Cannot convert string '{str}' to TimeSpan.", line, column);
                    break;
            }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceSameType(object value, TypeInfo targetType, out object? result)
    {
        // ========================================================================
        // SAME-TYPE PASSTHROUGH, avoid precision loss through double (long.MaxValue, etc.)
        // ========================================================================
        {
            var sysType = TypeTokenHelper.GetSystemType(targetType);
            if (value.GetType() == sysType)
                { result = value; return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceNumeric(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        // ========================================================================
        // NUMERIC CONVERSIONS
        // ========================================================================
        if (IsNumeric(value) && IsNumericType(targetType.BaseType))
        {
            var doubleValue = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            { result = ConvertNumeric(doubleValue, targetType.BaseType, line, column); return true; }
        }

        result = null;
        return false;
    }

    private static bool TryCoerceDateTimeFamily(object value, TypeInfo targetType, int line, int column, out object? result)
    {
        switch (targetType.BaseType)
        {
            // ========================================================================
            // DATE/TIME CONVERSIONS
            // ========================================================================
            case TokenType.DateTime when value is DateTime dt:
                { result = dt; return true; }
            case TokenType.DateTime when value is DateTimeOffset dto:
                { result = dto.DateTime; return true; }
            case TokenType.DateTime when value is string s && DateTime.TryParse(s, out var dateResult):
                { result = dateResult; return true; }
            case TokenType.DateTime:
                RuntimeError($"Cannot convert {value.GetType().Name} to DateTime.", line, column);
                break;
            case TokenType.DateTimeOffset when value is DateTimeOffset dto:
                { result = dto; return true; }
            case TokenType.DateTimeOffset when value is DateTime dt:
                { result = new DateTimeOffset(dt); return true; }
            case TokenType.DateTimeOffset when value is string s && DateTimeOffset.TryParse(s, out var dtoResult):
                { result = dtoResult; return true; }
            case TokenType.DateTimeOffset:
                RuntimeError($"Cannot convert {value.GetType().Name} to DateTimeOffset.", line, column);
                break;
            case TokenType.TimeSpan when value is TimeSpan ts:
                { result = ts; return true; }
            case TokenType.TimeSpan when value is string s && TimeSpan.TryParse(s, out var tsResult):
                { result = tsResult; return true; }
            case TokenType.TimeSpan when IsNumeric(value):
                {
                    var milliseconds = Convert.ToDouble(value);
                    { result = TimeSpan.FromMilliseconds(milliseconds); return true; }
                }
            case TokenType.TimeSpan:
                RuntimeError($"Cannot convert {value.GetType().Name} to TimeSpan.", line, column);
                break;
        }

        result = null;
        return false;
    }

    private static object? CoerceToSystemType(object value, TypeInfo targetType, int line, int column, bool probe)
    {
        // ========================================================================
        // FALLBACK: SYSTEM TYPE CONVERSION
        // ========================================================================
        var targetSystemType = TypeTokenHelper.GetSystemType(targetType);
        try
        {
            return Convert.ChangeType(value, targetSystemType, CultureInfo.InvariantCulture);
        }
        catch //(Exception ex)
        {
            var valName = TypeSystem.FromClrName(value.GetType().Name);
            var targetName = TypeSystem.Name(targetType.BaseType);
            // A PROBE stays quiet. SelectClrConstructor asks this very coercion whether a candidate constructor
            // ACCEPTS the values (so the choice cannot disagree with the conversion that follows it), and a
            // candidate it then rejects is not the script's mistake: `new StringBuilder([])` made the capacity
            // constructor warn "Type mismatch: expected 'int' but found 'Object[]'" with NO position, because a
            // probe has none to give. The real conversion still warns, at the call site.
            if (!probe && !string.Equals(valName, targetName, StringComparison.OrdinalIgnoreCase))
                Logging.Log.Warning("Type mismatch", $"expected '{targetName}' but found '{valName}'", line, column);
            RuntimeError($"Type error: cannot convert '{valName}' to '{targetName}'.", line, column);
            return null;
        }
    }


    /// <summary>
    /// Converts a numeric value to the specified target type.
    /// </summary>
    private static object ConvertNumeric(double value, TokenType targetType, int line, int column)
    {
        try
        {
            return NumericTypeConverter.ConvertNumeric(value, targetType);
        }
        catch (InvalidCastException ex)
        {
            RuntimeError(FriendlyExceptionMessage(ex), line, column);
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsNumeric(object? value) => NumericTypeConverter.IsNumeric(value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsTruthy(object? val) => val switch
    {
        null => false,
        bool b => b,
        string { Length: > 0 } => true,
        string => false,
        sbyte sb => sb != 0,
        byte b => b != 0,
        short s => s != 0,
        ushort us => us != 0,
        int i => i != 0,
        uint ui => ui != 0,
        long l => l != 0,
        ulong ul => ul != 0,
        char c => c != '\0',
        float f => Math.Abs(f) > Epsilon,
        double d => Math.Abs(d) > Epsilon,
        decimal dec => dec != 0,
        _ => true
    };

    private static readonly IReadOnlyDictionary<TokenType, object> DefaultValueMap = NumericTypeConverter.DefaultValueMap;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object? DefaultFor(TypeInfo type)
    {
        // A STRUCT-VALUED FIELD DEFAULTS TO A ZEROED STRUCT, never null: C# gives a value-type field its default
        // INSTANCE, so `var o = new Outer(); o.In.V = 5;` compiles and works, while this engine answered "Cannot
        // assign property 'V' on null" (measured: `struct:null` where every other field type already matched C#: int
        // 0, bool false, long 0, string null, array null). The declaration comes from the current interpreter, as
        // CloneStruct's comparer does, and no cycle is possible because a C# struct cannot contain itself.
        if (!type.IsNullable && !type.IsArray && type.TypeName != null
            && Current?.Structs.TryGetValue(type.TypeName, out var dropDecl) == true)
            return CreateDefaultStruct(dropDecl);
        return type.IsNullable || type.IsArray || !DefaultValueMap.TryGetValue(type.BaseType, out var defaultValue) ? null : defaultValue;
    }

    /// <summary>A zeroed instance of a struct, which is what an unassigned field of that type holds: every declared
    /// field and property backing field at ITS default, recursively (the same shape the struct ctor's default-filling
    /// uses). The MATERIALISED instance is what a caller stores, so a write through a read that followed it
    /// (`o.In.V = 5`) lands in the field rather than in a throwaway.</summary>
    private static StructInstance CreateDefaultStruct(StructDeclStmt decl)
    {
        var fields = new Dictionary<string, object?>(decl.Fields.Count, Current?._comparer ?? StringComparer.OrdinalIgnoreCase);
        foreach (var f in decl.Fields) fields[f.Name] = DefaultFor(f.Type);
        foreach (var p in decl.Properties) fields[BackingFieldName(p.Name)] = DefaultFor(p.Type);
        return new StructInstance(decl.Name, fields, decl.Line) { Declaration = decl };
    }

    private static IEnumerable<FieldDecl> GetAllFields(ClassInfo classInfo)
    {
        if (classInfo.Parent != null)
            foreach (var f in GetAllFields(classInfo.Parent))
                yield return f;
        foreach (var f in classInfo.Fields)
            yield return f;
    }

    private FuncDecl? FindMethod(ClassInfo classInfo, string methodName)
    {
        var current = classInfo;
        while (current != null)
        {
            var method = FindByName(current.Methods, methodName, static m => m.Name);
            if (method != null) return method;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Determines if a thrown exception matches a catch clause's exception type.
    /// </summary>
    internal bool IsExceptionMatch(object? exception, TypeInfo catchType)
    {
        // Empty catch or 'var' catch (object with no type name) matches everything
        if (catchType is { BaseType: TokenType.Object, TypeName: null })
        {
            return true;
        }

        // Handle null exception
        if (exception == null)
        {
            // Null only matches object/var, not concrete types like string or int
            return false;
        }

        // Get the actual runtime type of the exception
        var exceptionType = exception.GetType();
        var exceptionTypeInfo = MapTypeInfo(exceptionType);

        // For class instances, check inheritance
        if (exception is ClassInstance exInstance)
        {
            if (catchType.TypeName != null)
            {
                if (exInstance.Class.Name.Equals(catchType.TypeName, Comparison))
                    return true;

                var current = exInstance.Class.Parent;
                while (current != null)
                {
                    if (current.Name.Equals(catchType.TypeName, Comparison))
                        return true;
                    current = current.Parent;
                }
            }

            return exceptionTypeInfo.BaseType == catchType.BaseType;
        }

        // For primitive types, require exact type match
        return exceptionTypeInfo.BaseType == catchType.BaseType;
    }

    private static TypeInfo MapTypeInfo(Type t)
    {
        var jaggedDepth = 0;
        var arrayRank = 0;
        var current = t;

        while (current.IsArray)
        {
            var rank = current.GetArrayRank();
            if (rank > 1)
            {
                if (jaggedDepth > 0)
                    throw new ScriptException("Cannot mix jagged and multi-dimensional arrays.");
                arrayRank = rank;
                current = current.GetElementType()!;
                break;
            }
            else
            {
                jaggedDepth++;
                current = current.GetElementType()!;
            }
        }

        var isNullable = current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Nullable<>);
        var coreType = isNullable ? Nullable.GetUnderlyingType(current) : current;
        var baseType = MapTokenType(coreType);
        return new TypeInfo(baseType, isNullable, arrayRank, jaggedDepth, null, null);
    }

    private static TokenType MapTokenType(Type? t) => TypeSystem.TokenTypeOf(t ?? typeof(object));

    private static nint ToNInt(object? arg)
    {
        return arg switch
        {
            nint ptr => ptr,
            long l => (nint)l,
            int i => i,
            _ => (nint)Convert.ToInt64(arg)
        };
    }

    /// <summary>The long behind an enum member. A script `enum`'s members ARE values of its underlying
    /// type (`long`), so every numeric path unwraps here and keeps working exactly as before, while
    /// `typeof` and `is` keep the enum's name because they read the value, not this.</summary>
    internal static object? UnwrapEnum(object? v) => v is EnumValue ev ? ev.Value : v;

    /// <summary>Coerces ONE flat-call argument to the callee's declared parameter type, the way the TREE's
    /// parameter bind does with CoerceTo. The flat path used to bind the raw value, so a declared `double`
    /// parameter received an INT and the body's own arithmetic then ran in INT: measured, `double Half(double
    /// x) { return x / 2; }` called as `Half(3)` answered 1 where the tree answers 1.5, from a compiled
    /// function body (path OFF, i.e. the shipping configuration) and from the top level alike, and
    /// `typeof(IdF(1))` for `float IdF(float x)` answered "int" where the tree answers "float"
    /// (crash_typeof_matrix 7b). A REF/OUT position is left alone (it holds a cell), a params tail is
    /// assembled elsewhere, and an untyped parameter comes back untouched. The CLR types are cached on the
    /// declaration (FuncDecl.ParamClrTypes), so the common case costs comparisons only.</summary>
    internal static object? CoerceFlatCallArg(FuncDecl decl, object? value, int index, int line, int column)
    {
        // A REF/OUT CELL is judged by the ONE rule every other bind path asks (RefuseRefCellOnByValueParam):
        // the flat path bound it silently, so `IdIt(ref x)` from a compiled body returned a value where the
        // tree refuses BY NAME, and a swapped keyword went unnoticed too (vm_params 8a to 8j). The
        // declaration is in hand here, which is why the rule can be asked at all.
        if (value is RefCell)
        {
            if (index < decl.Params.Count)
            {
                var isRefCell = decl.IsRefParams is { Count: > 0 } rp && index < rp.Count && rp[index];
                var isOutCell = decl.IsOutParams is { Count: > 0 } op && index < op.Count && op[index];
                RefuseRefCellOnByValueParam(decl.Params[index].Name, value, isRefCell, isOutCell, line, column, decl.Params[index].Type);
            }
            return value;
        }
        // ONE cached array read per argument: a null entry means "leave this position alone" (a `params`
        // tail, a ref/out cell, an untyped parameter), and typeof(void) marks a typed parameter with no CLR
        // mapping, which never matches and so falls through to CoerceTo below.
        var targets = decl.ParamClrTypes ??= BuildParamClrTypes(decl);
        var target = index < targets.Length ? targets[index] : null;
        if (target == null || value == null || value.GetType() == target)
            // An UNTYPED parameter still unwraps an ENUM MEMBER, which is what the tree's own bind does
            // (CoerceTo: an enum member keeps its identity only while it is assigned to its OWN enum type).
            // Measured: `Row("enum member = ", Color.Red)` for `(string label, object? v) => typeof(v)`
            // reported "Color" from a compiled body where the tree reports "long" (crash_typeof_matrix 3h).
            return value is EnumValue untypedEnum ? untypedEnum.Value : value;
        return CoerceTo(value, decl.Params[index].Type, line, column);
    }

    /// <summary>Coerces a callee's RETURN value to its DECLARED type, the way the tree walker does when a body
    /// leaves through `return`. The VM used to hand the value back untouched, so a declared `int` function could
    /// return a string, a double or nothing at all, silently, and even a legitimate conversion was skipped: an
    /// `int` returning 1.5 answered 1.5 where the tree answers 2, and the caller then ran its arithmetic or its
    /// comparisons on it. The declared CLR type is cached on the declaration, so a return that already has the
    /// declared type costs one comparison; everything else goes through `CoerceTo`, the ONE rule both engines
    /// already share, which is what keeps the message and the conversion identical.</summary>
    /// <summary>The declared return type of a METHOD, checked the same way a free function's is: a REF-RETURNING body
    /// answers the REFERENCE itself (`ref int F()` + `return ref x;`), so its declared type describes what the reference
    /// points at rather than the value handed back, and the coercion is skipped for the cell.
    /// </summary>
    internal static object? MethodReturn(FuncDecl method, object? value, int line, int column)
        => method.IsRefReturn && value is RefCell ? value : CoerceTo(value, method.ReturnType, line, column);
    internal object? CoerceFlatCallResult(FuncDecl? decl, object? value, int line, int column)
    {
        if (decl == null) return value;
        if (decl.IsRefReturn && value is RefCell) return value;   // a ref return answers the reference itself
        var target = decl.ReturnClrType ??= ResolveReturnClrType(decl);
        if (target == null) return value;                 // nothing declared to check against
        if (value != null && target.IsInstanceOfType(value)) return value;
        return CoerceTo(value, decl.ReturnType!, line, column);
    }

    /// <summary>The CLR type to check a return against, or null when there is nothing to check: an untyped
    /// declaration, and the permissive `object` a lambda's compiled body is declared with (the VM skips return
    /// coercion for one on purpose, since its handler signature is not a promise about the value).</summary>
    private static Type? ResolveReturnClrType(FuncDecl decl)
    {
        var t = decl.ReturnType;
        if (t == null) return null;
        if (t.BaseType == TokenType.Void) return null;    // nothing comes back to coerce
        if (t.BaseType == TokenType.Object && t.TypeName == null) return null;
        var clr = TypeTokenHelper.GetSystemType(t);
        return clr == null || clr == typeof(void) ? null : clr;
    }

    private static Type?[] BuildParamClrTypes(FuncDecl decl)
    {
        var arr = new Type?[decl.Params.Count];
        for (var i = 0; i < decl.Params.Count; i++)
        {
            // null = no coercion for this position: a `params` tail (assembled by the caller), a ref/out
            // position (it holds a cell), or an untyped parameter.
            if (decl.IsParams is { Count: > 0 } ps && i < ps.Count && ps[i]) continue;
            if (decl.IsRefParams is { Count: > 0 } refs && i < refs.Count && refs[i]) continue;
            if (decl.IsOutParams is { Count: > 0 } outs && i < outs.Count && outs[i]) continue;
            var t = decl.Params[i].Type;
            // NOTE: a TUPLE parameter is NOT a tuple TYPE here. The parser gives a tuple parameter an
            // `object` TypeInfo and keeps its element NAMES on `FuncDecl.TupleParamNames` instead (measured:
            // `int G((int x, int y) p)` and `int F((int, int) p)` both arrive with `base=Object name=-`
            // `tuple=False`), so an `&& !t.IsTupleType` clause here would be DEAD CODE. What such a parameter
            // needs travels as TupleParamNames and is applied by BindTupleParam, which every call path calls.
            if (t == null || (t.BaseType == TokenType.Object && t.TypeName == null)) continue;
            arr[i] = TypeTokenHelper.GetSystemType(t) ?? typeof(void);   // typeof(void) = always CoerceTo
        }
        return arr;
    }
}
