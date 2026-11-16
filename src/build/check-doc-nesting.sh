#!/usr/bin/env bash
# check-doc-nesting.sh, THE PAGE'S MARKUP MUST NEST THE WAY IT IS READ, entry by entry.
#
# The class it guards, met for real 2026-11-07: two entries were DEAD in the sidebar. Clicking `MCI` or `Com` opened
# them (the script added `.open`) and the page did not move, and the sections could not be seen at all, which a reader
# reported as "nothing happens, it seem masked". The cause was one missing `</table>` inside the `Http` entry: the HTML
# parser kept that table open, so the rest of `Http` AND the next two entries became its children, and an entry inside
# a closed entry's `.entry-body` is `display: none`.
#
# Neither existing check could see it: `check-doc-structure.sh` balances div/section tags (the divs were balanced) and
# `check-doc-nav.sh` asks whether every entry HAS a sidebar link (it did). So this check asks the two questions the
# browser answers:
#   1. does every tracked tag close before the next one that closes over it, and does anything stay open at the end;
#   2. is every entry a direct child of an entry-list, and not inside another entry.
#
# How: one awk pass over the file keeps a stack of open tags. A close for a tag that is not the top of the stack names
# the tags it skipped, which is exactly the "one missing close swallows the rest" shape; anything left on the stack at
# EOF is named as never closed. `p`, `li`, `tr`, `td`, `th`, `option` and the table sections are NOT tracked, because a
# browser closes them itself; the tags that swallow content when unclosed are.
#
# Usage: bash src/build/check-doc-nesting.sh [file]
# The optional argument (default: docs/language/index.html) exists so the check can be run against a known-bad copy.

set -u
FILE="${1:-docs/language/index.html}"
if [ ! -f "$FILE" ]; then
  echo "FAIL: no such file: $FILE" >&2
  exit 2
fi

OUT=$(awk '
  BEGIN {
    split("div table section nav main ul ol pre code a textarea", t, " ")
    for (i in t) TRACK[t[i]] = 1
    split("p li tr td th option thead tbody tfoot", a, " ")
    for (i in a) AUTO[a[i]] = 1
    depth = 0
    nested = 0
    notInList = 0
  }
  {
    line = $0
    lineno = FNR
    while (match(line, /<\/?[A-Za-z][A-Za-z0-9]*[^>]*>/)) {
      tagtext = substr(line, RSTART, RLENGTH)
      line = substr(line, RSTART + RLENGTH)

      closing = (substr(tagtext, 1, 2) == "</")
      name = tagtext
      sub(/^<\/?/, "", name)
      sub(/[ \t\/>].*$/, "", name)
      name = tolower(name)
      if (substr(tagtext, RLENGTH - 1, 2) == "/>") continue

      if (!closing) {
        if (!(name in TRACK)) continue
        cls = ""
        if (match(tagtext, /class="[^"]*"/)) { cls = substr(tagtext, RSTART + 7, RLENGTH - 8) }
        id = ""
        if (match(tagtext, /data-entry-id="[^"]*"/)) { id = substr(tagtext, RSTART + 15, RLENGTH - 16) }
        depth++
        stackTag[depth] = name
        stackCls[depth] = cls
        stackId[depth] = id
        stackLine[depth] = lineno
        if (name == "div" && cls ~ /(^| )entry( |$)/ && id != "") {
          entries++
          for (k = depth - 1; k >= 1; k--) {
            if (stackId[k] != "") {
              printf "%s (L%d) sits INSIDE %s (L%d), so it is hidden while that entry is closed\n", id, lineno, stackId[k], stackLine[k]
              nested++
              break
            }
          }
          if (!(stackCls[depth - 1] ~ /(^| )entry-list( |$)/)) {
            printf "%s (L%d) is not a direct child of an entry-list (its parent is <%s class=\"%s\">)\n", id, lineno, stackTag[depth - 1], stackCls[depth - 1]
            notInList++
          }
        }
        continue
      }

      if (name in AUTO) continue
      if (!(name in TRACK)) continue
      found = 0
      for (k = depth; k >= 1; k--) { if (stackTag[k] == name) { found = k; break } }
      if (found == 0) continue
      for (k = depth; k > found; k--) {
        printf "L%d: <%s%s%s> is still open when L%d closes </%s>\n", stackLine[k], stackTag[k], (stackId[k] != "" ? " " stackId[k] : ""), "", lineno, name
        problems++
      }
      depth = found - 1
    }
  }
  END {
    for (k = 1; k <= depth; k++) {
      printf "L%d: <%s%s> is never closed\n", stackLine[k], stackTag[k], (stackId[k] != "" ? " " stackId[k] : "")
      problems++
    }
    if (nested == 0 && notInList == 0 && problems == 0) printf "ok: nothing swallowed (entries=%d)\n", entries
  }
' "$FILE")

if printf '%s' "$OUT" | grep -qv '^ok: '; then
  echo "FAIL: $FILE, the markup nests differently from how it is read:" >&2
  printf '%s\n' "$OUT" | sed 's/^/  /' >&2
  exit 1
fi

echo "ok: every entry in $FILE sits in its own entry-list and no tag is left open ($(printf '%s' "$OUT" | sed -n 's/.*entries=\([0-9]*\).*/\1/p') entries)"
