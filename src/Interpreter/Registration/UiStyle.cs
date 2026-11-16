namespace ScriptLangCore;

// ============================================================================
// UiStyle, the style flags of a control BY NAME, for a designer's style editor.
//
// ModStyle / StyleAdd / StyleRemove already write the bits (one implementation, two spellings: the Control
// methods and the WinApi functions on an HWND). What a style editor needs on top is the NAMES: a list of
// [ name, bits, extended ] so a panel can show what a control HAS and what it could HAVE, the way Spy++ does,
// instead of a hex word.
//
// A static table, not a reflection walk, for the same reason UiDesign is one: it stays AOT/trim safe and fast.
// Flag values are Win32's own; the ones for a class are the styles that class accepts, and the general WS_* /
// WS_EX_* ones apply to every window. The extended ListView and TreeView families (LVS_EX_*, TVS_EX_*) are NOT
// here on purpose: they travel through LVM_SETEXTENDEDLISTVIEWSTYLE / TVM_SETEXTENDEDSTYLE, not through
// GWL_EXSTYLE, so a panel that wrote them as window styles would set nothing.
// ============================================================================

internal partial class Interpreter
{
    /// <summary>The style-flag table for THIS control's kind, one [name, bits, extended, field, offered] entry
    /// each. The kind-level knowledge (which flags a class accepts, which of them a style panel may offer) is
    /// reached through the CONTROL, like every other member here: the designer's panel asks the control it is
    /// editing instead of a namespace that would need the kind as a string. `offered` folds in the two rules that
    /// used to be separate queries: a flag that only means something on a TOP-LEVEL window is not offered for a
    /// control (a control is a child, and toggling WS_CHILDWINDOW off would leave one that is no longer a child),
    /// and neither is one WINDOWS owns (measured: WS_EX_WINDOWEDGE cannot be set AND cannot be cleared on a created
    /// child button, because WS_EX_DLGMODALFRAME implies it, so offering it would record a change that does nothing).</summary>
    /// <summary>One row of a control's style-flag table, as a script reads it: named members instead of the
    /// positional [name, bits, extended, field, offered] array the table used to project. Registered as a script
    /// type (Scopes.Define + TypeAliases) so its REPORTED name is one a script can also write, and rooted in
    /// AotHints so the AOT trimmer keeps the members that only reflection reads.</summary>
    internal sealed class StyleFlag
    {
        public string Name { get; set; } = "";
        public long Bits { get; set; }
        public bool Extended { get; set; }
        public long Field { get; set; }
        /// <summary>False for a flag a panel should not show: one that only means something on a top-level
        /// window, or one Windows owns (see TopLevelOnly / SystemManaged).</summary>
        public bool Offered { get; set; }
    }
    internal static List<StyleFlag> BuildStyleFlags(Control ctrl)
    {
        var kind = KindOf(ctrl);
        var result = new List<StyleFlag>();
        foreach (var (name, bits, field) in GeneralStyles)
            result.Add(new StyleFlag { Name = name, Bits = bits, Extended = false, Field = field, Offered = IsOfferedStyle(name) });
        foreach (var (name, bits, field) in GeneralExStyles)
            result.Add(new StyleFlag { Name = name, Bits = bits, Extended = true, Field = field, Offered = IsOfferedStyle(name) });
        if (ClassStyles.TryGetValue(kind, out var table))
            foreach (var (name, bits, extended, field) in table)
                result.Add(new StyleFlag { Name = name, Bits = bits, Extended = extended, Field = field, Offered = IsOfferedStyle(name) });
        return result;
    }

    private static bool IsOfferedStyle(string name) => !TopLevelOnly.Contains(name) && !SystemManaged.Contains(name);

