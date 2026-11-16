# Coroutine System

Located in `src/Interpreter/CoroutineTypes.cs`  types supporting coroutine execution with `yield`.

## Key Types

| Type | Description |
|------|-------------|
| `CoroutineStatus` | `NotStarted`, `Suspended`, `Dead`, `Waiting` |
| `Coroutine` | A resumable coroutine instance |
| `CoroutineState` | Saved state for coroutine resumption |
| `CoroutineYieldRequest` | Base class for yield reasons |
| `HttpRequest` | Coroutine yielded for HTTP |
| `SleepRequest` | Coroutine yielded for sleep/delay |
| `EventWaitRequest` | Coroutine yielded for event |
| `YieldException` | Exception used to implement `yield` (caught by interpreter) |
| `LoopFrame` | Saved loop state for coroutine-aware loops |
| `LoopType` | `While`, `For`, `Foreach` |

## Coroutine Lifecycle

```
1. Function declared with 'yield' in body
2. Interpreter auto-detects → sets FunctionFlags.Coroutine
3. User calls: var co = myFunc()
4. co.Resume() → enters InvokeCoroutineBody()
5. On 'yield': YieldException is thrown, state saved
6. co.Resume() again → restores state, continues
7. On return → Status = Dead
```

## Yield Types

### `yield value;`
Suspends the coroutine and yields a value to the caller.

### `yield http(method, url, body);`
Creates an `HttpRequest`  the interpreter dispatches it via `HttpClient` and resumes the coroutine when complete.

### `yield sleep(ms);`
Creates a `SleepRequest`  the coroutine is scheduled in `_sleepQueue` and resumed after the delay.

### `yield event(eventName);`
Creates an `EventWaitRequest`  the coroutine waits in `_eventWaiters` until the event is triggered.

## Loop State

For coroutine-aware loops, a `LoopFrame` tracks:
- Loop type and phase
- Statement index within the body
- Iterator/enumerator state
- Scope active flag (for scope cleanup)

## Scheduling

`_sleepQueue` is a `SortedSet<ScheduledCoroutine>` ordered by wake time. `ProcessSleepQueue()` is called periodically to resume ready coroutines.
