namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    /// Provides utility methods for formatting objects into string representations suitable for display or debugging
    /// purposes.
    /// </summary>
    /// <remarks>This class supports formatting a variety of object types, including strings, characters,
    /// arrays, lists, dictionaries, and custom script-related types. It handles cyclic references gracefully by
    /// indicating cycles in the output, preventing infinite recursion. This class is intended for internal use and is
    /// not thread-safe.</remarks>
    internal static class ValueFormatter
    {
        public static string Format(object? value)
        {
            var visited = new HashSet<object>();
            return FormatInternal(value, visited);
        }

        /// <summary>The text a value shows as when PRINTED or CONCATENATED, deliberately not <see cref="Format"/>,
        /// which is the script-LITERAL form and quotes strings (`print([1, "a"])`). This one keeps ToString's
        /// behaviour for every value, because `print(x)`, `"a" + x` and `$"{x}"` all went through ToString and their
        /// output must not shift, with ONE exception: a `Type` reports the name a script can WRITE
        /// (<see cref="Interpreter.GetFriendlyTypeName"/>), since `print(typename("ComNode"))` showed
        /// "DispatchVtbl.ComNode" while `typeof()` of a node says "ComNode". A CLR Type's ToString cannot be
        /// overridden, so the decision has to live where a value is stringified: this is that single place now.
        /// Null is empty, which is what TextWriter.Write(object) and string.Concat already did.</summary>
        public static string ToDisplay(object? value) => value switch
        {
            null => string.Empty,
            Type type => Interpreter.GetFriendlyTypeName(type),
            // A TYPE DEFINITION used as a value (a class, record, enum or struct NAME) is named the way `typeof`
            // names it, so `print(Animal)` is "Animal" instead of the ClassInfo record's own `Class Animal`, and
            // `print(Pt)` is "Pt" instead of that record's field dump. Same rule the two arms below follow: a value
            // a script can print says what it is, in a shape a script can WRITE.
            ClassInfo definition => definition.Name,
            TypeInfo typeInfo => TypeInfo.Spelling(typeInfo),
            // An ARRAY reports its literal form for the same reason: an array has no ToString of its own, so
            // `print([1, "a"])` showed "System.Object[]", a CLR name no script can write, while the very same
            // values in a List print as `[1, "a"]`. Same shape as the List, so the two now agree.
            Array array => Format(array),
            // A CLR DICTIONARY has no ToString of its own either, so `"" + {a: 1}` showed
            // "System.Collections.Generic.Dictionary`2[System.Object,System.Object]" while the same entries in a
            // scripted dictionary print as `{ a = 1 }`. Same shape as the Array arm above, and the literal a
            // script dictionary builds IS this CLR type (measured: `typeof({})` is `Dictionary<object, object>`).
            IDictionary dictionary => Format(dictionary),
            // A TUPLE takes the same road as an Array and a CLR dictionary above, and for the same reason: its
            // ITEMS are what a reader wants, and its own ToString answered `Tuple` (measured: `print((1, "x"))`).
            ScriptTuple tuple => Format(tuple),
            // The same rule as FormatInternal's fallback, through the same helper: an unmodelled CLR object whose
            // ToString IS its type's full name (`System.Object`) prints the short SCRIPT name instead, because a
            // CLR name is not a name a script can write. print_render_360_test measured `print(new object())`
            // answering `System.Object`.
            _ => ClrValueText(value),
        };

        private static string FormatInternal(object? value, HashSet<object> visited)
        {
            if (value == null)
                return "null";
            // Note: no global visited.Add here, each formatter handles its own
            // cycle detection (ScriptBoxedList, ScriptBoxedDictionary, ClassInstance, etc.)

            return value switch
            {
                string s => $"\"{s}\"",
                char c => $"'{c}'",
                ScriptFunction sf => sf.ToString(),
                EventInfo ei => ei.ToString(),
                CombinedDelegate cd => cd.ToString(),
                ClassInstance ci => FormatClassInstance(ci, visited),
                StructInstance si => FormatStructInstance(si, visited),
                ScriptBoxedList list => FormatBoxedList(list, visited),
                IScriptList unboxedList => FormatUnboxedListViaInterface(unboxedList, visited),
                BoxedScriptHashSet hashSet => FormatBoxedHashSet(hashSet, visited),
                IScriptHashSet unboxedHashSet => FormatUnboxedHashSetViaInterface(unboxedHashSet, visited),
                ScriptBoxedDictionary dict => FormatBoxedDict(dict, visited),
                IScriptDictionary unboxedDict => FormatUnboxedDictViaInterface(unboxedDict, visited),
                // AFTER the two script forms, so a script dictionary that also implements IDictionary keeps its
                // own arm: this one is for the plain CLR dictionary a LITERAL produces.
                IDictionary clrDict => FormatDictionaryEntries(clrDict, visited),
                Array arr => FormatArray(arr, visited),
                DelegateInfo di => di.ToString(),
                // A TUPLE's items are what a reader wants, and it had no arm at all: `print((1, "x"))` answered
                // `Tuple`, and so did a tuple inside an array, a list and a class field, because every route ends
                // in this switch. The spelling matches the literal, so a tuple read back looks like the value that
                // made it (a string item keeps its quotes, as it does in a list).
                ScriptTuple tuple => FormatTuple(tuple, visited),
                // A type DEFINITION and a type REFERENCE are named the way `typeof` names them, by the SAME two
                // arms ToDisplay carries. Without them a type value nested in a list, a dictionary, an array or
                // a tuple fell through to ToString, so `print([Animal])` answered "Class Animal" while
                // `print(Animal)` answers "Animal", and `print([List<int>])` dropped the argument and answered
                // "List". One renderer per value, whichever container it sits in.
                ClassInfo definition => definition.Name,
                TypeInfo typeInfo => TypeInfo.Spelling(typeInfo),
                // A CLR value the formatter has no arm for. `ToString()` is used unless it is the type's own full
                // name, which is the DEFAULT rendering and a name no script can write: measured with
                // print_render_360_test, `print(new object())` answered `System.Object`. The short SCRIPT name is
                // what a `Type` renders as (`Math`, not `System.Math`), so the two now agree.
                _ => ClrValueText(value),
            };
            // Visited cleanup handled by each formatter's own finally block
        }

        /// <summary>A CLR value the formatter has no arm for: `ToString()` is used unless it IS the type's own
        /// full name, which is the default rendering of an unmodelled object and a name no script can write
        /// (`System.Object`). The short name is then mapped through the engine's own CLR-to-script table, so
        /// `new object()` prints `object`, exactly as a `Type` renders as `Math` rather than `System.Math`.</summary>
        private static string ClrValueText(object value)
        {
            var text = value.ToString();
            // `text is not null`, NOT `IsNullOrEmpty`: an EMPTY string is a value a script can print, and treating
            // it as "unmodelled" answered `string` for it (measured: an interpolated argument in a compiled
            // function printed `stringplain` where `plain` was the value).
            if (text is not null && !string.Equals(text, value.GetType().FullName, StringComparison.Ordinal))
                return text;
            var shortName = value.GetType().Name;
            var script = TypeSystem.FromClrName(shortName);
            return string.IsNullOrEmpty(script) ? shortName : script;
        }

        private static string FormatTuple(ScriptTuple tuple, HashSet<object> visited)
        {
            if (!visited.Add(tuple)) return "(<cycle>)";
            try
            {
                var parts = new string[tuple.Items.Length];
                for (var i = 0; i < tuple.Items.Length; i++) parts[i] = FormatInternal(tuple.Items[i], visited);
                return "(" + string.Join(", ", parts) + ")";
            }
            finally
            {
                visited.Remove(tuple);
            }
        }

        private static string FormatClassInstance(ClassInstance ci, HashSet<object> visited)
        {
            if (!visited.Add(ci))
                return $"{ci.Class.Name} {{ <cycle> }}";
            try
            {
                var backingToProp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in ci.Class.Properties)
                    backingToProp[BackingFieldName(p.Name)] = p.Name;

                var entries = new List<string>();
                foreach (var kvp in ci.FieldValues)
                {
                    var displayName = backingToProp.TryGetValue(kvp.Key, out var propName) ? propName : kvp.Key;
                    entries.Add($"{displayName} = {FormatInternal(kvp.Value, visited)}");
                }

                return $"{ci.Class.Name} {{ {string.Join(", ", entries)} }}";
            }
            finally
            {
                visited.Remove(ci);
            }
        }
        private static string FormatStructInstance(StructInstance si, HashSet<object> visited)
        {
            if (!visited.Add(si)) return $"{si.StructName} {{ <cycle> }}";
            try
            {
                var backingToProp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var decl = si.Declaration;
                if (decl?.Properties != null)
                {
                    foreach (var p in decl.Properties)
                        backingToProp[BackingFieldName(p.Name)] = p.Name;
                }

                var entries = new List<string>();
                foreach (var kvp in si.Fields)
                {
                    var displayName = backingToProp.TryGetValue(kvp.Key, out var propName) ? propName : kvp.Key;
                    entries.Add($"{displayName} = {FormatInternal(kvp.Value, visited)}");
                }
                return $"{si.StructName} {{ {string.Join(", ", entries)} }}";
            }
            finally
            {
                visited.Remove(si);
            }
        }
        private static string FormatBoxedList(ScriptBoxedList list, HashSet<object> visited)
        {
            if (!visited.Add(list)) return "[...]";
            try
            {
                var items = list.GetElements().Select(item => FormatInternal(item, visited));
                return $"[{string.Join(", ", items)}]";
            }
            finally
            {
                visited.Remove(list);
            }
        }
        private static string FormatUnboxedListViaInterface(IScriptList list, HashSet<object> visited)
        {
            if (!visited.Add(list)) return "[...]";
            try
            {
                // The interface's OWN accessor, not a reflectively looked-up `GetElements`: that lookup answered
                // null for an EXPLICIT interface implementation, so the arm took its give-up path and every such
                // list rendered as the CYCLE MARKER. Measured: `"" + new List<int>()` printed `[...]` for an empty
                // list, and a two-item one printed the same. ToArray is documented to answer a typed T[] for an
                // unboxed list and object[] for a boxed one, so the items render exactly as an array's do, and the
                // visited guard above is kept: a list holding itself still terminates.
                return FormatArray(list.ToArray(), visited);
            }
            finally
            {
                visited.Remove(list);
            }
        }
        private static string FormatBoxedHashSet(BoxedScriptHashSet set, HashSet<object> visited)
        {
            if (!visited.Add(set))
                return "[...]";
            try
            {
                var items = set.Select(item => FormatInternal(item, visited));
                return $"[ {string.Join(", ", items)} ]";
            }
            finally
            {
                visited.Remove(set);
            }
        }
        private static string FormatUnboxedHashSetViaInterface(IScriptHashSet set, HashSet<object> visited)
        {
            if (!visited.Add(set)) return "[...]";
            try
            {
                var items = new List<string>();
                foreach (var item in (IEnumerable)set)
                    items.Add(FormatInternal(item, visited));
                return $"[ {string.Join(", ", items)} ]";
            }
            finally
            {
                visited.Remove(set);
            }
        }
        private static string FormatBoxedDict(ScriptBoxedDictionary dict, HashSet<object> visited)
        {
            if (!visited.Add(dict)) return "{ ... }";
            try
            {
                var entries = dict.GetEntries().Select(kvp =>
                    $"{FormatInternal(kvp.Key, visited)} = {FormatInternal(kvp.Value, visited)}");
                return $"{{ {string.Join(", ", entries)} }}";
            }
            finally
            {
                visited.Remove(dict);
            }
        }
        private static string FormatUnboxedDictViaInterface(IScriptDictionary dict, HashSet<object> visited)
        {
            return FormatDictionaryEntries(dict.Dictionary, visited);
        }

        /// <summary>The ONE rendering of a dictionary's entries, for every dictionary a script can hold: a
        /// scripted one (`IScriptDictionary` above) and a plain CLR one. The second half matters because a
        /// DICTIONARY LITERAL is a CLR `Dictionary<object, object>` (measured: `typeof({})` says
        /// `Dictionary<object, object>`, which is neither `ScriptBoxedDictionary` nor `IScriptDictionary`), so
        /// without this arm `"" + {a: 1}` printed the CLR type name where a scripted dictionary printed
        /// `{ a = 1 }`, and `CoerceTo`'s string case made that name reach a `string` argument too.</summary>
        private static string FormatDictionaryEntries(IDictionary dict, HashSet<object> visited)
        {
            if (!visited.Add(dict)) return "{ ... }";
            try
            {
                var entries = new List<string>();
                foreach (DictionaryEntry entry in dict)
                {
                    entries.Add($"{FormatInternal(entry.Key, visited)} = {FormatInternal(entry.Value, visited)}");
                }
                return $"{{ {string.Join(", ", entries)} }}";
            }
            finally
            {
                visited.Remove(dict);
            }
        }
        private static string FormatArray(Array arr, HashSet<object> visited)
        {
            if (!visited.Add(arr)) return "[...]";
            try
            {
                var items = arr.Cast<object>().Select(item => FormatInternal(item, visited));
                return $"[{string.Join(", ", items)}]";
            }
            finally
            {
                visited.Remove(arr);
            }
        }
    }
}
