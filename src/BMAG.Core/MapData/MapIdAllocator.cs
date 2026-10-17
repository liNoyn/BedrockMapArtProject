using System.Text;
using BMAG.Bedrock.LevelDb;

namespace BMAG.MapData;

/// <summary>Finds used map IDs and hands out free ones (MapIdAllocator, design 9.4).</summary>
public sealed class MapIdAllocator
{
    private readonly HashSet<long> _used;

    public MapIdAllocator(IEnumerable<long> used) => _used = [.. used];

    public IReadOnlySet<long> Used => _used;

    public static MapIdAllocator FromDatabase(LevelDbDatabase db)
    {
        var ids = new List<long>();
        foreach (var kv in db.Scan("map_"))
            if (MapDataEncoder.TryParseKey(Encoding.ASCII.GetString(kv.Key), out long id)) ids.Add(id);
        return new MapIdAllocator(ids);
    }

    /// <summary>Default start of BMAG's ID range: far away from small IDs other tools tend to use.</summary>
    public const long DefaultStart = 1_000_000_000_000L;

    /// <summary>Allocates <paramref name="count"/> IDs that are not used, searching upward from <paramref name="start"/>.</summary>
    public List<long> Allocate(int count, long start = DefaultStart, bool consecutive = true)
    {
        var result = new List<long>(count);
        long candidate = start;
        while (result.Count < count)
        {
            if (consecutive && result.Count > 0 && _used.Contains(candidate))
            {
                // Restart the run so the whole artwork gets an unbroken range.
                result.Clear();
            }
            if (!_used.Contains(candidate)) result.Add(candidate);
            candidate = checked(candidate + 1);
        }
        foreach (var id in result) _used.Add(id);
        return result;
    }
}
