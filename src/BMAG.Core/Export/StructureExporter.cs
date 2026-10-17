using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BMAG.BlockEngine;
using BMAG.Palette;

namespace BMAG.Export;

/// <summary>One structure piece of a split build (design 8.3).</summary>
public sealed class StructureTile
{
    public required string Id { get; init; }
    public required McStructure Structure { get; init; }
    /// <summary>Layout coordinate of the structure's (0,0,0).</summary>
    public required int OffsetX { get; init; }
    public required int OffsetY { get; init; }
    public required int OffsetZ { get; init; }
    /// <summary>1-based map numbers covered by this piece.</summary>
    public required int[] MapNumbers { get; init; }
    public required int Order { get; init; }
    public int BlockCount => Structure.CountBlocks();
}

public sealed record StructureExportOptions
{
    /// <summary>Horizontal tile size (default 64, the structure block limit).</summary>
    public int TileSize { get; init; } = 64;
    /// <summary>Vertical limit per piece; taller columns are split vertically.</summary>
    public int MaxTileHeight { get; init; } = 256;
    public string Namespace { get; init; } = "bmag";
}

/// <summary>Splits a layout into .mcstructure pieces and writes files / behavior packs (StructureExporter, design 8).</summary>
public static class StructureExporter
{
    public static PaletteBlock ToPaletteBlock(BlockDefinition b) => new(b.Name, b.StatesToNbt(), b.Version);

    public static List<StructureTile> Split(BlockLayout layout, int mapsX, StructureExportOptions options)
    {
        var tiles = new List<StructureTile>();
        int ts = options.TileSize;
        int order = 0;

        // Bucket every block (visible and support) by horizontal tile.
        var buckets = new Dictionary<(int Tx, int Tz), List<BlockPlacement>>();
        foreach (var p in layout.Placements())
        {
            var key = (p.X / ts, p.Z == 0 ? -1 : (p.Z - 1) / ts); // Tz = -1: the support row
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(p);
        }

        // Support row pieces (Z = 0), 64 x 1 each, then the art pieces row by row.
        foreach (var key in buckets.Keys.OrderBy(k => k.Tz).ThenBy(k => k.Tx))
        {
            int x0 = key.Tx * ts;
            int z0 = key.Tz < 0 ? 0 : key.Tz * ts + 1;
            int w = Math.Min(ts, layout.Width - x0);
            int d = key.Tz < 0 ? 1 : Math.Min(ts, layout.Depth - z0);
            string id = key.Tz < 0 ? $"support_{key.Tx}" : $"t_{key.Tx}_{key.Tz}";
            tiles.AddRange(BuildPieces(buckets[key], layout, x0, z0, w, d, id, mapsX, options, ref order));
        }
        return tiles;
    }

    private static IEnumerable<StructureTile> BuildPieces(List<BlockPlacement> blocks, BlockLayout layout, int x0, int z0,
        int w, int d, string id, int mapsX, StructureExportOptions options, ref int order)
    {
        var result = new List<StructureTile>();
        if (blocks.Count == 0) return result;
        int minY = blocks.Min(b => b.Y), maxY = blocks.Max(b => b.Y);

        var maps = new SortedSet<int>();
        for (int z = z0; z < z0 + d; z++)
            for (int x = x0; x < x0 + w; x++)
                maps.Add(Math.Max(0, z - 1) / 128 * mapsX + x / 128 + 1);

        int part = 0;
        for (int y0 = minY; y0 <= maxY; y0 += options.MaxTileHeight, part++)
        {
            int h = Math.Min(options.MaxTileHeight, maxY - y0 + 1);
            var s = new McStructure(w, h, d);
            foreach (var b in blocks)
            {
                int y = b.Y - y0;
                if (y < 0 || y >= h) continue;
                s.SetBlock(b.X - x0, y, b.Z - z0, ToPaletteBlock(b.Block));
            }
            result.Add(new StructureTile
            {
                Id = maxY - minY < options.MaxTileHeight ? id : $"{id}_y{part}",
                Structure = s, OffsetX = x0, OffsetY = y0, OffsetZ = z0, MapNumbers = maps.ToArray(), Order = order++,
            });
        }
        return result;
    }

