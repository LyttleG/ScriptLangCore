# Architecture

## Design Philosophy

ScriptLangCore is a tree-walking interpreter  it parses source code into an AST and interprets it directly without compiling to bytecode or IL. This design was chosen for:

1. **AOT compatibility**  no runtime code generation
2. **Simplicity**  straightforward pipeline
3. **Debugability**  each node maps to a source location
4. **Flexibility**  easy to add new language features

> **Note:** A lightweight stack-based bytecode VM (POC) now serves as a *fast path* for a subset of VM-safe functions, with automatic fallback to the tree-walker. See [perf/bytecode-vm-poc.md](perf/bytecode-vm-poc.md) for architecture, supported constructs, and performance measurements.

## Pipeline Detail

### 1. Lexing (`src/Lexer/Lexer.cs`)

The Lexer reads source text character-by-character and produces a lazy `IEnumerable<Token>` stream. It handles:

- **Keywords**  case-insensitive matching
- **Identifiers**  `@` prefix for escaped keywords
- **Literals**  decimal, hex (`0x`), binary (`0b`), floats with suffixes (`f`, `d`, `m`, `l`)
- **Strings**  regular (with escape sequences) and verbatim (`@"..."`)
- **Comments**  `//` line and `/* block */`

### 2. Parsing (`src/Parser/Parser.cs`)

The Parser uses recursive descent with Pratt-style precedence climbing. It transforms the flat token stream into a hierarchical AST.

Key design points:
- **Operator precedence**  14 levels from assignment to primary
- **Top-level dispatch**  distinguishes imports, classes, functions, variables
- **Generic types**  `Action<T>`, `Func<T,R>`, etc. are instantiated on-demand
- **Error recovery**  throws on syntax errors with line/column information

### 3. Interpreting (`src/Interpreter/`)

The Interpreter uses the Visitor pattern to walk the AST. Each node type has a corresponding `Visit*` method that evaluates or executes it.

**Evaluation** (`VisitExpressions.cs`): Expressions produce values. The interpreter handles:
- Type coercion between numeric types
- Method overload resolution via reflection
- ArrayPool optimization for argument passing
- Coroutine-aware call dispatch

**Execution** (`VisitStatements.cs`): Statements produce `ExecutionResult` (control flow + optional value). The interpreter handles:
- Scoped execution with `ScopeStack`
- Coroutine-aware loops with `LoopFrame` state management
- Closure capture via `ClosureSnapshot`

#### Execution Model, Two-Pass `Interpret()`

The `Interpret()` method in `Interpreter.cs` runs in two passes:

1. **Pass 1, Declaration registration:** Iterates all top-level statements; `FuncDecl` nodes are visited via `VisitFuncDecl()` to register function declarations before any body executes. This enables forward references between functions.
2. **Pass 2, Execution:** Iterates all statements in order via `Execute(stmt)`. Each statement's `Visit*` method runs the actual runtime logic.

Runtime errors throw `ScriptException` (expected by the script model) or `ScriptAbortException` (for `exit` / `.exit`). Unexpected .NET exceptions are caught and forwarded with `HandleRuntimeError()`.

#### Module System (`import`)

When an `import "./path.inc"` statement is visited (VisitImportStmt in VisitStatements.cs):

1. **Path resolution** `ResolvePath()` combines the importing script's directory with the relative path.
2. **Cache lookup** `_modules[resolvedPath]` is checked. If the exact source content was already loaded via another path, the content-hash dedup (`_moduleContentHashes`) reuses the cached exports.
3. **Circular import detection** `_loadingModules` tracks the recursion stack. If a re-entrant load is detected, a warning is emitted and an empty module is returned.
4. **Module interpreter** A **fresh** `Interpreter` instance is created (`using var moduleInterpreter = new Interpreter(...)`). This module interpreter:
   - Runs its own `RegisterAllBuiltins()` (creating its own `_classes`, namespaces, and native lambdas)
   - Parses and interprets the module source
   - Exports its resulting global scope as a dictionary
