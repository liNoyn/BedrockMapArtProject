namespace BMAP.Bedrock.LevelDb;

/// <summary>CRC-32C (Castagnoli) with LevelDB's masking.</summary>
internal static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static uint Extend(uint crc, ReadOnlySpan<byte> data) => Compute(data, crc);

    private const uint MaskDelta = 0xa282ead8u;
    public static uint Unmask(uint masked)
    {
        uint rot = masked - MaskDelta;
        return (rot >> 17) | (rot << 15);
    }
}

internal static class VarInt
{
    public static ulong Read64(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            if (pos >= data.Length) throw new InvalidDataException("Truncated varint.");
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("Varint too long.");
    }

    public static uint Read32(ReadOnlySpan<byte> data, ref int pos) => checked((uint)Read64(data, ref pos));

    public static byte[] ReadLengthPrefixed(ReadOnlySpan<byte> data, ref int pos)
    {
        int len = (int)Read32(data, ref pos);
        if (len < 0 || pos + len > data.Length) throw new InvalidDataException("Truncated length-prefixed slice.");
        var r = data.Slice(pos, len).ToArray();
        pos += len;
        return r;
    }
}

/// <summary>Byte-wise comparison and hashing of keys (LevelDB's default "leveldb.BytewiseComparator").</summary>
internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj)
    {
        var h = new HashCode();
        h.AddBytes(obj);
        return h.ToHashCode();
    }

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}
