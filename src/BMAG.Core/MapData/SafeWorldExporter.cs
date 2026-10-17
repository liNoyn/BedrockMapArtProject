using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BMAG.Bedrock.LevelDb;
using BMAG.Bedrock.World;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.Nbt;

namespace BMAG.MapData;

public enum ItemDelivery
{
    /// <summary>Only the map records; items must be obtained another way.</summary>
    None,
    /// <summary>Chest structure holding the maps.</summary>
    Chest,
    /// <summary>Item frame wall structure.</summary>
    FrameWall,
    /// <summary>Both structures.</summary>
    ChestAndFrameWall,
}

public sealed record WorldExportOptions
{
    public string ProjectName { get; init; } = "mapart";
    public long StartId { get; init; } = MapIdAllocator.DefaultStart;
    public ItemDelivery Delivery { get; init; } = ItemDelivery.ChestAndFrameWall;
    public bool GlowFrames { get; init; }
    public bool BackingWall { get; init; } = true;
    /// <summary>Renames the copy (levelname.txt / LevelName) so it is easy to tell apart in the world list.</summary>
    public bool RenameCopy { get; init; } = true;
}

/// <summary>Everything that will be written, shown to the user before execution (design 13.5).</summary>
public sealed class WorldExportPlan
{
    public required WorldInfo Source { get; init; }
    public required VersionProfile Profile { get; init; }
    public required string Destination { get; init; }
    public required string DestinationName { get; init; }
    public required List<MapTile> Tiles { get; init; }
    public required List<long> MapIds { get; init; }
    public required int MapsX { get; init; }
    public required int MapsY { get; init; }
    public required WorldExportOptions Options { get; init; }
    public required List<string> ExpectedKeys { get; init; }
    public required long SourceBytes { get; init; }
    public required int ExistingMapCount { get; init; }
    public string ChestStructureName => $"bmag:{StructureExporter.Slug(Options.ProjectName)}_maps";
    public string WallStructureName => $"bmag:{StructureExporter.Slug(Options.ProjectName)}_wall";

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"元ワールド: {Source.Name} ({Source.Folder})  ※変更しません");
        sb.AppendLine($"ゲームバージョン: {Source.Version}  → プロファイル: {Profile.Label}");
        sb.AppendLine($"出力先（コピー）: {DestinationName}");
        sb.AppendLine($"  {Destination}");
        sb.AppendLine($"コピー容量: {SourceBytes / 1024.0 / 1024.0:0.0} MB");
        sb.AppendLine($"既存の地図: {ExistingMapCount} 件");
        sb.AppendLine($"追加する地図: {Tiles.Count} 枚 ({MapsX} x {MapsY}), ID {MapIds.First()} ～ {MapIds.Last()}");
        sb.AppendLine("書き込むキー:");
        foreach (var k in ExpectedKeys) sb.AppendLine($"  {k}");
        return sb.ToString();
    }
}

public sealed record WorldExportResult(string Destination, List<long> MapIds, List<string> WrittenKeys, string LogPath, string Instructions);

/// <summary>
/// Writes generated maps into a COPY of a world (SafeWorldExporter, design 10). The original is never written:
/// it is hashed before and after copying, the copy is verified against it, and all writes go to the copy only.
/// </summary>
public static class SafeWorldExporter
{
    public static WorldExportPlan Plan(WorldInfo world, List<MapTile> tiles, int mapsX, int mapsY, WorldExportOptions options)
    {
        if (!WorldInfo.LooksLikeWorld(world.Folder)) throw new InvalidOperationException("Bedrock ワールドのフォルダではありません（level.dat と db が必要です）。");
        var profile = VersionProfile.ForWorld(world.VersionParts)
            ?? throw new NotSupportedException($"このワールドのバージョン ({world.Version ?? "不明"}) は書き込み対象外です。画像編集・PNG・構造物出力は引き続き使えます。");

        List<long> ids;
        int existing;
        using (var db = LevelDbDatabase.Open(Path.Combine(world.Folder, "db"), readOnly: true))
        {
            var alloc = MapIdAllocator.FromDatabase(db);
            existing = alloc.Used.Count;
            ids = alloc.Allocate(tiles.Count, options.StartId);
        }

        var keys = ids.Select(MapDataEncoder.KeyFor).ToList();
        string slug = StructureExporter.Slug(options.ProjectName);
        if (options.Delivery is ItemDelivery.Chest or ItemDelivery.ChestAndFrameWall) keys.Add($"structuretemplate_bmag:{slug}_maps");
        if (options.Delivery is ItemDelivery.FrameWall or ItemDelivery.ChestAndFrameWall) keys.Add($"structuretemplate_bmag:{slug}_wall");

        string parent = Path.GetDirectoryName(world.Folder)!;
        string folderName = $"{world.FolderName}_bmag_{DateTime.Now:yyyyMMdd_HHmmss}";
        return new WorldExportPlan
        {
            Source = world, Profile = profile, Destination = Path.Combine(parent, folderName),
            DestinationName = $"{world.Name} (BMAG {slug})", Tiles = tiles, MapIds = ids, MapsX = mapsX, MapsY = mapsY,
            Options = options, ExpectedKeys = keys, SourceBytes = DirectorySize(world.Folder), ExistingMapCount = existing,
        };
    }

