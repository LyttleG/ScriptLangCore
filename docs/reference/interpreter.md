# Interpreter  Core Runtime

Located in `src/Interpreter/Interpreter.cs`  the main interpreter runtime: fields, constructor, execution loop, type conversion, and native function registration.

## Overview

The Interpreter is a tree-walking interpreter that implements both visitor interfaces (`IExprVisitor<object?>` and `IStmtVisitor`). It is split across multiple files via `partial class Interpreter`.

## Key Sections

| Lines | Section | Description |
|-------|---------|-------------|
| 1–300 | Usings + class declaration | File-scoped namespace, usings, partial class header |
| 20–200 | Instance fields | Dictionaries, stacks, caches, coroutine state |
| 200–300 | AOT hints | Removed  see `AotHints.cs` |
| 300–600 | Helper types | `ScriptResult`, `ScheduledCoroutine`, `PureYieldKey`, `HttpClient` |
| 600–1200 | Nested runtime types | `RefCell`, `ScriptDelegateWrapper`, `ClassInfo`, `IScriptList`, `IScriptDictionary`, boxed/unboxed collections |
| 1200–1300 | Execution records | `StructInstance`, `ClassInstance`, `SuperProxy`, `BaseProxy`, `BoundMethod`, `CombinedDelegate`, `ModuleInfo`, `DelegateInfo`, `EventInfo`, `CallFrame` |
| 1300–1500 | Instance fields + context | `_callFrames`, `_currentThis`, `_currentClass`, `PushContext()`, Reset(), Dispose() |
| 1500–1750 | Constructor + RegisterAllBuiltins | Initializes dictionaries, calls all `Register*` methods |
| 1750–1950 | Native registration helpers | `RegisterFunctions()`, `RegisterNative()` |
| 1950–2400 | Static helpers | `RuntimeError`, `CheckAccess`, `CoerceTo`, `MapTypeInfo`, `IsTruthy` |
| 2400–2600 | Execution methods | `Evaluate()`, `Execute()`, `Interpret()`, `LoadModule()` |
| 2600–4500 | Runtime helpers | `CreateArray`, `GetArrayElement`, `SetArrayElement`, `InvokeScriptFunction`, `InvokeMethodOnInstance` |
| 4500–end | Coroutine runtime | `ResumeCoroutine`, `ProcessSleepQueue` |

## Construction

```csharp
using var itp = new Interpreter(caseSensitive: true);
```

On construction:
1. Allocates dictionaries with the chosen comparer
2. Pushes a root `CallFrame` with a fresh `ScopeStack`
3. Calls `RegisterAllBuiltins()` which registers every native function
4. Captures the populated root frame

## Execution Pipeline

```csharp
var source = File.ReadAllText(path);
var lexer = new Lexer(source);
var tokens = lexer.GetTokens();
var parser = new Parser(tokens, interpreter.Delegates);
var statements = parser.GetStatements();
interpreter.Interpret(statements, path);
```

## Type Conversion

`CoerceTo(object? value, TypeInfo targetType, int line, int column)` handles:

- Nullable conversions
- Numeric widening (int → long, float → double)
- String → primitive parsing
- Implicit casts between compatible types

## AOT Considerations

- `AotCompilerHint()` in `AotHints.cs` roots all dynamic types
- `[DynamicDependency]` attributes prevent trimming of used BCL types
- `[UnmanagedFunctionPointer]` is used for Win32 callbacks
