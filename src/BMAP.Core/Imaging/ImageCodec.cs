using SkiaSharp;

namespace BMAP.Imaging;

/// <summary>Isolates file-format specific decoding/encoding (design 5.1). Everything else works on <see cref="RgbaImage"/>.</summary>
public static class ImageCodec
{
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".tif", ".tiff"];
    public static readonly string[] VideoExtensions = [".mp4", ".mov", ".webm", ".mkv", ".avi", ".m4v"];
    public static string[] SupportedExtensions => [.. ImageExtensions, .. VideoExtensions];

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    public static bool IsTiff(string path) => Path.GetExtension(path).ToLowerInvariant() is ".tif" or ".tiff";

    /// <param name="frame">Frame of an animated GIF/WebP or page of a TIFF (0-based).</param>
    /// <param name="videoSeconds">Position in a video file (needs ffmpeg on PATH).</param>
    public static RgbaImage Load(string path, int frame = 0, double videoSeconds = 0)
    {
        if (IsVideo(path)) return VideoFrame.Extract(path, videoSeconds);
        if (IsTiff(path))
            return OperatingSystem.IsWindows() ? TiffDecoder.Load(path, frame) : throw new PlatformNotSupportedException("TIFF は Windows でのみ対応しています。");
        using var stream = File.OpenRead(path);
        return Decode(stream, frame);
    }

    /// <summary>Number of frames/pages (1 for still images, 0 for videos: use a time instead).</summary>
    public static int FrameCount(string path)
    {
        if (IsVideo(path)) return 0;
        if (IsTiff(path)) return OperatingSystem.IsWindows() ? TiffDecoder.PageCount(path) : 1;
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        return Math.Max(1, codec?.FrameCount ?? 1);
    }

    /// <summary>Decodes an image (optionally one frame of an animation).</summary>
    public static RgbaImage Decode(Stream stream, int frame = 0)
    {
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("対応していない画像形式です。");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        frame = Math.Clamp(frame, 0, Math.Max(0, codec.FrameCount - 1));
        using var bitmap = new SKBitmap(info);
        if (frame == 0 || codec.FrameCount <= 1)
        {
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                throw new InvalidDataException($"画像をデコードできませんでした ({result})。");
        }
        else
        {
            // Animated frames may depend on earlier ones: decode the chain in order, keeping required frames.
            var frames = codec.FrameInfo;
            var decoded = new Dictionary<int, byte[]>();
            int size = info.BytesSize;
            for (int j = 0; j <= frame; j++)
            {
                int required = frames[j].RequiredFrame;
                var target = new byte[size];
                if (required >= 0 && decoded.TryGetValue(required, out var prior)) prior.CopyTo(target, 0);
                var handle = System.Runtime.InteropServices.GCHandle.Alloc(target, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    var r = codec.GetPixels(info, handle.AddrOfPinnedObject(), new SKCodecOptions(j, required));
                    if (r is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                        throw new InvalidDataException($"フレーム {j} をデコードできませんでした ({r})。");
                }
                finally { handle.Free(); }
                decoded[j] = target;
            }
            System.Runtime.InteropServices.Marshal.Copy(decoded[frame], 0, bitmap.GetPixels(), size);
        }
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
