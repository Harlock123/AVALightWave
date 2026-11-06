using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Rendering;
using AVAModeler.Scene;
using SceneModel = AVAModeler.Scene.Scene;

namespace AVALightWave;

/// <summary>
/// What Render › Make Preview makes: the frames (first to last by step), the picture's size, wireframe or shaded, and
/// what it looks through: the scene's camera (the default, or <see cref="Camera"/>) or a fixed view (<see cref="View"/>,
/// such as one of the window's panes).
/// </summary>
public sealed record PreviewSettings
{
    public int First { get; init; } = 1;
    public int Last { get; init; } = 60;
    /// <summary>Draw every step-th frame.</summary>
    public int Step { get; init; } = 1;
    /// <summary>
    /// The picture's size. Through a scene camera the picture takes the camera's frame shape, the largest that fits in
    /// Width × Height (Height 0: Width across, as tall as the shape makes it). Through a <see cref="View"/>, exactly this.
    /// </summary>
    public int Width { get; init; } = 480;
    public int Height { get; init; }
    /// <summary>Shaded faces with their wireframe (as the shaded view), or only the wireframe.</summary>
    public bool Shaded { get; init; } = true;
    /// <summary>The camera to look through, in the scene being worked on (default: its current camera).</summary>
    public CameraItem? Camera { get; init; }
    /// <summary>A view to look through instead of a camera; it stays where it is for the whole preview.</summary>
    public ViewCamera? View { get; init; }

    /// <summary>The scene frames drawn, in order: first, first + step and so on, up to last.</summary>
    public IReadOnlyList<int> Frames => Last < First || Step < 1 ? [] : Enumerable.Range(0, (Last - First) / Step + 1).Select(i => First + i * Step).ToList();

    /// <summary>What's wrong with these settings, or null if they'll do.</summary>
    public string? Problem()
    {
        if (Last < First) return $"The last frame ({Last}) is before the first ({First}).";
        if (Step < 1) return "The step must be 1 or more.";
        if (Width < 16 || Width > 4096 || Height != 0 && (Height < 16 || Height > 4096)) return "The size must be between 16 and 4096.";
        if (Frames.Count > Preview.MostFrames) return $"A preview holds at most {Preview.MostFrames} frames; use a step, or fewer frames.";
        return null;
    }
}

/// <summary>Where making a preview has got to.</summary>
public readonly record struct PreviewProgress(int Done, int Count, int Frame)
{
    public double Fraction => Count == 0 ? 1 : (double)Done / Count;
    public string Text => $"Frame {Frame} ({Done} of {Count})";
}

/// <summary>What saving a preview wrote, and what went wrong if anything did (null when all went well).</summary>
public sealed record PreviewSaved(IReadOnlyList<string> Frames, string? Movie, string? Problem);

/// <summary>
/// Classic Layout's Make Preview: a quick flipbook of a run of frames, drawn as the Layout view draws them (the
/// engine's <see cref="SoftwareRenderer"/> drawing <see cref="SceneDrawing.Build"/>), not ray traced. It works on a
/// <see cref="SceneModel.Copy"/> (documents copied too) taken when it's made, on a worker thread, so the scene can be
/// edited meanwhile; it can be cancelled. Frames are kept in memory as pixels (0xAARRGGBB, as the renderer draws them),
/// at most <see cref="MostBytes"/> in all: a bigger preview is made smaller to fit (<see cref="Note"/> says so), or,
/// below <see cref="SmallestWidth"/> across, refused. Saved as numbered PNGs (named as the engine's animation renders
/// name theirs) and, through the engine's <see cref="MovieEncoder"/> (ffmpeg), as an MP4 or GIF. No UI.
/// </summary>
public sealed class Preview
{
    /// <summary>The most memory a preview's frames may take (512 MiB).</summary>
    public const long MostBytes = 512L * 1024 * 1024;
    /// <summary>A preview is never made smaller than this across to fit in memory; it's refused instead.</summary>
    public const int SmallestWidth = 160;
    /// <summary>The most frames a preview holds.</summary>
    public const int MostFrames = 10000;

