# Scope System

Located in `src/Interpreter/Scope.cs`  manages variable scopes and closure snapshots.

## ScopeStack

The `ScopeStack` is the core of variable resolution:

```
Stack frames (top = innermost):
  ┌──────────────────────┐
  │ Local scope (push)   │ ← new BlockStmt pushes
  ├──────────────────────┤
  │ Local scope (push)   │
  ├──────────────────────┤
  │ Global scope (root)  │ ← Interpreter constructor
  └──────────────────────┘
```

### Key Operations

| Method | Description |
|--------|-------------|
| `Define(name, value)` | Adds variable to current (top) scope |
| `DefineConst(name, value)` | Adds immutable variable |
| `Get(name)` | Looks up variable across all scopes + parent snapshot |
| `Set(name, value)` | Mutates existing variable, checks const-ness |
| `PushScope()` | New scope for block/loop body |
| `PopScope()` | Exit scope |

## Closure Snapshots

When a function is declared, `CaptureClosureSnapshotWithGlobal()` creates a snapshot of:

1. **Local scopes**  captured by value (with `CloneValue` for `RefCell`)
2. **Global scope**  reference to the interpreter's real global scope
3. **Const sets**  which variables are immutable

On function invocation, `RestoreClosureSnapshot()`:

1. Clears current scopes
2. Restores the snapshot's global scope as the bottom frame
3. Restores local scopes in reverse order
4. Pushes a new top scope for local variables of the resumed function
5. Sets `_writableGlobalScope` to the restored global scope

## RefCell

`RefCell` wraps mutable captured variables in a getter/setter pair:

```csharp
internal sealed record RefCell(Func<object?> Getter, Action<object?> Setter)
```

Used when the interpreter detects that a captured variable might be mutated after capture.
