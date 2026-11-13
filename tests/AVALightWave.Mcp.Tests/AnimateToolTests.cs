using AVAModeler.Core;
using AVAModeler.Scene;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Motion in a phrase: animate's kinds, timing and mistakes, and camera_move.</summary>
public class AnimateToolTests
{
    private static async Task<(Bench B, InProcess S)> Stage(params string[] balls)
    {
        var b = new Bench();
        var s = await b.Serve();
        foreach (var name in balls) await s.Client.Json("make_object", new { shape = "ball", name, radius = 0.25 });
        return (b, s);
    }

    [Fact]
    public async Task A_move_eases_from_where_the_item_is_and_a_sequence_follows_on()
    {
        var (b, s) = await Stage("Ball");
        using var _ = b;
        await using var __ = s;
        var r = await s.Client.Json("animate", new
        {
            moves = new object[]
            {
                new { item = "Ball", kind = "move", to = new[] { 2.0, 0, 0 }, start = 1, end = 31, name = "across" },
                new { item = "Ball", kind = "move", by = new[] { 0.0, 1, 0 }, after = "across", duration = 40, ease = "linear" },
            },
        });
        var ball = b.Scene.Find("Ball")!;
        Assert.Equal(0, ball.PositionAt(1).X, 9);
        Assert.Equal(1, ball.PositionAt(16).X, 6); // half way at the middle (ease in and out is symmetric)
        Assert.True(ball.PositionAt(4).X < 0.1); // slow out of the start
        Assert.Equal(2, ball.PositionAt(31).X, 9);
        Assert.Equal(0.5, ball.PositionAt(51).Y, 6); // linear
        Assert.Equal(1, ball.PositionAt(71).Y, 9);
        Assert.True(ball.PositionAt(29).X > 1.95); // the first move's ease into frame 31 is kept
        Assert.Equal(71, (int)r["scene_frames"]![1]!);
        Assert.Contains("now runs to 71", (string?)r["note"]);
        Assert.Equal("Agent: animated", b.Host.Layout.History.UndoLabel);
        await s.Client.Json("undo_scene");
        Assert.Equal(0, b.Scene.Find("Ball")!.PositionAt(31).X, 9);
        Assert.Equal(60, b.Scene.LastFrame);
    }

    [Fact]
    public async Task A_bounce_keys_parabolas_that_never_sink_and_squashes_on_landing()
    {
        var (b, s) = await Stage("Ball");
        using var _ = b;
        await using var __ = s;
        await s.Client.Json("animate", new { moves = new object[] { new { item = "Ball", kind = "bounce", height = 2, count = 2, decay = 0.5, squash = 0.3, start = 1, end = 61 } } });
        var ball = b.Scene.Find("Ball")!;
        Assert.Equal(2, ball.PositionAt(1).Y, 9);
        double lowest = double.MaxValue, highestLater = 0;
        var landings = new List<double>();
        for (double f = 1; f <= 61; f += 0.25)
        {
            double at = ball.PositionAt(f).Y;
            lowest = Math.Min(lowest, at);
            if (f > 25) highestLater = Math.Max(highestLater, at);
            if (at < 1e-9) landings.Add(f);
        }
        Assert.True(lowest > -1e-6, $"it sank to {lowest}");
        Assert.InRange(highestLater, 0.4, 1.01); // the second bounce is lower than the first
        Assert.Equal(0, ball.PositionAt(61).Y, 9);
        // Squashed on the first landing: the y-envelope's first contact key.
        var y = ball.Channels[1];
        double contact = y.Keys.First(k => k.Frame > 1 && Math.Abs(k.Value) < 1e-9).Frame;
        Assert.Equal(0.7, ball.ScaleAt(contact).Y, 6);
        Assert.True(ball.ScaleAt(contact).X > 1.15);
        Assert.Equal(1, ball.ScaleAt(61).Y, 6);
    }

