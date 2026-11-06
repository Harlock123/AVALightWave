using System.Text.Json;
using System.Text.RegularExpressions;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>
/// AVALightWave's recipes: each fetched through a client and followed call by call through the server, as an agent
/// would (a wrong tool, argument or value fails here), on a working folder with the sample objects; then the result is
/// checked.
/// </summary>
public class RecipeTests
{
    public static TheoryData<string, string> Cases => new()
    {
        { "stage_object", """{"file": "objects/crate.lwo", "width": "64"}""" },
        { "object_turntable", """{"frames": "4", "width": "48"}""" },
        { "camera_flyby", """{"frames": "12"}""" },
        { "light_sweep", """{"frames": "12", "color": "blue"}""" },
        { "render_on_farm", """{"frames": "4", "width": "48", "samples": "1"}""" },
        { "three_point_lighting", """{"target": "pawn", "width": "64"}""" },
        { "clone_stagger", """{"copies": "3"}""" },
        { "light_beams", """{"samples": "1"}""" },
        { "composite_shot", "{}" },
        { "rig_from_skelegons", "{}" },
        { "follow_and_jitter", """{"lag": "6"}""" },
        { "morph_and_macro", """{"crates": "5"}""" },
    };

    internal static List<(string Tool, Dictionary<string, object?> Args)> CallsIn(string text) =>
        text.Split('\n').Select(l => Regex.Replace(l.Trim(), @"^\d+\.\s+", "")).Where(l => l.StartsWith(Recipes.CallMarker, StringComparison.Ordinal)).Select(l =>
        {
            string call = l[Recipes.CallMarker.Length..];
            int space = call.IndexOf(' ');
            var args = JsonDocument.Parse(call[(space + 1)..]).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
            return (call[..space], args);
        }).ToList();

    private static string PromptText(GetPromptResult r) => Assert.IsType<TextContentBlock>(Assert.Single(r.Messages).Content).Text;

