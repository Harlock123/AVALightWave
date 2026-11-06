using System.Text.Json;
using AVAModeler.Core;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// Auto-save and crash recovery for the scene, as AVA Modeler has them for its object: while AVALightWave runs it keeps
/// a lock file in its auto-save folder, and every few minutes a scene with unsaved changes is written there as a
/// self-contained .avascene (meshes inside), never over the person's own file and without changing the scene's file or
/// its unsaved state. A clean exit takes both away; if the lock is still there at the next start, AVALightWave didn't
/// close properly, and the auto-save is offered back. The folder is the app-data folder's <c>autosave</c>; tests give
/// their own.
/// </summary>
public sealed class SceneAutosave(string folder)
{
    public string Folder { get; } = folder;

    /// <summary>The default folder: <c>autosave</c> in AVALightWave's settings folder.</summary>
    public static string DefaultFolder => Path.Combine(LightWaveOptions.Folder, "autosave");

    public string ScenePath => Path.Combine(Folder, "autosave" + SceneJson.Extension);
    private string InfoPath => Path.Combine(Folder, "autosave.json");
    private string LockPath => Path.Combine(Folder, "running.lock");

    /// <summary>
    /// What an auto-save was of: the scene's own file (null if it had none), when it was written, and the files of the
    /// objects that had unsaved changes then (they're unsaved again when recovered).
    /// </summary>
    public sealed record Info(string? Scene, DateTime Saved, IReadOnlyList<string>? ModifiedObjects = null);

    /// <summary>
    /// Whether the last run didn't end cleanly and left an auto-save: its details, or null. Call before <see cref="Start"/>.
    /// </summary>
    public Info? Waiting()
    {
        if (!File.Exists(LockPath) || !File.Exists(ScenePath)) return null;
        try
        {
            var info = File.Exists(InfoPath) ? JsonSerializer.Deserialize<Info>(File.ReadAllText(InfoPath)) : null;
            return info ?? new Info(null, File.GetLastWriteTime(ScenePath));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Info(null, File.GetLastWriteTime(ScenePath));
        }
    }

    /// <summary>Marks a run as under way (the lock file).</summary>
    public void Start()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(LockPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Writes the scene to the auto-save if it has unsaved changes (else does nothing); returns whether it wrote. The
    /// scene is copied here (with copies of its objects' documents), and written on a worker thread, so the person can
    /// go on working; the document's own file and unsaved state are untouched.
    /// </summary>
    public async Task<bool> SaveAsync(SceneDocument document)
    {
        if (!document.Modified && document.UnsavedObjects.All(o => o.Document?.Modified != true)) return false;
        var copy = document.Scene.Copy(d => d.Copy());
        var modified = document.Scene.Objects.Select(o => o.Document).OfType<Document>().Where(d => d.Modified && d.FilePath != null)
            .Select(d => Path.GetFullPath(d.FilePath!)).Distinct().ToList();
        var info = new Info(document.Path, DateTime.Now, modified);
        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Folder);
                // Written beside and then moved over, so a crash mid-write leaves the last good one.
                var temp = ScenePath + ".tmp";
                SceneJson.WriteFile(copy, temp, embedMeshes: true);
                File.Move(temp, ScenePath, overwrite: true);
                File.WriteAllText(InfoPath, JsonSerializer.Serialize(info));
            }).ConfigureAwait(true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Puts the auto-saved scene in place of the open one: untitled (so saving asks where) and marked as having unsaved
    /// changes, its undo history fresh. Returns the auto-save's details.
    /// </summary>
    public Info Recover(SceneDocument document)
    {
        var info = Waiting() ?? new Info(null, File.GetLastWriteTime(ScenePath));
        var result = SceneJson.ReadFile(ScenePath, SceneDocument.LoadOrNull);
        // Meshes are inside the auto-save, so its objects keep the files they came from (for Save Object and .lws); those
        // that had unsaved changes have them again.
        var modified = new HashSet<string>(info.ModifiedObjects ?? []);
        foreach (var doc in result.Scene.Objects.Select(o => o.Document).OfType<Document>().Distinct())
            if (doc.FilePath != null && modified.Contains(Path.GetFullPath(doc.FilePath))) doc.Modified = true;
        document.Session.Replace(result.Scene, null);
        document.Session.Warnings.Clear();
        document.Session.Warnings.AddRange(result.Warnings);
        // A change after loading, so it counts as unsaved until the person saves it somewhere.
        document.Scene.Notify();
        document.Session.SetStatus($"Recovered the auto-save of {(info.Scene != null ? Path.GetFileName(info.Scene) : "an untitled scene")} from {info.Saved:g}: save it to keep it.");
        return info;
    }

    /// <summary>A clean exit: the lock and the auto-save go.</summary>
    public void Finish()
    {
        foreach (var f in new[] { LockPath, ScenePath, InfoPath })
            try
            {
                if (File.Exists(f)) File.Delete(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
