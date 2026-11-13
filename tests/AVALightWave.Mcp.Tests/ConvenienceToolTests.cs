using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Conveniences: camera bookmarks and packaging a scene.</summary>
public class ConvenienceToolTests
{
    [Fact]
    public async Task Bookmarks_are_saved_listed_recalled_and_removed()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("set_transform", new { item = "Camera", position = new[] { 2.0, 1, -6 }, key = false });
        var saved = await s.Client.Json("camera_bookmark", new { action = "save", name = "Wide" });
        Assert.Equal(2, (double)saved["saved"]!["position"]![0]!, 6);
        Assert.Equal("Agent: save a bookmark", b.Host.Layout.History.UndoLabel);
        await s.Client.Json("set_transform", new { item = "Camera", position = new[] { 0.0, 0, 0 }, key = false });
        var back = await s.Client.Json("camera_bookmark", new { action = "recall", name = "Wide", frame = 10 });
        Assert.False((bool)back["keyed"]!);
        Assert.Equal(-6, b.Scene.CurrentCamera!.PositionAt(10).Z, 9);
        var list = await s.Client.Json("camera_bookmark", new { action = "list" });
        Assert.Single(list["bookmarks"]!.AsArray());
        await s.Client.Json("camera_bookmark", new { action = "remove", name = "Wide" });
        Assert.Contains("no camera bookmarks", await s.Client.Fails("camera_bookmark", new { action = "recall", name = "Wide" }));
        Assert.Contains("action must be", await s.Client.Fails("camera_bookmark", new { action = "jump" }));
        Assert.Contains("isn't a camera", await s.Client.Fails("camera_bookmark", new { action = "save", name = "X", camera = "Light" }));
    }

    [Fact]
    public async Task Package_scene_writes_inside_the_working_folder_and_changes_nothing()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("add_object", new { file = "objects/crate.lwo" });
        await s.Client.Json("make_object", new { shape = "ball" });
        int steps = b.Host.Layout.History.Count;
        var r = await s.Client.Json("package_scene", new { folder = "packages/shot", zip = true });
        Assert.Equal("packages/shot/shot.avascene", ((string)r["scene"]!).Replace('\\', '/'));
        Assert.Contains(r["files"]!.AsArray(), f => ((string)f!).EndsWith("Ball.lwo", StringComparison.Ordinal));
        Assert.True(File.Exists(b.Folder.File("packages/shot.zip")));
        Assert.Equal(steps, b.Host.Layout.History.Count);
        Assert.Contains("already has files", await s.Client.Fails("package_scene", new { folder = "packages/shot" }));
        Assert.Contains("outside the working folder", await s.Client.Fails("package_scene", new { folder = "../away" }));
        await s.Client.Json("package_scene", new { folder = "packages/shot", overwrite = true });
    }

    [Fact]
    public async Task Set_audio_sets_the_sound_track_and_can_fit_the_scene()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var tone = Enumerable.Range(0, 8000 * 3).Select(i => (float)(0.4 * Math.Sin(i * 0.2))).ToArray();
        File.WriteAllBytes(b.Folder.File("voice.wav"), WavSound.Write(tone, 8000));
        var r = await s.Client.Json("set_audio", new { file = "voice.wav", start_frame = 11, fit_scene = true });
        Assert.Equal(3, (double)r["seconds"]!, 3);
        Assert.Equal(100, b.Scene.LastFrame);
        Assert.Equal(11, b.Scene.Audio.StartFrame);
        Assert.Equal("Agent: set the sound", b.Host.Layout.History.UndoLabel);
        Assert.Contains("no file", await s.Client.Fails("set_audio", new { file = "none.wav" }));
        File.WriteAllBytes(b.Folder.File("text.wav"), "hello"u8.ToArray());
        Assert.Contains("isn't a WAV", await s.Client.Fails("set_audio", new { file = "text.wav" }));
        await s.Client.Json("set_audio", new { off = true });
        Assert.False(b.Scene.Audio.On);
    }

    [Fact]
    public async Task Lip_sync_opens_the_mouth_with_the_voice()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        // Four syllables of a quarter second at frames 1-8, 16-23, 31-38, 46-53, with silence between (at 30 fps).
        const int rate = 16000;
        var voice = new float[rate * 2];
        for (int i = 0; i < voice.Length; i++)
        {
            double t = (double)i / rate;
            bool on = (t % 0.5) < 0.25;
            voice[i] = on ? (float)(0.5 * Math.Sin(2 * Math.PI * (t < 1 ? 300 : 1800) * t)) : 0;
        }
        File.WriteAllBytes(b.Folder.File("voice.wav"), WavSound.Write(voice, rate));
        Assert.Contains("no sound track", await s.Client.Fails("lip_sync", new { item = "Camera", channel = "zoom", open = 2 }));
        await s.Client.Json("set_audio", new { file = "voice.wav" });
        await s.Client.Json("make_object", new { shape = "box", name = "Mouth", size = new[] { 0.3, 0.1, 0.05 } });
        Assert.Contains("has no morph", await s.Client.Fails("lip_sync", new { item = "Mouth" }));
        var r = await s.Client.Json("lip_sync", new
        {
            item = "Mouth", channel = "scale.y", closed = 0.1, open = 1, every = 1, wide_channel = "scale.x", wide_closed = 0.8, wide_open = 1.4,
        });
        var mouth = b.Scene.Find("Mouth")!;
        Assert.True(mouth.ScaleAt(5).Y > 0.6, $"open in a syllable: {mouth.ScaleAt(5).Y}");
        Assert.True(mouth.ScaleAt(12).Y < 0.35, $"shut between: {mouth.ScaleAt(12).Y}");
        Assert.True(mouth.ScaleAt(35).Y > 0.6);
        Assert.True((int)r["keys"]! < 60, "the keys weren't thinned");
        Assert.True((int)r["wide_keys"]! > 0);
        // The bright second half (1800 Hz) is wider than the dark first (300 Hz).
        Assert.True(mouth.ScaleAt(50).X > mouth.ScaleAt(5).X + 0.1);
        Assert.Equal("Agent: lip synced", b.Host.Layout.History.UndoLabel);
    }
}
