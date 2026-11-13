using AVAModeler.Core;
using AVAModeler.Scene;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Physics: rigid bodies set, simulated and baked into keys, each one undo step.</summary>
public class PhysicsToolTests
{
    [Fact]
    public async Task Dominoes_fall_by_simulation_and_bake_into_keys()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("set_scene", new { last_frame = 90 });
        await s.Client.Json("make_object", new { shape = "ground" });
        await s.Client.Json("set_rigid_body", new { @object = "Ground", kind = "static", shape = "box", friction = 0.8 });
        for (int i = 0; i < 5; i++)
        {
            await s.Client.Json("make_object", new { shape = "box", name = $"Domino {i + 1}", size = new[] { 0.25, 0.5, 0.06 }, position = new[] { 0.0, 0, i * 0.3 } });
            await s.Client.Json("set_rigid_body", new { @object = $"Domino {i + 1}", shape = "box", bounce = 0, friction = 0.6, spin = i == 0 ? new[] { 150.0, 0, 0 } : null });
        }
        Assert.Equal("Agent: set a rigid body", b.Host.Layout.History.UndoLabel);
        var sim = await s.Client.Json("simulate");
        Assert.Equal(5, sim["bodies"]!.AsArray().Count);
        var last = (ObjectItem)b.Scene.Find("Domino 5")!;
        Assert.True(last.WorldMatrix(90).TransformVector(Vec3.UnitY).Normalized().Y < 0.75);
        var pic = await s.Client.Call("render_scene", new { frame = 60, width = 240, samples = 1 });
        if (Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOT_OUT") is { Length: > 0 } dir)
            File.WriteAllBytes(Path.Combine(dir, "dominoes-physics.png"), pic.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());
        var simulated = last.WorldPosition(70);

        var baked = await s.Client.Json("bake_simulation");
        Assert.Equal(5, baked["baked"]!.AsArray().Count);
        Assert.Null(last.RigidBody);
        Assert.True(last.Channels[4].Keys.Count > 80);
        Assert.True((last.WorldPosition(70) - simulated).Length < 1e-3);
        await s.Client.Json("undo_scene");
        Assert.NotNull(last.RigidBody);
        Assert.Contains("kind must be", await s.Client.Fails("set_rigid_body", new { @object = "Ground", kind = "floaty" }));
        Assert.Contains("must be from 0 to 1", await s.Client.Fails("set_rigid_body", new { @object = "Domino 1", bounce = 3 }));
        await s.Client.Json("set_rigid_body", new { @object = "Domino 1", off = true });
        Assert.Null(((ObjectItem)b.Scene.Find("Domino 1")!).RigidBody);
    }
}
