using System.Collections.ObjectModel;
using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.Palette;

namespace BMAP.App.ViewModels;

/// <summary>Block selection (MapartCraft-style rows), presets, textures and custom blocks.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<ColorProfile> Profiles { get; } = [];
    public ObservableCollection<ColourSetRowViewModel> Rows { get; } = [];
    public ObservableCollection<BlockPreset> Presets { get; } = [];
    public ObservableCollection<CustomBlockViewModel> CustomBlocks { get; } = [];

    /// <summary>The selected profile as stored (without custom blocks).</summary>
    public ColorProfile? BaseProfile { get; private set; }
    /// <summary>The effective profile: base profile + custom blocks.</summary>
    public ColorProfile? Profile { get; private set; }

    public ColorProfile? SelectedBaseProfile
    {
        get => BaseProfile;
        set { if (value != null && value != BaseProfile) SelectProfile(value, CurrentSelection().Keys); }
    }

    public string ProfileNote
    {
        get
        {
            if (Profile is not { } p) return "";
            int blocks = p.Blocks.Count(b => b.EffectiveStatus == ColorStatus.Verified), sets = p.ColourSets.Count(s => s.Status == ColorStatus.Verified);
            var sh = p.Shades;
            string S(ColorStatus s) => L[s switch
            {
                ColorStatus.Verified => "StatusVerified", ColorStatus.ThirdParty => "StatusThirdParty",
                ColorStatus.ApiDerived => "StatusApi", ColorStatus.Unknown => "StatusUnknown", _ => "StatusEstimated",
            }];
            string steps = sh.UpThreshold == sh.DownThreshold ? $"{sh.UpThreshold}" : $"+{sh.UpThreshold}/−{sh.DownThreshold}";
            return L.Format("ProfileNoteFmt", blocks, p.Blocks.Count(), sets, p.ColourSets.Count,
                $"{sh.Dark}/{sh.Normal}/{sh.Bright}", S(sh.Status), steps, S(sh.StepStatus))
                + (p.ColourSets.Count(s => s.Status == ColorStatus.ThirdParty) is int tp and > 0 ? L.Format("ProfileNoteThirdParty", tp) : "");
        }
    }

    private string _paletteSummary = "";
    public string PaletteSummary { get => _paletteSummary; set => Set(ref _paletteSummary, value); }

    private BlockPreset? _selectedPreset;
    public BlockPreset? SelectedPreset { get => _selectedPreset; set { if (Set(ref _selectedPreset, value)) DeletePresetCommand.RaiseCanExecuteChanged(); } }

    public RelayCommand ApplyPresetCommand { get; private set; } = null!;
    public RelayCommand SavePresetCommand { get; private set; } = null!;
    public RelayCommand DeletePresetCommand { get; private set; } = null!;
    public RelayCommand DownloadTexturesCommand { get; private set; } = null!;
    public RelayCommand UseResourcePackCommand { get; private set; } = null!;
    public RelayCommand AddCustomCommand { get; private set; } = null!;
    public RelayCommand ImportBpFolderCommand { get; private set; } = null!;
    public RelayCommand ImportBpFileCommand { get; private set; } = null!;
    public RelayCommand SelectAllBlocksCommand { get; private set; } = null!;
    public RelayCommand ClearAllBlocksCommand { get; private set; } = null!;

    public bool TexturesMissing => !Textures.IsAvailable;

    public string TextureStatus => Textures.IsAvailable
        ? L["TexturesReady"] + (Textures.ResourcePack != null ? $" ({Path.GetFileName(Textures.ResourcePack)})" : "")
        : L["NoTextures"];

    // custom block form
    private string _customId = "", _customStates = "", _customColor = "";
    private bool _customSupport;
    private ColourSet? _customSet;
    public string CustomId { get => _customId; set => Set(ref _customId, value); }
    public string CustomStates { get => _customStates; set => Set(ref _customStates, value); }
    public string CustomColor { get => _customColor; set => Set(ref _customColor, value); }
    public bool CustomSupport { get => _customSupport; set => Set(ref _customSupport, value); }
    public ColourSet? CustomSet { get => _customSet; set => Set(ref _customSet, value); }
    public IEnumerable<ColourSet> ColourSetsForCustom => BaseProfile?.ColourSets ?? [];

    private void InitPalette()
    {
        ApplyPresetCommand = new RelayCommand(ApplyPreset, () => SelectedPreset != null);
        SavePresetCommand = new RelayCommand(SavePresetAsync);
        DeletePresetCommand = new RelayCommand(DeletePreset, () => SelectedPreset is { BuiltIn: false });
        DownloadTexturesCommand = new RelayCommand(DownloadTexturesAsync, () => !IsBusy);
        UseResourcePackCommand = new RelayCommand(UseResourcePackAsync);
        AddCustomCommand = new RelayCommand(AddCustom);
        ImportBpFolderCommand = new RelayCommand(() => ImportBehaviorPackAsync(true));
        ImportBpFileCommand = new RelayCommand(() => ImportBehaviorPackAsync(false));
        SelectAllBlocksCommand = new RelayCommand(() => SelectAllBlocks(true));
        ClearAllBlocksCommand = new RelayCommand(() => SelectAllBlocks(false));
        Mixings = [new(BlockMixing.Balanced, "MixBalanced", L), new(BlockMixing.Mixed, "MixMixed", L)];
        _mixing = Mixings[0];

        foreach (var c in CustomBlockStore.Load()) CustomBlocks.Add(new CustomBlockViewModel(c, RemoveCustom));
        Profiles.Clear();
        foreach (var id in ColorProfile.BuiltInIds()) Profiles.Add(ColorProfile.LoadBuiltIn(id));
        foreach (var p in UserProfiles.Load()) Profiles.Add(p);
        var first = Profiles.FirstOrDefault(p => p.Id == ColorProfile.DefaultId) ?? Profiles.First();
        SelectProfile(first, null);
        Textures.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() => { Raise(nameof(TextureStatus)); Raise(nameof(TexturesMissing)); LoadIcons(); RefreshPreview(); });
    }

    /// <summary>Switches the colour profile and rebuilds the rows, keeping the selection where possible.</summary>
    private void SelectProfile(ColorProfile baseProfile, IEnumerable<string>? selection)
    {
        BaseProfile = baseProfile;
        Profile = baseProfile.WithCustomBlocks(CustomBlocks.Select(c => c.Block));
        Raise(nameof(SelectedBaseProfile));
        Raise(nameof(ProfileNote));
        Raise(nameof(ColourSetsForCustom));

        Rows.Clear();
        foreach (var set in Profile.ColourSets) Rows.Add(new ColourSetRowViewModel(set, Profile, L, OnSelectionChanged));

        Presets.Clear();
        foreach (var p in BlockPreset.BuiltIns(Profile)) Presets.Add(p);
        foreach (var p in BlockPreset.LoadUser()) Presets.Add(p);
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == SelectedPreset?.Name) ?? Presets.FirstOrDefault();

        ApplySelection(selection ?? Presets[0].Keys);
        ApplyLabels();
        SupportBlocks = Profile.Blocks.Where(b => !b.NeedsSupport && !b.Gravity).ToList();
        SelectedSupportBlock = Profile.Find(_supportKey) ?? Profile.Find("cobblestone") ?? SupportBlocks.FirstOrDefault();
        ApplyLabels();
        LoadIcons();
        ScheduleUpdate();
    }

    /// <summary>Refreshes language-dependent labels of model objects shown in combo boxes.</summary>
    private void ApplyLabels()
    {
        if (Profile is null) return;
        foreach (var set in Profile.ColourSets)
        {
            set.Label = set.LocalName(L.English);
            foreach (var b in set.Blocks) b.Label = b.LocalName(L.English);
        }
        if (BaseProfile != null)
            foreach (var set in BaseProfile.ColourSets) set.Label = set.LocalName(L.English);
        var selected = SelectedPreset;
        var presets = Presets.ToList();
        foreach (var p in presets) p.Label = p.LocalName(L.English);
        Presets.Clear();
        foreach (var p in presets) Presets.Add(p);
        SelectedPreset = selected;
        var support = SelectedSupportBlock;
        SupportBlocks = [.. SupportBlocks];
        SelectedSupportBlock = support;
        Raise(nameof(ColourSetsForCustom));
    }

    private void ApplySelection(IEnumerable<string> selection)
    {
        var keys = selection.ToHashSet();
        foreach (var row in Rows) row.Load(keys);
        UpdatePaletteSummary();
    }

    private PaletteSelection CurrentSelection() => new()
    {
        Keys = Rows.SelectMany(r => r.Chosen).Select(c => c.Block.Key).ToHashSet(),
        MaxBlockTypes = MaxBlockTypes,
        Mixing = MixingOption?.Value ?? BlockMixing.Balanced,
    };

    public IReadOnlyList<LocOption<BlockMixing>> Mixings { get; private set; } = [];
    private LocOption<BlockMixing>? _mixing;
    public LocOption<BlockMixing>? MixingOption { get => _mixing; set { if (value != null && Set(ref _mixing, value)) ScheduleUpdate(); } }

    /// <summary>Selects or clears every block of every row (buttons in the palette tab).</summary>
    private void SelectAllBlocks(bool on)
    {
        foreach (var row in Rows)
        {
            if (on) row.Load(row.Blocks.Where(b => b.Block.DefaultEnabled).Select(b => b.Block.Key));
            else row.Load([]);
        }
        OnSelectionChanged();
    }

    private void OnSelectionChanged()
    {
        UpdatePaletteSummary();
        ScheduleUpdate();
    }

    private void UpdatePaletteSummary()
    {
        int colours = Rows.Count(r => r.ChosenCount > 0);
        int blocks = Rows.Sum(r => r.ChosenCount);
        PaletteSummary = L.English
            ? $"{blocks} blocks selected, {colours} / {Rows.Count} colours"
            : $"{blocks} ブロックを選択中（{colours} / {Rows.Count} 色）";
    }

    private void ApplyPreset()
    {
        if (SelectedPreset is null) return;
        ApplySelection(SelectedPreset.Keys);
        Log((L.English ? "Applied preset: " : "プリセットを適用しました: ") + SelectedPreset.LocalName(L.English));
        ScheduleUpdate();
    }

    private async Task SavePresetAsync()
    {
        var name = await _dialogs.PromptAsync(L["SavePreset"], L["PresetName"], ProjectName);
        if (string.IsNullOrWhiteSpace(name)) return;
        var preset = new BlockPreset { Name = name.Trim(), NameEn = name.Trim(), Keys = [.. CurrentSelection().Keys] };
        var existing = Presets.FirstOrDefault(p => !p.BuiltIn && p.Name == preset.Name);
        if (existing != null) Presets.Remove(existing);
        Presets.Add(preset);
        BlockPreset.SaveUser(Presets);
        SelectedPreset = preset;
        Log((L.English ? "Saved preset: " : "プリセットを保存しました: ") + preset.Name);
    }

    private void DeletePreset()
    {
        if (SelectedPreset is not { BuiltIn: false } p) return;
        Presets.Remove(p);
        BlockPreset.SaveUser(Presets);
        SelectedPreset = Presets.FirstOrDefault();
    }

    // ------------------------------------------------------------------ textures

    private int _iconVersion;

    private async void LoadIcons()
    {
        int version = ++_iconVersion;
        var choices = Rows.SelectMany(r => r.Blocks).ToList();
        if (!Textures.IsAvailable)
        {
            foreach (var c in choices) c.Icon = null;
            return;
        }
        var images = await Task.Run(() => choices.Select(c => Textures.GetIcon(c.Block)).ToList());
        if (version != _iconVersion) return;
        for (int i = 0; i < choices.Count; i++)
            choices[i].Icon = images[i] is { } img ? BitmapHelper.ToBitmap(img) : null;
        UpdateBlockInfo();
    }

    private async Task DownloadTexturesAsync()
    {
        if (Profile is null) return;
        if (!_settings.TexturesAccepted)
        {
            string msg = L.English
                ? "BMAP will download the block textures from Mojang's official repository (github.com/Mojang/bedrock-samples) to this PC.\n\n" +
                  "These files are © Mojang AB and subject to the Minecraft End User License Agreement (https://www.minecraft.net/eula). " +
                  "They are only used to show icons in BMAP and are not redistributed.\n\nDownload now?"
                : "Mojang の公式リポジトリ（github.com/Mojang/bedrock-samples）からブロックのテクスチャをこの PC にダウンロードします。\n\n" +
                  "これらのファイルは © Mojang AB で、Minecraft エンドユーザー使用許諾契約（https://www.minecraft.net/eula）の対象です。" +
                  "BMAP 内でアイコンを表示するためだけに使い、再配布はしません。\n\nダウンロードしますか？";
            if (!await _dialogs.ConfirmAsync(L["DownloadTextures"], msg)) return;
            _settings.TexturesAccepted = true;
            _settings.Save();
        }
        IsBusy = true;
        try
        {
            int n = await Textures.DownloadAsync(Profile.Blocks, new Progress<string>(s => Status = s));
            Log((L.English ? "Textures ready: " : "テクスチャを取得しました: ") + n);
        }
        catch (Exception ex) { Log((L.English ? "Download failed: " : "ダウンロードに失敗しました: ") + ex.Message); }
        finally { IsBusy = false; Status = L["Ready"]; Raise(nameof(TextureStatus)); }
    }

    private async Task UseResourcePackAsync()
    {
        var folder = await _dialogs.PickFolderAsync(L["UseResourcePack"]);
        if (folder is null) return;
        try
        {
            Textures.UseResourcePack(folder);
            _settings.ResourcePack = folder;
            _settings.Save();
            Log((L.English ? "Using resource pack: " : "リソースパックを使用します: ") + folder);
        }
        catch (Exception ex) { Log(ex.Message); }
        Raise(nameof(TextureStatus));
    }

    // ------------------------------------------------------------------ custom blocks

    private void AddCustom()
    {
        string id = CustomId.Trim();
        if (!id.Contains(':')) { Log(L.English ? "Enter a block ID such as myaddon:tile." : "myaddon:tile のようなブロック ID を入力してください。"); return; }
        Dictionary<string, JsonElement> states = [];
        if (!string.IsNullOrWhiteSpace(CustomStates))
        {
            try { states = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(CustomStates) ?? []; }
            catch (JsonException ex) { Log("JSON: " + ex.Message); return; }
        }
        string? color = null;
        if (CustomSet is null)
        {
            try { color = Imaging.Rgba.FromHex(CustomColor).ToHex(); }
            catch { Log(L.English ? "Choose a colour set or enter #RRGGBB." : "色セットを選ぶか #RRGGBB を入力してください。"); return; }
        }
        var block = new CustomBlock
        {
            Key = "custom:" + id + (states.Count > 0 ? "#" + Guid.NewGuid().ToString("N")[..6] : ""),
            Name = id, DisplayName = id, States = states, ColourSetId = CustomSet?.Id, MapColor = color, NeedsSupport = CustomSupport,
        };
        AddCustomBlocks([block]);
        CustomId = CustomStates = CustomColor = "";
    }

    private async Task ImportBehaviorPackAsync(bool folder)
    {
        string? path = folder
            ? await _dialogs.PickFolderAsync(L["ImportBp"])
            : await _dialogs.OpenFileAsync(L["ImportBpFile"], "Behavior pack", ["*.mcpack", "*.mcaddon", "*.zip"]);
        if (path is null) return;
        var warnings = new List<string>();
        try
        {
            var blocks = CustomBlockStore.ScanBehaviorPack(path, warnings);
            foreach (var w in warnings) Log("  " + w);
            if (blocks.Count == 0) { Log(L.English ? "No blocks with minecraft:map_color were found." : "minecraft:map_color を持つブロックは見つかりませんでした。"); return; }
            AddCustomBlocks(blocks);
            Log((L.English ? "Imported custom blocks: " : "カスタムブロックを読み込みました: ") + blocks.Count);
        }
        catch (Exception ex) { Log(ex.Message); }
    }

    private void AddCustomBlocks(IEnumerable<CustomBlock> blocks)
    {
        foreach (var b in blocks)
            if (CustomBlocks.All(c => c.Block.Key != b.Key)) CustomBlocks.Add(new CustomBlockViewModel(b, RemoveCustom));
        CustomBlockStore.Save(CustomBlocks.Select(c => c.Block));
        if (BaseProfile != null) SelectProfile(BaseProfile, CurrentSelection().Keys);
    }

    private void RemoveCustom(CustomBlockViewModel vm)
    {
        CustomBlocks.Remove(vm);
        CustomBlockStore.Save(CustomBlocks.Select(c => c.Block));
        if (BaseProfile != null) SelectProfile(BaseProfile, CurrentSelection().Keys);
    }
}
