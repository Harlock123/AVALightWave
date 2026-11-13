using Avalonia.Controls;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVALightWave.Core.Tests;
using AVALightWave.Mcp;
using AVAModeler.Chat.Tests;
using AVAModeler.Hub;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AVALightWave.UI.Tests;

/// <summary>
/// The window's links to the outside, on pipes of the tests' own: the hub both ways with a stand-in AVA Modeler, an AI
/// agent through AVALightWave's own live pipe (Layout-only tools, Ctrl+Z undoing its steps), and the AI Chat against
/// a fake Anthropic API. Nothing reaches a real AVA Modeler, a real model or the person's settings.
/// </summary>
public class LinkTests
{
    private static string Pipe() => "avalightwave-uitest-" + Guid.NewGuid().ToString("N")[..12];

    [AvaloniaFact]
    public async Task Edit_in_modeler_goes_to_ava_modeler_and_its_save_comes_back_live()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.Hub.Pipe = Pipe();
        w.Hub.ModelerPipe = Pipe();
        w.Preferences.HubEnabled = true;
        w.SyncHub();
        Assert.True(w.Hub.Running);
        var got = new List<HubMessage>();
        await using var modeler = new HubListener(w.Hub.ModelerPipe, m => { lock (got) got.Add(m); return Task.FromResult(HubProtocol.Ok()); });
        modeler.Start();

        var pawn = w.LoadObject(bench.Object("pawn.lwo"))!;
        w.Session.SetFrame(15);
        w.Session.SetVector(pawn, 0, new AVAModeler.Core.Vec3(0, 1, 0));
        Ui.Key(w, Key.E, RawInputModifiers.Control);
        Ui.Until(() => { lock (got) return got.Count == 1; }, "AVA Modeler wasn't sent the object.");
        HubMessage sent;
        lock (got) sent = got[0];
        Assert.Equal(HubMessage.EditObject, sent.Type);
        Assert.Equal(bench.Object("pawn.lwo"), sent.Path);
        Ui.Until(() => w.Status.Contains("open in AVA Modeler"), "The status bar didn't say so.");

