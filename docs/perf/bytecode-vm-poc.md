# Bytecode VM, Status Report

## Status: Production fast path (complete)

ScriptLangCore is a tree-walking interpreter with a **lightweight stack-based bytecode VM** as the
execution fast path. Functions that compile safely run bytecode; everything else falls back to the
tree-walker with zero behavior change. The VM now covers **every construct** the language supports.

```
Parse → AST (FuncDecl) → Compiler.Compile → Bytecode (cached on FuncDecl.Compiled)
                                      ↓ (unsupported shape → null)
                              Tree-walker (unchanged, correct fallback)
                    Export → blob → Bytecode.Load (fresh process, VM-only shells)
```

## Architecture

- **`src/Interpreter/Opcodes.cs`**, ~50 opcodes: LOAD_CONST/LOAD_VAR/STORE_VAR (+ LOAD_GLOBAL/STORE_GLOBAL
  for live globals), arithmetic/comparison/bitwise/logical/unary, JMP/JMP_IF_FALSE, INC/DEC, NOT,
  IS_TYPE, AND/OR, MOD, CALL/CALL_VAL/CALL_METHOD, RETURN, DUP/POP, LOAD_BUILTIN, MAKE_LAMBDA/MAKE_LIST/
  MAKE_DICT/COMP_FINISH, NEW_CLASS/NEW_STRUCT/NEW_ARRAY/NEW_LIST/NEW_DICT/NEW_INSTANCE, GET_PROP/SET_PROP/
  GET_INDEX/SET_INDEX, MAKE_INDEX/MAKE_RANGE, OBJ_INIT/WITH, LOAD_SUPER, MATCH_EQ, FOREACH_INIT/
  FOREACH_NEXT, TRY/CATCH/END_FINALLY/THROW, STORE_LOCAL_LINE, LOC, STORE_LINE, etc.
- **`src/Interpreter/Compiler.cs`**, single-pass AST → bytecode. `Ctx.Success`: any unsupported
  shape → `null` → tree fallback. Lambdas compile capture-free and read-only-capturing; **written
  local captures stay tree** (RefCell propagation), **written global captures run VM** (live
  LOAD_GLOBAL/STORE_GLOBAL). Comprehensions materialize `IEnumerable` sources eagerly (tree-consistent);
  regular `foreach` over generators stays lazy via the tree.
- **`src/Interpreter/Vm.cs`**, **typed parallel operand stack** (`sK/sT/sL/sD/sO` arrays): hot numeric
  ops update `sL`/`sD` in place with no 32-byte struct copies. `VmFallback` sentinel signals a
  graceful hand-off to the tree.
- **`src/Interpreter/Interpreter.cs`**, `InvokeScriptFunction` runs `decl.Compiled` when available;
  appends `VmCaptures` for VM-created capturing lambdas; unwraps RefCell-wrapped globals
  (coroutine-lambda capture promotion); `IsVmShell` decls **error** on fallback instead of silently
  tree-walking an empty body.

## Pre-compilation

`PrecompileFunctions(statements, itp)` batch-compiles every top-level `FuncDecl` right after parsing
at all script entry points (CLI file run, REPL `.run`, `.looprun`, `.run -q`).

## VM Coverage

| Category | Constructs |
|----------|-----------|
| Literals | numbers (all numeric types), strings, chars, bools, null, list `[a,b]`, dict `{k:v}`, ranges, `^`/index |
| Arithmetic | `+ - * / % -x` (ADD also concatenates strings) |
| Comparisons | `< > <= >= == !=` (long fast path; tree `AreEqual` for mixed/enums) |
| Bitwise/shift | `& \| ^ << >> ~` incl. compound `&= \|= <<= >>= %=` |
| Logical | `&& \|\| !` |
| Unary | `-x !x ~x` |
| Control | if/while/for/foreach, break/continue, try/catch/finally, using |
| Objects | `new` (class/struct/array/.NET), object-init `new T { P = v }`, `with`, `super.method`, `this`, statics |
| Members | property/field get-set, method calls (cached overload dispatch), index/slice |
| Globals | plain + RefCell-wrapped + compound-assign + inc/dec (live `LOAD_GLOBAL`/`STORE_GLOBAL`) |
| Functions | named calls, capture-free lambdas, read-only-capturing lambdas, global-write-capturing lambdas, `typeof`, builtins |
| Misc | destructuring, `match` expressions, comprehensions (incl. `IEnumerable` sources), throw (raw values), line/col errors |

**Deliberately tree-walked** (correctness): coroutine functions (`yield`), written-*local*-capture
lambdas (RefCell propagation), lazy `foreach` over generators/coroutines, unknown compiler shapes.

## Performance

The typed parallel stack moved the pure-numeric benchmark from **394% → ~590% of tree time** (~5.9×).
`bench_vm_ab.slc` measures the same code with VM on/off and verifies identical results:
`.\publish-aot\ScriptLangCore.exe assets\language\repl\bench_vm_ab.slc`

## Operand Specialization (2026-08-06)

Three further dispatch-loop optimizations landed on top of the typed stack (spec
`docs/reasonix/specs/2026-08-06-vm-operand-specialization-design.md`, plan
`docs/reasonix/plans/2026-08-06-vm-operand-specialization.md`):

1. **Global fast-path slots (A)**, `LOAD_GLOBAL_FAST` / `STORE_GLOBAL_FAST` cache each global in a
   per-invocation slot table, skipping the per-access dictionary lookup. Slots are invalidated after
   every script call (a callee may mutate any global) and across coroutine yield/resume. Slot names
   are case-sensitivity-correct (the table matches the live scope's comparer in both strict and lax
   modes) and serialize into `.slcbc` bundles.
2. **Constant folding (B)**, binary `+ - * /` on two numeric literals folds at compile time
   (checked long arithmetic, double+double; mixed kinds never fold).
3. **Typed local access (C)**, `LOAD_VAR` / `STORE_VAR` / `STORE_CONST` push/pop the typed `VmValue`
   directly, eliminating the `ToObject()` / `VmValue.From` boxing round-trip on the hottest ops.

Measured on `bench_vm_ab.slc` (steady state, runs 2-3):

| Workload | Before | After | Δ |
|----------|--------|-------|---|
| Mixed | 395 ms | **311 ms** | **−21%** |
| Pure numeric | 213 ms | **149 ms** | **−30%** |
| Mixed vs tree | 39× | **45×** |, |
| Numeric vs tree | 7.6× | **10.2×** |, |

`VmStats()` now appends `gfast: N/M` counting `LOAD_GLOBAL_FAST`/`STORE_GLOBAL_FAST` executions
(100011/100007 on the `vm_opspec.slc` validation suite).

## Bytecode Export / Reload

`Bytecode.Export(fn, path)` serializes a VM-compiled function; `Bytecode.Load(path)` rehydrates it as
a VM-only shell and returns a callable `ScriptFunction`. Proven **cross-process** (blob written by one
exe run, executed by a separate process). Format: version + deterministic opcode fingerprint (rejects
incompatible compilers), consts by value (literals + `TypeInfo`), nested lambdas as shells (no AST),
load-time registry validation (missing types → clear error). See `assets/language/repl/vm_export*.slc`.

## Verification

30+ regression suites under `assets/language/repl/vm_*.slc` + `crash_*.slc` + `global_360.slc`
cover every VM path; `VmStats()` prints `VM: n (p%) | Tree: m (q%)` per run.
