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
/// The numeric requester (N), as classic Layout had for typing exact values: the chosen item's position, rotation and
/// scale at the current frame. OK sets them by the keying rule (with Auto Key, keys at this frame) as one undo step;
/// a value that isn't a number is named and nothing changes.
/// </summary>
public sealed class NumericRequester : Window
{
    private readonly LayoutSession _session;
    private readonly SceneItem _item;
    private readonly TextBlock _problem = new() { Foreground = Look.ButtonOn, FontSize = 11, TextWrapping = TextWrapping.Wrap };

    /// <summary>The nine boxes: position X, Y, Z; heading, pitch, bank; scale X, Y, Z.</summary>
    public TextBox[] Boxes { get; } = new TextBox[9];

    public NumericRequester(LayoutSession session, SceneItem item)
    {
        _session = session;
        _item = item;
        Title = $"Numeric: {item.Name}";
        Width = 380;
        SizeToContent = SizeToContent.Height;
        Background = Look.Panel;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = $"{item.Kind}: {item.Name} at frame {session.Frame:0}" + (session.AutoKey ? " (Auto Key: keyed here)" : ""), Foreground = Look.Text, FontWeight = FontWeight.Bold });
        Row(panel, "Position (m)", ["X", "Y", "Z"], item.PositionAt(session.Frame), 0);
        Row(panel, "Rotation (°)", ["H", "P", "B"], item.RotationAt(session.Frame), 3);
        Row(panel, "Scale", ["X", "Y", "Z"], item.ScaleAt(session.Frame), 6);
        panel.Children.Add(_problem);
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        ok.Click += (_, _) => { if (Apply()) Close(); };
        cancel.Click += (_, _) => Close();
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        Content = panel;
    }

    private void Row(StackPanel panel, string label, string[] axes, Vec3 v, int first)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = Look.Text, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*,Auto,*") };
        for (int i = 0; i < 3; i++)
        {
            var name = new TextBlock { Text = axes[i], Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            var box = Boxes[first + i] = new TextBox { Text = v[i].ToString("0.####", CultureInfo.InvariantCulture), FontSize = 12 };
            Grid.SetColumn(name, 2 * i);
            Grid.SetColumn(box, 2 * i + 1);
            grid.Children.Add(name);
            grid.Children.Add(box);
        }
        panel.Children.Add(grid);
    }

    /// <summary>Sets what's typed as one undo step; false (with the reason shown) if a value isn't a number.</summary>
    public bool Apply()
    {
        var v = new double[9];
        for (int i = 0; i < 9; i++)
            if (!double.TryParse(Boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !double.IsFinite(v[i]))
            {
                _problem.Text = $"\"{Boxes[i].Text}\" isn't a number.";
                return false;
            }
        var p = new Vec3(v[0], v[1], v[2]);
        var r = new Vec3(v[3], v[4], v[5]);
        var s = new Vec3(v[6], v[7], v[8]);
        if (p == _item.PositionAt(_session.Frame) && r == _item.RotationAt(_session.Frame) && s == _item.ScaleAt(_session.Frame)) return true;
        _session.Edit($"Numeric {_item.Name}", () =>
        {
            if (p != _item.PositionAt(_session.Frame)) _session.SetVector(_item, 0, p);
            if (r != _item.RotationAt(_session.Frame)) _session.SetVector(_item, 3, r);
            if (s != _item.ScaleAt(_session.Frame)) _session.SetVector(_item, 6, s);
        });
        return true;
    }
}
