using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// Motion Options for the chosen item, as classic Layout's motion panel with its motion plug-ins: the item's motion
/// modifiers in order (the engine's lag, jitter, cycle, oscillate and expression), adding one of a kind with its settings, taking one
/// off, and loading or saving the item's motion as a LightWave motion file (.mot). Each change is one undo step through
/// the engine's <see cref="LayoutSession.SetModifiers"/> and <see cref="LayoutSession.LoadMotion"/>.
/// </summary>
public sealed class MotionOptions : Window
{
    private readonly MainWindow _window;
    private readonly SceneItem _item;
    private readonly ListBox _list = new() { MaxHeight = 140, FontSize = 11, Name = "ModifierList" };
    private readonly ComboBox _kind = new() { ItemsSource = new[] { "Lag", "Jitter", "Cycle", "Oscillate", "Expression" }, SelectedIndex = 0, Name = "ModifierKind", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _fields = new() { Spacing = 3 };
    private readonly TextBlock _said = new() { Foreground = Look.ButtonOn, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, Control> _inputs = [];

    private LayoutSession Session => _window.Session;

    public MotionOptions(MainWindow window, SceneItem item)
    {
        _window = window;
        _item = item;
        Title = $"Motion Options: {item.Name}";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        Background = Look.Panel;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = $"{item.Kind}: {item.Name}", Foreground = Look.Text, FontWeight = FontWeight.Bold });
        panel.Children.Add(Note("Motion modifiers, applied in order after its keys (and path or IK): the views, previews and renders show them; its keys stay as they are."));
        panel.Children.Add(_list);
        panel.Children.Add(Buttons(Look.MakeButton("Remove", () => Remove(), "Take the chosen modifier off"), Look.MakeButton("Remove All", () => Set([], "removed them all"), "Take every modifier off")));
        panel.Children.Add(Label("Add a modifier"));
        panel.Children.Add(_kind);
        panel.Children.Add(_fields);
        panel.Children.Add(Buttons(Look.MakeButton("Add", () => Add(), "Add this modifier after the others")));
        panel.Children.Add(Label("Motion file (.mot, LightWave's)"));
        panel.Children.Add(Buttons(
            Look.MakeButton("Load Motion…", () => _ = _window.LoadMotionDialog(_item), "Replace its nine channels with a motion file's keys"),
            Look.MakeButton("Save Motion…", () => _ = _window.SaveMotionDialog(_item), "Save its nine channels as a motion file")));
        panel.Children.Add(_said);
        var close = new Button { Content = "Close", Width = 80, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);
        Content = panel;
        _kind.SelectionChanged += (_, _) => ShowFields();
        ShowFields();
        ShowList();
    }

    /// <summary>What the last action said.</summary>
    public string Said => _said.Text ?? "";

    /// <summary>The kind of modifier Add makes (Lag, Jitter, Cycle, Oscillate, Expression).</summary>
    public string Kind { get => (string)_kind.SelectedItem!; set => _kind.SelectedItem = value; }

    /// <summary>An input by its setting's name (for tests): a TextBox, ComboBox or CheckBox.</summary>
    public Control Input(string name) => _inputs[name];

    private static TextBlock Note(string t) => new() { Text = t, Foreground = Look.Text, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private static TextBlock Label(string t) => new() { Text = t, Foreground = Look.Text, FontSize = 12, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 6, 0, 0) };

    private static WrapPanel Buttons(params Button[] buttons)
    {
        var w = new WrapPanel();
        foreach (var b in buttons) { b.Margin = new Thickness(0, 0, 3, 3); w.Children.Add(b); }
        return w;
    }

    private void ShowList()
    {
        _list.ItemsSource = _item.Modifiers.Select((m, i) => $"{i + 1}. {Describe(m)}").ToList();
        if (_item.Modifiers.Count == 0) _list.ItemsSource = new[] { "(none)" };
    }

