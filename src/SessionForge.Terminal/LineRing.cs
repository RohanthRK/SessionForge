namespace SessionForge.Terminal;

/// <summary>Fixed-capacity ring of lines used for scrollback. Index 0 is the oldest line.</summary>
public sealed class LineRing
{
    private TerminalLine?[] _items;
    private int _head; // index of oldest
    private int _count;

    public LineRing(int capacity) => _items = new TerminalLine?[Math.Max(0, capacity)];

    public int Count => _count;
    public int Capacity => _items.Length;

    /// <summary>Total number of lines ever dropped off the front. Lets callers keep stable absolute line ids.</summary>
    public long Evicted { get; private set; }

    public TerminalLine this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return _items[(_head + index) % _items.Length]!;
        }
    }

    /// <summary>Appends a line; if full, the oldest line is evicted and returned (so it can be recycled).</summary>
    public TerminalLine? Push(TerminalLine line)
    {
        if (_items.Length == 0) { Evicted++; return line; }
        if (_count < _items.Length)
        {
            _items[(_head + _count) % _items.Length] = line;
            _count++;
            return null;
        }
        var old = _items[_head];
        _items[_head] = line;
        _head = (_head + 1) % _items.Length;
        Evicted++;
        return old;
    }

    /// <summary>Removes and returns the newest line.</summary>
    public TerminalLine PopNewest()
    {
        if (_count == 0) throw new InvalidOperationException("Empty");
        int idx = (_head + _count - 1) % _items.Length;
        var l = _items[idx]!;
        _items[idx] = null;
        _count--;
        return l;
    }

    public void Clear()
    {
        Evicted += _count;
        Array.Clear(_items);
        _head = 0;
        _count = 0;
    }

    /// <summary>
    /// Replaces the contents (used by reflow). The eviction counter is adjusted so that ids of
    /// lines that remain are as stable as possible; lines beyond capacity are counted as evicted.
    /// </summary>
    public void Rebuild(IReadOnlyList<TerminalLine> lines)
    {
        Array.Clear(_items);
        _head = 0;
        _count = 0;
        int skip = Math.Max(0, lines.Count - _items.Length);
        Evicted += skip;
        for (int i = skip; i < lines.Count; i++) _items[_count++] = lines[i];
    }

    public void SetCapacity(int capacity)
    {
        capacity = Math.Max(0, capacity);
        if (capacity == _items.Length) return;
        var keep = Math.Min(_count, capacity);
        var arr = new TerminalLine?[capacity];
        for (int i = 0; i < keep; i++) arr[i] = this[_count - keep + i];
        Evicted += _count - keep;
        _items = arr;
        _head = 0;
        _count = keep;
    }

    public IEnumerable<TerminalLine> Enumerate()
    {
        for (int i = 0; i < _count; i++) yield return this[i];
    }
}
