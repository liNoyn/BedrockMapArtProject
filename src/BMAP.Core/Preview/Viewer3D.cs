using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.Preview;

/// <summary>How faces are coloured in the 3D view.</summary>
public enum ViewColorMode
{
    /// <summary>Minecraft block textures (map colours when no textures are available).</summary>
    Texture,
    /// <summary>The colour each block gives on the map, including its shade (what the map will look like).</summary>
    MapColor,
    /// <summary>The shade each top block produces: dark blue, normal grey, bright yellow; problems in red/orange.</summary>
    Shade,
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

/// <summary>Render switches of the 3D view.</summary>
public sealed record View3DOptions
{
    public ViewColorMode Mode { get; init; } = ViewColorMode.Texture;
    public bool Outlines { get; init; } = true;
    /// <summary>Ambient occlusion and sun shadows (computed when the mesh is built).</summary>
    public bool Lighting { get; init; } = true;
    /// <summary>Chunk (16) and map (128) lines on the top faces.</summary>
    public bool Grid { get; init; }
    /// <summary>Block index to highlight (hovered block), or -1.</summary>
    public int Highlight { get; init; } = -1;
}

/// <summary>
/// Visible faces of a block layout with precomputed lighting. Faces between two blocks are dropped, so a flat
/// 128x128 map art is about 16k top faces instead of 100k cube faces, which keeps software rendering interactive.
/// Every top face also knows the map shade it produces, taken from the same rule the map simulator uses.
/// </summary>
public sealed class SceneMesh
{
    internal readonly List<Face> Faces = [];
    public IReadOnlyList<BlockPlacement> Blocks { get; }
    /// <summary>Map shade of each block when it is the top-most block of its column.</summary>
    internal readonly Shade[] TopShade;
    /// <summary>Per block: 0 = fine, 1 = map colour differs from the plan, 2 = depends on terrain, 3 = falls / breaks.</summary>
    internal readonly byte[] Problem;
    internal readonly bool[] IsTop;
    public float CenterX { get; }
    public float CenterY { get; }
    public float CenterZ { get; }
    /// <summary>Largest horizontal extent in blocks (used for the default zoom).</summary>
    public float Extent { get; }
    public int MaxY { get; }
    public int FaceCount => Faces.Count;

    /// <summary>
    /// Face directions: 0 up, 1 north (-Z), 2 south (+Z), 3 west (-X), 4 east (+X).
    /// Ao: ambient occlusion level 0..3 per corner (2 bits each, corner order as in the corner table).
    /// Sun: 1 when the face centre sees the sun, 0 in shadow.
    /// </summary>
    internal readonly record struct Face(int Block, byte Dir, float X, float Y, float Z, byte Ao, byte Sun);

    /// <summary>Direction towards the sun (normalised): high in the south-east, so the north/west sides are shaded.</summary>
    internal static readonly float[] SunDir = Normalize(0.42f, 1f, 0.55f);

    private SceneMesh(List<BlockPlacement> blocks, Shade[] topShade, byte[] problem, bool[] isTop, float cx, float cy, float cz, float extent, int maxY)
    {
        Blocks = blocks;
        TopShade = topShade;
        Problem = problem;
        IsTop = isTop;
        CenterX = cx; CenterY = cy; CenterZ = cz;
        Extent = extent;
        MaxY = maxY;
    }

