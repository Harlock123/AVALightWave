using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Whole shots, checked: <c>build_scene</c>, <c>frame_items</c> and <c>check_shot</c>.</summary>
public sealed partial class LightWaveTools
{
    /// <summary>The engine's scene calls a plan's steps can make: everything that changes the scene, nothing that renders, saves or opens.</summary>
    public static readonly IReadOnlyList<string> PlanSceneCalls =
    [
        "add_object", "add_light", "add_camera", "add_null", "add_bone", "remove_item", "set_item", "set_transform", "look_at", "set_key",
        "delete_key", "set_end_behavior", "set_follow_path", "bake_path", "shift_keys", "scale_keys", "set_scene", "set_morph",
        "add_displacement", "set_displacement", "skelegons_to_bones", "set_ik", "bake_ik", "add_motion_modifier", "set_motion_modifier",
        "load_image", "set_compositing", "set_dissolve", "set_particles", "set_hair", "set_soft_body", "set_lens_flare",
        "set_volumetric_light", "set_shadow_map",
    ];

    /// <summary>AVALightWave's own calls a plan's steps can make.</summary>
    public static readonly IReadOnlyList<string> PlanOwnCalls =
    [
        "make_object", "build_object", "edit_object", "set_object_surface", "clone_item", "add_morph_target", "replace_object",
        "animate", "camera_move", "frame_items",
    ];

    public static readonly IReadOnlyList<string> Rigs = ["three_point", "sun_and_sky", "studio", "night", "noir", "golden_hour", "sci_fi", "cartoon"];

    [McpServerTool(Name = "build_scene", Destructive = true)]
    [Description("Builds a whole shot from one plan, as one undo step, all or nothing: if any part fails, the error names it and the scene is as it was. The plan's parts, all optional, run in this order: " +
        "settings (set_scene's arguments: first_frame, last_frame, fps, background_color, ambient_intensity, ao, environment, environment_lighting…); " +
        "objects (a list: each is make_object's arguments (with shape), build_object's (with steps), or add_object's (with file)); " +
        "lights (a rig: \"three_point\", \"sun_and_sky\" (a warm sun and the built-in sky lighting the scene), \"studio\" (soft area lights and the built-in studio surroundings), \"night\" (moonlight and a warm lamp), \"noir\", \"golden_hour\", \"sci_fi\" or \"cartoon\", aimed at the objects and sized to them, replacing the scene's lights unless keep_lights is true; or a list of add_light's arguments; or {\"rig\": name, \"intensity\": factor, \"keep_lights\": true}); " +
        "steps (any scene calls, as {\"call\": name, \"args\": {...}}: set_item, set_key, add_null, set_particles, clone_item, set_object_surface…); " +
        "style (a look after the lights: noir, product_commercial, cartoon, golden_hour or sci_fi, as apply_style gives it; its lights only when the plan has none); " +
        "motion (animate's moves); " +
        "camera (after the motion, so fitting sees where things go: frame_items' arguments, e.g. {\"items\": [\"Ball\"], \"from\": \"three_quarter\", \"start\": 1, \"end\": 90}, or {\"position\": [x, y, z], \"look_at\": item or [x, y, z], \"zoom\": z}); " +
        "camera_moves (a list of camera_move's arguments, last, from where camera put it). A motion move on the camera itself (track, say) runs before camera: place such a camera in steps instead. " +
        "Start with new_scene for a clean slate. Returns what was made, then check_shot's look at the result (problems to fix before rendering).")]
    public Task<string> BuildScene(
        [Description("The plan: {\"settings\": {...}, \"objects\": [...], \"lights\": ..., \"camera\": {...}, \"steps\": [...], \"motion\": [...], \"camera_moves\": [...]}.")] JsonObject plan,
        CancellationToken cancel = default) =>
        Making("built a scene", (c, l, root) => RunPlan(c, l, root, app, plan), cancel);

