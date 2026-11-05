using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// AVALightWave's document: a scene of its own, with the objects it loads from files (several per scene), rather than
/// the live object of a modeler beside it. It wraps the engine's <see cref="LayoutSession"/> (the scene, its frame,
/// chosen item, auto-key and undo history, which agents share) and adds what a standalone Layout needs: New, Load and
/// Save Scene (.lws, with objects that have no file saved beside it first, or .avascene), Load and Replace Object,
/// saving objects, hiding and locking items, editing an object's surfaces, and the hub's links between scene objects and
/// what AVA Modeler has open. Every change to the scene is one undo step on the session's history.
/// </summary>
public sealed class SceneDocument
{
    private readonly Dictionary<ObjectItem, string> _links = [];

    public SceneDocument(LayoutSession? session = null)
    {
        Session = session ?? new LayoutSession();
        // A new or loaded scene has none of the old one's links.
        Session.SceneReplaced += _links.Clear;
    }

    public LayoutSession Session { get; }

    public Scene Scene => Session.Scene;

    /// <summary>The scene's file, or null until it's saved.</summary>
    public string? Path => Session.ScenePath;

    /// <summary>The scene's name as the title bar shows it.</summary>
    public string Name => Path != null ? System.IO.Path.GetFileName(Path) : "Untitled";

    /// <summary>Whether the scene has changed since it was made, loaded or saved.</summary>
    public bool Modified => Session.Modified;

    /// <summary>The title: the scene's name, with a star while it has changes.</summary>
    public string Title => Name + (Modified ? " *" : "");

    /// <summary>The objects whose geometry or surfaces haven't been saved to a file of their own (each document once).</summary>
    public IReadOnlyList<ObjectItem> UnsavedObjects =>
        Scene.Objects.Where(o => o.Document is { } d && (d.Modified || (o.SourcePath ?? d.FilePath) == null))
            .DistinctBy(o => o.Document).ToList();

    // ───────────────────────────── scenes ─────────────────────────────

    /// <summary>A new scene, as classic Layout starts: a camera and a light, nothing else. Clears the undo history.</summary>
    public void New() => Session.NewScene();

    /// <summary>
    /// Loads a scene (.lws from LightWave 4 and 5 or later, or .avascene) in place of this one. Objects it names that
    /// can't be read stand in as placeholders, and <see cref="LayoutSession.Warnings"/> says which.
    /// </summary>
    public void Load(string path) => Session.LoadScene(path, LoadOrNull);

    /// <summary>An object file through every format the engine reads, or null if it can't be had.</summary>
    public static Document? LoadOrNull(string path)
    {
        try { return ObjectFiles.Load(path); }
        catch (Exception e) when (e is IOException or FormatException or NotSupportedException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    /// <summary>
    /// Saves the scene: .lws (LightWave's scene; objects are referenced by their files, so any object with no file or
    /// unsaved changes is first saved as LWOB in an Objects folder beside the scene) or .avascene (everything, meshes
    /// included). Returns what the format couldn't keep and which objects were saved.
    /// </summary>
    public IReadOnlyList<string> Save(string path)
    {
        var notes = new List<string>();
        if (path.EndsWith(".lws", StringComparison.OrdinalIgnoreCase))
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, "Objects");
            foreach (var o in UnsavedObjects)
            {
                var file = FreeFile(folder, o.Name);
                SaveObject(o, file);
                notes.Add($"{o.Name} had no file of its own (or unsaved changes): saved as Objects/{System.IO.Path.GetFileName(file)}.");
            }
            if (Scene.Items.Any(i => i.Locked)) notes.Add("Locks are left out: .lws has no place for them (.avascene keeps them).");
        }
        var warnings = Session.SaveScene(path);
        notes.AddRange(warnings);
        return notes;
    }

