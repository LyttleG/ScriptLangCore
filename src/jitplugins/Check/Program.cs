// THE PIN for the JIT-plugin loader: an AOT process loads a JIT plugin, and every convention the contract states is
// measured rather than assumed. `src/build/jitplugin-check.sh` stages `nethost.dll` beside this exe and the fixture
// under `fixture/`, then runs it; the exit code is the verdict.
using System.Text.Json;
using ScriptLangCore;

namespace JitPluginCheck;

internal static class Program
{
    static int _passed;
    static int _failed;

    static void Check(string what, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"ok: {what}"); }
        else { _failed++; Console.WriteLine($"FAIL: {what}{(detail.Length == 0 ? "" : ": " + detail)}"); }
    }

    static int Main()
    {
        string here = AppContext.BaseDirectory;
        string nethost = Path.Combine(here, "nethost.dll");
        string fixture = Path.Combine(here, "fixture", "JitPluginFixture.dll");
        string config = Path.Combine(here, "fixture", "JitPluginFixture.runtimeconfig.json");
        const string typeName = "JitPluginFixture.Entry, JitPluginFixture";

        IPluginBridge plugin;
        try { plugin = JitPluginLoader.Load(nethost, config, fixture, typeName); }
        catch (PluginLoadException ex)
        {
            Check("the fixture loads through nethost and hostfxr", false, ex.Message);
            return Summary();
        }
        Check("the fixture loads through nethost and hostfxr", true);
        Check("the table reports this host's abi", plugin.Abi == PluginContract.Abi, $"abi={plugin.Abi}");
        Check("the typed slot answers without a document", plugin.Ping().Contains("JitPluginFixture"), plugin.Ping());

        Check("the door answers a verb", Value(plugin.Call("add", "{\"a\":20,\"b\":22}")) == "42", Raw(plugin.Call("add", "{\"a\":20,\"b\":22}")));

        // A 4000-byte answer cannot fit a fixed buffer, so this is the LENGTH convention (probe, then fill) working.
        var longCall = plugin.Call("long", "{\"n\":4000}");
        Check("a 4000-byte answer survives the probe", longCall.Ok && Text(longCall)?.Length == 4000, Raw(longCall).Length + " bytes of answer");

        // The door's payload is UTF-8 on every platform while hostfxr's strings are char_t: this is the half that
        // proves the two were not confused.
        var utf8 = Text(plugin.Call("utf8"));
        Check("the door's payload is UTF-8 (accent and astral plane)", utf8 is not null && utf8.Contains("caf\u00e9", StringComparison.Ordinal) && utf8.Contains("\U0001F44B", StringComparison.Ordinal), utf8 ?? "(null)");

        var bad = plugin.Call("bad");
        Check("text that is not an envelope is refused", !bad.Ok && bad.ErrorCode == "PluginReturnedNonEnvelope", bad.ErrorCode + " / " + bad.ErrorMessage);

        var liar = plugin.Call("liar");
        Check("a verdict contradicted by an error is refused", !liar.Ok && liar.ErrorCode == "PluginReturnedNonEnvelope", liar.ErrorCode);

        var old = plugin.Call("oldversion");
        Check("another envelope version is refused by name", !old.Ok && old.ErrorCode == "UnsupportedVersion", old.ErrorCode + " / " + old.ErrorMessage);

        var threw = plugin.Call("throw");
        Check("a plugin failure becomes a code, never an exception", !threw.Ok && threw.ErrorCode == "PluginFailed", threw.ErrorCode + " / " + threw.ErrorMessage);

        var unknown = plugin.Call("nope");
        Check("an unknown verb is the plugin's own refusal", !unknown.Ok && unknown.ErrorCode == "UnknownVerb", unknown.ErrorCode);

        Check("a bare type name is refused with the form it needs", Refuses(() => JitPluginLoader.Load(nethost, config, fixture, "JitPluginFixture.Entry"), "Namespace.Type, AssemblyName"));

        Check("a plugin speaking another abi is refused naming both", Refuses(() => JitPluginLoader.Load(nethost, config, fixture, typeName, expectedAbi: 2), "plugin speaks abi 1, this host speaks 2"));

        Check("parameters that are not an object are refused before the call", Refuses(() => PluginEnvelope.Request("add", "42"), "JSON object"));

        return Summary();
    }

    /// <summary>True when <paramref name="load"/> refuses with a sentence containing <paramref name="expected"/>: a
    /// refusal that names nothing is the failure mode these checks exist for.</summary>
    static bool Refuses(Func<object> load, string expected)
    {
        try { load(); return false; }
        catch (Exception ex) { return ex is PluginLoadException or ArgumentException && ex.Message.Contains(expected, StringComparison.Ordinal); }
    }

    static string Raw(PluginCall call) => call.Ok ? call.ResultJson : $"{call.ErrorCode} / {call.ErrorMessage}";

    static string? Text(PluginCall call)
    {
        if (!call.Ok || call.ResultJson.Length == 0) return null;
        try { using var document = JsonDocument.Parse(call.ResultJson); return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() : null; }
        catch (JsonException) { return null; }
    }

    static string? Value(PluginCall call) => call.Ok ? call.ResultJson : null;

    static int Summary()
    {
        // ASCII only: a Windows console renders a box-drawing character as mojibake on this box.
        Console.WriteLine($"-- {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
