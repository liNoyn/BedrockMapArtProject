using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BMAP.BlockEngine;
using BMAP.Palette;

namespace BMAP.Export;

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

public enum StructureSplit
{
    /// <summary>64x64 pieces (+ separate support row pieces): loadable with a structure block.</summary>
    Tiles64,
    /// <summary>One piece per map (128 x 129 with the support row for the top maps).</summary>
    PerMap,
    /// <summary>Everything in one piece (for /structure load from packs or editors such as Amulet).</summary>
    Whole,
    /// <summary>128x128 art pieces, with the northern support row in separate 128x1 pieces.</summary>
    Tiles128,
}

public sealed record StructureExportOptions
{
    public StructureSplit Split { get; init; } = StructureSplit.Tiles128;
    /// <summary>Horizontal tile size for <see cref="StructureSplit.Tiles64"/> (max 64, the structure block limit).</summary>
    public int TileSize { get; init; } = 64;
    /// <summary>Vertical limit per piece; taller builds are split vertically.</summary>
    public int MaxTileHeight { get; init; } = 256;
    public string Namespace { get; init; } = "bmap";
    /// <summary>Also write a behavior pack with load functions (off by default).</summary>
    public bool WriteBehaviorPack { get; init; }
    /// <summary>Language of the placement guide.</summary>
    public bool English { get; init; }
}

/// <summary>Splits a layout into .mcstructure pieces and writes them with a placement guide (StructureExporter, design 8).</summary>
public static class StructureExporter
{
    public static PaletteBlock ToPaletteBlock(BlockDefinition b) => new(b.Name, b.StatesToNbt(), b.Version);

