namespace ScriptLangCore;

/// <summary>Bytecode opcodes for the stack-based VM (POC).
///
/// SIX MEMBERS WERE RETIRED in bundle version 24, each because the compiler never emitted it and each carrying real
/// behaviour that no suite could exercise: AND and OR (their arm only bailed; live `&amp;&amp;`/`||` compile through
/// JMP_IF_FALSE), LOAD_BUILTIN (the builtin table is never populated, so the verifier refused it anyway), LOAD_GLOBAL
/// and STORE_GLOBAL (replaced by the per-invocation-slot `_FAST` pair), and NEW (replaced by CALL_NEW / NEW_ARRAY /
/// NEW_STRUCT / NEW_LIST). Removing them RENUMBERS everything after them, which is why that was a wire change: the
/// format's fingerprint hashes these names and an older blob is refused by version rather than mis-read. NOP STAYS on
/// purpose, as the compiler's one free alignment/instrumentation slot, and it is the single entry
/// tools/opcode-not-emitted.txt still lists.</summary>
internal enum Opcode : byte
{
    NOP,            // no-op
    LOAD_CONST,     // push constant from table
    LOAD_VAR,       // push local variable value
    STORE_VAR,      // pop into local variable
    ADD, SUB, MUL, DIV,     // arithmetic (pop B, pop A, push A op B)
    NEG,                    // pop, push -value
    WIDEN_NUM,              // pop value + TypeInfo const → push value widened to the declared numeric type (no-op when not a narrower numeric)
    LT, GT, EQ, LE, GE, NE, // comparison
    JMP,            // unconditional jump (2 bytes, little-endian offset)
    JMP_IF_FALSE,   // pop, jump if false/zero
    CALL,           // call function (operand = arg count)
    RETURN,         // return
    YIELD,          // suspend generator: pop value, swap back to caller (retain frame); boundary → suspend run
    DUP,            // duplicate top of stack
    POP,            // discard top of stack
    INC,            // increment local variable (operand = var index)
    DEC,            // decrement local variable
    NOT,            // logical not
    IS_TYPE,        // type check
    MOD,            // modulo
    BITAND,         // bitwise and
    BITOR,          // bitwise or
    BITXOR,         // bitwise xor
    SHL,            // shift left
    SHR,            // shift right
    BITNOT,         // bitwise not (~)
    MAKE_LAMBDA,    // create ScriptFunction from nested-lambda table (operand = index)
    GET_INDEX,      // pop N indices + array → push element (operand = index count)
    ARRAY_GET,      // pop index + array → push element, fast path for CLR arrays (no boxing)
    SET_INDEX,      // pop value + N indices + array → store (operand = index count)
    SLICE,          // pop end, start, collection → push sliced collection
    GET_PROP,       // pop instance + property-name const → push member value
    SET_PROP,       // pop value + instance + property-name const → store
    CALL_METHOD,    // pop name const + N args + receiver → method call (operand = arg count)
    CALL_VAL,       // pop N args + callee value → invoke (operand = arg count)
    LOAD_GLOBAL_FAST,  // push global via per-invocation slot (operand = slot index)
    STORE_GLOBAL_FAST, // pop → write global via per-invocation slot (operand = slot index)
    LOC,            // source line/col for error reporting (4 operand bytes: line lo/hi, col lo/hi)
    CALL_NEW,       // pop N args + type-name const → script-class instantiation (operand = arg count)
    NEW_ARRAY,      // pop nSizes + nInits + TypeInfo const → typed array (operands: nSizes, nInits)
    NEW_STRUCT,     // pop N field values + name const → struct instance (operand = field count)
    CAST,           // pop value + TypeInfo const → cast (tree VisitCastExpr semantics)
    AS,             // pop value + TypeInfo const → as (tree VisitAsExpr semantics, null on failure)
    MAKE_INDEX,     // pop int + fromEnd flag → C# Index (operand = flag)
    MAKE_RANGE,     // pop to + from + hasTo flag → long[] range literal (operand = hasTo)
    MAKE_TUPLE,     // pop N items → ScriptTuple (operand = item count)
    DISPOSE,        // pop value → IDisposable.Dispose() / script destructor chain (tree TryDispose)
    DESTRUCT,       // pop source → validate array/list + push min(nVars,count) elements (operand = nVars)
    MATCH_EQ,       // pop pattern + subject → AreEqual (tree's value-aware equality, incl. enum)
    NEW_LIST,       // create List<object?> in a local (operand = local idx)
    LIST_APPEND,    // pop value → append to List local (operand = local idx)
    COMP_FINISH,    // pop List<object?> → object?[] result array (comprehension)
    OBJ_INIT,       // pop N (name-const, value) pairs + instance → object-initializer (operand = count)
    WITH,           // pop N (name-const, value) pairs + base → with-expression (operand = count)
    LOAD_SUPER,     // push SuperProxy(currentThis, currentClass.Parent), for super.member access
    TRY_GET_PROP,   // pop name-const + instance -> push member value, then found(bool); missing member => found=false
    PAT_LIST,       // pop subject -> push bool; engine-delegated list/tuple pattern (operand = descriptor const)
    TRY,            // push exception handler {sp, catchSelIp} (operand = catchSelIp)
    ENDTRY,         // pop exception handler (normal path)
    CATCH_MATCH,    // pop TypeInfo const + value → bool (IsExceptionMatch); operand = type const idx
    THROW,          // pop value → wrap ScriptErrorInfo + throw ScriptException
    TRY_FINALLY,    // push finally handler {sp, finallyIp} (operand = finallyIp)
    POP_FINALLY,    // pop finally handler (normal/caught path)
    END_FINALLY,    // pop value-or-pending → rethrow if pending else continue (operand = afterIp)
    FOREACH_INIT,   // pop collection → live iterator state (index/enumerator per kind; null → empty) + zero index (operands: stateLocal, idxLocal, allowEnumerable)
    FOREACH_NEXT,   // pop idx + state → push next element or jump (operands: endLo, endHi, varTypeConstIdx)
    FOREACH_END,    // dispose enumerator-based iterator state (operand = state local)
    FOREACH_CALL,   // pop args + callee-name const → drive a statically-known coroutine in-place for a foreach, WITHOUT Coroutine/Generator wrappers (Task 4; operands: argCount, stateLocal, varTypeConstIdx, endLo, endHi)
    MAKE_LIST,      // create typed array (operand = element count; element TypeInfo popped first)
    MAKE_DICT,      // create Dictionary<object,object?> (operand = entry count; pops 2*count)
    THROW_FLOW_RETURN,   // pop value → VmFlowException(Return), return escaping try/finally regions (operand = handler unwind count)
    THROW_FLOW_BREAK,    // break escaping try regions: 2-byte relative target + 1-byte handler unwind count → VmFlowException(Break)
    THROW_FLOW_CONTINUE, // continue escaping try regions: 2-byte relative target + 1-byte handler unwind count → VmFlowException(Continue)
    LOAD_REF,            // push (localIdx, value) as a K=4 ref-arg, CALL wraps the slot in a RefCell for ref/out params
    REL_PATTERN,         // pop right-value + subject → relational-pattern match (operand: 0=LT 1=GT 2=LE 3=GE; tree PatternMatches RelationalPattern semantics)
    EVENT_ADD,           // pop handler + name const + instance → event subscribe (tree event += branch; operand = name const idx)
    EVENT_SUB,           // pop handler + name const + instance → event unsubscribe (tree event -= branch; operand = name const idx)
    STORE_CONST,         // store a local WITHOUT the const check, emitted for const declarations (their own initializing store)
    ATTACH_TUPLE_NAMES,   // pop names const + attach to ScriptTuple on stack top, tuple-typed var decl (tree parity: VisitVarDeclStmt attaches TupleElementNames)
    RENEW_LOCAL,         // give a local a FRESH capture cell (a new binding): emitted for a local declared below
                         // the top level, so closures made by a later execution of that declaration stop aliasing an
                         // earlier one (tree parity: every execution of a `var` makes a new VariableInfo, while the
                         // VM shared one RefCell per slot, so every closure over a loop local read the LAST value)
    AWAIT                // pop an awaitable (a Task, or a yield-request already built) and suspend; the RESUME
                         // PUSHES the awaited VALUE into the operand slot the enclosing expression expects, which is
                         // exactly what a yield cannot do (its resume pushes nothing), and is what makes an `await`
                         // usable in expression position and an async body compilable without carrying its source
}

