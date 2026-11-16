namespace ScriptLangCore;

/// <summary>Represents a registered MCP resource exposed by a script.</summary>
internal sealed class McpResource(string uri, string name, string mimeType, ScriptFunction handler)
{
    public string Uri { get; } = uri;
    public string Name { get; } = name;
    public string MimeType { get; } = mimeType;
    public ScriptFunction Handler { get; } = handler;
}
