namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>Registers the Mem namespace, safe, modern descendants of the classic BASIC PEEK/POKE.
    /// Operates only on buffers the script allocated itself (WinApi.Alloc / Marshal-backed pointers);
    /// there is intentionally NO absolute-address byte access (antiquated + dangerous on modern
    /// virtual-memory OSes). Named byte/word accessors round out the size-dispatched WinApi readers,
    /// plus Mem.Fill (bulk memset, the POKE-loop replacement) and Mem.Dump (hex inspection).</summary>
    private void RegisterMemFunctions()
    {
        var memNs = new Namespace(_comparer);

        // ── Named byte accessors (the true PEEK/POKE width) ─────────────────
        memNs.AddMethod("ReadByte", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
            return Marshal.ReadByte(ptr, offset);
        });

        memNs.AddMethod("WriteByte", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 2 ? Convert.ToInt32(args[1]) : 0;
            var value = args.Length > 2 ? Convert.ToInt32(args[2]) : Convert.ToInt32(args[1]);
            Marshal.WriteByte(ptr, offset, (byte)value);
            return null;
        });

        // ── Named 16-bit accessors ──────────────────────────────────────────
        memNs.AddMethod("ReadInt16", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
            return Marshal.ReadInt16(ptr, offset);
        });

        memNs.AddMethod("WriteInt16", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 2 ? Convert.ToInt32(args[1]) : 0;
            var value = args.Length > 2 ? Convert.ToInt32(args[2]) : Convert.ToInt32(args[1]);
            Marshal.WriteInt16(ptr, offset, (short)value);
            return null;
        });

        // ── Named 32/64-bit wrappers (offset optional, matching the byte forms) ──
        memNs.AddMethod("ReadInt32", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
            return Marshal.ReadInt32(ptr, offset);
        });

        memNs.AddMethod("WriteInt32", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 2 ? Convert.ToInt32(args[1]) : 0;
            var value = args.Length > 2 ? Convert.ToInt32(args[2]) : Convert.ToInt32(args[1]);
            Marshal.WriteInt32(ptr, offset, value);
            return null;
        });

        memNs.AddMethod("ReadInt64", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
            return Marshal.ReadInt64(ptr, offset);
        });

        memNs.AddMethod("WriteInt64", static args =>
        {
            var ptr = ToNInt(args[0]);
            var offset = args.Length > 2 ? Convert.ToInt32(args[1]) : 0;
            var value = args.Length > 2 ? Convert.ToInt64(args[2]) : Convert.ToInt64(args[1]);
            Marshal.WriteInt64(ptr, offset, value);
            return null;
        });

        // ── Bulk fill, the modern POKE-loop replacement ────────────────────
        memNs.AddMethod("Fill", static args =>
        {
            var ptr = ToNInt(args[0]);
            var length = Convert.ToInt32(args[1]);
            var value = args.Length > 2 ? Convert.ToInt32(args[2]) : 0;
            if (length < 0) throw new ScriptException("Mem.Fill: length must be >= 0");
            for (var i = 0; i < length; i++)
                Marshal.WriteByte(ptr, i, (byte)value);
            return null;
        });

        // ── Hex dump, the practical PEEK descendant (inspect a buffer you own) ──
        memNs.AddMethod("Dump", args =>
        {
            var ptr = ToNInt(args[0]);
            var length = Convert.ToInt32(args[1]);
            var bytesPerRow = args.Length > 2 ? Convert.ToInt32(args[2]) : 16;
            if (length < 0) throw new ScriptException("Mem.Dump: length must be >= 0");
            if (bytesPerRow <= 0) bytesPerRow = 16;

            var sb = new StringBuilder();
            for (var row = 0; row < length; row += bytesPerRow)
            {
                sb.Append(ptr.ToInt64().ToString("X16")).Append("  ");
                var count = Math.Min(bytesPerRow, length - row);
                for (var i = 0; i < count; i++) sb.Append(Marshal.ReadByte(ptr, row + i).ToString("X2")).Append(' ');
                for (var i = count; i < bytesPerRow; i++) sb.Append("   ");
                sb.Append(" | ");
                for (var i = 0; i < count; i++)
                {
                    var c = Marshal.ReadByte(ptr, row + i);
                    sb.Append(c is >= 32 and < 127 ? (char)c : '.');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        });

        // ── Alloc/ReadString/Free, self-contained Mem (delegates to the same Marshal helpers as WinApi) ──
        memNs.AddMethod("Alloc", static args =>
        {
            var size = Convert.ToInt32(args[0]);
            if (size <= 0) throw new ScriptException("Size must be positive");
            return Marshal.AllocHGlobal(size).ToInt64();
        });

        memNs.AddMethod("ReadString", args =>
        {
            var ptr = ToNInt(args[0]);
            if (ptr == nint.Zero) return "";
            return Marshal.PtrToStringUni(ptr) ?? "";
        });

        memNs.AddMethod("Free", static args =>
        {
            var ptr = ToNInt(args[0]);
            if (ptr != nint.Zero) Marshal.FreeHGlobal(ptr);
            return null;
        });

        // ── Mem.Buffer, disposable wrapper for using-block auto-free ─────────
        memNs.AddMethod("Buffer", static args =>
        {
            var size = Convert.ToInt32(args[0]);
            if (size <= 0) throw new ScriptException("Size must be positive");
            return new MemBuffer(Marshal.AllocHGlobal(size));
        });

        // Mem.LiveBuffers(), number of MemBuffer allocations not yet disposed. 0 proves every
        // using/using-var buffer was released. Unmanaged memory is invisible to GC.GetTotalMemory,
        // so this counter is the direct leak signal for the buffer wrapper.
        memNs.AddMethod("LiveBuffers", _ => MemBuffer.LiveCount);


        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("ToPointer", args =>
                {
                    var str = args[0] as string ?? throw new ScriptException("String required");
                    return Marshal.StringToHGlobalUni(str).ToInt64();
                });

        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("FreePointer", static args =>
                {
                    var ptr = ToNInt(args[0]);
                    if (ptr != nint.Zero) Marshal.FreeHGlobal(ptr);
                    return null;
                });

        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("ReadPointer", static args =>
                {
                    var ptr = ToNInt(args[0]);
                    var offset = Convert.ToInt32(args[1]);
                    return Marshal.ReadIntPtr(ptr, offset).ToInt64();
                });

        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("WritePointer", static args =>
                {
                    var ptr = ToNInt(args[0]);
                    var offset = Convert.ToInt32(args[1]);
                    var value = ToNInt(args[2]);
                    Marshal.WriteIntPtr(ptr, offset, value);
                    return null;
                });

        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("ReadStruct", args =>
                {
                    var ptr = ToNInt(args[0]);
                    var structTypeName = args[1] as string ?? throw new ScriptException("Type name required");
                    // AOT-safe: resolve each struct type via generic PtrToStructure<T> instead of
                    // Marshal.PtrToStructure(ptr, Type) which relies on reflection and is trimmed.
                    return structTypeName switch
                    {
                        "WNDCLASSEX" or "WNDCLASSEXW" => Marshal.PtrToStructure<Wndclassexw>(ptr),
                        "MSG" => Marshal.PtrToStructure<Msg>(ptr),
                        "RECT" => Marshal.PtrToStructure<Rect>(ptr),
                        "POINT" => Marshal.PtrToStructure<Point>(ptr),
                        "TCITEMW" => Marshal.PtrToStructure<TCITEMW>(ptr),
                        "LVCOLUMNW" => Marshal.PtrToStructure<LVCOLUMNW>(ptr),
                        "LVITEMW" => Marshal.PtrToStructure<LVITEMW>(ptr),
                        "TVINSERTSTRUCTW" => Marshal.PtrToStructure<TVINSERTSTRUCTW>(ptr),
                        "TVITEMW" => Marshal.PtrToStructure<TVITEMW>(ptr),
                        "TVITEMEXW" => Marshal.PtrToStructure<TVITEMEXW>(ptr),
                        "PAINTSTRUCT" => Marshal.PtrToStructure<PAINTSTRUCT>(ptr),
                        _ when (args is [_, _, string fieldName, ..] && Current?.Structs.TryGetValue(structTypeName, out var rsDecl) == true)
                            => StructSizeComputer.ReadField(ptr, rsDecl, fieldName, Current.Structs),
                        _ when Current?.Structs.TryGetValue(structTypeName, out var rsDecl2) == true
                            => ReadStructFromDecl(ptr, rsDecl2),
                        _ => throw new ScriptException($"Unknown struct type '{structTypeName}'")
                    };
                });

        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("WriteStruct", args =>
                {
                    var ptr = ToNInt(args[0]);
                    var structObj = args[1] ?? throw new ScriptException("Struct object cannot be null");
                    if (args is [_, string wsName, string wsField, ..])
                    {
                        if (Current?.Structs.TryGetValue(wsName, out var wsDecl) == true)
                        {
                            StructSizeComputer.WriteField(ptr, wsDecl, wsField, args.Length >= 4 ? args[3] : null, Current!.Structs);
                            return null;
                        }
                        throw new ScriptException($"Unknown struct type '{wsName}'");
                    }

                    Marshal.StructureToPtr(structObj, ptr, false);
                    return null;
                });

        // Moved from the WinApi namespace (2026-10-01): a pointer or a structs block IS memory work.
        memNs.AddMethod("SizeOfStruct", args =>
                {
                    var typeName = args[0] as string ?? throw new ScriptException("Struct type name required");
                    // AOT-safe: generic SizeOf<T> instead of Marshal.SizeOf(Type)
                    return typeName switch
                    {
                        "WNDCLASSEX" or "WNDCLASSEXW" => Marshal.SizeOf<Wndclassexw>(),
                        "MSG" => Marshal.SizeOf<Msg>(),
                        "RECT" => Marshal.SizeOf<Rect>(),
                        "POINT" => Marshal.SizeOf<Point>(),
                        "TCITEMW" => Marshal.SizeOf<TCITEMW>(),
                        "LVCOLUMNW" => Marshal.SizeOf<LVCOLUMNW>(),
                        "LVITEMW" => Marshal.SizeOf<LVITEMW>(),
                        "TVINSERTSTRUCTW" => Marshal.SizeOf<TVINSERTSTRUCTW>(),
                        "TVITEMW" => Marshal.SizeOf<TVITEMW>(),
                        "TVITEMEXW" => Marshal.SizeOf<TVITEMEXW>(),
                        "PAINTSTRUCT" => Marshal.SizeOf<PAINTSTRUCT>(),
                        _ => Current?.Structs.TryGetValue(typeName, out var structDecl) == true
                            ? StructSizeComputer.Compute(structDecl, Current.Structs)
                            : throw new ScriptException($"Unknown struct type '{typeName}'")
                    };
                });

        CurrentFrame.Scopes.Define("Mem", memNs);
    }

    /// <summary>Auto-freeing unmanaged buffer for `using (var b = Mem.Buffer(n))`, Dispose() frees
    /// the underlying allocation (the modern, safe descendant of BASIC's POKE-loop + free).</summary>
    internal sealed class MemBuffer : IDisposable
    {
        private static long _liveCount;
        /// <summary>Number of live (allocated, not yet disposed) MemBuffer instances.</summary>
        public static long LiveCount => Interlocked.Read(ref _liveCount);

        public long Ptr { get; private set; }

        public MemBuffer(IntPtr ptr)
        {
            Ptr = ptr.ToInt64();
            Interlocked.Increment(ref _liveCount);
        }

        public void Dispose()
        {
            if (Ptr != 0)
            {
                Marshal.FreeHGlobal((nint)Ptr);
                Ptr = 0;
                Interlocked.Decrement(ref _liveCount);
            }
        }

        /// <summary>Alias of <see cref="Dispose"/>, frees the buffer, zeroes <see cref="Ptr"/> and
        /// decrements the live count. Safe to call multiple times. Prefer this over the raw
        /// <c>Mem.Free(ptr)</c> for wrapped buffers, so the wrapper and counter stay consistent.</summary>
        public void Free() => Dispose();

        /// <summary>Script-facing name, print(wrapped) shows "MemBuffer" (nested CLR name would
        /// otherwise leak "ScriptLangCore.Interpreter+MemBuffer").</summary>
        public override string ToString() => ScriptNameOf(typeof(MemBuffer)) ?? "MemBuffer";
    }
}