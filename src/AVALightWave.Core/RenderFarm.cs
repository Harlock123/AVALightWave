using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

// The render farm, in the spirit of classic LightWave's ScreamerNet: a shared folder (a local one, or any network share
// the machines mount) holding jobs, and render nodes ("AVALightWave --render-node <folder>", or the window's own) that
// claim frames by making files in it. There's no network listener: the folder is the whole protocol. The local render
// queue is the same protocol in a folder of the person's own.
//
// The folder (protocol 1):
//   <folder>/jobs/<id>/job.json            the job (FarmJobSpec), written once, atomically (temp file + rename); a
//                                          reorder rewrites it the same way
//   <folder>/jobs/<id>/scene.avascene      the scene, meshes embedded, image paths relative to the job's folder
//   <folder>/jobs/<id>/images/…            copies of every image the scene uses (textures, the environment)
//   <folder>/jobs/<id>/claims/<frame>.claim   a node's claim on a scene frame (FileMode.CreateNew), JSON FarmClaim
//   <folder>/jobs/<id>/out/<output><n>.png    the frames, n = 1, 2… in render order (as Render Animation numbers
//                                          them, so ffmpeg reads them as a run), written as a temp file then renamed
//   <folder>/jobs/<id>/done/<frame>.done   written (atomically) after the frame's PNG is in place, JSON FarmDone
//   <folder>/jobs/<id>/errors/<frame>.txt  why a frame failed (job.txt: why the job can't be read); the job is failed
//   <folder>/jobs/<id>/cancel              the job is cancelled: nodes claim none of its frames and stop its frame
//   <folder>/jobs/<id>/movie.claim, movie.done, movie.txt   the movie (only if the job asks for one): claimed by the
//                                          node that finishes the last frame, then its files or what went wrong
//   <folder>/nodes/<node-id>.json          each node's heartbeat (FarmNodeInfo), rewritten every few seconds
// A job appears whole: it's made in jobs/.<id>.partial and that folder is renamed to jobs/<id> when complete.

