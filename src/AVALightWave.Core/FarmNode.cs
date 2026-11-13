using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>How a render node works.</summary>
public sealed record FarmNodeOptions
{
    /// <summary>Render until nothing is left to claim, then stop (for scripts and tests); otherwise keep watching the folder.</summary>
    public bool Once { get; init; }
    /// <summary>How long to wait between looks at the folder when there's nothing to do.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>The node's name in the queue (default: the machine's).</summary>
    public string? Name { get; init; }
    /// <summary>The most threads the ray tracer uses; 0 for as many as the machine has.</summary>
    public int Threads { get; init; }
    /// <summary>A claim this old, from a node not heard from for as long, is taken over (the node died).</summary>
    public TimeSpan StaleAfter { get; init; } = FarmFolder.DefaultStaleAfter;
    /// <summary>How often the node writes its heartbeat (and looks for its job being cancelled).</summary>
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Whether this node makes a job's movie when it finishes its last frame (with ffmpeg, if it has it).</summary>
    public bool Movies { get; init; } = true;
    /// <summary>What makes movies (default: the ffmpeg on the PATH).</summary>
    public MovieEncoder? Encoder { get; init; }
    /// <summary>A line for each frame rendered and each thing done.</summary>
    public Action<string>? Log { get; init; }
    /// <summary>A line for each thing that went wrong.</summary>
    public Action<string>? Error { get; init; }
}

/// <summary>What a node did: frames rendered and frames that failed.</summary>
public sealed record FarmNodeResult(int Rendered, int Failed);

/// <summary>A job refused by a node, with the reason (written to the job's errors folder).</summary>
public sealed class FarmRefusedException(string message) : Exception(message);

/// <summary>
/// A render node: it works a farm folder's jobs in the queue's order, claiming one frame at a time with a file made by
/// <see cref="FileMode.CreateNew"/>, rendering it with the engine's ray tracer, and writing the PNG and then the frame's
/// done file. It reads only what's inside each job's folder (<see cref="FarmJobLoader"/>) and runs nothing from it.
/// <para>
/// Claims and races: a claim is made with CreateNew, so of two nodes one wins. After a claim the node looks for the
/// frame's done file once more (the node before it writes done, then deletes its claim, so a claim made in between
/// finds done and steps back). A claim older than <see cref="FarmNodeOptions.StaleAfter"/> whose node's heartbeat is as
/// old (or missing) is taken over: the taker renames the claim aside (one rename wins; the losers find it gone), deletes
/// it and makes its own with CreateNew (which can still lose to a third node, which then has the frame). A node thought
/// dead that comes back may render a frame twice; rendering is deterministic and the PNG is renamed into place, so the
/// frame is the same either way.
/// </para>
/// </summary>
public sealed class FarmNode
{
    private readonly FarmNodeOptions _options;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly Lock _lock = new();
    private (string Id, DateTime Written, Scene Scene)? _loaded;
    private CancellationTokenSource? _frameCancel;
    private string? _job, _jobFolder;
    private int? _frame;
    private int _rendered, _failed;
    private bool _stopped;

    public FarmNode(string folder, FarmNodeOptions? options = null)
    {
        _options = options ?? new FarmNodeOptions();
        Folder = new FarmFolder(folder);
        Name = string.IsNullOrWhiteSpace(_options.Name) ? Environment.MachineName : _options.Name.Trim();
        string Part(string s) => new(s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(32).ToArray());
        Id = $"{Part(Name)}-{Part(Environment.MachineName)}-{Environment.ProcessId}-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(2))}";
    }

    public FarmFolder Folder { get; }
    public string Id { get; }
    public string Name { get; }
    public int Rendered => _rendered;
    public int Failed => _failed;

    /// <summary>Runs a node on a folder until cancelled (or, with <see cref="FarmNodeOptions.Once"/>, until nothing is left).</summary>
    public static Task<FarmNodeResult> RunAsync(string folder, FarmNodeOptions options, CancellationToken cancel = default) =>
        new FarmNode(folder, options).RunAsync(cancel);

    /// <summary>Works the folder on a worker thread until cancelled (or, with Once, until nothing is left to claim).</summary>
    public Task<FarmNodeResult> RunAsync(CancellationToken cancel = default) => Task.Run(() => Run(cancel), CancellationToken.None);