    /// <summary>Checks that pieces cover every placed block exactly once (design 8.5: no gaps, no overlap).</summary>
    public static List<string> ValidateCoverage(BlockLayout layout, List<StructureTile> tiles)
    {
        var problems = new List<string>();
        var seen = new Dictionary<(int, int, int), string>();
        foreach (var t in tiles)
        {
            var s = t.Structure;
            problems.AddRange(s.Validate().Select(p => $"{t.Id}: {p}"));
            for (int x = 0; x < s.SizeX; x++)
                for (int y = 0; y < s.SizeY; y++)
                    for (int z = 0; z < s.SizeZ; z++)
                    {
                        if (s.Indices[s.IndexOf(x, y, z)] < 0) continue;
                        var key = (t.OffsetX + x, t.OffsetY + y, t.OffsetZ + z);
                        if (!seen.TryAdd(key, t.Id)) problems.Add($"{t.Id} と {seen[key]} が {key} で重複しています。");
                    }
        }
        int expected = layout.Placements().Count();
        if (seen.Count != expected) problems.Add($"構造物のブロック数 {seen.Count} が配置数 {expected} と一致しません。");
        foreach (var p in layout.Placements())
            if (!seen.ContainsKey((p.X, p.Y, p.Z))) { problems.Add($"({p.X},{p.Y},{p.Z}) が構造物に含まれていません。"); break; }
        return problems;
    }

