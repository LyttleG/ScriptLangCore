namespace ScriptLangCore;

/// <summary>What the memory form of an export returns: the bundle's bytes and the report the file form returns.
/// A record rather than a bare byte[] because the report is where the strip story lives (skipped names, source
/// carried instead, class/struct counts), and a caller with no path to print it beside would otherwise lose it.</summary>
internal sealed record BytecodeResult(byte[] Bytes, string Report);

internal partial class Interpreter
{
    /// <summary>Registers the Bytecode namespace, export a VM-compiled function to a binary blob
    /// and re-load it later into any interpreter (same-version). The blob carries only value data
    /// (consts + TypeInfo) and nested-lambda shells, so no parser/AST is needed to run it.</summary>
    private void RegisterBytecodeFunctions()
    {
        var bcNs = new Namespace(_comparer);

        static VmBytecodeCodec.LibraryMeta LibraryMetaFromDict(IDictionary meta, IReadOnlyList<string>? srcs)
        {
            var metaDict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry e in meta)
                metaDict[Convert.ToString(e.Key) ?? ""] = e.Value;
            // 'entry' key is intentionally ignored, meta is informational for libraries.
            return new VmBytecodeCodec.LibraryMeta(Str("name"), Str("version"), Str("description"), srcs);

            string? Str(string key)
                => metaDict.TryGetValue(key, out var v)
                    ? v as string : null;
        }

        // The SOURCE MODE of an export, from an options dictionary: { "source": "carry" | "strip" | "strict" }.
        //   carry  = the bundle carries the program text, which is what a plain ExportScript has always done;
        //   strip  = strip it when the strip is honest, and carry it (the report saying so) when it is not;
        //   strict = strip it or REFUSE, naming what could not be carried, which is what the CLI's --no-source does.
        // An unknown key or spelling is REFUSED rather than ignored: a silently dropped option is the same surprise
        // the three modes exist to remove.
        static (bool Strip, bool Require) SourceMode(object? opts, string apiName, bool stripByDefault = false)
        {
            if (opts == null) return (stripByDefault, false);
            var dict = opts switch
            {
                IScriptDictionary sd => (IDictionary)sd.Dictionary,
                IDictionary id => id,
                _ => throw new ScriptException($"{apiName}: the options argument expects a dictionary,"
                    + " e.g. {{ \"source\": \"strict\" }}.")
            };
            var strip = stripByDefault;
            var require = false;
            foreach (DictionaryEntry e in dict)
            {
                var key = Convert.ToString(e.Key) ?? "";
                if (!StringComparer.OrdinalIgnoreCase.Equals(key, "source"))
                    throw new ScriptException($"{apiName}: unknown option \"{key}\". The only option is \"source\","
                        + " one of \"carry\", \"strip\" or \"strict\".");
                switch ((e.Value as string)?.ToLowerInvariant())
                {
                    case "carry": strip = false; require = false; break;
                    case "strip": strip = true; require = false; break;
                    case "strict": strip = true; require = true; break;
                    default:
                        throw new ScriptException($"{apiName}: \"source\": {e.Value ?? "null"} is not one of"
                            + " \"carry\", \"strip\" or \"strict\".");
                }
            }
            return (strip, require);
        }

        // THE RUN'S INTERPRETER, because every member below reads or writes per-run state: the file's own functions and
        // program body (ExportScript), the classes a library export carries, the directory a relative path means, and
        // the interpreter a loaded bundle registers into. A namespace is registered by ONE interpreter and
        // `BuiltinBlueprint` then CLONES that object into every later instance, so in an EMBEDDED session `this` is the
        // first interpreter while the run is `Current`: measured through the launcher, `this` had no current directory
        // and `Current` held the run's folder, and `Bytecode.ExportScript("f")` answered "entry function 'f' is not
        // VM-compiled or not found in this script" for a file that had just declared `f`. The engine's standing rule
        // for a namespace member (`Coroutine.Pump` and `Application.DoEvents` learned it the same way).
        Interpreter Run() => Current ?? this;

