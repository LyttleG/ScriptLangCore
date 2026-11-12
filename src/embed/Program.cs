// THE EMBEDDING CHECK: loads the published engine DLL and drives the C ABI as a host would. This is an INSTRUMENT, not a
// suite (a suite runs inside the engine and cannot load it), so it lives under `tools/` beside the other hand-run
// checks and `tools/embed-check.sh` is its runner.
//
// WHY A JIT HOST: what is under test is the DLL's native surface, and a JIT .NET host proves the exports are real entry
// points. The AOT launcher shape was measured separately (1.27 MB launching this same DLL, 2026-10-27).
//
// WHAT IT PROVES, in the order it asks: a session starts on a thread it owns (an MTA host thread) or on the host's
// thread (when that thread is STA with 16 MB or more), `print` and the engine's log lines reach a host callback, a
// run's declarations stay callable, a compiled bundle runs in BOTH shapes (carried source and source-less), a runaway is
// a catchable refusal rather than a dead process, a cancel lands from another thread even while the script is inside
// `sleep`, `slc_free` refuses while a run is in flight, and repeated create/free cycles do not leak handles.
//
// USAGE: embed-check <engine dll> [engine exe]
// The exe is only needed for the bundle section, which compiles its subject with `--compile`.

using System.Diagnostics;
using System.Runtime.InteropServices;
using ScriptLangCore.Abi;

var dllPath = args.Length > 0 ? args[0] : throw new ArgumentException("usage: embed-check <engine dll> [engine exe]");
var lib = NativeLibrary.Load(dllPath);
Console.WriteLine($"loaded: {dllPath}");

var version = Export<VersionFn>(AbiExports.Version);
var create = Export<CreateFn>(AbiExports.Create);
var free = Export<FreeFn>(AbiExports.Free);
var mode = Export<ThreadModeFn>(AbiExports.ThreadMode);
var lastError = Export<LastErrorFn>(AbiExports.LastError);
var stats = Export<StatsFn>(AbiExports.Stats);
// THE CONTRACT'S OWN NUMBERS, from the exports that exist to answer them. Both are pinned against the GENERATED
// bindings, so a harness that passes is evidence about `docs/embed/abi.json` rather than about a second declaration.
var abiRevision = Export<AbiRevisionFn>(AbiExports.AbiRevision);
var capabilities = Export<CapabilitiesFn>(AbiExports.Capabilities);

var failures = 0;

// 1. The version, and the ABI revision a host checks before anything else.
var text = Ask((b, c) => version(b, c));
Check("slc_version names the engine, the runtime and the ABI",
    text.Contains("ScriptLangCore") && text.Contains("NativeAOT") && text.Contains($"embed ABI {AbiExports.Revision}"), text);
Check("slc_abi_revision answers the revision the manifest describes, without a handle",
    abiRevision() == AbiExports.Revision, $"revision={abiRevision()} expected={AbiExports.Revision}");
// THE EMBED BUILD COMPILES THE COM SERVER, so this image must advertise that bit too: a host asks this mask once and
// drops what is absent, and a bit that is missing here is a door the DLL really does not have.
Check("slc_capabilities advertises every capability the manifest names, the COM server included",
    capabilities() == AbiCapabilities.All, $"mask=0x{capabilities():X} expected=0x{AbiCapabilities.All:X}");
Check("a 0-capacity call answers the needed length and a small buffer truncates, terminating",
    Ask((b, c) => version(b, c)) == text && text.Length > 0, $"len={text.Length}");

// 2. A session from this thread, which is MTA: the engine must own one.
Console.WriteLine($"host apartment: {Thread.CurrentThread.GetApartmentState()}");

var h1 = create(nint.Zero);
Check("slc_create answers a handle", h1 > 0, $"handle={h1} err=[{CreateError()}]");
Check("auto picks the engine's own thread on an MTA host thread", mode(h1) == 0, $"mode={mode(h1)}");

var s1 = SessionStats(h1);
Check("slc_stats reports a live interpreter with its namespaces registered",
    s1.Contains("\"Globals\":") && !s1.Contains("\"Globals\":0"), s1);
Check("slc_stats says STA and 32 MB", s1.Contains("\"Apartment\":\"STA\"") && s1.Contains("\"StackMB\":32"), s1);
Check("slc_stats repeats the revision and the capability mask, so one log line answers what answered",
    s1.Contains($"\"Abi\":{AbiExports.Revision}") && s1.Contains($"\"Capabilities\":{AbiCapabilities.All}"), s1);
Check("slc_last_error is empty after a good create", SessionError(h1).Length == 0, SessionError(h1));
Check("slc_free releases it", free(h1) == 0);
Check("a freed handle is not reused: free again answers 2", free(h1) == 2);
Check("slc_thread_mode on an unknown handle answers -1", mode(h1) == -1);

// 3. Bad input is refused by name, never silently defaulted.
var bad = create(Utf8("{\"threads\":\"own\"}"));
Check("an unknown option is refused", bad == 0 && CreateError().Contains("unknown option"), CreateError());

var badJson = create(Utf8("{oops"));
Check("malformed JSON is refused through the engine's message map",
    badJson == 0 && CreateError().Length > 0 && !CreateError().Contains("JsonReaderException"), CreateError());

var badMode = create(Utf8("{\"thread\":\"mine\"}"));
Check("an unknown thread mode is refused", badMode == 0 && CreateError().Contains("not \"auto\", \"own\" or \"host\""),
    CreateError());
Check("unknown handle: free answers 2, mode -1", free(999999) == 2 && mode(999999) == -1);

// 4. Asking the MTA host thread to carry a run fails with the reason a host can act on.
var forced = create(Utf8("{\"thread\":\"host\"}"));
Check("\"thread\":\"host\" on an MTA thread is refused with the STA reason",
    forced == 0 && CreateError().Contains("STA"), CreateError());

// 5. A host thread that IS STA and roomy is used in place, and it FREES there too: a session pinned to a host thread
// can only be driven from that thread (there is no queue to post to), which is the price of zero-marshalling calls.
long hostModeHandle = 0;
var hostModeStats = "";
var hostModeFree = -1;
var hostModeMode = -99;
var staBig = new Thread(() =>
{
    hostModeHandle = create(nint.Zero);
    if (hostModeHandle <= 0) return;
    hostModeMode = mode(hostModeHandle);
    hostModeStats = SessionStats(hostModeHandle);
    hostModeFree = free(hostModeHandle);
}, 32 * 1024 * 1024);

staBig.SetApartmentState(ApartmentState.STA);
staBig.Start();
staBig.Join();
Check("auto uses a host thread that is STA with 32 MB", hostModeHandle > 0 && hostModeMode == 1,
    $"handle={hostModeHandle} mode={hostModeMode} err=[{CreateError()}]");
Check("that session reports the host's apartment and budget",
    hostModeStats.Contains("\"Thread\":\"host\"") && hostModeStats.Contains("\"StackMB\":32"), hostModeStats);
Check("and a host-thread session frees on its own thread", hostModeFree == 0, $"free={hostModeFree}");

// 6. A host thread that is STA but SMALL is refused when the host insists on it.
var smallError = "";
var staSmall = new Thread(() =>
{
    var h = create(Utf8("{\"thread\":\"host\"}"));
    smallError = h == 0 ? CreateError() : "accepted with handle " + h;
    if (h > 0)
        free(h);
});

staSmall.SetApartmentState(ApartmentState.STA);
staSmall.Start();
staSmall.Join();
Check("a 1 MB STA host thread is refused with the stack reason", smallError.Contains("MB of stack"), smallError);

// 7. Auto on that same small STA thread must still work, on the engine's own thread.
long autoOnSmall = 0;
var staSmallAuto = new Thread(() => { autoOnSmall = create(nint.Zero); }, 1 * 1024 * 1024);
staSmallAuto.SetApartmentState(ApartmentState.STA);
staSmallAuto.Start();
staSmallAuto.Join();
Check("auto refuses nothing: a small STA thread still gets a working session",
    autoOnSmall > 0 && mode(autoOnSmall) == 0, $"handle={autoOnSmall} err=[{CreateError()}]");
