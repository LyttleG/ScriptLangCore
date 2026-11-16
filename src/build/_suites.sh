#!/usr/bin/env bash
# _suites.sh, the ONE statement of where the suites live and how one is found by name.
#
# The gate's suites are a TREE (assets/language/repl/<area>/<suite>.slc), while every registry, baseline and
# report keys them by BARE FILENAME. That is why discovery lives here rather than in each tool: before this
# file, nine tools each spelled `assets/language/repl/*.slc` themselves, so a layout change was a nine-file
# edit that could be forgotten in one of them (the suite would then silently stop running, which is the exact
# failure check-suite-accounting.sh exists for).
#
# A basename is UNIQUE by contract (check-suite-accounting.sh refuses a tree with two suites of the same
# name), which is what lets a registry name a suite without a path.
[ -n "${SUITE_ROOT:-}" ] || SUITE_ROOT="assets/language/repl"

# Every suite, in a stable PATH order, so a group's suites run together and two runs agree.
suite_files() {
  find "$SUITE_ROOT" -type f -name '*.slc' | LC_ALL=C sort
}

# The suites whose BASENAME contains a filter (the --filter semantics every tool shares).
suite_files_matching() {
  find "$SUITE_ROOT" -type f -name '*.slc' | LC_ALL=C sort | while IFS= read -r f; do
    case "$(basename "$f")" in *"$1"*) printf '%s\n' "$f" ;; esac
  done
}

# The path of one suite by its bare filename, or "" when there is none.
suite_path() {
  find "$SUITE_ROOT" -type f -name "$1" -print -quit 2>/dev/null
}

# How many suites the tree holds (the number every report's "scripts scanned" is measured against).
suite_count() {
  find "$SUITE_ROOT" -type f -name '*.slc' | wc -l | tr -d ' '
}
