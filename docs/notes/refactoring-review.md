# Refactoring review (2026-11-09)

A whole-tree survey of what is worth refactoring, with the numbers that justify each item and the proof
each item owes. It is the deliverable of the review round; the work items below are executed in the order
they appear in `docs/reasonix/plans/` terms, one split or extraction per commit.

## Method

Read the tree, measured it with `wc`/`grep` over `src`, read the boundary lines of every large method,
and used the repository's own instruments as the safety net. Nothing here is a claim about style: every
item names a measured size, a measured duplication, or a measured dead arm.

Three of the four classic refactoring targets are already closed by checkers, and that is why they are
NOT on this list:

- identical bodies: `src/build/check-dup-bodies.sh` is green and `src/build/dup-bodies-known.txt` is EMPTY;
- dead private members: `src/build/check-dead-privates.sh` is green and `src/build/dead-privates-known.txt` is EMPTY;
- opcode coverage and the operand shape table: `src/build/check-opcode-coverage.sh` is green, the shape matrix
  and the const width are each stated once;
- error wording and path resolution: `FunctionExceptionMessage` (`TypeHelpers.cs`) and `ResolvePath` are
  each the single implementation.

So the debt is size, cohesion and ONE duplicated command surface, not duplication of logic.

## Baseline (measured before any change)

| instrument | value |
|---|---|
| `dotnet build -c Debug` | 0 warnings, 0 errors (36.9 s) |
| focused gate `dot_command_strip_test` | passed 1, unexpected failure 0 |
| `src/build/dump-bytecode.sh` | 536 suites, no compiled function 28, refused 0, did not run 0, program bodies 535 compared, 0 regressed |
| `src/build/strip-survey.sh` | surveyed 536, stripped 524, refused 12 (matches the recorded baseline) |
| `src/build/bundle-parity-known.txt` | 333 rows (recorded, not re-run: a full compare is about 30 min) |
| `publish-aot/ScriptLangCore.exe` | 19,799,552 B |
| `publish-dll/ScriptLangCore.dll` | 19,799,040 B |
| `publish-launcher/ScriptLangCore.Launcher.exe` | 18,944 B |

## Ranked inventory

### 1. `src/Interpreter/Registration/Ui.cs`, 12,029 lines, about 30 public types in one file (LOW risk, HIGH payoff)

One file holds the whole UI framework: `Control` alone is 3,436 lines (1996 to 5431), `Form` 1,607
(6837 to 8443), `NativeApp` 1,047 (5567 to 6613), then `MdiChild`, `MdiForm`, `TheTimer`, 25 controls,
the event-args structs (11229 to 11645) and three dialogs. The file is named "Registration" although
almost none of it is a registration.

Refactor: one file per type under `src/Ui/`, keeping `partial` where `LibraryImport` requires it and
keeping `UiFactory`. Proof: build 0 warnings, `git diff` shows moves only, focused UI suites, AOT size
within noise.

### 2. `src/Interpreter/Interpreter.cs`, 17,719 lines, 780 member declarations in one partial class (LOW risk, HIGH payoff)

The partial already has honest boundaries elsewhere (`VisitExpressions.cs`, `VisitStatements.cs`,
`MethodDispatch.cs`, `Compiler.cs`, `Vm.cs`, `Interfaces.cs`). What is left in this file mixes six
concerns:

- bundle export and load, 13681 to 15650, about 1,970 lines (`ExportProgramWithoutSource*`,
  `ExportTopLevelToBundle*`, `ExportTopLevelToLibrary*`, `LoadBundleFromFile`, `LoadBundleFromBytes`,
  `LoadBundleToNamespace*`, `LoadSlcbcFileToRepl`);
- generic instantiation, 16564 to 17540, about 1,000 lines;
- coercion, 5117 to 5807 (`CoerceTo`, `DefaultFor`, `IsExceptionMatch`);
- ref cells and target resolution, 12430 to 12500;
- debugger support, 1608 to 1790 (there is already a `src/Debugger/ScriptDebugger.cs`);
- stack and memory guards, 1992 to 2233.

Refactor: `Interpreter.BundleIO.cs`, `.Generics.cs`, `.Coercion.cs`, `.RefCells.cs`, `.Debugger.cs`,
`.Runtime.cs`, all member-level moves into the same partial. Proof: moves only, build, focused suites.

### 3. `src/Program.cs`, 2,628 lines, six concerns in one static class (LOW risk, MEDIUM payoff)

`RunMain` dispatch, the compile verbs (`SourceModeFromArgs`, `OutPathFromArgs`, `CompileToBundle`),
`PrecompileFunctions` (about 300 lines of compiler concern), the bundle runners, `RunReplCore`
(about 400 lines), `HandleDotCommand` (780 lines) and the diagnostics printers.

Refactor: `Program.Repl.cs`, `Program.DotCommands.cs`, `Program.Bundle.cs`, `Program.Compile.cs`,
`Program.Diagnostics.cs`. Proof: as above.

### 4. The command surface is dispatched twice, and the second copy has dead arms (MEDIUM risk, correctness payoff)

