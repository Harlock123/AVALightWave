using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Rendering;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>
/// Make Preview: the frames in order by step, drawn as the view draws them through the camera's frame shape (a moving
/// object in different places, shaded unlike wireframe, no selection box), on a copy of the scene, cancelled between
/// frames, kept under the memory limit, saved as PNGs and as a movie (or, without ffmpeg, the PNGs and the reason).
/// </summary>
public class PreviewTests
{
    private sealed class Now<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>A crate moving left to right across the camera's picture over frames 1 to 10.</summary>
    private static (SceneDocument Doc, ObjectItem Crate) Moving(Workbench bench)
    {
        var d = new SceneDocument();
        var crate = d.LoadObject(bench.Object("crate.lwo"));
        crate.SetPosition(1, new Vec3(-1.5, 0, 0));
        crate.SetPosition(10, new Vec3(1.5, 0, 0));
        d.Session.Select(null);
        return (d, crate);
    }

    private static Preview Made(SceneDocument d, PreviewSettings settings)
    {
        var p = new Preview(d.Scene, settings);
        Assert.Equal(p.Frames.Count, p.MakeAsync(cancel: TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        return p;
    }

    /// <summary>The middle, across, of the pixels that differ between two pictures of the same size.</summary>
    private static double MiddleOfDifference(uint[] a, uint[] b, int width)
    {
        double sum = 0;
        int n = 0;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) { sum += i % width; n++; }
        Assert.True(n > 50, "The object didn't show.");
        return sum / n;
    }

    [Fact]
    public void Frames_come_in_order_by_step_timed_at_the_scenes_rate()
    {
        using var bench = new Workbench();
        var (d, _) = Moving(bench);
        d.Scene.FramesPerSecond = 24;
        var p = Made(d, new PreviewSettings { First = 1, Last = 10, Step = 3, Width = 64 });
        Assert.Equal([1, 4, 7, 10], p.Frames);
        Assert.Equal(4, p.Count);
        Assert.True(p.Complete);
        Assert.Equal(TimeSpan.Zero, p.TimeOf(0));
        Assert.Equal(TimeSpan.FromSeconds(9 / 24.0), p.TimeOf(3));
        Assert.Equal(TimeSpan.FromSeconds(3 / 24.0), p.FrameDuration);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(p.Width * p.Height, p.Image(i).Length));
        Assert.Equal("Camera: Camera", p.LooksThrough);
    }

    [Fact]
    public void A_moving_object_is_in_different_places_in_the_first_and_last_frames()
    {
        using var bench = new Workbench();
        var (d, crate) = Moving(bench);
        var settings = new PreviewSettings { First = 1, Last = 10, Width = 160 };
        var p = Made(d, settings);
        d.SetVisible(crate, false);
        var empty = Made(d, settings);
        double first = MiddleOfDifference(p.Image(0), empty.Image(0), p.Width), last = MiddleOfDifference(p.Image(9), empty.Image(9), p.Width);
        Assert.True(first < p.Width / 2.0 - 10, $"Frame 1's crate is at {first}.");
        Assert.True(last > p.Width / 2.0 + 10, $"Frame 10's crate is at {last}.");
    }

    [Fact]
    public void Shaded_and_wireframe_previews_differ()
    {
        using var bench = new Workbench();
        var (d, _) = Moving(bench);
        var shaded = Made(d, new PreviewSettings { First = 5, Last = 5, Width = 96, Shaded = true });
        var wire = Made(d, new PreviewSettings { First = 5, Last = 5, Width = 96, Shaded = false });
        Assert.NotEqual(shaded.Image(0), wire.Image(0));
    }

    [Fact]
    public void The_chosen_items_box_is_left_out()
    {
        using var bench = new Workbench();
        var (d, crate) = Moving(bench);
        var settings = new PreviewSettings { First = 5, Last = 5, Width = 96 };
        var none = Made(d, settings);
        d.Session.Select(crate);
        Assert.Equal(none.Image(0), Made(d, settings).Image(0));
    }

