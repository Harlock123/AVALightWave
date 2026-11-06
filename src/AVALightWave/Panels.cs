using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Core;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// One of the panels the buttons across the top open on the right (classic Layout's Scene, Objects, Bones, Lights,
/// Camera, Surfaces, Effects, Render and Options panels, re-imagined as docked panels rather than requesters).
/// </summary>
public abstract class LayoutPanel : StackPanel
{
    protected LayoutPanel(MainWindow window, string title)
    {
        Window = window;
        Title = title;
        Spacing = 4;
        Margin = new Thickness(8);
    }

    public MainWindow Window { get; }
    public string Title { get; }
    protected SceneDocument Doc => Window.Document;

    /// <summary>Shows the scene afresh (called when the panel opens, and on changes while it's open).</summary>
    public abstract void Refresh();

    protected static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.Bold, Foreground = Look.Text, FontSize = 14, Margin = new Thickness(0, 0, 0, 4) };
    protected static TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    internal static TextBlock Note(string text) => new() { Text = text, Foreground = Look.Text, Opacity = 0.75, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    protected static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    protected static WrapPanel Buttons(params Button[] buttons)
    {
        var w = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 3, 3); w.Children.Add(b); }
        return w;
    }

    /// <summary>A number box that applies its value on Enter or on leaving it, only when it changed.</summary>
    internal static TextBox NumberBox(double value, Action<double> set, string? name = null)
    {
        var box = new TextBox { Text = F(value), FontSize = 11, Name = name };
        var applied = box.Text;
        void Apply()
        {
            if (box.Text == applied || !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v)) return;
            applied = box.Text;
            set(v);
        }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        return box;
    }
}

/// <summary>The Scene panel: the frames, the rate, what's in the scene, and the scene's files.</summary>
public sealed class ScenePanel : LayoutPanel
{
    public ScenePanel(MainWindow window) : base(window, "Scene") { }

    public override void Refresh()
    {
        Children.Clear();
        var s = Doc.Scene;
        Children.Add(Heading("Scene"));
        Children.Add(Note(Doc.Path ?? "Not saved yet."));
        Children.Add(Label("First frame"));
        Children.Add(NumberBox(s.FirstFrame, v => Frames((int)v, s.LastFrame), "FirstFrame"));
        Children.Add(Label("Last frame"));
        Children.Add(NumberBox(s.LastFrame, v => Frames(s.FirstFrame, (int)v), "LastFrame"));
        Children.Add(Label("Frame step (Render Animation)"));
        Children.Add(NumberBox(s.FrameStep, v => Doc.Session.Edit("Frame Step", () => { s.FrameStep = Math.Max(1, (int)v); s.Notify(); })));
        Children.Add(Label("Frames per second"));
        Children.Add(NumberBox(s.FramesPerSecond, v => { if (v > 0) Doc.Session.Edit("Frames per Second", () => { s.FramesPerSecond = v; s.Notify(); }); }));
        Children.Add(Note($"{s.Objects.Count()} objects, {s.Nulls.Count()} nulls, {s.Bones.Count()} bones, {s.Lights.Count()} lights, {s.Cameras.Count()} cameras. " +
                          $"{(s.LastFrame - s.FirstFrame + 1) / Math.Max(1, s.FramesPerSecond):0.##} seconds."));
        Children.Add(Buttons(
            Look.MakeButton("New Scene", () => _ = Window.NewScene(), "Clear the scene to a camera and a light (Ctrl+N)"),
            Look.MakeButton("Load Scene…", () => _ = Window.LoadSceneDialog(), "Open a .lws or .avascene (Ctrl+O)"),
            Look.MakeButton("Save Scene", () => _ = Window.SaveScene(false), "Save (Ctrl+S)"),
            Look.MakeButton("Scene Editor", () => Window.ShowBottom(BottomTab.SceneEditor), "The items' hierarchy, visibility, locks and keys (E)")));
    }

    private void Frames(int first, int last)
    {
        if (last <= first) { Window.SetStatus("The last frame must be after the first."); return; }
        var s = Doc.Scene;
        Doc.Session.Edit("Frames", () => { s.FirstFrame = first; s.LastFrame = last; s.Notify(); });
        Doc.Session.SetFrame(Doc.Session.Frame);
    }
}

