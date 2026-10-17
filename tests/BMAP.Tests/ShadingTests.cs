using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

/// <summary>
/// The map simulator draws the map from block positions only. Every layout the generator makes must simulate to
/// exactly the colours the quantizer planned, in every staircase and support mode.
/// </summary>
public class ShadingTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static readonly List<BlockPreset> Presets = BlockPreset.BuiltIns(Profile);
    private static BlockDefinition Cobble => Profile.Find("cobblestone")!;

    [Fact]
    public void ShadeRule_ComparesWithTheNorthNeighbourOnly()
    {
        Assert.Equal(Shade.Bright, ShadeRule.Compare(5, 4));
        Assert.Equal(Shade.Bright, ShadeRule.Compare(9, 0)); // the size of the step does not matter
        Assert.Equal(Shade.Normal, ShadeRule.Compare(3, 3));
        Assert.Equal(Shade.Dark, ShadeRule.Compare(0, 7));
    }

    public static IEnumerable<object[]> Modes()
    {
        foreach (var st in new[] { StaircaseMode.Flat, StaircaseMode.Classic, StaircaseMode.Valley, StaircaseMode.FullDark, StaircaseMode.FullBright })
            foreach (var su in Enum.GetValues<SupportMode>())
                foreach (var dither in new[] { DitherMode.None, DitherMode.FloydSteinberg, DitherMode.KnollBayer })
                    yield return [st, su, dither];
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EveryLayout_SimulatesToThePlannedColours(StaircaseMode staircase, SupportMode supports, DitherMode dither)
    {
        // "All blocks" contains falling and fragile blocks, so the support rules are exercised too.
        var sel = Presets.Single(p => p.NameEn == "All blocks").ToSelection();
        var img = TestHelpers.Gradient(64, 64);
        var q = ColorQuantizer.Quantize(img, Profile, sel, new QuantizeOptions { Mode = staircase, Dither = dither });
        var layout = BlockLayout.Generate(q, Cobble, 0, supports);
        var report = ShadingAnalyzer.Analyze(layout, Profile, q.Preview);
        Assert.Equal(0, report.Mismatches);
        Assert.Equal(0, report.DependsOnTerrain);
        if (supports != SupportMode.None)
        {
            Assert.Equal(0, report.WillFall);
            Assert.Equal(0, report.WillBreak);
        }
    }

    [Theory]
    [InlineData(2, DitherMode.None)]
    [InlineData(3, DitherMode.FloydSteinberg)]
    [InlineData(4, DitherMode.Bayer4)]
    [InlineData(3, DitherMode.KnollBayer)]
    [InlineData(5, DitherMode.KnollBlueNoise)]
    public void HeightLimitedLayouts_SimulateExactly_AndRespectTheLimit(int limit, DitherMode dither)
    {
        var img = TestHelpers.Gradient(128, 128);
        var q = ColorQuantizer.Quantize(img, Profile, Presets[0].ToSelection(),
            new QuantizeOptions { Mode = StaircaseMode.Valley, MaxHeight = limit, Dither = dither });
        var layout = BlockLayout.Generate(q, Cobble, 0, SupportMode.Important);
        var report = ShadingAnalyzer.Analyze(layout, Profile, q.Preview);
        Assert.Equal(0, report.Mismatches);
        int top = 0, bottom = int.MaxValue;
        for (int i = 0; i < layout.Cells.Length; i++)
        {
            if (layout.Cells[i] == BlockLayout.Empty) continue;
            top = Math.Max(top, layout.Heights[i]);
            bottom = Math.Min(bottom, layout.Heights[i]);
        }
        Assert.InRange(top - bottom, 0, limit);
    }

    [Fact]
    public void TransparentPixels_MakeTheirSouthNeighbourDependOnTerrain()
    {
        var img = RgbaImage.Filled(8, 8, new Rgba(200, 50, 50));
        img[3, 2] = new Rgba(0, 0, 0, 0);
        var q = ColorQuantizer.Quantize(img, Profile, Presets[0].ToSelection(), new QuantizeOptions { Mode = StaircaseMode.Valley });
        var layout = BlockLayout.Generate(q, Cobble);
        var report = ShadingAnalyzer.Analyze(layout, Profile, q.Preview);
        Assert.Equal(1, report.Empty);
        Assert.Equal(1, report.DependsOnTerrain);
        Assert.Contains(report.Issues, i => i.Kind == ShadingIssueKind.DependsOnTerrain && i.X == 3 && i.Z == 4);
    }

    [Fact]
    public void UnsupportedSand_IsReported()
    {
        var sand = Profile.Find("sand")!;
        var c = Profile.ColorOf(sand, Shade.Bright);
        var q = ColorQuantizer.Quantize(RgbaImage.Filled(4, 4, c), Profile, new PaletteSelection { Keys = ["sand"] },
            new QuantizeOptions { Mode = StaircaseMode.FullBright });
        var none = ShadingAnalyzer.Analyze(BlockLayout.Generate(q, Cobble, 0, SupportMode.None), Profile, q.Preview);
        Assert.True(none.WillFall > 0);
        var supported = ShadingAnalyzer.Analyze(BlockLayout.Generate(q, Cobble, 0, SupportMode.Important), Profile, q.Preview);
        Assert.Equal(0, supported.WillFall);
        Assert.Equal(0, supported.Mismatches);
    }

    [Fact]
    public void ShadingTestPad_PredictsEveryRule()
    {
        var pad = ShadingTestPad.Create(Profile, Cobble);
        var report = ShadingAnalyzer.Analyze(pad.Layout, Profile, null);
        Assert.Equal(128, pad.Layout.Width);
        Assert.Equal(129, pad.Layout.Depth);
        Assert.Equal(0, report.WillFall);
        Assert.Equal(0, report.WillBreak);
        Assert.NotEmpty(pad.Cases);
        // Every case predicts its own shade.
        foreach (var c in pad.Cases)
            Assert.Equal(c.Expected, report.Map.Shades[report.Map.Index(c.X, c.Z - 1)]);
    }
}
