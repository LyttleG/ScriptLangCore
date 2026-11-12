namespace ScriptLangCore;

internal partial class Interpreter
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    internal sealed record ScriptBoxedDictionary : IScriptDictionary
    {
        internal IEnumerable<KeyValuePair<object, object?>> GetEntries()
        {
            List<KeyValuePair<object, object?>> snapshot;
            lock (_syncRoot) { snapshot = [.. _dict]; }
            return snapshot;
        }

        private readonly Dictionary<object, object?> _dict;
        private readonly Lock _syncRoot = new();
        /// <summary>Optional column-name order for ordinal access (row[0] → row["id"]).</summary>
        internal string[]? ColOrder;

        public IDictionary Dictionary { get { lock (_syncRoot) { return _dict; } } }

        public int Count { get { lock (_syncRoot) { return _dict.Count; } } }
        public TypeInfo KeyType { get; }
        public TypeInfo ValueType { get; }

        public object GetKey(int index)
        {
            lock (_syncRoot)
            {
                if (index < 0 || index >= _dict.Count)
                    throw new ScriptException("Index out of range.");
                if (ColOrder != null && index < ColOrder.Length)
                    return ColOrder[index];
                return _dict.Keys.ElementAt(index);
            }
        }
        public object? GetValue(int index)
        {
            lock (_syncRoot)
            {
                if (index < 0 || index >= _dict.Count)
                    throw new ScriptException("Index out of range.");
                if (ColOrder != null && index < ColOrder.Length)
                    return _dict[ColOrder[index]];
                return _dict.Values.ElementAt(index);
            }
        }

        // Primary constructor
        public ScriptBoxedDictionary(TypeInfo keyType, TypeInfo valueType, int capacity = 0)
                : this(keyType, valueType, capacity > 0
                    ? new Dictionary<object, object?>(capacity, TupleValueComparer.Instance)
                    : new Dictionary<object, object?>(TupleValueComparer.Instance))
        { }

        private ScriptBoxedDictionary(TypeInfo keyType, TypeInfo valueType, Dictionary<object, object?> dict)
        {
            KeyType = keyType;
            ValueType = valueType;
            _dict = dict;
        }

        /// <summary>
        /// Zero-copy wrap. Caller owns the source dict and must ensure type compatibility.
        /// </summary>
        public static ScriptBoxedDictionary FromExisting(
            Dictionary<object, object?> source,
            TypeInfo keyType,
            TypeInfo valueType)
            => new(keyType, valueType, source);

        public void Add(object? key, object? value, int line, int column)
        {
            lock (_syncRoot)
            {
                var coercedKey = CoerceTo(key, KeyType, line, column);
                var coercedValue = CoerceTo(value, ValueType, line, column);
                _dict.Add(coercedKey!, coercedValue);
            }
        }

        public void AddRaw(object key, object? value)
        {
            lock (_syncRoot)
            {
                _dict[key] = value;
            }
        }

        public void Remove(object key) { lock (_syncRoot) { _dict.Remove(key); } }
        public bool ContainsKey(object key) { lock (_syncRoot) { return _dict.ContainsKey(key); } }
        public object[] Keys()
        {
            lock (_syncRoot)
            {
                var keys = _dict.Keys;
                var result = new object[keys.Count];
                keys.CopyTo(result, 0);
                return result;
            }
        }
        public object?[] Values()
        {
            lock (_syncRoot)
            {
                var values = _dict.Values;
                var result = new object?[values.Count];
                values.CopyTo(result, 0);
                return result;
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _dict.Clear();
                _dict.TrimExcess();
            }
        }

        public object? this[object key]
        {
            get
            {
                lock (_syncRoot)
                {
                    // Ordinal access: row[0] → row[_colOrder[0]] → row["id"]
                    if (ColOrder != null)
                    {
                        int idx;
                        if (key is int i) idx = i;
                        else if (key is long l) idx = (int)l;
                        else idx = -1;
                        if (idx >= 0 && idx < ColOrder.Length)
                            return _dict[ColOrder[idx]];
                    }
                    return _dict[key];
                }
            }
            set { lock (_syncRoot) { _dict[key] = CoerceTo(value, ValueType, -1, -1); } }
        }

        public override string ToString() => ValueFormatter.Format(this);
    }


    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    internal sealed record ScriptUnboxedDictionary<TKey, TValue>(TypeInfo TkeyType, TypeInfo TvalueType) : IScriptDictionary
        where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue?> _dict = [];
        private readonly Lock _syncRoot = new();

        public ScriptUnboxedDictionary(TypeInfo TkeyType, TypeInfo TvalueType, int capacity = 0)
            : this(TkeyType, TvalueType)
        {
            if (capacity > 0) _dict = new Dictionary<TKey, TValue?>(capacity);
        }

        public IDictionary Dictionary { get { lock (_syncRoot) { return _dict; } } }

        public int Count { get { lock (_syncRoot) { return _dict.Count; } } }

        public TypeInfo KeyType => TkeyType;
        public TypeInfo ValueType => TvalueType;

        public object GetKey(int index)
        {
            lock (_syncRoot)
            {
                if (index < 0 || index >= _dict.Count)
                    throw new ScriptException("Index out of range.");
                return _dict.Keys.ElementAt(index);
            }
        }

        public object? GetValue(int index)
        {
            lock (_syncRoot)
            {
                if (index < 0 || index >= _dict.Count)
                    throw new ScriptException("Index out of range.");
                return _dict.Values.ElementAt(index);
            }
        }

        public void Add(object? key, object? value, int line, int column)
        {
            lock (_syncRoot)
            {
                var coercedKey = key is TKey k ? k : (TKey)CoerceTo(key, TkeyType, line, column)!;
                var coercedValue = value is TValue v ? v : (TValue)CoerceTo(value, TvalueType, line, column)!;
                _dict.Add(coercedKey, coercedValue);
            }
        }

        public void AddRaw(object key, object? value)
        {
            lock (_syncRoot)
            {
                _dict.Add((TKey)key, (TValue?)value);
            }
        }

        public void Remove(object key)
        {
            lock (_syncRoot)
            {
                var k = key is TKey tk ? tk : (TKey)CoerceTo(key, TkeyType, -1, -1)!;
                _dict.Remove(k);
            }
        }

        public bool ContainsKey(object key)
        {
            lock (_syncRoot)
            {
                var k = key is TKey tk ? tk : (TKey)CoerceTo(key, TkeyType, -1, -1)!;
                return _dict.ContainsKey(k);
            }
        }

        public object[] Keys()
        {
            lock (_syncRoot)
            {
                var keys = _dict.Keys;
                var result = new object[keys.Count];
                var i = 0;
                foreach (var k in keys) result[i++] = k;
                return result;
            }
        }

        public object?[] Values()
        {
            lock (_syncRoot)
            {
                var result = new object?[_dict.Count];
                var i = 0;
                foreach (var v in _dict.Values) result[i++] = v;
                return result;
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _dict.Clear();
                _dict.TrimExcess();
            }
        }

        public object? this[object key]
        {
            get
            {
                lock (_syncRoot)
                {
                    var k = key is TKey tk ? tk : (TKey)CoerceTo(key, TkeyType, -1, -1)!;
                    return _dict.TryGetValue(k, out var value)
                        ? value : throw new ScriptException($"Key '{key}' not found in dictionary.");
                }
            }
            set
            {
                lock (_syncRoot)
                {
                    var k = key is TKey tk ? tk : (TKey)CoerceTo(key, TkeyType, -1, -1)!;
                    var v = value is TValue tv ? tv : (TValue)CoerceTo(value, TvalueType, -1, -1)!;
                    _dict[k] = v;
                }
            }
        }

        public override string ToString() => ValueFormatter.Format(this);
    }
}
