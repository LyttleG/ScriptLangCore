namespace ScriptLangCore;
// ── Cached native exports for sqlite3 ──
internal sealed record SqliteExports
{
    public nint Exec { get; private init; }
    public nint Prepare { get; private init; }
    public nint Step { get; private init; }
    public nint Finalize { get; private init; }
    public nint Reset { get; private init; }
    public nint ClearBindings { get; private init; }
    public nint Close { get; private init; }
    public nint Open { get; private init; }
    public nint ColumnCount { get; private init; }
    public nint ColumnName { get; private init; }
    public nint ColumnType { get; private init; }
    public nint ColumnInt { get; private init; }
    public nint ColumnInt64 { get; private init; }
    public nint ColumnDouble { get; private init; }
    public nint ColumnText { get; private init; }
    public nint ColumnBytes { get; private init; }
    public nint ColumnBlob { get; private init; }
    public nint CreateFunction { get; private init; }

    private static readonly ConcurrentDictionary<nint, SqliteExports> Cache = new();

    // Caches the mapping from dbHandle → lib handle for auto-resolution.
    internal static readonly ConcurrentDictionary<long, nint> Map = new();

    public static SqliteExports GetOrLoad(nint lib)
    {
        return Cache.GetOrAdd(lib, static key =>
        {
            nint closePtr;
            try { closePtr = NativeLibrary.GetExport(key, "sqlite3_close_v2"); }
            catch { closePtr = NativeLibrary.GetExport(key, "sqlite3_close"); }
            return new SqliteExports
            {
                Exec = NativeLibrary.GetExport(key, "sqlite3_exec"),
                Prepare = NativeLibrary.GetExport(key, "sqlite3_prepare_v2"),
                Step = NativeLibrary.GetExport(key, "sqlite3_step"),
                Finalize = NativeLibrary.GetExport(key, "sqlite3_finalize"),
                Reset = NativeLibrary.GetExport(key, "sqlite3_reset"),
                ClearBindings = NativeLibrary.GetExport(key, "sqlite3_clear_bindings"),
                Close = closePtr,
                Open = NativeLibrary.GetExport(key, "sqlite3_open"),
                ColumnCount = NativeLibrary.GetExport(key, "sqlite3_column_count"),
                ColumnName = NativeLibrary.GetExport(key, "sqlite3_column_name"),
                ColumnType = NativeLibrary.GetExport(key, "sqlite3_column_type"),
                ColumnInt = NativeLibrary.GetExport(key, "sqlite3_column_int"),
                ColumnInt64 = NativeLibrary.GetExport(key, "sqlite3_column_int64"),
                ColumnDouble = NativeLibrary.GetExport(key, "sqlite3_column_double"),
                ColumnText = NativeLibrary.GetExport(key, "sqlite3_column_text"),
                ColumnBytes = NativeLibrary.GetExport(key, "sqlite3_column_bytes"),
                ColumnBlob = NativeLibrary.GetExport(key, "sqlite3_column_blob"),
                CreateFunction = NativeLibrary.GetExport(key, "sqlite3_create_function"),
            };
        });
    }

    /// <summary>Clears the static cache. Call from Interpreter.Dispose() to free native exports.</summary>
    public static void ClearCache() => Cache.Clear();
}

internal static partial class SqlitePragmas
{
    internal static readonly string[] OptimizeForBulk =
    [
        "PRAGMA synchronous=OFF",
        "PRAGMA temp_store=MEMORY",
        "PRAGMA foreign_keys=OFF",
        "PRAGMA cache_size=-80000",
    ];

    internal static readonly string[] RestoreDefaults =
    [
        "PRAGMA synchronous=NORMAL",
        "PRAGMA temp_store=DEFAULT",
        "PRAGMA foreign_keys=ON",
        "PRAGMA cache_size=-2000",
    ];
}

// ── Zero-copy blob wrapper: wraps a native SQLite blob pointer as ReadOnlyMemory<byte> ──
internal sealed unsafe class SqliteBlobManager(IntPtr ptr, int length) : MemoryManager<byte>
{
    private readonly void* _ptr = ptr.ToPointer();
    public override Span<byte> GetSpan() => new(_ptr, length);
    public override MemoryHandle Pin(int elementIndex = 0) => new(Unsafe.Add<byte>(_ptr, elementIndex));
    public override void Unpin() { }
    protected override void Dispose(bool disposing) { }
}

// ── UDF callback delegate matching sqlite3_xFunc signature ──
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void Sqlite3ScalarFunc(IntPtr ctx, int argc, IntPtr argv);

// ── P/Invoke bridge for sqlite3 (resolves same DLL as LoadLibrary("sqlite3")) ──
internal static partial class SqliteNative
{
    private const string DLL = "sqlite3";

    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial int sqlite3_create_function(IntPtr db, byte[] name, int nArg, int eTextRep, IntPtr pApp, IntPtr xFunc, IntPtr xStep, IntPtr xFinal);

    /// <summary>The connection's OWN error sentence. A failed prepare has a code AND a sentence, and only the sentence is
    /// actionable: measured through <c>Sqlite.Query</c> with a wrong-arity call, the refusal used to read
    /// <c>sqlite3_prepare_v2 failed: rc=1 for SQL: ...</c> while SQLite itself was saying
    /// <c>wrong number of arguments to function slc_map()</c>. Bound by NAME, so no sqlite3_api_routines index is
    /// involved (that table is the one place this repository insists on <c>offsetof</c>).</summary>
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    internal static partial IntPtr sqlite3_errmsg(IntPtr db);

