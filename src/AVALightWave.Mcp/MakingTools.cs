using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using AVAModeler.Tools;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>
/// Objects made in the scene (the milestone's first stage): ready-made shapes, objects built from the engine's modeling
/// calls on an object of their own, an object's shape changed by the same calls, and its surfaces' looks. The modeling
/// is the engine's (<see cref="ModelerTools"/> on a scratch document, called through <see cref="EngineCaller"/>); the
/// result goes into the scene as one undo step, and a failure anywhere changes nothing.
/// </summary>
public sealed partial class LightWaveTools
{
    /// <summary>The engine's modeling calls a build step can make: everything that shapes or surfaces an object, nothing that opens, saves or renders.</summary>
    public static readonly IReadOnlyList<string> ModelingSteps =
    [
        "run_tool", "add_points", "add_polygons", "set_layers", "set_selection_mode", "select_points", "select_polygons", "select_box",
        "select_all", "select_none", "select_invert", "select_connected", "select_edges", "select_loop", "select_ring",
        "set_surface", "set_surface_nodes", "set_texture", "set_procedural", "remove_textures", "make_uvs", "set_uv_map", "run_lua",
        "get_document", "check_mesh",
    ];

    /// <summary>The shapes make_object makes.</summary>
    public static readonly IReadOnlyList<string> Shapes = ["box", "ball", "cylinder", "cone", "disc", "torus", "ground", "steps", "text"];

    /// <summary>The most steps one build takes.</summary>
    public const int MaxSteps = 500;

    [McpServerTool(Name = "list_modeling_tools", ReadOnly = true, Idempotent = true)]
    [Description("Lists the engine's modeling tools (AVA Modeler's: Box, Ball, Disc, Cone, Text, Lathe, Extrude, Bevel, Boolean, Array, Bend, Twist…), each with its tab and a one-line summary, for run_tool steps in build_object and edit_object. describe_modeling_tool gives one's parameters.")]
    public Task<string> ListModelingTools(CancellationToken cancel = default) =>
        Task.FromResult(new JsonObject
        {
            ["tools"] = ToolCatalog.List(ToolRegistry.Default)["tools"]!.DeepClone(),
            ["steps"] = new JsonArray([.. ModelingSteps.Select(s => (JsonNode)s)]),
            ["next"] = "describe_modeling_tool gives a tool's parameters; build_object runs steps such as {\"tool\": \"Box\", \"params\": {\"Size\": [1, 2, 1]}} on an object of its own and puts it in the scene.",
        }.ToJsonString());

    [McpServerTool(Name = "describe_modeling_tool", ReadOnly = true, Idempotent = true)]
    [Description("Describes one of the engine's modeling tools: what it does and each parameter's name, type, default, unit (metres or degrees), limits or choices, with an example step. Vectors are [x, y, z].")]
    public Task<string> DescribeModelingTool(
        [Description("The tool's name as list_modeling_tools gives it, e.g. \"Lathe\" (any case).")] string name,
        CancellationToken cancel = default)
    {
        try
        {
            var d = ToolCatalog.Describe(ToolRegistry.Default, name);
            d["example"] = new JsonObject { ["tool"] = (string?)d["name"], ["params"] = d["example"]!["params"]!.DeepClone() };
            return Task.FromResult(d.ToJsonString());
        }
        catch (ArgumentException e)
        {
            throw new McpException(e.Message.Replace("list_tools", "list_modeling_tools", StringComparison.Ordinal), e);
        }
    }

