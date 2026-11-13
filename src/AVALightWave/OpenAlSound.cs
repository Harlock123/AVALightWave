using Silk.NET.OpenAL;

namespace AVALightWave;

/// <summary>
/// Sound through OpenAL Soft (bundled for every platform): one source, its buffer refilled with the part asked for. If
/// there's no sound device (a server, a headless run), it plays nothing and says so once in <see cref="Problem"/>.
/// </summary>
public sealed unsafe class OpenAlSound : ISoundOutput
{
    private readonly ALContext? _alc;
    private readonly AL? _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source;
    private uint _buffer;

    /// <summary>Why there's no sound, or null when it's working.</summary>
    public string? Problem { get; }

    public OpenAlSound()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice("");
            if (_device == null) { Problem = "No sound device was found, so the sound track can't be heard."; return; }
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
            _source = _al.GenSource();
        }
        catch (Exception e) when (e is DllNotFoundException or InvalidOperationException or EntryPointNotFoundException or BadImageFormatException)
        {
            Problem = "Sound couldn't start (OpenAL isn't available here), so the sound track can't be heard.";
            _al = null;
        }
    }

    private bool Ready => Problem == null && _al != null;

    public void Play(WavSound sound, double fromSeconds, double? seconds, double volume)
    {
        if (!Ready) return;
        Stop();
        int start = (int)Math.Max(0, Math.Round(fromSeconds * sound.SampleRate));
        int end = seconds is { } s ? Math.Min(sound.Mono.Length, start + (int)Math.Round(s * sound.SampleRate)) : sound.Mono.Length;
        if (end <= start) return;
        int ch = Math.Min(2, sound.Channels);
        var part = new short[(end - start) * ch];
        for (int f = start; f < end; f++)
            for (int c = 0; c < ch; c++) part[(f - start) * ch + c] = sound.Pcm16[f * sound.Channels + c];
        var al = _al!;
        _alc!.MakeContextCurrent(_context);
        _buffer = al.GenBuffer();
        al.BufferData(_buffer, ch == 2 ? BufferFormat.Stereo16 : BufferFormat.Mono16, part, sound.SampleRate);
        al.SetSourceProperty(_source, SourceInteger.Buffer, (int)_buffer);
        al.SetSourceProperty(_source, SourceFloat.Gain, (float)Math.Clamp(volume, 0, 2));
        // A sound starting later than now: wait in silence by starting it that much later.
        if (fromSeconds < 0)
        {
            var delay = TimeSpan.FromSeconds(-fromSeconds);
            var source = _source;
            _ = Task.Delay(delay).ContinueWith(_ => { lock (this) { if (_buffer != 0) al.SourcePlay(source); } }, TaskScheduler.Default);
        }
        else al.SourcePlay(_source);
    }

    public void Stop()
    {
        if (!Ready) return;
        lock (this)
        {
            var al = _al!;
            _alc!.MakeContextCurrent(_context);
            al.SourceStop(_source);
            al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
            if (_buffer != 0) al.DeleteBuffer(_buffer);
            _buffer = 0;
        }
    }

    public void Dispose()
    {
        if (!Ready) return;
        Stop();
        _al!.DeleteSource(_source);
        _alc!.MakeContextCurrent(null);
        _alc.DestroyContext(_context);
        _alc.CloseDevice(_device);
    }
}

/// <summary>No sound: what test windows use (it remembers what it was asked, for the tests).</summary>
public sealed class SilentSound : ISoundOutput
{
    public List<(double From, double? Seconds, double Volume)> Played { get; } = [];
    public int Stops { get; private set; }
    public void Play(WavSound sound, double fromSeconds, double? seconds, double volume) => Played.Add((fromSeconds, seconds, volume));
    public void Stop() => Stops++;
    public void Dispose() { }
}
