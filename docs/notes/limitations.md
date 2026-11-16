Simple User-Facing Documentation
Add a note in your language documentation:

Known Limitation: Array creation with size specifier is not supported for generic delegate types (e.g., new Predicate<int>[3]). Use one of these workarounds:

new List<Predicate<int>>() and Add() each delegate

new Predicate<int>[] { lambda1, lambda2, lambda3 } (array initializer)

new Predicate<int>[3] with explicit casting per element

Recommendation
Given the complexity of a full fix, I recommend:

Add the warning – So users understand why it fails

Document the limitation – In your language reference

Use the workaround in tests – List<T> or array initializer syntax

The comprehensive test already passes with List<Predicate<int>>, so users have a clear alternative. The array size specifier for generic delegates is an edge case that affects few scripts.

----- 

Missing Pieces in the Showcase Script
After fixing all the regressions, here are the features that your interpreter still needs to fully support the original showcase script:

1. Object/Anonymous Type Literals
csharp
return new { deposit = (int amount) => { ... } { ... } };
Status: ❌ Not supported
Workaround: Use classes instead

2. Lambda Field Initializers in Classes/Structs
csharp
class Calculator {
    var add = (int a, int b) => { ... } => a + b;  // ❌
    Func<int,int,int> add = (...) => { ... } => ...; // ❌ (needs explicit type but may still fail)
}
Status: ❌ Not supported (needs explicit delegate types, and even then invocation is problematic)
Workaround: Use methods instead of delegate fields

3. Expression-bodied Methods (=> for methods)
csharp
int add(int a, int b) => a + b;  // ❌
Status: ❌ Not supported
Workaround: Use block body { return a + b; }

4. var Field Initializers in Classes/Structs
csharp
class MyClass {
    var field = 42;  // ❌
}
Status: ❌ Not supported (fields require explicit types)
Workaround: Use explicit types (int field = 42;)

5. Recursive Lambdas (self-reference)
csharp
var fib = (int n) => { ... } => n <= 1 ? n : fib(n-1) + fib(n-2);  // ❌
Status: ❌ Not supported (variable not yet bound in closure)
Workaround: Use named functions

6. Pure Keyword on Variable Assignments
csharp
pure var fib = (int n) => { ... } => ...;  // ❌
Status: ❌ Not supported (pure only on function declarations)
Workaround: Use named pure functions

7. Closure Capture of Mutable Variables in Nested Scopes (Regression)
csharp
var MakeGenerator(int start, int step) {
    var current = start;  // ❌ Not captured correctly
    return () => { ... } => { yield current; current += step; };
}
Status: ⚠️ Partially broken (regression)
Workaround: Use class with fields instead of closure

8. this Resolution in Lambdas Inside Nested Classes (Regression)
csharp
class Inner {
    string innerName;
    var getGreeting = () => { ... } => "Hello from " + this.innerName;  // ❌ 'this' refers to Outer
}
Status: ⚠️ Broken (regression)
Workaround: Capture local variable var name = this.innerName;

9. Delegate Field Invocation (without storing in variable first)
csharp
obj.delegateField();  // ❌ (looks for method, not field)
Status: ❌ Not supported
Workaround: var fn = obj.delegateField; fn();

10. Coroutine Lambda from Function Returning Lambda
csharp
return () => { ... } => { yield value; };  // Returns ScriptFunction, not Coroutine
Status: ⚠️ Works but returns wrong type
Workaround: Wrap in Coroutine.Start() before returning

Summary of What Your Interpreter Supports Well
Feature	Status
Named functions (pure/non-pure)	✅ Fully working
Lambda expressions (expression body)	✅ Fully working
Lambda block bodies	✅ Fully working
Lambda assignment to delegate variables	✅ Working (with explicit types)
Closures (capturing local variables)	✅ Working (read-only)
Delegate types (Action, Func, Predicate, etc.)	✅ Working
Delegate collections (List<Predicate<int>>)	✅ Working
Coroutines with lambdas (when wrapped)	✅ Working
Nested classes	✅ Working
Structs with methods	✅ Working
Ternary operators in lambdas	✅ Working
Pure function memoization	✅ Working
var local variables	✅ Working
var? nullable locals	✅ Working
Explicit type declarations	✅ Working
Recommendation for the Showcase Script
The showcase script should be simplified to use only the features that your interpreter fully supports. Avoid:

Object literals (new { ... })

Lambda field initializers (use methods instead)

Expression-bodied methods (use block bodies)

Recursive lambdas (use named functions)

Pure on variables (use named pure functions)

Direct delegate field invocation (store in variable first)

Complex closure capture (use classes with fields)

-----

