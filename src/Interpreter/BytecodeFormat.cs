namespace ScriptLangCore;

/// <summary>The shape of an opcode's operand bytes, the ONE place a layout is declared.
///
/// Derived from evidence, not from memory: the Opcode enum's own operand comments (Opcodes.cs), the
/// executor's reads (Vm.cs, `code[ip++]` for an operand, `Pop()` for a stack value) and the
/// disassembler's operand decode (Vm.cs DecodeOperand). Three opcodes were corrected against that evidence
/// after the first draft got them wrong: IS_TYPE and TRY_GET_PROP pop everything they need and take NO
/// operand, while WIDEN_NUM does read one byte. Two more followed once the BytecodeVerifier walked real
/// bundles: GET_PROP and SET_PROP pop the property name from the operand stack and take NO operand either.</summary>
/// <summary>The ONE statement of the CONSTANT operand's width. Widening the const index (v10 made it 2 bytes
/// because a growing program body's pool crossed 255 and the 1-byte operand declined that body SILENTLY, which is
/// what refused --no-source for it) is THIS number plus the two operand helpers that read and write it, rather
/// than a hunt through the twelve executor arms that name a const. It lives here, on the format, and NOT in the
/// kind's name, which is why the member is ConstIdx rather than ConstU8: a kind that names its width has to be
/// renamed to widen.
/// </summary>
internal static partial class BytecodeFormat
{
    public const int ConstWidthBytes = 2;

    /// <summary>The width of a single-byte FIELD (a local index, a slot, a table index, a count).
    /// <see cref="OpcodeWidthBytes"/> is that same byte: an instruction's opcode is one.</summary>
    public const int U8WidthBytes = 1;

    /// <summary>The opcode byte's width. Unlike a FIELD (see <see cref="Fields"/>) this is not declared per
    /// kind: EVERY instruction in this format is one opcode byte followed by its operand shape. A consumer
    /// that measures from "the byte after the opcode" asks for it by name rather than writing `+ 1`, so a
    /// patch site reads as arithmetic on the SHAPE and nothing else.</summary>
    public const int OpcodeWidthBytes = U8WidthBytes;

    /// <summary>The width every 2-byte FIELD shares (a jump target, a LOC component, a FOREACH end). Declared
    /// once rather than per kind, for the same reason <see cref="OpcodeWidthBytes"/> is: what a shape declares
    /// is which FIELDS it has, and the width is a property of the field's kind.</summary>
    public const int U16WidthBytes = 2;

    /// <summary>Bits in a byte, what the little-endian packing shifts by, so that arithmetic reads as
    /// widths rather than as a magic 8.</summary>
    public const int ByteBits = 8;

    /// <summary>Where a MAKE_LAMBDA's capture PAIRS begin, counted from the operand base: the fixed prefix of
    /// two one-byte fields, the nested-lambda index and the capture count. This is the ONE statement of that
    /// arithmetic, asked by <see cref="OperandSize"/>'s variable form and by the verifier's capture walk, so a
    /// reader cannot count from a different place than the writer.</summary>
    public const int LambdaCapturePairsAt = 2 * U8WidthBytes;

    /// <summary>One capture PAIR's width: a LOCAL index and a written flag, both one byte. The index is a table
    /// index like any other (the executor reads the frame's locals with it), which is why the verifier checks
    /// each one against the body's local count.</summary>
    public const int LambdaCapturePairSize = 2 * U8WidthBytes;

    /// <summary>Packs a value LITTLE-ENDIAN into `dest`, whose LENGTH is the width, and unpacks one back.
    /// THE one place this format turns a value into bytes or back: the CONST operand, a U16 field and a jump
    /// target all compose from it, so no reader or writer restates a width OR a byte order. Every reader and
    /// writer walks a width instead of branching on it (`width == 1 ? one byte : two` is what these were, and
    /// that branch silently kept the old width the day the CONST operand grew).</summary>
    public static void PackLe(int value, Span<byte> dest)
    {
        for (var i = 0; i < dest.Length; i++) dest[i] = (byte)((value >> (ByteBits * i)) & 0xFF);
    }

    /// <summary>The inverse of <see cref="PackLe"/>.</summary>
    public static int UnpackLe(ReadOnlySpan<byte> src)
    {
        var v = 0;
        for (var i = 0; i < src.Length; i++) v |= src[i] << (ByteBits * i);
        return v;
    }

