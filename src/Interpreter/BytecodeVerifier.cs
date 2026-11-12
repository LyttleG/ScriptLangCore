namespace ScriptLangCore;

/// <summary>Checks a bytecode stream before it is allowed to execute. A loaded bundle is script INPUT: it
/// arrives from a file this interpreter did not compile, and the standing rule is that script input must
/// never kill the shipped binary. Without a check, a corrupted or hand-written body can index a table out
/// of range or jump into the middle of an instruction, where the executor reads operand bytes as though
/// they were opcodes, a wrong value at best, and on AOT a read past the end of the stream at worst.
///
/// What it checks, all of it against BytecodeFormat (the one declaration of each operand's shape):
///   * every instruction decodes, the opcode byte is a declared member, and its operand bytes fit;
///   * constant, local, global-slot and auxiliary-table indices lie inside their own tables. That covers the
///     COMPOUND operands too, which each carry a table index of their own: a FOREACH's loop-var type const,
///     and the capture list of a MAKE_LAMBDA, whose per-capture LOCAL index the executor reads as
///     `locals[slot]`. Both were unchecked, so such a body was ACCEPTED and only failed when it ran, as an
///     IndexOutOfRangeException attributed to the CALLER's line rather than to the byte that caused it. Each
///     field's offset comes from the shape table, never a literal;
///   * every jump target is in range AND lands on an instruction boundary. A target into the middle of an
///     instruction is exactly what a bounds check alone cannot see;
///   * the operand stack balances: no instruction consumes more values than the paths reaching it have
///     produced. This is the half that needs the per-opcode effect table (Effect below), and it runs over
///     the control-flow graph rather than down the byte stream, CheckStack says why, and says why an
///     instruction is allowed to be entered at more than one depth.
///
/// What it deliberately does NOT check: the per-invocation MEANING of an opcode, whether the values on the
/// stack are of the kind it expects (a callable where a callable is wanted, say). That is the executor's
/// business and is covered by the suites; the verifier's job is that the bytes cannot make it read or jump
/// somewhere the body never wrote.
///
/// The jump base is the executor's own: a relative target is measured from the byte AFTER the instruction,
/// i.e. the opcode's byte plus the operand SHAPE (BytecodeFormat.ShapeSize), which is what the executor
/// computes, every jump consumes its shape, then adds the offset.</summary>
internal static class BytecodeVerifier
{
    /// <summary>Returns null when the stream is sound, or a one-line reason that names the offset.</summary>
    public static string? Verify(Bytecode bc) => Verify(bc, out _, out _);