    /// <param name="maxLayer">Show only blocks at or below this layout Y (null = all): build layer by layer.</param>
    /// <param name="report">Shading analysis of the layout, used to mark problem blocks.</param>
    public static SceneMesh Build(BlockLayout layout, LayoutRegion region, bool includeSupports, int? maxLayer = null,
        ShadingReport? report = null)
    {
        var all = layout.Placements();
        // Shades come from the top-most block of each column over ALL blocks (supports included), like the game.
        int w = layout.Width, depth = layout.Depth;
        var topY = new int[w * depth];
        Array.Fill(topY, int.MinValue);
        foreach (var p in all) topY[p.Z * w + p.X] = Math.Max(topY[p.Z * w + p.X], p.Y);

        var blocks = all.Where(p =>
            p.X >= region.X0 && p.X < region.X0 + region.Width && p.Z >= region.Z0 && p.Z < region.Z0 + region.Depth &&
            (includeSupports || !p.IsSupport || p.Z == 0) && (maxLayer is null || p.Y <= maxLayer)).ToList();

        var problemAt = new Dictionary<(int, int), byte>();
        if (report != null)
            foreach (var issue in report.Issues)
            {
                byte code = issue.Kind switch
                {
                    ShadingIssueKind.Mismatch => 1,
                    ShadingIssueKind.DependsOnTerrain => 2,
                    _ => 3,
                };
                problemAt[(issue.X, issue.Z)] = Math.Max(problemAt.GetValueOrDefault((issue.X, issue.Z)), code);
            }

        var shades = new Shade[blocks.Count];
        var problem = new byte[blocks.Count];
        var isTop = new bool[blocks.Count];
        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            int ti = b.Z * w + b.X;
            isTop[i] = topY[ti] == b.Y;
            if (isTop[i] && b.Z > 0 && topY[ti - w] != int.MinValue) shades[i] = ShadeRule.Compare(b.Y, topY[ti - w], layout.Step);
            else shades[i] = Shade.Normal;
            if (isTop[i] || problemAt.GetValueOrDefault((b.X, b.Z)) == 3) problem[i] = problemAt.GetValueOrDefault((b.X, b.Z));
        }

        float cx = region.X0 + region.Width / 2f, cz = region.Z0 + region.Depth / 2f;
        int maxY = blocks.Count == 0 ? 0 : blocks.Max(b => b.Y);
        var mesh = new SceneMesh(blocks, shades, problem, isTop, cx, (maxY + 1) / 2f, cz, Math.Max(region.Width, region.Depth), maxY);

        // Compact per-column occupancy for face culling and ambient occlusion (a dense 3D grid would need hundreds of
        // megabytes for 8x8 maps).
        var columns = new ColumnIndex(w, depth, blocks);
        bool Occ(int x, int y, int z) => columns.Occupied(x, y, z);

        // Shadows: the build is a height field, so for every column compute once the height below which the sun is
        // hidden ("shadow height", marching towards the sun over the column tops). A face is lit when its centre is
        // at or above the shadow height of the column it lies in. O(columns × shadow length) instead of a ray per face.
        float sx = SunDir[0], sy = SunDir[1], sz = SunDir[2];
        var shadow = ShadowHeights(columns, region, sx, sy, sz, maxY);
        bool Lit(float px, float py, float pz)
        {
            int cx2 = (int)MathF.Floor(px) - region.X0, cz2 = (int)MathF.Floor(pz) - region.Z0;
            if (cx2 < 0 || cz2 < 0 || cx2 >= region.Width || cz2 >= region.Depth) return true;
            return py >= shadow[cz2 * region.Width + cx2] - 0.01f;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            for (byte dir = 0; dir < 5; dir++)
            {
                var n = Viewer3D.NormalsI[dir];
                if (Occ(b.X + n[0], b.Y + n[1], b.Z + n[2])) continue;
                byte ao = 0;
                var corners = Viewer3D.Corners[dir];
                for (int k = 0; k < 4; k++)
                {
                    // Neighbours in front of the face around this corner: two edges and the diagonal.
                    int bx = b.X + n[0], by = b.Y + n[1], bz = b.Z + n[2];
                    int dx = n[0] != 0 ? 0 : corners[k * 3] > 0.5f ? 1 : -1;
                    int dy = n[1] != 0 ? 0 : corners[k * 3 + 1] > 0.5f ? 1 : -1;
                    int dz = n[2] != 0 ? 0 : corners[k * 3 + 2] > 0.5f ? 1 : -1;
                    // The two tangent axes of this face (the axis along the normal has d = 0).
                    (int ax, int ay, int az) t1, t2;
                    if (n[0] != 0) { t1 = (0, dy, 0); t2 = (0, 0, dz); }
                    else if (n[1] != 0) { t1 = (dx, 0, 0); t2 = (0, 0, dz); }
                    else { t1 = (dx, 0, 0); t2 = (0, dy, 0); }
                    bool s1 = Occ(bx + t1.ax, by + t1.ay, bz + t1.az);
                    bool s2 = Occ(bx + t2.ax, by + t2.ay, bz + t2.az);
                    bool c = Occ(bx + t1.ax + t2.ax, by + t1.ay + t2.ay, bz + t1.az + t2.az);
                    int level = s1 && s2 ? 0 : 3 - ((s1 ? 1 : 0) + (s2 ? 1 : 0) + (c ? 1 : 0));
                    ao |= (byte)(level << (k * 2));
                }
                float fx = b.X + 0.5f + n[0] * 0.51f, fy = b.Y + 0.5f + n[1] * 0.51f, fz = b.Z + 0.5f + n[2] * 0.51f;
                float facing = n[0] * sx + n[1] * sy + n[2] * sz;
                byte sun = (byte)(facing > 0 && Lit(fx, fy, fz) ? 1 : 0);
                mesh.Faces.Add(new Face(i, dir, b.X, b.Y, b.Z, ao, sun));
            }
        }
        return mesh;
    }