    /// <summary>A CONST index packed into fresh bytes, for the emission sites that build a multi-operand
    /// instruction in one collection expression.</summary>
    public static byte[] ConstBytes(int idx)
    {
        var bytes = new byte[ConstWidthBytes];
        PackLe(idx, bytes);
        return bytes;
    }
}

internal enum OperandKind
{
    None,              // no operand
    ConstIdx,          // index into Bytecode.Constants, BytecodeFormat.ConstWidthBytes wide
    LocalU8,           // 1-byte index into Bytecode.Locals
    SlotU8,            // 1-byte per-invocation global slot index
    LambdaU8,          // lambda index, capture count, then count × (local index, written flag), VARIABLE
    CountU8,           // 1-byte count, or a small selector (REL_PATTERN, MAKE_INDEX, MAKE_RANGE)
    TargetU16,         // 2-byte little-endian relative jump target
    TargetU16UnwindU8, // 2-byte target + 1-byte handler-unwind count (THROW_FLOW_BREAK/CONTINUE)
    U8x2,              // two 1-byte operands (NEW_ARRAY: sizes, inits)
    U8x3,              // three 1-byte operands (FOREACH_INIT: stateLocal, idxLocal, allowEnumerable)
    U16Const,          // 2 bytes + a CONST index (FOREACH_NEXT: endLo, endHi, varTypeConstIdx)
    U8U8ConstU16,      // 1+1+CONST+2 (FOREACH_CALL: argCount, stateLocal, varTypeConstIdx, endLo, endHi)
    LocU8x4,           // LOC: line lo, line hi, col lo, col hi
}

/// <summary>One FIELD of an operand SHAPE. A compound shape is declared as a list of these (see
/// <see cref="BytecodeFormat.Fields"/>), so its size, each field's OFFSET and the reading order are DERIVED rather
/// than restated. The binary used to carry a shape as arithmetic (`2 + ConstWidthBytes`, `3 + ConstWidthBytes`) and
/// its consumers as literals (`ByteAt(code, ip + 2)`, `ip += 3`), which is why widening the const operand left the
/// readers disagreeing with the writer: the engine refused its OWN freshly compiled body (`LOAD_CONST constant
/// index 4864 is outside a table of 1`). Add a shape to the table, never an offset at a call site.</summary>
internal enum OperandField { U8, U16, Const }

