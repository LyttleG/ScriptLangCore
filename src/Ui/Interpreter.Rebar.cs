namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class Rebar : Control
    {
        public Rebar(int x, int y, int width, int height)
        { InitBounds(x, y, width, height); }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, "ReBarWindow32", "",
                WS_CHILD | WS_VISIBLE | CCS_NODIVIDER,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for Rebar failed: {Marshal.GetLastWin32Error()}");
            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);
        }
    }

    // Factory: public SysLink CreateSysLink(string text, int x, int y, int w, int h)
}

