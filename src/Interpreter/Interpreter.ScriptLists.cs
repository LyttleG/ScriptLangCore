namespace ScriptLangCore;

internal partial class Interpreter
{
    private sealed record ScriptBoxedList(TypeInfo ElementType) : ScriptCollectionBase, IScriptList, IEnumerable<object?>
    {
        public IEnumerator<object?> GetEnumerator()
        {
            List<object?> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            List<object?> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }

        internal IEnumerable<object?> GetElements()
        {
            List<object?> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot;
        }

        private readonly List<object?> _items = [];
        private readonly Lock _syncRoot = new();

        public int Count { get { lock (_syncRoot) { return _items.Count; } } }
        public TypeInfo ElementType { get; } = ElementType;

        public override void Add(object? item, int line, int column)
        {
            lock (_syncRoot)
            {
                var coerced = CoerceTo(item, ElementType, line, column);
                _items.Add(coerced);
            }
        }

        public override void AddRaw(object? item)
        {
            lock (_syncRoot)
            {
                _items.Add(item);
            }
        }

        public override void Remove(object? item)
        {
            lock (_syncRoot)
            {
                if (item is ScriptTuple || ElementType.IsTupleType)
                {
                    for (var i = 0; i < _items.Count; i++)
                        if (TupleValueComparer.Instance.Equals(_items[i], item))
                        {
                            _items.RemoveAt(i);
                            return;
                        }
                    return;
                }
                _items.Remove(item);
            }
        }
        public override void RemoveAt(int index) { lock (_syncRoot) { _items.RemoveAt(index); } }

        public object? this[int index]
        {
            get { lock (_syncRoot) { return _items[index]; } }
            set { lock (_syncRoot) { _items[index] = CoerceTo(value, ElementType, -1, -1); } }
        }

        public override Array ToArray()
        {
            lock (_syncRoot)
            {
                // 1. Get a Span pointing directly to the List's internal buffer
                ReadOnlySpan<object?> span = CollectionsMarshal.AsSpan(_items);

                // 2. Allocate the destination array
                var result = new object?[span.Length];

                // 3. Iterate through the span and copy
                for (var i = 0; i < span.Length; i++)
                    result[i] = span[i];

                return result;
            }
        }

        public override void Clear()
        {
            lock (_syncRoot)
            {
                _items.Clear();
                _items.TrimExcess();
            }
        }

        public override string ToString() => ValueFormatter.Format(this);

    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    private sealed record ScriptUnboxedList<T>(TypeInfo ElementType) : ScriptCollectionBase, IScriptList, IEnumerable<T> where T : notnull
    {
        public IEnumerator<T> GetEnumerator()
        {
            List<T> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            List<T> snapshot;
            lock (_syncRoot) { snapshot = [.. _items]; }
            return snapshot.GetEnumerator();
        }

        private readonly List<T> _items = [];
        private readonly Lock _syncRoot = new();

        public int Count { get { lock (_syncRoot) { return _items.Count; } } }
        public TypeInfo ElementType { get; } = ElementType;

        public override void Add(object? item, int line, int column)
        {
            lock (_syncRoot)
            {
                var value = item is T t ? t : (T)CoerceTo(item, ElementType, line, column)!;
                _items.Add(value);
            }
        }

        public override void AddRaw(object? item)
        {
            lock (_syncRoot)
            {
                _items.Add(item is T t ? t : (T)item!);
            }
        }

        public override void Remove(object? item)
        {
            lock (_syncRoot)
            {
                if (item is T t) _items.Remove(t);
            }
        }

        public override void RemoveAt(int index) { lock (_syncRoot) { _items.RemoveAt(index); } }

        public object? this[int index]
        {
            get { lock (_syncRoot) { return _items[index]; } }
            set
            {
                lock (_syncRoot)
                {
                    var coerced = value is T t ? t : (T)CoerceTo(value, ElementType, -1, -1)!;
                    _items[index] = coerced;
                }
            }
        }

        public override Array ToArray()
        {
            lock (_syncRoot)
            {
                // 1. Get a Span pointing directly to the List's internal buffer
                ReadOnlySpan<T> span = CollectionsMarshal.AsSpan(_items);

                // 2. Allocate a typed destination array, no boxing
                var result = new T[span.Length];
                span.CopyTo(result);

                return result;
            }
        }

        public override void Clear()
        {
            lock (_syncRoot)
            {
                _items.Clear();
                _items.TrimExcess();
            }
        }

        public override string ToString() => ValueFormatter.Format(this);

    }
}