/// <summary>The bytecode format, declared once. Every Opcode member MUST have an arm here,
/// tools/check-opcode-coverage.sh fails otherwise, naming the opcode, and that check also fails if this
/// table invents an opcode the enum does not have.
///
/// What the check does NOT prove is that OperandSize matches the executor's own advance for each opcode:
/// that agreement shows up as a mis-decoded stream, which is why the byte-identical dump
/// (tools/dump-bytecode.sh) and the disassembler's reads are the other half of the evidence.</summary>
internal static partial class BytecodeFormat
{
    public static OperandKind Kind(Opcode op) => op switch
    {
        // ---- no operand (they act on values already on the stack) ----
        Opcode.NOP or Opcode.ADD or Opcode.SUB or Opcode.MUL or Opcode.DIV or Opcode.NEG
            or Opcode.LT or Opcode.GT or Opcode.EQ or Opcode.LE or Opcode.GE or Opcode.NE
            or Opcode.RETURN or Opcode.DUP or Opcode.POP or Opcode.NOT or Opcode.IS_TYPE
            or Opcode.MOD
            or Opcode.BITAND or Opcode.BITOR or Opcode.BITXOR or Opcode.SHL or Opcode.SHR or Opcode.BITNOT
            or Opcode.ARRAY_GET or Opcode.SLICE or Opcode.DISPOSE or Opcode.MATCH_EQ or Opcode.COMP_FINISH
            or Opcode.LOAD_SUPER or Opcode.ENDTRY or Opcode.POP_FINALLY or Opcode.THROW
            or Opcode.TRY_GET_PROP or Opcode.YIELD or Opcode.AWAIT
            // GET_PROP/SET_PROP carry NO operand byte: the property name rides the operand stack (the
            // emitter pushes LOAD_CONST before them, the executor Pop()s it). This table first declared them
            // ConstIdx, and the BytecodeVerifier exposed the error by advancing one byte too far and reading
            // the NEXT instruction's opcode as a constant index, which is how the export suites failed.
            or Opcode.GET_PROP or Opcode.SET_PROP
            => OperandKind.None,

        // ---- constant index (ConstWidthBytes wide, see that constant) ----
        Opcode.LOAD_CONST
            or Opcode.CAST or Opcode.AS or Opcode.PAT_LIST or Opcode.CATCH_MATCH
            or Opcode.EVENT_ADD or Opcode.EVENT_SUB or Opcode.ATTACH_TUPLE_NAMES
            or Opcode.WIDEN_NUM
            => OperandKind.ConstIdx,

        // ---- 1-byte local index ----
        Opcode.LOAD_VAR or Opcode.STORE_VAR or Opcode.INC or Opcode.DEC or Opcode.LOAD_REF or Opcode.RENEW_LOCAL
            or Opcode.STORE_CONST or Opcode.NEW_LIST or Opcode.LIST_APPEND or Opcode.FOREACH_END
            => OperandKind.LocalU8,

        // ---- 1-byte global slot index (per invocation, not a constant) ----
        Opcode.LOAD_GLOBAL_FAST or Opcode.STORE_GLOBAL_FAST => OperandKind.SlotU8,

        // ---- lambda index + capture count + count × (local index, written flag) ----
        Opcode.MAKE_LAMBDA => OperandKind.LambdaU8,

        // ---- 1-byte count or selector ----
        Opcode.CALL or Opcode.GET_INDEX or Opcode.SET_INDEX or Opcode.CALL_METHOD or Opcode.CALL_VAL
            or Opcode.CALL_NEW or Opcode.NEW_STRUCT or Opcode.MAKE_TUPLE or Opcode.DESTRUCT
            or Opcode.OBJ_INIT or Opcode.WITH or Opcode.MAKE_LIST or Opcode.MAKE_DICT
            or Opcode.MAKE_INDEX or Opcode.MAKE_RANGE or Opcode.REL_PATTERN
            or Opcode.THROW_FLOW_RETURN
            => OperandKind.CountU8,

        // ---- 2-byte little-endian relative target ----
        Opcode.JMP or Opcode.JMP_IF_FALSE or Opcode.TRY or Opcode.TRY_FINALLY or Opcode.END_FINALLY
            => OperandKind.TargetU16,

        // ---- 2-byte target + 1-byte unwind count ----
        Opcode.THROW_FLOW_BREAK or Opcode.THROW_FLOW_CONTINUE => OperandKind.TargetU16UnwindU8,

        // ---- fixed multi-operand shapes ----
        Opcode.NEW_ARRAY => OperandKind.U8x2,
        Opcode.FOREACH_INIT => OperandKind.U8x3,
        Opcode.FOREACH_NEXT => OperandKind.U16Const,
        Opcode.FOREACH_CALL => OperandKind.U8U8ConstU16,
        Opcode.LOC => OperandKind.LocU8x4,

        _ => throw new ArgumentOutOfRangeException(nameof(op), op,
                 "opcode has no declared operand shape, add it here and to tools/check-opcode-coverage.sh's expectations"),
    };

    /// <summary>One FIELD of an operand SHAPE. A compound shape is declared as a list of these, so its size, each
    /// field's OFFSET and the reading order are all DERIVED rather than restated. The binary used to carry the shape
    /// as arithmetic (`2 + ConstWidthBytes`, `3 + ConstWidthBytes`) and its consumers as literals
    /// (`ByteAt(code, ip + 2)`, `ip += 3`), which is why widening the const operand left the readers disagreeing
    /// with the writer: the engine refused its OWN freshly compiled body (`LOAD_CONST constant index 4864 is outside
    /// a table of 1`). Add a shape here, never an offset at a call site.
    /// The array returned is SHARED and must be READ, never written to: it is the table entry itself, and the
    /// callers are the readers of a layout rather than its editors.</summary>
    public static OperandField[] Fields(OperandKind kind) => kind switch
    {
        OperandKind.U16Const => ShapeU16Const,
        OperandKind.U8U8ConstU16 => ShapeU8U8ConstU16,
        OperandKind.TargetU16UnwindU8 => ShapeTargetU16UnwindU8,
        OperandKind.U8x2 => ShapeU8x2,
        OperandKind.U8x3 => ShapeU8x3,
        OperandKind.LocU8x4 => ShapeLocU8x4,
        _ => NoFields,
    };

