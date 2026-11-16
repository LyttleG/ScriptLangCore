#!/usr/bin/env bash
# check-opcode-coverage.sh: every opcode must be known to the executor, the disassembler and the
# format table. The opcode set is stated several times in this tree (the enum, the executor's switch, the
# disassembler's operand and name tables, BytecodeFormat); this check is what stops them drifting apart
# silently. A missing entry does not crash, it yields a WRONG VALUE, so nothing else would notice.
#
# Note on what this check does and does not prove: it proves that every opcode is handled by the executor,
# by one of the disassembler's two tables, and by BytecodeFormat, and that the operand table decodes an
# operand wherever the format table says one exists, not that the operand *sizes* agree. Size agreement
# is proven by src/build/dump-bytecode.sh (the emitted bytes) and by the disassembler decoding real streams.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
fail=0
tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT

# 1. the enum, its members are the definition
sed -n '/internal enum Opcode/,/^}/p' src/Interpreter/Opcodes.cs \
  | sed 's|//.*||g' | tr ',' '\n' \
  | grep -oE '^[[:space:]]*[A-Z][A-Z0-9_]*' | tr -d ' ' | sort -u > "$tmp/enum.txt"

# 2. the executor's cases
grep -oE 'case Opcode\.[A-Z0-9_]+' src/Interpreter/Vm.cs | sed 's/case Opcode\.//' | sort -u > "$tmp/exec.txt"

# 3. the disassembler's TWO renderings, each read out of its OWN switch body. Scoping matters: Vm.cs also
#    holds executor-side `op switch` helpers (Arith, Bitwise, Compare, and two inline switches) whose arms
#    have the same `Opcode.X =>` shape. Reading the whole file let those satisfy this check for an opcode the
#    disassembler could no longer render: measured, not assumed: dropping Opcode.ADD from the name table
#    left the previous version of this check green, because Arith's arm still matched it.
dis_arms() {   # $1 = the switch header to start from
  awk -v hdr="$1" 'index($0, hdr) { f = 1 } f { print } f && /^[[:space:]]*};/ { exit }' src/Interpreter/Vm.cs
}
dis_arms 'private static string OpcodeMeaning(Opcode op) => op switch' \
  | grep -oE 'Opcode\.[A-Z0-9_]+' | sed 's/Opcode\.//' | sort -u > "$tmp/name.txt"
dis_arms 'var operandDecode = op switch' \
  | grep -oE 'Opcode\.[A-Z0-9_]+' | sed 's/Opcode\.//' | sort -u > "$tmp/operand.txt"

if [ ! -s "$tmp/name.txt" ] || [ ! -s "$tmp/operand.txt" ]; then
  echo "FAIL: could not read the disassembler's tables out of src/Interpreter/Vm.cs" >&2
  exit 1
fi

# The two tables partition the opcode set (name = operand-less, operand = operand-bearing), so each alone is
# legitimately partial, but together they must be total, and an opcode named by BOTH is a stale twin: one of
# the two renderings is lying about whether that opcode has operands.
comm -12 "$tmp/name.txt" "$tmp/operand.txt" > "$tmp/dis-both.txt"
if [ -s "$tmp/dis-both.txt" ]; then
  echo "FAIL: the disassembler renders these opcodes in BOTH its tables (one rendering is stale):" >&2
  sed 's/^/  /' "$tmp/dis-both.txt" >&2
  fail=1
fi
sort -u "$tmp/name.txt" "$tmp/operand.txt" > "$tmp/dis.txt"

# 4. the format table: one arm may group several opcodes (`Opcode.A or Opcode.B => ...`), so take every
#    Opcode.<NAME> inside the switch body instead of assuming one opcode per line.
sed -n '/Kind(Opcode op) => op switch/,/^    };/p' src/Interpreter/BytecodeFormat.cs \
  | sed 's|//.*||g' | grep -oE 'Opcode\.[A-Z0-9_]+' | sed 's/Opcode\.//' | sort -u > "$tmp/fmt.txt"

