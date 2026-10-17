using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using BMAG.App.ViewModels;

namespace BMAG.App;

public partial class MainWindow : Window, IDialogService
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.S) _vm.SaveProjectCommand.Execute(null);
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.O) _vm.OpenImageCommand.Execute(null);
        };
        // "BMAG.exe picture.png" (Open with…) loads the image on start.
        var startFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        if (startFile != null) Opened += async (_, _) => await _vm.LoadImageAsync(startFile);
        // Keep the log scrolled to the newest line.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogText) && this.FindControl<TextBox>("LogBox") is { } box)
                box.CaretIndex = box.Text?.Length ?? 0;
        };
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (file != null) await _vm.LoadImageAsync(file);
    }

    public async Task<string?> OpenFileAsync(string title, string filterName, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync(string title, string suggestedName, string extension)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message)
    {
        bool result = false;
        var dialog = new Window
        {
            Title = title, Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 700,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
        };
        var ok = new Button { Content = "実行する", Classes = { "accent" } };
        var cancel = new Button { Content = "キャンセル" };
        ok.Click += (_, _) => { result = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 12,
            Children =
            {
                new ScrollViewer { MaxHeight = 560, Content = new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        await dialog.ShowDialog(this);
        return result;
    }
}
