using AVAModeler.Core;
using AVAModeler.Geometry;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVALightWave;

/// <summary>A character's rig as the walk finds it: its rig object, its legs' bones and foot goals, its upper arms, and its sizes.</summary>
public sealed record CharacterRig(ObjectItem Rig, Bone ThighL, Bone ShinL, Bone ThighR, Bone ShinR, SceneItem FootL, SceneItem FootR, Bone? ArmL, Bone? ArmR, double Scale)
{
    /// <summary>The bones' names a rig needs (an object with these bones, the shins with IK goals, walks).</summary>
    public static readonly string[] LegBones = ["Thigh L", "Shin L", "Thigh R", "Shin R"];

    public double HipHeight => (Rig.Bones.FirstOrDefault(b => b.Name == "Thigh L")?.RestPosition.Y) ?? 0.92 * Scale;
    public double LegLength => ThighL.RestLength + ShinL.RestLength;
    public double HipWidth => Math.Abs(ThighL.RestPosition.X - ThighR.RestPosition.X) / 2;

    /// <summary>The rig of an object made by <see cref="Characters.Make"/>, or any with the leg bones named so and IK goals on the shins.</summary>
    public static CharacterRig Of(ObjectItem rig)
    {
        Bone Need(string n) => rig.Bones.FirstOrDefault(b => b.Name == n)
            ?? throw new ArgumentException($"{rig.Name} has no bone \"{n}\": a walking rig needs bones named {string.Join(", ", LegBones)} (make_character makes one).");
        var (tl, sl, tr, sr) = (Need("Thigh L"), Need("Shin L"), Need("Thigh R"), Need("Shin R"));
        var fl = sl.IKGoal ?? throw new ArgumentException($"{rig.Name}'s Shin L has no IK goal: give it one (set_ik) for the foot to step with.");
        var fr = sr.IKGoal ?? throw new ArgumentException($"{rig.Name}'s Shin R has no IK goal: give it one (set_ik) for the foot to step with.");
        double scale = (tl.RestLength + sl.RestLength) / 0.88;
        return new CharacterRig(rig, tl, sl, tr, sr, fl, fr, rig.Bones.FirstOrDefault(b => b.Name == "Upper Arm L"), rig.Bones.FirstOrDefault(b => b.Name == "Upper Arm R"), scale);
    }
}

