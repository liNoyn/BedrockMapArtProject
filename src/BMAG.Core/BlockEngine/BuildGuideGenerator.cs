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
        List<MaterialLine> materials, int mapsX, int mapsY, WorldOrigin? origin, string projectName)
    {
        Directory.CreateDirectory(dir);
        ImageCodec.SavePng(q.Preview, Path.Combine(dir, "preview_block_map.png"));
        File.WriteAllText(Path.Combine(dir, "materials.csv"), MaterialsCsv(materials), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "placements.csv"), PlacementsCsv(layout, origin), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(dir, "summary.txt"), Summary(layout, profile, materials, mapsX, mapsY, origin, projectName), new UTF8Encoding(false));

        var mapsDir = Path.Combine(dir, "maps");
        Directory.CreateDirectory(mapsDir);
        for (int my = 0; my < mapsY; my++)
            for (int mx = 0; mx < mapsX; mx++)
            {
                int n = my * mapsX + mx + 1;
                using (var png = RenderMapGuide(layout, q, mx, my, origin))
                using (var fs = File.Create(Path.Combine(mapsDir, $"map_{n:00}_guide.png")))
                    png.SaveTo(fs);
                File.WriteAllText(Path.Combine(mapsDir, $"map_{n:00}_rows.txt"), RowInstructions(layout, mx, my, origin, n), new UTF8Encoding(false));
            }
    }

    public static string MaterialsCsv(List<MaterialLine> materials)
    {
        var sb = new StringBuilder();
        sb.AppendLine("種類,ブロック,識別子,状態,必要数,スタック,シュルカーボックス,使用マップ,代替ブロック");
        foreach (var m in materials)
            sb.AppendLine(string.Join(",",
                m.IsSupport ? "支え" : "本体", Csv(m.Block.DisplayName), m.Block.Name, Csv(m.Block.StatesText),
                m.Count, Csv(m.Stacks), m.ShulkerBoxes, Csv(m.MapsText), Csv(m.AlternativesText)));
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
        int mapsX, int mapsY, WorldOrigin? origin, string projectName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Bedrock Map Art Generator - 建築ガイド: {projectName}");
        sb.AppendLine($"生成日時: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"地図: {mapsX} x {mapsY} 枚 ({mapsX * 128} x {mapsY * 128} ブロック)");
        sb.AppendLine($"方式: {ModeName(layout.Mode)}, 支えブロック: {SupportName(layout.Supports)}, 最大高さ: {layout.MaxHeight}");
        sb.AppendLine($"色プロファイル: {profile.Name} ({profile.Id})");
        var used = materials.Where(m => !m.IsSupport).Select(m => m.Block).ToList();
        int unverified = used.Count(b => !b.Verified);
        if (unverified > 0)
            sb.AppendLine($"注意: 使用ブロック {used.Count} 種のうち {unverified} 種の地図色は実機未検証の推定値です。");
        sb.AppendLine();
        sb.AppendLine("座標系: X+ = 東（画像の右）, Z+ = 南（画像の下）, 北が画像の上。");
        sb.AppendLine("Z=0 の行は地図の外側（北側）に置く支えブロック行です。1 行目の陰影を正しくするために必要です。");
        if (origin is { } o)
        {
            sb.AppendLine($"ワールド座標の原点 (支え行の北西角): X={o.X}, Y={o.Y}, Z={o.Z}");
            sb.AppendLine($"画像左上ピクセルのワールド座標: X={o.X}, Z={o.Z + 1}");
        }
        else
        {
            sb.AppendLine("スケール 0 の地図の範囲は X/Z = 128k-64 ～ 128k+63 です。画像左上を (128i-64, 128j-64) に置くと地図 1 枚にぴったり収まります。");
        }
        sb.AppendLine();
        sb.AppendLine("必要素材:");
        foreach (var m in materials)
            sb.AppendLine($"  {(m.IsSupport ? "[支え] " : "")}{m.Block.DisplayName}: {m.Count} 個 ({m.Stacks})");
        sb.AppendLine($"  合計: {materials.Sum(m => m.Count)} 個");
        return sb.ToString();
    }

    /// <summary>Run-length row instructions for survival building, west to east per row.</summary>
    public static string RowInstructions(BlockLayout layout, int mapX, int mapY, WorldOrigin? origin, int mapNumber)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"マップ #{mapNumber} の行ごとの配置（西→東）。座標はレイアウト座標{(origin is null ? "" : "（括弧内はワールド座標）")}。");
        int x0 = mapX * 128;
        int zStart = mapY * 128 + 1;
        if (mapY == 0)
            AppendRow(sb, layout, x0, 0, origin, "支え行");
        for (int z = zStart; z < zStart + 128; z++)
            AppendRow(sb, layout, x0, z, origin, $"行 {z - zStart + 1}");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, BlockLayout layout, int x0, int z, WorldOrigin? origin, string label)
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
            runs.Add($"{range}{hText} {b.DisplayName}×{x - start}");
        }
        sb.AppendLine(string.Join(" | ", runs));
    }

    /// <summary>Top-down guide image for one map with block, chunk (16), structure (64) grid lines and coordinates (design 7.1).</summary>
    public static SKData RenderMapGuide(BlockLayout layout, QuantizeResult q, int mapX, int mapY, WorldOrigin? origin, int cell = 8)
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
        canvas.DrawText($"マップ #{mapY * (layout.Width / 128) + mapX + 1}  {(origin is null ? "レイアウト座標" : "ワールド座標")}  北↑  赤線=構造物(64) 黒線=チャンク(16)", margin, 14, text);

        using var image = surface.Snapshot();
        return image.Encode(SKEncodedImageFormat.Png, 100);
    }

    public static string ModeName(StaircaseMode m) => m switch
    {
        StaircaseMode.Flat => "Flat（平面）",
        StaircaseMode.Classic => "Classic 階段（実験的）",
        StaircaseMode.Valley => "Valley 階段（実験的）",
        StaircaseMode.FullDark => "全面暗（実験的）",
        _ => "全面明（実験的）",
    };

    public static string SupportName(SupportMode m) => m switch
    {
        SupportMode.None => "なし",
        SupportMode.Important => "必要なブロックのみ",
        SupportMode.AllOptimized => "すべて（最適化）",
        _ => "すべて（二重・最適化）",
    };

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
