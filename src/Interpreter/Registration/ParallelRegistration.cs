namespace ScriptLangCore;

partial class Interpreter
{
    private void RegisterParallelFunctions()
    {
        var pNs = new Namespace(_comparer);

        // Parallel.Range(start, end, [step, ] body), uses BCL Parallel.For (real parallelism)
        pNs.AddMethod("Range", args =>
        {
            var start = Convert.ToInt64(args[0]);
            var end = Convert.ToInt64(args[1]);
            ScriptFunction body;
            long step = 1;

            if (args is [_, _, ScriptFunction fn3, _, ..])
            {
                body = fn3;
            }
            else if (args.Length >= 4)
            {
                step = Convert.ToInt64(args[2]);
                body = args[3] as ScriptFunction ?? throw new ScriptException("Parallel.Range expects a function as the last argument.");
            }
            else
            {
                body = args[2] as ScriptFunction ?? throw new ScriptException("Parallel.Range expects a function as the last argument.");
            }

            // THE RUNNING INTERPRETER, CAPTURED HERE (the calling thread) rather than reached through `this` inside the
            // BCL callback. A namespace object is registered on the interpreter that built it, and in a SESSION that can
            // be a different instance from the one the script is running on (the pool's blueprint), so the callback's
            // body resolved `syncObj` against the wrong globals and answered `Undefined variable` from every chunk
            // (reported from a launcher run of crash_parallel.slc, where Task.Run was fine and Parallel.Range was not).
            // This is the `Com.Run` rule: capture `Current ?? this` where the callback is CREATED.
            var self = Current ?? this;

            if (step == 1 && start >= int.MinValue && end <= int.MaxValue)
                Parallel.For((int)start, (int)end, i => self.InvokeScriptFunction(body, [i], 0, 0));
            else
            {
                var items = new List<long>();
                for (var i = start; i < end; i += step) items.Add(i);
                Parallel.ForEach(items, item => self.InvokeScriptFunction(body, [item], 0, 0));
            }
            return null;
        });

        // Parallel.Each(list, body), uses BCL Parallel.ForEach (real parallelism)
        pNs.AddMethod("Each", args =>
        {
            if (args.Length < 2 || args[1] is not ScriptFunction body)
                throw new ScriptException("Parallel.Each expects (list, function).");

            object[] items;
            if (args[0] is object[] arr)
                items = arr;
            else if (args[0] is Array sysArr)
            {
                items = new object[sysArr.Length];
                for (var i = 0; i < sysArr.Length; i++) items[i] = sysArr.GetValue(i)!;
            }
            else if (args[0] is IScriptList sl)
            {
                items = new object[sl.Count];
                for (var i = 0; i < sl.Count; i++)
                    items[i] = sl[i]!;
            }
            else
                throw new ScriptException("Parallel.Each expects a list or array.");

            var selfEach = Current ?? this;   // the `Com.Run` rule: capture where the callback is CREATED
            Parallel.ForEach(items, item => selfEach.InvokeScriptFunction(body, [item], 0, 0));
            return null;
        });

        // Parallel.Invoke(fn1, fn2, ...), uses BCL Parallel.Invoke (real parallelism)
        pNs.AddMethod("Invoke", args =>
        {
            if (args.Length == 0)
                throw new ScriptException("Parallel.Invoke expects at least one function.");
            var actions = new Action[args.Length];
            var selfInvoke = Current ?? this;   // the `Com.Run` rule: capture where the callbacks are CREATED
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] is ScriptFunction fn)
                {
                    var captured = fn;
                    actions[i] = () => selfInvoke.InvokeScriptFunction(captured, [], 0, 0);
                }
                else
                    throw new ScriptException("Parallel.Invoke arguments must be functions.");
            }
            Parallel.Invoke(actions);
            return null;
        });

        CurrentFrame.Scopes.Define("Parallel", pNs);
    }
}
