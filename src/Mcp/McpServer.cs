namespace ScriptLangCore;

internal static class McpServer
{
    private static readonly Dictionary<string, McpTool> Tools = [];
    private static readonly Dictionary<string, McpResource> Resources = [];
    private static volatile HttpListener? _httpListener;

    // What this process was launched to serve with, remembered so the listener can be brought back after an interrupt
    // stopped it (reported: pressing Ctrl+C stops the server well, but running the app again then serves nothing, "the
    // thread has been destroyed but not renewed"). A session that never called Run keeps these null, so nothing starts a
    // listener on its own.
    private static string? _serverUrl;
    private static string[] _serverAllowedDirs = [];

    // Set while a restart is in flight, so two quick runs cannot both start one: the second Start would fail on a prefix
    // that is already registered, and a logged failure is a worse outcome than a quiet no-op.
    private static int _serverStarting;

    /// <summary>
    /// Stops the HTTP listener this process is serving on, so a REPL session can take its server down and keep going.
    /// `HttpListener.Stop` is what unblocks the pending `GetContext` the listen loop is parked in: it signals that call
    /// with an HttpListenerException and `IsListening` goes false, so the loop leaves on either test. Answers whether a
    /// listener was actually running, which is what lets the caller say so instead of guessing.
    /// </summary>
    internal static bool StopHttpServer()
    {
        var listener = _httpListener;
        if (listener == null) return false;
        _httpListener = null;
        try { listener.Stop(); } catch { /* already gone */ }
        try { listener.Close(); } catch { /* already gone */ }
        return true;
    }

