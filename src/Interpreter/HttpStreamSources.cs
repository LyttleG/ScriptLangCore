using System.Collections;
using System.Reflection;

namespace ScriptLangCore;

/// <summary>The SOURCE half of a streaming answer: how a script value becomes a pull of elements. Kept in the
/// interpreter rather than in the transport because every kind it knows is an engine kind (`Coroutine`, a script
/// list, an `Events` feed) and two of the four need engine machinery to step: a generator is RESUMED, and an async
/// sequence's step is WAITED through the engine's own task bridge, the same wait a top-level `await` performs.
///
/// The transport therefore only ever sees `IHttpStreamSource`: one `MoveNext` and a `Dispose`. That is what lets
/// `McpServer` stay plain synchronous code on the request thread, which matters because the interpreter is
/// thread-affine: the producer is stepped on the very thread that ran the handler.</summary>
internal partial class Interpreter
{
    /// <summary>The streaming result for `http.StreamLines` / `http.StreamEvents`: the framing the member names, the
    /// source adapted from whatever the script passed, and an optional stated content type (only the JSON-lines
    /// spelling takes one: an event stream's type is fixed by the specification, and letting a script mislabel it
    /// would break the very client it exists for).</summary>
    private HttpResult StreamResult(object?[] args, HttpStreamFormat format, string member)
    {
        if (args.Length < 1)
            throw new ScriptException($"{member} expects the sequence to stream as its first argument: a list, a "
                + "generator, an async sequence (File.ReadLinesAsync, an Events feed) or nothing at all is refused.");
        var contentType = format == HttpStreamFormat.JsonLines && args.Length > 1 && args[1] is string stated
            && stated.Length > 0
            ? stated
            : null;
        return new HttpResult(200, contentType, null, null, null,
            new HttpStream(format, StreamSourceFor(args[0], member), contentType));
    }

    /// <summary>Adapts the four source kinds a script actually has into one pull. The order is the one that keeps a
    /// type from being read as a weaker one: an async sequence first (a feed is not an `IEnumerable`), then a
    /// `Coroutine` (whose step is a RESUME, not a `MoveNext`), then any enumerator, then any enumerable.
    ///
    /// A STRING is refused by name rather than streamed: it is a sequence of characters, so the obvious
    /// implementation would emit one element per character, which is never what an author meant.</summary>
    private IHttpStreamSource StreamSourceFor(object? source, string member)
    {
        switch (source)
        {
            case null:
                throw new ScriptException($"{member}: there is nothing to stream (the source is null).");
            case string:
                throw new ScriptException($"{member} streams a SEQUENCE of elements, and a string is one value: build "
                    + "a list of lines first, or answer the text as a body.");
            case Coroutine generator:
                return new CoroutineStreamSource(generator, member);
            case IEnumerator enumerator:
                return new EnumeratorStreamSource(enumerator);
            case IEnumerable enumerable:
                return new EnumeratorStreamSource(enumerable.GetEnumerator());
        }
        if (source is EventSubject runThreadFeed && runThreadFeed.Mode != "inline")
        {
            // THE FEED IS CLOSED ON THE WAY OUT, because a refusal must not leave a thread publishing into nothing: the
            // script's own stop is `while (!feed.Closed)`, so a producer it started would otherwise run for ever (measured
            // before this line: a counter advanced 1, 9, 18 after the 500). This is the same close the writer performs when
            // a stream ends, so a refused feed ends the same way an answered one does.
            runThreadFeed.CloseForStream();
            throw new ScriptException($"{member}: a feed in runThread mode cannot be a stream source. Its values are "
                + "delivered on the thread the RUN began on, and the writer holds that thread for the whole answer, so "
                + "they are lost and the server stops answering afterwards (measured: one of three values arrived and "
                + "every later request hung). Create the feed with Events.Create(\"inline\"), whose deliveries ride the "
                + "publishing thread, or publish from a thread of its own and subscribe in the handler. The feed has been "
                + "closed, so a producer you started on it stops at its `while (!feed.Closed)` guard.");
        }
        if (TryOpenAsyncEnumerator(source, out var asyncEnumerator, out var asyncInterface, 0, 0))
            return new AsyncStreamSource(asyncEnumerator, asyncInterface, source as EventSubject);
        throw new ScriptException($"{member}: {GetFriendlyTypeNameOfValue(source)} is not something to stream. It "
            + "takes a list, a generator, or an async sequence such as File.ReadLinesAsync(...) or an Events feed.");
    }

