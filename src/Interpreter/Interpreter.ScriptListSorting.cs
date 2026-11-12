namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>Sorting a script list, for every IScriptList implementation.</summary>
    internal static class ScriptListSorting
    {
        /// <summary>Sorts IN PLACE, optionally through a script comparer (`fun(int a, int b) { return a - b; }`, which
        /// answers a negative, zero or positive number, exactly as a C# comparer does).
        ///
        /// Implemented as ToArray + Array.Sort + Clear + Add rather than by sorting a backing store: `Add` is the ONE
        /// place the element type is enforced, so a list of ints cannot be left holding a string by a comparison that
        /// mixed them, and every implementation gets the same behaviour.
        ///
        /// A script comparison that FAILS keeps its own message: Array.Sort wraps a comparer's exception, and this
        /// repository's rule is that a ScriptException is rethrown as itself rather than reported through the CLR's text
        /// (the same rule InvokeScriptFunction applies to a wrapped delegate call).</summary>
        public static void Sort(IScriptList list, object?[] args, int line, int column)
        {
            if (args.Length > 1) RuntimeError("Sort takes a comparer or nothing.", line, column);
            var sorted = list.ToArray();
            if (args.Length == 0)
            {
                try { Array.Sort(sorted); }
                catch (Exception ex) { RuntimeError(FriendlyExceptionMessage(ex), line, column); }
            }
            else
            {
                if (!Interpreter.IsScriptCallable(args[0]))
                    RuntimeError("Sort takes a function that compares two elements, not a value.", line, column);
                try { Array.Sort(sorted, new ScriptComparer(args[0], line, column)); }
                catch (Exception ex)
                {
                    if ((ex as ScriptException ?? ex.InnerException as ScriptException) is { } scriptFailure)
                        throw scriptFailure;
                    RuntimeError(FriendlyExceptionMessage(ex), line, column);
                }
            }
            list.Clear();
            foreach (var item in sorted) list.Add(item, line, column);
        }
    }

    /// <summary>A script `fun(a, b) { ... }` as the `IComparer` Array.Sort takes. The NON-generic overload on purpose: the
    /// generic one needs a `Comparison<T>`, and an array a script produced may be a typed one (`int[]`), so the same code
    /// serves every element type without a cast that could fail at run time.</summary>
    internal sealed class ScriptComparer(object? fn, int line, int column) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
            => Convert.ToInt32(Interpreter.Current!.InvokeCallableValue(fn, [x, y], line, column));
    }
}
