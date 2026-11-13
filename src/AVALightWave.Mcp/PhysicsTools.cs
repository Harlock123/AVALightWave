using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Physics (milestone B, stage 2): rigid bodies on objects, simulating them, and baking the result into keys.</summary>
public sealed partial class LightWaveTools
{
    [McpServerTool(Name = "set_rigid_body", Destructive = true)]
    [Description("Makes an object a rigid body, or changes or (off) removes one, as one undo step. dynamic bodies fall under gravity from their start frame (where their keys have them then), bouncing, sliding, tumbling and stacking against each other and against static bodies, which move by their keys and push (make the ground static). shape: hull (default: a 26-sided convex fit to its points), box, or sphere (round things roll). mass in kg (only ratios matter), bounce 0 to 1, friction 0 to 2, damping a second, gravity m/s² (9.81). velocity [x, y, z] m/s and spin [x, y, z] degrees a second (about the world's axes) start it moving: a push to a domino, a throw; with inherit_motion (default) the speed its keys give it as it starts is added (a throw keyed before the start frame). floor: a height it can't go below (static bodies make floors too). The simulation runs from the earliest start frame when anything asks where a body is (views, renders, get_scene); simulate reports how it went; bake_simulation turns it into ordinary keys.")]
    public Task<string> SetRigidBody(
        [Description("The object, by name or #index.")] string @object,
        [Description("Remove its rigid body.")] bool off = false,
        [Description("dynamic (default) or static.")] string? kind = null,
        [Description("hull (default), box or sphere.")] string? shape = null,
        [Description("kg (default 1).")] double? mass = null,
        [Description("0 to 1 (default 0.3).")] double? bounce = null,
        [Description("0 to 2 (default 0.5).")] double? friction = null,
        [Description("How fast its motion dies away in the air, a second (default 0.05).")] double? damping = null,
        [Description("m/s² down -Y (default 9.81).")] double? gravity = null,
        [Description("Its start speed [x, y, z], m/s.")] double[]? velocity = null,
        [Description("Its start spin [x, y, z], degrees a second about the world's X, Y and Z.")] double[]? spin = null,
        [Description("Add the speed its keys give it at the start frame (default true; only from keys inside the scene's frames).")] bool? inherit_motion = null,
        [Description("A floor height, metres.")] double? floor = null,
        [Description("Take its floor away.")] bool no_floor = false,
        [Description("The frame it's let go (default: the scene's first).")] double? start_frame = null,
        [Description("Steps a frame, 1 to 100 (default 10): more is steadier and slower.")] int? substeps = null,
        CancellationToken cancel = default) =>
        Making("set a rigid body", (_, l, _) =>
        {
            var o = ObjectByName(l.Scene, @object);
            if (off)
            {
                o.RigidBody = null;
                l.Scene.Notify();
                return new JsonObject { ["object"] = o.Name, ["rigid_body"] = null };
            }
            var r = o.RigidBody ?? new RigidBodySettings();
            if (kind != null) r = r with { Kind = Choice<RigidBodyKind>(kind, "kind", "dynamic or static") };
            if (shape != null) r = r with { Shape = Choice<CollisionShape>(shape, "shape", "hull, box or sphere") };
            r = r with
            {
                Mass = mass ?? r.Mass, Bounce = bounce ?? r.Bounce, Friction = friction ?? r.Friction, Damping = damping ?? r.Damping,
                Gravity = gravity ?? r.Gravity, InheritMotion = inherit_motion ?? r.InheritMotion, Substeps = substeps ?? r.Substeps,
                Velocity = velocity != null ? Vector(velocity, "velocity") : r.Velocity, Spin = spin != null ? Vector(spin, "spin") : r.Spin,
                Floor = no_floor ? null : floor ?? r.Floor, StartFrame = start_frame ?? r.StartFrame,
            };
            o.RigidBody = r.Checked();
            if (o.BaseMesh().Points.Count == 0) throw new ArgumentException($"{o.Name} has no points to collide with.");
            l.Scene.Notify();
            return new JsonObject { ["object"] = o.Name, ["rigid_body"] = Describe(o.RigidBody) };
        }, cancel);

    [McpServerTool(Name = "simulate", ReadOnly = true, Idempotent = true)]
    [Description("Runs the rigid-body simulation through the scene's frames (or to last) and reports, for each dynamic body, where it starts and ends, the highest and lowest it goes, the frame it comes to rest (if it does), and its rotation at the end. Changes nothing. Use it to check a physics shot before rendering, then bake_simulation if you want keys to refine.")]
    public Task<string> Simulate(
        [Description("The last frame to run to (default: the scene's last).")] double? last = null,
        CancellationToken cancel = default) =>
        Making("simulated", (_, l, _) =>
        {
            var s = l.Scene;
            var bodies = s.Objects.Where(o => o.RigidBody is { Kind: RigidBodyKind.Dynamic }).ToList();
            if (bodies.Count == 0) throw new ArgumentException("No object is a dynamic rigid body: set_rigid_body makes one.");
            double end = last ?? s.LastFrame;
            var list = new JsonArray();
            foreach (var o in bodies)
            {
                double start = o.RigidBody!.StartFrame ?? s.FirstFrame;
                Vec3 first = o.WorldPosition(start), prev = first;
                double hi = first.Y, lo = first.Y;
                double? rest = null;
                for (double f = start + 1; f <= end; f++)
                {
                    var p = o.WorldPosition(f);
                    hi = Math.Max(hi, p.Y);
                    lo = Math.Min(lo, p.Y);
                    if ((p - prev).Length < 1e-4) rest ??= f - 1; else rest = null;
                    prev = p;
                }
                list.Add(new JsonObject
                {
                    ["object"] = o.Name, ["start_frame"] = start, ["from"] = Json3(first), ["to"] = Json3(o.WorldPosition(end)),
                    ["highest"] = Math.Round(hi, 4), ["lowest"] = Math.Round(lo, 4), ["at_rest_from"] = rest,
                    ["end_rotation"] = Json3(o.MotionRotationAt(end)),
                });
            }
            return new JsonObject { ["frames"] = new JsonArray(s.FirstFrame, end), ["bodies"] = list };
        }, cancel, changes: false);