/// <summary>
/// The Objects, Bones, Lights and Camera panels: the items of their kind (choosing one chooses it in the scene), what
/// can be added, and the chosen item's settings (the engine's item panel, with an object's file and the hub's
/// Edit in Modeler).
/// </summary>
public sealed partial class ItemsPanel : LayoutPanel
{
    private readonly ListBox _list = new() { MaxHeight = 180, FontSize = 12 };
    private readonly EditMode _mode;
    private string _shown = "";
    private bool _filling;
    private SceneItem? _chosenShown;
    private double _frameShown = double.NaN;

    public ItemPropertiesPanel Properties { get; }

    public ItemsPanel(MainWindow window, EditMode mode) : base(window, mode switch
    {
        EditMode.Objects => "Objects", EditMode.Bones => "Bones", EditMode.Lights => "Lights", _ => "Camera",
    })
    {
        _mode = mode;
        _list.Name = Title + "List";
        Properties = new ItemPropertiesPanel(window.Session) { Extras = Extras };
        Properties.ItemsChanged += Reshow;
        _list.SelectionChanged += (_, _) =>
        {
            if (_filling || _list.SelectedItem is not ItemPropertiesPanel.ItemChoice c) return;
            Window.Mode = _mode;
            Doc.Session.Select(c.Item);
        };
    }

    /// <summary>The items listed.</summary>
    public IReadOnlyList<SceneItem> Listed => _list.Items.OfType<ItemPropertiesPanel.ItemChoice>().Select(c => c.Item).ToList();

    public override void Refresh()
    {
        var items = Doc.Scene.ItemsOf(_mode);
        var list = string.Join("|", items.Select(i => $"{i.Name}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i)}"));
        bool listChanged = list != _shown;
        _shown = list;
        _filling = true;
        if (listChanged) _list.ItemsSource = items.Select(i => new ItemPropertiesPanel.ItemChoice(i)).ToList();
        _list.SelectedIndex = Doc.Session.Selected == null ? -1 : items.ToList().IndexOf(Doc.Session.Selected);
        _filling = false;
        var chosen = Doc.Session.Selected is { } c && _mode.Includes(c) ? c : null;
        bool rebuild = Children.Count == 0 || listChanged || _chosenShown != chosen || chosen != null && _frameShown != Doc.Session.Frame;
        if (!rebuild || Properties.IsKeyboardFocusWithin) return;
        (_chosenShown, _frameShown) = (chosen, Doc.Session.Frame);
        Children.Clear();
        Children.Add(Heading(Title));
        Children.Add(_list);
        Children.Add(Actions());
        if (chosen != null)
        {
            Properties.Show();
            Children.Add(Properties);
        }
        else
        {
            Properties.Children.Clear();
            Children.Add(Note(items.Count == 0 ? $"There are no {Title.ToLowerInvariant()} yet." : $"Choose one of the {Title.ToLowerInvariant()} to see its settings."));
        }
    }

    /// <summary>Shows the chosen item's settings afresh (after an agent or undo changed them).</summary>
    public void Reshow()
    {
        _shown = "";
        Children.Clear();
        Refresh();
    }

