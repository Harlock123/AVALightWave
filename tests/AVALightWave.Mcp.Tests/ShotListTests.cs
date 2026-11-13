using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Shot lists: a cut of shots, each through its own camera, built in one step and rendered as one sequence.</summary>
public class ShotListTests
{
    [Fact]
    public async Task A_cut_of_two_shots_is_built_and_rendered_in_order()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("build_scene", new
        {
            plan = new
            {
                objects = new object[] { new { shape = "ground" }, new { shape = "ball", name = "Ball", color = new[] { 220, 40, 30 } } },
                lights = "three_point",
                motion = new[] { new { item = "Ball", kind = "bounce", height = 1.5, start = 1, end = 40 } },
            },
        });
        int steps = b.Host.Layout.History.Count;
        var r = await s.Client.Json("build_shots", new
        {
            shots = new object[]
            {
                new { name = "Wide", duration = 20, camera = new { items = new[] { "all" }, from = "front" } },
                new { name = "Close", duration = 20, camera = new { items = new[] { "Ball" }, from = "three_quarter", margin = 0.3 },
                      camera_moves = new[] { new { kind = "orbit", target = "Ball", degrees = 30 } } },
            },
        });
        Assert.Equal(steps + 1, b.Host.Layout.History.Count);
        Assert.Equal(["Wide Camera", "Close Camera"], r["shots"]!.AsArray().Select(x => (string)x!["camera"]!));
        Assert.Equal(21, (int)r["shots"]![1]!["frames"]![0]!);
        Assert.Equal(2, LightWaveTools.Cuts(b.Scene).Count);
        Assert.Equal("Wide Camera", b.Scene.CurrentCamera!.Name);

        var shots = await s.Client.Call("render_shots", new { width = 64 });
        Assert.True(shots.IsError != true, Text(shots));
        Assert.Equal(40, Directory.GetFiles(b.Folder.File("shots"), "shot*.png").Length);
        Assert.True(File.Exists(b.Folder.File("shots/shot0040.png")));
        Assert.True(Colours(shots.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray()) > 10);

        Assert.Contains("Shot 2 (Close) failed", await s.Client.Fails("build_shots", new { shots = new object[] { new { name = "A" }, new { name = "Close", lens = 3 } } }));
        await s.Client.Json("undo_scene");
        Assert.Empty(LightWaveTools.Cuts(b.Scene));
        Assert.Null(b.Scene.Find("Wide Camera"));
        Assert.Contains("no shots", await s.Client.Fails("render_shots"));
    }
}
