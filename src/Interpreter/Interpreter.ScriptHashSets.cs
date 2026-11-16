namespace ScriptLangCore;

internal partial class Interpreter
{
    // ── HashSet ────────────────────────────────────────────────────────────
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    private sealed record BoxedScriptHashSet(TypeInfo ElementType) : IScriptHashSet, IEnumerable<object?>
    {
        public IEnumerator<object?> GetEnumerator()
        {
            HashSet<object?> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            HashSet<object?> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }

        private readonly HashSet<object?> _items = new(TupleValueComparer.Instance);
        private readonly Lock _syncRoot = new();

        public int Count { get { lock (_syncRoot) { return _items.Count; } } }
        public TypeInfo ElementType { get; } = ElementType;

        public void Add(object? item, int line, int column)
        {
            lock (_syncRoot)
            {
                var coerced = CoerceTo(item, ElementType, line, column);
                _items.Add(coerced);
            }
        }

        public void AddRaw(object item) { lock (_syncRoot) { _items.Add(item); } }
        public bool Contains(object item) { lock (_syncRoot) { return _items.Contains(item); } }
        public bool Remove(object item) { lock (_syncRoot) { return _items.Remove(item); } }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _items.Clear();
                _items.TrimExcess();
            }
        }

        public Array ToArray()
        {
            lock (_syncRoot)
            {
                var result = new object?[_items.Count];
                _items.CopyTo(result);
                return result;
            }
        }

        public override string ToString() => ValueFormatter.Format(this);
    }


    private sealed record UnboxedScriptHashSet<T>(TypeInfo ElementType) : IScriptHashSet, IEnumerable<T> where T : notnull
    {
        public IEnumerator<T> GetEnumerator()
        {
            HashSet<T> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            HashSet<T> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }

        private readonly HashSet<T> _items = [];
        private readonly Lock _syncRoot = new();

        public int Count { get { lock (_syncRoot) { return _items.Count; } } }
        public TypeInfo ElementType { get; } = ElementType;

        public void Add(object? item, int line, int column)
        {
            lock (_syncRoot)
            {
                var value = item is T t ? t : (T)CoerceTo(item, ElementType, line, column)!;
                _items.Add(value);
            }
        }

        public void AddRaw(object item) { lock (_syncRoot) { _items.Add(item is T t ? t : (T)item); } }
        public bool Contains(object item) { lock (_syncRoot) { return item is T t ? _items.Contains(t) : _items.Contains((T)CoerceTo(item, ElementType, 0, 0)!); } }
        public bool Remove(object item) { lock (_syncRoot) { return item is T t ? _items.Remove(t) : _items.Remove((T)CoerceTo(item, ElementType, 0, 0)!); } }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _items.Clear();
                _items.TrimExcess();
            }
        }

        public Array ToArray()
        {
            lock (_syncRoot)
            {
                // Typed T[], no boxing
                var result = new T[_items.Count];
                var i = 0;
                foreach (var item in _items) result[i++] = item;
                return result;
            }
        }

        public override string ToString() => ValueFormatter.Format(this);
    }
}
