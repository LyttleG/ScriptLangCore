namespace ScriptLangCore;
/// <summary>Sentinel indicating the VM couldn't execute, tree-walker fallback required.</summary>
internal sealed class VmFallback
{
    public static readonly VmFallback Instance = new();

    private VmFallback()
    {
    }
}

/// <summary>Compiler-emitted marker for a named-argument call. The compiler pushes it as a const at
/// the BOTTOM of the call layout, [marker, calleeName, arg0, …, argN-1], so the CALL handler's
/// "pop args, then pop callee" sequence leaves it as the new stack top. The handler pops it and
/// reorders callArgs against the callee's declared param names (see ReorderNamedArgs).</summary>
internal sealed class NamedArgsMarker(IReadOnlyList<string?>? Names)
{
    public IReadOnlyList<string?>? Names { get; } = Names;
}

/// <summary>Compiler-emitted marker for a call that NAMES TYPE ARGUMENTS. Pushed like the NamedArgsMarker, at
/// the BOTTOM of the layout, [marker, receiver, args…, name], because the bytecode has no other slot to carry
/// them: without it a compiled `M&lt;int&gt;(…)` reached the dispatcher with no type argument and the OPEN generic
/// definition was invoked instead, which the CLR refuses ("Late bound operations cannot be performed on types or
/// methods for which ContainsGenericParameters is true"), or, worse, a script-named argument would have been
/// silently widened to `object`. The CALL_METHOD handler pops it and hands the arguments to the interpreter's
/// dispatch, which is the ONE place that closes a generic definition.</summary>
internal sealed class TypeArgsMarker(IReadOnlyList<TypeInfo> TypeArgs)
{
    public IReadOnlyList<TypeInfo> TypeArgs { get; } = TypeArgs;
}

/// <summary>Compiler-emitted marker for a call that passes arguments by `ref`/`out`. It exists because the VM's
/// argument marker (K=4) means "this argument came from a LOCAL SLOT", and that is pushed for a plain local
/// argument too: measured, `f(ref a, 2)` and `f(a, 2)` compile to the same code length, const count and opcodes,
/// differing only in the source-position bytes of their `LOC` instructions, and `SumOn(xs)` (a local ARRAY into a
/// params tail) pushes the same marker with `isCell=False`. So the ref-ness is knowable only from here, and the
/// flags are what let the VM refuse a ref argument that the callee's declaration does not cover, including one
/// that lands in a `params` tail, where there is no declared parameter at all. Pushed at the BOTTOM of the call
/// layout, like NamedArgsMarker, because the bytecode has no other slot for it.</summary>
/// <summary>Compiler-emitted marker for a call whose result is used as a REFERENCE (`F(..) = 7;`, `ref int r = ref
/// F(..);`). Pushed at the BOTTOM of the call layout, like the other markers: the bytecode has no other slot, and
/// without it the VM cannot tell a value read from an lvalue use, because the callee's own bytecode looks the same
/// either way. A call carrying it is never inlined (the flat swap dereferences) and the callee is invoked with the
/// want-reference flag, so the CELL reaches the caller.</summary>
internal sealed class RefResultMarker
{
    public static readonly RefResultMarker Instance = new();

    private RefResultMarker()
    {
    }
}
internal sealed class ArgFlagsMarker(bool[] Refs, bool[] Outs)
{
    public bool[] Refs { get; } = Refs;
    public bool[] Outs { get; } = Outs;
}

/// <summary>Internal control-flow exception, break/continue/return escaping try/finally regions.
/// The VM unwinds the compiled handler stack exactly like a script exception (running every pending
/// finally), then resumes the flow: returns the pending value or jumps to the loop-exit target.
/// `Remaining` is decremented per handler popped so a flow targeting a loop INSIDE an enclosing try
/// stops unwinding at that try (its finally runs via the normal completion path instead of twice).</summary>
internal sealed class VmFlowException(int kind, object? value, int target, int remaining)
    : Exception
{
    public const int KindReturn = 1;
    public const int KindBreak = 2;
    public const int KindContinue = 3;
    public readonly int Kind = kind;
    public readonly object? Value = value; // pending return value (KindReturn)
    public readonly int Target = target; // absolute loop-exit ip (KindBreak/KindContinue)
    public int Remaining = remaining; // handler entries left to unwind (mutable across END_FINALLY rethrows)
}

/// <summary>Per-call state for the flat VM call stack, a user-function CALL pushes a SavedFrame and
/// swaps the execution state in place instead of re-entering Vm.Execute (which grew the C# stack per
/// script call and overflowed on deep non-tail recursion). RETURN / exception unwinding pops the frame
/// and restores the caller's state. The operand stack (sp) is shared across frames, each frame only
/// records its own base so `sp > base` return checks and absolute handler `sp` restores stay correct.</summary>
internal sealed class SavedFrame
{
    public string Name = "";      // callee function name (for the trace)
    public FuncDecl? Decl;        // callee declaration, for tuple-name attachment on return
    public Bytecode Bc = null!;
    public byte[] Code = null!;
    public int Ip;
    public VmValue[] Consts = null!;
    public VmValue[] Locals = null!;
    public int CurLine;
    public int CurCol;
    public int SpBase;
    public List<(int sp, int catchIp, int finallyIp, bool pendingThrow, VmValue pendingVal, int seq)>? Handlers;
    public int HandlerBase;
    public bool PendingFinally;
    public object? PendingFinallyPayload;
    /// <summary>Out-param assignment tracking for the call this frame saved: one flag per parameter
    /// position, true once the callee assigned it. This frame holds the CALLER's state, so it belongs
    /// to the call that finishes when the frame pops, verified on RETURN, mirroring the tree's
    /// InitOutTracking and the VM-direct check in TryExecuteVmBody. Null when no out positions.</summary>
    public bool[]? OutTrack;
    public int Seq;
    public List<(int stateLocal, int seq)>? ForEachStates;
    // Global fast-slot table (spec A), the caller's per-invocation arrays, saved so RETURN and the
    // exception-unwind paths can restore them (cleared post-call so the caller re-resolves globals
    // the callee may have mutated). The callee runs with a fresh table sized to its own bytecode.
    public VmValue[] GlobalSlots = null!;
    public byte[] GlobalSlotState = null!;
    public Namespace? NsContext;   // F4: caller's namespace context, restored on return
    /// <summary>For a SuspendedGenerator frame: the generator's operand-stack height at the yield
    /// (its shared-stack region [SpBase, Sp) is retained above the caller's active stack, the
    /// FOREACH_NEXT restore sets sp = Sp to resume). Unused for normal call frames.</summary>
    public int Sp;
    /// <summary>True when this frame holds a VM generator's suspended execution state (a YIELD left
    /// it in place instead of RETURN-popping it). The frame is NOT popped on the yield, a later
    /// FOREACH_NEXT swaps back into it. The frame's Bc/Code/Ip/Locals/etc. are the GENERATOR's live
    /// state (not the caller's), unlike a normal call frame.</summary>
    public bool SuspendedGenerator;
    /// <summary>The generator identity this frame drives: a Coroutine (FOREACH_NEXT drive of a VM
    /// generator) or the generator's own locals array (FOREACH_CALL, Task 4, no wrapper objects;
    /// the array is unique per invocation and live while suspended). RETURN marks the identity Dead
    /// when the drive completes; a SuspendedGenerator frame carries the generator's OWN identity so
    /// the next drive can locate its retained frame. Null for ordinary call frames.</summary>
    public object? GeneratorHandle;
    /// <summary>For a generator-drive caller frame: the absolute ip of the caller's FOREACH_END,
    /// when the driven generator completes, RETURN jumps here (its return value is NOT a foreach
    /// element) instead of resuming at the post-FOREACH_NEXT STORE.</summary>
    public int ForEachEndIp;
    /// <summary>The generator context of the function this frame belongs to at push time: the
    /// generator identity of the CURRENT function when it yielded/returned. Restored on swap-back so
    /// the caller's own generator identity survives a nested drive (the YIELD boundary-vs-nested
    /// decision keys on it). Null for the boundary generator / plain functions.</summary>
    public object? GenCo;
    /// <summary>The name of the function this frame belongs to (the frame's owner, not the callee),
    /// restored into currentFunc on swap-back so the trace reflects the resumed function.</summary>
    public string CallerName = "";
    /// <summary>True when this frame is the caller frame of a FOLDED generator-foreach drive:
    /// FOREACH_INIT over a VM-backed Generator stored the driven generator's Coroutine in the
    /// state local instead of allocating a ~96 B VmForEachState, so the per-step step state is
    /// just "a next element exists". False for ordinary frames and the non-folded
    /// VmForEachState path.</summary>
    public bool GenHasNext;
    /// <summary>For a FLAT METHOD frame: the class context the call set (_currentThis/_currentClass), so RETURN
    /// and the unwind restore the caller's. A struct method stores a StructInstance as ThisInstance and null as
    /// ThisClass, which is why the pair travels with the flag. False for every other frame, and cleared on rent
    /// like the generator identity, or a plain function frame would restore somebody else's context.</summary>
    public bool SavesClassContext;
    public object? ThisInstance;
    public ClassInfo? ThisClass;
    /// <summary>An extension block's NAMED receiver (`extension(string s)`), which its bodies may read as a
    /// second spelling of the value. Cleared on rent with the flag, because the class/struct/method branches set
    /// the flag without setting this, and a stale name would be restored into the CALLER's context.</summary>
    public string? ThisReceiverName;
    /// <summary>The CONTRACT whose default body is running, for the interface-default route: a default composes
    /// through its OWN contract (`this.other()` means the contract's meaning of `other`), so it travels with the
    /// frame exactly as the class context does, and is cleared on rent for the same reason.</summary>
    public string? ThisContract;
}

/// <summary>Live iterator state for VM FOREACH loops, mirrors the tree-walker's per-kind
/// iteration: index-based for arrays/strings/lists, materialized ScriptKeyValuePair entries
/// for dictionaries, lazy GetEnumerator() for HashSet/other IEnumerable, and in-place frame
/// driving for VM generators (the foreach drives the inner generator in the SAME Vm.Execute
/// run, flat frames, instead of falling back to the tree). Bare Coroutine collections and
/// unknown types are NOT classified (CreateForEachState → null) so the VM falls back to the
/// tree-walker, which owns the non-VM coroutine loop machinery.</summary>
internal sealed class VmForEachState
{
    public const byte KindArray = 0;
    public const byte KindString = 1;
    public const byte KindScriptList = 2;
    public const byte KindIList = 3;
    public const byte KindDict = 4;
    public const byte KindEnumerable = 5;
    public const byte KindGenerator = 6;

    /// <summary>A coroutine the VM cannot drive IN PLACE (its body did not compile, or it produces yield requests),
    /// stepped through the TREE instead: one `Resume()` per element, exactly the call a tree `foreach` makes. Without
    /// this the folded drive answered "no state" and the statement BAILED, which a body with no per-statement fallback
    /// (a lambda, a method, a function) cannot absorb: measured on the Excel sample, where `XlBook.Find` loops over
    /// `this.Sheets()` and the whole of `Add` refused, cascading into six more failed checks.</summary>
    public const byte KindTreeCoroutine = 7;

    public byte Kind;
    public Array? Arr;
    public string? Str;
    public IScriptList? ScriptList;
    public IList? List;
    public object?[]? Entries; // KindDict, ScriptKeyValuePair entries (matches the tree's dict foreach)
    public IEnumerator? Enumerator; // KindEnumerable
    public Coroutine? Generator; // KindGenerator, the driven generator's Coroutine (mark Dead on completion)
    public Coroutine? TreeCoro;  // KindTreeCoroutine, stepped with Resume()
    public bool TreeCoroPrimed;  // KindTreeCoroutine, false until the first step has been taken
}

/// <summary>Tagged union value for the VM operand stack, avoids boxing on the hot numeric path.</summary>
internal readonly struct VmValue(byte k, byte t, long l, double d, object? o)
{
    public readonly byte K = k; // 0=null, 1=long, 2=double, 3=object, 4=ref-arg (L=localIdx, O=value), 5=bool (payload in L)

    public readonly byte
        T = t; // K==1: the original CLR integral type (0=long,1=int,2=short,3=byte,4=sbyte,5=uint,
               // 6=ushort,7=ulong). K==2: 1 means the double payload IS a float, so it can come back as
               // one, a computed result leaves T==0 and stays a double, as it always did.

    public readonly long L = l;
    public readonly double D = d;
    public readonly object? O = o;

    public VmValue(long v) : this(1, 0, v, 0, null)
    {
    }

    public VmValue(long v, byte t) : this(1, t, v, 0, null)
    {
    }

    public VmValue(double v) : this(2, 0, 0, v, null)
    {
    }

    // Bool kind: K=5, payload in L (0/1), avoids boxing per comparison / NOT / is / match.
    public VmValue(bool v) : this(5, 0, v ? 1 : 0, 0, null)
    {
    }

    public VmValue(object? v) : this(v == null ? (byte)0 : (byte)3, 0, 0, 0, v)
    {
    }

    // Reassemble from the parallel stack arrays (Push/Pop fan-out keeps the same layout)

    public bool IsNull => K == 0;
    public bool IsLong => K == 1;
    public bool IsDouble => K == 2;
    public bool IsObj => K == 3;
    public bool IsBool => K == 5;

    /// <summary>Smart conversion: numeric boxed values become the long kind; everything else stays an object.</summary>
    /// <summary>A float: the double slot with the float hint, so ToObject() hands a float back.</summary>
    public VmValue(float v) : this(2, 1, 0, v, null) { }

    public static VmValue From(object? o) => o switch
    {
        null => new VmValue(),
        bool b => new VmValue(b),
        long l => new VmValue(l, 0),
        int i => new VmValue(i, 1),
        short s => new VmValue(s, 2),
        byte b => new VmValue(b, 3),
        sbyte sb => new VmValue(sb, 4),
        uint ui => new VmValue(ui, 5),
        ushort us => new VmValue(us, 6),
        ulong ul => new VmValue((long)ul, 7),
        double d => new VmValue(d),
        float f => new VmValue(f),
        // Boxed on purpose: `decimal` converts IMPLICITLY to double, so `new VmValue(m)` would bind to the
        // double constructor and a computed decimal would come back a double (typeof said "double" for a
        // sum the tree reports as "decimal"). Same trap the float line below/above had; the explicit object
        // cast keeps the value the decimal it is, and its arithmetic goes through the interpreter as before.
        decimal m => new VmValue((object)m),
        _ => new VmValue(o)
    };

    public object? ToObject() => K switch
    {
        0 => null,
        4 => O,             // ref-arg: O holds the value
        5 => L != 0,         // bool
        1 => T switch
        {
            0 => L,
            1 => (int)L,
            2 => (short)L,
            3 => (byte)L,
            4 => (sbyte)L,
            5 => (uint)L,
            6 => (ushort)L,
            7 => (ulong)L,
            _ => L
        },
        2 => T == 1 ? (object)(float)D : D,
        _ => O
    };

    public override string ToString() => ToObject()?.ToString() ?? "";
}

/// <summary>Stack-based VM that executes Bytecode with a typed operand stack (POC).</summary>
internal static class Vm
{

