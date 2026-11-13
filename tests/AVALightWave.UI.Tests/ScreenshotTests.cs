using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AVALightWave.Core.Tests;
using AVAModeler.Chat.Tests;
using AVAModeler.Layout;
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
        w.Session.Select(ball);
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

    [AvaloniaFact]
    public void The_quad_layout()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        w.Session.SetFrame(14);
        w.ViewLayout = ViewportLayoutKind.Quad;
        Ui.Pump();
        w.Viewports.FitAll();
        var perspective = w.Viewports.Panes[1].View;
        perspective.Camera.Heading = 25;
        perspective.Camera.Pitch = 20;
        perspective.Camera.Distance /= 3;
        w.Viewports.Activate(w.Viewports.Panes[1]);
        w.Session.Select(w.Session.Scene.Objects.Single(o => o.Name == "Ball"));
        Shot(w, "quad");
        Assert.Equal(4, w.Viewports.Shown.Count);
    }

    [AvaloniaFact]
    public void The_live_render_in_the_quad_layout()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        w.Session.SetFrame(14);
        w.ViewLayout = ViewportLayoutKind.Quad;
        Ui.Pump();
        w.Viewports.FitAll();
        var perspective = w.Viewports.Panes[1].View;
        perspective.Camera.Heading = 25;
        perspective.Camera.Pitch = 20;
        perspective.Camera.Distance /= 3;
        var live = w.Viewports.Panes[3];
        live.RenderSamples = 4;
        w.Viewports.Activate(live);
        Ui.Key(w, Avalonia.Input.Key.F8);
        Ui.Until(() => live.RenderCounter.StartsWith("done", StringComparison.Ordinal), "The live render finishes.", TimeSpan.FromMinutes(2));
        Shot(w, "live-render");
        Assert.True(live.Rendering);
    }

    [AvaloniaFact]
    public void The_preview_window()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        var p = w.MakePreview();
        p.Width = 760;
        p.Height = 640;
        Ui.Wait(p.Making.ContinueWith(_ => 0, TaskScheduler.Default));
        p.Show(13);
        Shot(p, "preview");
        Assert.True(p.Preview!.Complete);
        p.Close();
    }

    [AvaloniaFact]
    public void Lens_flares_and_volumetric_light_rendered()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        var s = w.Session;
        s.SetFrame(14);
        var scene = s.Scene;
        scene.AmbientIntensity = 0.05;
        scene.GradientTop = new AVAModeler.Core.Vec3(0.03, 0.04, 0.07);
        scene.GradientBottom = new AVAModeler.Core.Vec3(0.01, 0.01, 0.02);
        var key = (LightItem)scene.Find("Key")!;
        s.SetVolumetric(key, new VolumetricLight { On = true, Intensity = 1.2, Samples = 48 });
        s.SetShadows(key, true, new ShadowMapSettings { On = true, Size = 1024, Fuzziness = 3 });
        // A lamp glowing behind the crate, in the picture: its flare and its glow in the air.
        var lamp = s.Add(new LightItem("Lamp", LightType.Point) { Color = new AVAModeler.Core.Vec3(0.6, 0.8, 1), Range = 4 });
        s.SetVector(lamp, 0, new AVAModeler.Core.Vec3(1.7, 1.5, 1.6));
        s.SetLensFlare(lamp, new LensFlare { On = true, Size = 0.06, Ring = true, Streaks = 8, StreakLength = 0.25 });
        s.SetVolumetric(lamp, new VolumetricLight { On = true, Intensity = 0.5, Samples = 16, Shadows = false });
        w.ShowPanel("Lights");
        s.Select(lamp);
        w.CurrentPanel!.Refresh();
        var cam = scene.CurrentCamera!;
        cam.Width = 640;
        cam.Height = 480;
        var r = new RenderWindow(s) { Width = 820, Height = 640 };
        r.Show(w);
        Ui.Wait(r.Render(new RenderSettings { Width = 640, Height = 480, SamplesPerPixel = 4 }).ContinueWith(_ => 0, TaskScheduler.Default));
        Shot(r, "effects");
        r.Close();
        Look(w, 25, 20, 2.6);
        Ui.Pump();
        // The panel scrolled down to the lamp's shadow type, lens flare and volumetric settings.
        (w.CurrentPanel!.Parent as ScrollViewer)?.ScrollToEnd();
        Shot(w, "lights-panel");
    }

    [AvaloniaFact]
    public void The_graph_editor_with_spans_and_tcb()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        var s = w.Session;
        s.SetFrame(16);
        var ball = s.Scene.Objects.Single(o => o.Name == "Ball");
        s.Select(ball);
        w.ShowBottom(BottomTab.GraphEditor);
        var g = w.Graph.Editor;
        g.ShowOnly("position.y");
        g.ToggleShown("scale.y");
        // The top of the second bounce eased as a Bezier key; the landing a stepped squash.
        g.Choose([g.Key("position.y", 31)]);
        g.SetSpan(SpanKind.Bezier);
        g.Choose([g.Key("position.y", 16), g.Key("position.y", 46)]);
        g.Set(KeyField.Tension, 0.5);
        w.ShowPanel("Objects");
        w.CurrentPanel!.Refresh();
        Look(w, 20, 15, 3.2);
        Shot(w, "graph-editor");
    }

    [AvaloniaFact]
    public void The_images_panel_and_a_composited_render()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        var s = w.Session;
        s.SetFrame(14);
        w.ShowPanel("Images");
        var images = (ImagesPanel)w.CurrentPanel!;
        Assert.True(images.Add(bench.File(Path.Combine("images", "sky.png")), false));
        Assert.True(images.Add(bench.File(Path.Combine("images", "frame.png")), false));
        Assert.True(images.Add(bench.File(Path.Combine("images", "stripes0001.png")), true));
        var sky = s.Scene.Images[0].Path;
        var frame = s.Scene.Images[1].Path;
        s.SetCompositing(new CompositingSettings
        {
            Background = sky, Foreground = frame, Key = ForegroundKey.Color,
            KeyLow = new AVAModeler.Core.Vec3(0, 0.3, 0), KeyHigh = new AVAModeler.Core.Vec3(0.3, 1, 0.3),
        });
        // Without its floor the pawn and the crate stand against the sky; the ball dissolves half away.
        s.Remove(s.Scene.Objects.Single(o => o.Name == "Floor"));
        s.SetDissolve(s.Scene.Objects.Single(o => o.Name == "Ball"), 0.5);
        w.ShowPanel("Images");
        Ui.Find<ListBox>(w.CurrentPanel!, "ImageList").SelectedIndex = 2;
        w.View.Mode = LayoutViewMode.Camera;
        Shot(w, "images");
        var cam = s.Scene.CurrentCamera!;
        cam.Width = 640;
        cam.Height = 480;
        var r = new RenderWindow(s) { Width = 820, Height = 640 };
        r.Show(w);
        Ui.Wait(r.Render(new RenderSettings { Width = 640, Height = 480, SamplesPerPixel = 2 }).ContinueWith(_ => 0, TaskScheduler.Default));
        Shot(r, "composite");
        r.Close();
    }

    [AvaloniaFact]
    public void The_render_queue()
    {
        var (w, bench) = Showcase();
        using var _ = bench;
        var folder = Directory.CreateDirectory(Path.Combine(bench.Path, "studio-farm")).FullName;
        w.QueueFolder = folder;
        var q = w.OpenRenderQueue();
        q.Width = 900;
        q.Height = 520;
        Ui.Pump();
        Ui.Find<TextBox>(q, "QueueWidth").Text = "64";
        Ui.Find<TextBox>(q, "QueueHeight").Text = "48";
        Assert.NotNull(Ui.Wait(q.AddCurrentScene()));
        w.Session.Scene.LastFrame = 30;
        Assert.NotNull(Ui.Wait(q.AddCurrentScene()));
        var farm = new FarmFolder(folder);
        var second = farm.Scan()[1];
        Ui.Wait(Task.Run(async () =>
        {
            // The first job rendered by a node named after a studio machine; the second waits.
            farm.Cancel(second.Id);
            await FarmNode.RunAsync(folder, new FarmNodeOptions { Once = true, Name = "studio-pc" }, CancellationToken.None);
        }).ContinueWith(_ => 0, TaskScheduler.Default));
        Ui.Wait(q.RefreshAsync().ContinueWith(_ => 0, TaskScheduler.Default));
        Shot(q, "render-queue");
        q.Close();
    }
}
