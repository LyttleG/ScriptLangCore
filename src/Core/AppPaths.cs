namespace ScriptLangCore;

/// <summary>THE ONE ANSWER to "where may this program write". `{app}` is under Program Files on an installed engine,
/// where a normal account can only READ, so anything an engine, a tool or a script has to WRITE (a database, uploads,
/// a log, a generated bundle, a work folder) belongs in the DATA folder and not beside the executable:
///
/// 1. `SLC_DATA_HOME`, when the machine or a host declares it. Obeyed first, because a host that ships its own engine
///    must not be steered into another installation's folder.
/// 2. The install's `data` folder, which `setup/ScriptLangCore.iss` creates and grants `Modify` to the built-in Users
///    group. That is the engine folder's OWN `data` in a repository or published layout (`publish-aot\data`,
///    `bin\Debug\net11.0\data`), and the PARENT's `data` when the engine lives in an installed `bin` folder
///    (`{app}\data` beside `{app}\bin`), because that is where the package puts the two. The distinction is not
///    cosmetic: measured on the shipped install, `{app}\bin\data` is a folder nothing creates and a normal account
///    cannot create under Program Files, so a board's first `Directory.CreateDirectory` was refused with
///    "Access denied" while the granted folder sat one level up.
/// 3. `%LOCALAPPDATA%\ScriptLangCore`, only when the engine folder cannot be named at all (an embedded run whose image
///    path is unknown), because a path that does not exist is worse than a per-user one.
///
/// Nothing here creates, probes or writes anything on its own: resolving the path costs two string operations, so it
/// is safe to register as a script property on every interpreter. <see cref="DataWritable"/> is the question that
/// touches the disk, and it answers it the way the callers need: by creating a SUBFOLDER inside the candidate, writing
/// a file in it, and removing both. A probe that only writes a file in the folder itself cannot tell "this account may
/// write here" from "this account may create and delete a subfolder here", and the second is what an upload folder, a
/// per-run work directory or a bundle output actually needs.</summary>
internal static class AppPaths
{
    /// <summary>The folder name the installer creates and grants.</summary>
    internal const string DataFolderName = "data";

    /// <summary>The engine's folder in an INSTALLED layout: the package puts all four binaries in `{app}\bin` and its
    /// granted data folder in `{app}\data`, so this name is what tells the two apart.</summary>
    internal const string InstalledEngineFolderName = "bin";

    /// <summary>A declared override, obeyed before anything else (an environment variable, so a host or a machine
    /// policy can move every writer at once).</summary>
    internal const string OverrideVariable = "SLC_DATA_HOME";

    private static readonly Lock Gate = new();
    private static string? _dataRoot;
    private static bool? _writable;

    /// <summary>The folder a program may write in. Resolution only: an existing install has it, a fresh one is told to
    /// create it by whoever writes first, and <see cref="DataWritable"/> says whether that worked.</summary>
    internal static string DataRoot()
    {
        lock (Gate) return _dataRoot ??= Resolve();
    }

    private static string Resolve()
    {
        var declared = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(declared))
        {
            try { return Path.GetFullPath(declared.Trim()); } catch (Exception) { /* fall through to the install */ }
        }
        var home = ScriptSession.ResolveHome();
        if (home.Length > 0)
        {
            // The installer's data folder sits BESIDE its `bin` folder, not inside it: `{app}\data` is created and
            // granted Modify, while `{app}\bin\data` is a folder under Program Files that a normal account can neither
            // find nor create. A repository or published layout keeps its data under the engine folder itself.
            var engineFolder = Path.TrimEndingDirectorySeparator(home);
            var installRoot = Path.GetDirectoryName(engineFolder);
            if (installRoot is { Length: > 0 }
                && string.Equals(Path.GetFileName(engineFolder), InstalledEngineFolderName, StringComparison.OrdinalIgnoreCase))
                return Path.Combine(installRoot, DataFolderName);
            return Path.Combine(engineFolder, DataFolderName);
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScriptLangCore");
    }

    /// <summary>Is <see cref="DataRoot"/> really writable, SUBFOLDERS INCLUDED? Asked once per process and cached: the
    /// answer depends on the install and on the account, neither of which a run changes. A false answer is not an
    /// error, it is the caller's cue to fall back (a script can put its files under `%LOCALAPPDATA%` instead), and it
    /// is the one thing an installer cannot verify for the account that will run later.</summary>
    internal static bool DataWritable()
    {
        lock (Gate)
        {
            if (_writable is { } known) return known;
            _writable = ProbeWritable(DataRoot());
            return _writable.Value;
        }
    }

    /// <summary>Can this folder be written in, SUBFOLDERS INCLUDED? Creates `dir` when it is missing, then creates a
    /// uniquely named subfolder, writes a file inside it, and removes the file and the subfolder. Nothing is left
    /// behind on either outcome, and a refusal answers false rather than throwing: this is asked on the way to a first
    /// write, where a caller has a fallback rather than an error to report.</summary>
    internal static bool ProbeWritable(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        var probe = Path.Combine(dir, "_slc_write_probe_" + Guid.CreateVersion7().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(probe);
            var file = Path.Combine(probe, "probe.txt");
            File.WriteAllText(file, "probe");
            File.Delete(file);
            Directory.Delete(probe, false);
            return true;
        }
        catch (Exception)
        {
            // Best effort: a probe that failed halfway may have left the subfolder behind, and that must not turn a
            // false answer into an exception on the caller's side.
            try { if (Directory.Exists(probe)) Directory.Delete(probe, true); } catch (Exception) { }
            return false;
        }
    }
}