    private Control Actions()
    {
        var s = Doc.Session;
        // Read when a button is pressed, not when the panel was drawn: the choice may have changed since.
        ObjectItem? Obj() => s.Selected as ObjectItem;
        switch (_mode)
        {
            case EditMode.Objects:
                return Buttons(
                    Look.MakeButton("Load Object…", () => _ = Window.LoadObjectDialog(), "Load an object file into the scene"),
                    Look.MakeButton("Replace Object…", () => { if (Obj() is { } o) _ = Window.ReplaceObjectDialog(o); else Window.SetStatus("Choose an object to replace."); }, "Give the chosen object another file's geometry, keeping its motion"),
                    Look.MakeButton("Save Object", () => { if (Obj() is { } o) _ = Window.SaveObject(o, false); }, "Save the chosen object's geometry and surfaces to its file"),
                    Look.MakeButton("Save All Objects", () => Window.SaveAllObjects(), "Save every object with unsaved changes"),
                    Look.MakeButton("Edit in Modeler", () => { if (Obj() is { } o) _ = Window.EditInModeler(o); else Window.SetStatus("Choose an object to edit in AVA Modeler."); }, "Open the chosen object in AVA Modeler; its saves come back here (Ctrl+E)"),
                    Look.MakeButton("Add Null", () => s.Add(new NullItem("Null")), "An invisible item to parent things to"),
                    Look.MakeButton("Clone…", () => Window.OpenClone(), "Copies of the chosen item, each offset from the one before"),
                    Look.MakeButton("Load from Scene…", () => _ = Window.LoadFromSceneDialog(), "Bring another scene's items into this one"));
            case EditMode.Bones:
                ObjectItem? Owner() => (s.Selected as Bone)?.Owner ?? Window.CurrentObject;
                return Buttons(
                    Look.MakeButton("Add Bone", () =>
                    {
                        var owner = Owner();
                        if (owner == null) { Window.SetStatus("Choose an object (in the Objects panel) to add a bone to."); return; }
                        Window.Mode = EditMode.Bones;
                        var parent = s.Selected as Bone;
                        var bone = s.Edit("Add Bone", () =>
                        {
                            var b = s.Scene.AddBone(owner, $"Bone {owner.Bones.Count + 1}");
                            if (parent != null) b.Parent = parent;
                            return b;
                        });
                        s.Select(bone);
                    }, "Add a bone to the chosen object (a child of the chosen bone)"),
                    Look.MakeButton("Skelegons → Bones", () => Window.SkelegonsToBones(Owner()), "Make bones from the object's skelegons, in one step"));
            case EditMode.Lights:
                return Buttons(
                    Look.MakeButton("Distant", () => s.Add(new LightItem("Distant Light")), "Add a distant light (like the sun)"),
                    Look.MakeButton("Point", () => s.Add(new LightItem("Point Light", LightType.Point)), "Add a point light"),
                    Look.MakeButton("Spot", () => s.Add(new LightItem("Spot Light", LightType.Spot)), "Add a spotlight"),
                    Look.MakeButton("Area", () => s.Add(new LightItem("Area Light", LightType.Area)), "Add an area light (soft shadows)"),
                    Look.MakeButton("Clone…", () => Window.OpenClone(), "Copies of the chosen light, each offset from the one before"));
            default:
                return Buttons(Look.MakeButton("Add Camera", () => s.Add(new CameraItem("Camera")), "Add a camera"),
                    Look.MakeButton("Clone…", () => Window.OpenClone(), "Copies of the chosen camera, each offset from the one before"));
        }
    }

    /// <summary>An object's file and state, under its settings.</summary>
    private void Extras(SceneItem item, ItemPropertiesPanel panel)
    {
        if (item is LightItem light) LightEffects(light, panel);
        if (item is not ObjectItem o) return;
        var file = o.SourcePath ?? o.Document?.FilePath;
        panel.Children.Add(new TextBlock { Text = "File", FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) });
        panel.Children.Add(Note((file != null ? Path.GetFileName(file) : "none yet") + (o.Document is { Modified: true } ? " (unsaved changes)" : "")));
        Dissolve(o, panel);
        Morphs(o, panel);
    }

    /// <summary>
    /// The object's morph targets (classic Layout's Metamorph): each with its weight at this frame in percent (keyed by
    /// Auto Key, as any value), and buttons to add a target from a file or another object, or take one off.
    /// </summary>
    private void Morphs(ObjectItem o, ItemPropertiesPanel panel)
    {
        var s = Doc.Session;
        panel.Children.Add(new TextBlock { Text = "Morph targets", FontWeight = FontWeight.Bold, Foreground = Look.Text, Margin = new Thickness(0, 8, 0, 0) });
        if (o.Morphs.Count == 0) panel.Children.Add(Note("None. A target is another object with the same points in the same order; its weight (morph.<name>) blends toward it, keyed like any channel."));
        foreach (var m in o.Morphs.ToList())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,70,Auto"), Margin = new Thickness(0, 2) };
            var name = new TextBlock { Text = m.Name + (AVAModeler.Layout.Keying.IsAnimated(m.Weight) ? " ◆" : ""), Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            var weight = NumberBox(m.Weight.Evaluate(s.Frame) * 100, v =>
            {
                try { MorphTargets.SetWeight(s, o, m, v / 100); }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { Window.SetStatus(e.Message); }
            }, "MorphWeight" + o.Morphs.ToList().IndexOf(m));
            ToolTip.SetTip(weight, $"{m.Name}'s weight at this frame, % (0 {o.Name}'s own shape, 100 the target)");
            var remove = Look.MakeButton("×", () => { MorphTargets.Remove(s, o, m); Reshow(); }, $"Take {m.Name} off {o.Name}");
            Grid.SetColumn(weight, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(name);
            row.Children.Add(weight);
            row.Children.Add(remove);
            panel.Children.Add(row);
        }
        var candidates = MorphTargets.Candidates(s.Scene, o);
        var source = new ComboBox { ItemsSource = candidates.Select(c => c.Name).ToList(), PlaceholderText = candidates.Count == 0 ? "(no object matches)" : "Another object", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "MorphSource" };
        panel.Children.Add(source);
        panel.Children.Add(Buttons(
            Look.MakeButton("Add Target File…", () => _ = Window.AddMorphFromFile(o), "A morph target from an object file with the same points"),
            Look.MakeButton("Add Target Object", () =>
            {
                if (source.SelectedIndex < 0 || source.SelectedIndex >= candidates.Count) { Window.SetStatus("Choose the object to morph toward (one with the same points)."); return; }
                try
                {
                    var m = MorphTargets.AddFromObject(s, o, candidates[source.SelectedIndex]);
                    Window.SetStatus($"{o.Name} can morph toward {m.Name}: key its weight (morph.{m.Name}).");
                    Reshow();
                }
                catch (ArgumentException e) { Window.SetStatus(e.Message); }
            }, "A morph target from the chosen object's shape")));
    }
}

