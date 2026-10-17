using System.Text;
using BMAG.Bedrock.LevelDb;
using BMAG.Bedrock.World;
using BMAG.Imaging;
using BMAG.MapData;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        BMAG CLI
          worlds                         検出したワールドを一覧表示
          info <worldFolder>             LevelDB の概要（キー数の内訳）を表示（読み取り専用）
          maps <worldFolder> [outDir]    地図を一覧し、outDir があれば PNG で書き出す（読み取り専用）
          build <image> <outDir> [--maps 2x2] [--dither none|fs|jjn|burkes|sierra|stucki|atkinson|bayer2|bayer4|ordered3]
                [--staircase flat|classic|valley|dark|bright] [--support none|important|all|double] [--preset 0-7]
                [--origin mapX,mapZ,y] [--name n]
                                         ブロック建築データ（ガイド・構造物・ビヘイビアパック）を出力
          direct <image> <worldFolder> [--maps 2x2] [--name n] [--yes]
                                         ワールドのコピーを作り、画像を地図として追加（--yes なしは計画表示のみ）
          dump <worldFolder> <keyPrefix> / find <worldFolder> <name>   調査用（読み取り専用）
        """);
    return 0;
}

try
{
    switch (args[0])
    {
        case "worlds":
            foreach (var w in WorldLocator.FindWorlds())
                Console.WriteLine($"{w.FolderName}\t{w.Name}\t{w.Version}\t{w.LastPlayed:yyyy-MM-dd}\t{w.Error}");
            break;

        case "info":
        {
            using var db = LevelDbDatabase.Open(Path.Combine(args[1], "db"));
            var all = db.Scan(Array.Empty<byte>());
            Console.WriteLine($"tables={db.TableCount} lastSeq={db.LastSequence} keys={all.Count} logCorruption={db.LogCorruption ?? "none"}");
            var named = all.Where(kv => kv.Key.All(b => b >= 0x20 && b < 0x7F))
                .GroupBy(kv => new string(Encoding.ASCII.GetString(kv.Key).TakeWhile(c => c != '_' && !char.IsDigit(c)).ToArray()))
                .OrderByDescending(g => g.Count());
            foreach (var g in named) Console.WriteLine($"  {g.Key,-28} {g.Count()}");
            Console.WriteLine($"  (binary chunk keys)          {all.Count(kv => !kv.Key.All(b => b >= 0x20 && b < 0x7F))}");
            break;
        }

        case "maps":
        {
            using var db = LevelDbDatabase.Open(Path.Combine(args[1], "db"));
            foreach (var kv in db.Scan("map_"))
            {
                string key = Encoding.ASCII.GetString(kv.Key);
                if (!MapDataEncoder.TryParseKey(key, out long id)) continue;
                var m = MapRecord.Parse(id, kv.Value);
                Console.WriteLine($"{key}\tscale={m.Scale} locked={m.Locked} dim={m.Dimension} center=({m.XCenter},{m.ZCenter}) {m.Width}x{m.Height} colors={m.Colors?.Length} fields={string.Join(",", m.Nbt.Select(e => e.Key + ":" + e.Value.Type))}");
                if (args.Length > 2 && m.ToImage() is { } img) ImageCodec.SavePng(img, Path.Combine(args[2], key + ".png"));
            }
            break;
        }

        case "build":
        {
            // build <image> <outDir> [--maps 2x2] [--dither <mode>] [--staircase flat|classic|valley|dark|bright]
            //       [--support none|important|all|double] [--preset <index>] [--origin mapX,mapZ,y] [--name n]
            var opts = ParseOptions(args.Skip(3));
            var (mx, my) = ParseGrid(opts.GetValueOrDefault("maps", "1x1"));
            var settings = new ImageSettings { MapsX = mx, MapsY = my };
            var img = ImagePipeline.Process(ImageCodec.Load(args[1]), settings);
            var profile = BMAG.Palette.ColorProfile.LoadBuiltIn();
            var qopt = new BMAG.BlockEngine.QuantizeOptions
            {
                Dither = opts.GetValueOrDefault("dither", "none") switch
                {
                    "fs" => BMAG.BlockEngine.DitherMode.FloydSteinberg,
                    "jjn" => BMAG.BlockEngine.DitherMode.JarvisJudiceNinke,
                    "burkes" => BMAG.BlockEngine.DitherMode.Burkes,
                    "sierra" => BMAG.BlockEngine.DitherMode.SierraLite,
                    "stucki" => BMAG.BlockEngine.DitherMode.Stucki,
                    "atkinson" => BMAG.BlockEngine.DitherMode.Atkinson,
                    "bayer2" => BMAG.BlockEngine.DitherMode.Bayer2,
                    "bayer4" => BMAG.BlockEngine.DitherMode.Bayer4,
                    "ordered3" => BMAG.BlockEngine.DitherMode.Ordered3,
                    _ => BMAG.BlockEngine.DitherMode.None,
                },
                Mode = opts.GetValueOrDefault("staircase", "flat") switch
                {
                    "classic" => BMAG.BlockEngine.StaircaseMode.Classic,
                    "valley" or "true" => BMAG.BlockEngine.StaircaseMode.Valley,
                    "dark" => BMAG.BlockEngine.StaircaseMode.FullDark,
                    "bright" => BMAG.BlockEngine.StaircaseMode.FullBright,
                    _ => BMAG.BlockEngine.StaircaseMode.Flat,
                },
            };
            var support = opts.GetValueOrDefault("support", "important") switch
            {
                "none" => BMAG.BlockEngine.SupportMode.None,
                "all" => BMAG.BlockEngine.SupportMode.AllOptimized,
                "double" => BMAG.BlockEngine.SupportMode.AllDouble,
                _ => BMAG.BlockEngine.SupportMode.Important,
            };
            var presets = BMAG.BlockEngine.BlockPreset.BuiltIns(profile);
            var preset = presets[int.Parse(opts.GetValueOrDefault("preset", "0"))];
            var q = BMAG.BlockEngine.ColorQuantizer.Quantize(img, profile, preset.ToSelection(), qopt);
            var layout = BMAG.BlockEngine.BlockLayout.Generate(q, profile.Find("cobblestone")!, 380, support);
            var materials = BMAG.BlockEngine.MaterialCalculator.Calculate(layout, mx);
            BMAG.BlockEngine.WorldOrigin? origin = null;
            if (opts.TryGetValue("origin", out var o))
            {
                var p = o.Split(',').Select(int.Parse).ToArray();
                origin = BMAG.BlockEngine.WorldOrigin.AlignedToMap(p[0], p[1], p[2]);
            }
            string name = opts.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(args[1]));
            BMAG.BlockEngine.BuildGuideGenerator.WriteAll(Path.Combine(args[2], "guide"), layout, q, profile, materials, mx, my, origin, name);
            var result = BMAG.Export.StructureExporter.Export(args[2], name, layout, mx, new BMAG.Export.StructureExportOptions(), origin);
            Console.WriteLine($"blocks={materials.Sum(m => m.Count)} types={q.EntryIndex.Distinct().Count()} maxHeight={layout.MaxHeight} structures={result.Tiles.Count} pack={result.McpackPath}");
            break;
        }

        case "analyze":
        {
            // analyze <image> [--maps 1x1] [--dither none|fs|bayer4] [--preset 0-7]: quality vs height for 3D map art.
            var opts = ParseOptions(args.Skip(2));
            var (mx, my) = ParseGrid(opts.GetValueOrDefault("maps", "1x1"));
            var img = ImagePipeline.Process(ImageCodec.Load(args[1]), new ImageSettings { MapsX = mx, MapsY = my, BackgroundColor = opts.GetValueOrDefault("bg", "#FFFFFF") });
            var profile = BMAG.Palette.ColorProfile.LoadBuiltIn();
            var preset = BMAG.BlockEngine.BlockPreset.BuiltIns(profile)[int.Parse(opts.GetValueOrDefault("preset", "0"))];
            var dither = opts.GetValueOrDefault("dither", "none") switch
            {
                "fs" => BMAG.BlockEngine.DitherMode.FloydSteinberg,
                "bayer4" => BMAG.BlockEngine.DitherMode.Bayer4,
                _ => BMAG.BlockEngine.DitherMode.None,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = BMAG.BlockEngine.HeightAdvisor.Analyze(img, profile, preset.ToSelection(),
                new BMAG.BlockEngine.QuantizeOptions { Mode = BMAG.BlockEngine.StaircaseMode.Valley, Dither = dither },
                profile.Find("cobblestone")!, BMAG.BlockEngine.SupportMode.None);
            Console.WriteLine("mode      limit  maxH  build  ΔE     quality  blocks");
            foreach (var r in rows)
                Console.WriteLine($"{r.Mode,-9} {(r.Mode == BMAG.BlockEngine.StaircaseMode.Flat ? "-" : r.Limit == 0 ? "∞" : r.Limit.ToString()),5} {r.MaxHeight,5} {r.BuildHeight,6} {r.MeanDeltaE,6:0.00} {r.Quality,7:0}% {r.Blocks,7}{(r.Recommended ? "  <= recommended" : "")}");
            Console.WriteLine($"({sw.ElapsedMilliseconds} ms)");
            break;
        }

        case "direct":
        {
            // direct <image> <worldFolder> [--maps 2x2] [--name n] [--yes]: adds the image as maps to a COPY of the world.
            var opts = ParseOptions(args.Skip(3));
            var (mx, my) = ParseGrid(opts.GetValueOrDefault("maps", "1x1"));
            var img = ImagePipeline.Process(ImageCodec.Load(args[1]), new ImageSettings { MapsX = mx, MapsY = my });
            var tiles = TileSplitter.Split(img, mx, my);
            var plan = SafeWorldExporter.Plan(WorldInfo.Load(args[2]), tiles, mx, my,
                new WorldExportOptions { ProjectName = opts.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(args[1])) });
            Console.WriteLine(plan.Describe());
            if (!opts.ContainsKey("yes")) { Console.WriteLine("--yes を付けると実行します。"); break; }
            var res = SafeWorldExporter.Execute(plan, new Progress<string>(Console.WriteLine));
            Console.WriteLine(res.Instructions);
            break;
        }

        case "textures":
        {
            // textures [cacheDir]: downloads block textures from Mojang's bedrock-samples (Minecraft EULA applies).
            Console.WriteLine("Downloading block textures from github.com/Mojang/bedrock-samples (© Mojang AB, Minecraft EULA applies).");
            var provider = new BMAG.Textures.BlockTextureProvider(args.Length > 1 ? args[1] : null);
            int n = await provider.DownloadAsync(BMAG.Palette.ColorProfile.LoadBuiltIn().Blocks, new Progress<string>(Console.WriteLine));
            var missing = BMAG.Palette.ColorProfile.LoadBuiltIn().Blocks.Where(b => provider.GetIcon(b) is null).Select(b => b.Key).ToList();
            Console.WriteLine($"textures={n} cache={provider.CacheDirectory} missing={missing.Count} {string.Join(",", missing)}");
            if (missing.Count == 0) File.WriteAllText(Path.Combine(provider.CacheDirectory, ".complete"), DateTime.UtcNow.ToString("O"));
            else return 3;
            break;
        }

        case "dump":
        {
            // dump <world> <keyPrefix> [maxDepth]: prints NBT values as indented text (read-only).
            using var db = LevelDbDatabase.Open(Path.Combine(args[1], "db"));
            int maxItems = args.Length > 3 ? int.Parse(args[3]) : 40;
            foreach (var kv in db.Scan(args[2]).Take(5))
            {
                Console.WriteLine($"== {Encoding.ASCII.GetString(kv.Key)} ({kv.Value.Length} bytes)");
                foreach (var root in BMAG.Nbt.NbtIO.ReadAll(kv.Value)) Print(root, "", maxItems);
            }
            break;
        }

        case "find":
        {
            // find <world> <text>: prints NBT compounds anywhere in the DB whose "Name"/"name" equals text (read-only).
            using var db = LevelDbDatabase.Open(Path.Combine(args[1], "db"));
            int found = 0;
            foreach (var kv in db.Scan(Array.Empty<byte>()))
            {
                List<BMAG.Nbt.NbtCompound> roots;
                try { roots = BMAG.Nbt.NbtIO.ReadAll(kv.Value); } catch { continue; }
                foreach (var root in roots)
                    foreach (var c in Walk(root))
                        if ((c.GetString("Name") ?? c.GetString("name")) == args[2] && found++ < 3)
                        {
                            Console.WriteLine($"== in key {Convert.ToHexString(kv.Key)}");
                            Print(c, "  ", 30);
                        }
            }
            Console.WriteLine($"found {found}");
            break;
        }

        default:
            Console.Error.WriteLine($"不明なコマンド: {args[0]}");
            return 1;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("エラー: " + ex);
    return 2;
}

static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
{
    var dict = new Dictionary<string, string>();
    var list = args.ToList();
    for (int i = 0; i < list.Count; i++)
    {
        if (!list[i].StartsWith("--")) continue;
        string key = list[i][2..];
        bool hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--");
        dict[key] = hasValue ? list[++i] : "true";
    }
    return dict;
}

static (int, int) ParseGrid(string s)
{
    var p = s.ToLowerInvariant().Split('x');
    return (int.Parse(p[0]), int.Parse(p[1]));
}

static IEnumerable<BMAG.Nbt.NbtCompound> Walk(BMAG.Nbt.NbtTag tag)
{
    if (tag is BMAG.Nbt.NbtCompound c)
    {
        yield return c;
        foreach (var (_, v) in c) foreach (var x in Walk(v)) yield return x;
    }
    else if (tag is BMAG.Nbt.NbtList l)
        foreach (var item in l.Items) foreach (var x in Walk(item)) yield return x;
}

static void Print(BMAG.Nbt.NbtTag tag, string indent, int maxItems)
{
    switch (tag)
    {
        case BMAG.Nbt.NbtCompound c:
            foreach (var (k, v) in c)
            {
                if (v is BMAG.Nbt.NbtCompound or BMAG.Nbt.NbtList) { Console.WriteLine($"{indent}{k}: {v}"); Print(v, indent + "  ", maxItems); }
                else Console.WriteLine($"{indent}{k}: {v}");
            }
            break;
        case BMAG.Nbt.NbtList l:
            int i = 0;
            foreach (var item in l.Items.Take(maxItems))
            {
                if (item is BMAG.Nbt.NbtCompound or BMAG.Nbt.NbtList) { Console.WriteLine($"{indent}[{i}]"); Print(item, indent + "  ", maxItems); }
                else Console.WriteLine($"{indent}[{i}] {item}");
                i++;
            }
            if (l.Count > maxItems) Console.WriteLine($"{indent}... ({l.Count - maxItems} more)");
            break;
    }
}
