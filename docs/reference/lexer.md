# Lexer

Located in `src/Lexer/Lexer.cs`  tokenizes source text into a stream of `Token` values.

## Overview

The Lexer is a single-pass, character-by-character scanner. It implements the classic "maximal munch" rule: when multiple tokens match, the longest one wins.

## Entry Point

```csharp
var lexer = new Lexer(source);
IEnumerable<Token> tokens = lexer.GetTokens();
```

## Token Categories

| Category | Examples | Lines |
|----------|----------|-------|
| Punctuation | `( ) { } [ ] , . ; :` | ~30 |
| Operators | `+ - * / % = ! < > & \| ^ ~` | ~50 |
| Compound operators | `+= -= *= /= %= ++ -- == != <= >= << >> && \|\|` | ~20 |
| Keywords | `if else while for return class fun var const import` | ~60 |
| Literals | integers, floats, strings, chars, booleans, null | ~80 |
| Identifiers | user-defined names | ~20 |

## Literal Scanning

### Numeric Literals

```csharp
1234      → decimal
0xFF      → hexadecimal
0b1010    → binary
3.14      → floating-point
100f      → float suffix
100L      → long suffix
100m      → decimal suffix
```

### String Literals

```csharp
"hello"        → regular string (escape sequences: \n, \t, \\, \", \0)
@"verbatim"    → verbatim string ("" → literal ")
```

### Char Literals

```csharp
'A'            → single character
'\n'           → escape sequence
```

## Identifier Resolution

An identifier follows the interpreter's case rule: **C#-strict by default**, so `myVar` and `myvar` are two
different names and a differently-cased one is reported, as an unknown variable, an unknown type or an unknown
member, depending on where it is written. The REPL's `.lax` mode relaxes *lookups* to any case for the session.
Keywords are matched case-insensitively, since `class`/`struct`/`if` are declaration keywords rather than names,
and a reserved keyword can be escaped to use it as an identifier:

```csharp
@class     → identifier "class" (not keyword)
@if        → identifier "if" (not keyword)
```

## Error Handling

Errors throw with the format: `"{message} at line {line}, column {column}."`
