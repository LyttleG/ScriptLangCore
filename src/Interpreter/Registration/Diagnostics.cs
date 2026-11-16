namespace ScriptLangCore;

/// <summary>THE registry of the engine's run-configuration gates, so that a gate cannot exist outside it.
///
/// Each entry NAMES the gate (its environment spelling is SLC_&lt;Name&gt;, derived from the name here so the two
/// cannot drift), says whether it is a FLAG or a VALUE, gives the value this process uses when its variable says
/// nothing, and READs and WRITEs the engine field the gate backs. `Diagnostics.Traces()` lists every entry with its
/// state, `Diagnostics.Trace(name, ...)` flips one, `LogTraces` flips the TRACE ones together, and the engine's own
/// fields take their initial value from here, so the environment variable is only ever the STARTING value.
///
/// WHY ONE LIST, and what it replaced: three gates used to read their variable themselves and each with its own
/// idea of what "on" means (`SLC_CARRIED_STATEMENTS` accepted "1" or "true", `SLC_TOPLEVEL_VM` accepted anything
/// except "0"/"false", and the traces required exactly "1"), and the two path switches and the frame cap were
/// invisible to `Diagnostics.Traces()` while the traces were listed. Measured cost of the split: a reader could not
/// ask what the process had configured, and the same string meant different things per gate.
///
/// TWO DECISIONS THIS FILE RECORDS:
///  * the value rule is ONE rule for every flag: "1" is on, "0" and "false" are off, and ANYTHING ELSE is the
///    entry's own default. So a stray non-empty value cannot switch a trace on (what the traces always required),
///    and `SLC_TOPLEVEL_VM=false` / `SLC_CARRIED_STATEMENTS=0` are the documented opt-outs of the two gates whose
///    default is ON.
///  * VM_MAXFRAMES is a VALUE and sits LAST, because "on" has no meaning for it: it is reported as its number, and
///    its default is NOT computed here (it is platform-derived and reads Interpreter.PhysicalMemoryBytes, which
///    must not run while this type is initialising: see Vm.DefaultVmMaxFrames and Vm.EnvMaxFrames).</summary>
internal static class Diagnostics
{
    /// <summary>The one gate the script can set. `Diagnostics.LogTraces(true)` turns every TRACE on.</summary>
    internal static bool All;

    /// <summary>A gate is a FLAG (on/off) or a VALUE (a number).</summary>
    internal enum GateKind { Flag, Value }

    /// <summary>One gate: what it is called, what kind it is, what it defaults to, and how to read and write the
    /// engine field it backs. `Set` is null for a gate whose state lives only in its environment variable, or which
    /// the process fixes at startup: flipping one is then REFUSED with the reason rather than silently ignored.</summary>
    internal sealed record Gate(string Name, GateKind Kind, object? Default,
        Func<object?> Get, Action<object?>? Set, bool InLogTraces = false);

    /// <summary>The trace names the engine knows, in the order a report lists them. Derived from the gates, so a
    /// name cannot be listed without a gate and a gate cannot exist unlisted.</summary>
    internal static string[] Names => Gates.Select(g => g.Name).ToArray();

    /// <summary>One rule for every flag: "1" is on, "0" and "false" are off, anything else is the caller's
    /// fallback (the gate's own default).</summary>
    internal static bool EnvFlag(string name, bool fallback)
    {
        var s = Environment.GetEnvironmentVariable("SLC_" + name);
        if (s == null) return fallback;
        if (s == "1") return true;
        if (s == "0" || StringComparer.OrdinalIgnoreCase.Equals(s, "false")) return false;
        return fallback;
    }

    /// <summary>One rule for every value: a positive whole number, or null when the variable says nothing usable
    /// (so the caller keeps its own default). The same shape `EnvMaxFrames` used, now shared.</summary>
    internal static int? EnvNumber(string name)
    {
        var s = Environment.GetEnvironmentVariable("SLC_" + name);
        return s is { Length: > 0 } && int.TryParse(s, out var v) && v > 0 ? v : null;
    }

    /// <summary>The initial value of a flag-backed gate, from its own entry: the environment variable when it says
    /// something the rule understands, otherwise the entry's default. The engine's fields are initialised here so
    /// the default is stated ONCE, in the registry.</summary>
    internal static bool InitialFlag(string name)
    {
        var gate = Find(name);
        if (gate is not { Kind: GateKind.Flag })
            throw new InvalidOperationException($"'{name}' is not a flag gate");
        return EnvFlag(name, gate.Default is bool b && b);
    }

