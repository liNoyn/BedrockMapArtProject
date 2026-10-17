using Avalonia.Media.Imaging;
using BMAP.BlockEngine;
using BMAP.Preview;
using BMAP.Shading;
using BMAP.Textures;

namespace BMAP.App.ViewModels;

/// <summary>Interactive 3D build view: orbit / pan / zoom with the mouse, rendered on demand in the background.</summary>
public sealed partial class MainViewModel
{
    private SceneMesh? _mesh;
    private (BlockLayout? Layout, LayoutRegion Region, bool Supports, int Layer, ShadingReport? Report) _meshKey;
    private Camera3D _camera = new();
    private bool _cameraFitted;
    private RenderResult3D? _last3D;
    private int _vpW = 900, _vpH = 600;
    private bool _rendering3D, _dirty3D, _dirtyFast, _dragging3D;
    private int _hover3D = -1;

    private Bitmap? _view3D;
    public Bitmap? View3D { get => _view3D; set => Set(ref _view3D, value); }

    public IReadOnlyList<LocOption<ViewColorMode>> ColorModes3D { get; private set; } = [];
    private LocOption<ViewColorMode>? _colorMode3D;
    public LocOption<ViewColorMode>? ColorMode3D { get => _colorMode3D; set { if (value != null && Set(ref _colorMode3D, value)) Request3D(); } }
    private bool _perspective3D;
    public bool Perspective3D { get => _perspective3D; set { if (Set(ref _perspective3D, value)) { _camera = _camera with { Perspective = value }; Request3D(); } } }
    private bool _outlines3D = true;
    public bool Outlines3D { get => _outlines3D; set { if (Set(ref _outlines3D, value)) Request3D(); } }
    private bool _lighting3D = true;
    public bool Lighting3D { get => _lighting3D; set { if (Set(ref _lighting3D, value)) Request3D(); } }
    private bool _grid3D;
    public bool Grid3D { get => _grid3D; set { if (Set(ref _grid3D, value)) Request3D(); } }

    // Height slice: show the build only up to a layer (build it layer by layer).
    private int _layer3D = 400;
    public int Layer3D { get => _layer3D; set { if (Set(ref _layer3D, Math.Max(0, value))) { Raise(nameof(Layer3DText)); Request3D(); } } }
    public int MaxLayer3D => _layout?.MaxHeight ?? 0;
    public string Layer3DText => Layer3D >= MaxLayer3D ? $"Y+0 … Y+{MaxLayer3D}" : $"Y+0 … Y+{Layer3D} / {MaxLayer3D}";

    private string _view3DInfo = "";
    public string View3DInfo { get => _view3DInfo; set => Set(ref _view3DInfo, value); }

    public RelayCommand Reset3DCommand { get; private set; } = null!;
    public RelayCommand TopView3DCommand { get; private set; } = null!;
    public RelayCommand Turn3DCommand { get; private set; } = null!;
    public RelayCommand Tallest3DCommand { get; private set; } = null!;

    private void Init3D()
    {
        ColorModes3D =
        [
            new(ViewColorMode.Texture, "Color3DTexture", L), new(ViewColorMode.MapColor, "Color3DMap", L),
            new(ViewColorMode.Shade, "Color3DShade", L), new(ViewColorMode.Height, "Color3DHeight", L),
        ];
        _colorMode3D = ColorModes3D[0];
        Reset3DCommand = new RelayCommand(() => { FitCamera(); Request3D(); });
        TopView3DCommand = new RelayCommand(() => { _camera = _camera with { Yaw = 0, Pitch = 90 }; Request3D(); });
        Turn3DCommand = new RelayCommand(() => { _camera = _camera with { Yaw = _camera.Yaw + 90 }; Request3D(); });
        Tallest3DCommand = new RelayCommand(FocusTallest);
    }

    /// <summary>Centres the view on the tallest column (where the height limit matters most).</summary>
    private void FocusTallest()
    {
        if (_report is not { } r || _mesh is null) return;
        // Pan is in camera space: rotate the offset of the column from the scene centre like the renderer does.
        double yaw = _camera.Yaw * Math.PI / 180, pitch = _camera.Pitch * Math.PI / 180;
        double px = r.TallestX + 0.5 - _mesh.CenterX, pz = r.TallestZ + 0.5 - _mesh.CenterZ, py = r.MaxY + 1 - _mesh.CenterY;
        double x1 = px * Math.Cos(yaw) - pz * Math.Sin(yaw);
        double z1 = px * Math.Sin(yaw) + pz * Math.Cos(yaw);
        _camera = _camera with { PanX = x1, PanY = py * Math.Cos(pitch) - z1 * Math.Sin(pitch), Zoom = Math.Max(_camera.Zoom, 12) };
        Request3D();
    }

    /// <summary>The 3D panel's size in physical pixels.</summary>
    public void SetViewport3D(int width, int height)
    {
        width = Math.Clamp(width, 64, 4096);
        height = Math.Clamp(height, 64, 4096);
        if (width == _vpW && height == _vpH) return;
        _vpW = width;
        _vpH = height;
        if (!_cameraFitted) FitCamera();
        Request3D();
    }

    private void FitCamera()
    {
        if (_mesh is null) return;
        _camera = _mesh.FitCamera(_vpW, _vpH) with { Perspective = Perspective3D };
        _cameraFitted = true;
    }

    /// <summary>Left drag: rotate around the scene.</summary>
    public void Orbit3D(double dx, double dy)
    {
        _dragging3D = true;
        _camera = (_camera with { Yaw = _camera.Yaw - dx * 0.4, Pitch = _camera.Pitch + dy * 0.3 }).Clamp();
        Request3D(fast: true);
    }

