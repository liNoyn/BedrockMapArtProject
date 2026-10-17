using System.Buffers.Binary;
using System.Text;

namespace BMAG.Nbt;

/// <summary>
/// Bedrock "disk" NBT: little-endian, uncompressed, strings prefixed by an unsigned 16-bit length.
/// This is the format of .mcstructure files, level.dat (after its 8-byte header) and LevelDB values.
/// </summary>
public static class NbtIO
{
    private const int MaxDepth = 512;

    public static byte[] Write(NbtCompound root, string rootName = "")
    {
        using var ms = new MemoryStream();
        Write(ms, root, rootName);
        return ms.ToArray();
    }

    public static void Write(Stream stream, NbtCompound root, string rootName = "")
    {
        var w = new LeWriter(stream);
        w.U8((byte)NbtType.Compound);
        w.Str(rootName);
        WritePayload(w, root, 0);
    }

    public static NbtCompound Read(byte[] data) => Read(data, out _, out _);

    public static NbtCompound Read(ReadOnlySpan<byte> data, out string rootName, out int bytesRead)
    {
        var r = new LeReader(data.ToArray());
        var type = (NbtType)r.U8();
        if (type != NbtType.Compound)
            throw new InvalidDataException($"Root tag must be a compound, got {type}.");
        rootName = r.Str();
        var root = (NbtCompound)ReadPayload(ref r, type, 0);
        bytesRead = r.Position;
        return root;
    }

    /// <summary>Reads consecutive root compounds (some LevelDB values, e.g. player data, are concatenated).</summary>
    public static List<NbtCompound> ReadAll(byte[] data)
    {
        var list = new List<NbtCompound>();
        int offset = 0;
        while (offset < data.Length)
        {
            list.Add(Read(data.AsSpan(offset), out _, out int n));
            offset += n;
        }
        return list;
    }

    private static void WritePayload(LeWriter w, NbtTag tag, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT nesting too deep.");
        switch (tag)
        {
            case NbtByte b: w.U8(b.Value); break;
            case NbtShort s: w.I16(s.Value); break;
            case NbtInt i: w.I32(i.Value); break;
            case NbtLong l: w.I64(l.Value); break;
            case NbtFloat f: w.I32(BitConverter.SingleToInt32Bits(f.Value)); break;
            case NbtDouble d: w.I64(BitConverter.DoubleToInt64Bits(d.Value)); break;
            case NbtByteArray ba:
                w.I32(ba.Value.Length);
                w.Bytes(ba.Value);
                break;
            case NbtString str: w.Str(str.Value); break;
            case NbtList list:
                w.U8((byte)list.ElementType);
                w.I32(list.Count);
                foreach (var item in list.Items) WritePayload(w, item, depth + 1);
                break;
            case NbtCompound c:
                foreach (var (name, child) in c)
                {
                    w.U8((byte)child.Type);
                    w.Str(name);
                    WritePayload(w, child, depth + 1);
                }
                w.U8((byte)NbtType.End);
                break;
            case NbtIntArray ia:
                w.I32(ia.Value.Length);
                foreach (var v in ia.Value) w.I32(v);
                break;
            case NbtLongArray la:
                w.I32(la.Value.Length);
                foreach (var v in la.Value) w.I64(v);
                break;
            default:
                throw new InvalidDataException($"Unknown tag {tag.GetType().Name}");
        }
    }

    private static NbtTag ReadPayload(ref LeReader r, NbtType type, int depth)
    {
        if (depth > MaxDepth) throw new InvalidDataException("NBT nesting too deep.");
        switch (type)
        {
            case NbtType.Byte: return new NbtByte(r.U8());
            case NbtType.Short: return new NbtShort(r.I16());
            case NbtType.Int: return new NbtInt(r.I32());
            case NbtType.Long: return new NbtLong(r.I64());
            case NbtType.Float: return new NbtFloat(BitConverter.Int32BitsToSingle(r.I32()));
            case NbtType.Double: return new NbtDouble(BitConverter.Int64BitsToDouble(r.I64()));
            case NbtType.ByteArray:
                return new NbtByteArray(r.Bytes(r.Length()));
            case NbtType.String: return new NbtString(r.Str());
            case NbtType.List:
            {
                var elemType = (NbtType)r.U8();
                int count = r.Length();
                var list = new NbtList(elemType);
                for (int i = 0; i < count; i++) list.Items.Add(ReadPayload(ref r, elemType, depth + 1));
                return list;
            }
            case NbtType.Compound:
            {
                var c = new NbtCompound();
                while (true)
                {
                    var childType = (NbtType)r.U8();
                    if (childType == NbtType.End) break;
                    string name = r.Str();
                    c.Add(name, ReadPayload(ref r, childType, depth + 1));
                }
                return c;
            }
            case NbtType.IntArray:
            {
                int n = r.Length(4);
                var arr = new int[n];
                for (int i = 0; i < n; i++) arr[i] = r.I32();
                return new NbtIntArray(arr);
            }
            case NbtType.LongArray:
            {
                int n = r.Length(8);
                var arr = new long[n];
                for (int i = 0; i < n; i++) arr[i] = r.I64();
                return new NbtLongArray(arr);
            }
            default:
                throw new InvalidDataException($"Unknown NBT tag type {(byte)type} at offset {r.Position}.");
        }
    }

    private sealed class LeWriter(Stream s)
    {
        private readonly byte[] _buf = new byte[8];
        public void U8(byte v) => s.WriteByte(v);
        public void I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(_buf, v); s.Write(_buf, 0, 2); }
        public void I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(_buf, v); s.Write(_buf, 0, 4); }
        public void I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(_buf, v); s.Write(_buf, 0, 8); }
        public void Bytes(byte[] b) => s.Write(b, 0, b.Length);

        public void Str(string v)
        {
            var bytes = Encoding.UTF8.GetBytes(v);
            if (bytes.Length > ushort.MaxValue) throw new InvalidDataException("NBT string too long.");
            BinaryPrimitives.WriteUInt16LittleEndian(_buf, (ushort)bytes.Length);
            s.Write(_buf, 0, 2);
            Bytes(bytes);
        }
    }

    private ref struct LeReader(byte[] data)
    {
        private readonly byte[] _data = data;
        public int Position { get; private set; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Position + n > _data.Length)
                throw new InvalidDataException($"Unexpected end of NBT data at offset {Position}.");
            var span = _data.AsSpan(Position, n);
            Position += n;
            return span;
        }

        public byte U8() => Take(1)[0];
        public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public byte[] Bytes(int n) => Take(n).ToArray();

        /// <summary>Reads an array/list length and rejects values that cannot fit in the remaining data.</summary>
        public int Length(int elementSize = 1)
        {
            int n = I32();
            if (n < 0 || (long)n * elementSize > _data.Length - Position)
                throw new InvalidDataException($"Invalid NBT length {n} at offset {Position}.");
            return n;
        }

        public string Str()
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
            return Encoding.UTF8.GetString(Take(len));
        }
    }
}
