namespace ScriptLangCore;

internal partial class Interpreter
{
    internal class OrderedEnumerable : IEnumerable<object?>, IList<object?>, IReleaseNothing
    {
        private readonly object?[] _items;
        private readonly Func<object?, object?> _keySelector;
        private object?[]? _sorted;
        private object?[]? _keys;
        /// <summary>How two KEYS are compared, when the script gave a comparer (`orderBy(key, comparer)`). Null means
        /// the default order, which is what `orderBy(key)` has always used.</summary>
        private readonly Func<object?, object?, int>? _compareKeys;

        public OrderedEnumerable(IEnumerable<object?> items, Func<object?, object?> keySelector,
            Func<object?, object?, int>? compareKeys = null)
        {
            _items = [.. items];
            _keySelector = keySelector;
            _compareKeys = compareKeys;
        }

        public void Dispose() { } // pure managed data, IDisposable for uniform script using/is

        public override string ToString() => ScriptNameOf(typeof(OrderedEnumerable)) ?? "OrderedEnumerable";

        /// <summary>The ordered sequence as a script LIST, and as a native array. Both go through the ENGINE's own
        /// materializer (`IterableToList`, the one `Iterable.ToList` also calls) rather than building a copy here, so
        /// `orderBy(...).ToList()` and `Iterable.ToList(orderBy(...))` cannot produce different values. Public, and
        /// therefore AOT-rooted (AotHints), because a script reaches them by name: measured before, both reported
        /// "Method 'ToList' not found on type OrderedEnumerable".</summary>
        public object? ToList() => Current?.IterableToList([GetSorted()]);

        /// <summary>...and as a native array. The sorted items ARE an array already, so there is nothing to convert;
        /// they come back as `object[]` rather than a typed `int[]`, which is what an erased element type allows.</summary>
        public object? ToArray() => GetSorted();

        public OrderedEnumerable ThenBy(Func<object?, object?> keySelector)
        {
            // Ensure keys are computed before creating ThenBy
            var sorted = GetSorted();
            var newKeys = new object?[_items.Length];
            for (var i = 0; i < _items.Length; i++)
                newKeys[i] = keySelector(sorted[i]);
            return new OrderedEnumerable(sorted, newKeys);
        }

        private OrderedEnumerable(object?[] items, object?[] keys)
        {
            _items = items;
            _keys = keys;
            _sorted = items;
            _keySelector = null!;
        }

        private object?[] ComputeKeys()
        {
            if (_keys != null) return _keys;
            _keys = new object?[_items.Length];
            for (var i = 0; i < _items.Length; i++)
                _keys[i] = _keySelector(_items[i]);
            return _keys;
        }

        private object?[] GetSorted()
        {
            if (_sorted != null) return _sorted;
            var keys = ComputeKeys();
            var indices = new int[_items.Length];
            for (var i = 0; i < indices.Length; i++) indices[i] = i;
            // A script comparer can FAIL (it runs script code), and Array.Sort WRAPS whatever its comparer throws, so the
            // unwrap has to sit OUTSIDE that call: unwrapping inside the comparer only reaches the wrapper's inner
            // exception, and the report then came out as the runtime's "Failed to compare two elements in the array."
            // instead of the script's own "Division by zero" (measured).
            try
            {
                Array.Sort(indices, (a, b) =>
                {
                    var cmp = _compareKeys != null
                        ? _compareKeys(keys[a], keys[b])
                        : Comparer<object>.Default.Compare(keys[a], keys[b]);
                    return cmp != 0 ? cmp : a.CompareTo(b);
                });
            }
            catch (Exception ex)
            {
                if ((ex as ScriptException ?? ex.InnerException as ScriptException) is { } scriptFailure)
                    throw scriptFailure;
                throw;
            }
            _sorted = new object?[_items.Length];
            for (var i = 0; i < indices.Length; i++)
                _sorted[i] = _items[indices[i]];
            _keys = null; // free key memory, no longer needed after sort
            return _sorted;
        }

