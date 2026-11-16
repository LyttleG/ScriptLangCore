namespace ScriptLangCore;

internal partial class Interpreter
{
    private sealed class CaptureCollector(HashSet<string> paramNames, ScopeStack scopes)
    {
        public bool HasExternalCapture { get; private set; }

        public void Visit(Expr expr)
        {
            if (HasExternalCapture) return;
            switch (expr)
            {
                case LambdaExpr nestedLambda:
                    var nestedParams = new HashSet<string>(nestedLambda.Parameters.Count);
                    foreach (var p in nestedLambda.Parameters)
                        nestedParams.Add(p.Name);
                    VisitChildren(expr, nestedParams);
                    break;
                default:
                    VisitChildren(expr, paramNames);
                    break;
            }
        }

        public void Visit(Stmt stmt)
        {
            if (HasExternalCapture)
                return;

            switch (stmt)
            {
                case VarDeclStmt v:
                    if (v.Initializer != null) Visit(v.Initializer);
                    break;

                case ReturnStmt r:
                    if (r.Value != null) Visit(r.Value);
                    break;

                case IfStmt iff:
                    Visit(iff.Condition);
                    foreach (var s in iff.ThenBranch) Visit(s);
                    foreach (var s in iff.ElseBranch) Visit(s);
                    break;

                case WhileStmt w:
                    Visit(w.Condition);
                    foreach (var s in w.Body) Visit(s);
                    break;

                case ForStmt f:
                    if (f.Init != null) Visit(f.Init);
                    Visit(f.Condition);
                    if (f.Increment != null) Visit(f.Increment);
                    foreach (var s in f.Body) Visit(s);
                    break;

                case ForeachStmt fe:
                    Visit(fe.Collection);
                    foreach (var s in fe.Body) Visit(s);
                    break;

                case BlockStmt b:
                    foreach (var s in b.Statements) Visit(s);
                    break;

                case ExprStmt es:
                    Visit(es.Expression);
                    break;
            }
        }

        private void VisitChildren(Expr expr, HashSet<string> currentParamNames)
        {
            if (HasExternalCapture) return;
            switch (expr)
            {
                case BinaryExpr b:
                    Visit(b.Left); Visit(b.Right);
                    break;

                case UnaryExpr u:
                    Visit(u.Right);
                    break;

                case AssignExpr a:
                    Visit(a.Value);
                    break;

                case CallExpr c:
                    Visit(c.Callee);
                    foreach (var arg in c.Arguments) Visit(arg);
                    break;

                case PropertyAccessExpr p:
                    Visit(p.Target);
                    break;

                case ThisExpr:
                    // "this" is an implicit external capture for lambdas inside methods/constructors
                    //, flag it so the lambda gets a real snapshot carrying the instance.
                    HasExternalCapture = true;
                    break;

                case VariableExpr v:
                    if (!currentParamNames.Contains(v.Name))
                    {
                        try { scopes.TryGet(v.Name, out _); HasExternalCapture = true; }
                        catch
                        {
                            // ignored
                        }
                    }
                    break;

                case ConditionalExpr t:
                    Visit(t.Condition); Visit(t.ThenExpr); Visit(t.ElseExpr);
                    break;

                case ArrayAccessExpr ix:
                    Visit(ix.Array);
                    foreach (var idx in ix.Indices) Visit(idx);
                    break;

                case ArrayAssignExpr aa:
                    Visit(aa.Array);
                    foreach (var idx in aa.Indices) Visit(idx);
                    Visit(aa.Value);
                    break;

                case PropertyAssignExpr pa:
                    Visit(pa.Target);
                    Visit(pa.Value);
                    break;

                case NewInstanceExpr ne:
                    if (ne.Arguments != null)
                        foreach (var arg in ne.Arguments) Visit(arg);
                    break;

                case CastExpr ce:
                    Visit(ce.Expression);
                    break;
                case MethodCallExpr m:
                    Visit(m.Receiver);
                    foreach (var arg in m.Arguments) Visit(arg);
                    break;

                case LogicalExpr le:
                    Visit(le.Left); Visit(le.Right);
                    break;

                case IsExpr ise:
                    Visit(ise.Left);
                    break;

                case CompoundAssignExpr ca:
                    Visit(ca.Target); Visit(ca.Value);
                    break;

                case IncrementDecrementExpr ide:
                    Visit(ide.Target);
                    break;

                case IndexExpr ie:
                    Visit(ie.Value);
                    break;

                case NewArrayExpr nae:
                    if (nae.Sizes != null)
                        foreach (var size in nae.Sizes) Visit(size);
                    if (nae.Initializer != null)
                        foreach (var init in nae.Initializer) Visit(init);
                    break;

                case WithExpr we:
                    Visit(we.Base);
                    foreach (var (_, fv) in we.Fields) Visit(fv);
                    break;

                case ObjectInitExpr oie:
                    Visit(oie.Inner);
                    foreach (var (_, iv) in oie.Initializers) Visit(iv);
                    break;

                case MatchExpr me:
                    Visit(me.Subject);
                    foreach (var arm in me.Arms)
                    {
                        if (arm.Guard != null) Visit(arm.Guard);
                        Visit(arm.Value);
                    }
                    break;

                case SwitchExpr se:
                    Visit(se.Subject);
                    foreach (var arm in se.Arms)
                    {
                        if (arm.Guard != null) Visit(arm.Guard);
                        Visit(arm.Value);
                    }
                    break;

                case LambdaExpr nested:
                    var nestedParams = new HashSet<string>(nested.Parameters.Count);
                    foreach (var p in nested.Parameters) nestedParams.Add(p.Name);
                    if (nested.BodyExpr != null) VisitChildren(nested.BodyExpr, nestedParams);
                    if (nested.BodyStmts != null)
                        foreach (var s in nested.BodyStmts)
                            if (s is ExprStmt es) VisitChildren(es.Expression, nestedParams);
                    break;
            }
        }
    }
}
