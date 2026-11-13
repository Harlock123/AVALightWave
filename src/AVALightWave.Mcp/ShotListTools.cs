using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>A shot in a cut: its name, the camera it's seen through, and its frames.</summary>
public sealed record ShotCut(string Name, string Camera, int First, int Last);

/// <summary>Shot lists (milestone A, stage 6): several shots, each with its own camera, cut together into one sequence.</summary>
public sealed partial class LightWaveTools
{
    public const string ShotsKey = "avalightwave.shots";
    private static readonly JsonSerializerOptions CutJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>The scene's cut, in order (none if no shots were built).</summary>
    public static IReadOnlyList<ShotCut> Cuts(AVAModeler.Scene.Scene s)
    {
        if (s.Extra(ShotsKey) is not { } text) return [];
        try { return JsonSerializer.Deserialize<List<ShotCut>>(text, CutJson) ?? []; }
        catch (JsonException) { return []; }
    }

    [McpServerTool(Name = "build_shots", Destructive = true)]
    [Description("Builds a cut of several shots on the timeline, one after another, each seen through a camera of its own, as one undo step, all or nothing. Each shot: {\"name\": \"Wide\", \"duration\": 48 (or \"end\"), \"camera\": frame_items' arguments (items, from, margin…: fitted over the shot's frames) or {\"position\": [x, y, z], \"look_at\": item or [x, y, z], \"zoom\": z}, \"camera_moves\": [camera_move's arguments, their frames defaulting to the shot's], \"motion\": [animate's moves, starting at the shot's first frame unless they say]}. Shots start at start (default the scene's first frame) and follow on without a gap; the scene is lengthened to hold them. Each shot's camera is named \"<shot> Camera\"; the cut is kept with the scene for render_shots. Returns the cut and each shot's check through its camera.")]
    public Task<string> BuildShots(
        [Description("The shots, in order.")] JsonObject[] shots,
        [Description("The first shot's first frame (default: the scene's first).")] int? start = null,
        CancellationToken cancel = default) =>
        Making("built shots", (c, l, root) =>
        {
            if (shots == null || shots.Length == 0) throw new ArgumentException("Give at least one shot, e.g. {\"name\": \"Wide\", \"duration\": 48, \"camera\": {\"items\": [\"all\"], \"from\": \"front\"}}.");
            if (shots.Length > 50) throw new ArgumentException("At most 50 shots in one cut.");
            var s = l.Scene;
            var calls = SceneCalls(root, c, l, ["add_camera", "set_item", "set_transform", "look_at", "set_scene"]);
            calls.Add(new LightWaveTools(new InlineHost(root, c, l), app), ["frame_items", "camera_move", "animate"]);
            int at = start ?? s.FirstFrame;
            if (at < s.FirstFrame) throw new ArgumentException($"start must be at or after the scene's first frame, {s.FirstFrame}.");
            var cut = new List<ShotCut>();
            var said = new JsonArray();
            for (int i = 0; i < shots.Length; i++)
            {
                var shot = shots[i];
                string where = $"Shot {i + 1}";
                try
                {
                    string name = shot["name"]?.GetValue<string>()?.Trim() is { Length: > 0 } n ? n : $"Shot {i + 1}";
                    where = $"Shot {i + 1} ({name})";
                    foreach (var (key, _) in shot)
                        if (key is not ("name" or "duration" or "end" or "camera" or "camera_moves" or "motion"))
                            throw new ArgumentException($"A shot has no \"{key}\". It has name, duration or end, camera, camera_moves and motion.");
                    if (cut.Any(x => x.Name == name)) throw new ArgumentException($"Two shots are called \"{name}\".");
                    int first = at;
                    int last = shot["end"] is { } e ? e.GetValue<int>() : first + (shot["duration"]?.GetValue<int>() ?? 48) - 1;
                    if (last < first) throw new ArgumentException($"It ends at frame {last}, before it starts ({first}).");
                    if (last > s.LastFrame) calls.Call("set_scene", new JsonObject { ["last_frame"] = last });
                    var camName = EngineCaller.AsObject(calls.Call("add_camera", new JsonObject { ["name"] = name + " Camera" }))["name"]!.GetValue<string>();
                    if (shot["motion"] is JsonArray motion)
                    {
                        var moves = new JsonArray();
                        foreach (var m in motion)
                        {
                            var move = (JsonObject)(m as JsonObject ?? throw new ArgumentException("Each motion move is an object.")).DeepClone();
                            if (move["start"] == null && move["after"] == null && move["with"] == null) move["start"] = first;
                            moves.Add(move);
                        }
                        calls.Call("animate", new JsonObject { ["moves"] = moves });
                    }
                    var camera = shot["camera"] as JsonObject ?? new JsonObject { ["items"] = new JsonArray("all") };
                    if (camera["position"] != null || camera["look_at"] != null || camera["zoom"] != null)
                    {
                        if (camera["zoom"] is { } z) calls.Call("set_item", new JsonObject { ["item"] = camName, ["zoom"] = z.DeepClone() });
                        if (camera["position"] is { } p) calls.Call("set_transform", new JsonObject { ["item"] = camName, ["position"] = p.DeepClone(), ["key"] = false });
                        if (camera["look_at"] is { } t)
                            calls.Call("look_at", t is JsonArray ? new JsonObject { ["item"] = camName, ["point"] = t.DeepClone(), ["key"] = false }
                                : new JsonObject { ["item"] = camName, ["target"] = t.DeepClone(), ["key"] = false });
                    }
                    else
                    {
                        var fit = (JsonObject)camera.DeepClone();
                        fit["camera"] = camName;
                        if (fit["frame"] == null && fit["start"] == null) { fit["start"] = first; fit["end"] = last; }
                        calls.Call("frame_items", fit);
                    }
                    if (shot["camera_moves"] is JsonArray cms)
                        foreach (var m in cms)
                        {
                            var move = (JsonObject)(m as JsonObject ?? throw new ArgumentException("Each camera move is an object.")).DeepClone();
                            move["camera"] = camName;
                            if (move["start"] == null) move["start"] = first;
                            if (move["end"] == null && move["duration"] == null) move["end"] = last;
                            calls.Call("camera_move", move);
                        }
                    cut.Add(new ShotCut(name, camName, first, last));
                    var cam = (CameraItem)s.Find(camName)!;
                    said.Add(new JsonObject
                    {
                        ["name"] = name, ["camera"] = camName, ["frames"] = new JsonArray(first, last),
                        ["check"] = Shot.Check(l, cam, Shot.Frames(first, last, Math.Max(1, (last - first) / 12.0)), 0.04),
                    });
                    at = last + 1;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new ArgumentException($"{where} failed: {e.Message.Replace(" Nothing was changed.", "", StringComparison.Ordinal)} Nothing was changed.", e);
                }
            }
            s.SetExtra(ShotsKey, JsonSerializer.Serialize(cut, CutJson));
            if (s.Find(cut[0].Camera) is CameraItem firstCam) s.CurrentCamera = firstCam;
            return new JsonObject { ["shots"] = said, ["scene_frames"] = new JsonArray(s.FirstFrame, s.LastFrame), ["next"] = "render_shots renders the cut into one folder (and a movie)." };
        }, cancel);

