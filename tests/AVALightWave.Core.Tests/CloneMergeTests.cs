using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>Clone Item, Load from Scene, and the scene's auto-save and recovery.</summary>
public class CloneMergeTests
{
    [Fact]
    public void Clones_copy_motion_and_bones_each_offset_from_the_one_before_as_one_step()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var rig = d.Session.Add(new NullItem("Rig"));
        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        d.SetParent(pawn, rig);
        pawn.SetPosition(1, new Vec3(0, 0, 0));
        pawn.SetPosition(11, new Vec3(0, 2, 0));
        var bone = d.Session.Edit("Add Bone", () => d.Scene.AddBone(pawn, "Spine"));
        int steps = d.Session.History.Count;

        var copies = d.Clone(pawn, new CloneOptions { Count = 3, Position = new Vec3(1, 0, 0), Rotation = new Vec3(10, 0, 0), Scale = new Vec3(0.5, 0.5, 0.5), TimeOffset = 5 });

        Assert.Equal(steps + 1, d.Session.History.Count);
        Assert.Equal("Clone pawn ×3", d.Session.History.UndoLabel);
        Assert.Equal(["pawn (2)", "pawn (3)", "pawn (4)"], copies.Select(c => c.Name));
        Assert.Same(copies[^1], d.Session.Selected);
        var third = (ObjectItem)copies[2];
        Assert.Same(rig, third.Parent);
        Assert.Same(pawn.Document, third.Document);
        Assert.Single(third.Bones);
        Assert.Equal("Spine", third.Bones[0].Name);
        // Three steps along X, three turns of 10°, each half again as big; the motion 15 frames later.
        Assert.Equal(3, third.PositionAt(1).X, 9);
        Assert.Equal(30, third.RotationAt(1).X, 9);
        Assert.Equal(2.5, third.ScaleAt(1).X, 9);
        Assert.Equal(pawn.PositionAt(6).Y, third.PositionAt(21).Y, 9);
        Assert.Equal(pawn.PositionAt(11).Y, third.PositionAt(26).Y, 9);
        Assert.Equal(pawn[Channel.PositionY].Keys.Select(k => k.Frame + 15), third[Channel.PositionY].Keys.Select(k => k.Frame));
        // The original is untouched; one Ctrl+Z takes every clone away.
        Assert.Equal(0, pawn.PositionAt(1).X, 9);
        Assert.Single(pawn.Bones);
        d.Session.Undo();
        Assert.Single(d.Scene.Objects);
        Assert.Single(d.Scene.Bones);
        Assert.Same(bone, d.Scene.Bones.Single());
    }

    [Fact]
    public void Lights_cameras_and_nulls_clone_and_bones_refuse()
    {
        var d = new SceneDocument();
        var light = d.Scene.Lights.Single();
        light.Intensity.AddKey(1, 0.5);
        light.Intensity.AddKey(30, 2);
        var lights = d.Clone(light, new CloneOptions { Count = 2, Position = new Vec3(0, 1, 0) });
        Assert.All(lights, l => Assert.IsType<LightItem>(l));
        Assert.Equal(light.Intensity.Keys, ((LightItem)lights[1]).Intensity.Keys);
        Assert.Equal(light.PositionAt(1).Y + 2, lights[1].PositionAt(1).Y, 9);
        var cam = d.Clone(d.Scene.Cameras.Single(), new CloneOptions());
        Assert.Equal("Camera (2)", Assert.Single(cam).Name);
        Assert.NotSame(d.Scene.CurrentCamera, cam[0]);

        using var w = new Workbench();
        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        var bone = d.Session.Edit("Add Bone", () => d.Scene.AddBone(pawn, "Spine"));
        int before = d.Scene.Items.Count;
        Assert.Throws<ArgumentException>(() => d.Clone(bone, new CloneOptions()));
        Assert.Throws<ArgumentException>(() => d.Clone(pawn, new CloneOptions { Count = 0 }));
        Assert.Throws<ArgumentException>(() => d.Clone(pawn, new CloneOptions { TimeOffset = double.NaN }));
        Assert.Equal(before, d.Scene.Items.Count);
    }

    [Fact]
    public void Unique_names_count_on()
    {
        var scene = new Scene();
        scene.AddNull("Ball");
        scene.AddNull("Ball (2)");
        Assert.Equal("Ball (3)", Cloning.UniqueName(scene, "Ball"));
        Assert.Equal("Ball (3)", Cloning.UniqueName(scene, "Ball (2)"));
        Assert.Equal("Other", Cloning.UniqueName(scene, "Other"));
    }

    [Fact]
    public void Load_from_scene_brings_items_with_hierarchy_and_motion_renaming_clashes()
    {
        using var w = new Workbench();
        // Another scene: a null carrying the pawn (with a bone), a spot and a camera.
        var other = new SceneDocument();
        var carrier = other.Session.Add(new NullItem("Carrier"));
        var pawn = other.LoadObject(w.Object("pawn.lwo"));
        other.SetParent(pawn, carrier);
        other.Session.Edit("Bone", () => other.Scene.AddBone(pawn, "Spine"));
        carrier.SetPosition(1, new Vec3(0, 0, 0));
        carrier.SetPosition(20, new Vec3(4, 0, 0));
        var spot = other.Session.Add(new LightItem("Key", LightType.Spot) { ConeAngle = 15 });
        other.Scene.FirstFrame = 5;
        var path = w.File("other.avascene");
        other.Save(path);
        var lws = w.File("other.lws");
        other.Save(lws);

        var d = new SceneDocument();
        d.Session.Add(new NullItem("Carrier"));
        int steps = d.Session.History.Count;
        var result = d.LoadFromScene(path);

        Assert.Equal(steps + 1, d.Session.History.Count);
        Assert.Equal("Load from Scene other.avascene", d.Session.History.UndoLabel);
        var names = d.Scene.Items.Select(i => i.Name).ToList();
        Assert.Contains("Carrier (2)", names);
        Assert.Contains("pawn", names);
        Assert.Contains("Key", names);
        Assert.Equal(2, d.Scene.Cameras.Count());
        Assert.Equal("Camera (2)", d.Scene.Cameras.Last().Name);
        Assert.Same(d.Scene.Cameras.First(), d.Scene.CurrentCamera);
        var newPawn = d.Scene.Objects.Single();
        Assert.Equal("Carrier (2)", newPawn.Parent!.Name);
        Assert.Equal("Spine", Assert.Single(newPawn.Bones).Name);
        Assert.Equal(482, newPawn.BaseMesh().Points.Count);
        Assert.Equal(4, newPawn.WorldPosition(20).X, 6);
        Assert.Equal(15, ((LightItem)d.Scene.Find("Key")!).ConeAngle);
        // The other scene's settings stay behind.
        Assert.Equal(1, d.Scene.FirstFrame);
        Assert.Contains(result.Warnings, x => x.Contains("renamed"));
        d.Session.Undo();
        Assert.Equal(3, d.Scene.Items.Count);

        // From the .lws, without lights or cameras, and keeping names.
        var kept = d.LoadFromScene(lws, new MergeOptions { Lights = false, Cameras = false, Names = NameClash.Keep });
        Assert.DoesNotContain(kept.Items, i => i is LightItem or CameraItem);
        Assert.Equal(2, d.Scene.Items.Count(i => i.Name == "Carrier"));
        Assert.Contains(kept.Warnings, x => x.Contains("left out"));
    }

    [Fact]
    public void Load_from_scene_refuses_what_isnt_a_scene_and_changes_nothing()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        File.WriteAllText(w.File("bad.avascene"), "{ not json");
        Assert.ThrowsAny<Exception>(() => d.LoadFromScene(w.File("bad.avascene")));
        Assert.ThrowsAny<Exception>(() => d.LoadFromScene(w.File("missing.lws")));
        Assert.Equal(2, d.Scene.Items.Count);
        Assert.False(d.Session.History.CanUndo);
    }

    [Fact]
    public async Task Auto_save_writes_only_changes_and_leaves_the_scene_as_it_was()
    {
        using var w = new Workbench();
        var auto = new SceneAutosave(w.File("autosave"));
        var d = new SceneDocument();
        Assert.Null(auto.Waiting());
        auto.Start();
        Assert.False(await auto.SaveAsync(d));
        Assert.False(File.Exists(auto.ScenePath));

        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        d.Save(w.File("mine.lws"));
        pawn.SetPosition(1, new Vec3(1, 2, 3));
        d.Scene.Notify();
        d.EditSurface(pawn, 0, "Colour", s => s.Color = new Rgb(250, 0, 0));
        Assert.True(await auto.SaveAsync(d));
        Assert.True(File.Exists(auto.ScenePath));
        // The person's file and unsaved state are untouched.
        Assert.Equal(w.File("mine.lws"), d.Path);
        Assert.True(d.Modified);

        // A crash: the lock is still there next time, and the auto-save comes back untitled with its changes unsaved.
        var info = auto.Waiting();
        Assert.NotNull(info);
        Assert.Equal(w.File("mine.lws"), info!.Scene);
        var next = new SceneDocument();
        auto.Recover(next);
        Assert.Null(next.Path);
        Assert.True(next.Modified);
        var back = next.Scene.Objects.Single();
        Assert.Equal(new Vec3(1, 2, 3), back.PositionAt(1));
        Assert.Equal(new Rgb(250, 0, 0), back.Surfaces[0].Color);
        Assert.Contains(back, next.UnsavedObjects);
        Assert.False(next.Session.History.CanUndo);
        Assert.Contains("Recovered", next.Session.Status);

        // A clean exit leaves nothing to recover.
        auto.Finish();
        Assert.Null(auto.Waiting());
        Assert.False(File.Exists(auto.ScenePath));
    }
}