5. **Export filtering** The `LoadModule()` method filters the module's global scope:
   - `Namespace` objects are **skipped** (line 3020), built-in namespaces (`Sqlite`, `Http`, `JSON`, etc.) belong to the main interpreter only and their lambdas capture the module interpreter's `_classes` which would be invalid after the module interpreter is disposed.
   - Non-public `FuncDecl` and `ClassInfo` values are skipped (private by default).
6. **Re-export into the importing interpreter** `VisitImportStmt` merges the filtered exports into the importing interpreter's scope and **type dictionaries** (`_classes`, `_delegates`, `_structs`), making script-defined types available for native method resolution.
7. **Module disposal** The `using` statement disposes the module interpreter, which calls `_classes.Clear()` on the module interpreter, the main interpreter's `_classes` is unaffected because it received copies via step 6.

**Important:** Native method lambdas registered during `RegisterAllBuiltins()` capture a reference to the registering interpreter's `_classes` field. This is why step 5 skips `Namespace` objects: passing a namespace from a module interpreter would give the main interpreter access to lambdas that reference a disposed `_classes`.

#### Type Registration Flow

Script-defined types are tracked in three dictionaries on the interpreter:

| Dictionary | Type | Registered by |
|---|---|---|
| `_classes` | `Dictionary<string, ClassInfo>` | `VisitClassDecl`, `VisitImportStmt`, `Reset()` |
| `_delegates` | `Dictionary<string, DelegateInfo>` | `VisitDelegateDecl`, `VisitImportStmt`, `Reset()` |
| `_structs` | `Dictionary<string, StructDeclStmt>` | `VisitStructDeclStmt`, `VisitImportStmt`, `Reset()` |

When a class is declared (`VisitClassDecl` in VisitStatements.cs):
1. The class is registered in `_classes[name]`.
2. It is also defined in the current scope via `CurrentFrame.Scopes.Define(name, classInfo)`.
3. If the interpreter is currently recording exports (`_recordingExports = true`), the class is added to `_currentExports`.

**Native method class resolution (scope fallback):** Generic typed methods like `FromSqlOnDb<T>` look up type argument names in `_classes`. Since module import merges classes into both `_classes` and the scope, a **scope fallback** provides defense-in-depth:

```csharp
if (!_classes.TryGetValue(typeName, out var classInfo))
{
    // Fallback: try to resolve via current scope
    if (CurrentFrame.Scopes.TryGet(typeName, out var scopeVal) && scopeVal is ClassInfo ci)
        classInfo = ci;
    else
        throw new ScriptException($"Class '{typeName}' not found.");
}
```

This pattern is used in `FromSqlOnDb<T>` and `FromSqlVOnDb<T,V>` in `SqliteCursor.cs`.

#### Interpreter Lifecycle

**Construction** (`Interpreter(bool caseSensitive)`):
1. Initializes all dictionaries with the chosen string comparer.
2. Creates a root `CallFrame` with an empty `ScopeStack`.
3. Calls `RegisterAllBuiltins()` which wires up every built-in function, namespace, and type:
   - `RegisterHelpers()` (CallNative, math helpers, HTTP)
   - `RegisterTypeAliases()` (type info reflection)
   - `RegisterBaseDelegates()` (Action/Func templates)
   - `RegisterFunctions()` (Application, Form, Console types)
   - `RegisterLinqFunctions()`, `RegisterFileFunctions()`, `RegisterWinApiFunctions()`, etc.
   - `RegisterSqliteCursor()` (creates the `Sqlite` namespace)
   - `RegisterCoroutines()` (must be last)
4. Captures the root frame as `_rootFrame`.

**Reset** (`Reset(bool keepGlobals, bool clearTypes)`):
- Called between script executions (e.g., REPL `.reset` or after `RunFile`).
- **`keepGlobals=false`:** Clears `_classes`, `_delegates`, `_structs`, `_modules`, then rebuilds them from the preserved global scope (keeping built-in types, classes, and namespaces; clearing user variables).
- **`keepGlobals=true`:** Preserves all global scope values including user-defined variables (REPL mode).
- **`clearTypes=true`:** Also clears all type definition dictionaries and frees loaded native libraries (full reset).
- Clears generic caches, AST pools, method/property caches, and execution state flags.

