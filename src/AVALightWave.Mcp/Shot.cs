using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using SceneModel = AVAModeler.Scene.Scene;

namespace AVALightWave.Mcp;

/// <summary>
/// Looking at a shot without rendering it (the milestone's third stage): where things are in the world, where they
/// land in a camera's picture, placing a camera to fit them, and the problems worth fixing before a render.
/// </summary>
internal static class Shot
{
    /// <summary>An item's corners in the world at a frame: an object's box (its base shape), or a single point for anything else.</summary>
    public static Vec3[] Corners(SceneItem item, double frame)
    {
        var world = item.WorldMatrix(frame);
        if (item is ObjectItem o && o.BaseMesh() is { Points.Count: > 0 } mesh)
        {
            var b = Box3.Of(mesh.Points);
            var corners = new Vec3[8];
            for (int i = 0; i < 8; i++)
                corners[i] = world.TransformPoint(new Vec3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z));
            return corners;
        }
        return [item.WorldPosition(frame)];
    }

    public static Box3 Bounds(SceneItem item, double frame) => Box3.Of(Corners(item, frame));

    /// <summary>Whether an object is shown at a frame (visible and not wholly dissolved).</summary>
    public static bool Shown(SceneItem item, double frame) =>
        item.Visible && item.Ancestors().All(a => a.Visible) && !(item is ObjectItem o && o.Dissolve.Evaluate(frame) >= 0.999);

    /// <summary>The frames to look at: every <paramref name="every"/> frames from first to last, the last always included.</summary>
    public static List<double> Frames(double first, double last, double every)
    {
        var frames = new List<double>();
        for (double f = first; f < last - 1e-9; f += every) frames.Add(Math.Round(f, 4));
        frames.Add(last);
        return frames;
    }

    /// <summary>A world point in the camera's picture: x and y from -1 to 1 across the frame (y up), and its depth in front of the camera.</summary>
    public static (double X, double Y, double Depth) Project(CameraItem cam, double frame, Vec3 world)
    {
        var p = cam.CameraToWorld(frame).Inverted().TransformPoint(world);
        double zoom = cam.Zoom.Evaluate(frame);
        if (p.Z <= 1e-9) return (double.NaN, double.NaN, p.Z);
        return (p.X / p.Z * zoom / cam.FrameAspect, p.Y / p.Z * zoom, p.Z);
    }

    /// <summary>How an item sits in the picture at a frame: in view, cut off by the edge, out of view, behind the camera; and how much of the picture's height it fills.</summary>
    public static (string Where, double Fill) InPicture(CameraItem cam, SceneItem item, double frame)
    {
        var corners = Corners(item, frame);
        int behind = 0, inside = 0;
        double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
        foreach (var c in corners)
        {
            var (x, y, z) = Project(cam, frame, c);
            if (z <= 0.01) { behind++; continue; }
            if (Math.Abs(x) <= 1 && Math.Abs(y) <= 1) inside++;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        if (behind == corners.Length) return ("behind", 0);
        double fill = behind > 0 ? 1 : Math.Min(1, (Math.Min(maxY, 1) - Math.Max(minY, -1)) / 2);
        if (behind == 0 && (maxX < -1 || minX > 1 || maxY < -1 || minY > 1)) return ("out", 0);
        if (inside == corners.Length) return ("in", fill);
        return ("cut", fill);
    }

    /// <summary>
    /// Where a camera goes to fit points in its picture with a margin, looking at their middle from a direction (from
    /// the middle toward the camera).
    /// </summary>
    public static (Vec3 Position, Vec3 Center, double Distance) Fit(CameraItem cam, double frame, IReadOnlyCollection<Vec3> points, Vec3 direction, double margin)
    {
        var box = Box3.Of(points);
        var center = (box.Min + box.Max) * 0.5;
        double radius = Math.Max(1e-3, points.Max(p => (p - center).Length));
        double zoom = cam.Zoom.Evaluate(frame);
        double half = Math.Min(Math.Atan(1 / zoom), Math.Atan(cam.FrameAspect / zoom));
        double distance = radius / Math.Sin(half) * (1 + margin);
        return (center + direction.Normalized() * distance, center, distance);
    }

    /// <summary>A direction to look from, by name or as [x, y, z] (from the subject toward the camera).</summary>
    public static Vec3 Direction(JsonNode? from, Vec3 fallback) => from switch
    {
        null => fallback,
        JsonArray a when a.Count == 3 && a.All(x => x is JsonValue) => new Vec3((double)a[0]!, (double)a[1]!, (double)a[2]!) is var v && v.Length > 1e-9 ? v
            : throw new ArgumentException("from can't be [0, 0, 0]."),
        JsonValue s when s.TryGetValue(out string? name) => name.Trim().ToLowerInvariant() switch
        {
            "front" => new Vec3(0, 0.25, -1),
            "three_quarter" or "three-quarter" => new Vec3(-0.75, 0.5, -1),
            "side" or "right" => new Vec3(1, 0.2, 0),
            "left" => new Vec3(-1, 0.2, 0),
            "back" => new Vec3(0, 0.25, 1),
            "top" or "above" => new Vec3(0, 1, -0.02),
            "low" => new Vec3(0, -0.1, -1),
            _ => throw new ArgumentException($"from must be front, three_quarter, side, left, back, top, low or [x, y, z], not \"{name}\"."),
        },
        _ => throw new ArgumentException("from must be a name (front, three_quarter, side…) or [x, y, z]."),
    };

    /// <summary>The problems worth fixing before a render, looked for at the given frames through a camera.</summary>
    public static JsonObject Check(LayoutSession l, CameraItem cam, IReadOnlyList<double> frames, double small)
    {
        var s = l.Scene;
        var problems = new JsonArray();
        void Add(string kind, string? item, IEnumerable<double> at, string detail, string fix)
        {
            var o = new JsonObject { ["kind"] = kind };
            if (item != null) o["item"] = item;
            var list = at.ToList();
            if (list.Count > 0) o["frames"] = Ranges(list, frames);
            o["detail"] = detail;
            o["fix"] = fix;
            problems.Add(o);
        }
        var objects = s.Objects.Where(o => o.BaseMesh().Points.Count > 0).ToList();
        var grounds = objects.Where(IsGround).ToList();
        var summary = new JsonArray();

        foreach (var o in objects)
        {
            var at = frames.Where(f => Shown(o, f)).ToList();
            if (at.Count == 0) continue;
            var seen = at.Select(f => (F: f, P: InPicture(cam, o, f))).ToList();
            if (!IsGround(o))
            {
                var outFrames = seen.Where(x => x.P.Where is "out" or "behind").Select(x => x.F).ToList();
                var cut = seen.Where(x => x.P.Where == "cut").Select(x => x.F).ToList();
                var tiny = seen.Where(x => x.P.Where == "in" && x.P.Fill < small).Select(x => x.F).ToList();
                if (outFrames.Count == seen.Count)
                    Add("never_in_view", o.Name, [], $"{o.Name} isn't in {cam.Name}'s picture at any frame looked at.", $"frame_items with items [\"{o.Name}\"], or look_at {o.Name}.");
                else
                {
                    if (outFrames.Count > 0) Add("out_of_view", o.Name, outFrames, $"{o.Name} leaves {cam.Name}'s picture.", "frame_items over those frames, a wider lens (smaller zoom), or a camera_move that follows it.");
                    if (cut.Count > 0) Add("cut_off", o.Name, cut, $"{o.Name} is cut off by the edge of the picture.", "frame_items (or move the camera back, or lower its zoom).");
                }
                if (tiny.Count > 0 && tiny.Count == seen.Count(x => x.P.Where == "in"))
                    Add("too_small", o.Name, tiny, $"{o.Name} fills less than {small:P0} of the picture's height.", "frame_items with it, or a push_in.");
                // Into the ground.
                foreach (var g in grounds.Where(g => !ReferenceEquals(g, o)))
                {
                    var sink = at.Where(f =>
                    {
                        var b = Bounds(o, f);
                        var gb = Bounds(g, f);
                        bool over = b.Max.X > gb.Min.X && b.Min.X < gb.Max.X && b.Max.Z > gb.Min.Z && b.Min.Z < gb.Max.Z;
                        return over && b.Min.Y < gb.Max.Y - Math.Max(0.01, 0.05 * (b.Max.Y - b.Min.Y));
                    }).ToList();
                    if (sink.Count > 0) Add("below_ground", o.Name, sink, $"{o.Name}'s bottom goes below the top of {g.Name}.", $"Raise it (its bottom should be at y = {Bounds(g, sink[0]).Max.Y:0.###}), or check its bounce or move.");
                }
            }
            // Reached by a light?
            if (!Lit(s, o, at[0])) Add("unlit", o.Name, [at[0]], $"No light reaches {o.Name} (only ambient light).", "add_light aimed at it, or a lights rig in build_scene, or environment lighting.");
            var inView = seen.Count(x => x.P.Where is "in" or "cut");
            summary.Add(new JsonObject
            {
                ["item"] = o.Name, ["in_view"] = $"{inView} of {seen.Count} frames",
                ["fills"] = Math.Round(seen.Where(x => x.P.Where is "in" or "cut").Select(x => x.P.Fill).DefaultIfEmpty().Max(), 3),
            });
        }

        // Things passing through each other (their boxes overlapping deeply), grounds and parents apart.
        var movers = objects.Where(o => !IsGround(o)).ToList();
        for (int i = 0; i < movers.Count; i++)
            for (int j = i + 1; j < movers.Count; j++)
            {
                var (a, b) = (movers[i], movers[j]);
                if (a.Ancestors().Contains(b) || b.Ancestors().Contains(a)) continue;
                var hits = frames.Where(f => Shown(a, f) && Shown(b, f) && Overlap(Bounds(a, f), Bounds(b, f)) > 0.35).ToList();
                if (hits.Count > 0) Add("overlap", $"{a.Name} and {b.Name}", hits, $"{a.Name} and {b.Name} take up the same space.", "Move one of them, or check their moves' timing.");
            }

        // The camera inside something.
        foreach (var o in objects)
        {
            var inside = frames.Where(f => Shown(o, f) && Contains(Bounds(o, f), cam.WorldPosition(f))).ToList();
            if (inside.Count > 0) Add("camera_inside", o.Name, inside, $"{cam.Name} is inside {o.Name}'s box.", "Move the camera out (frame_items, or set_transform).");
        }

        // Nothing at all to see.
        var empty = frames.Where(f => objects.Where(o => !IsGround(o) && Shown(o, f)).All(o => InPicture(cam, o, f).Where is "out" or "behind")).ToList();
        if (objects.Any(o => !IsGround(o)) && empty.Count > 0 && empty.Count < frames.Count)
            Add("empty_picture", null, empty, $"Nothing but the ground is in {cam.Name}'s picture.", "frame_items over those frames, or a camera_move.");

        // Keys outside the scene's frames.
        foreach (var item in s.Items)
        {
            var late = SceneChannels.Of(item).SelectMany(c => c.Envelope.Keys.Select(k => k.Frame)).Where(f => f > s.LastFrame + 1e-6).ToList();
            if (late.Count > 0) Add("keys_after_end", item.Name, [], $"{item.Name} has keys after the last frame ({s.LastFrame}), up to {late.Max():0.###}: that motion isn't rendered.", $"set_scene last_frame {Math.Ceiling(late.Max())}, or scale_keys.");
        }

        if (!s.Lights.Any(x => x.Intensity.Evaluate(frames[0]) > 0) && !s.Environment.Lighting)
            Add("no_lights", null, [], "The scene has no light on (only ambient).", "add_light, or a lights rig in build_scene.");

        return new JsonObject
        {
            ["camera"] = cam.Name,
            ["frames_looked_at"] = frames.Count,
            ["first"] = frames[0], ["last"] = frames[^1],
            ["ok"] = problems.Count == 0,
            ["problems"] = problems,
            ["objects"] = summary,
        };
    }

    /// <summary>A ground: an object named like one (ground, floor), or one much wider than it is tall lying under the others.</summary>
    public static bool IsGround(ObjectItem o)
    {
        var n = o.Name.ToLowerInvariant();
        return n.Contains("ground", StringComparison.Ordinal) || n.Contains("floor", StringComparison.Ordinal);
    }

    private static bool Lit(SceneModel s, ObjectItem o, double f)
    {
        if (s.Environment.Lighting && s.Environment.On) return true;
        var c = Bounds(o, f);
        var mid = (c.Min + c.Max) * 0.5;
        double size = (c.Max - c.Min).Length / 2;
        foreach (var light in s.Lights)
        {
            if (light.Intensity.Evaluate(f) <= 0) continue;
            if (light.Type == LightType.Distant) return true;
            var to = mid - light.WorldPosition(f);
            double d = to.Length;
            if (light.Range > 0 && d - size > light.Range) continue;
            if (light.Type == LightType.Spot)
            {
                double angle = Math.Acos(Math.Clamp(Vec3.Dot(to.Normalized(), light.Direction(f)), -1, 1)) * 180 / Math.PI;
                double spread = d > 1e-9 ? Math.Atan(size / d) * 180 / Math.PI : 90;
                if (angle - spread > light.ConeAngle) continue;
            }
            if (light.Type == LightType.Area && Vec3.Dot(to, light.Direction(f)) <= 0) continue;
            return true;
        }
        return false;
    }

    private static double Overlap(Box3 a, Box3 b)
    {
        double x = Math.Min(a.Max.X, b.Max.X) - Math.Max(a.Min.X, b.Min.X);
        double y = Math.Min(a.Max.Y, b.Max.Y) - Math.Max(a.Min.Y, b.Min.Y);
        double z = Math.Min(a.Max.Z, b.Max.Z) - Math.Max(a.Min.Z, b.Min.Z);
        if (x <= 0 || y <= 0 || z <= 0) return 0;
        double Vol(Box3 c) => Math.Max(1e-12, (c.Max.X - c.Min.X) * (c.Max.Y - c.Min.Y) * (c.Max.Z - c.Min.Z));
        return x * y * z / Math.Min(Vol(a), Vol(b));
    }

    private static bool Contains(Box3 b, Vec3 p) =>
        p.X > b.Min.X && p.X < b.Max.X && p.Y > b.Min.Y && p.Y < b.Max.Y && p.Z > b.Min.Z && p.Z < b.Max.Z;

    /// <summary>Frames as runs of the frames looked at: "1–20, 45".</summary>
    private static string Ranges(List<double> at, IReadOnlyList<double> all)
    {
        var index = all.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i);
        var parts = new List<string>();
        for (int i = 0; i < at.Count;)
        {
            int j = i;
            while (j + 1 < at.Count && index[at[j + 1]] == index[at[j]] + 1) j++;
            parts.Add(j == i ? Motion.F(at[i]) : $"{Motion.F(at[i])}–{Motion.F(at[j])}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
