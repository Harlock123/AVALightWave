using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVALightWave.Core.Tests;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVALightWave.UI.Tests;

/// <summary>
/// The window in a headless test: edit modes, Selected Item, mouse modes and axes, the keyboard, the panels, the
/// numeric requester, the Scene Editor, files and undo. Settings are the test's own and saved nowhere.
/// </summary>
public class ShellTests
{
    [AvaloniaFact]
    public void The_window_starts_as_classic_layout_with_objects_and_move()
    {
        var w = Ui.Open();
        Assert.Equal("AVALightWave — Untitled", w.Title);
        Assert.Equal(EditMode.Objects, w.Mode);
        Assert.Equal(ItemDrag.Move, w.DragMode);
        Assert.Equal(LayoutViewMode.Perspective, w.View.Mode);
        Assert.Null(w.OpenPanel);
        Assert.Equal(BottomTab.GraphEditor, w.Bottom);
        Assert.Equal(["Scene", "Objects", "Bones", "Lights", "Camera", "Surfaces", "Images", "Effects", "Render", "Options"], w.Panels.Keys);
        Assert.Empty(w.ChooserItems);
        Assert.Contains("re-imagining of classic LightWave Layout", MainWindow.About);
        Assert.False(w.Hub.Running);
        Assert.False(w.Agents.Running);
    }