    /// <summary>Aborts a VM body to the tree, and can SAY which instruction did it: SLC_BAIL_TRACE=1 prints
    /// `[bail] Vm.cs:<line>` for every abort, which is the only way to attribute one, since the body's own
    /// error says nothing about the site. Every `return VmFallback.Instance;` in the dispatch loop goes through
    /// here, so the report is complete; the env read costs nothing when it is not set.</summary>
    private static VmFallback Bail([System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (Interpreter.BailTraceEnabled)
            Console.Error.WriteLine($"[bail] Vm.cs:{line}");
        // THROWN, not returned: the dispatch loop's own handler walk is what runs a script's pending `finally`
        // blocks, and returning the sentinel straight out of the loop skipped them (measured: a `finally` that sets
        // a marker never ran, which is how the Excel sample left an invisible EXCEL.EXE behind). The boundary turns
        // it back into VmFallback.Instance, so every `is VmFallback` site is unaffected: see VmBailException.
        throw Interpreter.NewVmBail();
    }

    /// <summary>Hands `AssembleParamsTail` a CELL for every argument the compiler flagged ref/out at a `params`
    /// TAIL position, so the assembler's own refusal ("a tail takes by-value arguments") fires. A tail has no
    /// declared parameter, so the declaration cannot answer this question, and the VM's K=4 argument marker cannot
    /// either: it is pushed for a plain local argument as well (measured: `SumOn(xs)` with a local ARRAY pushes the
    /// same marker as `f(ref a, 2)`, both `isCell=False`). The flags come from `ArgFlagsMarker`.</summary>
    private static void RewrapFlaggedTailArgsAsCells(
        FuncDecl d, ArgFlagsMarker flags, int[]? refIdx, VmValue[] locals, object?[] callArgs, int argCount)
    {
        if (refIdx == null) return;
        if (d.IsParams is not { Count: > 0 } ip || !ip[^1]) return;
        var tailStart = d.Params.Count - 1;   // the declared tail is the last parameter
        for (var i = tailStart; i < argCount && i < flags.Refs.Length; i++)
        {
            if (!flags.Refs[i] && (i >= flags.Outs.Length || !flags.Outs[i])) continue;
            var slot = refIdx[i];
            if (slot < 0 || slot >= locals.Length) continue;
            object? held = locals[slot].ToObject();
            callArgs[i] = held as RefCell ?? new RefCell(() => held, v => held = v);
        }
    }

    /// <summary>Small thread-static pool for the SavedFrame objects ping-ponged at every generator
    /// boundary (FOREACH_NEXT drive ⇄ nested YIELD): per element both sides allocate+discard a
    /// 22-field register snapshot. Recycling them makes the steady-state generator loop allocate
    /// ZERO objects per element. Bounded to 64 so an abandoned frame can never pin unbounded memory.</summary>
    [ThreadStatic] private static Stack<SavedFrame>? t_savedFramePool;
    /// <summary>Frames of an error that escapes this VM invocation: the names collected while
    /// unwinding (innermost first) plus this invocation's own function, it is being abandoned too.
    /// The entry frame is never in the flat `frames` list (it IS the current execution state), so
    /// without this the outermost script function would be missing from ErrorInfo.Frames.
    /// Note: an error raised in TAIL position legitimately has no frame for that call (the frame is
    /// popped before the tail expression is evaluated), the chain is then simply shorter.</summary>
    private static List<string>? WithEntryFrame(List<string>? unwound, string? entryName, FuncDecl? entryDecl = null)
    {
        var names = unwound;
        // An EMPTY entry name is a DECLARATION with no name, i.e. a LAMBDA, and its frame reports `<lambda>`. A NULL
        // entry name is a script's top level, which adds nothing (that is what check 4 of error_frames pins). Measured:
        // a lambda called from a topped-level `try` in a bundle reported Frames ["__program__"] because the lambda's own
        // invocation passed `decl.Name` == "" and nothing was collected, so the entry frame was the only survivor.
        // The PROGRAM ENTRY is not a script frame: `__program__` is a synthetic name for a bundle's top level, and a
        // source run reports NO frame for the same error, so a bundle must not either (that is the other half of
        // error_frames check 4a). It keeps its `currentFunc`, which is what a trace needs; only the FRAME is skipped.
        if (entryName != null && entryName != "<script>" && entryName != Interpreter.ProgramEntryFunctionName)
        {
            names ??= new List<string>();
            names.Add(Interpreter.FrameWithPosition(Interpreter.FrameLabel(entryName), 0, 0, entryDecl));
        }

        // Bound the chain like the tree's snapshot (innermost first). Without this a runaway
        // recursion attached one name per abandoned frame: deep(30000) printed 30000 lines.
        if (names is { Count: > Interpreter.MaxErrorFrames })
            names.RemoveRange(Interpreter.MaxErrorFrames, names.Count - Interpreter.MaxErrorFrames);
        return names;
    }

    /// <summary>True when the declaration takes any out parameter, and the ONE place that question is asked.
    /// It is no longer a ROUTING decision: the VM tracks an out parameter's assignment and verifies it at
    /// RETURN itself (see OutTrack and the `out parameter ... was not assigned` refusal), which is why the
    /// helper outlived the version that handed such a call to the tree. Three call sites used to spell this
    /// test inline, in three different shapes, which is the drift it exists to prevent.</summary>
    private static bool HasOutParam(FuncDecl d)
        => d.IsOutParams is { Count: > 0 } && d.IsOutParams.Contains(true);

    /// <summary>Message for an allocation failure while growing the flat call stack: the DEPTH REACHED and
    /// the innermost function, which is what a raw "Arg_OutOfMemoryException" (the resource key under
    /// InvariantGlobalization) could never tell the user, for a counter-style recursion the depth IS the
    /// value the script was computing when it ran out. Deliberately one sentence: the cost model, the cap and
    /// the override knob live in the code comments and docs rather than in every error.</summary>
    internal static string FlatStackOomMessage(int depth, string? innermost)
        => $"Out of memory after {depth:n0} nested script calls (innermost '{innermost}').";

    /// <summary>Depth-cap message. The one hint worth carrying in the text is the knob: a cap hit used to
    /// read only "Maximum call depth exceeded (N), possible runaway recursion", which tells a reader with a
    /// legitimate depth nothing about how to allow it.</summary>
    internal static string DepthCapMessage(long cap, string kind) =>
        $"Maximum call depth exceeded ({cap:n0}), possible runaway recursion. "
        + $"{kind} depth is overridable with the SLC_VM_MAXFRAMES environment variable.";

    private static SavedFrame RentSavedFrame()
    {
        var pool = t_savedFramePool;
        if (pool is not { Count: > 0 }) return new SavedFrame();
        var rented = pool.Pop();
        // A pooled frame can come back from a SUSPENDED generator: ReturnSavedFrame recycles the retained frame a
        // drive no longer needs, and the YIELD path had marked it SuspendedGenerator. Every other field is
        // rewritten by the push sites, but nothing else clears THIS one, and the YIELD handler's driver scan
        // requires a non-suspended frame carrying the yielding coroutine's handle. Measured: without this reset a
        // two-deep generator chain drives correctly on its first element and loses the second, because the frame
        // re-pushed for the resume arrived still marked suspended, the scan found no driver, and the yield was
        // taken for the boundary suspension (the load-bearing symptom: `names kind: Rec` instead of a list).
        rented.SuspendedGenerator = false;
        // The REST OF THE GENERATOR IDENTITY, cleared here so a rented frame is field-for-field what
        // `new SavedFrame()` is. An ordinary call push sets 21 fields and leaves exactly these unset, and they
        // are the ones a plain frame must not inherit from a recycled generator frame: the YIELD handler's
        // driver scan matches on `GeneratorHandle == curGenCo` with `!SuspendedGenerator` (so a stale handle
        // would make a plain frame look like a driver), a driven generator's completion jumps to
        // ForEachEndIp, and the swap-back restores CallerName into currentFunc. `GenHasNext` belongs to the
        // folded-drive caller frame, and Sp to a suspended one.
        rented.GeneratorHandle = null;
        rented.ForEachEndIp = 0;
        rented.Sp = 0;
        rented.GenHasNext = false;
        rented.CallerName = "";   // the field initializer's value, NOT null: swap-back assigns it to currentFunc
        rented.SavesClassContext = false;
        rented.ThisReceiverName = null;
        rented.ThisContract = null;
        return rented;
    }
    private static void ReturnSavedFrame(SavedFrame f)
    {
        var pool = t_savedFramePool;
        if (pool == null) t_savedFramePool = pool = new Stack<SavedFrame>();
        if (pool.Count < 64) pool.Push(f);
    }
    /// <summary>When true, the dispatch loop prints each executed opcode ([vm] ip=… OP sp=…).
    /// Opt-in debug aid, enabled via VmSetTrace(true) or the .vm trace REPL command. It also honours
    /// `SLC_VM_TRACE=1` like every other trace: this was the ONE trace the environment could not turn on, because
    /// the field was never initialized from the gate (found while tracing the alloc bench, item 58, where the script
    /// route `Diagnostics.Trace("VM_TRACE", true)` was the only way in).
    public static bool Trace = Diagnostics.On("VM_TRACE");

    /// <summary>The operand stack's initial capacity, per VM invocation. It was 64 in FIVE fresh arrays
    /// (two byte, a long, a double, an object?) built for every invocation, 1,784 B of them, and a METHOD call
    /// is a nested invocation: measured, a `void` method with an empty body cost 2,913 B against 978 B for the
    /// same body walked by the tree. Grow() doubles, so a body needing more pays a resize, never a fault.</summary>
    private const int OperandStackInitial = 16;

    /// <summary>Most recent per-invocation crash ring (trace lines), dumped by the fatal handler
    /// in Program.cs when a ScriptException escapes with no usable [Lx:Cx]. Populated only when
    /// Trace is on; size 64.</summary>
    public static string[]? LastCrashRing;

    /// <summary>Source file of the most recent traced instruction, the trace prints a one-time
    /// banner (not a per-line src= tag) when this changes, i.e. at import/module boundaries.</summary>
    public static string LastTracedSource = "";

    /// <summary>Flat-stack frame cap is build-dependent: the heap frames list costs a tiny C#-stack
    /// re-entry per level (~15-37B), so on the Debug/JIT 1.5MB apphost stack the process dies
    /// between 40000 and 100000 levels, before this cap. JIT caps at 30000 (below the observed
    /// 40000+ limit). NativeAOT is heap-bounded, not stack-bounded: the flat path runs 100% VM
    /// (C# stack never grows), and the real limit is memory, measured ~370B of working set per
    /// level, with the process OOM-ing (catchably) at ~20.3M levels. The 1,000,000 default
    /// therefore costs ~370MB worst-case before the guard throws a catchable ScriptException.
    /// Diagnostic: SLC_VM_MAXFRAMES=N overrides the cap (raise it to measure the REAL limit,
    /// the depth where the process finally dies).</summary>
    internal static int VmMaxFrames = Diagnostics.EnvNumber("VM_MAXFRAMES") ?? DefaultVmMaxFrames();

    /// <summary>Per-frame cost used to derive the AOT cap from memory. Measured on the NativeAOT publish by
    /// taking the GC.GetTotalMemory(true) SLOPE between 200k and 400k live frames, which cancels fixed
    /// overhead: plain frames 370 B/level, ref frames 402 B/level (a ref call keeps one RefCell per level,
    /// +32 B). 410 B is the worst measured case, used so the budget errs conservative. The older 370 B
    /// figure in the note above was the generator measurement and still holds for plain frames.</summary>
    private const long FrameBytesAot = 410;

    /// <summary>Flat-stack frame cap for a build where the flat path is the ONLY thing growing: NativeAOT.
    /// AOT is bounded by MEMORY, not by the C# stack, so a fixed count was both arbitrary and often too low
    ///, a legitimate 2.5M-deep ref recursion completed in 4 s but died on the old 1,000,000 default. The
    /// cap is therefore derived from this machine's available memory (a quarter of it, which leaves room for
    /// the unwinding that follows a guard hit) divided by the measured per-frame cost, floored at the
    /// previous 1,000,000 so no machine sees a regression and ceilinged at 12,000,000 so a runaway still
    /// stops early on a large server. The process still OOMs CATCHABLY beyond that (measured ~20.3M levels).
    /// An unknown memory limit falls back to the proven 1,000,000.
    /// JIT deliberately keeps the count cap: that path is C#-STACK-bounded (it dies between 40000 and
    /// 100000 levels no matter how much memory there is), so a large cap would be unsafe there.
    /// SLC_VM_MAXFRAMES overrides all of this.</summary>
    private static int DefaultVmMaxFrames()
    {
        // platform: limit, JIT is stack-bounded, so its flat-frame cap is a fixed 30000; AOT has a 32MB stack and derives the cap from physical memory instead (VmMaxFrames). The difference is the point, not an accident: this is why suites must assert catchability or "either X or a clear error" rather than one absolute limit.
        if (RuntimeFeature.IsDynamicCodeSupported) return 30000;   // JIT: stack-bounded, not memory-bounded
        // Budgets a quarter of PHYSICAL RAM (see Interpreter.PhysicalMemoryBytes, whose note explains why
        // GC.GetGCMemoryInfo().TotalAvailableMemoryBytes is not usable here).
        var available = Interpreter.PhysicalMemoryBytes();
        if (available <= 0) return 1_000_000;                      // no memory API: the proven default
        var byMemory = (int)Math.Min(12_000_000L, available / 4 / FrameBytesAot);
        return Math.Max(1_000_000, byMemory);
    }

    /// <summary>Tail-loop iteration cap. A tail call (CALL immediately followed by RETURN) swaps the
    /// callee in place, so the C# stack stays flat, but a genuinely INFINITE tail recursion
    /// (`int loopx(int n) { return loopx(n + 1); }`) would otherwise loop forever. This cap turns
    /// that runaway into the same catchable ScriptException as the flat-call guard, which is a
    /// property a suite asserts (recursion_shapes 2b).
    ///
    /// It is NOT a resource guard the way VmMaxFrames is: a swap returns the caller's locals to the
    /// pool, so an infinite tail recursion costs TIME, not memory. That is why its default is a FIXED
    /// number an order of magnitude above any legit depth instead of the flat cap, and why it is no
    /// longer 100000: the counter is PER INVOCATION and a statement's own tail calls share it, so the
    /// effective limit depended on the caller's emitted shape. Measured on the shipping build, top
    /// level tree-walked: `int depthAt(int n) { return tailRec(n, 0); }` was REFUSED at 100000 because
    /// depthAt's own tail call is swap #1, while the same call made from the tree (whose tail loop has
    /// no cap) answered 100000; and `chk("x", tailRec(100000, 0) == 100000)` was allowed tree-walked but
    /// refused once the statement was compiled, because the tail call into `chk` is swap #100001 of that
    /// invocation. Four suites need 100000 to pass (crash_recursion 3a/5f, test_tailrec,
    /// crash_session_regression 1e and gc_bench section 10), and each passed only while the statement
    /// holding the call happened to be tree-walked.
    /// SLC_VM_MAXFRAMES still sets BOTH caps, so a caller that wants the runaway guard pinned can lower
    /// it; recursion_shapes does, and that is what keeps its unbounded-tail check firing.</summary>
    internal static int TailMaxFrames = Diagnostics.EnvNumber("VM_MAXFRAMES") ?? 1_000_000;

    /// <summary>Sets BOTH frame caps, which is what SLC_VM_MAXFRAMES has always done (see the note above).
    /// Diagnostics.Trace("VM_MAXFRAMES", n) reaches this through the gate registry, so the variable and a script
    /// cannot mean two different things; zero or less restores the platform default (the flat cap from
    /// DefaultVmMaxFrames, the tail cap to its fixed 1,000,000), which is how a script undoes a lowering.
    /// Both caps are plain statics rather than readonly ones for this: every read of them is a comparison on the
    /// VM's hot path, and a static field read costs the same either way.</summary>
    internal static void SetMaxFrames(int frames)
    {
        if (frames <= 0)
        {
            VmMaxFrames = DefaultVmMaxFrames();
            TailMaxFrames = 1_000_000;
            return;
        }
        VmMaxFrames = frames;
        TailMaxFrames = frames;
    }

    /// <summary>Dump the last recorded crash ring to the console (oldest → newest).</summary>
    public static void DumpCrashRing()
    {
        if (LastCrashRing == null) return;
        foreach (var l in LastCrashRing)
            if (l != null) Console.WriteLine("  … " + l);
    }

    /// <summary>Opcode-name lookup for the trace printer (index = (byte)Opcode).</summary>
    private static readonly string[] OpcodeNames = Enum.GetNames<Opcode>();
    private static readonly int MaxOpcodeNameLen = GetMaxOpcodeNameLen();

    private static int GetMaxOpcodeNameLen()
    {
        var max = 0;
        foreach (var name in OpcodeNames)
            if (name.Length > max) max = name.Length;
        return max;
    }

    /// <summary>Fixed width of the `fn=Name[depth]` column in the trace matrix. Long names are
    /// truncated (middle dropped) keeping the trailing `[depth]` so every line keeps its columns:
    /// e.g. fn=TestWithTestWithTestWithTestWith…[0] → fn=TestWithTestWithTestW..[0] (30 chars).</summary>
    private const int TraceFnTagWidth = 30;

    /// <summary>Formats the fn tag for the trace matrix, truncating over-long names to
    /// <see cref="TraceFnTagWidth"/> chars while preserving the trailing `[depth]` marker.</summary>
    private static string FormatFnTag(string fnName, int depth)
    {
        var tag = $"fn={fnName}[{depth}]";
        if (tag.Length <= TraceFnTagWidth) return tag;
        var keep = TraceFnTagWidth - 5;          // reserve "..[N]" (5 chars) at the end
        return tag[..keep] + $"..[{depth}]";
    }

    /// <summary>Semantic description for operand-less opcodes (shown after the fixed columns).</summary>
    private static string OpcodeMeaning(Opcode op) => op switch
    {
        Opcode.NOP => "; no-op",
        Opcode.RETURN => "; return value (top of stack)",
        Opcode.DUP => "; duplicate top of stack",
        Opcode.POP => "; discard top of stack",
        Opcode.ADD => "; pop B, pop A, push A+B",
        Opcode.SUB => "; pop B, pop A, push A-B",
        Opcode.MUL => "; pop B, pop A, push A*B",
        Opcode.DIV => "; pop B, pop A, push A/B",
        Opcode.NEG => "; pop, push -value",
        Opcode.LT => "; pop B, pop A, push A<B",
        Opcode.GT => "; pop B, pop A, push A>B",
        Opcode.EQ => "; pop B, pop A, push A==B",
        Opcode.LE => "; pop B, pop A, push A<=B",
        Opcode.GE => "; pop B, pop A, push A>=B",
        Opcode.NE => "; pop B, pop A, push A!=B",
        Opcode.NOT => "; pop, push !value",
        Opcode.MOD => "; pop B, pop A, push A%B",
        Opcode.BITAND => "; pop B, pop A, push A&B",
        Opcode.BITOR => "; pop B, pop A, push A|B",
        Opcode.BITXOR => "; pop B, pop A, push A^B",
        Opcode.SHL => "; pop B, pop A, push A<<B",
        Opcode.SHR => "; pop B, pop A, push A>>B",
        Opcode.BITNOT => "; pop, push ~value",
        Opcode.IS_TYPE => "; pop value, push (value is T)",
        Opcode.SLICE => "; pop end, start, coll → push sliced",
        Opcode.DISPOSE => "; dispose value",
        Opcode.MATCH_EQ => "; pop pattern, pop subject, push match",
        Opcode.COMP_FINISH => "; finalize comprehension → array",
        Opcode.LOAD_SUPER => "; push super proxy",
        Opcode.ENDTRY => "; end try region",
        Opcode.THROW => "; pop value → throw",
        Opcode.POP_FINALLY => "; pop finally handler (normal path)",
        _ => ""
    };

    /// <summary>Single-line, bounded decode suffix: collapse all whitespace runs (incl. newlines)
    /// to a single space and cap the length so multi-line constants don't explode the trace buffer.</summary>
    private const int TraceDecodeMaxLen = 96;
    private static string SanitizeDecode(string decode)
    {
        if (decode.Length == 0) return decode;
        var sb = new StringBuilder(decode.Length);
        var pendingSpace = false;
        var i = 0;
        var n = decode.Length;
        while (i < n)
        {
            var c = decode[i];
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                i++;
                continue;
            }
            if (pendingSpace)
            {
                if (sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
            i++;
        }
        if (sb.Length > TraceDecodeMaxLen)
        {
            sb.Length = TraceDecodeMaxLen;
            sb.Append("…");
        }
        return sb.ToString();
    }

    /// <summary>Human-readable decode of an opcode's operand bytes (read-only peek through BytecodeReader;
    /// does NOT consume them, the dispatch loop does that). Returns "" for operand-less opcodes
    /// without a semantic entry. For GET_PROP/SET_PROP the property name rides on the operand
    /// stack (pushed by the preceding LOAD_CONST), so the current operand stack + sp are passed
    /// in and the name is read from there, BytecodeReader.ByteAt(code, ip) after these opcodes is the NEXT instruction,
    /// not an operand.</summary>
    private static string DecodeOperand(Bytecode bc, VmValue[] consts, byte[] code, int ip, Opcode op,
        int sp = -1, object?[]? sO = null)
    {
        try
        {
            var r = new BytecodeReader(code, ip);
            static string ConstRef(VmValue[] c, int idx)
            {
                if (idx < 0 || idx >= c.Length) return $"#{idx}";
                var o = c[idx].ToObject();
                return o is string s ? $"'{s}'" : (o?.ToString() ?? "null");
            }
            static string? PropName(int sp, object?[]? sO, int fromTop)
            {
                // fromTop: 1 = top of stack (GET_PROP: [target, name]), 2 = second (SET_PROP: [target, name, value]).
                if (sp < 0 || sO == null) return null;
                var idx = sp - fromTop;
                if (idx < 0 || idx >= sO.Length || sO[idx] is not string s) return null;
                return $"'{s}'";
            }
            static string LocalRef(Bytecode b, int idx)
                => idx >= 0 && idx < b.Locals.Length ? b.Locals[idx] : $"#{idx}";
            static string ArgCount(BytecodeReader r)
                => r.Ip < r.Length ? $"({r.ByteAt(0)} args)" : "(?)";
            static string RelOffset(BytecodeReader r, int field = 0, OperandKind kind = OperandKind.TargetU16)
            {
                var width = BytecodeFormat.FieldSize(OperandField.U16);
                if (r.Ip + field + width > r.Length) return "(?)";
                var off = r.I16At(field);
                // Absolute target: the executor adds the offset once it is PAST the operand shape, so the
                // SHAPE decides where the base is, and `field` where the offset sits inside it, which is
                // not always 0 (FOREACH_CALL carries its end offset last, after a CONST).
                var target = r.Ip + BytecodeFormat.ShapeSize(kind) + off;
                return off >= 0 ? $"+{off} → ip {target}" : $"{off} → ip {target}";
            }
            static string GlobalSlotRef(Bytecode b, int idx)
                => idx >= 0 && b.GlobalSlotNames != null && idx < b.GlobalSlotNames.Length
                    ? $"{b.GlobalSlotNames[idx]} (slot {idx})"
                    : $"slot {idx}";

            var operandDecode = op switch
            {
                Opcode.LOAD_CONST => $"; push const {ConstRef(consts, r.ConstAt(0))}",
                Opcode.LOAD_VAR => $"; push local {LocalRef(bc, r.ByteAt(0))}",
                Opcode.RENEW_LOCAL => $"; renew local binding {LocalRef(bc, r.ByteAt(0))}",
                Opcode.STORE_VAR => $"; pop -> local {LocalRef(bc, r.ByteAt(0))}",
                Opcode.INC => $"; local {LocalRef(bc, r.ByteAt(0))}++",
                Opcode.DEC => $"; local {LocalRef(bc, r.ByteAt(0))}--",
                Opcode.LOAD_REF => $"; ref local {LocalRef(bc, r.ByteAt(0))}",
                Opcode.STORE_CONST => $"; pop -> const local {LocalRef(bc, r.ByteAt(0))}",
                Opcode.ATTACH_TUPLE_NAMES => $"; attach tuple names {ConstRef(consts, r.ConstAt(0))}",
                Opcode.LOAD_GLOBAL_FAST => $"; push global {GlobalSlotRef(bc, r.ByteAt(0))}",
                Opcode.STORE_GLOBAL_FAST => $"; pop -> global {GlobalSlotRef(bc, r.ByteAt(0))}",
                Opcode.GET_PROP => $"; get {PropName(sp, sO, 1) ?? "?"}",
                Opcode.SET_PROP => $"; set {PropName(sp, sO, 2) ?? "?"}",
                Opcode.JMP => $"; jump {RelOffset(r)}",
                Opcode.JMP_IF_FALSE => $"; jump-if-false {RelOffset(r)}",
                Opcode.CALL => $"; call {ArgCount(r)}",
                Opcode.CALL_VAL => $"; call-value {ArgCount(r)}",
                Opcode.CALL_METHOD => $"; call-method {ArgCount(r)}",
                Opcode.CALL_NEW => $"; new {ArgCount(r)}",
                Opcode.NEW_STRUCT => $"; new struct ({r.ByteAt(0)} fields)",
                Opcode.MAKE_LAMBDA => $"; lambda #{r.ByteAt(0)}",
                Opcode.MAKE_LIST => $"; make list ({r.ByteAt(0)} elems)",
                Opcode.MAKE_DICT => $"; make dict ({r.ByteAt(0)} entries)",
                Opcode.CAST => $"; cast to {ConstRef(consts, r.ConstAt(0))}",
                Opcode.AS => $"; as {ConstRef(consts, r.ConstAt(0))}",
                Opcode.NEW_ARRAY => $"; new array (sizes={r.ByteAt(0)} inits={r.ByteAt(1)})",
                Opcode.GET_INDEX => $"; get index ({r.ByteAt(0)})",
                Opcode.ARRAY_GET => "; array get (single index)",
                Opcode.SET_INDEX => $"; set index ({r.ByteAt(0)})",
                Opcode.DESTRUCT => $"; destruct ({r.ByteAt(0)} vars)",
                Opcode.OBJ_INIT => $"; object-init ({r.ByteAt(0)} pairs)",
                Opcode.WITH => $"; with ({r.ByteAt(0)} pairs)",
                Opcode.FOREACH_INIT => $"; foreach init (state={r.ByteAt(0)} idx={r.ByteAt(1)})",
                Opcode.FOREACH_END => $"; foreach dispose (state={r.ByteAt(0)})",
                Opcode.THROW_FLOW_RETURN => $"; return-escaping-finally (unwind={r.ByteAt(0)})",
                Opcode.THROW_FLOW_BREAK => $"; break-escaping-try (jump {RelOffset(r, 0, OperandKind.TargetU16UnwindU8)} unwind={r.ByteAt(BytecodeFormat.FieldOffset(OperandKind.TargetU16UnwindU8, 1))})",
                Opcode.THROW_FLOW_CONTINUE => $"; continue-escaping-try (jump {RelOffset(r, 0, OperandKind.TargetU16UnwindU8)} unwind={r.ByteAt(BytecodeFormat.FieldOffset(OperandKind.TargetU16UnwindU8, 1))})",
                Opcode.FOREACH_NEXT => $"; foreach next (end {RelOffset(r, 0, OperandKind.U16Const)} varType={ConstRef(consts, r.ConstAt(BytecodeFormat.FieldOffset(OperandKind.U16Const, 1)))})",
                Opcode.FOREACH_CALL => $"; foreach call (args={r.ByteAt(0)} state={r.ByteAt(1)} end {RelOffset(r, BytecodeFormat.FieldOffset(OperandKind.U8U8ConstU16, 3), OperandKind.U8U8ConstU16)})",
                Opcode.LOC => $"; set line/col -> L{r.U16At(0)}:C{r.U16At(BytecodeFormat.FieldOffset(OperandKind.LocU8x4, 1))}",
                // Nine opcodes used to be named by OpcodeMeaning, the table documented for OPERAND-LESS
                // opcodes, yet BytecodeFormat declares an operand for every one of them, and the executor
                // reads it (code[ip++], or a 2-byte little-endian target then `ip += 2` for the try/finally
                // three). The trace therefore never showed those operands, so a bytecode dump was blind to
                // operand changes on these opcodes. check-opcode-coverage.sh's operand-table tie found them;
                // every shape below is the executor's own read.
                Opcode.NEW_LIST => $"; create List<> in local {LocalRef(bc, r.ByteAt(0))}",
                Opcode.LIST_APPEND => $"; append top to local {LocalRef(bc, r.ByteAt(0))}",
                Opcode.CATCH_MATCH => $"; pop ex, push (ex matches {ConstRef(consts, r.ConstAt(0))})",
                Opcode.MAKE_INDEX => $"; make ^index (fromEnd={r.ByteAt(0)})",
                Opcode.MAKE_RANGE => $"; make range (hasTo={r.ByteAt(0)})",
                Opcode.TRY => $"; begin try region (catch {RelOffset(r)})",
                Opcode.TRY_FINALLY => $"; push finally handler ({RelOffset(r)})",
                Opcode.END_FINALLY => $"; end finally (resume {RelOffset(r)})",
                // These eight had no arm, so the `_ => ""` fallback below absorbed them and their operands
                // went undecoded in the trace. check-opcode-coverage.sh found the gap; the shapes come from
                // the executor (Vm.cs). TRY_GET_PROP and YIELD take no operand, they pop everything they
                // need, so their arms are stated for coverage, not to read a byte.
                Opcode.EVENT_ADD => $"; event += {ConstRef(consts, r.ConstAt(0))}",
                Opcode.EVENT_SUB => $"; event -= {ConstRef(consts, r.ConstAt(0))}",
                Opcode.MAKE_TUPLE => $"; make tuple ({r.ByteAt(0)} items)",
                Opcode.PAT_LIST => $"; pattern list (descriptor {ConstRef(consts, r.ConstAt(0))})",
                Opcode.REL_PATTERN => $"; relational pattern (selector {r.ByteAt(0)})",
                Opcode.TRY_GET_PROP => "; try get property (name from stack)",
                Opcode.WIDEN_NUM => $"; widen numeric ({ConstRef(consts, r.ConstAt(0))})",
                Opcode.YIELD => "; yield (suspend)",
                Opcode.AWAIT => "; await (suspend; the resume pushes the awaited value)",
                _ => ""
            };
            if (operandDecode.Length > 0) return operandDecode;
            return OpcodeMeaning(op);
        }
        catch
        {
            return " ; (decode error)";   // never crash the VM over a debug print
        }
    }

    /// <param name="wantRefResult">True when the CALLER wants the REFERENCE a ref-returning body answers (`ref int r = ref
    /// F(..)` and `F(..) = 7;`, both of which run on the tree): the outermost RETURN then hands back the cell instead of
    /// dereferencing it. Every other call site leaves it false, and an INNER flat call always derefs, because a compiled
    /// body can only consume a value.</param>
    public static object? Execute(Bytecode bc, object?[] args, Interpreter interp, string? entryName = null, Namespace? nsContext = null, bool wantRefResult = false, FuncDecl? entryDecl = null)
        => ExecuteCore(bc, args, interp, out _, entryName, nsContext, wantRefResult: wantRefResult, entryDecl: entryDecl);

    /// <summary>Executes a bytecode body, optionally RESUMING a VM generator's captured run state.
    /// `suspend` receives a VmSuspendState when the invocation suspends at the outermost boundary
    /// (a generator's YIELD with frames.Count == 0); it stays null when the invocation ran to
    /// completion or aborted. Only Generator.MoveNext uses the suspend-out/resume-in parameters,
    /// every other caller goes through the Execute wrapper above.</summary>
    /// <summary>Boundary wrapper: attaches the script call frames to any error raised by the VM.
    /// This is the ONE place every VM-raised ScriptException passes through, and the throwing
    /// function's frame is still on the interpreter's stack here (its caller pops it in a finally
    /// while unwinding). Errors that already carry frames (raised by the tree's RuntimeError) are
    /// rethrown untouched.
    /// <para>
    /// `entryDecl` is the DECLARATION this body was compiled from, carried so an entry frame can name a position:
    /// measured 2026-10-31, a failure inside a `Com.Run` callback printed `at <lambda>` with nothing a reader could go
    /// to, because the entry frame is built from a NAME and a lambda's name is empty. Optional, so a synthetic body
    /// (the program entry, a carried statement) keeps the unnamed form.
    /// </para></summary>
    internal static object? ExecuteCore(Bytecode bc, object?[] args, Interpreter interp, out VmSuspendState? suspend,
        string? entryName = null, Namespace? nsContext = null, VmSuspendState? resume = null, object? resumeValue = null,
        bool wantRefResult = false, FuncDecl? entryDecl = null)
    {
        try
        {
            return ExecuteCoreInner(bc, args, interp, out suspend, entryName, nsContext, resume, resumeValue, wantRefResult, entryDecl);
        }
        catch (ScriptException ex)
        {
            // A BAIL is not an error, and it is not an abort: the invocation stopped at an instruction the VM
            // cannot run, so the body goes back to the tree as the sentinel every `is VmFallback` site already
            // understands, and no suspension is left behind. Its pending `finally` blocks have ALREADY run by the
            // time it gets here (the handler walk runs them for this signal too, see VmBailException).
            if (IsVmBailSignal(ex)) { suspend = null; return VmFallback.Instance; }
            // A script's own exit() is an ABORT, not an error: it has no error to annotate, and its exit
            // code travels in the exception's VALUE, which the rebuild below replaces with a ScriptErrorInfo,
            // so rerolling it DESTROYS the code (measured: `exit(0)` out of a compiled body reached the
            // CLI as "Error: 0" with exit code 1, where the same file run from source exited 0).
            if (ex.ErrorCode == ErrorCode.ScriptAbort) throw;
            if (ex.Value is ScriptErrorInfo { Frames: not null }) throw;
            var frames = interp.SnapshotCallFrames();
            throw new ScriptException(ex.ErrorCode,
                ex.Value is ScriptErrorInfo sei
                    ? sei with { Frames = frames }
                    : new ScriptErrorInfo(ex.ErrorCode, ex.Message, null, frames),
                ex.Line, ex.Column);
        }
    }

    private static object? ExecuteCoreInner(Bytecode bc, object?[] args, Interpreter interp, out VmSuspendState? suspend, string? entryName = null, Namespace? nsContext = null, VmSuspendState? resume = null, object? resumeValue = null, bool wantRefResult = false, FuncDecl? entryDecl = null)
    {
        suspend = null;
        // A CANCELLED RUN NEVER ENTERS A BODY, which is the third place the kill signal is asked and the one a
        // STRAIGHT-LINE compiled body needs: such a body has no loop back-edge and no statement boundary, so a token
        // cancelled before the run started would otherwise let it run to the end (measured: `RunScript("var q = 1;",
        // false, false, cancelledToken)` answered ok). A resumed coroutine is left alone: its token was already checked
        // when the drive started.
        if (resume == null && Interpreter.RunCancellationRequested)
            throw new ScriptException(ErrorCode.Cancelled, "Stopped: the run was cancelled");
        // Flat VM call stack depth cap, the flat stack grows the HEAP (frames list), not the C# stack,
        // so deep non-tail recursion (sumTo(5000), fib, ...) runs fine far beyond the tree-walker's
        // recursion guard. This cap only guards TRUE runaway recursion (infinite boom(n+1)) from
        // exhausting memory, it throws the same catchable ScriptException the tree-walker raises.
        // Resume: the generator's locals come back from the suspend state (never pooled while
        // suspended, ExecuteResumed hands them straight back). Fresh start: allocate + bind params.
        var locals = resume?.Locals ?? new VmValue[bc.Locals.Length];
        // Bind parameters
        if (resume == null)
            for (var i = 0; i < args.Length && i < locals.Length; i++)
                locals[i] = VmValue.From(args[i]);
        // Remaining locals default to null-kind (K=0, L=0, D=0, O=null), struct default

        // Pre-convert constants once, cached on the Bytecode, reused by every invocation
        var consts = bc.ConstsVm;

        // F4: namespace context of the function whose body is executing, bare-name calls
        // (`helper(n)`) fall back to it when no global/builtin resolves (see the CALL handler).
        var curNs = nsContext;

        // Global fast-slot table (spec A): per-invocation VmValue slots + state (0=unresolved, 1=cached).
        // Cleared after every script-function call, the callee may have mutated any global, so a
        // subsequent read must re-resolve from the real scope. Slot indices are per-Bytecode, so a
        // flat-call callee gets a FRESH table sized to ITS GlobalSlotNames (see the CALL handler).
        // The arrays are pooled per-Execute: flat calls nest strictly LIFO, so a free list of
        // exact-size pairs is safe, the caller's table stays in its SavedFrame, the callee's is
        // returned at frame pop. Array.Empty pairs are never pooled (shared, zero-sized).
        var slotPool = new List<(VmValue[] Slots, byte[] State)>();
        (VmValue[] Slots, byte[] State) RentSlots()
        {
            var len = bc.GlobalSlotNames?.Length ?? 0;
            if (len == 0) return (Array.Empty<VmValue>(), Array.Empty<byte>());
            for (var i = slotPool.Count - 1; i >= 0; i--)
                if (slotPool[i].Slots.Length == len)
                {
                    var pair = slotPool[i];
                    slotPool.RemoveAt(i);
                    Array.Clear(pair.State); // stale 0/1 flags from the previous owner
                    return pair;
                }
            return (new VmValue[len], new byte[len]);
        }
        void ReturnSlots(VmValue[] slots, byte[] state)
        {
            if (slots.Length == 0) return; // Array.Empty is shared, never pool it
            slotPool.Add((slots, state));
        }
        var (globalSlots, globalSlotState) = RentSlots();

        // Locals array pool, same LIFO discipline as the slot pool. The array never escapes the
        // invocation (MAKE_LAMBDA copies captures into its own array; the frame keeps the CALLER's
        // array, which is restored on pop), so the callee's array can be recycled. Clear on return
        // drops RefCell/List/VmForEachState references so idle pooled arrays retain nothing.
        var localsPool = new List<VmValue[]>();
        VmValue[] RentLocals()
        {
            var len = bc.Locals.Length;
            if (len == 0) return Array.Empty<VmValue>();
            for (var i = localsPool.Count - 1; i >= 0; i--)
                if (localsPool[i].Length == len)
                {
                    var arr = localsPool[i];
                    localsPool.RemoveAt(i);
                    return arr; // cleared on return, already zeroed
                }
            return new VmValue[len];
        }
        void ReturnLocals(VmValue[] arr)
        {
            if (arr.Length == 0) return; // Array.Empty is shared, never pool it
            Array.Clear(arr);
            localsPool.Add(arr);
        }

        // Parallel typed operand stack, ints/longs in sL, floats in sD, objects in sO, kinds/types in sK/sT.
        // The hot numeric ops update sL/sD in place (no 32-byte VmValue struct copies).
        // Sized by OperandStackInitial, and Grow() doubles on demand, so a body that needs more pays a
        // resize rather than a fault.
        var sK = new byte[OperandStackInitial];
        var sT = new byte[OperandStackInitial];
        var sL = new long[OperandStackInitial];
        var sD = new double[OperandStackInitial];
        var sO = new object?[OperandStackInitial];
        var sp = 0;

        void Grow()
        {
            var n = sL.Length * 2;
            Array.Resize(ref sK, n);
            Array.Resize(ref sT, n);
            Array.Resize(ref sL, n);
            Array.Resize(ref sD, n);
            Array.Resize(ref sO, n);
        }

        void Push(in VmValue v)
        {
            if (sp == sL.Length) Grow();
            sK[sp] = v.K;
            sT[sp] = v.T;
            sL[sp] = v.L;
            sD[sp] = v.D;
            sO[sp] = v.O;
            sp++;
        }

        VmValue Pop()
        {
            var i = --sp;
            return new VmValue(sK[i], sT[i], sL[i], sD[i], sO[i]);
        }

        VmValue Peek() => new VmValue(sK[sp - 1], sT[sp - 1], sL[sp - 1], sD[sp - 1], sO[sp - 1]);
        var code = bc.Code;
        var ip = 0;
        var curLine = 0;
        var curCol = 0;

        // Exception handlers (invocation-local, nested calls get their own)
        List<(int sp, int catchIp, int finallyIp, bool pendingThrow, VmValue pendingVal, int seq)>? handlers = null;
        var pendingFinally = false;
        // Payload for the pending finally: a ScriptErrorInfo (script exception) or a VmFlowException
        // (return/break/continue escaping a try region). END_FINALLY rethrows whichever it is.
        object? pendingFinallyPayload = null;
        var handlerBase = 0; // handlers is lazily allocated on first TRY, count 0 at entry
        // Program-order sequence counter, disambiguates "try entered BEFORE a foreach" (loop inside
        // the try: exception abandons it → dispose its enumerator) from "try entered AFTER a foreach"
        // (try inside the loop body: exception is caught locally → the loop must keep its enumerator).
        // Stack depths alone collide in that case; the sequence does not.
        var seq = 0;
        List<(int stateLocal, int seq)>? forEachStates = null;

        // ── Flat VM call stack ──
        // User-function CALL pushes a SavedFrame and swaps execution state in place instead of
        // re-entering InvokeScriptFunction → Vm.Execute (nested C# calls). Deep non-tail recursion
        // therefore grows this heap list, not the C# stack. The operand stack (sp) is shared across
        // frames; each frame records its SpBase so `sp > SpBase` return checks and absolute handler
        // `sp` restores remain correct.
        var frames = new List<SavedFrame>();
        var spBase = 0;
        var tailSwaps = 0; // per-invocation tail-swap counter, guards infinite tail recursion (TailMaxFrames)
        // The Coroutine handle of the CURRENT function when it is a VM generator entered/resumed by
        // a FOREACH_NEXT drive (null for the boundary generator driven by Generator.MoveNext and for
        // plain functions). The YIELD handler keys the boundary-vs-nested decision on it: a nested
        // generator always has its driver frame in `frames` (GeneratorHandle == curGenCo).
        var curGenCo = (object?)null;
        // Drops abandoned suspended-generator frames that sit ABOVE the real caller: a generator
        // whose foreach loop ended early (break/return/throw) leaves its SuspendedGenerator frame in
        // `frames`, the current function is NOT that generator, so its frame is not a caller and
        // must never be popped as one. Marked Dead so a later drive cannot resume a state whose loop
        // is gone.
        void DropAbandonedGeneratorFrames(Coroutine? only = null)
        {
            while (frames.Count > 0 && frames[^1].SuspendedGenerator
                   && (only == null || ReferenceEquals(frames[^1].GeneratorHandle, only)))
            {
                var ab = frames[^1];
                if (ab.GeneratorHandle is Coroutine abCo)
                {
                    // The status the TREE reports depends on WHICH KIND of coroutine this is, measured on the tree
                    // with both shapes side by side:
                    //     g = CountTo(3)                          (owned by Generator<T>)  after a break -> Suspended
                    //     Coroutine.Start(CountTo, 3, false)      (user-started)           after a break -> Waiting
                    // `Coroutine.IsGenerator` is that distinction in the engine's own words ("true when owned by
                    // Generator<T>, not user-started via Coroutine.Start"), and the YIELD handler already reports
                    // `Suspended` for a driven generator (Vm.cs:1962), so the tree's generator case is the status a
                    // suspension ALREADY had and this line must not overwrite it. 14b pins the user-started shape
                    // (`Waiting`, and it always was), which is why the old unconditional `Waiting` passed while the
                    // call-created one diverged.
                    abCo.Status = abCo.IsGenerator ? CoroutineStatus.Suspended : CoroutineStatus.Waiting;
                    // CAPTURE THE SUSPENSION instead of destroying it, so a LATER drive RESUMES where this one
                    // stopped instead of answering nothing (the divergence the tree does not have: it drives a
                    // generator through MoveNext and keeps its own resume state). This is the half the `VmState =
                    // null` here was missing, and clearing it was a NO-OP in any case, because a generator driven by
                    // the VM's folded foreach keeps its suspension in the RETAINED FRAME, never in `VmState`.
                    // Only a CLEAN operand region is captured (`Sp == SpBase`, the shape a `yield` at a loop's
                    // statement level leaves): the materialized stack would have to be read out of the parallel
                    // operand arrays, which this helper does not have in scope, and a resume that guessed at a live
                    // region would be worse than the old silent end. Anything else keeps today's behaviour.
                    if (ab.Sp == ab.SpBase)
                        abCo.VmState = new VmSuspendState
                        {
                            Bc = ab.Bc,
                            Code = ab.Code,
                            Stack = Array.Empty<VmValue>(),
                            Sp = 0,
                            SpBase = ab.SpBase,
                            Locals = ab.Locals,
                            Frames = [ab],
                            Handlers = ab.Handlers,
                            HandlerBase = ab.HandlerBase,
                            Ip = ab.Ip,
                            CurLine = ab.CurLine,
                            CurCol = ab.CurCol,
                            PendingFinally = ab.PendingFinally,
                            PendingFinallyPayload = ab.PendingFinallyPayload,
                            Seq = ab.Seq,
                            ForEachStates = ab.ForEachStates,
                            // The namespace leaf a resumed body needs to resolve a namespace-qualified name: it
                            // lives on the runtime ScriptFunction (`Fn.NamespaceContext`), which is where a
                            // tree-side resume reads it too (CoroutineTypes.cs:273).
                            NsContext = abCo.Fn.NamespaceContext,
                            CurrentFunc = ab.Name,
                        };
                    else
                        abCo.VmState = null;
                }
                // The abandoned generator's LOCALS must stay alive when they were captured above: the snapshot holds
                // the array, and pooling it would hand the generator's continuation to the next invocation to
                // overwrite. That is the rule the boundary suspension already follows ("locals already restored
                // above (resume.Locals), never pooled at suspend time"). The fast-slot table is not part of the
                // snapshot, so it still goes back, and a resume re-derives globals as a fresh invocation does.
                if (!(ab.GeneratorHandle is Coroutine abCap && abCap.VmState != null))
                    ReturnLocals(ab.Locals);
                ReturnSlots(ab.GlobalSlots, ab.GlobalSlotState);
                frames.RemoveAt(frames.Count - 1);
            }
        }


        // Trace context: current function name ("" = top-level entry) + crash ring buffer.
        // The ring keeps the last N trace lines so a fatal exception that escapes the VM can
        // still be reported with the instruction context that led to it.
        var currentFunc = entryName ?? "";
        // Only when the trace is ON, and then once per invocation: 64 slots hold the instruction context a fatal
        // exception is reported with, so a normal run never pays for it.
        var crashRing = Trace ? new string[64] : null;
        var crashRingPos = 0;
        var crashRingCount = 0;
        if (crashRing != null) LastCrashRing = crashRing;

        // A fault from an awaited task, carried out of the resume block below: that block runs BEFORE the try that
        // owns the handler walk, so a throw there would escape a script's own `try`. It is thrown on the first
        // line INSIDE that try instead, which is what makes an in-body catch see it.
        Exception? pendingAwaitFault = null;

        // ── Resume a boundary-suspended VM generator (VmSuspendState): reinstate every
        // per-invocation local so the main loop continues exactly where YIELD left off.
        if (resume != null)
        {
            var st = resume;
            bc = st.Bc;
            code = st.Code;
            consts = st.Bc.ConstsVm;
            ip = st.Ip;
            curLine = st.CurLine;
            curCol = st.CurCol;
            curNs = st.NsContext;
            // Operand stack, materialize the parallel typed arrays back from the captured VmValue[].
            if (st.Stack.Length > sK.Length)
            {
                var n = st.Stack.Length;
                Array.Resize(ref sK, n);
                Array.Resize(ref sT, n);
                Array.Resize(ref sL, n);
                Array.Resize(ref sD, n);
                Array.Resize(ref sO, n);
            }
            for (var i = 0; i < st.Sp; i++)
            {
                var v = st.Stack[i];
                sK[i] = v.K;
                sT[i] = v.T;
                sL[i] = v.L;
                sD[i] = v.D;
                sO[i] = v.O;
            }
            sp = st.Sp;
            // locals already restored above (resume.Locals), never pooled at suspend time.
            frames = st.Frames;
            spBase = st.SpBase;
            handlers = st.Handlers;
            handlerBase = st.HandlerBase;
            pendingFinally = st.PendingFinally;
            pendingFinallyPayload = st.PendingFinallyPayload;
            seq = st.Seq;
            forEachStates = st.ForEachStates;
            currentFunc = st.CurrentFunc;
            if (st.PushResumeValue)
            {
                // An AWAIT suspension: the awaited value belongs in the operand slot the enclosing expression
                // expects, resolved through the ONE rule the tree uses (the Task owns the value AND the fault).
                try { Push(VmValue.From(Interpreter.AwaitOutcome(resumeValue))); }
                catch (Exception awaitEx) { pendingAwaitFault = awaitEx; }
            }
            // Fresh invocation: the tail-swap guard restarts. The generator's own globals slot table
            // was NOT retained (a cleared table just re-resolves on the next FAST load), the table
            // sized above (RentSlots with bc = st.Bc) is correct. Generators never consume a resume
            // value (tree parity, VisitYieldExpr skips generators), so `resumeValue` is not pushed.
        }

    restart:
        try
        {
            if (pendingAwaitFault != null)
            {
                // THE FAULT IS THROWN ONCE. `restart:` is also where a script-owned `catch` arm jumps back
                // to, so a field left set re-threw the SAME fault after the catch had been entered and
                // trampled it: measured, a COMPILED `async` body could not catch a fault raised by the task
                // it awaited (the catch ran, its very next `goto restart` raised the identical exception
                // again, and the fault escaped the try that was supposed to hold it). Clearing it here is
                // what hands the exception to the handler walk exactly once, which is why the compiler no
                // longer has to decline an `async` body that holds a `try`.
                var awaitedFault = pendingAwaitFault;
                pendingAwaitFault = null;
                throw awaitedFault;
            }
            while (ip < code.Length)
            {
                var op = (Opcode)BytecodeReader.U8(code, ref ip);
                if (Trace)
                {
                    var opName = (int)op < OpcodeNames.Length ? OpcodeNames[(int)op] : ((int)op).ToString();
                    var decode = DecodeOperand(bc, consts, code, ip, op, sp, sO);
                    decode = SanitizeDecode(decode);
                    var src = interp.CurrentSourceName;
                    // Print the source file only when it changes (import/module boundary), not per line.
                    if (!string.Equals(src, LastTracedSource, StringComparison.Ordinal))
                    {
                        LastTracedSource = src;
                        var banner = src.Length > 0 ? $"[vm] ── src={src} ──" : "[vm] ── src=<inline> ──";
                        Console.WriteLine(banner);
                        if (crashRing != null)
                        {
                            crashRing[crashRingPos] = banner;
                            crashRingPos = (crashRingPos + 1) % crashRing.Length;
                            if (crashRingCount < crashRing.Length) crashRingCount++;
                        }
                    }
                    // Compact flow layout: fixed [Lx:Cy] anchor first (18 wide), then the fn tag as
                    // a FIXED 30-wide field (blank on untagged/top-level lines, the tag is
                    // variable-length, so without the field its presence shifts the opcode/ip=/sp=
                    // columns right on tagged lines vs. untagged ones), then opcode, then ip= and
                    // sp= (padded to 10 so the decode column stays separated).
                    // e.g. [vm] [L107:C25]                           RETURN               ip=10      sp=1        ; return value (top of stack)
                    //      [vm] [L103:C9]         fn=DeepSub.Fire[0] RETURN               ip=37      sp=1        ; return value (top of stack)
                    var fnTag = (currentFunc.Length > 0 ? FormatFnTag(currentFunc, frames.Count) : "").PadRight(TraceFnTagWidth + 1);
                    var baseLine = $"[vm] " +
                                   $"[L{curLine}:C{curCol}]".PadRight(18) +
                                   fnTag +
                                   $"{opName.PadRight(MaxOpcodeNameLen)}  " +
                                   $"ip={ip - 1}".PadRight(10) + " " +
                                   $"sp={sp}".PadRight(10);
                    var line = decode.Length > 0 ? baseLine + "  " + decode : baseLine;
                    Console.WriteLine(line);
                    // The ring is allocated only when Trace was ALREADY on at this body's entry, and a script can
                    // turn the trace on mid-run (`VmSetTrace(true)`, which is what _casecheck and
                    // diagnostics_trace_test do): the null check is what keeps the TRACE from crashing a run it
                    // was just asked to describe. Found as an unguarded `crashRing![...]` throwing
                    // NullReferenceException at exactly that moment, with the top-level VM path on.
                    if (crashRing != null)
                    {
                        crashRing[crashRingPos] = line;
                        crashRingPos = (crashRingPos + 1) % crashRing.Length;
                        if (crashRingCount < crashRing.Length) crashRingCount++;
                    }
                }
                switch (op)
                {
                    case Opcode.LOC:
                        curLine = BytecodeReader.U16(code, ref ip);
                        curCol = BytecodeReader.U16(code, ref ip);
                        // The interpreter's "statement now executing", so a refusal raised from EITHER engine
                        // reports the same place (see Interpreter.Refusal). The tree writes it from Execute(Stmt).
                        interp.SetCurrentStatement(curLine, curCol);
                        // A STATEMENT BOUNDARY, which is where the tree drains the Task continuation queue (its loop
                        // does it after every statement). A compiled body has no other such point, so a `ContinueWith`
                        // registered while its task was still running fired only after an explicit `sleep` in a
                        // `--no-source` bundle (measured: `cw3=False` before the sleep, `True` after, against `True`
                        // immediately from the file). The compiler emits one LOC per statement, so this costs one
                        // empty-queue read per statement.
                        if (Interpreter.DrainMainThreadQueue())
                            Array.Clear(globalSlotState);
                        break;

                    case Opcode.NOP:
                        break;

                    case Opcode.LOAD_CONST:
                        Push(consts[BytecodeReader.Const(code, ref ip)]);
                        break;

                    case Opcode.LOAD_VAR:
                        {
                            // Typed local access (spec C), push the existing tagged VmValue with no
                            // ToObject()/From round-trip on the hot path. RefCell locals (ref/out params)
                            // still deref through the cell to the caller's live value.
                            var lvi = BytecodeReader.U8(code, ref ip);
                            var lv = locals[lvi];
                            if (lv.O is RefCell rc) Push(VmValue.From(rc.Get()));
                            else Push(lv);
                            break;
                        }

                    case Opcode.RENEW_LOCAL:
                        {
                            // A fresh capture CELL for the slot, keeping the current value: the declaration
                            // this belongs to is a new binding every time it executes, exactly as the tree's
                            // VisitVarDeclStmt makes a new VariableInfo. Sharing the slot's existing cell
                            // (which is what MAKE_LAMBDA does on demand) made every closure over a loop
                            // local read the LAST iteration's value.
                            var renewSlot = BytecodeReader.U8(code, ref ip);
                            var renewCur = locals[renewSlot].ToObject();
                            object? renewHeld = renewCur is RefCell renewRc ? renewRc.Get() : renewCur;
                            locals[renewSlot] = VmValue.From(new RefCell(() => renewHeld, v => renewHeld = v));
                            break;
                        }
                    case Opcode.STORE_VAR:
                        {
                            var svi = BytecodeReader.U8(code, ref ip);
                            if (bc.ConstLocals != null && svi < bc.ConstLocals.Length && bc.ConstLocals[svi])
                                throw Interpreter.VmRuntimeError<ScriptException>($"Cannot assign to constant variable '{bc.Locals[svi]}'", curLine, curCol);
                            var sv = Pop();
                            // A STRUCT IS A VALUE TYPE, so a store COPIES it: the compiled path takes its
                            // assignments here, with no CoerceTo in between (`var b = a;` is LOAD_VAR + STORE_VAR),
                            // which is why the tree obeyed the rule and the VM did not (measured: `a.X=5 b.X=99`
                            // on the tree against `a.X=99 b.X=99` from a compiled body). The receiver and a
                            // `ref`/`out` argument never arrive here, so sharing there is untouched.
                            if (sv.K == 3 && sv.O is StructInstance svStruct)
                                sv = VmValue.From(Interpreter.CloneStructForStore(svStruct));
                            // A WRITE THROUGH A CELL may land on a GLOBAL (a ref local or parameter bound to one), and
                            // this body's LOAD_GLOBAL_FAST cache would keep serving the value it read before: measured,
                            // `ref int rg = ref g; rg = 5;` answered `top=1 fn=5` from a bundle (the cached read against a
                            // real one from a function). Cells are rare, so clearing here costs a rare Array.Clear.
                            // A ref LOCAL declared below the top level is the case both branches exist for: RENEW_LOCAL
                            // installed a fresh cell for THIS execution's binding, and then this store installs the
                            // initializer's value. When the initializer is itself a reference (`ref int r = ref box.F`)
                            // the binding IS that reference, so the cell REPLACES the renew cell; writing the cell INTO
                            // the renew cell would nest two cells and hand a RefCell to the next arithmetic operand
                            // (measured: `r = r + 1` answered "cannot convert 'RefCell' to number").
                            if (locals[svi].O is RefCell rc2)
                            {
                                if (sv.O is RefCell) { locals[svi] = sv; }
                                else rc2.Set(sv.ToObject());
                                Array.Clear(globalSlotState);
                            }
                            else locals[svi] = sv; // typed write, no boxing round-trip
                            break;
                        }

                    case Opcode.STORE_CONST:
                        {
                            // Typed write (spec C), same RefCell split, no const guard (internal temp store).
                            var svi = BytecodeReader.U8(code, ref ip);
                            var sv = Pop();
                            if (locals[svi].O is RefCell rc3)
                            {
                                if (sv.O is RefCell) { locals[svi] = sv; }   // a reference binding replaces, never nests
                                else rc3.Set(sv.ToObject());
                                Array.Clear(globalSlotState);   // same reason as STORE_VAR: the cell may address a global
                            }
                            else locals[svi] = sv;
                            break;
                        }

                    case Opcode.LOAD_REF:
                        {
                            var rvi = BytecodeReader.U8(code, ref ip);
                            // O is the argument VALUE, and a slot can hold a RefCell (a ref/out param, a closure
                            // capture), so it is dereferenced here. The CALL reaches the caller actual slot through L,
                            // which is what the shared-cell path uses, so ref/out write-through is untouched. Measured
                            // before this: typeof(x) answered "RefCell" on the VM for a ref parameter, a global passed
                            // by ref and a captured local, while the tree answered the value; a script callee was
                            // unaffected, because its own LOAD_VAR derefs, so only a builtin or .NET callee saw it.
                            var refHeld = locals[rvi].ToObject();
                            Push(new VmValue(4, 0, rvi, 0, refHeld is RefCell refCell ? refCell.Get() : refHeld));
                            break;
                        }

                    case Opcode.ADD:
                    case Opcode.SUB:
                    case Opcode.MUL:
                    case Opcode.DIV:
                        {
                            // In-place typed fast path, both longs (or both doubles): no struct copies
                            if (sp >= 2 && sK[sp - 1] == 1 && sK[sp - 2] == 1)
                            {
                                sp--;
                                var b = sL[sp];
                                var a = sL[sp - 1];
                                sL[sp - 1] = op switch
                                {
                                    Opcode.ADD => a + b,
                                    Opcode.SUB => a - b,
                                    Opcode.MUL => a * b,
                                    Opcode.DIV => b == 0 ? throw Interpreter.VmRuntimeError<ScriptException>("Division by zero", curLine, curCol) : a / b,
                                    _ => 0L
                                };
                                // The RESULT's script type follows C#'s binary numeric promotion: two SMALL
                                // signed operands (int/byte/short/sbyte, tags 1..4) make an int, and a long (or
                                // an unsigned) operand makes a long. Writing 0 here made every computed integer
                                // a "long" where the tree answered "int": measured, `var s = 1 + 2;
                                // typeof(s)` was "long" (probe-toplevel-vm section 10, and vm_typeof_parity's
                                // "int arithmetic is 'int' in both" / "short + short promotes to 'int' in both").
                                var ta = sT[sp - 1];
                                var tb = sT[sp];
                                sT[sp - 1] = IntegralResultTag(ta, tb);
                                break;
                            }

                            if (sp >= 2 && sK[sp - 1] == 2 && sK[sp - 2] == 2)
                            {
                                sp--;
                                var b = sD[sp];
                                var a = sD[sp - 1];
                                sD[sp - 1] = op switch
                                {
                                    Opcode.ADD => a + b,
                                    Opcode.SUB => a - b,
                                    Opcode.MUL => a * b,
                                    Opcode.DIV => a / b,
                                    _ => 0.0
                                };
                                break;
                            }

                            var vb = Pop();
                            var va = Pop();
                            // Struct operator overloads (`a + b` where a is a StructInstance with an
                            // operator+): tree parity with ApplyBinaryOp's struct branch, check BEFORE
                            // numeric fallback. Only when the left (or right) operand is a struct that
                            // declares the operator; otherwise fall through to Arith.
                            var opTok = op switch
                            {
                                Opcode.ADD => TokenType.Plus,
                                Opcode.SUB => TokenType.Minus,
                                Opcode.MUL => TokenType.Star,
                                Opcode.DIV => TokenType.Slash,
                                _ => TokenType.Plus
                            };
                            // A script CLASS OR STRUCT operand goes to the interpreter's OWN ApplyBinaryOp, which
                            // is where the tree resolves a class/struct operator AND where its "Operator '+' is
                            // not defined for 'Bare'" message is produced. The struct arms used to delegate and
                            // the CLASS arm used to bail instead, which meant a class that DECLARES the operator
                            // could never use it from a compiled body: `(new Box(3) * new Box(4)).V` refused with
                            // "stopped part-way through" (crash_operator_overload 2a). One rule, one place, and
                            // the refusal for a class with no operator is the tree's own.
                            if (IsScriptInstanceValue(va) || IsScriptInstanceValue(vb))
                            {
                                Push(VmValue.From(interp.ApplyBinaryOpFromVm(va.ToObject(), vb.ToObject(), opTok, curLine, curCol)));
                                break;
                            }
                            // An EVENT member (`p.OnPublish += handler` / `-= h`) SUBSCRIBES here: the tree's
                            // compound assign routes those two operators to EventInfo.Add/Remove, and a compiled
                            // body reaches the same member as GET_PROP + ADD/SUB + DUP + SET_PROP, so this is
                            // where the subscription has to happen. Handled BEFORE the delegate branch, which
                            // would otherwise try to COMBINE the EventInfo with the handler ("Cannot combine
                            // non-delegate type 'EventInfo' with delegate"). The SAME EventInfo is pushed so the
                            // SET_PROP that follows writes back the object it read (see its own no-op arm).
                            if (va.K == 3 && va.O is Interpreter.EventInfo evLeft
                                && op is Opcode.ADD or Opcode.SUB)
                            {
                                interp.SubscribeClassEvent(evLeft, vb.ToObject(), op == Opcode.SUB, curLine, curCol);
                                Push(va);
                                break;
                            }
                            if (vb.K == 3 && vb.O is Interpreter.EventInfo evRight
                                && op is Opcode.ADD or Opcode.SUB)
                            {
                                interp.SubscribeClassEvent(evRight, va.ToObject(), op == Opcode.SUB, curLine, curCol);
                                Push(vb);
                                break;
                            }
                            // Arith handles string concat (ADD) and numerics. Delegate arithmetic:
                            // `a + b` where either operand is a delegate combines (tree parity,
                            // ApplyBinaryOp Plus → CombineDelegates), BUT string concat wins FIRST
                            // (tree: `Plus when left is string || right is string` → $"{left}{right}"
                            // is checked before the delegate branch). SUB/MUL/DIV on delegates are not
                            // supported (the tree raises "Cannot convert ... to number"), so fall back
                            // to the tree-walker for the proper error message.
                            var oaDel = va.K == 3 && va.O is ScriptFunction or CombinedDelegate;
                            var obDel = vb.K == 3 && vb.O is ScriptFunction or CombinedDelegate;
                            if (oaDel || obDel)
                            {
                                if (op != Opcode.ADD)
                                {
                                    // SUB/MUL/DIV on a delegate is the TREE'S to refuse ("Cannot convert '…' to
                                    // number"), and it produces that message from ApplyBinaryOp, so DELEGATING gives the
                                    // right wording AND does not bail: a bail refuses a bundle's whole one-body entry
                                    // (measured: vm_delegate check 5, "delegate '-' error parity", failed from a bundle
                                    // with "stopped part-way through its compiled body").
                                    Push(VmValue.From(interp.ApplyBinaryOpFromVm(va.ToObject(), vb.ToObject(), opTok, curLine, curCol)));
                                    break;
                                }
                                if (va.K == 3 && va.O is string || vb.K == 3 && vb.O is string)
                                {
                                    Push(Arith(op, va, vb, curLine, curCol));   // string + delegate → concat (tree parity)
                                    break;
                                }
                                Push(VmValue.From(interp.CombineDelegates(va.O, vb.O, curLine, curCol)));
                                break;
                            }
                            // An OBJECT operand the VM cannot do arithmetic on itself (DateTime, TimeSpan, a
                            // Guid, ...) goes to the TREE'S binary-operator core, which has the arm for it
                            // (ApplyBinaryOp: "DateTime - DateTime => TimeSpan"). Aborting the body instead
                            // was measured to be unusable: a program body that has already stored a global
                            // cannot be handed to the tree, so a top-level `(DateTime.Now - t0)` after a
                            // `var` was refused as an engine gap while the same code in a function worked.
                            // A STRING CONCAT is excluded: that is Arith's own arm (`+` with a string on either
                            // side) and it is the one that routes through the display rule
                            // (ValueFormatter.ToDisplay), so delegating a concat printed a raw type name where
                            // the VM already printed the friendly one (measured: crash_typeof_matrix 9e/9f
                            // answered "DispatchVtbl.ComNode").
                            var stringConcat = op == Opcode.ADD
                                && (va is { K: 3, O: string } || vb is { K: 3, O: string });
                            // A BOXED char counts as unmodelled for ARITHMETIC even though the list above names
                            // char: a char promotes to int in C#, so `char.MaxValue + 1` is an int, and Arith's
                            // numeric tail answered a DOUBLE (crash_smallint_promote). The concat arm above still
                            // wins, so `'a' + "b"` stays Arith's own.
                            var boxedChar = va is { K: 3, O: char } || vb is { K: 3, O: char };
                            // A STRING operand on a NON-concat operator is the TREE's to REFUSE: Arith's numeric
                            // tail called ToDoubleV on it, so `"ab" * 2` threw the CLR's own "The input string
                            // 'ab' was not in a correct format." where the tree says "Cannot convert 'ab' to
                            // number" (crash_extension_generics L528). The concat arm above still owns `+`.
                            var boxedString = va is { K: 3, O: string } || vb is { K: 3, O: string };
                            if (!stringConcat && (UnmodelledArithOperand(va) || UnmodelledArithOperand(vb) || boxedChar || boxedString))
                            {
                                Push(VmValue.From(interp.ApplyBinaryOpFromVm(va.ToObject(), vb.ToObject(), opTok, curLine, curCol)));
                                break;
                            }
                            Push(Arith(op, va, vb, curLine, curCol));
                            break;
                        }

                    case Opcode.MOD:
                    case Opcode.BITAND:
                    case Opcode.BITOR:
                    case Opcode.BITXOR:
                    case Opcode.SHL:
                    case Opcode.SHR:
                        {
                            var b = Pop();
                            var a = Pop();
                            // A script CLASS or STRUCT owns `%`, `&`, `|`, `^`, `<<` and `>>` through its operator
                            // overload exactly as it owns `+`, and this arm read both operands through AsInt64V,
                            // so `(new Vec(9) % new Vec(4))` answered "Modulo by zero" where the tree called the
                            // operator (crash_operator_overload 1d). Delegated WHOLESALE to the interpreter's own
                            // ApplyBinaryOp, which is where the tree resolves a user-defined operator, so the two
                            // engines cannot disagree about which arm runs. The test is "is this a plain number"
                            // rather than "is this a script instance", because a CLR ENUM member needs the same
                            // delegation for a different reason: ApplyBinaryOp UNWRAPS an enum, and reading one
                            // through AsInt64V answered 0, so `RegexOptions.IgnoreCase | 8` was 8 where the tree
                            // answers 9 (crash_regex 6f).
                            var bitTok = op switch
                            {
                                Opcode.MOD => TokenType.Percent,
                                Opcode.BITAND => TokenType.Ampersand,
                                Opcode.BITOR => TokenType.Pipe,
                                Opcode.BITXOR => TokenType.Caret,
                                Opcode.SHL => TokenType.LeftShift,
                                _ => TokenType.RightShift
                            };
                            if (!IsNumericValue(a) || !IsNumericValue(b))
                            {
                                Push(VmValue.From(interp.ApplyBinaryOpFromVm(a.ToObject(), b.ToObject(), bitTok, curLine, curCol)));
                                break;
                            }
                            Push(Bitwise(op, a, b, curLine, curCol));
                            break;
                        }

                    case Opcode.NEG:
                        {
                            var v = Pop();
                            // A BOXED non-number (`-"hello"`) is the TREE's to answer: ToDoubleV threw the
                            // runtime's own "The input string 'hello' was not in a correct format.", a raw
                            // exception that also escaped the script's own `try` (error_msgs_test). The
                            // interpreter's unary core answers the engine's own wording. The test is the shared
                            // "is this a plain number", so a bool and a null take the same road.
                            if (!IsNumericValue(v))
                            {
                                Push(VmValue.From(Interpreter.ApplyUnaryMinusFromVm(v.ToObject(), curLine, curCol)));
                                break;
                            }
                            // A TAGGED integral keeps the PROMOTED type, which is what C# does with a unary
                            // minus: sbyte/short/byte/ushort promote to int, so `-sbyte.MaxValue` is an int and
                            // `typeof(-a)` for an int is "int", both as the tree answers. `new VmValue(-v.L)`
                            // answered an untagged long, the same tag loss the Bitwise arm had.
                            Push(v.K == 1
                                ? new VmValue(-v.L, PromotesToInt(v.T) ? (byte)1 : v.T)
                                : v.K == 2 ? new VmValue(-v.D) : new VmValue(-ToDoubleV(v)));
                            break;
                        }

                    case Opcode.WIDEN_NUM:
                        {
                            // Widen a narrower runtime numeric to the variable's declared type
                            // (`long sum = 0; sum = 0;` keeps sum long). No-op for anything that is
                            // not a narrower numeric, dynamic typing stays untouched otherwise.
                            var ci = BytecodeReader.Const(code, ref ip);
                            var decl = consts[ci].O as TypeInfo;
                            var v = Pop().ToObject();
                            if (decl != null)
                            {
                                v = decl.BaseType switch
                                {
                                    TokenType.Long when v is int or byte or short or uint or ushort => Convert.ToInt64(v),
                                    TokenType.Double when v is int or byte or short or uint or ushort or long or ulong => Convert.ToDouble(v),
                                    TokenType.Decimal when v is int or byte or short or uint or ushort or long or ulong => Convert.ToDecimal(v),
                                    TokenType.Float when v is int or byte or short or uint or ushort or long or ulong => Convert.ToSingle(v),
                                    _ => v
                                };
                            }
                            Push(VmValue.From(v));
                            break;
                        }

                    case Opcode.BITNOT:
                        {
                            var v = Pop();
                            // A NON-NUMBER operand is the TREE's to answer, exactly as the unary minus below:
                            // `~true` was computed here as ~1 = -2 where the tree refuses it with "Bitwise
                            // complement operator '~' not defined for type bool" (error_msgs_test), and a
                            // refusal the argument still does not raise is a script's `try` that never fires.
                            if (!IsNumericValue(v))
                            {
                                Push(VmValue.From(Interpreter.ApplyBitwiseComplementFromVm(v.ToObject(), curLine, curCol)));
                                break;
                            }
                            // C# promotes the operand first, so `~` of a sbyte/short/byte/ushort is an int, which is
                            // what the tree's ApplyBitwiseComplement answers too (it complements the CLR type it was
                            // handed, and a small integral arrives as an int). PromotesToInt is the one statement of
                            // "this tag becomes an int".
                            Push(v.K == 1
                                ? new VmValue(~v.L, PromotesToInt(v.T) ? (byte)1 : v.T)
                                : new VmValue(~AsInt64V(v)));
                            break;
                        }

                    case Opcode.LT:
                    case Opcode.GT:
                    case Opcode.EQ:
                    case Opcode.LE:
                    case Opcode.GE:
                    case Opcode.NE:
                        {
                            // In-place typed fast path: both longs (matches Compare's long branch exactly),
                            // and both SIGNED: an unsigned tag (uint=5, ushort=6, ulong=7) keeps its value in
                            // the signed payload, so `ulong.MaxValue > 0` compared -1 against 0 here and
                            // answered False (probe-toplevel-vm section 17, crash_numeric_types,
                            // crash_type_check). Those pairs fall through to Compare, which widens them.
                            if (sp >= 2 && sK[sp - 1] == 1 && sK[sp - 2] == 1
                                && sT[sp - 1] is not (5 or 6 or 7) && sT[sp - 2] is not (5 or 6 or 7))
                            {
                                sp--;
                                var b = sL[sp];
                                var a = sL[sp - 1];
                                var r = op switch
                                {
                                    Opcode.LT => a < b,
                                    Opcode.GT => a > b,
                                    Opcode.EQ => a == b,
                                    Opcode.LE => a <= b,
                                    Opcode.GE => a >= b,
                                    Opcode.NE => a != b,
                                    _ => false
                                };
                                sp--;
                                sK[sp] = 5;              // bool kind, no boxing
                                sL[sp] = r ? 1 : 0;
                                sD[sp] = 0;
                                sO[sp] = null;
                                sp++;
                                break;
                            }

                            var cb = Pop();
                            var ca = Pop();
                            // A script CLASS or STRUCT owns EVERY comparison through its operator overload,
                            // including == and !=: without this arm `new Vec(1) == new Vec(1)` fell through to
                            // Compare, which answers AreEqual, so the operator never ran and the answer was False
                            // (crash_operator_overload 1e, and 3b where the operator body's own tolerant `==` is
                            // what makes the outer comparison True). Delegated WHOLESALE to the interpreter's own
                            // ApplyBinaryOp, the place the tree resolves a user-defined operator, so a type with
                            // no operator for the token answers the tree's own message from the same core.
                            //
                            // The ORDERING of any BOXED operand goes the same way, because ApplyBinaryOp unwraps
                            // an enum before comparing and has the arm for the other boxed kinds: `Http.OK > 100`
                            // answered the numeric FALSE (Compare sees a non-numeric object) where the tree
                            // answers True, and a DateTime comparison answered FALSE where the tree REFUSES it
                            // ("Cannot convert '...' to number"). EQ/NE stay on Compare for a non-instance
                            // operand: AreEqual is the engine's own tolerant equality and already matches the
                            // tree for a string, a DateTime and an enum member.
                            var orderingOfBoxed = op is Opcode.LT or Opcode.GT or Opcode.LE or Opcode.GE
                                                  && (ca.K == 3 || cb.K == 3);
                            if (IsScriptInstanceValue(ca) || IsScriptInstanceValue(cb) || orderingOfBoxed)
                            {
                                var cmpTok = op switch
                                {
                                    Opcode.LT => TokenType.Less,
                                    Opcode.GT => TokenType.Greater,
                                    Opcode.LE => TokenType.LessEqual,
                                    Opcode.GE => TokenType.GreaterEqual,
                                    Opcode.EQ => TokenType.EqualEqual,
                                    _ => TokenType.BangEqual
                                };
                                Push(VmValue.From(interp.ApplyBinaryOpFromVm(ca.ToObject(), cb.ToObject(), cmpTok, curLine, curCol)));
                                break;
                            }
                            Push(new VmValue(Compare(op, ca, cb)));
                            break;
                        }

                    case Opcode.JMP:
                        {
                            var offset = BytecodeReader.I16(code, ref ip);
                            ip += offset;
                            // A BACKWARD JUMP IS A LOOP BACK-EDGE, and it is where a compiled body asks whether its run
                            // was cancelled: a `while (true) { }` compiles to this and never touches a statement
                            // boundary, so the tree walker's poll cannot see it (measured: a cancelled compiled loop ran
                            // until the process died). Asking here costs one static field read per loop turn, not per
                            // instruction, which is why `bench_ref_vm_tree` is the check for the cost.
                            if (offset < 0 && Interpreter.RunCancellationRequested)
                                throw new ScriptException(ErrorCode.Cancelled, "Stopped: the run was cancelled", curLine, curCol);
                            break;
                        }

                    case Opcode.JMP_IF_FALSE:
                        {
                            var offset = BytecodeReader.I16(code, ref ip);
                            var i = --sp;
                            var truthy = sK[i] == 1 ? sL[i] != 0
                                : sK[i] == 2 ? Math.Abs(sD[i]) > Interpreter.Epsilon
                                : IsTruthyV(new VmValue(sK[i], sT[i], sL[i], sD[i], sO[i]));
                            if (!truthy)
                                ip += offset;
                            break;
                        }

                    case Opcode.CALL:
                        {
                            var argCount = BytecodeReader.U8(code, ref ip);
                            var callArgs = RentArgs(argCount);
                            try
                            {
                                // Per-call ref/out position map. NOTE: must be heap-allocated, a
                                // `stackalloc int[argCount]` here leaks ~16B of C# stack PER CALL in
                                // the JIT (the block sits inside a loop), so a deep tail-recursion
                                // loop or the flat-call stack died with "Stack overflow." after
                                // ~90000 levels on the 1.5MB Debug apphost stack (measured via
                                // GetCurrentThreadStackLimits). Heap array is GC-temporary and freed.
                                Span<int> refIdx = new int[argCount];
                                for (var i = argCount - 1; i >= 0; i--)
                                {
                                    var av = Pop();
                                    refIdx[i] = av.K == 4 ? (int)av.L : -1;
                                    callArgs[i] = av.K == 4 ? av.O : av.ToObject();
                                }

                                var callee = Pop();
                                if (callee is { K: 3, O: string builtinName })
                                {
                                    // A NAMED-arg call's marker sits just above the arguments once they and the
                                    // callee are popped ([marker, calleeName, args…]), so it is read FIRST: the
                                    // overload picker weighs the call's NAMES against each candidate (C#'s rule),
                                    // and taking the marker after the pick made a renamed call choose by POSITION
                                    // and then coerce the winning member against arguments meant for another one
                                    // (measured: `P(b: 1, a: "s")` against `P(int, string)`/`P(string, int)`
                                    // refused with "Cannot convert string 's' to int"). A non-user callee cannot
                                    // be reordered at all, and the bail below reports exactly what it always did.
                                    NamedArgsMarker? namedMarker = null;
                                    if (sp > 0 && sO[sp - 1] is NamedArgsMarker pendingNames)
                                    {
                                        sp--;
                                        namedMarker = pendingNames;
                                    }
                                    // The call site's REF/OUT FLAGS, when it wrote any (ArgFlagsMarker, pushed at the
                                    // bottom of the layout like the named one; the two never meet on one call, because a
                                    // named call with a ref argument still declines the body). The overload picker needs
                                    // them: a ref position arrives here as a VALUE plus a slot reference, and without the
                                    // flags it cannot tell `F(ref n)` from `F(n)`, so an overload set holding a `ref`
                                    // member could not be resolved from a compiled body.
                                    ArgFlagsMarker? callFlags = null;
                                    if (sp > 0 && sO[sp - 1] is ArgFlagsMarker pendingFlags)
                                    {
                                        sp--;
                                        callFlags = pendingFlags;
                                    }
                                    // A call whose result is used as a REFERENCE (`F(..) = 7;`): its marker sits at the
                                    // bottom of the layout, so it is the stack top once the callee and its arguments
                                    // are popped. Such a call is never inlined below (the flat swap dereferences) and
                                    // the callee is invoked asking for the cell.
                                    var callWantsRef = false;
                                    if (sp > 0 && sO[sp - 1] is RefResultMarker)
                                    {
                                        sp--;
                                        callWantsRef = true;
                                    }
                                    bool[]? callRefFlags = null;
                                    if (callFlags != null)
                                    {
                                        callRefFlags = new bool[argCount];
                                        for (var i = 0; i < argCount; i++)
                                            callRefFlags[i] =
                                                (i < callFlags.Refs.Length && callFlags.Refs[i])
                                                || (i < callFlags.Outs.Length && callFlags.Outs[i]);
                                    }
                                    // User functions first, ref/out args must be wrapped BEFORE invocation
                                    // (ResolveBuiltin/CallBuiltinByName would dispatch them unwrapped).
                                    var sf = interp.ResolveGlobalFunction(builtinName);
                                    // A free-function NAME with several signatures is an OVERLOAD SET, not one
                                    // ScriptFunction, and the arguments are already in hand here: pick with the
                                    // ONE picker (the same one the tree's InvokeCallee asks), so an overloaded
                                    // name resolves identically on both engines instead of bailing.
                                    sf ??= interp.ResolveGlobalOverloadSet(builtinName) is { } overloadSet
                                        ? interp.PickOverload(overloadSet, callArgs, builtinName, curLine, curCol,
                                            namedMarker?.Names, callRefFlags)
                                        : null;
                                    if (sf != null)
                                    {
                                        var d = sf.Declaration;
                                        // Reorder into the callee's declared param order, same rules/errors as
                                        // the tree's ReorderNamedArgs (positional fill, duplicate/unknown-name
                                        // errors), and through the SAME mapping the picker just used.
                                        if (namedMarker != null)
                                        {
                                            Interpreter.NamedTrace("CALL",
                                                $"callee={d.Name} names={Interpreter.NamedTraceNames(namedMarker.Names)}");
                                            callArgs = interp.ReorderNamedArgs(callArgs, namedMarker.Names, d.Params, curLine, curCol);
                                        }
                                        // Same arity rule as the tree, through the same helper: the VM used to accept
                                        // extra arguments here silently (writing them into the callee's capture slots
                                        // once it had captures), while the tree refused them.
                                        interp.CheckScriptCallArity(d, callArgs.Length, curLine, curCol);
                                        var hasRef = d.IsRefParams.Contains(true) ||
                                                     HasOutParam(d);
                                        // Out positions are wired as TRACKING cells below (assignment recorded,
                                        // value type-checked on set) and verified on RETURN, so they are
                                        // flat-eligible too, the same semantics as the tree's InitOutTracking.
                                        var declaresOut = d.IsOutParams is { Count: > 0 } dOutFlags && dOutFlags.Contains(true);
                                        var allRefWired = false;
                                        bool[]? outTrack = null;
                                        if (hasRef)
                                        {
                                            bool IsRefPos(int i) =>
                                                (i < d.IsRefParams.Count && d.IsRefParams[i]) ||
                                                (d.IsOutParams != null && i < d.IsOutParams.Count && d.IsOutParams[i]);

                                            // Every ref/out position must be a local-slot arg OR an already-built
                                            // CELL (a GLOBAL ref: the engine's helper answers one, see
                                            // CompileArgument); a non-lvalue (e.g. a literal) falls back to the
                                            // tree, which reports the error.
                                            var canWrap = true;
                                            for (var i = 0; i < argCount; i++)
                                                if (IsRefPos(i) && refIdx[i] < 0 && callArgs[i] is not RefCell)
                                                {
                                                    canWrap = false;
                                                    break;
                                                }

                                            if (!canWrap)
                                            {
                                                // The call passed a VALUE where the declaration demands `ref`/`out` (a
                                                // variable read by value: the compiler declines every other shape, so a
                                                // literal or a field never reaches here). Refuse through the ONE rule,
                                                // exactly as the tree's own bind does, rather than aborting to a tree a
                                                // compiled body does not have. Measured before: a bundle reported an
                                                // engine-gap message here while the source bound a silent COPY.
                                                for (var i = 0; i < argCount; i++)
                                                    if (IsRefPos(i) && refIdx[i] < 0 && callArgs[i] is not RefCell)
                                                        Interpreter.RefuseRefCellOnByValueParam(
                                                            d.Params[i].Name, callArgs[i],
                                                            i < d.IsRefParams.Count && d.IsRefParams[i],
                                                            d.IsOutParams != null && i < d.IsOutParams.Count && d.IsOutParams[i],
                                                            curLine, curCol);
                                                return Bail();   // unreachable: the rule refuses, satisfies flow analysis
                                            }

                                            for (var i = 0; i < argCount; i++)
                                            {
                                                if (!IsRefPos(i)) continue;
                                                RefCell cell;
                                                if (refIdx[i] < 0)
                                                {
                                                    // The argument arrived AS a cell: a `ref`/`out` target that is a
                                                    // GLOBAL, built by the engine's helper (see CompileArgument).
                                                    // There is no local slot to wrap, and the cell writes the global
                                                    // itself, so it goes straight through.
                                                    if (callArgs[i] is not RefCell passed) return Bail();
                                                    cell = passed;
                                                }
                                                else
                                                {
                                                    var idx = refIdx[i];
                                                    object? held = locals[idx].ToObject();
                                                    var existing = held as RefCell;
                                                    if (existing == null)
                                                    {
                                                        existing = new RefCell(() => held, v => held = v);
                                                        locals[idx] =
                                                            VmValue.From(existing); // shared, callee writes through to our slot
                                                    }
                                                    cell = existing;
                                                }

                                                if (d.IsOutParams != null && i < d.IsOutParams.Count && d.IsOutParams[i])
                                                {
                                                    // Out position: wrap the shared cell in a TRACKING cell, the
                                                    // setter records the assignment and validates the value's type,
                                                    // then writes through. Same messages/behaviour as InitOutTracking.
                                                    outTrack ??= new bool[d.Params.Count];
                                                    var outFlag = i;
                                                    var outType = d.Params[i].Type;
                                                    var outName = d.Params[i].Name;
                                                    var innerCell = cell;
                                                    cell = new RefCell(innerCell.Getter, v =>
                                                    {
                                                        var coerced = interp.ValidateOutAssignmentFromVm(v, outType, outName, curLine, curCol);
                                                        outTrack[outFlag] = true;
                                                        innerCell.Set(coerced);
                                                    });
                                                    // The SLOT keeps the plain cell: storing the TRACKING wrapper
                                                    // here made every call wrap the previous call's wrapper, so a
                                                    // loop of 50,000 out-param calls built a 50,000-deep `Set` chain
                                                    // and the process died with 0xC0000005 (measured,
                                                    // crash_out_ref_stress section 10). The callee still receives the
                                                    // tracking cell, and its writes reach the slot through innerCell.
                                                    callArgs[i] = cell;
                                                    continue;
                                                }
                                                callArgs[i] = cell;
                                            }

                                            // Every ref position now holds a cell over OUR slot, and the flat
                                            // swap below binds those cells into the callee's locals, where
                                            // LOAD_VAR derefs and STORE_VAR writes through. So a ref callee no
                                            // longer has to spend a C# frame per level: without this, deep ref
                                            // recursion (rec(ref x, 5000)) blew the 1.5MB apphost stack and died
                                            // with an UNCATCHABLE StackOverflow before the depth guard could fire.
                                            allRefWired = true;
                                        }

                                        // Tail call: the CALL is immediately followed by RETURN and the callee is a
                                        // capture-free top-level function with no defaults/params, swap frames and
                                        // loop instead of re-entering InvokeScriptFunction (no C# stack growth).
                                        var d2 = sf.Declaration;
                                        var isTailCall = BytecodeReader.ByteAt(code, ip) == (byte)Opcode.RETURN;
                                        // A call made inside THIS frame's own try/finally must NOT be inlined (tail
                                        // or flat): a bail returns VmFallback out of the WHOLE Vm.Execute, so a
                                        // script handler covering the call never sees it (a bail is not an
                                        // exception), while the non-flat boundary (InvokeScriptFunction ->
                                        // TryExecuteVmBody) turns a committed bail into a CATCHABLE refusal.
                                        // Measured: `try { WritesGlobalFirst(); } catch (var e) { ... }` was
                                        // refused at the STATEMENT level instead of being caught
                                        // (vm_bail_commit_test, probe-toplevel-vm section 16). A try region pays
                                        // one C# call per call it makes; everywhere else nothing changes.
                                        var handlerHere = handlers != null && handlers.Count > handlerBase;
                                        // `VmPathEnabled(d2)` is the ONE question every OTHER flat path already asks
                                        // (the four method arms in this file, `TryExecuteVmBody` and the coroutine
                                        // drives): may this body run on the VM at all, i.e. is the VM on, or is it a
                                        // SHELL that has no AST to walk? Function-call INLINING was the one place
                                        // that skipped it, so a script that turned the VM off inside a compiled body
                                        // kept calling flat from it: measured, `int outer(int n) {
                                        // VmSetEnabled(false); var r = inner(n); ... }` answered `VM: 3 | Tree: 0`
                                        // where the flag's own promise is the tree (`VM: 1 | Tree: 3` now). A shell
                                        // still inlines, because a loaded body cannot be tree-walked at all.
                                        if (!callWantsRef && isTailCall && !handlerHere && !hasRef && sf.VmCaptures == null &&
                                            d2.DefaultValues == null && !(d2.IsParams.Count > 0 && d2.IsParams[^1]) &&
                                            !d2.HasYield && !d2.IsAsync && d2.Compiled != null &&
                                            Interpreter.VmPathEnabled(d2))
                                        {
                                            // Tail-loop runaway guard: a tail swap keeps the C# stack flat (the
                                            // callee replaces the caller in place), so a genuinely infinite tail
                                            // recursion, `int loopx(int n) { return loopx(n + 1); }`, would
                                            // otherwise loop forever. Cap the swaps per invocation at a level far
                                            // beyond any legit tail depth (the suite exercises tailRec(100000));
                                            // the catchable ScriptException mirrors the flat-call guard.
                                            if (++tailSwaps > TailMaxFrames)
                                                throw new ScriptException(DepthCapMessage(TailMaxFrames, "Tail-call"));
                                            handlers?.Clear();
                                            forEachStates?.Clear();
                                            handlerBase = 0;
                                            // The callee's operands start at THIS frame's base, not at 0. A
                                            // tail call is a flat frame swapping itself for the callee, so the
                                            // region BELOW spBase belongs to the frame's own caller and must not be
                                            // touched: resetting to 0 let the callee's first push clobber it (and
                                            // left `sp` below `spBase`, so the callee's RETURN read `sp > spBase`
                                            // false and returned NULL, measured through a callback: `print("x" + f())`
                                            // where f's body tail-calls, the concat lost an operand and the caller's
                                            // own CALL then underflowed with IndexOutOfRangeException). For a
                                            // top-level frame spBase is 0, which is why an ordinary tail recursion
                                            // never showed it.
                                            sp = spBase;
                                            bc = d2.Compiled;
                                            interp.RegisterNestedDeclsForRun(bc);
                                            code = bc.Code;
                                            consts = bc.ConstsVm;
                                            ReturnLocals(locals);
                                            locals = RentLocals();
                                            for (var ai = 0; ai < callArgs.Length && ai < locals.Length; ai++)
                                                locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(d2, callArgs[ai], ai, curLine, curCol));
                                            // Tail callee gets a FRESH slot table, slot indices are per-Bytecode
                                            // (ITS GlobalSlotNames), and the abandoned caller's cached globals must
                                            // not leak across the swap. Return the caller's table to the pool first.
                                            ReturnSlots(globalSlots, globalSlotState);
                                            (globalSlots, globalSlotState) = RentSlots();
                                            ip = 0;
                                            curLine = 0;
                                            curCol = 0;
                                            curNs = sf.NamespaceContext;   // F4: tail callee's namespace context
                                            continue;
                                        }

                                        // Defaults / params-array: the flat swap binds argument VALUES into the
                                        // callee's locals, so it needs the same normalization the VM-direct path
                                        // applies (defaults filled, params array assembled, by-value coercion).
                                        // Only for callees that actually declare them, an ordinary call pays
                                        // nothing and keeps today's path exactly.
                                        object?[]? normalizedArgs = null;
                                        if (d2.DefaultValues != null || (d2.IsParams.Count > 0 && d2.IsParams[^1]))
                                            normalizedArgs = interp.NormalizeArgsForFlatCall(d2, callArgs, curLine, curCol);

                                        // ── Flat call: push the caller's state and swap to the callee in place.
                                        // Same VM-safe eligibility as the tail path, but the callee RETURNS to us,
                                        // the SavedFrame restores every per-call local (RETURN pops it). This is what
                                        // lets deep NON-tail recursion (sumTo(1990), fib, ...) run without growing the
                                        // C# stack: the heap `frames` list holds the pending callers.
                                        if (!callWantsRef && (!hasRef || allRefWired) && !handlerHere && sf.VmCaptures == null &&
                                            !d2.HasYield && !d2.IsAsync && d2.Compiled != null &&
                                            Interpreter.VmPathEnabled(d2))
                                        {
                                            if (frames.Count >= VmMaxFrames)
                                                throw new ScriptException(DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                            interp.VmInvocations++;
                                            // RENTED rather than `new`: a flat call allocates one frame per call and this was the
                                            // engine's single hottest allocation (measured 288 B per 0-arg call, of which this
                                            // object is about 184 B). The same pool already serves the generator boundary frames,
                                            // and RentSavedFrame clears the generator identity a plain frame must not inherit.
                                            var callFrame = RentSavedFrame();
                                            callFrame.Name = d2.Name;
                                            callFrame.Decl = d2;
                                            callFrame.OutTrack = outTrack;
                                            callFrame.Bc = bc;
                                            callFrame.Code = code;
                                            callFrame.Ip = ip;
                                            callFrame.Consts = consts;
                                            callFrame.Locals = locals;
                                            callFrame.CurLine = curLine;
                                            callFrame.CurCol = curCol;
                                            callFrame.SpBase = spBase;
                                            callFrame.Handlers = handlers;
                                            callFrame.HandlerBase = handlerBase;
                                            callFrame.PendingFinally = pendingFinally;
                                            callFrame.PendingFinallyPayload = pendingFinallyPayload;
                                            callFrame.Seq = seq;
                                            // Save the CALLER's slot table (restored + cleared on return)
                                            callFrame.GlobalSlots = globalSlots;
                                            callFrame.GlobalSlotState = globalSlotState;
                                            callFrame.NsContext = curNs;
                                            callFrame.ForEachStates = forEachStates;
                                            callFrame.GenCo = curGenCo;

                                            frames.Add(callFrame);
                                            currentFunc = d2.Name;
                                            bc = d2.Compiled;
                                            interp.RegisterNestedDeclsForRun(bc);
                                            code = bc.Code;
                                            consts = bc.ConstsVm;
                                            curNs = sf.NamespaceContext;
                                            // No ReturnLocals here, the frame above still holds the CALLER's
                                            // array (Locals = locals). The callee rents a fresh one, pooled at pop.
                                            locals = RentLocals();
                                            var flatSrc = normalizedArgs ?? callArgs;
                                            for (var ai = 0; ai < flatSrc.Length && ai < locals.Length; ai++)
                                                locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(d2, flatSrc[ai], ai, curLine, curCol));
                                            // Tuple parameters: the flat call binds VALUES straight into the callee's
                                            // locals, so the declared element names never reach the tuple and `p.v`
                                            // fails ("Property 'v' not found on tuple"). The VM-direct path does this
                                            // in TryExecuteVmBody for the same reason; measured on t4_struct, whose
                                            // `(int b, int a) Test((int v, int s) p) => (p.v * 5, p.s + 5)` is called
                                            // from a compiled body.
                                            if (d2.TupleParamNames is { Count: > 0 })
                                                for (var tpi = 0; tpi < d2.Params.Count && tpi < flatSrc.Length; tpi++)
                                                    interp.BindTupleParam(d2, tpi, flatSrc[tpi], curLine, curCol);
                                            ip = 0;
                                            curLine = 0;
                                            curCol = 0;
                                            spBase = sp;
                                            handlers = null; // lazily allocated on the callee's first TRY
                                            handlerBase = 0;
                                            pendingFinally = false;
                                            pendingFinallyPayload = null;
                                            seq = 0;
                                            forEachStates = null; // lazily allocated on the callee's first FOREACH
                                                                  // Post-call invalidation: clear the caller's cached slot state (saved in
                                                                  // the frame above), the callee may mutate any global, so the caller
                                                                  // re-resolves on return. The callee itself gets a FRESH table sized to
                                                                  // ITS GlobalSlotNames (slot indices are per-Bytecode, not shared).
                                            Array.Clear(globalSlotState);
                                            (globalSlots, globalSlotState) = RentSlots();
                                            continue;
                                        }

                                        // Post-call invalidation: the nested Vm.Execute (or tree fallback) may
                                        // have mutated globals, re-resolve on the next FAST load.
                                        Array.Clear(globalSlotState);
                                        // A ref-RETURNING body answers a REFERENCE (a cell), and the VM has no lvalue use
                                        // for one: the two shapes that need it (`ref int r = ref F(x);`, `F(x) = 7;`) are
                                        // DECLINED by the compiler, so the tree owns them and the VM dereferences here,
                                        // exactly as a read of a cell-holding local does.
                                        var callResult = interp.InvokeScriptFunction(sf, callArgs, curLine, curCol, wantRefResult: callWantsRef);
                                        // A want-reference call keeps the CELL (that is the point of the marker); every
                                        // other call dereferences, because a compiled body consumes values.
                                        Push(VmValue.From(callWantsRef
                                            ? callResult
                                            : callResult is RefCell refRet ? refRet.Get() : callResult));
                                        break;
                                    }

                                    // Not a user function, try builtins/natives. A named-args marker with a
                                    // non-user callee can't be reordered (it was taken above, before the pick),
                                    // consume nothing more and fall back to the tree, which reports the error
                                    // (or binds builtin named args).
                                    if (namedMarker != null)
                                    {
                                        Interpreter.NamedTrace("CALL",
                                            $"non-user callee={builtinName} names={Interpreter.NamedTraceNames(namedMarker.Names)} -> VmFallback");
                                        // Abort to the tree, which reports the error (or binds builtin named args).
                                        return Bail();
                                    }
                                    // A builtin's CLR failure is CONVERTED here (Interpreter.InvokeBuiltinFromVm),
                                    // the way the tree's call path converts one: a raw exception would escape the
                                    // script's own `try`, because this walk catches ScriptException only.
                                    // A BUILTIN CAN RUN SCRIPT CODE, so whatever it wrote must not be read back through
                                    // this invocation's slot cache. THE STAMP IS THE GENERAL RULE (the same one the CLR
                                    // cached invoke above uses): `Interpreter.GlobalWriteStamp` moves on every
                                    // announced global write from ANY thread and any interpreter, so `RunScript`,
                                    // `Application.DoEvents`, a `Task.Run` body, an `Events` publish, `Parallel.*` and a
                                    // comparer are covered, and a builtin added tomorrow is covered without an entry.
                                    // Measured: `await_window_liveness_test` posts a `BM_CLICK`, then awaits through
                                    // `RunScript`, and the click handler (a script lambda the PUMP dispatches) writes a
                                    // top-level counter. The handler wrote 1 through `SetGlobalValue` and the compiled
                                    // body then read 0 from its own slot, so two checks failed from a bundle and passed
                                    // from the file.
                                    //
                                    // THE NAME PREDICATE IS NOT REDUNDANT, and dropping it cost a corpus suite: a TREE
                                    // write to a name that lives in the frame's own scope returns from
                                    // `ScopeStack.Set`'s first loop, which does NOT announce itself, so the
                                    // carried-statement helper is invisible to the stamp (measured: `vm_nosource_test`
                                    // read the pre-loop `acc` after a carried `foreach`, `top=[]` against a function's
                                    // `[012]`). See `BuiltinTouchesScriptState` for that entry's own reason.
                                    var builtinStamp = Interpreter.GlobalWriteStamp;
                                    var result = interp.InvokeBuiltinFromVm(builtinName, callArgs, curLine, curCol);
                                    if (result is not VmFallback)
                                    {
                                        // TWO FACTS, ONE CLEAR: the stamp covers every callback (`RunScript`, `DoEvents`,
                                        // a Task body, a subscriber), and the NAME covers the one helper whose write the
                                        // stamp cannot see (see `BuiltinTouchesScriptState`, which carries the repro).
                                        if (Interpreter.BuiltinTouchesScriptState(builtinName)
                                            || Interpreter.GlobalWriteStamp != builtinStamp)
                                            Array.Clear(globalSlotState);
                                        Push(VmValue.From(result));
                                        break;
                                    }

                                    // F4: bare sibling call, walk the executing function's
                                    // namespace chain (leaf → root) for the callee. Resolves
                                    // same-leaf, ancestor-namespace, and flat (root) siblings.
                                    var targetNs = curNs;
                                    while (targetNs != null && !targetNs.HasMethod(builtinName))
                                        targetNs = targetNs.Parent;
                                    if (targetNs != null)
                                    {
                                        // A ref/out callee must NOT be dispatched here, its args arrived
                                        // unwrapped (LOAD_REF) and the namespace wrapper (InvokeScriptFunction)
                                        // would throw 'Expected a reference'. Skip it so the call takes the
                                        // VmFallback path below (the tree binds RefCells, pre-F4 behavior for
                                        // tree functions; the shell error for bytecode-only ones).
                                        var nsHasRef = targetNs.GetProperty(builtinName) is ScriptFunction nsFn
                                            && (nsFn.Declaration.IsRefParams.Contains(true)
                                                || (nsFn.Declaration.IsOutParams != null
                                                    && nsFn.Declaration.IsOutParams.Contains(true)));
                                        if (!nsHasRef)
                                        {
                                            // Post-call invalidation: the namespace callee may have mutated
                                            // globals, re-resolve on the next FAST load.
                                            Array.Clear(globalSlotState);
                                            Push(VmValue.From(targetNs.InvokeMethod(builtinName, callArgs, curLine, curCol)));
                                            break;
                                        }
                                    }
                                }
                                else if (callee.K == 1)
                                {
                                    // Not a valid callee, fall back
                                }

                                // A TYPE CALLED AS A FUNCTION (`double(42)`): the engine already owns that message, and
                                // the tree's own call arm produces it, so a bundle raises the SAME text from the same
                                // resolver the global-load miss uses instead of refusing the whole entry. The callee
                                // arrives as a NAME const (K == 1) or as a resolved String VALUE (K == 3, measured), so
                                // the check reads the value rather than the kind.
                                if (callee.O is string typeCallee && interp.ResolveTypeNameAsValue(typeCallee) is Type calledTypeValue)
                                    interp.RuntimeErrorFromVm($"Type '{calledTypeValue.Name}' is not a function.", curLine, curCol);

                                // Fallback: abort to the tree, VmFallback returned (not pushed) so
                                // TryExecuteVmBody re-runs this function tree-walked, which raises the
                                // proper "Undefined variable" / unresolved-callee error.
                                return Bail();
                            }
                            finally
                            {
                                ReturnArgs(callArgs);
                            }
                        }
                    case Opcode.RETURN:
                        {

                            var retVal = sp > spBase ? Pop().ToObject() : null;

                            // A generator whose foreach loop ended early (break/return/throw) left its
                            // SuspendedGenerator frame in `frames`, the returning function is NOT that
                            // generator, so its frame is not a caller. Drop them first.
                            DropAbandonedGeneratorFrames();
                            if (frames.Count == 0) return retVal;
                            var f = frames[^1];
                            // The DECLARED return type belongs to the body that is leaving, and the VM used to
                            // hand the value back untouched: a declared `int` function could return a string, a
                            // double or nothing at all, silently, and `int` <- 1.5 answered 1.5 where the tree
                            // converts to 2. ONE rule, the interpreter's own coercion, asked of the frame whose
                            // declaration it is. `curLine`/`curCol` are still the RETURN's, which is the position
                            // the tree reports for the same refusal.
                            retVal = interp.CoerceFlatCallResult(f.Decl, retVal, curLine, curCol);
                            // A REF-RETURNING body answers the REFERENCE, and this is a FLAT call: the caller is a
                            // compiled body, which can only consume a value, so the cell is dereferenced here. (The two
                            // shapes that want the reference itself are declined and run on the tree.) The OUTERMOST
                            // return is left alone: its caller is C#, and TryExecuteVmBody decides with the flag the call
                            // site set, because a tree consumer may be asking for the reference.
                            if (f.Decl is { IsRefReturn: true } && retVal is RefCell flatRefCell) retVal = flatRefCell.Get();
                            // Out-param verification for the flat call this frame belongs to, mirroring the
                            // VM-direct check (same message and call-site location). Skipped when the callee
                            // leaves by unwinding, exactly like the tree and VM-direct paths.
                            if (f.OutTrack != null && f.Decl != null && f.Decl.IsOutParams != null)
                            {
                                for (var oi = 0; oi < f.OutTrack.Length && oi < f.Decl.Params.Count; oi++)
                                    if (f.Decl.IsOutParams[oi] && !f.OutTrack[oi])
                                        interp.RuntimeErrorFromVm(Interpreter.OutNotAssignedMessage(f.Decl.Params[oi].Name), f.CurLine, f.CurCol);
                            }
                            if (f.GeneratorHandle != null)
                            {
                                // ── The returning function is a VM generator driven by a FOREACH_NEXT:
                                // it completed, mark the generator Dead and jump to the driver's
                                // FOREACH_END (its return value is NOT a foreach element).
                                // The DRIVER's operand height, recorded when this drive frame was pushed. It used to be
                                // read as the generator's spBase, which is the same number only when the body was
                                // ENTERED in place (`spBase = sp` at the push); a drive RESUMED from a VmState
                                // captured in another invocation has no relation to it, and the driver's own
                                // `return` then found `sp` below `spBase` and refused its declared type.
                                var driveSp = f.Sp;
                                // A `return` abandoning the generator's own foreach (the compiler
                                // emits RETURN directly, skipping FOREACH_END) leaks the enumerator,
                                // dispose everything still tracked for this frame, like the
                                // VmFlowException return path does.
                                DisposeAbandonedForEach(forEachStates, locals, -1);
                                if (f.GeneratorHandle is Coroutine fCo)
                                {
                                    // The driven generator returned naturally → Completed.
                                    fCo.Status = CoroutineStatus.Completed;
                                    fCo.VmState = null;
                                }
                                frames.RemoveAt(frames.Count - 1);
                                currentFunc = frames.Count > 0 ? frames[^1].Name : "";
                                bc = f.Bc;
                                code = f.Code;
                                ip = f.ForEachEndIp;
                                consts = f.Consts;
                                ReturnLocals(locals); // generator's array, dead now, back to the pool
                                ReturnSlots(globalSlots, globalSlotState);
                                locals = f.Locals;
                                curLine = f.CurLine;
                                curCol = f.CurCol;
                                spBase = f.SpBase;
                                handlers = f.Handlers;
                                handlerBase = f.HandlerBase;
                                pendingFinally = f.PendingFinally;
                                pendingFinallyPayload = f.PendingFinallyPayload;
                                seq = f.Seq;
                                forEachStates = f.ForEachStates;
                                globalSlots = f.GlobalSlots;
                                globalSlotState = f.GlobalSlotState;
                                curNs = f.NsContext;
                                curGenCo = f.GenCo;
                                ReturnSavedFrame(f);   // the completed drive's own frame, recycled
                                // The generator may have mutated globals before returning, re-resolve.
                                Array.Clear(globalSlotState);
                                // Restore the caller's operand-stack height, the generator's dead
                                // region (with any phantom operands) is discarded, not left under the
                                // driver's FOREACH_END resume.
                                sp = driveSp;
                                continue;
                            }
                            // Attach declared tuple element names to the callee's return value before
                            // handing it back to the caller (flat in-place calls bypass InvokeScriptFunction).
                            if (retVal is ScriptTuple retTup && f.Decl is { TupleElementNames.Count: > 0 } retDecl)
                                retTup.Names = retDecl.TupleElementNames;
                            // Pop the caller's frame and resume its loop, the return value stays on the
                            // shared operand stack exactly where the flat CALL left it.
                            frames.RemoveAt(frames.Count - 1);
                            currentFunc = frames.Count > 0 ? frames[^1].Name : "";
                            bc = f.Bc;
                            code = f.Code;
                            ip = f.Ip;
                            consts = f.Consts;
                            ReturnLocals(locals); // callee's array, back to the pool
                            ReturnSlots(globalSlots, globalSlotState); // callee's table, back to the pool
                            locals = f.Locals;
                            curLine = f.CurLine;
                            curCol = f.CurCol;
                            spBase = f.SpBase;
                            handlers = f.Handlers;
                            handlerBase = f.HandlerBase;
                            pendingFinally = f.PendingFinally;
                            pendingFinallyPayload = f.PendingFinallyPayload;
                            seq = f.Seq;
                            forEachStates = f.ForEachStates;
                            globalSlots = f.GlobalSlots;
                            globalSlotState = f.GlobalSlotState;
                            curNs = f.NsContext;   // F4: restore the caller's namespace context
                            curGenCo = f.GenCo;    // restore the caller's generator context
                            if (f.SavesClassContext)
                            {
                                interp.CurrentThis = f.ThisInstance;
                                interp.CurrentClass = f.ThisClass;
                                interp.CurrentReceiverName = f.ThisReceiverName;   // an extension block's alias
                                interp.CurrentContract = f.ThisContract;           // an interface default's contract
                            }
                            // Post-call invalidation: the callee may have mutated any global, re-resolve.
                            Array.Clear(globalSlotState);
                            ReturnSavedFrame(f);   // this flat call's own frame, recycled (see RentSavedFrame)
                            Push(VmValue.From(retVal));
                            continue;
                        }

                    case Opcode.YIELD:
                    case Opcode.AWAIT:
                        {
                            var isAwait = op == Opcode.AWAIT;
                            var yielded = Pop().ToObject();
                            // An await leaves its awaitable on the operand stack and the pump takes an
                            // AwaitRequest, so a bare Task is wrapped here. The suspension below is then the one a
                            // yield takes, and only the captured flag differs: the resume of an await PUSHES the
                            // awaited value into the slot the enclosing expression expects.
                            if (isAwait && yielded is not CoroutineYieldRequest)
                            {
                                // Not awaitable at all (`await 42`): the TREE refuses that with its own words,
                                // so hand the statement back rather than invent a second message here.
                                if (yielded is not ScriptLangCore.Task awaitTask)
                                    return VmFallback.Instance;
                                yielded = new AwaitRequest(awaitTask);
                            }
                            // ── Boundary vs nested: a nested generator always has its driver frame in
                            // `frames` (a non-suspended frame whose GeneratorHandle is the current
                            // function's coroutine, pushed by the FOREACH_NEXT that entered/resumed it).
                            // The boundary generator (driven by Generator.MoveNext) has none, curGenCo
                            // is null and frames may still hold retained frames of nested generators.
                            var driverIdx = -1;
                            if (curGenCo != null)
                            {
                                // Fast path: the caller's drive frame is frames[^1] in the common
                                // flat case (FOREACH_NEXT just pushed it); only scan when a nested
                                // drive put other frames above it.
                                var n = frames.Count;
                                if (n > 0 && frames[n - 1].GeneratorHandle == curGenCo && !frames[n - 1].SuspendedGenerator)
                                    driverIdx = n - 1;
                                else
                                    for (var fi = n - 1; fi >= 0; fi--)
                                        if (frames[fi].GeneratorHandle == curGenCo && !frames[fi].SuspendedGenerator)
                                        {
                                            driverIdx = fi;
                                            break;
                                        }
                            }
                            if (driverIdx < 0)
                            {
                                // ── Outermost suspension, a generator driven by Generator.MoveNext.
                                // Capture the full run state into a VmSuspendState (surfaced through the
                                // `suspend` out-param) and return the yielded value as this Vm.Execute
                                // invocation's result. MoveNext stores the state and resumes it with
                                // ExecuteResumed on the next call. The yielded value is consumed here, so
                                // the continuation (ip = after YIELD) resumes with a clean stack, and the
                                // tree's generator path never consumes a resume value, so nothing is
                                // pushed on resume. The captured Frames may include retained frames of
                                // nested suspended generators, they are restored untouched on resume.
                                suspend = new VmSuspendState
                                {
                                    Bc = bc,
                                    Code = code,
                                    Stack = MaterializeStack(sK, sT, sL, sD, sO, sp),
                                    Sp = sp,
                                    Locals = locals,
                                    Frames = frames,
                                    Handlers = handlers,
                                    Ip = ip,
                                    HandlerBase = handlerBase,
                                    SpBase = spBase,
                                    PendingFinally = pendingFinally,
                                    PendingFinallyPayload = pendingFinallyPayload,
                                    Seq = seq,
                                    ForEachStates = forEachStates,
                                    CurLine = curLine,
                                    CurCol = curCol,
                                    NsContext = curNs,
                                    CurrentFunc = currentFunc,
                                    PushResumeValue = isAwait,
                                };
                                return yielded;
                            }

                            // An await is never inside a driven generator, so a nested frame here means a shape this
                            // opcode does not model: hand the statement to the tree rather than resume it without
                            // the value the expression is waiting for.
                            if (isAwait) return VmFallback.Instance;

                            // ── Nested generator frame, a generator entered/resumed by a FOREACH_NEXT
                            // drive. Its driver frame (the foreach owner's state) may sit anywhere in
                            // `frames` (frames pushed by the owner's own nested drives sit above it).
                            // Extract it, store THIS function's live state as a retained frame (marked
                            // SuspendedGenerator, its locals array stays OUT of the pool), then swap
                            // back to the caller exactly like RETURN minus the pop.
                            var saved = frames[driverIdx];
                            // The DRIVER's operand height, recorded when its drive frame was pushed (see the
                            // FOREACH_NEXT drive). It is the number the swap-back must restore, and it equals the
                            // generator's spBase ONLY on a drive that entered the body in place; a resumed VmState
                            // carries a base from another invocation (measured `driverBase=2 snapBase=0`).
                            var driverSp = saved.Sp;
                            if (curGenCo is Coroutine curCoStatus) curCoStatus.Status = CoroutineStatus.Suspended;
                            frames.RemoveAt(driverIdx);
                            var yieldFrame = RentSavedFrame();
                            yieldFrame.Name = currentFunc;        // the suspended generator's name
                            yieldFrame.Decl = saved.Decl;         // tuple names attach on its eventual return
                            yieldFrame.Bc = bc;
                            yieldFrame.Code = code;
                            yieldFrame.Ip = ip;
                            yieldFrame.Consts = consts;
                            yieldFrame.Locals = locals;
                            yieldFrame.CurLine = curLine;
                            yieldFrame.CurCol = curCol;
                            yieldFrame.SpBase = spBase;
                            // sp after the yielded value was popped, the retained shared-stack
                            // region [SpBase, Sp) is what a FOREACH_NEXT resume must reinstate.
                            yieldFrame.Sp = sp;
                            yieldFrame.Handlers = handlers;
                            yieldFrame.HandlerBase = handlerBase;
                            yieldFrame.PendingFinally = pendingFinally;
                            yieldFrame.PendingFinallyPayload = pendingFinallyPayload;
                            yieldFrame.Seq = seq;
                            yieldFrame.ForEachStates = forEachStates;
                            yieldFrame.GlobalSlots = globalSlots;
                            yieldFrame.GlobalSlotState = globalSlotState;
                            yieldFrame.NsContext = curNs;
                            yieldFrame.GeneratorHandle = curGenCo;   // the generator's own handle (FOREACH_NEXT locates it)
                            yieldFrame.CallerName = currentFunc;
                            yieldFrame.SuspendedGenerator = true;
                            frames.Add(yieldFrame);
                            bc = saved.Bc; code = saved.Code; ip = saved.Ip; consts = saved.Consts;
                            locals = saved.Locals;
                            curLine = saved.CurLine; curCol = saved.CurCol;
                            spBase = saved.SpBase;
                            handlers = saved.Handlers; handlerBase = saved.HandlerBase;
                            pendingFinally = saved.PendingFinally; pendingFinallyPayload = saved.PendingFinallyPayload;
                            seq = saved.Seq; forEachStates = saved.ForEachStates;
                            globalSlots = saved.GlobalSlots; globalSlotState = saved.GlobalSlotState;
                            curNs = saved.NsContext;   // F4: restore the caller's namespace context
                            curGenCo = saved.GenCo;    // restore the caller's generator context
                            currentFunc = saved.CallerName; // the caller (foreach owner) is now executing
                            ReturnSavedFrame(saved);       // recycles the caller's drive snapshot
                            Array.Clear(globalSlotState);
                            // Restore the caller's operand-stack height. Mid-expression yields (e.g.
                            // `f(1, yield 2)` / `arr[yield i]`) leave the generator's operand region
                            // non-empty at YIELD, so restoring the caller's frame must also reset sp to
                            // the DRIVER's recorded height, or the caller's later Push/STORE ops would
                            // land above phantom values.
                            sp = driverSp;
                            // Leave the yielded value on the shared stack for the caller, the
                            // FOREACH_NEXT's post-instruction STORE binds it as the loop element. The
                            // suspended generator's retained stack region sits ABOVE the caller's new sp;
                            // resumption must keep that region intact (sp = retained.Sp).
                            Push(VmValue.From(yielded));
                            continue;
                        }

                    case Opcode.DISPOSE:
                        TryDispose(Pop().ToObject());
                        break;

                    case Opcode.DESTRUCT:
                        {
                            var nVars = BytecodeReader.U8(code, ref ip);
                            var src = Pop().ToObject();
                            var vals = RentArgs(nVars);
                            try
                            {
                                var filled = 0;
                                switch (src)
                                {
                                    case Array a:
                                        filled = Math.Min(nVars, a.Length);
                                        for (var i = 0; i < filled; i++) vals[i] = a.GetValue(i);
                                        break;
                                    case ScriptTuple tup:
                                        filled = Math.Min(nVars, tup.Length);
                                        for (var i = 0; i < filled; i++) vals[i] = tup.Items[i];
                                        break;
                                    case IScriptList sl:
                                        filled = Math.Min(nVars, sl.Count);
                                        for (var i = 0; i < filled; i++) vals[i] = sl[i];
                                        break;
                                    case ScriptKeyValuePair kvp:
                                        // A Dictionary's foreach element IS a KeyValuePair, and the tree's
                                        // destructure unpacks it (Key then Value); this arm had no such case, so
                                        // `foreach (var kv in d) { var (k, v) = kv; }` from a compiled body
                                        // answered "Cannot destructure type 'ScriptKeyValuePair'" where the tree
                                        // destructured it (tuple_params_test 17b).
                                        filled = Math.Min((int)nVars, 2);
                                        vals[0] = kvp.Key;
                                        if (filled > 1) vals[1] = kvp.Value;
                                        break;
                                    case IScriptHashSet:
                                        // Both refusals go through the engine's own rule (Interpreter.VmRuntimeError), so
                                        // the wording AND the position are the tree's: this arm used to build a
                                        // ScriptException itself, which answered "Expected array or list." with no position
                                        // where the tree says "array, list, tuple or KeyValuePair" at the destructuring
                                        // statement (measured, then pinned by vm_nosource_test).
                                        throw Interpreter.VmRuntimeError<ScriptException>(
                                            "Cannot destructure a HashSet. Use foreach to enumerate elements.", curLine, curCol);
                                    default:
                                        throw Interpreter.VmRuntimeError<ScriptException>(
                                            $"Cannot destructure type '{src?.GetType().Name ?? "null"}'. Expected array, list, tuple or KeyValuePair.",
                                            curLine, curCol);
                                }

                                for (var i = nVars - 1; i >= 0; i--) Push(VmValue.From(vals[i]));
                            }
                            finally { ReturnArgs(vals); }
                            break;
                        }

                    case Opcode.MATCH_EQ:
                        {
                            var pattern = Pop().ToObject();
                            var subject = Pop().ToObject();
                            Push(new VmValue(AreEqual(subject, pattern)));
                            break;
                        }
                    case Opcode.REL_PATTERN:
                        {
                            // Operand: 0=LT 1=GT 2=LE 3=GE. Stack: [subject, right] → pop right, pop subject.
                            var relOp = BytecodeReader.U8(code, ref ip) switch { 0 => TokenType.Less, 1 => TokenType.Greater, 2 => TokenType.LessEqual, _ => TokenType.GreaterEqual };
                            var relRight = Pop().ToObject();
                            var relSubject = Pop().ToObject();
                            Push(new VmValue(interp.MatchRelationalPattern(relSubject, relRight, relOp)));
                            break;
                        }
                    case Opcode.EVENT_ADD:
                    case Opcode.EVENT_SUB:
                        {
                            // Stack: [instance, name-const, handler]. Pop handler + name, then instance.
                            var evHandler = Pop().ToObject();
                            var evName = consts[BytecodeReader.Const(code, ref ip)].O as string;
                            var evInst = Pop().ToObject();
                            var evResult = evInst is ClassInstance evCi
                                ? (op == Opcode.EVENT_ADD
                                    ? interp.EventAdd(evCi, evName ?? "", evHandler, curLine, curCol)
                                    : interp.EventSub(evCi, evName ?? "", evHandler, curLine, curCol))
                                : VmFallback.Instance;
                            if (evResult is VmFallback) return Bail();
                            Push(VmValue.From(evResult));
                            break;
                        }
                    case Opcode.NEW_LIST:
                        locals[BytecodeReader.U8(code, ref ip)] = new VmValue(new List<object?>());
                        break;

                    case Opcode.LIST_APPEND:
                        {
                            var li = BytecodeReader.U8(code, ref ip);
                            var val = Pop();
                            if (locals[li].O is List<object?> lst) lst.Add(val.ToObject());
                            break;
                        }

                    case Opcode.COMP_FINISH:
                        {
                            var list = Pop().O as List<object?>;
                            if (list == null) return Bail();
                            var arr = new object?[list.Count];
                            for (var i = 0; i < list.Count; i++) arr[i] = list[i];
                            Push(new VmValue(arr));
                            break;
                        }
                    case Opcode.OBJ_INIT:
                        {
                            var count = BytecodeReader.U8(code, ref ip);
                            var vals = RentPairs(count);
                            try
                            {
                                for (var i = count - 1; i >= 0; i--)
                                {
                                    var v = Pop().ToObject();
                                    var n = Pop().O as string;
                                    if (n == null) return Bail();
                                    vals[i] = (n, v);
                                }

                                var inst = Pop().ToObject();
                                Push(VmValue.From(interp.ObjectInitValue(inst, vals, curLine, curCol)));
                            }
                            finally { ReturnPairs(vals); }
                            break;
                        }
                    case Opcode.WITH:
                        {
                            var count = BytecodeReader.U8(code, ref ip);
                            var vals = RentPairs(count);
                            try
                            {
                                for (var i = count - 1; i >= 0; i--)
                                {
                                    var v = Pop().ToObject();
                                    var n = Pop().O as string;
                                    if (n == null) return Bail();
                                    vals[i] = (n, v);
                                }

                                var baseVal = Pop().ToObject();
                                // Null base reaches ApplyWithValues' default case → runtime error, same as tree path.
                                Push(VmValue.From(interp.WithValue(baseVal!, vals, curLine, curCol)));
                            }
                            finally { ReturnPairs(vals); }
                            break;
                        }
                    case Opcode.LOAD_SUPER:
                        {
                            var sup = interp.CreateSuperProxy(curLine, curCol);
                            if (sup == null) return Bail();
                            Push(VmValue.From(sup));
                            break;
                        }
                    case Opcode.TRY:
                        {
                            var tOff = BytecodeReader.I16(code, ref ip);
                            (handlers ??= new List<(int sp, int catchIp, int finallyIp, bool pendingThrow, VmValue pendingVal, int seq)>()).Add((sp, ip + tOff, -1, false, default, seq++));
                            break;
                        }

                    case Opcode.ENDTRY:
                        if (handlers != null && handlers.Count > handlerBase) handlers.RemoveAt(handlers.Count - 1);
                        break;

                    case Opcode.TRY_FINALLY:
                        {
                            var fOff = BytecodeReader.I16(code, ref ip);
                            (handlers ??= new List<(int sp, int catchIp, int finallyIp, bool pendingThrow, VmValue pendingVal, int seq)>()).Add((sp, -1, ip + fOff, false, default, seq++));
                            break;
                        }

                    case Opcode.POP_FINALLY:
                        if (handlers != null && handlers.Count > handlerBase) handlers.RemoveAt(handlers.Count - 1);
                        break;

                    case Opcode.END_FINALLY:
                        {
                            var afterIp = BytecodeReader.I16(code, ref ip);
                            if (pendingFinally)
                            {
                                pendingFinally = false;
                                if (pendingFinallyPayload is VmFlowException flow) throw flow;
                                // An abort stored as itself (see the handler walk): rethrow the very
                                // exception, so its exit code reaches the caller intact.
                                if (pendingFinallyPayload is ScriptException carried) throw carried;
                                var val = pendingFinallyPayload;
                                throw new ScriptException(ErrorCode.InvalidOperation, val, curLine, curCol);
                            }

                            ip += afterIp;
                            break;
                        }

                    case Opcode.THROW_FLOW_RETURN:
                        throw new VmFlowException(VmFlowException.KindReturn,
                            sp > spBase ? Pop().ToObject() : null, 0, BytecodeReader.U8(code, ref ip));

                    case Opcode.THROW_FLOW_BREAK:
                        {
                            var bOff = BytecodeReader.I16At(code, ip);
                            var bUnwind = BytecodeReader.ByteAt(code, ip + BytecodeFormat.FieldOffset(OperandKind.TargetU16UnwindU8, 1));
                            ip += BytecodeFormat.ShapeSize(OperandKind.TargetU16UnwindU8);
                            throw new VmFlowException(VmFlowException.KindBreak, null, ip + bOff, bUnwind);
                        }

                    case Opcode.THROW_FLOW_CONTINUE:
                        {
                            var cOff = BytecodeReader.I16At(code, ip);
                            var cUnwind = BytecodeReader.ByteAt(code, ip + BytecodeFormat.FieldOffset(OperandKind.TargetU16UnwindU8, 1));
                            ip += BytecodeFormat.ShapeSize(OperandKind.TargetU16UnwindU8);
                            throw new VmFlowException(VmFlowException.KindContinue, null, ip + cOff, cUnwind);
                        }

                    case Opcode.CATCH_MATCH:
                        {
                            var tIdx = BytecodeReader.Const(code, ref ip);
                            var val = Pop().ToObject();
                            var typeInfo = consts[tIdx].O as TypeInfo;
                            var matches = typeInfo == null || interp.IsExceptionMatch(val, typeInfo);
                            Push(new VmValue(matches));
                            break;
                        }

                    case Opcode.THROW:
                        {
                            var raw = Pop().ToObject();
                            var value = raw is Exception or ScriptErrorInfo
                                ? raw
                                : new ScriptErrorInfo(ErrorCode.InvalidOperation, raw?.ToString() ?? "", raw);
                            throw new ScriptException(ErrorCode.InvalidOperation, value, curLine, curCol);
                        }

                    case Opcode.DUP:
                        {
                            Push(Peek());
                            break;
                        }

                    case Opcode.POP:
                        sp--;
                        break;

                    case Opcode.LOAD_GLOBAL_FAST:
                        {
                            // Push a global via the per-invocation slot table (spec A). First touch
                            // resolves the real scope and caches; VmFallback propagates exactly like
                            // LOAD_GLOBAL_FAST's retired const-indexed twin did. GlobalSlotNames is
                            // nullable (the bundle codec doesn't serialize it until Task 5), a FAST
                            // opcode with no name table is corrupt.
                            var gsi = BytecodeReader.U8(code, ref ip);
                            if (bc.GlobalSlotNames == null || gsi >= globalSlots.Length)
                                throw new ScriptException("Bytecode missing global slot names, re-export bundle.");
                            interp.GlobalFastLoads++;
                            if (globalSlotState[gsi] == 0)
                            {
                                var gname = bc.GlobalSlotNames[gsi];
                                var gval = interp.ResolveGlobalValue(gname);
                                if (gval is VmFallback)
                                {
                                    // Match the tree (VisitVariableExpr): a bare-name read in a
                                    // method that did NOT compile as a member access, static
                                    // fields are never emitted specially (StaticFieldNames is
                                    // unused) and instance fields/events inherited from an
                                    // IMPORTED base class fall outside the compile-time field
                                    // set (same-file parents only), resolves against the
                                    // current class's statics FIRST (tree step 2), then the
                                    // receiver's class hierarchy (tree step 6), before erroring
                                    // (the tree checks locals + global scope first). Keep the
                                    // catchable Undefined-variable error for genuinely missing
                                    // names (for-loop scope shadowing, 2d/5c).
                                    if (interp.TryResolveBareStaticMember(gname, out var staticVal))
                                    {
                                        Push(VmValue.From(staticVal));
                                        break;
                                    }
                                    var thisIdx = Array.IndexOf(bc.Locals, "this");
                                    if (thisIdx >= 0 && locals[thisIdx].ToObject() is ClassInstance thisCi
                                        && interp.TryResolveBareThisMember(thisCi, gname, curLine, curCol, out var thisVal))
                                    {
                                        Push(VmValue.From(thisVal));
                                        break;
                                    }
                                    // A name that is a registered TYPE is a type value, the tree's last resort,
                                    // step for step: `SqliteRow.Empty()` and `ComNode.Make(...)` compile to a
                                    // global load, so without this the VM reports Undefined variable exactly
                                    // where the tree succeeds. Deliberately NOT cached in globalSlots: it is
                                    // not a writable global, so the check stays cheap and stateless.
                                    if (interp.ResolveTypeNameAsValue(gname) is { } bareTypeVal)
                                    {
                                        Push(VmValue.From(bareTypeVal));
                                        break;
                                    }
                                    // A name that IS a type but has no CLR value is the TREE's: a script INTERFACE
                                    // is not a value at all, and `IShapeX.area(obj)` (the qualified call that
                                    // reaches an explicit interface member) is a shape the tree resolves and the
                                    // VM has nothing to put on the stack for. Bailing here is clean precisely
                                    // because the receiver is the FIRST operand compiled: nothing of the
                                    // statement has run yet. Measured: without this the run stopped at
                                    // `Undefined variable 'IShapeX'` where the tree answers
                                    // (crash_interface_test 39).
                                    if (interp.IsKnownTypeName(gname))
                                        return Bail();
                                    throw Interpreter.VmRuntimeError<ScriptException>(
                                        $"Undefined variable '{gname}'.", curLine, curCol, ErrorCode.UndefinedVariable);
                                }
                                globalSlots[gsi] = VmValue.From(gval);
                                globalSlotState[gsi] = 1;
                            }
                            // A GLOBAL can hold a CELL: a top-level ref LOCAL (var re = ref a[2];) stores one, and a
                            // promoted capture does too, so a read DEREFERENCES it exactly as LOAD_VAR does. Measured
                            // without this, `re = re + 1;` read the RefCell itself and refused with a message naming
                            // the internal record, where the file answered the target value.
                            var gpush = globalSlots[gsi];
                            if (gpush.K == 3 && gpush.O is RefCell grefCell) Push(VmValue.From(grefCell.Get()));
                            else Push(gpush);
                            break;
                        }
                    case Opcode.STORE_GLOBAL_FAST:
                        {
                            interp.VmBodyCommitted = true;   // observable work: a later bail must not be handed to the tree
                            // Pop → write the real global scope (always, mirrors STORE_GLOBAL), then
                            // update the cached slot. ScriptException from SetGlobalValue propagates.
                            var gsi = BytecodeReader.U8(code, ref ip);
                            if (bc.GlobalSlotNames == null || gsi >= globalSlots.Length)
                                throw new ScriptException("Bytecode missing global slot names, re-export bundle.");
                            interp.GlobalFastStores++;
                            var sv = Pop();
                            // A STRUCT IS A VALUE TYPE, so a store COPIES it (see STORE_VAR): a program body's
                            // `var b = a;` is a GLOBAL store on the compiled path, which is why the probe's
                            // assignment shape still shared after the local-store fix.
                            if (sv.K == 3 && sv.O is StructInstance svGStruct)
                                sv = VmValue.From(Interpreter.CloneStructForStore(svGStruct));
                            interp.SetGlobalValue(bc.GlobalSlotNames[gsi], sv.ToObject(), curLine, curCol);
                            // A write through a CELL (the slot held a RefCell: a ref local or parameter bound to a global)
                            // landed on that cell's TARGET, so this body's cache for that other name is stale. The write
                            // path says so with ONE flag rather than every global store paying a clear.
                            if (Interpreter.TakeGlobalWriteThroughCell())
                                Array.Clear(globalSlotState);
                            globalSlots[gsi] = sv;
                            globalSlotState[gsi] = 1;
                            break;
                        }
                    case Opcode.CALL_VAL:
                        {
                            var argCount = BytecodeReader.U8(code, ref ip);
                            var callArgs = RentArgs(argCount);
                            try
                            {
                                // Ref args arrive as a K=4 "local slot reference", the same encoding the
                                // string-callee CALL path reads, so the ref positions can be rewired as a
                                // RefCell over OUR slot below (write-through). The index array is
                                // allocated ONLY when a ref arg is actually present: an ordinary by-value
                                // call allocates nothing here.
                                int[]? refIdx = null;
                                for (var i = argCount - 1; i >= 0; i--)
                                {
                                    var av = Pop();
                                    if (av.K == 4)
                                    {
                                        if (refIdx == null) { refIdx = new int[argCount]; Array.Fill(refIdx, -1); }
                                        refIdx[i] = (int)av.L;
                                        callArgs[i] = av.O;
                                    }
                                    else
                                    {
                                        callArgs[i] = av.ToObject();
                                    }
                                }
                                var callee = Pop().ToObject();
                                // The REF/OUT ARG FLAGS the compiler pushed under this call (see ArgFlagsMarker):
                                // the K=4 argument marker cannot carry them, and they are what let a `params` tail
                                // refusal fire (measured: `f(ref a, 2)` on a lambda declared `params int[] xs`
                                // passed a COPY and answered 2 where the tree refuses, vm_params 7e).
                                ArgFlagsMarker? valArgFlags = null;
                                if (sp > 0 && sO[sp - 1] is ArgFlagsMarker vFlagsMarker)
                                {
                                    sp--;
                                    valArgFlags = vFlagsMarker;
                                }
                                // Named-arg call through a VALUE callee (local/param-held function or
                                // delegate): the compiler pushes a NamedArgsMarker const BELOW the callee
                                // value ([marker, calleeValue, args…]), so after args + callee are popped
                                // it is the new stack top. Reorder against the resolved callee's declared
                                // params (ScriptFunction), same rules as the CALL path. A non-function
                                // value with a marker falls back to the tree (InvokeCallableValue → VmFallback).
                                // A VALUE-callee call whose result is used as a REFERENCE: same marker as the CALL arm,
                                // popped after the named one because the compiler pushes it BELOW it.
                                var valWantsRef = false;
                                if (sp > 0 && sO[sp - 1] is RefResultMarker)
                                {
                                    sp--;
                                    valWantsRef = true;
                                }
                                if (sp > 0 && sO[sp - 1] is NamedArgsMarker vMarker)
                                {
                                    Interpreter.NamedTrace("CALL_VAL",
                                        $"callee={callee?.GetType().Name ?? "null"} names={Interpreter.NamedTraceNames(vMarker.Names)}");
                                    if (callee is ScriptFunction sfVal)
                                    {
                                        sp--;
                                        callArgs = interp.ReorderNamedArgs(callArgs, vMarker.Names, sfVal.Declaration.Params, curLine, curCol);
                                    }
                                    else
                                    {
                                        // Marker present but callee isn't a known-params function (e.g. a
                                        // native/delegate value), let the tree resolve/error it.
                                        return Bail();
                                    }
                                }
                                // Arity, for the same reason and through the same helper as the tree: a script-side
                                // call of a zero-parameter function with arguments is REFUSED. The VM used to accept
                                // it here and write the arguments into the callee's capture slots, which is how
                                // `Com.Run(fun() { ... })` came to read the scope. The C#-side entry stays tolerant,
                                // so that same Com.Run shape keeps working, see CheckScriptCallArity.
                                if (callee is ScriptFunction sfArity)
                                    interp.CheckScriptCallArity(sfArity.Declaration, callArgs.Length, curLine, curCol);
                                // True only when EVERY ref AND out position of the callee arrived as a slot
                                // reference and was wired as a cell, the flat in-place call below binds
                                // those cells into the callee's locals (deref on read, write-through on store;
                                // an out cell also records assignment), so it stays eligible. A callee called
                                // WITHOUT the matching ref/out keeps the InvokeCallableValue route.
                                var refWiredAll = false;
                                bool[]? outTrack = null;
                                // A `super(ref x)` CALL has a SuperProxy callee, which carries no declaration, so the
                                // ref/out wiring below (which needs one to verify positions against) BAILED here. A
                                // loaded constructor body has no tree to hand the call to, so `super(ref n)` became a
                                // silent no-op: measured on a stripped bundle of `class B extends A { constructor(ref
                                // string n) { n = n + "z"; super(ref n); } }`, where B's body ran, the parent's never
                                // did, and `this.Name` stayed empty. The parent's constructor is matched INSIDE
                                // InvokeSuperConstructor, and it binds a cell it is handed (write-through), so the only
                                // work here is to give each FLAGGED position the cell its slot reference names: the
                                // ArgFlagsMarker says which those are, since a K=4 argument means "came from a local
                                // slot", by value included.
                                if (refIdx != null && callee is SuperProxy)
                                {
                                    for (var i = 0; i < argCount; i++)
                                    {
                                        if (refIdx[i] < 0) continue;
                                        var flagged = valArgFlags != null
                                            && ((i < valArgFlags.Refs.Length && valArgFlags.Refs[i])
                                                || (i < valArgFlags.Outs.Length && valArgFlags.Outs[i]));
                                        if (!flagged) continue;
                                        object? held = locals[refIdx[i]].ToObject();
                                        if (held is RefCell alreadyCell)
                                        {
                                            callArgs[i] = alreadyCell;
                                            continue;
                                        }
                                        var cell = new RefCell(() => held, v => held = v);
                                        locals[refIdx[i]] = VmValue.From(cell);
                                        callArgs[i] = cell;
                                    }
                                    // Nothing left for the declaration-checked block below: the parent ctor's own bind
                                    // is what owns these positions from here.
                                    refIdx = null;
                                }
                                if (refIdx != null)
                                {
                                    // A ref/out param must reach the callee as a RefCell over the caller's local
                                    // slot so its write propagates back (an out cell additionally records the
                                    // assignment). A callee that is not a script function (native/delegate value)
                                    // has no declaration to read, that still defers to the tree.
                                    if (callee is not ScriptFunction sfRef)
                                        return Bail();

                                    var dRef = sfRef.Declaration;
                                    bool IsRefPos(int i) =>
                                        (dRef.IsRefParams != null && i < dRef.IsRefParams.Count && dRef.IsRefParams[i]) ||
                                        (dRef.IsOutParams != null && i < dRef.IsOutParams.Count && dRef.IsOutParams[i]);

                                    // Every ref/out position must be a local-slot arg OR an already-built CELL (a
                                    // GLOBAL ref: the engine's helper answers one, see CompileArgument); anything
                                    // else (a literal, a property...) is not an lvalue, so the tree reports it.
                                    var canWrap = true;
                                    for (var i = 0; i < argCount; i++)
                                        if (IsRefPos(i) && refIdx[i] < 0 && callArgs[i] is not RefCell) { canWrap = false; break; }
                                    if (!canWrap)
                                    {
                                        // Same rule as the function path above: a VALUE where the declaration demands
                                        // `ref`/`out` is refused by name rather than handed to a tree this body does
                                        // not have.
                                        for (var i = 0; i < argCount; i++)
                                            if (IsRefPos(i) && refIdx[i] < 0 && callArgs[i] is not RefCell)
                                                Interpreter.RefuseRefCellOnByValueParam(
                                                    dRef.Params[i].Name, callArgs[i],
                                                    i < dRef.IsRefParams.Count && dRef.IsRefParams[i],
                                                    dRef.IsOutParams != null && i < dRef.IsOutParams.Count && dRef.IsOutParams[i],
                                                    curLine, curCol);
                                        return Bail();   // unreachable: the rule refuses
                                    }

                                    for (var i = 0; i < argCount; i++)
                                    {
                                        if (!IsRefPos(i)) continue;
                                        RefCell cell;
                                        if (refIdx[i] < 0)
                                        {
                                            // The argument arrived AS a cell: a `ref`/`out` target that is a GLOBAL.
                                            if (callArgs[i] is not RefCell passed) return Bail();
                                            cell = passed;
                                        }
                                        else
                                        {
                                            var idx = refIdx[i];
                                            object? held = locals[idx].ToObject();
                                            var existing = held as RefCell;
                                            if (existing == null)
                                            {
                                                existing = new RefCell(() => held, v => held = v);
                                                locals[idx] = VmValue.From(existing);   // shared, callee writes through
                                            }
                                            cell = existing;
                                        }
                                        if (dRef.IsOutParams != null && i < dRef.IsOutParams.Count && dRef.IsOutParams[i])
                                        {
                                            outTrack ??= new bool[dRef.Params.Count];
                                            var outFlag = i;
                                            var outType = dRef.Params[i].Type;
                                            var outName = dRef.Params[i].Name;
                                            var innerCell = cell;
                                            cell = new RefCell(innerCell.Getter, v =>
                                            {
                                                var coerced = interp.ValidateOutAssignmentFromVm(v, outType, outName, curLine, curCol);
                                                outTrack[outFlag] = true;
                                                innerCell.Set(coerced);
                                            });
                                            // The SLOT keeps the plain cell (innerCell), which is what the caller
                                            // reads: storing the TRACKING wrapper here made every call wrap the
                                            // previous call's wrapper, so a loop of 50,000 out-param calls built a
                                            // 50,000-deep `Set` chain and the process died with 0xC0000005 (measured,
                                            // crash_out_ref_stress section 10). The callee still gets the tracking
                                            // cell as its argument, and its writes reach the slot through innerCell.
                                            callArgs[i] = cell;
                                            continue;
                                        }
                                        callArgs[i] = cell;
                                    }

                                    refWiredAll = true;   // every ref/out position verified and wrapped above
                                }

                                // ── Flat call (VALUE callee), mirrors the CALL handler's in-place swap,
                                // so deep local-function / lambda recursion stays VM-flat (the heap
                                // `frames` list holds the pending callers, the C# stack stays flat).
                                // Previously CALL_VAL always re-entered InvokeScriptFunction (one C# frame
                                // per level), a Debug depth-24 cap. Captures (VmCaptures) are appended
                                // after the args, exactly like TryExecuteVmBody's rentedArgs layout, so a
                                // capturing local function / MAKE_LAMBDA lambda recurses correctly.
                                // Eligibility is the same conservative set as the CALL flat-call (compiled,
                                // no out, not a coroutine/shell) plus ref params ONLY when their cells were
                                // wired above; defaults and params arrays are normalized before the gate.
                                // Same defaults/params normalization as the CALL path: the flat swap binds
                                // values into the callee's locals, so its defaults must exist and its params
                                // array must be assembled BEFORE the swap.
                                object?[]? normalizedValArgs = null;
                                // A flagged ref/out argument in the callee's `params` tail must reach the assembler
                                // as a cell, since that is where the rule lives (see the helper's note).
                                if (valArgFlags is { } tailFlags && callee is ScriptFunction sfTailFlags)
                                    RewrapFlaggedTailArgsAsCells(sfTailFlags.Declaration, tailFlags, refIdx, locals, callArgs, argCount);
                                if (callee is ScriptFunction sfNorm
                                    && (sfNorm.Declaration.DefaultValues != null
                                        || (sfNorm.Declaration.IsParams.Count > 0 && sfNorm.Declaration.IsParams[^1])))
                                    normalizedValArgs = interp.NormalizeArgsForFlatCall(sfNorm.Declaration, callArgs, curLine, curCol);

                                if (!valWantsRef && callee is ScriptFunction sfFlat && !sfFlat.IsCoroutine
                                    && !sfFlat.Declaration.HasYield
                                    && sfFlat.Declaration.Compiled != null && !sfFlat.Declaration.IsVmShell
                                    && ((!sfFlat.Declaration.IsRefParams.Any(b => b)
                                         && !HasOutParam(sfFlat.Declaration))
                                        || refWiredAll)
                                    // A 2nd+ instance of a shared local FuncDecl may still lack its
                                    // per-instance VmCaptures (materialized in InvokeScriptFunction on
                                    // the VM-direct gate). Flat-calling it now would fill the capture
                                    // slots from zeroed pooled locals, defer to InvokeCallableValue so
                                    // materialization runs first; later calls (VmCaptures set) go flat.
                                    && (sfFlat.VmCaptures != null || !sfFlat.IsLocal))
                                {
                                    var dFlat = sfFlat.Declaration;
                                    object?[] flatArgs = normalizedValArgs ?? callArgs;
                                    if (sfFlat.VmCaptures is { Length: > 0 })
                                    {
                                        // The capture block starts at the callee's DECLARED PARAMETER COUNT, not at the
                                        // number of arguments this call happened to pass. The two differ exactly when a
                                        // caller passes more arguments than the callee declares, which is legal (extras
                                        // are ignored, as the tree-walker does) and is the shape
                                        // `Com.Run(fun() { … })` uses: ONE argument, ZERO declared parameters. Appending
                                        // at callArgs.Length then put the argument into capture slot 0 and handed it to
                                        // every captured read, measured: such a callback read the SCOPE where the
                                        // enclosing function's variable should have been, and with two declared
                                        // parameters the reads went empty while the extra parameter received the
                                        // enclosing frame's value. Copying min(passed, declared) keeps the argument
                                        // block where the compiler expects it and drops the extras, which is what the
                                        // tree does. `normalizedValArgs` (defaults/params arrays) already holds exactly
                                        // one entry per declared parameter, so this is the same layout as before for it.
                                        var declaredParams = dFlat.Params.Count;
                                        var packed = new object?[declaredParams + sfFlat.VmCaptures.Length];
                                        var copyCount = Math.Min(flatArgs.Length, declaredParams);
                                        Array.Copy(flatArgs, packed, copyCount);
                                        Array.Copy(sfFlat.VmCaptures, 0, packed, declaredParams, sfFlat.VmCaptures.Length);
                                        flatArgs = packed;
                                    }
                                    if (frames.Count >= VmMaxFrames)
                                        throw new ScriptException(DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                    interp.VmInvocations++;
                                    // RENTED for the same reason the CALL site above is: one frame per value-callee call,
                                    // and this pool already serves the generator boundary frames.
                                    var callFrame = RentSavedFrame();
                                    callFrame.Name = dFlat.Name;
                                    callFrame.Decl = dFlat;
                                    callFrame.OutTrack = outTrack;
                                    callFrame.Bc = bc;
                                    callFrame.Code = code;
                                    callFrame.Ip = ip;
                                    callFrame.Consts = consts;
                                    callFrame.Locals = locals;
                                    callFrame.CurLine = curLine;
                                    callFrame.CurCol = curCol;
                                    callFrame.SpBase = spBase;
                                    callFrame.Handlers = handlers;
                                    callFrame.HandlerBase = handlerBase;
                                    callFrame.PendingFinally = pendingFinally;
                                    callFrame.PendingFinallyPayload = pendingFinallyPayload;
                                    callFrame.Seq = seq;
                                    callFrame.GlobalSlots = globalSlots;
                                    callFrame.GlobalSlotState = globalSlotState;
                                    callFrame.NsContext = curNs;
                                    callFrame.ForEachStates = forEachStates;
                                    callFrame.GenCo = curGenCo;
                                    frames.Add(callFrame);
                                    currentFunc = dFlat.Name;
                                    bc = dFlat.Compiled!;
                                    interp.RegisterNestedDeclsForRun(bc);
                                    code = bc.Code;
                                    consts = bc.ConstsVm;
                                    curNs = sfFlat.NamespaceContext;
                                    locals = RentLocals();
                                    for (var ai = 0; ai < flatArgs.Length && ai < locals.Length; ai++)
                                        locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(sfFlat.Declaration, flatArgs[ai], ai, curLine, curCol));
                                    ip = 0;
                                    curLine = 0;
                                    curCol = 0;
                                    spBase = sp;
                                    handlers = null;   // lazily allocated on the callee's first TRY
                                    handlerBase = 0;
                                    pendingFinally = false;
                                    pendingFinallyPayload = null;
                                    seq = 0;
                                    forEachStates = null;
                                    Array.Clear(globalSlotState);
                                    (globalSlots, globalSlotState) = RentSlots();
                                    continue;
                                }
                                // A `super(...)` CALLEE is a SuperProxy, which InvokeCallableValue cannot invoke:
                                // it answers VmFallback, so a COMPILED constructor body that called `super()`
                                // bailed, and a body loaded from bytecode has no AST to fall back to (the tree's
                                // arm ran then, over an empty statement list, silently). Measured on a stripped
                                // bundle: `new C()` ran NO constructor and read 0 where the file reads 5. The
                                // parent-constructor call has ONE implementation now, the interpreter's
                                // InvokeSuperConstructor, which the tree's own arm asks.
                                if (callee is SuperProxy)
                                {
                                    var superResult = interp.InvokeSuperConstructor((SuperProxy)callee, callArgs, curLine, curCol);
                                    // The parent's body may have been a compiled one: same reason as below.
                                    Array.Clear(globalSlotState);
                                    Push(VmValue.From(superResult));
                                    break;
                                }
                                // A TYPE VALUE CALLED AS A FUNCTION (`var tt = typename("int"); tt();`) has the message the
                                // tree's call arm owns, so it is raised here instead of aborting: a bundle cannot re-run
                                // the statement on the tree (measured: error_msgs_test test 8).
                                if (callee is Type calledTypeValue)
                                    interp.RuntimeErrorFromVm($"Type '{calledTypeValue.Name}' is not a function.", curLine, curCol);
                                var result = valWantsRef && callee is ScriptFunction sfWantRef
                                    ? interp.InvokeScriptFunction(sfWantRef, callArgs, curLine, curCol, wantRefResult: true)
                                    : interp.InvokeCallableValue(callee, callArgs, curLine, curCol);
                                // May have invoked a script function (nested Vm.Execute) that mutated globals.
                                Array.Clear(globalSlotState);
                                if (result is VmFallback)
                                {
                                    return Bail();
                                }
                                Push(VmValue.From(result));
                                break;
                            }
                            finally
                            {
                                ReturnArgs(callArgs);
                            }
                        }
                    case Opcode.MAKE_INDEX:
                        {
                            var flag = BytecodeReader.U8(code, ref ip) == 1;
                            var idxVal = Pop().ToObject();
                            Push(new VmValue(new Index(Convert.ToInt32(idxVal), flag)));
                            break;
                        }
                    case Opcode.MAKE_RANGE:
                        {
                            var hasTo = BytecodeReader.U8(code, ref ip) == 1;
                            var toObj = Pop().ToObject();
                            var fromObj = Pop().ToObject();
                            var from = Convert.ToInt64(fromObj ?? 0L);
                            var to = hasTo ? Convert.ToInt64(toObj!) : from - 1;
                            if (from > to)
                                throw new ScriptException(ErrorCode.Unspecified,
                                    $"Invalid range {from}..{to}: start must be <= end.", curLine, curCol);
                            if (to - from >= 1000000)
                                throw new ScriptException(ErrorCode.Unspecified,
                                    $"Range {from}..{to} is too large (max 1,000,000 elements).", curLine, curCol);
                            var count = (int)(to - from + 1);
                            var result = new long[count];
                            for (var i = 0; i < count; i++) result[i] = from + i;
                            Push(VmValue.From(result));
                            break;
                        }
                    case Opcode.CAST:
                        {
                            var tIdx = BytecodeReader.Const(code, ref ip);
                            var v = Pop().ToObject();
                            var t = consts[tIdx].O as TypeInfo;
                            if (t == null)
                            {
                                return Bail();
                            }

                            Push(VmValue.From(interp.CastValue(t, v, curLine, curCol)));
                            break;
                        }
                    case Opcode.AS:
                        {
                            var tIdx = BytecodeReader.Const(code, ref ip);
                            var v = Pop().ToObject();
                            var t = consts[tIdx].O as TypeInfo;
                            if (t == null)
                            {
                                return Bail();
                            }

                            Push(VmValue.From(interp.AsValue(t, v, curLine, curCol)));
                            break;
                        }
                    case Opcode.NEW_ARRAY:
                        {
                            var nSizes = BytecodeReader.U8(code, ref ip);
                            var nInits = BytecodeReader.U8(code, ref ip);
                            var initVals = RentArgs(nInits);
                            var sizeVals = RentArgs(nSizes);
                            try
                            {
                                for (var i = nInits - 1; i >= 0; i--) initVals[i] = Pop().ToObject();
                                for (var i = nSizes - 1; i >= 0; i--) sizeVals[i] = Pop().ToObject();
                                var et = Pop().O as TypeInfo;
                                if (et == null)
                                {
                                    return Bail();
                                }

                                Push(VmValue.From(interp.CreateArrayValue(et, sizeVals, initVals, curLine, curCol)));
                            }
                            finally
                            {
                                ReturnArgs(initVals);
                                ReturnArgs(sizeVals);
                            }
                            break;
                        }
                    case Opcode.NEW_STRUCT:
                        {
                            var nFields = BytecodeReader.U8(code, ref ip);
                            var vals = RentArgs(nFields);
                            try
                            {
                                for (var i = nFields - 1; i >= 0; i--) vals[i] = Pop().ToObject();
                                var sn = Pop().O as string;
                                if (sn == null)
                                {
                                    return Bail();
                                }

                                Push(VmValue.From(interp.CreateStructValue(sn, vals, curLine, curCol)));
                            }
                            finally { ReturnArgs(vals); }
                            break;
                        }
                    case Opcode.CALL_NEW:
                        {
                            var argCount = BytecodeReader.U8(code, ref ip);
                            var newArgs = RentArgs(argCount);
                            try
                            {
                                // Ref args arrive as a K=4 "local slot reference", the same encoding the CALL and
                                // CALL_METHOD paths read, so the flagged positions can be rewired as RefCells over
                                // OUR slots (or taken as the cell a global target already arrives in) once the call
                                // site's flags are known. The index array is allocated ONLY when one is present.
                                int[]? newRefIdx = null;
                                for (var i = argCount - 1; i >= 0; i--)
                                {
                                    var nv = Pop();
                                    if (nv.K == 4)
                                    {
                                        if (newRefIdx == null) { newRefIdx = new int[argCount]; Array.Fill(newRefIdx, -1); }
                                        newRefIdx[i] = (int)nv.L;
                                        newArgs[i] = nv.O;
                                    }
                                    else
                                    {
                                        newArgs[i] = nv.ToObject();
                                    }
                                }
                                var typeName = Pop().O as string;
                                if (typeName == null)
                                {
                                    return Bail();
                                }
                                // Named ctor args: the compiler pushes a NamedArgsMarker BELOW the type
                                // name ([marker, typeName, args…]), after args + typeName are popped it
                                // is the new stack top. Reorder against the resolved ctor's params.
                                if (sp > 0 && sO[sp - 1] is NamedArgsMarker cMarker)
                                {
                                    Interpreter.NamedTrace("CALL_NEW",
                                        $"type={typeName} names={Interpreter.NamedTraceNames(cMarker.Names)}");
                                    if (interp.TryGetCtorParams(typeName, out var ctorParams))
                                    {
                                        sp--;
                                        newArgs = interp.ReorderNamedArgs(newArgs, cMarker.Names, ctorParams, curLine, curCol);
                                    }
                                    else
                                    {
                                        return Bail();
                                    }
                                }

                                // TYPE ARGUMENTS (`new List<int>()`): the compiler pushes a TypeArgsMarker
                                // under the named-args marker, so it is the stack top once that one is
                                // consumed. The interpreter closes the type, exactly as it does for a
                                // generic method call.
                                TypeArgList? newTypeArgs = null;
                                if (sp > 0 && sO[sp - 1] is TypeArgsMarker taMarker)
                                {
                                    sp--;
                                    newTypeArgs = new TypeArgList(taMarker.TypeArgs);
                                }
                                // The REF/OUT ARG FLAGS the compiler pushed under this call (see ArgFlagsMarker):
                                // they say which positions the call site wrote a keyword at, which is what turns a
                                // K=4 slot reference (or a cell a global target already is) into the cell the ctor's
                                // bind expects, and what tells the bind a swapped keyword. Read AFTER the named and
                                // type-args markers, because it sits below both.
                                ArgFlagsMarker? newArgFlags = null;
                                if (sp > 0 && sO[sp - 1] is ArgFlagsMarker nArgFlags)
                                {
                                    sp--;
                                    newArgFlags = nArgFlags;
                                }
                                if (newArgFlags != null)
                                {
                                    for (var i = 0; i < argCount; i++)
                                    {
                                        var wantRef = i < newArgFlags.Refs.Length && newArgFlags.Refs[i];
                                        var wantOut = i < newArgFlags.Outs.Length && newArgFlags.Outs[i];
                                        if (!wantRef && !wantOut) continue;
                                        if (newRefIdx != null && newRefIdx[i] >= 0)
                                        {
                                            var slot = newRefIdx[i];
                                            object? held = locals[slot].ToObject();
                                            var cell = held as RefCell;
                                            if (cell == null)
                                            {
                                                // ByOut is the CALL SITE's keyword: a cell that knows it is what
                                                // RefuseRefCellOnByValueParam judges a `ref`-at-`out` swap by.
                                                var byOut = wantOut;
                                                cell = new RefCell(() => held, v => held = v, ByOut: byOut);
                                                locals[slot] = VmValue.From(cell);   // shared, the ctor writes through
                                            }
                                            newArgs[i] = cell;
                                        }
                                        else if (newArgs[i] is RefCell passedCell)
                                        {
                                            newArgs[i] = passedCell;   // a `ref`/`out` target that is a GLOBAL
                                        }
                                        else
                                        {
                                            // Not an lvalue (a literal, a property, ...): the tree owns the refusal.
                                            return Bail();
                                        }
                                    }
                                }

                                // A `new` on a name that is NOT A TYPE (`new num(1, 2)` where num is a variable) is the
                                // tree's own refusal, and it can be raised here: a bundle refuses the whole entry
                                // otherwise (measured: error_msgs_test test 7). A name the engine knows keeps the
                                // existing road, because InstantiateForVm may legitimately defer a known type to the tree.
                                if (!interp.IsKnownTypeName(typeName))
                                    interp.RuntimeErrorFromVm($"Type '{typeName}' not defined.", curLine, curCol);
                                var result = interp.InstantiateForVm(typeName, newTypeArgs, newArgs, curLine, curCol,
                                    newArgFlags?.Refs, newArgFlags?.Outs);
                                // Script constructors can mutate globals, flush the slot cache.
                                Array.Clear(globalSlotState);
                                if (result is VmFallback) return Bail();
                                Push(VmValue.From(result));
                            }
                            finally { ReturnArgs(newArgs); }
                            break;
                        }
                    case Opcode.FOREACH_INIT:
                        {
                            var stateLocal = BytecodeReader.U8(code, ref ip);
                            var idxLocal = BytecodeReader.U8(code, ref ip);
                            BytecodeReader.U8(code, ref ip); // allowEnumerable flag, retained for instruction layout; classification is runtime-lazy
                            var coll = Pop().ToObject();
                            // ── Folded generator-foreach: a VM-backed Generator<T> is driven
                            // in-place and its per-step state is just the driven generator's
                            // Coroutine, store it in the state local (NOT a ~96 B
                            // VmForEachState). The element flows through the shared operand
                            // stack; the frame's GenHasNext marks the active drive. Nothing is
                            // tracked in forEachStates, there is nothing to dispose (FOREACH_END
                            // and the abandoned-loop scan type-check VmForEachState, so they no-op
                            // on the folded state). Non-VmBacked generators (yield-request bodies)
                            // keep the old path: CreateForEachState → null → VM falls back to the
                            // tree, which owns the request machinery.
                            if (coll is Generator<object?> foldGen && foldGen.InnerCoroutine.VmBacked)
                            {
                                locals[stateLocal] = VmValue.From(foldGen.InnerCoroutine);
                                locals[idxLocal] = new VmValue(0L);
                                break;
                            }

                            var state = CreateForEachState(coll, interp);
                            if (state == null)
                            {
                                return Bail();
                            }

                            locals[stateLocal] = new VmValue(state);
                            locals[idxLocal] = new VmValue(0L);
                            (forEachStates ??= new List<(int stateLocal, int seq)>()).Add((stateLocal, seq++));
                            break;
                        }
                    case Opcode.FOREACH_NEXT:
                        {
                            var endOff = BytecodeReader.I16(code, ref ip);
                            var vtIdx = BytecodeReader.Const(code, ref ip);   // the loop var's type const
                            var idxV = Pop();
                            var stV = Pop();
                            if (idxV.K != 1)
                            {
                                return Bail();
                            }

                            var hasNext = true;
                            object? elem = null;
                            Coroutine? driveCo;
                            if (stV.O is Coroutine foldedCo)
                            {
                                // ── Folded generator-foreach: FOREACH_INIT stored the driven
                                // generator's Coroutine directly in the state local, no
                                // VmForEachState was allocated (the frame's GenHasNext marks the
                                // drive). Bare Coroutine collections never reach the VM foreach
                                // (CreateForEachState → null → tree fallback), so a Coroutine in
                                // the state slot is unambiguous. Drive it in place below (genDrive)
                                //, the yielded element arrives via the YIELD swap-back, a RETURN
                                // marks it Dead + jumps to FOREACH_END.
                                driveCo = foldedCo;
                                goto genDrive;
                            }

                            if (stV.O is not VmForEachState st)
                            {
                                return Bail();
                            }

                            switch (st.Kind)
                            {
                                case VmForEachState.KindArray:
                                    if (st.Arr == null || idxV.L >= st.Arr.Length) hasNext = false;
                                    else elem = st.Arr.GetValue((int)idxV.L);
                                    break;
                                case VmForEachState.KindString:
                                    if (st.Str == null || idxV.L >= st.Str.Length) hasNext = false;
                                    else elem = st.Str[(int)idxV.L];
                                    break;
                                case VmForEachState.KindScriptList:
                                    if (st.ScriptList == null || idxV.L >= st.ScriptList.Count) hasNext = false;
                                    else elem = st.ScriptList[(int)idxV.L];
                                    break;
                                case VmForEachState.KindIList:
                                    if (st.List == null || idxV.L >= st.List.Count) hasNext = false;
                                    else elem = st.List[(int)idxV.L];
                                    break;
                                case VmForEachState.KindDict:
                                    if (st.Entries == null || idxV.L >= st.Entries.Length) hasNext = false;
                                    else elem = st.Entries[(int)idxV.L];
                                    break;
                                case VmForEachState.KindEnumerable:
                                    if (st.Enumerator == null || !st.Enumerator.MoveNext()) hasNext = false;
                                    else elem = st.Enumerator.Current;
                                    break;
                                case VmForEachState.KindGenerator:
                                    // Non-folded generator state, retained for cases that need a
                                    // VmForEachState (e.g. a generator-foreach state materialized
                                    // outside FOREACH_INIT); shares the in-place drive below (genDrive).
                                    driveCo = st.Generator;
                                    goto genDrive;
                                case VmForEachState.KindTreeCoroutine:
                                    // A coroutine the VM cannot drive in place is stepped through the TREE, one
                                    // Resume() per element, the very call a tree foreach makes (VisitForeachStmt's
                                    // Coroutine case). The first step mirrors that shape: start it only when it has
                                    // not started, otherwise take the value it already holds; later steps resume first.
                                    var tc = st.TreeCoro;
                                    if (tc == null) { hasNext = false; break; }
                                    if (!st.TreeCoroPrimed)
                                    {
                                        st.TreeCoroPrimed = true;
                                        if (tc.Status == CoroutineStatus.NotStarted) tc.Resume();
                                    }
                                    else if (tc.IsResumable) tc.Resume();
                                    if (tc.IsResumable) elem = tc.LastYieldedValue;
                                    else hasNext = false;
                                    break;
                                default:
                                    return Bail();
                            }

                            if (!hasNext)
                            {
                                ip += endOff;
                                break;
                            }

                            if (consts[vtIdx].O is TypeInfo vt) elem = CoerceTo(elem, vt, curLine, curCol);
                            Push(VmValue.From(elem));
                            break;

                        genDrive:
                            {
                                // ── In-place generator drive (flat frames), shared by the folded
                                // path (driveCo read from the state local) and the non-folded
                                // KindGenerator VmForEachState path. The generator's frame lives in
                                // `frames`; the foreach owns the drive exactly like a flat CALL. The
                                // yielded value arrives via the nested YIELD handler (which swaps back
                                // to this FOREACH_NEXT's post-instruction STORE); a generator RETURN
                                // is intercepted by the RETURN handler (marks Dead, jumps to
                                // FOREACH_END). Non-VM/abandoned generators end the loop.
                                var co = driveCo;
                                if (co == null || !co.IsResumable || co.Fn.Declaration.Compiled == null)
                                {
                                    hasNext = false;
                                    goto genNoNext;
                                }
                                // Locate its retained (SuspendedGenerator) frame, in the common
                                // recursive case it is frames[^1]; stale frames from abandoned
                                // loops may sit above, so search from the top by handle. A
                                // NotStarted generator has no retained frame yet (first visit),
                                // skip the O(frames) scan (a deep fresh drive would otherwise be
                                // O(depth²)).
                                var genIdx = -1;
                                if (co.Status != CoroutineStatus.NotStarted)
                                {
                                    // Fast path: the generator's retained frame is frames[^1] in the
                                    // common flat case (YIELD just pushed it); scan only when nested
                                    // generator frames sit above it.
                                    var n = frames.Count;
                                    if (n > 0 && frames[n - 1].GeneratorHandle == co && frames[n - 1].SuspendedGenerator)
                                        genIdx = n - 1;
                                    else
                                        for (var fi = n - 1; fi >= 0; fi--)
                                            if (frames[fi].GeneratorHandle == co && frames[fi].SuspendedGenerator)
                                            {
                                                genIdx = fi;
                                                break;
                                            }
                                    if (genIdx < 0)
                                    {
                                        // A captured suspension (an ABANDONED drive, or a boundary-suspended
                                        // VmBacked generator whose state a tree-side `MoveNext` captured): resume it
                                        // here. This is the same hand-off as the retained-frame resume below, except
                                        // the generator's own state comes from the SNAPSHOT, because its frame is
                                        // gone. It is what makes an abandon-then-re-foreach match the tree, which
                                        // drives the generator through MoveNext and continues where it stopped.
                                        if (co.VmBacked && co.VmState is { } snap)
                                        {
                                            co.Status = CoroutineStatus.Dead; // mid-drive, as the resume below
                                            co.VmState = null;
                                            var resumeDrive = RentSavedFrame();
                                            resumeDrive.Name = currentFunc;
                                            resumeDrive.CallerName = currentFunc;
                                            resumeDrive.Bc = bc;
                                            resumeDrive.Code = code;
                                            resumeDrive.Ip = ip;
                                            resumeDrive.Consts = consts;
                                            resumeDrive.Locals = locals;
                                            resumeDrive.CurLine = curLine;
                                            resumeDrive.CurCol = curCol;
                                            resumeDrive.SpBase = spBase;
                                            // The DRIVER's operand height at drive time. The swap-back used to restore
                                            // the GENERATOR's spBase here, which is the same number only on the paths that
                                            // enter the body in place (`spBase = sp` right after the push). A coroutine
                                            // started by `Coroutine.Start` (autoStart) is resumed from a VmState captured
                                            // in ANOTHER invocation, whose base has nothing to do with this loop's height:
                                            // measured `driverBase=2 snapBase=0`, so the driver's own `return` then saw
                                            // `sp=0 < spBase=2`, popped nothing and refused its declared `int`.
                                            resumeDrive.Sp = sp;
                                            resumeDrive.Handlers = handlers;
                                            resumeDrive.HandlerBase = handlerBase;
                                            resumeDrive.PendingFinally = pendingFinally;
                                            resumeDrive.PendingFinallyPayload = pendingFinallyPayload;
                                            resumeDrive.Seq = seq;
                                            resumeDrive.ForEachStates = forEachStates;
                                            resumeDrive.GlobalSlots = globalSlots;
                                            resumeDrive.GlobalSlotState = globalSlotState;
                                            resumeDrive.NsContext = curNs;
                                            resumeDrive.GeneratorHandle = co;
                                            resumeDrive.ForEachEndIp = ip + endOff;
                                            resumeDrive.GenCo = curGenCo;
                                            resumeDrive.GenHasNext = true;
                                            frames.Add(resumeDrive);
                                            // The generator's continuation, out of the snapshot.
                                            bc = snap.Bc;
                                            code = snap.Code;
                                            consts = snap.Bc.ConstsVm;
                                            ip = snap.Ip;
                                            locals = snap.Locals;
                                            // RELOCATE the operand region to the driver's height. A captured VmState carries the base
                                            // of the invocation that suspended it, which can be BELOW the driver's live operands
                                            // (measured: snapBase=0 against the driver's 2), so resuming there wrote the
                                            // generator's own operands over the driver's stack slots: the caller's `Check` and
                                            // its description came back as gen's `0`, `4`, `1`, and the next CALL bailed with
                                            // a number for a callee. The retained-frame path already reinstates at the current
                                            // height, and this is the same hand-off from a snapshot.
                                            // The captured array is that invocation's WHOLE stack ([0, Sp)), so the region that
                                            // belongs to the generator starts at its base and only that part travels, in order.
                                            var opStart = snap.SpBase > 0 && snap.SpBase <= snap.Stack.Length ? snap.SpBase : 0;
                                            var opLen = snap.Stack.Length - opStart;
                                            if (sp + opLen > sK.Length)
                                            {
                                                var need = sp + opLen;
                                                Array.Resize(ref sK, need);
                                                Array.Resize(ref sT, need);
                                                Array.Resize(ref sL, need);
                                                Array.Resize(ref sD, need);
                                                Array.Resize(ref sO, need);
                                            }
                                            spBase = sp;
                                            for (var si = 0; si < opLen; si++)
                                            {
                                                var sv = snap.Stack[opStart + si];
                                                sK[sp] = sv.K;
                                                sT[sp] = sv.T;
                                                sL[sp] = sv.L;
                                                sD[sp] = sv.D;
                                                sO[sp] = sv.O;
                                                sp++;
                                            }
                                            handlers = snap.Handlers;
                                            handlerBase = snap.HandlerBase;
                                            pendingFinally = snap.PendingFinally;
                                            pendingFinallyPayload = snap.PendingFinallyPayload;
                                            seq = snap.Seq;
                                            forEachStates = snap.ForEachStates;
                                            curNs = snap.NsContext;
                                            curLine = snap.CurLine;
                                            curCol = snap.CurCol;
                                            currentFunc = snap.CurrentFunc;
                                            curGenCo = co;
                                            Array.Clear(globalSlotState);
                                            continue;
                                        }
                                        // Suspended with neither a frame nor a snapshot: nothing to resume. For an
                                        // abandoned generator this is the remainder the capture above did not take
                                        // (a suspension with a live operand region), and for it the SILENT end stays
                                        // the deliberate choice: "nothing to resume" IS the completion, and it never
                                        // re-executes the abandoned body's remaining side effects. A loud error
                                        // would be a false-positive crash for a legitimate abandon-then-re-foreach.
                                        hasNext = false;
                                        goto genNoNext;
                                    }
                                }
                                if (genIdx >= 0)
                                {
                                    // ── Resume: swap into the generator's retained frame. Its
                                    // locals stay OUT of the pool (live while suspended); its
                                    // retained shared-stack region is reinstated via Sp.
                                    // NOTE: no VmMaxFrames check here, a resume removes the
                                    // retained frame and re-adds the driver (net-zero growth),
                                    // so it can never push the stack past the cap.
                                    // Mark the generator Dead while it is mid-drive: if its body
                                    // aborts with a VmFallback, this stays set, so a later tree
                                    // re-run can never double-execute its already-run side
                                    // effects. The YIELD swap-back restores Suspended on a real
                                    // suspension; RETURN/exception paths set Dead on completion.
                                    co.Status = CoroutineStatus.Dead;
                                    var genFrame = frames[genIdx];
                                    frames.RemoveAt(genIdx);
                                    var driveFrame = RentSavedFrame();
                                    driveFrame.Name = currentFunc;
                                    driveFrame.CallerName = currentFunc;
                                    driveFrame.Bc = bc;
                                    driveFrame.Code = code;
                                    driveFrame.Ip = ip;
                                    driveFrame.Consts = consts;
                                    driveFrame.Locals = locals;
                                    driveFrame.CurLine = curLine;
                                    driveFrame.CurCol = curCol;
                                    driveFrame.SpBase = spBase;
                                    driveFrame.Sp = sp;          // the driver's height, restored on the swap-back
                                    driveFrame.Handlers = handlers;
                                    driveFrame.HandlerBase = handlerBase;
                                    driveFrame.PendingFinally = pendingFinally;
                                    driveFrame.PendingFinallyPayload = pendingFinallyPayload;
                                    driveFrame.Seq = seq;
                                    driveFrame.ForEachStates = forEachStates;
                                    driveFrame.GlobalSlots = globalSlots;
                                    driveFrame.GlobalSlotState = globalSlotState;
                                    driveFrame.NsContext = curNs;
                                    driveFrame.GeneratorHandle = co;
                                    driveFrame.ForEachEndIp = ip + endOff;
                                    driveFrame.GenCo = curGenCo;
                                    driveFrame.GenHasNext = true; // folded generator-foreach drive active
                                    frames.Add(driveFrame);
                                    bc = genFrame.Bc; code = genFrame.Code; ip = genFrame.Ip;
                                    consts = genFrame.Consts;
                                    locals = genFrame.Locals;
                                    sp = genFrame.Sp;    // reinstate the retained stack region
                                    spBase = genFrame.SpBase;
                                    handlers = genFrame.Handlers; handlerBase = genFrame.HandlerBase;
                                    pendingFinally = genFrame.PendingFinally;
                                    pendingFinallyPayload = genFrame.PendingFinallyPayload;
                                    seq = genFrame.Seq;
                                    forEachStates = genFrame.ForEachStates;
                                    globalSlots = genFrame.GlobalSlots; globalSlotState = genFrame.GlobalSlotState;
                                    curNs = genFrame.NsContext;
                                    curLine = genFrame.CurLine; curCol = genFrame.CurCol;
                                    currentFunc = genFrame.CallerName.Length > 0 ? genFrame.CallerName : genFrame.Name;
                                    curGenCo = co;
                                    ReturnSavedFrame(genFrame);   // recycles the generator's snapshot
                                    Array.Clear(globalSlotState);
                                    continue;
                                }
                                // ── First visit: push the caller's frame and enter the generator
                                // fresh (mirrors the flat CALL frame setup, the callee rents a
                                // fresh locals array bound to its params, pooled at return).
                                // This is the only generator path that GROWS `frames`, the
                                // runaway guard lives here (resumes are net-zero).
                                if (frames.Count >= VmMaxFrames)
                                    throw new ScriptException(
                                        DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                // Mark the generator Dead while it is mid-drive, see the resume
                                // path above (guards a VmFallback escape against double-execution
                                // on a later tree re-run; the YIELD swap-back restores Suspended).
                                co.Status = CoroutineStatus.Dead;
                                // RENTED like the call frames: a drive frame is pushed per foreach (and, at the resume,
                                // per element), and the discarded one is handed back, so a generator loop reaches the
                                // pool's steady state instead of allocating one frame per step.
                                var foldedDriveFrame = RentSavedFrame();
                                foldedDriveFrame.Name = currentFunc;
                                foldedDriveFrame.CallerName = currentFunc;
                                foldedDriveFrame.Bc = bc;
                                foldedDriveFrame.Code = code;
                                foldedDriveFrame.Ip = ip;
                                foldedDriveFrame.Consts = consts;
                                foldedDriveFrame.Locals = locals;
                                foldedDriveFrame.CurLine = curLine;
                                foldedDriveFrame.CurCol = curCol;
                                foldedDriveFrame.SpBase = spBase;
                                foldedDriveFrame.Sp = sp;          // the driver's height, restored on the swap-back
                                foldedDriveFrame.Handlers = handlers;
                                foldedDriveFrame.HandlerBase = handlerBase;
                                foldedDriveFrame.PendingFinally = pendingFinally;
                                foldedDriveFrame.PendingFinallyPayload = pendingFinallyPayload;
                                foldedDriveFrame.Seq = seq;
                                foldedDriveFrame.ForEachStates = forEachStates;
                                foldedDriveFrame.GlobalSlots = globalSlots;
                                foldedDriveFrame.GlobalSlotState = globalSlotState;
                                foldedDriveFrame.NsContext = curNs;
                                foldedDriveFrame.GeneratorHandle = co;
                                foldedDriveFrame.ForEachEndIp = ip + endOff;
                                foldedDriveFrame.GenCo = curGenCo;
                                foldedDriveFrame.GenHasNext = true;   // folded generator-foreach drive active
                                frames.Add(foldedDriveFrame);
                                var genBc = co.Fn.Declaration.Compiled;
                                currentFunc = co.Fn.Declaration.Name;
                                bc = genBc;
                                code = genBc.Code;
                                consts = genBc.ConstsVm;
                                curNs = co.Fn.NamespaceContext;
                                // No ReturnLocals here, the frame above still holds the CALLER's
                                // array. The generator rents a fresh one, pooled at completion.
                                locals = RentLocals();
                                for (var ai = 0; ai < co.Args.Length && ai < locals.Length; ai++)
                                    locals[ai] = VmValue.From(co.Args[ai]);
                                ip = 0;
                                curLine = 0;
                                curCol = 0;
                                spBase = sp;
                                handlers = null;   // lazily allocated on the generator's first TRY
                                handlerBase = 0;
                                pendingFinally = false;
                                pendingFinallyPayload = null;
                                seq = 0;
                                forEachStates = null;
                                curGenCo = co;
                                Array.Clear(globalSlotState);
                                (globalSlots, globalSlotState) = RentSlots();
                                continue;
                            }

                        genNoNext:
                            ip += endOff;
                            break;
                        }
                    case Opcode.FOREACH_CALL:
                        {
                            // ── Task 4: `foreach (var x in <bare call to a statically-known coroutine>)`.
                            // The compiler emitted LOAD_CONST(calleeName); args...; FOREACH_CALL(argCount,
                            // stateLocal, varTypeConstIdx, endOff) as the LOOP HEAD (re-entered each
                            // iteration so a re-entered loop re-evaluates fresh). The state local holds the
                            // generator's own locals array as a lightweight identity: null → first visit
                            // (pop args, resolve, drive the callee's frame in-place, NO Coroutine/
                            // Generator wrappers allocated); non-null → resume the retained
                            // SuspendedGenerator frame; not found → the generator completed/was
                            // abandoned, so the loop is over (or re-drives fresh if re-entered).
                            var argCount = BytecodeReader.U8(code, ref ip);
                            var stateLocal = BytecodeReader.U8(code, ref ip);
                            BytecodeReader.SkipConst(code, ref ip); // varTypeConstIdx, retained for layout parity (generator elements
                                                                    // arrive via the YIELD swap-back, never coerced here, exactly like
                                                                    // the FOREACH_NEXT genDrive path)
                            var endOff = BytecodeReader.I16(code, ref ip);
                            var callArgs = RentArgs(argCount);
                            try
                            {
                                for (var i = argCount - 1; i >= 0; i--)
                                {
                                    var av = Pop();
                                    callArgs[i] = av.K == 4 ? av.O : av.ToObject();
                                }
                                var callee = Pop();
                                var identity = locals[stateLocal].O;
                                if (identity != null)
                                {
                                    // ── Resume: locate the generator's retained SuspendedGenerator frame
                                    // by identity and swap into it (mirrors the genDrive resume, no
                                    // VmMaxFrames check, a resume is net-zero frame growth). Not found:
                                    // the generator completed or its loop was abandoned, end the loop.
                                    var genIdx = -1;
                                    var n3 = frames.Count;
                                    if (n3 > 0 && frames[n3 - 1].GeneratorHandle == identity && frames[n3 - 1].SuspendedGenerator)
                                        genIdx = n3 - 1;
                                    else
                                        for (var fi = n3 - 1; fi >= 0; fi--)
                                            if (frames[fi].GeneratorHandle == identity && frames[fi].SuspendedGenerator)
                                            { genIdx = fi; break; }
                                    if (genIdx >= 0)
                                    {
                                        var genFrame = frames[genIdx];
                                        frames.RemoveAt(genIdx);
                                        // RENTED like the call frames: a drive frame is pushed per foreach (and, at the resume,
                                        // per element), and the discarded one is handed back, so a generator loop reaches the
                                        // pool's steady state instead of allocating one frame per step.
                                        var resumeDriveFrame = RentSavedFrame();
                                        resumeDriveFrame.Name = currentFunc;
                                        resumeDriveFrame.CallerName = currentFunc;
                                        resumeDriveFrame.Bc = bc;
                                        resumeDriveFrame.Code = code;
                                        resumeDriveFrame.Ip = ip;
                                        resumeDriveFrame.Consts = consts;
                                        resumeDriveFrame.Locals = locals;
                                        resumeDriveFrame.CurLine = curLine;
                                        resumeDriveFrame.CurCol = curCol;
                                        resumeDriveFrame.SpBase = spBase;
                                        resumeDriveFrame.Sp = sp;   // the driver's height, restored on the swap-back
                                        resumeDriveFrame.Handlers = handlers;
                                        resumeDriveFrame.HandlerBase = handlerBase;
                                        resumeDriveFrame.PendingFinally = pendingFinally;
                                        resumeDriveFrame.PendingFinallyPayload = pendingFinallyPayload;
                                        resumeDriveFrame.Seq = seq;
                                        resumeDriveFrame.ForEachStates = forEachStates;
                                        resumeDriveFrame.GlobalSlots = globalSlots;
                                        resumeDriveFrame.GlobalSlotState = globalSlotState;
                                        resumeDriveFrame.NsContext = curNs;
                                        resumeDriveFrame.GeneratorHandle = identity;
                                        resumeDriveFrame.ForEachEndIp = ip + endOff;
                                        resumeDriveFrame.GenCo = curGenCo;
                                        resumeDriveFrame.GenHasNext = true;
                                        frames.Add(resumeDriveFrame);
                                        bc = genFrame.Bc; code = genFrame.Code; ip = genFrame.Ip;
                                        consts = genFrame.Consts;
                                        locals = genFrame.Locals;
                                        sp = genFrame.Sp;    // reinstate the retained stack region
                                        spBase = genFrame.SpBase;
                                        handlers = genFrame.Handlers; handlerBase = genFrame.HandlerBase;
                                        pendingFinally = genFrame.PendingFinally;
                                        pendingFinallyPayload = genFrame.PendingFinallyPayload;
                                        seq = genFrame.Seq;
                                        forEachStates = genFrame.ForEachStates;
                                        globalSlots = genFrame.GlobalSlots; globalSlotState = genFrame.GlobalSlotState;
                                        curNs = genFrame.NsContext;
                                        curLine = genFrame.CurLine; curCol = genFrame.CurCol;
                                        currentFunc = genFrame.CallerName.Length > 0 ? genFrame.CallerName : genFrame.Name;
                                        // Its reads are done, recycle it: this arm runs per ELEMENT, so a frame
                                        // dropped here is one fresh allocation per step for the yield that made it.
                                        ReturnSavedFrame(genFrame);
                                        curGenCo = identity;
                                        Array.Clear(globalSlotState);
                                        continue;
                                    }
                                    ip += endOff;
                                    break;
                                }
                                // ── First visit: resolve the callee (a ScriptFunction for a coroutine,
                                // static resolution guaranteed it; anything else is a catchable internal
                                // error rather than a silent wrong-behavior) and drive it fresh.
                                if (callee is not { K: 3, O: string fcName })
                                    throw new ScriptException("FOREACH_CALL: expected a callee name (internal compiler/VM mismatch).");
                                var fcSf = interp.ResolveGlobalFunction(fcName);
                                var fcDecl = fcSf?.Declaration;
                                // Re-validate the whole FOREACH_CALL eligibility contract at runtime, not
                                // just the name: FOREACH_CALL binds args POSITIONALLY into the callee's
                                // locals with NO defaults/params/ref-out handling, so any callee whose
                                // signature violates that (or whose arity no longer matches the emitted
                                // args) would silently mis-bind. The compile-time gate guarantees this
                                // normally; a stale compile (reloaded bytecode, redefined global, mixed
                                // shell) must fail LOUDLY with a catchable ScriptException instead of
                                // binding defaults/params/ref cells incorrectly.
                                if (fcDecl == null || !interp.IsVmBackedCoroutine(fcDecl) || fcDecl.Compiled == null
                                    || fcDecl.DefaultValues != null
                                    || (fcDecl.IsParams.Count > 0 && fcDecl.IsParams[^1])
                                    || (fcDecl.IsRefParams is { Count: > 0 } && fcDecl.IsRefParams.Contains(true))
                                    || HasOutParam(fcDecl)
                                    || fcDecl.Params.Count != argCount)
                                    throw new ScriptException(
                                        $"generator '{fcName}' cannot be driven in-place by the bytecode VM foreach, " +
                                        "its signature (defaults/params/ref/out or arity mismatch) is not supported " +
                                        "by FOREACH_CALL (internal compiler/VM mismatch; the compile-time " +
                                        "FOREACH_CALL resolution was stale).");
                                if (frames.Count >= VmMaxFrames)
                                    throw new ScriptException(
                                        DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                // Push the driver frame (the foreach owner's state) and enter the callee
                                // fresh, mirrors the FOREACH_NEXT genDrive first-visit. The callee's
                                // rented locals array doubles as the resume identity stored in the state
                                // local (unique per invocation, live while suspended, no wrapper objects).
                                // NOTE: bc/code/consts/locals are the CALLER's here, the driver frame
                                // captures them, and RentLocals() sizes the callee's array by the CURRENT
                                // bc, so the callee's bytecode must be switched in FIRST.
                                var fcBc = fcDecl.Compiled;
                                var callerBc = bc;
                                var callerCode = code;
                                var callerConsts = consts;
                                var callerLocals = locals;
                                bc = fcBc;
                                code = fcBc.Code;
                                consts = fcBc.ConstsVm;
                                var fcLocals = RentLocals();
                                callerLocals[stateLocal] = VmValue.From(fcLocals);
                                interp.VmInvocations++;   // VmStats, a FOREACH_CALL drive is an invocation
                                // RENTED like the call frames: a drive frame is pushed per foreach (and, at the resume,
                                // per element), and the discarded one is handed back, so a generator loop reaches the
                                // pool's steady state instead of allocating one frame per step.
                                var callDriveFrame = RentSavedFrame();
                                callDriveFrame.Name = currentFunc;
                                callDriveFrame.CallerName = currentFunc;
                                callDriveFrame.Bc = callerBc;
                                callDriveFrame.Code = callerCode;
                                callDriveFrame.Ip = ip;
                                callDriveFrame.Consts = callerConsts;
                                callDriveFrame.Locals = callerLocals;
                                callDriveFrame.CurLine = curLine;
                                callDriveFrame.CurCol = curCol;
                                callDriveFrame.SpBase = spBase;
                                callDriveFrame.Sp = sp;          // the driver's height, restored on the swap-back
                                callDriveFrame.Handlers = handlers;
                                callDriveFrame.HandlerBase = handlerBase;
                                callDriveFrame.PendingFinally = pendingFinally;
                                callDriveFrame.PendingFinallyPayload = pendingFinallyPayload;
                                callDriveFrame.Seq = seq;
                                callDriveFrame.ForEachStates = forEachStates;
                                callDriveFrame.GlobalSlots = globalSlots;
                                callDriveFrame.GlobalSlotState = globalSlotState;
                                callDriveFrame.NsContext = curNs;
                                callDriveFrame.GeneratorHandle = fcLocals;   // lightweight identity, not a Coroutine
                                callDriveFrame.ForEachEndIp = ip + endOff;
                                callDriveFrame.GenCo = curGenCo;
                                callDriveFrame.GenHasNext = true;   // folded generator-foreach drive active
                                frames.Add(callDriveFrame);
                                currentFunc = fcDecl.Name;
                                curNs = fcSf!.NamespaceContext;
                                locals = fcLocals;
                                for (var ai = 0; ai < callArgs.Length && ai < locals.Length; ai++)
                                    locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(fcDecl, callArgs[ai], ai, curLine, curCol));
                                ip = 0;
                                curLine = 0;
                                curCol = 0;
                                spBase = sp;
                                handlers = null;   // lazily allocated on the callee's first TRY
                                handlerBase = 0;
                                pendingFinally = false;
                                pendingFinallyPayload = null;
                                seq = 0;
                                forEachStates = null;
                                curGenCo = fcLocals;
                                Array.Clear(globalSlotState);
                                (globalSlots, globalSlotState) = RentSlots();
                                continue;
                            }
                            finally { ReturnArgs(callArgs); }
                        }
                    case Opcode.FOREACH_END:
                        {
                            var stateLocal = BytecodeReader.U8(code, ref ip);
                            // ── Abandonment happens when the LOOP ends, not only at a RETURN ──
                            // A loop that ends EARLY (break/return/throw) leaves its driven generator's retained
                            // frame on top of `frames`, and the cleanup used to run only at a RETURN. A source
                            // run's top level is a SEQUENCE of statements, so the loop statement's own RETURN ran
                            // it immediately; a bundle's ENTRY is ONE compiled body, so nothing ran until the whole
                            // program ended, and a script reading `co.Status` or calling `co.Resume()` right after
                            // the loop saw the mid-drive `Suspended` where the tree answers `Waiting`. Dropping it
                            // HERE is the moment the loop is over, and it goes through the same helper, so the
                            // status rule (generator versus user-started) and the suspension capture stay in ONE
                            // place. The FILTER is what keeps it safe: only the frame of the coroutine THIS loop
                            // drove is dropped, never a suspended frame an enclosing loop still needs.
                            var loopCo = locals[stateLocal].O switch
                            {
                                VmForEachState { Generator: Coroutine genCo } => genCo,
                                Coroutine bareCo => bareCo,   // the folded drive stores the Coroutine itself
                                _ => null
                            };
                            if (loopCo != null) DropAbandonedGeneratorFrames(loopCo);
                            if (forEachStates != null)
                                for (var i = forEachStates.Count - 1; i >= 0; i--)
                                    if (forEachStates[i].stateLocal == stateLocal)
                                    {
                                        forEachStates.RemoveAt(i);
                                        break;
                                    }

                            if (locals[stateLocal].O is VmForEachState { Enumerator: IDisposable disp } st2)
                            {
                                disp.Dispose();
                                st2.Enumerator = null; // double-dispose safety for the exception-path scan
                            }
                            // Task 4: a FOREACH_CALL state local holds the generator's identity, clear it
                            // so a re-entered loop (the same foreach re-executed by an enclosing loop)
                            // sees null and re-drives the callee fresh instead of resuming stale state.
                            // Harmless for every other path (the state is dead past FOREACH_END).
                            locals[stateLocal] = new VmValue();

                            break;
                        }
                    case Opcode.INC:
                        {
                            int vi = BytecodeReader.U8(code, ref ip);
                            var nv = new VmValue(locals[vi].L + 1);
                            locals[vi] = nv;
                            Push(nv);
                            break;
                        }
                    case Opcode.DEC:
                        {
                            int vi = BytecodeReader.U8(code, ref ip);
                            var nv = new VmValue(locals[vi].L - 1);
                            locals[vi] = nv;
                            Push(nv);
                            break;
                        }
                    case Opcode.NOT:
                        {
                            var a = Pop();
                            Push(new VmValue(!IsTruthyV(a)));
                            break;
                        }
                    case Opcode.IS_TYPE:
                        {
                            var typeInfo = Pop().O as TypeInfo;
                            var value = Pop().ToObject();
                            Push(new VmValue(typeInfo != null && interp.IsTypeCheck(value, typeInfo, curLine, curCol)));
                            break;
                        }
                    case Opcode.MAKE_LAMBDA:
                        {
                            var li = BytecodeReader.U8(code, ref ip);
                            var cc = BytecodeReader.U8(code, ref ip);
                            var decl = bc.NestedLambdas?[li];
                            if (decl == null || decl.Compiled == null)
                            {
                                return Bail();
                            }

                            object?[]? captures = null;
                            if (cc > 0)
                            {
                                captures = new object?[cc];
                                for (var i = 0; i < cc; i++)
                                {
                                    var slot = BytecodeReader.U8(code, ref ip);
                                    var written = BytecodeReader.U8(code, ref ip) != 0;
                                    var cv = locals[slot].ToObject();
                                    if (written)
                                    {
                                        // Share a RefCell with the enclosing frame, the lambda's writes
                                        // (STORE_VAR write-through) propagate to the captured variable.
                                        object? held = cv;
                                        var cell = held as RefCell;
                                        if (cell == null)
                                        {
                                            cell = new RefCell(() => held, v => held = v);
                                            locals[slot] = VmValue.From(cell);
                                        }

                                        captures[i] = cell;
                                    }
                                    else if (bc.ConstLocals != null && slot < bc.ConstLocals.Length &&
                                             bc.ConstLocals[slot])
                                    {
                                        // Const captured local, stays a value copy (an immutable const
                                        // cannot be reassigned, so no binding is needed; snapshot semantics).
                                        captures[i] = cv is RefCell rc ? rc.Get() : cv;
                                    }
                                    else
                                    {
                                        // Read-only capture: SHARE a RefCell with the caller's slot (binding
                                        // semantics), a later assignment in the enclosing body (STORE_VAR
                                        // write-through) is visible to the closure. If the slot already holds
                                        // a RefCell (loop var / prior capture / ref param), reuse that cell.
                                        object? held = cv;
                                        var cell = held as RefCell;
                                        if (cell == null)
                                        {
                                            cell = new RefCell(() => held, v => held = v);
                                            locals[slot] = VmValue.From(cell);
                                        }

                                        captures[i] = cell;
                                    }
                                }
                            }

                            var sf = interp.CreateLambdaFunction(decl);
                            sf.VmCaptures = captures;
                            Push(VmValue.From(sf));
                            break;
                        }
                    case Opcode.MAKE_LIST:
                        {
                            var typeInfo = Pop().O as TypeInfo;
                            var cnt = BytecodeReader.U8(code, ref ip);
                            var vals = new object?[cnt];
                            for (var i = cnt - 1; i >= 0; i--) vals[i] = Pop().ToObject();
                            // ONE implementation with the tree's list literal: an empty literal is an
                            // object[] and a non-empty one is built by the SHARED array factory, which
                            // infers the element type from the values when the TypeInfo has a NAME (a
                            // dict, a class, a delegate) and falls back to the declared type otherwise.
                            // Building it here instead lost that inference: measured, `var da = [{}]`
                            // answered object[] from a compiled body where the tree answers
                            // Dictionary<object, object>[] (crash_dict 7a).
                            //
                            // A REFUSAL from the factory PROPAGATES. This arm used to wrap the call in a
                            // `catch (Exception)` that pushed the raw values as an object[] "on type
                            // mismatch", which made a compiled body ACCEPT what the tree refuses and hand
                            // back a different container: measured, `var v = [1, null, 3];` (no best type)
                            // and `int[] bad = [1, null, 3];` (null does not convert to int) both came back
                            // as an object[] holding the null, silently, where the tree raises "Cannot
                            // assign null to non-nullable type". A wrong value with no error is the one
                            // outcome worth refusing, and the refusal is the engine's own message, which a
                            // script's `try` can catch like any other.
                            if (cnt == 0)
                            {
                                Push(new VmValue(Array.Empty<object>()));
                                break;
                            }
                            var elemType = typeInfo ?? new TypeInfo(TokenType.Object, false, 0, 0, null);
                            var built = interp.CreateArrayValue(elemType, null, vals, curLine, curCol);
                            Push(new VmValue(built ?? (object)vals));

                            break;
                        }
                    case Opcode.MAKE_TUPLE:
                        {
                            var cnt = BytecodeReader.U8(code, ref ip);
                            var items = new object?[cnt];
                            for (var i = cnt - 1; i >= 0; i--) items[i] = Pop().ToObject();
                            Push(new VmValue(new ScriptTuple(items)));
                            break;
                        }
                    case Opcode.ATTACH_TUPLE_NAMES:
                        {
                            // Tuple-typed var decl: attach the declared element names to the
                            // ScriptTuple on the stack top (MAKE_TUPLE builds it nameless; the tree
                            // attaches TupleElementNames in VisitVarDeclStmt). Non-tuple top (a
                            // mismatched initializer) is left untouched, the tree errors via
                            // CoerceTo, so a silent skip here is no worse than the VM's usual
                            // best-effort typing.
                            var namesIdx = BytecodeReader.Const(code, ref ip);
                            if (consts[namesIdx].O is IReadOnlyList<string> names)
                            {
                                _ = Pop();   // the names const pushed by LOAD_CONST (stack: [tuple, names])
                                if (Peek().ToObject() is ScriptTuple tupN)
                                    tupN.Names = names;
                            }
                            break;
                        }
                    case Opcode.MAKE_DICT:
                        {
                            var cnt = BytecodeReader.U8(code, ref ip);
                            // The pairs are pushed in SOURCE order, so popping them one at a time INSERTS them
                            // reversed. A Dictionary keeps insertion order (for a fresh one with no removals),
                            // and the tree's `{a: 1, b: 2}` literal is built in source order, so a compiled
                            // body's `d.Keys[0]` used to answer "b" where the tree answered "a" (measured,
                            // crash_dict 11c/11e). Collect first, then insert in source order.
                            var keys = new object?[cnt];
                            var vals = new object?[cnt];
                            for (var i = cnt - 1; i >= 0; i--) { vals[i] = Pop().ToObject(); keys[i] = Pop().ToObject(); }
                            var dict = new Dictionary<object, object?>();
                            for (var i = 0; i < cnt; i++)
                                if (keys[i] != null) dict[keys[i]!] = vals[i];

                            Push(new VmValue(dict));
                            break;
                        }
                    case Opcode.GET_INDEX:
                        {
                            var idxCount = BytecodeReader.U8(code, ref ip);
                            if (idxCount == 1)
                            {
                                var idxObj = Pop().ToObject();
                                var target = Pop().ToObject();
                                if (target is object[] oa)
                                {
                                    var idx = idxObj is Index ix ? ix.GetOffset(oa.Length) : Convert.ToInt32(idxObj);
                                    Push(idx >= 0 && idx < oa.Length
                                        ? VmValue.From(oa[idx])
                                        : VmValue.From(interp.GetArrayElementValues(target, [idxObj], curLine, curCol)));
                                    break;
                                }

                                if (target is Array arr)
                                {
                                    var len = arr.GetLength(0);
                                    var idx = idxObj is Index ix2 ? ix2.GetOffset(len) : Convert.ToInt32(idxObj);
                                    Push(idx >= 0 && idx < len
                                        ? VmValue.From(arr.GetValue(idx))
                                        : VmValue.From(interp.GetArrayElementValues(target, [idxObj], curLine, curCol)));
                                    break;
                                }

                                // Non-array target (string / list / dictionary), generic dispatch
                                Push(VmValue.From(interp.GetArrayElementValues(target, [idxObj], curLine, curCol)));
                                break;
                            }

                            var idxVals = RentArgs(idxCount);
                            try
                            {
                                for (var i = idxCount - 1; i >= 0; i--) idxVals[i] = Pop().ToObject();
                                var target2 = Pop().ToObject();
                                Push(VmValue.From(interp.GetArrayElementValues(target2, idxVals, curLine, curCol)));
                            }
                            finally { ReturnArgs(idxVals); }
                            break;
                        }
                    case Opcode.ARRAY_GET:
                        {
                            // Fast single-index read: CLR array + unboxed numeric index → push the
                            // element WITHOUT boxing the index (K==1 reads L directly). Typed arrays
                            // (int[]/long[]/double[]) keep elements unboxed too (VmValue K=1/2); the
                            // object[] path pushes the pre-boxed element as-is. Everything else falls
                            // back to the general indexed-access machinery (strings, lists, dicts,
                            // multi-dim arrays, Index-from-end, out-of-range → proper runtime error).
                            var idx = Pop();
                            var target = Pop();
                            if (idx.K == 1)
                            {
                                var i = (int)idx.L;
                                if (target.K == 3 && target.O is int[] ia)
                                {
                                    if ((uint)i < (uint)ia.Length) { Push(new VmValue(ia[i], 1)); break; }
                                }
                                else if (target.K == 3 && target.O is long[] la)
                                {
                                    if ((uint)i < (uint)la.Length) { Push(new VmValue(la[i], 0)); break; }
                                }
                                else if (target.K == 3 && target.O is double[] da)
                                {
                                    if ((uint)i < (uint)da.Length) { Push(new VmValue(da[i])); break; }
                                }
                                else if (target.K == 3 && target.O is object?[] oa)
                                {
                                    if ((uint)i < (uint)oa.Length) { Push(VmValue.From(oa[i])); break; }
                                }
                            }
                            Push(VmValue.From(interp.GetArrayElementValues(target.ToObject(), [idx.ToObject()], curLine, curCol)));
                            break;
                        }

                    case Opcode.SET_INDEX:
                        {
                            var idxCount = BytecodeReader.U8(code, ref ip);
                            var value = Pop().ToObject();
                            if (idxCount == 1)
                            {
                                var idxObj = Pop().ToObject();
                                var target = Pop().ToObject();
                                if (target is object?[] oa)
                                {
                                    var idx = idxObj is Index ix ? ix.GetOffset(oa.Length) : Convert.ToInt32(idxObj);
                                    if (idx >= 0 && idx < oa.Length)
                                    {
                                        // A TYPED array is assignable to object?[] (array COVARIANCE), so this arm also
                                        // catches `string[]`, `int[]` and their like, and writing a value the element
                                        // type cannot hold through that covariant view threw the CLR's own
                                        // InvalidCastException straight out of the engine, where a script could not
                                        // catch it: measured, `var s = ["x", "y"]; s[0] = 42;` ended the run with
                                        // "Attempted to access an element as a type incompatible with the array". A
                                        // mismatched write goes to the tree core, which coerces through CoerceTo (42
                                        // becomes "42" for a string[]) or refuses catchably, exactly as the Array arm
                                        // below already does for the same reason.
                                        var covElem = oa.GetType().GetElementType();
                                        if (covElem != null && covElem != typeof(object) && value != null
                                            && !covElem.IsInstanceOfType(value))
                                        {
                                            interp.SetArrayElementValues(target, [idxObj], value, curLine, curCol);
                                            Push(VmValue.From(value));
                                            break;
                                        }
                                        // A struct stored in an array element is BOXED in C#, which COPIES it, so the
                                        // element must not share the variable's instance: measured, `arr2[0] = a;
                                        // arr2[0].X = 99;` answered `a.X=99` on the path (the tree was already right
                                        // because its array assign coerces). A write THROUGH an element afterwards
                                        // then mutates the element's own copy, which is what C# does for `arr[i].X`.
                                        if (value is StructInstance siElem)
                                            value = Interpreter.CloneStructForStore(siElem);
                                        oa[idx] = value;
                                        Push(VmValue.From(value));
                                        break;
                                    }

                                    // Out of range, let the generic path raise the proper error
                                    interp.SetArrayElementValues(target, [idxObj], value, curLine, curCol);
                                    Push(VmValue.From(value));
                                    break;
                                }

                                if (target is Array arr)
                                {
                                    var len = arr.GetLength(0);
                                    var idx = idxObj is Index ix2 ? ix2.GetOffset(len) : Convert.ToInt32(idxObj);
                                    if (idx >= 0 && idx < len)
                                    {
                                        var elemType = arr.GetType().GetElementType();
                                        if (elemType != null && value != null && !elemType.IsInstanceOfType(value))
                                        {
                                            // Value type differs (e.g. Int64 into Int32[]), let the tree
                                            // dispatcher coerce, same semantics as the tree-walker path.
                                            interp.SetArrayElementValues(target, [idxObj], value, curLine, curCol);
                                            Push(VmValue.From(value));
                                            break;
                                        }

                                        arr.SetValue(value, idx);
                                        Push(VmValue.From(value));
                                        break;
                                    }

                                    interp.SetArrayElementValues(target, [idxObj], value, curLine, curCol);
                                    Push(VmValue.From(value));
                                    break;
                                }

                                // Non-array target (list / dictionary / string), generic dispatch
                                interp.SetArrayElementValues(target, [idxObj], value, curLine, curCol);
                                Push(VmValue.From(value));
                                break;
                            }

                            var idxVals = RentArgs(idxCount);
                            try
                            {
                                for (var i = idxCount - 1; i >= 0; i--) idxVals[i] = Pop().ToObject();
                                var target2 = Pop().ToObject();
                                interp.SetArrayElementValues(target2, idxVals, value, curLine, curCol);
                            }
                            finally { ReturnArgs(idxVals); }
                            Push(VmValue.From(value));
                            break;
                        }
                    case Opcode.SLICE:
                        {
                            var toVal = Pop().ToObject();
                            var fromVal = Pop().ToObject();
                            var target = Pop().ToObject();
                            Push(VmValue.From(interp.SliceValues(target, fromVal, toVal, curLine, curCol)));
                            break;
                        }
                    case Opcode.PAT_LIST:
                        {
                            // List/tuple pattern arm: the ARM stays on the VM (no tree fallback for the
                            // construct) while the positional matching runs through the tree matcher,
                            // so VM and tree cannot diverge. Operand = descriptor const index.
                            var ped = consts[BytecodeReader.Const(code, ref ip)].O as PatternEngineDescriptor;
                            var pedSubject = Pop().ToObject();
                            if (ped == null) return Bail();
                            var pedMatched = interp.PatternMatches(ped.Pattern, pedSubject, out var pedBinds);
                            if (pedMatched)
                                foreach (var (bName, bValue) in pedBinds)
                                    for (var bi = 0; bi < ped.Names.Length; bi++)
                                        if (ped.Names[bi] == bName)
                                        {
                                            locals[ped.LocalIdxs[bi]] = VmValue.From(bValue);
                                            break;
                                        }
                            Push(new VmValue(pedMatched));
                            break;
                        }
                    case Opcode.TRY_GET_PROP:
                        {
                            // Property-pattern member read: pushes the member value and a found flag
                            // (found on top) so a missing member is a NON-match, never an error.
                            var tryName = Pop().O as string ?? "";
                            var tryTarget = Pop().ToObject();
                            var foundMember = interp.TryResolvePatternMember(tryTarget, tryName, out var memberValue);
                            Push(VmValue.From(memberValue));
                            Push(new VmValue(foundMember));
                            break;
                        }
                    case Opcode.GET_PROP:
                        {
                            var name = Pop().O as string;
                            var target = Pop().ToObject();
                            // A member that is an EVENT of a script class is read through the SAME accessor the
                            // tree's own compound assign uses (GetEventInfo): that accessor is what makes
                            // `x.E += h` work from anywhere, while the property arm's CheckAccess used to refuse a
                            // private event even though that path ignored the check. Reading an event is not
                            // reading a value, so both engines answer the same EventInfo and the ADD/SUB below is
                            // what subscribes. Measured through tools/probe-toplevel-vm.slc section 8.
                            if (target is ClassInstance ciEvt && name != null && interp.IsClassEvent(ciEvt, name))
                            {
                                Push(VmValue.From(interp.ReadClassEvent(ciEvt, name, curLine, curCol)));
                                break;
                            }
                            // Fast path: plain .NET object property, direct cached getter, no temp-scope wrapper.
                            // The stamp is sampled because a GETTER can run script code (`Task.Result` waits and pumps
                            // continuations, which write globals): when this invocation's globals moved, the cache goes.
                            var propStampBefore = Interpreter.GlobalWriteStamp;
                            if (target != null && name != null
                                               && target is not ClassInstance and not StructInstance and not Type
                                                   and not TypeInfo
                                                   and not ClassInfo and not Namespace and not BaseProxy
                                               && interp.TryGetCachedProperty(target, name, out var pv, curLine, curCol))
                            {
                                if (Interpreter.GlobalWriteStamp != propStampBefore) Array.Clear(globalSlotState);
                                Push(VmValue.From(pv));
                                break;
                            }

                            // Value-level struct field read, no synthetic AST nodes / temp scope
                            // (the generic fallback allocated ~4.5 KB per access).
                            if (target is StructInstance si)
                            {
                                Push(VmValue.From(interp.GetStructMemberValue(si, name ?? "", curLine, curCol)));
                                break;
                            }

                            // Value-level class field/property/event read, mirrors the tree's
                            // ClassInstance member semantics (access checks, events, static fallback);
                            // no synthetic AST nodes / temp scope (~1.1 KB per access saved).
                            if (target is ClassInstance ci)
                            {
                                Push(VmValue.From(interp.GetClassMemberValue(ci, name ?? "", curLine, curCol)));
                                break;
                            }

                            Push(VmValue.From(interp.GetMemberValue(target, name ?? "", curLine, curCol)));
                            // Script property getters can mutate globals, flush the slot cache.
                            Array.Clear(globalSlotState);
                            break;
                        }
                    case Opcode.SET_PROP:
                        {
                            var value = Pop().ToObject();
                            var name = Pop().O as string;
                            var target = Pop().ToObject();
                            // The SET_PROP that closes a compiled `x.E += h` writes back the EventInfo the
                            // GET_PROP read, and the ADD already subscribed it: an event is not a stored value, so
                            // nothing is written (the tree's arm returns without writing at all). SET_PROP's
                            // contract is "pop value, name, target, then PUSH the stored value", so the value
                            // goes back on the stack or the statement's own POP reads below the frame base
                            // (measured: IndexOutOfRangeException inside Push). Nothing observable happened, so
                            // the body is not marked committed either.
                            if (value is Interpreter.EventInfo)
                            {
                                Push(VmValue.From(value));
                                break;
                            }
                            interp.VmBodyCommitted = true;   // observable work: a later bail must not be handed to the tree
                            // Value-level struct field write, no synthetic AST nodes / temp scope
                            // (the generic fallback allocated ~7.2 KB per access).
                            if (target is StructInstance si)
                            {
                                Push(VmValue.From(interp.SetStructMemberValue(si, name ?? "", value, curLine, curCol)));
                                Array.Clear(globalSlotState);
                                break;
                            }
                            // Value-level class field/property write, mirrors the tree's ClassInstance
                            // member semantics (setter access, init guards, static fallback).
                            if (target is ClassInstance ci)
                            {
                                Push(VmValue.From(interp.SetClassMemberValue(ci, name ?? "", value, curLine, curCol)));
                                Array.Clear(globalSlotState);
                                break;
                            }
                            Push(VmValue.From(interp.SetMemberValue(target, name ?? "", value, curLine, curCol)));
                            // Script property setters can mutate globals, flush the slot cache.
                            Array.Clear(globalSlotState);
                            break;
                        }
                    case Opcode.CALL_METHOD:
                        {
                            var argCount = BytecodeReader.U8(code, ref ip);
                            var callArgs = RentArgs(argCount);
                            try
                            {
                                var name = Pop().O as string;
                                // A REF/OUT argument arrives in ONE of the two shapes the compiler emits (see
                                // Ctx.CompileArgument): a slot reference (LOAD_REF names a local slot) or an
                                // already-built CELL (a global target, through the engine's __ref_global__ helper).
                                // The callee needs a RefCell either way, exactly as the CALL arm states it
                                // ("every ref position holds a cell over OUR slot"), and the paths that carry one
                                // are the interpreter's dispatch and its plain invoke: the FLAT class-method path
                                // declines a ref/out declaration outright (see its gate below). Without this the
                                // argument travelled as a plain value and the write went nowhere, which is why the
                                // compiler used to decline the whole body.
                                // The map is allocated ONLY when a ref is present: this arm is the hot path of every
                                // method call, and an unconditional array per call would show up in the allocation
                                // benches. An argument that is ALREADY a cell (a global target) needs no wrapping.
                                int[]? refIdx = null;
                                var hasCellRef = false;
                                for (var i = argCount - 1; i >= 0; i--)
                                {
                                    var av = Pop();
                                    if (av.K == 4)
                                    {
                                        if (refIdx == null)
                                        {
                                            refIdx = new int[argCount];
                                            for (var j = 0; j < argCount; j++) refIdx[j] = -1;
                                        }
                                        refIdx[i] = (int)av.L;
                                        callArgs[i] = av.O;
                                        continue;
                                    }
                                    callArgs[i] = av.ToObject();
                                    if (callArgs[i] is RefCell) hasCellRef = true;
                                }
                                var receiver = Pop().ToObject();
                                // A method call whose result is used as a REFERENCE (`box.F() = 7;`, `ref int r = ref
                                // box.F();`) carries the same marker the free-function shape does, pushed directly under
                                // the RECEIVER so it is the stack top once the callee's name, arguments and receiver are
                                // popped. Such a call is never inlined below (the flat method swap dereferences a
                                // ref-returning callee's cell) and the dispatch hands the CELL back, which is what
                                // `InvokeMethodDispatch` already does for a ref return (MethodReturn keeps it).
                                var methodWantsRef = false;
                                if (sp > 0 && sO[sp - 1] is RefResultMarker)
                                {
                                    sp--;
                                    methodWantsRef = true;
                                }
                                // The CALL SITE's keyword (see Ctx.EmitArgFlags), which the bytecode otherwise loses
                                // for a local target: the bind judges a swapped `ref`/`out` by what a cell knows
                                // about how it was built, and a method call cannot be declined at compile time for a
                                // mismatch the way a bare call can (its declaration is unknown there). It sits
                                // directly under the receiver, because a named or generic call with a ref/out
                                // argument declines at compile time.
                                ArgFlagsMarker? argFlags = null;
                                if (sp > 0 && sO[sp - 1] is ArgFlagsMarker af)
                                {
                                    sp--;
                                    argFlags = af;
                                }
                                // An OUT position ALSO takes the tracking cell the CALL arm builds (same
                                // ValidateOutAssignmentFromVm, same "out parameter ... was not assigned" refusal on
                                // RETURN), which needs the callee's declaration: a script class method has one, a CLR
                                // or provider method has none to track. An argument that already arrived as a CELL (a
                                // GLOBAL target) is a ref/out position by construction, so the declaration is only
                                // consulted to learn whether it is an OUT one.
                                bool[]? outTrack = null;
                                if (refIdx != null || hasCellRef)
                                {
                                    FuncDecl? mDecl = null;
                                    if (receiver is ClassInstance wrapCi && name != null)
                                        interp.TryGetScriptMethod(wrapCi.Class, name, callArgs, out mDecl);
                                    for (var i = 0; i < argCount; i++)
                                    {
                                        var fromSlot = refIdx != null && refIdx[i] >= 0;
                                        var declaredRefOrOut = mDecl != null && i < mDecl.Params.Count
                                            && ((i < mDecl.IsRefParams.Count && mDecl.IsRefParams[i])
                                                || (mDecl.IsOutParams != null && i < mDecl.IsOutParams.Count && mDecl.IsOutParams[i]));
                                        if (!fromSlot && !declaredRefOrOut) continue;
                                        RefCell cell;
                                        var callByOut = argFlags == null ? (bool?)null : argFlags.Outs[i];
                                        if (fromSlot)
                                        {
                                            var refSlot = refIdx![i];
                                            object? held = locals[refSlot].ToObject();
                                            var existing = held as RefCell;
                                            if (existing == null)
                                            {
                                                existing = new RefCell(() => held, v => held = v, callByOut);
                                                locals[refSlot] = VmValue.From(existing); // shared: the callee writes through to our slot
                                            }
                                            cell = existing;
                                        }
                                        else
                                        {
                                            if (callArgs[i] is not RefCell passedArg)
                                            {
                                                // A DECLARED ref/out position whose argument is a PLAIN value: the call
                                                // did not write `ref`/`out` there. Bail to the tree, which reports it
                                                // by name, exactly as the CALL arm's canWrap does. A compiled body
                                                // would otherwise bind the value and read it like a cell, silently
                                                // (measured: a METHOD's declared ref obeys the same rule, vm_params 8k).
                                                return Bail();
                                            }
                                            // A cell the engine already built (a CAPTURE cell, or a global through
                                            // __ref_global__) does not know which keyword the call wrote, and the
                                            // bind judges a swap by exactly that: wrap it in a thin cell that does,
                                            // so the callee still writes through to the original.
                                            cell = callByOut == null
                                                ? passedArg
                                                : new RefCell(passedArg.Getter, passedArg.Setter, callByOut);
                                        }
                                        if (mDecl != null && mDecl.IsOutParams != null
                                            && i < mDecl.IsOutParams.Count && mDecl.IsOutParams[i])
                                        {
                                            outTrack ??= new bool[mDecl.Params.Count];
                                            var outFlag = i;
                                            var outType = mDecl.Params[i].Type;
                                            var outName = mDecl.Params[i].Name;
                                            var innerCell = cell;
                                            cell = new RefCell(innerCell.Getter, v =>
                                            {
                                                var coerced = interp.ValidateOutAssignmentFromVm(v, outType, outName, curLine, curCol);
                                                outTrack[outFlag] = true;
                                                innerCell.Set(coerced);
                                            });
                                        }
                                        callArgs[i] = cell;
                                    }
                                }
                                // Named-arg method call: the compiler pushes a NamedArgsMarker BELOW the
                                // receiver ([marker, receiver, args…, name]), so after name+args+receiver
                                // are popped it is the new stack top. Reorder against the resolved
                                // method's declared params (mirrors the tree's ReorderNamedArgs). Only
                                // script methods have known params, plain .NET calls with a marker fall
                                // back to the tree (can't reorder reflection params safely here).
                                if (sp > 0 && sO[sp - 1] is NamedArgsMarker mMarker)
                                {
                                    Interpreter.NamedTrace("CALL_METHOD",
                                        $"member={name} receiver={receiver?.GetType().Name ?? "null"} names={Interpreter.NamedTraceNames(mMarker.Names)}");
                                    if (receiver is ClassInstance mCi
                                        && interp.TryGetScriptMethod(mCi.Class, name ?? "", callArgs, out var mDecl))
                                    {
                                        sp--;
                                        callArgs = interp.ReorderNamedArgs(callArgs, mMarker.Names, mDecl.Params, curLine, curCol);
                                    }
                                    // A receiver that names its own members (a COM node, whose provider resolves
                                    // PARAMETER names through the server's type library) takes the same ONE call the
                                    // TREE makes for it, with the marker's names. Bailing here instead is what made a
                                    // bytecode-only body fail: a source-less bundle carries a class or extension
                                    // method's body as bytecode only, so there is no tree to fall back to, and the
                                    // sample's `Save` (a named `SaveAs`) could not run at all from a stripped bundle.
                                    // The marker's names travel with the constants, so a loaded body has them too.
                                    else if (receiver is IDynamicPropertyProvider dynNamed
                                             && dynNamed.TryInvokeNamedMethod(name ?? "", callArgs, mMarker.Names, out var namedResult))
                                    {
                                        sp--;
                                        Push(VmValue.From(namedResult));
                                        Array.Clear(globalSlotState);
                                        break;
                                    }
                                    // Everything else (a plain .NET receiver: string, a collection, a Type) goes
                                    // to the interpreter's ONE dispatch WITH the marker's names, which is where
                                    // the CLR arm reorders them against the reflected parameter names. This used to
                                    // bail to the tree, which a bytecode-only body does not have: measured, a named
                                    // call on a string receiver aborted a stripped bundle's program body with
                                    // "Function '__program__' was loaded from bytecode for a different environment".
                                    else
                                    {
                                        sp--;
                                        Push(VmValue.From(interp.InvokeMethodValues(
                                            receiver, name ?? "", callArgs, curLine, curCol, null, mMarker.Names)));
                                        Array.Clear(globalSlotState);
                                        break;
                                    }
                                }
                                // A call that NAMES TYPE ARGUMENTS: the compiler pushes a TypeArgsMarker below the
                                // receiver too, so it is the stack top once name+args+receiver are popped (and, for
                                // a call that has both, it sits UNDER the NamedArgsMarker just consumed above).
                                // Hand the arguments to the interpreter's dispatch, the ONE place that closes a
                                // generic definition with the script's own types, and skip the cached fast invoke
                                // below, whose key does not carry them: a hit there would run the method resolved
                                // for a call WITHOUT them.
                                IReadOnlyList<TypeInfo>? callTypeArgs = null;
                                if (sp > 0 && sO[sp - 1] is TypeArgsMarker tMarker)
                                {
                                    sp--;
                                    callTypeArgs = tMarker.TypeArgs;
                                }
                                // Direct cached invoke for plain .NET objects, skips node synthesis + dispatch switch.
                                // THE STAMP IS SAMPLED HERE because this is the one CLR dispatch that does not clear
                                // the cache afterwards (the two arms below clear unconditionally, for the script
                                // methods they can reach): a CLR member can run SCRIPT code, a subscriber or a
                                // comparer or a `ForEach` body, and the global it writes would otherwise be read back
                                // through this invocation's cache. Measured before it existed: a `--no-source` bundle
                                // read "none" where the source leg read the value its own subscriber had just written.
                                var cachedCallStamp = Interpreter.GlobalWriteStamp;
                                if (callTypeArgs is null && receiver != null && name != null
                                                     && interp.TryInvokeCachedMethod(receiver, name, callArgs, curLine,
                                                         curCol, out var mres))
                                {
                                    if (Interpreter.GlobalWriteStamp != cachedCallStamp) Array.Clear(globalSlotState);
                                    Push(VmValue.From(mres));
                                    break;
                                }

                                // ── FLAT class-method call: run the compiled body IN PLACE, like CALL and
                                // CALL_VAL do. The dispatch below enters the interpreter's wrapper, which runs the
                                // body through a NESTED Vm.Execute, and that invocation builds its own locals array
                                // and its three pool lists: measured, the whole of a method call's remaining cost.
                                // Eligibility mirrors the wrapper's own gate, plus the shapes the flat swap cannot
                                // express yet (a params tail, which needs the tail assembled) and pure methods,
                                // whose memo the wrapper owns.
                                // A `super.M()` receiver is a SuperProxy: the SAME swap, with the two things the
                                // keyword changes. The LOOKUP starts at the PARENT class, because the instance's own
                                // class would answer the override, and the receiver a body sees is the INSTANCE, which
                                // is also the trailing `this` local it reads.
                                var flatCi = receiver as ClassInstance;
                                ClassInfo? flatSuperClass = null;
                                if (flatCi == null && receiver is SuperProxy flatSuperProxy)
                                {
                                    flatCi = flatSuperProxy.Instance as ClassInstance;
                                    flatSuperClass = flatSuperProxy.ParentClass;
                                }
                                FuncDecl? mFlat = null;
                                if (flatCi != null && name is not null)
                                {
                                    // A `super.M()` call asks the same question with the parent as the starting
                                    // class, which is exactly what the tree's BaseProxy arm does.
                                    interp.TryGetScriptMethod(flatSuperClass ?? flatCi.Class, name, callArgs, out mFlat);
                                }
                                if (!methodWantsRef && callTypeArgs is null && flatCi != null && name is not null && mFlat != null
                                    && mFlat.Compiled != null && Interpreter.VmPathEnabled(mFlat)
                                    && (mFlat.DefaultValues == null || mFlat.IsVmShell)
                                    && !mFlat.HasYield && !mFlat.IsCoroutine && !mFlat.IsPure
                                    && mFlat.Params.Count == argCount
                                    // A ref/out declaration IS flat-eligible now: the arguments arrive as cells (wired
                                    // just above, with the OUT ones wrapped in tracking cells) and a compiled body
                                    // derefs and writes through them exactly as the CALL arm's flat swap does. That
                                    // exclusion was what a source-less bundle died on ("Method 'Bad' is
                                    // bytecode-only and cannot run on the tree walker": the call fell through to the
                                    // tree, which has no AST for a carried body).
                                    && !(mFlat.IsParams.Count > 0 && mFlat.IsParams[^1]))
                                {
                                    if (frames.Count >= VmMaxFrames)
                                        throw new ScriptException(DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                    interp.VmInvocations++;
                                    var flatDeclClass = interp.DeclaringClassOf(flatCi.Class, mFlat);
                                    var methodFrame = RentSavedFrame();
                                    methodFrame.Name = mFlat.Name;
                                    methodFrame.Decl = mFlat;
                                    methodFrame.OutTrack = outTrack;
                                    methodFrame.Bc = bc;
                                    methodFrame.Code = code;
                                    methodFrame.Ip = ip;
                                    methodFrame.Consts = consts;
                                    methodFrame.Locals = locals;
                                    methodFrame.CurLine = curLine;
                                    methodFrame.CurCol = curCol;
                                    methodFrame.SpBase = spBase;
                                    methodFrame.Handlers = handlers;
                                    methodFrame.HandlerBase = handlerBase;
                                    methodFrame.PendingFinally = pendingFinally;
                                    methodFrame.PendingFinallyPayload = pendingFinallyPayload;
                                    methodFrame.Seq = seq;
                                    methodFrame.GlobalSlots = globalSlots;
                                    methodFrame.GlobalSlotState = globalSlotState;
                                    methodFrame.NsContext = curNs;
                                    methodFrame.ForEachStates = forEachStates;
                                    methodFrame.GenCo = curGenCo;
                                    methodFrame.SavesClassContext = true;
                                    methodFrame.ThisInstance = interp.CurrentThis;
                                    methodFrame.ThisClass = interp.CurrentClass;
                                    frames.Add(methodFrame);
                                    currentFunc = mFlat.Name;
                                    bc = mFlat.Compiled;
                                    interp.RegisterNestedDeclsForRun(bc);
                                    code = bc.Code;
                                    consts = bc.ConstsVm;
                                    locals = RentLocals();
                                    for (var ai = 0; ai < argCount && ai < locals.Length; ai++)
                                        locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(mFlat, callArgs[ai], ai, curLine, curCol));
                                    // A VM body reads its parameters from these locals, so the tuple element names
                                    // the tree's bind loop attaches for it have to be attached here.
                                    if (mFlat.TupleParamNames is { Count: > 0 })
                                        for (var tpi = 0; tpi < mFlat.Params.Count && tpi < argCount; tpi++)
                                            interp.BindTupleParam(mFlat, tpi, callArgs[tpi], curLine, curCol);
                                    // The receiver goes in the trailing `this` local the compiler indexes after
                                    // the parameters, and the context the body reads is set for its duration.
                                    if (mFlat.Params.Count < locals.Length)
                                        locals[mFlat.Params.Count] = VmValue.From(flatCi);
                                    interp.CurrentThis = flatCi;
                                    interp.CurrentClass = flatDeclClass ?? flatCi.Class;
                                    ip = 0;
                                    curLine = 0;
                                    curCol = 0;
                                    spBase = sp;
                                    handlers = null;   // lazily allocated on the callee's first TRY
                                    handlerBase = 0;
                                    pendingFinally = false;
                                    pendingFinallyPayload = null;
                                    seq = 0;
                                    forEachStates = null;
                                    Array.Clear(globalSlotState);
                                    (globalSlots, globalSlotState) = RentSlots();
                                    continue;
                                }

                                // ── FLAT struct-method call: the same in-place swap, for a StructInstance
                                // receiver. A struct method's context is the instance itself with NO class (a
                                // struct has no ClassInfo), which is what the struct wrapper sets too, and the
                                // ACCESS check the tree makes at the call site happens here before the swap.
                                if (callTypeArgs is null && receiver is StructInstance structSi && name is not null
                                    && interp.TryGetScriptStructMethod(structSi, name, out var sFlat)
                                    && sFlat.Compiled != null && Interpreter.VmPathEnabled(sFlat)
                                    && (sFlat.DefaultValues == null || sFlat.IsVmShell)
                                    && !sFlat.HasYield && !sFlat.IsCoroutine && !sFlat.IsPure
                                    && sFlat.Params.Count == argCount
                                    && (sFlat.IsRefParams == null || !sFlat.IsRefParams.Any(b => b))
                                    && (sFlat.IsOutParams == null || !sFlat.IsOutParams.Any(b => b))
                                    && !(sFlat.IsParams.Count > 0 && sFlat.IsParams[^1]))
                                {
                                    interp.CheckStructMethodAccess(sFlat, structSi.StructName, curLine, curCol);
                                    if (frames.Count >= VmMaxFrames)
                                        throw new ScriptException(DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                    interp.VmInvocations++;
                                    var structFrame = RentSavedFrame();
                                    structFrame.Name = sFlat.Name;
                                    structFrame.Decl = sFlat;
                                    structFrame.OutTrack = null;
                                    structFrame.Bc = bc;
                                    structFrame.Code = code;
                                    structFrame.Ip = ip;
                                    structFrame.Consts = consts;
                                    structFrame.Locals = locals;
                                    structFrame.CurLine = curLine;
                                    structFrame.CurCol = curCol;
                                    structFrame.SpBase = spBase;
                                    structFrame.Handlers = handlers;
                                    structFrame.HandlerBase = handlerBase;
                                    structFrame.PendingFinally = pendingFinally;
                                    structFrame.PendingFinallyPayload = pendingFinallyPayload;
                                    structFrame.Seq = seq;
                                    structFrame.GlobalSlots = globalSlots;
                                    structFrame.GlobalSlotState = globalSlotState;
                                    structFrame.NsContext = curNs;
                                    structFrame.ForEachStates = forEachStates;
                                    structFrame.GenCo = curGenCo;
                                    structFrame.SavesClassContext = true;
                                    structFrame.ThisInstance = interp.CurrentThis;
                                    structFrame.ThisClass = interp.CurrentClass;
                                    frames.Add(structFrame);
                                    currentFunc = sFlat.Name;
                                    bc = sFlat.Compiled;
                                    interp.RegisterNestedDeclsForRun(bc);
                                    code = bc.Code;
                                    consts = bc.ConstsVm;
                                    locals = RentLocals();
                                    for (var ai = 0; ai < argCount && ai < locals.Length; ai++)
                                        locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(sFlat, callArgs[ai], ai, curLine, curCol));
                                    if (sFlat.TupleParamNames is { Count: > 0 })
                                        for (var tpi = 0; tpi < sFlat.Params.Count && tpi < argCount; tpi++)
                                            interp.BindTupleParam(sFlat, tpi, callArgs[tpi], curLine, curCol);
                                    if (sFlat.Params.Count < locals.Length)
                                        locals[sFlat.Params.Count] = VmValue.From(structSi);
                                    interp.CurrentThis = structSi;
                                    interp.CurrentClass = null;   // a struct has no ClassInfo
                                    ip = 0;
                                    curLine = 0;
                                    curCol = 0;
                                    spBase = sp;
                                    handlers = null;
                                    handlerBase = 0;
                                    pendingFinally = false;
                                    pendingFinallyPayload = null;
                                    seq = 0;
                                    forEachStates = null;
                                    Array.Clear(globalSlotState);
                                    (globalSlots, globalSlotState) = RentSlots();
                                    continue;
                                }

                                // ── FLAT interface-default call: a default is a compiled body too, and it reaches
                                // InvokeExtensionMember by the SAME helper an extension member does, so the swap is
                                // the same with one more thing set: the CONTRACT, because a default composes through
                                // its own contract. It comes FIRST because that is the order the tree asks in: every
                                // real member has already declined, and a default is nearer than a block.
                                FuncDecl? dFlat = null;
                                string? dContract = null;
                                ClassInfo? dClass = null;
                                if (name is not null
                                    && interp.TryGetFlatInterfaceDefault(receiver, name, argCount, out dFlat, out dContract, out dClass)
                                    && dFlat is { Compiled: not null } && Interpreter.VmPathEnabled(dFlat)
                                    && (dFlat.DefaultValues == null || dFlat.IsVmShell)
                                    && !dFlat.HasYield && !dFlat.IsCoroutine && !dFlat.IsPure
                                    && dFlat.Params.Count == argCount
                                    && (dFlat.IsRefParams == null || !dFlat.IsRefParams.Any(b => b))
                                    && (dFlat.IsOutParams == null || !dFlat.IsOutParams.Any(b => b))
                                    && !(dFlat.IsParams.Count > 0 && dFlat.IsParams[^1]))
                                {
                                    if (frames.Count >= VmMaxFrames)
                                        throw new ScriptException(DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                    interp.VmInvocations++;
                                    var dFrame = RentSavedFrame();
                                    dFrame.Name = dFlat.Name;
                                    dFrame.Decl = dFlat;
                                    dFrame.OutTrack = null;
                                    dFrame.Bc = bc;
                                    dFrame.Code = code;
                                    dFrame.Ip = ip;
                                    dFrame.Consts = consts;
                                    dFrame.Locals = locals;
                                    dFrame.CurLine = curLine;
                                    dFrame.CurCol = curCol;
                                    dFrame.SpBase = spBase;
                                    dFrame.Handlers = handlers;
                                    dFrame.HandlerBase = handlerBase;
                                    dFrame.PendingFinally = pendingFinally;
                                    dFrame.PendingFinallyPayload = pendingFinallyPayload;
                                    dFrame.Seq = seq;
                                    dFrame.GlobalSlots = globalSlots;
                                    dFrame.GlobalSlotState = globalSlotState;
                                    dFrame.NsContext = curNs;
                                    dFrame.ForEachStates = forEachStates;
                                    dFrame.GenCo = curGenCo;
                                    dFrame.SavesClassContext = true;
                                    dFrame.ThisInstance = interp.CurrentThis;
                                    dFrame.ThisClass = interp.CurrentClass;
                                    dFrame.ThisReceiverName = interp.CurrentReceiverName;
                                    dFrame.ThisContract = interp.CurrentContract;
                                    frames.Add(dFrame);
                                    currentFunc = dFlat.Name;
                                    bc = dFlat.Compiled;
                                    interp.RegisterNestedDeclsForRun(bc);
                                    code = bc.Code;
                                    consts = bc.ConstsVm;
                                    locals = RentLocals();
                                    for (var ai = 0; ai < argCount && ai < locals.Length; ai++)
                                        locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(dFlat, callArgs[ai], ai, curLine, curCol));
                                    if (dFlat.TupleParamNames is { Count: > 0 })
                                        for (var tpi = 0; tpi < dFlat.Params.Count && tpi < argCount; tpi++)
                                            interp.BindTupleParam(dFlat, tpi, callArgs[tpi], curLine, curCol);
                                    if (dFlat.Params.Count < locals.Length)
                                        locals[dFlat.Params.Count] = VmValue.From(receiver);
                                    interp.CurrentThis = receiver;
                                    interp.CurrentClass = dClass;      // a class instance's class, null for a struct
                                    interp.CurrentReceiverName = null;
                                    interp.CurrentContract = dContract;
                                    ip = 0;
                                    curLine = 0;
                                    curCol = 0;
                                    spBase = sp;
                                    handlers = null;
                                    handlerBase = 0;
                                    pendingFinally = false;
                                    pendingFinallyPayload = null;
                                    seq = 0;
                                    forEachStates = null;
                                    Array.Clear(globalSlotState);
                                    (globalSlots, globalSlotState) = RentSlots();
                                    continue;
                                }

                                // ── FLAT extension-member call: the same in-place swap, for the ONE route a member
                                // MISS takes into a compiled body (InvokeExtensionMember, which the wrapper ran
                                // through a nested Vm.Execute). An extension member's receiver is ANY value, so this
                                // runs wherever the class and struct branches found no real member: measured, 3707
                                // bytes per call for `5.TwiceExt()` against 131 for a plain call. An interface
                                // DEFAULT rides the same path, because it is invoked through the same helper.
                                FuncDecl? xFlat = null;
                                string? xReceiverName = null;
                                if (name is not null
                                    && interp.TryGetFlatExtensionMember(receiver, name, argCount, out xFlat, out xReceiverName)
                                    && xFlat is { Compiled: not null } && Interpreter.VmPathEnabled(xFlat)
                                    && (xFlat.DefaultValues == null || xFlat.IsVmShell)
                                    && !xFlat.HasYield && !xFlat.IsCoroutine && !xFlat.IsPure
                                    && xFlat.Params.Count == argCount
                                    && (xFlat.IsRefParams == null || !xFlat.IsRefParams.Any(b => b))
                                    && (xFlat.IsOutParams == null || !xFlat.IsOutParams.Any(b => b))
                                    && !(xFlat.IsParams.Count > 0 && xFlat.IsParams[^1]))
                                {
                                    if (frames.Count >= VmMaxFrames)
                                        throw new ScriptException(DepthCapMessage(VmMaxFrames, "Flat-frame"));
                                    interp.VmInvocations++;
                                    var xFrame = RentSavedFrame();
                                    xFrame.Name = xFlat.Name;
                                    xFrame.Decl = xFlat;
                                    xFrame.OutTrack = null;
                                    xFrame.Bc = bc;
                                    xFrame.Code = code;
                                    xFrame.Ip = ip;
                                    xFrame.Consts = consts;
                                    xFrame.Locals = locals;
                                    xFrame.CurLine = curLine;
                                    xFrame.CurCol = curCol;
                                    xFrame.SpBase = spBase;
                                    xFrame.Handlers = handlers;
                                    xFrame.HandlerBase = handlerBase;
                                    xFrame.PendingFinally = pendingFinally;
                                    xFrame.PendingFinallyPayload = pendingFinallyPayload;
                                    xFrame.Seq = seq;
                                    xFrame.GlobalSlots = globalSlots;
                                    xFrame.GlobalSlotState = globalSlotState;
                                    xFrame.NsContext = curNs;
                                    xFrame.ForEachStates = forEachStates;
                                    xFrame.GenCo = curGenCo;
                                    xFrame.SavesClassContext = true;
                                    xFrame.ThisInstance = interp.CurrentThis;
                                    xFrame.ThisClass = interp.CurrentClass;
                                    xFrame.ThisReceiverName = interp.CurrentReceiverName;
                                    frames.Add(xFrame);
                                    currentFunc = xFlat.Name;
                                    bc = xFlat.Compiled;
                                    interp.RegisterNestedDeclsForRun(bc);
                                    code = bc.Code;
                                    consts = bc.ConstsVm;
                                    locals = RentLocals();
                                    for (var ai = 0; ai < argCount && ai < locals.Length; ai++)
                                        locals[ai] = VmValue.From(Interpreter.CoerceFlatCallArg(xFlat, callArgs[ai], ai, curLine, curCol));
                                    if (xFlat.TupleParamNames is { Count: > 0 })
                                        for (var tpi = 0; tpi < xFlat.Params.Count && tpi < argCount; tpi++)
                                            interp.BindTupleParam(xFlat, tpi, callArgs[tpi], curLine, curCol);
                                    if (xFlat.Params.Count < locals.Length)
                                        locals[xFlat.Params.Count] = VmValue.From(receiver);
                                    interp.CurrentThis = receiver;          // an extension member's `this` is the receiver
                                    interp.CurrentClass = null;             // it belongs to no class, so bare names stay global
                                    interp.CurrentReceiverName = xReceiverName;
                                    ip = 0;
                                    curLine = 0;
                                    curCol = 0;
                                    spBase = sp;
                                    handlers = null;
                                    handlerBase = 0;
                                    pendingFinally = false;
                                    pendingFinallyPayload = null;
                                    seq = 0;
                                    forEachStates = null;
                                    Array.Clear(globalSlotState);
                                    (globalSlots, globalSlotState) = RentSlots();
                                    continue;
                                }

                                Push(VmValue.From(
                                    interp.InvokeMethodValues(receiver, name ?? "", callArgs, curLine, curCol, callTypeArgs)));
                                // Script methods run VM-compiled bodies that can mutate globals, flush.
                                Array.Clear(globalSlotState);
                                break;
                            }
                            finally
                            {
                                ReturnArgs(callArgs);
                            }
                        }
                    default:
                        // The dispatch's unknown-opcode arm. This is not a bad ARGUMENT, it is an instruction the
                        // executor has no arm for, which BytecodeVerifier refuses at LOAD, so reaching it means the
                        // engine's own tables disagree; hence InvalidOperationException, and the VALUE in the message
                        // because the opcode is the one thing that says which instruction arrived. (CA2208 is the rule
                        // that asked for this shape: an argument exception has to name a real parameter, and there is
                        // no parameter here to name.)
                        throw new InvalidOperationException($"the bytecode gave the VM an unknown opcode ({(int)op})");
                }
            }
        }
        catch (OutOfMemoryException)
        {
            // An allocation failure while the flat stack grew, the case a deep-but-legitimate recursion
            // hits when the machine cannot back the depth it asked for. The runtime's own message is the
            // RESOURCE KEY "Arg_OutOfMemoryException" under InvariantGlobalization (which the AOT build
            // sets), so without this the user learns neither how far the script got nor which knob governs
            // the depth. frames/currentFunc are live right here, so both answers are available.
            // NOTE: building the message needs an allocation of its own while the heap is at its limit; if
            // that fails the original OutOfMemoryException propagates, which is an acceptable fallback.
            throw new ScriptException(ErrorCode.Unspecified, FlatStackOomMessage(frames.Count, currentFunc));
        }
        catch (ScriptException ex)
        {
            // used to build ScriptErrorInfo.Frames when the error escapes the VM entirely.
            List<string>? unwoundNames = null;

            // A BAIL runs the pending `finally` blocks of every frame it crosses, and it is NEVER swallowed by a
            // script's own `catch`: the catch arm below is skipped for it. Both halves matter. Running the finallys
            // is the whole point (a bail used to skip them, which is how the Excel sample left an invisible
            // EXCEL.EXE behind while its `finally` held Quit()), and skipping the catches is what keeps this change
            // invisible to a script: a `try { <unrunnable instruction> } catch (var e) { ... }` must still end up
            // re-run by the tree (which RUNS that instruction), not answered by its own catch on the VM.
            //
            // And the finallys run ONLY for a COMMITTED body, which is the case the boundary REFUSES. An
            // UNCOMMITTED bail is handed back to the tree and RE-RUN there, so a finally run here would duplicate
            // its effects and, if the finally is itself observable, COMMIT the body, turning a successful re-run
            // into a refusal. Such a bail therefore propagates untouched and the tree runs the statement, finally
            // included, exactly as it did before this change.
            var isBail = Interpreter.IsVmBailSignal(ex);
            if (isBail && !interp.VmBodyCommitted) throw;
            // A CANCELLED RUN IS NOT THE SCRIPT'S TO CATCH, exactly like the bail above: a runner's Stop button cannot be
            // defeated by `try { ... } catch (var e) { }`, so the catch arms are skipped while the FINALLY arms still
            // run (a script's own teardown has to happen). Measured before this: a cancelled run inside a `try`/`catch`
            // ended with `HasError=False`, i.e. the script swallowed the kill and the loop kept going.
            var isCancelled = ex.ErrorCode is ErrorCode.Cancelled or ErrorCode.RunSuspend;

            // Unwind handlers, innermost first; a catchIp handler binds the value, a finallyIp handler
            // runs the finally block then rethrows (via END_FINALLY), outermost no-handler → propagate.
            // With the flat call stack, "no handler in this frame" pops the caller's SavedFrame and
            // re-walks ITS handlers (the caller may try/catch around the call). No frames left → the
            // exception escapes the whole VM invocation (propagates to the interpreter).
            while (true)
            {
                while (handlers != null && handlers.Count > handlerBase)
                {
                    var h = handlers[^1];
                    handlers.RemoveAt(handlers.Count - 1);
                    DisposeAbandonedForEach(forEachStates, locals, h.seq);
                    // A script's own exit() is intercepted by this try: the tree records its code in
                    // Environment.ExitCode when the try sees the abort (VisitTryStmt), and `exitcode` is
                    // script-visible, so the VM does the same here, for a catch and for a finally alike.
                    if (Interpreter.AbortExitCode(ex) is int abortExitCode) Environment.ExitCode = abortExitCode;
                    if (h.catchIp >= 0 && !isBail && !isCancelled)
                    {
                        sp = h.sp;
                        // A new exception supersedes any pending finally from an earlier handler
                        // iteration (e.g. a finally body rethrowing), clear the stale payload so the
                        // next END_FINALLY never rethrows it.
                        pendingFinally = false;
                        pendingFinallyPayload = null;
                        var val = ex.Value is ScriptErrorInfo sei
                            ? sei.WithLocation(ex.Line, ex.Column)
                            : new ScriptErrorInfo(ex.ErrorCode, ex.Message);
                        // Script frames collected while unwinding down to this handler (innermost
                        // first), a catch that starts in the VM must see the same chain the tree
                        // would report.
                        var handlerNames = WithEntryFrame(unwoundNames, entryName, entryDecl);
                        if (handlerNames is { Count: > 0 } && val.Frames is null)
                            val = val with { Frames = handlerNames };
                        Push(VmValue.From(val));
                        ip = h.catchIp;
                        // The exception may have escaped a non-flat script call that mutated globals,
                        // the same frame continues from here, so flush the stale slot cache.
                        Array.Clear(globalSlotState);
                        goto restart;
                    }

                    if (h.finallyIp < 0) continue;
                    sp = h.sp;
                    pendingFinally = true;
                    // An abort keeps its CODE, so the payload carries the exception itself and END_FINALLY
                    // rethrows it as the abort it is. The ScriptErrorInfo form below stores the code only
                    // as the message text, which turned a script's own exit() into an engine error the
                    // moment the try had a finally (measured: "[ERROR] ScriptAbort: 4" and exit 1, where
                    // the tree ran the finally and exited 4). A BAIL travels the same way and for the same
                    // reason: END_FINALLY rethrows it, so it keeps unwinding out of the VM (see VmBailException).
                    if (ex.ErrorCode == ErrorCode.ScriptAbort || isBail)
                    {
                        pendingFinallyPayload = ex;
                    }
                    else
                    {
                        var payload = ex.Value is ScriptErrorInfo sei2
                            ? sei2.WithLocation(ex.Line, ex.Column)
                            : new ScriptErrorInfo(ex.ErrorCode, ex.Message);
                        if (unwoundNames is { Count: > 0 } && payload.Frames is null)
                            payload = payload with { Frames = unwoundNames };
                        pendingFinallyPayload = payload;
                    }
                    ip = h.finallyIp;
                    // Same-frame resume, flush any stale slot cache before the finally body reads globals.
                    Array.Clear(globalSlotState);
                    goto restart;
                }

                DisposeAbandonedForEach(forEachStates, locals, -1); // escapes this frame, dispose everything
                // A suspended generator whose foreach loop was abandoned by the throw has no live
                // caller frame of its own, drop its retained frame so it is never restored as one.
                DropAbandonedGeneratorFrames();
                if (frames.Count == 0)
                {
                    // The error escapes this VM invocation. The frames themselves are gone (popped
                    // as we unwound), but unwoundNames collected their function names innermost
                    // first, attach them exactly like the tree's RuntimeError does, so a script
                    // catch around the call can walk ex.Frames from either engine.
                    var names = WithEntryFrame(unwoundNames, entryName, entryDecl);
                    // An abort's code lives in ex.Value, so it is never enriched (the rebuild would
                    // replace the code with the ScriptErrorInfo that carries these names). A BAIL is never
                    // enriched either, so the boundary can still recognise it by its value.
                    if (names is { Count: > 0 } && ex.ErrorCode != ErrorCode.ScriptAbort && !isBail
                        && ex.Value is not ScriptErrorInfo { Frames: not null })
                    {
                        var enriched = ex.Value is ScriptErrorInfo sei
                            ? sei with { Frames = names }
                            : new ScriptErrorInfo(ex.ErrorCode, ex.Message, null, names);
                        throw new ScriptException(ex.ErrorCode, enriched, ex.Line, ex.Column);
                    }
                    throw;
                }
                var cf = frames[^1];
                // An UNNAMED body is a LAMBDA, and its frame reports `<lambda>`: the label the tree's own frames use
                // (Interpreter.FrameLabel). Without it a bundle reported ["__program__"] for an error raised inside a
                // lambda, because the frame's Name was empty and the entry frame was then the only name collected
                // (measured: crash_json_roundtrip's error-frames section, which is why error_frames differs).
                (unwoundNames ??= new List<string>()).Add(Interpreter.FrameWithPosition(Interpreter.FrameLabel(cf.Name), cf.CurLine, cf.CurCol, cf.Decl));
                // The throwing function may be a VM generator driven by a FOREACH_NEXT, its drive is
                // over; mark the generator Dead (an exception ends a generator, like the tree). Its
                // spBase (still the generator's, not yet overwritten below) is the driver's sp at
                // drive time; the unwind must restore THAT so the driver resumes at the right height.
                var cfIsGen = cf.GeneratorHandle != null;
                var cfGenSpBase = cfIsGen ? spBase : 0;
                if (cf.GeneratorHandle is Coroutine cfCo)
                {
                    cfCo.Status = CoroutineStatus.Dead;
                    cfCo.VmState = null;
                }
                frames.RemoveAt(frames.Count - 1);
                bc = cf.Bc;
                code = cf.Code;
                ip = cf.Ip;
                consts = cf.Consts;
                ReturnLocals(locals); // callee's array, back to the pool BEFORE restoring the caller's
                locals = cf.Locals;
                curLine = cf.CurLine;
                curCol = cf.CurCol;
                spBase = cf.SpBase;
                handlers = cf.Handlers;
                handlerBase = cf.HandlerBase;
                pendingFinally = cf.PendingFinally;
                pendingFinallyPayload = cf.PendingFinallyPayload;
                seq = cf.Seq;
                forEachStates = cf.ForEachStates;
                ReturnSlots(globalSlots, globalSlotState); // callee's table, back to the pool
                globalSlots = cf.GlobalSlots;
                globalSlotState = cf.GlobalSlotState;
                curNs = cf.NsContext;   // F4: restore the caller's namespace context on unwind
                curGenCo = cf.GenCo;    // restore the caller's generator context on unwind
                if (cf.SavesClassContext)
                {
                    interp.CurrentThis = cf.ThisInstance;
                    interp.CurrentClass = cf.ThisClass;
                    interp.CurrentReceiverName = cf.ThisReceiverName;   // an extension block's alias
                    interp.CurrentContract = cf.ThisContract;           // an interface default's contract
                }
                // The callee may have mutated globals before throwing, the caller re-resolves.
                Array.Clear(globalSlotState);
                // A driven generator's throw leaves its operand region non-empty, reset sp to the
                // driver's drive-time height (its spBase for a plain callee).
                sp = cfIsGen ? cfGenSpBase : spBase;
                // Re-enter the handler walk for the caller's handlers (its try/catch around the call).
            }
        }
        catch (VmFlowException flow)
        {
            // break/continue/return escaping try regions: unwind the compiled handler stack (running
            // each pending finally via END_FINALLY, which rethrows this same flow), then resume the
            // flow. Only the handlers the compiler attributed to the exit site are unwound, a flow
            // targeting a loop inside an enclosing try stops before that try so its finally runs
            // exactly once via the normal completion path. Catch handlers never intercept control flow.
            while (handlers != null && handlers.Count > handlerBase && flow.Remaining > 0)
            {
                flow.Remaining--;
                var h = handlers[^1];
                handlers.RemoveAt(handlers.Count - 1);
                if (h.finallyIp < 0) continue;
                sp = h.sp;
                pendingFinally = true;
                pendingFinallyPayload = flow;
                ip = h.finallyIp;
                goto restart;
            }

            if (flow.Kind == VmFlowException.KindReturn)
            {
                DisposeAbandonedForEach(forEachStates, locals, -1);
                // A suspended generator whose foreach loop was abandoned by the return has no live
                // caller frame of its own, drop its retained frame so it is never restored as one.
                DropAbandonedGeneratorFrames();
                if (frames.Count == 0) return flow.Value;
                // Return crosses the flat call boundary: pop the caller's frame, push the value.
                var rf = frames[^1];
                frames.RemoveAt(frames.Count - 1);
                // The callee's OWN base is the caller's operand height at the call (a flat call sets
                // spBase = sp), and it is captured HERE because the swap below overwrites spBase with the
                // caller's own.
                var flowReturnBase = spBase;
                if (rf.GeneratorHandle != null)
                {
                    // The returning function is a VM generator driven by a FOREACH_NEXT that
                    // completed via a try/finally return, mark it Dead and jump to the driver's
                    // FOREACH_END (its return value is NOT a foreach element). Capture the
                    // generator's spBase before the swap-back overwrites it and restore the
                    // caller's stack height (the finallys may have left the operand region
                    // non-empty; the driver resumes at FOREACH_END with sp == its drive-time sp).
                    var genSpBase = spBase;
                    if (rf.GeneratorHandle is Coroutine rfCo)
                    {
                        // try/finally return → natural completion.
                        rfCo.Status = CoroutineStatus.Completed;
                        rfCo.VmState = null;
                    }
                    bc = rf.Bc;
                    code = rf.Code;
                    ip = rf.ForEachEndIp;
                    consts = rf.Consts;
                    ReturnLocals(locals); // generator's array, dead now, back to the pool
                    locals = rf.Locals;
                    curLine = rf.CurLine;
                    curCol = rf.CurCol;
                    spBase = rf.SpBase;
                    handlers = rf.Handlers;
                    handlerBase = rf.HandlerBase;
                    pendingFinally = rf.PendingFinally;
                    pendingFinallyPayload = rf.PendingFinallyPayload;
                    seq = rf.Seq;
                    forEachStates = rf.ForEachStates;
                    ReturnSlots(globalSlots, globalSlotState); // generator's table, back to the pool
                    globalSlots = rf.GlobalSlots;
                    globalSlotState = rf.GlobalSlotState;
                    curNs = rf.NsContext;   // F4: restore the caller's namespace context on flow unwind
                    curGenCo = rf.GenCo;    // restore the caller's generator context
                    Array.Clear(globalSlotState);
                    sp = genSpBase;
                    goto restart;
                }
                bc = rf.Bc;
                code = rf.Code;
                ip = rf.Ip;
                consts = rf.Consts;
                ReturnLocals(locals); // callee's array, back to the pool BEFORE restoring the caller's
                locals = rf.Locals;
                curLine = rf.CurLine;
                curCol = rf.CurCol;
                spBase = rf.SpBase;
                handlers = rf.Handlers;
                handlerBase = rf.HandlerBase;
                pendingFinally = rf.PendingFinally;
                pendingFinallyPayload = rf.PendingFinallyPayload;
                seq = rf.Seq;
                forEachStates = rf.ForEachStates;
                ReturnSlots(globalSlots, globalSlotState); // callee's table, back to the pool
                globalSlots = rf.GlobalSlots;
                globalSlotState = rf.GlobalSlotState;
                curNs = rf.NsContext;   // F4: restore the caller's namespace context on flow unwind
                curGenCo = rf.GenCo;    // restore the caller's generator context
                // A return escaping try/finally still crossed the flat-call boundary, the callee may
                // have mutated globals; the restored caller re-resolves.
                Array.Clear(globalSlotState);
                // DISCARD the callee's operand region before pushing the value, because a finally that ran
                // during the unwind can leave a PHANTOM operand there: an unwind jumps STRAIGHT to
                // `finallyIp`, so the `STORE_VAR` the NORMAL path uses to consume a `using`'s value is never
                // executed and that value is still on the stack when the flow resumes. Measured: a function
                // whose body is `using (var sb = new System.Text.StringBuilder()) { sb.Append("x"); return
                // sb.Length; }` left the CALLER one operand too high, so the caller's NEXT call popped the
                // wrong callee (a probe printed the right value only because the mis-popped CALL bailed and
                // the statement was re-run on the tree, and a stripped bundle refused the whole body:
                // `_vm_const` and `generic_struct_test` both died this way). The generator branch above
                // already performs this same discard (`sp = genSpBase`), so the two agree now.
                sp = flowReturnBase;
                Push(VmValue.From(flow.Value));
                goto restart;
            }

            ip = flow.Target;
            goto restart;
        }

        return null;
    }

    /// <summary>Resumes a VM generator suspended at the outermost boundary (VmSuspendState captured
    /// by a YIELD in the top frame). `resumeValue` mirrors the tree's pending-resume-value contract
    /// (Coroutine.Resume(arg) → the next bare yield consumes it); VM generators NEVER consume resume
    /// values, tree VisitYieldExpr skips the consume branch for generators, and Generator.MoveNext
    /// passes null, so the value is deliberately not pushed.</summary>
    public static object? ExecuteResumed(VmSuspendState st, object? resumeValue, Interpreter interp)
        => ExecuteResumedCore(st, resumeValue, interp, out _);

    /// <summary>ExecuteResumed with the next suspend state surfaced (a resumed generator may yield
    /// again, MoveNext needs the fresh state for the following resume).</summary>
    internal static object? ExecuteResumedCore(VmSuspendState st, object? resumeValue, Interpreter interp, out VmSuspendState? nextSuspend)
        => ExecuteCore(st.Bc, [], interp, out nextSuspend, st.CurrentFunc, st.NsContext, resume: st, resumeValue: resumeValue);

    /// <summary>Materializes the VM's parallel typed operand arrays ([0, sp)) into a VmValue[] for
    /// the VmSuspendState snapshot (the boundary YIELD consumes the yielded value first, so the
    /// captured stack is exactly the continuation's pre-YIELD stack).</summary>
    private static VmValue[] MaterializeStack(byte[] sK, byte[] sT, long[] sL, double[] sD, object?[] sO, int sp)
    {
        var arr = new VmValue[sp];
        for (var i = 0; i < sp; i++) arr[i] = new VmValue(sK[i], sT[i], sL[i], sD[i], sO[i]);
        return arr;
    }

    /// <summary>Disposes foreach enumerators whose loop was abandoned, their FOREACH_END never ran
    /// because an exception unwound past them (or escaped the invocation). Only states created AFTER
    /// the unwound handler's program-order sequence are disposed: a foreach still alive outside the
    /// abandoned region (e.g. a try inside the loop body catching locally) keeps its enumerator.
    /// minSeq = -1 disposes everything (invocation escape).</summary>
    private static void DisposeAbandonedForEach(List<(int stateLocal, int seq)>? tracked, VmValue[] locals, int minSeq)
    {
        if (tracked == null) return;
        for (var i = tracked.Count - 1; i >= 0; i--)
        {
            if (tracked[i].seq <= minSeq) continue; // created before the try, loop still alive
            var stateLocal = tracked[i].stateLocal;
            tracked.RemoveAt(i);
            if (locals[stateLocal].O is not VmForEachState { Enumerator: IDisposable d } fs) continue;
            try
            {
                d.Dispose();
            }
            catch
            {
                /* disposal must never mask the original error */
            }

            fs.Enumerator = null;
        }
    }

    /// <summary>Classifies a foreach collection into a live iterator state. VM generators
    /// (Generator&lt;T&gt;) are driven in-place (KindGenerator). Returns null for bare
    /// Coroutine/unknown → the VM falls back to the tree-walker (silent, correct).</summary>
    private static VmForEachState? CreateForEachState(object? coll, Interpreter interp)
    {
        switch (coll)
        {
            case null:
                return new VmForEachState { Kind = VmForEachState.KindArray, Arr = Array.Empty<object?>() };

            case Generator<object?> gen:
                // VM-flat generator drive: the foreach owns the inner generator's frame. ONLY
                // VmBacked generators (plain value-yield, proven free of CoroutineYieldRequest
                // producers by IsYieldRequestBody) may be driven in-place, a tree-backed
                // generator (Sleep/http/event/ReadLineAsync or an uncompilable body) must stay on
                // the tree, which owns the request machinery; driving it raw would flow the raw
                // request object as the loop element. Non-VmBacked → null → VM falls back to tree.
                return gen.InnerCoroutine.VmBacked
                    ? new VmForEachState { Kind = VmForEachState.KindGenerator, Generator = gen.InnerCoroutine }
                    : new VmForEachState { Kind = VmForEachState.KindTreeCoroutine, TreeCoro = gen.InnerCoroutine };

            // A BARE Coroutine is the same drive, and it is what `Coroutine.Start(fn, ...)` hands a `foreach`.
            case Coroutine bare:
                // A coroutine suspended AT THE BOUNDARY IS DRIVEN NOW, and it used to be refused here. The refusal
                // was load-bearing only because the drive got two things wrong, both measured and fixed (2026-10-19):
                //   * the swap-back restored the GENERATOR's spBase as the driver's height, and a VmState captured
                //     by `Coroutine.Start` (autoStart) belongs to another invocation (`driverBase=2 snapBase=0`), so
                //     the driver's own `return` found `sp` below `spBase` and refused its declared type. The driver's
                //     height now travels in the drive frame (`SavedFrame.Sp`) and both swap-backs restore it.
                //   * the resumed body ran at `snap.SpBase`, which can sit BELOW the driver's live operands, so its
                //     own operands overwrote the caller's stack slots. The snapshot is RELOCATED to the driver's
                //     height now, the same hand-off the retained-frame path already made.
                // With those two fixed, `aot_test` (35/0), `coroutine_bench` (35/0) and `vm_enumcomp` (9/0) answer
                // from their `--no-source` bundles what they answer from their sources, and `vm_enumcomp`'s source
                // leg stays green (it was the counter-example that kept the refusal alive).
                return bare.VmBacked
                    ? new VmForEachState { Kind = VmForEachState.KindGenerator, Generator = bare }
                    : new VmForEachState { Kind = VmForEachState.KindTreeCoroutine, TreeCoro = bare };

            case Array arr:
                return new VmForEachState { Kind = VmForEachState.KindArray, Arr = arr };

            case string s:
                return new VmForEachState { Kind = VmForEachState.KindString, Str = s };

            case IScriptList sl:
                return new VmForEachState { Kind = VmForEachState.KindScriptList, ScriptList = sl };

            case IList il:
                return new VmForEachState { Kind = VmForEachState.KindIList, List = il };

            case IScriptDictionary:
                // Matches the tree-walker exactly: dict foreach yields ScriptKeyValuePair(entry.Key, entry.Value).
                var dictEntries = interp.MaterializeForEach(coll, false);
                return dictEntries == null ? null : new VmForEachState { Kind = VmForEachState.KindDict, Entries = dictEntries };

            case IEnumerable en:
                {
                    // NOTE: do NOT dispose here, the enumerator is stored in VmForEachState and
                    // disposed by FOREACH_END (see VmForEachState disposal in the FOREACH_END handler).
                    // A `using var` here would dispose before the loop runs (MoveNext on a disposed
                    // native-backed enumerator like SqliteRowEnumerator → access violation).
                    var enu = en.GetEnumerator();
                    return new VmForEachState { Kind = VmForEachState.KindEnumerable, Enumerator = enu };
                }

            default:
                return null; // bare Coroutine / unknown, the tree-walker owns these
        }
    }

    /// <summary>True for an operand the VM's numeric Arith cannot model, so the operation belongs to the tree
    /// walker, whose ApplyBinaryOp has the arm that answers it (DateTime and TimeSpan arithmetic for instance).
    /// A number, a char and a string stay with the VM: a string is only ever a concat, which Arith answers for
    /// ADD, and SUB/MUL/DIV on one are already the VM's own error today. The predicate is asked of the OBJECT
    /// kind (K == 3) alone; the numeric kinds need no coercion.</summary>
    private static bool UnmodelledArithOperand(VmValue v) => v.K == 3
        && v.O is not (null or long or int or short or byte or double or float or decimal or char or string);

    /// <summary>The arithmetic operation itself, over VALUES: the one implementation the dispatch's slow
    /// path, the in-place fast path's fallback and the compiler's constant fold all reach, so a folded
    /// constant cannot be a second opinion.</summary>
    internal static VmValue Arith(Opcode op, VmValue a, VmValue b, int line, int col)
    {
        // Fast path: both long, no boxing
        if (a.K == 1 && b.K == 1)
            return op switch
            {
                Opcode.ADD => new VmValue(a.L + b.L, IntegralResultTag(a.T, b.T)),
                Opcode.SUB => new VmValue(a.L - b.L, IntegralResultTag(a.T, b.T)),
                Opcode.MUL => new VmValue(a.L * b.L, IntegralResultTag(a.T, b.T)),
                Opcode.DIV => b.L == 0 ? throw Interpreter.VmRuntimeError<ScriptException>("Division by zero", line, col) : new VmValue(a.L / b.L, IntegralResultTag(a.T, b.T)),
                _ => default
            };
        // String concatenation, through the ONE display rule, not VmValue.ToString(): a `Type` and an ARRAY have
        // no writable ToString of their own (ValueFormatter.ToDisplay), and spelling the conversion out here made
        // a COMPILED body print "DispatchVtbl.ComNode" and "System.Object[]" after the tree had been fixed, the
        // stale twin of the tree's concat, which is why the check for it has to run inside a function too.
        if (op == Opcode.ADD && (a is { K: 3, O: string } || b is { K: 3, O: string }))
            return new VmValue(ValueFormatter.ToDisplay(a.ToObject()) + ValueFormatter.ToDisplay(b.ToObject()));
        // A decimal PAIR stays decimal, exactly as the tree's ApplyBinaryOp does. Without this the fallback
        // below was `ToDoubleV(a) op ToDoubleV(b)`, whose decimal arm is `(double)m`, the whole of the
        // "decimal arithmetic is double-based" gap, and why a compiled sum of two decimal literals reported
        // "double" while the tree reported "decimal".
        if (op is Opcode.ADD or Opcode.SUB or Opcode.MUL or Opcode.DIV
            && a is { K: 3, O: decimal decA } && b is { K: 3, O: decimal decB })
            return op switch
            {
                Opcode.ADD => VmValue.From(decA + decB),
                Opcode.SUB => VmValue.From(decA - decB),
                Opcode.MUL => VmValue.From(decA * decB),
                Opcode.DIV => decB == 0m
                    ? throw Interpreter.VmRuntimeError<ScriptException>("Division by zero", line, col)
                    : VmValue.From(decA / decB),
                _ => default
            };

        // Float path
        var da = ToDoubleV(a);
        var db = ToDoubleV(b);
        return op switch
        {
            Opcode.ADD => new VmValue(da + db),
            Opcode.SUB => new VmValue(da - db),
            Opcode.MUL => new VmValue(da * db),
            Opcode.DIV => new VmValue(da / db),
            _ => default
        };
    }

    /// <summary>The bitwise/shift/modulo operation itself, over VALUES (see Arith).</summary>
    internal static VmValue Bitwise(Opcode op, VmValue a, VmValue b, int line, int col)
    {
        // MOD on a decimal pair is decimal too. AsInt64V has no decimal arm, so a boxed decimal read as 0,
        // which is why `3m % 2m` in a compiled function threw "Modulo by zero." while the tree answered 1.
        if (op == Opcode.MOD && a is { K: 3, O: decimal modA } && b is { K: 3, O: decimal modB })
            return modB == 0m
                ? throw Interpreter.VmRuntimeError<ScriptException>("Modulo by zero", line, col)
                : VmValue.From(modA % modB);

        // MOD on a pair with a NON-INTEGRAL operand is the tree's double rule. AsInt64V truncates, so
        // `7.5 % 2` answered 1 where the tree (whose numeric tail converts both operands with ToDouble)
        // and C# answer 1.5, and `10.5 % 0.0` threw "Modulo by zero" where both answer NaN. The
        // all-decimal pair keeps its own arm above, which is the same split the tree makes.
        if (op == Opcode.MOD && (IsNonIntegralNumeric(a) || IsNonIntegralNumeric(b)))
            return new VmValue(ToDoubleV(a) % ToDoubleV(b));

        var la = AsInt64V(a);
        var lb = AsInt64V(b);
        // A SHIFT keeps the promoted LEFT operand's width, and C# masks its COUNT by that width: `& 0x1F`
        // for a 32-bit type, `& 0x3F` for a 64-bit one. Reading every operand as a long therefore made
        // `1 << 33` 8589934592 typed long, where the tree (IntOp shifts an int, so 1 << (33 & 31) = 2) and
        // C# answer 2 typed int. The width test is the one the ADD arm already asks for its result tag
        // (IntegralResultTag over both operands), so the two arms cannot disagree about what "int" means.
        var tag = IntegralResultTag(OperandTag(a), OperandTag(b));
        var shiftMask = ShiftMask(tag == 1);
        var result = op switch
        {
            Opcode.MOD => lb == 0 ? throw Interpreter.VmRuntimeError<ScriptException>("Modulo by zero", line, col) : la % lb,
            Opcode.BITAND => la & lb,
            Opcode.BITOR => la | lb,
            Opcode.BITXOR => la ^ lb,
            Opcode.SHL => la << (int)(lb & shiftMask),
            Opcode.SHR => la >> (int)(lb & shiftMask),
            _ => 0L
        };
        // The RESULT's script type is the promoted operand type, as in C#: an all-int pair answers an int, so
        // `typeof(1 << 33)` and `typeof(7 % 2)` are "int" here as they are on the tree. This arm used to
        // return a long for every op, which is why a compiled body named "long" for a MOD, a bitwise op or a
        // shift the tree names "int"; the ADD/SUB/MUL/DIV arm next door states the same rule for its own
        // in-place operands (PromotesToInt over both tags), and both now ask the same question.
        return tag == 1 ? new VmValue(result, 1) : new VmValue(result);
    }

    /// <summary>The integral TAG of an operand, so a rule stated for the tagged in-place fast path can be
    /// asked of a BOXED integral too. VmValue.From is the one mapping between a CLR value and a tag, so this
    /// cannot drift from it; anything that is not integral answers 0 (long), which is the conservative side
    /// for the shift-width question.</summary>
    private static byte OperandTag(VmValue v) => v.K switch
    {
        1 => v.T,
        3 => VmValue.From(v.O).T,
        _ => 0,
    };

    /// <summary>True for a numeric operand the integer path cannot read exactly: a double, a float, or a
    /// decimal that is not part of an all-decimal pair (that pair has its own arm in Bitwise, and the tree
    /// splits arithmetic the same way).</summary>
    private static bool IsNonIntegralNumeric(VmValue v)
        => v.K == 2 || (v.K == 3 && v.O is double or float or decimal);

    private static long AsInt64V(VmValue v) => v.K switch
    {
        1 => v.L,
        2 => (long)v.D,
        3 => v.O switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            sbyte sb => sb,
            uint ui => ui,
            ushort us => us,
            ulong ul => (long)ul,
            _ => 0L
        },
        _ => 0L
    };

    /// <summary>True for the integer type tags a binary arithmetic result promotes to INT: int (1), short
    /// (2), byte (3), sbyte (4) and ushort (6). C# promotes those five to int, so `ushort.MaxValue + 1` is
    /// an int, and a uint (5) or ulong (7) operand keeps the long. The constant fold in the compiler applies
    /// the same rule to its own operands (Compiler.IsSmallSignedInt, literals only).</summary>
    private static bool PromotesToInt(byte tag) => tag is 1 or 2 or 3 or 4 or 6;

    /// <summary>True when the operand is a script CLASS or STRUCT instance, which is the condition under
    /// which a binary operator has to be resolved by the interpreter's own ApplyBinaryOp: a script type can
    /// declare an operator for any of the twelve tokens, and the VM's numeric arms cannot know that.</summary>
    private static bool IsScriptInstanceValue(VmValue v)
        => v.K == 3 && v.O is ClassInstance or StructInstance;

    /// <summary>True when the operand is a PLAIN NUMBER, i.e. one the arithmetic arms below can read
    /// themselves. Everything else (a script class or struct, a CLR ENUM member, a bool, a string, a null) is
    /// the INTERPRETER's to resolve, which is the rule the two bitwise arms got wrong:
    /// a script class owns `|` through its operator overload, and a CLR enum member is unwrapped by
    /// ApplyBinaryOp, so `RegexOptions.IgnoreCase | RegexOptions.Compiled` answered 0 through the VM's
    /// AsInt64V where the tree answers 9 (crash_regex 6f). A bool is K=5 and a null K=0, so both delegate
    /// rather than entering a numeric path they have no payload for.</summary>
    private static bool IsNumericValue(VmValue v)
        => v.K == 1 || v.K == 2 || (v.K == 3 && IsNumericObj(v.O));

    /// <summary>The promoted type of an integral result: an INT (tag 1) when both operands promote to int, a
    /// LONG (tag 0) otherwise, which is C#'s binary numeric promotion. ONE statement, asked by the in-place
    /// arithmetic arm, by Bitwise and by the compiler's constant fold, so a folded constant cannot name a
    /// different type than a computed one.</summary>
    internal static byte IntegralResultTag(byte tagA, byte tagB)
        => PromotesToInt(tagA) && PromotesToInt(tagB) ? (byte)1 : (byte)0;

    /// <summary>The mask a shift applies to its count: 1F for a 32-bit promoted type, 3F for a 64-bit one.</summary>
    internal static int ShiftMask(bool bothInt) => bothInt ? 0x1F : 0x3F;

    internal static bool Compare(Opcode op, VmValue a, VmValue b)
    {
        // Fast path: both long, and both SIGNED. An unsigned tag (5=uint, 6=ushort, 7=ulong) keeps its
        // value in the signed payload, so `ulong.MaxValue > long.MaxValue` compared -1 against
        // long.MaxValue and answered False where the tree answers True (crash_numeric_types,
        // crash_type_check, probe-toplevel-vm section 17). Those pairs take the numeric path below, which
        // is the same comparison the tree makes.
        if (a.K == 1 && b.K == 1 && a.T is not (5 or 6 or 7) && b.T is not (5 or 6 or 7))
            return op switch
            {
                Opcode.LT => a.L < b.L,
                Opcode.GT => a.L > b.L,
                Opcode.EQ => a.L == b.L,
                Opcode.LE => a.L <= b.L,
                Opcode.GE => a.L >= b.L,
                Opcode.NE => a.L != b.L,
                _ => false
            };
        // Equality with null / non-numeric, use the interpreter's value-aware AreEqual
        // (handles records field-by-field, enums, null semantics) instead of plain Equals
        if (op is Opcode.EQ or Opcode.NE)
        {
            var ao = a.ToObject();
            var bo = b.ToObject();
            var eq = Interpreter.AreEqual(ao, bo);
            return op == Opcode.EQ ? eq : !eq;
        }

        // Ordering, numeric compare via doubles (handles int↔double mixing). K==5 bools are
        // non-numeric, same as boxed bools were (K==3/!IsNumericObj), ordering returns false.
        if (a.K == 3 && !IsNumericObj(a.O) || b.K == 3 && !IsNumericObj(b.O) || a.K == 5 || b.K == 5)
            return false;
        var da = ToDoubleV(a);
        var db = ToDoubleV(b);
        return op switch
        {
            Opcode.LT => da < db,
            Opcode.GT => da > db,
            Opcode.EQ => Math.Abs(da - db) < Interpreter.Epsilon,
            Opcode.LE => da <= db,
            Opcode.GE => da >= db,
            Opcode.NE => Math.Abs(da - db) > Interpreter.Epsilon,
            _ => false
        };
    }

    private static bool IsNumericObj(object? v) => v is long or int or short or byte or double or float or decimal;

    private static double ToDoubleV(VmValue v) => v.K switch
    {
        // An UNSIGNED tag keeps its value in the SIGNED payload, so it must be widened back before any
        // numeric conversion: `ulong.MaxValue > 0` compared -1 against 0 and answered False
        // (crash_numeric_types, crash_type_check, probe-toplevel-vm section 17). ToObject already re-widens
        // (its `7 => (ulong)L` arm); this is the numeric path's half of the same rule.
        1 => v.T switch
        {
            5 => (uint)v.L,
            6 => (ushort)v.L,
            7 => unchecked((ulong)v.L),
            _ => v.L
        },
        2 => v.D,
        3 => v.O switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            double d => d,
            float f => f,
            decimal m => (double)m,
            char c => c,
            _ => Convert.ToDouble(v.O)
        },
        _ => 0.0
    };

