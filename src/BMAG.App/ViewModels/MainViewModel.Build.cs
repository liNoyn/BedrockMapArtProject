using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media.Imaging;
using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.Palette;
using BMAG.Project;

namespace BMAG.App.ViewModels;

/// <summary>Block construction settings, export, materials and build progress.</summary>
public sealed partial class MainViewModel
{
    public IReadOnlyList<LocOption<ColorMetric>> Metrics { get; private set; } = [];
    public IReadOnlyList<LocOption<DitherMode>> Dithers { get; private set; } = [];
    public IReadOnlyList<LocOption<StaircaseMode>> Staircases { get; private set; } = [];
    public IReadOnlyList<LocOption<SupportMode>> SupportModes { get; private set; } = [];
    public IReadOnlyList<LocOption<ProgressState>> ProgressStates { get; private set; } = [];

    private LocOption<ColorMetric> _metric = null!;
    public LocOption<ColorMetric> Metric { get => _metric; set { if (value != null && Set(ref _metric, value)) ScheduleUpdate(); } }
    private LocOption<DitherMode> _dither = null!;
    public LocOption<DitherMode> Dither { get => _dither; set { if (value != null && Set(ref _dither, value)) ScheduleUpdate(); } }
    private LocOption<StaircaseMode> _staircase = null!;
    public LocOption<StaircaseMode> StaircaseOption
    {
        get => _staircase;
        set { if (value != null && Set(ref _staircase, value)) { Raise(nameof(IsStaircase)); ScheduleUpdate(); } }
    }
    public bool IsStaircase => StaircaseOption?.Value is not (null or StaircaseMode.Flat);
    private LocOption<SupportMode> _supportMode = null!;
    public LocOption<SupportMode> SupportModeOption { get => _supportMode; set { if (value != null && Set(ref _supportMode, value)) ScheduleUpdate(); } }

    private int _maxTypes;
    public int MaxBlockTypes { get => _maxTypes; set { if (Set(ref _maxTypes, Math.Max(0, value))) ScheduleUpdate(); } }

    private List<BlockDefinition> _supportBlocks = [];
    public List<BlockDefinition> SupportBlocks { get => _supportBlocks; set => Set(ref _supportBlocks, value); }
    private string _supportKey = "cobblestone";
    private BlockDefinition? _supportBlock;
    public BlockDefinition? SelectedSupportBlock
    {
        get => _supportBlock;
        set { if (Set(ref _supportBlock, value) && value != null) { _supportKey = value.Key; ScheduleUpdate(); } }
    }

    private bool _useOrigin = true;
    public bool UseOrigin { get => _useOrigin; set { if (Set(ref _useOrigin, value)) Raise(nameof(OriginText)); } }
    private int _originMapX, _originMapZ, _originY = 64;
    public int OriginMapX { get => _originMapX; set { if (Set(ref _originMapX, value)) Raise(nameof(OriginText)); } }
    public int OriginMapZ { get => _originMapZ; set { if (Set(ref _originMapZ, value)) Raise(nameof(OriginText)); } }
    public int OriginY { get => _originY; set { if (Set(ref _originY, value)) Raise(nameof(OriginText)); } }
    private WorldOrigin? Origin => UseOrigin ? WorldOrigin.AlignedToMap(OriginMapX, OriginMapZ, OriginY) : null;
    public string OriginText => Origin is { } o ? L.Format("OriginText", o.X, o.Z + 1, o.Z, o.Y) : L["OriginNone"];

    private string _blockSummary = "";
    public string BlockSummary { get => _blockSummary; set => Set(ref _blockSummary, value); }

    public ObservableCollection<MaterialViewModel> Materials { get; } = [];
    public ObservableCollection<ViewAreaOption> MaterialAreas { get; } = [];
    private ViewAreaOption? _materialArea;
    public ViewAreaOption? MaterialArea { get => _materialArea; set { if (Set(ref _materialArea, value)) UpdateMaterials(); } }
    public ObservableCollection<MapProgressViewModel> MapProgress { get; } = [];

    public RelayCommand ExportBlockCommand { get; private set; } = null!;
    public RelayCommand ExportMaterialsCommand { get; private set; } = null!;