Interpreter Improvement Roadmap
Phase 1: Easy Fixes (1-3 hours)
#	Issue	Complexity	Est. Time	Priority
1	Delegate field invocation – Allow obj.field() without storing in variable first	Low	30 min	High
2	pure keyword on variable assignments – Disable with clear error message	Low	15 min	Medium
3	Coroutine lambda return type – Auto-wrap in Coroutine.Start() when returning a lambda	Low	30 min	Medium
4	Expression-bodied methods – Support int add(int a, int b) => a + b;	Low	1 hour	Medium
5	var field initializers in classes – Auto-convert to explicit type with warning	Low	30 min	Low
Phase 2: Medium Complexity (4-8 hours)
#	Issue	Complexity	Est. Time	Priority
6	Closure capture of mutable variables – Fix regression with RefCell promotion	Medium	2 hours	High
7	this resolution in nested class lambdas – Capture correct this context	Medium	2 hours	High
8	Recursive lambdas – Implement fixed-point combinator or late binding	Medium	2 hours	Medium
9	Lambda field initializers – Support Func<int,int> add = (int x) => { ... } => x * 2;	Medium	1.5 hours	Medium
10	Array size specifier for generic delegates – new Predicate<int>[3]	Medium	2 hours	Low
Phase 3: Complex Features (10-20 hours)
#	Issue	Complexity	Est. Time	Priority
11	Object/Anonymous type literals – new { field = value, method = fun ... }	High	4-6 hours	Medium
12	Full generic type inference – Auto-detect generic arguments from context	High	4 hours	Low
13	Lambda parameter type inference – (x) => { ... } => x * 2 (infer from context)	High	3 hours	Low
14	Extension methods – Add support for obj.Method() syntax	High	4 hours	Low
15	LINQ-style query syntax – from x in list where x > 0 select x	High	6-8 hours	Very Low
Detailed Implementation Plan
Phase 1 Details
1. Delegate field invocation (30 min)
Current behavior:

c
obj.delegateField();  // Error: looks for method
Fix in VisitMethodCallExpr:

csharp
// If method not found, try to get field and invoke it
if (method == null && receiver is ClassInstance ci) {
    if (ci.FieldValues.TryGetValue(methodName, out var field) && field is ScriptFunction fn) {
        return InvokeScriptFunction(fn, args, line, column);
    }
}
2. Expression-bodied methods (1 hour)
Current:

c
int add(int a, int b) { return a + b; }  // OK
int add(int a, int b) => a + b;          // Error
Fix in FunctionDeclaration:

csharp
if (Match(TokenType.Arrow)) {
    var bodyExpr = Expression();
    var returnStmt = new ReturnStmt(bodyExpr, line, column);
    body = new List<Stmt> { returnStmt };
    Consume(TokenType.Semicolon, "Expect ';' after expression body.");
} else {
    Consume(TokenType.LeftBrace, "Expect '{' before function body.");
    body = Block();
}
3. var field initializers in classes (30 min)
Fix in ClassDeclaration:

csharp
if (memberName.Type == TokenType.Var) {
    Warn("'var' in field initializer is experimental. Use explicit type for stability.");
    // Treat as object
}
Phase 2 Details
6. Closure capture of mutable variables (2 hours)
Problem: Variables like current in MakeGenerator are not preserved.

Fix in VisitLambdaExpr:

Detect when a captured variable is modified after lambda creation

Promote to RefCell before capturing

Ensure RefCell is used for all accesses

7. this resolution in nested class lambdas (2 hours)
Problem: this inside lambda in nested class refers to outer class.

Fix: In VisitLambdaExpr, capture the correct this based on lambda's declaration context:

csharp
var currentThis = GetCurrentThisForLambda(); // Not _currentThis from execution
8. Recursive lambdas (2 hours)
Problem: var fib = (int n) => { ... } => n <= 1 ? n : fib(n-1) + fib(n-2); – fib undefined

Fix: Implement late binding or Y-combinator:

Option A: Allow self-reference via temporary variable

Option B: Transform to named function internally

Phase 3 Details
11. Object/Anonymous type literals (4-6 hours)
Syntax: new { name = value, method = (int x) => { ... } => x * 2 }

Implementation steps:

Create AnonymousObjectExpr AST node

Parse { name = expr, ... } after new

Create dynamic object at runtime with properties

Support method calls on anonymous objects

Summary
Phase	Total Time	Features Added
Phase 1	3 hours	5 easy features
Phase 2	10 hours	5 medium features
Phase 3	20 hours	5 complex features
Recommended order:

Start with Phase 1 (highest impact, lowest effort)

Then fix the closure regressions in Phase 2 (critical for correctness)

Evaluate if Phase 3 features are needed for your use cases

Immediate priorities (2-3 hours):

Delegate field invocation (#1)

Expression-bodied methods (#4)

Closure mutable capture fix (#6)

this resolution in nested classes (#7)

These four fixes will resolve most issues in the showcase script and make the interpreter much more user-friendly.