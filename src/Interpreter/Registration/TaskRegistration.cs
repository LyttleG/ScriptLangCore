namespace ScriptLangCore;

partial class Interpreter
{
    private void RegisterTaskFunctions()
    {
        var taskNs = new Namespace(_comparer);

        // Task.Run(fn) -> Task (runs callback on background thread, ThreadLocal<CallFrame> ensures isolation)
        // Task.Run(fn, cancellationToken) -> Task (with cancellation support)
        taskNs.AddMethod("Run", args =>
        {
            if (args.Length == 0 || args[0] is not ScriptFunction fn)
                throw new ScriptException("Task.Run expects a function argument.");

            // C# flavour: Task.Run(an ASYNC function) is the Func<Task<T>> overload, whose task the runtime
            // UNWRAPS. An async body suspends cooperatively on an interpreter's pump rather than on the task
            // thread, so it is started HERE and that Task IS the answer.
            //
            // The interpreter to start it on is the one RUNNING the script (`Current`), not the one that registered
            // this namespace: a namespace member captures its registering interpreter, so inside a nested
            // `RunScript` the two differ, and the body then suspended onto a pump nobody was waiting in. Measured
            // before this: `await Task.Run(anAsyncFn)` inside a body evaluated by RunScript hung, while the same
            // shape in a script file answered, and awaiting the same Task.Run OUTSIDE a body also answered (that
            // wait drives the interpreter the body was started on, so the two agreed by accident).
            if (fn.Declaration.IsAsync)
            {
                // A pre-cancelled token means the body never starts, and the task it would have produced is
                // cancelled rather than completed, which is C#'s rule for Task.Run(fn, token).
                if (args is [_, CancellationToken alreadyCancelled, ..] && alreadyCancelled.IsCancellationRequested)
                    return Task.CanceledTask(alreadyCancelled);
                return (Current ?? this).InvokeScriptFunction(fn, [], 0, 0);
            }

            // THE RUNNING INTERPRETER, CAPTURED ON THIS THREAD: the body runs on a POOL thread, where `Current` is null
            // (it is a thread-static), so asking it there would answer the registering interpreter instead, which in a
            // nested `RunScript` is the OUTER one. Measured: `Task.Run(fun () { g = g + 1; })` over a global of the file
            // answered "Undefined variable 'g'" from a nested run and worked from a direct one, because
            // `InvokeScriptFunction` links the readable globals of the interpreter it is called on. The ASYNC branch
            // below already did this (its own comment argues the case); the two work-function branches did not.
            var target = Current ?? this;
            if (args is not [_, CancellationToken token, ..])
            {
                var ran = Task.Run(() => RunBodyWithCancellation(target, fn, CancellationToken.None));
                ran.SetPumpDriver(target.PumpCoroutines);   // a work function returning a Task is unwrapped, which needs the pump
                ran.SetPumpWork(target.PumpWorkSignal, target.PumpNextDueMs);   // and a body that `sleep`s is due by the clock, not by a signal
                return ran;
            }

            return token.IsCancellationRequested
                ? Task.CompletedTask : Task.Run(() => RunBodyWithCancellation(target, fn, token));
        });

        // THE STACK A `Task.RunSta` THREAD IS CREATED WITH: the same 32 MB the shipped AOT binary gives its main thread
        // (`IlcDefaultStackSize`), because the engine's depth guard measures what is LEFT of the stack a script runs on.
        // A managed thread defaults to 1 MB, and inside that budget the guard refused the FIRST re-entry of a bundle entry
        // ("Maximum call depth exceeded (1), possible runaway recursion (native stack nearly exhausted)").
        const int staRunStackBytes = 32 * 1024 * 1024;

        // Task.RunSta(fn [, cancellationToken]) -> Task
        //
        // A WORK FUNCTION ON A DEDICATED STA THREAD, which is the only way a script that drives COM can run OFF the UI
        // thread: this engine's COM Automation requires an STA apartment, and a ThreadPool thread is MTA, so
        // `Com.CreateObject` answers "COM Automation requires an STA thread" there. Measured from a runner: a captured
        // `music_player.slc` stopped at its first `Com.CreateObject` (line 90) because a capture runs on a pool thread to
        // stay killable. C# has no `RunSta`, and this repo's own note prescribes exactly this shape (a dedicated `Thread`
        // with `SetApartmentState` and a join) for a path that needs its own apartment.
        taskNs.AddMethod("RunSta", args =>
        {
            if (args.Length == 0 || args[0] is not ScriptFunction fn)
                throw new ScriptException("Task.RunSta expects a function argument.");
            var target = Current ?? this;
            var token = args is [_, CancellationToken t, ..] ? t : CancellationToken.None;
            if (token.IsCancellationRequested) return Task.CompletedTask;
            var answer = new Task();
            // A GENEROUS STACK, because a script's own depth budget is measured from the stack it runs on: a managed
            // thread defaults to 1 MB, and inside that budget the engine's guard refused the FIRST re-entry of a bundle
            // entry ("Maximum call depth exceeded (1), possible runaway recursion (native stack nearly exhausted)"), so a
            // captured script that calls `Application.Run` could not run at all. 32 MB is what the shipped AOT binary
            // gives its main thread (`IlcDefaultStackSize`), so a captured run gets the budget a direct run has.
            var thread = new System.Threading.Thread(() =>
            {
                try { answer.SetResult(RunBodyWithCancellation(target, fn, token)); }
                catch (Exception ex) { answer.SetException(ex); }
            }, staRunStackBytes)
            {
                IsBackground = true,
                Name = "SLC STA run",
            };
            // Windows is where the engine's whole UI and COM surface lives; on anything else the apartment call is not
            // supported and the thread simply runs as it is.
            if (OperatingSystem.IsWindows()) { thread.SetApartmentState(ApartmentState.STA); }
            thread.Start();
            answer.SetPumpDriver(target.PumpCoroutines);
            answer.SetPumpWork(target.PumpWorkSignal, target.PumpNextDueMs);
            return answer;
        });

        // Runs a work-function body with its task's token installed as THAT RUN's cancellation, which is what makes the
        // token a KILL SIGNAL: the tree walker asks between statements, `sleep` between chunks, and the VM at a loop
        // back-edge, so `cts.Cancel()` from another thread stops the body wherever it is working, instead of only keeping
        // a pending token from starting it (which was all the token did before). The token is `[ThreadStatic]` on the
        // interpreter, so the body's own nested runs (a `RunScript`, a module) inherit it, work on another thread is
        // untouched, and it is restored on the way out so a later body on the same pool thread does not inherit a spent
        // token. A LOCAL function on purpose: it captures nothing but its parameters, and it needs no member of the
        // registration method's scope.
        object? RunBodyWithCancellation(Interpreter target, ScriptFunction body, CancellationToken token)
        {
            var previous = ActiveRunCancellation;
            ActiveRunCancellation = token;
            try
            {
                return target.InvokeScriptFunction(body, [], 0, 0);
            }
            finally
            {
                ActiveRunCancellation = previous;
            }
        }

        // Task.Delay(int ms) -> Task (blocks synchronously, returns completed task)
        // Task.Delay(ms [, token]) -> Task: the pump-native delay, which holds NO thread. It replaced `DelayAsync`, the
        // engine's own duplicate spelling for the same C# member. This one used to BLOCK (`t.Wait()` inside the
        // registration), which is the opposite of what `await Task.Delay(300)` means, so a C# reader silently occupied a
        // thread for the whole delay. Retired 2026-10-10 by the maintainer's call.
        taskNs.AddMethod("Delay", args =>
        {
            var ms = args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
            var token = args.Length > 1 && args[1] is CancellationToken ct ? ct : default;
            return Task.DelayAsync(ms, token);
        });

        // Task.Yield() -> Task: C#'s yield point, and the ONE member here whose whole purpose is a suspension of no
        // duration. The task is incomplete when it is read and completes on a later pass of the run queue, so an
        // `await` on it really hands control back: whatever the run thread already has queued is served first, and a
        // loop that yields keeps the run responsive instead of holding it statement to statement. Registered through
        // the ASYNC DOOR because that is what this member is: it answers a Task at once and the WAIT is the caller's
        // `await`, exactly as `Task.WhenAll` is written. C#'s `Task.Yield()` returns a `YieldAwaitable` rather than a
        // Task, so the shape a script writes is identical while the type it reads is this engine's Task (a `.Wait()`
        // on it is legal here and would be a compile error there).
        taskNs.AddMethodAsync("Yield", _ => Task.Yield());

        // Task.WhenAll(list) -> Task (supports ScriptList and List<Task>)
        //
        // REGISTERED THROUGH THE ASYNC DOOR, AND THE CALL DOES NOT WAIT: C#'s composite returns at once, complete or
        // not, and the caller `await`s it (or `.Wait()`s it) — the arrow is where the waiting belongs. The reference
        // said "blocks until all tasks complete", and while `Task.Run` blocked its caller that made a bare
        // `Task.WhenAll(tasks);` look like a barrier; since `Task.Run` returns immediately (C#'s shape, correct) the
        // next statement read a list no body had touched yet. Measured: `crash_parallel`'s "20 concurrent adds" read
        // `list.Count == 0`. So the CONTRACT moved to C#'s, in the docs and in that suite, rather than this member
        // parking a thread to preserve a shape C# does not have.
        taskNs.AddMethodAsync("WhenAll", args => Task.WhenAll(TaskList(args, "Task.WhenAll")));

        // Task.WhenAny(list) -> Task: completes with the ENGINE Task that finished first (C# answers Task<Task>).
        taskNs.AddMethodAsync("WhenAny", args => Task.WhenAny(TaskList(args, "Task.WhenAny")));

        // ONE reader for the three accepted shapes, so the two composite members cannot disagree about what a list
        // of tasks is. NO WAIT happens here: C#'s composites RETURN the composite, complete or not, and the caller
        // awaits or waits it (measured: a wait inside WhenAll raised a fault at the CALL instead of at the caller's
        // await).
        static List<Task> TaskList(object?[] args, string member)
        {
            if (args.Length == 0 || args[0] is null)
                throw new ScriptException($"{member} expects a list of Tasks.");
            if (args[0] is List<Task> typed) return typed;
            var list = new List<Task>();
            if (args[0] is object[] array)
            {
                foreach (var item in array)
                    list.Add(item as Task ?? throw new ScriptException($"{member}: all items must be Task objects."));
                return list;
            }
            if (args[0] is IScriptList scriptList)
            {
                for (var i = 0; i < scriptList.Count; i++)
                    list.Add(scriptList[i] as Task ?? throw new ScriptException($"{member}: all items must be Task objects."));
                return list;
            }
            throw new ScriptException($"{member} expects a list of Tasks.");
        }

        // Task.DelayAsync(int ms) -> Task (NON-blocking: the engine's own `async` method awaits the runtime's
        // Task.Delay, so nothing occupies a thread while it waits). `await` it inside an async function.
        // `Task.DelayAsync` is RETIRED: `Task.Delay` is the same delay with C#'s spelling, so the engine no longer
        // carries a second name for a standard member.

        // Task.CompletedTask -> a Task that is already complete. A DYNAMIC property on purpose: the engine's own
        // getter builds a fresh completed task per read, so binding one instance would hand the same Task to every
        // reader of the name.
        taskNs.AddDynamicProperty("CompletedTask", () => Task.CompletedTask);

        // Task.FromResult(value) -> a Task that is ALREADY complete and carries that value: C#'s factory for the
        // synchronous path, and the pair of CompletedTask, which carries nothing. `Task<int> A() => Task.FromResult(42);`
        // is then a legal forwarder here, awaited or read through `.Result`.
        taskNs.AddMethod("FromResult", args =>
        {
            var completed = new Task();
            completed.SetResult(args.Length > 0 ? args[0] : null);
            return completed;
        });

        // Task.WhenAny(list) -> Task (supports ScriptList and List<Task>)
        // Task.Pump() -> void
        // `Task.Pump` is RETIRED too: it drove the pump by hand for the pre-`await` layer, and `await` plus the
        // top-level entry drive it themselves now. Nothing in C# has such a member.

        CurrentFrame.Scopes.Define("Task", taskNs);
    }
}