    /// <summary>Right / middle drag: move the view.</summary>
    public void Pan3D(double dx, double dy)
    {
        _dragging3D = true;
        _camera = _camera with { PanX = _camera.PanX - dx / _camera.Zoom, PanY = _camera.PanY + dy / _camera.Zoom };
        Request3D(fast: true);
    }

    /// <summary>Mouse wheel: zoom towards the pointer (fx, fy = position in the view, 0..1).</summary>
    public void Zoom3D(double wheel, double fx, double fy)
    {
        double factor = Math.Pow(1.18, wheel);
        double oldZoom = _camera.Zoom;
        double newZoom = Math.Clamp(oldZoom * factor, 0.25, 64);
        // Keep the point under the cursor in place.
        double ox = (fx - 0.5) * _vpW, oy = (fy - 0.5) * _vpH;
        double panX = _camera.PanX + ox / oldZoom - ox / newZoom;
        double panY = _camera.PanY - oy / oldZoom + oy / newZoom;
        _camera = _camera with { Zoom = newZoom, PanX = panX, PanY = panY };
        Request3D(fast: true);
    }

    /// <summary>Called when a drag ends: render once more at full resolution.</summary>
    public void Settle3D()
    {
        _dragging3D = false;
        Request3D();
    }

    public void Hover3D(double fx, double fy)
    {
        if (_last3D is not { } r || _mesh is null) return;
        int id = r.BlockAt((int)(fx * r.Width), (int)(fy * r.Height));
        if (id < 0 || id >= _mesh.Blocks.Count)
        {
            View3DInfo = "";
            if (_hover3D != -1) { _hover3D = -1; Request3D(); }
            return;
        }
        var b = _mesh.Blocks[id];
        string world = Origin is { } o ? $"  {(L.English ? "world" : "ワールド")} X {o.X + b.X}, Y {o.Y + b.Y}, Z {o.Z + b.Z}" : "";
        string shade = "";
        if (_report is { } rep && b.Z >= 1 && b.Z - 1 < rep.Map.Height && rep.Map.TopY[rep.Map.Index(b.X, b.Z - 1)] == b.Y)
        {
            var s = rep.Map.Shades[rep.Map.Index(b.X, b.Z - 1)];
            shade = "  " + L[s switch { Palette.Shade.Dark => "ShadeDark", Palette.Shade.Bright => "ShadeBright", _ => "ShadeNormal" }];
        }
        View3DInfo = $"{b.Block.LocalName(L.English)}  ({b.Block.Name})  x {b.X}, y {b.Y}, z {b.Z}{world}{shade}" +
                     (b.IsSupport && b.Z != 0 ? $"  [{L["Support"]}]" : "");
        if (id != _hover3D && !_dragging3D)
        {
            _hover3D = id;
            Request3D();
        }
    }

    /// <summary>Renders the 3D view in the background. Requests during a render are merged into one follow-up render.</summary>
    private async void Request3D(bool fast = false)
    {
        if (SelectedPreviewMode.Value != PreviewMode.Build3D) return;
        if (_rendering3D) { _dirty3D = true; _dirtyFast = fast; return; }
        if (_layout is not { } layout || Profile is not { } profile) { View3D = null; return; }

        var region = RegionFor(layout);
        bool supports = ShowSupports;
        int layer = Layer3D >= layout.MaxHeight ? int.MaxValue : Layer3D;
        var report = _report;
        _rendering3D = true;
        try
        {
            if (_mesh is null || !ReferenceEquals(_meshKey.Layout, layout) || _meshKey.Region != region || _meshKey.Supports != supports
                || _meshKey.Layer != layer || !ReferenceEquals(_meshKey.Report, report))
            {
                bool regionChanged = _meshKey.Region != region || _mesh is null;
                if (!ReferenceEquals(_meshKey.Layout, layout)) { Raise(nameof(MaxLayer3D)); Raise(nameof(Layer3DText)); }
                _mesh = await Task.Run(() => SceneMesh.Build(layout, region, supports, layer == int.MaxValue ? null : layer, report));
                _meshKey = (layout, region, supports, layer, report);
                _hover3D = -1;
                if (regionChanged || !_cameraFitted) FitCamera();
            }

            var mesh = _mesh;
            var cam = _camera;
            var options = new View3DOptions
            {
                Mode = ColorMode3D?.Value ?? ViewColorMode.Texture,
                Outlines = Outlines3D && !fast,
                Lighting = Lighting3D,
                Grid = Grid3D,
                Highlight = fast ? -1 : _hover3D,
            };
            // While dragging, render at half resolution so the view follows the mouse.
            int w = fast ? Math.Max(64, _vpW / 2) : _vpW, h = fast ? Math.Max(64, _vpH / 2) : _vpH;
            var camForSize = fast ? cam with { Zoom = cam.Zoom / 2, PanX = cam.PanX, PanY = cam.PanY } : cam;
            BlockTextureProvider? tex = Textures.IsAvailable ? Textures : null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => Viewer3D.Render(mesh, profile, camForSize, w, h, tex is null ? null : tex.GetIcon, options));
            _last3D = result;
            View3D = BitmapHelper.ToBitmap(result.Image);
            PreviewInfo = L.Format("Info3D", mesh.FaceCount, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) { Log(ex.Message); }
        finally { _rendering3D = false; }

        if (_dirty3D)
        {
            _dirty3D = false;
            Request3D(_dirtyFast);
        }
    }
}
