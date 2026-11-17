# REPL Commands Reference

Located in `src/Program.DotCommands.cs` (the session commands) and `src/Repl/DotCommands.cs` (the interpreter-local ones), the interactive shell commands available in REPL mode.

## Overview

The REPL (Read-Eval-Print Loop) provides a set of dot commands prefixed with `.` that allow controlling the interpreter session, inspecting state, and managing execution. Every dot line enters through one entry point, `Program.DispatchDotCommand`, which expands an alias once and then offers the line to the interpreter-side dispatcher (`Repl.DotCommands.Handle`) and, if that does not take it, to the session-side one (`Program.HandleDotCommand`). No command name may appear in both, and `Repl.DotCommands.CommandHelp` is the table behind `.help` and `IsKnownCommand`; `src/build/check-dot-commands.sh` holds the two dispatchers, the table and the characterization suite together.

| Command | Action |
|---------|--------|
| `.exit` / `.quit` | Exit the interpreter |
| `.clear` / `.cls` | Clear screen (a session with no console is not an error) |
| `.reset` | Clear all global variables, reset interpreter state, and clear command history |
| `.reset all` | Full reset, wipes user-defined types (classes, structs, delegates), module caches, and frees native library handles (<code>DynamicLibrary</code>). Built-in functions, .NET types, and namespaces are preserved. |
| `.reset hard` | Dispose the interpreter and create a fresh instance, every cache and static dictionary with it. Refused, with the reason, when an embedding host owns the interpreter (`.reset all` clears that one in place). |
| <kbd>Ctrl</kbd>+<kbd>C</kbd> | <code>KeyboardInterrupt</code>, cancels the current line or interrupts a running script without exiting the REPL (like Python). A fresh prompt appears afterward. |

### Information & Editing

| Command | Action |
|---------|--------|
| `.help` | Show this command reference |
| `.infos` | Show version, build date, AOT status, runtime info |
| `.history [N]` | Show last N commands (default: all, max 500). Multi-line entries display with indented continuation lines |
| `.edit <line>` | Display a specific history entry (use the number from `.history`). Shows the exact code that was entered, preserving multi-line formatting |

### History Recall

| Command | Action |
|---------|--------|
| `!<N>` | Re-execute history entry N (bash-style). The entry is printed then executed. Useful for replaying or fixing previous commands |

Example:

```
>>> var x = 10;
>>> var y = 20;
>>> print(x + y);
30
>>> !2                  # re-executes "var y = 20;"
>>> var y = 20;
Warning: Variable 'y' is being redeclared at line 4.
>>> !1                  # re-executes "var x = 10;"
>>> var x = 10;
Warning: Variable 'x' is being redeclared at line 5.
```

### Mode Toggle

| Command | Action |
|---------|--------|
| `.strict` | Enable case-sensitive mode (default). `A` and `a` are distinct variables |
| `.lax` | Enable case-insensitive mode. `A` and `a` refer to the same variable |
| `.time` | Toggle execution timing, shows elapsed ms after each result |
| `.echo on/off` | Toggle echo mode, each input line is shown before execution (useful when piping) |

### Variable Inspection

| Command | Action |
|---------|--------|
| `.vars` | List user-defined global variables with Name, Type, Line, Column |
| `.varsall [cat]` | List ALL global scope keys grouped by category |

**Categories** (for `.varsall [cat]`):

| Category | Description |
|----------|-------------|
| `types` | Built-in .NET types (`int`, `string`, `DateTime`, etc.) |
| `native` | Native functions (`print`, `sleep`, `typeof`, etc.) |
| `delegates` | Delegate type aliases (`Action`, `Func`, `Predicate`) |
| `namespaces` | Namespace objects (`Math`, `Http`, `Json`, `Colors`) |
| `functions` | Built-in function references |
| `classes` | Class definitions |
| `structs` | Struct declarations |
| `user` | User-defined variables (primitives, lists, dicts, instances) |
| `lists` | Built-in list types |
| `dictionaries` | Built-in dictionary types |
| `ref cells` | Reference cells (`exitcode`, `pause`) |
| `other` | Uncategorized entries |

Multiple categories can be specified: `.varsall types native` or `.varsall [types][native]`.

### Declaration Inspection

| Command | Action |
|---------|--------|
| `.functions` | List user-defined functions, classes, and structs with return type and location |
| `.tree <var>` | Show variable structure as a recursive tree (supports lists, dictionaries, nested types) |

