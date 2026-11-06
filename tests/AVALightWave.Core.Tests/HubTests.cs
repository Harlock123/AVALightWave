using System.Diagnostics;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Hub;
using AVAModeler.Scene;

namespace AVALightWave.Core.Tests;

/// <summary>
/// AVALightWave's end of the hub, both ways, in one process on pipes of the tests' own with a stand-in for AVA
/// Modeler: Edit in Modeler (by the object's file, or a copy in the hub folder, and starting AVA Modeler, faked, when
/// it isn't there), AVA Modeler's saves updating the scene by link or by file as one undo step, and Send to
/// AVALightWave adding or updating an object. Nothing reaches a real AVA Modeler or the person's settings.
/// </summary>
public sealed class HubTests : IDisposable
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly Workbench _w = new();
    private readonly SceneDocument _doc = new();
    private readonly LightWaveHub _hub;

    public HubTests()
    {
        // Messages are answered at once here (the window hands them to its UI thread).
        _hub = new LightWaveHub(_doc, work => Task.FromResult(work()))
        {
            Pipe = Pipe(), ModelerPipe = Pipe(), SettingsFolder = _w.File("settings"),
            Launcher = new AppLauncher(HubProtocol.Modeler) { BaseDirectory = _w.File("nowhere"), SearchPath = "", Start = _ => false },
            Timeout = TimeSpan.FromSeconds(2),
        };
    }

    public void Dispose()
    {
        _hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _w.Dispose();
    }

    private static string Pipe() => "avalightwave-hubtest-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>A stand-in AVA Modeler on the hub, answering with <paramref name="answer"/> and keeping what it's sent.</summary>
    private HubListener FakeModeler(List<HubMessage> got, Func<HubMessage, JsonObject>? answer = null)
    {
        var l = new HubListener(_hub.ModelerPipe, m =>
        {
            lock (got) got.Add(m);
            return Task.FromResult(answer?.Invoke(m) ?? HubProtocol.Ok());
        });
        l.Start();
        return l;
    }

    private Task<JsonObject> FromModeler(HubMessage m) => HubClient.SendAsync(_hub.Pipe, m, Wait, Cancel);

    [Fact]
    public async Task Edit_in_modeler_sends_the_objects_own_file_with_a_link()
    {
        var got = new List<HubMessage>();
        await using var modeler = FakeModeler(got);
        var pawn = _doc.LoadObject(_w.Object("pawn.lwo"));
        var said = await _hub.EditInModeler(pawn);
        Assert.Contains("open in AVA Modeler", said);
        var m = Assert.Single(got);
        Assert.Equal(HubMessage.EditObject, m.Type);
        Assert.Equal(HubProtocol.LightWave, m.From);
        Assert.Equal(_w.Object("pawn.lwo"), m.Path);
        Assert.Equal("pawn", m.Name);
        Assert.Equal(_doc.LinkOf(pawn), m.Link);
        Assert.Equal([pawn], _doc.Linked(m.Link!));
    }

    [Fact]
    public async Task An_object_with_unsaved_changes_goes_as_a_copy_in_the_hub_folder()
    {
        var got = new List<HubMessage>();
        await using var modeler = FakeModeler(got);
        var ball = _doc.LoadObject(_w.Object("ball.lwo"));
        _doc.EditSurface(ball, 0, "Colour", s => s.Color = new Rgb(9, 9, 9));
        await _hub.EditInModeler(ball);
        var m = Assert.Single(got);
        Assert.StartsWith(Path.Combine(_w.File("settings"), "hub"), m.Path);
        Assert.Equal(new Rgb(9, 9, 9), ObjectFiles.Load(m.Path!).Surfaces[0].Color);
        // The original file is untouched.
        Assert.NotEqual(new Rgb(9, 9, 9), ObjectFiles.Load(_w.Object("ball.lwo")).Surfaces[0].Color);
    }

    [Fact]
    public async Task A_refusal_and_an_absent_modeler_are_said_plainly()
    {
        var pawn = _doc.LoadObject(_w.Object("pawn.lwo"));
        var started = new List<ProcessStartInfo>();
        Assert.Contains("couldn't be found", await _hub.EditInModeler(pawn));
        var program = _w.File(OperatingSystem.IsWindows() ? "AVAModeler.exe" : "AVAModeler");
        File.WriteAllText(program, "");
        _hub.Launcher = new AppLauncher(HubProtocol.Modeler) { BaseDirectory = _w.Path, SearchPath = "", Start = i => { started.Add(i); return true; } };
        var clock = Stopwatch.StartNew();
        Assert.Equal("Started AVA Modeler with pawn: save it there (Ctrl+S) to update it here.", await _hub.EditInModeler(pawn));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8));
        var info = Assert.Single(started);
        Assert.Equal(program, info.FileName);
        Assert.Equal([_w.Object("pawn.lwo"), "--hub-link", _doc.LinkOf(pawn)], info.ArgumentList);

        await using var modeler = FakeModeler([], _ => HubProtocol.Error("AVA Modeler has unsaved changes to ship.lwo."));
        Assert.Equal("AVA Modeler didn't open pawn: AVA Modeler has unsaved changes to ship.lwo.", await _hub.EditInModeler(pawn));
    }

    [Fact]
    public async Task A_save_in_the_modeler_updates_the_linked_object_keeping_its_motion_as_one_undo_step()
    {
        Assert.Null(_hub.Start());
        var pawn = _doc.LoadObject(_w.Object("pawn.lwo"));
        _doc.Session.SetFrame(20);
        _doc.Session.SetVector(pawn, 0, new Vec3(1, 0, 0));
        var link = _doc.LinkOf(pawn);
        var old = pawn.Document;
        // AVA Modeler saved it under a new name (a .lwo of its own): the link still finds it.
        var saved = _w.File("pawn-v2.lwo");
        File.Copy(_w.Object("crate.lwo"), saved);
        var reply = await FromModeler(new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, saved, link, "pawn-v2"));
        Assert.Equal(1, (int?)reply["updated"]);
        Assert.Equal(8, pawn.BaseMesh().Points.Count);
        Assert.Equal(saved, pawn.SourcePath);
        Assert.Equal(new Vec3(1, 0, 0), pawn.PositionAt(20));
        Assert.Equal("Update pawn from AVA Modeler", _doc.Session.History.UndoLabel);
        _doc.Session.Undo();
        Assert.Same(old, pawn.Document);
    }

    [Fact]
    public async Task A_save_with_no_link_updates_every_object_from_that_file()
    {
        Assert.Null(_hub.Start());
        var a = _doc.LoadObject(_w.Object("crate.lwo"));
        var b = _doc.LoadObject(_w.Object("crate.lwo"));
        var other = _doc.LoadObject(_w.Object("spaceship.lwo"));
        File.Copy(_w.Object("pawn.lwo"), _w.Object("crate.lwo"), overwrite: true);
        var reply = await FromModeler(new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, _w.Object("crate.lwo")));
        Assert.Equal(2, (int?)reply["updated"]);
        Assert.Equal(482, a.BaseMesh().Points.Count);
        Assert.Equal(482, b.BaseMesh().Points.Count);
        Assert.Equal(244, other.BaseMesh().Points.Count);
        // A file the scene doesn't use changes nothing.
        reply = await FromModeler(new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, _w.Object("floor.lwo")));
        Assert.Equal(0, (int?)reply["updated"]);
    }

    [Fact]
    public async Task Send_to_lightwave_adds_an_object_then_updates_it()
    {
        Assert.Null(_hub.Start());
        string? said = null;
        _hub.Said += s => said = s;
        var reply = await FromModeler(new HubMessage(HubMessage.AddObject, HubProtocol.Modeler, _w.Object("spaceship.lwo"), "link-9", "Ship"));
        Assert.False((bool?)reply["updated"]);
        var ship = Assert.Single(_doc.Scene.Objects);
        Assert.Equal("Ship", ship.Name);
        Assert.Equal(244, ship.BaseMesh().Points.Count);
        Assert.Contains("AVA Modeler sent Ship", said);
        reply = await FromModeler(new HubMessage(HubMessage.AddObject, HubProtocol.Modeler, _w.Object("crate.lwo"), "link-9", "Ship"));
        Assert.True((bool?)reply["updated"]);
        Assert.Same(ship, Assert.Single(_doc.Scene.Objects));
        Assert.Equal(8, ship.BaseMesh().Points.Count);
        // A new scene has none of the old one's links.
        _doc.New();
        Assert.Empty(_doc.Linked("link-9"));
    }

    [Fact]
    public async Task Hello_is_answered_and_the_rest_refused()
    {
        Assert.Null(_hub.Start());
        Assert.True(_hub.Running);
        Assert.Equal(HubProtocol.LightWave, (string?)(await FromModeler(new HubMessage(HubMessage.Hello, "test")))["app"]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FromModeler(new HubMessage(HubMessage.EditObject, "test", _w.Object("pawn.lwo"), "x")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => FromModeler(new HubMessage(HubMessage.ObjectSaved, "test", _w.File("gone.lwo"))));
        // A second AVALightWave can't take the same pipe.
        var second = new LightWaveHub(new SceneDocument(), work => Task.FromResult(work())) { Pipe = _hub.Pipe };
        Assert.NotNull(second.Start());
        Assert.False(second.Running);
        await _hub.StopAsync();
        Assert.False(_hub.Running);
        Assert.False(HubClient.Answers(_hub.Pipe));
    }

    [Fact]
    public async Task Live_updates_from_modeler_merge_into_one_undo_step_and_keep_the_objects_file()
    {
        _hub.Start();
        var pawn = _doc.LoadObject(_w.Object("pawn.lwo"));
        pawn.SetPosition(10, new Vec3(1, 0, 0));
        var link = _doc.LinkOf(pawn);
        int steps = _doc.Session.History.Count;
        // Three live copies in a row, as AVA Modeler sends them while someone models.
        for (int i = 1; i <= 3; i++)
        {
            var copy = _w.Triangle($"live{i}.lwo", new Rgb((byte)(i * 60), 0, 0));
            var reply = await FromModeler(new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, copy, link, "pawn"));
            Assert.True((bool?)reply["ok"]);
            await _hub.LastLive!;
        }
        Assert.Equal(3, pawn.BaseMesh().Points.Count);
        Assert.Equal(new Rgb(180, 0, 0), pawn.Surfaces[0].Color);
        Assert.Equal(_w.Object("pawn.lwo"), pawn.SourcePath);
        Assert.True(pawn.Document!.Modified);
        Assert.Equal(1, pawn.PositionAt(10).X, 9);
        Assert.Equal(steps + 1, _doc.Session.History.Count);
        Assert.Equal("Live update pawn from AVA Modeler", _doc.Session.History.UndoLabel);
        Assert.Contains("linked pawn", _hub.Status.Text);
        Assert.Contains("synced", _hub.Status.Text);
        _doc.Session.Undo();
        Assert.Equal(482, pawn.BaseMesh().Points.Count);
        // Without a link it's refused; with a link nothing uses, nothing changes.
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => FromModeler(new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, _w.Triangle("x.lwo", Rgb.White), null, "x")));
        Assert.Contains("needs its link", e.Message);
        await FromModeler(new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, _w.File("x.lwo"), "nobody", "x"));
        await _hub.LastLive!;
        Assert.Equal(482, pawn.BaseMesh().Points.Count);
    }

    [Fact]
    public async Task The_status_follows_modeler_coming_and_going()
    {
        Assert.Equal("Hub: off", _hub.Status.Text);
        _hub.Start();
        await _hub.CheckPresenceAsync();
        Assert.Equal("Hub: on (AVA Modeler not running)", _hub.Status.Text);
        int changes = 0;
        _hub.StatusChanged += () => changes++;
        var got = new List<HubMessage>();
        await using (FakeModeler(got))
        {
            await _hub.CheckPresenceAsync();
            Assert.Equal("Hub: AVA Modeler connected", _hub.Status.Text);
        }
        await _hub.CheckPresenceAsync();
        Assert.Equal("Hub: on (AVA Modeler not running)", _hub.Status.Text);
        Assert.Equal(2, changes);
    }
}
