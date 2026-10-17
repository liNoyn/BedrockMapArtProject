namespace BMAG.Imaging;

/// <summary>Applies <see cref="ImageSettings"/> to a source image and produces the full map-grid sized image.</summary>
public static class ImagePipeline
{
    public static RgbaImage Process(RgbaImage source, ImageSettings s, CancellationToken ct = default)
    {
        var src = source;
        if (s.Crop is { } c)
        {
            int x = Math.Clamp(c.X, 0, source.Width - 1), y = Math.Clamp(c.Y, 0, source.Height - 1);
            int w = Math.Clamp(c.Width, 1, source.Width - x), h = Math.Clamp(c.Height, 1, source.Height - y);
            src = source.Crop(x, y, w, h);
        }

        int outW = s.OutputWidth, outH = s.OutputHeight;
        double zoom = s.Zoom <= 0 ? 1 : s.Zoom;
        double scaleX, scaleY;
        switch (s.Fit)
        {
            case FitMode.Stretch:
                scaleX = (double)outW / src.Width * zoom;
                scaleY = (double)outH / src.Height * zoom;
                break;
            case FitMode.Fill:
                scaleX = scaleY = Math.Max((double)outW / src.Width, (double)outH / src.Height) * zoom;
                break;
            default:
                scaleX = scaleY = Math.Min((double)outW / src.Width, (double)outH / src.Height) * zoom;
                break;
        }

        // Top-left of the scaled image in output pixels.
        double dstX0 = (outW - src.Width * scaleX) / 2 + s.OffsetX * outW;
        double dstY0 = (outH - src.Height * scaleY) / 2 + s.OffsetY * outH;
        var img = Resampler.Resample(src, -dstX0 / scaleX, -dstY0 / scaleY, outW / scaleX, outH / scaleY,
            outW, outH, s.Interpolation, ct);

        ct.ThrowIfCancellationRequested();
        ApplyAdjustments(img, s);
        if (s.Sharpen > 0) img = Sharpen(img, s.Sharpen);
        if (!s.KeepTransparency) CompositeOnto(img, Rgba.FromHex(s.BackgroundColor));
        else FillLetterboxOnly(img);
        return img;
    }

    public static void ApplyAdjustments(RgbaImage img, ImageSettings s)
    {
        if (s.Brightness == 0 && s.Contrast == 0 && s.Saturation == 0 && Math.Abs(s.Gamma - 1) < 1e-9) return;
        // Lookup table for brightness/contrast/gamma, per-pixel work only for saturation.
        var lut = new byte[256];
        double contrast = s.Contrast >= 0 ? 1 + s.Contrast * 3 : 1 + s.Contrast; // 0..4
        double gamma = s.Gamma <= 0 ? 1 : s.Gamma;
        for (int i = 0; i < 256; i++)
        {
            double v = i / 255.0;
            v += s.Brightness * 0.5;
            v = (v - 0.5) * contrast + 0.5;
            v = Math.Clamp(v, 0, 1);
            v = Math.Pow(v, 1 / gamma);
            lut[i] = Resampler.ClampByte(v * 255);
        }
        double sat = 1 + s.Saturation;
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            double r = lut[p[i]], g = lut[p[i + 1]], b = lut[p[i + 2]];
            if (s.Saturation != 0)
            {
                double l = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = l + (r - l) * sat;
                g = l + (g - l) * sat;
                b = l + (b - l) * sat;
            }
            p[i] = Resampler.ClampByte(r);
            p[i + 1] = Resampler.ClampByte(g);
            p[i + 2] = Resampler.ClampByte(b);
        }
    }

    /// <summary>Unsharp mask with a 3x3 blur.</summary>
    public static RgbaImage Sharpen(RgbaImage img, double amount)
    {
        var dst = img.Clone();
        int w = img.Width, h = img.Height;
        int[] k = [1, 2, 1, 2, 4, 2, 1, 2, 1];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            for (int ch = 0; ch < 3; ch++)
            {
                int sum = 0, ki = 0;
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = Math.Clamp(x + dx, 0, w - 1), sy = Math.Clamp(y + dy, 0, h - 1);
                    sum += img.Pixels[(sy * w + sx) * 4 + ch] * k[ki++];
                }
                double blur = sum / 16.0;
                double v = img.Pixels[i + ch] + (img.Pixels[i + ch] - blur) * amount;
                dst.Pixels[i + ch] = Resampler.ClampByte(v);
            }
        }
        return dst;
    }

    public static void CompositeOnto(RgbaImage img, Rgba bg)
    {
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            if (a == 255) continue;
            p[i] = (byte)((p[i] * a + bg.R * (255 - a) + 127) / 255);
            p[i + 1] = (byte)((p[i + 1] * a + bg.G * (255 - a) + 127) / 255);
            p[i + 2] = (byte)((p[i + 2] * a + bg.B * (255 - a) + 127) / 255);
            p[i + 3] = 255;
        }
    }

    /// <summary>Normalises fully transparent pixels to transparent black so hashes stay stable.</summary>
    private static void FillLetterboxOnly(RgbaImage img)
    {
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 4)
            if (p[i + 3] == 0) p[i] = p[i + 1] = p[i + 2] = 0;
    }

    /// <summary>Cheap preview of a large source image (design 15.1).</summary>
    public static RgbaImage Thumbnail(RgbaImage src, int maxSide)
    {
        if (src.Width <= maxSide && src.Height <= maxSide) return src;
        double scale = Math.Min((double)maxSide / src.Width, (double)maxSide / src.Height);
        int w = Math.Max(1, (int)(src.Width * scale)), h = Math.Max(1, (int)(src.Height * scale));
        return Resampler.Resample(src, 0, 0, src.Width, src.Height, w, h, Interpolation.Bilinear);
    }
}