### Debugging

| Command | Action |
|---------|--------|
| `.debug on/off` | Enable/disable script debugger |
| `.break <line>` | Set a breakpoint at the specified line (requires `.debug on`) |

### File Execution

| Command | Action |
|---------|--------|
| `.run <file>` | Execute a script file. Supports glob patterns and pipe-separated paths |
| `.import <file>` / `.include <file>` | Import a script into the current session (keeps state, no reset) |
| `.cd [dir]` | Show or change the script base directory for `.run` and `.import` |

### Memory

| Command | Action |
|---------|--------|
| `.gc` | Show managed memory usage |
| `.gc collect` | Force garbage collection and report freed memory |
| `.gc stats` | Show detailed GC statistics (generations, committed, fragmented) |

## Implementation Details

### Command Dispatch

Commands are dispatched via a `switch` statement in `HandleDotCommand()`. The method receives the raw input line split by spaces into `parts[]`.

```csharp
// Simplified dispatch structure
private static bool HandleDotCommand(string line, Interpreter itp)
{
    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    switch (parts[0])
    {
        case ".exit": case ".quit": return false;
        case ".clear": case ".cls": ...
        case ".help": PrintHelp(); ...
        case ".vars": ...
        case ".functions": ...
        case ".tree": ...
        case ".strict": itp.SetCaseSensitive(true); ...
        case ".lax": itp.SetCaseSensitive(false); ...
        // ...
    }
}
```

### Input Accumulation

Multi-line input is detected by tracking brace depth (`{`, `}`, `(`, `)`, `[`, `]`). When depth > 0, the REPL shows a continuation prompt `... ` instead of `>>> `.

```csharp
private static int BraceDepth(string s)
{
    var depth = 0;
    foreach (var c in s)
    {
        if (c == '{' || c == '(' || c == '[') depth++;
        else if (c == '}' || c == ')' || c == ']') depth--;
    }
    return depth;
}
```

### Line Number Tracking

In REPL mode, each input block is offset by the cumulative line count of all previous blocks (`_replLineOffset`), so error messages and `print()` output show line numbers matching the original paste.

### Variable Redeclaration

When a variable is declared with a name that already exists in the current scope, a warning is shown:

- **Exact match** (same name): `Warning: Variable 'm' is being redeclared at line 2.`
- **Case-different match** (lax mode only): `Warning: Variable 'B' already exists and will be reused as 'b' at line 3.`

Use `@name` to suppress the warning and explicitly shadow an existing type binding.

### Console Colors

| Element | Color |
|---------|-------|
| `>>>` / `...` prompt | Magenta |
| Dot command output | Cyan |
| Group headers (`.varsall`) | DarkYellow |
| Error messages | Red |
| Expression results (`= value`) | DarkGreen |
| Warnings | Yellow |
| `-- more --` pager | DarkGray |

## API Reference

### TCP Networking

ScriptLangCore provides a built-in TCP server/client system via `Server` and `Client` objects. Both are registered in the global scope at interpreter startup.

#### Server

```csharp
var host = Server.Create(port, "internal"|"external", "name");
```

| Method | Description |
|--------|-------------|
| `Server.Create(port, mode, name)` | Create a server instance. `mode` is `"internal"` or `"external"` |
| `Server.Start()` | Start listening (returns bool). Called after setting callbacks |
| `host.OnError(fn(clientId, err))` | Callback when an error occurs |
| `host.OnPending(fn(clientId))` | Callback when a new client connects (pre-ACK) |
| `host.OnConnect(fn(clientId))` | Callback when a client has sent ACK (fully registered) |
| `host.OnMessage(fn(clientId, msg))` | Callback when a message is received from a client |
| `host.OnDisconnect(fn(clientId))` | Callback when a client disconnects |
| `host.Send(clientId, data)` | Send a string message to a specific client |
| `host.Broadcast(data, excludeId?)` | Send to all connected clients (optionally exclude one) |
| `host.ClientCount()` | Number of connected clients |
| `host.ClientKick(clientId)` | Disconnect a specific client |
| `host.Clients()` | Return dictionary of connected clients (key=id, value=status) |
| `host.Listen()` | Start the message dispatch loop (blocking) |
| `host.Stop()` | Stop the server and close all connections |
| `host.Remove(clientId)` | Remove a disconnected client from the active list |
| `host.Name` | Server name (string) |

