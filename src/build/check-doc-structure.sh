#!/usr/bin/env bash
# check-doc-structure.sh, every entry in the language reference must live inside a category's entry-list.
#
# The class it guards, met for real: the sqlite category's `entry-list` was closed one entry early, so the
# entry appended after it (UiDesign) became a direct child of `<section class="category">`. Nothing crashes,
# the page still loads, but that entry loses the list's styling and the search/collapse script stops finding
# it where it looks, and nothing else in the tree reads docs/language/index.html at all, so it would ship
# again unnoticed.
#
# How: a stack walk over the div/section tags IN ORDER (several can share a line, so the tags are pulled out
# one per line first). An entry is well placed exactly when the innermost open div is an entry-list; the stack
# must also be empty at the end, with no stray close, or the nesting is broken somewhere above.
#
# Usage: bash src/build/check-doc-structure.sh [file]
# The optional argument (default: docs/language/index.html) exists so this check can be tested against a
# known-bad copy. It was: run against the version that shipped the bug above, it reports it at the entry's own
# line; run against the fixed file, it is green.
set -u
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$ROOT" || exit 2
FILE="${1:-docs/language/index.html}"
[ -f "$FILE" ] || { echo "FAIL: no such file: $FILE" >&2; exit 1; }

# -v file= (not FILENAME): the tags arrive on stdin, where awk's FILENAME is "-", and the ok line must name the
# file it checked, the FAIL path below names it from the shell, which is why that one was never affected.
grep -no '<div[^>]*>\|</div>\|<section[^>]*>\|</section>' "$FILE" | awk -F: -v file="$FILE" '
  BEGIN { n = 0; entries = 0; errs = 0 }
  function top()   { return n > 0 ? stack[n] : "nothing open" }
  function report(ln, msg) { errs++; if (errs <= 5) printf "  line %s: %s\n", ln, msg > "/dev/stderr" }
  {
    ln = $1
    tag = substr($0, index($0, ":") + 1)
    if (tag ~ /^<section/) { stack[++n] = "section"; next }
    if (tag ~ /^<\/section>/) {
      if (top() == "section") n--
      else report(ln, "</section> closes " top())
      next
    }
    if (tag ~ /^<div/) {
      c = ""
      if (match(tag, /class="[^"]*"/)) c = substr(tag, RSTART + 7, RLENGTH - 8)
      if (c == "entry") {
        entries++
        if (top() != "entry-list")
          report(ln, "entry is a direct child of " top() ", not of an entry-list")
        stack[++n] = "entry"
        next
      }
      stack[++n] = (c == "entry-list" ? "entry-list" : "div")
      next
    }
    if (tag ~ /^<\/div>/) {
      t = top()
      if (t == "entry" || t == "entry-list" || t == "div") n--
      else report(ln, "</div> closes " t)
      next
    }
  }
  END {
    if (errs > 5) printf "  … and %d more\n", errs - 5 > "/dev/stderr"
    if (errs > 0) exit 1
    if (n != 0) {
      printf "  unclosed at EOF: %d tag(s), innermost is %s\n", n, stack[n] > "/dev/stderr"
      exit 1
    }
    printf "ok: all %d entries in %s sit inside an entry-list; div/section tags balance\n", entries, file
  }
' || {
  echo "FAIL: $FILE, entries outside an entry-list, or unbalanced div/section tags (above)" >&2
  exit 1
}

# ── anchors: every href="#x" must resolve to an entry id or a plain id ──
# The class this guards, met for real: the table of contents linked `#CallFunc` while no entry carried that
# id (the function is described in a WinApi table row, which the TOC cannot point at), so clicking it in the
# shipped page does nothing. The tag-balance walk above cannot see that, it never looks at hrefs, and
# nothing else in the tree reads this file.
IDS="$( { grep -o 'data-entry-id="[^"]*"' "$FILE"; grep -o ' id="[^"]*"' "$FILE"; } | sed 's/.*="//; s/"$//' | sort -u )"
MISSING="$(grep -o 'href="#[^"]*"' "$FILE" | sed 's/href="#//; s/"$//' | sort -u | while read -r target; do
  printf '%s' "$IDS" | grep -Fxq "$target" || echo "$target"
done)"
if [ -n "$MISSING" ]; then
  echo "FAIL: $FILE, TOC links to ids that do not exist (a reader lands nowhere):" >&2
  echo "$MISSING" | sed 's/^/  #/' >&2
  exit 1
fi
echo "ok: every href=\"#...\" in $FILE resolves to an entry id or a plain id"
