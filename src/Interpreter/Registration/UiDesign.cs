namespace ScriptLangCore;
// ============================================================================
// The design metadata, typed, reflection-free, for the RAD form designer and for any script that
// edits a form: a per-control-kind property table (not GetProperties) so it is AOT/trim safe
// and fast. A script reaches them through the CONTROL: PropList / GetProp / SetProp, the same door
// ============================================================================
internal partial class Interpreter
{

    private static string KindOf(Control c) =>
        c is Label ? "Label"
        : c is RadioButton ? "RadioButton"
        : c is CheckBox ? "CheckBox"
        : c is TextBox ? "TextBox"
        : c is ListBox ? "ListBox"
        : c is ComboBox ? "ComboBox"
        : c is TrackBar ? "TrackBar"
        : c is Button ? "Button"
        : c.GetType().Name;

    // ============================================================================
    // The document: Json.Serialize writes a form as its design table plus its children, and this REBUILDS it
    // ============================================================================

    /// <summary>Rebuild a form from the document <c>Json.Serialize</c> wrote: its own rows are applied to
    /// <paramref name="form"/> and its children are CREATED, in the order the document lists them. Answers how many
    /// controls were created.
    ///
    /// A row that names a property which cannot be WRITTEN is skipped on purpose: the document carries facts a
    /// reader reports (ClassName, Style, ExStyle, ItemCount, SelectedItem) as well as inputs, and refusing the
    /// facts would refuse every document. Anything else unknown is REFUSED by name, because a document that
    /// silently drops a property is a document that loses it.
    ///
    /// `Kind` is skipped for the same reason: it is the identity the ENTRY already carries, so the entry is what a
    /// reader uses and the row inside the table is what the export wrote for uniformity.</summary>
    internal static int LoadFromJson(Form form, string json) => LoadFromJsonCore(form, json);