# 4b. …and WHICH kind each opcode got, because the operand table must decode an operand exactly where the
#     format table says one exists. Arms wrap over lines (`Opcode.A or Opcode.B` then `=> OperandKind.X`),
#     so opcodes accumulate until the line carrying the arm's `=> OperandKind.X`.
awk '
  /Kind\(Opcode op\) => op switch/ { f = 1; next }
  f && /^[[:space:]]*};/           { exit }
  f {
    line = $0; sub(/\/\/.*/, "", line)
    t = line
    while (match(t, /Opcode\.[A-Z0-9_]+/)) { pend[++n] = substr(t, RSTART + 7, RLENGTH - 7); t = substr(t, RSTART + RLENGTH) }
    if (line ~ /=>[[:space:]]*OperandKind\./) {
      k = line; sub(/.*=>[[:space:]]*OperandKind\./, "", k); sub(/[^A-Za-z0-9_].*/, "", k)
      for (i = 1; i <= n; i++) print k, pend[i]
      n = 0; delete pend
    }
  }
' src/Interpreter/BytecodeFormat.cs > "$tmp/kind.txt"
awk '$1 != "None" { print $2 }' "$tmp/kind.txt" | sort -u > "$tmp/withOperand.txt"

# 4c. the verifier's stack-effect table, the same shape (an arm may group opcodes), and one arm per opcode
#     is required for the same reason as everywhere else here: the table's `_ => (0, 0)` default reads a
#     MISSING arm as "no effect at all", which is a wrong answer rather than a crash, so nothing else would
#     notice a table that quietly stopped covering the enum.
# The anchor names the method by its PREFIX only: it used to spell the whole parameter list, and when `Effect`
# gained a `constants` parameter this line silently produced an EMPTY set, so the check failed with "could not read
# the stack-effect table" while the table was fine. A prefix survives the next parameter, and the END anchor (the
# table's closing brace line) is what actually delimits the table.
sed -n '/Effect(Opcode op, byte\[\] code, int operandBase/,/^[[:space:]]*};/p' src/Interpreter/BytecodeVerifier.cs \
  | sed 's|//.*||g' | grep -oE 'Opcode\.[A-Z0-9_]+' | sed 's/Opcode\.//' | sort -u > "$tmp/effect.txt"

if [ ! -s "$tmp/enum.txt" ]; then
  echo "FAIL: could not read the Opcode enum out of src/Interpreter/Opcodes.cs" >&2
  exit 1
fi
if [ ! -s "$tmp/withOperand.txt" ]; then
  echo "FAIL: could not read the operand kinds out of src/Interpreter/BytecodeFormat.cs" >&2
  exit 1
fi
if [ ! -s "$tmp/effect.txt" ]; then
  echo "FAIL: could not read the stack-effect table out of src/Interpreter/BytecodeVerifier.cs" >&2
  exit 1
fi

report() {   # $1 = label, $2 = file of opcodes it knows, $3 = the set it must cover
  missing="$(comm -23 "$3" "$2")"
  if [ -n "$missing" ]; then
    echo "FAIL: $1 does not handle:" >&2
    echo "$missing" | sed 's/^/  /' >&2
    fail=1
  fi
}
report "the executor"     "$tmp/exec.txt"    "$tmp/enum.txt"
report "the disassembler" "$tmp/dis.txt"     "$tmp/enum.txt"
report "BytecodeFormat"   "$tmp/fmt.txt"     "$tmp/enum.txt"
report "the verifier's stack-effect table" "$tmp/effect.txt" "$tmp/enum.txt"
# The tie: an opcode the format table says carries an operand must be decoded by the operand table, or the
# disassembler under-renders it and a bytecode dump cannot see that operand change.
report "the disassembler's operand table (every opcode BytecodeFormat says carries an operand)" \
       "$tmp/operand.txt" "$tmp/withOperand.txt"