Full example: `assets/language/scratch/tutorial/25_server.slc`

#### Client

```csharp
var client = Client.Create(host, port, "internal");
```

| Method | Description |
|--------|-------------|
| `Client.Create(host, port, mode)` | Create a TCP client and connect to a server |
| `client.OnConnect(fn())` | Callback when connected |
| `client.OnMessage(fn(msg))` | Callback when a message arrives |
| `client.OnDisconnect(fn())` | Callback when disconnected |
| `client.OnError(fn(err))` | Callback on error |
| `client.Send(data)` | Send a string to the server |
| `client.IsRunning` | Whether the client is connected |
| `client.Close()` | Disconnect from the server |

### UI Subsystem (Win32)

ScriptLangCore includes a complete Win32 UI subsystem accessible through the `Application` namespace and related types.

#### Application

| Method | Description |
|--------|-------------|
| `Application.CreateForm(title)` | Create a new window (returns a Form handle) |
| `Application.Run(form?)` | Start the Windows message loop (optional initial form) |
| `Application.Exit()` | Exit the application |
| `Application.DoEvents()` | Process pending messages |
| `Application.PumpMessage()` | Pump a single message |
| `Application.IsRunning` | Whether the app is running |
| `Application.PeekMessage()` | Check for pending messages |
| `Application.WaitMessage()` | Wait for a message (blocks) |
| `ShowConsole(bool)` | Show/hide the console window |

#### Form Methods

| Method | Description |
|--------|-------------|
| `form.SetBounds(x, y, w, h)` | Set position and size |
| `form.Width` / `form.Height` | Get/set dimensions |
| `form.StartPosition` | `0`=Manual, `1`=Center (`StartPosition.Center`) |
| `form.DarkMode` | Enable/disable dark mode (`true`/`false`) |
| `form.Text` | Window title |
| `form.Handle` | Native window handle (HWND) |

#### Control Creation

All controls are created via Form methods and return a control handle for further configuration:

| Method | Control |
|--------|---------|
| `form.CreateLabel(text, x, y, w, h)` | Static label |
| `form.CreateTextBox(text, x, y, w, h)` | Text input field |
| `form.CreateButton(text, x, y, w, h)` | Clickable button |
| `form.CreateCheckBox(text, x, y, w, h, checked)` | Checkbox |
| `form.CreateRadioButton(text, x, y, w, h, checked)` | Radio button |
| `form.CreateListBox(x, y, w, h)` | List of selectable items |
| `form.CreateComboBox(x, y, w, h)` | Drop-down selector |
| `form.CreateTrackBar(x, y, w, h)` | Slider control |
| `form.CreateTimer(intervalMs)` | Timer (sends WM_TIMER) |

#### Control Methods

| Method | Works On |
|--------|----------|
| `.AddItem(text)` | ListBox, ComboBox |
| `.SelectedIndex` | ListBox, ComboBox (get/set) |
| `.Text` | Label, TextBox, Button, CheckBox, RadioButton (get/set) |
| `.Checked` | CheckBox, RadioButton (get/set) |
| `.Value` / `.Minimum` / `.Maximum` | TrackBar |
| `.Interval` | Timer |

Events are handled via Win32 window procedure callbacks, see `SetWindowHandler` in the WinApi namespace.

Full example: `assets/language/ui/forms/16_ui_two_forms_darkmode.slc`

#### Related Types

| Type | Description |
|------|-------------|
| `Form` | Window object (System.Windows.Forms.Form wrapper) |
| `Control` | Base control type |
| `Size` | Width/Height structure |
| `CancelEventArgs` | Event cancellation argument |
| `EventArgs` | Base event argument type |
| `StartPosition` | Form start position enum: `Manual`, `Center` |

### Namespaces &amp; Utility Types

#### WinApi (Native Interop)

The `WinApi` namespace provides direct Win32 API access for advanced interop scenarios.

