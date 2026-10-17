using BMAP.Imaging;
using BMAP.Nbt;

namespace BMAP.Bedrock;

/// <summary>A Bedrock map record ("map_&lt;id&gt;" in LevelDB), read only.</summary>
public sealed class MapRecord
{
    public required long Id { get; init; }
    public required NbtCompound Nbt { get; init; }

    public int Width => Nbt.GetInt("width") ?? 128;
    public int Height => Nbt.GetInt("height") ?? 128;
    public int Scale => Nbt.GetInt("scale") ?? 0;
    public bool Locked => Nbt.GetInt("mapLocked") == 1;
    public int Dimension => Nbt.GetInt("dimension") ?? 0;
    public int XCenter => Nbt.GetInt("xCenter") ?? 0;
    public int ZCenter => Nbt.GetInt("zCenter") ?? 0;
    public byte[]? Colors => Nbt.Get<NbtByteArray>("colors")?.Value;

    /// <summary>World X / Z of the map's north-west pixel (scale 0: one block per pixel).</summary>
    public int MinX => XCenter - Width / 2 * (1 << Scale);
    public int MinZ => ZCenter - Height / 2 * (1 << Scale);

    /// <summary>The RGBA image stored in the map (row-major from the north-west corner), or null when malformed.</summary>
    public RgbaImage? ToImage()
    {
        var c = Colors;
        int w = Width, h = Height;
        if (c is null || w <= 0 || h <= 0 || c.Length != w * h * 4) return null;
        return new RgbaImage(w, h, (byte[])c.Clone());
    }

    public static MapRecord Parse(long id, byte[] value) => new() { Id = id, Nbt = NbtIO.Read(value) };

    public static bool TryParseKey(string key, out long id)
    {
        id = 0;
        return key.StartsWith("map_") && long.TryParse(key.AsSpan(4), out id);
    }

    /// <summary>All map records of a world, read from a temporary copy of its database.</summary>
    public static List<MapRecord> ReadAll(string worldFolder)
    {
        using var db = LevelDb.LevelDbDatabase.OpenSnapshot(worldFolder);
        var list = new List<MapRecord>();
        foreach (var kv in db.Scan("map_"))
        {
            if (!TryParseKey(System.Text.Encoding.ASCII.GetString(kv.Key), out long id)) continue;
            try { list.Add(Parse(id, kv.Value)); } catch { /* skip unreadable records */ }
        }
        return list;
    }
}