    [McpServerTool(Name = "render_shots", Destructive = true)]
    [Description("Renders the cut build_shots made: each shot's frames through its own camera into one folder, numbered in order across the cut (shot0001.png on), so it plays as one sequence; with movie (mp4, gif or both, needing ffmpeg) they're joined into one. Long shots are rendered in parts within render_animation's limits. Returns a contact sheet of each shot's first frame (numbered) and the files.")]
    public async Task<CallToolResult> RenderShots(
        [Description("The folder, relative to the working folder (default \"shots\").")] string folder = "shots",
        [Description("The files' names before the frame number (default \"shot\").")] string name = "shot",
        [Description("Width in pixels, 16 to 960 (default 320).")] int width = 320,
        [Description("Rays a pixel, 1 to 4 (default 1).")] int samples = 1,
        [Description("mp4, gif or both to join the frames (default none).")] string? movie = null,
        [Description("Replace files that already exist.")] bool overwrite = false,
        CancellationToken cancel = default)
    {
        IReadOnlyList<ShotCut> cut;
        int height;
        try
        {
            (cut, height) = await host.RunSceneAsync("read the cut", (_, l) =>
            {
                var cuts = Cuts(l.Scene);
                if (cuts.Count == 0) throw new ArgumentException("The scene has no shots: build_shots makes them.");
                foreach (var x in cuts)
                    if (l.Scene.Find(x.Camera) is not CameraItem) throw new ArgumentException($"Shot {x.Name}'s camera, {x.Camera}, isn't in the scene any more: build the shots again.");
                var cam = (CameraItem)l.Scene.Find(cuts[0].Camera)!;
                return (cuts, Math.Max(16, (int)Math.Round(width / cam.FrameAspect)));
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
        var engine = new ModelerTools(host) { Profile = LightWaveServer.Profile(app) };
        int chunk = (int)Math.Clamp(ModelerTools.MaxAnimationWork / ((long)width * height * samples), 1, ModelerTools.MaxAnimationFrames);
        var firsts = new List<RenderImage>();
        int files = 0;
        foreach (var shot in cut)
        {
            for (int a = shot.First; a <= shot.Last; a += chunk)
            {
                int b = Math.Min(shot.Last, a + chunk - 1);
                var r = await engine.RenderAnimation(folder, name, a, b, 1, width, height, samples, shot.Camera, overwrite, null, cancel).ConfigureAwait(false);
                if (r.IsError == true) throw new McpException($"Shot {shot.Name}, frames {a} to {b}: " + string.Join(" ", r.Content.OfType<TextContentBlock>().Select(t => t.Text)));
                files += b - a + 1;
            }
            var file = Path.Combine(Inside(host.Root, folder), AnimationRender.FileName(name, shot.First));
            if (TextureImages.Decode(File.ReadAllBytes(file)) is { } img) firsts.Add(new RenderImage(img.Width, img.Height, img.Rgba));
        }
        var info = new JsonObject
        {
            ["folder"] = folder, ["frames"] = files,
            ["shots"] = new JsonArray([.. cut.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["camera"] = x.Camera, ["frames"] = new JsonArray(x.First, x.Last) })]),
            ["sheet"] = "The picture: each shot's first frame, numbered, in order.",
        };
        if (movie != null)
        {
            var m = await engine.MakeMovie(folder, name, cut[0].First, cut[^1].Last, 1, movie, null, overwrite, cancel).ConfigureAwait(false);
            info["movie"] = JsonNode.Parse(string.Join("", m.Content.OfType<TextContentBlock>().Select(t => t.Text)));
        }
        var sheet = LabelledSheet(firsts, cut.Select(x => (double)x.First).ToList());
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(sheet, "image/png"), new TextContentBlock { Text = info.ToJsonString() }] };
    }
}