if (autoOnSmall > 0) Check("and it frees", free(autoOnSmall) == 0);

// 9. Repeated create/free must not leak OS handles (one thread per session, joined on free).
var process = Process.GetCurrentProcess();
process.Refresh();
var before = process.HandleCount;
for (var i = 0; i < 40; i++)
{
    var h = create(nint.Zero);
    if (h <= 0)
    {
        Check($"cycle {i} created a session", false, CreateError());
        break;
    }

    if (free(h) != 0)
    {
        Check($"cycle {i} freed its session", false, SessionError(h));
        break;
    }
}

process.Refresh();
var immediate = process.HandleCount;
// A managed Thread wrapper holds its OS handle until it is finalized, so ask the GC before judging: what matters for
// a host is that nothing ACCUMULATES once collection has run, not that a wrapper is still on the finalizer queue.
for (var i = 0; i < 3; i++)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
}

process.Refresh();
var after = process.HandleCount;
Check("40 create/free cycles grow the handle count by no more than one per session thread", after - before <= 55,
    $"{before} -> {immediate} -> {after} (collected)");

// THE RESIDUE IS NAMED, NOT HIDDEN: an own-thread session leaves about one kernel handle behind even after the
// interpreter is disposed and the GC has run three times. Two of the three original handles were closed by this
// round (`EmbedSession`'s two wait events, and the interpreter's own pump event); what is left is per CREATED THREAD
// rather than per session or per interpreter, which the next block measures. Recorded in AGENTS.md as the open item
// with the numbers, so a host that opens thousands of sessions knows what to expect and can pin to its own thread.

