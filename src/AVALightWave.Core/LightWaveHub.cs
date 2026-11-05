using System.Text.Json.Nodes;
using AVAModeler.Formats;
using AVAModeler.Hub;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// AVALightWave's end of the hub, the live link with AVA Modeler, as the original package's hub joined its Layout and
/// its modeler. It listens on its own per-user pipe (only this user's programs can open it; no network):
/// <c>object_saved</c> (AVA Modeler saved an object: the scene's copies of it are read again and put in place, keeping
/// their motion, as one undo step) and <c>add_object</c> (Send to AVALightWave: a new object in the scene, or the linked
/// one updated). <see cref="EditInModeler"/> sends <c>edit_object</c> the other way, starting AVA Modeler with the object
/// if it isn't on the hub. Messages are answered on the app's UI thread (<c>onUi</c>). Either app works alone: when the
/// other isn't there, nothing waits more than <see cref="Timeout"/>.
/// </summary>
public sealed class LightWaveHub(SceneDocument document, Func<Func<JsonObject>, Task<JsonObject>> onUi) : IAsyncDisposable
{
    private HubListener? _listener;

    /// <summary>The pipe AVALightWave listens on (tests give their own).</summary>
    public string Pipe { get; set; } = HubProtocol.PipeName(HubProtocol.LightWave);

    /// <summary>The pipe AVA Modeler listens on (tests give their own).</summary>
    public string ModelerPipe { get; set; } = HubProtocol.PipeName(HubProtocol.Modeler);

    /// <summary>The settings folder, whose hub folder holds objects handed over without a file of their own (null: none).</summary>
    public string? SettingsFolder { get; set; }

    /// <summary>Finds and starts AVA Modeler (tests give one that starts nothing).</summary>
    public AppLauncher Launcher { get; set; } = new(HubProtocol.Modeler);

    /// <summary>Where the person said AVA Modeler is (Options), if anywhere.</summary>
    public string? ModelerSetting { get; set; }

    /// <summary>How long a message waits for AVA Modeler.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised (on the UI thread) with a line for the status bar when the hub has done something.</summary>
    public event Action<string>? Said;

    public bool Running => _listener != null;

    /// <summary>Joins the hub; returns why it couldn't (another AVALightWave has it), or null.</summary>
    public string? Start()
    {
        if (_listener != null) return null;
        var listener = new HubListener(Pipe, Handle);
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
        if (listener != null) await listener.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Whether AVA Modeler is on the hub just now.</summary>
    public bool ModelerThere() => HubClient.Answers(ModelerPipe);

    /// <summary>A message from AVA Modeler.</summary>
    internal Task<JsonObject> Handle(HubMessage message) => message.Type switch
    {
        HubMessage.Hello => Task.FromResult(HubProtocol.Ok(new JsonObject { ["app"] = HubProtocol.LightWave, ["pid"] = Environment.ProcessId })),
        HubMessage.ObjectSaved => onUi(() => Saved(message)),
        HubMessage.AddObject => onUi(() => Add(message)),
        _ => Task.FromResult(HubProtocol.Error($"AVALightWave doesn't take \"{message.Type}\" messages.")),
    };

    private JsonObject Saved(HubMessage message)
    {
        if (message.Path is not { } path || !File.Exists(path)) return HubProtocol.Error($"There's no object file {message.Path}.");
        var updated = document.UpdateFromFile(path, message.Link);
        if (updated.Count > 0) Said?.Invoke($"Updated {string.Join(", ", updated.Select(o => o.Name))} from AVA Modeler.");
        return HubProtocol.Ok(new JsonObject { ["updated"] = updated.Count });
    }

    private JsonObject Add(HubMessage message)
    {
        if (message.Path is not { } path || !File.Exists(path)) return HubProtocol.Error($"There's no object file {message.Path}.");
        if (message.Link is { } link && document.Linked(link).Count > 0)
        {
            var updated = document.UpdateFromFile(path, link);
            Said?.Invoke($"Updated {string.Join(", ", updated.Select(o => o.Name))} from AVA Modeler.");
            return HubProtocol.Ok(new JsonObject { ["updated"] = true, ["item"] = updated[0].Name });
        }
        var item = document.LoadObject(path);
        if (message.Name is { Length: > 0 } name && item.Name != name)
        {
            document.Session.Edit($"Rename {item.Name}", () => { item.Name = name; document.Scene.Notify(); });
            document.Session.NotifyChangedElsewhere();
        }
        if (message.Link is { } l) document.Link(item, l);
        Said?.Invoke($"AVA Modeler sent {item.Name}: it's in the scene.");
        return HubProtocol.Ok(new JsonObject { ["updated"] = false, ["item"] = item.Name });
    }

    /// <summary>
    /// The file AVA Modeler should open for an object: its own file while it's unchanged and in a format AVA Modeler
    /// reads, else a copy (LWOB) in the hub folder. Null if there's neither.
    /// </summary>
    public string? HandoverPath(ObjectItem item, string link)
    {
        var doc = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded, so there's nothing to model.");
        var own = item.SourcePath ?? doc.FilePath;
        if (own != null && !doc.Modified && File.Exists(own) && ObjectFiles.LoadExtensions.Contains(Path.GetExtension(own).ToLowerInvariant()))
            return Path.GetFullPath(own);
        if (SettingsFolder == null) return null;
        var path = HubFiles.PathFor(HubFiles.Folder(SettingsFolder), item.Name, link);
        ObjectFiles.Save(doc, path);
        return path;
    }

    /// <summary>
    /// Edit in Modeler: sends the object to AVA Modeler to model (by its file, or a copy), tied by a link so AVA
    /// Modeler's saves come back to it; starts AVA Modeler with it if it isn't on the hub. Returns what happened.
    /// </summary>
    public async Task<string> EditInModeler(ObjectItem item)
    {
        var link = document.LinkOf(item);
        string? path;
        try
        {
            path = HandoverPath(item, link);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
        if (path == null) return $"Save {item.Name} first (File › Save Object As): it has no file AVA Modeler could open.";
        var message = new HubMessage(HubMessage.EditObject, HubProtocol.LightWave, path, link, item.Name);
        try
        {
            await Task.Run(() => HubClient.SendAsync(ModelerPipe, message, Timeout)).ConfigureAwait(true);
            return $"{item.Name} is open in AVA Modeler: save it there (Ctrl+S) to update it here.";
        }
        catch (InvalidOperationException e)
        {
            return $"AVA Modeler didn't open {item.Name}: {e.Message}";
        }
        catch (HubUnavailableException)
        {
            var started = Launcher.Launch(ModelerSetting, path, link);
            return started != null
                ? $"Started AVA Modeler with {item.Name}: save it there (Ctrl+S) to update it here."
                : "AVA Modeler isn't running and couldn't be found: set where it is in Options (or AVAMODELER_PATH), or start it and try again.";
        }
    }
}
