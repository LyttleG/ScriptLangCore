namespace ScriptLangCore;

internal partial class Interpreter
{
    internal sealed class DynamicLibrary(string name, IntPtr handle) : IDisposable
    {
        private readonly Dictionary<string, IntPtr> _functions = new();
        private readonly Dictionary<int, IntPtr> _ordinals = new();
        private bool _freed;

        /// <summary>The module handle, and it is MUTABLE because <see cref="Free"/> ZEROES it: a freed library used to
        /// keep the dead pointer and only flip <see cref="IsFreed"/>, so a script reading <c>lib.Handle</c> after
        /// <c>FreeLibrary</c> got a number it could still hand to a native call (measured: the JSON read back
        /// `{"Handle":140718913093632,...,"IsFreed":true}`). Zero is the honest answer, the same one a Win32 caller sees
        /// when its own variable is cleared, and every consumer already treats it as "no module".</summary>
        private nint _handle = handle;

        /// <summary>The members of this class that are NOT DLL exports. The call dispatch treats every
        /// <c>lib.X(...)</c> as an export lookup, that is the rule that makes <c>lib.Beep(...)</c> work
        ///, so a real member added here must be listed, or a script calling it looks up an export by
        /// that name and gets "native library is missing an expected entry point". Both dispatch sites
        /// consult this one set rather than keeping a copy of the list each.</summary>
        internal static readonly HashSet<string> BuiltInMembers = new(StringComparer.Ordinal)
        {
            "Free", "Dispose", "ToString", "GetFunctionPointer", "Function",
            "Invoke", "InvokeRaw", "InvokeStr", "InvokeDouble",
            "Name", "Handle", "Functions", "Ordinals", "IsFreed",
        };

        public string Name { get; } = name;
        public IntPtr Handle => _handle;

        /// <summary>The export NAMES this handle has resolved so far, as a snapshot array, the
        /// cache grows as the script calls more members, so read it again rather than hold it.
        /// This is what has been resolved, not the DLL's export table (listing every export would
        /// mean walking the PE export directory, a different feature).</summary>
        public string[] Functions => _freed ? [] : [.. _functions.Keys];

        /// <summary>The ORDINALS this handle has resolved so far, same snapshot semantics and the
        /// same caveat as <see cref="Functions"/>.</summary>
        public int[] Ordinals => _freed ? [] : [.. _ordinals.Keys];

        /// <summary>True once <see cref="Free"/> has released the module. A freed handle resolves
        /// nothing: asking it for an export would otherwise hand back a pointer into memory the
        /// loader has taken away, and <c>NativeLibrary.GetExport</c> on a freed handle is undefined
        /// behaviour besides. Refusing loudly is the only safe answer.</summary>
        public bool IsFreed => _freed;

        public nint GetFunctionPointer(string name)
        {
            RequireNotFreed();
            if (!_functions.TryGetValue(name, out var ptr))
            {
                ptr = NativeLibrary.GetExport(_handle, name);
                _functions[name] = ptr;
            }
            return ptr;
        }

        /// <summary>Resolve a function by ordinal number (faster than by name).</summary>
        public nint GetFunctionPointer(int ordinal)
        {
            RequireNotFreed();
            if (!_ordinals.TryGetValue(ordinal, out var ptr))
            {
                // MAKEINTRESOURCE: ordinal in low 16 bits, high word zero
                ptr = GetProcAddress(_handle, ordinal & 0xFFFF);
                _ordinals[ordinal] = ptr;
            }
            return ptr;
        }

        public void Free()
        {
            if (_freed) return;                 // idempotent: a second free must not double-release
            _freed = true;
            _functions.Clear();                 // the pointers in here died with the module
            _ordinals.Clear();
            // THE HANDLE IS CLEARED AS WELL, and AFTER the release: a script that reads `lib.Handle` once the library is
            // gone used to get the dead pointer back (the JSON read `{"Handle":140718913093632,...,"IsFreed":true}`),
            // which is a number it could hand straight to a native call. Win32's own callers see their variable
            // zeroed by FreeLibrary in the C API's own idiom, and zero is what every consumer of this handle already
            // treats as "no module".
            var releasing = _handle;
            _handle = nint.Zero;
            if (releasing != nint.Zero)
                NativeLibrary.Free(releasing);
        }

        private void RequireNotFreed()
        {
            if (_freed)
                throw new ScriptException($"Library '{Name}' has been freed, its exports cannot be resolved any more.");
        }

        public void Dispose() => Free();

        public override string ToString() => ScriptNameOf(typeof(DynamicLibrary)) ?? "DynamicLibrary";

