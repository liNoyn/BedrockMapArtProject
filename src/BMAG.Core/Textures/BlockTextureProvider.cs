using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BMAG.Imaging;
using BMAG.Palette;

namespace BMAG.Textures;

/// <summary>
/// Block icons from Minecraft's own textures. Textures are never shipped with BMAG: they are either downloaded on
/// request from Mojang's official bedrock-samples repository (subject to the Minecraft EULA) into a local cache, or
/// read from a resource pack the user points to. Without textures, callers fall back to colour swatches.
/// </summary>
public sealed class BlockTextureProvider
{
    public const string SamplesBase = "https://raw.githubusercontent.com/Mojang/bedrock-samples/main/resource_pack/";

    /// <summary>blocks.json keys that differ from the current block identifiers.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["grass_block"] = "grass",
        ["sea_lantern"] = "seaLantern",
    };

    private readonly ConcurrentDictionary<string, RgbaImage?> _icons = new();
    private JsonObject? _blocks;
    private JsonObject? _terrain;

    public BlockTextureProvider(string? cacheDirectory = null)
    {
        CacheDirectory = cacheDirectory ?? Environment.GetEnvironmentVariable("BMAG_TEXTURE_CACHE") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BMAG", "textures", "vanilla");
        // Textures copied next to the executable at build time (see BMAG.App.csproj) are used when present.
        if (cacheDirectory is null)
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "textures", "vanilla");
            if (File.Exists(Path.Combine(bundled, "blocks.json"))) BundledDirectory = bundled;
        }
        LoadIndex();
    }

    /// <summary>Folder holding blocks.json, textures/terrain_texture.json and textures/blocks/* (downloaded copy).</summary>
    public string CacheDirectory { get; }
    /// <summary>Extra resource pack (folder) searched before the cache.</summary>
    public string? ResourcePack { get; private set; }
    public bool IsAvailable => _blocks != null && _terrain != null;
    /// <summary>Textures shipped next to BMAG.exe (fetched by the build), or null.</summary>
    public string? BundledDirectory { get; }

    public event Action? Changed;

    private void LoadIndex()
    {
        _blocks = ReadJson("blocks.json");
        _terrain = ReadJson(Path.Combine("textures", "terrain_texture.json"));
        _icons.Clear();
    }

    private JsonObject? ReadJson(string relative)
    {
        foreach (var root in Roots())
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            try { return JsonNode.Parse(CustomBlockStore.StripComments(File.ReadAllText(path))) as JsonObject; }
            catch { /* try the next root */ }
        }
        return null;
    }

    private IEnumerable<string> Roots()
    {
        if (ResourcePack != null) yield return ResourcePack;
        if (BundledDirectory != null) yield return BundledDirectory;
        yield return CacheDirectory;
    }

    /// <summary>Uses a resource pack folder (e.g. an extracted vanilla pack or a texture pack) for icons.</summary>
    public void UseResourcePack(string? folder)
    {
        if (folder != null && !Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        ResourcePack = folder;
        LoadIndex();
        Changed?.Invoke();
    }

    /// <summary>
    /// Downloads blocks.json, terrain_texture.json and the textures needed for <paramref name="blocks"/> from
    /// Mojang's bedrock-samples repository. Returns the number of textures downloaded.
    /// </summary>
    public async Task<int> DownloadAsync(IEnumerable<BlockDefinition> blocks, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BMAG/0.2 (+https://github.com/liNoyn/BedrockMapArtGenerator)");
        Directory.CreateDirectory(Path.Combine(CacheDirectory, "textures", "blocks"));

        async Task Fetch(string relative)
        {
            var bytes = await http.GetByteArrayAsync(SamplesBase + relative.Replace('\\', '/'), ct);
            var target = Path.Combine(CacheDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, bytes, ct);
        }

        progress?.Report("ブロック定義を取得しています…");
        await Fetch("blocks.json");
        await Fetch("textures/terrain_texture.json");
        var saved = ResourcePack;
        ResourcePack = null;
        LoadIndex();

        var paths = blocks.Select(ResolveTexturePath).Where(p => p != null).Select(p => p!.Value.Path).Distinct().ToList();
        int done = 0, ok = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (FindFile(path) != null) { ok++; continue; }
            foreach (var ext in new[] { ".png", ".tga" })
            {
                try
                {
                    await Fetch(path + ext);
                    ok++;
                    break;
                }
                catch (HttpRequestException) { /* try the other extension */ }
            }
            if (done % 10 == 0) progress?.Report($"テクスチャを取得しています… {done}/{paths.Count}");
        }
        ResourcePack = saved;
        LoadIndex();
        Changed?.Invoke();
        return ok;
    }

    /// <summary>A square icon (normally 16x16) for the block, or null when no texture is available.</summary>
    public RgbaImage? GetIcon(BlockDefinition block) => _icons.GetOrAdd(block.Key, _ => LoadIcon(block));

    private RgbaImage? LoadIcon(BlockDefinition block)
    {
        if (!IsAvailable) return null;
        var resolved = ResolveTexturePath(block);
        if (resolved is not { } r) return null;
        var file = FindFile(r.Path);
        if (file is null) return null;
        try
        {
            var img = file.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                ? TgaDecoder.Decode(File.ReadAllBytes(file))
                : ImageCodec.Load(file);
            // Animated textures are vertical strips of frames: keep the first square.
            if (img.Height > img.Width && img.Height % img.Width == 0) img = img.Crop(0, 0, img.Width, img.Width);
            if (r.Tint is { } overlay) Overlay(img, overlay);
            else if (block.BiomeTinted || IsFoliage(block)) Tint(img, DefaultTint(block));
            Shape(img, block);
            return img;
        }
        catch { return null; }
    }

    private string? FindFile(string relative)
    {
        foreach (var root in Roots())
            foreach (var ext in new[] { ".png", ".tga" })
            {
                var p = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar) + ext);
                if (File.Exists(p)) return p;
            }
        return null;
    }

    /// <summary>blocks.json → terrain_texture.json → texture path (without extension) and optional tint.</summary>
    public (string Path, Rgba? Tint)? ResolveTexturePath(BlockDefinition block)
    {
        if (_blocks is null || _terrain is null) return null;
        string id = block.Name.Contains(':') ? block.Name[(block.Name.IndexOf(':') + 1)..] : block.Name;
        var entry = _blocks[id] ?? _blocks[block.Name] ?? (Aliases.TryGetValue(id, out var alias) ? _blocks[alias] : null);
        if (entry is null) return null;
        // Prefer the inventory ("carried") look, then the side face (what MapartCraft shows), then any face.
        var tex = entry["carried_textures"] ?? entry["textures"];
        string? key = tex switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonObject o => (o["side"] ?? o["south"] ?? o["north"] ?? o["up"] ?? o.Select(kv => kv.Value).FirstOrDefault())?.GetValue<string>(),
            _ => null,
        };
        if (key is null) return null;
        var data = _terrain["texture_data"]?[key]?["textures"];
        return data switch
        {
            JsonValue v => (v.GetValue<string>(), null),
            JsonArray a when a.Count > 0 => a[0] switch
            {
                JsonValue v0 => (v0.GetValue<string>(), null),
                JsonObject o0 => (o0["path"]!.GetValue<string>(), ParseTint(o0)),
                _ => null,
            },
            JsonObject o => (o["path"]!.GetValue<string>(), ParseTint(o)),
            _ => null,
        };
    }

    private static Rgba? ParseTint(JsonObject o)
    {
        var c = o["overlay_color"] ?? o["tint_color"];
        try { return c is null ? null : Rgba.FromHex(c.GetValue<string>()); }
        catch { return null; }
    }

    private static bool IsFoliage(BlockDefinition b) =>
        b.Name.EndsWith("_leaves") && !b.Name.Contains("cherry") && !b.Name.Contains("azalea");

    private static Rgba DefaultTint(BlockDefinition b) => b.Name switch
    {
        "minecraft:spruce_leaves" => new Rgba(0x61, 0x99, 0x61),
        "minecraft:birch_leaves" => new Rgba(0x80, 0xA7, 0x55),
        _ when b.Name.EndsWith("_leaves") => new Rgba(0x48, 0xB5, 0x18),
        _ => new Rgba(0x79, 0xC0, 0x5A),
    };

    /// <summary>
    /// Bedrock "overlay_color" textures keep the tinted layer in the alpha channel: alpha 255 = tinted overlay,
    /// alpha 0 = base texture. The result is fully opaque.
    /// </summary>
    private static void Overlay(RgbaImage img, Rgba t)
    {
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            p[i] = (byte)((p[i] * t.R / 255 * a + p[i] * (255 - a)) / 255);
            p[i + 1] = (byte)((p[i + 1] * t.G / 255 * a + p[i + 1] * (255 - a)) / 255);
            p[i + 2] = (byte)((p[i + 2] * t.B / 255 * a + p[i + 2] * (255 - a)) / 255);
            p[i + 3] = 255;
        }
    }

    /// <summary>Slabs show their lower half and thin blocks (carpets, pressure plates, snow layers) a thin strip, like MapartCraft.</summary>
    private static void Shape(RgbaImage img, BlockDefinition b)
    {
        int keep = b.Name.EndsWith("_slab") ? img.Height / 2
            : b.Name.EndsWith("_carpet") || b.Name.EndsWith("_pressure_plate") || b.Name == "minecraft:snow_layer" ? Math.Max(1, img.Height / 8)
            : b.Name.EndsWith("_trapdoor") ? Math.Max(1, img.Height * 3 / 16)
            : img.Height;
        for (int y = 0; y < img.Height - keep; y++)
            for (int x = 0; x < img.Width; x++)
                img.Pixels[(y * img.Width + x) * 4 + 3] = 0;
    }

    /// <summary>Multiplies the grey parts of a texture with the tint (coloured parts, e.g. grass-side dirt, stay).</summary>
    private static void Tint(RgbaImage img, Rgba t)
    {
        var p = img.Pixels;
        for (int i = 0; i < p.Length; i += 4)
        {
            int r = p[i], g = p[i + 1], b = p[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max - min > 24) continue; // already coloured
            p[i] = (byte)(r * t.R / 255);
            p[i + 1] = (byte)(g * t.G / 255);
            p[i + 2] = (byte)(b * t.B / 255);
        }
    }
}
