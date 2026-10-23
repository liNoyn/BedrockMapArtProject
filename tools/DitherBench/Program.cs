using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BMAP.BlockEngine;
using BMAP.ColorEngine;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Shading;

// DitherBench <output> [size=96] [repeats=3] [optional image directory] [speed|height]
// CoreProject may point to a source snapshot for before/after comparisons with exactly the same harness.
int size = args.Length > 1 ? int.Parse(args[1]) : 96;
int repeats = args.Length > 2 ? int.Parse(args[2]) : 3;
if (size < 8 || repeats < 1) throw new ArgumentOutOfRangeException(nameof(args));
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
var profile = ColorProfile.LoadBuiltIn();
var selection = BlockPreset.BuiltIns(profile)[0].ToSelection();
var support = profile.Find("cobblestone")!;
var images = new List<(string Name, RgbaImage Image)>();
foreach (string name in new[] { "gradient", "sky", "illustration", "mixed", "skin", "texture", "alpha", "flat" })
{
    var image = new RgbaImage(size,size); var random = new Random(7429);
    for (int y=0;y<size;y++) for(int x=0;x<size;x++)
    {
        double u=x/(size-1.0),v=y/(size-1.0);
        byte B(double value)=>(byte)Math.Clamp(Math.Round(value),0,255);
        Rgba ill = (u-.62)*(u-.62)+(v-.36)*(v-.36)<.08 ? Rgba.FromHex("#7E7CCB")
            : (u-.3)*(u-.3)+(v-.7)*(v-.7)<.055 ? Rgba.FromHex("#FCBCD4") : new Rgba(245,240,235);
        image[x,y] = name switch
        {
            "gradient"=>new Rgba(B(255*u),B(255*v),B(255*(1-u)*v)),
            "sky"=>new Rgba(B(130+30*v),B(175+35*v),B(225+20*v)),
            "illustration"=>ill,
            "mixed"=>u<.5?ill:new Rgba(B(180+50*Math.Sin(4*u)*v),B(100+80*v),B(80+70*u)),
            "skin"=>new Rgba(B(150+100*u),B(90+110*u+10*v),B(65+110*u+10*v)),
            "texture"=>new Rgba(B(115+70*Math.Sin(x*.12)*Math.Cos(y*.08)+random.Next(-10,11)),B(130+70*v+random.Next(-10,11)),B(75+80*u+random.Next(-10,11))),
            "alpha"=>(x+y)%17<4?new Rgba(255,0,255,64):new Rgba(B(30+180*u),B(60+170*v),B(100+90*u)),
            _=>Rgba.FromHex("#FCBCD4"),
        };
    }
    images.Add((name,image));
}
if(args.Length>3)
    foreach(string path in Directory.EnumerateFiles(args[3]).Where(p=>new[]{".png",".jpg",".jpeg"}.Contains(Path.GetExtension(p).ToLowerInvariant())).Order())
    {
        var input=ImageCodec.Load(path);
        // The same linear-light resampler is used by every model; benchmark dimensions are independent of map size.
        images.Add((Path.GetFileNameWithoutExtension(path),Resampler.Resample(input,0,0,input.Width,input.Height,size,size,Interpolation.Lanczos3,linearLight:true)));
    }
if(args.Length>4 && args[4]=="height")
{
    HeightChecks.Run(images.Where(i=>i.Name is "mixed" or "astronaut" or "coffee").ToList(),profile,selection,output);
    return;
}
bool speedOnly = args.Length > 4 && args[4] == "speed";
if(speedOnly) images = images.Where(i=>i.Name is "gradient" or "illustration" or "astronaut" or "coffee").ToList();
var conditions = speedOnly ? new[]{(StaircaseMode.Valley,0),(StaircaseMode.Valley,4)}
    : new[]{(StaircaseMode.Flat,0),(StaircaseMode.Classic,0),(StaircaseMode.Valley,0),(StaircaseMode.Valley,4)};