// ATTRIBUTION 1: a host-pinned session creates no thread, and those must not accumulate at all.
{
    long pinnedDelta = 0;
    var sta = new Thread(() =>
    {
        var p0 = Process.GetCurrentProcess().HandleCount;
        for (var i = 0; i < 20; i++)
        {
            var h = create(Utf8("{\"thread\":\"host\"}"));
            if (h <= 0 || free(h) != 0)
            {
                Console.WriteLine($"     host-pinned cycle {i} failed: [{CreateError()}]");
                break;
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Process.GetCurrentProcess().Refresh();
        pinnedDelta = Process.GetCurrentProcess().HandleCount - p0;
    }, 32 * 1024 * 1024);
    sta.SetApartmentState(ApartmentState.STA);
    sta.Start();
    sta.Join();
    Check("20 host-pinned sessions (no thread each) do not accumulate handles", pinnedDelta <= 6,
        $"{pinnedDelta} handle(s)");
}

// ATTRIBUTION 2, report-only: is the residue the ABI, or the runtime's own bookkeeping for a thread we create?
{
    var c0 = process.HandleCount;
    for (var i = 0; i < 20; i++)
    {
        var bare = new Thread(() => { });
        bare.Start();
        bare.Join();
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    process.Refresh();
    var c1 = process.HandleCount;
    var st0 = c1;
    for (var i = 0; i < 20; i++)
    {
        var sta = new Thread(() => { }, 32 * 1024 * 1024);
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join();
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    process.Refresh();
    var st1 = process.HandleCount;
    var d0 = st1;
    for (var i = 0; i < 20; i++)
    {
        using var e1 = new ManualResetEventSlim(false);
        using var e2 = new ManualResetEventSlim(false);
        e2.Set();
        e1.Wait(1);
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    process.Refresh();
    var d1 = process.HandleCount;
    Console.WriteLine(
        $"     control: 20 bare threads {c0} -> {c1} ({c1 - c0}), 20 STA threads {st0} -> {st1} ({st1 - st0}), 20 disposed event pairs {d0} -> {d1} ({d1 - d0})");
}

// 10. A session created and freed while another one exists is independent.
var a = create(nint.Zero);
var b = create(nint.Zero);
Check("two sessions at once get distinct handles and distinct ids", a > 0 && b > 0 && a != b, $"{a} / {b}");
Check("each reports its own id in stats",
    SessionStats(a).Contains($"\"Id\":{a}") && SessionStats(b).Contains($"\"Id\":{b}"));
Check("and both free", free(a) == 0 && free(b) == 0);

// ── P3: THE VERBS ────────────────────────────────────────────────────────────────────────────────────────────────
// A session that runs real scripts: output through the callback, verdicts, a call back into what a run declared, the
// depth budget from an MTA host, COM on the STA thread the session owns, and a cancel from another thread.

var setOutput = Export<SetOutputFn>(AbiExports.SetOutput);
var runFile = Export<RunFileFn>(AbiExports.RunFile);
var runText = Export<RunTextFn>(AbiExports.RunText);
var runBundle = Export<RunBundleFn>(AbiExports.RunBundle);
var callFn = Export<CallFn>(AbiExports.Call);
var cancelFn = Export<CancelFn>(AbiExports.Cancel);
var lastResultFn = Export<LastResultFn>(AbiExports.LastResult);
// The host-object door and the asynchronous pair (embed ABI 4), exported beside the rest so a machine that ships only
// some of them fails HERE rather than in a host's `Declare`.
var adopt = Export<AdoptFn>(AbiExports.Adopt);
var poll = Export<PollFn>(AbiExports.Poll);
var runTextAsync = Export<RunTextAsyncFn>(AbiExports.RunTextAsync);

// ── THE SESSION SANDBOX (ABI 5) ────────────────────────────────────────────────────────────────────────────────────
// An embedded host names the directories a script's files must live in ("dirs") and the families of calls it does not
// trust ("deny"). Every refusal names the option and what it covers, because that sentence is what a host shows its
// user; a sandbox that failed as "unknown member" would look like a script bug. The vocabulary is validated at CREATE,
// where a typo is the host's to see, not at the first path a script touches.
var sandboxRoot = System.IO.Path.GetTempPath().Replace("\\", "/").TrimEnd('/');
var sandboxJson = "{\"dirs\":[\"" + sandboxRoot + "\"],\"deny\":[\"process\"]}";
var sandboxed = create(Utf8(sandboxJson));
Check("a session accepts \"dirs\" and \"deny\" together", sandboxed > 0, CreateError() + " " + sandboxJson);
Check("a script writes INSIDE its \"dirs\"", runText(sandboxed, Utf8($"File.WriteAllText(\"{sandboxRoot}/abi_sandbox_inside.txt\", \"inside\");"), nint.Zero) == 0,
    SessionError(sandboxed));
Check("and reads back what it wrote", runText(sandboxed, Utf8($"print(File.ReadAllText(\"{sandboxRoot}/abi_sandbox_inside.txt\"));"), nint.Zero) == 0,
    SessionError(sandboxed));
var outsideRead = runText(sandboxed, Utf8("File.ReadAllText(\"C:/Windows/win.ini\");"), nint.Zero);
Check("a path OUTSIDE \"dirs\" is refused, naming the list", outsideRead != 0 && SessionError(sandboxed).Contains("dirs"),
    SessionError(sandboxed));
var deniedProcess = runText(sandboxed, Utf8("Process.Start(\"cmd.exe\", \"/c echo hi\");"), nint.Zero);
Check("a denied capability refuses BY NAME, naming the option",
    deniedProcess != 0 && SessionError(sandboxed).Contains("\"process\"") && SessionError(sandboxed).Contains("deny"),
    SessionError(sandboxed));
Check("a denied capability does not stop the rest of the session", runText(sandboxed, Utf8("print(1 + 1);"), nint.Zero) == 0,
    SessionError(sandboxed));
Check("the sandboxed session frees", free(sandboxed) == 0);

var noFiles = create(Utf8("{\"deny\":[\"file\"]}"));
Check("\"deny\":\"file\" refuses a file call too", noFiles > 0 &&
    runText(noFiles, Utf8("File.Exists(\"C:/Windows/win.ini\");"), nint.Zero) != 0 && SessionError(noFiles).Contains("\"file\""),
    CreateError() + " " + SessionError(noFiles));
Check("...and it frees", free(noFiles) == 0);

var badCapability = create(Utf8("{\"deny\":[\"telepathy\"]}"));
Check("\"deny\" refuses a name that is not a capability, listing the ones that are",
    badCapability == 0 && CreateError().Contains("telepathy") && CreateError().Contains("clipboard"), CreateError());
var missingDir = create(Utf8("{\"dirs\":[\"no-such-folder-anywhere-9c1\"]}"));
Check("\"dirs\" refuses a directory this machine does not have",
    missingDir == 0 && CreateError().Contains("not a directory"), CreateError());


var lines = new List<string>(64);
var sinkPointer = Marshal.GetFunctionPointerForDelegate((LineCb)Sink); // kept alive by the local until process end

var v = create(nint.Zero);
Check("a session for the verbs", v > 0, CreateError());
Check("slc_set_output takes a callback", setOutput(v, sinkPointer, nint.Zero) == 0, SessionError(v));

var hello = runText(v, Utf8("print('hello from the engine');"), nint.Zero);
Check("slc_run_text runs source text", hello == 0, $"status={hello} [{ResultOf(v)}]");
Check("its print reached the host callback on channel 0", lines.Contains("0|hello from the engine"),
    string.Join(" ; ", lines));

var declares = runText(v, Utf8("int Twice(int n) { return n * 2; } print('declared');"), Utf8("decl.slc"));
Check("a run's declarations stay in the session", declares == 0, ResultOf(v));

// A CALLBACK THE ENGINE RUNS ON ITS OWN THREAD MUST RESOLVE AGAINST THE SESSION'S INTERPRETER. A namespace object is
// registered on the interpreter that built it, and in a session that is not the instance the script runs on, so a body
// invoked from a BCL worker (Parallel.For) or from the scheduler's own thread used to answer `Undefined variable` for a
// global the script had just declared. Reported from a launcher run of crash_parallel.slc, where Task.Run was fine and
// Parallel.Range was not, and the fix is the `Com.Run` rule: capture `Current ?? this` where the callback is CREATED.
var parallel = runText(v, Utf8(
    "var hits = 0;\n"
    + "var gate = new object();\n"
    + "Parallel.Range(0, 8, fun(i) { Monitor.Enter(gate); hits = hits + 1; Monitor.Exit(gate); });\n"
    + "print('parallel hits=' + hits);\n"), Utf8("parallel.slc"));
Check("a Parallel.Range body reads and writes a session global", parallel == 0, ResultOf(v));
Check("and every chunk ran", lines.Contains("0|parallel hits=8"), string.Join(" ; ", lines));

var scheduled = runText(v, Utf8(
    "var fired = 0;\n"
    + "Scheduler.Enqueue('probe', fun(name) { fired = fired + 1; });\n"
    + "var spins = 0;\n"
    + "while (spins < 300 && fired == 0) { Scheduler.Tick(); sleep(10); spins = spins + 1; }\n"
    + "Scheduler.Clear();\n"
    + "print('scheduler fired=' + fired);\n"), Utf8("scheduler.slc"));
Check("a Scheduler callback reads and writes a session global", scheduled == 0, ResultOf(v));
Check("and it fired once", lines.Contains("0|scheduler fired=1"), string.Join(" ; ", lines));

// A COROUTINE THAT SLEEPS RESUMES IN A SESSION, which is a different question from the two above: `Coroutine.Pump`
// is a namespace member, so it captured the interpreter that REGISTERED it rather than the one the run uses, and a
// session has both. Measured through the launcher before the fix: the sleep request was scheduled on one interpreter
// and every pump drained the other, so the coroutine stayed Waiting for ever, `assets/language/misc/runtime/06_step6.slc`
// (whose loop waits for it) never exited, and the batch runner running the misc corpus froze on that file.
var pumped = runText(v, Utf8(
    "int Seq() { yield Coroutine.Sleep(60); return 0; }\n"
    + "var co = Coroutine.Start(Seq);\n"
    + "var spins = 0;\n"
    + "while (co.Status != CoroutineStatus.Completed && spins < 100) { Coroutine.Pump(); sleep(20); spins = spins + 1; }\n"
    + "print(co.Status == CoroutineStatus.Completed ? 'pump resumed' : 'pump STUCK');\n"), Utf8("pump.slc"));
Check("Coroutine.Pump resumes a session's own coroutine", pumped == 0, ResultOf(v));
Check("and the sleep request came due", lines.Contains("0|pump resumed"), string.Join(" ; ", lines));

// A `configure` CALLBACK THE ENGINE INVITES IS THE SAME FAMILY, one door over: `Application.CreateForm(title, cb)` is a
// namespace member, so it captured the interpreter that REGISTERED it, and inviting the callback on THAT one made the
// callback read the wrong globals. Reported from `factory_callback_test.slc` run through the launcher: its stripped
// bundle's entry declared `var hits = 0`, the callback could not see it ("Undefined variable 'hits'"), and the same
// bundle through the CLI (ONE interpreter) passed. The engine now invites it on `Current ?? this`.
var configured = runText(v, Utf8(
    "var hits = 0;\n"
    + "var f = Application.CreateForm('abi init probe', (frm) => { hits = hits + 1; frm.Width = 420; });\n"
    + "print('init hits=' + hits + ' width=' + f.Width);\n"
    + "f.Close();\n"), Utf8("configure.slc"));
Check("an Application.CreateForm callback runs in a session", configured == 0, ResultOf(v));
Check("and it reads and writes the session's own globals",
    lines.Contains("0|init hits=1 width=420"), string.Join(" ; ", lines));
var called = callFn(v, Utf8("Twice"), Utf8("[21]"));
Check("slc_call calls a function the run declared", called == 0 && ResultOf(v).Contains("\"Result\":42"),
    $"status={called} [{ResultOf(v)}]");
var missCall = callFn(v, Utf8("NotThere"), Utf8("[]"));
Check("slc_call on a name nothing declares is a usage refusal naming it",
    missCall == 2 && SessionError(v).Contains("NotThere"), SessionError(v));

var exited = runText(v, Utf8("exit(7);"), nint.Zero);
Check("a script's own exit code is the status", exited == 7, $"status={exited} [{ResultOf(v)}]");

var divided = runText(v, Utf8("var x = 1 / 0;"), nint.Zero);
var dividedResult = ResultOf(v);
Check("a script failure reports status 1 with the engine's own message",
    divided == 1 && dividedResult.Contains("\"Error\":true") && dividedResult.Contains("Division by zero"),
    $"status={divided} [{dividedResult}]");

lines.Clear();
var deep = runText(v,
    Utf8("int Down(int n) { if (n == 0) { return 0; } return Down(n - 1) + 1; } print('deep=' + Down(50000));"),
    nint.Zero);
Check("50,000 deep runs from an MTA host (the session's own 32 MB thread)", deep == 0 && lines.Contains("0|deep=50000"),
    $"status={deep} [{ResultOf(v)}] {string.Join(" ; ", lines)}");

lines.Clear();
var com = runText(v,
    Utf8(
        "Com.Run(fun(scope) { var fso = scope.CreateObject('Scripting.FileSystemObject'); print('com=' + typeof(fso)); });"),
    nint.Zero);
Check("COM works, which is the STA thread the session owns", com == 0 && lines.Any(l => l.StartsWith("0|com=", StringComparison.Ordinal)),
    $"status={com} [{ResultOf(v)}] {string.Join(" ; ", lines)}");

lines.Clear();
var ui = runText(v,
    Utf8(
        "var f = Application.CreateForm('embed probe'); f.Width = 320; f.Height = 200; f.Show(); print('form shown'); Application.RunFor(400);"),
    nint.Zero);
Check("a form runs and closes on the session's apartment", ui == 0 && lines.Contains("0|form shown"),
    $"status={ui} [{ResultOf(v)}] {string.Join(" ; ", lines)}");

// A cancel from ANOTHER thread while a run is in flight: this is the one verb that is not marshalled.
long cancelled = -1;
var spinner = new Thread(() => { cancelled = runText(v, Utf8("var i = 0; while (true) { i = i + 1; }"), nint.Zero); })
{
    IsBackground = true
};

spinner.Start();
Thread.Sleep(300);

var asked = cancelFn(v);
spinner.Join(15000);
var cancelResult = ResultOf(v);

Check("slc_cancel stops a run in flight from another thread",
    asked == 0 && cancelled == 1 && cancelResult.Contains("\"Cancelled\":true"),
    $"asked={asked} status={cancelled} [{cancelResult}]");

Check("and the session still works afterwards",
    runText(v, Utf8("print('after cancel');"), nint.Zero) == 0 && lines.Contains("0|after cancel"));

Check("slc_run_file on a missing file is a usage refusal naming it",
    runFile(v, Utf8("no-such-file-anywhere.slc")) == 2 && SessionError(v).Contains("not found"), SessionError(v));

var subject = Path.Combine(Path.GetTempPath(), "p3_subject.slc");
File.WriteAllText(subject, "print('from a file');\nint Thrice(int n) { return n * 3; }\n");
Check("slc_run_file runs a real file", runFile(v, Utf8(subject)) == 0 && lines.Contains("0|from a file"),
    $"status={SessionError(v)}");

Check("and its function is callable afterwards",
    callFn(v, Utf8("Thrice"), Utf8("[5]")) == 0 && ResultOf(v).Contains("\"Result\":15"), ResultOf(v));

// ── P4/P5: THE ACCEPTANCE SET ──────────────────────────────────────────────────────────────────────────────────
// What a host actually does under load: a compiled bundle, a runaway script, a cancel that lands inside `sleep`, and a
// free that has to refuse while a run is in flight.
var engineExe = args.Length > 1 ? args[1] : "publish-aot/ScriptLangCore.exe";

// A MISSING ENGINE EXE SKIPS THIS SECTION RATHER THAN KILLING THE RUN, which is the runner's own contract: it passes
// only the DLL when the tree has no second artifact, and prints a note saying so. Measured before this guard: with
// `publish-aot` holding an archive instead of a publish, `Process.Start` threw `Win32Exception`, the host died with
// rc 127 and no summary line at all, so a run that had already passed thirty checks reported nothing usable.
var hasEngineExe = File.Exists(engineExe);
if (!hasEngineExe)
{
    Console.WriteLine($"note: no engine exe at {engineExe}, so the compiled-bundle checks are skipped " +
                      "(compile in both layouts, the bytes verb, and the bundle-as-source refusal)");
}

var bundleSubject = Path.Combine(Path.GetTempPath(), "p4_bundle_subject.slc");
File.WriteAllText(bundleSubject, "print('bundle body');\nint Quad(int n) { return n * 4; }\n");
var carriedBundle = Path.Combine(Path.GetTempPath(), "p4_bundle_carried.slcbc");
var strippedBundle = Path.Combine(Path.GetTempPath(), "p4_bundle_stripped.slcbc");

// THE BYTES VERB IS WHAT A PACKAGED EXE USES, because a payload has no path to name. It is exported regardless of
// whether a bundle exists to hand it, so the helper is declared out here.
var runBundleBytes = Export<RunBundleBytesFn>(AbiExports.RunBundleBytes);

if (hasEngineExe)
{
    var carriedBuild = CompileBundle(bundleSubject, carriedBundle, "");
    Check("the engine compiles a subject to a bundle that carries its source", File.Exists(carriedBundle),
        carriedBuild);

    lines.Clear();
    var carriedRun = runBundle(v, Utf8(carriedBundle));
    Check("slc_run_bundle runs a bundle that carries its source", carriedRun == 0 && lines.Contains("0|bundle body"),
        $"status={carriedRun} [{ResultOf(v)}] {string.Join(" ; ", lines)}");
    Check("and its function is callable afterwards",
        callFn(v, Utf8("Quad"), Utf8("[6]")) == 0 && ResultOf(v).Contains("\"Result\":24"), ResultOf(v));

    var strippedBuild = CompileBundle(bundleSubject, strippedBundle, "--source=strip");
    Check("the engine also builds a source-less bundle", File.Exists(strippedBundle), strippedBuild);
    lines.Clear();

    var strippedRun = runBundle(v, Utf8(strippedBundle));
    Check("slc_run_bundle runs a SOURCE-LESS bundle through its compiled entry",
        strippedRun == 0 && lines.Contains("0|bundle body"),
        $"status={strippedRun} [{ResultOf(v)}] {string.Join(" ; ", lines)}");
    lines.Clear();

    var bytesStripped = RunBytes(v, File.ReadAllBytes(strippedBundle));
    Check("slc_run_bundle_bytes runs a SOURCE-LESS bundle held in memory",
        bytesStripped == 0 && lines.Contains("0|bundle body"),
        $"status={bytesStripped} [{ResultOf(v)}] {string.Join(" ; ", lines)}");
    Check("and its function is callable afterwards",
        callFn(v, Utf8("Quad"), Utf8("[7]")) == 0 && ResultOf(v).Contains("\"Result\":28"), ResultOf(v));
    lines.Clear();

    var bytesCarried = RunBytes(v, File.ReadAllBytes(carriedBundle));
    Check("slc_run_bundle_bytes runs a bundle that carries its source too",
        bytesCarried == 0 && lines.Contains("0|bundle body"),
        $"status={bytesCarried} [{ResultOf(v)}] {string.Join(" ; ", lines)}");

    // A BUNDLE HANDED TO THE SOURCE VERB MUST BE REFUSED BY NAME, naming the verb that works. This is the shape a real
    // launcher hit: it sent a `.slcbc` to `slc_run_file`, which read the header as text and answered
    // "Unexpected character '\u001d' (line 1, column 5)", i.e. a control character at a byte offset in a binary file.
    var bundleAsSource = runFile(v, Utf8(strippedBundle));
    Check("slc_run_file on a compiled bundle is refused by name, naming slc_run_bundle",
        bundleAsSource == 2 && SessionError(v).Contains("slc_run_bundle") &&
        !SessionError(v).Contains("Unexpected character"),
        $"status={bundleAsSource} {SessionError(v)}");

    // A SCRIPT'S OWN `exitcode` IS THE VERDICT for a bundle too, as it is for a file (`Interpret` reports
    // `ExitCode = Environment.ExitCode`). Without it the source-less entry arm answered 0 whatever the script said:
    // measured on the maintainer's sqlite demo, whose tail is `exitcode = 150; return result;`, where the FILE door
    // reported ExitCode 150 and the BUNDLE door 0 for the same document.
    var exitSubject = Path.Combine(Path.GetTempPath(), "p4_exitcode_subject.slc");
    var exitBundle = Path.Combine(Path.GetTempPath(), "p4_exitcode.slcbc");
    File.WriteAllText(exitSubject, "print('exitcode subject');\nexitcode = 150;\nreturn 3;\n");
    Check("the engine compiles a subject whose top level sets `exitcode`",
        CompileBundle(exitSubject, exitBundle, "--source=strip").Contains("source stripped"),
        "the strip refused, so the check below could not be measured");
    lines.Clear();

    var exitStatus = runBundle(v, Utf8(exitBundle));
    Check("a source-less bundle reports the script's own `exitcode`, not a bare 0",
        exitStatus == 150 && lines.Contains("0|exitcode subject"),
        $"status={exitStatus} [{ResultOf(v)}] {string.Join(" ; ", lines)}");

    // A SUITE'S VERDICT NAMES THE CHECK THAT FAILED, which is what the status alone cannot say: the tail
    // `CheckSummary(); exit(CheckFailed());` is the SCRIPT's decision, so `Status` was 1 with no explanation
    // anywhere but the stdout a host may not own. The ledger answers with the label and the position, and the value the
    // run ended with rides inside the report named for its ORIGIN: `Echo` here, because `CheckSummary()` is an
    // expression statement whose `false` the language records as the run's value.
    lines.Clear();
    var failingSuite = runText(v,
        Utf8("Check(\"this test will fail\", 1 == 0);\nCheckSummary();\nexit(CheckFailed());\n"),
        Utf8("failing_suite"));

    var failingVerdict = ResultOf(v);

    Check("a failing suite's verdict names the check, its position and where the answer came from",
        failingSuite == 1 && failingVerdict.Contains("\"Checks\":{\"Passed\":0,\"Failed\":1,")
                          && failingVerdict.Contains("\"Label\":\"this test will fail\"") &&
                          failingVerdict.Contains("\"Line\":1")
                          && failingVerdict.Contains("\"Result\":{\"Passed\":0,\"Failed\":1,")
                          && failingVerdict.Contains("\"Echo\":false") && !failingVerdict.Contains("\"Answer\"")
                          && failingVerdict.Contains("\"ResultFrom\":\"checks\""),
        $"status={failingSuite} [{failingVerdict}]");

    // THE CONTROLS: a run whose checks all passed carries the counts with no label, and a value the script RETURNED is
    // `Answer` while the echo of an expression statement is `Echo`, so neither can be read as the other.
    lines.Clear();
    var passingSuite = runText(v, Utf8("Check(\"this test passes\", 1 == 1);\nCheckSummary();\nexit(0);\n"),
        Utf8("passing_suite"));

    var passingVerdict = ResultOf(v);
    Check("a passing suite's verdict counts them and lists no failure",
        passingSuite == 0 && passingVerdict.Contains("\"Checks\":{\"Passed\":1,\"Failed\":0}")
                          && !passingVerdict.Contains("\"Label\""),
        $"status={passingSuite} [{passingVerdict}]");

    lines.Clear();
    var answeredSuite = runText(v, Utf8("Check(\"answered\", 1 == 0);\nreturn 42;\n"), Utf8("answered_suite"));

    var answeredVerdict = ResultOf(v);
    Check("a script that also answered keeps its value, as Answer and not as Echo",
        answeredSuite == 0 && answeredVerdict.Contains("\"Answer\":42") && !answeredVerdict.Contains("\"Echo\"")
        && answeredVerdict.Contains("\"Failed\":1") && answeredVerdict.Contains("\"ResultFrom\":\"checks\""),
        $"status={answeredSuite} [{answeredVerdict}]");
}

// THESE TWO NEED NO ENGINE EXE: one is a refusal for a path that does not exist, the other is a blob that is not a
// bundle, so both are checked even on a tree with only the DLL.
Check("slc_run_bundle on a missing bundle is a usage refusal naming it",
    runBundle(v, Utf8("no-such-thing.slcbc")) == 2 && SessionError(v).Contains("not found"), SessionError(v));

var textBlob = RunBytes(v, System.Text.Encoding.UTF8.GetBytes("print('not a bundle');\n"));
Check("a blob that is not a bundle is refused rather than parsed as source",
    textBlob == 3 && SessionError(v).Contains("not a ScriptLangCore bytecode blob"),
    $"status={textBlob} {SessionError(v)}");

Check("and an empty payload is a usage refusal", RunBytes(v, Array.Empty<byte>()) == 2,
    $"status={RunBytes(v, Array.Empty<byte>())} {SessionError(v)}");

// A PACKAGED EXE'S ANSWER REACHES ITS VERDICT (`Result`), which only the FILE door reported before: the bytes arm
// returned `(outcome, null)`, so a payload whose entry answered a value came back with Status and no Result at all,
// while `slc_run_bundle`/`slc_file` on the same bundle reported it. The subject answers at the top level (a `return`
// in a program body IS the script's answer, `takeVoidAnswer`), and it needs the engine exe to be compiled at all, so
// it sits behind the same guard as the other two bundle builds.
if (hasEngineExe)
{
    var answerSubject = Path.Combine(Path.GetTempPath(), "embed_bytes_answer_" + Environment.ProcessId + ".slc");
    File.WriteAllText(answerSubject, "print('bytes answer body');\nreturn 'packed answer';\n");

    var answerBundle = Path.ChangeExtension(answerSubject, ".slcbc");
    var answerBuild = CompileBundle(answerSubject, answerBundle, "--source=strip");
    var answerBytes = File.Exists(answerBundle) ? File.ReadAllBytes(answerBundle) : Array.Empty<byte>();
    var answerStatus = RunBytes(v, answerBytes);
    var answerVerdict = ResultOf(v);
    Check("a source-less payload's answer is in the verdict of slc_run_bundle_bytes",
        answerStatus == 0 && answerVerdict.Contains("\"Result\":\"packed answer\""),
        $"status={answerStatus} [{answerVerdict}] build={answerBuild}");

    // THE CONTROL: a payload that answers NOTHING must not acquire a `Result`, so the check above cannot pass by
    // carrying some earlier run's value.
    var quietSubject = Path.Combine(Path.GetTempPath(), "embed_bytes_quiet_" + Environment.ProcessId + ".slc");
    File.WriteAllText(quietSubject, "print('bytes quiet body');\n");

    var quietBundle = Path.ChangeExtension(quietSubject, ".slcbc");
    CompileBundle(quietSubject, quietBundle, "--source=strip");

    var quietStatus = RunBytes(v, File.Exists(quietBundle) ? File.ReadAllBytes(quietBundle) : Array.Empty<byte>());
    var quietVerdict = ResultOf(v);
    Check("and a payload that answers nothing still reports no Result",
        quietStatus == 0 && !quietVerdict.Contains("\"Result\""),
        $"status={quietStatus} [{quietVerdict}]");
}

// THE PREDICATE A PACKAGED EXE USES, pinned where a suite cannot reach it: the spellings the engine's dispatch owns, and
// a few that must NOT be owned (an app's own flag, a bare word, a path). A flag added to `Program.EngineArguments` and
// not to this list would let a packaged app swallow an engine flag.
var ownsFn = Export<MainOwnsFn>(AbiExports.MainOwns);
var owned = new[] { "--mcp", "--mcp-bc", "--repl", "--dump-bytecode", "--compile" };
var notOwned = new[] { "--verbose", "alpha", "app.slc", "", "--COMPILE" };
var ownedMisses = owned.Where(f => ownsFn(Utf8(f)) != 1).ToArray();
var notOwnedLeaks = notOwned.Where(f => ownsFn(Utf8(f)) != 0).ToArray();
Check("slc_main_owns knows exactly the arguments the engine's dispatch owns",
    ownedMisses.Length == 0 && notOwnedLeaks.Length == 0,
    $"missed=[{string.Join(",", ownedMisses)}] wrong=[{string.Join(",", notOwnedLeaks)}]");

// A `slc_main` RUN THAT FAILS MUST SAY WHY, because that is the arm a launcher uses. Reported 2026-10-31 through the
// launcher: a file that failed inside a `Com.Run` callback answered {"Status":1,"Ok":false,"Error":false,"Fatal":false,
// "Canceled":false} with no message, no position and no frames, because the `.slc` arm built its verdict from the exit
// code alone while the message had already gone to a console the host does not own. The subject fails deterministically
// and needs no COM, and its call is NOT in tail position (a tail call legitimately has no frame). The path is written
// with forward slashes so the JSON needs no escaping.
var mainFn = Export<MainFn>(AbiExports.Main);
var mainStatsBefore = SessionStats(v);
var mainSubject = Path.Combine(Path.GetTempPath(), "embed_main_verdict_" + Environment.ProcessId + ".slc")
    .Replace('\\', '/');
File.WriteAllText(mainSubject, "int Inner(int n) { return 1 / (n - 1); }\nvar q = Inner(1);\nprint(q);\n");

var mainArgv = Utf8("[\"" + mainSubject + "\"]");
int mainStatus;
try
{
    mainStatus = mainFn(v, mainArgv);
}
finally
{
    Marshal.FreeCoTaskMem(mainArgv);
}

var mainVerdict = ResultOf(v);
Check("slc_main reports WHY a file failed, not just the code",
    mainStatus == 1 && mainVerdict.Contains("\"Error\":true") && mainVerdict.Contains("\"Message\"")
    && mainVerdict.Contains("Division by zero") && mainVerdict.Contains("\"Code\":\"Unspecified\"")
    && mainVerdict.Contains("\"Line\":"),
    $"status={mainStatus} [{mainVerdict}]");

Check("...and the frame it names carries a position",
    mainVerdict.Contains("\"Frames\":[\"Inner [L"),
    $"status={mainStatus} [{mainVerdict}]");

File.Delete(mainSubject);

// AND THE RUN IT PERFORMED MUST NOT SILENCE THE SESSION'S OWN OUTPUT. `RunMain`'s first statement assigns
// `Console.OutputEncoding`, which RE-CREATES `Console.Out`, so the engine's "is stdout redirected" baseline has to be
// re-captured or every later `print` takes the redirect door and bypasses the host's callback (measured 2026-10-31:
// with the re-capture missing, every run AFTER a `slc_main` delivered nothing, `captured=[]`, while its lines landed on
// the process console). A launcher calls `slc_main`, so a host that runs a file and then drives the session hits it.
lines.Clear();
var sinkAfterMain = runText(v, Utf8("print('sink after main');"), nint.Zero);
Check("a slc_main call leaves the host's output callback working",
    sinkAfterMain == 0 && lines.Contains("0|sink after main"),
    $"status={sinkAfterMain} captured=[{string.Join(" ; ", lines)}] (stats {mainStatsBefore})");

// A RUNAWAY SCRIPT MUST BE REFUSED, NOT KILL THE HOST: this is the property an embedded engine is bought for. TWO
// SHAPES, because the two engines spend different resources: a COMPILED recursion uses the VM's heap frames (measured:
// 2,000,000 deep answers status 0, which is the documented property), while a TREE-WALKED one uses the native stack,
// which is exactly what would take a host process down, and there it must be the engine's own catchable guard.
lines.Clear();
var deepVm = runText(v,
    Utf8("int Down(int n) { if (n == 0) { return 0; } return Down(n - 1) + 1; } print('r=' + Down(2000000));"),
    nint.Zero);
Check("2,000,000 deep on the VM answers (its frames are on the heap, not the stack)",
    deepVm == 0 && lines.Contains("0|r=2000000"), $"status={deepVm} [{ResultOf(v)}] {string.Join(" ; ", lines)}");

lines.Clear();
var deepTree = runText(v,
    Utf8(
        "VmSetEnabled(false);\nint DownTree(int n) { if (n == 0) { return 0; } return DownTree(n - 1) + 1; }\nprint('t=' + DownTree(2000000));"),
    nint.Zero);
var deepTreeResult = ResultOf(v);
Check("a tree-walked runaway comes back as the depth guard rather than crashing the host",
    deepTree == 1 && deepTreeResult.Contains("depth"), $"status={deepTree} [{deepTreeResult}]");
Check("and the session survives it",
    runText(v, Utf8("print('after runaway');"), nint.Zero) == 0 && lines.Contains("0|after runaway"),
    $"status={SessionError(v)}");

// A CANCEL THAT LANDS INSIDE `sleep`: the engine polls the token between its 100 ms chunks, so this is the shape a host
// uses for "wait until I say stop".
var sleeping = -1;
var sleeper = new Thread(() => { sleeping = runText(v, Utf8("while (true) { sleep(50); }"), nint.Zero); })
{
    IsBackground = true
};
sleeper.Start();
Thread.Sleep(400);
var sleepAsked = cancelFn(v);
sleeper.Join(20000);
Check("slc_cancel stops a loop that is inside sleep", sleepAsked == 0 && sleeping == 1
                                                                      && ResultOf(v).Contains("\"Cancelled\":true"),
    $"asked={sleepAsked} status={sleeping} [{ResultOf(v)}]");

// FREE WHILE A RUN IS IN FLIGHT IS REFUSED, with the reason, and the handle stays valid.
var longRunStatus = -1;
var longRun = new Thread(() =>
{
    longRunStatus = runText(v, Utf8("var i = 0; while (i < 60) { i = i + 1; sleep(25); } print('long done');"),
        nint.Zero);
})
{
    IsBackground = true
};

longRun.Start();
Thread.Sleep(400);
var freeWhileBusy = free(v);
var busyReason = SessionError(v);
Check("slc_free refuses while a run is in flight, naming why", freeWhileBusy == 3 && busyReason.Contains("in progress"),
    $"status={freeWhileBusy} [{busyReason}]");
longRun.Join(30000);

Check("the run it refused to abandon still finished", longRunStatus == 0, $"status={longRunStatus} [{ResultOf(v)}]");

Check("the session frees", free(v) == 0, SessionError(v));

// THE SQLITE DOOR IS EXPORTED UNDER TWO NAMES, so both spellings a user can type work: the short `init` and the
// name SQLite DERIVES from the file when no entry point is given (`sqlite3_scriptlangcore_init`, the basename without
// its extension and without a leading `lib`, lowercased). Both land on the same body, and a host that finds neither
// gets `no such function` from the shell, so each is asserted here rather than assumed from the source.
Check("the SQLite door exports the short entry point",
    NativeLibrary.TryGetExport(lib, "init", out var initShort) && initShort != nint.Zero,
    "init");

Check("the SQLite door also exports the name SQLite derives from the file",
    NativeLibrary.TryGetExport(lib, "sqlite3_scriptlangcore_init", out var initLong) && initLong != nint.Zero,
    "sqlite3_scriptlangcore_init");

// ── THE HOST OBJECT AND THE ASYNCHRONOUS VERBS (embed ABI 4) ─────────────────────────────────────────────────────────
// A host that loads this engine INSIDE the application it wants to drive hands its own object over (`slc_adopt`) and a
// script reaches it with `Com.Host()`. The interesting half cannot be checked here (it needs a real host application, so
// `bindings\vba-check.ps1` drives Excel for it); what a harness CAN pin is the container's contract, which is what a host
// codes against: the adoption refuses an empty pointer by name, an asynchronous run is reported through `slc_poll`, its
// verdict lands where a synchronous one does, and `Com.Host()` answers NULL (not an error) when nothing was adopted.
var hostSession = create(nint.Zero);
Check("a session opens for the host-object contract", hostSession > 0, CreateError());

var adoptNothing = adopt(hostSession, nint.Zero);
Check("slc_adopt refuses an empty pointer, by name", adoptNothing == 2 && SessionError(hostSession).Contains("ObjPtr"),
    $"status={adoptNothing} [{SessionError(hostSession)}]");

var idlePoll = poll(hostSession);
Check("slc_poll answers 0 on an idle session", idlePoll == 0, $"poll={idlePoll}");

var nullAnswer = runText(hostSession, Utf8("var a = Com.Run(fun(scope) { return Com.Host(); }); return (a == null ? 'null' : 'object');"), nint.Zero);
var nullVerdict = ResultOf(hostSession);
Check("Com.Host() answers null when nothing was adopted, rather than refusing",
    nullAnswer == 0 && nullVerdict.Contains("\"Result\":\"null\""), $"status={nullAnswer} [{nullVerdict}]");

var strictAnswer = runText(hostSession, Utf8("Com.Run(fun(scope) { return Com.Host(true); });"), nint.Zero);
Check("Com.Host(true) answers the REASON instead, naming how a host adopts",
    strictAnswer == 1 && SessionError(hostSession).Contains("slc_adopt"), $"status={strictAnswer} [{SessionError(hostSession)}]");

var asyncStatus = runTextAsync(hostSession, Utf8("var total = 0; for (var i = 0; i < 400000; i = i + 1) { total = total + i; } return total;"), nint.Zero);
Check("an asynchronous run is ACCEPTED at once (0, not the run's status)", asyncStatus == 0, $"status={asyncStatus} [{SessionError(hostSession)}]");

var sawRunning = false;
for (var waited = 0; waited < 200 && poll(hostSession) == 1; waited++)
{
    sawRunning = true;
    Thread.Sleep(10);
}
var asyncVerdict = ResultOf(hostSession);
Check("the asynchronous run reports itself through slc_poll and lands its verdict", asyncVerdict.Contains("\"Ok\":true"),
    $"pollSawRunning={sawRunning} [{asyncVerdict}]");

// A RUN THAT AWAITS AT THE FILE'S TOP LEVEL SUSPENDS, which is what makes the asynchronous verbs worth using: the run
// is in flight (poll 1) for the whole awaited call, its verdict lands afterwards, and a cancel issued while it is
// suspended finishes the run at once instead of one awaited call late (the shape a cancel used to have).
var suspendStatus = runTextAsync(hostSession, Utf8("var ms = await Task.Delay(300); return ms;"), nint.Zero);
var pollsWhileSuspended = 0;
for (var waited = 0; waited < 200; waited++)
{
    var p = poll(hostSession);
    if (p == 0) break;
    if (p == 1) pollsWhileSuspended++;
    Thread.Sleep(10);
}
var suspendVerdict = ResultOf(hostSession);
Check("a run that awaits at the file's top level is in flight for the whole await, then answers its value",
    suspendStatus == 0 && pollsWhileSuspended > 0 && suspendVerdict.Contains("\"Result\":300"),
    $"status={suspendStatus} polls={pollsWhileSuspended} [{suspendVerdict}]");

var suspendedStatus = runTextAsync(hostSession, Utf8("var ms = await Task.Delay(2000); return ms;"), nint.Zero);
for (var waited = 0; waited < 100 && poll(hostSession) != 1; waited++) Thread.Sleep(5);
var askedSuspend = cancelFn(hostSession);
var idleAfterCancel = -1;
for (var waited = 0; waited < 100; waited++)
{
    if (poll(hostSession) == 0) { idleAfterCancel = waited * 10; break; }
    Thread.Sleep(10);
}
var cancelledVerdict = ResultOf(hostSession);
Check("a cancel lands on a SUSPENDED run at once, not one awaited call later",
    suspendedStatus == 0 && askedSuspend == 0 && idleAfterCancel >= 0 && cancelledVerdict.Contains("\"Cancelled\":true"),
    $"status={suspendedStatus} asked={askedSuspend} idleAfter={idleAfterCancel}ms [{cancelledVerdict}]");

Check("the session frees after an asynchronous run", free(hostSession) == 0, SessionError(hostSession));

// ── P6: THE COM SERVER (DllGetClassObject + IDispatch, with NO registration) ─────────────────────────────────────
// The in-proc server is a second door onto the same session: Office reaches it with CreateObject. This proves the door
// opens WITHOUT the registry, which is what makes the check deterministic on a machine that never ran the installer:
// the class factory is asked for by CLSID directly, exactly as COM itself would after reading one registry value.
// The CLSID below is compared with src/Host/ComServer.cs, setup/com-register.reg and the installer by
// tools/check-com-registration.sh, so a hand edit in one of the four cannot pass unnoticed.
Console.WriteLine();
Console.WriteLine("── the COM server (DllGetClassObject + IDispatch) ──");

var comClsid = new Guid("4A9D69C4-D9E6-4914-B7DF-ABC442BF5096");
var iidUnknown = new Guid("00000000-0000-0000-C000-000000000046");
var iidDispatch = new Guid("00020400-0000-0000-C000-000000000046");
var iidClassFactory = new Guid("00000001-0000-0000-C000-000000000046");
var iidNull = Guid.Empty;

var getClassObject = Export<DllGetClassObjectFn>("DllGetClassObject");
var canUnloadNow = Export<DllCanUnloadNowFn>("DllCanUnloadNow");

var strangerClsid = new Guid("11111111-2222-3333-4444-555555555555");
var strangerResult = getClassObject(ref strangerClsid, ref iidClassFactory, out _);
Check("DllGetClassObject refuses a CLSID that is not ours (CLASS_E_CLASSNOTAVAILABLE)",
    strangerResult == unchecked((int)0x80040111), $"hr=0x{strangerResult:X8}");

var factoryResult = getClassObject(ref comClsid, ref iidClassFactory, out var factory);
Check("DllGetClassObject hands out an IClassFactory for the engine's CLSID", factoryResult == 0 && factory != nint.Zero,
    $"hr=0x{factoryResult:X8}");

var createInstance = Marshal.GetDelegateForFunctionPointer<FactoryCreateInstanceFn>(Slot(factory, 3));
var dispatchResult = createInstance(factory, nint.Zero, ref iidDispatch, out var dispatch);
Check("IClassFactory::CreateInstance answers an IDispatch", dispatchResult == 0 && dispatch != nint.Zero,
    $"hr=0x{dispatchResult:X8}");

Check("DllCanUnloadNow says S_FALSE (1) while an object is alive", canUnloadNow() == 1, $"hr={canUnloadNow()}");

var getIdsOfNames = Marshal.GetDelegateForFunctionPointer<GetIDsOfNamesFn>(Slot(dispatch, 5));
var comInvoke = Marshal.GetDelegateForFunctionPointer<ComInvokeFn>(Slot(dispatch, 6));

// THE MEMBERS ARE RESOLVED BY NAME, and case does not matter: that is what late binding in VBA does, and a name the
// server does not know must answer DISPID_UNKNOWN rather than a wrong id.
var versionId = DispIdOf("version");
Check("GetIDsOfNames resolves a member case-insensitively", versionId == 1, $"dispid={versionId}");
Check("GetIDsOfNames answers DISPID_UNKNOWN for a name no member has", DispIdOf("NoSuchMember") == -1, "");

var (versionText, versionHr) = InvokeMember(versionId);
Check("Version answers the engine's line through IDispatch",
    versionHr == 0 && versionText.Contains($"embed ABI {AbiExports.Revision}"), versionText);

var (printed, runHr) = InvokeMember(DispIdOf("RunText"), "print(2 + 2);");
Check("RunText runs a snippet and returns what it PRINTED", runHr == 0 && printed == "4", $"hr=0x{runHr:X8} [{printed}]");

var (verdict, _) = InvokeMember(DispIdOf("LastResult"));
Check("LastResult is the engine's own verdict JSON", verdict.Contains("\"Status\":0"), verdict);

// A function the run declared, called with an argument that is NOT a string: Eval marshals VARIANTs, and the script
// side of the same converter the engine's COM CLIENT half uses.
InvokeMember(DispIdOf("RunText"), "int twice(int n) { return n * 2; }");
var (evalAnswer, evalHr) = InvokeMember(DispIdOf("Eval"), "twice", 21);
Check("Eval calls a function the run left behind, with a numeric argument", evalHr == 0 && evalAnswer == "42",
    $"hr=0x{evalHr:X8} [{evalAnswer}]");

// THE SHAPE A VBA CALL SENDS, and the reason a VBA `Engine.Eval(name, arguments(0))` answered error 13 while this
// harness passed: a late-bound call passes a Variant argument as VT_VARIANT|VT_BYREF and a typed variable as
// VT_x|VT_BYREF, and the first version of the server only knew by-value shapes (DISP_E_TYPEMISMATCH, which VBA
// reports as "Type mismatch"). Both shapes are pinned here, so the fix cannot be lost without Excel.
var (evalByRef, evalByRefHr) = InvokeMember(DispIdOf("Eval"), "twice", new ByRefVariant(21));
Check("Eval accepts the BY-REFERENCE VARIANT a VBA call sends (VT_VARIANT|VT_BYREF)",
    evalByRefHr == 0 && evalByRef == "42", $"hr=0x{evalByRefHr:X8} [{evalByRef}]");
var (evalTypedByRef, evalTypedHr) = InvokeMember(DispIdOf("Eval"), "twice", new TypedByRefInt(21));
Check("Eval accepts a TYPED by-reference argument (VT_I4|VT_BYREF)",
    evalTypedHr == 0 && evalTypedByRef == "42", $"hr=0x{evalTypedHr:X8} [{evalTypedByRef}]");

var (_, failedHr) = InvokeMember(DispIdOf("RunText"), "this is not valid (");
var (failureText, _) = InvokeMember(DispIdOf("LastError"));
Check("a failing snippet answers and parks the reason in LastError", failedHr == 0 && failureText.Length > 0,
    failureText);

var (freedText, freedHr) = InvokeMember(DispIdOf("Free"));
var (afterFree, _) = InvokeMember(versionId);
Check("Free tears the session down, and a later call says so instead of crashing",
    freedHr == 0 && freedText == "freed" && afterFree.Contains("has been freed"),
    $"[{freedText}] [{afterFree}]");

var release = Marshal.GetDelegateForFunctionPointer<ComReleaseFn>(Slot(dispatch, 2));
release(dispatch);
Check("releasing the last reference leaves nothing for DllCanUnloadNow to keep alive", canUnloadNow() == 0,
    $"hr={canUnloadNow()}");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "HARNESS OK" : $"HARNESS FAILED: {failures} check(s)");
Environment.ExitCode = failures == 0 ? 0 : 1;
return;

string CreateError() => Ask((buffer, c) => lastError(0, buffer, c));

string SessionError(long h) => Ask((buffer, c) => lastError(h, buffer, c));

string SessionStats(long h) => Ask((buffer, c) => stats(h, buffer, c));

string ResultOf(long h) => Ask((buffer, c) => lastResultFn(h, buffer, c));

void Sink(long h, int channel, nint utf8, nint user)
{
    var txt = Marshal.PtrToStringUTF8(utf8) ?? "";
    lock (lines)
        lines.Add($"{channel}|{txt}");
}

string CompileBundle(string sourcePath, string bundlePath, string extra)
{
    var psi = new ProcessStartInfo(engineExe, $"--compile \"{sourcePath}\" \"{bundlePath}\" {extra}")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit(120000);
    return $"exit={proc.ExitCode} out=[{stdout.Trim()}] err=[{stderr.Trim()}]";
}

int RunBytes(long handle, byte[] data)
{
    var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
    try
    {
        return runBundleBytes(handle, pinned.AddrOfPinnedObject(), data.Length);
    }
    finally
    {
        pinned.Free();
    }
}

static nint Utf8(string? text) => text is null ? nint.Zero : Marshal.StringToCoTaskMemUTF8(text);

static string Ask(Func<nint, int, int> call)
{
    var needed = call(nint.Zero, 0);
    if (needed <= 0)
        return "";

    var buf = Marshal.AllocCoTaskMem(needed + 1);
    try
    {
        call(buf, needed + 1);
        return Marshal.PtrToStringUTF8(buf) ?? "";
    }
    finally
    {
        Marshal.FreeCoTaskMem(buf);
    }
}

T Export<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? " | " + detail : "")}");
    if (!ok)
        failures++;
}

// ── the three readers the COM leg needs ──────────────────────────────────────────────────────────────────────────

/// <summary>One slot of a COM object's vtable: `*(void***)object` is the table, and the index is the method's position
/// in it (IUnknown 0-2, IDispatch 3-6).</summary>
nint Slot(nint comObject, int index) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), index * nint.Size);

