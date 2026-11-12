namespace ScriptLangCore;

// Source: extracted from Interpreter.cs, Type helpers
internal partial class Interpreter
{

    /// <summary>Extracts a human-readable message from a .NET exception, avoiding NativeAOT resource-key fallback.</summary>
    /// <summary>
    /// The single exception→message map, with a tripwire in front of it. Callers keep using this name; the
    /// mapping body is FriendlyExceptionMessageCore below, so this file still owns the ONLY such mapping.
    /// </summary>
    internal static string FriendlyExceptionMessage(Exception ex) =>
        WarnIfRawPlatformMessage(FriendlyExceptionMessageCore(ex));

    private static string FriendlyExceptionMessageCore(Exception ex) => ex switch
    {
        // The engine's own exception: its Message is already written for script authors (it is the text the
        // engine composes for errors like the recursion guard), so pass it through. Without this case it fell
        // into the type-name fallback below and scripts saw "[MoveNext]: ScriptException: '<message>'"
        // (reported 2026-09-11), the raw type name wrapped around an already-friendly message.
        ScriptException se => se.Message,
        FileNotFoundException fnf => ComposeDetail("File not found", fnf.FileName),
        DirectoryNotFoundException dnf => ComposeDetail("Directory not found", ExtractPath(dnf.Message)),
        UnauthorizedAccessException ua => ComposeDetail("Access denied", ua.Message),
        PathTooLongException ptl => ComposeDetail("Path too long", ptl.Message),
        // EndOfStreamException derives from IOException, so it must come first, and it must have an arm at all:
        // the IOException arm below forwards the CLR detail, and on the AOT build (InvariantGlobalization) a
        // truncated input's detail is the RESOURCE KEY "IO_EOF_ReadBeyondEOF" rather than a sentence, which is
        // exactly the kind of message this map exists to keep away from a script. A bytecode blob is script
        // input, so this is also where a truncated bundle's refusal gets its wording.
        EndOfStreamException => "the input ended before the value it promised was read",
        IOException ioe => ComposeDetail("I/O error", ioe.Message),
        // An HTTP failure's own text is the CLR's, which is a RESOURCE KEY under InvariantGlobalization and the
        // MACHINE'S LANGUAGE on a localized box (measured on this build: a refused connection answered the French
        // "Aucune connexion n'a pu etre etablie ..."). So the arm names what a script can act on, the STATUS when there
        // is one and otherwise that the server was not reachable, and `Http.LastErrorKind` carries the same distinction
        // for a caller that wants to switch on it.
        HttpRequestException hx => hx.StatusCode is { } status
            ? ComposeDetail("the server refused the request", $"it answered {(int)status} ({status})")
            : ComposeDetail("the request could not be completed", "the server could not be reached"),
        // ObjectDisposedException derives from InvalidOperationException, so it must come first.
        ObjectDisposedException ode => ComposeDetail("cannot access a disposed object", ode.ObjectName),
        // A CLR `InvalidOperationException` message is often a full sentence and just as often a RESOURCE KEY: an
        // invariant-globalization build reports `NoAssociatedProcess` where the resource text reads "No process is
        // associated with this object". Echoing that key is exactly what this map exists to prevent, so a message
        // with no space in it (a key, never a sentence) is replaced by what the exception means.
        InvalidOperationException ioe => ComposeDetail("Invalid operation",
            ioe.Message is { Length: > 0 } m && m.Contains(' ') ? m
            : ioe.Message == "NoAssociatedProcess" ? "there is no process associated with this object"
            : "the operation is not valid for the object's current state"),
        NotImplementedException nie => ComposeDetail("Not implemented", nie.Message),
        FormatException fe => ComposeDetail("Invalid format", fe.Message),
        // A JSON payload the reader rejected. Its Message is a resource key plus a byte offset
        // ("'o' is an invalid start of a property name. Expected a '\"'. LineNumber: 0 | BytePositionInLine: 1."),
        // which names nothing a script can act on and would be localized, so it is never forwarded: the position
        // is composed OUR way, from the fields the reader actually exposes.
        System.Text.Json.JsonException je => ComposeDetail("the payload is not valid JSON",
            $"line {je.LineNumber}, byte {je.BytePositionInLine}"),
        // Moved here from the two per-site duplicates in Interpreter.cs, with their exact wording, so the
        // single map is a strict superset of every copy it replaces.
        DivideByZeroException => "Attempted to divide by zero.",
        OverflowException => "Arithmetic overflow.",
        // InvariantGlobalization (set for the AOT build) leaves Message as the resource KEY
        // ("Arg_OutOfMemoryException"), so never forward it: say what happened instead.
        OutOfMemoryException => "Out of memory: the runtime could not honour an allocation",
        // Native-loading failures. DllNotFoundException.Message is the RESOURCE KEY plus a localized Win32
        // fragment ("DllNotFound_Windows, sqlite3, Le module spécifié est introuvable."), and the bridge in
        // SqliteCursor.cs throws it directly, so say what is wrong and what to do instead.
        DllNotFoundException => "native library not found: it must sit beside the executable "
                              + "(loaded by name), or be loaded with LoadLibrary(\"<full path>\")",
        BadImageFormatException => "native library cannot be loaded: wrong architecture or corrupt "
                                 + "(an x64 native is required beside the AOT executable)",
        EntryPointNotFoundException ep => ComposeDetail("native library is missing an expected entry point", ep.Message),
        // ArgumentOutOfRangeException derives from ArgumentException, so it must come first.
        ArgumentOutOfRangeException aor => $"Argument '{aor.ParamName}' out of range: actual {aor.ActualValue}",
        // ArgumentException and its relatives. InvariantGlobalization leaves the resource KEY in Message
        // ("Argument_AddingDuplicateWithKey, key", "Arg_PathEmpty ..."), which the message-quality tripwire
        // caught reaching a script through Dictionary.Add on a duplicate key and File.WriteAllText(""). Say
        // what the argument was when the CLR names it; ComposeDetail drops any key-shaped detail.
        ArgumentException ae when ae.ParamName is { Length: > 0 } => $"invalid argument '{ae.ParamName}'",
        ArgumentException ae => ComposeDetail("invalid argument", ae.Message),
        KeyNotFoundException => "the requested key was not found in the collection",
        // An index a corrupt bytecode table names can point outside the table it means: without an arm that reads
        // as a CLR type name plus a CLR sentence, and a blob is script input.
        IndexOutOfRangeException => "an index is outside the collection it names",
        InvalidCastException ice => ComposeDetail("invalid cast", ice.Message),
        NotSupportedException nse => ComposeDetail("operation not supported", nse.Message),
        System.Security.SecurityException se => ComposeDetail("a security error occurred", se.Message),
        // Cryptography. InvariantGlobalization leaves Message as the resource KEY
        // ("Cryptography_AuthTagMismatch"), which the message-quality tripwire caught reaching a script through
        // Cipher.Decrypt with a wrong key: the author saw a key, not a reason. Matched on the message as well as
        // the type, because the key can arrive on a plain CryptographicException too. Never echo it.
        System.Security.Cryptography.CryptographicException cex when cex.Message.Contains("AuthTagMismatch") =>
            "cryptographic failure: the authentication tag does not match, the key is wrong, or the data was tampered with",
        System.Security.Cryptography.CryptographicException =>
            "cryptographic failure: the operation could not be completed (wrong key, or corrupted or truncated data)",
        // COM dispatch. Without this arm a script saw the raw CLR text the wrapper throws, "COMException:
        // 'Invoke failed (HRESULT: 0x80020003)'" on any mis-called member: an exception type name plus a hex
        // code, and nothing saying which member. The HRESULT is the only part that carries meaning, so it picks
        // the sentence; DISP_E_EXCEPTION keeps the COM server's own description, the one failure whose words are
        // authored by the object rather than by the CLR.
        System.Runtime.InteropServices.COMException com when com.HResult == unchecked((int)0x80020009) =>
            ComposeDetail($"the COM object raised an error (HRESULT 0x{com.HResult:X8})", com.Message),
        System.Runtime.InteropServices.COMException com => com.HResult switch
        {
            unchecked((int)0x80020003) => "the COM object has no such member",
            unchecked((int)0x80020006) => "the COM object does not know that name",
            unchecked((int)0x80020005) => "the COM object rejected the argument: wrong type",
            unchecked((int)0x8002000E) => "the COM object rejected the call: wrong number of arguments",
            unchecked((int)0x8002000B) => "the COM object rejected the index",
            // The COM server was BUSY and refused the call. Measured while driving Excel: an argument-less
            // Worksheet.Copy() returned this, and every later call on that application was refused the same
            // way until it was recreated, so the sentence says the object is alive but would not answer.
            unchecked((int)0x80010001) => "the COM object refused the call: it was busy (the application may be showing a dialog, or another call is in progress)",
            unchecked((int)0x8001010A) => "the COM object asked to be called later: it is busy right now",
            unchecked((int)0x800706BA) or unchecked((int)0x800706BE) =>
                "the COM object's server is no longer running (the application was closed or crashed)",
            unchecked((int)0x80010108) or unchecked((int)0x800401FD) =>
                "the COM object is disconnected: its server is gone (the object was released, or the application quit)",
            unchecked((int)0x800401E3) or unchecked((int)0x80040154) =>
                "no COM class or object matches that name, is the application installed?",
            // The code AND the system's text for it: with InvariantGlobalization a COMException.Message can be
            // the resource KEY, but on a normal machine it is the HRESULT's own description ("The object invoked
            // has disconnected from its clients."), which is what a reader searches for. The maintainer's call,
            // 2026-10-06, after a failing COM call reported the bare sentence with no code at all.
            _ => ComposeDetail($"a COM call failed (HRESULT 0x{com.HResult:X8})", com.Message)
        },
        // The fallback must never forward a raw Message: with InvariantGlobalization it is the resource KEY.
        // The exception TYPE NAME is always readable, so derive the text from it and sanitise the detail.
        // A COMPOSITE's failures (Task.WhenAll): each inner goes through the SAME map, so a script reads one line per
        // failure in the engine's own words instead of the CLR's "One or more errors occurred".
        AggregateException agg => ComposeDetail("Multiple failures", string.Join("; ", agg.Flatten().InnerExceptions.Select(i => FriendlyExceptionMessage(i)))),
        _ => ComposeDetail(ex.GetType().Name, ex.Message)
    };


