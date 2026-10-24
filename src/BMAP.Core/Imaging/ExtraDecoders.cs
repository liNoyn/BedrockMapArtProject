using System.Diagnostics;
using System.Runtime.Versioning;

namespace BMAP.Imaging;

/// <summary>TIFF (including multi-page) through Windows' built-in imaging codecs.</summary>
[SupportedOSPlatform("windows")]
public static class TiffDecoder
{
    public static int PageCount(string path)
    {
        EnsureWindows();
        return WithImage(path, img => img.GetFrameCount(System.Drawing.Imaging.FrameDimension.Page));
    }

    public static RgbaImage Load(string path, int page = 0)
    {
        EnsureWindows();
        return WithImage(path, img =>
        {
            int pages = img.GetFrameCount(System.Drawing.Imaging.FrameDimension.Page);
            img.SelectActiveFrame(System.Drawing.Imaging.FrameDimension.Page, Math.Clamp(page, 0, pages - 1));
            using var bmp = new System.Drawing.Bitmap(img.Width, img.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, img.Width, img.Height);
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var result = new RgbaImage(bmp.Width, bmp.Height);
                var row = new byte[bmp.Width * 4];
                for (int y = 0; y < bmp.Height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int i = (y * bmp.Width + x) * 4; // BGRA -> RGBA
                        result.Pixels[i] = row[x * 4 + 2];
                        result.Pixels[i + 1] = row[x * 4 + 1];
                        result.Pixels[i + 2] = row[x * 4];
                        result.Pixels[i + 3] = row[x * 4 + 3];
                    }
                }
                return result;
            }
            finally { bmp.UnlockBits(data); }
        });
    }

    private static T WithImageCore<T>(string path, Func<System.Drawing.Image, T> f)
    {
        using var img = System.Drawing.Image.FromFile(path);
        return f(img);
    }

    private static T WithImage<T>(string path, Func<System.Drawing.Image, T> f)
    {
        return WithImageCore(path, f);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("TIFF の読み込みは Windows でのみ対応しています。");
    }
}

/// <summary>Grabs one frame from a video with ffmpeg (must be installed and on PATH, or next to BMAP).</summary>
public static class VideoFrame
{
    public static string? FindFfmpeg()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static RgbaImage Extract(string path, double seconds, CancellationToken ct = default)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new ArgumentOutOfRangeException(nameof(seconds));
        ct.ThrowIfCancellationRequested();
        var ffmpeg = FindFfmpeg() ?? throw new FileNotFoundException(
            "動画からフレームを取り出すには ffmpeg が必要です。ffmpeg.exe を PATH に通すか BMAP と同じフォルダに置いてください。");
        string temp = Path.Combine(Path.GetTempPath(), $"bmap_frame_{Guid.NewGuid():N}.png");
        try
        {
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-ss", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "-i", path, "-frames:v", "1", "-y", temp })
                psi.ArgumentList.Add(a);
            var result = ProcessRunner.RunAsync(psi, TimeSpan.FromSeconds(60), ct).GetAwaiter().GetResult();
            if (result.ExitCode != 0 || !File.Exists(temp)) throw new InvalidDataException("動画のフレームを取り出せませんでした: " + result.Error.Trim());
            return ImageCodec.Load(temp);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
