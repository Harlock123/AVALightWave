using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// One of the view layout's panes: the engine's <see cref="SceneView"/> on the window's session, with its own view
/// type (and so its own cameras' zoom and pan), a small chooser of that type in its top-right corner, and an outline
/// when it's the active pane. A container, so a drawing surface (OpenGL, later) can go beneath the view: insert it at
/// <see cref="SurfaceIndex"/>.
/// </summary>
public sealed class ViewportPane : Panel
{
    private readonly Border _outline = new() { BorderBrush = Look.ButtonOn, BorderThickness = new Thickness(2), IsHitTestVisible = false, IsVisible = false };
    private bool _choosing;

    /// <summary>Where a surface drawn beneath the view goes among the pane's children (the view and its overlays are above it).</summary>
    public const int SurfaceIndex = 0;

    public int Index { get; }
    public SceneView View { get; }

    /// <summary>The pane's view type (Camera, Perspective, Top, Front, Side, Light).</summary>
    public ComboBox Chooser { get; }

    /// <summary>Whether the view has been framed since the pane was first shown at a size.</summary>
    internal bool Fitted { get; set; }

    /// <summary>The name the chooser gives the live ray-traced view (not a view type: the pane shows Camera beneath it).</summary>
    public const string RenderChoice = "Render";

    private readonly LayoutSession _session;

