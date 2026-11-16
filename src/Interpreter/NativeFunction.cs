namespace ScriptLangCore;

public interface INativeFunction
{
    string Name { get; }
    int Arity { get; }
    IReadOnlyList<TokenType> ParameterTypes { get; }
    TokenType? VariadicType { get; }
    TokenType ReturnType { get; }
    object? Invoke(object?[] args);
}

internal readonly record struct NativeFunctionAdapter(
    string Name,
    int Arity,
    IReadOnlyList<TokenType> ParameterTypes,
    TokenType? VariadicType,
    TokenType ReturnType,
    Func<object?[], object?> FunPtr) : INativeFunction
{
    public string Name { get; } = Name;
    public int Arity { get; } = Arity;
    public IReadOnlyList<TokenType> ParameterTypes { get; } = ParameterTypes;
    public TokenType? VariadicType { get; } = VariadicType;
    public TokenType ReturnType { get; } = ReturnType;

    public readonly object? Invoke(object?[] args) => FunPtr(args);

    public override string ToString()
        => NativeSignature.Of(Name, ParameterTypes, VariadicType, ReturnType);
}

/// <summary>The rendering a SCRIPT sees for a native, in the same shape a script function uses
/// (`func Named(int) -> int`), because a builtin, a DLL export and a script function are all callables: printing
/// two of them one way and the third another is the non-uniformity this replaced (measured: `[Native: print]` and
/// `NativeFunction Beep` beside `func Named(int) -> int`). ONE implementation, so the two native kinds cannot
/// drift. `arityUnknown` is for a DLL export, whose arity is not a script signature at all.</summary>
internal static class NativeSignature
{
    internal static string Of(string name, IReadOnlyList<TokenType>? parameterTypes, TokenType? variadicType,
                              TokenType returnType, bool arityUnknown = false)
    {
        var sig = new StringBuilder(64);
        sig.Append("func ").Append(name).Append('(');
        if (arityUnknown)
        {
            sig.Append("...");
        }
        else
        {
            for (var i = 0; i < (parameterTypes?.Count ?? 0); i++)
            {
                if (i > 0) sig.Append(", ");
                sig.Append(TypeSystem.Name(parameterTypes![i]));
            }
            if (variadicType is { } vt)
            {
                if (parameterTypes is { Count: > 0 }) sig.Append(", ");
                sig.Append(TypeSystem.Name(vt)).Append("...");
            }
        }
        sig.Append(')');
        if (!arityUnknown && returnType != TokenType.Void)
            sig.Append(" -> ").Append(TypeSystem.Name(returnType));
        return sig.ToString();
    }
}
