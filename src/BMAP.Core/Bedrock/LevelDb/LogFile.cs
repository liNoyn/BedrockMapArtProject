using System.Buffers.Binary;

namespace BMAP.Bedrock.LevelDb;

/// <summary>
/// LevelDB log format (used by *.log write-ahead logs and MANIFEST files), read only:
/// 32 KiB blocks; each physical record = crc32c(4, masked) | length(2) | type(1) | data.
/// </summary>
internal static class LogFile
{
    public const int BlockSize = 32768;
    public const int HeaderSize = 7;

    private const byte Zero = 0, Full = 1, First = 2, Middle = 3, Last = 4;

    public sealed class ReadResult
    {
        public List<byte[]> Records { get; } = [];
        /// <summary>Description of corruption found (null = clean). A torn tail after a crash also shows up here.</summary>
        public string? Corruption { get; set; }
        /// <summary>File offset just after the last complete physical record.</summary>
        public long LogicalEnd { get; set; }
    }

    public static ReadResult ReadRecords(byte[] file)
    {
        var result = new ReadResult();
        List<byte>? pending = null;
        int pos = 0;
        while (pos < file.Length)
        {
            int blockRemaining = BlockSize - pos % BlockSize;
            if (blockRemaining < HeaderSize)
            {
                pos += blockRemaining; // trailer padding
                continue;
            }
            if (pos + HeaderSize > file.Length) break; // truncated header at end of file (writer died) -> ignore
            uint maskedCrc = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(pos));
            int length = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(pos + 4));
            byte type = file[pos + 6];
            if (type == Zero && length == 0)
            {
                // Pre-allocated / padded area: skip the rest of this block (same as LevelDB).
                pos += blockRemaining;
                continue;
            }
            if (HeaderSize + length > blockRemaining || pos + HeaderSize + length > file.Length)
            {
                result.Corruption ??= $"レコード長が不正です (offset {pos})";
                break;
            }
            var data = file.AsSpan(pos + HeaderSize, length);
            uint actual = Crc32C.Extend(Crc32C.Compute([type]), data);
            if (Crc32C.Unmask(maskedCrc) != actual)
            {
                result.Corruption ??= $"CRC 不一致 (offset {pos})";
                break;
            }
            pos += HeaderSize + length;
            result.LogicalEnd = pos;
            switch (type)
            {
                case Full:
                    if (pending != null) result.Corruption ??= "断片化されたレコードが途中で終わっています";
                    pending = null;
                    result.Records.Add(data.ToArray());
                    break;
                case First:
                    pending = [.. data];
                    break;
                case Middle:
                    if (pending == null) { result.Corruption ??= "先頭のない MIDDLE レコード"; break; }
                    pending.AddRange(data.ToArray());
                    break;
                case Last:
                    if (pending == null) { result.Corruption ??= "先頭のない LAST レコード"; break; }
                    pending.AddRange(data.ToArray());
                    result.Records.Add(pending.ToArray());
                    pending = null;
                    break;
                default:
                    result.Corruption ??= $"不明なレコード種別 {type} (offset {pos})";
                    return result;
            }
        }
        return result;
    }
}