    /// <summary>
    /// Builds a shot from a plan, as build_scene does, inside the caller's edit (so it's one undo step, all or nothing):
    /// for the tool, and for the window's wizards.
    /// </summary>
    public static JsonObject RunPlan(Commands c, LayoutSession l, string root, ILightWaveApp? app, JsonObject plan)
    {
        var known = new[] { "settings", "objects", "lights", "style", "camera", "steps", "motion", "camera_moves" };
        foreach (var (key, _) in plan)
            if (!known.Contains(key)) throw new ArgumentException($"A plan has no \"{key}\". It has: {string.Join(", ", known)}.");
        var inline = new LightWaveTools(new InlineHost(root, c, l), app);
        var calls = SceneCalls(root, c, l, PlanSceneCalls);
        calls.Add(inline, PlanOwnCalls);
        var made = new JsonObject();
        string part = "";
        JsonObject Call(string name, JsonObject? args, string where)
        {
            part = where;
            return EngineCaller.AsObject(calls.Call(name, args));
        }
        try
        {
            if (plan["settings"] is { } settings)
                made["settings"] = Call("set_scene", Obj(settings, "settings"), "settings");

            var objects = new JsonArray();
            if (plan["objects"] is { } objs)
                foreach (var (o, i) in List(objs, "objects").Select((o, i) => (Obj(o, $"objects[{i}]"), i)))
                {
                    string name = o["shape"] != null ? "make_object" : o["steps"] != null ? "build_object" : o["file"] != null ? "add_object"
                        : throw new ArgumentException($"objects[{i}] needs shape (make_object), steps (build_object) or file (add_object).");
                    var r = Call(name, o, $"objects[{i}] ({name})");
                    objects.Add(new JsonObject { ["name"] = r["name"]?.DeepClone(), ["made_by"] = name, ["size"] = r["size"]?.DeepClone() });
                }
            made["objects"] = objects;

            if (plan["lights"] is { } lights)
            {
                part = "lights";
                made["lights"] = Lights(lights, l, calls);
            }

            if (plan["style"] is { } style)
            {
                part = "style";
                made["style"] = ApplyStyleTo(c, l, root, style.GetValue<string>(), lights: plan["lights"] == null, surfaces: true, lens: true);
            }

            if (plan["steps"] is { } steps)
            {
                var said = new JsonArray();
                foreach (var (s, i) in List(steps, "steps").Select((s, i) => (Obj(s, $"steps[{i}]"), i)))
                {
                    var (call, args) = Step(s, i);
                    if (call == "run_tool") throw new ArgumentException($"steps[{i}]: modeling tools go in an object's steps (build_object), not the plan's.");
                    Call(call, args, $"steps[{i}] ({call})");
                    said.Add(call);
                }
                made["steps"] = said;
            }

            if (plan["motion"] is { } motion)
            {
                var r = Call("animate", new JsonObject { ["moves"] = motion.DeepClone() }, "motion");
                made["motion"] = r["moves"]?.AsArray().Count;
                if (r["note"] is { } note) made["frames_note"] = note.DeepClone();
            }

            if (plan["camera"] is { } camera)
            {
                var cam = Obj(camera, "camera");
                if (cam["position"] != null || cam["look_at"] != null || cam["zoom"] != null)
                {
                    part = "camera";
                    var name = cam["camera"]?.GetValue<string>() ?? l.Scene.CurrentCamera?.Name ?? throw new ArgumentException("The scene has no camera.");
                    foreach (var (key, _) in cam)
                        if (key is not ("camera" or "position" or "look_at" or "zoom")) throw new ArgumentException($"camera with position or look_at has no \"{key}\" (give items to fit instead).");
                    if (cam["zoom"] is { } zoom) Call("set_item", new JsonObject { ["item"] = name, ["zoom"] = zoom.DeepClone() }, "camera");
                    if (cam["position"] is { } pos) Call("set_transform", new JsonObject { ["item"] = name, ["position"] = pos.DeepClone(), ["key"] = false }, "camera");
                    if (cam["look_at"] is { } at)
                        Call("look_at", at is JsonArray ? new JsonObject { ["item"] = name, ["point"] = at.DeepClone(), ["key"] = false }
                            : new JsonObject { ["item"] = name, ["target"] = at.DeepClone(), ["key"] = false }, "camera");
                    made["camera"] = name;
                }
                else made["camera"] = Call("frame_items", cam, "camera (frame_items)");
            }

            if (plan["camera_moves"] is { } moves)
            {
                int n = 0;
                foreach (var (m, i) in List(moves, "camera_moves").Select((m, i) => (Obj(m, $"camera_moves[{i}]"), i)))
                {
                    Call("camera_move", m, $"camera_moves[{i}]");
                    n++;
                }
                made["camera_moves"] = n;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ArgumentException($"The plan's {part} failed: {e.Message.Replace(" Nothing was changed.", "", StringComparison.Ordinal)} Nothing was changed.", e);
        }
        var s0 = l.Scene;
        made["scene_frames"] = new JsonArray(s0.FirstFrame, s0.LastFrame);
        if (s0.CurrentCamera is { } current)
            made["check"] = Shot.Check(l, current, Shot.Frames(s0.FirstFrame, s0.LastFrame, Math.Max(1, (s0.LastFrame - s0.FirstFrame) / 24.0)), 0.04);
        return made;
    }

    [McpServerTool(Name = "frame_items", Destructive = true)]
    [Description("Places a camera so chosen items fill its picture with a margin, aimed at their middle, looking from a direction: by name (front, three_quarter, side, left, back, top, low) or [x, y, z] from the items toward the camera (default: from where the camera is now). At one frame (default the current), or over start to end: then it fits everywhere they go in that time from one place, or with follow true keys the camera every few frames to keep them framed as they move. Items: names, or \"all\" (every object but the ground). One undo step. Returns where it put the camera and how far it is.")]
    public Task<string> FrameItems(
        [Description("The items to fit, by name; [\"all\"] or empty for every object but the ground.")] string[]? items = null,
        [Description("The camera (default: the current one).")] string? camera = null,
        [Description("front, three_quarter, side, left, back, top, low, or [x, y, z] (as JSON text or a list).")] JsonElement? from = null,
        [Description("Room round them, a fraction (default 0.15).")] double margin = 0.15,
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("Fit over frames from start…")] double? start = null,
        [Description("…to end.")] double? end = null,
        [Description("Over start to end: key the camera every few frames to follow them (default false: one place that fits them all the time).")] bool follow = false,
        [Description("Follow: frames between keys (default 5).")] double every = 5,
        [Description("Key the camera at the frame (default: only if it's already animated).")] bool? key = null,
        CancellationToken cancel = default) =>
        Making("framed the camera", (c, l, root) =>
        {
            var s = l.Scene;
            var cam = (camera != null ? ItemByName(s, camera) : s.CurrentCamera) as CameraItem
                ?? throw new ArgumentException(camera != null ? $"{camera} isn't a camera." : "The scene has no camera.");
            var targets = (items == null || items.Length == 0 || items is ["all"]
                ? s.Objects.Where(o => !Shot.IsGround(o) && o.BaseMesh().Points.Count > 0).Cast<SceneItem>()
                : items.Select(n => ItemByName(s, n))).ToList();
            if (targets.Count == 0) throw new ArgumentException("There's nothing to frame: give items (the scene has no objects but the ground).");
            if (targets.Contains(cam)) throw new ArgumentException("The camera can't frame itself.");
            if (margin < 0 || margin > 5) throw new ArgumentException("margin must be from 0 to 5.");
            if ((start == null) != (end == null)) throw new ArgumentException("Give both start and end, or neither.");
            if (start != null && !(end > start)) throw new ArgumentException("end must be after start.");
            double f0 = frame ?? start ?? l.Frame;
            var fromNode = from is { } fe ? (fe.ValueKind == JsonValueKind.String && fe.GetString()!.TrimStart().StartsWith('[') ? JsonNode.Parse(fe.GetString()!) : JsonNode.Parse(fe.GetRawText())) : null;
            var frames = start != null ? Shot.Frames(start.Value, end!.Value, follow ? Math.Max(0.5, every) : Math.Max(1, (end.Value - start.Value) / 24)) : [f0];
            Vec3 Middle(double f) { var b = Box3.Of(targets.SelectMany(t => Shot.Corners(t, f))); return (b.Min + b.Max) * 0.5; }
            var direction = Shot.Direction(fromNode, cam.WorldPosition(f0) - Middle(f0) is var d && d.Length > 1e-6 ? d : new Vec3(0, 0.25, -1));
            var calls = SceneCalls(root, c, l, ["set_transform", "look_at"]);
            var o = new JsonObject { ["camera"] = cam.Name, ["items"] = new JsonArray([.. targets.Select(t => (JsonNode)t.Name)]) };
            if (!follow)
            {
                var (pos, center, dist) = Shot.Fit(cam, f0, frames.SelectMany(f => targets.SelectMany(t => Shot.Corners(t, f))).ToList(), direction, margin);
                bool k = key ?? (Keying.IsAnimated(cam.Channels[0]) || Keying.IsAnimated(cam.Channels[3]));
                calls.Call("set_transform", new JsonObject { ["item"] = cam.Name, ["frame"] = f0, ["position"] = Json3(Local(cam, f0, pos)), ["key"] = k });
                calls.Call("look_at", new JsonObject { ["item"] = cam.Name, ["point"] = Json3(center), ["frame"] = f0, ["key"] = k });
                o["position"] = Json3(pos);
                o["aimed_at"] = Json3(center);
                o["distance"] = Math.Round(dist, 4);
                o["keyed"] = k;
            }
            else
            {
                foreach (var ch in cam.Channels.Take(6))
                    for (int i = ch.Keys.Count - 1; i >= 0; i--)
                        if (ch.Keys[i].Frame > frames[0] + 1e-9 && ch.Keys[i].Frame <= frames[^1] + 1e-9 && ch.Keys.Count > 1) ch.RemoveKey(i);
                foreach (var f in frames)
                {
                    var (pos, center, _) = Shot.Fit(cam, f, targets.SelectMany(t => Shot.Corners(t, f)).ToList(), direction, margin);
                    calls.Call("set_transform", new JsonObject { ["item"] = cam.Name, ["frame"] = f, ["position"] = Json3(Local(cam, f, pos)), ["key"] = true });
                    calls.Call("look_at", new JsonObject { ["item"] = cam.Name, ["point"] = Json3(center), ["frame"] = f, ["key"] = true });
                }
                o["keys"] = frames.Count;
                o["frames"] = new JsonArray(frames[0], frames[^1]);
            }
            return o;
        }, cancel);

