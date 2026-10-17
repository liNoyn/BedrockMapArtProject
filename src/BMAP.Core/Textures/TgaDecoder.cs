using BMAP.Imaging;

namespace BMAP.Textures;

/// <summary>Minimal TGA decoder (types 2/3/10/11, 8/24/32-bit) for Bedrock resource pack textures.</summary>
public static class TgaDecoder
{
    public static RgbaImage Decode(byte[] d)
    {
        if (d.Length < 18) throw new InvalidDataException("TGA が短すぎます。");
        int idLength = d[0], colorMapType = d[1], type = d[2];
        int width = d[12] | d[13] << 8, height = d[14] | d[15] << 8, bpp = d[16], desc = d[17];
        if (colorMapType != 0 || type is not (2 or 3 or 10 or 11) || width == 0 || height == 0)
            throw new InvalidDataException($"未対応の TGA 形式です (type {type})。");
        int bytesPer = bpp / 8;
        if (bytesPer is not (1 or 3 or 4)) throw new InvalidDataException($"未対応の TGA ビット深度 {bpp}。");
        int pos = 18 + idLength;
        int total = width * height;
        var raw = new byte[total * bytesPer];
        if (type is 2 or 3)
        {
            Buffer.BlockCopy(d, pos, raw, 0, Math.Min(raw.Length, d.Length - pos));
        }
        else
        {
            int px = 0;
            while (px < total && pos < d.Length)
            {
                int header = d[pos++];
                int count = (header & 0x7F) + 1;
                if ((header & 0x80) != 0)
                {
                    for (int i = 0; i < count && px < total; i++, px++)
                        Buffer.BlockCopy(d, pos, raw, px * bytesPer, bytesPer);
                    pos += bytesPer;
                }
                else
                {
                    int n = Math.Min(count, total - px) * bytesPer;
                    Buffer.BlockCopy(d, pos, raw, px * bytesPer, Math.Min(n, d.Length - pos));
                    pos += count * bytesPer;
                    px += count;
                }
            }
        }
        bool topDown = (desc & 0x20) != 0;
        var img = new RgbaImage(width, height);
        for (int y = 0; y < height; y++)
        {
            int sy = topDown ? y : height - 1 - y;
            for (int x = 0; x < width; x++)
            {
                int s = (sy * width + x) * bytesPer;
                img[x, y] = bytesPer switch
                {
                    1 => new Rgba(raw[s], raw[s], raw[s]),
                    3 => new Rgba(raw[s + 2], raw[s + 1], raw[s]),
                    _ => new Rgba(raw[s + 2], raw[s + 1], raw[s], raw[s + 3]),
                };
            }
        }
        return img;
    }
}
