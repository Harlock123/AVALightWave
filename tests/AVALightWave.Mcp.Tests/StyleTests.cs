using AVAModeler.Core;
using AVAModeler.Scene;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Style presets: lights, surfaces, lens and default ease, in one undo step.</summary>
public class StyleTests
{
    private static async Task Stage(InProcess s)
    {
        await s.Client.Json("build_scene", new
        {
            plan = new
            {
                objects = new object[]
                {
                    new { shape = "ground", color = new[] { 150, 150, 140 } },
                    new { shape = "ball", name = "Ball", radius = 0.5, color = new[] { 220, 40, 30 } },
                    new { shape = "box", name = "Crate", color = new[] { 60, 120, 220 }, position = new[] { 1.4, 0, 0.3 } },
                },
                lights = "three_point",
                camera = new { items = new[] { "all" }, from = "three_quarter", margin = 0.3 },
            },
        });
    }

    [Theory]
    [InlineData("noir")]
    [InlineData("product_commercial")]
    [InlineData("cartoon")]
    [InlineData("golden_hour")]
    [InlineData("sci_fi")]
    public async Task Each_style_lights_and_renders(string style)
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await Stage(s);
        var r = await s.Client.Json("apply_style", new { style });
        Assert.Equal(style, (string?)r["style"]);
        Assert.Equal("Agent: applied a style", b.Host.Layout.History.UndoLabel);
        Assert.NotEmpty(b.Scene.Lights);
        var pic = await s.Client.Call("render_scene", new { width = 240, samples = 2 });
        var png = pic.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray();
        Assert.True(Colours(png) > 20, style);
        if (Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOT_OUT") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, $"style-{style}.png"), png);
    }

    [Fact]
    public async Task A_style_treats_surfaces_sets_the_lens_and_the_default_ease_and_undoes()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await Stage(s);
        var ball = (ObjectItem)b.Scene.Find("Ball")!;
        var before = ball.Document;
        await s.Client.Json("apply_style", new { style = "noir" });
        var c = ball.Document!.Surfaces.Single(x => x.Name == "Ball").Color;
        Assert.True(c.R == c.G && c.G == c.B);
        Assert.Equal(3.6, b.Scene.CurrentCamera!.Zoom.Evaluate(1), 6);
        Assert.Equal(["Key", "Edge"], b.Scene.Lights.Select(l => l.Name));
        await s.Client.Json("undo_scene");
        Assert.Same(before, ball.Document);
        Assert.Null(LightWaveTools.StyleOf(b.Scene));

        // Cartoon: moves without an ease overshoot.
        await s.Client.Json("apply_style", new { style = "cartoon", lights = false });
        Assert.Equal(["Key", "Fill", "Rim"], b.Scene.Lights.Select(l => l.Name));
        await s.Client.Json("animate", new { moves = new[] { new { item = "Ball", kind = "move", by = new[] { 0.0, 1, 0 }, start = 1, end = 31 } } });
        Assert.Contains(Enumerable.Range(1, 30), f => ball.PositionAt(f).Y > 1.05);
        Assert.Contains("no style \"gothic\"", await s.Client.Fails("apply_style", new { style = "gothic" }));

        // In a plan.
        await s.Client.Json("new_scene", new { discard_changes = true });
        var r = await s.Client.Json("build_scene", new { plan = new { objects = new[] { new { shape = "ball" } }, style = "golden_hour" } });
        Assert.Equal("golden_hour", (string?)r["style"]!["style"]);
        Assert.Contains(b.Scene.Lights, l => l.Name == "Sun");
    }
}
