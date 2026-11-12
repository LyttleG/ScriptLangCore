namespace ScriptLangCore;

internal partial class Interpreter
{
    internal readonly record struct ModuleInfo(
        IReadOnlyDictionary<string, object?> Exports,
        bool IsEvaluated,
        IReadOnlyDictionary<string, string>? ExportPaths = null,
        IReadOnlyDictionary<string, object?>? ModuleGlobals = null);
}
