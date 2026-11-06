using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVAModeler.Modeler.UI;

namespace AVALightWave;

/// <summary>
/// Render › Render Queue: the jobs in the render queue's folder (the local queue, or a render farm folder the person
/// chooses), each with its frames done, its state and the nodes on it; Add Current Scene, Cancel, Up and Down, Remove
/// Finished, Start or Stop Local Rendering (a node in this process), and how to start render nodes elsewhere. A thin
/// window over <see cref="FarmFolder"/> and <see cref="RenderQueue"/>: every look at the folder runs on a worker, so a
/// slow network share never holds up the window; it looks again every second while open.
/// </summary>
public sealed class RenderQueueWindow : Window
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _folder = new() { Name = "QueueFolder", Foreground = Look.Text, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _command = new() { Name = "NodeCommand", IsReadOnly = true, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _first, _last, _step, _width, _height, _samples, _name, _jobName;
    private readonly ComboBox _movie = new() { Name = "QueueMovie", ItemsSource = FarmJobSpec.Movies, SelectedIndex = 0, MinWidth = 80 };
    private readonly ListBox _jobs = new() { Name = "QueueJobs", MinHeight = 160, Background = Look.PanelDark };
    private readonly TextBlock _detail = new() { Name = "QueueDetail", Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _nodes = new() { Name = "QueueNodes", Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "QueueStatus", Foreground = Look.ButtonOn, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly Button _local;
    private Task? _scan;
    private bool _showing;

    /// <summary>The jobs as last read, in the queue's order.</summary>
    public IReadOnlyList<FarmJobStatus> Jobs { get; private set; } = [];

    /// <summary>The nodes heard from lately, as last read.</summary>
    public IReadOnlyList<FarmNodeInfo> Nodes { get; private set; } = [];

    /// <summary>Asks for a farm folder (a folder dialog; tests give their own).</summary>
    public Func<Task<string?>> PickFolder { get; set; }

    public string Status => _status.Text ?? "";
    public string Detail => _detail.Text ?? "";
    public string NodesText => _nodes.Text ?? "";
    public string Command => _command.Text ?? "";

    /// <summary>The chosen job's id, if one is chosen.</summary>
    public string? SelectedId => _jobs.SelectedIndex >= 0 && _jobs.SelectedIndex < Jobs.Count ? Jobs[_jobs.SelectedIndex].Id : null;

    public RenderQueueWindow(MainWindow main)
    {
        _main = main;
        Title = "Render Queue";
        Width = 760;
        Height = 640;
        MinWidth = 520;
        Background = Look.Panel;
        PickFolder = async () =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a render farm folder (one every render node can reach)", AllowMultiple = false });
            return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        };

        var s = main.Session.Scene;
        var cam = s.CurrentCamera;
        _first = Box("QueueFirst", s.FirstFrame);
        _last = Box("QueueLast", s.LastFrame);
        _step = Box("QueueStep", Math.Max(1, s.FrameStep));
        _width = Box("QueueWidth", cam?.Width ?? 640);
        _height = Box("QueueHeight", cam?.Height ?? 480);
        _samples = Box("QueueSamples", 4);
        _name = new TextBox { Name = "QueueName", Text = "frame", FontSize = 12, MinWidth = 90 };
        _jobName = new TextBox { Name = "QueueJobName", Text = main.Document.Path != null ? Path.GetFileNameWithoutExtension(main.Document.Path) : "Scene", FontSize = 12, MinWidth = 120 };

        var folderRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        folderButtons.Children.Add(Look.MakeButton("Local Queue", () => _ = UseFolder(main.LocalQueueFolder), "The render queue on this computer"));
        folderButtons.Children.Add(Look.MakeButton("Farm Folder…", () => _ = ChooseFarm(), "A folder (on this computer or a network share) that render nodes on other machines watch"));
        DockPanel.SetDock(folderButtons, Dock.Right);
        folderRow.Children.Add(folderButtons);
        folderRow.Children.Add(_folder);

        var settings = new WrapPanel();
        foreach (var (label, box) in new (string, Control)[] { ("Job", _jobName), ("First", _first), ("Last", _last), ("Step", _step), ("Width", _width), ("Height", _height), ("Samples", _samples), ("Files", _name), ("Movie", _movie) })
        {
            var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 10, 4) };
            cell.Children.Add(new TextBlock { Text = label, Foreground = Look.Text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            cell.Children.Add(box);
            settings.Children.Add(cell);
        }
        settings.Children.Add(Look.MakeButton("Add Current Scene", () => _ = AddCurrentScene(), "Send a copy of the scene, as it is now, to the queue"));

        _local = Look.MakeButton("Start Local Rendering", () => _ = ToggleLocal(), "Render the queue's frames in this window's process, beside any other nodes");
        _local.Name = "QueueLocal";
        var actions = new WrapPanel();
        foreach (var b in new[]
        {
            Look.MakeButton("Cancel Job", () => _ = CancelSelected(), "Stop the chosen job: no more of its frames are rendered (those done stay)"),
            Look.MakeButton("Up", () => _ = Move(-1), "Render the chosen job sooner"),
            Look.MakeButton("Down", () => _ = Move(1), "Render the chosen job later"),
            Look.MakeButton("Remove Finished", () => _ = RemoveFinished(), "Delete the folders of jobs that are done, failed or cancelled (their frames too)"),
            _local,
        })
        {
            b.Margin = new Thickness(0, 0, 4, 4);
            actions.Children.Add(b);
        }

        _jobs.SelectionChanged += (_, _) => { if (!_showing) ShowDetail(); };
        var stack = new StackPanel { Spacing = 6, Margin = new Thickness(10) };
        stack.Children.Add(Heading("Folder"));
        stack.Children.Add(folderRow);
        stack.Children.Add(new TextBlock { Text = "Render nodes: on any machine that reaches this folder (the same path, or the share's path there), run", Foreground = Look.Text, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(_command);
        stack.Children.Add(Heading("Add a job"));
        stack.Children.Add(settings);
        stack.Children.Add(Heading("Jobs"));
        stack.Children.Add(_jobs);
        stack.Children.Add(actions);
        stack.Children.Add(_detail);
        stack.Children.Add(Heading("Render nodes"));
        stack.Children.Add(_nodes);
        stack.Children.Add(_status);
        Content = new ScrollViewer { Content = stack };

        ShowFolder();
        _timer.Tick += (_, _) => _ = RefreshAsync();
        Opened += (_, _) => { _timer.Start(); _ = RefreshAsync(); };
        Closed += (_, _) => _timer.Stop();
    }

    private static TextBox Box(string name, int value) => new() { Name = name, Text = value.ToString(CultureInfo.InvariantCulture), FontSize = 12, Width = 64 };

    private static TextBlock Heading(string text) => new() { Text = text, Foreground = Look.ButtonOn, FontSize = 13, FontWeight = FontWeight.Bold };

    private FarmFolder Farm => new(_main.QueueFolder);

    private void ShowFolder()
    {
        bool local = string.Equals(_main.QueueFolder, _main.LocalQueueFolder, StringComparison.Ordinal);
        _folder.Text = (local ? "Local queue: " : "Render farm: ") + _main.QueueFolder;
        _command.Text = Farm.NodeCommand;
        _local.Content = _main.LocalRendering ? "Stop Local Rendering" : "Start Local Rendering";
    }

    /// <summary>Works on another folder (the local queue or a farm folder): local rendering stops, the list is read again.</summary>
    public async Task UseFolder(string folder)
    {
        if (_main.LocalRendering) await _main.StopLocalRendering();
        _main.QueueFolder = folder;
        Jobs = [];
        Nodes = [];
        ShowFolder();
        Show(Jobs, Nodes);
        await RefreshAsync();
    }

    private async Task ChooseFarm()
    {
        if (await PickFolder() is { } folder) await UseFolder(folder);
    }

    /// <summary>Reads the folder on a worker and shows it (a read already under way is waited for instead).</summary>
    public Task RefreshAsync()
    {
        if (_scan is { IsCompleted: false }) return _scan;
        var farm = Farm;
        return _scan = Run();

        async Task Run()
        {
            IReadOnlyList<FarmJobStatus> jobs;
            IReadOnlyList<FarmNodeInfo> nodes;
            try { (jobs, nodes) = await Task.Run(() => (farm.Scan(), farm.Nodes())); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _status.Text = $"The folder can't be read: {e.Message}";
                return;
            }
            if (farm.Root != Farm.Root) return; // the folder changed meanwhile
            Show(jobs, nodes);
        }
    }

    private void Show(IReadOnlyList<FarmJobStatus> jobs, IReadOnlyList<FarmNodeInfo> nodes)
    {
        string? chosen = SelectedId;
        Jobs = jobs;
        Nodes = nodes;
        _showing = true;
        try
        {
            _jobs.ItemsSource = jobs.Select(Row).ToList();
            int i = chosen == null ? -1 : jobs.ToList().FindIndex(j => j.Id == chosen);
            _jobs.SelectedIndex = i;
        }
        finally { _showing = false; }
        ShowDetail();
        _nodes.Text = nodes.Count == 0 ? "None heard from in the last two minutes." : string.Join("\n", nodes.Select(n => n.Text));
        _local.Content = _main.LocalRendering ? "Stop Local Rendering" : "Start Local Rendering";
    }

    private static Control Row(FarmJobStatus j)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,120") };
        grid.Children.Add(new TextBlock { Text = j.Text, Foreground = Look.Text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var bar = new ProgressBar
        {
            Minimum = 0, Maximum = 1, Value = j.Fraction, Height = 8, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = j.State == FarmJobState.Failed ? Brushes.IndianRed : j.State == FarmJobState.Cancelled ? Brushes.Gray : Look.ButtonOn,
        };
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        return grid;
    }

    private void ShowDetail()
    {
        if (SelectedId is not { } id || Jobs.FirstOrDefault(j => j.Id == id) is not { } j)
        {
            _detail.Text = Jobs.Count == 0 ? "No jobs. Add Current Scene sends the scene as it is now." : "Choose a job to see where its frames go.";
            return;
        }
        var spec = j.Job;
        _detail.Text = $"{j.Name} ({j.Id}): " + (spec != null ? $"frames {spec.First} to {spec.Last} every {spec.Step}, {spec.Width}×{spec.Height}, {spec.Samples} rays a pixel. " : "") +
                       $"Frames go to {j.OutputFolder}" + (j.Reason != null ? $"\nWhy it failed: {j.Reason}" : "");
    }

    private int Number(TextBox box, string what)
    {
        if (int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
        throw new ArgumentException($"{what} must be a whole number.");
    }

    /// <summary>Add Current Scene: a copy of the scene (taken here, on the window's thread) sent on a worker. Returns the job, or null if it couldn't be sent.</summary>
    public async Task<FarmSubmitted?> AddCurrentScene()
    {
        FarmSubmit settings;
        AVAModeler.Scene.Scene snapshot;
        try
        {
            settings = new FarmSubmit
            {
                Name = _jobName.Text, First = Number(_first, "The first frame"), Last = Number(_last, "The last frame"), Step = Number(_step, "The step"),
                Width = Number(_width, "The width"), Height = Number(_height, "The height"), Samples = Number(_samples, "The samples"),
                Output = (_name.Text ?? "").Trim(), Movie = _movie.SelectedItem as string ?? "none",
            };
            snapshot = FarmFolder.Snapshot(_main.Session.Scene);
        }
        catch (ArgumentException e)
        {
            _status.Text = e.Message;
            return null;
        }
        string? sceneFolder = _main.Document.Path is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        var farm = Farm;
        _status.Text = "Sending the scene…";
        try
        {
            var sent = await Task.Run(() => farm.Submit(snapshot, settings, sceneFolder));
            _status.Text = $"Sent {sent.Job.Name} ({sent.Job.Frames.Count} frames)." + (sent.Warnings.Count > 0 ? " " + string.Join(" ", sent.Warnings) : "") +
                           (_main.LocalRendering || Nodes.Count > 0 ? "" : " No node is rendering this folder yet: Start Local Rendering, or start render nodes.");
            await RefreshAsync();
            return sent;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _status.Text = $"Couldn't send it: {e.Message}";
            return null;
        }
    }

    private async Task OnSelected(string what, Func<FarmFolder, string, string> work)
    {
        if (SelectedId is not { } id) { _status.Text = $"Choose a job to {what}."; return; }
        var farm = Farm;
        try { _status.Text = await Task.Run(() => work(farm, id)); }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        await RefreshAsync();
    }

    /// <summary>Cancels the chosen job.</summary>
    public Task CancelSelected() => OnSelected("cancel", (f, id) => { f.Cancel(id); return "Cancelled: no more of its frames will be rendered."; });

    /// <summary>Moves the chosen job up (negative) or down the queue.</summary>
    public Task Move(int direction) => OnSelected("move", (f, id) => f.Move(id, direction) ? "Moved." : "It can't go further that way.");

    /// <summary>Deletes the finished jobs' folders.</summary>
    public async Task RemoveFinished()
    {
        var farm = Farm;
        try
        {
            int n = await Task.Run(farm.RemoveFinished);
            _status.Text = n == 0 ? "No finished jobs to remove." : $"Removed {n} finished job{(n == 1 ? "" : "s")}.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _status.Text = e.Message; }
        await RefreshAsync();
    }

    /// <summary>Start or Stop Local Rendering.</summary>
    public async Task ToggleLocal()
    {
        if (_main.LocalRendering)
        {
            await _main.StopLocalRendering();
            _status.Text = "Local rendering stopped.";
        }
        else
        {
            _main.StartLocalRendering();
            _status.Text = "Rendering the queue here.";
        }
        ShowFolder();
        await RefreshAsync();
    }
}
