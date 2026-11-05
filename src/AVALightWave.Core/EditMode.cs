using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// What the mouse works on, as classic Layout's Edit buttons chose: objects (nulls count as objects, as they did then),
/// bones, lights or cameras. The view picks only items of the mode, and the Selected Item list shows only them.
/// </summary>
public enum EditMode { Objects, Bones, Lights, Cameras }

public static class EditModes
{
    /// <summary>Whether an item is one the mode works on.</summary>
    public static bool Includes(this EditMode mode, SceneItem item) => mode switch
    {
        EditMode.Objects => item is ObjectItem or NullItem,
        EditMode.Bones => item is Bone,
        EditMode.Lights => item is LightItem,
        EditMode.Cameras => item is CameraItem,
        _ => false,
    };

    /// <summary>The mode an item belongs to.</summary>
    public static EditMode Of(SceneItem item) => item switch
    {
        Bone => EditMode.Bones,
        LightItem => EditMode.Lights,
        CameraItem => EditMode.Cameras,
        _ => EditMode.Objects,
    };

    /// <summary>The scene's items of a mode, in the scene's order.</summary>
    public static IReadOnlyList<SceneItem> ItemsOf(this Scene scene, EditMode mode) => scene.Items.Where(i => mode.Includes(i)).ToList();

    /// <summary>
    /// The next (or previous) item of a mode after <paramref name="current"/>, round and round (classic Layout's up and
    /// down arrows); the first if the current one isn't of the mode; null if there are none.
    /// </summary>
    public static SceneItem? Step(this Scene scene, EditMode mode, SceneItem? current, int by)
    {
        var items = scene.ItemsOf(mode);
        if (items.Count == 0) return null;
        int at = current == null ? -1 : items.ToList().IndexOf(current);
        if (at < 0) return items[by >= 0 ? 0 : items.Count - 1];
        return items[((at + by) % items.Count + items.Count) % items.Count];
    }
}
