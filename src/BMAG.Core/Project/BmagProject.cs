using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Imaging;
using BMAG.MapData;

namespace BMAG.Project;

public enum ExportMode { BlockConstruction, DirectMap }

/// <summary>Saved project (ProjectCore, design 13.1). Stored as JSON (*.bmag.json).</summary>
public sealed class BmagProject
{
    public int FormatVersion { get; set; } = 1;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectName { get; set; } = "新しいマップアート";

    /// <summary>Absolute path, or relative to the project file when the image was copied next to it.</summary>
    public string? SourceImagePath { get; set; }
    public string? SourceImageHash { get; set; }
    public bool ImageCopiedIntoProject { get; set; }

    public ImageSettings Image { get; set; } = new();
    public ExportMode Mode { get; set; } = ExportMode.BlockConstruction;
    public string TargetGameVersion { get; set; } = "1.26";

    // Block construction
    public string ColorProfileId { get; set; } = "bedrock-estimate-v1";
    public ColorMetric Metric { get; set; } = ColorMetric.Lab;
    public DitherMode Dither { get; set; } = DitherMode.None;
    public BuildMode BuildMode { get; set; } = BuildMode.Flat;
    public List<string>? EnabledBlocks { get; set; }
    public bool ExcludeGravity { get; set; } = true;
    public bool ExcludeFlammable { get; set; }
    public int MaxRarity { get; set; } = 3;
    public int MaxBlockTypes { get; set; }
    public string SupportBlockKey { get; set; } = "cobblestone";
    public int? OriginX { get; set; }
    public int? OriginY { get; set; }
    public int? OriginZ { get; set; }

    // Direct map
    public string? LastWorldFolder { get; set; }
    public long StartMapId { get; set; } = MapIdAllocator.DefaultStart;
    public ItemDelivery Delivery { get; set; } = ItemDelivery.ChestAndFrameWall;
    public bool GlowFrames { get; set; }

    /// <summary>Build progress per map number (design 7.3).</summary>
    public Dictionary<int, string> MapProgress { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string path)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options), new System.Text.UTF8Encoding(false));
    }

    public static BmagProject Load(string path) =>
        JsonSerializer.Deserialize<BmagProject>(File.ReadAllText(path), Options) ?? throw new InvalidDataException("プロジェクトファイルが空です。");

    public string? ResolveImagePath(string projectPath)
    {
        if (SourceImagePath is null) return null;
        return Path.IsPathRooted(SourceImagePath)
            ? SourceImagePath
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, SourceImagePath));
    }

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
