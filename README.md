<p align="center">
  <img src="docs/slc.png" alt="Logo" width="128">
</p>

# ScriptLangCore

An embedded scripting language interpreter for .NET 11, written in C# with full NativeAOT compatibility.

**Author: Gérôme Guillemin.** ScriptLangCore, the language, the engine and this documentation are his work.

## Pipeline

```
Source text
    │
    ▼
┌─────────────┐
│   Lexer     │  src/Lexer/Lexer.cs
│             │  Character-by-character scanner
│             │  Produces: IEnumerable<Token>
└──────┬──────┘
       │
       ▼
┌─────────────┐
│   Parser    │  src/Parser/Parser.cs
│             │  Recursive descent Pratt parser
│             │  Produces: IEnumerable<Stmt> (the AST, src/Ast/Nodes.cs)
└──────┬──────┘
       │
       ▼
┌─────────────┐
│  Compiler   │  src/Interpreter/Compiler.cs
│             │  Compiles a body to BYTECODE: every function, every class and struct member, and a file's top level
│             │  src/Interpreter/Opcodes.cs and BytecodeFormat.cs hold the instruction set and its operand table
│             │  Produces: Bytecode per body, and the bodies a .slcbc bundle carries
└──────┬──────┘
       │
       ▼
┌─────────────┐
│     VM      │  src/Interpreter/Vm.cs, the fast path, with FLAT call frames (a million deep costs no C# stack)
│             │  src/Interpreter/VmBytecodeCodec.cs   writes and reads a bundle (--compile / .slcbc)
│             │  src/Interpreter/BytecodeVerifier.cs  what a loaded body must pass before it may run
└──────┬──────┘
       │
       │  a body the compiler does NOT take (a pattern match, an unfinished try, a request-producing
       │  coroutine) runs on the tree instead, and VmStats() reports the split, `VM: N (P%) | Tree: M (Q%)`
       ▼
┌─────────────┐
│ Interpreter │  src/Interpreter/Interpreter.cs + VisitExpressions.cs + VisitStatements.cs
│             │  Tree-walking visitor (IExprVisitor + IStmtVisitor): the fallback, and the front door
│             │  Produces: ScriptResult
└─────────────┘
```

Both engines are always in the binary, and the SAME statements have to answer the same way on either one, which
is the invariant most of this repository's tests exist for: a script's behaviour must not depend on whether its
body happened to be compilable.

## Project Structure

Counts are the `.cs` files in each directory, checked against the tree when this was written; the notable
members are named under it. Re-count before editing a number here, since a moved file is how this list goes
stale.

```
src/
├── Program.cs              Entry point: CLI dispatch, run a file, or start the REPL
├── Program.BytecodeDump.cs `--dump-bytecode`, which compiles a file and prints every body
├── Ast/          (1)   AST node types (Nodes.cs: every expression and statement, plus the pooled state)
├── Core/        (22)   Fundamental types: Token, TypeInfo, TypeTokenHelper, ScriptException,
│                       ScriptErrorInfo, ErrorFormatter, Namespace, ExecutionResult, ControlFlow,
│                       and the pools (ArrayPool, DictionaryPool, StringBuilderPool, CharBufferPool)
│   ├── Logging/  (1)   The one logger
│   └── Middleware/ (3) MiddlewarePipeline, HttpRouteTable and HttpResult (http.Use, mcp.Use, tcp.Use)
├── Lexer/        (1)   Tokenizer
├── Parser/       (1)   Recursive descent Pratt parser
├── Interpreter/ (29)   The engine: the tree walker, the bytecode compiler and the VM
│   ├── Interpreter.cs         Core runtime: frames, scopes, globals, errors, the CLR bridge
│   ├── VisitExpressions.cs    Expression evaluation (the tree)
│   ├── VisitStatements.cs     Statement execution (the tree)
│   ├── MethodDispatch.cs      Member and method resolution, overloads, the native call path
│   ├── Interfaces.cs          Contracts: declaration, defaults, the match rules
│   ├── ExtensionMembers.cs    `extension` blocks
│   ├── Compiler.cs            AST to bytecode
│   ├── Vm.cs                  The bytecode VM, with flat call frames
│   ├── Opcodes.cs             The instruction set
│   ├── BytecodeFormat.cs      The operand table: how many bytes each shape is, and what each one means
│   ├── BytecodeVerifier.cs    What a loaded body must pass (targets, boundaries, indices, stack balance)
│   ├── VmBytecodeCodec.cs     Writes and reads a bundle, and the source-less program loader
│   ├── CoroutineTypes.cs      Yield, generators and the coroutine drive
│   ├── Task.cs                async/await, and the engine's Task
│   ├── TypeHelpers.cs         The ONE exception to message map, and the CLR type index
│   ├── AotHints.cs            NativeAOT linker roots (plus AotInvoker.cs for native calls)
│   └── Registration/ (30)     Built-in registrations, one file per namespace or family
├── Interop/      (1)   TaskPump.cs, the runtime's side of an awaited task
│   └── COM/     (11)   The COM bridge: ComObject, ComScope, the VARIANT converter, the Ole host
├── Mcp/          (4)   The MCP server, its tools, its resources and the tool sandbox
├── Repl/         (3)   DotCommands, the REPL's handle types, and the interrupt flag
├── Runtime/      (6)   Standalone helpers: Networking, Scheduler, CronExpression, the LINQ and JSON
│                       extensions, and the HTTP error pages
└── Debugger/     (1)   The interactive script debugger
```