    [McpServerTool(Name = "bake_simulation", Destructive = true)]
    [Description("Bakes the rigid-body simulation into ordinary keys, as one undo step: each dynamic body (or just the ones named) gets position and rotation keys every `every` frames from its start frame to the last (keys there before are replaced; rotations kept continuous, without jumps of 360°), and stops being a rigid body, so its motion can be edited in the graph editor and other programs see it. undo_scene puts the simulation back.")]
    public Task<string> BakeSimulation(
        [Description("The objects to bake, by name (default: every dynamic body).")] string[]? objects = null,
        [Description("Frames between keys (default 1).")] double every = 1,
        [Description("The last frame (default: the scene's last).")] double? last = null,
        CancellationToken cancel = default) =>
        Making("baked the simulation", (_, l, _) =>
        {
            var s = l.Scene;
            if (!(every >= 0.25) || every > 30) throw new ArgumentException("every must be from 0.25 to 30 frames.");
            var bodies = (objects is { Length: > 0 } ? objects.Select(n => ObjectByName(s, n)) : s.Objects.Where(o => o.RigidBody is { Kind: RigidBodyKind.Dynamic })).ToList();
            if (bodies.Count == 0) throw new ArgumentException("No object is a dynamic rigid body to bake.");
            foreach (var o in bodies)
                if (o.RigidBody is not { Kind: RigidBodyKind.Dynamic }) throw new ArgumentException($"{o.Name} isn't a dynamic rigid body.");
            return new JsonObject { ["baked"] = BakeBodies(s, bodies, every, last ?? s.LastFrame) };
        }, cancel);

    /// <summary>
    /// Bakes dynamic rigid bodies' simulated motion into keys from each one's start frame to <paramref name="end"/>, every so
    /// many frames, and takes their rigid bodies off. Call inside an edit (one undo step).
    /// </summary>
    public static JsonArray BakeBodies(AVAModeler.Scene.Scene s, IReadOnlyList<ObjectItem> bodies, double every, double end)
    {
        // Read every pose first: once one body's keys change, the others' simulation would start again from them.
        var poses = bodies.ToDictionary<ObjectItem, ObjectItem, List<(double F, Vec3 P, Vec3 R)>>(o => o, o =>
        {
            double start = o.RigidBody!.StartFrame ?? s.FirstFrame;
            var frames = new List<double>();
            for (double f = start; f < end - 1e-9; f += every) frames.Add(Math.Round(f, 4));
            frames.Add(end);
            return frames.Select(f => (F: f, P: o.MotionPositionAt(f), R: o.MotionRotationAt(f))).ToList();
        }, ReferenceEqualityComparer.Instance);
        var said = new JsonArray();
        foreach (var o in bodies)
        {
            var keys = poses[o];
            double start = keys[0].F;
            o.RigidBody = null;
            Vec3? before = null;
            for (int ch = 0; ch < 6; ch++)
            {
                var env = o.Channels[ch];
                for (int i = env.Keys.Count - 1; i >= 0; i--)
                    if (env.Keys[i].Frame >= start - 1e-9 && env.Keys.Count > 1) env.RemoveKey(i);
            }
            foreach (var (f, p, r0) in keys)
            {
                var r = r0;
                // Keep each angle within half a turn of the one before, so the curve doesn't spin back through 360°.
                if (before is { } b)
                    r = new Vec3(Near(r.X, b.X), Near(r.Y, b.Y), Near(r.Z, b.Z));
                before = r;
                for (int ch = 0; ch < 3; ch++) Keying.Key(o.Channels[ch], s, new EnvelopeKey(f, p[ch]), keepShape: false);
                for (int ch = 0; ch < 3; ch++) Keying.Key(o.Channels[3 + ch], s, new EnvelopeKey(f, r[ch]), keepShape: false);
            }
            said.Add(new JsonObject { ["object"] = o.Name, ["keys"] = keys.Count, ["frames"] = new JsonArray(start, end) });
        }
        s.Notify();
        return said;
    }

    private static double Near(double angle, double previous) => angle + 360 * Math.Round((previous - angle) / 360);

    private static T Choice<T>(string value, string what, string choices) where T : struct, Enum =>
        Enum.TryParse<T>((value ?? "").Trim(), ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : throw new ArgumentException($"{what} must be {choices}, not \"{value}\".");

    private static JsonObject Describe(RigidBodySettings r)
    {
        var o = new JsonObject
        {
            ["kind"] = r.Kind.ToString().ToLowerInvariant(), ["shape"] = r.Shape.ToString().ToLowerInvariant(), ["mass"] = r.Mass, ["bounce"] = r.Bounce,
            ["friction"] = r.Friction, ["damping"] = r.Damping, ["gravity"] = r.Gravity, ["inherit_motion"] = r.InheritMotion, ["substeps"] = r.Substeps,
        };
        if (r.Velocity != Vec3.Zero) o["velocity"] = Json3(r.Velocity);
        if (r.Spin != Vec3.Zero) o["spin"] = Json3(r.Spin);
        if (r.Floor is { } f) o["floor"] = f;
        if (r.StartFrame is { } s) o["start_frame"] = s;
        return o;
    }
}
