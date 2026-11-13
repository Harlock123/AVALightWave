using System.Collections.Concurrent;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>The progressive render: small and rough first, then full size with more rays, and only the latest start reported.</summary>
public class ProgressiveRenderTests
{
    private static Scene Stage()
    {
        var d = new SceneDocument();
        d.Session.Edit("Add", () => d.Scene.AddObject("Box", Docs.Box()));
        return d.Scene.Copy();
    }

    [Fact]
    public void Passes_grow_to_full_size_and_the_rays_asked_for()
    {
        Assert.Equal([0.25, 0.5, 1, 1, 1], ProgressiveRender.Steps(16).Select(s => s.Scale));
        Assert.Equal([1, 1, 1, 4, 16], ProgressiveRender.Steps(16).Select(s => s.Samples));
        Assert.Equal([1, 1, 1, 4, 10], ProgressiveRender.Steps(10).Select(s => s.Samples));
        Assert.Equal(3, ProgressiveRender.Steps(1).Count);
        Assert.All(ProgressiveRender.Steps(16, denoise: false), s => Assert.False(s.Denoise));

        using var r = new ProgressiveRender { MaxThreads = 2 };
        var frames = new ConcurrentQueue<ProgressiveFrame>();
        using var done = new ManualResetEventSlim();
        r.Updated += f => { frames.Enqueue(f); if (f.Done) done.Set(); };
        long g = r.Start(Stage(), 1, 80, 60, 4);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), "It finishes.");
        var list = frames.ToList();
        Assert.Equal([1, 2, 3, 4], list.Select(f => f.Step));
        Assert.All(list, f => Assert.Equal(g, f.Generation));
        Assert.Equal((20, 15), (list[0].Image!.Width, list[0].Image!.Height));
        Assert.Equal((80, 60), (list[^1].Image!.Width, list[^1].Image!.Height));
        Assert.StartsWith("done · 4 rays · denoised", list[^1].Counter);
        Assert.StartsWith("pass 1 of 4 · 1 ray · 1/4 size", list[0].Counter);
    }

    [Fact]
    public void Starting_again_or_stopping_reports_nothing_more_of_the_old_render()
    {
        using var r = new ProgressiveRender { MaxThreads = 1 };
        var frames = new ConcurrentQueue<ProgressiveFrame>();
        using var done = new ManualResetEventSlim();
        r.Updated += f => { frames.Enqueue(f); if (f.Done) done.Set(); };
        long first = r.Start(Stage(), 1, 320, 240, 64);
        long second = r.Start(Stage(), 1, 40, 30, 1);
        Assert.True(second > first);
        Assert.True(done.Wait(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.All(frames, f => Assert.Equal(second, f.Generation));

        frames.Clear();
        r.Start(Stage(), 1, 320, 240, 64);
        r.Stop();
        Thread.Sleep(300);
        Assert.DoesNotContain(frames, f => f.Step > 1);

        // No camera: it says so.
        var empty = new Scene();
        done.Reset();
        frames.Clear();
        r.Start(empty, 1, 40, 30, 1);
        Assert.True(done.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains("no camera", frames.Single().Counter);
    }
}