        /// <summary>Resolves an export to an <em>invocable</em> handle, by name (<c>string</c>) or by
        /// ordinal (<c>int</c>). For a name written literally <c>lib.someName(...)</c> is already
        /// shorter; this exists for names that are not known until runtime, e.g. walking
        /// <see cref="Functions"/>.</summary>
        public NativeFunction Function(object? nameOrOrdinal)
        {
            switch (nameOrOrdinal)
            {
                case string s:
                    {
                        try { return new NativeFunction(this, s, -1, GetFunctionPointer(s)); }
                        catch (ScriptException) { throw; }
                        catch (Exception) { throw new ScriptException($"Export '{s}' not found in library '{Name}'."); }
                    }
                case int o:
                    return new NativeFunction(this, "", o, GetFunctionPointer(o));
                case long l when l is >= int.MinValue and <= int.MaxValue:
                    {
                        var o = (int)l;
                        return new NativeFunction(this, "", o, GetFunctionPointer(o));
                    }
                default:
                    throw new ScriptException("Function expects an export name (string) or an ordinal (int).");
            }
        }

        /// <summary>Resolves an export AND calls it, the shorthand for
        /// <c>lib.Function(name).Invoke(args…)</c>, sharing that handle's marshaling and its guarded
        /// guess. The explicit forms mirror the handle's: <see cref="InvokeRaw"/> (machine value),
        /// <see cref="InvokeStr"/> (read the result as a C string) and <see cref="InvokeDouble"/>.</summary>
        public object? Invoke(object? nameOrOrdinal, params object?[] args)
            => Function(nameOrOrdinal).Invoke(args);

        public nint InvokeRaw(object? nameOrOrdinal, params object?[] args)
            => Function(nameOrOrdinal).InvokeRaw(args);

        public string InvokeStr(object? nameOrOrdinal, params object?[] args)
            => Function(nameOrOrdinal).InvokeStr(args);

        public double InvokeDouble(object? nameOrOrdinal, params object?[] args)
            => Function(nameOrOrdinal).InvokeDouble(args);
    }

    /// <summary>A resolved native function, what <see cref="DynamicLibrary.Function"/> hands back.
    ///
    /// A pointer carries no signature, so <see cref="Invoke"/> has to GUESS the result shape. The
    /// guess is deliberately narrow and, above all, safe: a return that points into committed,
    /// readable memory (checked with VirtualQuery) is read as a NUL-terminated C string, anything
    /// else comes back as an integer. A small integer is therefore never dereferenced, and a wrong
    /// guess cannot kill the process, an access violation is not a catchable script error, and the
    /// shipped binary must not be killable by script input.
    ///
    /// Knowing the shape beats guessing, so the explicit forms are here too: <see cref="InvokeRaw"/>
    /// (the untouched machine value), <see cref="InvokeStr"/> (read the result as a C string, it
    /// throws a clear error rather than reading unreadable memory) and <see cref="InvokeDouble"/>
    /// (a double return is bit-identical to an int64 one, so it cannot be guessed at all).
    ///
    /// Strings passed IN are marshaled as UTF-8 and freed after the call, the CallNative
    /// convention, so a dynamic invoke and a CallNative call send the same bytes.</summary>
    internal sealed class NativeFunction(DynamicLibrary owner, string name, int ordinal, nint pointer)
    {
        public string Name { get; } = name;

        /// <summary>The ordinal it was resolved by, or -1 when it was resolved by name.</summary>
        public int Ordinal { get; } = ordinal;

        public nint Pointer { get; } = pointer;

        public override string ToString()
            => Name.Length > 0
                ? NativeSignature.Of(Name, null, null, TokenType.Void, arityUnknown: true)
                : NativeSignature.Of($"#{Ordinal}", null, null, TokenType.Void, arityUnknown: true);

        /// <summary>Calls it and returns either the C string the result points at, or the raw value
        /// as an integer. See the class note for the exact rule.</summary>
        public object? Invoke(params object?[] args)
        {
            var raw = InvokeRaw(args);
            if (raw == nint.Zero) return 0;
            return NativeMemoryProbe.TryReadCString(raw, out var text) ? text : (object)raw.ToInt64();
        }

        /// <summary>The machine value, untouched, the honest form when you know the signature.</summary>
        /// <summary>The machine value, untouched, the honest form when you know the signature.</summary>
        public nint InvokeRaw(params object?[] args)
        {
            RequireAddress();
            return (nint)NativeCall.Invoke(Pointer, args);
        }

        private void RequireAddress()
        {
            if (owner.IsFreed)
                throw new ScriptException(
                    $"Library '{owner.Name}' has been freed, its resolved functions cannot be called any more.");
            if (Pointer == nint.Zero)
                throw new ScriptException(Name.Length > 0
                    ? $"Function '{Name}' did not resolve to an address."
                    : $"Ordinal {Ordinal} did not resolve to an address.");
        }

        /// <summary>Reads the result as a NUL-terminated C string; throws when it does not point at
        /// readable memory instead of faulting.</summary>
        public string InvokeStr(params object?[] args)
        {
            var raw = InvokeRaw(args);
            if (raw == nint.Zero) return "";
            return NativeMemoryProbe.TryReadCString(raw, out var text)
                ? text
                : throw new ScriptException($"InvokeStr: the result 0x{raw.ToInt64():X} does not point at readable memory.");
        }

