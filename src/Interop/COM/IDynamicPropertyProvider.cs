namespace ScriptLangCore;

/// <summary>
/// Implemented by types that resolve member access (., =, ()) dynamically at runtime.
/// The interpreter checks this interface as a fallback after reflection-based resolution fails.
/// </summary>
internal interface IDynamicPropertyProvider
{
    bool TryGetProperty(string name, out object? value);
    bool TrySetProperty(string name, object? value);
    bool TryInvokeMethod(string name, object?[] args, out object? result);

    /// <summary>Invoke with the PARAMETER NAMES the caller wrote, `obj.SaveAs(p, AccessMode: 3)`. The AST carries
    /// them for every other provider; this default leaves a provider that cannot relate a name to a parameter
    /// unchanged. It is why COM had been silently re-pointing a named value at the next POSITION: `AccessMode: 3`
    /// arrived as `FileFormat = 3` and wrote a CSV with an .xlsx name (measured, not suspected).</summary>
    bool TryInvokeNamedMethod(string name, object?[] args, IReadOnlyList<string?>? argNames, out object? result)
        => TryInvokeMethod(name, args, out result);
}