    public static WorldExportResult Execute(WorldExportPlan plan, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var log = new StringBuilder();
        void Log(string msg)
        {
            log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
            progress?.Report(msg);
        }

        string tempDest = plan.Destination + "_incomplete";
        string logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BMAG", "logs");
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, $"export_{DateTime.Now:yyyyMMdd_HHmmss}.log");

        try
        {
            if (WorldLocator.IsMinecraftRunning())
                throw new InvalidOperationException("Minecraft が起動中です。ワールドを閉じるだけでなく、ゲームを終了してから実行してください。");
            if (Directory.Exists(plan.Destination) || Directory.Exists(tempDest))
                throw new IOException($"出力先がすでに存在します: {plan.Destination}");
            var drive = new DriveInfo(Path.GetPathRoot(plan.Destination)!);
            long needed = plan.SourceBytes + plan.Tiles.Count * 70_000L + 50_000_000L;
            if (drive.AvailableFreeSpace < needed)
                throw new IOException($"空き容量が不足しています（必要: {needed / 1048576} MB, 空き: {drive.AvailableFreeSpace / 1048576} MB）。");

            Log("元ワールドのハッシュを計算しています…");
            var before = HashTree(plan.Source.Folder, ct);

            Log("ワールドをコピーしています…");
            CopyTree(plan.Source.Folder, tempDest, ct);

            Log("コピーを検証しています…");
            var copied = HashTree(tempDest, ct);
            var after = HashTree(plan.Source.Folder, ct);
            if (!SameHashes(before, after))
                throw new IOException("コピー中に元ワールドが変更されました。Minecraft やバックアップソフトが使用していないか確認してください。");
            if (!SameHashes(before, copied))
                throw new IOException("コピーしたファイルが元ワールドと一致しません。");
            Log($"コピー OK（{copied.Count} ファイル）。元ワールドは変更されていません。");

            var written = WriteToCopy(plan, tempDest, Log, ct);

            if (plan.Options.RenameCopy) RenameWorld(tempDest, plan.DestinationName, Log);

            Directory.Move(tempDest, plan.Destination);
            var finalHashes = HashTree(plan.Source.Folder, ct);
            if (!SameHashes(before, finalHashes)) throw new IOException("元ワールドのハッシュが変化しました（想定外）。");
            Log($"完了: {plan.Destination}");

            WriteMetadata(plan, written);
            string instructions = Instructions(plan);
            log.AppendLine().AppendLine(instructions);
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
            return new WorldExportResult(plan.Destination, plan.MapIds, written, logPath, instructions);
        }
        catch (Exception ex)
        {
            Log($"失敗: {ex.Message}");
            try
            {
                if (Directory.Exists(tempDest)) Directory.Delete(tempDest, true); // only our own unfinished copy
                Log("未完成のコピーを削除しました。元ワールドは変更されていません。");
            }
            catch (Exception cleanup) { Log($"未完成のコピーを削除できませんでした: {tempDest} ({cleanup.Message})"); }
            File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
            throw;
        }
    }

    private static List<string> WriteToCopy(WorldExportPlan plan, string worldDir, Action<string> log, CancellationToken ct)
    {
        var batch = new WriteBatch();
        var expected = new Dictionary<string, byte[]>();
        void Put(string key, byte[] value) { batch.Put(key, value); expected[key] = value; }

        for (int i = 0; i < plan.Tiles.Count; i++)
        {
            var nbt = MapDataEncoder.CreateMapNbt(plan.MapIds[i], plan.Tiles[i].Image, plan.Profile);
            Put(MapDataEncoder.KeyFor(plan.MapIds[i]), MapDataEncoder.Serialize(nbt));
            plan.Tiles[i].AssignedMapId = plan.MapIds[i];
        }

        var grid = new long[plan.MapsX, plan.MapsY];
        foreach (var t in plan.Tiles) grid[t.GridX, t.GridY] = t.AssignedMapId!.Value;
        string slug = StructureExporter.Slug(plan.Options.ProjectName);
        if (plan.Options.Delivery is ItemDelivery.Chest or ItemDelivery.ChestAndFrameWall)
            Put($"structuretemplate_bmag:{slug}_maps", MapItemGenerator.CreateChestStructure(plan.MapIds, plan.Profile).ToBytes());
        if (plan.Options.Delivery is ItemDelivery.FrameWall or ItemDelivery.ChestAndFrameWall)
        {
            var backing = plan.Options.BackingWall
                ? new PaletteBlock("minecraft:smooth_stone", new NbtCompound(), MapItemGenerator.LegacyBlockVersion)
                : null;
            Put($"structuretemplate_bmag:{slug}_wall",
                MapItemGenerator.CreateFrameWall(grid, plan.Profile, plan.Options.GlowFrames && plan.Profile.SupportsGlowFrames, backing).ToBytes());
        }

        ct.ThrowIfCancellationRequested();
        log($"コピー側の LevelDB に {batch.Count} 件を 1 バッチで書き込みます…");
        using (var db = LevelDbDatabase.Open(Path.Combine(worldDir, "db"), readOnly: false))
        {
            foreach (var id in plan.MapIds)
                if (db.Contains(MapDataEncoder.KeyFor(id))) throw new InvalidOperationException($"地図 ID {id} はすでに使われています。");
            db.Write(batch);
        }

        log("書き込み結果を再読み込みして検証しています…");
        using (var verify = LevelDbDatabase.Open(Path.Combine(worldDir, "db"), readOnly: true))
        {
            if (verify.LogCorruption != null) throw new InvalidDataException($"書き込み後のログ検証に失敗: {verify.LogCorruption}");
            foreach (var (key, value) in expected)
            {
                var back = verify.Get(key) ?? throw new InvalidDataException($"{key} を読み戻せません。");
                if (!back.AsSpan().SequenceEqual(value)) throw new InvalidDataException($"{key} の内容が一致しません。");
                if (key.StartsWith("map_"))
                {
                    var rec = MapRecord.Parse(long.Parse(key[4..]), back);
                    if (rec.Colors?.Length != MapDataEncoder.PixelBytes) throw new InvalidDataException($"{key} の colors サイズが不正です。");
                }
            }
        }
        log($"検証 OK: {expected.Count} 件");
        return expected.Keys.ToList();
    }

    private static void RenameWorld(string worldDir, string newName, Action<string> log)
    {
        File.WriteAllText(Path.Combine(worldDir, "levelname.txt"), newName, new UTF8Encoding(false));
        string datPath = Path.Combine(worldDir, "level.dat");
        var original = File.ReadAllBytes(datPath);
        if (!LevelDat.RoundTrips(original))
        {
            log("level.dat を完全に再現できないため、ワールド名（LevelName）は変更しません。levelname.txt のみ変更しました。");
            return;
        }
        var dat = LevelDat.Parse(original);
        dat.Root["LevelName"] = new NbtString(newName);
        File.WriteAllBytes(datPath, dat.Serialize());
        if (LevelDat.Read(datPath).LevelName != newName) throw new InvalidDataException("level.dat の書き込み検証に失敗しました。");
        log($"コピーのワールド名を「{newName}」にしました。");
    }

    private static void WriteMetadata(WorldExportPlan plan, List<string> written)
    {
        var meta = new
        {
            generator = "Bedrock Map Art Generator",
            created = DateTime.Now,
            source = plan.Source.Folder,
            project = plan.Options.ProjectName,
            grid = new { plan.MapsX, plan.MapsY },
            maps = plan.Tiles.Select(t => new { t.Index, t.GridX, t.GridY, id = t.AssignedMapId, hash = t.ImageHash }),
            keys = written,
        };
        File.WriteAllText(Path.Combine(plan.Destination, "bmag_export.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public static string Instructions(WorldExportPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ワールド一覧に「{plan.DestinationName}」が追加されています（元のワールドはそのままです）。");
        sb.AppendLine("地図アイテムの取り出し方（チートを有効にしてください）:");
        if (plan.Options.Delivery is ItemDelivery.Chest or ItemDelivery.ChestAndFrameWall)
            sb.AppendLine($"  チェスト: /structure load {plan.ChestStructureName} ~1 ~ ~");
        if (plan.Options.Delivery is ItemDelivery.FrameWall or ItemDelivery.ChestAndFrameWall)
        {
            sb.AppendLine($"  額縁の壁: /structure load {plan.WallStructureName} ~1 ~ ~");
            sb.AppendLine("    額縁は西向き（-X）に並びます。東を向いて立つと左上が 1 枚目です。向きを変えるには /structure load の回転指定を使ってください。");
        }
        sb.AppendLine("構造ブロック（読み込みモード）に同じ名前を入れても読み込めます。");
        return sb.ToString();
    }

    // ---- file helpers ----

    public static long DirectorySize(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static void CopyTree(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(src, file);
            // The LOCK file is meaningless in a copy (and may be held by another process).
            if (rel.Equals(Path.Combine("db", "LOCK"), StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dst, rel), overwrite: false);
        }
    }

    private static Dictionary<string, string> HashTree(string dir, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(dir, file);
            if (rel.Equals(Path.Combine("db", "LOCK"), StringComparison.OrdinalIgnoreCase)) continue;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            result[rel] = Convert.ToHexString(SHA256.HashData(fs));
        }
        return result;
    }

    private static bool SameHashes(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
