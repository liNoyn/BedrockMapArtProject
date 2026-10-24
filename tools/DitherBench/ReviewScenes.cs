using System.Diagnostics;
using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.ColorEngine;

internal static class ReviewScenes
{
    public static RgbaImage RareOutline(int size, ColorProfile profile, PaletteSelection selection)
    {
        var colours=ColorQuantizer.BuildCandidates(profile,selection.Resolve(profile),StaircaseMode.Flat).Select(c=>c.Color).Distinct().ToArray();
        var light=colours.Where(c=>ColorMath.ToOklab(c.R,c.G,c.B).L>.75).ToArray();
        var pair=light.SelectMany((a,i)=>light.Skip(i+1).Select(b=>(A:a,B:b,Distance:ColorMath.DeltaE76(ColorMath.ToOklab(a.R,a.G,a.B),ColorMath.ToOklab(b.R,b.G,b.B))))).OrderBy(p=>p.Distance).First();
        var dark=colours.OrderBy(c=>ColorMath.ToOklab(c.R,c.G,c.B).L).First();
        var image=RgbaImage.Filled(size,size,pair.A);
        for(int y=0;y<size;y++)for(int x=0;x<size;x++)
            image[x,y]=x==size-2 ? dark : x<size/2 ? pair.A : pair.B;
        return image;
    }

    // Same geometry as the outline regression test, scaled from a 128-pixel reference.
    public static RgbaImage Outline(int size, bool antialias)
    {
        int scale = antialias ? 4 : 1, width = size * scale;
        var image = RgbaImage.Filled(width, width, Rgba.FromHex("#F3EFE4"));
        for (int y = 0; y < width; y++)
            for (int x = 0; x < width; x++)
            {
                double xx = (x + 0.5) * 128 / width - 0.5, yy = (y + 0.5) * 128 / width - 0.5;
                double face = Math.Sqrt((xx - 40) * (xx - 40) / 900 + (yy - 70) * (yy - 70) / 1600);
                double disc = Math.Sqrt((xx - 95) * (xx - 95) + (yy - 35) * (yy - 35));
                if (face <= 0.93) image[x, y] = Rgba.FromHex("#F2E6DC");
                else if (face <= 1) image[x, y] = new Rgba(110, 84, 66);
                if (disc <= 22) image[x, y] = Rgba.FromHex("#7E7CCB");
                else if (disc <= 25) image[x, y] = new Rgba(110, 84, 66);
            }
        return antialias ? Resampler.Resample(image, 0, 0, width, width, size, size, Interpolation.Lanczos3, linearLight: true) : image;
    }

    public static void CheckDetection(string output)
    {
        var rows = new List<object>();
        foreach (int size in new[] { 96, 128, 256, 512 })
            foreach (string scene in new[] { "outline", "outline-aa", "sky" })
            {
                var image = scene == "sky" ? Sky(size) : Outline(size, scene == "outline-aa");
                ColorQuantizer.FlatAreas(image, 128);
                bool[] mask = [];
                var times = new List<double>();
                for (int run = 0; run < 5; run++)
                {
                    var timer = Stopwatch.StartNew();
                    mask = ColorQuantizer.FlatAreas(image, 128);
                    times.Add(timer.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                int face = 0, faceFlat = 0, disc = 0, discFlat = 0;
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        double xx = (x + 0.5) * 128 / size - 0.5, yy = (y + 0.5) * 128 / size - 0.5;
                        if (Math.Sqrt((xx - 40) * (xx - 40) / 900 + (yy - 70) * (yy - 70) / 1600) < 0.8)
                        { face++; if (mask[y * size + x]) faceFlat++; }
                        if ((xx - 95) * (xx - 95) + (yy - 35) * (yy - 35) < 18 * 18)
                        { disc++; if (mask[y * size + x]) discFlat++; }
                    }
                rows.Add(new { Scene = scene, Size = size, FlatFraction = mask.Count(v => v) / (double)mask.Length,
                    FaceFraction = faceFlat / (double)face, DiscFraction = discFlat / (double)disc, MedianMs = times[2] });
                var preview = new RgbaImage(size, size);
                for (int i = 0; i < mask.Length; i++) preview[i % size, i / size] = mask[i] ? new Rgba(255, 255, 255) : new Rgba(0, 0, 0);
                ImageCodec.SavePng(image, Path.Combine(output, $"{scene}-{size}-source.png"));
                ImageCodec.SavePng(preview, Path.Combine(output, $"{scene}-{size}-mask.png"));
            }
        File.WriteAllText(Path.Combine(output, "detection.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"detection: {rows.Count} cases");
    }

    private static RgbaImage Sky(int size)
    {
        var image = new RgbaImage(size, size);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double v = y / (size - 1.0);
                image[x, y] = new Rgba((byte)Math.Round(130 + 30 * v), (byte)Math.Round(175 + 35 * v), (byte)Math.Round(225 + 20 * v));
            }
        return image;
    }
}
