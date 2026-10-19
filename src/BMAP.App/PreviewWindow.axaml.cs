using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BMAP.App.ViewModels;

namespace BMAP.App;

public partial class PreviewWindow : Window
{
    private readonly MainViewModel _vm;
    private double _zoom = 1, _offsetX, _offsetY;
    private bool _fit = true;
    private Point? _drag;

    public PreviewWindow() : this(null!) { }

    public PreviewWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        if (vm is null) return;

        var surface = this.FindControl<Border>("Surface")!;
        var picture = this.FindControl<Image>("Picture")!;
        this.FindControl<Button>("FitButton")!.Click += (_, _) => { _fit = true; Apply(); };
        this.FindControl<Button>("InButton")!.Click += (_, _) => ZoomAt(2, Centre());
        this.FindControl<Button>("OutButton")!.Click += (_, _) => ZoomAt(0.5, Centre());
        this.FindControl<Button>("OneButton")!.Click += (_, _) => ZoomAt(1 / _zoom, Centre());

        surface.SizeChanged += (_, _) => Apply();
        vm.PropertyChanged += OnVmChanged;
        Closed += (_, _) => vm.PropertyChanged -= OnVmChanged;

        // Touch up: left drag paints map pixels, right click picks the block under the cursor; panning moves to the
        // middle button or Space + drag. Without touch up, any drag pans.
        (int X, int Z)? stroke = null;
        bool space = false;
        surface.PointerWheelChanged += (_, e) => ZoomAt(e.Delta.Y > 0 ? 1.25 : 0.8, e.GetPosition(surface));
        surface.PointerPressed += (_, e) =>
        {
            var p = e.GetPosition(surface);
            var props = e.GetCurrentPoint(surface).Properties;
            if (vm.TouchUpMode && !space && !props.IsMiddleButtonPressed)
            {
                if (ToBlock(p) is not { } b) return;
                if (props.IsRightButtonPressed) { vm.TouchUpPick(b.X, b.Z); ShowPixel(p); return; }
                stroke = b;
                vm.TouchUpPaint(b.X, b.Z, true, false);
                e.Pointer.Capture(surface);
                return;
            }
            if (e.ClickCount == 2) { _fit = !_fit; if (!_fit) ZoomAt(4 / _zoom, p); else Apply(); return; }
            _drag = p;
            e.Pointer.Capture(surface);
        };
        surface.PointerReleased += (_, e) =>
        {
            if (stroke is { } s) { vm.TouchUpPaint(s.X, s.Z, false, true); stroke = null; ShowPixel(e.GetPosition(surface)); }
            _drag = null;
            e.Pointer.Capture(null);
        };
        surface.PointerMoved += (_, e) =>
        {
            var p = e.GetPosition(surface);
            if (stroke is { } last && ToBlock(p) is { } b && b != last)
            {
                // Fill the gap to the last pixel so fast strokes stay continuous.
                int n = Math.Max(Math.Abs(b.X - last.X), Math.Abs(b.Z - last.Z));
                for (int k = 1; k <= n; k++)
                    vm.TouchUpPaint(last.X + (int)Math.Round((b.X - last.X) * (double)k / n), last.Z + (int)Math.Round((b.Z - last.Z) * (double)k / n), false, false);
                stroke = b;
            }
            else if (_drag is { } d)
            {
                _fit = false;
                _offsetX += p.X - d.X;
                _offsetY += p.Y - d.Y;
                _drag = p;
                Apply();
            }
            ShowPixel(p);
        };
        surface.PointerExited += (_, _) => this.FindControl<TextBlock>("PixelText")!.Text = "";
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) vm.UndoEditCommand.Execute(null);
            else if (e.Key == Key.Space) { space = true; surface.Cursor = new Cursor(StandardCursorType.SizeAll); e.Handled = true; }
            else if (e.Key is Key.Add or Key.OemPlus) ZoomAt(2, Centre());
            else if (e.Key is Key.Subtract or Key.OemMinus) ZoomAt(0.5, Centre());
            else if (e.Key is Key.D0 or Key.NumPad0) { _fit = true; Apply(); }
            else if (e.Key == Key.Escape) Close();
        };
        KeyUp += (_, e) => { if (e.Key == Key.Space) { space = false; UpdateCursor(); } };
        void UpdateCursor() => surface.Cursor = new Cursor(vm.TouchUpMode ? StandardCursorType.Cross : StandardCursorType.SizeAll);
        void OnTouchUp(object? s, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.TouchUpMode)) UpdateCursor(); }
        vm.PropertyChanged += OnTouchUp;
        Closed += (_, _) => vm.PropertyChanged -= OnTouchUp;
        UpdateCursor();

        // Map pixel (picture x, y) under a point of the surface, or null outside the map.
        (int X, int Z)? ToBlock(Point p)
        {
            if (picture.Source is not { } src || _zoom <= 0) return null;
            int x = (int)Math.Floor((p.X - _offsetX) / _zoom), z = (int)Math.Floor((p.Y - _offsetY) / _zoom);
            int blocks = vm.MapsX * 128, width = (int)src.Size.Width;
            if (x < 0 || z < 0 || x >= width || z >= src.Size.Height || width < blocks || width % blocks != 0) return null;
            int scale = width / blocks;
            return (x / scale, z / scale);
        }
        Opened += (_, _) => Apply();

        Point Centre() => new(surface.Bounds.Width / 2, surface.Bounds.Height / 2);

        void ZoomAt(double factor, Point at)
        {
            if (picture.Source is null) return;
            double z = Math.Clamp(_zoom * factor, 0.1, 64);
            factor = z / _zoom;
            // Keep the point under the cursor in place.
            _offsetX = at.X - (at.X - _offsetX) * factor;
            _offsetY = at.Y - (at.Y - _offsetY) * factor;
            _zoom = z;
            _fit = false;
            Apply();
        }

        void Apply()
        {
            var text = this.FindControl<TextBlock>("ZoomText")!;
            if (picture.Source is not { } src) { text.Text = ""; return; }
            double w = src.Size.Width, h = src.Size.Height;
            double sw = surface.Bounds.Width, sh = surface.Bounds.Height;
            if (sw <= 0 || sh <= 0) return;
            if (_fit)
            {
                _zoom = Math.Min(sw / w, sh / h);
                _offsetX = (sw - w * _zoom) / 2;
                _offsetY = (sh - h * _zoom) / 2;
            }
            else
            {
                // Do not let the picture be dragged completely out of view.
                _offsetX = Math.Clamp(_offsetX, Math.Min(0, sw - w * _zoom) - sw / 2, Math.Max(0, sw - w * _zoom) + sw / 2);
                _offsetY = Math.Clamp(_offsetY, Math.Min(0, sh - h * _zoom) - sh / 2, Math.Max(0, sh - h * _zoom) + sh / 2);
            }
            picture.Width = w * _zoom;
            picture.Height = h * _zoom;
            Canvas.SetLeft(picture, _offsetX);
            Canvas.SetTop(picture, _offsetY);
            text.Text = $"{_zoom * 100:0}%";
        }

        void ShowPixel(Point p)
        {
            var label = this.FindControl<TextBlock>("PixelText")!;
            if (picture.Source is not { } src) { label.Text = ""; return; }
            int x = (int)Math.Floor((p.X - _offsetX) / _zoom), z = (int)Math.Floor((p.Y - _offsetY) / _zoom);
            if (x < 0 || z < 0 || x >= src.Size.Width || z >= src.Size.Height) { label.Text = ""; return; }
            // Map previews are drawn a whole number of pixels per block, 128 blocks per map.
            int blocks = _vm.MapsX * 128, width = (int)src.Size.Width;
            if (width < blocks || width % blocks != 0) { label.Text = $"x {x}, y {z}"; return; }
            int bx = x / (width / blocks), bz = z / (width / blocks);
            string block = _vm.SelectedPreviewMode.Value == PreviewMode.BlockMap ? "   " + _vm.DescribePixel(bx, bz) : "";
            label.Text = $"x {bx}, z {bz}   {_vm.L.Format("MapN", BMAP.BlockEngine.MaterialCalculator.MapNumberOf(bx, bz, _vm.MapsX))}{block}";
        }

        void OnVmChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Preview)) Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
        }
    }
}
