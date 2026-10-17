using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BMAP.BlockEngine;
using BMAP.Palette;
using BMAP.Project;

namespace BMAP.App.ViewModels;

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

    /// <summary>Selected for generation. Any number of blocks per row can be selected.</summary>
    public bool IsSelected
    {
        get => _row.IsChosen(this);
        set => _row.SetChosen(this, value);
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

/// <summary>A MapartCraft-style row: three shade swatches, "none", then one toggle button per block.</summary>
public sealed class ColourSetRowViewModel : ViewModelBase
{
    private readonly HashSet<BlockChoiceViewModel> _chosen = [];
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
    public IEnumerable<BlockChoiceViewModel> Chosen => Blocks.Where(_chosen.Contains);
    public int ChosenCount => _chosen.Count;
    /// <summary>Shown next to the swatch when more than one block shares this colour.</summary>
    public string CountBadge => _chosen.Count > 1 ? $"×{_chosen.Count}" : "";

    /// <summary>"None" button: checked when nothing is selected, clicking it clears the row.</summary>
    public bool IsNone
    {
        get => _chosen.Count == 0;
        set { if (value) Clear(); else Raise(nameof(IsNone)); }
    }

    public bool IsChosen(BlockChoiceViewModel choice) => _chosen.Contains(choice);

    public void SetChosen(BlockChoiceViewModel choice, bool on, bool notify = true)
    {
        bool changed = on ? _chosen.Add(choice) : _chosen.Remove(choice);
        choice.Refresh();
        if (!changed) return;
        RaiseCounts();
        if (notify) _changed();
    }

    public void Clear(bool notify = true)
    {
        if (_chosen.Count == 0) { Raise(nameof(IsNone)); return; }
        var old = _chosen.ToList();
        _chosen.Clear();
        foreach (var c in old) c.Refresh();
        RaiseCounts();
        if (notify) _changed();
    }

    /// <summary>Replaces the selection without notifying (used when applying presets / projects).</summary>
    public void Load(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet();
        _chosen.Clear();
        foreach (var b in Blocks)
        {
            if (set.Contains(b.Block.Key)) _chosen.Add(b);
            b.Refresh();
        }
        RaiseCounts();
    }

    private void RaiseCounts()
    {
        Raise(nameof(IsNone));
        Raise(nameof(ChosenCount));
        Raise(nameof(CountBadge));
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

public sealed class CustomBlockViewModel(CustomBlock block, Action<CustomBlockViewModel> remove)
{
    public CustomBlock Block { get; } = block;
    public string Text => $"{Block.Name}  {(Block.ColourSetId is int id ? $"set {id}" : Block.MapColor)}  ({Block.Source})";
    public RelayCommand RemoveCommand => new(() => remove(this));
}

/// <summary>A paint brush: a block, "no block" or "back to the automatic result".</summary>
public sealed record BrushOption(string Key, string Label, BlockDefinition? Block)
{
    public const string Auto = "<auto>";
    public const string None = "<none>";
    public override string ToString() => Label;
}

/// <summary>One row of the "best height" analysis table.</summary>
public sealed class HeightRowViewModel(HeightOption option, Loc loc, Action<HeightOption> apply)
{
    public HeightOption Option { get; } = option;
    public string LimitText => Option.Mode == StaircaseMode.Flat ? loc["Flat"] : Option.Limit == 0 ? loc["Unlimited"] : $"≤ {Option.Limit}";
    public string HeightText => loc.Format("BlocksTall", Option.BuildHeight);
    public string DeltaText => $"ΔE {Option.MeanDeltaE:0.00}";
    public double Quality => Option.Quality;
    public string QualityText => $"{Option.Quality:0}%";
    public bool IsRecommended => Option.Recommended;
    public string Badge => Option.Recommended ? "★ " + loc["Recommended"] : "";
    public RelayCommand ApplyCommand => new(() => apply(Option));
}
