using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

public class PaletteReductionTests
{
    public static IEnumerable<object[]> Models => Enum.GetValues<DitherMode>().Select(model=>new object[]{model});
    public static IEnumerable<object[]> ModelMetrics => Enum.GetValues<DitherMode>()
        .SelectMany(model=>Enum.GetValues<ColorMetric>().Select(metric=>new object[]{model,metric}));

    [Fact]
    public void HeightSearch_RetainsTheBetterFeasibleRenderFromALowerShadeBudget()
    {
        var profile=ColorProfile.LoadBuiltIn();var support=profile.Find("cobblestone")!;
        var selection=BlockPreset.BuiltIns(profile)[0].ToSelection() with { MaxBlockTypes=2 };
        var source=new RgbaImage(48,48);
        for(int y=0;y<48;y++)for(int x=0;x<48;x++)source[x,y]=Rgba.FromHex(x==46?"#0B0F13":x<24?"#DCD9D3":"#DCDCDC");
        var q=HeightPlanner.Quantize(source,profile,selection,new QuantizeOptions { Dither=DitherMode.Ordered3,Mode=StaircaseMode.Valley,MaxHeight=4 },support,SupportMode.Important,true);
        var layout=BlockLayout.Generate(q,support,supports:SupportMode.Important,onMapGrid:true);
        Assert.InRange(layout.MaxHeight,0,4);
        Assert.InRange(QualityScore.Measure(source,q.Preview).Overall,0,.714);
        Assert.True(ShadingAnalyzer.Analyze(layout,profile,q.Preview).IsExact);
    }

    [Theory]
    [InlineData(DitherMode.Bayer4)]
    [InlineData(DitherMode.Bayer8)]
    public void HeightBudget_DoesNotDiscardAFeasiblePaletteForAnOversizedLowerErrorPlan(DitherMode model)
    {
        const int size=48,width=size*4;
        var large=RgbaImage.Filled(width,width,Rgba.FromHex("#F3EFE4"));
        for(int y=0;y<width;y++)for(int x=0;x<width;x++)
        {
            double xx=(x+.5)*128/width-.5,yy=(y+.5)*128/width-.5;
            double face=Math.Sqrt((xx-40)*(xx-40)/900+(yy-70)*(yy-70)/1600);
            double disc=Math.Sqrt((xx-95)*(xx-95)+(yy-35)*(yy-35));
            if(face<=.93)large[x,y]=Rgba.FromHex("#F2E6DC");
            else if(face<=1)large[x,y]=new Rgba(110,84,66);
            if(disc<=22)large[x,y]=Rgba.FromHex("#7E7CCB");
            else if(disc<=25)large[x,y]=new Rgba(110,84,66);
        }
        var source=Resampler.Resample(large,0,0,width,width,size,size,Interpolation.Lanczos3,linearLight:true);
        var profile=ColorProfile.LoadBuiltIn();var support=profile.Find("cobblestone")!;
        var selection=BlockPreset.BuiltIns(profile)[0].ToSelection() with { MaxBlockTypes=6 };
        var q=HeightPlanner.Quantize(source,profile,selection,new QuantizeOptions { Dither=model,Mode=StaircaseMode.Valley,MaxHeight=4 },support,SupportMode.Important,true);
        var layout=BlockLayout.Generate(q,support,supports:SupportMode.Important,onMapGrid:true);
        // Previously the better-looking oversized plan forced a smaller shade budget: height 3, error > 5.36.
        Assert.Equal(4,layout.MaxHeight);
        Assert.InRange(QualityScore.Measure(source,q.Preview).Overall,0,5.1);
        Assert.True(ShadingAnalyzer.Analyze(layout,profile,q.Preview).IsExact);
        Assert.InRange(q.EntryIndex.Distinct().Count(),1,6);
    }