    [McpServerTool(Name = "make_object", Destructive = true)]
    [Description("Makes a ready-made shape as a new object in the scene, with a surface of its own (named after the object): box (size [w, h, d]), ball (radius, or size [w, h, d] for an egg), cylinder and cone (radius, height; a cone narrows upward), disc (a flat round, radius, facing up), torus (radius to the tube's middle, thickness the tube's radius), ground (a floor slab, size [w, d], its top at y = 0), steps (count steps, size [width, rise, run] each, climbing toward +Z) and text (text, height the font size, depth; reading in the camera's default view). origin base (the default) puts its own origin at the middle of its bottom, so at position [x, 0, z] it stands on the ground and scaling squashes it onto the floor; center at its middle. One undo step. Returns the item, its size and its surface. For any other shape use build_object.")]
    public Task<string> MakeObject(
        [Description("box, ball, cylinder, cone, disc, torus, ground, steps or text.")] string shape,
        [Description("The item's name (default: the shape's, e.g. \"Ball\"). Made unique if taken.")] string? name = null,
        [Description("box, ball (an egg), ground ([w, d]) and steps ([width, rise, run] of each step): metres.")] double[]? size = null,
        [Description("ball, cylinder, cone, disc and torus: metres (default 0.5).")] double? radius = null,
        [Description("cylinder and cone: metres (default 1); text: the font size (default 1).")] double? height = null,
        [Description("torus: the tube's radius, metres (default radius / 4).")] double? thickness = null,
        [Description("text: how deep the letters are, metres (default height / 5).")] double? depth = null,
        [Description("steps: how many (default 3).")] int? count = null,
        [Description("text: the words.")] string? text = null,
        [Description("Round shapes: sides around (default 32).")] int? sides = null,
        [Description("[r, g, b], 0 to 255 (default light grey).")] double[]? color = null,
        [Description("Highlights, 0 to 1 (default 0.3; 0 for ground).")] double? specular = null,
        [Description("Highlight tightness: 16 low, 64 medium, 256 high, 1024 very high (default 64).")] double? glossiness = null,
        [Description("Mirror reflection, 0 to 1.")] double? reflection = null,
        [Description("0 opaque to 1 clear.")] double? transparency = null,
        [Description("Self-illumination, 0 to 1 (a glowing object).")] double? luminosity = null,
        [Description("Smooth shading (default: true for ball, cylinder, cone and torus).")] bool? smooth = null,
        [Description("base (default) or center: where the object's own origin is.")] string origin = "base",
        [Description("Where it goes [x, y, z], metres (default [0, 0, 0]).")] double[]? position = null,
        [Description("[heading, pitch, bank] degrees.")] double[]? rotation = null,
        [Description("[x, y, z] scale.")] double[]? scale = null,
        [Description("An item to move with, by name.")] string? parent = null,
        CancellationToken cancel = default)
    {
        var kind = (shape ?? "").Trim().ToLowerInvariant();
        if (!Shapes.Contains(kind)) throw new McpException($"shape must be one of {string.Join(", ", Shapes)}, not \"{shape}\". For anything else use build_object.");
        string label = kind == "text" ? "Text" : char.ToUpperInvariant(kind[0]) + kind[1..];
        return Making($"made a {kind}", (c, l, root) =>
        {
            var objName = Cloning.UniqueName(l.Scene, string.IsNullOrWhiteSpace(name) ? label : name.Trim());
            var doc = new Document();
            var modeler = Modeler(root, c.Registry, doc);
            var surface = new JsonObject
            {
                ["name"] = objName, ["color"] = Numbers(color ?? [200, 200, 200]), ["specular"] = specular ?? (kind == "ground" ? 0 : 0.3),
                ["glossiness"] = glossiness ?? 64, ["smoothing"] = smooth ?? kind is "ball" or "cylinder" or "cone" or "torus", ["assign"] = false,
            };
            if (reflection != null) surface["reflection"] = reflection;
            if (transparency != null) surface["transparency"] = transparency;
            if (luminosity != null) surface["luminosity"] = luminosity;
            modeler.Call("set_surface", surface);
            foreach (var step in ShapeSteps(kind, objName, size, radius, height, thickness, depth, count, text, sides))
                modeler.Call(step.Call, step.Args);
            var place = Origin(doc, kind == "ground" ? "as_built" : origin);
            var item = AddToScene(c, l, root, doc, objName, null, position, rotation, scale, parent);
            var result = Built(item, doc, l);
            result["shape"] = kind;
            result["surface"] = objName;
            if (place != null) result["origin"] = place;
            return result;
        }, cancel);
    }

