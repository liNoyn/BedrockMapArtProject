using System.Text.Json;
using System.Text.Json.Serialization;
using BMAP.Imaging;
using BMAP.Nbt;

namespace BMAP.Palette;

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
    /// <summary>Japanese display name.</summary>
    public required string DisplayName { get; init; }
    public string DisplayNameEn { get; init; } = "";

    /// <summary>Current Bedrock block identifier.</summary>
    public required string Name { get; init; }
    /// <summary>Complete block state set (values: string, number or bool).</summary>
    public Dictionary<string, JsonElement> States { get; init; } = [];
    /// <summary>Block palette "version" written to .mcstructure files.</summary>
    public int Version { get; init; } = 18168865;

    /// <summary>Base map colour (before shading). Filled from the colour set when loading.</summary>
    public string MapColor { get; set; } = "";
    [JsonIgnore] public int ColourSetId { get; set; }

    /// <summary>Colours measured in-game per shade (filled by calibration). Override the computed ones.</summary>
    public Dictionary<Shade, string> Measured { get; init; } = [];

    public string Source { get; init; } = "java-base-color";
    public bool Verified { get; init; }

    /// <summary>Needs a block underneath to stay in place (carpets, slabs on air, pressure plates, falling blocks…).</summary>
    public bool NeedsSupport { get; init; }
    /// <summary>Falls when nothing is below (sand, gravel, concrete powder).</summary>
    public bool Gravity { get; init; }
    public bool Flammable { get; init; }
    public bool BiomeTinted { get; init; }
    /// <summary>0 = common ... 3 = very rare.</summary>
    public int Rarity { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public int MaxStack { get; init; } = 64;
    /// <summary>True for blocks the user added (custom / behavior pack blocks).</summary>
    public bool IsCustom { get; init; }

    [JsonIgnore] public Rgba BaseColor => Rgba.FromHex(MapColor);

    public string LocalName(bool english) => english && DisplayNameEn.Length > 0 ? DisplayNameEn : DisplayName;

    /// <summary>Display label in the current UI language (set by the UI).</summary>
    [JsonIgnore] public string Label { get; set; } = "";

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

    internal BlockDefinition With(Dictionary<Shade, string> measured, string source, bool verified) => new()
    {
        Key = Key, DisplayName = DisplayName, DisplayNameEn = DisplayNameEn, Name = Name, States = States,
        Version = Version, MapColor = MapColor, ColourSetId = ColourSetId, Measured = measured, Source = source,
        Verified = verified, NeedsSupport = NeedsSupport, Gravity = Gravity, Flammable = Flammable,
        BiomeTinted = BiomeTinted, Rarity = Rarity, DefaultEnabled = DefaultEnabled, MaxStack = MaxStack, IsCustom = IsCustom,
    };
}

/// <summary>All blocks that share one map colour (MapartCraft calls this a "colour set").</summary>
public sealed class ColourSet
{
    public required int Id { get; init; }
    /// <summary>Japanese name.</summary>
    public required string Name { get; init; }
    public string NameEn { get; init; } = "";
    public required string MapColor { get; init; }
    public List<BlockDefinition> Blocks { get; init; } = [];
    public bool IsCustom { get; init; }

    public string LocalName(bool english) => english && NameEn.Length > 0 ? NameEn : Name;

    /// <summary>Display label in the current UI language (set by the UI).</summary>
    [JsonIgnore] public string Label { get; set; } = "";
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
    public int FormatVersion { get; init; } = 2;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string GameVersion { get; init; } = "";
    public string Updated { get; init; } = "";
    public string Notes { get; init; } = "";
    public ShadeMultipliers Shades { get; init; } = new();
    public List<ColourSet> ColourSets { get; init; } = [];

    [JsonIgnore] public IEnumerable<BlockDefinition> Blocks => ColourSets.SelectMany(s => s.Blocks);

    private Dictionary<string, BlockDefinition>? _byKey;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public BlockDefinition? Find(string key)
    {
        _byKey ??= Blocks.GroupBy(b => b.Key).ToDictionary(g => g.Key, g => g.First());
        return _byKey.TryGetValue(key, out var b) ? b : null;
    }

    public ColourSet? SetOf(BlockDefinition block) => ColourSets.FirstOrDefault(s => s.Id == block.ColourSetId);

    /// <summary>The map colour a block shows with the given shade. Measured values win over computed ones.</summary>
    public Rgba ColorOf(BlockDefinition block, Shade shade)
    {
        if (block.Measured.TryGetValue(shade, out var hex)) return Rgba.FromHex(hex);
        return Shaded(block.BaseColor, shade);
    }

    public Rgba Shaded(Rgba c, Shade shade)
    {
        int m = Shades.Get(shade);
        return new Rgba((byte)(c.R * m / 255), (byte)(c.G * m / 255), (byte)(c.B * m / 255));
    }