/// <summary>A job on the farm: what job.json holds. Frames are scene frames; sizes are pixels.</summary>
public sealed record FarmJobSpec
{
    /// <summary>The folder protocol this was written for (<see cref="FarmFolder.Protocol"/>).</summary>
    public int Protocol { get; init; } = FarmFolder.Protocol;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>The scene file, relative to the job's folder (an .avascene).</summary>
    public string Scene { get; init; } = "scene" + SceneJson.Extension;
    public int First { get; init; } = 1;
    public int Last { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 480;
    /// <summary>Rays a pixel (antialiasing), 1 to 64.</summary>
    public int Samples { get; init; } = 1;
    /// <summary>Denoise each frame (the engine's RenderSettings.Denoise). Written only when true, so older nodes read the job as before.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Denoise { get; init; }
    /// <summary>The PNGs' names before their number, as Render Animation's: frame0001.png and so on.</summary>
    public string Output { get; init; } = "frame";
    /// <summary>The movie made when every frame is done: none, mp4, gif or both (with ffmpeg, if the node has it).</summary>
    public string Movie { get; init; } = "none";
    /// <summary>Frames a second, for the movie.</summary>
    public double Fps { get; init; } = 30;
    public DateTime Created { get; init; } = DateTime.UtcNow;
    /// <summary>The queue's order: lower first (the creation time in .NET ticks, unless reordered).</summary>
    public long Order { get; init; }
    /// <summary>The machine it was sent from (for the queue's list).</summary>
    public string? Submitter { get; init; }

    /// <summary>The scene frames to render, in order.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> Frames => Last < First || Step < 1 ? [] : Enumerable.Range(0, (Last - First) / Step + 1).Select(i => First + i * Step).ToList();

    /// <summary>The PNG of the n-th frame rendered (from 1).</summary>
    public string FileName(int number) => AnimationRender.FileName(Output, number);

    /// <summary>The PNG of a scene frame.</summary>
    public string FileOfFrame(int frame) => FileName((frame - First) / Math.Max(1, Step) + 1);

    public static readonly IReadOnlyList<string> Movies = ["none", "mp4", "gif", "both"];

    /// <summary>What's wrong with it, or null if it will do.</summary>
    public string? Problem()
    {
        if (Protocol != FarmFolder.Protocol) return $"It was written for farm protocol {Protocol}; this AVALightWave reads protocol {FarmFolder.Protocol}.";
        if (!FarmFolder.IsJobId(Id)) return "Its id isn't one AVALightWave makes.";
        if (Last < First) return $"The last frame ({Last}) is before the first ({First}).";
        if (Step < 1) return "The step must be 1 or more.";
        if ((long)(Last - First) / Step + 1 > FarmFolder.MaxFrames) return $"A job renders at most {FarmFolder.MaxFrames} frames.";
        if (Width < 1 || Height < 1 || Width > 8192 || Height > 8192) return "The size must be between 1 and 8192.";
        if (Samples < 1 || Samples > 64) return "The samples must be between 1 and 64.";
        if (!FarmFolder.IsPlainName(Output)) return "The frames' name must be a plain file name, such as \"frame\".";
        if (!Movies.Contains(Movie)) return "The movie must be none, mp4, gif or both.";
        if (!double.IsFinite(Fps) || Fps <= 0 || Fps > 240) return "The frame rate must be between 0 and 240.";
        if (string.IsNullOrWhiteSpace(Scene) || !Scene.EndsWith(SceneJson.Extension, StringComparison.OrdinalIgnoreCase))
            return $"The scene must be an {SceneJson.Extension} file in the job's folder.";
        if (Name.Length > 200) return "The name is too long.";
        return null;
    }
}

/// <summary>What to send to the farm: the frames, size, samples, the frames' name and a movie.</summary>
public sealed record FarmSubmit
{
    /// <summary>The job's name in the queue (default: the scene's).</summary>
    public string? Name { get; init; }
    public int First { get; init; } = 1;
    public int Last { get; init; } = 1;
    public int Step { get; init; } = 1;
    public int Width { get; init; } = 640;
    public int Height { get; init; } = 480;
    public int Samples { get; init; } = 1;
    public bool Denoise { get; init; }
    public string Output { get; init; } = "frame";
    public string Movie { get; init; } = "none";

    /// <summary>The scene's frames, step and frame rate and its camera's size.</summary>
    public static FarmSubmit From(Scene scene, string? name = null) => new()
    {
        Name = name,
        First = scene.FirstFrame,
        Last = scene.LastFrame,
        Step = Math.Max(1, scene.FrameStep),
        Width = scene.CurrentCamera?.Width ?? 640,
        Height = scene.CurrentCamera?.Height ?? 480,
    };
}

/// <summary>A job sent: its spec, its folder, where its frames go, and what the copy couldn't keep.</summary>
public sealed record FarmSubmitted(FarmJobSpec Job, string Folder, string OutputFolder, IReadOnlyList<string> Warnings);

/// <summary>Where a job has got to.</summary>
public enum FarmJobState { Waiting, Rendering, Done, Failed, Cancelled }

/// <summary>A job as the queue shows it.</summary>
public sealed record FarmJobStatus(string Id, string Folder, FarmJobSpec? Job, FarmJobState State, int Done, int Total, string? Reason, IReadOnlyList<string> Nodes, string? Movie)
{
    public string Name => Job?.Name is { Length: > 0 } n ? n : Id;
    public string OutputFolder => Path.Combine(Folder, "out");
    public bool Finished => State is FarmJobState.Done or FarmJobState.Failed or FarmJobState.Cancelled;
    public double Fraction => Total == 0 ? 0 : (double)Done / Total;