| Method | Description |
|--------|-------------|
| `WinApi.SetWindowHandler(hwnd, fn)` | Replace a window's message handler with a script callback |
| `LoadLibrary(path)` / `WinApi.LoadLibrary(path)` | Load a DLL and return a <code>DynamicLibrary</code> object. Deduplicated, loading the same library twice returns the same instance. Methods like <code>lib.Sleep(1000)</code> auto-resolve function pointers (numeric args only). |
| `lib.Free()` / `FreeLibrary(lib)` | Unload a DLL. <code>FreeLibrary</code> also accepts raw <code>IntPtr</code> for backward compatibility. |
| `WinApi.GetFunction(handle, name)` | Get a function pointer from a loaded DLL |
| `WinApi.Alloc(size)` | Allocate unmanaged memory |
| `WinApi.Free(ptr)` | Free unmanaged memory |
| `WinApi.ReadInt32/64(ptr, offset)` | Read integer from memory |
| `WinApi.WriteInt32/64(ptr, offset, val)` | Write integer to memory |
| `WinApi.ReadPointer(ptr, offset)` | Read pointer from memory |
| `WinApi.WritePointer(ptr, offset, val)` | Write pointer to memory |
| `WinApi.ReadString(ptr)` | Read a Unicode string from memory |
| `WinApi.ToPointer(str)` | Allocate native memory and copy a string |
| `WinApi.FreePointer(ptr)` | Free a pointer allocated with `ToPointer` |
| `WinApi.GetFunctionPointer(fn, line, col)` | Get a native function pointer for a script callback |
| `WinApi.FreeFunctionPointer(fn)` | Release a function pointer |
| `WinApi.MessageBox(hwnd, text, caption, type)` | Show a Windows message box |
| `WinApi.ShowWindow(hwnd, cmd)` | Show or hide a window |
| `WinApi.GetModuleHandle(name)` | Get a module handle by name |
| `WinApi.CreateWindowEx(...)` | Create a native Win32 window |
| `WinApi.RegisterClassEx(...)` | Register a window class |
| `WinApi.DefWindowProc(...)` | Default window procedure |
| `WinApi.GetMessage, TranslateMessage, DispatchMessage` | Windows message loop functions |
| `WinApi.PostQuitMessage(exitCode)` | Post a quit message |
| `WinApi.SetWindowPos, SetWindowText, GetWindowText` | Window manipulation |
| `WinApi.GetDlgItem, SetFocus, EnableWindow` | Control interaction |
| `WinApi.SendMessage, PostMessage` | Send/post window messages |
| `WinApi.CreateMenu, AppendMenu, SetMenu, DrawMenuBar` | Menu handling |
| `WinApi.SetWindowLongPtr, GetWindowLongPtr` | Window data access |
| `WinApi.LoadCursor` | Load a cursor resource |
| `WinApi.GetStockObject` | Get a stock GDI object |
| `WinApi.GetModuleHandle` | Get module handle |
| `WinApi.WriteStruct, ReadStruct` | Structure marshaling |
| `WinApi.SizeOfStruct(typeName)` | Get structure size |

#### Colors

The `Colors` namespace provides named ARGB color constants:

```csharp
Colors.Red       // 0x000000FF
Colors.Green     // 0x0000FF00
Colors.Blue      // 0x00FF0000
Colors.White     // 0x00FFFFFF
Colors.Black     // 0x00000000
Colors.Yellow    // 0x0000FFFF
Colors.Cyan      // 0x00FFFF00
Colors.Magenta   // 0x00FF00FF
Colors.FromArgb(a, r, g, b)  // Create custom color
```

#### Rect

The `Rect` function creates a Win32 RECT structure:

```csharp
var r = Rect(10, 20, 100, 200);  // left, top, right, bottom
```

#### GC (Garbage Collection)

| Method | Description |
|--------|-------------|
| `GC.Collect()` | Force garbage collection |
| `GC.GetTotalMemory()` | Get current managed memory usage |
| `GC.GetTotalAllocated()` | Get total allocated bytes |
| `GC.MaxGeneration` | Maximum supported generation |

#### Json

| Method | Description |
|--------|-------------|
| `Json.Parse(string)` | Parse JSON string into a script dictionary |
| `Json.Serialize(value)` | Serialize a value to JSON string |

#### Http

| Method | Description |
|--------|-------------|
| `Http.Get(url)` | HTTP GET request (blocking) |
| `Http.Post(url, body)` | HTTP POST request |
| `Http.Put(url, body)` | HTTP PUT request |
| `Http.Delete(url)` | HTTP DELETE request |

Returns the response body as a string. In coroutines, these yield an `HttpRequest` for non-blocking dispatch.

