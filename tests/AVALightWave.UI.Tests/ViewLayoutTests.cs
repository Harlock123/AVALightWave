using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVALightWave.Core.Tests;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave.UI.Tests;

/// <summary>
/// The view layouts: one, two side by side, two stacked and four panes, each with its own view type; the active pane
/// (the last clicked) takes the view keys and the toolbar's view list; picking and dragging work in any pane; the
/// layout and its views are remembered in lightwave.json.
/// </summary>
public class ViewLayoutTests
{
    private static LayoutViewMode[] Modes(MainWindow w) => w.Viewports.Shown.Select(p => p.View.Mode).ToArray();

    private static void Ready(MainWindow w)
    {
        Ui.Pump();
        w.Viewports.FitAll();
        Ui.Pump();
    }

    private static Point Middle(MainWindow w, ViewportPane pane) => Ui.In(w, pane, new Point(pane.Bounds.Width / 2, pane.Bounds.Height / 2 + 20));

    [AvaloniaFact]
    public void Switching_layouts_gives_one_two_or_four_panes()
    {
        var w = Ui.Open();
        Assert.Equal(ViewportLayoutKind.Single, w.ViewLayout);
        Assert.Single(w.Viewports.Shown);
        Assert.Equal([LayoutViewMode.Perspective], Modes(w));
        Ui.Key(w, Key.D4, RawInputModifiers.Control);
        Assert.Equal(ViewportLayoutKind.Quad, w.ViewLayout);
        Assert.Equal([LayoutViewMode.Top, LayoutViewMode.Perspective, LayoutViewMode.Front, LayoutViewMode.Camera], Modes(w));
        Assert.All(w.Viewports.Shown, p => Assert.Same(w.Viewports, p.Parent));
        // Four quarters: each about half the middle's width and height.
        var whole = w.Viewports.Bounds;
        Assert.All(w.Viewports.Shown, p => Assert.InRange(p.Bounds.Width, whole.Width / 2 - 10, whole.Width / 2));
        Assert.All(w.Viewports.Shown, p => Assert.InRange(p.Bounds.Height, whole.Height / 2 - 10, whole.Height / 2));
        Ui.Key(w, Key.D2, RawInputModifiers.Control);
        Assert.Equal([LayoutViewMode.Perspective, LayoutViewMode.Camera], Modes(w));
        Assert.True(w.Viewports.Panes[0].Bounds.Height > whole.Height - 10);
        Assert.Null(w.Viewports.Panes[2].Parent);
        // The toolbar's layout list.
        Ui.Find<ComboBox>(w, "ViewLayout").SelectedIndex = (int)ViewportLayoutKind.Stacked;
        Ui.Pump();
        Assert.Equal(ViewportLayoutKind.Stacked, w.ViewLayout);
        Assert.Equal([LayoutViewMode.Top, LayoutViewMode.Camera], Modes(w));
        Assert.True(w.Viewports.Panes[0].Bounds.Width > whole.Width - 10);
        Ui.Key(w, Key.D1, RawInputModifiers.Control);
        Assert.Single(w.Viewports.Shown);
        Assert.Equal(0, Ui.Find<ComboBox>(w, "ViewLayout").SelectedIndex);
        // The view keys still work on the single view.
        Ui.Key(w, Key.D3);
        Assert.Equal(LayoutViewMode.Top, w.View.Mode);
    }

    [AvaloniaFact]
    public void Each_panes_chooser_changes_only_its_view()
    {
        var w = Ui.Open();
        w.ViewLayout = ViewportLayoutKind.Quad;
        Ui.Pump();
        var chooser = Ui.Find<ComboBox>(w, "PaneView2");
        chooser.SelectedItem = "Side";
        Ui.Pump();
        Assert.Equal([LayoutViewMode.Top, LayoutViewMode.Perspective, LayoutViewMode.Side, LayoutViewMode.Camera], Modes(w));
        // A view changed another way shows in its chooser.
        w.Viewports.Panes[0].View.Mode = LayoutViewMode.Light;
        Assert.Equal("Light", Ui.Find<ComboBox>(w, "PaneView0").SelectedItem);
        Assert.Equal("Side", chooser.SelectedItem);
    }

