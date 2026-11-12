namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    /// <summary>
    /// Provides data for events that can be canceled.
    /// </summary>
    /// <remarks>Use this class as the base for event data in events that support cancellation. Event handlers
    /// can set the <see cref="Cancel"/> property to <see langword="true"/> to indicate that the event should be
    /// canceled.</remarks>
    /// <param name="cancel">A value indicating whether the event should be canceled. Set to <see langword="true"/> to request cancellation;
    /// otherwise, <see langword="false"/>.</param>
    public class CancelEventArgs(bool cancel) : IEventArguments
    {
        public string Event { get; set; } = "";
        public bool Cancel { get; set; } = cancel;
        public CancelEventArgs() : this(false) { }
        public override string ToString() => $"{{Cancel = {Cancel}}}";
    }

    public struct EventArgs : IEventArguments
    {
        // NO initializer is possible on this one: a struct with field initializers must declare a constructor
        // (CS8983), and this struct is built as `new()` in places the engine cannot change. The backing field is
        // what makes an Event that was never stamped read as "" the way the classes and the other structs read,
        // without a constructor to run.
        private string? _event;
        public string Event { get => _event ?? ""; set => _event = value; }
        public static readonly EventArgs Empty = new();
    }

    public enum MouseButtons
    {
        None = 0,
        Left = 0x100000,
        Right = 0x200000,
        Middle = 0x400000,
        XButton1 = 0x800000,
        XButton2 = 0x1000000
    }

    /// <summary>What every event argument answers: the name of the event it belongs to. The engine stamps it at the
    /// raise, through this interface, so ONE line at the funnel serves every argument type and the same name reaches
    /// the sender's LastEvent. A struct passed through the interface is BOXED, which is what makes the stamp survive:
    /// the box is a copy per call, so a shared value like EventArgs.Empty is never touched.</summary>
    public interface IEventArguments
    {
        string Event { get; set; }
    }

    /// <summary>The arguments of a Command event: the WM_COMMAND a window receives from a menu, an accelerator or a
    /// control, reported as ONE value instead of two loose parameters. It used to be raised as (id, hwndCtrl)
    /// positionally, so a handler declared (Control s, EventArgs e) took the second value as `e` and the engine
    /// reported its own type mismatch for it. Property accessors, not fields: the binder reads properties, and the
    /// reflection verbs read Get/Keys from the same shape.</summary>
    /// <summary>The arguments of a DropFiles event: the drop handle Windows hands the window, which a script passes
    /// to WinApi.DragQueryFile style calls to read the names. It was raised as a bare IntPtr, so a handler declared
    /// (Control s, EventArgs e) took the handle as `e` and the engine warned about the type, the same shape
    /// CommandEventArgs was introduced for.</summary>
    public struct DropFilesEventArgs(IntPtr hDrop) : IEventArguments
    {
        public string Event { get; set; } = "";
        public IntPtr hDrop = hDrop;
        public IntPtr HDrop => hDrop;

        /// <summary>How many files were dropped.</summary>
        public object? FileCount() => (long)Interpreter.DragQueryFileW(hDrop, 0xFFFFFFFF, IntPtr.Zero, 0);

        /// <summary>The path of the dropped file at that index. The engine releases the drop list as soon as the
        /// handler returns, so read what you need while you are inside it.</summary>
        public string FileName(int index)
        {
            var needed = (int)Interpreter.DragQueryFileW(hDrop, (uint)index, IntPtr.Zero, 0);
            if (needed <= 0) { return ""; }
            var buffer = Marshal.AllocHGlobal((needed + 1) * 2);
            try
            {
                Interpreter.DragQueryFileW(hDrop, (uint)index, buffer, (uint)(needed + 1));
                return Marshal.PtrToStringUni(buffer) ?? "";
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    /// <summary>The arguments of a Timer event: which timer fired. It was raised as a bare int, the same shape
    /// DropFilesEventArgs and CommandEventArgs replaced.</summary>
    public struct TimerEventArgs(int timerId) : IEventArguments
    {
        public string Event { get; set; } = "";
        public int timerId = timerId;
        public int TimerId => timerId;
    }

    /// <summary>The arguments of a ResizeEvent: the new client size. It used to pass a Size, which is a general
    /// type whose Width and Height are FIELDS, and a field is not script-readable, so this event answered nothing
    /// useful and was the one outside the IEventArguments family. Property accessors, like every other argument
    /// type, so Width, Height and the event name all read.</summary>
    public struct ResizeEventArgs(int width, int height) : IEventArguments
    {
        public string Event { get; set; } = "";
        public int Width { get; } = width;
        public int Height { get; } = height;
    }

    public struct CommandEventArgs(int id, IntPtr hwnd, uint time = 0) : IEventArguments
    {
        public string Event { get; set; } = "";
        public int id = id;
        public IntPtr hwnd = hwnd;
        public int Id => id;
        public uint time = time;
        /// <summary>WHEN the message that raised this arrived, from the engine's own Msg.time. Two events carrying the
        /// same time are the SAME delivery seen again, which is what makes it usable as a key: a handler that must act
        /// ONCE can compare it against the last one it saw, and a genuine second click carries a different time.</summary>

        /// <summary>WHEN this message was posted, as a UTC instant. Msg.time is a tick since boot, so the boot moment
        /// is taken from the tick count and the difference applied, which makes it a real DATE a log line can print
        /// while keeping the relation the key needs: two events from ONE delivery carry the same instant. The raw
        /// tick stays available as Time, since comparing two of those needs no clock at all.</summary>
        public DateTime UtcDateTime => DateTime.UtcNow.AddMilliseconds(-(Environment.TickCount64 - time));
        public uint Time => time;
        public IntPtr Hwnd => hwnd;
    }

    public struct MouseEventArgs(int x, int y, MouseButtons button, int clicks, int delta) : IEventArguments
    {
        public string Event { get; set; } = "";
        public int x = x, y = y;
        public MouseButtons button = button;
        public int clicks = clicks, delta = delta;
        // Property accessors for the script binder (fields are not script-readable). Button, Clicks and Delta were
        // FIELDS only, so reflecting a mouse event showed two of its five members: measured, a dump and a Keys()
        // both answered just X and Y while the button and the click count had been carried all along. The fields
        // stay lowercase and private to the type, exactly as x and y do, so the property names can be the plain
        // ones a script would write.
        public int X => x;
        public int Y => y;
        public MouseButtons Button => button;
        public int Clicks => clicks;
        public int Delta => delta;
    }

    public struct PaintEventArgs(IntPtr hwnd, IntPtr hdc, Rect clipRect) : IEventArguments
    {
        public string Event { get; set; } = "";
        public IntPtr Hwnd { get; } = hwnd;
        public IntPtr Hdc { get; } = hdc;
        public int Left { get; } = clipRect.Left;
        public int Top { get; } = clipRect.Top;
        public int Right { get; } = clipRect.Right;
        public int Bottom { get; } = clipRect.Bottom;

        // The drawing surface. A Paint handler already holds the DC, so nothing here fetches one, and a helper that
        // makes a GDI object DELETES it before returning: a repaint loop that leaks one pen per frame is a process
        // that dies after a few thousand frames, which is the reason these exist rather than the raw verbs.
        private const int PsSolid = 0;       // CreatePen's style
        private const int NullBrush = 5;     // GetStockObject: the hollow brush, so only the outline draws
        private const int NullPen = 8;       // ... and the no-line pen, so only the fill draws
        private const int Transparent = 1;   // SetBkMode

        /// <summary>The text colour for every draw that follows, the way GDI itself keeps it.</summary>
        public object? SetTextColor(long color) => (long)Interpreter.SetTextColor(Hdc, (uint)color);        /// <summary>The background mode for text (1 is TRANSPARENT, which is what a control's own painting wants).</summary>
        public object? SetBkMode(int mode) => (long)Interpreter.SetBkMode(Hdc, mode);
        /// <summary>The background colour for text drawn in OPAQUE mode.</summary>
        public object? SetBkColor(long color) => (long)Interpreter.SetBkColor(Hdc, (uint)color);

        /// <summary>A filled rectangle of the given colour. Whatever pen and brush the DC had are put back.</summary>
        public object? Fill(int x, int y, int width, int height, long color)
        {
            var brush = Interpreter.CreateSolidBrush((uint)color);
            var oldBrush = Interpreter.SelectObject(Hdc, brush);
            var oldPen = Interpreter.SelectObject(Hdc, Interpreter.GetStockObject(NullPen));
            Interpreter.RectangleW(Hdc, x, y, x + width, y + height);
            Interpreter.SelectObject(Hdc, oldPen);
            Interpreter.SelectObject(Hdc, oldBrush);
            Interpreter.DeleteObject(brush);
            return null;
        }

        /// <summary>A rectangle filled with the DC's CURRENT brush, for a script that selected its own.</summary>
        public object? FillRect(int x, int y, int width, int height)
        {
            Interpreter.RectangleW(Hdc, x, y, x + width, y + height);
            return null;
        }

        /// <summary>A rectangle OUTLINE of the given colour and width (0 means one pixel).</summary>
        public object? Frame(int x, int y, int width, int height, long color, int lineWidth = 0)
        {
            var pen = Interpreter.CreatePen(PsSolid, lineWidth < 1 ? 1 : lineWidth, (uint)color);
            var oldPen = Interpreter.SelectObject(Hdc, pen);
            var oldBrush = Interpreter.SelectObject(Hdc, Interpreter.GetStockObject(NullBrush));
            Interpreter.RectangleW(Hdc, x, y, x + width, y + height);
            Interpreter.SelectObject(Hdc, oldBrush);
            Interpreter.SelectObject(Hdc, oldPen);
            Interpreter.DeleteObject(pen);
            return null;
        }

        /// <summary>A rectangle OUTLINE with the DC's CURRENT pen, for a script that selected its own.</summary>
        public object? Stroke(int x, int y, int width, int height)
        {
            var oldBrush = Interpreter.SelectObject(Hdc, Interpreter.GetStockObject(NullBrush));
            Interpreter.RectangleW(Hdc, x, y, x + width, y + height);
            Interpreter.SelectObject(Hdc, oldBrush);
            return null;
        }

        /// <summary>An ellipse OUTLINE inside the given box, of the given colour and width.</summary>
        public object? Ellipse(int x, int y, int width, int height, long color, int lineWidth = 0)
        {
            var pen = Interpreter.CreatePen(PsSolid, lineWidth < 1 ? 1 : lineWidth, (uint)color);
            var oldPen = Interpreter.SelectObject(Hdc, pen);
            var oldBrush = Interpreter.SelectObject(Hdc, Interpreter.GetStockObject(NullBrush));
            Interpreter.Ellipse(Hdc, x, y, x + width, y + height);
            Interpreter.SelectObject(Hdc, oldBrush);
            Interpreter.SelectObject(Hdc, oldPen);
            Interpreter.DeleteObject(pen);
            return null;
        }

        /// <summary>An ellipse OUTLINE with the DC's CURRENT pen, for a script that selected its own.</summary>
        public object? StrokeEllipse(int x, int y, int width, int height)
        {
            var oldBrush = Interpreter.SelectObject(Hdc, Interpreter.GetStockObject(NullBrush));
            Interpreter.Ellipse(Hdc, x, y, x + width, y + height);
            Interpreter.SelectObject(Hdc, oldBrush);
            return null;
        }

        /// <summary>A FILLED ellipse inside the given box.</summary>
        public object? Disc(int x, int y, int width, int height, long color)
        {
            var brush = Interpreter.CreateSolidBrush((uint)color);
            var oldBrush = Interpreter.SelectObject(Hdc, brush);
            var oldPen = Interpreter.SelectObject(Hdc, Interpreter.GetStockObject(NullPen));
            Interpreter.Ellipse(Hdc, x, y, x + width, y + height);
            Interpreter.SelectObject(Hdc, oldPen);
            Interpreter.SelectObject(Hdc, oldBrush);
            Interpreter.DeleteObject(brush);
            return null;
        }

        /// <summary>A line of the given colour and width (0 means one pixel).</summary>
        public object? Line(int x1, int y1, int x2, int y2, long color, int lineWidth = 0)
        {
            var pen = Interpreter.CreatePen(PsSolid, lineWidth < 1 ? 1 : lineWidth, (uint)color);
            var oldPen = Interpreter.SelectObject(Hdc, pen);
            Interpreter.MoveToEx(Hdc, x1, y1, IntPtr.Zero);
            Interpreter.LineTo(Hdc, x2, y2);
            Interpreter.SelectObject(Hdc, oldPen);
            Interpreter.DeleteObject(pen);
            return null;
        }

        /// <summary>A line with the DC's CURRENT pen, for a script that selected its own.</summary>
        public object? StrokeLine(int x1, int y1, int x2, int y2)
        {
            Interpreter.MoveToEx(Hdc, x1, y1, IntPtr.Zero);
            Interpreter.LineTo(Hdc, x2, y2);
            return null;
        }

        /// <summary>Text at a point, in the CURRENT text colour (see SetTextColor).</summary>
        public object? Text(int x, int y, string text)
        {
            var s = text ?? "";
            var oldMode = Interpreter.SetBkMode(Hdc, Transparent);
            Interpreter.TextOutW(Hdc, x, y, s, s.Length);
            Interpreter.SetBkMode(Hdc, oldMode);
            return null;
        }

        /// <summary>Text at a point in a colour of its own, which the DC keeps afterwards.</summary>
        public object? Text(int x, int y, string text, long color)
        {
            SetTextColor(color);
            return Text(x, y, text);
        }

        /// <summary>Text inside a box, with DrawText's format flags (DT_CENTER, DT_LEFT, DT_SINGLELINE and so on).</summary>
        public object? Text(string text, int x, int y, int width, int height, long color, int format = 0)
        {
            var s = text ?? "";
            var box = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
            SetTextColor(color);
            var oldMode = Interpreter.SetBkMode(Hdc, Transparent);
            var answer = Interpreter.DrawTextW(Hdc, s, s.Length, ref box, unchecked((uint)format));
            Interpreter.SetBkMode(Hdc, oldMode);
            return (long)answer;
        }

        /// <summary>A solid brush HANDLE, for a script that keeps one across calls. Delete it when done.</summary>
        public long CreateBrush(long color) => Interpreter.CreateSolidBrush((uint)color).ToInt64();
        /// <summary>A pen HANDLE of the given colour and width, for a script that keeps one across calls.</summary>
        public long CreatePen(long color, int lineWidth = 0) => Interpreter.CreatePen(PsSolid, lineWidth < 1 ? 1 : lineWidth, (uint)color).ToInt64();
        /// <summary>Put a GDI object in the DC, answering the one it displaced.</summary>
        public long SelectObject(long handle) => Interpreter.SelectObject(Hdc, (nint)handle).ToInt64();
        /// <summary>Put back or throw away a GDI object. Answers 1 when it worked.</summary>
        public object? DeleteObject(long handle) => Interpreter.DeleteObject((nint)handle) ? 1L : 0L;
        /// <summary>A stock object by index (5 is the hollow brush, 8 the no-line pen).</summary>
        public long StockObject(int index) => Interpreter.GetStockObject(index).ToInt64();

        // Themed parts of THIS window. The theme handle is opened and closed around the one call, because holding
        // one across paints means a handle per repaint or a stale one. The class name is what Windows asks for
        // ("BUTTON", "EDIT", "SCROLLBAR", "HEADER", "TAB", ...), the part and state ids are the theme's own.
        /// <summary>Draw a themed part into the surface (its window class, part id, state and box). Answers the
        /// theme's own HRESULT, or -1 when this machine has no theme for that class.</summary>
        public object? ThemePart(string windowClass, int part, int state, int x, int y, int width, int height)
        {
            var theme = Interpreter.OpenThemeData(Hwnd, windowClass ?? "");
            if (theme == IntPtr.Zero) { return -1L; }
            var rect = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
            var answer = (long)Interpreter.DrawThemeBackground(theme, Hdc, part, state, ref rect, IntPtr.Zero);
            Interpreter.CloseThemeData(theme);
            return answer;
        }

        /// <summary>Draw a themed part's TEXT into the surface, with DrawText's flags (DT_CENTER and friends).</summary>
        public object? ThemeText(string windowClass, int part, int state, string text, int x, int y, int width, int height, int flags = 0)
        {
            var theme = Interpreter.OpenThemeData(Hwnd, windowClass ?? "");
            if (theme == IntPtr.Zero) { return -1L; }
            var rect = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
            var s = text ?? "";
            var answer = (long)Interpreter.DrawThemeText(theme, Hdc, part, state, s, s.Length, unchecked((uint)flags), 0, ref rect);
            Interpreter.CloseThemeData(theme);
            return answer;
        }
    }

    public enum KeyCodes
    {
        None = 0,

        // Control Keys
        Enter = 0x0D,
        Escape = 0x1B,
        Delete = 0x2E,
        Space = 0x20,

        // Navigation Keys
        Left = 0x25,
        Up = 0x26,
        Right = 0x27,
        Down = 0x28,

        // Alphabet
        A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46, G = 0x47,
        H = 0x48, I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C, M = 0x4D, N = 0x4E,
        O = 0x4F, P = 0x50, Q = 0x51, R = 0x52, S = 0x53, T = 0x54, U = 0x55,
        V = 0x56, W = 0x57, X = 0x58, Y = 0x59, Z = 0x5A,

        // Modifiers and Additional Keys
        Shift = 0x10,
        Control = 0x11,
        Alt = 0x12,
        Tab = 0x09,
        Backspace = 0x08,
        CapsLock = 0x14,
        PageUp = 0x21,
        PageDown = 0x22,
        End = 0x23,
        Home = 0x24,
        Insert = 0x2D,
        PrintScreen = 0x2C,
        Pause = 0x13,
        ScrollLock = 0x91,
        NumLock = 0x90,
        LeftWindows = 0x5B,
        RightWindows = 0x5C,

        // Digits
        D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34,
        D5 = 0x35, D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39,

        // Function keys. Without these a key like F12 had no name at all, so a dump showed its raw code (123)
        // where A and Control showed a name, and the same member could be a name or a number depending on the key.
        F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
        F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
        F13 = 0x7C, F14 = 0x7D, F15 = 0x7E, F16 = 0x7F, F17 = 0x80, F18 = 0x81,
        F19 = 0x82, F20 = 0x83, F21 = 0x84, F22 = 0x85, F23 = 0x86, F24 = 0x87,

        // The numeric keypad
        NumPad0 = 0x60, NumPad1 = 0x61, NumPad2 = 0x62, NumPad3 = 0x63, NumPad4 = 0x64,
        NumPad5 = 0x65, NumPad6 = 0x66, NumPad7 = 0x67, NumPad8 = 0x68, NumPad9 = 0x69,
        Multiply = 0x6A, Add = 0x6B, Separator = 0x6C, Subtract = 0x6D, Decimal = 0x6E, Divide = 0x6F
    }

    private static KeyEventArgs MakeKeyArgs(nint vk, nint lParam)
    {
        static bool pressed(int v) => (GetKeyState(v) & 0x8000) != 0;
        // The Alt flag comes from the MESSAGE: bit 29 of lParam is the context code, set for every message
        // of an Alt combination INCLUDING the Alt key own release, which arrives as a plain WM_KEYUP and
        // therefore read alt=false while GetKeyState already saw the key as up. Shift and Control are not in
        // the message and stay on GetKeyState, which is right: they are still held during their combination.
        // Three sources, cheapest truth first: the KEY is Alt (VK_MENU), which needs no message at all and is
        // the case that matters most, then the message context bit, then the keyboard. Windows does NOT set
        // the context bit on the Alt key own release, so the bit alone left that event reading alt=false.
        bool altContext = (vk & 0xFF) == 0x12 || (lParam.ToInt64() & 0x20000000) != 0;
        return new KeyEventArgs((KeyCodes)vk, altContext || pressed(0x12), pressed(0x11), pressed(0x10));
    }

    public struct KeyEventArgs(KeyCodes keys, bool alt, bool control, bool shift) : IEventArguments
    {
        public string Event { get; set; } = "";
        /// <summary>The key's NUMERIC code (a virtual-key code), always the same shape whatever key it is: 65 for A,
        /// 123 for F12, 17 for Control. It used to be the KeyCodes enum alone, which reads as a NAME when the value
        /// has one and as a NUMBER when it does not, so the same member was a string in one dump and an integer in
        /// the next. KeyValue answers the name.</summary>
        public int KeyCode { get; } = (int)keys;
        /// <summary>The key's NAME, always a string: "A", "F12", "Control". Long or short, it is one shape, which
        /// is what a label or a log wants.</summary>
        public string KeyValue { get; } = keys.ToString();
        public bool Alt { get; } = alt;
        public bool Control { get; } = control;
        public bool Shift { get; } = shift;
    }

    public struct Size(int width, int height)
    {
        public int Width = width, Height = height;
    }

    // ==========================================================================
    // Win32 common dialog P/Invokes (AOT-safe)
    // ==========================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        public IntPtr lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpstrTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_HIDEREADONLY = 0x00000004;

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

    // ==========================================================================
    // OpenFileDialog
    // ==========================================================================
}

