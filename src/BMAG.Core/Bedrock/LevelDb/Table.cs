using System.Buffers.Binary;
using System.IO.Compression;

namespace BMAG.Bedrock.LevelDb;

/// <summary>Internal key = user key + 8-byte trailer (sequence &lt;&lt; 8 | type).</summary>
internal readonly record struct InternalEntry(byte[] UserKey, ulong Sequence, bool IsDeletion, byte[] Value);

/// <summary>
/// Read-only SSTable (.ldb / .sst) reader with the compression types used by Minecraft Bedrock:
/// 0 = none, 2 = zlib, 4 = raw deflate ("zlib raw").
/// </summary>
internal sealed class Table
{
    private const ulong Magic = 0xdb4775248b80fb57UL;
    private const int FooterSize = 48;

    private readonly string _path;
    private readonly List<(byte[] SeparatorUserKey, ulong Offset, ulong Size)> _index = [];

    private Table(string path) => _path = path;

    public static Table Open(string path)
    {
        var t = new Table(path);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < FooterSize) throw new InvalidDataException($"{Path.GetFileName(path)}: テーブルが短すぎます。");
        var footer = new byte[FooterSize];
        fs.Seek(-FooterSize, SeekOrigin.End);
        fs.ReadExactly(footer);
        if (BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(40)) != Magic)
            throw new InvalidDataException($"{Path.GetFileName(path)}: マジック番号が一致しません。");
        int pos = 0;
        VarInt.Read64(footer, ref pos); // metaindex offset
        VarInt.Read64(footer, ref pos); // metaindex size
        ulong indexOffset = VarInt.Read64(footer, ref pos);
        ulong indexSize = VarInt.Read64(footer, ref pos);
        var indexBlock = ReadBlock(fs, indexOffset, indexSize, path);
        foreach (var (key, value) in IterateBlock(indexBlock))
        {
            int p = 0;
            ulong off = VarInt.Read64(value, ref p);
            ulong size = VarInt.Read64(value, ref p);
            t._index.Add((UserKey(key), off, size));
        }
        return t;
    }

    public static byte[] UserKey(byte[] internalKey) =>
        internalKey.Length >= 8 ? internalKey[..^8] : throw new InvalidDataException("Internal key too short.");

    public static InternalEntry ToEntry(byte[] internalKey, byte[] value)
    {
        ulong trailer = BinaryPrimitives.ReadUInt64LittleEndian(internalKey.AsSpan(internalKey.Length - 8));
        return new InternalEntry(internalKey[..^8], trailer >> 8, (trailer & 0xFF) == 0, value);
    }

    /// <summary>Entries whose user key starts with <paramref name="prefix"/> (empty = all). Only reads blocks that can match.</summary>
    public IEnumerable<InternalEntry> Scan(byte[] prefix)
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var cmp = ByteArrayComparer.Instance;
        for (int i = 0; i < _index.Count; i++)
        {
            var (sep, off, size) = _index[i];
            if (prefix.Length > 0)
            {
                // Block i holds keys in (sep[i-1], sep[i]].
                if (cmp.Compare(sep, prefix) < 0 && !sep.AsSpan().StartsWith(prefix)) continue;
                if (i > 0)
                {
                    var prev = _index[i - 1].SeparatorUserKey;
                    if (cmp.Compare(prev, prefix) > 0 && !prev.AsSpan().StartsWith(prefix)) break;
                }
            }
            var block = ReadBlock(fs, off, size, _path);
            foreach (var (key, value) in IterateBlock(block))
            {
                var e = ToEntry(key, value);
                if (prefix.Length == 0 || e.UserKey.AsSpan().StartsWith(prefix)) yield return e;
            }
        }
    }

    public IEnumerable<InternalEntry> Get(byte[] userKey) =>
        Scan(userKey).Where(e => ByteArrayComparer.Instance.Equals(e.UserKey, userKey));

    private static byte[] ReadBlock(FileStream fs, ulong offset, ulong size, string path)
    {
        var buf = new byte[size + 5];
        fs.Seek((long)offset, SeekOrigin.Begin);
        fs.ReadExactly(buf);
        byte type = buf[size];
        uint masked = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan((int)size + 1));
        uint actual = Crc32C.Compute(buf.AsSpan(0, (int)size + 1));
        if (Crc32C.Unmask(masked) != actual)
            throw new InvalidDataException($"{Path.GetFileName(path)}: ブロックの CRC が一致しません (offset {offset})。");
        var raw = buf.AsMemory(0, (int)size);
        return type switch
        {
            0 => raw.ToArray(),
            2 => Inflate(new ZLibStream(new MemoryStream(raw.ToArray()), CompressionMode.Decompress)),
            4 => Inflate(new DeflateStream(new MemoryStream(raw.ToArray()), CompressionMode.Decompress)),
            _ => throw new InvalidDataException($"{Path.GetFileName(path)}: 未対応の圧縮形式 {type}"),
        };
    }

    private static byte[] Inflate(Stream s)
    {
        using (s)
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    /// <summary>Iterates a block's prefix-compressed entries (restart array at the end is skipped).</summary>
    private static IEnumerable<(byte[] Key, byte[] Value)> IterateBlock(byte[] block)
    {
        if (block.Length < 4) yield break;
        int numRestarts = (int)BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(block.Length - 4));
        int limit = block.Length - 4 - numRestarts * 4;
        if (limit < 0) throw new InvalidDataException("Bad block restart array.");
        int pos = 0;
        byte[] lastKey = [];
        while (pos < limit)
        {
            int shared = (int)VarInt.Read32(block, ref pos);
            int nonShared = (int)VarInt.Read32(block, ref pos);
            int valueLen = (int)VarInt.Read32(block, ref pos);
            if (shared > lastKey.Length || pos + nonShared + valueLen > limit) throw new InvalidDataException("Bad block entry.");
            var key = new byte[shared + nonShared];
            Buffer.BlockCopy(lastKey, 0, key, 0, shared);
            Buffer.BlockCopy(block, pos, key, shared, nonShared);
            pos += nonShared;
            var value = block.AsSpan(pos, valueLen).ToArray();
            pos += valueLen;
            lastKey = key;
            yield return (key, value);
        }
    }
}
