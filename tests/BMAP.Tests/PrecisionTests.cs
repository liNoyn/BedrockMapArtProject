using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

public class PrecisionTests
{
    [Theory]
    [InlineData("#F5F0EB", StaircaseMode.Classic)]
    [InlineData("#F5F0EB", StaircaseMode.Valley)]
    [InlineData("#F3EFE4", StaircaseMode.Classic)]
    [InlineData("#F3EFE4", StaircaseMode.Valley)]
    public void IllustrationBackground_StaysSolidWhenItsCalmPlanHasOneColour(string hex, StaircaseMode mode)
    {
        var profile = ColorProfile.LoadBuiltIn();
        var selection = BlockPreset.BuiltIns(profile)[0].ToSelection();
        foreach (var model in new[] { DitherMode.Optimized,DitherMode.Hybrid })
        {
            var q = ColorQuantizer.Quantize(RgbaImage.Filled(64,64,Rgba.FromHex(hex)),profile,selection,
                new QuantizeOptions { Dither=model,Mode=mode });
            int dominant = Enumerable.Range(0,64*64).GroupBy(i=>(q.EntryIndex[i],q.Shades[i])).Max(g=>g.Count());
            Assert.True(dominant > .9*64*64,$"{model}/{mode}/{hex}: only {dominant} pixels keep the dominant colour");
        }
    }

    public static IEnumerable<object[]> ExactColourCases => Enum.GetValues<DitherMode>()
        .SelectMany(model => new[] { StaircaseMode.Flat, StaircaseMode.Valley }.Select(mode => new object[] { model,mode }));
    public static IEnumerable<object[]> Models => Enum.GetValues<DitherMode>().Select(model=>new object[] { model });

    [Theory]
    [MemberData(nameof(Models))]
    public void EveryModel_PreservesExactNormalColoursAtTheSupportedHeightLimit(DitherMode model)
    {
        var profile = ColorProfile.LoadBuiltIn();
        var selection = BlockPreset.BuiltIns(profile)[0].ToSelection();
        var support = profile.Find("cobblestone")!;
        foreach (var colour in ColorQuantizer.BuildCandidates(profile,selection.Resolve(profile),StaircaseMode.Flat).Select(c=>c.Color).Distinct())
        {
            var source = RgbaImage.Filled(8,8,colour);
            var q = HeightPlanner.Quantize(source,profile,selection,
                new QuantizeOptions { Dither=model,Mode=StaircaseMode.Valley,MaxHeight=2 },support,SupportMode.Important,true);
            Assert.True(source.Pixels.SequenceEqual(q.Preview.Pixels),$"{model}: exact normal colour {colour.ToHex()} changed at height 2");
            var layout = BlockLayout.Generate(q,support,supports:SupportMode.Important,onMapGrid:true);
            Assert.InRange(layout.MaxHeight,0,2);
            Assert.True(ShadingAnalyzer.Analyze(layout,profile,q.Preview).IsExact);
        }
    }

