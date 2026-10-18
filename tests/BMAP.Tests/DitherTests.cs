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
    public void Optimized_IsDeterministic_AndCalmerThanDiffusionAndKnoll()
    {
        var img = TestHelpers.Gradient(128, 128);
        foreach (var mode in new[] { StaircaseMode.Flat, StaircaseMode.Valley })
        {
            var a = Q(img, new QuantizeOptions { Dither = DitherMode.Optimized, Mode = mode });
            var b = Q(img, new QuantizeOptions { Dither = DitherMode.Optimized, Mode = mode });
            Assert.Equal(a.EntryIndex, b.EntryIndex);
            // Closer to the source seen from afar than diffusion and Knoll's patterns, and not much noisier pixel by pixel
            // (the corpus in docs/benchmarks.md is the real check).
            var opt = QualityScore.Measure(img, a.Preview);
            var fs = QualityScore.Measure(img, Q(img, new QuantizeOptions { Dither = DitherMode.FloydSteinberg, Mode = mode }).Preview);
            var knoll = QualityScore.Measure(img, Q(img, new QuantizeOptions { Dither = DitherMode.KnollBayer, Mode = mode }).Preview);
            Assert.True(opt.Far < fs.Far, $"{mode}: far {opt.Far:0.00} vs FS {fs.Far:0.00}");
            Assert.True(opt.Far < knoll.Far, $"{mode}: far {opt.Far:0.00} vs Knoll {knoll.Far:0.00}");
            Assert.True(opt.Pixel < fs.Pixel * 1.05, $"{mode}: pixel {opt.Pixel:0.00} vs FS {fs.Pixel:0.00}");
            Assert.True(opt.Pixel < knoll.Pixel, $"{mode}: pixel {opt.Pixel:0.00} vs Knoll {knoll.Pixel:0.00}");
        }
    }

    [Fact]
    public void Optimized_MixesAPaleColourFromFewColours()
    {
        // Pale lavender has no map colour: it must be mixed, but from a few colours (a pair or triple plus the nearest),
        // not from many hues.
        var q = Q(RgbaImage.Filled(64, 64, new Rgba(228, 227, 245)), new QuantizeOptions { Dither = DitherMode.Optimized, Mode = StaircaseMode.Valley });
        var colours = Enumerable.Range(0, 64 * 64).Select(i => (q.Preview.Pixels[i * 4], q.Preview.Pixels[i * 4 + 1], q.Preview.Pixels[i * 4 + 2])).Distinct().Count();
        Assert.InRange(colours, 1, 4);
    }

    [Fact]
    public void Optimized_KeepsThePinkOfPalePink()
    {
        // Pale pink (no map colour) must stay pink: mixed from white and pink, not flattened to grey.
        var pale = new Rgba(240, 200, 210);
        var q = Q(RgbaImage.Filled(64, 64, pale), new QuantizeOptions { Dither = DitherMode.Optimized, Mode = StaircaseMode.Valley });
        int pink = q.EntryIndex.Count(e => q.Entries[e].Block.Key.StartsWith("pink"));
        Assert.True(pink > 64 * 64 / 20, $"only {pink} pink pixels");
        Assert.True(QualityScore.Measure(RgbaImage.Filled(64, 64, pale), q.Preview).Far < 4);
    }

    [Theory]
    [InlineData("#EED0D4")]
    [InlineData("#ECD4D4")]
    [InlineData("#EDD1D6")]
    public void Optimized_DoesNotTurnPalePinkHairGrey(string hex)
    {
        // Pale pinks with the lightness of the grey #DCD9D3 (the top of the hair in a user's anime image): grey alone was
        // closer by distance than white + pink, which pay for their contrast, so whole areas turned grey. The plan now
        // pays for the colour it loses (PlanChroma). Flat builds are left out: without the bright shades white + pink
        // can only reach these pinks much darker, and a stronger penalty there only adds red dots.
        foreach (var mode in new[] { StaircaseMode.Classic, StaircaseMode.Valley })
        {
            var q = Q(RgbaImage.Filled(64, 64, Rgba.FromHex(hex)), new QuantizeOptions { Dither = DitherMode.Optimized, Mode = mode });
            int pink = q.EntryIndex.Count(e => q.Entries[e].Block.Key.StartsWith("pink"));
            Assert.True(pink > 64 * 64 / 10, $"{hex} {mode}: only {pink} pink pixels");
        }
    }

    [Fact]
    public void Optimized_HasNoPatchesInAPaleGradientWithBlockNoise()
    {
        // Flat pale pinks near the point where grey alone stops being the best plan, with 8x8 block noise of ±2 (as in
        // compressed anime frames). One hard plan per colour bucket made the blocks flip between grey and
        // white + pink: square patches. Inside each flat area the pink share must be even.
        const int S = 64, T = 16;
        // #F2D4D7 / #F1D1D5 sat where "grey alone" and "white + pink" tied and turned grey with stripes until the plan
        // paid for lost chroma (PlanChroma).
        string[] colours = ["#F4DADB", "#F3D7D9", "#F2D4D7", "#F1D1D5", "#F0CED3", "#EFCBD1"];
        double worst = 0;
        foreach (var hex in colours)
        {
            var c0 = Rgba.FromHex(hex);
            var img = new RgbaImage(S * 2, S * 2);
            for (int y = 0; y < img.Height; y++)
                for (int x = 0; x < img.Width; x++)
                {
                    int n = (((x / 8) * 7 + (y / 8) * 13) % 5) - 2;
                    int i = (y * img.Width + x) * 4;
                    img.Pixels[i] = (byte)Math.Clamp(c0.R + n, 0, 255);
                    img.Pixels[i + 1] = (byte)Math.Clamp(c0.G + n, 0, 255);
                    img.Pixels[i + 2] = (byte)Math.Clamp(c0.B + n, 0, 255);
                    img.Pixels[i + 3] = 255;
                }
            var q = Q(img, new QuantizeOptions { Dither = DitherMode.Optimized, Mode = StaircaseMode.Valley });
            var shares = new List<double>();
            for (int ty = T; ty < img.Height - T; ty += T) // away from the borders
                for (int tx = T; tx < img.Width - T; tx += T)
                {
                    int pink = 0;
                    for (int y = ty; y < ty + T; y++)
                        for (int x = tx; x < tx + T; x++)
                            if (q.Entries[q.Get(x, y)].Block.Key.StartsWith("pink")) pink++;
                    shares.Add(pink / (double)(T * T));
                }
            worst = Math.Max(worst, shares.Max() - shares.Min());
        }
        Assert.True(worst < 0.15, $"pink share varies by {worst:0.00} inside a flat area");
    }
    [Fact]
    public void QualityScore_IsZeroForAnIdenticalImage_AndSeesNoise()
    {
        var img = TestHelpers.Gradient(64, 64);
        var same = QualityScore.Measure(img, img);
        Assert.Equal(0, same.Pixel, 6);
        Assert.Equal(0, same.Far, 6);
        // Zero-mean checkerboard noise: clearly visible per pixel, fading when blurred.
        var grey = RgbaImage.Filled(64, 64, new Rgba(128, 128, 128));
        img = grey;
        var noisy = grey.Clone();
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                for (int c = 0; c < 3; c++) noisy.Pixels[(y * 64 + x) * 4 + c] = (byte)(((x + y) & 1) == 0 ? 158 : 94);
        var s = QualityScore.Measure(img, noisy);
        Assert.True(s.Pixel > s.Near && s.Near > s.Far && s.Far > 0);
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