/// <summary>Morph targets from files and other objects, their weights keyed, and what's refused.</summary>
public class MorphTargetTests
{
    [Fact]
    public void A_target_from_a_file_blends_by_its_keyed_weight()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        double top = pawn.BaseMesh().Bounds().Max.Y;
        var m = MorphTargets.AddFromFile(d.Session, pawn, w.SquashedPawn());
        Assert.Equal("pawn-squash", m.Name);
        Assert.Equal("Add Morph pawn-squash to pawn", d.Session.History.UndoLabel);
        Assert.Equal(top, pawn.DeformedMesh(1).Bounds().Max.Y, 6);
        d.Session.SetFrame(1);
        MorphTargets.SetWeight(d.Session, pawn, m, 0);
        d.Session.SetFrame(21);
        MorphTargets.SetWeight(d.Session, pawn, m, 1);
        Assert.True(AVAModeler.Layout.Keying.IsAnimated(m.Weight));
        Assert.Equal(top * 0.5, pawn.DeformedMesh(21).Bounds().Max.Y, 6);
        Assert.InRange(pawn.DeformedMesh(11).Bounds().Max.Y, top * 0.5, top);
        // It renders and saves with the scene (.avascene keeps morphs).
        var path = w.File("morph.avascene");
        d.Save(path);
        var back = new SceneDocument();
        back.Load(path);
        Assert.Equal(top * 0.5, back.Scene.Objects.Single().DeformedMesh(21).Bounds().Max.Y, 6);
        d.Session.Undo();
        d.Session.Undo();
        d.Session.Undo();
        Assert.Empty(pawn.Morphs);
    }

    [Fact]
    public void A_target_from_another_object_and_what_is_refused()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        var squash = d.LoadObject(w.SquashedPawn());
        var crate = d.LoadObject(w.Object("crate.lwo"));
        Assert.Equal([squash], MorphTargets.Candidates(d.Scene, pawn));
        var m = MorphTargets.AddFromObject(d.Session, pawn, squash, weight: 1);
        Assert.Equal(pawn.BaseMesh().Bounds().Max.Y * 0.5, pawn.DeformedMesh(1).Bounds().Max.Y, 6);
        // A second one with the same name is numbered.
        Assert.Equal("pawn-squash 2", MorphTargets.AddFromObject(d.Session, pawn, squash).Name);
        int steps = d.Session.History.Count;
        Assert.Throws<ArgumentException>(() => MorphTargets.AddFromObject(d.Session, pawn, crate));
        Assert.Throws<ArgumentException>(() => MorphTargets.AddFromObject(d.Session, pawn, pawn));
        Assert.Throws<ArgumentException>(() => MorphTargets.AddFromFile(d.Session, pawn, w.Object("pawn.lwo"), layer: 9));
        Assert.Equal(steps, d.Session.History.Count);
        MorphTargets.Remove(d.Session, pawn, m);
        Assert.Single(pawn.Morphs);
    }
}
