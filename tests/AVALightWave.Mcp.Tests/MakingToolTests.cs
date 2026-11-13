using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Scene;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Objects made in the scene: make_object's shapes, build_object and edit_object steps, and surfaces.</summary>
public class MakingToolTests
{
    [Fact]
    public async Task Every_shape_is_made_closed_and_resting_on_its_base()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        foreach (var shape in new[] { "box", "ball", "cylinder", "cone", "torus", "steps", "text" })
        {
            var r = await s.Client.Json("make_object", new { shape, text = shape == "text" ? "Hi" : null, color = new[] { 200, 30, 30 } });
            Assert.True((int)r["polygons"]! > 0, shape);
            Assert.Equal(0, (double)r["own_bounds"]![0]![1]!, 6);
            var obj = b.Scene.Objects.Last();
            var check = MeshCheck.Mesh(obj.BaseMesh());
            Assert.True(check.Watertight, $"{shape} isn't closed");
            Assert.True(check.InconsistentEdges.Count == 0 && check.InwardShells == 0, $"{shape} doesn't face outward");
            Assert.Equal(obj.Name, (string?)r["surface"]);
            Assert.Equal(new Rgb(200, 30, 30), obj.Document!.Surfaces.Single(x => x.Name == obj.Name).Color);
        }
        // The ground's top is y = 0, whatever the origin.
        var g = await s.Client.Json("make_object", new { shape = "ground", size = new[] { 8.0, 6 } });
        Assert.Equal(0, (double)g["own_bounds"]![1]![1]!, 6);
        Assert.Equal(8, (double)g["size"]![0]!, 6);
        Assert.Equal("Agent: made a ground", b.Host.Layout.History.UndoLabel);
        // Placed, centred and parented.
        var c = await s.Client.Json("make_object", new { shape = "ball", name = "Moon", radius = 0.2, origin = "center", position = new[] { 2.0, 1, 0 }, parent = "Ground" });
        Assert.Equal(-0.2, (double)c["own_bounds"]![0]![1]!, 6);
        Assert.Equal("Ground", (string?)c["parent"]);
        Assert.Equal(2, b.Scene.Find("Moon")!.PositionAt(1).X, 9);
    }

    [Fact]
    public async Task Make_object_mistakes_change_nothing()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        int before = b.Scene.Items.Count;
        Assert.Contains("shape must be one of", await s.Client.Fails("make_object", new { shape = "teapot" }));
        Assert.Contains("needs the words", await s.Client.Fails("make_object", new { shape = "text" }));
        Assert.Contains("less than radius", await s.Client.Fails("make_object", new { shape = "torus", radius = 0.5, thickness = 0.6 }));
        Assert.Contains("3 numbers", await s.Client.Fails("make_object", new { shape = "box", size = new[] { 1.0, 2 } }));
        Assert.Contains("no item", await s.Client.Fails("make_object", new { shape = "box", parent = "Nobody" }));
        Assert.Equal(before, b.Scene.Items.Count);
        Assert.False(b.Host.Layout.History.CanUndo);
    }

    [Fact]
    public async Task Build_object_runs_the_engines_modeling_calls_and_names_a_failing_step()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var r = await s.Client.Json("build_object", new
        {
            name = "Table",
            origin = "base",
            steps = new object[]
            {
                new { call = "set_surface", args = new { name = "Wood", color = new[] { 150, 100, 60 }, assign = false } },
                new { tool = "Box", @params = new Dictionary<string, object> { ["Size"] = new[] { 2.0, 0.1, 1 }, ["Center"] = new[] { 0, 0.75, 0 }, ["Surface"] = "Wood" } },
                new { call = "select_none" },
                new { tool = "Box", @params = new Dictionary<string, object> { ["Size"] = new[] { 0.1, 0.7, 0.1 }, ["Center"] = new[] { -0.9, 0.35, -0.4 }, ["Surface"] = "Wood" } },
                new { call = "check_mesh" },
            },
        });
        Assert.Equal("Table", (string?)r["name"]);
        Assert.Equal(12, (int)r["polygons"]!);
        Assert.Equal(5, r["steps"]!.AsArray().Count);
        Assert.Equal(0.8, (double)r["size"]![1]!, 6);
        Assert.Equal(["Wood"], r["surfaces"]!.AsArray().Select(x => (string)x!));
        Assert.Equal("Agent: built an object", b.Host.Layout.History.UndoLabel);

        int items = b.Scene.Items.Count;
        var e = await s.Client.Fails("build_object", new
        {
            steps = new object[] { new { tool = "Box" }, new { tool = "Bxo" } },
        });
        Assert.Contains("Step 2 (Bxo) failed", e);
        Assert.Contains("Did you mean", e);
        Assert.Contains("can't be used here", await s.Client.Fails("build_object", new { steps = new object[] { new { call = "save", args = new { path = "x.lwo" } } } }));
        Assert.Contains("no argument \"size\"", await s.Client.Fails("build_object", new { steps = new object[] { new { call = "set_surface", args = new { name = "A", size = 2 } } } }));
        Assert.Contains("no points", await s.Client.Fails("build_object", new { steps = new object[] { new { call = "set_surface", args = new { name = "A", assign = false } } } }));
        Assert.Equal(items, b.Scene.Items.Count);
    }

    [Fact]
    public async Task Edit_object_changes_a_copy_keeping_the_motion_and_undo_puts_it_back()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "box", name = "Crate" });
        await s.Client.Json("set_transform", new { item = "Crate", frame = 1, position = new[] { 0.0, 0, 0 }, key = true });
        await s.Client.Json("set_transform", new { item = "Crate", frame = 30, position = new[] { 3.0, 0, 0 }, key = true });
        var crate = (ObjectItem)b.Scene.Find("Crate")!;
        var oldDoc = crate.Document;
        var r = await s.Client.Json("edit_object", new
        {
            @object = "Crate",
            steps = new object[] { new { tool = "Size", @params = new Dictionary<string, object> { ["Factor %"] = 200 } } },
        });
        Assert.Equal(2, (double)r["size"]![0]!, 6);
        Assert.Equal(2, (double)r["size"]![1]!, 6);
        Assert.NotSame(oldDoc, crate.Document);
        Assert.Equal(3, crate.PositionAt(30).X, 9);
        await s.Client.Json("undo_scene");
        Assert.Same(oldDoc, crate.Document);
        Assert.Equal(1, Box3.Of(crate.BaseMesh().Points).Size.X, 6);
        Assert.Contains("Step 1 (Size) failed", await s.Client.Fails("edit_object", new { @object = "Crate", steps = new object[] { new { tool = "Size", @params = new { Nope = 1 } } } }));
        Assert.Same(oldDoc, crate.Document);
    }

    [Fact]
    public async Task Set_object_surface_changes_a_copy_and_lists_surfaces()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ball", name = "Ball" });
        var ball = (ObjectItem)b.Scene.Find("Ball")!;
        var oldDoc = ball.Document!;
        var r = await s.Client.Json("set_object_surface", new
        {
            @object = "Ball", color = new[] { 10, 200, 10 }, reflection = 0.4,
            procedural = new { kind = "checkerboard", channel = "color", color = new[] { 20, 20, 20 }, size = new[] { 0.2, 0.2, 0.2 } },
        });
        Assert.Equal("Ball", (string?)r["surface"]);
        Assert.Equal(1, (int)r["textures"]!);
        Assert.Equal(new Rgb(10, 200, 10), ball.Document!.Surfaces.Single(x => x.Name == "Ball").Color);
        Assert.Equal(Rgb.Grey with { R = 200 }, oldDoc.Surfaces.Single(x => x.Name == "Ball").Color with { R = 200 });
        var list = await s.Client.Json("get_object_surfaces", new { @object = "Ball" });
        Assert.Contains(list["surfaces"]!.AsArray(), x => (string?)x!["name"] == "Ball" && (int)x["polygons"]! > 0);
        await s.Client.Json("undo_scene");
        Assert.Same(oldDoc, ball.Document);
        Assert.Contains("no surface \"Glass\"", await s.Client.Fails("set_object_surface", new { @object = "Ball", surface = "Glass", color = new[] { 1, 2, 3 } }));
    }

    [Fact]
    public async Task Modeling_tools_are_listed_and_described()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var list = await s.Client.Json("list_modeling_tools");
        Assert.Contains(list["tools"]!.AsArray(), t => (string?)t!["name"] == "Lathe");
        var d = await s.Client.Json("describe_modeling_tool", new { name = "box" });
        Assert.Equal("Box", (string?)d["example"]!["tool"]);
        Assert.Contains("list_modeling_tools", await s.Client.Fails("describe_modeling_tool", new { name = "Boxx" }));
    }

    [Fact]
    public async Task A_made_object_renders_and_saves_with_the_scene()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ball", color = new[] { 220, 40, 30 } });
        await s.Client.Json("look_at", new { item = "Camera", target = "Ball", key = false });
        var r = await s.Client.Call("render_scene", new { width = 64, samples = 1 });
        var png = r.Content.OfType<ModelContextProtocol.Protocol.ImageContentBlock>().Single();
        Assert.True(Colours(png.DecodedData.ToArray()) > 4);
        await s.Client.Json("save_scene", new { path = "made.avascene" });
        await s.Client.Json("new_scene");
        await s.Client.Json("open_scene", new { path = "made.avascene" });
        Assert.NotNull(b.Scene.Find("Ball"));
        Assert.True(((ObjectItem)b.Scene.Find("Ball")!).BaseMesh().Polygons.Count > 0);
    }
}
