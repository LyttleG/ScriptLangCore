namespace ScriptLangCore;

/// <summary>The CHUNKED door on a request body: what `ctx.BodyChunks()` answers, one byte array at a time, so a body
/// bigger than memory (a multi-GB upload is the measured case) is written to disk chunk by chunk instead of becoming
/// one string or one `byte[]` first.
///
/// It is an `IAsyncEnumerable&lt;byte[]&gt;` rather than an `IEnumerable`, and that is this engine's own convention
/// rather than a fashion: `await foreach` is the spelling a script has for "elements arrive one step at a time", the
/// engine's drive already waits each `MoveNextAsync` through its task bridge, and it disposes the enumerator on every
/// exit. A C# async iterator would also work, but every step here is a synchronous `Read` on the request thread (the
/// platform's request stream has no useful asynchronous path on the engine's single-request server), so a hand-written
/// enumerator answers a COMPLETED `ValueTask` per chunk: no thread is ever handed away, no continuation is queued, and
/// the interpreter stays on the one thread it is affine to.
///
/// Three rules, each the reason this door exists beside `ctx.Body`, `ctx.BodyLines()` and `ctx.RequestBytes`:
/// * the body is read ONCE, so a call after any other door (or after a second call of this one) is REFUSED by name;
/// * `ctx.RequestAborted` is checked BETWEEN chunks and an aborted read THROWS, because a truncated upload must not
///   read as a complete file (the half-written file is the caller's to delete, and the sentence says how far it got);
/// * a body whose length the request DECLARED is compared at the end, so a client that went away without the abort
///   token firing is still caught rather than reported as a complete body.
///
/// The chunks are freshly allocated per step and exactly as long as what was read, so the caller may hold one without
/// the next read writing over it (a pooled buffer would make that a footgun, and 64 KB per chunk is not the cost that
/// matters beside the disk write).
///
/// Whoever created it owns it, and the writer disposes it on every exit; `DisposeAsync` touches nothing, because the
/// request stream belongs to the server.</summary>
internal sealed class RequestBodyChunks : IAsyncEnumerable<byte[]>
{
    /// <summary>A body the PROGRAM set (a middleware building a sub-request, or a suite): its UTF-8 bytes are the one
    /// chunk, which is what `ctx.RequestBytes` answers for the same body, and nothing is consumed.</summary>
    private readonly byte[]? _given;

    /// <summary>The request's own stream, or null when there is none (a GET, or a body the program set).</summary>
    private readonly HttpContext? _context;

    private readonly int _chunkSize;

    /// <summary>The token the caller handed to `GetAsyncEnumerator`, kept so every chunk's pull can ask it: a script
    /// passes `CancellationToken.None` (its `await foreach` declares no token), while a caller that passes one is
    /// asking for the read to stop where it is.</summary>
    private readonly CancellationToken _cancellation;

    /// <summary>A stream-backed sequence opens ONCE: a second `GetAsyncEnumerator` would read a half-consumed stream
    /// and look like a truncated body, so it is refused by name instead.</summary>
    private bool _opened;

    internal RequestBodyChunks(byte[] given, int chunkSize, CancellationToken cancellation)
    {
        _given = given;
        _chunkSize = chunkSize;
        _cancellation = cancellation;
    }

    internal RequestBodyChunks(HttpContext context, int chunkSize, CancellationToken cancellation)
    {
        _context = context;
        _chunkSize = chunkSize;
        _cancellation = cancellation;
    }

