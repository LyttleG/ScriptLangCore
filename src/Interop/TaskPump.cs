namespace ScriptLangCore;

/// <summary>The RUN QUEUE: work posted to the interpreter's own thread and drained at its next statement
/// boundary, plus the completion-driven hand-off for <see cref="Task.Run"/>.
///
/// WHAT THIS REPLACED, and why the shape changed: this used to be a dedicated thread that spun
/// <c>Thread.Sleep(1)</c> and whose <c>Enqueue</c> BLOCKED the calling script until that thread had executed
/// the item. Measured consequences: a script could not start work and continue (<c>Task.Run</c> was
/// synchronous from the caller's point of view), every work item was serialised on one thread, and an idle
/// process still woke up a thousand times a second. It is now a queue plus a signal: posting never blocks,
/// the pool runs the work, and <see cref="PumpPending"/> is what the run thread calls between statements
/// (the VM's LOC arm, the tree's loop, <c>Task.Wait</c>, <c>Application.DoEvents</c> and the message loop),
/// exactly as before, so per-thread script affinity is unchanged.</summary>
internal static class TaskPump
{
    private static readonly ConcurrentQueue<WorkItem> Queue = new();

    /// <summary>Signalled whenever the queue GAINS an item, so a waiter that is parked on a task can come back and
    /// drain it instead of waking on a timer. Manual reset, and cleared only once a drain leaves the queue empty:
    /// with several waiters one must not consume another's wake-up, and an item posted between the drain and the
    /// clear re-sets it, so the wake-up cannot be lost. Measured: without this, `ContinueWith(..).Wait()` parked
    /// for ever: the continuation sat in this queue and nothing signalled the waiter (the 20 ms cap that used to
    /// paper over it is gone).</summary>
    private static readonly EventWaitHandle Ready = new(false, EventResetMode.ManualReset);

    /// <summary>The queue's signal, for a wait that must come back when script work is posted to it.</summary>
    internal static WaitHandle ReadySignal => Ready;

    /// <summary>Posts work for the run thread and returns immediately.</summary>
    public static void Post(Action action)
    {
        Queue.Enqueue(new WorkItem(action, static _ => { }));
        Ready.Set();
    }

    /// <summary>The old blocking <c>Enqueue</c>, kept as a NON-BLOCKING post for its callers: a completion that
    /// must be observed at a statement boundary (a composite task settling) belongs on this queue, and nothing
    /// that posts is allowed to wait for it.</summary>
    public static void Enqueue(Func<object?> action, Action<Exception> onError)
    {
        Queue.Enqueue(new WorkItem(() => action(), onError));
        Ready.Set();
    }

    /// <summary>Wakes a parked waiter that posted through another queue and only needs the signal raised.</summary>
    internal static void SignalReady() => Ready.Set();

    /// <summary>Runs every posted item on the CALLING thread. Only the run thread may call this, which is why
    /// it is the same door the statement boundaries already used.</summary>
    public static void PumpPending()
    {
        while (Queue.TryDequeue(out var item))
        {
            try { item.Action(); }
            catch (Exception e)
            {
                try { item.OnError(e); }
                catch
                {
                    // an error handler must not take the pump down with it
                }
            }
        }
        // Only when a full pass found nothing left: an item that arrived during the pass keeps the signal up, so
        // the next waiter returns at once instead of parking on a queue that is not empty.
        if (Queue.IsEmpty) Ready.Reset();
    }

    private readonly record struct WorkItem(Action Action, Action<Exception> OnError);
}
