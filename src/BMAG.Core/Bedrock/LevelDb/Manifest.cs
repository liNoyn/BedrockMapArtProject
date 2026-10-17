namespace BMAG.Bedrock.LevelDb;

internal sealed record TableFileInfo(int Level, ulong Number, ulong Size, byte[] Smallest, byte[] Largest);

/// <summary>The current version state replayed from the MANIFEST (VersionEdit records).</summary>
internal sealed class ManifestState
{
    public string? Comparator { get; private set; }
    public ulong LogNumber { get; private set; }
    public ulong PrevLogNumber { get; private set; }
    public ulong NextFileNumber { get; private set; }
    public ulong LastSequence { get; private set; }
    public Dictionary<ulong, TableFileInfo> Files { get; } = [];
    public string ManifestName { get; private set; } = "";

    private const int KComparator = 1, KLogNumber = 2, KNextFileNumber = 3, KLastSequence = 4,
        KCompactPointer = 5, KDeletedFile = 6, KNewFile = 7, KPrevLogNumber = 9;

    public static ManifestState Load(string dbDir)
    {
        string currentPath = Path.Combine(dbDir, "CURRENT");
        if (!File.Exists(currentPath)) throw new InvalidDataException("db/CURRENT がありません。LevelDB ワールドではない可能性があります。");
        string name = File.ReadAllText(currentPath).Trim();
        if (!name.StartsWith("MANIFEST-")) throw new InvalidDataException($"CURRENT の内容が不正です: {name}");
        var state = new ManifestState { ManifestName = name };
        var log = LogFile.ReadRecords(File.ReadAllBytes(Path.Combine(dbDir, name)));
        if (log.Corruption != null) throw new InvalidDataException($"MANIFEST が破損しています: {log.Corruption}");
        foreach (var rec in log.Records) state.Apply(rec);
        if (state.Comparator is not null && state.Comparator != "leveldb.BytewiseComparator")
            throw new InvalidDataException($"未対応の比較関数: {state.Comparator}");
        return state;
    }

    private void Apply(byte[] edit)
    {
        int pos = 0;
        var span = edit.AsSpan();
        while (pos < edit.Length)
        {
            uint tag = VarInt.Read32(span, ref pos);
            switch (tag)
            {
                case KComparator:
                    Comparator = System.Text.Encoding.ASCII.GetString(VarInt.ReadLengthPrefixed(span, ref pos));
                    break;
                case KLogNumber: LogNumber = VarInt.Read64(span, ref pos); break;
                case KPrevLogNumber: PrevLogNumber = VarInt.Read64(span, ref pos); break;
                case KNextFileNumber: NextFileNumber = VarInt.Read64(span, ref pos); break;
                case KLastSequence: LastSequence = VarInt.Read64(span, ref pos); break;
                case KCompactPointer:
                    VarInt.Read32(span, ref pos);
                    VarInt.ReadLengthPrefixed(span, ref pos);
                    break;
                case KDeletedFile:
                    VarInt.Read32(span, ref pos);
                    Files.Remove(VarInt.Read64(span, ref pos));
                    break;
                case KNewFile:
                {
                    int level = (int)VarInt.Read32(span, ref pos);
                    ulong number = VarInt.Read64(span, ref pos);
                    ulong size = VarInt.Read64(span, ref pos);
                    var smallest = VarInt.ReadLengthPrefixed(span, ref pos);
                    var largest = VarInt.ReadLengthPrefixed(span, ref pos);
                    Files[number] = new TableFileInfo(level, number, size, smallest, largest);
                    break;
                }
                default:
                    throw new InvalidDataException($"MANIFEST に未対応のタグ {tag} があります。");
            }
        }
    }
}
