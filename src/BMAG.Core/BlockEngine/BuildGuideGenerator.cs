using System.Globalization;
using System.Text;
using BMAG.Imaging;
using BMAG.Palette;
using SkiaSharp;

namespace BMAG.BlockEngine;

/// <summary>World position of layout coordinate (0, 0, 0) = north-west corner of the support row (design 7.4).</summary>
public readonly record struct WorldOrigin(int X, int Y, int Z)
{
    /// <summary>
    /// Origin that lines the art up with Bedrock's map grid at scale 0: maps cover x/z = 128k-64 .. 128k+63.
    /// The support row sits one block north of the map area.
    /// </summary>
    public static WorldOrigin AlignedToMap(int mapIndexX, int mapIndexZ, int y) =>
        new(128 * mapIndexX - 64, y, 128 * mapIndexZ - 64 - 1);
}

/// <summary>Creates guide images, row instructions and CSV files (BuildGuideGenerator, design 7).</summary>
public static class BuildGuideGenerator
{
    public static void WriteAll(string dir, BlockLayout layout, QuantizeResult q, ColorProfile profile,
        List<MaterialLine> materials, int mapsX, int mapsY, WorldOrigin? origin, string projectName, bool english = false)
    {
        Directory.CreateDirectory(dir);
        ImageCodec.SavePng(q.Preview, Path.Combine(dir, "preview_block_map.png"));
        File.WriteAllText(Path.Combine(dir, "materials.csv"), MaterialsCsv(materials, english), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "placements.csv"), PlacementsCsv(layout, origin), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "summary.txt"), Summary(layout, profile, materials, mapsX, mapsY, origin, projectName, english), new UTF8Encoding(false));

        var mapsDir = Path.Combine(dir, "maps");
        Directory.CreateDirectory(mapsDir);
        for (int my = 0; my < mapsY; my++)
            for (int mx = 0; mx < mapsX; mx++)
            {
                int n = my * mapsX + mx + 1;
                using (var png = RenderMapGuide(layout, q, mx, my, origin, english: english))
                using (var fs = File.Create(Path.Combine(mapsDir, $"map_{n:00}_guide.png")))
                    png.SaveTo(fs);
                File.WriteAllText(Path.Combine(mapsDir, $"map_{n:00}_rows.txt"), RowInstructions(layout, mx, my, origin, n, english), new UTF8Encoding(false));
            }
    }

    private static string T(bool en, string ja, string english) => en ? english : ja;

    public static string MaterialsCsv(List<MaterialLine> materials, bool en = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(T(en, "種類,ブロック,識別子,状態,必要数,スタック,シュルカーボックス,使用マップ,代替ブロック",
            "kind,block,id,states,count,stacks,shulker_boxes,maps,alternatives"));
        foreach (var m in materials)
            sb.AppendLine(string.Join(",",
                m.IsSupport ? T(en, "支え", "support") : T(en, "本体", "art"), Csv(m.Block.LocalName(en)), m.Block.Name, Csv(m.Block.StatesText),
                m.Count, Csv(m.Stacks), m.ShulkerBoxes, Csv(m.MapsText), Csv(string.Join(", ", m.Alternatives.Select(a => a.LocalName(en))))));
        return sb.ToString();
    }

    public static string PlacementsCsv(BlockLayout layout, WorldOrigin? origin)
    {
        var sb = new StringBuilder();
        sb.AppendLine("x,y,z,world_x,world_y,world_z,block_key,block_name,states,support,pixel_x,pixel_y");
        foreach (var p in layout.Placements())
        {
            string wx = origin is { } o ? (o.X + p.X).ToString() : "";
            string wy = origin is { } o2 ? (o2.Y + p.Y).ToString() : "";
            string wz = origin is { } o3 ? (o3.Z + p.Z).ToString() : "";
            sb.Append(p.X).Append(',').Append(p.Y).Append(',').Append(p.Z).Append(',')
              .Append(wx).Append(',').Append(wy).Append(',').Append(wz).Append(',')
              .Append(p.Block.Key).Append(',').Append(p.Block.Name).Append(',').Append(Csv(p.Block.StatesText)).Append(',')
              .Append(p.IsSupport ? 1 : 0).Append(',').Append(p.SourcePixelX).Append(',').Append(p.SourcePixelY).AppendLine();
        }
        return sb.ToString();
    }

    public static string Summary(BlockLayout layout, ColorProfile profile, List<MaterialLine> materials,
        int mapsX, int mapsY, WorldOrigin? origin, string projectName, bool en = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bedrock Map Art Generator - {T(en, "建築ガイド", "build guide")}: {projectName}");
        sb.AppendLine($"{T(en, "生成日時", "Created")}: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(T(en, $"地図: {mapsX} x {mapsY} 枚 ({mapsX * 128} x {mapsY * 128} ブロック)", $"Maps: {mapsX} x {mapsY} ({mapsX * 128} x {mapsY * 128} blocks)"));
        sb.AppendLine(T(en, $"方式: {ModeName(layout.Mode)}, 支えブロック: {SupportName(layout.Supports)}, 最大高さ: {layout.MaxHeight}",
            $"3D: {ModeName(layout.Mode, true)}, support blocks: {SupportName(layout.Supports, true)}, max height: {layout.MaxHeight}"));
        sb.AppendLine($"{T(en, "色プロファイル", "Colour profile")}: {profile.Name} ({profile.Id})");
        var used = materials.Where(m => !m.IsSupport).Select(m => m.Block).ToList();
        int unverified = used.Count(b => !b.Verified);
        if (unverified > 0)
            sb.AppendLine(T(en, $"注意: 使用ブロック {used.Count} 種のうち {unverified} 種の地図色は実機未検証の推定値です。",
                $"Note: {unverified} of the {used.Count} map colours used are unverified estimates."));
        sb.AppendLine();
        sb.AppendLine(T(en, "座標系: X+ = 東（画像の右）, Z+ = 南（画像の下）, 北が画像の上。", "Axes: X+ = east (image right), Z+ = south (image down), north is up."));
        sb.AppendLine(T(en, "Z=0 の行は地図の外側（北側）に置く支えブロック行です。1 行目の陰影を正しくするために必要です。",
            "Row Z=0 is the support row just north of the map area; it gives the first image row its shade."));
        if (origin is { } o)
        {
            sb.AppendLine(T(en, $"ワールド座標の原点 (支え行の北西角): X={o.X}, Y={o.Y}, Z={o.Z}", $"World origin (north-west corner of the support row): X={o.X}, Y={o.Y}, Z={o.Z}"));
            sb.AppendLine(T(en, $"画像左上ピクセルのワールド座標: X={o.X}, Z={o.Z + 1}", $"Top-left image pixel: X={o.X}, Z={o.Z + 1}"));
        }
        else
        {
            sb.AppendLine(T(en, "スケール 0 の地図の範囲は X/Z = 128k-64 ～ 128k+63 です。画像左上を (128i-64, 128j-64) に置くと地図 1 枚にぴったり収まります。",
                "A scale-0 map covers X/Z = 128k-64 .. 128k+63. Put the top-left pixel at (128i-64, 128j-64) to fill one map exactly."));
        }
        sb.AppendLine();
        sb.AppendLine(T(en, "必要素材:", "Materials:"));
        foreach (var m in materials)
            sb.AppendLine($"  {(m.IsSupport ? T(en, "[支え] ", "[support] ") : "")}{m.Block.LocalName(en)}: {m.Count} ({m.Stacks})");
        sb.AppendLine($"  {T(en, "合計", "Total")}: {materials.Sum(m => m.Count)}");
        return sb.ToString();
    }

    /// <summary>Run-length row instructions for survival building, west to east per row.</summary>
    public static string RowInstructions(BlockLayout layout, int mapX, int mapY, WorldOrigin? origin, int mapNumber, bool en = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(T(en, $"マップ #{mapNumber} の行ごとの配置（西→東）。座標はレイアウト座標{(origin is null ? "" : "（括弧内はワールド座標）")}。",
            $"Map #{mapNumber}, row by row (west to east). Layout coordinates{(origin is null ? "" : " (world coordinates in brackets)")}."));
        int x0 = mapX * 128;
        int zStart = mapY * 128 + 1;
        if (mapY == 0)
            AppendRow(sb, layout, x0, 0, origin, T(en, "支え行", "Support row"), en);
        for (int z = zStart; z < zStart + 128; z++)
            AppendRow(sb, layout, x0, z, origin, $"{T(en, "行", "Row")} {z - zStart + 1}", en);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, BlockLayout layout, int x0, int z, WorldOrigin? origin, string label, bool en)
    {
        string zText = origin is { } o ? $"Z={z} ({o.Z + z})" : $"Z={z}";
        sb.Append($"{label} [{zText}]: ");
        var runs = new List<string>();
        int x = x0;
        while (x < x0 + 128)
        {
            var b = layout.BlockAt(x, z);
            int h = layout.HeightAt(x, z);
            int start = x;
            while (x < x0 + 128 && layout.BlockAt(x, z) == b && layout.HeightAt(x, z) == h) x++;
            if (b is null) continue;
            string range = x - start == 1 ? $"{start}" : $"{start}-{x - 1}";
            string hText = layout.IsFlat ? "" : $" y{h}";
            runs.Add($"{range}{hText} {b.LocalName(en)}×{x - start}");
        }
        sb.AppendLine(string.Join(" | ", runs));
    }

    /// <summary>Top-down guide image for one map with block, chunk (16), structure (64) grid lines and coordinates (design 7.1).</summary>
    public static SKData RenderMapGuide(BlockLayout layout, QuantizeResult q, int mapX, int mapY, WorldOrigin? origin, int cell = 8, bool english = false)
    {
        const int margin = 40;
        int size = 128 * cell;
        var info = new SKImageInfo(size + margin * 2, size + margin * 2 + cell);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        int x0 = mapX * 128, y0 = mapY * 128;
        float top = margin + cell; // room for the support row

        using var fill = new SKPaint { Style = SKPaintStyle.Fill };
        // Support row (only above the top maps).
        if (mapY == 0)
        {
            fill.Color = new SKColor(150, 150, 150);
            canvas.DrawRect(margin, margin, 128 * cell, cell, fill);
        }
        for (int y = 0; y < 128; y++)
            for (int x = 0; x < 128; x++)
            {
                var c = q.Preview[x0 + x, y0 + y];
                fill.Color = q.Get(x0 + x, y0 + y) < 0 ? new SKColor(255, 255, 255) : new SKColor(c.R, c.G, c.B);
                canvas.DrawRect(margin + x * cell, top + y * cell, cell, cell, fill);
            }

        using var thin = new SKPaint { Color = new SKColor(0, 0, 0, 40), StrokeWidth = 1, Style = SKPaintStyle.Stroke };
        using var chunk = new SKPaint { Color = new SKColor(0, 0, 0, 160), StrokeWidth = 1.5f, Style = SKPaintStyle.Stroke };
        using var structure = new SKPaint { Color = new SKColor(220, 30, 30), StrokeWidth = 2.5f, Style = SKPaintStyle.Stroke };
        for (int i = 0; i <= 128; i++)
        {
            var p = i % 64 == 0 ? structure : i % 16 == 0 ? chunk : cell >= 6 ? thin : null;
            if (p is null) continue;
            canvas.DrawLine(margin + i * cell, top, margin + i * cell, top + size, p);
            canvas.DrawLine(margin, top + i * cell, margin + size, top + i * cell, p);
        }

        using var typeface = SKTypeface.FromFamilyName("Yu Gothic UI") ?? SKTypeface.Default;
        using var text = new SKPaint { Color = SKColors.Black, TextSize = 12, IsAntialias = true, Typeface = typeface };
        for (int i = 0; i <= 128; i += 16)
        {
            int bx = x0 + i, bz = y0 + i + 1;
            string lx = origin is { } o ? (o.X + bx).ToString(CultureInfo.InvariantCulture) : bx.ToString(CultureInfo.InvariantCulture);
            string lz = origin is { } o2 ? (o2.Z + bz).ToString(CultureInfo.InvariantCulture) : bz.ToString(CultureInfo.InvariantCulture);
            canvas.DrawText(lx, margin + i * cell + 2, top + size + 14, text);
            canvas.DrawText(lz, 2, top + i * cell + 12, text);
        }
        canvas.DrawText(english
            ? $"Map #{mapY * (layout.Width / 128) + mapX + 1}  {(origin is null ? "layout coordinates" : "world coordinates")}  north up  red = structure (64)  black = chunk (16)"
            : $"マップ #{mapY * (layout.Width / 128) + mapX + 1}  {(origin is null ? "レイアウト座標" : "ワールド座標")}  北↑  赤線=構造物(64) 黒線=チャンク(16)", margin, 14, text);

        using var image = surface.Snapshot();
        return image.Encode(SKEncodedImageFormat.Png, 100);
    }

    public static string ModeName(StaircaseMode m, bool en = false) => m switch
    {
        StaircaseMode.Flat => T(en, "Flat（平面）", "flat"),
        StaircaseMode.Classic => T(en, "Classic 階段（実験的）", "classic staircase (experimental)"),
        StaircaseMode.Valley => T(en, "Valley 階段（実験的）", "valley staircase (experimental)"),
        StaircaseMode.FullDark => T(en, "全面暗（実験的）", "full dark (experimental)"),
        _ => T(en, "全面明（実験的）", "full light (experimental)"),
    };

    public static string SupportName(SupportMode m, bool en = false) => m switch
    {
        SupportMode.None => T(en, "なし", "none"),
        SupportMode.Important => T(en, "必要なブロックのみ", "under blocks that need it"),
        SupportMode.AllOptimized => T(en, "すべて（最適化）", "all (optimized)"),
        _ => T(en, "すべて（二重・最適化）", "all (double, optimized)"),
    };

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
