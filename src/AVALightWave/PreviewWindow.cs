using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVAModeler.Layout;
using AVAModeler.Modeler.UI;
using AVAModeler.Rendering;

namespace AVALightWave;

/// <summary>What Save in the Preview window writes.</summary>
public enum PreviewOutput { Pngs, Mp4, Gif }

/// <summary>
/// Render › Make Preview's window (classic Layout's preview): the frames, step, size, shading and what to look through
/// across the top; the flipbook being made (with its progress) and then played at the scene's frame rate, with Play and
/// Stop, Loop, a frame back and on, and a slider; and Save as numbered PNGs, an MP4 or a GIF. The work is the core's
/// <see cref="Preview"/>, on a copy of the scene, so the person can keep working. Closing the window stops a preview
/// being made.
/// </summary>
public sealed class PreviewWindow : Window
{
    private readonly LayoutSession _session;
    private readonly Func<ViewCamera?> _activeView;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 4) };
    private readonly TextBlock _status = new() { Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Name = "PreviewStatus" };
    private readonly TextBlock _frameLabel = new() { Name = "PreviewFrame", Foreground = Look.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120, Margin = new Thickness(8, 0) };
    private readonly TextBox _first, _last, _step, _width;
    private readonly CheckBox _shaded = new() { Content = "Shaded", Foreground = Look.Text, Name = "PreviewShaded" };
    private readonly CheckBox _loop = new() { Content = "Loop", IsChecked = true, Foreground = Look.Text, Name = "PreviewLoop" };
    private readonly ComboBox _through = new() { ItemsSource = new[] { "Scene camera", "Active view" }, Width = 130, Name = "PreviewThrough" };
    private readonly Slider _slider = new() { Minimum = 0, Maximum = 0, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1, Name = "PreviewSlider" };
    private readonly Button _play, _stopMaking;
    private readonly List<Button> _needFrames = [];
    private readonly DispatcherTimer _timer = new();
    private WriteableBitmap? _bitmap;
    private CancellationTokenSource? _cancel;
    private bool _sliding;

    /// <summary>The preview shown (being made, or made).</summary>
    public Preview? Preview { get; private set; }

    /// <summary>Making the preview: done when every frame is made, or it was stopped.</summary>
    public Task Making { get; private set; } = Task.CompletedTask;

    /// <summary>The frame shown, counted from 0 in the preview's frames.</summary>
    public int Index { get; private set; }

    public bool Playing => _timer.IsEnabled;

    /// <summary>How long each frame shows when playing: the step's worth of the scene's frames at its frame rate.</summary>
    public TimeSpan Interval => _timer.Interval;

    /// <summary>Playing starts over at the end (else it stops on the last frame).</summary>
    public bool Loop { get => _loop.IsChecked == true; set => _loop.IsChecked = value; }

    /// <summary>Play as soon as the preview is made (the window's; tests turn it off).</summary>
    public bool PlayWhenMade { get; set; } = true;

    /// <summary>What makes the movies: the ffmpeg on the PATH, found when first needed, unless a test gives another.</summary>
    public MovieEncoder? Movies { get; set; }

    /// <summary>Asks where to save (title, suggested name, save?); tests give their own. Null: the save dialog.</summary>
    public Func<string, string?, bool, Task<string?>>? PickFile { get; set; }

    public string Status => _status.Text ?? "";

    public PreviewWindow(LayoutSession session, PreviewSettings settings, Func<ViewCamera?> activeView, Func<string, string?, bool, Task<string?>>? pickFile = null)
    {
        _session = session;
        _activeView = activeView;
        PickFile = pickFile;
        Title = "Preview";
        Width = 760;
        Height = 640;
        MinWidth = 520;
        MinHeight = 380;
        Background = Look.Panel;
        _first = Box(settings.First, "PreviewFirst");
        _last = Box(settings.Last, "PreviewLast");
        _step = Box(settings.Step, "PreviewStep");
        _width = Box(settings.Width, "PreviewWidth");
        _shaded.IsChecked = settings.Shaded;
        _through.SelectedIndex = settings.View != null || session.Scene.CurrentCamera == null ? 1 : 0;
        ToolTip.SetTip(_through, "Look through the scene's camera, or the window's active view as it is now");

        var make = Look.MakeButton("Make", () => Make(), "Make the preview again with these settings");
        _stopMaking = Look.MakeButton("Stop", StopMaking, "Stop making the preview (the frames made so far stay)");
        _stopMaking.IsEnabled = false;
        _play = Look.MakeButton("Play", () => { if (Playing) Stop(); else Play(); }, "Play / stop at the scene's frame rate", 60);
        _play.Name = "PreviewPlay";
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new[]
        {
            Look.MakeButton("|◀", () => Show(0), "The first frame"), Look.MakeButton("◀", () => StepBy(-1), "Back a frame"), _play,
            Look.MakeButton("▶", () => StepBy(1), "On a frame"), Look.MakeButton("▶|", () => Show(int.MaxValue), "The last frame"),
        })
        {
            transport.Children.Add(b);
            _needFrames.Add(b);
        }
        var save = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (output, label, tip) in new[]
        {
            (PreviewOutput.Pngs, "Save PNGs…", "Save the frames as numbered PNGs (name0001.png…)"),
            (PreviewOutput.Mp4, "Save MP4…", "Save an MP4 movie (needs ffmpeg; the PNG frames are saved beside it)"),
            (PreviewOutput.Gif, "Save GIF…", "Save a looping GIF (needs ffmpeg; the PNG frames are saved beside it)"),
        })
        {
            var b = Look.MakeButton(label, () => _ = Save(output), tip, 100);
            save.Children.Add(b);
            _needFrames.Add(b);
        }
        _slider.ValueChanged += (_, _) => { if (!_sliding) Show((int)Math.Round(_slider.Value)); };
        _timer.Tick += (_, _) => Tick();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _cancel?.Cancel();
        };

        var settingsRow = Row(Label("Frames"), _first, Label("to"), _last, Label("step"), _step, Label("Width"), _width, _shaded, Label("Through"), _through, make, _stopMaking);
        var player = new DockPanel { Margin = new Thickness(0, 4) };
        DockPanel.SetDock(transport, Dock.Left);
        DockPanel.SetDock(_loop, Dock.Left);
        DockPanel.SetDock(_frameLabel, Dock.Right);
        _loop.Margin = new Thickness(8, 0);
        player.Children.Add(transport);
        player.Children.Add(_loop);
        player.Children.Add(_frameLabel);
        player.Children.Add(_slider);
        var bottom = new StackPanel { Children = { _progress, player, save, _status } };
        Content = new DockPanel
        {
            Margin = new Thickness(10),
            Children = { Docked(settingsRow, Dock.Top), Docked(bottom, Dock.Bottom), new Border { Background = Brushes.Black, Child = _image, Margin = new Thickness(0, 8, 0, 4) } },
        };
        UpdateControls();
    }

    private static TextBox Box(int value, string name) => new() { Text = value.ToString(CultureInfo.InvariantCulture), Width = 64, Name = name };

    private static TextBlock Label(string t) => new() { Text = t, Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) };

    private static WrapPanel Row(params Control[] controls)
    {
        var row = new WrapPanel();
        foreach (var c in controls) { c.Margin = c.Margin == default ? new Thickness(2, 0) : c.Margin; row.Children.Add(c); }
        return row;
    }

    private static Control Docked(Control c, Dock d)
    {
        DockPanel.SetDock(c, d);
        return c;
    }

    private string Say(string text)
    {
        _status.Text = text;
        return text;
    }

    /// <summary>The settings as filled in, or null (with the reason in the status line) if they won't do.</summary>
    public PreviewSettings? ReadSettings()
    {
        static bool Int(TextBox b, out int v) => int.TryParse(b.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (!Int(_first, out int first) || !Int(_last, out int last) || !Int(_step, out int step) || !Int(_width, out int width))
        {
            Say("The frames, step and width must be whole numbers.");
            return null;
        }
        var settings = new PreviewSettings
        {
            First = first, Last = last, Step = step, Width = width, Shaded = _shaded.IsChecked == true,
            View = _through.SelectedIndex == 1 ? _activeView() : null,
        };
        if (settings.Problem() is { } problem)
        {
            Say(problem);
            return null;
        }
        return settings;
    }

    /// <summary>Makes the preview with the settings shown (stopping one being made); returns the making.</summary>
    public Task Make()
    {
        if (ReadSettings() is not { } settings) return Task.CompletedTask;
        StopMaking();
        Stop();
        Preview p;
        try { p = new Preview(_session.Scene, settings); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Say(e.Message);
            return Task.CompletedTask;
        }
        Preview = p;
        Index = 0;
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(p.Width, p.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _image.Source = null;
        _progress.Value = 0;
        Say($"Making {p.Frames.Count} frames at {p.Width} × {p.Height}, {p.LooksThrough}…");
        var cancel = _cancel = new CancellationTokenSource();
        Making = Run(p, cancel);
        UpdateControls();
        return Making;
    }

    private async Task Run(Preview p, CancellationTokenSource cancel)
    {
        var clock = Stopwatch.StartNew();
        bool finished = false;
        var progress = new Progress<PreviewProgress>(r =>
        {
            if (Preview != p || finished) return;
            _progress.Value = r.Fraction;
            Say($"Making the preview: {r.Text}…");
            if (!Playing) Show(r.Done - 1);
        });
        int made = await p.MakeAsync(progress, cancel.Token);
        finished = true;
        if (_cancel == cancel) _cancel = null;
        cancel.Dispose();
        if (Preview != p) return;
        _progress.Value = 1;
        string note = p.Note is { } n ? " " + n : "";
        Say(made == p.Frames.Count
            ? $"{made} frames at {p.Width} × {p.Height} ({p.LooksThrough}) in {AnimationRender.Duration(clock.Elapsed)}, playing at {p.FramesPerSecond / p.Settings.Step:0.##} frames a second.{note}"
            : $"Stopped after {made} of {p.Frames.Count} frames.{note}");
        UpdateControls();
        Show(0);
        if (PlayWhenMade && made > 1) Play();
    }

    /// <summary>Stops making the preview; the frames made so far stay.</summary>
    public void StopMaking() => _cancel?.Cancel();

    private void UpdateControls()
    {
        int count = Preview?.Count ?? 0;
        bool making = !Making.IsCompleted;
        _stopMaking.IsEnabled = making;
        foreach (var b in _needFrames) b.IsEnabled = count > 0 && !making;
        _sliding = true;
        _slider.Maximum = Math.Max(0, count - 1);
        _slider.Value = Index;
        _sliding = false;
    }

    /// <summary>Shows a frame of those made (clamped to them).</summary>
    public void Show(int index)
    {
        if (Preview is not { } p || p.Count == 0) return;
        Index = Math.Clamp(index, 0, p.Count - 1);
        if (_bitmap != null)
        {
            var pixels = p.Image(Index);
            using (var fb = _bitmap.Lock())
                for (int y = 0; y < p.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy((int[])(object)pixels, y * p.Width, fb.Address + y * fb.RowBytes, p.Width);
            // A new source each time, so the image redraws.
            _image.Source = null;
            _image.Source = _bitmap;
        }
        _frameLabel.Text = $"Frame {p.Frames[Index]}  ({Index + 1} of {p.Count})";
        _sliding = true;
        if (_slider.Maximum < p.Count - 1) _slider.Maximum = p.Count - 1;
        _slider.Value = Index;
        _sliding = false;
    }

    /// <summary>A frame back or on; past either end it wraps round with Loop on, else stops there.</summary>
    public void StepBy(int frames)
    {
        if (Preview is not { Count: > 0 } p) return;
        int next = Index + frames;
        if (next < 0 || next >= p.Count) next = Loop ? (next % p.Count + p.Count) % p.Count : Math.Clamp(next, 0, p.Count - 1);
        Show(next);
    }

    /// <summary>Plays from the frame shown (from the first, if the last is shown and Loop is off) at the scene's frame rate.</summary>
    public void Play()
    {
        if (Preview is not { Count: > 1 } p || !Making.IsCompleted) return;
        if (!Loop && Index >= p.Count - 1) Show(0);
        _timer.Interval = p.FrameDuration > TimeSpan.Zero ? p.FrameDuration : TimeSpan.FromMilliseconds(33);
        _timer.Start();
        _play.Content = "Stop";
    }

    public void Stop()
    {
        _timer.Stop();
        _play.Content = "Play";
    }

    /// <summary>The player's tick: the next frame, wrapping round with Loop on, else stopping on the last.</summary>
    internal void Tick()
    {
        if (Preview is not { Count: > 0 } p) { Stop(); return; }
        if (Index + 1 >= p.Count && !Loop)
        {
            Stop();
            return;
        }
        Show((Index + 1) % p.Count);
    }

    /// <summary>Saves the frames made as PNGs, an MP4 or a GIF, asking where; returns what it says in the status line.</summary>
    public async Task<string> Save(PreviewOutput output)
    {
        if (Preview is not { Count: > 0 } p) return Say("Make a preview first.");
        if (!Making.IsCompleted) return Say("Wait for the preview to be made (or stop it) before saving.");
        Stop();
        var (ext, title, type) = output switch
        {
            PreviewOutput.Mp4 => (".mp4", "Save the preview as an MP4", new FilePickerFileType("MP4 movie") { Patterns = ["*.mp4"] }),
            PreviewOutput.Gif => (".gif", "Save the preview as a GIF", new FilePickerFileType("GIF") { Patterns = ["*.gif"] }),
            _ => (".png", "Save the preview's frames (numbered PNGs)", new FilePickerFileType("PNG frames") { Patterns = ["*.png"] }),
        };
        var path = PickFile != null ? await PickFile(title, "preview" + ext, true) : await Dialogs.Save(this, title, "preview" + ext, type);
        if (path == null) return Status;
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!, name = Preview.NameFrom(path);
        if (output == PreviewOutput.Pngs)
        {
            try
            {
                var files = await Task.Run(() => p.SavePngs(folder, name));
                return Say($"Saved {files.Count} frames as {AnimationRender.FileName(name, 1)} onwards in {folder}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Say($"Can't save the frames: {e.Message}");
            }
        }
        Movies ??= MovieEncoder.FromPath();
        Say("Saving…");
        var saved = await p.SaveMovieAsync(folder, name, output == PreviewOutput.Mp4 ? MovieFormat.Mp4 : MovieFormat.Gif, Movies);
        if (saved.Movie != null) return Say($"Saved {Path.GetFileName(saved.Movie)} in {folder} (its {saved.Frames.Count} PNG frames beside it).");
        return Say(saved.Problem == MovieEncoder.NotInstalled || saved.Frames.Count == 0 ? saved.Problem ?? "" : $"{saved.Problem} The {saved.Frames.Count} PNG frames are saved.");
    }
}
