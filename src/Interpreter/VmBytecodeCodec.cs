namespace ScriptLangCore;
/// <summary>Binary serializer for VM bytecode, allows exporting a compiled function and re-importing
/// it into a fresh interpreter. Consts are stored by VALUE (literals + TypeInfo value data, no live
/// interpreter objects); nested lambdas are stored as shells (params/return/line-col + their own
/// bytecode, NO AST bodies) so the loader never needs the parser.</summary>
internal static class VmBytecodeCodec
{
    // A PROGRAM bundle and a LIBRARY bundle are told apart by this first int, which is also what lets a caller that is
    // handed a PATH (`RunScript`) refuse a compiled file by name instead of parsing its bytes as source.
    internal const int ProgramMagic = 0x43424353;   // "SLCB"
    private const int Magic = ProgramMagic;
    // Library bundles (SerializeLibrary/DeserializeLibrary) use a distinct magic so a
    // single-script bundle (Bytecode.ExportScript) can be rejected with a clear error.
    internal const int LibraryMagic = 0x4C424C53;   // "SLBL"
    // Bundle format version. Every blob (single-script and library) carries this version and
    // readers require exactly it, DEV: no retro-compat, the format is not sealed yet.
    private const int BundleVersion = 30;  // v30 CARRIES A DECLARATION'S ACCESS: a loaded function or constructor was
    // registered as `public` unconditionally, in BOTH layouts (the record header and the shell record), so a private or
    // protected ctor/method was callable from outside in a bundle while the file refused it (measured on
    // `constructor_test`: "private ctor: global call rejected", "protected ctor: global call rejected" and a struct's
    // private ctor, three checks of one hole). Each declaration gained one int, so every part moved and the seven
    // verifier fixtures are re-cut in full; the emitted CODE is unchanged, so the FINGERPRINT and the body-relative
    // corruption offsets stand.
    // v29 EMBEDS THE BUNDLE'S OWN DIGEST: the header gained a 32-byte SHA-256 at
    // offset 12, computed over every byte outside that field, which is what makes a corrupted or naively edited file
    // detectable with NO sidecar. The header grew, so the parts moved and the frozen verifier fixtures were re-cut in
    // full (sizes, body starts, md5s). No opcode moved, so the FINGERPRINT is unchanged. A blob from an older engine is
    // refused by VERSION before the digest is looked at, which is why the check runs after it.
    // v26 CARRIES A DECLARATION'S TYPE PARAMETERS in its header, which is what
                                           // makes a loaded template generic again: the tree's instantiation arm is
                                           // selected by `TypeParameters.Count > 0`, and with an empty list a bundle
                                           // called the TEMPLATE and dropped the call site's type arguments
                                           // (`Identity<long>(42)` answered typeof "int" instead of "long"). The
                                           // header GREW by one string list, so every part moved and the seven
                                           // fixtures are re-cut in full; the emitted CODE is unchanged, so the
                                           // FINGERPRINT and the body-relative corruption offsets stand.
                                           //
                                           // v25 REMOVES the per-body BUILTINS section, which no opcode has read
                                           // since v24 retired LOAD_BUILTIN. The compiler never populated the table
                                           // (the list it fed was never appended to), so every body wrote a zero count
                                           // and its interned indexes were built, stored on the shell and dropped at
                                           // load: four dead bytes per body and two loops per shell. Nothing else moved,
                                           // so the FINGERPRINT is unchanged, while every body SHRANK by 4 bytes, which
                                           // is why the seven fixtures are re-cut in full rather than one byte each.
                                           //
                                           // v24 RETIRES SIX OPCODES the compiler never emitted (AND, OR,
                                           // LOAD_BUILTIN, LOAD_GLOBAL, STORE_GLOBAL, NEW). Removing them from the
                                           // enum RENUMBERS every later opcode, which moves the FINGERPRINT as well
                                           // as the version byte, so an older engine refuses a v24 blob by version
                                           // instead of decoding a differently-numbered stream. NOP stays, as the
                                           // compiler's one free alignment slot, and is the last entry
                                           // tools/opcode-not-emitted.txt lists. No record or descriptor layout
                                           // moved, so the five fixtures keep their sizes and body offsets and the
                                           // re-cut is the version byte plus the fingerprint.
                                           //
                                           // v23 stores each function's RECORD ONCE, in the parts section: the
                                           // directory holds (offset, length) per shell and the loader reads a
                                           // declaration's header FROM the part it points at (skip the body by the
                                           // entry's own length, which is why no body-layout knowledge is needed).
                                           // Before this the header was written twice per function, once as the
                                           // directory's own copy and once inside the part, so a format change had
                                           // to move BOTH and two readers had to agree about the flags byte (they
                                           // did not, once: a declaration that was only `async` came back
                                           // IsStatic). The stored parts base is gone with it, since a directory
                                           // offset is relative to the first part, which is the position right
                                           // after the directory, so a bundle and a library (whose tail follows its
                                           // own header block) cannot disagree about it. The record SHRANK, so an
                                           // older engine mis-reads a blob quickly rather than subtly, and the
                                           // fixtures were re-cut in full: the directory lost a header per function,
                                           // so their bodies moved.
                                           //
                                           // v22 carries each PROPERTY ACCESSOR's own access modifier and init
                                           // flag in the class and struct descriptors. The wire held only the
                                           // PROPERTY's access and no init flag, and the reader rebuilt both
                                           // accessors as public non-init, so a stripped bundle ALLOWED a private
                                           // setter write and an init write outside a constructor while its source
                                           // refused both (crash_properties_test 7a/7b/12n/12o/12q: 84 passed / 0
                                           // failed as a file against 79 / 5 as a bundle). The record GREW, so an
                                           // older engine is refused by this version rather than mis-reading it; the
                                           // five verifier fixtures hold functions only, so their layout did not move
                                           // and the re-cut was this byte alone.
                                           //
                                           // v21 carried a GENERIC CLASS as its own SOURCE TEXT in the DECLARATIONS
                                           // section (kind 6), because nothing else can reproduce one: an
                                           // INSTANTIATION clones the template's AST and carries no bytecode at all
                                           // (measured, `VmStats()` reads `VM: 0` for `new Box<int>().Get()`), so a
                                           // class descriptor, which holds bytecode, has no body to write. A KIND was
                                           // added to a list an older engine walks, so the version turns that into a
                                           // clean refusal rather than an unknown kind. No opcode moved, so the
                                           // FINGERPRINT is unchanged; the fixtures' layout does move, because the
                                           // declaration COUNT is written in every blob's header, so they are re-cut in
                                           // full (a header field grew from N to N+1 entries only when a class text
                                           // exists, which for these five function-only blobs means the count is
                                           // unchanged and only the version byte moves).
                                           // v20 carries a CLASS's EVENTS in its descriptor: name, delegate type name
                                           // and access. A class descriptor dropped them entirely, so a loaded
                                           // instance had no events at all: measured, `class_features_recap`'s
                                           // `event ValueChanged OnChange` answered "Field or event 'OnChange' not
                                           // found in class 'EventSource'" from a stripped bundle where the file
                                           // subscribed and fired it. The class SECTION grew, so a blob written here
                                           // cannot be read by an older engine: the version turns that into a clean
                                           // refusal rather than a mis-read stream. No opcode moved, so the FINGERPRINT
                                           // is unchanged; a blob that holds NO class (every frozen fixture) keeps its
                                           // layout, so its re-cut is the version byte alone.
                                           // v19 carries a declaration's TUPLE PARAMETER names in its shell: a tuple
                                           // parameter arrives with an `object` TypeInfo (the parser keeps the names
                                           // on FuncDecl.TupleParamNames instead), so a loaded body had none and
                                           // `int SumXY((int x, int y) p) { return p.x + p.y; }` called from a
                                           // compiled body answered "Property 'x' not found on tuple" from a
                                           // stripped bundle where the file answered 7. A RECORD gained a field, so a
                                           // blob written here cannot be read by an older engine: the version turns
                                           // that into a clean refusal rather than a mis-read stream. No opcode moved,
                                           // so the FINGERPRINT is unchanged and the fixtures were re-cut in full
                                           // again (a record GREW, so every body begins further along).
                                           // v18 carries a TYPE's TUPLE ELEMENT TYPES and NAMES in WriteTypeInfo: a
                                           // TypeInfo used to be written without them, so a tuple-typed value came
                                           // back from a bundle as a type that is not a tuple at all, losing every
                                           // element coercion and the element names of a returned tuple. Measured:
                                           // `var tl = new List<(int, int)>(); tl.Add(("a", 1));` THREW from a source
                                           // run and was accepted silently from a stripped bundle. A RECORD gained two
                                           // fields, and every TypeInfo on the wire is one of those records (a const, a
                                           // parameter, a return type, a field, a type argument), so a blob written here
                                           // cannot be read by an older engine: the version turns that into a clean
                                           // refusal rather than a mis-read stream. No opcode moved, so the FINGERPRINT is
                                           // unchanged, but a RECORD GREW, so this is NOT a one-byte re-cut: every body
                                           // begins further along and the five vm_verify_refused fixtures were re-cut in
                                           // full (433/439/558 B, bodies at 299/305/392, was 387/393/496 at
                                           // 265/271/342) with their provenance block updated.
                                           // v17 carries a MATCH PATTERN as const kind 20, which is its own SOURCE
                                           // TEXT: a Pattern is an AST fragment holding arbitrary expressions, so the
                                           // loader re-parses the text with the engine's parser. Without it the export
                                           // failed IN THE CODEC (with or without --no-source), so a file whose function
                                           // holds a `match` could not be compiled at all: pattern_vm_parity, the
                                           // corpus's single codec refusal. The wire gained a VALUE KIND, so a blob
                                           // written here cannot be read by an older engine: the version turns that into
                                           // a clean refusal rather than "bad const kind 20". No opcode moved, so the
                                           // FINGERPRINT is unchanged and only byte 4 of the five vm_verify_refused
                                           // fixtures moved, 16 -> 17. v16 carries a TUPLE's element NAMES as a const kind (19, a
                                           // List<string>), which the compiler puts in the pool for
                                           // ATTACH_TUPLE_NAMES: without it the export refused any file declaring a
                                           // tuple-typed variable with the raw "unsupported const type List`1",
                                           // measured on t4_struct, crash_json_serialize, pattern_vm_parity and
                                           // _probe7. The wire gained a VALUE KIND, so a blob written here cannot be
                                           // read by an older engine: the version is what turns that into a clean
                                           // refusal rather than "bad const kind 19". No opcode moved, so the
                                           // FINGERPRINT is unchanged and only byte 4 of the five vm_verify_refused
                                           // fixtures moved, 15 -> 16. v14 carries an INSTANCE FIELD INITIALIZER in a class descriptor as a compiled thunk (the shape a parameter default already travels in), so `public int V = 5;` no longer costs a stripped bundle its whole source. The field record gained a slot, so a class descriptor written here cannot be read by an older engine: the version turns that into a clean refusal rather than a mis-read stream. No opcode moved, so the FINGERPRINT is unchanged and only byte 4 of the five vm_verify_refused fixtures moved, 13 -> 14. v13 adds the IMPORT declaration kind to the DECLARATIONS section, so a stripped bundle carries an import statement's own data (path, alias, position) and the loader re-imports the module instead of the file carrying its whole source for one line. A KIND was added to a list an older engine walks, so the version is what turns that into a clean refusal rather than an unknown kind. No opcode moved, so the FINGERPRINT is unchanged and only byte 4 of the five vm_verify_refused fixtures moved, 12 -> 13. v12 records `async` on a function's header byte, which v11 used for `IsStatic` ALONE: the two bits share one byte, so no record boundary moved and the FINGERPRINT is unchanged (no opcode was added), and only byte 4 of the five vm_verify_refused fixtures moved (8-11 stay). A body's async-ness cannot always be derived from its bytecode (an `async` body with NO await has no AWAIT instruction), and a loaded one was called synchronously. v11 adds the AWAIT opcode (an await suspends like a yield but its RESUME pushes the awaited value), which moves the FINGERPRINT because that hashes the opcode names, so a blob written here is refused by fingerprint rather than mis-read by an older engine: byte 4 (the version) and bytes 8-11 (the fingerprint) of the five vm_verify_refused fixtures are re-cut with it, their sizes untouched. v10 widens the CONST operand to 2 bytes (BytecodeFormat.ConstWidthBytes), so every instruction carrying a const index is one byte longer: a body compiled here cannot be read by an older engine, which answers a clean version refusal instead of mis-decoding the stream. v9 adds the ArgFlagsMarker const kind (18), which records which call arguments were written `ref`/`out`: the wire gained a value kind, so an older engine reading one of these answers a clean version refusal rather than "bad const kind 18". No opcode moved, so the FINGERPRINT is unchanged and only byte 4 moved in the five verifier fixtures. v8 adds RENEW_LOCAL (a fresh capture cell at a nested declaration, the
                                           // tree's per-execution binding); v7 had added a declaration's DEFAULT VALUES to its shell, so a loaded function keeps the optional parameters the script declared (v6 added the DECLARATIONS section, so a stripped program can carry the enum, delegate, interface and extension declarations its bodies reference; v5 stores the carried source as a DEFLATE blob, v4 introduced that source field)

    /// <summary>The bits of a bundle function's ONE flags byte (see WriteBundleHeader). Kept as named constants so
    /// the writer and the reader cannot disagree about which bit is which, and so the next fact a declaration owes
    /// the wire is a third constant rather than a re-reading of the format.</summary>
    private const byte FlagStatic = 1;
    private const byte FlagAsync = 2;
    /// <summary>A REF RETURN (`ref int F(...)`): its body answers a REFERENCE rather than a value, so the RETURN has to
    /// know. Bit 4 of the same byte, so no record boundary moved.</summary>
    private const byte FlagRefReturn = 4;

    /// <summary>Stable fingerprint of the instruction set, blobs compiled by a different opcode layout
    /// are rejected on load instead of mis-executing.</summary>
    private static readonly int Fingerprint = ComputeFingerprint();

    /// <summary>Metadata attached to a bytecode-library bundle (always present in library bundles,
    /// DEV: no backward compat with pre-metadata bundles). Stored inline in the header as
    /// length-prefixed UTF-8 strings: name, version, description, then funcCount + per-function
    /// namespaces ("" = flat), index-aligned with the directory.</summary>
    internal sealed record LibraryMeta(
        string? Name, string? Version, string? Description,
        IReadOnlyList<string>? Sources);

    // ── Declaration descriptors (v6) ────────────────────────────────────────
    // The class/struct descriptor sections carry what a CLASS or STRUCT reproduces. Four kinds of
    // top-level declaration have no such section, so a program that declared one and was compiled with
    // --no-source simply lost it at run time (measured: a delegate answered "Undefined variable", an
    // extension block "Method 'X' not found on type string", an enum made the writer refuse with a raw
    // "value of type EnumValue cannot be serialized", and an interface made the CLI die on load). These
    // records are what the DECLARATIONS section carries instead, one per kind, and they are deliberately
    // plain data: the loader rebuilds the same registry state the interpreter's visitor builds.
    /// <summary>An enum: its members' RESOLVED values (the descriptor stores values, never expressions).</summary>
    internal sealed record LibraryEnum(string Name, AccessModifier Access, int Line, int Column,
        IReadOnlyList<(string Name, long Value)> Members);

    /// <summary>An interface: what its consumers must provide, plus the interfaces it extends and its type
    /// parameters (a generic interface is a TEMPLATE, so those are part of what it IS). Its DEFAULT BODIES and
    /// PROPERTY ACCESSORS travel as members' shells (bodies in the shared table), so a class that does not
    /// implement one gets the same body a source run gives it.</summary>
    internal sealed record LibraryInterface(string Name, IReadOnlyList<string> TypeParameters,
        IReadOnlyList<string> Parents, IReadOnlyList<(string Name, int Arity)> Members,
        IReadOnlyList<(string Key, FuncDecl Decl)> Defaults,
        IReadOnlyList<InterfacePropertySignature> Properties);

    /// <summary>An extension block: its target and type parameters, the container it was written in, and its
    /// MEMBER functions as ordinary shells (their bodies travel in the shared table like any other). The two
    /// halves a shell cannot reproduce are refused by the strip instead of lost here: a PROPERTY (no backing
    /// storage, its accessors are the whole implementation) and an OPERATOR (a shell would arrive without its
    /// operator token, which is how the registry finds it).</summary>
    internal sealed record LibraryExtension(TypeInfo Target, IReadOnlyList<string> TypeParameters,
        string? ContainerName, string? ReceiverName, IReadOnlyList<FuncDecl> Members);

