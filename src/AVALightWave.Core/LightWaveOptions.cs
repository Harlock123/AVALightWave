using System.Text.Json;
using System.Text.Json.Serialization;

namespace AVALightWave;

/// <summary>
/// AVALightWave's own settings, beside the engine's <c>preferences.json</c> (which holds what it shares with AVA
/// Modeler's: the AI Chat's provider and model, whether agents may connect, whether it's on the hub, recent scenes) in
/// <see cref="Folder"/>: where AVA Modeler is, recent objects, and how the window starts.
/// </summary>
public sealed class LightWaveOptions
{
    /// <summary>Where AVA Modeler is, for Edit in Modeler to start it (else beside AVALightWave, its submodule build, or the PATH).</summary>
    public string? ModelerPath { get; set; }

    /// <summary>Object files loaded lately, newest first.</summary>
    public List<string> RecentObjects { get; set; } = [];

    /// <summary>The view the Single layout shows (Camera, Perspective, Top, Front, Side, Light).</summary>
    public string ViewMode { get; set; } = "Perspective";

    /// <summary>How the middle is split into views (Single, SideBySide, Stacked, Quad: View › Layout).</summary>
    public string ViewLayout { get; set; } = nameof(ViewportLayoutKind.Single);

    /// <summary>The views each multi-pane layout's panes show, by layout name (the single view is <see cref="ViewMode"/>).</summary>
    public Dictionary<string, List<string>> LayoutViews { get; set; } = [];

    /// <summary>Auto Key on at the start (classic Layout left it to you; on, every move is a key).</summary>
    public bool AutoKey { get; set; } = true;

    /// <summary>Minutes between auto-saves of a scene with unsaved changes (0: never).</summary>
    public int AutosaveMinutes { get; set; } = 3;

    /// <summary>AVALightWave's settings folder, in the app-data folder.</summary>
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVALightWave");

    public static string DefaultPath => Path.Combine(Folder, "lightwave.json");

    /// <summary>The engine's shared preferences file in AVALightWave's folder.</summary>
    public static string PreferencesPath => Path.Combine(Folder, "preferences.json");

    /// <summary>The AI Chat's saved conversations (only with Keep chat history on).</summary>
    public static string ChatsFolder => Path.Combine(Folder, "chats");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Where these were loaded from and <see cref="Save"/> writes; null (made with <c>new</c>, as tests' are) saves nowhere.</summary>
    [JsonIgnore]
    public string? Location { get; private set; }

    public static LightWaveOptions Load(string? path = null)
    {
        path ??= DefaultPath;
        var options = new LightWaveOptions();
        try
        {
            if (File.Exists(path)) options = JsonSerializer.Deserialize<LightWaveOptions>(File.ReadAllText(path), Json) ?? options;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        options.Location = path;
        return options;
    }

    public void Save()
    {
        if (Location == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            File.WriteAllText(Location, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void AddRecentObject(string path)
    {
        RecentObjects.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentObjects.Insert(0, path);
        if (RecentObjects.Count > 10) RecentObjects.RemoveRange(10, RecentObjects.Count - 10);
    }
}
