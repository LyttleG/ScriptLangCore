namespace ScriptLangCore;

internal sealed record ConstructorDecl(
    IReadOnlyList<(TypeInfo Type, string Name)> Params,
    IReadOnlyList<bool> IsRefParams,
    IReadOnlyList<Stmt> Body,
    AccessModifier Access,
    int Line, int Column,
    IReadOnlyList<bool>? IsOutParams = null,
    IReadOnlyList<bool>? IsParams = null
)
{
    public bool IsStatic { get; init; }
    public Bytecode? Compiled { get; set; }

    /// <summary>Which parameters are declared `in` (read-only, by value), aligned with Params. Set by the parser, and
    /// enforced as a DECLARATION-TIME refusal: a body that writes an `in` parameter (or takes a reference to one) is
    /// rejected where it is declared, which is the C# rule and needs no runtime guard.</summary>
    public IReadOnlyList<bool>? IsInParams { get; set; }

    /// <summary>True when the LAST declared parameter is a `params` tail. Nullable like IsOutParams: every
    /// construction site that does not know about `params` (an old clone, a bodyless shell) leaves it null, and
    /// null means "no tail", a constructor can only have one.</summary>
    public bool HasParamsTail => IsParams is { Count: > 0 } p && p[^1];

    /// <summary>The parameters that must be supplied POSITIONALLY before the tail (all of them when there is
    /// no tail). A `params` tail is zero-or-more, so it is not part of the minimum.</summary>
    public int FixedParamCount => HasParamsTail ? Params.Count - 1 : Params.Count;
}
