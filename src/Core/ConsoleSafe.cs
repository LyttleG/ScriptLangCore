namespace ScriptLangCore;

/// <summary>Safe console color helpers, no-op when stderr/stdout is redirected to a pipe.</summary>
internal static class ConsoleSafe
{
    public static void SetColor(ConsoleColor color)
    {
        try { Console.ForegroundColor = color; }
        catch
        {
            // ignored
        }
    }

    public static void ResetColor()
    {
        try { Console.ResetColor(); }
        catch
        {
            // ignored
        }
    }
}
