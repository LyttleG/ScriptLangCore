namespace ScriptLangCore;

/// <summary>A container read LAZILY: `Avro.ReadContainer("Iterable", data)` answers this instead of a
/// materialised list. It is the container's answer to `Json.Deserialize("Iterable", ...)`, and the
/// correspondence is exact in what it leaves undone: the source is already in hand (the file's bytes), the
/// header is parsed once so the schema and the codec are known, the BLOCK boundaries are recorded without
/// decompressing anything, `Count` is the sum of the blocks' own counts, and a record is decoded only when
/// it is read. So a 1 000 000-record container costs its bytes plus the records actually touched, where the
/// eager read costs every decoded record at once.
///
/// The value is a VIEW over the file's bytes, so every mutation is REFUSED by name: an appended record
/// would have nowhere to go, and a silent drop is the outcome this repository refuses.
///
/// WHY PER RECORD AND NOT PER BLOCK: this writer puts a whole container's records in ONE block, so
/// decompressing a block and decoding its records one at a time is what keeps a file this engine wrote
/// lazy too. A block written by another tool is decoded the same way, block after block.
///
/// THE NAME IS THE DOOR: `Avro.ReadContainer("Iterable", data)` and `... "IEnumerable"` ask for this, which is
/// the one rule `Json.Deserialize` follows as well (a type name decides the shape of the answer). An element
/// argument (`Iterable&lt;Row&gt;`) is imposed on each record as it is decoded, the way a lazy JSON list imposes
/// its own element name; a bare name imposes nothing, so the records stay the dictionaries the file holds.</summary>
internal sealed class AvroLazyContainer : IScriptList
{
    private readonly byte[] _data;
    private readonly AvroSchema _schema;
    private readonly string _codec;
    private readonly string _member;
    /// <summary>The name the read was ASKED for ("Iterable", "IEnumerable", "Iterable&lt;Row&gt;"), which is what
    /// `typeof` and `print` report, exactly as a lazy Json read reports the declaration it was asked for.</summary>
    private readonly string _declared;
    /// <summary>The element name inside that declaration, imposed on each record as it is decoded. EMPTY for a
    /// bare "Iterable", where the file's own records are the answer.</summary>
    private readonly string _elementName;
    private readonly bool _caseSensitive;
    private readonly List<AvroContainer.BlockSpan> _blocks;
    /// <summary>The global record index each block STARTS at, so an index finds its block by binary search
    /// rather than by walking the list for every read.</summary>
    private readonly int[] _starts;
    private readonly Dictionary<int, BlockState> _states = new();
    private long _count = -1;

    /// <summary>One block's decode progress: its decompressed payload (built on first touch), how far its
    /// records have been decoded, and the values decoded so far. A record is decoded by decoding the NEXT one
    /// from the cursor, which is why an untouched block costs nothing and a walked one costs one decode each.
    /// </summary>
    private sealed class BlockState
    {
        internal byte[]? Payload;
        internal int Cursor;
        internal readonly List<object?> Values = new();
    }

    private AvroLazyContainer(byte[] data, AvroSchema schema, string codec,
        List<AvroContainer.BlockSpan> blocks, string declared, string elementName, bool caseSensitive,
        string member)
    {
        _data = data;
        _schema = schema;
        _codec = codec;
        _blocks = blocks;
        _declared = declared;
        _elementName = elementName;
        _caseSensitive = caseSensitive;
        _member = member;
        _starts = new int[blocks.Count];
        var at = 0L;
        for (var i = 0; i < blocks.Count; i++)
        {
            _starts[i] = (int)at;
            at += blocks[i].Count;
        }
    }

    /// <summary>The script spelling this value reports to `typeof` and `print`: the name the read was ASKED
    /// for, which is the same one `Json.Deserialize("Iterable", ...)` answers.</summary>
    internal string DeclaredName => _declared;

    public override string ToString() => DeclaredName;

