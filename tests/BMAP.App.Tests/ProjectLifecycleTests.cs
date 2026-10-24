using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using BMAP.App.ViewModels;
using BMAP.BlockEngine;
using BMAP.Imaging;
using BMAP.Project;
using BMAP.Export;

[assembly: AvaloniaTestApplication(typeof(BMAP.App.Tests.TestAppBuilder))]

namespace BMAP.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class ProjectLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bmap-ui-audit-" + Guid.NewGuid().ToString("N"));
    private static MainViewModel Create() => new(new Dialogs(), new AppSettings { TexturesAsked = true });
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static Task Convert(MainViewModel model)
    {
        // Invoke the conversion now, so the normal 300ms timer cannot start a second conversion during this one.
        Field<Avalonia.Threading.DispatcherTimer>(model, "_debounce")?.Stop();
        return (Task)typeof(MainViewModel).GetMethod("UpdateAsync", Private)!.Invoke(model, null)!;
    }
    private static T? Field<T>(MainViewModel model, string name) => (T?)typeof(MainViewModel).GetField(name, Private)!.GetValue(model);

    private static void SetImage(MainViewModel model)
    {
        typeof(MainViewModel).GetField("_source", Private)!.SetValue(model, RgbaImage.Filled(128, 128, new Rgba(137, 186, 222)));
        model.MapsX = 2;
    }

    public ProjectLifecycleTests() => Directory.CreateDirectory(_directory);

    [AvaloniaFact]
    public async Task ConvertedArtwork_CanRenderIn3DWithoutTheRemovedAnalysisPipeline()
    {
        var model=Create();
        SetImage(model);
        await Convert(model);
        model.SelectedPreviewMode=model.PreviewModes.First(p=>p.Value==PreviewMode.BlockMap);
        Assert.True(model.HasPreview);
        Assert.NotEmpty(model.Materials);
        var rendered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged+=(_,e)=>
        {
            if(e.PropertyName==nameof(MainViewModel.View3D) && model.HasView3D)rendered.TrySetResult();
        };
        model.SetViewport3D(320,240);
        model.ViewerOpen=true;
        try
        {
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(320,model.View3D!.PixelSize.Width);
            Assert.Equal(240,model.View3D.PixelSize.Height);
            Assert.True(model.ExportBlockCommand.CanExecute(null));
        }
        finally
        {
            model.ViewerOpen=false;
            model.NewProjectCommand.Execute(null);
        }
    }

    [AvaloniaFact]
    public async Task NewProject_CannotBeRepopulatedByOldConversion()
    {
        var model = Create();
        SetImage(model);
        Task conversion = Convert(model);
        Assert.False(conversion.IsCompleted);
        model.NewProjectCommand.Execute(null);
        await conversion;
        Assert.Null(Field<RgbaImage>(model, "_source"));
        Assert.False(model.HasLayout);
        Assert.False(model.IsConverting);
        Assert.False(model.ExportBlockCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task NewProject_CancelsAnOutstandingImageLoad()
    {
        var model = Create();
        string path = Path.Combine(_directory, "large.png");
        ImageCodec.SavePng(RgbaImage.Filled(2048, 2048, new Rgba(20, 80, 120)), path);
        Task load = model.LoadImageAsync(path);
        model.NewProjectCommand.Execute(null);
        await load;
        Assert.Null(Field<RgbaImage>(model, "_source"));
        Assert.False(model.IsBusy);
        Assert.False(model.HasLayout);
    }

    [AvaloniaFact]
    public async Task NewerImageRequest_WinsAndCompletesBusyState()
    {
        var model = Create();
        string red = Path.Combine(_directory, "red.png"), blue = Path.Combine(_directory, "blue.png");
        ImageCodec.SavePng(RgbaImage.Filled(1024, 1024, new Rgba(255, 0, 0)), red);
        ImageCodec.SavePng(RgbaImage.Filled(8, 8, new Rgba(0, 0, 255)), blue);
        Task first = model.LoadImageAsync(red);
        Task second = model.LoadImageAsync(blue);
        await Task.WhenAll(first, second);
        Assert.Equal(new Rgba(0, 0, 255), Field<RgbaImage>(model, "_source")![0, 0]);
        Assert.False(model.IsBusy);
        model.NewProjectCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task MissingProjectImage_ClearsOldArtworkAndPreservesMissingReference()
    {
        var model = Create();
        SetImage(model);
        await Convert(model);
        Assert.True(model.HasLayout);
        string path = Path.Combine(_directory, "missing.bmap.json");
        new BmapProject { SourceImagePath = "not-found.png", SourceImageHash = "original-hash" }.Save(path);
        await model.OpenProjectFileAsync(path);
        Assert.False(model.HasLayout);
        Assert.False(model.HasPreview);
        var saved = (BmapProject)typeof(MainViewModel).GetMethod("ToProject", Private)!.Invoke(model, null)!;
        Assert.Equal(Path.Combine(_directory, "not-found.png"), saved.SourceImagePath);
        Assert.Equal("original-hash", saved.SourceImageHash);
        model.NewProjectCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task PendingSettings_DisableExportOfPreviousBuild()
    {
        var model = Create();
        SetImage(model);
        await Convert(model);
        Assert.True(model.ExportBlockCommand.CanExecute(null));
        model.DitherStrength = 0.5;
        Assert.True(model.IsConverting);
        Assert.False(model.ExportBlockCommand.CanExecute(null));
        Assert.False(model.ExportMaterialsCommand.CanExecute(null));
        Assert.False(model.SavePreviewPngCommand.CanExecute(null));
        model.NewProjectCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task FailedConversion_DoesNotLeaveOldBuildExportable()
    {
        var model = Create();
        SetImage(model);
        await Convert(model);
        Assert.True(model.HasLayout);
        model.MapsX = 0;
        await Convert(model);
        Assert.False(model.HasLayout);
        Assert.False(model.ExportBlockCommand.CanExecute(null));
        Assert.False(model.IsConverting);
        Assert.Empty(model.Materials);
        model.NewProjectCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task InvalidProject_DoesNotClearTheCurrentArtwork()
    {
        var model = Create();
        SetImage(model);
        await Convert(model);
        string path = Path.Combine(_directory, "invalid.bmap.json");
        File.WriteAllText(path, "{\"Image\":null}");
        await model.OpenProjectFileAsync(path);
        Assert.True(model.HasLayout);
        Assert.NotNull(Field<RgbaImage>(model, "_source"));
        model.NewProjectCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task LegacyMode_IsPreservedWithoutAccumulatingChoices()
    {
        var model = Create();
        foreach (var mode in new[] { DitherMode.KnollBayer, DitherMode.Atkinson, DitherMode.Hybrid })
        {
            string path = Path.Combine(_directory, "mode.bmap.json");
            new BmapProject { Dither = mode }.Save(path);
            await model.OpenProjectFileAsync(path);
            Assert.Equal(mode, model.Dither.Value);
            Assert.Equal(mode == DitherMode.Hybrid ? 4 : 5, model.Dithers.Count);
            Assert.Equal(mode != DitherMode.Hybrid, model.IsLegacyDither);
        }
        model.NewProjectCommand.Execute(null);
        Assert.Equal(4, model.Dithers.Count);
        Assert.Contains(model.Dithers, d => d.Label == "ブレンド");
    }

    [AvaloniaFact]
    public async Task OutputDefaultsTo128_AndExistingSplitChoicesStillLoad()
    {
        var model = Create();
        Assert.Equal(StructureSplit.Tiles128, model.SplitOption.Value);
        Assert.Equal(StructureSplit.Tiles128, new BmapProject().Split);
        foreach (var split in Enum.GetValues<StructureSplit>())
        {
            string path = Path.Combine(_directory, "split.bmap.json");
            new BmapProject { Split = split }.Save(path);
            await model.OpenProjectFileAsync(path);
            Assert.Equal(split, model.SplitOption.Value);
            var saved = (BmapProject)typeof(MainViewModel).GetMethod("ToProject", Private)!.Invoke(model, null)!;
            Assert.Equal(split, saved.Split);
        }
        model.NewProjectCommand.Execute(null);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Dialogs : IDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filterName, string[] patterns) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);
        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
        public Task<string?> PromptAsync(string title, string message, string initial) => Task.FromResult<string?>(null);
        public Task InfoAsync(string title, string message) => Task.CompletedTask;
    }
}
