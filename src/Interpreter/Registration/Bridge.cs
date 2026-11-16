using System.Text;
using System.Text.Json;

namespace ScriptLangCore;

/// <summary>THE UNIFIED BOUNDARY: one request document in, one response document out, in BOTH directions.
///
/// WHY IT EXISTS. A script value cannot cross a runtime boundary as an object: a NativeAOT engine has its own heap and
/// cannot see a host's managed objects, `Reflection.Emit` does not exist here, and a COM pointer is a per-host story
/// (Excel has one, a Python script does not). What CAN cross every boundary is UTF-8 text, so the contract is one verb
/// on each side and the object graph stays where it lives:
///
///   script -> host   Bridge.Call(requestJson)     answers the host's response document, already parsed
///   host   -> script Bridge.Handle(requestJson)   answers the script's response document as text
///
/// THE SAME TWO DOORS, three carriers: `slc_invoke` / `slc_set_bridge` over the C ABI, an `Invoke` member on the COM
/// server, and a .NET host setting <see cref="HostBridge"/> directly. One envelope, one dispatch, so a failure reads
/// the same in every log.
///
/// THE ENVELOPE (version 1). A request is an object:
///   { "v": 1, "id": "7", "m": "invoice.post", "p": { ... } }
/// `v` is the contract version, `id` is echoed VERBATIM (optional, and absent stays absent), `m` is the verb, and `p`
/// is the parameters as an object or omitted. A response is an object:
///   { "v": 1, "id": "7", "ok": true,  "r": ... }
///   { "v": 1, "id": "7", "ok": false, "e": { "code": "...", "message": "...", "retry": false,
///                                            "at": { "file": "...", "line": 12, "col": 5 },
///                                            "frames": ["Post [L10:C9]"] } }
///
/// THE SEVEN RULES, each of which is a decision rather than a habit:
///   1. ONE VERB SHAPE. Both directions use the same document, so a log or a fixture is readable without knowing
///      which side produced it.
///   2. NO RE-ENTRY ON THE SAME SESSION. A session serves one host request at a time. A handler MAY call the host with
///      `Bridge.Call`; what is REFUSED by name is `Bridge.Handle` on its own session, a verb issued from inside a host
///      callback (a run posted there would wait on itself), and a host that answers `Bridge.Call` by calling back into
///      the same session. Each of those is a loop, which is why the rule is about re-entry rather than about depth.
///   3. NOTHING THROWS ACROSS. A handler that throws becomes `ok:false` with the engine's own code, message, position
///      and frames; a host that throws is refused by name at the `Bridge.Call` that reached it.
///   4. `m` IS ADDITIVE ONLY. Removing or re-meaning a verb is a contract version bump, which is what `v` is for. A
///      reader tolerates members it does not know, so a newer side does not break an older one.
///   5. `p` IS AN OBJECT, ABSENT, OR EXPLICITLY NULL. A null means the same as absent, which is stated on the contract
///      page rather than guessed by a handler. Anything large is paged inside it, because a contract that allows one
///      huge value invites a payload no reader can bound.
///   6. `id` IS ECHOED BY CONSTRUCTION. The dispatcher rewrites the response's `id` from the request, `null` included,
///      so a handler cannot forget it. It is the handle a host matches an answer to its request with in a log; NO
///      carrier allows two requests outstanding at once, so pipelining needs a batch, which version 1 does not define.
///   7. A RESPONSE IS VALIDATED. The WHOLE shape, not only `ok`: text that is not an object, a missing or non-boolean
///      `ok`, an `ok:true` answer carrying `e`, an `e` that is not an object, or an error whose `code` or `message` is
///      missing or not a non-empty string is `HandlerReturnedNonEnvelope`. An `ok:false` answer carrying `r` has the
///      `r` dropped, because exactly one of the two shapes is present. A host never has to guess.
///
/// WHAT IS NOT SHARED. A live object crosses as a REFERENCE the owning side resolves (`acc:17`), never as a value, so
/// lifetime belongs to the host and a dangling reference is a `code` in the error object rather than a freed COM
/// pointer. That is what makes this contract work for a Python or C host at all.
/// </summary>
internal partial class Interpreter
{
    /// <summary>The contract version this build writes and reads. A request that names another one is refused with
    /// `UnsupportedVersion` rather than mis-read, which is the same rule the bundle format follows.</summary>
    internal const int BridgeProtocol = 1;