    [AvaloniaFact]
    public void The_active_pane_follows_clicks_and_takes_the_view_keys()
    {
        var w = Ui.Open();
        w.ViewLayout = ViewportLayoutKind.Quad;
        Ready(w);
        var panes = w.Viewports.Panes;
        Assert.Same(panes[0], w.Viewports.Active);
        Assert.True(panes[0].Outlined);
        Ui.Click(w, Middle(w, panes[3]));
        Assert.Same(panes[3], w.Viewports.Active);
        Assert.Same(panes[3].View, w.View);
        Assert.True(panes[3].Outlined);
        Assert.False(panes[0].Outlined);
        Assert.Equal("Camera", Ui.Find<ComboBox>(w, "ViewMode").SelectedItem);
        Ui.Key(w, Key.D5);
        Assert.Equal([LayoutViewMode.Top, LayoutViewMode.Perspective, LayoutViewMode.Front, LayoutViewMode.Side], Modes(w));
        Assert.Equal("Side", Ui.Find<ComboBox>(w, "ViewMode").SelectedItem);
        Ui.Click(w, Middle(w, panes[1]));
        Assert.Same(panes[1], w.Viewports.Active);
        Assert.Equal("Perspective", Ui.Find<ComboBox>(w, "ViewMode").SelectedItem);
        // The toolbar's view list works on the active pane.
        Ui.Find<ComboBox>(w, "ViewMode").SelectedItem = "Front";
        Ui.Pump();
        Assert.Equal([LayoutViewMode.Top, LayoutViewMode.Front, LayoutViewMode.Front, LayoutViewMode.Side], Modes(w));
        // A layout without the active pane makes the first active; one pane has no outline.
        Ui.Key(w, Key.D1, RawInputModifiers.Control);
        Assert.Same(panes[0], w.Viewports.Active);
        Assert.False(panes[0].Outlined);
    }

    [AvaloniaFact]
    public void Modes_and_axes_go_to_every_pane()
    {
        var w = Ui.Open();
        w.ViewLayout = ViewportLayoutKind.Quad;
        Ui.Pump();
        Ui.Key(w, Key.Y);
        Assert.All(w.Viewports.Panes, p => Assert.Equal(ItemDrag.Rotate, p.View.DragMode));
        w.ToggleAxis(DragAxes.Z);
        Assert.All(w.Viewports.Panes, p => Assert.Equal(DragAxes.X | DragAxes.Y, p.View.Axes));
        var cam = w.Session.Scene.CurrentCamera!;
        Assert.All(w.Viewports.Panes, p => Assert.False(p.View.Pickable(cam)));
        w.Mode = EditMode.Cameras;
        Assert.All(w.Viewports.Panes, p => Assert.True(p.View.Pickable(cam)));
    }

