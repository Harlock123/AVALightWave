using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Core;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>A small requester window in classic Layout's way: rows of labelled boxes, a line for problems, OK and Cancel.</summary>
public abstract class Requester : Window
{
    private readonly TextBlock _problem = new() { Foreground = Look.ButtonOn, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    protected readonly StackPanel Rows = new() { Spacing = 4, Margin = new Thickness(12) };

    protected Requester(string title, double width = 400)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        Background = Look.Panel;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
    }

    /// <summary>Puts the problem line and the buttons under the rows.</summary>
    protected void Finish()
    {
        Rows.Children.Add(_problem);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, Name = "RequesterOK" };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (Apply()) Close(); };
        cancel.Click += (_, _) => Close();
        Rows.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        Content = Rows;
    }

    /// <summary>Does what was asked; false (with the reason shown) if something typed won't do.</summary>
    public abstract bool Apply();

    /// <summary>Why the last Apply didn't work, or "".</summary>
    public string Problem => _problem.Text ?? "";

    protected void Say(string text) => _problem.Text = text;

    protected TextBlock Heading(string text) => new() { Text = text, Foreground = Look.Text, FontWeight = FontWeight.Bold };

    protected TextBlock Label(string text) => new() { Text = text, Foreground = Look.Text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>Three boxes in a row (X, Y, Z or H, P, B).</summary>
    protected TextBox[] Triple(string[] axes, Vec3 v, string name)
    {
        var boxes = new TextBox[3];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*") };
        for (int i = 0; i < 3; i++)
        {
            var label = new TextBlock { Text = axes[i], Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            var box = boxes[i] = new TextBox { Text = F(v[i]), FontSize = 12, Name = name + axes[i] };
            Grid.SetColumn(label, 2 * i);
            Grid.SetColumn(box, 2 * i + 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
        }
        Rows.Children.Add(grid);
        return boxes;
    }

    protected static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A number from a box, or null with the problem said.</summary>
    protected double? Number(TextBox box)
    {
        if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)) return v;
        Say($"\"{box.Text}\" isn't a number.");
        return null;
    }

    protected Vec3? Vector(TextBox[] boxes)
    {
        var v = new double[3];
        for (int i = 0; i < 3; i++)
            if (Number(boxes[i]) is { } n) v[i] = n;
            else return null;
        return new Vec3(v[0], v[1], v[2]);
    }
}

/// <summary>
/// Clone Item: how many copies of the chosen item, and what each adds to the one before (a move, a turn, a change of
/// size, and a time offset for its motion). OK makes them all as one undo step.
/// </summary>
public sealed class CloneRequester : Requester
{
    private readonly SceneDocument _document;
    private readonly SceneItem _item;

    public TextBox Count { get; }
    public TextBox[] Move { get; }
    public TextBox[] Turn { get; }
    public TextBox[] Grow { get; }
    public TextBox TimeOffset { get; }

    /// <summary>The copies made by the last Apply.</summary>
    public IReadOnlyList<SceneItem> Made { get; private set; } = [];

    public CloneRequester(SceneDocument document, SceneItem item) : base($"Clone {item.Name}")
    {
        _document = document;
        _item = item;
        Rows.Children.Add(Heading($"{item.Kind}: {item.Name}" + (item is ObjectItem { Bones.Count: > 0 } o ? $" (its {o.Bones.Count} bones come too)" : "")));
        Rows.Children.Add(Label("Number of clones"));
        Rows.Children.Add(Count = new TextBox { Text = "1", FontSize = 12, Name = "CloneCount" });
        Rows.Children.Add(Label("Each clone moves from the one before (m)"));
        Move = Triple(["X", "Y", "Z"], Vec3.Zero, "ClonePosition");
        Rows.Children.Add(Label("…turns (°)"));
        Turn = Triple(["H", "P", "B"], Vec3.Zero, "CloneRotation");
        Rows.Children.Add(Label("…grows (added to its scale)"));
        Grow = Triple(["X", "Y", "Z"], Vec3.Zero, "CloneScale");
        Rows.Children.Add(Label("…and moves in time (frames: its keys later by this)"));
        Rows.Children.Add(TimeOffset = new TextBox { Text = "0", FontSize = 12, Name = "CloneTime" });
        Finish();
    }

    public override bool Apply()
    {
        if (!int.TryParse(Count.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
        {
            Say($"\"{Count.Text}\" isn't a whole number.");
            return false;
        }
        if (Vector(Move) is not { } p || Vector(Turn) is not { } r || Vector(Grow) is not { } s || Number(TimeOffset) is not { } t) return false;
        try
        {
            Made = _document.Clone(_item, new CloneOptions { Count = count, Position = p, Rotation = r, Scale = s, TimeOffset = t });
            return true;
        }
        catch (ArgumentException e)
        {
            Say(e.Message);
            return false;
        }
    }
}

/// <summary>Load from Scene's question: which kinds of item to bring, and what to do about names already taken.</summary>
public sealed class LoadFromSceneRequester : Requester
{
    private readonly SceneDocument _document;
    private readonly string _path;

    public CheckBox Lights { get; } = new() { Content = "Its lights", IsChecked = true, Name = "MergeLights" };
    public CheckBox Cameras { get; } = new() { Content = "Its cameras", IsChecked = true, Name = "MergeCameras" };
    public CheckBox Rename { get; } = new() { Content = "Rename items whose names are taken (\"Ball (2)\")", IsChecked = true, Name = "MergeRename" };

    /// <summary>What the last Apply brought in.</summary>
    public MergeResult? Result { get; private set; }

    public LoadFromSceneRequester(SceneDocument document, string path) : base("Load from Scene", 420)
    {
        _document = document;
        _path = path;
        Rows.Children.Add(Heading($"Bring {Path.GetFileName(path)}'s items into this scene"));
        Rows.Children.Add(new TextBlock { Text = "Its objects (with their bones), nulls, hierarchy and motion come in; its frames, backdrop and effects stay behind.", Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        foreach (var c in new[] { Lights, Cameras, Rename })
        {
            c.Foreground = Look.Text;
            Rows.Children.Add(c);
        }
        Finish();
    }

    public override bool Apply()
    {
        try
        {
            Result = _document.LoadFromScene(_path, new MergeOptions
            {
                Lights = Lights.IsChecked == true, Cameras = Cameras.IsChecked == true, Names = Rename.IsChecked == true ? NameClash.Rename : NameClash.Keep,
            });
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or UnauthorizedAccessException or ArgumentException)
        {
            Say($"Can't load from {Path.GetFileName(_path)}: {e.Message}");
            return false;
        }
    }
}