    /// <summary>
    /// Per column of the region: the lowest height that still sees the sun, i.e. max over t of
    /// (top surface at distance t towards the sun − t × rise). Rays march in half-block steps and stop as soon as they
    /// are above the highest block.
    /// </summary>
    private static float[] ShadowHeights(ColumnIndex columns, LayoutRegion region, float sx, float sy, float sz, int maxY)
    {
        float hl = MathF.Sqrt(sx * sx + sz * sz);
        float ux = sx / hl, uz = sz / hl, rise = sy / hl; // per horizontal block towards the sun
        int rw = region.Width, rd = region.Depth;
        var result = new float[rw * rd];
        float ceiling = maxY + 1;
        Parallel.For(0, rd, z =>
        {
            for (int x = 0; x < rw; x++)
            {
                float px = region.X0 + x + 0.5f, pz = region.Z0 + z + 0.5f;
                float best = float.NegativeInfinity;
                for (float t = 0.5f; ; t += 0.5f)
                {
                    float drop = t * rise;
                    if (ceiling - drop <= best) break; // nothing further away can cast a higher shadow
                    int qx = (int)MathF.Floor(px + t * ux), qz = (int)MathF.Floor(pz + t * uz);
                    if (qx < region.X0 || qz < region.Z0 || qx >= region.X0 + rw || qz >= region.Z0 + rd) break;
                    int top = columns.Top(qx, qz);
                    if (top == int.MinValue || (qx == region.X0 + x && qz == region.Z0 + z)) continue;
                    best = Math.Max(best, top + 1 - drop);
                }
                result[z * rw + x] = best;
            }
        });
        return result;
    }

    /// <summary>A camera that shows the whole scene in a view of the given size.</summary>
    public Camera3D FitCamera(int width, int height, double yaw = 35, double pitch = 40)
    {
        double span = Math.Max(1, Extent * 1.25 + MaxY * 0.5);
        return new Camera3D { Yaw = yaw, Pitch = pitch, Zoom = Math.Clamp(Math.Min(width, height) / span, 0.25, 64) };
    }

    private static float[] Normalize(float x, float y, float z)
    {
        float l = MathF.Sqrt(x * x + y * y + z * z);
        return [x / l, y / l, z / l];
    }
}

/// <summary>Result of a 3D render: the picture and, per pixel, the index of the block drawn there (-1 = none).</summary>
public sealed record RenderResult3D(RgbaImage Image, int[] BlockIds, int Width, int Height)
{
    public int BlockAt(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? -1 : BlockIds[y * Width + x];
}

/// <summary>
/// Software 3D renderer for build previews: depth-buffered, perspective-correct textured or map-coloured faces,
/// ambient occlusion, sun shadows, block outlines and chunk/map grid lines, orthographic or perspective.
/// Pure C#, so it runs anywhere the app runs.
/// </summary>
public static class Viewer3D
{
    // Corner offsets for each face direction, in order (u,v) = (0,0) (1,0) (1,1) (0,1).
    internal static readonly float[][] Corners =
    [
        [0, 1, 0, 1, 1, 0, 1, 1, 1, 0, 1, 1], // up
        [1, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0], // north
        [0, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1], // south
        [0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0], // west
        [1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 1], // east
    ];
    internal static readonly int[][] NormalsI = [[0, 1, 0], [0, 0, -1], [0, 0, 1], [-1, 0, 0], [1, 0, 0]];
    /// <summary>Without lighting: a fixed brightness per side (up brightest, east/north darkest).</summary>
    private static readonly float[] FlatLight = [1.0f, 0.72f, 0.86f, 0.80f, 0.66f];
    /// <summary>Ambient occlusion levels 0..3 → brightness (Minecraft-like smooth lighting).</summary>
    private static readonly float[] AoLevel = [0.5f, 0.68f, 0.84f, 1f];

