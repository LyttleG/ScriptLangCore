#!/usr/bin/env bash
# check-doc-nav.sh, EVERY documented entry must be reachable from the sidebar, IN ITS OWN SECTION'S GROUP.
#
# The class it guards, met for real twice, so the rule has two halves.
#
# 1. REACHABILITY (the original rule). The sidebar in docs/language/index.html is a HAND-KEPT list of
#    `<a href="#...">` links, while the entries are written per category. An entry added to the page was
#    therefore visible in its section and INVISIBLE in the navigation, and nothing else in the tree reads that
#    file, so it shipped again unnoticed. Measured when this check was written: 39 of the 315 entries had no link,
#    which is the whole `interface` entry, all eleven bitwise members, all thirteen REPL commands, all eight
#    sqlite members, `beep`, `ClientStatus`, `InputBox`, `ListView`, `TypeInfo`, the `CLR interfaces` entry and
#    the page's own `Overview`.
# 2. GROUPING (added 2026-11-03, reported as "the sidebar list is incomplete and does not reflect the help
#    content"). Every entry then HAD a link, so reachability was green while the sidebar still lied: its groups
#    were a hand-kept SHAPE that no longer matched the page, and three sections had no group of their own
#    (`TCP Networking` and `UI Subsystem (Win32)` shared one bucket called "Networking & UI", and the whole
#    `Sqlite Namespace` section was filed under "Namespaces & Utils"). The sidebar is now GENERATED from the page:
#    one `<h3>` per `<section id="cat-...">`, carrying that section's `<h2>` title, with its entries in page order.
#    So the rule is checkable, and it is what this check now enforces: an entry's link must sit in the group whose
#    heading equals the title of the SECTION the entry lives in, exactly once.
#
# How: the page is read as a sequence of `<section id="cat-...">` blocks, each with its `<h2>` title and its
# `data-entry-id` entries (in page order). The sidebar is read as a sequence of `<h3>heading</h3>` groups followed by
# their `href="#..."` targets. Then: every entry has exactly one link, and it is in its own section's group. A
# sidebar link MAY also target a sub-heading inside a section (the deep links `#replayable-memoize`, `#CallFunc`,
# `#ReadStruct`, `#SizeOfStruct`, the `Iterable` members), which is legitimate navigation and is ignored here.
# Whether a link's target EXISTS is check-doc-structure's half of the pair, deliberately not repeated.
#
# Usage: bash src/build/check-doc-nav.sh [file]
# The optional argument (default: docs/language/index.html) exists so this check can be tested against a
# known-bad copy.
set -u

FILE="${1:-docs/language/index.html}"
[ -f "$FILE" ] || { echo "FAIL: no such file: $FILE" >&2; exit 2; }

ENTRY_COUNT=$(grep -c 'data-entry-id="' "$FILE")
if [ "$ENTRY_COUNT" -eq 0 ]; then
  echo "FAIL: $FILE, no data-entry-id found: the page is not the language reference, or the attribute was renamed" >&2
  exit 1
fi