    // ONE array per shape, built at type init and SHARED. These used to be collection expressions inside the
    // switch, so EVERY `Fields` call allocated, and the callers are all hot: `OperandSize`/`ShapeSize`/
    // `FieldOffset` are asked PER INSTRUCTION by the verifier's own stack walk (a LOC is one, and LOC is one
    // per statement), by the strip's instruction walk, and by the compiler's patch sites. A caller READS a
    // shape and never writes to it, which is what lets one array serve every call.
    private static readonly OperandField[] ShapeU16Const = [OperandField.U16, OperandField.Const];
    private static readonly OperandField[] ShapeU8U8ConstU16 = [OperandField.U8, OperandField.U8, OperandField.Const, OperandField.U16];
    private static readonly OperandField[] ShapeTargetU16UnwindU8 = [OperandField.U16, OperandField.U8];
    private static readonly OperandField[] ShapeU8x2 = [OperandField.U8, OperandField.U8];
    private static readonly OperandField[] ShapeU8x3 = [OperandField.U8, OperandField.U8, OperandField.U8];
    private static readonly OperandField[] ShapeLocU8x4 = [OperandField.U16, OperandField.U16];
    private static readonly OperandField[] NoFields = [];

    /// <summary>The width of one field, each kind asking the single width constant it belongs to.</summary>
    public static int FieldSize(OperandField f) => f switch
    {
        OperandField.U8 => U8WidthBytes,
        OperandField.U16 => U16WidthBytes,
        _ => ConstWidthBytes,
    };

    /// <summary>The byte offset of a field within its shape: what a consumer that used to write `ip + 2` asks for.
    /// A field index past the shape answers the shape's total, which is the position of the NEXT instruction.</summary>
    public static int FieldOffset(OperandKind kind, int fieldIndex)
    {
        var fields = Fields(kind);
        var sum = 0;
        for (var i = 0; i < fieldIndex && i < fields.Length; i++) sum += FieldSize(fields[i]);
        return sum;
    }

    /// <summary>The whole shape's width, summed from its fields.</summary>
    public static int ShapeSize(OperandKind kind)
    {
        var sum = 0;
        foreach (var f in Fields(kind)) sum += FieldSize(f);
        return sum;
    }

    /// <summary>A FIELD's byte counted from the START of the INSTRUCTION, opcode included, what a consumer
    /// patches or reads when it holds the opcode's own position. <see cref="FieldOffset"/> counts from the
    /// operand base instead.</summary>
    public static int FieldAt(OperandKind kind, int fieldIndex) => OpcodeWidthBytes + FieldOffset(kind, fieldIndex);

    /// <summary>The whole INSTRUCTION for a FIXED shape: its opcode byte plus its operand shape. A relative jump's
    /// target is measured from the END of the instruction, and the executor advances by the same total, so the
    /// compiler's patch arithmetic asks for THIS rather than adding the opcode byte itself.
    /// A shape with a VARIABLE tail has no size a caller holding only the KIND can measure, so asking for one here is
    /// REFUSED rather than answered with the fixed prefix. That refusal is the point: the strip's own instruction walk
    /// asked THIS for an arbitrary opcode and advanced 3 bytes over a MAKE_LAMBDA whose real size is 3 + 2 per capture,
    /// read the capture list as opcodes and desynced (measured on `crash_udf_leak`, whose capturing lambdas made the
    /// walk stop with "bad op=108 at ip=1704 len=1947" and turned a strippable file into a refusal). A walker must ask
    /// <see cref="InstructionSizeAt"/> with the position it sits at.</summary>
    public static int InstructionSize(OperandKind kind)
        => HasVariableSize(kind)
            ? throw new ArgumentException(
                $"{kind} has a variable tail, so its size needs the code it sits in: ask InstructionSizeAt",
                nameof(kind))
            : OpcodeWidthBytes + OperandSize(kind);

    /// <summary>True when an operand shape's size depends on the CODE it sits in (a count followed by that many
    /// fields), so a consumer holding only the kind cannot measure its instruction.</summary>
    public static bool HasVariableSize(OperandKind kind) => kind == OperandKind.LambdaU8;

    /// <summary>The whole INSTRUCTION for a shape that MAY have a variable tail: the opcode, the fixed prefix and
    /// whatever the count declares. This is the ONE measurement for an opcode decoded from a stream, the way
    /// <see cref="OperandSizeAt"/> is the one for an operand, so a walker cannot decode a body differently from the
    /// writer that produced it. `operandBase` is the byte AFTER the opcode.</summary>
    public static int InstructionSizeAt(OperandKind kind, byte[] code, int operandBase)
        => OpcodeWidthBytes + OperandSizeAt(kind, code, operandBase);

