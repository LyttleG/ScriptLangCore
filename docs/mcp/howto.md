# MCP Server, How To

ScriptLangCore can act as an **MCP (Model Context Protocol) server**, exposing script-defined tools to AI assistants like Claude Desktop, VS Code Copilot, or any MCP-compatible host.

## Quick start

```bash
# 1. Start the MCP server
ScriptLangCore.exe --mcp --allowed-dir C:\Projects\my-data

# 2. Configure your AI client to connect to it
# Claude Desktop → see "Configuration" section below
```

The server loads all `.slc` files from the `tools/` directory, registers any tools defined via `RegisterMcpTool()`, and starts listening for JSON-RPC 2.0 messages over **stdio** (default) or **HTTP+SSE** (with `--port`).

> **Not just for Claude.** MCP is an open protocol (JSON-RPC 2.0 over stdio or SSE). The same config works with VS Code extensions (Cline, Continue, GitHub Copilot), IDEs (IntelliJ via Continue), editors (Cursor, Windsurf), and custom clients built with the `mcp` Python/TypeScript SDKs. The server doesn't know or care which client connected, it speaks standard MCP.

---

## Table of contents

- [Command-line reference](#command-line-reference)
- [Writing tools](#writing-tools)
- [RegisterMcpTool API](#registermcpTool-api)
- [Argument marshalling](#argument-marshalling)
- [Path sandbox](#path-sandbox)
- [Transport modes](#transport-modes)
- [REPL + MCP combo](#repl--mcp-combo)
- [Configuration for AI clients](#configuration-for-ai-clients)
- [REPL commands](#repl-commands)
- [MCP Protocol Methods](#mcp-protocol-methods-not-tools)
- [Full example](#full-example)
- [Troubleshooting](#troubleshooting)

---

## Command-line reference

```bash
ScriptLangCore.exe --mcp [--port <number>] [--url <prefix>] [--allowed-dir <path>]
```

| Flag | Required | Description |
|------|----------|-------------|
| `--mcp` | Yes | Start in MCP server mode instead of CLI/REPL |
| `--port <N>` | No | Enable HTTP+SSE transport on `http://localhost:N/`. Default: stdio |
| `--url <prefix>` | No | Full URL prefix (supports `https://`). Overrides `--port`. Example: `--url https://*:8765/` |
| `--allowed-dir <path>` | No (but recommended) | Restrict file operations to this directory. Can be repeated. |
| `--repl` | No | Start MCP server in background + launch interactive REPL for testing. Requires `--port`, if omitted, defaults to `http://localhost:8765/`. |

### Examples

```bash
# Stdio mode (default), for Claude Desktop
ScriptLangCore.exe --mcp --allowed-dir C:\Projects

# HTTP+SSE mode, for remote clients
ScriptLangCore.exe --mcp --port 8765 --allowed-dir C:\Projects

# HTTPS with custom URL prefix (requires certificate binding)
ScriptLangCore.exe --mcp --url https://+:8765/ --allowed-dir C:\Projects

# Multiple allowed directories
ScriptLangCore.exe --mcp --allowed-dir C:\Data --allowed-dir D:\Work

# REPL + MCP for debugging
ScriptLangCore.exe --mcp --port 8765 --repl --allowed-dir C:\Projects
```

---

## Writing tools

Each tool is a JavaScript function registered via `RegisterMcpTool()`. Place your scripts in the `tools/` directory alongside `ScriptLangCore.exe`.

### Minimal example

```javascript
// tools/hello.slc
RegisterMcpTool("hello", "Greet a person by name.",
    fun(String name) {
        return "Hello, " + name + "!";
    },
    '{ "type": "object", "properties": { "name": { "type": "string" } }, "required": ["name"] }'
);
```

### What happens when the server starts

1. `ScriptLangCore.exe --mcp` scans `tools/` for `*.slc` files
2. Each file is executed, `RegisterMcpTool()` registers the tool in the MCP tool registry
3. When the AI sends `tools/call`, the server looks up the tool by name, marshals the JSON arguments to script values, invokes the function, and returns the result as JSON

---

## RegisterMcpTool API

```javascript
RegisterMcpTool(name, description, handler, [schema])
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `name` | `String` | Yes | Tool name used in `tools/call`. Must be unique. |
| `description` | `String` | Yes | Human-readable description shown to the AI. |
| `handler` | `Function` | Yes | Script function invoked when the tool is called. |
| `schema` | `String` | No | JSON Schema string describing the input parameters. Helps the AI understand what arguments to pass. |

### Handler function signature

```javascript
fun(String path, String content) {
    // The function receives arguments matching the JSON-RPC
    // "arguments" object keys. Return a string, it becomes
    // the MCP response content.
    return "result";
}
```

---

## Middleware pipeline

Middleware intercepts every tool call **before** the handler runs. Register middleware with `mcp.Use`:

```javascript
tools/mcp_middleware.slc
// Logging middleware
mcp.Use((McpContext ctx, Action next) => {
    print("  [middleware] tool=" + ctx.ToolName + " args=" + ctx.Args.Count);
    next();
});
```

### Pipeline flow

| Action | Effect |
|--------|--------|
| `next()` | Continue to next middleware (or tool handler) |
| `ctx.ErrorMessage = "msg"` | Stop pipeline, returns error to caller |
| `ctx.Result = value` | Bypass tool handler, return this value directly |
| No `next()` call | Silent short-circuit, pipeline stops |

### Multiple middleware, onion order

```javascript
mcp.Use((McpContext ctx, Action next) => { print("A"); next(); print("a"); });
mcp.Use((McpContext ctx, Action next) => { print("B"); next(); print("b"); });
// Execution: A → B → handler → b → a
```

### Blocking a tool

```javascript
mcp.Use((McpContext ctx, Action next) => {
    if (ctx.ToolName == "delete_file") {
        ctx.ErrorMessage = "delete_file is blocked by policy";
        return;  // don't call next()
    }
    next();
});
```

### Context properties

| Property | Type | Description |
|----------|------|-------------|
| `ctx.ToolName` | `string` | Name of the tool being called |
| `ctx.Args` | `list` | Arguments passed to the tool |
| `ctx.Result` | `object?` | Set to return a value (bypasses handler) |

`tcp.Use` is also available for TCP connection middleware: it runs for every connection the engine's own TCP server accepts (`Server.Create` + `Server.Start`). Its context is a `TcpContext` (provides `RemoteAddress`, `Close()`).

---

## HTTP server middleware

`http.Use` registers middleware for non-MCP HTTP requests on the same port. Every request that isn't `POST /` or `GET /sse` flows through the HTTP middleware pipeline.

```bash
# tools/mcp_httpserver.slc
// Logging middleware
http.Use((HttpContext ctx, Action next) => {
    print("  [http] " + ctx.HttpMethod + " " + ctx.Url);
    next();
});

// Route: GET /hello
http.Use((HttpContext ctx, Action next) => {
    if (ctx.Url == "/hello" && ctx.HttpMethod == "GET") {
        ctx.ResponseBody = "Hello from middleware!";
        ctx.ResponseStatus = 200;
        return;  // short-circuit
    }
    next();
});
```

### Context properties

| Property | Type | Description |
|----------|------|-------------|
| `ctx.HttpMethod` | `string` | HTTP method (GET, POST, etc.) |
| `ctx.Url` | `string` | Request path (e.g. `/hello`) |
| `ctx.Body` | `string?` | Request body (for POST/PUT) |
| `ctx.ResponseStatus` | `int` | Set to change HTTP status (default 200) |
| `ctx.ResponseBody` | `string?` | Set to define the response text |
| `ctx.ResponseBytes` | `byte[]?` | Set to return raw binary data (overrides ResponseBody) |
| `ctx.ErrorMessage` | `string?` | Set to return HTTP 500 with error |

### Pipeline flow

Same onion-order as `mcp.Use`: `next()` continues, no `next()` short-circuits, `ctx.ErrorMessage` returns 500 error carrying its text as the body, `ctx.ResponseBody` + `ResponseStatus` define the response. A status the ENGINE sets carries no body, the way ASP.NET Core does it: a path nothing answers is a 404 with `Content-Length: 0`, and an unhandled exception is a 500 with no body (the cause goes to the server's log). Without any middleware at all, every non-MCP path returns 404.

### Testing from curl

```bash
# Welcome page
curl http://localhost:8765/

# Current server time
curl http://localhost:8765/time

# Server status (memory)
curl http://localhost:8765/status

# POST JSON echo
curl -X POST http://localhost:8765/echo -H "Content-Type: application/json" -d '{"message":"hello"}'

# Download a file
curl -X POST http://localhost:8765/download -d "README.md" -o README.md

# Download binary, exact bytes preserved via ResponseBytes
curl -X POST http://localhost:8765/download -d "publish-aot/ScriptLangCore.exe" -o ScriptLangCore.exe
```

### Testing from browser DevTools

```js
// GET routes
fetch("http://localhost:8765/time").then(r => r.text()).then(console.log)
// → "2026-07-10 19:45:30"

// POST with JSON body
fetch("http://localhost:8765/echo", {
  method: "POST",
  headers: {"Content-Type": "application/json"},
  body: JSON.stringify({hello: "world"})
}).then(r => r.text()).then(console.log)
```

> **Routes are shared with MCP.** All requests arrive on the single HTTP listener. MCP paths (`POST /` and `GET /sse`) are handled first; everything else passes through the HTTP middleware pipeline. To start the server: `ScriptLangCore.exe --mcp --port 8765`.

---

## Argument marshalling

When the AI calls a tool, the MCP server:

1. **Reads the parameter names** from the function declaration (e.g., `path`, `content`)
2. **Looks up matching keys** in the `arguments` JSON object
3. **Converts values** to script-compatible types:

| JSON type | Script type |
|-----------|-------------|
| `string` | `String` |
| `number` | `int` or `double` |
| `true` / `false` | `bool` |
| `null` | `null` |
| `object` / `array` | `String` (raw JSON text) |

If the function has no named parameters, the entire `arguments` object is passed as a single JSON string.

---

## Path sandbox

The `--allowed-dir` flag restricts which files tools can access:

```bash
ScriptLangCore.exe --mcp --allowed-dir C:\Projects
```

The sandbox only checks string arguments that **look like file paths** (contain a `/`, `\`, `.`, or start with a drive letter like `C:`). Plain strings like `"hello"` are allowed through. Any tool that tries to read or write a file outside the allowed directory receives an error:

```json
{
  "error": {
    "code": -32000,
    "message": "Access denied: 'D:\\secrets.txt' is outside allowed directories."
  }
}
```

### How it works

- Paths are resolved via `Path.GetFullPath()` before checking
- Comparison is case-insensitive
- Subdirectories of the allowed directory are permitted
- Multiple `--allowed-dir` flags can be specified for multiple directories

**Without `--allowed-dir`, tools have unrestricted filesystem access**, the sandbox is opt-in.

---

## REPL + MCP combo

Use `--repl` to run the MCP server in a background thread while keeping the interactive REPL. This is useful for debugging, you can call tool functions directly from the REPL while the server handles AI requests.

> **`--repl` requires `--port` (SSE transport).** Both the REPL and the stdio MCP server read from stdin, so they cannot coexist in stdio mode. If you specify `--repl` without `--port`, the server automatically defaults to `http://localhost:8765/`. For pure stdio mode (e.g. for Claude Desktop), omit `--repl` and pipe JSON-RPC messages via stdin.

```bash
ScriptLangCore.exe --mcp --port 8765 --repl --allowed-dir C:\Projects
```

From the REPL you can:
- Type `.mcp` to list all registered tools
- Call tool functions directly: `.call test_echo {"message":"hi"}`
- Modify scripts and re-register tools (restart required to reload from disk)
- Test edge cases before letting the AI use the tool

## Transport modes

### Stdio (default)

The server reads JSON-RPC messages from stdin and writes responses to stdout. This is the standard MCP transport and works with Claude Desktop, VS Code, and most MCP hosts.

```
┌──────────────┐  stdio JSON-RPC  ┌──────────────────┐
│  AI Client   │ ◄──────────────► │  ScriptLangCore  │
│  (Claude)    │                  │  (--mcp)          │
└──────────────┘                  └──────────────────┘
```

> **Stdio = JSON-RPC only.** In pure stdio mode (without `--port` or `--repl`), every line is read as JSON-RPC. Typing `.help` or other REPL commands will fail with a parse error. For interactive testing, use `--repl --port 8765` instead.

#### Send requests from PowerShell

Pipe a JSON-RPC message to the exe:

```powershell
# List tools
'{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | .\ScriptLangCore.exe --mcp

# Ping
'{"jsonrpc":"2.0","id":1,"method":"ping"}' | .\ScriptLangCore.exe --mcp

# Call a tool
'{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hi"}}}' | .\ScriptLangCore.exe --mcp
```

For file-based requests:

```powershell
# Save request to file
Set-Content request.json '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# Pipe the file
Get-Content request.json | .\ScriptLangCore.exe --mcp

# Capture and format the response
$response = Get-Content request.json | .\ScriptLangCore.exe --mcp
$response | ConvertFrom-Json | ConvertTo-Json -Depth 10
```

### HTTP+SSE (`--port` / `--url`)

When `--port` or `--url` is specified, the server listens for HTTP(S) connections:

```
POST /          → JSON-RPC request/response (tools/call, tools/list)
GET  /sse       → Server-Sent Events stream for notifications
```

All other paths return 404.

### Testing from a browser or HTTP client

You can call MCP tools directly from any HTTP client, no AI assistant needed.

#### Browser DevTools Console

Open DevTools (<kbd>F12</kbd>) on any page and paste:

**List tools:**

```javascript
fetch("http://localhost:8765/", {
  method: "POST",
  headers: {"Content-Type": "application/json"},
  body: JSON.stringify({
    jsonrpc: "2.0", id: 1, method: "tools/list"
  })
}).then(r => r.json()).then(console.log)
```

**Call a tool:**

```javascript
fetch("http://localhost:8765/", {
  method: "POST",
  headers: {"Content-Type": "application/json"},
  body: JSON.stringify({
    jsonrpc: "2.0", id: 2, method: "tools/call",
    params: { name: "test_echo", arguments: { message: "hello from browser" } }
  })
}).then(r => r.json()).then(console.log)
```

**More examples:**

```javascript
// test_add, two integers
fetch("http://localhost:8765/", {method:"POST",headers:{"Content-Type":"application/json"},
  body: JSON.stringify({jsonrpc:"2.0", id:3, method:"tools/call",
    params:{name:"test_add", arguments:{a:42, b:58}}
  })}).then(r=>r.json()).then(console.log)

// test_bool, booleans
fetch("http://localhost:8765/", {method:"POST",headers:{"Content-Type":"application/json"},
  body: JSON.stringify({jsonrpc:"2.0", id:4, method:"tools/call",
    params:{name:"test_bool", arguments:{flag:true, enabled:false}}
  })}).then(r=>r.json()).then(console.log)

// test_mixed, all types together
fetch("http://localhost:8765/", {method:"POST",headers:{"Content-Type":"application/json"},
  body: JSON.stringify({jsonrpc:"2.0", id:5, method:"tools/call",
    params:{name:"test_mixed", arguments:{name:"widget", count:5, price:9.99, active:true}}
  })}).then(r=>r.json()).then(console.log)
```

#### curl

> **Note:** The `-d` flag is required, it sets the `Content-Length` header. Without it, `HttpListener` returns HTTP 411 (Length Required).

```bash
# List tools
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# Call a tool
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"test_echo","arguments":{"message":"hello from curl"}}}'
```

#### PowerShell

```powershell
# List tools
Invoke-RestMethod -Uri http://localhost:8765/ -Method Post -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' -ContentType "application/json"

# Call a tool
Invoke-RestMethod -Uri http://localhost:8765/ -Method Post -Body '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"test_echo","arguments":{"message":"hi from powershell"}}}' -ContentType "application/json"
```

Expected response:

```json
{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"ECHO: hello from curl"}]}}
```

#### HTTPS support

`HttpListener` supports HTTPS when a certificate is bound to the port. To set up HTTPS:

```bash
# 1. Create a self-signed certificate (one time)
dotnet dev-certs https --trust

# 2. Get the certificate thumbprint (40-char hex string)
certutil -store -user My | findstr /i "sha1"
# Copy the hex string (e.g., dc40e6b404fde6c692946efbe0e679193330e830)

# 3. Bind the certificate to the port (run as Administrator)
netsh http add sslcert ipport=0.0.0.0:8765 certhash="REPLACE_WITH_ACTUAL_THUMBPRINT" appid="{00000000-0000-0000-0000-000000000000}"

# 4. Start with HTTPS
ScriptLangCore.exe --mcp --url https://+:8765/ --allowed-dir C:\Projects

#### HTTPS troubleshooting

**Error 1312: "A specified logon session does not exist"**, the certificate is in the **Current User** store but HTTP.sys requires it in the **Local Machine** store.

```bash
# Export from Current User with private key
certutil -user -exportpfx -p "password" MY REPLACE_WITH_THUMBPRINT mycert.pfx

# Import into Local Machine store (run as Administrator)
certutil -importpfx -p "password" mycert.pfx

# Grant NETWORK SERVICE read access to the private key
# certlm.msc → Personal → Certificates → right-click cert →
# All Tasks → Manage Private Keys → Add NETWORK SERVICE with Read

# Now bind should work
netsh http add sslcert ipport=0.0.0.0:8765 certhash="REPLACE_WITH_ACTUAL_THUMBPRINT" appid="{00000000-0000-0000-0000-000000000000}"
```

**Note for local development:** If the AI assistant runs on the same machine, HTTP on localhost is safe, traffic never leaves your computer. Skip the HTTPS setup and use `--port` instead.
```

The `--url` flag accepts any prefix that `HttpListener` supports:
- `http://localhost:8765/`, localhost only (default with `--port`)
- `http://*:8765/`, all network interfaces, HTTP
- `https://+:8765/`, all network interfaces, HTTPS (requires certificate binding)
- `https://myhost.example.com:8765/`, specific hostname

---

## Configuration for AI clients

### Claude Desktop

Edit `claude_desktop_config.json` (usually at `%APPDATA%\Claude\`):

```json
{
  "mcpServers": {
    "scriptlang": {
      "command": "C:\\Tools\\ScriptLangCore.exe",
      "args": ["--mcp", "--allowed-dir", "C:\\Projects"]
    }
  }
}
```

### VS Code (Cline / Continue)

In your VS Code extension settings for Cline or Continue:

```json
{
  "mcpServers": {
    "scriptlang": {
      "command": "C:\\Tools\\ScriptLangCore.exe",
      "args": ["--mcp", "--allowed-dir", "C:\\Projects"]
    }
  }
}
```

### Compatibility with other AI tools

MCP is an **open protocol**, it is not tied to Anthropic or Claude. Any AI tool that implements the MCP client side can connect to this server without modification.

| Tool / Editor | MCP support | Config location | Same config works? |
|---------------|-------------|-----------------|-------------------|
| **Claude Desktop** | Native | `%APPDATA%\Claude\claude_desktop_config.json` | ✅ Identical JSON |
| **Cline** (VS Code) | Built-in | VS Code settings → `mcpServers` | ✅ Identical JSON |
| **Continue** (VS Code / IntelliJ) | Built-in | VS Code / IntelliJ settings → `mcpServers` | ✅ Identical JSON |
| **GitHub Copilot** (VS Code) | Via extension | VS Code settings → `mcpServers` | ✅ Identical JSON |
| **Cursor** | Built-in | Project root → `.cursor/tools.json` | ⚠️ Slightly different path |
| **Windsurf** | Native | Settings → MCP servers | ✅ Identical JSON |
| **Custom client** | Via MCP SDK | Any language (Python, TS, Java) | ✅ Stdio or SSE |

#### Config for Cursor

```json
// .cursor/tools.json  (place in your project root)
{
  "scriptlang": {
    "command": "C:\\Tools\\ScriptLangCore.exe",
    "args": ["--mcp", "--allowed-dir", "C:\\Projects"]
  }
}
```

#### Config for a custom Python client

```python
# connect_via_sse.py
import json, requests

# List available tools
resp = requests.post("http://localhost:8765", json={
    "jsonrpc": "2.0", "id": 1, "method": "tools/list"
})
print(resp.json())

# Call a tool
resp = requests.post("http://localhost:8765", json={
    "jsonrpc": "2.0", "id": 2, "method": "tools/call",
    "params": {"name": "read_file", "arguments": {"path": "test.txt"}}
})
print(resp.json())
```

#### Config for a custom TypeScript client (stdin/stdout)

```typescript
// connect_via_stdio.ts
import { spawn } from "child_process";

const mcp = spawn("C:\\Tools\\ScriptLangCore.exe", [
  "--mcp", "--allowed-dir", "C:\\Projects"
]);

mcp.stdin.write(JSON.stringify({
  jsonrpc: "2.0", id: 1, method: "tools/list"
}) + "\n");

mcp.stdout.on("data", (data) => {
  console.log("Response:", data.toString());
});
```

The wire protocol is always the same, JSON-RPC 2.0 with `\n`-delimited messages over stdio, or HTTP POST + SSE for remote connections. **The server doesn't know or care which client is connected.**

---

## MCP Protocol Methods (not tools)

The MCP server handles several protocol-level methods that don't appear in `.mcp` because they're not registered via `RegisterMcpTool`. These are built into the server and handle lifecycle, logging, and resource discovery.

### Lifecycle

| Method | Purpose | Example |
|--------|---------|---------|
| `ping` | Health check, keeps clients connected | `{"jsonrpc":"2.0","id":1,"method":"ping"}` |
| `initialize` | Protocol version + capability negotiation | Sent automatically by clients on connection |
| `notifications/initialized` | Client confirms initialization complete | Notification, no response expected |

The server responds to `initialize` with the protocol version and advertised capabilities:

```json
{
  "protocolVersion": "2025-03-26",
  "capabilities": {
    "tools": {},
    "resources": {},
    "logging": {}
  }
}
```

### Logging

Clients can set the log verbosity level to receive diagnostic messages:

```bash
# Set log level (debug, info, notice, warning, error)
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"debug"}}'

# Response: {"jsonrpc":"2.0","id":1,"result":{}}
```

Log messages are written to **stderr** (not stdout, which carries JSON-RPC). They are filtered by the configured level:

| Level | Shows | Example output |
|-------|-------|----------------|
| `debug` | Everything | `[MCP debug] → tools/call id=1` |
| `info` | Tool calls + errors | `[MCP infos] tool:system_info args:0` |
| `notice` | Same as info (reserved) | |
| `warning` | Errors only | |
| `error` | Errors only | `[MCP error] tool:read_file failed: ...` |
| `off` | Nothing | |

Setting `.log debug` traces every incoming JSON-RPC method with full HTTP headers and body. Setting `.log info` shows tool invocations and results (displayed as `infos` for alignment). Setting `.log error` only shows failures.

Example output at `debug` level:

```
[2026-06-04 20:21:44][MCP debug] ↓ POST / from [::1]:65157
[2026-06-04 20:21:44][MCP debug]   Content-Length: 113
[2026-06-04 20:21:44][MCP debug]   Content-Type: application/json
[2026-06-04 20:21:44][MCP debug]   Accept: */*
[2026-06-04 20:21:44][MCP debug]   Host: localhost:8765
[2026-06-04 20:21:44][MCP debug]   User-Agent: curl/8.18.0
[2026-06-04 20:21:44][MCP debug]   Body[113]: {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"hello from curl"}}}
[2026-06-04 20:21:44][MCP debug] → tools/call id=2
[2026-06-04 20:21:44][MCP infos] tool:echo args:1
```

Example output at `info` level (tool call only, no HTTP details):

```
[2026-06-04 20:21:44][MCP infos] tool:echo args:1
```

### RegisterMcpResource API

Resources expose read-only data via the MCP resources protocol. Register them from scripts:

```javascript
RegisterMcpResource(uri, name, mimeType, handler)
```

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `uri` | `String` | Yes | Unique identifier (e.g., `"info/system"`) |
| `name` | `String` | Yes | Human-readable name shown to the AI |
| `mimeType` | `String` | Yes | MIME type (`"text/plain"`, `"application/json"`, etc.) |
| `handler` | `Function` | Yes | Zero-argument function returning content as string |

**Handler is zero-argument**, no parameters, returns the resource content as a string:

```javascript
// Inline lambda
RegisterMcpResource("info/system", "System Info", "text/plain",
    fun() { return "OS: " + Environment.OSVersion; }
);

// Named function also works
String myHandler() { return File.ReadAllText("README.md"); }
RegisterMcpResource("docs/readme", "README", "text/markdown", myHandler);
```

### Resources

Resources are read-only data exposed alongside tools. They are listed via `resources/list` and read via `resources/read`:

```bash
# List available resources
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"resources/list"}'

# → {"jsonrpc":"2.0","id":1,"result":{"resources":[{"uri":"info/system","name":"System Info","mimeType":"text/plain"}]}}

# Read a specific resource
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"info/system"}}'

# → {"jsonrpc":"2.0","id":2,"result":{"contents":[{"uri":"info/system","mimeType":"text/plain","text":"OS: Microsoft Windows NT 10.0...\nCores: 16\n..."}]}}
```

### Tools vs Resources

| | Tools (`tools/call`) | Resources (`resources/read`) |
|---|---|---|
| Use for | **Actions**, write files, modify state | **Data**, read-only files, config, info |
| From `.call` REPL | ✅ `.call read_file {"path":"..."}` | ❌ Protocol method, not a tool |
| From curl | ✅ `tools/call` | ✅ `resources/read` |
| Discovery | `.mcp` listing | `resources/list` |
| Parameters | Required (tool-defined) | None (handler is zero-arg) |

### Testing from curl

```bash
# Ping the server
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"ping"}'
# → {"jsonrpc":"2.0","id":1,"result":{}}

# Check protocol version
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'
# → {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2025-03-26","capabilities":{"tools":{},"resources":{},"logging":{}}}}

# Set debug logging
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"logging/setLevel","params":{"level":"debug"}}'

# List resources
curl -X POST http://localhost:8765/ -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"resources/list"}'
```

---

## Full example

Here's a complete MCP tool script that exposes file operations to the AI:

```javascript
// tools/file_tools.slc
// Provides read, write, and list operations via MCP

// ── Read a file ──
RegisterMcpTool("read_file", "Read the full contents of a text file from disk.",
    fun(String path) {
        if (!File.Exists(path))
            return "Error: file not found: " + path;
        return File.ReadAllText(path);
    },
    '{ "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }'
);

// ── Write a file ──
RegisterMcpTool("write_file", "Write text content to a file (creates or overwrites).",
    fun(String path, String content) {
        File.WriteAllText(path, content);
        return "Written: " + path + " (" + content.Length + " chars)";
    },
    '{ "type": "object", "properties": { "path": { "type": "string" }, "content": { "type": "string" } }, "required": ["path", "content"] }'
);

// ── List directory contents ──
RegisterMcpTool("list_dir", "List all files in a directory.",
    fun(String dir) {
        if (!Directory.Exists(dir))
            return "Error: directory not found: " + dir;
        var files = Directory.GetFiles(dir);
        var result = "Files in " + dir + ":\n";
        var i = 0;
        while (i < files.Count) {
            result = result + "  " + files[i] + "\n";
            i = i + 1;
        }
        return result;
    },
    '{ "type": "object", "properties": { "dir": { "type": "string" } }, "required": ["dir"] }'
);

// ── Get file size ──
RegisterMcpTool("file_size", "Get the size of a file in bytes.",
    fun(String path) {
        if (!File.Exists(path))
            return "Error: file not found: " + path;
        var size = File.GetSize(path);
        return path + ", " + size + " bytes";
    },
    '{ "type": "object", "properties": { "path": { "type": "string" } }, "required": ["path"] }'
);

// ── Win32 system info (WinApi demo) ──
RegisterMcpTool("system_info", "Return OS and process information.",
    fun() {
        var os = Environment.OSVersion;
        var proc = Environment.ProcessorCount;
        var mem = GC.GetTotalMemory();
        return "OS: " + os + "\nCPU cores: " + proc + "\nGC memory: " + mem + " bytes";
    },
    '{ "type": "object", "properties": {} }'
);
```

---

## REPL commands

Start the MCP server on a background thread with the interactive REPL for live testing:

```bash
ScriptLangCore.exe --mcp --port 8765 --repl --allowed-dir C:\Projects
```

Once started, the REPL provides these MCP-specific commands:

### `.log`, set MCP log level

```
>>> .log debug
Log level set to: debug

>>> .log warning
Log level set to: warning

>>> .log off
Logging disabled.
```

Levels: `debug` < `info` < `notice` < `warning` < `error`. Setting a level shows all messages at that level and above. `off` disables logging.

### `.mcp`, list registered tools

```
>>> .mcp
Registered MCP tools (6):
  read_file(path: string)                           Read the full contents of a text file from disk.
  write_file(path: string, content: string)         Write text content to a file (creates or overwrites).
  list_dir(dir: string)                             List all files in a directory.
  test_echo(message: string)                        Echo back the input (quick connectivity test).
  test_add(a: int, b: int)                          Add two numbers (tests number marshalling).
  [Protocol methods: ping, initialize, logging/setLevel, resources/list]
```

### `.call`, invoke a tool directly

```
>>> .call test_echo {"message":"hi"}
ECHO: hi

>>> .call test_add {"a":3,"b":4}
Result: 7

>>> .call read_file {"path":"test.txt"}
(file contents)
```

### Supported JSON argument types

| JSON type | Script type | Example |
|-----------|-------------|---------|
| `string` | `String` | `.call test_echo {"message":"hello"}` |
| `integer` | `int` (or `long`) | `.call test_add {"a":3,"b":4}` |
| `float` | `double` | `.call round {"value":3.14}` |
| `true` / `false` | `bool` | `.call set_flag {"enabled":true}` |
| `null` | `null` | `.call clear {"value":null}` |
| `object` / `array` | `String` (raw JSON) | `.call parse {"data":{"key":"val"}}` |

The `.call` command tests the exact same code path the AI uses, JSON-RPC → argument marshalling → `InvokeScriptFunction` → result formatting. This is useful for debugging tool behavior without needing an AI client connected.

---

## Troubleshooting

### Server starts but tools don't appear

Check that your script files are in the `tools/` directory next to `ScriptLangCore.exe`. The server looks for `tools/*.slc` relative to the executable path.

### "Unknown tool" errors

Make sure `RegisterMcpTool` is called at the top level of your script, not inside a function. The registration runs when the script is loaded at startup.

### JSON parsing errors

Verify the JSON Schema string in `RegisterMcpTool` is valid JSON. Use a linter if unsure. The schema is optional, you can omit it and the AI will infer the arguments from the function signature.

### File access denied

If you see "Access denied" errors, either:
- Add the file's parent directory with `--allowed-dir`
- Or remove `--allowed-dir` entirely to allow unrestricted access

### Windows piping issues

On Windows, stdin piping from `echo` may not work correctly with binary executables. Use `type request.json | ScriptLangCore.exe --mcp` instead.