    [AvaloniaFact]
    public void Edit_modes_choose_what_the_view_picks_and_the_list_shows()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var crate = w.LoadObject(bench.Object("crate.lwo"))!;
        Assert.Equal([crate], w.ChooserItems);
        Assert.Same(crate, w.Session.Selected);
        var light = w.Session.Scene.Lights.Single();
        w.View.Mode = LayoutViewMode.Top;
        w.View.Fit();
        Ui.Pump();
        var at = w.View.Camera.Project(light.WorldPosition(1));
        Assert.Null(w.View.Pick(new Point(at.X, at.Y)));
        Ui.Key(w, Key.L, RawInputModifiers.Shift);
        Assert.Equal(EditMode.Lights, w.Mode);
        Assert.Equal([light], w.ChooserItems);
        // Switching mode chooses an item of it.
        Assert.Same(light, w.Session.Selected);
        Assert.Same(light, w.View.Pick(new Point(at.X, at.Y)));
        Ui.Key(w, Key.C, RawInputModifiers.Shift);
        Assert.Same(w.Session.Scene.CurrentCamera, w.Session.Selected);
        Ui.Key(w, Key.O, RawInputModifiers.Shift);
        Ui.Key(w, Key.B, RawInputModifiers.Shift);
        Assert.Equal(EditMode.Bones, w.Mode);
        Assert.Null(w.Session.Selected);
    }

    [AvaloniaFact]
    public void Keys_step_frames_and_items_and_set_the_mouse_and_view()
    {
        var w = Ui.Open();
        w.Session.Add(new NullItem("A"));
        w.Session.Add(new NullItem("B"));
        var a = w.Session.Scene.Find("A")!;
        var b = w.Session.Scene.Find("B")!;
        Ui.Key(w, Key.Down);
        Assert.Same(a, w.Session.Selected);
        Ui.Key(w, Key.Down);
        Assert.Same(b, w.Session.Selected);
        Ui.Key(w, Key.Up);
        Assert.Same(a, w.Session.Selected);
        Ui.Key(w, Key.Right);
        Ui.Key(w, Key.Right);
        Assert.Equal(3, w.Session.Frame);
        Ui.Key(w, Key.End);
        Assert.Equal(60, w.Session.Frame);
        Ui.Key(w, Key.Home);
        Assert.Equal(1, w.Session.Frame);
        Ui.Key(w, Key.Y);
        Assert.Equal(ItemDrag.Rotate, w.DragMode);
        Ui.Key(w, Key.H);
        Assert.Equal(ItemDrag.Size, w.DragMode);
        Ui.Key(w, Key.H, RawInputModifiers.Shift);
        Assert.Equal(ItemDrag.Stretch, w.DragMode);
        Ui.Key(w, Key.T);
        Assert.Equal(ItemDrag.Move, w.DragMode);
        Ui.Key(w, Key.D3);
        Assert.Equal(LayoutViewMode.Top, w.View.Mode);
        Ui.Key(w, Key.D6);
        Assert.Equal(LayoutViewMode.Light, w.View.Mode);
        Ui.Key(w, Key.D);
        Assert.Equal(BottomTab.DopeSheet, w.Bottom);
        Ui.Key(w, Key.E);
        Assert.Equal(BottomTab.SceneEditor, w.Bottom);
        w.ToggleAxis(DragAxes.Y);
        w.ToggleAxis(DragAxes.Z);
        Assert.Equal(DragAxes.X, w.View.Axes);
        w.ToggleAxis(DragAxes.X);
        Assert.Equal(DragAxes.X, w.View.Axes);
        // Enter keys the chosen item; Ctrl+Z takes it back; the menu names the step.
        Ui.Key(w, Key.Enter);
        Assert.Equal("_Undo Create Key", w.UndoItem.Header);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal("_Undo Add Null", w.UndoItem.Header);
    }

    [AvaloniaFact]
    public void The_numeric_requester_sets_exact_values_as_one_step()
    {
        var w = Ui.Open();
        var cam = w.Session.Scene.CurrentCamera!;
        w.Session.Select(cam);
        w.Session.SetFrame(12);
        var n = w.OpenNumeric()!;
        Assert.Equal("Numeric: Camera", n.Title);
        n.Boxes[0].Text = "1.5";
        n.Boxes[3].Text = "30";
        n.Boxes[8].Text = "2";
        Assert.True(n.Apply());
        Assert.Equal(new Vec3(1.5, 0, -5), cam.PositionAt(12));
        Assert.Equal(30, cam.RotationAt(12).X, 9);
        Assert.Equal(2, cam.ScaleAt(12).Z, 9);
        Assert.Equal("Numeric Camera", w.Session.History.UndoLabel);
        n.Boxes[1].Text = "high";
        Assert.False(n.Apply());
        w.Undo();
        Assert.Equal(new Vec3(0, 0, -5), cam.PositionAt(12));
        n.Close();
        w.Session.Select(null);
        Assert.Null(w.OpenNumeric());
    }

    [AvaloniaFact]
    public void The_panels_show_the_scene_and_change_it_as_undo_steps()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var pawn = w.LoadObject(bench.Object("pawn.lwo"))!;
        w.ShowPanel("Objects");
        var objects = (ItemsPanel)w.CurrentPanel!;
        Assert.Equal([pawn], objects.Listed);
        Assert.Equal(pawn, objects.Properties.ShownItem);
        w.ShowPanel("Lights");
        Assert.Equal(EditMode.Lights, w.Mode);
        var lights = (ItemsPanel)w.CurrentPanel!;
        var add = lights.Children.OfType<WrapPanel>().First().Children.OfType<Button>().Single(b => (string?)b.Content == "Spot");
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
        var spot = Assert.Single(w.Session.Scene.Lights, l => l.Type == LightType.Spot);
        Assert.Contains(spot, lights.Listed);
        Assert.Same(spot, w.Session.Selected);

        w.ShowPanel("Scene");
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "LastFrame"), "120");
        Assert.Equal(120, w.Session.Scene.LastFrame);
        Assert.Equal("Frames", w.Session.History.UndoLabel);
        w.Undo();
        Assert.Equal(60, w.Session.Scene.LastFrame);

        w.ShowPanel("Render");
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "RenderWidth"), "800");
        Assert.Equal(800, w.Session.Scene.CurrentCamera!.Width);

        foreach (var name in w.Panels.Keys)
        {
            w.ShowPanel(name);
            Assert.Equal(name, w.OpenPanel);
            Assert.NotEmpty(w.CurrentPanel!.Children);
        }
        w.ShowPanel(null);
        Assert.Null(w.CurrentPanel);
    }

    [AvaloniaFact]
    public void The_surfaces_panel_changes_a_surface_as_an_undo_step()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var ball = w.LoadObject(bench.Object("ball.lwo"))!;
        w.ShowPanel("Surfaces");
        var panel = (SurfacesPanel)w.CurrentPanel!;
        panel.Choose(ball);
        Assert.Same(ball, panel.Object);
        Assert.Equal(["Ball"], ball.Surfaces.Select(x => x.Name));
        Ui.Type(w, Ui.Find<TextBox>(panel, "SurfaceColourG"), "201");
        Assert.Equal(201, ball.Surfaces[0].Color.G);
        Assert.True(ball.Document!.Modified);
        Ui.Type(w, Ui.Find<TextBox>(panel, "SurfaceSpecular"), "10");
        Assert.Equal(0.1, ball.Surfaces[0].Specular, 9);
        Assert.Equal("Ball Specular", w.Session.History.UndoLabel);
        w.Undo();
        w.Undo();
        Assert.Equal(30, ball.Surfaces[0].Color.G);
    }

    [AvaloniaFact]
    public void The_scene_editor_hides_locks_and_chooses_items()
    {
        var w = Ui.Open();
        w.ShowBottom(BottomTab.SceneEditor);
        Ui.Pump();
        var view = w.SceneEditor.View;
        Assert.Equal(["Camera: Camera", "Light: Light"], view.Rows.Select(r => r.ToString()));
        var light = w.Session.Scene.Lights.Single();
        Ui.Click(w, Ui.In(w, view, SceneEditorView.EyeOf(1)));
        Assert.False(light.Visible);
        Assert.Equal("Hide Light", w.Session.History.UndoLabel);
        Ui.Click(w, Ui.In(w, view, SceneEditorView.LockOf(0)));
        Assert.True(w.Session.Scene.CurrentCamera!.Locked);
        Ui.Click(w, Ui.In(w, view, view.NameOf(1)));
        Assert.Same(light, w.Session.Selected);
        Assert.Equal("Light: Light (hidden)", view.Rows[1].ToString());
        w.Undo();
        w.Undo();
        Assert.True(light.Visible);
        Assert.False(w.Session.Scene.CurrentCamera!.Locked);
    }

    [AvaloniaFact]
    public async Task Scenes_and_objects_load_and_save_through_the_file_commands()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var answers = new Queue<string?>();
        w.PickFile = (_, _, _) => Task.FromResult(answers.Dequeue());
        answers.Enqueue(bench.Object("pawn.lwo"));
        await w.LoadObjectDialog();
        answers.Enqueue(bench.Object("crate.lwo"));
        await w.LoadObjectDialog();
        Assert.Equal(2, w.Session.Scene.Objects.Count());
        Assert.Equal(bench.Object("crate.lwo"), w.Options.RecentObjects[0]);
        answers.Enqueue(bench.File("two.lws"));
        await w.SaveScene(false);
        Assert.Equal("AVALightWave — two.lws", w.Title);
        Assert.Equal(bench.File("two.lws"), w.Preferences.RecentFiles[0]);
        // Replace Object keeps the item, with the new file's geometry.
        var crate = w.Session.Scene.Objects.Last();
        answers.Enqueue(bench.Object("ball.lwo"));
        await w.ReplaceObjectDialog(crate);
        Assert.Equal("ball", crate.Name);
        await w.NewScene();
        Assert.Empty(w.Session.Scene.Objects);
        await w.LoadScene(bench.File("two.lws"));
        Assert.Equal(["pawn", "crate"], w.Session.Scene.Objects.Select(o => o.Name));
        // The test window's settings were never written anywhere.
        Assert.Null(w.Preferences.Location);
        Assert.Null(w.Options.Location);
        await w.LoadScene(bench.File("missing.lws"));
        Assert.Contains("Can't load", w.Status);
    }

    [AvaloniaFact]
    public void Dragging_in_the_view_moves_the_chosen_item_by_the_mouse_mode()
    {
        var w = Ui.Open();
        w.Mode = EditMode.Cameras;
        var cam = w.Session.Scene.CurrentCamera!;
        w.View.Mode = LayoutViewMode.Top;
        w.View.Fit();
        Ui.Pump();
        var start = cam.PositionAt(1);
        var at = w.View.Camera.Project(cam.WorldPosition(1));
        var from = Ui.In(w, w.View, new Point(at.X, at.Y));
        w.MouseMove(from);
        w.MouseDown(from, MouseButton.Left);
        for (int i = 1; i <= 5; i++) w.MouseMove(from + new Point(10 * i, 0), RawInputModifiers.LeftMouseButton);
        w.MouseUp(from + new Point(50, 0), MouseButton.Left, RawInputModifiers.LeftMouseButton);
        Ui.Pump();
        Assert.True(cam.PositionAt(1).X > start.X + 0.1);
        Assert.Equal("Move Camera", w.Session.History.UndoLabel);
    }

    [AvaloniaFact]
    public void The_camera_panel_saves_and_recalls_bookmarks()
    {
        var w = Ui.Open();
        var cam = w.Session.Scene.CurrentCamera!;
        w.Session.Select(cam);
        w.ShowPanel("Camera");
        Ui.Pump();
        var panel = (ItemsPanel)w.CurrentPanel!;
        var start = cam.PositionAt(1);
        Ui.Find<TextBox>(panel, "BookmarkName").Text = "Start";
        Press(Ui.Find<Button>(panel, "SaveBookmark"));
        Assert.Equal("Start", CameraBookmarks.List(w.Session.Scene).Single().Name);
        for (int i = 0; i < 3; i++) cam.Channels[i].SetConstant(7);
        w.Session.Scene.Notify();
        panel.Reshow();
        Ui.Pump();
        Press(Ui.Find<Button>(w.CurrentPanel!, "Bookmark_Start"));
        Assert.Equal(start, cam.PositionAt(1));
        Assert.Contains("is at \"Start\"", w.Status);
    }

    [AvaloniaFact]
    public void Package_scene_gathers_the_scene_into_a_folder()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.Document.LoadObject(bench.Object("crate.lwo"));
        var r = w.PackageScene(bench.Path, zip: false);
        Assert.NotNull(r);
        Assert.True(File.Exists(Path.Combine(bench.Path, "scene package", "scene.avascene")));
        Assert.Contains("Packaged scene", w.Status);
    }

    [AvaloniaFact]
    public void The_turntable_wizard_builds_a_turntable_in_one_step()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.Document.LoadObject(bench.Object("crate.lwo"));
        var wizard = w.OpenWizard(AVALightWave.Mcp.WizardLook.Turntable)!;
        wizard.Frames.Text = "24";
        Assert.True(wizard.Apply(), wizard.Problem);
        wizard.Close();
        Ui.Pump();
        Assert.NotNull(w.Session.Scene.Find("Turntable"));
        Assert.Equal("Turntable", w.Session.History.UndoLabel);
        Assert.Contains("Built the turntable", w.Status);
        wizard = w.OpenWizard(AVALightWave.Mcp.WizardLook.Turntable)!;
        wizard.Frames.Text = "5";
        Assert.False(wizard.Apply());
        Assert.Contains("12 to 3000", wizard.Problem);
        wizard.Close();
    }

    [AvaloniaFact]
    public void The_objects_panel_makes_a_rigid_body_and_bakes_it()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var ball = w.Document.LoadObject(bench.Object("ball.lwo"));
        ball[AVAModeler.Scene.Channel.PositionY].SetConstant(2);
        w.Session.Select(ball);
        w.ShowPanel("Objects");
        Ui.Pump();
        Ui.Find<CheckBox>(w.CurrentPanel!, "RigidBody").IsChecked = true;
        Ui.Pump();
        Assert.NotNull(ball.RigidBody);
        Assert.Equal("Make Rigid Body", w.Session.History.UndoLabel);
        ((ItemsPanel)w.CurrentPanel!).Reshow();
        Ui.Pump();
        Assert.True(ball.WorldPosition(30).Y < 1.5); // falling
        Press(Ui.Find<Button>(w.CurrentPanel!, "BakeSimulation"));
        Assert.Null(ball.RigidBody);
        Assert.Equal("Bake Simulation", w.Session.History.UndoLabel);
        Assert.True(ball.Channels[1].Keys.Count > 30);
    }

    [AvaloniaFact]
    public void A_sound_track_shows_and_plays_with_the_timeline_and_the_preview()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var path = bench.File("voice.wav");
        File.WriteAllBytes(path, WavSound.Write(Enumerable.Range(0, 8000 * 2).Select(i => (float)(0.4 * Math.Sin(i * 0.1))).ToArray(), 8000));
        Assert.False(w.Wave.IsVisible);
        Assert.True(w.LoadSound(path));
        Ui.Pump();
        Assert.True(w.Wave.IsVisible);
        var sound = (SilentSound)w.Sound;
        w.Session.SetFrame(31);
        w.PlayOrStop();
        Assert.True(w.Playing);
        Assert.Equal(1, sound.Played[^1].From, 6); // a second in, at frame 31
        Assert.Null(sound.Played[^1].Seconds);
        w.PlayOrStop();
        Assert.Equal(1, sound.Stops);
        w.ShowPanel("Scene");
        Ui.Pump();
        Assert.Contains("voice.wav", string.Join(" ", Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(w.CurrentPanel!).OfType<TextBlock>().Select(t => t.Text)));
        Assert.False(w.LoadSound(bench.Object("crate.lwo")));
        Assert.Contains("Can't use crate.lwo", w.Status);
    }

    private static void Press(Button b)
    {
        b.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Ui.Pump();
    }

    private static Button ButtonIn(Control panel, string text) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(panel).OfType<Button>().First(b => (string?)b.Content == text);

    [AvaloniaFact]
    public void The_bones_panel_adds_bones_to_the_chosen_object()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.ShowPanel("Bones");
        Assert.Equal(EditMode.Bones, w.Mode);
        // No object chosen yet: Add Bone needs to know which.
        Press(ButtonIn(w.CurrentPanel!, "Add Bone"));
        Assert.Contains("Choose an object", w.Status);
        var pawn = w.LoadObject(bench.Object("pawn.lwo"))!;
        Assert.Same(pawn, w.CurrentObject);
        w.ShowPanel("Bones");
        Assert.Null(w.Session.Selected);
        Press(ButtonIn(w.CurrentPanel!, "Add Bone"));
        var root = Assert.Single(pawn.Bones);
        Assert.Same(root, w.Session.Selected);
        Assert.Equal(EditMode.Bones, w.Mode);
        Press(ButtonIn(w.CurrentPanel!, "Add Bone"));
        Assert.Equal(2, pawn.Bones.Count);
        Assert.Same(root, pawn.Bones[1].Parent);
        Press(ButtonIn(w.CurrentPanel!, "Skelegons → Bones"));
        Assert.Contains("has no skelegons", w.Status);
        Assert.Equal(["Bone 1", "Bone 2"], ((ItemsPanel)w.CurrentPanel!).Listed.Select(b => b.Name));
    }

    [AvaloniaFact]
    public async Task The_options_panel_joins_the_hub_and_lets_agents_in()
    {
        var w = Ui.Open();
        w.Hub.Pipe = "avalightwave-uitest-" + Guid.NewGuid().ToString("N")[..12];
        w.AgentPipe = "avalightwave-uitest-" + Guid.NewGuid().ToString("N")[..12];
        w.Preferences.HubEnabled = false;
        w.ShowPanel("Options");
        var hub = Ui.Find<CheckBox>(w.CurrentPanel!, "HubEnabled");
        hub.IsChecked = true;
        Ui.Pump();
        Assert.True(w.Hub.Running);
        Assert.True(AVAModeler.Hub.HubClient.Answers(w.Hub.Pipe));
        Ui.Find<CheckBox>(w.CurrentPanel!, "HubEnabled").IsChecked = false;
        Ui.Pump();
        Ui.Until(() => !w.Hub.Running, "The hub didn't stop.");
        var agents = Ui.Find<CheckBox>(w.CurrentPanel!, "AllowAgents");
        agents.IsChecked = true;
        Ui.Pump();
        Assert.True(w.Agents.Running);
        Assert.Contains("AVALightWave --mcp --attach", w.Status);
        agents.IsChecked = false;
        Ui.Pump();
        Assert.False(w.Agents.Running);
        await w.Hub.StopAsync();
    }
}