    [AvaloniaFact]
    public void Picking_and_dragging_work_in_any_pane_as_one_undo_step()
    {
        var w = Ui.Open();
        w.ViewLayout = ViewportLayoutKind.Quad;
        w.Mode = EditMode.Cameras;
        w.Session.Select(null);
        Ready(w);
        var front = w.Viewports.Panes[2];
        Assert.Equal(LayoutViewMode.Front, front.View.Mode);
        var cam = w.Session.Scene.CurrentCamera!;
        var at = front.View.Camera.Project(cam.WorldPosition(1));
        var from = Ui.In(w, front.View, new Point(at.X, at.Y));
        Ui.Click(w, from);
        Assert.Same(front, w.Viewports.Active);
        Assert.Same(cam, w.Session.Selected);
        int steps = w.Session.History.Count;
        var start = cam.PositionAt(1);
        w.MouseMove(from);
        w.MouseDown(from, MouseButton.Left);
        for (int i = 1; i <= 5; i++) w.MouseMove(from + new Point(10 * i, 0), RawInputModifiers.LeftMouseButton);
        w.MouseUp(from + new Point(50, 0), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        Ui.Pump();
        Assert.True(cam.PositionAt(1).X > start.X + 0.1);
        Assert.Equal("Move Camera", w.Session.History.UndoLabel);
        Assert.Equal(steps + 1, w.Session.History.Count);
        w.Undo();
        Assert.Equal(start, cam.PositionAt(1));
    }

    [AvaloniaFact]
    public void The_layout_and_its_views_are_remembered()
    {
        using var bench = new Workbench();
        var path = bench.File("lightwave.json");
        var options = LightWaveOptions.Load(path);
        var w = new MainWindow([], new Preferences(), options, forTests: true) { Width = 1400, Height = 880 };
        w.Show();
        Ui.Pump();
        Ui.Key(w, Key.D4, RawInputModifiers.Control);
        Ui.Find<ComboBox>(w, "PaneView1").SelectedItem = "Light";
        Ui.Pump();
        w.Close();
        var back = LightWaveOptions.Load(path);
        Assert.Equal("Quad", back.ViewLayout);
        Assert.Equal(["Top", "Light", "Front", "Camera"], back.LayoutViews["Quad"]);
        var again = new MainWindow([], new Preferences(), back, forTests: true) { Width = 1400, Height = 880 };
        again.Show();
        Ui.Pump();
        Assert.Equal(ViewportLayoutKind.Quad, again.ViewLayout);
        Assert.Equal([LayoutViewMode.Top, LayoutViewMode.Light, LayoutViewMode.Front, LayoutViewMode.Camera], Modes(again));
        // The single view is remembered too, as before.
        Ui.Key(again, Key.D1, RawInputModifiers.Control);
        Ui.Key(again, Key.D4);
        Assert.Equal("Front", LightWaveOptions.Load(path).ViewMode);
        Assert.Equal("Single", LightWaveOptions.Load(path).ViewLayout);
        again.Close();
    }

    [AvaloniaFact]
    public void The_live_render_shows_the_camera_refining_and_starts_again_when_the_scene_changes()
    {
        var w = Ui.Open();
        var pane = w.Viewports.Active;
        pane.RenderSamples = 4;
        pane.RenderMaxSize = 160;
        Ui.Key(w, Key.F8);
        Assert.True(pane.Rendering);
        Assert.Equal(LayoutViewMode.Camera, pane.View.Mode);
        Assert.Equal(ViewportPane.RenderChoice, pane.Chooser.SelectedItem);
        Ui.Until(() => pane.RenderCounter.StartsWith("done", StringComparison.Ordinal), "The live render finishes its passes.");
        Assert.Contains("4 rays", pane.RenderCounter);
        Assert.Equal(160, Math.Max(pane.RenderPicture!.PixelSize.Width, pane.RenderPicture.PixelSize.Height));

        // A change to the scene starts it again.
        var shown = new List<ProgressiveFrame>();
        pane.RenderShown += shown.Add;
        var light = w.Session.Scene.Items.OfType<LightItem>().First();
        w.Session.SetVector(light, 0, new AVAModeler.Core.Vec3(1, 2, -3));
        pane.Tick();
        Assert.Empty(shown); // changed, but not yet held still
        pane.Tick();
        Ui.Until(() => shown.Any(f => f.Done), "It renders the changed scene.");
        Assert.Equal(1, shown[0].Step);

        // Another view type turns it off; the chooser turns it on again; hidden panes stop.
        Ui.Key(w, Key.D2);
        Assert.False(pane.Rendering);
        Assert.Equal("Perspective", pane.Chooser.SelectedItem);
        pane.Chooser.SelectedItem = ViewportPane.RenderChoice;
        Ui.Pump();
        Assert.True(pane.Rendering);
        Ui.Key(w, Key.F8);
        Assert.False(pane.Rendering);
        Assert.Equal("Camera", pane.Chooser.SelectedItem);
    }
}

/// <summary>The panes' OpenGL path in a headless window, where OpenGL can't start: it falls back to software cleanly.</summary>
public class OpenGlPaneTests
{
    [AvaloniaFact]
    public void Panes_ask_for_opengl_and_fall_back_to_software_where_it_cant_start()
    {
        using var bench = new AVALightWave.Core.Tests.Workbench();
        var w = Ui.Open();
        var crate = w.LoadObject(bench.Object("crate.lwo"))!;
        w.UseOpenGl(true);
        Ui.Pump();
        Assert.All(w.Viewports.Panes, p => Assert.NotNull(p.Gl));
        Assert.False(w.View.DrawsScene);
        // While waiting for a context the view still picks and takes the mouse.
        w.View.Mode = AVAModeler.Layout.UI.LayoutViewMode.Top;
        w.View.Fit();
        Ui.Pump();
        var at = w.View.Camera.Project(crate.BaseMesh().Bounds().Center);
        Assert.Same(crate, w.View.Pick(new Avalonia.Point(at.X, at.Y)));
        // Headless there's no OpenGL: the check puts software back and says so.
        w.CheckOpenGl();
        Assert.All(w.Viewports.Panes, p => Assert.Null(p.Gl));
        Assert.True(w.View.DrawsScene);
        Assert.Contains("drawn in software", w.Status);
        // And off again changes nothing more.
        w.UseOpenGl(false);
        Assert.True(w.View.DrawsScene);
    }
}
