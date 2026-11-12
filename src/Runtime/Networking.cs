namespace ScriptLangCore.Networking;

internal enum ClientStatus
{
    Pending,        // connecté TCP, handshake WS fait, en attente de l'ACK clientId
    Acknowledged,   // a renvoyé son clientId → enregistré comme actif
    Disconnected
}

internal readonly record struct ClientEntry(string ClientId, ClientStatus Status);

internal sealed class NativeWebSocketServer
{
    private TcpListener? _listener;
    private volatile bool _running;
    private Thread? _acceptThread;
    internal string? RootDir;

    /// <summary>The most request-head LINES a probe will read before it stops (a peer that never ends its head must not
    /// hold a connection open). 100 is far above any real request: a WebSocket handshake sends about ten.</summary>
    private const int MaxHttpHeadLines = 100;

    /// <summary>The most request-head CHARACTERS a probe will read, for the same reason.</summary>
    private const int MaxHttpHeadBytes = 16 * 1024;

    /// <summary>The owning interpreter's `tcp.Use` pipeline, handed over by `TcpServer.Start` before the accept
    /// loop runs. Every accepted connection goes through it first, so the script's middleware sees each one and
    /// may refuse it: `ctx.Close()` closes the socket AND stops the rest of the chain.</summary>
    internal MiddlewarePipeline<TcpContext>? TcpMiddleware { get; set; }

    // clientId → stream
    private readonly ConcurrentDictionary<string, NetworkStream> _streams = new();
    private readonly ConcurrentDictionary<string, Lock> _clientLocks = new();

    // clientId → (clientId, status), liste officielle
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();

    // Events internes consommés par ScriptServerApi
    public event Action<string>? OnClientPending;       // clientId généré, en attente ACK
    public event Action<string>? OnClientAcknowledged;  // client a confirmé son id
    public event Action<string>? OnClientDisconnected;
    public event Action<string, string>? OnMessage;
    public event Action<string?, string>? OnError;

    public bool IsRunning => _running;
    public int ClientCount => _clients.Count(c => c.Value.Status == ClientStatus.Acknowledged);

    public bool Start(bool external, int port)
    {
        var ip = external ? IPAddress.Any : IPAddress.Loopback;
        _listener = new TcpListener(ip, port);
        _listener.Start();
        _running = true;

        if (_acceptThread is not null)
            throw new ScriptException("Server is already running.");

        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "WSServer-Accept"
        };
        _acceptThread.Start();