    /// <summary>Verifies the stream and reports the two facts a LOADED declaration has to carry but the wire does
    /// not: does the body contain a value YIELD, and does it contain an AWAIT. A body's flags are DERIVED from its
    /// own instructions for the same reason the bundle carries no flags byte at all: an `async` body suspends like a
    /// coroutine while producing no value yield, so `hasYield` alone cannot name it, and a loaded async body that was
    /// read as an ordinary one was FLAT-CALLED by the VM: it reached its first AWAIT, the suspension went to the
    /// invocation the caller had already stopped reading, and the run ended silently with exit 0 (measured on the
    /// row-5 repro, a `--no-source` bundle printing its statements before the wait and then nothing).</summary>
    public static string? Verify(Bytecode bc, out bool hasYield, out bool hasAwait)
    {
        hasYield = false;
        hasAwait = false;
        var code = bc.Code;
        if (code.Length == 0) return "the body is empty";

        var starts = new HashSet<int>();          // instruction boundaries, for the jump check
        var jumps = new List<(int From, int Target)>();
        var ip = 0;
        while (ip < code.Length)
        {
            var op = (Opcode)code[ip];
            if (!Enum.IsDefined(op)) return $"ip {ip}: 0x{code[ip]:x2} is not an opcode";
            if (op == Opcode.YIELD) hasYield = true;
            else if (op == Opcode.AWAIT) hasAwait = true;

            var kind = BytecodeFormat.Kind(op);   // total over the enum, and the opcode is defined
            // The fixed prefix must fit before a variable tail can be measured at all.
            var prefix = BytecodeFormat.OperandSize(kind);
            if (ip + BytecodeFormat.OpcodeWidthBytes + prefix > code.Length)
                return $"ip {ip}: {op} needs {prefix} operand byte(s), but the code ends at {code.Length}";
            var size = BytecodeFormat.OperandSizeAt(kind, code, ip + BytecodeFormat.OpcodeWidthBytes);
            if (ip + BytecodeFormat.OpcodeWidthBytes + size > code.Length)
                return $"ip {ip}: {op} needs {size} operand byte(s), but the code ends at {code.Length}";

            var r = new BytecodeReader(code, ip + BytecodeFormat.OpcodeWidthBytes);
            var reason = CheckOperands(bc, op, kind, r, ip);
            if (reason != null) return reason;
            if (kind is OperandKind.TargetU16 or OperandKind.TargetU16UnwindU8)
                jumps.Add((ip, ip + BytecodeFormat.OpcodeWidthBytes + size + r.I16At(0)));

            starts.Add(ip);
            ip += BytecodeFormat.OpcodeWidthBytes + size;
        }

        foreach (var (from, target) in jumps)
        {
            if (target < 0 || target > code.Length)
                return $"ip {from}: jump target {target} is outside the code (0..{code.Length})";
            if (target < code.Length && !starts.Contains(target))
                return $"ip {from}: jump target {target} is not an instruction boundary";
        }
        return CheckStack(code, bc.Constants, starts);
    }

    private static string? CheckOperands(Bytecode bc, Opcode op, OperandKind kind, BytecodeReader r, int ip)
    {
        switch (kind)
        {
            case OperandKind.ConstIdx:
                return CheckIndex(bc.Constants.Length, r.ConstAt(0), ip, $"{op} constant");
            case OperandKind.LocalU8:
                return CheckIndex(bc.Locals.Length, r.ByteAt(0), ip, $"{op} local");
            case OperandKind.SlotU8:
                return CheckIndex(bc.GlobalSlotNames?.Length ?? 0, r.ByteAt(0), ip, $"{op} global slot");
            case OperandKind.LambdaU8:
                return CheckLambdaOperands(bc, op, r, ip);
            default:
                // The remaining kinds are counts, jump targets and the COMPOUND shapes, whose const fields
                // are table indices like any other.
                return CheckCompoundConstFields(bc, op, kind, r, ip);
        }
    }

    /// <summary>MAKE_LAMBDA's two table indices: the nested-lambda index, which the executor reads with
    /// <c>bc.NestedLambdas[li]</c>, and then one LOCAL index per capture, which it reads with
    /// <c>locals[slot]</c>. Both were unchecked, so a corrupted bundle loaded cleanly and failed only when
    /// the body ran, as an IndexOutOfRangeException reported at the caller's line. The prefix and the pair
    /// width come from BytecodeFormat's own statement of the shape, so this walk and the size the verifier
    /// already measured cannot disagree.</summary>
    private static string? CheckLambdaOperands(Bytecode bc, Opcode op, BytecodeReader r, int ip)
    {
        var reason = CheckIndex(bc.NestedLambdas?.Length ?? 0, r.ByteAt(0), ip, $"{op} lambda");
        if (reason != null) return reason;
        var count = r.ByteAt(BytecodeFormat.U8WidthBytes);
        var pairs = BytecodeFormat.LambdaCapturePairsAt;
        for (var i = 0; i < count; i++)
        {
            var slot = r.ByteAt(pairs + i * BytecodeFormat.LambdaCapturePairSize);
            reason = CheckIndex(bc.Locals.Length, slot, ip, $"{op} captured local");
            if (reason != null) return reason;
        }
        return null;
    }

