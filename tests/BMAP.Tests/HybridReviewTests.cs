using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

public class HybridReviewTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static PaletteSelection Selection => BlockPreset.BuiltIns(Profile)[0].ToSelection();

    [Fact]
    public void FrozenPixel_IsNotReplacedByPairCandidatePlanning()
    {
        var image = RgbaImage.Filled(8, 8, new Rgba(128,128,128));
        var candidates = new List<Candidate> { new(0,Shade.Normal,new Rgba(96,96,96)), new(1,Shade.Normal,new Rgba(160,160,160)), new(2,Shade.Normal,new Rgba(255,0,0)) };
        var chosen = Enumerable.Repeat(1,64).ToArray();
        chosen[0] = 2;
        var frozen = new bool[64]; frozen[0] = true;
        ColorQuantizer.Refine(image,8,8,candidates,chosen,false,new RefineSettings { PairLambda=0.01, Neighbours=2, MaxPasses=0 },default,frozen);
        Assert.Equal(2,chosen[0]);
    }

    [Fact]
    public void Hybrid_ZeroStrengthMatchesNoDither()
    {
        var image = RgbaImage.Filled(32,32,Rgba.FromHex("#FCBCD4"));
        var options = new QuantizeOptions { Dither=DitherMode.Hybrid, Mode=StaircaseMode.Classic, DitherStrength=0 };
        var hybrid = ColorQuantizer.Quantize(image,Profile,Selection,options);
        var none = ColorQuantizer.Quantize(image,Profile,Selection,options with { Dither=DitherMode.None });
        Assert.Equal(none.Preview.Pixels,hybrid.Preview.Pixels);
    }

    [Theory]
    [InlineData(2, false, 0)]
    [InlineData(4, true, 0)]
    [InlineData(4, false, 4)]
    [InlineData(8, true, 4)]
    [InlineData(4, true, 2)]
    [InlineData(16, true, 0)]
    public void HeightBudget_WithMaterialLimit_IsRespected(int limit,bool grid,int types)
    {
        var image = TestHelpers.Gradient(32,48);
        var options = new QuantizeOptions { Dither=DitherMode.Hybrid,Mode=StaircaseMode.Valley,MaxHeight=limit };
        var q = HeightPlanner.Quantize(image,Profile,Selection with { MaxBlockTypes=types },options,Profile.Find("cobblestone")!,SupportMode.AllDouble,grid);
        var layout=BlockLayout.Generate(q,Profile.Find("cobblestone")!,supports:SupportMode.AllDouble,onMapGrid:grid);
        Assert.InRange(layout.MaxHeight,0,limit);
        Assert.True(ShadingAnalyzer.Analyze(layout,Profile,q.Preview).IsExact);
        if(types>0) Assert.InRange(q.EntryIndex.Distinct().Count(),1,types);
    }

    [Fact]
    public void FlatDetection_UsesTheAlphaThresholdBeforeDenoising_AndCanCancel()
    {
        var a=TestHelpers.Gradient(64,64);
        var b=a.Clone();
        for(int y=0;y<64;y++)for(int x=0;x<64;x++)
            if((x+y)%5==0){a[x,y]=new Rgba(130,128,128,64);b[x,y]=new Rgba(255,0,255,0);}
        Assert.Equal(ColorQuantizer.FlatAreas(a,128),ColorQuantizer.FlatAreas(b,128));
        using var ct=new CancellationTokenSource();ct.Cancel();
        Assert.Throws<OperationCanceledException>(()=>ColorQuantizer.FlatAreas(a,128,ct:ct.Token));
    }

    [Fact]
    public void FlatAndGradientShortcuts_PreserveTheirRespectiveModels()
    {
        var gradient=TestHelpers.Gradient(48,48);
        Assert.DoesNotContain(true,ColorQuantizer.FlatAreas(gradient,128));
        var options=new QuantizeOptions{Mode=StaircaseMode.Valley,Dither=DitherMode.Hybrid};
        Assert.Equal(ColorQuantizer.Quantize(gradient,Profile,Selection,options with{Dither=DitherMode.Perceptual}).Preview.Pixels,
            ColorQuantizer.Quantize(gradient,Profile,Selection,options).Preview.Pixels);
        var flat=RgbaImage.Filled(48,48,Rgba.FromHex("#FCBCD4"));
        Assert.All(ColorQuantizer.FlatAreas(flat,128),Assert.True);
        Assert.Equal(ColorQuantizer.Quantize(flat,Profile,Selection,options with{Dither=DitherMode.Optimized}).Preview.Pixels,
            ColorQuantizer.Quantize(flat,Profile,Selection,options).Preview.Pixels);
    }

    [Fact]
    public void QuantizedSky_IsNotFrozenAsOneLargeFlatArea()
    {
        const int size=96;
        var image=new RgbaImage(size,size);
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
        {
            double v=y/(size-1.0);
            image[x,y]=new Rgba((byte)Math.Round(130+30*v),(byte)Math.Round(175+35*v),(byte)Math.Round(225+20*v));
        }
        Assert.InRange(ColorQuantizer.FlatAreas(image,128).Count(v=>v),0,size*size/10);
        var options=new QuantizeOptions{Dither=DitherMode.Hybrid,Mode=StaircaseMode.Valley};
        var hybrid=QualityScore.Measure(image,ColorQuantizer.Quantize(image,Profile,Selection,options).Preview);
        var perceptual=QualityScore.Measure(image,ColorQuantizer.Quantize(image,Profile,Selection,options with{Dither=DitherMode.Perceptual}).Preview);
        Assert.InRange(hybrid.Far,0,perceptual.Far*1.05);
    }
}
