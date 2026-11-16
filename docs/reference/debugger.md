# Script Debugger

Located in `src/Debugger/ScriptDebugger.cs`  interactive debugger for script execution.

## Overview

The debugger allows stepping through script execution with breakpoints, variable inspection, and call stack navigation.

## States

| State | Description |
|-------|-------------|
| `Running` | Normal execution |
| `Paused` | Execution suspended at breakpoint |
| `StepOver` | Continue to next statement (same scope) |
| `StepInto` | Continue to next statement (enter calls) |

## Usage

```csharp
Interpreter.EnableDebugger(breakOnFirst: true);
```

When paused, the debugger accepts:
- `h` / `help`  list commands
- `c` / `continue`  resume execution
- `n` / `next`  step over
- `s` / `step`  step into
- `p` / `print <var>`  print variable value
- `l` / `list`  show current line context
- `b` / `break <line>`  set breakpoint
- `q` / `quit`  stop debugging

## Integration

- `ShouldBreak(Stmt)` checks breakpoints + step state
- `Break()` pauses execution and enters the REPL loop
- `_debugger` is a static field on `Interpreter`, accessible across all instances
- Console availability is checked via `GetConsoleWindow()`  debugger auto-disables if no console
