// Generates palettes/bedrock-full-v2.json from blocks.txt.
//
//   dotnet run --project tools/PaletteGen -- <mojang-blocks.json> [output.json]
//
// mojang-blocks.json comes from Mojang's bedrock-samples repository
// (metadata/vanilladata_modules/mojang-blocks.json). It is only read here to validate identifiers and to fill
// in every block state with its default value; nothing from it is copied into the repository.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: PaletteGen <mojang-blocks.json> [output.json]");
    return 1;
}

string root = FindRepoRoot();
string output = args.Length > 1 ? args[1] : Path.Combine(root, "palettes", "bedrock-full-v2.json");
const int BlockVersion = 18168865; // the block palette version the 1.26 game writes into structures

var mojang = JsonNode.Parse(File.ReadAllText(args[0]))!;
var propertyValues = new Dictionary<string, (string Type, JsonArray Values)>();
foreach (var p in mojang["block_properties"]!.AsArray())
    propertyValues[p!["name"]!.GetValue<string>()] = (p["type"]!.GetValue<string>(), p["values"]!.AsArray());
var blockProps = new Dictionary<string, List<string>>();
foreach (var b in mojang["data_items"]!.AsArray())
    blockProps[b!["name"]!.GetValue<string>()] = b["properties"]?.AsArray().Select(p => p!["name"]!.GetValue<string>()).ToList() ?? [];

string[] dyes = ["white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black"];
var dyeJa = new Dictionary<string, string>
{
    ["white"] = "白色", ["orange"] = "橙色", ["magenta"] = "赤紫色", ["light_blue"] = "空色", ["yellow"] = "黄色", ["lime"] = "黄緑色",
    ["pink"] = "桃色", ["gray"] = "灰色", ["light_gray"] = "薄灰色", ["cyan"] = "青緑色", ["purple"] = "紫色", ["blue"] = "青色",
    ["brown"] = "茶色", ["green"] = "緑色", ["red"] = "赤色", ["black"] = "黒色",
};
string En(string dye) => string.Join(' ', dye.Split('_').Select(w => char.ToUpper(w[0]) + w[1..]));

var sets = new JsonArray();
JsonObject? current = null;
string[]? currentShades = null;
var errors = new List<string>();
var keys = new HashSet<string>();

void AddBlock(string key, string name, string stateText, string flags, string en, string ja)
{
    if (current is null) throw new InvalidDataException($"Block {key} outside a colour set");
    if (!keys.Add(key)) errors.Add($"duplicate key {key}");
    if (!blockProps.TryGetValue(name, out var props)) { errors.Add($"{key}: unknown Bedrock block {name}"); props = []; }

    var overrides = new Dictionary<string, string>();
    foreach (var kv in stateText.Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = kv.Split('=', 2);
        overrides[parts[0]] = parts[1];
    }
    var states = new JsonObject();
    foreach (var prop in props)
    {
        if (!propertyValues.TryGetValue(prop, out var def)) { errors.Add($"{key}: unknown property {prop}"); continue; }
        string? raw = overrides.TryGetValue(prop, out var o) ? o : null;
        overrides.Remove(prop);
        JsonNode value = def.Type switch
        {
            "bool" => JsonValue.Create(raw is null ? def.Values[0]!["value"]!.GetValue<bool>() : bool.Parse(raw))!,
            "int" => JsonValue.Create(raw is null ? def.Values[0]!["value"]!.GetValue<int>() : int.Parse(raw))!,
            _ => JsonValue.Create(raw ?? def.Values[0]!["value"]!.GetValue<string>())!,
        };
        if (raw != null && !def.Values.Any(v => v!["value"]!.ToJsonString().Trim('"') == raw))
            errors.Add($"{key}: value {raw} not valid for {prop}");
        states[prop] = value;
    }
    foreach (var leftover in overrides.Keys) errors.Add($"{key}: {name} has no property {leftover}");

    var block = new JsonObject
    {
        ["key"] = key,
        ["displayName"] = ja,
        ["displayNameEn"] = en,
        ["name"] = name,
        ["states"] = states,
        ["version"] = BlockVersion,
    };
    if (flags.Contains('S')) block["needsSupport"] = true;
    if (flags.Contains('G')) block["gravity"] = true;
    if (flags.Contains('F')) block["flammable"] = true;
    if (flags.Contains('T')) block["biomeTinted"] = true;
    if (flags.Contains('X')) block["defaultEnabled"] = false;
    var rarity = flags.FirstOrDefault(char.IsDigit);
    if (rarity != default) block["rarity"] = rarity - '0';
    if (currentShades is { Length: 3 })
        block["measured"] = new JsonObject { ["Dark"] = currentShades[0], ["Normal"] = currentShades[1], ["Bright"] = currentShades[2] };
    current["blocks"]!.AsArray().Add(block);
}

