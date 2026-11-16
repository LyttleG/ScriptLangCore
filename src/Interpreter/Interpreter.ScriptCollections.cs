namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    ///  Defines the basic contract for script collections, such as lists, dictionaries, and hash sets.<br/>
    ///  It provides a property to get the count of elements in the collection and a method to clear the collection.
    /// </summary>
    /// <summary>The collections that add an ITEM, as opposed to a key and a value: IScriptList and
    /// IScriptHashSet share this and the two dictionary types do not. It exists because hoisting Add onto
    /// IScriptCollection was tried and refused by name (CS0535 for both dictionary types, whose Add takes a
    /// key and a value), so a generic over item-adding collections needs a type wider than the two, narrower
    /// than the base.</summary>
    internal interface IScriptItemCollection : IScriptCollection
    {
        void Add(object? item, int line, int column);
    }

    internal interface IScriptCollection
    {
        int Count { get; }
        void Clear();
    }

    internal interface IScriptList : IScriptItemCollection
    {
        TypeInfo ElementType { get; }
        void AddRaw(object item);
        void Remove(object? item);
        void RemoveAt(int index);
        object? this[int index] { get; set; }
        /// <summary>Returns the list's elements as an array, a typed <c>T[]</c> for unboxed lists
        /// (e.g. <c>int[]</c>), <c>object[]</c> for boxed lists.</summary>
        Array ToArray();
        /// <summary>Sorts the list IN PLACE, with a script comparer or without one. Declared on the INTERFACE because
        /// the script dispatch (MethodDispatch's `IScriptList` arm) is what a script's `list.Sort()` reaches, and the
        /// base class's own `InvokeMethod` is NOT that arm: the same member has to be reachable from both, or one of
        /// them silently misses it (measured: `[].ToList().Sort()` reported "Method 'Sort' not found on type
        /// List&lt;int&gt;" while an array's identical call worked, because only the array arm had it).</summary>
        void Sort(object?[] args, int line, int column);
    }

    internal interface IScriptDictionary : IScriptCollection
    {
        IDictionary Dictionary { get; }
        TypeInfo KeyType { get; }
        TypeInfo ValueType { get; }

        object? GetKey(int index);
        object? GetValue(int index);

        void Add(object? key, object? value, int line, int column);
        void AddRaw(object key, object? value);
        void Remove(object key);
        bool ContainsKey(object key);
        object[] Keys();
        object?[] Values();
        object? this[object key] { get; set; }
    }

    internal interface IScriptHashSet : IScriptItemCollection
    {
        TypeInfo ElementType { get; }
        void AddRaw(object item);
        bool Contains(object item);
        bool Remove(object item);
        /// <summary>Returns the set's elements as an array, a typed <c>T[]</c> for unboxed sets
        /// (e.g. <c>int[]</c>), <c>object[]</c> for boxed sets.</summary>
        Array ToArray();
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicMethods)]
    /// <summary>The name dispatch a script LIST answers, stated ONCE: ScriptBoxedList and ScriptUnboxedList
    /// carried byte-identical InvokeMethod bodies, which tools/check-dup-bodies.sh cannot see because a
    /// same-file, same-name group reads as an overload set. Each derived type still implements the six
    /// operations over its OWN storage; only the dispatch is here. The six are abstract and the derived
    /// members carry `override`, which is what an inherited abstract requires (a plain public member only
    /// hides it), and an override still implements the IScriptList member each of them already served.
    /// AddRaw takes a NON-nullable object on purpose: the only engine caller asserts non-null
    /// (AddRaw(args[0]!)), and ScriptUnboxedList stores into a List<T> whose T is notnull, so `object?`
    /// would push a maybe-null into it. The JSON array path forces its value for the same reason.</summary>
    internal abstract record ScriptCollectionBase
    {
        public abstract void Add(object? item, int line, int column);
        public abstract void AddRaw(object? item);
        public abstract void Remove(object? item);
        public abstract void RemoveAt(int index);
        public abstract Array ToArray();
        public abstract void Clear();
        public object? InvokeMethod(string methodName, object?[] args, int line, int column)
        {
            switch (methodName)
            {
                case "Add":
                    if (args.Length != 1) RuntimeError("Add expects one argument.", line, column);
                    Add(args[0], line, column);
                    return null;
                case "AddRaw":
                    if (args.Length != 1) RuntimeError("AddRaw expects one argument.", line, column);
                    AddRaw(args[0]!);
                    return null;
                case "Remove":
                    if (args.Length != 1) RuntimeError("Remove expects one argument.", line, column);
                    Remove(args[0]);
                    return null;
                case "RemoveAt":
                    if (args.Length != 1) RuntimeError("RemoveAt expects one argument.", line, column);
                    RemoveAt(Convert.ToInt32(args[0]));
                    return null;
                case "ToArray":
                    if (args.Length != 0) RuntimeError("ToArray expects no arguments.", line, column);
                    return ToArray();
                case "Clear":
                    if (args.Length != 0) RuntimeError("Clear expects no arguments.", line, column);
                    Clear();
                    return null;
                case "Sort":
                    Sort(args, line, column);
                    return null;
                default:
                    RuntimeError(MissingMemberText(this, methodName, args.Length), line, column);
                    return null;
            }
        }

        /// <summary>Sorts the elements IN PLACE, with a script comparer or without one. The WORK lives in
        /// <see cref="ScriptListSorting"/> so the three other IScriptList implementations (the lazy JSON list and the
        /// two sqlite adapters) can each be a one-line delegation instead of a fourth copy of the algorithm.</summary>
        public void Sort(object?[] args, int line, int column) => ScriptListSorting.Sort((IScriptList)this, args, line, column);
    }
}
