using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVALightWave.Mcp;

/// <summary>The window's shot wizards.</summary>
public enum WizardLook { Turntable, ProductShot }

/// <summary>What a wizard is asked for: the object, the look, how many frames, and (for a turntable) how many turns.</summary>
public sealed record WizardChoice(string Object, WizardLook Look, int Frames = 96, double Turns = 1);

/// <summary>
/// Shot wizards for people (milestone A, stage 3): a turntable or a product shot of an object in the scene, built as
/// one undo step through the same plans build_scene runs. No agent is needed.
/// </summary>
public static class ShotWizards
{
    /// <summary>Builds the shot as one undo step; returns build_scene's result (with its check).</summary>
    public static JsonObject Run(Commands c, LayoutSession l, string root, WizardChoice w) =>
        l.Edit(w.Look == WizardLook.Turntable ? "Turntable" : "Product Shot", () => LightWaveTools.RunPlan(c, l, root, null, Plan(l, w)));

    /// <summary>The plan for a wizard's shot of an object in the scene.</summary>
    public static JsonObject Plan(LayoutSession l, WizardChoice w)
    {
        var s = l.Scene;
        if (w.Frames < 12 || w.Frames > 3000) throw new ArgumentException("Frames must be from 12 to 3000.");
        if (!(w.Turns > 0) || w.Turns > 50) throw new ArgumentException("Turns must be more than 0 and at most 50.");
        var obj = LightWaveTools.ItemByName(s, w.Object) as ObjectItem ?? throw new ArgumentException($"{w.Object} isn't an object.");
        if (obj.BaseMesh().Points.Count == 0) throw new ArgumentException($"{obj.Name} has no points to show.");
        double first = s.FirstFrame;
        int last = s.FirstFrame + w.Frames - 1;
        var box = Shot.Bounds(obj, first);
        var c = (box.Min + box.Max) * 0.5;
        var size = box.Max - box.Min;
        double reach = Math.Max(size.X, size.Z);
        bool still = obj.Parent == null && obj.Channels.Take(3).All(e => !Keying.IsAnimated(e));
        var steps = new JsonArray();
        var motion = new JsonArray();
        var objects = new JsonArray();
        var frame = new JsonArray(obj.Name);
        JsonArray V(double x, double y, double z) => new(Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6));
        string ground = Free(s, "Ground");
        objects.Add(new JsonObject
        {
            ["shape"] = "ground", ["name"] = ground, ["size"] = new JsonArray(Math.Round(Math.Max(20, reach * 30), 3), Math.Round(Math.Max(20, reach * 30), 3)),
            ["color"] = w.Look == WizardLook.Turntable ? new JsonArray(70, 72, 80) : new JsonArray(36, 36, 42),
            ["position"] = V(c.X, w.Look == WizardLook.Turntable ? box.Min.Y : 0, c.Z),
        });
        if (w.Look == WizardLook.Turntable)
        {
            if (still)
            {
                // A null at the object's middle (on its base), the object parented to it in place: the null turns.
                string table = Free(s, "Turntable");
                var p = obj.PositionAt(first);
                steps.Add(Call("add_null", new JsonObject { ["name"] = table, ["position"] = V(c.X, box.Min.Y, c.Z) }));
                steps.Add(Call("set_item", new JsonObject { ["item"] = obj.Name, ["parent"] = table }));
                steps.Add(Call("set_transform", new JsonObject { ["item"] = obj.Name, ["position"] = V(p.X - c.X, p.Y - box.Min.Y, p.Z - c.Z), ["key"] = false }));
                motion.Add(new JsonObject { ["item"] = table, ["kind"] = "spin", ["turns"] = w.Turns, ["start"] = first, ["end"] = last, ["ease"] = "linear" });
            }
            else motion.Add(new JsonObject { ["item"] = obj.Name, ["kind"] = "spin", ["turns"] = w.Turns, ["start"] = first, ["end"] = last, ["ease"] = "linear" });
            return new JsonObject
            {
                ["settings"] = new JsonObject { ["first_frame"] = (int)first, ["last_frame"] = last, ["gradient"] = true, ["gradient_top"] = new JsonArray(60, 66, 80), ["gradient_bottom"] = new JsonArray(14, 14, 18) },
                ["objects"] = objects, ["lights"] = "three_point", ["steps"] = steps, ["motion"] = motion,
                ["camera"] = new JsonObject { ["items"] = frame, ["from"] = "three_quarter", ["margin"] = 0.3, ["start"] = first, ["end"] = last },
            };
        }
        if (!still) throw new ArgumentException($"{obj.Name} moves (or has a parent): the product shot stands it on a pedestal, so choose an object that stays put, or use the turntable.");
        // A pedestal under it, the object raised onto it, studio light, a slow quarter turn and a push in.
        string pedestal = Free(s, "Pedestal");
        double height = Math.Max(0.15, size.Y * 0.8);
        objects.Add(new JsonObject
        {
            ["shape"] = "cylinder", ["name"] = pedestal, ["radius"] = Math.Round(Math.Max(0.1, reach * 0.75), 4), ["height"] = Math.Round(height, 4),
            ["color"] = new JsonArray(232, 232, 236), ["specular"] = 0.2, ["position"] = V(c.X, 0, c.Z),
        });
        var at = obj.PositionAt(first);
        steps.Add(Call("set_transform", new JsonObject { ["item"] = obj.Name, ["position"] = V(at.X, at.Y + height - box.Min.Y, at.Z), ["key"] = false }));
        motion.Add(new JsonObject { ["item"] = obj.Name, ["kind"] = "spin", ["turns"] = 0.25, ["start"] = first, ["end"] = last, ["ease"] = "ease_in_out" });
        frame.Add(pedestal);
        return new JsonObject
        {
            ["settings"] = new JsonObject { ["first_frame"] = (int)first, ["last_frame"] = last },
            ["objects"] = objects, ["lights"] = "studio", ["steps"] = steps, ["motion"] = motion,
            ["camera"] = new JsonObject { ["items"] = frame, ["from"] = "three_quarter", ["margin"] = 0.4, ["frame"] = first },
            ["camera_moves"] = new JsonArray(new JsonObject { ["kind"] = "push_in", ["target"] = obj.Name, ["start"] = first, ["end"] = last, ["ease"] = "ease_out" }),
        };
    }

    private static JsonObject Call(string name, JsonObject args) => new() { ["call"] = name, ["args"] = args };

    private static string Free(AVAModeler.Scene.Scene s, string name) => AVALightWave.Cloning.UniqueName(s, name);
}
