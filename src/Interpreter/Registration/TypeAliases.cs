namespace ScriptLangCore;

internal partial class Interpreter
{
    public object InvokeNative(string name, params object?[] args)
    {
        if (CurrentFrame.Scopes.TryGet(name, out var func) && func is INativeFunction native)
            return native.Invoke(args)!;

        throw new ScriptException($"Native function '{name}' not found.");
    }

    private void RegisterTypeAliases()
    {
        // ============================================================================
        // TYPE ALIASES - Map C# style names to TokenType and System.Type
        // ============================================================================

        // Script result
        CurrentFrame.Scopes.Define("Result", typeof(ScriptResult));
        CurrentFrame.Scopes.Define("StringBuilder", typeof(StringBuilder));
        CurrentFrame.Scopes.Define("OrderedEnumerable", typeof(OrderedEnumerable));

        CurrentFrame.Scopes.Define("Encoding", typeof(Encoding));
        CurrentFrame.Scopes.Define("Convert", typeof(Convert));

        CurrentFrame.Scopes.Define("Console", typeof(Console));
        CurrentFrame.Scopes.Define("ConsoleColor", typeof(ConsoleColor));
        CurrentFrame.Scopes.Define("Environment", typeof(Environment));

        CurrentFrame.Scopes.Define("Math", typeof(Math));
        CurrentFrame.Scopes.Define("Path", typeof(Path));
        CurrentFrame.Scopes.Define("File", typeof(File));
        CurrentFrame.Scopes.Define("FileAttributes", typeof(FileAttributes));
        CurrentFrame.Scopes.Define("Directory", typeof(Directory));
        CurrentFrame.Scopes.Define("Guid", typeof(Guid));
        CurrentFrame.Scopes.Define("Regex", typeof(Regex));
        CurrentFrame.Scopes.Define("RegexOptions", typeof(RegexOptions));
        CurrentFrame.Scopes.Define("Enum", typeof(Enum));
        CurrentFrame.Scopes.Define("Array", typeof(Array));

        // ============================================================================
        // TYPE ALIASES, Consolidated primitive/value-type registrations
        // Single source of truth: _primitiveTypes array
        // ============================================================================

        var primitiveTypes = new (string Name, Type SystemType, TokenType BaseType, bool IsNullable)[]
        {
            ("byte",   typeof(byte),   TokenType.Byte,   true),
            ("sbyte",  typeof(sbyte),  TokenType.SByte,  true),
            ("short",  typeof(short),  TokenType.Short,  true),
            ("ushort", typeof(ushort), TokenType.UShort, true),
            ("int",    typeof(int),    TokenType.Int,    true),
            ("uint",   typeof(uint),   TokenType.UInt,   true),
            ("long",   typeof(long),   TokenType.Long,   true),
            ("ulong",  typeof(ulong),  TokenType.ULong,  true),
            ("float",  typeof(float),  TokenType.Float,  true),
            ("double", typeof(double), TokenType.Double, true),
            ("decimal",typeof(decimal),TokenType.Decimal,true),
            ("bool",   typeof(bool),   TokenType.Bool,   true),
            ("char",   typeof(char),   TokenType.Char,   true),
            ("object", typeof(object), TokenType.Object, false),
            ("string", typeof(string), TokenType.StringType, false),
            ("DateTime", typeof(DateTime), TokenType.DateTime, true),
            ("DateTimeOffset", typeof(DateTimeOffset), TokenType.DateTimeOffset, true),
            ("TimeSpan", typeof(TimeSpan), TokenType.TimeSpan, true),
        };

        // ── Derive: Scopes.Define, typeMappings, valueTypeNames, nullableMap ──
        var typeInfoNs = new Namespace(_comparer);
        var typeMappings = new Dictionary<string, TypeInfo>(16, _comparer);
        var valueTypeNames = new List<string>(16);
        var nullableMap = new Dictionary<string, Type>(16);

        foreach (var (name, systemType, baseType, isValueType) in primitiveTypes)
        {
            CurrentFrame.Scopes.Define(name, systemType);
            typeMappings[name] = new TypeInfo(baseType, false, 0, 0, null);
            if (isValueType)
            {
                typeMappings[$"{name}?"] = new TypeInfo(baseType, true, 0, 0, null);
                nullableMap[$"{name}?"] = typeof(Nullable<>).MakeGenericType(systemType);
                valueTypeNames.Add(name);
            }
        }

        foreach (var kv in nullableMap)
            CurrentFrame.Scopes.Define(kv.Key, kv.Value);

        foreach (var mapping in typeMappings)
            typeInfoNs.AddProperty(mapping.Key, mapping.Value);

        CurrentFrame.Scopes.Define("TypeInfo", typeInfoNs);

        // Non-primitive types registered in global scope
        CurrentFrame.Scopes.Define("CronExpression", typeof(CronExpression));
        CurrentFrame.Scopes.Define("CronType", typeof(CronType));
        CurrentFrame.Scopes.Define("TimeZoneInfo", typeof(TimeZoneInfo));
        CurrentFrame.Scopes.Define("TaskStatus", typeof(TaskStatus));
        CurrentFrame.Scopes.Define("CancellationTokenSource", typeof(CancellationTokenSource));
        CurrentFrame.Scopes.Define("CancellationToken", typeof(CancellationToken));

        // typeof operator - works on any value
        RegisterNative("typeof", TokenType.StringType, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("typeof expects exactly one argument.");

            return GetFriendlyTypeNameOfValue(args[0]);
        });

