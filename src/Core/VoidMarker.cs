namespace ScriptLangCore;

/// <summary>
/// Marker for void return values to distinguish from actual null values.
/// </summary>
public sealed record VoidMarker
{
    public static readonly VoidMarker Instance = new();
    private VoidMarker() { }

    public override string ToString() => "<void>";
}
