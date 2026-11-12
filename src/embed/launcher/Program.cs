using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

// THE SMALL EXE: a SHORTCUT onto the engine's own entry point. It holds NO engine code, decides NOTHING about its
// arguments, and loads `ScriptLangCore.dll` from its own folder, so `ScriptLangCore.Launcher.exe <args>` does what
// `ScriptLangCore.exe <args>` does: no arguments opens the REPL, a `.slc` runs, a `.slcbc` runs, `--compile`,
// `--dump-bytecode`, `--repl [script]` and `--mcp` all work, because the argument list is handed to the engine's own
// dispatch (`slc_main`) rather than reinterpreted here. That is the whole point of the shape: nothing in this file can
// drift from the exe's behavior, and a new flag is inherited the day it lands.
//
// USAGE: ScriptLangCore.Launcher.exe [the engine's own arguments, exactly]
//
// WHAT IT DOES, in order: load the DLL beside itself (explaining how to get one if it is missing), start a session with
// that folder as the engine's `home` (its `assets\`, `tools\` and name-bound natives such as `sqlite3.dll` live there),
// register an output callback that writes channel 0 to stdout and channel 1 to stderr, hand its own arguments to
// `slc_main`, and exit with the code that answered. It sets NO session arguments of its own: `Application.Args` then
// reads this process's command line, which is what the exe's `Application.Args` reads too.