        // AVA Modeler saves it (as a crate, for a change you can count): the scene's object follows, keeping its motion.
        File.Copy(bench.Object("crate.lwo"), bench.Object("pawn.lwo"), overwrite: true);
        var reply = Ui.Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.ObjectSaved, HubProtocol.Modeler, sent.Path, sent.Link, "pawn"), TimeSpan.FromSeconds(10)));
        Assert.Equal(1, (int?)reply["updated"]);
        Assert.Equal(8, pawn.BaseMesh().Points.Count);
        Assert.Equal(1, pawn.PositionAt(15).Y, 9);
        Assert.Contains("Updated pawn from AVA Modeler", w.Status);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal(482, pawn.BaseMesh().Points.Count);

        // Send to AVALightWave from AVA Modeler: a new object in the scene.
        reply = Ui.Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.AddObject, HubProtocol.Modeler, bench.Object("spaceship.lwo"), "ship-link", "Ship"), TimeSpan.FromSeconds(10)));
        Assert.Equal("Ship", (string?)reply["item"]);
        Assert.Contains(w.Session.Scene.Objects, o => o.Name == "Ship");
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task Live_sync_from_modeler_updates_the_view_and_the_hub_light()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.Hub.Pipe = Pipe();
        w.Hub.ModelerPipe = Pipe();
        w.Preferences.HubEnabled = true;
        w.SyncHub();
        var hubText = Ui.Find<TextBlock>(w, "HubStatus");
        Assert.Equal("Hub: on (AVA Modeler not running)", w.Hub.Status.Text);
        await using var modeler = new HubListener(w.Hub.ModelerPipe, _ => Task.FromResult(HubProtocol.Ok()));
        modeler.Start();
        Ui.Wait(w.Hub.CheckPresenceAsync());
        Ui.Until(() => hubText.Text == "Hub: AVA Modeler connected", "The hub light didn't see AVA Modeler.");
        var pawn = w.LoadObject(bench.Object("pawn.lwo"))!;
        var link = w.Document.LinkOf(pawn);
        for (int i = 0; i < 2; i++)
        {
            var reply = Ui.Wait(HubClient.SendAsync(w.Hub.Pipe, new HubMessage(HubMessage.ObjectChanged, HubProtocol.Modeler, bench.Object("crate.lwo"), link, "pawn"), TimeSpan.FromSeconds(10)));
            Assert.True((bool?)reply["ok"]);
            Ui.Wait(w.Hub.LastLive!);
        }
        Assert.Equal(8, pawn.BaseMesh().Points.Count);
        Ui.Until(() => hubText.Text?.Contains("linked pawn · synced") == true, "The hub light didn't show the sync.");
        Assert.Equal("Live update pawn from AVA Modeler", w.Session.History.UndoLabel);
        Ui.Key(w, Key.Z, RawInputModifiers.Control);
        Assert.Equal(482, pawn.BaseMesh().Points.Count);
        await w.Hub.StopAsync();
    }

    [AvaloniaFact]
    public async Task An_agent_works_on_the_open_scene_with_the_scene_tools_only()
    {
        using var bench = new Workbench();
        var w = Ui.Open();
        w.AgentPipe = Pipe();
        w.Preferences.AllowAgents = true;
        w.SyncAgents();
        Assert.True(w.Agents.Running);
        var client = Ui.Wait(LiveBridge.ConnectAsync(w.AgentPipe, bench.Path, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        try
        {
            var tools = Ui.Wait(client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask()).Select(t => t.Name).ToList();
            Assert.Contains("edit_in_modeler", tools);
            Assert.DoesNotContain("run_tool", tools);
            Assert.Equal("AVALightWave", client.ServerInfo!.Name);
            Call(client, "add_object", new { file = "objects/crate.lwo", name = "Crate" });
            Call(client, "add_light", new { type = "spot", name = "Spot" });
            Assert.Contains(w.Session.Scene.Objects, o => o.Name == "Crate");
            Assert.Contains("AI agent", w.Status);
            Assert.Equal("Agent: added a light", w.Session.History.UndoLabel);
            Ui.Key(w, Key.Z, RawInputModifiers.Control);
            Assert.DoesNotContain(w.Session.Scene.Lights, l => l.Name == "Spot");
            var status = Call(client, "hub_status");
            Assert.False((bool?)status["on_hub"]);
            // A whole shot from a plan, in the open window: one step, which the person's Ctrl+Z takes back.
            int items = w.Session.Scene.Items.Count;
            var built = Call(client, "build_scene", new
            {
                plan = new
                {
                    objects = new object[] { new { shape = "ground" }, new { shape = "ball", name = "Ball", color = new[] { 220, 40, 30 }, position = new[] { 2.5, 0, 0 } } },
                    lights = new { rig = "three_point", keep_lights = true },
                    motion = new[] { new { item = "Ball", kind = "bounce", height = 1.5, start = 1, end = 40 } },
                    camera = new { items = new[] { "all" }, start = 1, end = 40, from = "three_quarter" },
                },
            });
            Assert.Equal(items + 5, w.Session.Scene.Items.Count);
            Assert.Equal(1.5, w.Session.Scene.Find("Ball")!.PositionAt(1).Y, 6);
            Assert.True((bool?)built["check"]!["ok"], built["check"]!.ToJsonString());
            Assert.Equal("Agent: built a scene", w.Session.History.UndoLabel);
            Ui.Key(w, Key.Z, RawInputModifiers.Control);
            Assert.Equal(items, w.Session.Scene.Items.Count);
        }
        finally
        {
            Ui.Wait(client.DisposeAsync().AsTask().ContinueWith(_ => 0, TaskScheduler.Default));
            await w.Agents.StopAsync();
        }
    }

    private static JsonObject Call(McpClient client, string tool, object? args = null)
    {
        var dict = args == null ? null : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var r = Ui.Wait(client.CallToolAsync(tool, dict, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.True(r.IsError != true, $"{tool} failed: {text}");
        return (JsonObject)JsonNode.Parse(text)!;
    }

    [AvaloniaFact]
    public void The_ai_chat_stages_the_scene_with_avalightwaves_tools_and_prompt()
    {
        using var bench = new Workbench();
        var api = new FakeClaude()
            .Reply([FakeClaude.Say("I'll load the pawn and light it."), FakeClaude.Call("t1", "add_object", new { file = "objects/pawn.lwo", name = "Pawn" }), FakeClaude.Call("t2", "add_light", new { type = "spot", name = "Key", position = new[] { -2.0, 3, -2 } })])
            .Reply([FakeClaude.Say("Done: the pawn is lit by a spot from the upper left.")]);
        var w = Ui.Open();
        w.ChatRoot = bench.Path;
        w.Chat.ClientFactory = _ => api.Client();
        w.Chat.Keys.Override = FakeClaude.Key;
        w.ToggleChat();
        Assert.True(w.ChatOpen);
        Assert.Contains("put the pawn on the floor with a spotlight", w.Chat.IntroText);
        Assert.Contains("stage, light or animate", w.Chat.Input.PlaceholderText);
        w.Chat.Input.Text = "put the pawn in with a spotlight";
        w.Chat.Send();
        Ui.Wait(w.Chat.Running.ContinueWith(_ => 0, TaskScheduler.Default));
        Assert.Contains(w.Session.Scene.Objects, o => o.Name == "Pawn");
        Assert.Contains(w.Session.Scene.Lights, l => l.Name == "Key" && l.Type == LightType.Spot);
        var request = api.Requests[0];
        Assert.StartsWith("AVALightWave is a standalone scene", (string?)request["system"]![0]!["text"]);
        var offered = request["tools"]!.AsArray().Select(t => (string?)t!["name"]).ToList();
        Assert.Contains("replace_object", offered);
        Assert.DoesNotContain("run_tool", offered);
        Assert.Contains("queue_render", offered);
        int own = typeof(AVALightWave.Mcp.LightWaveTools).GetMethods().Count(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Length > 0);
        Assert.Equal(ToolGroups.Scene.Count + own, offered.Count);
        Assert.Equal("Agent: added a light", w.Session.History.UndoLabel);
        // The chat keeps no history in a test window, and has no key of its own.
        Assert.Null(w.Chat.History);
    }
}
