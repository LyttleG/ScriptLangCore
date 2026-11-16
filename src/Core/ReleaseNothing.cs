namespace ScriptLangCore;

/// <summary>An `IDisposable` value that releases NOTHING. The engine gives several pure-data values an empty
/// `Dispose` so `using` and `obj is IDisposable` answer the same on every value (a tuple, a `KeyValuePair`, a
/// `CronExpression`, a LINQ result), and this marker is how the run-end registry tells those apart from a value that
/// owns a HANDLE.
///
/// IT IS NOT A PREFERENCE, it is what keeps that registry bounded. `ScriptTuple` is `IDisposable` and a loop that
/// builds a tuple makes one per iteration, so a registry that held every `IDisposable` a script declared would grow
/// with the loop, for values whose release is a no-op.
///
/// SO THE RULE RUNS THE OTHER WAY FOR A REAL HANDLE: a type whose `Dispose` closes a file, a process, a socket, a
/// window, a COM reference, a DLL or an unmanaged buffer MUST NOT carry this marker, because then the registry is
/// the only thing that releases it when a script forgets to (`File.OpenWriteBytes` with no `Close`, which is the case
/// the registry exists for). A new empty-body `Dispose` adds this marker with the type; a new handle does not.</summary>
internal interface IReleaseNothing : IDisposable
{
}
