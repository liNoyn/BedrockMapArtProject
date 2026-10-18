using System.Collections.ObjectModel;
using BMAP.Bedrock;
using BMAP.Bedrock.World;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

namespace BMAP.App.ViewModels;

/// <summary>
/// In-game calibration, read only: export a test pad, let the game draw a map of it, then read that map from a
/// temporary copy of the world's database and compare it with the prediction. Worlds are never written.
/// </summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<WorldInfo> CalWorlds { get; } = [];
    private WorldInfo? _calWorld;
    public WorldInfo? CalWorld { get => _calWorld; set { if (Set(ref _calWorld, value)) ReadMapsCommand.RaiseCanExecuteChanged(); } }
    private PadDescriptor? _pad;
    private string _padText = "";
    public string PadText { get => _padText; set => Set(ref _padText, value); }
    private string _calibrationReport = "";
    public string CalibrationReport { get => _calibrationReport; set => Set(ref _calibrationReport, value); }
    private CalibrationResult? _calResult;
    private RgbaImage? _calImage;
    private int _colourPadStep = 2; // the measured Bedrock rule shades every pixel only for steps of 2 or more
    public int ColourPadStep { get => _colourPadStep; set => Set(ref _colourPadStep, Math.Clamp(value, 1, 8)); }

    public RelayCommand ExportColourPadCommand { get; private set; } = null!;
    public RelayCommand RefreshCalWorldsCommand { get; private set; } = null!;
    public RelayCommand BrowseCalWorldCommand { get; private set; } = null!;
    public RelayCommand LoadPadCommand { get; private set; } = null!;
    public RelayCommand ReadMapsCommand { get; private set; } = null!;
    public RelayCommand SaveCalibrationCommand { get; private set; } = null!;

    private void InitCalibration()
    {
        ExportColourPadCommand = new RelayCommand(() => ExportPadAsync(PadKind.Colours));
        RefreshCalWorldsCommand = new RelayCommand(RefreshCalWorlds);
        BrowseCalWorldCommand = new RelayCommand(BrowseCalWorldAsync);
        LoadPadCommand = new RelayCommand(LoadPadAsync);
        ReadMapsCommand = new RelayCommand(ReadMapsAsync, () => CalWorld != null && _pad != null && !IsBusy);
        SaveCalibrationCommand = new RelayCommand(SaveCalibration, () => _calResult != null);
    }

    private bool _calExpanded;
    public bool CalExpanded { get => _calExpanded; set { if (Set(ref _calExpanded, value) && value) EnsureCalWorlds(); } }
    private bool _calWorldsLoaded;

    /// <summary>World list for the calibration panel (filled the first time the panel is used).</summary>
    public void EnsureCalWorlds()
    {
        if (_calWorldsLoaded) return;
        _calWorldsLoaded = true;
        RefreshCalWorlds();
    }

    private void RefreshCalWorlds()
    {
        var current = CalWorld?.Folder;
        CalWorlds.Clear();
        try { foreach (var w in WorldLocator.FindWorlds()) CalWorlds.Add(w); }
        catch (Exception ex) { Log(ex.Message); }
        CalWorld = CalWorlds.FirstOrDefault(w => w.Folder == current) ?? CalWorlds.FirstOrDefault();
    }

    private async Task BrowseCalWorldAsync()
    {
        var folder = await _dialogs.PickFolderAsync(L["CalWorld"]);
        if (folder is null) return;
        if (!WorldInfo.LooksLikeWorld(folder)) { Log(L["CalNotWorld"]); return; }
        var w = WorldInfo.Load(folder, "manual");
        CalWorlds.Insert(0, w);
        CalWorld = w;
    }

    /// <summary>Exports the shading pad (from the green button) or the colour pad, with pad.json and instructions.</summary>
    private async Task ExportPadAsync(PadKind kind)
    {
        if (Profile is null) return;
        var folder = await _dialogs.PickFolderAsync(kind == PadKind.Shading ? L["BtnTestPad"] : L["CalColourPad"]);
        if (folder is null) return;
        try
        {
            var profile = Profile;
            var support = SelectedSupportBlock ?? profile.Find("cobblestone") ?? profile.Blocks.First();
            bool en = L.English;
            var origin = Origin;
            int step = ColourPadStep;
            string dir = await Task.Run(() =>
            {
                var pad = kind == PadKind.Shading ? ShadingTestPad.Create(profile, support, en) : ShadingTestPad.CreateColourPad(profile, support, step, en);
                return PadExporter.Export(folder, pad, profile, origin, en);
            });
            _pad = PadDescriptor.Load(Path.Combine(dir, "pad.json"));
            PadText = Path.Combine(dir, "pad.json");
            ReadMapsCommand.RaiseCanExecuteChanged();
            Log(L["TestPadDone"] + dir);
            if (origin is null) Log(L["CalNoOrigin"]);
            OpenFolder(dir);
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private async Task LoadPadAsync()
    {
        var path = await _dialogs.OpenFileAsync(L["CalLoadPad"], "pad.json", ["pad.json", "*.json"]);
        if (path is null) return;
        try
        {
            _pad = PadDescriptor.Load(path);
            PadText = path;
            ReadMapsCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private async Task ReadMapsAsync()
    {
        if (CalWorld is not { } world || _pad is not { } pad || BaseProfile is not { } profile) return;
        if (WorldLocator.IsMinecraftRunning()) Log(L["CalRunning"]);
        IsBusy = true;
        Status = L["CalReading"];
        try
        {
            bool en = L.English;
            var (result, report) = await Task.Run(() =>
            {
                var maps = MapRecord.ReadAll(world.Folder); // reads a temporary copy of the database
                var predicted = MapSimulator.Simulate(ShadingTestPad.Recreate(pad, profile).Layout, profile).Image;
                var map = Calibrator.FindMap(maps, pad, predicted)
                          ?? throw new InvalidOperationException(en ? "No scale-0 map was found in this world." : "このワールドに縮尺 0 の地図が見つかりませんでした。");
                var r = Calibrator.Analyze(pad, map, profile, world.Version ?? "?", en);
                return (r, Calibrator.Report(r, en));
            });
            _calResult = result;
            _calImage = Calibrator.ComparisonImage(result);
            CalibrationReport = report + "\n" + L["CalImageHint"];
            Log(L["CalDone"]);
            SelectedPreviewMode = PreviewModes.First(p => p.Value == PreviewMode.Calibration);
            RefreshPreview();
        }
        catch (Exception ex)
        {
            CalibrationReport = ex.Message;
            Log(ex.Message);
        }
        finally { IsBusy = false; Status = L["Ready"]; SaveCalibrationCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Saves the measurements as a new colour profile (estimated values stay marked as estimated).</summary>
    private void SaveCalibration()
    {
        if (_calResult is not { } r || BaseProfile is not { } baseProfile) return;
        string version = r.GameVersion;
        string id = $"bedrock-measured-{DateTime.Now:yyyyMMdd-HHmmss}";
        string name = (L.English ? "Measured " : "実測 ") + $"{version} ({DateTime.Now:yyyy-MM-dd HH:mm})";
        ColorProfile profile;
        if (r.Pad.Kind == PadKind.Shading)
        {
            var rule = r.MeasuredRule(version);
            if (rule is null) { Log(L["CalNoRule"]); return; }
            profile = baseProfile.WithMeasurements(new Dictionary<string, Dictionary<Shade, string>>(), id, name, rule, gameVersion: version);
        }
        else
        {
            var m = r.Measurements();
            if (m.Count == 0) { Log(L["CalNoColours"]); return; }
            profile = baseProfile.WithMeasurements(m, id, name,
                note: $"Bedrock {version}, colour pad, map {r.Map.Id}, {DateTime.Now:yyyy-MM-dd}", gameVersion: version);
        }
        try
        {
            var path = UserProfiles.Save(profile);
            Profiles.Add(profile);
            SelectProfile(profile, CurrentSelection().Keys);
            Log(L["CalSaved"] + path);
        }
        catch (Exception ex) { Log(ex.Message); }
    }
}