var rows = new List<Row>();
var models = new[]{DitherMode.Optimized,DitherMode.Perceptual,DitherMode.Hybrid};
// Warm JIT and parallel workers, without timing these runs.
foreach(var model in models) ColorQuantizer.Quantize(images[0].Image,profile,selection,new QuantizeOptions{Dither=model,Mode=StaircaseMode.Valley});
foreach(var (name,image) in images)
{
    ImageCodec.SavePng(image,Path.Combine(output,name+"-source.png"));
    double flat=ColorQuantizer.FlatAreas(image,128).Count(v=>v)/(double)(size*size);
    foreach(var (mode,limit) in conditions)
        for(int modelIndex=0;modelIndex<models.Length;modelIndex++)
        {
            // Rotate order across scenes and conditions to reduce systematic warm-cache bias.
            var model=models[(modelIndex+images.FindIndex(i=>i.Name==name)+(int)mode+limit)%models.Length];
            var times=new List<double>(); QuantizeResult? result=null;
            for(int run=0;run<repeats;run++)
            {
                var sw=Stopwatch.StartNew();
                result=HeightPlanner.Quantize(image,profile,selection,new QuantizeOptions{Dither=model,Mode=mode,MaxHeight=limit},support,SupportMode.Important,true);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            var layout=BlockLayout.Generate(result!,support,supports:SupportMode.Important,onMapGrid:true);
            var report=ShadingAnalyzer.Analyze(layout,profile,result!.Preview);
            var score=QualityScore.Measure(image,result.Preview);
            var row=new Row(name,mode.ToString(),limit,model.ToString(),size,times[times.Count/2],score.Pixel,score.Near,score.Far,score.Overall,
                Roughness(image,result.Preview),flat,layout.MaxHeight,report.Mismatches,report.DependsOnTerrain,report.WillFall+report.WillBreak);
            rows.Add(row);Console.WriteLine(JsonSerializer.Serialize(row));
            ImageCodec.SavePng(result.Preview,Path.Combine(output,$"{name}-{mode}-{limit}-{model}.png"));
        }
}
File.WriteAllText(Path.Combine(output,"metrics.json"),JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
File.WriteAllText(Path.Combine(output,"environment.json"),JsonSerializer.Serialize(new{Runtime=Environment.Version.ToString(),OS=Environment.OSVersion.ToString(),Environment.ProcessorCount,Size=size,Repeats=repeats,Support="Important",OnMapGrid=true}));
File.WriteAllLines(Path.Combine(output,"metrics.csv"),new[]{"Scene,Mode,Limit,Model,Size,MedianMs,Pixel,Near,Far,Overall,Roughness,FlatFraction,MaxY,Mismatches,Terrain,Unstable"}.Concat(rows.Select(r=>string.Join(",",r.Scene,r.Mode,r.Limit,r.Model,r.Size,F(r.MedianMs),F(r.Pixel),F(r.Near),F(r.Far),F(r.Overall),F(r.Roughness),F(r.FlatFraction),r.MaxY,r.Mismatches,r.Terrain,r.Unstable))));
static string F(double v)=>v.ToString("G9",CultureInfo.InvariantCulture);
static double Roughness(RgbaImage source,RgbaImage map)
{
    var error=new Lab[source.Width*source.Height];
    for(int i=0;i<error.Length;i++)
    {
        int p=i*4;var a=ColorMath.ToOklab(source.Pixels[p],source.Pixels[p+1],source.Pixels[p+2]);var b=ColorMath.ToOklab(map.Pixels[p],map.Pixels[p+1],map.Pixels[p+2]);
        error[i]=new Lab(b.L-a.L,b.A-a.A,b.B-a.B);
    }
    double sum=0;int n=0;
    for(int y=0;y<map.Height;y++)for(int x=0;x<map.Width;x++)
    {
        int i=y*map.Width+x;if(map.Pixels[i*4+3]==0)continue;
        if(x+1<map.Width)Add(i+1);if(y+1<map.Height)Add(i+map.Width);
        void Add(int j){if(map.Pixels[j*4+3]==0)return;sum+=ColorMath.DeltaE76(error[i],error[j]);n++;}
    }
    return n==0?0:100*Math.Sqrt(sum/n);
}
record Row(string Scene,string Mode,int Limit,string Model,int Size,double MedianMs,double Pixel,double Near,double Far,double Overall,double Roughness,double FlatFraction,int MaxY,int Mismatches,int Terrain,int Unstable);
