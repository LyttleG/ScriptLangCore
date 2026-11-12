namespace ScriptLangCore;

/// <summary>Status of a scheduled task.</summary>
public enum TaskStatus
{
    Scheduled,
    Running,
    Stopped,
    Failed,
    Completed
}

/// <summary>Type of scheduled task's firing schedule.</summary>
public enum CronType
{
    /// <summary>Standard CRON expression (5 or 6 fields).</summary>
    Cron,
    /// <summary>Fire-and-forget, runs on the next Tick() then self-removes.</summary>
    Queue,
    /// <summary>Fires once at a specific time or after a delay, then self-removes.</summary>
    OneShot
}

/// <summary>
/// Handle for a scheduled CRON task. Returned by <c>Scheduler.Add()</c>.
/// All tasks are driven by <see cref="WaitAll"/> on the main thread,
/// callbacks run synchronously, avoiding thread-safety issues.
/// Use <see cref="Stop"/> to cancel, access properties to inspect state.
/// </summary>
public sealed class ScheduledTask
{
    private static readonly List<ScheduledTask> AllTasks = [];
    private static int _activeCount;

    private bool _stopped;
    private readonly bool _isOneShot;
    private readonly CronExpression? _cron;
    private readonly Action _callback;
    private readonly List<ScheduledTask> _continuations = [];
    private readonly TimeZoneInfo? _timezone;

    /// <summary>The CRON expression this task runs on.</summary>
    public string CronExpression { get; }

    /// <summary>Optional human-readable name for identifying this task.</summary>
    public string Name { get; }

    /// <summary>The next scheduled occurrence (UTC), or null if none left.</summary>
    public DateTimeOffset? NextOccurrence { get; private set; }

    /// <summary>Current task status.</summary>
    public TaskStatus Status { get; private set; }

    /// <summary>Type of this task's firing schedule.</summary>
    public CronType CronType { get; private set; } = CronType.Cron;

    /// <summary>True while the task is scheduled and waiting for the next occurrence.</summary>
    public bool IsRunning => Status == TaskStatus.Scheduled;

    internal ScheduledTask(string expression, CronExpression cron, Action callback, string? name = null, TimeZoneInfo? timezone = null)
    {
        CronExpression = expression;
        Name = name ?? expression;
        _cron = cron;
        _callback = callback;
        _timezone = timezone;
        NextOccurrence = ComputeNextUtcOffset();
        Status = TaskStatus.Scheduled;
    }

    /// <summary>
    /// Creates a one-shot task (fire-and-forget or delayed).
    /// </summary>
    internal ScheduledTask(string name, DateTime fireAt, Action callback, CronType cronType = CronType.OneShot)
    {
        CronExpression = cronType == CronType.Cron ? "" : $"{cronType}";
        Name = name;
        _cron = null;
        _callback = callback;
        _isOneShot = cronType != CronType.Cron;
        _timezone = null;
        CronType = cronType;
        NextOccurrence = ToUtcOffset(fireAt);
        Status = TaskStatus.Scheduled;
    }

    private DateTimeOffset? ComputeNextUtcOffset()
    {
        if (_cron == null) return null;

        if (_timezone == null) return ToUtcOffset(_cron.GetNextOccurrence(DateTime.Now));
        var nowUtc = DateTimeOffset.UtcNow;
        var nowInUserTz = TimeZoneInfo.ConvertTime(nowUtc, _timezone);
        var nextLocal = _cron.GetNextOccurrence(nowInUserTz.DateTime);
        if (nextLocal == null) return null;
        var offset = _timezone.GetUtcOffset(nextLocal.Value);
        return new DateTimeOffset(nextLocal.Value, offset);

        // Default: server local time
    }

    internal void Register()
    {
        lock (AllTasks)
        {
            AllTasks.Add(this);
        }
        Interlocked.Increment(ref _activeCount);
    }

    /// <summary>
    /// Cancels this scheduled task. Prints a status trace.
    /// Safe to call from any thread. Removes the task from the global list.
    /// </summary>
    public void Stop()
    {
        CancelWithTrace("stopped");
    }

    /// <summary>
    /// Internal cancellation that prints a custom action word in the trace.
    /// Used by <see cref="Stop"/> and <see cref="Remove"/>.
    /// </summary>
    private void CancelWithTrace(string action)
    {
        if (Status == TaskStatus.Completed || Status == TaskStatus.Failed) return;
        _stopped = true;
        Status = TaskStatus.Stopped;
        Interlocked.Decrement(ref _activeCount);
        lock (AllTasks) { AllTasks.Remove(this); }
        //Console.WriteLine($"[Scheduler] Task '{Name}', {action}.");
    }

    /// <summary>
    /// Returns true if this task's next occurrence has arrived (or passed).
    /// Called by <see cref="WaitAll"/> on the main thread.
    /// </summary>
    internal bool ShouldFire()
    {
        if (Status != TaskStatus.Scheduled || _stopped) return false;
        if (NextOccurrence == null) return false;
        return DateTimeOffset.Now >= NextOccurrence.Value;
    }

