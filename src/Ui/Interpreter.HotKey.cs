namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class HotKey : Control
    {
        public HotKey(int x, int y, int width, int height)
        { InitBounds(x, y, width, height); }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, "msctls_hotkey32", "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for HotKey failed: {Marshal.GetLastWin32Error()}");
            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);
        }

        public (int VirtualKey, int Modifiers) GetHotKey()
        {
            var val = (uint)SendMessageW(handle, HKM_GETHOTKEY, 0, IntPtr.Zero);
            return ((int)(val & 0xFF), (int)(val >> 8));
        }
    }
}

