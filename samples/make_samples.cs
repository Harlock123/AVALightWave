// Makes AVALightWave's sample objects (samples/objects/*.lwo) and scenes (samples/showcase.lws and .avascene) with the
// engine's own tools, so they're the same every time. Run from the repository's root:
//   dotnet run samples/make_samples.cs
#:project ../external/AVAModeler/src/AVAModeler.Tools/AVAModeler.Tools.csproj
#:project ../external/AVAModeler/src/AVAModeler.Formats/AVAModeler.Formats.csproj
#:project ../external/AVAModeler/src/AVAModeler.Scene/AVAModeler.Scene.csproj
#:property TreatWarningsAsErrors=false
#:property PublishAot=false
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Scene;
using AVAModeler.Tools;

var root = Path.Combine(Directory.GetCurrentDirectory(), "samples");
var objects = Path.Combine(root, "objects");
Directory.CreateDirectory(objects);

Commands Fresh() => new(new Document());

void Surface(Commands c, string name, Rgb color, double specular, double gloss, bool smooth, double reflection = 0)
{
    // The new object's one surface, "Default", becomes this one (so no unused surface is saved).
    var s = c.Document.Surfaces.FirstOrDefault(x => x.Name == name) ?? c.Document.Surfaces.FirstOrDefault(x => x.Name == "Default");
    if (s == null) { s = new Surface(name); c.Document.Surfaces.Add(s); }
    s.Name = name;
    s.Color = color;
    s.Specular = specular;
    s.Glossiness = gloss;
    s.Smoothing = smooth;
    s.Reflection = reflection;
}

string Save(Commands c, string name)
{
    var path = Path.Combine(objects, name);
    ObjectFiles.Save(c.Document, path);
    Console.WriteLine($"{path}: {c.Document.Current.Mesh.Points.Count} points, {c.Document.Current.Mesh.Polygons.Count} polygons");
    return path;
}

// A chess pawn, lathed from its profile.
{
    var c = Fresh();
    Surface(c, "Pawn", new Rgb(235, 225, 200), 0.5, 256, true);
    var outline = new List<(double X, double Y)>
    {
        (0, 0), (0.3, 0), (0.3, 0.05), (0.26, 0.08), (0.27, 0.11), (0.2, 0.14), (0.14, 0.2), (0.1, 0.45), (0.17, 0.5), (0.17, 0.53), (0.09, 0.56),
    };
    foreach (double a in new[] { -60.0, -30, 0, 30, 60, 90 })
        outline.Add((Math.Round(0.16 * Math.Cos(a * Math.PI / 180), 4), Math.Round(0.72 + 0.16 * Math.Sin(a * Math.PI / 180), 4)));
    const double height = 1.2;
    double scale = height / outline[^1].Y;
    var points = c.AddPoints(outline.Select(p => new Vec3(p.X * scale, p.Y * scale, 0)));
    c.AddPolygon(points, "Pawn");
    c.Run("Lathe", new Dictionary<string, object?> { ["Axis"] = Axis.Y, ["Sides"] = 32 });
    Save(c, "pawn.lwo");
}

// A crate: a metre box sitting on the floor.
{
    var c = Fresh();
    Surface(c, "Crate", new Rgb(160, 110, 60), 0.15, 32, false);
    c.Run("Box", new Dictionary<string, object?> { ["Center"] = new Vec3(0, 0.5, 0), ["Size"] = new Vec3(1, 1, 1), ["Surface"] = "Crate" });
    Save(c, "crate.lwo");
}

// A glossy red ball, resting on the floor.
{
    var c = Fresh();
    Surface(c, "Ball", new Rgb(200, 30, 40), 0.8, 512, true, 0.1);
    c.Run("Ball", new Dictionary<string, object?> { ["Center"] = new Vec3(0, 0.4, 0), ["Radius"] = new Vec3(0.4, 0.4, 0.4), ["Sides"] = 32, ["Segments"] = 16, ["Surface"] = "Ball" });
    Save(c, "ball.lwo");
}

// The floor: wide and thin, its top at y = 0.
{
    var c = Fresh();
    Surface(c, "Floor", new Rgb(150, 155, 165), 0.1, 16, false);
    // Cut into a metre grid, so views close to it clip small faces rather than one huge one.
    c.Run("Box", new Dictionary<string, object?> { ["Center"] = new Vec3(0, -0.02, 0), ["Size"] = new Vec3(12, 0.04, 12), ["SegmentsX"] = 12, ["SegmentsZ"] = 12, ["Surface"] = "Floor" });
    Save(c, "floor.lwo");
}

