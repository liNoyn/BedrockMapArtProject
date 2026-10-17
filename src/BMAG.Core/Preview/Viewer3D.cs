using BMAG.BlockEngine;
using BMAG.Imaging;
using BMAG.Palette;

namespace BMAG.Preview;

/// <summary>How faces are coloured in the 3D view.</summary>
public enum ViewColorMode
{
    /// <summary>Minecraft block textures (map colours when no textures are available).</summary>
    Texture,
    /// <summary>The colour each block gives on the map, including its shade (what the map will look like).</summary>
    MapColor,
    /// <summary>Blue (low) to red (high).</summary>
    Height,
}

/// <summary>Orbit camera: yaw/pitch in degrees around the centre of the scene, zoom = pixels per block.</summary>
public sealed record Camera3D
{
    public double Yaw { get; init; } = 35;
    /// <summary>5 (almost level) .. 90 (straight down).</summary>
    public double Pitch { get; init; } = 40;
    /// <summary>Pixels per block (orthographic) at the scene centre.</summary>
    public double Zoom { get; init; } = 4;
    /// <summary>Pan offset in blocks (screen right / up).</summary>
    public double PanX { get; init; }
    public double PanY { get; init; }
    public bool Perspective { get; init; }

    public Camera3D Clamp() => this with
    {
        Pitch = Math.Clamp(Pitch, 5, 90),
        Zoom = Math.Clamp(Zoom, 0.25, 64),
        Yaw = ((Yaw % 360) + 360) % 360,
    };
}

/// <summary>
/// Visible faces of a block layout. Faces between two blocks are dropped, so a flat 128x128 map art is about
/// 16k top faces instead of 100k cube faces, which keeps software rendering interactive.
/// </summary>
public sealed class SceneMesh
{
    internal readonly List<Face> Faces = [];
    public IReadOnlyList<BlockPlacement> Blocks { get; }
    /// <summary>Map shade of each block's top face (for <see cref="ViewColorMode.MapColor"/>).</summary>
    internal readonly Shade[] TopShade;
    public float CenterX { get; }
    public float CenterY { get; }
    public float CenterZ { get; }
    /// <summary>Largest horizontal extent in blocks (used for the default zoom).</summary>
    public float Extent { get; }
    public int MaxY { get; }
    public int FaceCount => Faces.Count;

    /// <summary>Face directions: 0 up, 1 north (-Z), 2 south (+Z), 3 west (-X), 4 east (+X).</summary>
    internal readonly record struct Face(int Block, byte Dir, float X, float Y, float Z);

    private SceneMesh(List<BlockPlacement> blocks, Shade[] topShade, float cx, float cy, float cz, float extent, int maxY)
    {
        Blocks = blocks;
        TopShade = topShade;
        CenterX = cx; CenterY = cy; CenterZ = cz;
        Extent = extent;
        MaxY = maxY;
    }

    public static SceneMesh Build(BlockLayout layout, LayoutRegion region, bool includeSupports)
    {
        var blocks = layout.Placements().Where(p =>
            p.X >= region.X0 && p.X < region.X0 + region.Width && p.Z >= region.Z0 && p.Z < region.Z0 + region.Depth &&
            (includeSupports || !p.IsSupport || p.Z == 0)).ToList();
        static long Key(int x, int y, int z) => ((long)(x + 4096) << 40) | ((long)(y + 4096) << 20) | (long)(z + 4096);
        var occupied = new HashSet<long>(blocks.Select(b => Key(b.X, b.Y, b.Z)));

        var shades = new Shade[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (b.IsSupport || b.Z == 0) { shades[i] = Shade.Normal; continue; }
            int d = layout.HeightAt(b.X, b.Z) - layout.HeightAt(b.X, b.Z - 1);
            shades[i] = d > 0 ? Shade.Bright : d < 0 ? Shade.Dark : Shade.Normal;
        }

        float cx = region.X0 + region.Width / 2f, cz = region.Z0 + region.Depth / 2f;
        int maxY = blocks.Count == 0 ? 0 : blocks.Max(b => b.Y);
        var mesh = new SceneMesh(blocks, shades, cx, (maxY + 1) / 2f, cz, Math.Max(region.Width, region.Depth), maxY);
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            if (!occupied.Contains(Key(b.X, b.Y + 1, b.Z))) mesh.Faces.Add(new Face(i, 0, b.X, b.Y, b.Z));
            if (!occupied.Contains(Key(b.X, b.Y, b.Z - 1))) mesh.Faces.Add(new Face(i, 1, b.X, b.Y, b.Z));
            if (!occupied.Contains(Key(b.X, b.Y, b.Z + 1))) mesh.Faces.Add(new Face(i, 2, b.X, b.Y, b.Z));
            if (!occupied.Contains(Key(b.X - 1, b.Y, b.Z))) mesh.Faces.Add(new Face(i, 3, b.X, b.Y, b.Z));
            if (!occupied.Contains(Key(b.X + 1, b.Y, b.Z))) mesh.Faces.Add(new Face(i, 4, b.X, b.Y, b.Z));
        }
        return mesh;
    }

    /// <summary>A camera that shows the whole scene in a view of the given size.</summary>
    public Camera3D FitCamera(int width, int height, double yaw = 35, double pitch = 40)
    {
        double span = Math.Max(1, Extent * 1.25 + MaxY * 0.5);
        return new Camera3D { Yaw = yaw, Pitch = pitch, Zoom = Math.Clamp(Math.Min(width, height) / span, 0.25, 64) };
    }
}

