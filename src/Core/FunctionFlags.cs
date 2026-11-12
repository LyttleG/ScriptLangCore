namespace ScriptLangCore;

[Flags]
internal enum FunctionFlags
{
    None = 0,
    Legacy = 1 << 0, // default (existing behavior)
    Pure = 1 << 1, // memoizable
    Coroutine = 1 << 2, // can yield (resumable)
    Generic = 1 << 3, // instantiated from a generic template
}