        // VmStats - reports bytecode VM vs tree-walker invocation coverage.
        //
        // THE INTERPRETER RUNNING, NOT THE ONE THAT REGISTERED THIS NATIVE: the counters are per-interpreter fields, and
        // a namespace member captures its registering interpreter, so inside a nested `RunScript` this used to report
        // the OUTER run's counters (measured: a file that runs five compiled lambdas reported `VM: 0` before and after
        // from a nested run, where the same file run directly reported `25 -> 36`, and `toplevel:` never moved either).
        // The pattern is the one `__define_const__` states for the same question: ask `Current`, fall back to the
        // registering interpreter (a pool thread, where a `Task.Run` callback runs, has no `Current`).
        RegisterNative("VmStats", TokenType.StringType, _ =>
        {
            var self = Current ?? this;
            var total = self.VmInvocations + self.TreeInvocations;
            // The SHORT form still carries the top-level count: the two above count FUNCTION invocations, so a
            // script whose work is all at the top level reports "no function invocations yet" while statements
            // are running on the VM, and `toplevel:` is the only field that can see them.
            if (total == 0)
                return $"VM: 0 (no function invocations yet) | Tree: 0 | toplevel: {self.TopLevelVmStatements}";
            var vmPct = (self.VmInvocations * 100.0) / total;
            return $"VM: {self.VmInvocations} ({vmPct:F1}%) | Tree: {self.TreeInvocations} ({100.0 - vmPct:F1}%) | Total: {total} | cacheHits: {self.VmCacheHits} misses: {self.VmCacheMisses} | gfast: {self.GlobalFastLoads}/{self.GlobalFastStores} | toplevel: {self.TopLevelVmStatements}";
        });

        // VmSetEnabled(bool) - globally enables/disables the bytecode VM (A-B diagnostics). STRICT about the
        // argument, for the reason VmSetTopLevelEnabled's own comment gives: this used to read
        // `args[0] is bool and true`, so a non-boolean silently meant FALSE, and a switch whose whole job is to
        // tell a runner which engine it measured must not answer by turning the path off.
        RegisterNative("VmSetEnabled", TokenType.Object, args =>
        {
            if (args.Length != 1 || args[0] is not bool enabled)
                throw new ScriptException("VmSetEnabled expects exactly one boolean argument.");
            VmEnabled = enabled;
            return null;
        });

        // VmSetTopLevelEnabled([bool]) - the A/B switch for the TOP-LEVEL path: true runs each top-level
        // statement on the VM instead of the tree walker, while VmSetEnabled keeps governing function bodies.
        // NO ARGUMENT reports the state, the shape Diagnostics.LogTraces uses, so a runner can turn it on,
        // measure and turn it off again in ONE process (an environment variable cannot change mid-run), and it
        // ANSWERS what it set rather than nothing, because the point of the switch is to be verified. VmStats'
        // `toplevel:` field is the observable half, and a statement already executed keeps the engine it ran on.
        RegisterNative("VmSetTopLevelEnabled", TokenType.Object, args =>
        {
            if (args.Length == 0) return TopLevelVmEnabled;
            // Both switches are strict about their argument now, and the difference worth knowing is the ARGLESS
            // form: this one READS the state with no argument (the shape Diagnostics.LogTraces uses), while
            // VmSetEnabled requires its boolean.
            if (args.Length > 1 || args[0] is not bool want)
                throw new ScriptException("VmSetTopLevelEnabled expects one boolean argument, or none to read the state.");
            TopLevelVmEnabled = want;
            return TopLevelVmEnabled;
        });

        // VmSetStatementCarrier([bool]) - the A/B switch for the PARKED STATEMENT CARRIER: with it on, a program body
        // the compiler could not compile as ONE unit is retried with its uncompilable top-level statements handed to
        // the TREE, so a file the strip would otherwise refuse by name compiles to a runnable entry instead (see
        // Interpreter.StatementCarrierEnabled). OFF by default, and it is a SWITCH for the top-level path's reason:
        // the mechanism works and takes the strip from 52 refusals of 505 suites to 23, but the suites it newly
        // strips are not all FAITHFUL yet.
        // The SHAPE is VmSetTopLevelEnabled's (no argument reads the state, one boolean sets it, and the answer is
        // what it set) for the same reason: a runner has to turn it on, measure and turn it off in ONE process.
        // Before this existed the only way in was SLC_CARRIED_STATEMENTS, an environment variable, and a SUITE cannot
        // set one (Process.Start takes no environment), so the carrier had no pin at all beyond a hand-run A/B.
        RegisterNative("VmSetStatementCarrier", TokenType.Object, args =>
        {
            if (args.Length == 0) return StatementCarrierEnabled;
            if (args.Length > 1 || args[0] is not bool wantCarrier)
                throw new ScriptException("VmSetStatementCarrier expects one boolean argument, or none to read the state.");
            StatementCarrierEnabled = wantCarrier;
            return StatementCarrierEnabled;
        });

        // VmSetTrace(bool) - enables/disables per-opcode execution tracing ([vm] ip=… OP sp=…)
        RegisterNative("VmSetTrace", TokenType.Object, args =>
        {
            if (args.Length != 1) throw new ScriptException("VmSetTrace expects exactly one boolean argument.");
            Vm.Trace = args[0] is bool and true;
            return null;
        });

