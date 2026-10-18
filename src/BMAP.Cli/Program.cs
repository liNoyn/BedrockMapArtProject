using System.Text;
using BMAP.Imaging;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        BMAP CLI
          build <image> <outDir> [--maps 2x2] [--dither none|fs|jjn|burkes|sierra|stucki|atkinson|bayer2|bayer4|ordered3]
                [--staircase flat|classic|valley|dark|bright] [--support none|important|all|double] [--preset 0-7]
                [--origin mapX,mapZ,y] [--name n]
                                         ブロック建築データ（.mcstructure とガイド）を出力
          analyze <image> [--maps 1x1] [--dither none|fs|bayer4] [--preset n] [--bg #RRGGBB]
                                         3D マップアートの高さと画質の関係を解析
          bench <image>... [--maps 1x1]  ディザリング・色差の方式を比較（画質と速度）
          render <image> <out.png> [--maps 1x1] [--mode texture|map|shade|height] [--yaw 35] [--pitch 40] [--zoom 0]
                [--size 1200x800] [--textures dir] [--grid] [--nolight]
                                         3D ビューを PNG に描画（各段階の処理時間を表示）
          worlds                         ワールドを一覧表示（読み取り専用）
          maps <world> [outDir]          ワールドの地図を一覧し、outDir があれば PNG で保存（データベースのコピーを読むだけ）
          pad shading|colours <outDir> [--origin mapX,mapZ,y] [--step n]
                                         キャリブレーション用テストパッド（.mcstructure と pad.json）を出力
          calibrate <world> <pad.json> [--out dir] [--en]
                                         ゲームが描いた地図からパッドを読み取り、陰影規則と地図色を測定
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
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var qopt = new BMAP.BlockEngine.QuantizeOptions
            {
                Dither = opts.GetValueOrDefault("dither", "none") switch
                {
                    "fs" => BMAP.BlockEngine.DitherMode.FloydSteinberg,
                    "jjn" => BMAP.BlockEngine.DitherMode.JarvisJudiceNinke,
                    "burkes" => BMAP.BlockEngine.DitherMode.Burkes,
                    "sierra" => BMAP.BlockEngine.DitherMode.SierraLite,
                    "stucki" => BMAP.BlockEngine.DitherMode.Stucki,
                    "atkinson" => BMAP.BlockEngine.DitherMode.Atkinson,
                    "bayer2" => BMAP.BlockEngine.DitherMode.Bayer2,
                    "bayer4" => BMAP.BlockEngine.DitherMode.Bayer4,
                    "ordered3" => BMAP.BlockEngine.DitherMode.Ordered3,
                    _ => BMAP.BlockEngine.DitherMode.None,
                },
                Mode = opts.GetValueOrDefault("staircase", "flat") switch
                {
                    "classic" => BMAP.BlockEngine.StaircaseMode.Classic,
                    "valley" or "true" => BMAP.BlockEngine.StaircaseMode.Valley,
                    "dark" => BMAP.BlockEngine.StaircaseMode.FullDark,
                    "bright" => BMAP.BlockEngine.StaircaseMode.FullBright,
                    _ => BMAP.BlockEngine.StaircaseMode.Flat,
                },
            };
            var support = opts.GetValueOrDefault("support", "important") switch
            {
                "none" => BMAP.BlockEngine.SupportMode.None,
                "all" => BMAP.BlockEngine.SupportMode.AllOptimized,
                "double" => BMAP.BlockEngine.SupportMode.AllDouble,
                _ => BMAP.BlockEngine.SupportMode.Important,
            };
            var presets = BMAP.BlockEngine.BlockPreset.BuiltIns(profile);
            var preset = presets[int.Parse(opts.GetValueOrDefault("preset", "0"))];
            var q = BMAP.BlockEngine.ColorQuantizer.Quantize(img, profile, preset.ToSelection(), qopt);
            BMAP.BlockEngine.WorldOrigin? origin = null;
            if (opts.TryGetValue("origin", out var o))
            {
                var p = o.Split(',').Select(int.Parse).ToArray();
                origin = BMAP.BlockEngine.WorldOrigin.AlignedToMap(p[0], p[1], p[2]);
            }
            // Single parity steps only when the build is placed on the map grid (--origin).
            var layout = BMAP.BlockEngine.BlockLayout.Generate(q, profile.Find("cobblestone")!, 380, support, onMapGrid: origin != null);
            var materials = BMAP.BlockEngine.MaterialCalculator.Calculate(layout, mx);
            string name = opts.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(args[1]));
            BMAP.BlockEngine.BuildGuideGenerator.WriteAll(Path.Combine(args[2], "guide"), layout, q, profile, materials, mx, my, origin, name);
            var result = BMAP.Export.StructureExporter.Export(args[2], name, layout, mx, new BMAP.Export.StructureExportOptions(), origin);
            Console.WriteLine($"blocks={materials.Sum(m => m.Count)} types={q.EntryIndex.Distinct().Count()} maxHeight={layout.MaxHeight} structures={result.Tiles.Count} pack={result.McpackPath}");
            break;
        }

        case "analyze":
        {
            // analyze <image> [--maps 1x1] [--dither none|fs|bayer4] [--preset 0-7]: quality vs height for 3D map art.
            var opts = ParseOptions(args.Skip(2));
            var (mx, my) = ParseGrid(opts.GetValueOrDefault("maps", "1x1"));
            var img = ImagePipeline.Process(ImageCodec.Load(args[1]), new ImageSettings { MapsX = mx, MapsY = my, BackgroundColor = opts.GetValueOrDefault("bg", "#FFFFFF") });
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var preset = BMAP.BlockEngine.BlockPreset.BuiltIns(profile)[int.Parse(opts.GetValueOrDefault("preset", "0"))];
            var dither = opts.GetValueOrDefault("dither", "none") switch
            {
                "fs" => BMAP.BlockEngine.DitherMode.FloydSteinberg,
                "bayer4" => BMAP.BlockEngine.DitherMode.Bayer4,
                _ => BMAP.BlockEngine.DitherMode.None,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rows = BMAP.BlockEngine.HeightAdvisor.Analyze(img, profile, preset.ToSelection(),
                new BMAP.BlockEngine.QuantizeOptions { Mode = BMAP.BlockEngine.StaircaseMode.Valley, Dither = dither },
                profile.Find("cobblestone")!, BMAP.BlockEngine.SupportMode.None, onMapGrid: opts.ContainsKey("grid"));
            Console.WriteLine("mode      limit  maxH  build  ΔE     quality  blocks");
            foreach (var r in rows)
                Console.WriteLine($"{r.Mode,-9} {(r.Mode == BMAP.BlockEngine.StaircaseMode.Flat ? "-" : r.Limit == 0 ? "∞" : r.Limit.ToString()),5} {r.MaxHeight,5} {r.BuildHeight,6} {r.MeanDeltaE,6:0.00} {r.Quality,7:0}% {r.Blocks,7}{(r.Recommended ? "  <= recommended" : "")}");
            Console.WriteLine($"({sw.ElapsedMilliseconds} ms)");
            break;
        }

        case "render":
        {
            // render <image> <out.png> [--staircase valley] [--mode texture|map|shade|height] [--yaw 35] [--pitch 40] [--zoom 0]
            //        [--size 1200x800] [--textures dir] [--focus x,z]: renders the 3D build view to a PNG (for docs and checks).
            var opts = ParseOptions(args.Skip(3));
            var (rmx, rmy) = ParseGrid(opts.GetValueOrDefault("maps", "1x1"));
            var stage = System.Diagnostics.Stopwatch.StartNew();
            void Lap(string what) { Console.WriteLine($"  {what,-12} {stage.ElapsedMilliseconds,6} ms"); stage.Restart(); }
            var img = ImagePipeline.Process(ImageCodec.Load(args[1]), new ImageSettings { MapsX = rmx, MapsY = rmy });
            Lap("image");
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var mode = opts.GetValueOrDefault("staircase", "valley") == "flat" ? BMAP.BlockEngine.StaircaseMode.Flat : BMAP.BlockEngine.StaircaseMode.Valley;
            var q = BMAP.BlockEngine.ColorQuantizer.Quantize(img, profile, BMAP.BlockEngine.BlockPreset.BuiltIns(profile)[0].ToSelection(),
                new BMAP.BlockEngine.QuantizeOptions { Mode = mode, Dither = BMAP.BlockEngine.DitherMode.FloydSteinberg });
            Lap("quantize");
            var layout = BMAP.BlockEngine.BlockLayout.Generate(q, profile.Find("cobblestone")!, 0, BMAP.BlockEngine.SupportMode.AllOptimized);
            Lap("layout");
            var report = BMAP.Shading.ShadingAnalyzer.Analyze(layout, profile, q.Preview);
            Lap("analysis");
            var mesh = BMAP.Preview.SceneMesh.Build(layout, BMAP.Preview.LayoutRegion.All(layout), true, null, report);
            Lap("mesh");
            var (w, h) = ParseGrid(opts.GetValueOrDefault("size", "1200x800"));
            var cam = mesh.FitCamera(w, h, double.Parse(opts.GetValueOrDefault("yaw", "35")), double.Parse(opts.GetValueOrDefault("pitch", "40")));
            if (double.Parse(opts.GetValueOrDefault("zoom", "0")) is > 0 and var z) cam = cam with { Zoom = z };
            var tex = opts.TryGetValue("textures", out var td) ? new BMAP.Textures.BlockTextureProvider(td) : null;
            var vmode = opts.GetValueOrDefault("mode", "texture") switch
            {
                "map" => BMAP.Preview.ViewColorMode.MapColor, "shade" => BMAP.Preview.ViewColorMode.Shade,
                "height" => BMAP.Preview.ViewColorMode.Height, _ => BMAP.Preview.ViewColorMode.Texture,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = BMAP.Preview.Viewer3D.Render(mesh, profile, cam, w, h, tex is { IsAvailable: true } ? tex.GetIcon : null,
                new BMAP.Preview.View3DOptions { Mode = vmode, Lighting = !opts.ContainsKey("nolight"), Grid = opts.ContainsKey("grid") });
            Lap("render");
            ImageCodec.SavePng(r.Image, args[2]);
            Console.WriteLine($"faces={mesh.FaceCount} render={sw.ElapsedMilliseconds}ms maxY={report.MaxY} mismatches={report.Mismatches} exact={report.IsExact}");
            break;
        }

        case "bench":
        {
            // bench <image>... [--maps 1x1]: compares dithering methods and colour metrics (quality and speed).
            var images = args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
            var opts = ParseOptions(args.Skip(1));
            var (mx, my) = ParseGrid(opts.GetValueOrDefault("maps", "1x1"));
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var sel = BMAP.BlockEngine.BlockPreset.BuiltIns(profile)[0].ToSelection();
            var configs = new List<(string Name, BMAP.BlockEngine.QuantizeOptions O)>();
            foreach (var st in new[] { BMAP.BlockEngine.StaircaseMode.Flat, BMAP.BlockEngine.StaircaseMode.Valley })
            {
                var b = new BMAP.BlockEngine.QuantizeOptions { Mode = st };
                configs.Add(($"{st} none oklab", b with { Dither = BMAP.BlockEngine.DitherMode.None }));
                configs.Add(($"{st} none de2000", b with { Dither = BMAP.BlockEngine.DitherMode.None, Metric = BMAP.ColorEngine.ColorMetric.Ciede2000 }));
                foreach (var d in new[] { BMAP.BlockEngine.DitherMode.FloydSteinberg, BMAP.BlockEngine.DitherMode.JarvisJudiceNinke, BMAP.BlockEngine.DitherMode.SierraLite })
                {
                    configs.Add(($"{st} {d} srgb", b with { Dither = d, LinearDiffusion = false }));
                    configs.Add(($"{st} {d} linear", b with { Dither = d }));
                }
                foreach (var d in new[] { BMAP.BlockEngine.DitherMode.Bayer4, BMAP.BlockEngine.DitherMode.Bayer8, BMAP.BlockEngine.DitherMode.BlueNoise })
                    configs.Add(($"{st} {d}", b with { Dither = d }));
                foreach (var d in new[] { BMAP.BlockEngine.DitherMode.KnollBayer, BMAP.BlockEngine.DitherMode.KnollBlueNoise })
                {
                    configs.Add(($"{st} {d} srgb", b with { Dither = d, LinearDiffusion = false }));
                    configs.Add(($"{st} {d} linear", b with { Dither = d }));
                }
                if (st == BMAP.BlockEngine.StaircaseMode.Valley)
                    foreach (var d in new[] { BMAP.BlockEngine.DitherMode.None, BMAP.BlockEngine.DitherMode.FloydSteinberg, BMAP.BlockEngine.DitherMode.Bayer8, BMAP.BlockEngine.DitherMode.KnollBayer })
                        configs.Add(($"Valley<=4 {d}", b with { Dither = d, MaxHeight = 4 }));
            }
            var totals = configs.ToDictionary(c => c.Name, _ => (P: 0.0, D: 0.0, Ms: 0L));
            foreach (var path in images)
            {
                var img = ImagePipeline.Process(ImageCodec.Load(path), new ImageSettings { MapsX = mx, MapsY = my });
                foreach (var (name, o) in configs)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var q = BMAP.BlockEngine.ColorQuantizer.Quantize(img, profile, sel, o);
                    long ms = sw.ElapsedMilliseconds;
                    var t = totals[name];
                    totals[name] = (t.P + BMAP.BlockEngine.HeightAdvisor.PerceivedDeltaE(img, q.Preview),
                        t.D + BMAP.BlockEngine.HeightAdvisor.DistanceDeltaE2000(img, q.Preview), t.Ms + ms);
                }
                Console.Error.WriteLine("done " + Path.GetFileName(path));
            }
            Console.WriteLine($"{"config",-38} {"blurΔE76",9} {"linΔE2000",10} {"ms",7}   (means over {images.Count} images)");
            foreach (var (name, _) in configs)
            {
                var t = totals[name];
                Console.WriteLine($"{name,-38} {t.P / images.Count,9:0.000} {t.D / images.Count,10:0.000} {t.Ms / images.Count,7}");
            }
            break;
        }

        case "worlds":
            foreach (var w in BMAP.Bedrock.World.WorldLocator.FindWorlds())
                Console.WriteLine($"{w.Version,-10} {w.LastPlayed:yyyy-MM-dd HH:mm}  {w.Name}  {w.Folder}");
            break;

        case "maps":
        {
            // maps <world> [outDir]: lists the maps (read from a temporary copy of the database).
            var maps = BMAP.Bedrock.MapRecord.ReadAll(args[1]);
            foreach (var m in maps.OrderBy(m => m.Id))
            {
                int span = m.Width << m.Scale;
                Console.WriteLine($"map_{m.Id}\tscale={m.Scale} dim={m.Dimension} centre=({m.XCenter},{m.ZCenter}) area x {m.MinX}..{m.MinX + span - 1}, z {m.MinZ}..{m.MinZ + span - 1} locked={m.Locked}");
                if (args.Length > 2 && m.ToImage() is { } img) ImageCodec.SavePng(img, Path.Combine(args[2], $"map_{m.Id}.png"));
            }
            Console.WriteLine($"{maps.Count} maps");
            break;
        }

        case "mapcheck":
        {
            // mapcheck <world>...: are the colours of drawn maps "base colour x multiplier"? (read only)
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var bases = profile.ColourSets.Select(s => BMAP.Imaging.Rgba.FromHex(s.MapColor)).Append(new BMAP.Imaging.Rgba(64, 64, 255)).ToList(); // + water
            var images = new List<BMAP.Imaging.RgbaImage>();
            foreach (var world in args.Skip(1))
            {
                try
                {
                    foreach (var m in BMAP.Bedrock.MapRecord.ReadAll(world).Where(m => m.ToImage() is { } im && im.Pixels.Where((_, i) => i % 4 == 3).Any(a => a > 0)))
                    {
                        // Image maps (imported pictures) sit far outside the world and are locked: not drawn by the game.
                        bool image = m.XCenter == int.MaxValue || m.ZCenter == int.MaxValue || Math.Abs((long)m.XCenter) > 30_000_000;
                        var one = BMAP.Shading.Calibrator.PaletteCheck([m.ToImage()!], bases, [180, 220, 255, 135]);
                        Console.WriteLine($"  map_{m.Id} scale={m.Scale} locked={m.Locked} centre=({m.XCenter},{m.ZCenter}) in {Path.GetFileName(world)}: drawn={one.Drawn} " +
                            string.Join(" ", one.PerMultiplier.Select(kv => $"x{kv.Key}={kv.Value}")) + $" unexplained={one.Unexplained.Sum(u => u.Count)}" + (image ? "  [image map, skipped]" : ""));
                        if (!image) images.Add(m.ToImage()!);
                    }
                }
                catch (Exception ex) { Console.WriteLine($"  skip {Path.GetFileName(world)}: {ex.Message}"); }
            }
            var (drawn, per, unexplained) = BMAP.Shading.Calibrator.PaletteCheck(images, bases, [180, 220, 255, 135]);
            Console.WriteLine($"maps={images.Count} drawn pixels={drawn}");
            foreach (var (m, n) in per) Console.WriteLine($"  x{m}/255: {n} ({(drawn == 0 ? 0 : 100.0 * n / drawn):0.0} %)");
            int un = unexplained.Sum(u => u.Count);
            Console.WriteLine($"  unexplained: {un} ({(drawn == 0 ? 0 : 100.0 * un / drawn):0.0} %), {unexplained.Count} colours");
            foreach (var (c, n) in unexplained.Take(25)) Console.WriteLine($"    {c.ToHex()} x{n}");
            // Which colour sets were seen, in which shade.
            var seen = new Dictionary<(string Set, int M), int>();
            foreach (var img in images)
                for (int i = 0; i < img.Width * img.Height; i++)
                {
                    if (img.Pixels[i * 4 + 3] == 0) continue;
                    foreach (var s in profile.ColourSets.Select(s => (Name: s.NameEn, C: BMAP.Imaging.Rgba.FromHex(s.MapColor))).Append((Name: "Water", C: new BMAP.Imaging.Rgba(64, 64, 255))))
                        foreach (int mul in new[] { 180, 220, 255 })
                            if (img.Pixels[i * 4] == s.C.R * mul / 255 && img.Pixels[i * 4 + 1] == s.C.G * mul / 255 && img.Pixels[i * 4 + 2] == s.C.B * mul / 255)
                                seen[(s.Name, mul)] = seen.GetValueOrDefault((s.Name, mul)) + 1;
                }
            var rc = BMAP.Shading.Calibrator.RoundingCheck(images, bases, profile.Shades);
            Console.WriteLine($"rounding (light/dark pixels): {rc.Shaded} explained, both models {rc.Both}, only base-floor {rc.OnlyFromBase}, only gd-codes from-normal {rc.OnlyFromNormal}");
            Console.WriteLine("colour sets seen (x180 / x220 / x255):");
            foreach (var g in seen.GroupBy(k => k.Key.Set).OrderByDescending(g => g.Sum(x => x.Value)))
                Console.WriteLine($"    {g.Key,-22} {seen.GetValueOrDefault((g.Key, 180)),7} {seen.GetValueOrDefault((g.Key, 220)),7} {seen.GetValueOrDefault((g.Key, 255)),7}");
            break;
        }

        case "refcompare":
        {
            // refcompare [world...]: downloads gd-codes' palette (not stored), compares it with BMAP's profile and,
            // when worlds are given, counts how often each normal colour appears on maps drawn there (read only).
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BMAP (+https://github.com/liNoyn/BedrockMapArtProject)");
            var js = await http.GetStringAsync(BMAP.Palette.ReferencePalette.GdCodesUrl);
            var reference = BMAP.Palette.ReferencePalette.ParseGdCodes(js);
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var cmp = BMAP.Palette.ReferencePalette.Compare(reference, profile);
            var seen = new Dictionary<BMAP.Imaging.Rgba, int>();
            foreach (var world in args.Skip(1))
                try
                {
                    foreach (var m in BMAP.Bedrock.MapRecord.ReadAll(world))
                        if (m.ToImage() is { } im && m.XCenter != int.MaxValue)
                            for (int i = 0; i < im.Width * im.Height; i++)
                                if (im.Pixels[i * 4 + 3] != 0)
                                {
                                    var c = new BMAP.Imaging.Rgba(im.Pixels[i * 4], im.Pixels[i * 4 + 1], im.Pixels[i * 4 + 2]);
                                    seen[c] = seen.GetValueOrDefault(c) + 1;
                                }
                }
                catch (Exception ex) { Console.WriteLine($"  skip {Path.GetFileName(world)}: {ex.Message}"); }
            Console.WriteLine($"gd-codes: {reference.Count} categories, {reference.Select(r => r.Normal).Distinct().Count()} distinct normal colours");
            Console.WriteLine($"{"key",-18} {"gd normal",-10} {"same colour in BMAP",-22} {"block's BMAP set (normal, max diff)",-40} {"on maps",7}");
            foreach (var c in cmp)
                Console.WriteLine($"{c.Reference.Key,-18} {c.Reference.Normal.ToHex(),-10} {c.ExactSet?.NameEn ?? "-",-22} " +
                    $"{(c.BlockSet is null ? "(block not in BMAP)" : $"{c.BlockSet.NameEn} {c.BlockSetNormal?.ToHex()} d={c.Distance}"),-40} {seen.GetValueOrDefault(c.Reference.Normal),7}" +
                    (c.Reference.BiomeDependent ? "  biome" : ""));
            int exact = cmp.Count(c => c.ExactSet != null), sameBlock = cmp.Count(c => c.Distance == 0);
            Console.WriteLine($"identical to a BMAP colour: {exact}/{cmp.Count}; block maps to the same colour in both: {sameBlock}; " +
                $"different: {cmp.Count(c => c.Distance > 0)}; block unknown to BMAP: {cmp.Count(c => c.BlockSet is null)}");
            if (seen.Count > 0)
                Console.WriteLine($"gd-only colours (no identical BMAP colour) seen on maps: {cmp.Where(c => c.ExactSet is null && seen.ContainsKey(c.Reference.Normal)).Select(c => c.Reference.Key).DefaultIfEmpty("none").Aggregate((a, b) => a + ", " + b)}");
            break;
        }

        case "pad":
        {
            // pad shading|colours <outDir> [--origin mapX,mapZ,y] [--step n]
            var opts = ParseOptions(args.Skip(3));
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var support = profile.Find("cobblestone")!;
            var pad = args[1] == "colours" || args[1] == "colors"
                ? BMAP.Shading.ShadingTestPad.CreateColourPad(profile, support, int.Parse(opts.GetValueOrDefault("step", profile.Shades.Step.ToString())))
                : BMAP.Shading.ShadingTestPad.Create(profile, support);
            var p = opts.GetValueOrDefault("origin", "0,0,64").Split(',').Select(int.Parse).ToArray();
            var origin = BMAP.BlockEngine.WorldOrigin.AlignedToMap(p[0], p[1], p[2]);
            var files = BMAP.Shading.PadExporter.Export(args[2], pad, profile, origin, english: opts.ContainsKey("en"));
            Console.WriteLine($"{pad.Descriptor.Kind}: {pad.Descriptor.Samples.Count} samples -> {files}");
            break;
        }

        case "calibrate":
        {
            // calibrate <world> <pad.json> [--out dir] [--en]
            var opts = ParseOptions(args.Skip(3));
            bool en = opts.ContainsKey("en");
            var pad = BMAP.Shading.PadDescriptor.Load(args[2]);
            var profile = BMAP.Palette.ColorProfile.LoadBuiltIn();
            var world = BMAP.Bedrock.World.WorldInfo.Load(args[1]);
            var maps = BMAP.Bedrock.MapRecord.ReadAll(args[1]);
            var predicted = BMAP.Shading.MapSimulator.Simulate(BMAP.Shading.ShadingTestPad.Recreate(pad, profile).Layout, profile).Image;
            var map = BMAP.Shading.Calibrator.FindMap(maps, pad, predicted) ?? throw new InvalidOperationException("no scale-0 map found");
            var r = BMAP.Shading.Calibrator.Analyze(pad, map, profile, world.Version ?? "?", en);
            Console.WriteLine(BMAP.Shading.Calibrator.Report(r, en));
            if (opts.TryGetValue("out", out var outDir))
            {
                Directory.CreateDirectory(outDir);
                ImageCodec.SavePng(BMAP.Shading.Calibrator.ComparisonImage(r), Path.Combine(outDir, "comparison.png"));
                File.WriteAllText(Path.Combine(outDir, "report.txt"), BMAP.Shading.Calibrator.Report(r, en));
            }
            break;
        }

        case "textures":
        {
            // textures [cacheDir]: downloads block textures from Mojang's bedrock-samples (Minecraft EULA applies).
            Console.WriteLine("Downloading block textures from github.com/Mojang/bedrock-samples (© Mojang AB, Minecraft EULA applies).");
            var provider = new BMAP.Textures.BlockTextureProvider(args.Length > 1 ? args[1] : null);
            int n = await provider.DownloadAsync(BMAP.Palette.ColorProfile.LoadBuiltIn().Blocks, new Progress<string>(Console.WriteLine));
            var missing = BMAP.Palette.ColorProfile.LoadBuiltIn().Blocks.Where(b => provider.GetIcon(b) is null).Select(b => b.Key).ToList();
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
