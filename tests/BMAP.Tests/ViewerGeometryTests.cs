using System.Numerics;
using BMAP.BlockEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Preview;
using BMAP.Shading;

namespace BMAP.Tests;

public class ViewerGeometryTests
{
    private static readonly ColorProfile Profile = ColorProfile.LoadBuiltIn();
    private static BlockLayout Layout(int width, int depth, params int[] heights) => new()
    {
        Width = width, Depth = depth, Cells = Enumerable.Repeat(0, width * depth).ToArray(),
        Heights = heights, Entries = [new PaletteEntry(Profile.Find("stone")!, [])],
        SupportBlock = Profile.Find("cobblestone")!, Mode = StaircaseMode.Valley,
    };

    // Compare picking against independent ray/axis-aligned-box intersections, rather than another rasterizer.
    [Theory]
    [InlineData(false, 35, 40)]
    [InlineData(true, 35, 40)]
    [InlineData(true, 145, 5)]
    [InlineData(true, 225, 25)]
    [InlineData(true, 315, 65)]
    public void DepthAndPicking_MatchRayBoxIntersections(bool perspective, double yaw, double pitch)
    {
        var layout = Layout(3, 4, 0, 4, 9, 2, 8, 1, 12, 0, 5, 3, 15, 7);
        var mesh = SceneMesh.Build(layout, LayoutRegion.All(layout), true);
        const int w = 180, h = 160;
        var cam = mesh.FitCamera(w, h, yaw, pitch, perspective) with { PanX = 0.45, PanY = -0.65 };
        var result = Viewer3D.Render(mesh, Profile, cam, w, h, null, new View3DOptions { Lighting = false });
        double ya = yaw * Math.PI / 180, pi = pitch * Math.PI / 180;
        var right = new Vector3((float)Math.Cos(ya), 0, -(float)Math.Sin(ya));
        var up = new Vector3(-(float)(Math.Sin(ya) * Math.Sin(pi)), (float)Math.Cos(pi), -(float)(Math.Cos(ya) * Math.Sin(pi)));
        var front = Vector3.Cross(right, up);
        var centre = new Vector3(mesh.CenterX, mesh.CenterY, mesh.CenterZ);
        int checkedHits = 0;
        for (int y = 1; y < h; y += 3)
            for (int x = 1; x < w; x += 3)
            {
                float px = (float)((x + 0.5 - w / 2.0) / cam.Zoom + cam.PanX);
                float py = (float)(-(y + 0.5 - h / 2.0) / cam.Zoom + cam.PanY);
                var offset = right * px + up * py;
                var origin = centre + front * mesh.CameraDistance + (perspective ? Vector3.Zero : offset);
                var direction = perspective ? offset / mesh.CameraDistance - front : -front;
                float nearest = float.PositiveInfinity;
                int expected = -1;
                bool tie = false;
                for (int i = 0; i < mesh.Blocks.Count; i++)
                {
                    var b = mesh.Blocks[i];
                    float near = 0, far = float.PositiveInfinity;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        float low = axis == 0 ? b.X : axis == 1 ? b.Y : b.Z;
                        float o = origin[axis], d = direction[axis];
                        if (Math.Abs(d) < 1e-7f) { if (o < low || o > low + 1) far = -1; continue; }
                        float a = (low - o) / d, c = (low + 1 - o) / d;
                        near = Math.Max(near, Math.Min(a, c)); far = Math.Min(far, Math.Max(a, c));
                    }
                    if (near > far) continue;
                    if (Math.Abs(near - nearest) < 0.0001f) tie = true;
                    else if (near < nearest) { nearest = near; expected = i; tie = false; }
                }
                if (tie) continue;
                Assert.True(expected == result.BlockAt(x, y), $"({x},{y}) expected {expected}, got {result.BlockAt(x, y)}");
                if (expected >= 0) checkedHits++;
            }
        Assert.True(checkedHits > 20);
    }

    [Theory]
    [InlineData(false, 35, 40)]
    [InlineData(true, 135, 5)]
    [InlineData(true, 315, 90)]
    public void FitCamera_ContainsTallBuildInWideAndNarrowViewports(bool perspective, double yaw, double pitch)
    {
        var layout = Layout(2, 2, 0, 180, 380, 90);
        var mesh = SceneMesh.Build(layout, LayoutRegion.All(layout), true);
        foreach (var (w, h) in new[] { (600, 120), (120, 600) })
        {
            var cam = mesh.FitCamera(w, h, yaw, pitch, perspective);
            double ya = yaw * Math.PI / 180, pi = pitch * Math.PI / 180;
            foreach (var b in mesh.Blocks)
                foreach (var corner in Viewer3D.Corners.SelectMany(c => Enumerable.Range(0, 4).Select(i => new Vector3(c[i * 3], c[i * 3 + 1], c[i * 3 + 2]))))
                {
                    double x = b.X + corner.X - mesh.CenterX, y = b.Y + corner.Y - mesh.CenterY, z = b.Z + corner.Z - mesh.CenterZ;
                    double z1 = x * Math.Sin(ya) + z * Math.Cos(ya), d = y * Math.Sin(pi) + z1 * Math.Cos(pi);
                    Assert.True(mesh.CameraDistance - d > 1);
                    double scale = perspective ? mesh.CameraDistance / (mesh.CameraDistance - d) : 1;
                    double sx = w / 2.0 + ((x * Math.Cos(ya) - z * Math.Sin(ya)) * scale - cam.PanX) * cam.Zoom;
                    double sy = h / 2.0 - ((y * Math.Cos(pi) - z1 * Math.Sin(pi)) * scale - cam.PanY) * cam.Zoom;
                    Assert.InRange(sx, 0, w); Assert.InRange(sy, 0, h);
                }
        }
    }

    [Theory]
    [InlineData(ShadingModelKind.Checker)]
    [InlineData(ShadingModelKind.Threshold)]
    [InlineData(ShadingModelKind.Graded)]
    public void UntexturedPreviews_AgreeWithSimulator(ShadingModelKind kind)
    {
        var profile = Profile.WithShadingModel(ShadingModel.Candidates.First(m => m.Kind == kind));
        var layout = Layout(4, 5, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1, 1, 4, 4, 4, 4);
        var map = MapSimulator.Simulate(layout, profile);
        var region = LayoutRegion.All(layout);
        var mesh = SceneMesh.Build(layout, region, true);
        var top = LayoutRenderer.RenderTopDown(layout, profile, region, 8, null, false);
        var view = Viewer3D.Render(mesh, profile, new Camera3D { Yaw = 0, Pitch = 90, Zoom = 8 }, 32, 40, null,
            new View3DOptions { Mode = ViewColorMode.MapColor, Lighting = false, Outlines = false });
        for (int row = 0; row < map.Height; row++)
            for (int x = 0; x < map.Width; x++)
            {
                Assert.Equal(map.Image[x, row], top[x * 8 + 4, (row + 1) * 8 + 4]);
                Assert.Equal(map.Image[x, row], view.Image[x * 8 + 4, (row + 1) * 8 + 4]);
            }
    }

    [Fact]
    public void TerrainDependentMap_IsNotReportedAsExact()
    {
        var layout = Layout(1, 3, 0, 0, 0);
        layout.Cells[1] = BlockLayout.Empty;
        var report = ShadingAnalyzer.Analyze(layout, Profile, null);
        Assert.Equal(1, report.DependsOnTerrain);
        Assert.False(report.IsExact);
    }

    [Fact]
    public void CroppedRegion_KeepsNorthernHeightOutsideTheRegion()
    {
        var layout = Layout(1, 3, 0, 1, 2);
        var region = new LayoutRegion(0, 2, 1, 1);
        var mesh = SceneMesh.Build(layout, region, true);
        var result = Viewer3D.Render(mesh, Profile, new Camera3D { Yaw = 0, Pitch = 90, Zoom = 16 }, 16, 16, null,
            new View3DOptions { Mode = ViewColorMode.MapColor, Outlines = false, Lighting = false });
        Assert.Equal(MapSimulator.Simulate(layout, Profile).Image[0, 1], result.Image[8, 8]);
    }

    [Fact]
    public void FaceTextureCallback_IsUsedForTheRenderedTopFace()
    {
        var layout = Layout(1, 2, 0, 0);
        var mesh = SceneMesh.Build(layout, LayoutRegion.All(layout), true);
        var red = RgbaImage.Filled(16, 16, new Rgba(255, 0, 0));
        var blue = RgbaImage.Filled(16, 16, new Rgba(0, 0, 255));
        var result = Viewer3D.Render(mesh, Profile, new Camera3D { Yaw = 0, Pitch = 90, Zoom = 16 }, 16, 32,
            _ => blue, new View3DOptions { Lighting = false, Outlines = false }, (_, face) => face == 0 ? red : blue);
        Assert.Equal(new Rgba(255, 0, 0), result.Image[8, 24]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Export_RejectsNonpositiveVerticalSliceSize(int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StructureExporter.Split(Layout(1, 2, 0, 2), 1,
            new StructureExportOptions { MaxTileHeight = height }));
    }

    [Fact]
    public void Export_LargeSliceSize_DoesNotOverflowAtAnElevatedBase()
    {
        var layout = Layout(1, 2, 1, 2);
        var pieces = StructureExporter.Split(layout, 1, new StructureExportOptions { Split = StructureSplit.Whole, MaxTileHeight = int.MaxValue });
        Assert.Single(pieces);
        Assert.Empty(StructureExporter.ValidateCoverage(layout, pieces));
    }

    [Fact]
    public void FaceTextures_UseWorldFacesAndKeepInventoryMaskSeparate()
    {
        var dir = TestHelpers.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "textures"));
        File.WriteAllText(Path.Combine(dir, "blocks.json"), """
            { "oak_slab": { "textures": { "up": "top", "side": "side", "down": "bottom" },
                            "carried_textures": "inventory" } }
            """);
        File.WriteAllText(Path.Combine(dir, "textures", "terrain_texture.json"), """
            { "texture_data": { "top": { "textures": "top" }, "side": { "textures": "side" },
              "bottom": { "textures": "bottom" }, "inventory": { "textures": "inventory" } } }
            """);
        var colours = new[] { new Rgba(255, 0, 0), new Rgba(0, 255, 0), new Rgba(0, 0, 255), new Rgba(255, 255, 0) };
        var names = new[] { "top", "side", "bottom", "inventory" };
        for (int i = 0; i < names.Length; i++) ImageCodec.SavePng(RgbaImage.Filled(16, 16, colours[i]), Path.Combine(dir, names[i] + ".png"));
        var block = new BlockDefinition { Key = "slab", Name = "minecraft:oak_slab", DisplayName = "slab", MapColor = "#AAAAAA" };
        var textures = new BMAP.Textures.BlockTextureProvider(dir);
        Assert.Equal(colours[0], textures.GetFaceTexture(block, 0)![0, 0]);
        foreach (int face in new[] { 1, 2, 3, 4 }) Assert.Equal(colours[1], textures.GetFaceTexture(block, face)![0, 0]);
        Assert.Equal(colours[2], textures.GetFaceTexture(block, 5)![0, 0]);
        var icon = textures.GetIcon(block)!;
        Assert.Equal(0, icon[0, 0].A);
        Assert.Equal(colours[3], icon[0, 15]);
        textures.UseResourcePack(null);
        Assert.NotSame(icon, textures.GetIcon(block));
    }

    [Fact]
    public void HeightAdvisor_FromClassic_OffersEnforceableValleyBudgets()
    {
        var stone = Profile.Find("stone")!;
        var image = RgbaImage.Filled(8, 16, Profile.ColorOf(stone, Shade.Bright));
        var options = HeightAdvisor.Analyze(image, Profile, new PaletteSelection { Keys = [stone.Key] },
            new QuantizeOptions { Mode = StaircaseMode.Classic }, Profile.Find("cobblestone")!, SupportMode.Important,
            limits: [2, 4], onMapGrid: true);
        Assert.Contains(options, o => o.Mode == StaircaseMode.Classic && o.Unlimited);
        foreach (var option in options.Where(o => o.Limit > 0))
        {
            Assert.Equal(StaircaseMode.Valley, option.Mode);
            Assert.InRange(option.BuildHeight - 1, 0, option.Limit);
        }
        Assert.Equal(2, options.Count(o => o.Limit > 0));
    }
}
