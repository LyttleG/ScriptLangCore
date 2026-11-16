namespace ScriptLangCore;

internal partial class Interpreter
{
    // P/Invoke declarations
    // P/Invoke declarations
    [LibraryImport("winmm.dll", EntryPoint = "PLACEHOLDER", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int mciSendString(string command, char[]? returnBuffer, int bufferSize, IntPtr callback);

    [LibraryImport("winmm.dll", EntryPoint = "PLACEHOLDER", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool mciGetErrorString(int errorCode, char[] errorBuffer, int bufferSize);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutSetVolume(IntPtr hwo, uint dwVolume);

    [LibraryImport("winmm.dll")]
    private static partial int waveOutGetVolume(IntPtr hwo, out uint dwVolume);

    private void RegisterMciFunctions()
    {
        var mciNs = new Namespace(_comparer);

        // Send command without return value
        mciNs.AddMethod("SendCommand", args =>
        {
            var command = args[0] as string ?? throw new ScriptException("Command required");
            var error = mciSendString(command, null, 0, IntPtr.Zero);
            if (error != 0)
            {
                var errorBuffer = new char[256];
                mciGetErrorString(error, errorBuffer, errorBuffer.Length);
                var errorMsg = new string(errorBuffer).TrimEnd('\0');
                throw new ScriptException($"MCI Error {error}: {errorMsg}");
            }
            return null;
        });

        // Get status with return value
        mciNs.AddMethod("GetStatus", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            var statusType = args.Length > 1 ? args[1] as string : "mode";
            var command = $"status {alias} {statusType}";

            var buffer = new char[256];
            var error = mciSendString(command, buffer, buffer.Length, IntPtr.Zero);

            if (error != 0)
            {
                // Return empty string on error instead of throwing
                return "";
            }

            return new string(buffer).TrimEnd('\0');
        });

        // Get length
        mciNs.AddMethod("GetLength", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            var buffer = new char[32];
            var error = mciSendString($"status {alias} length", buffer, buffer.Length, IntPtr.Zero);

            if (error != 0)
                return "0";

            return new string(buffer).TrimEnd('\0');
        });

        // Get position
        mciNs.AddMethod("GetPosition", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            var buffer = new char[32];
            var error = mciSendString($"status {alias} position", buffer, buffer.Length, IntPtr.Zero);

            if (error != 0)
                return "0";

            return new string(buffer).TrimEnd('\0');
        });

        mciNs.AddMethod("SetVolume", args =>
        {
            var volume = Convert.ToInt32(args[0]); // 0-1000

            // Clamp
            volume = Math.Max(0, Math.Min(1000, volume));

            // Convert to Windows volume (logarithmic scale feels more natural)
            var normalized = volume / 1000.0;
            // Use logarithmic scaling for more natural volume curve
            var logVolume = normalized * normalized; // Quadratic curve
            var windowsVolume = (uint)(logVolume * 0xFFFF);

            // Stereo: same volume for left and right
            var stereoVolume = (windowsVolume << 16) | windowsVolume;

            var result = waveOutSetVolume(IntPtr.Zero, stereoVolume);
            Console.WriteLine(result == 0
                ? $"[WaveOut] Volume set to {volume} -> 0x{stereoVolume:X}"
                : $"[WaveOut] Failed to set volume: error {result}");

            return null;
        });

        mciNs.AddMethod("GetVolume", _ =>
        {
            var result = waveOutGetVolume(IntPtr.Zero, out var stereoVolume);
            if (result != 0)
                return "500";

            // Extract left channel
            var leftVolume = stereoVolume & 0xFFFF;

            // Reverse the logarithmic scaling
            var normalized = Math.Sqrt(leftVolume / 65535.0);
            var volume = (int)(normalized * 1000);

            return volume.ToString();
        });

        // Open file
        mciNs.AddMethod("Open", args =>
                {
                    var filePath = args[0] as string ?? throw new ScriptException("File path required");
                    var alias = args.Length > 1 ? args[1] as string : "mp3";

                    mciSendString($"close {alias}", null, 0, IntPtr.Zero);

                    var command = $"open \"{filePath}\" type mpegvideo alias {alias}";
                    var error = mciSendString(command, null, 0, IntPtr.Zero);

                    if (error != 0)
                    {
                        var errorBuffer = new char[256];
                        mciGetErrorString(error, errorBuffer, errorBuffer.Length);
                        var errorMsg = new string(errorBuffer).TrimEnd('\0');
                        throw new ScriptException($"MCI Error {error}: {errorMsg}");
                    }

                    return null;
                });

        // Play
        mciNs.AddMethod("Play", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            mciSendString($"play {alias}", null, 0, IntPtr.Zero);
            return null;
        });

        // Pause
        mciNs.AddMethod("Pause", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            mciSendString($"pause {alias}", null, 0, IntPtr.Zero);
            return null;
        });

        // Resume
        mciNs.AddMethod("Resume", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            mciSendString($"resume {alias}", null, 0, IntPtr.Zero);
            return null;
        });

        // Stop
        mciNs.AddMethod("Stop", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            mciSendString($"stop {alias}", null, 0, IntPtr.Zero);
            return null;
        });

        // Close
        mciNs.AddMethod("Close", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            mciSendString($"close {alias}", null, 0, IntPtr.Zero);
            return null;
        });

        // Seek to position
        mciNs.AddMethod("Seek", args =>
        {
            var alias = args.Length > 0 ? args[0] as string : "mp3";
            var position = Convert.ToInt32(args[1]);
            mciSendString($"seek {alias} to {position}", null, 0, IntPtr.Zero);
            return null;
        });

        CurrentFrame.Scopes.Define("MCI", mciNs);
    }
}
