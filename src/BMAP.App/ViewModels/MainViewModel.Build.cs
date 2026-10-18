using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media.Imaging;
using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Project;
using BMAP.Shading;

namespace BMAP.App.ViewModels;

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
        set { if (value != null && Set(ref _staircase, value)) { Raise(nameof(IsStaircase)); Raise(nameof(CanLimitHeight)); ScheduleUpdate(); } }
    }
    public bool IsStaircase => StaircaseOption?.Value is not (null or StaircaseMode.Flat);
    private LocOption<SupportMode> _supportMode = null!;
    public LocOption<SupportMode> SupportModeOption { get => _supportMode; set { if (value != null && Set(ref _supportMode, value)) ScheduleUpdate(); } }

    // ---- 3D map art: height ----
    private int _maxHeight;
    public int MaxHeight { get => _maxHeight; set { if (Set(ref _maxHeight, Math.Max(0, value))) ScheduleUpdate(); } }
    private double _shadePenalty;
    public double ShadePenalty { get => _shadePenalty; set { if (Set(ref _shadePenalty, Math.Clamp(value, 0, 1))) ScheduleUpdate(); } }
    /// <summary>The height limit works with Valley only (a Classic staircase never drops back).</summary>
    public bool CanLimitHeight => StaircaseOption?.Value is StaircaseMode.Valley;
    public ObservableCollection<HeightRowViewModel> HeightRows { get; } = [];
    private bool _isAnalyzing;
    public bool IsAnalyzing { get => _isAnalyzing; set { if (Set(ref _isAnalyzing, value)) AnalyzeHeightCommand?.RaiseCanExecuteChanged(); } }
    private int _analysisProgress;
    public int AnalysisProgress { get => _analysisProgress; set => Set(ref _analysisProgress, value); }
    private string _heightAnalysisNote = "";
    public string HeightAnalysisNote { get => _heightAnalysisNote; set => Set(ref _heightAnalysisNote, value); }
    private string _placementText = "";
    public string PlacementText { get => _placementText; set => Set(ref _placementText, value); }
    private PlacementAdvice? _advice;
    public RelayCommand AnalyzeHeightCommand { get; private set; } = null!;
    public RelayCommand ApplyRecommendedYCommand { get; private set; } = null!;
    public RelayCommand FlatBackgroundCommand { get; private set; } = null!;

    /// <summary>
    /// Sets the background to the closest colour the selected blocks can show in the normal (flat) shade, so large
    /// background areas need no staircase. Pure white, for example, only exists in the light shade.
    /// </summary>
    private void UseFlatBackground()
    {
        if (Profile is null) return;
        Rgba bg;
        try { bg = Rgba.FromHex(BackgroundColor); } catch { bg = new Rgba(255, 255, 255); }
        var target = ColorEngine.ColorMath.ToOklab(bg.R, bg.G, bg.B);
        var best = CurrentSelection().Resolve(Profile)
            .Select(e => Profile.ColorOf(e.Block, Shade.Normal))
            .OrderBy(c => ColorEngine.ColorMath.DeltaE76(target, ColorEngine.ColorMath.ToOklab(c.R, c.G, c.B)))
            .FirstOrDefault();
        if (best == default) return;
        BackgroundColor = best.ToHex();
        Log(L.Format("FlatBackgroundSet", BackgroundColor));
    }

    private async Task AnalyzeHeightAsync()
    {
        if (_processed is null || Profile is null) return;
        IsAnalyzing = true;
        AnalysisProgress = 0;
        HeightRows.Clear();
        try
        {
            var img = _processed;
            var profile = Profile;
            var selection = CurrentSelection();
            var options = CurrentQuantizeOptions();
            var support = SelectedSupportBlock ?? profile.Blocks.First();
            var supports = SupportModeOption.Value;
            var progress = new Progress<int>(p => AnalysisProgress = p);
            bool grid = UseOrigin;
            var rows = await Task.Run(() => HeightAdvisor.Analyze(img, profile, selection, options, support, supports, progress: progress, onMapGrid: grid));
            foreach (var r in rows) HeightRows.Add(new HeightRowViewModel(r, L, ApplyHeightOption));
            var rec = rows.Single(r => r.Recommended);
            var flat = rows[0];
            HeightAnalysisNote = L.Format("HeightAnalysisResult",
                rec.Mode == StaircaseMode.Flat ? L["Flat"] : rec.Limit == 0 ? L["Unlimited"] : rec.Limit.ToString(),
                rec.BuildHeight, rec.Quality.ToString("0"), flat.MeanDeltaE.ToString("0.0"), rec.MeanDeltaE.ToString("0.0"));
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { IsAnalyzing = false; }
    }

    private void ApplyHeightOption(HeightOption o)
    {
        _loadingProject = true;
        try
        {
            StaircaseOption = Staircases.First(s => s.Value == o.Mode);
            MaxHeight = o.Limit;
        }
        finally { _loadingProject = false; }
        ScheduleUpdate();
        Log(L.Format("HeightApplied", o.Mode == StaircaseMode.Flat ? L["Flat"] : o.Limit == 0 ? L["Unlimited"] : o.Limit.ToString(), o.BuildHeight));
    }

    private void UpdatePlacement()
    {
        if (_layout is null || ApplyRecommendedYCommand is null) { PlacementText = ""; _advice = null; return; }
        _advice = PlacementAdvisor.Advise(_layout, UseOrigin ? OriginY : null, L.English);
        var a = _advice;
        var sb = new StringBuilder(L.Format("PlacementSummary", a.BuildHeight, a.MinBaseY, a.MaxBaseY, a.RecommendedBaseY));
        if (UseOrigin) sb.Append("\n").Append(L.Format("PlacementCurrent", OriginY, OriginY + a.BuildHeight - 1));
        foreach (var n in a.Notes) sb.Append("\n• ").Append(n);
        PlacementText = sb.ToString();
        ApplyRecommendedYCommand.RaiseCanExecuteChanged();
    }

    // ---- shading model (the profile's own rule, or a candidate chosen before it is measured) ----
    public IReadOnlyList<LocOption<BMAP.Shading.ShadingModel?>> ShadingModels { get; private set; } = [];
    private LocOption<BMAP.Shading.ShadingModel?>? _shadingModel;
    public LocOption<BMAP.Shading.ShadingModel?>? ShadingModelOption
    {
        get => _shadingModel;
        set
        {
            if (value is null || !Set(ref _shadingModel, value) || BaseProfile is null) return;
            SelectProfile(BaseProfile, CurrentSelection().Keys);
        }
    }

    private double _ditherStrength = 1.0;
    public double DitherStrength { get => _ditherStrength; set { if (Set(ref _ditherStrength, Math.Clamp(value, 0, 1))) ScheduleUpdate(); } }
    private bool _serpentine = true;
    public bool Serpentine { get => _serpentine; set { if (Set(ref _serpentine, value)) ScheduleUpdate(); } }
    private bool _linearDiffusion = true;
    public bool LinearDiffusion { get => _linearDiffusion; set { if (Set(ref _linearDiffusion, value)) ScheduleUpdate(); } }

    public IReadOnlyList<LocOption<StructureSplit>> Splits { get; private set; } = [];
    private LocOption<StructureSplit> _split = null!;
    public LocOption<StructureSplit> SplitOption { get => _split; set { if (value != null) Set(ref _split, value); } }
    private bool _writeBehaviorPack;
    public bool WriteBehaviorPack { get => _writeBehaviorPack; set => Set(ref _writeBehaviorPack, value); }

    // ---- manual editing (paint) ----
    public ObservableCollection<BrushOption> Brushes { get; } = [];
    private BrushOption? _brush;
    public BrushOption? Brush { get => _brush; set => Set(ref _brush, value); }
    public IReadOnlyList<LocOption<Shade>> BrushShades { get; private set; } = [];
    private LocOption<Shade> _brushShade = null!;
    public LocOption<Shade> BrushShade { get => _brushShade; set { if (value != null) Set(ref _brushShade, value); } }
    private bool _paintMode;
    public bool PaintMode { get => _paintMode; set { if (Set(ref _paintMode, value) && value) EditProgress = false; } }
    public string EditsText => L.Format("EditsCount", _edits.Count);
    public RelayCommand UndoEditCommand { get; private set; } = null!;
    public RelayCommand ClearEditsCommand { get; private set; } = null!;
    public RelayCommand SavePreviewPngCommand { get; private set; } = null!;
    public RelayCommand ExportTestPadCommand { get; private set; } = null!;
    public RelayCommand View3DCommand { get; private set; } = null!;
    public RelayCommand ShowShadeMapCommand { get; private set; } = null!;

    // ---- shading & build check ----
    private string _shadingText = "";
    public string ShadingText { get => _shadingText; set => Set(ref _shadingText, value); }
    private string _shadingVerdict = "";
    public string ShadingVerdict { get => _shadingVerdict; set => Set(ref _shadingVerdict, value); }
    private bool _shadingOk = true;
    public bool ShadingOk { get => _shadingOk; set { if (Set(ref _shadingOk, value)) Raise(nameof(ShadingProblem)); } }
    public bool ShadingProblem => !ShadingOk;

    private void UpdateShadingText()
    {
        if (_report is not { } r) { ShadingText = ""; ShadingVerdict = ""; ShadingOk = true; return; }
        var notes = new List<string> { r.Mismatches == 0 ? L["ExactYes"] : L.Format("ExactNo", r.Mismatches) };
        if (r.DependsOnTerrain > 0) notes.Add(L.Format("TerrainNote", r.DependsOnTerrain));
        if (r.WillFall + r.WillBreak > 0) notes.Add(L.Format("FallNote", r.WillFall + r.WillBreak));
        ShadingVerdict = string.Join("\n", notes);
        ShadingOk = r.Mismatches == 0 && r.WillFall + r.WillBreak == 0;
        ShadingText = L.Format("ShadingSummary", r.Dark, r.Normal, r.Bright, r.MaxY, r.TallestX, r.TallestZ, r.MeanHeight, r.Steps, r.Drops);
    }

    /// <summary>Saves the predicted map (block map colours) as PNG: the whole picture and one file per map.</summary>
    private async Task SavePreviewPngAsync()
    {
        if (_quantized is null) return;
        var folder = await _dialogs.PickFolderAsync(L["BtnSavePng"]);
        if (folder is null) return;
        try
        {
            string dir = Path.Combine(folder, $"BMAP_{StructureExporter.Slug(ProjectName)}_preview");
            var img = _quantized.Preview;
            var tiles = TileSplitter.Split(img, MapsX, MapsY);
            await Task.Run(() =>
            {
                ImageCodec.SavePng(img, Path.Combine(dir, "map_preview.png"));
                if (tiles.Count > 1)
                    foreach (var t in tiles) ImageCodec.SavePng(t.Image, Path.Combine(dir, $"map_{t.Index + 1:00}_x{t.GridX}_y{t.GridY}.png"));
            });
            Log((L.English ? "Saved: " : "保存しました: ") + dir);
            OpenFolder(dir);
        }
        catch (Exception ex) { Log(ex.Message); }
    }


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
    public bool UseOrigin { get => _useOrigin; set { if (Set(ref _useOrigin, value)) { Raise(nameof(OriginText)); UpdatePlacement(); ApplyEditsAndLayout(); RefreshPreview(); } } }
    private int _originMapX, _originMapZ, _originY = 64;
    public int OriginMapX { get => _originMapX; set { if (Set(ref _originMapX, value)) Raise(nameof(OriginText)); } }
    public int OriginMapZ { get => _originMapZ; set { if (Set(ref _originMapZ, value)) Raise(nameof(OriginText)); } }
    public int OriginY { get => _originY; set { if (Set(ref _originY, value)) { Raise(nameof(OriginText)); UpdatePlacement(); } } }
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
        Metrics = [new(ColorMetric.Oklab, "Oklab", L), new(ColorMetric.Lab, "CIELAB ΔE76", L), new(ColorMetric.Ciede2000, "CIEDE2000", L), new(ColorMetric.WeightedRgb, "Weighted RGB", L), new(ColorMetric.Rgb, "RGB", L)];
        Splits = [new(StructureSplit.Tiles64, "SplitTiles", L), new(StructureSplit.PerMap, "SplitPerMap", L), new(StructureSplit.Whole, "SplitWhole", L)];
        _split = Splits[0];
        BrushShades = [new(Shade.Normal, "ShadeNormal", L), new(Shade.Bright, "ShadeBright", L), new(Shade.Dark, "ShadeDark", L)];
        _brushShade = BrushShades[0];
        UndoEditCommand = new RelayCommand(UndoEdit, () => _edits.CanUndo);
        ClearEditsCommand = new RelayCommand(ClearEditsAsync, () => _edits.Count > 0);
        SavePreviewPngCommand = new RelayCommand(SavePreviewPngAsync, () => _quantized != null);
        ExportTestPadCommand = new RelayCommand(() => ExportPadAsync(PadKind.Shading));
        View3DCommand = new RelayCommand(() => SelectedPreviewMode = PreviewModes.First(p => p.Value == PreviewMode.Build3D), () => _layout != null);
        ShowShadeMapCommand = new RelayCommand(() => SelectedPreviewMode = PreviewModes.First(p => p.Value == PreviewMode.ShadeMap));
        AnalyzeHeightCommand = new RelayCommand(AnalyzeHeightAsync, () => _processed != null && !IsAnalyzing);
        FlatBackgroundCommand = new RelayCommand(UseFlatBackground);
        ApplyRecommendedYCommand = new RelayCommand(() => { if (_advice is { Fits: true } a) { UseOrigin = true; OriginY = a.RecommendedBaseY; } }, () => _advice is { Fits: true });
        ShadingModels =
        [
            new(null, "ModelProfile", L),
            .. BMAP.Shading.ShadingModel.Candidates.Where(m => m.Kind != BMAP.Shading.ShadingModelKind.Graded)
                .Select(m => new LocOption<BMAP.Shading.ShadingModel?>(m, "Model_" + m.Id, L)),
        ];
        _shadingModel = ShadingModels[0];
        Dithers =
        [
            new(DitherMode.None, "SuNone", L), new(DitherMode.FloydSteinberg, "Floyd–Steinberg", L),
            new(DitherMode.JarvisJudiceNinke, "Jarvis–Judice–Ninke (MinAvgErr)", L), new(DitherMode.Burkes, "Burkes", L),
            new(DitherMode.SierraLite, "Sierra Lite", L), new(DitherMode.Stucki, "Stucki", L), new(DitherMode.Atkinson, "Atkinson", L),
            new(DitherMode.KnollBayer, "DitherKnollBayer", L), new(DitherMode.KnollBlueNoise, "DitherKnollBlue", L),
            new(DitherMode.BlueNoise, "DitherBlueNoise", L),
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

    private QuantizeOptions CurrentQuantizeOptions() => new()
    {
        Metric = Metric.Value, Dither = Dither.Value, Mode = StaircaseOption.Value, DitherStrength = DitherStrength, Serpentine = Serpentine,
        LinearDiffusion = LinearDiffusion,
        MaxHeight = MaxHeight, ShadePenalty = ShadePenalty,
    };

    private StructureExportOptions CurrentStructureOptions() => new()
    {
        Split = SplitOption.Value, WriteBehaviorPack = WriteBehaviorPack, English = L.English,
    };

    /// <summary>Brush list: every block of the profile plus "no block" and "back to automatic".</summary>
    private void RebuildBrushes()
    {
        if (Profile is null) return;
        var current = Brush?.Key;
        Brushes.Clear();
        Brushes.Add(new BrushOption(BrushOption.Auto, L["BrushAuto"], null));
        Brushes.Add(new BrushOption(BrushOption.None, L["BrushNone"], null));
        var used = _quantized?.Entries.Select(e => e.Block.Key).ToHashSet() ?? [];
        foreach (var b in Profile.Blocks.OrderByDescending(b => used.Contains(b.Key)).ThenBy(b => b.ColourSetId))
            Brushes.Add(new BrushOption(b.Key, (used.Contains(b.Key) ? "● " : "") + b.LocalName(L.English), b));
        Brush = Brushes.FirstOrDefault(x => x.Key == current) ?? Brushes.ElementAtOrDefault(2) ?? Brushes[0];
    }

    private bool _painting;
    private int _brushEntryCount = -1;

    /// <summary>Paints the pixel under the pointer (layout x, z) with the current brush.</summary>
    private void Paint(int x, int z, bool start, bool end)
    {
        if (Brush is null || _baseQuantized is null || z < 1) return;
        if (start) { _edits.BeginStroke(); _painting = true; }
        if (!_painting) return;
        int px = x, py = z - 1;
        bool changed = Brush.Key == BrushOption.Auto
            ? _edits.Set(new PixelEdit(px, py, null), clear: true)
            : _edits.Set(new PixelEdit(px, py, Brush.Key == BrushOption.None ? null : Brush.Key, BrushShade.Value));
        if (end) { _edits.EndStroke(); _painting = false; }
        if (!changed && !end) return;
        ApplyEditsAndLayout();
        UpdateBlockInfo();
        RefreshPreview();
        Raise(nameof(EditsText));
        UndoEditCommand.RaiseCanExecuteChanged();
        ClearEditsCommand.RaiseCanExecuteChanged();
    }

    public void EndPaintStroke()
    {
        if (!_painting) return;
        _edits.EndStroke();
        _painting = false;
        UndoEditCommand.RaiseCanExecuteChanged();
    }

    private void UndoEdit()
    {
        _edits.Undo();
        ApplyEditsAndLayout();
        UpdateBlockInfo();
        RefreshPreview();
        Raise(nameof(EditsText));
        UndoEditCommand.RaiseCanExecuteChanged();
        ClearEditsCommand.RaiseCanExecuteChanged();
    }

    private async Task ClearEditsAsync()
    {
        if (!await _dialogs.ConfirmAsync(L["ClearEdits"], L["ClearEditsConfirm"])) return;
        _edits.Clear();
        UndoEdit();
    }

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
        // Unverified: neither the block's colour nor its colour set's base colour has been confirmed on Bedrock maps.
        int unverified = all.Where(m => !m.IsSupport).Count(m => m.Block.EffectiveStatus != ColorStatus.Verified
            && Profile.SetOf(m.Block)?.Status != ColorStatus.Verified);
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
        if (Brushes.Count == 0 || _quantized.Entries.Count != _brushEntryCount) { RebuildBrushes(); _brushEntryCount = _quantized.Entries.Count; }
        Raise(nameof(EditsText));
        UpdatePlacement();
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
            string outDir = Path.Combine(folder, $"BMAP_{StructureExporter.Slug(name)}");
            var structureOptions = CurrentStructureOptions();
            bool en = L.English;
            var result = await Task.Run(() =>
            {
                if (layout.MaxHeight > 380) throw new InvalidOperationException(L.English ? "The build is taller than the world height." : "ワールドの高さを超えています。");
                var materials = MaterialCalculator.Calculate(layout, mx);
                BuildGuideGenerator.WriteAll(Path.Combine(outDir, "guide"), layout, q, profile, materials, mx, my, origin, name, en);
                // Textured 2D build views per map (MapartCraft "view online" as images).
                for (int my2 = 0; my2 < my; my2++)
                    for (int mx2 = 0; mx2 < mx; mx2++)
                    {
                        var img = BMAP.Preview.LayoutRenderer.RenderTopDown(layout, profile, BMAP.Preview.LayoutRegion.Map(mx2, my2), 8,
                            tex is null ? null : tex.GetIcon, true);
                        ImageCodec.SavePng(img, Path.Combine(outDir, "guide", "maps", $"map_{my2 * mx + mx2 + 1:00}_blocks.png"));
                    }
                return StructureExporter.Export(outDir, name, layout, mx, structureOptions, origin);
            });
            Log((L.English ? "Exported: " : "出力しました: ") + outDir);
            Log(L.Format("ExportedFiles", result.Files.Count, result.StructureDirectory));
            if (result.McpackPath != null) Log("  " + Path.GetFileName(result.McpackPath));
            OpenFolder(outDir);
        }
        catch (Exception ex) { Log((L.English ? "Export failed: " : "建築データの出力に失敗しました: ") + ex.Message); }
        finally { IsBusy = false; Status = L["Ready"]; }
    }

    private static void OpenFolder(string dir)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
        catch { /* opening the folder is a convenience */ }
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