    // Parity with the tree-walker's Interpreter.IsTruthy (incl. empty-string falsy,
    // '\0' char falsy, and EPSILON for float/double) so JMP_IF_FALSE / NOT / ternary
    // conditions evaluate identically to the tree-walker (P3-T1 VM-parity requirement).
    // The interpreter's tolerance itself, not a copy of its value: one constant for float/double equality and
    // truthiness across both engines, so the tree and the VM cannot drift apart.

    private static bool IsTruthyV(VmValue v) => v.K switch
    {
        0 => false,
        5 => v.L != 0,       // bool
        1 => v.L != 0,
        2 => Math.Abs(v.D) > Interpreter.Epsilon,
        _ => v.O switch
        {
            null => false,
            bool bb => bb,
            string { Length: > 0 } => true,
            string => false,
            long l => l != 0,
            int i => i != 0,
            short s => s != 0,
            byte by => by != 0,
            sbyte sb => sb != 0,
            uint ui => ui != 0,
            ushort us => us != 0,
            ulong ul => ul != 0,
            char c => c != '\0',
            double d => Math.Abs(d) > Interpreter.Epsilon,
            float f => Math.Abs(f) > Interpreter.Epsilon,
            decimal m => m != 0m,
            _ => true
        }
    };

    // ── Exact-size native-arg pool ──
    // VM CALL/CALL_VAL/CALL_METHOD pass an object?[] to natives. Allocation per call is
    // measurable on call-heavy loops. Exact-size arrays are REQUIRED (natives validate
    // args.Length, ArrayPool buckets would pad them, see the reverted 59e51a6). A
    // thread-static per-arity slot with a busy flag is safe: the array is only reused
    // after the synchronous callee returns, and a nested same-arity call while the slot
    // is busy allocates fresh (reentrancy through eval/RunScript is therefore safe).
    // Retention audit: Coroutine.Start copies via range slice, Task.Run uses [], and
    // BindParam copies values into the scope, no callee retains the incoming array.
    [ThreadStatic] private static object?[][]? _argPool;
    [ThreadStatic] private static int _argPoolBusy; // bit i set = arity-i slot in use