    // the FIELD mask of a named flag: 0 for a plain bit, the field's width for a value of one
    /// <summary>Is the named style flag set on this control? The names are the ones StyleFlags() lists. This is the
    /// one implementation behind Control.HasStyle(name), in the same way
    /// ApplyWindowStyle is the one implementation behind Control.ModStyle and WinApi.ModStyle. A flag that lives in a
    /// FIELD is in use when the field EQUALS its value, not when its bits are a subset: 0x7 (BS_GROUPBOX) contains
    /// 0x5 (BS_3STATE) and 0x6 (BS_AUTO3STATE) as bit subsets, and asking "are these bits set" said yes to all three.</summary>
    internal static bool StyleFlagIsSet(Control ctrl, string name)
    {
        if (!TryFindBits(name, ctrl, out var bits, out var isEx))
            throw new ScriptException($"Unknown style flag '{name}' (a WS_EX_* name is looked up in the extended word on its own).");
        var word = ctrl.ReadStyle(isEx);
        if (TryFindField(name, ctrl, out var field) && field != 0) return (word & field) == bits;
        return (word & bits) == bits;
    }

    /// <summary>Set or clear ONE named style flag. FIELD-aware: setting a value clears the rest of its field, so
    /// BS_CHECKBOX followed by BS_RADIOBUTTON cannot OR into 0x7, which is BS_GROUPBOX. The one implementation behind
    /// Control.SetStyle(name, on); the designer records the change for the .ui as well.</summary>
    internal static void StyleFlagSet(Control ctrl, string name, bool on)
    {
        if (!TryFindBits(name, ctrl, out var bits, out var isEx))
            throw new ScriptException($"Unknown style flag '{name}'.");
        if (on)
        {
            var field = TryFindField(name, ctrl, out var f) ? f : 0;
            ctrl.ModStyle(bits, field & ~bits, isEx);
        }
        else
        {
            var field = TryFindField(name, ctrl, out var f2) ? f2 : 0;
            // OFF of a FIELD value CLEARS THE FIELD, which selects its zero value (BS_PUSHBUTTON for a button type,
            // LVS_ICON for a list view). A field always holds a value, so "removing" the one in use cannot mean
            // "nothing"; and clearing the whole field is exactly what a file with no ON entry for that field loads
            // as, so the live window and a saved file agree. Leaving it untouched was wrong twice over: measured, a
            // WS_BORDER OFF moved the word back while a BS_CHECKBOX OFF left the type at 0x2, three clicks running.
            if (field == 0) ctrl.StyleRemove(bits, isEx);
            else ctrl.ModStyle(0, field, isEx);
        }
    }

    private static bool TryFindField(string name, Control ctrl, out long field)
    {
        field = 0;
        var kind = UiKindName(ctrl);
        if (kind != null && ClassStyles.TryGetValue(kind, out var table))
            foreach (var (n, _, _, f) in table)
                if (n == name) { field = f; return true; }
        return false;
    }

    private static bool TryFindBits(string name, Control ctrl, out long bits, out bool extended)
    {
        foreach (var (n, b, _) in GeneralStyles) if (n == name) { bits = b; extended = false; return true; }
        foreach (var (n, b, _) in GeneralExStyles) if (n == name) { bits = b; extended = true; return true; }
        var kind = UiKindName(ctrl);
        if (kind != null && ClassStyles.TryGetValue(kind, out var table))
            foreach (var (n, b, ex, _) in table)
                if (n == name) { bits = b; extended = ex; return true; }
        bits = 0; extended = false;
        return false;
    }

    // The kind a control is, by the same names the designer and the factories use.
    private static string? UiKindName(Control c) =>
        c is Label ? "Label"
        : c is RadioButton ? "RadioButton"
        : c is CheckBox ? "CheckBox"
        : c is TextBox ? "TextBox"
        : c is ListBox ? "ListBox"
        : c is ComboBox ? "ComboBox"
        : c is TrackBar ? "TrackBar"
        : c is ListView ? "ListView"
        : c is TreeView ? "TreeView"
        : c is DateTimePicker ? "DateTimePicker"
        : c is StatusBar ? "StatusBar"
        : c is TabControl ? "TabControl"
        : c is ToolBar ? "ToolBar"
        : c is Button ? "Button"
        : null;

