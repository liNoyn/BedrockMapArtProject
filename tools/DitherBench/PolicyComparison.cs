using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;

/// <summary>Rescore saved PNGs with the current model policy, without rerunning either converter.</summary>
internal static class PolicyComparison
{
    public static void Run(string beforeDirectory,string afterDirectory,string output)
    {
        using var before=JsonDocument.Parse(File.ReadAllText(Path.Combine(beforeDirectory,"metrics.json")));
        using var after=JsonDocument.Parse(File.ReadAllText(Path.Combine(afterDirectory,"metrics.json")));
        static string Key(JsonElement row)=>$"{row.GetProperty("Scene")}/{row.GetProperty("Mode")}/{row.GetProperty("Limit")}/{row.GetProperty("Model")}/{row.GetProperty("Size")}/{row.GetProperty("PaletteLimit")}";
        var baseline=before.RootElement.EnumerateArray().ToDictionary(Key);
        if(baseline.Count!=after.RootElement.GetArrayLength())throw new ArgumentException("Benchmark conditions must match.");
        var rows=new List<object>();
        foreach(var row in after.RootElement.EnumerateArray())
        {
            var old=baseline[Key(row)];
            string scene=row.GetProperty("Scene").GetString()!,mode=row.GetProperty("Mode").GetString()!;
            int limit=row.GetProperty("Limit").GetInt32();
            var model=Enum.Parse<DitherMode>(row.GetProperty("Model").GetString()!);
            string file=$"{scene}-{mode}-{limit}-{model}.png";
            var source=ImageCodec.Load(Path.Combine(afterDirectory,scene+"-source.png"));
            var oldSource=ImageCodec.Load(Path.Combine(beforeDirectory,scene+"-source.png"));
            if(source.Width!=oldSource.Width || source.Height!=oldSource.Height || !source.Pixels.SequenceEqual(oldSource.Pixels))
                throw new ArgumentException($"Source changed: {scene}");
            var a=ImageCodec.Load(Path.Combine(beforeDirectory,file));
            var b=ImageCodec.Load(Path.Combine(afterDirectory,file));
            rows.Add(new
            {
                Scene=scene,Mode=mode,Limit=limit,Model=model.ToString(),Size=source.Width,PaletteLimit=row.GetProperty("PaletteLimit").GetInt32(),
                ModelBefore=QualityScore.MeasureForModel(source,a,model).Overall,ModelAfter=QualityScore.MeasureForModel(source,b,model).Overall,
                MetricBefore=QualityScore.MeasureForModel(source,a,model,ColorMetric.Oklab).Overall,
                MetricAfter=QualityScore.MeasureForModel(source,b,model,ColorMetric.Oklab).Overall,
                MeanBefore=old.GetProperty("Overall").GetDouble(),MeanAfter=row.GetProperty("Overall").GetDouble(),
                BeforeMs=old.GetProperty("MedianMs").GetDouble(),AfterMs=row.GetProperty("MedianMs").GetDouble(),
                IdenticalPixels=a.Width==b.Width && a.Height==b.Height && a.Pixels.SequenceEqual(b.Pixels),
            });
        }
        File.WriteAllText(output,JsonSerializer.Serialize(rows,new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine($"Model-policy comparison: {rows.Count} cases. Matcher-distance columns use the benchmark's default Oklab.");
    }
}