    private readonly SceneModel _copy;
    private readonly CameraItem? _camera;
    private readonly ViewCamera? _view;
    private readonly List<uint[]> _images = [];
    private readonly Lock _lock = new();

    public PreviewSettings Settings { get; }
    /// <summary>The scene frames, in order.</summary>
    public IReadOnlyList<int> Frames { get; }
    /// <summary>The picture's size (the camera's frame shape, and made smaller to fit in memory if need be).</summary>
    public int Width { get; }
    public int Height { get; }
    /// <summary>The scene's frame rate: frame f shows at (f − first) / fps seconds.</summary>
    public double FramesPerSecond { get; }
    /// <summary>What it looks through ("Camera" or a view's name).</summary>
    public string LooksThrough { get; }
    /// <summary>Why the size isn't what was asked for, or null.</summary>
    public string? Note { get; }

    /// <summary>Copies the scene (on the thread that owns it) and works out the size; throws ArgumentException for bad settings.</summary>
    public Preview(SceneModel scene, PreviewSettings settings)
    {
        if (settings.Problem() is { } problem) throw new ArgumentException(problem);
        Settings = settings;
        Frames = settings.Frames;
        FramesPerSecond = scene.FramesPerSecond > 0 ? scene.FramesPerSecond : 30;
        int width = settings.Width, height = settings.Height;
        if (settings.View is { } view)
        {
            if (height == 0) height = Math.Max(1, (int)Math.Round(width * (double)view.Height / Math.Max(1, view.Width)));
            LooksThrough = view.Title;
        }
        else
        {
            var camera = settings.Camera ?? scene.CurrentCamera ?? throw new InvalidOperationException("The scene has no camera to look through. Add one, or make the preview through a view.");
            if (scene.Items.ToList().IndexOf(camera) < 0) throw new ArgumentException($"The camera {camera.Name} isn't in this scene.");
            (width, height) = FrameSize(camera.FrameAspect, width, height);
            LooksThrough = $"Camera: {camera.Name}";
        }
        // Too big to keep: smaller, keeping the shape, down to SmallestWidth.
        long bytes = 4L * width * height * Frames.Count;
        if (bytes > MostBytes)
        {
            double k = Math.Sqrt((double)MostBytes / bytes);
            int w = (int)Math.Floor(width * k), h = (int)Math.Floor(height * k);
            if (w < SmallestWidth || h < 1)
                throw new ArgumentException($"{Frames.Count} frames won't fit in memory (at most {MostBytes / (1024 * 1024)} MB) even at {SmallestWidth} pixels across; use a step, or fewer frames.");
            Note = $"Made at {w} × {h} (not {width} × {height}) to keep {Frames.Count} frames under {MostBytes / (1024 * 1024)} MB.";
            (width, height) = (w, h);
        }
        Width = width;
        Height = height;
        _copy = scene.Copy(d => d.Copy());
        if (settings.View is { } v)
        {
            // A copy of the view, scaled so the picture shows what the view shows.
            _view = Fixed(v, width, height);
        }
        else
        {
            var camera = settings.Camera ?? scene.CurrentCamera!;
            _camera = (CameraItem)_copy.Items[scene.Items.ToList().IndexOf(camera)];
        }
    }

    /// <summary>The largest picture of a camera's frame shape in width × height (height 0: width across).</summary>
    public static (int Width, int Height) FrameSize(double aspect, int width, int height)
    {
        if (aspect <= 0 || double.IsNaN(aspect)) aspect = 4.0 / 3;
        if (height <= 0) return (width, Math.Max(1, (int)Math.Round(width / aspect)));
        return width / (double)height > aspect
            ? (Math.Max(1, (int)Math.Round(height * aspect)), height)
            : (width, Math.Max(1, (int)Math.Round(width / aspect)));
    }

    private static ViewCamera Fixed(ViewCamera v, int width, int height)
    {
        var c = v.Clone();
        c.Bank = v.Bank;
        // A flat view keeps the same stretch of world across its height; a perspective view keeps its field of view.
        if (!c.IsPerspective) c.PixelsPerUnit = v.PixelsPerUnit * height / Math.Max(1, v.Height);
        c.Resize(width, height);
        return c;
    }