    /// <summary>
    /// Composes a readable message from a prefix and an optional detail. The detail is ECHOED only when it is
    /// safe: a resource-key shaped detail (see <see cref="LooksLikeResourceKey"/>) is dropped, so the caller
    /// gets the prefix alone. Every arm that used to interpolate a raw exception Message goes through here, so
    /// no arm can emit a key, not even one embedded after a readable prefix.
    /// </summary>
    private static string ComposeDetail(string prefix, string? detail)
    {
        var safe = SafeDetail(detail);
        return safe is null ? prefix : $"{prefix}: '{safe}'";
    }

    /// <summary>
    /// Returns the detail when it is safe to show, or null when it must be dropped. A CLR/Win32 resource key
    /// under InvariantGlobalization is not readable text, so it is never used as a detail.
    /// </summary>
    private static string? SafeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return null;
        if (LooksLikeResourceKey(detail)) return null;
        return detail.Trim();
    }

    /// <summary>
    /// Resource-key shaped: the CLR's own "Arg_" prefix, or a Word_CapitalizedWord token (an '_' followed by
    /// an uppercase letter followed by a lowercase one) anywhere in the text. This is the same shape the
    /// message-quality tripwire detects, minus its deliberate first-word restriction: unlike the tripwire
    /// (which must not fire on paths or CONSTANTS), here a match means the whole detail is dropped, the safe
    /// response for a string that is either a key or a key embedded in otherwise-readable text.
    /// </summary>
    private static bool LooksLikeResourceKey(string text)
    {
        if (text.StartsWith("Arg_", StringComparison.Ordinal)) return true;
        for (int i = 0; i + 2 < text.Length; i++)
        {
            if (text[i] != '_') continue;
            if (!char.IsUpper(text[i + 1]) || !char.IsLower(text[i + 2])) continue;
            if (i > 0 && (char.IsUpper(text[i - 1]) || text[i - 1] == '_')) continue;
            return true;
        }
        return false;
    }


    private static string ExtractPath(string msg)
    {
        var comma = msg.IndexOf(',');
        return comma < 0 ? msg : msg.AsSpan(comma + 1).TrimStart().ToString();
    }


    /// <summary>True when a STATIC name set knows this name under the interpreter's case rule: the name
    /// spelled as the engine spells it when strict (the default), any case in `.lax`. These sets are static,
    /// shared across interpreters, so they cannot read the flag, and a plain membership test would answer any
    /// case in BOTH modes. That is how `is consolecolor` stayed legal while `typename("datetimeoffset")` was
    /// already refused: one rule for a name, two answers depending on which lookup got there first.</summary>
    private bool SetKnows(IEnumerable<string> set, Func<string, bool> contains, string name)
        => contains(name) && (!_caseSensitive || set.Contains(name, StringComparer.Ordinal));

    /// <summary>The ONE way a script-written TYPE NAME becomes a CLR Type, for every consumer: `typename`, a
    /// generic type argument, the bare-identifier fallback, and the "is this name known?" gate. Steps: the gated
    /// registry (the script-facing aliases) → a Type held in the global scope (Form, Control, …) → the CLR's own
    /// lookup over the scanned namespaces. That last step is what `x is DateTime` already had and
    /// `typename("DateTime")` lacked, the same name answered differently depending on which entry point asked,
    /// which is the one thing a name lookup must not do. A name a script DECLARES (class, struct) still has no
    /// CLR type to return, so it stays unknown here while `is`/`as` accept it by name.
    ///
    /// The registry step honours the interpreter's case flag (strict demands the registered spelling); the CLR
    /// lookup is ordinal by nature, so in `.lax` the extra case-insensitivity does not extend to a CLR-only name
    /// such as DateTime, the flag cannot loosen a lookup the runtime itself makes case-sensitively.</summary>
    internal Type? ResolveTypeToClr(string name, bool lax)
    {
        // A TYPE PARAMETER bound by the generic instantiation that is RUNNING resolves to its binding, and this is
        // the ONE place that answers, because every consumer asks here: `is`, a cast, `typeof`, `new`, and
        // IsKnownType, which delegates to this chain. That is deliberate: the alternative, rewriting the body at
        // instantiation, only knows three node shapes and measured, `o is IEnumerable<T>` answered False from a
        // return, a print, an if, a `&&`, a var initializer, a lambda and a foreach body (see
        // FuncDecl.TypeParamBindings). A name no instantiation bound falls through untouched.
        if (ActiveTypeBindings is { } active && active.TryGetValue(name, out var bound)
            && !bound.IsGenericParameter && !string.Equals(bound.TypeName, name, Comparison))
        {
            if (bound.TypeName is { Length: > 0 } boundName && ResolveTypeName(boundName, lax) is { } boundRegistered)
                return boundRegistered;
            if (TypeTokenHelper.GetSystemType(bound) is { } boundClr) return boundClr;
        }
        if (ResolveTypeName(name, lax) is { } registered) return registered;
        if (CurrentFrame.Scopes.TryGet(name, out var scopeVal))
        {
            if (scopeVal is Type scopeType) return scopeType;
            // A name the engine BINDS TO A NAMESPACE is that namespace, and it must not ALSO be answered as a CLR
            // type: a loaded framework leaf carrying the same name would turn `x is <that name>` into a SILENT
            // FALSE, the outcome the DockStyle binding already records as the one to avoid (`typename` resolved
            // the name, nothing refused, and the `is`/`as` arm answered wrong). THE COLLISION IS REAL AND MEASURED:
            // `Process` is an engine NAMESPACE (`Registration/Process.cs`, so `Process.Start(...)` is the engine's)
            // AND a `System.Diagnostics.Process` leaf, and without this guard `typename("Process")` answered a
            // type while the name a script writes for spawning is the namespace. `File`, `Directory` and `Path` are
            // namespaces too, but the script registry answers those before this step, which is why the guard is
            // stated for every namespace rather than for the one that happens to collide today.
            if (scopeVal is Namespace) return null;
        }
        // EVERY assembly the process has loaded, which is what makes a framework type reachable at all.
        // `Type.GetType(name)` without an assembly qualifier searches only the CALLING assembly and the core
        // library, so the fixed namespace list this step used to be could never reach a type the framework keeps
        // in its own assembly, however the list was spelled. Measured before this step, identically on the JIT
        // build and on the AOT publish: `Uri`, `HttpClient`, `IPAddress`, `Stopwatch`, `Color`, `ZipFile`,
        // `SHA256`, `BigInteger`, `CultureInfo`, `Process` and `FileInfo` all missed, which is why an
        // `http.Use` middleware could not reach `Uri.UnescapeDataString` and had to hand-roll (and get wrong) its
        // own percent-decoder.
        if (FindLoadedClrType(name) is { } loaded) return loaded;
        return null;
    }

    /// <summary>The process-wide index behind <see cref="ResolveTypeToClr"/>'s assembly step: every EXPORTED type
    /// of every assembly loaded so far, by full name and by LEAF name.
    ///
    /// A leaf name is what a script writes (the engine has no dotted type spelling), and several types can share
    /// one leaf, so the FIRST assembly to export that leaf wins, in the process's own load order. That is the
    /// same "first match wins" the namespace list it replaces already had, it keeps the answer deterministic for
    /// a given run rather than dependent on hashing, and the registries and a scope binding are both consulted
    /// BEFORE this, so a script-visible spelling always outranks a CLR leaf.
    ///
    /// Assemblies are indexed as they ARRIVE rather than once: a script can `LoadAssembly` one later, and a
    /// one-shot snapshot would then answer for a type the process has held all along and not for that one. The
    /// gate is held for the whole lookup, which is deliberately coarse: a resolver a script reaches through a
    /// typo is not a hot path, and a lock-free read would buy nothing against the lookup it protects.</summary>
    private static readonly Lock ClrTypeIndexGate = new();
    private static readonly Dictionary<string, Type> ClrTypeIndex = new(StringComparer.Ordinal);
    /// <summary>The ARITY-STRIPPED spellings ("IAsyncEnumerable`1" answering "IAsyncEnumerable"), in a table of
    /// their OWN and consulted only when the exact map MISSES. That separation is what makes the answer
    /// DETERMINISTIC, which `TryAdd` alone cannot be: a first-wins `TryAdd` means "whichever the enumeration
    /// reaches first", and `GetExportedTypes()` order decides which of `System.Collections.Generic.KeyValuePair`
    /// (a real, NON-generic static class) and `KeyValuePair`2[TKey,TValue]` claims the leaf "KeyValuePair".
    /// Measured: the JIT build resolved that leaf to the non-generic type and the AOT publish to the OPEN
    /// generic, so `new KeyValuePair("a", 1)` reached the tree's own refusal on one and
    /// `Activator.CreateInstance` on an open definition on the other (`_dup360` line 47, `Acc_CreateGenericEx`).
    /// An EXACT name now always wins, on every platform, and the stripped table stays the fallback the
    /// `IAsyncEnumerable` fix needs.</summary>
    private static readonly Dictionary<string, Type> ClrTypeIndexByAritylessName = new(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> ClrTypeIndexAssemblies = [];

    private static Type? FindLoadedClrType(string name)
    {
        if (name.Length == 0) return null;
        // The engine's own PROMISES first: a name `AotCompilerHint` materialised as a `typeof` at setup is a name
        // a script is told it can write, so it has to answer on the shipped build as well, whatever a narrower
        // assembly set offers the scan below.
        if (_promisedClrTypes is { } promised && promised.TryGetValue(name, out var promisedType))
            return promisedType;
        lock (ClrTypeIndexGate)
        {
            // THE ENGINE'S OWN ASSEMBLY WINS A LEAF, because it is indexed before the process's load order decides.
            // Measured reason: `HttpContext` is this engine's request context (`Core/Middleware`) AND
            // `Microsoft.AspNetCore.Http.HttpContext`, and "the first loaded assembly to export the leaf" would have
            // handed a host that loads ASP.NET Core the framework's type for a name this engine documents and a
            // script writes. The registry and a scope binding are still consulted before this step, so a
            // script-visible spelling outranks both.
            IndexClrTypes(typeof(Interpreter).Assembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                IndexClrTypes(asm);
            return ClrTypeIndex.TryGetValue(name, out var found) ? found
                 : ClrTypeIndexByAritylessName.TryGetValue(name, out var byArityless) ? byArityless
                 : null;
        }
    }

    /// <summary>Indexes one assembly's exported types, once. A dynamic or reflection-only assembly REFUSES
    /// `GetExportedTypes` (NotSupportedException, a FileNotFoundException for one of its own references), and one bad
    /// assembly must not take the whole lookup down with it: skipping it leaves that assembly's types unindexed,
    /// which is what a miss already means. A NESTED type is indexed by full name only, never by its bare leaf, which
    /// is the most collision-prone kind of all (`Interpreter.Process` against `System.Diagnostics.Process`).</summary>
    private static void IndexClrTypes(Assembly asm)
    {
        if (!ClrTypeIndexAssemblies.Add(asm)) return;
        Type[] exported;
        try { exported = asm.GetExportedTypes(); }
        catch { return; }
        foreach (var t in exported)
        {
            if (t.FullName is { Length: > 0 } full)
            {
                ClrTypeIndex.TryAdd(full, t);
                // A GENERIC type's CLR name carries its ARITY ("IAsyncEnumerable`1"), which no script can write, so
                // the stripped spelling is indexed too: measured, `x is IAsyncEnumerable<string>` could not RESOLVE
                // the name at all ("Type 'IAsyncEnumerable' is unknown or undefined") while the value WAS one, and
                // a generic argument can only be matched once the name resolves. It goes in the ARITYLESS table,
                // which the lookup consults only after the exact one MISSES, so it can never take a leaf away from
                // a type whose real name has no arity (see that table's doc for the measured collision).
                if (full.IndexOf('`') is var fullCut and > 0) ClrTypeIndexByAritylessName.TryAdd(full[..fullCut], t);
            }
            if (!t.IsNested)
            {
                ClrTypeIndex.TryAdd(t.Name, t);
                if (t.Name.IndexOf('`') is var leafCut and > 0) ClrTypeIndexByAritylessName.TryAdd(t.Name[..leafCut], t);
            }
        }
    }

    /// <summary>The CLR types the engine PROMISES a script can name by its leaf, contributed once by
    /// `AotCompilerHint`. Two things make this a table rather than just an assembly touch: the promise has to
    /// hold on the build whose assembly set is narrower than the development one (measured: with only the
    /// assembly scan, `System.Diagnostics.Process` resolved on the JIT build and was STILL a miss on the AOT
    /// publish), and routing the list through a method is what stops an optimizer from deleting the touches (a
    /// local array used only for its `Length` is removed, and then nothing references the type at all).</summary>
    private static Dictionary<string, Type>? _promisedClrTypes;

    internal static void RegisterPromisedClrTypes(IReadOnlyList<Type> types)
    {
        var map = new Dictionary<string, Type>(types.Count, StringComparer.Ordinal);
        // The LEAF is what a script writes, and a nested type (whose leaf would be the most collision-prone kind)
        // is deliberately not indexed by it, exactly as the assembly scan does not.
        foreach (var t in types)
            if (!t.IsNested) map.TryAdd(t.Name, t);
        // Assigned in ONE write after the map is complete, so a reader sees either nothing or the whole table.
        _promisedClrTypes = map;
    }

    private bool IsKnownType(string name)
    {
        // ScriptFunction is the internal CLR name for script callables, hidden from the script
        // type surface. `is ScriptFunction` must throw 'Type ScriptFunction is unknown or
        // undefined'; scripts write `Function` instead (TryMatchKnownType / IsFunctionType).
        if (name.Equals("ScriptFunction", StringComparison.OrdinalIgnoreCase)) return false;
        // ScriptErrorInfo is the CLR name of a script error, hidden for the same reason as ScriptFunction above:
        // scripts write `ErrorInfo`. It used to be in the builtin-name list and nowhere else, so the name was
        // "known", no arm matched it, and `x is ScriptErrorInfo` answered a silent False instead of reporting.
        if (name.Equals("ScriptErrorInfo", StringComparison.OrdinalIgnoreCase)) return false;
        // Predicate type, the unified iterable contract (arrays, lists, tuples, dicts,
        // HashSets, LINQ results, strings, generators). Resolves via IsTypeCheck's predicate.
        if (name.Equals("Iterable", StringComparison.Ordinal)) return true;
        if (name.Equals("RandomAccess", StringComparison.Ordinal)) return true;
        // Function, the first-class callable contract (script functions/lambdas, bound methods,
        // combined delegates, native functions). Resolves via IsTypeCheck's predicate.
        if (name.Equals("Function", StringComparison.Ordinal)) return true;
        // Class / Struct, the contract type names for class/struct instances. is/as accept them (they
        // arrive from ParseType as keyword-token types, since `class`/`struct` are case-insensitive
        // declaration keywords). typeof reports the DECLARED name instead, "Animal", "Pt", because a
        // contract name is a category, not a type: nothing resolves it (see GetFriendlyTypeNameOfValue).
        if (name.Equals("Class", StringComparison.Ordinal)) return true;
        if (name.Equals("Struct", StringComparison.Ordinal)) return true;
        // Handle generic type names like Func<String,String>, strip <> and check base name
        if (name.IndexOf('<') >= 0)
        {
            var baseName = name[..name.IndexOf('<')].Trim();
            if (IsKnownType(baseName))
                return true;
        }
        if (Classes.ContainsKey(name) || Structs.ContainsKey(name) || DelegateInfos.ContainsKey(name)
            || _interfaces.ContainsKey(name) || _genericInterfaceTemplates.ContainsKey(name))
            return true;
        // Unqualified library type name (imported via `import ... as Z`), resolves `is`/`typeof`
        // checks against classes/structs registered from a bytecode library under their qualified names.
        if (SetKnows(_libraryTypeAliases.Keys, _libraryTypeAliases.ContainsKey, name)) return true;
        // Every CLR-backed name resolves through the ONE lookup, the registry, a Type in the global scope, and
        // the CLR's own namespace scan. It used to be spelled out HERE as well, which is exactly how this gate and
        // `typename` came to answer differently for `DateTime`: two copies of a lookup drift, one rule cannot.
        if (ResolveTypeToClr(name, lax: !_caseSensitive) != null) return true;
        // Check _builtinNames, covers Action`1, Func`2, Predicate`1, etc.
        if (SetKnows(_builtinNames, _builtinNames.Contains, name))
            return true;
        // Check script-known type names that aren't resolved via Type.GetType
        if (SetKnows(_reservedNames, _reservedNames.Contains, name))
            return true;
        return false;
    }

    // ── message-quality tripwire ───────────────────────────────────────────────────────────────────
    private static readonly Lock MessageQualityGate = new();
    private static readonly HashSet<string> ReportedRawMessages = new();

    /// <summary>
    /// A message about to become script-visible must not carry a CLR/Win32 resource key
    /// ("DllNotFound_Windows, sqlite3, Le module spécifié est introuvable.", "Arg_OutOfMemoryException"): a
    /// script author cannot read that, and messages of exactly that shape reached users four separate times.
    /// If this fires, something forwarded a raw message, so a warning is the right response. Emitted once per
    /// distinct message, under a hard cap, so it can never spam a run.
    /// </summary>
    internal static string WarnIfRawPlatformMessage(string message)
    {
        if (LooksLikeRawPlatformMessage(message)) WarnMessageQualityOnce(message);
        return message;
    }

    /// <summary>
    /// Deliberately narrow, because a warning that cries wolf gets ignored (the lesson from the duplicate-map
    /// check): the key must be the FIRST word, and constants (SLC_VM_MAXFRAMES) or paths (C:\my_Dir) must not
    /// trip it.
    /// </summary>
    private static bool LooksLikeRawPlatformMessage(string message)
    {
        if (message.Length == 0) return false;
        if (message.StartsWith("Arg_", StringComparison.Ordinal)) return true;   // the CLR's own key prefix
        int start = 0;
        if (message.StartsWith("[", StringComparison.Ordinal))
        {
            int br = message.IndexOf("]: ", StringComparison.Ordinal);            // after a "[prefix]: " we added
            if (br >= 0) start = br + 3;
        }
        int sp = message.IndexOf(' ', start);
        int limit = sp < 0 ? message.Length : sp;                                // only within the first word
        // Word_CapitalizedWord: every '_' followed by an uppercase letter that is itself followed by a
        // lowercase one, and not part of a longer CONSTANT_LIKE run.
        for (int i = start; i + 2 < limit; i++)
        {
            if (message[i] != '_') continue;
            if (!char.IsUpper(message[i + 1]) || !char.IsLower(message[i + 2])) continue;
            if (i > 0 && (char.IsUpper(message[i - 1]) || message[i - 1] == '_')) continue;
            return true;
        }
        return false;
    }

    private static void WarnMessageQualityOnce(string message)
    {
        lock (MessageQualityGate)
        {
            if (ReportedRawMessages.Count >= 200 || !ReportedRawMessages.Add(message)) return;
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine("[message-quality] a script-visible error message looks like a raw CLR/Win32 message:");
        Console.Error.WriteLine($"    {message}");
        Console.Error.WriteLine("  That is a resource key plus localized text, unreadable for script authors.");
        Console.Error.WriteLine("  Fix: route it through FriendlyExceptionMessage, or extend that map (see AGENTS.md).");
    }
}