/// <summary>
/// The Surfaces panel: an object's surfaces, each with its colour, diffuse, specular, glossiness, luminosity,
/// reflection, transparency, smoothing and two-sidedness, as classic Layout's surface panel. Each change is one scene
/// undo step (the object gets a changed copy of its document) and marks the object unsaved; Save Object keeps it.
/// </summary>
public sealed class SurfacesPanel : LayoutPanel
{
    private readonly ComboBox _objects = new() { HorizontalAlignment = HorizontalAlignment.Stretch, Name = "SurfaceObject" };
    private readonly ListBox _surfaces = new() { MaxHeight = 160, FontSize = 12, Name = "SurfaceList" };
    private readonly StackPanel _editor = new() { Spacing = 3 };
    private List<ObjectItem> _choices = [];
    private bool _filling;
    private string _shown = "";

    public SurfacesPanel(MainWindow window) : base(window, "Surfaces")
    {
        _objects.SelectionChanged += (_, _) => { if (!_filling) { _surfaces.SelectedIndex = 0; ShowSurfaces(); } };
        _surfaces.SelectionChanged += (_, _) => { if (!_filling) ShowEditor(); };
    }

    /// <summary>The object whose surfaces are showing.</summary>
    public ObjectItem? Object => _objects.SelectedIndex >= 0 && _objects.SelectedIndex < _choices.Count ? _choices[_objects.SelectedIndex] : null;

    /// <summary>Chooses an object and one of its surfaces (for tests and the Objects panel).</summary>
    public void Choose(ObjectItem item, int surface = 0)
    {
        Refresh();
        _objects.SelectedIndex = _choices.IndexOf(item);
        ShowSurfaces();
        _surfaces.SelectedIndex = surface;
        ShowEditor();
    }

    public override void Refresh()
    {
        _choices = Doc.Scene.Objects.Where(o => !o.IsPlaceholder).ToList();
        var signature = string.Join("|", _choices.Select(o => o.Name + o.Document!.GetHashCode()));
        if (Children.Count > 0 && signature == _shown) return;
        _shown = signature;
        var keep = Object;
        Children.Clear();
        Children.Add(Heading("Surfaces"));
        if (_choices.Count == 0)
        {
            Children.Add(Note("There are no objects in the scene: load one in the Objects panel."));
            return;
        }
        _filling = true;
        _objects.ItemsSource = _choices.Select(o => o.Name).ToList();
        int at = keep != null ? _choices.IndexOf(keep) : -1;
        if (at < 0 && Doc.Session.Selected is ObjectItem sel) at = _choices.IndexOf(sel);
        _objects.SelectedIndex = Math.Max(0, at);
        _filling = false;
        Children.Add(Label("Object"));
        Children.Add(_objects);
        Children.Add(Label("Surface"));
        Children.Add(_surfaces);
        Children.Add(_editor);
        ShowSurfaces();
    }