    private FarmNodeResult Run(CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder.JobsFolder);
        Directory.CreateDirectory(Folder.NodesFolder);
        using var beat = new Timer(_ => Beat(), null, TimeSpan.Zero, _options.Heartbeat);
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                bool worked = false;
                try { worked = WorkOne(cancel); }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { break; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The folder went away for a moment (a share dropped): look again later.
                    _options.Error?.Invoke($"The farm folder couldn't be read: {e.Message}");
                }
                if (worked) continue;
                if (_options.Once) break;
                if (cancel.WaitHandle.WaitOne(_options.Poll)) break;
            }
        }
        finally
        {
            lock (_lock) _stopped = true;
            beat.Dispose();
            try { File.Delete(HeartbeatFile); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new FarmNodeResult(_rendered, _failed);
    }

    private string HeartbeatFile => Path.Combine(Folder.NodesFolder, Id + ".json");

    /// <summary>Writes the heartbeat, and stops the frame under way if its job was cancelled.</summary>
    private void Beat()
    {
        lock (_lock)
        {
            if (_stopped) return;
            if (_jobFolder != null && File.Exists(Path.Combine(_jobFolder, FarmFolder.CancelFile))) _frameCancel?.Cancel();
            var info = new FarmNodeInfo
            {
                Id = Id, Name = Name, Host = Environment.MachineName, Pid = Environment.ProcessId,
                State = _frame != null ? "rendering" : "idle", Job = _job, Frame = _frame,
                Rendered = _rendered, Failed = _failed, Started = _started, Seen = DateTime.UtcNow,
            };
            try { FarmFiles.WriteAtomic(HeartbeatFile, JsonSerializer.Serialize(info, FarmFolder.Json)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Whether a node (by the id in a claim) has written its heartbeat lately.</summary>
    private bool Alive(string? node)
    {
        if (string.IsNullOrEmpty(node) || node.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        return FarmFiles.Age(Path.Combine(Folder.NodesFolder, node + ".json")) is { } age && age < _options.StaleAfter;
    }

    /// <summary>Finds the first frame free to claim, in the queue's order, and renders it. Returns whether it did any work.</summary>
    private bool WorkOne(CancellationToken cancel)
    {
        var jobs = new List<(string Id, FarmJobSpec Spec)>();
        foreach (var id in Folder.JobIds())
        {
            string folder = Folder.JobFolder(id);
            if (File.Exists(Path.Combine(folder, FarmFolder.CancelFile)) || HasErrors(folder)) continue;
            try { jobs.Add((id, Folder.ReadJob(id))); }
            catch (InvalidDataException e) { Refuse(folder, "job", e.Message, id); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        foreach (var (id, spec) in jobs.OrderBy(j => j.Spec.Order).ThenBy(j => j.Id, StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            string folder = Folder.JobFolder(id);
            var done = Names(Path.Combine(folder, "done"), ".done");
            if (spec.Frames.All(f => done.Contains(Key(f))))
            {
                MakeMovie(folder, spec, cancel);
                continue;
            }
            var claims = Names(Path.Combine(folder, "claims"), ".claim");
            foreach (int frame in spec.Frames)
            {
                string key = Key(frame);
                if (done.Contains(key)) continue;
                if (claims.Contains(key) && FarmFiles.Age(ClaimFile(folder, frame)) is { } age && age < _options.StaleAfter) continue;
                if (File.Exists(Path.Combine(folder, FarmFolder.CancelFile))) break;
                if (!Claim(folder, frame)) continue;
                Render(id, folder, spec, frame, cancel);
                return true;
            }
        }
        return false;
    }

    private static string Key(int frame) => frame.ToString(CultureInfo.InvariantCulture);
    private static string ClaimFile(string job, int frame) => Path.Combine(job, "claims", Key(frame) + ".claim");
    private static string DoneFile(string job, int frame) => Path.Combine(job, "done", Key(frame) + ".done");
    private static string ErrorFile(string job, string key) => Path.Combine(job, "errors", key + ".txt");

    private static bool HasErrors(string job)
    {
        string errors = Path.Combine(job, "errors");
        try { return Directory.Exists(errors) && Directory.EnumerateFiles(errors, "*.txt").Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static HashSet<string> Names(string folder, string extension)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*" + extension).Select(Path.GetFileName).Where(n => n!.EndsWith(extension, StringComparison.Ordinal)).Select(n => n![..^extension.Length]).ToHashSet()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private string ClaimText() => JsonSerializer.Serialize(new FarmClaim(Id, Name, Environment.MachineName, Environment.ProcessId, DateTime.UtcNow), FarmFolder.Json);

    /// <summary>Claims a frame (taking over a dead node's claim if need be). Returns whether this node has it.</summary>
    private bool Claim(string job, int frame)
    {
        string claim = ClaimFile(job, frame);
        Directory.CreateDirectory(Path.GetDirectoryName(claim)!);
        if (File.Exists(ErrorFile(job, Key(frame)))) return false;
        if (!FarmFiles.TryCreateNew(claim, ClaimText()))
        {
            // Someone has it. Only a stale claim from a node not heard from is taken over.
            if (FarmFiles.Age(claim) is not { } age || age < _options.StaleAfter) return false;
            var held = FarmFiles.ReadJson<FarmClaim>(claim, 1 << 16);
            if (Alive(held?.Node)) return false;
            string aside = claim + "." + Id + ".stale";
            try { File.Move(claim, aside); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            try { File.Delete(aside); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (!FarmFiles.TryCreateNew(claim, ClaimText())) return false;
            _options.Log?.Invoke($"Took over frame {frame} of job {Path.GetFileName(job)} from {held?.Name ?? "a node"} (not heard from for {age.TotalSeconds:0} s).");
        }
        if (File.Exists(DoneFile(job, frame)))
        {
            // Done by the node before, between our look and our claim.
            TryDelete(claim);
            return false;
        }
        return true;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void Render(string id, string job, FarmJobSpec spec, int frame, CancellationToken cancel)
    {
        using var frameCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        lock (_lock) { _job = id; _jobFolder = job; _frame = frame; _frameCancel = frameCancel; }
        Beat();
        var clock = Stopwatch.StartNew();
        try
        {
            var scene = Scene(id, job, spec);
            var camera = scene.CurrentCamera ?? throw new FarmRefusedException("The scene has no camera to render through.");
            var settings = new RenderSettings
            {
                Width = spec.Width, Height = spec.Height, Camera = camera, SamplesPerPixel = spec.Samples, Denoise = spec.Denoise,
                MaxThreads = _options.Threads > 0 ? _options.Threads : -1,
            };
            var image = RayTracer.Render(scene, frame, settings, null, frameCancel.Token);
            string name = spec.FileOfFrame(frame);
            string output = Path.Combine(job, "out", name);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            FarmFiles.WriteAtomic(output, image.ToPng(alpha: false));
            double seconds = clock.Elapsed.TotalSeconds;
            FarmFiles.WriteAtomic(DoneFile(job, frame), JsonSerializer.Serialize(new FarmDone(Id, Name, Environment.MachineName, "out/" + name, Math.Round(seconds, 3), DateTime.UtcNow), FarmFolder.Json));
            TryDelete(ClaimFile(job, frame));
            Interlocked.Increment(ref _rendered);
            _options.Log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Rendered frame {frame} of \"{spec.Name}\" ({id}) in {seconds:0.0} s: {output}"));
            MakeMovie(job, spec, cancel);
        }
        catch (OperationCanceledException)
        {
            // Stopping, or the job was cancelled: the frame is left for another node (or nobody).
            TryDelete(ClaimFile(job, frame));
            if (cancel.IsCancellationRequested) throw;
            _options.Log?.Invoke($"Stopped frame {frame} of \"{spec.Name}\" ({id}): the job was cancelled.");
        }
        catch (Exception e) when (e is DirectoryNotFoundException && !Directory.Exists(job))
        {
            // The job was removed while it rendered.
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref _failed);
            Refuse(job, Key(frame), e is FarmRefusedException ? e.Message : $"{e.GetType().Name}: {e.Message}", id, frame);
            TryDelete(ClaimFile(job, frame));
        }
        finally
        {
            lock (_lock) { _job = null; _jobFolder = null; _frame = null; _frameCancel = null; }
            Beat();
        }
    }

    /// <summary>Writes why a frame (or the whole job: key "job") can't be rendered; the job is then failed.</summary>
    private void Refuse(string job, string key, string reason, string id, int? frame = null)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(job, "errors"));
            if (!FarmFiles.TryCreateNew(ErrorFile(job, key), reason + Environment.NewLine)) return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        _options.Error?.Invoke(frame is int f ? $"Frame {f} of job {id} failed: {reason}" : $"Job {id} can't be rendered: {reason}");
    }

    /// <summary>The job's scene, loaded once a job (again if its file changes), through <see cref="FarmJobLoader"/>.</summary>
    private Scene Scene(string id, string job, FarmJobSpec spec)
    {
        var written = File.GetLastWriteTimeUtc(Path.Combine(job, spec.Scene.Replace('\\', '/')));
        if (_loaded is { } l && l.Id == id && l.Written == written) return l.Scene;
        var warnings = new List<string>();
        var scene = FarmJobLoader.Load(Folder, id, spec, warnings);
        foreach (var w in warnings) _options.Log?.Invoke($"Job {id}: {w}");
        _loaded = (id, written, scene);
        return scene;
    }

    /// <summary>When every frame is done and the job asks for a movie, the first node here makes it (once).</summary>
    private void MakeMovie(string job, FarmJobSpec spec, CancellationToken cancel)
    {
        if (spec.Movie == "none" || !_options.Movies) return;
        var done = Names(Path.Combine(job, "done"), ".done");
        if (!spec.Frames.All(f => done.Contains(Key(f)))) return;
        if (!FarmFiles.TryCreateNew(Path.Combine(job, "movie.claim"), ClaimText())) return;
        var formats = spec.Movie switch
        {
            "mp4" => new[] { MovieFormat.Mp4 },
            "gif" => [MovieFormat.Gif],
            _ => [MovieFormat.Mp4, MovieFormat.Gif],
        };
        var encoder = _options.Encoder ?? MovieEncoder.FromPath();
        // ffmpeg reads only the frames the farm rendered into out/ (name0001.png onwards), and only because the job asked.
        var result = encoder.EncodeAsync(Path.Combine(job, "out"), spec.Output, spec.Fps, spec.Frames.Count, spec.Width, spec.Height, formats, spec.Output, cancel).GetAwaiter().GetResult();
        if (result.Problem != null)
        {
            FarmFiles.WriteAtomic(Path.Combine(job, "movie.txt"), result.Problem);
            _options.Error?.Invoke($"Job {Path.GetFileName(job)}: {result.Problem}");
        }
        else
        {
            FarmFiles.WriteAtomic(Path.Combine(job, "movie.done"), JsonSerializer.Serialize(result.Files.Select(f => "out/" + Path.GetFileName(f)).ToList(), FarmFolder.Json));
            _options.Log?.Invoke($"Made {string.Join(" and ", result.Files.Select(Path.GetFileName))} for job {Path.GetFileName(job)}.");
        }
    }
}

/// <summary>
/// Loads a job's scene for a node, reading only what's inside the job's folder. Every path the scene names (the scene
/// itself, object files, texture images, the environment picture) is resolved against the job's folder with symbolic
/// links followed (<see cref="FarmFiles.RealPath"/>), and anything leading outside it (an absolute path elsewhere, "..",
/// a link out) refuses the job with the reason. Images are then given to the renderer by their full paths inside the
/// job, so the engine's own image search (beside the object, Images folders, the working folder) is never used; a
/// missing image renders as the engine's checker (a texture) or not at all (the environment), with a warning. Only
/// .avascene scenes are read (a .lws names objects by path and is never a job's scene).
/// </summary>
public static class FarmJobLoader
{
    public static Scene Load(FarmFolder farm, string id, FarmJobSpec spec, List<string> warnings)
    {
        string jobs = FarmFiles.RealPath(farm.JobsFolder);
        string job = FarmFiles.RealPath(farm.JobFolder(id));
        if (!FarmFiles.IsInside(job, jobs) || string.Equals(job, jobs, StringComparison.Ordinal))
            throw new FarmRefusedException("The job's folder is a link out of the farm's jobs folder; a node reads only what's in the job.");
        if (!spec.Scene.EndsWith(SceneJson.Extension, StringComparison.OrdinalIgnoreCase)) throw new FarmRefusedException($"The scene must be an {SceneJson.Extension} file.");
        string scenePath = Inside(job, spec.Scene, "The scene");
        if (!File.Exists(scenePath)) throw new FarmRefusedException($"The scene {spec.Scene} isn't in the job's folder.");
        var refused = new List<string>();
        Document? LoadObject(string path)
        {
            try
            {
                string real = Inside(job, path, "An object file");
                return File.Exists(real) ? SceneDocument.LoadOrNull(real) : null;
            }
            catch (FarmRefusedException e) { refused.Add(e.Message); return null; }
        }
        SceneLoadResult loaded;
        try { loaded = SceneJson.ReadFile(scenePath, LoadObject); }
        catch (Exception e) when (e is JsonException or InvalidDataException or FormatException or NotSupportedException)
        {
            throw new FarmRefusedException($"The scene can't be read: {e.Message}");
        }
        if (refused.Count > 0) throw new FarmRefusedException(refused[0]);
        var scene = loaded.Scene;
        warnings.AddRange(loaded.Warnings);
        var docs = new HashSet<Document>(ReferenceEqualityComparer.Instance);
        foreach (var o in scene.Objects)
        {
            if (o.SourcePath != null) Inside(job, o.SourcePath, $"{o.Name}'s object file");
            if (o.Document is not { } doc || !docs.Add(doc)) continue;
            if (doc.FilePath != null) Inside(job, doc.FilePath, $"{o.Name}'s object file");
            foreach (var s in doc.Surfaces)
            foreach (var t in s.Textures)
            {
                if (!t.IsImage || string.IsNullOrWhiteSpace(t.Image)) continue;
                string real = Inside(job, t.Image, $"{o.Name}'s image");
                if (File.Exists(real)) t.Image = real;
                else
                {
                    warnings.Add($"{o.Name}'s image {t.Image} isn't in the job's folder: a checker stands in for it.");
                    t.Image = "";
                }
            }
        }
        var env = scene.Environment;
        if (env.On && !env.Image.Trim().StartsWith(EnvironmentMap.BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string real = Inside(job, env.Image, "The environment image");
            if (File.Exists(real)) scene.Environment = env with { Image = real };
            else
            {
                warnings.Add($"The environment image {env.Image} isn't in the job's folder: it's left out.");
                scene.Environment = env with { Image = "" };
            }
        }
        if (scene.CurrentCamera == null) throw new FarmRefusedException("The scene has no camera to render through.");
        return scene;
    }

    /// <summary>
    /// A path from the job (relative to its folder, or absolute) as the real path it leads to, which must be inside the
    /// job's folder (<paramref name="job"/>, already real); otherwise the job is refused.
    /// </summary>
    public static string Inside(string job, string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new FarmRefusedException($"{what} has no usable path.");
        string clean = OperatingSystem.IsWindows() ? path : path.Replace('\\', '/');
        string real;
        try { real = FarmFiles.RealPath(Path.IsPathRooted(clean) ? clean : Path.Combine(job, clean)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            throw new FarmRefusedException($"{what} {path} can't be followed: {e.Message}");
        }
        if (!FarmFiles.IsInside(real, job) || string.Equals(real, job, StringComparison.Ordinal))
            throw new FarmRefusedException($"{what} {path} is outside the job's folder: a render node reads only what's in the job.");
        return real;
    }
}

/// <summary>
/// <c>AVALightWave --render-node &lt;folder&gt;</c>: a headless render node, run before any window is made. A line per
/// frame goes to standard output, problems to standard error; the exit code is 0, or 1 when the folder can't be used,
/// the arguments are wrong, or a frame failed.
/// </summary>
public static class FarmCommandLine
{
    public const string Switch = "--render-node";

    public const string Usage = """
        AVALightWave --render-node FOLDER [--once] [--name NAME] [--threads N] [--poll SECONDS]

          --render-node FOLDER  Render the jobs in a render farm folder (a folder on this machine or a network share
                                every node can reach), one frame at a time, with no window. Send jobs to it from
                                AVALightWave's Render › Render Queue (Farm Folder) or an agent's queue_render. Run as
                                many nodes as you like, on this machine or others; each claims frames the others haven't.
          --once                Stop when there's nothing left to render (for scripts), instead of watching for jobs.
          --name NAME           The node's name in the queue (default: the machine's name).
          --threads N           The most threads to render with (default: every core).
          --poll SECONDS        How often to look for new jobs when idle (default 5).

        A node reads only the scene and images inside each job's folder and runs nothing from it; ffmpeg is used only
        to join a finished job's frames into a movie, when the job asks for one and ffmpeg is on the PATH.
        """;

    public static bool Wanted(string[] args) => args.Contains(Switch);

    /// <summary>Runs the node the arguments describe until it's cancelled (Ctrl+C) or, with --once, has nothing left.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter errors, CancellationToken cancel = default)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            await output.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }
        string? folder = null, name = null;
        bool once = false;
        int threads = 0;
        double poll = 5;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (a)
            {
                case Switch: folder = Next(); break;
                case "--once": once = true; break;
                case "--name": name = Next(); break;
                case "--threads":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out threads) || threads < 0) return await Fail(errors, "--threads needs a whole number (0 for every core).").ConfigureAwait(false);
                    break;
                case "--poll":
                    if (!double.TryParse(Next(), NumberStyles.Float, CultureInfo.InvariantCulture, out poll) || !(poll >= 0.1 && poll <= 3600)) return await Fail(errors, "--poll needs a number of seconds from 0.1 to 3600.").ConfigureAwait(false);
                    break;
                default: return await Fail(errors, $"Unknown argument {a}.").ConfigureAwait(false);
            }
        }
        if (string.IsNullOrWhiteSpace(folder)) return await Fail(errors, "--render-node needs the farm folder.").ConfigureAwait(false);
        if (!Directory.Exists(folder)) return await Fail(errors, $"There's no folder {folder}. Make it (or mount the share), then start the node again.", usage: false).ConfigureAwait(false);
        var node = new FarmNode(folder, new FarmNodeOptions
        {
            Once = once, Name = name, Threads = threads, Poll = TimeSpan.FromSeconds(poll),
            Log = line => { lock (output) output.WriteLine(line); },
            Error = line => { lock (errors) errors.WriteLine(line); },
        });
        await output.WriteLineAsync($"AVALightWave render node {node.Name} ({node.Id}) on {node.Folder.Root}" + (once ? ", until nothing is left." : ". Ctrl+C stops it.")).ConfigureAwait(false);
        var result = await node.RunAsync(cancel).ConfigureAwait(false);
        await output.WriteLineAsync($"Stopped: {result.Rendered} frames rendered, {result.Failed} failed.").ConfigureAwait(false);
        await output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return result.Failed > 0 ? 1 : 0;
    }

    private static async Task<int> Fail(TextWriter errors, string message, bool usage = true)
    {
        await errors.WriteLineAsync(message + (usage ? Environment.NewLine + Environment.NewLine + Usage : "")).ConfigureAwait(false);
        return 1;
    }
}

/// <summary>
/// The local render queue: the farm protocol in a folder of the person's own (<see cref="FarmFolder.DefaultQueue"/>,
/// or a farm folder they choose), worked by a node in this process on a background task that the window starts and
/// stops. Other nodes (other processes or machines) can work the same folder at the same time.
/// </summary>
public sealed class RenderQueue(string folder) : IAsyncDisposable
{
    private CancellationTokenSource? _stop;
    private Task<FarmNodeResult>? _run;

    public FarmFolder Folder { get; } = new(folder);

    /// <summary>The node working the folder in this process, while it runs.</summary>
    public FarmNode? Node { get; private set; }

    public bool Running => _run is { IsCompleted: false };

    /// <summary>A line for each frame and problem (from the node's thread).</summary>
    public event Action<string>? Said;

    /// <summary>Starts working the folder in this process (nothing happens if it already is).</summary>
    public void Start(FarmNodeOptions? options = null)
    {
        if (Running) return;
        _stop = new CancellationTokenSource();
        var o = (options ?? new FarmNodeOptions { Name = Environment.MachineName + " (this window)", Poll = TimeSpan.FromSeconds(2) }) with
        {
            Once = false,
            Log = line => Said?.Invoke(line),
            Error = line => Said?.Invoke(line),
        };
        Node = new FarmNode(Folder.Root, o);
        _run = Node.RunAsync(_stop.Token);
    }

    /// <summary>Stops the node (the frame under way is dropped and its claim given back).</summary>
    public async Task StopAsync()
    {
        if (_stop == null || _run == null) return;
        await _stop.CancelAsync().ConfigureAwait(false);
        try { await _run.ConfigureAwait(false); }
        catch (Exception e) when (e is OperationCanceledException or IOException) { }
        _stop.Dispose();
        _stop = null;
        _run = null;
        Node = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
