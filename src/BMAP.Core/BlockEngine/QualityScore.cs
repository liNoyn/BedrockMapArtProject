using BMAP.ColorEngine;
using BMAP.Imaging;

namespace BMAP.BlockEngine;

/// <summary>
/// How close a map image is to its source, at three viewing distances (CIEDE2000 by default, lower is better):
/// <list type="bullet">
/// <item>Pixel: every pixel on its own (sharpness, noise).</item>
/// <item>Near: blurred by σ 0.8 px in linear light — the preview or a map held in the hand, where single pixels are
/// still almost visible and coloured speckle shows.</item>
/// <item>Far: blurred by σ 2 px — a map art seen from a distance, where only the mixed colour counts.</item>
/// </list>
/// </summary>
public readonly record struct QualityScore(double Pixel, double Near, double Far)
{
    /// <summary>One number for comparisons: speckle seen up close counts most, as people judge previews that way.</summary>
    public double Overall => 0.25 * Pixel + 0.5 * Near + 0.25 * Far;

    public static QualityScore Measure(RgbaImage source, RgbaImage map) => Measure(source,map,ColorMetric.Ciede2000);

    /// <summary>Use the selected matcher's distance at each scale (squared for RGB/Lab/Oklab, CIEDE2000 as ΔE).</summary>
    public static QualityScore Measure(RgbaImage source, RgbaImage map, ColorMetric metric)
        => MeasureWeighted(source,map,metric,null);

    /// <summary>Material-budget comparison: illustration/balance preserve features; other models preserve average colour.</summary>
    public static QualityScore MeasureForModel(RgbaImage source,RgbaImage map,DitherMode model,ColorMetric metric=ColorMetric.Ciede2000)
        => MeasureWeighted(source,map,metric,PaletteWeights(source,map,model));

    internal static double[]? PaletteWeights(RgbaImage source,RgbaImage mask,DitherMode model,CancellationToken ct=default)
    {
        if(source.Width!=mask.Width || source.Height!=mask.Height)throw new ArgumentException("Source and mask dimensions must match.");
        if(model is not (DitherMode.Optimized or DitherMode.Hybrid))return null;
        int w=source.Width,h=source.Height,n=checked(w*h),valid=0;
        var lab=new Lab[n];var weights=new double[n];
        for(int i=0;i<n;i++)
        {
            if((i&16383)==0)ct.ThrowIfCancellationRequested();
            if(mask.Pixels[i*4+3]==0)continue;
            lab[i]=ColorMath.ToOklab(source.Pixels[i*4],source.Pixels[i*4+1],source.Pixels[i*4+2]);valid++;
        }
        double sum=0,max=0;
        for(int y=0;y<h;y++)
        {
            ct.ThrowIfCancellationRequested();
            for(int x=0;x<w;x++)
            {
                int i=y*w+x;if(mask.Pixels[i*4+3]==0)continue;
                double contrast=0;
                // A symmetric derivative misses the centre of a one-pixel line. Neighbour contrast does not.
                for(int yy=Math.Max(0,y-1);yy<=Math.Min(h-1,y+1);yy++)
                    for(int xx=Math.Max(0,x-1);xx<=Math.Min(w-1,x+1);xx++)
                    {
                        int j=yy*w+xx;if(mask.Pixels[j*4+3]==0)continue;
                        contrast=Math.Max(contrast,Math.Sqrt(ColorMath.DeltaE76(lab[i],lab[j])));
                    }
                double strength=Math.Clamp((contrast-.015)/.135,0,1);
                weights[i]=strength;sum+=strength;max=Math.Max(max,strength);
            }
        }
        // Strong rare contours retain a meaningful share at any resolution; weak variation adds less weight.
        double gain=sum>0?max*valid/sum:0;
        for(int i=0;i<n;i++)weights[i]=mask.Pixels[i*4+3]==0?0:1+gain*weights[i];
        return weights;
    }

    private static QualityScore MeasureWeighted(RgbaImage source,RgbaImage map,ColorMetric metric,double[]? importance)
    {
        if (source.Width != map.Width || source.Height != map.Height)
            throw new ArgumentException("Source and map dimensions must match.", nameof(map));
        int w = source.Width, h = source.Height;
        var a = ToLinear(source);
        var b = ToLinear(map);
        return new QualityScore(Mean(a, b, map, w, h, metric,importance), Mean(Blur(a, map, w, h, 0.8), Blur(b, map, w, h, 0.8), map, w, h, metric,importance),
            Mean(Blur(a, map, w, h, 2.0), Blur(b, map, w, h, 2.0), map, w, h, metric,importance));
    }

    private static double[] ToLinear(RgbaImage img)
    {
        var lin = new double[img.Width * img.Height * 3];
        for (int i = 0; i < img.Width * img.Height; i++)
            for (int c = 0; c < 3; c++) lin[i * 3 + c] = ColorMath.SrgbToLinear(img.Pixels[i * 4 + c]);
        return lin;
    }

    /// <summary>Compare both distances while reusing the source and scale preparation; true only for a strict improvement.</summary>
    internal static bool PreferCandidate(RgbaImage source, RgbaImage candidate, RgbaImage baseline, ColorMetric metric, CancellationToken ct = default,double[]? importance=null)
    {
        if (source.Width != candidate.Width || source.Height != candidate.Height || source.Width != baseline.Width || source.Height != baseline.Height)
            throw new ArgumentException("All comparison images must have matching dimensions.");
        ct.ThrowIfCancellationRequested();
        int w=source.Width,h=source.Height;
        bool sameMask=true;
        for(int i=0;i<w*h;i++)
            if ((candidate.Pixels[i*4+3]==0)!=(baseline.Pixels[i*4+3]==0)) { sameMask=false;break; }
        if (!sameMask)
            return Better(MeasureWeighted(source,candidate,ColorMetric.Ciede2000,importance),MeasureWeighted(source,baseline,ColorMetric.Ciede2000,importance),MeasureWeighted(source,candidate,metric,importance),MeasureWeighted(source,baseline,metric,importance));

        (double[] Pixel,double[] Near,double[] Far) Prepare(RgbaImage image)
        {
            ct.ThrowIfCancellationRequested();
            var pixel=ToLinear(image);
            var near=Blur(pixel,baseline,w,h,.8);
            ct.ThrowIfCancellationRequested();
            var far=Blur(pixel,baseline,w,h,2);
            return (pixel,near,far);
        }
        var a=Prepare(source);var b=Prepare(baseline);var c=Prepare(candidate);
        QualityScore Score((double[] Pixel,double[] Near,double[] Far) values,ColorMetric distance) =>
            new(Mean(a.Pixel,values.Pixel,baseline,w,h,distance,importance),Mean(a.Near,values.Near,baseline,w,h,distance,importance),Mean(a.Far,values.Far,baseline,w,h,distance,importance));
        var oldPerceptual=Score(b,ColorMetric.Ciede2000);var newPerceptual=Score(c,ColorMetric.Ciede2000);
        ct.ThrowIfCancellationRequested();
        var oldMetric=metric==ColorMetric.Ciede2000?oldPerceptual:Score(b,metric);
        var newMetric=metric==ColorMetric.Ciede2000?newPerceptual:Score(c,metric);
        return Better(newPerceptual,oldPerceptual,newMetric,oldMetric);

        static bool Better(QualityScore proposed,QualityScore old,QualityScore proposedMetric,QualityScore oldMetric) =>
            proposed.Overall<=old.Overall && proposedMetric.Overall<=oldMetric.Overall
            && (proposed.Overall<old.Overall || proposedMetric.Overall<oldMetric.Overall);
    }

    private static double Mean(double[] a, double[] b, RgbaImage map, int w, int h, ColorMetric metric,double[]? importance=null)
    {
        double sum = 0;
        double n = 0;
        for (int i = 0; i < w * h; i++)
        {
            if (map.Pixels[i * 4 + 3] == 0) continue;
            double ar=ColorMath.LinearToSrgb(a[i*3]),ag=ColorMath.LinearToSrgb(a[i*3+1]),ab=ColorMath.LinearToSrgb(a[i*3+2]);
            double br=ColorMath.LinearToSrgb(b[i*3]),bg=ColorMath.LinearToSrgb(b[i*3+1]),bb=ColorMath.LinearToSrgb(b[i*3+2]);
            double weight=importance?[i]??1;
            sum += weight*(metric switch
            {
                ColorMetric.Rgb => (ar-br)*(ar-br)+(ag-bg)*(ag-bg)+(ab-bb)*(ab-bb),
                ColorMetric.WeightedRgb => ColorMath.WeightedRgbSq(ar,ag,ab,br,bg,bb),
                ColorMetric.Oklab => ColorMath.DeltaE76(ColorMath.ToOklab(ar,ag,ab),ColorMath.ToOklab(br,bg,bb)),
                ColorMetric.Lab => ColorMath.DeltaE76(ColorMath.ToLab(ar,ag,ab),ColorMath.ToLab(br,bg,bb)),
                _ => ColorMath.DeltaE2000(ColorMath.ToLab(ar,ag,ab),ColorMath.ToLab(br,bg,bb)),
            });
            n+=weight;
        }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Separable Gaussian blur of linear values; transparent pixels do not contribute.</summary>
    private static double[] Blur(double[] src, RgbaImage mask, int w, int h, double sigma)
    {
        var valid = Enumerable.Range(0, w * h).Select(i => mask.Pixels[i * 4 + 3] != 0).ToArray();
        return DitherFilter.Blur(src, valid, w, h, sigma);
    }
}