    /// <summary>The CONST index an instruction carries, for a consumer holding the part-written
    /// <see cref="List{Byte}"/> the compiler appends to (the executor reads the finished array through
    /// <see cref="BytecodeReader.Const"/>). It walks the width rather than branching on it, so a compiler
    /// PEEPHOLE that recognises a `LOAD_CONST` cannot keep a stale width of its own: a literal one-byte read
    /// here is what silently stopped every constant fold the day the operand was widened (the guard above it
    /// compared the two instructions' span against 4 and rejected all of them).</summary>
    public static int ReadConstIdx(List<byte> code, int operandBase)
    {
        var v = 0;
        for (var i = 0; i < ConstWidthBytes; i++) v |= code[operandBase + i] << (ByteBits * i);
        return v;
    }

    /// <summary>The finished-array twin of <see cref="ReadConstIdx"/>, for a consumer holding the emitted
    /// body (the verifier's marker test, the disassembler) rather than the part-written list the compiler
    /// appends to. <see cref="BytecodeReader"/> composes its own constant peeks from this one.</summary>
    public static int ReadConst(byte[] code, int operandBase)
        => UnpackLe(code.AsSpan(operandBase, ConstWidthBytes));

    /// <summary>Total operand bytes following the opcode. The executor's own advance (ip += n) is the
    /// authority this must agree with; a mismatch mis-decodes the stream rather than crashing, which is
    /// why the byte-identical dump is part of the evidence.</summary>
    public static int OperandSize(OperandKind kind) => kind switch
    {
        OperandKind.None => 0,
        OperandKind.LocalU8 or OperandKind.SlotU8
            or OperandKind.CountU8 => U8WidthBytes,
        OperandKind.ConstIdx => ConstWidthBytes,
        OperandKind.TargetU16 or OperandKind.U8x2 => U16WidthBytes,
        OperandKind.LambdaU8 => LambdaCapturePairsAt,   // the fixed PREFIX (lambda index + capture count); the capture pairs follow
        OperandKind.U16Const or OperandKind.U8U8ConstU16 or OperandKind.U8x3
            or OperandKind.LocU8x4 or OperandKind.TargetU16UnwindU8 => ShapeSize(kind),

        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "undeclared operand size"),
    };

    /// <summary>The operand bytes at `operandBase` (the byte after the opcode). Fixed kinds are the table's
    /// own size; MAKE_LAMBDA is the one VARIABLE shape, index, capture count, then count × (local index,
    /// written flag), so its count byte decides. The executor reads exactly that many, which is why a
    /// consumer that trusted the fixed TableU8 size advanced one byte and mis-read the capture pairs as the
    /// next instruction (it saw LOAD_CONST with an index past the end of an empty constant table).</summary>
    public static int OperandSizeAt(OperandKind kind, byte[] code, int operandBase)
        => kind == OperandKind.LambdaU8
            // The fixed prefix (the lambda index and the capture count), then one PAIR per capture, a local
            // index and a written flag. Both numbers come from the one statement of that layout, which the
            // verifier's capture walk also asks.
            ? LambdaCapturePairsAt
              + LambdaCapturePairSize * BytecodeReader.ByteAt(code, operandBase + U8WidthBytes)
            : OperandSize(kind);
}

