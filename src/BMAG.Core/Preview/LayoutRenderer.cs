using BMAG.BlockEngine;
using BMAG.Imaging;
using BMAG.Palette;
using SkiaSharp;

namespace BMAG.Preview;

/// <summary>Region of the layout to show: one map (with its support row when it is in the top row) or everything.</summary>
public readonly record struct LayoutRegion(int X0, int Z0, int Width, int Depth)
{
    public static LayoutRegion All(BlockLayout l) => new(0, 0, l.Width, l.Depth);

    /// <summary>Map (mapX, mapY): image rows 128*mapY .. +127 are layout rows +1; the top maps include the support row.</summary>
    public static LayoutRegion Map(int mapX, int mapY) =>
        mapY == 0 ? new(mapX * 128, 0, 128, 129) : new(mapX * 128, mapY * 128 + 1, 128, 128);
}

/// <summary>
/// Renders build previews (PreviewCore, design 7.1 / 11.4): a top-down block view with textures ("view online 2D")
/// and an isometric 3D view. Pure Skia, so it works in the GUI and the CLI.
/// </summary>
public static class LayoutRenderer
{
    public delegate RgbaImage? IconSource(BlockDefinition block);

    /// <summary>Top-down view. Each block is <paramref name="cell"/> pixels; textures are used when cell ≥ 8 and icons exist.</summary>
    public static RgbaImage RenderTopDown(BlockLayout layout, ColorProfile profile, LayoutRegion region, int cell,
        IconSource? icons, bool showHeights, Func<int, int, bool>? isDone = null)
    {
        int w = region.Width * cell, h = region.Depth * cell;
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(40, 40, 40));
        var cache = new Dictionary<string, SKBitmap?>();
        using var fill = new SKPaint { Style = SKPaintStyle.Fill };
        using var sampling = new SKPaint { FilterQuality = SKFilterQuality.None };
        int maxH = Math.Max(1, layout.MaxHeight);

        for (int dz = 0; dz < region.Depth; dz++)
            for (int dx = 0; dx < region.Width; dx++)
            {
                int x = region.X0 + dx, z = region.Z0 + dz;
                if (x >= layout.Width || z >= layout.Depth) continue;
                var b = layout.BlockAt(x, z);
                if (b is null) continue;
                var rect = new SKRect(dx * cell, dz * cell, (dx + 1) * cell, (dz + 1) * cell);
                SKBitmap? tex = null;
                if (cell >= 8 && icons != null && !cache.TryGetValue(b.Key, out tex))
                    cache[b.Key] = tex = icons(b) is { } icon ? ToBitmap(icon) : null;
                if (tex != null) canvas.DrawBitmap(tex, rect, sampling);
                else
                {
                    var c = z == 0 ? new Rgba(150, 150, 150) : profile.ColorOf(b, layout.Mode == StaircaseMode.Flat ? Shade.Normal : ShadeAt(layout, x, z));
                    fill.Color = new SKColor(c.R, c.G, c.B);
                    canvas.DrawRect(rect, fill);
                }
                if (showHeights && !layout.IsFlat)
                {
                    // Darken by height so staircases are visible.
                    byte a = (byte)(120 - 120 * layout.HeightAt(x, z) / maxH);
                    fill.Color = new SKColor(0, 0, 0, a);
                    canvas.DrawRect(rect, fill);
                }
            }

        // Finished parts get a green wash (build progress, design 7.3).
        if (isDone != null)
        {
            fill.Color = new SKColor(40, 220, 80, 100);
            for (int dz = 0; dz < region.Depth; dz++)
                for (int dx = 0; dx < region.Width; dx++)
                    if (isDone(region.X0 + dx, region.Z0 + dz))
                        canvas.DrawRect(dx * cell, dz * cell, cell, cell, fill);
        }