    [Fact]
    public async Task Spin_orbit_and_track_keep_their_geometry()
    {
        var (b, s) = await Stage("Sun", "Planet");
        using var _ = b;
        await using var __ = s;
        await s.Client.Json("set_transform", new { item = "Planet", position = new[] { 3.0, 0, 0 }, key = false });
        await s.Client.Json("animate", new
        {
            moves = new object[]
            {
                new { item = "Sun", kind = "spin", turns = 2, start = 1, end = 61 },
                new { item = "Planet", kind = "orbit", around = "Sun", turns = 1, start = 1, end = 61, face = "forward" },
                new { item = "Camera", kind = "track", target = "Planet", with = "previous" },
            },
        });
        var sun = b.Scene.Find("Sun")!;
        var planet = b.Scene.Find("Planet")!;
        var cam = b.Scene.Find("Camera")!;
        Assert.Equal(720, sun.RotationAt(61).X, 6);
        Assert.Equal(360, sun.RotationAt(31).X, 6);
        for (double f = 1; f <= 61; f += 0.5)
        {
            var p = planet.PositionAt(f);
            Assert.InRange(Math.Sqrt(p.X * p.X + p.Z * p.Z), 2.97, 3.03);
        }
        Assert.Equal(3, planet.PositionAt(61).X, 6);
        Assert.Equal(-3, planet.PositionAt(31).X, 2);
        // The camera faces the planet as it goes: its +Z axis points at it.
        foreach (var f in new[] { 1.0, 15, 30, 45, 61 })
        {
            var look = cam.WorldMatrix(f).TransformVector(Vec3.UnitZ).Normalized();
            var to = (planet.WorldPosition(f) - cam.WorldPosition(f)).Normalized();
            Assert.True(Vec3.Dot(look, to) > 0.995, $"frame {f}: {Vec3.Dot(look, to)}");
        }
    }

    [Fact]
    public async Task Through_pop_fade_sway_and_shake()
    {
        var (b, s) = await Stage("Ball", "Bell");
        using var _ = b;
        await using var __ = s;
        await s.Client.Json("animate", new
        {
            moves = new object[]
            {
                new { item = "Ball", kind = "pop_in", start = 1, duration = 10 },
                new { item = "Ball", kind = "through", points = new[] { new[] { 1.0, 0, 0 }, new[] { 1.0, 0, 1 }, new[] { 0.0, 0, 1 } }, after = "previous", duration = 30, ease = "linear" },
                new { item = "Ball", kind = "fade_out", after = "previous", duration = 10 },
                new { item = "Bell", kind = "sway", amplitude = 20, cycles = 2, start = 1, end = 41, damping = 1 },
                new { item = "Bell", kind = "shake", start = 41, duration = 12 },
            },
        });
        var ball = (ObjectItem)b.Scene.Find("Ball")!;
        Assert.Equal(0.01, ball.ScaleAt(1).X, 6);
        Assert.True(ball.ScaleAt(8).X > 1.02); // overshoots
        Assert.Equal(1, ball.ScaleAt(11).X, 6);
        Assert.Equal(1, ball.PositionAt(21).X, 6);
        Assert.Equal(0, ball.PositionAt(21).Z, 6);
        Assert.Equal(1, ball.PositionAt(31).X, 6);
        Assert.Equal(1, ball.PositionAt(31).Z, 6);
        Assert.Equal(0, ball.PositionAt(41).X, 6);
        Assert.Equal(0, ball.Dissolve.Evaluate(41), 6);
        Assert.Equal(1, ball.Dissolve.Evaluate(51), 6);
        var bell = b.Scene.Find("Bell")!;
        Assert.InRange(bell.RotationAt(6).Z, 17.2, 17.8);   // the first peak, a quarter of a cycle in, an eighth died away
        Assert.Equal(0, bell.RotationAt(41).Z, 6);   // damped away by the end
        Assert.NotEqual(0, bell.PositionAt(47).X);
        Assert.Equal(0, bell.PositionAt(53).X, 9);   // back where it was
    }

