# Core Types Reference

Located in `src/Core/`  fundamental types shared across the entire interpreter pipeline.

## Files

| File | Key Types | Purpose |
|------|-----------|---------|
| `Token.cs` | `Token`, `TokenType` | Lexer output  a token with type, literal text, position |
| `TypeInfo.cs` | `TypeInfo` | Runtime type descriptor (base type, nullable, array rank, generics) |
| `TypeArgList.cs` | `TypeArgList` | Optimized list of `TypeInfo` (inline storage for ≤4 args) |
| `TypeTokenHelper.cs` | `TypeTokenHelper` | Maps `TokenType` ↔ `System.Type`, detects base types |
| `FunctionFlags.cs` | `FunctionFlags` | `Legacy`, `Pure`, `Coroutine` flags |
| `TypeSystem.cs` | `TypeSystem` | Centralized type system: Name, TokenTypeOf, CanConvert, IsBaseType, IsNumeric |
| `Pattern.cs` | Pattern variants | AST nodes for switch pattern matching: Constant, Type, Var, Discard, Relational |
| `ControlFlow.cs` | `ControlFlow` | `Continue`, `Return`, `Break`, `ContinueLoop`, `Throw` |
| `ExecutionResult.cs` | `ExecutionResult` | Result of executing a statement (flow + value) |
| `ConstructorDecl.cs` | `ConstructorDecl` | Constructor declaration: params, refs, body, access |
| `Namespace.cs` | `Namespace` | Hierarchical namespace for native functions |
| `ScriptException.cs` | `ScriptException` | Exception thrown during script execution |
| `VoidMarker.cs` | `VoidMarker` | Singleton marker for void return values |
| `ErrorFormatter.cs` | `ErrorFormatter` | Pretty-prints script errors with source context |

## Architecture

```
Token                    ← Lexer output
  │
  ├── TypeInfo           ← Type descriptor used by Parser + Interpreter
  │     └── TypeArgList  ← Generic type argument storage
  │
  ├── TypeTokenHelper    ← Conversion utilities
  │
  ├── ExecutionResult    ← Pipeline output from statements
  │     └── ControlFlow  ← Flow control signals
  │
  └── ScriptException    ← Error signaling
        └── ErrorFormatter ← User-facing error display
```

## AOT Considerations

- `TypeArgList` uses aggressive inlining (`[MethodImpl(MethodImplOptions.AggressiveInlining)]`)
- `TypeSystem` uses switch expressions, all branches are compile-time constant
- `VoidMarker` is a sealed record with private constructor, safe for AOT
- `ErrorFormatter` uses `[GeneratedRegex]` for pattern matching