#### MCI (Media Control Interface)

```csharp
MCI.Open("file.mp3")       // Open a media file
MCI.Play("file.mp3")       // Play
MCI.Pause("file.mp3")      // Pause
MCI.Close("file.mp3")      // Close
MCI.Status("file.mp3")     // Get status
MCI.Seek("file.mp3", pos)  // Seek to position
```

#### Com (COM Interop)

```csharp
Com.Create(progId)       // Create COM object by ProgID
Com.Get(obj, property)   // Get property value
Com.Set(obj, property, value)  // Set property value
Com.Call(obj, method, args...)  // Call COM method
Com.Release(obj)         // Release COM object
```

#### string

The built-in `string` type provides common string operations:

```csharp
var s = "hello";
s.Length            // String length
s.ToUpper()         // Uppercase
s.ToLower()         // Lowercase
s.Trim()            // Trim whitespace
s.Substring(start, length)  // Substring
s.StartsWith(prefix)        // Prefix check
s.EndsWith(suffix)          // Suffix check
s.Contains(substring)       // Contains check
s.IndexOf(substring)        // Find position
s.Replace(old, new)         // Replace
s.Split(separator)          // Split into array
```

#### StringBuilder

For efficient string concatenation:

```csharp
var sb = new StringBuilder();
sb.Append("hello");
sb.Append(" world");
print(sb.ToString());  // "hello world"
```

#### StringComparison

Enum for string comparison mode:

```csharp
StringComparison.Ordinal
StringComparison.OrdinalIgnoreCase
```

#### DialogResult

Enum for message box results:

```csharp
DialogResult.OK       // IDOK = 1
DialogResult.Cancel   // IDCANCEL = 2
DialogResult.Yes      // IDYES = 6
DialogResult.No       // IDNO = 7
```

#### TypeInfo

Namespace providing type metadata at runtime. Accessible via `TypeInfo.typeName`.

#### Ref Cells

`pause`, blocks until a key is pressed:
```csharp
pause;
```

`exitcode`, get/set the process exit code:
```csharp
exitcode = 0;
print(exitcode);
```

## Generics Support

ScriptLangCore supports generic type parameters on user-defined classes and functions, in addition to the built-in generic types (`List<T>`, `Dictionary<K,V>`).

### Generic Functions

```csharp
// Single type parameter
T Identity<T>(T x) { return x; }
var result = Identity(42);       // type inference → int
var result2 = Identity("hello"); // type inference → string

// Explicit type arguments
var result3 = Identity<long>(42); // force long

// Multiple type parameters
A Choose<A, B>(A a, B b) { return a; }
var chosen = Choose("alpha", 42);
```

**Type inference**: When calling a generic function without explicit type arguments, the interpreter infers concrete types from the call argument values. The first parameter that uses a given type parameter determines its concrete type.

**Explicit type arguments**: Syntax `FunctionName<Type1, Type2>(args)` is supported. The parser disambiguates `<` from the less-than operator by checking whether the token after the identifier looks like a type.

**Generic coroutines**: Coroutine functions with generic type parameters work via `Coroutine.Start()` with a wrapper:

```csharp
T CountTo<T>(T max) {      // generic coroutine
    var i = 1;
    while (i <= max) { yield Identity(i); i = i + 1; }
    return i;
}
// Must use a wrapper: calling CountTo(3) directly returns the final value, not a Coroutine
int Wrapper(int max) { return CountTo(max); }
var co = Coroutine.Start(Wrapper, 3);
print(co.Resume());  // 1
```

Calling any coroutine function directly (whether generic or not) executes it to completion and returns the final value. Use `Coroutine.Start()` to obtain a `Coroutine` object that supports `.Resume()`.

### Generic Classes

```csharp
// Single type parameter
class Box<T> {
    T Value;
    constructor(T v) { this.Value = v; }
    T Get() { return this.Value; }
}
var intBox = new Box<int>(42);
var strBox = new Box<string>("hello");

// Multiple type parameters
class Pair<A, B> {
    A First;
    B Second;
    constructor(A a, B b) { ... }
    A GetFirst() { return this.First; }
}
var p = new Pair<int, string>(1, "one");
```

### Implementation Details

