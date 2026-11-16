# Runtime Helpers

Located in `src/Runtime/`  standalone runtime helpers (not part of the Interpreter partial class).

## Files

| File | Namespace | Key Types | Purpose |
|------|-----------|-----------|---------|
| `ComHelper.cs` | `ScriptLangCore` | `ComHelper` | Raw COM interop via DispID |
| `Networking.cs` | `ScriptLangCore.Networking` | `TcpServer`, `TcpClient`, `ClientStatus` | TCP networking for scripts |
| `JsonExtensions.cs` | `ScriptLangCore` | `JsonParseExtensions` | UTF-8 JSON parsing for the MCP and CLI paths |
| `LinqExtensions.cs` | `ScriptLangCore.ScriptExtensions` | `LinqExtensions` | LINQ-style extension methods |

## ComHelper

Raw COM automation using `IDispatch`:

```csharp
ComHelper.Initialize();
int excel = ComHelper.CreateObject("Excel.Application");
ComHelper.PutValue(excel, "Visible", true);
string version = ComHelper.GetValue<string>(excel, "Version");
ComHelper.CallMethod(excel, "Quit");
ComHelper.ReleaseObject(excel);
ComHelper.Uninitialize();
```

## Networking

Script-level TCP server/client:

```csharp
var server = new TcpServer(interpreter);
server.Start(8080);

var client = new TcpClient(interpreter);
client.Connect("localhost", 8080);
client.Send("Hello");
var response = client.Receive();
```

## JSON Extensions

- `JsonParseExtensions.ParseJson(byte[])`  parses UTF-8 JSON to `object?` (returns `Dictionary<string, object?>`, `List<object?>`, primitives)
- Uses `Utf8JsonReader` for high-performance parsing
- This is the READER the MCP server and the CLI's tool-call printer use. Serializing goes through the language's own `Json` namespace (`Json.Serialize`, `Json.SerializeToUtf8Bytes`), whose core is `JsonConverter.SerializeToUtf8Bytes`: the hand-rolled `JsonSerializeExtensions` writer that sat beside this reader had NO caller and was removed on 2026-10-02, so one concern has one implementation.

## LINQ Extensions

Provides LINQ-style operations on `IScriptList` and `IScriptDictionary`:

```csharp
// From scripts:
var even = list.where((int x) => { ... } => x % 2 == 0);
var names = people.select((p) => { ... } => p.Name);
var sorted = nums.orderBy((x) => { ... } => x);
```
