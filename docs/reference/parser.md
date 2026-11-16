# Parser

Located in `src/Parser/Parser.cs`  recursive descent parser that builds an AST from the token stream.

## Overview

The Parser implements a Pratt-style recursive descent parser with the following precedence levels (lowest to highest):

| Level | Associativity | Operators |
|-------|---------------|-----------|
| Assignment | Right | `= += -= *= /= %=` |
| Conditional | Right | `? :` |
| Logical OR | Left | `\|\|` |
| Logical AND | Left | `&&` |
| Bitwise OR | Left | `\|` |
| Bitwise XOR | Left | `^` |
| Bitwise AND | Left | `&` |
| Equality | Left | `== !=` |
| Relational | Left | `< > <= >=` |
| Shift | Left | `<< >>` |
| Additive | Left | `+ -` |
| Multiplicative | Left | `* / %` |
| Unary | N/A | `- ! ~ ++ --` |
| Primary | N/A | literals, identifiers, `(expr)` |

## Entry Point

```csharp
var parser = new Parser(tokens, delegates);
IEnumerable<Stmt> statements = parser.GetStatements();
```

## Declaration Parsing

The `TopLevel()` method dispatches to:

1. **Import statements**  `import "path" [alias name];`
2. **Delegate declarations**  `delegate ReturnType Name(params);`
3. **Class declarations**  `[public|private] class Name [extends Parent] { members }`
4. **Struct declarations**  `[public] struct Name { fields }`
5. **Function declarations**  `[pure] ReturnType Name(params) { body }`
6. **Variable declarations**  `Type name [= expr];`

Within blocks, `Block()` uses `IsAtDeclaration()` to distinguish declarations from statements.

## Generic Type Parsing

The parser supports generic delegate instantiation:

```csharp
Action<Int>                      → CreateConcreteActionDelegate("Action<Int>", ...)
Func<String, Bool>               → CreateConcreteFuncDelegate("Func<String,Bool>", ...)
Predicate<Int?>                  → CreateConcretePredicateDelegate(...)
Comparison<DateTimeOffset?>      → CreateConcreteComparisonDelegate(...)
Converter<String, Int>           → CreateConcreteConverterDelegate(...)
```

## Lambda Parsing

Lambdas are parsed in `ParseLambda()`:

```csharp
(params) => expr                  // Expression body
(params) => { statements }        // Block body
```

`fun (params)` is the equivalent KEYWORD spelling (`fun (int x) => x` parses to the same
lambda), and it is the one still required by two shapes: `async fun (params)` (a bare
`async (params) =>` is a parse error) and a lambda with a `ref`/`out` parameter
(`fun (ref int x) =>`, where `(ref int x) =>` does not parse). Everywhere else the arrow
form is the spelling the documentation uses.

`ContainsYield()` auto-detects coroutine lambdas, in either spelling.

## Error Handling

Parser errors use the format: `"{message} at line {line}, column {column}."`
