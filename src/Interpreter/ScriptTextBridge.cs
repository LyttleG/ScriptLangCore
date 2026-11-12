using System.Text;

namespace ScriptLangCore;

/// <summary>
/// A SCRIPT CLASS CANNOT BE A CLR SUBTYPE, so `class Sink extends TextWriter` is served by a BRIDGE: one engine
/// class per CLR base the engine supports, created per instance, which forwards every abstract/virtual member to the
/// script object's own member of that name.
///
/// WHY A BRIDGE AND NOT A REAL SUBCLASS: a script class is a <see cref="ClassInstance"/> value in this engine, not a
/// CLR type, and NativeAOT has no `Reflection.Emit` to mint one. So the class keeps its identity (it is still a
/// ClassInstance, `typeof` still names it, its members still dispatch here) and gains a CLR FACE it can be handed
/// through: <see cref="FacadeFor"/> answers that face, and the two coercion funnels plus `is`/`as` ask it.
///
/// WHAT THE BASE NEEDS FROM THE SCRIPT, and what happens when it is absent, is decided per member rather than by a
/// declaration-time requirement, because the engine's own base implementations are correct fallbacks: an unhandled
/// `WriteLine(string)` runs `TextWriter`'s own (which writes the text through `Write`), an unhandled `Flush` does
/// nothing, and an unhandled `Read` answers end-of-input. A declared member therefore wins and an absent one behaves
/// exactly as the CLR base would, which is why no abstract member has to be declared.
///
/// THE POSITION a script error reports is the sink CLASS's declaration line: the bridge has no call site of its own
/// (the CLR calls it), and a number that names where the sink was declared is the honest one.
internal static class ScriptTextBridge
{
    /// <summary>The CLR bases a script class may extend, in ONE statement: the name a script writes is resolved to a
    /// CLR type first (so the spelling follows the engine's type index), and this set decides whether the engine can
    /// stand behind it.</summary>
    internal static Type? BridgedBase(Type? resolved) =>
        resolved == typeof(TextWriter) ? typeof(TextWriter)
        : resolved == typeof(TextReader) ? typeof(TextReader)
        : null;

    /// <summary>
    /// The CLR face of a script instance, or null when the instance has no bridged base or the target it is being
    /// handed to cannot take that face. CACHED ON THE INSTANCE, so `Console.SetOut(sink)` twice hands the CLR the
    /// same writer, which is what a caller comparing them expects.
    /// </summary>
    internal static object? FacadeFor(ClassInstance instance, Type? target)
    {
        if (target is null || target == typeof(object)) return null;   // `object` means "the value itself", never a face
        var baseType = instance.Class.ClrBase;
        if (baseType is null || !target.IsAssignableFrom(baseType)) return null;
        if (instance.ClrFacade is { } cached) return cached;
        // `Current` is the running interpreter (a thread static): the facade dispatches script members, so it needs
        // one, and a coercion that happens with no interpreter running cannot produce a usable sink.
        if (Interpreter.Current is not { } itp) return null;
        object? face = baseType == typeof(TextWriter) ? new ScriptTextWriter(instance, itp)
                     : baseType == typeof(TextReader) ? new ScriptTextReader(instance, itp)
                     : null;
        instance.ClrFacade = face;
        return face;
    }

    /// <summary>True when the instance can be handed over as this CLR type, which is `is`/`as` in one line.</summary>
    internal static bool Is(ClassInstance instance, Type? target) => FacadeFor(instance, target) is not null;

    /// <summary>The `Dispose` half both bridges owe: the CLR disposes the FACE, and the script object that owns it gets
    /// its own `Dispose()` when it declares one. ONE implementation, because the two bridges derive from different CLR
    /// bases and cannot share a class, and two copies of this body is exactly the drift tools/check-dup-bodies.sh exists to
    /// catch.</summary>
    internal static void DisposeScriptSide(bool disposing, ClassInstance instance, Interpreter interp)
    {
        if (!disposing || !interp.TryFindScriptMethod(instance.Class, "Dispose", 0)) return;
        interp.InvokeMethodValues(instance, "Dispose", [], instance.Class.LineNumber, 0);
    }
}

/// <summary>
/// A <see cref="TextWriter"/> whose every write is a member call on a script object. `Encoding` is the one member the
/// CLR demands of any writer (it describes nothing here: UTF-8 is what the engine's own streams use, and a sink that
/// reads the property gets that answer).
/// </summary>
internal sealed class ScriptTextWriter(ClassInstance instance, Interpreter interp) : TextWriter
{
    private readonly ClassInstance _instance = instance;
    private readonly Interpreter _interp = interp;

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (!Call("Write", [value])) base.Write(value);
    }

    public override void Write(string? value)
    {
        if (!Call("Write", [value ?? ""])) base.Write(value);
    }

    public override void WriteLine()
    {
        if (!Call("WriteLine", [])) base.WriteLine();
    }

    public override void WriteLine(string? value)
    {
        if (!Call("WriteLine", [value ?? ""])) base.WriteLine(value);
    }

    public override void Flush() => Call("Flush", []);

    protected override void Dispose(bool disposing)
    {
        ScriptTextBridge.DisposeScriptSide(disposing, _instance, _interp);   // the script side, once, for both bridges
        base.Dispose(disposing);
    }

    /// <summary>
    /// One member call, and the answer is "the script declared it". The EXISTENCE test is by name and arity on the
    /// class (and its parents), never by catching a refusal, so a genuine error INSIDE the member still surfaces as
    /// itself instead of being mistaken for "no such member".
    /// </summary>
    private bool Call(string name, object?[] args)
    {
        if (!_interp.TryFindScriptMethod(_instance.Class, name, args.Length)) return false;
        _interp.InvokeMethodValues(_instance, name, args, _instance.Class.LineNumber, 0);
        return true;
    }
}

/// <summary>
/// A <see cref="TextReader"/> whose every read is a member call on a script object. An undeclared member answers
/// what the CLR base would for an empty source: -1 (end of input) from `Read`/`Peek`, null from `ReadLine`.
/// </summary>
internal sealed class ScriptTextReader(ClassInstance instance, Interpreter interp) : TextReader
{
    private readonly ClassInstance _instance = instance;
    private readonly Interpreter _interp = interp;

    private bool _closed;

    public override int Peek() => _closed ? -1 : Call("Peek", []) is int p ? p : -1;

    public override int Read() => _closed ? -1 : Call("Read", []) is int r ? r : -1;

    public override string? ReadLine() => _closed ? null : Call("ReadLine", []) as string;

    protected override void Dispose(bool disposing)
    {
        ScriptTextBridge.DisposeScriptSide(disposing, _instance, _interp);
        _closed = true;                 // a DISPOSED reader answers end of input; it never hands out more data
        base.Dispose(disposing);
    }

    private object? Call(string name, object?[] args)
    {
        if (!_interp.TryFindScriptMethod(_instance.Class, name, args.Length)) return null;
        return _interp.InvokeMethodValues(_instance, name, args, _instance.Class.LineNumber, 0);
    }
}
