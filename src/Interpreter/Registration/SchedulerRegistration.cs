namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>
    /// Wraps a ScriptFunction into an Action that passes the task name as the first argument.
    /// The script callback receives: (taskName) => { ... }
    /// </summary>
    private Action WrapNamedCallback(ScriptFunction callback, string taskName)
    {
        var capturedName = taskName; // C# closure, works correctly
        // THE RUNNING INTERPRETER, captured HERE where the callback is created: the returned Action runs on the
        // scheduler's own thread, where `Current` is null, so reaching `this` resolved against the interpreter that
        // registered the namespace, which in a SESSION is not the one the script runs on. That is the `Com.Run` rule,
        // and the same defect that made `Parallel.Range` bodies answer `Undefined variable` through the launcher.
        var self = Current ?? this;
        return () => self.InvokeScriptFunction(callback, [capturedName], 0, 0);
    }

    private void RegisterSchedulerFunctions()
    {
        var schedulerNs = new Namespace(_comparer);

        // Scheduler.Add(name, cron, callback[, tz]) -> ScheduledTask
        schedulerNs.AddMethod("Add", args =>
        {
            if (args.Length < 3 || args[0] is not string taskName || args[1] is not CronExpression cron || args[2] is not ScriptFunction callback)
                throw new ScriptException("Scheduler.Add expects: (name, CronExpression, callback[, timezone])");

            var exprStr = cron.Expression;
            var tz = args.Length > 3 ? args[3] as TimeZoneInfo : null;
            var task = new ScheduledTask(exprStr, cron, WrapNamedCallback(callback, taskName), taskName, tz);
            task.Register();
            //Console.WriteLine($"[Scheduler] Task '{taskName}' added.");
            return task;
        });

        // Scheduler.Enqueue(name, callback) -> ScheduledTask
        schedulerNs.AddMethod("Enqueue", args =>
        {
            if (args.Length < 2 || args[0] is not string taskName || args[1] is not ScriptFunction callback)
                throw new ScriptException("Scheduler.Enqueue expects: (name, callback)");
            return ScheduledTask.Enqueue(taskName, WrapNamedCallback(callback, taskName));
        });

        // Scheduler.Schedule(name, delayMs, callback) -> ScheduledTask
        // Scheduler.Schedule(name, dateTime, callback) -> ScheduledTask
        schedulerNs.AddMethod("Schedule", args =>
        {
            if (args.Length < 3 || args[0] is not string taskName || args[2] is not ScriptFunction callback)
                throw new ScriptException("Scheduler.Schedule expects: (name, delayMs|dateTime, callback)");

            return args[1] switch
            {
                long delayMs => ScheduledTask.Schedule(taskName, WrapNamedCallback(callback, taskName),
                    TimeSpan.FromMilliseconds(delayMs)),
                int delayMsInt => ScheduledTask.Schedule(taskName, WrapNamedCallback(callback, taskName),
                    TimeSpan.FromMilliseconds(delayMsInt)),
                double delayDouble => ScheduledTask.Schedule(taskName, WrapNamedCallback(callback, taskName),
                    TimeSpan.FromMilliseconds(delayDouble)),
                DateTime dt => ScheduledTask.ScheduleAt(taskName, WrapNamedCallback(callback, taskName), dt),
                _ => throw new ScriptException(
                    "Scheduler.Schedule: second argument must be a delay in ms (long) or a DateTime.")
            };
        });

        // Scheduler.AddOrUpdate(name, cron, callback[, tz]) -> ScheduledTask
        schedulerNs.AddMethod("AddOrUpdate", args =>
        {
            if (args.Length < 3 || args[0] is not string name || args[1] is not CronExpression cron || args[2] is not ScriptFunction callback)
                throw new ScriptException("Scheduler.AddOrUpdate expects: (name, CronExpression, callback[, timezone])");
            var tz = args.Length > 3 ? args[3] as TimeZoneInfo : null;
            return ScheduledTask.AddOrUpdate(name, cron, WrapNamedCallback(callback, name), tz);
        });

        // Scheduler.ContinueWith(parentTask, childName, callback) -> void
        schedulerNs.AddMethod("ContinueWith", args =>
        {
            if (args.Length < 3 || args[0] is not ScheduledTask parentTask || args[1] is not string childName || args[2] is not ScriptFunction callback)
                throw new ScriptException("Scheduler.ContinueWith expects: (ScheduledTask, childName, callback)");

            ScheduledTask.ContinueWith(parentTask, childName, WrapNamedCallback(callback, childName));
            return null;
        });

        // Scheduler.List() -> ScheduledTask[]
        schedulerNs.AddMethod("List", args => ScheduledTask.GetAll());

        // Scheduler.WaitAll() -> void, blocks until all tasks are done
        schedulerNs.AddMethod("WaitAll", args =>
        {
            ScheduledTask.WaitAll();
            return null;
        });

        // Scheduler.Tick() -> int, fires due callbacks, returns count
        schedulerNs.AddMethod("Tick", args => ScheduledTask.Tick());

        // Scheduler.Clear() -> void, removes all tasks
        schedulerNs.AddMethod("Clear", args =>
        {
            ScheduledTask.Clear();
            return null;
        });

        // Scheduler.Remove(name) -> bool, remove a task by name if not running
        schedulerNs.AddMethod("Remove", args =>
        {
            if (args.Length < 1 || args[0] is not string taskName)
                throw new ScriptException("Scheduler.Remove expects: (name)");
            return ScheduledTask.Remove(taskName);
        });

        CurrentFrame.Scopes.Define("Scheduler", schedulerNs);
    }
}
