namespace ScriptLangCore;

/// <summary>WHAT A HOST ASKED FOR when it opened a session, parsed from one JSON object so the C ABI needs no
/// struct layout agreement (a struct in a shared header is a versioning promise; a JSON object is not).
///
/// EVERY KEY IS OPTIONAL and every default is the one that surprises least:
///   "home"    the engine's own folder (its `assets\`, `tools\` and the name-bound natives such as sqlite3.dll).
///             ABSENT means "beside the host executable", which is where a host that deployed those files itself
///             already put them.
///   "image"   what `Application.FilePath` reports. ABSENT means the HOST's own image, which is the truth about the
///             running program and the wrong answer for "which engine am I".
///   "args"    what `Application.Args` reports, WITHOUT the executable's own path. ABSENT means NO arguments: the
///             host's own command line is another program's flags and must never leak into a script.
///   "thread"  "auto" (default), "own" or "host", see <see cref="EmbedSession"/>.
///   "dirs"    the directories the SCRIPT's files must live in, each an existing directory. ABSENT means no
///             confinement. A path outside them is refused by name, naming the list; a host that runs untrusted input
///             names the one folder it staged the script's data in.
///   "deny"    the capabilities this session does NOT have, from <see cref="SessionPolicy.Known"/> (`file`, `process`,
///             `network`, `ui`, `com`, `clipboard`). ABSENT means all of them. A denied call refuses by name, naming
///             the capability and the option, rather than failing as "unknown member".
/// An unknown key is REFUSED rather than ignored, for the reason the source-mode flags are: a host that typed
/// "threads" would otherwise get the default and believe it had configured something. A "dirs" entry that is not a
/// directory, or a "deny" entry that is not a capability, is refused the same way and lists what IS accepted.</summary>
internal sealed class EmbedOptions
{
    internal string? Home { get; private init; }
    internal string? ImagePath { get; private init; }
    internal IReadOnlyList<string>? Arguments { get; private init; }
    internal string ThreadMode { get; private init; } = "auto";

    /// <summary>The sandbox this session runs under, or null when it named neither "dirs" nor "deny" — the CLI's own
    /// situation, where every member call and every path keeps its historical meaning.</summary>
    internal SessionPolicy? Policy { get; private init; }

    internal static EmbedOptions? Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return new EmbedOptions();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "the options must be a JSON object, for example {\"home\":\"C:\\\\engine\"}";
                return null;
            }

            string? home = null, image = null;
            var mode = "auto";
            IReadOnlyList<string>? args = null;
            var directories = new List<string>();
            var denied = new List<string>();
            foreach (var option in doc.RootElement.EnumerateObject())
            {
                switch (option.Name.ToLowerInvariant())
                {
                    case "home":
                    case "image":
                        if (option.Value.ValueKind != JsonValueKind.String)
                        {
                            error = $"\"{option.Name}\" must be a string path";
                            return null;
                        }
                        if (option.Name.Equals("home", StringComparison.OrdinalIgnoreCase)) home = option.Value.GetString();
                        else image = option.Value.GetString();
                        break;

                    case "args":
                        if (option.Value.ValueKind != JsonValueKind.Array)
                        {
                            error = "\"args\" must be an array of strings";
                            return null;
                        }
                        var list = new List<string>();
                        foreach (var item in option.Value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String)
                            {
                                error = "\"args\" must be an array of strings";
                                return null;
                            }
                            list.Add(item.GetString() ?? "");
                        }
                        args = list;
                        break;

                    case "thread":
                        if (option.Value.ValueKind != JsonValueKind.String)
                        {
                            error = "\"thread\" must be \"auto\", \"own\" or \"host\"";
                            return null;
                        }
                        mode = (option.Value.GetString() ?? "").ToLowerInvariant();
                        if (mode is not ("auto" or "own" or "host"))
                        {
                            error = $"\"thread\" is \"{mode}\", which is not \"auto\", \"own\" or \"host\"";
                            return null;
                        }
                        break;

                    case "dirs":
                        if (option.Value.ValueKind != JsonValueKind.Array)
                        {
                            error = "\"dirs\" must be an array of directory paths";
                            return null;
                        }
                        foreach (var item in option.Value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String)
                            {
                                error = "\"dirs\" must be an array of directory paths";
                                return null;
                            }
                            var given = item.GetString() ?? "";
                            string full;
                            try { full = Path.GetFullPath(given); }
                            catch (Exception ex)
                            {
                                error = $"\"dirs\" names '{given}', which is not a usable path: " + FriendlyExceptionMessage(ex);
                                return null;
                            }
                            // A directory the machine does not have is refused HERE rather than at the first script
                            // path: a typo that silently confined everything to nowhere would look like a script bug.
                            if (!Directory.Exists(full))
                            {
                                error = $"\"dirs\" names '{full}', which is not a directory this machine has";
                                return null;
                            }
                            directories.Add(full);
                        }
                        break;

                    case "deny":
                        if (option.Value.ValueKind != JsonValueKind.Array)
                        {
                            error = "\"deny\" must be an array of capability names";
                            return null;
                        }
                        foreach (var item in option.Value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String)
                            {
                                error = "\"deny\" must be an array of capability names";
                                return null;
                            }
                            var given = item.GetString() ?? "";
                            // ONE statement of the vocabulary (SessionPolicy.Known), so the option, the refusals and the
                            // page cannot disagree about what exists.
                            if (!Array.Exists(SessionPolicy.Known, k => string.Equals(k, given, StringComparison.OrdinalIgnoreCase)))
                            {
                                error = $"\"deny\" names '{given}', which is not a capability. The capabilities are "
                                        + string.Join(", ", SessionPolicy.Known);
                                return null;
                            }
                            denied.Add(given);
                        }
                        break;

                    default:
                        error = $"unknown option \"{option.Name}\". The options are home, image, args, thread, dirs and deny";
                        return null;
                }
            }

            return new EmbedOptions
            {
                Home = home,
                ImagePath = image,
                Arguments = args,
                ThreadMode = mode,
                Policy = directories.Count > 0 || denied.Count > 0 ? SessionPolicy.Create(denied, directories) : null,
            };
        }
        catch (Exception ex)
        {
            // The ONE message map, so a malformed payload cannot reach a host as a resource key (the AOT build runs
            // with InvariantGlobalization and UseSystemResourceKeys, and the framework's own JSON sentences then
            // arrive as keys rather than as prose).
            error = "the options are not usable JSON: " + FriendlyExceptionMessage(ex);
            return null;
        }
    }
}
