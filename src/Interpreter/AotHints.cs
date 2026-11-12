namespace ScriptLangCore;

internal partial class Interpreter
{
    private const DynamicallyAccessedMemberTypes AvoidAotTrim
        = DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicFields
        | DynamicallyAccessedMemberTypes.PublicConstructors
        | DynamicallyAccessedMemberTypes.Interfaces;

    #region AOT Compiler Hints
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification = "DynamicDependency on enum triggers false-positive Enum.GetValues analysis")]
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification = "Marshal.StructureToPtr(object,…), Win32 interop; refactor to generic overload when Marshal supports it")]
    [UnconditionalSuppressMessage("Trim", "IL2026", Justification = "Assembly.Load(byte[]), dynamic assembly loading by script (GetType/Invoke)")]
    [UnconditionalSuppressMessage("Trim", "IL2062", Justification = "Activator.CreateInstance(Type), script-requested .NET type instantiation (new SomeType())")]
    [UnconditionalSuppressMessage("Trim", "IL2065", Justification = "Type.GetMethods(BindingFlags), script method resolution on user-supplied .NET types")]
    [UnconditionalSuppressMessage("Trim", "IL2075", Justification = "Type.GetMethod/GetProperty/GetField, script property/field access on .NET objects")]
    [UnconditionalSuppressMessage("Trim", "IL2057", Justification = "Type.GetType(string), script-requested .NET type resolution (new SomeType())")]
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification = "Type.MakeGenericType(Type[]), script-requested generic type instantiation")]
    [UnconditionalSuppressMessage("Trim", "IL2055", Justification = "MakeGenericType call, intended for runtime type construction")]
    [UnconditionalSuppressMessage("Trim", "IL2070", Justification = "Type.GetConstructors(), script constructor resolution (new SomeType(args))")]
    [UnconditionalSuppressMessage("Trim", "IL2067", Justification = "Activator.CreateInstance(Type, Object[]), script constructor invocation")]
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification = "Array.CreateInstance(Type, int[]), script-requested array creation")]
    [UnconditionalSuppressMessage("Trim", "IL2070", Justification = "FindBestMethod reflection, intentional runtime method resolution")]
    // Types accessed via reflection, must keep members for the interpreter to work
    // Common .NET types used by the script runtime
    // A script-visible CLR property whose SETTER only reflection reaches: the trimmer keeps a control's constructor and
    // drops the members nothing managed calls, which is the lesson SqliteRow, VmBytecodeCodec.LibraryMeta and
    // BytecodeResult each recorded (a Debug build works, an AOT publish reports "property not found"). ClassHandlesMouse
    // is set BY a script, so nothing managed reads the setter.
    //
    // ONE ENTRY PER TYPE IS ENOUGH, and the per-MEMBER entries this region used to carry were REDUNDANT: a named entry
    // keeps one member, while `AvoidAotTrim` on the same type keeps its public methods, properties, fields and
    // constructors, which is a superset of every member that was named here (`ClassHandlesMouse` and `MessageBox` on
    // Control, `SetMenu` on Form, the three `MouseButtons` values, and the same for MessageInfo and PaintEventArgs
    // further down). SIXTEEN such entries were removed, verified on the AOT publish by the suites that READ those
    // members: control_style_test (ClassHandlesMouse), control_events_test (PaintEventArgs), menu_bar_test
    // (MouseButtons), winapi_surface_test (MessageInfo.WParam). A per-member entry is worth writing only where NO
    // broad entry exists for the type, and then the reason goes beside it.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.PublicProperties, typeof(ScriptLangCore.Interpreter.Menu))]
    // A SCRIPT CLASS CAN EXTEND `TextWriter` OR `TextReader` (ScriptTextBridge): the CLASS is a script class, but the
    // FACE it is handed to the CLR as is one of the engine's bridge types, and `Console.SetOut`/`SetIn` reach the base's
    // members by REFLECTION, so both bases are rooted here. Without them the shipped build answers "Method 'SetOut' has no
    // overload" for a call that works on the development build, which is the family this file exists for.
    [DynamicDependency(AvoidAotTrim, typeof(TextWriter))]
    [DynamicDependency(AvoidAotTrim, typeof(TextReader))]
    // `StringComparer.Ordinal` IS DOCUMENTED (`docs/language/index.html`, the IComparable/IComparer row gives it as
    // the alternative to writing a comparer), and the AOT publish answered "Static member 'Ordinal' not found on
    // type StringComparer" while the Debug build answered it, because nothing managed references the type and the
    // trimmer dropped its members. The static-member lookup reaches a framework type by REFLECTION, which is what
    // this list exists for; measured after rooting: Ordinal resolves and compares on both builds.
    [DynamicDependency(AvoidAotTrim, typeof(StringComparer))]
    [DynamicDependency(AvoidAotTrim, typeof(char))]
    [DynamicDependency(AvoidAotTrim, typeof(char[]))]
    [DynamicDependency(AvoidAotTrim, typeof(byte))]
    [DynamicDependency(AvoidAotTrim, typeof(byte[]))]
    [DynamicDependency(AvoidAotTrim, typeof(sbyte))]
    [DynamicDependency(AvoidAotTrim, typeof(sbyte[]))]
    [DynamicDependency(AvoidAotTrim, typeof(short))]
    [DynamicDependency(AvoidAotTrim, typeof(short[]))]
    [DynamicDependency(AvoidAotTrim, typeof(ushort))]
    [DynamicDependency(AvoidAotTrim, typeof(ushort[]))]
    [DynamicDependency(AvoidAotTrim, typeof(int))]
    [DynamicDependency(AvoidAotTrim, typeof(int[]))]
    [DynamicDependency(AvoidAotTrim, typeof(uint))]
    [DynamicDependency(AvoidAotTrim, typeof(uint[]))]
    [DynamicDependency(AvoidAotTrim, typeof(long))]
    [DynamicDependency(AvoidAotTrim, typeof(long[]))]
    [DynamicDependency(AvoidAotTrim, typeof(ulong))]
    [DynamicDependency(AvoidAotTrim, typeof(ulong[]))]
    [DynamicDependency(AvoidAotTrim, typeof(float))]
    [DynamicDependency(AvoidAotTrim, typeof(float[]))]
    [DynamicDependency(AvoidAotTrim, typeof(double))]
    [DynamicDependency(AvoidAotTrim, typeof(double[]))]
    [DynamicDependency(AvoidAotTrim, typeof(decimal))]
    [DynamicDependency(AvoidAotTrim, typeof(decimal[]))]
    [DynamicDependency(AvoidAotTrim, typeof(bool))]
    [DynamicDependency(AvoidAotTrim, typeof(bool[]))]
    [DynamicDependency(AvoidAotTrim, typeof(string))]
    [DynamicDependency(AvoidAotTrim, typeof(string[]))]
    [DynamicDependency(AvoidAotTrim, typeof(Guid))]
    [DynamicDependency(AvoidAotTrim, typeof(Enum))]
    [DynamicDependency(AvoidAotTrim, typeof(DateTime))]
    [DynamicDependency(AvoidAotTrim, typeof(DateTimeExtensions))]
    [DynamicDependency(AvoidAotTrim, typeof(DateTimeOffset))]
    [DynamicDependency(AvoidAotTrim, typeof(TimeSpan))]
    [DynamicDependency(AvoidAotTrim, typeof(TimeZoneInfo))]
    [DynamicDependency(AvoidAotTrim, typeof(Regex))]
    [DynamicDependency(AvoidAotTrim, typeof(RegexOptions))]
    [DynamicDependency(AvoidAotTrim, typeof(Match))]
    [DynamicDependency(AvoidAotTrim, typeof(Group))]
    [DynamicDependency(AvoidAotTrim, typeof(Capture))]
    [DynamicDependency(AvoidAotTrim, typeof(MatchCollection))]
    [DynamicDependency(AvoidAotTrim, typeof(GroupCollection))]
    [DynamicDependency(AvoidAotTrim, typeof(CaptureCollection))]
    [DynamicDependency(AvoidAotTrim, typeof(CronExpression))]
    [DynamicDependency(AvoidAotTrim, typeof(ScheduledTask))]
    [DynamicDependency(AvoidAotTrim, typeof(Array))]
    [DynamicDependency(AvoidAotTrim, typeof(List<>))]
    [DynamicDependency(AvoidAotTrim, typeof(Dictionary<,>))]
    [DynamicDependency(AvoidAotTrim, typeof(Math))]
    [DynamicDependency(AvoidAotTrim, typeof(Convert))]
    // The framework types a script reaches BY NAME, which the assembly-backed type index in TypeHelpers now
    // resolves. `Type.GetType(name)` without an assembly qualifier searches only the CALLING assembly and the
    // core library, so not one of these was reachable by name before that step (measured: `Uri`, `HttpClient`,
    // `IPAddress`, `Stopwatch`, `Color`, `ZipFile`, `SHA256`, `BigInteger`, `CultureInfo`, `Process` and
    // `FileInfo` all missed, identically on the JIT build and on the AOT publish). Once a name resolves, the
    // trimmer still has to keep what reflection reads: `AvoidAotTrim` covers public STATIC members as well
    // (`PublicMethods` means all public methods, the enum carries no static/instance dimension), so one entry
    // per TYPE is what this needs, and the list grows as a script asks for another type. HAVING NO ENTRY HERE IS
    // NOT AN OMISSION for `HttpClient` and `FileInfo`: their assemblies are already loaded, so the index answers
    // them unaided. DELIBERATELY ABSENT: `Color`, whose type lives in `System.Drawing.Primitives` and would pull
    // that assembly in for a name the engine's own palette replaces, and `Process`, which is an engine NAMESPACE
    // name (see the guard in ResolveTypeToClr) and stays a miss by rule. Both report an unknown type name.
    [DynamicDependency(AvoidAotTrim, typeof(Uri))]
    [DynamicDependency(AvoidAotTrim, typeof(UriBuilder))]
    [DynamicDependency(AvoidAotTrim, typeof(WebUtility))]
    [DynamicDependency(AvoidAotTrim, typeof(Stopwatch))]
    [DynamicDependency(AvoidAotTrim, typeof(CultureInfo))]
    [DynamicDependency(AvoidAotTrim, typeof(BitConverter))]
    [DynamicDependency(AvoidAotTrim, typeof(Version))]
    [DynamicDependency(AvoidAotTrim, typeof(Hashtable))]
    // `RuntimeFeature` needs BOTH halves and each was measured on its own: the promise table below makes the NAME
    // resolve (without it a script reads `Undefined variable 'RuntimeFeature'` on the shipping build), and this entry
    // keeps its MEMBERS (without it the same script reads `Static member 'IsDynamicCodeSupported' not found on type
    // RuntimeFeature`). The pair is what turns "can this build load code at run time?" into `False` there and `True`
    // under the JIT build, which is the one question a script needs before calling `LoadAssembly`.
    [DynamicDependency(AvoidAotTrim, typeof(System.Runtime.CompilerServices.RuntimeFeature))]
    // Iterating a `Hashtable` yields a `DictionaryEntry`, and reading its Key/Value is reflection over a type
    // nothing else touches: measured, `foreach (var kv in Environment.GetEnvironmentVariables()) { kv.Key }`
    // answered on the JIT build and was "Property 'Key' not found on type DictionaryEntry" on the AOT publish.
    [DynamicDependency(AvoidAotTrim, typeof(DictionaryEntry))]
    [DynamicDependency(AvoidAotTrim, typeof(ArrayList))]
    [DynamicDependency(AvoidAotTrim, typeof(SortedDictionary<,>))]
    [DynamicDependency(AvoidAotTrim, typeof(ZipFile))]
    [DynamicDependency(AvoidAotTrim, typeof(ZipArchive))]
    [DynamicDependency(AvoidAotTrim, typeof(SHA256))]
    [DynamicDependency(AvoidAotTrim, typeof(MD5))]
    [DynamicDependency(AvoidAotTrim, typeof(RandomNumberGenerator))]
    [DynamicDependency(AvoidAotTrim, typeof(IPAddress))]
    [DynamicDependency(AvoidAotTrim, typeof(Dns))]
    [DynamicDependency(AvoidAotTrim, typeof(System.Numerics.BigInteger))]
    // The three CURATED namespaces answer the CLR surface behind them (File over System.IO.File, and the same for
    // Directory and Path), so a member the engine never registered is resolved by REFLECTION on these types: measured,
    // without these entries the JIT build resolved File.ReadAllTextAsync while the AOT publish kept reporting
    // "Method 'ReadAllTextAsync' not found in namespace 'File'", because the trimmer had removed every member
    // nothing managed calls. These types are the reason the entry is needed at all: the engine's own code names
    // them for the curated members (File.ReadAllText and friends), which roots only the members it CALLS.
    [DynamicDependency(AvoidAotTrim, typeof(System.IO.File))]
    [DynamicDependency(AvoidAotTrim, typeof(System.IO.Directory))]
    [DynamicDependency(AvoidAotTrim, typeof(System.IO.Path))]
    // The ASYNC ITERATOR a `File.ReadLinesAsync` hands back, which the engine's `await foreach` FINDS by reflection:
    // it walks the value's interface list for IAsyncEnumerable<T> and the enumerator's for IAsyncEnumerator<T>, and on
    // AOT a framework type's interface list is metadata the trimmer REDUCES (measured: without these entries the AOT
    // publish answered "has no GetAsyncEnumerator" while the JIT build read the file line by line, and the interface
    // list shrank from six entries to five). The DRIVE then uses COMPILED calls (`step is ValueTask<bool>`, `is
    // IAsyncDisposable`) rather than reflected ones, because a member reached only by reflection can be a stub the AOT
    // compiler left for a body its scanner removed: measured, a reflected DisposeAsync answered
    // "[DisposeAsync]: operation not supported" (an empty-message NotSupportedException) on AOT, and a broad
    // `All`-on-System.IO.File hint did NOT fix it, while the compiled cast did on the first try.
    [DynamicDependency(AvoidAotTrim, typeof(IAsyncEnumerable<>))]
    [DynamicDependency(AvoidAotTrim, typeof(IAsyncEnumerator<>))]
    [DynamicDependency(AvoidAotTrim, typeof(IAsyncDisposable))]
    // And the task a `File.*Async` member hands back: the fallback reads its value off the ACTUAL type's `Result`
    // by reflection, because the FRAMEWORK's async methods are compiled with the runtime lowering and hand back its
    // own task (System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask<T>), which is not a Task<T> a C# cast
    // can name. That is the RUNTIME's lowering, not this project's switch (measured: a publish with
    // `-p:Features=runtime-async=off` still awaits a File.*Async member correctly, because only the engine's own
    // async methods change with that switch and there is one, EventSubject's). Measured without these roots: the JIT
    // build awaited the value while the AOT publish awaited an EMPTY one, the trimmer having dropped the property
    // nothing managed reads.
    [DynamicDependency(AvoidAotTrim, typeof(System.Threading.Tasks.Task))]
    [DynamicDependency(AvoidAotTrim, typeof(System.Threading.Tasks.Task<>))]
    [DynamicDependency(AvoidAotTrim, typeof(System.Threading.Tasks.Task<string>))]
    [DynamicDependency(AvoidAotTrim, typeof(System.Runtime.CompilerServices.TaskAwaiter<>))]
    // A VALUE TASK is the same shape one step over: `ValueTask` is matched directly by the bridge, while a
    // `ValueTask<T>` is reached by REFLECTION, because a pattern cannot name a generic struct without its type
    // argument and the bridge has only the runtime type to go on (a script reaches one whenever a CLR member
    // returns it: an enumerator's MoveNextAsync, Stream.DisposeAsync). Measured without this root: the JIT build
    // awaited `en.MoveNextAsync()` and the AOT publish answered "await expects a Task", because the trimmer had
    // removed `ValueTask<T>.AsTask`, the ONE member the bridge asks for.
    [DynamicDependency(AvoidAotTrim, typeof(System.Threading.Tasks.ValueTask))]
    [DynamicDependency(AvoidAotTrim, typeof(System.Threading.Tasks.ValueTask<>))]
    [DynamicDependency(AvoidAotTrim, typeof(McpContext))]
    [DynamicDependency(AvoidAotTrim, typeof(TcpServer))]
    [DynamicDependency(AvoidAotTrim, typeof(TcpClient))]
    [DynamicDependency(AvoidAotTrim, typeof(ReadLineRequest))]
    [DynamicDependency(AvoidAotTrim, typeof(TcpContext))]
    [DynamicDependency(AvoidAotTrim, typeof(HttpContext))]
    [DynamicDependency(AvoidAotTrim, typeof(Random))]
    [DynamicDependency(AvoidAotTrim, typeof(StringBuilder))]
    [DynamicDependency(AvoidAotTrim, typeof(MemoryStream))]
    [DynamicDependency(AvoidAotTrim, typeof(StringWriter))]
    [DynamicDependency(AvoidAotTrim, typeof(Environment))]
    [DynamicDependency(AvoidAotTrim, typeof(GC))]
    [DynamicDependency(AvoidAotTrim, typeof(Console))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptBoxedList))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptUnboxedList<>))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptTuple))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptLazyDeserializedList))]
    // The LAZY AVRO READ (`Avro.ReadContainer("Iterable", data)`) is the same shape one reader over, and it needs the
    // same root for the same reason: `Count` and the record indexer are members a SCRIPT reads by NAME, so nothing
    // managed calls them and the trimmer drops them. Measured without this entry: avro_slc_store_test 5a passed on
    // the Debug build and the AOT publish answered "Property 'Count' not found on type Iterable" at that check,
    // because the value reports the DECLARATION it was read as (TypeAliases names it `DeclaredName`), which is what
    // made the refusal read as a missing member on the declaration rather than on the wrapper that holds it.
    [DynamicDependency(AvoidAotTrim, typeof(AvroLazyContainer))]
    [DynamicDependency(AvoidAotTrim, typeof(MemBuffer))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptBoxedDictionary))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptUnboxedDictionary<,>))]
    [DynamicDependency(AvoidAotTrim, typeof(BoxedScriptHashSet))]
    [DynamicDependency(AvoidAotTrim, typeof(UnboxedScriptHashSet<>))]
    [DynamicDependency(AvoidAotTrim, typeof(HashSet<>))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptResult))]
    // The generic event layer (`Events.Create`): a script reads `Subscribe`/`Next`/`Count` and friends BY NAME, which
    // is reflection over this type's public members, so the trimmer has to be told. `Interfaces` in the flag set is the
    // half that matters for the pull side: the engine's `await foreach` drive finds `IAsyncEnumerable<object?>` by
    // walking `GetInterfaces()`, and the recorded failure of that route on AOT was a framework iterator answering five
    // interfaces where the sixth had been trimmed away.
    [DynamicDependency(AvoidAotTrim, typeof(EventSubject))]
    [DynamicDependency(AvoidAotTrim, typeof(EventSubscription))]
    [DynamicDependency(AvoidAotTrim, typeof(EventSubjectEnumerator))]
    [DynamicDependency(AvoidAotTrim, typeof(CancelEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(EventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(CommandEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(DropFilesEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(TimerEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(ResizeEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(MouseEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(MouseButtons))]
    [DynamicDependency(AvoidAotTrim, typeof(KeyEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(KeyCodes))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptFunction))]
    // Delegate adapter types, preserve for MakeGenericType under NativeAOT
    [DynamicDependency(AvoidAotTrim, typeof(ActionAdapter0))]
    [DynamicDependency(AvoidAotTrim, typeof(ActionAdapter1<>))]
    [DynamicDependency(AvoidAotTrim, typeof(ActionAdapter2<,>))]
    [DynamicDependency(AvoidAotTrim, typeof(ActionAdapter3<,,>))]
    [DynamicDependency(AvoidAotTrim, typeof(FuncAdapter0<>))]
    [DynamicDependency(AvoidAotTrim, typeof(FuncAdapter1<,>))]
    [DynamicDependency(AvoidAotTrim, typeof(FuncAdapter2<,,>))]
    [DynamicDependency(AvoidAotTrim, typeof(FuncAdapter3<,,,>))]
    [DynamicDependency(AvoidAotTrim, typeof(Coroutine))]
    // ReplayableEnumerable, script-visible cache-once iterable wrapper (Replayable/Memoize);
    // preserved so enumeration stays intact under NativeAOT trimming.
    [DynamicDependency(AvoidAotTrim, typeof(ReplayableEnumerable))]
    // Generator<T> must be preserved so scripts can call .MoveNext()/.Current/.Dispose() on the direct-call
    // generator path under NativeAOT (its members are otherwise trimmed; older batteries only exercised
    // Coroutine.Start + Resume, which the Coroutine hint above already preserves).
    [DynamicDependency(AvoidAotTrim, typeof(Generator<>))]
    [DynamicDependency(AvoidAotTrim, typeof(Task))]
    [DynamicDependency(AvoidAotTrim, typeof(TaskStatusEnum))]
    [DynamicDependency(AvoidAotTrim, typeof(CancellationTokenSource))]
    [DynamicDependency(AvoidAotTrim, typeof(CancellationToken))]
    [DynamicDependency(AvoidAotTrim, typeof(ManualResetEventSlim))]
    // A SCRIPT CAN START WORK ON A POOL THREAD, and that is how a runner raises a kill: measured on the AOT publish,
    // `ThreadPool.QueueUserWorkItem(fun (o) { ... })` answered "Method 'QueueUserWorkItem' not found on type ThreadPool
    // with 1 argument(s)" because nothing referenced the overload in managed code, so the trimmer dropped it and a
    // script could not put a run where another thread can cancel it. `StreamReader`/`StreamWriter`/`FileStream` are
    // rooted here for the same reason: a script that streams a long run's output to a file and tails it (exactly what
    // `clipboard_runner.slc` does) asks those types by name.
    [DynamicDependency(AvoidAotTrim, typeof(ThreadPool))]
    [DynamicDependency(AvoidAotTrim, typeof(StreamReader))]
    [DynamicDependency(AvoidAotTrim, typeof(StreamWriter))]
    [DynamicDependency(AvoidAotTrim, typeof(FileStream))]
    [DynamicDependency(AvoidAotTrim, typeof(Lock))]
    [DynamicDependency(AvoidAotTrim, typeof(ScopeStack))]
    [DynamicDependency(AvoidAotTrim, typeof(ScopeStack.VariableInfo))]
    [DynamicDependency(AvoidAotTrim, typeof(CallFrame))]
    [DynamicDependency(AvoidAotTrim, typeof(ConcurrentQueue<Action>))]
    [DynamicDependency(AvoidAotTrim, typeof(ConcurrentQueue<>))]
    [DynamicDependency(AvoidAotTrim, typeof(BlockingCollection<ValueTuple<Action, ExecutionContext?>>))]
    [DynamicDependency(AvoidAotTrim, typeof(ExecutionContext))]
    [DynamicDependency(AvoidAotTrim, typeof(Stack<Dictionary<string, ScopeStack.VariableInfo>>))]
    [DynamicDependency(AvoidAotTrim, typeof(Stack<>))]
    [DynamicDependency(AvoidAotTrim, typeof(Dictionary<string, ScopeStack.VariableInfo>))]
    [DynamicDependency(AvoidAotTrim, typeof(ConcurrentDictionary<string, ScopeStack.VariableInfo>))]
    [DynamicDependency(AvoidAotTrim, typeof(ConcurrentDictionary<,>))]
    [DynamicDependency(AvoidAotTrim, typeof(HashSet<string>))]
    [DynamicDependency(AvoidAotTrim, typeof(LinqExtensions))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptKeyValuePair))]
    [DynamicDependency(AvoidAotTrim, typeof(KeyValuePair<,>))]
    // THE NON-GENERIC `KeyValuePair` IS A DIFFERENT TYPE AND NEEDS ITS OWN ROOT: it is the STATIC class
    // (`System.Collections.Generic.KeyValuePair`, abstract + sealed, ZERO public constructors), and it is what the
    // Debug build's leaf-name lookup finds for `new KeyValuePair("a", 1)`, so that leg answered "No matching
    // constructor found for .NET type 'KeyValuePair'". With nothing referencing it, the trimmer dropped it, the AOT
    // lookup fell through to the ARITY-STRIPPED generic and answered "Failed to instantiate .NET type
    // 'KeyValuePair`2': invalid argument" instead: a JIT/AOT divergence that surfaced as two different REFUSALS.
    // Rooting it makes both builds resolve the same type, which is the parity the bundle's entry needs.
    [DynamicDependency(AvoidAotTrim, typeof(KeyValuePair))]
    [DynamicDependency(AvoidAotTrim, typeof(EventInfo))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptErrorInfo))]
    [DynamicDependency(AvoidAotTrim, typeof(ScriptException))]
    [DynamicDependency(AvoidAotTrim, typeof(Exception))]
    [DynamicDependency(AvoidAotTrim, typeof(InvalidCastException))]
    [DynamicDependency(AvoidAotTrim, typeof(DivideByZeroException))]
    [DynamicDependency(AvoidAotTrim, typeof(IOException))]
    [DynamicDependency(AvoidAotTrim, typeof(FileNotFoundException))]
    [DynamicDependency(AvoidAotTrim, typeof(TargetInvocationException))]
    [DynamicDependency(AvoidAotTrim, typeof(ProcessHandle))]
    [DynamicDependency(AvoidAotTrim, typeof(Process))]
    [DynamicDependency(AvoidAotTrim, typeof(FileStreamReader))]
    [DynamicDependency(AvoidAotTrim, typeof(FileStreamWriter))]
    [DynamicDependency(AvoidAotTrim, typeof(Control))]
    [DynamicDependency(AvoidAotTrim, typeof(ControlCollection))]
    [DynamicDependency(AvoidAotTrim, typeof(Form))]
    [DynamicDependency(AvoidAotTrim, typeof(MdiForm))]
    [DynamicDependency(AvoidAotTrim, typeof(MdiChild))]
    [DynamicDependency(AvoidAotTrim, typeof(Button))]
    [DynamicDependency(AvoidAotTrim, typeof(RadioButton))]
    [DynamicDependency(AvoidAotTrim, typeof(ButtonBase))]
    [DynamicDependency(AvoidAotTrim, typeof(OpenFileDialog))]
    [DynamicDependency(AvoidAotTrim, typeof(InputBox))]
    [DynamicDependency(AvoidAotTrim, typeof(SaveFileDialog))]
    [DynamicDependency(AvoidAotTrim, typeof(GenericControl))]
    [DynamicDependency(AvoidAotTrim, typeof(TrackBar))]
    [DynamicDependency(AvoidAotTrim, typeof(Label))]
    [DynamicDependency(AvoidAotTrim, typeof(TextBox))]
    [DynamicDependency(AvoidAotTrim, typeof(ListBox))]
    [DynamicDependency(AvoidAotTrim, typeof(ComboBox))]
    [DynamicDependency(AvoidAotTrim, typeof(ListView))]
    [DynamicDependency(AvoidAotTrim, typeof(TreeView))]
    [DynamicDependency(AvoidAotTrim, typeof(DateTimePicker))]
    [DynamicDependency(AvoidAotTrim, typeof(StatusBar))]
    [DynamicDependency(AvoidAotTrim, typeof(TheTimer))]
    [DynamicDependency(AvoidAotTrim, typeof(TabControl))]
    [DynamicDependency(AvoidAotTrim, typeof(ToolBar))]
    [DynamicDependency(AvoidAotTrim, typeof(SysLink))]
    [DynamicDependency(AvoidAotTrim, typeof(HotKey))]
    [DynamicDependency(AvoidAotTrim, typeof(Rebar))]
    [DynamicDependency(AvoidAotTrim, typeof(CheckBox))]

    // COM interop types, used via IDynamicPropertyProvider bridge
    [DynamicDependency(AvoidAotTrim, typeof(DispatchVtbl.ComAutomation))]
    [DynamicDependency(AvoidAotTrim, typeof(DispatchVtbl.ComObject))]
    [DynamicDependency(AvoidAotTrim, typeof(DispatchVtbl.ComNode))]
    [DynamicDependency(AvoidAotTrim, typeof(DispatchVtbl.ComScope))]

    // Win32 interop structs, used via Marshal.StructureToPtr / SizeOf<T> in WinApi.cs and Ui.cs
    [DynamicDependency(AvoidAotTrim, typeof(Rect))]
    [DynamicDependency(AvoidAotTrim, typeof(RECT))]
    [DynamicDependency(AvoidAotTrim, typeof(Point))]
    [DynamicDependency(AvoidAotTrim, typeof(Msg))]
    [DynamicDependency(AvoidAotTrim, typeof(Wndclassexw))]
    [DynamicDependency(AvoidAotTrim, typeof(PAINTSTRUCT))]
    [DynamicDependency(AvoidAotTrim, typeof(MONITORINFO))]
    [DynamicDependency(AvoidAotTrim, typeof(INITCOMMONCONTROLSEX))]
    [DynamicDependency(AvoidAotTrim, typeof(TCITEMW))]
    [DynamicDependency(AvoidAotTrim, typeof(LVCOLUMNW))]
    [DynamicDependency(AvoidAotTrim, typeof(LVITEMW))]
    [DynamicDependency(AvoidAotTrim, typeof(TVINSERTSTRUCTW))]
    [DynamicDependency(AvoidAotTrim, typeof(TVITEMW))]
    [DynamicDependency(AvoidAotTrim, typeof(TVITEMEXW))]
    [DynamicDependency(AvoidAotTrim, typeof(MINMAXINFO))]
    [DynamicDependency(AvoidAotTrim, typeof(TV_UNION))]
    [DynamicDependency(AvoidAotTrim, typeof(TBBUTTON))]
    [DynamicDependency(AvoidAotTrim, typeof(Control.TOOLINFOW))]
    // PeekMessage return type, members accessed by script via reflection
    // Explicit property-name dependencies to survive AOT trimming of struct metadata
    [DynamicDependency(AvoidAotTrim, typeof(PaintEventArgs))]
    [DynamicDependency(AvoidAotTrim, typeof(MessageInfo))]
    // DynamicLibrary for script LoadLibrary/FreeLibrary
    [DynamicDependency(AvoidAotTrim, typeof(DynamicLibrary))]
    [DynamicDependency(AvoidAotTrim, typeof(NativeFunction))]
    [DynamicDependency(AvoidAotTrim, typeof(OrderedEnumerable))]
    [DynamicDependency(AvoidAotTrim, typeof(SqliteIterator))]
    [DynamicDependency(AvoidAotTrim, typeof(SqliteMappingIterator))]
    // The lazy row's accessors (this[], Keys, Values, Count, ContainsKey, Dictionary) are reached
    // through reflection by the engine, without this entry they work in Debug and vanish on AOT.
    [DynamicDependency(AvoidAotTrim, typeof(SqliteRow))]
    [DynamicDependency(AvoidAotTrim, typeof(CronType))]
    [DynamicDependency(AvoidAotTrim, typeof(Cipher.CipherInfo))]
    [DynamicDependency(AvoidAotTrim, typeof(WeakRef))]
    [DynamicDependency(AvoidAotTrim, typeof(JsonSerializeOptions))]
    [DynamicDependency(AvoidAotTrim, typeof(VmBytecodeCodec.LibraryMeta))]
    // The memory export's result: its .Bytes and .Report are read by reflection and by nothing else, so without
    // this entry ExportScriptBytes works in Debug and answers "Property 'Report' not found on type BytecodeResult"
    // on AOT. Measured on the shipped build before this line existed.
    [DynamicDependency(AvoidAotTrim, typeof(BytecodeResult))]
    [DynamicDependency(AvoidAotTrim, typeof(ControlProp))]
    [DynamicDependency(AvoidAotTrim, typeof(StyleFlag))]
    [DynamicDependency(AvoidAotTrim, typeof(ColumnProp))]
    [DynamicDependency(AvoidAotTrim, typeof(TreeItemProp))]
    [DynamicDependency(AvoidAotTrim, typeof(Encoding))]
    [DynamicDependency(AvoidAotTrim, typeof(AesGcm))]
    /// <summary>A reachable body whose only job is to make the AOT compiler GENERATE the generic instantiations a
    /// script can ask for at runtime, and to LOAD the assemblies the CLR types a script can NAME live in.
    /// `Array.Empty<int>()` in a script closes the definition through
    /// MakeGenericMethod, and NativeAOT can only instantiate what its compiler already saw: a reference type works
    /// either way (shared generics), a VALUE type only when that exact instantiation exists in the binary. These
    /// statements are the touch that creates them, and the method is called from the interpreter's setup, so the
    /// trimmer keeps both the method and the initializers it contains.
    ///
    /// Deliberately short and aligned with the suite's portable check (clr_generic_typeargs_test 2b: that
    /// instantiation either closes or the engine reports it), the list is the value types a script plausibly
    /// asks for, not an attempt to cover every type. Anything outside it is reported, never mis-instantiated.
    ///
    /// The second job is new with the type index (TypeHelpers.FindLoadedClrType), and it is a LOAD rather than a
    /// trim hint: the index resolves a script-written name against the assemblies that are loaded, and a framework
    /// type the engine never touches is in an assembly nothing has loaded, so `Uri`, `ZipFile` and `BigInteger`
    /// missed however the index was built (they resolved on the JIT build only once these touches existed, and the
    /// `typeof` is what loads each one in Debug and in the shipped binary alike). The [DynamicDependency] entries
    /// above are what keep the MEMBERS reflection reads. The table is REGISTERED rather than a bare `_ =` touch so
    /// an optimizing compiler cannot delete it, and so the same list answers the lookup directly.</summary>
    public static void AotCompilerHint()
    {
        // Instantiation roots, see the summary. Reference-type instantiations are listed for the record: they
        // work on AOT without this touch, while the value types below do not.
        _ = Array.Empty<int>();
        _ = Array.Empty<long>();
        _ = Array.Empty<double>();
        _ = Array.Empty<bool>();
        _ = Array.Empty<DateTime>();
        _ = Array.Empty<TimeSpan>();
        _ = Array.Empty<Guid>();
        _ = Array.Empty<string>();
        _ = Array.Empty<object>();

        // The roots, see the summary. They are REGISTERED rather than merely touched: a bare `_ = typeof(X);` or a
        // local array read only for its `Length` is a statement an optimizing compiler may delete, and a REGISTERED
        // table is also what answers the lookup directly, so the promise holds even where an assembly is not
        // enumerable. The same list is what the [DynamicDependency] entries above keep the members of.
        //
        // `Process` is NOT here, and that is a rule rather than an omission: it is the name of an engine NAMESPACE
        // (`Registration/Process.cs`), so the scope already answers it and the type lookup deliberately does not
        // (see the namespace guard in TypeHelpers.ResolveTypeToClr). Its CLR type stays reachable through
        // `ProcessStartInfo`, which is a type name and resolves.
        RegisterPromisedClrTypes(
        [
            typeof(Uri),
            typeof(UriBuilder),
            typeof(WebUtility),
            typeof(Stopwatch),
            typeof(CultureInfo),
            typeof(BitConverter),
            typeof(Version),
            typeof(Hashtable),
            typeof(DictionaryEntry),
            typeof(ArrayList),
            typeof(SortedDictionary<string, object?>),
            typeof(ZipFile),
            typeof(ZipArchive),
            typeof(SHA256),
            typeof(MD5),
            typeof(RandomNumberGenerator),
            typeof(IPAddress),
            typeof(Dns),
            typeof(System.Numerics.BigInteger),
            // `RuntimeFeature` is what a script asks "can this build load code at run time?" with, which is the ONE
            // question the shipping artifact answers no to and every other answer is a refusal it has to catch. The
            // engine itself uses the type five times (the depth cap, the VM's frame cap, `LoadAssembly`'s guard and
            // `slc_version`), so a script was reading `Undefined variable 'RuntimeFeature'` on the build where the
            // answer matters most and `True` on the JIT one. Registered here so the name resolves and answers `False`.
            typeof(System.Runtime.CompilerServices.RuntimeFeature),
        ]);
    }

    #endregion
}