`Program.cs:1174` calls `Repl.DotCommands.Handle` first and falls back to `Program.HandleDotCommand`
only when it answers false. `DotCommands.Handle` handles `.help`, `.vars`, `.reset` and `.gc` and returns
true on every path (`DotCommands.cs:137, 225, 258, 608`), so `Program.HandleDotCommand`'s arms at
`Program.cs:1472` (`.gc`), `1712` (`.help`), `1788` (`.vars`) and `1840` (`.reset`) are unreachable,
about 300 of its 780 lines. `Program.PrintHelp()` has exactly one call site, line 1713, inside the dead
`.help` arm, so a 54-line help text is dead too.

That dead `.reset` arm is the one that implements the documented behaviour: a new interpreter for
`hard`, `Reset(keepGlobals: false, clearTypes: true)` for `all`, and the refusal for an interpreter an
embedding host owns. The reachable `DotCommands` arm (`DotCommands.cs:245-258`) runs the same
`Reset(keepGlobals: true, clearTypes: false)` in BOTH branches, differing only in the sentence it
prints, so `.reset hard` and `.reset all` never reach the code the docs describe and `.reset` never
clears globals.

And the registry must agree with the dispatch: `CommandHelp` (39 entries) backs the reachable `.help`
output AND `IsKnownCommand`, which the file stripper uses to decide whether a column-1 dot line in a
script is a directive to delete or code to keep. A command implemented in one table and not the other
silently changes that decision.

Refactor: one command table (name, help, signature, handler) feeding dispatch, `.help`, `IsKnownCommand`,
aliases and the usage strings; delete the dead arms, `PrintHelp` and the reflection bridge
(`DotCommands.cs:104`, `typeof(Program).GetMethod("HandleDotCommand", BindingFlags.NonPublic | ...)`);
move the real `.reset` implementation into the surviving handler. Proof: a characterization suite that
drives every documented command in a child REPL and pins `.reset` behaviour, plus the gate.

### 5. The god methods (HIGH risk for `Vm`, MEDIUM for the rest, proved by byte-identical output)

Measured method spans (start line to the next member declaration):

| method | lines | note |
|---|---|---|
| `Vm.ExecuteCoreInner` (`Vm.cs`) | 813 to 5280, about 4,470 | one dispatch loop, 89 opcode arms |
| `Compiler.CompileExpr` | 2968 to 4518, about 1,550 | one switch over expression kinds |
| `Compiler.CompileStmt` | 1884 to 2789, about 906 | one switch over statement kinds |
| `DotCommands.Handle` | 83 to 1152, about 1,070 | overlapping with item 4 |
| `Interpreter.InstantiateGenericFunction` | 16745 to 17540, about 796 | |
| `Interpreter.VisitNewInstanceExpr` | 7764 to 8413, about 650 | |
| `Interpreter.InvokeScriptFunction` | 15984 to 16563, about 580 | |
| `Interpreter.CoerceTo` | 5117 to 5670, about 554 | |
| `Parser.Primary` | 4556 to 5150, about 595 | the parser is otherwise one method per production |
| `VisitExpressions.InvokeMethodDispatchCore` | 1793 to 2297, about 505 | |
| `Control` (class, `Ui.cs`) | 1996 to 5431, about 3,436 | paint, theme, layout, scroll bar, WndProc, DPI |

Refactor, in this order: `Compiler.CompileExpr` and `CompileStmt` into per-family private methods, then
`Interpreter.CoerceTo` and `InvokeScriptFunction`, then `Control` into partials by concern. The proof is
the bytecode dump: the emitted stream must be IDENTICAL before and after, `refused: 0`, with the strip
survey and bundle parity unchanged. Any byte difference is a defect, not a refactor.

`Vm.ExecuteCoreInner` is deliberately NOT proposed for a full split. It is the hottest and most delicate
method in the tree. Only the already separable blocks (the exception walk, the foreach arms, the CALL and
CALL_VAL arms) are offered, behind the same dump proof and the full VM/coroutine suite set.

### 6. `src/Interpreter/InterpreterTypes.cs`, 2,145 lines, about 20 unrelated nested types (LOW risk)

`ScriptResult`, the check ledger, the delegate adapters, the script collections, `ValueFormatter`,
`ScriptListSorting`/`ScriptComparer`, `DynamicLibrary`/`NativeFunction`/`NativeCall`, `ModuleInfo`,
`CallFrame` and its poppers, `CaptureCollector`, `WeakRef`, `ClassInfo`, `StructInstance`, the marker
exceptions. One file per concern, all members of the same partial class.

## Deliberately not touched

- No public API, script-visible name, performance or behavioural change as part of a refactor. The one
  behavioural item (item 4's `.reset`) is flagged and pinned separately.
- No new interfaces, seams or abstractions for testability: the repository forbids an interface with one
  implementation and no swap.
- `TargetInvokeCachedMethod`'s duplication of the shared invoke arm and the interpreter's two index
  cores are recorded decisions, not debt.
- The COM lifetime rule ("the engine ends nothing, it reports") and the host rules in `ScriptSession`.
- The format table, the error-message map, the path rule and the opacity of the embedded ABI.

## Documentation debt noticed during the survey

`docs/architecture.md` is stale in three claims, and the language reference contradicts two of them:

- line 5 says the engine interprets "without compiling to bytecode", while the bytecode VM is the default
  path for compiled bodies and the top-level statement path since the flip;
- line 12 calls the VM a "POC fast path for a subset of VM-safe functions", which understates it;
- line 20 says keywords are matched case-insensitively, while `docs/language/index.html` states (and the
  lexer implements) case-sensitive keywords since 2026-10-05.

Those are corrected with the inventory so a reader meets the engine that exists.
