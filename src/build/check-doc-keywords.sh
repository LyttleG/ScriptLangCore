#!/usr/bin/env bash
# check-doc-keywords.sh, the two rules a KEYWORD owes: the page documents it, and its entry carries a sample.
#
# Both were found by auditing the page against the engine (2026-10-19), and both were PARTIALLY true:
#   * RULE 1 (documented): `check-doc-nav.sh` proves every ENTRY is linked, but a reader looks a keyword up BY NAME,
#     and sixteen keywords you can type had no entry at all, hence nothing in the sidebar: case, when, debugger,
#     true, false, void, new, get, set, init, implements, operator, in, and, or, not. Some keywords were only
#     reachable as the second half of a compound label (`if/else`, `try/catch/finally`, `async / await`), which is
#     fine and is what this rule accepts, because the page groups a construct rather than a token. The keywords are
#     read FROM THE LEXER, so a keyword added to the engine and not to the page is caught here.
#   * RULE 2 (a sample): a keyword entry with prose and no `.syntax` or `.example` block teaches nothing. The page had
#     0 of those when the rule was written, so this half guards the future: it catches an entry added in a hurry.
#
# How RULE 1 decides "documented": the keyword is covered when an ENTRY's id is the keyword or starts with it plus a
# dash (`case-when`, `bool-literals`), or its entry-name STARTS with the keyword followed by a non-word character
# (`if/else` covers `if`, `do/while` covers `do`), or the entry-name NAMES the keyword as a whole word (`and / or /
# not`, `get / set / init`, `true / false`).
#
# Usage: bash src/build/check-doc-keywords.sh [file] [lexer-file]
# The optional arguments (defaults: docs/language/index.html, src/Lexer/Lexer.cs) let this check run against a
# known-bad copy, which is how both rules were shown to bite.
set -u

FILE="${1:-docs/language/index.html}"
LEXER="${2:-src/Lexer/Lexer.cs}"
[ -f "$FILE" ] || { echo "FAIL: no such file: $FILE" >&2; exit 2; }
[ -f "$LEXER" ] || { echo "FAIL: no such file: $LEXER" >&2; exit 2; }

# ── the keywords the engine actually has ────────────────────────────────────────────────────────────────────
# `["kw"] = TokenType.X,` lines inside the Keywords dictionary, which is the ONE place a keyword is declared.
# Contextual words the PARSER owns are added here, because they are not in that table at all.
KEYWORDS=$(
  { sed -n '/Dictionary<string, TokenType> Keywords/,/^        };/p' "$LEXER" \
      | grep -o '\["[a-z]*"\]' | sed 's/\["//; s/"\]//'
    printf '%s\n' enum record implements interface extension do foreach match await async
  } | sort -u
)
[ -n "$KEYWORDS" ] || { echo "FAIL: no keywords read out of $LEXER (the table moved or was renamed)" >&2; exit 1; }

# ── one row per entry: id, name, tag, has-a-sample ──────────────────────────────────────────────────────────
# An entry's end is the next entry's start, so the row is emitted when the NEXT id arrives (and once at EOF).
TABLE=$(
  awk '
    function flush() { if (id != "") print id "\t" name "\t" tag "\t" sample }
    /data-entry-id="/ {
      flush()
      rest = substr($0, index($0, "data-entry-id=\"") + 15)
      id = substr(rest, 1, index(rest, "\"") - 1)
      name = ""; tag = 0; sample = 0
      next
    }
    /class="entry-name"/ {
      name = $0
      gsub(/<[^>]*>/, "", name)
      gsub(/^[ \t]+|[ \t]+$/, "", name)
      next
    }
    /class="entry-tag tag-keyword"/ { tag = 1; next }
    /class="syntax"|class="example"/ { sample = 1; next }
    END { flush() }
  ' "$FILE"
)
[ -n "$TABLE" ] || { echo "FAIL: no entries read out of $FILE (data-entry-id / entry-name changed)" >&2; exit 1; }

# ── RULE 1: every keyword is documented by some entry ──────────────────────────────────────────────────────
MISSING=$(printf '%s\n' "$TABLE" | awk -F'\t' -v kws="$KEYWORDS" '
  BEGIN {
    n = split(kws, K, "\n")
    for (i = 1; i <= n; i++) if (K[i] != "") want[K[i]] = 1
  }
  {
    id = $1; name = $2
    for (k in want) {
      if (seen[k]) continue
      if (id == k || index(id, k "-") == 1) { seen[k] = 1; continue }
      if (index(name, k) == 1) {
        rest = substr(name, length(k) + 1, 1)
        if (rest == "" || rest !~ /[A-Za-z0-9_]/) { seen[k] = 1; continue }
      }
      m = split(name, parts, /[^A-Za-z0-9_]+/)
      for (i = 1; i <= m; i++) if (parts[i] == k) { seen[k] = 1; break }
    }
  }
  END {
    for (k in want) if (!seen[k]) print k
  }
' | sort)

if [ -n "$MISSING" ]; then
  echo "FAIL: $FILE, keywords the engine declares and the page documents nowhere:" >&2
  printf '%s\n' "$MISSING" | sed 's/^/  /' >&2
  echo "  (a keyword is covered when an entry id or name names it, e.g. 'if/else' covers if and else)" >&2
  exit 1
fi

# ── RULE 2: every keyword entry carries a sample ───────────────────────────────────────────────────────────
# Only entries TAGGED keyword are judged: a namespace member's entry is a table row by design.
NOSAMPLE=$(printf '%s\n' "$TABLE" | awk -F'\t' '$3 == 1 && $4 == 0 { print $1 }')
if [ -n "$NOSAMPLE" ]; then
  echo "FAIL: $FILE, keyword entries with no sample (neither .syntax nor .example):" >&2
  printf '%s\n' "$NOSAMPLE" | sed 's/^/  /' >&2
  exit 1
fi

KWCOUNT=$(printf '%s\n' "$KEYWORDS" | grep -c .)
SCOUNT=$(printf '%s\n' "$TABLE" | awk -F'\t' '$3 == 1' | grep -c .)
echo "ok: all $KWCOUNT keywords the engine declares are documented, and all $SCOUNT keyword entries have a sample"