**Dispose** (`Dispose()`):
- Frees callback handles from `RegisterWinApiFunctions.GetFunctionPointer`.
- Pops the interpreter from the thread-static `_interpreterStack`.
- Clears all dictionaries including `_classes`, `_delegates`, `_structs`.
- Frees loaded native libraries.

## Type System

```
TokenType (keyword) ──→ TypeInfo ──→ System.Type
                              │
                         TypeArgList
                         (generic args)
```

- `TypeInfo` is the runtime type descriptor (base type, nullable, array rank, jagged depth, type name, generic args)
- `TypeTokenHelper.GetSystemType()` maps `TypeInfo` to `System.Type` for .NET interop
- `CoerceTo()` handles implicit and explicit type conversions

## Memory Management

### AST Node Pooling

All AST nodes are pooled via `AstPool` to reduce GC pressure during parsing:
- Per-type static pools using `ConcurrentQueue`
- Thread-local tracking with `[ThreadStatic]`
- Bulk cleanup after each script execution

### Argument Array Pooling

Method calls use `ArrayPool<object?>.Shared` for temporary argument arrays, avoiding per-call allocations.

## AOT Strategy

The project uses several techniques for NativeAOT compatibility:
1. **`[DynamicDependency]`**  prevents trimming of dynamically-accessed types
2. **`[UnmanagedFunctionPointer]`**  for Win32 callback marshaling
3. **Static type caches**  `ListFactoryCache`, `DictFactoryCache` for generic collections
4. **No `Expression` trees**  no runtime compilation
5. **No `Assembly.Load`**  no dynamic module loading

## ScriptFunction → Native Delegate Bridging

When a script function (`ScriptFunction`) is passed to a .NET method expecting a native delegate type (`Action<T>`, `Func<T,R>`, etc.), the interpreter's method-resolution layer automatically wraps it.

### Resolution Flow

1. **`IsTypeCompatibleForInvoke`** (`VisitExpressions.cs` line 1649): Accepts `ScriptFunction` as compatible with any `Delegate`-typed parameter.
2. **`CoerceArgument`** (`VisitExpressions.cs` line 1666): Dispatches to `AsDelegate` when target type is a delegate and argument is a `ScriptFunction`.
3. **`AsDelegate`** (`VisitExpressions.cs` line 1680): Inspects the delegate's `Invoke` signature (parameter count and return type). Creates a closed generic adapter type (`ActionAdapterN` or `FuncAdapterN`) via `MakeGenericType`, instantiates it with the captured `ScriptFunction` + `Interpreter.Current`, and returns `Delegate.CreateDelegate` pointing to the adapter's `Invoke` method.
4. **Adapter Invoke** (`Interpreter.cs` lines 727–781): Each adapter's `Invoke` method calls `Interpreter.InvokeScriptFunction` with the captured function and marshaled arguments. Return values are coerced via `Convert.ChangeType`.

### Supported Signatures

| Shape | Adapter | Parameter count |
|---|---|---|
| `Action` | `ActionAdapter0` | 0 |
| `Action<T1>` | `ActionAdapter1<T1>` | 1 |
| `Action<T1,T2>` | `ActionAdapter2<T1,T2>` | 2 |
| `Action<T1,T2,T3>` | `ActionAdapter3<T1,T2,T3>` | 3 |
| `Func<R>` | `FuncAdapter0<R>` | 0 |
| `Func<T1,R>` | `FuncAdapter1<T1,R>` | 1 |
| `Func<T1,T2,R>` | `FuncAdapter2<T1,T2,R>` | 2 |
| `Func<T1,T2,T3,R>` | `FuncAdapter3<T1,T2,T3,R>` | 3 |

### AOT Safety

Adapter types are registered with `[DynamicDependency]` in `AotHints.cs` to prevent trimming. No `Expression.Compile()` or runtime IL generation is used, all adapters are pre-compiled generic classes.