        bcNs.AddMethod("ExportScript", args =>
        {
            if (args.Length < 1 || args[0] is not string path)
                throw new ScriptException("Bytecode.ExportScript expects (path [, entryName] [, options]).");
            var entryName = args.Length >= 2 ? args[1] as string : null;
            // The options dictionary also fits the entryName slot, so `ExportScript(path, { "source": "strict" })`
            // needs no placeholder null where the caller wanted no entry.
            var opts = args.Length >= 3 ? args[2] : args.Length == 2 && args[1] is not string ? args[1] : null;
            var (strip, require) = SourceMode(opts, "Bytecode.ExportScript");
            return Run().ExportTopLevelToBundle(ResolvePath(path), entryName, strip, require);
        });

        // The memory form of ExportScript: the same bundle, returned instead of written. Both go through
        // ExportTopLevelToBundleBytes, which is the same body the file form writes, so the two cannot drift.
        bcNs.AddMethod("ExportScriptBytes", args =>
        {
            var entryName = args.Length >= 1 ? args[0] as string : null;
            var opts = args.Length >= 2 ? args[1] : args.Length == 1 && args[0] is not string ? args[0] : null;
            var (strip, require) = SourceMode(opts, "Bytecode.ExportScriptBytes");
            var (data, report) = Run().ExportTopLevelToBundleBytes(entryName, strip, require);
            return new BytecodeResult(data, report);
        });

        bcNs.AddMethod("ExportProgram", args =>
        {
            // args: (outPath, source [, entryName] [, options]): a program bundle, STRIPPED by default. The source
            // is compiled in a fresh interpreter, so the caller's own session is untouched, and the bundle runs its
            // top level from bytecode with its declarations as descriptors. { "source": "carry" } asks for the
            // program text instead, and "strict" refuses rather than carrying it when the strip is not honest.
            if (args.Length < 2 || args[0] is not string outPath || args[1] is not string src)
                throw new ScriptException("Bytecode.ExportProgram expects (outPath, source [, entryName] [, options]).");
            var entryName = args.Length >= 3 ? args[2] as string : null;
            var opts = args.Length >= 4 ? args[3] : args.Length == 3 && args[2] is not string ? args[2] : null;
            var (strip, require) = SourceMode(opts, "Bytecode.ExportProgram", stripByDefault: true);
            return Run().ExportProgramWithoutSource(ResolvePath(outPath), src, entryName, strip, require);
        });

        // The memory form of ExportProgram: (source [, entryName] [, options]) with no output path.
        bcNs.AddMethod("ExportProgramBytes", args =>
        {
            if (args.Length < 1 || args[0] is not string src)
                throw new ScriptException("Bytecode.ExportProgramBytes expects (source [, entryName] [, options]).");
            var entryName = args.Length >= 2 ? args[1] as string : null;
            var opts = args.Length >= 3 ? args[2] : args.Length == 2 && args[1] is not string ? args[1] : null;
            var (strip, require) = SourceMode(opts, "Bytecode.ExportProgramBytes", stripByDefault: true);
            var (data, report) = Run().ExportProgramWithoutSourceBytes(src, entryName, strip, require);
            return new BytecodeResult(data, report);
        });

