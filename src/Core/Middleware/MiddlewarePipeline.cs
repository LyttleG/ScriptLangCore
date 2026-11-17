namespace ScriptLangCore;

/// <summary>Generic chain-of-responsibility middleware pipeline.</summary>
/// <typeparam name="TContext">Context type passed through the pipeline.</typeparam>
public class MiddlewarePipeline<TContext> where TContext : class
{
    private readonly List<Func<TContext, Action, object?>> _middlewares = [];

    public void Use(Func<TContext, Action, object?> middleware) => _middlewares.Add(middleware);

    /// <summary>Runs the chain. <paramref name="stopped"/> is asked before each middleware, so a context that
    /// ends the work itself (a TCP middleware that closed the connection) stops the rest of the chain there:
    /// "closes the connection immediately" has to mean that, or the next middleware runs on a dead socket.</summary>
    public object? Execute(TContext ctx, Func<bool>? stopped = null)
    {
        var index = -1;
        void Next()
        {
            if (stopped?.Invoke() == true) return;
            index++;
            if (index < _middlewares.Count)
                _middlewares[index](ctx, Next);
        }
        Next();
        return (ctx as McpContext)?.Result;
    }

    public int Count => _middlewares.Count;
}

/// <summary>Context for MCP tool call middleware.</summary>
public sealed class McpContext
{
    public string ToolName { get; set; } = "";
    public IReadOnlyList<object?> Args { get; set; } = [];
    public object? Result { get; set; }
    public string? ErrorMessage { get; private set; }

    public void Error(string msg) { ErrorMessage = msg; }
}

/// <summary>Context for TCP connection middleware.</summary>
public sealed class TcpContext(Action close)
{
    public string RemoteAddress { get; set; } = "";
    public void Close() => close();
}

/// <summary>Context for HTTP server request middleware.</summary>
public sealed class HttpContext
{

    /// <summary>Cancellation for the current request: fired when the MCP HTTP server stops or a
    /// socket failure is detected (best-effort, HttpListener has no Kestrel-style abort event).
    /// Http calls made by the handler inherit this token through the interpreter's ambient scope.</summary>
    public CancellationToken RequestAborted { get; set; }
    public string HttpMethod { get; set; } = "";
    public string Url { get; set; } = "";

    /// <summary>The request body as TEXT. It is read from the request the FIRST time it is asked for, and the answer
    /// is kept, so a handler that reads it twice pays once and sees the same text.
    ///
    /// A request's body is read ONCE, by whichever door was used first: this one, or <see cref="BodyLines"/>. Mixing
    /// them is refused by name rather than answered with a half-consumed stream, which is the rule a big import needs
    /// (inside a suite it would otherwise look like a truncated body rather than a mistake).
    ///
    /// There is no size limit: the platform imposes none, so a huge body is accepted and spooled, and THIS member is
    /// what reads all of it into memory. A body too big for that is what <see cref="BodyLines"/> exists for.</summary>
    public string? Body
    {
        get
        {
            if (_body != null) return _body;
            if (_bodyBytesTaken)
                throw new ScriptException("ctx.Body cannot be read after ctx.RequestBytes: a request body is read "
                    + "once, and this one has already been read as bytes.");
            if (_bodyLinesTaken)
                throw new ScriptException("ctx.Body cannot be read after ctx.BodyLines(): a request body is read "
                    + "once, and this one has been taken line by line.");
            if (_bodyChunksTaken)
                throw new ScriptException("ctx.Body cannot be read after ctx.BodyChunks(): a request body is read "
                    + "once, and this one has been taken chunk by chunk.");
            if (_bodyStreamTaken)
                throw new ScriptException("ctx.Body cannot be read after Avro.OpenReader(ctx): a request body is read "
                    + "once, and this one has been streamed record by record.");
            _bodyReadWhole = true;
            _body = ReadBodyWhole();
            return _body;
        }
        set => _body = value;
    }

