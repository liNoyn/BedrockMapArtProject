namespace BMAP.BlockEngine;

/// <summary>Normalized convolution: blur the masked numerator and mask separately, divide only after both axes.</summary>
internal static class DitherFilter
{
    public static double[] Blur(double[] source, bool[] valid, int width, int height, double sigma)
    {
        var kernel = DitherKernel.Gaussian(sigma);
        int radius = kernel.Length / 2, n = checked(width * height);
        if (source.Length != n * 3 || valid.Length != n) throw new ArgumentException("Filter dimensions do not match.");
        var tmp = new double[n * 3];
        var weights = new double[n];
        var result = new double[n * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int d = -radius; d <= radius; d++)
                {
                    int sx = x + d;
                    if (sx < 0 || sx >= width || !valid[y * width + sx]) continue;
                    int i = y * width + x, j = y * width + sx;
                    double k = kernel[d + radius];
                    weights[i] += k;
                    for (int c = 0; c < 3; c++) tmp[i * 3 + c] += k * source[j * 3 + c];
                }
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x;
                double weight = 0;
                for (int d = -radius; d <= radius; d++)
                {
                    int sy = y + d;
                    if (sy < 0 || sy >= height) continue;
                    int j = sy * width + x;
                    double k = kernel[d + radius];
                    weight += k * weights[j];
                    for (int c = 0; c < 3; c++) result[i * 3 + c] += k * tmp[j * 3 + c];
                }
                if (weight > 0) for (int c = 0; c < 3; c++) result[i * 3 + c] /= weight;
            }
        return result;
    }
}