/// <summary>The only encoder: extension methods on the List&lt;byte&gt; the compiler already appends to, so no
/// call site gains an object or a layer, these inline to the same bytes the hand-written Adds produced.
///
/// This owns the byte-order decision and the 2-byte patch, which used to be written out by hand at every
/// site (294 raw code.Add calls, no operand encode/decode helper anywhere in the tree).</summary>
internal static class BytecodeWriter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Emit(this List<byte> code, Opcode op) => code.Add((byte)op);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitU8(this List<byte> code, int v) => code.Add((byte)v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void EmitConstIdx(this List<byte> code, int idx)
    {
        Span<byte> bytes = stackalloc byte[BytecodeFormat.ConstWidthBytes];
        BytecodeFormat.PackLe(idx, bytes);
        foreach (var b in bytes) code.Add(b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// <summary>A CONST index as BYTES, for the emission sites that build a multi-operand instruction in one
    /// collection expression. The width comes from the one constant, so a widened const operand reaches those sites
    /// too: the alternative, a raw (byte) cast, which is what 56 sites did, silently kept the old width and made
    /// the engine refuse its own freshly compiled output (LOAD_CONST constant index 4864 is outside a table of 1).
    /// </summary>
    public static byte[] ConstBytes(this List<byte> code, int idx) => BytecodeFormat.ConstBytes(idx);

    public static void EmitLocalIdx(this List<byte> code, int idx) => code.Add((byte)idx);

    public static void EmitU16(this List<byte> code, int v)
    {
        Span<byte> bytes = stackalloc byte[BytecodeFormat.U16WidthBytes];
        BytecodeFormat.PackLe(v, bytes);
        foreach (var b in bytes) code.Add(b);
    }

    /// <summary>Write a U16 field's bytes over an operand already emitted. The width and the byte order both
    /// come from <see cref="BytecodeFormat.PackLe"/>, so a patch site names neither.</summary>
    public static void PatchU16(this List<byte> code, int pos, int value)
    {
        Span<byte> bytes = stackalloc byte[BytecodeFormat.U16WidthBytes];
        BytecodeFormat.PackLe(value, bytes);
        for (var i = 0; i < bytes.Length; i++) code[pos + i] = bytes[i];
    }
}

/// <summary>The only decoder: one implementation of every operand read, shared by the executor (which
/// consumes as it goes) and the disassembler (which peeks, because the dispatch loop owns `ip`).
///
/// A readonly ref struct over the existing byte[], so the executor's hot loop gains no object and its
/// codegen is unchanged. The byte-order decision (little-endian) lives here and nowhere else: before this,
/// the executor's 50 consuming reads and the disassembler's 59 peeks each shifted bytes by hand.
///
/// Bounds are deliberately NOT checked here. The disassembler's existing guards stay where they are, and the
/// executor already relies on a well-formed stream; range guards belong at the single encoder together with
/// the BytecodeVerifier that Phase 2 puts on the bundle load path. Adding them here first would change what
/// a malformed stream does while nothing yet checks the stream.</summary>
internal readonly ref struct BytecodeReader
{
    private readonly byte[] _code;
    private readonly int _ip;      // operand base: the byte AFTER the opcode

    public BytecodeReader(byte[] code, int ip) { _code = code; _ip = ip; }

    /// <summary>The operand base, for a caller that formats an absolute target from a relative offset.</summary>
    public int Ip => _ip;

    /// <summary>Stream length, so a peeking caller can bound itself without owning the array.</summary>
    public int Length => _code.Length;

    /// <summary>Operand byte `off` past the base, the disassembler's read: peeks, never consumes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ByteAt(int off) => ByteAt(_code, _ip + off);

    /// <summary>2-byte little-endian operand `off` past the base.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int U16At(int off) => U16At(_code, _ip + off);

    /// <summary>A CONST index `off` past the base, the PEEKING twin of <c>Const</c>, its width from the same one
    /// place, so the verifier checks a widened const operand without restating a width of its own.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ConstAt(int off) => BytecodeFormat.ReadConst(_code, _ip + off);

    /// <summary>2-byte little-endian operand `off` past the base, signed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short I16At(int off) => (short)U16At(_code, _ip + off);

    // ---- the ONE implementation of each read; the executor's consuming reads compose from these ----

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ByteAt(byte[] code, int ip) => code[ip];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int U16At(byte[] code, int ip)
        => BytecodeFormat.UnpackLe(code.AsSpan(ip, BytecodeFormat.U16WidthBytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short I16At(byte[] code, int ip) => (short)U16At(code, ip);

    /// <summary>Consume one operand byte.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte U8(byte[] code, ref int ip) => code[ip++];

    /// <summary>Consume a CONSTANT index, the read named for the ROLE, not for today's width, so widening the
    /// operand is `BytecodeFormat.ConstWidthBytes` and this one method. It walks the width rather than branching
    /// on it, so the role-named spelling is what keeps the const-bearing executor arms from restating a width.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Const(byte[] code, ref int ip)
    {
        var v = BytecodeFormat.ReadConst(code, ip);
        ip += BytecodeFormat.ConstWidthBytes;
        return v;
    }

    /// <summary>Skip a constant index without reading it, its width coming from the same one place.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SkipConst(byte[] code, ref int ip) => ip += BytecodeFormat.ConstWidthBytes;

    /// <summary>Consume a 2-byte little-endian operand as signed, the jump-offset form.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short I16(byte[] code, ref int ip)
    { var v = I16At(code, ip); ip += 2; return v; }

    /// <summary>Consume a 2-byte little-endian operand as unsigned.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int U16(byte[] code, ref int ip)
    { var v = U16At(code, ip); ip += 2; return v; }
}
