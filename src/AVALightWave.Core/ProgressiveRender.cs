using System.Diagnostics;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>One pass of a progressive render: its size as a share of the full picture, its rays a pixel, and whether it's denoised.</summary>
public sealed record ProgressiveStep(double Scale, int Samples, bool Denoise);

/// <summary>What a progressive render has drawn so far: its latest picture and how far along it is (or why it couldn't).</summary>
public sealed record ProgressiveFrame(RenderImage? Image, int Step, int Steps, ProgressiveStep? Pass, TimeSpan Elapsed, long Generation, string? Error = null)
{
    public bool Done => Error != null || Step == Steps;

    /// <summary>The counter a view shows: "pass 3 of 5 · 4 rays · 1.2 s", "done", or the error.</summary>
    public string Counter => Error ?? (Pass == null ? "starting…"
        : $"{(Done ? "done" : $"pass {Step} of {Steps}")} · {Pass.Samples} {(Pass.Samples == 1 ? "ray" : "rays")}{(Pass.Scale < 1 ? $" · 1/{Math.Round(1 / Pass.Scale)} size" : "")}{(Pass.Denoise ? " · denoised" : "")} · {Elapsed.TotalSeconds:0.0} s");
}

/// <summary>
/// A progressive ray-traced view: the scene camera's picture rendered on a worker thread below normal priority, first
/// small and rough, then at full size with more and more rays (denoised), each pass reported as it finishes. Start it
/// again with a fresh <see cref="Scene.Copy"/> whenever the scene changes; the passes of the last start are the only
/// ones reported, and <see cref="Stop"/> ends it. No UI: a view shows <see cref="Updated"/>'s pictures.
/// </summary>
public sealed class ProgressiveRender : IDisposable
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cancel;
    private long _generation;

    /// <summary>Each pass's picture, from the worker thread (marshal it to the UI yourself). Only the latest start's.</summary>
    public event Action<ProgressiveFrame>? Updated;

    /// <summary>The most threads a pass uses: all but one, so the window stays quick.</summary>
    public int MaxThreads { get; set; } = Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>The latest start's number (each <see cref="Start"/> adds one).</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// The passes up to <paramref name="samples"/> rays a pixel: a quarter size and a half size with one ray, then full
    /// size with 1, 4, 16… rays (denoised, if asked), the last with exactly <paramref name="samples"/>.
    /// </summary>
    public static IReadOnlyList<ProgressiveStep> Steps(int samples, bool denoise = true)
    {
        samples = Math.Clamp(samples, 1, 256);
        var steps = new List<ProgressiveStep> { new(0.25, 1, false), new(0.5, 1, false), new(1, 1, denoise) };
        for (int s = 4; s < samples; s *= 4) steps.Add(new ProgressiveStep(1, s, denoise));
        if (samples > 1) steps.Add(new ProgressiveStep(1, samples, denoise));
        return steps;
    }

    /// <summary>
    /// Starts rendering <paramref name="scene"/> (a copy the render owns: nothing else may change it) at a frame through
    /// its current camera, at <paramref name="width"/> × <paramref name="height"/>, stopping any render before it.
    /// </summary>
    public long Start(Scene scene, double frame, int width, int height, int samples, bool denoise = true)
    {
        CancellationTokenSource cancel;
        long generation;
        lock (_lock)
        {
            _cancel?.Cancel();
            _cancel = cancel = new CancellationTokenSource();
            generation = Interlocked.Increment(ref _generation);
        }
        var steps = Steps(samples, denoise);
        int threads = MaxThreads;
        var worker = new Thread(() => Run(scene, frame, Math.Max(1, width), Math.Max(1, height), steps, threads, generation, cancel.Token))
        {
            IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Progressive render",
        };
        worker.Start();
        return generation;
    }

    /// <summary>Stops the render under way (its pass ends at the next row) and reports nothing more.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cancel?.Cancel();
            _cancel = null;
            Interlocked.Increment(ref _generation);
        }
    }

    private void Run(Scene scene, double frame, int width, int height, IReadOnlyList<ProgressiveStep> steps, int threads, long generation, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            if (scene.CurrentCamera == null)
            {
                Report(new ProgressiveFrame(null, 0, steps.Count, null, clock.Elapsed, generation, "The scene has no camera to render through."));
                return;
            }
            for (int i = 0; i < steps.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var step = steps[i];
                var settings = new RenderSettings
                {
                    Width = Math.Max(1, (int)Math.Round(width * step.Scale)), Height = Math.Max(1, (int)Math.Round(height * step.Scale)),
                    SamplesPerPixel = step.Samples, Denoise = step.Denoise, MaxThreads = threads,
                };
                var image = RayTracer.Render(scene, frame, settings, cancel: cancel);
                Report(new ProgressiveFrame(image, i + 1, steps.Count, step, clock.Elapsed, generation));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            Report(new ProgressiveFrame(null, 0, steps.Count, null, clock.Elapsed, generation, $"Can't render: {e.Message}"));
        }
    }

    private void Report(ProgressiveFrame f)
    {
        if (f.Generation != Generation) return;
        Updated?.Invoke(f);
    }

    public void Dispose() => Stop();
}
