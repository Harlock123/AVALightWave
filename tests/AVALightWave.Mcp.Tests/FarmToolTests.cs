using AVAModeler.Scene;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>queue_render and render_queue_status: jobs sent to a farm folder inside the working folder, their progress and the nodes.</summary>
public class FarmToolTests
{
    private static async Task Stage(InProcess s)
    {
        await s.Client.Json("add_object", new { file = "objects/crate.lwo" });
        await s.Client.Json("set_scene", new { first_frame = 1, last_frame = 3 });
    }

    [Fact]
    public async Task Queue_render_sends_the_scene_and_the_status_follows_it()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await Stage(s);
        long version = b.Scene.Version;
        string? undo = b.Host.Layout.History.UndoLabel;
        var r = await s.Client.Json("queue_render", new { width = 32, height = 24, name = "crate", job_name = "Crate test", denoise = true });
        string id = (string)r["job"]!;
        Assert.Contains("\"denoise\": true", File.ReadAllText(b.Folder.File($"render-queue/jobs/{id}/job.json")));
        Assert.Equal("render-queue", (string?)r["folder"]);
        Assert.Equal(3, (int?)r["frames"]);
        Assert.Equal($"render-queue/jobs/{id}/out", (string?)r["output_folder"]);
        Assert.Equal($"render-queue/jobs/{id}/out/crate0001.png", (string?)r["first_file"]);
        Assert.Contains("--render-node", (string?)r["note"]);
        Assert.Equal(0, (int?)r["nodes_working"]);
        // Sending changes nothing in the scene: no undo step.
        Assert.Equal(version, b.Scene.Version);
        Assert.Equal(undo, b.Host.Layout.History.UndoLabel);

        var st = await s.Client.Json("render_queue_status");
        var job = st["jobs"]!.AsArray().Single()!;
        Assert.Equal(("Crate test", "waiting", 0, 3), ((string?)job["name"], (string?)job["state"], (int)job["done"]!, (int)job["total"]!));
        Assert.Empty(st["nodes"]!.AsArray());
        Assert.Contains("AVALightWave --render-node", (string?)st["node_command"]);

        var farm = b.Folder.File("render-queue");
        var node = new FarmNode(farm, new FarmNodeOptions { Once = true, Threads = 1, Name = "test node" });
        Assert.Equal(3, (await node.RunAsync(Cancel)).Rendered);
        st = await s.Client.Json("render_queue_status");
        job = st["jobs"]!.AsArray().Single()!;
        Assert.Equal(("done", 3), ((string?)job["state"], (int)job["done"]!));
        Assert.True(File.Exists(b.Folder.File(Path.Combine("render-queue", "jobs", id, "out", "crate0003.png"))));
    }

    [Fact]
    public async Task Queue_render_keeps_to_the_working_folder_and_checks_its_settings()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await Stage(s);
        Assert.Contains("outside the working folder", await s.Client.Fails("queue_render", new { folder = "../farm" }));
        Assert.Contains("outside the working folder", await s.Client.Fails("render_queue_status", new { folder = "/tmp" }));
        Assert.Contains("samples must be", await s.Client.Fails("queue_render", new { folder = "farm", samples = 0 }));
        Assert.Contains("movie must be", await s.Client.Fails("queue_render", new { folder = "farm", movie = "avi" }));
        Assert.Contains("plain file name", await s.Client.Fails("queue_render", new { folder = "farm", name = "../x" }));
        Assert.Contains("before the first", await s.Client.Fails("queue_render", new { folder = "farm", first = 5, last = 2 }));
        Assert.False(Directory.Exists(b.Folder.File(Path.Combine("farm", "jobs"))) && Directory.GetDirectories(b.Folder.File(Path.Combine("farm", "jobs"))).Length > 0);
        var r = await s.Client.Json("queue_render", new { folder = "farm/a", first = 2, last = 3, width = 16, height = 16 });
        Assert.Equal("farm/a", (string?)r["folder"]);
        Assert.Equal(2, (int?)r["frames"]);
        var st = await s.Client.Json("render_queue_status", new { folder = "farm/a" });
        Assert.Single(st["jobs"]!.AsArray());
        // An empty or missing folder is an empty queue.
        st = await s.Client.Json("render_queue_status", new { folder = "farm/none" });
        Assert.Empty(st["jobs"]!.AsArray());
    }

    private sealed class QueueApp(string queue) : ILightWaveApp
    {
        public Task<string> EditInModeler(ObjectItem item) => Task.FromResult("");
        public (bool OnHub, bool ModelerThere) HubState() => (false, false);
        public string? QueueFolder => queue;
    }

    [Fact]
    public async Task In_the_window_jobs_go_to_its_render_queue()
    {
        using var b = new Bench();
        using var elsewhere = new Core.Tests.Workbench();
        await using var s = await b.Serve(new QueueApp(elsewhere.File("queue")));
        await Stage(s);
        var r = await s.Client.Json("queue_render", new { width = 16, height = 16, last = 1 });
        Assert.Equal(elsewhere.File("queue"), (string?)r["folder"]);
        Assert.Single(new FarmFolder(elsewhere.File("queue")).Scan());
        Assert.False(Directory.Exists(b.Folder.File("render-queue")));
    }
}
