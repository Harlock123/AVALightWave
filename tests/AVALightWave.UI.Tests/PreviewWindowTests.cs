using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVALightWave.Core.Tests;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;

namespace AVALightWave.UI.Tests;

/// <summary>
/// Render › Make Preview: the window makes the flipbook (Shift+P), then plays it at the scene's frame rate with Loop,
/// a frame back and on, and the slider; it saves PNGs and movies where the test says; closing it stops the making.
/// </summary>
public class PreviewWindowTests
{
    /// <summary>A window with a crate moving across the camera's view over frames 1 to 10, the scene ten frames long.</summary>
    private static (MainWindow W, Workbench Bench) Moving()
    {
        var bench = new Workbench();
        var w = Ui.Open();
        var crate = w.LoadObject(bench.Object("crate.lwo"))!;
        w.Session.Edit("Animate", () =>
        {
            crate.SetPosition(1, new Vec3(-1.5, 0, 0));
            crate.SetPosition(10, new Vec3(1.5, 0, 0));
            w.Session.Scene.LastFrame = 10;
        });
        return (w, bench);
    }

    private static Button Button(Window w, string label) =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(w).OfType<Button>().First(b => (string?)b.Content == label);

    private static PreviewWindow Made(MainWindow w)
    {
        var p = w.MakePreview();
        Ui.Wait(p.Making);
        Assert.True(p.Preview!.Complete);
        return p;
    }

    [AvaloniaFact]
    public void Shift_P_makes_a_preview_of_the_scenes_frames_through_its_camera()
    {
        var (w, bench) = Moving();
        using var _ = bench;
        Ui.Key(w, Key.P, RawInputModifiers.Shift);
        var p = Assert.Single(w.OwnedWindows.OfType<PreviewWindow>());
        Ui.Wait(p.Making);
        Assert.Equal(Enumerable.Range(1, 10), p.Preview!.Frames);
        Assert.Equal(10, p.Preview.Count);
        Assert.Equal((480, 360), (p.Preview.Width, p.Preview.Height));
        Assert.Contains("10 frames at 480 × 360 (Camera: Camera)", p.Status);
        Assert.NotEqual(p.Preview.Image(0), p.Preview.Image(9));
        Assert.Equal(0, p.Index);
        Assert.False(p.Playing);
        Assert.All(new[] { "Save PNGs…", "Save MP4…", "Save GIF…", "Play" }, label => Assert.True(Button(p, label).IsEnabled, label));
        Assert.False(Button(p, "Stop").IsEnabled);
        // Asking again shows the same window.
        Assert.Same(p, w.MakePreview());
        p.Close();
        Ui.Pump();
        Assert.NotSame(p, w.MakePreview());
    }

    [AvaloniaFact]
    public void Stepping_wraps_round_with_loop_on_and_stops_at_the_ends_with_it_off()
    {
        var (w, bench) = Moving();
        using var _ = bench;
        var p = Made(w);
        p.StepBy(1);
        Assert.Equal(1, p.Index);
        p.StepBy(-2);
        Assert.Equal(9, p.Index);
        p.StepBy(1);
        Assert.Equal(0, p.Index);
        p.Loop = false;
        p.StepBy(-1);
        Assert.Equal(0, p.Index);
        p.Show(9);
        p.StepBy(1);
        Assert.Equal(9, p.Index);
        Assert.Equal(9, Ui.Find<Slider>(p, "PreviewSlider").Value);
        // The player's tick: on with Loop, stopped at the end without.
        p.Tick();
        Assert.Equal(9, p.Index);
        p.Loop = true;
        p.Tick();
        Assert.Equal(0, p.Index);
        Ui.Find<Slider>(p, "PreviewSlider").Value = 4;
        Assert.Equal(4, p.Index);
    }

