using System.Text.Json;
using System.Text.Json.Serialization;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>A key in a clip: its frame from the clip's start, its value, and its shape.</summary>
public sealed record ClipKey(double Frame, double Value, double Tension = 0, double Continuity = 0, double Bias = 0, string Span = "tcb", double InSlope = 0, double OutSlope = 0);

/// <summary>One channel of one item in a clip. Items are named as the scene names them; a bone as "Object/Bone".</summary>
public sealed record ClipChannel(string Item, string Channel, List<ClipKey> Keys);

/// <summary>
/// An action clip: a named run of keys over several items or bones (a wave, a step, a jump), kept from a span of frames
/// so it can be placed again at any frame, on the same items or (by its owner's name) another character's.
/// </summary>
public sealed record ActionClip(string Name, double Length, List<ClipChannel> Channels);

/// <summary>
/// Action clips: saved from a span of frames, kept with the scene (its extras) and in .action files, and placed at a
/// frame, scaled in time, looped and blended in. Each change is one undo step.
/// </summary>
public static class ActionClips
{
    public const string Key = "avalightwave.actions";
    public const string Extension = ".action";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static IReadOnlyList<ActionClip> List(Scene s)
    {
        if (s.Extra(Key) is not { } text) return [];
        try { return JsonSerializer.Deserialize<List<ActionClip>>(text, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static ActionClip Find(Scene s, string name) =>
        List(s).FirstOrDefault(c => c.Name == name) ?? List(s).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(List(s).Count == 0 ? "The scene has no action clips yet: save_action keeps one." : $"There's no action \"{name}\". They are: {string.Join(", ", List(s).Select(c => c.Name))}.");

    /// <summary>An item's name in a clip: its own, or "Object/Bone" for a bone.</summary>
    public static string NameOf(SceneItem item) => item is Bone b ? $"{b.Owner.Name}/{b.Name}" : item.Name;

    /// <summary>The item a clip's name stands for, with the owner renamed (for placing onto another character).</summary>
    public static SceneItem? Resolve(Scene s, string name, string? from = null, string? to = null)
    {
        int slash = name.IndexOf('/');
        string owner = slash < 0 ? name : name[..slash];
        if (from != null && to != null && owner == from) owner = to;
        if (slash < 0) return s.Items.FirstOrDefault(i => i is not Bone && i.Name == owner);
        string bone = name[(slash + 1)..];
        return s.Bones.FirstOrDefault(b => b.Owner.Name == owner && b.Name == bone);
    }

    /// <summary>
    /// Keeps the keys of <paramref name="items"/> (and, for an object, its bones) from <paramref name="from"/> to
    /// <paramref name="to"/> as a clip: every animated channel, with its value at both ends keyed, so the clip stands on its own.
    /// </summary>
    public static ActionClip Capture(Scene s, string name, IEnumerable<SceneItem> items, double from, double to, bool bones = true)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("An action needs a name.");
        if (!(to > from)) throw new ArgumentException("An action's last frame must be after its first.");
        var chosen = new List<SceneItem>();
        foreach (var it in items)
        {
            if (!chosen.Contains(it)) chosen.Add(it);
            if (bones && it is ObjectItem o) foreach (var b in o.Bones) if (!chosen.Contains(b)) chosen.Add(b);
        }
        var channels = new List<ClipChannel>();
        foreach (var it in chosen)
            foreach (var (channel, env) in SceneChannels.Of(it))
            {
                if (!Keying.IsAnimated(env)) continue;
                var keys = new List<ClipKey>();
                if (env.IndexOf(from) < 0) keys.Add(new ClipKey(0, env.Evaluate(from)));
                foreach (var k in env.Keys.Where(k => k.Frame >= from - 1e-9 && k.Frame <= to + 1e-9))
                    keys.Add(new ClipKey(k.Frame - from, k.Value, k.Tension, k.Continuity, k.Bias, SpanName(k.Kind), k.InSlope, k.OutSlope));
                if (env.IndexOf(to) < 0) keys.Add(new ClipKey(to - from, env.Evaluate(to)));
                channels.Add(new ClipChannel(NameOf(it), channel, keys));
            }
        if (channels.Count == 0) throw new ArgumentException("Nothing there is animated between those frames, so there's no action to keep.");
        return new ActionClip(name.Trim(), to - from, channels);
    }

    /// <summary>Keeps a clip with the scene (replacing one of its name), as one undo step.</summary>
    public static void Keep(LayoutSession session, ActionClip clip)
    {
        var list = List(session.Scene).Where(c => c.Name != clip.Name).Append(clip).ToList();
        session.Edit($"Keep action {clip.Name}", () => session.Scene.SetExtra(Key, JsonSerializer.Serialize(list, Json)));
    }

    public static void Remove(LayoutSession session, string name)
    {
        var clip = Find(session.Scene, name);
        var list = List(session.Scene).Where(c => c.Name != clip.Name).ToList();
        session.Edit($"Remove action {clip.Name}", () => session.Scene.SetExtra(Key, list.Count == 0 ? null : JsonSerializer.Serialize(list, Json)));
    }

    private static readonly JsonSerializerOptions Pretty = new(Json) { WriteIndented = true };

    public static void Save(ActionClip clip, string path) => File.WriteAllText(path, JsonSerializer.Serialize(clip, Pretty));

    public static ActionClip Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ActionClip>(File.ReadAllText(path), Json) is { Name.Length: > 0, Channels: not null } c ? c
                : throw new InvalidDataException($"{Path.GetFileName(path)} isn't an action clip.");
        }
        catch (JsonException e) { throw new InvalidDataException($"{Path.GetFileName(path)} isn't an action clip: {e.Message}", e); }
    }

