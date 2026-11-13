using System.Text.Json.Nodes;
using AVAModeler.Scene;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Whole shots, checked: build_scene, frame_items and check_shot.</summary>
public class ShotToolTests
{
    internal static object BouncePlan => new
    {
        settings = new { first_frame = 1, last_frame = 90 },
        objects = new object[]
        {
            new { shape = "ground", size = new[] { 12.0, 12 }, color = new[] { 120, 130, 120 } },
            new { shape = "ball", name = "Ball", radius = 0.4, color = new[] { 220, 40, 30 }, specular = 0.5, glossiness = 256, position = new[] { -1.5, 0, 0 } },
            new { name = "Crate", origin = "base", position = new[] { 1.5, 0, 0.5 }, steps = new object[]
            {
                new { call = "set_surface", args = new { name = "Wood", color = new[] { 150, 100, 60 }, assign = false } },
                new { tool = "Box", @params = new Dictionary<string, object> { ["Size"] = new[] { 1.0, 1, 1 }, ["Surface"] = "Wood" } },
            } },
        },
        lights = "three_point",
        camera = new { items = new[] { "all" }, from = "three_quarter", margin = 0.6 },
        motion = new object[] { new { item = "Ball", kind = "bounce", height = 1.5, count = 3, squash = 0.25, by = new[] { 1.0, 0, 0 }, start = 1, end = 90 } },
        camera_moves = new object[] { new { kind = "orbit", target = "Ball", degrees = 20, start = 1, end = 90 } },
    };

    [Fact]
    public async Task A_plan_builds_a_whole_shot_in_one_step_and_checks_it()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var r = await s.Client.Json("build_scene", new { plan = BouncePlan });
        Assert.Equal(3, r["objects"]!.AsArray().Count);
        Assert.Equal(["Key", "Fill", "Rim"], r["lights"]!["lights"]!.AsArray().Select(x => (string)x!));
        Assert.Null(b.Scene.Find("Light")); // the default light gave way to the rig
        Assert.Equal(90, b.Scene.LastFrame);
        Assert.Equal("Agent: built a scene", b.Host.Layout.History.UndoLabel);
        Assert.Equal(1, b.Host.Layout.History.Count);
        var check = r["check"]!;
        Assert.DoesNotContain(check["problems"]!.AsArray(), p => (string?)p!["kind"] is "never_in_view" or "out_of_view" or "unlit" or "below_ground" or "no_lights");
        // It renders: the ball in the middle of the bounce.
        var pic = await s.Client.Call("render_scene", new { frame = 30, width = 320, samples = 2 });
        var png = pic.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray();
        Assert.True(Colours(png) > 50);
        if (Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOT_OUT") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, "build_scene.png"), png);
        await s.Client.Json("undo_scene");
        Assert.Equal(["Camera", "Light"], b.Scene.Items.Select(i => i.Name).Order());
        Assert.Equal(60, b.Scene.LastFrame);
    }

