using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BMAG.BlockEngine;
using BMAG.MapData;
using BMAG.Palette;
using BMAG.Project;

namespace BMAG.App.ViewModels;

/// <summary>A combo box option whose label follows the UI language (non-generic part for XAML templates).</summary>
public abstract class LocOptionBase : ViewModelBase
{
    private readonly Loc _loc;

    protected LocOptionBase(string key, Loc loc)
    {
        Key = key;
        _loc = loc;
        loc.PropertyChanged += (_, _) => Raise(nameof(Label));
    }

    public string Key { get; }
    public string Label => _loc[Key];
    public override string ToString() => Label;
}

public sealed class LocOption<T>(T value, string key, Loc loc) : LocOptionBase(key, loc)
{
    public T Value { get; } = value;
}

/// <summary>One block button in a colour set row.</summary>
public sealed class BlockChoiceViewModel : ViewModelBase
{
    private readonly ColourSetRowViewModel _row;
    private Bitmap? _icon;

    public BlockChoiceViewModel(ColourSetRowViewModel row, BlockDefinition block, Loc loc)
    {
        _row = row;
        Block = block;
        Loc = loc;
        loc.PropertyChanged += (_, _) => Raise(nameof(Tooltip));
    }

    public BlockDefinition Block { get; }
    public Loc Loc { get; }
    public IBrush Swatch => _row.NormalBrush;
    public Bitmap? Icon { get => _icon; set { if (Set(ref _icon, value)) Raise(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;

    public bool IsSelected
    {
        get => _row.Selected == this;
        set
        {
            if (value) _row.Select(this);
            else if (_row.Selected == this) _row.Select(null);
        }
    }

    internal void Refresh() => Raise(nameof(IsSelected));

    public string Tooltip
    {
        get
        {
            var flags = new List<string>();
            if (Block.NeedsSupport) flags.Add(Loc.English ? "needs support" : "支えが必要");
            if (Block.Gravity) flags.Add(Loc.English ? "falls" : "落下する");
            if (Block.Flammable) flags.Add(Loc.English ? "flammable" : "燃える");
            if (Block.BiomeTinted) flags.Add(Loc.English ? "biome tinted" : "バイオームで色が変わる");
            if (Block.Rarity >= 2) flags.Add(Loc.English ? "rare" : "希少");
            if (Block.IsCustom) flags.Add(Loc.English ? "custom" : "カスタム");
            flags.Add(Block.Verified ? (Loc.English ? "measured colour" : "実測色") : (Loc.English ? "estimated colour" : "推定色"));
            return $"{Block.LocalName(Loc.English)}\n{Block.Name}{Block.StatesText}\n{string.Join(" · ", flags)}";
        }
    }
}

/// <summary>A MapartCraft-style row: three shade swatches, "none", then one button per block.</summary>
public sealed class ColourSetRowViewModel : ViewModelBase
{
    private BlockChoiceViewModel? _selected;
    private readonly Action _changed;

    public ColourSetRowViewModel(ColourSet set, ColorProfile profile, Loc loc, Action changed)
    {
        Set = set;
        Loc = loc;
        _changed = changed;
        var baseColor = Imaging.Rgba.FromHex(set.MapColor);
        DarkBrush = Brush(profile.Shaded(baseColor, Shade.Dark));
        NormalBrush = Brush(profile.Shaded(baseColor, Shade.Normal));
        BrightBrush = Brush(profile.Shaded(baseColor, Shade.Bright));
        foreach (var b in set.Blocks) Blocks.Add(new BlockChoiceViewModel(this, b, loc));
        loc.PropertyChanged += (_, _) => Raise(nameof(Name));
    }

    public ColourSet Set { get; }
    public Loc Loc { get; }
    public string Name => Set.LocalName(Loc.English);
    public IBrush DarkBrush { get; }
    public IBrush NormalBrush { get; }
    public IBrush BrightBrush { get; }
    public ObservableCollection<BlockChoiceViewModel> Blocks { get; } = [];
    public BlockChoiceViewModel? Selected => _selected;

    public bool IsNone
    {
        get => _selected is null;
        set { if (value) Select(null); }
    }

    public void Select(BlockChoiceViewModel? choice, bool notify = true)
    {
        if (_selected == choice) { Raise(nameof(IsNone)); return; }
        var old = _selected;
        _selected = choice;
        old?.Refresh();
        choice?.Refresh();
        Raise(nameof(IsNone));
        if (notify) _changed();
    }

    public static IBrush Brush(Imaging.Rgba c) => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
}

public sealed class MaterialViewModel : ViewModelBase
{
    private readonly MaterialLine _line;
    private readonly Action<string, int> _ownedChanged;
    private int _owned;

    public MaterialViewModel(MaterialLine line, ColorProfile profile, Bitmap? icon, int owned, Loc loc, Action<string, int> ownedChanged)
    {
        _line = line;
        _owned = owned;
        _ownedChanged = ownedChanged;
        Icon = icon;
        Loc = loc;
        Swatch = ColourSetRowViewModel.Brush(profile.ColorOf(line.Block, Shade.Normal));
    }

    public Loc Loc { get; }
    public string Key => _line.Block.Key;
    public string Name => _line.Block.LocalName(Loc.English) + (_line.IsSupport ? $" ({Loc["Support"]})" : "");
    public Bitmap? Icon { get; }
    public bool HasIcon => Icon != null;
    public IBrush Swatch { get; }
    public int Count => _line.Count;
    public string Stacks => _line.Stacks;
    public string Maps => _line.MapsText;
    public string Alternatives => _line.AlternativesText.Length == 0 ? "" : (Loc.English ? "Alt: " : "代替: ") + _line.AlternativesText;

    public int Owned
    {
        get => _owned;
        set
        {
            if (!Set(ref _owned, Math.Max(0, value))) return;
            Raise(nameof(Missing));
            Raise(nameof(MissingText));
            Raise(nameof(MissingStacks));
            _ownedChanged(Key, _owned);
        }
    }

    public int Missing => Math.Max(0, Count - Owned);
    public string MissingText => Missing == 0 ? "✓" : $"{Missing:N0}";
    public string MissingStacks => Missing == 0 ? "" : MaterialLine.FormatStacks(Missing, _line.Block.MaxStack);
}

public sealed class MapProgressViewModel : ViewModelBase
{
    private readonly Action<int, ProgressState> _changed;
    private LocOption<ProgressState> _state;
    private string _chunksText = "";

    public MapProgressViewModel(int number, ProgressState state, IReadOnlyList<LocOption<ProgressState>> options, Action<int, ProgressState> changed)
    {
        Number = number;
        Options = options;
        _state = options.First(o => o.Value.Equals(state));
        _changed = changed;
    }

    public int Number { get; }
    public string Title => $"#{Number}";
    public IReadOnlyList<LocOption<ProgressState>> Options { get; }
    public LocOption<ProgressState> State
    {
        get => _state;
        set { if (value != null && Set(ref _state, value)) _changed(Number, value.Value); }
    }
    public string ChunksText { get => _chunksText; set => Set(ref _chunksText, value); }
}

public sealed class MapEntryViewModel(MapRecord record, Bitmap? thumbnail)
{
    public MapRecord Record { get; } = record;
    public Bitmap? Thumbnail { get; } = thumbnail;
    public string Title => $"map_{Record.Id}";
    public string Info => $"scale {Record.Scale} / center ({FormatCenter(Record.XCenter)}, {FormatCenter(Record.ZCenter)}) / {(Record.Locked ? "locked" : "unlocked")} / dim {Record.Dimension}";

    private static string FormatCenter(int v) => v == int.MaxValue ? "∞" : v.ToString();
}

public sealed class CustomBlockViewModel(CustomBlock block, Action<CustomBlockViewModel> remove)
{
    public CustomBlock Block { get; } = block;
    public string Text => $"{Block.Name}  {(Block.ColourSetId is int id ? $"set {id}" : Block.MapColor)}  ({Block.Source})";
    public RelayCommand RemoveCommand => new(() => remove(this));
}
