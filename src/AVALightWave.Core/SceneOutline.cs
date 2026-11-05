using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>One line of the Scene Editor: an item, how deep it sits under its parents, and its flags and keys.</summary>
public sealed record OutlineRow(SceneItem Item, int Depth, IReadOnlyList<double> Keys)
{
    public string Kind => Item.Kind;
    public string Name => Item.Name;
    public bool Visible => Item.Visible;
    public bool Locked => Item.Locked;

    /// <summary>The row as text: indented under its parent, its kind and name, and what's off ("hidden", "locked").</summary>
    public override string ToString() =>
        new string(' ', Depth * 2) + $"{Kind}: {Name}" + (Visible ? "" : " (hidden)") + (Locked ? " (locked)" : "");
}

/// <summary>
/// The Scene Editor's view of a scene: every item in its hierarchy (each under its parent, children in the scene's
/// order; a bone under its parent bone or its object), with its visibility, lock and the frames it has keys at on any
/// channel (motion, intensity, zoom, morphs, displacements, path position) inside the scene's frames.
/// </summary>
public static class SceneOutline
{
    public static IReadOnlyList<OutlineRow> Rows(Scene scene)
    {
        var rows = new List<OutlineRow>();
        var children = scene.Items.ToLookup(Above);
        void Add(SceneItem item, int depth)
        {
            rows.Add(new OutlineRow(item, depth, Keys(item, scene)));
            foreach (var child in children[item]) Add(child, depth + 1);
        }
        foreach (var root in scene.Items.Where(i => Above(i) == null)) Add(root, 0);
        return rows;
    }

    /// <summary>What an item sits under in the outline: its parent, or for a bone with none, its object.</summary>
    public static SceneItem? Above(SceneItem item) => item.Parent ?? (item as Bone)?.Owner;

    /// <summary>The frames an item has keys at on any of its animated channels (not a never-keyed constant), inside the scene's frames, in order.</summary>
    public static IReadOnlyList<double> Keys(SceneItem item, Scene scene)
    {
        var frames = new SortedSet<double>();
        foreach (var (_, env) in SceneChannels.Keyed(item, scene))
            foreach (var k in env.Keys)
                if (k.Frame >= scene.FirstFrame && k.Frame <= scene.LastFrame) frames.Add(k.Frame);
        return [.. frames];
    }
}
