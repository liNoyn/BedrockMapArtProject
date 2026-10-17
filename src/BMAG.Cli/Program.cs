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
          build <image> <outDir> [--maps 2x2] [--dither fs|atkinson|bayer4|none] [--staircase] [--origin mapX,mapZ,y] [--name n]
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
            // build <image> <outDir> [--maps 2x2] [--dither fs|atkinson|bayer4|none] [--staircase] [--origin mapX,mapZ,y] [--name n]
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
                    "atkinson" => BMAG.BlockEngine.DitherMode.Atkinson,
                    "bayer4" => BMAG.BlockEngine.DitherMode.Bayer4,
                    _ => BMAG.BlockEngine.DitherMode.None,
                },
                Mode = opts.ContainsKey("staircase") ? BMAG.BlockEngine.BuildMode.Staircase : BMAG.BlockEngine.BuildMode.Flat,
            };
            var q = BMAG.BlockEngine.ColorQuantizer.Quantize(img, profile, new BMAG.BlockEngine.PaletteSelection(), qopt);
            var layout = BMAG.BlockEngine.BlockLayout.Generate(q, profile.Find("cobblestone")!, 380);
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
