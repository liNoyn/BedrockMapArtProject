using System.Buffers.Binary;
using System.Text;

namespace BMAG.Bedrock.LevelDb;

public sealed class WriteBatch
{
    internal readonly List<(byte[] Key, byte[]? Value)> Ops = [];

    public int Count => Ops.Count;

    public void Put(string key, byte[] value) => Put(Encoding.ASCII.GetBytes(key), value);
    public void Put(byte[] key, byte[] value) => Ops.Add((key, value));
    public void Delete(string key) => Ops.Add((Encoding.ASCII.GetBytes(key), null));

    /// <summary>Serialises as LevelDB's WriteBatch rep: seq(8) | count(4) | records.</summary>
    internal byte[] Encode(ulong sequence)
    {
        using var ms = new MemoryStream();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(header, sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)Ops.Count);
        ms.Write(header);
        foreach (var (key, value) in Ops)
        {
            ms.WriteByte(value is null ? (byte)0 : (byte)1);
            VarInt.Write(ms, (ulong)key.Length);
            ms.Write(key);
            if (value is not null)
            {
                VarInt.Write(ms, (ulong)value.Length);
                ms.Write(value);
            }
        }
        return ms.ToArray();
    }

    /// <summary>Decodes a batch record from a log file.</summary>
    internal static (ulong Sequence, List<(byte[] Key, byte[]? Value)> Ops) Decode(byte[] rep)
    {
        if (rep.Length < 12) throw new InvalidDataException("WriteBatch too small.");
        ulong seq = BinaryPrimitives.ReadUInt64LittleEndian(rep);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(rep.AsSpan(8));
        var ops = new List<(byte[], byte[]?)>((int)Math.Min(count, 100000));
        int pos = 12;
        for (uint i = 0; i < count; i++)
        {
            if (pos >= rep.Length) throw new InvalidDataException("WriteBatch truncated.");
            byte tag = rep[pos++];
            var key = VarInt.ReadLengthPrefixed(rep, ref pos);
            if (tag == 1) ops.Add((key, VarInt.ReadLengthPrefixed(rep, ref pos)));
            else if (tag == 0) ops.Add((key, null));
            else throw new InvalidDataException($"Unknown WriteBatch tag {tag}.");
        }
        return (seq, ops);
    }
}
