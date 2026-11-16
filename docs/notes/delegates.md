# Delegate Types Reference

The ScriptLangCore interpreter supports generic delegate types similar to C#. Delegates are registered at interpreter startup via `RegisterBaseDelegates()`.

## Built-in Delegate Templates

### Action (void return, 0–4 parameters)

```csharp
Action                  // void()
Action<T>               // void(T)
Action<T1,T2>           // void(T1, T2)
Action<T1,T2,T3>        // void(T1, T2, T3)
Action<T1,T2,T3,T4>     // void(T1, T2, T3, T4)
```

### Func (with return value, 0–4 parameters + TResult)

```csharp
Func<TResult>                // TResult()
Func<T,TResult>              // TResult(T)
Func<T1,T2,TResult>          // TResult(T1, T2)
Func<T1,T2,T3,TResult>       // TResult(T1, T2, T3)
Func<T1,T2,T3,T4,TResult>    // TResult(T1, T2, T3, T4)
```

### Predicate

```csharp
Predicate<T>   // bool(T)
```

### Comparison

```csharp
Comparison<T>  // int(T, T)
```

## Nullable Type Arguments

All delegate templates accept nullable type arguments:

```csharp
Action<Int?>                 // void(Nullable<int>)
Action<Double?>              // void(Nullable<double>)
Action<Bool?>                // void(Nullable<bool>)
Func<Int?, String>           // String(Nullable<int>)
Func<String, Int?>           // Nullable<int>(String)
Predicate<Int?>              // bool(Nullable<int>)
Comparison<DateTime?>        // int(Nullable<DateTime>, Nullable<DateTime>)
```

## Concrete Instantiation

When the parser encounters a generic delegate reference like `Action<Int>`, it calls `ParseGenericTypeInstantiation()` which:

1. Parses the type arguments inside `<>`
2. Builds a concrete name string (e.g., `"Action<Int>"`)
3. Checks the `_delegates` cache for an existing entry
4. Creates a new `DelegateInfo` via `CreateConcreteActionDelegate()` if not cached
5. Caches and returns the result

## Runtime Behavior

- Delegates are stored in `Interpreter._delegates` (a `Dictionary<string, DelegateInfo>`)
- The `Parser` receives a reference to this dictionary for resolving delegate types during parsing
- `as` casts from `ScriptFunction` to delegate types create `ScriptDelegateWrapper` instances
- Combined delegates (via `+` operator) are represented as `CombinedDelegate` records