/// <summary>Result of a 3D render: the picture and, per pixel, the index of the block drawn there (-1 = none).</summary>
public sealed record RenderResult3D(RgbaImage Image, int[] BlockIds, int Width, int Height)
{
    public int BlockAt(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? -1 : BlockIds[y * Width + x];
}

/// <summary>
/// Software 3D renderer for build previews: depth-buffered, textured or map-coloured faces, directional shading and
/// block outlines, orthographic or perspective. Pure C#, so it runs anywhere the app runs.
/// </summary>
public static class Viewer3D
{
    // Corner offsets for each face direction, in order (u,v) = (0,0) (1,0) (1,1) (0,1).
    private static readonly float[][] Corners =
    [
        [0, 1, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1], // up
        [1, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0], // north
        [0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1], // south
        [0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0], // west
        [1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 1], // east
    ];
    private static readonly float[][] Normals = [[0, 1, 0], [0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0]];
    /// <summary>Simple sun from the south-west above: up brightest, east/north darkest.</summary>
    private static readonly float[] Light = [1.0f, 0.72f, 0.86f, 0.80f, 0.66f];

    public static RenderResult3D Render(SceneMesh mesh, ColorProfile profile, Camera3D camera, int width, int height,
        LayoutRenderer.IconSource? icons, ViewColorMode mode, bool outlines = true)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var cam = camera.Clamp();
        var img = new RgbaImage(width, height);
        var ids = new int[width * height];
        var depth = new float[width * height];
        Array.Fill(ids, -1);
        Array.Fill(depth, float.NegativeInfinity);
        PaintBackground(img);

        double yaw = cam.Yaw * Math.PI / 180, pitch = cam.Pitch * Math.PI / 180;
        float cy = (float)Math.Cos(yaw), sy = (float)Math.Sin(yaw), cp = (float)Math.Cos(pitch), sp = (float)Math.Sin(pitch);
        float zoom = (float)cam.Zoom, halfW = width / 2f, halfH = height / 2f;
        float panX = (float)cam.PanX, panY = (float)cam.PanY;
        // Direction towards the camera in world space (for back-face culling).
        float vx = sy * cp, vy = sp, vz = cy * cp;
        float camDist = mesh.Extent * 1.6f + 20;

        var textures = new Dictionary<string, Rgba[]?>();
        Rgba[]? Texture(BlockDefinition b)
        {
            if (mode != ViewColorMode.Texture || icons is null) return null;
            if (textures.TryGetValue(b.Key, out var t)) return t;
            var icon = icons(b);
            t = icon is null ? null : Enumerable.Range(0, icon.Width * icon.Height).Select(i => icon[i % icon.Width, i / icon.Width]).ToArray();
            if (t != null && icon!.Width != 16) t = Resample16(icon);
            textures[b.Key] = t;
            return t;
        }

