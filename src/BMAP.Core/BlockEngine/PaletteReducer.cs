using BMAP.ColorEngine;
using BMAP.Palette;
using BMAP.Imaging;
using System.Collections.Concurrent;
using System.Text;

namespace BMAP.BlockEngine;

/// <summary>
/// Preserves the unconstrained model's colour distribution under a material budget. Counts alone cannot tell a
/// redundant background shade from a rare contrasting outline. Weighted facility selection and one-entry swaps
/// minimise the colour cost of representing that distribution with the allowed number of palette entries.
/// </summary>
internal static class PaletteReducer
{
    private const int MaxCachedPalettes = 8;
    private const int MaxCachedEntries = 256;
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, Geometry> CachedGeometry = new();
    private static readonly Queue<string> CacheOrder = new();

    // Only exact palette geometry is shared. Source-image counts, selected sets and render scores remain per call.
    private static Geometry GetGeometry(List<Candidate> candidates,int entryCount,ColorMetric metric)
    {
        if (entryCount > MaxCachedEntries) return new Geometry(candidates,entryCount,metric);
        var keyBuilder=new StringBuilder().Append((int)metric).Append(':').Append(entryCount).Append(':');
        foreach(var c in candidates)
            keyBuilder.Append(c.Entry).Append('/').Append((int)c.Shade).Append('/').Append(c.Color.R).Append(',').Append(c.Color.G).Append(',').Append(c.Color.B).Append(';');
        string key=keyBuilder.ToString();
        lock(CacheLock)
        {
            if(CachedGeometry.TryGetValue(key,out var hit))return hit;
            while(CachedGeometry.Count>=MaxCachedPalettes)CachedGeometry.Remove(CacheOrder.Dequeue());
            var geometry=new Geometry(candidates,entryCount,metric);
            CachedGeometry.Add(key,geometry);CacheOrder.Enqueue(key);
            return geometry;
        }
    }

    private sealed class Geometry(List<Candidate> candidates,int entryCount,ColorMetric metric)
    {
        private readonly ColorQuantizer.NearestMatcher _matcher=new(candidates,metric);
        private readonly ConcurrentDictionary<int,double[]> _rows=new();

        public double[] Row(Rgba colour,int shadeMask,CancellationToken ct)
        {
            int key=(shadeMask<<24)|(colour.R<<16)|(colour.G<<8)|colour.B;
            if(_rows.TryGetValue(key,out var hit))return hit;
            var row=new double[entryCount];Array.Fill(row,double.PositiveInfinity);
            for(int c=0;c<candidates.Count;c++)
            {
                if((c&63)==0)ct.ThrowIfCancellationRequested();
                if((shadeMask&(1<<(int)candidates[c].Shade))==0)continue;
                int e=candidates[c].Entry;
                double d=_matcher.Cost(colour.R,colour.G,colour.B,c,0);
                if(d<row[e])row[e]=d;
            }
            // Publish only complete rows. Cancellation cannot poison a cached Lazy or leave a half-filled row.
            return _rows.GetOrAdd(key,row);
        }
    }

    public static List<PaletteEntry> Select(ColorProfile profile, List<PaletteEntry> entries, QuantizeResult full,
        int limit, QuantizeOptions options, CancellationToken ct,double[]? importance=null)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if(importance is not null && importance.Length!=full.EntryIndex.Length)throw new ArgumentException("Importance dimensions must match.",nameof(importance));
        var histogram = new Dictionary<(int Entry, Shade Shade), double>();
        for (int i = 0; i < full.EntryIndex.Length; i++)
        {
            if ((i & 16383) == 0) ct.ThrowIfCancellationRequested();
            int e = full.EntryIndex[i];
            if (e < 0) continue;
            var key = (e,full.Shades[i]);
            histogram[key] = histogram.GetValueOrDefault(key)+(importance?[i]??1);
        }
        if (histogram.Count == 0) return [];
        var samples = histogram.ToArray();
        int n = samples.Length, m = entries.Count;
        var distances = new double[checked(n*m)];
        var candidates = ColorQuantizer.BuildCandidates(profile,entries,full.Mode);
        var geometry = GetGeometry(candidates,m,options.Metric);
        bool sameShade = options.HeightLimited || options.ShadePenalty > 0;
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sample = samples[i].Key;
            var colour = profile.ColorOf(entries[sample.Entry].Block,sample.Shade);
            var row=geometry.Row(colour,sameShade?1<<(int)sample.Shade:0b111,ct);
            row.CopyTo(distances,i*m);
        }

        var selected = new bool[m];
        var best = new double[n];
        Array.Fill(best,double.PositiveInfinity);
        double total = double.PositiveInfinity;
        for (int count = 0; count < Math.Min(limit,m); count++)
        {
            ct.ThrowIfCancellationRequested();
            int add = -1; double score = total;
            for (int c = 0; c < m; c++)
            {
                if (selected[c]) continue;
                double cost = 0;
                for (int i = 0; i < n; i++) cost += samples[i].Value * Math.Min(best[i],distances[i*m+c]);
                if (cost < score) { score = cost; add = c; }
            }
            if (add < 0) break;
            selected[add] = true; total = score;
            for (int i = 0; i < n; i++) best[i] = Math.Min(best[i],distances[i*m+add]);
        }

        // Exact one-entry replacements on this bounded histogram, including colours unused by the initial model.
        // Recompute the objective in a fixed order; every accepted set has a strictly smaller score.
        var nearest = new int[n];
        var second = new double[n];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Array.Fill(best,double.PositiveInfinity); Array.Fill(second,double.PositiveInfinity);
            for (int i = 0; i < n; i++)
                for (int c = 0; c < m; c++)
                {
                    if (!selected[c]) continue;
                    double d = distances[i*m+c];
                    if (d < best[i]) { second[i] = best[i]; best[i] = d; nearest[i] = c; }
                    else if (d < second[i]) second[i] = d;
                }
            total = 0;
            for (int i = 0; i < n; i++) total += samples[i].Value * best[i];
            int remove = -1, add = -1;
            double score = total, tolerance = 1e-12 * Math.Max(1,total);
            for (int a = 0; a < m; a++)
            {
                if (!selected[a]) continue;
                ct.ThrowIfCancellationRequested();
                for (int b = 0; b < m; b++)
                {
                    if (selected[b]) continue;
                    double cost = 0;
                    for (int i = 0; i < n; i++)
                        cost += samples[i].Value * Math.Min(nearest[i] == a ? second[i] : best[i],distances[i*m+b]);
                    if (cost < score-tolerance) { score = cost; remove = a; add = b; }
                }
            }
            if (remove < 0) break;
            selected[remove] = false; selected[add] = true;
        }
        return Enumerable.Range(0,m).Where(i=>selected[i]).Select(i=>entries[i]).ToList();
    }
}