    public static RenderResult3D Render(SceneMesh mesh, ColorProfile profile, Camera3D camera, int width, int height,
        LayoutRenderer.IconSource? icons, View3DOptions? options = null)
    {
        var opt = options ?? new View3DOptions();
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
        // Direction towards the camera in world space (for back-face culling, orthographic).
        float vx = sy * cp, vy = sp, vz = cy * cp;
        float camDist = mesh.Extent * 1.6f + 20;
        // Camera position for perspective back-face culling.
        float camX = mesh.CenterX + vx * camDist, camY = mesh.CenterY + vy * camDist, camZ = mesh.CenterZ + vz * camDist;

        var textures = new Dictionary<string, Rgba[]?>();
        Rgba[]? Texture(BlockDefinition b)
        {
            if (opt.Mode != ViewColorMode.Texture || icons is null) return null;
            if (textures.TryGetValue(b.Key, out var t)) return t;
            var icon = icons(b);
            t = icon is null ? null : icon.Width == 16 && icon.Height == 16
                ? Enumerable.Range(0, 256).Select(i => icon[i % 16, i / 16]).ToArray()
                : Resample16(icon);
            textures[b.Key] = t;
            return t;
        }

        float[] sun = SceneMesh.SunDir;
        var sxs = new float[4];
        var sys = new float[4];
        var szs = new float[4];
        var sws = new float[4]; // 1/w for perspective-correct interpolation (1 in orthographic)
        var aoc = new float[4];
        foreach (var f in mesh.Faces)
        {
            var n = NormalsI[f.Dir];
            if (cam.Perspective)
            {
                // Visible when the camera is on the outer side of the face plane.
                float ox = f.X + (n[0] > 0 ? 1 : 0), oy = f.Y + (n[1] > 0 ? 1 : 0), oz = f.Z + (n[2] > 0 ? 1 : 0);
                if (n[0] * (camX - ox) + n[1] * (camY - oy) + n[2] * (camZ - oz) <= 0) continue;
            }
            else if (n[0] * vx + n[1] * vy + n[2] * vz <= 0) continue; // facing away

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
                float scale = zoom, invW = 1;
                if (cam.Perspective)
                {
                    float dist = camDist - d;
                    if (dist < 1) { behind = true; break; }
                    invW = camDist / dist;
                    scale = zoom * invW;
                }
                sxs[k] = halfW + (x1 - panX) * scale;
                sys[k] = halfH - (vyv - panY) * scale;
                szs[k] = d;
                sws[k] = invW;
                aoc[k] = opt.Lighting ? AoLevel[(f.Ao >> (k * 2)) & 3] : 1;
            }
            if (behind) continue;

            var block = mesh.Blocks[f.Block];
            var tex = Texture(block.Block);
            Rgba flat = FaceColour(mesh, profile, f, block, opt.Mode);
            float light;
            if (opt.Lighting)
            {
                float facing = Math.Max(0, n[0] * sun[0] + n[1] * sun[1] + n[2] * sun[2]);
                float sky = f.Dir == 0 ? 0.62f : 0.5f;
                light = sky + 0.5f * facing * f.Sun;
            }
            else light = FlatLight[f.Dir];
            if (block.IsSupport && block.Z != 0) light *= 0.85f;
            bool highlight = f.Block == opt.Highlight;
            // Grid lines (top faces): chunk borders every 16 blocks, map borders every 128 (image rows start at z = 1).
            int gridU0 = 0, gridV0 = 0;
            if (opt.Grid && f.Dir == 0)
            {
                int gx = (int)f.X, gz = (int)f.Z - 1;
                gridU0 = gx % 128 == 0 ? 2 : gx % 16 == 0 ? 1 : 0;
                gridV0 = gz >= 0 && gz % 128 == 0 ? 2 : gz >= 0 && gz % 16 == 0 ? 1 : 0;
            }

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
                // Corner UVs for (0,0) (1,0) (1,1) (0,1), pre-divided by w.
                float wa = sws[a], wb = sws[b2], wc = sws[c];
                float ua = UvU(a) * wa, ub = UvU(b2) * wb, uc = UvU(c) * wc, va = UvV(a) * wa, vb = UvV(b2) * wb, vc = UvV(c) * wc;
                float za = szs[a], zb = szs[b2], zc = szs[c];
                float inv = 1 / area;
                // Outline width in UV units (about one pixel at this zoom).
                float edgeLen = Math.Max(1, (float)Math.Sqrt(Math.Abs(area)));
                // Outlines only once blocks are big enough on screen; when zoomed out they would just darken the picture.
                float edge = (opt.Outlines || highlight) && edgeLen >= 6 ? Math.Min(0.12f, 0.9f / edgeLen) : -1;
                float gridEdge = Math.Min(0.25f, 1.2f / edgeLen);
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
                        float iw = w0 * wa + w1 * wb + w2 * wc;
                        float u = (w0 * ua + w1 * ub + w2 * uc) / iw, v = (w0 * va + w1 * vb + w2 * vc) / iw;
                        Rgba col = tex is null ? flat : tex[Math.Clamp((int)(v * 16), 0, 15) * 16 + Math.Clamp((int)(u * 16), 0, 15)];
                        if (col.A < 128) col = flat; // transparent texels (glass, leaves): show the block colour
                        // Smooth ambient occlusion: bilinear over the four corners.
                        float ao = (aoc[0] * (1 - u) + aoc[1] * u) * (1 - v) + (aoc[3] * (1 - u) + aoc[2] * u) * v;
                        float l = light * ao;
                        if (edge > 0 && (u < edge || v < edge || u > 1 - edge || v > 1 - edge)) l *= highlight ? 0 : 0.72f;
                        int o = i * 4;
                        if (gridU0 + gridV0 > 0 && ((gridU0 > 0 && u < gridEdge) || (gridV0 > 0 && v < gridEdge)))
                        {
                            bool map = (gridU0 == 2 && u < gridEdge) || (gridV0 == 2 && v < gridEdge);
                            col = map ? new Rgba(235, 50, 50) : new Rgba(255, 210, 60);
                            l = 1;
                        }
                        if (highlight && l > 0)
                        {
                            img.Pixels[o] = (byte)Math.Min(255, col.R * l * 0.6f + 100);
                            img.Pixels[o + 1] = (byte)Math.Min(255, col.G * l * 0.6f + 100);
                            img.Pixels[o + 2] = (byte)Math.Min(255, col.B * l * 0.6f + 100);
                        }
                        else
                        {
                            img.Pixels[o] = (byte)Math.Min(255, col.R * l);
                            img.Pixels[o + 1] = (byte)Math.Min(255, col.G * l);
                            img.Pixels[o + 2] = (byte)Math.Min(255, col.B * l);
                        }
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
        bool support = b.IsSupport && b.Z != 0;
        if (mode == ViewColorMode.Shade)
        {
            byte problem = mesh.Problem[f.Block];
            if (problem == 3) return new Rgba(255, 140, 0);
            if (f.Dir != 0 || !mesh.IsTop[f.Block]) return support ? new Rgba(110, 110, 110) : new Rgba(150, 150, 150);
            if (problem == 1) return new Rgba(235, 30, 30);
            if (problem == 2) return new Rgba(230, 40, 230);
            if (b.Z == 0) return new Rgba(120, 120, 120);
            return mesh.TopShade[f.Block] switch
            {
                Shade.Dark => new Rgba(70, 105, 225),
                Shade.Bright => new Rgba(255, 215, 80),
                _ => new Rgba(205, 205, 205),
            };
        }
        if (support) return new Rgba(128, 128, 128);
        return mode switch
        {
            ViewColorMode.Height => LayoutRenderer.Ramp(mesh.MaxY == 0 ? 0 : b.Y / (double)mesh.MaxY),
            ViewColorMode.MapColor => f.Dir == 0 && mesh.IsTop[f.Block] ? profile.ColorOf(b.Block, mesh.TopShade[f.Block]) : profile.ColorOf(b.Block, Shade.Dark),
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
