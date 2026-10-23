namespace BMAP.BlockEngine;

/// <summary>
/// Exact discrete quadratic model for multiscale halftoning: C = pixel·I + Σ weight·GᵀG.
/// G is a finite, unit-sum Gaussian; errors outside the image (including empty pixels) are zero.
/// Unlike sampling a continuous autocorrelation, this is the actual energy of the finite filters,
/// including small sigmas, and is positive semidefinite by construction.
/// </summary>
internal sealed class DitherKernel
{
    public int Radius { get; }
    public int Size => Radius * 2 + 1;
    public float[] Values { get; }
    public float Centre => Values[Radius * Size + Radius];

    public DitherKernel(RefineSettings settings)
    {
        Validate(settings.PixelWeight, nameof(settings.PixelWeight));
        Validate(settings.Sigma1, nameof(settings.Sigma1));
        Validate(settings.Sigma2, nameof(settings.Sigma2));
        Validate(settings.Weight1, nameof(settings.Weight1));
        Validate(settings.Weight2, nameof(settings.Weight2));
        if (settings.Sigma1 > 16 || settings.Sigma2 > 16)
            throw new ArgumentOutOfRangeException(nameof(settings), "Dither filter sigma must be at most 16 pixels.");
        int r1 = settings.Weight1 > 0 ? (int)Math.Ceiling(3 * settings.Sigma1) : 0;
        int r2 = settings.Weight2 > 0 ? (int)Math.Ceiling(3 * settings.Sigma2) : 0;
        Radius = Math.Max(1, 2 * Math.Max(r1, r2)); // neighbour swaps also need an offset of one
        var values = new double[Size * Size];
        values[Radius * Size + Radius] = settings.PixelWeight;
        Add(settings.Sigma1, settings.Weight1, r1);
        Add(settings.Sigma2, settings.Weight2, r2);
        Values = Array.ConvertAll(values, v => (float)v);

        void Add(double sigma, double weight, int r)
        {
            if (weight == 0) return;
            if (sigma == 0) { values[Radius * Size + Radius] += weight; return; }
            var g = Gaussian(sigma);
            var correlation = new double[4 * r + 1];
            for (int d = -2 * r; d <= 2 * r; d++)
                for (int i = 0; i < g.Length; i++)
                    if (i + d >= 0 && i + d < g.Length) correlation[d + 2 * r] += g[i] * g[i + d];
            for (int y = -2 * r; y <= 2 * r; y++)
                for (int x = -2 * r; x <= 2 * r; x++)
                    values[(y + Radius) * Size + x + Radius] += weight * correlation[y + 2 * r] * correlation[x + 2 * r];
        }
    }

    internal static double[] Gaussian(double sigma)
    {
        Validate(sigma, nameof(sigma));
        if (sigma == 0) return [1];
        int radius = (int)Math.Ceiling(3 * sigma);
        var g = Enumerable.Range(-radius, 2 * radius + 1).Select(d => Math.Exp(-d * d / (2 * sigma * sigma))).ToArray();
        double sum = g.Sum();
        for (int i = 0; i < g.Length; i++) g[i] /= sum;
        return g;
    }

    private static void Validate(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(name);
    }
}