    /// <summary>
    /// Serves again if this session was launched to serve and its listener is gone. Called when the REPL runs a script,
    /// so the `.run` a reader reaches for after an interrupt heals the session: otherwise the routes register into a
    /// pipeline that nothing is reading, which is what a stopped listener left behind.
    /// </summary>
    internal static void EnsureHttpServerServing()
    {
        if (_httpListener != null) return;                 // already serving
        var url = _serverUrl;
        if (url == null) return;                           // this session was never launched to serve
        if (Interlocked.CompareExchange(ref _serverStarting, 1, 0) != 0) return;   // a restart is already under way
        var url2 = url;
        var dirs = _serverAllowedDirs;
        new Thread(() =>
        {
            try { Run(url2, dirs); }
            catch (Exception ex) { Logging.Log.Error("MCP", $"could not serve again on {url2}: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _serverStarting, 0); }
        })
        { IsBackground = true, Name = "MCP-Server" }.Start();
        Logging.Log.Info("MCP", $"serving again on {url2}");
    }
    /// <summary>The request currently being handled. The HTTP loop is sequential, so one slot is
    /// enough; it is cancelled when the server stops or a socket failure is detected.</summary>
    private static volatile CancellationTokenSource? _currentRequestCts;
    private static McpSandbox? _sandbox;

    public static string? ServerUrl { get; private set; }
    public static List<McpTool> ListTools() { lock (Tools) { return [.. Tools.Values]; } }

    private static WeakReference<Interpreter>? _toolInterpreter;
    private static Interpreter? _mcpInterpreter;
    internal static Interpreter? McpInterpreter => _mcpInterpreter;


    /// <summary>Gives the HTTP pipeline an interpreter to invoke middleware on, when it has none. `_mcpInterpreter`
    /// is captured while the server starts, and it is only non-null when an interpreter happened to be current then:
    /// measured, loading the engine's own sample tools before the server starts leaves one, so their middlewares
    /// run, while `--tools-dir &lt;empty&gt;` (or an app loaded later through `.run`) leaves null, and then EVERY
    /// `http.Use` middleware is skipped in silence: requests answer an empty 200 and nothing is logged. Filling it at
    /// registration keeps the captured value whenever there is one.</summary>
    /// <summary>Hands the HTTP pipeline the interpreter it should invoke middleware on. The pipeline keeps ONE
    /// interpreter across requests, so it has to be one that OUTLIVES them, and the REPL session's is: a tool's is
    /// disposed once the tool has been loaded (measured: requests then answer `Cannot access a disposed object ...
    /// ThreadLocal&lt;Stack&lt;CallFrame&gt;&gt;`). The capture in `Run` remains a fallback for a server nobody
    /// hands one over, and it can land on a tool's interpreter, which is why the REPL says so explicitly.</summary>
    internal static void UseInterpreter(Interpreter itp) => _mcpInterpreter = itp;

    internal static void EnsureInterpreter(Interpreter itp)
    {
        // A DISPOSED interpreter is worse than none: its per-thread state is gone, so a middleware invoked on it
        // throws, and before that was made visible the request answered an empty 200 with nothing logged, which is
        // the failure this whole thread chased. Keep a captured interpreter while it is usable, take the registering
        // one otherwise.
        if (_mcpInterpreter is { IsDisposed: false }) return;
        _mcpInterpreter = itp;
    }
    public static MiddlewarePipeline<McpContext>? ToolPipeline { get; set; }
    public static MiddlewarePipeline<HttpContext>? HttpPipeline { get; set; }

    // ── Pooled StringBuilder (thread-static, avoids per-call allocation) ──
    [ThreadStatic] private static StringBuilder? _tsBuf;
    [ThreadStatic] private static StringBuilder? _tsBuf2;

    private static StringBuilder Borrow() => Borrow(ref _tsBuf);
    private static StringBuilder Borrow(ref StringBuilder? slot)
    {
        if (slot == null)
            return slot = new StringBuilder(256);
        slot.Clear(); return slot;
    }

    public static void RegisterTool(string name, string description, ScriptFunction handler, string? schemaJson)
    {
        lock (Tools)
        {
            Tools[name] = new McpTool(name, description, handler, schemaJson);
            if (Current != null)
            {
                _toolInterpreter ??= new WeakReference<Interpreter>(Current);
                Current.IsMcpHost = true;
                _mcpInterpreter = Current;
            }
        }
    }

    public static void RegisterResource(string uri, string name, string mimeType, ScriptFunction handler)
    {
        lock (Resources)
        {
            Resources[uri] = new McpResource(uri, name, mimeType, handler);
            if (Current != null)
            {
                _toolInterpreter ??= new WeakReference<Interpreter>(Current);
                Current.IsMcpHost = true;
            }
        }
    }

    public static void Run(string? url, string[] allowedDirs)
    {
        // Remembered so EnsureHttpServerServing can bring the listener back with the same URL and directories.
        _serverUrl = url;
        _serverAllowedDirs = allowedDirs;
        // Normalize: HttpListener requires URL prefix to end with '/'
        if (url != null && !url.EndsWith("/"))
            url = url + "/";
        ServerUrl = url;
        _sandbox = allowedDirs.Length > 0 ? new McpSandbox(allowedDirs) : null;
        if (url != null) RunSse(url); else RunStdio();
    }

    private static void RunStdio()
    {
        Logging.Log.Info("MCP", "STDIO server started");
        while (Console.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            Console.WriteLine(Dispatch(line));
            Console.Out.Flush();
        }
    }

    private static void RunSse(string url)
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(url);
            _httpListener.Start();
            Logging.Log.Info("MCP", $"SSE server on {url}");
        }
        catch (Exception ex)
        {
            Logging.Log.Error("MCP", $"Failed to start SSE server on {url}: {ex.Message}");
            return;
        }
        while (_httpListener.IsListening)
        {
            try
            {
                var ctx = _httpListener.GetContext();
                var req = ctx.Request; var res = ctx.Response;

                // Per-request cancellation scope: gives handlers (and the Http calls they make) an
                // ambient token tied to this request, exposed to middleware as RequestAborted.
                using var requestCts = new CancellationTokenSource();
                _currentRequestCts = requestCts;
                // CORS headers for browser-based testing
                res.Headers["Access-Control-Allow-Origin"] = "*";
                res.Headers["Access-Control-Allow-Methods"] = "POST, GET, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "Content-Type";

                // Handle preflight OPTIONS request
                if (req.HttpMethod == "OPTIONS")
                {
                    res.StatusCode = 204;
                    res.Close();
                    continue;
                }
                Log("debug", $"↓ {req.HttpMethod} {req.Url?.AbsolutePath} from {req.RemoteEndPoint}");

                if (req is { HttpMethod: "POST", Url.AbsolutePath: "/" })
                {
                    // Log all request headers at debug level
                    foreach (string key in req.Headers)
                        Log("debug", $"  {key}: {req.Headers[key]}");

                    using var r = new StreamReader(req.InputStream, Encoding.UTF8);
                    var body = r.ReadToEnd();
                    Log("debug", $"  Body[{body.Length}]: {(body.Length > 200 ? body[..200] + "..." : body)}");
                    byte[] buf;
                    if (McpInterpreter is { } itpDispatch)
                    {
                        // Ambient scope: Http calls inside the tool handler inherit this request token.
                        using (itpDispatch.PushRequestCancellation(requestCts))
                            buf = Encoding.UTF8.GetBytes(Dispatch(body));
                    }
                    else buf = Encoding.UTF8.GetBytes(Dispatch(body));
                    res.ContentType = "application/json";
                    res.ContentLength64 = buf.Length;
                    res.OutputStream.Write(buf); res.Close();
                }
                else if (req is { HttpMethod: "GET", Url.AbsolutePath: "/sse" })
                {
                    res.ContentType = "text/event-stream";
                    res.Headers["Cache-Control"] = "no-cache";
                    var d = Encoding.UTF8.GetBytes("data: {\"endpoint\":\"/\"}\n\n");
                    res.OutputStream.Write(d); res.OutputStream.Flush();
                    try { while (true) Thread.Sleep(30_000); } catch { break; }
                }
                else { HandleHttpMiddleware(ctx, req, res, requestCts); }
            }
            catch (HttpListenerException ex)
            {
                // Best-effort client-disconnect signal: the request's token is cancelled so any
                // work it started (Http calls) stops.
                CancelInFlight(_currentRequestCts);
                // ERROR_OPERATION_ABORTED (995) = listener stopped, exit loop
                // error 411 (Length Required) = client error, just continue
                if (ex.ErrorCode == 995 || ex.NativeErrorCode == 995)
                    break;
                Logging.Log.Error("MCP", $"HTTP error: {ex.Message} (code={ex.ErrorCode})");
            }
            catch (Exception ex)
            {
                CancelInFlight(_currentRequestCts);
                Logging.Log.Error("MCP", $"Request error: {ex.Message}");
            }
            finally
            {
                _currentRequestCts = null;
            }
        }
        // Server stopped: cancel whatever request was in flight (best-effort).
        CancelInFlight(_currentRequestCts);
    }

