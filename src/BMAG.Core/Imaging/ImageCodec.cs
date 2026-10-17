using SkiaSharp;

namespace BMAG.Imaging;

/// <summary>Isolates file-format specific decoding/encoding (design 5.1). Everything else works on <see cref="RgbaImage"/>.</summary>
public static class ImageCodec
{
    public static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"];

    public static RgbaImage Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Decode(stream);
    }

    /// <summary>Decodes an image. Animated GIFs yield their first frame.</summary>
    public static RgbaImage Decode(Stream stream)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("対応していない画像形式です。");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"画像をデコードできませんでした ({result})。");
        var pixels = new byte[info.Width * info.Height * 4];
        System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return ApplyOrigin(new RgbaImage(info.Width, info.Height, pixels), codec.EncodedOrigin);
    }

    /// <summary>Applies the EXIF orientation of photos so the image appears the way viewers show it.</summary>
    private static RgbaImage ApplyOrigin(RgbaImage src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default) return src;
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? src.Height : src.Width, h = swap ? src.Width : src.Height;
        var dst = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            (int sx, int sy) = origin switch
            {
                SKEncodedOrigin.TopRight => (src.Width - 1 - x, y),
                SKEncodedOrigin.BottomRight => (src.Width - 1 - x, src.Height - 1 - y),
                SKEncodedOrigin.BottomLeft => (x, src.Height - 1 - y),
                SKEncodedOrigin.LeftTop => (y, x),
                SKEncodedOrigin.RightTop => (y, src.Height - 1 - x),
                SKEncodedOrigin.RightBottom => (src.Width - 1 - y, src.Height - 1 - x),
                SKEncodedOrigin.LeftBottom => (src.Width - 1 - y, x),
                _ => (x, y),
            };
            dst[x, y] = src[sx, sy];
        }
        return dst;
    }

    public static void SavePng(RgbaImage image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var fs = File.Create(path);
        fs.Write(EncodePng(image));
    }

    public static byte[] EncodePng(RgbaImage image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(image.Pixels, 0, bitmap.GetPixels(), image.Pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