    [Fact]
    public async Task Every_recipe_calls_only_tools_the_server_has_and_reads_its_arguments()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var tools = (await s.Client.ListToolsAsync(cancellationToken: Cancel)).Select(t => t.Name).ToHashSet();
        foreach (var name in new[] { "stage_object", "object_turntable", "camera_flyby", "light_sweep", "render_on_farm", "clone_stagger", "light_beams", "composite_shot", "rig_from_skelegons", "follow_and_jitter", "morph_and_macro" })
        {
            var prompt = (await s.Client.ListPromptsAsync(cancellationToken: Cancel)).Single(p => p.Name == name);
            Assert.False(string.IsNullOrWhiteSpace(prompt.Description));
            var calls = CallsIn(PromptText(await s.Client.GetPromptAsync(name, cancellationToken: Cancel)));
            Assert.True(calls.Count >= 5, name);
            Assert.All(calls, c => Assert.Contains(c.Tool, tools));
        }
        var e = await Assert.ThrowsAsync<McpProtocolException>(async () => await s.Client.GetPromptAsync("camera_flyby", new Dictionary<string, object?> { ["frames"] = "lots" }, cancellationToken: Cancel));
        Assert.Contains("frames must be a number", e.Message);
        e = await Assert.ThrowsAsync<McpProtocolException>(async () => await s.Client.GetPromptAsync("object_turntable", new Dictionary<string, object?> { ["movie"] = "avi" }, cancellationToken: Cancel));
        Assert.Contains("movie must be", e.Message);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Each_recipe_runs_through_the_server(string name, string arguments)
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        // Three-point lighting lights what's there: a pawn.
        if (name == "three_point_lighting") await s.Client.Json("add_object", new { file = "objects/pawn.lwo" });
        var args = JsonDocument.Parse(arguments).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.GetString());
        var text = PromptText(await s.Client.GetPromptAsync(name, args, cancellationToken: Cancel));
        byte[]? last = null;
        int step = 0;
        foreach (var (tool, callArgs) in CallsIn(text))
        {
            step++;
            var result = await s.Client.CallToolAsync(tool, callArgs, cancellationToken: Cancel);
            Assert.True(result.IsError != true, $"{name} step {step}, {tool}, failed: {Text(result)}");
            if (result.Content.OfType<ImageContentBlock>().FirstOrDefault() is { } image) last = image.DecodedData.ToArray();
        }
        Assert.NotNull(last);
        Assert.True(Colours(last) > 8, $"{name}'s last picture is blank.");
        Check(name, b);
    }

    private static void Check(string name, Bench b)
    {
        var scene = b.Scene;
        switch (name)
        {
            case "stage_object":
                Assert.Equal("Subject", scene.Objects.Single().Name);
                Assert.Equal(8, scene.Objects.Single().BaseMesh().Points.Count);
                Assert.Equal(["Key", "Fill", "Rim"], scene.Lights.Select(l => l.Name));
                break;
            case "object_turntable":
                var table = scene.Find("Turntable")!;
                Assert.Same(table, scene.Find("Subject")!.Parent);
                Assert.Equal(270, table.RotationAt(4).X, 6);
                Assert.True(File.Exists(b.Folder.File(Path.Combine("turntable", "turntable0004.png"))));
                break;
            case "camera_flyby":
                var cam = scene.CurrentCamera!;
                Assert.NotNull(cam.Path);
                Assert.Equal(4, cam.Path!.Points.Count);
                Assert.False(cam.Path.AlignToPath);
                Assert.True(scene.Lights.Count() == 3);
                // It moves: the start and the end are metres apart.
                Assert.True((cam.WorldPosition(1) - cam.WorldPosition(12)).Length > 2);
                break;
            case "light_sweep":
                var spot = Assert.IsType<LightItem>(scene.Find("Sweep"));
                Assert.Equal(LightType.Spot, spot.Type);
                Assert.Equal(0, spot.Intensity.Evaluate(1), 6);
                Assert.Equal(1.4, spot.Intensity.Evaluate(4), 6);
                Assert.Equal(-35, spot.RotationAt(1).X, 6);
                Assert.Equal(35, spot.RotationAt(12).X, 6);
                Assert.Null(scene.Find("Light"));
                break;
            case "clone_stagger":
                Assert.Equal(["Hopper", "Hopper (2)", "Hopper (3)", "Hopper (4)"], scene.Objects.Skip(1).Select(o => o.Name));
                var hop = scene.Find("Hopper")!;
                var third = scene.Find("Hopper (3)")!;
                Assert.Equal(hop.PositionAt(7).Y, third.PositionAt(13).Y, 6);
                Assert.Equal(hop.PositionAt(1).X + 1.8, third.PositionAt(1).X, 6);
                Assert.Equal(AVAModeler.Scene.SpanKind.Bezier, hop[AVAModeler.Scene.Channel.PositionY].Keys[1].Kind);
                Assert.Equal(10, Directory.GetFiles(b.Folder.File("preview"), "stagger*.png").Length);
                break;
            case "light_beams":
                var beam = Assert.IsType<LightItem>(scene.Find("Beam"));
                Assert.True(beam.ShadowMap.On && beam.Volumetric.On && beam.Flare.On);
                Assert.Equal(1024, beam.ShadowMap.Size);
                Assert.Null(scene.Find("Light"));
                break;
            case "composite_shot":
                Assert.Equal(2, scene.Images.Count);
                Assert.EndsWith("sky.png", scene.Compositing.Background);
                Assert.Equal(AVAModeler.Scene.ForegroundKey.Color, scene.Compositing.Key);
                var subject = (ObjectItem)scene.Find("Subject")!;
                Assert.Equal(1, subject.DissolveAt(1), 6);
                Assert.Equal(0, subject.DissolveAt(18), 6);
                break;
            case "rig_from_skelegons":
                Assert.Equal(["Upper", "Forearm"], scene.Bones.Select(x => x.Name));
                var fore = scene.Find("Forearm")!;
                Assert.Equal(0, fore.RotationAt(9.5).Z, 6);
                Assert.Equal(50, fore.RotationAt(10).Z, 6);
                Assert.Equal(35, scene.Find("Upper")!.RotationAt(15).Z, 6);
                break;
            case "follow_and_jitter":
                var leader = scene.Find("Leader")!;
                var follower = scene.Find("Follower")!;
                Assert.Equal(leader.WorldPosition(24).X, follower.WorldPosition(30).X, 6);
                Assert.Single(scene.Find("Nervous")!.Modifiers);
                Assert.Single(scene.CurrentCamera!.Modifiers);
                Assert.True(File.Exists(b.Folder.File(Path.Combine("motions", "leader.mot"))));
                Assert.Equal(leader.PositionAt(20).X, scene.Find("Copy")!.PositionAt(20).X, 6);
                break;
            case "morph_and_macro":
                var pawn = (ObjectItem)scene.Find("Pawn")!;
                Assert.Equal("Squash", Assert.Single(pawn.Morphs).Name);
                Assert.True(pawn.DeformedMesh(15).Bounds().Max.Y < pawn.BaseMesh().Bounds().Max.Y * 0.7);
                Assert.Equal(6, scene.Objects.Count());
                break;
            case "render_on_farm":
                // The job waits in the farm folder until a node renders it; one does here.
                var farm = new FarmFolder(b.Folder.File("farm"));
                var job = Assert.Single(farm.Scan());
                Assert.Equal(("Turntable", FarmJobState.Waiting, 4), (job.Name, job.State, job.Total));
                Assert.Equal((48, 36), (job.Job!.Width, job.Job.Height));
                var rendered = FarmNode.RunAsync(farm.Root, new FarmNodeOptions { Once = true, Threads = 1 }, Cancel).GetAwaiter().GetResult();
                Assert.Equal(4, rendered.Rendered);
                Assert.Equal(FarmJobState.Done, farm.Status(job.Id).State);
                Assert.True(File.Exists(Path.Combine(job.OutputFolder, "turntable0004.png")));
                break;
            case "three_point_lighting":
                Assert.Equal(["Key", "Fill", "Rim"], scene.Lights.Select(l => l.Name));
                break;
        }
    }
}
