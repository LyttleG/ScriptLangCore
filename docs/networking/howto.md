# WebSocket Networking API

The ScriptLangCore interpreter provides a built-in WebSocket server and client implementation for real-time bidirectional communication.

## API Overview

| Script access | Type | Description |
|---|---|---|
| `Server` | `TcpServer` instance | Create and manage a WebSocket server |
| `Client` | `TcpClient` instance | Create and manage a WebSocket client |
| `ClientStatus` | enum | `Pending`, `Acknowledged`, `Disconnected` |

---

## Server API

### Creating and starting

```
var svr = Server.Create(port, mode?, name?);
svr.Start();
```

- `port`, TCP port number
- `mode`, `"internal"` (localhost only) or `"external"` (any interface), default `"local"`
- `name`, optional server name for identification

### Event handlers (set BEFORE Start)

```
svr.OnConnect(fun(clientId) { ... });
svr.OnMessage(fun(clientId, msg) { ... });
svr.OnDisconnect(fun(clientId) { ... });
```

- `OnConnect`, fires when a client completes the WebSocket handshake. `clientId` is a unique 12-char hex string.
- `OnMessage`, fires when a client sends a text message
- `OnDisconnect`, fires when a client disconnects

### Sending messages

```
svr.Send(clientId, msg);           // send to one client
svr.Broadcast(msg);                // send to ALL connected clients
svr.Broadcast(msg, excludeId);     // send to all EXCEPT one client
```

### Event dispatch

```
svr.Listen();     // BLOCKING: dispatches events until server stops
svr.Pump();       // NON-BLOCKING: drains queued events, returns immediately
```

- Use `Listen()` in simple scripts where blocking is acceptable
- Use `Pump()` in a coroutine loop when you need to interleave with other work

---

## Client API

### Creating and connecting

```
var cli = Client.Create(host, port);
cli.Connect();     // returns bool
```

### Event handlers (set BEFORE Connect)

```
cli.OnMessage(fun(msg) { ... });
cli.OnDisconnect(fun() { ... });
```

### Sending

```
cli.Send(data);    // sends a text frame
```

### Event dispatch

```
cli.Wait();     // BLOCKING: dispatches events until disconnect
cli.Pump();     // NON-BLOCKING: drains queued events, returns immediately
```

### Properties

```
cli.IsConnected     // bool, connection status
cli.AssignedId      // string, clientId assigned by server (available after __welcome)
```

---

## System Messages

The convention for system-level messages uses `__` prefix. These are handled by the client internally and should not be displayed as chat messages.

| Message | Direction | Purpose |
|---|---|---|
| `__id:{id}` | Server → Client | Server assigns a clientId |
| `__ack:{id}` | Client → Server | Client acknowledges the assigned id (auto-sent) |
| `__welcome:{nick}` | Server → Client | Server confirms connection and sends nick |
| `__join:{nick}` | Server → All | A new client joined |
| `__leave:{nick}` | Server → All | A client left |
| `__ping` | Server → Client | Keep-alive probe |
| `__pong` | Client → Server | Keep-alive response |
| `__nick:{nick}` | Client → Server | Request a nick change |
| `__rename:{old} is now {new}` | Server → All | Nick change notification |
| `__error:{message}` | Server → Client | Error message (nick taken, etc.) |
| `__timeout` | Server → Client | Connection timed out due to no ping response |
| `__users:{list}` | Server → Client | List of connected users |
| `__bye` | Client → Server | Graceful disconnect notification |

---

## Example: Minimal Server

```
var svr = Server.Create(8080, "internal", "Demo");
svr.Start();

svr.OnConnect(fun(id) {
    print("Client connected: " + id);
    svr.Send(id, "__welcome:" + id.Substring(id.Length - 4));
});

svr.OnMessage(fun(id, msg) {
    svr.Broadcast("[" + id + "] " + msg, id);
});

svr.Listen();  // blocks
```

## Example: Minimal Client

```
var cli = Client.Create("localhost", 8080);
cli.OnMessage(fun(msg) { print(msg); });
cli.Connect();
cli.Send("Hello!");
cli.Wait();  // blocks dispatching events
```

## Interactive Client Pattern

Use `cli.Pump()` in a loop to interleave event dispatch with input processing:

```
var cli = Client.Create("localhost", 8080);
cli.OnMessage(fun(msg) { print(msg); });
cli.Connect();

var line = ".";
while (line != null && line != "") {
    cli.Pump();
    line = Console.ReadLine();
    if (line != "") cli.Send(line);
}
cli.Disconnect();
```

## Full Chat Demo

See `assets/language/runtime/ws_chat_server.slc` and `ws_chat_client.slc` for a complete chat implementation with broadcasts, whispers, nick changes, and keep-alive.