// The engine's spaceship, from its sample macro.
{
    var c = Fresh();
    var macro = Path.Combine(Directory.GetCurrentDirectory(), "external", "AVAModeler", "samples", "scripts", "spaceship.lua");
    Macros.Run(File.ReadAllText(macro), c);
    Save(c, "spaceship.lwo");
}

// An arm for Skelegons → Bones: an upper arm and forearm (two boxes) with a chain of skelegons up its middle: 2-point
// polygons on a "Skelegon" surface, as AVA Modeler's skelegon tools leave them.
{
    var c = Fresh();
    Surface(c, "Arm", new Rgb(110, 150, 210), 0.3, 64, false);
    c.Run("Box", new Dictionary<string, object?> { ["Center"] = new Vec3(0, 0.5, 0), ["Size"] = new Vec3(0.24, 1, 0.24), ["SegmentsY"] = 4, ["Surface"] = "Arm" });
    c.Run("Box", new Dictionary<string, object?> { ["Center"] = new Vec3(0, 1.45, 0), ["Size"] = new Vec3(0.2, 0.9, 0.2), ["SegmentsY"] = 4, ["Surface"] = "Arm" });
    var doc = c.Document;
    doc.Surfaces.Add(new Surface("Skelegon") { Color = new Rgb(255, 220, 60) });
    int skelegon = doc.Surfaces.Count - 1;
    var mesh = doc.Current.Mesh;
    int shoulder = mesh.Points.Count;
    mesh.Points.AddRange([new Vec3(0, 0, 0), new Vec3(0, 1, 0), new Vec3(0, 1.9, 0)]);
    mesh.Polygons.Add(new Polygon([shoulder, shoulder + 1], skelegon));
    mesh.Polygons.Add(new Polygon([shoulder + 1, shoulder + 2], skelegon));
    mesh.Touch();
    Save(c, "arm.lwo");
}

// The pawn again, squashed (wider and half as tall) point for point: a morph target for the pawn.
{
    var doc = ObjectFiles.Load(Path.Combine(objects, "pawn.lwo"));
    foreach (var layer in doc.Layers)
    {
        var m = layer.Mesh;
        for (int i = 0; i < m.Points.Count; i++) m.Points[i] = new Vec3(Math.Round(m.Points[i].X * 1.35, 6), Math.Round(m.Points[i].Y * 0.6, 6), Math.Round(m.Points[i].Z * 1.35, 6));
        m.Touch();
    }
    var path = Path.Combine(objects, "pawn-squash.lwo");
    ObjectFiles.Save(doc, path);
    Console.WriteLine($"{path}: the pawn squashed, for morphing");
}

// Images for the Images panel and compositing: a painted sky for the background, a foreground frame with a green key
// colour to cut out, and a short numbered sequence of drifting stripes (stripes0001.png … stripes0012.png) for textures.
{
    var images = Path.Combine(root, "images");
    Directory.CreateDirectory(images);
    void Picture(string name, int w, int h, Func<int, int, (double R, double G, double B, double A)> at)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b, a) = at(x, y);
                int i = (y * w + x) * 4;
                rgba[i] = (byte)Math.Clamp(Math.Round(r * 255), 0, 255);
                rgba[i + 1] = (byte)Math.Clamp(Math.Round(g * 255), 0, 255);
                rgba[i + 2] = (byte)Math.Clamp(Math.Round(b * 255), 0, 255);
                rgba[i + 3] = (byte)Math.Clamp(Math.Round(a * 255), 0, 255);
            }
        File.WriteAllBytes(Path.Combine(images, name), Png.Encode(w, h, rgba));
        Console.WriteLine($"samples/images/{name}");
    }
    // The sky: a dusk gradient with a low sun and rolling hills, 320 × 240.
    Picture("sky.png", 320, 240, (x, y) =>
    {
        double t = y / 239.0;
        double hill = 170 + 18 * Math.Sin(x / 40.0) + 9 * Math.Sin(x / 13.0 + 1);
        if (y > hill) return (0.10 + 0.05 * Math.Sin(x / 9.0), 0.20 + 0.04 * t, 0.12, 1);
        double sun = Math.Max(0, 1 - Math.Sqrt((x - 230) * (x - 230) + (y - 150) * (y - 150)) / 28);
        return (Math.Min(1, 0.25 + 0.75 * t + sun), Math.Min(1, 0.25 + 0.45 * t + sun * 0.8), Math.Min(1, 0.55 - 0.25 * t + sun * 0.4), 1);
    });
    // The foreground: a dark frame like a window's, with pure green (0, 255, 0) in the middle to key out.
    Picture("frame.png", 320, 240, (x, y) =>
    {
        bool inside = x > 24 && x < 296 && y > 20 && y < 220 && !(Math.Abs(x - 160) < 4) && !(Math.Abs(y - 120) < 4);
        return inside ? (0, 1, 0, 1) : (0.16, 0.12, 0.10, 1);
    });
    // The sequence: stripes that drift one twelfth of their spacing each frame, so it loops after 12.
    for (int n = 1; n <= 12; n++)
    {
        int frame = n;
        Picture($"stripes{n:0000}.png", 64, 64, (x, y) =>
        {
            double v = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (x + y + frame * 32.0 / 12) / 32);
            return (0.2 + 0.7 * v, 0.3 + 0.4 * v, 0.9 - 0.5 * v, 1);
        });
    }
}