    // The flags WINDOWS will not let a control move. MEASURED, not recalled: a sweep over every flag the table lists
    // for a Button, setting or clearing each one and asking whether it took, leaves exactly these. The first four
    // refuse a SET on a child window (a child cannot be topmost, WS_EX_LAYERED is unsupported for children,
    // WS_EX_NOREDIRECTIONBITMAP and WS_EX_COMPOSITED likewise); WS_EX_WINDOWEDGE is DERIVED, added by Windows for a
    // caption or border frame (a log line WS_DLGFRAME ON moved the extended word from 0x0 to 0x100 by itself).
    private static readonly HashSet<string> SystemManaged = new(StringComparer.OrdinalIgnoreCase)
    {
        "WS_EX_WINDOWEDGE", "WS_EX_TOPMOST", "WS_EX_LAYERED", "WS_EX_NOREDIRECTIONBITMAP", "WS_EX_COMPOSITED",
    };

    private static readonly HashSet<string> TopLevelOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "WS_OVERLAPPED", "WS_POPUP", "WS_MINIMIZE", "WS_MAXIMIZE", "WS_CAPTION", "WS_SYSMENU",
        "WS_THICKFRAME", "WS_SIZEBOX", "WS_ICONIC", "WS_CHILDWINDOW",
    };

    // WS_* : every window, GWL_STYLE
    private static readonly (string Name, long Bits, long Field)[] GeneralStyles =
    [
        ("WS_OVERLAPPED", 0x00000000L, 0L), ("WS_POPUP", 0x80000000L, 0L),
        ("WS_CHILDWINDOW", 0x40000000L, 0L), ("WS_MINIMIZE", 0x20000000L, 0L), ("WS_VISIBLE", 0x10000000L, 0L),
        ("WS_DISABLED", 0x08000000L, 0L), ("WS_CLIPSIBLINGS", 0x04000000L, 0L), ("WS_CLIPCHILDREN", 0x02000000L, 0L),
        ("WS_MAXIMIZE", 0x01000000L, 0L), ("WS_CAPTION", 0x00C00000L, 0L), ("WS_BORDER", 0x00800000L, 0L),
        ("WS_DLGFRAME", 0x00400000L, 0L), ("WS_VSCROLL", 0x00200000L, 0L), ("WS_HSCROLL", 0x00100000L, 0L),
        ("WS_SYSMENU", 0x00080000L, 0L), ("WS_THICKFRAME", 0x00040000L, 0L), ("WS_GROUP", 0x00020000L, 0L),
        ("WS_TABSTOP", 0x00010000L, 0L),
    ];

    // WS_EX_* : every window, GWL_EXSTYLE. The zero-valued ones (WS_EX_LEFT, WS_EX_LTRREADING,
    // WS_EX_RIGHTSCROLLBAR) are listed because Spy++ lists them: they are the absence of the opposite flag.
    private static readonly (string Name, long Bits, long Field)[] GeneralExStyles =
    [
        ("WS_EX_LEFT", 0x00000000L, 0L), ("WS_EX_LTRREADING", 0x00000000L, 0L), ("WS_EX_RIGHTSCROLLBAR", 0x00000000L, 0L),
        ("WS_EX_DLGMODALFRAME", 0x00000001L, 0L), ("WS_EX_NOPARENTNOTIFY", 0x00000004L, 0L), ("WS_EX_TOPMOST", 0x00000008L, 0L),
        ("WS_EX_ACCEPTFILES", 0x00000010L, 0L), ("WS_EX_TRANSPARENT", 0x00000020L, 0L), ("WS_EX_MDICHILD", 0x00000040L, 0L),
        ("WS_EX_TOOLWINDOW", 0x00000080L, 0L), ("WS_EX_WINDOWEDGE", 0x00000100L, 0L), ("WS_EX_CLIENTEDGE", 0x00000200L, 0L),
        ("WS_EX_CONTEXTHELP", 0x00000400L, 0L), ("WS_EX_RIGHT", 0x00001000L, 0L), ("WS_EX_RTLREADING", 0x00002000L, 0L),
        ("WS_EX_LEFTSCROLLBAR", 0x00004000L, 0L), ("WS_EX_CONTROLPARENT", 0x00010000L, 0L),
        ("WS_EX_STATICEDGE", 0x00020000L, 0L), ("WS_EX_APPWINDOW", 0x00040000L, 0L), ("WS_EX_LAYERED", 0x00080000L, 0L),
        ("WS_EX_NOINHERITLAYOUT", 0x00100000L, 0L), ("WS_EX_NOREDIRECTIONBITMAP", 0x00200000L, 0L),
        ("WS_EX_COMPOSITED", 0x02000000L, 0L),
    ];

    // The class-specific families, per kind. A style with bits 0 is that class's default (BS_PUSHBUTTON,
    // SS_LEFT, ES_LEFT, DTS_SHORTDATEFORMAT): it is listed so the panel can show it as the value in use.
    private static readonly Dictionary<string, (string Name, long Bits, bool Extended, long Field)[]> ClassStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Button"] =
        [
            ("BS_PUSHBUTTON", 0x00000000L, false, 0xFL), ("BS_DEFPUSHBUTTON", 0x00000001L, false, 0xFL),
            ("BS_CHECKBOX", 0x00000002L, false, 0xFL), ("BS_AUTOCHECKBOX", 0x00000003L, false, 0xFL),
            ("BS_RADIOBUTTON", 0x00000004L, false, 0xFL), ("BS_3STATE", 0x00000005L, false, 0xFL),
            ("BS_AUTO3STATE", 0x00000006L, false, 0xFL), ("BS_GROUPBOX", 0x00000007L, false, 0xFL),
            ("BS_AUTORADIOBUTTON", 0x00000009L, false, 0xFL), ("BS_OWNERDRAW", 0x0000000BL, false, 0xFL),
            ("BS_LEFTTEXT", 0x00000020L, false, 0L), ("BS_TEXT", 0x00000000L, false, 0xFL),
        ],
        ["CheckBox"] =
        [
            ("BS_CHECKBOX", 0x00000002L, false, 0xFL), ("BS_AUTOCHECKBOX", 0x00000003L, false, 0xFL),
            ("BS_3STATE", 0x00000005L, false, 0xFL), ("BS_AUTO3STATE", 0x00000006L, false, 0xFL),
            ("BS_LEFTTEXT", 0x00000020L, false, 0L), ("BS_TEXT", 0x00000000L, false, 0xFL),
        ],
        ["RadioButton"] =
        [
            ("BS_RADIOBUTTON", 0x00000004L, false, 0xFL), ("BS_AUTORADIOBUTTON", 0x00000009L, false, 0xFL),
            ("BS_LEFTTEXT", 0x00000020L, false, 0L), ("BS_TEXT", 0x00000000L, false, 0xFL),
        ],
        ["Label"] =
        [
            ("SS_LEFT", 0x00000000L, false, 0x1FL), ("SS_CENTER", 0x00000001L, false, 0x1FL), ("SS_RIGHT", 0x00000002L, false, 0x1FL),
            ("SS_ICON", 0x00000003L, false, 0x1FL), ("SS_LEFTNOWORDWRAP", 0x0000000CL, false, 0x1FL),
            ("SS_OWNERDRAW", 0x0000000DL, false, 0x1FL), ("SS_BITMAP", 0x0000000EL, false, 0x1FL),
            ("SS_NOPREFIX", 0x00000080L, false, 0L), ("SS_NOTIFY", 0x00000100L, false, 0L),
            ("SS_CENTERIMAGE", 0x00000200L, false, 0L), ("SS_SUNKEN", 0x00001000L, false, 0L),
        ],
        ["TextBox"] =
        [
            ("ES_LEFT", 0x0000L, false, 0x3L), ("ES_CENTER", 0x0001L, false, 0x3L), ("ES_RIGHT", 0x0002L, false, 0x3L),
            ("ES_MULTILINE", 0x0004L, false, 0L), ("ES_UPPERCASE", 0x0008L, false, 0L), ("ES_LOWERCASE", 0x0010L, false, 0L),
            ("ES_PASSWORD", 0x0020L, false, 0L), ("ES_AUTOVSCROLL", 0x0040L, false, 0L), ("ES_AUTOHSCROLL", 0x0080L, false, 0L),
            ("ES_NOHIDESEL", 0x0100L, false, 0L), ("ES_OEMCONVERT", 0x0400L, false, 0L), ("ES_READONLY", 0x0800L, false, 0L),
            ("ES_WANTRETURN", 0x1000L, false, 0L), ("ES_NUMBER", 0x2000L, false, 0L),
        ],
        ["ListBox"] =
        [
            ("LBS_NOTIFY", 0x0001L, false, 0L), ("LBS_SORT", 0x0002L, false, 0L), ("LBS_MULTIPLESEL", 0x0008L, false, 0L),
            ("LBS_HASSTRINGS", 0x0040L, false, 0L), ("LBS_NOINTEGRALHEIGHT", 0x0100L, false, 0L),
            ("LBS_MULTICOLUMN", 0x0200L, false, 0L), ("LBS_WANTKEYBOARDINPUT", 0x0400L, false, 0L),
            ("LBS_EXTENDEDSEL", 0x0800L, false, 0L), ("LBS_DISABLENOSCROLL", 0x1000L, false, 0L),
        ],
        ["ComboBox"] =
        [
            ("CBS_SIMPLE", 0x0001L, false, 0x3L), ("CBS_DROPDOWN", 0x0002L, false, 0x3L), ("CBS_DROPDOWNLIST", 0x0003L, false, 0x3L),
            ("CBS_AUTOHSCROLL", 0x0040L, false, 0L), ("CBS_OEMCONVERT", 0x0080L, false, 0L), ("CBS_SORT", 0x0100L, false, 0L),
            ("CBS_HASSTRINGS", 0x0200L, false, 0L), ("CBS_NOINTEGRALHEIGHT", 0x0400L, false, 0L),
            ("CBS_DISABLENOSCROLL", 0x0800L, false, 0L),
        ],
        ["TrackBar"] =
        [
            ("TBS_AUTOTICKS", 0x0001L, false, 0L), ("TBS_VERT", 0x0002L, false, 0L), ("TBS_TOP", 0x0004L, false, 0xCL),
            ("TBS_BOTH", 0x0008L, false, 0xCL), ("TBS_NOTICKS", 0x0010L, false, 0L), ("TBS_ENABLESELRANGE", 0x0020L, false, 0L),
            ("TBS_NOTHUMB", 0x0080L, false, 0L), ("TBS_TOOLTIPS", 0x0100L, false, 0L), ("TBS_REVERSED", 0x0200L, false, 0L),
            ("TBS_DOWNISLEFT", 0x0400L, false, 0L),
        ],
        ["ListView"] =
        [
            ("LVS_ICON", 0x0000L, false, 0x3L), ("LVS_REPORT", 0x0001L, false, 0x3L), ("LVS_SMALLICON", 0x0002L, false, 0x3L),
            ("LVS_LIST", 0x0003L, false, 0x3L), ("LVS_SINGLESEL", 0x0004L, false, 0L), ("LVS_SHOWSELALWAYS", 0x0008L, false, 0L),
            ("LVS_SORTASCENDING", 0x0010L, false, 0L), ("LVS_SORTDESCENDING", 0x0020L, false, 0L),
            ("LVS_AUTOARRANGE", 0x0100L, false, 0L), ("LVS_EDITLABELS", 0x0200L, false, 0L),
            ("LVS_ALIGNLEFT", 0x0800L, false, 0L), ("LVS_OWNERDATA", 0x1000L, false, 0L), ("LVS_NOSCROLL", 0x2000L, false, 0L),
            ("LVS_NOCOLUMNHEADER", 0x4000L, false, 0L), ("LVS_NOSORTHEADER", 0x8000L, false, 0L),
        ],
        ["TreeView"] =
        [
            ("TVS_HASBUTTONS", 0x0001L, false, 0L), ("TVS_HASLINES", 0x0002L, false, 0L), ("TVS_LINESATROOT", 0x0004L, false, 0L),
            ("TVS_EDITLABELS", 0x0008L, false, 0L), ("TVS_DISABLEDRAGDROP", 0x0010L, false, 0L),
            ("TVS_SHOWSELALWAYS", 0x0020L, false, 0L), ("TVS_CHECKBOXES", 0x0100L, false, 0L),
            ("TVS_TRACKSELECT", 0x0200L, false, 0L), ("TVS_SINGLEEXPAND", 0x0400L, false, 0L),
            ("TVS_INFOTIP", 0x0800L, false, 0L), ("TVS_FULLROWSELECT", 0x1000L, false, 0L), ("TVS_NOSCROLL", 0x2000L, false, 0L),
            ("TVS_NONEVENHEIGHT", 0x4000L, false, 0L),
        ],
        ["DateTimePicker"] =
        [
            ("DTS_UPDOWN", 0x0001L, false, 0L), ("DTS_SHOWNONE", 0x0002L, false, 0L),
            ("DTS_SHORTDATEFORMAT", 0x0000L, false, 0xFL), ("DTS_LONGDATEFORMAT", 0x0004L, false, 0xFL),
            ("DTS_TIMEFORMAT", 0x0009L, false, 0xFL), ("DTS_APPCANPARSE", 0x0010L, false, 0L), ("DTS_RIGHTALIGN", 0x0020L, false, 0L),
        ],
        ["StatusBar"] =
        [
            ("SBARS_SIZEGRIP", 0x0100L, false, 0L), ("SBARS_TOOLTIPS", 0x0800L, false, 0L),
            ("SBT_OWNERDRAW", 0x1000L, false, 0L), ("SBT_NOBORDERS", 0x0100L, false, 0L), ("SBT_POPOUT", 0x0200L, false, 0L),
        ],
        ["TabControl"] =
        [
            ("TCS_SCROLLOPPOSITE", 0x0001L, false, 0L), ("TCS_BOTTOM", 0x0002L, false, 0L), ("TCS_RIGHT", 0x0002L, false, 0L),
            ("TCS_MULTISELECT", 0x0004L, false, 0L), ("TCS_FLATBUTTONS", 0x0008L, false, 0L),
            ("TCS_FORCEICONLEFT", 0x0010L, false, 0L), ("TCS_FORCELABELLEFT", 0x0020L, false, 0L),
            ("TCS_HOTTRACK", 0x0040L, false, 0L), ("TCS_VERTICAL", 0x0080L, false, 0L), ("TCS_BUTTONS", 0x0100L, false, 0L),
            ("TCS_MULTILINE", 0x0200L, false, 0L), ("TCS_FIXEDWIDTH", 0x0400L, false, 0L),
            ("TCS_RAGGEDRIGHT", 0x0800L, false, 0L), ("TCS_FOCUSONBUTTONDOWN", 0x1000L, false, 0L),
            ("TCS_OWNERDRAWFIXED", 0x2000L, false, 0L), ("TCS_TOOLTIPS", 0x4000L, false, 0L),
            ("TCS_FOCUSNEVER", 0x8000L, false, 0L),
        ],
        ["ToolBar"] =
        [
            ("TBSTYLE_FLAT", 0x0800L, false, 0L), ("TBSTYLE_LIST", 0x1000L, false, 0L),
            ("TBSTYLE_TRANSPARENT", 0x8000L, false, 0L), ("CCS_TOP", 0x0001L, false, 0L), ("CCS_BOTTOM", 0x0003L, false, 0L),
            ("CCS_NODIVIDER", 0x0040L, false, 0L), ("CCS_NORESIZE", 0x0004L, false, 0L), ("CCS_NOPARENTALIGN", 0x0008L, false, 0L),
        ],
    };
}