foreach (var rawLine in File.ReadAllLines(Path.Combine(root, "tools", "PaletteGen", "blocks.txt")))
{
    var line = rawLine.Trim();
    if (line.Length == 0 || line.StartsWith('#')) continue;
    var f = line.Split('|');
    switch (f[0])
    {
        case "CS":
            var rgb = f[4].Split(',').Select(byte.Parse).ToArray();
            current = new JsonObject
            {
                ["id"] = int.Parse(f[1]),
                ["nameEn"] = f[2],
                ["name"] = f[3],
                ["mapColor"] = $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}",
                ["blocks"] = new JsonArray(),
            };
            // Optional fields:
            //   seen=d,n,b  pixels of base x180 / x220 / x255 found on maps drawn by Bedrock (BMAP mapcheck)
            //   gd=r,g,b    normal colour in gd-codes/mc-pixelart-maker (Bedrock, experimental, third-party)
            //   pad=r,g,b@version  base colour measured with the BMAP colour pad in game: every block of the set,
            //                      bright / normal / dark, matched (base x 255 / 220 / 180, rounded down)
            // Priority of evidence: colour pad > seen on Bedrock maps in 2+ shades > gd-codes (Bedrock) > Java value.
            var notes = new List<string>();
            string status = "estimated";
            string? pad = f.Skip(5).FirstOrDefault(e => e.StartsWith("pad="));
            // shades=#dark,#normal,#bright: colours measured per shade that do not follow base x multiplier with an
            // integer base (spruce leaves: a tinted colour the game keeps unrounded). Written to every block of the set.
            currentShades = f.Skip(5).FirstOrDefault(e => e.StartsWith("shades="))?[7..].Split(',');
            if (currentShades is not null) notes.Add($"Shades measured separately: {string.Join(" / ", currentShades)} (dark / normal / bright)");
            foreach (var extra in f.Skip(5).Where(e => !e.StartsWith("pad=") && !e.StartsWith("shades=")))
            {
                if (extra.StartsWith("gd="))
                {
                    var g = extra[3..].Split(',').Select(int.Parse).ToArray();
                    int N(int c) => c * 220 / 255;
                    if (N(rgb[0]) != g[0] || N(rgb[1]) != g[1] || N(rgb[2]) != g[2])
                    {
                        notes.Add($"Java base #{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2} (normal #{N(rgb[0]):X2}{N(rgb[1]):X2}{N(rgb[2]):X2}) replaced by gd-codes");
                        rgb = g.Select(n => (byte)Inverse(n)).ToArray();
                        current["mapColor"] = $"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2}";
                    }
                    notes.Add($"gd-codes/mc-pixelart-maker (MIT, Bedrock, determined experimentally) normal #{g[0]:X2}{g[1]:X2}{g[2]:X2}");
                    status = "thirdParty";
                }
                else if (extra.StartsWith("seen="))
                {
                    var n = extra[5..].Split(',').Select(int.Parse).ToArray();
                    bool verified = n.Count(c => c >= 3) >= 2;
                    if (verified) status = "verified";
                    notes.Add($"Seen on maps drawn by Bedrock 1.21.132-1.26.51: x180 {n[0]}, x220 {n[1]}, x255 {n[2]} pixels" +
                        (verified ? "" : " (one shade only: not enough to verify)"));
                }
            }
            if (pad is not null)
            {
                var parts = pad[4..].Split('@');
                var m = parts[0].Split(',').Select(byte.Parse).ToArray();
                string version = parts.Length > 1 ? parts[1] : "?";
                if (m[0] != rgb[0] || m[1] != rgb[1] || m[2] != rgb[2])
                    notes.Add($"#{rgb[0]:X2}{rgb[1]:X2}{rgb[2]:X2} replaced by the measured colour");
                rgb = m;
                current["mapColor"] = $"#{m[0]:X2}{m[1]:X2}{m[2]:X2}";
                notes.Insert(0, $"Measured with the BMAP colour pad on Bedrock {version}: all blocks of the set, three shades");
                status = "verified";
            }
            if (notes.Count > 0)
            {
                current["status"] = status;
                current["statusNote"] = string.Join("; ", notes);
            }
            sets.Add(current);
            break;
        case "B":
            AddBlock(f[1], f[2], f[3], f[4], f[5], f[6]);
            break;
        case "@DYE":
        {
            string d = f[1], j = dyeJa[d], e = En(d);
            AddBlock($"{d}_wool", $"minecraft:{d}_wool", "", "F", $"{e} Wool", $"{j}の羊毛");
            AddBlock($"{d}_carpet", $"minecraft:{d}_carpet", "", "SF", $"{e} Carpet", $"{j}のカーペット");
            AddBlock($"{d}_stained_glass", $"minecraft:{d}_stained_glass", "", "S", $"{e} Stained Glass", $"{j}の色付きガラス");
            AddBlock($"{d}_concrete", $"minecraft:{d}_concrete", "", "", $"{e} Concrete", $"{j}のコンクリート");
            AddBlock($"{d}_concrete_powder", $"minecraft:{d}_concrete_powder", "", "SG", $"{e} Concrete Powder", $"{j}のコンクリートパウダー");
            string glazed = d == "light_gray" ? "silver" : d;
            AddBlock($"{d}_glazed_terracotta", $"minecraft:{glazed}_glazed_terracotta", "facing_direction=2", "", $"{e} Glazed Terracotta", $"{j}の彩釉テラコッタ");
            break;
        }
        case "@TERRACOTTA":
        {
            string d = f[1];
            AddBlock($"{d}_terracotta", $"minecraft:{d}_terracotta", "", "", $"{En(d)} Terracotta", $"{dyeJa[d]}のテラコッタ");
            break;
        }
        default:
            errors.Add($"bad line: {line}");
            break;
    }
}

