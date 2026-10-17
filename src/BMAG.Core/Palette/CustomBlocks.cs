using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BMAG.Palette;

/// <summary>Stores user-defined blocks and reads <c>minecraft:map_color</c> from behavior packs (F24, design 3.4).</summary>
public static class CustomBlockStore
{
    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BMAG", "custom-blocks.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<CustomBlock> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return JsonSerializer.Deserialize<List<CustomBlock>>(File.ReadAllText(FilePath), Options) ?? [];
        }
        catch { return []; }
    }

    public static void Save(IEnumerable<CustomBlock> blocks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(blocks.ToList(), Options));
    }

    /// <summary>
    /// Scans a behavior pack folder (or .mcpack / .mcaddon / .zip) for custom blocks that declare
    /// <c>minecraft:map_color</c>. Accepted forms: "#RRGGBB", [r, g, b] and { "color": ..., "tint_method": ... }.
    /// </summary>
    public static List<CustomBlock> ScanBehaviorPack(string path, List<string>? warnings = null)
    {
        var result = new List<CustomBlock>();
        foreach (var (name, json) in EnumerateJson(path))
        {
            if (!name.Replace('\\', '/').Contains("blocks/", StringComparison.OrdinalIgnoreCase)) continue;
            JsonNode? root;
            try { root = JsonNode.Parse(StripComments(json)); }
            catch (Exception ex) { warnings?.Add($"{name}: JSON を読めません ({ex.Message})"); continue; }
            var block = root?["minecraft:block"];
            string? id = block?["description"]?["identifier"]?.GetValue<string>();
            var mapColor = block?["components"]?["minecraft:map_color"];
            if (id is null) continue;
            if (mapColor is null) { warnings?.Add($"{id}: minecraft:map_color がないため地図には映りません"); continue; }
            string? hex = ParseColor(mapColor is JsonObject o ? o["color"] : mapColor);
            if (hex is null) { warnings?.Add($"{id}: map_color の形式を解釈できません"); continue; }
            if (mapColor is JsonObject tinted && tinted["tint_method"] is { } tm && tm.GetValue<string>() != "none")
                warnings?.Add($"{id}: tint_method={tm} のためバイオームで色が変わります");
            result.Add(new CustomBlock
            {
                Key = "custom:" + id, Name = id, DisplayName = id, MapColor = hex, Source = Path.GetFileName(path),
            });
        }
        return result;
    }

    public static string? ParseColor(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                s = s.Trim().TrimStart('#');
                return s.Length >= 6 && int.TryParse(s[..6], NumberStyles.HexNumber, null, out _) ? "#" + s[..6].ToUpperInvariant() : null;
            case JsonArray a when a.Count >= 3:
                var vals = a.Take(3).Select(x => x!.GetValue<double>()).ToArray();
                if (vals.All(x => x <= 1.0)) vals = vals.Select(x => x * 255).ToArray(); // 0..1 floats
                return "#" + string.Concat(vals.Select(x => ((int)Math.Round(Math.Clamp(x, 0, 255))).ToString("X2")));
            default:
                return null;
        }
    }

    private static IEnumerable<(string Name, string Json)> EnumerateJson(string path)
    {
        if (Directory.Exists(path))
        {
            foreach (var f in Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories))
                yield return (Path.GetRelativePath(path, f), File.ReadAllText(f));
            yield break;
        }
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var r = new StreamReader(e.Open());
            yield return (e.FullName, r.ReadToEnd());
        }
    }

    /// <summary>Bedrock JSON files may contain // and /* */ comments.</summary>
    public static string StripComments(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; sb.Append(c); continue; }
            if (c == '/' && i + 1 < json.Length && json[i + 1] == '/')
            {
                while (i < json.Length && json[i] != '\n') i++;
                sb.Append('\n');
                continue;
            }
            if (c == '/' && i + 1 < json.Length && json[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < json.Length && !(json[i] == '*' && json[i + 1] == '/')) i++;
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
