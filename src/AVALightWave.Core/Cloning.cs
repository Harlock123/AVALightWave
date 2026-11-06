using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// How Clone Item copies: how many copies, and what each one adds to the one before it — a move, a turn and a change
/// of size (added to every key of those channels, so a clone of an animated item moves as the original does, offset),
/// and a time offset in frames (every key of the copy's animated channels later by that much, so clones follow one
/// another in time, as in a stagger).
/// </summary>
public sealed record CloneOptions
{
    /// <summary>How many copies, 1 to <see cref="Cloning.MaxCopies"/>.</summary>
    public int Count { get; init; } = 1;
    /// <summary>Each copy's move from the one before, metres.</summary>
    public Vec3 Position { get; init; }
    /// <summary>Each copy's turn from the one before: heading, pitch, bank in degrees.</summary>
    public Vec3 Rotation { get; init; }
    /// <summary>Each copy's change of scale from the one before, added (0.1 makes each a tenth bigger than the original).</summary>
    public Vec3 Scale { get; init; }
    /// <summary>Each copy's motion this many frames after the one before (negative: before).</summary>
    public double TimeOffset { get; init; }

    /// <summary>Throws an <see cref="ArgumentException"/> saying what's wrong, if anything.</summary>
    public CloneOptions Checked()
    {
        if (Count < 1 || Count > Cloning.MaxCopies) throw new ArgumentException($"The number of clones must be from 1 to {Cloning.MaxCopies}.");
        foreach (var v in new[] { Position, Rotation, Scale })
            if (!double.IsFinite(v.X) || !double.IsFinite(v.Y) || !double.IsFinite(v.Z)) throw new ArgumentException("The offsets must be numbers.");
        if (!double.IsFinite(TimeOffset)) throw new ArgumentException("The time offset must be a number of frames.");
        return this;
    }
}

/// <summary>
/// Clone Item, as classic Layout had it: copies of an object, light, camera or null with its motion and settings (an
/// object with its bones, morphs and displacements), named after it ("Ball (2)", "Ball (3)"…), its parent kept, each
/// copy offset from the one before by <see cref="CloneOptions"/>. All the copies are one undo step. Built on the engine's
/// <see cref="Scene.CopyItems"/>.
/// </summary>
public static class Cloning
{
    public const int MaxCopies = 1000;

    /// <summary>Clones an item as one undo step; returns the copies, in order. Bones are cloned with their object, not alone.</summary>
    public static IReadOnlyList<SceneItem> Clone(LayoutSession session, SceneItem item, CloneOptions options)
    {
        options.Checked();
        if (item.Scene != session.Scene) throw new ArgumentException($"{item.Name} isn't in the scene.");
        if (item is Bone b) throw new ArgumentException($"{item.Name} is a bone: clone its object, {b.Owner.Name}, and its bones come with it.");
        var scene = session.Scene;
        var copies = new List<SceneItem>();
        session.Edit(options.Count == 1 ? $"Clone {item.Name}" : $"Clone {item.Name} ×{options.Count}", () => scene.Batch(() =>
        {
            for (int k = 1; k <= options.Count; k++)
            {
                var map = scene.CopyItems([item]);
                var copy = map[item];
                copy.Name = UniqueName(scene, item.Name, copy);
                Offset(copy, options, k, scene);
                copies.Add(copy);
            }
            scene.Notify();
        }));
        return copies;
    }

    /// <summary>A name like the original's not used by another item (bones aside): "Ball (2)", "Ball (3)"…</summary>
    public static string UniqueName(Scene scene, string name, SceneItem? except = null)
    {
        bool Taken(string n) => scene.Items.Any(i => i != except && i is not Bone && i.Name == n);
        if (!Taken(name)) return name;
        // A name that already ends in " (n)" counts on from it rather than nesting.
        var stem = name;
        int start = 2;
        if (System.Text.RegularExpressions.Regex.Match(name, @"^(.*) \((\d+)\)$") is { Success: true } m)
        {
            stem = m.Groups[1].Value;
            start = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1;
        }
        for (int n = start; ; n++)
            if (!Taken($"{stem} ({n})")) return $"{stem} ({n})";
    }

    private static void Offset(SceneItem copy, CloneOptions o, int k, Scene scene)
    {
        Add(copy, 0, o.Position * k);
        Add(copy, 3, o.Rotation * k);
        Add(copy, 6, o.Scale * k);
        if (o.TimeOffset != 0)
            foreach (var (_, env) in SceneChannels.Of(copy))
                if (Keying.IsAnimated(env)) Shift(env, o.TimeOffset * k);
        if (copy is ObjectItem obj)
            foreach (var bone in obj.Bones)
                if (o.TimeOffset != 0)
                    foreach (var (_, env) in SceneChannels.Of(bone))
                        if (Keying.IsAnimated(env)) Shift(env, o.TimeOffset * k);
    }

    /// <summary>Adds to every key of three channels (position, rotation or scale from <paramref name="first"/>).</summary>
    private static void Add(SceneItem item, int first, Vec3 by)
    {
        for (int i = 0; i < 3; i++)
        {
            double d = by[i];
            if (d == 0) continue;
            var env = item.Channels[first + i];
            if (env.Keys.Count == 0)
            {
                env.SetConstant((first == 6 ? 1 : 0) + d);
                continue;
            }
            var keys = env.Keys.ToList();
            var moved = new Envelope { PreBehavior = env.PreBehavior, PostBehavior = env.PostBehavior };
            foreach (var key in keys) moved.AddKey(key with { Value = key.Value + d });
            env.CopyFrom(moved);
        }
    }

    /// <summary>Moves every key of an envelope later by some frames.</summary>
    private static void Shift(Envelope env, double frames)
    {
        var moved = new Envelope { PreBehavior = env.PreBehavior, PostBehavior = env.PostBehavior };
        foreach (var key in env.Keys) moved.AddKey(key with { Frame = key.Frame + frames });
        env.CopyFrom(moved);
    }
}
