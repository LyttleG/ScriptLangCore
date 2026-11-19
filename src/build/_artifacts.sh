#!/usr/bin/env bash
# src/build/_artifacts.sh, THE ONE RULE for "does this compiled artifact describe THIS tree".
#
# WHY THIS FILE EXISTS, in one measurement: four instruments answered that question, and they answered it three
# different ways. `run-repl-tests.sh` and `embed-check.sh` scanned `src/**/*.cs`, but one excluded `src/embed/*`
# and the other did not, and neither excluded the `obj/` and `bin/` folders a NESTED project writes INSIDE `src/`
# (the launcher lives in `src/embed/launcher`, so its generated `AssemblyInfo.cs` is a file a BUILD writes);
# `bundle-parity.sh` excluded `obj`/`bin` and not `src/embed`; `ab-check.sh` excluded nothing and warned instead of
# refusing. The result is the shape this repository keeps paying for: a stale artifact refused by the gate and
# accepted by another tool, which is worse than either rule, because the instrument that accepted it reports the old
# behaviour with the new tree's authority.
#
# SOURCED BY: src/build/run-repl-tests.sh (the gate), src/build/bundle-parity.sh, src/build/ab-check.sh, src/build/embed-check.sh.
# NOT HERE, deliberately: WHICH artifact to measure. The gate prefers the AOT publish, `bundle-parity.sh` takes the
# newest build, `--exe` pins one; those are POLICIES and each tool states its own. This file only decides whether the
# file a tool is holding is the file this tree compiles to.
#
# Source it with the tool's own root:  . "$ROOT/src/build/_artifacts.sh"

# ── the command that rebuilds each artifact, stated ONCE so a refusal cannot teach an outdated remedy ─────────────
SLC_REMEDY_EXE="dotnet publish ScriptLangCore.csproj -c Release -v q --nologo"
SLC_REMEDY_DLL="dotnet publish ScriptLangCore.csproj -c Embed -r win-x64 --self-contained -o publish-dll -v q --nologo"
SLC_REMEDY_ALL="cmd /c compile_aot.bat   (republishes the exe, the DLL and the launcher, and stages the DLL beside it)"

# ── the sources that BUILD the engine ────────────────────────────────────────────────────────────────────────────
# `src/embed/` is PRUNED because those are TWO OTHER PROGRAMS (the C ABI host and the launcher), each with its own
# project: their sources and their generated intermediates say nothing about this engine binary. `obj`/`bin` are
# pruned because a nested project writes its intermediates INSIDE src/, and counting one refused a fresh publish
# (measured: `src/embed/launcher/obj/Launcher/net11.0/win-x64/EmbedLauncher.AssemblyInfo.cs` named as the reason a
# correct binary was stale).
# `src/jitplugins/` is pruned for the same reason, and it is the twin of `src/embed/`: the FIXTURE is a JIT plugin and
# the CHECK is a NativeAOT host, two programs of their own, so editing the pin must not declare the engine stale (it
# did, once, which is what earned this line).
#   slc_engine_newest_source <artifact> [root]   -> the first source newer than <artifact>, or nothing (rc 1)
slc_engine_newest_source() {
  local artifact="$1" root="${2:-.}"
  find "$root/src" \
       \( -path "$root/src/embed" -o -path "$root/src/jitplugins" -o -name obj -o -name bin \) -prune -o \
       -name '*.cs' -newer "$artifact" -print -quit 2>/dev/null
}

# The same answer as a REPOSITORY-RELATIVE name, which is what a refusal should show: an absolute POSIX path
# (`/d/Reasonix/wsScriptCore/src/...`) beside a Windows artifact path reads as a second kind of thing.
#   slc_engine_stale_source <artifact> [root]
slc_engine_stale_source() {
  local artifact="$1" root="${2:-.}" found
  found="$(slc_engine_newest_source "$artifact" "$root")" || return 1
  [ -n "$found" ] || return 1
  echo "${found#"$root"/}"
}

# ── refusal 1: the artifact is older than the sources that build it ──────────────────────────────────────────────
#   slc_refuse_stale <artifact> <label> <remedy> [extra line] [root]
#     rc 0 = it REFUSED (the caller exits with its own code), rc 1 = the artifact is fine.
slc_refuse_stale() {
  local artifact="$1" label="$2" remedy="$3" extra="${4:-}" root="${5:-.}"
  local stale
  stale="$(slc_engine_stale_source "$artifact" "$root")" || true
  [ -n "$stale" ] || return 1
  echo "REFUSING: $label $artifact is OLDER than $stale." >&2
  echo "  A compiled artifact older than the sources does not describe this tree, so there is nothing trustworthy to measure." >&2
  echo "  rebuild: $remedy" >&2
  [ -n "$extra" ] && echo "  $extra" >&2
  return 0
}

# ── refusal 2: the file an instrument LOADS is not the file the build PRODUCED ──────────────────────────────────
# The launcher is the case that taught this: it loads the DLL BESIDE ITSELF, so a `-c Embed` publish alone left every
# launcher leg measuring an OLDER engine while the ABI leg beside it measured the new one (reported here as "the fix
# does nothing through the launcher"). A note is not enough, because the legs then answer questions about a tree
# that is not this one.
#   slc_refuse_copy_drift <produced> <loaded> <label> <remedy>
slc_refuse_copy_drift() {
  local produced="$1" loaded="$2" label="$3" remedy="$4"
  [ -f "$loaded" ] || return 1
  cmp -s "$produced" "$loaded" && return 1
  echo "REFUSING: $label $loaded is not the file the build produced ($produced)." >&2
  echo "  The instrument would answer for an engine this tree does not contain." >&2
  echo "  restage: $remedy" >&2
  return 0
}

# ── the warning shape, for a tool whose job is convenience rather than a verdict ────────────────────────────────
#   slc_warn_stale <artifact> <label> [root]
slc_warn_stale() {
  local artifact="$1" label="$2" root="${3:-.}"
  local stale
  stale="$(slc_engine_stale_source "$artifact" "$root")" || true
  [ -n "$stale" ] || return 0
  echo "WARNING: $label $artifact is OLDER than $stale, so it does not contain your change." >&2
  echo "  rebuild: $SLC_REMEDY_EXE" >&2
}
