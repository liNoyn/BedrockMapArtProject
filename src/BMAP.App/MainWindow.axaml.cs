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
        if (this.FindControl<Image>("BuildImage") is { } img)
        {
            img.PointerMoved += (_, e) => ForwardPointer(img, e, false, e.GetCurrentPoint(img).Properties.IsLeftButtonPressed, false);
            img.PointerPressed += (_, e) => { e.Pointer.Capture(img); ForwardPointer(img, e, true, true, false); };
            img.PointerReleased += (_, e) => { ForwardPointer(img, e, false, false, true); e.Pointer.Capture(null); _vm.EndPaintStroke(); };
        }
        SetupCropDrag();
        Setup3DView();
    }

    /// <summary>Mouse control of the 3D view: left drag rotates, right/middle drag pans, wheel zooms, double-click fits.</summary>
    private void Setup3DView()
    {
        if (this.FindControl<Border>("View3DPanel") is not { } panel) return;
        Point? last = null;
        bool panning = false;
        void UpdateSize()
        {
            double scale = RenderScaling;
            _vm.SetViewport3D((int)(panel.Bounds.Width * scale), (int)(panel.Bounds.Height * scale));
        }
        panel.SizeChanged += (_, _) => UpdateSize();
        panel.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && panel.IsVisible) UpdateSize(); };
        panel.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(panel).Properties;
            if (e.ClickCount == 2 && props.IsLeftButtonPressed) { _vm.Reset3DCommand.Execute(null); return; }
            last = e.GetPosition(panel);
            panning = props.IsRightButtonPressed || props.IsMiddleButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            e.Pointer.Capture(panel);
        };
        panel.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(panel);
            if (last is { } l)
            {
                double s = RenderScaling;
                if (panning) _vm.Pan3D((p.X - l.X) * s, (p.Y - l.Y) * s);
                else _vm.Orbit3D(p.X - l.X, p.Y - l.Y);
                last = p;
            }
            else if (panel.Bounds.Width > 0) _vm.Hover3D(p.X / panel.Bounds.Width, p.Y / panel.Bounds.Height);
        };
        panel.PointerReleased += (_, e) =>
        {
            if (last is null) return;
            last = null;
            e.Pointer.Capture(null);
            _vm.Settle3D();
        };
        panel.PointerWheelChanged += (_, e) =>
        {
            var p = e.GetPosition(panel);
            if (panel.Bounds.Width <= 0) return;
            _vm.Zoom3D(e.Delta.Y, p.X / panel.Bounds.Width, p.Y / panel.Bounds.Height);
            _wheelSettle?.Dispose();
            _wheelSettle = Avalonia.Threading.DispatcherTimer.RunOnce(() => _vm.Settle3D(), TimeSpan.FromMilliseconds(200));
            e.Handled = true;
        };
    }

    private IDisposable? _wheelSettle;

    private void ForwardPointer(Image img, PointerEventArgs e, bool click, bool pressed, bool released)
    {
        var p = e.GetPosition(img);
        if (img.Bounds.Width <= 0 || img.Bounds.Height <= 0) return;
        _vm.OnPreviewPointer(p.X / img.Bounds.Width, p.Y / img.Bounds.Height, click, pressed, released);
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
        Title = title, Width = 620, SizeToContent = SizeToContent.Height, MaxHeight = 720,
        WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false,
    };
}
