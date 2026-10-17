using System.Security.Cryptography;

namespace BMAG.Imaging;

/// <summary>The internal image buffer: 8-bit straight (non-premultiplied) RGBA, row-major, top row first.</summary>
public sealed class RgbaImage
{
    public RgbaImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image must not be empty.");
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("Pixel buffer size does not match.");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public Rgba this[int x, int y]
    {
        get
        {
            int i = (y * Width + x) * 4;
            return new Rgba(Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
        }
        set
        {
            int i = (y * Width + x) * 4;
            Pixels[i] = value.R;
            Pixels[i + 1] = value.G;
            Pixels[i + 2] = value.B;
            Pixels[i + 3] = value.A;
        }
    }

    public RgbaImage Clone() => new(Width, Height, (byte[])Pixels.Clone());

    public RgbaImage Crop(int x, int y, int w, int h)
    {
        var result = new RgbaImage(w, h);
        for (int row = 0; row < h; row++)
            Buffer.BlockCopy(Pixels, ((y + row) * Width + x) * 4, result.Pixels, row * w * 4, w * 4);
        return result;
    }

    public void Paste(RgbaImage src, int x, int y)
    {
        for (int row = 0; row < src.Height; row++)
        {
            int ty = y + row;
            if (ty < 0 || ty >= Height) continue;
            for (int col = 0; col < src.Width; col++)
            {
                int tx = x + col;
                if (tx < 0 || tx >= Width) continue;
                this[tx, ty] = src[col, row];
            }
        }
    }

    public static RgbaImage Filled(int width, int height, Rgba color)
    {
        var img = new RgbaImage(width, height);
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            img.Pixels[i] = color.R;
            img.Pixels[i + 1] = color.G;
            img.Pixels[i + 2] = color.B;
            img.Pixels[i + 3] = color.A;
        }
        return img;
    }

    /// <summary>Nearest-neighbour upscale by an integer factor, used for guide images.</summary>
    public RgbaImage ScaleNearest(int factor)
    {
        var result = new RgbaImage(Width * factor, Height * factor);
        for (int y = 0; y < result.Height; y++)
            for (int x = 0; x < result.Width; x++)
                result[x, y] = this[x / factor, y / factor];
        return result;
    }

    public string ComputeHash() => Convert.ToHexString(SHA256.HashData(Pixels))[..16];
}

public readonly record struct Rgba(byte R, byte G, byte B, byte A = 255)
{
    public static Rgba FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        byte P(int i) => Convert.ToByte(hex.Substring(i, 2), 16);
        return hex.Length switch
        {
            6 => new Rgba(P(0), P(2), P(4)),
            8 => new Rgba(P(0), P(2), P(4), P(6)),
            _ => throw new FormatException($"Invalid colour '{hex}'."),
        };
    }

    public string ToHex() => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}
