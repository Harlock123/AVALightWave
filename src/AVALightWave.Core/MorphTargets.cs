using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// Morph envelopes in Layout, as classic Layout's Metamorph did with a morph target object: an object blends toward the
/// shape of another object file (or another object in the scene, or one of its own file's layers) with the same points
/// in the same order, by a keyable weight (the engine's morph.&lt;name&gt; channel: 0 the object's own shape, 1 the
/// target, between blends). Several targets add up. Each change is one undo step; the target's shape is copied when it's
/// added (later changes to its file don't reach it), as the engine keeps morph meshes by reference.
/// </summary>
public static class MorphTargets
{
    /// <summary>Adds a morph whose target is a file's shape (one layer, or every non-empty layer merged), as one undo step.</summary>
    public static MorphTarget AddFromFile(LayoutSession session, ObjectItem obj, string path, string? name = null, int? layer = null, double weight = 0)
    {
        var doc = ObjectFiles.Load(path);
        var mesh = MeshOf(doc, layer, Path.GetFileName(path));
        return Add(session, obj, mesh, name ?? Path.GetFileNameWithoutExtension(path), weight, Path.GetFileName(path) + (layer is int l ? $" layer {l}" : ""));
    }

    /// <summary>Adds a morph whose target is another scene object's shape (as it is now, undeformed), as one undo step.</summary>
    public static MorphTarget AddFromObject(LayoutSession session, ObjectItem obj, ObjectItem source, string? name = null, double weight = 0)
    {
        if (source == obj) throw new ArgumentException($"{obj.Name} can't be its own morph target.");
        if (source.IsPlaceholder) throw new ArgumentException($"{source.Name} couldn't be loaded, so it has no shape.");
        return Add(session, obj, source.BaseMesh(), name ?? source.Name, weight, source.Name);
    }

    /// <summary>The objects in the scene with as many points as <paramref name="obj"/> (possible targets for it).</summary>
    public static IReadOnlyList<ObjectItem> Candidates(Scene scene, ObjectItem obj)
    {
        int count = obj.IsPlaceholder ? -1 : obj.BaseMesh().Points.Count;
        return scene.Objects.Where(o => o != obj && !o.IsPlaceholder && o.BaseMesh().Points.Count == count).ToList();
    }

    /// <summary>Sets a morph's weight at the session's frame by the keying rule (Auto Key keys it), as one undo step.</summary>
    public static void SetWeight(LayoutSession session, ObjectItem obj, MorphTarget morph, double weight)
    {
        if (!double.IsFinite(weight)) throw new ArgumentException("The weight must be a number (0 the object's own shape, 1 the target).");
        session.Edit($"Set {obj.Name}'s {morph.Name}", () =>
        {
            var env = morph.Weight;
            bool key = session.AutoKey || Keying.IsAnimated(env) && env.IndexOf(session.Frame) < 0;
            Keying.Set(env, session.Scene, session.Frame, weight, key, $"{obj.Name}'s morph {morph.Name}");
            session.Scene.Notify();
        });
    }

    /// <summary>Takes a morph off its object (with its weight keys), as one undo step.</summary>
    public static void Remove(LayoutSession session, ObjectItem obj, MorphTarget morph) =>
        session.Edit($"Remove {obj.Name}'s {morph.Name}", () => obj.RemoveMorph(morph));

    private static MorphTarget Add(LayoutSession session, ObjectItem obj, Mesh target, string name, double weight, string from)
    {
        if (obj.IsPlaceholder) throw new ArgumentException($"{obj.Name} couldn't be loaded, so it has no shape to morph.");
        if (!double.IsFinite(weight)) throw new ArgumentException("The weight must be a number (0 the object's own shape, 1 the target).");
        int count = obj.BaseMesh().Points.Count;
        if (target.Points.Count != count)
            throw new ArgumentException($"{from} has {target.Points.Count} points, but {obj.Name} has {count}: a morph target needs the same points in the same order (made from the object in a modeler).");
        var unique = UniqueName(obj, name.Trim().Length > 0 ? name.Trim() : "Morph");
        return session.Edit($"Add Morph {unique} to {obj.Name}", () => obj.AddMorph(new MorphTarget(unique, target, new Envelope(weight))));
    }

    private static Mesh MeshOf(Document doc, int? layer, string file)
    {
        var mesh = new Mesh();
        if (layer is int n)
        {
            if (n < 1 || n > doc.Layers.Count) throw new ArgumentException($"{file} has layers 1 to {doc.Layers.Count}, not {n}.");
            mesh.Append(doc.Layers[n - 1].Mesh);
        }
        else
            foreach (var l in doc.Layers)
                if (!l.Mesh.IsEmpty) mesh.Append(l.Mesh);
        return mesh;
    }

    private static string UniqueName(ObjectItem obj, string name)
    {
        bool Taken(string n) => obj.Morphs.Any(m => string.Equals(m.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(name)) return name;
        for (int i = 2; ; i++)
            if (!Taken($"{name} {i}")) return $"{name} {i}";
    }
}
