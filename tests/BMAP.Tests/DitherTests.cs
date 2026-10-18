using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Tests;

public class DitherTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static PaletteSelection Everything => BlockPreset.BuiltIns(Profile)[0].ToSelection();

    private static QuantizeResult Q(RgbaImage img, QuantizeOptions o) => ColorQuantizer.Quantize(img, Profile, Everything, o);

    [Fact]
    public void BlueNoise_IsAPermutation_AndEvenlySpread()
    {
        var r = BlueNoise.Ranks;
        Assert.Equal(64 * 64, r.Length);
        Assert.Equal(Enumerable.Range(0, 64 * 64), r.Order());
        // The darkest 1/16 of the thresholds are spread out: every 16x16 block gets close to its share (16).
        for (int by = 0; by < 4; by++)
            for (int bx = 0; bx < 4; bx++)
            {
                int n = 0;
                for (int y = by * 16; y < by * 16 + 16; y++)
                    for (int x = bx * 16; x < bx * 16 + 16; x++)
                        if (r[y * 64 + x] < 256) n++;
                Assert.InRange(n, 10, 22);
            }
    }

    [Fact]
    public void Knoll_IsDeterministic_AndBeatsBayerFromADistance()
    {
        var img = TestHelpers.Gradient(128, 128);
        var a = Q(img, new QuantizeOptions { Dither = DitherMode.KnollBayer });
        var b = Q(img, new QuantizeOptions { Dither = DitherMode.KnollBayer });
        Assert.Equal(a.EntryIndex, b.EntryIndex);
        var bayer = Q(img, new QuantizeOptions { Dither = DitherMode.Bayer8 });
        Assert.True(HeightAdvisor.DistanceDeltaE2000(img, a.Preview) < HeightAdvisor.DistanceDeltaE2000(img, bayer.Preview));
    }

    [Fact]
    public void Optimized_IsDeterministic_AndBeatsDiffusionAndKnollFromADistance()
    {
        var img = TestHelpers.Gradient(128, 128);
        foreach (var mode in new[] { StaircaseMode.Flat, StaircaseMode.Valley })
        {
            var a = Q(img, new QuantizeOptions { Dither = DitherMode.Optimized, Mode = mode });
            var b = Q(img, new QuantizeOptions { Dither = DitherMode.Optimized, Mode = mode });
            Assert.Equal(a.EntryIndex, b.EntryIndex);
            double opt = HeightAdvisor.DistanceDeltaE2000(img, a.Preview);
            Assert.True(opt < HeightAdvisor.DistanceDeltaE2000(img, Q(img, new QuantizeOptions { Dither = DitherMode.FloydSteinberg, Mode = mode }).Preview));
            Assert.True(opt < HeightAdvisor.DistanceDeltaE2000(img, Q(img, new QuantizeOptions { Dither = DitherMode.KnollBayer, Mode = mode }).Preview));
        }
    }

    [Fact]
    public void Optimized_KeepsAnExactColourFlat()
    {
        // A colour the palette has exactly needs no mixing: no noise may be added.
        var colour = Profile.ColorOf(Profile.Find("red_wool")!, Shade.Normal);
        var q = Q(RgbaImage.Filled(64, 64, colour), new QuantizeOptions { Dither = DitherMode.Optimized, Mode = StaircaseMode.Valley });
        Assert.Single(q.EntryIndex.Distinct());
        Assert.All(q.Shades, s => Assert.Equal(Shade.Normal, s));
    }

    [Fact]
    public void Optimized_WithAHeightLimit_KeepsTheStaircaseWithinTheLimit()
    {
        var img = TestHelpers.Gradient(128, 128);
        var o = new QuantizeOptions { Dither = DitherMode.Optimized, Mode = StaircaseMode.Valley, MaxHeight = 4 };
        var q = Q(img, o);
        var fs = Q(img, o with { Dither = DitherMode.FloydSteinberg });
        // Only same-shade swaps: the shades (and so the heights) are those of the diffusion it started from.
        Assert.Equal(fs.Shades, q.Shades);
        Assert.True(HeightAdvisor.DistanceDeltaE2000(img, q.Preview) < HeightAdvisor.DistanceDeltaE2000(img, fs.Preview));
    }

    [Fact]
    public void Knoll_MixesTwoColoursForAColourBetweenThem()
    {
        // Halfway (in linear light) between white and black concrete: both must appear, in similar amounts.
        var white = Profile.ColorOf(Profile.Find("white_concrete")!, Shade.Normal);
        var black = Profile.ColorOf(Profile.Find("black_concrete")!, Shade.Normal);
        double mid(byte p, byte q) => ColorMath.LinearToSrgb((ColorMath.SrgbToLinear(p) + ColorMath.SrgbToLinear(q)) / 2);
        var grey = new Rgba((byte)mid(white.R, black.R), (byte)mid(white.G, black.G), (byte)mid(white.B, black.B));
        var sel = new PaletteSelection { Keys = ["white_concrete", "black_concrete"] };
        var q = ColorQuantizer.Quantize(RgbaImage.Filled(64, 64, grey), Profile, sel, new QuantizeOptions { Dither = DitherMode.KnollBayer });
        int whites = q.EntryIndex.Count(e => q.Entries[e].Block.Key == "white_concrete");
        Assert.InRange(whites, 64 * 64 * 0.4, 64 * 64 * 0.6);
    }

    [Fact]
    public void LinearDiffusion_KeepsTheBrightnessSeenFromADistance()
    {
        // A dark grey that the palette cannot show exactly: dithered in linear light, the average light matches.
        var img = RgbaImage.Filled(96, 96, new Rgba(60, 60, 60));
        var sel = new PaletteSelection { Keys = ["white_concrete", "black_concrete"] };
        double MeanLinear(RgbaImage m) => Enumerable.Range(0, m.Width * m.Height).Average(i => ColorMath.SrgbToLinear(m.Pixels[i * 4 + 1]));
        double target = ColorMath.SrgbToLinear(60);
        var lin = ColorQuantizer.Quantize(img, Profile, sel, new QuantizeOptions { Dither = DitherMode.FloydSteinberg });
        var srgb = ColorQuantizer.Quantize(img, Profile, sel, new QuantizeOptions { Dither = DitherMode.FloydSteinberg, LinearDiffusion = false });
        Assert.True(Math.Abs(MeanLinear(lin.Preview) - target) < Math.Abs(MeanLinear(srgb.Preview) - target));
        Assert.InRange(MeanLinear(lin.Preview), target * 0.85, target * 1.15);
    }

    [Fact]
    public void ColumnIndex_AnswersOccupancyAndTops()
    {
        var b = Profile.Find("stone")!;
        var blocks = new List<BlockPlacement> { new(1, 3, 2, b, false, 1, 1), new(1, 1, 2, b, true, 1, 1), new(0, 0, 0, b, true, 0, -1) };
        var c = new ColumnIndex(4, 4, blocks);
        Assert.True(c.Occupied(1, 3, 2));
        Assert.True(c.Occupied(1, 1, 2));
        Assert.False(c.Occupied(1, 2, 2));
        Assert.False(c.Occupied(2, 3, 2));
        Assert.Equal(3, c.Top(1, 2));
        Assert.Equal(int.MinValue, c.Top(3, 3));
        Assert.False(c.Occupied(-1, 0, 0));
    }

    [Fact]
    public void OldProjectFiles_AreStillRecognised()
    {
        Assert.True(AppPaths.IsProjectFile(@"C:\x\art.bmap.json"));
        Assert.True(AppPaths.IsProjectFile(@"C:\x\art.BMAG.json"));
        Assert.False(AppPaths.IsProjectFile(@"C:\x\art.json"));
    }
}
