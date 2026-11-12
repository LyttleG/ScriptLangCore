namespace ScriptLangCore;

/// <summary>The two framings a streaming answer can use. They are NOT interchangeable for a client: a browser reads
/// `text/event-stream` natively through `EventSource`, while a JSON-lines consumer parses one value per line. That is
/// why they are two members rather than one member with a format string (a misspelled format would be a runtime
/// refusal, and the two types are not what an endpoint author should have to remember).</summary>
public enum HttpStreamFormat
{
    /// <summary>One JSON value per line, `application/x-ndjson`.</summary>
    JsonLines,

    /// <summary>`data: &lt;text&gt;` then a blank line, `text/event-stream`, which is UTF-8 by definition.</summary>
    EventStream
}

/// <summary>What a streaming answer PULLS FROM: one element at a time, in the caller's thread, until it answers
/// false. Synchronous on purpose, because the writer runs on the server's request thread and the engine's producer
/// kinds (`Coroutine`, an `IEnumerable`, an `IAsyncEnumerable`) each have a blocking pull that the engine already
/// uses elsewhere: a script generator is RESUMED, and an async step is WAITED through the engine's own task bridge,
/// which is the same wait a top-level `await` performs. So the transport never has to know which kind it holds.
///
/// Whoever creates a source owns it, and the writer disposes it on every exit, which is what releases an async
/// enumerator's file handle.</summary>
public interface IHttpStreamSource : IDisposable
{
    /// <summary>Answers the next element, or false when the sequence has ended. A failure thrown through here
    /// reaches the script's own `try`/`catch` when the producer is a script body, and ends the stream otherwise.</summary>
    bool MoveNext(out object? item);
}

/// <summary>What a route streams, carried by the answer rather than turned into bytes up front. `http.StreamLines`
/// and `http.StreamEvents` build one, and the server writes it with CHUNKED framing: an answer whose length is not
/// known when it starts, which is exactly what `Content-Length` cannot express.
///
/// The framing rules this record leaves to <see cref="HttpStreamFraming"/> are the reason there is one helper rather
/// than a method per member: the two spellings must not drift about what a line looks like.</summary>
/// <param name="Format">Which framing the answer uses.</param>
/// <param name="Source">Where the elements come from; the writer disposes it.</param>
/// <param name="ContentType">A stated content type, or null for the format's own.</param>
public sealed record HttpStream(HttpStreamFormat Format, IHttpStreamSource Source, string? ContentType = null);

/// <summary>THE one statement of how a streamed element looks on the wire, for both framings. Two rules, and each is
/// a decision rather than a detail:
///
/// * JSON LINES writes every element through the engine's serializer, so a string arrives QUOTED (`"a line"`) and the
///   body is valid JSON from its first byte to its last. That is what an NDJSON consumer parses.
/// * SERVER-SENT EVENTS writes the element's TEXT, so a string arrives as it is (`data: a line`), which is what makes
///   `EventSource` usable for a feed of lines, and anything else is serialized as JSON so a client can parse it. A
///   multi-line value becomes one `data:` line per line, which the SSE grammar requires (a bare newline would end the
///   event).
///
/// Both framings terminate each element with a blank line, and both are written as UTF-8 by the server.</summary>
internal static class HttpStreamFraming
{
    /// <summary>What an NDJSON answer says it is. `application/jsonl` is used by some tools; the registered spelling
    /// for newline-delimited JSON is `application/x-ndjson`.</summary>
    public const string JsonLinesType = "application/x-ndjson";

    /// <summary>What an SSE answer says it is, fixed by the event-source specification, UTF-8 always.</summary>
    public const string EventStreamType = "text/event-stream";

    public static string ContentTypeFor(HttpStreamFormat format) =>
        format == HttpStreamFormat.EventStream ? EventStreamType : JsonLinesType;

    /// <summary>The headers a streamed answer needs BESIDES its content type, and why each is there: `Cache-Control:
    /// no-cache` keeps a browser from storing an event stream, and `X-Accel-Buffering: no` is what stops a reverse
    /// proxy from holding the whole stream before forwarding it (a real deployment hazard, and the header costs
    /// nothing when no proxy is in front).</summary>
    public static void ApplyHeaders(HttpStreamFormat format, System.Collections.Generic.IDictionary<string, string> headers)
    {
        if (format != HttpStreamFormat.EventStream) return;
        headers["Cache-Control"] = "no-cache";
        headers["X-Accel-Buffering"] = "no";
    }

    /// <summary>One element as the bytes of its wire text, terminator included.</summary>
    public static string Frame(HttpStreamFormat format, object? item) =>
        format == HttpStreamFormat.EventStream ? FrameEvent(item) : FrameJsonLine(item);

    /// <summary>One JSON value, then a newline. A null element is the JSON `null`, so a stream can carry one.</summary>
    public static string FrameJsonLine(object? item) => Interpreter.SerializeToText(item) + "\n";

    /// <summary>`data: &lt;text&gt;` lines, then the blank line that ends the event. A string is used as it is and
    /// anything else is serialized, because those are the two things a consumer expects: a text feed and a record
    /// feed.</summary>
    public static string FrameEvent(object? item)
    {
        var text = item as string ?? Interpreter.SerializeToText(item);
        var builder = new System.Text.StringBuilder(text.Length + 16);
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n' && text[i] != '\r') continue;
            builder.Append("data: ").Append(text, start, i - start).Append('\n');
            // A CRLF pair is ONE line break: skip the LF so an empty second line is not emitted.
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        builder.Append("data: ").Append(text, start, text.Length - start).Append('\n');
        builder.Append('\n');
        return builder.ToString();
    }
}
