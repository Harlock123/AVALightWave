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
/// The AI Chat's in-process server runs on a host of its own here too. Agents get AVALightWave's profile: the scene
/// tools, its own tools (with the hub, through <see cref="ILightWaveApp"/>) and its recipes.
/// </summary>
internal sealed class WindowAgents(SceneDocument document, Func<ObjectItem, Task<string>> editInModeler, Func<(bool, bool)> hubState, Action<string> status)
    : ILiveModeler, IUiThread, ILightWaveApp, IAsyncDisposable
{
    private LiveListener? _listener;

    /// <summary>
    /// The engine's command API wants a document; AVALightWave has no Modeler object, so it's an empty one nobody sees
    /// (the profile serves no modeling tools, and add_object asks for a file).
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
