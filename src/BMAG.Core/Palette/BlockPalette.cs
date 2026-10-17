using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BMAG.Imaging;
using BMAG.Nbt;

namespace BMAG.Palette;

/// <summary>Map shading caused by the height difference to the block north of a pixel.</summary>
public enum Shade
{
    /// <summary>Block is lower than its northern neighbour.</summary>
    Dark = 0,
    /// <summary>Same height (flat map art).</summary>
    Normal = 1,
    /// <summary>Block is higher than its northern neighbour.</summary>
    Bright = 2,
}

/// <summary>One block usable in map art (design 6.2 / 13.3).</summary>
public sealed class BlockDefinition
{
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public string Category { get; init; } = "other";

    /// <summary>Block identifier written to structures (legacy identifiers are upgraded by the game based on <see cref="Version"/>).</summary>
    public required string Name { get; init; }
    public Dictionary<string, JsonElement> States { get; init; } = [];
    /// <summary>Block palette "version" written to .mcstructure files.</summary>
    public int Version { get; init; } = 17959425;

    /// <summary>Base map colour (before shading).</summary>
    public required string MapColor { get; init; }

    /// <summary>Colours measured in-game per shade (filled by calibration). Override the computed ones.</summary>
    public Dictionary<Shade, string> Measured { get; init; } = [];

    public string Source { get; init; } = "java-base-color";
    public bool Verified { get; init; }

    public bool Gravity { get; init; }
    public bool Flammable { get; init; }
    public bool BiomeTinted { get; init; }
    /// <summary>0 = common ... 3 = very rare.</summary>
    public int Rarity { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public int MaxStack { get; init; } = 64;

    [JsonIgnore] public Rgba BaseColor => Rgba.FromHex(MapColor);

    public string StatesText => States.Count == 0 ? "" :
        "[" + string.Join(",", States.Select(kv => $"\"{kv.Key}\"={kv.Value.GetRawText()}")) + "]";

    /// <summary>Converts JSON states to NBT: strings stay strings, numbers become ints, booleans become bytes.</summary>
    public NbtCompound StatesToNbt()
    {
        var c = new NbtCompound();
        foreach (var (k, v) in States)
        {
            c.Add(k, v.ValueKind switch
            {
                JsonValueKind.String => new NbtString(v.GetString()!),
                JsonValueKind.Number => new NbtInt(v.GetInt32()),
                JsonValueKind.True => new NbtByte(1),
                JsonValueKind.False => new NbtByte(0),
                _ => throw new InvalidDataException($"Unsupported state value for {Key}.{k}"),
            });
        }
        return c;
    }
}

public sealed class ShadeMultipliers
{
    public int Dark { get; init; } = 180;
    public int Normal { get; init; } = 220;
    public int Bright { get; init; } = 255;
    public bool Verified { get; init; }

    public int Get(Shade s) => s switch { Shade.Dark => Dark, Shade.Bright => Bright, _ => Normal };
}

/// <summary>A versioned Bedrock map colour profile (design 3.3 / 13.6).</summary>
public sealed class ColorProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string GameVersion { get; init; } = "";
    public string Updated { get; init; } = "";
    public string Notes { get; init; } = "";
    public ShadeMultipliers Shades { get; init; } = new();
    public List<BlockDefinition> Blocks { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public BlockDefinition? Find(string key) => Blocks.FirstOrDefault(b => b.Key == key);

    /// <summary>The map colour a block shows with the given shade. Measured values win over computed ones.</summary>
    public Rgba ColorOf(BlockDefinition block, Shade shade)
    {
        if (block.Measured.TryGetValue(shade, out var hex)) return Rgba.FromHex(hex);
        var c = block.BaseColor;
        int m = Shades.Get(shade);
        return new Rgba((byte)(c.R * m / 255), (byte)(c.G * m / 255), (byte)(c.B * m / 255));
    }

    /// <summary>True when the colour for this block/shade comes from an in-game measurement.</summary>
    public static bool IsMeasured(BlockDefinition block, Shade shade) => block.Measured.ContainsKey(shade);

    public static ColorProfile LoadJson(string json) =>
        JsonSerializer.Deserialize<ColorProfile>(json, JsonOptions) ?? throw new InvalidDataException("Empty profile.");

    public static ColorProfile LoadFile(string path) => LoadJson(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static IReadOnlyList<string> BuiltInIds() =>
        typeof(ColorProfile).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".json") && n.Contains("Palettes"))
            .Select(n => n.Split('.')[^2])
            .ToList();

    /// <summary>The default estimated profile shipped with the app.</summary>
    public static ColorProfile LoadBuiltIn(string id = "bedrock-estimate-v1")
    {
        var asm = typeof(ColorProfile).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith($".{id}.json"))
                   ?? throw new FileNotFoundException($"Built-in profile '{id}' not found.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(s);
        return LoadJson(reader.ReadToEnd());
    }

    /// <summary>Returns a copy where calibration measurements are applied and marked as verified.</summary>
    public ColorProfile WithMeasurements(IReadOnlyDictionary<string, Dictionary<Shade, string>> measurements, string newId, string newName)
    {
        var json = JsonNode.Parse(ToJson())!;
        var copy = JsonSerializer.Deserialize<ColorProfile>(json, JsonOptions)!;
        var blocks = copy.Blocks.Select(b =>
        {
            if (!measurements.TryGetValue(b.Key, out var m) || m.Count == 0) return b;
            var merged = new Dictionary<Shade, string>(b.Measured);
            foreach (var (shade, hex) in m) merged[shade] = hex;
            return new BlockDefinition
            {
                Key = b.Key, DisplayName = b.DisplayName, Category = b.Category, Name = b.Name, States = b.States,
                Version = b.Version, MapColor = b.MapColor, Measured = merged, Source = "in-game-calibration",
                Verified = true, Gravity = b.Gravity, Flammable = b.Flammable, BiomeTinted = b.BiomeTinted,
                Rarity = b.Rarity, DefaultEnabled = b.DefaultEnabled, MaxStack = b.MaxStack,
            };
        }).ToList();
        return new ColorProfile
        {
            Id = newId, Name = newName, GameVersion = GameVersion, Updated = DateTime.Now.ToString("yyyy-MM-dd"),
            Notes = Notes, Shades = Shades, Blocks = blocks,
        };
    }
}

public static class BlockCategories
{
    public static readonly IReadOnlyDictionary<string, string> Names = new Dictionary<string, string>
    {
        ["wool"] = "羊毛",
        ["concrete"] = "コンクリート",
        ["terracotta"] = "テラコッタ",
        ["wood"] = "木材",
        ["stone"] = "石材",
        ["natural"] = "自然ブロック",
        ["nether"] = "ネザー",
        ["mineral"] = "鉱石・金属",
        ["other"] = "その他",
    };

    public static string Display(string category) => Names.TryGetValue(category, out var n) ? n : category;
}