/// <summary>
/// Characters (milestone C, stage 3): a simple jointed figure made in the scene, and walking it along a path with its
/// feet planted on whatever is below. The figure is rigid parts (torso, head, limbs) riding on an invisible rig object
/// and its bones, so nothing bends where it shouldn't; its legs reach for foot goals by IK.
/// </summary>
public static class Characters
{
    /// <summary>Makes a character standing at <paramref name="at"/> (its feet on that ground) facing <paramref name="heading"/>. Call inside an edit.</summary>
    public static CharacterRig Make(Scene s, string name, double height, Rgb body, Rgb skin, Vec3 at, double heading)
    {
        if (!(height >= 0.1) || height > 100) throw new ArgumentException("height must be from 0.1 to 100 metres.");
        double k = height / 1.8;
        double hip = 0.92 * k, thigh = 0.44 * k, shin = 0.44 * k, shoulder = 1.45 * k, upper = 0.3 * k, fore = 0.28 * k;
        string Free(string n) => Cloning.UniqueName(s, n);
        var rig = s.AddObject(Free(name), new Document());
        foreach (var (ch, v) in new[] { (Channel.PositionX, at.X), (Channel.PositionY, at.Y), (Channel.PositionZ, at.Z), (Channel.Heading, heading) })
            rig[ch].SetConstant(v);

        Document Part(string surface, Rgb colour, Action<Commands> build)
        {
            var d = new Document();
            d.Surfaces[0].Name = surface;
            d.Surfaces[0].Color = colour;
            d.Surfaces[0].Specular = 0.2;
            d.Surfaces[0].Smoothing = true;
            build(new Commands(d));
            d.Modified = true;
            return d;
        }
        void Box(Commands c, Vec3 centre, Vec3 size) => c.Run("Box", new Dictionary<string, object?> { ["Center"] = centre, ["Size"] = size, ["Surface"] = c.Document.Surfaces[0].Name });
        void Ball(Commands c, Vec3 centre, Vec3 radius) => c.Run("Ball", new Dictionary<string, object?> { ["Center"] = centre, ["Radius"] = radius, ["Sides"] = 16, ["Segments"] = 8, ["Surface"] = c.Document.Surfaces[0].Name });
        ObjectItem Add(string part, Document doc, SceneItem parent)
        {
            var o = s.AddObject(Free($"{rig.Name} {part}"), doc);
            o.Parent = parent;
            return o;
        }

        string clothes = $"{rig.Name} Clothes", skinName = $"{rig.Name} Skin";
        Add("Torso", Part(clothes, body, c => Box(c, new Vec3(0, (hip + shoulder) / 2 + 0.02 * k, 0), new Vec3(0.36 * k, shoulder - hip + 0.08 * k, 0.2 * k))), rig);
        Add("Head", Part(skinName, skin, c =>
        {
            Ball(c, new Vec3(0, 1.64 * k, 0), new Vec3(0.1 * k, 0.12 * k, 0.11 * k));
            c.Document.Selection.Clear();
            Box(c, new Vec3(0, 1.5 * k, 0), new Vec3(0.08 * k, 0.08 * k, 0.08 * k));
        }), rig);

        // Legs: thighs from the hips pointing down (pitch 90), shins on from the knees, each reaching for its foot goal.
        (Bone Thigh, Bone Shin) Leg(string side, double x, out NullItem goal)
        {
            // A little bent at rest (the thigh forward, the shin back): a straight leg is where IK can't tell which way to bend.
            var t = s.AddBone(rig, $"Thigh {side}", new Vec3(x, hip, 0), new Vec3(0, 86, 0), thigh);
            var sh = s.AddBone(rig, $"Shin {side}", new Vec3(0, 0, thigh), new Vec3(0, 8, 0), shin);
            sh.Parent = t;
            sh.ResetPose();
            goal = s.AddNull(Free($"{rig.Name} Foot {side}"));
            var foot = rig.WorldMatrix(s.FirstFrame).TransformPoint(new Vec3(x, hip - 0.86 * k, 0));
            goal[Channel.PositionX].SetConstant(foot.X);
            goal[Channel.PositionY].SetConstant(foot.Y);
            goal[Channel.PositionZ].SetConstant(foot.Z);
            sh.IKGoal = goal;
            sh.FullTimeIK = true;
            // The hip swings forward and back, and a little to the side (its heading, about the thigh's forward axis).
            t.IKLocked = IKChannels.Bank;
            sh.IKLocked = IKChannels.Heading | IKChannels.Bank;
            // A knee bends one way only: its shin swings back (a positive pitch in the thigh's frame, whose +Y is forward).
            sh.IKLimits = new IKLimits(Pitch: new IKLimit(2, 150));
            t.IKLimits = new IKLimits(Heading: new IKLimit(-20, 20), Pitch: new IKLimit(20, 160));
            Add($"Thigh {side}", Part(clothes, body, c => Box(c, new Vec3(0, 0, thigh / 2), new Vec3(0.13 * k, 0.13 * k, thigh))), t);
            Add($"Shin {side}", Part(clothes, body, c => Box(c, new Vec3(0, 0, shin / 2), new Vec3(0.11 * k, 0.11 * k, shin))), sh);
            Add($"Foot {side}", Part(skinName, skin, c => Box(c, new Vec3(0, 0.05 * k, shin + 0.03 * k), new Vec3(0.1 * k, 0.24 * k, 0.06 * k))), sh);
            return (t, sh);
        }
        var (thighL, shinL) = Leg("L", -0.1 * k, out var footL);
        var (thighR, shinR) = Leg("R", 0.1 * k, out var footR);

        // Arms: upper arms from the shoulders, forearms on from the elbows (posed by keys: they swing as it walks).
        Bone Arm(string side, double x)
        {
            var u = s.AddBone(rig, $"Upper Arm {side}", new Vec3(x, shoulder, 0), new Vec3(0, 90, 0), upper);
            var f = s.AddBone(rig, $"Forearm {side}", new Vec3(0, 0, upper), new Vec3(0, -10, 0), fore);
            f.Parent = u;
            f.ResetPose();
            Add($"Upper Arm {side}", Part(clothes, body, c => Box(c, new Vec3(0, 0, upper / 2), new Vec3(0.09 * k, 0.09 * k, upper))), u);
            Add($"Forearm {side}", Part(skinName, skin, c =>
            {
                Box(c, new Vec3(0, 0, fore / 2), new Vec3(0.08 * k, 0.08 * k, fore));
                c.Document.Selection.Clear();
                Ball(c, new Vec3(0, 0, fore + 0.05 * k), new Vec3(0.05 * k, 0.05 * k, 0.05 * k));
            }), f);
            return u;
        }
        var armL = Arm("L", -0.24 * k);
        var armR = Arm("R", 0.24 * k);
        s.Notify();
        return new CharacterRig(rig, thighL, shinL, thighR, shinR, footL, footR, armL, armR, k);
    }

