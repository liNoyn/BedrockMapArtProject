using Avalonia.Media;
using Avalonia.Media.Imaging;
using BMAG.BlockEngine;
using BMAG.MapData;
using BMAG.Palette;

namespace BMAG.App.ViewModels;

public sealed class PaletteItemViewModel(BlockDefinition block, ColorProfile profile, bool enabled, Action changed) : ViewModelBase
{
    private bool _isEnabled = enabled;

    public BlockDefinition Block => block;
    public string Name => Block.DisplayName;
    public string Category => BlockCategories.Display(Block.Category);
    public string Identifier => Block.Name + Block.StatesText;
    public IBrush Swatch { get; } = Brush(profile.ColorOf(block, Shade.Normal));
    public string ColorText => profile.ColorOf(block, Shade.Normal).ToHex();
    public string Status => (Block.Verified ? "実測済み" : "推定値") +
                            (Block.Gravity ? "・重力" : "") + (Block.Flammable ? "・可燃" : "") +
                            (Block.BiomeTinted ? "・バイオーム色" : "") + (Block.Rarity >= 2 ? "・希少" : "");

    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (Set(ref _isEnabled, value)) changed(); }
    }

    internal void SetSilently(bool value)
    {
        _isEnabled = value;
        Raise(nameof(IsEnabled));
    }

    public static IBrush Brush(Imaging.Rgba c) => new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
}

public sealed class MaterialViewModel(MaterialLine line, ColorProfile profile)
{
    public string Kind => line.IsSupport ? "支え" : "本体";
    public string Name => line.Block.DisplayName;
    public IBrush Swatch { get; } = PaletteItemViewModel.Brush(profile.ColorOf(line.Block, Shade.Normal));
    public int Count => line.Count;
    public string Stacks => line.Stacks;
    public string Maps => line.MapsText;
    public string Alternatives => line.AlternativesText.Length == 0 ? "" : "代替: " + line.AlternativesText;
}

public sealed class MapEntryViewModel(MapRecord record, Bitmap? thumbnail)
{
    public MapRecord Record { get; } = record;
    public Bitmap? Thumbnail { get; } = thumbnail;
    public string Title => $"map_{Record.Id}";
    public string Info => $"縮尺 {Record.Scale} / 中心 ({FormatCenter(Record.XCenter)}, {FormatCenter(Record.ZCenter)}) / {(Record.Locked ? "ロック" : "未ロック")} / 次元 {Record.Dimension}";

    private static string FormatCenter(int v) => v == int.MaxValue ? "∞" : v.ToString();
}