    [Theory]
    [MemberData(nameof(ExactColourCases))]
    public void EveryModel_KeepsEveryReachableSolidColourExact(DitherMode model, StaircaseMode mode)
    {
        var profile = ColorProfile.LoadBuiltIn();
        var selection = BlockPreset.BuiltIns(profile)[0].ToSelection();
        foreach (var colour in ColorQuantizer.BuildCandidates(profile,selection.Resolve(profile),mode).Select(c=>c.Color).Distinct())
        {
            var image = RgbaImage.Filled(8,8,colour);
            var q = ColorQuantizer.Quantize(image,profile,selection,new QuantizeOptions { Dither=model,Mode=mode });
            Assert.True(image.Pixels.SequenceEqual(q.Preview.Pixels),$"{model}/{mode}: reachable {colour.ToHex()} changed");
        }
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    public void ExactSearch_EqualsExhaustiveSearchWithMasksPenaltiesAndDuplicateColours(ColorMetric metric)
    {
        var random = new Random(932711);
        var candidates = Enumerable.Range(0,251).Select(i=>new Candidate(i,(Shade)(i%3),
            new Rgba((byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256)))).ToList();
        candidates[137] = candidates[3] with { Entry = 137 };
        var matcher = new ColorQuantizer.NearestMatcher(candidates,metric);
        foreach (double penalty in new[]{0,.01,.5,2.0})
            for (int query = 0; query < 300; query++)
            {
                double r = random.NextDouble()*300-20, g = random.NextDouble()*300-20, b = random.NextDouble()*300-20;
                if (query%4 == 0) { r = candidates[3].Color.R; g = candidates[3].Color.G; b = candidates[3].Color.B; }
                int mask = query%8;
                int expected = -1; double best = double.MaxValue;
                for (int c = 0; c < candidates.Count; c++)
                {
                    if ((mask & (1 << (int)candidates[c].Shade)) == 0) continue;
                    double cost = matcher.Cost(r,g,b,c,penalty);
                    if (cost < best) { best = cost; expected = c; }
                }
                Assert.Equal(expected,matcher.Find(r,g,b,mask,penalty));
            }
    }

    [Fact]
    public void ExactSearch_RetainsFirstCandidateWhenEveryPointIsIdentical()
    {
        var candidates = Enumerable.Range(0,64).Select(i=>new Candidate(i,(Shade)(i%3),new Rgba(123,91,212))).ToList();
        var matcher = new ColorQuantizer.NearestMatcher(candidates,ColorMetric.Oklab);
        foreach (var (mask,expected) in new[]{(1,0),(2,1),(4,2),(7,0),(0,-1)})
            Assert.Equal(expected,matcher.Find(123.1,91.2,212.3,mask,0));
        Assert.Equal(1,matcher.Find(123.1,91.2,212.3,7,.5));
    }

    [Fact]
    public void HeightPlan_DoesNotSacrificeAnExactColourToScatterTies()
    {
        var dark = new Rgba(254, 255, 255);
        var block = new BlockDefinition
        {
            Key = "near-white", Name = "minecraft:stone", DisplayName = "test", MapColor = "#FFFFFF",
            Measured = new() { [Shade.Dark] = "#FEFFFF", [Shade.Normal] = "#FFFFFF", [Shade.Bright] = "#FFFFFF" },
        };
        var profile = new ColorProfile
        {
            Id = "test", Name = "test",
            ColourSets = [new ColourSet { Id = 1, Name = "test", MapColor = "#FFFFFF", Blocks = [block] }],
        };
        foreach (var mode in new[] { DitherMode.None, DitherMode.Bayer8, DitherMode.KnollBayer, DitherMode.KnollBlueNoise })
        {
            var q = ColorQuantizer.Quantize(RgbaImage.Filled(64, 1, dark), profile,
                new PaletteSelection { Keys = [block.Key] },
                new QuantizeOptions { Mode = StaircaseMode.Valley, MaxHeight = 2, Dither = mode, DitherStrength = 0 });
            Assert.All(q.Shades, shade => Assert.Equal(Shade.Dark, shade));
            Assert.Equal(RgbaImage.Filled(64, 1, dark).Pixels, q.Preview.Pixels);
        }
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    [InlineData(ColorMetric.Rgb)]
    [InlineData(ColorMetric.WeightedRgb)]
    [InlineData(ColorMetric.Ciede2000)]
    public void HeightPlan_EqualsExhaustiveFeasibleShadeSearch(ColorMetric metric)
    {
        var profile = ColorProfile.LoadBuiltIn();
        var selection = new PaletteSelection { Keys = ["white_wool", "pink_wool", "blue_wool", "black_wool"] };
        var entries = selection.Resolve(profile);
        var candidates = ColorQuantizer.BuildCandidates(profile, entries, StaircaseMode.Valley);
        var matcher = new ColorQuantizer.NearestMatcher(candidates, metric);
        const int w = 7, h = 6, runs = 1;
        var source = TestHelpers.Gradient(w, h);
        source[3, 2] = new Rgba(255, 0, 255, 0);
        var options = new QuantizeOptions
        {
            Mode = StaircaseMode.Valley, MaxHeight = runs * profile.Shades.Step, Metric = metric, ShadePenalty = .17,
        };
        var q = ColorQuantizer.Quantize(source, profile, selection, options);
        for (int x = 0; x < w; x++)
        {
            double actual = 0;
            for (int y = 0; y < h; y++)
            {
                if (q.Get(x,y) < 0) continue;
                var c = source[x,y];
                int index = candidates.FindIndex(ca => ca.Entry == q.Get(x,y) && ca.Shade == q.GetShade(x,y));
                actual += matcher.Cost(c.R,c.G,c.B,index,options.ShadePenalty);
            }
            Assert.Equal(Search(0,0), actual, 9);

            double Search(int y, int run)
            {
                if (y == h) return 0;
                var c = source[x,y];
                if (c.A < 128) return Search(y+1,run);
                double best = double.PositiveInfinity;
                foreach (var shade in new[]{Shade.Dark,Shade.Normal,Shade.Bright})
                {
                    int next = shade switch { Shade.Dark => run < 0 ? run-1 : -1, Shade.Bright => run > 0 ? run+1 : 1, _ => run };
                    if (Math.Abs(next) > runs) continue;
                    double cost = candidates.Select((ca,i)=>(ca,i)).Where(t=>t.ca.Shade == shade)
                        .Min(t=>matcher.Cost(c.R,c.G,c.B,t.i,options.ShadePenalty));
                    best = Math.Min(best,cost+Search(y+1,next));
                }
                return best;
            }
        }
    }

    [Fact]
    public void EdgeWeights_RetainHueEdgesOfEqualLightnessAndChroma()
    {
        const int w = 8, h = 5;
        var source = Enumerable.Range(0,w*h).Select(i=>new Lab(.6,i%w<4 ? .1 : -.1,0)).ToArray();
        var chosen = new int[w*h];
        var weights = ColorQuantizer.EdgeWeights(source,chosen,w,h,1);
        Assert.Equal(1,weights[2*w+1]);
        Assert.Equal(2,weights[2*w+3]);
        Assert.Equal(2,weights[2*w+4]);
        // Rotating the hue plane must not change the strength of an edge.
        var rotated = source.Select(c=>new Lab(c.L,0,c.A)).ToArray();
        Assert.Equal(weights,ColorQuantizer.EdgeWeights(rotated,chosen,w,h,1));
        for (int i = 0; i < chosen.Length; i++) if (i%w>=4) chosen[i] = -1;
        var masked = ColorQuantizer.EdgeWeights(source,chosen,w,h,1);
        Assert.Equal(1,masked[2*w+3]);
    }
}