    private void ShowSurfaces()
    {
        int keep = Math.Max(0, _surfaces.SelectedIndex);
        _filling = true;
        _surfaces.ItemsSource = Object?.Surfaces.Select(s => s.Name).ToList() ?? [];
        _surfaces.SelectedIndex = Object == null ? -1 : Math.Min(keep, Object.Surfaces.Count - 1);
        _filling = false;
        ShowEditor();
    }

    private void ShowEditor()
    {
        _editor.Children.Clear();
        if (Object is not { } o || _surfaces.SelectedIndex < 0 || _surfaces.SelectedIndex >= o.Surfaces.Count) return;
        int index = _surfaces.SelectedIndex;
        var s = o.Surfaces[index];
        void Set(string what, Action<Surface> change)
        {
            Doc.EditSurface(o, index, $"{s.Name} {what}", change);
            _shown = "";
            Refresh();
        }
        var colour = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        byte[] rgb = [s.Color.R, s.Color.G, s.Color.B];
        for (int i = 0; i < 3; i++)
        {
            int channel = i;
            var box = NumberBox(rgb[i], v =>
            {
                var c = new byte[] { s.Color.R, s.Color.G, s.Color.B };
                c[channel] = (byte)Math.Clamp(Math.Round(v), 0, 255);
                Set("Colour", x => x.Color = new Rgb(c[0], c[1], c[2]));
            }, "SurfaceColour" + "RGB"[i]);
            box.Margin = new Thickness(1, 0);
            Grid.SetColumn(box, i);
            colour.Children.Add(box);
        }
        _editor.Children.Add(Label("Colour (0–255)"));
        _editor.Children.Add(colour);
        void Percent(string label, double value, Action<Surface, double> set, string name)
        {
            _editor.Children.Add(Label(label + " %"));
            _editor.Children.Add(NumberBox(value * 100, v => Set(label, x => set(x, Math.Clamp(v / 100, 0, 10))), name));
        }
        Percent("Luminosity", s.Luminosity, (x, v) => x.Luminosity = v, "SurfaceLuminosity");
        Percent("Diffuse", s.Diffuse, (x, v) => x.Diffuse = v, "SurfaceDiffuse");
        Percent("Specular", s.Specular, (x, v) => x.Specular = v, "SurfaceSpecular");
        _editor.Children.Add(Label("Glossiness (16 low … 1024 high)"));
        _editor.Children.Add(NumberBox(s.Glossiness, v => Set("Glossiness", x => x.Glossiness = Math.Clamp(v, 1, 4096)), "SurfaceGlossiness"));
        Percent("Reflection", s.Reflection, (x, v) => x.Reflection = Math.Min(1, v), "SurfaceReflection");
        Percent("Transparency", s.Transparency, (x, v) => x.Transparency = Math.Min(1, v), "SurfaceTransparency");
        var smooth = new CheckBox { Content = "Smoothing", IsChecked = s.Smoothing, Foreground = Look.Text, Name = "SurfaceSmoothing" };
        smooth.IsCheckedChanged += (_, _) => Set("Smoothing", x => x.Smoothing = smooth.IsChecked == true);
        _editor.Children.Add(smooth);
        _editor.Children.Add(Label("Smoothing angle °"));
        _editor.Children.Add(NumberBox(s.SmoothingAngle, v => Set("Smoothing Angle", x => x.SmoothingAngle = Math.Clamp(v, 0, 180))));
        var sides = new CheckBox { Content = "Double sided", IsChecked = s.DoubleSided, Foreground = Look.Text };
        sides.IsCheckedChanged += (_, _) => Set("Double Sided", x => x.DoubleSided = sides.IsChecked == true);
        _editor.Children.Add(sides);
        _editor.Children.Add(Note(s.Textures.Count > 0 ? $"{s.Textures.Count} texture layer(s): edit textures in AVA Modeler (Edit in Modeler)." : "Textures are edited in AVA Modeler (Edit in Modeler)."));
        _editor.Children.Add(Buttons(Look.MakeButton("Save Object", () => _ = Window.SaveObject(o, false), "Keep the surface changes in the object's file")));
    }
}