    /// <summary>An IMPORT: the statement's own data, so a stripped bundle re-imports the module at load instead of
    /// carrying its whole source for one line. The path travels AS WRITTEN (the loader resolves it exactly as the
    /// statement would), the alias when it has one, and the position so a failure reports where the statement was.
    /// Measured before this: 23 of the corpus's 122 non-strippable suites were blocked by nothing but imports.</summary>
    internal sealed record LibraryImport(string Path, string? Alias, int Line, int Column);

    /// <summary>A DECLARATION carried as its own SOURCE TEXT, which the loader hands to the engine's OWN parser and
    /// runs through the same visitor a source run reaches. TWO declarations travel this way, and the wire entry is
    /// the same for both because the loader only ever re-parses:
    /// <list type="bullet">
    /// <item>A GENERIC class, because nothing else can reproduce one: an INSTANTIATION clones the template's AST and
    /// carries NO bytecode at all (measured, `VmStats()` reads `VM: 0` for `new Box<int>().Get()` against `VM: 1`
    /// for the same call on a plain class's method), so a class descriptor, which holds bytecode, has no body to
    /// write. One entry PER class, so a nested generic class of its own travels separately from its outer
    /// declaration.</item>
    /// <item>A TOP-LEVEL FUNCTION whose body the compiler did not take (a coroutine that yields a REQUEST, or any
    /// other body left to the tree): the wire has no slot for a body that was never compiled, so carrying the
    /// declaration is what gives a compiled caller something to run rather than a name with an empty body.</item>
    /// </list>
    /// The loader registers these LAST, so a declaration naming an enum, a delegate, an interface or an extension
    /// block finds it already registered.</summary>
    internal sealed record LibraryDeclText(string? Text, int Line, int Column);

    /// <summary>Everything the DECLARATIONS section carries, in one value so a loader needs a single out.</summary>
    internal sealed record LibraryDeclarations(
        IReadOnlyList<LibraryEnum> Enums, IReadOnlyList<DelegateInfo> Delegates,
        IReadOnlyList<LibraryInterface> Interfaces, IReadOnlyList<LibraryExtension> Extensions,
        IReadOnlyList<LibraryImport> Imports, IReadOnlyList<LibraryDeclText> DeclTexts)
    {
        public static readonly LibraryDeclarations Empty = new([], [], [], [], [], []);
        public bool IsEmpty => Enums.Count == 0 && Delegates.Count == 0 && Interfaces.Count == 0
            && Extensions.Count == 0 && Imports.Count == 0 && DeclTexts.Count == 0;
    }

    /// <summary>No operators. BuildDescriptorShells takes a dictionary, and the v6 extension descriptor carries
    /// MEMBERS only (an operator shell would arrive without the token its lookup is keyed by).</summary>
    private static readonly IReadOnlyDictionary<TokenType, FuncDecl> EmptyOperators = new Dictionary<TokenType, FuncDecl>();

    private static int ComputeFingerprint()
    {
        // Deterministic FNV-1a over the opcode names, string.GetHashCode() is randomized per process
        // (blobs must load in a different process than the one that wrote them).
        var names = Enum.GetNames(typeof(Opcode));
        var hash = 2166136261u;
        foreach (var n in names)
        {
            foreach (var ch in n) { hash ^= ch; hash *= 16777619u; }
            hash ^= 0xFF;
            hash *= 16777619u;
        }
        hash ^= (uint)names.Length;
        hash *= 16777619u;
        return unchecked((int)hash);
    }

    /// <summary>Sectioned bundle model, the on-disk SLCBC content. All name strings (function names,
    /// parameter names, local names, builtin names) live ONCE in a shared string table; each function
    /// shell references them by index, so a bundle with N functions carries the names once instead of
    /// N times. Non-name constants stay per-function (they are self-contained value data).</summary>
    internal sealed class BytecodeBundle
    {
        public string? EntryName = null;
        public string[] Strings = [];
        public BundleFunction[] Functions = [];
    }

    internal sealed class BundleFunction
    {
        public int NameIdx;
        public (TypeInfo Type, int NameIdx)[] Params = [];
        public IReadOnlyList<bool> IsRefParams = [];
        public IReadOnlyList<bool> IsParams = [];
        public IReadOnlyList<bool> IsOutParams = [];
        public TypeInfo ReturnType = null!;
        /// <summary>The RETURN tuple's element NAMES, when the declaration's return type is a NAMED tuple. Kept
        /// BESIDE ReturnType rather than read off it because that is where the runtime keeps it: the three sites
        /// that stamp a returned tuple (`Vm.cs`'s RETURN arm, `InvokeScriptFunction`'s VM-direct path and the
        /// tree's return path) all read `FuncDecl.TupleElementNames`, not the TypeInfo's own list. Measured
        /// without it: `(int a, int b) Pair() { return (1, 2); }` answered `Property 'a' not found on tuple` from
        /// a stripped bundle while the file answered 1.</summary>
        public IReadOnlyList<string>? TupleElementNames;
        /// <summary>The element NAMES of every TUPLE PARAMETER, index-aligned with Params (null where the
        /// parameter is not a tuple), which is where the parser keeps them: a tuple parameter arrives with an
        /// `object` TypeInfo, so nothing about it can be read off the type. `BindTupleParam` applies these names
        /// to the argument, and without them a loaded body answered "Property 'x' not found on tuple" while the
        /// file answered 7. Measured: the parameter's own TypeInfo is `base=Object name=- tuple=False` for both
        /// `int F((int, int) p)` and `int G((int x, int y) p)`, so the NAMES are the only carrier.</summary>
        public IReadOnlyList<IReadOnlyList<string>?>? TupleParamNames;
        /// <summary>The declaration's TYPE PARAMETERS (`T` in `T Identity<T>(T x)`), as written. They travel
        /// because a loaded template is otherwise an ORDINARY function to everything downstream: the tree's
        /// `case FuncDecl { TypeParameters.Count: > 0 }` arm is what instantiates a clone with the call's type
        /// arguments, and with the list empty a bundle called the TEMPLATE itself, so `Identity<long>(42)` answered
        /// `typeof` "int" where the file answers "long" (measured, and the same cause for a generic `Filter<int>`
        /// whose element type then went uncoerced).</summary>
        public IReadOnlyList<string>? TypeParameters;
        public bool IsStatic;
        /// <summary>The declaration's ACCESS (`private`, `protected`, `internal`, `public`). It travels because a
        /// LOADED declaration was registered as `public` unconditionally, so a private or protected constructor or
        /// method was callable from outside in a bundle while the file refused it: measured on
        /// `constructor_test`, whose own `try { new Shape("x", "y"); } catch` is REFUSED by the file (a private
        /// ctor) and was ALLOWED by a stripped bundle, three checks' worth of the same hole. The same family the
        /// property accessors had (v22), one member kind over.</summary>
        public AccessModifier Access = AccessModifier.Public;
        /// <summary>Whether the declaration was written `async`. Carried because the body's bytecode cannot always
        /// say so: an `async` body with NO `await` has no AWAIT instruction to derive it from, and a loaded one was
        /// then called synchronously (measured: `async Task<int> NF() { return 1; }` answered `typeof(NF()) ==
        /// "int"` from its `--no-source` bundle where the file answers `"Task"`).</summary>
        public bool IsAsync;
        /// <summary>A REF RETURN (`ref int F(...)`): the body answers a REFERENCE, so a loaded declaration has to know
        /// (its RETURN dereferences for a value consumer and hands the cell back for a tree lvalue one).</summary>
        public bool IsRefReturn;
        public int Line, Column;
        public byte[] Code = [];
        public object?[] Consts = [];
        public int[] LocalIndexes = [];
        /// <summary>The per-local const FLAG, as the source of truth rather than the indexes of the set bits: the
        /// writer derives those (see <see cref="WriteConstFlags"/>), and storing the indexes here meant the same
        /// five-line loop existed in both shell builders to compute them.</summary>
        public bool[]? ConstLocals;
        public int[] GlobalSlotIndexes = [];
        public BundleFunction[] NestedLambdas = [];
        /// <summary>The parameter index of each carried DEFAULT VALUE, aligned with DefaultThunks: a default is
        /// a zero-parameter body in the same shape as a nested lambda, and only the parameters that really have
        /// one appear here (see FuncDecl.DefaultThunks).</summary>
        public int[] DefaultThunkParams = [];
        public BundleFunction[] DefaultThunks = [];
    }

    // ── Library descriptor shells / shared writer context ───────────────────
    /// <summary>Shared writer context: ONE string table over the whole library bundle, function
    /// shells, class/struct method shells and ctor shells all intern their names here, so the table
    /// is written exactly once (see WriteBundleTail) and every shell references it by index.</summary>
    private sealed class BundleWriterContext
    {
        public readonly List<string> Strings = [];
        private readonly Dictionary<string, int> _strIdx = new(StringComparer.Ordinal);

        private int Intern(string s)
        {
            if (_strIdx.TryGetValue(s, out var i)) return i;
            _strIdx[s] = Strings.Count;
            Strings.Add(s);
            return Strings.Count - 1;
        }

        public BundleFunction BuildShell(FuncDecl f)
        {
            var sf = new BundleFunction
            {
                NameIdx = Intern(f.Name),
                IsRefParams = f.IsRefParams,
                IsParams = f.IsParams,
                IsOutParams = f.IsOutParams,
                ReturnType = f.ReturnType,
                TupleElementNames = f.TupleElementNames,
                TupleParamNames = f.TupleParamNames,
                TypeParameters = f.TypeParameters,
                Line = f.Line,
                Column = f.Column,
                IsStatic = f.IsStatic,
                IsAsync = f.IsAsync,
                IsRefReturn = f.IsRefReturn,
                Access = f.Access,
                Consts = f.Compiled!.Constants,
                Code = f.Compiled.Code
            };
            var prms = new (TypeInfo, int)[f.Params.Count];
            for (var i = 0; i < f.Params.Count; i++) prms[i] = (f.Params[i].Type, Intern(f.Params[i].Name));
            sf.Params = prms;
            var locs = new int[f.Compiled.Locals.Length];
            for (var i = 0; i < locs.Length; i++) locs[i] = Intern(f.Compiled.Locals[i]);
            sf.LocalIndexes = locs;
            sf.ConstLocals = f.Compiled.ConstLocals;
            if (f.Compiled.GlobalSlotNames != null)
            {
                var gs = new int[f.Compiled.GlobalSlotNames.Length];
                for (var i = 0; i < gs.Length; i++) gs[i] = Intern(f.Compiled.GlobalSlotNames[i]);
                sf.GlobalSlotIndexes = gs;
            }
            var nl = f.Compiled.NestedLambdas;
            if (nl != null)
            {
                var shells = new BundleFunction[nl.Length];
                for (var i = 0; i < nl.Length; i++) shells[i] = BuildShell(nl[i]);
                sf.NestedLambdas = shells;
            }
            // The DEFAULT VALUES this declaration carries, one zero-parameter shell each: a default expression
            // is compiled for the wire because the shell has no AST to carry it (FuncDecl.DefaultThunks), and a
            // parameter the compiler could not compile one for is simply absent here, which is what the export
            // reads when it decides whether the strip is honest.
            if (f.DefaultThunks != null)
            {
                var idxs = new List<int>();
                var thunks = new List<BundleFunction>();
                for (var i = 0; i < f.DefaultThunks.Length; i++)
                {
                    if (f.DefaultThunks[i] == null) continue;
                    idxs.Add(i);
                    thunks.Add(BuildThunkShell(f.DefaultThunks[i]!));
                }
                sf.DefaultThunkParams = [.. idxs];
                sf.DefaultThunks = [.. thunks];
            }
            return sf;
        }

        /// <summary>A default value's own shell: a zero-parameter body wrapping the compiled expression, in the
        /// same shape a nested lambda uses (so a default that contains a lambda still carries it).</summary>
        public BundleFunction BuildThunkShell(Bytecode body)
        {
            var decl = FuncDecl.Create("", [], [], [], [], new TypeInfo(TokenType.Object, false, 0, 0, null), null,
                AccessModifier.Public, 0, 0);
            decl.Compiled = body;
            return BuildShell(decl);
        }

        /// <summary>Constructor shells mirror BuildShell's field mappings; constructors have no
        /// name (NameIdx = ""), return void, and carry their own compiled params/locals/consts.</summary>
        public BundleFunction BuildCtorShell(ConstructorDecl c)
        {
            var sf = new BundleFunction
            {
                NameIdx = Intern(""),
                IsRefParams = c.IsRefParams,
                IsParams = c.IsParams ?? [],
                IsOutParams = c.IsOutParams ?? [],
                ReturnType = TypeInfo.Void,
                Line = c.Line,
                Column = c.Column,
                Consts = c.Compiled!.Constants,
                Code = c.Compiled.Code,
                IsStatic = c.IsStatic,
                Access = c.Access
            };
            var prms = new (TypeInfo, int)[c.Params.Count];
            for (var i = 0; i < c.Params.Count; i++) prms[i] = (c.Params[i].Type, Intern(c.Params[i].Name));
            sf.Params = prms;
            var locs = new int[c.Compiled.Locals.Length];
            for (var i = 0; i < locs.Length; i++) locs[i] = Intern(c.Compiled.Locals[i]);
            sf.LocalIndexes = locs;
            sf.ConstLocals = c.Compiled.ConstLocals;
            if (c.Compiled.GlobalSlotNames != null)
            {
                var gs = new int[c.Compiled.GlobalSlotNames.Length];
                for (var i = 0; i < gs.Length; i++) gs[i] = Intern(c.Compiled.GlobalSlotNames[i]);
                sf.GlobalSlotIndexes = gs;
            }
            var nl = c.Compiled.NestedLambdas;
            if (nl == null) return sf;
            {
                var shells = new BundleFunction[nl.Length];
                for (var i = 0; i < nl.Length; i++) shells[i] = BuildShell(nl[i]);
                sf.NestedLambdas = shells;
            }
            return sf;
        }
    }

    /// <summary>Pre-built method/operator/destructor/constructor shells for one class or struct
    /// descriptor. Built BEFORE the shared string table is written (descriptors live after the parts
    /// section in the stream, but their method/ctor bodies reference the shared table by index).</summary>
    /// <summary>The shells an interface's DEFAULT BODIES and PROPERTY ACCESSORS travel as. A shell is the shape
    /// every other body on the wire takes (a class method's, an extension member's), so an interface needs no third
    /// mechanism: its own list of them, aligned with the descriptor's defaults and properties.</summary>
    private sealed record InterfaceShells(BundleFunction[] Defaults,
        (BundleFunction? Getter, BundleFunction? Setter)[] Properties);

    private static InterfaceShells BuildInterfaceShells(BundleWriterContext ctx, LibraryInterface iface)
    {
        var defaults = new BundleFunction[iface.Defaults.Count];
        for (var i = 0; i < defaults.Length; i++) defaults[i] = ctx.BuildShell(iface.Defaults[i].Decl);
        var props = new (BundleFunction? Getter, BundleFunction? Setter)[iface.Properties.Count];
        for (var i = 0; i < props.Length; i++)
        {
            var p = iface.Properties[i];
            props[i] = (p.Getter == null ? null : ctx.BuildShell(p.Getter),
                p.Setter == null ? null : ctx.BuildShell(p.Setter));
        }
        return new InterfaceShells(defaults, props);
    }

    private sealed class DescriptorShells
    {
        public required BundleFunction[] Methods;
        /// <summary>One carried INSTANCE-FIELD INITIALIZER per field of the descriptor's list, or null where the
        /// field has none (a static field's value travels as a value, and a struct's initializer is ignored by the
        /// interpreter itself, so neither is here). Aligned with the field list the descriptor writes.</summary>
        public required BundleFunction?[] FieldInitializers;
        public required (TokenType Key, BundleFunction Shell)[] Operators;
        public BundleFunction? Destructor;
        public required BundleFunction[] Ctors;
    }