        // ── retention diagnostic, print(DbgRetention()) after .gc leak / long sessions ──
        RegisterNative("DbgRetention", TokenType.StringType, _ => DbgRetentionDump());
        // VmReset() - zeroes the VM/tree invocation counters (per-state A-B percentages)
        RegisterNative("VmReset", TokenType.Object, _ =>
        {
            VmInvocations = 0;
            TreeInvocations = 0;
            GlobalFastLoads = 0;
            GlobalFastStores = 0;
            TopLevelVmStatements = 0;
            return null;
        });

        // typename operator - works on type strings (like C# typeof)
        RegisterNative("typename", TokenType.Object, args =>
        {
            if (args.Length != 1) throw new ScriptException("typename expects exactly one argument.");
            var typeName = args[0] as string ?? throw new ScriptException("Type name required");

            // The SAME lookup `x is Name` uses, so a name the language accepts cannot report itself unknown here:
            // one lookup for a name->Type, not two that disagree. (Until 2026-09-16 this went through the registry
            // alone, so `typename("DateTime")` was refused while `DateTime.Now` and `x is DateTime` both resolved,
            // the `is` gate had a CLR namespace scan that this path lacked.)
            if (ResolveTypeToClr(typeName, lax: !_caseSensitive) is { } resolved) return resolved;
            throw new ScriptException($"Unknown type name: {typeName}");
        });

