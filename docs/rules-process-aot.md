# Process Output Handling, AOT / JIT Rules

## Stream Output Ordering

**Rule:** Never use `ThreadPool.QueueUserWorkItem` (via `InvokeOnBackgroundThread`) to dispatch process output callbacks.

**Why:** `OutputDataReceived` fires sequentially on the stdout reader thread, but queuing work items to ThreadPool does **not** guarantee FIFO order. Three rapid lines (`echo line1 & echo line2 & echo line3`) can be delivered in the wrong order (line3 before line2), both in JIT and AOT.

**Pattern:**
```csharp
// ❌ Bad, reorders output lines
outputHandler = (_, e) => {
    if (e.Data != null)
        handle.InvokeOnBackgroundThread(() =>
            InvokeScriptFunction(onOutput, [e.Data], 0, 0));
};

// ✅ Good, preserves line order
outputHandler = (_, e) => {
    if (e.Data != null) {
        System.Threading.Monitor.Enter(handle._invokeLock);
        try { InvokeScriptFunction(onOutput, [e.Data], 0, 0); }
        finally { System.Threading.Monitor.Exit(handle._invokeLock); }
    }
};
```

## Lock Type, AOT Compatibility

**Rule:** Use `object` not `Lock` for synchronization in lambdas captured by closures.

**Why:** `System.Threading.Lock` (`.NET 9+`) uses `EnterScope()`/`Lock.Scope.Dispose()` under `lock()`. AOT trimming may interfere with `Lock.Scope`. Additionally, `lock(handle._invokeLock)` where `_invokeLock` is `Lock` produces CS0185 when used inside a lambda captured from an outer class's nested class. Use `Monitor.Enter/Exit` on `object` instead.

**Pattern:**
```csharp
// ❌ Bad, AOT may not preserve Lock.Scope
private readonly Lock _invokeLock = new();
lock (handle._invokeLock) { ... }

// ✅ Good, Monitor.Enter/Exit is AOT-safe
internal readonly object _invokeLock = new();
System.Threading.Monitor.Enter(handle._invokeLock);
try { ... }
finally { System.Threading.Monitor.Exit(handle._invokeLock); }
```

## Lock Field Visibility, Lambda Access

**Rule:** Make the lock field `internal` (not `private`) when accessed from inside a lambda that captures a `ProcessHandle` instance, even within the same outer class.

**Why:** The C# compiler generates a separate closure class for the lambda, which does **not** have access to `private` members of `ProcessHandle` (a nested class of `Interpreter`).

**Pattern:**
```csharp
internal readonly object _invokeLock = new();
```

## Verification

| Test | Environment | Expected |
|------|-------------|----------|
| `process_comprehensive_test.slc` | `dotnet run -c Release` | 19/19 ✅ |
| `process_comprehensive_test.slc` | `dotnet publish -c Release` | 19/19 ✅ |
| Output line order (echo & chaining) | Both JIT and AOT | line1, line2, line3 in order |