    /// <summary>"Ship turn: 12 of 48 frames, rendering on studio-pc".</summary>
    public string Text
    {
        get
        {
            string state = State switch
            {
                FarmJobState.Waiting => "waiting",
                FarmJobState.Rendering => "rendering" + (Nodes.Count > 0 ? " on " + string.Join(", ", Nodes) : ""),
                FarmJobState.Done => "done",
                FarmJobState.Failed => "failed: " + Reason,
                _ => "cancelled",
            };
            return $"{Name}: {Done} of {Total} frames, {state}" + (Movie != null ? $" ({Movie})" : "");
        }
    }
}

/// <summary>A node's heartbeat: nodes/&lt;id&gt;.json.</summary>
public sealed record FarmNodeInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Pid { get; init; }
    /// <summary>idle, rendering or stopped.</summary>
    public string State { get; init; } = "idle";
    public string? Job { get; init; }
    public int? Frame { get; init; }
    public int Rendered { get; init; }
    public int Failed { get; init; }
    public DateTime Started { get; init; }
    public DateTime Seen { get; init; }

    public string Text => $"{Name} on {Host} (process {Pid}): " + (State == "rendering" && Frame is int f ? $"rendering frame {f} of {Job}" : State) + $", {Rendered} frames rendered";
}

/// <summary>A claim on a frame: claims/&lt;frame&gt;.claim.</summary>
public sealed record FarmClaim(string Node, string Name, string Host, int Pid, DateTime Time);

/// <summary>A finished frame: done/&lt;frame&gt;.done.</summary>
public sealed record FarmDone(string Node, string Name, string Host, string File, double Seconds, DateTime Time);

/// <summary>
/// A render farm's folder (or the local render queue's): sending jobs to it, reading where they've got to and which
/// nodes are there, cancelling, removing finished jobs and reordering. Every change is a file made with
/// <see cref="FileMode.CreateNew"/> or written to a temporary name and renamed, so nodes on other machines never see
/// half a file. Nothing here runs anything from the folder. Slow on a network share: call it off the UI thread.
/// </summary>
public sealed class FarmFolder
{
    public const int Protocol = 1;
    public const int MaxFrames = 100_000;
    public const string JobFile = "job.json";
    public const string CancelFile = "cancel";

    /// <summary>A node not heard from for this long is gone, and its claims can be taken over.</summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromMinutes(2);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public FarmFolder(string root) => Root = Path.GetFullPath(root);

    public string Root { get; }
    public string JobsFolder => Path.Combine(Root, "jobs");
    public string NodesFolder => Path.Combine(Root, "nodes");

    /// <summary>The local render queue's folder: queue in AVALightWave's app-data folder.</summary>
    public static string DefaultQueue => Path.Combine(LightWaveOptions.Folder, "queue");

    public string JobFolder(string id) => IsJobId(id) ? Path.Combine(JobsFolder, id) : throw new ArgumentException($"\"{id}\" isn't a job's id.");

    public string OutputFolder(string id) => Path.Combine(JobFolder(id), "out");

    /// <summary>The command a person runs on each machine to render this folder's jobs.</summary>
    public string NodeCommand => $"AVALightWave --render-node \"{Root}\"";

    // ───────────────────────────── sending ─────────────────────────────

    /// <summary>A copy of a scene to send, taken on the thread that owns it (quick); <see cref="Submit"/> may then run anywhere.</summary>
    public static Scene Snapshot(Scene scene) => scene.Copy(d => d.Copy());

