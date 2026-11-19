using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AVALightWave.UI.Tests;

/// <summary>
/// The window's network door: Options opens it (on localhost and a port the system picks here), an MCP client with
/// the token works on the open scene with AVALightWave's tools (Ctrl+Z undoes it), a new token shuts out the old one,
/// and turning it off closes it. Its default port is AVALightWave's own, so AVA Modeler's door can be open beside it.
/// </summary>
public class NetworkAgentTests
{
    private static McpClient Connect(int port, string token) =>
        Ui.Wait(McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }), new McpClientOptions { ProtocolVersion = "2025-11-25" }, null, TestContext.Current.CancellationToken));

    private static JsonObject Call(McpClient client, string tool, object? args = null)
    {
        var dict = args == null ? null : JsonSerializer.SerializeToElement(args).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var r = Ui.Wait(client.CallToolAsync(tool, dict, cancellationToken: TestContext.Current.CancellationToken).AsTask());
        var text = string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));
        Assert.True(r.IsError != true, $"{tool} failed: {text}");
        return (JsonObject)JsonNode.Parse(text)!;
    }

    [AvaloniaFact]
    public void An_agent_on_the_network_works_on_the_open_scene_with_the_token()
    {
        var folder = Directory.CreateTempSubdirectory("avalightwave-net-").FullName;
        var w = Ui.Open();
        try
        {
            Assert.Equal(8766, w.Agents.Profile.NetworkPort);
            var p = w.Preferences;
            p.AllowNetworkAgents = true;
            p.NetworkAgentsAddress = "localhost";
            p.NetworkAgentsPort = 0;
            p.NetworkAgentsFolder = folder;
            Ui.Wait(w.SyncNetworkAsync());
            Assert.True(w.Agents.NetworkRunning, w.Status);
            Assert.Contains("AI agents on other computers may connect", w.Status);

            var client = Connect(w.Agents.NetworkPort, w.Network.Token);
            Assert.Equal("AVALightWave", client.ServerInfo!.Name);
            var tools = Ui.Wait(client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken).AsTask()).Select(t => t.Name).ToList();
            Assert.Contains("edit_in_modeler", tools);
            Assert.DoesNotContain("run_tool", tools);
            Call(client, "add_light", new { type = "spot", name = "Spot" });
            Assert.Contains(w.Session.Scene.Lights, l => l.Name == "Spot");
            Ui.Key(w, Key.Z, RawInputModifiers.Control);
            Assert.DoesNotContain(w.Session.Scene.Lights, l => l.Name == "Spot");
            Ui.Wait(client.DisposeAsync().AsTask());

            string old = w.Network.Token;
            w.Network.RenewToken();
            Ui.Wait(w.SyncNetworkAsync());
            Assert.ThrowsAny<Exception>(() => Connect(w.Agents.NetworkPort, old));
            var again = Connect(w.Agents.NetworkPort, w.Network.Token);
            Call(again, "get_scene");
            Ui.Wait(again.DisposeAsync().AsTask());

            p.AllowNetworkAgents = false;
            Ui.Wait(w.SyncNetworkAsync());
            Assert.False(w.Agents.NetworkRunning);
        }
        finally
        {
            w.Close();
            Directory.Delete(folder, true);
        }
    }

    [AvaloniaFact]
    public void Options_show_the_network_settings_once_ticked()
    {
        var w = Ui.Open();
        w.ShowPanel("Options");
        Ui.Pump();
        var network = Ui.Find<CheckBox>(w.CurrentPanel!, "AllowNetworkAgents");
        Assert.False(network.IsChecked);
        Assert.Throws<InvalidOperationException>(() => Ui.Find<TextBox>(w.CurrentPanel!, "NetworkPort"));

        w.Preferences.NetworkAgentsAddress = "localhost";
        w.Preferences.NetworkAgentsFolder = Path.GetTempPath();
        network.IsChecked = true;
        Ui.Wait(w.SyncNetworkAsync());
        for (int i = 0; i < 50; i++) Ui.Pump();
        Assert.Equal("8766", Ui.Find<TextBox>(w.CurrentPanel!, "NetworkPort").Text);
        var token = Ui.Find<TextBlock>(w.CurrentPanel!, "NetworkToken").Text!;
        Assert.DoesNotContain(w.Network.Token, token);
        Assert.StartsWith(w.Network.Token[..4], token);
        network = Ui.Find<CheckBox>(w.CurrentPanel!, "AllowNetworkAgents");
        network.IsChecked = false;
        Ui.Wait(w.SyncNetworkAsync());
        Assert.False(w.Agents.NetworkRunning);
        w.Close();
    }
}