internal sealed class Bytecode(
    byte[] code,
    object?[] consts,
    string[] locals,
    FuncDecl[]? nestedLambdas = null,
    bool[]? constLocals = null,
    string[]? globalSlotNames = null,
    IReadOnlyList<string>? bareCalls = null,
    IReadOnlyList<FuncDecl>? nestedDecls = null,
    IReadOnlyList<string>? carriedDeclaredNames = null)
{
    /// <summary>The raw instruction stream.</summary>
    public byte[] Code { get; } = code;

    /// <summary>The bare-name CALLS this body emits, as the compiler recorded them (LOAD_CONST(name) + CALL).
    /// IN-MEMORY ONLY: the codec does not serialize it, so a loaded body has null and the LOADER's own validation
    /// guards that side. The strip reads it to refuse a call the bundle could not resolve.</summary>
    public IReadOnlyList<string>? BareCalls { get; } = bareCalls;
    public IReadOnlyList<FuncDecl>? NestedDecls { get; } = nestedDecls;

    /// <summary>The names a CARRIED top-level STATEMENT declares at its own top level (a `var`, a destructuring),
    /// IN-MEMORY ONLY like BareCalls. A carried statement is not compiled, so its declarations do not appear in
    /// <see cref="GlobalSlotNames"/>; but running it binds them in the interpreter's global scope (the tree
    /// executes it with the root frame pushed), so a later compiled statement CAN call them. Without this list the
    /// strip read such a call as unresolvable and refused the file by name: measured on
    /// `var g = fun(int n) { ... g(n - 1) ... }; print(g(4));`, whose first statement is carried (a lambda that
    /// calls the variable being declared) and whose second one calls `g` from a compiled body.</summary>
    public IReadOnlyList<string>? CarriedDeclaredNames { get; } = carriedDeclaredNames;

    /// <summary>Set once a run has registered this body's NestedDecls in the interpreter's registry. A FLAT call continues
    /// the caller's C# invocation, so it never passes the door that registers them, and without this flag every swap would
    /// walk the list again (measured: two bodies declaring a helper of the SAME name, the second body's declaration
    /// unresolved because only the first body had ever been walked, so the helper was looked up by name and the first
    /// one won).</summary>
    internal bool NestedDeclsRegistered;

    /// <summary>Constant table, indexed by LOAD_CONST operand.</summary>
    public object?[] Constants { get; } = consts;

    /// <summary>Local variable names, indexed by LOAD_VAR/STORE_VAR operand.</summary>
    public string[] Locals { get; } = locals;

    /// <summary>Nested lambda declarations, indexed by MAKE_LAMBDA operand. Each has .Compiled set.</summary>
    public FuncDecl[]? NestedLambdas { get; } = nestedLambdas;

    /// <summary>Per-local const flag, STORE_VAR to a const slot throws (same semantics as the tree).</summary>
    public bool[]? ConstLocals { get; } = constLocals;

    /// <summary>Global-slot names, indexed by LOAD_GLOBAL_FAST/STORE_GLOBAL_FAST operand.</summary>
    public string[]? GlobalSlotNames { get; } = globalSlotNames;

    /// <summary>Cached converted constant table (read-only after first fill).</summary>
    private VmValue[]? _constsVm;

    /// <summary>VmValue-typed constants, computed once, reused by every invocation of this Bytecode.</summary>
    public VmValue[] ConstsVm => _constsVm ??= BuildConstsVm();

    private VmValue[] BuildConstsVm()
    {
        var arr = new VmValue[Constants.Length];
        for (var i = 0; i < arr.Length; i++) arr[i] = VmValue.From(Constants[i]);
        return arr;
    }
}

/// <summary>Descriptor for PAT_LIST: the pattern to match (engine-side), plus the names it may
/// bind and the VM local slot for each, so bindings land in locals exactly like STORE_VAR. `PatternText` is that
/// pattern's own SOURCE, which is the only part of it the wire carries: a bundle rebuilds the pattern by handing the
/// text back to the parser (Pattern.SourceText, Parser.ParsePatternFromText), so a pattern cannot mean two different
/// things on the two sides. Null when the parse had no text, which the const writer REFUSES by name.</summary>
internal sealed record PatternEngineDescriptor(Pattern Pattern, string[] Names, byte[] LocalIdxs, string? PatternText = null);
