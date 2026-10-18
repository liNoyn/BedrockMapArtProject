using BMAP.Bedrock;
using BMAP.Imaging;
using BMAP.Nbt;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Tests;

/// <summary>
/// Each candidate rule from the research (Java, step 2 like gd-codes / Nukkit, gd-codes rounding, Java formula in floating
/// point = checkerboard, PowerNukkitX graded, asymmetric steps) is used to "draw" the shading pad; the calibrator must
/// name that rule from the picture alone.
/// </summary>
public class ModelIdentificationTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static BlockDefinition Cobble => Profile.Find("cobblestone")!;

    private static ColorProfile Game(ShadeMultipliers rule) => new() { Id = "game", Name = "game", Shades = rule, ColourSets = Profile.ColourSets };

    private static CalibrationResult Measure(ShadeMultipliers rule)
    {
        var pad = ShadingTestPad.Create(Profile, Cobble);
        var img = MapSimulator.Simulate(pad.Layout, Game(rule)).Image;
        var map = new MapRecord
        {
            Id = 1,
            Nbt = new NbtCompound
            {
                { "scale", new NbtByte(0) }, { "dimension", new NbtByte(0) }, { "width", new NbtShort(128) }, { "height", new NbtShort(128) },
                { "xCenter", new NbtInt(64) }, { "zCenter", new NbtInt(64) }, { "colors", new NbtByteArray(img.Pixels) },
            },
        };
        return Calibrator.Analyze(pad.Descriptor, map, Profile, "test");
    }

    [Fact]
    public void JavaRule_IsIdentified()
    {
        var r = Measure(new ShadeMultipliers());
        Assert.Equal(1.0, r.BestModel!.ValidationAccuracy, 3);
        Assert.Equal(ShadingModelKind.Threshold, r.BestModel.Model.Kind);
        Assert.Equal(1, r.BestModel.Model.BrightStep);
        Assert.Equal(1.0, r.BestModel.ExactRate, 3);
    }

    [Fact]
    public void StepTwo_IsIdentified_AndDistinguishedFromJava()
    {
        var r = Measure(new ShadeMultipliers { MinStep = 2 });
        Assert.Equal(2, r.BestModel!.Model.BrightStep);
        Assert.Equal(2, r.BestModel.Model.DarkStep);
        Assert.Equal(1.0, r.BestModel.ValidationAccuracy, 3);
        Assert.True(r.Models.Single(m => m.Model.Id == "java-step1").ValidationAccuracy < 0.8);
    }

    [Fact]
    public void GdCodesRounding_IsPreferredWhenTheGameRoundsThatWay()
    {
        var r = Measure(new ShadeMultipliers { MinStep = 2, Rounding = ShadeRounding.FromNormal });
        Assert.Equal("step2-normal", r.BestModel!.Model.Id);
        Assert.True(r.BestModel.ExactRate > r.Models.Single(m => m.Model.Id == "step2").ExactRate);
    }

    [Fact]
    public void Checkerboard_IsIdentified_AndParityIsReported()
    {
        var r = Measure(new ShadeMultipliers { Model = ShadingModelKind.Checker });
        Assert.Equal("checker", r.BestModel!.Model.Id);
        Assert.Contains(r.Parity, p => p.Delta == 1 && Math.Abs(p.EvenRatio - p.OddRatio) > 0.05);
    }

    [Fact]
    public void Graded_IsIdentified()
    {
        var r = Measure(new ShadeMultipliers { Model = ShadingModelKind.Graded });
        Assert.Equal("graded", r.BestModel!.Model.Id);
        Assert.True(r.Graded);
    }

    [Fact]
    public void AsymmetricSteps_AreKeptSeparately()
    {
        var r = Measure(new ShadeMultipliers { BrightStep = 1, DarkStep = 2 });
        Assert.Equal(1, r.BrightStep);
        Assert.Equal(2, r.DarkStep);
        Assert.Equal(1.0, r.BestModel!.ValidationAccuracy, 3);
        var rule = r.MeasuredRule("test")!;
        Assert.Equal(1, rule.BrightStep);
        Assert.Equal(2, rule.DarkStep);
        Assert.Equal(1, rule.UpThreshold);
        Assert.Equal(2, rule.DownThreshold);
    }
}