    [McpServerTool(Name = "check_shot", ReadOnly = true, Idempotent = true)]
    [Description("Looks over the shot without rendering, at frames through it (every few frames from first to last), through a camera, and says what to fix before rendering: objects never in view, leaving the picture or cut off by its edge, too small to see; objects sinking below the ground (an object named ground or floor: by their lowest real point) or passing into another (points of one inside the other's shape, so touching and rings round poles don't count); objects no light reaches; a camera inside an object; frames with nothing but the ground in the picture; keys after the last frame; no lights at all. Each problem has its frames and a fix. Also says, for each object, in how many frames it's seen and how much of the picture's height it fills at most. Then look with render_scene_view (camera) or a small render_animation's contact sheet.")]
    public Task<string> CheckShot(
        [Description("The camera (default: the current one).")] string? camera = null,
        [Description("The first frame (default: the scene's first).")] double? first = null,
        [Description("The last frame (default: the scene's last).")] double? last = null,
        [Description("Frames between looks (default: about 24 looks in all).")] double? every = null,
        [Description("Below this fraction of the picture's height an object is too small (default 0.04).")] double small = 0.04,
        CancellationToken cancel = default) =>
        Making("checked the shot", (_, l, _) =>
        {
            var s = l.Scene;
            var cam = (camera != null ? ItemByName(s, camera) : s.CurrentCamera) as CameraItem
                ?? throw new ArgumentException(camera != null ? $"{camera} isn't a camera." : "The scene has no camera.");
            double a = first ?? s.FirstFrame, b = last ?? s.LastFrame;
            if (!(b >= a)) throw new ArgumentException("last must be at or after first.");
            double step = every ?? Math.Max(1, (b - a) / 24);
            if (!(step > 0)) throw new ArgumentException("every must be more than 0.");
            if ((b - a) / step > 2000) throw new ArgumentException("That's more than 2000 frames to look at: give a larger every.");
            return Shot.Check(l, cam, Shot.Frames(a, b, step), small);
        }, cancel, changes: false);