- **AST storage**: `FuncDecl` and `ClassDeclStmt` have an `IReadOnlyList<string> TypeParameters` field (default empty).
- **Parser**: `ParseTypeParameters()` parses `<T1, T2, ...>` after class/function names. `SafeParseTypeEnd()` and `LookAheadIsType()` disambiguate generics from comparison operators.
- **TypeInfo**: `IsGenericParameter` detects type variables (`T`). `Substitute(Dictionary<string, TypeInfo>)` replaces type parameters with concrete types.
- **Instantiation caches**: `_genericClassTemplates`, `_genericFuncTemplates`, `_instantiatedClasses`, `_instantiatedFuncs` store templates and concrete versions. Cleared on `.reset`.
- **Class template registration**: `VisitClassDeclStmt` stores the bare AST in `_genericClassTemplates` for later instantiation, and also creates a synthetic `ClassInfo` holding only static fields/methods. This `ClassInfo` is registered in `_classes` and the scope, so `Tracker.Count` inside `class Tracker<T>` resolves correctly (the scope lookup finds a `ClassInfo` with `StaticFields` rather than a bare `ClassDeclStmt`).
- **Class instantiation**: `InstantiateGenericClass()` clones the `ClassDeclStmt`, substitutes types in fields, methods, and constructors, and registers the concrete class in `_classes`.
- **Function instantiation**: `InstantiateGenericFunction()` infers type arguments from argument values (or uses explicit type args). Creates a `ScriptFunction` from the cloned `FuncDecl` with substituted parameter types.

### Limitations

- Generic struct declarations are not yet supported
- Generic array types as function parameters (`T[]`) may not parse correctly
- 16 type parameters limit matches `AotInvoker.InvokeCdecl<T>` (which supports up to 16 native arguments)
- `Coroutine.Start` with a generic function directly is not yet supported (use a wrapper)
- Type constraints (`where T : IComparable`) are not implemented
- Generic nested types and generic events/delegates are not yet supported

## Class Features

### Access Modifiers

ScriptLangCore supports four access modifiers on classes, structs, fields, methods, and events:

| Modifier | Scope | Description |
|----------|-------|-------------|
| `public` | Everywhere | Accessible from any code |
| `private` | Same class only | Accessible only within the declaring class |
| `protected` | Class + subclasses | Accessible within the declaring class and any derived class |
| `internal` | Same module | Accessible from any code in the same script/module |

```csharp
class Parent {
    public int Pub;
    private int Priv;
    protected int Prot;
    internal int Int;
}
```

### Destructors

Classes can define a destructor using `~ClassName() { }` syntax. When a destructor is defined, the class automatically gets a `Dispose()` method that executes the destructor body. In inheritance chains, destructors run from most-derived to base class automatically.

```csharp
class Resource {
    public bool Closed;
    ~Resource() {
        this.Closed = true;    // cleanup code
    }
}

var r = new Resource();
r.Dispose();                   // calls destructor
// r.Closed == true
```

**Inheritance chaining**: When a derived class has a destructor, calling `Dispose()` runs ALL destructors in the hierarchy (derived first, then base):

```csharp
class Connection { ~Connection() { /* base cleanup */ } }
class DbConnection extends Connection { ~DbConnection() { /* derived cleanup */ } }

var db = new DbConnection();
db.Dispose();  // runs DbConnection destructor → Connection destructor
```

Calling `Dispose()` on a class without a destructor produces a runtime error.

### Static Members

Classes can have `static` fields and methods, shared across all instances:

```csharp
class Config {
    static string AppName;
    static string GetApp() { return Config.AppName; }
}

Config.AppName = "MyApp";
print(Config.GetApp());  // "MyApp"
```

Static fields are initialized at class load time (to default values or initializer expressions). Static methods cannot access `this` (no instance context).

### Cross-Module Inheritance

Classes from imported modules can be extended. Use `import` without an alias to bring the parent class into scope:

```csharp
// animal_module.slc
class Animal {
    public string Name;
    constructor(string n) { this.Name = n; }
}

// main.slc
import "animal_module.slc";
class Dog extends Animal {
    constructor(string n) { super(n); }
}
```

The parent class's fields, methods, and constructors are all accessible in the derived class.

## File I/O Support

ScriptLangCore provides three namespaces for file system operations: `File`, `Directory`, and `Path`.

### File

