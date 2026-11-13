using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Scene;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Characters (milestone C): the sound track, action clips, walk cycles and lip sync.</summary>
public sealed partial class LightWaveTools
{
    [McpServerTool(Name = "set_audio", Destructive = true)]
    [Description("Sets the scene's sound track, as one undo step: a WAV file from the working folder (8, 16, 24 or 32-bit PCM, or 32-bit float), the frame its start plays at (default the scene's first), and its volume (0 to 2, default 1); off takes it away. The window shows its waveform under the timeline and plays it with the timeline and previews; MP4 movies from render_animation and make_movie carry it, lined up with their frames. fit_scene lengthens (or shortens) the scene to end with the sound. lip_sync keys a mouth to it. Returns its length in seconds and frames.")]
    public Task<string> SetAudio(
        [Description("The WAV file, relative to the working folder (not needed with off).")] string? file = null,
        [Description("The frame the sound's start plays at (default: the scene's first frame).")] double? start_frame = null,
        [Description("0 (silent) to 2 (default 1).")] double volume = 1,
        [Description("Make the scene's last frame where the sound ends.")] bool fit_scene = false,
        [Description("Take the sound track off.")] bool off = false,
        CancellationToken cancel = default) =>
        Making("set the sound", (_, l, root) =>
        {
            var s = l.Scene;
            if (off)
            {
                s.Audio = new SceneAudio();
                s.Notify();
                return new JsonObject { ["audio"] = null };
            }
            var full = Inside(root, file ?? throw new ArgumentException("file is needed: a .wav in the working folder (or off true)."));
            if (!File.Exists(full)) throw new ArgumentException($"There's no file {file} in the working folder.");
            WavSound sound;
            try { sound = WavSound.Load(full); }
            catch (InvalidDataException e) { throw new ArgumentException(e.Message, e); }
            s.Audio = new SceneAudio { Path = full, StartFrame = start_frame, Volume = volume }.Checked();
            double start = start_frame ?? s.FirstFrame;
            double frames = sound.Seconds * s.FramesPerSecond;
            if (fit_scene)
            {
                int end = (int)Math.Ceiling(start + frames - 1);
                if (end <= s.FirstFrame) throw new ArgumentException("The sound ends before the scene's first frame, so the scene can't be fitted to it.");
                s.LastFrame = end;
            }
            s.Notify();
            return new JsonObject
            {
                ["audio"] = Path.GetRelativePath(root, full), ["seconds"] = Math.Round(sound.Seconds, 3), ["sample_rate"] = sound.SampleRate,
                ["channels"] = sound.Channels, ["frames"] = new JsonArray(start, Math.Round(start + frames - 1, 2)), ["volume"] = volume,
                ["scene_frames"] = new JsonArray(s.FirstFrame, s.LastFrame),
            };
        }, cancel);

    [McpServerTool(Name = "save_action", Destructive = true)]
    [Description("Keeps an action clip: the keys of some items (an object brings its bones) from one frame to another, every animated channel, with their values at both ends, under a name, kept with the scene (one undo step) and, with file, in an .action file in the working folder too. place_action puts it back at any frame, faster or slower, looped, blended in, on the same items or another character with the same bones.")]
    public Task<string> SaveAction(
        [Description("The action's name, e.g. \"wave\".")] string name,
        [Description("The items, by name (an object brings its bones).")] string[] items,
        [Description("The first frame.")] double from,
        [Description("The last frame.")] double to,
        [Description("Bring each object's bones (default true).")] bool bones = true,
        [Description("Also write it to this .action file in the working folder.")] string? file = null,
        CancellationToken cancel = default) =>
        Making("kept an action", (_, l, root) =>
        {
            if (items == null || items.Length == 0) throw new ArgumentException("items is needed: what the action moves.");
            var clip = ActionClips.Capture(l.Scene, name, items.Select(n => ItemByName(l.Scene, n)), from, to, bones);
            ActionClips.Keep(l, clip);
            var o = new JsonObject
            {
                ["action"] = clip.Name, ["frames"] = clip.Length, ["channels"] = clip.Channels.Count,
                ["items"] = new JsonArray([.. clip.Channels.Select(c => c.Item).Distinct().Select(n => (JsonNode)n)]),
            };
            if (file != null)
            {
                var full = Inside(root, file.EndsWith(ActionClips.Extension, StringComparison.OrdinalIgnoreCase) ? file : file + ActionClips.Extension);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                ActionClips.Save(clip, full);
                o["file"] = Path.GetRelativePath(root, full);
            }
            return o;
        }, cancel);

