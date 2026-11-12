namespace ScriptLangCore;

/// <summary>
/// Walks a lambda expression body to find free (captured) variable names.
/// A variable is "free" if it is referenced in the lambda body but is not
/// a parameter of the lambda itself.
/// </summary>
internal static class FreeVariableAnalyzer
{
    public static List<string>? Analyze(LambdaExpr lambda)
    {
        var captured = new HashSet<string>();
        var paramNames = new HashSet<string>(lambda.Parameters.Select(p => p.Name));
        CollectFreeVars(lambda.BodyExpr, captured);
        CollectFreeVars(lambda.BodyStmts, captured);
        captured.RemoveWhere(paramNames.Contains);
        return captured.Count > 0 ? [.. captured] : null;
    }

    /// <summary>The same analysis for a NAMED nested function, which the parser never ran it for: a `FuncDecl` came
    /// out with no `CapturedNames`, so its closure snapshot captured no local scope and a caller that outlived the
    /// enclosing frame could not resolve the block's own locals (measured: `Undefined variable 'report'` from an
    /// engine-invoked callback calling a local function). Body-declared locals are filtered out later by the caller's
    /// own scope test, exactly as they are for a lambda.</summary>
    public static List<string>? Analyze(FuncDecl decl)
    {
        var captured = new HashSet<string>();
        var paramNames = new HashSet<string>(decl.Params.Select(p => p.Name));
        CollectFreeVars(decl.Body, captured);
        captured.RemoveWhere(paramNames.Contains);
        return captured.Count > 0 ? [.. captured] : null;
    }

    private static void CollectFreeVars(Expr? expr, HashSet<string> captured)
    {
        if (expr == null) return;
        switch (expr)
        {
            case VariableExpr ve:
                captured.Add(ve.Name);
                break;
            case ThisExpr te:
                // "this" is an implicit capture for lambdas defined inside methods
                captured.Add("this");
                break;
            case AssignExpr ae:
                captured.Add(ae.Name);
                CollectFreeVars(ae.Value, captured);
                break;
            case BinaryExpr be:
                CollectFreeVars(be.Left, captured);
                CollectFreeVars(be.Right, captured);
                break;
            case LogicalExpr le:
                CollectFreeVars(le.Left, captured);
                CollectFreeVars(le.Right, captured);
                break;
            case UnaryExpr ue:
                CollectFreeVars(ue.Right, captured);
                break;
            case CallExpr ce:
                foreach (var arg in ce.Arguments)
                    CollectFreeVars(arg, captured);
                CollectFreeVars(ce.Callee, captured);
                break;
            case PropertyAccessExpr pe:
                CollectFreeVars(pe.Target, captured);
                break;
            case MethodCallExpr mce:
                CollectFreeVars(mce.Receiver, captured);
                foreach (var arg in mce.Arguments)
                    CollectFreeVars(arg, captured);
                break;
            case ArrayAccessExpr aae:
                CollectFreeVars(aae.Array, captured);
                foreach (var idx in aae.Indices)
                    CollectFreeVars(idx, captured);
                break;
            case ConditionalExpr ce:
                CollectFreeVars(ce.Condition, captured);
                CollectFreeVars(ce.ThenExpr, captured);
                CollectFreeVars(ce.ElseExpr, captured);
                break;
            case CastExpr cast:
                CollectFreeVars(cast.Expression, captured);
                break;
            case IsExpr ise:
                CollectFreeVars(ise.Left, captured);
                break;
            case LambdaExpr le:
                var inner = Analyze(le);
                if (inner != null)
                    foreach (var name in inner)
                        captured.Add(name);
                break;
            case CompoundAssignExpr ca:
                CollectFreeVars(ca.Target, captured);
                CollectFreeVars(ca.Value, captured);
                break;
            case IncrementDecrementExpr ide:
                CollectFreeVars(ide.Target, captured);
                break;
            case PropertyAssignExpr pae:
                CollectFreeVars(pae.Target, captured);
                CollectFreeVars(pae.Value, captured);
                break;
            case NewInstanceExpr nie:
                CollectFreeVars(nie.ConstructorExpr, captured);
                if (nie.Arguments != null)
                    foreach (var arg in nie.Arguments)
                        CollectFreeVars(arg, captured);
                break;
            case IndexExpr ie:
                CollectFreeVars(ie.Value, captured);
                break;
            case NewArrayExpr nae:
                if (nae.Sizes != null)
                    foreach (var size in nae.Sizes)
                        CollectFreeVars(size, captured);
                if (nae.Initializer != null)
                    foreach (var init in nae.Initializer)
                        CollectFreeVars(init, captured);
                break;
            case WithExpr we:
                CollectFreeVars(we.Base, captured);
                foreach (var (_, fv) in we.Fields)
                    CollectFreeVars(fv, captured);
                break;
            case ObjectInitExpr oie:
                CollectFreeVars(oie.Inner, captured);
                foreach (var (_, iv) in oie.Initializers)
                    CollectFreeVars(iv, captured);
                break;
            case MatchExpr me:
                CollectFreeVars(me.Subject, captured);
                foreach (var arm in me.Arms)
                {
                    if (arm.Guard != null) CollectFreeVars(arm.Guard, captured);
                    CollectFreeVars(arm.Value, captured);
                }
                break;
            case SwitchExpr se:
                CollectFreeVars(se.Subject, captured);
                foreach (var arm in se.Arms)
                {
                    if (arm.Guard != null) CollectFreeVars(arm.Guard, captured);
                    CollectFreeVars(arm.Value, captured);
                }
                break;
            case ListLiteralExpr lle:
                foreach (var elem in lle.Elements)
                    CollectFreeVars(elem, captured);
                break;
            case DictLiteralExpr dle:
                foreach (var (key, val) in dle.Entries)
                {
                    CollectFreeVars(key, captured);
                    CollectFreeVars(val, captured);
                }
                break;
            case ArrayAssignExpr aaae:
                CollectFreeVars(aaae.Array, captured);
                foreach (var idx in aaae.Indices)
                    CollectFreeVars(idx, captured);
                CollectFreeVars(aaae.Value, captured);
                break;
            case NullCoalescingExpr nce:
                CollectFreeVars(nce.Left, captured);
                CollectFreeVars(nce.Right, captured);
                break;
            case YieldExpr ye:
                CollectFreeVars(ye.Value, captured);
                break;
            case AwaitExpr awe:
                CollectFreeVars(awe.Operand, captured);
                break;
            case RangeExpr re:
                CollectFreeVars(re.From, captured);
                CollectFreeVars(re.To, captured);
                break;
            case ComprehensionExpr ce:
                CollectFreeVars(ce.ElementExpr, captured);
                CollectFreeVars(ce.Collection, captured);
                CollectFreeVars(ce.Filter, captured);
                break;
            case IsNullExpr ine:
                CollectFreeVars(ine.Left, captured);
                break;
            case AsExpr ae:
                CollectFreeVars(ae.Left, captured);
                break;
        }
    }

