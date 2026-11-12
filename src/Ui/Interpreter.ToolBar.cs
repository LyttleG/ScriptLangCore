namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class ToolBar : Control
    {
        private static readonly string WindowClass = "ToolbarWindow32";

        public ToolBar(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, WindowClass, "",
                // CCS_NORESIZE | CCS_NOPARENTALIGN, NOT CCS_TOP: a CCS_TOP toolbar docks across its parent and
                // ignores the rect it was created with, so a placed control was 1066x28 at the parent's top instead
                // of the 304x96 it was given, and a click at its model centre fell below the real window and reached
                // the form. The engine's Dock layout positions a docked bar explicitly, so it does not need the flag.
                // NO TBSTYLE_FLAT: it makes a toolbar TRANSPARENT, so its background is whatever its parent
                // paints. On a light form that looks like a toolbar; here the form is dark, so a placed toolbar
                // came out BLACK. Measured with GetPixel: the toolbar's centre read 0x202020, the form's own
                // background exactly, while a Button read 0xFE-odd, a TabControl and a Label 0xF0F0F0 and a
                // StatusBar white. A toolbar that paints itself reads as one of those instead.
                WS_CHILD | WS_VISIBLE | CCS_NORESIZE | CCS_NOPARENTALIGN | TBSTYLE_TOOLTIPS,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for ToolBar failed: {Marshal.GetLastWin32Error()}");

            // A toolbar creates an INTERNAL CHILD of its own window class, and a click lands on that: measured,
            // WindowFromPoint at a placed toolbar's centre returned neither the toolbar nor the form, so the press
            // was lost and the designer could not drag it. WS_EX_TRANSPARENT on that child makes the click fall
            // through to the toolbar, which is the managed control carrying the handlers. Nothing is lost: the
            // palette's toolbar has no buttons of its own to press.
            var inner = GetWindow(handle, GW_CHILD);
            if (inner != IntPtr.Zero)
                SetWindowLongPtrW(inner, GWL_EXSTYLE, ToNInt(GetWindowLongPtrW(inner, GWL_EXSTYLE) | WS_EX_TRANSPARENT));

            SendMessageW(handle, TB_BUTTONSTRUCTSIZE, (nuint)Marshal.SizeOf<TBBUTTON>(), IntPtr.Zero);
            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);

            // Register in the static map, and subclass the native window so script mouse events fire: without this
            // a StatusBar (like a TabControl or a ToolBar) receives no MouseDown at all, so a designer cannot drag
            // it. Measured with a placed control of each kind and a posted click, the same way ComboBox, TreeView
            // and DateTimePicker were found. Same pair ListBox and ListView already call.
            RegisterHandle();
            SubclassNativeChild();
        }

        public void AddButton(int id, string text)
        {
            TBBUTTON btn = new()
            {
                iBitmap = -1,
                idCommand = id,
                fsState = (byte)TBSTATE_ENABLED,
                fsStyle = (byte)BTNS_BUTTON,
                iString = Marshal.StringToHGlobalUni(text)
            };
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(btn));
            Marshal.StructureToPtr(btn, ptr, false);
            SendMessageW(handle, TB_INSERTBUTTONW, unchecked((nuint)(-1)), ptr);
            Marshal.FreeHGlobal(ptr);
            Marshal.FreeHGlobal(btn.iString);
        }
    }
}