    /// <summary>The request body LINE BY LINE, so a handler can import a big NDJSON payload without holding it in
    /// memory: `foreach (var line in ctx.BodyLines()) { ... }` reads one line at a time from the request's stream.
    ///
    /// Three rules, each with a reason:
    /// * the body is consumed ONCE, so a second call (or a `ctx.Body` read) is refused by NAME;
    /// * `ctx.RequestAborted` is checked BETWEEN lines, and an aborted read THROWS rather than ending quietly: a
    ///   truncated import must not read as a complete one, and the handler's own `catch` can then report how many
    ///   lines arrived;
    /// * a line is the raw text, NOT parsed, so the handler owns the format: it can skip and count the lines it
    ///   cannot use, which is the choice a bulk import needs.
    ///
    /// The lines are only readable while the request is being handled.</summary>
    public IEnumerable<string> BodyLines()
    {
        if (_bodyBytesTaken) throw new ScriptException("ctx.BodyLines() cannot be used after ctx.RequestBytes: "
            + "a request body is read once, and this one has already been read as bytes.");
        if (BodyWasGiven) throw new ScriptException("ctx.BodyLines() cannot be used here: this request's body was "
            + "set by the program rather than arriving from a client.");
        if (_bodyLinesTaken) throw new ScriptException("ctx.BodyLines() was already called: a request body is read "
            + "once, so iterate the sequence it answered rather than asking for a second one.");
        if (_bodyReadWhole) throw new ScriptException("ctx.BodyLines() cannot be used after ctx.Body: the body was "
            + "already read whole, so there is nothing left to read line by line.");
        if (_bodyChunksTaken) throw new ScriptException("ctx.BodyLines() cannot be used after ctx.BodyChunks(): a "
            + "request body is read once, and this one has been taken chunk by chunk.");
        if (_bodyStreamTaken) throw new ScriptException("ctx.BodyLines() cannot be used after Avro.OpenReader(ctx): "
            + "a request body is read once, and this one has been streamed record by record.");
        _bodyLinesTaken = true;
        return ReadBodyLines();
    }

    /// <summary>The request body as BYTES, for a body that is not text: an Avro container, an image, a zip. It reads
    /// the request's stream RAW, so nothing decodes a binary payload as UTF-8 on the way in (which a `ctx.Body` read
    /// of a container would do, changing every byte above 0x7F).
    ///
    /// The three rules are `ctx.Body`'s, and they exist for the same reason: the body is read ONCE, so a read through
    /// this door and then through `ctx.Body` or `ctx.BodyLines()` is REFUSED by name rather than answered with a
    /// half-consumed stream; a request with no body answers null (a GET); and a body the PROGRAM set is that text's
    /// UTF-8 bytes, which consumes nothing, so the textual doors stay open.
    ///
    /// `Avro.Read(SlcFile, ctx.RequestBytes)` is the pair this exists for, and `http.Avro(...)` is the
    /// answer side: one route can store what the other sends with no base64 in between.</summary>
    public byte[]? RequestBytes
    {
        get
        {
            if (_bodyBytes != null) return _bodyBytes;
            if (_bodyReadWhole)
                throw new ScriptException("ctx.RequestBytes cannot be read after ctx.Body(): a request body is read "
                    + "once, and this one has already been read as text.");
            if (_bodyLinesTaken)
                throw new ScriptException("ctx.RequestBytes cannot be read after ctx.BodyLines(): a request body is "
                    + "read once, and this one has been taken line by line.");
            if (_bodyChunksTaken)
                throw new ScriptException("ctx.RequestBytes cannot be read after ctx.BodyChunks(): a request body is "
                    + "read once, and this one has been taken chunk by chunk.");
            if (_bodyStreamTaken)
                throw new ScriptException("ctx.RequestBytes cannot be read after Avro.OpenReader(ctx): a request body "
                    + "is read once, and this one has been streamed record by record.");
            if (RequestBodyStream == null)
            {
                // The PROGRAM set this body (a middleware building a sub-request, or a suite), so its bytes are that
                // text's UTF-8. Nothing is consumed, which is why this door does not mark the body as taken.
                return _body == null ? null : Encoding.UTF8.GetBytes(_body);
            }
            _bodyBytesTaken = true;
            _bodyBytes = ReadBodyBytesWhole();
            return _bodyBytes;
        }
        set => _bodyBytes = value;
    }

