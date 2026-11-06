using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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

    public ViewportPane(LayoutSession session, int index)
    {
        Index = index;
        Name = $"Pane{index}";
        ClipToBounds = true;
        View = new SceneView(session);
        Chooser = new ComboBox
        {
            ItemsSource = ViewportLayouts.ViewNames, SelectedItem = View.Mode.ToString(), FontSize = 11, MinHeight = 22, Padding = new Thickness(6, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4), Opacity = 0.85,
            Name = $"PaneView{index}",
        };
        ToolTip.SetTip(Chooser, "This view's type (1 to 6 change the active view's)");
        Chooser.SelectionChanged += (_, _) =>
        {
            if (_choosing || !Enum.TryParse<LayoutViewMode>(Chooser.SelectedItem as string, out var m)) return;
            View.Mode = m;
        };
        View.ModeChanged += () =>
        {
            _choosing = true;
            Chooser.SelectedItem = View.Mode.ToString();
            _choosing = false;
        };
        Children.Add(View);
        Children.Add(Chooser);
        Children.Add(_outline);
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
