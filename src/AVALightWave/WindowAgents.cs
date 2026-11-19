using Avalonia.Threading;
using AVALightWave.Mcp;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Modeler.UI;
using AVAModeler.Scene;
using AVAModeler.Tools;

namespace AVALightWave;

/// <summary>
/// The window's live MCP door, as AVA Modeler's: while Options › Allow AI Agents is on, a <see cref="LiveListener"/>
/// on AVALightWave's own pipe takes <c>AVALightWave --mcp --attach</c> bridges, and every call runs on the UI thread
/// against the open scene (through the session, so Ctrl+Z undoes an agent's steps), waiting while a dialog box is open.
/// While Options › Allow AI agents on other computers is on, a <see cref="NetworkListener"/> takes MCP clients on the
/// network the same way, with the access token. The AI Chat's in-process server runs on a host of its own here too. Agents get AVALightWave's profile: the scene
/// tools, its own tools (with the hub, through <see cref="ILightWaveApp"/>) and its recipes.
/// </summary>
internal sealed class WindowAgents(SceneDocument document, Func<ObjectItem, Task<string>> editInModeler, Func<(bool, bool)> hubState, Action<string> status)
    : ILiveModeler, IUiThread, ILightWaveApp, IAsyncDisposable
{
    private LiveListener? _listener;

    /// <summary>
    /// The engine's command API wants a document; AVALightWave has no Modeler object, so it's an empty one nobody sees
    /// (the profile serves none of its modeling tools, add_object asks for a file, and making objects uses documents of its own).
    /// </summary>
    public Commands Commands { get; } = new(new Document());

    public LayoutSession Layout => document.Session;

    public ServerProfile Profile => LightWaveServer.Profile(this);

    public bool Running => _listener != null;

    public TimeSpan BusyWait { get; set; } = TimeSpan.FromSeconds(20);

    public string? Busy => Dialogs.ModalsOpen > 0 ? "a dialog box is open in AVALightWave" : null;

    public void Report(string text)
    {
        status(text);
        document.Session.NotifyChangedElsewhere();
    }

    public Task<T> InvokeAsync<T>(Func<T> work) => Dispatcher.UIThread.InvokeAsync(work).GetTask();

    public Task<string> EditInModeler(ObjectItem item) => Dispatcher.UIThread.InvokeAsync(() => editInModeler(item));

    public (bool OnHub, bool ModelerThere) HubState() => hubState();

    /// <summary>The window's render queue folder now (queue_render's default).</summary>
    public Func<string>? Queue { get; set; }

    public string? QueueFolder => Queue?.Invoke();

    /// <summary>The live host the AI Chat's server runs on; its status lines say Claude is working.</summary>
    public LiveHost ChatHost(string root) => new(this, new ChatReports(this), root) { BusyWait = BusyWait };

    private sealed class ChatReports(WindowAgents agents) : ILiveModeler
    {
        public Commands Commands => agents.Commands;
        public LayoutSession Layout => agents.Layout;
        public string? Busy => agents.Busy;
        public void Report(string text) => agents.Report(text.StartsWith("AI agent: ", StringComparison.Ordinal) ? "Claude is working… " + text["AI agent: ".Length..] : text);
    }

    /// <summary>Opens the door on a pipe; returns why it couldn't (another AVALightWave has it), or null.</summary>
    public string? Start(string pipeName)
    {
        if (_listener != null) return null;
        var listener = new LiveListener(pipeName, root => new LiveHost(this, this, root) { BusyWait = BusyWait }, Profile);
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() =>
            status(n > 0 ? $"AI agents connected: {n}. Their changes show here, and Ctrl+Z undoes them." : "The AI agent disconnected."));
        try
        {
            listener.Start();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ = listener.DisposeAsync();
            return e.Message;
        }
        _listener = listener;
        return null;
    }

    public async Task StopAsync()
    {
        var listener = _listener;
        _listener = null;
        if (listener == null) return;
        listener.Stop();
        await listener.DisposeAsync();
    }

    private NetworkListener? _network;

    /// <summary>Whether the network door is open.</summary>
    public bool NetworkRunning => _network != null;

    /// <summary>The settings and working folder the network door was opened with (to tell when they've changed).</summary>
    public NetworkSettings? NetworkSettings { get; private set; }

    public string? NetworkRoot { get; private set; }

    /// <summary>Where the network door listens, while it's open.</summary>
    public IReadOnlyList<string> NetworkUrls => _network?.Urls ?? [];

    /// <summary>The network door's port, while it's open (the one the system chose, for port 0).</summary>
    public int NetworkPort => _network?.Port ?? 0;

    /// <summary>
    /// Opens the network door: agents on other computers reach the open scene through the same live host as the pipe's,
    /// with AVALightWave's profile, working in <paramref name="root"/>. Returns why it couldn't open, or null.
    /// </summary>
    public async Task<string?> StartNetworkAsync(NetworkSettings settings, string root)
    {
        if (_network != null) return null;
        if (!Directory.Exists(root)) return $"the working folder {root} doesn't exist.";
        var profile = Profile;
        var listener = new NetworkListener(settings, profile.Name,
            o => ModelerServer.Configure(o, new LiveHost(this, this, root) { BusyWait = BusyWait }, profile), ModelerServer.EndSessionAsync);
        listener.ConnectionsChanged += n => Dispatcher.UIThread.Post(() =>
            status(n > 0 ? $"AI agents on the network: {n}. Their changes show here, and Ctrl+Z undoes them." : "No AI agent on the network is working just now."));
        try
        {
            await listener.StartAsync();
        }
        catch (IOException e)
        {
            return e.Message;
        }
        _network = listener;
        NetworkSettings = settings;
        NetworkRoot = root;
        return null;
    }

    public async Task StopNetworkAsync()
    {
        var listener = _network;
        _network = null;
        NetworkSettings = null;
        NetworkRoot = null;
        if (listener != null) await listener.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await StopNetworkAsync();
    }
}

/// <summary>
/// The network door's token and addresses for the Options panel: the token in its owner-only file beside the
/// preferences (only in memory for a window made for tests).
/// </summary>
internal sealed class NetworkDoor(WindowAgents agents, string? folder) : INetworkDoor
{
    private string? _memory;

    private NetworkToken? Store => folder == null ? null : NetworkToken.In(folder);

    public string Token => Store?.Get() ?? (_memory ??= NetworkListener.NewToken());

    public string RenewToken()
    {
        var token = Store?.Renew() ?? (_memory = NetworkListener.NewToken());
        Renewed?.Invoke();
        return token;
    }

    public IReadOnlyList<string> Urls => agents.NetworkUrls;

    /// <summary>Raised when the token changes, so an open door reopens with the new one.</summary>
    public event Action? Renewed;
}
