namespace ScriptLangCore;

internal partial class Interpreter
{
    /// <summary>The feeds (`Events.Create`) this interpreter made, so a run's end closes them: a subscriber is a
    /// callable built on THIS interpreter, and a delivery after `Dispose` would enter a body whose frames are gone.
    /// Only the PUBLISHING object is registered; a read-only view shares its core, so closing it is the same act.</summary>
    private readonly List<EventSubject> _subjects = [];
    private readonly object _subjectGate = new();

    internal void RegisterSubject(EventSubject subject)
    {
        lock (_subjectGate) _subjects.Add(subject);
    }

    /// <summary>Ends every feed this interpreter created. Called from `Dispose` (see the note there) and it says
    /// nothing to the subscribers on purpose: the run is over, so a delivery would be worse than a silence, and a
    /// pull consumer is released by the same call rather than left waiting for an end that cannot arrive.</summary>
    private void CloseSubjects()
    {
        EventSubject[] subjects;
        lock (_subjectGate)
        {
            if (_subjects.Count == 0) return;
            subjects = [.. _subjects];
            _subjects.Clear();
        }
        foreach (var subject in subjects) subject.Dispose();
    }

    /// <summary>`Events` is the generic event layer: a feed a script can hold, hand around and subscribe to, for
    /// values that arrive from somewhere else (a socket, a `Process.Start` output callback, a COM event, another
    /// thread) rather than from the object that owns them.
    ///
    /// WHY IT IS NOT `event` AGAIN. A script class already has `event X OnY;` with `+=`/`-=`, and that covers the
    /// in-process case where the publisher is the object you hold. What it cannot do is be PASSED as a value, be
    /// published to from a background thread while a subscriber writes a control, or end with a reason. Those three
    /// are this layer's whole subject, and the middle one is the measured gap: a control write from a `Process`
    /// output callback does not land, so a feed needs a delivery mode that queues onto the run thread.
    ///
    /// TWO SHAPES ON ONE BUFFER. A push subscriber is a function called per value; a pull consumer is
    /// `await foreach (var v in feed)`, because the type implements `IAsyncEnumerable&lt;object?&gt;` and the engine's
    /// existing await-foreach drive finds it by reflection. Both are fed by `Next`, each subscriber owns its own
    /// bounded queue, and that queue is where backpressure is answered instead of hidden.</summary>
    private void RegisterEventsFunctions()
    {
        var eventsNs = new Namespace(_comparer);

        // Events.Create()                       -> inline delivery, capacity 1024, dropOldest
        // Events.Create("runThread")            -> the mode alone, for the common case
        // Events.Create({ mode: ..., capacity: ..., overflow: ... })
        eventsNs.AddMethod("Create", args =>
        {
            // The registering interpreter is not necessarily the one running: a module inherits its parent's
            // namespaces, and the feed must belong to whoever CALLED, so the disposal at the run's end is right.
            var self = Current ?? this;
            var delivery = SubjectDelivery.Inline;
            var capacity = SubjectCore.DefaultCapacity;
            var overflow = SubjectOverflow.DropOldest;

            var options = args.Length > 0 ? args[0] : null;
            if (options is not null)
            {
                if (options is string modeName)
                {
                    delivery = self.DeliveryNamed(modeName, "Events.Create");
                }
                else
                {
                    var pairs = SubjectOptionsOf(options);
                    if (pairs is null)
                        RuntimeError(
                            $"Events.Create takes a mode name or an options dictionary, not '{GetFriendlyTypeNameOfValue(options)}'.",
                            0, 0);
                    foreach (var (name, value) in pairs)
                    {
                        if (self._comparer.Equals(name, "mode")) delivery = self.DeliveryNamed(value, "Events.Create");
                        else if (self._comparer.Equals(name, "capacity")) capacity = CapacityOf(value);
                        else if (self._comparer.Equals(name, "overflow")) overflow = self.OverflowNamed(value, "Events.Create");
                        else RuntimeError($"Events.Create: unknown option '{name}'. The options are mode, capacity and overflow.", 0, 0);
                    }
                }
            }

            var subject = new EventSubject(new SubjectCore(delivery, capacity, overflow), home: self);
            self.RegisterSubject(subject);
            return subject;
        });

        CurrentFrame.Scopes.Define("Events", eventsNs);
    }

    /// <summary>A mode name, compared with THIS interpreter's case rule (so `.lax` accepts `Inline`), and refused by
    /// name otherwise rather than silently falling back to the default.</summary>
    private SubjectDelivery DeliveryNamed(object? value, string member)
    {
        if (value is string text)
        {
            if (_comparer.Equals(text, "inline")) return SubjectDelivery.Inline;
            if (_comparer.Equals(text, "runThread")) return SubjectDelivery.RunThread;
            RuntimeError($"{member}: '{text}' is not a delivery mode. The modes are inline and runThread.", 0, 0);
        }
        RuntimeError($"{member}: the delivery mode is a name (inline or runThread), not '{GetFriendlyTypeNameOfValue(value)}'.", 0, 0);
        return SubjectDelivery.Inline;   // unreachable: RuntimeError throws
    }

    private SubjectOverflow OverflowNamed(object? value, string member)
    {
        if (value is string text)
        {
            if (_comparer.Equals(text, "dropOldest")) return SubjectOverflow.DropOldest;
            if (_comparer.Equals(text, "dropNewest")) return SubjectOverflow.DropNewest;
            if (_comparer.Equals(text, "block")) return SubjectOverflow.Block;
            RuntimeError($"{member}: '{text}' is not an overflow policy. The policies are dropOldest, dropNewest and block.", 0, 0);
        }
        RuntimeError($"{member}: the overflow policy is a name (dropOldest, dropNewest or block), not '{GetFriendlyTypeNameOfValue(value)}'.", 0, 0);
        return SubjectOverflow.DropOldest;   // unreachable: RuntimeError throws
    }

    private static int CapacityOf(object? value)
    {
        var n = value switch
        {
            int i => i,
            long l when l is > 0 and <= int.MaxValue => (int)l,
            _ => int.MinValue,
        };
        if (n == int.MinValue)
            RuntimeError($"Events.Create: capacity must be a whole number of items, not '{GetFriendlyTypeNameOfValue(value)}'.", 0, 0);
        if (n <= 0) RuntimeError("Events.Create: capacity must be at least 1.", 0, 0);
        return n;
    }

    /// <summary>The options as name/value pairs, or null when the value is not a dictionary at all. A script dictionary
    /// literal is a `Dictionary&lt;object, object?&gt;` (measured, `typeof({})` says so) while `Http.ParseForm` and
    /// friends answer an `IScriptDictionary`, and BOTH spellings are what an author writes. The KEYS are compared
    /// through the caller's interpreter, deliberately: a dictionary's own rule is whatever it was created with, and an
    /// option name is the engine's business (the same defect `Json`'s option reader records having had).</summary>
    internal static List<(string Name, object? Value)>? SubjectOptionsOf(object? options)
    {
        switch (options)
        {
            case IScriptDictionary scripted:
            {
                var pairs = new List<(string Name, object? Value)>();
                foreach (var key in scripted.Keys())
                    if (key is string name) pairs.Add((name, scripted[key]));
                return pairs;
            }
            case IDictionary raw:
            {
                var pairs = new List<(string Name, object? Value)>();
                foreach (var key in raw.Keys)
                    if (key is string name) pairs.Add((name, raw[key]));
                return pairs;
            }
            default:
                return null;
        }
    }
}