    /// <summary>A modifier in a line.</summary>
    public static string Describe(MotionModifier m) => m switch
    {
        LagModifier l => $"Lag behind {l.Leader.Name} by {F(l.Delay)} frames, {F(l.Strength * 100)}%" + (l.Rotation ? ", with its rotation" : ""),
        JitterModifier j => $"Jitter ±{F(j.Position.X)}/{F(j.Position.Y)}/{F(j.Position.Z)} m, ±{F(j.Rotation.X)}/{F(j.Rotation.Y)}/{F(j.Rotation.Z)}°, {F(j.Frequency)} a second, seed {j.Seed}",
        CycleModifier c => $"Cycle its keys as {c.Driver.Name}'s {SceneChannels.MotionNames[(int)c.DriverChannel]} goes {F(c.Low)} to {F(c.High)}",
        ExpressionModifier e => $"{SceneChannels.MotionNames[(int)e.Channel]} = {e.Formula}",
        OscillateModifier o => $"Oscillate {SceneChannels.MotionNames[(int)o.Channel]} ±{F(o.Amplitude)} every {F(o.Period)} frames from {F(o.Start)}" + (o.Damping > 0 ? $", damped {F(o.Damping)}" : ""),
        _ => m.Kind,
    };

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void ShowFields()
    {
        _fields.Children.Clear();
        _inputs.Clear();
        var others = Session.Scene.Items.Where(i => i != _item).Select(i => i.Name).ToList();
        void Box(string name, string label, double value)
        {
            var box = new TextBox { Text = F(value), FontSize = 11, Name = "Modifier" + name };
            _inputs[name] = box;
            _fields.Children.Add(Row(label, box));
        }
        void Choice(string name, string label, IReadOnlyList<string> items, int selected)
        {
            var combo = new ComboBox { ItemsSource = items, SelectedIndex = Math.Min(selected, items.Count - 1), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch, Name = "Modifier" + name };
            _inputs[name] = combo;
            _fields.Children.Add(Row(label, combo));
        }
        switch (Kind)
        {
            case "Lag":
                Choice("Leader", "Follow", others, 0);
                Box("Delay", "Frames behind", 5);
                Box("Strength", "Strength (0 to 1)", 1);
                var rot = new CheckBox { Content = "Its rotation too", Foreground = Look.Text, FontSize = 11, Name = "ModifierRotation" };
                _inputs["Rotation"] = rot;
                _fields.Children.Add(rot);
                break;
            case "Jitter":
                foreach (var a in new[] { "X", "Y", "Z" }) Box("Position" + a, $"Position {a} (m)", 0.1);
                foreach (var a in new[] { "H", "P", "B" }) Box("Rotation" + a, $"Rotation {a} (°)", 0);
                Box("Frequency", "Cycles a second", 2);
                Box("Seed", "Seed", 0);
                break;
            case "Cycle":
                Choice("Driver", "Driven by", others, 0);
                Choice("DriverChannel", "Its channel", SceneChannels.MotionNames, (int)Channel.Heading);
                Box("Low", "From value", 0);
                Box("High", "To value (one pass)", 360);
                break;
            case "Expression":
            {
                Choice("Channel", "Channel", SceneChannels.MotionNames, (int)Channel.PositionY);
                var formula = new TextBox { Text = "value + sin(time * 4) * 0.1", FontSize = 11, Name = "ModifierFormula" };
                _inputs["Formula"] = formula;
                _fields.Children.Add(Row("Formula", formula));
                _fields.Children.Add(new TextBlock
                {
                    Text = "frame, time, value (the channel before it), [Item.position.x], [Item.heading], [Item.world.y]…; sin cos (radians) deg rad sqrt abs min max clamp lerp if noise…",
                    Foreground = Look.Text, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                });
                break;
            }
            default:
                Choice("Channel", "Channel", SceneChannels.MotionNames, (int)Channel.PositionY);
                Box("Amplitude", "Amplitude", 0.5);
                Box("Period", "Period (frames)", 30);
                Box("Phase", "Phase (°)", 0);
                Box("Damping", "Damping (per frame)", 0);
                Box("Start", "From frame", Session.Scene.FirstFrame);
                break;
        }
    }

    private static Control Row(string label, Control input)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        g.Children.Add(new TextBlock { Text = label, Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(input, 1);
        g.Children.Add(input);
        return g;
    }

    private double Num(string name)
    {
        var text = ((TextBox)_inputs[name]).Text;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            throw new ArgumentException($"\"{text}\" isn't a number.");
        return v;
    }

    private SceneItem ItemOf(string name) =>
        ((ComboBox)_inputs[name]).SelectedItem is string n && Session.Scene.Items.FirstOrDefault(i => i != _item && i.Name == n) is { } it
            ? it : throw new ArgumentException("Choose the other item.");

    private Channel ChannelOf(string name) => (Channel)Math.Max(0, ((ComboBox)_inputs[name]).SelectedIndex);

    /// <summary>Adds a modifier of <see cref="Kind"/> from the fields, as one undo step; false (said why) if they won't do.</summary>
    public bool Add()
    {
        try
        {
            MotionModifier m = Kind switch
            {
                "Lag" => new LagModifier(ItemOf("Leader")) { Delay = Num("Delay"), Strength = Num("Strength"), Rotation = ((CheckBox)_inputs["Rotation"]).IsChecked == true },
                "Jitter" => new JitterModifier
                {
                    Position = new Vec3(Num("PositionX"), Num("PositionY"), Num("PositionZ")), Rotation = new Vec3(Num("RotationH"), Num("RotationP"), Num("RotationB")),
                    Frequency = Num("Frequency"), Seed = (int)Num("Seed"),
                },
                "Cycle" => new CycleModifier(ItemOf("Driver")) { DriverChannel = ChannelOf("DriverChannel"), Low = Num("Low"), High = Num("High") },
                "Expression" => new ExpressionModifier { Channel = ChannelOf("Channel"), Formula = ((TextBox)_inputs["Formula"]).Text ?? "" }.Checked(),
                _ => new OscillateModifier { Channel = ChannelOf("Channel"), Amplitude = Num("Amplitude"), Period = Num("Period"), Phase = Num("Phase"), Damping = Num("Damping"), Start = Num("Start") },
            };
            return Set([.. _item.Modifiers, m], $"added: {Describe(m)}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _said.Text = e.Message;
            return false;
        }
    }

    /// <summary>Takes the chosen modifier off.</summary>
    public bool Remove(int? index = null)
    {
        int i = index ?? _list.SelectedIndex;
        if (i < 0 || i >= _item.Modifiers.Count) { _said.Text = "Choose a modifier in the list."; return false; }
        var list = _item.Modifiers.ToList();
        list.RemoveAt(i);
        return Set(list, "removed one");
    }

    private bool Set(IReadOnlyList<MotionModifier> list, string what)
    {
        try
        {
            Session.SetModifiers(_item, list);
            _said.Text = $"{_item.Name}: {what}.";
            ShowList();
            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _said.Text = e.Message;
            return false;
        }
    }
}
