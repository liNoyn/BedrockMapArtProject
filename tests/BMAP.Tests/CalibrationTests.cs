using BMAP.Bedrock;
using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Nbt;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

/// <summary>
/// Calibration end to end without the game: a "game" is simulated by drawing the pad with a known rule, storing the
/// picture in a map record, and the calibrator must find that rule again from the picture alone.
/// </summary>
public class CalibrationTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static BlockDefinition Cobble => Profile.Find("cobblestone")!;

    private static ColorProfile WithRule(ShadeMultipliers rule) => new()
    {
        Id = "test", Name = "test", Shades = rule, ColourSets = Profile.ColourSets,
    };

    private static MapRecord FakeMap(RgbaImage img, int xCenter = 64, int zCenter = 64) => new()
    {
        Id = 7,
        Nbt = new NbtCompound
        {
            { "scale", new NbtByte(0) }, { "dimension", new NbtByte(0) }, { "width", new NbtShort(128) },
            { "height", new NbtShort(128) }, { "xCenter", new NbtInt(xCenter) }, { "zCenter", new NbtInt(zCenter) },
            { "colors", new NbtByteArray(img.Pixels) },
        },
    };

    [Fact]
    public void ShadingPad_IsBuildable_AndSamplesEveryStep()
    {
        var pad = ShadingTestPad.Create(Profile, Cobble);
        var report = ShadingAnalyzer.Analyze(pad.Layout, Profile, null);
        Assert.Equal(128, pad.Layout.Width);
        Assert.Equal(129, pad.Layout.Depth);
        Assert.Equal(0, report.WillFall);
        Assert.Equal(0, report.WillBreak);
        foreach (var d in new[] { -4, -3, -2, -1, 0, 1, 2, 3, 4 })
            foreach (var c in new[] { "full", "carpet", "slab" })
                Assert.Contains(pad.Descriptor.Samples, s => s.Case == c && s.Delta == d);
        Assert.Contains(pad.Descriptor.Samples, s => s.Case == "north-empty" && s.North is null);
        Assert.Contains(pad.Descriptor.Samples, s => s.Case == "east-west");
    }

    [Fact]
    public void JavaRule_IsMeasuredBack()
    {
        var pad = ShadingTestPad.Create(Profile, Cobble);
        var drawn = MapSimulator.Simulate(pad.Layout, Profile).Image;
        var r = Calibrator.Analyze(pad.Descriptor, FakeMap(drawn), Profile, "test");
        Assert.Equal(0, r.Orientation);
        Assert.Equal(1, r.BrightStep);
        Assert.Equal(1, r.DarkStep);
        Assert.False(r.Graded);
        Assert.InRange(r.BrightRatio, 255 / 220.0 - 0.02, 255 / 220.0 + 0.02);
        Assert.InRange(r.DarkRatio, 180 / 220.0 - 0.02, 180 / 220.0 + 0.02);
        Assert.InRange(r.NormalFactor, 220 / 255.0 - 0.01, 220 / 255.0 + 0.01);
        Assert.True(r.ControlDeviation < 0.02);
        var rule = r.MeasuredRule("test")!;
        Assert.Equal(1, rule.MinStep);
        Assert.InRange(rule.Normal, 218, 222);
        Assert.InRange(rule.Bright, 252, 258);
        Assert.InRange(rule.Dark, 177, 183);
    }

    [Fact]
    public void ADifferentRule_IsFound_AndTheMapCanBeRotated()
    {
        // A hypothetical game that needs 2 blocks for a shade change and uses other multipliers, storing maps rotated.
        var game = WithRule(new ShadeMultipliers { Dark = 170, Normal = 210, Bright = 250, MinStep = 2 });
        var pad = ShadingTestPad.Create(Profile, Cobble);
        var drawn = Calibrator.Orient(MapSimulator.Simulate(pad.Layout, game).Image, 1);
        var r = Calibrator.Analyze(pad.Descriptor, FakeMap(drawn), Profile, "test");
        Assert.NotEqual(0, r.Orientation);
        Assert.Equal(2, r.BrightStep);
        Assert.Equal(2, r.DarkStep);
        var rule = r.MeasuredRule("test")!;
        Assert.Equal(2, rule.MinStep);
        Assert.InRange(rule.Normal, 208, 212);
        Assert.InRange(rule.Bright, 247, 253);
        Assert.InRange(rule.Dark, 167, 173);
        Assert.Equal(ColorStatus.Verified, rule.Status);
    }

    [Fact]
    public void ColourPad_ReadsEveryBlock_AndBuildsAVerifiedProfile()
    {
        var pad = ShadingTestPad.CreateColourPad(Profile, Cobble, 1);
        Assert.Equal(0, ShadingAnalyzer.Analyze(pad.Layout, Profile, null).WillFall);
        // The "game" shows white concrete slightly differently from the estimate.
        var game = Profile.WithMeasurements(new Dictionary<string, Dictionary<Shade, string>>
        {
            ["white_concrete"] = new() { [Shade.Normal] = "#DADADA", [Shade.Bright] = "#FEFEFE", [Shade.Dark] = "#B0B0B0" },
        }, "game", "game");
        var drawn = MapSimulator.Simulate(pad.Layout, game).Image;
        var r = Calibrator.Analyze(pad.Descriptor, FakeMap(drawn), Profile, "1.26.51");
        var m = r.Measurements();
        Assert.Equal(Profile.Blocks.Count(), m.Count);
        Assert.Equal("#DADADA", m["white_concrete"][Shade.Normal]);
        Assert.Equal("#FEFEFE", m["white_concrete"][Shade.Bright]);
        Assert.Equal("#B0B0B0", m["white_concrete"][Shade.Dark]);
        var measured = Profile.WithMeasurements(m, "measured", "measured", note: "Bedrock 1.26.51");
        Assert.All(measured.Blocks, b => Assert.Equal(ColorStatus.Verified, b.EffectiveStatus));
        Assert.Equal("#DADADA", measured.ColorOf(measured.Find("white_concrete")!, Shade.Normal).ToHex());
    }

    [Fact]
    public void MapsArePickedByPosition_AndUnexploredPixelsAreSkipped()
    {
        var pad = ShadingTestPad.Create(Profile, Cobble);
        var d = pad.Descriptor with { OriginX = 64, OriginZ = 64 };
        var drawn = MapSimulator.Simulate(pad.Layout, Profile).Image;
        for (int i = 0; i < 128 * 30; i++) drawn.Pixels[i * 4 + 3] = 0; // the first 30 rows are not explored
        var right = FakeMap(drawn, 128, 128);    // covers x 64..191, z 64..191
        var wrong = FakeMap(drawn, 0, 0);
        var map = Calibrator.FindMap([wrong, right], d, drawn);
        Assert.Same(right, map);
        var r = Calibrator.Analyze(d, map!, Profile);
        Assert.True(r.Covered < r.SampleCount);
        Assert.Equal(r.Covered, r.Exact);
    }

    [Fact]
    public void PadDescriptor_RoundTripsThroughJson()
    {
        var pad = ShadingTestPad.Create(Profile, Cobble).Descriptor with { OriginX = 1, OriginY = 2, OriginZ = 3 };
        var path = Path.Combine(TestHelpers.TempDir(), "pad.json");
        File.WriteAllText(path, pad.ToJson());
        var back = PadDescriptor.Load(path);
        Assert.Equal(pad.Samples.Count, back.Samples.Count);
        Assert.Equal(pad.Samples[10], back.Samples[10]);
        Assert.Equal(3, back.OriginZ);
    }

    [Fact]
    public void StepTwoProfiles_StillSimulateExactly()
    {
        var game = WithRule(new ShadeMultipliers { MinStep = 2 });
        var q = ColorQuantizer.Quantize(TestHelpers.Gradient(64, 64), game, BlockPreset.BuiltIns(game)[0].ToSelection(),
            new QuantizeOptions { Mode = StaircaseMode.Valley, Dither = DitherMode.FloydSteinberg, MaxHeight = 6 });
        var layout = BlockLayout.Generate(q, Cobble, 0, SupportMode.AllOptimized);
        Assert.Equal(2, layout.Step);
        var report = ShadingAnalyzer.Analyze(layout, game, q.Preview);
        Assert.Equal(0, report.Mismatches);
        Assert.True(layout.MaxHeight - 0 <= 6 + 2); // runs of 3 steps of 2 blocks, plus supports below
    }
}
