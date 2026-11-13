using System.Buffers.Binary;

namespace AVALightWave.Core.Tests;

/// <summary>The sound track: WAV files read in every common form, their waveform, loudness and brightness, set as one undo step.</summary>
public class SoundTests
{
    private static float[] Tone(double freq, double seconds, int rate, double amp = 0.5) =>
        Enumerable.Range(0, (int)(seconds * rate)).Select(i => (float)(amp * Math.Sin(2 * Math.PI * freq * i / rate))).ToArray();

    [Fact]
    public void Wavs_of_every_common_kind_are_read()
    {
        var mono = Tone(440, 0.5, 8000);
        var sound = WavSound.Read(WavSound.Write(mono, 8000));
        Assert.Equal(8000, sound.SampleRate);
        Assert.Equal(0.5, sound.Seconds, 6);
        Assert.Equal(mono[100], sound.Mono[100], 3);

        // 32-bit float, stereo, WAVE_FORMAT_EXTENSIBLE: the left a tone, the right silent, so the mix is half the tone.
        int n = 800;
        var b = new byte[12 + 8 + 40 + 8 + n * 8];
        void Text(int at, string s) => System.Text.Encoding.ASCII.GetBytes(s).CopyTo(b, at);
        Text(0, "RIFF"); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), b.Length - 8); Text(8, "WAVE");
        Text(12, "fmt "); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 0xFFFE); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), 8000); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(44), 3); // the sub-format: IEEE float
        Text(60, "data"); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(64), n * 8);
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(68 + i * 8), mono[i]);
        var stereo = WavSound.Read(b);
        Assert.Equal(2, stereo.Channels);
        Assert.Equal(mono[200] / 2, stereo.Mono[200], 4);

        Assert.Throws<InvalidDataException>(() => WavSound.Read([1, 2, 3]));
        Assert.Contains("save it as 16-bit PCM", Assert.Throws<InvalidDataException>(() => WavSound.Read(b.Select((x, i) => i == 44 ? (byte)7 : x).ToArray())).Message);
    }

    [Fact]
    public void Peaks_loudness_and_brightness_follow_the_sound()
    {
        // Half a second of silence, then half a second of tone.
        var mono = new float[4000].Concat(Tone(1000, 0.5, 8000, 0.8)).ToArray();
        var sound = WavSound.Read(WavSound.Write(mono, 8000));
        var peaks = sound.Peaks(0, 1, 4);
        Assert.True(peaks[0] < 0.01 && peaks[3] > 0.75);
        Assert.Equal(0, sound.Loudness(0.25, 0.1), 3);
        Assert.Equal(0.8 / Math.Sqrt(2), sound.Loudness(0.75, 0.1), 2);
        var low = WavSound.Read(WavSound.Write(Tone(200, 0.5, 8000), 8000));
        var high = WavSound.Read(WavSound.Write(Tone(2000, 0.5, 8000), 8000));
        Assert.True(high.Brightness(0.25, 0.1) > low.Brightness(0.25, 0.1) * 3);
    }

    [Fact]
    public void A_sound_track_is_set_as_one_undo_step()
    {
        using var w = new Workbench();
        var path = w.File("voice.wav");
        File.WriteAllBytes(path, WavSound.Write(Tone(300, 2, 8000), 8000));
        var d = new SceneDocument();
        SceneSound.Set(d.Session, path, startFrame: 11);
        Assert.Equal("Set Sound", d.Session.History.UndoLabel);
        Assert.Equal(1, SceneSound.SecondsAt(d.Scene, 41), 9);
        Assert.NotNull(SceneSound.Of(d.Scene));
        d.Session.Undo();
        Assert.False(d.Scene.Audio.On);
        File.WriteAllBytes(w.File("bad.wav"), [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => SceneSound.Set(d.Session, w.File("bad.wav")));
    }
}