        public IEnumerator<object?> GetEnumerator()
            => ((IEnumerable<object?>)GetSorted()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ── IList<object?> (for range/index slicing in scripts) ──
        public object? this[int index]
        {
            get => GetSorted()[index];
            set => throw new NotSupportedException();
        }
        public int Count => GetSorted().Length;
        public bool IsReadOnly => true;
        public int IndexOf(object? item) => Array.IndexOf(GetSorted(), item);
        public void Insert(int index, object? item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public void Add(object? item) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public bool Contains(object? item) => IndexOf(item) >= 0;
        public void CopyTo(object?[] array, int arrayIndex) => GetSorted().CopyTo(array, arrayIndex);
        public bool Remove(object? item) => throw new NotSupportedException();

        public object? First() { var s = GetSorted(); return s.Length > 0 ? s[0] : null; }
        public object? FirstOrDefault() => First();
        public object? Last() { var s = GetSorted(); return s.Length > 0 ? s[^1] : null; }

    }

    /// <summary>Thin wrapper, routes to InvokeScriptFunction which internally uses the fast path when eligible.</summary>
    private static object? FastInvoke(Interpreter it, ScriptFunction fn, params object?[] args)
        => it.InvokeScriptFunction(fn, args, 0, 0);

    private void RegisterLinqFunctions()
    {
        // Where
        // A script can READ a KeyValuePair (a dict iteration hands one over) but could not BUILD one: `new
        // KeyValuePair("a", 1)` answers "No matching constructor found for .NET type 'KeyValuePair'", because the
        // bare name resolves to the CLR GENERIC, whose constructor takes (TKey, TValue) and cannot infer them from
        // the arguments. The engine's own type is constructible, so it is exposed as a bare-call builder, the same
        // shape Rect(l, t, r, b) has: KeyValuePair(key, value).
        RegisterNative("KeyValuePair", TokenType.Object, args =>
        {
            // A wrong arity must refuse BY NAME: measured, `KeyValuePair("only")` otherwise surfaced the raw
            // "IndexOutOfRangeException: Index was outside the bounds of the array", which names nothing.
            if (args.Length != 2) throw new ScriptException("KeyValuePair expects 2 arguments: key and value.");
            // A null key is refused BY NAME rather than handed to the CLR, which would answer in its own words: the
            // pair is a struct a script builds and reads, and a null key only surfaces later, from the INSERT into a
            // dictionary, blaming the wrong call.
            var key = args[0] ?? throw new ScriptException("KeyValuePair needs a key, and the first argument was null.");
            return new ScriptKeyValuePair(key, args[1]);
        });
        RegisterNative("Where", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("Where expects a collection and a predicate function.");
            var collection = args[0];
            var predicate = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");

            var interpreter = Current ?? this;
            var result = new List<object?>();
            var arg = new object?[1];
            foreach (var item in GetItems(collection))
            {
                arg[0] = item;
                var keep = FastInvoke(interpreter, predicate, arg[0]);
                if (IsTruthy(keep)) result.Add(item);
            }
            return result;
        });

        // Select
        RegisterNative("Select", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("Select expects a collection and a selector function.");
            var collection = args[0];
            var selector = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");

            var interpreter = Current ?? this;
            var result = new List<object?>();
            var arg = new object?[1];
            foreach (var item in GetItems(collection))
            {
                arg[0] = item;
                result.Add(FastInvoke(interpreter, selector, arg[0]));
            }
            return result;
        });

        RegisterNative("SelectMany", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("SelectMany expects a collection and a collection selector function.");
            var source = args[0];
            var collectionSelector = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");

            var interpreter = Current ?? this;
            var result = new List<object?>();
            var arg = new object?[1];
            foreach (var outer in GetItems(source))
            {
                arg[0] = outer;
                var inner = FastInvoke(interpreter, collectionSelector, arg[0]);
                foreach (var item in GetItems(inner)) result.Add(item);
            }
            return result;
        });