    private static string FreeFile(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var safe = new string(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (safe.Length == 0) safe = "object";
        var file = System.IO.Path.Combine(folder, safe + ".lwo");
        for (int n = 2; File.Exists(file); n++) file = System.IO.Path.Combine(folder, $"{safe} {n}.lwo");
        return file;
    }

    // ───────────────────────────── objects ─────────────────────────────

    /// <summary>Loads an object file (LWOB, LWO2, OBJ, GEO, glTF/GLB, STL) into the scene as one undo step, and chooses it.</summary>
    public ObjectItem LoadObject(string path) => Session.LoadObject(path, ObjectFiles.Load);

    /// <summary>
    /// Replace Object: the item takes another file's geometry and surfaces, keeping its name (unless it was named after
    /// its old file, when it takes the new one's), motion, parent, bones and settings, as one undo step.
    /// </summary>
    public void ReplaceObject(ObjectItem item, string path)
    {
        var doc = ObjectFiles.Load(path);
        string? oldName = item.SourcePath != null ? System.IO.Path.GetFileNameWithoutExtension(item.SourcePath) : null;
        Session.Edit($"Replace {item.Name}", () =>
        {
            Swap(item, doc, path);
            if (oldName != null && item.Name == oldName) item.Name = System.IO.Path.GetFileNameWithoutExtension(path);
            Scene.Notify();
        });
        Session.SetStatus($"Replaced {item.Name}'s object with {System.IO.Path.GetFileName(path)}.");
    }

    private static void Swap(ObjectItem item, Document doc, string? path)
    {
        item.Document = doc;
        if (item.Layer is int l && l >= doc.Layers.Count) item.Layer = null;
        item.SourcePath = path;
    }

    /// <summary>
    /// Puts a new version of an object's document in place for every item that uses <paramref name="old"/>, as one undo
    /// step named <paramref name="label"/> (undo brings the old one back). Returns the items changed.
    /// </summary>
    public IReadOnlyList<ObjectItem> ReplaceDocument(Document old, Document doc, string? path, string label)
    {
        var items = Scene.Objects.Where(o => ReferenceEquals(o.Document, old)).ToList();
        if (items.Count == 0) return items;
        Session.Edit(label, () =>
        {
            foreach (var o in items) Swap(o, doc, path ?? o.SourcePath);
            Scene.Notify();
        });
        return items;
    }

    /// <summary>
    /// Saves an object's geometry and surfaces to <paramref name="path"/> (by its extension; its own file if not given)
    /// and makes that its file. Not an undo step: files aren't undone.
    /// </summary>
    public string SaveObject(ObjectItem item, string? path = null)
    {
        var doc = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded, so there's nothing to save.");
        path ??= item.SourcePath ?? doc.FilePath ?? throw new InvalidOperationException($"{item.Name} has no file yet: choose where to save it.");
        if (!ObjectFiles.SaveExtensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidOperationException($"{System.IO.Path.GetFileName(path)}: objects are saved as {string.Join(", ", ObjectFiles.SaveExtensions)}.");
        ObjectFiles.Save(doc, path);
        ObjectFiles.RebaseImages(doc, path);
        doc.FilePath = path;
        doc.Modified = false;
        foreach (var o in Scene.Objects.Where(o => ReferenceEquals(o.Document, doc))) o.SourcePath = path;
        Session.SetStatus($"Saved {item.Name} as {System.IO.Path.GetFileName(path)}.");
        return path;
    }

    /// <summary>Saves every object with unsaved changes to its own file (those with none are skipped and named). Returns what was done.</summary>
    public IReadOnlyList<string> SaveAllObjects()
    {
        var notes = new List<string>();
        foreach (var o in UnsavedObjects)
        {
            if ((o.SourcePath ?? o.Document?.FilePath) is not { } file || !ObjectFiles.SaveExtensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()))
            {
                notes.Add($"{o.Name} has no file of its own yet: use Save Object As.");
                continue;
            }
            SaveObject(o, file);
            notes.Add($"Saved {o.Name}.");
        }
        return notes;
    }

    /// <summary>
    /// Changes one of an object's surfaces (colour, diffuse, specular and the rest) as one scene undo step: the object
    /// gets a changed copy of its document, so undo puts the old one back. Every item using that document follows.
    /// </summary>
    public void EditSurface(ObjectItem item, int surface, string label, Action<Surface> change)
    {
        var old = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded, so it has no surfaces.");
        if (surface < 0 || surface >= old.Surfaces.Count) throw new ArgumentOutOfRangeException(nameof(surface), $"{item.Name} has {old.Surfaces.Count} surfaces.");
        var doc = old.Copy();
        change(doc.Surfaces[surface]);
        doc.Modified = true;
        ReplaceDocument(old, doc, null, label);
    }

    // ───────────────────────────── items ─────────────────────────────

    /// <summary>Shows or hides an item (in the views and renders) as one undo step.</summary>
    public void SetVisible(SceneItem item, bool visible)
    {
        if (item.Visible == visible) return;
        Session.Edit($"{(visible ? "Show" : "Hide")} {item.Name}", () => { item.Visible = visible; Scene.Notify(); });
    }

    /// <summary>Locks an item against the mouse (or frees it) as one undo step.</summary>
    public void SetLocked(SceneItem item, bool locked)
    {
        if (item.Locked == locked) return;
        Session.Edit($"{(locked ? "Lock" : "Unlock")} {item.Name}", () => { item.Locked = locked; Scene.Notify(); });
    }

    /// <summary>Parents an item to another (or to none) as one undo step; throws if that would make a cycle.</summary>
    public void SetParent(SceneItem item, SceneItem? parent) =>
        Session.Edit($"Parent {item.Name}", () => item.Parent = parent);

    // ───────────────────────────── the hub's links ─────────────────────────────

    /// <summary>The link tying a scene object to the document AVA Modeler has open for it (made if it has none).</summary>
    public string LinkOf(ObjectItem item)
    {
        if (!_links.TryGetValue(item, out var link)) _links[item] = link = AVAModeler.Hub.HubFiles.NewLink();
        return link;
    }

    /// <summary>Ties an object to a link (one AVA Modeler gave it).</summary>
    public void Link(ObjectItem item, string link) => _links[item] = link;

    /// <summary>The objects tied to a link (in the scene now).</summary>
    public IReadOnlyList<ObjectItem> Linked(string link) =>
        _links.Where(kv => kv.Value == link && kv.Key.Scene == Scene).Select(kv => kv.Key).ToList();

    /// <summary>
    /// An object AVA Modeler saved (or synced): its file is read again and put in place, as one undo step, for every
    /// object tied to <paramref name="link"/>, or else every object that came from that file. Returns the objects updated.
    /// </summary>
    public IReadOnlyList<ObjectItem> UpdateFromFile(string path, string? link)
    {
        var full = System.IO.Path.GetFullPath(path);
        var targets = link != null ? Linked(link).ToList() : [];
        if (targets.Count == 0)
            targets = Scene.Objects.Where(o => (o.SourcePath ?? o.Document?.FilePath) is { } f && System.IO.Path.GetFullPath(f) == full).ToList();
        if (targets.Count == 0) return [];
        var doc = ObjectFiles.Load(full);
        var updated = new List<ObjectItem>();
        Session.Edit($"Update {targets[0].Name} from AVA Modeler", () =>
        {
            foreach (var old in targets.Select(t => t.Document).Distinct().ToList())
            {
                foreach (var o in Scene.Objects.Where(o => ReferenceEquals(o.Document, old) && targets.Contains(o)))
                {
                    Swap(o, doc, full);
                    updated.Add(o);
                }
            }
            Scene.Notify();
        });
        if (link != null) foreach (var o in updated) _links[o] = link;
        return updated;
    }
}
