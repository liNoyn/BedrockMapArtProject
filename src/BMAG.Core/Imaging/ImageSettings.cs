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

    /// <summary>When false, transparent pixels are composited onto the background colour (default, design 5.4).</summary>
    public bool KeepTransparency { get; init; }

    public int OutputWidth => MapsX * 128;
    public int OutputHeight => MapsY * 128;
}

public sealed record CropRect(int X, int Y, int Width, int Height);
