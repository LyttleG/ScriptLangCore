namespace ScriptLangCore;

/// <summary>WHAT THIS BUILD'S C ABI CAN DO, as a bitmask a binding can TEST instead of probing exports one by one and
/// treating a missing one as a crash. ONE PLACE holds the bits: `slc_capabilities` answers this mask, `slc_stats`
/// repeats it in the session's JSON, and `docs/embed/abi.json` publishes the same names and values, which
/// `tools/check-abi-manifest.sh` compares so the three cannot drift.
///
/// A BIT SAYS "the calls behind it are there and mean what the docs say", not "the engine has this ability": this
/// engine can always run a bundle, but only what a build ADVERTISES is a promise a host may rely on. A binding written
/// against ABI 5 therefore drops what an older library lacks instead of failing a lookup in somebody else's program.
///
/// EACH BIT NAMES THE REVISION THAT INTRODUCED IT, because that is the question a binding actually asks: "library
/// says revision N, may I call `slc_adopt`?" Revision 1 was create/free/version/thread mode/stats/error; revision 2
/// added the verbs and the output callback; revision 3 respelled the verdict members in PascalCase; revision 4 added
/// adoption and the asynchronous verbs; revision 5 added `slc_abi_revision` and `slc_capabilities` themselves, which is
/// what makes this mask readable at all.
///
/// THE MASKS ARE STABLE: a bit is never reused for a different meaning, so a binding built against ABI 5 keeps
/// answering the same way against ABI 6. A new ability takes the next free bit and a new revision.</summary>
internal static class EmbedCapabilities
{
    /// <summary>`slc_set_output`, and the line callback with its two channels (since 2).</summary>
    internal const int Output = 1 << 0;

    /// <summary>`slc_call`, and `Result` in the verdict (since 2).</summary>
    internal const int Call = 1 << 1;

    /// <summary>`slc_cancel`, the one verb a host may call while a run is in flight (since 2).</summary>
    internal const int Cancel = 1 << 2;

    /// <summary>`slc_run_bundle` and `slc_run_bundle_bytes`, the compiled-payload verbs (since 2).</summary>
    internal const int Bundles = 1 << 3;

    /// <summary>`slc_main` and `slc_main_owns`, the executable's own argument dispatch (since 2).</summary>
    internal const int Main = 1 << 4;

    /// <summary>`slc_run_text_async`, `slc_run_file_async` and `slc_poll` (since 4).</summary>
    internal const int Async = 1 << 5;

    /// <summary>`slc_adopt`, which registers the host's own object so `Com.Host()` reaches it (since 4).</summary>
    internal const int Adopt = 1 << 6;

    /// <summary>The in-proc COM server (`ScriptLangCore.Session`, `DllGetClassObject`), which only the `Embed`
    /// configuration compiles (`SLC_EMBED_COM`). The ONE build-dependent bit, and the reason a bit says something
    /// about THIS image rather than about the engine (since 5).</summary>
    internal const int Com = 1 << 7;

    /// <summary>The session options `"dirs"` and `"deny"` are honored, so a host can confine a script's paths to the
    /// directories it names and forbid the process, network, UI, COM and clipboard families
    /// (<see cref="SessionPolicy"/>). A build that answered a mask without this bit would silently ignore both options,
    /// which is the failure a binding must be able to see (since 5).</summary>
    internal const int Sandbox = 1 << 8;

    /// <summary>Everything this build answers, which is what `slc_capabilities` returns. The COM bit is compiled in
    /// with the class factory itself, so an exe leg cannot advertise a door it does not have.</summary>
    internal static int Bits()
    {
        var bits = Output | Call | Cancel | Bundles | Main | Async | Adopt | Sandbox;
#if SLC_EMBED_COM
        bits |= Com;
#endif
        return bits;
    }
}
