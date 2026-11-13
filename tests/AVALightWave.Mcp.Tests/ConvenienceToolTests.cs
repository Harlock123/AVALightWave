using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Conveniences: camera bookmarks and packaging a scene.</summary>
public class ConvenienceToolTests
{
    [Fact]
    public async Task Bookmarks_are_saved_listed_recalled_and_removed()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("set_transform", new { item = "Camera", position = new[] { 2.0, 1, -6 }, key = false });
        var saved = await s.Client.Json("camera_bookmark", new { action = "save", name = "Wide" });
        Assert.Equal(2, (double)saved["saved"]!["position"]![0]!, 6);
        Assert.Equal("Agent: save a bookmark", b.Host.Layout.History.UndoLabel);
        await s.Client.Json("set_transform", new { item = "Camera", position = new[] { 0.0, 0, 0 }, key = false });
        var back = await s.Client.Json("camera_bookmark", new { action = "recall", name = "Wide", frame = 10 });
        Assert.False((bool)back["keyed"]!);
        Assert.Equal(-6, b.Scene.CurrentCamera!.PositionAt(10).Z, 9);
        var list = await s.Client.Json("camera_bookmark", new { action = "list" });
        Assert.Single(list["bookmarks"]!.AsArray());
        await s.Client.Json("camera_bookmark", new { action = "remove", name = "Wide" });
        Assert.Contains("no camera bookmarks", await s.Client.Fails("camera_bookmark", new { action = "recall", name = "Wide" }));
        Assert.Contains("action must be", await s.Client.Fails("camera_bookmark", new { action = "jump" }));
        Assert.Contains("isn't a camera", await s.Client.Fails("camera_bookmark", new { action = "save", name = "X", camera = "Light" }));
    }
}