    /// <summary>The handler a script registered with <c>Bridge.Serve</c>, or null. One per interpreter, which is what
    /// makes it the SESSION's contract rather than the process's.</summary>
    private object? _bridgeHandler;

    /// <summary>The file that registered the handler, captured AT REGISTRATION because a host's call can arrive after
    /// the run that served it ended (and `CurrentSourceName` is reset then). It is the `at.file` a refusal names, and a
    /// number without a file is what a host would otherwise have to correlate by hand.</summary>
    private string _bridgeSource = "";

    /// <summary>Set while a request is being served, so a handler that calls `Bridge.Handle` on its own session is
    /// refused by name instead of recursing (rule 2).</summary>
    private bool _bridgeServing;

    /// <summary>THE HOST'S SIDE OF THE CONTRACT, installed by the embedding host: a request document in, a response
    /// document out. The C ABI sets it from <c>slc_set_bridge</c>, and a .NET host can set it directly (the same
    /// field, because two implementations of one rule is the defect this repository keeps finding). Null means no
    /// host is listening, and `Bridge.Call` says so by name.</summary>
    internal Func<string, string?>? HostBridge { get; set; }

    /// <summary>Registers the Bridge namespace. Nothing here decides how a call travels: that is the host's choice of
    /// carrier, and swapping a COM host for a C one changes no script.</summary>
    private void RegisterBridgeFunctions()
    {
        var bridgeNs = new Namespace(_comparer);

        bridgeNs.AddDynamicProperty("Version", () => BridgeProtocol);
        bridgeNs.AddDynamicProperty("Served", () => (Current ?? this)._bridgeHandler is not null);
        bridgeNs.AddDynamicProperty("Hosted", () => (Current ?? this).HostBridge is not null);

        // ── a script SERVES verbs to its host ────────────────────────────────────────────────────────────────────
        // The handler takes ONE parameter, the request as a script dictionary (`req["m"]`, `req["p"]`), and answers a
        // response document built with Bridge.Ok or Bridge.Fail. A handler that throws is not a bug in the contract:
        // the dispatcher turns it into the error object, which is what "nothing throws across" means here.
        bridgeNs.AddMethod("Serve", args =>
        {
            var self = Current ?? this;
            if (args.Length < 1)
                throw new ScriptException("Bridge.Serve expects the function that answers a request, or null to stop serving.");
            if (args[0] is null)
            {
                self._bridgeHandler = null;
                self._bridgeSource = "";
                return true;
            }
            if (!IsScriptCallable(args[0]))
                throw new ScriptException($"Bridge.Serve expects a function, not '{args[0]!.GetType().Name}'.");
            self._bridgeHandler = args[0];
            self._bridgeSource = self.CurrentSourceName;
            return true;
        });

        // ── the HOST -> SCRIPT door, as text ─────────────────────────────────────────────────────────────────────
        // This is what the ABI export and the COM member call, and it is also what a stub host in a suite calls, so
        // the contract is provable without any host technology at all.
        bridgeNs.AddMethod("Handle", args =>
        {
            var self = Current ?? this;
            if (args.Length < 1 || args[0] is not string request)
                throw new ScriptException("Bridge.Handle expects the request document as text.");
            return self.BridgeHandle(request);
        });

        // ── the SCRIPT -> HOST door, as a parsed document ────────────────────────────────────────────────────────
        bridgeNs.AddMethod("Call", args =>
        {
            var self = Current ?? this;
            if (args.Length < 1 || args[0] is not string request)
                throw new ScriptException("Bridge.Call expects the request document as text.");
            var host = self.HostBridge;
            if (host is null)
                throw new ScriptException("Bridge.Call needs a host bridge: the host installs one with slc_set_bridge, or a .NET host sets HostBridge on the interpreter.");
            string? answer;
            try { answer = host(request); }
            catch (ScriptException) { throw; }
            catch (Exception ex) { throw new ScriptException("the host bridge failed: " + FriendlyExceptionMessage(ex)); }
            var parsed = ParseBridgeDocument(answer);
            if (parsed is not IScriptDictionary response)
                throw new ScriptException("the host answered something that is not a JSON object, so it does not follow the Bridge contract.");
            return response;
        });

        // ── the two ways a handler answers ───────────────────────────────────────────────────────────────────────
        bridgeNs.AddMethod("Ok", args =>
        {
            var id = args.Length > 0 ? args[0] : null;
            var result = args.Length > 1 ? args[1] : null;
            var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = true };
            if (id is not null) response["id"] = id;
            if (result is not null) response["r"] = result;
            return SerializeToText(response);
        });