    /// <summary>The request body as a SEQUENCE OF CHUNKS, for a body that is neither text nor small enough to hold: an
    /// upload of several gigabytes, which is written to disk one chunk at a time.
    ///
    ///     await foreach (var chunk in ctx.BodyChunks(1048576)) { writer.Write(chunk); }
    ///
    /// The answer is an <see cref="IAsyncEnumerable{T}"/> (of `byte[]`), because that is the spelling a script has for
    /// "elements arrive one step at a time": `await foreach` reads it, disposing the sequence on every exit. Each
    /// element is a freshly allocated array of exactly the bytes read, `ctx.RequestAborted` is checked between
    /// elements, and a body whose length the request DECLARED is compared at the end, so an upload that stopped early
    /// THROWS rather than reading as a complete file. `<paramref name="chunkSize"/>` defaults to 64 KB, is raised to at
    /// least 1 KB and lowered to at most 8 MB, so a typo cannot ask for one allocation of the whole body.
    /// <paramref name="cancellationToken"/> is asked between elements too: a caller that stops itself (`var cts = new
    /// CancellationTokenSource(); ... cts.Cancel();` in the body) ends the read at the NEXT chunk with a refusal
    /// naming how far the body had got, rather than after it.
    ///
    /// The three rules are `ctx.Body`'s: the body is read ONCE (this door, `ctx.Body`, `ctx.BodyLines()` and
    /// `ctx.RequestBytes` exclude one another, each refusal naming the door already used), the sequence opens ONCE
    /// (iterate it, do not ask for a second one), and the chunks are only readable while the request is being handled.
    /// A body the PROGRAM set answers its UTF-8 bytes as one chunk and consumes nothing.
    ///
    /// `ctx.RequestBodyLength` is the length the request declared, or -1 when it declared none (a chunked body), which
    /// is what a progress line and a "these bytes arrived" check read.</summary>
    public IAsyncEnumerable<byte[]> BodyChunks(int chunkSize = 0,
        CancellationToken cancellationToken = default)
    {
        if (_bodyBytesTaken) throw new ScriptException("ctx.BodyChunks() cannot be used after ctx.RequestBytes: a "
            + "request body is read once, and this one has already been read as bytes.");
        if (_bodyReadWhole) throw new ScriptException("ctx.BodyChunks() cannot be used after ctx.Body: the body was "
            + "already read whole, so there is nothing left to read chunk by chunk.");
        if (_bodyLinesTaken) throw new ScriptException("ctx.BodyChunks() cannot be used after ctx.BodyLines(): a "
            + "request body is read once, and this one has been taken line by line.");
        if (_bodyStreamTaken) throw new ScriptException("ctx.BodyChunks() cannot be used after Avro.OpenReader(ctx): "
            + "a request body is read once, and this one has been streamed record by record.");
        var size = chunkSize <= 0 ? DefaultChunkSize : Math.Clamp(chunkSize, MinChunkSize, MaxChunkSize);
        if (RequestBodyStream == null)
        {
            // The PROGRAM set this body, so its bytes are that text's UTF-8, exactly as `ctx.RequestBytes` reads them,
            // and nothing is consumed: the textual doors stay open, which is what makes a suite able to build a
            // request and drive this door with no socket in between.
            return new RequestBodyChunks(_body == null ? [] : Encoding.UTF8.GetBytes(_body), size, cancellationToken);
        }
        _bodyChunksTaken = true;
        return new RequestBodyChunks(this, size, cancellationToken);
    }

    /// <summary>The one chunk size default, and the bounds a stated one is clamped to: 64 KB is the size the platform
    /// itself buffers a request in, and 8 MB is the most one chunk may allocate (a million chunks for a terabyte is
    /// still a pull per 1 MB, which costs nothing beside the disk write the chunk is for).</summary>
    private const int DefaultChunkSize = 64 * 1024;
    private const int MinChunkSize = 1024;
    private const int MaxChunkSize = 8 * 1024 * 1024;

    /// <summary>The number of bytes the request DECLARED in its `Content-Length`, or -1 when it declared none: a
    /// `Transfer-Encoding: chunked` body arrives with no length at all, and a body the program set has none either.
    /// `ctx.BodyChunks()` compares what it read against this, so a body that stopped early is refused rather than
    /// reported as complete.</summary>
    public long RequestBodyLength { get; set; } = -1;

