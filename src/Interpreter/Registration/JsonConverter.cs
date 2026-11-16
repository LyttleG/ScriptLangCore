namespace ScriptLangCore;

/// <summary>Options controlling JSON serialization behavior.</summary>
/// <param name="IncludeNonPublic">If true, the non-public members are emitted ALONGSIDE the public ones.</param>
/// <param name="IncludeType">If true, includes class/struct name as key in the output.</param>
/// <param name="ShowType">If true, includes __type metadata for round-trip deserialization.</param>
/// <param name="OnlyNonPublic">If true, the public members are left out. This is the complement of
/// <paramref name="IncludeNonPublic"/>, that one ADDS to the public view, this one replaces it, so a
/// payload meant to show the non-public side (an audit trail, a redacted export) does not have to filter
/// its own output. It implies <paramref name="IncludeNonPublic"/>, since the members it wants are exactly
/// the ones that flag admits.</param>
/// <param name="OmitDefaults">If true, a CLASS or STRUCT field whose value is its TYPE's default is left
/// out, so an object that only sets a few of its fields emits only those. It is exact with respect to this
/// engine's reader, which fills an absent key with <c>DefaultValue(field.Type)</c> and never applies a
/// declaration initializer, so what is omitted reads back as the default it was. A DICTIONARY entry or an
/// ARRAY element is never omitted: there a zero is data or a position, not a default.</param>
public readonly record struct JsonSerializeOptions(
    bool IncludeNonPublic = false,
    bool IncludeType = false,
    bool ShowType = false,
    bool OnlyNonPublic = false,
    bool OmitDefaults = false
);

internal partial class Interpreter
{
    /// <summary>Write any interpreter value as JSON text, AOT-safe (no reflection).</summary>
    /// <summary>The ROOT name of the collection a value participates in, for the JSON marker: a value the engine
    /// serves through an INTERNAL class (a lazy typed read today, a lazy dictionary or set tomorrow) is the
    /// collection it STANDS FOR, `List`, `HashSet` or `Dictionary`, never the wrapper's own class name, which is
    /// no name a script writes. The element type is deliberately not spelled here: a wrapper keeps it internally
    /// for its own per-element work, and a typed read takes it from the caller's declaration. Anything that is
    /// not one of these collections answers null and keeps its normal name.</summary>
    private static string? ContainerRootNameOf(object? value) => value switch
    {
        IScriptDictionary => "Dictionary",
        IScriptHashSet => "HashSet",
        IScriptList => "List",
        _ => null,
    };

    /// <summary>Writes the <c>__type</c> member a value can carry while <c>ShowType</c> is on. Objects
    /// (and dictionaries) have a member slot for it; a JSON array does not, which is why arrays take the
    /// wrapper from <see cref="WriteArrayStart"/> instead.</summary>
    private static void WriteTypeMember(Utf8JsonWriter writer, object? value, JsonSerializeOptions options)
    {
        // `includeType` means the same as `showType` everywhere: the flag asks for the value's type to be
        // NAMED. It used to mean "wrap the payload in the type's name", a form the reader did not understand
        // (a typed read of it returned a blank instance), so the two spellings are one now.
        if (!(options.ShowType || options.IncludeType) || value is null) return;
        writer.WritePropertyName("__type");
        // The marker names the SHAPE a reader must rebuild. A name that already READS as one (IsContainerTypeName
        // accepts `List`, `List<int>`, `int[]`, `HashSet<int>`, `Tuple`, `Dictionary`) is left exactly as it is;
        // only a name that does NOT, i.e. an internal wrapper's own class name, is replaced by the ROOT of the
        // collection the value stands for, because such a class name is no name a script writes. The
        // `LazyDeserializedList` ALIAS stays registered, so `is LazyDeserializedList` keeps answering. What that
        // spelling did NOT give is a READABLE payload: measured on the previous build, a lazy list's own text
        // answered a 2-entry object to the untyped read (nothing unwrapped the wrapper) and "names a list, but the
        // payload is a JSON object" to a typed one. It reads as the list it is now.
        var markerName = GetFriendlyTypeName(value.GetType());
        if (!IsContainerTypeName(markerName)) markerName = ContainerRootNameOf(value) ?? markerName;
        writer.WriteStringValue(markerName);
    }

    /// <summary>Opens a JSON array, as <c>{"__type":"&lt;name&gt;","Values":[…]}</c> when ShowType is on,
    /// because an array has no member slot of its own. Returns true when the wrapper was opened, so the
    /// caller closes one extra object with <see cref="WriteArrayEnd"/>.</summary>
    private static bool WriteArrayStart(Utf8JsonWriter writer, object? value, JsonSerializeOptions options,
        IReadOnlyList<string?>? names = null)
    {
        if (!(options.ShowType || options.IncludeType) || value is null)
        {
            writer.WriteStartArray();
            return false;
        }
        writer.WriteStartObject();
        WriteTypeMember(writer, value, options);
        // A tuple's labels, when it has any: an array has nowhere to put them, so they travel beside Values.
        if (names is { Count: > 0 })
        {
            writer.WritePropertyName("Names");
            writer.WriteStartArray();
            foreach (var n in names)
            {
                if (n is null) writer.WriteNullValue();
                else writer.WriteStringValue(n);
            }
            writer.WriteEndArray();
        }
        writer.WritePropertyName("Values");
        writer.WriteStartArray();
        return true;
    }

    private static void WriteArrayEnd(Utf8JsonWriter writer, bool wrapped)
    {
        writer.WriteEndArray();
        if (wrapped) writer.WriteEndObject();
    }

    /// <summary>The ONE statement of the JSON writer's options. The encoder that escapes only what JSON requires
    /// (so a + in an ISO 8601 date survives) is a decision, not a detail, and a second copy of it is a second
    /// answer: the registration file carried its own until 2026-10-02.</summary>
    internal static JsonWriterOptions JsonWriterOpts(bool pretty) => new()
    {
        Indented = pretty,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>The one way a value becomes UTF-8 JSON BYTES, which is the primitive here: <c>Json.SerializeToUtf8Bytes</c>
    /// hands them straight back and <see cref="SerializeToText"/> is this decoded, so the two forms cannot drift.</summary>
    internal static byte[] SerializeToUtf8Bytes(object? value, JsonSerializeOptions options = default, bool pretty = false)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, JsonWriterOpts(pretty));
        WriteJsonValue(writer, value, options: options);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The one way a value becomes JSON text. <c>Json.Serialize</c> and the round-trip cast in
    /// <c>Json.Deserialize(type, value)</c> both come through here, so the two cannot drift apart.</summary>
    internal static string SerializeToText(object? value, JsonSerializeOptions options = default, bool pretty = false)
        => Encoding.UTF8.GetString(SerializeToUtf8Bytes(value, options, pretty));

    /// <summary>The ONE place a serializer's options are read, so the text form (<c>Json.Serialize</c>) and the
    /// UTF-8 form (<c>Json.SerializeToUtf8Bytes</c>) cannot drift apart. The byte form read them itself until
    /// 2026-10-02 and had all four of the defects the text form's own comments record having fixed: a dictionary
    /// LITERAL (the natural spelling) was ignored, only two of the five keys were known, the key names were
    /// compared without the interpreter's case rule, and an unknown key was dropped without a word.</summary>
    private (JsonSerializeOptions Options, bool Pretty) ReadSerializeOptions(object?[] args, string member)
    {
        // Parse options from optional dictionary argument
        var opts = new JsonSerializeOptions();
        var pretty = args.Length > 1 && args[1] is bool and true;
        // The options argument is read in whichever dictionary shape the language handed over, its own script
        // dictionary (boxed or unboxed) OR a plain CLR Dictionary<object, object>, which is what a dictionary
        // LITERAL is materialized as. Requiring the script class alone made `Json.Serialize(x, {"pretty": true})`
        // a SILENT no-op, precisely the spelling an author naturally writes.
        //
        // Keys are NAMES, so they follow the interpreter's case rule like every other name: the spelling
        // declared below in the default (C#-strict) mode, any case under `.lax`. A key outside the set is
        // reported and dropped, so a typo, or a differently-cased key in strict mode, says so instead of
        // quietly doing nothing. (This one lookup hardcoded OrdinalIgnoreCase until 2026-09-16, which made
        // `{"ShowType": true}` mean what `{"showType": true}` means while every OTHER name in the language was
        // C#-strict: the case-flag pass of f724401 had simply missed this spot.)
        // The options of a JSON string, so `Json.Serialize(v, '{"showType":true}')` means what
        // `Json.Serialize(v, {"showType":true})` means. A text that is not a JSON object is read as a bare
        // option NAME instead, so `Json.Serialize(v, "showType")` is the same thing again, and a name the set
        // does not know is reported by the key loop below, by name. Null when the text is neither, which the
        // caller reports rather than ignoring.
        static IEnumerable<(string, object?)>? OptionsFromJson(string optText)
        {
            try
            {
                using var doc = JsonDocument.Parse(optText);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var pairs = new List<(string, object?)>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        pairs.Add((prop.Name, prop.Value.ValueKind switch
                        {
                            JsonValueKind.True => (object?)true,
                            JsonValueKind.False => false,
                            JsonValueKind.Number => prop.Value.TryGetInt64(out var num) ? num : prop.Value.GetDouble(),
                            JsonValueKind.String => prop.Value.GetString() ?? "",
                            _ => null,
                        }));
                    }
                    return pairs;
                }
            }
            catch (JsonException)
            {
                // Not JSON at all: fall through to the bare-name reading below.
            }