    [ThreadStatic] private static (string Name, object? Value)[][]? _pairPool;
    [ThreadStatic] private static int _pairPoolBusy; // bit i set = count-i slot in use

    // Exact-size pool for (string, object?)[] pairs (OBJ_INIT / WITH), managed element
    // type, so stackalloc is impossible; mirrors RentArgs' exact-length safety.
    private static (string Name, object? Value)[] RentPairs(int count)
    {
        if (count == 0) return Array.Empty<(string Name, object? Value)>();
        if (count is 1 or > 16) return new (string Name, object? Value)[count];
        var mask = 1 << count;
        if ((_pairPoolBusy & mask) != 0) return new (string Name, object? Value)[count];
        _pairPoolBusy |= mask;
        _pairPool ??= new (string Name, object? Value)[17][];
        return _pairPool[count] ??= new (string Name, object? Value)[count];
    }

    private static void ReturnPairs((string Name, object? Value)[] pairs)
    {
        var n = pairs.Length;
        if (n <= 16 && _pairPool != null && ReferenceEquals(pairs, _pairPool[n]))
            _pairPoolBusy &= ~(1 << n);
    }

    private static object?[] RentArgs(int count)
    {
        // Arity-1 allocations are cheaper than the pool bookkeeping in string-heavy
        // loops (ToString etc. allocate anyway), let them allocate fresh.
        // Arity-0 uses the shared empty array, zero allocation, never mutated.
        if (count == 0) return Array.Empty<object?>();
        if (count is 1 or > 16) return new object?[count];
        var mask = 1 << count;
        if ((_argPoolBusy & mask) != 0) return new object?[count];
        _argPoolBusy |= mask;
        _argPool ??= new object?[17][];
        // Prime the slot on first use, the outer array starts all-null, and a null
        // array reaching CALL/CALL_VAL/CALL_METHOD NREs on args.Length/indexing.
        return _argPool[count] ??= new object?[count];

    }

    private static void ReturnArgs(object?[] args)
    {
        var n = args.Length;
        if (n <= 16 && _argPool != null && ReferenceEquals(args, _argPool[n]))
            _argPoolBusy &= ~(1 << n);
    }
}