        // The body is ONE local function: the file form adds the write, the memory form returns the bytes, and
        // BOTH hand it args in the file form's shape (the memory form passes a null placeholder for the output
        // path, which is the one slot the body itself never reads).
        // `stringsAreSource` is how the two spellings differ: the FILE form's strings are paths, and the BYTES form's
        // are the script text, encoded here so a caller never has to write Encoding.UTF8.GetBytes for a script it
        // holds. A byte[] spec means the same thing in both, since a buffer can only ever be the text.
        (byte[] Data, string Report) ExportLibraryCore(object?[] args, bool stringsAreSource)
        {
            var self = Current ?? this;   // the RUN's interpreter, for the reason the Run() note above states
            // args: (outPath, meta, scriptOrTuple1 [, scriptOrTuple2 ...])
            //   string        → flat script (namespace "")
            //   (script, ns)  → namespaced script, ScriptTuple with 2 items
            if (args.Length < 3)
                throw new ScriptException("Bytecode.ExportLibrary expects (outPath, meta, scripts... or (script, ns) tuples...).");
            // The meta argument is either a script-facing "Meta" value (VmBytecodeCodec.LibraryMeta,
            // from Bytecode.Meta or a re-exported Z.__meta), an IScriptDictionary (dictionary literal),
            // or a raw System.Collections.IDictionary for host callers.
            var metaObj = args[1] switch
            {
                VmBytecodeCodec.LibraryMeta lm => lm,
                IDictionary id => LibraryMetaFromDict(id, null),
                IScriptDictionary sd => LibraryMetaFromDict(sd.Dictionary, null),
                _ => throw new ScriptException(
                    "Bytecode.ExportLibrary expects (outPath, meta, scripts... or (script, ns) tuples...).")
            };

            // Optional trailing argument: an array/list of top-level VARIABLE NAMES to ignore if
            // unserializable, the explicit escape hatch. The strict default aborts the export on
            // any unserializable variable; listing a name here skips it (with a warning) instead.
            var ignoreVars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var specsEnd = args.Length;
            if (args.Length >= 4 && args[^1] is Array ignoreArr)
            {
                foreach (var n in ignoreArr)
                    if (n is string s) ignoreVars.Add(s);
                specsEnd--;
            }
            else if (args.Length >= 4 && args[^1] is IScriptList ignoreList)
            {
                foreach (var n in ignoreList.ToArray())
                    if (n is string s) ignoreVars.Add(s);
                specsEnd--;
            }

            // Collect (script, ns) specs from the variadic args. A SCRIPT is a path, the script's TEXT, or its text as
            // BYTES: which a STRING means is decided by the MEMBER (see stringsAreSource), so nothing is guessed at
            // from the value, while a byte[] is always the text and a path is always a string in the file form.
            // A spec here is the script TEXT, so a PATH is a mistake worth naming BEFORE it becomes a parse error about
            // a backslash. Measured: `("C:\\...\\m1.slc", "NS")` answered only "Unexpected character '\'".
            bool NamesAnExistingFile(string s)
            {
                try { return s.Length > 0 && !s.Contains('\n') && File.Exists(ResolvePath(s)); }
                catch { return false; }
            }
            object SpecScript(string s, bool asSource)
            {
                if (!asSource) return s;
                if (NamesAnExistingFile(s))
                    throw new ScriptException($"Bytecode.ExportLibraryBytes: '{s}' is an existing FILE, and this member"
                        + " takes the script TEXT. Use Bytecode.ExportLibrary to build from a path, or read the file and"
                        + " pass its text.");
                return Encoding.UTF8.GetBytes(s);
            }
            var specs = new List<(object Script, string Ns)>();
            foreach (var a in args[2..specsEnd])
            {
                switch (a)
                {
                    case string s:
                        specs.Add((SpecScript(s, stringsAreSource), ""));
                        break;
                    case byte[] srcBytes:
                        specs.Add((srcBytes, ""));
                        break;
                    case ScriptTuple { Items: [string script, string ns] }:
                        specs.Add((SpecScript(script, stringsAreSource), ns));
                        break;
                    case ScriptTuple { Items: [byte[] src, string ns] }:
                        specs.Add((src, ns));
                        break;
                    default:
                        throw new ScriptException("Bytecode.ExportLibrary expects (outPath, meta, scripts... or"
                            + " (script, ns) tuples...), where a script is a path or the script's text.");
                }
            }

            var exported = new List<FuncDecl>();
            var exportedNs = new List<string>();
            var exportedClasses = new List<ClassInfo>();
            var exportedStructs = new List<StructDeclStmt>();
            var exportedVars = new List<(string Name, object? Value)>();
            var skipped = new List<string>();
            // Dedupe by FULL name, "Test.add" and "Utils.add" coexist; two "Test.add" error.
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (script, ns) in specs)
            {
                var content = self.CollectLibraryContent(script, ns);
                for (var i = 0; i < content.Funcs.Count; i++)
                {
                    var decl = content.Funcs[i];
                    var declNs = content.FuncNs[i];
                    var fullName = declNs.Length == 0 ? decl.Name : $"{declNs}.{decl.Name}";
                    if (seen.TryGetValue(fullName, out var first))
                        throw new ScriptException(
                            $"Bytecode.ExportLibrary: duplicate function '{fullName}'.");
                    seen[fullName] = script is string scriptPath ? scriptPath : "<bytes>";
                    exported.Add(decl);
                    exportedNs.Add(declNs);
                }
                exportedClasses.AddRange(content.Classes);
                exportedStructs.AddRange(content.Structs);
                skipped.AddRange(content.Skipped);
                foreach (var (name, value) in content.Variables)
                {
                    // Explicitly-ignored names are skipped (the caller opted in, informative
                    // warning, not the confusing silent skip that the strict contract removed).
                    if (ignoreVars.Contains(name))
                    {
                        Logging.Log.Warning("Bytecode.ExportLibrary",
                            $"variable '{name}' is in ignoreVars, so it was skipped (not exported).",
                            Interpreter.CurrentScriptLine, Interpreter.CurrentScriptColumn);
                        continue;
                    }
                    // Unserializable variables ABORT the export (throw, naming the variable) BEFORE
                    // the file is written, a partially-serialized library would silently lose data.
                    // (This reverts the 5dbd74e warn+skip behavior: skip was confusing, the file
                    // existed but the module's top-level state was incomplete.)
                    try { VmBytecodeCodec.ProbeSerializable(value); }
                    catch (InvalidDataException ex)
                    {
                        throw new ScriptException(
                            $"Bytecode.ExportLibrary: variable '{name}' is not serializable ({ex.Message}). " +
                            "Remove or make it serializable before exporting, the export is aborted and no file is written.");
                    }
                    exportedVars.Add((name, value));
                }
            }