    /// <summary>
    /// Fires the callback and advances to the next occurrence.
    /// Called by <see cref="WaitAll"/> on the main thread.
    /// </summary>
    internal void Fire()
    {
        Status = TaskStatus.Running;
        Exception? callbackError = null;
        try { _callback(); }
        catch (Exception ex) { callbackError = ex; /*Console.WriteLine($"[Scheduler] Error in task '{Name}': {ex.Message}");*/ }
        if (_stopped) return;

        if (_isOneShot)
        {
            Status = callbackError != null ? TaskStatus.Failed : TaskStatus.Completed;
            Interlocked.Decrement(ref _activeCount);
            lock (AllTasks) { AllTasks.Remove(this); }
            //Console.WriteLine($"[Scheduler] Task '{Name}', {(callbackError != null ? "failed" : "completed")}.");
            // Fire continuations only on success
            if (callbackError != null) return;
            FireContinuations();
            return;
        }

        // CRON task: stay Scheduled on error so script-level retry (can_retry) still works.
        // The script callback handles error recording & skip logic via ShouldSkipDueToError.
        Status = TaskStatus.Scheduled;

        // Fire continuations if the callback succeeded
        if (callbackError == null)
            FireContinuations();

        // Advance to next occurrence
        if (_cron == null) return;
        NextOccurrence = ComputeNextUtcOffset();
        if (NextOccurrence != null) return;
        Status = TaskStatus.Completed;
        Interlocked.Decrement(ref _activeCount);
        lock (AllTasks) { AllTasks.Remove(this); }
        //Console.WriteLine($"[Scheduler] Task '{Name}', completed (no more occurrences within 5 years).");
    }

    private void FireContinuations()
    {
        List<ScheduledTask> conts;
        lock (_continuations) { conts = [.. _continuations]; _continuations.Clear(); }
        foreach (var cont in conts)
        {
            cont.Register();
            // Fire immediately if the continuation is also due
            if (cont.ShouldFire()) cont.Fire();
        }
    }

    /// <summary>
    /// Removes all registered tasks and resets the active count.
    /// Call at script start to avoid stale tasks from previous REPL runs.
    /// </summary>
    public static void Clear()
    {
        lock (AllTasks)
        {
            AllTasks.Clear();
            _activeCount = 0;
        }
    }

    /// <summary>
    /// Stops all registered tasks, disposes them, and clears the list.
    /// More thorough than <see cref="Clear"/>, ensures every task is properly
    /// stopped before being discarded.
    /// </summary>
    public static void ResetAll()
    {
        ScheduledTask[] tasks;
        lock (AllTasks)
        {
            tasks = [.. AllTasks];
            AllTasks.Clear();
            _activeCount = 0;
        }
        foreach (var t in tasks)
        {
            t.Stop();
        }
    }

    /// <summary>
    /// Finds a scheduled task by its <see cref="Name"/> and stops it.
    /// Only stops tasks whose status is "Scheduled" (not currently running).
    /// Returns true if a task was found and stopped, false otherwise.
    /// </summary>
    public static bool Remove(string taskName)
    {
        ScheduledTask? found = null;
        lock (AllTasks)
        {
            foreach (var t in AllTasks.Where(t
                         => t.Name.Equals(taskName, StringComparison.OrdinalIgnoreCase)
                          && t.Status != TaskStatus.Completed
                          && t.Status != TaskStatus.Failed))
            {
                found = t;
                break;
            }
        }
        if (found == null) return false;
        found.CancelWithTrace("removed");
        return true;
    }

    /// <summary>
    /// Fire-and-forget: queues a callback to run on the next <see cref="Tick"/>.
    /// </summary>
    public static ScheduledTask Enqueue(string name, Action callback)
    {
        var task = new ScheduledTask(name, DateTime.Now, callback, CronType.Queue);
        task.Register();
        //Console.WriteLine($"[Scheduler] Task '{name}' enqueued.");
        return task;
    }

    /// <summary>
    /// Schedules a one-shot callback after a delay.
    /// </summary>
    public static ScheduledTask Schedule(string name, Action callback, TimeSpan delay)
    {
        var fireAt = DateTime.Now.Add(delay);
        return ScheduleAt(name, callback, fireAt);
    }

    /// <summary>
    /// Schedules a one-shot callback at a specific date/time.
    /// </summary>
    public static ScheduledTask ScheduleAt(string name, Action callback, DateTime fireAt)
    {
        if (fireAt <= DateTime.Now)
            fireAt = DateTime.Now.AddMilliseconds(1);
        var task = new ScheduledTask(name, fireAt, callback);
        task.Register();
        //Console.WriteLine($"[Scheduler] Task '{name}' scheduled at {task.NextOccurrence}.");
        return task;
    }