/// <summary>`GetIDsOfNames` for ONE name, which is how VBA resolves `engine.RunText`. The name goes over as an
/// LPOLESTR (UTF-16), and DISPID_UNKNOWN (-1) is the answer for anything the server does not know.</summary>
int DispIdOf(string member)
{
    var names = Marshal.StringToHGlobalUni(member);
    try
    {
        var id = -1;
        return getIdsOfNames(dispatch, ref iidNull, ref names, 1, 0, ref id) == 0 ? id : -1;
    }
    finally
    {
        Marshal.FreeHGlobal(names);
    }
}

/// <summary>One `Invoke`, with the arguments in the CALLER's order: the block below reverses them, because COM puts the
/// LAST argument at `rgvarg[0]`. Strings go over as BSTR, numbers as VT_I4, and the answer is read as a BSTR the
/// caller owns (freed here with SysFreeString, which is what the server documented).</summary>
(string Text, int Hr) InvokeMember(int dispId, params object?[] callArguments)
{
    var count = callArguments.Length;
    var block = Marshal.AllocCoTaskMem(ComVariant.Size * Math.Max(count, 1));
    var owned = new List<nint>();
    var ownedBlocks = new List<nint>();
    try
    {
        for (var i = 0; i < count; i++)
        {
            var value = callArguments[count - 1 - i];          // reversed on purpose: see the summary above
            var variant = new ComVariant();
            if (value is string text)
            {
                variant.vt = ComVariant.VT_BSTR;
                variant.bstrVal = Marshal.StringToBSTR(text);
                owned.Add(variant.bstrVal);
            }
            else if (value is int number)
            {
                variant.vt = ComVariant.VT_I4;
                variant.iVal = number;
            }
            else if (value is ByRefVariant byRef)
            {
                var inner = Marshal.AllocCoTaskMem(ComVariant.Size);
                ownedBlocks.Add(inner);
                Marshal.StructureToPtr(new ComVariant { vt = ComVariant.VT_I4, iVal = byRef.Value }, inner, false);
                variant.vt = (ushort)(ComVariant.VT_VARIANT | ComVariant.VT_BYREF);
                variant.llVal = inner;
            }
            else if (value is TypedByRefInt typed)
            {
                var inner = Marshal.AllocCoTaskMem(sizeof(int));
                ownedBlocks.Add(inner);
                Marshal.WriteInt32(inner, typed.Value);
                variant.vt = (ushort)(ComVariant.VT_I4 | ComVariant.VT_BYREF);
                variant.llVal = inner;
            }
            else
            {
                variant.vt = ComVariant.VT_EMPTY;
            }
            Marshal.StructureToPtr(variant, block + i * ComVariant.Size, false);
        }

        var parameters = new ComDispparams
        {
            rgvarg = count == 0 ? nint.Zero : block,
            rgdispidNamedArgs = nint.Zero,
            cArgs = (uint)count,
            cNamedArgs = 0
        };
        var result = new ComVariant();
        var hr = comInvoke(dispatch, dispId, ref iidNull, 0, 1, ref parameters, ref result, nint.Zero, nint.Zero);
        return (TakeBStr(ref result), hr);
    }
    finally
    {
        foreach (var bstr in owned) Marshal.FreeBSTR(bstr);
        foreach (var inner in ownedBlocks) Marshal.FreeCoTaskMem(inner);
        Marshal.FreeCoTaskMem(block);
    }
}

