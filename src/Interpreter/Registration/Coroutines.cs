namespace ScriptLangCore;

internal partial class Interpreter
{

    /// <summary>
    /// Registers coroutine-related methods in the scripting environment, enabling support for starting, sleeping,
    /// waiting, and pumping coroutines.
    /// </summary>
    /// <remarks>This method adds the 'Coroutine' namespace to the current script scope, exposing methods such
    /// as Start, Sleep, Wait, and Pump for coroutine management. These methods are intended to be used within scripts
    /// to facilitate asynchronous and cooperative multitasking patterns.</remarks>
    /// <exception cref="ScriptException">Thrown if a coroutine method is called with invalid arguments or from an invalid context, such as calling
    /// Coroutine.Sleep or Coroutine.Wait outside a coroutine, or attempting to start a non-coroutine function.</exception>
    private void RegisterCoroutines()
    {
        var coroutineNs = new Namespace(_comparer);

        // 1. Coroutine.Start(SomeFn) – auto‑start, zero arguments.
        // 2. Coroutine.Start(SomeFn, false) – manual mode, zero arguments.
        // 3. Coroutine.Start(SomeFn, 5) – auto‑start, one argument 5.
        // 4. Coroutine.Start(SomeFn, 5, false) – manual mode, one argument 5
        // Coroutine.Start(fn, args..., autoStart?)
        // --------------------------------------------------------------------
        // When to use auto‑start(false(default))
        //  - Scheduler‑based scripts(yield Coroutine.Sleep(ms))
        //  - Event‑driven scripts(yield Coroutine.Wait(evt))
        // When to use manual start (false)
        //  - When you want to call co.Resume() yourself and get each yielded value directly.
        coroutineNs.AddMethod("Start", args =>
        {
            var itp = Current;
            if (args.Length < 1)
                throw new ScriptException("Coroutine.Start expects a function as first argument.");

            ScriptFunction fn;
            if (args[0] is ScriptFunction sf)
            {
                fn = sf;
            }
            else if (args[0] is FuncDecl { TypeParameters.Count: > 0 } genericFunc)
            {
                // Generic function template, instantiate with inferred type arguments
                var genericArgs = new List<object?>();
                var autoStartFlag = args.Length > 1 && args[^1] is bool;
                var genericEnd = autoStartFlag ? args.Length - 2 : args.Length - 1;
                for (var gi = 1; gi <= genericEnd && gi < args.Length; gi++)
                    genericArgs.Add(args[gi]);
                var concrete = itp?.InstantiateGenericFunction(genericFunc, [.. genericArgs], 0, 0);
                fn = concrete ?? throw new ScriptException("Failed to instantiate generic function for coroutine.");
            }
            else
                throw new ScriptException("Coroutine.Start expects a function as first argument.");

            // Parse: [fn, arg1, arg2, ..., autoStart?]
            // Default autoStart = true (scheduler/event coroutines)
            var autoStart = true;
            const int startArgs = 1;           // first argument after fn
            var endArgs = args.Length - 1;

            // If the very last argument is a boolean, it's the autoStart flag
            if (args is [.., _, bool flag])
            {
                autoStart = flag;
                endArgs = args.Length - 2;   // exclude both fn and the flag
            }

            var coroutineArgs = startArgs > endArgs ? [] : args[startArgs..(endArgs + 1)];

            //Console.WriteLine($"[START] autoStart={autoStart}, argCount={args.Length}");
            // VM-flat drive (mirrors the direct-call generator path): a coroutine whose body
            // compiled to bytecode and is free of yield-request/resume-value producers runs its
            // body on the VM (ResumeCoroutine's VmBacked branch) instead of the tree, the
            // 09_stress_bench gap (~6.6x). Sleep/http/event/readline and mid-expression yields
            // stay tree via the IsVmBackedCoroutine / Compiled == null gates.
            var vmBacked = itp?.CanRunCoroutineOnVm(fn.Declaration) == true;
            var co = new Coroutine(itp!, fn,
                vmBacked ? itp!.NormalizeArgsForVm(fn.Declaration, coroutineArgs, 0, 0) : coroutineArgs);
            if (vmBacked) co.VmBacked = true;

            if (autoStart)
                co.Resume();

            return co;
        });

        // Coroutine.Sleep(milliseconds) – inside a coroutine: yield Coroutine.Sleep(500)
        coroutineNs.AddMethod("Sleep", args =>
        {
            var ms = Convert.ToInt32(args[0]);
            return new SleepRequest(ms);
        });

        // Coroutine.Wait(event) – inside a coroutine: yield Coroutine.Wait(myEvent)
        coroutineNs.AddMethod("Wait", args =>
        {
            if (args.Length < 1 || args[0] is not EventInfo evt)
                throw new ScriptException("Coroutine.Wait expects an event.");
            return new EventWaitRequest(evt);
        });

        // Coroutine.Pump() – wake up ready coroutines (called automatically by DoEvents)
        coroutineNs.AddMethod("Pump", _ =>
        {
            // THE RUNNING INTERPRETER'S PUMP, not the registering one: a namespace member captures the interpreter
            // that REGISTERED it, and in an EMBEDDED session that is not the interpreter the script runs on, so
            // `PumpCoroutines()` on `this` drained a queue the sleep request had never reached. Measured through the
            // launcher: the request was scheduled on one interpreter and every pump drained the other, so a
            // `yield Coroutine.Sleep(...)` never resumed (and `assets/language/misc/06_step6.slc`, which loops until
            // its coroutine completes, never exited and froze the batch runner on it). The same rule as every other
            // member that touches per-run state, and the one `Application.DoEvents` already followed.
            (Current ?? this).PumpCoroutines();
            return null;
        });

        coroutineNs.AddMethod("Fetch", args =>
        {
            if (args.Length < 1)
                throw new ScriptException("Coroutine.Fetch expects at least a URL.");

            var url = args[0] as string ?? throw new ScriptException("URL must be a string.");
            var method = args.Length > 1 ? (args[1] as string ?? "GET") : "GET";
            var body = args.Length > 2 ? (args[2] as string) : null;
            return new HttpRequest(method, url, body);
        });

        CurrentFrame.Scopes.Define("Coroutine", coroutineNs);
    }
}