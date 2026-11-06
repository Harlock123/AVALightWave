using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// The Images panel, as classic Layout's: the scene's images, stills and numbered sequences (sky0001.png,
/// sky0002.png…), each sequence with its frame offset and looping, for the compositing background and foreground
/// (the Effects panel) and for surfaces' image textures (a texture whose file is a sequence here plays it in renders).
/// Each change is one undo step through the engine's <c>LayoutSession.SetImages</c>.
/// </summary>
public sealed class ImagesPanel : LayoutPanel
{
    private readonly ListBox _list = new() { MaxHeight = 180, FontSize = 12, Name = "ImageList" };
    private string _shown = "";

    public ImagesPanel(MainWindow window) : base(window, "Images")
    {
        _list.SelectionChanged += (_, _) => { if (!_filling) ShowChosen(); };
    }

    private bool _filling;
    private readonly StackPanel _chosen = new() { Spacing = 3 };

    /// <summary>The image chosen in the list.</summary>
    public SceneImage? Chosen => _list.SelectedIndex >= 0 && _list.SelectedIndex < Doc.Scene.Images.Count ? Doc.Scene.Images[_list.SelectedIndex] : null;

    public override void Refresh()
    {
        var images = Doc.Scene.Images;
        var signature = string.Join("|", images.Select(i => i.ToString())) + Doc.Session.Frame;
        if (Children.Count > 0 && signature == _shown) return;
        _shown = signature;
        int keep = _list.SelectedIndex;
        Children.Clear();
        Children.Add(Heading("Images"));
        Children.Add(Note("Stills and numbered sequences for the compositing background and foreground (Effects panel) and for image textures."));
        _filling = true;
        _list.ItemsSource = images.Count == 0 ? new[] { "(none)" } : images.Select(Describe).ToList();
        _list.SelectedIndex = images.Count == 0 ? -1 : Math.Clamp(keep, 0, images.Count - 1);
        _filling = false;
        Children.Add(_list);
        Children.Add(Buttons(
            Look.MakeButton("Load Image…", () => _ = Load(false), "Add a still picture"),
            Look.MakeButton("Load Sequence…", () => _ = Load(true), "Add a numbered sequence: choose any one of its files"),
            Look.MakeButton("Remove", () => Remove(), "Take the chosen image out of the list")));
        Children.Add(_chosen);
        ShowChosen();
    }

    private string Describe(SceneImage i)
    {
        var name = Path.GetFileName(i.Path);
        if (!i.Sequence) return name;
        var range = ImageSequence.Range(i);
        return $"{name} (sequence{(range is { } r ? $" {r.First}–{r.Last}" : ", no files")}{(i.Offset != 0 ? $", offset {i.Offset}" : "")}{(i.Loop ? ", loops" : "")})";
    }

    private void ShowChosen()
    {
        _chosen.Children.Clear();
        if (Chosen is not { } image) return;
        int index = _list.SelectedIndex;
        var frame = Doc.Session.Frame;
        _chosen.Children.Add(Label(image.Path));
        if (ImageSequence.Problem(image) is { } problem) _chosen.Children.Add(Note(problem));
        if (image.Sequence)
        {
            _chosen.Children.Add(Note($"At frame {frame:0}: {Path.GetFileName(ImageSequence.Resolve(image, frame))}"));
            _chosen.Children.Add(Label("Frame offset (number = frame + offset)"));
            _chosen.Children.Add(NumberBox(image.Offset, v => Change(index, image with { Offset = (int)Math.Round(v) }), "ImageOffset"));
            var loop = new CheckBox { Content = "Loop (else hold the first and last)", IsChecked = image.Loop, Foreground = Look.Text, FontSize = 11, Name = "ImageLoop" };
            loop.IsCheckedChanged += (_, _) => Change(index, image with { Loop = loop.IsChecked == true });
            _chosen.Children.Add(loop);
        }
        var users = new List<string>();
        var c = Doc.Scene.Compositing;
        if (c.Background.Length > 0 && SceneImages.Same(c.Background, image.Path)) users.Add("the background");
        if (c.Foreground.Length > 0 && SceneImages.Same(c.Foreground, image.Path)) users.Add("the foreground");
        if (c.ForegroundAlpha.Length > 0 && SceneImages.Same(c.ForegroundAlpha, image.Path)) users.Add("the foreground's alpha");
        _chosen.Children.Add(Note(users.Count > 0 ? $"Used as {string.Join(" and ", users)}." : "Not used in compositing (Effects panel)."));
    }

    /// <summary>Adds an image file (a still, or a sequence by any one of its files) as one undo step.</summary>
    public bool Add(string path, bool sequence)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (SceneImages.Contains(Doc.Scene, full)) { Window.SetStatus($"{Path.GetFileName(full)} is already in the list."); return false; }
            if (sequence && ImageSequence.Pattern(full) == null) { Window.SetStatus($"{Path.GetFileName(full)} has no number before its extension, so it can't be a sequence."); return false; }
            var image = new SceneImage { Path = full, Sequence = sequence }.Checked();
            Doc.Session.SetImages([.. Doc.Scene.Images, image], label: $"Load Image {Path.GetFileName(full)}");
            _shown = "";
            Refresh();
            _list.SelectedIndex = Doc.Scene.Images.Count - 1;
            Window.SetStatus($"{Path.GetFileName(full)} is in the scene's images.");
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
        {
            Window.SetStatus(e.Message);
            return false;
        }
    }

    private async Task Load(bool sequence)
    {
        var path = await Window.PickToOpen(sequence ? "Choose any file of the sequence" : "Load an image", Dialogs.Images);
        if (path != null) Add(path, sequence);
    }

    private void Change(int index, SceneImage image)
    {
        try
        {
            var list = Doc.Scene.Images.ToList();
            list[index] = image.Checked();
            Doc.Session.SetImages(list, label: $"Change {Path.GetFileName(image.Path)}");
        }
        catch (ArgumentException e) { Window.SetStatus(e.Message); }
        _shown = "";
        Refresh();
    }

    /// <summary>Takes the chosen image out of the list (refused while compositing uses it).</summary>
    public bool Remove()
    {
        if (Chosen is not { } image) { Window.SetStatus("Choose an image to remove."); return false; }
        try
        {
            Doc.Session.SetImages(Doc.Scene.Images.Where(i => i != image).ToList(), label: $"Remove {Path.GetFileName(image.Path)}");
            _shown = "";
            Refresh();
            return true;
        }
        catch (ArgumentException e)
        {
            Window.SetStatus($"{Path.GetFileName(image.Path)} can't go: {e.Message}");
            return false;
        }
    }
}
