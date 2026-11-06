using AVAModeler.Core;
using AVAModeler.Formats;

namespace AVALightWave.Core.Tests;

/// <summary>A folder of a test's own, with copies of the repository's sample objects in it, deleted afterwards.</summary>
internal sealed class Workbench : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "avalightwave-" + Guid.NewGuid().ToString("N"));

    public Workbench()
    {
        Directory.CreateDirectory(Path);
        var objects = Directory.CreateDirectory(System.IO.Path.Combine(Path, "objects")).FullName;
        foreach (var f in Directory.GetFiles(Samples.Objects)) System.IO.File.Copy(f, System.IO.Path.Combine(objects, System.IO.Path.GetFileName(f)));
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);
    public string Object(string name) => System.IO.Path.Combine(Path, "objects", name);

    /// <summary>A small object saved in a format, made here (a triangle on a surface of the given colour).</summary>
    public string Triangle(string name, Rgb color)
    {
        var doc = new Document();
        doc.Surfaces[0].Color = color;
        var m = doc.Current.Mesh;
        m.Points.AddRange([new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(1, 0, 0)]);
        m.Polygons.Add(new Polygon([0, 1, 2], 0));
        m.Touch();
        var path = File(name);
        ObjectFiles.Save(doc, path);
        return path;
    }

    /// <summary>The pawn file again with its points squashed (wider, half as tall): a morph target for the pawn.</summary>
    public string SquashedPawn()
    {
        var doc = ObjectFiles.Load(Object("pawn.lwo"));
        foreach (var layer in doc.Layers)
        {
            var m = layer.Mesh;
            for (int i = 0; i < m.Points.Count; i++) m.Points[i] = new Vec3(m.Points[i].X * 1.5, m.Points[i].Y * 0.5, m.Points[i].Z * 1.5);
            m.Touch();
        }
        var path = File("pawn-squash.lwo");
        ObjectFiles.Save(doc, path);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { }
    }
}

internal static class Samples
{
    /// <summary>The repository's samples folder (found from the test's build folder).</summary>
    public static string Folder
    {
        get
        {
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                if (File.Exists(System.IO.Path.Combine(d.FullName, "AVALightWave.slnx"))) return System.IO.Path.Combine(d.FullName, "samples");
            throw new DirectoryNotFoundException("The repository's samples folder wasn't found.");
        }
    }

    public static string Objects => System.IO.Path.Combine(Folder, "objects");
}
