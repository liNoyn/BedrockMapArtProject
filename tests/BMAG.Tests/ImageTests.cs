using BMAG.Imaging;

namespace BMAG.Tests;

public class ImageTests
{
    [Fact] // T01
    public void Image128_BecomesOneMap()
    {
        var src = TestHelpers.Gradient(128, 128);
        var img = ImagePipeline.Process(src, new ImageSettings { Interpolation = Interpolation.Nearest });
        var tiles = TileSplitter.Split(img, 1, 1);
        Assert.Single(tiles);
        Assert.Equal(src.Pixels, tiles[0].Image.Pixels); // identity at 1:1 with nearest
    }

    [Fact] // T02
    public void Image256_SplitsIntoFour_InReadingOrder()
    {
        var src = TestHelpers.Gradient(256, 256);
        var img = ImagePipeline.Process(src, new ImageSettings { MapsX = 2, MapsY = 2, Interpolation = Interpolation.Nearest });
        var tiles = TileSplitter.Split(img, 2, 2);
        Assert.Equal(4, tiles.Count);
        Assert.Equal((1, 0), (tiles[1].GridX, tiles[1].GridY));
        Assert.Equal((0, 1), (tiles[2].GridX, tiles[2].GridY));
        Assert.Equal(src[128, 0], tiles[1].Image[0, 0]);
        Assert.Equal(src[0, 128], tiles[2].Image[0, 0]);
    }

    [Fact] // T03
    public void Fit_KeepsAspectRatio_WithLetterbox()
    {
        var src = RgbaImage.Filled(400, 200, new Rgba(255, 0, 0));
        var img = ImagePipeline.Process(src, new ImageSettings { Fit = FitMode.Fit, BackgroundColor = "#0000FF" });
        Assert.Equal(new Rgba(0, 0, 255), img[64, 5]);    // letterbox (top)
        Assert.Equal(new Rgba(255, 0, 0), img[64, 64]);   // image
        Assert.Equal(new Rgba(0, 0, 255), img[64, 122]);  // letterbox (bottom)
        Assert.Equal(new Rgba(255, 0, 0), img[0, 64]);    // touches left edge
    }

    [Fact]
    public void Fill_CoversWholeOutput()
    {
        var src = RgbaImage.Filled(400, 200, new Rgba(10, 200, 30));
        var img = ImagePipeline.Process(src, new ImageSettings { Fit = FitMode.Fill });
        for (int y = 0; y < 128; y += 7) Assert.Equal(new Rgba(10, 200, 30), img[0, y]);
    }

    [Fact] // T04
    public void Transparency_IsCompositedOntoBackground_ByDefault()
    {
        var src = TestHelpers.Gradient(128, 128, alpha: true);
        var img = ImagePipeline.Process(src, new ImageSettings { Interpolation = Interpolation.Nearest, BackgroundColor = "#123456" });
        Assert.Equal(new Rgba(0x12, 0x34, 0x56), img[0, 0]);
        var kept = ImagePipeline.Process(src, new ImageSettings { Interpolation = Interpolation.Nearest, KeepTransparency = true });
        Assert.Equal(0, kept[0, 0].A);
        Assert.Equal(255, kept[127, 0].A);
    }

    [Fact] // T05 (PNG round trip through the codec)
    public void Png_RoundTrips()
    {
        var src = TestHelpers.Gradient(37, 23, alpha: true);
        var png = ImageCodec.EncodePng(src);
        var back = ImageCodec.Decode(new MemoryStream(png));
        Assert.Equal(src.Pixels, back.Pixels);
    }

    [Fact]
    public void Lanczos_IsDeterministic_AndKeepsSolidColour()
    {
        var src = RgbaImage.Filled(333, 517, new Rgba(90, 120, 200));
        var s = new ImageSettings { MapsX = 2, MapsY = 1, Fit = FitMode.Stretch };
        var a = ImagePipeline.Process(src, s);
        var b = ImagePipeline.Process(src, s);
        Assert.Equal(a.Pixels, b.Pixels);
        Assert.All(Enumerable.Range(0, 256), x => Assert.Equal(new Rgba(90, 120, 200), a[x, 64]));
    }

    [Fact] // T07
    public void LargeImage_CanBeCancelled()
    {
        var src = TestHelpers.Gradient(3000, 3000);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ImagePipeline.Process(src, new ImageSettings { MapsX = 4, MapsY = 4 }, cts.Token));
    }
}