/// <summary>The Effects panel: the backdrop, ambient light, ambient occlusion and the environment (the engine's scene settings), applied as one undo step.</summary>
public sealed class EffectsPanel : LayoutPanel
{
    private SceneSettingsPanel? _settings;
    private AVAModeler.Scene.Scene? _for;
    private CompositingSettings? _compositing;
    private int _images = -1;

    public EffectsPanel(MainWindow window) : base(window, "Effects") { }

    public SceneSettingsPanel? Settings => _settings;

    public override void Refresh()
    {
        // Rebuilt for a new scene, or after the settings were applied (to show them as they are now).
        if (_settings != null && ReferenceEquals(_for, Doc.Scene) && Children.Count > 0 && _compositing == Doc.Scene.Compositing && _images == Doc.Scene.Images.Count) return;
        (_compositing, _images) = (Doc.Scene.Compositing, Doc.Scene.Images.Count);
        Children.Clear();
        Children.Add(Heading("Effects"));
        Children.Add(Note("The backdrop, ambient light, ambient occlusion and the environment picture. Apply makes them one undo step. Fog is kept in the scene file; depth of field and motion blur are the camera's (Camera panel)."));
        _for = Doc.Scene;
        _settings = new SceneSettingsPanel(Doc.Session) { Margin = new Thickness(0) };
        _settings.Applied += () => { _settings = null; Window.SetStatus("Effects applied."); Refresh(); };
        Children.Add(_settings);
        Compositing();
    }

    /// <summary>Forces the panel to be built afresh (after a compositing change).</summary>
    private void Rebuild()
    {
        _settings = null;
        Refresh();
    }

    /// <summary>
    /// Compositing, as classic Layout's Effects panel had it: a background image behind everything the camera sees
    /// nothing in front of, a foreground image laid over the render, cut out by a colour key (low to high) or by alpha,
    /// and its dissolve; the images come from the Images panel. Each change is one undo step.
    /// </summary>
    private void Compositing()
    {
        var s = Doc.Session;
        var c = Doc.Scene.Compositing;
        var names = new List<string> { "(none)" };
        names.AddRange(Doc.Scene.Images.Select(i => Path.GetFileName(i.Path)));
        string PathOf(int index) => index <= 0 ? "" : Doc.Scene.Images[index - 1].Path;
        int IndexOf(string path) => path.Length == 0 ? 0 : 1 + Doc.Scene.Images.ToList().FindIndex(i => SceneImages.Same(i.Path, path));
        void Set(CompositingSettings next)
        {
            try { s.SetCompositing(next); Window.SetStatus("Compositing changed."); }
            catch (ArgumentException e) { Window.SetStatus(e.Message); }
            Rebuild();
        }
        Children.Add(Heading("Compositing"));
        if (Doc.Scene.Images.Count == 0) Children.Add(Note("Load pictures in the Images panel first; they can then be the background or foreground here."));
        ComboBox Images(string name, string path, Func<string, CompositingSettings> with)
        {
            var box = new ComboBox { ItemsSource = names, SelectedIndex = Math.Max(0, IndexOf(path)), HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Name = name };
            box.SelectionChanged += (_, _) => { if (PathOf(box.SelectedIndex) != path) Set(with(PathOf(box.SelectedIndex))); };
            return box;
        }
        Children.Add(Label("Background image (where the camera sees nothing)"));
        Children.Add(Images("CompositingBackground", c.Background, p => c with { Background = p }));
        Children.Add(Label("Foreground image (laid over the render)"));
        Children.Add(Images("CompositingForeground", c.Foreground, p => c with { Foreground = p }));
        Children.Add(Label("Foreground key"));
        var key = new ComboBox { ItemsSource = new[] { "None (its own alpha)", "Colour range", "Alpha image" }, SelectedIndex = (int)c.Key, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11, Name = "CompositingKey" };
        key.SelectionChanged += (_, _) => { if (key.SelectedIndex != (int)c.Key) Set(c with { Key = (ForegroundKey)key.SelectedIndex }); };
        Children.Add(key);
        if (c.Key == ForegroundKey.Color)
        {
            Grid Colour(string label, Vec3 v, Func<Vec3, CompositingSettings> with, string name)
            {
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
                for (int i = 0; i < 3; i++)
                {
                    int ch = i;
                    var b = NumberBox(Math.Round(v[i] * 255), x =>
                    {
                        double[] a = [v.X, v.Y, v.Z];
                        a[ch] = Math.Clamp(x, 0, 255) / 255;
                        Set(with(new Vec3(a[0], a[1], a[2])));
                    }, name + "RGB"[i]);
                    b.Margin = new Thickness(1, 0);
                    Grid.SetColumn(b, i);
                    g.Children.Add(b);
                }
                Children.Add(Label(label));
                return g;
            }
            Children.Add(Colour("Key out from (0–255)", c.KeyLow, v => c with { KeyLow = v }, "KeyLow"));
            Children.Add(Colour("…to (0–255)", c.KeyHigh, v => c with { KeyHigh = v }, "KeyHigh"));
        }
        if (c.Key == ForegroundKey.Alpha)
        {
            Children.Add(Label("Alpha image (empty: the foreground's own alpha)"));
            Children.Add(Images("CompositingAlpha", c.ForegroundAlpha, p => c with { ForegroundAlpha = p }));
        }
        Children.Add(Label("Foreground dissolve %"));
        Children.Add(NumberBox(c.ForegroundDissolve * 100, v => Set(c with { ForegroundDissolve = Math.Clamp(v, 0, 100) / 100 }), "ForegroundDissolve"));
    }
}