    /// <summary>The one line splitter for a body already in memory. A line ends at CRLF, LF or CR alone, which are the
    /// three terminators a text file can carry (Windows, Unix, an old Mac) and exactly what <c>StreamReader.ReadLine</c>
    /// does on the streamed path, so a body a program SET and a body a client SENT cannot disagree about where a line
    /// ends. A trailing terminator does not produce an empty last line, for the same reason.
    /// <para>
    /// It takes a <c>string</c> and NOT a <c>ReadOnlySpan&lt;char&gt;</c>, and that is measured rather than stylistic: the
    /// caller already holds a string (this is the in-memory path, where a program assigned the body), every line is handed
    /// to a script as a <c>string</c> anyway, and over a 5,000-line body a span enumerator with <c>ToString()</c> per line
    /// allocated 239,144 B against this shape's 232,056 B, i.e. it saved nothing. It measured 3x faster at the splitting
    /// itself (0.148 ms against 0.444 ms) because a ref struct's MoveNext is inlined while an iterator pays one interface
    /// call per line, which is 0.3 ms on a call that happens once per request. A lazy span version cannot exist with this
    /// signature at all: an iterator may not yield a ref struct (CS9267).
    /// </para>
    /// <para>
    /// The PRIMITIVE for "where does a line end" is <see cref="TextLines.LineSplitter"/>, which is a ref struct and
    /// therefore cannot be yielded; this loop is the ONE copy that exists because of that, so a change to the terminator
    /// rule has to land in both and the pair is pinned by the CR/LF/CR checks of http_streaming_test.
    /// </para></summary>
    private static IEnumerable<string> SplitBodyLines(string body)
    {
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '\r' && c != '\n')
                continue;
            yield return body[start..i];
            if (c == '\r' && i + 1 < body.Length && body[i + 1] == '\n')
                i++;                       // CRLF is ONE terminator, not two
            start = i + 1;
        }
        if (start < body.Length)
            yield return body[start..];
    }

    /// <summary>The one lazy reader behind <see cref="BodyLines"/>: one line per step, the token asked in between,
    /// and the reader kept so a second move continues where the first stopped.</summary>
    private IEnumerable<string> ReadBodyLines()
    {
        // A body a PROGRAM SET wins over the request's own stream, because that is what `ctx.Body` already reports: a
        // handler that normalises or decompresses a body and assigns it back must read ITS lines. Measured before this
        // order was fixed: `ctx.Body = "a\rb\rc\r"` then `ctx.BodyLines()` answered ZERO lines, because a served request
        // always has an InputStream (even a GET), so the stream branch was taken and the assignment was invisible.
        if (_body != null)
        {
            foreach (var line in SplitBodyLines(_body))
                yield return line;
            yield break;
        }
        if (RequestBodyStream == null)
            yield break;
        _bodyReader ??= new StreamReader(RequestBodyStream, Encoding.UTF8);
        var read = 0;
        while (true)
        {
            if (RequestAborted.IsCancellationRequested)
                throw new ScriptException($"the request was aborted after {read} body line(s): the client went away, "
                    + "so the body is incomplete.");
            var line = _bodyReader.ReadLine();
            if (line == null) yield break;
            read++;
            yield return line;
        }
    }

    /// <summary>The whole body, read now and kept. Null for an empty one, which is what a GET sends: a script tests
    /// `ctx.Body != null` to tell "no body" from "an empty one", the rule the docs state.</summary>
    private string? ReadBodyWhole()
    {
        if (RequestBodyStream == null) return null;
        _bodyReader ??= new StreamReader(RequestBodyStream, Encoding.UTF8);
        var text = _bodyReader.ReadToEnd();
        return text.Length > 0 ? text : null;
    }

    /// <summary>The whole body as BYTES: the stream copied raw, so nothing is decoded. Null for an empty one, the rule
    /// <see cref="ReadBodyWhole"/> follows.</summary>
    private byte[]? ReadBodyBytesWhole()
    {
        if (RequestBodyStream == null) return null;
        using var buffer = new MemoryStream();
        RequestBodyStream.CopyTo(buffer);
        return buffer.Length > 0 ? buffer.ToArray() : null;
    }

    /// <summary>The request body as a STREAM, for a reader that walks it piece by piece and cannot hold it: an Avro
    /// container, which is what `Avro.OpenReader(ctx)` takes. The stream is the request's own, it cannot seek, and it
    /// is only readable while the request is being handled.
    ///
    /// The rules are the other doors', with one addition: the body is read ONCE, so this door and `ctx.Body`,
    /// `ctx.BodyLines()`, `ctx.RequestBytes` and `ctx.BodyChunks()` exclude one another and each refusal names the door
    /// already used; a request with no body refuses by name; a body the PROGRAM set is accepted as that text's UTF-8
    /// bytes in a `MemoryStream`, which consumes nothing, so a suite can drive this door with no socket in between.
    ///
    /// WHAT THE CALLER MUST DO: read the stream to the end, or drain it, before the answer. The server drains only a
    /// body NOBODY touched (see `BodyWasTouched`), so a body taken here is the caller's to finish.</summary>
    internal RequestBodyTake TakeRequestBodyStream(string member)
    {
        if (_bodyBytesTaken)
            throw new ScriptException($"{member}: the request body was already read by ctx.RequestBytes, so there is "
                + "nothing left to stream.");
        if (_bodyReadWhole)
            throw new ScriptException($"{member}: the request body was already read by ctx.Body, so there is nothing "
                + "left to stream.");
        if (_bodyLinesTaken)
            throw new ScriptException($"{member}: the request body was already read by ctx.BodyLines(), so there is "
                + "nothing left to stream.");
        if (_bodyChunksTaken)
            throw new ScriptException($"{member}: the request body was already taken by ctx.BodyChunks(), so there is "
                + "nothing left to stream.");
        if (_bodyStreamTaken)
            throw new ScriptException($"{member}: the request body was already taken by Avro.OpenReader(ctx): a "
                + "request body is read once.");
        if (RequestBodyStream == null)
        {
            // A body the PROGRAM set has no stream: it is that text's UTF-8 bytes, exactly as `ctx.RequestBytes`
            // answers them, in a stream this caller owns because this call made it.
            if (_body == null)
                throw new ScriptException($"{member}: this request has no body to read: a GET sends none, and no body "
                    + "was set either.");
            return new RequestBodyTake(new MemoryStream(Encoding.UTF8.GetBytes(_body), writable: false),
                OwnsStream: true, DrainOnClose: false);
        }
        // A request that declared nothing carries nothing: a GET, which has a stream that is already at its end. The
        // reader would otherwise report a container that stopped inside its magic, which is true but useless.
        if (RequestBodyLength == 0)
            throw new ScriptException($"{member}: this request carries no body (its Content-Length is 0), so there is "
                + "nothing to stream.");
        // The request's own stream belongs to the SERVER: what a caller owns is whatever it puts over this, and what
        // it must do on the way out is drain, because the server will not.
        _bodyStreamTaken = true;
        return new RequestBodyTake(RequestBodyStream, OwnsStream: false, DrainOnClose: true);
    }

    /// <summary>What <see cref="TakeRequestBodyStream"/> hands over: the stream to walk, whether the CALLER owns it (a
    /// body the program set is a `MemoryStream` built for this call, while the request's own stream belongs to the
    /// server), and whether it has to be drained on the way out so a kept-alive connection is not left with an unread
    /// payload.</summary>
    internal readonly record struct RequestBodyTake(Stream Stream, bool OwnsStream, bool DrainOnClose);

    /// <summary>True when the program SET the body instead of a client sending one (a middleware building a
    /// sub-request, or a suite), which is why line reading refuses: there is no stream to read.</summary>
    internal bool BodyWasGiven => RequestBodyStream == null && _body != null;

    /// <summary>Has anything read the body? The server drains an untouched one so a kept-alive connection is not left
    /// with an unread payload. A body taken as a STREAM counts as touched: the reader that took it drains what it did
    /// not consume when it closes, and draining the same payload twice would break the connection.</summary>
    internal bool BodyWasTouched => _body != null || _bodyLinesTaken || _bodyReadWhole || _bodyBytesTaken
        || _bodyChunksTaken || _bodyStreamTaken;

    /// <summary>The request's body stream, handed over by the server, which owns it. Null when the program built this
    /// context itself, and then <see cref="Body"/> is whatever it set.</summary>
    internal Stream? RequestBodyStream { get; set; }

    private StreamReader? _bodyReader;
    private string? _body;
    private byte[]? _bodyBytes;
    private bool _bodyReadWhole;
    private bool _bodyLinesTaken;
    private bool _bodyBytesTaken;
    private bool _bodyChunksTaken;
    private bool _bodyStreamTaken;

    /// <summary>The request's headers, because a middleware sometimes has to know WHO is asking. HTMX identifies
    /// itself with `HX-Request: true`, and a person typing the URL sends nothing: that is the difference between
    /// answering a fragment (what the polling element wants) and answering the whole page (what a browser needs, or
    /// it shows an unstyled fragment with no layout at all).</summary>
    public Dictionary<string, string> RequestHeaders { get; set; } = [];
    public int ResponseStatus { get; set; } = 200;
    public string? ResponseBody { get; set; }
    public byte[]? ResponseBytes { get; set; }

    /// <summary>The answer STREAMS rather than carrying bytes: the server writes it with chunked framing, one flush
    /// per element. Not script-visible on purpose (the `http` entry documents ten members and this is a transport
    /// detail): a route produces one by RETURNING what `http.StreamLines`/`http.StreamEvents` answer.</summary>
    internal HttpStream? ResponseStream { get; set; }

    /// <summary>What the answer IS. Without it the server could only send bytes as application/octet-stream and a
    /// text body with no type at all, so a script could serve an icon, an image or a download but never say what it
    /// was. A middleware sets it; the server falls back to the two defaults.</summary>
    public string? ResponseContentType { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Headers to send with the answer: a result's own `Location`, or a `Cache-Control` a handler sets
    /// itself. The server writes them before the body, and a name the platform refuses is REPORTED rather than
    /// dropped in silence.</summary>
    public Dictionary<string, string> ResponseHeaders { get; set; } = [];
}