    [Fact]
    public void The_picture_takes_the_cameras_frame_shape_and_a_view_its_own()
    {
        using var bench = new Workbench();
        var (d, _) = Moving(bench);
        var cam = d.Scene.CurrentCamera!;
        cam.Width = 1280;
        cam.Height = 720;
        var p = new Preview(d.Scene, new PreviewSettings { Width = 320 });
        Assert.Equal((320, 180), (p.Width, p.Height));
        p = new Preview(d.Scene, new PreviewSettings { Width = 320, Height = 120 });
        Assert.Equal((213, 120), (p.Width, p.Height));
        Assert.Equal((300, 200), Preview.FrameSize(1.5, 400, 200));
        var top = new ViewCamera(ViewKind.Top) { PixelsPerUnit = 40 };
        top.Resize(400, 300);
        p = Made(d, new PreviewSettings { First = 1, Last = 2, Width = 200, View = top });
        Assert.Equal((200, 150), (p.Width, p.Height));
        Assert.Equal("Top (XZ)", p.LooksThrough);
        Assert.Throws<ArgumentException>(() => new Preview(d.Scene, new PreviewSettings { First = 5, Last = 2 }));
        Assert.Throws<ArgumentException>(() => new Preview(d.Scene, new PreviewSettings { Step = 0 }));
    }

    [Fact]
    public void A_preview_too_big_for_memory_is_made_smaller_or_refused()
    {
        var d = new SceneDocument();
        // 4000 frames of 1600 × 1200 would be 30 GB.
        var p = new Preview(d.Scene, new PreviewSettings { First = 1, Last = 4000, Width = 1600 });
        Assert.True(4L * p.Width * p.Height * p.Frames.Count <= Preview.MostBytes);
        Assert.True(p.Width < 1600 && p.Width >= Preview.SmallestWidth);
        Assert.Equal(4.0 / 3, (double)p.Width / p.Height, 1);
        Assert.Contains("under 512 MB", p.Note);
        Assert.Null(new Preview(d.Scene, new PreviewSettings { First = 1, Last = 60, Width = 320 }).Note);
        Assert.Contains("won't fit in memory", Assert.Throws<ArgumentException>(() => new Preview(d.Scene, new PreviewSettings { First = 1, Last = 10000, Width = 4096 })).Message);
        Assert.Contains("at most", Assert.Throws<ArgumentException>(() => new Preview(d.Scene, new PreviewSettings { First = 1, Last = 20000, Width = 64 })).Message);
    }

    [Fact]
    public async Task It_works_on_a_copy_so_the_scene_can_change_meanwhile()
    {
        using var bench = new Workbench();
        var (d, crate) = Moving(bench);
        var settings = new PreviewSettings { First = 1, Last = 1, Width = 96 };
        var before = Made(d, settings).Image(0);
        var p = new Preview(d.Scene, settings);
        d.Session.Edit("Move", () => crate.SetPosition(1, new Vec3(1.5, 0, 0)));
        await p.MakeAsync(cancel: TestContext.Current.CancellationToken);
        Assert.Equal(before, p.Image(0));
        Assert.NotEqual(before, Made(d, settings).Image(0));
    }

    [Fact]
    public async Task Cancelling_stops_between_frames_and_keeps_those_done()
    {
        using var bench = new Workbench();
        var (d, _) = Moving(bench);
        var p = new Preview(d.Scene, new PreviewSettings { First = 1, Last = 30, Width = 64 });
        using var stop = new CancellationTokenSource();
        var seen = new List<PreviewProgress>();
        int made = await p.MakeAsync(new Now<PreviewProgress>(r => { seen.Add(r); if (r.Done == 2) stop.Cancel(); }), stop.Token);
        Assert.Equal(2, made);
        Assert.Equal(2, p.Count);
        Assert.False(p.Complete);
        Assert.Equal([1, 2], seen.Select(s => s.Frame));
        Assert.Equal("Frame 2 (2 of 30)", seen[^1].Text);
        // Already cancelled: nothing.
        var q = new Preview(d.Scene, new PreviewSettings { First = 1, Last = 30, Width = 64 });
        Assert.Equal(0, await q.MakeAsync(cancel: stop.Token));
    }