/// <summary>The Render panel: the camera rendered through, the picture's size, and rendering a frame or the animation.</summary>
public sealed class RenderPanel : LayoutPanel
{
    public RenderPanel(MainWindow window) : base(window, "Render") { }

    public override void Refresh()
    {
        Children.Clear();
        var s = Doc.Scene;
        Children.Add(Heading("Render"));
        var cams = s.Cameras.ToList();
        var camera = new ComboBox { ItemsSource = cams.Select(c => c.Name).ToList(), SelectedIndex = s.CurrentCamera == null ? -1 : cams.IndexOf(s.CurrentCamera), HorizontalAlignment = HorizontalAlignment.Stretch, Name = "RenderCamera" };
        camera.SelectionChanged += (_, _) =>
        {
            if (camera.SelectedIndex >= 0 && cams[camera.SelectedIndex] != s.CurrentCamera)
                Doc.Session.Edit("Use Camera", () => s.CurrentCamera = cams[camera.SelectedIndex]);
        };
        Children.Add(Label("Camera"));
        Children.Add(camera);
        if (s.CurrentCamera is { } cam)
        {
            Children.Add(Label("Width × height (pixels)"));
            var size = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var w = NumberBox(cam.Width, v => Doc.Session.Edit("Camera Width", () => { cam.Width = Math.Clamp((int)v, 1, 8192); s.Notify(); }), "RenderWidth");
            var h = NumberBox(cam.Height, v => Doc.Session.Edit("Camera Height", () => { cam.Height = Math.Clamp((int)v, 1, 8192); s.Notify(); }), "RenderHeight");
            Grid.SetColumn(h, 1);
            size.Children.Add(w);
            size.Children.Add(h);
            Children.Add(size);
            var presets = new WrapPanel();
            foreach (var (pw, ph) in new[] { (320, 240), (640, 480), (1280, 720), (1920, 1080) })
            {
                var b = Look.MakeButton($"{pw}×{ph}", () => { Doc.Session.Edit("Resolution", () => { cam.Width = pw; cam.Height = ph; s.Notify(); }); Refresh(); });
                b.Margin = new Thickness(0, 0, 3, 3);
                presets.Children.Add(b);
            }
            Children.Add(presets);
            Children.Add(Note($"Zoom {cam.Zoom.Evaluate(Doc.Session.Frame):0.##} ({cam.VerticalFov(Doc.Session.Frame):0.#}° high)" +
                              (cam.DepthOfField.On ? ", depth of field" : "") + (cam.MotionBlur.On ? ", motion blur" : "") + "."));
        }
        Children.Add(Note($"Frames {s.FirstFrame} to {s.LastFrame}, every {s.FrameStep}, at {s.FramesPerSecond:0.##} a second."));
        Children.Add(Buttons(
            Look.MakeButton("Render Frame", Window.Render, "Ray-trace this frame (F9)"),
            Look.MakeButton("Render Animation…", () => Window.RenderAnimation(), "Render the frames to PNGs, and an MP4 or GIF with ffmpeg (F10)"),
            Look.MakeButton("Render Queue…", () => Window.OpenRenderQueue(), "Batch rendering: send the scene to the render queue or a render farm folder that render nodes work"),
            Look.MakeButton("Export glTF…", () => _ = Window.ExportGltf(), "The scene as .glb or .gltf for other programs")));
    }
}