    /// <summary>Best-effort cancel of the request in flight. The source can ALREADY be disposed when the server
    /// stops or when the request finished on its own, and the unguarded call killed the whole process: measured, a
    /// serve run ended with an unhandled ObjectDisposedException at the catch-all arm, a stack trace and exit 1,
    /// which takes the server down with it. A cancel that cannot happen is not an error worth that.</summary>
    private static void CancelInFlight(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { /* the request is already finished */ }
    }

    /// <summary>The request's headers as a plain map, case-insensitively keyed, for `ctx.RequestHeaders`.</summary>
    private static Dictionary<string, string> HeaderMap(System.Collections.Specialized.NameValueCollection headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? key in headers.AllKeys)
            if (key != null) map[key] = headers[key] ?? "";
        return map;
    }

    /// <summary>Answers with a status and an EMPTY body, framed as `Content-Length: 0`, which is what ASP.NET Core sends
    /// for a bare 404 and what a browser needs. Without it the platform picks `Transfer-Encoding: chunked` for a bodiless
    /// answer, and a NAVIGATION refuses such a response (`net::ERR_HTTP_RESPONSE_CODE_FAILURE`) instead of showing its own
    /// not-found page, which is how this was reported from a live session. ONE place for it: this server has TWO
    /// framework-set statuses that carry no body (no pipeline is registered at all, and a pipeline that answered
    /// nothing).</summary>
    private static void CloseWithEmptyBody(HttpListenerResponse res)
    {
        res.ContentLength64 = 0;
        res.Close();
    }

    /// <summary>The interpreter a page can be registered on, for the OTHER HTTP writer (the engine's own socket server,
    /// which serves files): `http.ErrorPage` is recorded per session, and this is the session that owns it. Null when
    /// nothing is serving through the MCP server, so the socket path answers the engine's own default page.</summary>
    internal static Interpreter? CurrentHttpInterpreter => McpInterpreter;

    /// <summary>Answers a status the FRAMEWORK set (nothing matched, or an unhandled failure), with the page the program
    /// registered for that code or the engine's own default, and states the length so the framing is explicit. A status
    /// the HTTP spec forbids a body on never comes through here.</summary>
    private static void WriteFrameworkStatus(HttpListenerResponse res, int status, string reason, object? request)
    {
        var (body, contentType) = McpInterpreter?.ErrorPageFor(status, reason, request)
            ?? (HttpErrorPages.Default(status, reason), HttpErrorPages.ContentType);
        var payload = ResponseTextEncoding.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = payload.Length;
        res.OutputStream.Write(payload);
        res.Close();
    }

    /// <summary>What an API is asked for when it wants a body it can only produce over time: chunked framing, one
    /// element at a time, one FLUSH per element so the client sees each line as it is produced rather than when the
    /// answer ends.
    ///
    /// TWO ORDERING RULES, both measured on this platform (.NET 11 RC3), and the first one corrects the note the plan
    /// was written from: `SendChunked = true` must come BEFORE the first write, and `ContentLength64` must NEVER be
    /// set afterwards. Setting it does not throw as one would expect: it is ACCEPTED, it silently turns chunking off
    /// (`SendChunked` reads False again), and then every write is refused with a ProtocolViolationException while the
    /// client receives a `Content-Length: 0` and an empty body. So a streamed answer must not touch the length at all.
    ///
    /// A failure can only END the stream, never change the status: the headers went out with the first element. An
    /// error raised by the producer is therefore reported in the LOG (through the engine's one message map) and the
    /// stream closes there, which is the rule the `http` entry states and the first thing an author gets wrong.
    ///
    /// A BROKEN CONNECTION CANCELS THE REQUEST, and that is what makes `ctx.RequestAborted` useful to a handler: this
    /// server has no abort event (HttpListener does not offer one), so a failed WRITE is the only evidence that the
    /// client went away. Cancelling the request's source is what a handler's third parameter sees, so a producer that
    /// guards itself with the token stops at its next check instead of publishing into nothing.</summary>
    private static void WriteChunkedStream(HttpListenerResponse res, HttpStream stream, HttpContext hctx,
        CancellationTokenSource requestCts)
    {
        var format = stream.Format;
        res.ContentType = stream.ContentType ?? HttpStreamFraming.ContentTypeFor(format);
        var framed = new Dictionary<string, string>();
        HttpStreamFraming.ApplyHeaders(format, framed);
        foreach (var header in framed)
        {
            try { res.Headers[header.Key] = header.Value; }
            catch (Exception hex)
            {
                Logging.Log.Error("MCP", $"HTTP response header '{header.Key}' was refused: {Interpreter.FriendlyExceptionMessage(hex)}");
            }
        }
        res.SendChunked = true;
        var token = hctx.RequestAborted;
        var sent = 0;
        var broke = false;
        try
        {
            var source = stream.Source;
            try
            {
                while (true)
                {
                    // Between elements, which is the only moment the writer owns: a long-lived stream is cancelled
                    // when the client goes away, and the producer stops instead of writing into a dead socket.
                    if (token.IsCancellationRequested)
                    {
                        Logging.Log.Info("MCP", $"HTTP stream '{hctx.Url}' stopped after {sent} element(s): the request was aborted.");
                        broke = true;
                        break;
                    }
                    if (!source.MoveNext(out var item)) break;
                    var bytes = ResponseTextEncoding.GetBytes(HttpStreamFraming.Frame(format, item));
                    res.OutputStream.Write(bytes, 0, bytes.Length);
                    // Flush per element, or the platform decides when the client sees a line: measured, three
                    // elements 700 ms apart arrived at 2 / 709 / 1416 ms with a flush and in one burst without.
                    res.OutputStream.Flush();
                    sent++;
                }
            }
            finally
            {
                // The producer owns whatever it opened (a file, a feed subscription), so it is released on EVERY
                // exit, a mid-stream failure included.
                try { source.Dispose(); }
                catch (Exception dex) { Logging.Log.Error("MCP", $"HTTP stream '{hctx.Url}' cleanup failed: {Interpreter.FriendlyExceptionMessage(dex)}"); }
            }
        }
        catch (HttpListenerException ex)
        {
            // The client is gone. That is what a closed tab looks like from here, and the token fires too, so it is
            // not an engine failure and not worth an error line.
            Logging.Log.Info("MCP", $"HTTP stream '{hctx.Url}' ended after {sent} element(s): the client disconnected (code={ex.ErrorCode}).");
            broke = true;
            // The only evidence this server gets that a client went away: cancel the request so the handler's own
            // CancellationToken sees it.
            CancelInFlight(requestCts);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Logging.Log.Error("MCP", $"HTTP stream '{hctx.Url}' failed after {sent} element(s), so the stream ends there: "
                + Interpreter.FriendlyExceptionMessage(inner));
            broke = true;
        }
        finally
        {
            // A stream that ended EARLY is ABORTED rather than closed, and the difference is what the client sees: a
            // Close() writes the terminating zero-length chunk, which tells a chunked reader the body was COMPLETE, so
            // a failure part-way would look like a short successful answer. Aborting drops the connection instead,
            // which is how a chunked reader learns the body is incomplete.
            try
            {
                if (broke) res.Abort();
                else res.Close();
            }
            catch (Exception) { /* the connection may already be closed: the answer is over either way */ }
        }
    }

    private static void HandleHttpMiddleware(HttpListenerContext httpCtx, HttpListenerRequest req,
        HttpListenerResponse res, CancellationTokenSource requestCts)
    {
        if (HttpPipeline == null || HttpPipeline.Count == 0)
        {
            // 404 with the page for it, and the reason goes to the log as well: this is the engine's OWN status (no
            // `http.Use` is registered at all), so the page is the engine's default unless the program registered one
            // with `http.ErrorPage`.
            Logging.Log.Warning("MCP", $"HTTP {req.HttpMethod} {req.Url?.AbsolutePath} answered 404: no http.Use pipeline is registered.");
            WriteFrameworkStatus(res, 404, "Not Found", new HttpContext
            {
                HttpMethod = req.HttpMethod,
                Url = req.Url?.AbsolutePath ?? "/",
                RequestHeaders = HeaderMap(req.Headers),
                RequestAborted = requestCts.Token
            });
            return;
        }

        // THE BODY IS NOT READ HERE ANY MORE, and that is what makes an incrementally read import possible: a
        // handler asks for `ctx.Body` (whole, read on first use) or `ctx.BodyLines()` (line by line), and whichever
        // it asks for first owns the body. Reading it eagerly here would defeat the second door entirely: measured
        // reason it matters, a 100 MB NDJSON import would be one string before the handler ever ran.
        var hctx = new HttpContext
        {
            HttpMethod = req.HttpMethod,
            Url = req.Url?.AbsolutePath ?? "/",
            RequestBodyStream = req.InputStream,
            RequestHeaders = HeaderMap(req.Headers),
            RequestAborted = requestCts.Token
        };

        try
        {
            if (McpInterpreter is { } itpScope)
            {
                // Http calls made by the handler inherit this request's token.
                using (itpScope.PushRequestCancellation(requestCts))
                    HttpPipeline.Execute(hctx);
            }
            else HttpPipeline.Execute(hctx);
        }
        catch (Exception ex)
        {
            // A middleware that throws used to be swallowed right here: the client got an empty 200 and nothing was
            // logged, which is how an app answers nothing while looking perfectly healthy. The context already turns
            // an ErrorMessage into a 500 carrying its text, so hand it over.
            // UNWRAP AND USE THE ENGINE'S ONE MAP, never `ex.Message`. A failure inside a middleware arrives as a
            // `TargetInvocationException` (reflection wraps the callee's exception) whose own Message is the useless
            // "Exception has been thrown by the target of an invocation.": a reader was shown exactly that on the page
            // and could do nothing with it. The inner exception plus the engine's map names the real cause, which is
            // how the engine's own call sites report one.
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            // An UNHANDLED exception is a 500 the ENGINE raises, so it carries NO body, exactly as ASP.NET Core
            // answers one in production (the developer exception page is a Development convenience, and
            // UseExceptionHandler + AddProblemDetails is an explicit opt-in). Nothing is lost: the cause reaches the
            // log with the engine's own message map, which is where a server's report belongs, and a body here would
            // be the engine answering on the application's behalf.
            hctx.ResponseStatus = 500;
            hctx.ResponseBody = null;
            hctx.ResponseBytes = null;
            Logging.Log.Error("MCP", $"HTTP middleware error: {ex.GetType().Name}: {inner.Message}");
            // The engine's own page, because the ENGINE answered: the cause is in the log above, and a client getting
            // nothing at all is what made an unhealthy app look answered.
            WriteFrameworkStatus(res, 500, "Internal Server Error", hctx);
            return;
        }

        // AN UNTOUCHED BODY IS DRAINED BEFORE THE ANSWER, best effort. The body is now read lazily, so a handler that
        // never asked for it would leave the payload unread, and a kept-alive connection cannot be reused while its
        // request still has bytes on the wire. A failure here changes nothing about the answer, so it is swallowed
        // with that reason rather than reported as a request error.
        if (!hctx.BodyWasTouched)
        {
            try
            {
                var sink = System.Buffers.ArrayPool<byte>.Shared.Rent(8192);
                try { while (req.InputStream.Read(sink, 0, sink.Length) > 0) { } }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(sink); }
            }
            catch (Exception) { /* the connection is already done with us either way */ }
        }

        // A middleware that SET an ErrorMessage is the APPLICATION refusing rather than the engine failing, so that
        // message IS the answer it chose and it is written: the same contract the MCP tool pipeline has, where the
        // message a middleware blocks a call with is what the caller receives.
        if (hctx.ErrorMessage != null)
        {
            res.StatusCode = 500;
            var refusal = ResponseTextEncoding.GetBytes("Error: " + hctx.ErrorMessage);
            res.ContentType ??= "text/plain; charset=utf-8";
            res.ContentLength64 = refusal.Length;
            res.OutputStream.Write(refusal);
            res.Close();
            return;
        }

        // A pipeline that answered NOTHING is a 404 carrying NO BODY, which is what ASP.NET Core does for a request no
        // endpoint matched: the status IS the message and Content-Length is 0, while the "page not found" text a
        // browser shows for it is the BROWSER's own. A sentence in the body was ours to choose and a client may render
        // it, so the reason moves to the LOG: nothing is lost for the author, and nothing is invented for the caller.
        // An `http.Use` middleware that means "an empty 200" says so with `ctx.ResponseBody = ""`, which the route
        // middleware already does for a matched route.
        if (hctx.ErrorMessage == null && hctx.ResponseBytes == null && hctx.ResponseBody == null
            && hctx.ResponseStream == null
            && hctx.ResponseStatus == 200)
        {
            // THE LINE NAMES WHAT THE SESSION DOES SERVE, which is the half a reader needs: a route that is missing
            // because its file never ran looks exactly like a typo otherwise (measured on a live session: the request
            // read only "nothing is registered for '/todos'" while six routes from the engine's own sample tool were
            // answering, which is what `--tools-dir <an empty folder>` exists to keep out of the way).
            Logging.Log.Warning("MCP", $"no middleware answered this request: nothing is registered for '{hctx.Url}'."
                + HttpRouteTable.DescribeLive() + " Register one with http.Use.");
            // A request nothing matched is the ENGINE's answer, so it carries the page for 404: the engine's default, or
            // the one the program registered. A script that means "404 with no body" says `http.NotFound()`, which
            // reaches the bodiless tail below instead, so the two decisions stay separate.
            WriteFrameworkStatus(res, 404, "Not Found", hctx);
            return;
        }

        res.StatusCode = hctx.ResponseStatus;
        // The answer's own headers, before any body: a result's `Location`, or whatever a handler set. A name the
        // platform refuses is a REPORTED failure rather than a silently dropped header.
        foreach (var header in hctx.ResponseHeaders)
        {
            try { res.Headers[header.Key] = header.Value; }
            catch (Exception hex)
            {
                Logging.Log.Error("MCP", $"HTTP response header '{header.Key}' was refused: {Interpreter.FriendlyExceptionMessage(hex)}");
            }
        }
        if (hctx.ResponseStream is { } stream)
        {
            WriteChunkedStream(res, stream, hctx, requestCts);
            return;
        }
        if (hctx.ResponseBytes != null)
        {
            res.ContentType = hctx.ResponseContentType ?? "application/octet-stream";
            res.ContentLength64 = hctx.ResponseBytes.Length;
            res.OutputStream.Write(hctx.ResponseBytes);
            res.Close();
            return;
        }
        if (hctx.ResponseBody != null)
        {
            res.ContentType = hctx.ResponseContentType ?? "text/plain; charset=utf-8";
            // The BYTE count, not the character count: this encoding is UTF-8 without a BOM, so "e" is one byte and
            // "e" with an accent is two. Writing through a StreamWriter left the length UNSTATED, which is what made
            // the platform choose chunked framing for an answer that has a body.
            var payload = ResponseTextEncoding.GetBytes(hctx.ResponseBody);
            res.ContentLength64 = payload.Length;
            res.OutputStream.Write(payload);
            res.Close();
            return;
        }
        // A status with NO body is framed as an EMPTY body, exactly what the note above promises and what ASP.NET Core
        // sends for a bare 404.
        CloseWithEmptyBody(res);
    }

    // ── MCP protocol version ──
    private const string McpVersion = "2025-03-26";

    /// <summary>The encoding a TEXT answer is written with: UTF-8 WITHOUT a byte-order mark. `Encoding.UTF8` emits
    /// the mark, so every text response carried a 3-byte `EF BB BF` prefix (measured: a 10-character body arrived as
    /// 13 bytes), which a strict JSON client rejects and which the platform refuses outright when a length was
    /// declared (a 204 or a bytes answer logged "Bytes to be written to the stream exceed the Content-Length bytes
    /// size specified").</summary>
    private static readonly Encoding ResponseTextEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ── Logging ──
    private static string? _logLevel; // null = disabled
    public static string? LogLevel => _logLevel;
    public static void SetLogLevel(string? level)
    {
        _logLevel = level;
        Logging.Log.SetLevel(level switch
        {
            "debug" => Logging.LogLevel.Debug,
            "info" => Logging.LogLevel.Information,
            "notice" => Logging.LogLevel.Information,
            "warning" => Logging.LogLevel.Warning,
            "error" => Logging.LogLevel.Error,
            _ => Logging.LogLevel.None,
        });
    }

    /// <summary>Send a structured log message, delegates to centralized Logging.Log.</summary>
    private static void Log(string level, string msg)
    {
        if (_logLevel == null) return;
        var levels = new[] { "debug", "info", "infos", "notice", "warning", "error" };
        var msgIdx = Array.IndexOf(levels, level);
        var setIdx = Array.IndexOf(levels, _logLevel);
        if (msgIdx < 0 || msgIdx < setIdx) return;

        // Route through centralized logger, preserving MCP-specific level semantics
        // HTTP headers/body use DarkYellow, dispatch/tool lines use DarkGray
        var logLevel = level switch
        {
            "debug" => Logging.LogLevel.Debug,
            "info" or "infos" => Logging.LogLevel.Information,
            "notice" => Logging.LogLevel.Information,
            "warning" => Logging.LogLevel.Warning,
            "error" => Logging.LogLevel.Error,
            _ => Logging.LogLevel.Information,
        };
        var source = level switch { "info" => "infos", _ => level };
        Logging.Log.Raw(logLevel, $"[{source}] {msg}", LogColorFor(level, msg));
    }

    private static ConsoleColor LogColorFor(string level, string msg) => level switch
    {
        "debug" => msg.StartsWith("↓ ") || msg.StartsWith("  ") ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray,
        "info" => ConsoleColor.DarkGray,
        "notice" => ConsoleColor.Gray,
        "warning" => ConsoleColor.Yellow,
        "error" => ConsoleColor.Red,
        _ => ConsoleColor.DarkGray,
    };

    // ── JSON-RPC dispatch ──

    internal static string Dispatch(string rawJson)
    {
        try
        {
            var parsed = JsonParseExtensions.ParseJson(Encoding.UTF8.GetBytes(rawJson));
            if (parsed is not Dictionary<string, object?> msg)
                return RpcError(null, -32700, "Request must be a JSON object.");

            var method = msg.TryGetValue("method", out var mv) ? mv as string ?? "" : "";
            var idRaw = msg.GetValueOrDefault("id");
            var idStr = idRaw != null ? JsonEscape(idRaw.ToString()!) : "null";

            return method switch
            {
                // ── Lifecycle ──
                "ping" => RpcResult(idStr, "{}"),
                "initialize" => RpcResult(idStr, "{\"protocolVersion\":\"" + McpVersion + "\",\"capabilities\":{\"tools\":{},\"resources\":{},\"logging\":{}}}"),
                "notifications/initialized" => "", // notification, no response

                // ── Tools ──
                "tools/list" => HandleToolsList(idStr),
                "tools/call" => HandleToolsCall(idStr, msg),

                // ── Resources ──
                "resources/list" => HandleResourcesList(idStr),
                "resources/read" => HandleResourcesRead(idStr, msg),

                // ── Logging ──
                "logging/setLevel" => HandleLoggingSetLevel(idStr, msg),

                _ => RpcError(idStr, -32601, $"Unknown method: {method}"),
            };
        }
        catch (Exception ex) { return RpcError(null, -32700, $"Parse error: {ex.Message}"); }
    }

    private static string HandleToolsList(string id)
    {
        List<McpTool> snapshot;
        lock (Tools) { snapshot = [.. Tools.Values]; }

        var sb = Borrow();
        sb.Append("{\"tools\":[");
        var first = true;
        foreach (var t in snapshot)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"name\":");
            AppendEscapeTo(sb, t.Name);
            sb.Append(",\"description\":");
            AppendEscapeTo(sb, t.Description);
            sb.Append(",\"inputSchema\":").Append(t.InputSchema ?? "{}").Append('}');
        }
        sb.Append("]}");
        return RpcResultTo(id, sb, Borrow(ref _tsBuf2));
    }

    private static string HandleToolsCall(string id, Dictionary<string, object?> msg)
    {
        var p = msg.TryGetValue("params", out var pv) ? pv as Dictionary<string, object?> : null;
        var name = p != null && p.TryGetValue("name", out var nv) ? nv as string : null;
        if (name == null) return RpcError(id, -32602, "Missing 'params.name'.");

        // Resolve arguments early (needed by both middleware and tool handler)
        var argsDict = p?.TryGetValue("arguments", out var av) == true ? av as Dictionary<string, object?> : null;
        var args = new List<object?>();

        // ── Middleware pipeline (runs BEFORE tool lookup) ──
        Logging.Log.Debug("MCP", $"HandleToolsCall pipe={ToolPipeline?.Count ?? 0} tool={name}");
        if (ToolPipeline is { Count: > 0 })
        {
            // Populate middlewareArgs from raw JSON (tool lookup is after middleware)
            var middlewareArgs = new List<object?>();
            if (argsDict != null)
                foreach (var kv in argsDict)
                    middlewareArgs.Add(kv.Value);
            var ctx = new McpContext { ToolName = name, Args = middlewareArgs };
            ToolPipeline.Execute(ctx);
            //Log("debug", $"middleware: Execute done err={ctx.ErrorMessage} res={ctx.Result}");
            if (ctx.ErrorMessage != null)
                return RpcError(id, -32000, ctx.ErrorMessage);
            if (ctx.Result != null)
            {
                var msb = Borrow();
                msb.Append("{\"content\":[{\"type\":\"text\",\"text\":");
                AppendEscapeTo(msb, ctx.Result.ToString() ?? "");
                msb.Append("}]}");
                return RpcResultTo(id, msb, Borrow(ref _tsBuf2));
            }
        }

        McpTool? tool;
        var itp = Current;
        lock (Tools) { Tools.TryGetValue(name, out tool); if (itp == null) _toolInterpreter?.TryGetTarget(out itp); }
        if (tool == null)
        {
            Log("error", $"Unknown tool: {name}");
            return RpcError(id, -32602, $"Unknown tool: {name}");
        }

        if (argsDict != null)
        {
            foreach (var pName in tool.Handler.Declaration.Params.Select(valueTuple => valueTuple.Name))
                if (argsDict.TryGetValue(pName, out var val))
                    args.Add(val);
        }

        if (_sandbox != null)
        {
            foreach (var arg in args)
            {
                if (arg is not string path || !LooksLikeFilePath(path) || _sandbox.IsPathAllowed(path))
                    continue;
                Log("warning", $"Access denied: '{path}'");
                return RpcError(id, -32000, $"Access denied: '{path}'.");
            }
        }

        try
        {
            Log("info", $"tool:{name} args:{args.Count}");
            if (itp == null) return RpcError(id, -32000, "No interpreter context.");

            var result = itp.InvokeScriptFunction(tool.Handler, [.. args], 0, 0);
            var text = result?.ToString() ?? "";

            // Build: {"content":[{"type":"text","text":"..."}]}
            var sb = Borrow();
            sb.Append("{\"content\":[{\"type\":\"text\",\"text\":");
            AppendEscapeTo(sb, text);
            sb.Append("}]}");
            return RpcResultTo(id, sb, Borrow(ref _tsBuf2));
        }
        catch (Exception ex) { Log("error", $"tool:{name} failed: {ex.Message}"); return RpcError(id, -32000, $"Tool error: {ex.Message}"); }
    }

    // ── Resources ──

    private static string HandleResourcesList(string id)
    {
        List<McpResource> snapshot;
        lock (Resources) { snapshot = [.. Resources.Values]; }
        if (snapshot.Count == 0)
            return RpcResult(id, "{\"resources\":[]}");

        var sb = Borrow();
        sb.Append("{\"resources\":[");
        var first = true;
        foreach (var r in snapshot)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append("{\"uri\":");
            AppendEscapeTo(sb, r.Uri);
            sb.Append(",\"name\":");
            AppendEscapeTo(sb, r.Name);
            sb.Append(",\"mimeType\":");
            AppendEscapeTo(sb, r.MimeType);
            sb.Append('}');
        }
        sb.Append("]}");
        return RpcResultTo(id, sb, Borrow(ref _tsBuf2));
    }

    private static string HandleResourcesRead(string id, Dictionary<string, object?> msg)
    {
        var p = msg.TryGetValue("params", out var pv) ? pv as Dictionary<string, object?> : null;
        var uri = p?.TryGetValue("uri", out var uv) == true ? uv as string : null;
        if (uri == null) return RpcError(id, -32602, "Missing 'params.uri'.");

        McpResource? resource;
        var ritp = Current;
        lock (Resources) { Resources.TryGetValue(uri, out resource); if (ritp == null) _toolInterpreter?.TryGetTarget(out ritp); }
        if (resource == null)
        {
            Log("error", $"Resource not found: {uri}");
            return RpcError(id, -32602, $"Resource not found: {uri}");
        }

        try
        {
            if (ritp == null) return RpcError(id, -32000, "No interpreter context.");
            var result = ritp.InvokeScriptFunction(resource.Handler, [], 0, 0);
            var text = result?.ToString() ?? "";

            var sb = Borrow();
            sb.Append("{\"contents\":[{\"uri\":");
            AppendEscapeTo(sb, uri);
            sb.Append(",\"mimeType\":");
            AppendEscapeTo(sb, resource.MimeType);
            sb.Append(",\"text\":");
            AppendEscapeTo(sb, text);
            sb.Append("}]}");
            return RpcResultTo(id, sb, Borrow(ref _tsBuf2));
        }
        catch (Exception ex) { return RpcError(id, -32000, $"Resource error: {ex.Message}"); }
    }

    // ── Logging ──

    private static string HandleLoggingSetLevel(string id, Dictionary<string, object?> msg)
    {
        var p = msg.TryGetValue("params", out var pv) ? pv as Dictionary<string, object?> : null;
        var level = p?.TryGetValue("level", out var lv) == true ? lv as string : null;
        if (level == null || Array.IndexOf(["debug", "info", "notice", "warning", "error"], level) < 0)
            return RpcError(id, -32602, "Invalid logging level.");

        if (level != _logLevel)
            Log("info", "Log level set to " + level);
        _logLevel = level;
        return RpcResult(id, "{}");
    }

    private static bool LooksLikeFilePath(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s is [_, ':', ..]) return true;
        if (s[0] == '/' || s[0] == '\\') return true;
        return s.IndexOfAny(PathSepChars) >= 0 || s.Contains('.');
    }
    private static readonly char[] PathSepChars = ['\\', '/'];

    // ── Pooled JSON-RPC formatting ──

    private static string RpcResult(string id, string resultJson)
    {
        var sb = Borrow();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id)
          .Append(",\"result\":").Append(resultJson).Append('}');
        return sb.ToString();
    }

    private static string RpcResultTo(string id, StringBuilder resultSb, StringBuilder sb)
    {
        sb.Clear();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id)
          .Append(",\"result\":").Append(resultSb).Append('}');
        return sb.ToString();
    }

    private static string RpcError(string? id, int code, string msg)
    {
        var sb = Borrow();
        sb.Append("{\"jsonrpc\":\"2.0\",\"id\":").Append(id ?? "null")
          .Append(",\"error\":{\"code\":").Append(code).Append(",\"message\":");
        AppendEscapeTo(sb, msg);
        sb.Append("}}");
        return sb.ToString();
    }

    // ── Span-based JSON escaping ──

    private static void AppendEscapeTo(StringBuilder sb, string s)
    {
        sb.Append('"');
        // Walk the string in spans, appending escaped chunks
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c > 0x7E || EscapeTable[c] is not { } esc)
                continue;
            if (i > start) sb.Append(s.AsSpan(start, i - start));
            sb.Append(esc);
            start = i + 1;
        }
        if (start < s.Length) sb.Append(s.AsSpan(start));
        sb.Append('"');
    }

    private static string JsonEscape(string s)
    {
        var sb = Borrow();
        AppendEscapeTo(sb, s);
        return sb.ToString();
    }

    /// <summary>ASCII escape table: 0..0x7E, null = pass-through, string = replacement.</summary>
    private static readonly string?[] EscapeTable = BuildEscapeTable();
    private static string?[] BuildEscapeTable()
    {
        var t = new string?[0x7F];
        t['"'] = @"\""";
        t['\\'] = @"\\";
        t['\n'] = @"\n";
        t['\r'] = @"\r";
        t['\t'] = @"\t";
        return t;
    }
}
