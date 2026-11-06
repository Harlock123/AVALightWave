using System.Globalization;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;
using MoonSharp.Interpreter;

namespace AVALightWave;

/// <summary>
/// Lua macros in Layout, on AVA Modeler's macro engine and in its sandbox (<see cref="LuaSandbox"/>: no os, io or
/// require; a time limit): functions for the scene, its items, their keys and channels, and rendering. A whole macro is
/// one undo step ("Macro: name"), and one that fails leaves the scene as it was. Items are named as in the scene; frames
/// are the scene's; colours are {r, g, b} from 0 to 255, as in the panels. Relative paths start from the macro's folder.
/// <code>
/// local ball = addobject("objects/ball.lwo")
/// for f = 1, 60, 10 do key(ball, "position.y", f, math.abs(math.sin(f / 10)) * 2) end
/// clone(ball, 3, {1.5, 0, 0}, 5)
/// </code>
/// </summary>
public static class LayoutMacros
{
    /// <summary>A macro may run this long before it is stopped (as AVA Modeler's agents' macros).</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);

    /// <summary>The functions a macro can call, one line each, for the Macro Reference.</summary>
    public static readonly (string Signature, string Does)[] Reference =
    [
        ("items([kind])", "the names of the scene's items, in order (kind: \"object\", \"null\", \"light\", \"camera\", \"bone\")"),
        ("kind(item)", "an item's kind"),
        ("frame() / frame(n)", "the current frame, or go to frame n"),
        ("frames() / frames(first, last [, fps])", "the scene's first and last frames (and frames per second), or set them"),
        ("addobject(path [, name])", "loads an object file (.lwo, .obj, .geo, .glb, .gltf, .stl); returns its name"),
        ("addnull(name) / addcamera(name)", "adds a null or a camera; returns its name"),
        ("addlight(name [, type])", "adds a light: \"distant\" (default), \"point\", \"spot\" or \"area\"; returns its name"),
        ("remove(item)", "takes an item out of the scene (an object takes its bones)"),
        ("select(item) / selected()", "chooses an item, or gives the chosen one's name"),
        ("rename(item, name) / parent(item, parent)", "renames an item, or parents it (nil for none)"),
        ("setpos(item, x, y, z [, frame]) / setrot(item, h, p, b [, frame]) / setscale(item, x, y, z [, frame])", "keys position, rotation or scale at a frame (default: the current one), by Layout's keying rule"),
        ("getpos(item [, frame]) / getrot(item [, frame]) / getscale(item [, frame])", "its position, rotation or scale there, as it moves"),
        ("key(item, channel, frame, value [, {Tension=, Continuity=, Bias=, Span=}])", "makes a key on a channel (\"position.x\", \"heading\", \"intensity\", \"zoom\", \"dissolve\", \"morph.Smile\", \"flare.intensity\" …); Span is \"tcb\", \"linear\", \"stepped\" or \"bezier\""),
        ("unkey(item, channel, frame)", "removes a key (a channel's last stays)"),
        ("get(item, channel [, frame])", "a channel's value"),
        ("channels(item)", "the names of an item's channels"),
        ("light(item, {Color={r,g,b}, Intensity=, Cone=, Edge=, Range=, Shadows=})", "changes a light's settings (Intensity throughout; key \"intensity\" to animate)"),
        ("clone(item, count [, {dx, dy, dz} [, frames]])", "Clone Item: copies each moved and delayed from the one before; returns their names"),
        ("render(path [, width, height])", "ray-traces the current frame through the current camera and saves it as a PNG"),
        ("print(...)", "writes to the macro's output"),
    ];

    /// <summary>
    /// Runs a macro on the session's scene as one undo step named after it; what it prints goes to <paramref name="print"/>.
    /// Errors come back as <see cref="MacroException"/>, the scene left as it was.
    /// </summary>
    /// <remarks><paramref name="guard"/>, if given, checks every file path a macro gives (after it's made full) and throws for one it may not use (agents' macros stay in the working folder).</remarks>
    public static void Run(LayoutSession session, string code, string name, string? folder = null, Action<string>? print = null, TimeSpan? timeLimit = null,
        Func<string, string>? guard = null)
    {
        session.Edit($"Macro: {name}", () =>
            LuaSandbox.Run(code, lua => Register(lua, session, folder ?? Environment.CurrentDirectory, guard), print, timeLimit ?? TimeLimit));
    }

