namespace ScriptLangCore;

/// <summary>Restricts file paths to allowed directories for MCP tool safety.</summary>
internal sealed class McpSandbox(string[] allowedDirs)
{
    private readonly string[] _allowedDirs = [.. allowedDirs.Select(Path.GetFullPath)];
    private readonly bool _allowAll = allowedDirs.Length == 0;

    /// <summary>Returns true if the path is inside one of the allowed directories.</summary>
    public bool IsPathAllowed(string path)
    {
        if (_allowAll) return true;
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            var full = Path.GetFullPath(path);
            foreach (var dir in _allowedDirs)
            {
                if (full.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Also allow files directly at the root of the allowed dir
                if (string.Equals(Path.GetDirectoryName(full), dir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
