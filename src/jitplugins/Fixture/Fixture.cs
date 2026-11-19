// THE PLUGIN SIDE of the contract, written as a third party would: no reference to the engine, its own copy of the
// table layout, its own runtimeconfig.json, and nothing that throws across an entry point.
using System.Runtime.InteropServices;
using System.Text;

namespace JitPluginFixture;

/// <summary>The table, restated here on purpose: the layout IS the contract, and a plugin that shared the host's C#
/// type would be exercising a shared type rather than an ABI.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct Exports
{
    public uint Abi;
    public uint Size;
    public delegate* unmanaged<byte*, int, int> Ping;
    public delegate* unmanaged<byte*, byte*, int, int> Invoke;
}

public static unsafe class Entry
{
    public const uint ContractAbi = 1;
    const string Identity = "JitPluginFixture abi 1";

    /// <summary>Public, static, [UnmanagedCallersOnly]: the three things
    /// `load_assembly_and_get_function_pointer(UNMANAGEDCALLERSONLY_METHOD)` requires.</summary>
    [UnmanagedCallersOnly]
    public static int PluginInit(Exports* table)
    {
        table->Abi = ContractAbi;
        table->Size = (uint)sizeof(Exports);
        table->Ping = &Ping;
        table->Invoke = &Invoke;
        return 0;
    }

    [UnmanagedCallersOnly]
    static int Ping(byte* answer, int capacity) => Write(Identity, answer, capacity);

    /// <summary>The door. ONE catch-all, because rule 3 of the contract says nothing throws across: a plugin failure
    /// is an envelope with a code, never an exception the host cannot survive.</summary>
    [UnmanagedCallersOnly]
    static int Invoke(byte* request, byte* answer, int capacity)
    {
        string reply;
        try
        {
            reply = Answer(Marshal.PtrToStringUTF8((nint)request) ?? "");
        }
        catch (Exception ex)
        {
            reply = Failure("PluginFailed", ex.GetType().Name);
        }
        return Write(reply, answer, capacity);
    }

    static string Answer(string request)
    {
        using var document = System.Text.Json.JsonDocument.Parse(request);
        var root = document.RootElement;
        string verb = root.TryGetProperty("m", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() ?? "" : "";
        var parameters = root.TryGetProperty("p", out var p) ? p : default;
        int a = Number(parameters, "a");
        int b = Number(parameters, "b");
        return verb switch
        {
            "add" => Ok(a + b),
            "echo" => Ok($"{verb}:{a}/{b}"),
            "long" => Ok(new string('x', Number(parameters, "n"))),
            "utf8" => Ok("caf\u00e9 et \U0001F44B"),
            "throw" => throw new InvalidOperationException("the fixture threw on purpose"),
            "bad" => "this is not an envelope",
            "liar" => "{\"v\":1,\"ok\":true,\"e\":{\"code\":\"Contradiction\",\"message\":\"both a verdict and an error\"}}",
            "oldversion" => "{\"v\":99,\"ok\":true,\"r\":1}",
            _ => Failure("UnknownVerb", $"this plugin serves add, echo, long, utf8, throw, bad, liar and oldversion, not '{verb}'"),
        };
    }

    static int Number(System.Text.Json.JsonElement parameters, string member)
        => parameters.ValueKind == System.Text.Json.JsonValueKind.Object
           && parameters.TryGetProperty(member, out var value)
           && value.ValueKind == System.Text.Json.JsonValueKind.Number
           && value.TryGetInt32(out var n) ? n : 0;

    static string Ok(object result) => $"{{\"v\":1,\"ok\":true,\"r\":{System.Text.Json.JsonSerializer.Serialize(result)}}}";

    static string Failure(string code, string message)
        => $"{{\"v\":1,\"ok\":false,\"e\":{{\"code\":{System.Text.Json.JsonSerializer.Serialize(code)},\"message\":{System.Text.Json.JsonSerializer.Serialize(message)}}}}}";

    /// <summary>The `snprintf` convention: the length needed, the bytes written, and the same length when the caller
    /// probed with `capacity` 0 or with no buffer at all.</summary>
    static int Write(string text, byte* answer, int capacity)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);
        if (answer is null || capacity < utf8.Length) return utf8.Length;
        for (int i = 0; i < utf8.Length; i++) answer[i] = utf8[i];
        return utf8.Length;
    }
}