    /// <summary>What placing a clip did: each channel written, and names that weren't found.</summary>
    public sealed record Placed(int Channels, int Keys, double First, double Last, IReadOnlyList<string> Missing);

    /// <summary>
    /// Places a clip at a frame (one undo step): its keys, scaled in time by <paramref name="speed"/>'s inverse (2 plays
    /// it twice as fast), repeated <paramref name="loops"/> times, replacing the keys there on its channels; with
    /// <paramref name="blend"/> frames, the first so many frames ease from the motion there into the clip (keyed every
    /// frame). <paramref name="from"/> and <paramref name="to"/> rename the owner (another character with the same bones).
    /// </summary>
    public static Placed Place(LayoutSession session, ActionClip clip, double at, double speed = 1, int loops = 1, double blend = 0, string? from = null, string? to = null)
    {
        if (!(speed > 0) || speed > 100) throw new ArgumentException("speed must be more than 0 (2 plays it twice as fast).");
        if (loops < 1 || loops > 1000) throw new ArgumentException("loops must be from 1 to 1000.");
        if (blend < 0) throw new ArgumentException("blend must be 0 or more frames.");
        var s = session.Scene;
        double length = clip.Length / speed, end = at + length * loops;
        if (blend > length) throw new ArgumentException($"blend ({blend} frames) can't be longer than the clip ({length:0.##} frames).");
        var missing = new List<string>();
        int channels = 0, keys = 0;
        session.Edit($"Place action {clip.Name}", () =>
        {
            foreach (var ch in clip.Channels)
            {
                var item = Resolve(s, ch.Item, from, to);
                var env = item == null ? null : SceneChannels.Find(item, ch.Channel);
                if (env == null) { missing.Add($"{ch.Item}'s {ch.Channel}"); continue; }
                // The motion there before, for blending.
                var before = env.Clone();
                for (int i = env.Keys.Count - 1; i >= 0; i--)
                    if (env.Keys[i].Frame >= at - 1e-9 && env.Keys[i].Frame <= end + 1e-9 && env.Keys.Count > 1) env.RemoveKey(i);
                for (int loop = 0; loop < loops; loop++)
                    foreach (var k in ch.Keys)
                    {
                        if (loop > 0 && k.Frame <= 1e-9) continue; // a loop's first key is the last one's end
                        double f = at + (loop * clip.Length + k.Frame) / speed;
                        var key = new EnvelopeKey(Math.Round(f, 4), k.Value, k.Tension, k.Continuity, k.Bias,
                            InSlope: k.InSlope * speed, OutSlope: k.OutSlope * speed).WithSpan(SpanOf(k.Span));
                        Keying.Key(env, s, key, keepShape: false);
                        keys++;
                    }
                if (blend > 0)
                {
                    // Ease from what was there into the clip over the first frames: a key a frame, the clip's weight smoothly rising.
                    var clipCurve = env.Clone();
                    for (double f = at; f < at + blend - 1e-9; f += 1)
                    {
                        double t = (f - at) / blend, w = t * t * (3 - 2 * t);
                        double v = before.Evaluate(f) * (1 - w) + clipCurve.Evaluate(f) * w;
                        Keying.Key(env, s, new EnvelopeKey(f, v), keepShape: false);
                        keys++;
                    }
                }
                channels++;
            }
            if (channels == 0) throw new ArgumentException($"None of the clip's items are in the scene ({string.Join(", ", clip.Channels.Select(c => c.Item).Distinct())})" +
                (from == null ? ": give onto to put it on another item." : "."));
            if (end > s.LastFrame) s.LastFrame = (int)Math.Ceiling(end);
            s.Notify();
        });
        return new Placed(channels, keys, at, end, missing);
    }

    private static string SpanName(SpanKind k) => k switch { SpanKind.Linear => "linear", SpanKind.Stepped => "stepped", SpanKind.Bezier => "bezier", _ => "tcb" };

    private static SpanKind SpanOf(string s) => s switch { "linear" => SpanKind.Linear, "stepped" => SpanKind.Stepped, "bezier" => SpanKind.Bezier, _ => SpanKind.Tcb };
}
