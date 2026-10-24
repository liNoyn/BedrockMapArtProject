using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;

internal static class PrecisionChecks
{
    public static void Run(ColorProfile profile, PaletteSelection selection, string output)
    {
        var rows = new List<object>();
        foreach (var mode in new[] { StaircaseMode.Flat, StaircaseMode.Valley })
        {
            var colours = ColorQuantizer.BuildCandidates(profile,selection.Resolve(profile),mode).Select(c=>c.Color).Distinct().ToArray();
            foreach (var model in Enum.GetValues<DitherMode>())
            {
                var failures = new List<object>();
                foreach (var colour in colours)
                {
                    var source = RgbaImage.Filled(8,8,colour);
                    var q = ColorQuantizer.Quantize(source,profile,selection,new QuantizeOptions { Mode=mode,Dither=model });
                    int different = Enumerable.Range(0,64).Count(i=>q.Preview[i%8,i/8] != colour);
                    if(different>0) failures.Add(new { Colour=colour.ToHex(),DifferentPixels=different,Quality=QualityScore.Measure(source,q.Preview) });
                }
                rows.Add(new { Model=model.ToString(),Mode=mode.ToString(),Cases=colours.Length,Failed=failures.Count,Failures=failures });
                Console.WriteLine($"{model}/{mode}: {failures.Count}/{colours.Length} exact colours changed");
            }
        }
        File.WriteAllText(Path.Combine(output,"exact-colours.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }
}