    private void InitBuild()
    {
        Metrics = [new(ColorMetric.Lab, "CIELAB ΔE76", L), new(ColorMetric.Ciede2000, "CIEDE2000", L), new(ColorMetric.WeightedRgb, "Weighted RGB", L), new(ColorMetric.Rgb, "RGB", L)];
        Dithers =
        [
            new(DitherMode.None, "SuNone", L), new(DitherMode.FloydSteinberg, "Floyd–Steinberg", L),
            new(DitherMode.JarvisJudiceNinke, "Jarvis–Judice–Ninke (MinAvgErr)", L), new(DitherMode.Burkes, "Burkes", L),
            new(DitherMode.SierraLite, "Sierra Lite", L), new(DitherMode.Stucki, "Stucki", L), new(DitherMode.Atkinson, "Atkinson", L),
            new(DitherMode.Bayer2, "Bayer 2x2", L), new(DitherMode.Bayer4, "Bayer 4x4", L), new(DitherMode.Bayer8, "Bayer 8x8", L),
            new(DitherMode.Ordered3, "Ordered 3x3", L),
        ];
        Staircases =
        [
            new(StaircaseMode.Flat, "StFlat", L), new(StaircaseMode.Classic, "StClassic", L), new(StaircaseMode.Valley, "StValley", L),
            new(StaircaseMode.FullDark, "StDark", L), new(StaircaseMode.FullBright, "StBright", L),
        ];
        SupportModes =
        [
            new(SupportMode.None, "SuNone", L), new(SupportMode.Important, "SuImportant", L),
            new(SupportMode.AllOptimized, "SuAll", L), new(SupportMode.AllDouble, "SuDouble", L),
        ];
        ProgressStates =
        [
            new(ProgressState.NotStarted, "PrNotStarted", L), new(ProgressState.InProgress, "PrInProgress", L),
            new(ProgressState.Done, "PrDone", L), new(ProgressState.Skipped, "PrSkipped", L), new(ProgressState.NeedsFix, "PrNeedsFix", L),
        ];
        _metric = Metrics[0];
        _dither = Dithers[0];
        _staircase = Staircases[0];
        _supportMode = SupportModes[1];
        ExportBlockCommand = new RelayCommand(ExportBlockAsync, () => _layout != null && !IsBusy);
        ExportMaterialsCommand = new RelayCommand(ExportMaterialsAsync, () => _layout != null);
        RebuildProgress();
    }

    private QuantizeOptions CurrentQuantizeOptions() => new() { Metric = Metric.Value, Dither = Dither.Value, Mode = StaircaseOption.Value };

    private void UpdateBlockInfo()
    {
        if (_quantized is null || _layout is null || Profile is null || _processed is null)
        {
            BlockSummary = "";
            Materials.Clear();
            return;
        }
        var all = MaterialCalculator.Calculate(_layout, MapsX);
        double err = MeanDeltaE(_processed, _quantized.Preview);
        int types = all.Count(m => !m.IsSupport);
        int unverified = all.Where(m => !m.IsSupport).Count(m => !m.Block.Verified);
        var sb = new StringBuilder();
        if (L.English)
        {
            sb.Append($"{types} block types / {all.Sum(m => m.Count):N0} blocks (support {all.Where(m => m.IsSupport).Sum(m => m.Count):N0})");
            sb.Append($"\nAverage colour error ΔE {err:0.0} / max height {_layout.MaxHeight}");
            if (unverified > 0) sb.Append($"\n* {unverified} of the map colours are unverified estimates.");
        }
        else
        {
            sb.Append($"使用ブロック {types} 種 / 合計 {all.Sum(m => m.Count):N0} 個（支え {all.Where(m => m.IsSupport).Sum(m => m.Count):N0}）");
            sb.Append($"\n平均色差 ΔE {err:0.0} / 最大高さ {_layout.MaxHeight}");
            if (unverified > 0) sb.Append($"\n※ {unverified} 種の地図色は実機未検証の推定値です。");
        }
        BlockSummary = sb.ToString();

        var current = MaterialArea?.MapNumber ?? 0;
        MaterialAreas.Clear();
        MaterialAreas.Add(new ViewAreaOption(0, L["AllMaps"]));
        for (int n = 1; n <= MapsX * MapsY; n++) MaterialAreas.Add(new ViewAreaOption(n, L.Format("MapN", n)));
        _materialArea = MaterialAreas.FirstOrDefault(a => a.MapNumber == current) ?? MaterialAreas[0];
        Raise(nameof(MaterialArea));
        UpdateMaterials();
        if (MapProgress.Count != MapsX * MapsY) RebuildProgress();
        UpdateProgressTexts();
    }

