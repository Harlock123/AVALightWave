using AVAModeler.Core;
using AVAModeler.Scene;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Action clips: kept from a span, placed again faster, looped, blended, onto another character, and in files.</summary>
public class ActionTests
{
    [Fact]
    public async Task A_hop_is_kept_and_placed_faster_looped_and_blended()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ball", name = "Ball" });
        await s.Client.Json("animate", new { moves = new[] { new { item = "Ball", kind = "bounce", height = 1, count = 0, start_at = "ground", start = 1, end = 21 } } });
        var ball = b.Scene.Find("Ball")!;
        double top = ball.PositionAt(11).Y;
        var r = await s.Client.Json("save_action", new { name = "hop", items = new[] { "Ball" }, from = 1, to = 21, file = "clips/hop" });
        Assert.Equal(20, (double)r["frames"]!);
        Assert.True(File.Exists(b.Folder.File("clips/hop.action")));
        Assert.Equal("Agent: kept an action", b.Host.Layout.History.UndoLabel);

        // Twice as fast, three times, from frame 40.
        var placed = await s.Client.Json("place_action", new { name = "hop", at = 40, speed = 2, loops = 3 });
        Assert.Equal(70, (double)placed["frames"]![1]!);
        Assert.Equal(top, ball.PositionAt(45).Y, 6);
        Assert.Equal(top, ball.PositionAt(65).Y, 6);
        Assert.Equal(0, ball.PositionAt(60).Y, 6);
        Assert.Equal(70, b.Scene.LastFrame);

        // Blended in over 6 frames from a raised hold: it starts where it was and is the clip's by the end of the blend.
        await s.Client.Json("set_scene", new { last_frame = 120 });
        await s.Client.Json("set_key", new { item = "Ball", channel = "position.y", frame = 75, value = 3 });
        await s.Client.Json("set_key", new { item = "Ball", channel = "position.y", frame = 80, value = 3 });
        await s.Client.Json("place_action", new { name = "hop", at = 80, blend = 6 });
        Assert.Equal(3, ball.PositionAt(80).Y, 6);
        Assert.Equal(top, ball.PositionAt(90).Y, 6);

        var list = await s.Client.Json("list_actions");
        Assert.Equal("hop", (string?)list["actions"]![0]!["name"]);
        await s.Client.Json("place_action", new { file = "clips/hop.action", at = 100 });
        Assert.Equal(top, ball.PositionAt(110).Y, 6);
        Assert.Contains("no action \"jump\"", await s.Client.Fails("place_action", new { name = "jump", at = 1 }));
        Assert.Contains("one of them", await s.Client.Fails("place_action", new { at = 1 }));
        Assert.Contains("Nothing there is animated", await s.Client.Fails("save_action", new { name = "still", items = new[] { "Camera" }, from = 1, to = 10 }));
    }

    [Fact]
    public async Task A_bone_clip_goes_onto_another_character()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "box", name = "Robot" });
        await s.Client.Json("make_object", new { shape = "box", name = "Robot 2", position = new[] { 3.0, 0, 0 } });
        var robot = (ObjectItem)b.Scene.Find("Robot")!;
        var other = (ObjectItem)b.Scene.Find("Robot 2")!;
        var arm = b.Scene.AddBone(robot, "Arm");
        var arm2 = b.Scene.AddBone(other, "Arm");
        await s.Client.Json("animate", new { moves = new[] { new { item = "#" + b.Scene.Items.ToList().IndexOf(arm), kind = "sway", channel = "bank", amplitude = 40, cycles = 1, start = 1, end = 31 } } });
        await s.Client.Json("save_action", new { name = "wave", items = new[] { "Robot" }, from = 1, to = 31 });
        var r = await s.Client.Json("place_action", new { name = "wave", at = 1, onto = "Robot 2" });
        Assert.Equal(1, (int)r["channels"]!);
        Assert.Equal(arm.RotationAt(9).Z, arm2.RotationAt(9).Z, 6);
        Assert.NotEqual(0, arm2.RotationAt(9).Z);
    }
}
