namespace BMAP.BlockEngine;

/// <summary>
/// A 64x64 blue-noise threshold matrix made with Ulichney's void-and-cluster method (1993). Blue noise has no
/// low-frequency structure, so ordered dithering with it shows neither the cross-hatch of Bayer matrices nor the
/// worms of error diffusion. Generated once, deterministically (fixed seed), instead of shipping a texture.
/// </summary>
public static class BlueNoise
{
    public const int Size = 64;
    private static readonly Lazy<int[]> Matrix = new(() => Generate(Size, 1.5, 0x5EED));

    /// <summary>Ranks 0 .. Size²-1, row-major.</summary>
    public static int[] Ranks => Matrix.Value;

    public static int[] Generate(int size, double sigma, int seed)
    {
        int n = size * size;
        // Toroidal Gaussian kernel, indexed by offset.
        var kernel = new double[n];
        for (int dy = 0; dy < size; dy++)
            for (int dx = 0; dx < size; dx++)
            {
                int ox = Math.Min(dx, size - dx), oy = Math.Min(dy, size - dy);
                kernel[dy * size + dx] = Math.Exp(-(ox * ox + oy * oy) / (2 * sigma * sigma));
            }

        var bits = new bool[n];
        var energy = new double[n];
        void Toggle(int i, bool on)
        {
            bits[i] = on;
            int ix = i % size, iy = i / size;
            double s = on ? 1 : -1;
            for (int y = 0; y < size; y++)
            {
                int ky = ((y - iy) % size + size) % size;
                for (int x = 0; x < size; x++)
                    energy[y * size + x] += s * kernel[ky * size + ((x - ix) % size + size) % size];
            }
        }
        int Tightest() { int best = -1; for (int i = 0; i < n; i++) if (bits[i] && (best < 0 || energy[i] > energy[best])) best = i; return best; }
        int Largest() { int best = -1; for (int i = 0; i < n; i++) if (!bits[i] && (best < 0 || energy[i] < energy[best])) best = i; return best; }

        // Initial pattern: ~10 % random ones, then spread evenly by moving the tightest cluster to the largest void.
        var rng = new Random(seed);
        int ones = n / 10;
        foreach (int i in Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(ones)) Toggle(i, true);
        for (int guard = 0; guard < n * 4; guard++)
        {
            int c = Tightest();
            Toggle(c, false);
            int v = Largest();
            if (v == c) { Toggle(c, true); break; }
            Toggle(v, true);
        }
        var initial = (bool[])bits.Clone();
        var initialEnergy = (double[])energy.Clone();

        var rank = new int[n];
        // Phase 1: remove the tightest clusters of the initial pattern, ranking them downwards.
        for (int r = ones - 1; r >= 0; r--)
        {
            int c = Tightest();
            Toggle(c, false);
            rank[c] = r;
        }
        // Phases 2 and 3: from the initial pattern, fill the largest voids, ranking upwards. (Phase 3's "tightest
        // cluster of zeros" is the zero with the least energy from ones, so one loop covers both.)
        Array.Copy(initial, bits, n);
        Array.Copy(initialEnergy, energy, n);
        for (int r = ones; r < n; r++)
        {
            int v = Largest();
            Toggle(v, true);
            rank[v] = r;
        }
        return rank;
    }
}
