using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using SceneModel = AVAModeler.Scene.Scene;

namespace AVALightWave.Mcp;

/// <summary>
/// Motion in a phrase (the milestone's second stage): moves such as "bounce three times" or "orbit the ball once",
/// each turned into ordinary keys on an item's channels over a span of frames, so the graph editor and the dope sheet
/// can change them afterwards. Moves are applied in order, each starting from where the moves before left the item.
/// Call it inside one <see cref="LayoutSession.Edit{T}"/>: a mistake anywhere throws and the edit puts everything back.
/// </summary>
internal sealed class Motion(LayoutSession l)
{
    public static readonly IReadOnlyList<string> Kinds =
    [
        "move", "turn", "grow", "bounce", "spin", "orbit", "sway", "bob", "shake", "pop_in", "pop_out", "fade_in", "fade_out",
        "through", "track", "dolly", "push_in", "crane", "truck", "follow",
    ];

    public static readonly IReadOnlyList<string> Eases = ["linear", "ease_in", "ease_out", "ease_in_out", "overshoot"];

    public const int MaxMoves = 200;
    public const double DefaultDuration = 30;

    private static readonly string[] Common = ["name", "item", "kind", "start", "after", "with", "delay", "duration", "end", "ease", "every", "aim"];

    private static readonly Dictionary<string, string[]> Own = new()
    {
        ["move"] = ["to", "by"], ["turn"] = ["to", "by"], ["grow"] = ["to", "by"],
        ["bounce"] = ["height", "count", "decay", "squash", "start_at", "to", "by"],
        ["spin"] = ["axis", "turns", "degrees"],
        ["orbit"] = ["around", "radius", "turns", "degrees", "height", "face"],
        ["sway"] = ["channel", "amplitude", "cycles", "damping"], ["bob"] = ["amplitude", "cycles", "damping"],
        ["shake"] = ["amplitude", "rotate", "seed"],
        ["pop_in"] = [], ["pop_out"] = [], ["fade_in"] = [], ["fade_out"] = [],
        ["through"] = ["points", "face"], ["track"] = ["target"],
        ["dolly"] = ["distance"], ["push_in"] = ["target", "distance"], ["crane"] = ["height", "target"],
        ["truck"] = ["distance", "target"], ["follow"] = ["target", "offset"],
    };

    private sealed record Done(string? Name, SceneItem Item, string Kind, double Start, double End);

    private readonly Dictionary<string, Done> _named = new(StringComparer.Ordinal);
    private Done? _previous;
    private int _keys;
    private readonly SortedSet<string> _channels = new(StringComparer.Ordinal);

    private SceneModel S => l.Scene;

