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

    private void Dissolve(ObjectItem o, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        panel.Children.Add(Section("Object dissolve"));
        var box = NumberBox(o.DissolveAt(s.Frame) * 100, v => Try(() => s.SetDissolve(o, Math.Clamp(v, 0, 100) / 100)), "Dissolve");
        ToolTip.SetTip(box, "0 % solid, 100 % gone, at this frame (Auto Key keys it); the channel \"dissolve\"");
        panel.Children.Add(Pair("Dissolve % (this frame)" + (Keying.IsAnimated(o.Dissolve) ? " ◆" : ""), box));
    }
}
