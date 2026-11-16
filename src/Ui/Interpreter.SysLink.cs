namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class SysLink : Control
    {
        public SysLink(string text, int x, int y, int width, int height)
        {
            Text = text; InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, "SysLink", Text,
                WS_CHILD | WS_VISIBLE | WS_TABSTOP,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for SysLink failed: {Marshal.GetLastWin32Error()}");
            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);
        }
    }
}

