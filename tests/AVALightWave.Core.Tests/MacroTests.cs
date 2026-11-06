using AVAModeler.Core;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVALightWave.Core.Tests;

/// <summary>Lua macros in Layout: the scene API, one undo step a macro, all or nothing, and AVA Modeler's sandbox.</summary>
public class MacroTests
{
    [Fact]
    public void A_macro_builds_and_animates_a_scene_as_one_undo_step()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var printed = new List<string>();
        LayoutMacros.Run(d.Session, """
            frames(1, 40)
            local ball = addobject("objects/ball.lwo", "Ball")
            for f = 1, 40, 10 do key(ball, "position.y", f, (f - 1) / 10) end
            key(ball, "position.x", 1, 0, {Span = "linear"})
            key(ball, "position.x", 40, 4, {Span = "linear"})
            local spot = addlight("Key", "spot")
            light(spot, {Color = {255, 200, 100}, Cone = 20, Intensity = 1.5})
            setpos(spot, 0, 4, -3)
            local copies = clone(ball, 2, {0, 0, 1}, 5)
            parent(copies[1], "Key")
            print(#items(), selected(), get(ball, "position.x", 20.5))
            """, "Bouncing", w.Path, printed.Add);
        Assert.Equal("Macro: Bouncing", d.Session.History.UndoLabel);
        Assert.Equal(1, d.Session.History.Count);
        var ball = (ObjectItem)d.Scene.Find("Ball")!;
        Assert.Equal(4, ball.PositionAt(40).X, 9);
        Assert.Equal(SpanKind.Linear, ball[Channel.PositionX].Keys[^1].Kind);
        var spot = (LightItem)d.Scene.Find("Key")!;
        Assert.Equal(20, spot.ConeAngle);
        Assert.Equal(new Vec3(1, 200 / 255.0, 100 / 255.0), spot.Color);
        Assert.Equal(1.5, spot.Intensity.Evaluate(1));
        Assert.Same(spot, d.Scene.Find("Ball (2)")!.Parent);
        Assert.Equal(40, d.Scene.LastFrame);
        Assert.Single(printed);
        Assert.StartsWith("6", printed[0]);
        d.Session.Undo();
        Assert.Equal(2, d.Scene.Items.Count);
        Assert.Equal(60, d.Scene.LastFrame);
    }

    [Fact]
    public void A_failing_macro_leaves_the_scene_as_it_was()
    {
        var d = new SceneDocument();
        var e = Assert.Throws<MacroException>(() => LayoutMacros.Run(d.Session, "addnull('A')\nkey('A', 'nonsense', 1, 2)", "Bad"));
        Assert.Contains("no channel \"nonsense\"", e.Message);
        Assert.Equal(2, d.Scene.Items.Count);
        Assert.False(d.Session.History.CanUndo);
        Assert.Throws<MacroException>(() => LayoutMacros.Run(d.Session, "remove('Nobody')", "Bad"));
    }

    [Fact]
    public void Macros_run_in_the_sandbox_with_a_time_limit()
    {
        var d = new SceneDocument();
        Assert.Throws<MacroException>(() => LayoutMacros.Run(d.Session, "os.execute('ls')", "Os"));
        Assert.Throws<MacroException>(() => LayoutMacros.Run(d.Session, "io.open('/etc/passwd')", "Io"));
        var e = Assert.Throws<MacroException>(() => LayoutMacros.Run(d.Session, "addnull('Spin') while true do end", "Forever", timeLimit: TimeSpan.FromMilliseconds(300)));
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Null(d.Scene.Find("Spin"));
    }

    [Fact]
    public void A_macro_file_renders_and_the_folder_lists_macros()
    {
        using var w = new Workbench();
        var folder = Directory.CreateDirectory(w.File("macros")).FullName;
        File.WriteAllText(Path.Combine(folder, "b.lua"), "addnull('B')");
        File.WriteAllText(Path.Combine(folder, "a.lua"), "addobject('../objects/crate.lwo')\nrender('out/still.png', 32, 24)");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "");
        Assert.Equal(["a.lua", "b.lua"], LayoutMacros.List(folder).Select(Path.GetFileName));
        Assert.Empty(LayoutMacros.List(w.File("none")));
        var d = new SceneDocument();
        LayoutMacros.RunFile(d.Session, Path.Combine(folder, "a.lua"));
        Assert.True(File.Exists(Path.Combine(folder, "out", "still.png")));
        Assert.Equal("Macro: a", d.Session.History.UndoLabel);
        Assert.Contains("render(path", LayoutMacros.ReferenceText);
    }
}