### Closure Mutation Propagation

When a `ScriptFunction` is invoked via `InvokeScriptFunction`, a fresh scope stack is created with the closure snapshot as parent. The `_writableGlobalScope` is always linked to the root interpreter's global scope (removed the `LocalScopes.Count == 0` guard), ensuring that assignments to outer-scope `var` variables inside closures propagate back to the caller.

## COM Interop (`src/Interop/COM/`)

### Architecture

The COM interop layer uses raw vtable dispatch (no `System.Type.InvokeMember`) for NativeAOT compatibility. Three key types form the bridge:

- **`ComScope`**, Manages COM apartment initialization (`CoInitialize`/`CoUninitialize`) and owns a tracked set of `ComObject` instances. Created via `ComScope.Create()` and disposed via `using`. All tracked objects are released in LIFO order on dispose.
- **`ComObject`**, Wraps an `IDispatch` pointer with refcounting. Provides `GetProperty<T>`, `SetProperty`, `InvokeMethod`, `InvokeMethodResult<T>` using raw vtable calls through `DispatchInvoker`.
- **`ComNode`**, Implements `IDynamicPropertyProvider` to expose COM objects to the script engine as dynamic objects. Property get/set and method calls on a `ComNode` are forwarded to COM dispatch.

### `Com.Run` Callback

`Com.Run(fun(scope) { ... })`:
1. Creates a `ComScope` (STA apartment initialized).
2. Stores it in the thread-static `_currentScope`.
3. Passes the `ComScope` as the first argument to the script callback via `InvokeScriptFunction(callback, [scope], ...)`.
4. Inside the callback, `Com.CreateObject` and `Com.GetObject` read `_currentScope` to access the active scope.
5. The callback can also receive the scope as a parameter: `Com.Run(fun(s) { s.CreateObject(...) })`, `ComScope.CreateObject`/`GetObject` resolve via normal .NET reflection.

### Dispatch: Handling Variant Return Types

Methods on COM objects are dispatched through `TryInvokeMethod` in `ComNode` using a single path with typed result unwrapping:

1. **Primary path, `InvokeMethodResult<ComObject>`**: Calls with `DISPATCH_METHOD | DISPATCH_PROPERTYGET` combined flags. If the result variant is a COM object, it's tracked and returned as a `ComNode`. If the result is `VT_EMPTY` or `VT_NULL`, returns `this` (the method had no meaningful return value).

2. **`InvalidCastException` catch, no re-invoke**: When a void method (`Close`, `Quit`, `Select`) returns `VT_BOOL` (a COM success indication rather than a COM object), the cast from variant to `ComObject` throws `InvalidCastException`. The catch returns `this` **without re-invoking**, the method already executed successfully. The previous behavior called `InvokeMethodResult<object>` (which re-invoked the method) on the already-closed/disconnected object, causing `RPC_E_DISCONNECTED` (0x80010108).

3. **`Exception` catch, graceful degradation**: Catches any other COM errors (disconnects, timeouts) and returns `this`, preventing unhandled exceptions from propagating to the script.

### COM Collection Enumeration

Three enumeration paths exist on `ComNode`:

- **`ForEach(Action<ComNode>)`**: Iterates via `IEnumVARIANT`, wrapping each item in a `ComNode`. Handled directly in `TryInvokeMethod` (bypasses delegate wrapping) to avoid COM re-entrancy issues during enumeration.
- **`ForEach<T>(Func<ComNode, T>)`**: Same enumeration with a value-returning callback.
- **`AsEnumerable()`**: Returns `IEnumerable<ComNode>` for use with script `foreach` loops. No delegate wrapping involved.

### GetObject via GetActiveObject

`Com.GetObject(progId)` uses `GetActiveObject` (oleaut32) for direct CLSID lookup in the Running Object Table, no more enumeration of all running COM objects. Replaced the previous `IRunningObjectTable::EnumRunning` loop which iterated 20+ system COM objects and hung on debug output.

### AOT Hints for COM Types

