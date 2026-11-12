namespace ScriptLangCore;

internal readonly record struct ExecutionResult(ControlFlow Flow, object? Value)
{
    public static ExecutionResult Continue() => new(ControlFlow.Continue, null);
    public static ExecutionResult Return(object? value) => new(ControlFlow.Return, value);
    public static ExecutionResult Break() => new(ControlFlow.Break, null);
    public static ExecutionResult ContinueLoop() => new(ControlFlow.ContinueLoop, null);
    public static ExecutionResult Throw(object? exception) => new(ControlFlow.Throw, exception);
    public static ExecutionResult Yielded(object? value) => new(ControlFlow.Yield, value);
}