    // ───────────────────────────── plans ─────────────────────────────

    private static JsonObject Obj(JsonNode node, string what) =>
        node as JsonObject is { } o ? (JsonObject)o.DeepClone() : throw new ArgumentException($"{what} must be an object {{...}}.");

    private static IEnumerable<JsonNode> List(JsonNode node, string what) =>
        node is JsonArray a ? a.Select((x, i) => x ?? throw new ArgumentException($"{what}[{i}] is empty.")) : throw new ArgumentException($"{what} must be a list [...].");

    private static JsonArray Json3(Vec3 v) => new(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6));

    private static Vec3 Local(SceneItem item, double frame, Vec3 world) =>
        item.Parent is { } parent ? parent.WorldMatrix(frame).Inverted().TransformPoint(world) : world;

    /// <summary>The plan's lights: a rig sized to the objects, or add_light calls.</summary>
    private static JsonNode Lights(JsonNode lights, LayoutSession l, EngineCaller calls)
    {
        if (lights is JsonArray list)
        {
            var names = new JsonArray();
            foreach (var (o, i) in list.Select((x, i) => (Obj(x ?? throw new ArgumentException($"lights[{i}] is empty."), $"lights[{i}]"), i)))
                names.Add(EngineCaller.AsObject(calls.Call("add_light", o))["name"]?.DeepClone());
            return names;
        }
        string rig;
        double scale = 1;
        bool keep = false;
        if (lights is JsonValue v && v.TryGetValue(out string? r)) rig = r;
        else if (lights is JsonObject o)
        {
            foreach (var (key, _) in o)
                if (key is not ("rig" or "intensity" or "keep_lights")) throw new ArgumentException($"A lights rig has no \"{key}\": it takes rig, intensity and keep_lights.");
            rig = o["rig"]?.GetValue<string>() ?? throw new ArgumentException("rig is needed: " + string.Join(", ", Rigs) + ".");
            scale = o["intensity"]?.GetValue<double>() ?? 1;
            keep = o["keep_lights"]?.GetValue<bool>() ?? false;
            if (!(scale > 0)) throw new ArgumentException("intensity must be more than 0.");
        }
        else throw new ArgumentException("lights must be a rig's name, {\"rig\": ...}, or a list of add_light's arguments.");
        rig = rig.Trim().ToLowerInvariant();
        if (!Rigs.Contains(rig)) throw new ArgumentException($"There's no rig \"{rig}\": they are {string.Join(", ", Rigs)}.");
        var s = l.Scene;
        double f = l.Frame;
        var subjects = s.Objects.Where(o => !Shot.IsGround(o) && o.BaseMesh().Points.Count > 0).ToList();
        if (subjects.Count == 0) subjects = s.Objects.Where(o => o.BaseMesh().Points.Count > 0).ToList();
        var box = subjects.Count > 0 ? Box3.Of(subjects.SelectMany(o => Shot.Corners(o, f))) : new Box3(new Vec3(-0.5, 0, -0.5), new Vec3(0.5, 1, 0.5));
        var c = (box.Min + box.Max) * 0.5;
        double size = Math.Max(0.5, (box.Max - box.Min).Length / 2);
        if (!keep)
            foreach (var light in s.Lights.ToList()) calls.Call("remove_item", new JsonObject { ["item"] = light.Name });
        var made = new JsonArray();
        void Light(string name, string type, Vec3 offset, int[] colour, double intensity, bool shadows = true, JsonObject? more = null)
        {
            var args = new JsonObject
            {
                ["type"] = type, ["name"] = name, ["position"] = Json3(c + offset * size), ["look_at"] = Json3(c),
                ["color"] = new JsonArray([.. colour.Select(x => (JsonNode)x)]), ["intensity"] = Math.Round(intensity * scale, 4), ["shadows"] = shadows,
            };
            if (more != null) foreach (var (k, x) in more) args[k] = x?.DeepClone();
            made.Add(EngineCaller.AsObject(calls.Call("add_light", args))["name"]?.DeepClone());
        }
        switch (rig)
        {
            case "three_point":
                // A distant key, so things that move keep their light (a spot's pool would stay behind).
                Light("Key", "distant", new Vec3(-2.2, 2.6, -2.4), [255, 244, 229], 1.05);
                Light("Fill", "distant", new Vec3(2.5, 0.8, -2), [214, 226, 255], 0.35, shadows: false);
                Light("Rim", "distant", new Vec3(0.8, 2.2, 3), [255, 255, 255], 0.7, shadows: false);
                calls.Call("set_scene", new JsonObject { ["ambient_intensity"] = 0.1 });
                break;
            case "sun_and_sky":
                Light("Sun", "distant", new Vec3(-2, 3.5, -1.5), [255, 240, 214], 1.2);
                calls.Call("set_scene", new JsonObject { ["environment"] = "builtin:sky", ["environment_lighting"] = true, ["environment_intensity"] = 0.6, ["ambient_intensity"] = 0.05 });
                break;
            case "studio":
                Light("Softbox", "area", new Vec3(-2, 2.5, -2), [255, 250, 244], 1.2, more: new JsonObject { ["area_size"] = new JsonArray(Math.Round(1.5 * size, 3), Math.Round(1.5 * size, 3)) });
                Light("Fill", "area", new Vec3(2.5, 1, -1.5), [240, 245, 255], 0.4, shadows: false, more: new JsonObject { ["area_size"] = new JsonArray(Math.Round(2 * size, 3), Math.Round(2 * size, 3)) });
                calls.Call("set_scene", new JsonObject { ["environment"] = "builtin:studio", ["environment_intensity"] = 0.8, ["ambient_intensity"] = 0.05 });
                break;
            case "night":
                Light("Moon", "distant", new Vec3(2, 3, 1.5), [150, 170, 255], 0.35);
                Light("Lamp", "point", new Vec3(-1.2, 1.4, -1.2), [255, 190, 120], 1, more: new JsonObject { ["range"] = Math.Round(6 * size, 3), ["radius"] = Math.Round(0.05 * size, 4) });
                calls.Call("set_scene", new JsonObject { ["ambient_intensity"] = 0.04, ["ambient_color"] = new JsonArray(120, 140, 220), ["background_color"] = new JsonArray(8, 10, 24) });
                break;
            case "noir":
                // One hard key from high to the side, almost no fill: deep shadows on a black ground.
                Light("Key", "spot", new Vec3(-2.5, 3.2, -1.2), [255, 255, 255], 1.4, more: new JsonObject { ["cone_angle"] = 28, ["edge_angle"] = 4 });
                Light("Edge", "distant", new Vec3(1.5, 1.2, 3), [255, 255, 255], 0.25, shadows: false);
                calls.Call("set_scene", new JsonObject { ["ambient_intensity"] = 0.02, ["background_color"] = new JsonArray(0, 0, 0), ["gradient"] = false });
                break;
            case "golden_hour":
                // A low warm sun raking across, a cool sky fill, a sunset sky.
                Light("Sun", "distant", new Vec3(-3, 0.75, -1.2), [255, 196, 120], 1.3);
                Light("Sky", "distant", new Vec3(1.5, 3, 2), [150, 175, 230], 0.4, shadows: false);
                calls.Call("set_scene", new JsonObject
                {
                    ["ambient_intensity"] = 0.08, ["ambient_color"] = new JsonArray(255, 210, 170), ["gradient"] = true,
                    ["gradient_top"] = new JsonArray(80, 110, 170), ["gradient_bottom"] = new JsonArray(250, 170, 100),
                });
                break;
            case "sci_fi":
                // Cyan and magenta rims, a dim cool key, dark teal haze.
                Light("Key", "distant", new Vec3(-1.5, 2.5, -2.5), [180, 200, 255], 0.55);
                Light("Cyan Rim", "point", new Vec3(2.2, 1.2, 2.2), [40, 230, 255], 1.6, shadows: false, more: new JsonObject { ["range"] = Math.Round(8 * size, 3) });
                Light("Magenta Rim", "point", new Vec3(-2.2, 1.0, 2.2), [255, 40, 200], 1.4, shadows: false, more: new JsonObject { ["range"] = Math.Round(8 * size, 3) });
                calls.Call("set_scene", new JsonObject
                {
                    ["ambient_intensity"] = 0.05, ["background_color"] = new JsonArray(4, 12, 18), ["gradient"] = false,
                    ["fog"] = "linear", ["fog_min"] = Math.Round(4 * size, 3), ["fog_max"] = Math.Round(30 * size, 3), ["fog_color"] = new JsonArray(6, 22, 30),
                });
                break;
            case "cartoon":
                // Bright and even: a strong key and fill, high ambient, a clear sky.
                Light("Key", "distant", new Vec3(-2, 3, -2.5), [255, 255, 250], 1.0);
                Light("Fill", "distant", new Vec3(2.5, 1, -2), [255, 255, 255], 0.55, shadows: false);
                calls.Call("set_scene", new JsonObject
                {
                    ["ambient_intensity"] = 0.35, ["gradient"] = true, ["gradient_top"] = new JsonArray(110, 180, 255), ["gradient_bottom"] = new JsonArray(220, 240, 255),
                });
                break;
        }
        return new JsonObject { ["rig"] = rig, ["lights"] = made };
    }
}
