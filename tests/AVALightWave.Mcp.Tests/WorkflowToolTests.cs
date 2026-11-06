using AVAModeler.Core;
using AVAModeler.Scene;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>AVALightWave's own tools for Clone Item and Load from Scene, through a client, each one undo step.</summary>
public class WorkflowToolTests
{
    [Fact]
    public async Task Clone_item_makes_offset_copies_as_one_step()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("add_object", new { file = "objects/ball.lwo", name = "Ball" });
        await s.Client.Json("set_key", new { item = "Ball", channel = "position.y", frame = 1, value = 0 });
        await s.Client.Json("set_key", new { item = "Ball", channel = "position.y", frame = 10, value = 2 });
        var r = await s.Client.Json("clone_item", new { item = "Ball", count = 3, position_offset = new[] { 1.0, 0, 0 }, time_offset = 4 });
        Assert.Equal(["Ball (2)", "Ball (3)", "Ball (4)"], r["copies"]!.AsArray().Select(c => (string?)c!["name"]));
        var last = (ObjectItem)b.Scene.Find("Ball (4)")!;
        Assert.Equal(3, last.PositionAt(1).X, 9);
        Assert.Equal(2, last.PositionAt(22).Y, 9);
        Assert.Equal(4, b.Scene.Objects.Count());
        await s.Client.Json("undo_scene");
        Assert.Single(b.Scene.Objects);

        Assert.Contains("1 to 1000", await s.Client.Fails("clone_item", new { item = "Ball", count = 0 }));
        Assert.Contains("[x, y, z]", await s.Client.Fails("clone_item", new { item = "Ball", position_offset = new[] { 1.0 } }));
        Assert.Contains("no item", await s.Client.Fails("clone_item", new { item = "Nobody" }));
        Assert.Single(b.Scene.Objects);
    }

    [Fact]
    public async Task Load_from_scene_merges_a_scene_file_from_the_working_folder()
    {
        using var b = new Bench();
        var other = new SceneDocument();
        var crate = other.LoadObject(b.Folder.Object("crate.lwo"));
        var spin = other.Session.Add(new NullItem("Spin"));
        other.SetParent(crate, spin);
        spin.SetRotation(1, Vec3.Zero);
        spin.SetRotation(30, new Vec3(90, 0, 0));
        other.Save(b.Folder.File("props.lws"));

        await using var s = await b.Serve();
        var r = await s.Client.Json("load_from_scene", new { file = "props.lws", cameras = false });
        Assert.Equal(["Light (2)", "Spin", "crate"], r["items"]!.AsArray().Select(i => (string?)i!["name"]).Order());
        var merged = (ObjectItem)b.Scene.Find("crate")!;
        Assert.Equal("Spin", merged.Parent!.Name);
        Assert.Equal(90, merged.Parent.RotationAt(30).X, 6);
        Assert.Single(b.Scene.Cameras);
        await s.Client.Json("undo_scene");
        Assert.Null(b.Scene.Find("crate"));

        Assert.Contains("outside the working folder", await s.Client.Fails("load_from_scene", new { file = "../x.lws" }));
        Assert.Contains("isn't a scene", await s.Client.Fails("load_from_scene", new { file = "objects/crate.lwo" }));
        Assert.Contains("rename", await s.Client.Fails("load_from_scene", new { file = "props.lws", names = "shuffle" }));
        Assert.Contains("no file", await s.Client.Fails("load_from_scene", new { file = "none.lws" }));
        Assert.Equal(2, b.Scene.Items.Count);
    }
}

/// <summary>add_morph_target through a client.</summary>
public class MorphToolTests
{
    [Fact]
    public async Task A_morph_target_from_a_file_or_object_keyed_with_set_key()
    {
        using var b = new Bench();
        var doc = AVAModeler.Formats.ObjectFiles.Load(b.Folder.Object("ball.lwo"));
        foreach (var layer in doc.Layers)
        {
            for (int i = 0; i < layer.Mesh.Points.Count; i++) layer.Mesh.Points[i] = layer.Mesh.Points[i] * 2;
            layer.Mesh.Touch();
        }
        AVAModeler.Formats.ObjectFiles.Save(doc, b.Folder.Object("ball-big.lwo"));
        await using var s = await b.Serve();
        await s.Client.Json("add_object", new { file = "objects/ball.lwo", name = "Ball" });
        var r = await s.Client.Json("add_morph_target", new { @object = "Ball", file = "objects/ball-big.lwo", name = "Grow" });
        Assert.Equal("morph.Grow", (string?)r["added"]);
        await s.Client.Json("set_key", new { item = "Ball", channel = "morph.Grow", frame = 10, value = 1 });
        var ball = (ObjectItem)b.Scene.Find("Ball")!;
        Assert.Equal(ball.BaseMesh().Bounds().Max.Y * 2, ball.DeformedMesh(10).Bounds().Max.Y, 6);
        await s.Client.Json("add_object", new { file = "objects/ball-big.lwo", name = "Big" });
        r = await s.Client.Json("add_morph_target", new { @object = "Ball", from_object = "Big" });
        Assert.Equal("morph.Big", (string?)r["added"]);
        Assert.Contains("one of them", await s.Client.Fails("add_morph_target", new { @object = "Ball" }));
        Assert.Contains("same points", await s.Client.Fails("add_morph_target", new { @object = "Ball", file = "objects/crate.lwo" }));
        Assert.Contains("outside the working folder", await s.Client.Fails("add_morph_target", new { @object = "Ball", file = "../x.lwo" }));
        Assert.Equal(2, ball.Morphs.Count);
    }
}

/// <summary>save_motion and load_motion: one item's motion onto another through a .mot file.</summary>
public class MotionFileToolTests
{
    [Fact]
    public async Task A_motion_saved_from_one_item_loads_onto_another()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("add_null", new { name = "A" });
        await s.Client.Json("add_null", new { name = "B" });
        await s.Client.Json("set_key", new { item = "A", channel = "position.x", frame = 1, value = 0 });
        await s.Client.Json("set_key", new { item = "A", channel = "position.x", frame = 20, value = 3 });
        var r = await s.Client.Json("save_motion", new { item = "A", file = "motions/a.mot" });
        Assert.Equal("motions/a.mot", ((string?)r["saved"])!.Replace('\\', '/'));
        Assert.Contains("already exists", await s.Client.Fails("save_motion", new { item = "A", file = "motions/a.mot" }));
        await s.Client.Json("save_motion", new { item = "A", file = "motions/a1.mot", version = 1 });
        r = await s.Client.Json("load_motion", new { item = "B", file = "motions/a1.mot" });
        Assert.Equal(1, (int?)r["version"]);
        var a = b.Scene.Find("A")!;
        var bb = b.Scene.Find("B")!;
        Assert.Equal(a.PositionAt(10).X, bb.PositionAt(10).X, 6);
        await s.Client.Json("undo_scene");
        Assert.Equal(0, bb.PositionAt(10).X, 9);
        await s.Client.Json("load_motion", new { item = "B", file = "motions/a.mot" });
        Assert.Equal(a.PositionAt(13).X, bb.PositionAt(13).X, 9);
        File.WriteAllText(b.Folder.File("bad.mot"), "not a motion");
        Assert.Contains("LWMO", await s.Client.Fails("load_motion", new { item = "B", file = "bad.mot" }));
        Assert.Contains("version must be", await s.Client.Fails("save_motion", new { item = "A", file = "x.mot", version = 2 }));
        Assert.Contains("outside the working folder", await s.Client.Fails("load_motion", new { item = "B", file = "../x.mot" }));
    }
}