    internal static readonly List<Gate> Gates =
    [
        // The TRACES, in the order a report lists them. BAIL_TRACE and COERCE_TRACE are LAST among them on purpose:
        // diagnostics_trace_test pins a trace's state BY INDEX (Traces()[1] is STACK_TRACE, [5] is NAMED_TRACE), so
        // appending keeps every index a suite already reads. Inserting BAIL_TRACE second broke five of that suite's
        // checks on a corpus run, which is what put it last.
        new("VM_TRACE", GateKind.Flag, false, () => Vm.Trace, v => Vm.Trace = v is bool b && b, InLogTraces: true),
        new("STACK_TRACE", GateKind.Flag, false, () => Interpreter.StackTraceEnabled,
            v => Interpreter.StackTraceEnabled = v is bool b && b, InLogTraces: true),
        new("STACK_MAP", GateKind.Flag, false, () => Interpreter.StackMapEnabled,
            v => Interpreter.StackMapEnabled = v is bool b && b, InLogTraces: true),
        new("DISABLE_GUARDS", GateKind.Flag, false, () => Interpreter.GuardsDisabled,
            v => Interpreter.GuardsDisabled = v is bool b && b, InLogTraces: true),
        new("CORO_TRACE", GateKind.Flag, false, () => Interpreter.CoroTraceEnabled,
            v => Interpreter.CoroTraceEnabled = v is bool b && b, InLogTraces: true),
        new("NAMED_TRACE", GateKind.Flag, false, () => Interpreter.NamedTraceEnabled,
            v => Interpreter.NamedTraceEnabled = v is bool b && b, InLogTraces: true),
        new("BAIL_TRACE", GateKind.Flag, false, () => Interpreter.BailTraceEnabled,
            v => Interpreter.BailTraceEnabled = v is bool b && b, InLogTraces: true),
        // COERCE_TRACE reports every CLR-constructor candidate and whether the argument values fit it, which is how a
        // choice between same-arity overloads is explained (see SelectClrConstructor). It has no engine field of its
        // own, so it is read from the variable (plus the master switch), and there is nothing to flip per name.
        new("COERCE_TRACE", GateKind.Flag, false,
            () => All || EnvFlag("COERCE_TRACE", false), null, InLogTraces: true),
        // ── appended after the traces, so no index moves ──
        // THE TWO PATH SWITCHES. Each also has a member of its own family (VmSetTopLevelEnabled,
        // VmSetStatementCarrier), which works on the SAME field, so the registry is a second door onto one state
        // rather than a second state. They are deliberately NOT flipped by LogTraces: that switch means "every
        // trace", and neither of these is a trace.
        new("TOPLEVEL_VM", GateKind.Flag, true, () => Interpreter.TopLevelVmEnabled,
            v => Interpreter.TopLevelVmEnabled = v is bool b && b),
        // ON by default since 2026-10-21, and the flip is what five rounds of bundle work bought: the strip frontier
        // moves from 471 stripped / 43 refused of 514 suites to 501 / 13, and every suite it newly strips has been
        // measured against its own source (tools/bundle-parity.sh, the whole corpus): the divergences that gate it
        // were each a PRE-EXISTING bundle defect the old refusal had been hiding, and they are closed rather than
        // reasoned (a generic call that names its type arguments, a generic body resolving its own `T`, an
        // `out _` discard, a tuple name in a generic position, an `async Task<int>` that falls off, a carried
        // statement writing the global where the body reads a local slot, and two capture-free lambdas at the same
        // position in two snippets). `SLC_CARRIED_STATEMENTS=0` (or `VmSetStatementCarrier(false)`) is the OPT-OUT,
        // for comparing the two states in one process.
        // WHAT IT IS: a top-level statement the compiler cannot model is CARRIED as its own source text and runs on
        // the tree, which is what a source run does for that statement, so a stripped bundle answers like its file
        // for the `is`-pattern, `var` and call families.
        new("CARRIED_STATEMENTS", GateKind.Flag, true, () => Interpreter.StatementCarrierEnabled,
            v => Interpreter.StatementCarrierEnabled = v is bool b && b),
        // THE FRAME CAP, a VALUE, and the ONLY gate whose default is not stated here (see the class comment): its
        // Set writes BOTH caps, which is what its environment variable has always done.
        new("VM_MAXFRAMES", GateKind.Value, null, () => Vm.VmMaxFrames,
            v => Vm.SetMaxFrames(v is int n && n > 0 ? n : 0)),
        // THE SINGLE-INSTANCE LOCK'S NAME, appended LAST for the reason the note above gives: a trace's state is
        // pinned BY INDEX by diagnostics_trace_test. It exists because the name is long (a sanitized full path) and
        // the default message is one short line: a reader who needs the name to inspect the lock switches this on.
        new("SINGLE_INSTANCE", GateKind.Flag, false, () => Interpreter.SingleInstanceTrace,
            v => Interpreter.SingleInstanceTrace = v is bool b && b, InLogTraces: true),
        // THE PARKED-WAIT CENSUS, appended LAST for the same index reason: it reports ONE line per await that PARKED
        // (the run waited on a thread inside Task.Wait) with its shape and duration, which is how "which awaits do
        // real scripts park on" is answered by a corpus run instead of by reading the compiler. The same numbers are
        // readable without the log through `Diagnostics.AsyncParks([site])`.
        new("ASYNC_TRACE", GateKind.Flag, false, () => Interpreter.AsyncTraceEnabled,
            v => Interpreter.AsyncTraceEnabled = v is bool b && b, InLogTraces: true)
    ];

