# AstPool  AST Node Object Pool

The `AstPool` reduces allocations during parsing and interpretation by pooling AST nodes.

## Architecture

```
┌─────────────────────┐     ┌──────────────────────┐
│   RentNodes         │     │   Per-type Pools     │
│   (Thread-local)    │     │   (Shared, static)   │
│                     │     │                      │
│  Tracks what this   │     │  NumberPool  (NumberExpr)  │
│  thread rented      │     │  StringPool  (StringExpr)  │
│                     │     │  BinaryPool  (BinaryExpr)  │
│  (Cleared after     │     │  ...                    │
│   each execution)   │     └──────────────────────┘
└─────────────────────┘
```

## Usage

### Renting a node

```csharp
var node = NumberExpr.Create(...);
// → Gets from NumberPool (shared)
// → Added to RentNodes (thread-local tracker)
```

### Returning nodes

```csharp
AstPool.CleanupAll();
// → Each tracked node is Reset()
// → Enqueued back to its type pool
// → RentNodes cleared for the thread
```

## Key Types

| Type | File | Description |
|------|------|-------------|
| `IPoolable` | `src/Ast/Pool.cs` | Interface with `void Reset()` |
| `AstPool` | `src/Ast/Pool.cs` | Static entry point: `Rent<T>()`, `CleanupAll()` |
| `Pool<T>` | `src/Ast/Pool.cs` | Per-type generic pool with `ConcurrentQueue` + `ThreadStatic` tracking |

## AOT Note

Each `Pool<T>` instantiation is triggered by the first `AstPool.Rent<T>()` call. The `AotCompilerHint()` method in `AotHints.cs` ensures all pooled AST node types are rooted for NativeAOT compilation.

## Thread Safety

- `Rent()` is thread-safe (uses `ConcurrentQueue`)
- `CleanupAll()` is thread-local (uses `[ThreadStatic]` tracking)
- `ReturnAll()` only affects the calling thread's rented nodes
