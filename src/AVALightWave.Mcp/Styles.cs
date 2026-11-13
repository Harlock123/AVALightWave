using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>A look: lights, how surfaces are treated, the lens, and the ease moves take unless told otherwise.</summary>
public sealed record StyleLook(string Name, string Rig, string Surfaces, double Zoom, string Ease, string About);

/// <summary>Style presets (milestone A, stage 7): a named look applied to the whole scene in one undo step.</summary>
public sealed partial class LightWaveTools
{
    public const string StyleKey = "avalightwave.style";

    public static readonly IReadOnlyList<StyleLook> Styles =
    [
        new("noir", "noir", "grey", 3.6, "ease_in_out", "black and white: one hard key from high to the side, deep shadows, a longer lens"),
        new("product_commercial", "studio", "glossy", 4.2, "ease_out", "clean studio light, glossy surfaces with a touch of reflection, a long lens, moves that settle"),
        new("cartoon", "cartoon", "flat", 2.6, "overshoot", "bright even light, saturated matte colours, a wide lens, moves that overshoot and settle"),
        new("golden_hour", "golden_hour", "keep", 2.8, "ease_in_out", "a low warm sun raking across under a sunset sky"),
        new("sci_fi", "sci_fi", "sleek", 2.4, "ease_in_out", "cyan and magenta rims in dark teal haze, sleek glossy surfaces, a wide lens"),
    ];

    /// <summary>The style the scene was last given, or null.</summary>
    public static StyleLook? StyleOf(AVAModeler.Scene.Scene s) =>
        s.Extra(StyleKey) is { } text && JsonNode.Parse(text)?["name"]?.GetValue<string>() is { } n ? Styles.FirstOrDefault(x => x.Name == n) : null;

    [McpServerTool(Name = "apply_style", Destructive = true)]
    [Description("Gives the whole scene a look in one undo step: noir (black and white, one hard key, deep shadows, a longer lens), product_commercial (studio light, glossy surfaces, a long lens, moves that settle), cartoon (bright even light, saturated matte colours, a wide lens, moves that overshoot), golden_hour (a low warm sun under a sunset sky) or sci_fi (cyan and magenta rims in teal haze, sleek surfaces). It replaces the scene's lights with the style's (unless lights is false), changes every object's surfaces as the style does (unless surfaces is false; the ground is spared colour changes), sets the cameras' zoom (unless lens is false), and from then on animate's moves take the style's ease when they don't give one. build_scene takes a style too.")]
    public Task<string> ApplyStyle(
        [Description("noir, product_commercial, cartoon, golden_hour or sci_fi.")] string style,
        [Description("Replace the lights with the style's (default true).")] bool lights = true,
        [Description("Treat the objects' surfaces as the style does (default true).")] bool surfaces = true,
        [Description("Set the cameras' zoom to the style's lens (default true).")] bool lens = true,
        CancellationToken cancel = default) =>
        Making("applied a style", (c, l, root) => ApplyStyleTo(c, l, root, style, lights, surfaces, lens), cancel);

    /// <summary>Applies a style inside the caller's edit (for apply_style and build_scene).</summary>
    internal static JsonObject ApplyStyleTo(Commands c, LayoutSession l, string root, string style, bool lights, bool surfaces, bool lens)
    {
        var look = Styles.FirstOrDefault(x => x.Name == (style ?? "").Trim().ToLowerInvariant())
            ?? throw new ArgumentException($"There's no style \"{style}\": they are {string.Join(", ", Styles.Select(x => x.Name))}.");
        var s = l.Scene;
        var calls = SceneCalls(root, c, l, ["add_light", "remove_item", "set_scene", "set_item"]);
        var result = new JsonObject { ["style"] = look.Name, ["about"] = look.About };
        if (lights) result["lights"] = Lights(new JsonObject { ["rig"] = look.Rig }, l, calls);
        if (surfaces)
        {
            int changed = 0;
            var docs = new Dictionary<Document, Document>(ReferenceEqualityComparer.Instance);
            foreach (var o in s.Objects.ToList())
            {
                if (o.Document is not { } doc) continue;
                if (!docs.TryGetValue(doc, out var copy))
                {
                    copy = doc.Copy();
                    foreach (var surface in copy.Surfaces) Treat(surface, look.Surfaces, Shot.IsGround(o));
                    copy.Modified = true;
                    docs[doc] = copy;
                    changed++;
                }
                o.Document = copy;
            }
            result["objects_restyled"] = changed;
        }
        if (lens)
            foreach (var cam in s.Cameras)
                Keying.Set(cam.Zoom, s, l.Frame, look.Zoom, Keying.IsAnimated(cam.Zoom), $"{cam.Name}'s zoom");
        s.SetExtra(StyleKey, new JsonObject { ["name"] = look.Name, ["ease"] = look.Ease }.ToJsonString());
        result["default_ease"] = look.Ease;
        return result;
    }

    /// <summary>A surface as a style treats it.</summary>
    private static void Treat(Surface s, string how, bool ground)
    {
        var c = s.Color.ToVec();
        switch (how)
        {
            case "grey":
            {
                double y = 0.299 * c.X + 0.587 * c.Y + 0.114 * c.Z;
                s.Color = Rgb.FromVec(new Vec3(y, y, y));
                s.Specular = Math.Max(s.Specular, 0.2);
                break;
            }
            case "glossy":
                s.Specular = Math.Max(s.Specular, ground ? 0.2 : 0.6);
                s.Glossiness = Math.Max(s.Glossiness, ground ? 128 : 512);
                if (!ground) s.Reflection = Math.Max(s.Reflection, 0.08);
                break;
            case "flat":
                if (!ground)
                {
                    // More saturated, and matte.
                    double mean = (c.X + c.Y + c.Z) / 3;
                    s.Color = Rgb.FromVec(new Vec3(Math.Clamp(mean + (c.X - mean) * 1.4, 0, 1), Math.Clamp(mean + (c.Y - mean) * 1.4, 0, 1), Math.Clamp(mean + (c.Z - mean) * 1.4, 0, 1)));
                }
                s.Specular = 0;
                s.Reflection = 0;
                s.Luminosity = Math.Max(s.Luminosity, ground ? 0 : 0.12);
                break;
            case "sleek":
                s.Specular = Math.Max(s.Specular, 0.7);
                s.Glossiness = Math.Max(s.Glossiness, 256);
                s.Reflection = Math.Max(s.Reflection, ground ? 0.2 : 0.15);
                break;
        }
        s.Raw = null;
    }
}