        /// <summary>Calls it expecting a double return.</summary>
        public double InvokeDouble(params object?[] args)
        {
            RequireAddress();
            return NativeCall.InvokeDouble(Pointer, args);
        }
    }


    /// <summary>The ONE marshal-and-call step behind every native call a script can make:
    /// <c>lib.someExport(args…)</c>, <c>lib._42(args…)</c>, <c>lib.Invoke(name, args…)</c> and the
    /// <see cref="NativeFunction"/> handle all land here. They used to be three copies, one of which
    /// converted every argument with Convert.ToInt64 and so had no string support at all, which made
    /// a call behave differently depending on which syntax reached it.
    ///
    /// Strings go as UTF-8, NUL-terminated, freed after the call: ONE convention, matching the
    /// CallNative family. Call the ANSI entry points (<c>…A</c>); a <c>…W</c> entry point would read
    /// those bytes as UTF-16.</summary>
    internal static class NativeCall
    {
        internal static long Invoke(nint pointer, object?[] args)
        {
            Span<long> nativeArgs = stackalloc long[args.Length];
            var owned = Allocate(args, nativeArgs);
            try { return Convert.ToInt64(AotInvoker.InvokeCdeclPtr(pointer, nativeArgs) ?? 0L); }
            finally { Free(owned); }
        }

        internal static double InvokeDouble(nint pointer, object?[] args)
        {
            Span<long> nativeArgs = stackalloc long[args.Length];
            var owned = Allocate(args, nativeArgs);
            try { return Convert.ToDouble(AotInvoker.InvokeCdeclDouble(pointer, nativeArgs) ?? 0.0); }
            finally { Free(owned); }
        }

        /// <summary>Fills the machine-word argument slots; strings are UTF-8 blocks whose pointers are
        /// returned so the caller can free them after the call.</summary>
        private static List<nint> Allocate(object?[] args, Span<long> nativeArgs)
        {
            var owned = new List<nint>(args.Length);
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case null:
                        nativeArgs[i] = 0;
                        break;
                    case string str:
                        {
                            var utf8 = Marshal.StringToCoTaskMemUTF8(str);
                            owned.Add(utf8);
                            nativeArgs[i] = utf8.ToInt64();
                            break;
                        }
                    case IntPtr ptr:
                        nativeArgs[i] = ptr.ToInt64();
                        break;
                    case UIntPtr uptr:
                        nativeArgs[i] = (long)uptr.ToUInt64();
                        break;
                    default:
                        nativeArgs[i] = Convert.ToInt64(args[i]);
                        break;
                }
            }
            return owned;
        }

        private static void Free(List<nint> owned)
        {
            foreach (var p in owned)
                if (p != nint.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    /// <summary>Bounded, crash-safe reads of native memory. Everything here refuses clearly instead of
    /// faulting: a page is read only after VirtualQuery says it is committed and readable, and a string
    /// scan stops at the first unreadable page or at a cap.</summary>
    internal static class NativeMemoryProbe
    {
        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_NOACCESS = 0x01, PAGE_GUARD = 0x100;
        private const uint PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04, PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40;
        private const int MaxStringBytes = 4096;
        private const int PageSize = 4096;

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryBasicInformation
        {
            public nint BaseAddress;
            public nint AllocationBase;
            public uint AllocationProtect;
            public nint RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint VirtualQuery(nint lpAddress, out MemoryBasicInformation lpBuffer, nint dwLength);

        /// <summary>True when <paramref name="address"/> is inside committed, readable memory.</summary>
        internal static bool IsReadable(nint address)
        {
            if (address == nint.Zero) return false;
            if (VirtualQuery(address, out var mbi, (nint)Marshal.SizeOf<MemoryBasicInformation>()) == nint.Zero) return false;
            if (mbi.State != MEM_COMMIT) return false;
            if ((mbi.Protect & PAGE_NOACCESS) != 0 || (mbi.Protect & PAGE_GUARD) != 0) return false;
            return (mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE)) != 0;
        }

        /// <summary>Reads a NUL-terminated UTF-8 string, verifying readability page by page and
        /// stopping at the cap, so an unterminated or partly-mapped buffer cannot run away.</summary>
        internal static bool TryReadCString(nint address, out string value)
        {
            value = "";
            if (!IsReadable(address)) return false;

            var buffer = new byte[MaxStringBytes];
            var length = 0;
            while (length < MaxStringBytes)
            {
                if (length % PageSize == 0 && !IsReadable(address + length)) break;
                byte b;
                try { b = Marshal.ReadByte(address, length); }
                catch { break; }
                if (b == 0) break;
                buffer[length] = b;
                length++;
            }

            if (length == 0) return false;
            value = Encoding.UTF8.GetString(buffer, 0, length);
            return true;
        }
    }
}
