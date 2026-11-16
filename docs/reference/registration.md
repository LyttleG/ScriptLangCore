# Built-in Function Registration

Located in `src/Interpreter/Registration/`  11 partial class files that register all built-in native functions.

## File Overview

| File | Registered Functions | Lines |
|------|---------------------|-------|
| `Helpers.cs` | `print`, `sleep`, `beep`, `abort`, `LoadAssembly`, `GetType`, `CreateInstance`, `Invoke`, `RunScript`, `ClearPureCache`, `GetPureCacheSize`, `GetScriptDepth`, `AssertEqual`, `AssertTrue`, `TypeName`, `pause`, `exitcode` (ref cells) | ~460 |
| `TypeAliases.cs` | `typeof`, `typename`, type aliases (`int`, `long`, `double`, `string`, `bool`, etc.), nullable types (`int?`, `double?`, etc.) | ~260 |
| `Bitwise.cs` | `ToBinary`, `ToBinaryWithBits`, `ToHex`, `ToHexWithPrefix`, `BitCount`, `IsPowerOfTwo`, `BitSet`, `BitClear`, `BitToggle`, `BitTest`, `RotateLeft`, `RotateRight`, `SwapBytes` | ~190 |
| `BaseDelegates.cs` | `Action`, `Func`, `Predicate`, `Comparison`, `Converter`, `EventHandler` templates, `CoroutineStatus` enum | ~220 |
| `Linq.cs` | `Where`, `Select`, `SelectMany`, `First`, `FirstOrDefault`, `Last`, `LastOrDefault`, `Any`, `All`, `ElementAt`, `ElementAtOrDefault`, `Count`, `Sum`, `Average`, `Min`, `Max`, `Take`, `Skip`, `OrderBy`, `OrderByDescending`, `ThenBy`, `Distinct`, `GroupBy`, `Join`, `GroupJoin`, `LeftJoin`, `RightJoin`, `Aggregate`, `Reverse`, `Contains`, `Zip` | ~800 |
| `Gc.cs` | GC operations via namespace | ~220 |
| `Mci.cs` | Media Control Interface namespace | ~180 |
| `Json.cs` | JSON namespace | ~120 |
| `Com.cs` | COM interop namespace | ~40 |
| `WinApi.cs` | Win32 API wrappers: `SetWindowHandler`, `LoadLibrary`, `FreeLibrary`, `GetFunction`, `Alloc`, `Free`, `Read/Write Int32/Int64/Pointer/String`, `MessageBox`, `ShowWindow`, `GetMessage`, `TranslateMessage`, `DispatchMessage`, `CreateWindowEx`, `RegisterClassEx`, plus `Colors` namespace and `Rect` | ~1100 |
| `Coroutines.cs` | `Coroutine.Start`, coroutine scheduling, HTTP/sleep/event yield support | ~110 |

## Registration Flow

```
Interpreter constructor
  └─ RegisterAllBuiltins()
       ├─ RegisterHelpers()        → print, sleep, assert, invoke, etc.
       ├─ RegisterTypeAliases()     → typeof, typename, type aliases, nullable types
       ├─ RegisterBitwiseFunctions()
       ├─ RegisterBaseDelegates()   → Action/Func/Predicate templates
       ├─ RegisterFunctions()       → App namespace, Colors, native functions
       ├─ RegisterLinqFunctions()
       ├─ RegisterGcFunctions()
       ├─ RegisterMciFunctions()
       ├─ RegisterComFunctions()
       ├─ RegisterWinApiFunctions()
       └─ RegisterCoroutines()      ← must be last
```

## Registration Pattern

Each built-in function follows:

```csharp
RegisterNative("functionName", TokenType.ReturnType, args =>
{
    // Validate args, convert, perform operation
    return result;
});
```

For complex APIs (Win32, UI), the registration files define full `partial class Interpreter` sections with P/Invoke declarations, helper types, and state management.
