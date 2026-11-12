namespace ScriptLangCore;

internal static class AotInvoker
{
    /// <summary>
    /// Invoke a CDECL function pointer up to 16 parameters.
    /// SQLite, libcurl, and most C libraries use CDECL.
    /// </summary>
    private static unsafe T? InvokeCdecl<T>(nint fPtr, ReadOnlySpan<long> args) where T : unmanaged
    {
        if (fPtr == nint.Zero)
            throw new ArgumentException("Invalid function pointer.");
        if (args.Length > 16)
            throw new NotSupportedException("InvokeCdecl only supports up to 16 arguments.");
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Cdecl]<T>)fPtr)(),
            1 => ((delegate* unmanaged[Cdecl]<long, T>)fPtr)(args[0]),
            2 => ((delegate* unmanaged[Cdecl]<long, long, T>)fPtr)(args[0], args[1]),
            3 => ((delegate* unmanaged[Cdecl]<long, long, long, T>)fPtr)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged[Cdecl]<long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            13 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
            14 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]),
            15 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14]),
            16 => ((delegate* unmanaged[Cdecl]<long, long, long, long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]),
            _ => throw new NotSupportedException("InvokeCdecl only supports up to 16 arguments.")
        };
    }

    /// <summary>
    /// Invoke an STDCALL function pointer up to 16 parameters.
    /// Win32 API (user32, kernel32) uses STDCALL on x86.
    /// On x64, identical to CDECL, provided for API completeness.
    /// </summary>
    private static unsafe T? InvokeStdcall<T>(nint fPtr, ReadOnlySpan<long> args) where T : unmanaged
    {
        if (fPtr == nint.Zero)
            throw new ArgumentException("Invalid function pointer.");
        if (args.Length > 16)
            throw new NotSupportedException("InvokeStdcall only supports up to 16 arguments.");
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Stdcall]<T>)fPtr)(),
            1 => ((delegate* unmanaged[Stdcall]<long, T>)fPtr)(args[0]),
            2 => ((delegate* unmanaged[Stdcall]<long, long, T>)fPtr)(args[0], args[1]),
            3 => ((delegate* unmanaged[Stdcall]<long, long, long, T>)fPtr)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged[Stdcall]<long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            13 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
            14 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]),
            15 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14]),
            16 => ((delegate* unmanaged[Stdcall]<long, long, long, long, long, long, long, long, long, long, long, long, long, long, long, long, T>)fPtr)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]),
            _ => throw new NotSupportedException("InvokeStdcall only supports up to 16 arguments.")
        };
    }

    // ── CDECL wrappers ──
    public static object? InvokeCdeclPtr(nint fPtr, ReadOnlySpan<long> args) => InvokeCdecl<long>(fPtr, args);
    public static object? InvokeCdeclDouble(nint fPtr, ReadOnlySpan<long> args) => InvokeCdecl<double>(fPtr, args);

    // ── Backward-compatible aliases (default to CDECL) ──
    public static object? InvokePtr(nint fPtr, ReadOnlySpan<long> args) => InvokeCdecl<long>(fPtr, args);
}
