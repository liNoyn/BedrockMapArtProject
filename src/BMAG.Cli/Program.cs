using System.Text;
using BMAG.Imaging;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        BMAG CLI
          build <image> <outDir> [--maps 2x2] [--dither none|fs|jjn|burkes|sierra|stucki|atkinson|bayer2|bayer4|ordered3]
                [--staircase flat|classic|valley|dark|bright] [--support none|important|all|double] [--preset 0-7]
                [--origin mapX,mapZ,y] [--name n]
                                         ブロック建築データ（.mcstructure とガイド）を出力
          analyze <image> [--maps 1x1] [--dither none|fs|bayer4] [--preset n] [--bg #RRGGBB]
                                         3D マップアートの高さと画質の関係を解析
          textures [cacheDir]            ブロックテクスチャを取得
        """);
    return 0;
}

try
{
    switch (args[0])
    {
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

        case "render":
        {
            // render <image> <out.png> [--staircase valley] [--mode texture|map|shade|height] [--yaw 35] [--pitch 40] [--zoom 0]
            //        [--size 1200x800] [--textures dir] [--focus x,z]: renders the 3D build view to a PNG (for docs and checks).
            var opts = ParseOptions(args.Skip(3));
            var img = ImagePipeline.Process(ImageCodec.Load(args[1]), new ImageSettings());
            var profile = BMAG.Palette.ColorProfile.LoadBuiltIn();
            var mode = opts.GetValueOrDefault("staircase", "valley") == "flat" ? BMAG.BlockEngine.StaircaseMode.Flat : BMAG.BlockEngine.StaircaseMode.Valley;
            var q = BMAG.BlockEngine.ColorQuantizer.Quantize(img, profile, BMAG.BlockEngine.BlockPreset.BuiltIns(profile)[0].ToSelection(),
                new BMAG.BlockEngine.QuantizeOptions { Mode = mode, Dither = BMAG.BlockEngine.DitherMode.FloydSteinberg });
            var layout = BMAG.BlockEngine.BlockLayout.Generate(q, profile.Find("cobblestone")!, 0, BMAG.BlockEngine.SupportMode.Important);
            var report = BMAG.Shading.ShadingAnalyzer.Analyze(layout, profile, q.Preview);
            var mesh = BMAG.Preview.SceneMesh.Build(layout, BMAG.Preview.LayoutRegion.All(layout), true, null, report);
            var (w, h) = ParseGrid(opts.GetValueOrDefault("size", "1200x800"));
            var cam = mesh.FitCamera(w, h, double.Parse(opts.GetValueOrDefault("yaw", "35")), double.Parse(opts.GetValueOrDefault("pitch", "40")));
            if (double.Parse(opts.GetValueOrDefault("zoom", "0")) is > 0 and var z) cam = cam with { Zoom = z };
            var tex = opts.TryGetValue("textures", out var td) ? new BMAG.Textures.BlockTextureProvider(td) : null;
            var vmode = opts.GetValueOrDefault("mode", "texture") switch
            {
                "map" => BMAG.Preview.ViewColorMode.MapColor, "shade" => BMAG.Preview.ViewColorMode.Shade,
                "height" => BMAG.Preview.ViewColorMode.Height, _ => BMAG.Preview.ViewColorMode.Texture,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = BMAG.Preview.Viewer3D.Render(mesh, profile, cam, w, h, tex is { IsAvailable: true } ? tex.GetIcon : null,
                new BMAG.Preview.View3DOptions { Mode = vmode, Lighting = !opts.ContainsKey("nolight"), Grid = opts.ContainsKey("grid") });
            ImageCodec.SavePng(r.Image, args[2]);
            Console.WriteLine($"faces={mesh.FaceCount} render={sw.ElapsedMilliseconds}ms maxY={report.MaxY} mismatches={report.Mismatches} exact={report.IsExact}");
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
