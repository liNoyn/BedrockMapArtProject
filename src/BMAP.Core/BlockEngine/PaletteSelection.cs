using System.Text.Json;
using System.Text.Json.Serialization;
using BMAP.Palette;

namespace BMAP.BlockEngine;

/// <summary>How the work is shared when several selected blocks produce the same map colour.</summary>
public enum BlockMixing
{
    /// <summary>Equal shares in reading order (map by map, row by row): each block covers one connected stretch.</summary>
    Balanced,
    /// <summary>Equal shares scattered pixel by pixel (looks like a mix of materials in the build).</summary>
    Mixed,
}

/// <summary>
/// The user's block choice: any number of blocks, from any colour sets. Blocks that show the same map colour are
/// matched as one colour and then share its pixels according to <see cref="Mixing"/>.
/// </summary>
public sealed record PaletteSelection
{
    /// <summary>Selected block keys.</summary>
    public HashSet<string> Keys { get; init; } = [];

    /// <summary>Upper bound for the number of different map colours in the result (0 = unlimited).</summary>
    public int MaxBlockTypes { get; init; }

    public BlockMixing Mixing { get; init; } = BlockMixing.Balanced;

    /// <summary>
    /// One entry per reachable map colour. Entries list the selected blocks sharing that colour in
    /// <see cref="PaletteEntry.Members"/>; unselected blocks of the set are <see cref="PaletteEntry.Alternatives"/>.
    /// </summary>
    public List<PaletteEntry> Resolve(ColorProfile profile)
    {
        var list = new List<PaletteEntry>();
        foreach (var set in profile.ColourSets)
        {
            var chosen = set.Blocks.Where(b => Keys.Contains(b.Key)).ToList();
            if (chosen.Count == 0) continue;
            var others = set.Blocks.Where(b => !Keys.Contains(b.Key)).ToList();
            // Calibrated blocks may differ in colour: only blocks that look identical share one entry.
            foreach (var group in chosen.GroupBy(b => string.Join("|", Enum.GetValues<Shade>().Select(s => profile.ColorOf(b, s).ToHex()))))
            {
                var members = group.ToList();
                list.Add(new PaletteEntry(members[0], others, members));
            }
        }
        return list;
    }

    public PaletteSelection With(string key, bool selected)
    {
        var copy = new HashSet<string>(Keys);
        if (selected) copy.Add(key); else copy.Remove(key);
        return this with { Keys = copy };
    }
}

/// <summary>A reachable map colour: the block that represents it, the selected blocks sharing it, and alternatives.</summary>
public sealed record PaletteEntry(BlockDefinition Block, List<BlockDefinition> Alternatives, List<BlockDefinition>? Members = null)
{
    public IReadOnlyList<BlockDefinition> AllMembers => Members is { Count: > 0 } ? Members : [Block];
}

/// <summary>A named block selection (MapartCraft "presets").</summary>
public sealed class BlockPreset
{
    public required string Name { get; init; }
    public string NameEn { get; init; } = "";
    public bool BuiltIn { get; init; }
    /// <summary>Selected block keys.</summary>
    public List<string> Keys { get; init; } = [];

    /// <summary>Old preset files (one block per colour set). Read only, merged into <see cref="Keys"/>.</summary>
    [JsonPropertyName("Blocks")]
    public Dictionary<int, string>? LegacyBlocks
    {
        get => null;
        init { if (value != null) Keys.AddRange(value.Values.Where(v => !Keys.Contains(v))); }
    }

    public string LocalName(bool english) => english && NameEn.Length > 0 ? NameEn : Name;

    /// <summary>Display label in the current UI language (set by the UI).</summary>
    [JsonIgnore] public string Label { get; set; } = "";

    public PaletteSelection ToSelection(int maxTypes = 0, BlockMixing mixing = BlockMixing.Balanced) =>
        new() { Keys = [.. Keys], MaxBlockTypes = maxTypes, Mixing = mixing };

    private static readonly int[] GreyscaleSets = [2, 5, 7, 9, 12, 13, 20, 21, 28, 58];

    public static List<BlockPreset> BuiltIns(ColorProfile profile)
    {
        BlockPreset Make(string name, string en, Func<ColourSet, IEnumerable<BlockDefinition>> pick) => new()
        {
            Name = name, NameEn = en, BuiltIn = true,
            Keys = profile.ColourSets.SelectMany(pick).Select(b => b.Key).Distinct().ToList(),
        };
        IEnumerable<BlockDefinition> First(ColourSet s, Func<BlockDefinition, bool> ok) =>
            s.Blocks.Where(b => b.DefaultEnabled && ok(b)).Take(1);
        IEnumerable<BlockDefinition> Ending(ColourSet s, string suffix) => s.Blocks.Where(b => b.Key.EndsWith(suffix)).Take(1);

        return
        [
            // First (default): every colour set, with a block that needs no support where there is one — the same
            // colours and quality as "one block per colour" on the test corpus, but fewer support blocks to place.
            Make("おすすめ（支え不要・全色）", "Recommended (no support, all colours)",
                s => First(s, b => !b.NeedsSupport && !b.Gravity).Concat(First(s, _ => true)).Take(1)),
            Make("各色 1 ブロック（先頭）", "One block per colour", s => First(s, _ => true)),
            Make("サバイバル向け（入手しやすい・落下しない）", "Survival friendly",
                s => First(s, b => !b.NeedsSupport && !b.Gravity && b.Rarity <= 1 && !b.BiomeTinted)),
            Make("サバイバル向け・全部（同じ色は分担）", "Survival friendly, all blocks (shared)",
                s => s.Blocks.Where(b => b.DefaultEnabled && !b.NeedsSupport && !b.Gravity && b.Rarity <= 1 && !b.BiomeTinted)),
            Make("支え不要のブロックのみ", "No support needed", s => First(s, b => !b.NeedsSupport)),
            Make("すべてのブロック", "All blocks", s => s.Blocks.Where(b => b.DefaultEnabled)),
            Make("カーペット", "Carpets", s => Ending(s, "_carpet")),
            Make("コンクリート", "Concrete", s => Ending(s, "_concrete")),
            Make("羊毛", "Wool", s => Ending(s, "_wool")),
            Make("グレースケール", "Greyscale", s => GreyscaleSets.Contains(s.Id) ? First(s, _ => true) : []),
            new BlockPreset { Name = "なし", NameEn = "None", BuiltIn = true },
        ];
    }

    public static string UserPresetFile =>
        Path.Combine(AppPaths.Roaming, "presets.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

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