public class CheckerArithmeticTests
{
    [Fact]
    public void JavaFormulaInDoubles_EqualsStepOne()
    {
        // The parity term cannot change the result in double arithmetic: 0.8 - 0.2 rounds above 0.6.
        double d = 1 * 4.0 / 5.0 + (0 - 0.5) * 0.4;
        Assert.True(d > 0.6);
        var exact = ShadingModel.Candidates.Single(m => m.Id == "checker");
        Assert.Equal(Shade.Normal, exact.Classify(1, 0, 0));
        Assert.Equal(Shade.Bright, exact.Classify(1, 1, 0));
        Assert.Equal(Shade.Bright, exact.Classify(2, 0, 0));
        Assert.Equal(Shade.Dark, exact.Classify(-1, 0, 0));
        Assert.Equal(Shade.Normal, exact.Classify(-1, 1, 0));
    }
}

/// <summary>Staircases for the parity rule measured on Bedrock 1.26.51 (±1 alternates by x + row, ±2 always).</summary>
public class ParityStairTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static ColorProfile Checker => new()
    {
        Id = "checker", Name = "checker", ColourSets = Profile.ColourSets,
        Shades = new ShadeMultipliers { Model = ShadingModelKind.Checker, BrightStep = 2, DarkStep = 2 },
    };

    [Theory]
    [InlineData(BMAP.BlockEngine.StaircaseMode.Valley, BMAP.BlockEngine.DitherMode.FloydSteinberg)]
    [InlineData(BMAP.BlockEngine.StaircaseMode.Classic, BMAP.BlockEngine.DitherMode.None)]
    [InlineData(BMAP.BlockEngine.StaircaseMode.Valley, BMAP.BlockEngine.DitherMode.KnollBayer)]
    public void ParitySteps_SimulateExactly_AndBuildLower(BMAP.BlockEngine.StaircaseMode mode, BMAP.BlockEngine.DitherMode dither)
    {
        var game = Checker;
        var q = BMAP.BlockEngine.ColorQuantizer.Quantize(TestHelpers.Gradient(128, 128), game,
            BMAP.BlockEngine.BlockPreset.BuiltIns(game)[0].ToSelection(), new BMAP.BlockEngine.QuantizeOptions { Mode = mode, Dither = dither });
        Assert.True(q.ParityShading);
        var cobble = game.Find("cobblestone")!;
        var onGrid = BMAP.BlockEngine.BlockLayout.Generate(q, cobble, 0, BMAP.BlockEngine.SupportMode.Important, onMapGrid: true);
        var anywhere = BMAP.BlockEngine.BlockLayout.Generate(q, cobble, 0, BMAP.BlockEngine.SupportMode.Important, onMapGrid: false);
        Assert.True(onGrid.ParitySteps);
        Assert.False(anywhere.ParitySteps);
        Assert.Equal(0, ShadingAnalyzer.Analyze(onGrid, game, q.Preview).Mismatches);
        Assert.Equal(0, ShadingAnalyzer.Analyze(anywhere, game, q.Preview).Mismatches);
        Assert.True(onGrid.MaxHeight < anywhere.MaxHeight, $"{onGrid.MaxHeight} vs {anywhere.MaxHeight}");
    }

    [Fact]
    public void StepTwoStairs_AreWrongWithAStepOneLayout()
    {
        // A Java-style layout (steps of 1) drawn by the measured Bedrock rule loses about half of its shades.
        var java = new ColorProfile { Id = "java", Name = "java", Shades = new ShadeMultipliers(), ColourSets = Profile.ColourSets };
        var q = BMAP.BlockEngine.ColorQuantizer.Quantize(TestHelpers.Gradient(64, 64), java,
            BMAP.BlockEngine.BlockPreset.BuiltIns(java)[0].ToSelection(), new BMAP.BlockEngine.QuantizeOptions { Mode = BMAP.BlockEngine.StaircaseMode.Valley });
        var javaLayout = BMAP.BlockEngine.BlockLayout.Generate(q, Profile.Find("cobblestone")!);
        Assert.True(ShadingAnalyzer.Analyze(javaLayout, Checker, q.Preview).Mismatches > 0);
    }
}
