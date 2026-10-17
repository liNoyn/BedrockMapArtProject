using BMAG.Imaging;

namespace BMAG.Tests;

internal static class TestHelpers
{
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bmag-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static RgbaImage Gradient(int w, int h, bool alpha = false)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img[x, y] = new Rgba((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)),
                    (byte)((x + y) % 256), alpha ? (byte)(x < w / 2 ? 0 : 255) : (byte)255);
        return img;
    }
}