// THE TWO BUNDLE MAGICS, as the wire writes them (a little-endian int32 at offset 0), declared before any use because a
// top-level statement cannot refer to a local declared below it. Both are spelled with their byte readings beside them,
// because the names mislead: 0x43424353 reads "SCBC" and 0x4C424C53 reads "SLBL".
const int programBundleMagic = 0x43424353; // SCBC
const int libraryBundleMagic = 0x4C424C53; // SLBL
// WHERE THE ENGINE IS, in four steps, most explicit first: SLC_ENGINE_DLL (an exact file) → BESIDE THIS EXE → SLC_ENGINE_HOME
// (a directory) → each PATH entry, in order.
//
// BESIDE THE EXE COMES BEFORE THE ENVIRONMENT, and that order is a MEASUREMENT rather than a preference: SLC_ENGINE_HOME is
// set machine-wide by the installer, so with the environment first a PACKAGED APP THAT SHIPS ITS OWN ScriptLangCore.dll was
// made to run the INSTALLED engine instead (measured: `launcher: engine from C:\Program Files\ScriptLangCore\bin\...` while
// its own copy sat beside it), which silently changes the engine version an app was built against. Beside-the-exe first
// makes a self-contained app self-contained, and a launcher with no engine of its own still finds an installed one through
// the environment.
//
// A DECLARED location that does not hold the engine is REFUSED rather than skipped: a typo must not silently load a
// different engine than the one the user named.
const string dllName = "ScriptLangCore.dll";
// A PACKAGED APP HAS NO CONSOLE, so an early refusal has to be SAID IN A WINDOW. Reported from a live run: a package
// started without its DLL exited with code 3 and showed NOTHING AT ALL, which reads as a program that does nothing.
// Every refusal below therefore goes through ONE door, `Fatal`, which keeps the stderr lines (a console run, a script and
// `tools/embed-check.sh` read those, and its legs pin exit 3 plus the text) and adds a box when there is NO console
// attached, which is the double-clicked-GUI case and nothing else. The box is `MessageBoxTimeoutW` with a timeout, so an
// unattended window cannot block forever: it closes itself, and a user dismisses it sooner with Enter.
const string fatalTitle = "ScriptLangCore: the engine DLL is missing";
string? engineDll = null;
var declaredFile = Environment.GetEnvironmentVariable("SLC_ENGINE_DLL");
var declaredHome = Environment.GetEnvironmentVariable("SLC_ENGINE_HOME");
if (!string.IsNullOrWhiteSpace(declaredFile))
{
    if (!File.Exists(declaredFile))
    {
        // A STALE VARIABLE IS THE COMMON SHAPE OF THIS REFUSAL, not a typo: an uninstall (or a moved folder) leaves the
        // value naming a file nothing recreates, and the refusal used to end by telling the user to do the one thing it
        // could not know was already done: "copy ScriptLangCore.dll beside this exe", in a folder that held one.
        // Reported from a live run whose engine sat beside the exe the whole time, blocked by a `C:\seed\...` value its
        // shell had inherited from before the uninstall. So the answer depends on what IS there: a copy beside the exe
        // means the variable is the only obstacle and clearing it is the whole fix; no copy means the advice is the old
        // one. The refusal itself is unchanged (a declared location is obeyed, not consulted), and so is the exit code.
        var beside = Path.Combine(AppContext.BaseDirectory, dllName);
        var nextStep = File.Exists(beside)
            ? $"There IS a {dllName} beside this exe ({beside}), so the variable is all that stops it: clear SLC_ENGINE_DLL " +
              "in the Environment Variables dialog and start a new console, or point it at the DLL you mean."
            : $"Point SLC_ENGINE_DLL at a DLL that exists, or clear it and put {dllName} beside this exe " +
              $"({AppContext.BaseDirectory}).";
        Fatal(fatalTitle,
            $"This app needs {dllName}, and SLC_ENGINE_DLL names '{declaredFile}', which does not exist.\n\n" +
            "A declared location is obeyed rather than skipped, so this app will not fall back to another copy " +
            $"while SLC_ENGINE_DLL is set.\n\n{nextStep}");
        return 3;
    }

    engineDll = Path.GetFullPath(declaredFile);
}
else
{
    // Beside this exe FIRST: a packaged app that carries its engine must use the one it carries (see the note above).
    var beside = Path.Combine(AppContext.BaseDirectory, dllName);
    if (File.Exists(beside))
    {
        engineDll = Path.GetFullPath(beside);
    }
    else if (!string.IsNullOrWhiteSpace(declaredHome))
    {
        var candidate = Path.Combine(declaredHome, dllName);
        if (!File.Exists(candidate))
        {
            Fatal(fatalTitle,
                $"This app needs {dllName}. There is none beside this exe, and SLC_ENGINE_HOME names '{declaredHome}', " +
                $"which holds no {dllName}.\n\n" +
                $"Point SLC_ENGINE_HOME at the folder that holds the DLL, with its assets\\, tools\\ and sqlite3.dll beside it.");
            return 3;
        }

        engineDll = Path.GetFullPath(candidate);
    }
    else
    {
        // PATH, in its own order, so a directory the user declared there wins over another one further along it.
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var raw in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir;
            try
            {
                dir = Path.GetFullPath(raw.Trim().Trim('"'));
            }
            catch
            {
                continue;
            }

            var pathCandidate = Path.Combine(dir, dllName);
            if (File.Exists(pathCandidate))
            {
                engineDll = pathCandidate;
                break;
            }
        }
    }
}

if (engineDll == null)
{
    Fatal(fatalTitle,
        $"This app is packaged around {dllName}, and that file is not there.\n\n" +
        "Looked for it:\n" +
        $"  {Path.Combine(AppContext.BaseDirectory, dllName)}\n" +
        "  the SLC_ENGINE_DLL / SLC_ENGINE_HOME you set (none was set)\n" +
        "  every folder on PATH\n\n" +
        $"Copy {dllName} beside this exe, with its assets\\, tools\\ and sqlite3.dll, or set SLC_ENGINE_HOME to the folder that holds it.");
    return 3;
}

// THE ENGINE'S HOME IS THE FOLDER ITS DLL CAME FROM, not this exe's: that folder is where its `assets\`, `tools\` and
// `sqlite3.dll` were published, so a shared or PATH-found engine still finds its own files. A launcher beside a DIFFERENT
// copy of the DLL therefore runs the one it appears to run.
var engineFolder = Path.GetDirectoryName(engineDll) ?? AppContext.BaseDirectory;
if (!string.Equals(Path.GetFullPath(engineDll), Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), dllName),
        StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"launcher: engine from {engineDll}");
}

var dllPath = engineDll;

