using System.IO.Pipelines;
using AVALightWave.Mcp;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>
/// AVALightWave's Layout-only MCP server: the scene tools and its own (no modeling), its instructions and recipes, its
/// own tools' work and errors, the hub tools headless and with a window, and the --mcp command line.
/// </summary>
public class ServerTests
{
    internal static readonly string[] Own = ["replace_object", "save_object", "edit_in_modeler", "hub_status", "clone_item", "load_from_scene"];

    [Fact]
    public async Task It_serves_the_scene_tools_and_its_own_and_no_modeling_tool()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var tools = (await s.Client.ListToolsAsync(cancellationToken: Cancel)).Select(t => t.ProtocolTool).ToList();
        Assert.Equal(ToolGroups.Scene.Concat(Own).Order(), tools.Select(t => t.Name).Order());
        Assert.Empty(tools.Select(t => t.Name).Intersect(ToolGroups.Modeling));
        Assert.Equal("AVALightWave", s.Client.ServerInfo!.Name);
        Assert.Equal("0.1.0", s.Client.ServerInfo.Version);
        Assert.StartsWith("AVALightWave is a standalone scene", s.Client.ServerInstructions);
        Assert.DoesNotContain("run_tool", s.Client.ServerInstructions);
        // Descriptions speak of files, not of a Modeler object it doesn't have.
        Assert.StartsWith("Loads an object file", tools.Single(t => t.Name == "add_object").Description);
        Assert.DoesNotContain("Modeler's unsaved", tools.Single(t => t.Name == "save_scene").Description);
        Assert.Contains("save_object", tools.Single(t => t.Name == "save_scene").Description);
        Assert.Contains("no Modeler layers", tools.Single(t => t.Name == "set_morph").Description);
        var prompts = (await s.Client.ListPromptsAsync(cancellationToken: Cancel)).Select(p => p.Name).ToList();
        Assert.Equal(["camera_flyby", "light_sweep", "object_turntable", "stage_object", "three_point_lighting"], prompts.Order());
        // AVA Modeler's own server is as it was.
        Assert.Contains(ModelerServer.Definitions(), t => t.Name == "run_tool");
    }

    [Fact]
    public async Task Objects_come_from_files_and_add_object_without_one_says_so()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        Assert.Contains("give file", await s.Client.Fails("add_object"));
        var r = await s.Client.Json("add_object", new { file = "objects/pawn.lwo", name = "Pawn" });
        Assert.Equal("Pawn", (string?)r["name"]);
        Assert.Equal(3, b.Scene.Items.Count);
        Assert.Contains("outside the working folder", await s.Client.Fails("add_object", new { file = "../elsewhere.lwo" }));
    }

    [Fact]
    public async Task Replace_object_keeps_the_motion_and_is_one_undo_step()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("add_object", new { file = "objects/pawn.lwo" });
        await s.Client.Json("set_transform", new { item = "pawn", frame = 10, position = new[] { 1.0, 0, 0 }, key = true });
        var r = await s.Client.Json("replace_object", new { @object = "pawn", file = "objects/crate.lwo" });
        Assert.Equal("crate", (string?)r["name"]);
        Assert.Equal(8, (int?)r["points"]);
        var crate = b.Scene.Objects.Single();
        Assert.Equal(1, crate.PositionAt(10).X, 9);
        Assert.Equal("Agent: replaced an object", b.Host.Layout.History.UndoLabel);
        await s.Client.Json("undo_scene");
        Assert.Equal("pawn", b.Scene.Objects.Single().Name);
        Assert.Equal(482, b.Scene.Objects.Single().BaseMesh().Points.Count);
        Assert.Contains("not an object", await s.Client.Fails("replace_object", new { @object = "Camera", file = "objects/crate.lwo" }));
        Assert.Contains("no item \"Ghost\"", await s.Client.Fails("replace_object", new { @object = "Ghost", file = "objects/crate.lwo" }));
        Assert.Contains("no file", await s.Client.Fails("replace_object", new { @object = "pawn", file = "objects/none.lwo" }));
        // A failed call left no step.
        Assert.Equal(482, b.Scene.Objects.Single().BaseMesh().Points.Count);
    }

    [Fact]
    public async Task Save_object_writes_inside_the_working_folder_and_lws_scenes_then_refer_to_it()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("add_object", new { file = "objects/ball.lwo", name = "Ball" });
        var r = await s.Client.Json("save_object", new { @object = "Ball", path = "out/ball copy.lwo" });
        Assert.Equal(Path.Combine("out", "ball copy.lwo"), (string?)r["saved"]);
        Assert.True(File.Exists(b.Folder.File(Path.Combine("out", "ball copy.lwo"))));
        Assert.Contains("already exists", await s.Client.Fails("save_object", new { @object = "Ball", path = "out/ball copy.lwo" }));
        await s.Client.Json("save_object", new { @object = "Ball", path = "out/ball copy.lwo", overwrite = true });
        Assert.Contains("outside the working folder", await s.Client.Fails("save_object", new { @object = "Ball", path = "/tmp/x.lwo" }));
        Assert.Contains("are saved as", await s.Client.Fails("save_object", new { @object = "Ball", path = "ball.txt" }));
        await s.Client.Json("save_scene", new { path = "ball.lws" });
        var d = new SceneDocument();
        d.Load(b.Folder.File("ball.lws"));
        Assert.Empty(d.Session.Warnings);
        Assert.EndsWith("ball copy.lwo", d.Scene.Objects.Single().SourcePath);
    }

    private sealed class FakeApp : ILightWaveApp
    {
        public List<ObjectItem> Edited { get; } = [];
        public Task<string> EditInModeler(ObjectItem item) { Edited.Add(item); return Task.FromResult($"{item.Name} is open in AVA Modeler."); }
        public (bool OnHub, bool ModelerThere) HubState() => (true, false);
    }

    [Fact]
    public async Task The_hub_tools_work_in_the_window_and_say_why_not_headless()
    {
        using var b = new Bench();
        await using (var headless = await b.Serve())
        {
            Assert.Contains("open AVALightWave window", await headless.Client.Fails("edit_in_modeler", new { @object = "x" }));
            var st = await headless.Client.Json("hub_status");
            Assert.False((bool?)st["on_hub"]);
            Assert.Contains("headless", (string?)st["note"]);
        }
        var app = new FakeApp();
        await using var live = await b.Serve(app);
        await live.Client.Json("add_object", new { file = "objects/crate.lwo" });
        var r = await live.Client.Json("edit_in_modeler", new { @object = "crate" });
        Assert.Equal("crate is open in AVA Modeler.", (string?)r["result"]);
        Assert.Same(b.Scene.Objects.Single(), Assert.Single(app.Edited));
        Assert.Contains("not an object", await live.Client.Fails("edit_in_modeler", new { @object = "Light" }));
        var status = await live.Client.Json("hub_status");
        Assert.True((bool?)status["on_hub"]);
        Assert.False((bool?)status["modeler_running"]);
    }

    [Fact]
    public async Task The_command_line_serves_avalightwave_headless()
    {
        using var b = new Bench();
        var up = new Pipe();
        var down = new Pipe();
        var log = new StringWriter();
        var run = Task.Run(() => McpCommandLine.RunAsync(["--mcp", "--root", b.Folder.Path], _ => ToolRegistry.Create(), up.Reader.AsStream(), down.Writer.AsStream(), log, LightWaveServer.Profile(), Cancel));
        await using (var client = await McpClient.CreateAsync(new StreamClientTransport(up.Writer.AsStream(), down.Reader.AsStream()), null, null, Cancel))
        {
            Assert.Equal("AVALightWave", client.ServerInfo!.Name);
            await client.Json("add_object", new { file = "objects/pawn.lwo" });
            await client.Json("save_scene", new { path = "pawn.avascene" });
        }
        await up.Writer.CompleteAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), Cancel));
        Assert.True(File.Exists(b.Folder.File("pawn.avascene")));
        Assert.Contains("AVALightWave MCP server (headless)", log.ToString());
        var help = new StringWriter();
        Assert.Equal(0, await McpCommandLine.RunAsync(["--mcp", "--help"], _ => ToolRegistry.Create(), Stream.Null, Stream.Null, help, LightWaveServer.Profile(), Cancel));
        Assert.Contains("AVALightWave --mcp", help.ToString());
        Assert.Contains("no modeling", help.ToString());
    }

    [Fact]
    public void The_chat_is_told_about_avalightwave_and_its_core_tools_exist()
    {
        Assert.StartsWith(LightWaveServer.Instructions.TrimEnd()[..100], LightWaveChat.System);
        Assert.Contains("inside AVALightWave itself", LightWaveChat.System);
        var names = ModelerServer.Definitions(LightWaveServer.Profile()).Select(t => t.Name).ToHashSet();
        Assert.All(LightWaveChat.CoreTools, t => Assert.Contains(t, names));
        Assert.StartsWith("AVALightWave-mcp-", LightWaveServer.PipeName);
        Assert.NotEqual(LiveProtocol.PipeName, LightWaveServer.PipeName);
    }
}
