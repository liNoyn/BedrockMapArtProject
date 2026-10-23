using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

/// <summary>Hybrid: flat colour areas from the calm Optimized model, everything else from Perceptual.</summary>
public class HybridDitherTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static PaletteSelection Selection => BlockPreset.BuiltIns(Profile)[0].ToSelection();

    /// <summary>A flat illustration: purple and light pink discs with brown outlines on white.</summary>
    private static RgbaImage Illustration()
    {
        var img = RgbaImage.Filled(96, 96, new Rgba(255, 255, 255));
        void Disc(int cx, int cy, int r, Rgba fill)
        {
            for (int y = 0; y < 96; y++)
                for (int x = 0; x < 96; x++)
                {
                    double d = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    if (d <= r - 2) img[x, y] = fill;
                    else if (d <= r) img[x, y] = new Rgba(110, 84, 66);
                }
        }
        Disc(60, 34, 30, Rgba.FromHex("#7E7CCB"));
        Disc(30, 66, 26, Rgba.FromHex("#FCBCD4"));
        return img;
    }

    [Fact]
    public void FlatAreas_KeepTheCalmResult_AndAreDeterministic()
    {
        var img = Illustration();
        var opt = new QuantizeOptions { Mode = StaircaseMode.Classic, Dither = DitherMode.Optimized };
        var calm = ColorQuantizer.Quantize(img, Profile, Selection, opt);
        var hybrid = ColorQuantizer.Quantize(img, Profile, Selection, opt with { Dither = DitherMode.Hybrid });
        Assert.Equal(hybrid.Preview.Pixels, ColorQuantizer.Quantize(img, Profile, Selection, opt with { Dither = DitherMode.Hybrid }).Preview.Pixels);
        var flat = ColorQuantizer.FlatAreas(img, 128);
        // Inside both discs (well away from the outlines) the hybrid equals the calm model, pixel for pixel.
        int inside = 0, same = 0;
        for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                // Deep inside a disc: everything within 5 pixels has the same (purple or pink) colour.
                var c = img[x, y];
                if (c == new Rgba(255, 255, 255) || c == new Rgba(110, 84, 66)) continue;
                bool deep = true;
                for (int dy = -5; dy <= 5 && deep; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                        if (x + dx < 0 || y + dy < 0 || x + dx >= 96 || y + dy >= 96 || img[x + dx, y + dy] != c) { deep = false; break; }
                if (!deep) continue;
                Assert.True(flat[y * 96 + x], $"({x},{y}) should be flat");
                inside++;
                if (calm.Preview[x, y] == hybrid.Preview[x, y]) same++;
            }
        Assert.Equal(inside, same);
    }

    [Fact]
    public void Gradients_GetThePerceptualQuality()
    {
        var img = TestHelpers.Gradient(73, 59);
        var opt = new QuantizeOptions { Mode = StaircaseMode.Valley, Dither = DitherMode.Hybrid };
        var hybrid = QualityScore.Measure(img, ColorQuantizer.Quantize(img, Profile, Selection, opt).Preview);
        var calm = QualityScore.Measure(img, ColorQuantizer.Quantize(img, Profile, Selection, opt with { Dither = DitherMode.Optimized }).Preview);
        var perceptual = QualityScore.Measure(img, ColorQuantizer.Quantize(img, Profile, Selection, opt with { Dither = DitherMode.Perceptual }).Preview);
        Assert.True(hybrid.Far < calm.Far, $"far {hybrid.Far:0.00} vs Optimized {calm.Far:0.00}");
        Assert.True(hybrid.Near < calm.Near, $"near {hybrid.Near:0.00} vs Optimized {calm.Near:0.00}");
        Assert.True(hybrid.Overall < perceptual.Overall * 1.1, $"overall {hybrid.Overall:0.00} vs Perceptual {perceptual.Overall:0.00}");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void HeightLimit_KeepsThePlannedShades(int limit)
    {
        var img = Illustration();
        var opt = new QuantizeOptions { Dither = DitherMode.Hybrid, Mode = StaircaseMode.Valley, MaxHeight = limit };
        var cobble = Profile.Find("cobblestone")!;
        var q = HeightPlanner.Quantize(img, Profile, Selection, opt, cobble, SupportMode.Important, true);
        var layout = BlockLayout.Generate(q, cobble, supports: SupportMode.Important, onMapGrid: true);
        Assert.InRange(layout.MaxHeight, 0, limit);
        Assert.True(ShadingAnalyzer.Analyze(layout, Profile, q.Preview).IsExact);
        var direct = ColorQuantizer.Quantize(img, Profile, Selection, opt);
        var seed = ColorQuantizer.Quantize(img, Profile, Selection, opt with { Dither = DitherMode.FloydSteinberg });
        Assert.Equal(seed.Shades, direct.Shades);
    }
}
