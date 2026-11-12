namespace ScriptLangCore.ScriptExtensions;

internal static class LinqExtensions
{
    private static Interpreter Interpret => Current ?? throw new InvalidOperationException("No interpreter context");

    // Where
    extension(IEnumerable source)
    {
        public object Where(ScriptFunction predicate)
            => Interpret.InvokeNative("Where", source, predicate);

        public object Select(ScriptFunction selector)
            => Interpret.InvokeNative("Select", source, selector);

        public object SelectMany(ScriptFunction collectionSelector)
            => Interpret.InvokeNative("SelectMany", source, collectionSelector);

        public object First()
            => Interpret.InvokeNative("First", source);

        public object First(ScriptFunction predicate)
            => Interpret.InvokeNative("First", source, predicate);

        public object FirstOrDefault()
            => Interpret.InvokeNative("FirstOrDefault", source);

        public object FirstOrDefault(ScriptFunction predicate)
            => Interpret.InvokeNative("FirstOrDefault", source, predicate);

        public object Last()
            => Interpret.InvokeNative("Last", source);

        public object Last(ScriptFunction predicate)
            => Interpret.InvokeNative("Last", source, predicate);

        public object LastOrDefault()
            => Interpret.InvokeNative("LastOrDefault", source);

        public object LastOrDefault(ScriptFunction predicate)
            => Interpret.InvokeNative("LastOrDefault", source, predicate);

        public bool Any()
            => (bool)Interpret.InvokeNative("Any", source);

        public bool Any(ScriptFunction predicate)
            => (bool)Interpret.InvokeNative("Any", source, predicate);

        public bool All(ScriptFunction predicate)
            => (bool)Interpret.InvokeNative("All", source, predicate);

        public object ElementAt(object index)
            => Interpret.InvokeNative("ElementAt", source, index);

        public object ElementAtOrDefault(object index)
            => Interpret.InvokeNative("ElementAtOrDefault", source, index);

        public int Count()
            => (int)Interpret.InvokeNative("Count", source);

        public int Count(ScriptFunction predicate)
            => (int)Interpret.InvokeNative("Count", source, predicate);

        public double Sum()
            => (double)Interpret.InvokeNative("Sum", source);

        public double Average()
            => (double)Interpret.InvokeNative("Average", source);

        public object Min()
            => Interpret.InvokeNative("Min", source);

        public object Max()
            => Interpret.InvokeNative("Max", source);

        public object Take(object count)
            => Interpret.InvokeNative("Take", source, count);

        public object Skip(object count)
            => Interpret.InvokeNative("Skip", source, count);

        public OrderedEnumerable OrderBy(ScriptFunction keySelector)
            => (OrderedEnumerable)Interpret.InvokeNative("OrderBy", source, keySelector);

        // `orderBy(key, comparer)`: the comparer is applied to the KEYS (C#'s overload), so a script can order by a
        // projection with its own rule, the shape the page used to list as missing.
        public OrderedEnumerable OrderBy(ScriptFunction keySelector, ScriptFunction comparer)
            => (OrderedEnumerable)Interpret.InvokeNative("OrderBy", source, keySelector, comparer);

        public object OrderByDescending(ScriptFunction keySelector)
            => Interpret.InvokeNative("OrderByDescending", source, keySelector);

        public object OrderByDescending(ScriptFunction keySelector, ScriptFunction comparer)
            => Interpret.InvokeNative("OrderByDescending", source, keySelector, comparer);
    }

    // OrderBy / ThenBy / OrderByDescending
    public static OrderedEnumerable ThenBy(this OrderedEnumerable source, ScriptFunction keySelector)
        => (OrderedEnumerable)Interpret.InvokeNative("ThenBy", source, keySelector);

    // Distinct
    extension(IEnumerable source)
    {
        public object Distinct()
            => Interpret.InvokeNative("Distinct", source);

        public object GroupBy(ScriptFunction keySelector)
            => Interpret.InvokeNative("GroupBy", source, keySelector);

        public object Join(IEnumerable inner,
            ScriptFunction outerKeySelector, ScriptFunction innerKeySelector, ScriptFunction resultSelector)
            => Interpret.InvokeNative("Join", source, inner, outerKeySelector, innerKeySelector, resultSelector);

        public object LeftJoin(IEnumerable inner,
            ScriptFunction outerKeySelector, ScriptFunction innerKeySelector, ScriptFunction resultSelector)
            => Interpret.InvokeNative("LeftJoin", source, inner, outerKeySelector, innerKeySelector, resultSelector);

        public object RightJoin(IEnumerable inner,
            ScriptFunction outerKeySelector, ScriptFunction innerKeySelector, ScriptFunction resultSelector)
            => Interpret.InvokeNative("RightJoin", source, inner, outerKeySelector, innerKeySelector, resultSelector);

        public object GroupJoin(IEnumerable inner,
            ScriptFunction outerKeySelector, ScriptFunction innerKeySelector, ScriptFunction resultSelector)
            => Interpret.InvokeNative("GroupJoin", source, inner, outerKeySelector, innerKeySelector, resultSelector);

        public object Aggregate(object seed, ScriptFunction accumulator)
            => Interpret.InvokeNative("Aggregate", source, seed, accumulator);

        public object Reverse()
            => Interpret.InvokeNative("Reverse", source);

        public bool Contains(object value)
            => (bool)Interpret.InvokeNative("Contains", source, value);

        public object Zip(IEnumerable second, ScriptFunction resultSelector)
            => Interpret.InvokeNative("Zip", source, second, resultSelector);
    }
}