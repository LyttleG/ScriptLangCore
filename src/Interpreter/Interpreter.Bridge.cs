namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>THE ANSWER TO A VERB CALL, as an object rather than a document.
    /// `Bridge.CallEnvelope(verb, params)` answers one of these for EVERY outcome, a host's refusal and a call that
    /// could not be made alike, so a verb call is never wrapped in a `try`: the script reads `answer.Ok`,
    /// `answer.Value` and `answer.Error.Code` instead of guessing the envelope's key names; the members are the
    /// contract's own four (`ok`, `r`, `e`), PascalCase like every other engine type a script reads.
    ///
    /// WHY NOT A DICTIONARY, which is what it answers before this type existed: a document is right for a HOST, whose
    /// document crosses as text, and wrong for the script that just made the call, which had to spell `"ok"` and
    /// `"e"` as strings and read `"code"` out of a nested one. The typed members cannot be misspelled, and the shape a
    /// refusal carries is the SAME normalised `e` the throwing path builds, so `Call`'s error and `CallEnvelope`'s
    /// `.Error` cannot disagree.
    ///
    /// WHAT IT DOES NOT LOSE: <see cref="Document"/> is the envelope exactly as the host wrote it, parsed, so a member
    /// the contract does not name (`origin`, `at`, `frames`, anything a host adds) is still reachable, and
    /// `Json.Serialize(answer.Document)` is the wire shape a reader of the page already knows. It is NULL when the call
    /// failed before a host answered (no host bridge, a verb that is not a name, parameters that are not an object),
    /// because no host document exists to hand back. The RAW door `Bridge.Call(request)` keeps answering the parsed
    /// document rather than this type: that form is the escape hatch, where the caller owns the document and the engine
    /// has no contract to type.</summary>
    public sealed class BridgeEnvelope(bool ok, object? value, BridgeError? error, IScriptDictionary? document)
    {
        /// <summary>The host's verdict: the envelope's `ok`. False means the call was REFUSED, and
        /// <see cref="Error"/> says why.</summary>
        public bool Ok { get; } = ok;

        /// <summary>The host's `r`: the value the verb answered. Null when the answer carried none, and null on a
        /// refusal, because a refusal has no result (read <see cref="Error"/> instead).</summary>
        public object? Value { get; } = value;

        /// <summary>The host's `e`, when <see cref="Ok"/> is false; null when it is true.</summary>
        public BridgeError? Error { get; } = error;

        /// <summary>The envelope as the host wrote it, parsed: the escape hatch for a member the contract does not
        /// name, and the document `Json.Serialize` turns back into the wire shape. Null when the call failed before a
        /// host answered, because there is no host document then.</summary>
        public IScriptDictionary? Document { get; } = document;
    }

    /// <summary>WHY a verb call was refused, as an object: the contract's `e`, whose five members are what a caller can
    /// act on. `Code` is the machine-readable name to branch on and `Message` the text a human reads, which is the
    /// split the contract's own doc states; `Retry` and `RetryAfter` say whether and how long to wait, and `Details`
    /// is whatever the host attached.
    ///
    /// The `e` a HANDLER throws through `Bridge.Error` arrives here too, because the refusal shape is one: it carries
    /// the script's own code rather than the engine's enum name, and a refusal a host invented and one a script threw
    /// therefore read the same way.</summary>
    public sealed class BridgeError(string code, string message, bool retry, long retryAfter, object? details)
    {
        /// <summary>The machine-readable code, non-empty, as in `NoSuchInvoice` or `UnknownVerb`.</summary>
        public string Code { get; } = code;

        /// <summary>The text a human reads, non-empty.</summary>
        public string Message { get; } = message;

        /// <summary>Whether the caller may try again.</summary>
        public bool Retry { get; } = retry;

        /// <summary>How long to wait before retrying, in milliseconds, and 0 when the host left it out (the contract's
        /// `retryAfter` is optional, so 0 means "not stated" and never "retry now").</summary>
        public long RetryAfter { get; } = retryAfter;

        /// <summary>The host's `details` object, or null when it attached none.</summary>
        public object? Details { get; } = details;
    }

    /// <summary>THE REQUEST A `Bridge.Serve` HANDLER RECEIVES, as an object: the same four members the wire carries,
    /// typed, so a handler writes `req.Verb` and `req.Id` instead of `req["m"]` and `req["id"]`, which was a string
    /// lookup an author could misspell and never see reported as an error.
    ///
    /// IT ALSO BUILDS THE ANSWER, because the id the envelope has to echo is the REQUEST's own: `req.Ok(value)`,
    /// `req.Fail(code, message [, retry | options])` and `req.FromError(error)` are `Bridge.Ok`, `Bridge.Fail` and
    /// `Bridge.FromError` with the id taken from here, so a handler never restates it and never writes the envelope by
    /// hand. The text is the same document either spelling builds (one builder under both), which is what a handler
    /// answers: what crosses the boundary is TEXT, so a typed answer would have to be serialized back anyway. In one
    /// sentence: what the script READS is typed, what crosses the wire is text.
    ///
    /// <see cref="Document"/> is the request exactly as the host wrote it, so a member the contract does not name (an
    /// extension a host and a script agreed on) is still reachable, and the dispatcher's own reading of the document is
    /// the one that built the typed members: they cannot disagree.</summary>
    public sealed class BridgeRequest(string verb, object? id, int version, object? parameters, IScriptDictionary document)
    {
        /// <summary>The verb, the wire's `m`: a non-empty dotted name such as `invoice.post`. Always present, because
        /// the dispatcher refuses a request that names none.</summary>
        public string Verb { get; } = verb;

        /// <summary>The caller's correlation id, the wire's `id`, echoed verbatim (a string stays a string, a number
        /// stays a number, an explicit null stays null). Null when the caller stated none, which is legal.</summary>
        public object? Id { get; } = id;

        /// <summary>The contract version the request asked for: the wire's `v`, or the version this engine speaks when
        /// the request left it out.</summary>
        public int Version { get; } = version;

        /// <summary>The parameters, the wire's `p`: a script dictionary, or null when the request carried none (an
        /// explicit `null` reads the same, which is why a handler tests `== null` rather than `ContainsKey`).</summary>
        public object? Parameters { get; } = parameters;

        /// <summary>The request as the host wrote it, parsed: the escape hatch for a member the contract does not
        /// name.</summary>
        public IScriptDictionary Document { get; } = document;

        /// <summary>THE SUCCESS ANSWER FOR THIS REQUEST: the document `Bridge.Ok(Id, result)` builds, with the id this
        /// request carries (and no `id` at all when it carried none, because the answer must not invent one).</summary>
        public string Ok(object? result) => BridgeOkText(Id, result);

        /// <summary>THE FAILURE ANSWER FOR THIS REQUEST: `Bridge.Fail(Id, code, message)`, with the id taken from the
        /// request. `code` and `message` must be non-empty; the refusal names `req.Fail`.</summary>
        public string Fail(string code, string message) => BridgeFailText("req.Fail", Id, code, message, null);

        /// <summary>The same failure with the fourth argument `Bridge.Fail` takes: the `retry` boolean, or an options
        /// object `{ retry, retryAfter, details }`, refused by name when a key or a type is wrong.</summary>
        public string Fail(string code, string message, object? retryOrOptions) => BridgeFailText("req.Fail", Id, code, message, retryOrOptions);

        /// <summary>THE FAILURE ANSWER BUILT FROM A CAUGHT VALUE, `Bridge.FromError(Id, error)`: what a handler's own
        /// `catch` answers without inventing a second shape.</summary>
        public string FromError(object? error) => BridgeFromErrorText("req.FromError", Id, error);
    }
}
