namespace ScriptLangCore;

internal partial class Interpreter
{
    private readonly Lock _interlockedLock = new();

    private void RegisterSyncFunctions()
    {
        // ── Interlocked ──
        var ilNs = new Namespace(_comparer);

        ilNs.AddMethod("Increment", args =>
        {
            if (args.Length == 0 || args[0] is not RefCell rc)
                throw new ScriptException("Interlocked.Increment expects a ref variable.");
            lock (_interlockedLock)
            {
                var val = Convert.ToInt32(rc.Getter());
                var result = val + 1;
                rc.Setter(result);
                return result;
            }
        });

        ilNs.AddMethod("Decrement", args =>
        {
            if (args.Length == 0 || args[0] is not RefCell rc)
                throw new ScriptException("Interlocked.Decrement expects a ref variable.");
            lock (_interlockedLock)
            {
                var val = Convert.ToInt32(rc.Getter());
                var result = val - 1;
                rc.Setter(result);
                return result;
            }
        });

        ilNs.AddMethod("Exchange", args =>
        {
            if (args.Length < 2 || args[0] is not RefCell rc)
                throw new ScriptException("Interlocked.Exchange expects (ref variable, value).");
            lock (_interlockedLock)
            {
                var val = Convert.ToInt32(rc.Getter());
                var newVal = Convert.ToInt32(args[1]);
                rc.Setter(newVal);
                return val;
            }
        });

        ilNs.AddMethod("CompareExchange", args =>
        {
            if (args.Length < 3 || args[0] is not RefCell rc)
                throw new ScriptException("Interlocked.CompareExchange expects (ref variable, value, comparand).");
            lock (_interlockedLock)
            {
                var val = Convert.ToInt32(rc.Getter());
                var newVal = Convert.ToInt32(args[1]);
                var cmp = Convert.ToInt32(args[2]);
                if (val != cmp) return val;
                rc.Setter(newVal);
                return cmp;
            }
        });

        ilNs.AddMethod("Add", args =>
        {
            if (args.Length == 0 || args[0] is not RefCell rc)
                throw new ScriptException("Interlocked.Add expects a ref variable.");
            lock (_interlockedLock)
            {
                var val = Convert.ToInt32(rc.Getter());
                var addend = args.Length > 1 ? Convert.ToInt32(args[1]) : 1;
                var result = val + addend;
                rc.Setter(result);
                return result;
            }
        });

        CurrentFrame.Scopes.Define("Interlocked", ilNs);

        // ── Monitor ──
        var monNs = new Namespace(_comparer);

        monNs.AddMethod("Enter", args =>
        {
            if (args.Length == 0) throw new ScriptException("Monitor.Enter expects an object.");
            Monitor.Enter(args[0]!);
            return null;
        });

        monNs.AddMethod("Exit", args =>
        {
            if (args.Length == 0) throw new ScriptException("Monitor.Exit expects an object.");
            Monitor.Exit(args[0]!);
            return null;
        });

        monNs.AddMethod("TryEnter", args =>
        {
            return args.Length switch
            {
                0 => throw new ScriptException("Monitor.TryEnter expects an object."),
                > 1 => Monitor.TryEnter(args[0]!, Convert.ToInt32(args[1])),
                _ => Monitor.TryEnter(args[0]!)
            };
        });

        CurrentFrame.Scopes.Define("Monitor", monNs);
    }
}