/// <summary>The Options panel: the hub, AI agents, Auto Key and the view.</summary>
public sealed class OptionsPanel : LayoutPanel
{
    public OptionsPanel(MainWindow window) : base(window, "Options") { }

    public override void Refresh()
    {
        Children.Clear();
        var prefs = Window.Preferences;
        var options = Window.Options;
        Children.Add(Heading("Options"));
        Children.Add(Label("The hub"));
        var hub = new CheckBox { Content = "Link with AVA Modeler", IsChecked = prefs.HubEnabled, Foreground = Look.Text, Name = "HubEnabled" };
        ToolTip.SetTip(hub, "Edit in Modeler sends objects to AVA Modeler, and its saves update them here; its Send to AVALightWave adds objects here. Over a local pipe only your programs can open.");
        hub.IsCheckedChanged += (_, _) => { prefs.HubEnabled = hub.IsChecked == true; prefs.Save(); Window.SyncHub(); Refresh(); };
        Children.Add(hub);
        Children.Add(Note(Window.Hub.Running ? "On the hub." : "Not on the hub."));
        Children.Add(Label("AVA Modeler's program (empty: beside AVALightWave, its build, or the PATH)"));
        var path = new TextBox { Text = options.ModelerPath ?? "", FontSize = 11, Name = "ModelerPath" };
        path.LostFocus += (_, _) =>
        {
            options.ModelerPath = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text.Trim();
            options.Save();
            Window.Hub.ModelerSetting = options.ModelerPath;
        };
        Children.Add(path);
        Children.Add(Buttons(Look.MakeButton("Check", () =>
        {
            Window.Hub.ModelerSetting = options.ModelerPath;
            var found = Window.Hub.Launcher.Find(options.ModelerPath);
            bool there = Window.Hub.ModelerThere();
            Window.SetStatus(there ? "AVA Modeler is running and on the hub." : found != null ? $"AVA Modeler isn't running; Edit in Modeler will start {found}." : "AVA Modeler isn't running and can't be found: give its program here.");
        }, "Is AVA Modeler running, or can it be found?")));
        Children.Add(Label("AI"));
        var agents = new CheckBox { Content = "Allow AI agents (MCP, this user's programs only)", IsChecked = prefs.AllowAgents, Foreground = Look.Text, Name = "AllowAgents" };
        ToolTip.SetTip(agents, "An agent set up with \"AVALightWave --mcp --attach\" can then work on the open scene: you see its work, and Ctrl+Z undoes it.");
        agents.IsCheckedChanged += (_, _) => { prefs.AllowAgents = agents.IsChecked == true; prefs.Save(); Window.SyncAgents(); };
        Children.Add(agents);
        Children.Add(Label("Animation"));
        var auto = new CheckBox { Content = "Auto Key when starting", IsChecked = options.AutoKey, Foreground = Look.Text };
        auto.IsCheckedChanged += (_, _) => { options.AutoKey = auto.IsChecked == true; options.Save(); };
        Children.Add(auto);
        var gl = new CheckBox { Content = "Draw the views with OpenGL (software where it can't start)", IsChecked = options.OpenGlViews, Foreground = Look.Text, Name = "OpenGlViews" };
        gl.IsCheckedChanged += (_, _) => { options.OpenGlViews = gl.IsChecked == true; options.Save(); Window.UseOpenGl(options.OpenGlViews); };
        Children.Add(gl);
        Children.Add(Label("Auto-save the scene every (minutes, 0 for never; from the next start)"));
        Children.Add(NumberBox(options.AutosaveMinutes, v => { options.AutosaveMinutes = Math.Clamp((int)v, 0, 600); options.Save(); }, "AutosaveMinutes"));
        Children.Add(Note($"Settings are kept in {(prefs.Location != null ? Path.GetDirectoryName(prefs.Location) : "memory only (this window was made for tests)")}."));
    }
}
