# AST (Abstract Syntax Tree)

Located in `src/Ast/`  AST node types and object pool.

## Files

| File | Key Types | Purpose |
|------|-----------|---------|
| `Nodes.cs` | `IExprVisitor<T>`, `IStmtVisitor`, 40+ AST node types | Visitor interfaces + all AST node classes |
| `Pool.cs` | `IPoolable`, `AstPool`, `Pool<T>` | Object pooling for AST nodes |

## Visitor Interfaces

### `IExprVisitor<T>`

```csharp
interface IExprVisitor<out T>
{
    T VisitNumberExpr(NumberExpr node);
    T VisitStringExpr(StringExpr node);
    T VisitVariableExpr(VariableExpr node);
    T VisitBinaryExpr(BinaryExpr node);
    T VisitCallExpr(CallExpr node);
    // ... 25+ expression visit methods
}
```

### `IStmtVisitor`

```csharp
interface IStmtVisitor
{
    ExecutionResult VisitExprStmt(ExprStmt node);
    ExecutionResult VisitVarDeclStmt(VarDeclStmt node);
    ExecutionResult VisitIfStmt(IfStmt node);
    ExecutionResult VisitWhileStmt(WhileStmt node);
    ExecutionResult VisitFuncDecl(FuncDecl node);
    // ... 15+ statement visit methods
}
```

## Node Pattern

Every AST node follows the poolable factory pattern:

```csharp
internal sealed class BinaryExpr : Expr, IPoolable
{
    public Expr Left { get; private set; } = null!;
    public Token Operator { get; private set; }
    public Expr Right { get; private set; } = null!;

    public static BinaryExpr Create(Expr left, Token op, Expr right, int line, int col)
    {
        var rent = AstPool.Rent<BinaryExpr>();
        rent.Initialize(left, op, right, line, col);
        return rent;
    }

    public override void Reset()
    {
        Left = null!; Operator = default; Right = null!;
        Line = 0; Column = 0;
    }
}
```

## Node Hierarchy

```
AstNode
├── Expr (abstract)
│   ├── NumberExpr, StringExpr, BoolExpr, CharExpr, NullExpr
│   ├── VariableExpr, AssignExpr
│   ├── BinaryExpr, UnaryExpr
│   ├── CallExpr, MethodCallExpr, PropertyAccessExpr
│   ├── NewInstanceExpr, NewArrayExpr, NewStructExpr
│   ├── ArrayAccessExpr, ArrayAssignExpr
│   ├── ConditionalExpr, LogicalExpr
│   ├── CompoundAssignExpr, IncrementDecrementExpr
│   ├── LambdaExpr, YieldExpr
│   ├── IsExpr, IsPatternExpr, AsExpr, CastExpr, IsNullExpr
│   └── ThisExpr, SuperExpr, BaseExpr
└── Stmt (abstract)
    ├── ExprStmt, VarDeclStmt, ReturnStmt
    ├── BlockStmt, IfStmt, WhileStmt, ForStmt, ForeachStmt
    ├── FuncDecl, ClassDeclStmt, StructDeclStmt
    ├── ImportStmt, BreakStmt, ContinueStmt
    ├── DebuggerStmt, ThrowStmt, TryStmt
    └── DelegateDecl, EventDecl
```

## Pooling

See [docs/ast-pool.md](../ast-pool.md) for detailed pool architecture.
