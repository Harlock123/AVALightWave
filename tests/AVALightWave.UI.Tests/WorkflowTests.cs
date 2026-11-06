using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AVALightWave.Core.Tests;
using AVAModeler.Core;
using AVAModeler.Scene;
using AVAModeler.Layout.UI;

namespace AVALightWave.UI.Tests;

/// <summary>Clone Item, Load from Scene and crash recovery through the window, headless, with the test's own folders.</summary>
public class WorkflowTests
{
    [AvaloniaFact]
    public void Clone_item_makes_offset_copies_from_its_requester_as_one_undo_step()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var crate = w.LoadObject(bench.Object("crate.lwo"))!;
        var r = w.OpenClone()!;
        Ui.Pump();
        r.Count.Text = "4";
        r.Move[0].Text = "1.5";
        r.TimeOffset.Text = "2";
        Assert.True(r.Apply());
        r.Close();
        Ui.Pump();
        Assert.Equal(5, w.Session.Scene.Objects.Count());
        Assert.Equal("crate (5)", w.Session.Selected!.Name);
        Assert.Equal(6, w.Session.Selected.PositionAt(1).X, 9);
        Assert.Contains(w.Session.Selected, w.ChooserItems);
        w.Undo();
        Assert.Same(crate, w.Session.Scene.Objects.Single());

        // A bad number is named and nothing changes; a bone isn't cloned alone.
        w.Session.Select(crate);
        r = w.OpenClone()!;
        r.Count.Text = "lots";
        Assert.False(r.Apply());
        Assert.Contains("whole number", r.Problem);
        r.Close();
        Assert.Single(w.Session.Scene.Objects);
        var bone = w.Session.Edit("Bone", () => w.Session.Scene.AddBone(crate, "B"));
        w.Session.Select(bone);
        Assert.Null(w.OpenClone());
        Assert.Contains("is a bone", w.Status);
    }

    [AvaloniaFact]
    public void Load_from_scene_brings_another_scenes_items_in()
    {
        using var bench = new Workbench();
        var other = new SceneDocument();
        other.LoadObject(bench.Object("pawn.lwo"));
        other.Session.Add(new LightItem("Rim", LightType.Point));
        var path = bench.File("other.avascene");
        other.Save(path);

        var w = Ui.Open();
        var r = w.OpenLoadFromScene(path);
        Ui.Pump();
        r.Cameras.IsChecked = false;
        Assert.True(r.Apply());
        r.Close();
        Ui.Pump();
        Assert.Equal(["Camera", "Light", "Light (2)", "pawn", "Rim"], w.Session.Scene.Items.Select(i => i.Name));
        Assert.Single(w.Session.Scene.Cameras);
        Assert.Contains("Loaded 3 item(s) from other.avascene", w.Status);
        w.Undo();
        Assert.Equal(2, w.Session.Scene.Items.Count);

        var bad = w.OpenLoadFromScene(bench.File("none.lws"));
        Assert.False(bad.Apply());
        Assert.Contains("Can't load", bad.Problem);
        bad.Close();
    }

    [AvaloniaFact]
    public void A_crash_leaves_an_auto_save_that_the_next_start_offers_back()
    {
        using var bench = new Workbench();
        var folder = bench.File("autosave");
        var w = Ui.Open();
        Assert.Null(w.Autosave);
        w.Autosave = new SceneAutosave(folder);
        w.Autosave.Start();
        w.LoadObject(bench.Object("ball.lwo"));
        Ui.Wait(w.AutosaveNow());
        Assert.Contains("Auto-saved", w.Status);
        // The window "crashes": nothing cleans up. The next one finds the auto-save and (asked) recovers it.
        var next = Ui.Open();
        next.Autosave = new SceneAutosave(folder);
        Assert.True(Ui.Wait(next.OfferRecovery()));
        Assert.Equal("ball", next.Session.Scene.Objects.Single().Name);
        Assert.True(next.Document.Modified);
        Assert.Null(next.Document.Path);
        Assert.Equal("AVALightWave — Untitled *", next.Title);
        // A clean exit leaves nothing to offer.
        next.Autosave.Finish();
        var third = Ui.Open();
        third.Autosave = new SceneAutosave(folder);
        Assert.False(Ui.Wait(third.OfferRecovery()));
    }

    [AvaloniaFact]
    public void The_objects_panel_adds_a_morph_target_and_keys_its_weight()
    {
        using var bench = new Workbench();
        var squash = bench.SquashedPawn();
        var w = Ui.Open();
        var target = w.LoadObject(squash)!;
        var pawn = w.LoadObject(bench.Object("pawn.lwo"))!;
        w.ShowPanel("Objects");
        Ui.Pump();
        var panel = (ItemsPanel)w.CurrentPanel!;
        var source = Ui.Find<Avalonia.Controls.ComboBox>(panel, "MorphSource");
        Assert.Equal(["pawn-squash"], source.ItemsSource!.Cast<string>());
        source.SelectedIndex = 0;
        var add = panel.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().First(b => (b.Content as string) == "Add Target Object");
        add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Ui.Pump();
        var m = Assert.Single(pawn.Morphs);
        Assert.Equal(target.BaseMesh().Points.Count, m.Target.Points.Count);
        w.Session.SetFrame(20);
        Ui.Pump();
        var weight = Ui.Find<Avalonia.Controls.TextBox>(w.CurrentPanel!, "MorphWeight0");
        Ui.Type(w, weight, "100");
        Assert.Equal(1, m.Weight.Evaluate(20), 9);
        Assert.Equal("Set pawn's pawn-squash", w.Session.History.UndoLabel);
        Assert.True(pawn.DeformedMesh(20).Bounds().Max.Y < pawn.BaseMesh().Bounds().Max.Y * 0.6);
    }

    [AvaloniaFact]
    public void Skelegons_in_a_loaded_object_become_bones_in_one_step()
    {
        using var bench = new Workbench();
        // An arm: a box with a chain of two skelegons (2-point polygons on a "Skelegon" surface) up its middle.
        var doc = new Document();
        var m = doc.Current.Mesh;
        m.Points.AddRange([new Vec3(-0.1, 0, -0.1), new Vec3(0.1, 0, -0.1), new Vec3(0.1, 2, -0.1), new Vec3(-0.1, 2, -0.1)]);
        m.Polygons.Add(new Polygon([0, 1, 2, 3], 0));
        doc.Surfaces.Add(new Surface("Skelegon"));
        m.Points.AddRange([new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 2, 0)]);
        m.Polygons.Add(new Polygon([4, 5], 1));
        m.Polygons.Add(new Polygon([5, 6], 1));
        m.Touch();
        var path = bench.File("arm.lwo");
        AVAModeler.Formats.ObjectFiles.Save(doc, path);

        var w = Ui.Open();
        var arm = w.LoadObject(path)!;
        int steps = w.Session.History.Count;
        var bones = w.SkelegonsToBones(arm);
        Assert.Equal(2, bones.Count);
        Assert.Same(bones[0], bones[1].Parent);
        Assert.Equal(steps + 1, w.Session.History.Count);
        Assert.Equal(EditMode.Bones, w.Mode);
        Assert.Contains("Made 2 bones", w.Status);
        w.Undo();
        Assert.Empty(arm.Bones);
        w.Session.Select(null);
        Assert.Empty(w.SkelegonsToBones(null));
    }
}
