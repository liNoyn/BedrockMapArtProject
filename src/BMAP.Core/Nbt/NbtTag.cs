namespace BMAP.Nbt;

public enum NbtType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12,
}

/// <summary>Base class of all NBT tags. Names live in the parent compound, not in the tag.</summary>
public abstract class NbtTag
{
    public abstract NbtType Type { get; }
}

public sealed class NbtByte(byte value) : NbtTag
{
    public byte Value { get; set; } = value;
    public override NbtType Type => NbtType.Byte;
    public override string ToString() => $"{Value}b";
}

public sealed class NbtShort(short value) : NbtTag
{
    public short Value { get; set; } = value;
    public override NbtType Type => NbtType.Short;
    public override string ToString() => $"{Value}s";
}

public sealed class NbtInt(int value) : NbtTag
{
    public int Value { get; set; } = value;
    public override NbtType Type => NbtType.Int;
    public override string ToString() => Value.ToString();
}

public sealed class NbtLong(long value) : NbtTag
{
    public long Value { get; set; } = value;
    public override NbtType Type => NbtType.Long;
    public override string ToString() => $"{Value}L";
}

public sealed class NbtFloat(float value) : NbtTag
{
    public float Value { get; set; } = value;
    public override NbtType Type => NbtType.Float;
    public override string ToString() => $"{Value}f";
}

public sealed class NbtDouble(double value) : NbtTag
{
    public double Value { get; set; } = value;
    public override NbtType Type => NbtType.Double;
    public override string ToString() => $"{Value}d";
}

public sealed class NbtByteArray(byte[] value) : NbtTag
{
    public byte[] Value { get; set; } = value;
    public override NbtType Type => NbtType.ByteArray;
    public override string ToString() => $"[B; {Value.Length} bytes]";
}

public sealed class NbtString(string value) : NbtTag
{
    public string Value { get; set; } = value;
    public override NbtType Type => NbtType.String;
    public override string ToString() => $"\"{Value}\"";
}

public sealed class NbtIntArray(int[] value) : NbtTag
{
    public int[] Value { get; set; } = value;
    public override NbtType Type => NbtType.IntArray;
    public override string ToString() => $"[I; {Value.Length} ints]";
}

public sealed class NbtLongArray(long[] value) : NbtTag
{
    public long[] Value { get; set; } = value;
    public override NbtType Type => NbtType.LongArray;
    public override string ToString() => $"[L; {Value.Length} longs]";
}

public sealed class NbtList : NbtTag
{
    public NbtList(NbtType elementType) => ElementType = elementType;

    public NbtList(NbtType elementType, IEnumerable<NbtTag> items) : this(elementType)
    {
        foreach (var item in items) Add(item);
    }

    /// <summary>Element type. Kept even for empty lists so files round-trip byte for byte.</summary>
    public NbtType ElementType { get; set; }
    public List<NbtTag> Items { get; } = [];
    public override NbtType Type => NbtType.List;
    public int Count => Items.Count;
    public NbtTag this[int index] => Items[index];

    public void Add(NbtTag tag)
    {
        if (tag.Type != ElementType)
            throw new ArgumentException($"List of {ElementType} cannot hold {tag.Type}.");
        Items.Add(tag);
    }

    public override string ToString() => $"[{ElementType} x{Items.Count}]";
}

/// <summary>Compound tag. Keeps insertion order so that re-serialized files stay identical.</summary>
public sealed class NbtCompound : NbtTag, IEnumerable<KeyValuePair<string, NbtTag>>
{
    private readonly List<KeyValuePair<string, NbtTag>> _entries = [];

    public override NbtType Type => NbtType.Compound;
    public int Count => _entries.Count;

    public NbtTag? this[string name]
    {
        get
        {
            foreach (var e in _entries)
                if (e.Key == name) return e.Value;
            return null;
        }
        set
        {
            int i = IndexOf(name);
            if (value is null)
            {
                if (i >= 0) _entries.RemoveAt(i);
            }
            else if (i >= 0) _entries[i] = new(name, value);
            else _entries.Add(new(name, value));
        }
    }

    public void Add(string name, NbtTag tag) => this[name] = tag;

    public bool Remove(string name)
    {
        int i = IndexOf(name);
        if (i < 0) return false;
        _entries.RemoveAt(i);
        return true;
    }

    private int IndexOf(string name)
    {
        for (int i = 0; i < _entries.Count; i++)
            if (_entries[i].Key == name) return i;
        return -1;
    }

    public T? Get<T>(string name) where T : NbtTag => this[name] as T;

    public long? GetLong(string name) => this[name] switch
    {
        NbtLong l => l.Value,
        NbtInt i => i.Value,
        NbtShort s => s.Value,
        NbtByte b => b.Value,
        _ => null,
    };

    public int? GetInt(string name) => this[name] switch
    {
        NbtInt i => i.Value,
        NbtShort s => s.Value,
        NbtByte b => b.Value,
        _ => null,
    };

    public string? GetString(string name) => (this[name] as NbtString)?.Value;

    public IEnumerator<KeyValuePair<string, NbtTag>> GetEnumerator() => _entries.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => $"{{{_entries.Count} entries}}";
}