    /// <summary>
    /// Adds or updates a recurring task by name.
    /// If a task with this name already exists and is Scheduled, it is replaced.
    /// </summary>
    public static ScheduledTask AddOrUpdate(string name, CronExpression cron, Action callback, TimeZoneInfo? timezone = null)
    {
        // Remove existing task with the same name
        Remove(name);

        var exprStr = cron.Expression;
        var task = new ScheduledTask(exprStr, cron, callback, name, timezone);
        task.Register();
        //Console.WriteLine($"[Scheduler] Task '{name}' added (next: {task.NextOccurrence}).");
        return task;
    }

    /// <summary>
    /// Registers a continuation: <paramref name="childCallback"/> will fire after
    /// the <paramref name="parent"/> task completes successfully.
    /// </summary>
    public static void ContinueWith(ScheduledTask parent, string childName, Action childCallback)
    {
        if (parent == null) throw new ArgumentNullException(nameof(parent));

        var contName = string.IsNullOrEmpty(childName) ? $"{parent.Name}_cont" : childName;
        var cont = new ScheduledTask(contName, DateTimeOffset.MinValue.DateTime, childCallback);
        lock (parent._continuations)
        {
            parent._continuations.Add(cont);
        }
        //Console.WriteLine($"[Scheduler] Continuation '{contName}' registered after '{parent.Name}'.");
    }

    /// <summary>
    /// Returns a snapshot of all registered tasks.
    /// </summary>
    public static ScheduledTask[] GetAll()
    {
        lock (AllTasks)
        {
            var arr = new ScheduledTask[AllTasks.Count];
            for (var i = 0; i < AllTasks.Count; i++)
                arr[i] = AllTasks[i];
            return arr;
        }
    }

    /// <summary>
    /// Drives all scheduled tasks on the calling thread.
    /// Blocks until all tasks are stopped or completed.
    /// Callbacks run on THIS thread, no background threads involved.
    /// </summary>
    public static void WaitAll()
    {
        while (_activeCount > 0)
        {
            // Check each task
            ScheduledTask[] snapshot;
            lock (AllTasks)
            {
                snapshot = new ScheduledTask[AllTasks.Count];
                for (var i = 0; i < AllTasks.Count; i++)
                    snapshot[i] = AllTasks[i];
            }

            var anyFired = false;
            // Iterate in reverse addition order so newer tasks
            // (typically controller tasks) fire before older ones.
            for (var i = snapshot.Length - 1; i >= 0; i--)
            {
                if (!snapshot[i].ShouldFire()) continue;
                snapshot[i].Fire();
                anyFired = true;
            }

            if (_activeCount == 0) break;

            // If nothing fired, sleep a short while before checking again
            if (!anyFired)
            {
                // Sleep at most 1 second, or until the nearest occurrence
                var nearest = GetNearestDelayMs(snapshot);
                Thread.Sleep(nearest);
            }
        }
    }

    private static DateTimeOffset? ToUtcOffset(DateTime? dt)
    {
        if (dt == null) return null;
        // MinValue/MaxValue sentinels, keep them as-is with +00:00 offset
        if (dt.Value == DateTime.MinValue || dt.Value == DateTime.MaxValue)
            return new DateTimeOffset(dt.Value, TimeSpan.Zero);
        // Preserve the local time with the local timezone offset.
        var offset = TimeZoneInfo.Local.GetUtcOffset(dt.Value);
        return new DateTimeOffset(dt.Value, offset);
    }

    private static int GetNearestDelayMs(ScheduledTask[] tasks)
    {
        var nearest = 1000; // default: 1 second
        var now = DateTimeOffset.Now;
        foreach (var t in tasks)
        {
            if (t.Status != TaskStatus.Scheduled || t.NextOccurrence == null) continue;
            var ms = (int)(t.NextOccurrence.Value - now).TotalMilliseconds;
            if (ms < 1) ms = 1;
            if (ms < nearest) nearest = ms;
            if (nearest <= 10) return 10; // don't go below 10ms
        }
        return nearest;
    }

    /// <summary>
    /// Checks all scheduled tasks and fires any whose time has come.
    /// Returns the number of callbacks that were fired.
    /// Useful for custom wait loops where the main script needs to
    /// interleave task management (e.g. stopping tasks from outside callbacks).
    /// </summary>
    public static int Tick()
    {
        // Take a snapshot under lock, safe even if callbacks remove tasks
        ScheduledTask[] snapshot;
        lock (AllTasks)
        {
            snapshot = new ScheduledTask[AllTasks.Count];
            for (var i = 0; i < AllTasks.Count; i++)
                snapshot[i] = AllTasks[i];
        }

        var count = 0;
        for (var i = snapshot.Length - 1; i >= 0; i--)
        {
            if (!snapshot[i].ShouldFire()) continue;
            snapshot[i].Fire();
            count++;
        }
        return count;
    }
}
