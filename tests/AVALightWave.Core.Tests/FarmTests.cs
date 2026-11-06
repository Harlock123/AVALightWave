using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>
/// The render farm's folder protocol: sending a job (the scene and its images copied, the open scene untouched), nodes
/// rendering it (one, two at once, taking over a dead node's claim), cancelling, refusing jobs that point outside their
/// folder or can't be read, the command line and the local queue. Temporary folders only; tiny renders.
/// </summary>
public class FarmTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A small scene: the camera and light, a textured crate, and an environment picture, all from files in the bench.</summary>
    private static SceneDocument Scene(Workbench bench, int frames = 3)
    {
        var d = new SceneDocument();
        d.New();
        var crate = d.LoadObject(bench.Object("crate.lwo"));
        string pictures = Directory.CreateDirectory(bench.File("pictures")).FullName;
        string tex = Path.Combine(pictures, "tex.png");
        File.WriteAllBytes(tex, Checker(8, 8));
        File.WriteAllBytes(Path.Combine(pictures, "sky.png"), Checker(16, 8));
        crate.Document!.Surfaces[0].Textures.Add(new TextureLayer { Channel = TextureChannel.Color, Image = tex, Size = new Vec3(1, 1, 1) });
        var s = d.Scene;
        s.FirstFrame = 1;
        s.LastFrame = frames;
        s.Environment = new EnvironmentSettings { Image = Path.Combine(pictures, "sky.png") };
        var cam = s.CurrentCamera!;
        cam.Width = 32;
        cam.Height = 24;
        return d;
    }

    private static byte[] Checker(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            byte v = (byte)(((x + y) & 1) == 0 ? 230 : 40);
            rgba[i] = v; rgba[i + 1] = (byte)(255 - v); rgba[i + 2] = 90; rgba[i + 3] = 255;
        }
        return Png.Encode(w, h, rgba);
    }

    private static FarmSubmit Tiny(int frames = 3) => new() { Name = "Tiny", First = 1, Last = frames, Width = 32, Height = 24, Samples = 1, Output = "shot" };

    private static FarmNodeOptions Once(string? name = null) => new() { Once = true, Threads = 1, Name = name, Heartbeat = TimeSpan.FromMilliseconds(200) };

    private static FarmSubmitted Send(Workbench bench, string farm, int frames = 3)
    {
        var d = Scene(bench, frames);
        return new FarmFolder(farm).Submit(FarmFolder.Snapshot(d.Scene), Tiny(frames), bench.Path);
    }

    [Fact]
    public void Sending_a_job_copies_the_scene_and_its_images_and_leaves_the_open_scene_alone()
    {
        using var bench = new Workbench();
        var d = Scene(bench);
        var crate = d.Scene.Objects.Single();
        string tex = crate.Document!.Surfaces[0].Textures[0].Image;
        string env = d.Scene.Environment.Image;
        long version = d.Scene.Version;
        var farm = new FarmFolder(bench.File("farm"));
        var sent = farm.Submit(FarmFolder.Snapshot(d.Scene), Tiny(), bench.Path);

        Assert.Empty(sent.Warnings);
        Assert.Equal(farm.JobFolder(sent.Job.Id), sent.Folder);
        Assert.True(FarmFolder.IsJobId(sent.Job.Id));
        Assert.True(File.Exists(Path.Combine(sent.Folder, "job.json")));
        Assert.True(File.Exists(Path.Combine(sent.Folder, "images", "tex.png")));
        Assert.True(File.Exists(Path.Combine(sent.Folder, "images", "sky.png")));
        string text = File.ReadAllText(Path.Combine(sent.Folder, "scene.avascene"));
        Assert.Contains("images/tex.png", text);
        Assert.Contains("images/sky.png", text);
        // Nothing of the person's folders is in the job: meshes are embedded, images copied.
        Assert.DoesNotContain(bench.Path, text);
        Assert.Contains("\"layers\"", text);
        // The open scene is as it was.
        Assert.Equal(tex, crate.Document.Surfaces[0].Textures[0].Image);
        Assert.Equal(env, d.Scene.Environment.Image);
        Assert.Equal(bench.Object("crate.lwo"), crate.SourcePath);
        Assert.Equal(version, d.Scene.Version);

        var job = JsonNode.Parse(File.ReadAllText(Path.Combine(sent.Folder, "job.json")))!;
        Assert.Equal(1, (int)job["protocol"]!);
        Assert.Equal("Tiny", (string?)job["name"]);
        Assert.Equal("scene.avascene", (string?)job["scene"]);
        Assert.Equal(32, (int)job["width"]!);
        var status = Assert.Single(farm.Scan());
        Assert.Equal(FarmJobState.Waiting, status.State);
        Assert.Equal((0, 3), (status.Done, status.Total));
        Assert.Equal("Tiny: 0 of 3 frames, waiting", status.Text);
        Assert.DoesNotContain(Directory.GetDirectories(farm.JobsFolder), f => Path.GetFileName(f).StartsWith('.'));
    }

    [Fact]
    public async Task A_missing_image_is_left_out_with_a_warning()
    {
        using var bench = new Workbench();
        var d = Scene(bench);
        d.Scene.Objects.Single().Document!.Surfaces[0].Textures[0].Image = bench.File("nowhere.png");
        var sent = new FarmFolder(bench.File("farm")).Submit(FarmFolder.Snapshot(d.Scene), Tiny(1), bench.Path);
        Assert.Contains(sent.Warnings, w => w.Contains("nowhere.png") && w.Contains("checker"));
        var r = await FarmNode.RunAsync(bench.File("farm"), Once(), Cancel);
        Assert.Equal(new FarmNodeResult(1, 0), r);
    }

    [Fact]
    public async Task One_node_renders_every_frame_and_the_job_is_done()
    {
        using var bench = new Workbench();
        var sent = Send(bench, bench.File("farm"));
        var lines = new List<string>();
        var node = new FarmNode(bench.File("farm"), Once("solo") with { Log = l => { lock (lines) lines.Add(l); } });
        var r = await node.RunAsync(Cancel);
        Assert.Equal(new FarmNodeResult(3, 0), r);
        for (int n = 1; n <= 3; n++)
        {
            var png = File.ReadAllBytes(Path.Combine(sent.OutputFolder, $"shot{n:0000}.png"));
            var image = TextureImages.Decode(png)!;
            Assert.Equal((32, 24), (image.Width, image.Height));
            var done = JsonNode.Parse(File.ReadAllText(Path.Combine(sent.Folder, "done", $"{n}.done")))!;
            Assert.Equal("solo", (string?)done["name"]);
            Assert.Equal($"out/shot{n:0000}.png", (string?)done["file"]);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(sent.Folder, "claims")));
        Assert.Empty(Directory.GetFiles(sent.OutputFolder, "*.tmp"));
        Assert.Equal(3, lines.Count(l => l.StartsWith("Rendered frame")));
        var status = new FarmFolder(bench.File("farm")).Status(sent.Job.Id);
        Assert.Equal(FarmJobState.Done, status.State);
        Assert.Equal(1.0, status.Fraction);
        // A node that stops takes its heartbeat with it.
        Assert.Empty(Directory.GetFiles(Path.Combine(bench.File("farm"), "nodes")));
    }

    [Fact]
    public async Task The_node_finishing_the_last_frame_makes_the_movie_once_or_says_why_not()
    {
        using var bench = new Workbench();
        var d = Scene(bench, 2);
        var farm = new FarmFolder(bench.File("farm"));
        var sent = farm.Submit(FarmFolder.Snapshot(d.Scene), Tiny(2) with { Movie = "mp4" }, bench.Path);
        Assert.Equal("movie when done", farm.Status(sent.Job.Id).Movie);
        // A node with no ffmpeg: the frames are all there, and the job says why there's no movie.
        var r = await FarmNode.RunAsync(farm.Root, Once() with { Encoder = new AVAModeler.Layout.MovieEncoder(null) }, Cancel);
        Assert.Equal(2, r.Rendered);
        var status = farm.Status(sent.Job.Id);
        Assert.Equal(FarmJobState.Done, status.State);
        Assert.StartsWith("no movie: ffmpeg isn't installed", status.Movie);
        Assert.True(File.Exists(Path.Combine(sent.Folder, "movie.claim")));
        // Another node doesn't try again.
        Assert.Equal(new FarmNodeResult(0, 0), await FarmNode.RunAsync(farm.Root, Once(), Cancel));
        Assert.Contains("2 of 2 frames, done (no movie", status.Text);
    }

    [Fact]
    public async Task Two_nodes_share_a_job_and_no_frame_is_rendered_twice()
    {
        using var bench = new Workbench();
        var sent = Send(bench, bench.File("farm"), frames: 8);
        var a = new FarmNode(bench.File("farm"), Once("a"));
        var b = new FarmNode(bench.File("farm"), Once("b"));
        var results = await Task.WhenAll(a.RunAsync(Cancel), b.RunAsync(Cancel));
        Assert.Equal(8, results.Sum(r => r.Rendered));
        Assert.All(results, r => Assert.Equal(0, r.Failed));
        var by = Enumerable.Range(1, 8).Select(f => (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(sent.Folder, "done", $"{f}.done")))!["name"]).ToList();
        Assert.Equal(results[0].Rendered, by.Count(n => n == "a"));
        Assert.Equal(results[1].Rendered, by.Count(n => n == "b"));
        Assert.Equal(8, Directory.GetFiles(sent.OutputFolder, "shot*.png").Length);
        Assert.Equal(FarmJobState.Done, new FarmFolder(bench.File("farm")).Status(sent.Job.Id).State);
    }

    [Fact]
    public async Task A_dead_nodes_claim_is_taken_over_and_a_live_ones_is_left()
    {
        using var bench = new Workbench();
        string farm = bench.File("farm");
        var sent = Send(bench, farm);
        string claims = Path.Combine(sent.Folder, "claims");
        var old = DateTime.UtcNow.AddMinutes(-10);
        File.WriteAllText(Path.Combine(claims, "1.claim"), JsonSerializer.Serialize(new FarmClaim("dead-node", "dead", "elsewhere", 1, old), FarmFolder.Json));
        File.SetLastWriteTimeUtc(Path.Combine(claims, "1.claim"), old);
        // Frame 2's node is slow but alive: its heartbeat is fresh.
        File.WriteAllText(Path.Combine(claims, "2.claim"), JsonSerializer.Serialize(new FarmClaim("slow-node", "slow", "elsewhere", 2, old), FarmFolder.Json));
        File.SetLastWriteTimeUtc(Path.Combine(claims, "2.claim"), old);
        Directory.CreateDirectory(Path.Combine(farm, "nodes"));
        File.WriteAllText(Path.Combine(farm, "nodes", "slow-node.json"), JsonSerializer.Serialize(new FarmNodeInfo { Id = "slow-node", Name = "slow", Host = "elsewhere", State = "rendering", Job = sent.Job.Id, Frame = 2, Seen = DateTime.UtcNow }, FarmFolder.Json));
        var status = new FarmFolder(farm).Status(sent.Job.Id);
        Assert.Equal(FarmJobState.Rendering, status.State);
        Assert.Equal(["dead", "slow"], status.Nodes.Order());
        Assert.Contains(new FarmFolder(farm).Nodes(), n => n.Name == "slow");

        var lines = new List<string>();
        var r = await new FarmNode(farm, Once() with { StaleAfter = TimeSpan.FromSeconds(30), Log = l => { lock (lines) lines.Add(l); } }).RunAsync(Cancel);
        Assert.Equal(2, r.Rendered);
        Assert.True(File.Exists(Path.Combine(sent.Folder, "done", "1.done")));
        Assert.Contains(lines, l => l.Contains("Took over frame 1") && l.Contains("dead"));
        Assert.False(File.Exists(Path.Combine(sent.Folder, "done", "2.done")));
        Assert.True(File.Exists(Path.Combine(claims, "2.claim")));
        Assert.Empty(Directory.GetFiles(claims, "*.stale"));
    }

    [Fact]
    public async Task A_cancelled_job_is_not_claimed()
    {
        using var bench = new Workbench();
        var farm = new FarmFolder(bench.File("farm"));
        var sent = Send(bench, farm.Root);
        farm.Cancel(sent.Job.Id);
        var r = await FarmNode.RunAsync(farm.Root, Once(), Cancel);
        Assert.Equal(0, r.Rendered);
        Assert.Equal(FarmJobState.Cancelled, farm.Status(sent.Job.Id).State);
        Assert.Empty(Directory.GetFiles(Path.Combine(sent.Folder, "claims")));
        // Finished jobs go with Remove Finished; jobs still to do stay.
        var next = Send(bench, farm.Root, frames: 1);
        Assert.False(farm.Remove(next.Job.Id));
        Assert.Equal(1, farm.RemoveFinished());
        Assert.Equal([next.Job.Id], farm.Scan().Select(j => j.Id));
    }

    [Fact]
    public async Task Jobs_can_be_reordered_and_nodes_take_them_in_order()
    {
        using var bench = new Workbench();
        var farm = new FarmFolder(bench.File("farm"));
        var first = Send(bench, farm.Root, frames: 1);
        var second = Send(bench, farm.Root, frames: 1);
        Assert.Equal([first.Job.Id, second.Job.Id], farm.Scan().Select(j => j.Id));
        Assert.True(farm.Move(second.Job.Id, -1));
        Assert.Equal([second.Job.Id, first.Job.Id], farm.Scan().Select(j => j.Id));
        Assert.False(farm.Move(second.Job.Id, -1));
        var r = await FarmNode.RunAsync(farm.Root, Once(), Cancel);
        Assert.Equal(2, r.Rendered);
        var times = new[] { second, first }.Select(s => File.GetLastWriteTimeUtc(Path.Combine(s.Folder, "done", "1.done"))).ToList();
        Assert.True(times[0] <= times[1]);
    }

    public static TheoryData<string> Escapes => ["absolute image", "dotdot image", "linked image", "linked folder", "absolute object", "absolute environment"];

    [Theory]
    [MemberData(nameof(Escapes))]
    public async Task A_job_pointing_outside_its_folder_is_refused_and_nothing_outside_is_read(string how)
    {
        if (how.StartsWith("linked") && OperatingSystem.IsWindows()) return; // links need rights on Windows
        using var bench = new Workbench();
        using var outside = new Workbench();
        string secret = outside.File("secret.png");
        File.WriteAllBytes(secret, Checker(4, 4));
        string farm = bench.File("farm");
        var sent = Send(bench, farm, frames: 2);
        string scene = Path.Combine(sent.Folder, "scene.avascene");
        var json = JsonNode.Parse(File.ReadAllText(scene))!;
        void SetImage(string path)
        {
            var layer = json["documents"]![0]!["surfaces"]![0]!["textures"]![0]!;
            layer["image"] = path;
        }
        switch (how)
        {
            case "absolute image": SetImage(secret); break;
            case "dotdot image":
                File.Copy(secret, Path.Combine(farm, "jobs", "secret.png"));
                SetImage("../secret.png");
                break;
            case "linked image":
                File.CreateSymbolicLink(Path.Combine(sent.Folder, "images", "link.png"), secret);
                SetImage("images/link.png");
                break;
            case "linked folder":
                Directory.CreateSymbolicLink(Path.Combine(sent.Folder, "more"), outside.Path);
                SetImage("more/secret.png");
                break;
            case "absolute object":
                json["documents"]![0] = new JsonObject { ["path"] = outside.Object("crate.lwo") };
                break;
            case "absolute environment":
                json["settings"]!["environment"]!["image"] = secret;
                break;
        }
        File.WriteAllText(scene, json.ToJsonString());
        var errors = new List<string>();
        var r = await new FarmNode(farm, Once() with { Error = e => { lock (errors) errors.Add(e); } }).RunAsync(Cancel);
        Assert.Equal(0, r.Rendered);
        Assert.Equal(1, r.Failed);
        string why = File.ReadAllText(Path.Combine(sent.Folder, "errors", "1.txt"));
        Assert.Contains("outside the job's folder", why);
        Assert.False(File.Exists(Path.Combine(sent.Folder, "errors", "2.txt")));
        Assert.Empty(Directory.GetFiles(sent.OutputFolder));
        Assert.Single(errors);
        var status = new FarmFolder(farm).Status(sent.Job.Id);
        Assert.Equal(FarmJobState.Failed, status.State);
        Assert.Contains("outside the job's folder", status.Reason);
    }

    [Fact]
    public async Task A_malformed_job_is_refused()
    {
        using var bench = new Workbench();
        var farm = new FarmFolder(bench.File("farm"));
        var sent = Send(bench, farm.Root, frames: 1);
        File.WriteAllText(Path.Combine(sent.Folder, "job.json"), "{ \"protocol\": 1, \"id\": ");
        var bad = Send(bench, farm.Root, frames: 1);
        var spec = JsonNode.Parse(File.ReadAllText(Path.Combine(bad.Folder, "job.json")))!;
        spec["output"] = "../../escape";
        File.WriteAllText(Path.Combine(bad.Folder, "job.json"), spec.ToJsonString());
        var newer = Send(bench, farm.Root, frames: 1);
        spec = JsonNode.Parse(File.ReadAllText(Path.Combine(newer.Folder, "job.json")))!;
        spec["scene"] = "/etc/passwd.avascene";
        File.WriteAllText(Path.Combine(newer.Folder, "job.json"), spec.ToJsonString());
        var good = Send(bench, farm.Root, frames: 1);

        var r = await FarmNode.RunAsync(farm.Root, Once(), Cancel);
        Assert.Equal(new FarmNodeResult(1, 1), r);
        Assert.Contains("isn't a job", File.ReadAllText(Path.Combine(sent.Folder, "errors", "job.txt")));
        Assert.Contains("plain file name", File.ReadAllText(Path.Combine(bad.Folder, "errors", "job.txt")));
        Assert.Contains("outside the job's folder", File.ReadAllText(Path.Combine(newer.Folder, "errors", "1.txt")));
        var states = farm.Scan().ToDictionary(j => j.Id, j => j.State);
        Assert.Equal(FarmJobState.Failed, states[sent.Job.Id]);
        Assert.Equal(FarmJobState.Failed, states[bad.Job.Id]);
        Assert.Equal(FarmJobState.Done, states[good.Job.Id]);
        Assert.Empty(Directory.GetFiles(bad.OutputFolder));
    }

    [Fact]
    public async Task The_command_line_runs_a_node_until_nothing_is_left()
    {
        using var bench = new Workbench();
        var sent = Send(bench, bench.File("farm"), frames: 2);
        var output = new StringWriter();
        var errors = new StringWriter();
        Assert.Equal(0, await FarmCommandLine.RunAsync(["--render-node", bench.File("farm"), "--once", "--name", "cli", "--threads", "1", "--poll", "0.5"], output, errors, Cancel));
        Assert.Contains("AVALightWave render node cli", output.ToString());
        Assert.Equal(2, output.ToString().Split('\n').Count(l => l.StartsWith("Rendered frame")));
        Assert.Contains("2 frames rendered, 0 failed", output.ToString());
        Assert.Equal("", errors.ToString());
        Assert.Equal(FarmJobState.Done, new FarmFolder(bench.File("farm")).Status(sent.Job.Id).State);

        Assert.True(FarmCommandLine.Wanted(["--render-node", "x"]));
        Assert.False(FarmCommandLine.Wanted(["--mcp"]));
        var help = new StringWriter();
        Assert.Equal(0, await FarmCommandLine.RunAsync(["--render-node", "--help"], help, errors, Cancel));
        Assert.Contains("AVALightWave --render-node FOLDER", help.ToString());
        var said = new StringWriter();
        Assert.Equal(1, await FarmCommandLine.RunAsync(["--render-node", bench.File("not-there"), "--once"], output, said, Cancel));
        Assert.Contains("There's no folder", said.ToString());
        Assert.False(Directory.Exists(bench.File("not-there")));
        Assert.Equal(1, await FarmCommandLine.RunAsync(["--render-node", bench.Path, "--threads", "many"], output, said, Cancel));
        Assert.Equal(1, await FarmCommandLine.RunAsync(["--render-node"], output, said, Cancel));
        Assert.Equal(1, await FarmCommandLine.RunAsync(["--render-node", bench.Path, "--frobnicate"], output, said, Cancel));
    }

    [Fact]
    public async Task The_local_queue_renders_in_the_background_until_stopped()
    {
        using var bench = new Workbench();
        await using var queue = new RenderQueue(bench.File("queue"));
        var lines = new List<string>();
        queue.Said += l => { lock (lines) lines.Add(l); };
        queue.Start(new FarmNodeOptions { Name = "local", Threads = 1, Poll = TimeSpan.FromMilliseconds(100), Heartbeat = TimeSpan.FromMilliseconds(100) });
        Assert.True(queue.Running);
        var sent = Send(bench, queue.Folder.Root, frames: 2);
        for (int i = 0; i < 600 && queue.Folder.Status(sent.Job.Id).State != FarmJobState.Done; i++) await Task.Delay(50, Cancel);
        Assert.Equal(FarmJobState.Done, queue.Folder.Status(sent.Job.Id).State);
        Assert.Contains(queue.Folder.Nodes(), n => n.Name == "local");
        await queue.StopAsync();
        Assert.False(queue.Running);
        Assert.Empty(queue.Folder.Nodes());
        Assert.Equal(2, lines.Count(l => l.StartsWith("Rendered frame")));
    }

    [Fact]
    public void Real_paths_follow_links_and_inside_means_inside()
    {
        using var bench = new Workbench();
        string a = Directory.CreateDirectory(bench.File("a")).FullName;
        Assert.True(FarmFiles.IsInside(Path.Combine(a, "x.png"), a));
        Assert.False(FarmFiles.IsInside(bench.File("ab"), a));
        Assert.False(FarmFiles.IsInside(bench.Path, a));
        Assert.Equal(Path.Combine(a, "missing", "x.png"), FarmFiles.RealPath(Path.Combine(a, "missing", "..", "missing", "x.png")));
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(bench.File("b"), "a");
        Assert.Equal(Path.Combine(a, "x.png"), FarmFiles.RealPath(Path.Combine(bench.File("b"), "x.png")));
        Assert.Throws<FarmRefusedException>(() => FarmJobLoader.Inside(a, "../ab/x.png", "An image"));
        Assert.Throws<FarmRefusedException>(() => FarmJobLoader.Inside(a, "/etc/hosts", "An image"));
        Assert.Equal(Path.Combine(a, "sub", "x.png"), FarmJobLoader.Inside(a, "sub\\x.png", "An image"));
    }
}