The shipping layout is a SEPARATE directory, and the code assumes it: `publish-aot/` holds
`ScriptLangCore.exe` beside `sqlite3.dll`, `assets/` (the suites, samples and language assets) and `tools/`
(the MCP tool scripts). `compile_aot.bat` publishes to the default location and mirrors it there.

## Language Features

| Category | Features |
|----------|----------|
| **Types** | `int`, `long`, `short`, `byte`, `float`, `double`, `decimal`, `bool`, `char`, `string`, `object`, `DateTime`, `DateTimeOffset`, `TimeSpan`, nullable (`?`), arrays, list literals, and the framework's own types by name (`List<T>`, `Dictionary<K, V>`, `Regex`, `Uri`, the CLR interfaces) |
| **Control Flow** | `if/else`, `while`, `for`, `foreach` and `await foreach`, `do/while`, `break`, `continue`, `return`, `switch`, the `match` expression, and `is` patterns |
| **Functions** | Named functions, local functions, lambdas, closures, `pure` memoization, default parameter values, `params`, `ref`/`out`, named arguments |
| **Coroutines & async** | `yield` generators driven by `foreach` or `Resume()`, and `async`/`await` over `await Coroutine.Sleep/Wait/Fetch`, `ReadLineAsync` and any `Task` |
| **Classes & Structs** | Single inheritance, interfaces (with default bodies, `implements`, `is`, casts), records, generics, extension members, constructors, destructors, fields, methods, nested classes, auto-properties, object initializers, access modifiers (`public`/`private`/`protected`), `super`/`base`, enums. Defaults to `private`. Structs are value-types with fields, methods and operator overloads. |
| **Delegates** | `Action<>`, `Func<>`, `Predicate<>`, `Comparison<>`, `Converter<>`, `EventHandler<>`, `event`, multi-cast, `as` casting |
| **Operators** | Arithmetic, comparison, logical, bitwise, compound assignment, `is`/`as`/`is not null`, ternary, null-coalescing (`??`, `??=`), null-conditional (`?.`), ranges (`1..5`), operator overloading |
| **Modules** | `import "path" [alias name]` cross-file loading, `namespace`, `RunScript` dynamic evaluation, and bytecode bundles (`--compile` writes a `.slcbc`; `--no-source` strips the text when that is honest) |
| **Runtime APIs** | LINQ (30+ methods), JSON, regex, sqlite (typed queries, bulk insert, CSV import), HTTP client and `http.Use` server pipelines, TCP and WebSocket, COM and Excel automation, Win32 and a Windows UI toolkit, MCI multimedia, clipboard, scheduler, GC control |

## AOT Compatibility

The project targets `net11.0` and publishes with NativeAOT, so the shipped artifact is one native
executable (`win-x64`, self-contained, `PublishAot` on Release):
- **No dynamic code generation.** Every function is compiled to bytecode and runs on the internal VM; the
  tree-walking interpreter is the fallback for the constructs the compiler declines, and both are always in
  the image.
