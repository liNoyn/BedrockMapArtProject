using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BMAG.BlockEngine;
using BMAG.ColorEngine;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.Palette;
using BMAG.Preview;
using BMAG.Project;
using BMAG.Textures;

namespace BMAG.App.ViewModels;

public interface IDialogService
{
    Task<string?> OpenFileAsync(string title, string filterName, string[] patterns);
    Task<string?> SaveFileAsync(string title, string suggestedName, string extension);
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> PromptAsync(string title, string message, string initial);
    Task InfoAsync(string title, string message);
}

public enum PreviewMode { Original, DirectMap, BlockMap, Difference, Build2D, Build3D }

/// <summary>Main window state. Split into partial files by area (palette, build, world).</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IDialogService _dialogs;
    private readonly AppSettings _settings = AppSettings.Load();
    private CancellationTokenSource? _updateCts;
    private DispatcherTimer? _debounce;
    private RgbaImage? _source;
    private RgbaImage? _sourceThumb;
    private RgbaImage? _processed;
    private QuantizeResult? _baseQuantized;
    private QuantizeResult? _quantized;
    private BlockLayout? _layout;
    private PixelOverrides _edits = new();
    private string? _projectPath;
    private bool _loadingProject;
    private BmagProject _project = new();

    public Loc L { get; } = new();
    public BlockTextureProvider Textures { get; }

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        L.English = _settings.English;
        Textures = new BlockTextureProvider();
        if (_settings.ResourcePack is { } rp && Directory.Exists(rp))
        {
            try { Textures.UseResourcePack(rp); } catch { /* ignore broken settings */ }
        }

        FitModes = [new(FitMode.Fit, "FitFit", L), new(FitMode.Fill, "FitFill", L), new(FitMode.Stretch, "FitStretch", L)];
        Interpolations = [new(Interpolation.Lanczos3, "InterpLanczos", L), new(Interpolation.Bilinear, "InterpBilinear", L), new(Interpolation.Nearest, "InterpNearest", L)];
        CropModes = [new(CropMode.Off, "CropOff", L), new(CropMode.Center, "CropCenter", L), new(CropMode.Manual, "CropManual", L)];
        TransparencyModes = [new(TransparencyMode.Smooth, "TransSmooth", L), new(TransparencyMode.Dithered, "TransDithered", L), new(TransparencyMode.Keep, "TransKeep", L)];
        PreviewModes =
        [
            new(PreviewMode.Original, "PvOriginal", L), new(PreviewMode.DirectMap, "PvDirect", L), new(PreviewMode.BlockMap, "PvBlockMap", L),
            new(PreviewMode.Difference, "PvDifference", L), new(PreviewMode.Build2D, "PvBuild2D", L), new(PreviewMode.Build3D, "PvBuild3D", L),
        ];
        _fit = FitModes[0];
        _interp = Interpolations[0];
        _cropMode = CropModes[0];
        _transparency = TransparencyModes[0];
        _previewMode = PreviewModes[0];

        OpenImageCommand = new RelayCommand(OpenImageAsync);
        ReloadFrameCommand = new RelayCommand(ReloadFrameAsync, () => _sourcePath != null);
        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(OpenProjectAsync);
        SaveProjectCommand = new RelayCommand(() => SaveProjectAsync(false));
        SaveProjectAsCommand = new RelayCommand(() => SaveProjectAsync(true));
        SetJapaneseCommand = new RelayCommand(() => SetLanguage(false));
        SetEnglishCommand = new RelayCommand(() => SetLanguage(true));
        RotateCommand = new RelayCommand(() => { Rotation = (Rotation + 1) % 4; });
        AboutCommand = new RelayCommand(ShowAboutAsync);
        OpenLogsCommand = new RelayCommand(() => OpenFolder(LogDirectory));

        InitPalette();
        InitBuild();
        InitWorld();
        L.PropertyChanged += (_, _) => { RaiseLocalizedTexts(); RefreshPreview(); };
        Log(L.English ? "Bedrock Map Art Generator started. Open an image." : "Bedrock Map Art Generator を起動しました。画像を開いてください。");
        if (!Textures.IsAvailable && !_settings.TexturesAsked)
            Dispatcher.UIThread.Post(async () => await OfferTexturesAsync(), DispatcherPriority.Background);
    }

    public static string Version =>
        typeof(MainViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public static string LogDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BMAG", "logs");

    public RelayCommand AboutCommand { get; }
    public RelayCommand OpenLogsCommand { get; }

    private async Task OfferTexturesAsync()
    {
        _settings.TexturesAsked = true;
        _settings.Save();
        if (!await _dialogs.ConfirmAsync(L["DownloadTextures"], L["FirstRunTextures"])) return;
        _settings.TexturesAccepted = true;
        _settings.Save();
        await DownloadTexturesAsync();
    }

    private Task ShowAboutAsync() => _dialogs.InfoAsync(L["About"], L.English
        ? $"Bedrock Map Art Generator {Version}\nhttps://github.com/liNoyn/BedrockMapArtGenerator\n\n" +
          "Map art for Minecraft Bedrock Edition: .mcstructure builds and direct map data.\n\n" +
          "License: MIT. Inspired by MapartCraft (rebane2001, GPL-3.0; no code copied) and Image Map (tryashtar).\n" +
          "Block textures are © Mojang AB, downloaded to your PC from Mojang/bedrock-samples under the Minecraft EULA.\n" +
          "Minecraft is a trademark of Mojang Studios / Microsoft. This project is not affiliated with them.\n\n" +
          $"Logs: {LogDirectory}"
        : $"Bedrock Map Art Generator {Version}\nhttps://github.com/liNoyn/BedrockMapArtGenerator\n\n" +
          "Minecraft 統合版のマップアート作成ツール（.mcstructure 建築データと地図データの直接生成）。\n\n" +
          "ライセンス: MIT。MapartCraft（rebane2001、GPL-3.0。コードは含みません）と Image Map（tryashtar）を参考にしています。\n" +
          "ブロックのテクスチャは © Mojang AB で、Mojang/bedrock-samples から Minecraft EULA に基づいて PC にダウンロードしたものです。\n" +
          "Minecraft は Mojang Studios / Microsoft の商標です。本プロジェクトはこれらと関係ありません。\n\n" +
          $"ログ: {LogDirectory}");

    /// <summary>Sets a manual crop from a rectangle dragged on the Original view (fractions of the shown image).</summary>
    public void SetCropFromFractions(double x0, double y0, double x1, double y1)
    {
        if (_source is null) return;
        double ax = Math.Clamp(Math.Min(x0, x1), 0, 1), bx = Math.Clamp(Math.Max(x0, x1), 0, 1);
        double ay = Math.Clamp(Math.Min(y0, y1), 0, 1), by = Math.Clamp(Math.Max(y0, y1), 0, 1);
        int w = (int)Math.Round((bx - ax) * _source.Width), h = (int)Math.Round((by - ay) * _source.Height);
        if (w < 4 || h < 4) return;
        _loadingProject = true;
        try
        {
            CropModeOption = CropModes.First(c => c.Value == CropMode.Manual);
            CropX = (int)Math.Round(ax * _source.Width);
            CropY = (int)Math.Round(ay * _source.Height);
            CropW = w;
            CropH = h;
        }
        finally { _loadingProject = false; }
        RefreshPreview();
        ScheduleUpdate();
        Log(L.English ? $"Crop: {CropX},{CropY} {CropW}x{CropH}" : $"切り抜き: {CropX},{CropY} {CropW}x{CropH}");
    }

    // ------------------------------------------------------------------ language

    public RelayCommand SetJapaneseCommand { get; }
    public RelayCommand SetEnglishCommand { get; }

    private void SetLanguage(bool english)
    {
        L.English = english;
        _settings.English = english;
        _settings.Save();
    }

    private void RaiseLocalizedTexts()
    {
        Raise(nameof(OutputSizeText));
        Raise(nameof(OriginText));
        Raise(nameof(ProfileNote));
        Raise(nameof(TextureStatus));
        Raise(nameof(WorldDetails));
        Raise(nameof(CalibrationText));
        if (_source is null) SourceInfo = L["NoImage"];
        UpdateBlockInfo();
        RebuildViewAreas();
        ApplyLabels();
        UpdatePaletteSummary();
    }

    // ------------------------------------------------------------------ image options

    public IReadOnlyList<int> GridSizes { get; } = [1, 2, 3, 4, 5, 6, 7, 8];
    public IReadOnlyList<LocOption<FitMode>> FitModes { get; }
    public IReadOnlyList<LocOption<Interpolation>> Interpolations { get; }
    public IReadOnlyList<LocOption<CropMode>> CropModes { get; }
    public IReadOnlyList<LocOption<TransparencyMode>> TransparencyModes { get; }
    public IReadOnlyList<LocOption<PreviewMode>> PreviewModes { get; }

    private string _projectName = "新しいマップアート";
    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }

    private string _sourceInfo = "画像が読み込まれていません";
    public string SourceInfo { get => _sourceInfo; set => Set(ref _sourceInfo, value); }
    private string? _sourcePath;

    private int _frameCount = 1, _frame;
    private double _videoSeconds;
    public int FrameCount { get => _frameCount; set { if (Set(ref _frameCount, value)) { Raise(nameof(HasFrames)); Raise(nameof(MaxFrame)); } } }
    public int MaxFrame => Math.Max(0, FrameCount - 1);
    public bool HasFrames => FrameCount > 1;
    private bool _isVideo;
    public bool IsVideo { get => _isVideo; set => Set(ref _isVideo, value); }
    public int Frame { get => _frame; set => Set(ref _frame, Math.Clamp(value, 0, MaxFrame)); }
    public double VideoSeconds { get => _videoSeconds; set => Set(ref _videoSeconds, Math.Max(0, value)); }

    private int _mapsX = 1, _mapsY = 1;
    public int MapsX { get => _mapsX; set { if (Set(ref _mapsX, value)) { Raise(nameof(OutputSizeText)); ScheduleUpdate(); } } }
    public int MapsY { get => _mapsY; set { if (Set(ref _mapsY, value)) { Raise(nameof(OutputSizeText)); ScheduleUpdate(); } } }
    public string OutputSizeText => L.English
        ? $"{MapsX * 128} x {MapsY * 128} px ({MapsX * MapsY} maps)" + (MapsX * MapsY > 16 ? "  * large outputs are experimental" : "")
        : $"{MapsX * 128} x {MapsY * 128} px（地図 {MapsX * MapsY} 枚）" + (MapsX * MapsY > 16 ? "  ※大型出力は実験的です" : "");

    private LocOption<FitMode> _fit;
    public LocOption<FitMode> Fit { get => _fit; set { if (value != null && Set(ref _fit, value)) ScheduleUpdate(); } }
    private LocOption<Interpolation> _interp;
    public LocOption<Interpolation> Interp { get => _interp; set { if (value != null && Set(ref _interp, value)) ScheduleUpdate(); } }
    private LocOption<CropMode> _cropMode;
    public LocOption<CropMode> CropModeOption
    {
        get => _cropMode;
        set { if (value != null && Set(ref _cropMode, value)) { Raise(nameof(IsManualCrop)); ScheduleUpdate(); } }
    }
    public bool IsManualCrop => CropModeOption.Value == CropMode.Manual;
    private int _cropX, _cropY, _cropW, _cropH;
    public int CropX { get => _cropX; set { if (Set(ref _cropX, Math.Max(0, value))) ScheduleUpdate(); } }
    public int CropY { get => _cropY; set { if (Set(ref _cropY, Math.Max(0, value))) ScheduleUpdate(); } }
    public int CropW { get => _cropW; set { if (Set(ref _cropW, Math.Max(1, value))) ScheduleUpdate(); } }
    public int CropH { get => _cropH; set { if (Set(ref _cropH, Math.Max(1, value))) ScheduleUpdate(); } }

    private double _zoom = 1, _offsetX, _offsetY, _brightness, _contrast, _saturation, _gamma = 1, _sharpen;
    public double Zoom { get => _zoom; set { if (Set(ref _zoom, value)) ScheduleUpdate(); } }
    public double OffsetX { get => _offsetX; set { if (Set(ref _offsetX, value)) ScheduleUpdate(); } }
    public double OffsetY { get => _offsetY; set { if (Set(ref _offsetY, value)) ScheduleUpdate(); } }
    public double Brightness { get => _brightness; set { if (Set(ref _brightness, value)) ScheduleUpdate(); } }
    public double Contrast { get => _contrast; set { if (Set(ref _contrast, value)) ScheduleUpdate(); } }
    public double Saturation { get => _saturation; set { if (Set(ref _saturation, value)) ScheduleUpdate(); } }
    public double Gamma { get => _gamma; set { if (Set(ref _gamma, value)) ScheduleUpdate(); } }
    public double Sharpen { get => _sharpen; set { if (Set(ref _sharpen, value)) ScheduleUpdate(); } }

    private bool _linearLight = true;
    public bool LinearLight { get => _linearLight; set { if (Set(ref _linearLight, value)) ScheduleUpdate(); } }

    private string _background = "#FFFFFF";
    public string BackgroundColor { get => _background; set { if (Set(ref _background, value)) ScheduleUpdate(); } }
    private LocOption<TransparencyMode> _transparency;
    public LocOption<TransparencyMode> Transparency { get => _transparency; set { if (value != null && Set(ref _transparency, value)) ScheduleUpdate(); } }

    // ------------------------------------------------------------------ preview

    private LocOption<PreviewMode> _previewMode;
    public LocOption<PreviewMode> SelectedPreviewMode
    {
        get => _previewMode;
        set
        {
            if (value == null || !Set(ref _previewMode, value)) return;
            Raise(nameof(IsBuildView));
            Raise(nameof(IsBuild3D));
            Raise(nameof(IsImageView));
            RefreshPreview();
        }
    }
    public bool IsBuildView => SelectedPreviewMode.Value is PreviewMode.Build2D or PreviewMode.Build3D;
    public bool IsBuild3D => SelectedPreviewMode.Value == PreviewMode.Build3D;
    public bool IsImageView => !IsBuildView;

    private bool _showGrid = true;
    public bool ShowGrid { get => _showGrid; set { if (Set(ref _showGrid, value)) RefreshPreview(); } }
    private Bitmap? _preview;
    public Bitmap? Preview { get => _preview; set { if (Set(ref _preview, value)) Raise(nameof(HasPreview)); } }
    public bool HasPreview => _preview != null;
    private string _previewInfo = "";
    public string PreviewInfo { get => _previewInfo; set => Set(ref _previewInfo, value); }
    private string _hoverInfo = "";
    public string HoverInfo { get => _hoverInfo; set => Set(ref _hoverInfo, value); }

    // build view options
    public ObservableCollection<ViewAreaOption> ViewAreas { get; } = [];
    private ViewAreaOption? _viewArea;
    public ViewAreaOption? ViewArea { get => _viewArea; set { if (Set(ref _viewArea, value)) RefreshPreview(); } }
    private int _cellSize = 8;
    public int CellSize { get => _cellSize; set { if (Set(ref _cellSize, Math.Clamp(value, 1, 32))) RefreshPreview(); } }
    private int _rotation;
    public int Rotation { get => _rotation; set { if (Set(ref _rotation, value)) RefreshPreview(); } }
    private bool _showSupports = true, _showHeights = true, _editProgress;
    public bool ShowSupports { get => _showSupports; set { if (Set(ref _showSupports, value)) RefreshPreview(); } }
    public bool ShowHeights { get => _showHeights; set { if (Set(ref _showHeights, value)) RefreshPreview(); } }
    public bool EditProgress { get => _editProgress; set => Set(ref _editProgress, value); }
    public RelayCommand RotateCommand { get; }
    private LayoutRegion _renderedRegion;
    private int _renderedCell;

    // ------------------------------------------------------------------ status

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) RaiseCommands(); } }
    private string _status = "";
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
    public RelayCommand ReloadFrameCommand { get; }
    public RelayCommand NewProjectCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand SaveProjectCommand { get; }
    public RelayCommand SaveProjectAsCommand { get; }

    private IEnumerable<RelayCommand?> AllCommands() =>
    [
        ReloadFrameCommand, ExportBlockCommand, ExportTilesCommand, PlanWorldExportCommand, ExecuteWorldExportCommand,
        LoadMapsCommand, ExportMapPngCommand, UseMapAsSourceCommand, MeasureCalibrationCommand, SaveCalibratedProfileCommand,
        ExportMaterialsCommand, DownloadTexturesCommand, RegisterInWorldCommand, UndoEditCommand, ClearEditsCommand,
    ];

    private void RaiseCommands()
    {
        foreach (var c in AllCommands()) c?.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ settings <-> core

    private ImageSettings CurrentImageSettings() => new()
    {
        MapsX = MapsX, MapsY = MapsY, Fit = Fit.Value, Interpolation = Interp.Value, Zoom = Zoom,
        OffsetX = OffsetX, OffsetY = OffsetY, Brightness = Brightness, Contrast = Contrast, Saturation = Saturation,
        Gamma = Gamma, Sharpen = Sharpen, BackgroundColor = SafeColor(BackgroundColor), Transparency = Transparency.Value,
        LinearLight = LinearLight,
        CropMode = CropModeOption.Value, Crop = CropModeOption.Value == CropMode.Manual ? new CropRect(CropX, CropY, CropW, CropH) : null,
    };

    private static string SafeColor(string s)
    {
        try { Rgba.FromHex(s); return s; } catch { return "#FFFFFF"; }
    }

    // ------------------------------------------------------------------ pipeline

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
        if (_source is null || Profile is null) return;
        _updateCts?.Cancel();
        var cts = _updateCts = new CancellationTokenSource();
        var source = _source;
        var settings = CurrentImageSettings();
        var profile = Profile;
        var selection = CurrentSelection();
        var qopt = CurrentQuantizeOptions();
        Status = L["Converting"];
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
            _baseQuantized = q;
            if (error != null) Log(error);
            ApplyEditsAndLayout();
            RebuildViewAreas();
            UpdateBlockInfo();
            RefreshPreview();
            Status = L["Ready"];
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Status = L["Error"];
            Log((L.English ? "Conversion failed: " : "変換に失敗しました: ") + ex.Message);
        }
        finally { RaiseCommands(); }
    }

    /// <summary>Re-applies manual edits to a copy of the automatic result and rebuilds the 3D layout.</summary>
    private void ApplyEditsAndLayout()
    {
        if (_baseQuantized is null || Profile is null) { _quantized = null; _layout = null; return; }
        var q = _edits.Count == 0 ? _baseQuantized : _baseQuantized.Clone();
        _edits.ApplyTo(q, Profile);
        _quantized = q;
        var support = SelectedSupportBlock ?? Profile.Find("cobblestone") ?? Profile.Blocks.First();
        try { _layout = BlockLayout.Generate(q, support, 0, SupportModeOption.Value); }
        catch (InvalidOperationException ex) { _layout = null; Log(ex.Message); }
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

    // ------------------------------------------------------------------ preview rendering

    private int _previewVersion;

    private void RebuildViewAreas()
    {
        var current = ViewArea?.MapNumber;
        ViewAreas.Clear();
        ViewAreas.Add(new ViewAreaOption(0, L["AllMaps"]));
        for (int n = 1; n <= MapsX * MapsY; n++) ViewAreas.Add(new ViewAreaOption(n, L.Format("MapN", n)));
        _viewArea = ViewAreas.FirstOrDefault(v => v.MapNumber == current) ?? ViewAreas[MapsX * MapsY > 1 ? 1 : 0];
        Raise(nameof(ViewArea));
    }

    private LayoutRegion RegionFor(BlockLayout layout)
    {
        if (ViewArea is not { MapNumber: > 0 } a) return LayoutRegion.All(layout);
        int n = a.MapNumber - 1;
        return LayoutRegion.Map(n % MapsX, n / MapsX);
    }

    private async void RefreshPreview()
    {
        int version = ++_previewVersion;
        var mode = SelectedPreviewMode.Value;
        if (mode is PreviewMode.Build2D or PreviewMode.Build3D)
        {
            if (_layout is not { } layout || Profile is not { } profile)
            {
                Preview = null;
                PreviewInfo = _source is null ? L["DropHere"] : "";
                return;
            }
            var region = RegionFor(layout);
            int cell = CellSize, rotation = Rotation;
            bool supports = ShowSupports, heights = ShowHeights;
            BlockTextureProvider? tex = Textures.IsAvailable ? Textures : null;
            var done = _project.DoneChunks.ToHashSet();
            int mapsX = MapsX;
            Func<int, int, bool>? isDone = done.Count == 0 ? null : (x, z) => done.Contains(MaterialCalculator.ChunkKey(x, z, mapsX));
            RgbaImage img;
            try
            {
                img = await Task.Run(() => mode == PreviewMode.Build2D
                    ? LayoutRenderer.RenderTopDown(layout, profile, region, cell, tex is null ? null : tex.GetIcon, heights, isDone)
                    : LayoutRenderer.RenderIsometric(layout, profile, region, Math.Max(2, cell), rotation, tex is null ? null : tex.GetIcon, supports));
            }
            catch (Exception ex)
            {
                Log(ex.Message);
                return;
            }
            if (version != _previewVersion) return;
            _renderedRegion = region;
            _renderedCell = cell;
            Preview = BitmapHelper.ToBitmap(img);
            PreviewInfo = L["InfoBuild"] + $"  ({img.Width} x {img.Height})";
            return;
        }

        RgbaImage? image = mode switch
        {
            PreviewMode.Original => _sourceThumb,
            PreviewMode.DirectMap => _processed,
            PreviewMode.BlockMap => _quantized?.Preview,
            PreviewMode.Difference => _processed is null || _quantized is null ? null : Difference(_processed, _quantized.Preview),
            _ => null,
        };
        if (image is null)
        {
            Preview = null;
            PreviewInfo = _source is null ? L["DropHere"] : "";
            return;
        }
        if (mode == PreviewMode.Original && IsManualCrop && _source != null && _sourceThumb != null)
        {
            // Show the crop rectangle on the original.
            image = _sourceThumb.Clone();
            double k = (double)_sourceThumb.Width / _source.Width;
            int rx = (int)(CropX * k), ry = (int)(CropY * k), rw = Math.Max(1, (int)(CropW * k)), rh = Math.Max(1, (int)(CropH * k));
            var c = new Rgba(255, 60, 60);
            for (int t = 0; t < 2; t++)
            {
                for (int x = rx; x < rx + rw; x++)
                {
                    if (x < 0 || x >= image.Width) continue;
                    if (ry + t >= 0 && ry + t < image.Height) image[x, ry + t] = c;
                    if (ry + rh - 1 - t >= 0 && ry + rh - 1 - t < image.Height) image[x, ry + rh - 1 - t] = c;
                }
                for (int y = ry; y < ry + rh; y++)
                {
                    if (y < 0 || y >= image.Height) continue;
                    if (rx + t >= 0 && rx + t < image.Width) image[rx + t, y] = c;
                    if (rx + rw - 1 - t >= 0 && rx + rw - 1 - t < image.Width) image[rx + rw - 1 - t, y] = c;
                }
            }
        }
        if (mode != PreviewMode.Original)
        {
            int factor = Math.Max(1, 1024 / Math.Max(image.Width, image.Height));
            image = factor > 1 ? image.ScaleNearest(Math.Min(factor, 6)) : image.Clone();
            if (ShowGrid) DrawGrid(image, image.Width / (MapsX * 128));
        }
        Preview = BitmapHelper.ToBitmap(image);
        PreviewInfo = mode switch
        {
            PreviewMode.Original => L.Format("InfoOriginal", _source!.Width, _source.Height),
            PreviewMode.DirectMap => L["InfoDirect"],
            PreviewMode.BlockMap => L["InfoBlockMap"],
            _ => L["InfoDifference"],
        };
    }

    /// <summary>Pointer over the build view at fractions (0..1) of the rendered image.</summary>
    public void OnPreviewPointer(double fx, double fy, bool click, bool pressed = false, bool released = false)
    {
        if (SelectedPreviewMode.Value != PreviewMode.Build2D || _layout is not { } layout || Preview is null) return;
        var hit = LayoutRenderer.HitTestTopDown(_renderedRegion, _renderedCell, fx * Preview.PixelSize.Width, fy * Preview.PixelSize.Height);
        if (hit is not var (x, z) || x >= layout.Width || z >= layout.Depth) { HoverInfo = ""; return; }
        var b = layout.BlockAt(x, z);
        string world = Origin is { } o ? $"  {(L.English ? "world" : "ワールド")} X {o.X + x}, Y {o.Y + layout.HeightAt(x, z)}, Z {o.Z + z}" : "";
        int supportsBelow = layout.SupportPositions.Count(p => p.X == x && p.Z == z);
        HoverInfo = b is null ? $"x {x}, z {z}" :
            $"{b.LocalName(L.English)}  ({b.Name})  x {x}, y {layout.HeightAt(x, z)}, z {z}{world}" +
            $"  {L.Format("MapN", MaterialCalculator.MapNumberOf(x, z, MapsX))}" +
            (supportsBelow > 0 ? $"  +{supportsBelow} {L["Support"]}" : "");
        if (PaintMode && (click || pressed || released))
        {
            Paint(x, z, start: click, end: released);
            return;
        }
        if (click && EditProgress)
        {
            var key = MaterialCalculator.ChunkKey(x, z, MapsX);
            if (!_project.DoneChunks.Remove(key)) _project.DoneChunks.Add(key);
            UpdateProgressTexts();
            RefreshPreview();
        }
    }

    private static void DrawGrid(RgbaImage img, int scale)
    {
        if (scale <= 0) return;
        var red = new Rgba(230, 40, 40);
        var orange = new Rgba(255, 170, 0);
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
        var path = await _dialogs.OpenFileAsync(L["OpenImage"], L["Image"], ImageCodec.SupportedExtensions.Select(e => "*" + e).ToArray());
        if (path != null) await LoadImageAsync(path);
    }

    public async Task LoadImageAsync(string path, int frame = 0, double seconds = 0)
    {
        try
        {
            IsBusy = true;
            int frames = ImageCodec.IsVideo(path) ? 0 : await Task.Run(() => ImageCodec.FrameCount(path));
            var img = await Task.Run(() => ImageCodec.Load(path, frame, seconds));
            _sourcePath = path;
            IsVideo = ImageCodec.IsVideo(path);
            FrameCount = Math.Max(1, frames);
            _frame = Math.Clamp(frame, 0, MaxFrame);
            Raise(nameof(Frame));
            _videoSeconds = seconds;
            Raise(nameof(VideoSeconds));
            SetSource(img, path);
            if (ProjectName is "新しいマップアート" or "New map art") ProjectName = Path.GetFileNameWithoutExtension(path);
            if (CropW <= 1 || CropH <= 1) { _cropW = img.Width; _cropH = img.Height; Raise(nameof(CropW)); Raise(nameof(CropH)); }
            Log((L.English ? "Loaded " : "読み込みました: ") + $"{Path.GetFileName(path)} ({img.Width}x{img.Height})" +
                (FrameCount > 1 ? $" frame {Frame + 1}/{FrameCount}" : "") + (IsVideo ? $" @ {seconds:0.##}s" : ""));
        }
        catch (Exception ex) { Log((L.English ? "Could not open the file: " : "開けませんでした: ") + ex.Message); }
        finally { IsBusy = false; RaiseCommands(); }
    }

    private Task ReloadFrameAsync() => _sourcePath is null ? Task.CompletedTask : LoadImageAsync(_sourcePath, Frame, VideoSeconds);

    private void SetSource(RgbaImage img, string? path)
    {
        _source = img;
        _sourceThumb = ImagePipeline.Thumbnail(img, 1024);
        SourceInfo = path is null ? $"map ({img.Width}x{img.Height})" : $"{Path.GetFileName(path)} ({img.Width}x{img.Height})";
        if (SelectedPreviewMode.Value == PreviewMode.Original) SelectedPreviewMode = PreviewModes[1];
        ScheduleUpdate();
        RefreshPreview();
    }

    private void NewProject()
    {
        _projectPath = null;
        ApplyProject(new BmagProject());
        _source = _sourceThumb = _processed = null;
        _quantized = _baseQuantized = null;
        _layout = null;
        _edits = new PixelOverrides();
        _sourcePath = null;
        FrameCount = 1;
        IsVideo = false;
        SourceInfo = L["NoImage"];
        Materials.Clear();
        BlockSummary = "";
        RefreshPreview();
    }

    private async Task OpenProjectAsync()
    {
        var path = await _dialogs.OpenFileAsync(L["OpenProject"], "BMAG", ["*.bmag.json", "*.json"]);
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
                    Log(L.English ? "Note: the image changed since the project was saved." : "注意: 画像ファイルが保存時から変更されています。");
                await LoadImageAsync(img, p.SourceFrame, p.SourceVideoSeconds);
            }
            else if (img != null) Log((L.English ? "Image not found: " : "画像が見つかりません: ") + img);
            Log((L.English ? "Opened project: " : "プロジェクトを開きました: ") + path);
        }
        catch (Exception ex) { Log((L.English ? "Could not open the project: " : "プロジェクトを開けませんでした: ") + ex.Message); }
    }

    private async Task SaveProjectAsync(bool saveAs)
    {
        string? path = _projectPath;
        if (saveAs || path is null)
        {
            path = await _dialogs.SaveFileAsync(L["SaveProject"], StructureExporter.Slug(ProjectName) + ".bmag.json", "json");
            if (path is null) return;
        }
        try
        {
            ToProject().Save(path);
            _projectPath = path;
            Log((L.English ? "Saved project: " : "プロジェクトを保存しました: ") + path);
        }
        catch (Exception ex) { Log((L.English ? "Save failed: " : "保存に失敗しました: ") + ex.Message); }
    }

    private BmagProject ToProject()
    {
        var p = _project;
        p.ProjectName = ProjectName;
        p.SourceImagePath = _sourcePath;
        p.SourceImageHash = _sourcePath != null && File.Exists(_sourcePath) ? BmagProject.HashFile(_sourcePath) : null;
        p.SourceFrame = Frame;
        p.SourceVideoSeconds = VideoSeconds;
        p.Image = CurrentImageSettings();
        p.ColorProfileId = BaseProfile?.Id ?? ColorProfile.DefaultId;
        p.Metric = Metric.Value;
        p.Dither = Dither.Value;
        p.DitherStrength = DitherStrength;
        p.Serpentine = Serpentine;
        p.Split = SplitOption.Value;
        p.WriteBehaviorPack = WriteBehaviorPack;
        p.Edits = _edits.Count > 0 ? _edits.Serialize() : null;
        p.Staircase = StaircaseOption.Value;
        p.Supports = SupportModeOption.Value;
        p.Selection = CurrentSelection().Blocks;
        p.MaxBlockTypes = MaxBlockTypes;
        p.SupportBlockKey = _supportKey;
        p.OriginX = UseOrigin ? OriginMapX : null;
        p.OriginZ = UseOrigin ? OriginMapZ : null;
        p.OriginY = OriginY;
        p.LastWorldFolder = SelectedWorld?.Folder;
        p.StartMapId = StartId;
        p.Delivery = Delivery.Value;
        p.GlowFrames = GlowFrames;
        return p;
    }

    private void ApplyProject(BmagProject p)
    {
        _loadingProject = true;
        try
        {
            _project = p;
            ProjectName = p.ProjectName;
            var s = p.Image;
            MapsX = s.MapsX; MapsY = s.MapsY;
            Fit = FitModes.First(f => f.Value == s.Fit);
            Interp = Interpolations.First(f => f.Value == s.Interpolation);
            CropModeOption = CropModes.First(c => c.Value == s.CropMode);
            if (s.Crop is { } c) { CropX = c.X; CropY = c.Y; CropW = c.Width; CropH = c.Height; }
            Zoom = s.Zoom; OffsetX = s.OffsetX; OffsetY = s.OffsetY;
            Brightness = s.Brightness; Contrast = s.Contrast; Saturation = s.Saturation; Gamma = s.Gamma; Sharpen = s.Sharpen;
            BackgroundColor = s.BackgroundColor;
            Transparency = TransparencyModes.First(t => t.Value == s.Transparency);
            Metric = Metrics.First(m => m.Value == p.Metric);
            Dither = Dithers.First(d => d.Value == p.Dither);
            DitherStrength = p.DitherStrength;
            Serpentine = p.Serpentine;
            SplitOption = Splits.First(s => s.Value == p.Split);
            WriteBehaviorPack = p.WriteBehaviorPack;
            _edits = PixelOverrides.Deserialize(p.Edits);
            LinearLight = s.LinearLight;
            StaircaseOption = Staircases.First(b => b.Value == p.Staircase);
            SupportModeOption = SupportModes.First(b => b.Value == p.Supports);
            MaxBlockTypes = p.MaxBlockTypes;
            _supportKey = p.SupportBlockKey;
            var profile = Profiles.FirstOrDefault(x => x.Id == p.ColorProfileId) ?? Profiles.FirstOrDefault();
            if (profile != null) SelectProfile(profile, p.Selection);
            UseOrigin = p.OriginX != null;
            OriginMapX = p.OriginX ?? 0; OriginMapZ = p.OriginZ ?? 0; OriginY = p.OriginY ?? 64;
            StartId = p.StartMapId;
            Delivery = Deliveries.First(d => d.Value == p.Delivery);
            GlowFrames = p.GlowFrames;
            if (p.LastWorldFolder != null) SelectedWorld = Worlds.FirstOrDefault(w => w.Folder == p.LastWorldFolder) ?? SelectedWorld;
        }
        finally { _loadingProject = false; }
        RebuildProgress();
        ScheduleUpdate();
    }
}

public sealed record ViewAreaOption(int MapNumber, string Label)
{
    public override string ToString() => Label;
}
