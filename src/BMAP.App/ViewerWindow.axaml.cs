using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using BMAP.App.ViewModels;

namespace BMAP.App;

/// <summary>"View in 3D": the build in 3D, as a 2D placement plan and as a height map (MapartCraft's "View online").</summary>
public partial class ViewerWindow : Window
{
    private readonly MainViewModel _vm;
    private IDisposable? _wheelSettle;

    public ViewerWindow() : this(null!) { }

    public ViewerWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        if (vm is null) return;
        Opened += (_, _) => vm.ViewerOpen = true;
        Closed += (_, _) => vm.ViewerOpen = false;
        KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.Z) vm.UndoEditCommand.Execute(null);
        };
        if (this.FindControl<Image>("BuildImage") is { } img)
        {
            img.PointerMoved += (_, e) => Forward(img, e, false, e.GetCurrentPoint(img).Properties.IsLeftButtonPressed, false);
            img.PointerPressed += (_, e) => { e.Pointer.Capture(img); Forward(img, e, true, true, false); };
            img.PointerReleased += (_, e) => { Forward(img, e, false, false, true); e.Pointer.Capture(null); vm.EndPaintStroke(); };
        }
        Setup3DView();
    }

    private void Forward(Image img, PointerEventArgs e, bool click, bool pressed, bool released)
    {
        var p = e.GetPosition(img);
        if (img.Bounds.Width <= 0 || img.Bounds.Height <= 0) return;
        _vm.OnPreviewPointer(p.X / img.Bounds.Width, p.Y / img.Bounds.Height, click, pressed, released);
    }

    /// <summary>Mouse control of the 3D view: left drag rotates, right/middle drag (or Shift) pans, wheel zooms, double-click fits.</summary>
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
        panel.PointerCaptureLost += (_, _) =>
        {
            if (last is null) return;
            last = null;
            _vm.Settle3D();
        };
        panel.PointerExited += (_, _) => { if (last is null) _vm.ClearHover3D(); };
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
}