        // `nameof` is NOT registered here: it is a PARSE-TIME construct (Parser.cs rewrites `nameof(x)` into
        // the string it names), which is what makes it the C# operator rather than a runtime helper. The old
        // builtin took EVALUATED arguments, so it answered a value where C# gives the identifier.
    }

    private static readonly ConcurrentDictionary<Type, string> TypeNameCache = new();

    // ── retention diagnostic (DbgRetention native) ──
    internal static int DebugTypeNameCacheCount() => TypeNameCache.Count;

    /// <summary>typename native, case-insensitive name → CLR type lookup (no per-call
    /// ToLowerInvariant allocation).</summary>
    private static readonly Dictionary<string, Type> TypeNameLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = typeof(int),
        ["double"] = typeof(double),
        ["bool"] = typeof(bool),
        ["string"] = typeof(string),
        ["char"] = typeof(char),
        ["byte"] = typeof(byte),
        ["short"] = typeof(short),
        ["long"] = typeof(long),
        ["float"] = typeof(float),
        ["decimal"] = typeof(decimal),
        ["object"] = typeof(object),
    };

    /// <summary>The script-facing name for a CLR type, the name <c>print</c> gives, and the one the JSON
    /// writer builds most <c>__type</c> tags from. It is not the tag verbatim: the writer marks a struct tag
    /// with a leading '#' (<c>"#Pt"</c>, stripped again on the way back) and tags a script function by its
    /// storage class (<c>"ScriptFunction"</c>), so the tag agrees with <c>typeof</c> for a class instance
    /// (<c>"Person"</c>) and not for those two. For a VALUE that has a declared script type of its own use
    /// <see cref="GetFriendlyTypeNameOfValue"/> instead, this one can only see the storage class.</summary>
    internal static string GetFriendlyTypeName(Type type) => TypeNameCache.GetOrAdd(type, static t => BuildFriendlyTypeName(t));

    /// <summary>The script-facing name of a VALUE, what <c>typeof</c> reports.
    ///
    /// A script class or struct instance is named by its DECLARED type, not by its runtime storage class:
    /// every class instance is one <see cref="ClassInstance"/> and every struct instance one
    /// <see cref="StructInstance"/>, so asking <see cref="GetFriendlyTypeName"/> about the value answered
    /// "Class"/"Struct" for all of them. That is a category, not a type, it resolves to nothing, so
    /// <c>Json.Deserialize(typeof(p), json)</c> reported the class as an unknown type name, and the same
    /// string cannot be fed to a cast or an <c>is</c> check either. The declared name is the one those
    /// callers can map, and the one the caller asked about.
    ///
    /// A DEFINITION used as a value (the class name itself, or a generic type reference) is named the same
    /// way and for the same reason: <c>typeof(Animal)</c> is "Animal" and
    /// <c>typeof(Dictionary&lt;string, object&gt;)</c> is "Dictionary&lt;string, object&gt;", where both used to
    /// answer "ClassInfo" (the storage class of the engine's type object, again a category rather than a name).
    ///
    /// <c>is Class</c>/<c>as Class</c> are deliberately NOT affected: they match on the contract keyword
    /// through <c>IsKnownType</c>, not on this string. The split is the one a List already has, its typeof
    /// is "List"/"List&lt;int&gt;" while <c>l is List</c> stays true regardless.
    ///
    /// This is the name <c>typeof</c> itself reports, and the one the arms that talk about a VALUE's own kind use
    /// ("Cannot index object of type …", "Unary minus not defined for type …"). Where the value NAMES a type and
    /// that type is the subject, a member miss on it, or the REPL printing what a variable is, use
    /// <see cref="ReportedTypeName"/> instead.</summary>
    internal static string GetFriendlyTypeNameOfValue(object? value) => value switch
    {
        EnumValue ev => ev.EnumName,
        null => "null",
        ClassInstance ci => ci.Class.Name,
        StructInstance si => si.StructName,
        // A DEFINITION used as a value (a class, record, struct or enum NAME) names the type it denotes,
        // exactly as an INSTANCE does. It used to answer "ClassInfo", the storage class of the engine's own
        // type object: a category that resolves to nothing (the same defect the two arms above were fixed
        // for), so `typeof(Animal)` said "ClassInfo" while `typeof(new Animal())` said "Animal". A class,
        // record or enum binds a ClassInfo; a struct binds a bare TypeInfo handle, and a generic type
        // reference (`Dictionary<string, object>`) is a TypeInfo too, so the TypeInfo arm renders the whole
        // spelling and covers a struct handle and a generic type in one.
        ClassInfo def => def.Name,
        TypeInfo ti => TypeInfo.Spelling(ti),
        // A Type VALUE is the runtime's RuntimeType, a CLR name that leaks exactly the way "Class"/"Struct"
        // did, and `typename("int")` hands one straight to a script. Name the concept a script asked for,
        // which is also what the error path already calls it (TypeHelpers calls it "type").
        Type => "Type",
        // A BOXED collection is not a CLR generic type, so the mapper below can only name it by its CLR type
        // (`ScriptBoxedList` -> "List") and the ELEMENT TYPE the instance carries never reaches the name. That is
        // why `List<int>` printed with its argument and `List<int?>` printed the bare `List`: a nullable element
        // is what makes the list BOXED. The element type lives on the INSTANCE, so the name is built here, from
        // it, with the same shape the mapper uses for `List<T>`.
        ScriptBoxedList boxedList => $"List<{BoxedElementSpelling(boxedList.ElementType)}>",
        // The LAZY typed read reports the DECLARATION the read was asked for (`List<int?>`, `string[]`, `IList`),
        // not the wrapper class it happens to be: the value stands for that declaration, and every one of those
        // names is one an author wrote and can write back. `is LazyDeserializedList` still answers through the
        // registry alias, so nothing a script could ask for is lost.
        ScriptLazyDeserializedList lazyList => lazyList.DeclaredName,
        // A container read LAZILY (`Avro.Read("Iterable", data)`) reports the name the read was asked
        // for, the same rule as the lazy JSON read above and for the same reason: the value STANDS FOR that
        // declaration, and "Iterable" is a name an author wrote and can write back.
        AvroLazyContainer lazyContainer => lazyContainer.DeclaredName,
        _ => GetFriendlyTypeName(value.GetType()),
    };

    /// <summary>One element type as a script can write it, with the NULLABLE half kept. `Spelling` renders `?`
    /// inside a generic spelling but returns early for a non-generic type, so a name arriving here already ending
    /// in `?` is left alone and one carrying `IsNullable` without the mark gets it; appending it twice would be as
    /// wrong as dropping it. A BARE `object` element keeps no mark: `object?` and `object` are the same boxed
    /// element here, and this name is READ by `ReportedTypeArguments`, which hands its arguments to a resolver a
    /// `?` on an untyped element makes choke (measured: "Collection type not supported").
    ///
    /// The mark is tested with the LAST CHARACTER rather than `EndsWith`, and that is why the empty case must
    /// return above: `name[^1]` is `name[-1]` on an empty string and THROWS, where `EndsWith` answers false. A
    /// renderer a script reaches (`typeof`, a message) must not be able to throw, so the guard is not decoration
    /// and must stay where it is.</summary>
    private static string BoxedElementSpelling(TypeInfo type)
    {
        var name = TypeInfo.Spelling(type);
        if (name.Length == 0) return "object";
        return type.IsNullable && !name[^1].Equals('?') ? name + "?" : name;
    }

    /// <summary>The type name a MESSAGE or the REPL reports, <see cref="GetFriendlyTypeNameOfValue"/> except for
    /// a value that NAMES a type: a <c>Type</c> value names the type it DENOTES (<c>int[]</c>,
    /// <c>List&lt;int&gt;</c>), because that is the name written at the call site, the one to fix, and, in the
    /// REPL's `.type` / `.vars -v`, the information the reader asked for. "Type" is the right answer where the
    /// CONCEPT is the question (indexing, slicing, a unary operator: those arms call
    /// <see cref="GetFriendlyTypeNameOfValue"/>, i.e. exactly what <c>typeof</c> says). A class or struct instance
    /// is named by its declaration, never by its storage class. Either way the result is a name a script can write
    /// back, never a CLR leaf name like <c>Int32[]</c>.</summary>
    internal static string ReportedTypeName(object? value)
        => value is Type denoted ? GetFriendlyTypeName(denoted) : GetFriendlyTypeNameOfValue(value);

    private static string BuildFriendlyTypeName(Type type)
    {
        // Script-specific types (dictionary lookup instead of chained ifs)
        if (KnownTypes.TryGetValue(type, out var known))
            return known;

        if (type == typeof(ScriptTuple))
            return "Tuple";

        // Registry reverse lookup, covers every script-exposed internal type with ONE entry
        if (ScriptNameOf(type) is { } registered)
            return registered;

        if (type == typeof(MemBuffer))
            return "MemBuffer";

        // Generic types
        if (type.IsGenericType)
        {
            var def = type.GetGenericTypeDefinition();
            var args = type.GetGenericArguments();

            if (def == typeof(List<>))
                return $"List<{GetFriendlyTypeName(args[0])}>";
            if (def == typeof(Dictionary<,>))
                return $"Dictionary<{GetFriendlyTypeName(args[0])}, {GetFriendlyTypeName(args[1])}>";
            if (def == typeof(ScriptUnboxedList<>))
                return $"List<{GetFriendlyTypeName(args[0])}>";
            if (def == typeof(ScriptUnboxedDictionary<,>))
                return $"Dictionary<{GetFriendlyTypeName(args[0])}, {GetFriendlyTypeName(args[1])}>";
            if (def == typeof(UnboxedScriptHashSet<>))
                return $"HashSet<{GetFriendlyTypeName(args[0])}>";
        }

        // Arrays
        if (type.IsArray)
        {
            var elemName = GetFriendlyTypeName(type.GetElementType()!);
            return type.GetArrayRank() == 1
                ? $"{elemName}[]"
                : $"{elemName}[{new string(',', type.GetArrayRank() - 1)}]";
        }

        // Nullable
        if (Nullable.GetUnderlyingType(type) is { } ut)
            return $"{GetFriendlyTypeName(ut)}?";

        // Script collection interfaces (boxed variants fall through KnownTypes in some paths)
        if (typeof(IScriptDictionary).IsAssignableFrom(type)) return "Dictionary";
        if (typeof(IScriptList).IsAssignableFrom(type)) return "List";
        if (typeof(IScriptHashSet).IsAssignableFrom(type)) return "HashSet";

        // A COMPILER-GENERATED TYPE IS NOT A NAME A SCRIPT CAN WRITE, and it is what a CLR async sequence hands a
        // script: `typeof(File.ReadLinesAsync(p))` answered "<IterateFileLinesAsync>d__126" (measured on the Debug
        // build and on the AOT publish), a name that resolves to nothing and reads as an engine leak in a message.
        // C#'s generators name a state machine after the method it came from, so the type it IMPLEMENTS is what the
        // caller can act on. The test is the NAME (C#'s own convention, and the same rule the tail below follows for
        // the arity) rather than an attribute, so nothing has to survive the trimmer for this to keep working.
        if (type.Name.Length > 0 && type.Name[0] == '<')
        {
            if (FriendlyInterfaceOf(type, typeof(IAsyncEnumerable<>)) is { } seq)
                return $"IAsyncEnumerable<{seq}>";
            if (FriendlyInterfaceOf(type, typeof(IAsyncEnumerator<>)) is { } en)
                return $"IAsyncEnumerator<{en}>";
            if (typeof(IAsyncDisposable).IsAssignableFrom(type))
                return "IAsyncDisposable";
            if (FriendlyInterfaceOf(type, typeof(IEnumerable<>)) is { } items)
                return $"IEnumerable<{items}>";
            if (typeof(IEnumerable).IsAssignableFrom(type))
                return "IEnumerable";
            if (typeof(IDisposable).IsAssignableFrom(type))
                return "IDisposable";
            return "object";
        }

        // The CLR's own leaf name, with the two things a SCRIPT cannot write removed: the generic ARITY
        // (the CLR spells a generator's type literally "Generator`1", and no identifier may contain a backtick)
        // and a declaring type's qualification ("Outer+Inner"). What is left is the name the registry must also
        // carry, because a name `typeof` reports has to be one a script can write back, `Generator` is
        // registered for exactly this reason, and `TryMatchKnownType` has the arm that keeps `x is Generator`
        // in agreement with it.
        var leaf = type.Name;
        var nested = leaf.LastIndexOf('+');
        if (nested >= 0) leaf = leaf[(nested + 1)..];
        var arity = leaf.IndexOf('`');
        return arity >= 0 ? leaf[..arity] : leaf;
    }

    /// <summary>The friendly name of a type's ONE argument for the given generic interface, or null when the type
    /// does not implement it. One pass, one argument, which is all an enumerable contract ever has.</summary>
    private static string? FriendlyInterfaceOf(Type type, Type genericInterface)
    {
        foreach (var iface in type.GetInterfaces())
        {
            if (!iface.IsGenericType) continue;
            if (iface.GetGenericTypeDefinition() != genericInterface) continue;
            var args = iface.GetGenericArguments();
            if (args.Length == 1) return BuildFriendlyTypeName(args[0]);
        }
        return null;
    }

    /// <summary>Central conversion dictionary: script-facing names (namespace-free) → the real
    /// CLR type. Used by `is`/`as`/cast checks (TryMatchKnownType), typeof (BuildFriendlyTypeName),
    /// and print. Script-facing names carry NO "Script" prefix, the CLR type's leaf name with the
    /// "Script" prefix stripped (ScriptTuple → "tuple", ScriptKeyValuePair → "KeyValuePair",
    /// ScriptLazyDeserializedList → "LazyDeserializedList") so is/typeof/print all align. Adding a
    /// new script-exposed internal type means adding ONE entry here, the rest is wired
    /// automatically.</summary>
    /// <summary>The registered spellings, exactly as written. The two registries above are keyed
    /// case-insensitively, a static name-to-Type index cannot follow a per-interpreter flag, so strict mode
    /// needs this second, case-SENSITIVE view to be C#-strict: <c>DateTime</c> and <c>int</c> resolve,
    /// <c>datetime</c> and <c>Int</c> do not, and only <c>.lax</c> relaxes it. Built lazily, beside the
    /// registries it mirrors.</summary>
    private static readonly Lazy<HashSet<string>> ExactTypeNames = new(BuildExactTypeNames);

    /// <summary>Built in a METHOD rather than in the initializer above, because a static field initializer runs
    /// in DECLARATION order and the registry below is declared later: reading it from the initializer made the
    /// nullable analysis report CS8602 ("possible null dereference"), since it cannot know a Lazy only runs on
    /// first USE. A method call has no such ordering question, it runs after every initializer.</summary>
    private static HashSet<string> BuildExactTypeNames()
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in TypeNameLookup.Keys) exact.Add(key);
        foreach (var key in ScriptTypeRegistry.Keys) exact.Add(key);
        return exact;
    }

    /// <summary>The ONE way a script-written type name becomes a CLR Type, gated by the interpreter case flag:
    /// strict (the default) demands a registered spelling, lax accepts any case. One lookup, so `is`, `as`,
    /// `typename` and the unknown-type guard cannot disagree about what a name means.</summary>
    internal static Type? ResolveTypeName(string name, bool lax)
    {
        if (!lax && !ExactTypeNames.Value.Contains(name)) return null;
        if (TypeNameLookup.TryGetValue(name, out var primitive)) return primitive;
        return ScriptTypeRegistry.TryGetValue(name, out var registered) ? registered : null;
    }

    /// <summary>A bare identifier that resolved to NOTHING may still name a TYPE, and then it is a type value:
    /// `ComNode.Make(...)`, `SqliteRow.Empty()`, a static member is written through the type itself, and the
    /// parser cannot know that an identifier is a type (only a type POSITION, as after `is`, can). The SAME
    /// lookup `is`/`typename` use, so a name cannot be a type in one place and undefined in another, and strict
    /// mode still demands the registered spelling.
    ///
    /// ONE rule rather than a name list: a Type global per alias (what the UI types do for Form/Control/Size)
    /// has to be remembered for every alias added later, and a name that is only aliased elsewhere then answers
    /// in a type position but not as an expression. Called from ONE place per engine, the tree's identifier last
    /// resort and the VM's global-load miss, so the two cannot disagree about a name.</summary>
    internal Type? ResolveTypeNameAsValue(string name) => ResolveTypeToClr(name, lax: !_caseSensitive);
    internal static readonly Dictionary<string, Type> ScriptTypeRegistry = new(StringComparer.OrdinalIgnoreCase)
    {
        // Script-facing names carry NO "Script" prefix, the CLR type's leaf name with the
        // "Script" prefix stripped (ScriptTuple → "Tuple", ScriptKeyValuePair → "KeyValuePair",
        // ScriptLazyDeserializedList → "LazyDeserializedList", ScriptErrorInfo → "ErrorInfo")
        // so is/typeof/print all align. "tuple" (lowercase) is a legacy alias for ScriptTuple.
        ["Tuple"] = typeof(ScriptTuple),
        ["WeakRef"] = typeof(WeakRef),
        ["Meta"] = typeof(VmBytecodeCodec.LibraryMeta),
        ["MemBuffer"] = typeof(MemBuffer),
        // The contract a `using` asks for, which is `obj is IDisposable` on the engine side: a script could not
        // write the question before, so `handle is IDisposable` answered False for a handle that WAS one (measured),
        // and a consumer had no way to know whether `using` would dispose the value it was handed.
        ["IDisposable"] = typeof(IDisposable),
        ["DynamicLibrary"] = typeof(DynamicLibrary),
        ["OrderedEnumerable"] = typeof(OrderedEnumerable),
        ["KeyValuePair"] = typeof(ScriptKeyValuePair),
        ["LazyDeserializedList"] = typeof(ScriptLazyDeserializedList),
        ["FileStreamReader"] = typeof(FileStreamReader),
        ["FileStreamWriter"] = typeof(FileStreamWriter),
        ["MessageInfo"] = typeof(MessageInfo),
        ["CipherInfo"] = typeof(Cipher.CipherInfo),
        ["CronExpression"] = typeof(CronExpression),
        // The generic event layer's two values (`Events.Create` answers a subject, `Subscribe` answers a subscription).
        // Registered rather than left to the leaf-name fallback because `is`/`as` read THIS table and nothing else in
        // their default arm: a name that is not here answers a silent False for a value that IS one, which is the
        // failure the DockStyle binding records.
        ["EventSubject"] = typeof(EventSubject),
        ["EventSubscription"] = typeof(EventSubscription),
        // A type DEFINITION used as a value, a class, record, struct or enum NAME. One name for all four, so
        // `is ClassInfo` resolves it (IsKnownType consults this registry). A class/record/enum already bound a
        // ClassInfo; a struct binds a bare TypeInfo HANDLE (VisitStructDeclStmt), which is the same CLR record
        // a parameter/return type DESCRIPTOR uses, see the KnownTypes note.
        ["ClassInfo"] = typeof(ClassInfo),
        // The names of values the LANGUAGE hands out, each of which used to be a bare CLR leaf name that
        // nothing resolved: `x is ReplayableEnumerable` threw "unknown or undefined" for a value whose own
        // ToString() already promised that name. Registered rather than renamed, the leaf name is the
        // script-facing name by convention (WeakRef, MemBuffer, OrderedEnumerable…) and was simply missing
        // from this table. The is/as predicate switch reads this registry in its default arm, so none of
        // these needs a switch arm of its own.
        ["ReplayableEnumerable"] = typeof(ReplayableEnumerable),
        ["LazyEnumerable"] = typeof(LazyEnumerable),
        ["Task"] = typeof(Task),
        ["ScheduledTask"] = typeof(ScheduledTask),
        ["SleepRequest"] = typeof(SleepRequest),
        ["EventWaitRequest"] = typeof(EventWaitRequest),
        ["HttpRequest"] = typeof(HttpRequest),
        ["ReadLineRequest"] = typeof(ReadLineRequest),
        // Every name the interpreter BINDS IN SCOPE to a CLR type is a name a script can write, and
        // IsKnownType already accepted it, but nothing MATCHED it, so `x is Rect` answered False while
        // typeof(x) said "Rect", and the false was silent: no error, just the wrong answer. One entry per
        // name is what makes the two agree, because the is/as predicate switch reads this table in its
        // default arm and `typename` now reads it too. The static-only names (Console, Math, Path…) are
        // here for the same reason even though no value is ever an instance: resolving them is what makes
        // `typename("Math")` work and keeps `is Math` a False answer rather than an unknown name.
        ["CancelEventArgs"] = typeof(CancelEventArgs),
        ["CancellationToken"] = typeof(CancellationToken),
        ["CancellationTokenSource"] = typeof(CancellationTokenSource),
        ["ClientStatus"] = typeof(ClientStatus),
        ["Console"] = typeof(Console),
        ["ConsoleColor"] = typeof(ConsoleColor),
        ["Control"] = typeof(Control),
        ["Convert"] = typeof(Convert),
        ["CoroutineStatus"] = typeof(CoroutineStatus),
        ["DateTimeOffset"] = typeof(DateTimeOffset),
        ["Directory"] = typeof(Directory),
        ["DockStyle"] = typeof(DockStyle),
        ["Menu"] = typeof(Menu),
        ["ThemeMode"] = typeof(ThemeMode),
        // The two row types Control.PropList() and Control.StyleFlags() project: a script reads their REPORTED
        // names back, so this registry must answer the same names (the DockStyle rule above).
        ["ControlProp"] = typeof(ControlProp),
        ["ColumnProp"] = typeof(ColumnProp),
        ["TreeItemProp"] = typeof(TreeItemProp),
        ["StyleFlag"] = typeof(StyleFlag),
        ["Encoding"] = typeof(Encoding),
        ["Enum"] = typeof(Enum),
        ["Environment"] = typeof(Environment),
        ["EventArgs"] = typeof(EventArgs),
        ["File"] = typeof(File),
        ["FileAttributes"] = typeof(FileAttributes),
        ["Form"] = typeof(Form),
        ["GC"] = typeof(GC),
        ["Guid"] = typeof(Guid),
        ["Math"] = typeof(Math),
        ["MdiChild"] = typeof(MdiChild),
        ["MdiForm"] = typeof(MdiForm),
        ["Path"] = typeof(Path),
        ["Rect"] = typeof(Rect),
        ["RegexOptions"] = typeof(RegexOptions),
        ["Size"] = typeof(Size),
        ["StringBuilder"] = typeof(StringBuilder),
        ["TaskStatus"] = typeof(TaskStatus),
        ["TimeZoneInfo"] = typeof(TimeZoneInfo),
        // The public types the interpreter's UI / WinApi / Sqlite / COM surface can hand a script. Same rule
        // as the block above: a value of one of these reaches a script and typeof() already reports its name,
        // so that name has to be writable, otherwise `x is KeyEventArgs` is a loud unknown while `typeof(x)`
        // says "KeyEventArgs". (Measured: 32 of them were unwritable; `1 is <Name>` threw.)
        ["Button"] = typeof(Button),
        ["ButtonBase"] = typeof(ButtonBase),
        ["CheckBox"] = typeof(CheckBox),
        ["ComNode"] = typeof(DispatchVtbl.ComNode),
        ["ComObject"] = typeof(DispatchVtbl.ComObject),
        ["ComScope"] = typeof(DispatchVtbl.ComScope),
        // Nested in ListBox (its ctor takes the owner), which is why the entry is qualified.
        ["ItemsCollection"] = typeof(ListBox.ItemsCollection),
        ["ComboBox"] = typeof(ComboBox),
        ["ControlCollection"] = typeof(ControlCollection),
        ["DateTimePicker"] = typeof(DateTimePicker),
        ["GenericControl"] = typeof(GenericControl),
        ["HotKey"] = typeof(HotKey),
        ["KeyCodes"] = typeof(KeyCodes),
        ["KeyEventArgs"] = typeof(KeyEventArgs),
        ["Label"] = typeof(Label),
        ["ListBox"] = typeof(ListBox),
        ["ListView"] = typeof(ListView),
        ["MouseButtons"] = typeof(MouseButtons),
        ["MouseEventArgs"] = typeof(MouseEventArgs),
        ["CommandEventArgs"] = typeof(CommandEventArgs),
        ["DropFilesEventArgs"] = typeof(DropFilesEventArgs),
        ["TimerEventArgs"] = typeof(TimerEventArgs),
        ["ResizeEventArgs"] = typeof(ResizeEventArgs),
        // Names the engine REPORTS must be names a script can WRITE (this table's own rule): the explicit map
        // lists what it calls a value, so each of these needs the bare spelling too.
        ["Class"] = typeof(ClassInstance),
        ["CombinedDelegate"] = typeof(CombinedDelegate),
        ["Coroutine"] = typeof(Coroutine),
        ["DelegateInfo"] = typeof(DelegateInfo),
        ["EventInfo"] = typeof(EventInfo),
        ["Function"] = typeof(ScriptFunction),
        ["Namespace"] = typeof(Namespace),
        ["Native"] = typeof(NativeFunctionAdapter),
        ["RefCell"] = typeof(RefCell),
        ["Struct"] = typeof(StructInstance),
        ["VoidMarker"] = typeof(VoidMarker),
        ["PaintEventArgs"] = typeof(PaintEventArgs),   // the bare spelling: a value reports the FIRST name registered for its type, and a name a script cannot write is the defect the comment above this table records
        ["NativeApp"] = typeof(NativeApp),
        ["OpenFileDialog"] = typeof(OpenFileDialog),
        ["InputBox"] = typeof(InputBox),
        ["RadioButton"] = typeof(RadioButton),
        ["Rebar"] = typeof(Rebar),
        ["SaveFileDialog"] = typeof(SaveFileDialog),
        ["StatusBar"] = typeof(StatusBar),
        ["SysLink"] = typeof(SysLink),
        ["TabControl"] = typeof(TabControl),
        ["TextBox"] = typeof(TextBox),
        ["TheTimer"] = typeof(TheTimer),
        ["ToolBar"] = typeof(ToolBar),
        ["TrackBar"] = typeof(TrackBar),
        ["TreeView"] = typeof(TreeView),
        ["CronType"] = typeof(CronType),
        ["ErrorInfo"] = typeof(ScriptErrorInfo),
        ["SqliteIterator"] = typeof(SqliteIterator),
        ["SqliteMappingIterator"] = typeof(SqliteMappingIterator),
        ["SqliteRow"] = typeof(SqliteRow),
        ["NativeFunction"] = typeof(NativeFunction),
        // The generic type a `yield` function's call RETURNS (Generator<T> in CoroutineTypes.cs). Registered as
        // the OPEN definition because `typeof` reports its name, a generator answers "Generator" after
        // BuildFriendlyTypeName strips the CLR arity, and a reported name is one a script must be able to write:
        // without this entry `g is Generator` threw "Type 'Generator' is unknown or undefined" (measured).
        // An open generic is never `IsInstanceOfType`, which is why TryMatchKnownType needs its own arm for it.
        ["Generator"] = typeof(Generator<>),
    };

    /// <summary>Reverse lookup: CLR type → preferred script-facing name (the non-"Script" alias
    /// wins, so typeof keeps friendly names like "Tuple" / "MemBuffer" / "DynamicLibrary").
    /// Returns null when the type is not in the registry, never falls back recursively.</summary>
    internal static string? ScriptNameOf(Type type)
    {
        string? fallback = null;
        foreach (var (name, t) in ScriptTypeRegistry)
        {
            if (t != type) continue;
            if (name.StartsWith("Script", StringComparison.Ordinal)) { fallback ??= name; continue; }
            return name;
        }
        return fallback;
    }

    private static readonly Dictionary<Type, string> KnownTypes = new()
    {
        [typeof(RefCell)] = "RefCell",
        [typeof(ScriptFunction)] = "Function",
        // A type DEFINITION reports ONE name: a class, record and enum bind a ClassInfo, and a struct binds
        // a bare TypeInfo handle. Both answer "ClassInfo", so the name is uniform, but note that the TypeInfo
        // record is ALSO how a parameter/return type descriptor is stored, and those answer the same name.
        [typeof(ClassInfo)] = "ClassInfo",
        [typeof(TypeInfo)] = "ClassInfo",
        // The runtime STORAGE names: what the JSON writer tags a value with and print shows. typeof does not
        // read them for these two, a class instance reports its declared class through
        // GetFriendlyTypeNameOfValue, because "Class" resolves to nothing.
        [typeof(ClassInstance)] = "Class",
        [typeof(StructInstance)] = "Struct",
        [typeof(Coroutine)] = "Coroutine",
        [typeof(EventInfo)] = "EventInfo",
        [typeof(CombinedDelegate)] = "CombinedDelegate",
        [typeof(DelegateInfo)] = "DelegateInfo",
        [typeof(Namespace)] = "Namespace",
        [typeof(NativeFunctionAdapter)] = "Native",
        [typeof(ScriptResult)] = "Result",
        [typeof(VoidMarker)] = "VoidMarker",
        [typeof(ScriptBoxedList)] = "List",
        [typeof(ScriptBoxedDictionary)] = "Dictionary",
        [typeof(BoxedScriptHashSet)] = "HashSet",
        // Primitives
        [typeof(double)] = "double",
        [typeof(bool)] = "bool",
        [typeof(string)] = "string",
        [typeof(char)] = "char",
        [typeof(byte)] = "byte",
        [typeof(sbyte)] = "sbyte",
        [typeof(short)] = "short",
        [typeof(ushort)] = "ushort",
        [typeof(int)] = "int",
        [typeof(uint)] = "uint",
        [typeof(long)] = "long",
        [typeof(ulong)] = "ulong",
        [typeof(float)] = "float",
        [typeof(decimal)] = "decimal",
        [typeof(object)] = "object",
        [typeof(void)] = "void",
        [typeof(DateTime)] = "DateTime",
        [typeof(DateTimeOffset)] = "DateTimeOffset",
        [typeof(TimeSpan)] = "TimeSpan",
        [typeof(CronExpression)] = "CronExpression",
        [typeof(CronType)] = "CronType",
    };

    /// <summary>Maps short type names (from __type in JSON) to CLR types for round-trip deserialization.</summary>
    internal static readonly Dictionary<string, Type> JsonTypeMap = new()
    {
        // UI types
        ["Interpreter.CancelEventArgs"] = typeof(CancelEventArgs),
        ["Interpreter.ControlCollection"] = typeof(ControlCollection),
        ["Interpreter.MouseEventArgs"] = typeof(MouseEventArgs),
        ["Interpreter.CommandEventArgs"] = typeof(CommandEventArgs),
        ["Interpreter.DropFilesEventArgs"] = typeof(DropFilesEventArgs),
        ["Interpreter.TimerEventArgs"] = typeof(TimerEventArgs),
        ["Interpreter.ResizeEventArgs"] = typeof(ResizeEventArgs),
        ["Interpreter.KeyEventArgs"] = typeof(KeyEventArgs),
        ["Interpreter.PaintEventArgs"] = typeof(PaintEventArgs),
        ["Interpreter.MessageInfo"] = typeof(MessageInfo),
        // Core types
        ["ErrorInfo"] = typeof(ScriptErrorInfo),
        // Add more types here as they are registered for JSON round-trip
    };
}