- `[DynamicDependency]` attributes root what only reflection reaches (`AotHints.cs`), because the trimmer
  otherwise removes a member that works in Debug and vanishes in the publish.
- `[UnmanagedFunctionPointer]` and `LibraryImport` for Win32 and native calls.
- `InvariantGlobalization=true`, and `IlcDefaultStackSize=33554432` (32 MB, against the 1.5 MB default), which
  is why a depth limit that looks fine in Debug can behave differently in the shipped binary.
- Object pools (arrays, dictionaries, string builders, char buffers) to keep allocation off the hot paths.

## Build

```bash
dotnet build -c Debug                              # the development build (transient: Debug and the shipped binary differ)
dotnet publish ScriptLangCore.csproj -c Release    # the PRODUCT: the NativeAOT executable, in bin/Release/net11.0/win-x64/publish
compile_aot.bat                                    # the same publish, mirrored into publish-aot/ beside sqlite3.dll, assets/ and tools/
dotnet run --project ScriptLangCore.csproj         # build and start the REPL
```

The engine's project is NAMED in these commands on purpose: the solution also holds `src/embed/` (the host that drives
the published DLL through its C ABI) and `src/embed/launcher/` (the shipped AOT front end), so a bare `dotnet publish`
resolves to the SOLUTION and publishes all three.

The Debug build is a convenience: it runs on the JIT with a small apphost stack and a fixed flat-frame
cap, while the shipped binary is NativeAOT with a 32 MB stack and `InvariantGlobalization`. Anything
platform-sensitive is checked on the publish (`bash tools/run-repl-tests.sh --build both`), and a stale
publish is refused rather than reported as a pass.

### One rule for "does this artifact describe this tree"

Five artifacts exist (the gate build, the shipped `publish-aot\ScriptLangCore.exe` copy, the embedding
`publish-dll\ScriptLangCore.dll`, the launcher, and the DLL STAGED beside the launcher), and every
instrument has to answer the same two questions about the one it holds. Both answers live in
`tools/_artifacts.sh`, sourced by the gate, `bundle-parity.sh`, `ab-check.sh` and `embed-check.sh`, so a
tool cannot answer differently from another:

- **freshness**: an artifact older than the sources that build it is refused, with one sentence and the
  command that rebuilds it. The comparison prunes `src/embed/` and any `obj`/`bin` folder, because the
  host and the launcher are separate programs and a nested project writes its intermediates inside
  `src/` (counting one made a correct publish look stale);
- **identity**: the file an instrument LOADS must be the file the build PRODUCED. The launcher loads the
  DLL beside itself, so a `-c Embed` publish alone leaves every launcher leg, and every suite a launcher
  child runs, measuring an older engine; that is refused now, not noted.

`compile_aot.bat` checks the same identity at the other end, where the copy is made, and prints the
size and timestamp of all four artifacts.

## Testing (repl suites)

The language suites live in `assets/language/repl/*.slc` (`Check` / `CheckSummary` / `exit(CheckFailed())`).
Run them through the gate script, it picks the **newest** build (and warns when the binary is older
than the newest source file, so a stale AOT artifact cannot silently report old behaviour), enforces a
per-script timeout, and separates *known* failures from regressions:

```bash
tools/run-repl-tests.sh                  # everything not listed in tools/repl-test-skip.txt
tools/run-repl-tests.sh --filter match   # subset by name
tools/run-repl-tests.sh --verbose        # per-script lines
tools/run-repl-tests.sh --json           # machine-readable summary
tools/run-repl-tests.sh --exe bin/Debug/net11.0/ScriptLangCore.exe
tools/run-repl-tests.sh --build jit            # force the Debug/JIT binary
tools/run-repl-tests.sh --build aot            # force the NativeAOT publish binary
tools/run-repl-tests.sh --build both           # gate every available platform, per-platform report
```

The engine SHIPS as NativeAOT, so the gate is **AOT-primary by default**: it runs the NativeAOT publish,
prints `platform : aot (shipping target)`, and **refuses** (exit 2) when that publish is older than the
tree rather than reporting stale behaviour. With no publish present it falls back to the JIT build and
says so loudly, a JIT-only pass is not evidence about the shipping binary. `--build newest` restores
the old newest-wins behaviour, `--build jit|aot` pins a platform (needed for the JIT-only surfaces: the
1.5 MB-apphost stack guard, the `_vm_gen` override in `tools/repl-test-env.txt`), and `--build both`
gates every available platform in one command (`--exe` still wins over `--build`).

