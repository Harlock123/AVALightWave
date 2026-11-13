using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Scene;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Conveniences (milestone A): camera bookmarks and packaging a scene.</summary>
public sealed partial class LightWaveTools
{
    [McpServerTool(Name = "camera_bookmark", Destructive = true)]
    [Description("Camera bookmarks: named views kept with the scene (in .avascene files; undo puts them back). action save keeps a camera's place, rotation and zoom at a frame under a name (replacing one of that name); recall puts a camera there at a frame (keyed if its channels are animated, or if key is true; otherwise the value throughout); remove takes one away; list gives them all. Save, recall and remove are one undo step each.")]
    public Task<string> CameraBookmark(
        [Description("save, recall, remove or list.")] string action,
        [Description("The bookmark's name (not for list).")] string? name = null,
        [Description("The camera (default: the current one).")] string? camera = null,
        [Description("The frame (default: the current frame).")] double? frame = null,
        [Description("recall: key the camera (default: only the channels already animated).")] bool? key = null,
        CancellationToken cancel = default)
    {
        var what = (action ?? "").Trim().ToLowerInvariant();
        return Making(what == "list" ? "listed bookmarks" : $"{what} a bookmark", (_, l, _) =>
        {
            var s = l.Scene;
            CameraItem Cam() => (camera != null ? ItemByName(s, camera) : s.CurrentCamera) as CameraItem
                ?? throw new ArgumentException(camera != null ? $"{camera} isn't a camera." : "The scene has no camera.");
            string Name() => name ?? throw new ArgumentException("name is needed.");
            double f = frame ?? l.Frame;
            JsonObject Of(CameraBookmark b) => new()
            {
                ["name"] = b.Name, ["position"] = Json3(b.At), ["rotation"] = Json3(b.Turn), ["zoom"] = Math.Round(b.Zoom, 6),
            };
            switch (what)
            {
                case "save":
                {
                    var b = CameraBookmarks.Save(l, Name(), Cam(), f);
                    return new JsonObject { ["saved"] = Of(b), ["bookmarks"] = CameraBookmarks.List(s).Count };
                }
                case "recall":
                {
                    var cam = Cam();
                    var b = CameraBookmarks.Recall(l, Name(), cam, f, key);
                    return new JsonObject { ["camera"] = cam.Name, ["frame"] = f, ["recalled"] = Of(b), ["keyed"] = key ?? Keyed(cam) };
                }
                case "remove":
                    CameraBookmarks.Remove(l, Name());
                    return new JsonObject { ["removed"] = Name(), ["bookmarks"] = CameraBookmarks.List(s).Count };
                case "list":
                    return new JsonObject { ["bookmarks"] = new JsonArray([.. CameraBookmarks.List(s).Select(b => (JsonNode)Of(b))]) };
                default:
                    throw new ArgumentException($"action must be save, recall, remove or list, not \"{action}\".");
            }
        }, cancel);
    }

    [McpServerTool(Name = "package_scene", Destructive = false)]
    [Description("Package Scene: gathers the scene and everything it uses into one new folder in the working folder (and, with zip, a .zip of it beside), so it opens on another machine: the scene as .avascene with its meshes inside, each object's own file in objects/ (objects with none saved there as .lwo), and every image (textures, the environment, the Images panel's pictures and whole sequences, compositing) in images/, the scene's paths pointing at the copies. The open scene isn't changed (not an undo step). Returns the files made and anything that couldn't be found.")]
    public Task<string> PackageScene(
        [Description("The new folder, relative to the working folder, e.g. \"packages/robot\". It must be empty or new unless overwrite is true.")] string folder,
        [Description("The scene file's name in it (default: the folder's name).")] string? name = null,
        [Description("Also make a .zip of the folder beside it.")] bool zip = false,
        [Description("Replace the folder (and zip) if they already exist.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Making("packaged the scene", (_, l, root) =>
        {
            var full = Inside(root, folder);
            var r = ScenePackage.Make(l.Scene, full, name ?? Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), root, zip, overwrite);
            var o = new JsonObject
            {
                ["scene"] = Path.GetRelativePath(root, r.Scene), ["folder"] = Path.GetRelativePath(root, r.Folder),
                ["files"] = new JsonArray([.. r.Files.Select(f => (JsonNode)Path.GetRelativePath(r.Folder, f))]),
            };
            if (r.Zip != null) o["zip"] = Path.GetRelativePath(root, r.Zip);
            if (r.Warnings.Count > 0) o["warnings"] = new JsonArray([.. r.Warnings.Select(w => (JsonNode)w)]);
            return o;
        }, cancel, changes: false);

    private static bool Keyed(CameraItem cam) => cam.Channels.Take(6).Any(AVAModeler.Layout.Keying.IsAnimated);
}