    public static ColorProfile LoadJson(string json)
    {
        var p = JsonSerializer.Deserialize<ColorProfile>(json, JsonOptions) ?? throw new InvalidDataException("Empty profile.");
        if (p.FormatVersion < 2 || p.ColourSets.Count == 0)
            throw new InvalidDataException("古い形式の色プロファイルです（BMAP 0.1）。新しい形式で作り直してください。");
        p.Link();
        return p;
    }

    private void Link()
    {
        foreach (var set in ColourSets)
            foreach (var b in set.Blocks)
            {
                b.ColourSetId = set.Id;
                if (string.IsNullOrEmpty(b.MapColor)) b.MapColor = set.MapColor;
            }
        _byKey = null;
    }

    public static ColorProfile LoadFile(string path) => LoadJson(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static IReadOnlyList<string> BuiltInIds() =>
        typeof(ColorProfile).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".json") && n.Contains("Palettes"))
            .Select(n => n.Split('.')[^2])
            .ToList();

    public const string DefaultId = "bedrock-full-v2";

    /// <summary>The default estimated profile shipped with the app.</summary>
    public static ColorProfile LoadBuiltIn(string id = DefaultId)
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
        var sets = ColourSets.Select(s => new ColourSet
        {
            Id = s.Id, Name = s.Name, NameEn = s.NameEn, MapColor = s.MapColor, IsCustom = s.IsCustom,
            Blocks = s.Blocks.Select(b =>
            {
                if (!measurements.TryGetValue(b.Key, out var m) || m.Count == 0) return b;
                var merged = new Dictionary<Shade, string>(b.Measured);
                foreach (var (shade, hex) in m) merged[shade] = hex;
                return b.With(merged, "in-game-calibration", true);
            }).ToList(),
        }).ToList();
        var copy = new ColorProfile
        {
            Id = newId, Name = newName, GameVersion = GameVersion, Updated = DateTime.Now.ToString("yyyy-MM-dd"),
            Notes = Notes, Shades = Shades, ColourSets = sets,
        };
        copy.Link();
        return copy;
    }

    /// <summary>Returns a copy with user-defined blocks added (to an existing colour set or to new custom sets).</summary>
    public ColorProfile WithCustomBlocks(IEnumerable<CustomBlock> custom)
    {
        var sets = ColourSets.Select(s => new ColourSet
        {
            Id = s.Id, Name = s.Name, NameEn = s.NameEn, MapColor = s.MapColor, IsCustom = s.IsCustom, Blocks = [.. s.Blocks],
        }).ToList();
        int nextId = Math.Max(1000, sets.Max(s => s.Id) + 1);
        foreach (var c in custom)
        {
            if (Find(c.Key) != null) continue;
            var set = c.ColourSetId is int id ? sets.FirstOrDefault(s => s.Id == id) : null;
            if (set is null)
            {
                string color = c.MapColor ?? "#808080";
                set = sets.FirstOrDefault(s => s.IsCustom && s.MapColor.Equals(color, StringComparison.OrdinalIgnoreCase));
                if (set is null)
                {
                    set = new ColourSet { Id = nextId++, Name = $"カスタム {color}", NameEn = $"Custom {color}", MapColor = color, IsCustom = true };
                    sets.Add(set);
                }
            }
            set.Blocks.Add(c.ToDefinition(set));
        }
        var copy = new ColorProfile
        {
            Id = Id, Name = Name, GameVersion = GameVersion, Updated = Updated, Notes = Notes, Shades = Shades, ColourSets = sets,
        };
        copy.Link();
        return copy;
    }
}

/// <summary>A block added by the user (F24), e.g. from a behavior pack's minecraft:map_color component.</summary>
public sealed class CustomBlock
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string DisplayName { get; init; } = "";
    public Dictionary<string, JsonElement> States { get; init; } = [];
    /// <summary>Existing colour set to join; null = create/use a custom set for <see cref="MapColor"/>.</summary>
    public int? ColourSetId { get; init; }
    public string? MapColor { get; init; }
    public bool NeedsSupport { get; init; }
    public bool Gravity { get; init; }
    public string Source { get; init; } = "user";
    public int Version { get; init; } = 18168865;

    internal BlockDefinition ToDefinition(ColourSet set) => new()
    {
        Key = Key, DisplayName = string.IsNullOrEmpty(DisplayName) ? Name : DisplayName, DisplayNameEn = DisplayName,
        Name = Name, States = States, Version = Version, MapColor = set.MapColor, ColourSetId = set.Id,
        NeedsSupport = NeedsSupport, Gravity = Gravity, Source = Source, IsCustom = true,
    };
}
