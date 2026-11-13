using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>Package Scene: the scene and everything it uses in one folder that opens elsewhere, the open scene untouched.</summary>
public class PackageTests
{
    [Fact]
    public void A_package_holds_the_scene_its_objects_and_images_and_opens_from_anywhere()
    {
        using var w = new Workbench();
        var d = new SceneDocument();
        var crate = d.LoadObject(w.Object("crate.lwo"));
        // An object made here, with no file, and a texture.
        var made = new Document();
        made.Current.Mesh.Points.AddRange([new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0)]);
        made.Current.Mesh.Polygons.Add(new Polygon([0, 1, 2], 0));
        made.Current.Mesh.Touch();
        made.Surfaces[0].Textures.Add(new TextureLayer { Image = w.File("images/frame.png") });
        made.Modified = true;
        d.Scene.AddObject("Made", made);
        d.Scene.Images = [new SceneImage { Path = w.File("images/sky.png") }, new SceneImage { Path = w.File("images/stripes0001.png"), Sequence = true }];
        d.Scene.Compositing = d.Scene.Compositing with { Background = w.File("images/sky.png") };
        var before = SceneJson.Write(d.Scene);

        var r = ScenePackage.Make(d.Scene, w.File("out/pack"), "shot", zip: true);
        Assert.Equal(before, SceneJson.Write(d.Scene)); // the open scene is untouched
        Assert.True(File.Exists(w.File("out/pack/shot.avascene")));
        Assert.True(File.Exists(w.File("out/pack/objects/crate.lwo")));
        Assert.True(File.Exists(w.File("out/pack/objects/Made.lwo")));
        Assert.True(File.Exists(w.File("out/pack/images/frame.png")));
        Assert.Equal(12, Directory.GetFiles(w.File("out/pack/images"), "stripes*.png").Length);
        Assert.True(File.Exists(w.File("out/pack.zip")));
        Assert.Empty(r.Warnings);
        var text = File.ReadAllText(r.Scene);
        Assert.DoesNotContain(w.Path, text);

        // Moved somewhere else entirely, it still opens with its pictures.
        var elsewhere = Path.Combine(Path.GetTempPath(), "avalightwave-moved-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(w.File("out/pack"), elsewhere);
            var e = new SceneDocument();
            e.Load(Path.Combine(elsewhere, "shot.avascene"));
            Assert.Equal(2, e.Scene.Objects.Count());
            Assert.All(e.Scene.Images, i => Assert.StartsWith(elsewhere, i.Path));
            Assert.StartsWith(elsewhere, e.Scene.Compositing.Background);
            Assert.Equal(8, ((ObjectItem)e.Scene.Find("crate")!).BaseMesh().Points.Count);
        }
        finally { if (Directory.Exists(elsewhere)) Directory.Delete(elsewhere, true); }

        Assert.Throws<IOException>(() => ScenePackage.Make(d.Scene, w.File("objects"), "x"));
        Assert.Throws<ArgumentException>(() => ScenePackage.Make(d.Scene, w.File("out/p2"), "a/b"));
    }
}