All COM interop types (`ComAutomation`, `ComObject`, `ComNode`, `ComScope`) are explicitly preserved via `[DynamicDependency]` in `AotHints.cs` to prevent NativeAOT trimming of their public methods. `InvalidCastException` is also rooted for the dispatch catch path.

## UI Architecture

### Forms and Controls (`src/Interpreter/Registration/Ui.cs`)

The UI system wraps Win32 API calls into script-accessible classes:

- **`Control`**, abstract base class for all UI elements. Manages HWND creation, window procedure routing, DPI scaling via `StoreDesignBounds()`/`ScaleByDpi()`, font management, and dark mode colors via `WM_CTLCOLOR*` handlers.

- **`Form`** (via `Application.CreateForm`), top-level window with a `ControlCollection`. Provides factory methods (`CreateLabel`, `CreateButton`, etc.) for child controls. Supports dark mode, per-monitor DPI scaling (`WM_DPICHANGED`, `WM_GETDPISCALEDSIZE`), and the `Closing`/`Closed`/`Load` events.

- **`MdiForm`** (via `Application.CreateMdiForm`), MDI frame window containing an `"MDIClient"` system window. Frame-level controls sit above the client area; MDI children sit inside it. Manages MDI children via `CreateMdiChild`, `TileChildren`, `CascadeChildren`, `ArrangeIcons`. DPI scaling enumerates both frame controls and MDI children. Dark mode applies to frame controls, MDIClient background (via subclassed `WM_ERASEBKGND`), and all MDI children.

- **`MdiChild`**, MDI document window created via `WM_MDICREATE` to the MDIClient. Handles its own `WndProc` via `DefMDIChildProcW`. Exposes the same control factory methods as `Form`. Supports dark mode and DPI scaling.

### DPI Scaling Strategy

Both `Form` and `MdiForm` use per-monitor DPI awareness (`PerMonitorV2`). The system sends `WM_DPICHANGED` when the window moves to a monitor with different DPI. The handler:

1. Calls `DefWindowProcW` / `DefFrameProcW` first to let Windows process the DPI transition
2. Then calls `ScaleByDpi(scaleX, scaleY)` on each control using stored `_designBounds` values
3. Updates fonts via `CreateDpiFont` + `WM_SETFONT` on all child windows
4. Redraws with `RedrawWindow(RDW_ALLCHILDREN)`

The `WM_SIZE` handler does NOT overwrite `_designWidth`/`_designHeight` (this was the cause of progressive shrinking), keeping the authored design size as the stable baseline for DPI calculations.

### Mutualization

The three UI classes (`Form`, `MdiForm`, `MdiChild`) share a common infrastructure:

- **Control factories**, All 11 control types (`Label`, `Button`, `TextBox`, `CheckBox`, `RadioButton`, `ComboBox`, `ListBox`, `ListView`, `TreeView`, `DateTimePicker`, `TrackBar`) are available identically on `Form`, `MdiForm`, and `MdiChild`.
- **Events**, `Load`, `Closing`, `Closed`, and `ResizeEvent` are available on all three surfaces. `MdiChild` events are wired through `InvokeSafely` using the `_interpreter` reference passed from `MdiForm`. Exceptions in event handlers are caught silently (matching Form's pattern).
- **Dark mode**, All three surfaces use the same brush pattern (EDIT=0x303030, LISTBOX=0x282828, BUTTON=0x404040, background=0x202020). `MdiForm` cascades dark mode to its `MdiClient` via a subclassed `WM_ERASEBKGND` handler and to MDI children via `child.SetDarkMode(true)` on creation.

### Dark Mode Architecture

Dark mode uses three layers:
1. **Title bar**, `DwmSetWindowAttribute(DWMWA_USE_IMMERSIVE_DARK_MODE)`
2. **Control background**, `WM_CTLCOLOR*` messages return dark brushes per control type (EDIT=0x303030, LISTBOX=0x282828, BUTTON=0x404040, default=0x202020)
3. **Window background**, `WM_ERASEBKGND` fills with dark brush

For MDI, the `DarkMode_Explorer` theme is applied to frame-level controls and MDI child controls, but NOT to the MDIClient or MDI child windows themselves (which use `DefMDIChildProcW` painting). Instead, the MDIClient's `WM_ERASEBKGND` is subclassed for the dark background.

---

## Task Async System (`src/Interpreter/Task.cs`)

The Task system provides cooperative async primitives, all callbacks run on the main thread via a marshalling queue, avoiding the scope/call-stack corruption that would occur with background-thread execution of script functions.

### Files

| File | Purpose |
|------|---------|
| `src/Interpreter/Task.cs` | `Task` class (completion, continuation, Wait, Result, Status, Run, Delay, WhenAll, WhenAny) |
| `src/Interpreter/Registration/TaskRegistration.cs` | Registers `Task` namespace with `Run`/`Delay`/`WhenAll`/`WhenAny` |
| `src/Interpreter/AotHints.cs` | `[DynamicDependency]` for `Task`, `TaskStatusEnum`, `ManualResetEventSlim` |

### Architecture

```
┌──────────────────────────────────────────────────┐
│  Main Thread                                      │
│  ┌──────────────┐   ┌────────────────────────┐  │
│  │  Interpreter  │   │  Task.PumpPending()     │  │
│  │  (script eval)│   │  - drain queue           │  │
│  │               │   │  - check delay queue     │  │
│  └──────┬───────┘   └───────────┬────────────┘  │
│         │                        │                │
│  After each top-level stmt ──────┤ (auto-pump)    │
│         │                        │                │
│  ┌──────┴───────────────────────┐│                │
│  │  MainThreadQueue             ││                │
│  │  (ConcurrentQueue<Action>)   ││                │
│  │  Task.Run → enqueues here    ││                │
│  │  ContinueWith → enqueues here││                │
│  └──────────────────────────────┘│                │
│         │                        │                │
│  ┌──────┴───────────────────────┐│                │
│  │  DelayQueue                  ││                │
│  │  (SortedSet<(DateTime, Task)>││                │
│  │  Task.Delay → registers here ││                │
│  └──────────────────────────────┘│                │
└────────────────────────────────────┘
```

### Execution Model

| Method | Where callback runs | Queue |
|--------|-------------------|-------|
| `Task.Run(fn)` | Main thread (at next auto-pump) | `MainThreadQueue` |
| `ContinueWith(fn)` | Main thread (at next auto-pump) | `MainThreadQueue` |
| `Task.Delay(ms)` | Blocks calling thread | `s_delayQueue` (polled by `PumpPending()`) |
| `Task.WhenAll(list)` | Blocks until all subtasks complete | Composite via `SetDirectContinuation` + `Wait()` |
| `Task.WhenAny(list)` | Blocks until any subtask completes | Composite via `SetDirectContinuation` + `Wait()` |

### Auto-Pump

`Task.PumpPending()` is called automatically after **every top-level statement** in the interpreter's execution loop (`Interpreter.cs` line 3291). It:

1. Checks the `s_delayQueue` for elapsed wake times and completes them
2. Drains the `MainThreadQueue` (runs all queued callbacks in a `while` loop)

Additional Pump calls happen inside `Application.Run()` (message loop) and the REPL prompt loop.

### Wait() Behavior

When `Wait()` is called on an incomplete task, it:
1. Calls `PumpPending()` to drain any pending work
2. If still incomplete, creates a `ManualResetEventSlim` and sets `_continuation` to signal it
3. Polls `mres.Wait(50)` in a loop, calling `PumpPending()` after each timeout
4. This allows `Task.Delay` completions to be picked up during the wait

### AOT Hints

The following types are preserved for NativeAOT via `[DynamicDependency]`:
- `ScriptLangCore.Task`, exposes `Run`, `Delay`, `WhenAll`, `WhenAny`, `ContinueWith`, `Wait`, `Result`, `Status`
- `TaskStatusEnum`, `Running`, `Completed`, `Faulted`
- `ManualResetEventSlim`, used inside `Wait()` for blocking