nint lib;
try
{
    lib = NativeLibrary.Load(dllPath);
}
catch (Exception ex)
{
    // A DLL that is there but will not load (another architecture, a truncated copy, a blocked file) used to end the
    // process as an unhandled exception, which a packaged app shows as nothing at all.
    Fatal("ScriptLangCore: the engine DLL will not load",
        $"{dllName} is at {dllPath}, and Windows refused to load it.\n\n{ex.Message}");
    return 3;
}

var version = Export<VersionFn>("slc_version");
var create = Export<CreateFn>("slc_create");
var free = Export<FreeFn>("slc_free");
var setOutput = Export<SetOutputFn>("slc_set_output");
var main = Export<MainFn>("slc_main");
var mainOwns = Export<OwnsFn>("slc_main_owns");
var runText = Export<RunTextFn>("slc_run_text");
var runBundleBytes = Export<RunBytesFn>("slc_run_bundle_bytes");
var lastError = Export<LastErrorFn>("slc_last_error");
var lastResult = Export<LastResultFn>("slc_last_result");

static string Ask(Func<nint, int, int> call)
{
    var needed = call(nint.Zero, 0);
    if (needed <= 0) return "";
    var buffer = Marshal.AllocCoTaskMem(needed + 1);
    try
    {
        call(buffer, needed + 1);
        return Marshal.PtrToStringUTF8(buffer) ?? "";
    }
    finally
    {
        Marshal.FreeCoTaskMem(buffer);
    }
}

// THE ENGINE SAYS NOTHING ABOUT ITSELF BY DEFAULT, because a packaged app's stderr belongs to the app: this line used to
// print on every run, and a reader reported it as noise (it is also invisible in a double-clicked GUI app, so it could not
// help anybody there). Asking for it is one environment variable, which is how the launcher's own knobs already work
// (`SLC_ENGINE_HOME`, `SLC_ENGINE_DLL`), and it is what tells a stale DLL from a fresh one when a report looks wrong.
// THE VERDICT LINE AT THE BOTTOM ASKS THE SAME QUESTION, one variable and one answer, so "the launcher talks about
// itself" has ONE switch rather than two that could drift.
var informative =
    Environment.GetEnvironmentVariable("SLC_LAUNCHER_INFO") is { Length: > 0 } and not "0" and not "false";
if (informative)
{
    Console.Error.WriteLine("launcher: " + Ask((b, c) => version(b, c)));
}

// `home` is the ENGINE's folder, and nothing else is set: the arguments stay this process's own, so a script's
// `Application.Args` reads what the exe's would read (the command line minus the executable).
// `args` is this process's OWN argument list, verbatim, because that is what the engine's `Application.Args` reports
// under the exe (measured: [0] is the script's path and the script's own arguments follow). Leaving it out would mean
// NO arguments, which is a different thing: a script would see an empty list where the exe gives it its command line.
var quotedArgs = string.Join(",", args.Select(Escape));
var options = "{\"home\":\"" + engineFolder.Replace("\\", "\\\\") + "\",\"args\":[" + quotedArgs + "]}";
var optionsPointer = Marshal.StringToCoTaskMemUTF8(options);
long handle;
try
{
    handle = create(optionsPointer);
}
finally
{
    Marshal.FreeCoTaskMem(optionsPointer);
}

if (handle <= 0)
{
    Fatal("ScriptLangCore: the engine refused to start",
        $"The engine is at {dllPath}, and it refused to start a session:\n\n" + Ask((b, c) => lastError(0, b, c)));
    return 3;
}

var sinkPointer = Marshal.GetFunctionPointerForDelegate((LineCb)Sink);
setOutput(handle, sinkPointer, nint.Zero);