    public IAsyncEnumerator<byte[]> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        // BOTH tokens count: the one this method is given (a caller's own stop) and the one the sequence was built
        // with, so neither can be silently dropped by the other door.
        var cancellation = cancellationToken.CanBeCanceled ? cancellationToken : _cancellation;
        if (_given != null) return new ChunkEnumerator(_given, _chunkSize, cancellation);
        if (_opened)
            throw new ScriptException("ctx.BodyChunks() answers ONE sequence: this request body is a stream and cannot "
                + "be read twice, so iterate the sequence once (a second `await foreach` over it would see an "
                + "already-consumed stream).");
        _opened = true;
        return new ChunkEnumerator(_context!, _chunkSize, cancellation);
    }

    /// <summary>One pull per chunk. `MoveNextAsync` answers a COMPLETE `ValueTask`, and the read itself is the
    /// synchronous `Stream.Read` the platform's request stream offers: on this server a request is handled on its own
    /// thread, so parking the interpreter on an incomplete value task would only move the wait.</summary>
    private sealed class ChunkEnumerator : IAsyncEnumerator<byte[]>, IAsyncDisposable
    {
        private readonly byte[]? _given;
        private readonly HttpContext? _context;
        private readonly int _chunkSize;
        private readonly CancellationToken _cancellation;
        private long _total;
        private int _index;
        private bool _done;

        internal ChunkEnumerator(byte[] given, int chunkSize, CancellationToken cancellation)
        {
            _given = given;
            _chunkSize = chunkSize;
            _cancellation = cancellation;
        }

        internal ChunkEnumerator(HttpContext context, int chunkSize, CancellationToken cancellation)
        {
            _context = context;
            _chunkSize = chunkSize;
            _cancellation = cancellation;
        }

        public byte[] Current { get; private set; } = [];

        public ValueTask<bool> MoveNextAsync()
        {
            if (_done) return new ValueTask<bool>(false);

            // THE TOKEN `GetAsyncEnumerator` WAS GIVEN, asked between chunks. A script's `await foreach` passes
            // CancellationToken.None unless the loop says otherwise (`await foreach (var c in seq)` has no token in
            // this language), so the interesting case is a caller that DID pass one: a stop is honoured here rather
            // than only at the end, which is the cooperative rule the interface states. It is a ScriptException
            // because that is the sentence a script reads, and it says how far the body had got, exactly as the
            // abort's own refusal does.
            if (_cancellation.IsCancellationRequested)
                throw new ScriptException($"the chunked body read was cancelled after {_total:N0} byte(s) "
                    + $"({_index:N0} chunk(s)): the sequence was asked to stop, so what was written is part of the "
                    + "body and not a whole file.");

            // A BODY THE PROGRAM SET: one chunk, then the end.
            if (_given != null)
            {
                _done = true;
                if (_given.Length == 0) return new ValueTask<bool>(false);
                Current = _given;
                return new ValueTask<bool>(true);
            }

            var context = _context!;
            if (context.RequestAborted.IsCancellationRequested)
                throw new ScriptException($"the request was aborted after {_total:N0} byte(s) of the body: the client "
                    + "went away, so the body is INCOMPLETE and what was written so far must not be kept as a whole "
                    + "file.");

            var stream = context.RequestBodyStream;
            if (stream == null) { _done = true; return new ValueTask<bool>(false); }

            var buffer = new byte[_chunkSize];
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                // The transport's own failure (a reset connection mid-upload) reads as the body being incomplete,
                // with how far it got, rather than as a bare IO error after 900 MB.
                throw new ScriptException($"the request body could not be read after {_total:N0} byte(s): {ex.Message}");
            }
            if (read <= 0)
            {
                _done = true;
                VerifyComplete(context);
                return new ValueTask<bool>(false);
            }

            _total += read;
            _index++;
            Current = read == buffer.Length ? buffer : buffer[..read];
            return new ValueTask<bool>(true);
        }

        /// <summary>A body that DECLARED its length has to arrive whole. Without this check a client that vanished
        /// after the last chunk the server read would still be answered "complete", which is exactly the shape a
        /// resumed or retried upload makes.</summary>
        private void VerifyComplete(HttpContext context)
        {
            var declared = context.RequestBodyLength;
            if (declared < 0 || _total == declared) return;
            throw new ScriptException($"the request body ended after {_total:N0} of the {declared:N0} byte(s) it "
                + "declared, so the body is INCOMPLETE: the client stopped early, and what was read is not a whole "
                + $"file ({_index:N0} chunk(s)).");
        }

        public ValueTask DisposeAsync()
        {
            // Nothing to release: the request stream belongs to the server, which owns its lifetime and reuses it for
            // a kept-alive connection. Disposal exists because an async sequence must be disposable, and the script's
            // `await foreach` performs it on every exit.
            return ValueTask.CompletedTask;
        }
    }
}