    /// <summary>Runs a macro file (its folder is where its relative paths start).</summary>
    public static void RunFile(LayoutSession session, string path, Action<string>? print = null, TimeSpan? timeLimit = null) =>
        Run(session, File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), Path.GetDirectoryName(Path.GetFullPath(path)), print, timeLimit);

    /// <summary>The .lua files in a macro folder, by name (the Macros menu lists them).</summary>
    public static IReadOnlyList<string> List(string? folder) =>
        folder != null && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.lua").Order(StringComparer.OrdinalIgnoreCase).ToList() : [];

    /// <summary>The default user macro folder: <c>macros</c> in AVALightWave's settings folder.</summary>
    public static string DefaultFolder => Path.Combine(LightWaveOptions.Folder, "macros");

    private static void Register(Script lua, LayoutSession s, string folder, Func<string, string>? guard)
    {
        var g = lua.Globals;
        Scene Sc() => s.Scene;
        SceneItem Item(string name) => Sc().Find(name) ?? throw new ArgumentException($"There's no item \"{name}\".");
        string Full(string path)
        {
            var full = Path.GetFullPath(Path.Combine(folder, path));
            return guard?.Invoke(full) ?? full;
        }
        double Frame(double? f) => f ?? s.Frame;
        DynValue Triple(Vec3 v) => DynValue.NewTuple(DynValue.NewNumber(v.X), DynValue.NewNumber(v.Y), DynValue.NewNumber(v.Z));
        Envelope Channel(SceneItem item, string channel) =>
            SceneChannels.Find(item, channel) ?? throw new ArgumentException($"{item.Name} has no channel \"{channel}\": it has {string.Join(", ", SceneChannels.Of(item).Select(c => c.Name))}.");
        void Vector(string name, int first, double x, double y, double z, double? f)
        {
            var item = Item(name);
            double frame = Frame(f);
            double[] v = [x, y, z];
            if (v.Any(n => !double.IsFinite(n))) throw new ArgumentException("Numbers, please.");
            for (int i = 0; i < 3; i++) Keying.Key(item.Channels[first + i], Sc(), new EnvelopeKey(frame, v[i]), keepShape: true);
            Sc().Notify();
        }

        g["items"] = (Func<string?, Table>)(kind => LuaSandbox.List(lua, Sc().Items.Where(i => kind == null || i.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)).Select(i => i.Name)));
        g["kind"] = (Func<string, string>)(name => Item(name).Kind.ToLowerInvariant());
        g["frame"] = (Func<double?, double>)(f =>
        {
            if (f is { } n) s.SetFrame(n);
            return s.Frame;
        });
        g["frames"] = (Func<double?, double?, double?, DynValue>)((first, last, fps) =>
        {
            var sc = Sc();
            if (first is { } a && last is { } b)
            {
                if (b <= a) throw new ArgumentException("The last frame must be after the first.");
                (sc.FirstFrame, sc.LastFrame) = ((int)a, (int)b);
                if (fps is { } r)
                {
                    if (!(r > 0)) throw new ArgumentException("Frames per second must be more than 0.");
                    sc.FramesPerSecond = r;
                }
                sc.Notify();
            }
            return DynValue.NewTuple(DynValue.NewNumber(sc.FirstFrame), DynValue.NewNumber(sc.LastFrame), DynValue.NewNumber(sc.FramesPerSecond));
        });
        g["addobject"] = (Func<string, string?, string>)((path, name) =>
        {
            var full = Full(path);
            var o = Sc().AddObject(name ?? Path.GetFileNameWithoutExtension(full), ObjectFiles.Load(full));
            o.SourcePath = full;
            return o.Name;
        });
        g["addnull"] = (Func<string, string>)(name => Sc().AddNull(name).Name);
        g["addcamera"] = (Func<string, string>)(name => Sc().AddCamera(name).Name);
        g["addlight"] = (Func<string, string?, string>)((name, type) => Sc().AddLight(name, type == null ? LightType.Distant
            : Enum.TryParse<LightType>(type, true, out var t) && Enum.IsDefined(t) ? t : throw new ArgumentException("A light is \"distant\", \"point\", \"spot\" or \"area\".")).Name);
        g["remove"] = (Action<string>)(name => Sc().Remove(Item(name)));
        g["select"] = (Action<string>)(name => s.Select(Item(name)));
        g["selected"] = (Func<string?>)(() => s.Selected?.Name);
        g["rename"] = (Action<string, string>)((name, to) =>
        {
            if (string.IsNullOrWhiteSpace(to)) throw new ArgumentException("A name can't be empty.");
            Item(name).Name = to;
            Sc().Notify();
        });
        g["parent"] = (Action<string, string?>)((name, parent) =>
        {
            var item = Item(name);
            item.Parent = parent == null ? (item is Bone b ? b.Owner : null) : Item(parent);
        });
        g["setpos"] = (Action<string, double, double, double, double?>)((n, x, y, z, f) => Vector(n, 0, x, y, z, f));
        g["setrot"] = (Action<string, double, double, double, double?>)((n, x, y, z, f) => Vector(n, 3, x, y, z, f));
        g["setscale"] = (Action<string, double, double, double, double?>)((n, x, y, z, f) => Vector(n, 6, x, y, z, f));
        g["getpos"] = (Func<string, double?, DynValue>)((n, f) => Triple(Item(n).MotionPositionAt(Frame(f))));
        g["getrot"] = (Func<string, double?, DynValue>)((n, f) => Triple(Item(n).MotionRotationAt(Frame(f))));
        g["getscale"] = (Func<string, double?, DynValue>)((n, f) => Triple(Item(n).MotionScaleAt(Frame(f))));
        g["key"] = (Action<string, string, double, double, Table?>)((n, channel, frame, value, options) =>
        {
            var item = Item(n);
            var env = Channel(item, channel);
            if (!double.IsFinite(value) || !double.IsFinite(frame)) throw new ArgumentException("A key needs a number frame and value.");
            var key = new EnvelopeKey(frame, value);
            bool shape = false;
            if (options != null)
                foreach (var (k, v) in LuaSandbox.ToArgs(options))
                    switch (k.ToLowerInvariant())
                    {
                        case "tension": key = key with { Tension = Math.Clamp(Num(v, k), -1, 1) }; shape = true; break;
                        case "continuity": key = key with { Continuity = Math.Clamp(Num(v, k), -1, 1) }; shape = true; break;
                        case "bias": key = key with { Bias = Math.Clamp(Num(v, k), -1, 1) }; shape = true; break;
                        case "span":
                            key = key.WithSpan(v is string sp && Enum.TryParse<SpanKind>(sp, true, out var kind) ? kind : throw new ArgumentException("Span is \"tcb\", \"linear\", \"stepped\" or \"bezier\"."));
                            shape = true;
                            break;
                        default: throw new ArgumentException($"key() has no option \"{k}\": it has Tension, Continuity, Bias and Span.");
                    }
            Keying.Key(env, Sc(), key, keepShape: !shape);
            Sc().Notify();
        });
        g["unkey"] = (Action<string, string, double>)((n, channel, frame) =>
        {
            var env = Channel(Item(n), channel);
            if (env.Keys.Count > 1) env.RemoveKeyAt(frame);
        });
        g["get"] = (Func<string, string, double?, double>)((n, channel, f) => Channel(Item(n), channel).Evaluate(Frame(f)));
        g["channels"] = (Func<string, Table>)(n => LuaSandbox.List(lua, SceneChannels.Of(Item(n)).Select(c => c.Name)));
        g["light"] = (Action<string, Table>)((n, options) =>
        {
            if (Item(n) is not LightItem l) throw new ArgumentException($"{n} isn't a light.");
            foreach (var (k, v) in LuaSandbox.ToArgs(options))
                switch (k.ToLowerInvariant())
                {
                    case "color" or "colour":
                        var c = v as double[] ?? throw new ArgumentException("Color is {r, g, b}, 0 to 255.");
                        if (c.Length < 3 || c.Any(x => x < 0 || x > 255)) throw new ArgumentException("Color is {r, g, b}, 0 to 255.");
                        l.Color = new Vec3(c[0], c[1], c[2]) / 255;
                        break;
                    case "intensity": l.Intensity.SetConstant(Num(v, k)); break;
                    case "cone": l.ConeAngle = Math.Clamp(Num(v, k), 0.1, 90); break;
                    case "edge": l.EdgeAngle = Math.Max(0, Num(v, k)); break;
                    case "range": l.Range = Math.Max(0, Num(v, k)); break;
                    case "shadows": l.Shadows = v is true; break;
                    default: throw new ArgumentException($"light() has no option \"{k}\": it has Color, Intensity, Cone, Edge, Range and Shadows.");
                }
            Sc().Notify();
        });
        g["clone"] = (Func<string, int, Table?, double?, Table>)((n, count, offset, frames) =>
        {
            var o = offset == null ? Vec3.Zero : LuaSandbox.Numbers(offset) is { Length: >= 3 } a ? new Vec3(a[0], a[1], a[2]) : throw new ArgumentException("The offset is {dx, dy, dz}.");
            var copies = Cloning.Clone(s, Item(n), new CloneOptions { Count = count, Position = o, TimeOffset = frames ?? 0 });
            return LuaSandbox.List(lua, copies.Select(c => c.Name));
        });
        g["render"] = (Action<string, int?, int?>)((path, width, height) =>
        {
            var full = Full(path);
            if (!full.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("render() saves a .png.");
            var image = RayTracer.Render(Sc(), s.Frame, new RenderSettings { Width = width, Height = height, SamplesPerPixel = 2 });
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            image.SavePng(full, alpha: false);
        });
    }

    private static double Num(object? v, string what) => v is double d && double.IsFinite(d) ? d : throw new ArgumentException($"{what} must be a number.");

    /// <summary>The Macro Reference as text.</summary>
    public static string ReferenceText => string.Join("\n", Reference.Select(r => $"{r.Signature}\n    {r.Does}")) +
        "\n\nA macro is one undo step; if it stops with an error, the scene is left as it was. It runs in a sandbox (no os, io or require) and is stopped after " +
        TimeLimit.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds.";
}
