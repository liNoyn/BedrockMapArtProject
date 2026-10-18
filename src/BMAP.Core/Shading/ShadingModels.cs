using BMAP.Imaging;
using BMAP.Palette;

namespace BMAP.Shading;

/// <summary>How the shade of a map pixel follows from the height difference to its northern neighbour.</summary>
public enum ShadingModelKind
{
    /// <summary>At least <c>BrightStep</c> higher → light, at least <c>DarkStep</c> lower → dark, otherwise normal.</summary>
    Threshold,
    /// <summary>
    /// Java's map formula d = Δy·4/5 + ((x+z) mod 2 − 0.5)·0.4 (light when d &gt; 0.6, dark when d &lt; −0.6) evaluated
    /// exactly: a step of 1 gives light / dark on every other pixel only (checkerboard); 2 or more always.
    /// Note: in IEEE double arithmetic 0.8 − 0.2 = 0.6000000000000001 &gt; 0.6, so the same formula in Java (and any
    /// double implementation) is identical to <see cref="Threshold"/> with step 1 — the parity term then never matters.
    /// </summary>
    Checker,
    /// <summary>
    /// PowerNukkitX: flat = base colour, darker / lighter by a factor 0.875 − min(5, |Δy|)·0.05 (graded by step size).
    /// </summary>
    Graded,
}

/// <summary>How light and dark colours are rounded.</summary>
public enum ShadeRounding
{
    /// <summary>Each shade = base × m / 255, rounded down per channel (Java, Nukkit).</summary>
    FromBase,
    /// <summary>Normal = base × 220 / 255; dark = ⌊normal × 180 / 220⌋, light = ⌈normal × 255 / 220⌉ (gd-codes).</summary>
    FromNormal,
}

/// <summary>A candidate rule for Bedrock map shading, from an implementation or a measurement.</summary>
public sealed record ShadingModel(string Id, string Name, ShadingModelKind Kind, int BrightStep, int DarkStep,
    ShadeRounding Rounding, string Source)
{
    /// <summary>The shade class for a height difference (Δy = this column − north) at map pixel (x, row).</summary>
    public Shade Classify(int dy, int x, int row) => Kind switch
    {
        ShadingModelKind.Checker => CheckerTenths(dy, x, row) switch { > 6 => Shade.Bright, < -6 => Shade.Dark, _ => Shade.Normal },
        _ => dy >= Math.Max(1, BrightStep) ? Shade.Bright : -dy >= Math.Max(1, DarkStep) ? Shade.Dark : Shade.Normal,
    };

    /// <summary>10·d computed with integers: 8·Δy ± 2 (+2 on odd pixels), so 0.6 is compared exactly.</summary>
    private static int CheckerTenths(int dy, int x, int row) => 8 * dy + (((x + row) & 1) == 1 ? 2 : -2);

    /// <summary>The colour a block with base colour <paramref name="b"/> shows on the map.</summary>
    public Rgba Colour(Rgba b, int dy, int x, int row, ShadeMultipliers m)
    {
        if (Kind == ShadingModelKind.Graded)
        {
            if (dy == 0) return b;
            double f = 0.875 - Math.Min(5, Math.Abs(dy)) * 0.05;
            if (dy < 0) return new Rgba((byte)(b.R * f), (byte)(b.G * f), (byte)(b.B * f));
            int floor = (int)(1.0 / (1.0 - f));
            int Up(int c) => Math.Min(255, (int)((c > 0 && c < floor ? floor : c) / f));
            return b.R == 0 && b.G == 0 && b.B == 0 ? new Rgba((byte)floor, (byte)floor, (byte)floor) : new Rgba((byte)Up(b.R), (byte)Up(b.G), (byte)Up(b.B));
        }
        return Shaded(b, Classify(dy, x, row), m, Rounding);
    }

    public static Rgba Shaded(Rgba b, Shade s, ShadeMultipliers m, ShadeRounding rounding)
    {
        if (rounding == ShadeRounding.FromBase || s == Shade.Normal)
        {
            int k = m.Get(s);
            return new Rgba((byte)(b.R * k / 255), (byte)(b.G * k / 255), (byte)(b.B * k / 255));
        }
        int n = m.Normal;
        byte N(byte c) => (byte)(c * n / 255);
        byte D(byte c) => (byte)Math.Floor(N(c) * (double)m.Dark / n);
        byte L(byte c) => (byte)Math.Min(255, Math.Ceiling(N(c) * (double)m.Bright / n));
        return s == Shade.Dark ? new Rgba(D(b.R), D(b.G), D(b.B)) : new Rgba(L(b.R), L(b.G), L(b.B));
    }

    /// <summary>The candidates compared by calibration (docs/calibration.md).</summary>
    public static IReadOnlyList<ShadingModel> Candidates { get; } =
    [
        new("java-step1", "Java（段差 1）", ShadingModelKind.Threshold, 1, 1, ShadeRounding.FromBase, "Java Edition / BMAP before v0.6"),
        new("step2", "段差 2（gd-codes・Nukkit）", ShadingModelKind.Threshold, 2, 2, ShadeRounding.FromBase, "CloudburstMC/Nukkit Player.java (integer 4/5), gd-codes findYMap (±2)"),
        new("step2-normal", "段差 2・通常色から丸め（gd-codes）", ShadingModelKind.Threshold, 2, 2, ShadeRounding.FromNormal, "gd-codes/mc-pixelart-maker data.js darkPixel / lightPixel"),
        new("checker", "段差 1 は偶奇で交互・2 以上は常に（Bedrock 1.26.51 で実測）", ShadingModelKind.Checker, 2, 2, ShadeRounding.FromBase, "Java MapItem formula with exact arithmetic; measured on Bedrock 1.26.51"),
        new("graded", "段階的（PowerNukkitX）", ShadingModelKind.Graded, 1, 1, ShadeRounding.FromBase, "PowerNukkitX Level.getMapColorAt"),
    ];
}
