using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AVALightWave.Core.Tests;
using AVAModeler.Chat.Tests;
using AVAModeler.Layout.UI;
using AVAModeler.Scene;

namespace AVALightWave.UI.Tests;

/// <summary>
/// Pictures of AVALightWave at work, drawn in real (headless) windows. Set AVALIGHTWAVE_SHOTS to a folder to save them
/// as PNGs: the README's screenshots come from here.
/// </summary>
public class ScreenshotTests
{
    private static string? Folder => Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOTS") is { Length: > 0 } d ? d : null;

    private static void Shot(Window w, string name)
    {
        Ui.Pump();
        var frame = w.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Folder is not { } dir) return;
        Directory.CreateDirectory(dir);
        frame.Save(Path.Combine(dir, $"AVALightWave-{name}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    /// <summary>The sample showcase scene (a pawn, a crate and a bouncing ball on a floor, a key spot and a fill) in a window.</summary>
    private static (MainWindow Window, Workbench Bench) Showcase(int width = 1400, int height = 880)
    {
        var bench = new Workbench();
        File.Copy(Path.Combine(Samples.Folder, "showcase.avascene"), bench.File("showcase.avascene"));
        var w = Ui.Open(width, height);
        Ui.Wait(w.LoadScene(bench.File("showcase.avascene")).ContinueWith(_ => 0, TaskScheduler.Default));
        Assert.Empty(w.Session.Warnings);
        return (w, bench);
    }

    private static void Look(MainWindow w, double heading, double pitch, double closer = 1)
    {
        w.View.Mode = LayoutViewMode.Perspective;
        w.View.Fit();
        var c = w.View.Camera;
        c.Heading = heading;
        c.Pitch = pitch;
        c.Distance /= closer;
        w.View.InvalidateVisual();
    }

    [AvaloniaFact]
    public void The_layout_with_the_showcase_scene()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        w.Session.SetFrame(14);
        w.ShowPanel("Objects");
        w.Session.Select(w.Session.Scene.Objects.Single(o => o.Name == "Ball"));
        w.CurrentPanel!.Refresh();
        Look(w, 25, 20, 3.2);
        Shot(w, "layout");
        Assert.Equal("AVALightWave — showcase.avascene", w.Title);
    }

    [AvaloniaFact]
    public void The_light_view_and_the_scene_editor()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        w.Session.SetFrame(30);
        w.ShowPanel("Lights");
        w.Session.Select(w.Session.Scene.Lights.Single(l => l.Name == "Key"));
        w.CurrentPanel!.Refresh();
        w.View.Mode = LayoutViewMode.Light;
        w.Document.SetLocked(w.Session.Scene.Objects.Single(o => o.Name == "Floor"), true);
        w.ShowBottom(BottomTab.SceneEditor);
        Shot(w, "scene-editor");
        Assert.Equal(7, w.SceneEditor.View.Rows.Count);
    }

    [AvaloniaFact]
    public void The_surfaces_panel()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        w.Session.SetFrame(1);
        var ball = w.Session.Scene.Objects.Single(o => o.Name == "Ball");
        w.ShowPanel("Surfaces");
        ((SurfacesPanel)w.CurrentPanel!).Choose(ball);
        w.Document.EditSurface(ball, 0, "Ball Colour", s => s.Color = new AVAModeler.Core.Rgb(40, 110, 220));
        w.CurrentPanel!.Refresh();
        w.View.Mode = LayoutViewMode.Camera;
        w.ShowBottom(BottomTab.DopeSheet);
        Shot(w, "surfaces");
    }

    [AvaloniaFact]
    public void The_ai_chat_staging_a_scene()
    {
        using var bench = new Workbench();
        var api = new FakeClaude()
            .Reply([FakeClaude.Say("I'll load the pawn and the crate, light them with a warm spot, and frame the camera."),
                FakeClaude.Call("t1", "add_object", new { file = "objects/floor.lwo", name = "Floor" }),
                FakeClaude.Call("t2", "add_object", new { file = "objects/pawn.lwo", name = "Pawn", position = new[] { -0.6, 0, 0 } }),
                FakeClaude.Call("t3", "add_object", new { file = "objects/crate.lwo", name = "Crate", position = new[] { 0.8, 0, 0.4 }, rotation = new[] { 25.0, 0, 0 } })])
            .Reply([FakeClaude.Call("t4", "add_light", new { type = "spot", name = "Key", position = new[] { -3.0, 4, -3 }, look_at = new[] { 0.0, 0.4, 0 }, color = new[] { 255, 236, 200 }, cone_angle = 30, edge_angle = 10 }),
                FakeClaude.Call("t5", "set_transform", new { item = "Camera", position = new[] { 0.4, 1.6, -4.6 }, key = false }),
                FakeClaude.Call("t6", "look_at", new { item = "Camera", point = new[] { 0.0, 0.5, 0 }, key = false }),
                FakeClaude.Call("t7", "render_scene", new { width = 320, samples = 1 })])
            .Reply([FakeClaude.Say("Here it is: the pawn and the crate on the floor, lit by a warm spot from the upper left with the default light as a fill. Ctrl+Z takes back any step.")]);
        var w = Ui.Open();
        w.ChatRoot = bench.Path;
        w.Chat.ClientFactory = _ => api.Client();
        w.Chat.Keys.Override = FakeClaude.Key;
        w.ToggleChat();
        w.Chat.Input.Text = "Put the pawn and the crate on the floor and light them with a warm spotlight.";
        w.Chat.Send();
        Ui.Wait(w.Chat.Running.ContinueWith(_ => 0, TaskScheduler.Default));
        Assert.Equal(3, w.Session.Scene.Objects.Count());
        w.View.Mode = LayoutViewMode.Camera;
        w.Mode = EditMode.Lights;
        Shot(w, "chat");
    }

    [AvaloniaFact]
    public void A_ray_traced_frame()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        w.Session.SetFrame(14);
        var cam = w.Session.Scene.CurrentCamera!;
        cam.Width = 640;
        cam.Height = 480;
        var r = new RenderWindow(w.Session) { Width = 820, Height = 640 };
        r.Show(w);
        Ui.Wait(r.Render(new RenderSettings { Width = 640, Height = 480, SamplesPerPixel = 4 }).ContinueWith(_ => 0, TaskScheduler.Default));
        Shot(r, "render");
        r.Close();
    }
}