    /// <summary>The height of the highest surface under a point (rays cast down onto every visible object but those left out), or null.</summary>
    public static double? GroundBelow(Scene s, double x, double z, double frame, double above, ISet<SceneItem> leaveOut)
    {
        double? best = null;
        foreach (var o in s.Objects)
        {
            if (!o.Visible || leaveOut.Contains(o) || o.IsPlaceholder) continue;
            var mesh = o.DeformedMesh(frame);
            if (mesh.Polygons.Count == 0) continue;
            var world = o.WorldMatrix(frame);
            var inv = world.Inverted();
            var from = inv.TransformPoint(new Vec3(x, above, z));
            var dir = inv.TransformVector(new Vec3(0, -1, 0));
            double scale = dir.Length;
            if (scale < 1e-12) continue;
            var tree = Trees.GetValue(mesh, m => new TriangleBvh(m));
            if (tree.Raycast(new Ray(from, dir / scale)) is { } hit)
            {
                double y = world.TransformPoint(hit.Point).Y;
                if (best == null || y > best) best = y;
            }
        }
        return best;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mesh, TriangleBvh> Trees = new();

    /// <summary>What a walk made.</summary>
    public sealed record Walked(double Start, double End, int Steps, double Distance);

    /// <summary>
    /// Walks a character along a path of points (on the ground plane; heights come from the ground), from
    /// <paramref name="start"/> at <paramref name="speed"/> metres a second, each step <paramref name="step"/> metres
    /// and its feet lifted <paramref name="lift"/>: the rig moves and turns along the way, bobbing and swaying; each
    /// foot goal steps from plant to plant on whatever is below; the arms swing against the legs. Call inside an edit.
    /// </summary>
    public static Walked Walk(Scene s, CharacterRig c, IReadOnlyList<Vec3> points, double start, double speed, double step, double lift)
    {
        if (!(speed > 0) || speed > 20) throw new ArgumentException("speed must be more than 0 and at most 20 m/s.");
        if (!(step > 0) || step > c.LegLength) throw new ArgumentException($"step must be more than 0 and at most the leg's length ({c.LegLength:0.##} m).");
        if (lift < 0 || lift > c.LegLength) throw new ArgumentException("lift must be from 0 to the leg's length.");
        double fps = s.FramesPerSecond > 0 ? s.FramesPerSecond : 30;
        var rigAt = c.Rig.PositionAt(start);
        var path = new List<Vec3> { new(rigAt.X, 0, rigAt.Z) };
        foreach (var p in points) if ((new Vec3(p.X, 0, p.Z) - path[^1]).Length > 1e-6) path.Add(new Vec3(p.X, 0, p.Z));
        if (path.Count < 2) throw new ArgumentException("The path goes nowhere: give a point away from where the character stands.");
        // Corners rounded off (Chaikin's corner cutting, the ends kept): people walk round a corner, not to a point and pivot.
        for (int pass = 0; pass < 3 && path.Count > 2; pass++)
        {
            var round = new List<Vec3> { path[0] };
            for (int i = 0; i < path.Count - 1; i++)
            {
                var (p0, p1) = (path[i], path[i + 1]);
                if (i > 0) round.Add(p0 * 0.75 + p1 * 0.25);
                if (i < path.Count - 2) round.Add(p0 * 0.25 + p1 * 0.75);
            }
            round.Add(path[^1]);
            path = round;
        }
        var lengths = new List<double> { 0 };
        for (int i = 1; i < path.Count; i++) lengths.Add(lengths[^1] + (path[i] - path[i - 1]).Length);
        double total = lengths[^1];
        Vec3 At(double d)
        {
            d = Math.Clamp(d, 0, total);
            int i = 1;
            while (i < path.Count - 1 && lengths[i] < d) i++;
            double t = (d - lengths[i - 1]) / Math.Max(1e-9, lengths[i] - lengths[i - 1]);
            return path[i - 1] + (path[i] - path[i - 1]) * t;
        }
        // The way ahead, looked at over a step either side, so a corner is turned over a couple of steps rather than at once.
        Vec3 Ahead(double d)
        {
            var a = At(Math.Max(0, d - step));
            var b = At(Math.Min(total, d + step));
            var dir = b - a;
            return dir.Length > 1e-9 ? dir.Normalized() : (path[^1] - path[0]).Normalized();
        }
        var ignore = new HashSet<SceneItem>(ReferenceEqualityComparer.Instance) { c.Rig };
        foreach (var o in s.Objects) if (o.Ancestors().Contains(c.Rig) || o.Ancestors().Any(a => a is Bone b && b.Owner == c.Rig)) ignore.Add(o);
        double above = c.Rig.WorldPosition(start).Y + 4 * c.LegLength;
        double Ground(double x, double z, double f) => GroundBelow(s, x, z, f, above, ignore) ?? 0;
        double ankle = 0.06 * c.Scale;

        int steps = Math.Max(1, (int)Math.Ceiling(total / step));
        double period = step / speed * fps;          // frames a step
        double end = start + steps * period;

        // The rig: along the path, turning to face its way, the hips a little low (knees bent), bobbing twice a stride.
        var rig = c.Rig;
        // Lowest as a foot lands (both feet down, the legs furthest apart), highest as it passes the other.
        // No sideways sway: the legs stay in their own planes, where IK lifts the feet cleanly.
        double hipDrop = 0.03 * c.Scale, bob = 0.015 * c.Scale, sway = 0.0;
        void Clear(Envelope e) { for (int i = e.Keys.Count - 1; i >= 0; i--) if (e.Keys[i].Frame > start + 1e-9 && e.Keys[i].Frame <= end + 1e-9 && e.Keys.Count > 1) e.RemoveKey(i); }
        foreach (var ch in new[] { Channel.PositionX, Channel.PositionY, Channel.PositionZ, Channel.Heading }) Clear(rig[ch]);
        double heading0 = rig.RotationAt(start).X;
        double lastHeading = heading0;
        var frames = new List<double>();
        for (double f = start; f < end - 1e-9; f += 2) frames.Add(f);
        frames.Add(end);
        foreach (double f in frames)
        {
            double d = Math.Min(total, (f - start) / fps * speed);
            var p = At(d);
            var ahead = Ahead(d);
            double phase = (f - start) / period * Math.PI;   // one step = half a turn of the cycle
            var side = new Vec3(ahead.Z, 0, -ahead.X);
            var place = p + side * (sway * Math.Sin(phase));
            double ground = Ground(p.X, p.Z, f);
            double h = Rotations.ToDegrees(Math.Atan2(ahead.X, ahead.Z));
            h += 360 * Math.Round((lastHeading - h) / 360);
            lastHeading = h;
            bool settled = f >= end - 1e-9;
            Keying.Key(rig[Channel.PositionX], s, new EnvelopeKey(f, place.X), keepShape: false);
            Keying.Key(rig[Channel.PositionZ], s, new EnvelopeKey(f, place.Z), keepShape: false);
            Keying.Key(rig[Channel.PositionY], s, new EnvelopeKey(f, ground - (settled ? 0 : hipDrop) + (settled ? 0 : bob * Math.Abs(Math.Sin(phase)))), keepShape: false);
            Keying.Key(rig[Channel.Heading], s, new EnvelopeKey(f, f == start ? heading0 : h), keepShape: false);
        }

        // The feet: the right steps first. Each step k (1…) swings one foot from where it stood to half a step ahead of
        // where the hips will be when it lands, the last bringing the feet together at the end.
        var feet = new[] { (Goal: c.FootR, Side: 1.0), (Goal: c.FootL, Side: -1.0) };
        foreach (var (goal, _) in feet) foreach (var ch in new[] { Channel.PositionX, Channel.PositionY, Channel.PositionZ }) Clear(goal[ch]);
        Vec3 Plant(double d, double sideSign)
        {
            var p = At(d);
            var ahead = Ahead(d);
            var side = new Vec3(ahead.Z, 0, -ahead.X);
            var q = p + side * (c.HipWidth * sideSign);
            return new Vec3(q.X, Ground(q.X, q.Z, start) + ankle, q.Z);
        }
        var where = feet.Select(f => f.Goal.PositionAt(start)).ToArray();
        void Key(SceneItem goal, double f, Vec3 v, bool hold)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                var key = new EnvelopeKey(f, v[axis]);
                Keying.Key(goal.Channels[axis], s, hold ? key.WithSpan(SpanKind.Linear) : key, keepShape: false);
            }
        }
        foreach (var (goal, _) in feet) Key(goal, start, goal.PositionAt(start), hold: false);
        for (int k = 1; k <= steps; k++)
        {
            int which = (k - 1) % 2;
            var (goal, sign) = feet[which];
            double f0 = start + (k - 1) * period, f1 = start + k * period;
            bool last = k == steps;
            double d = last ? total : Math.Min(total, k * step + step / 2);
            var to = Plant(d, sign);
            var from = where[which];
            Key(goal, f0, from, hold: true);                // it has stood still until now
            var mid = (from + to) * 0.5;
            Key(goal, (f0 + f1) / 2, mid + new Vec3(0, lift, 0), hold: false);
            Key(goal, f1, to, hold: false);
            where[which] = to;
        }
        // The foot that didn't make the last step comes alongside.
        {
            int other = steps % 2;
            var (goal, sign) = feet[other];
            var to = Plant(total, sign);
            if ((to - where[other]).Length > 1e-3)
            {
                double f0 = end, f1 = end + period;
                Key(goal, f0, where[other], hold: true);
                Key(goal, (f0 + f1) / 2, (where[other] + to) * 0.5 + new Vec3(0, lift, 0), hold: false);
                Key(goal, f1, to, hold: false);
                end = f1;
            }
        }

