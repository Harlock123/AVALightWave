using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>
/// The scene document: new, loading and saving scenes (.lws with objects saved beside, .avascene), several objects of
/// every format, Replace Object, saving objects, surfaces, hiding, locking and parenting, all as undo steps.
/// </summary>
public class SceneDocumentTests
{
    [Fact]
    public void A_new_document_is_classic_layouts_empty_scene()
    {
        var d = new SceneDocument();
        Assert.Equal(["Camera", "Light"], d.Scene.Items.Select(i => i.Kind));
        Assert.Equal("Untitled", d.Name);
        Assert.False(d.Modified);
        Assert.Equal("Untitled", d.Title);
        Assert.Empty(d.UnsavedObjects);
        d.Session.Add(new NullItem("Pin"));
        Assert.True(d.Modified);
        Assert.Equal("Untitled *", d.Title);
        d.New();
        Assert.Equal(2, d.Scene.Items.Count);
        Assert.False(d.Session.History.CanUndo);
    }

    [Fact]
    public void Objects_of_every_format_load_several_to_a_scene()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        var obj = w.Triangle("tri.obj", new Rgb(10, 200, 30));
        var glb = w.Triangle("tri.glb", new Rgb(200, 10, 30));
        var stl = w.Triangle("tri.stl", Rgb.White);
        var geo = w.Triangle("tri.geo", Rgb.White);
        foreach (var f in new[] { obj, glb, stl, geo }) d.LoadObject(f);
        Assert.Equal(5, d.Scene.Objects.Count());
        Assert.Equal(["pawn", "tri", "tri", "tri", "tri"], d.Scene.Objects.Select(o => o.Name));
        Assert.Equal(w.Object("pawn.lwo"), pawn.SourcePath);
        Assert.Equal(482, pawn.BaseMesh().Points.Count);
        Assert.Equal(d.Scene.Objects.Last(), d.Session.Selected);
        Assert.Empty(d.UnsavedObjects);
        // Each load is an undo step.
        d.Session.Undo();
        Assert.Equal(4, d.Scene.Objects.Count());
        Assert.ThrowsAny<Exception>(() => d.LoadObject(w.File("missing.lwo")));
        Assert.Equal(4, d.Scene.Objects.Count());
    }

    [Fact]
    public void Lws_scenes_save_objects_without_files_beside_them_and_load_back()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var pawn = d.LoadObject(w.Object("pawn.lwo"));
        pawn.SetPosition(1, new Vec3(0, 0, 0));
        d.Session.SetFrame(30);
        d.Session.SetVector(pawn, 0, new Vec3(1, 2, 3));
        // An object made in memory (as the hub's or an agent's might be) has no file.
        var made = d.Session.Edit("Add", () => d.Scene.AddObject("Made Here", Docs.Box()));
        d.SetParent(made, pawn);
        Assert.Single(d.UnsavedObjects);
        var notes = d.Save(w.File("scene.lws"));
        Assert.Contains(notes, n => n.Contains("Objects/Made Here.lwo", StringComparison.Ordinal));
        Assert.True(File.Exists(w.File(Path.Combine("Objects", "Made Here.lwo"))));
        Assert.Empty(d.UnsavedObjects);
        Assert.False(d.Modified);
        Assert.Equal("scene.lws", d.Name);

        var back = new SceneDocument();
        back.Load(w.File("scene.lws"));
        Assert.Empty(back.Session.Warnings);
        Assert.Equal(["pawn", "Made Here"], back.Scene.Objects.Select(o => o.Name));
        var p = back.Scene.Objects.First();
        Assert.Equal(new Vec3(1, 2, 3), p.PositionAt(30));
        Assert.Same(p, back.Scene.Objects.Last().Parent);
        Assert.Equal(8, back.Scene.Objects.Last().BaseMesh().Points.Count);
    }

    [Fact]
    public void Avascene_keeps_everything_with_meshes_inside_locks_and_hidden_items_too()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var ball = d.LoadObject(w.Object("ball.lwo"));
        d.SetVisible(ball, false);
        d.SetLocked(d.Scene.CurrentCamera!, true);
        Assert.Contains(d.Save(w.File("s.lws")), n => n.Contains("Locks are left out", StringComparison.Ordinal));
        d.Save(w.File("s.avascene"));
        File.Delete(w.Object("ball.lwo"));
        var back = new SceneDocument();
        back.Load(w.File("s.avascene"));
        var b = back.Scene.Objects.Single();
        Assert.False(b.Visible);
        Assert.True(back.Scene.CurrentCamera!.Locked);
        Assert.Equal(ball.BaseMesh().Points.Count, b.BaseMesh().Points.Count);
    }

    [Fact]
    public void A_scene_whose_object_is_gone_loads_with_a_placeholder_and_a_warning()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        d.LoadObject(w.Object("crate.lwo"));
        d.Save(w.File("crate.lws"));
        File.Delete(w.Object("crate.lwo"));
        var back = new SceneDocument();
        back.Load(w.File("crate.lws"));
        Assert.True(back.Scene.Objects.Single().IsPlaceholder);
        Assert.NotEmpty(back.Session.Warnings);
    }

    [Fact]
    public void The_sample_scenes_load()
    {
        foreach (var name in new[] { "showcase.lws", "showcase.avascene" })
        {
            var d = new SceneDocument();
            d.Load(Path.Combine(Samples.Folder, name));
            Assert.Empty(d.Session.Warnings);
            Assert.Equal(["floor", "pawn", "crate", "ball"], d.Scene.Objects.Select(o => o.Name.ToLowerInvariant()));
            Assert.All(d.Scene.Objects, o => Assert.False(o.IsPlaceholder));
            Assert.Equal(60, d.Scene.LastFrame);
        }
    }

    [Fact]
    public void Replace_object_keeps_the_motion_and_undo_brings_the_old_one_back()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var item = d.LoadObject(w.Object("pawn.lwo"));
        d.Session.SetFrame(10);
        d.Session.SetVector(item, 0, new Vec3(2, 0, 0));
        var bone = d.Session.Edit("Bone", () => d.Scene.AddBone(item, "Spine"));
        var old = item.Document;
        d.ReplaceObject(item, w.Object("crate.lwo"));
        Assert.Equal("crate", item.Name);
        Assert.Equal(8, item.BaseMesh().Points.Count);
        Assert.Equal(new Vec3(2, 0, 0), item.PositionAt(10));
        Assert.Contains(bone, item.Bones);
        Assert.Equal(w.Object("crate.lwo"), item.SourcePath);
        Assert.Equal("Undo Replace pawn", "Undo " + d.Session.History.UndoLabel);
        d.Session.Undo();
        Assert.Same(old, item.Document);
        Assert.Equal("pawn", item.Name);
        // A name the person chose stays.
        item.Name = "Hero";
        d.ReplaceObject(item, w.Object("ball.lwo"));
        Assert.Equal("Hero", item.Name);
    }

    [Fact]
    public void Surfaces_change_as_undo_steps_and_save_with_the_object()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var first = d.LoadObject(w.Object("ball.lwo"));
        var twin = d.Session.Edit("Twin", () => d.Scene.AddObject("Twin", first.Document));
        var old = first.Document!;
        d.EditSurface(first, 0, "Ball Colour", s => s.Color = new Rgb(1, 2, 3));
        Assert.NotSame(old, first.Document);
        Assert.Same(first.Document, twin.Document);
        Assert.Equal(new Rgb(1, 2, 3), first.Surfaces[0].Color);
        Assert.NotEqual(new Rgb(1, 2, 3), old.Surfaces[0].Color);
        Assert.True(first.Document!.Modified);
        Assert.Single(d.UnsavedObjects);
        d.Session.Undo();
        Assert.Same(old, first.Document);
        d.Session.Redo();
        Assert.Contains("Saved ball.", d.SaveAllObjects());
        Assert.Empty(d.UnsavedObjects);
        Assert.Equal(new Rgb(1, 2, 3), ObjectFiles.Load(w.Object("ball.lwo")).Surfaces[0].Color);
        Assert.Throws<ArgumentOutOfRangeException>(() => d.EditSurface(first, 9, "x", _ => { }));
    }

    [Fact]
    public void Save_object_writes_a_format_by_extension_and_makes_it_the_objects_file()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var made = d.Session.Edit("Add", () => d.Scene.AddObject("Made", Docs.Box()));
        Assert.Throws<InvalidOperationException>(() => d.SaveObject(made));
        Assert.Contains(d.SaveAllObjects(), n => n.Contains("Save Object As", StringComparison.Ordinal));
        d.SaveObject(made, w.File("made.glb"));
        Assert.Equal(w.File("made.glb"), made.SourcePath);
        Assert.False(made.Document!.Modified);
        Assert.Equal(8, ObjectFiles.Load(w.File("made.glb")).Current.Mesh.Points.Count);
        Assert.Throws<InvalidOperationException>(() => d.SaveObject(made, w.File("made.txt")));
    }

    [Fact]
    public void Hiding_locking_and_parenting_are_undo_steps()
    {
        var d = new SceneDocument();
        var light = d.Scene.Lights.Single();
        var cam = d.Scene.CurrentCamera!;
        d.SetVisible(light, false);
        Assert.Equal("Hide Light", d.Session.History.UndoLabel);
        d.SetLocked(cam, true);
        Assert.Equal("Lock Camera", d.Session.History.UndoLabel);
        d.SetParent(light, cam);
        Assert.Same(cam, light.Parent);
        Assert.Throws<InvalidOperationException>(() => d.SetParent(cam, light));
        int steps = d.Session.History.Count;
        d.SetVisible(light, false);
        Assert.Equal(steps, d.Session.History.Count);
        d.Session.Undo();
        d.Session.Undo();
        d.Session.Undo();
        Assert.True(light.Visible);
        Assert.False(cam.Locked);
        Assert.Null(light.Parent);
    }
}

internal static class Docs
{
    /// <summary>A two-metre box, in memory, with no file.</summary>
    public static Document Box()
    {
        var doc = new Document();
        var m = doc.Current.Mesh;
        for (int i = 0; i < 8; i++) m.Points.Add(new Vec3(i & 1, (i >> 1) & 1, (i >> 2) & 1) * 2 - Vec3.One);
        int[][] faces = [[0, 1, 3, 2], [4, 6, 7, 5], [0, 4, 5, 1], [2, 3, 7, 6], [0, 2, 6, 4], [1, 5, 7, 3]];
        foreach (var f in faces) m.Polygons.Add(new Polygon(f, 0));
        m.Touch();
        doc.Modified = true;
        return doc;
    }
}
