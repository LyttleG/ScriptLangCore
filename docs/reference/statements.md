# Statement Visitor

Located in `src/Interpreter/VisitStatements.cs`  implements `IStmtVisitor` with all statement execution methods.

## Coverage

| Statement Type | Method | Description |
|----------------|--------|-------------|
| Expression | `VisitExprStmt` | Evaluates expression, captures top-level result |
| Debugger | `VisitDebuggerStmt` | Pauses execution for debugger |
| Throw | `VisitThrowStmt` | Throws a script exception |
| Try/Catch/Finally | `VisitTryStmt` | Structured exception handling |
| Variable declaration | `VisitVarDeclStmt` | Declares + optionally initializes variables |
| Class declaration | `VisitClassDeclStmt` | Registers class in `_classes` dictionary; generic templates also create a synthetic `ClassInfo` with static fields; `delegate` declarations inside the class body are registered via `VisitDelegateDecl` |
| Struct declaration | `VisitStructDeclStmt` | Registers struct in `_structs` dictionary |
| Import | `VisitImportStmt` | Loads and imports a module |
| Delegate declaration | `VisitDelegateDecl` | Registers delegate type |
| Event declaration | `VisitEventDecl` | No-op (events handled at class level) |
| Return | `VisitReturnStmt` | Returns value from function |
| Block | `VisitBlockStmt` | Executes statements in new scope |
| If/Else | `VisitIfStmt` | Conditional branching |
| While | `VisitWhileStmt` | Loop with condition (coroutine-aware) |
| Foreach | `VisitForeachStmt` | Iteration over arrays, lists, dicts, coroutines (coroutine-aware) |
| Function declaration | `VisitFuncDecl` | Creates `ScriptFunction` with closure snapshot |
| For | `VisitForStmt` | C-style for loop (coroutine-aware) |
| Break | `VisitBreakStmt` | Exits loop |
| Continue | `VisitContinueStmt` | Skips to next iteration |

## Coroutine-Aware Loops

`VisitWhileStmt`, `VisitForStmt`, and `VisitForeachStmt` have dual code paths:

- **Fast path** (no coroutine): simple loop iteration
- **Coroutine path**: phase-based execution with `LoopFrame` state management, allowing suspension and resumption at any point

## Import Resolution

`VisitImportStmt` handles:
1. Path resolution (relative to current script directory)
2. Circular import detection via `_loadingModules`
3. Module caching via `_modules` dictionary
4. Alias-based namespace imports vs. direct scope injection
