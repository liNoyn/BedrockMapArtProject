using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Shading;

/// <summary>One part of the in-game verification kit: a build covering MapsX x MapsY maps from map index (MapX, MapZ).</summary>
public sealed record KitItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required int MapX { get; init; }
    public required int MapZ { get; init; }
    public int MapsX { get; init; } = 1;
    public int MapsY { get; init; } = 1;
    /// <summary>Predicted map image (MapsX*128 x MapsY*128), relative to the kit folder.</summary>
    public required string Predicted { get; init; }
    /// <summary>pad.json of a calibration pad, relative to the kit folder (colour pad only).</summary>
    public string? Pad { get; init; }
    /// <summary>Water pools (water part only): measured, not predicted.</summary>
    public List<WaterPool>? Pools { get; init; }
    /// <summary>Discovery samples: one 3 x 3 patch per block, to find map colours the palette lacks.</summary>
    public List<DiscoverySample>? Discovery { get; init; }
}

/// <summary>A 3 x 3 patch of one block (default states) with its north-west map pixel at (X, Z).</summary>
public sealed record DiscoverySample(string Block, string States, int X, int Z);

/// <summary>A pool of still water: Size x Size water surface at map pixel (X, Z), Depth blocks deep, on a stone floor.</summary>
/// <param name="NorthDy">Height of the water surface minus the height the map sees just north of the pool (0 = same).</param>
/// <param name="Bars">The north edge holds the water with iron bars (not drawn on maps) instead of a higher wall.</param>
public sealed record WaterPool(int X, int Z, int Size, int Depth, int NorthDy = 0, bool Bars = false, int Width = 0);

/// <summary>kit.json: what was placed where, so <see cref="VerificationKit.Verify"/> can compare the game's maps.</summary>
public sealed record KitManifest
{
    public string Format { get; init; } = "bmap-verification-kit";
    public int Version { get; init; } = 1;
    public required int BaseY { get; init; }
    public required string ProfileId { get; init; }
    public required string Created { get; init; }
    public required List<KitItem> Items { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static KitManifest Load(string path) => JsonSerializer.Deserialize<KitManifest>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException("kit.json を読めません。");
}

/// <summary>Result of comparing one map tile of a kit item with the game's map.</summary>
public sealed record TileCheck(string Item, int MapX, int MapZ, long? MapId, int Compared, int Exact, int Unexplored, int Missing)
{
    public double ExactShare => Compared == 0 ? 0 : Exact / (double)Compared;
}

/// <summary>
/// One set for checking BMAP in game in a single session (docs/TODO.md 2 and 3): the colour pad with every block in
/// all three shades, and map art builds in each staircase mode (flat, classic, valley on 2 x 2 maps, including falling
/// and support-needing blocks), all on the map grid. A behavior pack places each part with one command; afterwards
/// <see cref="Verify"/> compares the maps the game drew with BMAP's predictions pixel by pixel.
/// </summary>
public static class VerificationKit
{
    public const string Namespace = "bmap_kit";

    /// <summary>All parts; <c>parts</c> picks some of them (a second session only needs what is still open).</summary>
    public static readonly string[] AllParts = ["colour_pad", "flat", "classic", "valley_2x2", "large_4x4", "water", "water_steps", "discovery"];

    /// <summary>
    /// Blocks left out of the discovery pad: technical or education blocks, liquids, fire, portals, explosives, blocks
    /// made of two parts (doors, beds, tall plants) and blocks with signs or banners. Everything else is placed in its
    /// default state on a cobblestone floor; blocks that pop off or are not drawn show the floor.
    /// </summary>
    private static readonly string[] DiscoverySkip =
    [
        "air", "allow", "deny", "border_block", "barrier", "structure_", "jigsaw", "command_block", "light_block", "moving_block",
        "piston_arm_collision", "info_update", "reserved6", "unknown", "client_request_placeholder", "camera", "element_", "hard_",
        "colored_torch", "chemistry", "compound_creator", "lab_table", "material_reducer", "element_constructor", "chemical_heat",
        "underwater_torch", "underwater_tnt", "tnt", "fire", "lava", "water", "portal", "end_gateway", "respawn_anchor",
        "bubble_column", "frosted_ice", "_door", "bed", "double_plant", "tall_", "large_fern", "sunflower", "lilac", "rose_bush",
        "peony", "pitcher", "big_dripleaf", "_sign", "banner", "skull", "_head", "candle_cake", "_cake", "glow_frame", "frame",
        "piston_head", "sweet_berry", "cave_vines", "kelp", "seagrass", "sea_pickle", "end_portal_frame", "lit_", "unlit_",
        "powered_", "_wall_banner", "trial_spawner", "vault", "creaking_heart", "mob_spawner",
        // Power sources and blocks that move or act when powered: next to each other they could push samples away.
        "piston", "redstone_torch", "daylight_detector", "observer", "sculk_sensor", "dispenser", "dropper", "crafter",
    ];