extra="$(comm -13 "$tmp/enum.txt" "$tmp/fmt.txt")"
if [ -n "$extra" ]; then
  echo "FAIL: BytecodeFormat declares opcodes the enum does not have:" >&2
  echo "$extra" | sed 's/^/  /' >&2
  fail=1
fi

# 5. reachability, an opcode the compiler never emits cannot be reached by any suite, and the dump
#    instrument never shows one, so its executor case and its disassembler arm sit unexercised and
#    invisible. The retired set is therefore NAMED in a registry, and this check is two-way: an opcode with
#    no emission site that is not listed fails, and a listed opcode that HAS one also fails (that entry has
#    become a lie, and a registry that cannot be wrong is a rubber stamp). `case Opcode.X:` label lines are
#    excluded, the constant folder's labels are not emission sites.
grep -v 'case Opcode\.' src/Interpreter/Compiler.cs \
  | grep -oE 'Opcode\.[A-Z0-9_]+' | sed 's/Opcode\.//' | sort -u > "$tmp/emits.txt"
grep -vE '^[[:space:]]*(#|$)' src/build/opcode-not-emitted.txt | awk '{print $1}' | sort -u > "$tmp/retired.txt"

comm -23 "$tmp/enum.txt" "$tmp/emits.txt" > "$tmp/no-emit.txt"     # no emission site at all
unlisted="$(comm -23 "$tmp/no-emit.txt" "$tmp/retired.txt")"
if [ -n "$unlisted" ]; then
  echo "FAIL: no emission site in Compiler.cs, and not listed in src/build/opcode-not-emitted.txt:" >&2
  echo "$unlisted" | sed 's/^/  /' >&2
  fail=1
fi
stale="$(comm -13 "$tmp/no-emit.txt" "$tmp/retired.txt")"
if [ -n "$stale" ]; then
  echo "FAIL: listed as never emitted, but Compiler.cs now emits these:" >&2
  echo "$stale" | sed 's/^/  /' >&2
  fail=1
fi

# 6. the const operand: the width is stated ONCE and every index is emitted BY ROLE. Two things must stay
#    true, and each was measured as a real failure before it was fixed: a raw `(byte)` cast on a const index
#    leaves the emitted stream one byte wide whatever the declared width says, so the engine refuses its OWN
#    output ("constant index 4864 is outside a table of 1"), and a SECOND statement of the width lets the reader
#    and the writer disagree silently. A const index goes through `EmitConstIdx`/`ConstBytes` (writer) and
#    `Const`/`ConstAt`/`SkipConst` (reader), all bound to `BytecodeFormat.ConstWidthBytes`.
rawConst="$(grep -rnE '\(byte\)[[:space:]]*(ctx\.)?(AddConst|ConstIdx|ConstBytes)\(' src --include=*.cs || true)"
if [ -n "$rawConst" ]; then
  echo "FAIL: a const index is emitted with a raw (byte) cast; use BytecodeWriter.EmitConstIdx/ConstBytes:" >&2
  echo "$rawConst" | sed 's/^/  /' >&2
  fail=1
fi
widthDefs="$(grep -rn 'ConstWidthBytes[[:space:]]*=' src --include=*.cs || true)"
widthCount="$(printf '%s\n' "$widthDefs" | grep -c . || true)"
if [ "$widthCount" != "1" ]; then
  echo "FAIL: the const operand's width must be stated exactly once (BytecodeFormat.ConstWidthBytes), found $widthCount:" >&2
  printf '%s\n' "$widthDefs" | sed 's/^/  /' >&2
  fail=1
fi

if [ "$fail" -eq 0 ]; then
  echo "ok: all $(wc -l < "$tmp/enum.txt") opcodes are known to the executor, the disassembler, the format" \
       "table and the verifier's stack-effect table; the operand table decodes all $(wc -l < "$tmp/withOperand.txt")" \
       "opcodes the format table gives an operand; the only opcodes with no emission site are the" \
       "$(wc -l < "$tmp/retired.txt") that src/build/opcode-not-emitted.txt names; and the const operand's width is" \
       "stated once with every index emitted by role"
fi
exit "$fail"
