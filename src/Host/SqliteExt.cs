using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ScriptLangCore;

/// <summary>THE THIRD DOOR: this engine loaded by SQLite as a loadable extension, so a database can call script
/// code from SQL.
///
/// The engine already has two shapes: <c>ScriptLangCore.exe</c> (self-contained) and <c>ScriptLangCore.dll</c> (loaded
/// by <c>ScriptLangCore.Launcher.exe</c>, or by any host through the <c>slc_*</c> C ABI). This file adds entry
/// points, under three names, and six SQL functions to the SAME <c>Embed</c> artifact, so nothing new is published and
/// no C toolchain joins the build:
///
///     .load ./ScriptLangCore init
///     .load ./ScriptLangCore init_sta          (same, and makes the calling thread the main STA so COM works)
///     SELECT slc_eval('a0 + a1', 2, 3);
///
/// THE SIX FUNCTIONS, and why six and not one:
///
///   slc_eval(code, a0, ...)         the run's ScriptResult, serialized with Json.Serialize. The contract a script
///                                   already knows, and the only one that can carry a failure.
///   slc_eval_scalar(code, a0)       just the value, as a native SQL value. The hot per-row shape: it skips the JSON
///                                   wrapper, and a failure becomes sqlite3_result_error because there is no envelope
///                                   to hold it.
///   slc_agg(code, value)            an AGGREGATE. Per row the code is evaluated with a0 = the running state (null on
///                                   the first row) and a1 = the row's value, and its answer becomes the new state;
///                                   the final state is the result. One function is enough to express any fold.
///   slc_map(code, rows)             a whole SET in one call, for a JSON array the caller already holds: the answer is
///                                   a JSON array of the values.
///   slc_map_agg(code, a0, ...)      the same answers with the rows coming from a query, collected as SQL values so
///                                   the set never becomes JSON text in the middle.
///   slc_file(path)                  loads a script FILE into this connection's interpreter, so a schema can carry
///                                   its logic: after it, slc_eval_scalar('Slug(a0)', title) calls the file's own
///                                   function with no further registration.
///   slc_avro_schema(blob)           the SCHEMA TEXT an Avro container carries in its own header, so a stored file
///                                   describes itself and a query can report what it holds without a second artifact.
///   slc_avro_json(blob)             the container's records as PURE JSON, DECOMPRESSED with the codec it declares,
///                                   which is what lets SQLite's own json_extract / json_each / json_group_array read
///                                   a stored Avro BLOB directly. `Avro.ContainerSchema` and `Avro.ContainerJson` are
///                                   the same two answers from a script, over the same code.
///
/// Arguments arrive as the globals <c>a0</c>, <c>a1</c>, ...: integer, real, text, blob and NULL map to the engine's
/// own values, so nothing is spliced into source text and a list or dictionary travels as JSON read with
/// <c>Json.Parse</c>:
///
///     SELECT slc_eval('a0.Length', 'hello');                   -- {"HasError":false,...,"ReturnValue":5}
///     SELECT slc_eval_scalar('a0 * 2', 21);                    -- 42, an integer
///     SELECT slc_agg('(a0 ?? 0) + a1', amount) FROM sales;      -- a script-defined SUM
///     SELECT slc_map('a0 * a0', '[1,2,3]');                    -- [1,4,9], a set in one call
///     SELECT slc_file('rules.slc'), slc_eval_scalar('Grade(a0)', score);
///     SELECT json_extract(slc_avro_json(data), '$[1].Name') FROM files;  -- one record's field, from a BLOB
///
/// WHY DIRECTONLY IS NOT OPTIONAL HERE: this language starts processes, reads files, drives COM and opens sockets, so
/// without it a hostile <c>.db</c> file could ship a view, trigger or generated column that runs script code the
/// moment a client opens it. Registered without <c>SQLITE_DETERMINISTIC</c> too, because an expression may read a
/// clock or a file and the planner must not assume otherwise.
///
/// THE ONE THING THAT MUST NOT BE TRUSTED: the position of each routine inside <c>struct sqlite3_api_routines</c>.
/// SQLite guarantees the struct is append-only, but a hand-copied index is still a memory corruption waiting to
/// happen, and this file's own first version proved it: its constants were derived by counting the header's members
/// with a text pass, and the load went straight to an access violation because index 72 is <c>libversion_number</c>
/// and index 66 is <c>libversion</c>, so a version string was read out of an integer. The indices below are therefore
/// taken from <c>offsetof</c> (the authoritative source), and every load RESOLVES each routine by NAME from the host's
/// own exports when the host exports them, refusing the load when the name and the index disagree. A host that links
/// SQLite statically exports nothing, and then the indices are all there is; that case is why the first call the load
/// makes is <c>libversion</c>, whose answer must read as a version or the load is refused instead of trusting the
/// table.
///
/// LIFETIME: one interpreter per CONNECTION, carried as the functions' <c>pApp</c> and released by
/// <c>sqlite3_create_function_v2</c>'s destructor when the connection closes. The destructor rides ONE of the six
/// registrations and not all of them, because SQLite runs it once per registration: six would dispose one session
/// six times. A host older than 3.31 has neither DIRECTONLY nor <c>create_function_v2</c>, so the load is REFUSED by
/// version rather than silently losing the flag.</summary>
internal static unsafe partial class SqliteExt
{
    // ---------------------------------------------------------------- SQLite vocabulary
    private const int SqliteOk = 0;
    private const int SqliteError = 1;

    private const int SqliteUtf8 = 1;
    private const int SqliteDeterministic = 0x00000800;
    private const int SqliteDirectOnly = 0x000080000;

    private const int TypeNull = 5, TypeInteger = 1, TypeFloat = 2, TypeText = 3, TypeBlob = 4;

    /// <summary>SQLITE_TRANSIENT: the callee copies the buffer before returning, so the managed array may be
    /// released, which is the only destructor a managed caller can offer.</summary>
    private static readonly nint SqliteTransient = -1;

    /// <summary>The oldest host this door works with: 3.31 introduced <c>SQLITE_DIRECTONLY</c>, and without it the
    /// registration would succeed while losing the guard, which is worse than a refused load.</summary>
    private const int MinMinorVersion = 31;

    // ---------------------------------------------------------------- the api table
    private enum Routine
    {
        AggregateContext,
        LibVersion,
        CreateFunction,
        CreateFunction2,
        Malloc,
        UserData,
        ResultBlob,
        ResultDouble,
        ResultError,
        ResultInt64,
        ResultNull,
        ResultText,
        ValueType,
        ValueInt64,
        ValueDouble,
        ValueText,
        ValueBlob,
        ValueBytes,
        Count,
    }