    public ViewportPane(LayoutSession session, int index)
    {
        _session = session;
        Index = index;
        Name = $"Pane{index}";
        ClipToBounds = true;
        View = new SceneView(session);
        Chooser = new ComboBox
        {
            ItemsSource = ViewportLayouts.ViewNames.Append(RenderChoice).ToList(), SelectedItem = View.Mode.ToString(), FontSize = 11, MinHeight = 22, Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4), Opacity = 0.85,
            Name = $"PaneView{index}",
        };
        ToolTip.SetTip(Chooser, "This view's type (1 to 6 change the active view's); Render ray-traces the camera's view as you work (F8)");
        Chooser.SelectionChanged += (_, _) =>
        {
            if (_choosing) return;
            if (Chooser.SelectedItem as string == RenderChoice) { ShowRender(true); return; }
            if (!Enum.TryParse<LayoutViewMode>(Chooser.SelectedItem as string, out var m)) return;
            ShowRender(false);
            View.Mode = m;
        };
        View.ModeChanged += () =>
        {
            if (Rendering && View.Mode != LayoutViewMode.Camera) ShowRender(false);
            ChooserFollows();
        };
        _render = new RenderOverlay();
        _render.IsVisible = false;
        Children.Add(View);
        Children.Add(_render);
        Children.Add(Chooser);
        Children.Add(_outline);
    }

    /// <summary>The OpenGL surface drawing the scene under the view, while the pane uses OpenGL.</summary>
    public GlSurface? Gl { get; private set; }

    /// <summary>Whether the pane draws its scene with OpenGL now (its surface has a context).</summary>
    public bool DrawsWithGl => Gl is { Ready: true } && !View.DrawsScene;

    /// <summary>
    /// Draws the scene with OpenGL under the view (the engine's GlSurface and GlRenderer; the view still draws its frame
    /// guides and label and takes the mouse), or back in software. <see cref="FallBackIfNoGl"/> goes back to software
    /// where OpenGL couldn't start.
    /// </summary>
    public void UseOpenGl(bool on)
    {
        if (on == (Gl != null)) return;
        if (on)
        {
            var gl = new GlSurface { Scene = () => View.CurrentView().Scene, CameraSource = () => View.Camera, IsHitTestVisible = false };
            View.Redrawn += gl.RequestNextFrameRendering;
            gl.Failed += _ => Dispatcher.UIThread.Post(() => UseOpenGl(false));
            Children.Insert(SurfaceIndex, gl);
            Gl = gl;
            View.DrawsScene = false;
        }
        else
        {
            View.Redrawn -= Gl!.RequestNextFrameRendering;
            Children.Remove(Gl);
            Gl = null;
            View.DrawsScene = true;
        }
        View.InvalidateVisual();
    }

    /// <summary>If the pane asked for OpenGL and it never started (no context: a headless or driverless machine), draws in software again. Returns whether it did.</summary>
    public bool FallBackIfNoGl()
    {
        if (Gl == null || Gl.Ready) return false;
        UseOpenGl(false);
        return true;
    }

    // ───────────────────────────── the live render ─────────────────────────────

    private readonly RenderOverlay _render;
    private ProgressiveRender? _progressive;
    private DispatcherTimer? _watch;
    private (Scene, int, double, int, int, CameraItem?, int, int) _rendered, _seen;

    /// <summary>Rays a pixel the live render refines to (denoised on the way).</summary>
    public int RenderSamples { get; set; } = 16;

    /// <summary>The longest side the live render draws, in pixels.</summary>
    public int RenderMaxSize { get; set; } = 1280;

    /// <summary>Whether the pane shows the live render (the camera's view ray-traced, refining while nothing changes).</summary>
    public bool Rendering { get; private set; }

    /// <summary>The live render's picture and counter, for tests.</summary>
    public Bitmap? RenderPicture => _render.Picture.Source as Bitmap;

    public string RenderCounter => _render.Counter.Text ?? "";

    /// <summary>Fires on the UI thread each time the live render shows a new pass.</summary>
    public event Action<ProgressiveFrame>? RenderShown;

    /// <summary>
    /// Shows the camera's view ray-traced over the pane (its view goes to Camera beneath, still taking the mouse), a
    /// quick rough pass first, refining to <see cref="RenderSamples"/> rays; it starts again when the scene, the frame,
    /// the camera or the pane's size changes, and stops when turned off or when the pane isn't shown.
    /// </summary>
    public void ShowRender(bool on)
    {
        if (on == Rendering) return;
        Rendering = on;
        if (on && View.Mode != LayoutViewMode.Camera) View.Mode = LayoutViewMode.Camera;
        _render.IsVisible = on;
        if (on) StartWatching();
        else StopRendering();
        ChooserFollows();
    }

    private void ChooserFollows()
    {
        _choosing = true;
        Chooser.SelectedItem = Rendering ? RenderChoice : View.Mode.ToString();
        _choosing = false;
    }

    private void StartWatching()
    {
        if (!Rendering || VisualRoot == null) return;
        _rendered = default;
        _seen = default;
        _watch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Check());
        _watch.Start();
        Check(force: true);
    }

    private void StopRendering()
    {
        _watch?.Stop();
        _progressive?.Stop();
    }

    private (Scene, int, double, int, int, CameraItem?, int, int) Key()
    {
        var s = _session.Scene;
        var cam = s.CurrentCamera;
        var (w, h) = RenderSize(cam);
        return (s, s.Version, _session.Frame, w, h, cam, cam?.Width ?? 0, cam?.Height ?? 0);
    }

    /// <summary>The live render's size: the pane's in pixels, at the camera's shape, no more than <see cref="RenderMaxSize"/>.</summary>
    private (int W, int H) RenderSize(CameraItem? cam)
    {
        double scaling = (VisualRoot as TopLevel)?.RenderScaling ?? 1;
        double pw = Math.Max(1, Bounds.Width * scaling), ph = Math.Max(1, Bounds.Height * scaling);
        double aspect = cam != null && cam.Height > 0 ? cam.Width * cam.PixelAspect / cam.Height : 4.0 / 3;
        double w = Math.Min(pw, ph * aspect);
        w = Math.Min(w, aspect >= 1 ? RenderMaxSize : RenderMaxSize * aspect);
        return (Math.Max(8, (int)Math.Round(w)), Math.Max(8, (int)Math.Round(w / aspect)));
    }

    /// <summary>One look at whether the live render must start again: the watch timer's tick (tests call it, as headless timers don't run).</summary>
    public void Tick() => Check();

    /// <summary>Starts again when what it shows has changed and then held still for a moment (so a drag doesn't start it every tick).</summary>
    private void Check(bool force = false)
    {
        if (!Rendering) return;
        var key = Key();
        bool steady = key.Equals(_seen);
        _seen = key;
        if (!force && (key.Equals(_rendered) || !steady)) return;
        _rendered = key;
        if (_progressive == null)
        {
            _progressive = new ProgressiveRender();
            _progressive.Updated += f => Dispatcher.UIThread.Post(() => Show(f));
        }
        _render.Counter.Text = "starting…";
        _progressive.Start(_session.Scene.Copy(), _session.Frame, key.Item4, key.Item5, RenderSamples);
    }

    private void Show(ProgressiveFrame f)
    {
        if (!Rendering || _progressive == null || f.Generation != _progressive.Generation) return;
        if (f.Image is { } img) _render.Picture.Source = ToBitmap(img);
        _render.Counter.Text = f.Counter;
        RenderShown?.Invoke(f);
    }

    private static WriteableBitmap ToBitmap(RenderImage img)
    {
        var bmp = new WriteableBitmap(new PixelSize(img.Width, img.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using var fb = bmp.Lock();
        int row = img.Width * 4;
        for (int y = 0; y < img.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(img.Rgba, y * row, fb.Address + y * fb.RowBytes, row);
        return bmp;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Rendering) Dispatcher.UIThread.Post(StartWatching, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopRendering();
    }

    /// <summary>The live render over a pane: the picture on black, letterboxed, with its counter; the mouse goes through to the view.</summary>
    private sealed class RenderOverlay : Panel
    {
        public Image Picture { get; } = new() { Stretch = Stretch.Uniform };
        public TextBlock Counter { get; } = new()
        {
            FontSize = 11, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4),
        };

        public RenderOverlay()
        {
            Background = Brushes.Black;
            IsHitTestVisible = false;
            RenderOptions.SetBitmapInterpolationMode(Picture, BitmapInterpolationMode.HighQuality);
            Children.Add(Picture);
            Children.Add(Counter);
        }
    }

    /// <summary>Outlines the pane (the active one, when there's more than one).</summary>
    internal void Outline(bool on) => _outline.IsVisible = on;

    internal bool Outlined => _outline.IsVisible;
}

/// <summary>
/// The window's middle: one, two (side by side or stacked) or four <see cref="ViewportPane"/>s with thin splitters
/// between, as LightWave's later layouts and AVA Modeler's quad view arrange them. The four panes are made once and
/// kept (each keeps its view type and framing while hidden); a layout shows the first one, two or four. The active
/// pane (the last clicked) takes the view keys; the window's edit and mouse modes go to every pane.
/// </summary>
public sealed class ViewportLayout : Grid
{
    private readonly ViewportPane[] _panes;
    private ViewportPane _active;

    public ViewportLayout(LayoutSession session)
    {
        _panes = Enumerable.Range(0, 4).Select(i => new ViewportPane(session, i)).ToArray();
        _active = _panes[0];
        foreach (var pane in _panes)
        {
            // The pane clicked becomes the active one before the view sees the click.
            pane.AddHandler(PointerPressedEvent, (_, _) => Activate(pane), RoutingStrategies.Tunnel, handledEventsToo: true);
            pane.View.ModeChanged += () => PaneViewChanged?.Invoke(pane);
        }
        Arrange(ViewportLayoutKind.Single, [LayoutViewMode.Perspective]);
    }

    /// <summary>Every pane, shown or not (four).</summary>
    public IReadOnlyList<ViewportPane> Panes => _panes;

    /// <summary>The panes the layout shows.</summary>
    public IReadOnlyList<ViewportPane> Shown => _panes.Take(ViewportLayouts.Panes(Kind)).ToList();

    public ViewportLayoutKind Kind { get; private set; }

    /// <summary>The active pane: the last clicked (the first after a layout without it).</summary>
    public ViewportPane Active => _active;

    public event Action? ActiveChanged;

    /// <summary>A pane's view type changed (by its chooser, the keys, the menu or code).</summary>
    public event Action<ViewportPane>? PaneViewChanged;

    public void Activate(ViewportPane pane)
    {
        if (pane == _active || !Shown.Contains(pane)) return;
        _active = pane;
        Outline();
        ActiveChanged?.Invoke();
    }

    private void Outline()
    {
        bool several = ViewportLayouts.Panes(Kind) > 1;
        foreach (var p in _panes) p.Outline(several && p == _active);
    }

    /// <summary>Shows a layout, its panes showing the views given (pane by pane).</summary>
    public void Arrange(ViewportLayoutKind kind, IReadOnlyList<LayoutViewMode> views)
    {
        Kind = kind;
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();
        int count = ViewportLayouts.Panes(kind);
        for (int i = 0; i < count && i < views.Count; i++) _panes[i].View.Mode = views[i];
        bool across = kind is ViewportLayoutKind.SideBySide or ViewportLayoutKind.Quad;
        bool down = kind is ViewportLayoutKind.Stacked or ViewportLayoutKind.Quad;
        if (across) ColumnDefinitions = new ColumnDefinitions("*,4,*");
        if (down) RowDefinitions = new RowDefinitions("*,4,*");
        for (int i = 0; i < count; i++)
        {
            var pane = _panes[i];
            int col = kind == ViewportLayoutKind.Stacked ? 0 : (kind == ViewportLayoutKind.Quad ? i % 2 : i) * 2;
            int row = kind == ViewportLayoutKind.SideBySide ? 0 : (kind == ViewportLayoutKind.Quad ? i / 2 : i) * 2;
            SetColumn(pane, across ? col : 0);
            SetRow(pane, down ? row : 0);
            Children.Add(pane);
        }
        if (across) Children.Add(Splitter(GridResizeDirection.Columns, column: 1, rowSpan: down ? 3 : 1));
        if (down) Children.Add(Splitter(GridResizeDirection.Rows, row: 1, columnSpan: across ? 3 : 1));
        if (!Shown.Contains(_active))
        {
            _active = _panes[0];
            ActiveChanged?.Invoke();
        }
        Outline();
        // Frame each pane the first time it's shown at a size (after this layout pass).
        Dispatcher.UIThread.Post(FitNew, DispatcherPriority.Background);
    }

    private static GridSplitter Splitter(GridResizeDirection direction, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1)
    {
        var s = new GridSplitter { ResizeDirection = direction, Background = Look.PanelDark };
        SetRow(s, row);
        SetColumn(s, column);
        SetRowSpan(s, rowSpan);
        SetColumnSpan(s, columnSpan);
        return s;
    }

    private void FitNew()
    {
        foreach (var p in Shown)
            if (!p.Fitted && p.Bounds.Width > 1 && p.Bounds.Height > 1)
            {
                p.View.Fit();
                p.Fitted = true;
            }
    }

    /// <summary>Frames the scene in every pane shown (A); the hidden ones are framed when next shown.</summary>
    public void FitAll()
    {
        foreach (var p in _panes) p.Fitted = false;
        FitNew();
    }

    /// <summary>Does something to every pane's view (shown or not): the edit and mouse modes, axes, shading.</summary>
    public void ForEachView(Action<SceneView> act)
    {
        foreach (var p in _panes) act(p.View);
    }

    public void InvalidateViews()
    {
        foreach (var p in Shown) p.View.InvalidateVisual();
    }
}
