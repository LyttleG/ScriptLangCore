namespace ScriptLangCore;

/// <summary>The process's handles BY OBJECT TYPE, the in-process counterpart of cdb's !handle, so a handle
/// measurement does not need a debugger. READ-ONLY on purpose: nothing here opens, closes or writes a handle, no
/// pointer comes from a script, and every failure degrades to "unavailable" instead of throwing into one. The buffer
/// grows in bounded steps; a type name is resolved from ONE handle of that type (the technique handle.exe and
/// Process Explorer use), so the call is repeated only per TYPE, never per handle.</summary>
internal static unsafe partial class HandleTypes
{
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypeInformation = 2;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint GrantedAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(IntPtr handle, int cls, IntPtr buf, int len, out int ret);

    /// <summary>Type name -> count for THIS process, ordered by count. Empty means "unavailable", never a throw.</summary>
    internal static List<(string Type, int Count)> Tally()
    {
        var result = new List<(string Type, int Count)>();
        var size = 1 << 20;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, size, out _);
                if (status == StatusInfoLengthMismatch) { size *= 2; continue; }
                if (status < 0) return result;

                var total = (long)Marshal.ReadIntPtr(buf);
                var stride = Marshal.SizeOf<HandleEntry>();
                var baseAddr = buf + 2 * IntPtr.Size;
                var pid = Environment.ProcessId;

                var perIndex = new Dictionary<uint, int>();
                var sample = new Dictionary<uint, IntPtr>();
                for (long i = 0; i < total; i++)
                {
                    var e = (HandleEntry*)(baseAddr + i * stride);
                    if ((long)e->UniqueProcessId != pid) continue;
                    perIndex[e->ObjectTypeIndex] = perIndex.GetValueOrDefault(e->ObjectTypeIndex) + 1;
                    sample.TryAdd(e->ObjectTypeIndex, e->HandleValue);
                }
                // Aggregate BY NAME, not by index: the kernel has several indices that resolve to the same name
                // (six distinct `File` indices, measured), and a tally that lists a name six times is not a tally.
                var byName = new Dictionary<string, int>();
                foreach (var kv in perIndex)
                {
                    var typeName = TypeName(sample[kv.Key], kv.Key);
                    byName[typeName] = byName.GetValueOrDefault(typeName) + kv.Value;
                }
                foreach (var kv in byName)
                    result.Add((kv.Key, kv.Value));
                result.Sort((a, b) => b.Count.CompareTo(a.Count));
                return result;
            }
            catch { return result; }
            finally { Marshal.FreeHGlobal(buf); }
        }
        return result;
    }

    private static string TypeName(IntPtr handle, uint index)
    {
        var buf = Marshal.AllocHGlobal(1024);
        try
        {
            // A single query per TYPE. If it refuses (some handles answer nothing useful), fall back to the index, so
            // the tally is still comparable call to call.
            if (NtQueryObject(handle, ObjectTypeInformation, buf, 1024, out _) < 0) return "type#" + index;
            var info = (UnicodeString*)buf;
            if (info->Buffer == IntPtr.Zero || info->Length == 0) return "type#" + index;
            return Marshal.PtrToStringUni(info->Buffer, info->Length / 2) ?? ("type#" + index);
        }
        catch { return "type#" + index; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