| Method | Signature | Description |
|--------|-----------|-------------|
| `ReadAllText` | `(path)` | Read text file → string |
| `WriteAllText` | `(path, content)` | Write text file |
| `AppendAllText` | `(path, content)` | Append text to file |
| `ReadAllLines` | `(path)` | Read text lines → list |
| `ReadAllBytes` | `(path)` | Read binary file → byte[] |
| `WriteAllBytes` | `(path, bytes)` | Write binary file |
| `Exists` | `(path)` | Check if file exists → bool |
| `Delete` | `(path)` | Delete file |
| `Copy` | `(source, dest[, overwrite])` | Copy file |
| `Move` | `(source, dest)` | Move/rename file |
| `GetSize` | `(path)` | File size in bytes → long |

```csharp
// Read and write text
File.WriteAllText("data.txt", "hello world");
var content = File.ReadAllText("data.txt");

// Append
File.AppendAllText("log.txt", "new entry\n");

// Read lines
var lines = File.ReadAllLines("data.txt");
lines[0];  // first line

// Binary
File.WriteAllBytes("out.bin", new byte[] { 0x00, 0xFF });
var bytes = File.ReadAllBytes("out.bin");

// File info
if (File.Exists("data.txt")) {
    var size = File.GetSize("data.txt");
    File.Delete("data.txt");
}
```

### Directory

| Method | Signature | Description |
|--------|-----------|-------------|
| `Exists` | `(path)` | Check if directory exists → bool |
| `CreateDirectory` | `(path)` | Create directory |
| `Delete` | `(path[, recursive])` | Delete directory |
| `GetFiles` | `(path[, pattern])` | List files → list of strings |
| `GetDirectories` | `(path)` | List subdirectories → list of strings |
| `GetCurrentDirectory` | `()` | Current working directory → string |

```csharp
Directory.CreateDirectory("output");
var files = Directory.GetFiles("output", "*.txt");
files[0];  // first matching file
Directory.Delete("output", true);  // recursive delete
```

### Path

| Method | Signature | Description |
|--------|-----------|-------------|
| `Combine` | `(parts...)` | Join path segments → string |
| `GetFileName` | `(path)` | Extract filename |
| `GetFileNameWithoutExtension` | `(path)` | Filename without extension |
| `GetDirectoryName` | `(path)` | Extract directory path |
| `GetExtension` | `(path)` | Get file extension (e.g. ".txt") |
| `ChangeExtension` | `(path, newExt)` | Change extension |
| `HasExtension` | `(path)` | Check if path has extension → bool |
| `GetFullPath` | `(path)` | Resolve to absolute path |

```csharp
var full = Path.Combine("folder", "sub", "file.txt");
// → "folder\\sub\\file.txt"

var name = Path.GetFileName("C:\\dir\\file.txt");
// → "file.txt"

var ext = Path.GetExtension("data.json");
// → ".json"
```

## Process Support

ScriptLangCore can launch and manage external processes via the `Process` namespace. Output, error, and exit callbacks run on background threads.

### API

```csharp
// Start a process
var proc = Process.Start(executable, arguments, onOutput?, onError?, onExited?);
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `executable` | `string` | Path to the executable (e.g. `"ffmpeg"`, `"cmd.exe"`) |
| `arguments` | `string` | Command-line arguments |
| `onOutput` | `(string) => void` or `null` | Called for each stdout line |
| `onError` | `(string) => void` or `null` | Called for each stderr line |
| `onExited` | `(int) => void` or `null` | Called with exit code when process exits |

### Return Value: ProcessHandle

```csharp
proc.Kill();                    // kill process and entire tree
proc.WaitForExit();             // block until done (returns exit code)
proc.WaitForExit(5000);         // block with timeout millseconds (returns bool)
proc.IsRunning;                 // true while process is alive
proc.ExitCode;                  // int (available after exit)
```

### Examples

**Fire-and-forget**, no callbacks, just run and wait:

```csharp
var p = Process.Start("cmd.exe", "/c echo hello");
p.WaitForExit();  // blocks
print("exit: " + p.ExitCode);
```

**With output and exit callbacks**:

```csharp
var lines = new List<object>();
var p = Process.Start("ffmpeg", "-i input.mp4 output.avi",
    (line) => lines.Add(line),    // onOutput
    (line) => print("[ERR] " + line),  // onError
    (code) => print("Exit: " + code)); // onExited