static void Animate(Envelope e, IEnumerable<EnvelopeKey> keys)
{
    var fresh = new Envelope();
    foreach (var k in keys) fresh.AddKey(k);
    e.CopyFrom(fresh);
}

// The showcase scene: the pawn, the crate and a bouncing ball on the floor, lit by a key spot and a fill, with the
// camera on a slow push in. Its objects are referenced relatively, so the scene works wherever samples/ is.
{
    Document Load(string name) => ObjectFiles.Load(Path.Combine(objects, name));
    var scene = Scene.CreateDefault();
    scene.LastFrame = 60;
    scene.AmbientIntensity = 0.12;
    scene.GradientBackground = true;
    scene.GradientTop = new Vec3(0.18, 0.22, 0.32);
    scene.GradientBottom = new Vec3(0.05, 0.05, 0.07);
    ObjectItem Add(string name, string file, Vec3 at)
    {
        var o = scene.AddObject(name, Load(file));
        o.SourcePath = "objects/" + file;
        for (int i = 0; i < 3; i++) o.Channels[i].SetConstant(at[i]);
        return o;
    }
    Add("Floor", "floor.lwo", Vec3.Zero);
    Add("Pawn", "pawn.lwo", new Vec3(-0.9, 0, 0.3));
    Add("Crate", "crate.lwo", new Vec3(1.0, 0, 0.6));
    var ball = Add("Ball", "ball.lwo", new Vec3(0.1, 0, -0.6));
    // The bounce: up and down twice, squashing a little where it lands.
    Animate(ball[Channel.PositionY], new[] { (1.0, 1.4), (16.0, 0.0), (31.0, 1.0), (46.0, 0.0), (60.0, 0.6) }.Select(k => new EnvelopeKey(k.Item1, k.Item2, Tension: k.Item2 == 0 ? 0 : -0.3)));
    Animate(ball[Channel.ScaleY], new[] { 16.0, 46.0 }.SelectMany(f => new[] { new EnvelopeKey(f - 2, 1), new EnvelopeKey(f, 0.8), new EnvelopeKey(f + 2, 1) }));
    var light = scene.Lights.First();
    light.Name = "Fill";
    light.Intensity.SetConstant(0.35);
    light.Shadows = false;
    for (int i = 0; i < 3; i++) light.Channels[3 + i].SetConstant(new Vec3(-40, 30, 0)[i]);
    var key = scene.Add(new LightItem("Key", LightType.Spot) { ConeAngle = 30, EdgeAngle = 10, Color = new Vec3(1, 0.95, 0.85) });
    for (int i = 0; i < 3; i++) key.Channels[i].SetConstant(new Vec3(-3, 4.5, -3)[i]);
    var aim = key.RotationToward(new Vec3(0, 0.4, 0), 1);
    for (int i = 0; i < 3; i++) key.Channels[3 + i].SetConstant(aim[i]);
    var camera = scene.CurrentCamera!;
    var (from, to) = (new Vec3(0.6, 1.8, -5.5), new Vec3(0.3, 1.4, -4.2));
    for (int i = 0; i < 3; i++) Animate(camera.Channels[i], [new EnvelopeKey(1, from[i]), new EnvelopeKey(60, to[i])]);
    var look1 = camera.RotationToward(new Vec3(0, 0.5, 0), 1);
    var look60 = camera.RotationToward(new Vec3(0, 0.5, 0), 60);
    for (int i = 0; i < 3; i++) Animate(camera.Channels[3 + i], [new EnvelopeKey(1, look1[i]), new EnvelopeKey(60, look60[i])]);
    var warnings = new List<string>();
    LwsWriter.WriteFile(scene, Path.Combine(root, "showcase.lws"), warnings);
    SceneJson.WriteFile(scene, Path.Combine(root, "showcase.avascene"));
    foreach (var w in warnings) Console.WriteLine("lws: " + w);
    Console.WriteLine("Wrote samples/showcase.lws and samples/showcase.avascene.");
}
