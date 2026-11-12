namespace ScriptLangCore;

// ============================================================================
// 2. VISITOR INTERFACES
// ============================================================================

internal interface IExprVisitor<out T>
{
    T VisitNumberExpr(NumberExpr node);
    T VisitStringExpr(StringExpr node);
    T VisitBoolExpr(BoolExpr node);
    T VisitCharExpr(CharExpr node);
    T VisitNullExpr(NullExpr node);
    T VisitVariableExpr(VariableExpr node);
    T VisitAssignExpr(AssignExpr node);
    T VisitBinaryExpr(BinaryExpr node);
    T VisitUnaryExpr(UnaryExpr node);
    T VisitCallExpr(CallExpr node);
    T VisitPropertyAccessExpr(PropertyAccessExpr node);
    T VisitMethodCallExpr(MethodCallExpr node);
    T VisitConditionalExpr(ConditionalExpr node);
    T VisitTupleExpr(TupleExpr node);
    T VisitNewArrayExpr(NewArrayExpr node);
    T VisitArrayAccessExpr(ArrayAccessExpr node);
    T VisitArrayAssignExpr(ArrayAssignExpr node);
    T VisitRefAssignExpr(RefAssignExpr node);
    T VisitLogicalExpr(LogicalExpr node);
    T VisitCompoundAssignExpr(CompoundAssignExpr node);
    T VisitIncrementDecrementExpr(IncrementDecrementExpr node);
    T VisitNewInstanceExpr(NewInstanceExpr node);
    T VisitWithExpr(WithExpr node);
    T VisitObjectInitExpr(ObjectInitExpr node);
    T VisitThisExpr(ThisExpr node);
    T VisitPropertyAssignExpr(PropertyAssignExpr node);
    T VisitSuperExpr(SuperExpr node);
    T VisitLambdaExpr(LambdaExpr node);
    T VisitNewStructExpr(NewStructExpr node);
    T VisitBaseExpr(BaseExpr node);
    T VisitIsExpr(IsExpr node);
    T VisitIsPatternExpr(IsPatternExpr node);
    T VisitAsExpr(AsExpr node);
    T VisitCastExpr(CastExpr node);
    T VisitIsNullExpr(IsNullExpr node);
    T VisitYieldExpr(YieldExpr node);
    T VisitAwaitExpr(AwaitExpr node);
    T VisitListLiteralExpr(ListLiteralExpr node);
    T VisitDictLiteralExpr(DictLiteralExpr node);
    T VisitComprehensionExpr(ComprehensionExpr node);
    T VisitDiscardExpr(DiscardExpr node);
    T VisitReturnValueExpr(ReturnValueExpr node);
    T VisitCompiledDefaultExpr(CompiledDefaultExpr node);
    T VisitMatchExpr(MatchExpr node);
    T VisitTuplePatternExpr(TuplePatternExpr node);
    T VisitIsTuplePatternExpr(IsTuplePatternExpr node);
    T VisitIndexExpr(IndexExpr node);
    T VisitRangeExpr(RangeExpr node);
    T VisitSwitchExpr(SwitchExpr node);
    T VisitNullCoalescingExpr(NullCoalescingExpr node);
    T VisitTypeRefExpr(TypeRefExpr node);
}

internal interface IStmtVisitor
{
    ExecutionResult VisitExprStmt(ExprStmt node);
    ExecutionResult VisitVarDeclStmt(VarDeclStmt node);
    ExecutionResult VisitReturnStmt(ReturnStmt node);
    ExecutionResult VisitBlockStmt(BlockStmt node);
    ExecutionResult VisitIfStmt(IfStmt node);
    ExecutionResult VisitWhileStmt(WhileStmt node);
    ExecutionResult VisitForeachStmt(ForeachStmt node);
    ExecutionResult VisitFuncDecl(FuncDecl node);
    ExecutionResult VisitForStmt(ForStmt node);
    ExecutionResult VisitBreakStmt(BreakStmt node);
    ExecutionResult VisitDebuggerStmt(DebuggerStmt node);
    ExecutionResult VisitContinueStmt(ContinueStmt node);
    ExecutionResult VisitClassDeclStmt(ClassDeclStmt node);
    ExecutionResult VisitImportStmt(ImportStmt node);
    ExecutionResult VisitNamespaceDeclStmt(NamespaceDeclStmt node);
    ExecutionResult VisitDelegateDecl(DelegateDecl node);
    ExecutionResult VisitEventDecl(EventDecl node);
    ExecutionResult VisitThrowStmt(ThrowStmt node);
    ExecutionResult VisitTryStmt(TryStmt node);
    ExecutionResult VisitStructDeclStmt(StructDeclStmt node);
    ExecutionResult VisitEnumDeclStmt(EnumDeclStmt node);
    ExecutionResult VisitDestructuringStmt(DestructuringStmt node);
    ExecutionResult VisitSwitchStmt(SwitchStmt node);
    ExecutionResult VisitUsingStmt(UsingStmt node);
    ExecutionResult VisitUsingDeclStmt(UsingDeclStmt node);
    ExecutionResult VisitExtensionDeclStmt(ExtensionDeclStmt node);
    ExecutionResult VisitInterfaceDeclStmt(InterfaceDeclStmt node);
}

// ============================================================================
// 3. AST NODES
// ============================================================================

internal sealed record CatchClause(TypeInfo ExceptionType, string VarName, Stmt Body, int Line, int Column);

internal sealed record FieldDecl(AccessModifier Access, TypeInfo Type, string Name, Expr? Initializer, bool IsStatic = false)
{
    /// <summary>The field's initializer as a compiled zero-parameter body, so a bundle can carry it: the shape a
    /// parameter default already travels in (see <see cref="FuncDecl.DefaultThunks"/>), wrapped as the expression
    /// every construction path evaluates. Null on a source run, where the tree evaluates the AST expression, and
    /// null when the compiler did not take it (an initializer that reads the INSTANCE, since a thunk is an ordinary
    /// body with no `this`), which the strip refuses by name.</summary>
    public Bytecode? InitializerThunk { get; set; }
}

// ============================================================================
// 3.1 ABSTRACTIONS
// ============================================================================
internal abstract class AstNode
{
    public int Line { get; set; }

    public int Column { get; set; }
    /// <summary>The node's own SOURCE TEXT, when the parse held the file's text (see the parser's
    /// <c>SourceTextOf</c>), and null otherwise. Attached to a TOP-LEVEL STATEMENT by the parser, and read by the
    /// two things that travel as text instead of as bytecode: a DECLARATION whose body the compiler did not take
    /// (a function, a class, a struct, which the loader re-registers from it), and a STATEMENT the compiler could
    /// not compile, which a compiled program body hands back to the tree by this text.</summary>
    public string? SourceText { get; set; }
    /// <summary>The source position every node carries, stated ONCE: Line and Column live here, so the
    /// nine Expression/Statement types that repeated this assignment have nothing of their own to add.</summary>
    protected void Initialize(int line, int col)
    {
        Line = line;
        Column = col;
    }

}

internal abstract class Expr : AstNode
{
    public abstract T Accept<T>(IExprVisitor<T> visitor);
}

internal abstract class Stmt : AstNode
{
    public abstract ExecutionResult Accept(IStmtVisitor visitor);
}

// ============================= EXPRESSIONS NODES (Expr)======================

internal sealed class NumberExpr : Expr
{
    public string Value { get; private set; } = null!;
    public TypeInfo Type { get; private set; } = null!;

    private void Initialize(string value, TypeInfo type, int line, int col)
    {
        Value = value;
        Type = type;
        Line = line;
        Column = col;
    }

