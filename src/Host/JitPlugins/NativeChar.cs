using System.Runtime.InteropServices;
using System.Text;

namespace ScriptLangCore;

/// <summary>THE `char_t` BOUNDARY, in one place, because getting it wrong reads as "the file does not exist".
///
/// `hostfxr.h` and `nethost.h` declare every string with `char_t`, which is `wchar_t` on Windows and `char`
/// everywhere else. Passing UTF-8 on Windows therefore arrives as interleaved letters and NULs: measured, a
/// `runtimeconfig.json` path handed over as UTF-8 was answered with
/// `The specified runtimeconfig.json [...] does not exist` (0x80008093), and `get_hostfxr_path` decoded as UTF-8
/// answered `"C"` because the second byte is a NUL.
///
/// THE ENGINE'S OWN C ABI IS THE OPPOSITE (`EmbedApi`: "STRINGS ARE UTF-8"), and so is the plugin DOOR's payload:
/// a request and an answer cross the door as UTF-8 bytes on every platform. So this file holds the split rather
/// than leaving it to each call site:
///   - <see cref="Alloc"/>/<see cref="Text"/>: the `char_t` side, for hostfxr and nethost arguments and buffers;
///   - <see cref="EncodeRequest"/>/<see cref="DecodeAnswer"/>: the UTF-8 side, for the door's payload.
/// Mixing the two is the failure above, which is why neither is spelled outside this file.</summary>
internal static unsafe class NativeChar
{
    /// <summary>True where `char_t` is UTF-16, which is Windows (and nowhere this repository ships to).</summary>
    internal static bool Wide => OperatingSystem.IsWindows();

    /// <summary>Allocates a `char_t` copy of <paramref name="text"/> with its terminator, for one hostfxr call.
    /// The caller frees it with <see cref="Free"/>.</summary>
    internal static byte* Alloc(string text)
        => (byte*)(Wide ? Marshal.StringToCoTaskMemUni(text) : Marshal.StringToCoTaskMemUTF8(text));

    /// <summary>Frees what <see cref="Alloc"/> returned. `Marshal.FreeCoTaskMem` is the same door for both encodings,
    /// because the allocator is the same one.</summary>
    internal static void Free(byte* text) => Marshal.FreeCoTaskMem((nint)text);

    /// <summary>Reads a NUL-terminated `char_t` buffer (what `get_hostfxr_path` fills) as text.</summary>
    internal static string Text(nint buffer)
        => (Wide ? Marshal.PtrToStringUni(buffer) : Marshal.PtrToStringUTF8(buffer)) ?? "";

    /// <summary>A door REQUEST as UTF-8 bytes with a terminating NUL: the input half of the contract, on every
    /// platform. The plugin may only read it during the call, which is why the caller pins it.</summary>
    internal static byte[] EncodeRequest(string requestJson)
    {
        var payload = Encoding.UTF8.GetBytes(requestJson);
        var bytes = new byte[payload.Length + 1];
        payload.CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>A door ANSWER as text, read from EXACTLY <paramref name="count"/> bytes: the plugin returns a length
    /// and the buffer is not required to be NUL-terminated (the engine's own ABI writes a terminator, a third-party
    /// plugin need not). Reading it as a C string is the mistake this signature prevents.</summary>
    internal static string DecodeAnswer(byte* answer, int count)
        => count <= 0 ? "" : Encoding.UTF8.GetString(answer, count);
}