    [Fact]
    public void A_png_sequence_saves_and_reads_back()
    {
        using var bench = new Workbench();
        var (d, _) = Moving(bench);
        var p = Made(d, new PreviewSettings { First = 1, Last = 3, Width = 64 });
        var folder = bench.File("shots");
        var files = p.SavePngs(folder, Preview.NameFrom(bench.File("walk0001.png")));
        Assert.Equal([AnimationRender.FileName("walk", 1), "walk0002.png", "walk0003.png"], files.Select(Path.GetFileName));
        for (int i = 0; i < 3; i++)
        {
            var img = TextureImages.Load(files[i]);
            Assert.False(img.IsPlaceholder);
            Assert.Equal((p.Width, p.Height), (img.Width, img.Height));
            Assert.Equal(Preview.Rgba(p.Image(i)), img.Rgba);
        }
        Assert.Equal("preview", Preview.NameFrom("/x/0001.png"));
        Assert.Equal("walk", Preview.NameFrom("/x/walk.mp4"));
    }

    [Fact]
    public async Task Without_ffmpeg_a_movie_save_writes_the_pngs_and_says_why()
    {
        using var bench = new Workbench();
        var (d, _) = Moving(bench);
        var p = Made(d, new PreviewSettings { First = 1, Last = 4, Width = 64 });
        var folder = bench.File("movie");
        var saved = await p.SaveMovieAsync(folder, "walk", MovieFormat.Mp4, new MovieEncoder(null), TestContext.Current.CancellationToken);
        Assert.Null(saved.Movie);
        Assert.Equal(MovieEncoder.NotInstalled, saved.Problem);
        Assert.Equal(4, saved.Frames.Count);
        Assert.All(saved.Frames, f => Assert.True(File.Exists(f)));
        // An ffmpeg that fails: the PNGs stay, and what it said comes back.
        if (!OperatingSystem.IsWindows() && File.Exists("/bin/false"))
        {
            saved = await p.SaveMovieAsync(folder, "walk", MovieFormat.Gif, new MovieEncoder("/bin/false"), TestContext.Current.CancellationToken);
            Assert.Null(saved.Movie);
            Assert.Contains("walk.gif", saved.Problem);
            Assert.Equal(4, saved.Frames.Count);
        }
    }

    [Fact]
    public void Viewport_layouts_have_their_panes_and_remember_their_views()
    {
        Assert.Equal([1, 2, 2, 4], Enum.GetValues<ViewportLayoutKind>().Select(ViewportLayouts.Panes));
        Assert.Equal(["Top", "Perspective", "Front", "Camera"], ViewportLayouts.Defaults(ViewportLayoutKind.Quad));
        Assert.Equal(["Perspective", "Camera"], ViewportLayouts.Defaults(ViewportLayoutKind.SideBySide));
        Assert.Equal(["Top", "Camera"], ViewportLayouts.Defaults(ViewportLayoutKind.Stacked));
        using var bench = new Workbench();
        var path = bench.File("lightwave.json");
        var o = LightWaveOptions.Load(path);
        Assert.Equal(ViewportLayoutKind.Single, ViewportLayouts.LayoutOf(o));
        Assert.Equal(["Perspective"], ViewportLayouts.ViewsOf(o, ViewportLayoutKind.Single));
        o.ViewLayout = "Quad";
        ViewportLayouts.Remember(o, ViewportLayoutKind.Quad, 3, "Side");
        ViewportLayouts.Remember(o, ViewportLayoutKind.Single, 0, "Top");
        o.Save();
        var back = LightWaveOptions.Load(path);
        Assert.Equal(ViewportLayoutKind.Quad, ViewportLayouts.LayoutOf(back));
        Assert.Equal(["Top", "Perspective", "Front", "Side"], ViewportLayouts.ViewsOf(back, ViewportLayoutKind.Quad));
        Assert.Equal(["Top"], ViewportLayouts.ViewsOf(back, ViewportLayoutKind.Single));
        // Nonsense in the file: the defaults.
        back.ViewLayout = "Nine";
        back.LayoutViews["Stacked"] = ["Sideways"];
        Assert.Equal(ViewportLayoutKind.Single, ViewportLayouts.LayoutOf(back));
        Assert.Equal(["Top", "Camera"], ViewportLayouts.ViewsOf(back, ViewportLayoutKind.Stacked));
    }
}