    /// <summary>The frames made so far.</summary>
    public int Count { get { lock (_lock) return _images.Count; } }

    /// <summary>The i-th frame's pixels (0xAARRGGBB, top row first).</summary>
    public uint[] Image(int index) { lock (_lock) return _images[index]; }

    /// <summary>When the i-th frame shows, from the first, at the scene's frame rate.</summary>
    public TimeSpan TimeOf(int index) => TimeSpan.FromSeconds((Frames[index] - Frames[0]) / FramesPerSecond);

    /// <summary>How long a frame stays on screen when played (the step's worth of the scene's frames).</summary>
    public TimeSpan FrameDuration => TimeSpan.FromSeconds(Settings.Step / FramesPerSecond);

    /// <summary>Whether every frame is made.</summary>
    public bool Complete => Count == Frames.Count;

    /// <summary>
    /// Draws every frame on a worker thread, keeping each as it's done. Cancelling stops between frames and keeps those
    /// done; the result is how many there are.
    /// </summary>
    public Task<int> MakeAsync(IProgress<PreviewProgress>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Make(progress, cancel), CancellationToken.None);

    private int Make(IProgress<PreviewProgress>? progress, CancellationToken cancel)
    {
        using var renderer = new SoftwareRenderer();
        var view = _view ?? new ViewCamera(ViewKind.Perspective);
        view.Resize(Width, Height);
        for (int i = Count; i < Frames.Count; i++)
        {
            if (cancel.IsCancellationRequested) break;
            int frame = Frames[i];
            if (_camera != null) SceneDrawing.LookThrough(view, _camera, frame);
            // As the view draws it, without the chosen item's yellow box.
            renderer.Render(SceneDrawing.Build(_copy.Evaluate(frame), _copy.Items, Settings.Shaded, selected: null), view);
            var pixels = (uint[])renderer.Pixels.Clone();
            lock (_lock) _images.Add(pixels);
            progress?.Report(new PreviewProgress(i + 1, Frames.Count, frame));
        }
        return Count;
    }

    /// <summary>A frame's pixels as RGBA bytes, for the PNG writer.</summary>
    public static byte[] Rgba(uint[] pixels)
    {
        var rgba = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            uint p = pixels[i];
            rgba[i * 4] = (byte)(p >> 16);
            rgba[i * 4 + 1] = (byte)(p >> 8);
            rgba[i * 4 + 2] = (byte)p;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    /// <summary>
    /// The name of a PNG sequence or movie the person chose: a file's name without its extension (and without a frame
    /// number, if they picked one of the frames), else "preview".
    /// </summary>
    public static string NameFrom(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('%')) name = "preview";
        return name;
    }

    /// <summary>Saves the frames made as name0001.png, name0002.png… in a folder (made if needed); returns their paths.</summary>
    public IReadOnlyList<string> SavePngs(string folder, string name = "preview")
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>();
        for (int i = 0; i < Count; i++)
        {
            var path = Path.Combine(folder, AnimationRender.FileName(name, i + 1));
            Png.Save(path, Width, Height, Rgba(Image(i)));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Saves the frames as PNGs in a folder and makes a movie of them there (name.mp4 or name.gif) with the engine's
    /// encoder, at the scene's frame rate over the step. Without ffmpeg the PNGs are still written, and the result says why
    /// there's no movie. Never throws for the encoder's failures.
    /// </summary>
    public async Task<PreviewSaved> SaveMovieAsync(string folder, string name, MovieFormat format, MovieEncoder encoder, CancellationToken cancel = default)
    {
        IReadOnlyList<string> frames;
        try { frames = await Task.Run(() => SavePngs(folder, name), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new PreviewSaved([], null, $"Can't save the frames: {e.Message}"); }
        if (frames.Count == 0) return new PreviewSaved(frames, null, "There are no frames to make a movie of.");
        var result = await encoder.EncodeAsync(folder, name, FramesPerSecond / Settings.Step, frames.Count, Width, Height, [format], name, cancel).ConfigureAwait(false);
        return new PreviewSaved(frames, result.Files.FirstOrDefault(), result.Problem);
    }

}