    /// <summary>EACH ROUTINE ONCE: its field index in <c>struct sqlite3_api_routines</c>, and the name this host
    /// exports it under when it exports anything at all. The index is the FALLBACK (a statically linked host), and a
    /// load refuses when the host's own symbol and the index disagree, which is what keeps a mis-derived index from
    /// reaching a call.
    ///
    /// DERIVED FROM THE COMPILER, NOT BY COUNTING THE HEADER, and that is a measurement rather than a style: counting
    /// the struct's members by eye or by text gets it WRONG, twice, for two different reasons. A member whose
    /// PARAMETERS are function pointers (<c>create_function</c> has three) looks like three extra members to a
    /// line-based pass, and a parenthesised comment throws off a depth-balancing pass. The authority is
    /// <c>offsetof</c>, and the command that regenerates this table is:
    ///
    ///     gcc -I tools/SqliteExt -o idx idx.c &amp;&amp; ./idx     // with the IDX(...) list below in idx.c
    ///
    /// running against <c>tools/SqliteExt/sqlite3ext.h</c>. Measured that way the struct holds 279 entries and these
    /// are their positions.</summary>
    private static readonly (Routine Id, string Name, int Index)[] Routines =
    [
        (Routine.AggregateContext, "sqlite3_aggregate_context",  0),
        (Routine.LibVersion,       "sqlite3_libversion",        66),
        (Routine.CreateFunction,   "sqlite3_create_function",   45),
        (Routine.CreateFunction2,  "sqlite3_create_function_v2", 162),
        (Routine.Malloc,           "sqlite3_malloc",            68),
        (Routine.UserData,         "sqlite3_user_data",         101),
        (Routine.ResultBlob,       "sqlite3_result_blob",       78),
        (Routine.ResultDouble,     "sqlite3_result_double",     79),
        (Routine.ResultError,      "sqlite3_result_error",      80),
        (Routine.ResultInt64,      "sqlite3_result_int64",      83),
        (Routine.ResultNull,       "sqlite3_result_null",       84),
        (Routine.ResultText,       "sqlite3_result_text",       85),
        (Routine.ValueType,        "sqlite3_value_type",        113),
        (Routine.ValueInt64,       "sqlite3_value_int64",       107),
        (Routine.ValueDouble,      "sqlite3_value_double",      105),
        (Routine.ValueText,        "sqlite3_value_text",        109),
        (Routine.ValueBlob,        "sqlite3_value_blob",        102),
        (Routine.ValueBytes,       "sqlite3_value_bytes",       103),
    ];

    private static readonly nint[] Resolved = new nint[(int)Routine.Count];

    /// <summary>The one index <see cref="Refuse"/> needs before the table has been resolved, so it is spelled once
    /// rather than as a literal at the refusal site.</summary>
    private const int MallocIndex = 68;

    private static nint Fn(Routine routine) => Resolved[(int)routine];

    // Finding the module that owns the table needs one Windows call: everything else is resolved through
    // NativeLibrary, whose TryGetExport already answers "this module has no such symbol" without a P/Invoke.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetModuleHandleExA(uint flags, nint moduleName, out nint module);

    private const uint HandleFromAddress = 0x00000004;
    private const uint HandleUnchangedRefCount = 0x00000002;

    /// <summary>Points every routine at a callable address, and answers the sentence that refuses the load, or null.
    /// A name the host EXPORTS is authoritative and is cross-checked against the table; a host that exports nothing
    /// (statically linked SQLite, which is what the <c>sqlite3.exe</c> on a build box usually is) is served from the
    /// table alone.</summary>
    private static string? Resolve(nint* api)
    {
        // The table's FIRST field is aggregate_context, a real function in the host's module, so the address of
        // entry 0 names that module without guessing its file name.
        var owner = nint.Zero;
        if (api[0] != nint.Zero && GetModuleHandleExA(HandleFromAddress | HandleUnchangedRefCount, (nint)api[0], out var module) != 0)
            owner = module;

        foreach (var (id, name, index) in Routines)
        {
            var fromTable = (nint)api[index];

            if (owner != nint.Zero && NativeLibrary.TryGetExport(owner, name, out var fromHost))
            {
                if (fromTable != nint.Zero && fromTable != fromHost)
                {
                    return $"the sqlite3_api_routines index for '{name}' is {index}, and this host's own '{name}' sits at " +
                           $"a different address (table 0x{fromTable:x}, host 0x{fromHost:x}). This table was derived from a " +
                           $"different sqlite3ext.h than the host's, so nothing may be called through it.";
                }

                Resolved[(int)id] = fromHost;
                continue;
            }

            if (fromTable == nint.Zero)
            {
                return $"the host neither exports '{name}' nor carries it at index {index} of its sqlite3_api_routines " +
                       "table, so this extension cannot run against it.";
            }

            Resolved[(int)id] = fromTable;
        }

        return null;
    }

    // ---------------------------------------------------------------- the entry point
    /// <summary>SQLite's loadable-extension entry point, exported under TWO names so either spelling works.
    ///
    /// <c>sqlite3_scriptlangcore_init</c> is the name <c>sqlite3_load_extension</c> DERIVES from the file: the
    /// basename, without its extension, without a leading <c>lib</c>, lowercased. It is what makes the bare
    /// <c>.load ./ScriptLangCore</c> work, so it has to exist.
    ///
    /// <c>init</c> is the short name a person types, and it exists because the guessing rule is easy to get wrong and
    /// nobody wants to type the long one: <c>.load ./ScriptLangCore init</c>. Both land on the same body, so they
    /// cannot drift.</summary>
    [UnmanagedCallersOnly(EntryPoint = "sqlite3_scriptlangcore_init", CallConvs = [typeof(CallConvCdecl)])]
    public static int Init(nint db, byte** ppErrMsg, nint* pApi) => InitCore(db, ppErrMsg, pApi);

    /// <summary>The same entry point under the short name: <c>.load ./ScriptLangCore init</c>.</summary>
    [UnmanagedCallersOnly(EntryPoint = "init", CallConvs = [typeof(CallConvCdecl)])]
    public static int InitShort(nint db, byte** ppErrMsg, nint* pApi) => InitCore(db, ppErrMsg, pApi);

    /// <summary>The same entry point for a host that has no STA thread of its own, so a script can drive COM:
    /// <c>.load ./ScriptLangCore init_sta</c>.
    ///
    /// A COM call needs an STA, and this engine refuses one that is not, so the plain <c>init</c> answers
    /// "COM Automation requires an STA thread" on the sqlite3 shell, whose own thread is MTA. That thread never
    /// <em>chose</em> MTA (Windows reports an implicit MTA for a thread that has not initialized COM), so it can
    /// still become the main STA, which is what this entry point does: it calls <c>OleInitialize</c> on the calling
    /// thread before the session exists. Choosing the name IS the opt-in, which is the only way a flag can be
    /// spelled here: sqlite fixes the entry point's signature, so there is no argument to carry one. What it costs
    /// is stated because it is the host's thread: after this, that thread is the main STA for the rest of its life
    /// (an MTA the host asks for later answers <c>RPC_E_CHANGED_MODE</c>).</summary>
    [UnmanagedCallersOnly(EntryPoint = "init_sta", CallConvs = [typeof(CallConvCdecl)])]
    public static int InitSta(nint db, byte** ppErrMsg, nint* pApi) => InitCore(db, ppErrMsg, pApi, requireSta: true);

    private static int InitCore(nint db, byte** ppErrMsg, nint* pApi, bool requireSta = false)
    {
        try
        {
            if (pApi == null)
                return Refuse(ppErrMsg, null, "the host passed no sqlite3_api_routines table");
            if (db == nint.Zero)
                return Refuse(ppErrMsg, pApi, "the host passed no database connection");

            if (requireSta && StaRefusal() is { } staRefusal)
                return Refuse(ppErrMsg, pApi, staRefusal);

            if (Resolve(pApi) is { } refusal)
                return Refuse(ppErrMsg, pApi, refusal);
            var libVersion = (delegate* unmanaged[Cdecl]<nint>)Fn(Routine.LibVersion);
            var version = Marshal.PtrToStringUTF8((nint)libVersion()) ?? "";
            if (VersionRefusal(version) is { } tooOld)
                return Refuse(ppErrMsg, pApi, tooOld);

            var session = new EvalSession();
            var handle = GCHandle.Alloc(session);
            var rc = Register(db, (void*)GCHandle.ToIntPtr(handle));
            if (rc != SqliteOk)
            {
                handle.Free();
                session.Dispose();
                return rc;
            }

            return SqliteOk;
        }
        catch (Exception ex)
        {
            return Refuse(ppErrMsg, pApi, FriendlyExceptionMessage(ex));
        }
    }

