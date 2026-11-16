namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public sealed class DateTimePicker : Control
    {
        private const string WindowClass = "SysDateTimePick32";

        public DateTimePicker(int x, int y, int width, int height)
        {
            InitBounds(x, y, width, height);
        }

        protected override void CreateHandle()
        {
            var lpParam = GCHandle.ToIntPtr(GcHandle);
            handle = CreateWindowExW(0, WindowClass, "",
                WS_CHILD | WS_VISIBLE | WS_TABSTOP | DTS_SHORTDATEFORMAT,
                Left, Top, Width, Height,
                ParentHandle, IntPtr.Zero, GetModuleHandleW(null!),
                lpParam);
            if (handle == IntPtr.Zero)
                throw new ScriptException($"CreateWindowEx for DateTimePicker failed: {Marshal.GetLastWin32Error()}");

            // Without these a DateTimePicker receives no script mouse events at all (measured), so it cannot be
            // dragged in the designer. Same pair ListBox and ListView already do.
            RegisterHandle();
            SubclassNativeChild();

            var hFont = GetStockObject(DEFAULT_GUI_FONT);
            SendMessageW(handle, WM_SETFONT, (nuint)hFont, 0);
        }

        public DateTime Value
        {
            get
            {
                // DTM_GETSYSTEMTIME fills the BUFFER, so the value has to be read back from it before the buffer is
                // freed. This used to build the DateTime from the local, which was still the zeroed `default`, and
                // new DateTime(0, 0, ...) throws ArgumentOutOfRangeException on the year. Measured: reading Value on
                // a fresh DateTimePicker threw "Argument '' out of range: actual", on JIT and on the AOT publish.
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<SYSTEMTIME>());
                try
                {
                    SendMessageW(handle, DTM_GETSYSTEMTIME, 0, ptr);
                    var st = Marshal.PtrToStructure<SYSTEMTIME>(ptr);
                    // an empty picker answers GDT_ERROR and leaves the struct zeroed: report that as MinValue
                    // instead of letting the DateTime constructor throw at the script
                    if (st.wYear < 1601) return DateTime.MinValue;
                    return new DateTime(st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds, DateTimeKind.Unspecified);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
            set
            {
                SYSTEMTIME st = new()
                {
                    wYear = (ushort)value.Year,
                    wMonth = (ushort)value.Month,
                    wDay = (ushort)value.Day,
                    wHour = (ushort)value.Hour,
                    wMinute = (ushort)value.Minute,
                    wSecond = (ushort)value.Second,
                    wMilliseconds = (ushort)value.Millisecond
                };
                var ptr = Marshal.AllocHGlobal(Marshal.SizeOf(st));
                try
                {
                    Marshal.StructureToPtr(st, ptr, false);
                    SendMessageW(handle, DTM_SETSYSTEMTIME, 0, ptr);
                }
                finally { Marshal.FreeHGlobal(ptr); }
            }
        }
    }
}