/// <summary>Reads a BSTR result and releases it: a host MUST, or every call leaks the answer.</summary>
string TakeBStr(ref ComVariant variant)
{
    if (variant.vt != ComVariant.VT_BSTR || variant.bstrVal == nint.Zero) return "";
    var text = Marshal.PtrToStringBSTR(variant.bstrVal) ?? "";
    Marshal.FreeBSTR(variant.bstrVal);
    variant.bstrVal = nint.Zero;
    variant.vt = ComVariant.VT_EMPTY;
    return text;
}

/// <summary>An argument a late-bound VBA call sends as `VT_VARIANT|VT_BYREF`: a `Variant` variable, a ParamArray
/// element included, is passed by reference. The harness allocates the inner VARIANT it points at.</summary>
internal sealed record ByRefVariant(int Value);

/// <summary>An argument sent as `VT_I4|VT_BYREF`: a TYPED variable passed ByRef.</summary>
internal sealed record TypedByRefInt(int Value);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void LineCb(long handle, int channel, nint utf8, nint user);

// ── the COM leg's types ──────────────────────────────────────────────────────────────────────────────────────────
// VARIANT is 24 bytes on x64 (16 on x86, which this leg is not published for); only the members the curated surface
// reads and writes are named, all at the offsets COM defines: `vt` first, the value union at offset 8.

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct ComVariant
{
    internal const int Size = 24;
    internal const ushort VT_EMPTY = 0;
    internal const ushort VT_I4 = 3;
    internal const ushort VT_BSTR = 8;
    internal const ushort VT_VARIANT = 12;
    internal const ushort VT_BYREF = 0x4000;

    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public int iVal;
    [FieldOffset(8)] public nint bstrVal;
    [FieldOffset(8)] public nint llVal;
}

/// <summary>`DISPPARAMS`, with `rgvarg` FIRST and the arguments in REVERSE order: `rgvarg[0]` is the call's LAST
/// argument, which is the one COM rule this leg exists to exercise.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ComDispparams
{
    public nint rgvarg;
    public nint rgdispidNamedArgs;
    public uint cArgs;
    public uint cNamedArgs;
}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int DllGetClassObjectFn(ref Guid rclsid, ref Guid riid, out nint ppv);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int DllCanUnloadNowFn();

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int FactoryCreateInstanceFn(nint self, nint outer, ref Guid riid, out nint ppv);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int GetIDsOfNamesFn(nint self, ref Guid riid, ref nint names, uint count, uint lcid, ref int dispIds);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ComInvokeFn(nint self, int dispId, ref Guid riid, uint lcid, ushort flags,
                                 ref ComDispparams parameters, ref ComVariant result, nint exceptionInfo, nint argErr);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate int ComReleaseFn(nint self);