    private static DescriptorShells BuildDescriptorShells(BundleWriterContext ctx, IReadOnlyList<FuncDecl> methods,
        IReadOnlyList<FieldDecl> fields, IReadOnlyDictionary<TokenType, FuncDecl> operators, FuncDecl? dtor,
        IReadOnlyList<ConstructorDecl> ctors)
    {
        var ms = new BundleFunction[methods.Count];
        for (var i = 0; i < ms.Length; i++) ms[i] = ctx.BuildShell(methods[i]);
        var fis = new BundleFunction?[fields.Count];
        for (var i = 0; i < fis.Length; i++)
            if (fields[i].InitializerThunk is { } thunk) fis[i] = ctx.BuildThunkShell(thunk);
        var ops = new (TokenType, BundleFunction)[operators.Count];
        var oi = 0;
        foreach (var kv in operators) ops[oi++] = (kv.Key, ctx.BuildShell(kv.Value));
        var cs = new BundleFunction[ctors.Count];
        for (var i = 0; i < cs.Length; i++) cs[i] = ctx.BuildCtorShell(ctors[i]);
        return new DescriptorShells
        {
            Methods = ms,
            FieldInitializers = fis,
            Operators = ops,
            Destructor = dtor == null ? null : ctx.BuildShell(dtor),
            Ctors = cs
        };
    }

    // ── The bundle's own digest ──────────────────────────────────────────────
    /// <summary>Where the content digest sits in the header: `Magic | BundleVersion | Fingerprint` is three ints, so the
    /// 32-byte SHA-256 starts at 12 and is computed with ITS OWN bytes taken as zeros (a file cannot hash itself). Every
    /// byte outside the field is covered: a body, a table, a name, the carried source, the version and the fingerprint.</summary>
    internal const int ContentHashOffset = 12;
    internal const int ContentHashBytes = 32;

    /// <summary>Stamps the digest into a freshly serialized blob and answers it. Called by both serializers, so a bundle
    /// and a library are sealed the same way and the loader's one check covers both.</summary>
    private static byte[] StampContentHash(byte[] blob)
    {
        if (blob.Length < ContentHashOffset + ContentHashBytes) { return blob; }
        Array.Clear(blob, ContentHashOffset, ContentHashBytes);
        var digest = SHA256.HashData(blob);
        Array.Copy(digest, 0, blob, ContentHashOffset, ContentHashBytes);
        return blob;
    }

    /// <summary>
    /// Verifies the digest a bundle carries about ITSELF, and refuses by name when it disagrees.
    /// WHAT THIS IS AND IS NOT, because the difference decides whether it is worth anything: it detects a CORRUPT or
    /// naively EDITED file (a truncated download, a byte changed by a bad copy, a hand-patched body), and it does that with
    /// NO second file, which is what the sidecar could not do. It is NOT a signature: the digest is in the file it
    /// describes, so somebody who edits the body AND recomputes the field produces a file that passes. For authenticity
    /// keep the `<name>.slcbc.sha256` sidecar (still honoured when present) or a hash you store elsewhere.
    /// ORDER MATTERS at the call sites: this runs AFTER the magic, version and fingerprint checks, so a blob from an older
    /// engine answers its clean version refusal rather than a checksum error (its bytes 12..43 are not a digest).
    /// </summary>
    internal static string? ContentHashMismatch(byte[] blob)
    {
        if (blob.Length < ContentHashOffset + ContentHashBytes) { return "the blob is too short to carry a content digest"; }
        var copy = (byte[])blob.Clone();
        var stored = new byte[ContentHashBytes];
        Array.Copy(copy, ContentHashOffset, stored, 0, ContentHashBytes);
        Array.Clear(copy, ContentHashOffset, ContentHashBytes);
        var actual = SHA256.HashData(copy);
        if (stored.AsSpan().SequenceEqual(actual)) { return null; }
        // NAME THE FIRST BYTE THAT DISAGREES, not two eight-byte prefixes: a digest field somebody edited differs from the
        // real one deeper in, so equal prefixes read as "they match" exactly when the file is wrong.
        var differing = 0;
        while (differing < ContentHashBytes && stored[differing] == actual[differing]) { differing = differing + 1; }
        return $"the file changed after it was built (the digest differs from byte {differing} on: it says "
            + $"{Convert.ToHexString(stored.AsSpan(0, 8))}..., the bytes say {Convert.ToHexString(actual.AsSpan(0, 8))}...)";
    }

