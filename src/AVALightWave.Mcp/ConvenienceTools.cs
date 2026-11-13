using System.ComponentModel;
using System.Text.Json.Nodes;
using AVALightWave.Core;
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

    private static bool Keyed(CameraItem cam) => cam.Channels.Take(6).Any(AVAModeler.Layout.Keying.IsAnimated);
}