// WHAT AM I? A PACKAGED exe carries its program in its OWN resource, put there by `tools/slc-pack.slc`, and that is
// the one thing this file decides for itself: is there a payload. Nothing else about a packaged run is inferred, and an
// exe with the empty slot (the one the build ships) behaves exactly like the launcher it is.
// THE RULE, in one line: a payload runs, UNLESS the first argument is one the ENGINE's own dispatch owns (`--compile`,
// `--repl`, `--mcp`, `--dump-bytecode`, `--mcp-bc`), which then takes over exactly as it would on the unpacked launcher.
// Asking the engine means the list has one statement, in the engine, and a flag added there works in a packaged app the
// day it lands. Anything else the app is given is the app's own, which is the whole point of packaging it.
var payload = ReadOwnPayload();
var wantsEngine = false;
foreach (var candidate in args.Length > 0 ? args : [""])
{
    var pointer = Marshal.StringToCoTaskMemUTF8(candidate);
    try
    {
        if (mainOwns(pointer) == 1)
        {
            wantsEngine = true;
            break;
        }
    }
    finally
    {
        Marshal.FreeCoTaskMem(pointer);
    }
}

int status;
if (payload is { Length: > 0 } && !wantsEngine)
{
    status = RunPayload(handle, payload);
}
else
{
    // THE ARGUMENTS, as one JSON array: this is the only thing this file knows about a command line.
    var argvPointer = Marshal.StringToCoTaskMemUTF8("[" + string.Join(",", args.Select(Escape)) + "]");
    try
    {
        status = main(handle, argvPointer);
    }
    finally
    {
        Marshal.FreeCoTaskMem(argvPointer);
    }
}

// THE VERDICT IS THE RUN'S EXIT CODE, exactly as the CLI's is, and the JSON is an OPTION (the same `SLC_LAUNCHER_INFO`
// switch the version line uses). It used to be written unconditionally, which was WRONG for a host that runs a script
// which CHOOSES its code: measured, a suite's child (`exit(3)`) put two stderr lines on a child that the suite asserts
// is silent, so `vm_exit_parity` read `"Error":false` inside the JSON as an error report and `single_instance_test`
// counted three lines where it expects one. `slc_run_file`/`slc_last_result` are the door for a host that needs the
// reason, and this switch is the door for a person running the exe.
if (status != 0 && informative)
{
    Console.Error.WriteLine($"launcher: the run answered {status}: {Ask((b, c) => lastError(handle, b, c))}");
    Console.Error.WriteLine(Ask((b, c) => lastResult(handle, b, c)));
}

Console.Out.Flush();
free(handle);
NativeLibrary.Free(lib);
return status;

T Export<T>(string name) => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(lib, name));

// Channel 0 is the script's `print` and channel 1 is the engine's own log lines; the callback runs on the session's
// thread, and a REPL's prompt and `= value` echoes go to the process's stdout on their own, which is parity with the exe.
void Sink(long l, int channel, IntPtr utf8, IntPtr nint)
{
    var text = Marshal.PtrToStringUTF8(utf8) ?? "";
    var stream = channel == 0 ? Console.Out : Console.Error;
    stream.WriteLine(text);
}

// THE PAYLOAD IN THIS EXE'S OWN RESOURCE, read through Win32: the build ships an EMPTY `SLCPAYLOAD` entry (payload.rc),
// and `tools/slc-pack.slc` fills it with a script's text or a bundle's bytes. Size 0 means "not packaged", which is why
// the slot exists at all: the reader never has to ask whether a resource entry is absent or the exe is old.
//   resource type:  RT_RCDATA (10)   name: "SLCPAYLOAD"   language: neutral
byte[]? ReadOwnPayload()
{
    var kernel = NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "GetModuleHandleW");
    var find = Marshal.GetDelegateForFunctionPointer<FindResourceFn>(
        NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"), "FindResourceW"));
    var sizeOf =
        Marshal.GetDelegateForFunctionPointer<SizeOfFn>(NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"),
            "SizeofResource"));
    var load = Marshal.GetDelegateForFunctionPointer<LoadFn>(NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"),
        "LoadResource"));
    var lockRes =
        Marshal.GetDelegateForFunctionPointer<LockFn>(NativeLibrary.GetExport(NativeLibrary.Load("kernel32.dll"),
            "LockResource"));
    var getModule = Marshal.GetDelegateForFunctionPointer<GetModuleFn>(kernel);

    var self = getModule(nint.Zero); // our own image, whatever its path is now
    if (self == nint.Zero) return null;
    var name = Marshal.StringToCoTaskMemUni("SLCPAYLOAD");
    try
    {
        var info = find(self, name, 10);
        if (info == nint.Zero) return null;
        var size = (int)sizeOf(self, info);
        if (size <= 0) return null;
        var pointer = lockRes(load(self, info));
        if (pointer == nint.Zero) return null;
        var buffer = new byte[size];
        Marshal.Copy(pointer, buffer, 0, size);
        return buffer;
    }
    finally
    {
        Marshal.FreeCoTaskMem(name);
    }
}