    // ── Bundle (whole-script export) ─────────────────────────────────────────
    /// <summary>Serializes several VM-compiled top-level functions plus an optional entry name into one
    /// sectioned bundle blob: Magic | BundleVersion | Fingerprint | entryName | shared-string-table |
    /// function-directory (header fields + body offset/length per function) | concatenated function
    /// bodies. The directory lets a loader scan every function's signature without decoding its body,
    /// so every body is decoded at load, in one pass over the parts.</summary>
    public static byte[] SerializeBundle(IReadOnlyList<FuncDecl> funcs, string? entryName, string? source = null)
    {
        var ctx = new BundleWriterContext();
        var shells = new BundleFunction[funcs.Count];
        for (var i = 0; i < funcs.Count; i++) shells[i] = ctx.BuildShell(funcs[i]);
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(BundleVersion);   // single-script bundles use the same versioned format as libraries, the format is versioned by BundleVersion (no retro-compat in DEV)
            w.Write(Fingerprint);
            w.Write(new byte[ContentHashBytes]);   // the digest field, stamped once the whole blob exists
            WriteBundleTail(w, ctx, shells, entryName ?? "", source ?? "");
        }
        return StampContentHash(ms.ToArray());
    }

    /// <summary>Writes the sectioned bundle tail shared by whole-script bundles and libraries:
    /// entryName | carried source | string-table | directory (offset/length per shell) | the parts, one
    /// header+body record per shell. A directory offset is relative to the first part, which is the position
    /// right after the directory, so no base is stored. All names referenced by the shells must have been
    /// interned into <paramref name="ctx"/> BEFORE this call so the string table is complete.</summary>
    /// <summary>Writes the carried source as a length-prefixed DEFLATE blob. The text is a script's
    /// own program, so it is stored compressed; the reader inflates it and every consumer still sees a
    /// plain string. Measured on this corpus a source deflates to 17 to 47% of its UTF-8 size, and the
    /// source is most of a bundle's payload (a suite whose source is 17,351 bytes went from a 20,396-byte
    /// bundle to about 7,800). A library passes an empty string, so there is one form rather than two.</summary>
    private static void WriteDeflatedSource(BinaryWriter w, string source)
    {
        var raw = Encoding.UTF8.GetBytes(source ?? "");
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(raw, 0, raw.Length);
        var packed = ms.ToArray();
        w.Write(packed.Length);
        w.Write(packed);
    }

    /// <summary>The largest inflated source accepted. A bundle is script INPUT, and a blob that inflates
    /// without bound is exactly the input the standing rule says must never hurt the process.</summary>
    private const int MaxSourceChars = 32 * 1024 * 1024;

    /// <summary>The refusal a bundle from ANOTHER ENGINE VERSION gets, stated once for both readers. It names the
    /// number it found, the one this engine reads, and the fix a reader can act on: a `.slcbc` from an older build
    /// is the ordinary cause, and "format version mismatch" alone leaves the author guessing (reported from a live
    /// session, where that sentence was all the message said).</summary>
    private static string VersionRefusal(int found) =>
        $"bytecode format version {found} was written by a different engine version (this one reads version {BundleVersion}): rebuild it with --compile <file.slc>";

    /// <summary>The sibling refusal for the instruction-set fingerprint, same reasoning: say what changed and what
    /// to do, rather than naming an internal field.</summary>
    /// <summary>The one wording for a bundle whose own digest disagrees, so the two layouts cannot answer differently.
    /// It names the field, the two half-digests, and the two things a reader can do about it.</summary>
    private static string DigestRefusal(string detail) =>
        $"this bundle does not match the digest it carries: {detail}. "
        + "Rebuild it with --compile, or rebuild it from its source; a bundle with a sidecar (`<name>.slcbc.sha256`) is "
        + "checked against that too.";

    private static string FingerprintRefusal() =>
        "bytecode compiled by an incompatible VM (the instruction set differs): rebuild it with --compile <file.slc>";

    /// <summary>Reads what WriteDeflatedSource wrote. Returns null with a reason when the blob cannot be
    /// inflated or inflates past MaxSourceChars, so a corrupted bundle is refused by name instead of
    /// swallowed. The library reader consumes this field too, so both callers take the same path.</summary>
    private static string? ReadDeflatedSource(BinaryReader r, out string? error)    {
        error = null;
        var packedLen = r.ReadInt32();
        if (packedLen < 0 || packedLen > r.BaseStream.Length)
        { error = "the carried source length is out of range"; return null; }
        var packed = r.ReadBytes(packedLen);
        if (packed.Length != packedLen) { error = "the carried source is truncated"; return null; }
        try
        {
            using var ms = new MemoryStream(packed);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var sr = new StreamReader(ds, Encoding.UTF8);
            var buffer = new char[64 * 1024];
            var sb = new StringBuilder();
            int read;
            while ((read = sr.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (sb.Length + read > MaxSourceChars) { error = "the carried source is implausibly large"; return null; }
                sb.Append(buffer, 0, read);
            }
            return sb.ToString();
        }
        catch (Exception) { error = "the carried source did not inflate"; return null; }
    }

    /// <summary>The sectioned TAIL both blob kinds share, in the order <see cref="WriteBundleTail"/> emits it:
    /// entryName, the carried source, the string table, the directory, and then the parts. ONE reader, so the two
    /// layouts cannot drift the way they did while each kept its own copy of this sequence (and while the library
    /// reader had to ignore a stored base the bundle reader trusted). The base is MEASURED, never stored: a
    /// directory offset is relative to the first part, which is the position right after the directory.
    /// <paramref name="sourceError"/> is returned rather than thrown because a library carries no source of its
    /// own, so it reads that field only to stay aligned and has no reason to refuse a blob over it.</summary>
    private static (string[] Strings, (int Offset, int Length)[] Entries, int PartsBase) ReadBundleTail(
        BinaryReader r, out string? entryName, out string? source, out string? sourceError)
    {
        var en = ReadWireString(r);
        entryName = en.Length == 0 ? null : en;
        source = ReadDeflatedSource(r, out sourceError);
        var strCount = r.ReadInt32();
        var strings = new string[strCount];
        for (var i = 0; i < strCount; i++) strings[i] = ReadWireString(r);
        var cnt = r.ReadInt32();
        var entries = new (int Offset, int Length)[cnt];
        for (var i = 0; i < cnt; i++) entries[i] = (r.ReadInt32(), r.ReadInt32());
        return (strings, entries, (int)r.BaseStream.Position);
    }

    private static void WriteBundleTail(BinaryWriter w, BundleWriterContext ctx, IReadOnlyList<BundleFunction> shells, string entryName, string source)
    {
        w.Write(entryName);
        WriteDeflatedSource(w, source);   // the entry SOURCE, deflated; a library passes ""
        w.Write(ctx.Strings.Count);
        foreach (var s in ctx.Strings) w.Write(s);
        w.Write(shells.Count);
        // The directory names each shell by where its RECORD is, and a record lives in ONE place now: the parts
        // section below. It used to be stored twice, once as the directory's own copy of the header (so a
        // declaration could be registered without decoding a body) and again inside the part, so a format change
        // had to move BOTH and two readers had to agree about the flags byte (they did not, once). The directory
        // is (offset, length) per shell, and the loader reads a declaration's header from the part it points at.
        //
        // An offset is relative to the first part, which is the position right after the directory, so no base is
        // stored: the old base int was absolute for a bundle and stale for a library, and the library reader
        // compensated for that. The two ints are written as PLACEHOLDERS and patched once each body has been
        // streamed, which is also why the bodies are not buffered anywhere first.
        var entryAt = new long[shells.Count];
        for (var i = 0; i < shells.Count; i++)
        {
            w.Flush();                                    // BinaryWriter buffers, so a position read wants this
            entryAt[i] = w.BaseStream.Position;           // where this entry's offset int goes
            w.Write(0);
            w.Write(0);
        }
        w.Flush();
        var partsBase = (int)w.BaseStream.Position;       // the parts begin here; directory offsets are relative to it
        for (var i = 0; i < shells.Count; i++)
        {
            w.Flush();
            var start = (int)w.BaseStream.Position - partsBase;
            WriteBundleFunc(w, shells[i]);                // header + body, streamed: no scratch copy
            w.Flush();
            var length = (int)w.BaseStream.Position - partsBase - start;
            var resume = w.BaseStream.Position;
            w.BaseStream.Position = entryAt[i];
            w.Write(start);
            w.Write(length);
            w.Flush();
            w.BaseStream.Position = resume;
        }
    }

    /// <summary>Serializes a library bundle: Magic | BundleVersion | Fingerprint | name | version |
    /// description | nsCount | ns[0..funcCount-1] | clsCount | structCount | varCount | variable
    /// snapshots (name|value), then the shared bundle tail (entryName "" | string table | directory |
    /// parts) for the library's functions, then the class and struct descriptor sections (their
    /// method/ctor shells are interned in the SAME string table but not added to the directory).
    /// No backward-compat path, every library bundle carries the namespace array.</summary>
    public static byte[] SerializeLibrary(IReadOnlyList<FuncDecl> funcs, IReadOnlyList<string>? namespaces,
        LibraryMeta? meta, IReadOnlyList<ClassInfo>? classes = null, IReadOnlyList<StructDeclStmt>? structs = null,
        IReadOnlyList<(string Name, object? Value)>? variables = null, string? entryName = null,
        LibraryDeclarations? declarations = null)
    {
        var clss = classes ?? [];
        var sts = structs ?? [];
        var decls = declarations ?? LibraryDeclarations.Empty;
        // Build ALL shells first (functions + class/struct methods/ctors + extension members) so the shared string
        // table is complete before the tail writer freezes it.
        var ctx = new BundleWriterContext();
        var funcShells = new BundleFunction[funcs.Count];
        for (var i = 0; i < funcShells.Length; i++) funcShells[i] = ctx.BuildShell(funcs[i]);
        var classShells = new DescriptorShells[clss.Count];
        for (var i = 0; i < clss.Count; i++)
            classShells[i] = BuildDescriptorShells(ctx, [.. clss[i].Methods, .. clss[i].StaticMethods ?? []], clss[i].Fields, clss[i].OperatorMethods, clss[i].Destructor, clss[i].Constructors);
        var structShells = new DescriptorShells[sts.Count];
        for (var i = 0; i < sts.Count; i++)
            // A STRUCT carries NO field initializer, deliberately: the interpreter ignores one (measured,
            // `struct S { public int V = 5; }` reads 0 from SOURCE, on both engines), so carrying it would make a
            // bundle build an instance the file does not. The empty list is the field set's stand-in.
            structShells[i] = BuildDescriptorShells(ctx, sts[i].Methods, [], sts[i].OperatorMethods, null, sts[i].Constructors);
        // An extension member is an ordinary FuncDecl, so its body travels exactly like a class method's:
        // through the shared table, as a shell. Same builder, same write/read pair, no third mechanism.
        var extShells = new DescriptorShells[decls.Extensions.Count];
        for (var i = 0; i < extShells.Length; i++)
            extShells[i] = BuildDescriptorShells(ctx, decls.Extensions[i].Members, [], EmptyOperators, null, []);
        // An interface's DEFAULT BODIES and PROPERTY ACCESSORS are bodies too, so they travel as shells like every
        // other member (see InterfaceShells): this is what makes an interface with a default strippable at all.
        var ifaceShells = new InterfaceShells[decls.Interfaces.Count];
        for (var i = 0; i < ifaceShells.Length; i++)
            ifaceShells[i] = BuildInterfaceShells(ctx, decls.Interfaces[i]);

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(LibraryMagic);
            w.Write(BundleVersion);   // library bundles carry the class/struct/variable/declaration descriptor sections; readers require exactly this version
            w.Write(Fingerprint);
            w.Write(new byte[ContentHashBytes]);   // the digest field, stamped once the whole blob exists
            w.Write(meta?.Name ?? "");
            w.Write(meta?.Version ?? "");
            w.Write(meta?.Description ?? "");
            var nss = namespaces ?? [];
            w.Write(nss.Count);
            foreach (var ns in nss) w.Write(ns);
            // v2 descriptor sections: counts first, descriptor bodies after the parts section.
            w.Write(clss.Count);
            w.Write(sts.Count);
            w.Write(variables?.Count ?? 0);
            // v6: the number of DECLARATION entries that follow the class/struct bodies (see WriteDeclarations).
            w.Write(decls.Enums.Count + decls.Delegates.Count + decls.Interfaces.Count + decls.Extensions.Count
                + decls.Imports.Count + decls.DeclTexts.Count);
            if (variables != null)
                foreach (var (name, val) in variables) { w.Write(name); WriteScriptValue(w, val); }
            // Shared bundle tail (libraries declare no bundle entryName):
            WriteBundleTail(w, ctx, funcShells, entryName ?? "", "");   // an entry turns a library blob into a runnable program that needs no source
            // Class/struct descriptor bodies, emitted AFTER the parts section, referencing the
            // shared string table written above.
            for (var i = 0; i < clss.Count; i++)
                WriteClassDescriptor(w, clss[i], classShells[i], val => WriteScriptValue(w, val));
            for (var i = 0; i < sts.Count; i++)
                WriteStructDescriptor(w, sts[i], structShells[i]);
            WriteDeclarations(w, decls, extShells, ifaceShells);
        }
        return StampContentHash(ms.ToArray());
    }

    /// <summary>The SOURCE TEXT a bundle carries, whichever KIND of blob it is, inflated here so a caller
    /// needs nothing external. A whole-script bundle carries its top level as text, stored compressed in the
    /// file; a library carries none of its own, so it answers "". Dispatched to the ordinary readers rather
    /// than to a second header walk, so the text can never come from a position other than the one the
    /// loader uses.</summary>
    public static string? GetSource(byte[] data, out string? error)
    {
        error = null;
        if (data.Length < 8) { error = "not a ScriptLangCore bytecode blob"; return null; }
        var magic = BitConverter.ToInt32(data, 0);
        if (magic == Magic)
        {
            var funcs = DeserializeBundle(data, out var err, out _, out var source);
            if (funcs == null) { error = err; return null; }
            return source ?? string.Empty;
        }
        if (magic == LibraryMagic)
        {
            var funcs = DeserializeLibrary(data, out var err, out _, out _, out _, out _, out _, out _, out _);
            if (funcs == null) { error = err; return null; }
            return string.Empty;   // a library carries no program text of its own
        }
        error = "not a ScriptLangCore bytecode bundle";
        return null;
    }

    /// <summary>Parses a bundle's directory (all function signatures) without decoding anybody.
    /// Returns the shells (Compiled stays null until first invocation) plus a per-shell lazy body
    /// that Decode()s the bytecode on demand.</summary>
    public static List<FuncDecl>? DeserializeBundle(byte[] data, out string? error, out string? entryName,
        out string? source)
    {
        error = null;
        entryName = null;
        source = null;
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            if (r.ReadInt32() != Magic) { error = "not a ScriptLangCore bytecode blob"; return null; }
            if (r.ReadInt32() is var bundleVersion && bundleVersion != BundleVersion) { error = VersionRefusal(bundleVersion); return null; }
            if (r.ReadInt32() != Fingerprint) { error = FingerprintRefusal(); return null; }
            _ = r.ReadBytes(ContentHashBytes);   // the digest field, verified just below
            if (ContentHashMismatch(data) is { } bundleDigestError) { error = DigestRefusal(bundleDigestError); return null; }
            var (strings, entries, partsBase) = ReadBundleTail(r, out entryName, out source, out var sourceError);
            if (sourceError != null) { error = sourceError; return null; }
            var result = new List<FuncDecl>(entries.Length);
            foreach (var (offset, length) in entries)
            {
                var fd = ReadPartFunc(data, partsBase, offset, length, strings, out var derr);
                if (fd == null) { error = derr; return null; }
                result.Add(fd);
            }
            return result;
        }
        catch (Exception ex)
        {
            // Same rule as the bundle reader: the engine's own words for a container that does not decode.
            error = FriendlyExceptionMessage(ex);
            return null;
        }
    }

    /// <summary>DeserializeBundle + reads the mandatory inline metadata block from the header.
    /// Returns funcs, the parsed meta, the per-function namespaces array (index-aligned with the
    /// directory), the rebuilt class/struct descriptors, and the top-level
    /// variable snapshots, the same shape as DeserializeBundle plus the meta, namespaces,
    /// classes, structs and variables out parameters.
    /// DEV: every library bundle carries metadata; a bundle without it (e.g. written by ExportScript)
    /// fails with a format error.</summary>
    public static List<FuncDecl>? DeserializeLibrary(byte[] data, out string? error, out LibraryMeta? meta,
        out IReadOnlyList<string>? namespaces,
        out IReadOnlyList<ClassInfo>? classes, out IReadOnlyList<StructDeclStmt>? structs,
        out IReadOnlyList<(string Name, object? Value)>? variables, out string? entryName,
        out LibraryDeclarations? declarations)
    {
        meta = null;
        error = null;
        namespaces = null;
        classes = null;
        structs = null;
        variables = null;
        entryName = null;
        declarations = LibraryDeclarations.Empty;
        try
        {
            using var ms = new MemoryStream(data);
            using var r = new BinaryReader(ms);
            if (r.ReadInt32() != LibraryMagic) { error = "not a bytecode library bundle (single-script blobs are not importable, use Bytecode.LoadScript)"; return null; }
            if (r.ReadInt32() is var libraryVersion && libraryVersion != BundleVersion) { error = VersionRefusal(libraryVersion); return null; }
            if (r.ReadInt32() != Fingerprint) { error = FingerprintRefusal(); return null; }
            _ = r.ReadBytes(ContentHashBytes);   // the digest field, verified just below
            if (ContentHashMismatch(data) is { } libraryDigestError) { error = DigestRefusal(libraryDigestError); return null; }
            static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
            var name = NullIfEmpty(ReadWireString(r));
            var version = NullIfEmpty(ReadWireString(r));
            var desc = NullIfEmpty(ReadWireString(r));
            var nsCount = r.ReadInt32();
            var nss = new List<string>(nsCount);
            for (var i = 0; i < nsCount; i++) nss.Add(ReadWireString(r));
            // Type/variable descriptor sections, unconditional (the only accepted version is
            // BundleVersion; v1 no longer exists). Counts are read here; the descriptor BODIES live
            // after the parts section and are consumed once the parts have been skipped below.
            var clsCount = r.ReadInt32();
            var structCount = r.ReadInt32();
            var varCount = r.ReadInt32();
            // v6: how many DECLARATION entries follow the class/struct bodies (enums, delegates,
            // interfaces, extensions, see ReadDeclarations).
            var declCount = r.ReadInt32();
            var vars = new List<(string Name, object? Value)>(varCount);
            for (var i = 0; i < varCount; i++) vars.Add((ReadWireString(r), ReadScriptValue(r)));
            variables = vars;
            meta = new LibraryMeta(name, version, desc, nss);
            namespaces = nss;
            var (strings, entries, partsBase) = ReadBundleTail(r, out entryName, out _, out _);
            var totalBodyLength = 0;
            var result = new List<FuncDecl>(entries.Length);
            foreach (var (offset, length) in entries)
            {
                var fd = ReadPartFunc(data, partsBase, offset, length, strings, out var derr);
                if (fd == null) { error = derr; return null; }
                result.Add(fd);
                totalBodyLength += length;
            }
            // Skip the parts section (function bodies, decoded at load) to reach
            // the class/struct descriptor sections the writer emits after the parts.
            r.BaseStream.Position = partsBase + totalBodyLength;
            if (clsCount > 0 || structCount > 0)
            {
                Func<BinaryReader, string[], FuncDecl> readFunc = (rr, sTrs) => ReadBundleFunc(rr, sTrs, out _)!;
                var cls = new List<ClassInfo>(clsCount);
                for (var i = 0; i < clsCount; i++)
                {
                    var ci = ReadClassDescriptor(r, strings, readFunc, out string? _);
                    MarkVmShell(ci);
                    cls.Add(ci);
                }
                classes = cls;
                var sts = new List<StructDeclStmt>(structCount);
                for (var i = 0; i < structCount; i++)
                {
                    var sd = ReadStructDescriptor(r, strings, readFunc, out var sErr);
                    MarkVmShell(sd);
                    sts.Add(sd);
                }
                structs = sts;
            }
            else
            {
                classes = [];
                structs = [];
            }
            // v6: the declaration entries come last, after the class/struct bodies (the classes/structs block
            // above still runs so the position is right even when there are none of either).
            if (declCount > 0) declarations = ReadDeclarations(r, strings, declCount);
            // THE MIRROR DRIFT: a reader that consumes FEWER fields than the writer wrote leaves bytes behind, and the
            // bounded string reader only catches the other direction (a length past the end). A blob this engine wrote
            // ends exactly where its last section does, so anything left over is the same defect, refused in the same
            // words before a single class is registered.
            var leftover = r.BaseStream.Length - r.BaseStream.Position;
            if (leftover != 0)
                throw new ScriptException($"the bundle's descriptor stream is out of phase: {leftover} byte(s) are left"
                    + " after the last declaration section. A reader and a writer of this format disagree about a field,"
                    + " or the blob was edited by hand; rebuild it with --compile and report it if the rebuilt one is"
                    + " refused too");
            return result;
        }
        catch (Exception ex)
        {
            // A blob is script INPUT, so a container failure is reported in the engine's own words: this is the
            // ONE exception-to-message place (FriendlyExceptionMessage), because a raw CLR sentence or a
            // resource-keyed one reached a user from here before (a truncated blob read
            // "EndOfStreamException: Unable to read beyond the end of the stream").
            error = FriendlyExceptionMessage(ex);
            return null;
        }
    }

    // Library class/struct method bodies are bytecode-only (no AST Body), mark them
    // IsVmShell so the VM dispatch gates (VmPathEnabled) run them on the VM even when
    // the bytecode VM is toggled off for source functions. Constructors need no flag:
    // ctor dispatch is `Compiled != null`-based (not VmPathEnabled-gated), so a compiled
    // bundle ctor already runs on the VM regardless of the toggle, and ConstructorDecl
    // carries no IsVmShell property.
    static void MarkVmShell(ClassInfo ci)
    {
        foreach (var m in ci.Methods) { m.IsVmShell = true; MarkNestedLambdasVmShell(m); }
        if (ci.StaticMethods != null)
            foreach (var m in ci.StaticMethods) { m.IsVmShell = true; MarkNestedLambdasVmShell(m); }
        foreach (var op in ci.OperatorMethods.Values) { op.IsVmShell = true; MarkNestedLambdasVmShell(op); }
        if (ci.Destructor != null) { ci.Destructor.IsVmShell = true; MarkNestedLambdasVmShell(ci.Destructor); }
    }

    /// <summary>A deserialized function's NESTED LAMBDAS are bytecode-only too: the wire carries their compiled
    /// bodies and NOT their statements. Measured: in a source-less bundle a Com.Run callback ran while its own
    /// `res` stayed empty (body=0, compiled=ok, IsVmShell false), i.e. the tree walked an empty body in silence,
    /// which shortened assets/language/com/com_excel_sheets_test.slc to 14 of its 43 checks with no error.
    /// Marking them puts them under the same VM dispatch gate as every other body the wire produced, so a
    /// fallback REFUSES by name instead of doing nothing.</summary>
    static void MarkNestedLambdasVmShell(FuncDecl fn)
    {
        foreach (var lam in fn.Compiled?.NestedLambdas ?? [])
        {
            lam.IsVmShell = true;
            MarkNestedLambdasVmShell(lam);
        }
    }
    static void MarkVmShell(StructDeclStmt sd)
    {
        foreach (var m in sd.Methods) { m.IsVmShell = true; MarkNestedLambdasVmShell(m); }
        foreach (var op in sd.OperatorMethods.Values) { op.IsVmShell = true; MarkNestedLambdasVmShell(op); }
    }

    /// <summary>Reads one class descriptor (mirror of WriteClassDescriptor): name, parent, access,
    /// record flag, fields, properties (backing name skipped), method/operator/destructor/ctor
    /// bodies via readFunc, nested-type counts, and static-field value snapshots via ReadScriptValue.
    /// Rebuilds a ClassInfo whose methods/ctors carry Compiled bytecode (no AST bodies).</summary>
    private static ClassInfo ReadClassDescriptor(BinaryReader r, string[] strings,
        Func<BinaryReader, string[], FuncDecl> readFunc, out string? error)
    {
        error = null;
        var name = ReadWireString(r);
        var parentRaw = ReadWireString(r);
        var parentName = parentRaw.Length == 0 ? null : parentRaw;
        var access = (AccessModifier)r.ReadInt32();
        var isRecord = r.ReadBoolean();
        var fieldCount = r.ReadInt32();
        var fields = new List<FieldDecl>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            var type = ReadTypeInfo(r); var fname = ReadWireString(r);
            var faccess = (AccessModifier)r.ReadInt32(); var isStatic = r.ReadBoolean();
            // An INSTANCE field's initializer comes back as a compiled zero-parameter body, wrapped as the
            // expression EVERY construction path evaluates (the node a carried parameter default uses), so a loaded
            // class builds the same instance the file does. A body a tree fallback ever reached is refused by name
            // rather than run empty, exactly as a default's thunk is. A struct reads NO such slot: its initializer is
            // ignored by the interpreter itself, so the class descriptor is the only one that carries it.
            Expr? init = null;
            Bytecode? initThunk = null;
            if (r.ReadBoolean())
            {
                var thunkDecl = readFunc(r, strings);
                thunkDecl.IsVmShell = true;
                MarkNestedLambdasVmShell(thunkDecl);
                initThunk = thunkDecl.Compiled;
                init = CompiledDefaultExpr.Create(thunkDecl, thunkDecl.Line, thunkDecl.Column);
            }
            fields.Add(new FieldDecl(faccess, type, fname, init, isStatic) { InitializerThunk = initThunk });
        }
        // The member blocks both descriptors share, read in ONE place (see ReadMemberBlocks): the implemented
        // interfaces, the properties, the methods, the operators, the destructor and the ctors. The class shape
        // splits its static methods out.
        var blocks = ReadMemberBlocks(r, strings, readFunc, withDestructor: true, splitStatics: true);
        // nested-type counts (writer emits counts only, no recursion)
        _ = r.ReadInt32();
        _ = r.ReadInt32();
        // static-field value snapshots, mirror the writer's name → WriteScriptValue section
        var staticCount = r.ReadInt32();
        Dictionary<string, object?>? staticFields = null;
        if (staticCount > 0)
        {
            staticFields = new Dictionary<string, object?>(staticCount, StringComparer.Ordinal);
            for (var i = 0; i < staticCount; i++) staticFields[ReadWireString(r)] = ReadScriptValue(r);
        }
        // events: count | (name|delegateTypeName|access)…, the class descriptor's LAST section, mirroring the
        // writer. The instance side is rebuilt from these BY NAME and lazily (`GetEventInfo`,
        // `FindEventInHierarchy`), so a class that declared an event has one again and a handler's signature is
        // checked against the delegate's name.
        var eventCount = r.ReadInt32();
        var events = new List<EventDecl>(eventCount);
        for (var i = 0; i < eventCount; i++)
        {
            var ename = ReadWireString(r);
            var edelegate = ReadWireString(r);
            var eaccess = (AccessModifier)r.ReadInt32();
            events.Add(EventDecl.Create(ename, edelegate, eaccess, 0, 0));
        }
        var classInfo = new ClassInfo(name, parentName, fields, blocks.Methods, blocks.Ctors, [], events,
            blocks.Operators, 0, access, blocks.Properties)
        {
            Destructor = blocks.Destructor,
            IsRecord = isRecord,
            ImplementedInterfaces = blocks.Implemented,
            StaticFields = staticFields,
            StaticMethods = blocks.StaticMethods is { Count: > 0 } ? blocks.StaticMethods : null
        };
        return classInfo;
    }

    /// <summary>Reads one struct descriptor (mirror of WriteStructDescriptor): name, access,
    /// isReadOnly, fields, properties, method/operator/ctor bodies via readFunc. Rebuilds a
    /// StructDeclStmt whose methods/ctors carry Compiled bytecode (no AST bodies).</summary>
    private static StructDeclStmt ReadStructDescriptor(BinaryReader r, string[] strings,
        Func<BinaryReader, string[], FuncDecl> readFunc, out string? error)
    {
        error = null;
        var name = ReadWireString(r);
        var access = (AccessModifier)r.ReadInt32();
        var isReadOnly = r.ReadBoolean();
        var fieldCount = r.ReadInt32();
        var fields = new List<FieldDecl>(fieldCount);
        for (var i = 0; i < fieldCount; i++)
        {
            var type = ReadTypeInfo(r); var fname = ReadWireString(r);
            var faccess = (AccessModifier)r.ReadInt32(); var isStatic = r.ReadBoolean();
            fields.Add(new FieldDecl(faccess, type, fname, null, isStatic));
        }
        // The member blocks a struct shares with a class, minus the destructor slot and the static-method split
        // (see ReadMemberBlocks): a struct keeps every method in `Methods`.
        var blocks = ReadMemberBlocks(r, strings, readFunc, withDestructor: false, splitStatics: false);
        var sd = StructDeclStmt.Create(name, fields, access, 0, 0);
        sd.Constructors = blocks.Ctors;
        sd.Methods = blocks.Methods;
        sd.OperatorMethods = blocks.Operators;
        sd.Properties = blocks.Properties;
        sd.IsReadOnly = isReadOnly;
        sd.ImplementedInterfaces = blocks.Implemented;
        return sd;
    }

    /// <summary>The declaration ONE directory entry points at, read WHOLE: its record lives in the parts
    /// section, and the part is length-delimited, so one reader over that slice gives the header and the body
    /// together. The bounds check is the engine's own message rather than a stream exception, because a blob is
    /// script input and a corruption is refused by name (the same rule ReadDeflatedSource follows).</summary>
    private static FuncDecl? ReadPartFunc(byte[] data, int partsBase, int offset, int length, string[] strings,
        out string? error)
    {
        error = null;
        if (offset < 0 || length < 0 || (long)partsBase + offset + length > data.Length)
        {
            error = "a function directory entry points outside the blob";
            return null;
        }
        using var ms = new MemoryStream(data, partsBase + offset, length);
        using var r = new BinaryReader(ms);
        var fd = ReadBundleFunc(r, strings, out error);
        if (fd == null) return null;
        fd.IsVmShell = true;
        MarkNestedLambdasVmShell(fd);
        return fd;
    }

    /// <summary>The DEFAULT VALUES a shell carries: each entry is the parameter index plus a zero-parameter
    /// body holding the compiled default expression, and the loader hands the declaration the LOADED form of
    /// each (CompiledDefaultExpr) at that index, so DefaultValues keeps the shape every binding site reads.
    /// Null when the declaration has none, which is the case for every declaration that declares no defaults
    /// and for a constructor (the language has no constructor defaults).</summary>
    private static Expr?[]? ReadDefaultThunks(BinaryReader r, string[] strings, int paramCount, out string? error)
    {
        error = null;
        var n = r.ReadInt32();
        if (n == 0) return null;
        var values = new Expr?[paramCount];
        for (var i = 0; i < n; i++)
        {
            var pi = r.ReadInt32();
            var thunk = ReadBundleFunc(r, strings, out var thunkError);
            if (thunk == null) { error = thunkError ?? "a carried default value could not be read"; return null; }
            // A bundle body is script INPUT, so a thunk gets the same treatment as every other body: it is
            // verified (ReadBundleFunc) and, if a tree fallback ever reached it, refused by name rather than
            // run as an empty body.
            thunk.IsVmShell = true;
            if (pi >= 0 && pi < values.Length) values[pi] = CompiledDefaultExpr.Create(thunk, thunk.Line, thunk.Column);
        }
        return values;
    }

    // ── bundle func shell (strings referenced via the shared table) ──────
    private static void WriteBundleHeader(BinaryWriter w, BundleFunction sf)
    {
        w.Write(sf.NameIdx);
        w.Write(sf.Params.Length);
        foreach (var (t, ni) in sf.Params) { WriteTypeInfo(w, t); w.Write(ni); }
        WriteBools(w, sf.IsRefParams);
        WriteBools(w, sf.IsParams);
        WriteBools(w, sf.IsOutParams);
        WriteTypeInfo(w, sf.ReturnType);
        w.Write(sf.Line);
        w.Write(sf.Column);
        // THE DECLARATION'S ACCESS, right before the flags byte: a loaded function or constructor was registered as
        // `public` unconditionally, so a private or protected ctor/method was CALLABLE FROM OUTSIDE in a bundle while
        // the file refused it (measured on constructor_test: three checks of the same hole, "private ctor: global call
        // rejected" among them). The property accessors had the same shape in v22, one member kind over.
        w.Write((int)sf.Access);
        // ONE byte, two bits: this slot held `IsStatic` alone, and `IsAsync` is the second fact the wire owes
        // (the body's bytecode can only say so when the body actually awaits). Keeping it the SAME byte is why
        // this change moves no record boundary and why the five verifier fixtures are patched on their version
        // byte alone. Bit 1 set on a blob an older engine read means `IsStatic` read as true, which is why the
        // version moved with it.
        w.Write((byte)((sf.IsStatic ? FlagStatic : 0) | (sf.IsAsync ? FlagAsync : 0) | (sf.IsRefReturn ? FlagRefReturn : 0)));
        // The RETURN tuple's element names, right beside the return type they belong to: the runtime stamps a
        // returned tuple from `FuncDecl.TupleElementNames` (not from the TypeInfo), so a bundle that carried only
        // the type left `Pair().a` answering "Property 'a' not found on tuple" where the file answers 1.
        WriteStringList(w, sf.TupleElementNames);
        WriteTupleParamNames(w, sf.TupleParamNames);
        // The TYPE PARAMETERS, which is what makes a loaded template generic again (see
        // BundleFunction.TypeParameters): without them a bundle called the template itself and the call site's
        // type arguments were dropped, measured on `Identity<long>(42)` (answered "int") and on `Filter<int>`.
        WriteStringList(w, sf.TypeParameters);
        // The carried DEFAULT VALUES, right here in the header: a declaration's arity rules are needed as soon
        // as it is registered, before any body is decoded (the body of a directory entry is decoded lazily).
        // A thunk is written as a nested header+body pair, exactly like a nested lambda.
        w.Write(sf.DefaultThunks.Length);
        for (var i = 0; i < sf.DefaultThunks.Length; i++)
        {
            w.Write(sf.DefaultThunkParams[i]);
            WriteBundleHeader(w, sf.DefaultThunks[i]);
            WriteBundleBody(w, sf.DefaultThunks[i]);
        }
    }

    // ── The ONE body codec ──────────────────────────────────────────────────
    /// <summary>A body is the same field groups in the same order in BOTH layouts, and they disagree about exactly
    /// two things, so each is a parameter here. RECORD: a string travels as an index into the shared string table
    /// (every name is interned while the shells are BUILT, because the table is written before the parts) and a
    /// nested lambda is a full record. SHELL: a string travels INLINE and a nested lambda is the header-less shell.
    /// The token is a TYPE PARAMETER, so the record's indexes and the shell's strings both pass through with no
    /// boxing, and the readers and writers below are the only place this order is stated.
    ///
    /// Why this exists: the body used to be written twice and read twice, and the round that added a PROPERTY
    /// ACCESSOR's own access rule is what that costs. It is a header field, and it had to be edited in four places,
    /// one per writer and one per reader. Groups, in wire order: code, constants, locals, the const-local
    /// flags (as the indexes of the set bits), the global slots, then the nested lambdas.</summary>
    private static void WriteBody<TToken, TNested>(BinaryWriter w, byte[] code, object?[] consts,
        TToken[] locals, bool[]? constLocals, TToken[]? globalSlots, TNested[] nested,
        Action<BinaryWriter, TToken> writeToken, Action<BinaryWriter, TNested> writeNested)
    {
        WriteBytes(w, code);
        WriteConsts(w, consts);
        w.Write(locals.Length);
        foreach (var t in locals) writeToken(w, t);
        WriteConstFlags(w, constLocals);
        w.Write(globalSlots?.Length ?? 0);
        if (globalSlots != null) foreach (var t in globalSlots) writeToken(w, t);
        w.Write(nested.Length);
        foreach (var n in nested) writeNested(w, n);
    }

    /// <summary>The const-local flags as a count followed by the indexes of the set bits, which is how the wire
    /// carries them.</summary>
    private static void WriteConstFlags(BinaryWriter w, bool[]? flags)
    {
        var count = 0;
        if (flags != null) foreach (var f in flags) if (f) count++;
        w.Write(count);
        if (flags == null) return;
        for (var i = 0; i < flags.Length; i++) if (flags[i]) w.Write(i);
    }

    /// <summary>Reads a NESTED LAMBDA for the layout being decoded: may fail, because a record's nested read
    /// reports through an error while a shell's throws (a shell is read inside a library, where a throw already IS
    /// that path's error channel).</summary>
    private delegate TNested? NestedBodyReader<TNested>(BinaryReader r, object? context, out string? error);

    /// <summary>Reads one body TOKEN as a NAME: the record resolves the string-table index it reads against the
    /// table it is handed as <c>context</c>, a shell reads the string inline. The context is a PARAMETER rather
    /// than a captured variable so both adapters are cached static lambdas and a load allocates nothing for them.
    /// Measured, which is why that matters: a capturing adapter cost +2000 B per load of a 4-function bundle, and
    /// resolving the record's indexes into a SECOND array afterwards cost another +1440 B.</summary>
    private delegate string TokenReader(BinaryReader r, object? context);

    /// <summary>What a decoded body holds, filled in the order <see cref="WriteBody"/> wrote it: the ONE result
    /// shape both layouts produce, so no reader can decode a different order than the writer emitted. A null
    /// <c>Nested</c> or <c>GlobalSlots</c> means the section was absent (a count of 0), which is how the record's
    /// reader treated them before this existed.
    /// A STRUCT, returned as a nullable, so handing the six groups back costs no object: as a class the carrier
    /// itself measured at about +550 B per load of a 25-body bundle, which is the whole of what this codec's read
    /// side would otherwise add to a load.</summary>
    private struct DecodedBody<TNested>
    {
        public byte[] Code;
        public object?[] Consts;
        public string[] Locals;
        public bool[]? ConstLocals;
        public string[]? GlobalSlots;
        public TNested[]? Nested;
    }

    private static DecodedBody<TNested>? ReadBody<TNested>(BinaryReader r, object? context, TokenReader readToken,
        NestedBodyReader<TNested> readNested, out string? error)
    {
        error = null;
        DecodedBody<TNested> body;
        body.Code = ReadBytes(r);
        body.Consts = ReadConsts(r);
        var lc = r.ReadInt32();
        body.Locals = new string[lc];
        for (var i = 0; i < lc; i++) body.Locals[i] = readToken(r, context);
        var cc = r.ReadInt32();
        body.ConstLocals = null;
        if (cc > 0)
        {
            body.ConstLocals = new bool[lc];
            for (var i = 0; i < cc; i++) body.ConstLocals[r.ReadInt32()] = true;
        }
        var gsc = r.ReadInt32();
        body.GlobalSlots = null;
        if (gsc > 0)
        {
            body.GlobalSlots = new string[gsc];
            for (var i = 0; i < gsc; i++) body.GlobalSlots[i] = readToken(r, context);
        }
        var nc = r.ReadInt32();
        body.Nested = null;
        if (nc > 0)
        {
            body.Nested = new TNested[nc];
            for (var i = 0; i < nc; i++)
            {
                body.Nested[i] = readNested(r, context, out var nestedError)!;
                if (nestedError != null) { error = nestedError; return null; }
            }
        }
        return body;
    }

    private static void WriteBundleBody(BinaryWriter w, BundleFunction sf)
        => WriteBody(w, sf.Code, sf.Consts, sf.LocalIndexes, sf.ConstLocals,
            sf.GlobalSlotIndexes, sf.NestedLambdas,
            static (b, idx) => b.Write(idx), static (b, f) => WriteBundleFunc(b, f));

    private static void WriteBundleFunc(BinaryWriter w, BundleFunction sf)
    {
        WriteBundleHeader(w, sf);
        WriteBundleBody(w, sf);
    }

    /// <summary>The flags a loaded body carries, derived once from the body's own instructions (see
    /// BytecodeVerifier.Verify's two out-params) plus the one the HEADER states. <c>IsAsync</c> is what routes a CALL
    /// to an `async` body through <c>BeginAsyncCall</c> (the caller receives a Task) and what keeps the VM from
    /// FLAT-CALLING it, which is how a loaded async body reached its first AWAIT with the invocation already
    /// abandoned and the run ended silently; <c>IsCoroutine</c> is what every gate reads for "this call suspends", so
    /// it holds for an await too. The header's bit and the derived one are UNIONED because they answer the same
    /// question from two sides and only the header can answer it for an `async` body with no `await`, while the
    /// derivation still covers the shell layout that has no flags byte (<see cref="ReadFuncShell"/>).</summary>
    private static void ApplyBodyFlags(FuncDecl fd, bool hasYield, bool hasAwait, bool isAsync)
    {
        fd.HasYield = hasYield;
        fd.IsAsync = isAsync || hasAwait;
        fd.IsCoroutine = hasYield || fd.IsAsync;
    }
    /// <summary>Reads ONE record's HEADER: everything a declaration needs to be registered. `ReadBundleFunc`
    /// calls this and then reads the body, and the lazy loader calls it on the PART a directory entry points at,
    /// so there is ONE reader of this layout. It used to have a second, in the directory-entry reader, whose
    /// copy of the flags byte had to be kept in step by hand and once was not.</summary>
    private static FuncDecl? ReadBundleHeader(BinaryReader r, string[] strings, out string? error)
    {
        error = null;
        var nameIdx = r.ReadInt32();
        var name = strings[nameIdx];
        var pn = r.ReadInt32();
        var prms = new (TypeInfo Type, string Name)[pn];
        for (var i = 0; i < pn; i++) { var t = ReadTypeInfo(r); var ni = r.ReadInt32(); prms[i] = (t, strings[ni]); }
        var refs = ReadBools(r);
        var prmsParams = ReadBools(r);
        var outs = ReadBools(r);
        var rt = ReadTypeInfo(r);
        var ln = r.ReadInt32();
        var col = r.ReadInt32();
        // The declaration's ACCESS, which this reader used to assume PUBLIC: a bundle then allowed a private or
        // protected ctor/method from outside while the file refused it (see WriteBundleHeader).
        var access = (AccessModifier)r.ReadInt32();
        // ONE byte, two bits (see WriteBundleHeader). Read as a BYTE rather than a bool so a future flag is a new
        // constant here and in the writer, not a new field.
        var flags = r.ReadByte();
        var isStatic = (flags & FlagStatic) != 0;
        var isAsync = (flags & FlagAsync) != 0;
        var isRefReturn = (flags & FlagRefReturn) != 0;
        var tupleElementNames = ReadStringList(r);
        var tupleParamNames = ReadTupleParamNames(r);
        var typeParams = ReadStringList(r);
        var defaults = ReadDefaultThunks(r, strings, pn, out var defaultsError);
        if (defaultsError != null) { error = defaultsError; return null; }
        var fd = FuncDecl.Create(name, prms, refs, prmsParams, [], rt, null, access, ln, col,
            isOutParams: outs, typeParameters: typeParams);
        fd.IsStatic = isStatic;
        fd.IsAsync = isAsync;
        fd.IsRefReturn = isRefReturn;
        fd.TupleElementNames = tupleElementNames;
        fd.TupleParamNames = tupleParamNames;
        if (defaults != null) fd.DefaultValues = defaults;
        return fd;
    }

    /// <summary>Reads ONE record's BODY and attaches it to the declaration its header produced, verifying it
    /// first: a bundle body is script input, and a corrupted one would otherwise be read as valid-looking
    /// instructions (see BytecodeVerifier). The two flags a coroutine needs are derived from those instructions
    /// and unioned with the header's own byte.</summary>
    private static bool ReadBundleBody(BinaryReader r, string[] strings, FuncDecl fd, out string? error)
    {
        // The record's two adapters, both cached static lambdas: the string table travels as their CONTEXT, and a
        // record token resolves the index it reads against it, so no second array is built either.
        var decoded = ReadBody<FuncDecl>(r, strings,
            static (br, table) => ((string[])table!)[br.ReadInt32()],
            static (BinaryReader br, object? table, out string? nestedError) =>
                ReadBundleFunc(br, (string[])table!, out nestedError), out error);
        if (decoded == null) return false;
        var body = decoded.Value;
        fd.Compiled = new Bytecode(body.Code, body.Consts, body.Locals, body.Nested,
            body.ConstLocals, body.GlobalSlots);
        var vErr = BytecodeVerifier.Verify(fd.Compiled, out var hasYield, out var hasAwait);
        if (vErr != null) { error = $"{fd.Name}: {vErr}"; return false; }
        ApplyBodyFlags(fd, hasYield, hasAwait, fd.IsAsync);
        return true;
    }

    private static FuncDecl? ReadBundleFunc(BinaryReader r, string[] strings, out string? error)
    {
        var fd = ReadBundleHeader(r, strings, out error);
        if (fd == null) return null;
        return ReadBundleBody(r, strings, fd, out error) ? fd : null;
    }

    // ── FuncDecl shell ─────────────────────────────────────────────────────
    private static void WriteFuncShell(BinaryWriter w, FuncDecl f)
    {
        w.Write(f.Params.Count);
        foreach (var (t, name) in f.Params) { WriteTypeInfo(w, t); w.Write(name); }
        WriteBools(w, f.IsRefParams);
        WriteBools(w, f.IsParams);
        WriteBools(w, f.IsOutParams);
        WriteTypeInfo(w, f.ReturnType);
        WriteStringList(w, f.TupleElementNames);
        WriteTupleParamNames(w, f.TupleParamNames);
        // Empty for a lambda (a lambda cannot declare a type parameter), written for LAYOUT PARITY with
        // WriteBundleHeader so the two readers stay mirror images.
        WriteStringList(w, f.TypeParameters);
        w.Write((int)f.Access);   // the declaration's access, the same fact WriteBundleHeader carries
        w.Write(f.Line);
        w.Write(f.Column);
        WriteBytecode(w, f.Compiled!);
    }

    private static FuncDecl ReadFuncShell(BinaryReader r, string name = "")
    {
        var pn = r.ReadInt32();
        var prms = new (TypeInfo Type, string Name)[pn];
        for (var i = 0; i < pn; i++) { var t = ReadTypeInfo(r); var nm = ReadWireString(r); prms[i] = (t, nm); }
        var refs = ReadBools(r);
        var prmsParams = ReadBools(r);
        var outs = ReadBools(r);
        var rt = ReadTypeInfo(r);
        var tupleElementNames = ReadStringList(r);
        var tupleParamNames = ReadTupleParamNames(r);
        var typeParams = ReadStringList(r);
        var shellAccess = (AccessModifier)r.ReadInt32();   // the declaration's access (see WriteFuncShell)
        var ln = r.ReadInt32();
        var col = r.ReadInt32();
        var bc = ReadBytecode(r);
        var fd = FuncDecl.Create(name, prms, refs, prmsParams, [], rt, null, shellAccess, ln, col,
            isOutParams: outs, typeParameters: typeParams);
        fd.TupleElementNames = tupleElementNames;
        fd.TupleParamNames = tupleParamNames;
        fd.Compiled = bc;
        // Library shells carry bodies too, same rule, reported through the ScriptException the
        // callers already wrap ("Bytecode load refused: ...").
        var sErr = BytecodeVerifier.Verify(bc, out var hasYield, out var hasAwait);
        if (sErr != null) throw new ScriptException($"Bytecode load refused for '{name}': {sErr}");
        // false for the header bit: this shell layout carries no flags byte (it is the nested-lambda record), so
        // its async-ness can only come from the derivation, and an `async` lambda is refused by the compiler.
        ApplyBodyFlags(fd, hasYield, hasAwait, isAsync: false);
        return fd;
    }

    // ── Bytecode ────────────────────────────────────────────────────────────
    /// <summary>The SHELL layout: the same body as <see cref="WriteBundleBody"/> with its strings INLINE and its
    /// nested lambdas as header-less shells, so the body order lives in one place (<see cref="WriteBody"/>).</summary>
    private static void WriteBytecode(BinaryWriter w, Bytecode bc)
        => WriteBody(w, bc.Code, bc.Constants, bc.Locals, bc.ConstLocals,
            bc.GlobalSlotNames, bc.NestedLambdas ?? [],
            static (b, s) => b.Write(s), static (b, f) => WriteFuncShell(b, f));

    private static Bytecode ReadBytecode(BinaryReader r)
    {
        var decoded = ReadBody<FuncDecl>(r, null, static (br, _) => ReadWireString(br),
            static (BinaryReader br, object? _, out string? e) => { e = null; return ReadFuncShell(br); }, out var error);
        // A shell's nested read THROWS rather than reporting, so this arm is defensive: a library is loaded
        // through the throw channel, and the message map owns the wording either way.
        if (decoded == null)
            throw new ScriptException($"Bytecode load refused: {error ?? "a nested body did not decode"}");
        var body = decoded.Value;
        return new Bytecode(body.Code, body.Consts, body.Locals, body.Nested, body.ConstLocals,
            body.GlobalSlots);
    }

    // ── Consts ──────────────────────────────────────────────────────────────
    /// <summary>The KIND BYTE every constant starts with. Named rather than spelled at each site: the number is the WIRE
    /// (an older engine reads it back in its own switch), so it is stated once here and read by both directions. A new
    /// kind takes the next free number and owes a BundleVersion bump, because an older engine meets it as an unknown
    /// kind rather than as a value it can skip.</summary>
    private static class ConstKind
    {
        public const int Null = 0;
        public const int Byte = 1;
        public const int SByte = 2;
        public const int Int16 = 3;
        public const int UInt16 = 4;
        public const int Int32 = 5;
        public const int UInt32 = 6;
        public const int Int64 = 7;
        public const int UInt64 = 8;
        public const int Double = 9;
        public const int Single = 10;
        public const int Decimal = 11;
        public const int Boolean = 12;
        public const int Char = 13;
        public const int String = 14;
        public const int TypeInfo = 15;
        public const int NamedArgs = 16;
        public const int TypeArgs = 17;
        public const int ArgFlags = 18;
        public const int TupleNames = 19;
        public const int PatternText = 20;
        public const int RefResult = 21;
    }

    private static void WriteConsts(BinaryWriter w, object?[] consts)
    {
        w.Write(consts.Length);
        foreach (var c in consts)
        {
            switch (c)
            {
                case null: w.Write((byte)ConstKind.Null); break;
                case byte b: w.Write((byte)ConstKind.Byte); w.Write(b); break;
                case sbyte sb: w.Write((byte)ConstKind.SByte); w.Write(sb); break;
                case short s: w.Write((byte)ConstKind.Int16); w.Write(s); break;
                case ushort us: w.Write((byte)ConstKind.UInt16); w.Write(us); break;
                case int i: w.Write((byte)ConstKind.Int32); w.Write(i); break;
                case uint ui: w.Write((byte)ConstKind.UInt32); w.Write(ui); break;
                case long l: w.Write((byte)ConstKind.Int64); w.Write(l); break;
                case ulong ul: w.Write((byte)ConstKind.UInt64); w.Write(ul); break;
                case double d: w.Write((byte)ConstKind.Double); w.Write(d); break;
                case float f: w.Write((byte)ConstKind.Single); w.Write(f); break;
                case decimal m: w.Write((byte)ConstKind.Decimal); w.Write(m); break;
                case bool b: w.Write((byte)ConstKind.Boolean); w.Write(b); break;
                case char ch: w.Write((byte)ConstKind.Char); w.Write(ch); break;
                case string s: w.Write((byte)ConstKind.String); w.Write(s); break;
                case TypeInfo t: w.Write((byte)ConstKind.TypeInfo); WriteTypeInfo(w, t); break;
                // A call whose result is used as a REFERENCE (see RefResultMarker): a kind of its own with no payload,
                // because the marker carries nothing but its own presence.
                case RefResultMarker:
                    w.Write((byte)ConstKind.RefResult);
                    break;
                case NamedArgsMarker marker:
                    w.Write((byte)ConstKind.NamedArgs);
                    var names = marker.Names;
                    w.Write(names?.Count ?? 0);
                    if (names != null)
                        foreach (var n in names) w.Write(n ?? "");
                    break;
                // A call's TYPE ARGUMENTS, carried the way named arguments are. Without a serialized form the
                // export path would refuse any script that names type arguments ("unsupported const type"), and a
                // bundle written here has to decode back into the closed call the compiler meant.
                case TypeArgsMarker typeArgsMarker:
                    w.Write((byte)ConstKind.TypeArgs);
                    w.Write(typeArgsMarker.TypeArgs.Count);
                    foreach (var typeArg in typeArgsMarker.TypeArgs) WriteTypeInfo(w, typeArg);
                    break;
                // A call's REF/OUT argument flags, carried the way named arguments are: without a serialized form
                // the export path would refuse any script with such a call ("unsupported const type"), and a
                // bundle has to decode back into the call the compiler meant (see ArgFlagsMarker).
                case ArgFlagsMarker argFlags:
                    w.Write((byte)ConstKind.ArgFlags);
                    w.Write(argFlags.Refs.Length);
                    for (var ai = 0; ai < argFlags.Refs.Length; ai++)
                        w.Write((byte)((argFlags.Refs[ai] ? 1 : 0)
                                     | (ai < argFlags.Outs.Length && argFlags.Outs[ai] ? 2 : 0)));
                    break;
                // A TUPLE's element NAMES, which the compiler puts in the const pool for ATTACH_TUPLE_NAMES
                // (`ctx.AddConst(vd.TupleElementNames)`, whose runtime type is a `List<string>`), and which the VM
                // reads back as an `IReadOnlyList<string>`. Without a kind the export refused any file that
                // declares a TUPLE-TYPED variable, with the raw "unsupported const type List`1": measured on
                // t4_struct, crash_json_serialize, pattern_vm_parity and _probe7. An empty name travels as "",
                // the convention the named-args marker already uses (both mean "no name here").
                case IReadOnlyList<string> tupleNames:
                    w.Write((byte)ConstKind.TupleNames);
                    w.Write(tupleNames.Count);
                    foreach (var n in tupleNames) w.Write(n ?? "");
                    break;
                // A MATCH PATTERN, carried as its own SOURCE TEXT. A `Pattern` is an AST fragment holding arbitrary
                // EXPRESSIONS (a ConstantPattern compares against one, a RelationalPattern against another), so there
                // is no value form for one, and the engine's own parser is the ONE thing that knows what a pattern
                // means: the loader hands the text back to it (Parser.ParsePatternFromText). Without this kind the
                // export failed IN THE CODEC, with or without --no-source, so a file whose function holds a `match`
                // could not be compiled at all (pattern_vm_parity, the corpus's single codec refusal).
                case PatternEngineDescriptor patternDescriptor:
                    {
                        if (patternDescriptor.PatternText is not { Length: > 0 } patternText)
                            throw new InvalidOperationException(
                                "VmBytecodeCodec: this match pattern has no source text, so a bundle cannot carry it"
                                + " (the file was parsed without its text): run the file, or compile it from its source");
                        w.Write((byte)ConstKind.PatternText);
                        w.Write(patternText);
                        var bindNames = patternDescriptor.Names;
                        w.Write(bindNames.Length);
                        foreach (var n in bindNames) w.Write(n ?? "");
                        w.Write(patternDescriptor.LocalIdxs.Length);
                        w.Write(patternDescriptor.LocalIdxs);
                        break;
                    }
                default:
                    throw new InvalidOperationException("VmBytecodeCodec: unsupported const type " + c.GetType().Name);
            }
        }
    }

    private static object?[] ReadConsts(BinaryReader r)
    {
        var n = r.ReadInt32();
        var arr = new object?[n];
        for (var i = 0; i < n; i++)
        {
            var kind = r.ReadByte();
            arr[i] = kind switch
            {
                ConstKind.Null => null,
                ConstKind.Byte => r.ReadByte(),
                ConstKind.SByte => r.ReadSByte(),
                ConstKind.Int16 => r.ReadInt16(),
                ConstKind.UInt16 => r.ReadUInt16(),
                ConstKind.Int32 => r.ReadInt32(),
                ConstKind.UInt32 => r.ReadUInt32(),
                ConstKind.Int64 => r.ReadInt64(),
                ConstKind.UInt64 => r.ReadUInt64(),
                ConstKind.Double => r.ReadDouble(),
                ConstKind.Single => r.ReadSingle(),
                ConstKind.Decimal => r.ReadDecimal(),
                ConstKind.Boolean => r.ReadBoolean(),
                ConstKind.Char => r.ReadChar(),
                ConstKind.String => ReadWireString(r),
                ConstKind.TypeInfo => ReadTypeInfo(r),
                ConstKind.NamedArgs => ReadNamedArgsMarker(r),
                ConstKind.TypeArgs => ReadTypeArgsMarker(r),
                ConstKind.ArgFlags => ReadArgFlagsMarker(r),
                ConstKind.TupleNames => ReadTupleNames(r),
                ConstKind.PatternText => ReadPatternDescriptor(r),
                ConstKind.RefResult => RefResultMarker.Instance,
                _ => throw new InvalidOperationException("VmBytecodeCodec: bad const kind " + kind)
            };
        }
        return arr;
    }

    /// <summary>A match pattern, written by const kind 20 as its own SOURCE TEXT (see the writer, and
    /// Parser.ParsePatternFromText): the same parser that produced the AST rebuilds it, so the pattern the loaded body
    /// matches against is the one the compiler compiled. The binding names and their local slots travel beside it,
    /// because the compiler allocated those slots in the body being read.</summary>
    private static PatternEngineDescriptor ReadPatternDescriptor(BinaryReader r)
    {
        var text = ReadWireString(r);
        var nameCount = r.ReadInt32();
        var names = new string[nameCount];
        for (var i = 0; i < nameCount; i++) names[i] = ReadWireString(r);
        var localIdxs = r.ReadBytes(r.ReadInt32());
        return new PatternEngineDescriptor(Parser.ParsePatternFromText(text), names, localIdxs, text);
    }

    /// <summary>A tuple's element names, written by const kind 19. Read back as a `List<string>`, which is what
    /// the VM's ATTACH_TUPLE_NAMES arm pattern-matches (`is IReadOnlyList<string>`), so a loaded body attaches the
    /// same names the compiler attached from the script's own declaration.</summary>
    private static IReadOnlyList<string> ReadTupleNames(BinaryReader r)
    {
        var count = r.ReadInt32();
        var names = new List<string>(count);
        for (var i = 0; i < count; i++) names.Add(ReadWireString(r));
        return names;
    }

    private static NamedArgsMarker ReadNamedArgsMarker(BinaryReader r)
    {
        var count = r.ReadInt32();
        var names = new string?[count];
        for (var i = 0; i < count; i++)
        {
            var s = ReadWireString(r);
            names[i] = s.Length == 0 ? null : s;
        }
        return new NamedArgsMarker(names);
    }

    private static TypeArgsMarker ReadTypeArgsMarker(BinaryReader r)
    {
        var count = r.ReadInt32();
        var typeArgs = new TypeInfo[count];
        for (var i = 0; i < count; i++) typeArgs[i] = ReadTypeInfo(r);
        return new TypeArgsMarker(typeArgs);
    }

    private static ArgFlagsMarker ReadArgFlagsMarker(BinaryReader r)
    {
        var count = r.ReadInt32();
        var refs = new bool[count];
        var outs = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var bits = r.ReadByte();
            refs[i] = (bits & 1) != 0;
            outs[i] = (bits & 2) != 0;
        }
        return new ArgFlagsMarker(refs, outs);
    }

    // ── TypeInfo ────────────────────────────────────────────────────────────
    private static void WriteTypeInfo(BinaryWriter w, TypeInfo t)
    {
        w.Write((int)t.BaseType);
        w.Write(t.IsNullable);
        w.Write(t.ArrayRank);
        w.Write(t.JaggedDepth);
        w.Write(t.TypeName ?? "");
        if (t.GenericArgs is { } ga)
        {
            w.Write(ga.Count);
            foreach (var a in ga) WriteTypeInfo(w, a);
        }
        else w.Write(0);
        // A TUPLE's ELEMENT TYPES and their NAMES are part of what the type IS, and leaving them out made a
        // tuple-typed value LOSE its element type through a bundle: `TypeInfo.IsTupleType` reads exactly this
        // list, so a loaded `List<(int, int)>` came back as a list of anything, `CoerceTo` had no tuple arm to
        // reach (every element assignment stopped being checked), and `StampTupleElementNames` could not name a
        // returned tuple's elements. Measured: `var tl = new List<(int, int)>(); tl.Add(("a", 1));` THREW from a
        // source run and silently accepted the element from a stripped bundle, and the same degradation broke
        // `Dictionary<string, (int, int)>` values. Recursive like the generic arguments, because a tuple element
        // can itself be a tuple.
        if (t.TupleElementTypes is { Count: > 0 } tet)
        {
            w.Write(tet.Count);
            foreach (var e in tet) WriteTypeInfo(w, e);
        }
        else w.Write(0);
        WriteStringList(w, t.TupleElementNames);
    }

    /// <summary>A nullable list of strings, written as a count plus the entries (an absent NAME travels as "",
    /// the convention the named-args marker already uses). ONE implementation, because both the RETURN tuple's
    /// element names and a TypeInfo's tuple element names are this exact shape.</summary>
    private static void WriteStringList(BinaryWriter w, IReadOnlyList<string>? names)
    {
        if (names is { Count: > 0 })
        {
            w.Write(names.Count);
            foreach (var n in names) w.Write(n ?? "");
        }
        else w.Write(0);
    }

    /// <summary>Reads ONE length-prefixed string of the wire, BOUNDED by what is left in the blob.
    ///
    /// `BinaryReader.ReadString` reads a 7-bit length and then allocates it, so a length that no writer of this engine
    /// could have produced asks the runtime for that many bytes: a desynchronised descriptor stream (a field the writer
    /// did not write, i.e. a reader/writer drift, which is the shape a wire change gets wrong) therefore surfaced as
    /// `Out of memory: the runtime could not honour an allocation` on a blob of a few hundred bytes. A length past the
    /// bytes that remain cannot be a string of this bundle's, so it is refused as what it is: the streams are out of
    /// phase, named with the byte offset where the read began. The encoding is exactly `BinaryReader.ReadString`'s
    /// (a 7-bit big-endian length then UTF-8), so the WIRE is unchanged and a blob written by any version reads the
    /// same; only a length that cannot be honest is refused sooner and in the engine's own words.</summary>
    private static string ReadWireString(BinaryReader r)
    {
        var start = r.BaseStream.Position;
        var length = 0;
        var shift = 0;
        while (true)
        {
            var b = r.ReadByte();
            length |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) throw Desynchronised(r, start, length);
        }
        var remaining = r.BaseStream.Length - r.BaseStream.Position;
        if (length < 0 || length > remaining) throw Desynchronised(r, start, length);
        return length == 0 ? "" : Encoding.UTF8.GetString(r.ReadBytes(length));
    }

    /// <summary>The refusal a desynchronised stream raises, naming the position and the length it was asked for, and
    /// putting the blame where it belongs: a BUNDLE whose sections do not line up is an engine defect or a crafted blob,
    /// never a mistake in the script, and it must not read like a memory shortage.</summary>
    private static ScriptException Desynchronised(BinaryReader r, long start, int declared)
        => new($"the bundle's descriptor stream is out of phase: a string at byte {start} declares {declared} byte(s)"
            + $" while {Math.Max(0, r.BaseStream.Length - r.BaseStream.Position)} remain. A reader and a writer of this"
            + " format disagree about a field, or the blob was edited by hand; a bundle from this engine cannot be in"
            + " this state, so rebuild it with --compile and report it if the rebuilt one is refused too");

    private static IReadOnlyList<string>? ReadStringList(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n <= 0) return null;
        var names = new List<string>(n);
        for (var i = 0; i < n; i++) names.Add(ReadWireString(r));
        return names;
    }

    /// <summary>A tuple PARAMETER's element names: one entry per parameter, in the parameter's own order, each
    /// written as a string list (count 0 for a parameter that is not a tuple). Kept index-aligned rather than as
    /// a name/value map because that is the shape `FuncDecl.TupleParamNames` has, so the loader hands it back
    /// unchanged and `BindTupleParam` reads it exactly as it reads the parser's.</summary>
    private static void WriteTupleParamNames(BinaryWriter w, IReadOnlyList<IReadOnlyList<string>?>? names)
    {
        if (names is { Count: > 0 })
        {
            w.Write(names.Count);
            foreach (var n in names) WriteStringList(w, n);
        }
        else w.Write(0);
    }

    private static IReadOnlyList<IReadOnlyList<string>?>? ReadTupleParamNames(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n <= 0) return null;
        var list = new List<IReadOnlyList<string>?>(n);
        for (var i = 0; i < n; i++) list.Add(ReadStringList(r));
        return list;
    }

    private static TypeInfo ReadTypeInfo(BinaryReader r)
    {
        var bt = (TokenType)r.ReadInt32();
        var nu = r.ReadBoolean();
        var ar = r.ReadInt32();
        var jd = r.ReadInt32();
        var tnRaw = ReadWireString(r);
        var tn = tnRaw.Length == 0 ? null : tnRaw;
        var gn = r.ReadInt32();
        TypeArgList? ga = null;
        if (gn > 0)
        {
            var list = new List<TypeInfo>(gn);
            for (var i = 0; i < gn; i++) list.Add(ReadTypeInfo(r));
            ga = [with(list)];
        }
        var en = r.ReadInt32();
        IReadOnlyList<TypeInfo>? tet = null;
        if (en > 0)
        {
            var elems = new List<TypeInfo>(en);
            for (var i = 0; i < en; i++) elems.Add(ReadTypeInfo(r));
            tet = elems;
        }
        var nn = r.ReadInt32();
        IReadOnlyList<string>? ten = null;
        if (nn > 0)
        {
            var names = new List<string>(nn);
            for (var i = 0; i < nn; i++) names.Add(ReadWireString(r));
            ten = names;
        }
        return new TypeInfo(bt, nu, ar, jd, tn, ga, tet, ten);
    }

    // ── Script values (variable / static-field snapshots) ───────────────────
    /// <summary>Serializes a variable/static-field VALUE snapshot. Supported kinds: null, bool,
    /// long/int/double (written as long or double by runtime type), string, array (object[] or
    /// script list), dictionary (IDictionary or script dictionary), tuple (ScriptTuple). Anything
    /// else throws InvalidDataException with the value's type name (caller converts to a
    /// ScriptException).</summary>
    private static void WriteScriptValue(BinaryWriter w, object? value)
    {
        switch (value)
        {
            case null: w.Write((byte)ConstKind.Null); break;
            case bool b: w.Write((byte)1); w.Write(b); break;
            case long l: w.Write((byte)2); w.Write(l); break;
            case int i: w.Write((byte)2); w.Write((long)i); break;
            case short s: w.Write((byte)2); w.Write((long)s); break;
            case byte by: w.Write((byte)2); w.Write((long)by); break;
            case double d: w.Write((byte)3); w.Write(d); break;
            case float f: w.Write((byte)3); w.Write((double)f); break;
            case string str: w.Write((byte)4); w.Write(str); break;
            case object[] arr:
                w.Write((byte)5); w.Write(arr.Length);
                foreach (var item in arr) WriteScriptValue(w, item);
                break;
            case IScriptList list:
                {
                    // script `[1,2,3]` literal → IScriptList; serialize as an object[] array
                    var items = list.ToArray();
                    w.Write((byte)5); w.Write(items.Length);
                    foreach (var item in items) WriteScriptValue(w, item);
                    break;
                }
            case IDictionary dict:
                w.Write((byte)6); w.Write(dict.Count);
                foreach (DictionaryEntry de in dict)
                { WriteScriptValue(w, de.Key); WriteScriptValue(w, de.Value); }
                break;
            case IScriptDictionary sd:
                {
                    // script `{ "k": v }` literal → IScriptDictionary (does not implement IDictionary
                    // directly; enumerate through its IDictionary view)
                    var d = sd.Dictionary;
                    w.Write((byte)6); w.Write(d.Count);
                    foreach (DictionaryEntry de in d)
                    { WriteScriptValue(w, de.Key); WriteScriptValue(w, de.Value); }
                    break;
                }
            case ScriptTuple tup:
                w.Write((byte)7); w.Write(tup.Items.Length);
                foreach (var item in tup.Items) WriteScriptValue(w, item);
                break;
            default:
                throw new InvalidDataException($"value of type {value.GetType().Name} cannot be serialized");
        }
    }

    private static object? ReadScriptValue(BinaryReader r)
    {
        switch (r.ReadByte())
        {
            case 0: return null;
            case 1: return r.ReadBoolean();
            case 2: return r.ReadInt64();
            case 3: return r.ReadDouble();
            case 4: return ReadWireString(r);
            case 5: { var n = r.ReadInt32(); var a = new object?[n]; for (var i = 0; i < n; i++) a[i] = ReadScriptValue(r); return a; }
            case 6: { var n = r.ReadInt32(); var d = new Dictionary<object, object?>(); for (var i = 0; i < n; i++) { var k = ReadScriptValue(r)!; d[k] = ReadScriptValue(r); } return d; }
            case 7: { var n = r.ReadInt32(); var items = new object?[n]; for (var i = 0; i < n; i++) items[i] = ReadScriptValue(r); return new ScriptTuple(items); }
            default: throw new InvalidDataException("unknown value kind");
        }
    }

    /// <summary>Writes a class descriptor: name, parent, access, record flag, fields, properties,
    /// methods/operator-methods/destructor (as compiled bundle functions via pre-built shells),
    /// compiled constructors (via pre-built ctor shells), nested-type counts, and static-field
    /// value snapshots via writeValue. Mirrored by ReadClassDescriptor.</summary>
    private static void WriteClassDescriptor(BinaryWriter w, ClassInfo c, DescriptorShells shells, Action<object?> writeValue)
    {
        w.Write(c.Name);
        w.Write(c.ParentName ?? "");
        w.Write((int)c.Access);
        w.Write(c.IsRecord);
        // fields: count | (type|name|access|isStatic|hasInitializer|thunk-header+body?)…
        w.Write(c.Fields.Count); for (var fi = 0; fi < c.Fields.Count; fi++)
        {
            var f = c.Fields[fi];
            WriteTypeInfo(w, f.Type); w.Write(f.Name); w.Write((int)f.Access); w.Write(f.IsStatic);
            // An INSTANCE field's initializer rides here as a compiled zero-parameter body, the shape a parameter
            // default already travels in: a class descriptor that dropped it would build an instance whose fields
            // read their type defaults while the bundle claims to BE the script (measured: the strip refused such a
            // file for exactly that reason, and it carries it now).
            var thunk = fi < shells.FieldInitializers.Length ? shells.FieldInitializers[fi] : null;
            w.Write(thunk != null);
            if (thunk != null) { WriteBundleHeader(w, thunk); WriteBundleBody(w, thunk); }
        }
        // The member blocks a class and a struct share, written in ONE place (see WriteMemberBlocks): the
        // implemented interfaces, the properties, the methods, the operators, the destructor and the ctors.
        WriteMemberBlocks(w, c.ImplementedInterfaces, c.Properties, shells, withDestructor: true);
        // nested types: counts only, recursion over NestedClasses/NestedStructs is out of scope
        w.Write(c.NestedClasses.Count);
        w.Write(c.NestedStructs.Count);
        // static values: name → value, written with writeValue (ScriptFunction static-method
        // snapshots are skipped, the method itself is serialized in the methods section above)
        w.Write(c.StaticFields?.Count(kv => kv.Value is not ScriptFunction) ?? 0);
        if (c.StaticFields != null)
            foreach (var kv in c.StaticFields)
                if (kv.Value is not ScriptFunction) { w.Write(kv.Key); writeValue(kv.Value); }
        // events: count | (name|delegateTypeName|access)…, the LAST section of a class descriptor.
        // A class descriptor dropped these ENTIRELY, so a loaded instance had no events at all: measured,
        // `class_features_recap`'s `event ValueChanged OnChange` answered "Field or event 'OnChange' not found in
        // class 'EventSource'" from a stripped bundle where the file subscribed and fired it. Three fields are all
        // it takes, because the instance side is rebuilt from `ClassInfo.Events` BY NAME (`GetEventInfo` and
        // `FindEventInHierarchy` both read it) and the handler's signature is checked against the delegate NAME.
        w.Write(c.Events.Count);
        foreach (var ev in c.Events)
        {
            w.Write(ev.Name); w.Write(ev.DelegateTypeName); w.Write((int)ev.Access);
        }
    }

    /// <summary>ONE property entry: name, type, access, static, then EACH ACCESSOR's own rule (a present flag, its
    /// access and its init flag) and the backing field's name.
    ///
    /// This block is the round that justifies the pair below. v22 gave each accessor its OWN access modifier and
    /// init flag, which is a field per accessor, and the change had to be made in FOUR places (both writers and both
    /// readers) or a bundle would rebuild a private setter as PUBLIC: measured on `crash_properties_test` as 84
    /// passed / 0 failed from the file against 79 / 5 from a stripped bundle, because `{ get; private set; }` and
    /// `{ get; init; }` are exactly the two shapes whose SETTER differs from the property. An access modifier is not
    /// a fact a reader may guess at.</summary>
    private static void WritePropertyEntry(BinaryWriter w, PropertyDecl p)
    {
        w.Write(p.Name); WriteTypeInfo(w, p.Type); w.Write((int)p.Access); w.Write(p.IsStatic);
        w.Write(p.Getter != null);
        if (p.Getter != null) { w.Write((int)p.Getter.Access); w.Write(p.Getter.IsInit); }
        w.Write(p.Setter != null);
        if (p.Setter != null) { w.Write((int)p.Setter.Access); w.Write(p.Setter.IsInit); }
        w.Write(p.Name); // backing field name == property name (existing convention)
    }

    /// <summary>The member blocks a CLASS and a STRUCT descriptor share, in the order both write them: the
    /// implemented interfaces (FLATTENED, because a GENERIC contract is matched by DECLARATION only), the
    /// properties, the methods, the operator methods, optionally the destructor, and the constructors.
    ///
    /// What the two descriptors do NOT share is their HEADER and their FIELD block, and that is not duplication: an
    /// instance field's initializer rides as a compiled thunk and a struct has none BY DECISION (the interpreter
    /// ignores a struct field's initializer, so carrying one would make a bundle build an instance the file does
    /// not). A struct also has no destructor slot, which is what <paramref name="withDestructor"/> selects: the flag
    /// chooses WHICH wire is written and is not itself written, so each descriptor emits exactly its old bytes.</summary>
    private static void WriteMemberBlocks(BinaryWriter w, IReadOnlyList<string> implemented,
        IReadOnlyList<PropertyDecl> properties, DescriptorShells shells, bool withDestructor)
    {
        w.Write(implemented.Count);
        foreach (var ifaceName in implemented) w.Write(ifaceName);
        w.Write(properties.Count);
        foreach (var p in properties) WritePropertyEntry(w, p);
        w.Write(shells.Methods.Length);
        foreach (var m in shells.Methods) WriteBundleFunc(w, m);
        w.Write(shells.Operators.Length);
        foreach (var (key, sh) in shells.Operators) { w.Write((int)key); WriteBundleFunc(w, sh); }
        if (withDestructor)
        {
            w.Write(shells.Destructor != null);
            if (shells.Destructor != null) WriteBundleFunc(w, shells.Destructor);
        }
        w.Write(shells.Ctors.Length);
        foreach (var ctor in shells.Ctors) WriteBundleFunc(w, ctor);
    }

    /// <summary>The member blocks both descriptors READ, as ONE carrier rather than eight out-parameters. A struct
    /// is the same sections minus the destructor slot, and the STATIC-METHOD SPLIT belongs to the class shape alone
    /// (a class keeps its statics apart from its instance methods, a struct keeps them all in `Methods`), which is
    /// what <paramref name="splitStatics"/> selects. Neither flag is read from the wire: each descriptor reads
    /// exactly the sections its own writer emitted.</summary>
    private struct MemberBlocks
    {
        public List<string> Implemented;
        public List<PropertyDecl> Properties;
        public List<FuncDecl> Methods;
        public List<FuncDecl>? StaticMethods;
        public Dictionary<TokenType, FuncDecl> Operators;
        public FuncDecl? Destructor;
        public List<ConstructorDecl> Ctors;
    }

    /// <summary>One property entry, as <see cref="WritePropertyEntry"/> wrote it.</summary>
    private static PropertyDecl ReadPropertyEntry(BinaryReader r)
    {
        var pname = ReadWireString(r); var ptype = ReadTypeInfo(r);
        var paccess = (AccessModifier)r.ReadInt32(); var pisStatic = r.ReadBoolean();
        var hasGet = r.ReadBoolean();
        AccessorDecl? getAcc = null;
        if (hasGet) getAcc = new AccessorDecl((AccessModifier)r.ReadInt32(), r.ReadBoolean());
        var hasSet = r.ReadBoolean();
        AccessorDecl? setAcc = null;
        if (hasSet) setAcc = new AccessorDecl((AccessModifier)r.ReadInt32(), r.ReadBoolean());
        _ = ReadWireString(r); // backing name (== property name)
        return PropertyDecl.Create(pname, ptype, paccess, pisStatic, getAcc, setAcc, null, 0, 0);
    }

    private static MemberBlocks ReadMemberBlocks(BinaryReader r, string[] strings,
        Func<BinaryReader, string[], FuncDecl> readFunc, bool withDestructor, bool splitStatics)
    {
        var ifaceCount = r.ReadInt32();
        var implemented = new List<string>(ifaceCount);
        for (var i = 0; i < ifaceCount; i++) implemented.Add(ReadWireString(r));
        var propCount = r.ReadInt32();
        var properties = new List<PropertyDecl>(propCount);
        for (var i = 0; i < propCount; i++) properties.Add(ReadPropertyEntry(r));
        var methodCount = r.ReadInt32();
        var methods = new List<FuncDecl>(methodCount);
        var staticMethods = splitStatics ? new List<FuncDecl>() : null;
        for (var i = 0; i < methodCount; i++)
        {
            var m = readFunc(r, strings);
            if (staticMethods != null && m.IsStatic) staticMethods.Add(m); else methods.Add(m);
        }
        var opCount = r.ReadInt32();
        var ops = new Dictionary<TokenType, FuncDecl>();
        for (var i = 0; i < opCount; i++) ops[(TokenType)r.ReadInt32()] = readFunc(r, strings);
        FuncDecl? dtor = null;
        if (withDestructor && r.ReadBoolean()) dtor = readFunc(r, strings);
        var ctorCount = r.ReadInt32();
        var ctors = new List<ConstructorDecl>(ctorCount);
        for (var i = 0; i < ctorCount; i++)
        {
            var fd = readFunc(r, strings);
            // ConstructorDecl has no name; the FuncDecl shell carries params + compiled body.
            ctors.Add(new ConstructorDecl(fd.Params, fd.IsRefParams, fd.Body, fd.Access, fd.Line, fd.Column, fd.IsOutParams, fd.IsParams)
            { Compiled = fd.Compiled, IsStatic = fd.IsStatic });
        }
        var blocks = new MemberBlocks
        {
            Implemented = implemented,
            Properties = properties,
            Methods = methods,
            StaticMethods = staticMethods,
            Operators = ops,
            Destructor = dtor,
            Ctors = ctors
        };
        return blocks;
    }

    /// <summary>Writes the v6 DECLARATIONS section: one entry per declaration, each tagged with its kind, in a
    /// fixed order (enums, delegates, interfaces, extensions). Everything a kind needs to rebuild the registry
    /// state its visitor builds is here, and nothing that cannot be reproduced is: an enum carries RESOLVED
    /// values, an extension carries its members as shells (bodies in the shared table), and the loader refuses
    /// nothing here because the STRIP refuses the shapes this section cannot carry (an interface with a default
    /// body, an extension with a property) rather than writing a bundle that would differ from its script.</summary>
    private static void WriteDeclarations(BinaryWriter w, LibraryDeclarations decls, DescriptorShells[] extShells,
        InterfaceShells[] ifaceShells)
    {
        // An IMPORT travels as the statement's own data (path, alias, position): the loader replays it through the
        // same visitor a source run reaches, so the module is loaded and bound before the entry runs.
        foreach (var im in decls.Imports)
        {
            w.Write((byte)DeclarationKind.Import);
            w.Write(im.Path);
            w.Write(im.Alias != null);
            if (im.Alias != null) w.Write(im.Alias);
            w.Write(im.Line);
            w.Write(im.Column);
        }
        foreach (var e in decls.Enums)
        {
            w.Write((byte)DeclarationKind.Enum);
            w.Write(e.Name);
            w.Write((int)e.Access);
            w.Write(e.Line);
            w.Write(e.Column);
            w.Write(e.Members.Count);
            foreach (var (name, value) in e.Members) { w.Write(name); w.Write(value); }
        }
        foreach (var d in decls.Delegates)
        {
            w.Write((byte)DeclarationKind.Delegate);
            w.Write(d.Name);
            w.Write((int)d.Access);
            WriteTypeInfo(w, d.ReturnType);
            w.Write(d.Parameters.Count);
            foreach (var (type, name) in d.Parameters) { WriteTypeInfo(w, type); w.Write(name); }
            WriteBools(w, d.IsRefParams);
            WriteBools(w, d.IsOutParams);
            w.Write(d.Line);
            w.Write(d.Column);
        }
        for (var idx = 0; idx < decls.Interfaces.Count; idx++)
        {
            var i = decls.Interfaces[idx];
            w.Write((byte)DeclarationKind.Interface);
            w.Write(i.Name);
            w.Write(i.TypeParameters.Count);
            foreach (var tp in i.TypeParameters) w.Write(tp);
            w.Write(i.Parents.Count);
            foreach (var p in i.Parents) w.Write(p);
            w.Write(i.Members.Count);
            foreach (var (name, arity) in i.Members) { w.Write(name); w.Write(arity); }
            // A DEFAULT BODY travels as a shell, keyed the way the registration keys it (`name/arity`), and a
            // PROPERTY travels with whichever accessor bodies it declares: both are what a class that does not
            // implement the member gets, so a bundle without them would answer differently from its file.
            var shells = ifaceShells[idx];
            w.Write(i.Defaults.Count);
            for (var di = 0; di < i.Defaults.Count; di++)
            {
                w.Write(i.Defaults[di].Key);
                WriteBundleHeader(w, shells.Defaults[di]);
                WriteBundleBody(w, shells.Defaults[di]);
            }
            w.Write(i.Properties.Count);
            for (var pi = 0; pi < i.Properties.Count; pi++)
            {
                var prop = i.Properties[pi];
                w.Write(prop.Name); WriteTypeInfo(w, prop.Type);
                w.Write(prop.HasGetter); w.Write(prop.HasSetter);
                var (getter, setter) = shells.Properties[pi];
                w.Write(getter != null);
                if (getter != null) { WriteBundleHeader(w, getter); WriteBundleBody(w, getter); }
                w.Write(setter != null);
                if (setter != null) { WriteBundleHeader(w, setter); WriteBundleBody(w, setter); }
            }
        }
        for (var idx = 0; idx < decls.Extensions.Count; idx++)
        {
            var x = decls.Extensions[idx];
            w.Write((byte)DeclarationKind.Extension);
            WriteTypeInfo(w, x.Target);
            w.Write(x.TypeParameters.Count);
            foreach (var tp in x.TypeParameters) w.Write(tp);
            w.Write(x.ContainerName ?? "");
            w.Write(x.ReceiverName ?? "");
            w.Write(extShells[idx].Methods.Length);
            foreach (var m in extShells[idx].Methods) WriteBundleFunc(w, m);
        }
        // The TEXT-CARRIED declarations, written LAST so the loader can register them once the enums, delegates,
        // interfaces and extensions above exist (a declaration may name one of them: an `implements`, a field type,
        // a parameter). A generic class and a top-level function whose body the compiler did not take share this
        // entry because the loader only ever re-parses and executes. An entry with no text is defensive rather than
        // a supported shape: the export refuses a declaration it has no text for, so a blob cannot carry an empty one.
        foreach (var ct in decls.DeclTexts)
        {
            w.Write((byte)DeclarationKind.DeclText);
            w.Write(ct.Text ?? "");
            w.Write(ct.Line);
            w.Write(ct.Column);
        }
    }

    // The WIRE VALUES never move: DeclText is the byte a ClassText entry has always carried, and the entry's
    // meaning was always "a declaration's source text, re-parsed by the loader", so a blob written before a
    // top-level function could travel this way still reads (and a v21 engine reads one written now, since the
    // layout and the payload are identical). Only the NAME states what the entry is.
    private enum DeclarationKind : byte
    {
        Enum = 1, Delegate = 2, Interface = 3, Extension = 4, Import = 5,
        DeclText = 6
    }

    /// <summary>Reads the v6 DECLARATIONS section written by <see cref="WriteDeclarations"/>. Member shells come
    /// back as FuncDecls with Compiled bodies (the class/struct readers' own helper), so the loader can register
    /// them through the very code paths the interpreter's visitors use.</summary>
    private static LibraryDeclarations ReadDeclarations(BinaryReader r, string[] strings, int count)
    {
        var enums = new List<LibraryEnum>();
        var delegates = new List<DelegateInfo>();
        var interfaces = new List<LibraryInterface>();
        var extensions = new List<LibraryExtension>();
        var imports = new List<LibraryImport>();
        var declTexts = new List<LibraryDeclText>();
        Func<BinaryReader, string[], FuncDecl> readFunc = (rr, sTrs) => ReadBundleFunc(rr, sTrs, out _)!;
        for (var n = 0; n < count; n++)
        {
            var kind = (DeclarationKind)r.ReadByte();
            switch (kind)
            {
                case DeclarationKind.Import:
                    {
                        var path = ReadWireString(r);
                        var alias = r.ReadBoolean() ? ReadWireString(r) : null;
                        var line = r.ReadInt32();
                        var col = r.ReadInt32();
                        imports.Add(new LibraryImport(path, alias, line, col));
                        break;
                    }
                case DeclarationKind.Enum:
                    {
                        var name = ReadWireString(r);
                        var access = (AccessModifier)r.ReadInt32();
                        var line = r.ReadInt32();
                        var col = r.ReadInt32();
                        var mc = r.ReadInt32();
                        var members = new List<(string, long)>(mc);
                        for (var i = 0; i < mc; i++) members.Add((ReadWireString(r), r.ReadInt64()));
                        enums.Add(new LibraryEnum(name, access, line, col, members));
                        break;
                    }
                case DeclarationKind.Delegate:
                    {
                        var name = ReadWireString(r);
                        var access = (AccessModifier)r.ReadInt32();
                        var ret = ReadTypeInfo(r);
                        var pc = r.ReadInt32();
                        var prms = new (TypeInfo Type, string Name)[pc];
                        for (var i = 0; i < pc; i++) prms[i] = (ReadTypeInfo(r), ReadWireString(r));
                        var refs = ReadBools(r);
                        var outs = ReadBools(r);
                        var line = r.ReadInt32();
                        var col = r.ReadInt32();
                        delegates.Add(new DelegateInfo(name, ret, prms, refs, access, line, col, IsOutParams: outs));
                        break;
                    }
                case DeclarationKind.Interface:
                    {
                        var name = ReadWireString(r);
                        var tpc = r.ReadInt32();
                        var tps = new List<string>(tpc);
                        for (var i = 0; i < tpc; i++) tps.Add(ReadWireString(r));
                        var pcn = r.ReadInt32();
                        var parents = new List<string>(pcn);
                        for (var i = 0; i < pcn; i++) parents.Add(ReadWireString(r));
                        var mc = r.ReadInt32();
                        var members = new List<(string, int)>(mc);
                        for (var i = 0; i < mc; i++) members.Add((ReadWireString(r), r.ReadInt32()));
                        // DEFAULT BODIES and PROPERTY ACCESSORS come back as FuncDecls with Compiled bodies, marked
                        // as shells exactly like an extension member's (a body the wire produced), so the loader can
                        // register them through the visitor's own path and a tree fallback refuses by name.
                        var dc = r.ReadInt32();
                        var defaults = new List<(string Key, FuncDecl Decl)>(dc);
                        for (var i = 0; i < dc; i++)
                        {
                            var key = ReadWireString(r);
                            var fn = readFunc(r, strings);
                            fn.IsVmShell = true;
                            MarkNestedLambdasVmShell(fn);
                            defaults.Add((key, fn));
                        }
                        var pc = r.ReadInt32();
                        var props = new List<InterfacePropertySignature>(pc);
                        for (var i = 0; i < pc; i++)
                        {
                            var pname = ReadWireString(r);
                            var ptype = ReadTypeInfo(r);
                            var hasGet = r.ReadBoolean();
                            var hasSet = r.ReadBoolean();
                            FuncDecl? getter = null;
                            if (r.ReadBoolean())
                            {
                                getter = readFunc(r, strings);
                                getter.IsVmShell = true;
                                MarkNestedLambdasVmShell(getter);
                            }
                            FuncDecl? setter = null;
                            if (r.ReadBoolean())
                            {
                                setter = readFunc(r, strings);
                                setter.IsVmShell = true;
                                MarkNestedLambdasVmShell(setter);
                            }
                            props.Add(new InterfacePropertySignature(pname, ptype, hasGet, hasSet, getter, setter));
                        }
                        interfaces.Add(new LibraryInterface(name, tps, parents, members, defaults, props));
                        break;
                    }
                case DeclarationKind.Extension:
                    {
                        var target = ReadTypeInfo(r);
                        var tpc = r.ReadInt32();
                        var tps = new List<string>(tpc);
                        for (var i = 0; i < tpc; i++) tps.Add(ReadWireString(r));
                        var container = ReadWireString(r);
                        var receiver = ReadWireString(r);
                        var mc = r.ReadInt32();
                        var members = new List<FuncDecl>(mc);
                        for (var i = 0; i < mc; i++)
                        {
                            var fn = readFunc(r, strings);
                            fn.IsVmShell = true;
                            MarkNestedLambdasVmShell(fn);
                            members.Add(fn);
                        }
                        extensions.Add(new LibraryExtension(target, tps,
                            container.Length == 0 ? null : container,
                            receiver.Length == 0 ? null : receiver, members));
                        break;
                    }
                case DeclarationKind.DeclText:
                    {
                        var text = ReadWireString(r);
                        var line = r.ReadInt32();
                        var col = r.ReadInt32();
                        declTexts.Add(new LibraryDeclText(text, line, col));
                        break;
                    }
                default:
                    throw new ScriptException($"bytecode declarations section: unknown declaration kind {(int)kind}");
            }
        }
        return new LibraryDeclarations(enums, delegates, interfaces, extensions, imports, declTexts);
    }
    /// <summary>Writes a struct descriptor: name | access | isReadOnly | fields | properties |
    /// methods | operator-methods | constructors (all method/ctor bodies via pre-built shells).
    /// Mirrored by ReadStructDescriptor. Mirrors WriteClassDescriptor minus the parent/record/
    /// destructor/nested/static-value sections (structs carry none of those).</summary>
    private static void WriteStructDescriptor(BinaryWriter w, StructDeclStmt sd, DescriptorShells shells)
    {
        w.Write(sd.Name);
        w.Write((int)sd.Access);
        w.Write(sd.IsReadOnly);
        // fields: count | (type|name|access|isStatic)…
        w.Write(sd.Fields.Count);
        foreach (var f in sd.Fields)
        {
            WriteTypeInfo(w, f.Type); w.Write(f.Name); w.Write((int)f.Access); w.Write(f.IsStatic);
        }
        // The member blocks a struct shares with a class, minus the destructor slot (see WriteMemberBlocks).
        WriteMemberBlocks(w, sd.ImplementedInterfaces, sd.Properties, shells, withDestructor: false);
    }

    /// <summary>Checks whether a value is serializable as a variable/static-field snapshot, used by
    /// ExportLibrary to name the offending VARIABLE in its error. Runs WriteScriptValue into a
    /// throwaway buffer (the export path is cold, so the throwaway cost is irrelevant).</summary>
    public static void ProbeSerializable(object? value)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        WriteScriptValue(w, value);
    }

    // ── primitive helpers ───────────────────────────────────────────────────
    private static void WriteBytes(BinaryWriter w, byte[] arr)
    {
        w.Write(arr.Length);
        w.Write(arr);
    }

    private static byte[] ReadBytes(BinaryReader r)
    {
        var n = r.ReadInt32();
        return r.ReadBytes(n);
    }

    private static void WriteBools(BinaryWriter w, IReadOnlyList<bool>? arr)
    {
        if (arr == null) { w.Write(0); return; }
        w.Write(arr.Count);
        foreach (var b in arr) w.Write(b);
    }

    private static IReadOnlyList<bool> ReadBools(BinaryReader r)
    {
        var n = r.ReadInt32();
        if (n == 0) return [];
        var arr = new bool[n];
        for (var i = 0; i < n; i++) arr[i] = r.ReadBoolean();
        return arr;
    }
}
