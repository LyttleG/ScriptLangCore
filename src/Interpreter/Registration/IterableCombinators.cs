namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>Cache-once wrapper: the first enumeration consumes the source and caches
    /// elements; later enumerations replay the cache. Makes one-shot Iterables (generators,
    /// LINQ results) re-enumerable. IDisposable so `using (var r = Replayable(...))` (and
    /// explicit r.Dispose()) deterministically releases the cache, a disposed Replayable
    /// enumerates as empty.</summary>
    internal sealed class ReplayableEnumerable : IEnumerable<object?>, IReleaseNothing
    {
        private object? _source;
        private List<object?>? _cache = [];
        private bool _consumed;
        private bool _disposed;

        internal ReplayableEnumerable(object? source) { _source = source; }

        /// <summary>Deterministic release: marks the wrapper disposed, drops the cached elements
        /// and the source iterator. Enumerating after Dispose yields nothing (empty sequence).
        /// Idempotent, repeated Dispose is a safe no-op.</summary>
        public void Dispose()
        {
            _disposed = true;
            _cache = null;
            _source = null;
        }

        public IEnumerator<object?> GetEnumerator()
        {
            if (_disposed || _cache == null)
                yield break;
            if (_consumed)
            {
                foreach (var e in _cache) yield return e;
                yield break;
            }
            if (TryGetIterableElements(_source, out var elements))
            {
                foreach (var e in elements)
                {
                    _cache.Add(e);
                    yield return e;
                }
            }
            else
            {
                // One-shot non-IEnumerable sources (coroutine generators), resume to materialize,
                // mirroring the LINQ wrapper's generator semantics (WrapIterableForLinq/MaterializeCoroutine).
                var seq = _source switch
                {
                    Generator<object?> gen => MaterializeCoroutine(gen.InnerCoroutine),
                    Coroutine co => MaterializeCoroutine(co),
                    _ => Array.Empty<object?>()
                };
                foreach (var e in seq)
                {
                    _cache.Add(e);
                    yield return e;
                }
            }
            _consumed = true;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => ScriptNameOf(typeof(ReplayableEnumerable)) ?? "ReplayableEnumerable";
    }


    /// <summary>A lazily-produced, SINGLE-PASS sequence, what the Iterable combinators return.
    ///
    /// It exists so that typeof has a NAME to report. Those combinators used to hand their iterator back
    /// directly, and a C# iterator's type is compiler-generated per method, so a script holding one saw
    /// <c>typeof(x)</c> == "&lt;IterableMapImpl&gt;d__564", a name no one can write and nothing resolves.
    /// This is the one named type behind all five (Map/Filter/Zip/Append/Range).
    ///
    /// Deliberately WITHOUT Replayable's caching: these combinators are documented lazy, and a cache would
    /// make them eager in memory terms. Registered in ScriptTypeRegistry, which is what makes
    /// <c>x is LazyEnumerable</c> work, the is/as default arm reads that table.</summary>
    internal sealed class LazyEnumerable(IEnumerable<object?> source) : IEnumerable<object?>
    {
        public IEnumerator<object?> GetEnumerator() => source.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => ScriptNameOf(typeof(LazyEnumerable)) ?? "LazyEnumerable";
    }

    /// <summary>Registers the iterable combinator stdlib: Replayable/Memoize cache-once natives,
    /// the Tuple namespace (Map/Fold, tuple-preserving), and the Iterable namespace
    /// (Map/Filter/Fold/Zip/Append/Concat/Range, lazy except Fold). Mirrors the Json/Coroutine
    /// namespace pattern: Namespace object bound into the scope; casts/`is` resolve via the
    /// hard-coded type-name path, so the same-named type predicates stay unaffected.</summary>
    private void RegisterIterableCombinators()
    {
        // ── Replayable / Memoize, cache-once re-enumerable wrappers ─────────────
        RegisterNative("Replayable", TokenType.Object, args =>
            args.Length != 1 ? RuntimeError<object?>("Replayable expects 1 argument.", 0, 0)
            : !IsIterable(args[0]) ? RuntimeError<object?>($"Expected an Iterable, got {args[0]?.GetType().Name ?? "null"}.", 0, 0)
            : new ReplayableEnumerable(args[0]));
        RegisterNative("Memoize", TokenType.Object, args =>
            args.Length != 1 ? RuntimeError<object?>("Memoize expects 1 argument.", 0, 0)
            : !IsIterable(args[0]) ? RuntimeError<object?>($"Expected an Iterable, got {args[0]?.GetType().Name ?? "null"}.", 0, 0)
            : new ReplayableEnumerable(args[0]));

        // ── Tuple namespace, Map / Fold (same-arity result, element names preserved) ──
        var tupleNs = new Namespace(_comparer) { Name = "Tuple" };
        tupleNs.AddMethod("Map", TupleMap);
        tupleNs.AddMethod("Fold", TupleFold);
        CurrentFrame.Scopes.Define("Tuple", tupleNs);

        // ── Iterable namespace, Map/Filter/Fold/Zip/Append/Concat/Range ─────────
        var iterableNs = new Namespace(_comparer) { Name = "Iterable" };
        iterableNs.AddMethod("Map", IterableMap);
        iterableNs.AddMethod("Filter", IterableFilter);
        iterableNs.AddMethod("Fold", IterableFold);
        iterableNs.AddMethod("Zip", IterableZip);
        iterableNs.AddMethod("Append", IterableAppend);
        iterableNs.AddMethod("Concat", IterableAppend);   // alias
        iterableNs.AddMethod("Range", IterableRange);
        iterableNs.AddMethod("ToList", IterableToList);
        CurrentFrame.Scopes.Define("Iterable", iterableNs);
    }

    // ── Tuple.Map, map every element, keep arity + element names ───────────────
    private object? TupleMap(object?[] args)
    {
        if (args.Length != 2)
            return RuntimeError<object?>("Tuple.Map expects a tuple and a function.", 0, 0);
        if (args[0] is not ScriptTuple src)
            return RuntimeError<object?>($"Expected a Tuple, got {args[0]?.GetType().Name ?? "null"}.", 0, 0);
        if (args[1] is not ScriptFunction fn)
            return RuntimeError<object?>("Second argument must be a function.", 0, 0);
        var itp = Current ?? this;
        var mapped = new object?[src.Length];
        for (var i = 0; i < src.Length; i++)
            mapped[i] = itp.InvokeScriptFunction(fn, [src[i]], 0, 0);
        return new ScriptTuple(mapped) { Names = src.Names };
    }

    // ── Tuple.Fold, eager left fold over the tuple elements ───────────────────
    private object? TupleFold(object?[] args)
    {
        if (args.Length != 3)
            return RuntimeError<object?>("Tuple.Fold expects a tuple, an initial value, and a function.", 0, 0);
        if (args[0] is not ScriptTuple src)
            return RuntimeError<object?>($"Expected a Tuple, got {args[0]?.GetType().Name ?? "null"}.", 0, 0);
        if (args[2] is not ScriptFunction fn)
            return RuntimeError<object?>("Third argument must be a function.", 0, 0);
        var itp = Current ?? this;
        var acc = args[1];
        for (var i = 0; i < src.Length; i++)
            acc = itp.InvokeScriptFunction(fn, [acc, src[i]], 0, 0);
        return acc;
    }

    // ── Iterable.Map, lazy map over any Iterable ──────────────────────────────
    private object? IterableMap(object?[] args)
    {
        if (args.Length != 2)
            return RuntimeError<object?>("Iterable.Map expects an Iterable and a function.", 0, 0);
        if (!IsIterable(args[0]))
            return RuntimeError<object?>($"Expected an Iterable, got {args[0]?.GetType().Name ?? "null"}.", 0, 0);
        if (args[1] is not ScriptFunction fn)
            return RuntimeError<object?>("Second argument must be a function.", 0, 0);
        var itp = Current ?? this;
        return new LazyEnumerable(IterableMapImpl(itp, args[0], fn));
    }

    private static IEnumerable<object?> IterableMapImpl(Interpreter itp, object? src, ScriptFunction fn)
    {
        if (!Interpreter.TryGetIterableElements(src, out var elements))
            yield break;
        var arg = new object?[1];
        foreach (var e in elements)
        {
            arg[0] = e;
            yield return itp.InvokeScriptFunction(fn, arg, 0, 0);
        }
    }

    // ── Iterable.Filter, lazy filter over any Iterable ────────────────────────
    private object? IterableFilter(object?[] args)
    {
        if (args.Length != 2)
            return RuntimeError<object?>("Iterable.Filter expects an Iterable and a predicate function.", 0, 0);
        if (!IsIterable(args[0]))
            return RuntimeError<object?>($"Expected an Iterable, got {args[0]?.GetType().Name ?? "null"}.", 0, 0);
        if (args[1] is not ScriptFunction fn)
            return RuntimeError<object?>("Second argument must be a function.", 0, 0);
        var itp = Current ?? this;
        return new LazyEnumerable(IterableFilterImpl(itp, args[0], fn));
    }

    private static IEnumerable<object?> IterableFilterImpl(Interpreter itp, object? src, ScriptFunction fn)
    {
        if (!Interpreter.TryGetIterableElements(src, out var elements))
            yield break;
        var arg = new object?[1];
        foreach (var e in elements)
        {
            arg[0] = e;
            if (IsTruthy(itp.InvokeScriptFunction(fn, arg, 0, 0)))
                yield return e;
        }
    }

    // ── Iterable.Fold, eager left fold over any Iterable ──────────────────────
    private object? IterableFold(object?[] args)
    {
        if (args.Length != 3)
            return RuntimeError<object?>("Iterable.Fold expects an Iterable, an initial value, and a function.", 0, 0);
        if (!IsIterable(args[0]))
            return RuntimeError<object?>($"Expected an Iterable, got {args[0]?.GetType().Name ?? "null"}.", 0, 0);
        if (args[2] is not ScriptFunction fn)
            return RuntimeError<object?>("Third argument must be a function.", 0, 0);
        var itp = Current ?? this;
        if (!TryGetIterableElements(args[0], out var elements))
            return args[1];
        var acc = args[1];
        foreach (var e in elements)
            acc = itp.InvokeScriptFunction(fn, [acc, e], 0, 0);
        return acc;
    }

    // ── Iterable.Zip, lazy pair-wise map over two Iterables (shortest wins) ───
    private object? IterableZip(object?[] args)
    {
        if (args.Length != 3)
            return RuntimeError<object?>("Iterable.Zip expects two Iterables and a function.", 0, 0);
        if (!IsIterable(args[0]) || !IsIterable(args[1]))
            return RuntimeError<object?>($"Expected Iterables, got {args[0]?.GetType().Name ?? "null"} and {args[1]?.GetType().Name ?? "null"}.", 0, 0);
        if (args[2] is not ScriptFunction fn)
            return RuntimeError<object?>("Third argument must be a function.", 0, 0);
        var itp = Current ?? this;
        return new LazyEnumerable(IterableZipImpl(itp, args[0], args[1], fn));
    }

    private static IEnumerable<object?> IterableZipImpl(Interpreter itp, object? left, object? right, ScriptFunction fn)
    {
        if (!Interpreter.TryGetIterableElements(left, out var l) || !Interpreter.TryGetIterableElements(right, out var r))
            yield break;
        using var le = l.GetEnumerator();
        using var re = r.GetEnumerator();
        while (le.MoveNext() && re.MoveNext())
            yield return itp.InvokeScriptFunction(fn, [le.Current, re.Current], 0, 0);
    }

    // ── Iterable.Append / Concat, lazy concatenation of two Iterables ─────────
    private object? IterableAppend(object?[] args)
    {
        if (args.Length != 2)
            return RuntimeError<object?>("Iterable.Append expects two Iterables.", 0, 0);
        if (!IsIterable(args[0]) || !IsIterable(args[1]))
            return RuntimeError<object?>($"Expected Iterables, got {args[0]?.GetType().Name ?? "null"} and {args[1]?.GetType().Name ?? "null"}.", 0, 0);
        return new LazyEnumerable(IterableAppendImpl(args[0], args[1]));
    }

    private static IEnumerable<object?> IterableAppendImpl(object? first, object? second)
    {
        if (Interpreter.TryGetIterableElements(first, out var f))
            foreach (var e in f) yield return e;
        if (Interpreter.TryGetIterableElements(second, out var s))
            foreach (var e in s) yield return e;
    }

    // ── Iterable.Range, lazy arithmetic sequence [start, start+count) ─────────
    private object? IterableRange(object?[] args)
    {
        if (args.Length != 2)
            return RuntimeError<object?>("Iterable.Range expects a start and a count.", 0, 0);
        var start = Convert.ToInt64(args[0]);
        var count = Convert.ToInt64(args[1]);
        if (count < 0)
            return RuntimeError<object?>("Iterable.Range count must be non-negative.", 0, 0);
        return new LazyEnumerable(IterableRangeImpl(start, count));
    }

    private static IEnumerable<object?> IterableRangeImpl(long start, long count)
    {
        for (var i = 0L; i < count; i++)
            yield return start + i;
    }

    // ── Iterable.ToList, eager typed copy of any Iterable into a NEW List<T> ──
    // Uniform surface for the delegating .ToList() methods (List/HashSet/array receivers
    // route here): typed when the element type is certain (array GetElementType, script
    // list/hashset ElementType), List<object> fallback otherwise (tuple, string, generator,
    // dict, LINQ result, mixed). Always a fresh copy (copy semantics, mirrors ToArray()).
    // Reached from the script surface (Iterable.ToList) and from OrderedEnumerable.ToList, so the two spellings
    // produce the same value by construction rather than by agreement.
    internal object? IterableToList(object?[] args)
    {
        if (args.Length != 1)
            return RuntimeError<object?>("Iterable.ToList expects an Iterable.", 0, 0);
        if (!IsIterable(args[0]))
            return RuntimeError<object?>($"Expected an Iterable, got {args[0]?.GetType().Name ?? "null"}.", 0, 0);
        var elementType = InferToListElementType(args[0]);
        var list = CreateScriptList(elementType);
        if (TryGetIterableElements(args[0], out var elements))
        {
            foreach (var e in elements)
                list.AddRaw(e!);
        }
        else
        {
            // TryGetIterableElements deliberately does NOT auto-resume generators/coroutines
            // (foreach keeps its generator branch, LINQ materializes). ToList is eager, materialize
            // one-shot generator/coroutine sources the same way the foreach generator branch does
            // (yielded values are the elements; the generator's return value is not).
            switch (args[0])
            {
                case Generator<object?> gen:
                    while (gen.MoveNext())
                        list.AddRaw(gen.Current!);
                    break;
                case Coroutine co:
                    while (co.IsResumable)
                    {
                        var v = co.Resume();
                        if (!co.IsResumable) break;
                        list.AddRaw(v!);
                    }
                    break;
            }
        }
        return list;
    }

    /// <summary>Best-effort element-type inference for ToList: arrays via GetElementType(),
    /// script lists/hashsets via their ElementType, strings via their char sequence; everything
    /// else (tuple, dict, generator, LINQ result, mixed) falls back to object. Unresolvable element
    /// types and generic-parameter markers also fall back to object (no false typing).</summary>
    private static TypeInfo InferToListElementType(object? src)
    {
        TypeInfo? et = src switch
        {
            Array arr => MapArrayElementType(arr.GetType().GetElementType()),
            IScriptList l => l.ElementType,
            IScriptHashSet s => s.ElementType,
            string => new TypeInfo(TokenType.Char, false, 0, 0, null),
            _ => null
        };
        if (et == null || et.IsGenericParameter)
            return new TypeInfo(TokenType.Object, false, 0, 0, null);
        return et;
    }

    private static TypeInfo? MapArrayElementType(Type? t)
    {
        if (t == null || t == typeof(object)) return null;   // mixed / unresolved element → object
        try { return MapTypeInfo(t); }
        catch (ScriptException) { return null; }             // exotic element (jagged-of-multidim) → object
    }
}
