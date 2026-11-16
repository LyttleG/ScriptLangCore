namespace ScriptLangCore;

//#pragma warning disable IL3050 // Marshal.StructureToPtr/PtrToStructure with Type parameter, WinAPI interop by design
internal partial class Interpreter
{
    // THE IMPORTS THE MENU REGISTRATIONS NEED. An import is visible to the class that uses it, so these cannot ride inside
    // the registering method, and a LibraryImport needs the type to be partial, which this class already is. The three menu
    // calls Ui.cs declares belong to the Menu class there and are not reachable here, so each is declared again.
    [LibraryImport("user32.dll", EntryPoint = "InsertMenuW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InsertMenuW(nint hMenu, uint uPosition, uint uFlags, nint uIDNewItem, string lpNewItem);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint hMenu);
    [LibraryImport("user32.dll")] private static partial nint GetMenu(nint hWnd);
    [LibraryImport("user32.dll")]
    private static partial nint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    /// <summary>A module whose BASE NAME is already in this process, in the shape our callers expect.
    /// LoadLibrary(path) must not put a SECOND instance of a native into the process: this engine also
    /// reaches sqlite3 through a [DllImport("sqlite3")] bridge (SqliteCursor.cs), which the runtime binds
    /// to the copy beside the executable. A path load then produced two modules, and calling across them
    /// (separate CRTs/heaps) killed the process with an access violation on NativeAOT, confirmed with cdb:
    /// two ModLoad entries for sqlite3, then c0000005. Windows resolves a bare name to the module already
    /// loaded under that name, so reusing it is both the safe and the intended behaviour: the P/Invoke
    /// bridge can only ever mean one of them.
    /// NOTE: this searches by name only; a native that is NOT already loaded still loads from the path.</summary>
    /// <summary>Natives this engine ALSO binds with [DllImport] (SqliteCursor.cs declares the bridge for
    /// "sqlite3"). For these, a script-supplied path must never put a SECOND module in the process: the
    /// runtime binds the copy beside the executable by name, and a call sequence crossing the two copies
    /// (separate CRTs/heaps) killed the process with an access violation on NativeAOT, confirmed with cdb
    /// (two ModLoad entries for sqlite3, then c0000005). A path load of such a name therefore resolves to
    /// the beside-the-executable file when one exists, which is the same file the bridge binds.
    /// Measured: with the copy beside the exe, suites and name loads pass and a foreign-path load crashed;
    /// with NO copy beside the exe, the same foreign-path load works (one module is the whole difference).
    /// Natives the engine does not bind itself are unaffected, the caller's path is honoured.</summary>
    private static readonly string[] NameBoundNatives = { "sqlite3" };

    /// <summary>True for a native the engine binds BY NAME (see NameBoundNatives): it ships beside the executable and
    /// is process-lifetime, so nothing may unload it while a pointer resolved from it can still be called, doing that
    /// is what produced an execute-into-unmapped-memory fault in &lt;Unloaded_sqlite3.DLL&gt;.</summary>
    internal static bool IsNameBound(string name)
    {
        for (var i = 0; i < NameBoundNatives.Length; i++)
            if (string.Equals(NameBoundNatives[i], name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Redirect a path load of a name-bound native to the executable's own copy, if present.</summary>
    private static string PreferBesideExe(string path, string name)
    {
        for (var i = 0; i < NameBoundNatives.Length; i++)
        {
            if (!string.Equals(NameBoundNatives[i], name, StringComparison.OrdinalIgnoreCase)) continue;
            // The candidate is the BASE NAME plus the native's extension, NOT the caller's spelling. The
            // difference is the whole bug: a spec written without one (`.../sqlite3`, which is exactly what
            // scheduler_sqlite_demo used) computed `<exeDir>\sqlite3`, found nothing beside the executable, and
            // fell through to the caller's FOREIGN path, so the process ended up with two sqlite3 modules and the
            // cross-instance call died in sqlite3_finalize (0xC0000005, measured 2026-10-06 on both legs).
            // "Beside the executable" is the SESSION's home when a host embedded the engine (there the engine's own
            // folder is where it deployed its natives), and the process's base directory otherwise, which is what
            // AppContext.BaseDirectory has always meant here.
            var besideExe = Path.Combine(ScriptSession.ResolveHome(), name + ".dll");
            return File.Exists(besideExe) ? besideExe : path;
        }
        return path;
    }

    /// <summary>
    /// The ONE implementation behind the script-visible LoadLibrary global. It lived twice, byte for byte, while
    /// the namespace carried a copy of its own, which is this repository's recurring bug: the copy an input
    /// reaches is the one that matters, and a stale twin stays invisible until it bites. The namespace copy is
    /// gone (2026-09-30). Behaviour is exactly what both copies did: dedup by module name, reuse an already-loaded
    /// module, otherwise load the beside-the-exe file for a name the engine also binds via [DllImport].
    /// </summary>
    // NOTE: the return type and the bookkeeping are DynamicLibrary, NOT nint, a first draft of this plan
    // guessed wrong, and the original code is the source of truth. Wording, ErrorCode and the 0,0 line/col
    // arguments are carried over verbatim from the former bodies.
    private DynamicLibrary LoadNativeLibrary(object?[] args)
    {
        var path = args[0] as string ?? throw new ScriptException("Path required");
        var name = Path.GetFileNameWithoutExtension(path);
        // Deduplicate, return the existing handle if the library is still loaded. A library the
        // script has FREED is not "already loaded": its module is gone, so serving it would hand back
        // a handle whose exports can no longer be resolved. `lib.Free()` (the method) never evicted
        // this cache, only FreeLibrary did, so a freed k32 came back from a later LoadLibrary, and
        // every call on it went through an unloaded module.
        if (_loadedLibraries.TryGetValue(name, out var existing) && !existing.IsFreed)
            return existing;
        if (AlreadyLoadedModule(name) is { } present)
            return present;
        nint handle;
        try
        {
            handle = NativeLibrary.Load(PreferBesideExe(path, name));
        }
        catch (DllNotFoundException ex)
        {
            // Report the PATH, which the CLR's message never contains ("DllNotFound_Windows, sqlite3,
            // <localized>"), and point at the deployment rule: natives sit beside the executable.
            throw new ScriptException(ErrorCode.IoError,
                $"no native library at '{path}' (searched: '{PreferBesideExe(path, name)}'). "
                + $"Native libraries must sit beside the executable or be given as a full path "
                + $"[{ex.GetType().Name}].", 0, 0);
        }
        catch (BadImageFormatException ex)
        {
            throw new ScriptException(ErrorCode.IoError,
                $"'{PreferBesideExe(path, name)}' cannot be loaded: wrong architecture or "
                + $"corrupt (an x64 native is required). [{ex.GetType().Name}]", 0, 0);
        }
        var lib = new DynamicLibrary(name, handle);
        _loadedLibraries[name] = lib;
        return lib;
    }

    private DynamicLibrary? AlreadyLoadedModule(string name)
    {
        // GetModuleHandleW, NOT NativeLibrary.TryLoad: TryLoad would LOAD the name if it is absent, and a
        // name lookup can match something else on the search path. This must only ever REUSE a module that
        // is already in the process (0 == not loaded), so a caller still gets its own file when nothing by
        // that name exists yet.
        var handle = GetModuleHandleW(name);
        if (handle == IntPtr.Zero) return null;
        var lib = new DynamicLibrary(name, handle);
        _loadedLibraries[name] = lib;   // cached so later loads of this name short-circuit
        return lib;
    }

    internal static object?[] SpreadFirstArrayArg(object?[] args)
    {
        if (args is [Array arr])
        {
            var result = new object?[arr.Length];
            for (var i = 0; i < arr.Length; i++)
                result[i] = arr.GetValue(i);
            return result;
        }
        return args;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(nint hWnd, int x, int y, int nWidth, int nHeight, [MarshalAs(UnmanagedType.Bool)] bool bRepaint);
    [StructLayout(LayoutKind.Sequential)]
    private struct Pt { public int x, y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Pt lpPoint);

    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static partial nint WindowFromPointImpl(Pt point);

    /// <summary>Which window a click at THIS SCREEN POINT would reach, or 0 when none. The natural question for a
    /// designer's hit test (a 8x8 resize handle sitting on top of a control, and which of the two a click
    /// actually reaches is a Z-ORDER fact that no rect comparison can answer).</summary>
    internal nint WindowFromPointAt(long x, long y)
    {
        var pt = new Pt { x = (int)x, y = (int)y };
        return WindowFromPointImpl(pt);
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetActiveWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("gdi32.dll")]
    private static partial uint GetPixel(IntPtr hdc, int x, int y);

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetFocus();

    private void RegisterNativeFunctions()
    {
        // Inside RegisterFunctions()
        var colorNs = new Namespace(_comparer);

        colorNs.AddProperty("Red", 0x00FF0000u);
        colorNs.AddProperty("Green", 0x0000FF00u);
        colorNs.AddProperty("Blue", 0x000000FFu);
        colorNs.AddProperty("White", 0x00FFFFFFu);
        colorNs.AddProperty("Black", 0x00000000u);
        colorNs.AddProperty("Yellow", 0x00FFFF00u);
        colorNs.AddProperty("Cyan", 0x0000FFFFu);
        colorNs.AddProperty("Magenta", 0x00FF00FFu);
        colorNs.AddProperty("Transparent", 0x00000000u);
        colorNs.AddProperty("AliceBlue", 0x00F8F8FFu);
        colorNs.AddProperty("AntiqueWhite", 0x00FAEBD7u);
        colorNs.AddProperty("Aqua", 0x0000FFFFu);
        colorNs.AddProperty("Aquamarine", 0x007FFFD4u);
        colorNs.AddProperty("Azure", 0x00F0FFFFu);
        colorNs.AddProperty("Beige", 0x00DCF5F5u);
        colorNs.AddProperty("Bisque", 0x00FFE4C4u);
        colorNs.AddProperty("BlanchedAlmond", 0x00FFEBCDu);
        colorNs.AddProperty("BlueViolet", 0x008A2BE2u);
        colorNs.AddProperty("Brown", 0x00A52A2Au);
        colorNs.AddProperty("BurlyWood", 0x0087B8DEu);
        colorNs.AddProperty("CadetBlue", 0x005F9EA0u);
        colorNs.AddProperty("Chartreuse", 0x007FFF00u);
        colorNs.AddMethod("FromArgb", args =>
        {
            switch (args.Length)
            {
                case 4:
                    {
                        var a = Convert.ToByte(args[0]);
                        var r = Convert.ToByte(args[1]);
                        var g = Convert.ToByte(args[2]);
                        var b = Convert.ToByte(args[3]);
                        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
                    }
                case 3:
                    {
                        var r = Convert.ToByte(args[0]);
                        var g = Convert.ToByte(args[1]);
                        var b = Convert.ToByte(args[2]);
                        return ((uint)r << 16) | ((uint)g << 8) | b;
                    }
                default:
                    throw new ScriptException("FromArgb expects either 3 or 4 arguments: [a,] r, g, b");
            }
        });
        // ── the two byte orders a colour can arrive in ──────────────────────────────────────────────────
        // A colour a script writes is 0xRRGGBB, red in the HIGH byte, and every colour member takes and reports it
        // that way (FromArgb and RGB compose it). GetPixel is the exception: it answers a pixel as 0x00BBGGRR, the
        // order a COLORREF carries. Measured: a red background reads back as 0xFF0000 from the property and as 255
        // from the pixel. BGR and FromPixel are the SAME swap, which is why swapping twice returns the original:
        // one is the colour you are looking for, the other the pixel you read.
        static long ColorNumber(object? value, string fn, string what)
            => value switch
            {
                int i => i,
                long l0 => l0,
                uint u => u,
                short s => s,
                byte b => b,
                _ => throw new ScriptException($"{fn}: {what} must be a whole number."),
            };
        static int ColorPart(object?[] args, int index, string fn, string part)
        {
            if (index >= args.Length) throw new ScriptException($"{fn} expects three arguments (red, green, blue).");
            var v = ColorNumber(args[index], fn, "the " + part + " component");
            if (v is < 0 or > 255) throw new ScriptException($"{fn}: the {part} component must be 0..255, got {v}.");
            return (int)v;
        }
        static uint SwapColorBytes(object? value)
        {
            var v = (uint)ColorNumber(value, "Colors.BGR / Colors.FromPixel", "the value") & 0xFFFFFF;
            return ((v & 0xFF) << 16) | (v & 0xFF00) | ((v >> 16) & 0xFF);
        }
        colorNs.AddMethod("RGB", args => (object?)(uint)((ColorPart(args, 0, "Colors.RGB", "red") << 16)
                                                      | (ColorPart(args, 1, "Colors.RGB", "green") << 8)
                                                      | ColorPart(args, 2, "Colors.RGB", "blue")));
        colorNs.AddMethod("BGR", args => (object?)SwapColorBytes(args.Length > 0 ? args[0]
            : throw new ScriptException("Colors.BGR expects a colour, as Colors.BGR(0xRRGGBB).")));
        colorNs.AddMethod("FromPixel", args => (object?)SwapColorBytes(args.Length > 0 ? args[0]
            : throw new ScriptException("Colors.FromPixel expects a pixel reading, as Colors.FromPixel(0x00BBGGRR).")));
        CurrentFrame.Scopes.Define("Colors", colorNs);

        // The Color constants are the GLOBAL Colors namespace (defined just above), which is the only
        // spelling: it used to be reachable twice, as Colors.Red and as WinApi.Color.Red.


        // Convenience alias on the WinApi namespace, delegating to the global RegisterNative registration.
        // LoadLibrary and FreeLibrary deliberately have NO namespace form: the bare globals ARE their spelling
        // (2026-09-30). Each was a byte-for-byte twin of its global, which is this repository's recurring bug, and
        // a member that only repeats a global is one nobody needs.
        RegisterNative("GetProcAddress", TokenType.Object, args =>
        {
            var hModule = args[0] is DynamicLibrary lib ? lib.Handle : ToNInt(args[0]);
            // Ordinal lookup, MAKEINTRESOURCE: low 16 bits, high word zero
            var ordinal = args.Length > 1 ? Convert.ToInt32(args[1]) : 0;
            var ptr = GetProcAddress(hModule, ordinal & 0xFFFF);
            return ptr.ToInt64();
        });

        RegisterNative("GetFunction", TokenType.Object, static args =>
        {
            // Accept DynamicLibrary or raw nint/long
            var handle = args[0] switch
            {
                DynamicLibrary lib => lib.Handle,
                _ => ToNInt(args[0])
            };
            var name = args[1] as string ?? throw new ScriptException("Function name required");
            return NativeLibrary.GetExport(handle, name).ToInt64();
        });

        RegisterNative("CallFunc", TokenType.Object, static args =>
        {
            // args: [library, functionName, arg0..argN]
            const int maxArgs = 16;

            if (args.Length < 2)
                throw new ScriptException("CallFunc requires library and function name.");
            if (args.Length > 2 + maxArgs)
                throw new ScriptException("CallFunc supports at most 16 arguments.");

            var handle = ResolveHandle(args[0]);
            var name = args[1] as string ?? throw new ScriptException("CallFunc: function name required (arg[1])");
            var ptr = ResolveExport(handle, name);

            var rawArgs = ExtractArgs(args);
            if (rawArgs.Length > maxArgs)
                throw new ScriptException("CallFunc supports at most 16 arguments.");

            // The same shared marshal-and-call every other native call uses: strings as UTF-8 (freed
            // after the call), pointers and numerics passed through. This site used to marshal
            // strings WIDE, so CallFunc and CallNative disagreed about the bytes they sent to the
            // very same kind of pointer.
            return NativeCall.Invoke(ptr, rawArgs);

            static nint ResolveHandle(object? arg0)
            {
                nint handle;
                try
                {
                    handle = arg0 switch
                    {
                        DynamicLibrary lib => lib.Handle,
                        _ => ToNInt(arg0)
                    };
                }
                catch (Exception ex)
                {
                    throw new ScriptException("CallFunc: invalid library handle (" + ex.GetType().Name
                        + "). Pass a DynamicLibrary from LoadLibrary or a numeric module handle. Got: "
                        + (arg0?.GetType().Name ?? "null"));
                }

                if (handle == nint.Zero)
                    throw new ScriptException("CallFunc: invalid library handle (zero)."
                        + " Pass a DynamicLibrary from LoadLibrary or a numeric module handle. Got: "
                        + (arg0?.GetType().Name ?? "null"));

                return handle;
            }

            static nint ResolveExport(nint handle, string name)
            {
                nint ptr;
                try
                {
                    ptr = NativeLibrary.GetExport(handle, name);
                }
                catch (EntryPointNotFoundException)
                {
                    throw new ScriptException("CallFunc: function '" + name + "' not found in the specified library.");
                }

                if (ptr == nint.Zero)
                    throw new ScriptException("CallFunc: function '" + name + "' not found in the specified library.");

                return ptr;
            }

            static object?[] ExtractArgs(object?[] args)
            {
                if (args.Length <= 2)
                    return [];

                switch (args[2]) // 3rd argument is the first "real" argument to the native function
                {
                    case object[] objArr:
                        return objArr;

                    case Array sysArr:
                        {
                            var result = new object?[sysArr.Length];
                            for (var i = 0; i < sysArr.Length; i++)
                                result[i] = sysArr.GetValue(i);
                            return result;
                        }

                    case IScriptList sl:
                        {
                            var result = new object?[sl.Count];
                            for (var i = 0; i < sl.Count; i++)
                                result[i] = sl[i];
                            return result;
                        }

                    case IList list:
                        {
                            var result = new object?[list.Count];
                            for (var i = 0; i < list.Count; i++)
                                result[i] = list[i];
                            return result;
                        }

                    default:
                        {
                            // Individual args, skip trailing null padding
                            var lastIdx = args.Length - 1;
                            while (lastIdx >= 2 && args[lastIdx] == null)
                                lastIdx--;
                            var result = new object?[lastIdx - 1];
                            Array.Copy(args, 2, result, 0, result.Length);
                            return result;
                        }
                }
            }

        });

        RegisterNative("GetFunctionPointer", TokenType.Object, args =>
        {
            var fn = args[0] as ScriptFunction ?? throw new ScriptException("Expected a function");
            var line = Convert.ToInt32(args[1]);
            var col = Convert.ToInt32(args[2]);
            return GetFunctionPointer(fn, line, col).ToInt64();
        });

        RegisterNative("FreeFunctionPointer", TokenType.Void, args =>
        {
            var fn = args[0] as ScriptFunction ?? throw new ScriptException("Expected a function");
            FreeFunctionPointer(fn);
            return null;
        });

        RegisterNative("GetModuleHandle", TokenType.Object, args =>
        {
            var name = args.Length > 0 ? args[0] as string : "";
            return GetModuleHandleW(name!).ToInt64();
        });

        RegisterNative("GetLastError", TokenType.Object, _ => (long)Marshal.GetLastWin32Error());

        // Expose RECT as a script-accessible type
        CurrentFrame.Scopes.Define("Rect", typeof(Rect));

        // ── GlobalHotKey namespace ──
        var hotkeyNs = new Namespace(_comparer);
        hotkeyNs.AddProperty("MOD_NONE", 0x0000);
        hotkeyNs.AddProperty("MOD_ALT", 0x0001);
        hotkeyNs.AddProperty("MOD_CTRL", 0x0002);
        hotkeyNs.AddProperty("MOD_SHIFT", 0x0004);
        hotkeyNs.AddProperty("MOD_WIN", 0x0008);
        hotkeyNs.AddProperty("MOD_NOREPEAT", 0x4000);

        hotkeyNs.AddMethod("Register", args =>
        {
            if (args.Length < 3) return 0;
            // Wrap script callback into an Action, ScriptFunction needs the interpreter to invoke
            Action? action = null;
            if (args[2] is Action a) action = a;
            else if (args[2] is ScriptFunction sf)
            {
                var itp = Current!;
                action = () => itp.InvokeScriptFunction(sf, [], 0, 0);
            }
            if (action == null) return 0;

            var result = args[0] is string keyStr
                ? GlobalHotKey.Register(keyStr, Convert.ToUInt32(args[1]), action)
                : GlobalHotKey.Register(Convert.ToUInt32(args[0]), Convert.ToUInt32(args[1]), action);
            return result;
        });

        hotkeyNs.AddMethod("Unregister", args =>
        {
            if (args.Length > 0) GlobalHotKey.Unregister(Convert.ToInt32(args[0]));
            return null;
        });

        hotkeyNs.AddMethod("UnregisterAll", _ => { GlobalHotKey.UnregisterAll(); return null; });

        hotkeyNs.AddMethod("Dispose", _ => { GlobalHotKey.Dispose(); return null; });

        hotkeyNs.AddMethod("SetHandle", args =>
        {
            if (args.Length <= 0) return null;
            var arg = args[0];
            var hWnd = arg switch
            {
                IntPtr ptr => ptr,
                long l => new IntPtr(l),
                int i => new IntPtr(i),
                _ => IntPtr.Zero
            };
            GlobalHotKey.SetHandle(hWnd);
            return null;
        });

        CurrentFrame.Scopes.Define("GlobalHotKey", hotkeyNs);

        RegisterNative("Rect", TokenType.Object, args =>
        {
            if (args.Length != 4)
                throw new ScriptException("Rect expects 4 arguments: left, top, right, bottom.");
            return new Rect
            {
                Left = Convert.ToInt32(args[0]),
                Top = Convert.ToInt32(args[1]),
                Right = Convert.ToInt32(args[2]),
                Bottom = Convert.ToInt32(args[3])
            };
        });

        // The two library entry points are GLOBALS, not namespace members: there is no WinApi.LoadLibrary or
        // WinApi.FreeLibrary (2026-09-30). Each was once registered in both places, byte for byte.
        RegisterNative("LoadLibrary", TokenType.Object, args => LoadNativeLibrary(args));

        RegisterNative("FreeLibrary", TokenType.Void, args =>
        {
            if (args[0] is DynamicLibrary lib)
            {
                lib.Free();
                _loadedLibraries.Remove(lib.Name);
            }
            else
            {
                var handle = ToNInt(args[0]);
                if (handle != nint.Zero) NativeLibrary.Free(handle);
            }
            return null;
        });
    }

    private nint GetFunctionPointer(ScriptFunction fn, int line, int column)
    {
        if (_callbackHandles.TryGetValue(fn, out var existingHandle))
            return Marshal.GetFunctionPointerForDelegate((CallbackDelegate)existingHandle.Target!);

        CallbackDelegate del = (a1, a2, a3, a4) =>
        {
            try
            {
                var args = new object?[] { a1, a2, a3, a4 };
                var result = InvokeScriptFunction(fn, args, line, column);

                return result switch
                {
                    null => nint.Zero,
                    nint r => r,
                    int i => i,
                    long l => (nint)l,
                    _ => new nint(Convert.ToInt64(result))
                };
            }
            catch (Exception ex)
            {
                ConsoleSafe.SetColor(ConsoleColor.Red);
                Console.WriteLine($"[Native Callback] Unhandled exception: {ex.Message}");
                ConsoleSafe.ResetColor();
                return nint.Zero;
            }
        };

        var handle = GCHandle.Alloc(del);
        _callbackHandles[fn] = handle;
        return Marshal.GetFunctionPointerForDelegate(del);
    }

    private void FreeFunctionPointer(ScriptFunction fn)
    {
        if (!_callbackHandles.TryGetValue(fn, out var handle)) return;
        handle.Free();
        _callbackHandles.Remove(fn);
    }

    /// <summary>Computes the unmanaged byte size of a script-defined struct from its field types.</summary>
    internal static class StructSizeComputer
    {
        internal static object ReadFromPtr(IntPtr ptr, int offset, int size) => size switch
        {
            1 => Marshal.ReadByte(ptr, offset),
            2 => Marshal.ReadInt16(ptr, offset),
            4 => Marshal.ReadInt32(ptr, offset),
            _ => Marshal.ReadInt64(ptr, offset)
        };

        private static void WriteToPtr(IntPtr ptr, int offset, int size, long val)
        {
            switch (size)
            {
                case 1: Marshal.WriteByte(ptr, offset, (byte)val); break;
                case 2: Marshal.WriteInt16(ptr, offset, (short)val); break;
                case 4: Marshal.WriteInt32(ptr, offset, (int)val); break;
                default: Marshal.WriteInt64(ptr, offset, val); break;
            }
        }

        public static long Compute(StructDeclStmt decl, Dictionary<string, StructDeclStmt> structs)
        {
            var total = 0;
            foreach (var field in decl.Fields)
            {
                if (field.Access != AccessModifier.Public && field.Access != AccessModifier.Internal) continue;
                var fieldSize = GetFieldSize(field.Type, structs);
                if (fieldSize <= 0) continue;
                var align = fieldSize >= 4 ? 4 : (fieldSize >= 2 ? 2 : 1);
                total = (total + align - 1) / align * align;
                total += fieldSize;
            }
            return (total + 3) / 4 * 4;
        }

        internal static int GetFieldSize(TypeInfo type, Dictionary<string, StructDeclStmt> structs)
        {
            if (type.IsArray) return 8;
            if (type.IsNullable) return GetFieldSize(type with { IsNullable = false }, structs);
            if (type.TypeName != null && structs.TryGetValue(type.TypeName, out var nested))
                return (int)Compute(nested, structs);
            return type.BaseType switch
            {
                TokenType.Bool or TokenType.Byte or TokenType.SByte => 1,
                TokenType.Short or TokenType.UShort or TokenType.Char => 2,
                TokenType.Int or TokenType.UInt or TokenType.Float => 4,
                TokenType.Long or TokenType.ULong or TokenType.Double => 8,
                TokenType.String => 8,
                TokenType.Object => 8,
                TokenType.Decimal => 16,
                _ => 8
            };
        }

        /// <summary>Reads a single named field from a raw pointer using the script struct's field layout.</summary>
        public static object? ReadField(IntPtr ptr, StructDeclStmt decl, string fieldName, Dictionary<string, StructDeclStmt> structs)
        {
            var offset = 0;
            foreach (var field in decl.Fields)
            {
                if (field.Access != AccessModifier.Public && field.Access != AccessModifier.Internal) continue;
                if (field.Name == fieldName)
                {
                    var fs = GetFieldSize(field.Type, structs);
                    return ReadFromPtr(ptr, offset, fs);
                }
                offset += GetFieldSize(field.Type, structs);
            }
            throw new ScriptException($"Field '{fieldName}' not found in public fields of struct '{decl.Name}'");
        }

        /// <summary>Writes a single named field to a raw pointer using the script struct's field layout.</summary>
        public static void WriteField(IntPtr ptr, StructDeclStmt decl, string fieldName, object? value, Dictionary<string, StructDeclStmt> structs)
        {
            var offset = 0;
            var fcount = 0;
            foreach (var field in decl.Fields)
            {
                fcount++;
                if (field.Access != AccessModifier.Public && field.Access != AccessModifier.Internal) continue;
                if (field.Name == fieldName)
                {
                    var fs = GetFieldSize(field.Type, structs);
                    WriteToPtr(ptr, offset, fs, Convert.ToInt64(value));
                    return;
                }
                offset += GetFieldSize(field.Type, structs);
            }
            throw new ScriptException($"Field '{fieldName}' not found in struct '{decl.Name}' (fields={fcount})");
        }
    }

    /// <summary>Creates a StructInstance from raw memory by reading each field at its computed offset.</summary>
    private static object ReadStructFromDecl(IntPtr ptr, StructDeclStmt decl)
    {
        var fields = new Dictionary<string, object?>(decl.Fields.Count);
        var offset = 0;
        foreach (var field in decl.Fields)
        {
            if (field.Access != AccessModifier.Public && field.Access != AccessModifier.Internal) continue;
            var fs = StructSizeComputer.GetFieldSize(field.Type, Current!.Structs);
            fields[field.Name] = ReadFieldValue(field, ptr, offset, fs);
            offset += fs;
        }
        return new StructInstance(decl.Name, fields, decl.Line) { Declaration = decl };
    }

    /// <summary>Reads a field value from raw memory, recursively creating StructInstance for nested struct types.</summary>
    private static object? ReadFieldValue(FieldDecl field, IntPtr ptr, int offset, int size)
    {
        if (field.Type.TypeName == null || !Current!.Structs.TryGetValue(field.Type.TypeName, out var nestedDecl))
            return StructSizeComputer.ReadFromPtr(ptr, offset, size);

        var nestedFields = new Dictionary<string, object?>(nestedDecl.Fields.Count);

        var no = 0;
        foreach (var nf in nestedDecl.Fields)
        {
            if (nf.Access != AccessModifier.Public && nf.Access != AccessModifier.Internal) continue;
            var nfs = StructSizeComputer.GetFieldSize(nf.Type, Current.Structs);
            nestedFields[nf.Name] = ReadFieldValue(nf, ptr, offset + no, nfs);
            no += nfs;
        }
        return new StructInstance(nestedDecl.Name, nestedFields, nestedDecl.Line) { Declaration = nestedDecl };
    }
}