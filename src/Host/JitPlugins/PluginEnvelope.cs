using System.Text.Json;

namespace ScriptLangCore;

/// <summary>THE ENVELOPE THE DOOR SPEAKS, and it is the SAME document `Bridge` already uses on every other carrier
/// (`Interpreter.BridgeProtocol`, which aliases <see cref="Protocol"/> so the two cannot drift):
///
///   request   { "v": 1, "m": "invoice.post", "p": { ... } }
///   answer    { "v": 1, "ok": true,  "r": ... }
///   answer    { "v": 1, "ok": false, "e": { "code": "...", "message": "..." } }
///
/// WHY REUSE IT RATHER THAN INVENT A SECOND PROTOCOL. A plugin is one more carrier of the same problem the bridge
/// already solves: two runtimes, two heaps, no shared object graph, so the boundary is UTF-8 text and the object
/// graph stays where it lives. A host that already logs `Bridge` documents reads a plugin's answer with the same
/// eyes, and the SEVEN RULES of that contract apply unchanged. What this file adds is the host SIDE of them for a
/// plugin: the builders, and the reader that refuses an answer whose whole shape is not readable.
///
/// WHAT A PLUGIN ANSWER DOES NOT CARRY: `id`. The bridge echoes it because a host correlates an answer with a
/// request across a log and several carriers allow one outstanding request, so a document has to identify itself.
/// The door answers SYNCHRONOUSLY, one call at a time (contract rule 4), so a request built here omits `id` and an
/// answer is matched by the call it returned from. Rule 6 says an absent `id` stays absent, so this is conformant
/// rather than an exception.
///
/// THE READER IS STRICT ON THE WHOLE SHAPE, which is rule 7 and the reason a host never has to guess:
///   - not an object, or no boolean `ok`              -> `PluginReturnedNonEnvelope`
///   - a `v` that is not <see cref="Protocol"/>       -> `UnsupportedVersion`
///   - `ok:true` with an `e`                          -> `PluginReturnedNonEnvelope` (the two contradict)
///   - `ok:false` with no object `e`, or an `e` whose `code`/`message` is missing or not a non-empty string
///                                                    -> `PluginReturnedNonEnvelope`
///   - `ok:false` carrying an `r`                     -> the `r` is IGNORED, never read (the value is stale by the
///                                                       answer's own verdict, the same call the bridge makes)</summary>
internal static class PluginEnvelope
{
    /// <summary>The contract version this host writes and reads. `Interpreter.BridgeProtocol` aliases it, so the
    /// plugin door and every other carrier of the bridge say one number.</summary>
    internal const int Protocol = 1;

    /// <summary>Builds a request. `p` follows the bridge's rule 5: a JSON OBJECT, or null/absent. A caller that hands
    /// over something else is making a host-side mistake, so it is refused HERE rather than sent to a plugin that
    /// would have to guess.</summary>
    internal static string Request(string verb, string? parametersJson = null)
    {
        if (string.IsNullOrWhiteSpace(verb) || verb.IndexOf('\0') >= 0)
            throw new ArgumentException("a verb is a non-empty string with no NUL in it", nameof(verb));
        var head = "{\"v\":" + Protocol + ",\"m\":\"" + JsonEncodedText.Encode(verb) + "\"";
        if (parametersJson is null) return head + "}";
        var kind = ShapeOf(parametersJson);
        if (kind != JsonValueKind.Object)
            throw new ArgumentException($"p must be a JSON object or null, and this one is {(kind == JsonValueKind.Undefined ? "not JSON" : kind.ToString().ToLowerInvariant())}", nameof(parametersJson));
        return head + ",\"p\":" + parametersJson + "}";
    }

    /// <summary>Reads an answer document into the host's own shape. Never throws: a plugin's verdict and a plugin's
    /// malformed text are both answers, and rule 7 is what turns the second into a `code` a host can branch on.</summary>
    internal static PluginCall ReadAnswer(string? answerJson)
    {
        if (string.IsNullOrWhiteSpace(answerJson))
            return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin answered nothing: a door answers an envelope, even for a failure");
        JsonDocument document;
        try { document = JsonDocument.Parse(answerJson); }
        catch (JsonException) { return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin answered text that is not JSON: build the answer with the envelope's shape"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin answered JSON that is not an object: an answer is {\"v\":1,\"ok\":...}");
            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != Protocol)
                return PluginCall.Refused("UnsupportedVersion", $"the plugin answered envelope version {(root.TryGetProperty("v", out var raw) ? raw.GetRawText() : "(none)")}, and this host speaks {Protocol}: rebuild the plugin or update the host");
            if (!root.TryGetProperty("ok", out var okElement) || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin's answer has no boolean \"ok\" member, so the verdict is unknown");

            if (okElement.ValueKind == JsonValueKind.True)
            {
                if (root.TryGetProperty("e", out _))
                    return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin's answer is ok true AND carries \"e\": answer the value as \"r\", or report a failure with \"ok\": false");
                return new PluginCall(true, root.TryGetProperty("r", out var r) ? r.GetRawText() : "null", "", "");
            }

            if (!root.TryGetProperty("e", out var error) || error.ValueKind != JsonValueKind.Object)
                return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin's answer is ok false with no object \"e\": a host branches on \"e.code\", so state it");
            if (!TryText(error, "code", out var code))
                return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin's \"e.code\" is missing, empty or not a string: a host branches on that name");
            if (!TryText(error, "message", out var message))
                return PluginCall.Refused("PluginReturnedNonEnvelope", "the plugin's \"e.message\" is missing, empty or not a string: state what to fix");
            return new PluginCall(false, "null", code, message);
        }
    }

    /// <summary>The envelope a HOST writes when it could not read the plugin's answer, so <see cref="IPluginBridge.Dispatch"/>
    /// always answers a readable document. A caller that wants the typed form reads it with <see cref="ReadAnswer"/>.</summary>
    internal static string FailureText(string code, string message)
        => "{\"v\":" + Protocol + ",\"ok\":false,\"e\":{\"code\":\"" + JsonEncodedText.Encode(code)
           + "\",\"message\":\"" + JsonEncodedText.Encode(message) + "\"}}";

    /// <summary>The kind of the document <paramref name="json"/> holds, or Undefined when it is not JSON at all.
    /// Reading `p` costs one parse, which is the price of refusing a host-side mistake before it reaches a plugin.</summary>
    private static JsonValueKind ShapeOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind;
        }
        catch (JsonException) { return JsonValueKind.Undefined; }
    }

    /// <summary>A non-empty string member, which is the only shape rule 7 accepts for a code or a message.</summary>
    private static bool TryText(JsonElement error, string member, out string text)
    {
        text = "";
        if (!error.TryGetProperty(member, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString() ?? "";
        return text.Length > 0;
    }
}
