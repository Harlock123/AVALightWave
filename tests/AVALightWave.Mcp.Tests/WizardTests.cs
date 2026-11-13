using AVAModeler.Scene;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>The window's shot wizards: a turntable and a product shot, each one undo step, checked.</summary>
public class WizardTests
{
    [Fact]
    public async Task A_turntable_spins_the_object_in_place_and_checks_out()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("add_object", new { file = "objects/pawn.lwo", position = new[] { 2.0, 0, 1 } });
        var pawn = b.Scene.Find("pawn")!;
        var before = pawn.WorldPosition(1);
        var r = await b.Host.RunSceneAsync("wizard", (c, l) => ShotWizards.Run(c, l, b.Host.Root, new WizardChoice("pawn", WizardLook.Turntable, 48)), Cancel);
        Assert.True((bool)r["check"]!["ok"]!, r["check"]!.ToJsonString());
        Assert.Equal("Turntable", b.Host.Layout.History.UndoLabel);
        var table = b.Scene.Find("Turntable")!;
        Assert.Same(table, pawn.Parent);
        Assert.True((pawn.WorldPosition(1) - before).Length < 1e-6); // parented in place
        Assert.Equal(360, table.RotationAt(48).X, 6);
        var pic = await s.Client.Call("render_scene", new { frame = 12, width = 320, samples = 2 });
        var png = pic.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray();
        Assert.True(Colours(png) > 40);
        if (Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOT_OUT") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, "turntable.png"), png);
        await s.Client.Json("undo_scene");
        Assert.Null(b.Scene.Find("Turntable"));
        Assert.Null(pawn.Parent);
    }

    [Fact]
    public async Task A_product_shot_stands_the_object_on_a_pedestal()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ball", name = "Gem", radius = 0.3, sides = 8, smooth = false, color = new[] { 40, 200, 120 }, specular = 0.9 });
        var r = await b.Host.RunSceneAsync("wizard", (c, l) => ShotWizards.Run(c, l, b.Host.Root, new WizardChoice("Gem", WizardLook.ProductShot, 48)), Cancel);
        Assert.Equal("Product Shot", b.Host.Layout.History.UndoLabel);
        var gem = b.Scene.Find("Gem")!;
        var pedestal = b.Scene.Find("Pedestal")!;
        Assert.Equal(Shot.Bounds(pedestal, 1).Max.Y, Shot.Bounds(gem, 1).Min.Y, 3);
        Assert.DoesNotContain(r["check"]!["problems"]!.AsArray(), p => (string?)p!["kind"] is "never_in_view" or "below_ground" or "unlit");
        var pic = await s.Client.Call("render_scene", new { frame = 48, width = 320, samples = 2 });
        var png = pic.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray();
        if (Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOT_OUT") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, "product.png"), png);
        // A moving object can't go on a pedestal; nothing changes.
        await s.Client.Json("animate", new { moves = new[] { new { item = "Gem", kind = "move", by = new[] { 1.0, 0, 0 } } } });
        int steps = b.Host.Layout.History.Count;
        Assert.Contains("moves", Assert.Throws<ArgumentException>(() => b.Host.RunSceneAsync("wizard", (c, l) => ShotWizards.Run(c, l, b.Host.Root, new WizardChoice("Gem", WizardLook.ProductShot)), Cancel).GetAwaiter().GetResult()).Message);
        Assert.Equal(steps, b.Host.Layout.History.Count);
    }
}