    private void UpdateMaterials()
    {
        Materials.Clear();
        if (_layout is null || Profile is null) return;
        int? only = MaterialArea is { MapNumber: > 0 } a ? a.MapNumber : null;
        var icons = new Dictionary<string, Bitmap?>();
        foreach (var m in MaterialCalculator.Calculate(_layout, MapsX, only))
        {
            if (!icons.TryGetValue(m.Block.Key, out var icon))
                icons[m.Block.Key] = icon = Textures.IsAvailable && Textures.GetIcon(m.Block) is { } img ? BitmapHelper.ToBitmap(img) : null;
            string ownedKey = m.Block.Key + (m.IsSupport ? "#support" : "");
            Materials.Add(new MaterialViewModel(m, Profile, icon, _project.OwnedMaterials.GetValueOrDefault(ownedKey), L,
                (_, v) => _project.OwnedMaterials[ownedKey] = v));
        }
    }

    private void RebuildProgress()
    {
        MapProgress.Clear();
        if (ProgressStates.Count == 0) return;
        for (int n = 1; n <= MapsX * MapsY; n++)
            MapProgress.Add(new MapProgressViewModel(n, _project.MapProgress.GetValueOrDefault(n, ProgressState.NotStarted), ProgressStates,
                (num, state) => _project.MapProgress[num] = state));
        UpdateProgressTexts();
    }

    private void UpdateProgressTexts()
    {
        foreach (var m in MapProgress)
        {
            int done = _project.DoneChunks.Count(k => k.StartsWith(m.Number + ":"));
            m.ChunksText = L.Format("ChunksDone", done, 64);
        }
    }

    private async Task ExportBlockAsync()
    {
        if (_quantized is null || _layout is null || Profile is null) return;
        var folder = await _dialogs.PickFolderAsync(L["ExportBuild"]);
        if (folder is null) return;
        IsBusy = true;
        Status = L["Exporting"];
        try
        {
            var q = _quantized;
            var layout = _layout;
            var profile = Profile;
            string name = ProjectName;
            int mx = MapsX, my = MapsY;
            var origin = Origin;
            var tex = Textures.IsAvailable ? Textures : null;
            string outDir = Path.Combine(folder, $"BMAG_{StructureExporter.Slug(name)}_build");
            var result = await Task.Run(() =>
            {
                if (layout.MaxHeight > 380) throw new InvalidOperationException(L.English ? "The build is taller than the world height." : "ワールドの高さを超えています。");
                var materials = MaterialCalculator.Calculate(layout, mx);
                BuildGuideGenerator.WriteAll(Path.Combine(outDir, "guide"), layout, q, profile, materials, mx, my, origin, name);
                // Textured 2D build views per map (MapartCraft "view online" as images).
                for (int my2 = 0; my2 < my; my2++)
                    for (int mx2 = 0; mx2 < mx; mx2++)
                    {
                        var img = BMAG.Preview.LayoutRenderer.RenderTopDown(layout, profile, BMAG.Preview.LayoutRegion.Map(mx2, my2), 8,
                            tex is null ? null : tex.GetIcon, true);
                        ImageCodec.SavePng(img, Path.Combine(outDir, "guide", "maps", $"map_{my2 * mx + mx2 + 1:00}_blocks.png"));
                    }
                return StructureExporter.Export(outDir, name, layout, mx, new StructureExportOptions(), origin);
            });
            Log((L.English ? "Exported build data: " : "建築データを出力しました: ") + outDir);
            Log($"  {result.Tiles.Count} structures ({result.Namespace}), {Path.GetFileName(result.McpackPath)}");
            Log($"  /function bmag/{result.Slug}/all");
        }
        catch (Exception ex) { Log((L.English ? "Export failed: " : "建築データの出力に失敗しました: ") + ex.Message); }
        finally { IsBusy = false; Status = L["Ready"]; }
    }

    private async Task ExportMaterialsAsync()
    {
        if (_layout is null) return;
        var path = await _dialogs.SaveFileAsync(L["ExportMaterials"], StructureExporter.Slug(ProjectName) + "_materials.csv", "csv");
        if (path is null) return;
        var sb = new StringBuilder("block,id,kind,count,stacks,owned,missing,maps\n");
        foreach (var m in Materials)
            sb.AppendLine($"\"{m.Name}\",{m.Key},{(m.Name.Contains(L["Support"]) ? "support" : "art")},{m.Count},\"{m.Stacks}\",{m.Owned},{m.Missing},\"{m.Maps}\"");
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true));
        Log((L.English ? "Saved: " : "保存しました: ") + path);
    }
}
