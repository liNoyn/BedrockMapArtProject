using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using BMAP.BlockEngine;
using BMAP.Export;
using BMAP.Imaging;
using BMAP.Palette;
using BMAP.Project;

namespace BMAP.Tests;

public sealed class SystemAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bmap-system-audit-" + Guid.NewGuid().ToString("N"));
    private string FilePath(string name) => Path.Combine(_directory, name);

    public SystemAuditTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void FailedWrite_KeepsOldFileAndCleansTemporaryFile()
    {
        string path = FilePath("project.json");
        File.WriteAllText(path, "original");
        Assert.Throws<IOException>(() => AtomicFile.Write(path, stream =>
        {
            stream.Write(Encoding.UTF8.GetBytes("partial replacement"));
            throw new IOException("simulated disk failure");
        }));
        Assert.Equal("original", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public void FailedFirstWrite_DoesNotPublishPartialFile()
    {
        string path = FilePath("new.json");
        Assert.Throws<IOException>(() => AtomicFile.Write(path, stream =>
        {
            stream.WriteByte(42);
            throw new IOException("simulated failure");
        }));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public void ProjectSave_CreatesParentsAndReplacesOldData()
    {
        string path = FilePath("nested/日本語.bmap.json");
        new BmapProject { ProjectName = "before" }.Save(path);
        new BmapProject { ProjectName = "after" }.Save(path);
        Assert.Equal("after", BmapProject.Load(path).ProjectName);
    }

    [Theory]
    [InlineData("{\"Image\":null}")]
    [InlineData("{\"Image\":{\"MapsX\":0}}")]
    [InlineData("{\"Image\":{\"MapsX\":2147483647,\"MapsY\":2147483647}}")]
    [InlineData("{\"Dither\":999}")]
    [InlineData("{\"Image\":{\"Fit\":999}}")]
    [InlineData("{\"FormatVersion\":3}")]
    [InlineData("{\"MapProgress\":{\"1\":999}}")]
    [InlineData("{\"ProjectName\":null}")]
    public void InvalidProject_IsRejectedBeforeApplyingIt(string json)
    {
        string path = FilePath("invalid.bmap.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => BmapProject.Load(path));
    }

    [Fact]
    public void EveryDitherMode_IncludingLegacy_RoundTrips()
    {
        string path = FilePath("legacy.bmap.json");
        foreach (var mode in Enum.GetValues<DitherMode>())
        {
            new BmapProject { Dither = mode }.Save(path);
            Assert.Equal(mode, BmapProject.Load(path).Dither);
        }
        File.WriteAllText(path, "{\"MapProgress\":null,\"DoneChunks\":null,\"OwnedMaterials\":null}");
        var restored = BmapProject.Load(path);
        Assert.Empty(restored.MapProgress);
        Assert.Empty(restored.DoneChunks);
        Assert.Empty(restored.OwnedMaterials);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("/outside")]
    [InlineData("C:\\outside")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    public void UserProfile_CannotEscapeItsStorageDirectory(string id)
    {
        var profile = new ColorProfile { Id = id, Name = "invalid" };
        Assert.Throws<InvalidDataException>(() => UserProfiles.Save(profile));
    }

    [Fact]
    public void PngSave_ReplacesAndRoundTripsAlpha()
    {
        string path = FilePath("nested/preview.png");
        var image = TestHelpers.Gradient(37, 23, alpha: true);
        ImageCodec.SavePng(RgbaImage.Filled(1, 1, new Rgba(0, 0, 0)), path);
        ImageCodec.SavePng(image, path);
        Assert.Equal(image.Pixels, ImageCodec.Load(path).Pixels);
    }

    [Fact]
    public void InvalidImageDimensions_DoNotWrapToASmallerBuffer()
    {
        Assert.Throws<OverflowException>(() => new RgbaImage(1 << 30, 4));
        Assert.Throws<OverflowException>(() => new RgbaImage(1 << 30, 4, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RgbaImage(0, 4, []));
    }

    [Fact]
    public void DamagedEdits_KeepValidEditsWithoutCrashing()
    {
        var edits = PixelOverrides.Deserialize([null!, "", "1,2,stone,invalid", "1,2,stone,2147483648", "3,4,stone,1"]);
        Assert.Equal("3,4,stone,1", Assert.Single(edits.Serialize()));
    }

    [Fact]
    public void ZipFailure_KeepsPreviousArchive()
    {
        string source = FilePath("piece.txt"), archive = FilePath("build.zip");
        File.WriteAllText(source, "piece");
        StructureExporter.ZipFiles([source], archive);
        byte[] before = File.ReadAllBytes(archive);
        Assert.Throws<FileNotFoundException>(() => StructureExporter.ZipFiles([source, FilePath("missing.txt")], archive));
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void ZipInsideSourceDirectory_CanBeReplacedWithoutIncludingItself()
    {
        File.WriteAllText(FilePath("piece.txt"), "piece");
        string archive = FilePath("build.zip");
        StructureExporter.ZipDirectory(_directory, archive);
        StructureExporter.ZipDirectory(_directory, archive);
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal("piece.txt", Assert.Single(zip.Entries).FullName);
    }

    [Fact]
    public void ExportFailure_DoesNotDeleteThePreviousPieces()
    {
        if (!OperatingSystem.IsWindows()) return;
        var profile = ColorProfile.LoadBuiltIn();
        var image = RgbaImage.Filled(4, 4, new Rgba(100, 100, 100));
        var q = ColorQuantizer.Quantize(image, profile, BlockPreset.BuiltIns(profile)[0].ToSelection(), new QuantizeOptions());
        var layout = BlockLayout.Generate(q, profile.Find("cobblestone")!);
        var options = new StructureExportOptions();
        var result = StructureExporter.ExportStructuresOnly(_directory, "audit", layout, 1, options, null);
        Assert.True(result.Files.Count >= 2);
        byte[] first = File.ReadAllBytes(result.Files[0]);
        using var locked = new FileStream(result.Files[1], FileMode.Open, FileAccess.Read, FileShare.Read);
        var failure = Record.Exception(() => StructureExporter.ExportStructuresOnly(_directory, "audit", layout, 1, options, null));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(first, File.ReadAllBytes(result.Files[0]));
        Assert.All(result.Files, file => Assert.True(File.Exists(file)));
    }

    [Theory]
    [InlineData("comma,name", "\"comma,name\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("first\nsecond", "\"first\nsecond\"")]
    [InlineData("first\rsecond", "\"first\rsecond\"")]
    public void Csv_QuotesFieldDelimiters(string value, string expected) => Assert.Equal(expected, BuildGuideGenerator.Csv(value));

    [Fact]
    public void MaterialsCsv_RemainsValidUnderCommaDecimalCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var line = new MaterialLine { Block = ColorProfile.LoadBuiltIn().Find("cobblestone")!, Count = 1728 };
            Assert.Equal("1.00", line.ShulkerBoxes);
            Assert.Contains(",1.00,", BuildGuideGenerator.MaterialsCsv([line]));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static ProcessStartInfo PowerShell(string script)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"));
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(script);
        return start;
    }

    [Fact]
    public async Task ExternalProcess_TimeoutIsEffectiveWhileStderrIsOpen()
    {
        if (!OperatingSystem.IsWindows()) return;
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(PowerShell("Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(500)));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExternalProcess_DrainsBothPipesAndPreservesExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        var result = await ProcessRunner.RunAsync(PowerShell("[Console]::Out.Write(('x' * 100000)); [Console]::Error.Write(('y' * 100000)); exit 7"), TimeSpan.FromSeconds(10));
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(100000, result.Output.Length);
        Assert.Equal(100000, result.Error.Length);
    }

    [Fact]
    public async Task ExternalProcess_CanBeCancelled()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(PowerShell("Start-Sleep -Seconds 30"), TimeSpan.FromSeconds(30), cts.Token));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
