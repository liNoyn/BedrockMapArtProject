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
        var kept = ImagePipeline.Process(src, new ImageSettings { Interpolation = Interpolation.Nearest, Transparency = TransparencyMode.Keep });
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

    [Fact]
    public void CenterCrop_UsesOutputAspect()
    {
        // Left half red, right half blue, 400x100: a centred 2:1 crop keeps both colours at the edges.
        var src = new RgbaImage(400, 100);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 400; x++) src[x, y] = x < 200 ? new Rgba(255, 0, 0) : new Rgba(0, 0, 255);
        var img = ImagePipeline.Process(src, new ImageSettings { MapsX = 2, MapsY = 1, CropMode = CropMode.Center, Fit = FitMode.Fit });
        Assert.Equal(new Rgba(255, 0, 0), img[2, 64]);
        Assert.Equal(new Rgba(0, 0, 255), img[253, 64]);
        Assert.Equal(new Rgba(255, 0, 0), img[100, 0]); // no letterbox
    }

    [Fact]
    public void DitheredTransparency_IsBinary()
    {
        var src = RgbaImage.Filled(128, 128, new Rgba(0, 0, 0, 128));
        var img = ImagePipeline.Process(src, new ImageSettings { Transparency = TransparencyMode.Dithered, BackgroundColor = "#FFFFFF", Interpolation = Interpolation.Nearest });
        var colours = Enumerable.Range(0, 128 * 128).Select(i => img[i % 128, i / 128]).Distinct().ToList();
        Assert.Equal(2, colours.Count);
        Assert.All(colours, c => Assert.Equal(255, c.A));
    }

    [Fact]
    public void Tga_Decodes()
    {
        // 2x2, 32-bit, uncompressed, top-down.
        var tga = new byte[18 + 16];
        tga[2] = 2; tga[12] = 2; tga[14] = 2; tga[16] = 32; tga[17] = 0x28;
        byte[] px = [0, 0, 255, 255, /**/ 0, 255, 0, 255, /**/ 255, 0, 0, 255, /**/ 0, 0, 0, 0];
        px.CopyTo(tga, 18);
        var img = BMAG.Textures.TgaDecoder.Decode(tga);
        Assert.Equal(new Rgba(255, 0, 0), img[0, 0]);
        Assert.Equal(new Rgba(0, 255, 0), img[1, 0]);
        Assert.Equal(new Rgba(0, 0, 255), img[0, 1]);
        Assert.Equal(0, img[1, 1].A);
    }

    [Fact]
    public void Textures_ResolveThroughBlocksAndTerrainJson()
    {
        var dir = TestHelpers.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "textures", "blocks"));
        File.WriteAllText(Path.Combine(dir, "blocks.json"), """
            { "white_wool": { "textures": "wool_colored_white" },
              "grass": { "textures": { "up": "grass_top", "side": "grass_side" }, "carried_textures": { "side": "grass_carried" } } }
            """);
        File.WriteAllText(Path.Combine(dir, "textures", "terrain_texture.json"), """
            // header comment
            { "texture_data": {
                "wool_colored_white": { "textures": "textures/blocks/wool_colored_white" },
                "grass_carried": { "textures": { "path": "textures/blocks/grass_side", "overlay_color": "#79c05a" } } } }
            """);
        ImageCodec.SavePng(RgbaImage.Filled(16, 16, new Rgba(200, 200, 200)), Path.Combine(dir, "textures", "blocks", "wool_colored_white.png"));
        ImageCodec.SavePng(RgbaImage.Filled(16, 32, new Rgba(128, 128, 128, 255)), Path.Combine(dir, "textures", "blocks", "grass_side.png"));
        var provider = new BMAG.Textures.BlockTextureProvider(dir);
        var profile = BMAG.Palette.ColorProfile.LoadBuiltIn();
        var wool = provider.GetIcon(profile.Find("white_wool")!)!;
        Assert.Equal(new Rgba(200, 200, 200), wool[0, 0]);
        var grass = provider.GetIcon(profile.Find("grass_block")!)!; // alias grass_block -> grass, tinted, animated strip cropped
        Assert.Equal((16, 16), (grass.Width, grass.Height));
        Assert.Equal(new Rgba(128 * 0x79 / 255, 128 * 0xC0 / 255, 128 * 0x5A / 255), grass[0, 0]);
        Assert.Null(provider.GetIcon(profile.Find("stone")!));
    }

    [Fact]
    public void LinearLightResize_KeepsAverageBrightness()
    {
        // A fine black/white checkerboard averages to mid grey: ~188 in linear light vs ~128 in gamma space.
        var src = new RgbaImage(256, 256);
        for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++) src[x, y] = (x + y) % 2 == 0 ? new Rgba(0, 0, 0) : new Rgba(255, 255, 255);
        var lin = ImagePipeline.Process(src, new ImageSettings { Interpolation = Interpolation.Bilinear, LinearLight = true });
        var gam = ImagePipeline.Process(src, new ImageSettings { Interpolation = Interpolation.Bilinear, LinearLight = false });
        Assert.InRange((int)lin[64, 64].R, 180, 195);
        Assert.InRange((int)gam[64, 64].R, 120, 135);
    }

    [Fact]
    public void Oklab_OrdersColoursPerceptually()
    {
        var white = BMAG.ColorEngine.ColorMath.ToOklab(255, 255, 255);
        var black = BMAG.ColorEngine.ColorMath.ToOklab(0, 0, 0);
        Assert.Equal(1.0, white.L, 3);
        Assert.Equal(0.0, black.L, 3);
        Assert.Equal(0.0, white.A, 3);
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