    [McpServerTool(Name = "build_object", Destructive = true)]
    [Description("Builds an object from steps and puts it in the scene as a new item, as one undo step. Each step is one of the engine's modeling calls, made on an empty object of the build's own: {\"tool\": \"Box\", \"params\": {\"Size\": [2, 0.1, 1], \"Center\": [0, 0.75, 0]}} runs a modeling tool (list_modeling_tools, describe_modeling_tool), and {\"call\": \"set_surface\", \"args\": {\"name\": \"Wood\", \"color\": [150, 100, 60]}} makes any other call: run_tool, add_points, add_polygons, set_layers, the select_ calls, set_surface, set_surface_nodes, set_texture, set_procedural, remove_textures, make_uvs, set_uv_map, run_lua, get_document and check_mesh, with the same arguments AVA Modeler's server takes. Tools act on the selection in the foreground layers (everything there when nothing is selected), and new shapes go into the current layer. If a step fails, the error names it and nothing is added. origin as_built (default) keeps the coordinates the steps made; base or center moves the object's own origin to the middle of its bottom or its middle. The object has no file until save_object. Returns the item, its counts, size and surfaces, and each step's short result.")]
    public Task<string> BuildObject(
        [Description("The steps, in order: {\"tool\": name, \"params\": {...}} or {\"call\": name, \"args\": {...}}.")] JsonObject[] steps,
        [Description("The item's name (default \"Object\"). Made unique if taken.")] string? name = null,
        [Description("Just this layer, 1 to 10, of what the steps made (default: every non-empty layer, merged).")] int? layer = null,
        [Description("as_built (default), base or center: where the object's own origin is.")] string origin = "as_built",
        [Description("Where it goes [x, y, z], metres (default [0, 0, 0]).")] double[]? position = null,
        [Description("[heading, pitch, bank] degrees.")] double[]? rotation = null,
        [Description("[x, y, z] scale.")] double[]? scale = null,
        [Description("An item to move with, by name.")] string? parent = null,
        CancellationToken cancel = default) =>
        Making("built an object", (c, l, root) =>
        {
            var objName = Cloning.UniqueName(l.Scene, string.IsNullOrWhiteSpace(name) ? "Object" : name.Trim());
            var doc = new Document();
            var said = RunSteps(Modeler(root, c.Registry, doc), steps);
            if (layer is { } n && (n < 1 || n > doc.Layers.Count)) throw new ArgumentException($"layer must be from 1 to {doc.Layers.Count}.");
            var place = Origin(doc, origin);
            var item = AddToScene(c, l, root, doc, objName, layer - 1, position, rotation, scale, parent);
            var result = Built(item, doc, l);
            if (place != null) result["origin"] = place;
            result["steps"] = said;
            return result;
        }, cancel);

