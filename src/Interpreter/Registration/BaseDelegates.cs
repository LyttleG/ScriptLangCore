namespace ScriptLangCore;

internal partial class Interpreter
{
    private void RegisterBaseDelegates()
    {
        // ============================================================================
        // COROUTINES STATUSES (a dedicated namespace for coroutine already exists)
        // ============================================================================
        CurrentFrame.Scopes.Define("CoroutineStatus", typeof(CoroutineStatus));

        // ============================================================================
        // ACTION DELEGATE TEMPLATES (void return)
        // ============================================================================

        DelegateInfos["Action"] = CreateActionTemplate(0);
        DelegateInfos["Action<T>"] = CreateActionTemplate(1);
        DelegateInfos["Action<T1,T2>"] = CreateActionTemplate(2);
        DelegateInfos["Action<T1,T2,T3>"] = CreateActionTemplate(3);
        DelegateInfos["Action<T1,T2,T3,T4>"] = CreateActionTemplate(4);

        // ============================================================================
        // FUNC DELEGATE TEMPLATES (with return value)
        // ============================================================================

        DelegateInfos["Func<TResult>"] = CreateFuncTemplate(0);
        DelegateInfos["Func<T,TResult>"] = CreateFuncTemplate(1);
        DelegateInfos["Func<T1,T2,TResult>"] = CreateFuncTemplate(2);
        DelegateInfos["Func<T1,T2,T3,TResult>"] = CreateFuncTemplate(3);
        DelegateInfos["Func<T1,T2,T3,T4,TResult>"] = CreateFuncTemplate(4);

        // ============================================================================
        // PREDICATE DELEGATE TEMPLATE
        // ============================================================================

        DelegateInfos["Predicate<T>"] = new DelegateInfo(
            "Predicate<T>",
            new TypeInfo(TokenType.Bool, false, 0, 0, null),
            [(new TypeInfo(TokenType.Object, true, 0, 0, null), "arg")],
            [false], AccessModifier.Public, 0, 0);

        // ============================================================================
        // COMPARISON DELEGATE TEMPLATE
        // ============================================================================

        DelegateInfos["Comparison<T>"] = new DelegateInfo(
            "Comparison<T>",
            new TypeInfo(TokenType.Int, false, 0, 0, null),
            [(new TypeInfo(TokenType.Object, true, 0, 0, null), "x"),
            (new TypeInfo(TokenType.Object, true, 0, 0, null), "y")],
            [false, false], AccessModifier.Public, 0, 0);

        // ============================================================================
        // CONVERTER DELEGATE TEMPLATE
        // ============================================================================

        DelegateInfos["Converter<TInput,TOutput>"] = new DelegateInfo(
            "Converter<TInput,TOutput>",
            new TypeInfo(TokenType.Object, true, 0, 0, null),
            [(new TypeInfo(TokenType.Object, true, 0, 0, null), "input")],
            [false], AccessModifier.Public, 0, 0);

        // ============================================================================
        // EVENT HANDLER DELEGATE
        // ============================================================================

        DelegateInfos["EventHandler"] = new DelegateInfo(
            "EventHandler",
            TypeInfo.Void,
            [
                (new TypeInfo(TokenType.Object, true, 0, 0, null), "sender"),
                (new TypeInfo(TokenType.Object, true, 0, 0, null), "e")
            ],
            [false, false], AccessModifier.Public, 0, 0);

        DelegateInfos["EventHandler<T>"] = new DelegateInfo(
            "EventHandler<T>",
            TypeInfo.Void,
            [
                (new TypeInfo(TokenType.Object, true, 0, 0, null), "sender"),
                (new TypeInfo(TokenType.Object, true, 0, 0, null), "e")
            ],
            [false, false], AccessModifier.Public, 0, 0);

        // ============================================================================
        // REGISTER TYPE NAMES IN SCOPE FOR 'is' AND 'as' OPERATORS
        // ============================================================================

        _ = new TypeInfo(TokenType.Object, true, 0, 0, null);

        // ---- Base Action names ----
        CurrentFrame.Scopes.Define("Action", new TypeInfo(TokenType.Object, true, 0, 0, "Action"));

        // ---- Action with arity (using backtick notation) ----
        CurrentFrame.Scopes.Define("Action`1", new TypeInfo(TokenType.Object, true, 0, 0, "Action`1"));
        CurrentFrame.Scopes.Define("Action`2", new TypeInfo(TokenType.Object, true, 0, 0, "Action`2"));
        CurrentFrame.Scopes.Define("Action`3", new TypeInfo(TokenType.Object, true, 0, 0, "Action`3"));
        CurrentFrame.Scopes.Define("Action`4", new TypeInfo(TokenType.Object, true, 0, 0, "Action`4"));

        // ---- Action with friendly generic syntax ----
        CurrentFrame.Scopes.Define("Action<T>", new TypeInfo(TokenType.Object, true, 0, 0, "Action<T>"));
        CurrentFrame.Scopes.Define("Action<T1,T2>", new TypeInfo(TokenType.Object, true, 0, 0, "Action<T1,T2>"));
        CurrentFrame.Scopes.Define("Action<T1,T2,T3>", new TypeInfo(TokenType.Object, true, 0, 0, "Action<T1,T2,T3>"));
        CurrentFrame.Scopes.Define("Action<T1,T2,T3,T4>", new TypeInfo(TokenType.Object, true, 0, 0, "Action<T1,T2,T3,T4>"));

        // ---- Action with numeric suffix (backtick-free alternative) ----
        CurrentFrame.Scopes.Define("Action1", new TypeInfo(TokenType.Object, true, 0, 0, "Action1"));
        CurrentFrame.Scopes.Define("Action2", new TypeInfo(TokenType.Object, true, 0, 0, "Action2"));
        CurrentFrame.Scopes.Define("Action3", new TypeInfo(TokenType.Object, true, 0, 0, "Action3"));
        CurrentFrame.Scopes.Define("Action4", new TypeInfo(TokenType.Object, true, 0, 0, "Action4"));

        // ---- Base Func names ----
        CurrentFrame.Scopes.Define("Func", new TypeInfo(TokenType.Object, true, 0, 0, "Func"));

        // ---- Func with arity (using backtick notation) ----
        CurrentFrame.Scopes.Define("Func`1", new TypeInfo(TokenType.Object, true, 0, 0, "Func`1"));
        CurrentFrame.Scopes.Define("Func`2", new TypeInfo(TokenType.Object, true, 0, 0, "Func`2"));
        CurrentFrame.Scopes.Define("Func`3", new TypeInfo(TokenType.Object, true, 0, 0, "Func`3"));
        CurrentFrame.Scopes.Define("Func`4", new TypeInfo(TokenType.Object, true, 0, 0, "Func`4"));

        // ---- Func with friendly generic syntax ----
        CurrentFrame.Scopes.Define("Func<TResult>", new TypeInfo(TokenType.Object, true, 0, 0, "Func<TResult>"));
        CurrentFrame.Scopes.Define("Func<T,TResult>", new TypeInfo(TokenType.Object, true, 0, 0, "Func<T,TResult>"));
        CurrentFrame.Scopes.Define("Func<T1,T2,TResult>", new TypeInfo(TokenType.Object, true, 0, 0, "Func<T1,T2,TResult>"));
        CurrentFrame.Scopes.Define("Func<T1,T2,T3,TResult>", new TypeInfo(TokenType.Object, true, 0, 0, "Func<T1,T2,T3,TResult>"));
        CurrentFrame.Scopes.Define("Func<T1,T2,T3,T4,TResult>", new TypeInfo(TokenType.Object, true, 0, 0, "Func<T1,T2,T3,T4,TResult>"));

        // ---- Func with numeric suffix (backtick-free alternative) ----
        CurrentFrame.Scopes.Define("Func1", new TypeInfo(TokenType.Object, true, 0, 0, "Func1"));
        CurrentFrame.Scopes.Define("Func2", new TypeInfo(TokenType.Object, true, 0, 0, "Func2"));
        CurrentFrame.Scopes.Define("Func3", new TypeInfo(TokenType.Object, true, 0, 0, "Func3"));
        CurrentFrame.Scopes.Define("Func4", new TypeInfo(TokenType.Object, true, 0, 0, "Func4"));

        // ---- Base Predicate names ----
        CurrentFrame.Scopes.Define("Predicate", new TypeInfo(TokenType.Object, true, 0, 0, "Predicate"));

        // ---- Predicate variants ----
        CurrentFrame.Scopes.Define("Predicate`1", new TypeInfo(TokenType.Object, true, 0, 0, "Predicate`1"));
        CurrentFrame.Scopes.Define("Predicate<T>", new TypeInfo(TokenType.Object, true, 0, 0, "Predicate<T>"));
        CurrentFrame.Scopes.Define("Predicate1", new TypeInfo(TokenType.Object, true, 0, 0, "Predicate1"));

        // ---- Base Comparison names ----
        CurrentFrame.Scopes.Define("Comparison", new TypeInfo(TokenType.Object, true, 0, 0, "Comparison"));

        // ---- Comparison variants ----
        CurrentFrame.Scopes.Define("Comparison`1", new TypeInfo(TokenType.Object, true, 0, 0, "Comparison`1"));
        CurrentFrame.Scopes.Define("Comparison<T>", new TypeInfo(TokenType.Object, true, 0, 0, "Comparison<T>"));

        // ---- Base Converter names ----
        CurrentFrame.Scopes.Define("Converter", new TypeInfo(TokenType.Object, true, 0, 0, "Converter"));

        // ---- Converter variants ----
        CurrentFrame.Scopes.Define("Converter`2", new TypeInfo(TokenType.Object, true, 0, 0, "Converter`2"));
        CurrentFrame.Scopes.Define("Converter<TInput,TOutput>", new TypeInfo(TokenType.Object, true, 0, 0, "Converter<TInput,TOutput>"));

        // ---- EventHandler ----
        CurrentFrame.Scopes.Define("EventHandler", new TypeInfo(TokenType.Object, true, 0, 0, "EventHandler"));
        CurrentFrame.Scopes.Define("EventHandler<T>", new TypeInfo(TokenType.Object, true, 0, 0, "EventHandler<T>"));

        // ============================================================================
        // EXPOSE COMMON DELEGATES TO GLOBAL SCOPE
        // ============================================================================

        // These make the delegate types available as values for 'is' and 'as' checks
        CurrentFrame.Scopes.Define("ActionType", DelegateInfos["Action"]);
        CurrentFrame.Scopes.Define("FuncType", DelegateInfos["Func<TResult>"]);
        CurrentFrame.Scopes.Define("PredicateType", DelegateInfos["Predicate<T>"]);

        //CurrentFrame.Scopes.Define("List", new TypeInfo(TokenType.Object, true, 0, 0, "List<>"));
    }