            var name = optText.Trim();
            return name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_')
                ? new List<(string, object?)> { (name, true) }
                : null;
        }

        const string validOptions = "includeNonPublic, nonPublic, includeType, showType, pretty, omitDefaults";
        if (args.Length > 1)
        {
            // A second argument that is neither the options dictionary nor a JSON STRING of one used to fall
            // through this switch and do NOTHING at all, with no warning: `Json.Serialize(v, '{"showType":true}')`
            // read as no options, which is the spelling an author writes when they think of options as a JSON
            // blob rather than as a dictionary literal. A string is now read as JSON (so that spelling works),
            // and anything that cannot be read as options is REPORTED here rather than dropped, the same way a
            // key outside validOptions is reported below.
            var optionPairs = args[1] switch
            {
                IScriptDictionary optDict => optDict.Keys().Select(pk => (pk.ToString() ?? "", optDict[pk])),
                IDictionary<object, object> optRaw => optRaw.Select(kv => ((string)(kv.Key?.ToString() ?? ""), (object?)kv.Value)),
                string optJson => OptionsFromJson(optJson),
                // A BOOL is a spelling of its own, read above as `pretty` before this switch is reached, so it is
                // accepted here rather than reported: warning about a documented call is worse than a no-op.
                bool => Enumerable.Empty<(string, object?)>(),
                _ => null,
            };
            if (optionPairs == null)
            {
                Logging.Log.Warning("Script",
                    $"{member}: the second argument is neither an options dictionary nor a JSON string of one, so it was ignored. Valid options: {validOptions}.",
                    Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);
            }
            else
            {
                foreach (var (k, v) in optionPairs)
                {
                    if (k.Equals("includeNonPublic", Comparison))
                        opts = opts with { IncludeNonPublic = IsTruthy(v) };
                    else if (k.Equals("nonPublic", Comparison))
                        // The COMPLEMENT of includeNonPublic, not a shorter spelling of it: only the
                        // non-public members, with the public ones left out. It implies IncludeNonPublic,
                        // since the members it wants are the ones that flag admits.
                        opts = IsTruthy(v)
                            ? opts with { IncludeNonPublic = true, OnlyNonPublic = true }
                            : opts with { OnlyNonPublic = false };
                    else if (k.Equals("includeType", Comparison))
                        opts = opts with { IncludeType = IsTruthy(v) };
                    else if (k.Equals("showType", Comparison))
                        opts = opts with { ShowType = IsTruthy(v) };
                    else if (k.Equals("omitDefaults", Comparison))
                        // A field whose value is its TYPE's default is left out, so an object that sets a few of
                        // its fields emits only those. Exact against this engine's reader, which fills an absent key
                        // with DefaultValue(field.Type): what is omitted reads back as the default it was.
                        opts = opts with { OmitDefaults = IsTruthy(v) };
                    else if (k.Equals("pretty", Comparison))
                        // Read ONCE, here: these writer options come from this very value, and the second lookup
                        // that used to read it again was a second copy of the same comparison, the shape that
                        // drifts. Accepted here so it is not reported as unknown.
                        pretty = IsTruthy(v);
                    else
                        Logging.Log.Warning("Script",
                            $"{member}: ignoring unknown option '{k}'. Valid options: {validOptions}.",
                            Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);
                }
            }
        }

        return (opts, pretty);
    }

    /// <summary>A control's WHOLE table: the rows every kind shares, the rows its OWN type adds, and its CHILDREN's
    /// own tables under a <c>Controls</c> row of type <c>list</c>. One shape at every depth, which is what lets a
    /// form be exported with everything inside it: `Kind` is in every child's rows already, so a reader can rebuild
    /// the tree from the document alone.
    ///
    /// The recursion is bounded by the WINDOW tree, not by the object graph: <c>Controls</c> is a forest whose
    /// children cannot hold an ancestor, and the back-reference a child has to its parent is a member of Control,
    /// which the own-row walk below deliberately stops short of.</summary>
    /// <summary>A control's own table: the design rows it carries, plus the rows its own type adds. The two
    /// IDENTITY rows (<c>Name</c> and <c>Kind</c>) are kept for the ROOT only, because a child is reached through an
    /// entry that already carries both: repeating them inside would be one fact in two places, which is one place
    /// too many for a reader to keep in step.
    ///
    /// The root has no entry above it, so its own rows name it, with the same generated name a child's entry would
    /// have carried.</summary>
    /// <summary>The version of the DOCUMENT layout, and the row that states it. It is not the bundle's version: a
    /// document and a `.slcbc` are two formats and only one of them is bytecode. The row exists so a document written
    /// by a LATER engine is REFUSED by name instead of mis-read, because the root's Kind says which host it is while
    /// this says which LAYOUT of it. A document with no row is the layout that predates the row, which is version 1.
    ///
    /// Bump it when a change is one a reader could not infer from the VALUES: a new row is not (an unknown row is
    /// refused on APPLY, and a reported fact is skipped), while a row whose meaning changes, a value whose shape
    /// changes, or a requirement that did not exist before is.</summary>
    internal const int DocumentVersion = 1;
    private const string DocumentVersionRow = "Version";

    /// <summary>A document ROOT's rows: the version FIRST, so a reader meets it before anything else. Only a root
    /// carries it. A child's own rows and a document window's rows are part of the same document, so repeating it
    /// there would be noise, and the check is made once, at the root.</summary>
    private static List<ControlProp> WithDocumentVersion(List<ControlProp> rows)
    {
        rows.Insert(0, new ControlProp { Name = DocumentVersionRow, Type = "int", Value = DocumentVersion });
        return rows;
    }

    /// <summary>The rows of something a caller is serializing, with the version stated only by a DOCUMENT. A document is
    /// exactly what <c>FormToJson</c> takes and refuses otherwise (a Form, an MdiForm, an MdiChild), so a bare CONTROL's
    /// table is a FRAGMENT of one and does not claim a version of a format it is not the whole of.</summary>
    private static List<ControlProp> DocumentRows(object value, List<ControlProp> rows)
        => value is IControlHost ? WithDocumentVersion(rows) : rows;

    /// <summary>Refuse a document whose LAYOUT this engine does not know, BY NAME, before anything is built. A version
    /// OLDER than this one is read, because every change so far has been additive and an older document is missing
    /// rows rather than mis-stating them; a NEWER one is the case worth stopping, since nothing here can tell what its
    /// rows mean.</summary>
    private static void CheckDocumentVersion(System.Text.Json.JsonElement root)
    {
        var stated = RowText(root, DocumentVersionRow);
        if (stated.Length == 0) return;   // the layout that predates the row
        if (!int.TryParse(stated, out var version))
            throw new ScriptException(
                $"FormFromJson: the document's {DocumentVersionRow} row is '{stated}', which is not a number.");
        if (version > DocumentVersion)
            throw new ScriptException(
                $"FormFromJson: this document is format version {version}, and this engine writes version {DocumentVersion}. "
                + "It was written by a newer engine, so its rows cannot be read safely here.");
    }

    private static List<ControlProp> TableOf(Control c, string? entryName = null)
    {
        var rows = c.PropList();
        if (entryName is null)
        {
            var rootName = string.IsNullOrEmpty(c.Name) ? KindOf(c) + "1" : c.Name;
            foreach (var row in rows) if (row.Name == "Name") { row.Value = rootName; break; }
        }
        else
        {
            rows.RemoveAll(r => r.Name is "Name" or "Kind");
        }
        rows.AddRange(OwnTypeRows(c, rows));
        if (c is Form form && form.Controls.Count > 0)
        {
            // A child is a ROW of its parent, and its Value is the child's own table: the same {Name, Type, Value}
            // triple at every depth, so the entry says what the child is instead of leaving a reader to scan the
            // child's rows for its Kind.
            var nested = new List<ControlProp>();
            var taken = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var child in form.Controls)
            {
                var childName = NameFor(child, taken);
                nested.Add(new ControlProp { Name = childName, Type = KindOf(child), Value = TableOf(child, childName) });
            }
            // The element type is the shape of an ENTRY, not of a bare row: these are the child tables, each one a
            // ControlProp whose Value is the child's own rows. A bare `List` would say a list and nothing about what
            // is inside it.
            rows.Add(new ControlProp { Name = "Controls", Type = "List<ControlProp>", Value = nested });
        }
        return rows;
    }

    /// <summary>A HOST's own table: the two identity rows, the scalar rows its own type has (it has no design table,
    /// which is the difference from a Control), its controls, and for a frame its DOCUMENT WINDOWS, each of which is
    /// a host in its own right and nests its own controls. Every control the document names carries a name, generated
    /// per kind among its siblings when the script set none.
    /// </summary>
    private static List<ControlProp> HostTable(string kind, object host, IEnumerable<Control> children,
                                               IEnumerable<MdiChild>? windows)
    {
        var rows = new List<ControlProp>
        {
            new() { Name = "Name", Type = "string", Value = kind + "1" },
            new() { Name = "Kind", Type = "string", Value = kind },
        };
        rows.AddRange(ScalarRowsOf(host, null));

        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        var childRows = new List<ControlProp>();
        foreach (var child in children)
        {
            var name = NameFor(child, taken);
            childRows.Add(new ControlProp { Name = name, Type = KindOf(child), Value = TableOf(child, name) });
        }
        rows.Add(new ControlProp { Name = "Controls", Type = "List<ControlProp>", Value = childRows });

        if (windows is not null)
        {
            var windowRows = new List<ControlProp>();
            // A document window has no Name of its own (its class exposes none), so its entry is named by its place,
            // which is the same rule the children follow one level down.
            var count = 0;
            foreach (var window in windows)
                windowRows.Add(new ControlProp
                {
                    Name = "MdiChild" + (++count),
                    Type = "MdiChild",
                    Value = HostTable("MdiChild", window, window.Controls, null),
                });
            rows.Add(new ControlProp { Name = "MdiChildren", Type = "List<ControlProp>", Value = windowRows });
        }
        return rows;
    }

    /// <summary>The name a control carries in a document: its own when the script set one, and <c>&lt;Kind&gt;&lt;n&gt;</c>
    /// when it did not, counted per KIND among its siblings so a toolbar of nine buttons reads <c>Button1</c> to
    /// <c>Button9</c> rather than Button1, ListView2, Button3. The counts are per parent because a name only has to
    /// be unique where it is used.</summary>
    private static string NameFor(Control c, Dictionary<string, int> taken)
    {
        if (!string.IsNullOrEmpty(c.Name)) return c.Name;
        var kind = KindOf(c);
        taken.TryGetValue(kind, out var n);
        taken[kind] = ++n;
        return kind + n;
    }

    /// <summary>The design-table rows a value's OWN type adds: its readable public properties, SCALARS ONLY, walked
    /// from the most derived type down to just below <see cref="Control"/>, whose rows the base table already
    /// carries under its own design names (so stopping there is what keeps one member from appearing twice under two
    /// spellings). A collection is left out on purpose, because <see cref="TableOf"/> is what carries the children,
    /// and a raw HANDLE is left out because a number that names nothing is not a property a reader can act on.</summary>
    /// <summary>The scalar rows a CONTROL's own type adds. <see cref="ScalarRowsOf"/> is the general one, and the
    /// stop is Control, whose rows the design table already carries under its own design names.</summary>
    private static List<ControlProp> OwnTypeRows(Control value, List<ControlProp> designRows)
    {
        // A name the design table ALREADY carries is not written a second time, and that is what makes a document
        // say one thing once. The two lists genuinely overlap: a CheckBox's own type declares Checked and the design
        // table carries it too, a TrackBar's declares Minimum/Maximum/Value, and the document wrote each of them
        // TWICE, which a reader sees and then has to decide which of the two it believes. The design row is the one
        // that survives, because it is the row a script's SetProp reaches and it comes first in the document.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in designRows) taken.Add(row.Name);
        var rows = new List<ControlProp>();
        foreach (var row in ScalarRowsOf(value, typeof(Control)))
            if (taken.Add(row.Name)) rows.Add(row);
        return rows;
    }

    /// <summary>The scalar rows ANY value's own type adds, walked down to <paramref name="stopBelow"/> (null walks to
    /// the top, which is what an MDI host needs: it has no design table, so its own members ARE its rows). SCALARS
    /// ONLY, and a raw HANDLE is left out, because a number that names nothing is not a property a reader can act
    /// on. An enum travels as the SPELLING, which is the whole reason a document writes the name and not the number.
    /// </summary>
    private static List<ControlProp> ScalarRowsOf(object value, Type? stopBelow)
    {
        var rows = new List<ControlProp>();
        for (var t = value.GetType();
             t is not null && t != stopBelow && t != typeof(object);
             t = t.BaseType)
        {
            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (pt == typeof(IntPtr) || pt == typeof(UIntPtr)) continue;
                if (pt != typeof(string) && !pt.IsEnum && !pt.IsPrimitive) continue;
                try
                {
                    rows.Add(new ControlProp
                    {
                        Name = prop.Name,
                        Type = GetFriendlyTypeName(prop.PropertyType),
                        Value = pt.IsEnum ? prop.GetValue(value)?.ToString() : prop.GetValue(value),
                    });
                }
                catch { /* a getter that throws is skipped, as the generic arm above does */ }
            }
        }
        return rows;
    }

    internal static void WriteJsonValue(Utf8JsonWriter writer, object? value,
        HashSet<object>? visited = null, JsonSerializeOptions options = default)
    {
        visited ??= [];

        switch (value)
        {
            // ── Null ──
            case null:
                writer.WriteNullValue();
                break;

            // ── Primitives ──
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case char c:
                writer.WriteStringValue(c.ToString());
                break;

            // ── Native handles ──
            // A pointer is a number. Without this the reflection default arm caught it, found no
            // public instance property on IntPtr, and wrote the string "IntPtr", so a handle
            // serialized as a type name instead of its value.
            case IntPtr p:
                writer.WriteNumberValue(p.ToInt64());
                break;
            case UIntPtr up:
                writer.WriteNumberValue(up.ToUInt64());
                break;

            // ── Numeric types, delegated to NumericTypeConverter ──
            // -- A script enum's member --
            // It serializes as its underlying value, exactly as it did before it carried its enum: an enum
            // TYPE writes {"Red":0,...}, and the reflection default arm would otherwise write the wrapper.
            case EnumValue ev:
                writer.WriteNumberValue(ev.Value);
                break;

            case var n when NumericTypeConverter.IsNumeric(n):
                NumericTypeConverter.WriteJsonNumber(writer, n);
                break;

            // ── Date/time ──
            case DateTime dt:
                writer.WriteStringValue(dt.ToString("O"));  // ISO 8601
                break;
            case DateTimeOffset dto:
                writer.WriteStringValue(dto.ToString("O"));
                break;
            case TimeSpan ts:
                writer.WriteStringValue(ts.ToString("c"));  // hh:mm:ss
                break;

            // ── Guid ──
            case Guid g:
                writer.WriteStringValue(g.ToString("D"));   // 00000000-0000-0000-0000-000000000000
                break;

            // ── Enum (serialize as string name) ──
            case Enum e:
                writer.WriteStringValue(e.ToString());
                break;

            // ── The engine's EventArgs carries NOTHING, and says so ──
            // It is a struct whose only member is a static Empty, so the generic path answered its friendly type
            // name and a click's arguments serialized as the string "EventArgs": measured, while MouseEventArgs
            // answered {"X":40,"Y":30} and KeyEventArgs {"KeyCode":"A","Alt":false,...}. An empty object is the
            // truth: that event carries no data, and the SENDER (the first argument) is the whole story.
            case EventArgs:
                writer.WriteStartObject();
                WriteTypeMember(writer, value, options);
                writer.WriteEndObject();
                break;

            // ── A feed serializes as its STATE, never as its observers ──
            // A subject is live state, and its public members include `ReadOnly`, which answers a NEW view on every
            // read, so the reflection default arm below recursed for ever (a stack nothing survives). What travels is
            // what a reader can act on: how it delivers, how much room each subscriber has, what the capacity has
            // cost, and whether it is over. The OBSERVERS are deliberately absent: each is a closure over an
            // interpreter, so nothing could rebuild it from JSON, and a snapshot that claimed to hold them would be a
            // lie. A read-only view carries the same state plus `ReadOnly: true`.
            case EventSubject feed:
                writer.WriteStartObject();
                WriteTypeMember(writer, value, options);
                writer.WriteString("Mode", feed.Mode);
                writer.WriteNumber("Capacity", feed.Capacity);
                writer.WriteString("Overflow", feed.Overflow);
                writer.WriteBoolean("ReadOnly", feed.ReadOnlyView);
                writer.WriteBoolean("Closed", feed.Closed);
                writer.WriteNumber("Subscribers", feed.Subscribers);
                writer.WriteNumber("Queued", feed.Count);
                writer.WriteNumber("Dropped", feed.Dropped);
                writer.WriteEndObject();
                break;

            // ── And its handle, for the same reason: two members, no closure ──
            case EventSubscription subscription:
                writer.WriteStartObject();
                WriteTypeMember(writer, value, options);
                writer.WriteBoolean("Closed", subscription.Closed);
                writer.WriteEndObject();
                break;

            // ── A Control serializes as its DESIGN TABLE, plus the rows its OWN type adds ──
            // The engine's own view of what a control carries: the rows PropList() answers, each a ControlProp with
            // Name / Type / Value. Measured before this case existed: a Button serialized as the string "Button",
            // because the generic path falls back to the friendly type name when it finds no public property OF THE
            // VALUE'S OWN TYPE (BindingFlags.DeclaredOnly: a Form declares some, which is why a form looked like it
            // worked while a button did not), while the same rows serialize perfectly when asked for directly.
            // Uniform for every kind now.
            case Control ctrl:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var ctrlRows = DocumentRows(ctrl, TableOf(ctrl));
                    var ctrlWrapped = WriteArrayStart(writer, value, options);
                    foreach (var ctrlRow in ctrlRows) WriteJsonValue(writer, ctrlRow, visited, options);
                    WriteArrayEnd(writer, ctrlWrapped);
                    break;
                }

            // ── An MDI FRAME and an MDI DOCUMENT WINDOW, which are hosts but not Controls ──
            // Neither is a Control descendant, so neither could reach the arm above: measured before this, a frame
            // serialized as a FLAT object of its public members with its Handle in it and its children as bare
            // tables, and nothing said what it was. They answer the same ROW document a form does, with a Kind row
            // that now really does say which host this is, and a document window's own controls nested inside it.
            case MdiForm frame:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var frameRows = DocumentRows(frame, HostTable("MdiForm", frame, frame.Controls, frame.DocumentWindows()));
                    var frameWrapped = WriteArrayStart(writer, value, options);
                    foreach (var row in frameRows) WriteJsonValue(writer, row, visited, options);
                    WriteArrayEnd(writer, frameWrapped);
                    break;
                }
            case MdiChild window:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var windowRows = DocumentRows(window, HostTable("MdiChild", window, window.Controls, null));
                    var windowWrapped = WriteArrayStart(writer, value, options);
                    foreach (var row in windowRows) WriteJsonValue(writer, row, visited, options);
                    WriteArrayEnd(writer, windowWrapped);
                    break;
                }

            // ── Types that CANNOT be serialized, skip gracefully ──
            // (DynamicLibrary is NOT one of them: it exposes public readable members, Name, and
            // the handle as a number, so the generic object path below serializes it.)
            case Delegate _:
            case Stream _:
            case Task _:
            case CancellationToken _:
            case VoidMarker _:
                writer.WriteNullValue();
                break;

            // ── Non-script .NET collections (serialize as arrays/objects) ──
            // ── Script collections ──
            case IScriptDictionary dict:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    writer.WriteStartObject();
                    WriteTypeMember(writer, value, options);
                    foreach (var key in dict.Keys())
                    {
                        var keyName = key.ToString() ?? "";
                        writer.WritePropertyName(keyName);
                        WriteJsonValue(writer, dict[key], visited, options);
                    }
                    writer.WriteEndObject();
                    break;
                }

            case IScriptList list:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var wrapped = WriteArrayStart(writer, value, options);
                    foreach (var item in list.ToArray())
                        WriteJsonValue(writer, item, visited, options);
                    WriteArrayEnd(writer, wrapped);
                    break;
                }

            // ── Script SETS: the same array wrapper a list takes, so a set is a container rather than an object
            // whose internals leak. Measured before this arm: a `HashSet<int>` wrote
            // `{"__type":"Interpreter.UnboxedScriptHashSet`1","Count":2,"ElementType":{…}}`, a CLR type name no
            // script writes, and its ELEMENTS were absent entirely, so it did not round-trip at all. The marker
            // names the root (`HashSet`), and a typed read imposes the declared element type, which is exactly
            // what a bare container name means on the way back in.
            case IScriptHashSet set:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var wrapped = WriteArrayStart(writer, value, options);
                    foreach (var item in set.ToArray())
                        WriteJsonValue(writer, item, visited, options);
                    WriteArrayEnd(writer, wrapped);
                    break;
                }

            // ── Script tuples, serialize as JSON array ──
            case ScriptTuple tup:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var wrapped = WriteArrayStart(writer, value, options, tup.Names);
                    foreach (var item in tup.Items)
                        WriteJsonValue(writer, item, visited, options);
                    WriteArrayEnd(writer, wrapped);
                    break;
                }

            // ── Script class instances ──
            case ClassInstance ci:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    writer.WriteStartObject();
                    var useShowType = options.ShowType || options.IncludeType;
                    if (useShowType)
                    {
                        writer.WritePropertyName("__type");
                        writer.WriteStringValue(ci.Class.Name);
                    }
                    foreach (var field in ci.Class.Fields)
                    {
                        if (field.IsStatic) continue;
                        if (!options.IncludeNonPublic && field.Access != AccessModifier.Public) continue;
                        if (options.OnlyNonPublic && field.Access == AccessModifier.Public) continue;
                        if (!ci.FieldValues.TryGetValue(field.Name, out var val)) continue;
                        // A field at its TYPE's default, when asked: the reader gives an absent key exactly that
                        // value (DefaultValue(field.Type)), so leaving it out costs nothing on the way back in.
                        if (options.OmitDefaults && IsTypeDefault(val, field.Type)) continue;
                        writer.WritePropertyName(ResolvePropertyName(ci.Class.Properties, field.Name));
                        WriteJsonValue(writer, val, visited, options);
                    }
                    writer.WriteEndObject(); // close root
                    break;
                }

            // ── Script struct instances ──
            case StructInstance si:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    writer.WriteStartObject();
                    var useShowType = options.ShowType || options.IncludeType;
                    if (useShowType)
                    {
                        writer.WritePropertyName("__type");
                        writer.WriteStringValue(si.StructName);
                    }
                    foreach (var kv in si.Fields)
                    {
                        // The declaration is looked up ONCE: the two visibility rules and the default check all ask
                        // for it, and two copies of the same FirstOrDefault is the shape that drifts apart.
                        var fieldDecl = si.Declaration?.Fields.FirstOrDefault(f =>
                            f.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                        // Skip non-public fields unless IncludeNonPublic is set
                        if (!options.IncludeNonPublic && fieldDecl != null && fieldDecl.Access != AccessModifier.Public)
                        {
                            continue;
                        }
                        // Only the non-public fields: anything the declaration calls public goes, and an unknown
                        // declaration is left out rather than guessed at.
                        if (options.OnlyNonPublic && (fieldDecl == null || fieldDecl.Access == AccessModifier.Public))
                        {
                            continue;
                        }
                        // A field at its TYPE's default, when asked: what comes back for an absent key is that
                        // same value (DefaultValue(field.Type)), so the round trip is unchanged.
                        if (options.OmitDefaults && IsTypeDefault(kv.Value, fieldDecl?.Type)) continue;
                        var displayName = si.Declaration?.Properties != null
                            ? ResolvePropertyName(si.Declaration.Properties, kv.Key)
                            : kv.Key;
                        writer.WritePropertyName(displayName);
                        WriteJsonValue(writer, kv.Value, visited, options);
                    }
                    writer.WriteEndObject(); // close root
                    break;
                }

            // ── A callable: its SIGNATURE, the way `print` describes it, never its internals ──
            // A script function's public properties are its AST declaration and its closure snapshot, so the
            // reflection arm below dumped `ClosureSnapshot { LocalScopes = ... }` where the native beside it
            // answered its signature. One rule for every kind: a script function, a lambda, a bound method, a
            // native and a combined delegate all describe themselves the same way.
            //
            // With a type flag ON the payload also SAYS it held a callable: a bare signature string is
            // indistinguishable from a data string on the way back, so the marker is what makes the payload
            // self-describing. The signature travels in a named member (a JSON string has no slot of its own),
            // and the reader unwraps this wrapper (see UnwrapTaggedContainer), so the flag changes what the text
            // SAYS without changing the shape a read gives back.
            case ScriptFunction:
            case INativeFunction:
            case CombinedDelegate:
                if (options.ShowType || options.IncludeType)
                {
                    writer.WriteStartObject();
                    WriteTypeMember(writer, value, options);
                    writer.WritePropertyName("Signature");
                    writer.WriteStringValue(ValueFormatter.ToDisplay(value));
                    writer.WriteEndObject();
                    break;
                }
                writer.WriteStringValue(ValueFormatter.ToDisplay(value));
                break;

            // ── .NET arrays ──
            case Array arr:
                {
                    // string[], int[], …, this is where a resolved export list gets its tag.
                    var wrapped = WriteArrayStart(writer, value, options);
                    foreach (var item in arr)
                        WriteJsonValue(writer, item, visited, options);
                    WriteArrayEnd(writer, wrapped);
                    break;
                }

            // ── Non-script .NET collections (IList, IDictionary) ──
            case IList list:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var wrapped = WriteArrayStart(writer, value, options);
                    foreach (var item in list)
                        WriteJsonValue(writer, item, visited, options);
                    WriteArrayEnd(writer, wrapped);
                    break;
                }

            case IDictionary dict:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    writer.WriteStartObject();
                    WriteTypeMember(writer, value, options);
                    foreach (DictionaryEntry entry in dict)
                    {
                        var keyName = entry.Key.ToString() ?? "";
                        writer.WritePropertyName(keyName);
                        WriteJsonValue(writer, entry.Value, visited, options);
                    }
                    writer.WriteEndObject();
                    break;
                }

            // ── ScriptResult (RunScript return value) ──
            case ScriptResult sr:
                {
                    writer.WriteStartObject();
                    WriteTypeMember(writer, value, options);
                    writer.WritePropertyName("HasError");
                    writer.WriteBooleanValue(sr.HasError);
                    writer.WritePropertyName("ExitCode");
                    writer.WriteNumberValue(sr.ExitCode);
                    if (sr.HasError)
                    {
                        if (sr.ErrorMessage != null)
                        {
                            writer.WritePropertyName("ErrorMessage");
                            writer.WriteStringValue(sr.ErrorMessage);
                        }
                        if (sr.ErrorInfo is { } errInfo)
                        {
                            writer.WritePropertyName("ErrorInfo");
                            writer.WriteStartObject();
                            writer.WritePropertyName("Code");
                            writer.WriteNumberValue((int)errInfo.Code);
                            writer.WritePropertyName("Message");
                            writer.WriteStringValue(errInfo.Message);
                            writer.WriteEndObject();
                        }
                        writer.WritePropertyName("ErrorLine");
                        writer.WriteNumberValue(sr.ErrorLine);
                        writer.WritePropertyName("ErrorColumn");
                        writer.WriteNumberValue(sr.ErrorColumn);
                        if (sr.StackTrace != null)
                        {
                            writer.WritePropertyName("StackTrace");
                            writer.WriteStringValue(sr.StackTrace);
                        }
                    }
                    if (sr.ReturnValue != null)
                    {
                        writer.WritePropertyName("ReturnValue");
                        WriteJsonValue(writer, sr.ReturnValue, visited, options);
                    }
                    writer.WriteEndObject();
                    break;
                }

            // ── ClassInfo (type definitions, including enums) ──
            case ClassInfo ci:
                {
                    if (ci.StaticFields != null)
                    {
                        writer.WriteStartObject();
                        WriteTypeMember(writer, value, options);
                        foreach (var kv in ci.StaticFields)
                        {
                            writer.WritePropertyName(kv.Key);
                            WriteJsonValue(writer, kv.Value, visited, options);
                        }
                        writer.WriteEndObject();
                    }
                    else if (options.ShowType || options.IncludeType)
                    {
                        // A type with no static fields still names itself when the flag asks for a type marker:
                        // an enum's members are its static fields, so the branch above is where they go, and a
                        // class or struct definition otherwise answered a bare string with no marker slot.
                        writer.WriteStartObject();
                        writer.WritePropertyName("__type");
                        writer.WriteStringValue(ci.Name);
                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteStringValue(ci.Name);
                    }
                    break;
                }

            // ── Namespace: its SURFACE, because it is a structure rather than data ──
            // Every namespace used to fall through to the arm below and serialize as the CLR type name
            // ("Namespace"): a loaded library, a builtin and a nested child alike, because GetProperties over an
            // internal type found nothing to write. What a reader wants is what it HOLDS: the label it answers
            // to, its nested namespaces, and the names it answers as calls and as values. The label a load
            // registers is a PROPERTY, so it is listed among the property names and never walked into.
            case Namespace ns:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    writer.WriteStartObject();
                    WriteTypeMember(writer, value, options);
                    writer.WritePropertyName("Name");
                    if (string.IsNullOrEmpty(ns.Name)) writer.WriteNullValue();
                    else writer.WriteStringValue(ns.Name);
                    var childNames = ns.ChildNames();
                    if (childNames.Count > 0)
                    {
                        writer.WritePropertyName("Namespaces");
                        writer.WriteStartObject();
                        foreach (var childName in childNames)
                        {
                            writer.WritePropertyName(childName);
                            var child = ns.GetChild(childName);
                            if (child == null) writer.WriteNullValue();
                            else WriteJsonValue(writer, child, visited, options);
                        }
                        writer.WriteEndObject();
                    }
                    var callNames = ns.MethodNames();
                    if (callNames.Count > 0)
                    {
                        writer.WritePropertyName("Functions");
                        writer.WriteStartArray();
                        foreach (var callName in callNames) writer.WriteStringValue(callName);
                        writer.WriteEndArray();
                    }
                    var valueNames = ns.PropertyNames();
                    if (valueNames.Count > 0)
                    {
                        writer.WritePropertyName("Properties");
                        writer.WriteStartArray();
                        foreach (var valueName in valueNames) writer.WriteStringValue(valueName);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndObject();
                    break;
                }

            // ── Fallback: serialize public properties as JSON object ──
            default:
                {
                    if (!visited.Add(value)) { writer.WriteNullValue(); return; }
                    var type = value.GetType();
                    // Strip namespace and nested-type + notation for cleaner names
                    var cleanTypeName = type.Name;
                    if (type.IsNested)
                    {
                        var outer = type.DeclaringType?.Name ?? "";
                        cleanTypeName = outer.Contains("+") ? type.Name : outer + "." + type.Name;
                    }
                    // Script-facing name for __type, registry (ScriptNameOf) wins so the tag
                    // matches the script-visible name (e.g. "ErrorInfo", not "ScriptErrorInfo").
                    if (ScriptNameOf(type) is { } registeredName)
                        cleanTypeName = registeredName;
                    var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (props.Length > 0)
                    {
                        var isReconstructible = JsonTypeMap.ContainsKey(cleanTypeName) ||
                            JsonTypeMap.ContainsKey("Interpreter." + cleanTypeName);
                        // `includeType` and `showType` mean the same thing here, as they do in every arm that
                        // writes the marker: the flag says "name the type", not "wrap it in the type's name".
                        var shouldShowType = options.ShowType || options.IncludeType || isReconstructible;

                        writer.WriteStartObject();
                        if (shouldShowType)
                        {
                            writer.WritePropertyName("__type");
                            writer.WriteStringValue(cleanTypeName);
                        }
                        foreach (var prop in props)
                        {
                            if (!prop.CanRead) continue;
                            if (prop.GetIndexParameters().Length > 0) continue;
                            try
                            {
                                var propVal = prop.GetValue(value);
                                writer.WritePropertyName(prop.Name);
                                WriteJsonValue(writer, propVal, visited, options);
                            }
                            catch { /* skip properties that throw */ }
                        }
                        writer.WriteEndObject();
                    }
                    else
                    {
                        // A value with no properties to write used to serialize as its CLR type name, which is
                        // the one kind of name the engine keeps out of a script's sight. It is the DISPLAY text
                        // now, so `Json.Serialize` agrees with `print` and `+` about what the value is.
                        writer.WriteStringValue(ValueFormatter.ToDisplay(value));
                    }
                    break;
                }
        }
    }

    /// <summary>Parse a JSON string into interpreter-compatible values (ScriptBoxedDictionary, lists, primitives).</summary>
    internal static object? ParseJsonValue(ref Utf8JsonReader reader, TypeInfo? keyType = null, TypeInfo? valueType = null)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.False => false,
            JsonTokenType.True => true,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? (object)l : reader.GetDouble(),
            JsonTokenType.StartObject => ParseJsonObject(ref reader),
            JsonTokenType.StartArray => ParseJsonArray(ref reader),
            _ => throw new ScriptException($"Unexpected JSON token: {reader.TokenType}")
        };
    }

    /// <summary>Parses a JSON object. It returns the VALUE rather than the dictionary because a
    /// tagged CONTAINER is unwrapped here: <c>{"__type":"string[]","Values":[…]}</c> is the writer's
    /// wrapper for a JSON array, which has no member slot of its own, the value the caller serialized
    /// is <c>Values</c>, so handing the wrapper back as an object would change the shape of every
    /// <c>Json.Parse(Json.Serialize(x, {showType:true}))</c>. The result is NULLABLE because unwrapping can
    /// legitimately yield nothing (a JSON null inside the wrapper), which the caller's own return type already
    /// allows, so the honest declaration is <c>object?</c> rather than a suppression at the return.</summary>
    private static object? ParseJsonObject(ref Utf8JsonReader reader)
    {
        var result = new ScriptBoxedDictionary(
            new TypeInfo(TokenType.String, false, 0, 0, null),
            new TypeInfo(TokenType.Object, true, 0, 0, null));
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new ScriptException("Expected property name in JSON object.");

            var key = reader.GetString() ?? "";
            reader.Read();
            result.AddRaw(key, ParseJsonValue(ref reader));
        }

        if (IsTaggedWrapper(result, out var wrapperType))
            return UnwrapTaggedContainer(result, wrapperType, reconstruct: false, caseSensitive: true); // unused when not reconstructing

        StripDictionaryMarker(result);
        return result;
    }

    /// <summary>Strips a DICTIONARY's container marker from a parsed object. A container-shaped value writes
    /// <c>{"__type":"List"|"HashSet"|"Dictionary","Values":[…]}</c>, the wrapper an array needs because a JSON
    /// array has no member slot of its own, while a DICTIONARY writes the same marker with its ENTRIES as the
    /// members instead. The reader consumed the wrapper's marker and never the dictionary's, measured: a tagged
    /// dictionary read back with <c>__type</c> as a THIRD entry, and a typed read of it threw "Cannot convert
    /// string 'Dictionary' to int". A member is metadata only when the object is NOT a wrapper and its
    /// <c>__type</c> names a container, which is the one rule both parsers ask.</summary>
    private static void StripDictionaryMarker(ScriptBoxedDictionary dict)
    {
        if (dict.ContainsKey("Values")) return;      // a wrapper keeps its marker: UnwrapTaggedContainer needs it
        if (!dict.ContainsKey("__type")) return;
        if (dict["__type"] is not string tag || !IsContainerTypeName(tag)) return;
        dict.Remove("__type");
    }

    private static object ParseJsonArray(ref Utf8JsonReader reader)
    {
        var result = new ScriptBoxedList(new TypeInfo(TokenType.Object, true, 0, 0, null));
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var item = ParseJsonValue(ref reader);
            result.AddRaw(item);
        }
        return result;
    }

    /// <summary>Parse a JsonDocument's RootElement into interpreter-compatible values.</summary>
    internal static object? ParseJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.False => false,
            JsonValueKind.True => true,
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
            JsonValueKind.Object => ParseJsonElementObject(element),
            JsonValueKind.Array => ParseJsonElementArray(element),
            _ => element.GetRawText()
        };
    }

    /// <summary>Parses a JSON object out of a JsonDocument. It returns the VALUE rather than the dictionary for the
    /// SAME reason the streaming parser does (see <see cref="ParseJsonObject"/>): a tagged wrapper is the writer's
    /// way of putting a type name on a value that has no member slot for it (a JSON array carries its items under
    /// <c>Values</c>, which is what the caller serialized, and a callable carries its signature string under
    /// <c>Signature</c>). Handing the wrapper back changes the shape of every
    /// <c>Json.Parse(Json.Serialize(x, {showType:true}))</c>, and the two parsers must agree: measured,
    /// <c>Json.Parse</c> of that text unwrapped it while <c>Json.FromFile</c> of the SAME text returned the
    /// wrapper, and a class field of a container type came back a wrapper dictionary through
    /// <c>Json.RoundTrip</c> (the typed <c>Json.Deserialize(T, …)</c> rebuilds it, which is the disagreement this
    /// removes). The result is NULLABLE because unwrapping can legitimately yield nothing.</summary>
    private static object? ParseJsonElementObject(JsonElement element)
    {
        var result = new ScriptBoxedDictionary(
            new TypeInfo(TokenType.String, false, 0, 0, null),
            new TypeInfo(TokenType.Object, true, 0, 0, null));
        foreach (var prop in element.EnumerateObject())
            result.AddRaw(prop.Name, ParseJsonElement(prop.Value));

        if (IsTaggedWrapper(result, out var wrapperType))
            return UnwrapTaggedContainer(result, wrapperType, reconstruct: false, caseSensitive: true);

        StripDictionaryMarker(result);
        return result;
    }

    private static object ParseJsonElementArray(JsonElement element)
    {
        var result = new ScriptBoxedList(new TypeInfo(TokenType.Object, true, 0, 0, null));
        foreach (var val in element.EnumerateArray().Select(ParseJsonElement).OfType<object>())
        {
            result.AddRaw(val);
        }
        return result;
    }

    /// <summary>Deserialize a parsed JSON dictionary into a ClassInstance or StructInstance.
    ///
    /// <paramref name="caseSensitive"/> has NO default on purpose: it is the interpreter's case flag (or the
    /// caller's explicit override), and a defaulted parameter is how a path silently loses it, which is how this
    /// file came to accept a differently-cased JSON key while the reference documented strict matching.</summary>
    internal static object? DeserializeFromDict(object? parsed, string typeName, bool caseSensitive)
    {
        // Handle primitive types serialized as bare JSON values (no __type dict)
        if (parsed is not ScriptBoxedDictionary dict)
        {
            var primResult = DeserializePrimitive(parsed, typeName);
            if (primResult != null) return primResult;
            return parsed; // can't reconstruct, return raw value
        }

        var itp = Current;
        if (itp == null) return dict;
        var scope = itp.CurrentFrame.Scopes;

        // Build a key lookup with the requested case sensitivity
        var keyLookup = caseSensitive
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in dict.Keys())
            if (key is string ks)
                keyLookup[ks] = dict[key];
        keyLookup.Remove("__type");

        // Try class first. THE SCOPE IS ONE DOOR AND THE REGISTRY IS THE OTHER: an engine-invoked body (a route
        // handler, a `Com.Run` callback, an event handler) runs on a closure snapshot that need not hold the class
        // NAMES the file declared, so resolving only through the scope answered "unknown type name 'Row'" for a
        // class of the same file when the same read was performed from a route (measured, through
        // `Avro.ReadContainer(Row, ctx.RequestBytes)`). The registry is where a declared class actually lives, and
        // it uses the interpreter's own comparer.
        var classInfo = scope.TryGet(typeName, out var typeObj) && typeObj is ClassInfo fromScope
            ? fromScope
            : itp.Classes.TryGetValue(typeName, out var fromRegistry) ? fromRegistry : null;
        if (classInfo is { } ci)
        {
            var fieldValues = new Dictionary<string, object?>(ci.Fields.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var field in ci.Fields)
            {
                if (field.IsStatic) continue;
                fieldValues[field.Name] = DefaultValue(field.Type);
                // Try exact field name first, then resolved property name (backing ↔ display)
                if (keyLookup.TryGetValue(field.Name, out var jsonVal))
                    fieldValues[field.Name] = DeserializeFieldValue(jsonVal, field.Type, caseSensitive);
                else if (ci.Properties != null)
                {
                    var propName = ResolvePropertyName(ci.Properties, field.Name);
                    if (propName != field.Name && keyLookup.TryGetValue(propName, out var pv))
                        fieldValues[field.Name] = DeserializeFieldValue(pv, field.Type, caseSensitive);
                }
            }
            return new ClassInstance(ci, fieldValues, new Dictionary<string, EventInfo>());
        }

        // Try struct from _structs dictionary (strip # prefix if present)
        var structName = typeName.StartsWith("#") ? typeName[1..] : typeName;
        if (itp.Structs.TryGetValue(structName, out var structDecl))
        {
            var fields = new Dictionary<string, object?>(structDecl.Fields.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var f in structDecl.Fields)
            {
                fields[f.Name] = DefaultValue(f.Type);
                if (keyLookup.TryGetValue(f.Name, out var jsonVal))
                    fields[f.Name] = DeserializeFieldValue(jsonVal, f.Type, caseSensitive);
                else if (structDecl.Properties != null)
                {
                    var sPropName = ResolvePropertyName(structDecl.Properties, f.Name);
                    if (sPropName != f.Name && keyLookup.TryGetValue(sPropName, out var spv))
                        fields[f.Name] = DeserializeFieldValue(spv, f.Type, caseSensitive);
                }
            }
            return new StructInstance(structDecl.Name, fields, structDecl.Line) { Declaration = structDecl };
        }

        // ── Resolve __type to a reconstructible CLR type ──
        if (!JsonTypeMap.TryGetValue(typeName, out var clrType))
        {
            // Also try stripping "Interpreter." prefix for nested types
            var stripped = typeName.StartsWith("Interpreter.") ? typeName["Interpreter.".Length..] : null;
            if (stripped != null) JsonTypeMap.TryGetValue(stripped, out clrType);
            // Also try adding "Interpreter." prefix for short names
            if (clrType == null) JsonTypeMap.TryGetValue("Interpreter." + typeName, out clrType);
        }

        if (clrType == null) return dict;
        {
            try
            {
                var instance = Activator.CreateInstance(clrType);
                if (instance == null) return dict;

                var clrProps = clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var prop in clrProps)
                {
                    if (!prop.CanWrite) continue;
                    if (!keyLookup.TryGetValue(prop.Name, out var jsonVal) || jsonVal == null) continue;
                    var converted = ConvertJsonValueToPropType(jsonVal, prop.PropertyType, caseSensitive);
                    if (converted != null)
                        prop.SetValue(instance, converted);
                }
                return instance;
            }
            catch
            {
                // Type not found, return raw dictionary silently
                return dict;
            }
        }
    }

    private static object? ConvertJsonValueToPropType(object? val, Type targetType, bool caseSensitive)
    {
        switch (val)
        {
            case null:
                return null;
            // ScriptBoxedDictionary with __type → try nested reconstruction first
            // (even when target is object, handles nested objects in arrays/dicts)
            case ScriptBoxedDictionary subDict when subDict.ContainsKey("__type") && subDict["__type"] is string tn:
                {
                    // The SAME comparer the outer keyLookup uses: this is the nested half of one rule, and it hardcoded
                    // OrdinalIgnoreCase while the outer lookup followed the interpreter's flag.
                    var subKeys = new Dictionary<string, object?>(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                    foreach (var k in subDict.Keys())
                        if (k is string ks) subKeys[ks] = subDict[k];
                    subKeys.Remove("__type");

                    if (JsonTypeMap.TryGetValue(tn, out var nestedType))
                    {
                        try
                        {
                            var nested = Activator.CreateInstance(nestedType);
                            if (nested == null) return val;
                            var nestedProps = nestedType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                            foreach (var np in nestedProps)
                            {
                                if (!np.CanWrite) continue;
                                if (!subKeys.TryGetValue(np.Name, out var nv) || nv == null) continue;
                                var converted = ConvertJsonValueToPropType(nv, np.PropertyType, caseSensitive);
                                if (converted != null) np.SetValue(nested, converted);
                            }
                            return nested;
                        }
                        catch { /* fall through to assignable check */ }
                    }

                    break;
                }
        }

        // Direct assignable
        if (targetType.IsInstanceOfType(val)) return val;

        // Numeric widening (JSON deserializer returns long for all integers)
        var coerced = NumericTypeConverter.CoerceToType(val, targetType);
        if (coerced != null) return coerced;

        // double → decimal
        if (targetType == typeof(decimal) && val is double d) return (decimal)d;

        // String → Enum
        if (targetType.IsEnum && val is string s)
        {
            // An enum MEMBER name is a name: tolerant only where the language is (`.lax`, or an explicit override).
            try { return Enum.Parse(targetType, s, ignoreCase: !caseSensitive); }
            catch { return val; }
        }

        // ScriptBoxedDictionary → nested CLR object (recursive)
        // (handled at the top of this method, duplicate block removed)
        // Intentionally empty: the __type check at the top handles all nested reconstruction.
        // Fall through to List<T> reconstruction below.

        // IScriptList → List<T> reconstruction
        // IScriptList → List<T> reconstruction. The target may be `List<T>` OR one of the collection shapes a CLR type
        // actually declares: a `List<T>` satisfies IList<T>, IReadOnlyList<T>, ICollection<T> and IEnumerable<T>, so ONE
        // construction serves them all. Measured: `ErrorInfo.Frames` is `IReadOnlyList<string>`, the conversion handed
        // the script list back unchanged, `PropertyInfo.SetValue` threw, and the whole object SILENTLY fell back to its
        // raw dictionary (so an error round-tripped to a Dictionary instead of an ErrorInfo as soon as it carried
        // Frames, which a bundle's error always does).
        // IScriptList → List<T> reconstruction. The target may be `List<T>` OR one of the collection shapes a CLR type
        // actually declares: a `List<T>` satisfies IList<T>, IReadOnlyList<T>, ICollection<T> and IEnumerable<T>, so ONE
        // construction serves them all. Measured: `ErrorInfo.Frames` is `IReadOnlyList<string>`, the conversion handed
        // the script list back unchanged, `PropertyInfo.SetValue` threw, and the whole object SILENTLY fell back to its
        // raw dictionary (an error round-tripped to a Dictionary instead of an ErrorInfo as soon as it carried Frames,
        // which a bundle's error always does: 4 parity entries, one cause).
        if (val is not IScriptList list || !targetType.IsGenericType) return val;
        var listDef = targetType.GetGenericTypeDefinition();
        if (listDef != typeof(List<>) && listDef != typeof(IList<>) && listDef != typeof(IReadOnlyList<>)
            && listDef != typeof(ICollection<>) && listDef != typeof(IReadOnlyCollection<>)
            && listDef != typeof(IEnumerable<>)) return val;
        {
            var elemType = targetType.GetGenericArguments()[0];
            var listType = typeof(List<>).MakeGenericType(elemType);
            var ilist = Activator.CreateInstance(listType) as IList;
            if (ilist == null) return val;
            foreach (var item in list.ToArray())
            {
                var converted = ConvertJsonValueToPropType(item, elemType, caseSensitive) ?? item;
                ilist.Add(converted);
            }
            return ilist;
        }

    }

    /// <summary>Deserialize one field's value. Like <see cref="DeserializeFromDict"/>, the case flag has NO
    /// default: it is the interpreter's flag (or the caller's override) and must reach every nested lookup.</summary>
    private static object? DeserializeFieldValue(object? val, TypeInfo fieldType, bool caseSensitive)
    {
        if (fieldType.TypeName != null && val is ScriptBoxedDictionary subDict)
            return DeserializeFromDict(subDict, fieldType.TypeName, caseSensitive);
        if (fieldType is { GenericArgs.Count: 1, BaseType: TokenType.Object, TypeName: "List" } && val is IScriptList list)
        {
            var elemType = fieldType.GenericArgs[0];
            var result = new ScriptBoxedList(elemType);
            foreach (var item in list.ToArray())
                result.AddRaw(DeserializeFieldValue(item, elemType, caseSensitive) ?? item);
            return result;
        }
        // An ARRAY-typed field: the writer tags it {"__type":"int[]","Values":[...]}, the parser unwraps that to the
        // raw list, and a list is not the declared shape. CoerceTo owns the list-to-typed-array rule for a
        // PARAMETER (a `string[]` argument), and this is the same rule for a field, so `int[] Arr` comes back an
        // int[] and not a List. The engine spells `int[]` as JaggedDepth 1, so ANY depth is taken and the element
        // conversion recurses one level down: an `int[][]` builds its inner `int[]` elements through this same arm.
        // The element's CLR type is the element TypeInfo's when it has one, and otherwise the first converted
        // element's own type (a nested array), so a jagged field is not flattened to object[]. A script class has no
        // CLR type, so a `Line[]` field keeps the list, which is what its array literal gives it.
        if (val is IScriptList arrayItems && fieldType is { ArrayRank: 0, JaggedDepth: >= 1 })
        {
            var elementInfo = fieldType with { JaggedDepth = fieldType.JaggedDepth - 1 };
            var converted = new object?[arrayItems.Count];
            for (var i = 0; i < converted.Length; i++)
                converted[i] = DeserializeFieldValue(arrayItems[i], elementInfo, caseSensitive);
            var elementClr = TypeTokenHelper.GetSystemType(elementInfo)
                ?? (converted.FirstOrDefault(v => v is not null)?.GetType())
                ?? typeof(object);
            var built = Array.CreateInstance(elementClr, converted.Length);
            for (var i = 0; i < built.Length; i++)
                built.SetValue(converted[i], i);
            return built;
        }
        // Coerce primitives to match the declared field type (JSON parser returns long for all ints)
        if (val is not long lVal) return val;
        return NumericTypeConverter.FromLongByToken.TryGetValue(fieldType.BaseType, out var converter)
            ? converter(lVal) : val;
    }

    /// <summary>The tagged WRAPPER a JSON-shaped value carries while a type flag is on: an array gets
    /// <c>{"__type":"int[]","Values":[…]}</c> because a JSON array has no member slot for a tag, and a callable
    /// gets <c>{"__type":"Function","Signature":"…"}</c> because its value is a plain string. ONE predicate, asked
    /// by all three readers (the streaming parser, the JsonDocument parser and <see cref="ReconstructNested"/>),
    /// which is what stops two of them disagreeing about the same text. The callable half is here rather than in a
    /// second check at each site, because a marked callable read back as a bare dictionary until they shared it.</summary>
    private static bool IsTaggedWrapper(ScriptBoxedDictionary dict, out string wrapperType)
    {
        wrapperType = "";
        if (!dict.ContainsKey("__type") || dict["__type"] is not string tag) return false;
        var isCallable = tag.Equals("Function", StringComparison.Ordinal);
        if (!isCallable && !IsContainerTypeName(tag)) return false;
        if (!dict.ContainsKey(isCallable ? "Signature" : "Values")) return false;
        wrapperType = tag;
        return true;
    }

    /// <summary>True for the type names the writer uses for a JSON array, <c>string[]</c>,
    /// <c>List&lt;int&gt;</c>, <c>List</c>, <c>Tuple</c>, <c>Dictionary</c>, <c>HashSet&lt;…&gt;</c>. It has to
    /// agree with <see cref="GetFriendlyTypeName"/>, which is where those names come from, and it is the
    /// check that keeps a class instance with a field literally called <c>Values</c> from being mistaken
    /// for a tagged array.</summary>
    /// <summary>The tagged container, <c>{"__type":"…","Values":[…]}</c>, which is how an array-shaped
    /// value is written, since a JSON array has no member slot for a tag. Usually the value inside is the
    /// answer, but a TUPLE is array-shaped without being a list: its shape is the tuple, so it is rebuilt as
    /// one, labels included when the writer recorded them. One rule, used by the parser and by
    /// ReconstructNested rather than written twice.</summary>
    private static object? UnwrapTaggedContainer(ScriptBoxedDictionary dict, string containerType, bool reconstruct,
        bool caseSensitive)
    {
        // A CALLABLE: the marker says the value WAS a function and the signature travels in a named member beside
        // it (a JSON string has no slot of its own). Unwrapping it here is what keeps a type flag shape-preserving;
        // without this arm a marked callable read back as a bare dictionary, which is the failure the flag's own
        // comment warns about for a scalar.
        if (containerType.Equals("Function", StringComparison.Ordinal) && dict.ContainsKey("Signature"))
            return dict["Signature"];

        var tagged = dict["Values"];
        if (containerType.Equals("Tuple", StringComparison.Ordinal) && tagged is IScriptList items)
        {
            var elements = new object?[items.Count];
            for (var i = 0; i < elements.Length; i++)
                elements[i] = reconstruct ? ReconstructNested(items[i], caseSensitive) : items[i];
            string[]? names = null;
            if (dict.ContainsKey("Names") && dict["Names"] is IScriptList nameList)
            {
                // All-or-nothing: a tuple's element names are either all there or the tuple has none. Keeping a
                // partial array would put a NULL element name on the tuple, which every reader would then have to
                // defend against, and it was already what `Names` has to be typed for.
                var allNames = new string[nameList.Count];
                var complete = true;
                for (var i = 0; i < allNames.Length; i++)
                {
                    if (nameList[i] is not string n) { complete = false; break; }
                    allNames[i] = n;
                }
                if (complete) names = allNames;
            }
            return new ScriptTuple(elements) { Names = names };
        }
        return reconstruct ? ReconstructNested(tagged, caseSensitive) : tagged;
    }

    private static bool IsContainerTypeName(string typeName)
        => typeName.EndsWith("[]", StringComparison.Ordinal)
        || typeName.Equals("List", StringComparison.Ordinal)
        || typeName.Equals("Dictionary", StringComparison.Ordinal)
        || typeName.Equals("HashSet", StringComparison.Ordinal)
        || typeName.Equals("Tuple", StringComparison.Ordinal)
        || typeName.StartsWith("List<", StringComparison.Ordinal)
        || typeName.StartsWith("Dictionary<", StringComparison.Ordinal)
        || typeName.StartsWith("HashSet<", StringComparison.Ordinal);

    /// <summary>Parses a JSON string or UTF-8 byte array into interpreter values; anything else comes back
    /// as-is (it has already been parsed, e.g. by Json.Parse).</summary>
    internal static object? ParseJsonInput(object? input)
    {
        switch (input)
        {
            case string s:
                {
                    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(s));
                    if (!reader.Read()) throw new ScriptException("Empty or invalid JSON.");
                    return ParseJsonValue(ref reader);
                }
            case byte[] bytes:
                {
                    var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(bytes));
                    return !reader.Read()
                        ? throw new ScriptException("Empty or invalid JSON.") : ParseJsonValue(ref reader);
                }
            default:
                return input;
        }
    }

    /// <summary>What a type name asks of the payload: an indexable sequence, a keyed object, a set built
    /// from a sequence, or, <c>Iterable</c>/<c>IEnumerable</c>, nothing in particular, since those are the
    /// collection predicates and a mapping satisfies them like a sequence does.</summary>
    internal enum ContainerKind { None, List, Dictionary, Set, Iterable, Tuple }

    /// <summary>Classifies a type name into the shape it requires: <c>List</c>, <c>List&lt;T&gt;</c> and
    /// <c>T[]</c> want a JSON array; <c>Dictionary</c> and <c>Dictionary&lt;K,V&gt;</c> want a JSON object;
    /// anything else (a class, a primitive) is <see cref="ContainerKind.None"/> and takes the mapping path.
    ///
    /// <c>Iterable</c> and <c>IEnumerable</c> accept either shape, because that is what those types mean
    /// everywhere else in the language (<c>v is Iterable</c> is true for a list and for a dictionary alike).
    /// <c>IList</c> does not: it promises indexing, which a JSON object cannot give.
    ///
    /// <c>HashSet</c> is a set, so an array is required and what comes back is built as one.</summary>
    internal static ContainerKind ContainerKindOf(string typeName) => ContainerKindOf(typeName.AsSpan());

    /// <summary>Reads the name as a span. This runs once per ELEMENT on the lazy path, a 200k-element
    /// List<int> calls it 200k times, so it allocates nothing: Trim and the head slice are views, not
    /// strings. The string-overload above keeps the public shape for every other caller.</summary>
    private static ContainerKind ContainerKindOf(ReadOnlySpan<char> name)
    {
        name = name.Trim();
        if (name.IsEmpty) return ContainerKind.None;
        // A tuple is positional, the writer emits [1,"a"] and tags it "Tuple" under showType, so a JSON
        // array is its input. Two spellings arrive here: the tag's own name, and the structural one that a
        // type argument carries, e.g. (string s, int i).
        if (name.Equals("Tuple", StringComparison.OrdinalIgnoreCase) || name.StartsWith('('))
            return ContainerKind.Tuple;
        if (name.Length >= 2 && name[^1] == ']' && name[^2] == '[') return ContainerKind.List;
        var head = name;
        var lt = name.IndexOf('<');
        if (lt > 0) head = name[..lt];
        if (head.Equals("List", StringComparison.OrdinalIgnoreCase)) return ContainerKind.List;
        if (head.Equals("IList", StringComparison.OrdinalIgnoreCase)) return ContainerKind.List;
        if (head.Equals("Dictionary", StringComparison.OrdinalIgnoreCase)) return ContainerKind.Dictionary;
        if (head.Equals("HashSet", StringComparison.OrdinalIgnoreCase)) return ContainerKind.Set;
        if (head.Equals("Iterable", StringComparison.OrdinalIgnoreCase)) return ContainerKind.Iterable;
        if (head.Equals("IEnumerable", StringComparison.OrdinalIgnoreCase)) return ContainerKind.Iterable;
        return ContainerKind.None;
    }

    /// <summary>Deserializes an already-parsed payload into <paramref name="typeName"/>, the ONE typed path,
    /// behind both <c>Json.Deserialize("Name", json)</c> and <c>Json.Deserialize&lt;T&gt;(json)</c>.
    ///
    /// The name is not advisory, and on the array path it used not to be read at all: a request for
    /// <c>"Dictionary&lt;string, int&gt;"</c> over an array came back a list, and a nonsense name behaved
    /// exactly like a correct one. A name that cannot agree with the payload, or that resolves to nothing, is
    /// an error now instead of a silent surprise.</summary>
    internal static object? DeserializeTyped(object? parsed, string typeName, bool caseSensitive, TypeInfo? typeInfo = null)
    {
        var kind = ContainerKindOf(typeName);

        // `object` is the ONE name that IMPOSES NOTHING: the parsed value IS the answer, which is the rule the
        // dictionary value-name handling already states ("object is the only one that means leave it alone").
        // Without this arm the name reached the unknown-name check, so every element of a `List<object>` was
        // refused with "the payload is a JSON string, which cannot become 'object'" while the untyped read of the
        // same payload is exactly what the caller asked for. The nullable mark is the same one a declared element
        // keeps, so `object?` is the same name.
        var bareName = typeName.EndsWith('?') ? typeName[..^1].TrimEnd() : typeName;
        if (bareName.Equals("object", caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            return parsed;

        // A BLANK payload arrives as null: `Json.Deserialize(name, "")` is an EMPTY payload, not a parse failure
        // (the caller answers null for blank text instead of calling the reader). The answer is the EMPTY value of
        // the shape the NAME asks for, so `Json.Deserialize("List<int>", "").ToList()` is an empty list rather
        // than an exception a script cannot act on. A list is the shape with an empty form here; a scalar, a
        // dictionary and a class name answer null, which is "no payload, no value" and is what a caller can test.
        if (parsed is null && kind == ContainerKind.List)
            return new ScriptBoxedList(
                ScalarKeywordType(ElementTypeNameOf(typeName)) ?? new TypeInfo(TokenType.Object, true, 0, 0, null));

        // A scalar keyword name (int, string, bool…) is a legitimate target that imposes nothing, because the
        // JSON parser has already produced long/double/bool/string. Without this arm the name reached the
        // unknown-name check below, which only knows classes, lists and dictionaries, so that check
        // rejected Dictionary<string, int>, the elements of List<int>, and every other scalar spelling.
        if (kind == ContainerKind.None && ScalarKeywordType(typeName) is { } scalarTarget)
        {
            // CoerceTo is the engine's one conversion, the same one behind Result.As<T>(), so imposing int
            // on a JSON number follows the same rules everywhere (1.5 rounds to 2, 70000 wraps into short).
            return CoerceTo(parsed, scalarTarget, 0, 0);
        }

        // A set is built from the sequence, not viewed lazily as one: the elements are boxed, because a name
        // alone says nothing about their type (the engine's unboxed sets need an element TypeInfo).
        if (kind == ContainerKind.Set)
        {
            if (parsed is not IScriptList setSource)
                throw new ScriptException(
                    $"Json.Deserialize: '{typeName}' names a set, but the payload is a JSON object. "
                    + "A set comes from a JSON array.");
            // A declared element type is imposed, the same as everywhere else; a bare "HashSet" says nothing
            // to impose, so its elements pass through and its storage stays object-typed. A declared SCALAR
            // element gives the set a real element type, which is what the engine's unboxed sets need.
            var setElementName = ElementTypeNameOf(typeName);
            var imposeSetElement = setElementName.Length > 0
                && !setElementName.Equals(typeName, StringComparison.Ordinal);
            var set = CreateScriptHashSet(imposeSetElement
                ? ScalarKeywordType(setElementName) ?? new TypeInfo(TokenType.Object, true, 0, 0, null)
                : new TypeInfo(TokenType.Object, true, 0, 0, null));
            foreach (var element in setSource.ToArray())
            {
                if (element is null)
                    throw new ScriptException("Json.Deserialize: a set cannot hold null.");
                // The guard above covers the payload, but a TYPED deserialize can return null of its own (a JSON
                // null), which would slip a null into the set through a path that guard never sees. Same rule,
                // same message, applied to the value that actually goes in.
                var setValue = imposeSetElement ? DeserializeTyped(element, setElementName, caseSensitive) : element;
                if (setValue is null)
                    throw new ScriptException("Json.Deserialize: a set cannot hold null.");
                set.AddRaw(setValue);
            }
            return set;
        }

        // A tuple: the declared element types are imposed per position when the caller had a TypeInfo (the
        // generic spelling does; a string name carries only what it spells, so its elements pass through).
        if (kind == ContainerKind.Tuple)
        {
            if (parsed is not IScriptList tupleItems)
                throw new ScriptException(
                    $"Json.Deserialize: '{typeName}' names a tuple, but the payload is a JSON object. "
                    + "A tuple comes from a JSON array.");
            var specs = TupleElementSpecsOf(typeName);
            var tupleValues = new object?[tupleItems.Count];
            var tupleNames = new string?[tupleItems.Count];
            for (var i = 0; i < tupleValues.Length; i++)
            {
                var spec = i < specs.Count ? specs[i] : default;
                tupleValues[i] = string.IsNullOrEmpty(spec.Type)
                    ? tupleItems[i]
                    : DeserializeTyped(tupleItems[i], spec.Type, caseSensitive);
                tupleNames[i] = spec.Name;
            }
            // All-or-nothing, the same rule UnwrapTaggedContainer applies: ANY name present used to hand over the
            // whole array INCLUDING its nulls, which is not a state a reader should ever have to expect.
            string[]? cleanNames = null;
            if (tupleNames.Any(n => n != null))
            {
                var allNames = new string[tupleNames.Length];
                var complete = true;
                for (var i = 0; i < allNames.Length; i++)
                {
                    if (tupleNames[i] is not string n) { complete = false; break; }
                    allNames[i] = n;
                }
                if (complete) cleanNames = allNames;
            }
            return new ScriptTuple(tupleValues) { Names = cleanNames };
        }

        // Array of typed elements: Deserialize(Point, '[{"X":1},{"X":2}]') → lazy List<Point>
        if (parsed is IScriptList arr)
        {
            if (kind == ContainerKind.Dictionary)
                throw new ScriptException(
                    $"Json.Deserialize: the payload is a JSON array, but '{typeName}' names a dictionary.");
            if (kind == ContainerKind.None)
            {
                // A CLASS over an array is the documented array-of-that-type form:
                // Deserialize("Person", '[{"Name":"a"}]') → a lazy List<Person>. Only an UNKNOWN name is an
                // error, so ask the resolver itself instead of keeping a second list of what counts as a
                // type: DeserializeFromDict hands the payload back untouched when nothing maps the name,
                // the same oracle the object path below uses.
                var sample = arr.Count > 0 ? arr[0] : null;
                var resolved = sample is null ? null : DeserializeFromDict(sample, typeName, caseSensitive);
                if (sample is null || ReferenceEquals(resolved, sample))
                    throw new ScriptException(
                        $"Json.Deserialize: the payload is a JSON array, which cannot become '{typeName}'. "
                        + "For an array payload name a list (List, List<T>, T[]) or a class whose items are "
                        + "JSON objects.");
            }
            return new ScriptLazyDeserializedList(arr, ElementTypeNameOf(typeName), caseSensitive, typeName);
        }

        if (kind == ContainerKind.List)
            throw new ScriptException(
                $"Json.Deserialize: '{typeName}' names a list, but the payload is a JSON object.");

        // A dictionary name imposes its VALUE type on every value, the same rule the generic form follows
        // everywhere else. DeserializeFromDict has no Dictionary arm, so it returned the parsed object as-is
        // and each value stayed whatever the parser made of it, which is why Dictionary<string, Person> used
        // to hand back a Dictionary per value while Person worked on its own. The recursion imposes whatever
        // the value name says, a class becomes a class, a scalar keyword is coerced to it, so there is no
        // separate list of value names that "need nothing": object is the only one that means leave it alone.
        if (kind == ContainerKind.Dictionary && parsed is ScriptBoxedDictionary dictPayload)
        {
            var valueName = DictionaryValueTypeNameOf(typeName);
            if (valueName != null && !valueName.Equals("object", StringComparison.OrdinalIgnoreCase))
                foreach (var key in dictPayload.Keys().ToList())
                    if (key is string ks)
                        dictPayload[ks] = DeserializeTyped(dictPayload[ks], valueName, caseSensitive);
            return dictPayload;
        }

        if (kind == ContainerKind.None && parsed is not IScriptList && parsed is not ScriptBoxedDictionary)
        {
            // A scalar payload that is not a scalar keyword is either a convertible named type (DateTime,
            // TimeSpan, Guid, Uri…) or a name it simply cannot become. Saying "unknown type name" for the
            // second case blamed the name, `Json.Deserialize("Person", 5)` reported Person as unknown.
            if (parsed is null) return null;
            var convertible = DeserializePrimitive(parsed, typeName);
            if (convertible != null) return convertible;
            throw new ScriptException(
                $"Json.Deserialize: the payload is {DescribePayload(parsed)}, which cannot become "
                + $"'{typeName}'. Name a scalar keyword, int, long, double, string, bool, char…, for a "
                + "scalar payload, or pass JSON text.");
        }

        var mapped = DeserializeFromDict(parsed, typeName, caseSensitive);

        // DeserializeFromDict hands the payload back untouched when the name resolves to nothing, which is
        // exactly the unknown-name case, so no second resolver is needed to tell the two apart.
        if (ReferenceEquals(mapped, parsed) && kind == ContainerKind.None)
            throw new ScriptException(
                $"Json.Deserialize: unknown type name '{typeName}', nothing in scope maps it. Name a class, "
                + "a List or Dictionary, or a type registered for round-trip.");

        return mapped;
    }

    /// <summary>What a payload is, for a message. The message used to call everything that was not a string
    /// or a bool "a JSON number", so an instance or a tuple was reported as a number.</summary>
    private static string DescribePayload(object? parsed) => parsed switch
    {
        null => "null",
        string => "a JSON string",
        bool => "a JSON boolean",
        IScriptList => "a JSON array",
        ScriptBoxedDictionary => "a JSON object",
        _ when TypeSystem.IsNumeric(TypeSystem.TokenTypeOf(parsed.GetType())) => "a JSON number",
        _ => "not JSON text",
    };

    /// <summary>The ELEMENT name inside a list name: <c>List&lt;Person&gt;</c> and <c>Person[]</c> both name
    /// <c>Person</c>; anything else names itself. The lazy list maps each ITEM through this name, so handing
    /// it the whole container name left every item a dictionary, it was asking for a type called
    /// "List&lt;Person&gt;", which nothing maps. Values survived it by accident (an int is already an int),
    /// which is why only a nested CLASS showed the difference.</summary>
    private static string ElementTypeNameOf(string typeName)
    {
        var t = typeName.AsSpan().Trim();
        if (t.Length >= 2 && t[^1] == ']' && t[^2] == '[') return t[..^2].Trim().ToString();
        var open = t.IndexOf('<');
        if (open > 0 && t[^1] == '>') return t.Slice(open + 1, t.Length - open - 2).Trim().ToString();
        // A name with no element argument is the ELEMENT type when it is not a CONTAINER name: a CLASS named over
        // a JSON array says "each item is that class", which is the shape this echo exists for. A CONTAINER name
        // with no argument (`List`, `Dictionary`, `HashSet`) names the container and not its elements, so the
        // element type is UNKNOWN and must not be read as the container itself: returning the name here is what
        // made a lazy `List` read each of its ELEMENTS as a List, so serializing one threw "'List' names a list,
        // but the payload is a JSON object" (measured on a `List<string?>`, whose runtime list is BOXED and
        // therefore reports the bare name).
        return IsContainerTypeName(t.ToString()) ? "" : t.ToString();
    }

    /// <summary>The type a base-type name denotes, or null when the name is not one. This is what lets a
    /// declared scalar be IMPOSED rather than merely tolerated: List&lt;int&gt; gives ints and not whatever the
    /// parser produced (JSON numbers arrive as long).
    ///
    /// The name may carry the NULLABLE mark a declared element type keeps: `List&lt;string?&gt;` reaches here as
    /// the element name `string?`, which is not a keyword, so every element used to be refused with "the payload
    /// is a JSON string, which cannot become 'string?'". The mark is stripped for the LOOKUP and CARRIED into the
    /// type rather than defaulted to false, which is the same rule everywhere a type is rebuilt from a name: a
    /// nullable element must accept a JSON null, and the mark is the reason the name has one.</summary>
    private static TypeInfo? ScalarKeywordType(string name)
    {
        var trimmed = name.AsSpan().Trim();
        var nullable = trimmed.Length > 0 && trimmed[^1] == '?';
        if (nullable) trimmed = trimmed[..^1].TrimEnd();
        return trimmed.Length > 0 && ScalarKeywordLookup.TryGetValue(trimmed, out var t)
            ? new TypeInfo(t, nullable, 0, 0, null) : null;
    }

    /// <summary>The scalar keyword names, built ONCE from TypeSystem.Name, the same single source, but read
    /// at startup instead of per call. The lookup takes a span, so this is allocation-free too; the previous
    /// form walked Enum.GetValues on every call, which allocates an array, on a path that runs per element.</summary>
    private static readonly Dictionary<string, TokenType> ScalarKeywords = BuildScalarKeywords();

    // The name -> type map is the same one the lookup above uses; a dictionary rather than a set because a
    // declared scalar has to be IMPOSED, which needs the TokenType and not just a yes.
    private static readonly Dictionary<string, TokenType>.AlternateLookup<ReadOnlySpan<char>> ScalarKeywordLookup =
        ScalarKeywords.GetAlternateLookup<ReadOnlySpan<char>>();

    private static Dictionary<string, TokenType> BuildScalarKeywords()
    {
        var names = new Dictionary<string, TokenType>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Enum.GetValues<TokenType>())
            if (IsScalarKeyword(t)) names[TypeSystem.Name(t)] = t;
        return names;
    }

    /// <summary>The keywords whose JSON form is the value itself: imposing a type on them is a no-op, and the
    /// ones DeserializePrimitive converts (DateTime, TimeSpan, Guid…) are deliberately NOT here.</summary>
    private static bool IsScalarKeyword(TokenType t) => TypeSystem.IsNumeric(t)
        || t is TokenType.String or TokenType.StringType or TokenType.Bool or TokenType.Char;

    /// <summary>The VALUE name inside a dictionary name, <c>Dictionary&lt;string, Person&gt;</c> names
    /// <c>Person</c>; anything else names itself. Split at depth, because the value may itself be generic
    /// (<c>Dictionary&lt;string, List&lt;int&gt;&gt;</c>).</summary>
    private static string? DictionaryValueTypeNameOf(string typeName)
    {
        var t = typeName.Trim();
        var open = t.IndexOf('<');
        if (open < 0 || !t.EndsWith(">")) return null;
        var args = SplitTypeArgs(t.Substring(open + 1, t.Length - open - 2));
        return args.Count > 1 ? args[1] : null;
    }

    /// <summary>Splits a type-argument list at its top-level commas, depth-aware, so
    /// <c>string, List&lt;int&gt;</c> is two arguments and not three. The one splitter, shared by the
    /// dictionary reader and the tuple reader rather than written twice.</summary>
    private static List<string> SplitTypeArgs(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            switch (inner[i])
            {
                case '<': case '(': case '[': depth++; break;
                case '>': case ')': case ']': depth--; break;
                case ',' when depth == 0:
                    parts.Add(inner[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }
        parts.Add(inner[start..].Trim());
        return parts;
    }

    /// <summary>The declared elements of a tuple name, <c>(string s, int i)</c> gives the type and the label
    /// of each position. The bare tag <c>Tuple</c> spells nothing, so it yields the type alone and its
    /// elements come back in their parsed form. Labels matter: without them a tuple's fields are only
    /// reachable as Item1/Item2.</summary>
    private static List<(string? Type, string? Name)> TupleElementSpecsOf(string typeName)
    {
        var specs = new List<(string?, string?)>();
        var t = typeName.Trim();
        if (!t.StartsWith('(') || !t.EndsWith(")")) return specs;
        foreach (var part in SplitTypeArgs(t[1..^1]))
        {
            if (part.Length == 0) continue;
            var space = part.LastIndexOf(' ');
            if (space > 0) specs.Add((part[..space].Trim(), part[(space + 1)..].Trim()));
            else specs.Add((part, null));
        }
        return specs;
    }

    /// <summary>The script spelling of a type argument, <c>List&lt;int&gt;</c>,
    /// <c>Dictionary&lt;string, int&gt;</c>, <c>Person</c>, so the generic registration can use the very same
    /// name-based path as the string form.</summary>
    internal static string ScriptTypeName(TypeInfo info)
    {
        // A tuple carries TupleElementTypes rather than a TypeName, so name it structurally: that is what
        // List<(string s, int i)> spells, and the recursion above reaches it for a nested tuple.
        if (info.TupleElementTypes is { Count: > 0 } tupleElements)
            return "(" + string.Join(", ", tupleElements.Select((t, i) =>
                ScriptTypeName(t) + (info.TupleElementNames is { } tn && i < tn.Count && tn[i] is { Length: > 0 } label
                    ? " " + label : ""))) + ")";

        var name = info.TypeName;
        if (string.IsNullOrEmpty(name))
        {
            // A base type carries only BaseType, its TypeName is null, and recursing into one used to be a
            // dead end: `List<int>` looked fine at the top level and then threw on its `int`. Arrays likewise.
            // TypeSystem.Name is the single source of truth for that keyword mapping, and `object` is one of
            // the names it gives: object is a real target and means "leave the payload as parsed", so it must
            // not be mistaken for the unnamed case the old guard threw on.
            name = TypeSystem.Name(info.BaseType);
        }
        if (info.GenericArgs is { Count: > 0 })
            name = name + "<" + string.Join(", ", info.GenericArgs.Select(ScriptTypeName)) + ">";
        if (info.ArrayRank == 1) name += "[]";
        else if (info.ArrayRank > 1) name += "[" + new string(',', info.ArrayRank - 1) + "]";
        return name;
    }

    /// <summary>Recursively walk parsed JSON data and reconstruct any __type-bearing dictionaries.</summary>
    internal static object? ReconstructNested(object? val, bool caseSensitive)
    {
        switch (val)
        {
            case ScriptBoxedDictionary dict:
                {
                    // A tagged CONTAINER first: a JSON array has no member slot, so the writer put the type
                    // in a wrapper, {"__type":"string[]","Values":[…]}, and what the caller serialized was
                    // the array, not an object. Handing it back as itself is what keeps
                    // Json.Parse(Json.Serialize(x, {showType:true})) the shape it was before tagging. A tuple
                    // is the exception, and UnwrapTaggedContainer is where that rule lives.
                    if (IsTaggedWrapper(dict, out var wrapperType))
                        return UnwrapTaggedContainer(dict, wrapperType, reconstruct: true, caseSensitive: caseSensitive);

                    // Reconstruct this node if it has __type
                    if (dict.ContainsKey("__type") && dict["__type"] is string tn)
                    {
                        var result = DeserializeFromDict(dict, tn, caseSensitive);
                        if (result is not ScriptBoxedDictionary) return result; // was reconstructed
                                                                                // If DeserializeFromDict returned raw dict, still process its values
                    }
                    // Process all values recursively
                    var keys = dict.Keys().ToList();
                    foreach (var key in keys)
                        if (key is string ks)
                            dict[ks] = ReconstructNested(dict[ks], caseSensitive);
                    return dict;
                }
            case IScriptList list:
                {
                    var items = list.ToArray();
                    for (var i = 0; i < items.Length; i = i + 1)
                    {
                        var orig = items.GetValue(i);
                        var reconstructed = ReconstructNested(orig, caseSensitive);
                        if (!ReferenceEquals(reconstructed, orig))
                        {
                            if (val is ScriptBoxedList bsl)
                                bsl[i] = reconstructed;
                        }
                    }
                    return val;
                }
            // Auto-detect temporal/identity strings for round-trip comfort
            case string { Length: > 0 } s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts):
                return ts;
            case string { Length: > 0 } s when DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt):
                return dt;
            case string { Length: > 0 } s when DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dto):
                return dto;
            case string { Length: > 0 } s when Guid.TryParse(s, out var g):
                return g;
            default:
                return val;
        }
    }

    /// <summary>Convert a primitive JSON value to a target CLR type for round-trip deserialization.</summary>
    private static object? DeserializePrimitive(object? parsed, string typeName)
    {
        if (parsed == null) return null;
        var clrName = typeName.Replace("Interpreter.", "");
        return (clrName, parsed) switch
        {
            ("DateTime", string s) when DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt) => dt,
            ("DateTimeOffset", string s) when DateTimeOffset.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dto) => dto,
            ("TimeSpan", string s) when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) => ts,
            ("DateOnly", string s) when DateOnly.TryParse(s, out var d) => d,
            ("TimeOnly", string s) when TimeOnly.TryParse(s, out var t) => t,
            ("Guid", string s) when Guid.TryParse(s, out var g) => g,
            ("Uri", string s) when Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out var u) => u,
            _ => null
        };
    }

    /// <summary>Returns the default value for a given TypeInfo.</summary>
    private static object? DefaultValue(TypeInfo type)
    {
        if (type.TypeName != null) return null; // class/struct default to null
        return type.BaseType switch
        {
            TokenType.Int or TokenType.Long or TokenType.Short or TokenType.Byte
                or TokenType.Float or TokenType.Double or TokenType.Decimal => 0,
            TokenType.Bool => false,
            TokenType.String or TokenType.StringType => "",
            TokenType.Char => '\0',
            _ => null
        };
    }

    /// <summary>Is <paramref name="value"/> the DEFAULT of <paramref name="type"/>, exactly? The comparison is
    /// EXACT on purpose where the language's own <c>==</c> is tolerant to 1e-6: a serializer that dropped a 1e-7
    /// as a zero would lose a value nobody asked it to lose. A named type (a class or a struct) answers false for
    /// a value, because a "default INSTANCE" is not modelled, so an all-defaults struct is emitted whole and only
    /// <c>null</c> is omitted; an unknown type answers false, since omitting is the riskier direction. This is
    /// the mirror of <see cref="DefaultValue"/>, which is what the READER fills an absent key with, so what
    /// <c>omitDefaults</c> leaves out is exactly what comes back.</summary>
    private static bool IsTypeDefault(object? value, TypeInfo? type)
    {
        if (value is null) return true;
        if (type is null || type.TypeName != null) return false;
        return type.BaseType switch
        {
            TokenType.Int or TokenType.Long or TokenType.Short or TokenType.Byte
                or TokenType.Float or TokenType.Double or TokenType.Decimal => value is 0,
            TokenType.Bool => value is false,
            TokenType.String or TokenType.StringType => value is "",
            TokenType.Char => value is '\0',
            _ => false
        };
    }

    /// <summary>Resolves a backing field name to the property name.</summary>
    private static string ResolvePropertyName(IReadOnlyList<PropertyDecl>? properties, string fieldName)
    {
        if (properties == null) return fieldName;
        foreach (var p in properties)
        {
            if (fieldName.Equals(BackingFieldName(p.Name), StringComparison.OrdinalIgnoreCase))
                return p.Name;
        }
        return fieldName;
    }
}
