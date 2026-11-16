# Scheduler, CRON Task Scheduler

The `Scheduler` namespace provides fire-and-forget, delayed, recurring, and chained task execution. All callbacks run synchronously on the calling thread, no background threads, no thread-safety issues.

## Methods

| Method | Returns | Description |
|---|---|---|
| `Scheduler.Add(name, cron, callback)` | `ScheduledTask` | Recurring task on a CRON expression |
| `Scheduler.AddOrUpdate(name, cron, callback)` | `ScheduledTask` | Add or replace a recurring task by name |
| `Scheduler.Enqueue(name, callback)` | `ScheduledTask` | Fire-and-forget (next `Tick()`) |
| `Scheduler.Schedule(name, callback, delayMs)` | `ScheduledTask` | Delayed one-shot (milliseconds) |
| `Scheduler.Schedule(name, callback, dateTime)` | `ScheduledTask` | Delayed one-shot (at a DateTime) |
| `Scheduler.ContinueWith(parentTask, callback[, childName])` | `void` | Runs callback after parentTask succeeds |
| `Scheduler.Remove(name)` | `bool` | Stops a task by name |
| `Scheduler.List()` | `ScheduledTask[]` | Snapshot of active tasks |
| `Scheduler.Tick()` | `int` | Fires due callbacks now |
| `Scheduler.WaitAll()` | `void` | Blocks until all tasks are done |
| `Scheduler.Clear()` | `void` | Removes all tasks |

## ScheduledTask properties

| Property | Type | Description |
|---|---|---|
| `.Name` | `string` | Unique identifier |
| `.CronExpression` | `string` | Original CRON string |
| `.NextOccurrence` | `DateTime?` | Next scheduled time |
| `.Status` | `TaskStatus` | `TaskStatus.Scheduled`, `Running`, `Stopped`, or `Completed` |
| `.IsRunning` | `bool` | `true` while Scheduled |

## ScheduledTask methods

| Method | Description |
|---|---|
| `.Stop()` | Cancels the task, prints a trace, removes from global list |

## Usage

```js
// Recurring
Scheduler.Add("DailyBackup", CronExpression.Parse("0 3 * * *"), () => {
    print("Backup running...");
});

// Fire-and-forget
Scheduler.Enqueue("Notify", () => print("Done!"));

// Delayed
Scheduler.Schedule("Reminder", () => print("2s elapsed"), 2000);

// Continuation
var job = Scheduler.AddOrUpdate("Job", CronExpression.Parse("* * * * *"), () => {});
Scheduler.ContinueWith(job, () => print("Job completed!"));

// Remove
Scheduler.Remove("DailyBackup");

// Drive callbacks
while (Scheduler.List().Length > 0) {
    Scheduler.Tick();
    sleep(1000);
}
```

## Auto-cleaning

Tasks are automatically removed from the global list when stopped or completed:
- `Stop()` → removed with status `"Stopped"`
- One-shot tasks (Enqueue, Schedule) → removed with status `"Completed"`
- CRON tasks with no more occurrences within 5 years → removed with status `"Completed"`

`Scheduler.List()` only returns currently active (`"Scheduled"`) tasks.
