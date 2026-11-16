#!/usr/bin/env bash
# check-doc-bridge-c.sh, the Bridge page's C block IS the shipped C carrier, verbatim.
#
# Why this exists. The C section of `docs/bridge/howto.html` used to carry a COPY of a scratch program that lived only
# under the gitignored `setup\output\`, so the two had already drifted: the page still showed printf, main and a
# `gcc -O1 -o bridge_host.exe` line while the file that actually built was CRT-free, had its own entry point and needed
# two flags the page never mentioned. The carrier ships as `bindings\bridge_host.c` and the page's block is regenerated
# from it, which fixes the state once; this check is what keeps it fixed, because a doc block that only a human compares
# drifts again the first time somebody edits one side.
#
# What it checks: the `<pre><code>` block inside the page's `#c` section, compared byte for byte against
# `bindings/bridge_host.c` after the file is escaped the way the page must escape it (&, <, >). Line endings are not
# part of the comparison (the file and the page are both LF, and a CRLF copy of the carrier would be a different
# problem), quotes are NOT escaped on the page, which is why only those three characters are transformed.
#
# Usage: bash src/build/check-doc-bridge-c.sh [page]
# The optional argument (default: docs/bridge/howto.html) exists so this check can be tested against a known-bad copy,
# the same way the other doc checks take one.
# Exit: 0 the two agree, 1 they differ (first differing line named with both texts), 2 a file or the section is missing.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2

PAGE="${1:-docs/bridge/howto.html}"
SRC=bindings/bridge_host.c

[ -f "$SRC" ] || { echo "FAIL: no such file: $SRC (the shipped C carrier moved, and this check was not told)" >&2; exit 2; }
[ -f "$PAGE" ] || { echo "FAIL: no such file: $PAGE" >&2; exit 2; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# ── the page's block, RAW (still HTML-escaped) ───────────────────────────────────────────────────────────────────
# Found by the section heading rather than by the block's first line, so a change to the carrier's own header comment
# cannot make this check silently find nothing: the `#c` heading, then the first <pre><code> after it, up to the line
# that is exactly `</code></pre>`. The content starts on the same line as the opening tag, which is why the prefix is
# stripped rather than skipped.
awk '
  /^<h3 id="c">/ { insection = 1; next }
  insection && !inblock && /^<pre><code>/ { inblock = 1; sub(/^<pre><code>/, ""); print; next }
  inblock && /^<\/code><\/pre>$/ { inblock = 0; insection = 0; done = 1; next }
  inblock { print }
  END { if (!done) exit 3 }
' "$PAGE" > "$TMP/page.block" || {
  echo "FAIL: $PAGE has no \`#c\` section whose <pre><code> block closes with </code></pre> (the C section moved or the block lost its fence)" >&2
  exit 2
}

# The shape guards: a regex that stopped matching must fail loudly rather than compare two empty lists and report
# success, and the carrier is hundreds of lines with a CRT-free entry point that no other block has.
wc_block=$(grep -c . "$TMP/page.block" || true)
[ "${wc_block:-0}" -gt 100 ] ||
  { echo "FAIL: $PAGE's C block is $wc_block non-empty line(s), which is not the shipped carrier" >&2; exit 2; }
grep -q 'wmainCRTStartup' "$TMP/page.block" ||
  { echo "FAIL: $PAGE's C block does not define wmainCRTStartup, so it is not $SRC" >&2; exit 2; }

# ── the shipped carrier, escaped the way the page escapes it ─────────────────────────────────────────────────────
# sed, not awk: a `&` in an awk gsub replacement means "the matched text", which silently keeps `&amp;` as it is.
sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' "$SRC" > "$TMP/src.escaped"

# ── compare, naming the first line that differs ─────────────────────────────────────────────────────────────────
# The awk arm exits 1 when the two differ and the `|| exit 1` is load-bearing: without it the `ok:` line below ran on a
# failing comparison and answered 0, which is a check that cannot fail. Measured on a copy with one character changed.
awk -v page="$PAGE" -v src="$SRC" '
  NR == FNR { want[FNR] = $0; nw = FNR; next }
            { got[FNR] = $0; ng = FNR }
  END {
    if (nw != ng) {
      printf "FAIL: %s carries %d line(s) of %s and the page block carries %d: one side was edited without the other\n", page, nw, src, ng > "/dev/stderr"
      exit 1
    }
    diffs = 0
    for (i = 1; i <= nw; i++) {
      if (want[i] == got[i]) continue
      diffs++
      if (diffs <= 3) {
        printf "  line %d of the block\n", i > "/dev/stderr"
        printf "    %s says: %s\n", src, want[i] > "/dev/stderr"
        printf "    %s says: %s\n", page, got[i] > "/dev/stderr"
      }
    }
    if (diffs > 0) {
      printf "FAIL: %s and the C block of %s differ on %d line(s): the page must carry that file verbatim\n", src, page, diffs > "/dev/stderr"
      exit 1
    }
  }
' "$TMP/src.escaped" "$TMP/page.block" || exit 1

echo "ok: the C block of $PAGE is $SRC verbatim ($wc_block non-empty lines)"
