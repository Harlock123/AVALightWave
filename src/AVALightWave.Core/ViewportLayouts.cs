namespace AVALightWave;

/// <summary>How the window's middle is split into views (View › Layout): one, two side by side, two stacked, or four.</summary>
public enum ViewportLayoutKind { Single, SideBySide, Stacked, Quad }

/// <summary>
/// The view layouts' panes and the view each pane starts with, and what <see cref="LightWaveOptions"/> remembers of
/// them. Views are named as the engine's Layout view modes are (Camera, Perspective, Top, Front, Side, Light). No UI.
/// </summary>
public static class ViewportLayouts
{
    /// <summary>The view names a pane can show, in the order the keys 1 to 6 choose them.</summary>
    public static readonly IReadOnlyList<string> ViewNames = ["Camera", "Perspective", "Top", "Front", "Side", "Light"];

    /// <summary>How many panes a layout has.</summary>
    public static int Panes(ViewportLayoutKind kind) => kind switch
    {
        ViewportLayoutKind.Single => 1,
        ViewportLayoutKind.SideBySide or ViewportLayoutKind.Stacked => 2,
        _ => 4,
    };

    /// <summary>
    /// The views a layout starts with, pane by pane (left to right, top to bottom): Perspective alone; Perspective and
    /// Camera side by side; Top over Camera; and the quad as LightWave and AVA Modeler arrange it, Top and Perspective
    /// over Front and, for a Layout, Camera in the fourth corner.
    /// </summary>
    public static IReadOnlyList<string> Defaults(ViewportLayoutKind kind) => kind switch
    {
        ViewportLayoutKind.Single => ["Perspective"],
        ViewportLayoutKind.SideBySide => ["Perspective", "Camera"],
        ViewportLayoutKind.Stacked => ["Top", "Camera"],
        _ => ["Top", "Perspective", "Front", "Camera"],
    };

    /// <summary>The layout the options remember (Single if they name none or one there isn't).</summary>
    public static ViewportLayoutKind LayoutOf(LightWaveOptions options) =>
        Enum.TryParse<ViewportLayoutKind>(options.ViewLayout, out var k) && Enum.IsDefined(k) ? k : ViewportLayoutKind.Single;

    /// <summary>
    /// The views a layout's panes show, as remembered (the single view is <see cref="LightWaveOptions.ViewMode"/>), each
    /// unknown or missing one taken from <see cref="Defaults"/>.
    /// </summary>
    public static IReadOnlyList<string> ViewsOf(LightWaveOptions options, ViewportLayoutKind kind)
    {
        var defaults = Defaults(kind);
        IReadOnlyList<string> saved = kind == ViewportLayoutKind.Single ? [options.ViewMode]
            : options.LayoutViews.TryGetValue(kind.ToString(), out var list) ? list : [];
        return defaults.Select((d, i) => i < saved.Count && ViewNames.Contains(saved[i]) ? saved[i] : d).ToList();
    }

    /// <summary>Remembers the view a layout's pane shows.</summary>
    public static void Remember(LightWaveOptions options, ViewportLayoutKind kind, int pane, string view)
    {
        if (kind == ViewportLayoutKind.Single) { options.ViewMode = view; return; }
        var views = ViewsOf(options, kind).ToList();
        if (pane < 0 || pane >= views.Count) return;
        views[pane] = view;
        options.LayoutViews[kind.ToString()] = views;
    }
}
