using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>
/// AVALightWave's own MCP tools, beside the engine's scene tools: Replace Object and saving an object (a standalone
/// Layout's objects come from files and go back to them), and the hub (Edit in Modeler and its state). They reach the
/// scene only through <see cref="IModelerHost.RunSceneAsync{T}"/> and the session, as the engine's tools do, so each
/// change is one undo step ("Agent: …") the person's Ctrl+Z takes back, and a call that fails changes nothing.
/// </summary>
public sealed class LightWaveTools(IModelerHost host, ILightWaveApp? app)
{
    [McpServerTool(Name = "replace_object", Destructive = true)]
    [Description("Replace Object, as classic Layout had it: the object item takes another file's geometry and surfaces (from the working folder: .lwo, .lw, .obj, .geo, .glb, .gltf or .stl), keeping its name, position, rotation, scale, keys, parent, bones and settings. An item named after its old file takes the new file's name. One undo step. Returns the item with its point and polygon counts.")]
    public Task<string> ReplaceObject(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        [Description("The object file, relative to the working folder, e.g. \"objects/ship2.lwo\".")] string file,
        CancellationToken cancel = default) =>
        Scene("replaced an object", (l, root) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var full = Inside(root, file);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {file} in the working folder ({root}).");
            var doc = ObjectFiles.Load(full);
            string? oldName = item.SourcePath != null ? Path.GetFileNameWithoutExtension(item.SourcePath) : null;
            item.Document = doc;
            if (item.Layer is int layer && layer >= doc.Layers.Count) item.Layer = null;
            item.SourcePath = full;
            if (oldName != null && item.Name == oldName) item.Name = Path.GetFileNameWithoutExtension(full);
            l.Scene.Notify();
            return Describe(item);
        }, cancel);

    [McpServerTool(Name = "save_object", Destructive = true)]
    [Description("Saves an object item's geometry and surfaces to a file in the working folder, the format chosen by the extension (.lwo keeps everything; .obj, .geo, .glb, .gltf, .stl for other programs), and makes it the object's file, so a .lws scene saved afterwards refers to it. An existing file is only replaced when overwrite is true. Not an undo step (files aren't undone).")]
    public Task<string> SaveObject(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        [Description("The file, relative to the working folder, e.g. \"objects/ship.lwo\".")] string path,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Scene("saved an object", (l, root) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var doc = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded, so there's nothing to save.");
            var full = Inside(root, path);
            if (!ObjectFiles.SaveExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
                throw new ArgumentException($"Objects are saved as {string.Join(", ", ObjectFiles.SaveExtensions)}, not {Path.GetExtension(full)}.");
            if (File.Exists(full) && !overwrite) throw new IOException($"{path} already exists: give overwrite true to replace it.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            ObjectFiles.Save(doc, full);
            ObjectFiles.RebaseImages(doc, full);
            doc.FilePath = full;
            doc.Modified = false;
            foreach (var o in l.Scene.Objects.Where(o => ReferenceEquals(o.Document, doc))) o.SourcePath = full;
            var result = Describe(item);
            result["saved"] = Path.GetRelativePath(root, full);
            return result;
        }, cancel, changes: false);

    [McpServerTool(Name = "edit_in_modeler", Destructive = true)]
    [Description("Edit in Modeler: sends an object item to AVA Modeler, the companion modeler, to change its shape there (starting AVA Modeler if it isn't running and can be found). When it's saved in AVA Modeler, AVALightWave reads it again and updates the item, keeping its motion. Only in the open AVALightWave window (on the hub); AVA Modeler won't open it over unsaved work of its own, and says so. Modeling itself is AVA Modeler's: an agent connected to AVA Modeler's own MCP server can model it there.")]
    public async Task<string> EditInModeler(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        CancellationToken cancel = default)
    {
        if (app == null) throw new McpException("edit_in_modeler works in the open AVALightWave window (the hub links it with AVA Modeler), not in the headless server.");
        ObjectItem item;
        try
        {
            item = await host.RunSceneAsync("chose an object for AVA Modeler", (_, l) => ObjectByName(l.Scene, @object), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
        var said = await app.EditInModeler(item).ConfigureAwait(false);
        return new JsonObject { ["object"] = item.Name, ["result"] = said }.ToJsonString();
    }

    [McpServerTool(Name = "hub_status", ReadOnly = true, Idempotent = true)]
    [Description("Says whether AVALightWave is on the hub (the live link with AVA Modeler, the companion modeler) and whether AVA Modeler is there now. Headless, there's no hub.")]
    public Task<string> HubStatus(CancellationToken cancel = default)
    {
        var (on, there) = app?.HubState() ?? (false, false);
        return Task.FromResult(new JsonObject
        {
            ["on_hub"] = on,
            ["modeler_running"] = there,
            ["note"] = app == null ? "The headless server has no hub: run AVALightWave's window for Edit in Modeler." :
                on ? (there ? "AVA Modeler is linked: edit_in_modeler sends it an object." : "AVA Modeler isn't running: edit_in_modeler starts it if it can be found.")
                : "The hub is off (Options › Link with AVA Modeler).",
        }.ToJsonString());
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Runs scene work on the host, as one undo step named "Agent: …" when it changes the scene; errors become tool errors.</summary>
    private async Task<string> Scene(string action, Func<LayoutSession, string, JsonObject> work, CancellationToken cancel, bool changes = true)
    {
        try
        {
            return await host.RunSceneAsync(action, (_, l) =>
                (changes ? l.Edit(ModelerTools.AgentStep(action), () => work(l, host.Root), apart: true) : work(l, host.Root)).ToJsonString(), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>A path inside the working folder, or an error saying it must be.</summary>
    internal static string Inside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is needed.");
        var full = Path.GetFullPath(Path.Combine(root, path));
        var rootSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootSlash, cmp)) throw new ArgumentException($"{path} is outside the working folder ({root}). Give a path inside it.");
        return full;
    }

    /// <summary>An object item by name or #index.</summary>
    internal static ObjectItem ObjectByName(AVAModeler.Scene.Scene scene, string name)
    {
        SceneItem? item = null;
        if (name.StartsWith('#') && int.TryParse(name[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < scene.Items.Count) item = scene.Items[i];
        item ??= scene.Find(name);
        return item switch
        {
            ObjectItem o => o,
            null => throw new ArgumentException($"There's no item \"{name}\". The objects are: {string.Join(", ", scene.Objects.Select(o => o.Name))}."),
            _ => throw new ArgumentException($"{item.Name} is a {item.Kind.ToLowerInvariant()}, not an object."),
        };
    }

    private static JsonObject Describe(ObjectItem item)
    {
        var mesh = item.BaseMesh();
        return new JsonObject
        {
            ["name"] = item.Name,
            ["source"] = item.SourcePath,
            ["points"] = mesh.Points.Count,
            ["polygons"] = mesh.Polygons.Count,
        };
    }
}