            // __meta.Sources = the per-function namespace list ("" = flat), aligned with the directory.
            metaObj = metaObj with { Sources = exportedNs };
            var data = VmBytecodeCodec.SerializeLibrary(exported, exportedNs, metaObj, exportedClasses, exportedStructs, exportedVars);
            var report = $"exported {exported.Count}, skipped {skipped.Count}";
            if (skipped.Count > 0) report += " (" + string.Join(", ", skipped) + ")";
            return (data, report);
        }

        bcNs.AddMethod("ExportLibrary", args =>
        {
            if (args.Length < 3 || args[0] is not string outPath)
                throw new ScriptException("Bytecode.ExportLibrary expects (outPath, meta, scripts... or (script, ns) tuples...).");
            var (data, report) = ExportLibraryCore(args, stringsAreSource: false);
            File.WriteAllBytes(ResolvePath(outPath), data);
            return report;
        });

        // The memory form of ExportLibrary: (meta, scripts... or (script, ns) tuples...) and no output path.
        bcNs.AddMethod("ExportLibraryBytes", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Bytecode.ExportLibraryBytes expects (meta, scripts... or (script, ns) tuples...).");
            var shifted = new object?[args.Length + 1];
            shifted[0] = null;   // the output path slot the shared body does not read
            Array.Copy(args, 0, shifted, 1, args.Length);
            var (data, report) = ExportLibraryCore(shifted, stringsAreSource: true);
            return new BytecodeResult(data, report);
        });

        // The body is ONE local function, the same shape as ExportLibraryCore: the file form adds the write and the
        // memory form returns the bytes, and both hand it args in the file form's shape (the memory form passes a
        // null placeholder for the output path, which is the one slot the body never reads). A LIBRARY here is a
        // path OR the blob's bytes, so a merged library can be built without any library touching the disk.
        // `member` is the name the CALLER was invoked by, so a refusal names a spelling the script can
        // write: the two members share this body, and a message about `MergeLibrary` sent to a script that
        // called `MergeLibraryBytes` sends it looking for the wrong member. Same shape as the JSON option
        // reader, which names the member it was called as.
        (byte[] Data, string Report) MergeLibraryCore(object?[] args, string member)
        {
            // args: (outPath, meta, lib1 [, lib2 ...]): merge compiled .slcbc libraries, each a path or bytes.
            // Meta is caller-supplied (Meta value / dict / IDictionary); 'entry' key ignored.
            if (args.Length < 3)
                throw new ScriptException($"{member} expects (outPath, meta, libPaths...).");
            VmBytecodeCodec.LibraryMeta metaObj = args[1] switch
            {
                VmBytecodeCodec.LibraryMeta lm => lm,
                IDictionary id => LibraryMetaFromDict(id, null),
                IScriptDictionary sd => LibraryMetaFromDict(sd.Dictionary, null),
                _ => throw new ScriptException($"{member} expects (outPath, meta, libPaths...).")
            };

            var allFuncs = new List<FuncDecl>();
            var allNs = new List<string>();
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in args[2..])
            {
                byte[] data;
                string libPath;
                if (a is byte[] libBytes)
                {
                    data = libBytes;
                    libPath = "<bytes>";   // what the messages below name a library that arrived as bytes
                }
                else if (a is string libFile)
                {
                    libPath = libFile;
                    try { data = File.ReadAllBytes(ResolvePath(libFile)); }
                    catch (Exception ex)
                    {
                        throw new ScriptException($"{member}: cannot read '{libFile}', {ex.Message}");
                    }
                }
                else
                {
                    throw new ScriptException($"{member} expects (outPath, meta, libPaths...),"
                        + " each a path or a byte array.");
                }
                var funcs = VmBytecodeCodec.DeserializeLibrary(data, out var err, out _, out var namespaces, out var classes, out var structs, out var vars, out _, out _);
                if (funcs == null)
                    throw new ScriptException($"{member}: '{libPath}', " + err);
                for (var i = 0; i < funcs.Count; i++)
                {
                    var d = funcs[i];
                    var ns = namespaces != null && i < namespaces.Count ? namespaces[i] ?? "" : "";
                    var fullName = ns.Length == 0 ? d.Name : $"{ns}.{d.Name}";
                    if (seen.TryGetValue(fullName, out var first))
                        throw new ScriptException(
                            $"{member}: duplicate function '{fullName}'.");
                    seen[fullName] = libPath;
                    allFuncs.Add(d);
                    allNs.Add(ns);
                }
            }

            // __meta.Sources = the merged per-function namespace list ("" = flat).
            metaObj = metaObj with { Sources = allNs };
            var dataOut = VmBytecodeCodec.SerializeLibrary(allFuncs, allNs, metaObj);
            return (dataOut, $"merged {allFuncs.Count}");
        }

        bcNs.AddMethod("MergeLibrary", args =>
        {
            if (args.Length < 3 || args[0] is not string outPath)
                throw new ScriptException("Bytecode.MergeLibrary expects (outPath, meta, libPaths...).");
            var (data, report) = MergeLibraryCore(args, "Bytecode.MergeLibrary");
            File.WriteAllBytes(ResolvePath(outPath), data);
            return report;
        });

        // The memory form of MergeLibrary: (meta, libs...) and no output path, where each library is a path
        // OR the blob's bytes, so a merged library need not touch the disk at all.
        bcNs.AddMethod("MergeLibraryBytes", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Bytecode.MergeLibraryBytes expects (meta, libs...), each a path or a byte array.");
            var shifted = new object?[args.Length + 1];
            shifted[0] = null;   // the output path slot the shared body does not read
            Array.Copy(args, 0, shifted, 1, args.Length);
            var (data, report) = MergeLibraryCore(shifted, "Bytecode.MergeLibraryBytes");
            return new BytecodeResult(data, report);
        });

        // The SOURCE TEXT a bundle carries, inflated here so a script needs nothing external. The name is
        // GetSource because that is what comes back, the program text, however the file stores it.
        // A whole-script bundle carries its top level; a library carries none of its own and answers "".
        bcNs.AddMethod("GetSource", args =>
        {
            if (args.Length == 0 || args[0] is not (string or byte[]))
                throw new ScriptException("Bytecode.GetSource expects a bundle path or a byte array.");
            byte[] blob;
            if (args[0] is byte[] givenBytes)
            {
                blob = givenBytes;
            }
            else if (args[0] is string givenPath)
            {
                var fullPath = ResolvePath(givenPath);
                if (!File.Exists(fullPath))
                    throw new ScriptException($"Bytecode.GetSource: no such file '{givenPath}'.");
                blob = File.ReadAllBytes(fullPath);
            }
            else
            {
                // Unreachable: the guard above refused everything that is neither a path nor bytes. It exists so
                // the flow is visible to the compiler and a widened guard is caught here, not at a cast.
                throw new ScriptException("Bytecode.GetSource expects a bundle path or a byte array.");
            }

            var source = VmBytecodeCodec.GetSource(blob, out var error);
            if (source == null)
                throw new ScriptException("Bytecode.GetSource: " + error);
            return source;
        });

        bcNs.AddMethod("Meta", args =>
        {
            // Bytecode.Meta(name, version, description) → a LibraryMeta value usable as
            // the metadata argument of Bytecode.ExportLibrary and returned as Z.__meta on import.
            if (args.Length < 1 || args[0] is not string name)
                throw new ScriptException("Bytecode.Meta expects (name [, version, description]).");
            var version = args.Length > 1 ? args[1] as string : null;
            var description = args.Length > 2 ? args[2] as string : null;
            return new VmBytecodeCodec.LibraryMeta(name, version, description, null);
        });

        bcNs.AddMethod("LoadScript", args =>
        {
            if (args.Length < 1 || args[0] is not (string or byte[]))
                throw new ScriptException("Bytecode.LoadScript expects a path or a byte array.");
            // Eager, like the module loader (LoadModuleFromBundle): a script-facing load hands the entry back to be
            // CALLED, and the lazy-body map is interpreter-local state. Left lazy, the shell's empty body ran in any
            // interpreter whose source did not already declare the function, measured: a fresh sub-interpreter
            // reported "Function 'NoExt' must return a value." for `int NoExt() { return 42; }`, and silently did
            // nothing for a void one.
            if (args[0] is byte[] loadBytes)
            {
                var (fromBytes, _) = Run().LoadBundleFromBytes(loadBytes);
                return fromBytes;
            }
            if (args[0] is string loadPath)
            {
                var (fromFile, _) = Run().LoadBundleFromFile(ResolvePath(loadPath));
                return fromFile;
            }
            // Unreachable: the guard above refused everything that is neither. The compiler needs the flow, and a
            // widened guard then fails here instead of at a cast.
            throw new ScriptException("Bytecode.LoadScript expects a path or a byte array.");
        });

        // The memory form of `import "x.slcbc" as Z`: a library in memory becomes a namespace VALUE, because
        // `import` takes its path in the SYNTAX and a blob in memory has none. The alias is the label nested
        // namespaces hang off, the same string `import` passes.
        bcNs.AddMethod("LoadNamespace", args =>
        {
            if (args.Length == 0 || args[0] is not byte[] payload)
                throw new ScriptException("Bytecode.LoadNamespace expects (bytes [, alias]).");
            var alias = args.Length >= 2 ? args[1] as string ?? "" : "";
            return Run().LoadBundleToNamespaceBytes(payload, 0, 0, alias);
        });

        CurrentFrame.Scopes.Define("Bytecode", bcNs);
    }

    /// <summary>Best-effort load-time validation: every named type the bytecode references (script classes,
    /// structs, delegates, casts, is-checks) must be resolvable in THIS interpreter, otherwise the shell
    /// would only fail at call time. Recurses into nested lambda shells. <paramref name="apiName"/> is the
    /// script-facing API prefix used in error messages.
    /// <paramref name="checkTypeNames"/> is FALSE for a PROGRAM bundle being loaded to run: there the shape a name
    /// cannot resolve is one the RUN answers with the engine's own catchable refusal, exactly as the file does, so
    /// a pre-flight that refused the whole bundle was STRICTER than the run it predicts. Measured on
    /// `clr_type_name_test`, whose subject IS an unknown type inside a `try` (the file catches "Type
    /// 'NoSuchTypeXyz' is unknown or undefined", and its stripped bundle could not be loaded at all). It stays TRUE
    /// for an `import`, where a library blob arrives in a DIFFERENT session and naming a dependency it lacks is
    /// worth reporting before any body runs.</summary>
    private void ValidateShellEnvironment(FuncDecl decl, string apiName, bool checkTypeNames = true)
    {
        if (decl.Compiled != null)
            CheckBytecode(decl.Compiled);
        return;

        void CheckBytecode(Bytecode bc)
        {
            // A PROGRAM BUNDLE'S NAMES ARE THE RUN'S TO ANSWER (see the summary above), and a GENERIC
            // DECLARATION's own type parameters are legitimate type references in its body (`T x` in
            // `T Identity<T>(T x)` puts a TypeInfo named "T" in the constant pool). Without the second half a
            // loaded template could not load at all: measured on `clr_type_name_test`, "type 'T' referenced by the
            // bytecode is not available in this environment".
            var typeParams = decl.TypeParameters;
            foreach (var c in bc.Constants)
                // IsKnownTypeName, not IsKnownType: the RICHER chain is the one `typename`, `is` and a type
                // argument resolve through, and it is the only one that knows a name the CARRIED SOURCE
                // DECLARES (which the load recorded before this check ran). With the narrow predicate, a
                // bundle whose body made an `is IShape` test was refused at load for a name the same file
                // declares, so that shape could not be bundled at all; the check keeps its value for a bundle
                // with no source to declare anything, which is what it was written for.
                if (checkTypeNames && c is TypeInfo ti && !string.IsNullOrEmpty(ti.TypeName)
                    // A DOTTED NAME IS NOT CHECKED HERE. It can only be a NAMESPACE-QUALIFIED type (`Z.Test.Animal`) or a
                    // NESTED one, and both resolve at RUN time: `import "./lib.slcbc" as Z` brings its types when the
                    // entry runs, which is AFTER this pre-flight. Refusing it here refused a bundle that runs, the one
                    // thing a pre-flight must never do (measured: crash_lib_types, "type 'Z.Test.Animal' ... not
                    // available in this environment"). A SIMPLE unknown name is still refused: that is the shape this
                    // check exists for.
                    && !ti.TypeName.Contains('.')
                    && !IsKnownTypeName(ti.TypeName)
                    // AND THE SCRIPT-FACING TYPE ALIASES, asked INSENSITIVELY, because a TYPE POSITION is the
                    // one place the runtime itself is insensitive: that table is declared OrdinalIgnoreCase, so
                    // `is consolecolor` and the legacy lowercase `tuple` resolve even in a strict interpreter.
                    // A body carrying either was refused HERE for a name the same run resolves (measured:
                    // `crash_typeof_matrix`'s body names `consolecolor`, `_dup360`'s names `tuple`, and both
                    // loaded and passed once admitted). THE ASYMMETRY WITH IsKnownTypeName IS DELIBERATE and is
                    // the whole point: that predicate also answers "is this loaded NAME a type?", where an
                    // insensitive match turns a lowercase VARIABLE into a type and refuses a bundle that used
                    // to strip (measured on com_provider_test, whose body loads a global spelled `comnode`).
                    && !ScriptTypeRegistry.ContainsKey(ti.TypeName)
                    && !typeParams.Any(tp => string.Equals(tp, ti.TypeName, Comparison)))
                    throw new ScriptException($"{apiName}: type '{ti.TypeName}' referenced by the bytecode is not available in this environment.");
            if (bc.NestedLambdas == null) return;
            foreach (var nl in bc.NestedLambdas)
            {
                if (nl.Compiled != null) CheckBytecode(nl.Compiled);
            }
        }
    }
}
