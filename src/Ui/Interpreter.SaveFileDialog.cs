namespace ScriptLangCore;

using DispatchVtbl; // OleEmbed, OleHostRegistry, OleEmbedRehost, ...

internal partial class Interpreter
{
    public class SaveFileDialog
    {
        public string Title { get; set; } = "Save As";
        public string Filter { get; set; } = "All Files (*.*)|*.*";
        public string? InitialDirectory { get; set; }
        public string? DefaultExtension { get; set; }

        public string? FileName { get; private set; }

        public int ShowDialog()
        {
            var filterPtr = OpenFileDialog.ConvertFilter(Filter);
            var fileBuffer = Marshal.AllocHGlobal(65536);
            // Zero filename buffer so no garbage appears as default filename
            for (var i = 0; i < 260; i++) Marshal.WriteInt16(fileBuffer, i * 2, 0);
            var titlePtr = Marshal.StringToHGlobalUni(Title);
            var initDirPtr = InitialDirectory != null ? Marshal.StringToHGlobalUni(InitialDirectory) : IntPtr.Zero;
            var defExtPtr = DefaultExtension != null ? Marshal.StringToHGlobalUni(DefaultExtension) : IntPtr.Zero;
            var ofn = new OPENFILENAME();
            try
            {
                ofn.lStructSize = Marshal.SizeOf<OPENFILENAME>();
                ofn.lpstrFilter = filterPtr;
                ofn.lpstrFile = fileBuffer;
                ofn.nMaxFile = 65535;
                ofn.lpstrTitle = titlePtr;
                ofn.Flags = OFN_HIDEREADONLY;
                if (initDirPtr != IntPtr.Zero)
                    ofn.lpstrInitialDir = initDirPtr;
                if (defExtPtr != IntPtr.Zero)
                    ofn.lpstrDefExt = defExtPtr;

                var result = GetSaveFileNameW(ref ofn);

                if (result && ofn.lpstrFile != IntPtr.Zero)
                {
                    FileName = Marshal.PtrToStringUni(ofn.lpstrFile) ?? "";
                    return 1;
                }
                return 2;
            }
            finally
            {
                Marshal.FreeHGlobal(filterPtr);
                Marshal.FreeHGlobal(fileBuffer);
                Marshal.FreeHGlobal(titlePtr);
                if (initDirPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(initDirPtr);
                if (defExtPtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(defExtPtr);
            }
        }
    }
}