    /// <summary>
    /// Makes the CALLING THREAD the main STA for <c>init_sta</c>, and answers why it could not.
    ///
    /// <c>OleInitialize</c> is a superset of <c>CoInitializeEx(COINIT_APARTMENTTHREADED)</c>, so on a thread that
    /// has not initialized COM it makes that thread the main STA. Measured on the sqlite3 shell: the shell's thread
    /// reports an IMPLICIT MTA apartment (<c>CoGetApartmentType</c> answers type 1 with qualifier 1), and
    /// <c>OleInitialize</c> answers S_OK there and the apartment becomes the main STA (type 3). A thread whose host
    /// CHOSE MTA cannot be changed, and <c>OleInitialize</c> itself says so with <c>RPC_E_CHANGED_MODE</c>.
    /// </summary>
    private static string? StaRefusal()
    {
        var hr = DispatchVtbl.NativeMethods.OleInitialize(IntPtr.Zero);
        if (hr >= 0)
            return null;

        return hr == DispatchVtbl.ComConst.RPC_E_CHANGED_MODE
            ? "this thread was initialized as MTA by its host, so it cannot become the STA a COM call needs: " +
              "load with the plain init entry point when the script needs no COM, or run it on a thread the host " +
              "created as STA"
            : "OleInitialize failed with HRESULT " + hr;
    }

    /// <summary>THE SIX REGISTRATIONS, each declaring the arity it actually has. That number is not decoration: it is
    /// what <c>pragma_function_list</c> reports to a reader, and SQLite refuses a call with the wrong count BEFORE this
    /// file's code runs, which is what closed a measured hole (an extra argument used to be accepted and silently
    /// ignored: <c>slc_agg('(a0 ?? 0) + a1', 5, 999)</c> answered 5, and <c>slc_map('a0', '[1,2]', 'x')</c> answered
    /// [1,2]). Three shapes are FIXED (<c>slc_file</c> takes a path, <c>slc_map</c> the code plus its JSON rows,
    /// <c>slc_agg</c> the code plus the row's value, <c>slc_eval_scalar</c> the code plus one value) and two are
    /// genuinely VARIADIC (<c>slc_eval</c> reads a0..aN, <c>slc_map_agg</c> sees a0..aN of every row), so those two keep
    /// -1 and their own refusal for a call with no arguments at all.
    /// <c>xDestroy</c> travels on the LAST one only: SQLite runs a registration's destructor once when the connection
    /// closes, so four copies of it would dispose one session four times.</summary>
    private static int Register(nint db, void* pApp)
    {
        var flags = SqliteUtf8 | SqliteDirectOnly;
        var xDestroy = (delegate* unmanaged[Cdecl]<void*, void>)&DestroySession;

        var rc = Add(db, "slc_eval", -1, flags, pApp, &EvalCallback, null, null, null);
        if (rc != SqliteOk) return rc;

        rc = Add(db, "slc_eval_scalar", 2, flags, pApp, &ScalarCallback, null, null, null);
        if (rc != SqliteOk) return rc;

        rc = Add(db, "slc_file", 1, flags, pApp, &FileCallback, null, null, null);
        if (rc != SqliteOk) return rc;

        rc = Add(db, "slc_avro_schema", 1, flags, pApp, &AvroSchemaCallback, null, null, null);
        if (rc != SqliteOk) return rc;

        rc = Add(db, "slc_avro_json", 1, flags, pApp, &AvroJsonCallback, null, null, null);
        if (rc != SqliteOk) return rc;

        rc = Add(db, "slc_map", 2, flags, pApp, &MapCallback, null, null, null);
        if (rc != SqliteOk) return rc;

        rc = Add(db, "slc_agg", 2, flags, pApp, null, &AggStepCallback, &AggFinalCallback, null);
        if (rc != SqliteOk) return rc;

        // The LAST registration carries the destructor: SQLite runs xDestroy once per registration, so putting it on
        // every one of them would free the session on the first. Keep this the last Add in the method.
        return Add(db, "slc_map_agg", -1, flags, pApp, null, &MapStepCallback, &MapFinalCallback, xDestroy);
    }

    private static int Add(nint db, string name, int nArg, int flags, void* pApp,
                           delegate* unmanaged[Cdecl]<nint, int, nint*, void> xFunc,
                           delegate* unmanaged[Cdecl]<nint, int, nint*, void> xStep,
                           delegate* unmanaged[Cdecl]<nint, void> xFinal,
                           delegate* unmanaged[Cdecl]<void*, void> xDestroy)
    {
        // A host without create_function_v2 (older than 3.7.3) cannot say when the connection closes. The session
        // then lives until the process ends, which is what such a host gets for skipping the destructor.
        var withDestroy = xDestroy != null && Fn(Routine.CreateFunction2) != nint.Zero;
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");

        unsafe
        {
            fixed (byte* pName = nameBytes)
            {
                if (withDestroy)
                {
                    var create = (delegate* unmanaged[Cdecl]<nint, byte*, int, int, void*, void*, void*, void*, void*, int>)Fn(Routine.CreateFunction2);
                    return create(db, pName, nArg, flags, pApp, xFunc, xStep, xFinal, xDestroy);
                }

                var createV1 = (delegate* unmanaged[Cdecl]<nint, byte*, int, int, void*, void*, void*, void*, int>)Fn(Routine.CreateFunction);
                return createV1(db, pName, nArg, flags, pApp, xFunc, xStep, xFinal);
            }
        }
    }

    // ---------------------------------------------------------------- the scalar function
    /// <summary>The JSON one: the run's <c>ScriptResult</c>, serialized. Nothing here may throw, because an exception
    /// leaving a native callback ends the host process, so every failure becomes either a Result in JSON or
    /// <c>sqlite3_result_error</c>.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void EvalCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            if (SessionFor(ctx) is not { } session)
            {
                ResultError(ctx, "slc_eval lost its session: this connection registered the function without one");
                return;
            }

            if (!CodeOk(session, ctx, argc, argv, "slc_eval"))
                return;

            ResultText(ctx, Interpreter.SerializeToText(session.EvaluateCode(argv[0], argc - 1, argv + 1)));
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>The hot per-row one: the VALUE, as a native SQL value, with no envelope. A failing run is a SQL error
    /// here, since a scalar answer has nowhere to put the failure.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void ScalarCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            if (SessionFor(ctx) is not { } session)
            {
                ResultError(ctx, "slc_eval_scalar lost its session: this connection registered the function without one");
                return;
            }

            if (!CodeOk(session, ctx, argc, argv, "slc_eval_scalar"))
                return;

            var result = session.EvaluateCode(argv[0], argc - 1, argv + 1);
            if (result.HasError)
            {
                ResultError(ctx, Describe(result));
                return;
            }

            ResultValue(ctx, result.ReturnValue);
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>The aggregate: per row the code is evaluated with <c>a0</c> the running state and <c>a1</c> the row's
    /// value, and its answer becomes the new state. One function folds anything a script can express, which is why
    /// there is no min/max/sum family to keep in step.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AggStepCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            if (SessionFor(ctx) is not { } session)
            {
                ResultError(ctx, "slc_agg lost its session: this connection registered the function without one");
                return;
            }

            if (!CodeOk(session, ctx, argc, argv, "slc_agg"))
                return;

            // A fold with no value never sees a row, so it can only answer the state it started from: the same
            // "shape that can only be a mistake" rule as slc_map's missing second argument.
            if (argc < 2)
            {
                ResultError(ctx, "slc_agg takes the code, then the row's value: slc_agg('(a0 ?? 0) + a1', n)");
                return;
            }

            var state = StateOf(ctx, create: true);
            if (state == null)
            {
                ResultError(ctx, "slc_agg could not reach its per-aggregate state");
                return;
            }

            // a0 is the running state, a1 is this row: the two names the expression reads, so a fold is written as
            // an ordinary expression rather than as a callback protocol.
            var result = session.EvaluateFoldCode(argv[0], state.Value, argv + 1, Math.Max(0, argc - 1));
            if (result.HasError) { state.Error = Describe(result); return; }

