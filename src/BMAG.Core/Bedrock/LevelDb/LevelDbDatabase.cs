using System.Text;

namespace BMAG.Bedrock.LevelDb;

/// <summary>
/// Minimal LevelDB implementation for Bedrock worlds (design 9.3 / 14).
/// Reads: MANIFEST + live tables + write-ahead logs, newest sequence number wins.
/// Writes: one WriteBatch appended to the current log file — the same thing LevelDB does for a write.
/// The game replays the log into its memtable the next time it opens the world. No compaction is performed.
/// </summary>
public sealed class LevelDbDatabase : IDisposable
{
    private readonly string _dbDir;
    private readonly ManifestState _manifest;
    private readonly List<(TableFileInfo Info, Lazy<Table> Table)> _tables = [];
    private readonly Dictionary<byte[], InternalEntry> _memtable = new(ByteArrayComparer.Instance);
    private readonly FileStream? _lock;
    private ulong _lastSequence;
    private string _currentLogPath = "";
    private long _currentLogEnd;

    /// <summary>Corruption found in the write-ahead logs. Writing is refused when set.</summary>
    public string? LogCorruption { get; private set; }
    public bool ReadOnly { get; }
    public int TableCount => _tables.Count;
    public ulong LastSequence => _lastSequence;

    private LevelDbDatabase(string dbDir, bool readOnly)
    {
        _dbDir = dbDir;
        ReadOnly = readOnly;
        if (!readOnly)
        {
            // Holding the LOCK file exclusively prevents writing while Minecraft (or anything else) has the world open.
            try
            {
                _lock = new FileStream(Path.Combine(dbDir, "LOCK"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new IOException("ワールドのデータベースが使用中です。Minecraft を終了してから再試行してください。", ex);
            }
        }
        _manifest = ManifestState.Load(dbDir);
        _lastSequence = _manifest.LastSequence;

        foreach (var f in _manifest.Files.Values.OrderBy(f => f.Level).ThenByDescending(f => f.Number))
        {
            string path = TablePath(f.Number);
            _tables.Add((f, new Lazy<Table>(() => Table.Open(path))));
        }
        ReplayLogs();
    }

    public static LevelDbDatabase Open(string dbDir, bool readOnly = true)
    {
        if (!Directory.Exists(dbDir)) throw new DirectoryNotFoundException($"db フォルダがありません: {dbDir}");
        return new LevelDbDatabase(dbDir, readOnly);
    }

    private string TablePath(ulong number)
    {
        string ldb = Path.Combine(_dbDir, $"{number:000000}.ldb");
        if (File.Exists(ldb)) return ldb;
        string sst = Path.Combine(_dbDir, $"{number:000000}.sst");
        if (File.Exists(sst)) return sst;
        throw new FileNotFoundException($"テーブルファイル {number:000000}.ldb がありません。");
    }

    private void ReplayLogs()
    {
        var logs = Directory.EnumerateFiles(_dbDir, "*.log")
            .Select(p => (Path: p, Ok: ulong.TryParse(Path.GetFileNameWithoutExtension(p), out var n), Number: n))
            .Where(l => l.Ok && (l.Number >= _manifest.LogNumber || l.Number == _manifest.PrevLogNumber))
            .OrderBy(l => l.Number)
            .ToList();

        foreach (var log in logs)
        {
            var data = File.ReadAllBytes(log.Path);
            var read = LogFile.ReadRecords(data);
            if (read.Corruption != null) LogCorruption ??= $"{Path.GetFileName(log.Path)}: {read.Corruption}";
            foreach (var rec in read.Records)
            {
                var (seq, ops) = WriteBatch.Decode(rec);
                ulong s = seq;
                foreach (var (key, value) in ops)
                {
                    var entry = new InternalEntry(key, s, value is null, value ?? []);
                    if (!_memtable.TryGetValue(key, out var old) || old.Sequence < s) _memtable[key] = entry;
                    s++;
                }
                if (ops.Count > 0) _lastSequence = Math.Max(_lastSequence, seq + (ulong)ops.Count - 1);
            }
            _currentLogPath = log.Path;
            _currentLogEnd = read.LogicalEnd;
        }
        if (_currentLogPath == "")
        {
            _currentLogPath = Path.Combine(_dbDir, $"{_manifest.LogNumber:000000}.log");
            _currentLogEnd = 0;
        }
    }

    public byte[]? Get(string key) => Get(Encoding.ASCII.GetBytes(key));

    public byte[]? Get(byte[] key)
    {
        InternalEntry? best = _memtable.TryGetValue(key, out var m) ? m : null;
        foreach (var (info, table) in _tables)
        {
            if (!InRange(info, key)) continue;
            foreach (var e in table.Value.Get(key))
                if (best is null || e.Sequence > best.Value.Sequence) best = e;
        }
        return best is { IsDeletion: false } b ? b.Value : null;
    }

    public bool Contains(string key) => Get(key) != null;

    /// <summary>All live (non-deleted) key/value pairs whose key starts with the prefix, in key order.</summary>
    public List<KeyValuePair<byte[], byte[]>> Scan(string prefix) => Scan(Encoding.ASCII.GetBytes(prefix));

    public List<KeyValuePair<byte[], byte[]>> Scan(byte[] prefix)
    {
        var latest = new Dictionary<byte[], InternalEntry>(ByteArrayComparer.Instance);
        void Offer(InternalEntry e)
        {
            if (!latest.TryGetValue(e.UserKey, out var old) || old.Sequence < e.Sequence) latest[e.UserKey] = e;
        }
        foreach (var (info, table) in _tables)
        {
            if (!RangeMayContainPrefix(info, prefix)) continue;
            foreach (var e in table.Value.Scan(prefix)) Offer(e);
        }
        foreach (var e in _memtable.Values)
            if (e.UserKey.AsSpan().StartsWith(prefix)) Offer(e);
        return latest.Values.Where(e => !e.IsDeletion)
            .OrderBy(e => e.UserKey, ByteArrayComparer.Instance)
            .Select(e => new KeyValuePair<byte[], byte[]>(e.UserKey, e.Value))
            .ToList();
    }

    private static bool InRange(TableFileInfo info, byte[] key)
    {
        var cmp = ByteArrayComparer.Instance;
        return cmp.Compare(Table.UserKey(info.Smallest), key) <= 0 && cmp.Compare(Table.UserKey(info.Largest), key) >= 0;
    }

    private static bool RangeMayContainPrefix(TableFileInfo info, byte[] prefix)
    {
        var cmp = ByteArrayComparer.Instance;
        var largest = Table.UserKey(info.Largest);
        var smallest = Table.UserKey(info.Smallest);
        if (cmp.Compare(largest, prefix) < 0) return false;
        if (cmp.Compare(smallest, prefix) > 0 && !smallest.AsSpan().StartsWith(prefix)) return false;
        return true;
    }

    /// <summary>Appends the batch to the write-ahead log and flushes it to disk.</summary>
    public void Write(WriteBatch batch)
    {
        if (ReadOnly) throw new InvalidOperationException("読み取り専用で開いています。");
        if (LogCorruption != null) throw new InvalidDataException($"ログが破損しているため書き込みを中止しました: {LogCorruption}");
        if (batch.Count == 0) return;
        ulong seq = _lastSequence + 1;
        var rep = batch.Encode(seq);
        using (var fs = new FileStream(_currentLogPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            LogFile.AppendRecord(fs, rep, _currentLogEnd);
            fs.Flush(true);
            _currentLogEnd = fs.Length;
        }
        ulong s = seq;
        foreach (var (key, value) in batch.Ops)
        {
            _memtable[key] = new InternalEntry(key, s++, value is null, value ?? []);
        }
        _lastSequence = s - 1;
    }

    public void Dispose() => _lock?.Dispose();
}
