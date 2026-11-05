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
