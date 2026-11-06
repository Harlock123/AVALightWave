using Avalonia.Headless.XUnit;
using AVALightWave.Core.Tests;
using AVAModeler.Core;
using AVAModeler.Scene;

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
}
