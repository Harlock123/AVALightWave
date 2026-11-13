using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Motion in a phrase: <c>animate</c> (moves for any items) and <c>camera_move</c> (a camera's shot), both through <see cref="Motion"/>.</summary>
public sealed partial class LightWaveTools
{
    public const string MoveHelp =
        "Each move is {\"item\": name, \"kind\": kind, ...} over a span of frames: start (a frame), or after (a named move, or \"previous\": starts when it ends) or with (starts when it starts and, unless told otherwise, lasts as long), plus delay frames; then duration (default 30) or end. Moves without any of these start at the scene's first frame. ease: linear, ease_in, ease_out, ease_in_out (the default for most) or overshoot. name lets later moves refer to it. aim (an item or a point) keeps the item facing it while it moves, keyed every `every` frames (default 2). Kinds: " +
        "move (to [x, y, z] or by [dx, dy, dz]); turn (to or by [heading, pitch, bank]); grow (to or by: a factor, or [x, y, z]); " +
        "bounce (height metres above where it is (default 1), count bounces after the first landing (3), decay each bounce's height over the last (0.55), squash 0 to 0.6 for squash and stretch (0; the object's origin should be at its base, as make_object puts it), start_at top or ground, and to or by to travel across as it bounces: keyed as exact parabolas); " +
        "spin (axis heading, pitch or bank; turns (1) or degrees; linear by default); " +
        "orbit (around an item or point; radius (default: how far it is now), turns or degrees (positive turns clockwise seen from above), height above the centre (default: as now), face center, forward or none (cameras and lights face the centre)); " +
        "sway (channel (default bank; any channel, e.g. heading or position.x), amplitude (degrees, or metres), cycles, damping 0 to 1: how much it has died away by the end); bob (up and down: amplitude metres (0.1), cycles); " +
        "shake (amplitude metres (0.05), or rotate true for degrees (3); seed); pop_in (scale from nothing to its size, overshooting); pop_out; fade_in and fade_out (an object's dissolve); " +
        "through (points: [[x, y, z], ...] to pass through at an even speed; face forward to turn along the way); track (target: keep facing an item or point); " +
        "dolly (distance forward along its view; negative backs off); truck (distance to its right); crane (height up, keeping a target in view if given); push_in (target, ending distance metres from it, default half way, aiming at it); follow (target, keeping offset [x, y, z] from it, default as now, aiming at it). " +
        "Moves on the same channel over the same frames replace each other's keys (the later wins); for motion on top of keys use add_motion_modifier.";

    [McpServerTool(Name = "animate", Destructive = true)]
    [Description("Animates items with moves described in a phrase each, written as ordinary keys (the graph editor and dope sheet can change them afterwards), all as one undo step: if any move is wrong, the error names it and nothing changes. Moves run in order, each from where the ones before left the item, so a sequence of beats across many items is one call. " + MoveHelp + " The scene is lengthened if a move runs past its last frame (unless extend is false). Returns each move's frames, channels and key count.")]
    public Task<string> Animate(
        [Description("The moves, in order.")] JsonObject[] moves,
        [Description("Lengthen the scene when a move ends after its last frame (default true).")] bool extend = true,
        CancellationToken cancel = default) =>
        Making("animated", (_, l, _) => new Motion(l).Run(moves, extend), cancel);

    [McpServerTool(Name = "camera_move", Destructive = true)]
    [Description("Gives a camera a shot over a span of frames, as keys (one undo step), keeping its target in view: dolly (in along its view by distance metres; negative pulls back), push_in (closes on target until distance metres from it), orbit (circles target, turns or degrees, at its present distance and height unless radius or height are given), crane (rises height metres, aiming at target), truck (slides distance metres to its right, aiming at target if given), follow (keeps offset [x, y, z] from target, as now unless given, aiming at it), or handheld (a gentle wobble all through the scene: a jitter motion modifier of amount degrees, default 0.6). ease: linear, ease_in, ease_out, ease_in_out (default) or overshoot. Several shots one after another: call again with start after the last one's end, or use animate with these kinds.")]
    public Task<string> CameraMove(
        [Description("dolly, push_in, orbit, crane, truck, follow or handheld.")] string kind,
        [Description("What to keep in view: an item's name (default: none for dolly and truck; needed for push_in, orbit and follow).")] string? target = null,
        [Description("The camera (default: the current camera).")] string? camera = null,
        [Description("The first frame (default: the scene's first).")] double? start = null,
        [Description("The last frame (or give duration).")] double? end = null,
        [Description("Frames it lasts (default 30, or to the scene's last frame for follow).")] double? duration = null,
        [Description("linear, ease_in, ease_out, ease_in_out (default) or overshoot.")] string? ease = null,
        [Description("dolly, truck: metres to move; push_in: metres from the target at the end.")] double? distance = null,
        [Description("orbit: whole turns (default 0.25: a quarter of the way round).")] double? turns = null,
        [Description("orbit: degrees instead of turns.")] double? degrees = null,
        [Description("crane: metres up (negative down); orbit: metres above the target.")] double? height = null,
        [Description("orbit: metres from the target (default: as now).")] double? radius = null,
        [Description("follow: [x, y, z] metres from the target (default: as now).")] double[]? offset = null,
        [Description("handheld: degrees of wobble (default 0.6).")] double? amount = null,
        CancellationToken cancel = default) =>
        Making("moved the camera", (c, l, root) =>
        {
            var k = (kind ?? "").Trim().ToLowerInvariant();
            var cam = camera != null ? ItemByName(l.Scene, camera) : l.Scene.CurrentCamera ?? throw new ArgumentException("The scene has no camera: add_camera first.");
            if (cam is not AVAModeler.Scene.CameraItem) throw new ArgumentException($"{cam.Name} is a {cam.Kind.ToLowerInvariant()}, not a camera.");
            if (k == "handheld")
            {
                double a = amount ?? 0.6;
                var mod = new JsonObject
                {
                    ["item"] = cam.Name, ["kind"] = "jitter", ["position_amount"] = new JsonArray(0.0, 0.0, 0.0),
                    ["rotation_amount"] = new JsonArray(a, a, a / 2), ["frequency"] = 1.5,
                };
                return EngineCaller.AsObject(SceneCalls(root, c, l, ["add_motion_modifier"]).Call("add_motion_modifier", mod));
            }
            var move = new JsonObject { ["item"] = cam.Name };
            if (start != null) move["start"] = start;
            if (end != null) move["end"] = end;
            if (duration != null) move["duration"] = duration;
            else if (end == null && k == "follow") move["end"] = (double)l.Scene.LastFrame;
            if (ease != null) move["ease"] = ease;
            switch (k)
            {
                case "dolly" or "truck":
                    move["kind"] = k;
                    move["distance"] = distance ?? throw new ArgumentException("distance is needed: metres to move.");
                    if (target != null) move["target"] = target;
                    break;
                case "push_in":
                    move["kind"] = "push_in";
                    move["target"] = target ?? throw new ArgumentException("target is needed: what to close in on.");
                    if (distance != null) move["distance"] = distance;
                    break;
                case "orbit":
                    move["kind"] = "orbit";
                    move["around"] = target ?? throw new ArgumentException("target is needed: what to circle.");
                    move["face"] = "center";
                    if (degrees != null) move["degrees"] = degrees;
                    else move["turns"] = turns ?? 0.25;
                    if (height != null) move["height"] = height;
                    if (radius != null) move["radius"] = radius;
                    move["ease"] ??= "ease_in_out";
                    break;
                case "crane":
                    move["kind"] = "crane";
                    move["height"] = height ?? 1;
                    if (target != null) move["target"] = target;
                    break;
                case "follow":
                    move["kind"] = "follow";
                    move["target"] = target ?? throw new ArgumentException("target is needed: the item to follow.");
                    if (offset != null) move["offset"] = new JsonArray([.. offset.Select(x => (JsonNode)x)]);
                    break;
                default:
                    throw new ArgumentException($"kind must be dolly, push_in, orbit, crane, truck, follow or handheld, not \"{kind}\".");
            }
            return new Motion(l).Run([move], extend: true);
        }, cancel);
}
