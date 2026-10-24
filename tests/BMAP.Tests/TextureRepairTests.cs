using System.Net;
using System.Text;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Textures;

namespace BMAP.Tests;

public sealed class TextureRepairTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bmap-textures-" + Guid.NewGuid().ToString("N"));
    private string Cache => Path.Combine(_directory, "cache");
    private string Pack => Path.Combine(_directory, "pack");
    private const string Blocks = """{"stone":{"textures":{"side":"side","up":"top","down":"top"}}}""";
    private const string Terrain = """{"texture_data":{"side":{"textures":"textures/blocks/side"},"top":{"textures":"textures/blocks/top"}}}""";
    private static BlockDefinition Stone => ColorProfile.LoadBuiltIn().Find("stone")!;
    private static byte[] Png(Rgba colour) => ImageCodec.EncodePng(RgbaImage.Filled(16,16,colour));
    private static void Text(string root, string relative, string content) => AtomicFile.WriteAllText(Path.Combine(root,relative),content);
    private static void Bytes(string root, string relative, byte[] content) => AtomicFile.WriteAllBytes(Path.Combine(root,relative),content);
    private void Metadata()
    {
        Text(Cache,"blocks.json",Blocks);
        Text(Cache,"textures/terrain_texture.json",Terrain);
    }

    [Fact]
    public void CompletionMarker_DoesNotHideMissingWorldFaces()
    {
        Metadata();
        Text(Cache,".complete","old completion marker");
        Bytes(Cache,"textures/blocks/side.png",Png(new Rgba(100,100,100)));
        var coverage = new BlockTextureProvider(Cache).CheckCoverage([Stone]);
        Assert.False(coverage.IsComplete);
        Assert.Equal(0,coverage.MissingIcons);
        Assert.Equal(2,coverage.MissingFaces);
        Assert.Equal("stone",Assert.Single(coverage.MissingBlocks));
    }

    [Fact]
    public async Task Download_RepairsCorruptIconAndMissingFaces_AndClearsNegativeCache()
    {
        Metadata();
        Bytes(Cache,"textures/blocks/side.png",[1,2,3]);
        var provider = new BlockTextureProvider(Cache);
        Assert.Null(provider.GetIcon(Stone));
        using var http = Client(new Dictionary<string,byte[]>
        {
            ["blocks.json"]=Encoding.UTF8.GetBytes(Blocks), ["textures/terrain_texture.json"]=Encoding.UTF8.GetBytes(Terrain),
            ["textures/blocks/side.png"]=Png(new Rgba(90,80,70)), ["textures/blocks/top.png"]=Png(new Rgba(10,20,30)),
        });
        Assert.Equal(2,await provider.DownloadAsync([Stone],http));
        Assert.True(provider.CheckCoverage([Stone]).IsComplete);
        Assert.Equal(new Rgba(90,80,70),provider.GetIcon(Stone)![0,0]);
        Assert.Equal(new Rgba(10,20,30),provider.GetFaceTexture(Stone,0)![0,0]);
        Assert.True(File.Exists(Path.Combine(Cache,".complete")));
    }

    [Fact]
    public async Task DownloadFailure_RemovesStaleMarkerAndKeepsResourcePack()
    {
        Metadata();
        Directory.CreateDirectory(Pack);
        Text(Cache,".complete","stale");
        var provider = new BlockTextureProvider(Cache);
        provider.UseResourcePack(Pack);
        using var http = Client(new Dictionary<string,byte[]>(),HttpStatusCode.InternalServerError);
        await Assert.ThrowsAsync<HttpRequestException>(()=>provider.DownloadAsync([Stone],http));
        Assert.Equal(Pack,provider.ResourcePack);
        Assert.False(File.Exists(Path.Combine(Cache,".complete")));
    }

    [Fact]
    public async Task MissingTexture404_DoesNotWriteACompletionMarker()
    {
        using var http = Client(new Dictionary<string,byte[]>
        {
            ["blocks.json"]=Encoding.UTF8.GetBytes(Blocks), ["textures/terrain_texture.json"]=Encoding.UTF8.GetBytes(Terrain),
        });
        var provider = new BlockTextureProvider(Cache);
        Assert.Equal(0,await provider.DownloadAsync([Stone],http));
        Assert.False(provider.CheckCoverage([Stone]).IsComplete);
        Assert.False(File.Exists(Path.Combine(Cache,".complete")));
    }

    [Fact]
    public void PartialResourcePack_InheritsVanillaMetadataAndCorruptFileFallsBack()
    {
        Metadata();
        var colour=new Rgba(70,80,90);
        Bytes(Cache,"textures/blocks/side.png",Png(colour));
        Bytes(Cache,"textures/blocks/top.png",Png(colour));
        Text(Pack,"blocks.json","""{"custom":{"textures":"custom"}}""");
        Text(Pack,"textures/terrain_texture.json","""{"texture_data":{"custom":{"textures":"textures/blocks/custom"}}}""");
        Bytes(Pack,"textures/blocks/side.png",[1,2,3]);
        var provider=new BlockTextureProvider(Cache);
        provider.UseResourcePack(Pack);
        Assert.True(provider.CheckCoverage([Stone]).IsComplete);
        Assert.Equal(colour,provider.GetIcon(Stone)![0,0]);
    }

    [Fact]
    public void ValidResourcePackTexture_HasPriority_AndMasksDoNotModifySharedRawImage()
    {
        Metadata();
        Text(Pack,"blocks.json","""{"stone":{"textures":"side"},"stone_slab":{"textures":"side"}}""");
        Bytes(Cache,"textures/blocks/side.png",Png(new Rgba(10,20,30)));
        Bytes(Pack,"textures/blocks/side.png",Png(new Rgba(90,80,70)));
        var provider=new BlockTextureProvider(Cache);
        provider.UseResourcePack(Pack);
        var slab = new BlockDefinition{Key="slab",Name="minecraft:stone_slab",DisplayName="slab",MapColor="#A0A0A0"};
        Assert.Equal(0,provider.GetIcon(slab)![0,0].A);
        Assert.Equal(255,provider.GetFaceTexture(slab,0)![0,0].A);
        Assert.Equal(new Rgba(90,80,70),provider.GetIcon(Stone)![0,0]);
    }

    [Fact]
    public void ExplicitPngExtensionAndTrailingDirectorySeparator_Work()
    {
        Metadata();
        Text(Cache,"textures/terrain_texture.json","""{"texture_data":{"side":{"textures":"textures/blocks/side.png"},"top":{"textures":"textures/blocks/side.png"}}}""");
        Bytes(Cache,"textures/blocks/side.png",Png(new Rgba(10,20,30)));
        Assert.True(new BlockTextureProvider(Cache+Path.DirectorySeparatorChar).CheckCoverage([Stone]).IsComplete);
    }

    [Fact]
    public void TexturePath_CannotReadOutsideCache()
    {
        Metadata();
        Text(Cache,"textures/terrain_texture.json","""{"texture_data":{"side":{"textures":"../outside"}}}""");
        Bytes(_directory,"outside.png",Png(new Rgba(1,2,3)));
        Assert.Null(new BlockTextureProvider(Cache).GetIcon(Stone));
    }

    [Theory]
    [InlineData("{\"texture_data\":{\"side\":{\"textures\":{}}}}")]
    [InlineData("{\"texture_data\":{\"side\":{\"textures\":123}}}")]
    public void MalformedTextureEntry_IsReportedAsMissingInsteadOfCrashing(string terrain)
    {
        Metadata();
        Text(Cache,"textures/terrain_texture.json",terrain);
        Assert.False(new BlockTextureProvider(Cache).CheckCoverage([Stone]).IsComplete);
    }

    private static HttpClient Client(Dictionary<string,byte[]> data,HttpStatusCode failure=HttpStatusCode.NotFound) => new(new Handler(data,failure));
    private sealed class Handler(Dictionary<string,byte[]> data,HttpStatusCode failure):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            string relative=request.RequestUri!.AbsoluteUri[BlockTextureProvider.SamplesBase.Length..];
            return Task.FromResult(data.TryGetValue(relative,out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)} : new HttpResponseMessage(failure));
        }
    }

    public void Dispose() { if(Directory.Exists(_directory))Directory.Delete(_directory,true); }
}