            state.Value = result.ReturnValue;
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AggFinalCallback(nint ctx)
    {
        try
        {
            var state = StateOf(ctx, create: false);
            if (state == null)
            {
                ResultNull(ctx);
                return;
            }

            if (state.Error != null)
            {
                ResultError(ctx, state.Error);
                return;
            }

            ResultValue(ctx, state.Value);
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>Loads a script FILE into this connection's interpreter and answers the run as JSON. After it, the
    /// file's own functions are callable from the other three, which is what lets a schema carry its logic instead of
    /// carrying one expression.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void FileCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            if (SessionFor(ctx) is not { } session)
            {
                ResultError(ctx, "slc_file lost its session: this connection registered the function without one");
                return;
            }

            if (PathArgOf(session, ctx, argc, argv) is not { } path)
                return;

            ResultText(ctx, Interpreter.SerializeToText(session.LoadFile(path)));
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>The two doors onto a STORED Avro container, which is what makes a `.avro` file usable as a SQL value
    /// at all: <c>slc_avro_schema(blob)</c> answers the schema TEXT the file carries in its own header, and
    /// <c>slc_avro_json(blob)</c> answers its records as PURE JSON, so `json_extract`, `json_each` and
    /// `json_group_array` read a stored container directly. Both come from the engine's own reader
    /// (<see cref="AvroContainer.SchemaText"/> and <see cref="AvroContainer.ToJson"/>), which is exactly what
    /// `Avro.ContainerSchema` and `Avro.ContainerJson` answer from a script, so a query and a script cannot disagree
    /// about the same bytes.
    ///
    /// The CODEC is the file's own, so the JSON is DECOMPRESSED with it (null, deflate, snappy, zstandard, gzip,
    /// brotli) and an unsupported one is refused by name rather than yielding JSON from a payload nobody expanded.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AvroSchemaCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            var (ok, data) = AvroBlobArg(ctx, argc, argv, "slc_avro_schema");
            if (!ok) return;
            if (data is null)
            {
                ResultNull(ctx);
                return;
            }
            ResultText(ctx, AvroContainer.SchemaText(data, "slc_avro_schema"));
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void AvroJsonCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            var (ok, data) = AvroBlobArg(ctx, argc, argv, "slc_avro_json");
            if (!ok) return;
            if (data is null)
            {
                ResultNull(ctx);
                return;
            }
            ResultText(ctx, AvroContainer.ToJson(data, "slc_avro_json"));
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>The ONE argument those two take: an Avro container's BYTES. `Ok` false means an error has already been
    /// reported, so the caller must not write a result; `Ok` true with `Data` null is a SQL NULL, which is a NULL
    /// answer (the rule the code argument follows everywhere in this file).
    ///
    /// A TEXT argument is REFUSED by name rather than decoded, because a container is binary: a text cast has already
    /// changed every byte above 0x7F, so the only honest answer is to say what to store instead.</summary>
    private static (bool Ok, byte[]? Data) AvroBlobArg(nint ctx, int argc, nint* argv, string name)
    {
        if (argc < 1)
        {
            ResultError(ctx, $"{name} takes the bytes of an Avro container as its one argument: the array "
                + "Avro.WriteContainer answers, stored in a BLOB column");
            return (false, null);
        }

        var type = ValueTypeOf(argv[0]);
        if (type == TypeNull) return (true, null);
        if (type != TypeBlob)
        {
            ResultError(ctx, $"{name} reads an Avro container's BYTES and this argument is {SqlTypeName(type)}: a "
                + "container is binary, so store it with File.ReadAllBytes and bind it as a BLOB");
            return (false, null);
        }
        return (true, BlobOf(argv[0]));
    }

    /// <summary>One BLOB value's bytes, as a COPY: SQLite owns the buffer and may reuse it the moment the call
    /// returns, which is the same reason <c>sqlite3_result_text</c> is registered with SQLITE_TRANSIENT.</summary>
    private static byte[] BlobOf(nint value)
    {
        var blob = (byte*)((delegate* unmanaged[Cdecl]<nint, nint>)Fn(Routine.ValueBlob))(value);
        var bytes = ValueBytesOf(value);
        return blob == null || bytes <= 0 ? Array.Empty<byte>() : new ReadOnlySpan<byte>(blob, bytes).ToArray();
    }

    /// <summary>What a SQL value IS, in the words a refusal uses, so a reader is told what arrived rather than which
    /// number the SQLite header calls the type.</summary>
    private static string SqlTypeName(int type) => type switch
    {
        TypeNull => "NULL",
        TypeInteger => "an integer",
        TypeFloat => "a real number",
        TypeText => "text",
        TypeBlob => "a BLOB",
        _ => "another type"
    };

    /// <summary>The SET-at-a-time one: many rows in, many answers out, in ONE call. This is the shape a transform over
    /// a large set wants, because the per-statement cost is two JSON operations and the per-row cost is a call.
    ///
    /// <c>slc_map(code, rows)</c> takes a JSON array of rows and answers a JSON array of values. An element that is
    /// itself an array supplies the arguments in order (<c>[[1,2],[3,4]]</c> for a code reading <c>a0</c> and
    /// <c>a1</c>), and any other element supplies <c>a0</c> alone, so <c>json_group_array(n)</c> can be handed
    /// straight in. A row that fails raises a SQL error naming its position, because an array of mixed shapes would
    /// make the caller sort successes from failures on every use.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MapCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            if (SessionFor(ctx) is not { } session)
            {
                ResultError(ctx, "slc_map lost its session: this connection registered the function without one");
                return;
            }

            if (!CodeOk(session, ctx, argc, argv, "slc_map"))
                return;

            // The set is the SECOND argument, and reading past the end of argv on a one-argument call is
            // undefined behaviour that happens to answer a plausible empty set: measured, `slc_map('a0')` answered
            // `[]`. A shape that can only be a mistake is refused by name instead.
            if (argc < 2)
            {
                ResultError(ctx, "slc_map takes the code, then a JSON array of rows: " +
                                 "slc_map('a0 * 2', '[1,2,3]')");
                return;
            }

            // A JSONB value is a BLOB, so it is not text this can read: say which wrapper fixes it rather than
            // letting the JSON reader report a byte offset inside a binary format.
            if (ValueTypeOf(argv[1]) == TypeBlob)
            {
                ResultError(ctx, "slc_map reads JSON TEXT as its second argument and this one is a BLOB, which is a " +
                                 "JSONB value: wrap it, as slc_map(code, json(rows))");
                return;
            }

            var rows = ReadText(argv[1]);
            if (rows == null)
            {
                // No set is an EMPTY set, which is what json_group_array answers over no rows and what json_each
                // reads happily. A NULL here is not a mistake worth failing a statement over.
                ResultText(ctx, "[]");
                return;
            }

            var result = session.Map(session.ReadCode(argv[0]) ?? "", rows);
            if (result.HasError)
            {
                ResultError(ctx, Describe(result));
                return;
            }

            ResultText(ctx, (string)result.ReturnValue!);
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>The SET form's aggregate: the same answers <c>slc_map</c> gives, with the rows arriving one at a time
    /// instead of inside a JSON array. It exists because the set door had to be handed a TEXT array, and building
    /// that text is what SQLite charges most for: measured over a hundred thousand rows, <c>json_group_array</c> plus
    /// <c>json_each</c> cost about seventy milliseconds on their own. Here the values are collected as SQL values and
    /// only the ANSWER becomes text.
    ///
    /// <c>slc_map_agg(code, a0, a1, ...)</c> takes its arguments the way <c>slc_eval</c> does, so a two-argument code
    /// is written <c>slc_map_agg('a0 + a1', a, b)</c> and it reads the same expression the scalar form would per row.
    /// Nothing is evaluated until the aggregate ends, so a failing row is reported once, with its index, instead of
    /// aborting a step; the values are collected FLAT, which is one list append per value and no allocation per row.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MapStepCallback(nint ctx, int argc, nint* argv)
    {
        try
        {
            if (argc < 2)
            {
                // Same rule as slc_agg's missing value: a map with no row has nothing to map.
                var guard = StateOf(ctx, create: true);
                if (guard != null) guard.Error = "slc_map_agg takes the code, then the row's values: slc_map_agg('a0 * 2', n)";
                return;
            }

            var state = StateOf(ctx, create: true);
            if (state == null) return;

            // The code is read once per row and kept on the state, because xFinal is the only place that runs it.
            state.Code ??= ReadText(argv[0]);
            state.Arity = argc - 1;
            state.Flat ??= [];

            for (var i = 1; i < argc; i++) state.Flat.Add(ReadValue(argv[i]));
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MapFinalCallback(nint ctx)
    {
        try
        {
            var state = StateOf(ctx, create: false);
            if (state == null)
            {
                // No rows at all: an empty aggregate context, which is an empty set.
                ResultText(ctx, "[]");
                return;
            }

            // The error comes BEFORE the empty-set arms: a step that recorded a refusal also leaves no arity, and a
            // misuse would otherwise answer `[]` (measured: `slc_map_agg('a0')` did exactly that).
            if (state.Error != null)
            {
                ResultError(ctx, state.Error);
                return;
            }

            if (state.Flat == null || state.Arity <= 0)
            {
                ResultText(ctx, "[]");
                return;
            }

            if (SessionFor(ctx) is not { } session)
            {
                ResultError(ctx, "slc_map_agg lost its session: this connection registered the function without one");
                return;
            }

            var result = session.MapCollected(state.Code ?? "", state.Arity, state.Flat);
            if (result.HasError)
            {
                ResultError(ctx, Describe(result));
                return;
            }

            ResultText(ctx, (string)result.ReturnValue!);
        }
        catch (Exception ex)
        {
            Guard(ctx, ex);
        }
    }

    /// <summary>The two questions the code argument asks, so the callbacks that keep the code in its SQL value (the
    /// per-row ones, which need the memo) and the one that wants the text answer them the same way: a missing first
    /// argument is a SQL error naming the usage, and a NULL code is a NULL answer.
    ///
    /// THE MISSING-ARGUMENT BRANCH IS NOW ONLY REACHABLE FOR THE TWO VARIADIC FUNCTIONS (<c>slc_eval</c> and
    /// <c>slc_map_agg</c>): the four with a fixed arity are refused by SQLite itself, which is a better door because it
    /// also refuses an argument COUNT that is too high. It stays because a refusal nobody can reach is cheaper than a
    /// crash if a host ever registers these differently.</summary>
    private static bool CodeOk(EvalSession session, nint ctx, int argc, nint* argv, string member)
    {
        if (argc < 1)
        {
            ResultError(ctx, $"{member} takes the code to evaluate as its first argument, then the arguments that code reads as a0, a1, ...");
            return false;
        }

        if (session.ReadCode(argv[0]) != null) return true;

        ResultNull(ctx);
        return false;
    }

    /// <summary>slc_file's ONE argument is a PATH, not code, so its refusal says so: the code-shaped sentence a reader
    /// used to get here ("takes the code to evaluate as its first argument") described a member that does not take
    /// code at all. A NULL path is a NULL answer, the same rule the code argument follows.</summary>
    private static string? PathArgOf(EvalSession session, nint ctx, int argc, nint* argv)
    {
        if (argc < 1)
        {
            ResultError(ctx, "slc_file takes the path of a .slc file to run, as in slc_file('assets/language/demo.slc')");
            return null;
        }

        var path = ReadText(argv[0]);
        if (path != null) return path;
        ResultNull(ctx);
        return null;
    }

    /// <summary>One door for a failure raised by this file's own code, so nothing can leave a native callback.</summary>
    private static void Guard(nint ctx, Exception ex)
    {
        try { ResultError(ctx, FriendlyExceptionMessage(ex)); }
        catch { /* a result that cannot be written is the host's problem, not a reason to end it */ }
    }

    /// <summary>The interpreter a connection owns, or null when <c>pApp</c> is not ours.</summary>
    private static EvalSession? SessionFor(nint ctx)
    {
        var pApp = ((delegate* unmanaged[Cdecl]<nint, nint>)Fn(Routine.UserData))(ctx);
        if (pApp == nint.Zero) return null;
        return GCHandle.FromIntPtr(pApp).Target as EvalSession;
    }

    /// <summary>The per-aggregate state, kept in SQLite's own aggregate context so it dies with the aggregate and not
    /// with the process. <c>create: false</c> is the final call, which must not allocate a state for an aggregate
    /// that never saw a row.</summary>
    private static AggregateState? StateOf(nint ctx, bool create)
    {
        var slot = ((delegate* unmanaged[Cdecl]<nint, int, void*>)Fn(Routine.AggregateContext))(ctx, create ? IntPtr.Size : 0);
        if (slot == null) return null;

        var stored = *(nint*)slot;
        if (stored == nint.Zero)
        {
            if (!create) return null;
            var fresh = new AggregateState();
            stored = GCHandle.ToIntPtr(GCHandle.Alloc(fresh));
            *(nint*)slot = stored;
            return fresh;
        }

        return (AggregateState)GCHandle.FromIntPtr(stored).Target!;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DestroySession(void* pApp)
    {
        if (pApp == null) return;

        try
        {
            var handle = GCHandle.FromIntPtr((nint)pApp);
            if (handle.Target is EvalSession session) session.Dispose();
            handle.Free();
        }
        catch { /* a connection closing is not a place to report anything */ }
    }

    // ---------------------------------------------------------------- one interpreter per connection
    /// <summary>Everything one connection owns: its interpreter, and the code it has already parsed. A run's
    /// <c>print</c> is DISCARDED (<see cref="Interpreter.Out"/>), because a SQL function's answer is its result and a
    /// host's console has no line for a script to land on; the engine's own log still goes to the host's stderr,
    /// which is where a diagnostic belongs.</summary>
    /// <summary>Everything one connection owns: its interpreter, and the code it has already turned into a callable.
    /// A run's <c>print</c> is DISCARDED (<see cref="Interpreter.Out"/>), because a SQL function's answer is its
    /// result and a host's console has no line for a script to land on; the engine's own log still goes to the host's
    /// stderr, which is where a diagnostic belongs.</summary>
    private sealed class EvalSession : IDisposable
    {
        private readonly Interpreter _itp;
        private readonly Dictionary<string, Prepared> _prepared = new(StringComparer.Ordinal);
        private int _depth;

        /// <summary>A re-entrant call means the evaluated code ran SQL that called one of these functions again on
        /// the same connection. Legitimate for a scalar subquery, so the guard is a depth rather than a refusal on the
        /// first one.</summary>
        private const int MaxDepth = 16;

        internal EvalSession()
        {
            _itp = new Interpreter(caseSensitive: true) { Out = TextWriter.Null };
        }

        // ------------------------------------------------------------------ one call, three doors into it

        /// <summary>The per-row entry from a native argument list, the shape a scalar function callback has.</summary>
        internal ScriptResult Evaluate(string code, int count, nint* values, object? state = null, bool fold = false)
            => Invoke(code, ArgsOf(count, values, state, fold));

        /// <summary>The same, with the code still in the SQL value the host passed, so it need not be decoded again
        /// while its bytes have not changed.</summary>
        internal ScriptResult EvaluateCode(nint codeValue, int count, nint* values, object? state = null, bool fold = false)
            => InvokeCode(codeValue, ArgsOf(count, values, state, fold));

        private static object?[] ArgsOf(int count, nint* values, object? state, bool fold)
        {
            var arity = count + (fold ? 1 : 0);
            var args = new object?[arity];
            var at = 0;
            if (fold) args[at++] = state;
            for (var i = 0; i < count; i++) args[at++] = ReadValue(values[i]);
            return args;
        }

        /// <summary>The aggregate's step, which puts the running state in <c>a0</c> and the row in <c>a1</c>.</summary>
        internal ScriptResult EvaluateFold(string code, object? state, nint* values, int count)
            => Evaluate(code, count, values, state, fold: true);

        internal ScriptResult EvaluateFoldCode(nint codeValue, object? state, nint* values, int count)
            => EvaluateCode(codeValue, count, values, state, fold: true);

        /// <summary>ONE call of the callable, which is the only implementation of "run the code once" this file has:
        /// the per-row entries hand it their arguments and the batch hands it one row's worth at a time. The depth
        /// guard lives here so a batch re-entering itself is bounded the same way a single row is.</summary>
        internal ScriptResult Invoke(string code, object?[] args) => InvokePrepared(Prepare(code, args.Length), args);

        /// <summary>The per-row door: the code arrives as the SQLITE VALUE the host passed, and a query hands the SAME
        /// value on every row (a literal is one Mem), so the UTF-8 decode and the cache key are skipped while the bytes
        /// have not changed. That guard is a byte compare rather than the pointer alone, because a BOUND parameter is
        /// one Mem whose bytes change per row.</summary>
        internal ScriptResult InvokeCode(nint codeValue, object?[] args)
            => InvokePrepared(PreparedFor(codeValue, args.Length), args);

        private ScriptResult InvokePrepared(Prepared prepared, object?[] args)
        {
            if (prepared.Refusal is { } refusal) return refusal;

            if (++_depth > MaxDepth)
            {
                _depth--;
                throw new ScriptException(
                    $"this function re-entered itself more than {MaxDepth} times on one connection: an expression that runs SQL " +
                    "which calls it again is allowed, but the nesting has to end.");
            }

            try
            {
                try
                {
                    // A CALLER'S `exit()` MUST NOT BECOME THE NEXT RUN'S VERDICT: `slc_eval('exit(3)')` sets the ambient
                    // `Environment.ExitCode`, and a source run on this connection reports that ambient value as its own,
                    // so the eval's code is isolated here the way a run's is.
                    return new ScriptResult
                    {
                        ReturnValue = ScriptRunner.InProcessState(
                            () => _itp.InvokeScriptFunction(prepared.Function!, args, 0, 0)),
                    };
                }
                catch (ScriptException ex)
                {
                    return new ScriptResult
                    {
                        HasError = true,
                        ErrorMessage = ex.Message,
                        ErrorLine = ex.Line,
                        ErrorColumn = ex.Column,
                    };
                }
            }
            finally
            {
                _depth--;
            }
        }

        // ------------------------------------------------------------------ the code text, and its memo

        private nint _memoPtr;
        private int _memoLen = -1;
        private byte[]? _memoBytes;
        private string? _memoCode;
        private int _memoArity = -1;
        private string? _memoText;
        private Prepared? _memoPrepared;

        private const string EmptyCode = "the code to evaluate is NULL";

        /// <summary>The code text of one SQL value, decoded only when it differs from the last one read.
        /// <c>value_text</c> answers a pointer and <c>value_bytes</c> the count that belongs to it, which is the pair
        /// compared here.</summary>
        internal string? ReadCode(nint codeValue)
        {
            if (ValueTypeOf(codeValue) == TypeNull) return null;

            var text = (byte*)((delegate* unmanaged[Cdecl]<nint, nint>)Fn(Routine.ValueText))(codeValue);
            if (text == null) return null;

            var length = ValueBytesOf(codeValue);
            if (_memoPtr == (nint)text && _memoLen == length && SameBytes(text, length, _memoBytes))
                return _memoCode;

            var decoded = length <= 0 ? "" : Encoding.UTF8.GetString(text, length);
            _memoPtr = (nint)text;
            _memoLen = length;
            _memoCode = decoded;
            if (length <= 0)
            {
                _memoBytes = [];
            }
            else
            {
                if (_memoBytes == null || _memoBytes.Length != length) _memoBytes = new byte[length];
                new ReadOnlySpan<byte>(text, length).CopyTo(_memoBytes);
            }

            return decoded;
        }

        private static bool SameBytes(byte* text, int length, byte[]? known)
        {
            if (known == null || known.Length != length) return false;
            return new ReadOnlySpan<byte>(text, length).SequenceEqual(known);
        }

        /// <summary>The prepared callable for one SQL value, memoised on its bytes. A miss decodes and prepares, which
        /// is what the first row of a statement pays.</summary>
        internal Prepared PreparedFor(nint codeValue, int arity)
        {
            var text = ReadCode(codeValue);
            if (text == null) return new Prepared(null, new ScriptResult { HasError = true, ErrorMessage = EmptyCode });

            if (_memoArity == arity && _memoText != null && string.Equals(text, _memoText, StringComparison.Ordinal))
                return _memoPrepared!;

            var prepared = Prepare(text, arity);
            _memoArity = arity;
            _memoText = text;
            _memoPrepared = prepared;
            return prepared;
        }

        // ------------------------------------------------------------------ the callable itself

        /// <summary>The callable for one (code, arity), built on first use. ONE <c>Interpret</c> per distinct pair is
        /// what makes the door usable per row, and everything after it is a call.
        ///
        /// The callable is assembled as an AST LAMBDA around the expression the caller wrote, rather than by
        /// re-parsing a wrapped text: a wrapper such as <c>(a0) =&gt; (CODE)</c> shifts every position in the code by
        /// the length of its own prefix, so a failure would name a column that is not the caller's. Built this way the
        /// expression keeps the positions it was parsed with, and there is no second lex.</summary>
        private Prepared Prepare(string code, int arity)
        {
            var key = arity.ToString() + "\u0001" + code;
            if (_prepared.TryGetValue(key, out var known)) return known;

            Prepared prepared;
            try
            {
                var lexer = new Lexer(code.AsMemory());
                var parser = new Parser(lexer.GetTokens().ToList(), _itp.DelegateInfos, caseSensitive: true, source: code);
                var expr = parser.ParseInlineExpression(code);

                // An untyped parameter per argument, which is what a lambda written as `(a, b) => ...` has: the values
                // keep the types they arrived with rather than being converted to a declared one.
                var parameters = new (TypeInfo Type, string Name)[arity];
                var none = new bool[arity];
                for (var i = 0; i < arity; i++)
                    parameters[i] = (new TypeInfo(TokenType.Object, true, 0, 0, null), "a" + i);

                var line = expr.Line > 0 ? expr.Line : 1;
                var column = expr.Column > 0 ? expr.Column : 1;
                var lambda = LambdaExpr.Create(parameters, none, none, none, expr, null, line, column);

                // Nothing to capture: the body is an expression over the parameters, and any other name it reads is a
                // global, which a lambda resolves live. Saying so keeps the compiler from analysing a body it did not
                // write capture metadata for.
                lambda.CapturesAnalyzed = true;
                lambda.HasExternalCaptures = false;

                // Evaluating the expression is what creates the callable, and it happens HERE, once, instead of per
                // row: the engine's own statement path builds the ScriptFunction exactly as a script's lambda would.
                var built = _itp.Interpret([ExprStmt.Create(lambda, line, column)]);

                prepared = built.HasError
                    ? new Prepared(null, new ScriptResult
                    {
                        HasError = true,
                        ErrorMessage = built.ErrorMessage,
                        ErrorLine = built.ErrorLine,
                        ErrorColumn = built.ErrorColumn,
                    })
                    : built.ReturnValue is ScriptFunction function
                        ? new Prepared(function, null)
                        : new Prepared(null, new ScriptResult
                        {
                            HasError = true,
                            ErrorMessage = "the code did not answer a function, so it cannot be called with arguments",
                        });
            }
            catch (ScriptException ex)
            {
                // A code text that does not parse is the CALLER's mistake, and it is still a Result: a shape that
                // answered a SQL error here would make a typo look like a broken query.
                prepared = new Prepared(null, new ScriptResult
                {
                    HasError = true,
                    ErrorMessage = ex.Message,
                    ErrorLine = ex.Line,
                    ErrorColumn = ex.Column,
                });
            }

            _prepared[key] = prepared;
            return prepared;
        }

        // ------------------------------------------------------------------ set at a time

        /// <summary>Many rows in, many answers out, in one call. The rows arrive as a JSON array here; the aggregate
        /// door below feeds the same loop from a query.</summary>
        internal ScriptResult Map(string code, string rowsJson)
        {
            object? parsed;
            try
            {
                parsed = Interpreter.ParseJsonInput(rowsJson);
            }
            catch (ScriptException ex)
            {
                return new ScriptResult { HasError = true, ErrorMessage = ex.Message };
            }

            if (parsed is not IScriptList rows)
                return new ScriptResult
                {
                    HasError = true,
                    ErrorMessage = "slc_map takes a JSON ARRAY of rows as its second argument, as in " +
                                   "json_group_array(n) or json_group_array(json_array(a, b))",
                };

            // Materialised once, because a script list is an indexer rather than an IReadOnlyList: one pass over the
            // set is what the loop below would have paid anyway, and it keeps one implementation for both set doors.
            var elements = new object?[rows.Count];
            for (var i = 0; i < rows.Count; i++) elements[i] = rows[i];
            return MapValues(code, elements);
        }

        /// <summary>The map itself, which both set doors share: the JSON form hands it what it read, and the aggregate
        /// hands it what it collected, so the two cannot disagree about what a row is or how a failure is reported.
        /// An element that is a list supplies the arguments in order and any other element supplies a0 alone.</summary>
        internal ScriptResult MapValues(string code, object?[] rows)
        {
            var answers = new List<object?>(rows.Length);
            int? arity = null;

            for (var i = 0; i < rows.Length; i++)
            {
                var element = rows[i];
                object?[] args;
                if (element is IScriptList row)
                {
                    args = new object?[row.Count];
                    for (var k = 0; k < row.Count; k++) args[k] = row[k];
                }
                else
                {
                    // Any other element is a0 alone, which is what `json_group_array(n)` produces and what an
                    // aggregate's step receives.
                    args = [element];
                }

                if (arity is { } expected && args.Length != expected)
                    return new ScriptResult
                    {
                        HasError = true,
                        ErrorMessage = $"row {i + 1} of {rows.Length} supplies {args.Length} argument(s) while the " +
                                       $"first row supplies {expected}: one call reads one shape of row",
                    };
                arity = args.Length;

                var answer = Invoke(code, args);
                if (answer.HasError) return RowFailure(i, rows.Length, answer);

                answers.Add(answer.ReturnValue);
            }

            return new ScriptResult { ReturnValue = Interpreter.SerializeToText(answers) };
        }

        /// <summary>The aggregate's door into the same map. Its rows arrive FLAT (one list append per value, no array
        /// per row) with a fixed argument count, and the buffer below is REUSED for every row: the callee binds each
        /// value into its own locals before the call returns, so nothing it can name holds this array.</summary>
        internal ScriptResult MapCollected(string code, int arity, List<object?> flat)
        {
            var count = flat.Count / arity;
            var answers = new List<object?>(count);
            var args = new object?[arity];

            for (var row = 0; row < count; row++)
            {
                var start = row * arity;
                for (var k = 0; k < arity; k++) args[k] = flat[start + k];

                var answer = Invoke(code, args);
                if (answer.HasError) return RowFailure(row, count, answer);

                answers.Add(answer.ReturnValue);
            }

            return new ScriptResult { ReturnValue = Interpreter.SerializeToText(answers) };
        }

        /// <summary>How a failing row is reported, stated once for both set doors: the index is the caller's own, and
        /// the message keeps the engine's position so a reader can find the expression.</summary>
        private static ScriptResult RowFailure(int index, int count, ScriptResult answer)
            => new()
            {
                HasError = true,
                ErrorMessage = $"row {index + 1} of {count}: {answer.ErrorMessage}",
                ErrorLine = answer.ErrorLine,
                ErrorColumn = answer.ErrorColumn,
            };

        /// <summary>Runs a FILE in this connection's interpreter, so its functions stay callable afterwards. The path
        /// is resolved against the process directory, which is the only directory a SQL host has. A file's REPL dot
        /// commands are stripped and its functions are precompiled, so it behaves as it does from the command line.
        /// <para>
        /// A COMPILED BUNDLE is decided by CONTENT (the wire's own magic, through
        /// <see cref="EmbedApi.IsCompiledBundle"/>), not by an extension, so `select slc_file('x.slcbc')` answers the
        /// same document `select slc_file('x.slc')` does: the source a bundle carries runs through this same pipeline,
        /// and a source-less one runs its entry. Measured before this, the bundle's binary header reached the LEXER and
        /// the answer was `Unexpected character '\u001E'`, at line 1 column 5.
        /// </para></summary>
        internal ScriptResult LoadFile(string path)
        {
            char[]? pooled = null;

            try
            {
                var full = Path.GetFullPath(path);
                if (!File.Exists(full))
                {
                    return new ScriptResult { HasError = true, ErrorMessage = $"File not found: '{full}'" };
                }

                if (EmbedApi.IsCompiledBundle(full))
                    return LoadBundle(full);

                var (source, buffer) = FileReader.ReadFile(full);
                pooled = buffer;
                var timing = false;
                var echo = false;
                source = FileReader.StripDotCommands(source, ref timing, ref echo);

                var lexer = new Lexer(source);
                var parser = new Parser(lexer.GetTokens().ToList(), _itp.DelegateInfos, caseSensitive: true, source: source.ToString());
                var statements = parser.GetStatements().ToList();

                // The pooled buffer is released as soon as the statements exist: a token carries its own text, and
                // what a node keeps is a copied slice, so nothing reads this memory afterwards.
                FileReader.ReleaseBuffer(pooled);
                pooled = null;

                Program.PrecompileFunctions(statements, _itp);
                // The ledger's baseline is taken BEFORE the run, and the report attached after it: a suite's tail is
                // `CheckSummary(); exit(CheckFailed());`, so without this the answer was the echo of that call and the
                // failing check existed only in stdout the SQL caller does not see. The whole run sits inside
                // `InProcessState` because `Interpret` reports the AMBIENT exit code: without it one subject ending
                // `exit(3)` made every later `slc_file` on that connection answer 3, measured.
                var since = _itp.SnapshotChecks();
                var result = ScriptRunner.InProcessState(() => _itp.Interpret(statements, full));
                return ScriptRunner.WithChecks(_itp, since, result);
            }
            catch (ScriptException ex)
            {
                return new ScriptResult { HasError = true, ErrorMessage = ex.Message, ErrorLine = ex.Line, ErrorColumn = ex.Column };
            }
            catch (Exception ex)
            {
                return new ScriptResult { HasError = true, ErrorMessage = FriendlyExceptionMessage(ex) };
            }
            finally
            {
                if (pooled != null) FileReader.ReleaseBuffer(pooled);
            }
        }

        public void Dispose() => _itp.Dispose();

        /// <summary>A COMPILED BUNDLE through the same door a source file takes: the run's verdict and the script's own
        /// answer, which is the shape `slc_file` reports (`ReturnValue`). Both bundle kinds work, because
        /// `ScriptRunner.RunBundleInValue` is the one pipeline: a bundle carrying its source runs that source, and a
        /// source-less one invokes its entry, in THIS session's interpreter, so a function it declares stays callable
        /// through `slc_eval`/`slc_map` afterwards exactly as a source file's does.
        /// <para>
        /// A LIBRARY blob has no entry, so it answers its own refusal ("this bundle declares no entry ...") rather than
        /// a parse error: that is the one shape this door cannot run, and it says which door can.
        /// </para></summary>
        private ScriptResult LoadBundle(string full)
        {
            var (outcome, value) = ScriptRunner.RunBundleIn(_itp, full);
            if (outcome.HasError)
            {
                return new ScriptResult
                {
                    HasError = true,
                    ExitCode = outcome.Status,
                    ErrorMessage = outcome.Message,
                    ErrorLine = outcome.Line,
                    ErrorColumn = outcome.Column,
                };
            }

            return new ScriptResult { ExitCode = outcome.Status, ReturnValue = value };
        }

        /// <summary>One already-built callable, or the Result that says why it could not be built.</summary>
        internal sealed record Prepared(ScriptFunction? Function, ScriptResult? Refusal);
    }

    /// <summary>An aggregate's running state, held across a statement by the SQLite aggregate context.</summary>
    private sealed class AggregateState
    {
        /// <summary>slc_agg's running value. Unused by the map form, which collects rows instead.</summary>
        internal object? Value;
        internal string? Error;

        // The map form's own state: the code read from the first row (xFinal is the only place it runs), the row's
        // argument count, and the values collected FLAT (one append per value, no array per row).
        internal string? Code;
        internal int Arity;
        internal List<object?>? Flat;
    }

    // ---------------------------------------------------------------- value and result marshalling
    private static string? ReadText(nint value)
    {
        var text = (byte*)((delegate* unmanaged[Cdecl]<nint, nint>)Fn(Routine.ValueText))(value);
        if (text == null) return null;

        // value_text BEFORE value_bytes is not a style: the byte count belongs to the representation value_text
        // produced, and this host may have converted from UTF-16 on the way.
        var bytes = ValueBytesOf(value);
        return bytes <= 0 ? "" : Encoding.UTF8.GetString(text, bytes);
    }

    /// <summary>The SQL type of one value, which is the question the two "shape that can only be a mistake" guards
    /// ask before reading it.</summary>
    private static int ValueTypeOf(nint value)
        => ((delegate* unmanaged[Cdecl]<nint, int>)Fn(Routine.ValueType))(value);

    /// <summary>The byte count that belongs to the text <c>value_text</c> just answered.</summary>
    private static int ValueBytesOf(nint value)
        => ((delegate* unmanaged[Cdecl]<nint, int>)Fn(Routine.ValueBytes))(value);

    private static object? ReadValue(nint value)
    {
        var type = ((delegate* unmanaged[Cdecl]<nint, int>)Fn(Routine.ValueType))(value);

        switch (type)
        {
            case TypeInteger:
                return ((delegate* unmanaged[Cdecl]<nint, long>)Fn(Routine.ValueInt64))(value);
            case TypeFloat:
                return ((delegate* unmanaged[Cdecl]<nint, double>)Fn(Routine.ValueDouble))(value);
            case TypeText:
                return ReadText(value);
            case TypeBlob:
                return BlobOf(value);
            default:
                return null;
        }
    }

    /// <summary>A script value as the nearest SQL value. A type SQL has no equivalent for (a DateTime, a list, a
    /// class instance) becomes TEXT through the engine's own display rule, so a caller sees what a script would print
    /// rather than a CLR type name.</summary>
    private static void ResultValue(nint ctx, object? value)
    {
        switch (value)
        {
            case null:
                ResultNull(ctx);
                return;
            case bool flag:
                ResultInt64(ctx, flag ? 1 : 0);
                return;
            case sbyte or short or int or long:
                ResultInt64(ctx, Convert.ToInt64(value));
                return;
            case byte b:
                ResultInt64(ctx, b);
                return;
            case ushort or uint:
                ResultInt64(ctx, Convert.ToInt64(value));
                return;
            case ulong big:
                ResultInt64(ctx, unchecked((long)big));
                return;
            case float or double or decimal:
                ResultDouble(ctx, Convert.ToDouble(value));
                return;
            case string text:
                ResultText(ctx, text);
                return;
            case char one:
                ResultText(ctx, one.ToString());
                return;
            case byte[] bytes:
                ResultBlob(ctx, bytes);
                return;
            default:
                ResultText(ctx, ValueFormatter.ToDisplay(value));
                return;
        }
    }

    private static void ResultText(nint ctx, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* p = bytes)
            ((delegate* unmanaged[Cdecl]<nint, byte*, int, nint, void>)Fn(Routine.ResultText))(ctx, p, bytes.Length, SqliteTransient);
    }

    private static void ResultError(nint ctx, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        fixed (byte* p = bytes)
            ((delegate* unmanaged[Cdecl]<nint, byte*, int, void>)Fn(Routine.ResultError))(ctx, p, bytes.Length);
    }

    private static void ResultNull(nint ctx) => ((delegate* unmanaged[Cdecl]<nint, void>)Fn(Routine.ResultNull))(ctx);

    private static void ResultInt64(nint ctx, long value)
        => ((delegate* unmanaged[Cdecl]<nint, long, void>)Fn(Routine.ResultInt64))(ctx, value);

    private static void ResultDouble(nint ctx, double value)
        => ((delegate* unmanaged[Cdecl]<nint, double, void>)Fn(Routine.ResultDouble))(ctx, value);

    private static void ResultBlob(nint ctx, byte[] bytes)
    {
        var buffer = bytes.Length == 0 ? new byte[1] : bytes;
        fixed (byte* p = buffer)
            ((delegate* unmanaged[Cdecl]<nint, byte*, int, nint, void>)Fn(Routine.ResultBlob))(ctx, p, bytes.Length, SqliteTransient);
    }

    // ---------------------------------------------------------------- refusals
    /// <summary>A failed run as one sentence, which is what a scalar answer needs. A message that already carries a
    /// position keeps it, and one that does not gets the run's, so the recipe a reader needs is always there.</summary>
    private static string Describe(ScriptResult result)
    {
        var message = result.ErrorMessage ?? "the run failed";
        if (result.ErrorLine > 0 && !message.Contains("(at line", StringComparison.Ordinal))
            message += $" (at line {result.ErrorLine}, column {result.ErrorColumn})";
        return message;
    }

    private static string? VersionRefusal(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return $"this host reports its SQLite version as '{version}', which cannot be read, so the load cannot know whether it " +
                   $"supports SQLITE_DIRECTONLY (3.{MinMinorVersion} and newer).";

        if (major < 3 || (major == 3 && minor < MinMinorVersion))
            return $"this host is SQLite {version}, and these functions need 3.{MinMinorVersion} or newer because they register with " +
                   "SQLITE_DIRECTONLY: registering on an older host would succeed while dropping the guard that stops a database " +
                   "file from running script code through a view or a trigger.";

        return null;
    }

    /// <summary>Answers the load with a message, using SQLite's OWN allocator so the host can free what it is given.
    /// A host whose table could not be resolved has no allocator this can reach, so the code is returned without a
    /// message rather than with memory the host would free through a different heap.</summary>
    private static int Refuse(byte** ppErrMsg, nint* api, string message)
    {
        try
        {
            if (ppErrMsg == null) return SqliteError;

            var malloc = api != null ? (nint)api[MallocIndex] : nint.Zero;
            if (malloc == nint.Zero) malloc = Fn(Routine.Malloc);
            if (malloc == nint.Zero) return SqliteError;

            var bytes = Encoding.UTF8.GetBytes("ScriptLangCore: " + message);
            var buffer = ((delegate* unmanaged[Cdecl]<int, byte*>)malloc)(bytes.Length + 1);
            if (buffer == null) return SqliteError;

            bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
            buffer[bytes.Length] = 0;
            *ppErrMsg = buffer;

            return SqliteError;
        }
        catch
        {
            return SqliteError;
        }
    }
}
