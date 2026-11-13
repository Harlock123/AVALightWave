using AVAModeler.Core;
using AVAModeler.Scene;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Characters: a jointed figure made in the scene, and walking it along a path with its feet planted.</summary>
public class WalkTests
{
    private static Vec3 Ankle(Bone shin, double f) => shin.WorldMatrix(f).TransformPoint(new Vec3(0, 0, shin.RestLength));

    [Fact]
    public async Task A_character_walks_to_its_point_with_feet_planted_and_knees_forward()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ground", size = new[] { 20.0, 20 } });
        var made = await s.Client.Json("make_character", new { name = "Walker" });
        Assert.Equal(12, made["parts"]!.AsArray().Count);
        var rig = (ObjectItem)b.Scene.Find("Walker")!;
        var shinR = rig.Bones.First(x => x.Name == "Shin R");
        var thighR = rig.Bones.First(x => x.Name == "Thigh R");
        Assert.True((Ankle(shinR, 1) - shinR.IKGoal!.WorldPosition(1)).Length < 0.01, "the leg doesn't reach its foot");

        var r = await s.Client.Json("walk", new { character = "Walker", points = new[] { new[] { 0.0, 0, 3 }, new[] { 2.0, 0, 3 } }, start = 1, speed = 1.2 });
        double end = (double)r["frames"]![1]!;
        Assert.Equal("Agent: walked a character", b.Host.Layout.History.UndoLabel);
        Assert.True(b.Scene.LastFrame >= end);
        var at = rig.WorldPosition(end);
        Assert.True(Math.Abs(at.X - 2) < 0.05 && Math.Abs(at.Z - 3) < 0.05, $"it ended at {at}");
        Assert.Equal(90, rig.RotationAt(end).X, 1); // facing +X along the last leg

        var goal = shinR.IKGoal!;
        int still = 0, lifted = 0;
        for (double f = 2; f < end; f++)
        {
            var g0 = goal.WorldPosition(f - 1);
            var g1 = goal.WorldPosition(f);
            if ((g1 - g0).Length < 1e-6) still++;
            if (g1.Y > 0.1) lifted++;
            // The ankle is on its goal at every frame (the legs are solved exactly), round the corner too.
            Assert.True((Ankle(shinR, f) - g1).Length < 0.01, $"frame {f}: the foot is off its goal by {(Ankle(shinR, f) - g1).Length}");
        }
        Assert.True(still > end / 4, "a foot never stands still");
        Assert.True(lifted > 3, "a foot never lifts");
        double f9 = 9;
        var forward = rig.WorldMatrix(f9).TransformVector(Vec3.UnitZ).Normalized();
        var hip = thighR.WorldPosition(f9);
        var knee = shinR.WorldPosition(f9);
        var ankle = Ankle(shinR, f9);
        Assert.True(Vec3.Dot(knee - (hip + ankle) * 0.5, forward) > 0, "the knee bends backwards");
    }

    [Fact]
    public async Task A_walk_climbs_steps_and_rigs_without_legs_are_refused()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ground", size = new[] { 20.0, 20 } });
        await s.Client.Json("make_object", new { shape = "steps", name = "Stairs", count = 3, size = new[] { 1.5, 0.12, 0.4 }, position = new[] { 0.0, 0, 1.0 } });
        await s.Client.Json("make_character", new { name = "Climber" });
        // The stairs run from z 0.4 to 1.6; the top step is z 1.2 to 1.6, 0.36 high.
        var r = await s.Client.Json("walk", new { character = "Climber", points = new[] { new[] { 0.0, 0, 1.4 } }, start = 1 });
        double end = (double)r["frames"]![1]!;
        var rig = (ObjectItem)b.Scene.Find("Climber")!;
        Assert.Equal(0.36, rig.WorldPosition(end).Y, 2); // standing on the top step
        var goalHeights = Enumerable.Range(1, (int)end).Select(f => rig.Bones.First(x => x.Name == "Shin L").IKGoal!.WorldPosition(f).Y).ToList();
        Assert.True(goalHeights.Max() > 0.36);

        await s.Client.Json("make_object", new { shape = "ball", name = "Blob" });
        Assert.Contains("no bone \"Thigh L\"", await s.Client.Fails("walk", new { character = "Blob", points = new[] { new[] { 1.0, 0, 0 } } }));
        Assert.Contains("goes nowhere", await s.Client.Fails("walk", new { character = "Climber", points = new[] { new[] { 0.0, 0, 1.4 } }, start = (int)end }));
    }
}
