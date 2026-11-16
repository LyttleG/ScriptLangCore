# Expression Visitor

Located in `src/Interpreter/VisitExpressions.cs`  implements `IExprVisitor<object?>` with all expression evaluation methods.

## Coverage

Every expression type from `src/Ast/Nodes.cs` has a visit method:

| Expression Type | Method | Lines |
|----------------|--------|-------|
| Literals | `VisitNullExpr`, `VisitNumberExpr`, `VisitStringExpr`, `VisitBoolExpr`, `VisitCharExpr` | ~20 |
| Variables | `VisitVariableExpr`, `VisitAssignExpr` | ~120 |
| Operators | `VisitBinaryExpr`, `VisitUnaryExpr` | ~100 |
| Invocation | `VisitCallExpr` + `InvokeCallee` | ~200 |
| Properties | `VisitPropertyAccessExpr`, `VisitPropertyAssignExpr` | ~120 |
| Methods | `VisitMethodCallExpr` + `InvokeMethodDispatch` | ~300 |
| Type tests | `VisitIsExpr`, `VisitIsPatternExpr`, `VisitAsExpr`, `VisitCastExpr`, `VisitIsNullExpr` | ~100 |
| Control | `VisitConditionalExpr`, `VisitLogicalExpr` | ~20 |
| Arrays | `VisitNewArrayExpr`, `VisitArrayAccessExpr`, `VisitArrayAssignExpr` | ~20 |
| Mutations | `VisitCompoundAssignExpr`, `VisitIncrementDecrementExpr` | ~10 |

## Key Method: `VisitCallExpr`

The call expression dispatch resolves the callee through multiple paths:

1. **SuperProxy**  calls parent constructor
2. **EventInfo**  invokes subscribers and wakes coroutine waiters
3. **BoundMethod**  invokes method on a class instance
4. **CombinedDelegate**  invokes all handlers
5. **ScriptDelegateWrapper**  delegate wrapper from `as` cast
6. **ScriptFunction**  user-defined function
7. **INativeFunction**  built-in native function
8. **Delegate**  .NET `System.Delegate`

## ArrayPool Optimization

Method calls use `ArrayPool<object?>.Shared` to avoid per-call allocations for argument arrays when there are no `ref` arguments.