    [McpServerTool(Name = "place_action", Destructive = true)]
    [Description("Places an action clip (by name from the scene, or from an .action file in the working folder) at a frame, as one undo step: its keys replace those there on its channels, played at speed (2 is twice as fast), repeated loops times end to end, and with blend frames the motion there eases into it (keyed every frame). onto puts it on another object with the same bones (a clip of Robot placed onto Robot 2). The scene is lengthened if it runs past the end. Returns the channels and frames written and anything not found.")]
    public Task<string> PlaceAction(
        [Description("The frame it starts at.")] double at,
        [Description("The action's name in the scene.")] string? name = null,
        [Description("Or an .action file in the working folder.")] string? file = null,
        [Description("Play speed: 2 is twice as fast, 0.5 half (default 1).")] double speed = 1,
        [Description("How many times end to end (default 1).")] int loops = 1,
        [Description("Frames over which the motion there eases into it (default 0).")] double blend = 0,
        [Description("Put it on this object (and its bones) instead of the one it was kept from.")] string? onto = null,
        CancellationToken cancel = default) =>
        Making("placed an action", (_, l, root) =>
        {
            if ((name == null) == (file == null)) throw new ArgumentException("Give name (an action in the scene) or file (an .action file), one of them.");
            ActionClip clip;
            if (file != null)
            {
                var full = Inside(root, file);
                if (!File.Exists(full)) throw new ArgumentException($"There's no file {file} in the working folder.");
                try { clip = ActionClips.Load(full); }
                catch (InvalidDataException e) { throw new ArgumentException(e.Message, e); }
            }
            else clip = ActionClips.Find(l.Scene, name!);
            string? owner = null;
            if (onto != null)
            {
                var owners = clip.Channels.Select(c => c.Item.Split('/')[0]).Distinct().ToList();
                if (owners.Count != 1) throw new ArgumentException($"onto works for a clip of one object (with its bones); this one moves {string.Join(", ", owners)}.");
                owner = owners[0];
                ObjectByName(l.Scene, onto);
            }
            var r = ActionClips.Place(l, clip, at, speed, loops, blend, owner, onto);
            var o = new JsonObject
            {
                ["action"] = clip.Name, ["channels"] = r.Channels, ["keys"] = r.Keys, ["frames"] = new JsonArray(Math.Round(r.First, 3), Math.Round(r.Last, 3)),
                ["scene_frames"] = new JsonArray(l.Scene.FirstFrame, l.Scene.LastFrame),
            };
            if (r.Missing.Count > 0) o["not_found"] = new JsonArray([.. r.Missing.Select(m => (JsonNode)m)]);
            return o;
        }, cancel);

    [McpServerTool(Name = "list_actions", ReadOnly = true, Idempotent = true)]
    [Description("Lists the action clips kept with the scene: each one's name, length in frames, and the items and channels it moves.")]
    public Task<string> ListActions(CancellationToken cancel = default) =>
        Making("listed actions", (_, l, _) => new JsonObject
        {
            ["actions"] = new JsonArray([.. ActionClips.List(l.Scene).Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name, ["frames"] = c.Length,
                ["items"] = new JsonArray([.. c.Channels.Select(x => x.Item).Distinct().Select(n => (JsonNode)n)]),
                ["channels"] = c.Channels.Count,
            })]),
        }, cancel, changes: false);
}