    private static DelegateInfo CreateActionTemplate(int paramCount)
    {
        var objectType = new TypeInfo(TokenType.Object, true, 0, 0, null);
        var parameters = Enumerable.Range(0, paramCount)
            .Select(i => (objectType, $"arg{i}"))
            .ToArray();

        var name = paramCount switch
        {
            0 => "Action",
            1 => "Action<T>",
            2 => "Action<T1,T2>",
            3 => "Action<T1,T2,T3>",
            4 => "Action<T1,T2,T3,T4>",
            _ => $"Action<{string.Join(",", Enumerable.Repeat("T", paramCount))}>"
        };

        return new DelegateInfo(
            name,
            TypeInfo.Void,
            parameters,
            [.. Enumerable.Repeat(false, paramCount)],
            AccessModifier.Public, 0, 0);
    }

    private static DelegateInfo CreateFuncTemplate(int paramCount)
    {
        var objectType = new TypeInfo(TokenType.Object, true, 0, 0, null);
        var parameters = Enumerable.Range(0, paramCount)
            .Select(i => (objectType, $"arg{i}"))
            .ToArray();

        var typeParams = Enumerable.Repeat("T", paramCount).Append("TResult");
        var name = paramCount switch
        {
            0 => "Func<TResult>",
            1 => "Func<T,TResult>",
            2 => "Func<T1,T2,TResult>",
            3 => "Func<T1,T2,T3,TResult>",
            4 => "Func<T1,T2,T3,T4,TResult>",
            _ => $"Func<{string.Join(",", typeParams)}>"
        };

        return new DelegateInfo(
            name,
            objectType,
            parameters,
            [.. Enumerable.Repeat(false, paramCount)],
            AccessModifier.Public, 0, 0);
    }
}