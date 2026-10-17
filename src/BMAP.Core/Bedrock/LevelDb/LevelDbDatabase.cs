using System.Text;

namespace BMAP.Bedrock.LevelDb;

/// <summary>
/// Read-only LevelDB reader for Bedrock worlds: MANIFEST + live tables + write-ahead logs, newest sequence wins.
/// BMAP never writes to worlds. Use <see cref="OpenSnapshot"/> so that only a temporary copy of the database is read.
/// </summary>
public sealed class LevelDbDatabase : IDisposable
{
    private readonly string _dbDir;
    private readonly ManifestState _manifest;
    private readonly List<(TableFileInfo Info, Lazy<Table> Table)> _tables = [];
    private readonly Dictionary<byte[], InternalEntry> _memtable = new(ByteArrayComparer.Instance);
    private readonly string? _snapshotDir;

    /// <summary>Corruption found in the write-ahead logs (a torn tail after a crash also shows up here).</summary>
    public string? LogCorruption { get; private set; }
    public int TableCount => _tables.Count;

    private LevelDbDatabase(string dbDir, string? snapshotDir)
    {
        _dbDir = dbDir;
        _snapshotDir = snapshotDir;
        _manifest = ManifestState.Load(dbDir);
        foreach (var f in _manifest.Files.Values.OrderBy(f => f.Level).ThenByDescending(f => f.Number))
        {
            string path = TablePath(f.Number);
            _tables.Add((f, new Lazy<Table>(() => Table.Open(path))));
        }
        ReplayLogs();
    }

    /// <summary>Opens a database folder for reading. Files are opened read-only and shared.</summary>
    public static LevelDbDatabase Open(string dbDir)
    {
        if (!Directory.Exists(dbDir)) throw new DirectoryNotFoundException($"db folder not found: {dbDir}");
        return new LevelDbDatabase(dbDir, null);
    }

    /// <summary>
    /// Copies the world's db folder to a temporary folder and opens the copy, so the original files are only ever read
    /// once, sequentially. The copy is deleted on <see cref="Dispose"/>.
    /// </summary>
    public static LevelDbDatabase OpenSnapshot(string worldFolder)
    {
        string db = Path.Combine(worldFolder, "db");
        if (!Directory.Exists(db)) throw new DirectoryNotFoundException($"db folder not found: {db}");
        string tmp = Path.Combine(Path.GetTempPath(), "BMAP-world-read", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in Directory.EnumerateFiles(db))
            {
                if (Path.GetFileName(f).Equals("LOCK", StringComparison.OrdinalIgnoreCase)) continue;
                using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = File.Create(Path.Combine(tmp, Path.GetFileName(f)));
                src.CopyTo(dst);
            }
            return new LevelDbDatabase(tmp, tmp);
        }
        catch
        {
            try { Directory.Delete(tmp, true); } catch { }
            throw;
        }
    }

    private string TablePath(ulong number)
    {
        string ldb = Path.Combine(_dbDir, $"{number:000000}.ldb");
        if (File.Exists(ldb)) return ldb;
        string sst = Path.Combine(_dbDir, $"{number:000000}.sst");
        if (File.Exists(sst)) return sst;
        throw new FileNotFoundException($"table file {number:000000}.ldb is missing.");
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
            var read = LogFile.ReadRecords(File.ReadAllBytes(log.Path));
            if (read.Corruption != null) LogCorruption ??= $"{Path.GetFileName(log.Path)}: {read.Corruption}";
            foreach (var rec in read.Records)
            {
                var (seq, ops) = LogBatch.Decode(rec);
                ulong s = seq;
                foreach (var (key, value) in ops)
                {
                    var entry = new InternalEntry(key, s, value is null, value ?? []);
                    if (!_memtable.TryGetValue(key, out var old) || old.Sequence < s) _memtable[key] = entry;
                    s++;
                }
            }
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

    public void Dispose()
    {
        if (_snapshotDir is null) return;
        try { Directory.Delete(_snapshotDir, true); } catch { /* temp folder, cleaned later by the OS */ }
    }
}