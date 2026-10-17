using System.Collections.ObjectModel;
using System.Text;
using BMAG.Bedrock.LevelDb;
using BMAG.Bedrock.World;
using BMAG.BlockEngine;
using BMAG.Calibration;
using BMAG.Export;
using BMAG.Imaging;
using BMAG.MapData;
using BMAG.Palette;

namespace BMAG.App.ViewModels;

/// <summary>Direct map generation, map viewer and colour calibration.</summary>
public sealed partial class MainViewModel
{
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
            if (SelectedWorld is not { } w) return L["SelectWorld"];
            var profile = VersionProfile.ForWorld(w.VersionParts);
            return L.English
                ? $"Folder: {w.Folder}\nVersion: {w.Version ?? "unknown"}\n" + (w.Error != null ? $"Read error: {w.Error}\n" : "") +
                  (profile is null ? "Writing to this version is not supported (viewing only)." : $"Write profile: {profile.Id}")
                : $"フォルダ: {w.Folder}\nバージョン: {w.Version ?? "不明"}\n" + (w.Error != null ? $"読み取りエラー: {w.Error}\n" : "") +
                  (profile is null ? "このバージョンへの書き込みは未対応です（閲覧のみ）。" : $"書き込みプロファイル: {profile.Label}");
        }
    }

    public IReadOnlyList<LocOption<ItemDelivery>> Deliveries { get; private set; } = [];
    private long _startId = MapIdAllocator.DefaultStart;
    public long StartId { get => _startId; set => Set(ref _startId, value); }
    private LocOption<ItemDelivery> _delivery = null!;
    public LocOption<ItemDelivery> Delivery { get => _delivery; set { if (value != null) Set(ref _delivery, value); } }
    private bool _glowFrames, _backingWall = true, _addToInventory;
    public bool GlowFrames { get => _glowFrames; set => Set(ref _glowFrames, value); }
    public bool BackingWall { get => _backingWall; set => Set(ref _backingWall, value); }
    public bool AddToInventory { get => _addToInventory; set => Set(ref _addToInventory, value); }
    private string _planText = "";
    public string PlanText { get => _planText; set => Set(ref _planText, value); }

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
            return L.English
                ? $"Pad area: X {o.X} – {o.X + 127}, Z {o.Z + 1} – {o.Z + 128} (Y {o.Y}). The map centre will be ({128 * CalibrationMapX}, {128 * CalibrationMapZ})."
                : $"テストパッドの範囲: X {o.X} ～ {o.X + 127}, Z {o.Z + 1} ～ {o.Z + 128}（Y {o.Y}）。地図の中心は ({128 * CalibrationMapX}, {128 * CalibrationMapZ}) になります。";
        }
    }
    private string _calibrationResult = "";
    public string CalibrationResult { get => _calibrationResult; set => Set(ref _calibrationResult, value); }
    private Dictionary<string, Dictionary<Shade, string>>? _pendingMeasurements;

    public RelayCommand ExportTilesCommand { get; private set; } = null!;
    public RelayCommand RefreshWorldsCommand { get; private set; } = null!;
    public RelayCommand BrowseWorldCommand { get; private set; } = null!;
    public RelayCommand PlanWorldExportCommand { get; private set; } = null!;
    public RelayCommand ExecuteWorldExportCommand { get; private set; } = null!;
    public RelayCommand LoadMapsCommand { get; private set; } = null!;
    public RelayCommand ExportMapPngCommand { get; private set; } = null!;
    public RelayCommand UseMapAsSourceCommand { get; private set; } = null!;
    public RelayCommand ExportCalibrationPackCommand { get; private set; } = null!;
    public RelayCommand MeasureCalibrationCommand { get; private set; } = null!;
    public RelayCommand SaveCalibratedProfileCommand { get; private set; } = null!;

    private void InitWorld()
    {
        Deliveries = [new(ItemDelivery.ChestAndFrameWall, "DlBoth", L), new(ItemDelivery.Chest, "DlChest", L), new(ItemDelivery.FrameWall, "DlWall", L), new(ItemDelivery.None, "DlNone", L)];
        _delivery = Deliveries[0];
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
        RefreshWorlds();
    }

    private async Task ExportTilesAsync()
    {
        if (_processed is null) return;
        var folder = await _dialogs.PickFolderAsync(L["ExportPng"]);
        if (folder is null) return;
        try
        {
            string dir = Path.Combine(folder, $"BMAG_{StructureExporter.Slug(ProjectName)}_maps");
            var processed = _processed;
            var tiles = TileSplitter.Split(processed, MapsX, MapsY);
            await Task.Run(() =>
            {
                ImageCodec.SavePng(processed, Path.Combine(dir, "full.png"));
                foreach (var t in tiles) ImageCodec.SavePng(t.Image, Path.Combine(dir, $"map_{t.Index + 1:00}_x{t.GridX}_y{t.GridY}.png"));
            });
            Log($"PNG x{tiles.Count}: {dir}");
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void RefreshWorlds()
    {
        var current = SelectedWorld?.Folder;
        Worlds.Clear();
        try
        {
            foreach (var w in WorldLocator.FindWorlds()) Worlds.Add(w);
            Log(L.English ? $"Found {Worlds.Count} worlds." : $"ワールドを {Worlds.Count} 件検出しました。");
        }
        catch (Exception ex) { Log(ex.Message); }
        SelectedWorld = Worlds.FirstOrDefault(w => w.Folder == current) ?? Worlds.FirstOrDefault();
    }

    private async Task BrowseWorldAsync()
    {
        var folder = await _dialogs.PickFolderAsync(L["BrowseFolder"]);
        if (folder is null) return;
        if (!WorldInfo.LooksLikeWorld(folder))
        {
            Log(L.English ? "That folder is not a Bedrock world (needs level.dat and db)." : "選択したフォルダは Bedrock のワールドではありません（level.dat と db が必要です）。");
            return;
        }
        var w = WorldInfo.Load(folder, "manual");
        Worlds.Insert(0, w);
        SelectedWorld = w;
    }

    private WorldExportOptions CurrentExportOptions() => new()
    {
        ProjectName = ProjectName, StartId = StartId, Delivery = Delivery.Value, GlowFrames = GlowFrames,
        BackingWall = BackingWall, AddToInventory = AddToInventory,
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
            PlanText = BuildPlan()?.Describe() ?? "";
            if (Transparency.Value == TransparencyMode.Keep)
                PlanText += L.English ? "\nNote: how transparent pixels look on real maps is unverified." : "\n注意: 透明部分が実際の地図でどう表示されるかは未検証です。";
        }
        catch (Exception ex) { PlanText = ex.Message; }
    }

    private async Task ExecuteWorldExportAsync()
    {
        WorldExportPlan? plan;
        try { plan = BuildPlan(); }
        catch (Exception ex) { Log(ex.Message); PlanText = ex.Message; return; }
        if (plan is null) return;
        PlanText = plan.Describe();
        string confirm = plan.Describe() + (L.English
            ? "\nThe original world is not changed: a copy is created and the maps are written into it. Continue?"
            : "\n元のワールドは変更せず、コピーを作成してそこに書き込みます。実行しますか？");
        if (!await _dialogs.ConfirmAsync(L["RunExport"], confirm)) return;

        IsBusy = true;
        try
        {
            var result = await Task.Run(() => SafeWorldExporter.Execute(plan, new Progress<string>(Log)));
            Log((L.English ? "Created world copy with maps: " : "地図を追加したワールドコピーを作成しました: ") + result.Destination);
            PlanText = result.Instructions + $"\nlog: {result.LogPath}";
            RefreshWorlds();
        }
        catch (Exception ex)
        {
            Log((L.English ? "Export failed (original world unchanged): " : "ワールドへの出力に失敗しました（元ワールドは変更されていません）: ") + ex.Message);
        }
        finally { IsBusy = false; Status = L["Ready"]; }
    }

    private async Task LoadMapsAsync()
    {
        if (SelectedWorld is null) return;
        if (WorldLocator.IsMinecraftRunning())
            Log(L.English ? "Note: Minecraft is running; the data may be out of date (read-only)." : "注意: Minecraft が起動中です。表示内容が最新でない可能性があります（読み取りのみ行います）。");
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
            Log(L.English ? $"Loaded {WorldMaps.Count} maps." : $"地図を {WorldMaps.Count} 件読み込みました。");
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { IsBusy = false; }
    }

    private async Task ExportMapPngAsync()
    {
        if (SelectedMap?.Record.ToImage() is not { } img) return;
        var path = await _dialogs.SaveFileAsync(L["SavePng"], SelectedMap.Title + ".png", "png");
        if (path is null) return;
        ImageCodec.SavePng(img, path);
        Log(path);
    }

    private void UseMapAsSource()
    {
        if (SelectedMap?.Record.ToImage() is not { } img) return;
        MapsX = MapsY = 1;
        _sourcePath = null;
        FrameCount = 1;
        SetSource(img, null);
    }

    private async Task ExportCalibrationPackAsync()
    {
        if (Profile is null) return;
        var folder = await _dialogs.PickFolderAsync(L["ExportPad"]);
        if (folder is null) return;
        try
        {
            var profile = Profile;
            var support = profile.Find("cobblestone") ?? profile.Blocks.First();
            var layout = CalibrationPad.CreateLayout(profile, support);
            var origin = WorldOrigin.AlignedToMap(CalibrationMapX, CalibrationMapZ, CalibrationY);
            string dir = Path.Combine(folder, "BMAG_calibration");
            var result = await Task.Run(() => StructureExporter.Export(dir, "calibration", layout, 1, new StructureExportOptions(), origin));
            int count = CalibrationPad.CalibratableBlocks(profile).Count;
            Log(result.McpackPath);
            CalibrationResult = L.English
                ? $"Pad with {count} blocks.\n1. Import the .mcpack and enable it in a flat creative world (or a copy) with cheats.\n" +
                  $"2. Run /function bmag/calibration/all_absolute ({CalibrationText})\n" +
                  "3. Stand inside the pad, use an empty map and keep holding it until everything is drawn (do not zoom out).\n" +
                  "4. Quit the game, select that world here, Load maps, pick the map with the matching centre and press Measure."
                : $"{count} ブロックのテストパッドです。\n1. .mcpack をインポートし、チートを有効にした平らなクリエイティブワールド（またはそのコピー）で有効にします。\n" +
                  $"2. /function bmag/calibration/all_absolute を実行します（{CalibrationText}）。\n" +
                  "3. パッドの範囲内に立ち、白紙の地図を使って全体が描かれるまで持ったままにします（拡大しないでください）。\n" +
                  "4. ゲームを終了し、このタブでそのワールドを選んで「地図を読み込む」→ 中心が一致する地図を選び「選択した地図で測定」を押します。";
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void MeasureCalibration()
    {
        if (SelectedMap is null || Profile is null) return;
        var map = SelectedMap.Record;
        var sb = new StringBuilder();
        if (!Calibrator.CoversGridCell(map, CalibrationMapX, CalibrationMapZ))
            sb.AppendLine(L.English
                ? $"Warning: this map (scale {map.Scale}, centre {map.XCenter},{map.ZCenter}) does not match the pad position."
                : $"警告: この地図（縮尺 {map.Scale}, 中心 {map.XCenter},{map.ZCenter}）は設定したパッドの位置と一致しません。");
        try
        {
            var samples = Calibrator.Measure(map, Profile);
            _pendingMeasurements = Calibrator.ToMeasurements(samples);
            var ok = samples.Where(s => s.Measured != null && s.Problem == null).ToList();
            sb.AppendLine($"{ok.Count} / {samples.Count} ({_pendingMeasurements.Count} blocks)");
            if (ok.Count > 0) sb.AppendLine($"ΔE2000 avg {ok.Average(s => s.DeltaE):0.00}, max {ok.Max(s => s.DeltaE):0.00}");
            foreach (var s in samples.OrderByDescending(s => double.IsNaN(s.DeltaE) ? 999 : s.DeltaE).Take(25))
                sb.AppendLine($"  {s.Block.LocalName(L.English)} [{s.Shade}] {s.Estimated.ToHex()} → {s.Measured?.ToHex() ?? "-"}  ΔE {s.DeltaE:0.0} {s.Problem}");
        }
        catch (Exception ex) { sb.AppendLine(ex.Message); _pendingMeasurements = null; }
        CalibrationResult = sb.ToString();
        RaiseCommands();
    }

    private void SaveCalibratedProfile()
    {
        if (_pendingMeasurements is null || BaseProfile is null) return;
        string id = $"bedrock-calibrated-{DateTime.Now:yyyyMMdd-HHmmss}";
        string version = SelectedWorld?.Version ?? "?";
        var profile = BaseProfile.WithMeasurements(_pendingMeasurements, id, $"{(L.English ? "Calibrated" : "実測プロファイル")} {DateTime.Now:yyyy-MM-dd HH:mm} ({version})");
        var path = Calibrator.SaveUserProfile(profile);
        Profiles.Add(profile);
        SelectProfile(profile, CurrentSelection().Blocks);
        Log(path);
        _pendingMeasurements = null;
        RaiseCommands();
    }
}