    public static string Create(string folder, ColorProfile profile, int baseY = -60, bool english = false,
        IReadOnlyCollection<string>? parts = null, string name = "BMAP_verification_kit", string? blocksJson = null)
    {
        parts ??= ["colour_pad", "flat", "classic", "valley_2x2"];
        foreach (var p in parts) if (!AllParts.Contains(p)) throw new ArgumentException($"unknown kit part {p}");
        string dir = Path.Combine(folder, name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var support = profile.Find("cobblestone") ?? profile.Blocks.First();
        var presets = BlockPreset.BuiltIns(profile);
        var items = new List<KitItem>();
        var pieces = new List<(KitItem Item, List<StructureTile> Tiles, WorldOrigin Origin)>();

        // 1. Colour pad: every block, three shades (map 0, 0).
        if (parts.Contains("colour_pad"))
        {
            var pad = ShadingTestPad.CreateColourPad(profile, support, profile.Shades.Step, english);
            var origin = WorldOrigin.AlignedToMap(0, 0, baseY);
            string padDir = PadExporter.Export(dir, pad, profile, origin, english);
            var item = new KitItem
            {
                Id = "colour_pad", Title = english ? "Colour pad (all blocks)" : "色テストパッド（全ブロック）", MapX = 0, MapZ = 0,
                Predicted = Path.GetRelativePath(dir, Path.Combine(padDir, "predicted_map.png")),
                Pad = Path.GetRelativePath(dir, Path.Combine(padDir, "pad.json")),
            };
            items.Add(item);
            pieces.Add((item, StructureExporter.Split(pad.Layout, 1, new StructureExportOptions { Split = StructureSplit.PerMap }), origin));
        }

        // 2-4. Map art in each mode, from a generated test card (no third-party image).
        var recommended = presets[0].ToSelection();
        var oneEach = presets.Single(p => p.NameEn == "One block per colour").ToSelection();
        (string Id, string Ja, string En, int MapX, int MapZ, int Maps, StaircaseMode Mode, int MaxHeight, PaletteSelection Sel, StructureSplit Split)[] builds =
        [
            ("flat", "平面（1 枚）", "Flat (1 map)", 1, 0, 1, StaircaseMode.Flat, 0, recommended, StructureSplit.PerMap),
            ("classic", "Classic 階段（1 枚）", "Classic staircase (1 map)", 2, 0, 1, StaircaseMode.Classic, 0, recommended, StructureSplit.PerMap),
            ("valley_2x2", "Valley 階段（2 x 2 枚・高さ上限 32・落下 / 支えの要るブロックあり）", "Valley staircase (2 x 2 maps, height limit 32, falling and support blocks)", 3, 0, 2, StaircaseMode.Valley, 32, oneEach, StructureSplit.PerMap),
            ("large_4x4", "Valley 階段（4 x 4 枚・64 x 64 のピースに分割・高さ上限 16）", "Valley staircase (4 x 4 maps, 64 x 64 pieces, height limit 16)", 5, 0, 4, StaircaseMode.Valley, 16, recommended, StructureSplit.Tiles64),
        ];
        foreach (var b in builds.Where(b => parts.Contains(b.Id)))
        {
            var img = TestCard(b.Maps * 128, b.Maps * 128);
            var q = HeightPlanner.Quantize(img, profile, b.Sel, new QuantizeOptions { Mode = b.Mode, Dither = DitherMode.Optimized, MaxHeight = b.MaxHeight },
                support, SupportMode.Important, onMapGrid: true);
            var layout = BlockLayout.Generate(q, support, 300, SupportMode.Important, onMapGrid: true);
            var origin = WorldOrigin.AlignedToMap(b.MapX, b.MapZ, baseY);
            string sub = Path.Combine(dir, b.Id);
            Directory.CreateDirectory(sub);
            ImageCodec.SavePng(img, Path.Combine(sub, "source.png"));
            ImageCodec.SavePng(MapSimulator.Simulate(layout, profile).Image, Path.Combine(sub, "predicted_map.png"));
            var materials = MaterialCalculator.Calculate(layout, b.Maps);
            BuildGuideGenerator.WriteAll(Path.Combine(sub, "guide"), layout, q, profile, materials, b.Maps, b.Maps, origin, b.Id, english);
            StructureExporter.Export(sub, b.Id, layout, b.Maps, new StructureExportOptions { Split = b.Split, English = english }, origin);
            var item = new KitItem
            {
                Id = b.Id, Title = english ? b.En : b.Ja, MapX = b.MapX, MapZ = b.MapZ, MapsX = b.Maps, MapsY = b.Maps,
                Predicted = Path.GetRelativePath(dir, Path.Combine(sub, "predicted_map.png")),
            };
            items.Add(item);
            pieces.Add((item, StructureExporter.Split(layout, b.Maps, new StructureExportOptions { Split = b.Split }), origin));
        }

        // Water: how the game draws water on maps (colour by depth, with or without a checker pattern). Not predicted:
        // BMAP has no water model yet, the kit measures one. 16 pools, depth 1..16, each 30 x 30 inside a stone wall.
        if (parts.Contains("water"))
        {
            const int Cell = 32, MaxDepth = 16;
            var stone = new PaletteBlock("minecraft:stone", new Nbt.NbtCompound(), 18168865);
            var water = new PaletteBlock("minecraft:water", new Nbt.NbtCompound { { "liquid_depth", new Nbt.NbtInt(0) } }, 18168865);
            var s = new McStructure(128, MaxDepth + 1, 128);
            var pools = new List<WaterPool>();
            for (int j = 0; j < 4; j++)
                for (int i = 0; i < 4; i++)
                {
                    int depth = j * 4 + i + 1, x0 = i * Cell, z0 = j * Cell;
                    for (int z = z0; z < z0 + Cell; z++)
                        for (int x = x0; x < x0 + Cell; x++)
                        {
                            s.SetBlock(x, 0, z, stone);
                            bool wall = x == x0 || z == z0 || x == x0 + Cell - 1 || z == z0 + Cell - 1;
                            for (int y = 1; y <= depth; y++) s.SetBlock(x, y, z, wall ? stone : water);
                        }
                    pools.Add(new WaterPool(x0 + 1, z0 + 1, Cell - 2, depth));
                }
            int mapX = -1, mapZ = 0;
            var origin = new WorldOrigin(128 * mapX - 64, baseY, 128 * mapZ - 64);
            string sub = Path.Combine(dir, "water");
            Directory.CreateDirectory(sub);
            ImageCodec.SavePng(new RgbaImage(128, 128), Path.Combine(sub, "predicted_map.png")); // nothing predicted
            var item = new KitItem
            {
                Id = "water", Title = english ? "Water depth 1-16" : "水深 1〜16 のプール", MapX = mapX, MapZ = mapZ,
                Predicted = Path.GetRelativePath(dir, Path.Combine(sub, "predicted_map.png")), Pools = pools,
            };
            items.Add(item);
            pieces.Add((item, [new StructureTile { Id = "pools", Structure = s, OffsetX = 0, OffsetY = 0, OffsetZ = 0, MapNumbers = [1], Order = 0 }], origin));
        }

        // Water steps: is water shaded by the height of the block north of it, like other blocks? Each cell is a pool
        // (water surface at y 4) whose north edge is seen by the map at another height: a higher stone wall (water
        // lower: dark side), or a lower wall topped with iron bars that hold the water but are not drawn (water higher:
        // light side). Two rows, with the cells shifted by one block between rows, so both pixel parities are covered.
        if (parts.Contains("water_steps"))
        {
            var stone = new PaletteBlock("minecraft:stone", new Nbt.NbtCompound(), 18168865);
            var water = new PaletteBlock("minecraft:water", new Nbt.NbtCompound { { "liquid_depth", new Nbt.NbtInt(0) } }, 18168865);
            var bars = new PaletteBlock("minecraft:iron_bars", new Nbt.NbtCompound
            {
                { "minecraft:connection_east", new Nbt.NbtByte(0) }, { "minecraft:connection_north", new Nbt.NbtByte(0) },
                { "minecraft:connection_south", new Nbt.NbtByte(0) }, { "minecraft:connection_west", new Nbt.NbtByte(0) },
            }, 18168865);
            const int Surface = 4, CellW = 10, CellD = 8, Height = 10;
            var s = new McStructure(128, Height, 128);
            var pools = new List<WaterPool>();
            (int Dy, bool Bars)[] kinds = [(0, false), (-1, false), (-2, false), (-3, false), (-4, false), (1, true), (2, true), (3, true)];
            for (int rowSet = 0; rowSet < 2; rowSet++)
                for (int k = 0; k < kinds.Length; k++)
                {
                    var (dy, useBars) = kinds[k];
                    int x0 = 4 + k * (CellW + 4) + rowSet, z0 = 8 + rowSet * 40;
                    // Floor and walls (east, west, south at the surface height; north as the case requires).
                    for (int z = z0; z < z0 + CellD; z++)
                        for (int x = x0; x < x0 + CellW; x++)
                        {
                            s.SetBlock(x, 0, z, stone);
                            bool edge = x == x0 || x == x0 + CellW - 1 || z == z0 + CellD - 1;
                            bool north = z == z0 && !edge;
                            if (north)
                            {
                                int wallTop = Surface - dy; // dy < 0: wall above the water; dy > 0: below it
                                for (int y = 1; y <= Math.Min(wallTop, Height - 1); y++) s.SetBlock(x, y, z, stone);
                                if (useBars) for (int y = wallTop + 1; y <= Surface; y++) s.SetBlock(x, y, z, bars);
                            }
                            else if (edge) for (int y = 1; y <= Surface; y++) s.SetBlock(x, y, z, stone);
                            else for (int y = 1; y <= Surface; y++) s.SetBlock(x, y, z, water);
                        }
                    pools.Add(new WaterPool(x0 + 1, z0 + 1, CellD - 2, Surface, dy, useBars, CellW - 2));
                }
            int mapX = -1, mapZ = 1;
            var origin = new WorldOrigin(128 * mapX - 64, baseY, 128 * mapZ - 64);
            string sub = Path.Combine(dir, "water_steps");
            Directory.CreateDirectory(sub);
            ImageCodec.SavePng(new RgbaImage(128, 128), Path.Combine(sub, "predicted_map.png"));
            var item = new KitItem
            {
                Id = "water_steps", Title = english ? "Water next to higher / lower blocks" : "高さの違うブロックの隣の水（陰影）", MapX = mapX, MapZ = mapZ,
                Predicted = Path.GetRelativePath(dir, Path.Combine(sub, "predicted_map.png")), Pools = pools,
            };
            items.Add(item);
            pieces.Add((item, [new StructureTile { Id = "cells", Structure = s, OffsetX = 0, OffsetY = 0, OffsetZ = 0, MapNumbers = [1], Order = 0 }], origin));
        }

        // Discovery: every other block of Mojang's block list in its default state, 3 x 3 each, on a cobblestone floor.
        if (parts.Contains("discovery"))
        {
            if (blocksJson is null) throw new ArgumentException("discovery needs Mojang's block list (mojang-blocks.json)");
            var doc = JsonDocument.Parse(File.ReadAllText(blocksJson)).RootElement;
            var firstValue = new Dictionary<string, (string Type, JsonElement Value)>();
            foreach (var p in doc.GetProperty("block_properties").EnumerateArray())
                firstValue[p.GetProperty("name").GetString()!] = (p.GetProperty("type").GetString()!, p.GetProperty("values")[0].GetProperty("value"));
            var known = profile.Blocks.Select(b => b.Name).ToHashSet();
            var floor = new PaletteBlock("minecraft:cobblestone", new Nbt.NbtCompound(), 18168865);
            var s = new McStructure(128, 2, 128);
            for (int z = 0; z < 128; z++) for (int x = 0; x < 128; x++) s.SetBlock(x, 0, z, floor);
            var samples = new List<DiscoverySample>();
            int slot = 0;
            foreach (var b in doc.GetProperty("data_items").EnumerateArray())
            {
                string id = b.GetProperty("name").GetString()!;
                string shortName = id["minecraft:".Length..];
                if (known.Contains(id) || DiscoverySkip.Any(k => shortName.Contains(k))) continue;
                var states = new Nbt.NbtCompound();
                var text = new List<string>();
                foreach (var prop in b.GetProperty("properties").EnumerateArray())
                {
                    string pn = prop.GetProperty("name").GetString()!;
                    var (type, v) = firstValue[pn];
                    states.Add(pn, type switch
                    {
                        "bool" => new Nbt.NbtByte((byte)(v.GetBoolean() ? 1 : 0)),
                        "int" => new Nbt.NbtInt(v.GetInt32()),
                        _ => new Nbt.NbtString(v.GetString()!),
                    });
                    text.Add($"{pn}={v}");
                }
                int sx = slot % 42 * 3 + 1, sz = slot / 42 * 3 + 1;
                if (sz + 3 > 127) break;
                var block = new PaletteBlock(id, states, 18168865);
                for (int dz = 0; dz < 3; dz++) for (int dx = 0; dx < 3; dx++) s.SetBlock(sx + dx, 1, sz + dz, block);
                samples.Add(new DiscoverySample(id, string.Join(";", text), sx, sz));
                slot++;
            }
            int mapX = -1, mapZ = 2;
            var origin = new WorldOrigin(128 * mapX - 64, baseY, 128 * mapZ - 64);
            string sub = Path.Combine(dir, "discovery");
            Directory.CreateDirectory(sub);
            ImageCodec.SavePng(new RgbaImage(128, 128), Path.Combine(sub, "predicted_map.png"));
            var item = new KitItem
            {
                Id = "discovery", Title = english ? $"Discovery: {samples.Count} other blocks" : $"未知の色さがし: ほかの {samples.Count} ブロック", MapX = mapX, MapZ = mapZ,
                Predicted = Path.GetRelativePath(dir, Path.Combine(sub, "predicted_map.png")), Discovery = samples,
            };
            items.Add(item);
            pieces.Add((item, [new StructureTile { Id = "blocks", Structure = s, OffsetX = 0, OffsetY = 0, OffsetZ = 0, MapNumbers = [1], Order = 0 }], origin));
        }

        var manifest = new KitManifest { BaseY = baseY, ProfileId = profile.Id, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), Items = items };
        File.WriteAllText(Path.Combine(dir, "kit.json"), manifest.ToJson(), new UTF8Encoding(false));
        WritePack(dir, pieces, packName: name);
        File.WriteAllText(Path.Combine(dir, english ? "HOW-TO.txt" : "手順.txt"), Instructions(manifest, english), new UTF8Encoding(true));
        return dir;
    }

