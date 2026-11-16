namespace ScriptLangCore;
/// <summary>
/// Global hotkey registration via raw Win32 RegisterHotKey/UnregisterHotKey.
/// Hotkeys fire on the invisible form message window (Ui.cs StaticWndProc).
/// </summary>
internal static partial class GlobalHotKey
{
    // Modifier bitmask constants
    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CTRL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private static int _nextId;
    private static readonly ConcurrentDictionary<int, object> Callbacks = new();

    private static readonly Dictionary<string, uint> KeyNameToVk = new(StringComparer.OrdinalIgnoreCase)
    {
        // Letters
        ["A"] = 0x41,
        ["B"] = 0x42,
        ["C"] = 0x43,
        ["D"] = 0x44,
        ["E"] = 0x45,
        ["F"] = 0x46,
        ["G"] = 0x47,
        ["H"] = 0x48,
        ["I"] = 0x49,
        ["J"] = 0x4A,
        ["K"] = 0x4B,
        ["L"] = 0x4C,
        ["M"] = 0x4D,
        ["N"] = 0x4E,
        ["O"] = 0x4F,
        ["P"] = 0x50,
        ["Q"] = 0x51,
        ["R"] = 0x52,
        ["S"] = 0x53,
        ["T"] = 0x54,
        ["U"] = 0x55,
        ["V"] = 0x56,
        ["W"] = 0x57,
        ["X"] = 0x58,
        ["Y"] = 0x59,
        ["Z"] = 0x5A,
        // Digits
        ["0"] = 0x30,
        ["1"] = 0x31,
        ["2"] = 0x32,
        ["3"] = 0x33,
        ["4"] = 0x34,
        ["5"] = 0x35,
        ["6"] = 0x36,
        ["7"] = 0x37,
        ["8"] = 0x38,
        ["9"] = 0x39,
        // Function keys
        ["F1"] = 0x70,
        ["F2"] = 0x71,
        ["F3"] = 0x72,
        ["F4"] = 0x73,
        ["F5"] = 0x74,
        ["F6"] = 0x75,
        ["F7"] = 0x76,
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
        ["F13"] = 0x7C,
        ["F14"] = 0x7D,
        ["F15"] = 0x7E,
        ["F16"] = 0x7F,
        ["F17"] = 0x80,
        ["F18"] = 0x81,
        ["F19"] = 0x82,
        ["F20"] = 0x83,
        ["F21"] = 0x84,
        ["F22"] = 0x85,
        ["F23"] = 0x86,
        ["F24"] = 0x87,
        // Special keys
        ["Enter"] = 0x0D,
        ["Escape"] = 0x1B,
        ["Space"] = 0x20,
        ["Tab"] = 0x09,
        ["Back"] = 0x08,
        ["Delete"] = 0x2E,
        ["Insert"] = 0x2D,
        ["Home"] = 0x24,
        ["End"] = 0x23,
        ["PageUp"] = 0x21,
        ["PageDown"] = 0x22,
        ["PrintScreen"] = 0x2C,
        ["Pause"] = 0x13,
        // Arrow keys
        ["Up"] = 0x26,
        ["Down"] = 0x28,
        ["Left"] = 0x25,
        ["Right"] = 0x27,
        // Numpad
        ["NumPad0"] = 0x60,
        ["NumPad1"] = 0x61,
        ["NumPad2"] = 0x62,
        ["NumPad3"] = 0x63,
        ["NumPad4"] = 0x64,
        ["NumPad5"] = 0x65,
        ["NumPad6"] = 0x66,
        ["NumPad7"] = 0x67,
        ["NumPad8"] = 0x68,
        ["NumPad9"] = 0x69,
        ["Multiply"] = 0x6A,
        ["Add"] = 0x6B,
        ["Subtract"] = 0x6D,
        ["Divide"] = 0x6F,
        ["Decimal"] = 0x6E,
    };

    /// <summary>Called by StaticWndProc when WM_HOTKEY arrives.</summary>
    internal static void DispatchHotKey(int id)
    {
        if (Callbacks.TryGetValue(id, out var cb) && cb is Action action)
            action();
    }

    /// <summary>Register a hotkey by string key name.</summary>
    internal static int Register(string key, uint modifiers, Action action)
    {
        return !KeyNameToVk.TryGetValue(key, out var vk)
            ? 0 : Register(vk, modifiers, action);
    }

    /// <summary>Register a hotkey by virtual-key code.</summary>
    internal static int Register(uint vk, uint modifiers, Action action)
    {
        var id = Interlocked.Increment(ref _nextId);
        var hWnd = _explicitHandle != IntPtr.Zero ? _explicitHandle : Control.GetFirstWindowHandle();
        if (hWnd == IntPtr.Zero) return 0;
        var ok = RegisterHotKey(hWnd, id, modifiers, vk);
        if (!ok) return 0;
        Callbacks[id] = action;

        return id;
    }

    /// <summary>Unregister a hotkey by id.</summary>
    internal static void Unregister(int id)
    {
        if (id <= 0) return;
        var hWnd = Control.GetFirstWindowHandle();
        if (hWnd != IntPtr.Zero)
            UnregisterHotKey(hWnd, id);

        Callbacks.TryRemove(id, out _);
    }

    /// <summary>Unregister all hotkeys.</summary>
    internal static void UnregisterAll()
    {
        var hWnd = Control.GetFirstWindowHandle();
        foreach (var id in Callbacks.Keys)
        {
            if (hWnd != IntPtr.Zero)
                UnregisterHotKey(hWnd, id);
        }
        Callbacks.Clear();
    }

    public static void Dispose()
    {
        UnregisterAll();
    }

    internal static void SetHandle(IntPtr hWnd)
    {
        _explicitHandle = hWnd;
    }


    private static IntPtr _explicitHandle;
}