    /// <summary>Every CONST field a COMPOUND shape carries: FOREACH_NEXT's loop-var type const and
    /// FOREACH_CALL's. A shape's field OFFSET comes from <see cref="BytecodeFormat.FieldOffset"/>, derived
    /// from the shape's own field list, so a shape that gains a const field is covered here rather than at a
    /// literal nobody updated. Kinds with no fields answer no violation, which is the behaviour every count
    /// and jump target had before this existed.</summary>
    private static string? CheckCompoundConstFields(Bytecode bc, Opcode op, OperandKind kind, BytecodeReader r, int ip)
    {
        var fields = BytecodeFormat.Fields(kind);
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i] != OperandField.Const) continue;
            return CheckIndex(bc.Constants.Length, r.ConstAt(BytecodeFormat.FieldOffset(kind, i)), ip, $"{op} constant");
        }
        return null;
    }

    private static string? CheckIndex(int count, int idx, int ip, string what)
        => idx < count ? null : $"ip {ip}: {what} index {idx} is outside a table of {count}";

    /// <summary>Second half: stack-depth balance, over the control-flow graph. A worklist rather than a walk
    /// down the byte stream, because the fall-through after a terminator (RETURN, THROW, THROW_FLOW_*) is
    /// dead: in `if (c) { return 5; } x = 1;` the bytes after the RETURN are a jump target, and their depth
    /// must come from the jump, not from the bytes before them. So every instruction is entered with exactly
    /// one depth, a second entry with a different depth is a merge mismatch, and an entry short of the
    /// values an instruction consumes is an underflow. Either way the executor would read the operand stack
    /// out of step with the code that wrote it.
    ///
    /// Handler entries are the VM's own: the executor pushes the raised value before jumping to a catch
    /// target, so TRY's target starts one value deeper than the TRY, while a finally block is entered at the
    /// depth of its own TRY_FINALLY.</summary>
    private static string? CheckStack(byte[] code, object?[] constants, HashSet<int> starts)
    {
        var entry = new int[code.Length];
        Array.Fill(entry, int.MaxValue);                // shallowest depth each instruction has been entered with
        var work = new Stack<(int Ip, int Depth)>();
        void Edge(int target, int atDepth) => work.Push((target, atDepth));
        Edge(0, 0);
        while (work.Count > 0)
        {
            var (at, depth) = work.Pop();
            while (true)
            {
                if (at == code.Length) break;               // ran off the end, as a body with no RETURN does
                if (at < 0 || at > code.Length || !starts.Contains(at))
                    return $"ip {at}: the stack walk reached a byte that is not an instruction boundary";

                // Walk again only when this arrival is SHALLOWER than one already seen. Both depths are
                // legitimate: the compiler does emit a path that leaves an extra value on the stack, the
                // `??=` emission below used to drop the value on its keep side, so the two sides of
                // `x.p ??= v` reached the join one apart (that defect is fixed; the tolerance is what makes
                // this half robust to the next one). Only a shallower arrival can run an instruction short
                // of the values it consumes, which is the hazard here, so the walk meets on the minimum.
                if (depth >= entry[at]) break;
                entry[at] = depth;

                var op = (Opcode)code[at];
                var size = BytecodeFormat.OperandSizeAt(BytecodeFormat.Kind(op), code, at + BytecodeFormat.OpcodeWidthBytes);
                var (pops, pushes) = Effect(op, code, at + BytecodeFormat.OpcodeWidthBytes, constants);
                if (depth < pops)
                    return $"ip {at}: {op} needs {pops} value(s) on the operand stack, but {depth} is/are there";
                var after = depth - pops + pushes;
                depth = after;                              // what the next instruction in this run is entered with
                var next = at + BytecodeFormat.OpcodeWidthBytes + size;

                switch (op)
                {
                    case Opcode.JMP:
                        Edge(next + BytecodeReader.I16At(code, at + BytecodeFormat.OpcodeWidthBytes), after);
                        break;
                    case Opcode.JMP_IF_FALSE:
                        Edge(next + BytecodeReader.I16At(code, at + BytecodeFormat.OpcodeWidthBytes), after);
                        at = next;
                        continue;
                    case Opcode.TRY:
                        Edge(next + BytecodeReader.I16At(code, at + BytecodeFormat.OpcodeWidthBytes), after + 1);  // the raised value
                        at = next;
                        continue;
                    case Opcode.TRY_FINALLY:
                        Edge(next + BytecodeReader.I16At(code, at + BytecodeFormat.OpcodeWidthBytes), after);
                        at = next;
                        continue;
                    case Opcode.FOREACH_NEXT:
                        // A loop head that is two things at once: falling through pushes the element for
                        // the body, while running out of elements leaves at its own end offset with
                        // nothing pushed. Hence two depths from one instruction, and hence the worklist.
                        Edge(next + BytecodeReader.I16At(code, at + BytecodeFormat.OpcodeWidthBytes + BytecodeFormat.FieldOffset(OperandKind.U16Const, 0)), after - 1);
                        at = next;
                        continue;
                    case Opcode.FOREACH_CALL:
                        // Same shape, with the end offset after argCount, stateLocal and varTypeConstIdx.
                        Edge(next + BytecodeReader.I16At(code, at + BytecodeFormat.OpcodeWidthBytes + BytecodeFormat.FieldOffset(OperandKind.U8U8ConstU16, 3)), after - 1);
                        at = next;
                        continue;
                    case Opcode.RETURN or Opcode.THROW or Opcode.THROW_FLOW_RETURN
                        or Opcode.THROW_FLOW_BREAK or Opcode.THROW_FLOW_CONTINUE:
                        break;                              // dead fall-through; any target was queued above
                    default:
                        at = next;
                        continue;
                }
                break;
            }
        }
        return null;
    }

    /// <summary>Net operand-stack effect of one instruction: how many values it consumes and how many it
    /// leaves. Read off the EXECUTOR's fast path for each case, deliberately not off the number of Pop() and
    /// Push() calls in the case, which counts every branch (coercion, slow path, error, cleanup) and so is
    /// not the effect: ADD's case calls Pop four times and Push five, and still pops two and pushes one.
    ///
    /// Where a case left room for doubt the pair chosen is the one that leaves the stack HIGHER, so a wrong
    /// entry can only fail to catch an unbalanced body, never refuse a sound one. RETURN is the clearest
    /// example: it pops only if a value is there. tools/check-opcode-coverage.sh requires an arm for every
    /// opcode, so the default below is unreachable and the table cannot silently fall behind the enum.</summary>

    private static (int Pops, int Pushes) Effect(Opcode op, byte[] code, int operandBase, object?[] constants)
    {
        var n = operandBase < code.Length ? code[operandBase] : 0;   // the count an operand-driven opcode carries
        // A MARKER is not a value on the operand stack: the interpreter call handlers pop it (CALL_METHOD and
        // CALL_NEW read it off the top and never push it back), so counting its LOAD_CONST as a push left the
        // model one deeper than the runtime after every generic call and refused bodies that run correctly.
        // Measured: the sweep refused two suites with "needs 1 value(s) on the operand stack, but 0 is/are
        // there" while the same bodies produced the right answer. A wrong entry may fail to catch an unbalanced
        // body, never refuse a sound one.
        if (op == Opcode.LOAD_CONST)
        {
            // The marker test reads the CONST through the role-named peek, not a raw byte: a byte read here
            // would recognise a marker only while the pool fits in one byte, and a marker it failed to see
            // would be counted as a PUSH (the very mistake this arm exists to prevent).
            var ci = operandBase < code.Length ? new BytecodeReader(code, operandBase).ConstAt(0) : -1;
            if (ci >= 0 && ci < constants.Length && constants[ci] is TypeArgsMarker or NamedArgsMarker or ArgFlagsMarker)
                return (0, 0);
        }

        return op switch
        {
            // No operand-stack involvement: bookkeeping that lives in side tables (handlers, foreach state),
            // line tracking, and NOP (the one opcode the compiler never emits, kept as an alignment slot).
            Opcode.LOC or Opcode.NOP or Opcode.JMP or Opcode.NEW_LIST
                or Opcode.TRY or Opcode.ENDTRY or Opcode.TRY_FINALLY or Opcode.POP_FINALLY or Opcode.END_FINALLY
                or Opcode.THROW_FLOW_BREAK or Opcode.THROW_FLOW_CONTINUE or Opcode.FOREACH_END => (0, 0),
            Opcode.RENEW_LOCAL => (0, 0),   // a fresh capture cell in the slot: no operand-stack change

            Opcode.LOAD_CONST or Opcode.LOAD_VAR or Opcode.LOAD_REF or Opcode.DUP
                or Opcode.LOAD_GLOBAL_FAST or Opcode.LOAD_SUPER
                or Opcode.MAKE_LAMBDA or Opcode.INC or Opcode.DEC => (0, 1),

            Opcode.STORE_VAR or Opcode.STORE_CONST or Opcode.STORE_GLOBAL_FAST
                or Opcode.POP or Opcode.DISPOSE or Opcode.LIST_APPEND or Opcode.THROW
                or Opcode.FOREACH_INIT or Opcode.JMP_IF_FALSE => (1, 0),

            Opcode.NEG or Opcode.BITNOT or Opcode.NOT or Opcode.WIDEN_NUM or Opcode.CAST or Opcode.AS
                or Opcode.MAKE_INDEX or Opcode.PAT_LIST or Opcode.COMP_FINISH or Opcode.CATCH_MATCH
                or Opcode.ATTACH_TUPLE_NAMES => (1, 1),

            Opcode.ADD or Opcode.SUB or Opcode.MUL or Opcode.DIV or Opcode.MOD
                or Opcode.BITAND or Opcode.BITOR or Opcode.BITXOR or Opcode.SHL or Opcode.SHR
                or Opcode.LT or Opcode.GT or Opcode.EQ or Opcode.LE or Opcode.GE or Opcode.NE
                or Opcode.ARRAY_GET or Opcode.MAKE_RANGE or Opcode.REL_PATTERN or Opcode.GET_PROP
                or Opcode.EVENT_ADD or Opcode.EVENT_SUB or Opcode.IS_TYPE => (2, 1),

            Opcode.MATCH_EQ => (2, 1),                          // pattern + subject → the verdict, like REL_PATTERN
            Opcode.TRY_GET_PROP => (2, 2),                      // member value AND a found flag on top
            Opcode.SLICE or Opcode.SET_PROP => (3, 1),

            // The count comes from the opcode's own operand.
            Opcode.CALL or Opcode.CALL_VAL or Opcode.CALL_NEW or Opcode.NEW_STRUCT
                => (n + 1, 1),                                  // name / callee / type name, then n arguments
            Opcode.CALL_METHOD => (n + 2, 1),                   // target + name + n arguments
            Opcode.DESTRUCT => (1, n),                          // one source, n bindings
            Opcode.MAKE_LIST => (n + 1, 1),                     // element type + n items
            Opcode.MAKE_TUPLE => (n, 1),
            Opcode.MAKE_DICT => (2 * n, 1),
            Opcode.OBJ_INIT or Opcode.WITH => (1 + 2 * n, 1),   // target + n name/value pairs
            Opcode.GET_INDEX => (n + 1, 1),                     // target + n indices
            Opcode.SET_INDEX => (n + 2, 1),                     // target + n indices + the value
            Opcode.FOREACH_NEXT => (2, 1),                      // state + index, then the element for the body
            Opcode.FOREACH_CALL => (n + 1, 1),                  // the callee, then its arguments
            Opcode.NEW_ARRAY => (1 + code[operandBase] + code[operandBase + 1], 1),  // type + sizes + inits

            Opcode.RETURN or Opcode.THROW_FLOW_RETURN => (0, 0),  // the value is popped only if present
            Opcode.YIELD => (1, 0),
            // An await pops its awaitable and the RESUME pushes the awaited value, so the linear model is a pop
            // and a push: the expression that holds the await keeps its operand either way.
            Opcode.AWAIT => (1, 1),

            _ => (0, 0),   // unreachable: check-opcode-coverage.sh requires an arm for every opcode
        };
    }
}