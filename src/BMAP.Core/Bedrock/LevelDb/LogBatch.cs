using System.Buffers.Binary;

namespace BMAP.Bedrock.LevelDb;

/// <summary>Decodes LevelDB write batches found in *.log files (read only).</summary>
internal static class LogBatch
{
    /// <summary>Batch rep: seq(8) | count(4) | records (tag, key, value).</summary>
    public static (ulong Sequence, List<(byte[] Key, byte[]? Value)> Ops) Decode(byte[] rep)
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