        var sxs = new float[4];
        var sys = new float[4];
        var szs = new float[4];
        foreach (var f in mesh.Faces)
        {
            var n = Normals[f.Dir];
            if (n[0] * vx + n[1] * vy + n[2] * vz <= 0) continue; // facing away

            var corners = Corners[f.Dir];
            bool behind = false;
            for (int k = 0; k < 4; k++)
            {
                float px = f.X + corners[k * 3] - mesh.CenterX;
                float py = f.Y + corners[k * 3 + 1] - mesh.CenterY;
                float pz = f.Z + corners[k * 3 + 2] - mesh.CenterZ;
                float x1 = px * cy - pz * sy;
                float z1 = px * sy + pz * cy;
                float vyv = py * cp - z1 * sp;
                float d = py * sp + z1 * cp;
                float scale = zoom;
                if (cam.Perspective)
                {
                    float dist = camDist - d;
                    if (dist < 1) { behind = true; break; }
                    scale = zoom * camDist / dist;
                }
                sxs[k] = halfW + (x1 - panX) * scale;
                sys[k] = halfH - (vyv - panY) * scale;
                szs[k] = d;
            }
            if (behind) continue;

            var block = mesh.Blocks[f.Block];
            var tex = Texture(block.Block);
            Rgba flat = FaceColour(mesh, profile, f, block, mode);
            float light = Light[f.Dir];
            if (block.IsSupport && block.Z != 0) light *= 0.85f;

            FillTriangle(0, 1, 2);
            FillTriangle(0, 2, 3);

            void FillTriangle(int a, int b2, int c)
            {
                float x0 = sxs[a], y0 = sys[a], x1 = sxs[b2], y1 = sys[b2], x2 = sxs[c], y2 = sys[c];
                float area = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
                if (Math.Abs(area) < 1e-6f) return;
                int minX = Math.Max(0, (int)Math.Floor(Math.Min(x0, Math.Min(x1, x2))));
                int maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(x0, Math.Max(x1, x2))));
                int minY = Math.Max(0, (int)Math.Floor(Math.Min(y0, Math.Min(y1, y2))));
                int maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(y0, Math.Max(y1, y2))));
                if (minX > maxX || minY > maxY) return;
                // Corner UVs for (0,0) (1,0) (1,1) (0,1).
                float ua = UvU(a), ub = UvU(b2), uc = UvU(c), va = UvV(a), vb = UvV(b2), vc = UvV(c);
                float za = szs[a], zb = szs[b2], zc = szs[c];
                float inv = 1 / area;
                // Outline width in UV units (about one pixel at this zoom).
                float edgeLen = Math.Max(1, (float)Math.Sqrt(Math.Abs(area)));
                // Outlines only once blocks are big enough on screen; when zoomed out they would just darken the picture.
                float edge = outlines && edgeLen >= 6 ? Math.Min(0.12f, 0.9f / edgeLen) : -1;
                for (int y = minY; y <= maxY; y++)
                {
                    float py = y + 0.5f;
                    for (int x = minX; x <= maxX; x++)
                    {
                        float px = x + 0.5f;
                        float w0 = ((x1 - px) * (y2 - py) - (x2 - px) * (y1 - py)) * inv;
                        float w1 = ((x2 - px) * (y0 - py) - (x0 - px) * (y2 - py)) * inv;
                        float w2 = 1 - w0 - w1;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                        float z = w0 * za + w1 * zb + w2 * zc;
                        int i = y * width + x;
                        if (z <= depth[i]) continue;
                        depth[i] = z;
                        ids[i] = f.Block;
                        float u = w0 * ua + w1 * ub + w2 * uc, v = w0 * va + w1 * vb + w2 * vc;
                        Rgba col = tex is null ? flat : tex[Math.Clamp((int)(v * 16), 0, 15) * 16 + Math.Clamp((int)(u * 16), 0, 15)];
                        if (col.A < 128) col = flat; // transparent texels (glass, leaves): show the block colour
                        float l = light;
                        if (edge > 0 && (u < edge || v < edge || u > 1 - edge || v > 1 - edge)) l *= 0.72f;
                        int o = i * 4;
                        img.Pixels[o] = (byte)Math.Min(255, col.R * l);
                        img.Pixels[o + 1] = (byte)Math.Min(255, col.G * l);
                        img.Pixels[o + 2] = (byte)Math.Min(255, col.B * l);
                        img.Pixels[o + 3] = 255;
                    }
                }
            }
        }
        return new RenderResult3D(img, ids, width, height);

        static float UvU(int k) => k is 1 or 2 ? 1 : 0;
        static float UvV(int k) => k is 2 or 3 ? 1 : 0;
    }

    private static Rgba FaceColour(SceneMesh mesh, ColorProfile profile, SceneMesh.Face f, BlockPlacement b, ViewColorMode mode)
    {
        if (b.IsSupport && b.Z != 0) return new Rgba(128, 128, 128);
        return mode switch
        {
            ViewColorMode.Height => LayoutRenderer.Ramp(mesh.MaxY == 0 ? 0 : b.Y / (double)mesh.MaxY),
            ViewColorMode.MapColor => f.Dir == 0 ? profile.ColorOf(b.Block, mesh.TopShade[f.Block]) : profile.ColorOf(b.Block, Shade.Dark),
            _ => profile.ColorOf(b.Block, Shade.Bright),
        };
    }

    private static Rgba[] Resample16(RgbaImage icon)
    {
        var t = new Rgba[256];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                t[y * 16 + x] = icon[x * icon.Width / 16, y * icon.Height / 16];
        return t;
    }

    private static void PaintBackground(RgbaImage img)
    {
        for (int y = 0; y < img.Height; y++)
        {
            double t = y / (double)Math.Max(1, img.Height - 1);
            byte r = (byte)(34 + 18 * t), g = (byte)(40 + 18 * t), b = (byte)(52 + 14 * t);
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                img.Pixels[o] = r; img.Pixels[o + 1] = g; img.Pixels[o + 2] = b; img.Pixels[o + 3] = 255;
            }
        }
    }
}
