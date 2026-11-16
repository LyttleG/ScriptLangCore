#!/usr/bin/env bash
# Names the engine REPORTS must be names a script can WRITE. That is the alias table's own rule, and the two lists
# live in two places: the explicit KnownTypes map says what the engine calls a value, and the alias table's keys are
# what a script can type. Nothing compared them, so PaintEventArgs was reported with only a qualified spelling a
# script could not use, and eleven more names were in the same state.
#
# The four exceptions are deliberate, and each is writable in a DIFFERENT spelling of the same idea rather than by
# its reported name: the generic families take a type argument (List<object>), and void is a keyword.
set -uo pipefail
FILE=src/Interpreter/Registration/TypeAliases.cs
[ -f "$FILE" ] || { echo "FAIL: no such file: $FILE"; exit 1; }

# Each exception is writable in another spelling of the same idea, and NONE is writable by its reported name:
#  - List, Dictionary, HashSet take a type argument (List<object>).
#  - void is a keyword.
#  - the rest are CLR names a script writes as they are (DateTime, TimeSpan, the sized integers), which the
#    name resolution reaches without this table.
allowed=" List Dictionary HashSet void Result sbyte ushort uint ulong DateTime TimeSpan "
bad=""
count=0
# every name the explicit map reports
while read -r name; do
  [ -n "$name" ] || continue
  count=$((count + 1))
  case "$allowed" in *" $name "*) continue ;; esac
  if ! grep -q "\[\"$name\"\] = typeof(" "$FILE"; then
    bad="$bad $name"
  fi
done < <(sed -n '/Dictionary<Type, string> KnownTypes/,/^        };/p' "$FILE" \
         | sed -n 's/.*=\s*"\([^"]*\)".*/\1/p')

if [ -n "$bad" ]; then
  echo "FAIL: the engine reports these names but a script cannot write them:$bad"
  echo "      add the bare alias, as PaintEventArgs needed, or list it above with its reason."
  exit 1
fi
echo "ok: all $count names the engine reports are also names a script can write"