using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>What Load from Scene does with an item whose name is already taken in the scene.</summary>
public enum NameClash
{
    /// <summary>The new item is renamed: "Ball (2)".</summary>
    Rename,
    /// <summary>Both keep the name (items are told apart by their place in the list).</summary>
    Keep,
}

/// <summary>Which of another scene's items Load from Scene brings in, and what it does about names.</summary>
public sealed record MergeOptions
{
    /// <summary>Bring its lights.</summary>
    public bool Lights { get; init; } = true;
    /// <summary>Bring its cameras (they never become the current camera).</summary>
    public bool Cameras { get; init; } = true;
    public NameClash Names { get; init; } = NameClash.Rename;
}

/// <summary>What Load from Scene brought in, and what it couldn't.</summary>
public sealed record MergeResult(IReadOnlyList<SceneItem> Items, IReadOnlyList<string> Warnings);

/// <summary>
/// Load from Scene (classic Layout's Load Items From Scene): another .lws or .avascene's objects (with their bones),
/// nulls, and if asked its lights and cameras, with their hierarchy, motion and settings, into the scene open now, as one
/// undo step. The other scene's own settings (frames, backdrop, effects, current camera) are left behind; names already
/// taken are renamed unless asked not to. Built on the engine's scene readers and <see cref="Scene.CopyItems"/>.
/// </summary>
public static class SceneMerge
{
    /// <summary>Reads a scene file (objects through <paramref name="load"/>, a placeholder for any that can't be had).</summary>
    public static SceneLoadResult Read(string path, Func<string, Document?> load) =>
        path.EndsWith(SceneJson.Extension, StringComparison.OrdinalIgnoreCase) ? SceneJson.ReadFile(path, load) : LwsReader.ReadFile(path, load);

    /// <summary>Brings another scene's items into the session's scene as one undo step; returns them, with warnings.</summary>
    public static MergeResult Load(LayoutSession session, string path, MergeOptions options, Func<string, Document?> load)
    {
        var read = Read(path, load);
        var warnings = new List<string>(read.Warnings);
        var other = read.Scene;
        var chosen = other.Items.Where(i => i switch
        {
            LightItem => options.Lights,
            CameraItem => options.Cameras,
            Bone => false, // they come with their objects
            _ => true,
        }).ToList();
        if (chosen.Count == 0)
        {
            warnings.Add($"{Path.GetFileName(path)} has nothing to bring in{(options.Lights && options.Cameras ? "" : " of the kinds chosen")}.");
            return new MergeResult([], warnings);
        }
        var scene = session.Scene;
        var added = new List<SceneItem>();
        int renamed = 0;
        session.Edit($"Load from Scene {Path.GetFileName(path)}", () => scene.Batch(() =>
        {
            var map = scene.CopyItems(chosen);
            foreach (var (original, copy) in map)
            {
                if (options.Names == NameClash.Rename && copy is not Bone)
                {
                    var name = Cloning.UniqueName(scene, copy.Name, copy);
                    if (name != copy.Name) renamed++;
                    copy.Name = name;
                }
                added.Add(copy);
            }
            scene.Notify();
        }));
        if (renamed > 0) warnings.Add($"{renamed} item(s) were renamed, as their names were taken.");
        var left = other.Items.Count(i => i is LightItem && !options.Lights || i is CameraItem && !options.Cameras);
        if (left > 0) warnings.Add($"{left} light(s) or camera(s) were left out, as asked.");
        return new MergeResult(added, warnings);
    }
}