        bridgeNs.AddMethod("Fail", args =>
        {
            if (args.Length < 3 || args[1] is not string code || code.Length == 0
                || args[2] is not string message || message.Length == 0)
                throw new ScriptException("Bridge.Fail expects (id, code, message [, retry | options]), where code and message are non-empty strings.");
            var id = args[0];
            var retry = false;
            long? retryAfter = null;
            object? details = null;
            if (args.Length > 3)
            {
                if (args[3] is bool flag)
                {
                    retry = flag;
                }
                else if (args[3] is IScriptDictionary || args[3] is System.Collections.IDictionary)
                {
                    var options = args[3]!;
                    foreach (var key in BridgeKeysOf(options))
                    {
                        var name = key as string;
                        if (name is null || (name != "retry" && name != "retryAfter" && name != "details"))
                            throw new ScriptException($"Bridge.Fail does not know the option '{key}'. Valid options: retry, retryAfter, details.");
                    }
                    if (TryBridgeMember(options, "retry", out var retryValue))
                    {
                        if (retryValue is bool retryFlag) retry = retryFlag;
                        else throw new ScriptException($"Bridge.Fail takes a boolean for \"retry\", not '{retryValue?.GetType().Name ?? "null"}'.");
                    }
                    if (TryBridgeMember(options, "retryAfter", out var afterValue))
                    {
                        var milliseconds = BridgeWholeNumber(afterValue);
                        if (milliseconds is null || milliseconds < 0)
                            throw new ScriptException("Bridge.Fail takes a whole number of milliseconds for \"retryAfter\", not a negative value or a fraction.");
                        retryAfter = milliseconds;
                    }
                    if (TryBridgeMember(options, "details", out var detailsValue))
                    {
                        if (detailsValue is IScriptDictionary || detailsValue is System.Collections.IDictionary) details = detailsValue;
                        else throw new ScriptException("Bridge.Fail takes a JSON object for \"details\", not a value a host cannot read as one.");
                    }
                }
                else
                {
                    throw new ScriptException($"Bridge.Fail takes a boolean for retry or an options object, not '{args[3]?.GetType().Name ?? "null"}'.");
                }
            }
            var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = false };
            if (id is not null) response["id"] = id;
            var error = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
                ["retry"] = retry,
                ["origin"] = "handler",
            };
            if (retryAfter is long after) error["retryAfter"] = after;
            if (details is not null) error["details"] = details;
            response["e"] = error;
            return SerializeToText(response);
        });

        // ── a caught error, shaped as the error object ────────────────────────────────────────────────────────────
        // A script catches a failure and wants to answer it without inventing a second shape. `ErrorInfo` carries the
        // code, the message and the frames; it carries NO position (that lives in the message the engine composed),
        // which is why an `at` filled from a caught value is left out rather than guessed.
        bridgeNs.AddMethod("FromError", args =>
        {
            if (args.Length < 2)
                throw new ScriptException("Bridge.FromError expects (id, error).");
            var id = args[0];
            var error = BridgeErrorFor(args[1], out var problem);
            if (problem is not null)
                throw new ScriptException("Bridge.FromError: " + problem + ".");
            var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = false };
            if (id is not null) response["id"] = id;
            response["e"] = error;
            return SerializeToText(response);
        });

        CurrentFrame.Scopes.Define("Bridge", bridgeNs);
    }

    /// <summary>Serves ONE request document and answers ONE response document. Every refusal the contract states is
    /// decided here, in order, so a host and a script cannot disagree about what a malformed request means.</summary>
    internal string BridgeHandle(string requestJson)
    {
        // Rule 2's script half. A host cannot reach this twice at once (the ABI marshals one call at a time), but a
        // handler that calls Bridge.Handle on its own session is a loop, and a named refusal is what it deserves.
        if (_bridgeServing)
            throw new ScriptException("Bridge.Handle is already serving a request: the contract is no re-entry on the same session, so a handler cannot call back into its own session.");
        _bridgeServing = true;
        try { return BridgeServe(requestJson); }
        finally { _bridgeServing = false; }
    }

    private string BridgeServe(string requestJson)
    {
        var parsed = ParseBridgeDocument(requestJson);
        if (parsed is not IScriptDictionary request)
            return BridgeErrorText(false, null, "BadRequest", "the request is not a JSON object", null);
        if (!TryReadBridgeVersion(request, out var problem))
            return BridgeErrorText(HasId(request), IdOf(request), "UnsupportedVersion", problem, null);
        if (!request.ContainsKey("m") || request["m"] is not string verb || verb.Length == 0)
            return BridgeErrorText(HasId(request), IdOf(request), "MissingMethod", "the request names no verb: a caller writes \"m\": \"verb.name\"", null);
        var parameters = request.ContainsKey("p") ? request["p"] : null;
        if (parameters is not null && parameters is not IScriptDictionary)
            return BridgeErrorText(HasId(request), IdOf(request), "BadParams", "p must be a JSON object, or left out", null);
        var handler = _bridgeHandler;
        if (handler is null)
            return BridgeErrorText(HasId(request), IdOf(request), "NoHandler", "the script has not called Bridge.Serve, so no verb can be served", null);

        object? answer;
        try { answer = InvokeScriptCallback(handler, [request]); }
        catch (ScriptException ex) { return BridgeFailureText(HasId(request), IdOf(request), ex); }
        catch (Exception ex) { return BridgeErrorText(HasId(request), IdOf(request), "Failed", FriendlyExceptionMessage(ex), null); }

        if (answer is not string document)
            return BridgeErrorText(HasId(request), IdOf(request), "HandlerReturnedNothing",
                answer is null
                    ? "the handler answered nothing, so a verb must end with Bridge.Ok or Bridge.Fail"
                    : $"the handler answered a {answer.GetType().Name}, so a verb must answer the text Bridge.Ok or Bridge.Fail builds", null);
        return BridgeNormalize(document, HasId(request), IdOf(request));
    }

    /// <summary>Reads a document into the engine's own values. One door, so the request, the handler's answer and the
    /// host's answer are all read the same way (and a malformed one becomes a contract error rather than an exception
    /// naming a JSON token).</summary>
    private static object? ParseBridgeDocument(string? document)
    {
        if (string.IsNullOrWhiteSpace(document)) return null;
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(document));
            if (!reader.Read()) return null;
            return ParseJsonValue(ref reader);
        }
        catch (JsonException) { return null; }
        catch (ScriptException) { return null; }
    }

    private static bool HasId(IScriptDictionary request) => request.ContainsKey("id");

    private static object? IdOf(IScriptDictionary request) => request.ContainsKey("id") ? request["id"] : null;

    /// <summary>`v` must be a whole number equal to this build's contract, and the message says which one it read:
    /// a host that asked for version 2 gets a sentence it can act on instead of mis-read rows.</summary>
    private static bool TryReadBridgeVersion(IScriptDictionary request, out string problem)
    {
        problem = "";
        if (!request.ContainsKey("v"))
        {
            problem = $"the request states no contract version: write \"v\": {BridgeProtocol}";
            return false;
        }
        var version = BridgeWholeNumber(request["v"]);
        if (version is null)
        {
            problem = "v is not a whole number";
            return false;
        }
        if (version != BridgeProtocol)
        {
            problem = $"this engine speaks contract version {BridgeProtocol}, and the request names {version}";
            return false;
        }
        return true;
    }

    private static long? BridgeWholeNumber(object? value) => value switch
    {
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint ui => ui,
        ulong ul when ul <= long.MaxValue => (long)ul,
        double d when d >= long.MinValue && d <= long.MaxValue && Math.Floor(d) == d => (long)d,
        _ => null,
    };

    /// <summary>Rule 6 and rule 7 in one place: the answer must be an envelope whose WHOLE shape is readable, and its
    /// `id` and `v` are the dispatcher's, not the handler's, so a client cannot be handed somebody else's answer.</summary>
    private string BridgeNormalize(string document, bool hasId, object? id)
    {
        var parsed = ParseBridgeDocument(document);
        if (parsed is not IScriptDictionary response)
            return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler answered text that is not a JSON object: build it with Bridge.Ok or Bridge.Fail", null);
        if (!response.ContainsKey("ok") || response["ok"] is not bool ok)
            return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's answer has no boolean \"ok\" member: build it with Bridge.Ok or Bridge.Fail", null);

        // Rule 7 is the WHOLE shape, not only the discriminator. An answer says ok TRUE with an `r` (or nothing) or
        // ok FALSE with an `e`. An `e` beside `ok:true` is refused, because it contradicts the verdict, while an `r`
        // beside `ok:false` is DROPPED rather than refused: the answer already says the call failed, so the value is
        // stale, and a host that reads `r` on a failure is reading something the contract does not promise.
        if (ok)
        {
            if (response.ContainsKey("e"))
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's answer is ok true AND carries \"e\": answer the result as \"r\", or report a failure with \"ok\": false", null);
        }
        else
        {
            if (response.ContainsKey("r")) response.Remove("r");
            if (!response.ContainsKey("e"))
                response["e"] = new Dictionary<string, object?>
                {
                    ["code"] = "Failed",
                    ["message"] = "the handler reported a failure without a reason: answer Bridge.Fail(id, code, message)",
                    ["retry"] = false,
                };
            // The error is read through the same two-shape helper the builders use, because the dispatcher's own
            // filler is a CLR dictionary while a handler's parsed answer is a script one: requiring one type here
            // would refuse the other, which is exactly what it did to the filler before this was written.
            var error = response["e"]!;
            if (error is not IScriptDictionary && error is not System.Collections.IDictionary)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e\" is not a JSON object: answer Bridge.Fail(id, code, message) or Bridge.FromError(id, error)", null);
            if (!TryBridgeMember(error, "code", out var codeValue) || codeValue is not string code || code.Length == 0)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's error has no non-empty string \"code\": a host branches on that name", null);
            if (!TryBridgeMember(error, "message", out var messageValue) || messageValue is not string message || message.Length == 0)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's error has no non-empty string \"message\": state what to fix", null);
            if (TryBridgeMember(error, "retry", out var retryValue))
            {
                if (retryValue is not bool)
                    return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e.retry\" is not a boolean", null);
            }
            else
            {
                SetBridgeMember(error, "retry", false);
            }
            if (TryBridgeMember(error, "retryAfter", out var afterValue)
                && !(BridgeWholeNumber(afterValue) is long retryAfter && retryAfter >= 0))
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e.retryAfter\" is not a whole number of milliseconds", null);
            if (TryBridgeMember(error, "details", out var detailsValue)
                && detailsValue is not IScriptDictionary && detailsValue is not System.Collections.IDictionary)
                return BridgeErrorText(hasId, id, "HandlerReturnedNonEnvelope", "the handler's \"e.details\" is not a JSON object: a host reads it as data", null);
            SetBridgeMember(error, "origin", "handler");
        }

        response["v"] = BridgeProtocol;
        if (hasId) response["id"] = id;
        else if (response.ContainsKey("id")) response.Remove("id");
        return SerializeToText(response);
    }

    /// <summary>The failure the ENGINE raises, with the position the exception carries and the file the handler was
    /// registered from. That is the half a host otherwise has to correlate by hand, which is why it is part of the
    /// contract and not of a host's log line.</summary>
    private string BridgeFailureText(bool hasId, object? id, ScriptException ex)
    {
        ScriptErrorInfo? info = ex.Value is ScriptErrorInfo sei ? sei : null;
        var code = (info?.Code ?? ex.ErrorCode).ToString();
        var message = info?.Message ?? ex.Message;
        var at = ex.Line > 0
            ? new Dictionary<string, object?>
            {
                ["file"] = _bridgeSource,
                ["line"] = ex.Line,
                ["col"] = ex.Column,
            }
            : null;
        return BridgeErrorText(hasId, id, code, message, at, info?.Frames);
    }

    /// <summary>ONE builder for every response the dispatcher writes, so no two refusals can differ in shape.</summary>
    private string BridgeErrorText(bool hasId, object? id, string code, string message, Dictionary<string, object?>? at, IReadOnlyList<string>? frames = null)
    {
        var error = new Dictionary<string, object?> { ["code"] = code, ["message"] = message, ["retry"] = false, ["origin"] = "engine" };
        if (at is not null) error["at"] = at;
        if (frames is { Count: > 0 }) error["frames"] = frames;
        var response = new Dictionary<string, object?> { ["v"] = BridgeProtocol, ["ok"] = false, ["e"] = error };
        if (hasId) response["id"] = id;
        return SerializeToText(response);
    }

    /// <summary>A caught value as the contract's error object: `ErrorInfo` when the script threw one, a script
    /// dictionary when the script BUILT one (`{ code: ..., message: ... }`, which is how a handler reports a failure of
    /// its own without inventing a shape), and the engine's friendly text otherwise. No `at` from a caught value, and
    /// that is stated rather than guessed: a caught value carries no position (the engine appends it to the message it
    /// composes), so the dispatcher fills `at` only for a failure IT caught. `problem` is set when the script's own
    /// dictionary is not a shape the contract can serve, so the builder refuses by name instead of guessing.</summary>
    private static Dictionary<string, object?> BridgeErrorFor(object? value, out string? problem)
    {
        problem = null;
        if (value is ScriptErrorInfo info)
        {
            var error = new Dictionary<string, object?>
            {
                ["code"] = info.Code.ToString(),
                ["message"] = info.Message,
                ["retry"] = false,
                ["origin"] = "handler",
            };
            if (info.Frames is { Count: > 0 }) error["frames"] = info.Frames;
            return error;
        }
        // A script-built error travels as the dictionary it is: a `{ code: ..., message: ... }` literal is a
        // `Dictionary<object, object>` rather than a script dictionary, so both shapes are read by name.
        if (value is IScriptDictionary || value is System.Collections.IDictionary)
        {
            var source = value!;
            var error = new Dictionary<string, object?>
            {
                ["code"] = "Failed",
                ["message"] = "",
                ["retry"] = false,
                ["origin"] = "handler",
            };
            if (!TryBridgeMember(source, "code", out var codeValue) || codeValue is not string code || code.Length == 0)
            {
                problem = "code must be a non-empty string a host can branch on";
                return error;
            }
            error["code"] = code;
            if (!TryBridgeMember(source, "message", out var messageValue) || messageValue is not string message || message.Length == 0)
            {
                problem = "message must be a non-empty string";
                return error;
            }
            error["message"] = message;
            if (TryBridgeMember(source, "retry", out var retryValue))
            {
                if (retryValue is not bool retry)
                {
                    problem = "retry must be a boolean";
                    return error;
                }
                error["retry"] = retry;
            }
            if (TryBridgeMember(source, "retryAfter", out var afterValue))
            {
                var milliseconds = BridgeWholeNumber(afterValue);
                if (milliseconds is null || milliseconds < 0)
                {
                    problem = "retryAfter must be a whole number of milliseconds";
                    return error;
                }
                error["retryAfter"] = milliseconds;
            }
            if (TryBridgeMember(source, "details", out var detailsValue))
            {
                if (detailsValue is not IScriptDictionary && detailsValue is not System.Collections.IDictionary)
                {
                    problem = "details must be a JSON object";
                    return error;
                }
                error["details"] = detailsValue;
            }
            return error;
        }
        return new Dictionary<string, object?>
        {
            ["code"] = "Failed",
            ["message"] = value?.ToString() ?? "the handler failed",
            ["retry"] = false,
            ["origin"] = "handler",
        };
    }

    /// <summary>Reads one member of a script dictionary or a CLR one by NAME, the pair `BridgeErrorFor` handles.
    /// Answers false when the member is ABSENT, so a caller can tell absent from present-and-null.</summary>
    private static bool TryBridgeMember(object dictionary, string name, out object? value)
    {
        if (dictionary is IScriptDictionary scripted && scripted.ContainsKey(name))
        {
            value = scripted[name];
            return true;
        }
        if (dictionary is System.Collections.IDictionary raw && raw.Contains(name))
        {
            value = raw[name];
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Writes one member of a script dictionary or a CLR one by NAME, the write half of `TryBridgeMember`.
    /// The dispatcher uses it to force the members it owns (`retry`'s default and `origin`) without caring which shape
    /// the handler's answer arrived in.</summary>
    private static void SetBridgeMember(object dictionary, string name, object? value)
    {
        if (dictionary is IScriptDictionary scripted) scripted[name] = value;
        else if (dictionary is System.Collections.IDictionary raw) raw[name] = value;
    }

    /// <summary>The keys of a script dictionary or a CLR one, so a builder can refuse a member it does not know rather
    /// than dropping it. The two shapes are the same pair `TryBridgeMember` reads.</summary>
    private static IEnumerable<object> BridgeKeysOf(object dictionary)
    {
        if (dictionary is IScriptDictionary scripted) return scripted.Keys();
        if (dictionary is System.Collections.IDictionary raw)
        {
            var keys = new List<object>(raw.Keys.Count);
            foreach (var key in raw.Keys) keys.Add(key!);
            return keys;
        }
        return [];
    }
}
