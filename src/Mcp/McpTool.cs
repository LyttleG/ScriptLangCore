namespace ScriptLangCore;

/// <summary>Represents a registered MCP tool that a script exposes.</summary>
internal sealed class McpTool(string name, string description, ScriptFunction handler, object? inputSchema)
{
    /// <summary>The tool name (used in tools/call).</summary>
    [JsonPropertyName("name")]
    public string Name { get; } = name;

    /// <summary>Human-readable description shown to the AI.</summary>
    [JsonPropertyName("description")]
    public string Description { get; } = description;

    /// <summary>The script function invoked on tools/call.</summary>
    [JsonIgnore]
    public ScriptFunction Handler { get; } = handler;

    /// <summary>JSON Schema object describing the input parameters.</summary>
    [JsonPropertyName("inputSchema")]
    public object? InputSchema { get; } = inputSchema;
}