    [AvaloniaFact]
    public void Play_runs_at_the_scenes_rate_and_its_ticks_advance_the_frames()
    {
        var (w, bench) = Moving();
        using var _ = bench;
        w.Session.Scene.FramesPerSecond = 25;
        var p = Made(w);
        p.Play();
        Assert.True(p.Playing);
        Assert.Equal(TimeSpan.FromSeconds(1 / 25.0), p.Interval);
        Assert.Equal("Stop", Ui.Find<Button>(p, "PreviewPlay").Content);
        // The headless dispatcher doesn't run timers by the clock: the test ticks it.
        p.Tick();
        p.Tick();
        Assert.Equal(2, p.Index);
        Assert.Equal("Frame 3  (3 of 10)", Ui.Find<TextBlock>(p, "PreviewFrame").Text);
        p.Stop();
        Assert.False(p.Playing);
        Assert.Equal("Play", Ui.Find<Button>(p, "PreviewPlay").Content);
        // Without Loop it stops on the last frame, and Play then starts from the first.
        p.Loop = false;
        p.Show(8);
        p.Play();
        p.Tick();
        p.Tick();
        Assert.False(p.Playing);
        Assert.Equal(9, p.Index);
        p.Play();
        Assert.Equal(0, p.Index);
        p.Stop();
        // A step of 2 plays each frame for two of the scene's.
        Ui.Find<TextBox>(p, "PreviewStep").Text = "2";
        Ui.Wait(p.Make());
        p.Play();
        Assert.Equal(TimeSpan.FromSeconds(2 / 25.0), p.Interval);
        p.Close();
    }

    [AvaloniaFact]
    public void Save_writes_pngs_and_without_ffmpeg_says_why_there_is_no_movie()
    {
        var (w, bench) = Moving();
        using var _ = bench;
        var p = Made(w);
        string? asked = null;
        p.PickFile = (title, suggested, save) => { asked = suggested; return Task.FromResult<string?>(bench.File("out/walk.png")); };
        var said = Ui.Wait(p.Save(PreviewOutput.Pngs));
        Assert.Equal("preview.png", asked);
        Assert.StartsWith("Saved 10 frames as walk0001.png", said);
        Assert.All(Enumerable.Range(1, 10), n => Assert.True(File.Exists(bench.File($"out/{AnimationRender.FileName("walk", n)}"))));
        p.Movies = new MovieEncoder(null);
        p.PickFile = (_, suggested, _) => { asked = suggested; return Task.FromResult<string?>(bench.File("movie/walk.mp4")); };
        said = Ui.Wait(p.Save(PreviewOutput.Mp4));
        Assert.Equal("preview.mp4", asked);
        Assert.Equal(MovieEncoder.NotInstalled, said);
        Assert.True(File.Exists(bench.File("movie/walk0010.png")));
        Assert.False(File.Exists(bench.File("movie/walk.mp4")));
        // Cancelled: nothing more.
        p.PickFile = (_, _, _) => Task.FromResult<string?>(null);
        Assert.Equal(said, Ui.Wait(p.Save(PreviewOutput.Gif)));
    }

    [AvaloniaFact]
    public void A_preview_through_the_active_view_and_closing_stops_the_making()
    {
        var (w, bench) = Moving();
        using var _ = bench;
        w.View.Mode = LayoutViewMode.Top;
        Ui.Pump();
        var p = Made(w);
        Ui.Find<ComboBox>(p, "PreviewThrough").SelectedIndex = 1;
        Ui.Find<TextBox>(p, "PreviewWidth").Text = "200";
        Ui.Wait(p.Make());
        Assert.Equal("Top (XZ)", p.Preview!.LooksThrough);
        Assert.Equal(200, p.Preview.Width);
        // Bad settings: said, nothing made.
        Ui.Find<TextBox>(p, "PreviewStep").Text = "none";
        p.Make();
        Assert.Contains("whole numbers", p.Status);
        // A long one, closed while it's made.
        Ui.Find<ComboBox>(p, "PreviewThrough").SelectedIndex = 0;
        Ui.Find<TextBox>(p, "PreviewStep").Text = "1";
        Ui.Find<TextBox>(p, "PreviewLast").Text = "3000";
        Ui.Find<TextBox>(p, "PreviewWidth").Text = "640";
        var making = p.Make();
        var preview = p.Preview!;
        Assert.Equal(3000, preview.Frames.Count);
        p.Close();
        Ui.Wait(making);
        Assert.True(preview.Count < 3000);
    }
}