        // The legs, solved exactly a frame at a time (two bones, the knee toward the way the body faces) and keyed, so
        // each ankle is on its goal; the shins' full-time IK is then off, the goals kept as the footprints.
        foreach (var (thigh, shin) in new[] { (c.ThighL, c.ShinL), (c.ThighR, c.ShinR) })
        {
            var goal = shin.IKGoal!;
            shin.FullTimeIK = false;
            foreach (var ch in new[] { Channel.Heading, Channel.Pitch, Channel.Bank }) { Clear(thigh[ch]); Clear(shin[ch]); }
            double l1 = thigh.RestLength, l2 = shin.RestLength;
            for (double f = start; f <= end + 1e-9; f = f + 1 > end && f < end ? end : f + 1)
            {
                var toRig = rig.WorldMatrix(f).Inverted();
                var hip = thigh.RestPosition; // the thigh hangs from the rig object at its rest place
                var target = toRig.TransformPoint(goal.WorldPosition(f));
                var reach = target - hip;
                double d = Math.Clamp(reach.Length, Math.Abs(l1 - l2) + 1e-4, (l1 + l2) * 0.9995);
                var u = reach.Length > 1e-9 ? reach.Normalized() : new Vec3(0, -1, 0);
                var forward = Vec3.UnitZ - u * Vec3.Dot(Vec3.UnitZ, u);
                forward = forward.Length > 1e-6 ? forward.Normalized() : Vec3.UnitZ;
                double alpha = Math.Acos(Math.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1));
                var thighDir = u * Math.Cos(alpha) + forward * Math.Sin(alpha);
                var knee = hip + thighDir * l1;
                var foot = hip + u * d;
                var hp = Rotations.FromDirection(thighDir);
                var inThigh = Rotations.Matrix(new Vec3(hp.X, hp.Y, 0)).Inverted().TransformVector((foot - knee).Normalized());
                var sp = Rotations.FromDirection(inThigh);
                Keying.Key(thigh[Channel.Heading], s, new EnvelopeKey(f, hp.X), keepShape: false);
                Keying.Key(thigh[Channel.Pitch], s, new EnvelopeKey(f, hp.Y), keepShape: false);
                Keying.Key(thigh[Channel.Bank], s, new EnvelopeKey(f, 0), keepShape: false);
                Keying.Key(shin[Channel.Heading], s, new EnvelopeKey(f, sp.X), keepShape: false);
                Keying.Key(shin[Channel.Pitch], s, new EnvelopeKey(f, sp.Y), keepShape: false);
                Keying.Key(shin[Channel.Bank], s, new EnvelopeKey(f, 0), keepShape: false);
                if (f >= end) break;
            }
        }

        // The arms swing against the legs: forward as the other side's foot steps.
        foreach (var (arm, sign) in new[] { (c.ArmL, 1.0), (c.ArmR, -1.0) })
        {
            if (arm == null) continue;
            var pitch = arm[Channel.Pitch];
            Clear(pitch);
            double rest = arm.RestRotation.Y;
            for (double f = start; f <= end + 1e-9; f += Math.Max(1, period / 2))
            {
                double phase = (f - start) / period * Math.PI;
                double swing = f >= end - 1e-9 ? 0 : 22 * Math.Sin(phase) * sign;
                Keying.Key(pitch, s, new EnvelopeKey(Math.Min(f, end), rest - swing), keepShape: false);
            }
            Keying.Key(pitch, s, new EnvelopeKey(end, rest), keepShape: false);
        }
        if (end > s.LastFrame) s.LastFrame = (int)Math.Ceiling(end);
        s.Notify();
        return new Walked(start, end, steps, total);
    }
}