p.WaitForExit();
sleep(300);  // let async callbacks drain
print("Got " + Count(lines) + " lines of output");
```

**Kill a long-running process**:

```csharp
var p = Process.Start("cmd.exe", "/c timeout 30");
sleep(1000);
p.Kill();  // clean kill with entire process tree
print("Running: " + p.IsRunning);  // false
```

### Thread Safety

Callbacks run on thread-pool background threads, serialized per process handle. Keep callbacks simple, no `yield`, no UI access. Use a `List<object>` to capture output and examine it after `WaitForExit` + a short `sleep()` to drain async streams.

## JSON Support

ScriptLangCore provides JSON serialization and deserialization via the `Json` namespace, built on `System.Text.Json` with AOT-safe `Utf8JsonWriter`/`Utf8JsonReader` (no reflection).

### Serialization

`Json.Stringify(value[, pretty])` serializes any interpreter-compatible value to a JSON string. `Json.Serialize(value[, pretty])` is an alias.

```csharp
Json.Stringify(null)                    // "null"
Json.Stringify(42)                      // "42"
Json.Stringify("hello")                 // "\"hello\""
Json.Stringify(true)                    // "true"
Json.Stringify(3.14)                    // "3.14"

var d = new Dictionary<string, object>();
d["name"] = "Alice";
d["age"] = 30;
Json.Stringify(d);                      // {"name":"Alice","age":30}
Json.Stringify(d, true);                // pretty-printed with 2-space indent
```

**Supported types**: null, primitives (int, long, double, string, bool, etc.), `IScriptDictionary`/`ScriptBoxedDictionary`, `IScriptList`/`ScriptBoxedList`, `ClassInstance` (serialized as `{__class__, __fields__}`), `StructInstance` (serialized as `{__struct__, __fields__}`), arrays. Cyclic references are detected and replaced with `null`.

### Deserialization

`Json.Parse(string)` parses a JSON string into interpreter-compatible values.

```csharp
var obj = Json.Parse('{"name":"Alice","age":30,"active":true}');
obj["name"];   // "Alice"
obj["age"];    // 30
obj["active"]; // true

var arr = Json.Parse('[10, 20, 30]');
arr[0];        // 10 (ScriptBoxedList, supports indexing)
```

**Return types**: JSON objects → `ScriptBoxedDictionary` (supports `["key"]` indexing). JSON arrays → `ScriptBoxedList` (supports `[index]` indexing, `Count`, iteration). Nested objects/arrays are recursively converted.

### Validation

`Json.Validate(string)` checks if a string is valid JSON without allocating script objects:

```csharp
Json.Validate('{"a":1}');  // true
Json.Validate("not json"); // false
```

### Format / Minify

`Json.Format(jsonString [, indent])` pretty-prints an existing JSON string, with `indent` spaces a level (0 through 8, default 2). `Json.Minify(jsonString)` strips whitespace:

```csharp
var compact = '{"a":1,"b":2}';
Json.Format(compact);         // pretty-printed, two spaces a level
Json.Format(compact, 4);      // four spaces a level
Json.Minify(compact);         // same (already minified)

var pretty = '{\n  "a": 1\n}';
Json.Minify(pretty);          // {"a":1}
```

### File I/O

`Json.ToFile(path, value[, pretty])` serializes and writes to disk. `Json.FromFile(path)` reads and parses a JSON file using chunked streaming (`FileStream` + `JsonDocument.Parse(Stream)`, no `ReadAllText`):

```csharp
var data = new Dictionary<string, object>();
data["x"] = 1;
Json.ToFile("output.json", data);            // write
Json.ToFile("output.json", data, true);      // write pretty-printed

var loaded = Json.FromFile("output.json");   // read and parse
loaded["x"];  // 1
```

### Known Limitations

- `ClassInstance`/`StructInstance`: `Stringify` works, but `Parse` does not reconstruct class/struct instances (no class metadata in JSON).
- `ScriptUnboxedList<T>`: `Stringify` works; `Parse` returns `ScriptBoxedList` (boxed, not unboxed).
- No schema validation, `Parse` accepts any valid JSON.
- No `System.Text.Json` source generators (custom writer/reader is AOT-safe directly).

### Dot Command Filtering

When executing script files (both CLI mode and `.run` in REPL), lines starting with `.` are filtered out before parsing via `StripDotCommands()`. Recognized commands like `.time` and `.echo` are executed as side effects during filtering.
