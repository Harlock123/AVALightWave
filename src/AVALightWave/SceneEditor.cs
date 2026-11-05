using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Modeler.UI;

namespace AVALightWave;

/// <summary>
/// The Scene Editor (View › Scene Editor, or its tab under the view): every item in its hierarchy, each with an eye
/// (shown or hidden, in the views and renders) and a lock (kept from the mouse), and a track of its keys across the
/// scene's frames. Click the eye or the lock to change it (one undo step each), the name to choose the item.
/// </summary>
public sealed class SceneEditor : DockPanel
{
    public SceneEditorView View { get; }

    public SceneEditor(SceneDocument document)
    {
        View = new SceneEditorView(document);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(6, 2) };
        bar.Children.Add(new TextBlock { Text = "Scene Editor", FontWeight = FontWeight.Bold, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
        bar.Children.Add(new TextBlock { Text = "the eye shows or hides an item, the lock keeps the mouse off it; click a name to choose it", Foreground = Look.Text, Opacity = 0.7, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(new ScrollViewer { Content = View, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
    }
}

/// <summary>The Scene Editor's rows, drawn; turns clicks into showing, hiding, locking and choosing items.</summary>
public sealed class SceneEditorView : Control
{
    public const double HeaderHeight = 18, RowHeight = 20, NameWidth = 230, Inset = 10, Indent = 12;
    private static readonly Typeface Face = new(FontFamily.Default);
    private readonly SceneDocument _document;
    private IReadOnlyList<OutlineRow> _rows = [];

    public SceneEditorView(SceneDocument document)
    {
        _document = document;
        ClipToBounds = true;
        document.Session.Changed += Refresh;
        Refresh();
    }

    /// <summary>The rows as shown.</summary>
    public IReadOnlyList<OutlineRow> Rows => _rows;

    public void Refresh()
    {
        _rows = SceneOutline.Rows(_document.Scene);
        Height = HeaderHeight + Math.Max(1, _rows.Count) * RowHeight + 4;
        InvalidateVisual();
    }

    public static int RowAt(double y) => y < HeaderHeight ? -1 : (int)((y - HeaderHeight) / RowHeight);

    private static double Top(int row) => HeaderHeight + row * RowHeight;

    /// <summary>The middle of a row's eye, lock and name, in the view's coordinates (for tests).</summary>
    public static Point EyeOf(int row) => new(12, Top(row) + RowHeight / 2);
    public static Point LockOf(int row) => new(32, Top(row) + RowHeight / 2);
    public Point NameOf(int row) => new(50 + _rows[row].Depth * Indent + 10, Top(row) + RowHeight / 2);

    private (double First, double Last) Frames()
    {
        var s = _document.Scene;
        return (s.FirstFrame, Math.Max(s.LastFrame, s.FirstFrame + 1));
    }

    /// <summary>Where a frame is across the key track.</summary>
    public double XOf(double frame)
    {
        var (first, last) = Frames();
        double across = Math.Max(1, Bounds.Width - NameWidth - 2 * Inset);
        return NameWidth + Inset + (frame - first) / (last - first) * across;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var p = e.GetPosition(this);
        int row = RowAt(p.Y);
        if (row < 0 || row >= _rows.Count) return;
        var item = _rows[row].Item;
        if (p.X < 22) _document.SetVisible(item, !item.Visible);
        else if (p.X < 42) _document.SetLocked(item, !item.Locked);
        else _document.Session.Select(item);
        e.Handled = true;
        Refresh();
    }

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width;
        dc.FillRectangle(new SolidColorBrush(Color.FromRgb(50, 50, 54)), new Rect(Bounds.Size));
        var (first, last) = Frames();
        var dim = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        // The frame ruler: about ten numbers across.
        double step = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, (last - first) / 8)))));
        if ((last - first) / step > 16) step *= 5;
        for (double f = Math.Ceiling(first / step) * step; f <= last; f += step)
        {
            double x = XOf(f);
            dc.DrawLine(new Pen(dim, 1), new Point(x, HeaderHeight - 4), new Point(x, HeaderHeight));
            dc.DrawText(Text(f.ToString("0", CultureInfo.InvariantCulture), 10, dim), new Point(x + 2, 2));
        }
        var session = _document.Session;
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            double top = Top(i);
            if (r.Item == session.Selected) dc.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 214, 74)), new Rect(0, top, w, RowHeight));
            else if (i % 2 == 1) dc.FillRectangle(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), new Rect(0, top, w, RowHeight));
            double cy = top + RowHeight / 2;
            // The eye: a filled dot when shown, a ring when hidden.
            var eye = new Pen(Brushes.White, 1.4);
            dc.DrawEllipse(r.Visible ? Brushes.White : null, eye, new Point(12, cy), 5, 3.5);
            if (r.Visible) dc.DrawEllipse(Brushes.Black, null, new Point(12, cy), 1.8, 1.8);
            // The lock: a closed padlock when locked, an open shackle when not.
            var lockBrush = r.Locked ? (IBrush)new SolidColorBrush(Color.FromRgb(255, 214, 74)) : dim;
            dc.FillRectangle(lockBrush, new Rect(27, cy - 1, 10, 7));
            var shackle = new Pen(lockBrush, 1.4);
            dc.DrawLine(shackle, new Point(29, cy - 1), new Point(29, cy - 5));
            dc.DrawLine(shackle, new Point(29, cy - 5), new Point(35, cy - 5));
            if (r.Locked) dc.DrawLine(shackle, new Point(35, cy - 5), new Point(35, cy - 1));
            var nameBrush = r.Visible ? Look.Text : dim;
            dc.DrawText(Text($"{r.Kind}: {r.Name}", 11, nameBrush), new Point(46 + r.Depth * Indent, top + 3));
            // The key track.
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1), new Point(XOf(first), cy), new Point(XOf(last), cy));
            foreach (var f in r.Keys)
            {
                double x = XOf(f);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    g.BeginFigure(new Point(x, cy - 4), true);
                    g.LineTo(new Point(x + 4, cy));
                    g.LineTo(new Point(x, cy + 4));
                    g.LineTo(new Point(x - 4, cy));
                    g.EndFigure(true);
                }
                dc.DrawGeometry(f == session.Frame ? Brushes.White : Brushes.Gold, null, geo);
            }
        }
        double now = XOf(session.Frame);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 120, 200, 255)), 1), new Point(now, 0), new Point(now, Bounds.Height));
    }

    private static FormattedText Text(string s, double size, IBrush brush) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush);
}