    internal static Gate? Find(string name)
        => Gates.FirstOrDefault(g => StringComparer.OrdinalIgnoreCase.Equals(g.Name, name));

    /// <summary>True when the named gate is on. Kept for the trace fields and for a caller that only has a name:
    /// the master switch OR the variable, which is the shape this had before the registry and is still right for a
    /// flag whose state is its variable (`COERCE_TRACE`). A caller that holds a gate's FIELD asks the field.</summary>
    internal static bool On(string name) => All || EnvFlag(name, false);

    /// <summary>The state of every gate, in the order of Gates, read through each gate's OWN accessor (so a per-name
    /// flip is reported, and the frame cap reports its number).</summary>
    internal static List<object?> Report()
    {
        var rows = new List<object?>();
        foreach (var g in Gates)
        {
            var value = g.Get();
            rows.Add(g.Kind == GateKind.Value
                ? $"{g.Name}={value}"
                : $"{g.Name}={(value is true ? "on" : "off")}");
        }
        return rows;
    }
}

/// <summary>The script surface for the registry above, in the same shape Gc.cs uses for its namespace.
/// `Diagnostics.LogTraces(true)` sets every TRACE, `Diagnostics.LogTraces()` reports whether they are on,
/// `Diagnostics.Trace(name, on)` flips ONE gate (a flag with a boolean, the frame cap with a number), and
/// `Diagnostics.Traces()` lists every gate with its state, so a script can see what it turned on rather than
/// trusting the call.</summary>
internal partial class Interpreter
{
    private void RegisterDiagnosticsFunctions()
    {
        var ns = new Namespace(_comparer);

        ns.AddMethod("LogTraces", args =>
        {
            if (args.Length == 0) return Diagnostics.All;
            var want = args.Length > 0 && args[0] is bool b && b;
            Diagnostics.All = want;
            // The engine's own fields, through the registry, so the set that "everything" means is stated once.
            // A gate that is not a trace (the two path switches, the frame cap) is NOT touched: this switch means
            // "every trace", and turning the carrier on or moving the frame cap as a side effect would be a
            // different promise.
            foreach (var g in Diagnostics.Gates)
                if (g.InLogTraces && g.Set != null) g.Set(want);
            return want;
        });

        // The per-gate form, because "everything" is not always usable: SLC_STACK_MAP dumps the thread's whole
        // stack geometry on EVERY call, so a script that wants the named-argument trace alone should not have to
        // take forty lines per call with it. The name is the SLC_* variable's own name, so the two spellings agree.
        ns.AddMethod("Trace", args =>
        {
            if (args.Length < 2) return Usage();
            var name = $"{args[0]}".ToUpperInvariant();
            var gate = Diagnostics.Find(name);
            if (gate == null) return "unknown gate '" + name + "': name one of " + string.Join(", ", Diagnostics.Names);
            if (gate.Set == null)
                return $"{name} has no state to flip in this process: it is read from SLC_{name} only";
            // A FLAG takes a boolean, a VALUE takes a whole number: the kind decides, so a caller cannot set a cap
            // to "on" and get the default back. ZERO is the value kind's reset, which is what makes a suite that
            // lowers the frame cap able to put it back (see Vm.SetMaxFrames).
            if (gate.Kind == Diagnostics.GateKind.Value)
            {
                if (args[1] is not int n || n < 0)
                    return $"{name} takes a whole number (0 restores the default)";
                gate.Set(n);
                return gate.Get();
            }
            var on = args[1];
            if (on is not bool b3)
                return $"{name} takes a boolean (on/off)";
            gate.Set(b3);
            return b3;
        });

        // THE PARKED-WAIT CENSUS, and it is a script member because a suite cannot see a parked wait any other way:
        // the VALUE is the same, only the thread is occupied, so the only other witness is a wall-clock bound. One
        // name with two shapes, the `VmStats()` idiom: no argument answers the TOTAL, a site name answers that shape's
        // count (0 for a shape nothing parked on), and the sites are `await`, `await foreach`, `await using`,
        // `http stream` and `Task.Wait` (a script's own call). The ASYNC_TRACE gate prints the same events with their
        // durations, so a corpus run says which shapes matter before anything is changed for them.
        ns.AddMethod("AsyncParks", args =>
        {
            if (args.Length > 0 && args[0] is string site)
                return Interpreter.ParkedAwaitCounts.TryGetValue(site, out var count) ? count : 0;
            var total = 0;
            foreach (var parked in Interpreter.ParkedAwaitCounts.Values) total += parked;
            return total;
        });

        ns.AddMethod("Traces", _ => Diagnostics.Report());

        CurrentFrame.Scopes.Define("Diagnostics", ns);

        static string Usage() => "Diagnostics.Trace(name, on): name one of " + string.Join(", ", Diagnostics.Names);
    }
}
