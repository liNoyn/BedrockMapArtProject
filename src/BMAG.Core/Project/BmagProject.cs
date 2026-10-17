using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Imaging;
using BMAG.MapData;
using BMAG.Palette;

namespace BMAG.Project;

public enum ExportMode { BlockConstruction, DirectMap }

/// <summary>Build progress state (design 7.3).</summary>
public enum ProgressState { NotStarted, InProgress, Done, Skipped, NeedsFix }

/// <summary>Saved project (ProjectCore, design 13.1). Stored as JSON (*.bmag.json).</summary>
public sealed class BmagProject
{
    public int FormatVersion { get; set; } = 2;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string ProjectName { get; set; } = "新しいマップアート";

    /// <summary>Absolute path, or relative to the project file when the image was copied next to it.</summary>
    public string? SourceImagePath { get; set; }
    public string? SourceImageHash { get; set; }
    public bool ImageCopiedIntoProject { get; set; }
    /// <summary>Animation frame / TIFF page.</summary>
    public int SourceFrame { get; set; }
    /// <summary>Position in a video source, in seconds.</summary>
    public double SourceVideoSeconds { get; set; }

    public ImageSettings Image { get; set; } = new();
    public ExportMode Mode { get; set; } = ExportMode.BlockConstruction;
    public string TargetGameVersion { get; set; } = "1.26";

    // Block construction
    public string ColorProfileId { get; set; } = ColorProfile.DefaultId;
    public ColorMetric Metric { get; set; } = ColorMetric.Oklab;
    public DitherMode Dither { get; set; } = DitherMode.None;
    public double DitherStrength { get; set; } = 1.0;
    public bool Serpentine { get; set; } = true;
    public Export.StructureSplit Split { get; set; } = Export.StructureSplit.Tiles64;
    public bool WriteBehaviorPack { get; set; }
    /// <summary>Manual pixel edits ("x,y,blockKey,shade").</summary>
    public List<string>? Edits { get; set; }
    public StaircaseMode Staircase { get; set; } = StaircaseMode.Flat;
    public SupportMode Supports { get; set; } = SupportMode.Important;
    /// <summary>Colour set id → chosen block key.</summary>
    public Dictionary<int, string>? Selection { get; set; }
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

    /// <summary>Build progress per map number (1-based).</summary>
    public Dictionary<int, ProgressState> MapProgress { get; set; } = [];
    /// <summary>Finished 16x16 chunks as "mapNumber:chunkX:chunkZ".</summary>
    public HashSet<string> DoneChunks { get; set; } = [];
    /// <summary>Blocks the player already has, by block key (material list "owned" column).</summary>
    public Dictionary<string, int> OwnedMaterials { get; set; } = [];

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
