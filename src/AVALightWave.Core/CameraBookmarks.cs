using System.Text.Json;
using System.Text.Json.Serialization;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>A saved camera view: where a camera was, which way it looked and its zoom.</summary>
public sealed record CameraBookmark(string Name, double[] Position, double[] Rotation, double Zoom)
{
    [JsonIgnore] public Vec3 At => new(Position[0], Position[1], Position[2]);
    [JsonIgnore] public Vec3 Turn => new(Rotation[0], Rotation[1], Rotation[2]);
}

/// <summary>
/// Camera bookmarks: named views kept with the scene (in its extras, so .avascene files keep them and undo puts them
/// back), saved from a camera at a frame and recalled onto any camera. Recalling keys the camera when it's animated
/// (or when asked), and otherwise just puts it there. Each change is one undo step.
/// </summary>
public static class CameraBookmarks
{
    public const string Key = "avalightwave.bookmarks";
    public const int MaxBookmarks = 200;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IReadOnlyList<CameraBookmark> List(Scene scene)
    {
        if (scene.Extra(Key) is not { } text) return [];
        try
        {
            return JsonSerializer.Deserialize<List<CameraBookmark>>(text, Json)?.Where(Valid).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Valid(CameraBookmark b) =>
        !string.IsNullOrWhiteSpace(b.Name) && b.Position is { Length: 3 } && b.Rotation is { Length: 3 } && b.Zoom > 0
        && b.Position.Concat(b.Rotation).All(double.IsFinite);

    /// <summary>Saves (or replaces) a bookmark of the camera as it is at the frame. One undo step.</summary>
    public static CameraBookmark Save(LayoutSession session, string name, CameraItem camera, double frame)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("A bookmark needs a name.");
        if (name.Length > 80) throw new ArgumentException("A bookmark's name is at most 80 characters.");
        var p = camera.PositionAt(frame);
        var r = camera.RotationAt(frame);
        var b = new CameraBookmark(name, [p.X, p.Y, p.Z], [r.X, r.Y, r.Z], camera.Zoom.Evaluate(frame));
        var list = List(session.Scene).Where(x => x.Name != name).ToList();
        if (list.Count >= MaxBookmarks) throw new InvalidOperationException($"A scene keeps at most {MaxBookmarks} bookmarks: remove one first.");
        list.Add(b);
        session.Edit($"Bookmark {name}", () => session.Scene.SetExtra(Key, JsonSerializer.Serialize(list, Json)));
        return b;
    }

    /// <summary>
    /// Puts a camera where a bookmark says at the frame: keyed when <paramref name="key"/> is true, or (when null) when
    /// that channel is animated; otherwise the value throughout. One undo step.
    /// </summary>
    public static CameraBookmark Recall(LayoutSession session, string name, CameraItem camera, double frame, bool? key = null)
    {
        var b = Find(session.Scene, name);
        session.Edit($"Go to bookmark {b.Name}", () =>
        {
            var s = session.Scene;
            var values = new[] { b.Position[0], b.Position[1], b.Position[2], b.Rotation[0], b.Rotation[1], b.Rotation[2] };
            for (int i = 0; i < 6; i++)
            {
                var env = camera.Channels[i];
                Keying.Set(env, s, frame, values[i], key ?? Keying.IsAnimated(env), $"{camera.Name}'s {SceneChannels.MotionNames[i]}");
            }
            Keying.Set(camera.Zoom, s, frame, b.Zoom, key ?? Keying.IsAnimated(camera.Zoom), $"{camera.Name}'s zoom");
            s.Notify();
        });
        return b;
    }

    /// <summary>Removes a bookmark. One undo step.</summary>
    public static void Remove(LayoutSession session, string name)
    {
        var b = Find(session.Scene, name);
        var list = List(session.Scene).Where(x => x.Name != b.Name).ToList();
        session.Edit($"Remove bookmark {b.Name}", () => session.Scene.SetExtra(Key, list.Count == 0 ? null : JsonSerializer.Serialize(list, Json)));
    }

    public static CameraBookmark Find(Scene scene, string name)
    {
        var list = List(scene);
        return list.FirstOrDefault(x => x.Name == name) ?? list.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(list.Count == 0 ? "The scene has no camera bookmarks yet." : $"There's no bookmark \"{name}\". The bookmarks are: {string.Join(", ", list.Select(x => x.Name))}.");
    }
}
