using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BMAG.Bedrock.LevelDb;
using BMAG.Bedrock.World;
using BMAG.BlockEngine;
using BMAG.Calibration;
using BMAG.ColorEngine;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.MapData;
using BMAG.Palette;
using BMAG.Project;

namespace BMAG.App.ViewModels;

public interface IDialogService
{
    Task<string?> OpenFileAsync(string title, string filterName, string[] patterns);
    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message);
}

public enum PreviewMode { Original, DirectMap, BlockMap, Difference }

public sealed class MainViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _updateCts;
    private DispatcherTimer? _debounce;
    private RgbaImage? _source;
    private RgbaImage? _sourceThumb;
    private RgbaImage? _processed;
    private QuantizeResult? _quantized;
    private string? _projectPath;
    private bool _loadingProject;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        OpenImageCommand = new RelayCommand(OpenImageAsync);
        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProjectAsync);
        SaveProjectCommand = new RelayCommand(() => SaveProjectAsync(false));
        SaveProjectAsCommand = new RelayCommand(() => SaveProjectAsync(true));
        ExportBlockCommand = new RelayCommand(ExportBlockAsync, () => _quantized != null && !IsBusy);
        ExportTilesCommand = new RelayCommand(ExportTilesAsync, () => _processed != null && !IsBusy);
        RefreshWorldsCommand = new RelayCommand(RefreshWorlds);
        BrowseWorldCommand = new RelayCommand(BrowseWorldAsync);
        PlanWorldExportCommand = new RelayCommand(PlanWorldExport, () => _processed != null && SelectedWorld != null);
        ExecuteWorldExportCommand = new RelayCommand(ExecuteWorldExportAsync, () => _processed != null && SelectedWorld != null && !IsBusy);
        LoadMapsCommand = new RelayCommand(LoadMapsAsync, () => SelectedWorld != null && !IsBusy);
        ExportMapPngCommand = new RelayCommand(ExportMapPngAsync, () => SelectedMap != null);
        UseMapAsSourceCommand = new RelayCommand(UseMapAsSource, () => SelectedMap != null);
        ExportCalibrationPackCommand = new RelayCommand(ExportCalibrationPackAsync);
        MeasureCalibrationCommand = new RelayCommand(MeasureCalibration, () => SelectedMap != null);
        SaveCalibratedProfileCommand = new RelayCommand(SaveCalibratedProfile, () => _pendingMeasurements is { Count: > 0 });
        PaletteAllCommand = new RelayCommand(() => SetAllPalette(_ => true));
        PaletteNoneCommand = new RelayCommand(() => SetAllPalette(_ => false));
        PaletteDefaultCommand = new RelayCommand(() => SetAllPalette(b => b.DefaultEnabled));

        LoadProfiles();
        RefreshWorlds();
        Log("Bedrock Map Art Generator を起動しました。画像を開いてください。");
    }

    // ------------------------------------------------------------------ options

    public IReadOnlyList<int> GridSizes { get; } = [1, 2, 3, 4, 5, 6, 7, 8];
    public IReadOnlyList<Option<FitMode>> FitModes { get; } =
        [new(FitMode.Fit, "全体を収める (Fit)"), new(FitMode.Fill, "隙間なく埋める (Fill)"), new(FitMode.Stretch, "引き伸ばす (Stretch)")];
    public IReadOnlyList<Option<Interpolation>> Interpolations { get; } =
        [new(Interpolation.Lanczos3, "Lanczos（高品質）"), new(Interpolation.Bilinear, "バイリニア"), new(Interpolation.Nearest, "最近傍（ドット絵向け）")];
    public IReadOnlyList<Option<PreviewMode>> PreviewModes { get; } =
        [new(PreviewMode.Original, "元画像"), new(PreviewMode.DirectMap, "地図直接（RGBA）"), new(PreviewMode.BlockMap, "ブロック地図色（予測）"), new(PreviewMode.Difference, "差分")];
    public IReadOnlyList<Option<ColorMetric>> Metrics { get; } =
        [new(ColorMetric.Lab, "CIELAB ΔE76"), new(ColorMetric.Ciede2000, "CIEDE2000（遅い）"), new(ColorMetric.WeightedRgb, "重み付き RGB"), new(ColorMetric.Rgb, "RGB 距離")];
    public IReadOnlyList<Option<DitherMode>> Dithers { get; } =
        [new(DitherMode.None, "なし"), new(DitherMode.FloydSteinberg, "Floyd–Steinberg"), new(DitherMode.Atkinson, "Atkinson"),
         new(DitherMode.JarvisJudiceNinke, "Jarvis–Judice–Ninke"), new(DitherMode.Bayer4, "Bayer 4x4"), new(DitherMode.Bayer8, "Bayer 8x8")];
    public IReadOnlyList<Option<BuildMode>> BuildModes { get; } =
        [new(BuildMode.Flat, "Flat（平面）"), new(BuildMode.Staircase, "Staircase（階段・実験的）")];
    public IReadOnlyList<Option<ItemDelivery>> Deliveries { get; } =
        [new(ItemDelivery.ChestAndFrameWall, "チェスト＋額縁の壁"), new(ItemDelivery.Chest, "チェストのみ"), new(ItemDelivery.FrameWall, "額縁の壁のみ"), new(ItemDelivery.None, "地図データのみ")];
    public IReadOnlyList<Option<int>> Rarities { get; } =
        [new(3, "すべて"), new(2, "非常に希少なものを除く"), new(1, "希少なものを除く"), new(0, "一般的なもののみ")];

    // ------------------------------------------------------------------ project / image

    private string _projectName = "新しいマップアート";
    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }

    private string _sourceInfo = "画像が読み込まれていません";
    public string SourceInfo { get => _sourceInfo; set => Set(ref _sourceInfo, value); }
    private string? _sourcePath;

    private int _mapsX = 1, _mapsY = 1;
    public int MapsX { get => _mapsX; set { if (Set(ref _mapsX, value)) { Raise(nameof(OutputSizeText)); ScheduleUpdate(); } } }
    public int MapsY { get => _mapsY; set { if (Set(ref _mapsY, value)) { Raise(nameof(OutputSizeText)); ScheduleUpdate(); } } }
    public string OutputSizeText => $"{MapsX * 128} x {MapsY * 128} px（地図 {MapsX * MapsY} 枚）" + (MapsX * MapsY > 16 ? "  ※大型出力は実験的です" : "");

    private Option<FitMode> _fit = null!;
    public Option<FitMode> Fit { get => _fit; set { if (Set(ref _fit, value)) ScheduleUpdate(); } }
    private Option<Interpolation> _interp = null!;
    public Option<Interpolation> Interp { get => _interp; set { if (Set(ref _interp, value)) ScheduleUpdate(); } }

    private double _zoom = 1, _offsetX, _offsetY, _brightness, _contrast, _saturation, _gamma = 1, _sharpen;
    public double Zoom { get => _zoom; set { if (Set(ref _zoom, value)) ScheduleUpdate(); } }
    public double OffsetX { get => _offsetX; set { if (Set(ref _offsetX, value)) ScheduleUpdate(); } }
    public double OffsetY { get => _offsetY; set { if (Set(ref _offsetY, value)) ScheduleUpdate(); } }
    public double Brightness { get => _brightness; set { if (Set(ref _brightness, value)) ScheduleUpdate(); } }
    public double Contrast { get => _contrast; set { if (Set(ref _contrast, value)) ScheduleUpdate(); } }
    public double Saturation { get => _saturation; set { if (Set(ref _saturation, value)) ScheduleUpdate(); } }
    public double Gamma { get => _gamma; set { if (Set(ref _gamma, value)) ScheduleUpdate(); } }
    public double Sharpen { get => _sharpen; set { if (Set(ref _sharpen, value)) ScheduleUpdate(); } }

    private string _background = "#FFFFFF";
    public string BackgroundColor { get => _background; set { if (Set(ref _background, value)) ScheduleUpdate(); } }
    private bool _keepTransparency;
    public bool KeepTransparency { get => _keepTransparency; set { if (Set(ref _keepTransparency, value)) ScheduleUpdate(); } }

    // ------------------------------------------------------------------ preview

    private Option<PreviewMode> _previewMode = null!;
    public Option<PreviewMode> SelectedPreviewMode { get => _previewMode; set { if (Set(ref _previewMode, value)) RefreshPreview(); } }
    private bool _showGrid = true;
    public bool ShowGrid { get => _showGrid; set { if (Set(ref _showGrid, value)) RefreshPreview(); } }
    private Bitmap? _preview;
    public Bitmap? Preview { get => _preview; set => Set(ref _preview, value); }
    private string _previewInfo = "";
    public string PreviewInfo { get => _previewInfo; set => Set(ref _previewInfo, value); }

    // ------------------------------------------------------------------ block construction

    public ObservableCollection<ColorProfile> Profiles { get; } = [];
    private ColorProfile? _profile;
    public ColorProfile? SelectedProfile
    {
        get => _profile;
        set
        {
            if (!Set(ref _profile, value) || value is null) return;
            BuildPaletteItems(value, PaletteItems.Count > 0 ? PaletteItems.Where(p => p.IsEnabled).Select(p => p.Block.Key).ToHashSet() : null);
            SupportBlocks = value.Blocks.Where(b => !b.Gravity).ToList();
            SelectedSupportBlock = value.Find(_supportKey) ?? SupportBlocks.FirstOrDefault();
            Raise(nameof(ProfileNote));
            ScheduleUpdate();
        }
    }
    public string ProfileNote => SelectedProfile is null ? "" :
        $"{SelectedProfile.Name}（{SelectedProfile.Blocks.Count(b => b.Verified)}/{SelectedProfile.Blocks.Count} ブロック実測済み）";

    private Option<ColorMetric> _metric = null!;
    public Option<ColorMetric> Metric { get => _metric; set { if (Set(ref _metric, value)) ScheduleUpdate(); } }
    private Option<DitherMode> _dither = null!;
    public Option<DitherMode> Dither { get => _dither; set { if (Set(ref _dither, value)) ScheduleUpdate(); } }
    private Option<BuildMode> _buildMode = null!;
    public Option<BuildMode> BuildModeOption { get => _buildMode; set { if (Set(ref _buildMode, value)) { Raise(nameof(IsStaircase)); ScheduleUpdate(); } } }
    public bool IsStaircase => BuildModeOption.Value == BuildMode.Staircase;

    private bool _excludeGravity = true, _excludeFlammable;
    public bool ExcludeGravity { get => _excludeGravity; set { if (Set(ref _excludeGravity, value)) ScheduleUpdate(); } }
    public bool ExcludeFlammable { get => _excludeFlammable; set { if (Set(ref _excludeFlammable, value)) ScheduleUpdate(); } }
    private Option<int> _rarity = null!;
    public Option<int> MaxRarity { get => _rarity; set { if (Set(ref _rarity, value)) ScheduleUpdate(); } }
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
    public string OriginText => Origin is { } o
        ? $"画像左上 = X {o.X}, Z {o.Z + 1}（支え行 Z {o.Z}）, 高さ Y {o.Y}"
        : "レイアウト座標のみで出力します";

    public ObservableCollection<MaterialViewModel> Materials { get; } = [];
    private string _blockSummary = "";
    public string BlockSummary { get => _blockSummary; set => Set(ref _blockSummary, value); }

    public ObservableCollection<PaletteItemViewModel> PaletteItems { get; } = [];
    private string _paletteSummary = "";
    public string PaletteSummary { get => _paletteSummary; set => Set(ref _paletteSummary, value); }

    // ------------------------------------------------------------------ direct map

    public ObservableCollection<WorldInfo> Worlds { get; } = [];
    private WorldInfo? _world;
    public WorldInfo? SelectedWorld
    {
        get => _world;
        set
        {
            if (!Set(ref _world, value)) return;
            Raise(nameof(WorldDetails));
            WorldMaps.Clear();
            PlanText = "";
            RaiseCommands();
        }
    }
    public string WorldDetails
    {
        get
        {
            if (SelectedWorld is not { } w) return "ワールドを選択してください。";
            var profile = VersionProfile.ForWorld(w.VersionParts);
            return $"フォルダ: {w.Folder}\nバージョン: {w.Version ?? "不明"}\n" +
                   (w.Error != null ? $"読み取りエラー: {w.Error}\n" : "") +
                   (profile is null ? "このバージョンへの書き込みは未対応です（閲覧のみ）。" : $"書き込みプロファイル: {profile.Label}");
        }
    }

    private long _startId = MapIdAllocator.DefaultStart;
    public long StartId { get => _startId; set => Set(ref _startId, value); }
    private Option<ItemDelivery> _delivery = null!;
    public Option<ItemDelivery> Delivery { get => _delivery; set => Set(ref _delivery, value); }
    private bool _glowFrames, _backingWall = true;
    public bool GlowFrames { get => _glowFrames; set => Set(ref _glowFrames, value); }
    public bool BackingWall { get => _backingWall; set => Set(ref _backingWall, value); }
    private string _planText = "";
    public string PlanText { get => _planText; set => Set(ref _planText, value); }

    // ------------------------------------------------------------------ maps viewer / calibration

    public ObservableCollection<MapEntryViewModel> WorldMaps { get; } = [];
    private MapEntryViewModel? _selectedMap;
    public MapEntryViewModel? SelectedMap { get => _selectedMap; set { if (Set(ref _selectedMap, value)) RaiseCommands(); } }
    private int _calX = 10, _calZ = 10, _calY = 100;
    public int CalibrationMapX { get => _calX; set { if (Set(ref _calX, value)) Raise(nameof(CalibrationText)); } }
    public int CalibrationMapZ { get => _calZ; set { if (Set(ref _calZ, value)) Raise(nameof(CalibrationText)); } }
    public int CalibrationY { get => _calY; set { if (Set(ref _calY, value)) Raise(nameof(CalibrationText)); } }
    public string CalibrationText
    {
        get
        {
            var o = WorldOrigin.AlignedToMap(CalibrationMapX, CalibrationMapZ, CalibrationY);
            return $"テストパッドの範囲: X {o.X} ～ {o.X + 127}, Z {o.Z + 1} ～ {o.Z + 128}（Y {o.Y}）。地図の中心は ({128 * CalibrationMapX}, {128 * CalibrationMapZ}) になります。";
        }
    }
    private string _calibrationResult = "";
    public string CalibrationResult { get => _calibrationResult; set => Set(ref _calibrationResult, value); }
    private Dictionary<string, Dictionary<Shade, string>>? _pendingMeasurements;

    // ------------------------------------------------------------------ status

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) RaiseCommands(); } }
    private string _status = "準備完了";
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _logText = "";
    public string LogText { get => _logText; set => Set(ref _logText, value); }

    public void Log(string message)
    {
        void Append() => LogText += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        if (Dispatcher.UIThread.CheckAccess()) Append(); else Dispatcher.UIThread.Post(Append);
    }

    // ------------------------------------------------------------------ commands

    public RelayCommand OpenImageCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand SaveProjectAsCommand { get; }
    public RelayCommand ExportBlockCommand { get; }
    public RelayCommand ExportTilesCommand { get; }
    public RelayCommand RefreshWorldsCommand { get; }
    public RelayCommand BrowseWorldCommand { get; }
    public RelayCommand PlanWorldExportCommand { get; }
    public RelayCommand ExecuteWorldExportCommand { get; }
    public RelayCommand LoadMapsCommand { get; }
    public RelayCommand ExportMapPngCommand { get; }
    public RelayCommand UseMapAsSourceCommand { get; }
    public RelayCommand ExportCalibrationPackCommand { get; }
    public RelayCommand MeasureCalibrationCommand { get; }
    public RelayCommand SaveCalibratedProfileCommand { get; }
    public RelayCommand PaletteAllCommand { get; }
    public RelayCommand PaletteNoneCommand { get; }
    public RelayCommand PaletteDefaultCommand { get; }

    private void RaiseCommands()
    {
        foreach (var c in new[] { ExportBlockCommand, ExportTilesCommand, PlanWorldExportCommand, ExecuteWorldExportCommand,
                     LoadMapsCommand, ExportMapPngCommand, UseMapAsSourceCommand, MeasureCalibrationCommand, SaveCalibratedProfileCommand })
            c?.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ settings <-> core

    private ImageSettings CurrentImageSettings() => new()
    {
        MapsX = MapsX, MapsY = MapsY, Fit = Fit.Value, Interpolation = Interp.Value, Zoom = Zoom,
        OffsetX = OffsetX, OffsetY = OffsetY, Brightness = Brightness, Contrast = Contrast, Saturation = Saturation,
        Gamma = Gamma, Sharpen = Sharpen, BackgroundColor = SafeColor(BackgroundColor), KeepTransparency = KeepTransparency,
    };

    private static string SafeColor(string s)
    {
        try { Rgba.FromHex(s); return s; } catch { return "#FFFFFF"; }
    }

    private PaletteSelection CurrentSelection() => new()
    {
        EnabledKeys = PaletteItems.Where(p => p.IsEnabled).Select(p => p.Block.Key).ToHashSet(),
        ExcludeGravity = ExcludeGravity, ExcludeFlammable = ExcludeFlammable, MaxRarity = MaxRarity.Value,
        MaxBlockTypes = MaxBlockTypes,
    };

    private QuantizeOptions CurrentQuantizeOptions() => new() { Metric = Metric.Value, Dither = Dither.Value, Mode = BuildModeOption.Value };

    // ------------------------------------------------------------------ preview pipeline

    private void ScheduleUpdate()
    {
        if (_loadingProject) return;
        if (_debounce is null)
        {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = UpdateAsync(); };
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task UpdateAsync()
    {
        if (_source is null || SelectedProfile is null) return;
        _updateCts?.Cancel();
        var cts = _updateCts = new CancellationTokenSource();
        var source = _source;
        var settings = CurrentImageSettings();
        var profile = SelectedProfile;
        var selection = CurrentSelection();
        var qopt = CurrentQuantizeOptions();
        Status = "変換中…";
        try
        {
            var (processed, q, error) = await Task.Run(() =>
            {
                var p = ImagePipeline.Process(source, settings, cts.Token);
                QuantizeResult? qr = null;
                string? err = null;
                try { qr = ColorQuantizer.Quantize(p, profile, selection, qopt, cts.Token); }
                catch (InvalidOperationException ex) { err = ex.Message; }
                return (p, qr, err);
            }, cts.Token);
            if (cts.IsCancellationRequested) return;
            _processed = processed;
            _quantized = q;
            if (error != null) Log(error);
            UpdateBlockInfo();
            RefreshPreview();
            Status = "準備完了";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = "エラー";
            Log("変換に失敗しました: " + ex.Message);
        }
        finally { RaiseCommands(); }
    }

    private void UpdateBlockInfo()
    {
        Materials.Clear();
        if (_quantized is null || SelectedProfile is null || _processed is null)
        {
            BlockSummary = "";
            return;
        }
        try
        {
            var layout = BlockLayout.Generate(_quantized, SelectedSupportBlock ?? SelectedProfile.Blocks[0]);
            var materials = MaterialCalculator.Calculate(layout, MapsX);
            foreach (var m in materials) Materials.Add(new MaterialViewModel(m, SelectedProfile));
            double err = MeanDeltaE(_processed, _quantized.Preview);
            int types = _quantized.EntryIndex.Where(e => e >= 0).Distinct().Count();
            int unverified = _quantized.EntryIndex.Where(e => e >= 0).Distinct().Count(e => !_quantized.Entries[e].Block.Verified);
            var sb = new StringBuilder();
            sb.Append($"使用ブロック {types} 種 / 合計 {materials.Sum(m => m.Count):N0} 個（支え {materials.Where(m => m.IsSupport).Sum(m => m.Count)}）");
            sb.Append($"\n平均色差 ΔE {err:0.0}");
            if (layout.Mode == BuildMode.Staircase) sb.Append($" / 最大高さ差 {layout.MaxHeight}");
            if (unverified > 0) sb.Append($"\n※ {unverified} 種の地図色は実機未検証の推定値です。");
            BlockSummary = sb.ToString();
        }
        catch (Exception ex) { BlockSummary = ex.Message; }
    }

    private static double MeanDeltaE(RgbaImage a, RgbaImage b)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < a.Pixels.Length; i += 4 * 7) // sampled for speed
        {
            var la = ColorMath.ToLab(a.Pixels[i], a.Pixels[i + 1], a.Pixels[i + 2]);
            var lb = ColorMath.ToLab(b.Pixels[i], b.Pixels[i + 1], b.Pixels[i + 2]);
            sum += Math.Sqrt(ColorMath.DeltaE76(la, lb));
            n++;
        }
        return n == 0 ? 0 : sum / n;
    }

    private void RefreshPreview()
    {
        RgbaImage? img = SelectedPreviewMode.Value switch
        {
            PreviewMode.Original => _sourceThumb,
            PreviewMode.DirectMap => _processed,
            PreviewMode.BlockMap => _quantized?.Preview,
            PreviewMode.Difference => _processed is null || _quantized is null ? null : Difference(_processed, _quantized.Preview),
            _ => null,
        };
        if (img is null)
        {
            Preview = null;
            PreviewInfo = _source is null ? "画像を開いてください" : "";
            return;
        }
        if (SelectedPreviewMode.Value != PreviewMode.Original)
        {
            // Show small outputs larger so individual pixels are visible.
            int factor = Math.Max(1, 1024 / Math.Max(img.Width, img.Height));
            if (factor > 1) img = img.ScaleNearest(Math.Min(factor, 6));
            else img = img.Clone();
            if (ShowGrid) DrawGrid(img, img.Width / (MapsX * 128));
        }
        Preview = BitmapHelper.ToBitmap(img);
        PreviewInfo = SelectedPreviewMode.Value switch
        {
            PreviewMode.Original => $"元画像 {_source!.Width} x {_source.Height}",
            PreviewMode.DirectMap => "地図データへそのまま書き込む RGBA 画像",
            PreviewMode.BlockMap => "ブロックの地図色による予測（実際のブロックの見た目とは異なります）",
            _ => "元画像との差（明るいほど差が大きい）",
        };
    }

    private void DrawGrid(RgbaImage img, int scale)
    {
        if (scale <= 0) return;
        var red = new Rgba(230, 40, 40);
        var orange = new Rgba(255, 170, 0, 255);
        for (int x = 0; x < img.Width; x++)
            for (int y = 0; y < img.Height; y++)
            {
                int bx = x / scale, by = y / scale;
                bool onX = x % scale == 0, onY = y % scale == 0;
                if ((onX && bx % 128 == 0 && bx > 0) || (onY && by % 128 == 0 && by > 0)) img[x, y] = red;
                else if (scale >= 2 && ((onX && bx % 64 == 0 && bx > 0) || (onY && by % 64 == 0 && by > 0))) img[x, y] = orange;
            }
    }

    private static RgbaImage Difference(RgbaImage a, RgbaImage b)
    {
        var d = new RgbaImage(a.Width, a.Height);
        for (int i = 0; i < a.Pixels.Length; i += 4)
        {
            int v = (Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2])) * 2;
            byte c = (byte)Math.Min(255, v);
            d.Pixels[i] = d.Pixels[i + 1] = d.Pixels[i + 2] = c;
            d.Pixels[i + 3] = 255;
        }
        return d;
    }

    // ------------------------------------------------------------------ image / project commands

    private async Task OpenImageAsync()
    {
        var path = await _dialogs.OpenFileAsync("画像を開く", "画像", ImageCodec.SupportedExtensions.Select(e => "*" + e).ToArray());
        if (path != null) await LoadImageAsync(path);
    }

    public async Task LoadImageAsync(string path)
    {
        try
        {
            IsBusy = true;
            var img = await Task.Run(() => ImageCodec.Load(path));
            SetSource(img, path);
            if (ProjectName == "新しいマップアート") ProjectName = Path.GetFileNameWithoutExtension(path);
            Log($"画像を読み込みました: {Path.GetFileName(path)} ({img.Width}x{img.Height})");
        }
        catch (Exception ex) { Log("画像を開けませんでした: " + ex.Message); }
        finally { IsBusy = false; }
    }

    private void SetSource(RgbaImage img, string? path)
    {
        _source = img;
        _sourcePath = path;
        _sourceThumb = ImagePipeline.Thumbnail(img, 1024);
        SourceInfo = path is null ? $"地図から読み込み ({img.Width}x{img.Height})" : $"{Path.GetFileName(path)} ({img.Width}x{img.Height})";
        if (SelectedPreviewMode.Value == PreviewMode.Original) SelectedPreviewMode = PreviewModes[1];
        ScheduleUpdate();
        RefreshPreview();
    }

    private void NewProject()
    {
        _projectPath = null;
        ApplyProject(new BmagProject());
        _source = _sourceThumb = _processed = null;
        _quantized = null;
        _sourcePath = null;
        SourceInfo = "画像が読み込まれていません";
        Materials.Clear();
        BlockSummary = "";
        RefreshPreview();
        Log("新しいプロジェクトを作成しました。");
    }

    private async Task OpenProjectAsync()
    {
        var path = await _dialogs.OpenFileAsync("プロジェクトを開く", "BMAG プロジェクト", ["*.bmag.json", "*.json"]);
        if (path is null) return;
        try
        {
            var p = BmagProject.Load(path);
            _projectPath = path;
            ApplyProject(p);
            var img = p.ResolveImagePath(path);
            if (img != null && File.Exists(img))
            {
                if (p.SourceImageHash != null && BmagProject.HashFile(img) != p.SourceImageHash)
                    Log("注意: 画像ファイルが保存時から変更されています。");
                await LoadImageAsync(img);
            }
            else if (img != null) Log($"画像が見つかりません: {img}");
            Log($"プロジェクトを開きました: {path}");
        }
        catch (Exception ex) { Log("プロジェクトを開けませんでした: " + ex.Message); }
    }

    private async Task SaveProjectAsync(bool saveAs)
    {
        string? path = _projectPath;
        if (saveAs || path is null)
        {
            path = await _dialogs.SaveFileAsync("プロジェクトを保存", StructureExporter.Slug(ProjectName) + ".bmag.json", "json");
            if (path is null) return;
        }
        try
        {
            var p = ToProject();
            p.Save(path);
            _projectPath = path;
            Log($"プロジェクトを保存しました: {path}");
        }
        catch (Exception ex) { Log("保存に失敗しました: " + ex.Message); }
    }

    private BmagProject ToProject()
    {
        var s = CurrentImageSettings();
        return new BmagProject
        {
            ProjectName = ProjectName,
            SourceImagePath = _sourcePath,
            SourceImageHash = _sourcePath != null && File.Exists(_sourcePath) ? BmagProject.HashFile(_sourcePath) : null,
            Image = s,
            ColorProfileId = SelectedProfile?.Id ?? "bedrock-estimate-v1",
            Metric = Metric.Value, Dither = Dither.Value, BuildMode = BuildModeOption.Value,
            EnabledBlocks = PaletteItems.Where(p => p.IsEnabled).Select(p => p.Block.Key).ToList(),
            ExcludeGravity = ExcludeGravity, ExcludeFlammable = ExcludeFlammable, MaxRarity = MaxRarity.Value, MaxBlockTypes = MaxBlockTypes,
            SupportBlockKey = _supportKey,
            OriginX = UseOrigin ? OriginMapX : null, OriginZ = UseOrigin ? OriginMapZ : null, OriginY = OriginY,
            LastWorldFolder = SelectedWorld?.Folder, StartMapId = StartId, Delivery = Delivery.Value, GlowFrames = GlowFrames,
        };
    }

    private void ApplyProject(BmagProject p)
    {
        _loadingProject = true;
        try
        {
            ProjectName = p.ProjectName;
            var s = p.Image;
            MapsX = s.MapsX; MapsY = s.MapsY;
            Fit = FitModes.First(f => f.Value == s.Fit);
            Interp = Interpolations.First(f => f.Value == s.Interpolation);
            Zoom = s.Zoom; OffsetX = s.OffsetX; OffsetY = s.OffsetY;
            Brightness = s.Brightness; Contrast = s.Contrast; Saturation = s.Saturation; Gamma = s.Gamma; Sharpen = s.Sharpen;
            BackgroundColor = s.BackgroundColor; KeepTransparency = s.KeepTransparency;
            Metric = Metrics.First(m => m.Value == p.Metric);
            Dither = Dithers.First(d => d.Value == p.Dither);
            BuildModeOption = BuildModes.First(b => b.Value == p.BuildMode);
            ExcludeGravity = p.ExcludeGravity; ExcludeFlammable = p.ExcludeFlammable;
            MaxRarity = Rarities.FirstOrDefault(r => r.Value == p.MaxRarity) ?? Rarities[0];
            MaxBlockTypes = p.MaxBlockTypes;
            _supportKey = p.SupportBlockKey;
            var profile = Profiles.FirstOrDefault(x => x.Id == p.ColorProfileId) ?? Profiles.FirstOrDefault();
            _profile = null;
            SelectedProfile = profile;
            if (profile != null) BuildPaletteItems(profile, p.EnabledBlocks?.ToHashSet());
            UseOrigin = p.OriginX != null;
            OriginMapX = p.OriginX ?? 0; OriginMapZ = p.OriginZ ?? 0; OriginY = p.OriginY ?? 64;
            StartId = p.StartMapId;
            Delivery = Deliveries.First(d => d.Value == p.Delivery);
            GlowFrames = p.GlowFrames;
            if (p.LastWorldFolder != null) SelectedWorld = Worlds.FirstOrDefault(w => w.Folder == p.LastWorldFolder) ?? SelectedWorld;
        }
        finally { _loadingProject = false; }
        ScheduleUpdate();
    }

    // ------------------------------------------------------------------ palette

    private void LoadProfiles()
    {
        _fit = FitModes[0];
        _interp = Interpolations[0];
        _previewMode = PreviewModes[0];
        _metric = Metrics[0];
        _dither = Dithers[0];
        _buildMode = BuildModes[0];
        _delivery = Deliveries[0];
        _rarity = Rarities[0];
        Profiles.Clear();
        foreach (var id in ColorProfile.BuiltInIds()) Profiles.Add(ColorProfile.LoadBuiltIn(id));
        foreach (var p in Calibrator.LoadUserProfiles()) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void BuildPaletteItems(ColorProfile profile, HashSet<string>? enabled)
    {
        PaletteItems.Clear();
        foreach (var b in profile.Blocks)
            PaletteItems.Add(new PaletteItemViewModel(b, profile, enabled?.Contains(b.Key) ?? b.DefaultEnabled, OnPaletteChanged));
        UpdatePaletteSummary();
    }

    private void OnPaletteChanged()
    {
        UpdatePaletteSummary();
        ScheduleUpdate();
    }

    private void SetAllPalette(Func<BlockDefinition, bool> pick)
    {
        foreach (var p in PaletteItems) p.SetSilently(pick(p.Block));
        OnPaletteChanged();
    }

    private void UpdatePaletteSummary() =>
        PaletteSummary = $"{PaletteItems.Count(p => p.IsEnabled)} / {PaletteItems.Count} ブロックを使用（重力・可燃・希少度の制限はブロック建築タブで設定）";

    // ------------------------------------------------------------------ block export

    private async Task ExportBlockAsync()
    {
        if (_quantized is null || SelectedProfile is null) return;
        var folder = await _dialogs.PickFolderAsync("建築データの出力先フォルダ");
        if (folder is null) return;
        IsBusy = true;
        Status = "建築データを出力中…";
        try
        {
            var q = _quantized;
            var profile = SelectedProfile;
            var support = SelectedSupportBlock ?? profile.Blocks[0];
            string name = ProjectName;
            int mx = MapsX, my = MapsY;
            var origin = Origin;
            string outDir = Path.Combine(folder, $"BMAG_{StructureExporter.Slug(name)}_build");
            var result = await Task.Run(() =>
            {
                var layout = BlockLayout.Generate(q, support, maxHeight: 380);
                var materials = MaterialCalculator.Calculate(layout, mx);
                BuildGuideGenerator.WriteAll(Path.Combine(outDir, "guide"), layout, q, profile, materials, mx, my, origin, name);
                return StructureExporter.Export(outDir, name, layout, mx, new StructureExportOptions(), origin);
            });
            Log($"建築データを出力しました: {outDir}");
            Log($"  構造物 {result.Tiles.Count} 個（{result.Namespace}）, ビヘイビアパック: {Path.GetFileName(result.McpackPath)}");
            Log($"  ゲーム内: /function bmag/{result.Slug}/all （実行位置 = 支え行の北西角）");
        }
        catch (Exception ex) { Log("建築データの出力に失敗しました: " + ex.Message); }
        finally { IsBusy = false; Status = "準備完了"; }
    }

    private async Task ExportTilesAsync()
    {
        if (_processed is null) return;
        var folder = await _dialogs.PickFolderAsync("地図画像 (PNG) の出力先フォルダ");
        if (folder is null) return;
        try
        {
            string dir = Path.Combine(folder, $"BMAG_{StructureExporter.Slug(ProjectName)}_maps");
            var tiles = TileSplitter.Split(_processed, MapsX, MapsY);
            await Task.Run(() =>
            {
                ImageCodec.SavePng(_processed, Path.Combine(dir, "full.png"));
                foreach (var t in tiles) ImageCodec.SavePng(t.Image, Path.Combine(dir, $"map_{t.Index + 1:00}_x{t.GridX}_y{t.GridY}.png"));
            });
            Log($"地図画像 {tiles.Count} 枚を出力しました: {dir}");
        }
        catch (Exception ex) { Log("PNG 出力に失敗しました: " + ex.Message); }
    }

    // ------------------------------------------------------------------ worlds

    private void RefreshWorlds()
    {
        var current = SelectedWorld?.Folder;
        Worlds.Clear();
        try
        {
            foreach (var w in WorldLocator.FindWorlds()) Worlds.Add(w);
            Log($"ワールドを {Worlds.Count} 件検出しました。");
        }
        catch (Exception ex) { Log("ワールドの検出に失敗しました: " + ex.Message); }
        SelectedWorld = Worlds.FirstOrDefault(w => w.Folder == current) ?? Worlds.FirstOrDefault();
    }

    private async Task BrowseWorldAsync()
    {
        var folder = await _dialogs.PickFolderAsync("ワールドフォルダ（level.dat と db があるフォルダ）を選択");
        if (folder is null) return;
        if (!WorldInfo.LooksLikeWorld(folder))
        {
            Log("選択したフォルダは Bedrock のワールドではありません（level.dat と db が必要です）。");
            return;
        }
        var w = WorldInfo.Load(folder, "手動選択");
        Worlds.Insert(0, w);
        SelectedWorld = w;
    }

    private WorldExportOptions CurrentExportOptions() => new()
    {
        ProjectName = ProjectName, StartId = StartId, Delivery = Delivery.Value, GlowFrames = GlowFrames, BackingWall = BackingWall,
    };

    private WorldExportPlan? BuildPlan()
    {
        if (_processed is null || SelectedWorld is null) return null;
        var tiles = TileSplitter.Split(_processed, MapsX, MapsY);
        return SafeWorldExporter.Plan(SelectedWorld, tiles, MapsX, MapsY, CurrentExportOptions());
    }

    private void PlanWorldExport()
    {
        try
        {
            var plan = BuildPlan();
            PlanText = plan?.Describe() ?? "";
            if (KeepTransparency) PlanText += "\n注意: 透明部分が実際の地図でどう表示されるかは未検証です。";
        }
        catch (Exception ex) { PlanText = "計画を作成できません: " + ex.Message; }
    }

    private async Task ExecuteWorldExportAsync()
    {
        WorldExportPlan? plan;
        try { plan = BuildPlan(); }
        catch (Exception ex) { Log("計画を作成できません: " + ex.Message); PlanText = ex.Message; return; }
        if (plan is null) return;
        PlanText = plan.Describe();
        if (!await _dialogs.ConfirmAsync("ワールドのコピーに地図を追加",
                plan.Describe() + "\n元のワールドは変更せず、コピーを作成してそこに書き込みます。実行しますか？")) return;

        IsBusy = true;
        Status = "ワールドのコピーを作成中…";
        try
        {
            var progress = new Progress<string>(Log);
            var result = await Task.Run(() => SafeWorldExporter.Execute(plan, progress));
            Log($"地図を追加したワールドコピーを作成しました: {result.Destination}");
            PlanText = result.Instructions + $"\nログ: {result.LogPath}";
            RefreshWorlds();
        }
        catch (Exception ex) { Log("ワールドへの出力に失敗しました（元ワールドは変更されていません）: " + ex.Message); }
        finally { IsBusy = false; Status = "準備完了"; }
    }

    // ------------------------------------------------------------------ map viewer / calibration

    private async Task LoadMapsAsync()
    {
        if (SelectedWorld is null) return;
        if (WorldLocator.IsMinecraftRunning()) Log("注意: Minecraft が起動中です。表示内容が最新でない可能性があります（読み取りのみ行います）。");
        IsBusy = true;
        try
        {
            var folder = SelectedWorld.Folder;
            var records = await Task.Run(() =>
            {
                using var db = LevelDbDatabase.Open(Path.Combine(folder, "db"), readOnly: true);
                var list = new List<(MapRecord, RgbaImage?)>();
                foreach (var kv in db.Scan("map_"))
                {
                    if (!MapDataEncoder.TryParseKey(Encoding.ASCII.GetString(kv.Key), out long id)) continue;
                    try
                    {
                        var r = MapRecord.Parse(id, kv.Value);
                        list.Add((r, r.ToImage()));
                    }
                    catch { /* skip unreadable records */ }
                }
                return list;
            });
            WorldMaps.Clear();
            foreach (var (r, img) in records.OrderBy(r => r.Item1.Id))
                WorldMaps.Add(new MapEntryViewModel(r, img is null ? null : BitmapHelper.ToBitmap(img)));
            Log($"地図を {WorldMaps.Count} 件読み込みました（{SelectedWorld.Name}）。");
        }
        catch (Exception ex) { Log("地図を読み込めませんでした: " + ex.Message); }
        finally { IsBusy = false; }
    }

    private async Task ExportMapPngAsync()
    {
        if (SelectedMap?.Record.ToImage() is not { } img) { Log("この地図には画像データがありません。"); return; }
        var path = await _dialogs.SaveFileAsync("地図を PNG で保存", SelectedMap.Title + ".png", "png");
        if (path is null) return;
        ImageCodec.SavePng(img, path);
        Log($"保存しました: {path}");
    }

    private void UseMapAsSource()
    {
        if (SelectedMap?.Record.ToImage() is not { } img) return;
        MapsX = MapsY = 1;
        SetSource(img, null);
        Log($"{SelectedMap.Title} を元画像として読み込みました。");
    }

    private async Task ExportCalibrationPackAsync()
    {
        if (SelectedProfile is null) return;
        var folder = await _dialogs.PickFolderAsync("カリブレーション用パックの出力先");
        if (folder is null) return;
        try
        {
            var support = SelectedProfile.Find("smooth_stone") ?? SelectedProfile.Blocks[0];
            var layout = CalibrationPad.CreateLayout(SelectedProfile, support);
            var origin = WorldOrigin.AlignedToMap(CalibrationMapX, CalibrationMapZ, CalibrationY);
            string dir = Path.Combine(folder, "BMAG_calibration");
            var result = await Task.Run(() => StructureExporter.Export(dir, "calibration", layout, 1, new StructureExportOptions(), origin));
            Log($"カリブレーション用パックを出力しました: {result.McpackPath}");
            CalibrationResult =
                "手順:\n" +
                "1. .mcpack をインポートし、チートを有効にした平らなクリエイティブワールド（またはそのコピー）で有効にします。\n" +
                $"2. /function bmag/calibration/all_absolute を実行します（{CalibrationText}）。\n" +
                "3. パッドの範囲内に立ち、白紙の地図を使って地図を作り、全体が描かれるまで持ったままにします（拡大しないでください）。\n" +
                "4. ゲームを終了し、このタブでそのワールドを選んで「地図を読み込む」→ 中心が一致する地図を選び「選択した地図で測定」を押します。";
        }
        catch (Exception ex) { Log("カリブレーション用パックの出力に失敗しました: " + ex.Message); }
    }

    private void MeasureCalibration()
    {
        if (SelectedMap is null || SelectedProfile is null) return;
        var map = SelectedMap.Record;
        var sb = new StringBuilder();
        if (!Calibrator.CoversGridCell(map, CalibrationMapX, CalibrationMapZ))
            sb.AppendLine($"警告: この地図（縮尺 {map.Scale}, 中心 {map.XCenter},{map.ZCenter}）は設定したパッドの位置と一致しません。");
        try
        {
            var samples = Calibrator.Measure(map, SelectedProfile);
            _pendingMeasurements = Calibrator.ToMeasurements(samples);
            var ok = samples.Where(s => s.Measured != null && s.Problem == null).ToList();
            sb.AppendLine($"測定: {ok.Count} / {samples.Count} 件（ブロック {_pendingMeasurements.Count} 種）");
            if (ok.Count > 0) sb.AppendLine($"推定値との平均色差 ΔE2000: {ok.Average(s => s.DeltaE):0.00}, 最大 {ok.Max(s => s.DeltaE):0.00}");
            foreach (var s in samples.OrderByDescending(s => double.IsNaN(s.DeltaE) ? 999 : s.DeltaE).Take(25))
                sb.AppendLine($"  {s.Block.DisplayName} [{s.Shade}] 推定 {s.Estimated.ToHex()} → 実測 {s.Measured?.ToHex() ?? "-"}  ΔE {s.DeltaE:0.0} {s.Problem}");
            sb.AppendLine("問題がなければ「実測プロファイルとして保存」を押してください。");
        }
        catch (Exception ex) { sb.AppendLine("測定できません: " + ex.Message); _pendingMeasurements = null; }
        CalibrationResult = sb.ToString();
        RaiseCommands();
    }

    private void SaveCalibratedProfile()
    {
        if (_pendingMeasurements is null || SelectedProfile is null) return;
        string id = $"bedrock-calibrated-{DateTime.Now:yyyyMMdd-HHmmss}";
        string version = SelectedWorld?.Version ?? "?";
        var profile = SelectedProfile.WithMeasurements(_pendingMeasurements, id, $"実測プロファイル {DateTime.Now:yyyy-MM-dd HH:mm}（{version}）");
        var path = Calibrator.SaveUserProfile(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        Log($"実測プロファイルを保存しました: {path}");
        _pendingMeasurements = null;
        RaiseCommands();
    }
}
