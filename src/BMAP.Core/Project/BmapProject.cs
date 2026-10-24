using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Project;

/// <summary>Build progress state (design 7.3).</summary>
public enum ProgressState { NotStarted, InProgress, Done, Skipped, NeedsFix }

/// <summary>Saved project (ProjectCore, design 13.1). Stored as JSON (*.bmap.json).</summary>
public sealed class BmapProject
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
    public string TargetGameVersion { get; set; } = "1.26";

    // Block construction
    public string ColorProfileId { get; set; } = ColorProfile.DefaultId;
    public ColorMetric Metric { get; set; } = ColorMetric.Oklab;
    public DitherMode Dither { get; set; } = DitherMode.Optimized;
    public double DitherStrength { get; set; } = 1.0;
    /// <summary>Maximum staircase height (0 = unlimited).</summary>
    public int MaxHeight { get; set; }
    /// <summary>Shading model chosen by the user ("" = the colour profile's own rule).</summary>
    public string ShadingModelId { get; set; } = "";
    public double ShadePenalty { get; set; }
    public bool Serpentine { get; set; } = true;
    /// <summary>Dither in linear light (default since v0.4.0).</summary>
    public bool LinearDiffusion { get; set; } = true;
    public Export.StructureSplit Split { get; set; } = Export.StructureSplit.Tiles128;
    public bool WriteBehaviorPack { get; set; }
    /// <summary>Manual pixel edits ("x,y,blockKey,shade").</summary>
    public List<string>? Edits { get; set; }
    public StaircaseMode Staircase { get; set; } = StaircaseMode.Flat;
    public SupportMode Supports { get; set; } = SupportMode.Important;
    /// <summary>Selected block keys (any number per colour).</summary>
    public List<string>? SelectedBlocks { get; set; }
    /// <summary>Old format (one block per colour set): colour set id → block key. Still read.</summary>
    public Dictionary<int, string>? Selection { get; set; }
    public BlockMixing Mixing { get; set; } = BlockMixing.Balanced;

    /// <summary>The selection in either format, or null when none is stored.</summary>
    public HashSet<string>? SelectionKeys() =>
        SelectedBlocks is { } list ? [.. list] : Selection is { } old ? [.. old.Values] : null;
    public int MaxBlockTypes { get; set; }
    public string SupportBlockKey { get; set; } = "cobblestone";
    /// <summary>Map grid position of the build (null = not aligned). Aligned by default: the coordinate parity is then known.</summary>
    public int? OriginX { get; set; } = 0;
    public int? OriginY { get; set; } = 64;
    public int? OriginZ { get; set; } = 0;

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
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options), new System.Text.UTF8Encoding(false));
    }

    public static BmapProject Load(string path)
    {
        var project = JsonSerializer.Deserialize<BmapProject>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("プロジェクトファイルが空です。");
        if (project.FormatVersion is < 1 or > 2)
            throw new InvalidDataException($"未対応のプロジェクト形式です: {project.FormatVersion}");
        if (project.Image is not { } image || image.MapsX <= 0 || image.MapsY <= 0 ||
            (long)image.MapsX * image.MapsY > int.MaxValue / (128 * 128 * 4) ||
            !Enum.IsDefined(image.Fit) || !Enum.IsDefined(image.Interpolation) ||
            !Enum.IsDefined(image.CropMode) || !Enum.IsDefined(image.Transparency) ||
            !Enum.IsDefined(project.Dither) || !Enum.IsDefined(project.Metric) || !Enum.IsDefined(project.Staircase) ||
            !Enum.IsDefined(project.Supports) || !Enum.IsDefined(project.Split) || !Enum.IsDefined(project.Mixing))
            throw new InvalidDataException("プロジェクトの変換設定が不正です。");
        project.MapProgress ??= [];
        project.DoneChunks ??= [];
        project.OwnedMaterials ??= [];
        if (project.ProjectName is null || project.MapProgress.Values.Any(state => !Enum.IsDefined(state)))
            throw new InvalidDataException("プロジェクト名または進捗設定が不正です。");
        project.DoneChunks.RemoveWhere(string.IsNullOrWhiteSpace);
        return project;
    }

    public string? ResolveImagePath(string projectPath)
    {
        if (SourceImagePath is null) return null;
        return Path.IsPathRooted(SourceImagePath)
            ? SourceImagePath
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(projectPath))!, SourceImagePath));
    }

    public static string HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