# The page, as "section-title<TAB>entry-id" pairs, in page order. awk keeps the state: the section's <h2> title is
# remembered until the next section starts, and every data-entry-id line after it is attributed to it.
PAGE=$(awk '
  /<section[^>]*id="cat-/ { insection = 1; title = ""; next }
  insection && title == "" && /<h2[^>]*>/ {
    line = $0
    sub(/.*<h2[^>]*>/, "", line); sub(/<\/h2>.*/, "", line)
    gsub(/<[^>]*>/, "", line); gsub(/&#x25BC;/, "", line)
    gsub(/^[ \t]+|[ \t]+$/, "", line)
    title = line
    next
  }
  insection && /data-entry-id="/ {
    line = $0
    sub(/.*data-entry-id="/, "", line); sub(/".*/, "", line)
    print title "\t" line
  }
' "$FILE")

[ -n "$PAGE" ] || { echo "FAIL: $FILE, no section/entry pairs found: the section markup was renamed" >&2; exit 1; }

# The sidebar, as "group-heading<TAB>target" pairs. A link before any <h3> is reported rather than silently kept.
SIDEBAR=$(awk '
  /<nav class="sidebar">/ { innav = 1; next }
  /<\/nav>/ { innav = 0 }
  !innav { next }
  /<h3>/ {
    line = $0
    sub(/.*<h3>/, "", line); sub(/<\/h3>.*/, "", line)
    gsub(/^[ \t]+|[ \t]+$/, "", line)
    heading = line
    next
  }
  /href="#[^"]*"/ {
    line = $0
    n = split(line, parts, "href=\"#")
    for (i = 2; i <= n; i++) {
      target = parts[i]; sub(/".*/, "", target)
      print heading "\t" target
    }
  }
' "$FILE")

[ -n "$SIDEBAR" ] || { echo "FAIL: $FILE, the sidebar block is empty or its markup was renamed" >&2; exit 1; }

FAILED=0

# 1. every entry has a link somewhere
MISSING=$(comm -23 <(cut -f2 <<<"$PAGE" | sort -u) <(cut -f2 <<<"$SIDEBAR" | sort -u))
if [ -n "$MISSING" ]; then
  echo "FAIL: $FILE, entries a reader cannot reach from the sidebar:" >&2
  printf '%s\n' "$MISSING" | sed 's/^/  /' >&2
  FAILED=1
fi

# 2. and it sits in the group of its OWN section, exactly once
MISPLACED=$(awk -F'\t' '
  NR == FNR { want[$2] = $1; next }
  {
    seen[$2]++
    if (($2 in want) && $1 != want[$2]) {
      printf "%s : in the group \"%s\", but its section is \"%s\"\n", $2, $1, want[$2]
    }
  }
  END {
    for (id in seen) if (seen[id] > 1) printf "%s : linked %d times (one link per entry)\n", id, seen[id]
  }
' <(printf '%s\n' "$PAGE") <(printf '%s\n' "$SIDEBAR") | sort)

if [ -n "$MISPLACED" ]; then
  echo "FAIL: $FILE, sidebar links that do not match the page's own sections:" >&2
  printf '%s\n' "$MISPLACED" | sed 's/^/  /' >&2
  FAILED=1
fi

# 3. AND EVERY SIDEBAR LINK LANDS ON A REAL ANCHOR, in the MARKUP (added 2026-11-07).
# Reported from a live read: clicking `MCI` or `Com` in the sidebar did nothing, and the section could not be seen at all.
# The cause is that the page's anchors were created by its SCRIPT (`script.js` copies each `data-entry-id` onto the
# element), so with the script not running there was nothing for `href="#Com"` to land on, and every entry body stayed
# collapsed (`.entry-body { display: none }` until `.open`), which reads as "the section is masked". Every entry now
# carries its own id in the markup and a deep link opens it through `:target`; this rule keeps that true, and it is the
# half check-doc-structure cannot see (that one accepts a link matching an ENTRY id, which needs the script).
IDS=$(grep -o ' id="[^"]*"' "$FILE" | sed 's/^ id="//; s/"$//' | sort -u)
LANDING=$(awk -F'\t' '
  NR == FNR { ids[$1] = 1; next }
  {
    target = $2
    sub(/^#/, "", target)
    if (!(target in ids)) printf "%s : the sidebar links to #%s, and the page has no id=\"%s\" to land on\n", $1, target, target
  }
' <(printf '%s\n' "$IDS") <(printf '%s\n' "$SIDEBAR") | sort)

if [ -n "$LANDING" ]; then
  echo "FAIL: $FILE, sidebar links with nothing to land on (they need a real id in the markup, not one a script makes):" >&2
  printf '%s\n' "$LANDING" | sed 's/^/  /' >&2
  FAILED=1
fi

[ "$FAILED" -eq 0 ] || exit 1

# NOT `GROUPS`: that name is a bash special variable (the caller's group ids), so an assignment to it is replaced by
# the shell's own array at expansion time and the count printed as a concatenation of real group ids (197609 here).
NENTRIES=$(cut -f2 <<<"$PAGE" | sort -u | grep -c .)
NGROUPS=$(cut -f1 <<<"$SIDEBAR" | sort -u | grep -c .)
echo "ok: all $NENTRIES entries in $FILE are reachable from the sidebar, each in its own section's group ($NGROUPS group(s))"