    /// <summary>
    /// Sends a job: <paramref name="snapshot"/> (a <see cref="Snapshot"/>, which this changes: never the open scene) is
    /// saved into the job's folder as a self-contained .avascene, meshes embedded, and every image it uses (texture
    /// images, the environment picture) is copied into the job's images folder with the copy's paths rewritten to them,
    /// so a node needs nothing outside the job. <paramref name="sceneFolder"/> is where the open scene was loaded from,
    /// for images given relative to it. Images that can't be found are left out (a node renders a checker for a
    /// texture, nothing for an environment) and said in the warnings.
    /// </summary>
    public FarmSubmitted Submit(Scene snapshot, FarmSubmit settings, string? sceneFolder = null)
    {
        var now = DateTime.UtcNow;
        var spec = new FarmJobSpec
        {
            Id = NewId(now),
            Name = (settings.Name ?? "").Trim() is { Length: > 0 } n ? n : "Scene",
            First = settings.First, Last = settings.Last, Step = settings.Step,
            Width = settings.Width, Height = settings.Height, Samples = settings.Samples, Denoise = settings.Denoise,
            Output = settings.Output, Movie = (settings.Movie ?? "none").Trim().ToLowerInvariant(),
            Fps = snapshot.FramesPerSecond > 0 ? snapshot.FramesPerSecond : 30,
            Created = now,
            Order = NextOrder(now),
            Submitter = Environment.MachineName,
        };
        if (spec.Problem() is { } problem) throw new ArgumentException(problem);
        if (snapshot.CurrentCamera == null) throw new InvalidOperationException("The scene has no camera to render through. Add one first.");
        Directory.CreateDirectory(JobsFolder);
        string partial = Path.Combine(JobsFolder, "." + spec.Id + ".partial");
        string folder = JobFolder(spec.Id);
        var warnings = new List<string>();
        try
        {
            Directory.CreateDirectory(partial);
            foreach (var sub in new[] { "claims", "done", "errors", "out" }) Directory.CreateDirectory(Path.Combine(partial, sub));
            CopyImages(snapshot, partial, sceneFolder, warnings);
            SceneJson.WriteFile(snapshot, Path.Combine(partial, spec.Scene), embedMeshes: true);
            FarmFiles.WriteAtomic(Path.Combine(partial, JobFile), JsonSerializer.Serialize(spec, Json));
            Directory.Move(partial, folder);
        }
        catch
        {
            try { if (Directory.Exists(partial)) Directory.Delete(partial, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return new FarmSubmitted(spec, folder, Path.Combine(folder, "out"), warnings);
    }

    /// <summary>Copies the images a scene uses into the job's images folder, pointing the copy's layers at them.</summary>
    private static void CopyImages(Scene scene, string job, string? sceneFolder, List<string> warnings)
    {
        string images = Path.Combine(job, "images");
        var copied = new Dictionary<string, string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        string? Copy(string source)
        {
            if (copied.TryGetValue(source, out var had)) return had;
            Directory.CreateDirectory(images);
            string name = FarmFiles.SafeName(Path.GetFileName(source), "image");
            string target = Path.Combine(images, name);
            for (int i = 2; File.Exists(target); i++)
                target = Path.Combine(images, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
            File.Copy(source, target);
            return copied[source] = "images/" + Path.GetFileName(target);
        }

        var docs = new HashSet<Document>(ReferenceEqualityComparer.Instance);
        foreach (var o in scene.Objects)
        {
            if (o.Document is not { } doc || !docs.Add(doc)) continue;
            string? from = TextureImages.BaseOf(o.SourcePath ?? doc.FilePath) ?? sceneFolder;
            foreach (var s in doc.Surfaces)
            foreach (var t in s.Textures)
            {
                if (!t.IsImage || string.IsNullOrWhiteSpace(t.Image)) continue;
                if (TextureImages.Resolve(t.Image, from) is { } file) t.Image = Copy(file)!;
                else
                {
                    warnings.Add($"{o.Name}'s surface {s.Name}: the image {t.Image} wasn't found, so the farm renders a checker in its place.");
                    t.Image = "";
                }
            }
        }
        foreach (var o in scene.Objects)
        {
            // The copy keeps nothing of the person's folders: the meshes are embedded.
            o.SourcePath = null;
            if (o.Document != null) o.Document.FilePath = null;
        }
        var env = scene.Environment;
        if (env.On && !env.Image.Trim().StartsWith(EnvironmentMap.BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (EnvironmentMap.Resolve(env.Image, sceneFolder) is { } file) scene.Environment = env with { Image = Copy(file)! };
            else
            {
                warnings.Add($"The environment image {env.Image} wasn't found, so the farm renders without it.");
                scene.Environment = env with { Image = "" };
            }
        }
    }

    private static long _lastOrder;

    /// <summary>A new job's place in the queue: the time in ticks, always later than this process's last job's.</summary>
    private static long NextOrder(DateTime utc)
    {
        while (true)
        {
            long last = Interlocked.Read(ref _lastOrder);
            long next = Math.Max(utc.Ticks, last + 1);
            if (Interlocked.CompareExchange(ref _lastOrder, next, last) == last) return next;
        }
    }

    /// <summary>A new job's id: the time it was made (so ids sort in order) and six random hex digits.</summary>
    internal static string NewId(DateTime utc) =>
        utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3));

    private static readonly Regex IdPattern = new(@"^\d{8}-\d{6}-[0-9a-f]{6}$", RegexOptions.CultureInvariant);

    public static bool IsJobId(string? id) => id != null && IdPattern.IsMatch(id);

    /// <summary>A plain file name: no folders, no wildcards for ffmpeg.</summary>
    public static bool IsPlainName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !name.Contains('/') && !name.Contains('\\') &&
        !name.Contains('%') && name != "." && name != "..";

    // ───────────────────────────── reading ─────────────────────────────

    /// <summary>Every job in the folder, in the queue's order.</summary>
    public IReadOnlyList<FarmJobStatus> Scan()
    {
        var jobs = new List<FarmJobStatus>();
        foreach (var id in JobIds())
        {
            try { jobs.Add(Status(id)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return jobs.OrderBy(j => j.Job?.Order ?? long.MaxValue).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The ids of the job folders there.</summary>
    internal IEnumerable<string> JobIds()
    {
        if (!Directory.Exists(JobsFolder)) return [];
        try { return Directory.GetDirectories(JobsFolder).Select(Path.GetFileName).Where(IsJobId).Cast<string>().Order(StringComparer.Ordinal).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>Reads a job's job.json, or says why it can't be had.</summary>
    public FarmJobSpec ReadJob(string id)
    {
        string file = Path.Combine(JobFolder(id), JobFile);
        var info = new FileInfo(file);
        if (!info.Exists) throw new InvalidDataException("It has no job.json.");
        if (info.Length > 1 << 20) throw new InvalidDataException("Its job.json is too big to be a job.");
        FarmJobSpec? spec;
        try { spec = JsonSerializer.Deserialize<FarmJobSpec>(File.ReadAllText(file), Json); }
        catch (JsonException e) { throw new InvalidDataException($"Its job.json isn't a job ({e.Message})."); }
        if (spec == null) throw new InvalidDataException("Its job.json is empty.");
        if (spec.Id != id) throw new InvalidDataException("Its job.json names another job.");
        if (spec.Problem() is { } problem) throw new InvalidDataException(problem);
        return spec;
    }

    /// <summary>Where one job has got to.</summary>
    public FarmJobStatus Status(string id)
    {
        string folder = JobFolder(id);
        FarmJobSpec? spec = null;
        string? reason = null;
        try { spec = ReadJob(id); }
        catch (InvalidDataException e) { reason = e.Message; }
        int total = spec?.Frames.Count ?? 0;
        var done = Names(Path.Combine(folder, "done"), ".done");
        var errors = Names(Path.Combine(folder, "errors"), ".txt");
        var claims = Names(Path.Combine(folder, "claims"), ".claim");
        var frames = spec?.Frames.Select(f => f.ToString(CultureInfo.InvariantCulture)).ToHashSet() ?? [];
        int doneCount = done.Count(frames.Contains);
        var nodes = new List<string>();
        foreach (var c in claims.Where(c => !done.Contains(c)))
            if (FarmFiles.ReadJson<FarmClaim>(Path.Combine(folder, "claims", c + ".claim"), 1 << 16) is { } claim && !nodes.Contains(claim.Name)) nodes.Add(claim.Name);
        if (reason == null && errors.Count > 0)
        {
            var first = errors.Contains("job") ? "job" : errors.Order(StringComparer.Ordinal).First();
            string text = FarmFiles.ReadText(Path.Combine(folder, "errors", first + ".txt"), 4096)?.Trim() ?? "";
            reason = first == "job" ? text : $"frame {first}: {text}";
        }
        var state = File.Exists(Path.Combine(folder, CancelFile)) ? FarmJobState.Cancelled
            : reason != null ? FarmJobState.Failed
            : total > 0 && doneCount >= total ? FarmJobState.Done
            : nodes.Count > 0 ? FarmJobState.Rendering
            : FarmJobState.Waiting;
        string? movie = null;
        if (spec != null && spec.Movie != "none")
        {
            if (FarmFiles.ReadText(Path.Combine(folder, "movie.txt"), 4096) is { } problem) movie = "no movie: " + problem.Trim();
            else if (File.Exists(Path.Combine(folder, "movie.done"))) movie = "movie made";
            else if (File.Exists(Path.Combine(folder, "movie.claim"))) movie = "making the movie";
            else movie = "movie when done";
            if (state == FarmJobState.Done && movie == "making the movie") state = FarmJobState.Rendering;
        }
        return new FarmJobStatus(id, folder, spec, state, doneCount, total, reason, nodes, movie);
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

    /// <summary>The nodes heard from within <paramref name="within"/> (default <see cref="DefaultStaleAfter"/>).</summary>
    public IReadOnlyList<FarmNodeInfo> Nodes(TimeSpan? within = null)
    {
        var limit = within ?? DefaultStaleAfter;
        var nodes = new List<FarmNodeInfo>();
        if (!Directory.Exists(NodesFolder)) return nodes;
        string[] files;
        try { files = Directory.GetFiles(NodesFolder, "*.json"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return nodes; }
        foreach (var f in files)
        {
            if (FarmFiles.Age(f) is not { } age || age > limit) continue;
            if (FarmFiles.ReadJson<FarmNodeInfo>(f, 1 << 16) is { } n && n.State != "stopped") nodes.Add(n);
        }
        return nodes.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ThenBy(n => n.Id, StringComparer.Ordinal).ToList();
    }

    // ───────────────────────────── changing ─────────────────────────────

    /// <summary>Cancels a job: nodes claim no more of its frames and stop the one they're on. The frames done stay.</summary>
    public void Cancel(string id)
    {
        string folder = JobFolder(id);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"There's no job {id}.");
        FarmFiles.TryCreateNew(Path.Combine(folder, CancelFile), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Deletes a finished (done, failed or cancelled) job's folder, frames and all. Returns false if it isn't finished.</summary>
    public bool Remove(string id)
    {
        var status = Status(id);
        if (!status.Finished) return false;
        // Renamed out of the jobs first, so no node starts on it while it's being deleted.
        string gone = Path.Combine(JobsFolder, "." + id + ".removed-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(3)));
        Directory.Move(status.Folder, gone);
        try { Directory.Delete(gone, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    /// <summary>Removes every finished job. Returns how many.</summary>
    public int RemoveFinished()
    {
        int n = 0;
        foreach (var j in Scan().Where(j => j.Finished))
        {
            try { if (Remove(j.Id)) n++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return n;
    }

    /// <summary>Moves a job up (negative) or down (positive) the queue, past the next one that way. Returns whether it moved.</summary>
    public bool Move(string id, int direction)
    {
        var jobs = Scan().Where(j => j.Job != null).ToList();
        int i = jobs.FindIndex(j => j.Id == id);
        int k = i + Math.Sign(direction);
        if (i < 0 || direction == 0 || k < 0 || k >= jobs.Count) return false;
        var a = jobs[i].Job!;
        var b = jobs[k].Job!;
        long ao = a.Order, bo = b.Order;
        if (ao == bo) bo += Math.Sign(direction);
        FarmFiles.WriteAtomic(Path.Combine(jobs[i].Folder, JobFile), JsonSerializer.Serialize(a with { Order = bo }, Json));
        FarmFiles.WriteAtomic(Path.Combine(jobs[k].Folder, JobFile), JsonSerializer.Serialize(b with { Order = ao }, Json));
        return true;
    }
}

/// <summary>The farm's file handling: atomic writes, exclusive creation, bounded reads and real paths (links resolved).</summary>
public static class FarmFiles
{
    /// <summary>Writes a file under a temporary name beside it, then renames it into place (replacing what was there).</summary>
    public static void WriteAtomic(string path, string text) => WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text));

    public static void WriteAtomic(string path, byte[] bytes)
    {
        string temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)) + ".tmp");
        File.WriteAllBytes(temp, bytes);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 10 && e is not FileNotFoundException and not DirectoryNotFoundException)
            {
                // Windows refuses to replace a file another process is reading this moment: try again shortly.
                Thread.Sleep(20 * (attempt + 1));
            }
            catch
            {
                try { File.Delete(temp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
                throw;
            }
        }
    }

    /// <summary>
    /// Makes a file only if there's none (<see cref="FileMode.CreateNew"/>: one process wins, on a local disk or an SMB
    /// or NFS share). Returns whether this call made it.
    /// </summary>
    public static bool TryCreateNew(string path, string text)
    {
        try
        {
            using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            f.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(path)) { return false; }
    }

    /// <summary>A small text file, or null if it isn't there, can't be read or is bigger than <paramref name="limit"/> bytes.</summary>
    public static string? ReadText(string path, int limit)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > limit) return null;
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A small JSON file read as <typeparamref name="T"/>, or null.</summary>
    public static T? ReadJson<T>(string path, int limit) where T : class
    {
        if (ReadText(path, limit) is not { } text) return null;
        try { return JsonSerializer.Deserialize<T>(text, FarmFolder.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>How long ago a file was last written, or null if it isn't there.</summary>
    public static TimeSpan? Age(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? DateTime.UtcNow - info.LastWriteTimeUtc : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A name made safe for a file: no folder separators or characters the system refuses.</summary>
    public static string SafeName(string? name, string fallback)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '%']).ToHashSet();
        var safe = new string((name ?? "").Select(c => bad.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().Trim('.');
        if (safe.Length > 80) safe = safe[..80];
        return safe.Length == 0 ? fallback : safe;
    }

    /// <summary>
    /// A path with every symbolic link (and junction) along it followed, so where it really leads can be checked. The
    /// part after the first missing folder is kept as it is.
    /// </summary>
    public static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        for (int hops = 0; hops < 64; hops++)
        {
            string root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            string? restart = null;
            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                string? target;
                try { target = info.LinkTarget; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { target = null; }
                if (target == null) continue;
                string resolved = Path.IsPathRooted(target) ? target : Path.Combine(Path.GetDirectoryName(current) ?? root, target);
                restart = Path.GetFullPath(Path.Combine([resolved, .. parts[(i + 1)..]]));
                break;
            }
            if (restart == null) return current.Length == 0 ? full : current;
            full = restart;
        }
        throw new IOException($"Too many links lead on from {path}.");
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Whether <paramref name="path"/> (already real) is <paramref name="folder"/> (already real) or inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        string f = Path.TrimEndingDirectorySeparator(folder);
        return string.Equals(path, f, PathComparison) || path.StartsWith(f + Path.DirectorySeparatorChar, PathComparison);
    }
}
