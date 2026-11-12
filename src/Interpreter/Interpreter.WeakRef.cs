namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>Script-accessible weak reference, holds a reference without preventing GC.</summary>
    internal sealed class WeakRef(object? target) : IDisposable
    {
        private readonly WeakReference<object> _ref = new(target!);
        public object? TryGetTarget() { return _ref.TryGetTarget(out var t) ? t : null; }
        public bool IsAlive => _ref.TryGetTarget(out _);
        public void Dispose() { } // no unmanaged resource, IDisposable for uniform script using/is
        public override string ToString() => ScriptNameOf(typeof(WeakRef)) ?? "WeakRef";
    }
}
