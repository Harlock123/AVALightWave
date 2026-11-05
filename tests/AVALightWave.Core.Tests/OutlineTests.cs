using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>Edit modes, the Scene Editor's outline, and AVALightWave's own settings.</summary>
public class OutlineTests
{
    private static (SceneDocument Doc, ObjectItem Ship, Bone Root, Bone Tip, NullItem Pivot) Rig()
    {
        var d = new SceneDocument();
        var s = d.Scene;
        var pivot = s.Add(new NullItem("Pivot"));
        var ship = d.Session.Edit("Ship", () => s.AddObject("Ship", Docs.Box()));
        ship.Parent = pivot;
        var root = s.AddBone(ship, "Root");
        var tip = s.AddBone(ship, "Tip");
        tip.Parent = root;
        return (d, ship, root, tip, pivot);
    }

    [Fact]
    public void Edit_modes_choose_items_as_classic_layout_did()
    {
        var (d, ship, root, tip, pivot) = Rig();
        var s = d.Scene;
        Assert.Equal([pivot, ship], s.ItemsOf(EditMode.Objects));
        Assert.Equal([root, tip], s.ItemsOf(EditMode.Bones));
        Assert.Equal([s.Lights.Single()], s.ItemsOf(EditMode.Lights));
        Assert.Equal([s.CurrentCamera!], s.ItemsOf(EditMode.Cameras));
        Assert.Equal(EditMode.Bones, EditModes.Of(tip));
        Assert.Equal(EditMode.Objects, EditModes.Of(pivot));
        Assert.Equal(EditMode.Cameras, EditModes.Of(s.CurrentCamera!));
        // Up and down step round the mode's items.
        Assert.Equal(ship, s.Step(EditMode.Objects, pivot, 1));
        Assert.Equal(pivot, s.Step(EditMode.Objects, ship, 1));
        Assert.Equal(ship, s.Step(EditMode.Objects, pivot, -1));
        Assert.Equal(pivot, s.Step(EditMode.Objects, s.CurrentCamera, 1));
        Assert.Equal(ship, s.Step(EditMode.Objects, null, -1));
        s.Remove(s.Lights.Single());
        Assert.Null(s.Step(EditMode.Lights, null, 1));
    }

    [Fact]
    public void The_outline_shows_the_hierarchy_with_bones_under_their_object()
    {
        var (d, _, _, _, _) = Rig();
        var rows = SceneOutline.Rows(d.Scene);
        Assert.Equal(["Camera: Camera", "Light: Light", "Null: Pivot", "  Object: Ship", "    Bone: Root", "      Bone: Tip"], rows.Select(r => r.ToString()));
        d.SetVisible(d.Scene.Find("Ship")!, false);
        d.SetLocked(d.Scene.Find("Light")!, true);
        rows = SceneOutline.Rows(d.Scene);
        Assert.Equal("  Object: Ship (hidden)", rows[3].ToString());
        Assert.Equal("Light: Light (locked)", rows[1].ToString());
    }

    [Fact]
    public void The_outline_shows_keys_inside_the_frames_on_every_channel()
    {
        var (d, ship, _, _, _) = Rig();
        Assert.Empty(SceneOutline.Keys(ship, d.Scene));
        d.Session.SetFrame(10);
        d.Session.SetVector(ship, 0, new Vec3(1, 0, 0));
        d.Session.SetFrame(40);
        d.Session.SetVector(ship, 0, new Vec3(2, 0, 0));
        var light = d.Scene.Lights.Single();
        light.Intensity.AddKey(5, 0.5);
        light.Intensity.AddKey(50, 1);
        light.Intensity.AddKey(500, 1);
        Assert.Equal([10.0, 40], SceneOutline.Keys(ship, d.Scene));
        Assert.Equal([5.0, 50], SceneOutline.Keys(light, d.Scene));
    }

    [Fact]
    public void Settings_save_only_where_they_were_loaded_from()
    {
        var dir = Path.Combine(Path.GetTempPath(), "avalightwave-options-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "lightwave.json");
            var o = LightWaveOptions.Load(path);
            Assert.Equal(path, o.Location);
            Assert.True(o.AutoKey);
            o.ModelerPath = "/opt/ava/AVAModeler";
            for (int i = 0; i < 12; i++) o.AddRecentObject($"/objects/{i}.lwo");
            o.AddRecentObject("/objects/3.lwo");
            o.Save();
            var back = LightWaveOptions.Load(path);
            Assert.Equal("/opt/ava/AVAModeler", back.ModelerPath);
            Assert.Equal(10, back.RecentObjects.Count);
            Assert.Equal("/objects/3.lwo", back.RecentObjects[0]);
            // Made with new (as a test window's are), they save nowhere.
            new LightWaveOptions { ModelerPath = "x" }.Save();
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LightWaveOptions.Folder);
            Assert.EndsWith("AVALightWave", LightWaveOptions.Folder);
            File.WriteAllText(path, "{ not json");
            Assert.Null(LightWaveOptions.Load(path).ModelerPath);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