    [McpServerTool(Name = "edit_object", Destructive = true)]
    [Description("Changes an object that's in the scene with modeling steps, as build_object takes them, run on a copy of its object (every layer of it, the foreground as it was left). The copy then takes the old one's place in every item that shows it, keeping their motion, bones and settings; undo_scene puts the old shape back. Points keep their order unless a step adds or removes some, so morphs still fit after tools that only move points. If a step fails, the error names it and nothing changes. The object's file, if it has one, isn't changed: save_object writes it. Returns the item's counts and size and each step's short result.")]
    public Task<string> EditObject(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        [Description("The steps, in order: {\"tool\": name, \"params\": {...}} or {\"call\": name, \"args\": {...}}.")] JsonObject[] steps,
        CancellationToken cancel = default) =>
        Making("changed an object", (c, l, root) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var old = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded, so there's nothing to change.");
            var doc = old.Copy();
            var said = RunSteps(Modeler(root, c.Registry, doc), steps);
            Swap(l, old, doc);
            var result = Built(item, doc, l);
            result["steps"] = said;
            if (item.SourcePath != null) result["file"] = "not saved: save_object writes the change to its file";
            return result;
        }, cancel);

    [McpServerTool(Name = "set_object_surface", Destructive = true)]
    [Description("Changes how one of an object's surfaces looks (get_object_surfaces, or make_object's result, names them), leaving out what you don't give: colour [r, g, b] 0 to 255, diffuse, specular, luminosity, reflection and transparency 0 to 1, glossiness (16 low to 1024 very high), smoothing, double_sided. procedural lays one of LightWave 5's procedural textures on it as well (as set_procedural takes it, e.g. {\"kind\": \"checkerboard\", \"channel\": \"color\", \"color\": [30, 30, 30], \"size\": [0.5, 0.5, 0.5]}). The object gets a changed copy, so undo_scene puts the old look back. One undo step.")]
    public Task<string> SetObjectSurface(
        [Description("The object item, by name or #index.")] string @object,
        [Description("The surface's name (default: the object's only surface, or the one named after it).")] string? surface = null,
        [Description("[r, g, b], 0 to 255.")] double[]? color = null,
        [Description("0 to 1.")] double? diffuse = null,
        [Description("0 to 1.")] double? specular = null,
        [Description("16 low, 64 medium, 256 high, 1024 very high.")] double? glossiness = null,
        [Description("0 to 1.")] double? luminosity = null,
        [Description("0 to 1.")] double? reflection = null,
        [Description("0 opaque to 1 clear.")] double? transparency = null,
        [Description("Smooth shading.")] bool? smoothing = null,
        [Description("Both sides of its faces.")] bool? double_sided = null,
        [Description("A procedural texture, as set_procedural's arguments without surface.")] JsonObject? procedural = null,
        CancellationToken cancel = default) =>
        Making("changed a surface", (c, l, root) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var old = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded.");
            var surf = surface ?? (old.Surfaces.Count == 1 ? old.Surfaces[0].Name
                : old.Surfaces.FirstOrDefault(s => s.Name == item.Name)?.Name ?? UsedSurfaces(old).FirstOrDefault() ?? old.Surfaces[0].Name);
            if (!old.Surfaces.Any(s => s.Name == surf))
                throw new ArgumentException($"{item.Name} has no surface \"{surf}\". Its surfaces are: {string.Join(", ", old.Surfaces.Select(s => s.Name))}.");
            var doc = old.Copy();
            var modeler = Modeler(root, c.Registry, doc);
            var args = new JsonObject { ["name"] = surf, ["assign"] = false };
            if (color != null) args["color"] = Numbers(color);
            if (diffuse != null) args["diffuse"] = diffuse;
            if (specular != null) args["specular"] = specular;
            if (glossiness != null) args["glossiness"] = glossiness;
            if (luminosity != null) args["luminosity"] = luminosity;
            if (reflection != null) args["reflection"] = reflection;
            if (transparency != null) args["transparency"] = transparency;
            if (smoothing != null) args["smoothing"] = smoothing;
            if (double_sided != null) args["double_sided"] = double_sided;
            modeler.Call("set_surface", args);
            if (procedural != null)
            {
                var p = (JsonObject)procedural.DeepClone();
                p["surface"] = surf;
                modeler.Call("set_procedural", p);
            }
            Swap(l, old, doc);
            var s = doc.Surfaces.First(x => x.Name == surf);
            return new JsonObject
            {
                ["object"] = item.Name, ["surface"] = surf, ["color"] = new JsonArray(s.Color.R, s.Color.G, s.Color.B),
                ["diffuse"] = s.Diffuse, ["specular"] = s.Specular, ["glossiness"] = s.Glossiness, ["reflection"] = s.Reflection,
                ["transparency"] = s.Transparency, ["luminosity"] = s.Luminosity, ["smoothing"] = s.Smoothing, ["textures"] = s.Textures.Count,
            };
        }, cancel);

    [McpServerTool(Name = "get_object_surfaces", ReadOnly = true, Idempotent = true)]
    [Description("Lists an object's surfaces and how each looks (colour, diffuse, specular, glossiness, reflection, transparency, luminosity, smoothing, textures) and how many polygons use it.")]
    public Task<string> GetObjectSurfaces(
        [Description("The object item, by name or #index.")] string @object,
        CancellationToken cancel = default) =>
        Making("looked at surfaces", (_, l, _) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var doc = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded.");
            var uses = new int[doc.Surfaces.Count];
            foreach (var layer in doc.Layers)
                foreach (var p in layer.Mesh.Polygons)
                    if (p.Surface >= 0 && p.Surface < uses.Length) uses[p.Surface]++;
            return new JsonObject
            {
                ["object"] = item.Name,
                ["surfaces"] = new JsonArray([.. doc.Surfaces.Select((s, i) => (JsonNode)new JsonObject
                {
                    ["name"] = s.Name, ["polygons"] = uses[i], ["color"] = new JsonArray(s.Color.R, s.Color.G, s.Color.B), ["diffuse"] = s.Diffuse,
                    ["specular"] = s.Specular, ["glossiness"] = s.Glossiness, ["reflection"] = s.Reflection, ["transparency"] = s.Transparency,
                    ["luminosity"] = s.Luminosity, ["smoothing"] = s.Smoothing, ["textures"] = s.Textures.Count,
                })]),
            };
        }, cancel, changes: false);

    // ───────────────────────────── making ─────────────────────────────

    private static IEnumerable<string> UsedSurfaces(Document doc) =>
        doc.Layers.SelectMany(l => l.Mesh.Polygons).Select(p => p.Surface).Where(i => i >= 0 && i < doc.Surfaces.Count).Distinct().Select(i => doc.Surfaces[i].Name);

    /// <summary>The engine's modeling calls on a document of their own.</summary>
    private static EngineCaller Modeler(string root, ToolRegistry registry, Document doc) =>
        new(new ModelerTools(new InlineHost(root, new Commands(doc, registry), null)), ModelingSteps);

    /// <summary>The engine's scene calls on the scene being changed (inside its edit).</summary>
    internal static EngineCaller SceneCalls(string root, Commands c, LayoutSession l, IEnumerable<string> names) =>
        new(new ModelerTools(new InlineHost(root, c, l)) { Profile = LightWaveServer.Profile() }, names);

    /// <summary>Runs build steps; each step's short result, or an error naming the step.</summary>
    internal static JsonArray RunSteps(EngineCaller modeler, JsonObject[]? steps)
    {
        if (steps == null || steps.Length == 0) throw new ArgumentException("Give at least one step, e.g. {\"tool\": \"Box\", \"params\": {\"Size\": [1, 1, 1]}}.");
        if (steps.Length > MaxSteps) throw new ArgumentException($"At most {MaxSteps} steps in one call.");
        var said = new JsonArray();
        for (int i = 0; i < steps.Length; i++)
        {
            var (call, args) = Step(steps[i], i);
            try
            {
                said.Add(Short(call, EngineCaller.AsObject(modeler.Call(call, args))));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new ArgumentException($"Step {i + 1} ({Name(call, args)}) failed: {e.Message} Nothing was changed.", e);
            }
        }
        return said;
    }

    private static (string Call, JsonObject? Args) Step(JsonObject step, int i)
    {
        if (step["tool"] is JsonValue tool && tool.TryGetValue(out string? toolName))
        {
            if (step.Any(kv => kv.Key is not ("tool" or "params")))
                throw new ArgumentException($"Step {i + 1}: a tool step has \"tool\" and \"params\" only.");
            var args = new JsonObject { ["name"] = toolName };
            if (step["params"] is { } p) args["params"] = p.DeepClone();
            return ("run_tool", args);
        }
        if (step["call"] is JsonValue call && call.TryGetValue(out string? callName))
        {
            if (step.Any(kv => kv.Key is not ("call" or "args")))
                throw new ArgumentException($"Step {i + 1}: a call step has \"call\" and \"args\" only.");
            return (callName, step["args"] is JsonObject a ? (JsonObject)a.DeepClone() : step["args"] == null ? null
                : throw new ArgumentException($"Step {i + 1}: args must be an object."));
        }
        throw new ArgumentException($"Step {i + 1} needs \"tool\" (a modeling tool, with \"params\") or \"call\" (with \"args\"): {step.ToJsonString()}.");
    }

    private static string Name(string call, JsonObject? args) => call == "run_tool" && args?["name"] is JsonValue v ? v.ToString() : call;

    /// <summary>A step's result cut down to what's worth reading back.</summary>
    private static JsonNode Short(string call, JsonObject r)
    {
        var o = new JsonObject { ["step"] = call };
        foreach (var key in new[] { "tool", "points", "polygons", "surface", "selected", "closed", "printable", "problems", "result", "layer" })
            if (r[key] is { } v) o[key] = v.DeepClone();
        return o;
    }

    /// <summary>Moves a document's own origin: base (the middle of its bottom) or center; as_built leaves it. Returns the move, or null.</summary>
    private static JsonArray? Origin(Document doc, string origin)
    {
        var how = (origin ?? "as_built").Trim().ToLowerInvariant();
        if (how is not ("as_built" or "base" or "center"))
            throw new ArgumentException($"origin must be as_built, base or center, not \"{origin}\".");
        var points = doc.Layers.SelectMany(l => l.Mesh.Points).ToList();
        if (points.Count == 0) throw new ArgumentException("The steps made nothing: the object has no points.");
        if (how == "as_built") return null;
        var box = Box3.Of(points);
        var c = (box.Min + box.Max) * 0.5;
        var move = new Vec3(-c.X, how == "base" ? -box.Min.Y : -c.Y, -c.Z);
        foreach (var layer in doc.Layers)
            for (int i = 0; i < layer.Mesh.Points.Count; i++) layer.Mesh.Points[i] += move;
        return Vec(move);
    }

    /// <summary>Puts a made document in the scene as a new object, placed, as get_scene would place it.</summary>
    private static ObjectItem AddToScene(Commands c, LayoutSession l, string root, Document doc, string name, int? layer, double[]? position, double[]? rotation, double[]? scale, string? parent)
    {
        doc.Modified = true;
        var item = l.Scene.AddObject(name, doc, layer);
        if (position != null || rotation != null || scale != null)
        {
            var place = new JsonObject { ["item"] = item.Name, ["key"] = false };
            if (position != null) place["position"] = Numbers(position);
            if (rotation != null) place["rotation"] = Numbers(rotation);
            if (scale != null) place["scale"] = Numbers(scale);
            SceneCalls(root, c, l, ["set_transform"]).Call("set_transform", place);
        }
        if (parent != null) SceneCalls(root, c, l, ["set_item"]).Call("set_item", new JsonObject { ["item"] = item.Name, ["parent"] = parent });
        return item;
    }

    /// <summary>Puts a changed copy of a document in place of the old in every item that shows it.</summary>
    private static void Swap(LayoutSession l, Document old, Document doc)
    {
        doc.Modified = true;
        foreach (var o in l.Scene.Objects.Where(o => ReferenceEquals(o.Document, old))) o.Document = doc;
        l.Scene.Notify();
    }

    private static JsonObject Built(ObjectItem item, Document doc, LayoutSession l)
    {
        var mesh = item.BaseMesh();
        var box = mesh.Points.Count > 0 ? Box3.Of(mesh.Points) : Box3.Empty;
        var r = new JsonObject
        {
            ["name"] = item.Name, ["points"] = mesh.Points.Count, ["polygons"] = mesh.Polygons.Count,
            ["size"] = mesh.Points.Count > 0 ? Vec(box.Max - box.Min) : null,
            ["own_bounds"] = mesh.Points.Count > 0 ? new JsonArray(Vec(box.Min), Vec(box.Max)) : null,
            ["position"] = Vec(item.PositionAt(l.Frame)),
            ["surfaces"] = new JsonArray([.. UsedSurfaces(doc).Select(s => (JsonNode)s)]),
        };
        if (item.Parent != null) r["parent"] = item.Parent.Name;
        return r;
    }

    private static JsonArray Numbers(double[] v) => new([.. v.Select(x => (JsonNode)x)]);

    private static JsonObject Tool(string name, object parameters) => new() { ["name"] = name, ["params"] = JsonSerializer(parameters) };

    private static JsonNode JsonSerializer(object o) => System.Text.Json.JsonSerializer.SerializeToNode(o)!;

    private static JsonArray V(double x, double y, double z) => new(x, y, z);

    /// <summary>The steps that make one of make_object's shapes, on the surface named after the object.</summary>
    private static IEnumerable<(string Call, JsonObject Args)> ShapeSteps(string kind, string surface, double[]? size, double? radius, double? height,
        double? thickness, double? depth, int? count, string? text, int? sides)
    {
        double Positive(double? v, double fallback, string what) =>
            v is not { } x ? fallback : x > 0 && double.IsFinite(x) ? x : throw new ArgumentException($"{what} must be more than 0.");
        double[] Size(int n, double[] fallback)
        {
            if (size == null) return fallback;
            if (size.Length != n || size.Any(x => !(x > 0) || !double.IsFinite(x)))
                throw new ArgumentException($"size for {kind} is {n} numbers, each more than 0.");
            return size;
        }
        int round = sides ?? 32;
        if (round < 3 || round > 256) throw new ArgumentException("sides must be from 3 to 256.");
        double r = Positive(radius, 0.5, "radius");
        switch (kind)
        {
            case "box":
            {
                var s = Size(3, [1, 1, 1]);
                yield return ("run_tool", Tool("Box", new Dictionary<string, object> { ["Size"] = s, ["Surface"] = surface }));
                break;
            }
            case "ball":
            {
                var radii = size != null ? Size(3, [1, 1, 1]).Select(x => x / 2).ToArray() : [r, r, r];
                yield return ("run_tool", Tool("Ball", new Dictionary<string, object> { ["Radius"] = radii, ["Sides"] = round, ["Segments"] = Math.Max(2, round / 2), ["Surface"] = surface }));
                break;
            }
            case "cylinder" or "cone":
            {
                double h = Positive(height, 1, "height");
                yield return ("run_tool", Tool(kind == "cone" ? "Cone" : "Disc", new Dictionary<string, object> { ["Radius"] = new[] { r, r, r }, ["Height"] = h, ["Sides"] = round, ["Surface"] = surface }));
                break;
            }
            case "disc":
                yield return ("run_tool", Tool("Disc", new Dictionary<string, object> { ["Radius"] = new[] { r, r, r }, ["Height"] = 0.0, ["Sides"] = round, ["Surface"] = surface }));
                break;
            case "torus":
            {
                double t = Positive(thickness, r / 4, "thickness");
                if (t >= r) throw new ArgumentException("thickness (the tube's radius) must be less than radius.");
                int around = round, tube = Math.Max(3, round / 2);
                var points = new JsonArray();
                for (int i = 0; i < around; i++)
                {
                    double a = 2 * Math.PI * i / around;
                    for (int j = 0; j < tube; j++)
                    {
                        double b = 2 * Math.PI * j / tube, ring = r + t * Math.Cos(b);
                        points.Add(V(R(ring * Math.Cos(a)), R(t * Math.Sin(b)), R(ring * Math.Sin(a))));
                    }
                }
                var polys = new JsonArray();
                for (int i = 0; i < around; i++)
                    for (int j = 0; j < tube; j++)
                    {
                        int P(int a, int b) => (a % around) * tube + b % tube;
                        polys.Add(new JsonArray(P(i, j), P(i + 1, j), P(i + 1, j + 1), P(i, j + 1)));
                    }
                yield return ("add_points", new JsonObject { ["points"] = points });
                yield return ("add_polygons", new JsonObject { ["polygons"] = polys, ["surface"] = surface });
                yield return ("run_tool", Tool("Align", new Dictionary<string, object>()));
                break;
            }
            case "ground":
            {
                var s = Size(2, [10, 10]);
                const double slab = 0.1;
                yield return ("run_tool", Tool("Box", new Dictionary<string, object> { ["Size"] = new[] { s[0], slab, s[1] }, ["Center"] = new[] { 0, -slab / 2, 0 }, ["Surface"] = surface }));
                break;
            }
            case "steps":
            {
                var s = Size(3, [1.5, 0.2, 0.3]);
                int n = count ?? 3;
                if (n < 1 || n > 100) throw new ArgumentException("count must be from 1 to 100.");
                for (int i = 0; i < n; i++)
                {
                    double top = s[1] * (i + 1);
                    yield return ("run_tool", Tool("Box", new Dictionary<string, object>
                    {
                        ["Size"] = new[] { s[0], top, s[2] }, ["Center"] = new[] { 0, top / 2, s[2] * (i + 0.5) }, ["Surface"] = surface,
                    }));
                    yield return ("select_none", new JsonObject());
                }
                break;
            }
            case "text":
            {
                if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("text needs the words: give text.");
                double h = Positive(height, 1, "height");
                double d = Positive(depth, h / 5, "depth");
                yield return ("run_tool", Tool("Text", new Dictionary<string, object> { ["Text"] = text, ["Height"] = h, ["Surface"] = surface }));
                yield return ("run_tool", Tool("Extrude", new Dictionary<string, object> { ["Offset"] = new[] { 0, 0, d }, ["Keep the base"] = true }));
                break;
            }
        }
    }

    private static double R(double v) => Math.Round(v, 9);

    /// <summary>Runs making work as Scene does, with the command API as well (for the engine's calls).</summary>
    private async Task<string> Making(string action, Func<Commands, LayoutSession, string, JsonObject> work, CancellationToken cancel, bool changes = true)
    {
        try
        {
            return await host.RunSceneAsync(action, (c, l) =>
            {
                var result = changes ? l.Edit(ModelerTools.AgentStep(action), () => work(c, l, host.Root), apart: true) : work(c, l, host.Root);
                return result.ToJsonString();
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
    }
}