    /// <summary>Whether a type name asks for the LAZY read, and the two names the read needs when it does: the
    /// declaration itself (what `typeof` reports) and the ELEMENT name inside it, which is empty for a bare
    /// "Iterable". The decision is <see cref="ContainerKindOf"/>, so "Iterable" means here exactly what it means
    /// to `Json.Deserialize` and the two readers cannot drift about the spelling; the element name is read here
    /// because a CONTAINER name carries its element in its own argument, which is the one piece of the name this
    /// read has to look into.</summary>
    internal static bool NamesLazyRead(string typeName, out string declared, out string elementName)
    {
        declared = typeName.Trim();
        elementName = "";
        if (ContainerKindOf(declared) != ContainerKind.Iterable) return false;
        var span = declared.AsSpan();
        var open = span.IndexOf('<');
        if (open > 0 && span[^1] == '>')
            elementName = span.Slice(open + 1, span.Length - open - 2).Trim().ToString();
        return true;
    }

    internal static AvroLazyContainer Read(byte[] data, string declared, string elementName, bool caseSensitive,
        string member)
    {
        var header = AvroContainer.Scan(data, member);
        return new AvroLazyContainer(data, header.Schema, header.Codec, header.Blocks, declared, elementName,
            caseSensitive, member);
    }

    public int Count
    {
        get
        {
            if (_count < 0)
            {
                var total = 0L;
                foreach (var block in _blocks) total += block.Count;
                if (total > int.MaxValue)
                    throw new ScriptException(
                        $"{_member}: the container declares {total} record(s), which is more than one script list " +
                        $"can index. Read it in pieces, or with Avro.ReadContainer(data) (no \"{_declared}\" name) " +
                        "when the count fits.");
                _count = total;
            }
            return (int)_count;
        }
    }

    public TypeInfo ElementType => new(TokenType.Object, false, 0, 0, null);

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= Count)
                throw new ScriptException(
                    $"{_member}: the index {index} is outside the {Count} record(s) this container holds.");
            var block = BlockOf(index);
            var state = StateOf(block);
            var within = index - _starts[block];
            while (state.Values.Count <= within) DecodeNext(block, state);
            return state.Values[within];
        }
        set => throw NotEditable("a record cannot be replaced");
    }

    private int BlockOf(int index)
    {
        var low = 0;
        var high = _starts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_starts[mid] <= index) low = mid;
            else high = mid - 1;
        }
        return low;
    }

    private BlockState StateOf(int block)
    {
        if (_states.TryGetValue(block, out var state)) return state;
        state = new BlockState();
        _states[block] = state;
        return state;
    }

    private void DecodeNext(int block, BlockState state)
    {
        if (state.Payload is null)
            state.Payload = AvroCodecs.Decompress(_codec,
                _data.AsSpan(_blocks[block].Offset, _blocks[block].Length).ToArray(), _member);
        if (state.Cursor >= state.Payload.Length)
            throw new ScriptException(
                $"{_member}: a block declares {_blocks[block].Count} object(s) and its payload ended after " +
                $"{state.Values.Count}, so the file's schema and its blocks disagree.");
        var reader = new AvroBinaryReader(state.Payload.AsSpan(state.Cursor), _member);
        var value = AvroValueMap.Read(ref reader, _schema, _member);
        state.Cursor += reader.Position;
        state.Values.Add(_elementName.Length > 0
            ? AvroValueMap.ImposeType(value, _elementName, _caseSensitive)
            : value);
    }

    public Array ToArray()
    {
        var result = new object?[Count];
        for (var i = 0; i < Count; i++) result[i] = this[i];
        return result;
    }

    public void Add(object? item, int line, int column) => throw NotEditable("an item cannot be added");
    public void AddRaw(object item) => throw NotEditable("an item cannot be added");
    public void Remove(object? item) => throw NotEditable("an item cannot be removed");
    public void RemoveAt(int index) => throw NotEditable("an item cannot be removed");
    public void Clear() => throw NotEditable("it cannot be cleared");
    public void Sort(object?[] args, int line, int column) => throw NotEditable("it cannot be sorted");

    private ScriptException NotEditable(string what) =>
        new($"{_member}: a container read as \"{_declared}\" is a VIEW over the file's bytes, so {what}. Add one " +
            $"with Avro.AppendContainer(data, records), or read it eagerly (Avro.ReadContainer(data), without the " +
            $"\"{_declared}\" name) and edit that copy.");
}