    [Fact]
    public async Task A_wrong_move_is_named_and_changes_nothing()
    {
        var (b, s) = await Stage("Ball");
        using var _ = b;
        await using var __ = s;
        int steps = b.Host.Layout.History.Count;
        async Task Fails(string expect, object move) =>
            Assert.Contains(expect, await s.Client.Fails("animate", new { moves = new[] { new { item = "Ball", kind = "spin" }, move } }));
        await Fails("Move 2 (wobble Ball)", new { item = "Ball", kind = "wobble" });
        await Fails("has no \"speed\"", new { item = "Ball", kind = "move", to = new[] { 1, 2, 3 }, speed = 3 });
        await Fails("no move named \"jump\"", new { item = "Ball", kind = "move", by = new[] { 1, 2, 3 }, after = "jump" });
        await Fails("no item \"Ghost\"", new { item = "Ghost", kind = "spin" });
        await Fails("not after it starts", new { item = "Ball", kind = "spin", start = 20, end = 10 });
        await Fails("to (a position", new { item = "Ball", kind = "move" });
        await Fails("only objects fade", new { item = "Camera", kind = "fade_in" });
        await Fails("ease must be one of", new { item = "Ball", kind = "spin", ease = "bouncy" });
        Assert.Contains("after the scene's last frame", await s.Client.Fails("animate", new { moves = new[] { new { item = "Ball", kind = "spin", end = 90 } }, extend = false }));
        Assert.Equal(steps, b.Host.Layout.History.Count);
        Assert.Equal(0, b.Scene.Find("Ball")!.RotationAt(30).X, 9);
    }

    [Fact]
    public async Task Camera_moves_keep_the_target_in_view()
    {
        var (b, s) = await Stage("Ball");
        using var _ = b;
        await using var __ = s;
        await s.Client.Json("set_transform", new { item = "Camera", position = new[] { 0.0, 1, -6 }, key = false });
        await s.Client.Json("look_at", new { item = "Camera", target = "Ball", key = false });
        var cam = b.Scene.Find("Camera")!;
        var ball = b.Scene.Find("Ball")!;
        await s.Client.Json("camera_move", new { kind = "push_in", target = "Ball", distance = 2, start = 1, end = 30 });
        Assert.Equal(2, (cam.WorldPosition(30) - ball.WorldPosition(30)).Length, 6);
        await s.Client.Json("camera_move", new { kind = "orbit", target = "Ball", turns = 0.25, start = 30, end = 60 });
        Assert.Equal(2, (cam.WorldPosition(60) - ball.WorldPosition(60)).Length, 2);
        foreach (var f in new[] { 10.0, 30, 45, 60 })
        {
            var look = cam.WorldMatrix(f).TransformVector(Vec3.UnitZ).Normalized();
            Assert.True(Vec3.Dot(look, (ball.WorldPosition(f) - cam.WorldPosition(f)).Normalized()) > 0.995, $"frame {f}");
        }
        await s.Client.Json("camera_move", new { kind = "crane", height = 1.5, target = "Ball", start = 60, duration = 20 });
        Assert.Equal(cam.WorldPosition(60).Y + 1.5, cam.WorldPosition(80).Y, 6);
        var h = await s.Client.Json("camera_move", new { kind = "handheld", amount = 0.5 });
        Assert.Single(cam.Modifiers);
        Assert.Contains("follow", await s.Client.Fails("camera_move", new { kind = "zoom" }));
        Assert.Contains("not a camera", await s.Client.Fails("camera_move", new { kind = "dolly", camera = "Ball", distance = 1 }));
        Assert.Contains("target is needed", await s.Client.Fails("camera_move", new { kind = "orbit" }));
        // Follow keeps its offset as the ball moves.
        await s.Client.Json("animate", new { moves = new[] { new { item = "Ball", kind = "move", to = new[] { 4.0, 0, 0 }, start = 1, end = 80 } } });
        await s.Client.Json("camera_move", new { kind = "follow", target = "Ball", offset = new[] { 0.0, 1, -3 }, start = 1, end = 80 });
        Assert.Equal(ball.WorldPosition(50).X, cam.WorldPosition(50).X, 1);
        Assert.Equal(-3, cam.WorldPosition(50).Z - ball.WorldPosition(50).Z, 1);
    }
}
