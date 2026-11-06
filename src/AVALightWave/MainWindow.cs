using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AVALightWave.Mcp;
using AVAModeler.Chat;
using AVAModeler.Chat.UI;
using AVAModeler.Formats;
using AVAModeler.Hub;
using AVAModeler.Layout;
using AVAModeler.Layout.UI;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>What shows under the view: the graph editor, the dope sheet, the Scene Editor, or nothing.</summary>
public enum BottomTab { GraphEditor, DopeSheet, SceneEditor, None }

/// <summary>
/// AVALightWave's window, a standalone Layout in the classic spirit: the menus and the row of panel buttons (Scene,
/// Objects, Bones, Lights, Camera, Surfaces, Effects, Render, Options) across the top; on the left the edit mode
/// (Objects, Bones, Lights, Cameras), the Selected Item, the mouse modes (Move, Rotate, Size, Stretch) with their axes,
/// Numeric and the keys; the view in the middle; the open panel on the right (and the AI Chat beside it, F4); and the
/// timeline and transport with the graph editor, dope sheet or Scene Editor at the bottom. It works on its own scene
/// document (<see cref="SceneDocument"/>), and links with AVA Modeler through the hub.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly bool _forTests;
    private readonly Dictionary<string, LayoutPanel> _panels = [];
    private readonly ScrollViewer _panelHost = new() { Width = 300, IsVisible = false, Background = Look.Panel };
    private readonly DockPanel _body = new();
    private readonly ComboBox _itemChooser = new() { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, Name = "SelectedItem" };
    private readonly Dictionary<EditMode, Button> _modeButtons = [];
    private readonly Dictionary<ItemDrag, Button> _dragButtons = [];
    private readonly Dictionary<DragAxes, Button> _axisButtons = [];
    private readonly Dictionary<string, Button> _panelButtons = [];
    private readonly Dictionary<BottomTab, Button> _bottomButtons = [];
    private readonly ContentControl _bottomHost = new() { Height = 210 };
    private readonly Slider _timeline = new() { Minimum = 1, Maximum = 60, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1 };
    private readonly TextBox _frameBox = new() { Width = 64, FontSize = 12, Name = "FrameBox" };
    private readonly TextBlock _status = new() { Foreground = Look.ButtonOn, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _hubLight = new() { Foreground = Look.Text, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
    private readonly CheckBox _autoKey = new() { Content = "Auto Key", Foreground = Look.Text, Margin = new Thickness(6, 0) };
    private readonly MenuItem _undo = new(), _redo = new(), _recentScenes = new() { Header = "Recent _Scenes" }, _recentObjects = new() { Header = "Recent O_bjects" };
    private readonly DispatcherTimer _player = new();
    private readonly DispatcherTimer _autosaveTimer = new();
    private readonly DispatcherTimer _hubTimer = new();
    private readonly LazyToolBridge _chatTools;
    private EditMode _mode = EditMode.Objects;
    private BottomTab _bottom = BottomTab.GraphEditor;
    private bool _refreshing, _chatOpen, _closingConfirmed;

    public SceneDocument Document { get; }
    public LayoutSession Session => Document.Session;
    /// <summary>The active pane's view (the one the view keys, the toolbar's view list and the View menu work on).</summary>
    public SceneView View => Viewports.Active.View;

    /// <summary>The view layout: one, two or four panes, each a view of its own type.</summary>
    public ViewportLayout Viewports { get; }
    public GraphEditor Graph { get; }
    public DopeSheetPanel DopeSheet { get; }
    public SceneEditor SceneEditor { get; }
    public KeyMarks Marks { get; }

    /// <summary>The settings shared with the engine (the AI Chat's, agents', the hub's, recent scenes).</summary>
    internal AVAModeler.Modeler.UI.Preferences Preferences { get; }

    /// <summary>AVALightWave's own settings.</summary>
    internal LightWaveOptions Options { get; }

    /// <summary>The hub: the live link with AVA Modeler.</summary>
    internal LightWaveHub Hub { get; }

    /// <summary>AI agents' live access (MCP) and the chat's host.</summary>
    internal WindowAgents Agents { get; }

    /// <summary>The AI Chat panel (F4).</summary>
    internal ChatPanel Chat { get; }

    /// <summary>The pipe agents reach the window on (tests give their own).</summary>
    internal string AgentPipe { get; set; } = LightWaveServer.PipeName;

    /// <summary>The chat's working folder (the Documents folder).</summary>
    internal string ChatRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) is { Length: > 0 } docs
        ? docs : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The scene's auto-save and crash recovery (none in a window made for tests unless a test gives one).</summary>
    internal SceneAutosave? Autosave { get; set; }
    /// <summary>The local render queue's folder (in the app-data folder; a test window's is a temporary one of its own).</summary>
    internal string LocalQueueFolder { get; }

    /// <summary>The render queue's folder now: the local queue, or a render farm folder chosen in the Render Queue window.</summary>
    internal string QueueFolder
    {
        get => _queueFolder;
        set => _queueFolder = Path.GetFullPath(value);
    }

    private string _queueFolder;
    private RenderQueue? _queue;
    private RenderQueueWindow? _queueWindow;

    /// <summary>How the local render node works (tests give small timings; null: the defaults).</summary>
    internal FarmNodeOptions? LocalNodeOptions { get; set; }

    /// <summary>Whether this window is rendering the queue's frames itself.</summary>
    internal bool LocalRendering => _queue?.Running == true;

    /// <summary>Asks where to open or save a file (tests give their own: title, suggested name, save?).</summary>
    internal Func<string, string?, bool, Task<string?>>? PickFile { get; set; }

    public MainWindow(string[] args) : this(args, AVAModeler.Modeler.UI.Preferences.Load(LightWaveOptions.PreferencesPath), LightWaveOptions.Load(), forTests: false) { }

    /// <summary>For tests: settings of their own (saving nowhere), no key, no history, no hub or agents unless they start them.</summary>
    public MainWindow(string[] args, AVAModeler.Modeler.UI.Preferences prefs, LightWaveOptions options, bool forTests)
    {
        _forTests = forTests;
        Preferences = prefs;
        Options = options;
        Document = new SceneDocument();
        Session.AutoKey = options.AutoKey;
        Viewports = new ViewportLayout(Session);
        Viewports.ForEachView(v => v.CanPick = i => _mode.Includes(i));
        ArrangeViews(ViewportLayouts.LayoutOf(options));
        Viewports.PaneViewChanged += pane =>
        {
            if (_arranging) return;
            ViewportLayouts.Remember(Options, Viewports.Kind, pane.Index, pane.View.Mode.ToString());
            Options.Save();
        };
        Graph = new GraphEditor(Session);
        DopeSheet = new DopeSheetPanel(Session);
        SceneEditor = new SceneEditor(Document);
        Marks = new KeyMarks(Session) { Height = 14 };

        Hub = new LightWaveHub(Document, work => Dispatcher.UIThread.InvokeAsync(work).GetTask()) { ModelerSetting = options.ModelerPath };
        Hub.Said += SetStatus;
        Hub.StatusChanged += () => Dispatcher.UIThread.Post(() => _hubLight.Text = Hub.Status.Text);
        _hubLight.Name = "HubStatus";
        if (!forTests) Hub.SettingsFolder = LightWaveOptions.Folder;
        else
        {
            // A window made for tests never reaches a real AVA Modeler or starts one: tests give pipes of their own.
            Hub.ModelerPipe = "avalightwave-test-nowhere-" + Guid.NewGuid().ToString("N");
            Hub.Launcher = new AppLauncher(HubProtocol.Modeler) { Start = _ => false };
        }
        Agents = new WindowAgents(Document, EditInModeler, () => (Hub.Running, Hub.Running && Hub.ModelerThere()), SetStatus);
        // The render queue: the person's own in the app-data folder; a test window's somewhere of its own (made only if used).
        LocalQueueFolder = forTests ? Path.Combine(Path.GetTempPath(), "avalightwave-queue-" + Guid.NewGuid().ToString("N")) : FarmFolder.DefaultQueue;
        _queueFolder = LocalQueueFolder;
        Agents.Queue = () => QueueFolder;

        // The AI Chat: the same panel as AVA Modeler's, on AVALightWave's server, prompt and words. Tests never see a key.
        var keys = forTests ? new ApiKeyStore(null, null) : ApiKeyStore.ForApp(LightWaveOptions.Folder);
        var serverKeys = forTests ? new ApiKeyStore(null, null) : ApiKeyStore.ForServer(LightWaveOptions.Folder);
        _chatTools = new LazyToolBridge(async cancel => await McpToolBridge.StartAsync(Agents.ChatHost(ChatRoot), Agents.Profile, cancel));
        Chat = new ChatPanel(_chatTools, keys, SetStatus, prefs, serverKeys);
        Chat.Session.SystemPrompt = LightWaveChat.System;
        Chat.Session.CoreTools = LightWaveChat.CoreTools;
        Chat.Session.CoreToolsNote = LightWaveChat.CoreToolsNote;
        Chat.Wording = new ChatWording("AVALightWave", "stage, light or animate something",
            "\"put the pawn on the floor with a spotlight\", \"orbit the camera round it and render the first frame\"",
            "\"add a spotlight above the pawn\", \"key the camera moving in\"",
            "Ctrl+Z undoes its steps.");
        Chat.CloseRequested += ToggleChat;
        if (!forTests)
        {
            Chat.History = new ChatHistory(LightWaveOptions.ChatsFolder);
            Chat.RestoreLast();
        }

        Title = "AVALightWave";
        Width = 1400;
        Height = 900;
        MinWidth = 900;
        MinHeight = 600;
        Background = Look.Panel;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AVALightWave/Assets/AVALightWave.png"))); }
        catch (Exception e) when (e is IOException or InvalidOperationException or FileNotFoundException) { }

        foreach (var p in new LayoutPanel[]
        {
            new ScenePanel(this), new ItemsPanel(this, EditMode.Objects), new ItemsPanel(this, EditMode.Bones), new ItemsPanel(this, EditMode.Lights),
            new ItemsPanel(this, EditMode.Cameras), new SurfacesPanel(this), new ImagesPanel(this), new EffectsPanel(this), new RenderPanel(this), new OptionsPanel(this),
        }) _panels[p.Title] = p;

        Content = BuildLayout();
        ShowBottom(BottomTab.GraphEditor);

        Session.Changed += Refresh;
        Session.History.Changed += UpdateUndoItems;
        Session.SceneReplaced += () => { Viewports.FitAll(); Mode = _mode; };
        Session.ChangedElsewhere += () =>
        {
            RefreshItems();
            if (CurrentPanel is ItemsPanel items && !items.IsKeyboardFocusWithin) items.Reshow();
        };
        _timeline.ValueChanged += (_, _) => { if (!_refreshing) Session.SetFrame(Math.Round(_timeline.Value)); };
        _frameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && double.TryParse(_frameBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) Session.SetFrame(Math.Round(f));
        };
        _itemChooser.SelectionChanged += (_, _) =>
        {
            if (_refreshing || _itemChooser.SelectedItem is not ItemPropertiesPanel.ItemChoice c) return;
            Session.Select(c.Item);
        };
        _autoKey.IsChecked = Session.AutoKey;
        _autoKey.IsCheckedChanged += (_, _) => Session.AutoKey = _autoKey.IsChecked == true;
        _player.Tick += (_, _) =>
        {
            var s = Session.Scene;
            Session.SetFrame(Session.Frame + 1 > s.LastFrame ? s.FirstFrame : Session.Frame + 1);
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(Viewports.FitAll, DispatcherPriority.Background);
        Mode = EditMode.Objects;
        DragMode = ItemDrag.Move;
        RefreshRecent();
        Refresh();
        UpdateUndoItems();

        if (!forTests)
        {
            Autosave = new SceneAutosave(SceneAutosave.DefaultFolder);
            // The hub light: whether AVA Modeler is there, looked at every few seconds (a moment's connection, off the UI thread).
            _hubTimer.Interval = TimeSpan.FromSeconds(3);
            _hubTimer.Tick += async (_, _) => await Hub.CheckPresenceAsync();
            _hubTimer.Start();
            _autosaveTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, options.AutosaveMinutes));
            _autosaveTimer.Tick += async (_, _) => await AutosaveNow();
            if (options.AutosaveMinutes > 0) _autosaveTimer.Start();
            Opened += async (_, _) => await Started(args);
            Closing += Closing_;
            SyncHub();
            SyncAgents();
        }
        Closed += (_, _) =>
        {
            _player.Stop();
            _autosaveTimer.Stop();
            _hubTimer.Stop();
            _preview?.Close();
            _ = StopLocalRendering();
            Chat.Stop();
            Chat.SaveHistory();
            _ = _chatTools.DisposeAsync().AsTask();
            _ = Agents.StopAsync();
            _ = Hub.StopAsync();
        };
    }

    // ───────────────────────────── the layout ─────────────────────────────

    private Control BuildLayout()
    {
        var top = new StackPanel { Background = Look.PanelDark, Children = { BuildMenu(), PanelBar() } };
        DockPanel.SetDock(top, Dock.Top);
        _body.Children.Add(top);

        var bottom = new StackPanel { Background = Look.PanelDark, Children = { TimelineRow(), BottomTabs(), _bottomHost, StatusRow() } };
        DockPanel.SetDock(bottom, Dock.Bottom);
        _body.Children.Add(bottom);

        var left = new ScrollViewer { Content = LeftColumn(), Width = 170, Background = Look.PanelDark };
        DockPanel.SetDock(left, Dock.Left);
        _body.Children.Add(left);

        DockPanel.SetDock(_panelHost, Dock.Right);
        _body.Children.Add(_panelHost);
        _body.Children.Add(Viewports);
        return _body;
    }

    private Control PanelBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 2) };
        foreach (var name in _panels.Keys)
        {
            var b = Look.MakeButton(name, () => ShowPanel(OpenPanel == name ? null : name), $"The {name} panel", 82);
            _panelButtons[name] = b;
            bar.Children.Add(b);
        }
        bar.Children.Add(new TextBlock { Text = "View", Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 4, 0) });
        // The active pane's view type.
        var mode = new ComboBox { ItemsSource = Enum.GetNames<LayoutViewMode>(), SelectedItem = View.Mode.ToString(), Width = 120, Name = "ViewMode" };
        mode.SelectionChanged += (_, _) => { if (Enum.TryParse<LayoutViewMode>(mode.SelectedItem as string, out var m)) View.Mode = m; };
        Viewports.PaneViewChanged += pane => { if (pane == Viewports.Active) mode.SelectedItem = View.Mode.ToString(); };
        Viewports.ActiveChanged += () => mode.SelectedItem = View.Mode.ToString();
        bar.Children.Add(mode);
        var shaded = new CheckBox { Content = "Shaded", IsChecked = View.Shaded, Foreground = Look.Text, Margin = new Thickness(8, 0) };
        shaded.IsCheckedChanged += (_, _) => { Viewports.ForEachView(v => v.Shaded = shaded.IsChecked == true); Viewports.InvalidateViews(); };
        bar.Children.Add(shaded);
        bar.Children.Add(Look.MakeButton("Fit", Viewports.FitAll, "Frame the scene in every view (A)"));
        var layout = new ComboBox { ItemsSource = LayoutNames, SelectedIndex = (int)Viewports.Kind, Width = 120, Name = "ViewLayout", Margin = new Thickness(8, 0, 0, 0) };
        ToolTip.SetTip(layout, "One view, two side by side, two stacked, or four (Ctrl+1 to Ctrl+4)");
        layout.SelectionChanged += (_, _) => { if (layout.SelectedIndex >= 0) ViewLayout = (ViewportLayoutKind)layout.SelectedIndex; };
        ViewLayoutChanged += () => layout.SelectedIndex = (int)Viewports.Kind;
        bar.Children.Add(layout);
        return bar;
    }

    private Control LeftColumn()
    {
        var col = new StackPanel { Spacing = 3, Margin = new Thickness(6) };
        TextBlock Head(string t) => new() { Text = t, FontWeight = FontWeight.Bold, Foreground = Look.Text, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        col.Children.Add(Head("Edit"));
        foreach (var (m, label, key) in new[] { (EditMode.Objects, "Objects", "Shift+O"), (EditMode.Bones, "Bones", "Shift+B"), (EditMode.Lights, "Lights", "Shift+L"), (EditMode.Cameras, "Cameras", "Shift+C") })
        {
            var b = Look.MakeButton(label, () => Mode = m, $"Work on {label.ToLowerInvariant()} ({key})", 150);
            _modeButtons[m] = b;
            col.Children.Add(b);
        }
        col.Children.Add(Head("Selected Item"));
        col.Children.Add(_itemChooser);
        col.Children.Add(new TextBlock { Text = "↑ ↓ choose the next", Foreground = Look.Text, Opacity = 0.6, FontSize = 10 });
        col.Children.Add(Head("Mouse"));
        foreach (var (d, label, key) in new[] { (ItemDrag.Move, "Move", "T"), (ItemDrag.Rotate, "Rotate", "Y"), (ItemDrag.Size, "Size", "H"), (ItemDrag.Stretch, "Stretch", "Shift+H") })
        {
            var b = Look.MakeButton(label, () => DragMode = d, $"{label} the chosen item with the mouse ({key}); the right button works the third axis", 150);
            _dragButtons[d] = b;
            col.Children.Add(b);
        }
        var axes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (a, label) in new[] { (DragAxes.X, "X"), (DragAxes.Y, "Y"), (DragAxes.Z, "Z") })
        {
            var b = Look.MakeButton(label, () => ToggleAxis(a), $"Let the mouse change {label} (heading, pitch, bank when rotating)", 48);
            _axisButtons[a] = b;
            axes.Children.Add(b);
        }
        col.Children.Add(axes);
        col.Children.Add(Look.MakeButton("Numeric…", () => OpenNumeric(), "Type the chosen item's position, rotation and scale (N)", 150));
        col.Children.Add(Head("Keys"));
        col.Children.Add(Look.MakeButton("Create Key", Session.CreateKey, "Key every channel of the chosen item at this frame (Enter)", 150));
        col.Children.Add(Look.MakeButton("Delete Key", Session.DeleteKey, "Remove the chosen item's keys at this frame (Delete)", 150));
        col.Children.Add(_autoKey);
        col.Children.Add(Head("Hub"));
        col.Children.Add(Look.MakeButton("Edit in Modeler", () => { if (Session.Selected is ObjectItem o) _ = EditInModeler(o); else SetStatus("Choose an object to edit in AVA Modeler."); }, "Open the chosen object in AVA Modeler; its saves come back here (Ctrl+E)", 150));
        return col;
    }

    private Control TimelineRow()
    {
        var row = new DockPanel { Margin = new Thickness(6, 2) };
        var transport = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        transport.Children.Add(Look.MakeButton("|◀", () => Session.SetFrame(Session.Scene.FirstFrame), "First frame (Home)"));
        transport.Children.Add(Look.MakeButton("◀", () => Session.SetFrame(Session.Frame - 1), "Back a frame (←)"));
        transport.Children.Add(Look.MakeButton("Play", TogglePlay, "Play / stop (Space)"));
        transport.Children.Add(Look.MakeButton("▶", () => Session.SetFrame(Session.Frame + 1), "On a frame (→)"));
        transport.Children.Add(Look.MakeButton("▶|", () => Session.SetFrame(Session.Scene.LastFrame), "Last frame (End)"));
        DockPanel.SetDock(transport, Dock.Right);
        var frame = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        frame.Children.Add(new TextBlock { Text = "Frame", Foreground = Look.Text, VerticalAlignment = VerticalAlignment.Center });
        frame.Children.Add(_frameBox);
        DockPanel.SetDock(frame, Dock.Left);
        row.Children.Add(transport);
        row.Children.Add(frame);
        row.Children.Add(new StackPanel { Margin = new Thickness(6, 0), Children = { _timeline, Marks } });
        return row;
    }

    private Control BottomTabs()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 0, 6, 2) };
        foreach (var (tab, label, key) in new[] { (BottomTab.GraphEditor, "Graph Editor", "G"), (BottomTab.DopeSheet, "Dope Sheet", "D"), (BottomTab.SceneEditor, "Scene Editor", "E"), (BottomTab.None, "Hide", "") })
        {
            var b = Look.MakeButton(label, () => ShowBottom(tab), key.Length > 0 ? $"{label} ({key})" : "Give the view the room", 100);
            _bottomButtons[tab] = b;
            bar.Children.Add(b);
        }
        return bar;
    }

    private Control StatusRow()
    {
        var row = new DockPanel { Margin = new Thickness(0, 2) };
        DockPanel.SetDock(_hubLight, Dock.Right);
        row.Children.Add(_hubLight);
        row.Children.Add(_status);
        return row;
    }

    // ───────────────────────────── modes, panels, the bottom ─────────────────────────────

    /// <summary>What the mouse works on (classic Layout's Edit buttons): the view picks only those, and Selected Item lists them.</summary>
    public EditMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            foreach (var (m, b) in _modeButtons) Look.SetOn(b, m == value);
            // The chosen item is always one of the mode's, if it has any (as classic Layout's Selected Item was).
            if (Session.Selected is not { } s || !value.Includes(s)) Session.Select(Session.Scene.ItemsOf(value).FirstOrDefault());
            RefreshItems();
            Viewports.InvalidateViews();
        }
    }

    private void SetModeOnly(EditMode mode)
    {
        _mode = mode;
        foreach (var (m, b) in _modeButtons) Look.SetOn(b, m == mode);
        RefreshItems();
    }

    /// <summary>What dragging does (Move, Rotate, Size, Stretch).</summary>
    public ItemDrag DragMode
    {
        get => View.DragMode;
        set
        {
            Viewports.ForEachView(v => v.DragMode = value);
            foreach (var (d, b) in _dragButtons) Look.SetOn(b, d == value);
            foreach (var (a, b) in _axisButtons) Look.SetOn(b, View.Axes.HasFlag(a));
        }
    }

    /// <summary>Lets the mouse change an axis, or stops it (at least one stays on).</summary>
    public void ToggleAxis(DragAxes axis)
    {
        var next = View.Axes ^ axis;
        if (next != DragAxes.None) Viewports.ForEachView(v => v.Axes = next);
        foreach (var (a, b) in _axisButtons) Look.SetOn(b, View.Axes.HasFlag(a));
    }

    // ───────────────────────────── the view layout ─────────────────────────────

    /// <summary>The layouts' names in the View menu and the toolbar, in <see cref="ViewportLayoutKind"/>'s order.</summary>
    public static readonly string[] LayoutNames = ["Single", "Side by Side", "Stacked", "Quad"];

    private bool _arranging;

    internal event Action? ViewLayoutChanged;

    /// <summary>How the middle is split (View › Layout, Ctrl+1 to Ctrl+4); remembered in lightwave.json with each pane's view.</summary>
    public ViewportLayoutKind ViewLayout
    {
        get => Viewports.Kind;
        set
        {
            if (value == Viewports.Kind) return;
            ArrangeViews(value);
            Options.ViewLayout = value.ToString();
            Options.Save();
            ViewLayoutChanged?.Invoke();
        }
    }

    private void ArrangeViews(ViewportLayoutKind kind)
    {
        var views = ViewportLayouts.ViewsOf(Options, kind).Select(v => Enum.Parse<LayoutViewMode>(v)).ToList();
        _arranging = true;
        try { Viewports.Arrange(kind, views); }
        finally { _arranging = false; }
    }

    /// <summary>The open panel's name, or null.</summary>
    public string? OpenPanel { get; private set; }

    public LayoutPanel? CurrentPanel => OpenPanel != null ? _panels[OpenPanel] : null;

    public IReadOnlyDictionary<string, LayoutPanel> Panels => _panels;

    /// <summary>Opens a panel on the right (null closes it). The Objects, Bones, Lights and Camera panels set the edit mode.</summary>
    public void ShowPanel(string? name)
    {
        OpenPanel = name;
        foreach (var (n, b) in _panelButtons) Look.SetOn(b, n == name);
        _panelHost.IsVisible = name != null;
        _panelHost.Content = name != null ? _panels[name] : null;
        if (name != null)
        {
            var mode = name switch { "Objects" => EditMode.Objects, "Bones" => EditMode.Bones, "Lights" => EditMode.Lights, "Camera" => EditMode.Cameras, _ => (EditMode?)null };
            if (mode is { } m && m != _mode) Mode = m;
            _panels[name].Refresh();
        }
        PlaceChat();
    }

    /// <summary>Shows the graph editor, the dope sheet or the Scene Editor under the view (or none).</summary>
    public void ShowBottom(BottomTab tab)
    {
        _bottom = tab;
        foreach (var (t, b) in _bottomButtons) Look.SetOn(b, t == tab && tab != BottomTab.None);
        _bottomHost.Content = tab switch { BottomTab.GraphEditor => Graph, BottomTab.DopeSheet => DopeSheet, BottomTab.SceneEditor => SceneEditor, _ => null };
        _bottomHost.IsVisible = tab != BottomTab.None;
        if (tab == BottomTab.DopeSheet)
        {
            DopeSheet.Sheet.Prune();
            DopeSheet.View.InvalidateMeasure();
            DopeSheet.View.InvalidateVisual();
        }
        if (tab == BottomTab.SceneEditor) SceneEditor.View.Refresh();
    }

    public BottomTab Bottom => _bottom;

    /// <summary>Shows or hides the AI Chat (F4).</summary>
    internal void ToggleChat()
    {
        _chatOpen = !_chatOpen;
        PlaceChat();
        if (_chatOpen)
        {
            Chat.RefreshIntro();
            Chat.FocusInput();
        }
    }

    internal bool ChatOpen => _chatOpen;

    private void PlaceChat()
    {
        if (Chat.Parent is Panel old) old.Children.Remove(Chat);
        if (!_chatOpen) return;
        DockPanel.SetDock(Chat, Dock.Right);
        // Beside the open panel (or the view's right edge), after the top and bottom bars and the left column.
        _body.Children.Insert(Math.Min(3, _body.Children.Count), Chat);
    }

    // ───────────────────────────── refreshing ─────────────────────────────

    private int _itemsShown = -1;

    /// <summary>The object chosen last (the Bones panel adds bones to it, as classic Layout's current object).</summary>
    public ObjectItem? CurrentObject => _lastObject is { } o && o.Scene == Session.Scene ? o : null;
    private ObjectItem? _lastObject;

    private void Refresh()
    {
        if (Session.Selected is ObjectItem chosenObject) _lastObject = chosenObject;
        _refreshing = true;
        var s = Session.Scene;
        _timeline.Minimum = s.FirstFrame;
        _timeline.Maximum = Math.Max(s.FirstFrame + 1, s.LastFrame);
        _timeline.Value = Session.Frame;
        if (!_frameBox.IsKeyboardFocusWithin) _frameBox.Text = Session.Frame.ToString("0", CultureInfo.InvariantCulture);
        _status.Text = Session.Status;
        _refreshing = false;
        // Choosing an item of another kind (in a panel, the Scene Editor, by an agent) switches to its edit mode.
        if (Session.Selected is { } chosen && !_mode.Includes(chosen)) SetModeOnly(EditModes.Of(chosen));
        else if (s.Items.Count != _itemsShown) RefreshItems();
        else SelectInChooser();
        _hubLight.Text = Hub.Status.Text;
        Marks.InvalidateVisual();
        Viewports.InvalidateViews();
        CurrentPanel?.Refresh();
        UpdateTitle();
    }

    private void RefreshItems()
    {
        _itemsShown = Session.Scene.Items.Count;
        _refreshing = true;
        _itemChooser.ItemsSource = Session.Scene.ItemsOf(_mode).Select(i => new ItemPropertiesPanel.ItemChoice(i)).ToList();
        _refreshing = false;
        SelectInChooser();
        if (_bottom == BottomTab.SceneEditor) SceneEditor.View.Refresh();
    }

    private void SelectInChooser()
    {
        _refreshing = true;
        var items = _itemChooser.ItemsSource as List<ItemPropertiesPanel.ItemChoice> ?? [];
        _itemChooser.SelectedIndex = items.FindIndex(c => c.Item == Session.Selected);
        _refreshing = false;
    }

    /// <summary>The Selected Item list as shown.</summary>
    public IReadOnlyList<SceneItem> ChooserItems => (_itemChooser.ItemsSource as List<ItemPropertiesPanel.ItemChoice> ?? []).Select(c => c.Item).ToList();

    private void UpdateTitle() => Title = $"AVALightWave — {Document.Title}";

    private void UpdateUndoItems()
    {
        var h = Session.History;
        _undo.Header = h.UndoLabel is { } u ? "_Undo " + u.Replace("_", "__") : "_Undo";
        _redo.Header = h.RedoLabel is { } r ? "_Redo " + r.Replace("_", "__") : "_Redo";
        _undo.IsEnabled = h.CanUndo;
        _redo.IsEnabled = h.CanRedo;
        UpdateTitle();
    }

    public MenuItem UndoItem => _undo;

    public void SetStatus(string text) => Session.SetStatus(text);

    public string Status => Session.Status;

    // ───────────────────────────── the menus ─────────────────────────────

    private Menu BuildMenu()
    {
        MenuItem Item(string header, Action action, string? gesture = null)
        {
            var i = new MenuItem { Header = header };
            i.Click += (_, _) => action();
            if (gesture != null) i.InputGesture = KeyGesture.Parse(gesture);
            return i;
        }
        MenuItem Group(string header, params object[] items) => new() { Header = header, ItemsSource = items.Select(x => x is string ? (object)new Separator() : x).ToList() };
        _undo.Click += (_, _) => Undo();
        _redo.Click += (_, _) => Redo();
        _undo.InputGesture = new KeyGesture(Key.Z, KeyModifiers.Control);
        _redo.InputGesture = new KeyGesture(Key.Z, KeyModifiers.Control | KeyModifiers.Shift);
        LayoutViewMode[] views = [LayoutViewMode.Camera, LayoutViewMode.Perspective, LayoutViewMode.Top, LayoutViewMode.Front, LayoutViewMode.Side, LayoutViewMode.Light];
        return new Menu
        {
            Background = Look.PanelDark,
            Foreground = Look.Text,
            ItemsSource = new[]
            {
                Group("_File",
                    Item("_New Scene", () => _ = NewScene(), "Ctrl+N"), Item("_Load Scene…", () => _ = LoadSceneDialog(), "Ctrl+O"), _recentScenes,
                    Item("Load _from Scene…", () => _ = LoadFromSceneDialog()),
                    Item("_Save Scene", () => _ = SaveScene(false), "Ctrl+S"), Item("Save Scene _As…", () => _ = SaveScene(true), "Ctrl+Shift+S"), "-",
                    Item("Load _Object…", () => _ = LoadObjectDialog()), _recentObjects,
                    Item("_Replace Object…", () => { if (Session.Selected is ObjectItem o) _ = ReplaceObjectDialog(o); else SetStatus("Choose an object to replace."); }),
                    Item("Save Ob_ject", () => { if (Session.Selected is ObjectItem o) _ = SaveObject(o, false); else SetStatus("Choose an object to save."); }),
                    Item("Save Object As…", () => { if (Session.Selected is ObjectItem o) _ = SaveObject(o, true); else SetStatus("Choose an object to save."); }),
                    Item("Save All Ob_jects", SaveAllObjects), "-",
                    Item("Export _glTF…", () => _ = ExportGltf()), "-", Item("_Quit", Close, "Ctrl+Q")),
                Group("_Edit", _undo, _redo, "-", Item("_Numeric…", () => OpenNumeric(), "N"), Item("Create _Key", Session.CreateKey), Item("_Delete Key", Session.DeleteKey), "-",
                    Item("_Options…", () => ShowPanel("Options"))),
                Group("_Items",
                    Item("Add _Null", () => Session.Add(new NullItem("Null"))),
                    Item("Add _Distant Light", () => Session.Add(new LightItem("Distant Light"))),
                    Item("Add _Point Light", () => Session.Add(new LightItem("Point Light", LightType.Point))),
                    Item("Add _Spot Light", () => Session.Add(new LightItem("Spot Light", LightType.Spot))),
                    Item("Add _Area Light", () => Session.Add(new LightItem("Area Light", LightType.Area))),
                    Item("Add _Camera", () => Session.Add(new CameraItem("Camera"))), "-",
                    Item("C_lone Item…", () => OpenClone()),
                    Item("_Motion Options…", () => OpenMotionOptions()),
                    Item("Load Mo_tion…", () => { if (Session.Selected is { } i) _ = LoadMotionDialog(i); else SetStatus("Choose an item to load a motion onto."); }),
                    Item("Sa_ve Motion…", () => { if (Session.Selected is { } i) _ = SaveMotionDialog(i); else SetStatus("Choose an item whose motion to save."); }),
                    Item("S_kelegons → Bones", () => SkelegonsToBones((Session.Selected as Bone)?.Owner ?? Session.Selected as ObjectItem ?? CurrentObject)),
                    Item("_Remove Item", () => { if (Session.Selected is { } i) Session.Remove(i); })),
                Group("_View", views.Select((v, n) => (object)Item($"_{n + 1} {v}", () => View.Mode = v)).Concat(new object[]
                {
                    "-", Item("_Fit", Viewports.FitAll, "A"),
                    Group("_Layout", Enum.GetValues<ViewportLayoutKind>().Select(k => (object)Item("_" + LayoutNames[(int)k], () => ViewLayout = k, $"Ctrl+{(int)k + 1}")).ToArray()),
                    Item("_Graph Editor", () => ShowBottom(BottomTab.GraphEditor)), Item("_Dope Sheet", () => ShowBottom(BottomTab.DopeSheet)),
                    Item("_Scene Editor", () => ShowBottom(BottomTab.SceneEditor)),
                }).ToArray()),
                Group("_Panels", _panels.Keys.Select(n => (object)Item("_" + n, () => ShowPanel(n))).ToArray()),
                Group("_Render", Item("Render _Frame", Render, "F9"), Item("Render _Animation…", () => RenderAnimation(), "F10"), "-",
                    Item("Make _Preview…", () => MakePreview(), "Shift+P"), Item("Render _Queue…", () => OpenRenderQueue())),
                Group("_Hub", Item("_Edit in Modeler", () => { if (Session.Selected is ObjectItem o) _ = EditInModeler(o); else SetStatus("Choose an object to edit in AVA Modeler."); }, "Ctrl+E"),
                    Item("_Link with AVA Modeler…", () => ShowPanel("Options"))),
                Group("_AI", Item("_Chat with Claude…", ToggleChat, "F4"), Item("Allow AI _Agents…", () => ShowPanel("Options"))),
                Group("_Help", Item("_Layout", () => _ = Dialogs.Message(this, "AVALightWave", Help)), Item("_Keyboard", () => _ = Dialogs.Message(this, "Keyboard", Keyboard)),
                    Item("_About", () => _ = Dialogs.Message(this, "About AVALightWave", About))),
            },
        };
    }

    public const string About = """
        AVALightWave 0.1.0

        A re-imagining of classic LightWave Layout (versions 4 and 5), the scene, animation and render program that
        shipped beside LightWave Modeler, built on the engine of AVA Modeler. Inspired by the original; not affiliated
        with NewTek or LightWave Digital. Copyright (c) 2026 Lonnie Watson, MIT licence.
        """;

    public const string Help = """
        AVALightWave stages, animates and renders scenes. Load objects with File › Load Object (LightWave .lwo, .obj, .geo,
        .glb, .gltf, .stl); a scene holds as many as you like. Add lights, cameras, nulls and bones from the Items menu or
        the panels.

        The Edit buttons on the left choose what the mouse works on: objects (and nulls), bones, lights or cameras. Click
        an item in the view, or choose it in Selected Item (the arrow keys step through them). The Mouse buttons say what
        dragging does: Move, Rotate, Size or Stretch, with X, Y and Z limiting the axes; the right button works the third
        axis. Numeric (N) types exact values. With Auto Key on, each change keys the frame; Create Key keys every channel.

        The panels along the top: Scene (frames and files), Objects, Bones, Lights and Camera (the items and their
        settings), Surfaces (an object's colours and shading), Effects (backdrop, ambient occlusion, environment), Render
        and Options. Under the view, the graph editor, the dope sheet or the Scene Editor (hierarchy, visibility, locks
        and keys).

        Items › Clone Item makes copies of the chosen item, each moved, turned, sized and delayed from the one before; File
        › Load from Scene brings another scene's items (with their hierarchy and motion) into this one. The scene is
        auto-saved every few minutes while it has changes, and offered back if AVALightWave didn't close properly.

        Edit in Modeler (Ctrl+E) opens the chosen object in AVA Modeler; saving it there updates it here, keeping its
        motion. AVA Modeler's File › Send to AVALightWave puts its object in this scene.

        View › Layout (Ctrl+1 to Ctrl+4) splits the middle into one view, two side by side, two stacked or four, each with
        its own view type (the list in its corner); the last one clicked is the active view, which 1 to 6 change.
        Render › Make Preview (Shift+P) plays a quick wireframe or shaded flipbook of the frames and saves it as PNGs, an
        MP4 or a GIF.

        Ctrl+Z undoes any change to the scene, an AI agent's included. F9 renders a frame, F10 an animation, F4 opens the
        AI Chat. Scenes save as LightWave .lws (objects by their files) or .avascene (everything).
        """;

    public const string Keyboard = """
        Shift+O, Shift+B, Shift+L, Shift+C   edit objects, bones, lights, cameras
        ↑ ↓   the next or previous item          ← →   a frame back or on      Home, End   the first or last frame
        T Move, Y Rotate, H Size, Shift+H Stretch      N Numeric      Enter Create Key      Delete Delete Key
        1 to 6   the active view: Camera, Perspective, Top, Front, Side, Light      A fit every view      Space play
        Ctrl+1 to Ctrl+4   one view, two side by side, two stacked, four      Shift+P Make Preview
        G graph editor, D dope sheet, E Scene Editor
        Ctrl+N new scene, Ctrl+O load, Ctrl+S save, Ctrl+Shift+S save as, Ctrl+E Edit in Modeler
        Ctrl+Z undo, Ctrl+Shift+Z or Ctrl+Y redo, F4 AI Chat, F9 render a frame, F10 render an animation
        """;

    // ───────────────────────────── the keyboard ─────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        e.Handled = HandleKey(e.Key, e.KeyModifiers, e.Source is TextBox);
    }

    /// <summary>A key pressed in the window (not typed in a text box, except the function keys); returns whether it was used.</summary>
    public bool HandleKey(Key key, KeyModifiers mods, bool inTextBox = false)
    {
        switch (key)
        {
            case Key.F4 when mods == KeyModifiers.None: ToggleChat(); return true;
            case Key.F9: Render(); return true;
            case Key.F10: RenderAnimation(); return true;
        }
        if (inTextBox) return false;
        if (_bottom == BottomTab.DopeSheet && DopeSheet.IsKeyboardFocusWithin && DopeSheet.HandleKey(new KeyEventArgs { Key = key, KeyModifiers = mods })) return true;
        bool ctrl = mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta);
        bool shift = mods.HasFlag(KeyModifiers.Shift);
        if (ctrl)
        {
            switch (key)
            {
                case Key.Z when !shift: Undo(); return true;
                case Key.Z: case Key.Y: Redo(); return true;
                case Key.N: _ = NewScene(); return true;
                case Key.O: _ = LoadSceneDialog(); return true;
                case Key.S: _ = SaveScene(shift); return true;
                case Key.Q: Close(); return true;
                case Key.E: if (Session.Selected is ObjectItem o) _ = EditInModeler(o); return true;
                case >= Key.D1 and <= Key.D4 when !shift: ViewLayout = (ViewportLayoutKind)(key - Key.D1); return true;
            }
            return false;
        }
        if (shift)
        {
            switch (key)
            {
                case Key.O: Mode = EditMode.Objects; return true;
                case Key.B: Mode = EditMode.Bones; return true;
                case Key.L: Mode = EditMode.Lights; return true;
                case Key.C: Mode = EditMode.Cameras; return true;
                case Key.H: DragMode = ItemDrag.Stretch; return true;
                case Key.P: MakePreview(); return true;
            }
            return false;
        }
        if (mods != KeyModifiers.None) return false;
        switch (key)
        {
            case Key.T: DragMode = ItemDrag.Move; return true;
            case Key.Y: DragMode = ItemDrag.Rotate; return true;
            case Key.H: DragMode = ItemDrag.Size; return true;
            case Key.N: OpenNumeric(); return true;
            case Key.Enter: Session.CreateKey(); return true;
            case Key.Delete: Session.DeleteKey(); return true;
            case Key.Left: Session.SetFrame(Session.Frame - 1); return true;
            case Key.Right: Session.SetFrame(Session.Frame + 1); return true;
            case Key.Home: Session.SetFrame(Session.Scene.FirstFrame); return true;
            case Key.End: Session.SetFrame(Session.Scene.LastFrame); return true;
            case Key.Up: Session.Select(Session.Scene.Step(_mode, Session.Selected, -1)); return true;
            case Key.Down: Session.Select(Session.Scene.Step(_mode, Session.Selected, 1)); return true;
            case Key.Space: TogglePlay(); return true;
            case Key.A: Viewports.FitAll(); return true;
            case Key.G: ShowBottom(BottomTab.GraphEditor); return true;
            case Key.D: ShowBottom(_bottom == BottomTab.DopeSheet ? BottomTab.GraphEditor : BottomTab.DopeSheet); return true;
            case Key.E: ShowBottom(_bottom == BottomTab.SceneEditor ? BottomTab.GraphEditor : BottomTab.SceneEditor); return true;
            case >= Key.D1 and <= Key.D6:
                View.Mode = new[] { LayoutViewMode.Camera, LayoutViewMode.Perspective, LayoutViewMode.Top, LayoutViewMode.Front, LayoutViewMode.Side, LayoutViewMode.Light }[key - Key.D1];
                return true;
        }
        return false;
    }

    public void Undo() => AfterStep(Session.Undo());

    public void Redo() => AfterStep(Session.Redo());

    private void AfterStep(string? label)
    {
        if (label == null) return;
        RefreshItems();
        if (CurrentPanel is ItemsPanel items) items.Reshow();
        else CurrentPanel?.Refresh();
    }

    private void TogglePlay()
    {
        if (_player.IsEnabled) { _player.Stop(); return; }
        _player.Interval = TimeSpan.FromSeconds(1 / Math.Max(1, Session.Scene.FramesPerSecond));
        _player.Start();
    }

    /// <summary>Numeric (N): the chosen item's numbers in a requester.</summary>
    public NumericRequester? OpenNumeric()
    {
        if (Session.Selected is not { } item) { SetStatus("Choose an item first."); return null; }
        var w = new NumericRequester(Session, item);
        if (_forTests) w.Show(this);
        else _ = Dialogs.ShowModal(w, this);
        return w;
    }

    /// <summary>Clone Item: the requester for copies of the chosen item (not a bone: its object brings it).</summary>
    public CloneRequester? OpenClone()
    {
        if (Session.Selected is not { } item) { SetStatus("Choose an item to clone."); return null; }
        if (item is Bone b) { SetStatus($"{item.Name} is a bone: clone its object, {b.Owner.Name}, and its bones come with it."); return null; }
        var w = new CloneRequester(Document, item);
        if (_forTests) w.Show(this);
        else _ = Dialogs.ShowModal(w, this);
        return w;
    }

    /// <summary>Motion Options for the chosen item: its motion modifiers and its motion file.</summary>
    public MotionOptions? OpenMotionOptions()
    {
        if (Session.Selected is not { } item) { SetStatus("Choose an item for its motion options."); return null; }
        var w = new MotionOptions(this, item);
        if (_forTests) w.Show(this);
        else _ = Dialogs.ShowModal(w, this);
        return w;
    }

    private static readonly FilePickerFileType MotionFiles = new("LightWave motion") { Patterns = ["*" + MotionFile.Extension] };

    /// <summary>Load Motion: replaces an item's nine channels with a motion file's keys, as one undo step.</summary>
    public async Task LoadMotionDialog(SceneItem item)
    {
        var path = await Pick($"Load a motion onto {item.Name}", null, false, MotionFiles);
        if (path != null) LoadMotion(item, path);
    }

    public bool LoadMotion(SceneItem item, string path)
    {
        try
        {
            var r = Session.LoadMotion(item, path);
            SetStatus($"Loaded {Path.GetFileName(path)} onto {item.Name}." + (r.Warnings.Count > 0 ? " " + string.Join(" ", r.Warnings) : ""));
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"Can't load {Path.GetFileName(path)}: {e.Message}");
            return false;
        }
    }

    /// <summary>Save Motion: an item's nine channels as a LightWave motion file.</summary>
    public async Task SaveMotionDialog(SceneItem item)
    {
        var path = await Pick($"Save {item.Name}'s motion", item.Name + MotionFile.Extension, true, MotionFiles);
        if (path == null) return;
        try
        {
            var warnings = Session.SaveMotion(item, path);
            SetStatus($"Saved {item.Name}'s motion as {Path.GetFileName(path)}." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Can't save the motion: {e.Message}");
        }
    }

    /// <summary>Skelegons → Bones for an object, in one step (one undo step): a bone for every skelegon in its file.</summary>
    public IReadOnlyList<Bone> SkelegonsToBones(ObjectItem? owner)
    {
        if (owner == null) { SetStatus("Choose an object to make bones from its skelegons."); return []; }
        var made = Session.Edit("Skelegons → Bones", () => Skelegons.CreateBones(Session.Scene, owner));
        if (made.Count > 0)
        {
            Mode = EditMode.Bones;
            Session.Select(made[0]);
        }
        SetStatus(made.Count > 0 ? $"Made {made.Count} bones from {owner.Name}'s skelegons." : $"{owner.Name} has no skelegons (2-point polygons on a \"Skelegon…\" surface).");
        return made;
    }
    private PreviewWindow? _preview;

    /// <summary>
    /// Render › Make Preview (Shift+P): a quick flipbook of the scene's frames drawn as the view draws them, through the
    /// scene's camera (or the active view if there's no camera), in a window of its own; one at a time.
    /// </summary>
    public PreviewWindow MakePreview()
    {
        _player.Stop();
        if (_preview is { IsVisible: true })
        {
            _preview.Activate();
            return _preview;
        }
        var s = Session.Scene;
        var settings = new PreviewSettings
        {
            First = s.FirstFrame, Last = Math.Max(s.FirstFrame, s.LastFrame), Step = Math.Max(1, s.FrameStep),
            Width = 480, Shaded = View.Shaded,
        };
        var w = _preview = new PreviewWindow(Session, settings, ActiveViewCamera, PickFile)
        {
            PlayWhenMade = !_forTests,
        };
        w.Closed += (_, _) => { if (_preview == w) _preview = null; };
        w.Show(this);
        w.Make();
        return w;
    }

    /// <summary>The active pane's view as it is now, to make a preview through (null for a Camera view: the scene camera).</summary>
    private AVAModeler.Rendering.ViewCamera? ActiveViewCamera() => View.Mode == LayoutViewMode.Camera ? null : View.Camera;

    public void Render()
    {
        _player.Stop();
        var w = new RenderWindow(Session);
        w.Show(this);
    }

    private AnimationWindow? _animation;

    public AnimationWindow RenderAnimation()
    {
        _player.Stop();
        if (_animation is { IsVisible: true })
        {
            _animation.Activate();
            return _animation;
        }
        var w = _animation = new AnimationWindow(Session);
        w.Closed += (_, _) => { if (_animation == w) _animation = null; };
        w.Show(this);
        return w;
    }

    /// <summary>Render › Render Queue: the queue's (or farm's) jobs, nodes and local rendering.</summary>
    public RenderQueueWindow OpenRenderQueue()
    {
        _player.Stop();
        if (_queueWindow is { IsVisible: true })
        {
            _queueWindow.Activate();
            return _queueWindow;
        }
        var w = _queueWindow = new RenderQueueWindow(this);
        w.Closed += (_, _) => { if (_queueWindow == w) _queueWindow = null; };
        w.Show(this);
        return w;
    }

    /// <summary>Starts a render node in this process on the queue's folder (it keeps going when the Render Queue window closes).</summary>
    internal void StartLocalRendering()
    {
        if (LocalRendering) return;
        var queue = _queue = new RenderQueue(QueueFolder);
        queue.Said += line => Dispatcher.UIThread.Post(() => SetStatus(line));
        queue.Start(LocalNodeOptions);
    }

    /// <summary>Stops this process's render node (the frame under way is given back to the queue).</summary>
    internal async Task StopLocalRendering()
    {
        var queue = _queue;
        _queue = null;
        if (queue != null) await queue.StopAsync();
    }

    // ───────────────────────────── files ─────────────────────────────

    private static readonly FilePickerFileType Scenes = new("Scenes") { Patterns = ["*.lws", "*" + SceneJson.Extension] };
    private static readonly FilePickerFileType Lws = new("LightWave scene") { Patterns = ["*.lws"] };
    private static readonly FilePickerFileType AvaScene = new("AVA scene (everything)") { Patterns = ["*" + SceneJson.Extension] };

    private async Task<string?> Pick(string title, string? suggested, bool save, params FilePickerFileType[] types)
    {
        if (PickFile != null) return await PickFile(title, suggested, save);
        return save ? await Dialogs.Save(this, title, suggested ?? "scene", types) : await Dialogs.Open(this, title, types);
    }

    /// <summary>Asks for a file to open (panels use it; tests give their own through <see cref="PickFile"/>).</summary>
    internal Task<string?> PickToOpen(string title, params FilePickerFileType[] types) => Pick(title, null, false, types);

    private async Task<bool> Confirm(string title, string text, string yes, string no) =>
        _forTests || await Dialogs.Ask(this, title, text, yes, no);

    /// <summary>File › New Scene, asking first if the scene has unsaved changes.</summary>
    public async Task NewScene()
    {
        if (Document.Modified && !await Confirm("New scene?", "The scene has changes that haven't been saved. Start a new one anyway?", "New scene", "Keep working")) return;
        Document.New();
        SetStatus("A new scene.");
    }

    public async Task LoadSceneDialog()
    {
        if (Document.Modified && !await Confirm("Load a scene?", "The scene has changes that haven't been saved. Load another anyway?", "Load", "Keep working")) return;
        var path = await Pick("Load a scene", null, false, Scenes);
        if (path != null) await LoadScene(path);
    }

    /// <summary>Loads a scene, showing what couldn't be had.</summary>
    public async Task LoadScene(string path)
    {
        try
        {
            Document.Load(path);
            Preferences.AddRecent(path);
            Preferences.Save();
            RefreshRecent();
            if (Session.Warnings.Count > 0 && !_forTests) await Dialogs.Message(this, "Loaded with warnings", string.Join("\n", Session.Warnings));
        }
        catch (Exception e) when (e is IOException or FormatException or System.Text.Json.JsonException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            SetStatus($"Can't load {Path.GetFileName(path)}: {e.Message}");
            if (!_forTests) await Dialogs.Message(this, "Can't load that scene", e.Message);
        }
    }

    /// <summary>File › Save Scene (as: asks where, .lws or .avascene).</summary>
    public async Task SaveScene(bool askName)
    {
        var path = Document.Path;
        if (askName || path == null)
        {
            path = await Pick("Save the scene", Path.GetFileNameWithoutExtension(path ?? "scene") + ".lws", true, Lws, AvaScene);
            if (path == null) return;
        }
        try
        {
            var notes = Document.Save(path);
            Preferences.AddRecent(path);
            Preferences.Save();
            RefreshRecent();
            UpdateTitle();
            if (notes.Count > 0) SetStatus($"Saved {Path.GetFileName(path)}. " + string.Join(" ", notes));
            if (notes.Count > 0 && !_forTests) await Dialogs.Message(this, "Saved", string.Join("\n", notes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Can't save: {e.Message}");
            if (!_forTests) await Dialogs.Message(this, "Can't save", e.Message);
        }
    }

    /// <summary>File › Load from Scene: another scene's items into this one (asks which kinds first).</summary>
    public async Task LoadFromSceneDialog()
    {
        var path = await Pick("Load items from a scene", null, false, Scenes);
        if (path != null) OpenLoadFromScene(path);
    }

    /// <summary>The Load from Scene requester for a scene file.</summary>
    public LoadFromSceneRequester OpenLoadFromScene(string path)
    {
        var w = new LoadFromSceneRequester(Document, path);
        if (_forTests) w.Show(this);
        else _ = Dialogs.ShowModal(w, this);
        return w;
    }

    /// <summary>Adds a morph target to an object from a file the person picks (same points, same order).</summary>
    public async Task AddMorphFromFile(ObjectItem obj)
    {
        var path = await Pick($"A morph target for {obj.Name}", null, false, Dialogs.Objects);
        if (path == null) return;
        try
        {
            var m = MorphTargets.AddFromFile(Session, obj, path);
            SetStatus($"{obj.Name} can morph toward {m.Name}: key its weight (morph.{m.Name}).");
            if (CurrentPanel is ItemsPanel items) items.Reshow();
        }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            SetStatus($"Can't use {Path.GetFileName(path)} as a morph target: {e.Message}");
        }
    }

    public async Task LoadObjectDialog()
    {
        var path = await Pick("Load an object", null, false, Dialogs.Objects);
        if (path != null) LoadObject(path);
    }

    /// <summary>Loads an object file into the scene and chooses it (edit mode Objects).</summary>
    public ObjectItem? LoadObject(string path)
    {
        try
        {
            var item = Document.LoadObject(path);
            Options.AddRecentObject(path);
            Options.Save();
            RefreshRecent();
            Mode = EditMode.Objects;
            Session.Select(item);
            SetStatus($"Loaded {Path.GetFileName(path)}: {item.BaseMesh().Points.Count} points, {item.BaseMesh().Polygons.Count} polygons.");
            return item;
        }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"Can't load {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    public async Task ReplaceObjectDialog(ObjectItem item)
    {
        var path = await Pick($"Replace {item.Name} with", null, false, Dialogs.Objects);
        if (path == null) return;
        try { Document.ReplaceObject(item, path); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or InvalidDataException)
        {
            SetStatus($"Can't replace {item.Name}: {e.Message}");
        }
    }

    /// <summary>Saves an object to its file (or, asked or with none, where the person says).</summary>
    public async Task SaveObject(ObjectItem item, bool askName)
    {
        var path = item.SourcePath ?? item.Document?.FilePath;
        if (askName || path == null || !ObjectFiles.SaveExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            path = await Pick($"Save {item.Name}", (item.Name.Length > 0 ? item.Name : "object") + ".lwo", true, Dialogs.Lwo, Dialogs.ObjFile, Dialogs.Glb);
            if (path == null) return;
        }
        try { Document.SaveObject(item, path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Can't save {item.Name}: {e.Message}");
        }
    }

    public void SaveAllObjects()
    {
        var notes = Document.SaveAllObjects();
        SetStatus(notes.Count == 0 ? "Every object is saved." : string.Join(" ", notes));
    }

    public async Task ExportGltf()
    {
        var path = await Pick("Export the scene as glTF", Path.GetFileNameWithoutExtension(Document.Path ?? "scene") + ".glb", true, Dialogs.Glb, Dialogs.GltfFile);
        if (path == null) return;
        try
        {
            var warnings = SceneGltf.Save(Session.Scene, path);
            SetStatus($"Exported {Path.GetFileName(path)}." + (warnings.Count > 0 ? " " + string.Join(" ", warnings) : ""));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            SetStatus($"Can't export: {e.Message}");
        }
    }

    private void RefreshRecent()
    {
        MenuItem Open(string path, Action<string> act)
        {
            var i = new MenuItem { Header = path.Replace("_", "__") };
            i.Click += (_, _) => act(path);
            return i;
        }
        _recentScenes.ItemsSource = Preferences.RecentFiles.Count == 0 ? [new MenuItem { Header = "(none)", IsEnabled = false }]
            : Preferences.RecentFiles.Select(p => Open(p, f => _ = LoadScene(f))).ToList();
        _recentObjects.ItemsSource = Options.RecentObjects.Count == 0 ? [new MenuItem { Header = "(none)", IsEnabled = false }]
            : Options.RecentObjects.Select(p => Open(p, f => LoadObject(f))).ToList();
    }

    // ───────────────────────────── the hub and agents ─────────────────────────────

    /// <summary>Edit in Modeler: the object to AVA Modeler (started if need be); its saves come back here.</summary>
    public async Task<string> EditInModeler(ObjectItem item)
    {
        Hub.ModelerSetting = Options.ModelerPath;
        if (!Hub.Running && !_forTests) SyncHub();
        var said = await Hub.EditInModeler(item);
        SetStatus(said);
        return said;
    }

    /// <summary>Joins or leaves the hub to match Options.</summary>
    internal void SyncHub()
    {
        if (Preferences.HubEnabled && !Hub.Running)
        {
            if (Hub.Start() is { } problem) SetStatus($"Not on the hub: {problem}");
        }
        else if (!Preferences.HubEnabled && Hub.Running) _ = Hub.StopAsync();
        _hubLight.Text = Hub.Status.Text;
    }

    /// <summary>Opens or closes the door to AI agents to match Options.</summary>
    internal void SyncAgents()
    {
        if (Preferences.AllowAgents && !Agents.Running)
            SetStatus(Agents.Start(AgentPipe) is { } problem
                ? $"AI agents can't connect: {problem}"
                : "AI agents may connect (MCP): set them up with \"AVALightWave --mcp --attach\".");
        else if (!Preferences.AllowAgents && Agents.Running)
        {
            _ = Agents.StopAsync();
            SetStatus("AI agents can no longer connect.");
        }
    }

    // ───────────────────────────── starting and closing ─────────────────────────────

    private async Task Started(string[] args)
    {
        bool recovered = await OfferRecovery();
        Autosave?.Start();
        if (recovered || args.FirstOrDefault(File.Exists) is not { } path) return;
        if (Path.GetExtension(path).ToLowerInvariant() is ".lws" or SceneJson.Extension) await LoadScene(path);
        else if (LoadObject(path) is { } item && AppLauncher.LinkArgument(args) is { } link)
        {
            // Started by AVA Modeler's Send to AVALightWave: its later sends and saves update this object.
            Document.Link(item, link);
        }
    }

    private async void Closing_(object? sender, WindowClosingEventArgs e)
    {
        if (_closingConfirmed || !Document.Modified && Document.UnsavedObjects.All(o => o.Document?.Modified != true)) { CleanExit(); return; }
        e.Cancel = true;
        if (await Dialogs.Ask(this, "Quit?", "The scene or its objects have changes that haven't been saved. Quit anyway?", "Quit", "Keep working"))
        {
            _closingConfirmed = true;
            Close();
        }
    }

    /// <summary>
    /// If AVALightWave didn't close properly last time and left an auto-save, offers it back; returns whether it was
    /// recovered (the scene then untitled, its changes unsaved).
    /// </summary>
    internal async Task<bool> OfferRecovery()
    {
        if (Autosave?.Waiting() is not { } info) return false;
        var name = info.Scene != null ? Path.GetFileName(info.Scene) : "an untitled scene";
        if (!await Confirm("Recover your work?", $"AVALightWave didn't close properly last time. An auto-save of {name} from {info.Saved:g} is waiting. Open it?", "Open it", "No thanks"))
            return false;
        try
        {
            Autosave.Recover(Document);
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            SetStatus($"The auto-save couldn't be read: {e.Message}");
            return false;
        }
    }

    /// <summary>Writes the auto-save now if the scene has unsaved changes (the timer calls it every few minutes).</summary>
    internal async Task AutosaveNow()
    {
        if (Autosave is { } a && await a.SaveAsync(Document)) SetStatus($"Auto-saved at {DateTime.Now:t}.");
    }

    private void CleanExit()
    {
        _autosaveTimer.Stop();
        Autosave?.Finish();
        Options.Save();
        Preferences.Save();
    }
}

/// <summary>The strip under the timeline: a mark at every frame the chosen item has a key.</summary>
public sealed class KeyMarks(LayoutSession session) : Control
{
    public override void Render(DrawingContext dc)
    {
        var s = session.Scene;
        double first = s.FirstFrame, last = Math.Max(s.LastFrame, s.FirstFrame + 1);
        dc.FillRectangle(new SolidColorBrush(Color.FromRgb(50, 50, 54)), new Rect(Bounds.Size));
        if (session.Selected is not { } item) return;
        double inset = 10, width = Math.Max(1, Bounds.Width - 2 * inset);
        foreach (double f in SceneOutline.Keys(item, s))
        {
            double x = inset + (f - first) / (last - first) * width;
            dc.FillRectangle(f == session.Frame ? Brushes.White : Brushes.Gold, new Rect(x - 2, 2, 4, Bounds.Height - 4));
        }
    }
}
