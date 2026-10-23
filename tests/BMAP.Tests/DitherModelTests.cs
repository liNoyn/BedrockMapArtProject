using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

public class DitherModelTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static PaletteSelection Selection => BlockPreset.BuiltIns(Profile)[0].ToSelection();

    [Theory]
    [InlineData(0.25, 0.8)]
    [InlineData(2.0, 0.5)]
    [InlineData(0.0, 0.0)]
    public void DiscreteKernel_EqualsExplicitFilteredEnergy(double sigma1, double sigma2)
    {
        var settings = new RefineSettings { Sigma1 = sigma1, Sigma2 = sigma2, PixelWeight = 0.15, Weight1 = 0.7, Weight2 = 1.3 };
        var kernel = new DitherKernel(settings);
        const int w = 4, h = 3;
        double[] errors = [0.2, -0.7, 0, 0.9, -0.3, 0.1, -0.9, 0.6, 0.4, 0.3, -0.2, 0.1];
        double quadratic = 0;
        for (int i = 0; i < errors.Length; i++)
            for (int j = 0; j < errors.Length; j++)
            {
                int dx = j % w - i % w, dy = j / w - i / w;
                if (Math.Abs(dx) <= kernel.Radius && Math.Abs(dy) <= kernel.Radius)
                    quadratic += errors[i] * errors[j] * kernel.Values[(dy + kernel.Radius) * kernel.Size + dx + kernel.Radius];
            }
        double expected = settings.PixelWeight * errors.Sum(e => e * e);
        foreach (var (sigma, weight) in new[] { (sigma1, settings.Weight1), (sigma2, settings.Weight2) })
        {
            var g = DitherKernel.Gaussian(sigma);
            int r = g.Length / 2;
            for (int y = -r; y < h + r; y++)
                for (int x = -r; x < w + r; x++)
                {
                    double filtered = 0;
                    for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++)
                        if (Math.Abs(x - xx) <= r && Math.Abs(y - yy) <= r)
                            filtered += errors[yy * w + xx] * g[x - xx + r] * g[y - yy + r];
                    expected += weight * filtered * filtered;
                }
        }
        Assert.True(quadratic >= 0);
        Assert.Equal(expected, quadratic, 6);
        Assert.Equal(settings.PixelWeight + settings.Weight1 + settings.Weight2, kernel.Values.Sum(v => (double)v), 6);
    }

    [Fact]
    public void Matcher_PreservesFractionalErrorAndDoesNotCacheItAsAnInteger()
    {
        var matcher = new ColorQuantizer.NearestMatcher([new(0, Shade.Normal, new Rgba(0,0,0)), new(1, Shade.Normal, new Rgba(1,1,0))], ColorMetric.Rgb);
        Assert.Equal(1, matcher.Find(0.4, 0.7, 0));
        Assert.Equal(0, matcher.Find(0, 1, 0)); // integer tie: first preferred candidate
        Assert.Equal(1, matcher.Find(0.4, 0.7, 0));
        Assert.Equal(0.45, matcher.Cost(0.4, 0.7, 0, 1, 0), 10);
    }

    [Fact]
    public void MaskedFilter_EqualsDirectNormalizedTwoDimensionalConvolution()
    {
        const int w = 4, h = 3;
        bool[] mask = [true,false,true,true, false,true,false,true, true,true,true,false];
        var src = Enumerable.Range(0, w*h*3).Select(i => Math.Sin(i)).ToArray();
        var result = DitherFilter.Blur(src, mask, w, h, 0.8);
        var g = DitherKernel.Gaussian(0.8);
        int r = g.Length / 2;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) for (int c = 0; c < 3; c++)
        {
            double sum = 0, weight = 0;
            for (int yy = 0; yy < h; yy++) for (int xx = 0; xx < w; xx++)
                if (mask[yy*w+xx] && Math.Abs(xx-x)<=r && Math.Abs(yy-y)<=r)
                {
                    double k = g[xx-x+r] * g[yy-y+r];
                    sum += k*src[(yy*w+xx)*3+c]; weight += k;
                }
            Assert.Equal(sum/weight,result[(y*w+x)*3+c],10);
        }
    }

    [Theory]
    [InlineData(DitherMode.KnollBayer)]
    [InlineData(DitherMode.KnollBlueNoise)]
    public void Knoll_ZeroStrengthEqualsNearestMapping_AndExactColoursStaySolid(DitherMode mode)
    {
        var img = TestHelpers.Gradient(32,32);
        var nearest = ColorQuantizer.Quantize(img,Profile,Selection,new QuantizeOptions());
        var off = ColorQuantizer.Quantize(img,Profile,Selection,new QuantizeOptions { Dither = mode, DitherStrength = 0 });
        Assert.Equal(nearest.Preview.Pixels,off.Preview.Pixels);
        foreach (var block in Profile.Blocks.Where(b => b.DefaultEnabled).Take(24))
        {
            var colour = Profile.ColorOf(block,Shade.Normal);
            var q = ColorQuantizer.Quantize(RgbaImage.Filled(16,16,colour),Profile,new PaletteSelection { Keys = [block.Key] },new QuantizeOptions { Dither = mode });
            Assert.All(Enumerable.Range(0,256),i => Assert.Equal(colour,q.Preview[i%16,i/16]));
        }
    }

    [Theory]
    [InlineData(DitherMode.Perceptual)]
    [InlineData(DitherMode.Optimized)]
    public void HiddenColoursBelowAlphaThreshold_DoNotChangeVisibleOutput(DitherMode mode)
    {
        var a = TestHelpers.Gradient(32,32);
        var b = a.Clone();
        for (int y = 0; y < 32; y++) for(int x = 0; x < 32; x++)
            if ((x+y)%4==0) { a[x,y]=new Rgba(255,0,255,64); b[x,y]=new Rgba(0,255,0,64); }
        var opt = new QuantizeOptions { Dither = mode, Mode = StaircaseMode.Valley, Refine = new RefineSettings { PlanBlur = 1, PairLambda = 0.1, Denoise = 4, EdgeWeight = 1 } };
        var qa = ColorQuantizer.Quantize(a,Profile,Selection,opt);
        var qb = ColorQuantizer.Quantize(b,Profile,Selection,opt);
        Assert.Equal(qa.Preview.Pixels,qb.Preview.Pixels);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void Perceptual_PreservesHeightPlanAndProducesExactBuild(int limit)
    {
        var img = TestHelpers.Gradient(48,48);
        var opt = new QuantizeOptions { Dither = DitherMode.Perceptual, Mode = StaircaseMode.Valley, MaxHeight = limit };
        var cobble = Profile.Find("cobblestone")!;
        var q = HeightPlanner.Quantize(img,Profile,Selection,opt,cobble,SupportMode.Important,true);
        var layout = BlockLayout.Generate(q,cobble,supports:SupportMode.Important,onMapGrid:true);
        Assert.InRange(layout.MaxHeight,0,limit);
        Assert.True(ShadingAnalyzer.Analyze(layout,Profile,q.Preview).IsExact);
        var direct = ColorQuantizer.Quantize(img,Profile,Selection,opt);
        var seed = ColorQuantizer.Quantize(img,Profile,Selection,opt with { Dither = DitherMode.FloydSteinberg });
        Assert.Equal(seed.Shades,direct.Shades);
        Assert.Equal(direct.Preview.Pixels,ColorQuantizer.Quantize(img,Profile,Selection,opt).Preview.Pixels);
    }

    [Fact]
    public void Perceptual_ImprovesMultiscaleAccuracyOnHeldOutGradient()
    {
        var img = TestHelpers.Gradient(73,59);
        var opt = new QuantizeOptions { Mode = StaircaseMode.Valley, Dither = DitherMode.Perceptual };
        var score = QualityScore.Measure(img,ColorQuantizer.Quantize(img,Profile,Selection,opt).Preview);
        var legacy = QualityScore.Measure(img,ColorQuantizer.Quantize(img,Profile,Selection,opt with { Dither = DitherMode.Optimized }).Preview);
        Assert.True(score.Near < legacy.Near);
        Assert.True(score.Far < legacy.Far);
        Assert.True(score.Overall < legacy.Overall);
    }

    [Fact]
    public void Perceptual_ZeroStrengthDisablesDither_AndHeightPlannerStillFits()
    {
        var img = TestHelpers.Gradient(48,48);
        var options = new QuantizeOptions { Dither = DitherMode.Perceptual, DitherStrength = 0, Mode = StaircaseMode.Valley };
        var a = ColorQuantizer.Quantize(img,Profile,Selection,options);
        var b = ColorQuantizer.Quantize(img,Profile,Selection,options with { Dither = DitherMode.None });
        Assert.Equal(a.Preview.Pixels,b.Preview.Pixels);
        var cobble = Profile.Find("cobblestone")!;
        var q = HeightPlanner.Quantize(img,Profile,Selection,options with { MaxHeight = 3 },cobble,SupportMode.Important,true);
        Assert.InRange(BlockLayout.Generate(q,cobble,supports:SupportMode.Important,onMapGrid:true).MaxHeight,0,3);
    }

    [Fact]
    public void Perceptual_CanBeCancelled_AndQualityRejectsDifferentDimensions()
    {
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        Assert.Throws<OperationCanceledException>(() => ColorQuantizer.Quantize(TestHelpers.Gradient(8,8),Profile,Selection,
            new QuantizeOptions { Dither = DitherMode.Perceptual },ct.Token));
        Assert.Throws<ArgumentException>(() => QualityScore.Measure(new RgbaImage(4,4),new RgbaImage(8,8)));
    }
}