// A PAYLOAD IS RUN BY WHAT IT IS, read from its first four bytes and never from a name it does not have: the two
// bundle magics mean "compiled", anything else is the SCRIPT'S TEXT. The run's directory is this exe's folder, so `./`
// means "beside the exe"; `Application.Args` is this process's command line, exactly as a `.slc` file would see it.
int RunPayload(long session, byte[] payLoad)
{
    // The two magics as INTEGERS, which is how the wire writes them and how `slc_run_file` tests a file: the byte
    // spellings are not what a reader guesses from the names (0x43424353 little-endian is "SCBC", not "SLCB", and the
    // codec's own comment says the latter), so a byte comparison here silently classified a `.slcbc` as source text and
    // answered a control-character parse error.
    var magic = payLoad.Length >= 4 ? BitConverter.ToInt32(payLoad, 0) : 0;
    var isBundle = magic == programBundleMagic || magic == libraryBundleMagic;
    if (isBundle)
    {
        var data = Marshal.AllocCoTaskMem(payLoad.Length);
        try
        {
            Marshal.Copy(payLoad, 0, data, payLoad.Length);
            return runBundleBytes(session, data, payLoad.Length);
        }
        finally
        {
            Marshal.FreeCoTaskMem(data);
        }
    }

    // Source text: NUL-terminated UTF-8, and the exe's own path is handed over as the run's NAME so `Script.Directory()`
    // and every `./` path point at the folder the packaged exe sits in.
    var text = Encoding.UTF8.GetString(payLoad);
    var textPointer = Marshal.StringToCoTaskMemUTF8(text);
    var namePointer = Marshal.StringToCoTaskMemUTF8(Environment.ProcessPath ?? "");
    try
    {
        return runText(session, textPointer, namePointer);
    }
    finally
    {
        Marshal.FreeCoTaskMem(textPointer);
        Marshal.FreeCoTaskMem(namePointer);
    }
}

static string Escape(string value)
{
    var sb = new StringBuilder(value.Length + 8);
    sb.Append('"');
    foreach (var c in value)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
                break;
        }
    }

    sb.Append('"');
    return sb.ToString();
}