    /// <summary>A script GENERATOR (or any engine `Coroutine`) streamed one element per step: resumed, and its last
    /// yielded value is the element. A coroutine that suspends on a REQUEST (a sleep, an HTTP call, an event) is
    /// REFUSED by name rather than having the request object handed out as if it were a value, which is what the
    /// engine's own `foreach` avoids by pumping and what a stream cannot do while it owns the request thread.</summary>
    private sealed class CoroutineStreamSource(Coroutine generator, string member) : IHttpStreamSource
    {
        private bool _stepped;

        public bool MoveNext(out object? item)
        {
            item = null;
            if (!_stepped)
            {
                _stepped = true;
                if (generator.Status == CoroutineStatus.NotStarted) generator.Resume();
            }
            else generator.Resume();

            if (generator.Status == CoroutineStatus.Waiting)
                throw new ScriptException($"{member}: the generator suspended on a REQUEST rather than yielding a "
                    + "value (a sleep, an HTTP call or an event), which a stream cannot deliver: yield the values "
                    + "themselves and let the client decide how often it reads.");
            if (!generator.IsResumable) return false;
            item = generator.LastYieldedValue;
            return true;
        }

        public void Dispose() => generator.Abort();
    }

    /// <summary>Any enumerator streamed one element per step, which covers a script list, an array, a dictionary's
    /// entries and the engine's `Generator<T>` wrapper. Disposed on the way out when it is disposable, because a
    /// `yield` iterator's body may be holding a file.</summary>
    private sealed class EnumeratorStreamSource(IEnumerator enumerator) : IHttpStreamSource
    {
        public bool MoveNext(out object? item)
        {
            if (!enumerator.MoveNext()) { item = null; return false; }
            item = enumerator.Current;
            return true;
        }

        public void Dispose() => (enumerator as IDisposable)?.Dispose();
    }

    /// <summary>An async sequence streamed one element per step: `MoveNextAsync` is invoked THROUGH THE INTERFACE
    /// (a compiler-generated iterator implements it explicitly, so a lookup by name finds nothing) and its
    /// `ValueTask&lt;bool&gt;` is waited through the engine's own bridge, with a COMPILED cast because the reflected
    /// route is a stub on the AOT publish. The enumerator is disposed with the async rule, which is what releases an
    /// async file reader's handle.
    ///
    /// A feed is ALSO CLOSED here (`CloseForStream`), and that half is what stops a producer: a script's long-lived
    /// feed guards itself with `while (!feed.Closed)`, so a client that goes away mid-stream ends the producer
    /// instead of leaving a thread publishing into a queue nothing reads.</summary>
    private sealed class AsyncStreamSource(object enumerator, Type enumeratorInterface, EventSubject? feed)
        : IHttpStreamSource
    {
        private readonly MethodInfo _moveNext = enumeratorInterface.GetMethod("MoveNextAsync")!;
        private readonly PropertyInfo _current = enumeratorInterface.GetProperty("Current")!;

        public bool MoveNext(out object? item)
        {
            item = null;
            var step = InvokeReflected(_moveNext, enumerator, null, 0, 0, "MoveNextAsync");
            if (step is not ValueTask<bool> valueTask) return false;
            var scriptTask = ToScriptTask(valueTask.AsTask());
            // PARKED, on the SERVER thread: `IHttpStreamSource.MoveNext` is a synchronous pull by contract (the
            // transport stays plain synchronous code), so the step waits here and is counted under its own site. The
            // run's kill signal does not travel to this thread, so a cancel is not what ends a stream: the writer's
            // own request token is.
            WaitForTask(scriptTask, "http stream", 0, 0);
            if (scriptTask.Result is not bool hasNext || !hasNext) return false;
            item = ReadReflected(_current, enumerator, 0, 0, "Current");
            return true;
        }

        public void Dispose()
        {
            DisposeAsyncResource(enumerator, 0, 0);
            feed?.CloseForStream();
        }
    }
}
