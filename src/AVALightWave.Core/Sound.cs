using System.Buffers.Binary;
using AVAModeler.Layout;
using AVAModeler.Scene;

namespace AVALightWave;

/// <summary>
/// A WAV file's sound, mixed down to one channel of samples from -1 to 1: for the timeline's waveform, for lip sync's
/// loudness, and for playing. PCM of 8, 16, 24 or 32 bits and 32-bit float, plain or WAVE_FORMAT_EXTENSIBLE.
/// </summary>
public sealed class WavSound
{
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    /// <summary>The sound in one channel, -1 to 1.</summary>
    public required float[] Mono { get; init; }
    /// <summary>The file's own samples as 16-bit, interleaved, for playing.</summary>
    public required short[] Pcm16 { get; init; }

    public double Seconds => Mono.Length / (double)SampleRate;

    /// <summary>The most a WAV may hold here: an hour.</summary>
    public const int MaxSeconds = 3600;

    private static readonly Dictionary<string, (DateTime When, long Size, WavSound Sound)> Cache = [];

    /// <summary>A file's sound, read once and kept while the file is unchanged.</summary>
    public static WavSound Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"There's no sound file {path}.");
        lock (Cache)
        {
            if (Cache.TryGetValue(info.FullName, out var had) && had.When == info.LastWriteTimeUtc && had.Size == info.Length) return had.Sound;
            var sound = Read(File.ReadAllBytes(info.FullName));
            if (Cache.Count > 8) Cache.Clear();
            Cache[info.FullName] = (info.LastWriteTimeUtc, info.Length, sound);
            return sound;
        }
    }

    /// <summary>A WAV file's bytes as sound, or an <see cref="InvalidDataException"/> saying why not.</summary>
    public static WavSound Read(byte[] b)
    {
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' || b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw new InvalidDataException("That isn't a WAV file (sound tracks are .wav).");
        int format = 0, channels = 0, rate = 0, bits = 0;
        int dataAt = -1, dataSize = 0;
        for (int at = 12; at + 8 <= b.Length;)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at + 4));
            if (size < 0) break;
            int body = at + 8;
            if (id == "fmt " && body + 16 <= b.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 14));
                // WAVE_FORMAT_EXTENSIBLE: the real format is the first two bytes of the sub-format GUID.
                if (format == 0xFFFE && size >= 40 && body + 26 <= b.Length) format = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 24));
            }
            else if (id == "data")
            {
                dataAt = body;
                dataSize = Math.Min(size, b.Length - body);
            }
            at = body + size + (size & 1);
        }
        if (dataAt < 0 || channels < 1 || rate < 1) throw new InvalidDataException("The WAV file has no sound in it (no fmt or data chunk).");
        bool isFloat = format == 3;
        if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(isFloat && bits == 32))
            throw new InvalidDataException($"The WAV file's samples are {(isFloat ? "float" : "format " + format)} at {bits} bits; save it as 16-bit PCM.");
        int bytes = bits / 8, frames = dataSize / (bytes * channels);
        if (frames / (double)rate > MaxSeconds) throw new InvalidDataException($"The sound is longer than an hour: shorten it first.");
        var mono = new float[frames];
        var pcm = new short[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = dataAt + (f * channels + c) * bytes;
                double v = bits switch
                {
                    8 => (b[o] - 128) / 128.0,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o)) / 32768.0,
                    24 => ((b[o] | b[o + 1] << 8 | (sbyte)b[o + 2] << 16)) / 8388608.0,
                    _ => isFloat ? BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o)) : BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) / 2147483648.0,
                };
                v = Math.Clamp(v, -1, 1);
                sum += v;
                pcm[f * channels + c] = (short)Math.Round(v * 32767);
            }
            mono[f] = (float)(sum / channels);
        }
        return new WavSound { SampleRate = rate, Channels = channels, Mono = mono, Pcm16 = pcm };
    }

    /// <summary>The loudest sample (absolute) in each of <paramref name="buckets"/> spans from <paramref name="from"/> to <paramref name="to"/> seconds (0 outside the sound).</summary>
    public float[] Peaks(double from, double to, int buckets)
    {
        var peaks = new float[Math.Max(0, buckets)];
        if (buckets <= 0 || to <= from) return peaks;
        for (int i = 0; i < buckets; i++)
        {
            int a = (int)Math.Floor((from + (to - from) * i / buckets) * SampleRate);
            int z = (int)Math.Ceiling((from + (to - from) * (i + 1) / buckets) * SampleRate);
            float most = 0;
            for (int s = Math.Max(0, a); s < Math.Min(Mono.Length, Math.Max(a + 1, z)); s++) most = Math.Max(most, Math.Abs(Mono[s]));
            peaks[i] = most;
        }
        return peaks;
    }

    /// <summary>How loud it is (root mean square, 0 to 1) over <paramref name="window"/> seconds centred on <paramref name="at"/>.</summary>
    public double Loudness(double at, double window)
    {
        int a = Math.Max(0, (int)((at - window / 2) * SampleRate)), z = Math.Min(Mono.Length, (int)((at + window / 2) * SampleRate));
        if (z <= a) return 0;
        double sum = 0;
        for (int s = a; s < z; s++) sum += Mono[s] * (double)Mono[s];
        return Math.Sqrt(sum / (z - a));
    }

    /// <summary>
    /// How bright it is (0 to 1) over a window: the share of its changes that are sign changes, a cheap stand-in for
    /// how much high sound there is (wide vowels and hisses are brighter than oo and m).
    /// </summary>
    public double Brightness(double at, double window)
    {
        int a = Math.Max(1, (int)((at - window / 2) * SampleRate)), z = Math.Min(Mono.Length, (int)((at + window / 2) * SampleRate));
        if (z <= a) return 0;
        int crossings = 0;
        for (int s = a; s < z; s++) if ((Mono[s] >= 0) != (Mono[s - 1] >= 0)) crossings++;
        // A crossing every 4 samples or more often is as bright as it gets.
        return Math.Clamp(crossings / ((z - a) / 4.0), 0, 1);
    }

    /// <summary>A sound file's 16-bit PCM WAV bytes from mono samples (for tests and examples).</summary>
    public static byte[] Write(float[] mono, int rate)
    {
        var o = new byte[44 + mono.Length * 2];
        void Text(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(o, at);
        Text(0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(4), o.Length - 8);
        Text(8, "WAVE");
        Text(12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(34), 16);
        Text(36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(o.AsSpan(40), mono.Length * 2);
        for (int i = 0; i < mono.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(o.AsSpan(44 + i * 2), (short)Math.Round(Math.Clamp(mono[i], -1, 1) * 32767));
        return o;
    }
}

/// <summary>Where sound is played: the app's OpenAL output, or a test's stand-in that records what it was asked.</summary>
public interface ISoundOutput : IDisposable
{
    /// <summary>Plays a sound from <paramref name="fromSeconds"/> for <paramref name="seconds"/> (or to its end), at a volume; stops what was playing.</summary>
    void Play(WavSound sound, double fromSeconds, double? seconds, double volume);

    void Stop();
}

/// <summary>The scene's sound track as the window uses it: the sound at a frame, set as one undo step.</summary>
public static class SceneSound
{
    /// <summary>The scene's sound, or null when it has none or it can't be read.</summary>
    public static WavSound? Of(Scene scene)
    {
        if (!scene.Audio.On) return null;
        try { return WavSound.Load(scene.Audio.Path); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Seconds into the scene's sound at a frame.</summary>
    public static double SecondsAt(Scene s, double frame) => s.Audio.SecondsAt(frame, s.FirstFrame, s.FramesPerSecond);

    /// <summary>Sets the scene's sound (a WAV file, read to check it) as one undo step; null for none.</summary>
    public static void Set(AVAModeler.Layout.LayoutSession session, string? path, double? startFrame = null, double volume = 1)
    {
        SceneAudio audio = new();
        if (path != null)
        {
            WavSound.Load(path);
            audio = new SceneAudio { Path = Path.GetFullPath(path), StartFrame = startFrame, Volume = volume }.Checked();
        }
        session.Edit(path == null ? "Remove Sound" : "Set Sound", () => { session.Scene.Audio = audio; session.Scene.Notify(); });
    }
}

/// <summary>
/// Lip sync from the sound track's loudness (milestone C, stage 4): a channel (a mouth morph, a jaw bone's pitch, a
/// mouth's scale) keyed from closed to open as the sound is loud, and optionally another from narrow to wide as it is
/// bright, the keys thinned to the shapes that matter. It follows loudness, not words: mouths move with the voice, not
/// phoneme by phoneme.
/// </summary>
public static class LipSync
{
    /// <summary>What lip sync keyed.</summary>
    public sealed record Keyed(int Keys, int WideKeys, double First, double Last);

    /// <summary>The voice's openness (0 to 1) at frames from first to last, every <paramref name="step"/> frames.</summary>
    public static List<(double Frame, double Open, double Wide)> Shapes(Scene s, WavSound sound, double first, double last, double step, double threshold)
    {
        double fps = s.FramesPerSecond > 0 ? s.FramesPerSecond : 30, window = 1.5 / fps;
        var frames = new List<double>();
        for (double f = first; f < last - 1e-9; f += step) frames.Add(Math.Round(f, 4));
        frames.Add(last);
        var loud = frames.Select(f => sound.Loudness(SceneSound.SecondsAt(s, f), window)).ToList();
        var bright = frames.Select(f => sound.Brightness(SceneSound.SecondsAt(s, f), window)).ToList();
        // Loud is relative to this voice: its loudest twentieth sets full open.
        var sorted = loud.Order().ToList();
        double top = Math.Max(1e-4, sorted[(int)Math.Clamp(Math.Round(sorted.Count * 0.95), 0, sorted.Count - 1)]);
        var shapes = new List<(double, double, double)>();
        for (int i = 0; i < frames.Count; i++)
        {
            double open = Math.Clamp(loud[i] / top, 0, 1);
            open = open < threshold ? 0 : (open - threshold) / (1 - threshold);
            // Mouths open fast and close a little slower: a touch of the frame before.
            if (i > 0) open = Math.Max(open, shapes[i - 1].Item2 * 0.55);
            shapes.Add((frames[i], open, open > 0 ? Math.Clamp(bright[i] * 1.6, 0, 1) : 0));
        }
        return shapes;
    }

    /// <summary>
    /// Keys an envelope from the shapes (closed + (open − closed) × openness), thinned: a key is left out where the
    /// keys either side already pass within <paramref name="tolerance"/> (of the full range) of it. Replaces the keys
    /// from first to last. Call inside an edit.
    /// </summary>
    public static int Key(Scene s, Envelope env, IReadOnlyList<(double Frame, double Amount)> shape, double closed, double open, double tolerance)
    {
        double first = shape[0].Frame, last = shape[^1].Frame;
        for (int i = env.Keys.Count - 1; i >= 0; i--)
            if (env.Keys[i].Frame >= first - 1e-9 && env.Keys[i].Frame <= last + 1e-9 && env.Keys.Count > 1) env.RemoveKey(i);
        // Douglas–Peucker on (frame, amount): keep the points the line between kept neighbours misses by more than the tolerance.
        var keep = new bool[shape.Count];
        keep[0] = keep[^1] = true;
        void Thin(int a, int b)
        {
            if (b - a < 2) return;
            int worst = -1;
            double most = tolerance;
            for (int i = a + 1; i < b; i++)
            {
                double t = (shape[i].Frame - shape[a].Frame) / (shape[b].Frame - shape[a].Frame);
                double line = shape[a].Amount + (shape[b].Amount - shape[a].Amount) * t;
                double off = Math.Abs(shape[i].Amount - line);
                if (off > most) { most = off; worst = i; }
            }
            if (worst < 0) return;
            keep[worst] = true;
            Thin(a, worst);
            Thin(worst, b);
        }
        Thin(0, shape.Count - 1);
        int n = 0;
        for (int i = 0; i < shape.Count; i++)
        {
            if (!keep[i]) continue;
            Keying.Key(env, s, new EnvelopeKey(shape[i].Frame, closed + (open - closed) * shape[i].Amount), keepShape: false);
            n++;
        }
        return n;
    }
}
