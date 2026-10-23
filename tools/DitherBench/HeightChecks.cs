using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

internal static class HeightChecks
{
    public static void Run(List<(string Name,RgbaImage Image)> images,ColorProfile profile,PaletteSelection selection,string output)
    {
        var support=profile.Find("cobblestone")!;
        var rows=new List<object>();
        int failures=0;
        foreach(var (scene,image) in images)
            foreach(int limit in new[]{2,4,8,16})
                foreach(bool grid in new[]{false,true})
                    foreach(var supports in new[]{SupportMode.Important,SupportMode.AllDouble})
                        foreach(var mode in new[]{DitherMode.Optimized,DitherMode.Perceptual,DitherMode.Hybrid})
                        {
                            var options=new QuantizeOptions{Mode=StaircaseMode.Valley,Dither=mode,MaxHeight=limit};
                            var q=HeightPlanner.Quantize(image,profile,selection,options,support,supports,grid);
                            var layout=BlockLayout.Generate(q,support,supports:supports,onMapGrid:grid);
                            var report=ShadingAnalyzer.Analyze(layout,profile,q.Preview);
                            var score=QualityScore.Measure(image,q.Preview);
                            if(layout.MaxHeight>limit || !report.IsExact)failures++;
                            rows.Add(new{Scene=scene,Limit=limit,Grid=grid,Support=supports.ToString(),Model=mode.ToString(),MaxY=layout.MaxHeight,
                                report.IsExact,report.Mismatches,report.DependsOnTerrain,report.WillFall,report.WillBreak,score.Near,score.Far});
                        }
        File.WriteAllText(Path.Combine(output,"height-matrix.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"height matrix: {rows.Count} cases, {failures} failures");
        if(failures>0)throw new InvalidOperationException("Height or map simulation validation failed.");
    }
}