    /// <summary>One behavior pack with every piece and a function per part (absolute coordinates).</summary>
    private static void WritePack(string dir, List<(KitItem Item, List<StructureTile> Tiles, WorldOrigin Origin)> pieces, string packName)
    {
        string pack = Path.Combine(dir, "BMAP_kit_BP");
        string structures = Path.Combine(pack, "structures", Namespace);
        string functions = Path.Combine(pack, "functions", "bmap", "kit");
        Directory.CreateDirectory(structures);
        Directory.CreateDirectory(functions);
        var manifest = new
        {
            format_version = 2,
            header = new { name = packName.Replace('_', ' '), description = "Bedrock Map Art Project", uuid = Guid.NewGuid().ToString(), version = new[] { 1, 0, 0 }, min_engine_version = new[] { 1, 20, 0 } },
            modules = new[] { new { type = "data", uuid = Guid.NewGuid().ToString(), version = new[] { 1, 0, 0 } } },
        };
        File.WriteAllText(Path.Combine(pack, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        foreach (var (item, tiles, o) in pieces)
        {
            var load = new StringBuilder($"# {item.Id}: maps {item.MapX}..{item.MapX + item.MapsX - 1}, {item.MapZ}..{item.MapZ + item.MapsY - 1}\n");
            foreach (var t in tiles.OrderBy(t => t.Order))
            {
                string name = $"{item.Id}_{t.Id}";
                File.WriteAllBytes(Path.Combine(structures, name + ".mcstructure"), t.Structure.ToBytes());
                load.AppendLine($"structure load {Namespace}:{name} {o.X + t.OffsetX} {o.Y + t.OffsetY} {o.Z + t.OffsetZ}");
            }
            File.WriteAllText(Path.Combine(functions, item.Id + ".mcfunction"), load.ToString(), new UTF8Encoding(false));
            // Standing in the middle loads the chunks the structure needs (structure load fails in unloaded chunks).
            int cx = 128 * item.MapX - 64 + item.MapsX * 64, cz = 128 * item.MapZ - 64 + item.MapsY * 64;
            File.WriteAllText(Path.Combine(functions, "goto_" + item.Id + ".mcfunction"),
                $"tp @s {cx} 200 {cz}\ngamemode creative @s\n", new UTF8Encoding(false));
            // Larger parts: one goto / load pair per map, as structure load needs the chunks around each piece loaded.
            if (item.MapsX * item.MapsY > 1)
                for (int row = 0; row < item.MapsY; row++)
                    for (int col = 0; col < item.MapsX; col++)
                    {
                        int n = row * item.MapsX + col + 1;
                        var mine = tiles.Where(t => t.MapNumbers.Length > 0 && t.MapNumbers[0] == n).OrderBy(t => t.Order).ToList();
                        var sb = new StringBuilder($"# {item.Id}: map {n} (map {item.MapX + col}, {item.MapZ + row})\n");
                        foreach (var t in mine) sb.AppendLine($"structure load {Namespace}:{item.Id}_{t.Id} {o.X + t.OffsetX} {o.Y + t.OffsetY} {o.Z + t.OffsetZ}");
                        File.WriteAllText(Path.Combine(functions, $"{item.Id}_m{n}.mcfunction"), sb.ToString(), new UTF8Encoding(false));
                        File.WriteAllText(Path.Combine(functions, $"goto_{item.Id}_m{n}.mcfunction"),
                            $"tp @s {128 * (item.MapX + col)} 200 {128 * (item.MapZ + row)}\ngamemode creative @s\n", new UTF8Encoding(false));
                    }
        }
        string mcpack = Path.Combine(dir, packName + ".mcpack");
        StructureExporter.ZipDirectory(pack, mcpack);
    }

    private static string Instructions(KitManifest m, bool en)
    {
        var sb = new StringBuilder();
        if (en)
        {
            sb.AppendLine("BMAP verification kit — checks block colours and .mcstructure loading in game. The world is never changed by BMAP.");
            sb.AppendLine();
            sb.AppendLine("1. Create a new world: Flat (superflat), creative, cheats on. Activate the .mcpack in this folder as a behavior pack.");
            sb.AppendLine("   Suggested: /gamerule doDaylightCycle false, /time set noon, /gamerule doMobSpawning false.");
            sb.AppendLine($"2. For each part below: /function bmap/kit/goto_<id>, wait a few seconds until the area has loaded, then /function bmap/kit/<id>.");
            sb.AppendLine("3. /give @s empty_map — use it (scale 0, do not zoom out) inside each map area and fly over the whole area until it is fully drawn.");
            sb.AppendLine("   One map per map area. Parts of several maps are placed map by map (commands below): goto, wait, load, draw that map, next.");
            sb.AppendLine("4. Leave the world and close Minecraft so the maps are saved.");
            sb.AppendLine("5. BMAP CLI: verify <world folder> <this folder>  (or send the world folder name).");
        }
        else
        {
            sb.AppendLine("BMAP 実機検証キット — ブロックの地図色と .mcstructure の読み込みをゲーム内で確かめます。BMAP はワールドを変更しません。");
            sb.AppendLine();
            sb.AppendLine("1. 新しいワールドを作ります: フラット（スーパーフラット）・クリエイティブ・チート有効。このフォルダの .mcpack をビヘイビアパックとして有効にします。");
            sb.AppendLine("   おすすめ: /gamerule doDaylightCycle false、/time set noon、/gamerule doMobSpawning false");
            sb.AppendLine("2. 下の各パートで、/function bmap/kit/goto_<id> を実行し、周りが読み込まれるまで数秒待ってから /function bmap/kit/<id> を実行します。");
            sb.AppendLine("3. /give @s empty_map で白紙の地図を出し、各マップの範囲の中で使います（縮尺 0。拡大しないでください）。範囲全体の上を飛んで、すべて描かれるまで待ちます。");
            sb.AppendLine("   マップ 1 枚の範囲ごとに地図 1 枚です。複数枚のパートは、下のコマンドで 1 枚分ずつ「移動 → 待つ → 置く → その地図を描く」を繰り返します。");
            sb.AppendLine("4. ワールドを出て Minecraft を終了し、地図を保存させます。");
            sb.AppendLine("5. BMAP CLI: verify <ワールドのフォルダ> <このフォルダ>（またはワールドのフォルダ名を教えてください）。");
        }
        sb.AppendLine();
        foreach (var i in m.Items)
        {
            int x0 = 128 * i.MapX - 64, z0 = 128 * i.MapZ - 64;
            sb.AppendLine($"- {i.Id}: {i.Title}  X {x0}..{x0 + 128 * i.MapsX - 1}, Z {z0}..{z0 + 128 * i.MapsY - 1}, Y {m.BaseY}~");
            if (i.MapsX * i.MapsY == 1)
                sb.AppendLine($"    /function bmap/kit/goto_{i.Id}   →   /function bmap/kit/{i.Id}");
            else
                for (int n = 1; n <= i.MapsX * i.MapsY; n++)
                    sb.AppendLine($"    {n,2}: /function bmap/kit/goto_{i.Id}_m{n}   →   /function bmap/kit/{i.Id}_m{n}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// What the game drew over each water pool: the colours inside it (one pixel in from the wall), split by the
    /// parity of map x + z, most frequent first. Shows whether water is shaded by depth and whether in a checker pattern.
    /// </summary>
    public static string WaterReport(string kitDir, IReadOnlyList<Bedrock.MapRecord> maps)
    {
        var m = KitManifest.Load(Path.Combine(kitDir, "kit.json"));
        var sb = new StringBuilder();
        foreach (var item in m.Items.Where(i => i.Pools != null))
        {
            var map = maps.Where(r => r.Scale == 0 && r.Dimension == 0 && r.XCenter == 128 * item.MapX && r.ZCenter == 128 * item.MapZ)
                .Select(r => r.ToImage()).FirstOrDefault(i => i != null);
            if (map is null) { sb.AppendLine($"{item.Id}: no map"); continue; }
            if (item.Pools!.Any(p => p.Width > 0))
            {
                // Step cells: the row next to the north edge (shaded by it, if water is shaded at all) and the rest.
                sb.AppendLine("north dy  bars  first row: even (x+z)            odd (x+z)                        | inner rows");
                foreach (var p in item.Pools!)
                {
                    var even = new Dictionary<string, int>(); var odd = new Dictionary<string, int>(); var inner = new Dictionary<string, int>();
                    for (int z = p.Z; z < p.Z + p.Size; z++)
                        for (int x = p.X; x < p.X + p.Width; x++)
                        {
                            int i = (z * 128 + x) * 4;
                            string c = map.Pixels[i + 3] == 0 ? "(unexplored)" : $"#{map.Pixels[i]:X2}{map.Pixels[i + 1]:X2}{map.Pixels[i + 2]:X2}";
                            var d = z > p.Z + 1 ? inner : ((x + z) & 1) == 0 ? even : odd;
                            if (z == p.Z + 1) continue;
                            d[c] = d.GetValueOrDefault(c) + 1;
                        }
                    static string T(Dictionary<string, int> d) => string.Join(" ", d.OrderByDescending(kv => kv.Value).Take(2).Select(kv => $"{kv.Key} x{kv.Value}"));
                    sb.AppendLine($"{p.NorthDy,8}  {(p.Bars ? "yes" : "no"),4}  {T(even),-32} {T(odd),-32} | {T(inner)}");
                }
                continue;
            }
            sb.AppendLine("depth  even (x+z)                          odd (x+z)");
            foreach (var p in item.Pools!)
            {
                var even = new Dictionary<string, int>(); var odd = new Dictionary<string, int>();
                for (int z = p.Z + 1; z < p.Z + p.Size - 1; z++)
                    for (int x = p.X + 1; x < p.X + p.Size - 1; x++)
                    {
                        int i = (z * 128 + x) * 4;
                        string c = map.Pixels[i + 3] == 0 ? "(unexplored)" : $"#{map.Pixels[i]:X2}{map.Pixels[i + 1]:X2}{map.Pixels[i + 2]:X2}";
                        var d = ((x + z) & 1) == 0 ? even : odd;
                        d[c] = d.GetValueOrDefault(c) + 1;
                    }
                static string Top(Dictionary<string, int> d) => string.Join(" ", d.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} x{kv.Value}"));
                sb.AppendLine($"{p.Depth,5}  {Top(even),-36} {Top(odd)}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// What the game drew for each discovery sample (the lower two rows of its 3 x 3 patch, whose north neighbour is the
    /// same block, so the normal shade), classified as a known colour set, the cobblestone floor (block not drawn or
    /// popped off) or a NEW colour. New colours come first.
    /// </summary>
    public static string DiscoveryReport(string kitDir, IReadOnlyList<Bedrock.MapRecord> maps, ColorProfile profile)
    {
        var m = KitManifest.Load(Path.Combine(kitDir, "kit.json"));
        var normals = new Dictionary<string, string>();
        foreach (var set in profile.ColourSets)
            foreach (var b in set.Blocks)
                normals.TryAdd(profile.ColorOf(b, Shade.Normal).ToHex(), $"{set.Id} {set.NameEn}");
        var floorShades = new[] { Shade.Dark, Shade.Normal, Shade.Bright }.Select(s => profile.ColorOf(profile.Find("cobblestone")!, s).ToHex()).ToHashSet();
        var rows = new List<(int Order, string Line)>();
        foreach (var item in m.Items.Where(i => i.Discovery != null))
        {
            var map = maps.Where(r => r.Scale == 0 && r.Dimension == 0 && r.XCenter == 128 * item.MapX && r.ZCenter == 128 * item.MapZ)
                .Select(r => r.ToImage()).FirstOrDefault(i => i != null);
            if (map is null) return $"{item.Id}: no map";
            foreach (var smp in item.Discovery!)
            {
                var counts = new Dictionary<string, int>();
                for (int dz = 1; dz < 3; dz++)
                    for (int dx = 0; dx < 3; dx++)
                    {
                        int i = ((smp.Z + dz) * 128 + smp.X + dx) * 4;
                        string c = map.Pixels[i + 3] == 0 ? "(unexplored)" : $"#{map.Pixels[i]:X2}{map.Pixels[i + 1]:X2}{map.Pixels[i + 2]:X2}";
                        counts[c] = counts.GetValueOrDefault(c) + 1;
                    }
                var top = counts.OrderByDescending(kv => kv.Value).First();
                string kind = top.Key == "(unexplored)" ? "unexplored"
                    : floorShades.Contains(top.Key) ? "floor (not drawn / popped off)"
                    : normals.TryGetValue(top.Key, out var set) ? $"set {set}" : "NEW";
                if (kind == "NEW")
                {
                    // Base colour estimate from the normal shade (x220 / 255, rounded down): the smallest base giving it.
                    var c = Rgba.FromHex(top.Key);
                    int B(int v) => (int)Math.Ceiling(v * 255 / 220.0);
                    kind = $"NEW  (base about #{B(c.R):X2}{B(c.G):X2}{B(c.B):X2})";
                }
                rows.Add((kind.StartsWith("NEW") ? 0 : kind.StartsWith("floor") ? 2 : 1,
                    $"{smp.Block,-44} {top.Key} ({top.Value}/6)  {kind}{(counts.Count > 1 ? $"  [{string.Join(" ", counts.Select(kv => $"{kv.Key} x{kv.Value}"))}]" : "")}"));
            }
        }
        var sb = new StringBuilder($"{rows.Count(r => r.Order == 0)} new colours, {rows.Count(r => r.Order == 1)} known, {rows.Count(r => r.Order == 2)} not drawn\n");
        foreach (var r in rows.OrderBy(r => r.Order)) sb.AppendLine(r.Line);
        return sb.ToString();
    }

    /// <summary>
    /// A test card: hue sweeps at several lightness levels, pastels, a grey ramp, skin tones, saturated patches and thin
    /// lines — the kinds of content map art has to handle.
    /// </summary>
    public static RgbaImage TestCard(int w, int h)
    {
        var img = new RgbaImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                double u = x / (double)(w - 1), v = y / (double)(h - 1);
                (double r, double g, double b) c;
                if (v < 0.5)
                {
                    // Hue across, lightness down (light to dark).
                    double hue = u * 360, light = 0.85 - v * 1.3;
                    c = Hsl(hue, 0.75, Math.Clamp(light, 0.15, 0.85));
                }
                else if (v < 0.625) c = Hsl(u * 360, 0.6, 0.88);       // pastels
                else if (v < 0.75) c = (u, u, u);                        // grey ramp
                else if (v < 0.875)
                {
                    double[][] skin = [[0.98, 0.87, 0.80], [0.93, 0.76, 0.64], [0.80, 0.58, 0.45], [0.55, 0.38, 0.28], [0.98, 0.85, 0.86], [0.89, 0.89, 0.96]];
                    var s = skin[Math.Min(skin.Length - 1, (int)(u * skin.Length))];
                    c = (s[0], s[1], s[2]);
                }
                else
                {
                    double[][] sat = [[1, 0, 0], [1, 0.5, 0], [1, 1, 0], [0, 0.8, 0], [0, 0.7, 1], [0, 0, 1], [0.6, 0, 0.9], [1, 0.3, 0.7]];
                    var s = sat[Math.Min(sat.Length - 1, (int)(u * sat.Length))];
                    c = (s[0], s[1], s[2]);
                }
                // Thin dark lines (line art) every 32 px in the upper half.
                if (v < 0.5 && (x % 32 == 16 || y % 32 == 16)) c = (0.1, 0.1, 0.12);
                int i = (y * w + x) * 4;
                img.Pixels[i] = (byte)Math.Round(c.r * 255);
                img.Pixels[i + 1] = (byte)Math.Round(c.g * 255);
                img.Pixels[i + 2] = (byte)Math.Round(c.b * 255);
                img.Pixels[i + 3] = 255;
            }
        return img;
    }

    private static (double, double, double) Hsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60 % 6, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
        var (r, g, b) = hp switch { < 1 => (c, x, 0.0), < 2 => (x, c, 0.0), < 3 => (0.0, c, x), < 4 => (0.0, x, c), < 5 => (x, 0.0, c), _ => (c, 0.0, x) };
        return (r + m, g + m, b + m);
    }

    /// <summary>
    /// Compares the game's maps with the kit's predictions: per map area the scale-0 overworld map centred on it (the
    /// best one if there are several), pixel by pixel. Unexplored pixels (transparent) are counted, not compared.
    /// </summary>
    public static (List<TileCheck> Tiles, Dictionary<string, RgbaImage> Diffs) Verify(string kitDir, IReadOnlyList<Bedrock.MapRecord> maps)
    {
        var m = KitManifest.Load(Path.Combine(kitDir, "kit.json"));
        var tiles = new List<TileCheck>();
        var diffs = new Dictionary<string, RgbaImage>();
        foreach (var item in m.Items)
        {
            var predicted = ImageCodec.Load(Path.Combine(kitDir, item.Predicted));
            var diff = new RgbaImage(predicted.Width, predicted.Height);
            for (int ty = 0; ty < item.MapsY; ty++)
                for (int tx = 0; tx < item.MapsX; tx++)
                {
                    int cx = 128 * (item.MapX + tx), cz = 128 * (item.MapZ + ty);
                    var candidates = maps.Where(r => r.Scale == 0 && r.Dimension == 0 && r.XCenter == cx && r.ZCenter == cz && r.Width == 128 && r.Height == 128)
                        .Select(r => (Map: r, Img: r.ToImage())).Where(c => c.Img != null).ToList();
                    if (candidates.Count == 0) { tiles.Add(new TileCheck(item.Id, item.MapX + tx, item.MapZ + ty, null, 0, 0, 0, 128 * 128)); continue; }
                    TileCheck? best = null;
                    RgbaImage? bestImg = null;
                    foreach (var (map, img) in candidates)
                    {
                        int compared = 0, exact = 0, unexplored = 0;
                        for (int py = 0; py < 128; py++)
                            for (int px = 0; px < 128; px++)
                            {
                                int pi = ((ty * 128 + py) * predicted.Width + tx * 128 + px) * 4, mi = (py * 128 + px) * 4;
                                if (predicted.Pixels[pi + 3] == 0) continue;
                                if (img!.Pixels[mi + 3] == 0) { unexplored++; continue; }
                                compared++;
                                if (img.Pixels[mi] == predicted.Pixels[pi] && img.Pixels[mi + 1] == predicted.Pixels[pi + 1] && img.Pixels[mi + 2] == predicted.Pixels[pi + 2]) exact++;
                            }
                        var check = new TileCheck(item.Id, item.MapX + tx, item.MapZ + ty, map.Id, compared, exact, unexplored, 0);
                        if (best is null || check.Exact > best.Exact) { best = check; bestImg = img; }
                    }
                    tiles.Add(best!);
                    // Difference image: matches greyed, mismatches in the game's colour with a red frame, unexplored blue.
                    for (int py = 0; py < 128; py++)
                        for (int px = 0; px < 128; px++)
                        {
                            int pi = ((ty * 128 + py) * predicted.Width + tx * 128 + px) * 4, mi = (py * 128 + px) * 4;
                            var g = bestImg!.Pixels;
                            bool same = g[mi] == predicted.Pixels[pi] && g[mi + 1] == predicted.Pixels[pi + 1] && g[mi + 2] == predicted.Pixels[pi + 2];
                            byte grey = (byte)((predicted.Pixels[pi] + predicted.Pixels[pi + 1] + predicted.Pixels[pi + 2]) / 6 + 80);
                            (byte r, byte gg, byte b) c = g[mi + 3] == 0 ? ((byte)40, (byte)60, (byte)200) : same ? (grey, grey, grey) : ((byte)255, (byte)0, (byte)0);
                            diff.Pixels[pi] = c.r; diff.Pixels[pi + 1] = c.gg; diff.Pixels[pi + 2] = c.b; diff.Pixels[pi + 3] = 255;
                        }
                }
            diffs[item.Id] = diff;
        }
        return (tiles, diffs);
    }
}