    private static void CollectFreeVars(IReadOnlyList<Stmt>? stmts, HashSet<string> captured)
    {
        if (stmts == null) return;
        foreach (var stmt in stmts)
            CollectFreeVars(stmt, captured);
    }

    private static void CollectFreeVars(Stmt stmt, HashSet<string> captured)
    {
        switch (stmt)
        {
            case ExprStmt es:
                CollectFreeVars(es.Expression, captured);
                break;
            case VarDeclStmt vds:
                CollectFreeVars(vds.Initializer, captured);
                break;
            case BlockStmt bs:
                CollectFreeVars(bs.Statements, captured);
                break;
            case NamespaceDeclStmt nsd:
                CollectFreeVars(nsd.Body, captured);
                break;
            case IfStmt ifs:
                CollectFreeVars(ifs.Condition, captured);
                CollectFreeVars(ifs.ThenBranch, captured);
                CollectFreeVars(ifs.ElseBranch, captured);
                break;
            case WhileStmt ws:
                CollectFreeVars(ws.Condition, captured);
                CollectFreeVars(ws.Body, captured);
                break;
            case ReturnStmt rs:
                CollectFreeVars(rs.Value, captured);
                break;
            case ForStmt fs:
                if (fs.Init != null) CollectFreeVars(fs.Init, captured);
                CollectFreeVars(fs.Condition, captured);
                CollectFreeVars(fs.Increment, captured);
                CollectFreeVars(fs.Body, captured);
                break;
            case ForeachStmt fes:
                CollectFreeVars(fes.Collection, captured);
                CollectFreeVars(fes.Body, captured);
                break;
            case ThrowStmt ts:
                CollectFreeVars(ts.Expression, captured);
                break;
            case TryStmt ts:
                CollectFreeVars(ts.Body, captured);
                foreach (var cc in ts.Catches)
                    CollectFreeVars(cc.Body, captured);
                if (ts.Finally != null)
                    CollectFreeVars(ts.Finally, captured);
                break;
            case SwitchStmt ss:
                CollectFreeVars(ss.Subject, captured);
                foreach (var caseClause in ss.Cases)
                {
                    if (caseClause.Guard != null)
                        CollectFreeVars(caseClause.Guard, captured);
                    CollectFreeVars(caseClause.Body, captured);
                }
                break;
            case DestructuringStmt ds:
                CollectFreeVars(ds.Source, captured);
                break;
            case UsingStmt us:
                CollectFreeVars(us.Initializer, captured);
                CollectFreeVars(us.Body, captured);
                break;
        }
    }
}