    [Fact]
    public async Task A_failing_part_is_named_and_changes_nothing()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var e = await s.Client.Fails("build_scene", new { plan = new { objects = new object[] { new { shape = "ball" }, new { shape = "blob" } } } });
        Assert.Contains("objects[1] (make_object)", e);
        Assert.Contains("shape must be one of", e);
        Assert.Contains("no \"stuff\"", await s.Client.Fails("build_scene", new { plan = new { stuff = 1 } }));
        Assert.Contains("no rig \"disco\"", await s.Client.Fails("build_scene", new { plan = new { lights = "disco" } }));
        Assert.Contains("motion failed: Move 1", await s.Client.Fails("build_scene", new { plan = new { objects = new[] { new { shape = "ball" } }, motion = new[] { new { item = "Ball", kind = "zoom" } } } }));
        Assert.Contains("go in an object's steps", await s.Client.Fails("build_scene", new { plan = new { steps = new[] { new { tool = "Box" } } } }));
        Assert.Equal(2, b.Scene.Items.Count);
        Assert.False(b.Host.Layout.History.CanUndo);
    }

    [Fact]
    public async Task Frame_items_fits_them_and_follow_keeps_a_moving_one_framed()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "box", name = "Crate", position = new[] { 5.0, 0, 8 } });
        var r = await s.Client.Json("frame_items", new { items = new[] { "Crate" }, from = "front" });
        var cam = (CameraItem)b.Scene.Find("Camera")!;
        var crate = b.Scene.Find("Crate")!;
        var (where, fill) = Shot.InPicture(cam, crate, 1);
        Assert.Equal("in", where);
        Assert.InRange(fill, 0.35, 0.95);
        Assert.False((bool)r["keyed"]!);
        // A moving crate, followed.
        await s.Client.Json("animate", new { moves = new[] { new { item = "Crate", kind = "move", by = new[] { 20.0, 0, 0 }, start = 1, end = 60 } } });
        Assert.NotEqual("in", Shot.InPicture(cam, crate, 60).Where);
        await s.Client.Json("frame_items", new { items = new[] { "Crate" }, start = 1, end = 60, follow = true, every = 5 });
        foreach (var f in new[] { 1.0, 17, 33, 47, 60 }) Assert.Equal("in", Shot.InPicture(cam, crate, f).Where);
        // Fitting over the span from one place instead.
        await s.Client.Json("undo_scene");
        await s.Client.Json("frame_items", new { items = new[] { "Crate" }, start = 1, end = 60, from = "front" });
        foreach (var f in new[] { 1.0, 30, 60 }) Assert.Equal("in", Shot.InPicture(cam, crate, f).Where);
        Assert.Contains("isn't a camera", await s.Client.Fails("frame_items", new { camera = "Crate" }));
        Assert.Contains("front, three_quarter", await s.Client.Fails("frame_items", new { from = "sideways" }));
    }

    [Fact]
    public async Task Check_shot_finds_what_to_fix()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ground" });
        await s.Client.Json("make_object", new { shape = "ball", name = "Sunk", position = new[] { 0.0, -0.3, 0 } });
        await s.Client.Json("make_object", new { shape = "box", name = "Away", position = new[] { 0.0, 0, -40 } });
        await s.Client.Json("set_item", new { item = "Light", intensity = 0 });
        await s.Client.Json("frame_items", new { items = new[] { "Sunk" } });
        await s.Client.Json("set_key", new { item = "Sunk", channel = "heading", frame = 1, value = 0 });
        await s.Client.Json("set_key", new { item = "Sunk", channel = "heading", frame = 50, value = 90 });
        b.Scene.Find("Sunk")!.Channels[3].AddKey(80, 180); // a key the scene's frames don't reach
        var r = await s.Client.Json("check_shot");
        var kinds = r["problems"]!.AsArray().Select(p => (string?)p!["kind"] + ":" + (string?)p["item"]).ToList();
        Assert.Contains("below_ground:Sunk", kinds);
        Assert.Contains("never_in_view:Away", kinds);
        Assert.Contains("keys_after_end:Sunk", kinds);
        Assert.Contains("no_lights:", kinds);
        Assert.Contains("unlit:Sunk", kinds);
        Assert.False((bool)r["ok"]!);
        Assert.DoesNotContain("never_in_view:Sunk", kinds);
        // The camera inside the box.
        await s.Client.Json("set_transform", new { item = "Camera", position = new[] { 0.0, 0.5, -40 }, key = false });
        var inside = await s.Client.Json("check_shot", new { every = 10 });
        Assert.Contains(inside["problems"]!.AsArray(), p => (string?)p!["kind"] == "camera_inside" && (string?)p["item"] == "Away");
        Assert.False(b.Host.Layout.History.UndoLabel == "Agent: checked the shot");
    }

    [Fact]
    public async Task The_checks_use_real_shapes_not_boxes()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ground" });
        // A ring round a pole: their boxes overlap, their shapes don't.
        await s.Client.Json("make_object", new { shape = "cylinder", name = "Pole", radius = 0.1, height = 2 });
        await s.Client.Json("make_object", new { shape = "torus", name = "Ring", radius = 0.6, thickness = 0.08, origin = "center", position = new[] { 0.0, 1, 0 } });
        // A ball turned 45°: its box dips below the ground, the ball doesn't.
        await s.Client.Json("make_object", new { shape = "ball", name = "Turned", radius = 0.4, origin = "center", position = new[] { 3.0, 0.4, 0 }, rotation = new[] { 0.0, 45, 45 } });
        // A ball resting on a box: touching, not passing through.
        await s.Client.Json("make_object", new { shape = "box", name = "Block", size = new[] { 1.0, 1, 1 }, position = new[] { -3.0, 0, 0 } });
        await s.Client.Json("make_object", new { shape = "ball", name = "Resting", radius = 0.3, position = new[] { -3.0, 1, 0 } });
        // Two balls truly into each other.
        await s.Client.Json("make_object", new { shape = "ball", name = "A", radius = 0.4, position = new[] { 0.0, 0, 3 } });
        await s.Client.Json("make_object", new { shape = "ball", name = "B", radius = 0.4, position = new[] { 0.3, 0, 3 } });
        await s.Client.Json("frame_items", new { from = "three_quarter" });
        var r = await s.Client.Json("check_shot", new { every = 30 });
        var kinds = r["problems"]!.AsArray().Select(p => (string?)p!["kind"] + ":" + (string?)p["item"]).ToList();
        Assert.Contains("overlap:A and B", kinds);
        Assert.DoesNotContain(kinds, k => k.Contains("Ring", StringComparison.Ordinal));
        Assert.DoesNotContain(kinds, k => k.Contains("Resting", StringComparison.Ordinal));
        Assert.DoesNotContain("below_ground:Turned", kinds);
        // A ball sunk for real is still found.
        await s.Client.Json("set_transform", new { item = "Turned", position = new[] { 3.0, 0.2, 0 }, key = false });
        r = await s.Client.Json("check_shot", new { every = 30 });
        Assert.Contains(r["problems"]!.AsArray(), p => (string?)p!["kind"] == "below_ground" && (string?)p["item"] == "Turned");
    }
}