        // First
        RegisterNative("First", TokenType.Object, args =>
        {
            if (args.Length < 1 || args.Length > 2)
                throw new ScriptException("First expects a collection and an optional predicate.");
            var collection = args[0];
            var predicate = args.Length == 2 ? args[1] as ScriptFunction : null;
            var interpreter = Current ?? this;
            var arg = new object?[1];

            foreach (var item in GetItems(collection))
            {
                var matches = true;
                if (predicate != null)
                {
                    arg[0] = item;
                    var keep = FastInvoke(interpreter, predicate, arg[0]);
                    matches = IsTruthy(keep);
                }
                if (matches)
                    return item;
            }
            throw new ScriptException("No element satisfies the condition.");
        });

        // FirstOrDefault
        RegisterNative("FirstOrDefault", TokenType.Object, args =>
        {
            if (args.Length < 1 || args.Length > 2)
                throw new ScriptException("FirstOrDefault expects a collection and an optional predicate.");
            var collection = args[0];
            var predicate = args.Length == 2 ? args[1] as ScriptFunction : null;
            var interpreter = Current ?? this;
            var arg = new object?[1];

            foreach (var item in GetItems(collection))
            {
                var matches = true;
                if (predicate != null)
                {
                    arg[0] = item;
                    var keep = FastInvoke(interpreter, predicate, arg[0]);
                    matches = IsTruthy(keep);
                }
                if (matches)
                    return item;
            }
            return null;
        });

        // Last
        RegisterNative("Last", TokenType.Object, args =>
        {
            if (args.Length < 1 || args.Length > 2)
                throw new ScriptException("Last expects a collection and an optional predicate.");
            var collection = args[0];
            var predicate = args.Length == 2 ? args[1] as ScriptFunction : null;
            var interpreter = Current ?? this;
            var arg = new object?[1];

            object? lastMatch = null;
            var found = false;
            foreach (var item in GetItems(collection))
            {
                var matches = true;
                if (predicate != null)
                {
                    arg[0] = item;
                    var keep = FastInvoke(interpreter, predicate, arg[0]);
                    matches = IsTruthy(keep);
                }

                if (!matches) continue;
                lastMatch = item;
                found = true;
            }
            if (!found)
                throw new ScriptException("No element satisfies the condition.");
            return lastMatch;
        });

        // LastOrDefault
        RegisterNative("LastOrDefault", TokenType.Object, args =>
        {
            if (args.Length < 1 || args.Length > 2)
                throw new ScriptException("LastOrDefault expects a collection and an optional predicate.");
            var collection = args[0];
            var predicate = args.Length == 2 ? args[1] as ScriptFunction : null;
            var interpreter = Current ?? this;
            var arg = new object?[1];

            object? lastMatch = null;
            foreach (var item in GetItems(collection))
            {
                var matches = true;
                if (predicate != null)
                {
                    arg[0] = item;
                    var keep = FastInvoke(interpreter, predicate, arg[0]);
                    matches = IsTruthy(keep);
                }
                if (matches)
                    lastMatch = item;
            }
            return lastMatch; // returns null if no match
        });

        // Any
        RegisterNative("Any", TokenType.Bool, args =>
        {
            if (args.Length < 1 || args.Length > 2)
                throw new ScriptException("Any expects a collection and an optional predicate.");
            var collection = args[0];
            var predicate = args.Length == 2 ? args[1] as ScriptFunction : null;
            var interpreter = Current ?? this;
            var arg = new object?[1];

            foreach (var item in GetItems(collection))
            {
                if (predicate == null)
                    return true;
                arg[0] = item;
                var keep = FastInvoke(interpreter, predicate, arg[0]);
                if (IsTruthy(keep))
                    return true;
            }
            return false;
        });

