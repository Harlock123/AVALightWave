using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace AVALightWave.UI.Tests;

/// <summary>Opening a test window (its own settings, saving nowhere; no key, no hub, no agents) and working it.</summary>
internal static class Ui
{
    public static MainWindow Open(int width = 1400, int height = 880)
    {
        var w = new MainWindow([], new AVAModeler.Modeler.UI.Preferences(), new LightWaveOptions(), forTests: true) { Width = width, Height = height };
        w.Show();
        Pump();
        return w;
    }

    public static void Pump()
    {
        for (int i = 0; i < 12; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps until a task is done.</summary>
    public static T Wait<T>(Task<T> task)
    {
        for (int i = 0; i < 20000 && !task.IsCompleted; i++) { Pump(); Thread.Sleep(1); }
        Assert.True(task.IsCompleted, "It didn't finish.");
        Pump();
        return task.GetAwaiter().GetResult();
    }

    public static void Wait(Task task) => Wait(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return 0; }, TaskScheduler.Default));

    public static void Until(Func<bool> done, string what)
    {
        for (int i = 0; i < 5000 && !done(); i++) { Pump(); Thread.Sleep(1); }
        Assert.True(done(), what);
    }

    public static Point In(Window w, Control c, Point p) => c.TranslatePoint(p, w)!.Value;

    public static void Click(Window w, Point at, MouseButton button = MouseButton.Left)
    {
        w.MouseMove(at);
        w.MouseDown(at, button);
        w.MouseUp(at, button);
        Pump();
    }

    public static void Key(Window w, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        w.KeyPress(key, mods, PhysicalKey.None, null);
        w.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    /// <summary>A named control anywhere in a control's logical tree.</summary>
    public static T Find<T>(Control root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>().First(c => c.Name == name);

    /// <summary>Types into a box and presses Enter.</summary>
    public static void Type(Window w, TextBox box, string text)
    {
        Pump();
        Assert.True(box.Focus(), $"{box.Name} couldn't take the focus.");
        box.Text = text;
        Pump();
        Key(w, Avalonia.Input.Key.Enter);
    }
}
