namespace ScriptLangCore;

internal partial class Interpreter
{
    private sealed class EventInfoReferenceComparer : IEqualityComparer<EventInfo>
    {
        public static readonly EventInfoReferenceComparer Instance = new();
        public bool Equals(EventInfo? x, EventInfo? y) => ReferenceEquals(x, y);
        public int GetHashCode(EventInfo obj) => RuntimeHelpers.GetHashCode(obj);
    }

    internal sealed record CombinedDelegate(Interpreter Interpreter, IReadOnlyList<ScriptFunction> Handlers)
    {
        public object? Invoke(object?[] args, int line, int column)
        {
            object? lastResult = null;
            foreach (var handler in Handlers)
                lastResult = Interpreter.InvokeScriptFunction(handler, args, line, column);

            return lastResult;
        }

        public override string ToString()
            => string.Join(" + ", Handlers.Select(h => h.ToString()));
    }

    public sealed record DelegateInfo(
        string Name,
        TypeInfo ReturnType,
        IReadOnlyList<(TypeInfo Type, string Name)> Parameters,
        IReadOnlyList<bool> IsRefParams,
        AccessModifier Access,
        int Line,
        int Column,
        IReadOnlyList<bool>? IsOutParams = null
    )
    {
        public int Line { get; } = Line;   // <-- make accessible
        public int Column { get; } = Column;
        public override string ToString()
        {
            var paramStr = string.Join(", ", Parameters.Select(static p => $"{p.Type.BaseType.ToString()[..^4]} {p.Name}"));
            return $"Delegate {ReturnType.BaseType.ToString()[..^4]} {Name}({paramStr})";
        }
    }

    internal sealed record EventInfo(string Name, string DelegateTypeName, List<ScriptFunction> Subscribers)
    {
        public bool HasSubscribers => Subscribers.Count > 0;
        public override string ToString() => $"<event {Name} of {DelegateTypeName} ({Subscribers.Count} subscriber(s))>";
    }
}
