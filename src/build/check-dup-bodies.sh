#!/usr/bin/env bash
# Two methods whose bodies are identical after normalization are a repetition, and this repository treats every
# one as something to COLLAPSE or to JUSTIFY BY NAME. The rule flags a group with >= 2 members unless every
# (file, name) in it appears in src/build/dup-bodies-known.txt, so a new repetition fails the gate instead of being
# invisible, and an accepted one carries its reason in the file rather than in someone's memory.
#
# The dichotomy that shipped before this rule is still what the registry is FOR: a same-file, same-name group
# reads as an overload set (Clear x4, Initialize x3) and a different-name or different-file group reads as a
# copy, and both were invisible to a rule keyed on the file alone. What changed is that "an overload set" is no
# longer an automatic pass: it is a pass only when the registry says why that group cannot be hoisted.
#
# Two traps the collector had to be taught, both measured: awk splits on LF only, so `$0 can carry a CR that
# defeats a ")"-at-end test; and a declaration whose `{` never follows (an interface member, a partial
# signature) must be DISCARDED rather than collected, or its collector stays open and swallows every method
# after it, which makes the whole check report nothing.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

# THE FLOOR is one named value, never a literal at the test. It counts lines of the body INCLUDING the body
# braces, so the real quantity is statements plus two. A one-statement body is three and is noise, and the
# floor sits above it by choice. FOUR is the chosen value (the maintainer call): at 5 two real repetitions
# stay invisible, and at 3 a one-statement body is noise. Override it to A/B the value without editing this
# file, for example MIN_BODY_LINES=5 bash src/build/check-dup-bodies.sh
MIN_BODY_LINES=${MIN_BODY_LINES:-4}

known=$(mktemp); trap 'rm -f "$known"' EXIT
awk -F'\t' '!/^#/ && NF >= 2 { print $1 "\t" $2 }' src/build/dup-bodies-known.txt | sort -u > "$known"

found=$(find src -name '*.cs' -print0 | sort -z | xargs -0 awk -v minlines="$MIN_BODY_LINES" '
BEGIN { sq = sprintf("%c", 39); dq = sprintf("%c", 34) }
{
  line = $0
  sub(/\r$/, "", line)
  if (pending) {
    pending = 0
    if (line ~ /^[ \t]*\{/) { collecting = 1; count = 0; depth = 0; started = 0; body = "" }
  }
  else if (!collecting && line ~ /^[ \t]*(public|private|protected|internal)[ \t]/ && line ~ /\)[ \t]*$/ && line !~ /=>/) {
    sig = line
    sub(/[ \t]*\(.*$/, "", sig)
    sub(/^[ \t]*(public|private|protected|internal)[ \t]+/, "", sig)
    n = split(sig, parts, /[ \t]+/)
    name = parts[n]
    dfile = FILENAME; dline = FNR
    if (line ~ /\{/) { collecting = 1; count = 0; depth = 0; started = 0; body = "" }
    else pending = 1
  }
  if (collecting) {
    t = line
    sub(/^[ \t]+/, "", t); sub(/[ \t\r]+$/, "", t)
    if (t != "" && t !~ /^\/\// && t !~ /^\/\*/ && t !~ /^\*/) { body = body "\x01" t; count++ }
    b = line; o = line
    # Braces inside a LITERAL are not structure: split on the quote characters and keep the odd fields,
    # so a char literal such as a brace does not move the depth. Measured: the ClassDeclaration in Parser.cs
    # produced a phantom duplicate of itself this way, and the registry had begun accepting it by name.
    net = 0
    m1 = split(line, q1, dq)
    for (q = 1; q <= m1; q += 2) {
      m2 = split(q1[q], q2, sq)
      for (r = 1; r <= m2; r += 2) { seg = q2[r]; net += gsub(/\{/, "", seg) - gsub(/\}/, "", seg) }
    }
    depth += net
    if (line ~ /\{/) started = 1
    if (started && depth <= 0) {
      if (count >= minlines) {
        rel = FILENAME; sub(/^src\//, "", rel)
        print body "\t" rel "\t" name "\t" dline
      }
      collecting = 0
    }
  }
}' | awk -F"\t" -v known="$known" '
BEGIN { while ((getline k < known) > 0) { accepted[k] = 1 } }
{
  key = $1 "\x01" $2 "\x01" $3
  if (!(key in seen)) { seen[key] = 1; total[$1]++ }
  if (!(($2 "\t" $3) in accepted)) denied[$1] = 1
  row = $2 " line " $4 "  " $3
  if (which[$1] == "") which[$1] = row; else which[$1] = which[$1] "\n  " row
}
END { for (b in total) if (total[b] >= 2 && (b in denied)) print which[b] }')

if [ -n "$found" ]; then
  echo "FAIL: identical method bodies that are neither collapsed nor listed in src/build/dup-bodies-known.txt:"
  echo "$found"
  echo "  Collapse them onto one implementation, or record why the copy is the right thing."
  exit 1
fi
echo "ok: every identical-body group is collapsed, or accepted by name with a reason"