    [Theory]
    [InlineData(DitherMode.Optimized,48,1)]
    [InlineData(DitherMode.Hybrid,48,1)]
    [InlineData(DitherMode.Optimized,256,1)]
    [InlineData(DitherMode.Hybrid,256,1)]
    [InlineData(DitherMode.Optimized,48,0)]
    [InlineData(DitherMode.Hybrid,48,0)]
    public void StructuralBudget_RetainsOnePixelLineAtDifferentResolutions(DitherMode model,int size,double strength)
    {
        var profile=Profile("#DCD9D3","#DCDCDC","#0B0F13");
        var selection=new PaletteSelection { Keys=profile.Blocks.Select(b=>b.Key).ToHashSet(),MaxBlockTypes=2 };
        var source=new RgbaImage(size,8);
        for(int y=0;y<8;y++)for(int x=0;x<size;x++)source[x,y]=Rgba.FromHex(x==size-2?"#0B0F13":x<size/2?"#DCD9D3":"#DCDCDC");
        var q=ColorQuantizer.Quantize(source,profile,selection,new QuantizeOptions { Dither=model,DitherStrength=strength });
        Assert.All(Enumerable.Range(0,8),y=>Assert.Equal(source[size-2,y],q.Preview[size-2,y]));
        Assert.InRange(q.EntryIndex.Distinct().Count(),1,2);
    }