        return true;
    }

    public void Stop()
    {
        _running = false;
        _listener?.Stop();

        // Forcefully close all connected clients
        foreach (var stream in _streams.Values)
        {
            try { stream.Close(); }
            catch
            {
                // ignored
            }
        }
        _streams.Clear();

        _acceptThread = null;
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var tcp = _listener!.AcceptTcpClient();

                // The script's connection middleware runs FIRST, on the accept thread, before the HTTP-vs-WebSocket
                // probe reads anything: a middleware that refuses by address must not have its bytes consumed.
                // `Close()` sets the flag through the context, and the chain stops at the next middleware AND the
                // connection is not handled at all, which is what "closes the connection immediately" means.
                if (TcpMiddleware is { Count: > 0 } connections)
                {
                    var closed = false;
                    var tcpCtx = new TcpContext(() => { closed = true; tcp.Close(); })
                    {
                        RemoteAddress = tcp.Client.RemoteEndPoint?.ToString() ?? ""
                    };
                    connections.Execute(tcpCtx, () => closed);
                    if (closed) continue;
                }

                // Protocol probe: ONE door for two roles. A WebSocket client opens with `GET /` exactly as a browser
                // asking for a file does, so the line alone cannot tell them apart and the old probe served anything
                // starting with `GET /` as a FILE, which answered a handshake with the 404 page (measured: no session,
                // no id, no message, while the client reported itself connected). The request HEAD is read here, byte
                // by byte through ReadLine so nothing beyond it is consumed, and the UPGRADE HEADER decides.
                if (RootDir != null)
                {
                    var stream = tcp.GetStream();
                    string? head;
                    string firstLine;
                    try { head = ReadHttpHead(stream, out firstLine); }
                    catch { tcp.Close(); continue; }

                    if (head == null) { tcp.Close(); continue; }

                    // Sec-WebSocket-Key is what every WebSocket client sends and no browser file request does (a
                    // browser's `Upgrade-Insecure-Requests` is not an upgrade), so it is the discriminator.
                    if (head.Contains("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                    {
                        // The head is ALREADY read, so it is handed to the handshake instead of being read twice.
                        var prelude = head;
                        new Thread(() => HandleClient(tcp, prelude)) { IsBackground = true, Name = "WSServer-Client" }.Start();
                        continue;
                    }
                    if (firstLine.StartsWith("GET /", StringComparison.Ordinal))
                    {
                        ServeHttpFile(tcp, firstLine, stream, RootDir);
                        continue;
                    }
                    // A first line that LOOKS like HTTP but is not a GET is a method this server does not serve, so
                    // it gets 405 with the Allow header, which is what endpoint routing does.
                    if (firstLine.Contains(" HTTP/", StringComparison.Ordinal))
                    {
                        RefuseMethod(stream, firstLine);
                        continue;
                    }
                    // Not HTTP at all: the WebSocket path decides, and refuses a handshake it cannot complete.
                    var other = head;
                    new Thread(() => HandleClient(tcp, other)) { IsBackground = true, Name = "WSServer-Client" }.Start();
                    continue;
                }
                else
                {
                    // No RootDir set, pure WebSocket server
                }
                new Thread(() => HandleClient(tcp)) { IsBackground = true, Name = "WSServer-Client" }.Start();
            }
            catch { if (!_running) break; }
        }
    }

    private void HandleClient(System.Net.Sockets.TcpClient tcp, string? head = null)
    {
        var stream = tcp.GetStream();

        // Handshake WebSocket
        if (!DoHandshake(stream, head))
        {
            tcp.Close();
            return;
        }

        // Génère un clientId unique et le distribue
        var clientId = Guid.CreateVersion7().ToString("N")[..12];
        _streams[clientId] = stream;
        _clientLocks[clientId] = new Lock();
        _clients[clientId] = new ClientEntry(clientId, ClientStatus.Pending);

        // Envoie le clientId au client pour qu'il puisse faire l'ACK
        SendFrame(stream, $"__id:{clientId}");
        OnClientPending?.Invoke(clientId);

        try
        {
            while (tcp.Connected && _running)
            {
                var msg = ReadFrame(stream);
                if (msg == null) break;

                // ACK du client : il renvoie son clientId
                if (msg == $"__ack:{clientId}")
                {
                    _clients[clientId] = _clients[clientId] with { Status = ClientStatus.Acknowledged };
                    OnClientAcknowledged?.Invoke(clientId);
                    continue;
                }

                // Message normal, on le dispatch uniquement si client acknowledged
                if (_clients.TryGetValue(clientId, out var entry) && entry.Status == ClientStatus.Acknowledged)
                    OnMessage?.Invoke(clientId, msg);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke(clientId, ex.Message);
        }
        finally
        {
            _streams.TryRemove(clientId, out _);
            _clientLocks.TryRemove(clientId, out _);
            OnClientDisconnected?.Invoke(clientId);
            tcp.Close();
        }
    }

    public void Send(string clientId, string data)
    {
        var dataSpan = data.AsSpan();

        if (!_streams.TryGetValue(clientId, out var stream)) return;
        try { lock (_clientLocks.GetOrAdd(clientId, _ => new Lock())) SendFrame(stream, dataSpan); }
        catch { /* client déjà parti */ }
    }

    public void Broadcast(string data, string? excludeId = null)
    {
        foreach (var (id, entry) in _clients)
        {
            if (entry.Status != ClientStatus.Acknowledged) continue;
            if (id == excludeId) continue;
            Send(id, data);
        }
    }

    public ICollection<ClientEntry> GetClients() => _clients.Values;

    public ClientStatus? GetClientStatus(string clientId) =>
        _clients.TryGetValue(clientId, out var e) ? e.Status : null;

    public bool Remove(string clientId) => _clients.Count > 0 && _clients.TryRemove(clientId, out _);

    public void DisconnectClient(string clientId)
    {
        if (!_streams.TryRemove(clientId, out var stream)) return;
        try { stream.Close(); }
        catch
        {
            // ignored
        }

        //if (_clients.TryGetValue(clientId, out var client)) { }
        OnClientDisconnected?.Invoke(clientId);
    }

    /// <summary>
    /// Read an HTTP request HEAD (its request line plus every header, up to and including the blank line) WITHOUT
    /// reading past it: <see cref="ReadLine"/> takes one byte at a time, so a client that sends its first frame right
    /// after its handshake is not truncated. That exactness is what lets the head be handed to the WebSocket handshake
    /// afterwards, and it is why one probe can serve a file request and a WebSocket upgrade on the same port.
    /// </summary>
    /// <returns>The head, CRLF-terminated, or null when the peer sent nothing.</returns>
    private string? ReadHttpHead(NetworkStream stream, out string firstLine)
    {
        firstLine = "";
        var head = new StringBuilder();
        var lines = 0;
        while (lines <= MaxHttpHeadLines)
        {
            var line = ReadLine(stream);
            if (line == null) return head.Length == 0 ? null : head.ToString();
            if (line.Length == 0) break;                    // blank line: end of the head
            if (lines == 0) firstLine = line;
            head.Append(line).Append("\r\n");
            lines++;
            if (head.Length > MaxHttpHeadBytes) break;      // a peer that never ends its head is not served
        }
        head.Append("\r\n");
        return head.ToString();
    }

    private bool DoHandshake(NetworkStream stream, string? head = null)
    {
        try
        {
            string raw;
            if (!string.IsNullOrEmpty(head))
            {
                // The accept loop already read the head to decide what this connection is, so it is USED here rather
                // than read again (a second read would block for ever: the client is waiting for the 101).
                raw = head;
            }
            else
            {
                var sb = new StringBuilder();
                // Lecture ligne par ligne sans StreamReader (safe AOT)
                while (true)
                {
                    var line = ReadLine(stream);
                    if (string.IsNullOrEmpty(line)) break;
                    sb.AppendLine(line);
                }
                raw = sb.ToString();
            }

            var key = raw.Split('\n')
                         .Select(l => l.Trim())
                         .FirstOrDefault(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                         ?.Split(':', 2)[1].Trim();

            if (key == null) return false;

            // RFC 6455 section 4.2.2 defines Sec-WebSocket-Accept as base64(SHA-1(key + the GUID)), so SHA-1 here is
            // the PROTOCOL's choice and not a security decision: the hash is a handshake proof, never a MAC or a
            // signature, and a client that sent anything else would be refused by every WebSocket server. CA5350 is
            // therefore right in general and wrong at this call, which is why it is suspended here with the reason
            // rather than dropped from the curated set (the rest of the engine's crypto is AES-GCM, ML-KEM, ML-DSA).
#pragma warning disable CA5350 // the WebSocket handshake's hash, fixed by RFC 6455
            var accept = Convert.ToBase64String(
                SHA1.HashData(Encoding.UTF8.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"))
            );
#pragma warning restore CA5350

            var response = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n"
            );

            stream.Write(response, 0, response.Length);
            return true;
        }
        catch { return false; }
    }

    private string? ReadLine(NetworkStream stream)
    {
        // Rent a large enough buffer to minimize physical reads
        var rentedArray = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            var bytesRead = 0;
            while (true)
            {
                // Synchronous read into the buffer
                var r = stream.Read(rentedArray, bytesRead, 1); // Reading 1 for logic, though 
                                                                // larger chunks are better if protocol allows.
                if (r == 0) return bytesRead == 0
                        ? null
                        : Parse(rentedArray, bytesRead);

                if (rentedArray[bytesRead] == (byte)'\n')
                    return Parse(rentedArray, bytesRead);

                bytesRead++;

                // Safety check for buffer overflow
                if (bytesRead >= rentedArray.Length)
                    throw new ScriptException("Line too long");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedArray);
        }
    }

    private string Parse(byte[] buffer, int length)
    {
        var sequence = new ReadOnlySequence<byte>(buffer, 0, length);
        var reader = new SequenceReader<byte>(sequence);

        // If the reader hasn't moved, 'UnreadSequence' represents the whole data
        var captured = reader.UnreadSequence;

        // Convert to string
        return Encoding.UTF8.GetString(captured).TrimEnd('\r');
    }

    private string? ReadFrame(NetworkStream stream)
    {
        var header = new byte[2];
        if (!ReadExact(stream, header)) return null;

        var masked = (header[1] & 0x80) != 0;
        var length = header[1] & 0x7F;

        switch (length)
        {
            case 126:
                {
                    var ext = new byte[2];
                    ReadExact(stream, ext);
                    length = (ext[0] << 8) | ext[1];
                    break;
                }
            case 127:
                {
                    var ext = new byte[8];
                    ReadExact(stream, ext);
                    length = (int)BitConverter.ToUInt64([.. ext.Reverse()], 0);
                    break;
                }
        }

        byte[]? mask = null;
        if (masked) { mask = new byte[4]; ReadExact(stream, mask); }

        var payload = new byte[length];
        if (!ReadExact(stream, payload)) return null;

        if (!masked)
            return Encoding.UTF8.GetString(payload);

        for (var i = 0; i < payload.Length; i++)
            payload[i] ^= mask![i % 4];

        return Encoding.UTF8.GetString(payload);
    }

    private static void SendFrame(NetworkStream stream, ReadOnlySpan<char> data)
    {
        var byteCount = Encoding.UTF8.GetByteCount(data);
        var payload = ArrayPool<byte>.Shared.Rent(byteCount);

        try
        {
            var written = Encoding.UTF8.GetBytes(data, payload);
            // utilise payload[0..written]
            using var ms = new MemoryStream();

            ms.WriteByte(0x81);

            switch (written)
            {
                case < 126:
                    ms.WriteByte((byte)written);
                    break;
                case < 65536:
                    ms.WriteByte(126);
                    ms.WriteByte((byte)(written >> 8));
                    ms.WriteByte((byte)written);
                    break;
                default:
                    {
                        ms.WriteByte(127);
                        var lenBytes = BitConverter.GetBytes((ulong)written);
                        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                        ms.Write(lenBytes, 0, 8);
                        break;
                    }
            }

            ms.Write(payload, 0, written);
            var frame = ms.ToArray();
            stream.Write(frame, 0, frame.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(payload);
        }
    }

    internal static bool ReadExact(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var r = stream.Read(buffer, offset, buffer.Length - offset);
            if (r == 0) return false;
            offset += r;
        }
        return true;
    }

    //private static string old_GetContentType(string ext) => ext.ToLowerInvariant() switch
    //{
    //    ".html" or ".htm" => "text/html; charset=utf-8",
    //    ".css" => "text/css; charset=utf-8",
    //    ".js" => "application/javascript",
    //    ".json" => "application/json",
    //    ".png" => "image/png",
    //    ".jpg" or ".jpeg" => "image/jpeg",
    //    ".gif" => "image/gif",
    //    ".svg" => "image/svg+xml",
    //    ".ico" => "image/x-icon",
    //    ".pdf" => "application/pdf",
    //    ".zip" => "application/zip",
    //    ".txt" or ".md" => "text/plain; charset=utf-8",
    //    ".wasm" => "application/wasm",
    //    _ => "application/octet-stream"
    //};

    private static readonly FrozenDictionary<string, string> ContentTypes = new Dictionary<string, string>
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".js"] = "application/javascript",
        [".json"] = "application/json",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/plain; charset=utf-8",
        [".wasm"] = "application/wasm",
        // An Avro Object Container File, which is what `http.Avro` answers and what `Avro.WriteContainer` writes, so a
        // route that serves one with `http.File` and a client that asks for it agree on the type without either saying
        // it. The SAME name `AvroConvert`'s HTTP layer uses.
        [".avro"] = "application/avro"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static string GetContentType(string ext) =>
        ContentTypes.TryGetValue(ext, out var contentType) ? contentType : "application/octet-stream";

    /// <summary>
    /// The content type a PATH would be served as, from the ONE table this server already uses, so a route's `http.File`
    /// answer and a file this server serves itself cannot disagree about what a `.pdf` is.
    /// </summary>
    internal static string ContentTypeForPath(string path) => GetContentType(Path.GetExtension(path));

    private static void ServeHttpFile(System.Net.Sockets.TcpClient tcp, string firstLine, NetworkStream stream, string rootDir)
    {
        try
        {
            // Parse GET /path HTTP/1.1
            var parts = firstLine.Split(' ');
            if (parts.Length < 2 || parts[0] != "GET")
            {
                RefuseMethod(stream, firstLine);
                return;
            }

            var rawPath = parts[1];
            var qmark = rawPath.IndexOf('?');
            var cleanPath = qmark >= 0 ? rawPath[..qmark] : rawPath;
            cleanPath = Uri.UnescapeDataString(cleanPath);

            // Security: prevent path traversal
            var normRoot = Path.GetFullPath(rootDir).TrimEnd('\\', '/') + '\\';
            var fullPath = Path.GetFullPath(Path.Combine(normRoot, cleanPath.TrimStart('/')));
            if (!fullPath.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
            {
                Logging.Log.Info("Server", $"HTTP GET {cleanPath} answered 403 with the page for it (the path leaves the served root).");
                AnswerWithPage(stream, 403, "Forbidden");
                return;
            }

            // Directory → try index.html
            if (Directory.Exists(fullPath))
            {
                var indexPath = Path.Combine(fullPath, "index.html");
                fullPath = File.Exists(indexPath) ? indexPath : null;
            }

            if (fullPath == null || !File.Exists(fullPath))
            {
                Logging.Log.Info("Server", $"HTTP GET {cleanPath} answered 404 with the page for it (no such file under the served root).");
                AnswerWithPage(stream, 404, "Not Found");
                return;
            }

            var fileBytes = File.ReadAllBytes(fullPath);
            var ext = Path.GetExtension(fullPath);
            SendHttpResponse(stream, 200, "OK", GetContentType(ext), fileBytes);
        }
        catch
        {
            try { tcp.Dispose(); } catch { }
        }
    }

    /// <summary>One answer for a method this server does not serve. It is a status the ENGINE chooses, so it carries NO
    /// body and the reason goes to the log: the same rule every framework-set status here follows, and ASP.NET Core
    /// answers a method no endpoint routed with 405, no body, and an Allow header.</summary>
    private static void RefuseMethod(NetworkStream stream, string firstLine)
    {
        var parts = firstLine.Split(' ');
        Logging.Log.Info("Server", $"HTTP {parts[0]} {parts.ElementAtOrDefault(1)} answered 405 with the page for it (this server serves GET only).");
        AnswerWithPage(stream, 405, "Method Not Allowed", "Allow: GET\r\n");
    }

    /// <summary>Writes one answer. A null <paramref name="contentType"/> means NO Content-Type header, which is what an
    /// empty body gets: the status is the message for a status the engine chose, exactly as ASP.NET Core answers one,
    /// and a type header on a body of zero bytes describes nothing.</summary>
    /// <summary>Answers a framework-set status with the PAGE for it (the session's registered one, or the engine's own
    /// default), stating the content type and the length: the same shape the MCP server's writer uses, so the two HTTP
    /// writers cannot answer one status differently.</summary>
    private static void AnswerWithPage(NetworkStream stream, int status, string reason, string? extraHeaders = null)
    {
        var (body, contentType) = HttpErrorPages.Resolve(status, reason);
        SendHttpResponse(stream, status, reason, contentType, body, extraHeaders);
    }
    private static void SendHttpResponse(NetworkStream stream, int status, string reason, string? contentType, ReadOnlySpan<byte> body, string? extraHeaders = null)
    {
        var typeHeader = string.IsNullOrEmpty(contentType) ? "" : $"Content-Type: {contentType}\r\n";
        var header = Encoding.UTF8.GetBytes($"HTTP/1.1 {status} {reason}\r\n{typeHeader}{extraHeaders}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        stream.Write(header);
        if (body.Length > 0)
            stream.Write(body);
        stream.Flush();
    }
}

internal sealed class NativeWebSocketClient
{
    private System.Net.Sockets.TcpClient? _tcp;
    private NetworkStream? _stream;
    private volatile bool _connected;
    private readonly Lock _streamLock = new();

    private string? _assignedId; // clientId assigné par le serveur

    public event Action<string>? OnMessage;
    public event Action? OnDisconnect;
    public event Action<string>? OnError;
    public event Action<string>? OnIdAssigned; // déclenché quand le serveur envoie __id:xxx

    public bool IsConnected => _connected;
    public string? AssignedId => _assignedId;

    public bool Connect(string? host, int port) => Connect(host, port, 5000);

    public bool Connect(string? host, int port, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ScriptException(nameof(host));

        _tcp = new System.Net.Sockets.TcpClient();
        var ar = _tcp.BeginConnect(host, port, null, null);
        if (!WaitWithPump(ar.AsyncWaitHandle, timeoutMs)) { _tcp.Close(); return false; }
        _tcp.EndConnect(ar);

        _stream = _tcp.GetStream();

        if (!DoHandshake(host, port)) { _tcp.Close(); return false; }

        _connected = true;
        new Thread(ReceiveLoop) { IsBackground = true, Name = "WSClient-Recv" }.Start();
        return true;
    }

    /// <summary>How long one connect slice waits before the caller's windows get a turn.</summary>
    private const int ConnectPumpSliceMs = 50;

    /// <summary>
    /// Wait for a connect to complete, PUMPING this thread's windows between slices. A connect to an address that
    /// never answers spends the whole timeout inside one native call, so no statement boundary is reached and a window
    /// this run owns used to freeze for the full wait (measured: zero timer ticks in 1013 ms with a 1000 ms timeout,
    /// against 20 to 23 ticks for the same duration in an <c>await</c> or a <c>sleep</c>). This is the same rule
    /// <c>sleep</c> follows for its chunks, and <see cref="NativeApp.PumpForWorkerRun"/> does nothing at all when the
    /// calling thread owns no window, so a headless client pays one list check per slice.
    /// </summary>
    private static bool WaitWithPump(WaitHandle handle, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var left = deadline - Environment.TickCount64;
            if (left <= 0) return handle.WaitOne(0);
            if (handle.WaitOne((int)Math.Min(left, ConnectPumpSliceMs))) return true;
            ScriptLangCore.Interpreter.NativeApp.PumpForWorkerRun();
        }
    }

    public void Disconnect()
    {
        _connected = false;
        _tcp?.Close();
    }

    public void Send(string data)
    {
        if (_stream == null || !_connected) return;
        try { lock (_streamLock) SendFrame(_stream, data); }
        catch (Exception ex) { OnError?.Invoke(ex.Message); }
    }

    private void ReceiveLoop()
    {
        try
        {
            while (_connected && _tcp!.Connected)
            {
                var msg = ReadFrame(_stream!);
                if (msg == null) break;

                // Le serveur nous envoie notre clientId
                if (msg.StartsWith("__id:"))
                {
                    _assignedId = msg[5..];
                    OnIdAssigned?.Invoke(_assignedId);
                    // ACK immédiat vers le serveur
                    Send($"__ack:{_assignedId}");
                    continue;
                }

                OnMessage?.Invoke(msg);
            }
        }
        catch { /* connexion fermée */ }
        finally
        {
            _connected = false;
            OnDisconnect?.Invoke();
        }
    }

    private bool DoHandshake(string host, int port)
    {
        var key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var request = Encoding.UTF8.GetBytes(
            $"GET / HTTP/1.1\r\n" +
            $"Host: {host}:{port}\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            "Sec-WebSocket-Version: 13\r\n\r\n"
        );

        _stream!.Write(request, 0, request.Length);

        // Lecture de la réponse HTTP 101
        var sb = new StringBuilder();
        while (true)
        {
            var line = ReadLine(_stream);
            if (string.IsNullOrEmpty(line)) break;
            sb.AppendLine(line);
        }

        return sb.ToString().Contains("101");
    }

    private string? ReadLine(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        while (true)
        {
            var r = stream.Read(buf, 0, 1);
            if (r == 0) return null;
            var c = (char)buf[0];
            if (c == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(c);
        }
    }

    private string? ReadFrame(NetworkStream stream)
    {
        var header = new byte[2];
        if (!ReadExact(stream, header)) return null;

        var length = header[1] & 0x7F;

        if (length == 126)
        {
            var ext = new byte[2]; ReadExact(stream, ext);
            length = (ext[0] << 8) | ext[1];
        }
        else if (length == 127)
        {
            var ext = new byte[8]; ReadExact(stream, ext);
            length = (int)BitConverter.ToUInt64([.. ext.Reverse()], 0);
        }

        var payload = new byte[length];
        if (!ReadExact(stream, payload)) return null;
        return Encoding.UTF8.GetString(payload);
    }

    private void SendFrame(NetworkStream stream, string data)
    {
        var payload = Encoding.UTF8.GetBytes(data);
        var mask = new byte[4];
        Random.Shared.NextBytes(mask); // les frames client DOIVENT être masquées (RFC 6455)

        using var ms = new MemoryStream();
        ms.WriteByte(0x81);

        switch (payload.Length)
        {
            case < 126:
                ms.WriteByte((byte)(payload.Length | 0x80));
                break;

            case < 65536:
                ms.WriteByte(126 | 0x80);
                ms.WriteByte((byte)(payload.Length >> 8));
                ms.WriteByte((byte)payload.Length);
                break;
        }

        ms.Write(mask, 0, 4);

        var masked = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
            masked[i] = (byte)(payload[i] ^ mask[i % 4]);

        ms.Write(masked, 0, masked.Length);
        var frame = ms.ToArray();
        stream.Write(frame, 0, frame.Length);
    }

    bool ReadExact(NetworkStream stream, byte[] buffer) => NativeWebSocketServer.ReadExact(stream, buffer);
}

internal record TcpServer
{
    private NativeWebSocketServer? _server;
    private readonly Lazy<Interpreter> _interpreter;
    private string? _mode;
    private string? _rootDir;
    private Action<string>? _hPending, _hAcknowledged, _hDisconnected;
    private Action<string, string>? _hMessage;
    private Action<string?, string>? _hError;

    // Dispatch queue : le thread WS pousse, le thread script consomme via Listen()
    private readonly ConcurrentQueue<Action> _dispatchQueue;
    private readonly ManualResetEventSlim _signal;

    public TcpServer(Interpreter interpreter)
    {
        _dispatchQueue = new();
        _signal = new ManualResetEventSlim(false);
        _interpreter = new Lazy<Interpreter>(() => interpreter);
        Id = Guid.CreateVersion7().ToString("N")[..12];
    }

    public TcpServer Create(int port, string? mode = "local", string? name = null, string? rootDir = null)
    {
        if (_server != null)
            return this;

        Port = port;
        _mode = mode;
        _rootDir = rootDir;
        Name = name;

        _server = new NativeWebSocketServer();
        {
            _hPending = id => EnqueueAction(() => _onPending?.Invoke(id));
            _hAcknowledged = id => EnqueueAction(() => _onConnect?.Invoke(id));
            _hDisconnected = id => EnqueueAction(() => _onDisconnect?.Invoke(id));
            _hMessage = (id, msg) => EnqueueAction(() => _onMessage?.Invoke(id, msg));
            _hError = (id, err) => EnqueueAction(() => _onError?.Invoke(id, err));
            _server.OnClientPending += _hPending;
            _server.OnClientAcknowledged += _hAcknowledged;
            _server.OnClientDisconnected += _hDisconnected;
            _server.OnMessage += _hMessage;
            _server.OnError += _hError;
        }

        return this;
    }

    private void EnqueueAction(Action action)
    {
        _dispatchQueue.Enqueue(action);
        _signal.Set(); // Wake up the consumer thread
    }

    /// <summary>Drain the dispatch queue without blocking.</summary>
    public void Pump()
    {
        while (_dispatchQueue.TryDequeue(out var action))
            action();
    }

    /// <summary>server.start(8888) ou server.start(8888, "external")</summary>
    public bool Start()
    {
        try
        {
            if (_server != null && _rootDir != null)
            {
                if (!Directory.Exists(_rootDir))
                {
                    _onError?.Invoke("Server", $"HTTP root directory not found: {_rootDir}");
                    return false;
                }
                _server.RootDir = _rootDir;
            }
            // The connection middleware belongs to the interpreter that owns this server, which is the one a
            // script called Server.Start from: hand the pipeline over before the accept loop starts, and it stays
            // the SAME instance, so a `tcp.Use` registered after Start is seen by the next connection.
            if (_server is { } srv)
            {
                srv.TcpMiddleware = _interpreter.Value.TcpPipeline;
                srv.Start(_mode!.Equals("external", StringComparison.OrdinalIgnoreCase), Port);
            }
        }
        catch (SocketException ex)
        {
            _onError?.Invoke("Server", $"{ex.SocketErrorCode} (0x{ex.SocketErrorCode:X})");
        }
        catch (Exception ex)
        {
            _onError?.Invoke("Server", $"{ex.Message} (0x{ex.HResult:X})");
        }

        return IsRunning;
    }

    /// <summary>server.stop()</summary>
    public void Stop()
    {
        if (_server != null)
        {
            _server.OnClientPending -= _hPending;
            _server.OnClientAcknowledged -= _hAcknowledged;
            _server.OnClientDisconnected -= _hDisconnected;
            _server.OnMessage -= _hMessage;
            _server.OnError -= _hError;
            _server.Stop();
        }
    }

    // â”€â”€ Callbacks enregistrables depuis le script â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    private Action<string>? _onPending;
    private Action<string>? _onConnect;
    private Action<string>? _onDisconnect;
    private Action<string, string>? _onMessage;
    private Action<string?, string>? _onError;

    /// <summary>server.onPending(fn(clientId)): client connecté, en attente ACK</summary>
    public void OnPending(ScriptFunction fn)
        => _onPending = id => _interpreter.Value.InvokeScriptFunction(fn, [id], 0, 0);

    /// <summary>server.onConnect(fn(clientId)): client acknowledged</summary>
    public void OnConnect(ScriptFunction fn)
        => _onConnect = id => _interpreter.Value.InvokeScriptFunction(fn, [id], 0, 0);

    /// <summary>server.onDisconnect(fn(clientId))</summary>
    public void OnDisconnect(ScriptFunction fn)
        => _onDisconnect = id => _interpreter.Value.InvokeScriptFunction(fn, [id], 0, 0);

    /// <summary>server.onMessage(fn(clientId, msg))</summary>
    public void OnMessage(ScriptFunction fn)
        => _onMessage = (id, m) => _interpreter.Value.InvokeScriptFunction(fn, [id, m], 0, 0);

    /// <summary>server.onError(fn(clientId, err))</summary>
    public void OnError(ScriptFunction fn)
        => _onError = (id, e) => _interpreter.Value.InvokeScriptFunction(fn, [id, e], 0, 0);

    /// <summary>server.send(clientId, data)</summary>
    public void Send(string clientId, string data)
        => _server?.Send(clientId, data);

    public void Remove(string clientId)
        => _server?.Remove(clientId);

    /// <summary>server.broadcast(data)</summary>
    public void Broadcast(string data)
        => _server?.Broadcast(data);

    /// <summary>server.broadcast(data, excludeId)</summary>
    public void Broadcast(string data, string excludeId)
        => _server?.Broadcast(data, excludeId);

    public IScriptDictionary Clients()
    {
        var keyType = new TypeInfo(TokenType.StringType, true, 0, 0, null);
        var valType = new TypeInfo(TokenType.Object, true, 0, 0, null);
        var dict = new ScriptUnboxedDictionary<string, ClientStatus>(keyType, valType);

        var clients = _server?.GetClients();
        if (clients == null)
            return dict;

        foreach (var client in clients)
            dict.AddRaw(client.ClientId, client.Status);

        return dict;
    }

    /// <summary>server.clientCount() nombre de clients acknowledged</summary>
    public int ClientCount()
        => _server?.ClientCount ?? 0;

    /// <summary>server.clientStatus(clientId) → "Pending" | "Acknowledged" | "Disconnected" | null</summary>
    public string? ClientStatus(string clientId)
        => _server?.GetClientStatus(clientId)?.ToString();

    public void ClientKick(string clientId)
        => _server?.DisconnectClient(clientId);

    public string Id { get; }

    /// <summary>server.name</summary>
    public string? Name { get; private set; }

    /// <summary>server.isRunning()</summary>
    public bool IsRunning
        => _server?.IsRunning ?? false;

    /// <summary>server.port</summary>
    public int Port { get; private set; }

    /// <summary>
    /// server.listen(): boucle bloquante côté script.
    /// Dispatch les callbacks sur le thread script.
    /// </summary>
    public void Listen()
    {
        while (_server?.IsRunning == true)
        {
            // Wait for 100ms. If EnqueueAction calls .Set(), this returns TRUE immediately.
            if (!_signal.Wait(100))
            {
                // A 100 ms slice with nothing to dispatch is time the SCRIPT spends inside one native call, so no
                // statement boundary is reached and a window this run owns would freeze for as long as the loop runs
                // (measured: zero timer ticks in 1295 ms of a blocking wait). `sleep` already pumps its slices the
                // same way, and this does nothing at all when the thread owns no window.
                ScriptLangCore.Interpreter.NativeApp.PumpForWorkerRun();
                continue;
            }

            // We only reset if the signal was actually caught
            _signal.Reset();

            // Drain the queue
            while (_dispatchQueue.TryDequeue(out var action))
                action();
            // If it timed out, the loop just restarts and checks _server.IsRunning
        }

        // Process remaining events after server stops (e.g., client disconnects)
        while (_dispatchQueue.TryDequeue(out var action))
            action();
    }
}

internal record TcpClient : IDisposable
{
    private NativeWebSocketClient? _client;
    private readonly ConcurrentQueue<Action> _dispatchQueue;
    private readonly ManualResetEventSlim _signal;
    private readonly Lazy<Interpreter> _interpreter;

    private string? _host;
    private int _port;
    private int _timeoutMs;
    private Action<string>? _hIdAssigned, _hMessage;
    private Action? _hDisconnect;
    private Action<string>? _hError;

    public TcpClient(Interpreter interpreter)
    {
        _dispatchQueue = new ConcurrentQueue<Action>();
        _signal = new ManualResetEventSlim(false);
        _interpreter = new Lazy<Interpreter>(() => interpreter);
    }

    public TcpClient Create(string? host, int port, int timeoutMs = 15_000)
    {
        if (_client != null)
            return this;

        _client = new NativeWebSocketClient();

        _hIdAssigned = id => EnqueueAction(() => _onIdAssigned?.Invoke(id));
        _hMessage = msg => EnqueueAction(() => _onMessage?.Invoke(msg));
        _hDisconnect = () => EnqueueAction(() => _onDisconnect?.Invoke());
        _hError = err => EnqueueAction(() => _onError?.Invoke(err));
        _client.OnIdAssigned += _hIdAssigned;
        _client.OnMessage += _hMessage;
        _client.OnDisconnect += _hDisconnect;
        _client.OnError += _hError;

        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;

        return this;
    }

    private void EnqueueAction(Action action)
    {
        _dispatchQueue.Enqueue(action);
        _signal.Set(); // Wake up the consumer thread
    }

    /// <summary>client.connect("localhost", 8888) → bool: true when the WebSocket handshake completed.</summary>
    public bool Connect()
    {
        try
        {
            if (_client is not null)
            {
                // The INNER verdict is the answer: a refused port, a timeout and a failed handshake each return false
                // there, and answering true regardless is what made `if (client.Connect())` meaningless (measured on
                // three closed ports and a non-routable address, every one answering true with IsConnected false).
                return _client.Connect(_host, _port, _timeoutMs);
            }
        }
        catch (SocketException ex)
        {
            _onError?.Invoke($"{ex.SocketErrorCode} (0x{ex.SocketErrorCode:X})");
        }
        catch (Exception ex)
        {
            _onError?.Invoke($"{ex.Message} (0x{ex.HResult:X})");
        }

        return false;
    }

    /// <summary>client.disconnect()</summary>
    public void Disconnect()
    {
        if (_client != null)
        {
            _client.OnIdAssigned -= _hIdAssigned;
            _client.OnMessage -= _hMessage;
            _client.OnDisconnect -= _hDisconnect;
            _client.OnError -= _hError;
            _client.Disconnect();
        }
    }

    private Action<string>? _onIdAssigned;
    private Action<string>? _onMessage;
    private Action<string>? _onError;
    private Action? _onDisconnect;

    /// <summary>client.onId(fn(id)): déclenché quand le serveur assigne un id</summary>
    public void OnId(ScriptFunction fn)
        => _onIdAssigned = id => _interpreter.Value.InvokeScriptFunction(fn, [id], 0, 0);

    /// <summary>client.onMessage(fn(msg))</summary>
    public void OnMessage(ScriptFunction fn)
        => _onMessage = msg => _interpreter.Value.InvokeScriptFunction(fn, [msg], 0, 0);

    /// <summary>client.onDisconnect(fn())</summary>
    public void OnDisconnect(ScriptFunction fn)
        => _onDisconnect = () => _interpreter.Value.InvokeScriptFunction(fn, [], 0, 0);

    /// <summary>client.onError(fn(err))</summary>
    public void OnError(ScriptFunction fn)
        => _onError = err => _interpreter.Value.InvokeScriptFunction(fn, [err], 0, 0);

    /// <summary>client.send(data)</summary>
    public void Send(string data)
        => _client?.Send(data);

    /// <summary>client.id → clientId assigné par le serveur, null si pas encore assigné</summary>
    public string? Id
        => _client?.AssignedId;

    /// <summary>server.name</summary>
    public string Name
        => "Test";

    /// <summary>client.isConnected</summary>
    public bool IsConnected
        => _client?.IsConnected ?? false;

    /// <summary>client.port</summary>
    public int Port
        => _port;

    /// <summary>
    /// Drain the dispatch queue without blocking. Call this from a pump loop
    /// instead of Wait() when you need to interleave with other operations.
    /// </summary>
    public void Pump()
    {
        while (_dispatchQueue.TryDequeue(out var action))
            action();
    }

    /// <summary>
    /// Dispatch les callbacks sur le thread script.
    /// </summary>
    public void Wait()
    {
        while (_client?.IsConnected == true)
        {
            // Wait for 100ms. If EnqueueAction calls .Set(), this returns TRUE immediately.
            if (!_signal.Wait(100))
            {
                // A slice with nothing to dispatch is time the SCRIPT spends inside one native call, so no statement
                // boundary is reached and a window this run owns would freeze for as long as the wait lasts (measured:
                // zero timer ticks in 1295 ms). `sleep` pumps its slices the same way, and a thread with no window
                // pays one check.
                ScriptLangCore.Interpreter.NativeApp.PumpForWorkerRun();
                continue;
            }

            // We only reset if the signal was actually caught
            _signal.Reset();

            // Drain the queue
            while (_dispatchQueue.TryDequeue(out var action))
                action();
            // If it timed out, the loop just restarts and checks _server.IsRunning
        }

        // Process any events queued after disconnection (including OnDisconnect)
        while (_dispatchQueue.TryDequeue(out var action))
            action();
    }

    /// <summary>Ensure event handlers are unsubscribed even if Disconnect() was not called explicitly.</summary>
    public void Dispose()
    {
        Disconnect();
        GC.SuppressFinalize(this);
    }
}