    /// <summary>The SAME reading for any host, so a frame, a document window and a form go through one set of rules:
    /// the root's <c>Kind</c> is what says which, and this is the one place that decides it.</summary>
    internal static object LoadHostFromJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new ScriptException("FormFromJson: the document must be the array of rows Json.Serialize writes.");
        CheckDocumentVersion(root);
        var rootKind = RowText(root, "Kind");
        switch (rootKind)
        {
            case "" or "Form":
                var form = new Form("");
                LoadRows(form, root);
                return form;
            case "MdiForm":
                var frame = new MdiForm("");
                frame.InitHandle();
                LoadRows(frame, root);
                return frame;
            case "MdiChild":
                // A document window has no frame in the document, so one is made to OWN it and the window keeps it
                // alive (MdiChild.Owner): a window is parented to an MDI client, and a client belongs to a frame.
                var owner = new MdiForm("");
                owner.InitHandle();
                var window = owner.CreateMdiChild(RowText(root, "Text"), RowInt(root, "Left"), RowInt(root, "Top"),
                                                  RowInt(root, "Width"), RowInt(root, "Height"));
                if (window is null)
                    throw new ScriptException("FormFromJson: the implicit frame for a root MdiChild could not be made.");
                LoadRows(window, root);
                return window;
            default:
                throw new ScriptException(
                    $"FormFromJson: the document's root is a '{rootKind}', and this reader builds a Form, an MdiForm or an MdiChild.");
        }
    }

    private static int LoadFromJsonCore(Form form, string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw new ScriptException("FormFromJson: the document must be the array of rows Json.Serialize writes.");
        CheckDocumentVersion(doc.RootElement);
        // The root SAYS which host it is, and this entry point builds a Form. Whatever else it is goes through
        // LoadHostFromJson, which is where the dispatch lives, so a document can never be read as a form that
        // quietly lost the part this reader cannot take.
        var rootKind = RowText(doc.RootElement, "Kind");
        if (rootKind.Length > 0 && rootKind != "Form")
            throw new ScriptException(
                $"FormFromJson: the document's root is a '{rootKind}', and this reader builds a Form. "
                + "Use Application.FormFromJson, which builds whichever host the root names.");
        return LoadRows(form, doc.RootElement);
    }

    private static int LoadRows(IControlHost host, System.Text.Json.JsonElement rows)
    {
        var childEntries = new List<System.Text.Json.JsonElement>();
        var windowEntries = new List<System.Text.Json.JsonElement>();
        var ownRows = new List<(string Name, System.Text.Json.JsonElement Value)>();
        foreach (var row in rows.EnumerateArray())
        {
            var name = MemText(row, "Name");
            if (name == "Controls")
            {
                foreach (var entry in RowValue(row).EnumerateArray()) childEntries.Add(entry);
                continue;
            }
            if (name == "MdiChildren")
            {
                foreach (var entry in RowValue(row).EnumerateArray()) windowEntries.Add(entry);
                continue;
            }
            // The two IDENTITY rows of a document: Kind says which host, Version says which LAYOUT of it. Neither is a
            // property, so neither is applied: the version was checked by the entry point that read this root.
            if (name is "Kind" or DocumentVersionRow) continue;
            ownRows.Add((name, RowValue(row)));
        }

        // The host's own rows FIRST, so a child is created under a host that already carries its look, and the
        // children next, in the order the document lists them.
        foreach (var (name, value) in ownRows) host.SetHostRow(name, JsonScalar(value));

        var created = 0;
        foreach (var entry in childEntries)
        {
            var childRows = RowValue(entry);
            var child = host.CreateKind(SpecOf(entry, childRows));
            ApplyChildRows(child, entry, childRows);
            created++;
        }

        // A frame's DOCUMENT WINDOWS are hosts in their own right, and the only door that makes one is
        // CreateMdiChild: its rect went to that call and nowhere else, so it is read from the entry.
        foreach (var entry in windowEntries)
        {
            if (host is not MdiForm frame)
                throw new ScriptException("FormFromJson: only an MdiForm holds document windows.");
            var windowRows = RowValue(entry);
            var window = frame.CreateMdiChild(RowText(windowRows, "Text"),
                RowInt(windowRows, "Left"), RowInt(windowRows, "Top"),
                RowInt(windowRows, "Width"), RowInt(windowRows, "Height"));
            if (window is null)
                throw new ScriptException("FormFromJson: the frame could not create a document window.");
            LoadRows(window, windowRows);
            created++;
        }

        // And the host's own rows AGAIN, which is not belt and braces: creating the first child creates the host's
        // WINDOW, and a form's OnHandleCreated places it (StartPosition may ask to be centred), which overwrites
        // everything before it. Measured: without this second pass a document whose form sits at 100,100 came back
        // CENTRED (1020,504) while every other row agreed. The POSITION is written LAST on purpose: a geometry row
        // moves the window, and the placement pass runs while StartPosition is not yet the document's, so Left and
        // Top have to be the final word or the centring wins again. Measured, and it is why the explicit assignment
        // a script makes works while the same value applied earlier did not.
        foreach (var (name, value) in ownRows) host.SetHostRow(name, JsonScalar(value));
        foreach (var (name, value) in ownRows)
            if (name is "Left" or "Top") host.SetHostRow(name, JsonScalar(value));
        return created;
    }

    /// <summary>What an entry says to create: its Type is the kind, and the rect, the text and the style words come
    /// from the child's own rows, which is where the export wrote them.</summary>
    private static ControlSpec SpecOf(System.Text.Json.JsonElement entry, System.Text.Json.JsonElement childRows)
        => new(MemText(entry, "Type"),
               RowText(childRows, "Text"),
               RowInt(childRows, "Left"), RowInt(childRows, "Top"),
               RowInt(childRows, "Width"), RowInt(childRows, "Height"),
               (uint)RowInt(childRows, "Style"), (uint)RowInt(childRows, "ExStyle"),
               RowText(childRows, "ClassName"));

    /// <summary>A created child's own rows: the ENTRY carries the identity, which is why the child's rows do not
    /// repeat it, and the kind was the thing that created it.</summary>
    private static void ApplyChildRows(Control child, System.Text.Json.JsonElement entry, System.Text.Json.JsonElement childRows)
    {
        var entryName = MemText(entry, "Name");
        if (entryName.Length > 0) child.Name = entryName;
        foreach (var row in childRows.EnumerateArray())
        {
            var name = MemText(row, "Name");
            if (name is "Kind" or "Name") continue;
            ApplyRow(child, name, RowValue(row));
        }
    }

    /// <summary>One row of a document, with the JSON value read FIRST: a container's contents arrive as arrays of
    /// objects, so they are turned into the row types (or plain text) at the ONE edge that reads a document, and the
    /// row is then applied by name through <see cref="ApplyDocumentRow"/>, which is the same path a script's SetProp
    /// takes. One coerced value, one applier, so the JSON shape and the script shape cannot diverge in the middle.
    /// </summary>
    private static void ApplyRow(Control target, string name, System.Text.Json.JsonElement value)
    {
        if (name.Length == 0) return;
        object? rowValue = name switch
        {
            "Columns" => ReadColumns(value),
            "Tabs" => ReadStrings(value),
            "Items" when target is ListView => ReadCellRows(value),
            "Items" when target is TreeView => ReadTreeItems(value),
            "Items" when target is ListBox or ComboBox => ReadStrings(value),
            _ => JsonScalar(value),
        };
        ApplyDocumentRow(target, name, rowValue);
    }

    /// <summary>A JSON scalar as the script-level value the setters already take, so the two directions agree: a
    /// string stays a string, a number a LONG, a boolean a bool, and JSON null a null.
    ///
    /// The number arm is a SWITCH rather than a ternary on purpose: `TryGetInt64(out var l) ? l : GetDouble()`
    /// unifies long and double into DOUBLE, so every integer arrived boxed as 11.0, the numeric converter cannot
    /// narrow a double to an int, and the property was then set to 0 with nothing reported anywhere. Measured.</summary>
    private static object? JsonScalar(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => value.GetString(),
        System.Text.Json.JsonValueKind.Number when value.TryGetInt64(out var l) => l,
        System.Text.Json.JsonValueKind.Number => value.GetDouble(),
        System.Text.Json.JsonValueKind.True => true,
        System.Text.Json.JsonValueKind.False => false,
        _ => null,
    };

    private static System.Text.Json.JsonElement RowValue(System.Text.Json.JsonElement row)
        => row.TryGetProperty("Value", out var v) ? v : default;

    /// <summary>A row's own Name / Type / Value member. Every row is <c>{Name, Type, Value}</c> and a CHILD entry is
    /// one too, so this reads either; a string member answers its text and a scalar its JSON spelling.</summary>
    private static string MemText(System.Text.Json.JsonElement row, string member)
        => row.TryGetProperty(member, out var v)
            ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    /// <summary>Search a TABLE (an array of rows) for the row named <paramref name="name"/>, which is how a child's
    /// creation inputs are read before its rows are applied.</summary>
    private static string RowText(System.Text.Json.JsonElement table, string name)
    {
        foreach (var row in table.EnumerateArray())
            if (MemText(row, "Name") == name) return MemText(row, "Value");
        return "";
    }

    private static int RowInt(System.Text.Json.JsonElement table, string name)
        => int.TryParse(RowText(table, name), out var n) ? n : 0;

    // ── A document's list values, turned into the types the design setter takes. One edge, one conversion, so the
    // JSON shape and the script shape cannot diverge in the middle.
    private static List<string> ReadStrings(System.Text.Json.JsonElement value)
    {
        var list = new List<string>();
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return list;
        foreach (var e in value.EnumerateArray())
            list.Add(e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "" : e.ToString());
        return list;
    }

    private static List<ColumnProp> ReadColumns(System.Text.Json.JsonElement value)
    {
        var cols = new List<ColumnProp>();
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return cols;
        foreach (var e in value.EnumerateArray())
            cols.Add(new ColumnProp { Text = RowText2(e, "Text"), Width = RowInt2(e, "Width") });
        return cols;
    }

    /// <summary>A table's rows: the outer array is the rows and each inner one is that row's cells.</summary>
    private static List<List<string>> ReadCellRows(System.Text.Json.JsonElement value)
    {
        var rows = new List<List<string>>();
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return rows;
        foreach (var e in value.EnumerateArray()) rows.Add(ReadStrings(e));
        return rows;
    }

    /// <summary>A tree, which nests: each entry carries its own Children.</summary>
    private static List<TreeItemProp> ReadTreeItems(System.Text.Json.JsonElement value)
    {
        var items = new List<TreeItemProp>();
        if (value.ValueKind != System.Text.Json.JsonValueKind.Array) return items;
        foreach (var e in value.EnumerateArray())
            items.Add(new TreeItemProp
            {
                Text = RowText2(e, "Text"),
                Children = e.TryGetProperty("Children", out var kids) ? ReadTreeItems(kids) : [],
            });
        return items;
    }

    /// <summary>One member of a JSON OBJECT (a column, a tree item), as text or as a number: the row shapes are
    /// objects with named members rather than {Name, Type, Value} rows.</summary>
    private static string RowText2(System.Text.Json.JsonElement obj, string member)
        => obj.TryGetProperty(member, out var v)
            ? (v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";

    private static int RowInt2(System.Text.Json.JsonElement obj, string member)
        => int.TryParse(RowText2(obj, member), out var n) ? n : 0;

    /// <summary>One row of a control's design-property table, as a script reads it: named members instead of the
    /// positional [name, type, value] array the table used to project. Registered as a script type and rooted for
    /// AOT for the same two reasons StyleFlag is.</summary>
    internal sealed class ControlProp
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public object? Value { get; set; }
    }

    /// <summary>One COLUMN of a ListView, which is what a table's header needs to travel: its text and its width.
    /// Registered as a script type and rooted for AOT for the same two reasons ControlProp is (a script reads the
    /// REPORTED name back, so the registry has to answer it).</summary>
    internal sealed class ColumnProp
    {
        public string Text { get; set; } = "";
        public int Width { get; set; }
    }

    /// <summary>One NODE of a TreeView, with its CHILDREN: a tree stays a tree in a document, which is why this one
    /// nests where a column does not. The list is never null, so a walk does not have to test it.</summary>
    internal sealed class TreeItemProp
    {
        public string Text { get; set; } = "";
        public List<TreeItemProp> Children { get; set; } = [];
    }

    private static List<ControlProp> BuildPropList(Control c)
    {
        var result = new List<ControlProp>();
        void Add(string n, string t, object? v) => result.Add(new ControlProp { Name = n, Type = t, Value = v });
        Add("Name", "string", c.Name);   // the control's own Name: the designer and its generator set it, so a
                                         // handler can tell which control it was handed
        Add("Kind", "string", KindOf(c));
        Add("Text", "string", c.Text);
        Add("Left", "int", c.Left);
        Add("Top", "int", c.Top);
        Add("Width", "int", c.Width);
        Add("Height", "int", c.Height);
        Add("Visible", "bool", c.Visible);
        Add("Enabled", "bool", c.Enabled);
        Add("FontName", "string", c.FontName);
        Add("FontSize", "int", c.FontSize);
        Add("FontBold", "bool", c.FontBold);
        Add("ForeColor", "color", c.TextColor);
        Add("BackColor", "color", c.BackgroundColor);
        // The two WINDOW-STYLE WORDS, and they belong HERE rather than in the type walk for the same reason the
        // containers do: the type walk only reaches a control whose own type declares them, so a Button's style
        // travelled nowhere and a document could not carry the one part of a control's look that no property names.
        // A style is a bit field, so one row per FLAG would be the alternative: the WORD is what a document needs,
        // and turning a flag into a bit is the style editor's job (StyleFlags / SetStyle).
        // They are added under names the type walk ALSO reports for a GenericControl, which is why it has to see
        // them: one row per name, the LIVE word rather than the creation-time copy GenericControl keeps.
        Add("Style", "long", c.ReadStyle(false));
        Add("ExStyle", "long", c.ReadStyle(true));
        switch (c)
        {
            case CheckBox cb: Add("Checked", "bool", cb.Checked); break;
            case RadioButton rb: Add("Checked", "bool", rb.Checked); break;
            // `List<string>` rather than a bare `list`: the TYPE column carries a name a SCRIPT can write, and the
            // engine's own spelling for one is `List` (that is what typeof answers, and what `List<int>` is written
            // as). Inside a ListBox or a ComboBox the element type is known exactly, because these are the item
            // TEXTS the engine reads and writes; a list whose elements were not fixed stays a bare `List`.
            case ListBox: Add("Items", "List<string>", ReadItemsText(c)); break;
            case ComboBox: Add("Items", "List<string>", ReadItemsText(c)); break;
            case TrackBar tb:
                Add("Minimum", "int", tb.Minimum);
                Add("Maximum", "int", tb.Maximum);
                Add("Value", "int", tb.Value);
                break;
            case DateTimePicker dtp:
                Add("Value", "datetime", dtp.Value.ToString("yyyy-MM-dd HH:mm:ss"));
                break;
            // The CONTENTS of a container, which is the part a template cannot do without: a table is its columns
            // and its cells, a tree is its nodes (nested, because nesting is the one thing a flat list of texts
            // loses), and a tab control is its tab texts. Each is READ off the control rather than remembered, so
            // the document says what the control holds now.
            case ListView lv:
                Add("Columns", "List<ColumnProp>", ColumnPropsOf(lv));
                Add("Items", "List<List<string>>", RowCellsOf(lv));
                break;
            case TreeView tv:
                Add("Items", "List<TreeItemProp>", tv.Items());
                break;
            case TabControl tc:
                Add("Tabs", "List<string>", TabTextsOf(tc));
                break;
            // The CLIENT area of a FORM, in design units, which is the space its children were placed in. It is the one
            // row here that is a pure FACT rather than an input: it exists because a document states the room its layout
            // was designed against, so a reader can CHECK that the frame it produced gives the same room instead of
            // assuming it (a different monitor DPI or theme changes the frame). Only a FORM carries the rows, because a
            // form is what lays children out; the members themselves are on every Control.
            case Form fm:
                Add("ClientWidth", "int", fm.ClientWidth);
                Add("ClientHeight", "int", fm.ClientHeight);
                break;
        }
        return result;
    }

    /// <summary>One node of a tree, inserted under its PARENT (0 for a root) with TVI_LAST, the id the engine's own
    /// InsertItem calls use, and depth first so the children follow the order the document wrote.</summary>
    private static void InsertTreeItem(TreeView tree, TreeItemProp item, long parent)
    {
        var handle = tree.InsertItem(item.Text, parent, -65534);
        foreach (var child in item.Children) InsertTreeItem(tree, child, handle);
    }

    /// <summary>A list from a document, as the values a control's own members take. Each reader accepts the row TYPE
    /// the document writes AND a plain dictionary with the same member names, because both reach here: the document
    /// path builds the type, and a script writing `SetProp("Columns", [{"Text": "Name", "Width": 120}])` passes a
    /// literal, which the language materializes as a dictionary.</summary>
    private static List<ColumnProp> AsColumns(object? value, string name)
    {
        var cols = new List<ColumnProp>();
        foreach (var entry in Entries(value, name))
            cols.Add(entry is ColumnProp cp
                ? cp
                : new ColumnProp { Text = Str(Member(entry, "Text")), Width = ToInt(Member(entry, "Width")) });
        return cols;
    }

    /// <summary>A table's rows: each entry is that row's cells, and a cell is text.</summary>
    private static List<List<string>> AsCellRows(object? value, string name)
    {
        var rows = new List<List<string>>();
        foreach (var entry in Entries(value, name)) rows.Add(AsStrings(entry, name));
        return rows;
    }

    private static List<TreeItemProp> AsTreeItems(object? value, string name)
    {
        var items = new List<TreeItemProp>();
        foreach (var entry in Entries(value, name))
            items.Add(entry is TreeItemProp tip
                ? tip
                : new TreeItemProp
                {
                    Text = Str(Member(entry, "Text")),
                    Children = AsTreeItems(Member(entry, "Children"), name),
                });
        return items;
    }

    private static List<string> AsStrings(object? value, string name)
    {
        var strings = new List<string>();
        foreach (var entry in Entries(value, name)) strings.Add(Str(entry));
        return strings;
    }

    /// <summary>The entries of a list value, or a refusal that says what was wanted: a container's contents are
    /// lists, and a bare scalar here is a document saying something the control cannot take.</summary>
    private static IEnumerable<object?> Entries(object? value, string name)
    {
        if (value is System.Collections.IEnumerable entries and not string)
        {
            foreach (var entry in entries) yield return entry;
            yield break;
        }
        throw new ScriptException(
            $"UiDesign: '{name}' takes a list (ColumnProp rows, tree items, or strings), not {value?.GetType().Name ?? "null"}.");
    }

    /// <summary>One named member of a row that arrived as a dictionary rather than as the row type.
    ///
    /// BOTH dictionary shapes have to be asked: a script's literal is materialized as an
    /// <see cref="IScriptDictionary"/>, which is NOT a System.Collections.IDictionary. Reading only the BCL one
    /// answered null for every field of a script's own row, which is how `SetProp("Columns", [{"Text": "Name",
    /// "Width": 120}])` built two columns with NO text and NO width, and how a tree's items lost theirs.</summary>
    private static object? Member(object? entry, string member)
    {
        if (entry is System.Collections.IDictionary d && d.Contains(member)) return d[member];
        if (entry is IScriptDictionary sd && sd.Dictionary.Contains(member)) return sd.Dictionary[member];
        return null;
    }

    private static string Str(object? value) => value as string ?? Convert.ToString(value) ?? "";

    private static int ToInt(object? value)
    {
        try { return value is null ? 0 : Convert.ToInt32(value); }
        catch { return 0; }
    }

    /// <summary>What a document entry needs to CREATE one control: the kind, the text, the rect, and the two things
    /// only a raw control wants. A struct, so a walk does not heap-allocate one per child.</summary>
    internal readonly record struct ControlSpec(
        string Kind, string Text, int X, int Y, int W, int H,
        uint Style = 0, uint ExStyle = 0, string ClassName = "");

    /// <summary>The factories an MDI frame and an MDI document window BOTH expose, which is what lets their kind
    /// table be written ONCE. A class satisfies an interface implicitly when its public members already have the
    /// signatures, so declaring this costs those two hosts no new member, and a factory added to one and not the
    /// other stops compiling here instead of drifting. The trailing `configure` parameter is part of the signature
    /// because the public factories carry it (the same callback every host's factories take, see
    /// <see cref="Interpreter.WithInit"/>); a caller inside the engine omits it.</summary>
    internal interface IMdiFactories
    {
        public Label CreateLabel(string text, int x, int y, int width, int height, Action<Label>? configure = null);
        public Button CreateButton(string text, int x, int y, int width, int height, Action<Button>? configure = null);
        public TextBox CreateTextBox(string text, int x, int y, int width, int height, Action<TextBox>? configure = null);
        public CheckBox CreateCheckBox(string text, int x, int y, int width, int height, bool isChecked = false, Action<CheckBox>? configure = null);
        public RadioButton CreateRadioButton(string text, int x, int y, int width, int height, bool isChecked = false, Action<RadioButton>? configure = null);
        public ListBox CreateListBox(int x, int y, int width, int height, Action<ListBox>? configure = null);
        public ComboBox CreateComboBox(int x, int y, int width, int height, Action<ComboBox>? configure = null);
        public ListView CreateListView(int x, int y, int width, int height, Action<ListView>? configure = null);
        public TreeView CreateTreeView(int x, int y, int width, int height, Action<TreeView>? configure = null);
        public DateTimePicker CreateDateTimePicker(int x, int y, int width, int height, Action<DateTimePicker>? configure = null);
        public TrackBar CreateTrackBar(int x, int y, int width, int height, Action<TrackBar>? configure = null);
    }

    /// <summary>A host that HOLDS controls: a Form, an MDI frame or an MDI document window. It exists because
    /// neither MDI host is a Control, so a document walk cannot be typed on Control and still reach them, and it is
    /// FOUR members on purpose: exactly what applying a document needs and nothing else. The members are implemented
    /// EXPLICITLY, so none of them reaches the script surface.
    /// </summary>
    internal interface IControlHost
    {
        /// <summary>One scalar row onto the host. A name it cannot WRITE is a fact the document merely reports
        /// (Visible on a frame, a handle) and is skipped rather than refused.</summary>
        public void SetHostRow(string name, object? value);
        /// <summary>The children a document lists, in the order to rebuild them.</summary>
        public IEnumerable<Control> HostChildren();
        /// <summary>The child with that name, or null: the handle a template's names are for.</summary>
        public Control? HostFind(string name);
        /// <summary>Make the control a document names, through THIS host's own factories, refusing by name what it
        /// cannot make (a frame has eleven factories where a form has eighteen).</summary>
        public Control CreateKind(in ControlSpec spec);
    }

    /// <summary>The ONE kind table a document is applied through, so the vocabulary cannot drift from the one
    /// <see cref="KindOf"/> reports: a Form's factories, and the MDI hosts' eleven, which is why those two share
    /// <see cref="IMdiFactories"/> and this table can read them as one. A kind a host does not have is refused BY
    /// NAME, saying which host could not make it.</summary>
    private static Control CreateKindOn(object host, in ControlSpec s)
    {
        if (host is Form form)
            return s.Kind switch
            {
                "Label" => form.CreateLabel(s.Text, s.X, s.Y, s.W, s.H),
                "Button" => form.CreateButton(s.Text, s.X, s.Y, s.W, s.H),
                "TextBox" => form.CreateTextBox(s.Text, s.X, s.Y, s.W, s.H),
                "CheckBox" => form.CreateCheckBox(s.Text, s.X, s.Y, s.W, s.H),
                "RadioButton" => form.CreateRadioButton(s.Text, s.X, s.Y, s.W, s.H),
                "ListBox" => form.CreateListBox(s.X, s.Y, s.W, s.H),
                "ComboBox" => form.CreateComboBox(s.X, s.Y, s.W, s.H),
                "TrackBar" => form.CreateTrackBar(s.X, s.Y, s.W, s.H),
                "ListView" => form.CreateListView(s.X, s.Y, s.W, s.H),
                "TreeView" => form.CreateTreeView(s.X, s.Y, s.W, s.H),
                "DateTimePicker" => form.CreateDateTimePicker(s.X, s.Y, s.W, s.H),
                "StatusBar" => form.CreateStatusBar(s.X, s.Y, s.W, s.H),
                "TabControl" => form.CreateTabControl(s.X, s.Y, s.W, s.H),
                "ToolBar" => form.CreateToolBar(s.X, s.Y, s.W, s.H),
                "SysLink" => form.CreateSysLink(s.Text, s.X, s.Y, s.W, s.H),
                "HotKey" => form.CreateHotKey(s.X, s.Y, s.W, s.H),
                "Rebar" => form.CreateRebar(s.X, s.Y, s.W, s.H),
                "GenericControl" when s.ClassName.Length > 0 =>
                    form.CreateWinControl(s.ClassName, s.Text, s.X, s.Y, s.W, s.H, s.Style, s.ExStyle),
                _ => throw new ScriptException(UncreatableKind("Form", s.Kind)),
            };

        if (host is IMdiFactories mdi)
            return s.Kind switch
            {
                "Label" => mdi.CreateLabel(s.Text, s.X, s.Y, s.W, s.H),
                "Button" => mdi.CreateButton(s.Text, s.X, s.Y, s.W, s.H),
                "TextBox" => mdi.CreateTextBox(s.Text, s.X, s.Y, s.W, s.H),
                "CheckBox" => mdi.CreateCheckBox(s.Text, s.X, s.Y, s.W, s.H),
                "RadioButton" => mdi.CreateRadioButton(s.Text, s.X, s.Y, s.W, s.H),
                "ListBox" => mdi.CreateListBox(s.X, s.Y, s.W, s.H),
                "ComboBox" => mdi.CreateComboBox(s.X, s.Y, s.W, s.H),
                "ListView" => mdi.CreateListView(s.X, s.Y, s.W, s.H),
                "TreeView" => mdi.CreateTreeView(s.X, s.Y, s.W, s.H),
                "DateTimePicker" => mdi.CreateDateTimePicker(s.X, s.Y, s.W, s.H),
                "TrackBar" => mdi.CreateTrackBar(s.X, s.Y, s.W, s.H),
                _ => throw new ScriptException(UncreatableKind(host.GetType().Name, s.Kind)),
            };

        throw new ScriptException($"FormFromJson: {host.GetType().Name} is not a host that holds controls.");
    }

    /// <summary>Why a kind cannot be made, said so an author can act on it: a frame's eleven factories are not a
    /// form's eighteen, and a raw control needs the class the document did not carry.</summary>
    private static string UncreatableKind(string hostKind, string kind)
        => $"{hostKind} cannot create '{kind}'. "
           + (kind == "GenericControl"
              ? "Its entry carries no ClassName, so CreateWinControl cannot be asked for a class."
              : "Rewrite it as one of the kinds PropList() reports for this host.");

    /// <summary>The child with that name among a host's children, or null. ONE comparison for all three hosts, so a
    /// name cannot follow a different case rule on a frame than on a form. The interpreter's own case rule is read
    /// at the call, and a call with none (a native body) matches exactly, which is what strict mode does.</summary>
    private static Control? FindIn(IEnumerable<Control> children, string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var comparison = Current is { } running ? running.Comparison : StringComparison.Ordinal;
        foreach (var ctrl in children)
            if (string.Equals(ctrl.Name, name, comparison)) return ctrl;
        return null;
    }

    /// <summary>One row of a document onto a CONTROL: the CLR property of that name when the control has one, the
    /// engine's own design setter for the names that are not properties (ForeColor and BackColor map to TextColor and
    /// BackgroundColor, Items to the item list), and NOTHING for a property that exists and cannot be written, which
    /// is a fact the document merely reports (ClassName, Style, ItemCount). Any other name is refused by name, so a
    /// document cannot lose a property in silence.</summary>
    private static void ApplyDocumentRow(Control target, string name, object? value)
    {
        if (name.Length == 0 || name == "Kind") return;
        if (ApplyClrRow(target, name, value)) return;
        // The DESIGN names come BEFORE the reported-fact test, because one of them names a property that exists and
        // cannot be WRITTEN: GenericControl declares Style and ExStyle as read-only scalars of the words it was made
        // with, so a document row of that name would be taken for a fact and skipped, and the live word would be
        // lost. The fact test below is for the names that are genuinely only reported (ClassName, ItemCount).
        if (name is "ForeColor" or "BackColor" or "Items" or "Columns" or "Tabs" or "Style" or "ExStyle")
        {
            SetDesignProp(target, name, value);
            return;
        }
        if (target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) is not null) return;
        throw new ScriptException($"FormFromJson: '{name}' is not a property of {KindOf(target)}.");
    }

    /// <summary>A value onto a CLR property of that name, coerced the way a script write would be: an enum from the
    /// SPELLING the export wrote (the whole reason it writes the name and not the number), everything else through
    /// the numeric converter. Answers false when the property is absent or cannot be written, which is how a reported
    /// fact is skipped.</summary>
    private static bool ApplyClrRow(object target, string name, object? value)
    {
        var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return false;
        try { prop.SetValue(target, CoerceValueFor(prop.PropertyType, value)); }
        catch { return false; }
        return true;
    }

    private static object? CoerceValueFor(Type propertyType, object? value)
    {
        var t = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (value is null) return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (t == typeof(string)) return Convert.ToString(value, inv);
        if (t == typeof(bool)) return Convert.ToBoolean(value, inv);
        if (t.IsEnum)
            return value is string spelling ? Enum.Parse(t, spelling, ignoreCase: true)
                                            : Enum.ToObject(t, Convert.ToInt64(value, inv));
        return NumericTypeConverter.CoerceToType(value, t);
    }

    // One reader per container, shared by the design table and by GetProp, so the two cannot answer differently.
    private static List<ColumnProp> ColumnPropsOf(ListView lv)
    {
        var cols = new List<ColumnProp>();
        var n = lv.ColumnCount;
        for (var i = 0; i < n; i++)
            cols.Add(new ColumnProp { Text = lv.GetColumnText(i), Width = lv.GetColumnWidth(i) });
        return cols;
    }

    /// <summary>A table's rows as CELLS: the outer list is the rows and each entry is that row's cells in column
    /// order, which is a shape the document can write without inventing a separator that a cell's own text could
    /// contain.</summary>
    private static List<object> RowCellsOf(ListView lv)
    {
        var rows = new List<object>();
        var cols = lv.ColumnCount;
        for (var r = 0; r < lv.ItemCount; r++)
        {
            var cells = new List<string>(cols);
            for (var i = 0; i < cols; i++) cells.Add(lv.GetItemText(r, i));
            rows.Add(cells);
        }
        return rows;
    }

    private static List<string> TabTextsOf(TabControl tc)
    {
        var tabs = new List<string>();
        var n = tc.TabCount;
        for (var i = 0; i < n; i++) tabs.Add(tc.GetTabText(i));
        return tabs;
    }

    private static object? GetDesignProp(Control c, string name) => name switch
    {
        "Text" => c.Text,
        "Left" => c.Left,
        "Top" => c.Top,
        "Width" => c.Width,
        "Height" => c.Height,
        "Visible" => c.Visible,
        "Enabled" => c.Enabled,
        "FontName" => c.FontName,
        "FontSize" => c.FontSize,
        "FontBold" => c.FontBold,
        "ForeColor" => c.TextColor,
        "BackColor" => c.BackgroundColor,
        "Checked" when c is CheckBox chk => chk.Checked,
        "Checked" when c is RadioButton rbk => rbk.Checked,
        "Items" when c is ListBox or ComboBox => ReadItemsText(c),
        "Items" when c is ListView lvp => RowCellsOf(lvp),
        "Items" when c is TreeView tvp => tvp.Items(),
        "Columns" when c is ListView lvc => ColumnPropsOf(lvc),
        "Tabs" when c is TabControl tcp => TabTextsOf(tcp),
        "Minimum" when c is TrackBar t1 => t1.Minimum,
        "Maximum" when c is TrackBar t2 => t2.Maximum,
        "Value" when c is TrackBar t3 => t3.Value,
        "Value" when c is DateTimePicker t4 => t4.Value,
        "Style" => c.ReadStyle(false),
        "ExStyle" => c.ReadStyle(true),
        // The two FACTS a form publishes about its layout space: read through the same door as every other row, so
        // PropList and GetProp keep agreeing, and never written (a client area is what the frame left, not a request).
        "ClientWidth" => c.ClientWidth,
        "ClientHeight" => c.ClientHeight,
        "Name" => c.Name,
        _ => throw new ScriptException($"UiDesign: prop '{name}' not supported for {KindOf(c)}")
    };

    private static void SetDesignProp(Control c, string name, object? value)
    {
        // A write of the value the control ALREADY holds is not a write, and this guard is what makes export ->
        // rebuild -> export a FIXED POINT instead of a drift. It matters because several of these setters do more than
        // store: FontSize re-applies a font, and the row metrics a ListView lays its items out with follow it, so an
        // item added afterwards can need SCROLLBARS the control did not have (measured: a document that restated
        // FontSize = 0 grew a ListView's WS_HSCROLL|WS_VSCROLL on every reload, so the same form never serialized
        // the same twice). A document that merely restates a property must not perturb the control it describes.
        // A name the table does not READ answers false here, so an unknown name still reaches the refusal below.
        var known = true;
        object? current = null;
        try { current = GetDesignProp(c, name); }
        catch (ScriptException) { known = false; }
        if (known)
        {
            if (current is null && value is null) return;
            if (current is not null && value is not null)
            {
                // Compared NUMERICALLY when both sides are numbers: a script's 0 and a property's 0 are boxed as
                // different CLR types, so Equals alone would report a difference that is not one.
                var bothNumeric = IsDesignNumber(current) && IsDesignNumber(value);
                if (bothNumeric)
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (Convert.ToDecimal(current, inv) == Convert.ToDecimal(value, inv)) return;
                }
                else if (Equals(current, value)) return;
            }
        }
        // hex strings (0x../#..) are accepted for every numeric/color property.
        // Colors additionally accept bare RRGGBB / RRGGBBAA when unambiguous (the
        // string is all hex digits AND contains an A-F letter; all-digit strings
        // like "16711680" stay decimal).
        ScriptException BadNumber(object? v) =>
            new ScriptException($"UiDesign: '{v}' is not a valid number for '{name}'. Use a decimal integer or hex (0x../#..; colors also accept bare RRGGBB or RRGGBBAA).");
        // Parse editor/JSON strings with a readable error instead of leaking raw
        // .NET FormatException text (under NativeAOT those message resources are
        // stripped, so callers saw resource keys like "Format_ExtraJunkAtEnd").
        long ToNum(object? v, bool color)
        {
            if (v is string sv)
            {
                var t = sv.Trim();
                if (t.Length == 0) throw BadNumber(v);
                string body;
                var isHex = false;
                if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { body = t.Substring(2); isHex = true; }
                else if (t.StartsWith("#")) { body = t.Substring(1); isHex = true; }
                else if (color && (t.Length == 6 || t.Length == 8) && IsHexOnly(t) && HasHexLetter(t))
                { body = t; isHex = true; }
                else { body = t; }
                if (isHex)
                {
                    if (body.Length == 0) throw BadNumber(v);
                    try { return Convert.ToInt64(body, 16); }
                    catch (Exception ex) when (ex is FormatException or OverflowException) { throw BadNumber(v); }
                }
                try
                {
                    return long.Parse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException) { throw BadNumber(v); }
            }
            try { return Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException) { throw BadNumber(v); }
        }
        static bool IsDesignNumber(object o) =>
            o is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
        static bool IsHexOnly(string s)
        {
            foreach (var ch in s)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))) return false;
            return true;
        }
        static bool HasHexLetter(string s)
        {
            foreach (var ch in s)
                if ((ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')) return true;
            return false;
        }
        long L() => ToNum(value, name is "ForeColor" or "BackColor");
        int I() => (int)L();
        bool B() => Convert.ToBoolean(value);
        DateTime D()
        {
            if (value is DateTime dt) return dt;
            var s = S();
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.None, out var parsed)) return parsed;
            throw new ScriptException($"UiDesign: '{s}' is not a valid date/time for '{name}'. Use e.g. 2024-05-31 14:30:00.");
        }
        string S() => value as string ?? Convert.ToString(value) ?? "";
        switch (name)
        {
            case "Name": c.Name = S(); break;
            case "Text": c.Text = S(); break;
            case "Left": c.Left = I(); break;
            case "Top": c.Top = I(); break;
            case "Width": c.Width = Math.Max(1, I()); break;
            case "Height": c.Height = Math.Max(1, I()); break;
            case "Visible": c.Visible = B(); break;
            case "Enabled": c.Enabled = B(); break;
            case "FontName": c.FontName = S(); break;
            case "FontSize": c.FontSize = I(); break;
            case "FontBold": c.FontBold = B(); break;
            case "ForeColor": c.TextColor = value == null ? null : L(); break;
            case "BackColor": c.BackgroundColor = value == null ? null : L(); break;
            case "Checked" when c is CheckBox chk: chk.Checked = B(); break;
            case "Checked" when c is RadioButton rbk: rbk.Checked = B(); break;
            case "Items" when c is ListBox: WriteItems(c, value); break;
            case "Items" when c is ComboBox: WriteItems(c, value); break;
            // A container's contents, written the way its own members write them, so a document and a script land in
            // the same place. Applying a document ADDS: the children it names are the ones it just created, so a
            // container is empty when these arrive.
            case "Columns" when c is ListView lvc:
                foreach (var col in AsColumns(value, name)) lvc.InsertColumn(lvc.ColumnCount, col.Text, col.Width);
                break;
            case "Items" when c is ListView lvr:
                foreach (var cells in AsCellRows(value, name))
                {
                    var row = lvr.InsertItem(lvr.ItemCount, cells.Count > 0 ? cells[0] : "");
                    for (var i = 1; i < cells.Count; i++) lvr.SetItemText(row, i, cells[i]);
                }
                break;
            case "Items" when c is TreeView tvw:
                foreach (var item in AsTreeItems(value, name)) InsertTreeItem(tvw, item, 0);
                break;
            case "Tabs" when c is TabControl tcw:
                foreach (var text in AsStrings(value, name)) tcw.AddTab(text);
                break;
            case "Minimum" when c is TrackBar t1: t1.Minimum = I(); break;
            case "Maximum" when c is TrackBar t2: t2.Maximum = I(); break;
            case "Value" when c is TrackBar t3: t3.Value = I(); break;
            case "Value" when c is DateTimePicker t4: t4.Value = D(); break;
            // The two STYLE WORDS. Equal ones never arrive here (the no-op guard at the top of this method returns),
            // and an apply that does run is the same ApplyWindowStyle every other style call goes through.
            case "Style": c.ModStyle(L(), ~L()); break;
            case "ExStyle": c.ModStyle(L(), ~L(), true); break;
            default: throw new ScriptException($"UiDesign: cannot set '{name}' on {KindOf(c)}");
        }
    }

    // LB_GETCOUNT 0x018B · LB_GETTEXT 0x0189 · CB_GETCOUNT 0x0146 · CB_GETLBText 0x0148
    private static List<object> ReadItemsText(Control c)
    {
        var result = new List<object>();
        if (c.Handle == IntPtr.Zero) return result;
        var msgCount = c is ListBox ? 0x018Bu : 0x0146u;
        var msgText = c is ListBox ? 0x0189u : 0x0148u;
        var count = (int)SendMessageW(c.Handle, msgCount, 0, IntPtr.Zero);
        if (count <= 0) return result;
        // LB_GETTEXT / CB_GETLBText want a real buffer (a NULL length probe is
        // undefined); use one 2048-char buffer and read the returned length.
        var buf = Marshal.AllocHGlobal(2048 * 2);
        try
        {
            for (var i = 0; i < count; i++)
            {
                var len = (int)SendMessageW(c.Handle, msgText, (nuint)i, buf);
                if (len < 0) continue;
                result.Add(Marshal.PtrToStringUni(buf, Math.Min(len, 2048)) ?? "");
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    private static void WriteItems(Control c, object? value)
    {
        var items = new List<string>();
        // Enumerated the way <see cref="Entries"/> does it, and for the same reason: a script's array LITERAL arrives
        // as a raw array (which is an IList) while a Json.Parse'd one is the engine's own list, which is IEnumerable
        // but NOT an IList. Requiring IList therefore emptied a ListBox whose items came from a parsed document, with
        // nothing reported at all (measured: 3 items in, 0 out).
        if (value is System.Collections.IEnumerable en and not string)
        {
            foreach (var item in en)
                items.Add(Str(item));
        }
        if (c is ListBox lb)
        {
            lb.Clear();
            foreach (var it in items) lb.AddItem(it);
        }
        else if (c is ComboBox cmb)
        {
            cmb.Clear();
            foreach (var it in items) cmb.AddItem(it);
        }
    }
}