### Trying a snippet without a rebuild

`assets/language/probes/clipboard_runner.slc` watches the clipboard through Windows' own notification
(`AddClipboardFormatListener`, delivered as `WM_CLIPBOARDUPDATE` to a hidden form's `SetWindowHandler`), so a copy
is an EVENT and not a poll. Start it in the REPL and leave it running:

```
.run assets/language/probes/clipboard_runner.slc
```

Select any script text in any editor and press Ctrl+C. The text runs in the session you are already in (isolated by
default; the window's `Run in this session (global)` check box runs it in the session's own scope, where it sees the
variables you already have and whatever the previous snippet declared, which is what a chain of pastes needs), its
source, output and result are appended to the window's log box AND stored as one row of `clipboard_runs.db`, which
sits beside the tool when that folder can be written and in `%LOCALAPPDATA%\ScriptLangCore` when it cannot (an installed
copy lives under Program Files, where nothing may be created), and the
clipboard is emptied so one paste runs once. A capture that is the PATH of an `.slc` or `.slcbc` (Explorer's "Copy as
path", quoted or not) is run as that FILE: a `.slc` keeps its own positions through the engine's file entry, and a
`.slcbc` is loaded and its own entry called, so a program bundle runs in this process (a library blob has no entry and
the loader refuses it by name). Either is logged as a path rather than echoed as text. A capture that STOPS part-way says so: the entry gains a
`--- note` line after its output, because an output that merely ends reads like a truncated capture when it is
really everything the run printed before it failed.
The window opens dark and centred, and its TITLE-BAR ICON is the state light: `◉` waiting for a copy, `⇩` a copy
just arrived, `▶` a run in progress, `✅` it finished, `❌` it failed, `⏹` the watch is over (each with its own
colour). The log box fills the window (it follows a resize), `Clear` empties the BOX while the database keeps every run
(answering Yes to its prompt also purges the stored history, which is the only thing that removes rows), and the status
bar always shows the LAST run with its datetime and its result. Each entry
carries the source, the output, the ENGINE LOG when the engine wrote to stderr during the run (a warning, or an error
it logged without raising: a repeated `CreateForm`, a variable declared twice) and the result, so the box and the file
say the same thing; a copy the tool decides not to run says why instead of staying silent. A second row of
switches changes how a capture runs (`Top level on the VM` routes the capture through `VmSetTopLevelEnabled`, `Compile
first` exports the snippet to a bundle and runs that, `Stats` appends the engine counters and the allocation delta),
and two buttons reuse the last run (`Repeat`, `Copy output`). The log box is a RICHEDIT, so each section of an entry
reads by its colour (source dim, output bright, an engine warning amber, a failure red) and the text wraps instead of
scrolling sideways; a third row drives the log itself (`Open log` reloads the file, `Follow` decides whether a new
entry scrolls, and the find pair walks a case-insensitive search that wraps). A second `.run` of the tool in the same session stops
with a message rather than building a second set of controls on the panel. The window
stays live while a capture runs, `await` included: a suspension is driven by the coroutine pump, which dispatches the
queued messages, so the panel keeps painting and a click still lands mid-run (that is the engine's behaviour, pinned by
`await_window_liveness_test`). Closing the
window stops the watch, and so does Ctrl+C in the REPL or a file named `clipboard_runner.stop` beside the log;
`maxWatchMs` bounds a scripted run, and `showPanel = false` gives the console-and-log-only mode a CI child wants. The
globals at the top of the file are the configuration. A snippet that calls `exit()` still ends the session, because
that is what `exit` means in this language.

One more check worth running before a commit that touches error handling: `tools/check-dup-friendly-maps.sh`.
It guards a bug class this codebase has produced repeatedly, a SECOND copy of the exception→message map (the
engine once carried four, and the copy an input happens to reach is the one that leaks a raw CLR/Win32
message to a script, e.g. `DllNotFound_Windows, sqlite3, <localized>`). It fails if any file outside
`src/Interpreter/TypeHelpers.cs` maps an exception TYPE to a string, or if more than one switch tail forwards
a raw `.Message`. Exit 0 clean, 1 on a duplicate, 2 when run outside the repo root. Both directions are
self-tested: injecting a duplicate switch makes it fail, removing it makes it pass again.

The structural checks that keep recurring bug classes out live in `tools/check-invariants.sh` (seconds, no
build): one friendly-exception map with no copies or interpolated raw messages, every suite tracked or
registered (or listed in a registry), the native load site appearing exactly once, every platform branch
stating its kind and reason, and every opcode known to the executor, to one of the disassembler's two
tables and to `BytecodeFormat`, the operand table decoding every opcode the format table gives an operand,
and the retired set named in `tools/opcode-not-emitted.txt`. `tools/check-native-matrix.sh` needs the AOT
publish and exercises the DEPLOYMENT
dimension that ordinary suites cannot: the native present, absent, given a foreign path, and duplicated, the
last being the layout that once put two instances of one native in a process and killed the binary with an
access violation. Both are self-tested: forcing the failure they describe makes them fail and name it. The
gate itself refuses a stale AOT publish on every AOT path; `--allow-stale` (or an explicit `--exe`) is the
deliberate way to check an artifact as-is.

Three checks guard the language reference (`docs/language/index.html`) instead of the code, and they exist
because each one was found broken: `tools/check-doc-structure.sh` (every entry inside an entry-list, tags
balanced, every `href="#..."` resolving), `tools/check-doc-nav.sh` (every entry reachable from the sidebar, which
is a hand-kept list and had drifted by 39 entries) and `tools/check-doc-keywords.sh` (every keyword the LEXER
declares is documented by some entry, read from `src/Lexer/Lexer.cs` so it cannot go stale, and every
keyword-tagged entry carries a `syntax` or `example` sample; sixteen keywords you can type had no entry at all
when it was written, and `case / when` exists because of it).

`tools/dump-bytecode.sh` compiles EVERY suite and writes its bytecode, so the emitted bytes can be diffed
before and after an emitter change with `diff -a` (same exe on both sides), that is how the format
consolidation was proven byte-identical across 412 suites. It also runs `BytecodeVerifier` on every body it
dumps and fails the sweep on a refusal: the only place `BytecodeFormat` operand SIZES are checked against real
emitted streams, which is how `GET_PROP`/`SET_PROP` (operand-less) and `MAKE_LAMBDA` (variable-length) were
found, and it is what found the `??=` non-null result defect (Phase 3 §10). A manual ~2-minute step,
deliberately not part of the gate.

`tools/strip-survey.sh` asks a different whole-corpus question: which suites REFUSE to be compiled without
their source (`--compile --no-source`), why, grouped by the engine's own phrase. It runs the compile only, so
nothing is executed and no suite can abort it, and it compares the result against
`tools/strip-survey-baseline.txt`, so a suite that STOPS stripping is reported as a regression (exit 1) rather
than hiding inside a total. `--record` re-writes that baseline after a change that clears refusals, which is
the checkout's own way of saying "the frontier moved". A manual step too, and the number it prints is the one
AGENTS.md quotes.

`tools/bundle-parity.sh` asks the question the corpus never asked: does a stripped bundle BEHAVE like the script
it was built from? Every other whole-corpus tool runs the SOURCE (`run-repl-tests.sh`), proves a body verifies
(`dump-bytecode.sh`) or proves a file strips (`strip-survey.sh`), so a suite whose bundle answers differently,
or aborts, or cannot even be LOADED, stayed green forever, because the gate never runs a bundle. For each suite
it compiles `--no-source` and requires the report to say `source stripped` (a compile that succeeded by
CARRYING its source is not a bundle, and comparing it would prove nothing), runs the file and then the bundle,
and diffs the two. A difference is labelled DIFF when the bundle's VERDICT differs (a failed check, an abort, an
error line, another exit code) and TEXT when only the free-form output does, which is what stops benchmark
timings and `VmStats()` counters from drowning the signal; timestamps, entry banners, `[Lnnnnn]` leaders,
durations and the two warm-up-dependent cache counters are normalised away, everything else is compared byte for
byte. The bundle is written BESIDE its suite on purpose: a run's directory comes from the file it runs, so a
bundle in a temp folder would answer `Script.Directory()` with that folder, which is a difference of place
rather than of behaviour. It compares the differing set against `tools/bundle-parity-known.txt` (a name that
appears is a regression, exit 1; `--record` re-writes it), refuses a binary older than `src/` as the gate does, and
it takes ONE RUN AT A TIME: a suite writes its artefacts with a bare path, which lands in the deployed folder it
runs from, so two runs (or a run beside the gate) would each compare its own source leg against the other's
artefact. A second run is refused by name rather than left to race. Two details of its resolution are worth knowing
too: it considers `publish-aot/` beside `bin/Release/**` when it picks the newest binary (this repository publishes
with `-o publish-aot`, so a glob over `bin/` alone measured an older folder and refused as stale), and the staleness
scan skips `obj/` and `bin/` wherever they sit, because the launcher project lives under `src/` and a build writes
generated `.cs` files there. Its first sweep found two defects nothing else in the tree could see: a stripped bundle
ran with NO run directory at all (so every `./` path refused), and a bundle whose body named a type the load check
did not know could not be loaded AT ALL.

`tools/embed-check.sh` is the only instrument that runs OUTSIDE the engine. The published embedding library
(`publish-dll/ScriptLangCore.dll`, built by `compile_aot.bat` with the `Embed` configuration) exposes a C ABI of
sixteen exports, and nothing a suite can do can load it, because suites run inside the engine's own process. So the
check is a separate host program (`src/embed/`, a `net11.0` console project with no reference to the engine) that
loads the DLL, starts a session, registers an output callback and drives the exports: a session on the engine's own
thread from an MTA host, a session pinned to a host thread that is STA with at least 16 MB, `print` and the engine's
log lines arriving on the callback, a run's declarations still callable through `slc_call`, a compiled bundle in BOTH
shapes (carrying its source and source-less), a runaway recursion coming back as the engine's depth guard instead of a
dead host, a cancel landing from another thread even while the script is inside `sleep`, `slc_free` refusing while a
run is in flight, and create/free cycles that leak no handles. It refuses a DLL older than `src/` as the gate does, and
its exit code is the verdict (`0` every check passed, `1` a check failed, `2` it could not start). It prints the
version and ABI revision it measured, so a run against a stale artifact says so first.

`tools/run-misc-scripts.sh` runs the maintainer's own `assets/language/misc/**` scripts, which are a regression set he
runs by hand, and they are not suites, since they live outside `SUITE_DIR`, mostly only
`print` (no `Check`/`CheckSummary`) and the directory is gitignored, so nothing else in the tree ever ran them.
A script passes when it exits 0 and its output holds no failure line; a script that times out is reported as
such (the corpus holds benchmarks, HTTP samples and two debugger demos that wait) and a name in
`tools/misc-scripts-skip.txt` is reported as skipped rather than run. It prints the exe it used and warns when
that binary is older than `src/`, because a fix that exists only in the source would otherwise read as a
failing script. Passing an exe explicitly runs the corpus against a specific build, which is how the delegate
arity fix was verified (`121 clean, 0 failed`).

`tools/opcode-not-emitted.txt` is a registry of a different kind: the opcodes the compiler never emits, with a
reason each, so the coverage check can tell a deliberately retired opcode from a new one nobody wired up.

Three suite registries keep it honest: `tools/repl-test-skip.txt` (GUIs, servers, sockets, anything that
blocks), `tools/repl-test-known-fail.txt` (already-broken suites, each with its reason; they are
reported but do not fail the run) and `tools/repl-test-env.txt` (per-suite overrides,
`VAR=VALUE` pairs exported for that one suite, e.g. `_vm_gen.slc` needs `SLC_VM_MAXFRAMES=1100000`
because the JIT flat-frame cap of 30000 is a deliberate runaway-recursion guard that must stay in force
everywhere else, and a `TIMEOUT=SECONDS` knob for suites that are merely SLOW so their checks still gate
instead of being parked in the known-fail registry). Exit code: `0` = no
unexpected failure, `1` = regression, `2` = no runner binary. Note that many suites exit `0` even when
a check fails, so the runner also reads the `N passed, M failed` summary line rather than trusting the
exit code alone.
