using BMAG.Imaging;
using BMAG.Nbt;

namespace BMAG.MapData;

/// <summary>A Bedrock map record ("map_&lt;id&gt;" in LevelDB).</summary>
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
    public long ParentMapId => Nbt.GetLong("parentMapId") ?? -1;
    public byte[]? Colors => Nbt.Get<NbtByteArray>("colors")?.Value;

    public string Key => MapDataEncoder.KeyFor(Id);

    /// <summary>The RGBA image stored in the map, or null when the colour data is missing or malformed.</summary>
    public RgbaImage? ToImage()
    {
        var c = Colors;
        int w = Width, h = Height;
        if (c is null || w <= 0 || h <= 0 || c.Length != w * h * 4) return null;
        return new RgbaImage(w, h, (byte[])c.Clone());
    }

    public static MapRecord Parse(long id, byte[] value) => new() { Id = id, Nbt = NbtIO.Read(value) };
}

/// <summary>
/// Turns a 128x128 RGBA tile into Bedrock map NBT (MapDataEncoder, design 9.2).
/// Field set follows Image Map's proven Bedrock 1.13+ template: a locked scale-4 map centred far outside the
/// world (Int32.MaxValue) so the game never redraws it from terrain.
/// </summary>
public static class MapDataEncoder
{
    public const int PixelBytes = MapTile.Size * MapTile.Size * 4; // 65,536

    public static string KeyFor(long id) => $"map_{id}";

    public static bool TryParseKey(string key, out long id)
    {
        id = 0;
        return key.StartsWith("map_") && long.TryParse(key.AsSpan(4), out id);
    }

    public static byte[] EncodeColors(RgbaImage tile)
    {
        if (tile.Width != MapTile.Size || tile.Height != MapTile.Size)
            throw new ArgumentException($"地図タイルは 128x128 である必要があります（{tile.Width}x{tile.Height}）。");
        var bytes = (byte[])tile.Pixels.Clone(); // byte order R, G, B, A per pixel, row-major from the north-west corner
        if (bytes.Length != PixelBytes) throw new InvalidDataException("RGBA データのサイズが不正です。");
        return bytes;
    }

    public static NbtCompound CreateMapNbt(long id, RgbaImage tile, VersionProfile profile)
    {
        var colors = EncodeColors(tile);
        return new NbtCompound
        {
            { "mapId", new NbtLong(id) },
            { "parentMapId", new NbtLong(-1) },
            { "dimension", new NbtByte(0) },
            { "fullyExplored", new NbtByte(1) },
            { "mapLocked", new NbtByte(1) },
            { "scale", new NbtByte((byte)profile.MapScale) },
            { "height", new NbtShort(128) },
            { "width", new NbtShort(128) },
            { "unlimitedTracking", new NbtByte(0) },
            { "xCenter", new NbtInt(int.MaxValue) },
            { "zCenter", new NbtInt(int.MaxValue) },
            { "decorations", new NbtList(NbtType.Compound) },
            { "colors", new NbtByteArray(colors) },
        };
    }

    public static byte[] Serialize(NbtCompound map) => NbtIO.Write(map);
}

/// <summary>Version dependent formats (design 13.6). Only profiles listed here allow writing.</summary>
public sealed record VersionProfile
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>Minimum game version (lastOpenedWithVersion) this profile applies to.</summary>
    public required int[] MinVersion { get; init; }
    public int MapScale { get; init; } = 4;
    public string MapItemName { get; init; } = "minecraft:filled_map";
    public bool SupportsGlowFrames { get; init; } = true;
    /// <summary>Block palette version used for chests / frames in generated structures.</summary>
    public int StructureBlockVersion { get; init; } = 17959425;
    /// <summary>True only after the profile has been confirmed in the real game.</summary>
    public bool VerifiedInGame { get; init; }

    public static readonly VersionProfile Bedrock_1_16_210 = new()
    {
        Id = "bedrock-1.16.210+",
        Label = "Bedrock 1.16.210 以降（地図 NBT は Image Map 互換・BMAG では実機未検証）",
        MinVersion = [1, 16, 210],
    };

    public static readonly IReadOnlyList<VersionProfile> All = [Bedrock_1_16_210];

    /// <summary>Picks the newest profile not newer than the world. Unknown/old worlds get null = writing disabled (design 10.4).</summary>
    public static VersionProfile? ForWorld(int[] worldVersion)
    {
        if (worldVersion.Length < 2) return null;
        return All.Where(p => CompareVersion(worldVersion, p.MinVersion) >= 0)
                  .OrderByDescending(p => p.MinVersion, Comparer<int[]>.Create(CompareVersion))
                  .FirstOrDefault();
    }

    public static int CompareVersion(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }
}