        // Grid: blocks (when large), chunks (16), structure pieces (64), maps (128).
        using var thin = new SKPaint { Color = new SKColor(0, 0, 0, 50), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        using var chunk = new SKPaint { Color = new SKColor(255, 255, 255, 110), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        using var piece = new SKPaint { Color = new SKColor(255, 170, 0), StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var map = new SKPaint { Color = new SKColor(230, 40, 40), StrokeWidth = 3, Style = SKPaintStyle.Stroke };
        for (int dx = 0; dx <= region.Width; dx++)
        {
            int x = region.X0 + dx;
            var p = x % 128 == 0 ? map : x % 64 == 0 ? piece : x % 16 == 0 ? chunk : cell >= 8 ? thin : null;
            if (p != null) canvas.DrawLine(dx * cell, 0, dx * cell, h, p);
        }
        for (int dz = 0; dz <= region.Depth; dz++)
        {
            int r = region.Z0 + dz - 1; // image row
            if (r < 0) continue;
            var p = r % 128 == 0 ? map : r % 64 == 0 ? piece : r % 16 == 0 ? chunk : cell >= 8 ? thin : null;
            if (p != null) canvas.DrawLine(0, dz * cell, w, dz * cell, p);
        }
        foreach (var bmp in cache.Values) bmp?.Dispose();
        return Snapshot(surface, w, h);
    }

    private static Shade ShadeAt(BlockLayout layout, int x, int z)
    {
        if (z == 0) return Shade.Normal;
        int d = layout.HeightAt(x, z) - layout.HeightAt(x, z - 1);
        return d > 0 ? Shade.Bright : d < 0 ? Shade.Dark : Shade.Normal;
    }

    /// <summary>
    /// Isometric 3D view of the region including support blocks. <paramref name="rotation"/> 0-3 turns the view by
    /// 90° steps. Faces are textured with the block icon when available.
    /// </summary>
    public static RgbaImage RenderIsometric(BlockLayout layout, ColorProfile profile, LayoutRegion region, int tile,
        int rotation, IconSource? icons, bool showSupports = true)
    {
        int halfW = tile, halfH = tile / 2, cubeH = tile;
        int rw = region.Width, rd = region.Depth;
        var blocks = layout.Placements().Where(p =>
            p.X >= region.X0 && p.X < region.X0 + rw && p.Z >= region.Z0 && p.Z < region.Z0 + rd &&
            (showSupports || !p.IsSupport || p.Z == 0)).ToList();
        int maxY = blocks.Count == 0 ? 0 : blocks.Max(b => b.Y);

        // Rotate (x, z) inside the region.
        (int, int) Rot(int x, int z) => rotation switch
        {
            1 => (rd - 1 - z, x),
            2 => (rw - 1 - x, rd - 1 - z),
            3 => (z, rw - 1 - x),
            _ => (x, z),
        };
        int gw = rotation % 2 == 0 ? rw : rd, gd = rotation % 2 == 0 ? rd : rw;
        int width = (gw + gd) * halfW + 4;
        int height = (gw + gd) * halfH + (maxY + 1) * cubeH + cubeH + 4;
        float ox = gd * halfW + 2, oy = (maxY + 1) * cubeH + 2;

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(30, 34, 40));
        var texCache = new Dictionary<string, SKBitmap?>();
        using var paint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill, FilterQuality = SKFilterQuality.None };
        using var edge = new SKPaint { Color = new SKColor(0, 0, 0, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

        // Painter's algorithm: far to near (small gx+gz first), bottom to top.
        var ordered = blocks.Select(b =>
        {
            var (gx, gz) = Rot(b.X - region.X0, b.Z - region.Z0);
            return (b, gx, gz);
        }).OrderBy(t => t.gx + t.gz).ThenBy(t => t.b.Y).ThenBy(t => t.gx);

        foreach (var (b, gx, gz) in ordered)
        {
            float cx = ox + (gx - gz) * halfW;
            float cy = oy + (gx + gz) * halfH - b.Y * cubeH;
            var top = new SKPoint[] { new(cx, cy), new(cx + halfW, cy + halfH), new(cx, cy + 2 * halfH), new(cx - halfW, cy + halfH) };
            var left = new SKPoint[] { top[3], top[2], new(top[2].X, top[2].Y + cubeH), new(top[3].X, top[3].Y + cubeH) };
            var right = new SKPoint[] { top[2], top[1], new(top[1].X, top[1].Y + cubeH), new(top[2].X, top[2].Y + cubeH) };

            Rgba baseColor = b.IsSupport && b.Z != 0 ? new Rgba(120, 120, 120) : profile.ColorOf(b.Block, Shade.Bright);
            SKBitmap? tex = null;
            if (icons != null && tile >= 6 && !texCache.TryGetValue(b.Block.Key, out tex))
                texCache[b.Block.Key] = tex = icons(b.Block) is { } icon ? ToBitmap(icon) : null;

            DrawFace(canvas, paint, top, tex, baseColor, 1.0f);
            DrawFace(canvas, paint, left, tex, baseColor, 0.78f);
            DrawFace(canvas, paint, right, tex, baseColor, 0.62f);
            if (tile >= 6)
            {
                using var path = Poly(top);
                canvas.DrawPath(path, edge);
            }
        }
        foreach (var bmp in texCache.Values) bmp?.Dispose();
        return Snapshot(surface, width, height);
    }

    private static void DrawFace(SKCanvas canvas, SKPaint paint, SKPoint[] quad, SKBitmap? tex, Rgba color, float light)
    {
        using var path = Poly(quad);
        if (tex != null)
        {
            // Map the texture square onto the parallelogram (quad[0] = texture origin, quad[1] = +u, quad[3] = +v).
            float sx = (quad[1].X - quad[0].X) / tex.Width, ky = (quad[1].Y - quad[0].Y) / tex.Width;
            float kx = (quad[3].X - quad[0].X) / tex.Height, sy = (quad[3].Y - quad[0].Y) / tex.Height;
            var matrix = new SKMatrix(sx, kx, quad[0].X, ky, sy, quad[0].Y, 0, 0, 1);
            using var shader = SKShader.CreateBitmap(tex, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, matrix);
            paint.Shader = shader;
            paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0, 0, 0, (byte)(255 * (1 - light))), SKBlendMode.SrcOver);
            canvas.DrawPath(path, paint);
            paint.Shader = null;
            paint.ColorFilter?.Dispose();
            paint.ColorFilter = null;
        }
        else
        {
            paint.Color = new SKColor((byte)(color.R * light), (byte)(color.G * light), (byte)(color.B * light));
            canvas.DrawPath(path, paint);
        }
    }

    private static SKPath Poly(SKPoint[] pts)
    {
        var path = new SKPath();
        path.AddPoly(pts, true);
        return path;
    }

    private static SKBitmap ToBitmap(RgbaImage img)
    {
        var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(img.Pixels, 0, bmp.GetPixels(), img.Pixels.Length);
        return bmp;
    }

    private static RgbaImage Snapshot(SKSurface surface, int w, int h)
    {
        var img = new RgbaImage(w, h);
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(img.Pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { surface.ReadPixels(info, handle.AddrOfPinnedObject(), w * 4, 0, 0); }
        finally { handle.Free(); }
        return img;
    }

    /// <summary>Block under a point of a top-down render (for the hover info, MapartCraft's "WAILA").</summary>
    public static (int X, int Z)? HitTestTopDown(LayoutRegion region, int cell, double px, double py)
    {
        if (px < 0 || py < 0) return null;
        int dx = (int)(px / cell), dz = (int)(py / cell);
        if (dx >= region.Width || dz >= region.Depth) return null;
        return (region.X0 + dx, region.Z0 + dz);
    }
}
