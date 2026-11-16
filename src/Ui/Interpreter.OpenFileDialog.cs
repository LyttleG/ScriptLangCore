namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public class OpenFileDialog
    {
        public string Title { get; set; } = "Open File";
        public string Filter { get; set; } = "All Files (*.*)|*.*";
        public bool Multiselect { get; set; }
        public string? InitialDirectory { get; set; }

        public string[] FileNames { get; private set; } = [];

        public int ShowDialog()
        {
            return ShowDialogCore(isOpen: true);
        }

        private int ShowDialogCore(bool isOpen)
        {
            var filterPtr = ConvertFilter(Filter);
            var fileBuffer = Marshal.AllocHGlobal(65536);
            // Zero filename buffer so no garbage appears as default filename
            for (var i = 0; i < 260; i++) Marshal.WriteInt16(fileBuffer, i * 2, 0);
            var titlePtr = Marshal.StringToHGlobalUni(Title);
            var initDirPtr = InitialDirectory != null ? Marshal.StringToHGlobalUni(InitialDirectory) : IntPtr.Zero;
            var ofn = new OPENFILENAME();
            try
            {
                ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
                ofn.lpstrFilter = filterPtr;
                ofn.lpstrFile = fileBuffer;
                ofn.nMaxFile = 65535;
                ofn.lpstrTitle = titlePtr;
                ofn.Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY;
                if (initDirPtr != IntPtr.Zero)
                    ofn.lpstrInitialDir = initDirPtr;

                var result = isOpen ? GetOpenFileNameW(ref ofn) : GetSaveFileNameW(ref ofn);

                if (result && ofn.lpstrFile != IntPtr.Zero)
                {
                    FileNames = [Marshal.PtrToStringUni(ofn.lpstrFile) ?? ""];
                    return 1; // OK
                }
                return 2; // Cancel
            }
            finally
            {
                Marshal.FreeHGlobal(filterPtr);
                Marshal.FreeHGlobal(fileBuffer);
                Marshal.FreeHGlobal(titlePtr);
                if (initDirPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(initDirPtr);
            }
        }

        internal static IntPtr ConvertFilter(string filter)
        {
            // Convert "Script files|*.slc|All files|*.*" → "Script files\0*.slc\0All files\0*.*\0\0"
            var parts = filter.Split('|');
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                sb.Append(p);
                sb.Append('\0');
            }
            if (parts.Length > 0 && (parts.Length % 2) == 0)
                sb.Append('\0'); // double null terminate
            return Marshal.StringToHGlobalUni(sb.ToString());
        }
    }

    // ==========================================================================
    // SaveFileDialog
    // ==========================================================================
}

