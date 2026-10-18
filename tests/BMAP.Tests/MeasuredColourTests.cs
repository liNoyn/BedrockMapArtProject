using BMAP.Palette;

namespace BMAP.Tests;

/// <summary>The built-in palette against colours measured in game (palettes/measured).</summary>
public class MeasuredColourTests
{
    [Fact]
    public void Palette_MatchesTheColourPadMeasuredOnBedrock_1_26_51()
    {
        var profile = ColorProfile.LoadBuiltIn();
        string file = Path.Combine(TestHelpers.RepoRoot(), "palettes", "measured", "colour-pad-1.26.51.csv");
        // Excluded on purpose: blocks removed from the palette (not drawn on maps, TNT), biome-tinted blocks (their colour
        // depends on where they stand) and samples a TNT explosion destroyed in the measured pad (redstone normal / dark,
        // half of the stone and ice samples — the most frequent colour of those is the crater).
        string[] crater = ["redstone_block/Normal", "redstone_block/Dark", "stone/Normal", "ice/Dark"];
        int checkedCount = 0;
        var wrong = new List<string>();
        foreach (var line in File.ReadAllLines(file).Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1))
        {
            var p = line.Split(',');
            var block = profile.Find(p[0]);
            if (block is null || block.BiomeTinted || crater.Contains($"{p[0]}/{p[1]}")) continue;
            var c = profile.ColorOf(block, Enum.Parse<Shade>(p[1]));
            if (c.ToHex() != p[2]) wrong.Add($"{p[0]} {p[1]}: palette {c.ToHex()}, game {p[2]}");
            checkedCount++;
        }
        Assert.True(checkedCount > 1000, $"only {checkedCount} samples checked");
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void EveryColourSet_IsVerifiedInGame()
    {
        var profile = ColorProfile.LoadBuiltIn();
        Assert.All(profile.ColourSets, s => Assert.Equal(ColorStatus.Verified, s.Status));
        // Blocks the map does not draw, and TNT, are not offered.
        foreach (var key in new[] { "iron_bars", "pointed_dripstone", "white_candle", "tnt" }) Assert.Null(profile.Find(key));
    }
}