    public static NumberExpr Create(string value, TypeInfo type, int line, int col)
    {
        var rent = new NumberExpr();
        rent.Initialize(value, type, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitNumberExpr(this);
}

internal sealed class StringExpr : Expr
{
    public string Value { get; private set; } = null!;

    private void Initialize(string value, int line, int col)
    {
        Value = value;
        Line = line;
        Column = col;
    }

    public static StringExpr Create(string value, int line, int col)
    {
        var rent = new StringExpr();
        rent.Initialize(value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitStringExpr(this);
}

internal sealed class BoolExpr : Expr
{
    public bool Value { get; private set; }

    private void Initialize(bool value, int line, int col)
    {
        Value = value;
        Line = line;
        Column = col;
    }

    public static BoolExpr Create(bool value, int line, int col)
    {
        var rent = new BoolExpr();
        rent.Initialize(value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitBoolExpr(this);
}

internal sealed class CharExpr : Expr
{
    public char Value { get; private set; }

    private void Initialize(char value, int line, int col)
    {
        Value = value;
        Line = line;
        Column = col;
    }

    public static CharExpr Create(char value, int line, int col)
    {
        var rent = new CharExpr();
        rent.Initialize(value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitCharExpr(this);
}

internal sealed class NullExpr : Expr
{
    public static NullExpr Create(int line, int col)
    {
        var rent = new NullExpr();
        rent.Initialize(line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitNullExpr(this);
}

internal sealed class VariableExpr : Expr
{
    public string Name { get; private set; } = null!;

    private void Initialize(string name, int line, int col)
    {
        Name = name;
        Line = line;
        Column = col;
    }

    public static VariableExpr Create(string name, int line, int col)
    {
        var rent = new VariableExpr();
        rent.Initialize(name, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitVariableExpr(this);
}

internal sealed class AssignExpr : Expr
{
    public string Name { get; private set; } = null!;
    public Expr Value { get; private set; } = null!;

    private void Initialize(string name, Expr value, int line, int col)
    {
        Name = name;
        Value = value;
        Line = line;
        Column = col;
    }

    public static AssignExpr Create(string name, Expr value, int line, int col)
    {
        var rent = new AssignExpr();
        rent.Initialize(name, value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitAssignExpr(this);
}

/// <summary>The two operands and the operator BinaryExpr and LogicalExpr both carry, stated ONCE. The pair
/// had identical properties AND an identical Initialize, which tools/check-dup-bodies.sh cannot see because a
/// same-file, same-name group reads as an overload set. The setters stay private to this class, so Initialize
/// is the only way in, which is why it is protected: each derived Create() calls it.</summary>
internal abstract class BinaryLikeExpr : Expr
{
    public Expr Left { get; private set; } = null!;
    public Token Operator { get; private set; }
    public Expr Right { get; private set; } = null!;

    protected void Initialize(Expr left, Token op, Expr right, int line, int col)
    {
        Left = left;
        Operator = op;
        Right = right;
        Line = line;
        Column = col;
    }
}
internal sealed class BinaryExpr : BinaryLikeExpr
{


    public static BinaryExpr Create(Expr left, Token op, Expr right, int line, int col)
    {
        var rent = new BinaryExpr();
        rent.Initialize(left, op, right, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitBinaryExpr(this);
}

internal sealed class UnaryExpr : Expr
{
    public Token Operator { get; private set; }
    public Expr Right { get; private set; } = null!;

    private void Initialize(Token op, Expr right, int line, int col)
    {
        Operator = op;
        Right = right;
        Line = line;
        Column = col;
    }

    public static UnaryExpr Create(Token op, Expr right, int line, int col)
    {
        var rent = new UnaryExpr();
        rent.Initialize(op, right, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitUnaryExpr(this);
}

internal sealed class DictLiteralExpr : Expr
{
    public IReadOnlyList<(Expr Key, Expr Value)> Entries { get; private set; } = null!;

    private void Initialize(IReadOnlyList<(Expr Key, Expr Value)> entries, int line, int col)
    {
        Entries = entries;
        Line = line;
        Column = col;
    }

    public static DictLiteralExpr Create(IReadOnlyList<(Expr Key, Expr Value)> entries, int line, int col)
    {
        var rent = new DictLiteralExpr();
        rent.Initialize(entries, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitDictLiteralExpr(this);
}

internal sealed class CallExpr : Expr
{
    public Expr Callee { get; private set; } = null!;
    public IReadOnlyList<Expr> Arguments { get; private set; } = null!;
    public IReadOnlyList<bool> IsRefArgs { get; private set; } = null!;
    public IReadOnlyList<bool> IsOutArgs { get; private set; } = null!;
    public IReadOnlyList<TypeInfo>? TypeArgs { get; private set; }
    /// <summary>Per-argument names for named-argument calls (null entry = positional); null when none used.</summary>
    public IReadOnlyList<string?>? ArgNames { get; private set; }

    /// <summary>True when the CALL itself is wanted as a REFERENCE rather than as a value (see Initialize below).</summary>
    public bool WantsRefResult { get; set; }

    private void Initialize(Expr callee, IReadOnlyList<Expr> arguments, IReadOnlyList<bool> isRefArgs, int line,
        int col, IReadOnlyList<TypeInfo>? typeArgs = null, IReadOnlyList<bool>? isOutArgs = null,
        IReadOnlyList<string?>? argNames = null)
    {
        Callee = callee;
        Arguments = arguments;
        IsRefArgs = isRefArgs;
        IsOutArgs = isOutArgs!;
        TypeArgs = typeArgs;

        // A CALL can be asked for as a REFERENCE rather than as a value: `ref int r = ref F(x);` and `F(x) = 7;`
        // (C# ref returns). Only a ref-returning declaration can answer one, and every other use of a call derefs.
        // (The flag is set by the parser on those two shapes, see WantsRefResult below.)

        // Normalize all-null argNames → null (a positional-only call must not look named).
        if (argNames is { Count: > 0 })
        {
            var hasNamed = false;
            foreach (var n in argNames) if (n != null) { hasNamed = true; break; }
            ArgNames = hasNamed ? argNames : null;
        }
        else
            ArgNames = null;
        Line = line;
        Column = col;
    }

    public static CallExpr Create(Expr callee, IReadOnlyList<Expr> arguments, IReadOnlyList<bool> isRefArgs, int line,
        int col, IReadOnlyList<TypeInfo>? typeArgs = null, IReadOnlyList<bool>? isOutArgs = null,
        IReadOnlyList<string?>? argNames = null)
    {
        var rent = new CallExpr();
        rent.Initialize(callee, arguments, isRefArgs, line, col, typeArgs, isOutArgs, argNames);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitCallExpr(this);
}

internal sealed class PropertyAccessExpr : Expr
{
    public Expr Target { get; private set; } = null!;
    public string PropertyName { get; private set; } = null!;

    private void Initialize(Expr target, string propertyName, int line, int col)
    {
        Target = target;
        PropertyName = propertyName;
        Line = line;
        Column = col;
    }

    public static PropertyAccessExpr Create(Expr target, string propertyName, int line, int col)
    {
        var rent = new PropertyAccessExpr();
        rent.Initialize(target, propertyName, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitPropertyAccessExpr(this);
}

internal sealed class PropertyAssignExpr : Expr
{
    public Expr Target { get; private set; } = null!;
    public string PropertyName { get; private set; } = null!;
    public Expr Value { get; private set; } = null!;

    private void Initialize(Expr target, string propertyName, Expr value, int line, int col)
    {
        Target = target;
        PropertyName = propertyName;
        Value = value;
        Line = line;
        Column = col;
    }

    public static PropertyAssignExpr Create(Expr target, string propertyName, Expr value, int line, int col)
    {
        var rent = new PropertyAssignExpr();
        rent.Initialize(target, propertyName, value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitPropertyAssignExpr(this);
}

internal sealed class MethodCallExpr : Expr
{
    public Expr Receiver { get; private set; } = null!;
    public string MethodName { get; private set; } = null!;
    public IReadOnlyList<Expr> Arguments { get; private set; } = null!;
    public IReadOnlyList<bool> IsRefArgs { get; private set; } = null!;
    public IReadOnlyList<bool> IsOutArgs { get; private set; } = null!;
    public IReadOnlyList<TypeInfo>? TypeArgs { get; private set; }
    /// <summary>Per-argument names for named-argument calls (null entry = positional); null when none used.</summary>
    public IReadOnlyList<string?>? ArgNames { get; private set; }

    /// <summary>True when the CALL itself is wanted as a REFERENCE rather than as a value: `ref int r = ref b.Field();`
    /// and `b.Field() = 42;` on a ref-returning METHOD (C# ref returns). Mirrors CallExpr.WantsRefResult.</summary>
    public bool WantsRefResult { get; set; }

    private void Initialize(Expr receiver, string methodName, IReadOnlyList<Expr> arguments,
        IReadOnlyList<bool> isRefArgs, int line, int col, IReadOnlyList<TypeInfo>? typeArgs = null, IReadOnlyList<bool>? isOutArgs = null,
        IReadOnlyList<string?>? argNames = null)
    {
        Receiver = receiver;
        MethodName = methodName;
        Arguments = arguments;
        IsRefArgs = isRefArgs;
        IsOutArgs = isOutArgs!;
        TypeArgs = typeArgs;
        // Normalize all-null argNames → null (a positional-only call must not look named).
        if (argNames is { Count: > 0 })
        {
            var hasNamed = false;
            foreach (var n in argNames) if (n != null) { hasNamed = true; break; }
            ArgNames = hasNamed ? argNames : null;
        }
        else
            ArgNames = null;
        Line = line;
        Column = col;
    }

    public static MethodCallExpr Create(Expr receiver, string methodName, IReadOnlyList<Expr> arguments,
        IReadOnlyList<bool> isRefArgs, int line, int col, IReadOnlyList<TypeInfo>? typeArgs = null, IReadOnlyList<bool>? isOutArgs = null,
        IReadOnlyList<string?>? argNames = null)
    {
        var rent = new MethodCallExpr();
        rent.Initialize(receiver, methodName, arguments, isRefArgs, line, col, typeArgs, isOutArgs, argNames);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitMethodCallExpr(this);
}

internal sealed class TupleExpr : Expr
{
    public IReadOnlyList<Expr> Items { get; private set; } = null!;

    private void Initialize(IReadOnlyList<Expr> items, int line, int col)
    {
        Items = items;
        Line = line;
        Column = col;
    }

    public static TupleExpr Create(IReadOnlyList<Expr> items, int line, int col)
    {
        var rent = new TupleExpr();
        rent.Initialize(items, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitTupleExpr(this);
}

internal sealed class ConditionalExpr : Expr
{
    public Expr Condition { get; private set; } = null!;
    public Expr ThenExpr { get; private set; } = null!;
    public Expr ElseExpr { get; private set; } = null!;

    private void Initialize(Expr condition, Expr thenExpr, Expr elseExpr, int line, int col)
    {
        Condition = condition;
        ThenExpr = thenExpr;
        ElseExpr = elseExpr;
        Line = line;
        Column = col;
    }

    public static ConditionalExpr Create(Expr condition, Expr thenExpr, Expr elseExpr, int line, int col)
    {
        var rent = new ConditionalExpr();
        rent.Initialize(condition, thenExpr, elseExpr, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitConditionalExpr(this);
}

internal sealed class ComprehensionExpr : Expr
{
    public Expr ElementExpr { get; private set; } = null!;
    public string IterVar { get; private set; } = null!;
    public Expr Collection { get; private set; } = null!;
    public Expr? Filter { get; private set; }

    private void Initialize(Expr elementExpr, string iterVar, Expr collection, Expr? filter, int line, int col)
    {
        ElementExpr = elementExpr;
        IterVar = iterVar;
        Collection = collection;
        Filter = filter;
        Line = line;
        Column = col;
    }

    public static ComprehensionExpr Create(Expr elementExpr, string iterVar, Expr collection, Expr? filter, int line, int col)
    {
        var rent = new ComprehensionExpr();
        rent.Initialize(elementExpr, iterVar, collection, filter, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitComprehensionExpr(this);
}

/// <summary>What MatchExpr and SwitchExpr both carry, stated ONCE: both match a subject against arms,
/// with the same two properties and an identical Initialize. tools/check-dup-bodies.sh cannot see the
/// pair because a same-file, same-name group reads as an overload set. The setters stay private to this
/// class, so Initialize is the only way in and is protected for each derived Create() to call.</summary>
internal abstract class MatchLikeExpr : Expr
{
    public Expr Subject { get; private set; } = null!;
    public IReadOnlyList<SwitchArm> Arms { get; private set; } = null!;

    protected void Initialize(Expr subject, IReadOnlyList<SwitchArm> arms, int line, int col)
    {
        Subject = subject;
        Arms = arms;
        Line = line;
        Column = col;
    }
}

internal sealed class MatchExpr : MatchLikeExpr
{

    public static MatchExpr Create(Expr subject, IReadOnlyList<SwitchArm> arms, int line, int col)
    {
        var rent = new MatchExpr();
        rent.Initialize(subject, arms, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitMatchExpr(this);
}

/// <summary>One element of a shared positional pattern: literal, discard `_`, bare type, or
/// typed variable. Typed elements are ENFORCED type checks (not informational).</summary>
internal enum TupleElementKind { Literal, Discard, TypeOnly, TypedVar }

internal sealed class TupleElementPattern
{
    public TupleElementKind Kind { get; init; }
    public object? LiteralValue { get; init; }        // Kind == Literal
    public TypeInfo? Type { get; init; }              // Kind == TypeOnly / TypedVar
    public string? Name { get; init; }                // Kind == TypedVar
}

/// <summary>Shared positional pattern, used by `match`/`case` arms and `expr is (…)`.
/// The subject must be a tuple/array/list of matching arity; typed elements are enforced.
/// TypedVar names bind in arm scope (case) or the if/while true branch (is).</summary>
internal sealed class TuplePatternExpr : Expr
{
    public IReadOnlyList<TupleElementPattern> Elements { get; private set; } = null!;
    private void Initialize(IReadOnlyList<TupleElementPattern> elements, int line, int col)
    {
        Elements = elements; Line = line; Column = col;
    }
    public static TuplePatternExpr Create(IReadOnlyList<TupleElementPattern> elements, int line, int col)
    {
        var rent = new TuplePatternExpr();
        rent.Initialize(elements, line, col);
        return rent;
    }
    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitTuplePatternExpr(this);
}

/// <summary>`expr is (p1, p2, …)`, evaluates to a bool. TypedVar names may only be bound when
/// this expression is the direct condition of an if/while (enforced at evaluation time).</summary>
internal sealed class IsTuplePatternExpr : Expr
{
    public TuplePatternExpr Pattern { get; private set; } = null!;
    public Expr Subject { get; private set; } = null!;
    private void Initialize(TuplePatternExpr pattern, Expr subject, int line, int col)
    {
        Pattern = pattern; Subject = subject; Line = line; Column = col;
    }
    public static IsTuplePatternExpr Create(TuplePatternExpr pattern, Expr subject, int line, int col)
    {
        var rent = new IsTuplePatternExpr();
        rent.Initialize(pattern, subject, line, col);
        return rent;
    }
    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitIsTuplePatternExpr(this);
}

// ══════════════════════════════════════════════════════════════════════════
// Pattern hierarchy, switch pattern matching
// ══════════════════════════════════════════════════════════════════════════

internal abstract record Pattern
{
    public int Line { get; init; }
    public int Column { get; init; }

    /// <summary>The pattern's SOURCE TEXT, captured at parse time when the parser was given the file's text (see
    /// Parser.AttachPatternSourceText). A bundle cannot carry a `Pattern` object, because that is an AST fragment
    /// holding arbitrary EXPRESSIONS and the wire has no form for one, so the compiler puts this text on
    /// PatternEngineDescriptor and the LOADER re-parses it with the engine's own parser: one implementation of what a
    /// pattern means. Null when the parse had no text, which is what makes the exporter refuse such a body by name
    /// instead of writing a descriptor it could never rebuild.</summary>
    public string? SourceText { get; init; }
}

internal sealed record ConstantPattern(Expr Expr) : Pattern;
internal sealed record TypePattern(TypeInfo Type, string? BindingName) : Pattern;
internal sealed record VarPattern(string BindingName) : Pattern;
internal sealed record DiscardPattern : Pattern;
internal sealed record RelationalPattern(TokenType Op, Expr Right) : Pattern;
/// <summary>Tuple pattern in a match arm, (Type1 name1, Type2 name2); matched positionally.</summary>
internal sealed record TupleArmPattern(IReadOnlyList<TupleElementPattern> Elements) : Pattern;
internal enum LogicalKind { And, Or, Not }
/// <summary>Logical composition: `p and q`, `p or q`, `not p` (Right is null for Not).</summary>
internal sealed record LogicalPattern(LogicalKind Kind, Pattern Left, Pattern? Right) : Pattern;
/// <summary>Parenthesized pattern, ( pattern ).</summary>
internal sealed record GroupPattern(Pattern Inner) : Pattern;
/// <summary>Property pattern, { Member: pattern, … } against instances, dictionaries or named tuples.</summary>
internal sealed record PropertyPattern(IReadOnlyList<(string Name, Pattern Pattern)> Members) : Pattern;
/// <summary>Slice slot of a list pattern, where the `..` sits among the fixed elements, plus an
/// optional `.. var name` capture of the remaining elements (as a new array).</summary>
internal sealed record SliceSpec(int Index, string? CaptureName, int Line, int Column);
/// <summary>List pattern, [ a, b ], [ a, .. ], [ .., z ], [ a, .., z ], [ a, .. var rest ].</summary>
internal sealed record ListPattern(IReadOnlyList<Pattern> Elements, SliceSpec? Slice) : Pattern;

internal sealed record CaseClause(IReadOnlyList<Pattern> Patterns, Expr? Guard, IReadOnlyList<Stmt> Body);
internal sealed record SwitchArm(Pattern Pattern, Expr? Guard, Expr Value);

internal sealed class SwitchStmt : Stmt
{
    public Expr Subject { get; private set; } = null!;
    public IReadOnlyList<CaseClause> Cases { get; private set; } = null!;

    private void Initialize(Expr subject, IReadOnlyList<CaseClause> cases, int line, int col)
    {
        Subject = subject;
        Cases = cases;
        Line = line;
        Column = col;
    }

    public static SwitchStmt Create(Expr subject, IReadOnlyList<CaseClause> cases, int line, int col)
    {
        var rent = new SwitchStmt();
        rent.Initialize(subject, cases, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitSwitchStmt(this);
}

internal sealed class SwitchExpr : MatchLikeExpr
{

    public static SwitchExpr Create(Expr subject, IReadOnlyList<SwitchArm> arms, int line, int col)
    {
        var rent = new SwitchExpr();
        rent.Initialize(subject, arms, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitSwitchExpr(this);
}

internal sealed class UsingStmt : Stmt
{
    public string VarName { get; private set; } = null!;
    public Expr Initializer { get; private set; } = null!;
    public Stmt Body { get; private set; } = null!;
    /// <summary>`await using`, C#'s form for a resource that implements IAsyncDisposable rather than
    /// IDisposable: the disposal is AWAITED (through the same pump a top-level `await` uses).</summary>
    public bool IsAwait { get; private set; }

    private void Initialize(string varName, Expr initializer, Stmt body, int line, int col, bool isAwait)
    {
        VarName = varName;
        Initializer = initializer;
        Body = body;
        IsAwait = isAwait;
        Line = line;
        Column = col;
    }

    public static UsingStmt Create(string varName, Expr initializer, Stmt body, int line, int col, bool isAwait = false)
    {
        var rent = new UsingStmt();
        rent.Initialize(varName, initializer, body, line, col, isAwait);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitUsingStmt(this);
}

/// <summary>C#-style `using var x = expr;`, the resource is disposed when the ENCLOSING scope exits
/// (unlike `using (var x = expr) { }` which disposes at the end of its own block).</summary>
internal sealed class UsingDeclStmt : Stmt
{
    public string VarName { get; private set; } = null!;
    public Expr Initializer { get; private set; } = null!;
    /// <summary>`await using var x = ...;`, the declaration form of the awaited disposal.</summary>
    public bool IsAwait { get; private set; }

    private void Initialize(string varName, Expr initializer, int line, int col, bool isAwait)
    {
        VarName = varName;
        Initializer = initializer;
        IsAwait = isAwait;
        Line = line;
        Column = col;
    }

    public static UsingDeclStmt Create(string varName, Expr initializer, int line, int col, bool isAwait = false)
    {
        var rent = new UsingDeclStmt();
        rent.Initialize(varName, initializer, line, col, isAwait);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitUsingDeclStmt(this);
}

internal sealed class IndexExpr : Expr
{
    public Expr Value { get; private set; } = null!;
    public bool FromEnd { get; private set; }

    private void Initialize(Expr value, bool fromEnd, int line, int col)
    {
        Value = value;
        FromEnd = fromEnd;
        Line = line;
        Column = col;
    }

    public static IndexExpr Create(Expr value, bool fromEnd, int line, int col)
    {
        var rent = new IndexExpr();
        rent.Initialize(value, fromEnd, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitIndexExpr(this);
}

internal sealed class RangeExpr : Expr
{
    public Expr? From { get; private set; }
    public Expr? To { get; private set; }

    private void Initialize(Expr? from, Expr? to, int line, int col)
    {
        From = from;
        To = to;
        Line = line;
        Column = col;
    }

    public static RangeExpr Create(Expr? from, Expr? to, int line, int col)
    {
        var rent = new RangeExpr();
        rent.Initialize(from, to, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitRangeExpr(this);
}

internal sealed class NullCoalescingExpr : Expr
{
    public Expr Left { get; private set; } = null!;
    public Expr Right { get; private set; } = null!;

    private void Initialize(Expr left, Expr right, int line, int col)
    {
        Left = left;
        Right = right;
        Line = line;
        Column = col;
    }

    public static NullCoalescingExpr Create(Expr left, Expr right, int line, int col)
    {
        var rent = new NullCoalescingExpr();
        rent.Initialize(left, right, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitNullCoalescingExpr(this);
}

internal sealed class NewArrayExpr : Expr
{
    public TypeInfo ElementType { get; private set; } = null!;
    public IReadOnlyList<Expr>? Sizes { get; private set; }
    public IReadOnlyList<Expr>? Initializer { get; private set; }

    private void Initialize(TypeInfo elementType, IReadOnlyList<Expr>? sizes, IReadOnlyList<Expr>? initializer,
        int line, int col)
    {
        ElementType = elementType;
        Sizes = sizes;
        Initializer = initializer;
        Line = line;
        Column = col;
    }

    public static NewArrayExpr Create(TypeInfo elementType, IReadOnlyList<Expr>? sizes,
        IReadOnlyList<Expr>? initializer, int line, int col)
    {
        var rent = new NewArrayExpr();
        rent.Initialize(elementType, sizes, initializer, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitNewArrayExpr(this);
}

internal sealed class ArrayAccessExpr : Expr
{
    public Expr Array { get; private set; } = null!;
    public IReadOnlyList<Expr> Indices { get; private set; } = null!;

    private void Initialize(Expr array, IReadOnlyList<Expr> indices, int line, int col)
    {
        Array = array;
        Indices = indices;
        Line = line;
        Column = col;
    }

    public static ArrayAccessExpr Create(Expr array, IReadOnlyList<Expr> indices, int line, int col)
    {
        var rent = new ArrayAccessExpr();
        rent.Initialize(array, indices, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitArrayAccessExpr(this);
}

internal sealed class ArrayAssignExpr : Expr
{
    public Expr Array { get; private set; } = null!;
    public IReadOnlyList<Expr> Indices { get; private set; } = null!;
    public Expr Value { get; private set; } = null!;

    private void Initialize(Expr array, IReadOnlyList<Expr> indices, Expr value, int line, int col)
    {
        Array = array;
        Indices = indices;
        Value = value;
        Line = line;
        Column = col;
    }

    public static ArrayAssignExpr Create(Expr array, IReadOnlyList<Expr> indices, Expr value, int line, int col)
    {
        var rent = new ArrayAssignExpr();
        rent.Initialize(array, indices, value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitArrayAssignExpr(this);
}

/// <summary>`F(x) = 7;` on a ref-RETURNING call (C# ref returns): the call answers a REFERENCE and this writes through
/// it. Its own node rather than a shape of AssignExpr, because the target is an EXPRESSION the interpreter has to
/// evaluate for its reference rather than a name or a slot it can address.</summary>
internal sealed class RefAssignExpr : Expr
{
    public Expr CellSource { get; private set; } = null!;
    public Expr Value { get; private set; } = null!;

    public static RefAssignExpr Create(Expr cellSource, Expr value, int line, int col)
    {
        var rent = new RefAssignExpr { CellSource = cellSource, Value = value, Line = line, Column = col };
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitRefAssignExpr(this);
}

internal sealed class LogicalExpr : BinaryLikeExpr
{


    public static LogicalExpr Create(Expr left, Token op, Expr right, int line, int col)
    {
        var rent = new LogicalExpr();
        rent.Initialize(left, op, right, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitLogicalExpr(this);
}

internal sealed class CompoundAssignExpr : Expr
{
    public Expr Target { get; private set; } = null!;
    public Token Operator { get; private set; }
    public Expr Value { get; private set; } = null!;

    private void Initialize(Expr target, Token op, Expr value, int line, int col)
    {
        Target = target;
        Operator = op;
        Value = value;
        Line = line;
        Column = col;
    }

    public static CompoundAssignExpr Create(Expr target, Token op, Expr value, int line, int col)
    {
        var rent = new CompoundAssignExpr();
        rent.Initialize(target, op, value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitCompoundAssignExpr(this);
}

internal sealed class IncrementDecrementExpr : Expr
{
    public Expr Target { get; private set; } = null!;
    public Token Operator { get; private set; }
    public bool IsPrefix { get; private set; }

    private void Initialize(Expr target, Token op, bool isPrefix, int line, int col)
    {
        Target = target;
        Operator = op;
        IsPrefix = isPrefix;
        Line = line;
        Column = col;
    }

    public static IncrementDecrementExpr Create(Expr target, Token op, bool isPrefix, int line, int col)
    {
        var rent = new IncrementDecrementExpr();
        rent.Initialize(target, op, isPrefix, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitIncrementDecrementExpr(this);
}

internal sealed class NewInstanceExpr : Expr
{
    public Expr ConstructorExpr { get; private set; } = null!;
    public IReadOnlyList<Expr>? Arguments { get; private set; }
    public TypeInfo? TypeInfo { get; private set; }
    public IReadOnlyList<bool>? IsRefArgs { get; private set; }
    public IReadOnlyList<bool>? IsOutArgs { get; private set; }
    /// <summary>Per-argument names for named-argument constructor calls (null entry = positional); null when none used.</summary>
    public IReadOnlyList<string?>? ArgNames { get; private set; }

    private void Initialize(Expr constructorExpr, IReadOnlyList<Expr>? arguments, TypeInfo? typeInfo, int line, int col,
        IReadOnlyList<bool>? isRefArgs = null, IReadOnlyList<bool>? isOutArgs = null, IReadOnlyList<string?>? argNames = null)
    {
        ConstructorExpr = constructorExpr;
        Arguments = arguments;
        TypeInfo = typeInfo;
        IsRefArgs = isRefArgs;
        IsOutArgs = isOutArgs;

        // Normalize: an all-null argNames list (every call builds one, filling null per arg)
        // means "no named arguments", keep the field null so VM compilers don't mis-fall-back.
        if (argNames is { Count: > 0 })
        {
            var hasNamed = false;
            foreach (var n in argNames) if (n != null) { hasNamed = true; break; }
            ArgNames = hasNamed ? argNames : null;
        }
        else
            ArgNames = null;
        Line = line;
        Column = col;
    }

    public static NewInstanceExpr Create(Expr constructorExpr, IReadOnlyList<Expr>? arguments, TypeInfo? typeInfo,
        int line, int col, IReadOnlyList<bool>? isRefArgs = null, IReadOnlyList<bool>? isOutArgs = null, IReadOnlyList<string?>? argNames = null)
    {
        var rent = new NewInstanceExpr();
        rent.Initialize(constructorExpr, arguments, typeInfo, line, col, isRefArgs, isOutArgs, argNames);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitNewInstanceExpr(this);
}

internal sealed class WithExpr : Expr
{
    public Expr Base { get; private set; } = null!;
    public IReadOnlyList<(string Name, Expr Value)> Fields { get; private set; } = null!;

    private void Initialize(Expr @base, IReadOnlyList<(string Name, Expr Value)> fields, int line, int col)
    {
        Base = @base;
        Fields = fields;
        Line = line;
        Column = col;
    }

    public static WithExpr Create(Expr @base, IReadOnlyList<(string Name, Expr Value)> fields, int line, int col)
    {
        var rent = new WithExpr();
        rent.Initialize(@base, fields, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitWithExpr(this);
}

internal sealed class ObjectInitExpr : Expr
{
    public Expr Inner { get; private set; } = null!;
    public IReadOnlyList<(string Name, Expr Value)> Initializers { get; private set; } = null!;

    private void Initialize(Expr inner, IReadOnlyList<(string Name, Expr Value)> initializers, int line, int col)
    {
        Inner = inner;
        Initializers = initializers;
        Line = line;
        Column = col;
    }

    public static ObjectInitExpr Create(Expr inner, IReadOnlyList<(string Name, Expr Value)> initializers, int line, int col)
    {
        var rent = new ObjectInitExpr();
        rent.Initialize(inner, initializers, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitObjectInitExpr(this);
}

internal sealed class ThisExpr : Expr
{
    public static ThisExpr Create(int line, int col)
    {
        var rent = new ThisExpr();
        rent.Initialize(line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitThisExpr(this);
}

internal sealed class SuperExpr : Expr
{
    public static SuperExpr Create(int line, int col)
    {
        var rent = new SuperExpr();
        rent.Initialize(line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitSuperExpr(this);
}

internal sealed class BaseExpr : Expr
{
    public static BaseExpr Create(int line, int col)
    {
        var rent = new BaseExpr();
        rent.Initialize(line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitBaseExpr(this);
}

internal sealed class LambdaExpr : Expr
{
    public IReadOnlyList<(TypeInfo Type, string Name)> Parameters { get; private set; } = null!;
    public IReadOnlyList<bool> IsRefParams { get; private set; } = null!;
    /// <summary>Which parameters are a `params` tail (at most the last one). Carried like IsRefParams: a
    /// lambda's invocable FuncDecl is built from these lists, and a dropped flag turns its tail into a FIXED
    /// parameter (extra arguments then refuse, measured before this list existed).</summary>
    public IReadOnlyList<bool> IsParams { get; private set; } = null!;
    public IReadOnlyList<bool> IsOutParams { get; private set; } = null!;
    /// <summary>Which parameters are declared `in` (read-only, passed by value), aligned with Params. SET by the parser
    /// after construction rather than threaded through Create, the way TupleElementNames travels: an `in` parameter
    /// changes one thing only, and that is how the BIND treats it (a cell whose setter refuses).
    /// </summary>
    public IReadOnlyList<bool>? IsInParams { get; set; } = null!;
    public Expr? BodyExpr { get; private set; }
    public IReadOnlyList<Stmt>? BodyStmts { get; private set; }
    public bool IsCoroutine { get; set; }
    /// <summary>Declared with the contextual `async` modifier (`async fun(...) { ... }`). Its body may `await`, it
    /// runs as a coroutine, and a CALL through the lambda value returns the script-visible Task.</summary>
    public bool IsAsync { get; set; }
    /// <summary>The interface an EXPLICIT implementation is qualified with (`IShape.area`), or null for an
    /// ordinary member. Dispatch is by name, so the contract must be named at the CALL SITE: an explicit
    /// member is therefore deliberately absent from the class name table.</summary>
    public string? Qualifier { get; set; }
    public bool HasExternalCaptures { get; set; } = true;  // safe default
    public bool CapturesAnalyzed { get; set; } = false;
    public List<string>? CapturedNames { get; set; }

    public IReadOnlyList<string?>? TupleElementNames { get; set; }

    /// <summary>Per-parameter tuple element names, aligned with Parameters: index i holds the
    /// element names when Parameters[i] is a tuple parameter ((int n, int m)), null otherwise.
    /// Copied onto the constructed ScriptFunction's FuncDecl at lambda creation.</summary>
    public IReadOnlyList<IReadOnlyList<string>?>? TupleParamNames { get; set; }

    /// <summary>A parameter's DEFAULT value, aligned with Parameters (`fun (int a, int b = 2) { }`), or null when
    /// none was written. Copied onto the constructed FuncDecl by BOTH engines, whose arity rules read the
    /// DECLARATION: without it a lambda's default was simply dropped, so a two-parameter lambda called with one
    /// argument was refused with "Expected between 2 and 2 arguments, got 1" while the SAME signature written as a
    /// named function answered (measured: the maintainer's tools/mcp_examples.slc demo_all).</summary>
    public IReadOnlyList<Expr?>? DefaultValues { get; set; }

    private void Initialize(
        IReadOnlyList<(TypeInfo Type, string Name)> parameters,
        IReadOnlyList<bool> isRefParams,
        IReadOnlyList<bool> isParams,
        IReadOnlyList<bool> isOutParams,
        Expr? bodyExpr,
        IReadOnlyList<Stmt>? bodyStmts,
        int line,
        int col
    )
    {
        Parameters = parameters;
        IsRefParams = isRefParams;
        IsParams = isParams;
        IsOutParams = isOutParams;
        BodyExpr = bodyExpr;
        BodyStmts = bodyStmts;
        Line = line;
        Column = col;
    }

    public static LambdaExpr Create(
        IReadOnlyList<(TypeInfo Type, string Name)> parameters,
        IReadOnlyList<bool> isRefParams,
        IReadOnlyList<bool> isParams,
        IReadOnlyList<bool> isOutParams,
        Expr? bodyExpr,
        IReadOnlyList<Stmt>? bodyStmts,
        int line,
        int col
    )
    {
        var rent = new LambdaExpr();
        rent.Initialize(parameters, isRefParams, isParams, isOutParams, bodyExpr, bodyStmts, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitLambdaExpr(this);
}

internal sealed class NewStructExpr : Expr
{
    public string StructName { get; private set; } = null!;
    public IReadOnlyList<Expr>? FieldValues { get; private set; }

    private void Initialize(string structName, IReadOnlyList<Expr>? fieldValues, int line, int col)
    {
        StructName = structName;
        FieldValues = fieldValues;
        Line = line;
        Column = col;
    }

    public static NewStructExpr Create(string structName, IReadOnlyList<Expr>? fieldValues, int line, int col)
    {
        var rent = new NewStructExpr();
        rent.Initialize(structName, fieldValues, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitNewStructExpr(this);
}

/// <summary>What IsExpr and AsExpr both carry, stated ONCE: both are type tests (is / as),
/// with the same two properties and an identical Initialize. tools/check-dup-bodies.sh cannot see the
/// pair because a same-file, same-name group reads as an overload set. The setters stay private to this
/// class, so Initialize is the only way in and is protected for each derived Create() to call.</summary>
internal abstract class TypeTestExpr : Expr
{
    public Expr Left { get; private set; } = null!;
    public TypeInfo Type { get; private set; } = null!;

    protected void Initialize(Expr left, TypeInfo type, int line, int col)
    {
        Left = left;
        Type = type;
        Line = line;
        Column = col;
    }
}

internal sealed class IsExpr : TypeTestExpr
{

    public static IsExpr Create(Expr left, TypeInfo type, int line, int col)
    {
        var rent = new IsExpr();
        rent.Initialize(left, type, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitIsExpr(this);
}

internal sealed class AsExpr : TypeTestExpr
{

    public static AsExpr Create(Expr left, TypeInfo type, int line, int col)
    {
        var rent = new AsExpr();
        rent.Initialize(left, type, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitAsExpr(this);
}

internal sealed class IsPatternExpr : Expr
{
    public Expr Left { get; private set; } = null!;
    public TypeInfo Type { get; private set; } = null!;
    public string VarName { get; private set; } = null!;
    /// <summary>General pattern form of `is` (constants, relational, logical, property, list).
    /// When set, Type/VarName are unused.</summary>
    public Pattern? MatchPattern { get; private set; }

    /// <summary>Creates the general `expr is pattern` form.</summary>
    public static IsPatternExpr CreatePattern(Expr left, Pattern pattern, int line, int col)
    {
        var rent = new IsPatternExpr();
        rent.Initialize(left, TypeInfo.Void, "", line, col);
        rent.MatchPattern = pattern;
        return rent;
    }

    private void Initialize(Expr left, TypeInfo type, string varName, int line, int col)
    {
        Left = left;
        Type = type;
        VarName = varName;
        Line = line;
        Column = col;
    }

    public static IsPatternExpr Create(Expr left, TypeInfo type, string varName, int line, int col)
    {
        var rent = new IsPatternExpr();
        rent.Initialize(left, type, varName, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitIsPatternExpr(this);
}

internal sealed class CastExpr : Expr
{
    public TypeInfo Type { get; private set; } = null!;
    public Expr Expression { get; private set; } = null!;

    private void Initialize(TypeInfo type, Expr expression, int line, int col)
    {
        Type = type;
        Expression = expression;
        Line = line;
        Column = col;
    }

    public static CastExpr Create(TypeInfo type, Expr expression, int line, int col)
    {
        var rent = new CastExpr();
        rent.Initialize(type, expression, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitCastExpr(this);
}

internal sealed class IsNullExpr : Expr
{
    public Expr Left { get; private set; } = null!;

    private void Initialize(Expr left, int line, int col)
    {
        Left = left;
        Line = line;
        Column = col;
    }

    public static IsNullExpr Create(Expr left, int line, int col)
    {
        var rent = new IsNullExpr();
        rent.Initialize(left, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitIsNullExpr(this);
}

internal sealed class YieldExpr : Expr
{
    public Expr? Value { get; private set; }

    private void Initialize(Expr? value, int line, int col)
    {
        Value = value;
        Line = line;
        Column = col;
    }

    public static YieldExpr Create(Expr? value, int line, int col)
    {
        var rent = new YieldExpr();
        rent.Initialize(value, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitYieldExpr(this);
}

/// <summary>`await expr` (C# flavour): suspend the enclosing `async` body until the awaited Task completes,
/// then produce its result. The operator yields the awaitable to the coroutine pump and consumes the value the
/// pump resumes it with, which is the SAME suspension the script's `yield req` spelling uses, so this is one
/// more kind of awaitable projection, not a second suspension mechanism.</summary>
internal sealed class AwaitExpr : Expr
{
    public Expr Operand { get; private set; } = null!;

    private void Initialize(Expr operand, int line, int col)
    {
        Operand = operand;
        Line = line;
        Column = col;
    }

    public static AwaitExpr Create(Expr operand, int line, int col)
    {
        var rent = new AwaitExpr();
        rent.Initialize(operand, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitAwaitExpr(this);
}

internal sealed class ListLiteralExpr : Expr
{
    public IReadOnlyList<Expr> Elements { get; private set; } = null!;
    public TypeInfo ElementType { get; private set; } = null!;

    private void Initialize(IReadOnlyList<Expr> elements, TypeInfo elementType, int line, int col)
    {
        Elements = elements;
        ElementType = elementType;
        Line = line;
        Column = col;
    }

    public static ListLiteralExpr Create(IReadOnlyList<Expr> elements, TypeInfo elementType, int line, int col)
    {
        var rent = new ListLiteralExpr();
        rent.Initialize(elements, elementType, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitListLiteralExpr(this);
}

// ============================================================================
// 3.2 STATEMENTS NODES (Stmt)
// ============================================================================

internal sealed class DiscardExpr : Expr
{
    public static DiscardExpr Create(int line, int col)
    {
        var rent = new DiscardExpr();
        rent.Initialize(line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitDiscardExpr(this);
}

internal sealed class ReturnValueExpr : Expr
{
    public static ReturnValueExpr Create(int line, int col)
    {
        var rent = new ReturnValueExpr();
        rent.Initialize(line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitReturnValueExpr(this);
}

/// <summary>A DEFAULT PARAMETER VALUE that travelled as BYTECODE. A bundle's function shell carries each
/// default expression the compiler took as a zero-parameter body, and the loader puts this node in its place,
/// so the declaration's DefaultValues keeps the shape every binding site already reads (a NON-NULL entry is
/// an optional parameter, and the arity rules follow from that alone). Evaluating it runs that body through
/// the ordinary call path, which is what makes a loaded default the same evaluation the source performed.
/// A declaration compiled from source never carries one, because its own AST expression is right there.</summary>
internal sealed class CompiledDefaultExpr : Expr
{
    public FuncDecl Declaration { get; private set; } = null!;

    /// <summary>The THUNK wrapped as a callable: built on first evaluation by the interpreter, which owns the
    /// empty snapshot and the parameter types (building it per call would allocate a ScriptFunction for every
    /// defaulted argument of every call).</summary>
    internal ScriptFunction? Thunk { get; set; }

    /// <summary>The namespace leaf the OWNING declaration was bound into, stamped by the loader that bound it
    /// (a library's functions resolve a bare sibling call through this leaf). Measured without it: an imported
    /// library's `int libH(int a, int e = D4())` failed with "loaded from bytecode for a different environment"
    /// while `int libPlain(int a) { return D4() + a; }` worked, because the function body had a leaf and its
    /// default's body did not.</summary>
    internal Namespace? NamespaceContext { get; set; }

    public static CompiledDefaultExpr Create(FuncDecl declaration, int line, int col)
    {
        return new CompiledDefaultExpr
        {
            Declaration = declaration,
            Line = line,
            Column = col
        };
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitCompiledDefaultExpr(this);
}

internal sealed class ExprStmt : Stmt
{
    public Expr Expression { get; private set; } = null!;

    private void Initialize(Expr expression, int line, int col)
    {
        Expression = expression;
        Line = line;
        Column = col;
    }

    public static ExprStmt Create(Expr expression, int line, int col)
    {
        var rent = new ExprStmt();
        rent.Initialize(expression, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitExprStmt(this);
}

internal sealed class VarDeclStmt : Stmt
{
    public string Name { get; private set; } = null!;
    public Expr? Initializer { get; private set; }
    public TypeInfo? Type { get; private set; }
    public bool IsConst { get; private set; }
    public bool WasEscaped { get; private set; }
    public IReadOnlyList<string>? TupleElementNames { get; set; }

    private void Initialize(string name, Expr? initializer, TypeInfo? type, bool isConst, bool wasEscaped, int line,
        int col)
    {
        Name = name;
        Initializer = initializer;
        Type = type;
        IsConst = isConst;
        WasEscaped = wasEscaped;
        Line = line;
        Column = col;
    }

    public static VarDeclStmt Create(string name, Expr? initializer, TypeInfo? type, bool isConst, bool wasEscaped,
        int line, int col)
    {
        var rent = new VarDeclStmt();
        rent.Initialize(name, initializer, type, isConst, wasEscaped, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitVarDeclStmt(this);
}

internal sealed class ReturnStmt : Stmt
{
    public Expr? Value { get; private set; }

    private void Initialize(Expr? value, int line, int col)
    {
        Value = value;
        Line = line;
        Column = col;
    }

    public static ReturnStmt Create(Expr? value, int line, int col)
    {
        var rent = new ReturnStmt();
        rent.Initialize(value, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitReturnStmt(this);
}

internal sealed class BlockStmt : Stmt
{
    public IReadOnlyList<Stmt> Statements { get; private set; } = null!;

    private void Initialize(IReadOnlyList<Stmt> statements, int line, int col)
    {
        Statements = statements;
        Line = line;
        Column = col;
    }

    public static BlockStmt Create(IReadOnlyList<Stmt> statements, int line, int col)
    {
        var rent = new BlockStmt();
        rent.Initialize(statements, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitBlockStmt(this);
}

internal sealed class IfStmt : Stmt
{
    public Expr Condition { get; private set; } = null!;
    public IReadOnlyList<Stmt> ThenBranch { get; private set; } = null!;
    public IReadOnlyList<Stmt> ElseBranch { get; private set; } = null!;

    private void Initialize(Expr condition, IReadOnlyList<Stmt> thenBranch, IReadOnlyList<Stmt> elseBranch, int line,
        int col)
    {
        Condition = condition;
        ThenBranch = thenBranch;
        ElseBranch = elseBranch;
        Line = line;
        Column = col;
    }

    public static IfStmt Create(Expr condition, IReadOnlyList<Stmt> thenBranch, IReadOnlyList<Stmt> elseBranch,
        int line, int col)
    {
        var rent = new IfStmt();
        rent.Initialize(condition, thenBranch, elseBranch, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitIfStmt(this);
}

internal sealed class WhileStmt : Stmt
{
    public Expr Condition { get; private set; } = null!;
    public IReadOnlyList<Stmt> Body { get; private set; } = null!;

    private void Initialize(Expr condition, IReadOnlyList<Stmt> body, int line, int col)
    {
        Condition = condition;
        Body = body;
        Line = line;
        Column = col;
    }

    public static WhileStmt Create(Expr condition, IReadOnlyList<Stmt> body, int line, int col)
    {
        var rent = new WhileStmt();
        rent.Initialize(condition, body, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitWhileStmt(this);
}

internal sealed class ForeachStmt : Stmt
{
    public string VarName { get; private set; } = null!;
    public TypeInfo? VarType { get; private set; }
    public IReadOnlyList<string>? LoopVarNames { get; private set; }   // FP: foreach (var (a, b) in ...), null for the single-var form
    public Expr Collection { get; private set; } = null!;
    public IReadOnlyList<Stmt> Body { get; private set; } = null!;
    /// <summary>`await foreach` (C#'s ASYNC STREAM): the sequence arrives asynchronously, so each element costs one
    /// `MoveNextAsync` the engine must await. A plain `foreach` over such a sequence is REFUSED by name (its
    /// elements cannot arrive without awaiting), and the compiler DECLINES an awaited foreach so it runs on the
    /// tree, which is where the drive lives.</summary>
    public bool IsAwait { get; private set; }

    private void Initialize(string varName, TypeInfo? varType, Expr collection, IReadOnlyList<Stmt> body, int line,
        int col, IReadOnlyList<string>? loopVarNames = null, bool isAwait = false)
    {
        VarName = loopVarNames is { Count: > 0 } ? "" : varName;
        VarType = varType;
        LoopVarNames = loopVarNames;
        Collection = collection;
        Body = body;
        Line = line;
        Column = col;
        IsAwait = isAwait;
    }

    public static ForeachStmt Create(string varName, TypeInfo? varType, Expr collection, IReadOnlyList<Stmt> body,
        int line, int col, IReadOnlyList<string>? loopVarNames = null, bool isAwait = false)
    {
        var rent = new ForeachStmt();
        rent.Initialize(varName, varType, collection, body, line, col, loopVarNames, isAwait);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitForeachStmt(this);
}

internal sealed class FuncDecl : Stmt
{
    public List<string>? CapturedNames { get; set; }

    /// <summary>Set when a closure captured this declaration BEFORE it ran (C# hoisting): the body keeps the tree, because
    /// the capture layout a compiled body would need did not exist when the closure was made.</summary>
    public bool CapturedBeforeDeclaration { get; set; }

    /// <summary>Local-function VM capture layout: the captured local names registered after params
    /// + "this" in this decl's compiled bytecode (deterministic first-appearance order). Set once
    /// at lazy compile; VmCaptures materialization MUST follow the same order. Null for top-level
    /// functions and tree-only local functions.</summary>
    public IReadOnlyList<string>? VmCaptureNames { get; set; }

    /// <summary>The CLR type each parameter coerces to, cached on first use. The VM's flat call binds its
    /// arguments straight into the callee's locals, and it asks this so a parameter whose declared type the
    /// argument already has costs one `GetType()` comparison instead of a lookup plus a conversion
    /// (Interpreter.CoerceFlatCallArg).</summary>
    public Type?[]? ParamClrTypes { get; set; }

    /// <summary>The CLR type a VM call coerces this declaration's RETURN value to, or null when there is nothing to
    /// check (an untyped or permissive `object` return, which is what a lambda body is declared with). Cached on
    /// first use, so a return that already has the declared type costs one `IsInstanceOfType` comparison
    /// (Interpreter.CoerceFlatCallResult).</summary>
    public Type? ReturnClrType { get; set; }
    public string Name { get; private set; } = null!;
    public IReadOnlyList<(TypeInfo Type, string Name)> Params { get; private set; } = null!;
    public IReadOnlyList<bool> IsRefParams { get; private set; } = null!;
    public IReadOnlyList<bool> IsOutParams { get; private set; } = null!;
    /// <summary>Which parameters are declared `in` (read-only, passed by value), aligned with Params. SET by the parser
    /// after construction rather than threaded through Create, the way TupleElementNames travels: an `in` parameter
    /// changes one thing only, and that is how the BIND treats it (a cell whose setter refuses).
    /// </summary>
    public IReadOnlyList<bool>? IsInParams { get; set; } = null!;
    public IReadOnlyList<bool> IsParams { get; private set; } = null!;
    public IReadOnlyList<Stmt> Body { get; private set; } = null!;
    public TypeInfo ReturnType { get; private set; } = null!;
    public TokenType? OperatorToken { get; private set; }
    public AccessModifier Access { get; private set; }
    public bool IsPure { get; set; }
    public bool IsCoroutine { get; set; }
    /// <summary>Declared with the `async` modifier (C# flavour). An async function's body may `await`, runs as a
    /// coroutine (so it can suspend), and a CALL to it returns the script-visible <see cref="Task"/> instead of the
    /// generator an ordinary coroutine function hands back. Always implies IsCoroutine.</summary>
    public bool IsAsync { get; set; }
    /// <summary>The interface an EXPLICIT implementation is qualified with (`IShape.area`), or null for an
    /// ordinary member. Dispatch is by name, so the contract must be named at the CALL SITE: an explicit
    /// member is therefore deliberately absent from the class name table.</summary>
    public string? Qualifier { get; set; }
    /// <summary>True when the body contains a YieldExpr (set by the parser's ContainsYield pass,
    /// the same analysis that derives IsCoroutine). Until the VM handles YIELD (Task 2), a
    /// HasYield function must never take a VM fast path, see InvokeScriptFunction's gates.</summary>
    public bool HasYield { get; set; }
    public bool IsStatic { get; set; }
    public IReadOnlyList<Expr?>? DefaultValues { get; set; }
    /// <summary>The compiled form of a default value, aligned with DefaultValues and Params: the compiler
    /// compiles each default expression into a zero-parameter body here, because a bundle's shell has no AST to
    /// carry (see CompiledDefaultExpr). Null where there is no default, and null where the compiler did not take
    /// the expression, which is what the export refuses on. A LOADED declaration leaves this null and carries
    /// the loaded form in DefaultValues itself, so every binding site reads one shape.</summary>
    public Bytecode?[]? DefaultThunks { get; set; }
    public IReadOnlyList<string> TypeParameters { get; private set; } = [];

    /// <summary>The concrete type each TYPE PARAMETER was bound to by the instantiation that is RUNNING (a generic
    /// clone carries its own map), or null for an ordinary declaration. A type position that SURVIVES the body's
    /// substitution (`is`, a cast, `typeof`) resolves a parameter through this, because that substitution walk only
    /// rewrites three node shapes: measured, `o is IEnumerable&lt;T&gt;` answered False from a return, a print, an
    /// if, a `&amp;&amp;`, a var initializer, a lambda and a foreach body alike.</summary>
    public Dictionary<string, TypeInfo>? TypeParamBindings { get; set; }
    public Bytecode? Compiled { get; set; }
    /// <summary>True for reloaded bytecode shells, no AST body; VM-only. A VM fallback must error,
    /// never silently execute the empty body.</summary>
    public bool IsVmShell { get; set; }

    private void Initialize(
        string name,
        IReadOnlyList<(TypeInfo Type, string Name)> @params,
        IReadOnlyList<bool> isRefParams,
        IReadOnlyList<bool> isParams,
        IReadOnlyList<Stmt> body,
        TypeInfo returnType,
        TokenType? operatorToken,
        AccessModifier access,
        int line,
        int col,
        IReadOnlyList<string>? typeParameters = null,
        IReadOnlyList<bool>? isOutParams = null,
        IReadOnlyList<bool>? isInParams = null
    )
    {
        Name = name;
        Params = @params;
        IsRefParams = isRefParams;
        IsOutParams = isOutParams!;
        IsInParams = isInParams;
        IsParams = isParams;
        Body = body;
        ReturnType = returnType;
        OperatorToken = operatorToken;
        Access = access;
        TypeParameters = typeParameters ?? [];
        Line = line;
        Column = col;
    }

    /// <summary>True when the declaration is a REF RETURN (`ref int F(...)`): the body answers a reference (a cell) and
    /// a call to it is an lvalue, so `F(a) = 7;` writes through it. C#'s ref returns; the reference is a heap
    /// getter/setter pair here, so nothing can dangle (see the lifetime note in the `ref` docs).
    /// </summary>
    public bool IsRefReturn { get; set; }

    /// <summary>Element names when the function''s declared return type is a tuple,
    /// e.g. (int i, string s) → ["i", "s"]. Null otherwise.</summary>
    public IReadOnlyList<string>? TupleElementNames { get; set; }

    /// <summary>Per-parameter tuple element names, aligned with Params: index i holds the
    /// element names when Params[i] is a tuple parameter ((int n, int m)), null otherwise.
    /// The function's own TupleElementNames (return type) is separate.</summary>
    public IReadOnlyList<IReadOnlyList<string>?>? TupleParamNames { get; set; }

    public static FuncDecl Create(
        string name,
        IReadOnlyList<(TypeInfo Type, string Name)> @params,
        IReadOnlyList<bool> isRefParams,
        IReadOnlyList<bool> isParams,
        IReadOnlyList<Stmt> body,
        TypeInfo returnType,
        TokenType? operatorToken,
        AccessModifier access,
        int line,
        int col,
        IReadOnlyList<string>? typeParameters = null,
        IReadOnlyList<bool>? isOutParams = null,
        IReadOnlyList<bool>? isInParams = null
    )
    {
        var rent = new FuncDecl();
        rent.Initialize(name, @params, isRefParams, isParams, body, returnType, operatorToken, access, line, col, typeParameters, isOutParams, isInParams);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitFuncDecl(this);
}

internal sealed class EnumDeclStmt : Stmt
{
    public string Name { get; private set; } = null!;
    public IReadOnlyList<(string Name, Expr? Init)> Members { get; private set; } = null!;
    public AccessModifier Access { get; private set; }

    private void Initialize(string name, IReadOnlyList<(string Name, Expr? Init)> members, AccessModifier access, int line, int col)
    {
        Name = name;
        Members = members;
        Access = access;
        Line = line;
        Column = col;
    }

    public static EnumDeclStmt Create(string name, IReadOnlyList<(string Name, Expr? Init)> members, AccessModifier access, int line, int col)
    {
        var rent = new EnumDeclStmt();
        rent.Initialize(name, members, access, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitEnumDeclStmt(this);
}

internal sealed class DestructuringStmt : Stmt
{
    public IReadOnlyList<string> VarNames { get; private set; } = null!;
    public Expr Source { get; private set; } = null!;
    public TypeInfo? DeclType { get; private set; }
    public bool IsConst { get; private set; }

    private void Initialize(IReadOnlyList<string> varNames, Expr source, TypeInfo? declType, bool isConst, int line, int col)
    {
        VarNames = varNames;
        Source = source;
        DeclType = declType;
        IsConst = isConst;
        Line = line;
        Column = col;
    }

    public static DestructuringStmt Create(IReadOnlyList<string> varNames, Expr source, TypeInfo? declType, bool isConst, int line, int col)
    {
        var rent = new DestructuringStmt();
        rent.Initialize(varNames, source, declType, isConst, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitDestructuringStmt(this);
}

internal sealed class ForStmt : Stmt
{
    public Stmt? Init { get; private set; }
    public Expr Condition { get; private set; } = null!;
    public Expr? Increment { get; private set; }
    public IReadOnlyList<Stmt> Body { get; private set; } = null!;

    private void Initialize(Stmt? init, Expr condition, Expr? increment, IReadOnlyList<Stmt> body, int line, int col)
    {
        Init = init;
        Condition = condition;
        Increment = increment;
        Body = body;
        Line = line;
        Column = col;
    }

    public static ForStmt Create(Stmt? init, Expr condition, Expr? increment, IReadOnlyList<Stmt> body, int line,
        int col)
    {
        var rent = new ForStmt();
        rent.Initialize(init, condition, increment, body, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitForStmt(this);
}

internal sealed class BreakStmt : Stmt
{
    public static BreakStmt Create(int line, int col)
    {
        var rent = new BreakStmt();
        rent.Initialize(line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitBreakStmt(this);
}

internal sealed class DebuggerStmt : Stmt
{
    public static DebuggerStmt Create(int line, int col)
    {
        var rent = new DebuggerStmt();
        rent.Initialize(line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitDebuggerStmt(this);
}

internal sealed class ContinueStmt : Stmt
{
    public static ContinueStmt Create(int line, int col)
    {
        var rent = new ContinueStmt();
        rent.Initialize(line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitContinueStmt(this);
}

internal sealed class ThrowStmt : Stmt
{
    public Expr? Expression { get; private set; }

    private void Initialize(Expr? expression, int line, int col)
    {
        Expression = expression;
        Line = line;
        Column = col;
    }

    public static ThrowStmt Create(Expr? expression, int line, int col)
    {
        var rent = new ThrowStmt();
        rent.Initialize(expression, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitThrowStmt(this);
}

internal sealed class TryStmt : Stmt
{
    public Stmt Body { get; private set; } = null!;
    public IReadOnlyList<CatchClause> Catches { get; private set; } = null!;
    public Stmt? Finally { get; private set; }

    private void Initialize(Stmt body, IReadOnlyList<CatchClause> catches, Stmt? @finally, int line, int col)
    {
        Body = body;
        Catches = catches;
        Finally = @finally;
        Line = line;
        Column = col;
    }

    public static TryStmt Create(Stmt body, IReadOnlyList<CatchClause> catches, Stmt? @finally, int line, int col)
    {
        var rent = new TryStmt();
        rent.Initialize(body, catches, @finally, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitTryStmt(this);
}

internal sealed class ClassDeclStmt : Stmt
{
    public string Name { get; private set; } = null!;
    public string? ParentName { get; private set; }
    public IReadOnlyList<FieldDecl> Fields { get; private set; } = null!;
    public IReadOnlyList<FuncDecl> Methods { get; private set; } = null!;
    public IReadOnlyList<ConstructorDecl> Constructors { get; private set; } = null!;
    public IReadOnlyList<ClassDeclStmt> NestedClasses { get; private set; } = null!;
    public IReadOnlyList<EventDecl> Events { get; private set; } = null!;
    public IReadOnlyDictionary<TokenType, FuncDecl> OperatorMethods { get; private set; } = null!;
    public AccessModifier Access { get; private set; }
    public IReadOnlyList<string> TypeParameters { get; private set; } = [];
    public FuncDecl? Destructor { get; private set; }
    public IReadOnlyList<DelegateDecl> Delegates { get; private set; } = [];
    public IReadOnlyList<PropertyDecl> Properties { get; private set; } = [];
    public IReadOnlyList<StructDeclStmt> NestedStructs { get; private set; } = [];
    /// <summary>Extension blocks declared inside this class body.</summary>
    public IReadOnlyList<ExtensionDeclStmt> Extensions { get; private set; } = [];
    /// <summary>The INTERFACES this class declares it implements, as written after `implements`. They are
    /// validated when the declaration executes, and the flattened set (these plus every ancestor interface) is
    /// what `is`/`as`/a cast consult.</summary>
    public IReadOnlyList<string> Interfaces { get; private set; } = [];
    /// <summary>True for record types, structural value equality (field-by-field ==/!=).</summary>
    public bool IsRecord { get; set; }
    /// <summary>True when THIS class or any class nested in its body declares TYPE PARAMETERS, which is the shape
    /// a class descriptor cannot reproduce (see <see cref="SourceText"/>). The outer name is what matters, since
    /// registering a declaration recurses into its nested classes.</summary>
    public bool DeclaresGenericClass => TypeParameters.Count > 0 || NestedClasses.Any(n => n.DeclaresGenericClass);

    private void Initialize(
        string name,
        string? parentName,
        IReadOnlyList<FieldDecl> fields,
        IReadOnlyList<FuncDecl> methods,
        IReadOnlyList<ConstructorDecl> constructors,
        IReadOnlyList<ClassDeclStmt> nestedClasses,
        IReadOnlyList<EventDecl> events,
        IReadOnlyDictionary<TokenType, FuncDecl> operatorMethods,
        AccessModifier access,
        int line,
        int col,
        IReadOnlyList<string>? typeParameters = null,
        FuncDecl? destructor = null,
        IReadOnlyList<DelegateDecl>? delegates = null,
        IReadOnlyList<PropertyDecl>? properties = null,
        IReadOnlyList<StructDeclStmt>? nestedStructs = null,
        IReadOnlyList<ExtensionDeclStmt>? extensions = null,
        IReadOnlyList<string>? interfaces = null
    )
    {
        Name = name;
        ParentName = parentName;
        Fields = fields;
        Methods = methods;
        Constructors = constructors;
        NestedClasses = nestedClasses;
        NestedStructs = nestedStructs ?? [];
        Extensions = extensions ?? [];
        Interfaces = interfaces ?? [];
        Events = events;
        OperatorMethods = operatorMethods;
        Access = access;
        TypeParameters = typeParameters ?? [];
        Destructor = destructor;
        Delegates = delegates ?? [];
        Properties = properties ?? [];
        TypeParameters = typeParameters ?? [];
        Line = line;
        Column = col;
    }

    public static ClassDeclStmt Create(
        string name,
        string? parentName,
        IReadOnlyList<FieldDecl> fields,
        IReadOnlyList<FuncDecl> methods,
        IReadOnlyList<ConstructorDecl> constructors,
        IReadOnlyList<ClassDeclStmt> nestedClasses,
        IReadOnlyList<EventDecl> events,
        IReadOnlyDictionary<TokenType, FuncDecl> operatorMethods,
        AccessModifier access,
        int line,
        int col,
        IReadOnlyList<string>? typeParameters = null,
        FuncDecl? destructor = null,
        IReadOnlyList<DelegateDecl>? delegates = null,
        IReadOnlyList<PropertyDecl>? properties = null,
        IReadOnlyList<StructDeclStmt>? nestedStructs = null,
        IReadOnlyList<ExtensionDeclStmt>? extensions = null,
        IReadOnlyList<string>? interfaces = null
    )
    {
        var rent = new ClassDeclStmt();
        rent.Initialize(name, parentName, fields, methods, constructors, nestedClasses, events, operatorMethods, access,
            line, col, typeParameters, destructor, delegates, properties, nestedStructs, extensions, interfaces);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitClassDeclStmt(this);
}

// ============================================================================
// Extension members (C# 14 style)
// ============================================================================

/// <summary>A generic type reference used as an expression - the `List&lt;int&gt;` of
/// `List&lt;int&gt;.Create()`. It carries a NAME plus its type arguments rather than a CLR Type, because
/// this language models types as TypeInfo and never by reflection. It means something only as the
/// receiver of a static member call.</summary>
internal sealed class TypeRefExpr : Expr
{
    public string Name { get; private set; } = null!;
    public IReadOnlyList<TypeInfo> TypeArgs { get; private set; } = [];

    /// <summary>The reference as the engine's own TypeInfo (name + arguments). That is the shape everything
    /// downstream already speaks: the bytecode const pool carries one (IS_TYPE pushes exactly this), and the
    /// extension lookup matches on it, so the compiled and interpreted paths hand over the same value.</summary>
    public TypeInfo Type { get; private set; } = null!;

    private void Initialize(string name, IReadOnlyList<TypeInfo> typeArgs, int line, int col)
    {
        Name = name;
        TypeArgs = typeArgs;
        Type = new TypeInfo(TokenType.Object, false, 0, 0, name, new TypeArgList(typeArgs));
        Line = line;
        Column = col;
    }

    public static TypeRefExpr Create(string name, IReadOnlyList<TypeInfo> typeArgs, int line, int col)
    {
        var rent = new TypeRefExpr();
        rent.Initialize(name, typeArgs, line, col);
        return rent;
    }

    public override T Accept<T>(IExprVisitor<T> visitor) => visitor.VisitTypeRefExpr(this);
}

/// <summary>An `extension [&lt;T&gt;] (Target) { members }` block: extension members declared in a script, at
/// top level or inside a static class. A member carrying `static` is called on the EXTENDED TYPE
/// (`List&lt;int&gt;.Create()`); one without it is called on a VALUE of the extended type.</summary>
internal sealed class ExtensionDeclStmt : Stmt
{
    public IReadOnlyList<string> TypeParameters { get; private set; } = [];
    public TypeInfo Target { get; private set; } = null!;
    public IReadOnlyList<FuncDecl> Members { get; private set; } = [];
    /// <summary>The static class the block was written in, or null when written at top level.</summary>
    public string? ContainerName { get; private set; }
    /// <summary>The name the block gave its receiver (`s` in `extension(string s)`), or null when it left the
    /// receiver unnamed (`extension(string)`). C# 14 names the receiver; here the name is a second spelling of
    /// `this` inside the block's members, so the parser records it and the invocation binds it.</summary>
    public string? ReceiverName { get; private set; }
    /// <summary>The block's COMPUTED properties. An extension block adds no storage, so an extension property has
    /// no backing field: its accessors are the whole implementation.</summary>
    public IReadOnlyList<ExtensionPropertyDecl> Properties { get; private set; } = [];
    /// <summary>The block's OPERATORS. They are kept apart from the members so that no NAME reaches them: an
    /// operator is found by the operator token and the operand types, never by a call.</summary>
    public IReadOnlyList<FuncDecl> Operators { get; private set; } = [];

    /// <summary>The extended type's name without its type arguments (`List` for `List&lt;T&gt;`).</summary>
    public string TargetName => Target.TypeName ?? TypeInfo.SimpleTypeName(Target.BaseType);

    private void Initialize(IReadOnlyList<string> typeParameters, TypeInfo target, IReadOnlyList<FuncDecl> members,
        string? containerName, string? receiverName, IReadOnlyList<ExtensionPropertyDecl> properties,
        int line, int col, IReadOnlyList<FuncDecl>? operators = null)
    {
        TypeParameters = typeParameters;
        Target = target;
        Members = members;
        ContainerName = containerName;
        ReceiverName = receiverName;
        Properties = properties;
        Operators = operators ?? [];
        Line = line;
        Column = col;
    }

    public static ExtensionDeclStmt Create(IReadOnlyList<string> typeParameters, TypeInfo target,
        IReadOnlyList<FuncDecl> members, string? containerName, int line, int col, string? receiverName = null,
        IReadOnlyList<ExtensionPropertyDecl>? properties = null, IReadOnlyList<FuncDecl>? operators = null)
    {
        var rent = new ExtensionDeclStmt();
        rent.Initialize(typeParameters, target, members, containerName, receiverName, properties ?? [], line, col,
            operators);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitExtensionDeclStmt(this);
}

/// <summary>A COMPUTED property declared by an `extension` block: `public int Len => this.Length;` or
/// `public int Len { get => …; set { … } }`. An extension block adds no storage, so there is no backing field and
/// no auto-accessor (`get;` is refused by the parser, with the reason), the accessors ARE the implementation.
///
/// Each accessor is an ordinary FuncDecl, so the invocation path, the bytecode compiler and the failure messages
/// are the member path's rather than a second one: the getter takes no parameters and returns the property's
/// type, and the setter takes one parameter named `value` of that type.</summary>
internal sealed class ExtensionPropertyDecl
{
    public string Name { get; private set; } = null!;
    public TypeInfo Type { get; private set; } = null!;
    public AccessModifier Access { get; private set; }
    public bool IsStatic { get; private set; }
    public FuncDecl? Getter { get; private set; }
    public FuncDecl? Setter { get; private set; }
    public int Line { get; private set; }
    public int Column { get; private set; }

    private void Initialize(string name, TypeInfo type, AccessModifier access, bool isStatic,
        FuncDecl? getter, FuncDecl? setter, int line, int col)
    {
        Name = name;
        Type = type;
        Access = access;
        IsStatic = isStatic;
        Getter = getter;
        Setter = setter;
        Line = line;
        Column = col;
    }

    public static ExtensionPropertyDecl Create(string name, TypeInfo type, AccessModifier access, bool isStatic,
        FuncDecl? getter, FuncDecl? setter, int line, int col)
    {
        var rent = new ExtensionPropertyDecl();
        rent.Initialize(name, type, access, isStatic, getter, setter, line, col);
        return rent;
    }
}


/// <summary>An `interface` declaration: a NAMED contract of method signatures, plus optional DEFAULT bodies.
/// It is a statement because it must execute: registering the contract is what a later class declaration is
/// checked against (see Interfaces.cs), which is the finalization the whole design rests on.</summary>
/// <summary>A property an interface pins. `int Area { get; }` demands a READABLE member of that name, the
/// `set;` half demands a writable one, and a body makes the accessor a DEFAULT the engine supplies. The accessor
/// bodies are ordinary FuncDecls, so they run with `this` bound to the receiver through the method-default path.</summary>
internal sealed record InterfacePropertySignature(
    string Name,
    TypeInfo Type,
    bool HasGetter,
    bool HasSetter,
    FuncDecl? Getter,
    FuncDecl? Setter);
internal sealed class InterfaceDeclStmt : Stmt
{
    public string Name { get; private set; } = null!;
    /// <summary>The interfaces this one inherits, as written. Flattened at registration, cycle refused.</summary>
    public IReadOnlyList<string> Parents { get; private set; } = [];
    /// <summary>The members it demands: a name and an arity, which is all a dynamically typed contract can pin.</summary>
    public IReadOnlyList<(string Name, int Arity)> Members { get; private set; } = [];
    /// <summary>A member with a body, keyed by `name/arity`: a class that does not implement it gets this one.</summary>
    public IReadOnlyDictionary<string, FuncDecl> Defaults { get; private set; } = null!;
    /// <summary>Its type parameters, empty for a non-generic contract. A generic one is a TEMPLATE: a class
    /// instantiates it in `implements` (`I<int>`), and the closed form is what the registry answers for.</summary>
    public IReadOnlyList<string> TypeParameters { get; private set; } = [];
    /// <summary>The properties it pins, with any accessor bodies as defaults.</summary>
    public IReadOnlyList<InterfacePropertySignature> Properties { get; private set; } = [];

    private void Initialize(string name, IReadOnlyList<string> parents,
        IReadOnlyList<(string Name, int Arity)> members, IReadOnlyDictionary<string, FuncDecl> defaults,
        IReadOnlyList<InterfacePropertySignature>? properties, IReadOnlyList<string>? typeParameters, int line, int col)
    {
        Name = name;
        Parents = parents;
        Members = members;
        Defaults = defaults;
        Properties = properties ?? [];
        TypeParameters = typeParameters ?? [];
        Line = line;
        Column = col;
    }

    public static InterfaceDeclStmt Create(string name, IReadOnlyList<string> parents,
        IReadOnlyList<(string Name, int Arity)> members, IReadOnlyDictionary<string, FuncDecl> defaults,
        IReadOnlyList<InterfacePropertySignature>? properties, IReadOnlyList<string>? typeParameters, int line, int col)
    {
        var rent = new InterfaceDeclStmt();
        rent.Initialize(name, parents, members, defaults, properties, typeParameters, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitInterfaceDeclStmt(this);
}

internal sealed class ImportStmt : Stmt
{
    /// <summary>The path EXPRESSION. A plain string literal is the common case (and the only one a bundle can
    /// carry as load-time data); anything else (`Application.Path + "/x.inc"`, an interpolated string, a path held
    /// in a variable) is evaluated WHERE THE IMPORT RUNS, which is what a deployed script needs because its own
    /// directory is only known at run time.</summary>
    public Expr PathExpression { get; private set; } = null!;

    /// <summary>The path's TEXT when it is a plain string literal, else null. A computed path is a run-time value,
    /// so it cannot travel as <c>LibraryImport</c> data and has no load-time replay (see the compiler's arm).</summary>
    public string? LiteralPath => PathExpression is StringExpr s ? s.Value : null;

    public string? Alias { get; private set; }

    private void Initialize(Expr pathExpression, string? alias, int line, int col)
    {
        PathExpression = pathExpression;
        Alias = alias;
        Line = line;
        Column = col;
    }

    public static ImportStmt Create(Expr pathExpression, string? alias, int line, int col)
    {
        var rent = new ImportStmt();
        rent.Initialize(pathExpression, alias, line, col);
        return rent;
    }

    /// <summary>The LITERAL form, for the loader's replay of a carried import and for the bundle's descriptor,
    /// both of which only ever see a literal path.</summary>
    public static ImportStmt Create(string path, string? alias, int line, int col)
        => Create(StringExpr.Create(path, line, col), alias, line, col);

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitImportStmt(this);
}

/// <summary>Declarative namespace: `namespace A.B { … }` (block form) or file-level
/// `namespace A.B;` (the parser wraps the rest of the file's top-level statements in one
/// of these). Segments are the dot-split path ("A", "B"); Body holds the statements.</summary>
internal sealed class NamespaceDeclStmt : Stmt
{
    public string Name { get; private set; } = null!;
    public IReadOnlyList<string> Segments { get; private set; } = null!;
    public IReadOnlyList<Stmt> Body { get; private set; } = null!;

    private void Initialize(string name, IReadOnlyList<string> segments, IReadOnlyList<Stmt> body, int line, int col)
    {
        Name = name;
        Segments = segments;
        Body = body;
        Line = line;
        Column = col;
    }

    public static NamespaceDeclStmt Create(string name, IReadOnlyList<string> segments, IReadOnlyList<Stmt> body,
        int line, int col)
    {
        var rent = new NamespaceDeclStmt();
        rent.Initialize(name, segments, body, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitNamespaceDeclStmt(this);
}

internal sealed class DelegateDecl : Stmt
{
    public string Name { get; private set; } = null!;
    public TypeInfo ReturnType { get; private set; } = null!;
    public IReadOnlyList<(TypeInfo Type, string Name)> Parameters { get; private set; } = null!;
    public IReadOnlyList<bool> IsRefParams { get; private set; } = null!;
    public IReadOnlyList<bool> IsOutParams { get; private set; } = null!;
    /// <summary>Which parameters are declared `in` (read-only, passed by value), aligned with Params. SET by the parser
    /// after construction rather than threaded through Create, the way TupleElementNames travels: an `in` parameter
    /// changes one thing only, and that is how the BIND treats it (a cell whose setter refuses).
    /// </summary>
    public IReadOnlyList<bool>? IsInParams { get; set; } = null!;
    public AccessModifier Access { get; private set; }

    private void Initialize(
        string name,
        TypeInfo returnType,
        IReadOnlyList<(TypeInfo Type, string Name)> parameters,
        IReadOnlyList<bool> isRefParams,
        IReadOnlyList<bool> isOutParams,
        AccessModifier access,
        int line,
        int col
    )
    {
        Name = name;
        ReturnType = returnType;
        Parameters = parameters;
        IsRefParams = isRefParams;
        IsOutParams = isOutParams;
        Access = access;
        Line = line;
        Column = col;
    }

    public static DelegateDecl Create(
        string name,
        TypeInfo returnType,
        IReadOnlyList<(TypeInfo Type, string Name)> parameters,
        IReadOnlyList<bool> isRefParams,
        IReadOnlyList<bool> isOutParams,
        AccessModifier access,
        int line,
        int col
    )
    {
        var rent = new DelegateDecl();
        rent.Initialize(name, returnType, parameters, isRefParams, isOutParams, access, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitDelegateDecl(this);
}

internal sealed class EventDecl : Stmt
{
    public string Name { get; private set; } = null!;
    public string DelegateTypeName { get; private set; } = null!;
    public AccessModifier Access { get; private set; }

    private void Initialize(string name, string delegateTypeName, AccessModifier access, int line, int col)
    {
        Name = name;
        DelegateTypeName = delegateTypeName;
        Access = access;
        Line = line;
        Column = col;
    }

    public static EventDecl Create(string name, string delegateTypeName, AccessModifier access, int line, int col)
    {
        var rent = new EventDecl();
        rent.Initialize(name, delegateTypeName, access, line, col);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitEventDecl(this);
}

internal sealed class StructDeclStmt : Stmt
{
    public string Name { get; private set; } = null!;
    public IReadOnlyList<FieldDecl> Fields { get; set; } = null!;
    public AccessModifier Access { get; private set; }
    public IReadOnlyList<ConstructorDecl> Constructors { get; set; } = [];
    public IReadOnlyList<FuncDecl> Methods { get; set; } = [];
    /// <summary>The members this struct implements EXPLICITLY, one per contract. Kept out of
    /// <see cref="Methods"/>, so the bare name refuses and the contract is the way in.</summary>
    public IReadOnlyList<FuncDecl> ExplicitMembers { get; set; } = [];
    /// <summary>The interfaces this struct declares it implements, as written after `implements`. A struct
    /// has no base to inherit from, so its own members are the whole surface the promise is checked against.</summary>
    public IReadOnlyList<string> Interfaces { get; set; } = [];
    /// <summary>The flattened set recorded when that promise was validated, so a struct follows the same
    /// dispatch rule a class does: a DECLARED contract is asked before a structurally matched one.</summary>
    public IReadOnlyList<string> ImplementedInterfaces { get; set; } = [];
    public IReadOnlyDictionary<TokenType, FuncDecl> OperatorMethods { get; set; } = new Dictionary<TokenType, FuncDecl>();
    public IReadOnlyList<string> TypeParameters { get; private set; } = [];
    public IReadOnlyList<PropertyDecl> Properties { get; set; } = [];
    public IReadOnlyList<ClassDeclStmt> NestedClasses { get; set; } = [];
    public IReadOnlyList<StructDeclStmt> NestedStructs { get; set; } = [];
    public bool IsReadOnly { get; set; }

    private void Initialize(
        string name,
        IReadOnlyList<FieldDecl> fields,
        AccessModifier access,
        int line,
        int col,
        IReadOnlyList<string>? typeParameters = null
    )
    {
        Name = name;
        Fields = fields;
        Access = access;
        TypeParameters = typeParameters ?? [];
        Line = line;
        Column = col;
    }

    public static StructDeclStmt Create(
        string name,
        IReadOnlyList<FieldDecl> fields,
        AccessModifier access,
        int line,
        int col,
        IReadOnlyList<string>? typeParameters = null
    )
    {
        var rent = new StructDeclStmt();
        rent.Initialize(name, fields, access, line, col, typeParameters);
        return rent;
    }

    public override ExecutionResult Accept(IStmtVisitor visitor) => visitor.VisitStructDeclStmt(this);
}

// ════════════════════════════════════════════════════════════════════
// PROPERTY SUPPORT, AccessorDecl and PropertyDecl
// ════════════════════════════════════════════════════════════════════

internal sealed class AccessorDecl
{
    public AccessModifier Access { get; }
    public bool IsInit { get; }
    public AccessorDecl(AccessModifier access, bool isInit) { Access = access; IsInit = isInit; }
}

internal sealed class PropertyDecl
{
    public string Name { get; private set; } = null!;
    public TypeInfo Type { get; private set; } = null!;
    public AccessModifier Access { get; private set; }
    public bool IsStatic { get; private set; }
    public AccessorDecl? Getter { get; private set; }
    public AccessorDecl? Setter { get; private set; }
    public Expr? Initializer { get; private set; }
    public int Line { get; private set; }
    public int Column { get; private set; }

    private PropertyDecl() { }

    public static PropertyDecl Create(
        string name, TypeInfo type, AccessModifier access, bool isStatic,
        AccessorDecl? getter, AccessorDecl? setter, Expr? initializer, int line, int col)
    {
        return new PropertyDecl
        {
            Name = name,
            Type = type,
            Access = access,
            IsStatic = isStatic,
            Getter = getter,
            Setter = setter,
            Initializer = initializer,
            Line = line,
            Column = col
        };
    }
}