    /// <summary>Makes a lowercase identifier safe for namespaces, file names and functions.</summary>
    public static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        if (s.Length == 0) s = "mapart";
        if (char.IsDigit(s[0])) s = "m" + s;
        return s.Length > 32 ? s[..32] : s;
    }

    /// <summary>
    /// Writes .mcstructure files, a behavior pack folder with load functions, and a zipped .mcpack (design 8.4).
    /// Every written file is read back and validated.
    /// </summary>
    public static StructureExportResult Export(string outDir, string projectName, BlockLayout layout, int mapsX,
        StructureExportOptions options, WorldOrigin? origin)
    {
        string slug = Slug(projectName);
        string ns = $"{options.Namespace}_{slug}";
        var tiles = Split(layout, mapsX, options);
        var problems = ValidateCoverage(layout, tiles);
        if (problems.Count > 0) throw new InvalidOperationException("構造物の分割検証に失敗しました:\n" + string.Join("\n", problems.Take(10)));

        string packDir = Path.Combine(outDir, $"BMAG_{slug}_BP");
        if (Directory.Exists(packDir)) Directory.Delete(packDir, true);
        string structDir = Path.Combine(packDir, "structures", ns);
        Directory.CreateDirectory(structDir);
        foreach (var t in tiles)
        {
            var bytes = t.Structure.ToBytes();
            var path = Path.Combine(structDir, t.Id + ".mcstructure");
            File.WriteAllBytes(path, bytes);
            // Re-read check (design 8.5).
            var back = McStructure.FromBytes(File.ReadAllBytes(path));
            var errs = back.Validate();
            if (errs.Count > 0 || back.CountBlocks() != t.BlockCount)
                throw new InvalidDataException($"{t.Id}.mcstructure の再読み込み検証に失敗しました: {string.Join(", ", errs)}");
        }

        WriteManifest(packDir, projectName);
        WriteFunctions(packDir, slug, ns, tiles, mapsX, layout, origin);
        File.WriteAllText(Path.Combine(packDir, "BMAG_README.txt"), Instructions(projectName, slug, ns, tiles, origin), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outDir, "structures.csv"), TilesCsv(tiles, ns, origin), new UTF8Encoding(true));

        string mcpack = Path.Combine(outDir, $"BMAG_{slug}.mcpack");
        if (File.Exists(mcpack)) File.Delete(mcpack);
        ZipDirectory(packDir, mcpack);
        return new StructureExportResult(packDir, mcpack, tiles, ns, slug);
    }

    private static void WriteManifest(string packDir, string projectName)
    {
        var manifest = new
        {
            format_version = 2,
            header = new
            {
                name = $"BMAG: {projectName}",
                description = "Bedrock Map Art Generator で生成したマップアート構造物",
                uuid = Guid.NewGuid().ToString(),
                version = new[] { 1, 0, 0 },
                min_engine_version = new[] { 1, 20, 0 },
            },
            modules = new[]
            {
                new { type = "data", uuid = Guid.NewGuid().ToString(), version = new[] { 1, 0, 0 } },
            },
        };
        File.WriteAllText(Path.Combine(packDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
            new UTF8Encoding(false));
    }

    private static void WriteFunctions(string packDir, string slug, string ns, List<StructureTile> tiles, int mapsX, BlockLayout layout, WorldOrigin? origin)
    {
        string fnDir = Path.Combine(packDir, "functions", "bmag", slug);
        Directory.CreateDirectory(fnDir);
        string Line(StructureTile t, bool absolute) => absolute && origin is { } o
            ? $"structure load {ns}:{t.Id} {o.X + t.OffsetX} {o.Y + t.OffsetY} {o.Z + t.OffsetZ}"
            : $"structure load {ns}:{t.Id} ~{t.OffsetX} ~{t.OffsetY} ~{t.OffsetZ}";

        var all = new StringBuilder("# 実行位置を支え行の北西角 (レイアウト座標 0,0,0) として全体を読み込みます。\n");
        foreach (var t in tiles.OrderBy(t => t.Order)) all.AppendLine(Line(t, false));
        File.WriteAllText(Path.Combine(fnDir, "all.mcfunction"), all.ToString(), new UTF8Encoding(false));

        int mapCount = mapsX * (layout.ImageHeight / 128);
        for (int n = 1; n <= mapCount; n++)
        {
            var sb = new StringBuilder($"# マップ #{n} の部分だけを読み込みます（実行位置は全体の原点）。\n");
            foreach (var t in tiles.Where(t => t.MapNumbers.Contains(n)).OrderBy(t => t.Order)) sb.AppendLine(Line(t, false));
            File.WriteAllText(Path.Combine(fnDir, $"map_{n:00}.mcfunction"), sb.ToString(), new UTF8Encoding(false));
        }
        if (origin is not null)
        {
            var abs = new StringBuilder("# 設定したワールド座標へ全体を読み込みます。\n");
            foreach (var t in tiles.OrderBy(t => t.Order)) abs.AppendLine(Line(t, true));
            File.WriteAllText(Path.Combine(fnDir, "all_absolute.mcfunction"), abs.ToString(), new UTF8Encoding(false));
        }
    }

    private static string TilesCsv(List<StructureTile> tiles, string ns, WorldOrigin? origin)
    {
        var sb = new StringBuilder("order,structure,offset_x,offset_y,offset_z,world_x,world_y,world_z,size_x,size_y,size_z,blocks,maps\n");
        foreach (var t in tiles.OrderBy(t => t.Order))
        {
            var s = t.Structure;
            string w = origin is { } o ? $"{o.X + t.OffsetX},{o.Y + t.OffsetY},{o.Z + t.OffsetZ}" : ",,";
            sb.AppendLine($"{t.Order + 1},{ns}:{t.Id},{t.OffsetX},{t.OffsetY},{t.OffsetZ},{w},{s.SizeX},{s.SizeY},{s.SizeZ},{t.BlockCount},\"{string.Join(" ", t.MapNumbers)}\"");
        }
        return sb.ToString();
    }

    private static string Instructions(string projectName, string slug, string ns, List<StructureTile> tiles, WorldOrigin? origin)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BMAG Behavior Pack: {projectName}");
        sb.AppendLine();
        sb.AppendLine("■ 使い方（クリエイティブ／チート有効ワールド向け）");
        sb.AppendLine("1. .mcpack を開いてインポートし、ワールド設定のビヘイビアパックで有効にします。");
        sb.AppendLine("2. 支え行の北西角にしたい位置に立ち、次を実行します:");
        sb.AppendLine($"     /function bmag/{slug}/all");
        sb.AppendLine("   マップ 1 枚ずつ読み込む場合: /function bmag/" + slug + "/map_01 など（実行位置は常に全体の原点）。");
        if (origin is not null) sb.AppendLine($"   設定したワールド座標に置く場合: /function bmag/{slug}/all_absolute");
        sb.AppendLine("3. 個別に読み込む場合は structures.csv の座標で /structure load を使うか、構造ブロックの読み込みモードで名前を指定します。");
        sb.AppendLine();
        sb.AppendLine("■ 注意");
        sb.AppendLine("- 読み込み先のチャンクが読み込まれていないと失敗します。遠い部分はその近くに移動してから map_XX を実行してください。");
        sb.AppendLine("- 構造物の読み込みにはチートが必要です。チートなしのサバイバルでは建築ガイド（maps フォルダ）を見て手作業で建築してください。");
        sb.AppendLine("- 空白（-1）の部分は既存ブロックを変更しません。地面を平らにしてから読み込んでください。");
        sb.AppendLine($"- 構造物数: {tiles.Count}、名前空間: {ns}");
        return sb.ToString();
    }

    public static void ZipDirectory(string dir, string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string entry = Path.GetRelativePath(dir, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
        }
    }
}

public sealed record StructureExportResult(string PackDirectory, string McpackPath, List<StructureTile> Tiles, string Namespace, string Slug);
