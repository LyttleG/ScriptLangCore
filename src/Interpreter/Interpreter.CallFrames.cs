namespace ScriptLangCore;

internal partial class Interpreter
{
    private readonly record struct CallFrame(ScopeStack Scopes)
    {
        public ScopeStack Scopes { get; } = Scopes;
    }

    private sealed class ContextPopper(Interpreter owner) : IDisposable
    {
        public void Dispose()
        {
            if (_interpreterStack == null) return;
            // Remove `owner` wherever it appears in the stack (robust against out-of-order pops)
            var temp = new Stack<Interpreter>();
            while (_interpreterStack.TryPop(out var itp))
            {
                if (itp == owner) break;
                temp.Push(itp);
            }
            while (temp.TryPop(out var itp))
                _interpreterStack.Push(itp);
        }
    }

    /// <summary>Restores the type-parameter bindings that were active before a generic body's own map replaced them,
    /// so an exception out of that body cannot leave its `T` resolving in the caller.</summary>
    private sealed class TypeBindingPopper(Interpreter owner, IReadOnlyDictionary<string, TypeInfo>? previous) : IDisposable
    {
        public void Dispose() => owner.ActiveTypeBindings = previous;
    }
}