        // All
        RegisterNative("All", TokenType.Bool, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("All expects a collection and a predicate.");
            var collection = args[0];
            var predicate = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");
            var interpreter = Current ?? this;
            var arg = new object?[1];
            foreach (var item in GetItems(collection))
            {
                arg[0] = item;
                if (!IsTruthy(FastInvoke(interpreter, predicate, arg[0])))
                    return false;
            }
            return true;
        });

        // ElementAt
        RegisterNative("ElementAt", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("ElementAt expects a collection and an index.");
            var collection = args[0];
            var index = Convert.ToInt32(args[1]);
            if (index < 0)
                throw new ScriptException("Index must be non‑negative.");

            var current = 0;
            foreach (var item in GetItems(collection))
            {
                if (current == index)
                    return item;
                current++;
            }
            throw new ScriptException("Index out of range.");
        });

        // ElementAtOrDefault
        RegisterNative("ElementAtOrDefault", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("ElementAtOrDefault expects a collection and an index.");
            var collection = args[0];
            var index = Convert.ToInt32(args[1]);
            if (index < 0)
                return null;

            var current = 0;
            foreach (var item in GetItems(collection))
            {
                if (current == index)
                    return item;
                current++;
            }
            return null;
        });

        // Count
        RegisterNative("Count", TokenType.Int, args =>
        {
            if (args.Length < 1 || args.Length > 2)
                throw new ScriptException("Count expects a collection and an optional predicate.");
            var collection = args[0];
            var predicate = args.Length == 2 ? args[1] as ScriptFunction : null;
            var interpreter = Current ?? this;
            var count = 0;

            if (predicate == null)
            {
                switch (collection)
                {
                    // Fast paths, identical semantics to enumerating (string.Length == char count, etc.)
                    // but avoids per-item allocation (e.g. char boxing when Count() is called on a string).
                    case string str:
                        return str.Length;

                    case ICollection col:
                        return col.Count;

                    case IScriptCollection sc:
                        return sc.Count;
                }
            }

            foreach (var item in GetItems(collection))
            {
                if (predicate == null)
                    count++;
                else
                {
                    var keep = FastInvoke(interpreter, predicate, item);
                    if (IsTruthy(keep))
                        count++;
                }
            }
            return count;
        });

        // Sum
        RegisterNative("Sum", TokenType.Double, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("Sum expects a collection.");
            var collection = args[0];
            return GetItems(collection).Sum(Convert.ToDouble);
        });

        // Average
        RegisterNative("Average", TokenType.Double, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("Average expects a collection.");
            var collection = args[0];
            double sum = 0;
            var count = 0;
            foreach (var item in GetItems(collection))
            {
                sum += Convert.ToDouble(item);
                count++;
            }
            return count == 0 ? 0 : sum / count;
        });

        // Min
        RegisterNative("Min", TokenType.Object, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("Min expects a collection.");
            object? min = null;
            var first = true;
            foreach (var item in GetItems(args[0]))
            {
                if (!first && Comparer<object>.Default.Compare(item, min) >= 0) continue;
                min = item;
                first = false;
            }
            return min;
        });

        // Max
        RegisterNative("Max", TokenType.Object, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("Max expects a collection.");
            object? min = null;
            var first = true;
            foreach (var item in GetItems(args[0]))
            {
                if (!first && Comparer<object>.Default.Compare(item, min) <= 0) continue;
                min = item;
                first = false;
            }
            return min;
        });

        // Take
        RegisterNative("Take", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("Take expects a collection and a count.");
            var collection = args[0];
            var takeCount = Convert.ToInt32(args[1]);

            var result = new List<object?>();
            var taken = 0;
            foreach (var item in GetItems(collection))
            {
                if (taken >= takeCount) break;
                result.Add(item);
                taken++;
            }
            return result;
        });

        // Skip
        RegisterNative("Skip", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("Skip expects a collection and a count.");
            var collection = args[0];
            var skipCount = Convert.ToInt32(args[1]);

            var index = 0;
            return GetItems(collection).Where(item => index++ >= skipCount).ToList();
        });

        // The comparer a script passes to an ordering, as a key comparison, or a refusal naming what was wanted. The
        // comparer receives two KEYS (C#'s meaning for that overload) and its answer is coerced to an int, the same
        // negative/zero/positive contract `list.Sort(comparer)` has.
        Func<object?, object?, int> ScriptKeyComparer(object? value, int line, int column)
        {
            if (value is not ScriptFunction fn)
                throw new ScriptException("The comparer must be a function that compares two keys.");
            var interpreter = Current ?? this;
            return (a, b) => Convert.ToInt32(interpreter.InvokeCallableValue(fn, [a, b], line, column));
        }

        // OrderBy
        RegisterNative("OrderBy", TokenType.Object, args =>
        {
            if (args.Length != 2 && args.Length != 3)
                throw new ScriptException("OrderBy expects a collection and a key selector, and optionally a comparer.");
            var collection = args[0];
            var keySelector = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");
            var interpreter = Current ?? this;
            var items = GetItems(collection).ToList();
            // `orderBy(key, comparer)`: the comparer receives two KEYS (not two items), which is what C#'s overload
            // means and what makes `orderBy(x => x.Name, compareNames)` read the same here.
            var compareKeys = args.Length == 3 ? ScriptKeyComparer(args[2], 0, 0) : null;
            return new OrderedEnumerable(items, item => FastInvoke(interpreter, keySelector, item), compareKeys);
        });

        // OrderByDescending
        RegisterNative("OrderByDescending", TokenType.Object, args =>
        {
            if (args.Length != 2 && args.Length != 3)
                throw new ScriptException("OrderByDescending expects a collection and a key selector, and optionally a comparer.");
            var collection = args[0];
            var keySelector = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");
            var interpreter = Current ?? this;
            var items = GetItems(collection).ToList();
            // An OrderedEnumerable with the comparison REVERSED, exactly as OrderBy builds one: the descending order is
            // then lazy in the same way and answers the same members (ToList, ToArray, ThenBy, Count). It used to return
            // a materialized CLR list, which a script could not materialize again: measured, `orderByDescending(f).ToList()`
            // reported "Method 'ToList' not found on type List<object>".
            var explicitCompare = args.Length == 3 ? ScriptKeyComparer(args[2], 0, 0) : null;
            Func<object?, object?, int> descending = explicitCompare != null
                ? (a, b) => explicitCompare(b, a)
                : (a, b) => Comparer<object>.Default.Compare(b, a);
            return new OrderedEnumerable(items, item => FastInvoke(interpreter, keySelector, item), descending);
        });

        // ThenBy
        RegisterNative("ThenBy", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("ThenBy expects an ordered sequence and a key selector.");
            var ordered = args[0] as OrderedEnumerable
                ?? throw new ScriptException("First argument must be the result of OrderBy.");
            var keySelector = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");
            var interpreter = Current ?? this;

            return ordered.ThenBy(item => FastInvoke(interpreter, keySelector, item));
        });

        // Distinct
        RegisterNative("Distinct", TokenType.Object, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("Distinct expects a collection.");
            var collection = args[0];

            return GetItems(collection).Distinct().ToList();
        });

        // GroupBy
        RegisterNative("GroupBy", TokenType.Object, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("GroupBy expects a collection and a key selector.");
            var collection = args[0];
            var keySelector = args[1] as ScriptFunction
                ?? throw new ScriptException("Second argument must be a function.");

            var interpreter = Current ?? this;

            var dict = new Dictionary<object, List<object?>>(100);
            var onearg = new object?[1];

            foreach (var item in GetItems(collection))
            {
                onearg[0] = item;
                var key = FastInvoke(interpreter, keySelector, onearg[0])!;

                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, key, out var exists);
                if (!exists)
                    list = [with(1)];
                list!.Add(item);
            }

            var dictType = new TypeInfo(TokenType.Object, false, 0, 0, null, null);
            return dict.Select(kv
                => new ScriptBoxedDictionary(dictType, dictType) { ["Key"] = kv.Key, ["Items"] = kv.Value })
                .Cast<object?>().ToList();
        });

        // Join
        RegisterNative("Join", TokenType.Object, args =>
        {
            if (args.Length != 5)
                throw new ScriptException("Join expects 5 arguments: outer, inner, outerKeySelector, innerKeySelector, resultSelector.");

            var outer = args[0];
            var inner = args[1];
            var outerKeySel = args[2] as ScriptFunction ?? throw new ScriptException("outerKeySelector (arg 3) must be a function.");
            var innerKeySel = args[3] as ScriptFunction ?? throw new ScriptException("innerKeySelector (arg 4) must be a function.");
            var resultSel = args[4] as ScriptFunction ?? throw new ScriptException("resultSelector (arg 5) must be a function.");

            var interpreter = Current ?? this;

            // Materialize inner and outer to avoid multiple enumerations
            var outerList = GetItems(outer).ToList();
            var innerList = GetItems(inner).ToList();

            // Build lookup
            var lookup = new Dictionary<object, List<object?>>();
            var oneArg = new object?[1];
            foreach (var innerItem in innerList)
            {
                var key = FastInvoke(interpreter, innerKeySel, innerItem)!;
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, key, out var exists);
                if (!exists) list = [with(1)];
                list!.Add(innerItem);
            }

            // Collect results
            var results = new List<object?>();
            var outerBuffer = new object?[1];
            var resultBuffer = new object?[2];
            foreach (var outerItem in outerList)
            {
                var key = FastInvoke(interpreter, outerKeySel, outerItem)!;
                if (!lookup.TryGetValue(key, out var innerMatches))
                    continue;

                resultBuffer[0] = outerItem;
                foreach (var innerItem in innerMatches)
                {
                    resultBuffer[1] = innerItem;
                    results.Add(FastInvoke(interpreter, resultSel, outerItem, innerItem));
                }
            }
            return results;
        });

        // GroupJoin
        RegisterNative("GroupJoin", TokenType.Object, args =>
        {
            if (args.Length != 5)
                throw new ScriptException("GroupJoin expects outer, inner, outerKeySelector, innerKeySelector, resultSelector.");

            var outer = args[0];
            var inner = args[1];
            var outerKeySel = args[2] as ScriptFunction ?? throw new ScriptException("Third argument must be a function.");
            var innerKeySel = args[3] as ScriptFunction ?? throw new ScriptException("Fourth argument must be a function.");
            var resultSel = args[4] as ScriptFunction ?? throw new ScriptException("Fifth argument must be a function.");
            var interpreter = Current ?? this;

            // Build lookup on inner
            var lookup = new Dictionary<object, List<object?>>();
            var oneArg = new object?[1];
            foreach (var innerItem in GetItems(inner))
            {
                var key = FastInvoke(interpreter, innerKeySel, innerItem)!;
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, key, out var exists);
                if (!exists) list = [with(1)];
                list!.Add(innerItem);
            }

            // Outer iteration, grouping matching inner items
            var gjResult = new List<object?>();
            var outerBuffer = new object?[1];
            var twoArg = new object?[2];
            foreach (var outerItem in GetItems(outer))
            {
                var key = FastInvoke(interpreter, outerKeySel, outerItem)!;
                var innerGroup = lookup.TryGetValue(key, out var group) ? group : [];
                twoArg[0] = outerItem;
                twoArg[1] = innerGroup;
                gjResult.Add(FastInvoke(interpreter, resultSel, twoArg[0], twoArg[1]));
            }
            return gjResult;
        });

        // LeftJoin
        RegisterNative("LeftJoin", TokenType.Object, args =>
        {
            if (args.Length != 5)
                throw new ScriptException("LeftJoin expects outer, inner, outerKeySelector, innerKeySelector, resultSelector.");
            var outer = args[0];
            var inner = args[1];
            var outerKeySel = args[2] as ScriptFunction ?? throw new ScriptException("Third argument must be a function.");
            var innerKeySel = args[3] as ScriptFunction ?? throw new ScriptException("Fourth argument must be a function.");
            var resultSel = args[4] as ScriptFunction ?? throw new ScriptException("Fifth argument must be a function.");
            var interpreter = Current ?? this;

            // Build lookup from inner sequence (right side)
            var lookup = new Dictionary<object, List<object?>>();
            var oneArg = new object?[1];
            foreach (var innerItem in GetItems(inner))
            {
                var key = FastInvoke(interpreter, innerKeySel, innerItem)!;
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, key, out var exists);
                if (!exists) list = [with(1)];
                list!.Add(innerItem);
            }

            // Iterate over outer (left) sequence
            var ljResult = new List<object?>();
            var outerBuffer = new object?[1];
            var twoArg = new object?[2];
            foreach (var outerItem in GetItems(outer))
            {
                var key = FastInvoke(interpreter, outerKeySel, outerItem)!;
                if (lookup.TryGetValue(key, out var innerMatches))
                {
                    foreach (var innerItem in innerMatches)
                    {
                        twoArg[0] = outerItem;
                        twoArg[1] = innerItem;
                        ljResult.Add(interpreter.InvokeScriptFunction(resultSel, twoArg, 0, 0));
                    }
                }
                else
                {
                    twoArg[0] = outerItem;
                    twoArg[1] = null;
                    ljResult.Add(interpreter.InvokeScriptFunction(resultSel, twoArg, 0, 0));
                }
            }
            return ljResult;
        });

        // RightJoin
        RegisterNative("RightJoin", TokenType.Object, args =>
        {
            if (args.Length != 5)
                throw new ScriptException("RightJoin expects outer, inner, outerKeySelector, innerKeySelector, resultSelector.");

            var outer = args[0];
            var inner = args[1];
            var outerKeySel = args[2] as ScriptFunction ?? throw new ScriptException("Third argument must be a function.");
            var innerKeySel = args[3] as ScriptFunction ?? throw new ScriptException("Fourth argument must be a function.");
            var resultSel = args[4] as ScriptFunction ?? throw new ScriptException("Fifth argument must be a function.");
            var interpreter = Current ?? this;

            // Build lookup from outer sequence (keys -> list of outer items)
            var lookup = new Dictionary<object, List<object?>>();
            var oneArg = new object?[1];
            foreach (var outerItem in GetItems(outer))
            {
                var key = FastInvoke(interpreter, outerKeySel, outerItem)!;
                ref var list = ref CollectionsMarshal.GetValueRefOrAddDefault(lookup, key, out var exists);
                if (!exists) list = [with(1)];
                list!.Add(outerItem);
            }

            // Iterate over inner (right) sequence
            var rjResult = new List<object?>();
            var innerBuffer = new object?[1];
            var twoArg = new object?[2];
            foreach (var innerItem in GetItems(inner))
            {
                var key = FastInvoke(interpreter, innerKeySel, innerItem)!;
                if (lookup.TryGetValue(key, out var outerMatches))
                {
                    foreach (var outerItem in outerMatches)
                    {
                        twoArg[0] = outerItem;
                        twoArg[1] = innerItem;
                        rjResult.Add(interpreter.InvokeScriptFunction(resultSel, twoArg, 0, 0));
                    }
                }
                else
                {
                    // No matching outer element - pass null for outer
                    twoArg[0] = null;
                    twoArg[1] = innerItem;
                    rjResult.Add(interpreter.InvokeScriptFunction(resultSel, twoArg, 0, 0));
                }
            }
            return rjResult;
        });

        // Aggregate
        RegisterNative("Aggregate", TokenType.Object, args =>
        {
            if (args.Length != 3)
                throw new ScriptException("Aggregate expects a collection, a seed, and an accumulator function.");

            var collection = args[0];
            var seed = args[1];
            var func = args[2] as ScriptFunction ?? throw new ScriptException("Third argument must be a function.");
            var interpreter = Current ?? this;

            var aggResult = seed;
            foreach (var item in GetItems(collection))
                aggResult = FastInvoke(interpreter, func, aggResult, item);
            return aggResult;
        });

        // Reverse
        RegisterNative("Reverse", TokenType.Object, args =>
        {
            if (args.Length != 1)
                throw new ScriptException("Reverse expects a collection.");

            var collection = args[0];

            var buffer = GetItems(collection).ToList();
            var revResult = new List<object?>(buffer.Count);
            for (var i = buffer.Count - 1; i >= 0; i--)
                revResult.Add(buffer[i]);
            return revResult;
        });

        // Contains
        RegisterNative("Contains", TokenType.Bool, args =>
        {
            if (args.Length != 2)
                throw new ScriptException("Contains expects a collection and a value.");

            var collection = args[0];
            var value = args[1];
            var items = GetItems(collection);
            // Tuples: membership uses value equality (distinct equal tuples match). The
            // comparer handles only tuples; non-tuple collections keep native semantics.
            return value is ScriptTuple
                || (collection is IScriptList { ElementType.IsTupleType: true })
                ? items.Any(i => TupleValueComparer.Instance.Equals(i, value))
                : items.Contains(value);
        });

        // Zip
        RegisterNative("Zip", TokenType.Object, args =>
        {
            if (args.Length != 3)
                throw new ScriptException("Zip expects first, second, and result selector.");

            var first = args[0];
            var second = args[1];
            var resultSelector = args[2] as ScriptFunction
                ?? throw new ScriptException("Third argument must be a function.");

            var interpreter = Current ?? this;

            var zipResult = new List<object?>();
            var e1 = GetItems(first).GetEnumerator();
            using IDisposable e3 = e1;
            var e2 = GetItems(second).GetEnumerator();
            using IDisposable e4 = e2;
            while (e1.MoveNext() && e2.MoveNext())
            {
                zipResult.Add(FastInvoke(interpreter, resultSelector, e1.Current, e2.Current));
            }
            return zipResult;
        });
    }

    private static IEnumerable<object?> GetItems(object? collection)
    {
        switch (collection)
        {
            case IScriptList list:
                {
                    for (var i = 0; i < list.Count; i++)
                        yield return list[i];
                    break;
                }

            case object?[] objArr:
                {
                    foreach (var t in objArr)
                        yield return t;

                    break;
                }

            case Array arr:
                {
                    for (var i = 0; i < arr.Length; i++)
                        yield return arr.GetValue(i);
                    break;
                }

            case IScriptDictionary dict:
                {
                    var enumerator = dict.Dictionary.GetEnumerator();
                    using var enumerator1 = enumerator as IDisposable;
                    while (enumerator.MoveNext())
                        yield return new ScriptKeyValuePair(enumerator.Entry.Key, enumerator.Entry.Value);
                    break;
                }

            case IEnumerable enumerable:
                {
                    foreach (var item in enumerable)
                        yield return item;
                    break;
                }

            default:
                throw new ScriptException("Collection type not supported.");
        }
    }

    internal readonly struct ScriptKeyValuePair(object key, object? value) : IDisposable
    {
        public object Key { get; } = key;
        public object? Value { get; } = value;
        public void Dispose() { } // pure data, IDisposable for uniform script using/is
        public override string ToString() => ScriptNameOf(typeof(ScriptKeyValuePair)) ?? "KeyValuePair";
    }
}