    public static List<StructureTile> Split(BlockLayout layout, int mapsX, StructureExportOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxTileHeight);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mapsX);
        var tiles = new List<StructureTile>();
        int ts = options.TileSize;
        int order = 0;

        if (options.Split is not (StructureSplit.Tiles64 or StructureSplit.Tiles128))
        {
            // Per map: the support row joins the top maps. Whole: one bucket.
            int size = options.Split == StructureSplit.PerMap ? 128 : int.MaxValue / 2;
            var groups = new Dictionary<(int Tx, int Tz), List<BlockPlacement>>();
            foreach (var p in layout.Placements())
            {
                var key = (p.X / size, Math.Max(0, p.Z - 1) / size);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(p);
            }
            foreach (var key in groups.Keys.OrderBy(k => k.Tz).ThenBy(k => k.Tx))
            {
                int x0 = options.Split == StructureSplit.Whole ? 0 : key.Tx * 128;
                int z0 = key.Tz == 0 ? 0 : key.Tz * 128 + 1;
                int w = options.Split == StructureSplit.Whole ? layout.Width : Math.Min(128, layout.Width - x0);
                int d = options.Split == StructureSplit.Whole ? layout.Depth : (key.Tz == 0 ? 129 : Math.Min(128, layout.Depth - z0));
                string id = options.Split == StructureSplit.Whole ? "all" : $"map_{key.Tz * mapsX + key.Tx + 1:00}";
                tiles.AddRange(BuildPieces(groups[key], layout, x0, z0, w, d, id, mapsX, options, ref order));
            }
            return tiles;
        }

        ts = options.Split == StructureSplit.Tiles128 ? 128 : Math.Clamp(ts, 1, 64);
        // Bucket every block (visible and support) by horizontal tile.
        var buckets = new Dictionary<(int Tx, int Tz), List<BlockPlacement>>();
        foreach (var p in layout.Placements())
        {
            var key = (p.X / ts, p.Z == 0 ? -1 : (p.Z - 1) / ts); // Tz = -1: the support row
            if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = [];
            list.Add(p);
        }

        // Support row pieces (Z = 0), one tile wide and one block deep, then the art pieces row by row.
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
        bool sliced = maxY - minY >= options.MaxTileHeight;
        for (long sliceY = minY; sliceY <= maxY; sliceY += options.MaxTileHeight, part++)
        {
            int y0 = (int)sliceY;
            // Each slice starts at its lowest block; slices without blocks are skipped.
            int top = (int)Math.Min(sliceY + options.MaxTileHeight - 1, maxY);
            if (!blocks.Any(b => b.Y >= y0 && b.Y <= top)) continue;
            int h = top - y0 + 1;
            var s = new McStructure(w, h, d);
            foreach (var b in blocks)
            {
                int y = b.Y - y0;
                if (y < 0 || y >= h) continue;
                s.SetBlock(b.X - x0, y, b.Z - z0, ToPaletteBlock(b.Block));
            }
            result.Add(new StructureTile
            {
                Id = sliced ? $"{id}_y{part}" : id,
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
    /// Writes the .mcstructure files into an "mcstructure" subfolder with a placement guide (text + CSV), a zip and,
    /// optionally, a behavior pack with load functions. Every written structure is read back and validated (design 8.5).
    /// </summary>
    public static StructureExportResult Export(string outDir, string projectName, BlockLayout layout, int mapsX,
        StructureExportOptions options, WorldOrigin? origin)
    {
        string slug = Slug(projectName);
        string ns = $"{options.Namespace}_{slug}";
        var tiles = CheckedSplit(layout, mapsX, options);

        // Only this project's earlier pieces are removed: the folder may hold the user's own files.
        string structDir = Path.Combine(outDir, "mcstructure");
        var files = WritePieces(structDir, slug, tiles);

        File.WriteAllText(Path.Combine(outDir, "placement.csv"), PlacementCsv(slug, tiles, origin), new UTF8Encoding(true));
        File.WriteAllText(Path.Combine(outDir, options.English ? "PLACEMENT.txt" : "配置方法.txt"),
            PlacementGuide(projectName, slug, tiles, origin, options), new UTF8Encoding(false));

        string zip = Path.Combine(outDir, $"{slug}_mcstructure.zip");
        ZipFiles(files, zip);

        string? mcpack = options.WriteBehaviorPack ? WriteMcpack(outDir, projectName, slug, ns, tiles, mapsX, layout, origin, keepFolder: true) : null;
        return new StructureExportResult(structDir, files, zip, mcpack, tiles, ns, slug);
    }

    /// <summary>
    /// Writes only the .mcstructure files, straight into <paramref name="outDir"/>: no subfolders, guide, CSV or zip.
    /// A build of one piece is written to <paramref name="singleFile"/> when given. With
    /// <see cref="StructureExportOptions.WriteBehaviorPack"/> the .mcpack file is added (its folder is not kept).
    /// </summary>
    public static StructureExportResult ExportStructuresOnly(string outDir, string projectName, BlockLayout layout, int mapsX,
        StructureExportOptions options, WorldOrigin? origin, string? singleFile = null)
    {
        string slug = Slug(projectName);
        string ns = $"{options.Namespace}_{slug}";
        var tiles = CheckedSplit(layout, mapsX, options);
        List<string> files;
        if (tiles.Count == 1 && singleFile != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(singleFile))!);
            files = [WritePiece(singleFile, tiles[0])];
            outDir = Path.GetDirectoryName(Path.GetFullPath(singleFile))!;
        }
        else files = WritePieces(outDir, slug, tiles);
        string? mcpack = options.WriteBehaviorPack ? WriteMcpack(outDir, projectName, slug, ns, tiles, mapsX, layout, origin, keepFolder: false) : null;
        return new StructureExportResult(outDir, files, "", mcpack, tiles, ns, slug);
    }

    /// <summary>Pieces of the build, checked to cover every block exactly once.</summary>
    public static List<StructureTile> CheckedSplit(BlockLayout layout, int mapsX, StructureExportOptions options)
    {
        var tiles = Split(layout, mapsX, options);
        var problems = ValidateCoverage(layout, tiles);
        if (problems.Count > 0) throw new InvalidOperationException("構造物の分割検証に失敗しました / structure split failed:\n" + string.Join("\n", problems.Take(10)));
        return tiles;
    }

    /// <summary>Writes and validates pieces first, then removes this project's obsolete pieces only.</summary>
    private static List<string> WritePieces(string dir, string slug, List<StructureTile> tiles)
    {
        Directory.CreateDirectory(dir);
        var files = tiles.Select(t => WritePiece(Path.Combine(dir, FileName(slug, t) + ".mcstructure"), t)).ToList();
        var current = files.Select(Path.GetFullPath).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var old in Directory.GetFiles(dir, $"{slug}_*.mcstructure"))
            if (!current.Contains(Path.GetFullPath(old))) File.Delete(old);
        return files;
    }

    /// <summary>Writes one piece and reads it back to check it.</summary>
    private static string WritePiece(string path, StructureTile t)
    {
        AtomicFile.Write(path, stream =>
        {
            stream.Write(t.Structure.ToBytes());
            stream.Position = 0;
            using var readback = new MemoryStream();
            stream.CopyTo(readback);
            var back = McStructure.FromBytes(readback.ToArray());
            var errs = back.Validate();
            if (errs.Count > 0 || back.CountBlocks() != t.BlockCount)
                throw new InvalidDataException($"{Path.GetFileName(path)}: re-read check failed {string.Join(", ", errs)}");
        });
        return path;
    }

    /// <summary>Behavior pack with the pieces and load functions, zipped to BMAP_{slug}.mcpack in <paramref name="outDir"/>.</summary>
    private static string WriteMcpack(string outDir, string projectName, string slug, string ns, List<StructureTile> tiles, int mapsX,
        BlockLayout layout, WorldOrigin? origin, bool keepFolder)
    {
        string packDir = keepFolder ? Path.Combine(outDir, $"BMAP_{slug}_BP") : Path.Combine(Path.GetTempPath(), $"bmap_pack_{Guid.NewGuid():N}");
        if (Directory.Exists(packDir)) Directory.Delete(packDir, true);
        try
        {
            var packStructDir = Path.Combine(packDir, "structures", ns);
            Directory.CreateDirectory(packStructDir);
            foreach (var t in tiles) File.WriteAllBytes(Path.Combine(packStructDir, t.Id + ".mcstructure"), t.Structure.ToBytes());
            WriteManifest(packDir, projectName);
            WriteFunctions(packDir, slug, ns, tiles, mapsX, layout, origin);
            string mcpack = Path.Combine(outDir, $"BMAP_{slug}.mcpack");
            ZipDirectory(packDir, mcpack);
            return mcpack;
        }
        finally
        {
            if (!keepFolder && Directory.Exists(packDir)) Directory.Delete(packDir, true);
        }
    }
    /// <summary>File name of a piece; it becomes the structure's name after importing it in game.</summary>
    public static string FileName(string slug, StructureTile t) => $"{slug}_{t.Id}";

    private static void WriteManifest(string packDir, string projectName)
    {
        var manifest = new
        {
            format_version = 2,
            header = new
            {
                name = $"BMAP: {projectName}",
                description = "Bedrock Map Art Project",
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
        string fnDir = Path.Combine(packDir, "functions", "bmap", slug);
        Directory.CreateDirectory(fnDir);
        string Line(StructureTile t, bool absolute) => absolute && origin is { } o
            ? $"structure load {ns}:{t.Id} {o.X + t.OffsetX} {o.Y + t.OffsetY} {o.Z + t.OffsetZ}"
            : $"structure load {ns}:{t.Id} ~{t.OffsetX} ~{t.OffsetY} ~{t.OffsetZ}";

        var all = new StringBuilder("# origin = north-west corner of the support row (layout 0,0,0)\n");
        foreach (var t in tiles.OrderBy(t => t.Order)) all.AppendLine(Line(t, false));
        File.WriteAllText(Path.Combine(fnDir, "all.mcfunction"), all.ToString(), new UTF8Encoding(false));
        int mapCount = mapsX * (layout.ImageHeight / 128);
        for (int n = 1; n <= mapCount; n++)
        {
            var sb = new StringBuilder($"# map #{n}\n");
            foreach (var t in tiles.Where(t => t.MapNumbers.Contains(n)).OrderBy(t => t.Order)) sb.AppendLine(Line(t, false));
            File.WriteAllText(Path.Combine(fnDir, $"map_{n:00}.mcfunction"), sb.ToString(), new UTF8Encoding(false));
        }
        if (origin is not null)
        {
            var abs = new StringBuilder("# absolute world coordinates\n");
            foreach (var t in tiles.OrderBy(t => t.Order)) abs.AppendLine(Line(t, true));
            File.WriteAllText(Path.Combine(fnDir, "all_absolute.mcfunction"), abs.ToString(), new UTF8Encoding(false));
        }
    }

    private static string PlacementCsv(string slug, List<StructureTile> tiles, WorldOrigin? origin)
    {
        var sb = new StringBuilder("order,file,offset_x,offset_y,offset_z,world_x,world_y,world_z,size_x,size_y,size_z,blocks,maps\n");
        foreach (var t in tiles.OrderBy(t => t.Order))
        {
            var s = t.Structure;
            string w = origin is { } o ? $"{o.X + t.OffsetX},{o.Y + t.OffsetY},{o.Z + t.OffsetZ}" : ",,";
            sb.AppendLine($"{t.Order + 1},{FileName(slug, t)}.mcstructure,{t.OffsetX},{t.OffsetY},{t.OffsetZ},{w},{s.SizeX},{s.SizeY},{s.SizeZ},{t.BlockCount},\"{string.Join(" ", t.MapNumbers)}\"");
        }
        return sb.ToString();
    }

    /// <summary>Step-by-step guide for importing the files with a structure block (Bedrock, Windows).</summary>
    public static string PlacementGuide(string projectName, string slug, List<StructureTile> tiles, WorldOrigin? origin, StructureExportOptions options)
    {
        bool en = options.English;
        bool tooBig = tiles.Any(t => t.Structure.SizeX > 64 || t.Structure.SizeZ > 64);
        var sb = new StringBuilder();
        sb.AppendLine($"Bedrock Map Art Project — {projectName}");
        sb.AppendLine(en ? $"{tiles.Count} .mcstructure files in the \"mcstructure\" folder (also zipped)." : $".mcstructure ファイル {tiles.Count} 個（mcstructure フォルダ、zip にもまとめています）。");
        sb.AppendLine();
        if (en)
        {
            sb.AppendLine("How to place (creative / cheats on):");
            sb.AppendLine("1. Flatten the area first. Air in the files is 'structure void', so existing blocks are kept.");
            sb.AppendLine("2. /give @s structure_block, place it, set the mode to Load and press \"Import\", then pick a file.");
            sb.AppendLine("   The imported structure is saved in the world under the file name (usually \"mystructure:<file name>\").");
            sb.AppendLine("3. Either set the structure block's offset so the corner lands on the coordinates below and press Load,");
            sb.AppendLine("   or run the command shown for each file after importing it.");
            sb.AppendLine("4. The target chunks must be loaded: stay near the piece you are loading.");
            if (tooBig) sb.AppendLine("Note: pieces larger than 64 blocks cannot be loaded with a structure block; use them with /structure load or editors such as Amulet.");
        }
        else
        {
            sb.AppendLine("配置のしかた（クリエイティブ・チート有効）:");
            sb.AppendLine("1. 先に地面を平らにしてください。ファイル内の空気は「ストラクチャーヴォイド」なので既存のブロックは残ります。");
            sb.AppendLine("2. /give @s structure_block で構造ブロックを出して置き、モードを「読み込み」にして「インポート」からファイルを選びます。");
            sb.AppendLine("   インポートした構造物はファイル名でワールドに保存されます（通常は「mystructure:ファイル名」）。");
            sb.AppendLine("3. 構造ブロックのオフセットを下の座標に角が来るよう設定して「読み込み」を押すか、");
            sb.AppendLine("   インポート後に各ファイルのコマンドを実行します。");
            sb.AppendLine("4. 置き先のチャンクが読み込まれている必要があります。読み込むピースの近くにいてください。");
            if (tooBig) sb.AppendLine("注意: 64 ブロックを超えるピースは構造ブロックでは読み込めません。/structure load や Amulet などのエディタで使ってください。");
        }
        sb.AppendLine();
        sb.AppendLine(origin is null
            ? (en ? "Positions relative to the north-west corner of the support row (layout 0,0,0):" : "支え行の北西角（レイアウト 0,0,0）からの位置:")
            : (en ? "World coordinates of each piece's north-west bottom corner:" : "各ピースの北西・下の角のワールド座標:"));
        foreach (var t in tiles.OrderBy(t => t.Order))
        {
            var s = t.Structure;
            string pos = origin is { } o ? $"{o.X + t.OffsetX} {o.Y + t.OffsetY} {o.Z + t.OffsetZ}" : $"~{t.OffsetX} ~{t.OffsetY} ~{t.OffsetZ}";
            sb.AppendLine($"  {t.Order + 1,3}. {FileName(slug, t)}.mcstructure  [{s.SizeX}x{s.SizeY}x{s.SizeZ}]  @ {pos}");
            sb.AppendLine($"       /structure load \"mystructure:{FileName(slug, t)}\" {pos}");
        }
        return sb.ToString();
    }

    /// <summary>Zips the given files flat (only this export's pieces, not other files in the folder).</summary>
    public static void ZipFiles(IEnumerable<string> files, string zipPath)
    {
        var sources = files.ToArray();
        AtomicFile.Write(zipPath, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in sources) zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.Optimal);
        });
    }

    public static void ZipDirectory(string dir, string zipPath)
    {
        string destination = Path.GetFullPath(zipPath);
        var sources = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(file => !string.Equals(Path.GetFullPath(file), destination,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        AtomicFile.Write(zipPath, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var file in sources)
            {
                string entry = Path.GetRelativePath(dir, file).Replace('\\', '/');
                zip.CreateEntryFromFile(file, entry, CompressionLevel.Optimal);
            }
        });
    }
}

public sealed record StructureExportResult(string StructureDirectory, List<string> Files, string ZipPath, string? McpackPath,
    List<StructureTile> Tiles, string Namespace, string Slug);