if (errors.Count > 0)
{
    foreach (var e in errors) Console.Error.WriteLine("ERROR " + e);
    return 2;
}

var profile = new JsonObject
{
    ["formatVersion"] = 2,
    ["id"] = "bedrock-full-v2",
    ["name"] = "Bedrock 全ブロック / All blocks（1.26.51 で実測 / measured on 1.26.51）",
    ["gameVersion"] = "1.26",
    ["updated"] = DateTime.Now.ToString("yyyy-MM-dd"),
    ["notes"] = "全 79 色セットの基本色と、各ブロックがどの色セットに描かれるかを Bedrock 1.26.51 の色テストパッド（全ブロック・明 / 通常 / 暗）で実測しました。陰影の倍率 180/220/255・丸め方・段差の規則も同じ版の実機で確認済み（段差 ±1 は座標の偶奇で交互、±2 以上は常に）。地図に描かれないブロック（鉄格子・鍾乳石・ろうそく）と TNT は除いています。草ブロックと一部の葉はバイオームで色が変わるため既定では使いません。ブロック識別子と状態は Bedrock の公式メタデータで検証済み。",
    ["shades"] = new JsonObject
    {
        ["dark"] = 180, ["normal"] = 220, ["bright"] = 255, ["verified"] = true, ["status"] = "verified",
        ["statusNote"] = "Multipliers seen exactly (base x180 / x220 / x255, rounded down) on maps drawn by Bedrock 1.21.132-1.26.51. " +
            "Step rule measured with the BMAP shading pad on Bedrock 1.26.51 (2026-10-18, 8032 samples, 100 % exact): " +
            "a step of +1 is light only where x + z is odd, -1 is dark only where x + z is even; steps of 2 or more always; carpets and slabs alike; east/west steps have no effect.",
        ["minStep"] = 2, ["brightStep"] = 2, ["darkStep"] = 2, ["model"] = "checker", ["rounding"] = "fromBase", ["modelId"] = "parity",
        ["stepStatus"] = "verified",
    },
    ["colourSets"] = sets,
};
File.WriteAllText(output, profile.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
}) + "\n", new UTF8Encoding(false));
Console.WriteLine($"{sets.Count} colour sets, {keys.Count} blocks -> {output}");
return 0;

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BMAP.sln"))) dir = dir.Parent;
    return dir?.FullName ?? throw new DirectoryNotFoundException("BMAP.sln not found");
}

/// <summary>Smallest base value whose normal shade (x 220 / 255, rounded down) is n.</summary>
static int Inverse(int n)
{
    for (int b = 0; b <= 255; b++) if (b * 220 / 255 == n) return b;
    throw new ArgumentException($"no base colour gives normal {n}");
}
