using System.Text.Json;
using BMAG.Palette;

namespace BMAG.BlockEngine;

/// <summary>
/// The user's block choice: at most one block per colour set (the MapartCraft model).
/// Sets without an entry are not used.
/// </summary>
public sealed record PaletteSelection
{
    /// <summary>Colour set id → chosen block key.</summary>
    public Dictionary<int, string> Blocks { get; init; } = [];

    /// <summary>Upper bound for the number of different blocks in the result (0 = unlimited).</summary>
    public int MaxBlockTypes { get; init; }

    public List<PaletteEntry> Resolve(ColorProfile profile)
    {
        var list = new List<PaletteEntry>();
        foreach (var set in profile.ColourSets)
        {
            if (!Blocks.TryGetValue(set.Id, out var key)) continue;
            var block = set.Blocks.FirstOrDefault(b => b.Key == key);
            if (block is null) continue;
            list.Add(new PaletteEntry(block, set.Blocks.Where(b => b != block).ToList()));
        }
        return list;
    }

    public PaletteSelection With(int setId, string? key)
    {
        var copy = new Dictionary<int, string>(Blocks);
        if (key is null) copy.Remove(setId); else copy[setId] = key;
        return this with { Blocks = copy };
    }
}

public sealed record PaletteEntry(BlockDefinition Block, List<BlockDefinition> Alternatives);

/// <summary>A named block selection (MapartCraft "presets").</summary>
public sealed class BlockPreset
{
    public required string Name { get; init; }
    public string NameEn { get; init; } = "";
    public bool BuiltIn { get; init; }
    /// <summary>Colour set id → block key.</summary>
    public Dictionary<int, string> Blocks { get; init; } = [];

    public string LocalName(bool english) => english && NameEn.Length > 0 ? NameEn : Name;

    /// <summary>Display label in the current UI language (set by the UI).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string Label { get; set; } = "";

    public PaletteSelection ToSelection(int maxTypes = 0) => new() { Blocks = new(Blocks), MaxBlockTypes = maxTypes };

    private static readonly int[] GreyscaleSets = [2, 5, 7, 9, 12, 13, 20, 21, 28, 58];

    public static List<BlockPreset> BuiltIns(ColorProfile profile)
    {
        BlockPreset Make(string name, string en, Func<ColourSet, BlockDefinition?> pick)
        {
            var dict = new Dictionary<int, string>();
            foreach (var set in profile.ColourSets)
                if (pick(set) is { } b) dict[set.Id] = b.Key;
            return new BlockPreset { Name = name, NameEn = en, BuiltIn = true, Blocks = dict };
        }
        BlockDefinition? First(ColourSet s, Func<BlockDefinition, bool> ok) => s.Blocks.FirstOrDefault(b => b.DefaultEnabled && ok(b));

        return
        [
            Make("すべて（各色の先頭ブロック）", "Everything", s => First(s, _ => true)),
            Make("サバイバル向け（入手しやすい・落下しない）", "Survival friendly",
                s => First(s, b => !b.NeedsSupport && !b.Gravity && b.Rarity <= 1 && !b.BiomeTinted)),
            Make("支え不要のブロックのみ", "No support needed", s => First(s, b => !b.NeedsSupport)),
            Make("カーペット", "Carpets", s => s.Blocks.FirstOrDefault(b => b.Key.EndsWith("_carpet"))),
            Make("コンクリート", "Concrete", s => s.Blocks.FirstOrDefault(b => b.Key.EndsWith("_concrete"))),
            Make("羊毛", "Wool", s => s.Blocks.FirstOrDefault(b => b.Key.EndsWith("_wool"))),
            Make("グレースケール", "Greyscale", s => GreyscaleSets.Contains(s.Id) ? First(s, _ => true) : null),
            new BlockPreset { Name = "なし", NameEn = "None", BuiltIn = true },
        ];
    }

    public static string UserPresetFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BMAG", "presets.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static List<BlockPreset> LoadUser()
    {
        try
        {
            if (!File.Exists(UserPresetFile)) return [];
            return JsonSerializer.Deserialize<List<BlockPreset>>(File.ReadAllText(UserPresetFile), Options) ?? [];
        }
        catch { return []; }
    }

    public static void SaveUser(IEnumerable<BlockPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UserPresetFile)!);
        File.WriteAllText(UserPresetFile, JsonSerializer.Serialize(presets.Where(p => !p.BuiltIn).ToList(), Options));
    }
}
