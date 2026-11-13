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
}