    [Fact]
    public void FeatureImportance_DetectsChromaticContoursOfSimilarLightness()
    {
        var a=new Rgba(198,50,80);var b=new Rgba(0,130,100);
        Assert.InRange(Math.Abs(ColorMath.ToOklab(a.R,a.G,a.B).L-ColorMath.ToOklab(b.R,b.G,b.B).L),0,.04);
        var source=RgbaImage.Filled(32,8,a);
        for(int y=0;y<8;y++)source[29,y]=b;
        var weights=QualityScore.PaletteWeights(source,source,DitherMode.Optimized)!;
        Assert.True(weights[29]>weights[10]);
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void FeatureImportance_RespectsModelRoleAndHiddenColours(DitherMode model)
    {
        var source=RgbaImage.Filled(32,8,new Rgba(220,220,220));
        var mask=source.Clone();
        for(int y=0;y<8;y++){source[29,y]=new Rgba(10,10,10);mask[0,y]=new Rgba(0,0,0,0);}
        var weights=QualityScore.PaletteWeights(source,mask,model);
        if(model is not (DitherMode.Optimized or DitherMode.Hybrid))
        {
            Assert.Null(weights);
            Assert.Equal(QualityScore.Measure(source,mask),QualityScore.MeasureForModel(source,mask,model));
            return;
        }
        Assert.NotNull(weights);
        Assert.True(weights[29]>weights[10]); // centre, not only the two sides of the line
        Assert.Equal(2*31*8,weights.Sum(),8); // strong features receive half the total weight
        for(int y=0;y<8;y++)source[0,y]=new Rgba(255,0,255);
        Assert.Equal(weights,QualityScore.PaletteWeights(source,mask,model));
        var flat=RgbaImage.Filled(8,8,new Rgba(110,110,110));
        Assert.All(QualityScore.PaletteWeights(flat,flat,model)!,v=>Assert.Equal(1,v));
        Assert.Throws<OperationCanceledException>(()=>QualityScore.PaletteWeights(source,mask,model,new CancellationToken(true)));
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    [InlineData(ColorMetric.Ciede2000)]
    [InlineData(ColorMetric.Rgb)]
    [InlineData(ColorMetric.WeightedRgb)]
    public void FeatureComparison_EqualsIndependentModelScores(ColorMetric metric)
    {
        var source=TestHelpers.Gradient(16,8);
        var a=RgbaImage.Filled(16,8,new Rgba(120,80,60));
        var b=RgbaImage.Filled(16,8,new Rgba(110,130,150));
        var weights=QualityScore.PaletteWeights(source,a,DitherMode.Optimized);
        double Score(RgbaImage image,ColorMetric distance)=>QualityScore.MeasureForModel(source,image,DitherMode.Optimized,distance).Overall;
        bool expected=Score(a,metric)<=Score(b,metric) && Score(a,ColorMetric.Ciede2000)<=Score(b,ColorMetric.Ciede2000)
            && (Score(a,metric)<Score(b,metric)||Score(a,ColorMetric.Ciede2000)<Score(b,ColorMetric.Ciede2000));
        Assert.Equal(expected,QualityScore.PreferCandidate(source,a,b,metric,importance:weights));
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    [InlineData(ColorMetric.Ciede2000)]
    [InlineData(ColorMetric.Rgb)]
    [InlineData(ColorMetric.WeightedRgb)]
    public void CachedGeometry_TracksActualPaletteColoursAndOrder(ColorMetric metric)
    {
        var profile=Profile("#F0C0C0","#B08080","#303030","#C0D0E0");
        var options=new QuantizeOptions { Metric=metric };
        for(int version=0;version<3;version++)
        {
            if(version==1)profile.ColourSets[1].Blocks[0].MapColor="#F0E0D0";
            if(version==2)profile.ColourSets.Reverse();
            var selection=new PaletteSelection { Keys=profile.Blocks.Select(b=>b.Key).ToHashSet() };
            var entries=selection.Resolve(profile);
            var image=new RgbaImage(16,1);
            for(int x=0;x<16;x++)image[x,0]=profile.ColorOf(entries[x%4].Block,Shade.Normal);
            var full=ColorQuantizer.Quantize(image,profile,selection,options);
            var candidates=ColorQuantizer.BuildCandidates(profile,entries,StaircaseMode.Flat);
            var matcher=new ColorQuantizer.NearestMatcher(candidates,metric);
            int expected=Enumerable.Range(0,candidates.Count).OrderBy(c=>Enumerable.Range(0,16).Sum(x=>
            {
                var p=image[x,0];return matcher.Cost(p.R,p.G,p.B,c,0);
            })).First();
            for(int repeat=0;repeat<2;repeat++)
            {
                var plan=PaletteReducer.Select(profile,entries,full,1,options,CancellationToken.None);
                Assert.Single(plan);
                Assert.Same(entries[expected],plan[0]);
            }
        }
    }

    [Fact]
    public void CachedGeometry_CanBeSharedByConcurrentConversions()
    {
        var profile=Profile("#AF03C9","#12EA72","#2468AC","#BDAF64");
        var selection=new PaletteSelection { Keys=profile.Blocks.Select(b=>b.Key).ToHashSet() };
        var entries=selection.Resolve(profile);var image=TestHelpers.Gradient(16,16);
        var options=new QuantizeOptions { Metric=ColorMetric.Ciede2000 };
        var full=ColorQuantizer.Quantize(image,profile,selection,options);
        var candidates=ColorQuantizer.BuildCandidates(profile,entries,StaircaseMode.Flat);
        var matcher=new ColorQuantizer.NearestMatcher(candidates,ColorMetric.Ciede2000);
        int expected=Enumerable.Range(0,entries.Count).OrderBy(c=>Enumerable.Range(0,256).Sum(i=>
        {
            var p=full.Preview[i%16,i/16];return matcher.Cost(p.R,p.G,p.B,c,0);
        })).First();
        // The expected answer is computed without Select, so concurrent calls also race to populate cold rows.
        Parallel.For(0,24,_=>Assert.Same(entries[expected],Assert.Single(PaletteReducer.Select(profile,entries,full,1,options,CancellationToken.None))));
        Assert.Throws<OperationCanceledException>(()=>PaletteReducer.Select(profile,entries,full,1,options,new CancellationToken(true)));
        Assert.Same(entries[expected],Assert.Single(PaletteReducer.Select(profile,entries,full,1,options,CancellationToken.None)));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void BuiltInPalette_TwoColourBudgetComparesBothPlansWithoutWeakeningTheQualityPolicy(DitherMode model)
    {
        var profile=ColorProfile.LoadBuiltIn();
        var selection=BlockPreset.BuiltIns(profile)[0].ToSelection() with { MaxBlockTypes=2 };
        var colours=ColorQuantizer.BuildCandidates(profile,selection.Resolve(profile),StaircaseMode.Flat).Select(c=>c.Color).Distinct().ToArray();
        var light=colours.Where(c=>ColorMath.ToOklab(c.R,c.G,c.B).L>.75).ToArray();
        var pair=light.SelectMany((a,i)=>light.Skip(i+1).Select(b=>(A:a,B:b,Distance:ColorMath.DeltaE76(ColorMath.ToOklab(a.R,a.G,a.B),ColorMath.ToOklab(b.R,b.G,b.B))))).OrderBy(p=>p.Distance).First();
        var dark=colours.OrderBy(c=>ColorMath.ToOklab(c.R,c.G,c.B).L).First();
        var image=new RgbaImage(48,24);
        for(int y=0;y<24;y++)for(int x=0;x<48;x++)image[x,y]=x==46?dark:x<24?pair.A:pair.B;
        var options=new QuantizeOptions { Dither=model };
        var q=ColorQuantizer.Quantize(image,profile,selection,options);
        Assert.InRange(q.EntryIndex.Where(e=>e>=0).Distinct().Count(),1,2);
        var full=ColorQuantizer.Quantize(image,profile,selection with { MaxBlockTypes=0 },options);
        var importance=QualityScore.PaletteWeights(image,full.Preview,model);
        if(model is DitherMode.Optimized or DitherMode.Hybrid)
            Assert.All(Enumerable.Range(0,24),y=>Assert.Equal(dark,q.Preview[46,y]));
        foreach(var metric in new[]{ColorMetric.Oklab,ColorMetric.Ciede2000})
        {
            var plan=PaletteReducer.Select(profile,selection.Resolve(profile),full,2,options with { Metric=metric },CancellationToken.None,importance);
            var subset=selection with { MaxBlockTypes=0,Keys=plan.SelectMany(e=>e.AllMembers.Select(b=>b.Key)).ToHashSet() };
            var alternative=ColorQuantizer.Quantize(image,profile,subset,options);
            Assert.False(QualityScore.PreferCandidate(image,alternative.Preview,q.Preview,options.Metric,importance:importance));
        }
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    [InlineData(ColorMetric.Ciede2000)]
    [InlineData(ColorMetric.Rgb)]
    [InlineData(ColorMetric.WeightedRgb)]
    public void SharedPreparation_EqualsIndependentQualityComparisons(ColorMetric metric)
    {
        var source=TestHelpers.Gradient(8,8);
        var baseline=source.Clone();var candidate=source.Clone();
        for(int y=0;y<8;y++)for(int x=0;x<8;x++)
        {
            if((x+y)%3==0){baseline[x,y]=new Rgba(0,0,0,0);candidate[x,y]=new Rgba(255,0,255,0);}
            else {baseline[x,y]=new Rgba(80,100,120);candidate[x,y]=new Rgba(150,90,140);}
        }
        Check();
        candidate[0,0]=new Rgba(150,90,140); // independent masks also use the original comparison semantics
        Check();
        void Check()
        {
            double old=QualityScore.Measure(source,baseline).Overall,newValue=QualityScore.Measure(source,candidate).Overall;
            double oldMetric=QualityScore.Measure(source,baseline,metric).Overall,newMetric=QualityScore.Measure(source,candidate,metric).Overall;
            bool expected=newValue<=old && newMetric<=oldMetric && (newValue<old || newMetric<oldMetric);
            Assert.Equal(expected,QualityScore.PreferCandidate(source,candidate,baseline,metric));
        }
    }

    [Theory]
    [MemberData(nameof(ModelMetrics))]
    public void FinalBudgetOutput_DoesNotRegressAgainstTheFormerPruningPath(DitherMode model, ColorMetric metric)
    {
        var profile=ColorProfile.LoadBuiltIn();
        var selection=BlockPreset.BuiltIns(profile)[0].ToSelection();
        var source=TestHelpers.Gradient(16,16);
        var options=new QuantizeOptions { Dither=model,Metric=metric,Mode=StaircaseMode.Valley };
        const int limit=4;
        var legacy=ColorQuantizer.Quantize(source,profile,selection,options);
        var full=legacy;
        var importance=QualityScore.PaletteWeights(source,full.Preview,model);
        while(true)
        {
            var counts=new int[legacy.Entries.Count];
            foreach(int e in legacy.EntryIndex)if(e>=0)counts[e]++;
            int used=counts.Count(c=>c>0);if(used<=limit)break;
            int keep=Math.Max(limit,used-Math.Max(1,(used-limit)/2));
            var keys=Enumerable.Range(0,counts.Length).Where(i=>counts[i]>0).OrderByDescending(i=>counts[i]).Take(keep)
                .SelectMany(i=>legacy.Entries[i].AllMembers.Select(b=>b.Key)).ToHashSet();
            legacy=ColorQuantizer.Quantize(source,profile,selection with { Keys=keys },options);
        }
        var actual=ColorQuantizer.Quantize(source,profile,selection with { MaxBlockTypes=limit },options);
        foreach(var distance in (importance is null?new[]{metric,ColorMetric.Ciede2000}.Distinct():Enumerable.Empty<ColorMetric>()))
        {
            double before=QualityScore.MeasureForModel(source,legacy.Preview,model,distance).Overall;
            double after=QualityScore.MeasureForModel(source,actual.Preview,model,distance).Overall;
            Assert.True(after<=before+1e-9*Math.Max(1,before),$"{model}/{metric}: {distance} error {before} -> {after}");
        }
        Assert.InRange(actual.EntryIndex.Where(e=>e>=0).Distinct().Count(),1,limit);
        QuantizeResult previous=legacy;
        foreach(var planningMetric in new[]{metric,ColorMetric.Ciede2000}.Distinct())
        {
            var plan=PaletteReducer.Select(profile,selection.Resolve(profile),full,limit,options with { Metric=planningMetric },CancellationToken.None);
            var subset=selection with { Keys=plan.SelectMany(e=>e.AllMembers.Select(b=>b.Key)).ToHashSet() };
            var alternative=ColorQuantizer.Quantize(source,profile,subset,options);
            if(QualityScore.PreferCandidate(source,alternative.Preview,previous.Preview,metric))previous=alternative;
            Assert.False(QualityScore.PreferCandidate(source,alternative.Preview,actual.Preview,options.Metric,importance:importance));
            if(importance is not null)
            {
                var featurePlan=PaletteReducer.Select(profile,selection.Resolve(profile),full,limit,options with { Metric=planningMetric },CancellationToken.None,importance);
                var featureSubset=selection with { Keys=featurePlan.SelectMany(e=>e.AllMembers.Select(b=>b.Key)).ToHashSet() };
                var featureAlternative=ColorQuantizer.Quantize(source,profile,featureSubset,options);
                Assert.False(QualityScore.PreferCandidate(source,featureAlternative.Preview,actual.Preview,metric,importance:importance));
            }
        }
        foreach(var distance in new[]{metric,ColorMetric.Ciede2000}.Distinct())
            Assert.True(QualityScore.MeasureForModel(source,actual.Preview,model,distance).Overall<=QualityScore.MeasureForModel(source,previous.Preview,model,distance).Overall+1e-9);
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    [InlineData(ColorMetric.Ciede2000)]
    [InlineData(ColorMetric.Rgb)]
    [InlineData(ColorMetric.WeightedRgb)]
    public void MultiscaleScore_UsesTheSelectedMatchersDistance(ColorMetric metric)
    {
        var source=new Rgba(37,89,153);var target=new Rgba(110,52,205);
        var matcher=new ColorQuantizer.NearestMatcher([new Candidate(0,Shade.Normal,target)],metric);
        double expected=matcher.Cost(source.R,source.G,source.B,0,0);
        var score=QualityScore.Measure(RgbaImage.Filled(1,1,source),RgbaImage.Filled(1,1,target),metric);
        Assert.True(Math.Abs(expected-score.Pixel)<1e-8);
        Assert.True(Math.Abs(expected-score.Near)<1e-8);
        Assert.True(Math.Abs(expected-score.Far)<1e-8);
    }

    private static ColorProfile Profile(params string[] colours) => new()
    {
        Id="palette-reduction",Name="test",Shades=new ShadeMultipliers { Normal=255 },
        ColourSets=colours.Select((hex,i)=>new ColourSet
        {
            Id=i+1,Name=$"colour-{i}",MapColor=hex,
            Blocks=[new BlockDefinition { Key=$"colour-{i}",Name="minecraft:stone",DisplayName=$"colour-{i}",MapColor=hex,ColourSetId=i+1 }],
        }).ToList(),
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void Budget_KeepsRareContrastingOutlineInsteadOfTwoRedundantBackgroundColours(DitherMode model)
    {
        var profile=Profile("#E6E6E6","#E4E4E4","#000000");
        var selection=new PaletteSelection { Keys=profile.Blocks.Select(b=>b.Key).ToHashSet(),MaxBlockTypes=2 };
        var image=new RgbaImage(64,64);
        for(int y=0;y<64;y++)for(int x=0;x<64;x++) image[x,y]=Rgba.FromHex(x==63?"#000000":x<32?"#E6E6E6":"#E4E4E4");
        var q=ColorQuantizer.Quantize(image,profile,selection,new QuantizeOptions { Dither=model });
        Assert.InRange(q.EntryIndex.Where(e=>e>=0).Distinct().Count(),1,2);
        Assert.All(Enumerable.Range(0,64),y=>Assert.Equal(new Rgba(0,0,0),q.Preview[63,y]));
        Assert.True(QualityScore.Measure(image,q.Preview).Overall<.8);
        var repeat=ColorQuantizer.Quantize(image,profile,selection,new QuantizeOptions { Dither=model });
        Assert.Equal(q.Preview.Pixels,repeat.Preview.Pixels);
    }

    [Theory]
    [InlineData(ColorMetric.Oklab)]
    [InlineData(ColorMetric.Lab)]
    [InlineData(ColorMetric.Ciede2000)]
    [InlineData(ColorMetric.Rgb)]
    [InlineData(ColorMetric.WeightedRgb)]
    public void OneColourProposal_EqualsExhaustiveWeightedChoice(ColorMetric metric)
    {
        string[] colours=["#FFF0ED","#E0C5C4","#403A39","#B26567","#C0C0C0"];
        int[] weights=[14,31,8,6,1];
        var profile=Profile(colours);
        var selection=new PaletteSelection { Keys=profile.Blocks.Select(b=>b.Key).ToHashSet(),MaxBlockTypes=1 };
        var source=new RgbaImage(weights.Sum(),1);int x=0;
        for(int c=0;c<weights.Length;c++)for(int i=0;i<weights[c];i++)source[x++,0]=Rgba.FromHex(colours[c]);
        var candidates=ColorQuantizer.BuildCandidates(profile,selection.Resolve(profile),StaircaseMode.Flat);
        var matcher=new ColorQuantizer.NearestMatcher(candidates,metric);
        double Cost(int candidate) => Enumerable.Range(0,colours.Length).Sum(i=>
        {
            var colour=Rgba.FromHex(colours[i]);return weights[i]*matcher.Cost(colour.R,colour.G,colour.B,candidate,0);
        });
        int expected=Enumerable.Range(0,candidates.Count).OrderBy(Cost).First();
        var options=new QuantizeOptions { Metric=metric };
        var full=ColorQuantizer.Quantize(source,profile,selection with { MaxBlockTypes=0 },options);
        var proposal=PaletteReducer.Select(profile,selection.Resolve(profile),full,1,options,CancellationToken.None);
        Assert.Single(proposal);
        Assert.Equal(candidates[expected].Color,profile.ColorOf(proposal[0].Block,Shade.Normal));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public void HiddenPixels_DoNotInfluenceBudgetSelection(DitherMode model)
    {
        var profile=ColorProfile.LoadBuiltIn();
        var selection=BlockPreset.BuiltIns(profile)[0].ToSelection() with { MaxBlockTypes=4 };
        var a=TestHelpers.Gradient(16,16);var b=a.Clone();
        for(int y=0;y<16;y++)for(int x=0;x<16;x++)
            if((x+y)%3==0){ a[x,y]=new Rgba(255,0,255,64);b[x,y]=new Rgba(0,255,0,64); }
        var options=new QuantizeOptions { Dither=model,Mode=StaircaseMode.Valley };
        var qa=ColorQuantizer.Quantize(a,profile,selection,options);
        var qb=ColorQuantizer.Quantize(b,profile,selection,options);
        Assert.Equal(qa.Preview.Pixels,qb.Preview.Pixels);
        Assert.InRange(qa.EntryIndex.Where(e=>e>=0).Distinct().Count(),1,4);
    }
}
