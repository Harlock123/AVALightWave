using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AVALightWave.Core.Tests;
using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave.UI.Tests;

/// <summary>The Lights panel's shadow type, lens flare and volumetric settings, an object's dissolve, the Images panel and compositing.</summary>
public class EffectsPanelTests
{
    [AvaloniaFact]
    public void The_lights_panel_sets_shadow_maps_flares_and_beams_as_undo_steps()
    {
        var w = Ui.Open();
        var spot = w.Session.Add(new LightItem("Spot", LightType.Spot));
        w.ShowPanel("Lights");
        Ui.Pump();
        Ui.Find<ComboBox>(w.CurrentPanel!, "ShadowType").SelectedIndex = 1;
        Ui.Pump();
        Assert.True(spot.ShadowMap.On);
        Assert.Equal("Set Spot's Shadows", w.Session.History.UndoLabel);
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "ShadowMapSize"), "1024");
        Assert.Equal(1024, spot.ShadowMap.Size);
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "ShadowMapSize"), "5");
        Assert.Equal(1024, spot.ShadowMap.Size);
        Ui.Find<CheckBox>(w.CurrentPanel!, "LensFlare").IsChecked = true;
        Ui.Pump();
        Assert.True(spot.Flare.On);
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "FlareStreaks"), "6");
        Assert.Equal(6, spot.Flare.Streaks);
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "FlareIntensity"), "50");
        Assert.Equal(0.5, spot.FlareIntensity.Evaluate(w.Session.Frame), 9);
        Ui.Find<CheckBox>(w.CurrentPanel!, "Volumetric").IsChecked = true;
        Ui.Pump();
        Assert.True(spot.Volumetric.On);
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "VolumetricSamples"), "8");
        Assert.Equal(8, spot.Volumetric.Samples);
        w.Undo();
        Assert.Equal(32, spot.Volumetric.Samples);
        Ui.Find<ComboBox>(w.CurrentPanel!, "ShadowType").SelectedIndex = 2;
        Ui.Pump();
        Assert.False(spot.Shadows);
    }

    [AvaloniaFact]
    public void An_objects_dissolve_is_keyed_from_its_panel()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        var ball = w.LoadObject(bench.Object("ball.lwo"))!;
        w.Session.SetFrame(10);
        w.ShowPanel("Objects");
        Ui.Pump();
        Ui.Type(w, Ui.Find<TextBox>(w.CurrentPanel!, "Dissolve"), "40");
        Assert.Equal(0.4, ball.DissolveAt(10), 9);
        Assert.Equal(0.4, w.View.CurrentView().Scene.Layers.Single().Dissolve, 9);
    }

    [AvaloniaFact]
    public void Images_load_into_the_list_and_composite_behind_and_over_the_render()
    {
        using var bench = new Workbench();
        var images = Path.Combine(Samples.Folder, "images");
        var w = Ui.Open();
        w.ShowPanel("Images");
        var panel = (ImagesPanel)w.CurrentPanel!;
        Assert.True(panel.Add(Path.Combine(images, "sky.png"), sequence: false));
        Assert.True(panel.Add(Path.Combine(images, "stripes0003.png"), sequence: true));
        Assert.False(panel.Add(Path.Combine(images, "sky.png"), sequence: false));
        Assert.Equal(2, w.Session.Scene.Images.Count);
        Assert.True(w.Session.Scene.Images[1].Sequence);
        Ui.Type(w, Ui.Find<TextBox>(panel, "ImageOffset"), "4");
        Assert.Equal(4, w.Session.Scene.Images[1].Offset);

        w.ShowPanel("Effects");
        Ui.Pump();
        Ui.Find<ComboBox>(w.CurrentPanel!, "CompositingBackground").SelectedIndex = 1;
        Ui.Pump();
        Assert.EndsWith("sky.png", w.Session.Scene.Compositing.Background);
        // A frame of nothing but the background shows the sky picture, not the black backdrop.
        var cam = w.Session.Scene.CurrentCamera!;
        var image = RayTracer.Render(w.Session.Scene, 1, new RenderSettings { Width = 32, Height = 24 });
        var (r, g, b, _) = image.Pixel(16, 2);
        Assert.True(r + g + b > 120, $"the top of the sky, got {r},{g},{b}");
        // Compositing uses it, so it can't be removed; undo takes compositing off and then it can.
        w.ShowPanel("Images");
        panel = (ImagesPanel)w.CurrentPanel!;
        Ui.Find<ListBox>(panel, "ImageList").SelectedIndex = 0;
        Assert.False(panel.Remove());
        w.Undo();
        Assert.Equal("", w.Session.Scene.Compositing.Background);
        w.ShowPanel("Images");
        Ui.Find<ListBox>(w.CurrentPanel!, "ImageList").SelectedIndex = 0;
        Assert.True(((ImagesPanel)w.CurrentPanel!).Remove());
        Assert.Single(w.Session.Scene.Images);
        Assert.NotNull(cam);
    }
}
