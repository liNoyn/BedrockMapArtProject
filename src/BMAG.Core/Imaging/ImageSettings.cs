namespace BMAG.Imaging;

public enum FitMode
{
    /// <summary>Keep aspect ratio, letterbox with the background colour.</summary>
    Fit,
    /// <summary>Keep aspect ratio, cut off what does not fit.</summary>
    Fill,
    /// <summary>Ignore aspect ratio.</summary>
    Stretch,
}

public enum Interpolation
{
    Nearest,
    Bilinear,
    Lanczos3,
}

/// <summary>Non-destructive editing settings (design 5.2). The source image is never modified.</summary>
public sealed record ImageSettings
{
    /// <summary>Map grid width in maps (output width = MapsX * 128).</summary>
    public int MapsX { get; init; } = 1;
    public int MapsY { get; init; } = 1;

    /// <summary>Crop rectangle in source pixels; null = whole image.</summary>
    public CropRect? Crop { get; init; }

    public FitMode Fit { get; init; } = FitMode.Fit;
    public Interpolation Interpolation { get; init; } = Interpolation.Lanczos3;
    /// <summary>Resample in linear light (gamma-correct): keeps fine bright/dark detail at its true brightness.</summary>
    public bool LinearLight { get; init; } = true;

    /// <summary>Extra zoom applied after fitting (1 = none). Values above 1 zoom in.</summary>
    public double Zoom { get; init; } = 1.0;
    /// <summary>Pan offset as a fraction of the output size (-1..1) used with Fill / Zoom.</summary>
    public double OffsetX { get; init; }
    public double OffsetY { get; init; }

    /// <summary>-1..1, 0 = unchanged.</summary>
    public double Brightness { get; init; }
    /// <summary>-1..1, 0 = unchanged.</summary>
    public double Contrast { get; init; }
    /// <summary>-1..1, 0 = unchanged, -1 = greyscale.</summary>
    public double Saturation { get; init; }
    /// <summary>Gamma, 1 = unchanged.</summary>
    public double Gamma { get; init; } = 1.0;
    /// <summary>Unsharp-mask amount, 0 = off.</summary>
    public double Sharpen { get; init; }

    /// <summary>Colour used for letterboxing and for compositing transparent pixels.</summary>
    public string BackgroundColor { get; init; } = "#FFFFFF";

    /// <summary>What happens to (semi-)transparent pixels (design 5.4). Default: blend onto the background colour.</summary>
    public TransparencyMode Transparency { get; init; } = TransparencyMode.Smooth;

    /// <summary>Crop mode shown in the UI; <see cref="Crop"/> holds the actual rectangle.</summary>
    public CropMode CropMode { get; init; } = CropMode.Off;

    public int OutputWidth => MapsX * 128;
    public int OutputHeight => MapsY * 128;
}

public sealed record CropRect(int X, int Y, int Width, int Height);

/// <summary>MapartCraft's "background colour" options plus keeping the alpha channel.</summary>
public enum TransparencyMode
{
    /// <summary>Alpha-blend onto the background colour.</summary>
    Smooth,
    /// <summary>Each pixel becomes either the image colour or the background colour, ordered-dithered by its alpha.</summary>
    Dithered,
    /// <summary>Keep transparency: transparent pixels get no block / stay transparent on direct maps.</summary>
    Keep,
}

public enum CropMode
{
    /// <summary>Use the whole image.</summary>
    Off,
    /// <summary>Largest centred region with the output aspect ratio.</summary>
    Center,
    /// <summary>User-defined rectangle.</summary>
    Manual,
}
