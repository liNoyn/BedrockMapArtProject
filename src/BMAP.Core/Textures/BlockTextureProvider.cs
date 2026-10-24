using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Textures;

/// <summary>
/// Block icons from Minecraft's own textures. Assets are fetched at build time or on request from Mojang's official
/// bedrock-samples repository (subject to the Minecraft EULA), or read from a user resource pack. They are not stored
/// in this source repository. Without textures, callers fall back to colour swatches.
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
    private readonly ConcurrentDictionary<(string Key, int Face), RgbaImage?> _faces = new();
    private readonly ConcurrentDictionary<string, RgbaImage?> _raw = new();
    private JsonObject? _blocks;
    private JsonObject? _terrain;

    public BlockTextureProvider(string? cacheDirectory = null)
    {
        CacheDirectory = cacheDirectory ?? Environment.GetEnvironmentVariable("BMAP_TEXTURE_CACHE")
            ?? Environment.GetEnvironmentVariable("BMAG_TEXTURE_CACHE") // name before v0.4.0
            ?? Path.Combine(AppPaths.Local, "textures", "vanilla");
        // Textures copied next to the executable at build time (see BMAP.App.csproj) are used when present.
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
    /// <summary>Textures shipped next to BMAP.exe (fetched by the build), or null.</summary>
    public string? BundledDirectory { get; }

    public event Action? Changed;

    private void LoadIndex()
    {
        _blocks = ReadJson("blocks.json");
        _terrain = ReadJson(Path.Combine("textures", "terrain_texture.json"));
        _icons.Clear();
        _faces.Clear();
        _raw.Clear();
    }

    private JsonObject? ReadJson(string relative)
    {
        JsonObject? result = null;
        foreach (var root in Roots().Reverse())
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(CustomBlockStore.StripComments(File.ReadAllText(path))) is not JsonObject json) continue;
                result ??= new JsonObject();
                foreach (var (key, value) in json)
                {
                    if (key == "texture_data" && value is JsonObject data && result[key] is JsonObject existing)
                        foreach (var (texture, definition) in data) existing[texture] = definition?.DeepClone();
                    else result[key] = value?.DeepClone();
                }
            }
            catch { /* try the next root */ }
        }
        return result;
    }

    private IEnumerable<string> Roots()
    {
        if (ResourcePack != null) yield return ResourcePack;
        yield return CacheDirectory;
        if (BundledDirectory != null) yield return BundledDirectory;
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
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BMAP/0.2 (+https://github.com/liNoyn/BedrockMapArtProject)");
        return await DownloadAsync(blocks, http, progress, ct);
    }

    internal async Task<int> DownloadAsync(IEnumerable<BlockDefinition> blocks, HttpClient http,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var requested = blocks.ToList();
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.Combine(CacheDirectory, "textures", "blocks"));
        File.Delete(Path.Combine(CacheDirectory, ".complete"));

        async Task Fetch(string relative)
        {
            var target = SafePath(CacheDirectory, relative) ?? throw new InvalidDataException($"Invalid texture path: {relative}");
            var bytes = await http.GetByteArrayAsync(SamplesBase + relative.Replace('\\', '/'), ct);
            ct.ThrowIfCancellationRequested();
            AtomicFile.WriteAllBytes(target, bytes);
        }

        progress?.Report("ブロック定義を取得しています…");
        await Fetch("blocks.json");
        await Fetch("textures/terrain_texture.json");
        // Resolve vanilla downloads independently; cancellation must not switch off the user's resource pack.
        var vanilla = new BlockTextureProvider(CacheDirectory);

        var paths = requested.SelectMany(b => Enumerable.Range(-1, 7).Select(face => vanilla.ResolveTexturePath(b, face)))
            .Where(p => p != null).Select(p => p!.Value.Path).Distinct().ToList();
        int done = 0, ok = 0;
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            if (vanilla.ReadTexture(path) != null) { ok++; continue; }
            foreach (var relative in TextureFiles(path))
            {
                try
                {
                    await Fetch(relative);
                    vanilla._raw.TryRemove(path, out _);
                    if (vanilla.ReadTexture(path) is null) continue;
                    ok++;
                    break;
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { /* Try TGA. */ }
            }
            if (done % 10 == 0) progress?.Report($"テクスチャを取得しています… {done}/{paths.Count}");
        }
        LoadIndex();
        Changed?.Invoke();
        var coverage = await Task.Run(() => new BlockTextureProvider(CacheDirectory).CheckCoverage(requested), ct);
        if (coverage.IsComplete) AtomicFile.WriteAllText(Path.Combine(CacheDirectory, ".complete"), DateTime.UtcNow.ToString("O"));
        else progress?.Report($"テクスチャ未取得: {coverage.MissingBlocks.Count} ブロック（アイコン {coverage.MissingIcons} / 面 {coverage.MissingFaces}）");
        return ok;
    }

    /// <summary>Verifies decoded icons and all six world faces; metadata or a completion marker alone is insufficient.</summary>
    public TextureCoverage CheckCoverage(IEnumerable<BlockDefinition> blocks)
    {
        int total = 0, icons = 0, faces = 0;
        var missing = new List<string>();
        foreach (var block in blocks)
        {
            total++;
            bool iconMissing = GetIcon(block) is null;
            int faceMissing = Enumerable.Range(0, 6).Count(face => GetFaceTexture(block, face) is null);
            if (iconMissing) icons++;
            faces += faceMissing;
            if (iconMissing || faceMissing > 0) missing.Add(block.Key);
        }
        return new TextureCoverage(total, icons, faces, missing);
    }

    /// <summary>A square icon (normally 16x16) for the block, or null when no texture is available.</summary>
    public RgbaImage? GetIcon(BlockDefinition block) => _icons.GetOrAdd(block.Key, _ => LoadIcon(block));

    /// <summary>Unmasked face texture: 0 up, 1 north, 2 south, 3 west, 4 east, 5 down. Inventory icons stay separate.</summary>
    public RgbaImage? GetFaceTexture(BlockDefinition block, int face)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(face);
        if (face > 5) throw new ArgumentOutOfRangeException(nameof(face));
        return _faces.GetOrAdd((block.Key, face), _ => LoadIcon(block, face));
    }

    private RgbaImage? LoadIcon(BlockDefinition block, int face = -1)
    {
        if (!IsAvailable) return null;
        var resolved = ResolveTexturePath(block, face);
        if (resolved is not { } r) return null;
        try
        {
            var img = ReadTexture(r.Path)?.Clone();
            if (img is null) return null;
            // Animated textures are vertical strips of frames: keep the first square.
            if (img.Height > img.Width && img.Height % img.Width == 0) img = img.Crop(0, 0, img.Width, img.Width);
            if (r.Tint is { } overlay) Overlay(img, overlay);
            else if (block.BiomeTinted || IsFoliage(block)) Tint(img, DefaultTint(block));
            if (face < 0) Shape(img, block);
            return img;
        }
        catch { return null; }
    }

    private static IEnumerable<string> TextureFiles(string relative) =>
        Path.GetExtension(relative).ToLowerInvariant() is ".png" or ".tga" ? [relative] : [relative + ".png", relative + ".tga"];

    private static string? SafePath(string root, string relative)
    {
        string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        return path.StartsWith(basePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? path : null;
    }

    private RgbaImage? ReadTexture(string relative) => _raw.GetOrAdd(relative, _ =>
    {
        foreach (var root in Roots())
            foreach (var name in TextureFiles(relative))
            {
                try
                {
                    var path = SafePath(root, name);
                    if (path is null || !File.Exists(path)) continue;
                    return path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)
                        ? TgaDecoder.Decode(File.ReadAllBytes(path)) : ImageCodec.Load(path);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or OverflowException or IndexOutOfRangeException)
                { /* A corrupt higher-priority file must not hide a valid fallback. */ }
            }
        return null;
    });

    /// <summary>blocks.json → terrain_texture.json → texture path (without extension) and optional tint.</summary>
    public (string Path, Rgba? Tint)? ResolveTexturePath(BlockDefinition block) => ResolveTexturePath(block, -1);

    private (string Path, Rgba? Tint)? ResolveTexturePath(BlockDefinition block, int face)
    {
        try { return ResolveTexturePathCore(block, face); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException)
        { return null; }
    }

    private (string Path, Rgba? Tint)? ResolveTexturePathCore(BlockDefinition block, int face)
    {
        if (_blocks is null || _terrain is null) return null;
        string id = block.Name.Contains(':') ? block.Name[(block.Name.IndexOf(':') + 1)..] : block.Name;
        var entry = _blocks[id] ?? _blocks[block.Name] ?? (Aliases.TryGetValue(id, out var alias) ? _blocks[alias] : null);
        if (entry is null) return null;
        // Prefer the inventory ("carried") look, then the side face (what MapartCraft shows), then any face.
        var tex = face < 0 ? entry["carried_textures"] ?? entry["textures"] : entry["textures"];
        string faceName = face switch { 0 => "up", 1 => "north", 2 => "south", 3 => "west", 4 => "east", 5 => "down", _ => "side" };
        string? key = tex switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonObject o => (o[faceName] ?? o["side"] ?? o["south"] ?? o["north"] ?? o["up"] ?? o.Select(kv => kv.Value).FirstOrDefault())?.GetValue<string>(),
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

public sealed record TextureCoverage(int TotalBlocks, int MissingIcons, int MissingFaces, IReadOnlyList<string> MissingBlocks)
{
    public bool IsComplete => MissingIcons == 0 && MissingFaces == 0;
}