// ONE DOOR FOR AN EARLY REFUSAL, and the window is the whole point of it: a packaged app has NO CONSOLE, so before this
// change a package started without its DLL exited 3 and showed nothing at all. The words go to stderr FIRST and always (a
// console run, a script, and `tools/embed-check.sh`, whose legs pin both exit 3 and the file name, read them), and a BOX
// follows when nobody would ever see those words. THREE SHAPES decide that, and the middle one is why this is not simply
// "a box when no console is attached":
//   * a SHELL shares its console with this process (GetConsoleProcessList counts more than one), so the lines stay on
//     screen: no box, which is also what keeps an unattended run from blocking;
//   * a DOUBLE-CLICKED CONSOLE IMAGE gets a console of its OWN (the count is one) and that console disappears with the
//     process, so the lines are unreadable: box. Measured before this rule: double-clicking the launcher without
//     `ScriptLangCore.dll` exited 3 and showed nothing at all, while the text was written correctly;
//   * NO CONSOLE AT ALL means either a GUI image started from Explorer (box) or a run whose streams are CAPTURED, and
//     then GetFileType answers pipe or disk and the reader sees the text (no box).
// `MessageBoxTimeoutW` rather than `MessageBoxW` because a window nobody is there to dismiss must not hang a machine: it
// closes itself after the timeout, and a user dismisses it sooner with Enter. MB_ICONERROR | MB_SETFOREGROUND |
// MB_TOPMOST keeps it above the app that was just started.
void Fatal(string caption, string text, int timeoutMs = 20000)
{
    foreach (var line in text.Split('\n'))
    {
        Console.Error.WriteLine("launcher: " + line.TrimEnd());
    }

    try
    {
        var kernel = NativeLibrary.Load("kernel32.dll");

        T NativeExport<T>(string name) =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(kernel, name));

        bool showBox;
        if (NativeExport<GetConsoleWindowFn>("GetConsoleWindow")() != nint.Zero)
        {
            // 1 means this process is ALONE on the console, so Windows created it for us, and it dies with us. 0 means the
            // call failed, and a box is the safer answer than silence. (A native buffer rather than `stackalloc`, because
            // this file is not an `unsafe` context and one fatal path does not justify making it one.)
            var list = NativeExport<GetConsoleProcessListFn>("GetConsoleProcessList");
            var pids = Marshal.AllocHGlobal(8 * sizeof(uint));
            try
            {
                showBox = list(pids, 8) <= 1;
            }
            finally
            {
                Marshal.FreeHGlobal(pids);
            }
        }
        else
        {
            // FILE_TYPE_PIPE is 3 and FILE_TYPE_DISK is 1: either way something is reading stderr.
            var type = NativeExport<GetFileTypeFn>("GetFileType")(NativeExport<GetStdHandleFn>("GetStdHandle")(-11));
            showBox = type != 3 && type != 1;
        }

        if (!showBox)
        {
            return;
        }

        var user = NativeLibrary.Load("user32.dll");
        var box = Marshal.GetDelegateForFunctionPointer<MessageBoxTimeoutFn>(
            NativeLibrary.GetExport(user, "MessageBoxTimeoutW"));
        var textPointer = Marshal.StringToCoTaskMemUni(text);
        var captionPointer = Marshal.StringToCoTaskMemUni(caption);
        try
        {
            box(nint.Zero, textPointer, captionPointer, 0x00000010u | 0x00010000u | 0x00040000u, 0, (uint)timeoutMs);
        }
        finally
        {
            Marshal.FreeCoTaskMem(textPointer);
            Marshal.FreeCoTaskMem(captionPointer);
        }
    }
    catch
    {
        // A box that cannot be shown must not change the exit code: the stderr lines above are already out.
    }
}

internal delegate int VersionFn(nint buffer, int capacity);

internal delegate long CreateFn(nint optionsJson);

internal delegate int FreeFn(long handle);

internal delegate int SetOutputFn(long handle, nint callback, nint user);

internal delegate int MainFn(long handle, nint argvJson);

internal delegate int OwnsFn(nint argument);

internal delegate int RunTextFn(long handle, nint source, nint name);

internal delegate int RunBytesFn(long handle, nint data, int count);

// The Win32 entries `Fatal` needs, resolved the same way every other export here is (a raw pointer plus a delegate):
// `GetConsoleWindow` answers a handle for a process that has a console and 0 for a GUI one, `GetConsoleProcessList`
// counts the processes attached to that console (1 means this one alone, i.e. the console was created for us and will
// vanish with us), and the `GetStdHandle` + `GetFileType` pair tells a captured stream from a real window.
internal delegate nint GetConsoleWindowFn();

internal delegate uint GetConsoleProcessListFn(nint processList, uint count);

internal delegate nint GetStdHandleFn(int index);

internal delegate uint GetFileTypeFn(nint handle);

internal delegate int MessageBoxTimeoutFn(nint hWnd, nint text, nint caption, uint type, ushort languageId,
    uint milliseconds);

internal delegate nint GetModuleFn(nint moduleName);

internal delegate nint FindResourceFn(nint module, nint name, nint type);

internal delegate uint SizeOfFn(nint module, nint info);

internal delegate nint LoadFn(nint module, nint info);

internal delegate nint LockFn(nint data);

internal delegate int LastErrorFn(long handle, nint buffer, int capacity);

internal delegate int LastResultFn(long handle, nint buffer, int capacity);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void LineCb(long handle, int channel, nint utf8, nint user);