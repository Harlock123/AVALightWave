using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// The light and object settings classic Layout's Lights and Objects panels had beyond the engine's item panel: a
/// light's shadow type (ray traced or a shadow map with its size and fuzziness), lens flare and volumetric beam, and an
/// object's dissolve. Each change is one undo step through the engine's session helpers.
/// </summary>
public sealed partial class ItemsPanel
{
    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) };

    private static CheckBox Check(string text, bool value, Action<bool> set, string name)
    {
        var c = new CheckBox { Content = text, IsChecked = value, Foreground = Look.Text, FontSize = 11, Name = name };
        c.IsCheckedChanged += (_, _) => set(c.IsChecked == true);
        return c;
    }

    private static Grid Pair(string label, Control input)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90"), Margin = new Thickness(0, 1) };
        g.Children.Add(new TextBlock { Text = label, Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1);
        g.Children.Add(input);
        return g;
    }

    /// <summary>Runs a change, showing why in the status bar if it's refused (and then nothing changed).</summary>
    private void Try(Action change)
    {
        try { change(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Window.SetStatus(e.Message); }
        Reshow();
    }

    private void LightEffects(LightItem l, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        // Shadows: ray traced (the default), a shadow map, or none.
        panel.Children.Add(Section("Shadow type"));
        var type = new ComboBox { ItemsSource = new[] { "Ray trace", "Shadow map", "Off" }, SelectedIndex = !l.Shadows ? 2 : l.ShadowMap.On ? 1 : 0, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "ShadowType" };
        type.SelectionChanged += (_, _) => Try(() => s.SetShadows(l, type.SelectedIndex != 2, l.ShadowMap with { On = type.SelectedIndex == 1 }));
        panel.Children.Add(type);
        if (l.ShadowMap.On && l.Shadows)
        {
            panel.Children.Add(Pair("Map size (pixels)", NumberBox(l.ShadowMap.Size, v => Try(() => s.SetShadows(l, true, l.ShadowMap with { Size = (int)v })), "ShadowMapSize")));
            panel.Children.Add(Pair("Fuzziness (map pixels)", NumberBox(l.ShadowMap.Fuzziness, v => Try(() => s.SetShadows(l, true, l.ShadowMap with { Fuzziness = v })), "ShadowFuzziness")));
            if (l.Type == LightType.Area) panel.Children.Add(Note("Area lights keep ray-traced shadows."));
        }

        // Lens flare.
        panel.Children.Add(Section("Lens flare"));
        var f = l.Flare;
        panel.Children.Add(Check("Lens flare", f.On, on => Try(() => s.SetLensFlare(l, f with { On = on })), "LensFlare"));
        if (f.On)
        {
            panel.Children.Add(Pair("Intensity % (this frame)", NumberBox(l.FlareIntensity.Evaluate(s.Frame) * 100,
                v => Try(() => s.SetFlareIntensity(l, s.Frame, v / 100, s.AutoKey || Keying.IsAnimated(l.FlareIntensity))), "FlareIntensity")));
            panel.Children.Add(Pair("Glow size (of the height)", NumberBox(f.Size, v => Try(() => s.SetLensFlare(l, f with { Size = v })), "FlareSize")));
            panel.Children.Add(Pair("Star streaks", NumberBox(f.Streaks, v => Try(() => s.SetLensFlare(l, f with { Streaks = (int)v })), "FlareStreaks")));
            panel.Children.Add(Check("Halo ring", f.Ring, on => Try(() => s.SetLensFlare(l, f with { Ring = on })), "FlareRing"));
            panel.Children.Add(Check("Fade behind objects", f.FadeBehind, on => Try(() => s.SetLensFlare(l, f with { FadeBehind = on })), "FlareFadeBehind"));
            panel.Children.Add(Check("Fade off screen", f.FadeOffScreen, on => Try(() => s.SetLensFlare(l, f with { FadeOffScreen = on })), "FlareFadeOff"));
            panel.Children.Add(Check("Fade with distance", f.FadeWithDistance, on => Try(() => s.SetLensFlare(l, f with { FadeWithDistance = on })), "FlareFadeDistance"));
        }

        // Volumetric beam (spot and point lights, and distant shafts).
        panel.Children.Add(Section("Volumetric"));
        var vol = l.Volumetric;
        if (l.Type == LightType.Area)
        {
            panel.Children.Add(Note("Area lights have no beam."));
            return;
        }
        panel.Children.Add(Check("Volumetric light (a beam in the air)", vol.On, on => Try(() => s.SetVolumetric(l, vol with { On = on })), "Volumetric"));
        if (vol.On)
        {
            panel.Children.Add(Pair("Intensity", NumberBox(vol.Intensity, v => Try(() => s.SetVolumetric(l, vol with { Intensity = v })), "VolumetricIntensity")));
            panel.Children.Add(Pair("Quality (samples a pixel)", NumberBox(vol.Samples, v => Try(() => s.SetVolumetric(l, vol with { Samples = (int)v })), "VolumetricSamples")));
            panel.Children.Add(Pair("Beam length (m, 0 auto)", NumberBox(vol.Distance, v => Try(() => s.SetVolumetric(l, vol with { Distance = v })), "VolumetricDistance")));
            panel.Children.Add(Check("Shadowed beam (slower)", vol.Shadows, on => Try(() => s.SetVolumetric(l, vol with { Shadows = on })), "VolumetricShadows"));
        }
    }

    /// <summary>Light linking: whether the light lights every object, only the ticked ones, or all but them.</summary>
    private void LightLinking(LightItem l, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        panel.Children.Add(Section("Light linking"));
        var mode = new ComboBox { ItemsSource = new[] { "Lights every object", "Lights only the ticked", "Lights all but the ticked" }, SelectedIndex = (int)l.LinkMode, FontSize = 11, Name = "LightLinkMode" };
        mode.SelectionChanged += (_, _) =>
        {
            if (mode.SelectedIndex < 0 || mode.SelectedIndex == (int)l.LinkMode) return;
            Try(() => s.Edit("Light Linking", () => { l.LinkMode = (LightLinkMode)mode.SelectedIndex; if (l.LinkMode == LightLinkMode.All) l.LinkedObjects = []; s.Scene.Notify(); }));
        };
        panel.Children.Add(mode);
        if (l.LinkMode == LightLinkMode.All) return;
        foreach (var o in s.Scene.Objects.Where(o => o.BaseMesh().Points.Count > 0))
        {
            var obj = o;
            panel.Children.Add(Check(obj.Name, l.LinkedObjects.Contains(obj), on => Try(() => s.Edit("Light Linking", () =>
            {
                l.LinkedObjects = on ? [.. l.LinkedObjects.Where(x => x != obj), obj] : [.. l.LinkedObjects.Where(x => x != obj)];
                s.Scene.Notify();
            })), "Link_" + obj.Name));
        }
        panel.Children.Add(Note("The ray tracer follows it; shadows, flares and beams are as without it, and the views light everything."));
    }

    /// <summary>A camera's bookmarks: save this view under a name, and go back to any saved one (keyed if the camera is animated).</summary>
    private void Bookmarks(CameraItem cam, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        panel.Children.Add(Section("Bookmarks"));
        var name = new TextBox { Name = "BookmarkName", PlaceholderText = "Name", FontSize = 11, MinWidth = 120 };
        var save = Look.MakeButton("Save View", () =>
        {
            var n = string.IsNullOrWhiteSpace(name.Text) ? $"View {CameraBookmarks.List(s.Scene).Count + 1}" : name.Text!;
            Try(() => { CameraBookmarks.Save(s, n, cam, s.Frame); Window.SetStatus($"Saved the view as \"{n}\"."); });
        }, "Keep where this camera is, which way it looks and its zoom, under the name");
        save.Name = "SaveBookmark";
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 2) };
        row.Children.Add(name);
        row.Children.Add(save);
        panel.Children.Add(row);
        foreach (var b in CameraBookmarks.List(s.Scene))
        {
            var go = Look.MakeButton(b.Name, () => Try(() => { CameraBookmarks.Recall(s, b.Name, cam, s.Frame); Window.SetStatus($"{cam.Name} is at \"{b.Name}\"."); }),
                $"Put {cam.Name} at this view (keyed at this frame if it's animated)");
            go.Name = "Bookmark_" + b.Name;
            var remove = Look.MakeButton("×", () => Try(() => CameraBookmarks.Remove(s, b.Name)), $"Remove the bookmark \"{b.Name}\"");
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 1) };
            line.Children.Add(go);
            line.Children.Add(remove);
            panel.Children.Add(line);
        }
    }

    /// <summary>An object's rigid body: on or off, dynamic or static, its shape, mass, bounce and friction, and Bake Simulation.</summary>
    private void RigidBody(ObjectItem o, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        panel.Children.Add(Section("Rigid body"));
        void Set(string label, Func<RigidBodySettings, RigidBodySettings> change) => Try(() =>
        {
            var next = change(o.RigidBody ?? new RigidBodySettings()).Checked();
            s.Edit(label, () => { o.RigidBody = next; s.Scene.Notify(); });
        });
        panel.Children.Add(Check("Rigid body (falls, bounces, stacks)", o.RigidBody != null, on => Try(() =>
            s.Edit(on ? "Make Rigid Body" : "Remove Rigid Body", () => { o.RigidBody = on ? new RigidBodySettings() : null; s.Scene.Notify(); })), "RigidBody"));
        if (o.RigidBody is not { } r) return;
        var kind = new ComboBox { ItemsSource = new[] { "Dynamic (simulated)", "Static (keyed, pushes)" }, SelectedIndex = (int)r.Kind, FontSize = 11, Name = "RigidKind" };
        kind.SelectionChanged += (_, _) => { if (kind.SelectedIndex >= 0 && kind.SelectedIndex != (int)r.Kind) Set("Rigid Body Kind", x => x with { Kind = (RigidBodyKind)kind.SelectedIndex }); };
        panel.Children.Add(Pair("Kind", kind));
        var shape = new ComboBox { ItemsSource = new[] { "Box", "Sphere", "Hull" }, SelectedIndex = (int)r.Shape, FontSize = 11, Name = "RigidShape" };
        shape.SelectionChanged += (_, _) => { if (shape.SelectedIndex >= 0 && shape.SelectedIndex != (int)r.Shape) Set("Rigid Body Shape", x => x with { Shape = (CollisionShape)shape.SelectedIndex }); };
        panel.Children.Add(Pair("Shape", shape));
        if (r.Kind == RigidBodyKind.Dynamic)
        {
            panel.Children.Add(Pair("Mass (kg)", NumberBox(r.Mass, v => Set("Rigid Body Mass", x => x with { Mass = v }), "RigidMass")));
            panel.Children.Add(Pair("Bounce (0–1)", NumberBox(r.Bounce, v => Set("Rigid Body Bounce", x => x with { Bounce = v }), "RigidBounce")));
        }
        panel.Children.Add(Pair("Friction (0–2)", NumberBox(r.Friction, v => Set("Rigid Body Friction", x => x with { Friction = v }), "RigidFriction")));
        if (r.Kind == RigidBodyKind.Dynamic)
        {
            var bake = Look.MakeButton("Bake Simulation", () => Try(() =>
            {
                var bodies = s.Scene.Objects.Where(x => x.RigidBody is { Kind: RigidBodyKind.Dynamic }).ToList();
                s.Edit("Bake Simulation", () => AVALightWave.Mcp.LightWaveTools.BakeBodies(s.Scene, bodies, 1, s.Scene.LastFrame));
                Window.SetStatus($"Baked {bodies.Count} rigid bod{(bodies.Count == 1 ? "y" : "ies")} into keys.");
            }), "Turn every dynamic body's simulated motion into keys (they stop being rigid bodies)");
            bake.Name = "BakeSimulation";
            panel.Children.Add(bake);
        }
    }

    private void Dissolve(ObjectItem o, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        panel.Children.Add(Section("Object dissolve"));
        var box = NumberBox(o.DissolveAt(s.Frame) * 100, v => Try(() => s.SetDissolve(o, Math.Clamp(v, 0, 100) / 100)), "Dissolve");
        ToolTip.SetTip(box, "0 % solid, 100 % gone, at this frame (Auto Key keys it); the channel \"dissolve\"");
        panel.Children.Add(Pair("Dissolve % (this frame)" + (Keying.IsAnimated(o.Dissolve) ? " ◆" : ""), box));
    }
}