    /// <summary>THE SENTENCE A RETURN CODE IS WORTH NOTHING WITHOUT, as a suffix (<c>": ..."</c>, or "" when the host
    /// answers none). Every refusal in this file that reports a SQLite code adds it through this one helper, so the
    /// wording exists once.</summary>
    internal static string ErrorText(IntPtr db)
    {
        var text = sqlite3_errmsg(db);
        var why = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text);
        return string.IsNullOrEmpty(why) ? "" : ": " + why;
    }

    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int sqlite3_value_type(IntPtr v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial long sqlite3_value_int64(IntPtr v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial double sqlite3_value_double(IntPtr v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial IntPtr sqlite3_value_text(IntPtr v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial int sqlite3_value_bytes(IntPtr v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial IntPtr sqlite3_value_blob(IntPtr v);

    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void sqlite3_result_int(IntPtr ctx, int v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void sqlite3_result_int64(IntPtr ctx, long v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void sqlite3_result_double(IntPtr ctx, double v);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void sqlite3_result_text(IntPtr ctx, byte[] v, int n, IntPtr dtor);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void sqlite3_result_null(IntPtr ctx);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial void sqlite3_result_blob(IntPtr ctx, byte[] v, int n, IntPtr dtor);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial void sqlite3_result_error(IntPtr ctx, byte[] msg, int len);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_bind_null(IntPtr stmt, int index);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_bind_int64(IntPtr stmt, int index, long val);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_bind_double(IntPtr stmt, int index, double val);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_bind_text(IntPtr stmt, int index, byte[] val, int len, IntPtr dtor);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_bind_blob(IntPtr stmt, int index, byte[] val, int len, IntPtr dtor);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_clear_bindings(IntPtr stmt);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_reset(IntPtr stmt);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_enable_load_extension(IntPtr db, int onoff);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_load_extension(IntPtr db, byte[] ext, byte[] zProc, out IntPtr errMsg);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_finalize(IntPtr stmt);
    [LibraryImport(DLL)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] internal static partial int sqlite3_close_v2(IntPtr db);

    /// <summary>Reads a BLOB value pointer as zero-copy ReadOnlyMemory&lt;byte&gt;.
    /// The pointer is valid for the duration of the UDF callback only.</summary>
    internal static ReadOnlyMemory<byte> ReadBlobValueMemory(IntPtr valPtr)
    {
        var n = sqlite3_value_bytes(valPtr);
        if (n == 0) return default;
        var blobPtr = sqlite3_value_blob(valPtr);
        return blobPtr == IntPtr.Zero ? default(ReadOnlyMemory<byte>) : new SqliteBlobManager(blobPtr, n).Memory;
    }

    internal static object? ReadValue(IntPtr valPtr)
    {
        var typeCode = sqlite3_value_type(valPtr);
        if (typeCode == 5) return null; // SQLITE_NULL
        switch (typeCode)
        {
            case 1:
                return sqlite3_value_int64(valPtr);
            case 2:
                return sqlite3_value_double(valPtr);
            case 3:
                var txtPtr = sqlite3_value_text(valPtr);
                return txtPtr != IntPtr.Zero ? Marshal.PtrToStringAnsi(txtPtr) : null;
            case 4:
                {
                    var n = sqlite3_value_bytes(valPtr);
                    if (n == 0) return Array.Empty<byte>();
                    var blobPtr = sqlite3_value_blob(valPtr);
                    if (blobPtr == IntPtr.Zero) return Array.Empty<byte>();
                    var result = new byte[n];
                    Marshal.Copy(blobPtr, result, 0, n);
                    return result;
                }
            default:
                return null;
        }
    }

    internal static void WriteResult(IntPtr ctx, object? result)
    {
        switch (result)
        {
            case null: sqlite3_result_null(ctx); return;
            case int i: sqlite3_result_int(ctx, i); return;
            case long l: sqlite3_result_int64(ctx, l); return;
            case uint ui: sqlite3_result_int64(ctx, ui); return;
            case ulong ul: sqlite3_result_int64(ctx, (long)ul); return;
            case bool b: sqlite3_result_int(ctx, b ? 1 : 0); return;
            case double d: sqlite3_result_double(ctx, d); return;
            case float f: sqlite3_result_double(ctx, f); return;
            case byte b: sqlite3_result_int(ctx, b); return;
            case short s: sqlite3_result_int(ctx, s); return;
            case ushort us: sqlite3_result_int(ctx, us); return;
            case sbyte sb: sqlite3_result_int(ctx, sb); return;
            case char c: sqlite3_result_int(ctx, c); return;
            case decimal m:
                sqlite3_result_double(ctx, (double)m);
                return;
            case DateTime dt:
                {
                    var bytes = Encoding.UTF8.GetBytes(dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    sqlite3_result_text(ctx, bytes, bytes.Length, IntPtr.Zero);
                    return;
                }
            case DateTimeOffset dto:
                {
                    var bytes = Encoding.UTF8.GetBytes(dto.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture));
                    sqlite3_result_text(ctx, bytes, bytes.Length, IntPtr.Zero);
                    return;
                }
            case TimeSpan ts:
                {
                    var bytes = Encoding.UTF8.GetBytes(ts.ToString());
                    sqlite3_result_text(ctx, bytes, bytes.Length, IntPtr.Zero);
                    return;
                }
            case string s:
                {
                    var bytes = Encoding.UTF8.GetBytes(s);
                    sqlite3_result_text(ctx, bytes, bytes.Length, IntPtr.Zero);
                    return;
                }
            case byte[] bArr:
                sqlite3_result_blob(ctx, bArr, bArr.Length, IntPtr.Zero);
                return;
            default:
                {
                    var str = result.ToString() ?? "";
                    var bytes = Encoding.UTF8.GetBytes(str);
                    sqlite3_result_text(ctx, bytes, bytes.Length, IntPtr.Zero);
                    return;
                }
        }
    }
}

// ── Type-dispatch binder: maps CLR types to sqlite3_bind_* via Dictionary ──
internal static partial class SqliteBind
{
    private delegate int Binder(IntPtr stmt, int index, object? val);

    private static readonly Dictionary<Type, Binder> Binders = new()
    {
        [typeof(int)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (int)v!),
        [typeof(long)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (long)v!),
        [typeof(short)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (short)v!),
        [typeof(byte)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (byte)v!),
        [typeof(sbyte)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (sbyte)v!),
        [typeof(uint)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (uint)v!),
        [typeof(ushort)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (ushort)v!),
        [typeof(bool)] = (s, i, v) => SqliteNative.sqlite3_bind_int64(s, i, (bool)v! ? 1L : 0L),
        [typeof(float)] = (s, i, v) => SqliteNative.sqlite3_bind_double(s, i, (float)v!),
        [typeof(double)] = (s, i, v) => SqliteNative.sqlite3_bind_double(s, i, (double)v!),
        [typeof(decimal)] = (s, i, v) => SqliteNative.sqlite3_bind_double(s, i, (double)(decimal)v!),
    };

    internal static int Bind(IntPtr stmt, int index, object? val)
    {
        if (val == null)
            return SqliteNative.sqlite3_bind_null(stmt, index);

        var type = val.GetType();
        if (Binders.TryGetValue(type, out var binder))
            return binder(stmt, index, val);

        switch (val)
        {
            case string s:
                {
                    var bytes = Encoding.UTF8.GetBytes(s);
                    return SqliteNative.sqlite3_bind_text(stmt, index, bytes, bytes.Length, SqliteIterator.SqliteTransient);
                }
            case byte[] bArr:
                return SqliteNative.sqlite3_bind_blob(stmt, index, bArr, bArr.Length, SqliteIterator.SqliteTransient);
        }

        // Fallback: ToString → TEXT
        var str = val.ToString() ?? "";
        var bytes2 = Encoding.UTF8.GetBytes(str);
        return SqliteNative.sqlite3_bind_text(stmt, index, bytes2, bytes2.Length, SqliteIterator.SqliteTransient);
    }
}

// ── UDF Registry, keeps managed callbacks alive ──
internal static partial class UdfRegistry
{
    private static readonly Dictionary<string, (object Callback, GCHandle GcHandle)> Udfs = new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(IntPtr db, string name, object callback, IntPtr nativePtr)
    {
        var key = $"{db.ToInt64():X}:{name}";
        if (Udfs.TryGetValue(key, out var existing))
            existing.GcHandle.Free();
        var handle = GCHandle.Alloc(callback);
        Udfs[key] = (callback, handle);
    }

    internal static void Unregister(IntPtr db, string name)
    {
        var key = $"{db.ToInt64():X}:{name}";
        if (!Udfs.TryGetValue(key, out var existing)) return;
        existing.GcHandle.Free();
        Udfs.Remove(key);
    }

    internal static void UnregisterAll(IntPtr db)
    {
        var prefix = $"{db.ToInt64():X}:";
        var keysToRemove = Udfs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in keysToRemove)
        {
            Udfs[key].GcHandle.Free();
            Udfs.Remove(key);
        }
    }
}

internal partial class Interpreter
{
    private void RegisterSqliteCursor()
    {
        var ns = new Namespace(_comparer);

        ns.AddMethod("Query", args =>
        {
            // Unified Query method, replaces OpenCursor and OpenCursorOnDb.
            // Detects form by argument types:
            //   Query(lib, path, sql[, params]) , OpenCursor (new connection)
            //   Query(lib, db, sql[, params])   , OpenCursorOnDb (existing handle)
            //   Query(path, sql[, params])      , OpenCursor with auto-loaded lib
            //   Query(db, sql[, params])        , OpenCursorOnDb with auto-resolved lib

            if (args[0] is DynamicLibrary dl)
            {
                // Explicit lib handle
                var lib = dl.Handle;
                if (args[1] is string path)
                {
                    // Query(lib, path, sql[, params]), new connection
                    var sql = args[2] as string ?? throw new ScriptException("SQL required");
                    var parameters = args.Length > 3 ? ConvertToObjectArray(args[3]) : null;
                    return SqliteIterator.Open(lib, path, sql, parameters);
                }
                else
                {
                    // Query(lib, db, sql[, params]), existing handle
                    var dbVal = Convert.ToInt64(args[1]);
                    var sql = args[2] as string ?? throw new ScriptException("SQL required");
                    var parameters = args.Length > 3 ? ConvertToObjectArray(args[3]) : null;
                    return SqliteIterator.OpenOnDb(lib, new IntPtr(dbVal), sql, parameters);
                }
            }
            else if (args[0] is string dbPath)
            {
                // Query(path, sql[, params]), auto-load lib + new connection
                var libHandle = SqliteIterator.LoadSqliteLibrary();
                var sql = args[1] as string ?? throw new ScriptException("SQL required");
                var parameters = args.Length > 2 ? ConvertToObjectArray(args[2]) : null;
                return SqliteIterator.Open(libHandle, dbPath, sql, parameters);
            }
            else
            {
                // Query(db, sql[, params]), auto-resolve lib + existing handle
                var dbVal = Convert.ToInt64(args[0]);
                var sql = args[1] as string ?? throw new ScriptException("SQL required");
                var parameters = args.Length > 2 ? ConvertToObjectArray(args[2]) : null;
                var lib = SqliteExports.Map.GetValueOrDefault(dbVal);
                return lib == 0
                    ? throw new ScriptException("Cannot resolve sqlite3 library handle. Use OpenDb(path) first, or pass lib explicitly: Query(lib, db, sql).")
                    : SqliteIterator.OpenOnDb(lib, new IntPtr(dbVal), sql, parameters);
            }
        });

        ns.AddMethod("QueryLazy", args =>
        {
            // Lazy counterpart of Query on an existing handle, a column is copied only when the
            // row is asked for one. Open-handle forms only, deliberately: Query's own dispatcher
            // carries the typed overloads and the new-connection forms, none of which this needs.
            //   QueryLazy(lib, db, sql[, params]) , existing handle, explicit lib
            //   QueryLazy(db, sql[, params])      , existing handle, auto-resolved lib
            nint lib;
            long dbVal;
            string sql;
            object?[]? parameters;

            if (args[0] is DynamicLibrary dl)
            {
                lib = dl.Handle;
                dbVal = Convert.ToInt64(args[1]);
                sql = args[2] as string ?? throw new ScriptException("SQL required");
                parameters = args.Length > 3 ? ConvertToObjectArray(args[3]) : null;
            }
            else
            {
                dbVal = Convert.ToInt64(args[0]);
                sql = args[1] as string ?? throw new ScriptException("SQL required");
                parameters = args.Length > 2 ? ConvertToObjectArray(args[2]) : null;
                lib = SqliteExports.Map.GetValueOrDefault(dbVal);
                if (lib == 0)
                    throw new ScriptException("Cannot resolve sqlite3 library handle. Use OpenDb(path) first, or pass lib explicitly: QueryLazy(lib, db, sql).");
            }

            return SqliteIterator.OpenOnDb(lib, new IntPtr(dbVal), sql, parameters, lazy: true);
        });

        ns.AddMethod("OpenDb", args =>
        {
            // Support both OpenDb(lib, path) and OpenDb(path)
            nint lib;
            string path;
            if (args[0] is string dbPath)
            {
                path = dbPath;
                // Auto-load sqlite3.dll
                var libHandle = SqliteIterator.LoadSqliteLibrary();
                lib = libHandle;
            }
            else
            {
                lib = args[0] is DynamicLibrary dl ? dl.Handle : new IntPtr(Convert.ToInt64(args[0]));
                path = args[1] as string ?? throw new ScriptException("Path required");
            }
            var exp = SqliteExports.GetOrLoad(lib);
            long dbVal = 0;
            int openRc;
            unsafe
            {
                var pf = (delegate* unmanaged[Cdecl]<byte*, long*, int>)exp.Open;
                var pb = Encoding.UTF8.GetBytes($"{path}\0");
                fixed (byte* p = pb)
                {
                    var d = &dbVal; openRc = pf(p, d);
                }
            }
            if (dbVal == 0)
                throw new ScriptException($"Failed to open database: {path}");
            // sqlite3_open ANSWERS a handle even when it could not create or read the file, and every later statement
            // then fails with "unable to open database file" and no path, which is what made a read-only folder look
            // like a missing sqlite3.dll. The code is checked here so the refusal names the file.
            if (openRc != 0)
            {
                var why = SqliteNative.ErrorText(new IntPtr(dbVal));
                unsafe
                {
                    var pc = (delegate* unmanaged[Cdecl]<long, int>)exp.Close;
                    pc(dbVal);
                }
                throw SqliteIterator.DatabaseOpenFailure(path, openRc, why);
            }

            // Track the db handle for cleanup on Interpreter.Dispose
            var ptr = new IntPtr(dbVal);
            if (!_registeredUdfDbHandles.Contains(ptr))
                _registeredUdfDbHandles.Add(ptr);

            // Cache lib → db mapping for auto-resolution in other calls
            SqliteExports.Map[dbVal] = lib;

            return dbVal;
        });

        ns.AddMethod("CloseDb", args =>
        {
            if (args.Length == 0)
                throw new ScriptException("CloseDb expects a db handle (Int64 from OpenDb).");

            var dbVal = Convert.ToInt64(args[0]);
            if (dbVal == 0)
                return false;

            if (!SqliteExports.Map.TryRemove(dbVal, out var lib))
                return false;

            var exp = SqliteExports.GetOrLoad(lib);
            if (exp.Close == nint.Zero)
                return false;

            int rc;
            unsafe { rc = ((delegate* unmanaged[Cdecl]<long, int>)exp.Close)(dbVal); }

            if (rc != 0)
            {
                // re-add to Map on failure (should never happen with close_v2)
                SqliteExports.Map[dbVal] = lib;
                return false;
            }

            // Remove from _registeredUdfDbHandles to prevent double-close by Reset
            _registeredUdfDbHandles.Remove(new IntPtr(dbVal));

            return true;
        });

        ns.AddTypedMethod("Query", (args, typeArgs) =>
        {
            // Typed Query<T> / Query<T,V>, same source detection as untyped Query,
            // with typed cursor (1 arg) or variant return (2+ args).

            // ── 1. Resolve lib, source (path vs handle), same pattern as Query ──
            nint lib;
            string? path = null;
            var dbHandle = IntPtr.Zero;
            int sqlIdx;

            switch (args[0])
            {
                case DynamicLibrary dl:
                    {
                        lib = dl.Handle;
                        if (args[1] is string p)
                        {
                            // Query(lib, path, sql[, params]), new connection
                            path = p;
                            sqlIdx = 2;
                        }
                        else
                        {
                            // Query(lib, db, sql[, params]), existing handle
                            dbHandle = new IntPtr(Convert.ToInt64(args[1]));
                            sqlIdx = 2;
                        }

                        break;
                    }
                case string dbPath:
                    // Query(path, sql[, params]), autoload lib
                    lib = SqliteIterator.LoadSqliteLibrary();
                    path = dbPath;
                    sqlIdx = 1;
                    break;
                default:
                    {
                        // Query(db, sql[, params]), auto-resolve lib
                        var dbVal = Convert.ToInt64(args[0]);
                        dbHandle = new IntPtr(dbVal);
                        lib = SqliteExports.Map.GetValueOrDefault(dbVal);
                        if (lib == 0)
                            throw new ScriptException(
                                "Cannot resolve sqlite3 library handle. Use OpenDb(path) first, " +
                                "or pass lib explicitly: Query<T>(lib, db, sql).");
                        sqlIdx = 1;
                        break;
                    }
            }

            // ── 2. Extract sql and optional params ──
            var sql = args[sqlIdx] as string ?? throw new ScriptException("SQL required");
            var parameters = args.Length > sqlIdx + 1
                ? ConvertToObjectArray(args[sqlIdx + 1])
                : null;

            // ── 3. Open cursor ──
            var cursor = path != null
                ? SqliteIterator.Open(lib, path, sql, parameters)
                : SqliteIterator.OpenOnDb(lib, dbHandle, sql, parameters);

            // ── 4. Resolve class info (with scope fallback for import edge cases) ──
            var className = typeArgs[0].TypeName
                ?? throw new ScriptException("Type argument required");

            // THE RUN'S CLASS TABLE, not the registering interpreter's (a namespace is cloned into every later instance,
            // so in an embedded session `this` is the first interpreter): measured through the launcher, this answered
            // "Class 'WideRow' not found" for a class the same file had just declared.
            var run = Current ?? this;
            if (!run.Classes.TryGetValue(className, out var classInfo))
            {
                if (CurrentFrame.Scopes.TryGet(className, out var scopeVal) && scopeVal is ClassInfo ci)
                    classInfo = ci;
                else
                    throw new ScriptException($"Class '{className}' not found.");
            }
            if (classInfo.Access != AccessModifier.Public && classInfo.Access != AccessModifier.Internal)
                throw new ScriptException($"Class '{className}' is not accessible ({classInfo.Access}).");

            // ── 5. Return based on type arg count ──
            if (typeArgs.Count == 1)
            {
                // Simple typed cursor: FromSql<T>
                return new SqliteMappingIterator(cursor, classInfo);
            }

            // V-style dispatch: FromSql<T, V>
            var retType = typeArgs[1];
            var retName = retType.TypeName;

            // List<T>
            if (retName != null && retName.Equals("List", StringComparison.OrdinalIgnoreCase)
                && retType.GenericArgs?.Count == 1)
            {
                var list = CreateScriptList(retType.GenericArgs[0]);
                foreach (ScriptBoxedDictionary row in cursor)
                    list.Add(SqliteMappingIterator.MapRowToInstance(row, classInfo), 0, 0);
                return list;
            }

            // Dictionary<K, V>
            if (retName != null && retName.Equals("Dictionary", StringComparison.OrdinalIgnoreCase)
                && retType.GenericArgs?.Count == 2)
            {
                var dict = CreateScriptDictionary(retType.GenericArgs[0], retType.GenericArgs[1]);
                foreach (ScriptBoxedDictionary row in cursor)
                {
                    // Get first column value as dictionary key, narrowing long to int
                    object? firstVal = null;
                    foreach (DictionaryEntry kv in row.Dictionary)
                    {
                        firstVal = kv.Value is long l and >= int.MinValue and <= int.MaxValue ? (int)l : kv.Value; break;
                    }
                    var key = firstVal != null
                        ? CoerceTo(firstVal, retType.GenericArgs[0], 0, 0)
                        : null;
                    var val = SqliteMappingIterator.MapRowToInstance(row, classInfo);
                    if (key != null) dict.Dictionary[key] = val;
                }
                return dict;
            }

            // Singleton class instance
            if (retName != null && Classes.ContainsKey(retName))
            {
                return (from ScriptBoxedDictionary row in cursor
                        select SqliteMappingIterator.MapRowToInstance(row, classInfo)).FirstOrDefault();
            }

            // Scalar (first-column coercion)
            if (retType is { BaseType: TokenType.Object, IsNullable: false })
                return new SqliteMappingIterator(cursor, classInfo);
            {
                foreach (ScriptBoxedDictionary row in cursor)
                {
                    foreach (DictionaryEntry kv in row.Dictionary)
                        return CoerceTo(kv.Value, retType, 0, 0);
                }
                return DefaultFor(retType);
            }

            // Fallback: typed cursor
        });

        ns.AddMethod("UnregisterFunctions", args =>
        {
            try
            {
                var dbHandle = new IntPtr(Convert.ToInt64(args[0]));
                if (dbHandle != IntPtr.Zero)
                {
                    UdfRegistry.UnregisterAll(dbHandle);

                    return true;
                }
            }
            catch (Exception ex)
            {
                throw new ScriptException(ex);
            }

            return false;
        });

        ns.AddMethod("CreateFunction", args =>
        {
            nint lib;
            long dbVal;
            string name;
            int argCount;
            bool deterministic;
            object? callback;

            if (args[0] is DynamicLibrary dl)
            {
                // Long form: CreateFunction(lib, db, name, argCount, deterministic, callback)
                lib = dl.Handle;
                dbVal = Convert.ToInt64(args[1]);
                name = args[2] as string ?? throw new ScriptException("Function name required");
                argCount = Convert.ToInt32(args[3]);
                deterministic = args.Length > 4 && args[4] is bool and true;
                callback = args.Length > 5 ? args[5] : null;
            }
            else
            {
                // Short form: CreateFunction(db, name, argCount, deterministic, callback), auto-resolve lib
                dbVal = Convert.ToInt64(args[0]);
                name = args[1] as string ?? throw new ScriptException("Function name required");
                argCount = Convert.ToInt32(args[2]);
                deterministic = args.Length > 3 && args[3] is bool and true;
                callback = args.Length > 4 ? args[4] : null;
                lib = SqliteExports.Map.GetValueOrDefault(dbVal);
                if (lib == 0)
                    throw new ScriptException("Cannot resolve sqlite3 library handle. Use OpenDb(path) first, or pass lib explicitly: CreateFunction(lib, db, name, ...).");
            }

            if (callback == null)
                throw new ScriptException("Callback function required");

            var dbHandle = new IntPtr(dbVal);
            var interp = this;
            var managedCallback = new Sqlite3ScalarFunc((ctx, argc, argvPtr) =>
            {
                try
                {
                    var callArgs = new object?[argc];
                    var ptrSize = IntPtr.Size;
                    for (var i = 0; i < argc; i++)
                    {
                        var valPtr = Marshal.ReadIntPtr(argvPtr, i * ptrSize);
                        callArgs[i] = SqliteNative.ReadValue(valPtr);
                    }

                    object? result;
                    if (callback is ScriptFunction sf)
                        result = interp.InvokeScriptFunction(sf, callArgs, 0, 0);
                    else
                        result = null;

                    SqliteNative.WriteResult(ctx, result);
                }
                catch (Exception ex)
                {
                    var errBytes = Encoding.UTF8.GetBytes(ex.Message);
                    SqliteNative.sqlite3_result_error(ctx, errBytes, errBytes.Length);
                }
            });

            var pCallback = Marshal.GetFunctionPointerForDelegate(managedCallback);
            UdfRegistry.Register(dbHandle, name, managedCallback, pCallback);
            if (!_registeredUdfDbHandles.Contains(dbHandle))
                _registeredUdfDbHandles.Add(dbHandle);

            const int SQLITE_UTF8 = 1;
            const int SQLITE_DETERMINISTIC = 2048;
            var flags = SQLITE_UTF8 | (deterministic ? SQLITE_DETERMINISTIC : 0);

            var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
            var exp = SqliteExports.GetOrLoad(lib);
            int rc;
            unsafe
            {
                var pFunc = (delegate* unmanaged[Cdecl]<long, byte*, int, int, long, long, long, long, int>)exp.CreateFunction;
                fixed (byte* pName = nameBytes)
                    rc = pFunc(dbHandle.ToInt64(), pName, argCount, flags, 0, pCallback.ToInt64(), 0, 0);
            }

            return rc != 0 ? throw new ScriptException($"sqlite3_create_function failed: rc={rc}") : true;
        });

        ns.AddMethod("BulkInsert", args =>
        {
            var (lib, dbHandle) = ParseDbArgs(args);
            var tableName = args[2] as string ?? throw new ScriptException("Table name required");
            int batchSize;

            if (args[3] is byte[] byteData)
            {
                // ── Binary path (former BulkInsertBytes) ──
                batchSize = args.Length > 4 ? Convert.ToInt32(args[4]) : 1000;
                if (batchSize <= 0) batchSize = 1000;
                var (_, pExec) = GetExecOrThrow(lib);

                // Read header
                using var reader = new BinaryReader(new MemoryStream(byteData));
                var colCount = reader.ReadInt32();
                var rowCount = reader.ReadInt32();

                // Read column names (null-terminated UTF-8 strings)
                var colNames = new string[colCount];
                for (var ci = 0; ci < colCount; ci++)
                {
                    var nameLen = reader.ReadInt32();
                    var nameBytes = reader.ReadBytes(nameLen);
                    colNames[ci] = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
                }

                // Build column list string
                var colSb = new StringBuilder();
                for (var ci = 0; ci < colCount; ci++)
                {
                    if (ci > 0) colSb.Append(", ");
                    colSb.Append(colNames[ci]);
                }
                var colsPart = colSb.ToString();

                var savepoint = "bulk_sp";

                // Optimize pragmas
                ApplyBulkPragmas(pExec, dbHandle);
                ExecSql(pExec, dbHandle, "BEGIN IMMEDIATE TRANSACTION");
                ExecSql(pExec, dbHandle, $"SAVEPOINT {savepoint};");

                try
                {
                    var rowIdx = 0;
                    while (rowIdx < rowCount)
                    {
                        var sqlSb = new StringBuilder();
                        sqlSb.Append("INSERT INTO ").Append(tableName).Append(" (").Append(colsPart).Append(") VALUES ");
                        var firstRow = true;
                        var batchEnd = rowIdx + batchSize;
                        if (batchEnd > rowCount) batchEnd = rowCount;

                        while (rowIdx < batchEnd)
                        {
                            if (!firstRow) sqlSb.Append(',');
                            firstRow = false;
                            sqlSb.Append('(');
                            for (var ci = 0; ci < colCount; ci++)
                            {
                                if (ci > 0) sqlSb.Append(", ");
                                var tag = reader.ReadByte();
                                switch (tag)
                                {
                                    case 0: sqlSb.Append("NULL"); break;
                                    case 1: sqlSb.Append(reader.ReadInt64()); break;
                                    case 2: sqlSb.Append(reader.ReadDouble()); break;
                                    case 3:
                                        var strLen = reader.ReadInt32();
                                        var strBytes = reader.ReadBytes(strLen);
                                        var str = Encoding.UTF8.GetString(strBytes);
                                        sqlSb.Append('\'');
                                        foreach (var ch in str) { if (ch == '\'') sqlSb.Append("''"); else sqlSb.Append(ch); }
                                        sqlSb.Append('\'');
                                        break;
                                }
                            }
                            sqlSb.Append(')');
                            rowIdx++;
                        }
                        ExecSql(pExec, dbHandle, sqlSb.ToString());

                        if (rowIdx >= rowCount) continue;
                        ExecSql(pExec, dbHandle, $"RELEASE {savepoint};");
                        ExecSql(pExec, dbHandle, $"SAVEPOINT {savepoint};");
                    }

                    ExecSql(pExec, dbHandle, $"RELEASE {savepoint};");
                    ExecSql(pExec, dbHandle, "COMMIT");
                }
                catch
                {
                    try { ExecSql(pExec, dbHandle, $"ROLLBACK TO {savepoint};"); }
                    catch
                    {
                        // ignored
                    }

                    try { ExecSql(pExec, dbHandle, "ROLLBACK;"); }
                    catch
                    {
                        // ignored
                    }

                    throw;
                }
                finally
                {
                    RestoreDefaultPragmas(pExec, dbHandle);
                }
                return (long)rowCount;
            }

            // ── Script-list path (former BulkInsert) ──
            var colNames2 = AsScriptList(args[3]) ?? throw new ScriptException("Column names list required");
            var rows = AsScriptList(args[4]) ?? throw new ScriptException("Rows list required");
            batchSize = args.Length > 5 ? Convert.ToInt32(args[5]) : 1000;
            if (batchSize <= 0) batchSize = 1000;
            var useTransaction = args.Length <= 6 || args[6] is bool and true;

            var exp2 = SqliteExports.GetOrLoad(lib);
            var pExec2 = exp2.Exec;

            var pPrepare = exp2.Prepare;
            var pStep = exp2.Step;
            var pFin = exp2.Finalize;
            //var pReset = exp2.Reset;
            //var pClear = exp2.ClearBindings;

            // Build column names and placeholders (?1, ?2, ...)
            var colSb2 = new StringBuilder();
            var phSb = new StringBuilder();
            for (var ci = 0; ci < colNames2.Count; ci++)
            {
                if (ci > 0) { colSb2.Append(", "); phSb.Append(", "); }
                colSb2.Append(colNames2[ci]?.ToString() ?? "");
                phSb.Append('?').Append(ci + 1);
            }
            var insertSql = $"INSERT INTO {tableName} ({colSb2}) VALUES ({phSb})";

            // Prepare the INSERT statement once
            var stmtVal = (long)0;
            int prepareRc;
            unsafe
            {
                var pf = (delegate* unmanaged[Cdecl]<long, byte*, int, long*, long*, int>)pPrepare;
                var sb = Encoding.UTF8.GetBytes(insertSql);
                fixed (byte* s = sb) { var t = &stmtVal; prepareRc = pf(dbHandle.ToInt64(), s, -1, t, null); }
            }
            if (prepareRc != 0 || stmtVal == 0)
                throw new ScriptException($"Failed to prepare INSERT for table '{tableName}': rc={prepareRc}{SqliteNative.ErrorText(dbHandle)}");
            var stmtPtr = new IntPtr(stmtVal);

            var totalRows = rows.Count;
            var rowIdx2 = 0;
            var savepoint2 = "bulk_sp";

            try
            {
                // Always apply optimization PRAGMAs (even without caller transaction)
                ApplyBulkPragmas(pExec2, dbHandle);

                if (useTransaction)
                {
                    ExecSql(pExec2, dbHandle, "BEGIN IMMEDIATE TRANSACTION");
                    ExecSql(pExec2, dbHandle, $"SAVEPOINT {savepoint2};");
                }

                while (rowIdx2 < totalRows)
                {
                    var batchEnd = rowIdx2 + batchSize;
                    if (batchEnd > totalRows) batchEnd = totalRows;

                    while (rowIdx2 < batchEnd)
                    {
                        RewindForRow(stmtPtr);

                        var row = rows[rowIdx2];
                        for (var ci = 0; ci < colNames2.Count; ci++)
                        {
                            var idx = ci + 1;
                            object? val = null;
                            var rowList = AsScriptList(row);
                            if (rowList != null && ci < rowList.Count)
                                val = rowList[ci];
                            else if (colNames2[ci] is string colName &&
                                (row is StructInstance si && si.Fields.TryGetValue(colName, out var fv) ||
                                 row is ClassInstance cInst && cInst.FieldValues.TryGetValue(colName, out fv)))
                                val = fv;
                            var rc = SqliteBind.Bind(stmtPtr, idx, val);

                            if (rc != 0)
                                throw new ScriptException($"BulkInsert bind failed at row {rowIdx2}: rc={rc}");
                        }

                        int stepRc;
                        unsafe { stepRc = ((delegate* unmanaged[Cdecl]<IntPtr, int>)pStep)(stmtPtr); }
                        if (stepRc != 101 && stepRc != 100) // SQLITE_DONE or SQLITE_ROW
                            throw new ScriptException($"BulkInsert step failed at row {rowIdx2}: rc={stepRc}");

                        rowIdx2++;
                    }

                    if (!useTransaction || rowIdx2 >= totalRows) continue;
                    ExecSql(pExec2, dbHandle, $"RELEASE {savepoint2};");
                    ExecSql(pExec2, dbHandle, $"SAVEPOINT {savepoint2};");
                }

                if (useTransaction)
                {
                    ExecSql(pExec2, dbHandle, $"RELEASE {savepoint2};");
                    ExecSql(pExec2, dbHandle, "COMMIT");
                }
            }
            catch
            {
                if (!useTransaction) throw;
                try { ExecSql(pExec2, dbHandle, $"ROLLBACK TO {savepoint2};"); }
                catch
                {
                    // ignored
                }

                try { ExecSql(pExec2, dbHandle, "ROLLBACK;"); }
                catch
                {
                    // ignored
                }
                throw;
            }
            finally
            {
                unsafe { ((delegate* unmanaged[Cdecl]<IntPtr, int>)pFin)(stmtPtr); }

                // Always restore PRAGMAs (even without caller transaction)
                RestoreDefaultPragmas(pExec2, dbHandle);
            }
            return (long)totalRows;
        });

        ns.AddMethod("CsvImport", args =>
        {
            var (lib, dbHandle) = ParseDbArgs(args);

            var tableName = args[2] as string
                ?? throw new ScriptException("Table name required");

            var csvPath = args[3] as string
                ?? throw new ScriptException("CSV path required");
            csvPath = ResolvePath(csvPath);   // resolve relative to the script's directory, like File.Exists

            if (!File.Exists(csvPath))
                throw new ScriptException($"CSV file not found: {csvPath}");

            var batchSize = args.Length > 4 ? Convert.ToInt32(args[4]) : 10000;
            if (batchSize <= 0) batchSize = 10000;
            string[]? explicitCols = null;
            if (args.Length > 6 && args[6] is Array arr)
            {
                explicitCols = new string[arr.Length];
                for (var i = 0; i < arr.Length; i++)
                    explicitCols[i] = arr.GetValue(i)?.ToString() ?? "";
            }
            var hasHeader = (args.Length <= 5 || args[5] is bool and true) && explicitCols == null;

            var (exp, pExec) = GetExecOrThrow(lib);

            try
            {
                // Apply optimization PRAGMAs
                ApplyBulkPragmas(pExec, dbHandle);

                ExecSql(pExec, dbHandle, "BEGIN IMMEDIATE TRANSACTION");

                using var reader = new StreamReader(csvPath, Encoding.UTF8);
                string[]? columns;
                var totalRows = 0;

                // Read the first line to determine columns
                var firstLine = reader.ReadLine();
                if (firstLine == null)
                {
                    ExecSql(pExec, dbHandle, "COMMIT");
                    return 0L;
                }

                // Auto-detect delimiter from first data line
                var delimiter = DetectDelimiter(firstLine);

                var firstFields = SplitCsvLine(firstLine, delimiter);

                if (explicitCols != null)
                    columns = explicitCols;
                else if (hasHeader)
                    columns = firstFields;
                else
                {
                    // No header and no explicit cols, create generic column names
                    columns = new string[firstFields.Length];
                    for (var i = 0; i < columns.Length; i++)
                        columns[i] = "col" + (i + 1);
                }

                // Create table if not exists, all columns as TEXT (SQLite's flexible type)
                var createSb = new StringBuilder();
                createSb.Append("CREATE TABLE IF NOT EXISTS ").Append(tableName).Append(" (");
                for (var ci = 0; ci < columns.Length; ci++)
                {
                    if (ci > 0) createSb.Append(", ");
                    createSb.Append('"').Append(EscapeSqlName(columns[ci])).Append('"');
                    createSb.Append(" ANY");
                }
                createSb.Append(')');
                ExecSql(pExec, dbHandle, createSb.ToString());

                // If no header, first line is data, count it as a row
                // We'll build INSERTs in batches
                var colListSb = new StringBuilder();
                var colPlaceholders = new StringBuilder();
                for (var ci = 0; ci < columns.Length; ci++)
                {
                    if (ci > 0) { colListSb.Append(", "); colPlaceholders.Append(", "); }
                    colListSb.Append('"').Append(EscapeSqlName(columns[ci])).Append('"');
                    colPlaceholders.Append('?').Append(ci + 1);
                }
                var insertPrefix = $"INSERT INTO {tableName} ({colListSb}) VALUES ({colPlaceholders})";

                var pPrepare = exp.Prepare;
                if (pPrepare == nint.Zero) throw new ScriptException("sqlite3_prepare_v2 not found");

                // Prepare the INSERT statement
                var stmtVal = (long)0;
                int prepareRc;
                unsafe
                {
                    var pf = (delegate* unmanaged[Cdecl]<long, byte*, int, long*, long*, int>)pPrepare;
                    var sb = Encoding.UTF8.GetBytes(insertPrefix);
                    fixed (byte* s = sb) { var t = &stmtVal; prepareRc = pf(dbHandle.ToInt64(), s, -1, t, null); }
                }
                if (prepareRc != 0 || stmtVal == 0)
                    throw new ScriptException($"Failed to prepare INSERT for table '{tableName}': rc={prepareRc}{SqliteNative.ErrorText(dbHandle)}");
                var stmtPtr = new IntPtr(stmtVal);
                var pStep = exp.Step;
                var pFin = exp.Finalize;

                try
                {
                    // Helper to bind a row of CSV fields
                    void BindRow(string[] fields)
                    {
                        for (var ci = 0; ci < columns.Length; ci++)
                        {
                            var val = ci < fields.Length ? fields[ci] : "";
                            var bytes = Encoding.UTF8.GetBytes(val);
                            var rc = SqliteNative.sqlite3_bind_text(stmtPtr, ci + 1, bytes, bytes.Length, SqliteIterator.SqliteTransient);
                            if (rc != 0)
                                throw new ScriptException($"Bind failed at column {ci}: rc={rc}");
                        }
                    }

                    var firstRowIsData = !hasHeader && explicitCols == null;
                    if (firstRowIsData)
                    {
                        // The first line we already read is data
                        if (firstFields.Length > 0)
                        {
                            RewindForRow(stmtPtr);
                            BindRow(firstFields);
                            unsafe { ((delegate* unmanaged[Cdecl]<IntPtr, int>)pStep)(stmtPtr); }
                            totalRows++;
                        }
                    }

                    // Process remaining lines in batches
                    var batchRowCount = 0;
                    while (reader.ReadLine() is { } line)
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var fields = SplitCsvLine(line, delimiter);
                        RewindForRow(stmtPtr);
                        BindRow(fields);
                        unsafe { ((delegate* unmanaged[Cdecl]<IntPtr, int>)pStep)(stmtPtr); }
                        totalRows++;
                        batchRowCount++;

                        if (batchRowCount < batchSize) continue;
                        ExecSql(pExec, dbHandle, "COMMIT");
                        ExecSql(pExec, dbHandle, "BEGIN IMMEDIATE TRANSACTION");
                        batchRowCount = 0;
                    }

                    // Commit remaining
                    ExecSql(pExec, dbHandle, "COMMIT");
                    return (long)totalRows;
                }
                finally
                {
                    unsafe { ((delegate* unmanaged[Cdecl]<IntPtr, int>)pFin)(stmtPtr); }
                }
            }
            catch
            {
                try { ExecSql(pExec, dbHandle, "ROLLBACK;"); } catch { /* Ignore errors during rollback */ }
                return -1L;
            }
            finally
            {
                RestoreDefaultPragmas(pExec, dbHandle);
            }
        });

        ns.AddMethod("SerializeRows", args =>
        {
            var colNames = AsScriptList(args[0]) ?? throw new ScriptException("Column names list required");
            var rows = AsScriptList(args[1]) ?? throw new ScriptException("Rows list required");
            var colCount = colNames.Count;

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);

            w.Write(colCount);
            w.Write(rows.Count);

            // Column names (UTF-8)
            for (var ci = 0; ci < colCount; ci++)
            {
                var name = colNames[ci]?.ToString() ?? "";
                var nb = Encoding.UTF8.GetBytes(name);
                w.Write(nb.Length);
                w.Write(nb);
            }

            // Row data, serialize each value to the binary format
            for (var ri = 0; ri < rows.Count; ri++)
            {
                var row = AsScriptList(rows[ri]);
                for (var ci = 0; ci < colCount; ci++)
                {
                    var val = (row != null && ci < row.Count) ? row[ci] : null;
                    if (val == null) { w.Write((byte)0); }
                    else if (val is int iv) { w.Write((byte)1); w.Write((long)iv); }
                    else if (val is long lv) { w.Write((byte)1); w.Write(lv); }
                    else if (val is float fv) { w.Write((byte)2); w.Write((double)fv); }
                    else if (val is double dv) { w.Write((byte)2); w.Write(dv); }
                    else
                    {
                        var str = val.ToString() ?? "";
                        w.Write((byte)3);
                        var sb = Encoding.UTF8.GetBytes(str);
                        w.Write(sb.Length);
                        w.Write(sb);
                    }
                }
            }

            w.Flush();
            return ms.ToArray();
        });

        ns.AddMethod("LoadExtensions", args =>
        {
            var (lib, dbHandle) = ParseDbArgs(args);
            var first = args[2] as string ?? throw new ScriptException("Extension name required");
            var more = new List<string>();
            for (var i = 3; i < args.Length; i++)
            {
                if (args[i] is string s) more.Add(s);
            }
            try { LoadExtensionsForDb(lib, dbHandle, first, [.. more]); return true; }
            catch { return false; }
        });

        ns.AddMethod("AttachDatabase", args =>
        {
            var (lib, dbHandle) = ParseDbArgs(args);

            var path = args[2] as string
                ?? throw new ScriptException("Database path required");

            var alias = args[3] as string
                ?? throw new ScriptException("Database alias required");

            if (!path.Equals(":memory:", StringComparison.OrdinalIgnoreCase) && !File.Exists(path))
                return false;

            var (_, pExec) = GetExecOrThrow(lib);
            if (pExec == nint.Zero)
                return false;

            try
            {
                ExecSql(pExec, dbHandle, $"ATTACH DATABASE '{path}' AS {alias};");
                return true;
            }
            catch
            {
                return false;
            }
        });

        ns.AddMethod("DetachDatabase", args =>
        {
            var (lib, dbHandle) = ParseDbArgs(args);
            var alias = args[2] as string ?? throw new ScriptException("Database alias required");
            var (_, pExec) = GetExecOrThrow(lib);
            if (pExec == nint.Zero) return false;
            try { ExecSql(pExec, dbHandle, $"DETACH DATABASE {alias};"); return true; }
            catch { return false; }
        });

        CurrentFrame.Scopes.Define("Sqlite", ns);
    }

    // ── DRY helpers ──

    private static (nint lib, IntPtr dbHandle) ParseDbArgs(object?[] args)
    {
        var lib = args[0] is DynamicLibrary dl ? dl.Handle : new IntPtr(Convert.ToInt64(args[0]));
        var dbHandle = new IntPtr(Convert.ToInt64(args[1]));
        return (lib, dbHandle);
    }

    private static void ApplyBulkPragmas(nint pExec, IntPtr dbHandle)
    {
        foreach (var pragma in SqlitePragmas.OptimizeForBulk)
            ExecSql(pExec, dbHandle, pragma);
    }

    private static void RestoreDefaultPragmas(nint pExec, IntPtr dbHandle)
    {
        foreach (var pragma in SqlitePragmas.RestoreDefaults)
            ExecSql(pExec, dbHandle, pragma);
    }

    private static (SqliteExports Exp, nint PExec) GetExecOrThrow(nint lib)
    {
        var exp = SqliteExports.GetOrLoad(lib);
        return exp.Exec == nint.Zero ? throw new ScriptException("sqlite3_exec not available") : (exp, exp.Exec);
    }

    private static void LoadExtensionsForDb(nint lib, IntPtr dbHandle, string firstExtension, string[] otherExtensions,
        bool enableExtensionOption = true)
    {
        // The caller ASKED for extension loading, so a refusal here is reported rather than swallowed: sqlite answers
        // SQLITE_MISUSE when the option is already on (harmless, this is the second call for that handle) and a real
        // failure otherwise. (CA1806 flagged this ignored result; the whole point of the call is its answer.)
        if (enableExtensionOption && SqliteNative.sqlite3_enable_load_extension(dbHandle, 1) is var enableRc and not 0 and not 21)
            throw new ScriptException($"SQLite refused to enable extension loading (error {enableRc})");

        var extensions = new List<string>(otherExtensions.Length + 1);
        if (!string.IsNullOrWhiteSpace(firstExtension))
            extensions.Add(firstExtension);
        extensions.AddRange(otherExtensions.Where(e => !string.IsNullOrWhiteSpace(e)));

        foreach (var ext in extensions)
        {
            var extBytes = Encoding.UTF8.GetBytes(ext + "\0");
            var rc = SqliteNative.sqlite3_load_extension(dbHandle, extBytes, null!, out var errMsg);
            if (rc == 0) continue;
            var err = errMsg != IntPtr.Zero ? Marshal.PtrToStringAnsi(errMsg) ?? "unknown error" : "unknown error";
            throw new ScriptException($"Failed to load extension '{ext}': {err}");
        }
    }

    /// <summary>
    /// Rewinds a prepared statement between rows of a bulk insert, clearing its bindings first.
    /// <para>
    /// sqlite's answer is deliberately NOT checked here, and that is a recorded decision rather than an oversight:
    /// the three loops that call this one ignore <c>sqlite3_step</c>'s result as well, so reporting a failed reset
    /// alone would name one failure while staying silent about the next one, which reads as "the rows went in".
    /// Giving that loop an error surface is its own round (it changes what a script sees on a failed bulk insert),
    /// and the curated CA1806 rule in AGENTS.md records it. Keeping the pair in ONE place means that round edits one
    /// method instead of six statements.
    /// </para>
    /// </summary>
    private static void RewindForRow(nint stmtPtr)
    {
#pragma warning disable CA1806 // see the summary above: the loop's error surface is its own round
        SqliteNative.sqlite3_reset(stmtPtr);
        SqliteNative.sqlite3_clear_bindings(stmtPtr);
#pragma warning restore CA1806
    }

    private static void ExecSql(nint pExec, IntPtr dbHandle, string sql)
    {
        var nativeSql = Marshal.StringToCoTaskMemUTF8(sql);
        var args = new long[] { dbHandle.ToInt64(), nativeSql, 0, 0, 0 };
        var rc = (long?)AotInvoker.InvokeCdeclPtr(pExec, args) ?? 0L;
        if (rc != 0)
        {
            Marshal.FreeCoTaskMem(nativeSql);
            throw new ScriptException("SQL error: " + sql[..Math.Min(80, sql.Length)]);
        }

        Marshal.FreeCoTaskMem(nativeSql);
    }

    /// <summary>Commonly used CSV delimiters for auto-detection.</summary>
    private static readonly char[] CsvDelimiters = [',', ';', '|', ':', '^', '~', '\t', '\x1F', '#', '@', '!', '%', '&', '*', '/', '\\', '–', '—', '¤', '\0'];

    /// <summary>Detects the most likely delimiter in a CSV line by scoring each candidate.
    /// Space is only chosen when no other delimiter scores at least 2 (avoids false positives).</summary>
    private static char DetectDelimiter(string line)
    {
        if (string.IsNullOrEmpty(line)) return ',';
        var best = ',';
        var bestScore = 0;
        var spaceScore = 0;
        foreach (var d in CsvDelimiters)
        {
            var count = 0;
            var inQ = false;
            foreach (var c in line)
            {
                if (c == '"') inQ = !inQ;
                else if (c == d && !inQ) count++;
            }
            if (d == ' ') spaceScore = count;
            else if (count > bestScore) { bestScore = count; best = d; }
        }
        // Only use space if no real delimiter had ≥2 occurrences
        if (bestScore >= 2) return best;
        return spaceScore >= 2 ? ' ' : ',';
    }

    /// <summary>Splits a CSV line into fields using the given delimiter, handling quoted fields.</summary>
    private static string[] SplitCsvLine(string line, char delimiter = ',')
    {
        if (string.IsNullOrEmpty(line)) return [];
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return [.. fields];
    }

    /// <summary>Escapes a SQL identifier (column/table name) for use in double-quoted identifiers.</summary>
    private static string EscapeSqlName(string name)
    {
        return name.Replace("\"", "\"\"");
    }

    /// <summary>Converts a script list or array value to object?[] for parameter binding.</summary>
    /// <summary>Wraps a raw CLR Array as an IScriptList for BulkInsert compatibility.</summary>
    private sealed class ListScriptList(IList list) : IScriptList
    {
        public void Sort(object?[] args, int line, int column) => ScriptListSorting.Sort(this, args, line, column);
        public int Count => list.Count;
        public TypeInfo ElementType => null!; // unused by BulkInsert
        public void Add(object? item, int line, int column) => throw new NotSupportedException();
        public void AddRaw(object item) => throw new NotSupportedException();
        public void Remove(object? item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public object? this[int index] { get => list[index]; set => throw new NotSupportedException(); }
        public Array ToArray() { var r = new object?[list.Count]; for (var i = 0; i < list.Count; i++) r[i] = list[i]; return r; }
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class ArrayScriptList(Array array) : IScriptList
    {
        public void Sort(object?[] args, int line, int column) => ScriptListSorting.Sort(this, args, line, column);
        public int Count => array.Length;
        public TypeInfo ElementType => null!; // unused by BulkInsert
        public void Add(object? item, int line, int column) => throw new NotSupportedException();
        public void AddRaw(object item) => throw new NotSupportedException();
        public void Remove(object? item) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
        public object? this[int index] { get => array.GetValue(index); set => throw new NotSupportedException(); }
        public Array ToArray() { var r = new object?[array.Length]; for (var i = 0; i < array.Length; i++) r[i] = array.GetValue(i); return r; }
        public void Clear() => throw new NotSupportedException();
    }

    private static IScriptList? AsScriptList(object? arg)
    {
        return arg switch
        {
            IScriptList list => list,
            Array arr => new ArrayScriptList(arr),
            IList list => new ListScriptList(list),
            _ => null
        };
    }

    private static object?[]? ConvertToObjectArray(object? arg)
    {
        switch (arg)
        {
            case null:
                return null;
            case object?[] arr:
                return arr;
            case IScriptList list:
                {
                    var result = new object?[list.Count];
                    for (var i = 0; i < list.Count; i++) result[i] = list[i];
                    return result;
                }
            case Array arrRaw:
                {
                    var result = new object?[arrRaw.Length];
                    for (var i = 0; i < arrRaw.Length; i++) result[i] = arrRaw.GetValue(i);
                    return result;
                }
            default:
                return [arg];
        }
    }
}

/// <summary>Wraps a SqliteIterator and maps each row to a typed ClassInstance by column name → field name.</summary>
internal sealed class SqliteMappingIterator(SqliteIterator inner, ClassInfo classInfo) : IEnumerable, IDisposable
{
    public void Dispose() => inner.Dispose(); // forward, owns the underlying iterator
    public override string ToString() => ScriptNameOf(typeof(SqliteMappingIterator)) ?? "SqliteMappingIterator";

    public IEnumerator GetEnumerator()
    {
        return (from ScriptBoxedDictionary row in inner select MapRowToInstance(row, classInfo)).GetEnumerator();
    }

    internal static ClassInstance MapRowToInstance(ScriptBoxedDictionary row, ClassInfo classInfo)
    {
        var fieldValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in classInfo.Fields)
        {
            object? val = null;
            var found = false;
            foreach (DictionaryEntry kv in row.Dictionary)
            {
                if (kv.Key is not string key || !key.Equals(field.Name, StringComparison.OrdinalIgnoreCase)) continue;
                val = kv.Value;
                found = true;
                break;
            }
            if (found)
            {
                try { fieldValues[field.Name] = CoerceTo(val, field.Type, 0, 0); }
                catch { fieldValues[field.Name] = DefaultFor(field.Type); }
            }
            else
            {
                fieldValues[field.Name] = DefaultFor(field.Type);
            }
        }

        return new ClassInstance(classInfo, fieldValues, new Dictionary<string, Interpreter.EventInfo>());
    }
}

/// <summary>SafeHandle with critical finalizer, calls sqlite3_finalize even if Dispose() is skipped.</summary>
internal sealed class SqliteStmtHandle : SafeHandle
{
    public SqliteStmtHandle() : base(IntPtr.Zero, true) { }
    public SqliteStmtHandle(IntPtr stmt) : base(IntPtr.Zero, true) { SetHandle(stmt); }
    public override bool IsInvalid => handle == IntPtr.Zero;
    protected override bool ReleaseHandle()
    {
        // `ReleaseHandle`'s answer IS sqlite's answer: SQLITE_OK means the statement really was finalized, and a
        // SafeHandle that reports success while the native call failed is exactly the leak this type exists to prevent.
        // (CA1806 flagged the ignored result here; the fix is to return it rather than to discard it.)
        return handle == IntPtr.Zero || SqliteNative.sqlite3_finalize(handle) == 0;
    }
}

/// <summary>SafeHandle with critical finalizer, guarantees sqlite3_close_v2 even if disposal is skipped.</summary>
internal sealed class SqliteDbHandle : SafeHandle
{
    public SqliteDbHandle(IntPtr db) : base(IntPtr.Zero, true) { SetHandle(db); }
    public override bool IsInvalid => handle == IntPtr.Zero;
    protected override bool ReleaseHandle()
    {
        // Same rule as the statement handle above: the database handle reports whether sqlite accepted the close.
        // `sqlite3_close_v2` normally answers SQLITE_OK, and it never answers SQLITE_BUSY for a handle it deferred (that
        // is the difference from `sqlite3_close`, which is why v2 is the one used here), so SQLITE_OK is the success test.
        return handle == IntPtr.Zero || SqliteNative.sqlite3_close_v2(handle) == 0;
    }
}

/// <summary>C# yield-return iterator for SQLite cursor.</summary>
internal sealed class SqliteIterator : IEnumerable, IDisposable
{
    internal static readonly IntPtr SqliteTransient = new(-1);
    public override string ToString() => ScriptNameOf(typeof(SqliteIterator)) ?? "SqliteIterator";

    private readonly SqliteStmtHandle _stmt;
    private readonly SqliteDbHandle? _dbHandle;
    private readonly IntPtr _db;
    private bool _done, _disposed;
    private readonly nint _pStep, _pColumnType, _pColumnInt, _pColumnDbl, _pColumnText, _pReset, _pFinalize, _pClose;
    private readonly nint _pColumnInt64, _pColumnBytes, _pColumnBlob;
    private readonly bool _useInt64;
    private readonly string[] _colNames;
    private readonly int _colCount;
    private readonly Dictionary<string, int> _nameIndex;
    private readonly bool _lazy;
    private int _rowVersion;

    private const int SQLITE_ROW = 100, SQLITE_DONE = 101;
    private const int SQLITE_INTEGER = 1, SQLITE_FLOAT = 2, SQLITE_TEXT = 3, SQLITE_BLOB = 4, SQLITE_NULL = 5;

    // Cached TypeInfo, same for every row, avoid per-row allocation
    private static readonly TypeInfo StringKeyType = new(TokenType.StringType, false, 0, 0, null);
    private static readonly TypeInfo ObjectValueType = new(TokenType.Object, true, 0, 0, null);

    /// <summary>Row key/value TypeInfo shared with <see cref="SqliteRow"/>.</summary>
    internal static TypeInfo RowKeyType => StringKeyType;
    internal static TypeInfo RowValueType => ObjectValueType;

    /// <summary>Bumped on every step and on cleanup, so a row captured at an earlier version is
    /// recognisably stale. This is the whole guard.</summary>
    internal int RowVersion => _rowVersion;

    /// <summary>The connection this cursor runs on, so a STEP failure can report SQLite's own sentence beside the
    /// code. A step failure is where a user-defined function's refusal arrives (this engine's `slc_*` family answers
    /// through <c>sqlite3_result_error</c>), so a bare `rc=1` threw away the only actionable half: measured,
    /// <c>select slc_avro_json('not bytes')</c> read "step error: rc=1" while SQLite was saying which argument was
    /// wrong.
    ///
    /// It is the RAW handle the caller opened, not `_dbHandle`: that one exists only when THIS iterator owns the
    /// database, so a cursor opened on a connection the script owns had nothing to ask and `sqlite3_errmsg(0)` answers
    /// "out of memory", which is worse than saying nothing (measured). Read only while the statement is live, which is
    /// exactly when this is asked.</summary>
    internal IntPtr DbHandle => _db;

    internal void InvalidateRow() => _rowVersion++;

    /// <summary>Reads column <paramref name="i"/> for a row yielded at <paramref name="version"/>.
    /// Throws once the cursor has stepped again or been closed, turning the use-after-free SQLite's
    /// column-pointer lifetime invites into a clear error.</summary>
    internal object? ReadColumnIfLive(int version, int i)
    {
        if (version != _rowVersion)
            throw new ScriptException(
                "This SQLite row is no longer live: the cursor has moved to another row or has been closed. "
                + "Copy the column inside the loop instead of keeping the row.");
        return ReadColumn(_stmt.DangerousGetHandle().ToInt64(), i);
    }

    /// <summary>The one column reader, shared by the eager fill and the lazy row so the two paths
    /// cannot drift.</summary>
    private object? ReadColumn(long stmt, int i)
    {
        var t = (int)C3(_pColumnType, stmt, i);
        return t switch
        {
            SQLITE_NULL => null,
            SQLITE_INTEGER => _useInt64
                ? NarrowInt(C3(_pColumnInt64, stmt, i))
                : (int)C3(_pColumnInt, stmt, i),
            SQLITE_FLOAT => CDbl(_pColumnDbl, stmt, i),
            SQLITE_TEXT => CStr(_pColumnText, stmt, i),
            SQLITE_BLOB when _pColumnBytes != nint.Zero && _pColumnBlob != nint.Zero => ReadBlob(stmt, i),
            _ => null
        };
    }

    private SqliteIterator(nint db, nint stmt, nint pStep, nint pColType, nint pColInt,
        nint pColDbl, nint pColText, nint pFinalize, nint pClose, nint pReset,
        nint pColInt64, nint pColBytes, nint pColBlob, string[] colNames,
        bool ownsDb = true, bool lazy = false)
    {
        _stmt = new SqliteStmtHandle(new IntPtr(stmt)); _dbHandle = ownsDb ? new SqliteDbHandle(new IntPtr(db)) : null;
        _db = new IntPtr(db);
        _colNames = colNames; _colCount = colNames.Length;
        _lazy = lazy;
        // One name → ordinal map per query, so a lazy row resolves a name by lookup rather than
        // by walking the column list on every access.
        _nameIndex = new Dictionary<string, int>(colNames.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < colNames.Length; i++) _nameIndex.TryAdd(colNames[i], i);
        _pStep = pStep; _pColumnType = pColType; _pColumnInt = pColInt;
        _pColumnDbl = pColDbl; _pColumnText = pColText;
        _pFinalize = pFinalize; _pClose = pClose; _pReset = pReset;
        _pColumnInt64 = pColInt64; _useInt64 = pColInt64 != nint.Zero;
        _pColumnBytes = pColBytes; _pColumnBlob = pColBlob;
    }

    // ── calli helpers ──
    private static unsafe long C2(nint fn, long a) => ((delegate* unmanaged[Cdecl]<long, long>)fn)(a);
    private static unsafe long C3(nint fn, long a, long b) => ((delegate* unmanaged[Cdecl]<long, long, long>)fn)(a, b);
    private static string CStr(nint fn, long a, long b) => Marshal.PtrToStringUTF8(new IntPtr(C3(fn, a, b))) ?? "";
    private static unsafe double CDbl(nint fn, long a, long b) => ((delegate* unmanaged[Cdecl]<long, long, double>)fn)(a, b);

    // ── BindParameters, type-dispatch bind of an object?[] to ?1..?N ──
    private static void BindParameters(nint lib, long stmt, object?[]? parameters)
    {
        if (parameters == null || parameters.Length == 0) return;

        var stmtPtr = new IntPtr(stmt);
        for (var i = 0; i < parameters.Length; i++)
        {
            var idx = i + 1;
            var val = parameters[i];

            var rc = SqliteBind.Bind(stmtPtr, idx, val);

            if (rc != 0) throw new ScriptException($"Parameter {idx} bind failed: rc={rc}");
        }
    }

    /// <summary>Wraps a native SQLite blob pointer in a MemoryManager for zero-copy access.</summary>

    /// <summary>Reads a BLOB column as a zero-copy ReadOnlyMemory&lt;byte&gt;.
    /// The pointer is valid until the next sqlite3_step() call on this statement.</summary>

    /// <summary>Reads a BLOB column as zero-copy ReadOnlyMemory&lt;byte&gt;.
    /// The pointer is valid only until the next sqlite3_step() call on this statement.
    /// Use this when consuming the blob immediately (e.g. serialization).</summary>
    private ReadOnlyMemory<byte> ReadBlobMemory(long stmt, int iCol)
    {
        var byteCount = (int)C3(_pColumnBytes, stmt, iCol);
        if (byteCount == 0) return default;
        var ptr = new IntPtr(C3(_pColumnBlob, stmt, iCol));
        return ptr == nint.Zero ? default(ReadOnlyMemory<byte>) : new SqliteBlobManager(ptr, byteCount).Memory;
    }

    /// <summary>Reads a BLOB column into a managed byte[] (safe copy for cursor yields).</summary>
    private byte[] ReadBlob(long stmt, int iCol) => ReadBlobMemory(stmt, iCol).ToArray();

    /// <summary>A path a SCRIPT named, resolved and put through the session's gate. A `:` spelling is one of SQLite's
    /// own in-memory names rather than a file, so it is not a path and is returned as it stands.</summary>
    internal static string SessionPath(string path)
        => string.IsNullOrEmpty(path) || path[0] == ':' ? path : SessionPolicy.Confine(Path.GetFullPath(path));

    internal static SqliteIterator Open(nint lib, string path, string sql, object?[]? parameters = null)
    {
        // THE SESSION'S PATH GATE, before the native open: a database path the SCRIPT named obeys the same rule a
        // `File` path does, so "deny: file" and "dirs" cannot be walked around with `Sqlite.Query`. An in-memory
        // database (":memory:" and the other `:name` spellings) is not a path and passes untouched.
        path = SessionPath(path);
        var exp = SqliteExports.GetOrLoad(lib);

        var dbVal = (long)0;
        int openRc;
        unsafe
        {
            var pf = (delegate* unmanaged[Cdecl]<byte*, long*, int>)exp.Open;
            var pb = Encoding.UTF8.GetBytes(path + "\0");
            fixed (byte* p = pb) { var d = &dbVal; openRc = pf(p, d); }
        }
        // The open is CHECKED, not assumed: a missing folder or a folder this account cannot write answers a handle
        // all the same, and the failure then surfaces at the PREPARE with "unable to open database file" and no path.
        // Measured: a read-only folder (an engine installed under Program Files) reported exactly that, which read as
        // a missing native rather than as a permission problem.
        if (openRc != 0)
        {
            var why = dbVal != 0 ? SqliteNative.ErrorText(new IntPtr(dbVal)) : "";
            if (dbVal != 0)
                unsafe
                {
                    var pc = (delegate* unmanaged[Cdecl]<long, int>)exp.Close;
                    pc(dbVal);
                }
            throw DatabaseOpenFailure(path, openRc, why);
        }

        var stmtVal = 0L;
        int prepareRc;
        unsafe
        {
            var pf = (delegate* unmanaged[Cdecl]<long, byte*, int, long*, long*, int>)exp.Prepare;
            var sb = Encoding.UTF8.GetBytes(sql);
            fixed (byte* s = sb) { var t = &stmtVal; prepareRc = pf(dbVal, s, -1, t, null); }
        }
        if (prepareRc != 0)
            throw PrepareFailure(new IntPtr(dbVal), prepareRc, sql, path);

        BindParameters(lib, stmtVal, parameters);

        var colCount = (int)C2(exp.ColumnCount, stmtVal);
        var colNames = new string[colCount];
        for (var i = 0; i < colCount; i++) colNames[i] = CStr(exp.ColumnName, stmtVal, i);

        return new SqliteIterator(new IntPtr(dbVal), new IntPtr(stmtVal),
            exp.Step, exp.ColumnType, exp.ColumnInt, exp.ColumnDouble, exp.ColumnText, exp.Finalize, exp.Close,
            exp.Reset, exp.ColumnInt64, exp.ColumnBytes, exp.ColumnBlob, colNames);
    }

    /// <summary>ONE statement of what a failed prepare says: the SQLite code, SQLITE'S OWN SENTENCE for it, then the SQL
    /// (capped). The sentence is the actionable half and it used to be missing, which made an arity refusal
    /// unreadable: SQLite answered <c>wrong number of arguments to function slc_map()</c> and the script saw only
    /// <c>rc=1</c>.</summary>
    private static ScriptException PrepareFailure(nint db, int rc, string sql, string? path = null)
        => new($"sqlite3_prepare_v2 failed: rc={rc}{SqliteNative.ErrorText(db)} for SQL: {sql.Substring(0, Math.Min(80, sql.Length))}"
               + (path is { Length: > 0 } ? $" on database '{path}'" : ""));

    /// <summary>ONE way to load the native, and a refusal that NAMES WHERE IT LOOKED. A packaged app in another folder is
    /// exactly the case that goes wrong: the engine finds its own folder (`ScriptSession.ResolveHome`, i.e. the DLL's),
    /// and if `sqlite3.dll` is not there the OS search may or may not find one, which used to surface as a bare
    /// <c>native library not found: it must sit beside the executable</c> with no path in it.</summary>
    internal static nint LoadSqliteLibrary()
    {
        try
        {
            return NativeLibrary.Load("sqlite3");
        }
        catch (DllNotFoundException)
        {
            var beside = "";
            try { beside = Path.Combine(ScriptSession.ResolveHome(), "sqlite3.dll"); } catch { }
            throw new ScriptException(
                "sqlite3.dll was not found: this engine looked " +
                (beside.Length > 0 ? $"beside itself ('{beside}') and " : "") +
                "on PATH. Copy sqlite3.dll beside the engine (the shipped layout does), or load it by its full path " +
                "with LoadLibrary(\"...\\sqlite3.dll\") first.");
        }
    }

    /// <summary>What a database that cannot be OPENED says. It names the file AND its folder, because the failure is
    /// almost always about the folder: a missing path, or one this account may not write (an engine installed under
    /// Program Files keeps its assets in such a folder). Measured before this existed: the same situation reported
    /// <c>sqlite3_prepare_v2 failed: rc=21: unable to open database file</c> with no path at all, at the first
    /// STATEMENT, which reads as a missing native and was reported as one.</summary>
    internal static ScriptException DatabaseOpenFailure(string path, int rc, string why)
    {
        var folder = "";
        try { folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? ""; } catch { }
        var where = folder.Length > 0 ? $" (folder '{folder}')" : "";
        return new ScriptException(
            $"cannot open the database '{path}'{where}: rc={rc}{why}. The folder must exist and this account must be " +
            "able to write to it; a database inside a protected folder such as Program Files cannot be created.");
    }


    /// <summary>Opens a cursor on an existing database connection.
    /// The caller owns the db handle, the iterator will NOT close it.</summary>
    internal static SqliteIterator OpenOnDb(nint lib, nint dbHandle, string sql, object?[]? parameters = null, bool lazy = false)
    {
        var exp = SqliteExports.GetOrLoad(lib);

        var stmtVal = (long)0;
        int prepareRc;
        unsafe
        {
            var pf = (delegate* unmanaged[Cdecl]<long, byte*, int, long*, long*, int>)exp.Prepare;
            var sb = Encoding.UTF8.GetBytes(sql);
            fixed (byte* s = sb) { var t = &stmtVal; prepareRc = pf(dbHandle.ToInt64(), s, -1, t, null); }
        }
        if (prepareRc != 0)
            throw PrepareFailure(new IntPtr(dbHandle), prepareRc, sql);

        BindParameters(lib, stmtVal, parameters);

        var colCount = (int)C2(exp.ColumnCount, stmtVal);
        var colNames = new string[colCount];
        for (var i = 0; i < colCount; i++) colNames[i] = CStr(exp.ColumnName, stmtVal, i);

        // pClose is nint.Zero, Cleanup will skip db close since _ownsDb=false
        return new SqliteIterator(dbHandle, new IntPtr(stmtVal),
            exp.Step, exp.ColumnType, exp.ColumnInt, exp.ColumnDouble, exp.ColumnText, exp.Finalize, nint.Zero,
            exp.Reset, exp.ColumnInt64, exp.ColumnBytes, exp.ColumnBlob, colNames, ownsDb: false, lazy: lazy);
    }

    private void Cleanup()
    {
        if (_done) return; _done = true;
        InvalidateRow(); // rows still held from a previous step are stale from here on

        if (_stmt is { IsInvalid: false })
        {
            if (_pReset != nint.Zero)
                C2(_pReset, _stmt.DangerousGetHandle().ToInt64());
            _stmt.Close(); // SafeHandle finalizer calls sqlite3_finalize
        }

        if (_dbHandle is not { IsInvalid: false }) return;
        _dbHandle.Close(); // SafeHandle releases via sqlite3_close_v2
    }

    /// <summary>Narrows a long value to int when it fits, avoiding int/long key lookup mismatch in script dictionaries.</summary>
    private static object NarrowInt(long val)
    {
        if (val is >= int.MinValue and <= int.MaxValue)
            return (int)val;

        return val;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Cleanup();
        _disposed = true;
        // Don't SuppressFinalize, SafeHandle finalizer is the safety net
    }

    public IEnumerator GetEnumerator() => new SqliteRowEnumerator(this, _lazy);

    private sealed class SqliteRowEnumerator : IEnumerator, IDisposable
    {
        private readonly SqliteIterator _owner;
        private readonly bool _lazy;
        private readonly ScriptBoxedDictionary? _row;
        private readonly Dictionary<object, object?>? _dict;
        private SqliteRow? _current;
        private bool _done;

        public SqliteRowEnumerator(SqliteIterator owner, bool lazy)
        {
            _owner = owner;
            _lazy = lazy;
            if (lazy) return; // the lazy path allocates one row per step, in MoveNext
            _row = new ScriptBoxedDictionary(StringKeyType, ObjectValueType, owner._colCount)
            {
                ColOrder = owner._colNames
            };
            _dict = (Dictionary<object, object?>)_row.Dictionary;
        }

        public object Current => _lazy ? _current! : _row!;

        public bool MoveNext()
        {
            if (_done) return false;
            var stmt = _owner._stmt.DangerousGetHandle().ToInt64();
            var rc = (int)C2(_owner._pStep, stmt);
            if (rc != SQLITE_ROW)
            {
                _done = true;
                _owner.InvalidateRow(); // a row held past the end of the cursor is stale too
                if (rc != SQLITE_DONE)
                    throw new ScriptException($"step error: rc={rc}{SqliteNative.ErrorText(_owner.DbHandle)}");
                return false;
            }

            // From here on the previous row's columns may no longer be read.
            _owner.InvalidateRow();

            if (_lazy)
            {
                _current = new SqliteRow(_owner, _owner.RowVersion, _owner._colNames, _owner._nameIndex);
                return true;
            }

            _dict!.Clear();
            _dict.TrimExcess();
            for (var i = 0; i < _owner._colCount; i++)
                _dict[_owner._colNames[i]] = _owner.ReadColumn(stmt, i);
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() => _owner.Dispose();
    }
}

/// <summary>Positional, lazily-materialised row yielded by <c>Sqlite.QueryLazy</c>.
///
/// Storage is positional (<c>object?[]</c> plus loaded flags) and a column is read and copied only
/// when the script asks for it, so a wide query that reads a few columns pays for those few.
/// Values are cached for the life of the row; once the cursor steps again or closes, every read
/// throws (see <see cref="SqliteIterator.ReadColumnIfLive"/>) instead of handing back data the
/// native statement has already moved out from under it.
///
/// It is a read-only <see cref="IScriptDictionary"/>, so <c>row[name]</c>, <c>row[i]</c>,
/// <c>row.Keys()</c>, <c>row.Values()</c>, <c>row.Count</c> and <c>foreach (var kv in row)</c> all
/// work through the engine's existing dispatch, and <c>Values()</c> is the positional read of the
/// whole row, for a caller that wants every column.</summary>
internal sealed class SqliteRow : IScriptDictionary, IReleaseNothing
{
    private const string ReadOnlyMessage =
        "A SQLite query row is read-only. Copy it into your own List or Dictionary if you need to modify it.";

    private readonly SqliteIterator _owner;
    private readonly int _version;
    private readonly string[] _colNames;
    private readonly Dictionary<string, int> _nameIndex;
    private readonly object?[] _values;
    private readonly bool[] _loaded;
    private System.Collections.IDictionary? _materialized;

    internal SqliteRow(SqliteIterator owner, int version, string[] colNames, Dictionary<string, int> nameIndex)
    {
        _owner = owner;
        _version = version;
        _colNames = colNames;
        _nameIndex = nameIndex;
        _values = new object?[colNames.Length];
        _loaded = new bool[colNames.Length];
    }

    public override string ToString() => ScriptNameOf(typeof(SqliteRow)) ?? "SqliteRow";

    /// <summary>Deliberate no-op, the row owns nothing, the cursor does.</summary>
    public void Dispose() { }

    /// <summary>Reads (and caches) one column. The one place a column is read from the cursor.</summary>
    private object? ReadAt(int index)
    {
        if ((uint)index >= (uint)_values.Length)
            throw new ScriptException($"Column index {index} is out of range: the row has {_values.Length} column(s).");
        if (_loaded[index]) return _values[index];
        var value = _owner.ReadColumnIfLive(_version, index);
        _values[index] = value;
        _loaded[index] = true;
        return value;
    }

    private int ResolveColumn(object key) => key switch
    {
        string name when _nameIndex.TryGetValue(name, out var ordinal) => ordinal,
        string name => throw new ScriptException($"Column '{name}' not found in the query result."),
        int ordinal => ordinal,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ => throw new ScriptException("Row key must be a column name (string) or an ordinal (int).")
    };

    // ── IScriptDictionary ──

    public int Count => _colNames.Length;
    public TypeInfo KeyType => SqliteIterator.RowKeyType;
    public TypeInfo ValueType => SqliteIterator.RowValueType;

    public object? this[object key]
    {
        get => ReadAt(ResolveColumn(key));
        set => throw new ScriptException(ReadOnlyMessage);
    }

    public object? GetValue(int index) => ReadAt(index);

    public object GetKey(int index) => (uint)index < (uint)_colNames.Length
        ? _colNames[index]
        : throw new ScriptException($"Column index {index} is out of range: the row has {_colNames.Length} column(s).");

    public bool ContainsKey(object key) => key switch
    {
        string name => _nameIndex.ContainsKey(name),
        int ordinal => (uint)ordinal < (uint)_colNames.Length,
        long l when l is >= int.MinValue and <= int.MaxValue => (uint)(int)l < (uint)_colNames.Length,
        _ => false
    };

    public object[] Keys()
    {
        var keys = new object[_colNames.Length];
        for (var i = 0; i < keys.Length; i++) keys[i] = _colNames[i];
        return keys;
    }

    /// <summary>The row's values, positionally, reads (and caches) every column on the first call.</summary>
    public object?[] Values()
    {
        var values = new object?[_values.Length];
        for (var i = 0; i < values.Length; i++) values[i] = ReadAt(i);
        return values;
    }

    /// <summary>The name-keyed view, built only if the script asks for it (foreach over the row,
    /// JSON export). Never touched on the lazy path.</summary>
    public System.Collections.IDictionary Dictionary
    {
        get
        {
            if (_materialized != null) return _materialized;
            var dict = new System.Collections.Generic.Dictionary<object, object?>(_colNames.Length, TupleValueComparer.Instance);
            for (var i = 0; i < _colNames.Length; i++) dict[_colNames[i]] = ReadAt(i);
            _materialized = dict;
            return dict;
        }
    }

    public void Add(object? key, object? value, int line, int column) => throw new ScriptException(ReadOnlyMessage);
    public void AddRaw(object key, object? value) => throw new ScriptException(ReadOnlyMessage);
    public void Remove(object key) => throw new ScriptException(ReadOnlyMessage);
    public void Clear() => throw new ScriptException(ReadOnlyMessage);
}
