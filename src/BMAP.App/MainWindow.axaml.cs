using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using BMAP.App.ViewModels;

namespace BMAP.App;

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
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) _vm.UndoEditCommand.Execute(null);
        };
        // "BMAP.exe picture.png" (Open with…) loads the image on start.
        var startFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        if (startFile != null)
            Opened += async (_, _) =>
            {
                if (AppPaths.IsProjectFile(startFile)) await _vm.OpenProjectFileAsync(startFile);
                else await _vm.LoadImageAsync(startFile);
            };
        // Keep the log scrolled to the newest line.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LogText) && this.FindControl<TextBox>("LogBox") is { } box)
                box.CaretIndex = box.Text?.Length ?? 0;
        };
        SetupCropDrag();
        SetupPreviewSize();
        _vm.OpenViewerRequested += OpenViewer;
        Closed += (_, _) => { _viewer?.Close(); _zoomWindow?.Close(); };
    }

    private ViewerWindow? _viewer;
    private PreviewWindow? _zoomWindow;

    /// <summary>The square preview follows the width of its column; "Enlarge" or a double-click opens it large.</summary>
    private void SetupPreviewSize()
    {
        if (this.FindControl<ScrollViewer>("PreviewColumn") is not { } column || this.FindControl<Border>("PreviewBox") is not { } box) return;
        column.SizeChanged += (_, _) =>
        {
            double side = Math.Max(48, Math.Floor(column.Bounds.Width - 20));
            box.Width = box.Height = side;
        };
        if (this.FindControl<Button>("ZoomButton") is { } zoom) zoom.Click += (_, _) => OpenZoom();
        box.DoubleTapped += (_, _) => { if (_vm.HasPreview) OpenZoom(); };
    }

    private void OpenZoom()
    {
        if (_zoomWindow is { } w) { w.Activate(); return; }
        _zoomWindow = new PreviewWindow(_vm);
        _zoomWindow.Closed += (_, _) => _zoomWindow = null;
        _zoomWindow.Show(this);
    }

    /// <summary>"View in 3D": one viewer window, brought to the front when it is already open.</summary>
    private void OpenViewer()
    {
        if (_viewer is { } v) { v.Activate(); return; }
        _viewer = new ViewerWindow(_vm);
        _viewer.Closed += (_, _) => _viewer = null;
        _viewer.Show(this);
    }

    /// <summary>Drag a rectangle on the Original view to set a manual crop.</summary>
    private void SetupCropDrag()
    {
        if (this.FindControl<Image>("PreviewImage") is not { } img || this.FindControl<Canvas>("CropOverlay") is not { } canvas
            || this.FindControl<Avalonia.Controls.Shapes.Rectangle>("CropRect") is not { } rect) return;
        Avalonia.Point? start = null;

        // The bitmap is drawn uniformly scaled and centred inside the control.
        Avalonia.Rect ShownRect()
        {
            if (img.Source is not { } src || img.Bounds.Width <= 0) return default;
            double scale = Math.Min(img.Bounds.Width / src.Size.Width, img.Bounds.Height / src.Size.Height);
            double w = src.Size.Width * scale, h = src.Size.Height * scale;
            return new Avalonia.Rect((img.Bounds.Width - w) / 2, (img.Bounds.Height - h) / 2, w, h);
        }

        img.PointerPressed += (_, e) =>
        {
            if (_vm.SelectedPreviewMode.Value != PreviewMode.Original || !e.GetCurrentPoint(img).Properties.IsLeftButtonPressed) return;
            start = e.GetPosition(img);
            e.Pointer.Capture(img);
        };
        img.PointerMoved += (_, e) =>
        {
            if (start is not { } s) return;
            var p = e.GetPosition(img);
            var a = img.TranslatePoint(new Avalonia.Point(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y)), canvas) ?? default;
            Canvas.SetLeft(rect, a.X);
            Canvas.SetTop(rect, a.Y);
            rect.Width = Math.Abs(p.X - s.X);
            rect.Height = Math.Abs(p.Y - s.Y);
            rect.IsVisible = true;
        };
        img.PointerReleased += (_, e) =>
        {
            if (start is not { } s) return;
            start = null;
            e.Pointer.Capture(null);
            rect.IsVisible = false;
            var r = ShownRect();
            if (r.Width <= 0) return;
            var p = e.GetPosition(img);
            _vm.SetCropFromFractions((s.X - r.X) / r.Width, (s.Y - r.Y) / r.Height, (p.X - r.X) / r.Width, (p.Y - r.Y) / r.Height);
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
        var dialog = MakeDialog(title);
        var ok = new Button { Content = _vm.L["Run"], Classes = { "accent" } };
        var cancel = new Button { Content = _vm.L["Cancel"] };
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

    public async Task<string?> PromptAsync(string title, string message, string initial)
    {
        string? result = null;
        var dialog = MakeDialog(title);
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", Classes = { "accent" } };
        var cancel = new Button { Content = _vm.L["Cancel"] };
        ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 10,
            Children =
            {
                new TextBlock { Text = message },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(this);
        return result;
    }

    public async Task InfoAsync(string title, string message)
    {
        var dialog = MakeDialog(title);
        var ok = new Button { Content = "OK", Classes = { "accent" } };
        ok.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(16), Spacing = 12,
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok } },
            },
        };
        await dialog.ShowDialog(this);
    }

    private static Window MakeDialog(string title) => new()
    {
        Title = title, Width = 620, SizeToContent = SizeToContent.Height,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, MinWidth = 240, MinHeight = 120,
    };
}