    /// <summary>Applies the moves; returns what each did. With <paramref name="extend"/>, a scene too short for them is lengthened.</summary>
    public JsonObject Run(IReadOnlyList<JsonObject> moves, bool extend)
    {
        if (moves.Count == 0) throw new ArgumentException("Give at least one move, e.g. {\"item\": \"Ball\", \"kind\": \"bounce\", \"duration\": 60}.");
        if (moves.Count > MaxMoves) throw new ArgumentException($"At most {MaxMoves} moves in one call.");
        var said = new JsonArray();
        double last = S.LastFrame;
        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            string what = $"Move {i + 1}" + (m["kind"] is JsonValue k ? $" ({k}{(m["item"] is JsonValue it ? " " + it : "")})" : "");
            _keys = 0;
            _channels.Clear();
            Done done;
            try
            {
                done = One(m, extend);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException)
            {
                throw new ArgumentException($"{what}: {e.Message} Nothing was changed.", e);
            }
            if (done.Name != null) _named[done.Name] = done;
            _previous = done;
            last = Math.Max(last, done.End);
            var o = new JsonObject
            {
                ["item"] = done.Item.Name, ["kind"] = done.Kind, ["start"] = R(done.Start), ["end"] = R(done.End),
                ["channels"] = new JsonArray([.. _channels.Select(c => (JsonNode)c)]), ["keys"] = _keys,
            };
            if (done.Name != null) o["name"] = done.Name;
            said.Add(o);
        }
        var result = new JsonObject { ["moves"] = said };
        if (last > S.LastFrame)
        {
            int was = S.LastFrame;
            S.LastFrame = (int)Math.Ceiling(last - 1e-9);
            result["note"] = $"The scene ran to frame {was}; it now runs to {S.LastFrame} so every move fits.";
        }
        result["scene_frames"] = new JsonArray(S.FirstFrame, S.LastFrame);
        S.Notify();
        return result;
    }

    private Done One(JsonObject m, bool extend)
    {
        var kind = Text(m, "kind")?.Trim().ToLowerInvariant() ?? throw new ArgumentException($"kind is needed: one of {string.Join(", ", Kinds)}.");
        if (!Own.TryGetValue(kind, out var own)) throw new ArgumentException($"kind \"{kind}\" isn't one of {string.Join(", ", Kinds)}.");
        foreach (var (key, _) in m)
            if (!Common.Contains(key) && !own.Contains(key))
                throw new ArgumentException($"A {kind} move has no \"{key}\". It takes {string.Join(", ", own.Concat(Common.Where(c => c != "kind")))}.");
        var item = Item(Text(m, "item") ?? throw new ArgumentException("item is needed: the item to move, by name."));
        var (start, end) = Span(m, extend);
        var ease = Ease(m, kind, LightWaveTools.StyleOf(S)?.Ease);
        double every = Number(m, "every") ?? 2;
        if (!(every >= 0.25) || every > 100) throw new ArgumentException("every (frames between keys) must be from 0.25 to 100.");
        var aim = m["aim"] is { } a ? Target(a, item, "aim") : null;
        switch (kind)
        {
            case "move":
            {
                var p0 = item.PositionAt(start);
                var p1 = ToOrBy(m, p0, "position", add: true);
                Position(item, start, end, p0, p1, ease, aim, every);
                break;
            }
            case "turn":
            {
                var r0 = item.RotationAt(start);
                Tween3(item, 3, start, end, r0, ToOrBy(m, r0, "rotation", add: true), ease);
                break;
            }
            case "grow":
            {
                var s0 = item.ScaleAt(start);
                Vec3 s1;
                if (m["to"] is JsonValue) s1 = Uniform(Number(m, "to")!.Value, "to");
                else if (m["by"] is JsonValue) s1 = s0 * Uniform(Number(m, "by")!.Value, "by").X;
                else s1 = ToOrBy(m, s0, "scale", add: false);
                if (s1.X <= 0 || s1.Y <= 0 || s1.Z <= 0) throw new ArgumentException("A scale must be more than 0.");
                Tween3(item, 6, start, end, s0, s1, ease);
                break;
            }
            case "pop_in":
            {
                var s1 = item.ScaleAt(start);
                Tween3(item, 6, start, end, s1 * 0.01, s1, ease, forceStart: true);
                break;
            }
            case "pop_out":
            {
                var s0 = item.ScaleAt(start);
                Tween3(item, 6, start, end, s0, s0 * 0.01, ease);
                break;
            }
            case "fade_in" or "fade_out":
            {
                var env = item is ObjectItem o ? o.Dissolve : throw new ArgumentException($"{item.Name} is a {item.Kind.ToLowerInvariant()}: only objects fade (their dissolve).");
                double v0 = kind == "fade_in" ? 1 : env.Evaluate(start);
                Tween(env, SceneChannels.Dissolve, start, end, v0, kind == "fade_in" ? 0 : 1, ease, forceStart: kind == "fade_in");
                break;
            }
            case "spin":
            {
                var axis = (Text(m, "axis") ?? "heading").Trim().ToLowerInvariant();
                int ch = axis switch { "heading" or "y" => 3, "pitch" or "x" => 4, "bank" or "z" => 5, _ => throw new ArgumentException("axis must be heading, pitch or bank.") };
                double degrees = Number(m, "degrees") ?? 360 * (Number(m, "turns") ?? 1);
                var env = item.Channels[ch];
                double v0 = env.Evaluate(start);
                Tween(env, SceneChannels.MotionNames[ch], start, end, v0, v0 + degrees, ease);
                break;
            }
            case "bounce":
                Bounce(item, m, start, end);
                break;
            case "orbit":
                Orbit(item, m, start, end, ease, aim, every);
                break;
            case "sway" or "bob":
            {
                var channel = kind == "bob" ? "position.y" : (Text(m, "channel") ?? "bank").Trim().ToLowerInvariant();
                var env = SceneChannels.Find(item, channel) ?? throw new ArgumentException($"{item.Name} has no channel \"{channel}\". Its channels are {string.Join(", ", SceneChannels.Of(item).Select(c => c.Name))}.");
                bool metres = channel.StartsWith("position", StringComparison.Ordinal);
                double amp = Number(m, "amplitude") ?? (kind == "bob" ? 0.1 : metres ? 0.2 : 15);
                double cycles = Number(m, "cycles") ?? Math.Max(1, Math.Round((end - start) / (kind == "bob" ? 40 : 30)));
                double damping = Number(m, "damping") ?? 0;
                if (!(cycles > 0) || cycles > 1000) throw new ArgumentException("cycles must be more than 0.");
                if (damping < 0 || damping > 1) throw new ArgumentException("damping must be from 0 (it keeps swinging) to 1 (it has stopped by the end).");
                double v0 = env.Evaluate(start);
                Sampled(env, channel, start, end, f =>
                {
                    double u = (f - start) / (end - start);
                    return v0 + amp * (1 - damping * u) * Math.Sin(2 * Math.PI * cycles * u);
                }, Math.Min(every, (end - start) / (cycles * 8)));
                break;
            }
            case "shake":
            {
                bool rotate = Bool(m, "rotate") ?? false;
                double amp = Number(m, "amplitude") ?? (rotate ? 3 : 0.05);
                var random = new Random(Whole(m, "seed") ?? 0);
                int first = rotate ? 3 : 0;
                double step = Number(m, "every") ?? 3;
                for (int axis = 0; axis < 3; axis++)
                {
                    var env = item.Channels[first + axis];
                    double v0 = env.Evaluate(start);
                    var offsets = new Dictionary<double, double>();
                    Sampled(env, SceneChannels.MotionNames[first + axis], start, end, f =>
                    {
                        if (f <= start || f >= end) return v0;
                        if (!offsets.TryGetValue(f, out var o)) offsets[f] = o = (random.NextDouble() * 2 - 1) * amp;
                        return v0 + o;
                    }, step);
                }
                break;
            }
            case "through":
                Through(item, m, start, end, ease, aim, every);
                break;
            case "track":
            {
                var target = Target(m["target"] ?? throw new ArgumentException("target is needed: an item's name or a point [x, y, z]."), item, "target");
                Aim(item, start, end, target, every);
                break;
            }
            case "dolly" or "truck":
            {
                double d = Number(m, "distance") ?? throw new ArgumentException($"distance is needed: metres to move {(kind == "dolly" ? "forward along its view (negative backs away)" : "to its right (negative to its left)")}.");
                var axis = item.WorldMatrix(start).TransformVector(kind == "dolly" ? Vec3.UnitZ : Vec3.UnitX).Normalized();
                var p0 = item.PositionAt(start);
                var p1 = Local(item, start, item.WorldPosition(start) + axis * d) + (p0 - Local(item, start, item.WorldPosition(start)));
                var keep = m["target"] is { } t ? Target(t, item, "target") : aim;
                Position(item, start, end, p0, p1, ease, keep, every);
                break;
            }
            case "crane":
            {
                double h = Number(m, "height") ?? 1;
                var p0 = item.PositionAt(start);
                var keep = m["target"] is { } t ? Target(t, item, "target") : aim;
                Position(item, start, end, p0, p0 + new Vec3(0, h, 0), ease, keep, every);
                break;
            }
            case "push_in":
            {
                var target = Target(m["target"] ?? throw new ArgumentException("target is needed: what to close in on."), item, "target");
                var from = item.WorldPosition(start);
                var at = target(start);
                double now = (at - from).Length;
                double left = Number(m, "distance") ?? now / 2;
                if (left < 0 || left >= now) throw new ArgumentException($"distance (how far from the target it ends) must be from 0 to less than the {now:0.###} m it is now.");
                var p0 = item.PositionAt(start);
                var p1 = Local(item, start, at + (from - at).Normalized() * left) + (p0 - Local(item, start, from));
                Position(item, start, end, p0, p1, ease, aim ?? target, every);
                break;
            }
            case "follow":
            {
                var target = Target(m["target"] ?? throw new ArgumentException("target is needed: the item to follow."), item, "target");
                var offset = m["offset"] is { } o ? Vector(o, "offset") : item.WorldPosition(start) - target(start);
                var origin = item.PositionAt(start) - Local(item, start, item.WorldPosition(start));
                SampledVector(item, 0, start, end, f => Local(item, f, target(f) + offset) + origin, every);
                Aim(item, start, end, aim ?? target, every);
                break;
            }
        }
        return new Done(Text(m, "name"), item, kind, start, end);
    }

    // ───────────────────────────── kinds ─────────────────────────────

    /// <summary>A move from one place to another: two keys (three for overshoot), or keys every few frames while it keeps aiming.</summary>
    private void Position(SceneItem item, double start, double end, Vec3 p0, Vec3 p1, string ease, Func<double, Vec3>? aim, double every)
    {
        if (aim == null) Tween3(item, 0, start, end, p0, p1, ease);
        else
        {
            SampledVector(item, 0, start, end, f => p0 + (p1 - p0) * Eased(ease, (f - start) / (end - start)), every);
            Aim(item, start, end, aim, every);
        }
    }

    private void Bounce(SceneItem item, JsonObject m, double start, double end)
    {
        double h = Number(m, "height") ?? 1;
        int count = Whole(m, "count") ?? 3;
        double decay = Number(m, "decay") ?? 0.55;
        double squash = Number(m, "squash") ?? 0;
        var from = (Text(m, "start_at") ?? "top").Trim().ToLowerInvariant();
        if (!(h > 0)) throw new ArgumentException("height must be more than 0.");
        if (count < 0 || count > 100) throw new ArgumentException("count (bounces after the first landing) must be from 0 to 100.");
        if (!(decay > 0) || decay > 1) throw new ArgumentException("decay (each bounce's height over the one before) must be more than 0 and at most 1.");
        if (squash < 0 || squash > 0.6) throw new ArgumentException("squash must be from 0 (none) to 0.6.");
        if (from is not ("top" or "ground")) throw new ArgumentException("start_at must be top or ground.");
        var y = item.Channels[1];
        double ground = y.Evaluate(start);
        // The arcs: heights, and whether each is a whole arc (up and down) or just the fall from the top.
        var arcs = new List<(double H, bool Whole)> { (h, from == "ground") };
        for (int k = 1; k <= count; k++) arcs.Add((h * Math.Pow(decay, k), true));
        // A half arc of height H takes time in proportion to √H.
        double units = arcs.Sum(a => (a.Whole ? 2 : 1) * Math.Sqrt(a.H));
        double scale = (end - start) / units;
        // Keys: (frame, value, in-slope, out-slope), parabolas made exactly by Bezier spans.
        var keys = new List<(double F, double V, double In, double Out)>();
        var contacts = new List<double>();
        double t = start;
        double firstOut = 0;
        for (int i = 0; i < arcs.Count; i++)
        {
            var (H, whole) = arcs[i];
            double half = Math.Sqrt(H) * scale;
            double v = 2 * H / half; // speed at the ground
            if (whole)
            {
                if (i == 0) firstOut = v;
                else keys[^1] = keys[^1] with { Out = v };
                t += half;
                keys.Add((t, ground + H, 0, 0));
            }
            t += half;
            keys.Add((t, ground, -v, 0));
            contacts.Add(t);
        }
        double startValue = from == "top" ? ground + h : ground;
        StartKey(y, start, startValue, firstOut, force: true);
        Clear(y, start, end);
        foreach (var k in keys)
            Put(y, new EnvelopeKey(k.F, k.V, InSlope: k.In, OutSlope: k.Out).WithSpan(SpanKind.Bezier));
        Note("position.y", keys.Count);
        // Across the ground as it bounces, if asked.
        if (m["to"] != null || m["by"] != null)
        {
            var p0 = item.PositionAt(start);
            var p1 = ToOrBy(m, p0, "position", add: true);
            for (int axis = 0; axis < 3; axis += 2)
                if (Math.Abs(p1[axis] - p0[axis]) > 1e-12) Tween(item.Channels[axis], SceneChannels.MotionNames[axis], start, end, p0[axis], p1[axis], "linear");
        }
        if (squash > 0)
        {
            var s0 = item.ScaleAt(start);
            var marks = new SortedDictionary<double, (double Y, double Side)> { [start] = (1, 1), [end] = (1, 1) };
            double stretch = 1 + 0.6 * squash, near = Math.Min(2, scale * Math.Sqrt(arcs[^1].H) / 3);
            foreach (var c in contacts.Where(c => c < end - 1e-9))
            {
                marks[c] = (1 - squash, 1 / Math.Sqrt(1 - squash));
                foreach (var f in new[] { c - near, c + near })
                    if (f > start && f < end && !marks.ContainsKey(f)) marks[f] = (stretch, 1 / Math.Sqrt(stretch));
            }
            foreach (var k in keys.Where(k => k.V > ground)) marks[k.F] = (1, 1);
            for (int axis = 0; axis < 3; axis++)
            {
                var env = item.Channels[6 + axis];
                StartKey(env, start, s0[axis], null, force: true);
                Clear(env, start, end);
                foreach (var (f, (sy, side)) in marks.Where(x => x.Key > start))
                    Put(env, new EnvelopeKey(f, s0[axis] * (axis == 1 ? sy : side)));
                Note(SceneChannels.MotionNames[6 + axis], marks.Count - 1);
            }
        }
    }

    private void Orbit(SceneItem item, JsonObject m, double start, double end, string ease, Func<double, Vec3>? aim, double every)
    {
        var around = Target(m["around"] ?? throw new ArgumentException("around is needed: an item's name or a point [x, y, z]."), item, "around");
        var c0 = around(start);
        var p0 = item.WorldPosition(start);
        var flat = new Vec3(p0.X - c0.X, 0, p0.Z - c0.Z);
        double radius = Number(m, "radius") ?? (flat.Length > 1e-6 ? flat.Length : 2);
        if (!(radius > 0)) throw new ArgumentException("radius must be more than 0.");
        double rise = Number(m, "height") ?? p0.Y - c0.Y;
        double degrees = Number(m, "degrees") ?? 360 * (Number(m, "turns") ?? 1);
        double a0 = flat.Length > 1e-6 ? Math.Atan2(flat.X, flat.Z) : 0;
        double total = degrees * Math.PI / 180;
        var face = (Text(m, "face") ?? (item is CameraItem or LightItem ? "center" : "none")).Trim().ToLowerInvariant();
        if (face is not ("center" or "forward" or "none")) throw new ArgumentException("face must be center, forward or none.");
        // At least 16 keys a turn, so the circle stays round.
        double step = Math.Min(every, (end - start) / Math.Max(4, Math.Abs(degrees) / 360 * 16));
        var origin = item.PositionAt(start) - Local(item, start, p0);
        Vec3 At(double f)
        {
            double a = a0 + total * Eased(ease, (f - start) / (end - start));
            return around(f) + new Vec3(radius * Math.Sin(a), rise, radius * Math.Cos(a));
        }
        SampledVector(item, 0, start, end, f => Local(item, f, At(f)) + origin, step);
        if (aim != null || face == "center") Aim(item, start, end, aim ?? around, step);
        else if (face == "forward") Aim(item, start, end, f => item.WorldPosition(f) + (At(f + 0.25) - At(f - 0.25)), step);
    }

    private void Through(SceneItem item, JsonObject m, double start, double end, string ease, Func<double, Vec3>? aim, double every)
    {
        if (m["points"] is not JsonArray list || list.Count == 0) throw new ArgumentException("points is needed: a list of [x, y, z] places to pass through, in order.");
        var points = new List<Vec3> { item.PositionAt(start) };
        points.AddRange(list.Select((p, i) => Vector(p ?? throw new ArgumentException($"points[{i}] is empty."), $"points[{i}]")));
        var lengths = new List<double> { 0 };
        for (int i = 1; i < points.Count; i++) lengths.Add(lengths[^1] + (points[i] - points[i - 1]).Length);
        double total = lengths[^1];
        if (total < 1e-9) throw new ArgumentException("The points are all where the item already is.");
        for (int axis = 0; axis < 3; axis++)
        {
            var env = item.Channels[axis];
            StartKey(env, start, points[0][axis], null);
            Clear(env, start, end);
        }
        for (int i = 1; i < points.Count; i++)
        {
            // Even speed along the way, with the ease: the frame at which the eased progress reaches this point.
            double f = Math.Round(start + (end - start) * Uneased(ease, lengths[i] / total), 4);
            for (int axis = 0; axis < 3; axis++) Put(item.Channels[axis], new EnvelopeKey(f, points[i][axis]));
        }
        for (int axis = 0; axis < 3; axis++) Note(SceneChannels.MotionNames[axis], points.Count - 1);
        var face = (Text(m, "face") ?? "none").Trim().ToLowerInvariant();
        if (face is not ("forward" or "none")) throw new ArgumentException("face must be forward or none.");
        if (aim != null) Aim(item, start, end, aim, every);
        else if (face == "forward")
            Aim(item, start, end, f => item.WorldPosition(f) + (item.WorldPosition(Math.Min(end, f + 0.5)) - item.WorldPosition(Math.Max(start, f - 0.5))), every);
    }

    /// <summary>Keys heading and pitch every few frames so the item faces a target as it (and the target) move.</summary>
    private void Aim(SceneItem item, double start, double end, Func<double, Vec3> target, double every)
    {
        var (h, p) = (item.Channels[3], item.Channels[4]);
        var first = Facing(item, target, start);
        StartKey(h, start, first.X, null, force: true);
        StartKey(p, start, first.Y, null, force: true);
        Clear(h, start, end);
        Clear(p, start, end);
        int n = 0;
        foreach (var f in Frames(start, end, every).Skip(1))
        {
            var hpb = Facing(item, target, f);
            Put(h, new EnvelopeKey(f, hpb.X));
            Put(p, new EnvelopeKey(f, hpb.Y));
            n++;
        }
        Note("heading", n);
        Note("pitch", n);
    }

    private static Vec3 Facing(SceneItem item, Func<double, Vec3> target, double f)
    {
        var to = target(f);
        return (to - item.WorldPosition(f)).Length < 1e-6 ? item.RotationAt(f) : item.RotationToward(to, f);
    }

    // ───────────────────────────── keys ─────────────────────────────

    /// <summary>Three channels from one vector to another (position 0, rotation 3, scale 6), skipping those that don't change.</summary>
    private void Tween3(SceneItem item, int first, double start, double end, Vec3 v0, Vec3 v1, string ease, bool forceStart = false)
    {
        for (int axis = 0; axis < 3; axis++)
            if (forceStart || Math.Abs(v1[axis] - v0[axis]) > 1e-12)
                Tween(item.Channels[first + axis], SceneChannels.MotionNames[first + axis], start, end, v0[axis], v1[axis], ease, forceStart);
    }

    /// <summary>One channel from one value to another with an ease: Bezier keys with the slopes the ease has at its ends.</summary>
    private void Tween(Envelope e, string name, double start, double end, double v0, double v1, string ease, bool forceStart = false)
    {
        double d = v1 - v0, span = end - start;
        switch (ease)
        {
            case "linear":
                StartKey(e, start, v0, null, forceStart);
                Clear(e, start, end);
                Put(e, new EnvelopeKey(end, v1).WithSpan(SpanKind.Linear));
                Note(name, 1);
                return;
            case "overshoot":
            {
                // Past the end by an eighth, then back: a key at the far point with flat slopes.
                double peak = start + 0.7 * span, far = v0 + 1.12 * d;
                StartKey(e, start, v0, 2 * (far - v0) / (peak - start), forceStart);
                Clear(e, start, end);
                Put(e, new EnvelopeKey(peak, far).WithSpan(SpanKind.Bezier));
                Put(e, new EnvelopeKey(end, v1).WithSpan(SpanKind.Bezier));
                Note(name, 2);
                return;
            }
            default:
            {
                double fast = 2 * d / span;
                var (outSlope, inSlope) = ease switch { "ease_in" => (0.0, fast), "ease_out" => (fast, 0.0), _ => (0.0, 0.0) };
                StartKey(e, start, v0, outSlope, forceStart);
                Clear(e, start, end);
                Put(e, new EnvelopeKey(end, v1, InSlope: inSlope).WithSpan(SpanKind.Bezier));
                Note(name, 1);
                return;
            }
        }
    }

    /// <summary>A channel keyed every few frames from a function of the frame (smooth TCB keys).</summary>
    private void Sampled(Envelope e, string name, double start, double end, Func<double, double> value, double every)
    {
        StartKey(e, start, value(start), null, force: true);
        Clear(e, start, end);
        int n = 0;
        foreach (var f in Frames(start, end, every).Skip(1))
        {
            Put(e, new EnvelopeKey(f, value(f)));
            n++;
        }
        Note(name, n);
    }

    private void SampledVector(SceneItem item, int first, double start, double end, Func<double, Vec3> value, double every)
    {
        var cache = new Dictionary<double, Vec3>();
        Vec3 At(double f) => cache.TryGetValue(f, out var v) ? v : cache[f] = value(f);
        foreach (var f in Frames(start, end, every)) At(f);
        for (int axis = 0; axis < 3; axis++)
        {
            int a = axis;
            Sampled(item.Channels[first + a], SceneChannels.MotionNames[first + a], start, end, f => At(f)[a], every);
        }
    }

    /// <summary>Frames from start to end, every so many, ending exactly at the end.</summary>
    private static IEnumerable<double> Frames(double start, double end, double every)
    {
        int n = Math.Max(1, (int)Math.Round((end - start) / every));
        for (int i = 0; i <= n; i++) yield return i == n ? end : Math.Round(start + (end - start) * i / n, 4);
    }

    /// <summary>
    /// The key a move starts from: one already at that frame keeps its value (and its slope in, from the move before)
    /// unless <paramref name="force"/>; its slope out is set when given (it starts a Bezier span).
    /// </summary>
    private void StartKey(Envelope e, double frame, double value, double? outSlope, bool force = false)
    {
        int i = Keying.IsConstant(e, S) ? -1 : e.IndexOf(frame);
        if (i >= 0)
        {
            var k = e.Keys[i];
            e.SetKey(i, k with { Value = force ? value : k.Value, OutSlope = outSlope ?? k.OutSlope });
            return;
        }
        Keying.Key(e, S, new EnvelopeKey(frame, value, OutSlope: outSlope ?? 0), keepShape: false);
        _keys++;
    }

    /// <summary>Removes a channel's keys after the start, up to and including the end: the move's own keys replace them.</summary>
    private static void Clear(Envelope e, double start, double end)
    {
        for (int i = e.Keys.Count - 1; i >= 0; i--)
            if (e.Keys[i].Frame > start + 1e-9 && e.Keys[i].Frame <= end + 1e-9 && e.Keys.Count > 1) e.RemoveKey(i);
    }

    private void Put(Envelope e, EnvelopeKey k)
    {
        Keying.Key(e, S, k, keepShape: false);
        _keys++;
    }

    private void Note(string channel, int keys) => _channels.Add(channel);

    // ───────────────────────────── reading a move ─────────────────────────────

    private (double Start, double End) Span(JsonObject m, bool extend)
    {
        double delay = Number(m, "delay") ?? 0;
        if (Count(m, "start", "after", "with") > 1) throw new ArgumentException("Give one of start, after and with.");
        double start = Number(m, "start") is { } s ? s + delay
            : Text(m, "after") is { } after ? Ref(after, "after").End + delay
            : Text(m, "with") is { } with ? Ref(with, "with").Start + delay
            : S.FirstFrame + delay;
        if (m["end"] != null && m["duration"] != null) throw new ArgumentException("Give end or duration, not both.");
        // A move "with" another lasts as long as it, unless told otherwise.
        double length = Number(m, "duration") ?? (Text(m, "with") is { } w ? Ref(w, "with").End - Ref(w, "with").Start : DefaultDuration);
        double end = Number(m, "end") ?? start + length;
        if (!double.IsFinite(start) || !double.IsFinite(end)) throw new ArgumentException("Frames must be numbers.");
        if (start < S.FirstFrame) throw new ArgumentException($"It starts at frame {R(start)}, before the scene's first frame, {S.FirstFrame}.");
        if (!(end > start)) throw new ArgumentException($"It ends at frame {R(end)}, not after it starts ({R(start)}).");
        if (end > S.LastFrame && !extend) throw new ArgumentException($"It ends at frame {R(end)}, after the scene's last frame, {S.LastFrame} (give extend true, or set_scene last_frame).");
        if (end - start > 100000) throw new ArgumentException("A move can last at most 100000 frames.");
        return (start, end);
    }

    private Done Ref(string name, string what) =>
        name == "previous" ? _previous ?? throw new ArgumentException($"{what} \"previous\" needs a move before this one.")
        : _named.TryGetValue(name, out var d) ? d
        : throw new ArgumentException($"{what}: there's no move named \"{name}\" before this one{(_named.Count > 0 ? $" (named so far: {string.Join(", ", _named.Keys)})" : "")}; or give \"previous\".");

    private static int Count(JsonObject m, params string[] keys) => keys.Count(k => m[k] != null);

    private static string Ease(JsonObject m, string kind, string? style = null)
    {
        var e = Text(m, "ease")?.Trim().ToLowerInvariant();
        if (e == "smooth") e = "ease_in_out";
        e ??= kind switch
        {
            "spin" or "orbit" or "fade_in" or "fade_out" => "linear",
            "pop_in" => "overshoot",
            "pop_out" => "ease_in",
            _ => style ?? "ease_in_out",
        };
        return Eases.Contains(e) ? e : throw new ArgumentException($"ease must be one of {string.Join(", ", Eases)}, not \"{e}\".");
    }

    /// <summary>The eased progress at progress t (0 to 1).</summary>
    internal static double Eased(string ease, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return ease switch
        {
            "ease_in" => t * t,
            "ease_out" => 1 - (1 - t) * (1 - t),
            "ease_in_out" => t * t * (3 - 2 * t),
            "overshoot" => 1 + 2.70158 * Math.Pow(t - 1, 3) + 1.70158 * Math.Pow(t - 1, 2),
            _ => t,
        };
    }

    /// <summary>The progress at which the eased progress reaches <paramref name="p"/> (bisection; the eases rise).</summary>
    private static double Uneased(string ease, double p)
    {
        if (ease is "linear" or "overshoot") return p;
        double lo = 0, hi = 1;
        for (int i = 0; i < 50; i++)
        {
            double mid = (lo + hi) / 2;
            if (Eased(ease, mid) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    private SceneItem Item(string name)
    {
        try
        {
            return LightWaveTools.ItemByName(S, name);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"There's no item \"{name}\". The items are: {string.Join(", ", S.Items.Where(i => i is not Bone).Select(o => o.Name))}.");
        }
    }

    /// <summary>A target that may move: an item (its pivot in the world) or a fixed point.</summary>
    private Func<double, Vec3> Target(JsonNode node, SceneItem self, string what)
    {
        if (node is JsonArray) { var p = Vector(node, what); return _ => p; }
        if (node is JsonValue v && v.TryGetValue(out string? name))
        {
            var it = Item(name);
            if (ReferenceEquals(it, self)) throw new ArgumentException($"{what} can't be the item itself.");
            return f => it.WorldPosition(f);
        }
        throw new ArgumentException($"{what} must be an item's name or a point [x, y, z].");
    }

    /// <summary>A world point in the item's parent's space (its position channels' space).</summary>
    private static Vec3 Local(SceneItem item, double frame, Vec3 world) =>
        item.Parent is { } parent ? parent.WorldMatrix(frame).Inverted().TransformPoint(world) : world;

    private static Vec3 ToOrBy(JsonObject m, Vec3 now, string what, bool add)
    {
        if (m["to"] != null && m["by"] != null) throw new ArgumentException("Give to or by, not both.");
        if (m["to"] is { } to) return Vector(to, "to");
        if (m["by"] is { } by)
        {
            var d = Vector(by, "by");
            return add ? now + d : new Vec3(now.X * d.X, now.Y * d.Y, now.Z * d.Z);
        }
        throw new ArgumentException($"to (a {what} [x, y, z]) or by (a change from where it is) is needed.");
    }

    private static Vec3 Uniform(double v, string what) =>
        v > 0 && double.IsFinite(v) ? new Vec3(v, v, v) : throw new ArgumentException($"{what} must be more than 0.");

    private static Vec3 Vector(JsonNode node, string what)
    {
        if (node is JsonArray a && a.Count == 3 && a.All(x => x is JsonValue jv && jv.TryGetValue(out double _)))
        {
            var v = new Vec3((double)a[0]!, (double)a[1]!, (double)a[2]!);
            if (double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z)) return v;
        }
        throw new ArgumentException($"{what} must be [x, y, z], three numbers.");
    }

    private static string? Text(JsonObject m, string key) => m[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => s,
        var other => throw new ArgumentException($"{key} must be text, not {other.ToJsonString()}."),
    };

    private static double? Number(JsonObject m, string key) => m[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out double d) && double.IsFinite(d) => d,
        var other => throw new ArgumentException($"{key} must be a number, not {other.ToJsonString()}."),
    };

    private static int? Whole(JsonObject m, string key) => Number(m, key) is not { } d ? null
        : d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? (int)d : throw new ArgumentException($"{key} must be a whole number.");

    private static bool? Bool(JsonObject m, string key) => m[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out bool b) => b,
        var other => throw new ArgumentException($"{key} must be true or false, not {other.ToJsonString()}."),
    };

    private static double R(double v) => Math.Round(v, 3);

    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
