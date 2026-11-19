using AVALightWave.Mcp;
using AVAModeler.Mcp;
using ModelContextProtocol.Client;

namespace AVALightWave.Mcp.Tests;

/// <summary>
/// AVALightWave's server through the engine's network door, headless: the profile's own tools (and none of the
/// Modeler's) over HTTP with the token, its own port, token variable and usage.
/// </summary>
public class NetworkTests
{
    [Fact]
    public async Task The_network_door_serves_avalightwaves_tools_with_the_token()
    {
        using var b = new Bench();
        var profile = LightWaveServer.Profile();
        Assert.Equal(8766, profile.NetworkPort);
        Assert.Equal("AVALIGHTWAVE_MCP_TOKEN", profile.TokenVariable);
        Assert.Contains("--http", profile.Usage);
        const string token = "lightwave-test-token-0123";
        await using var listener = new NetworkListener(new NetworkSettings("localhost", 0, token), profile.Name,
            o => ModelerServer.Configure(o, b.Host, profile), ModelerServer.EndSessionAsync);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        await using var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{listener.Port}/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
        }), new McpClientOptions { ProtocolVersion = "2025-11-25" }, null, TestContext.Current.CancellationToken);
        Assert.Equal("AVALightWave", client.ServerInfo?.Name);
        var tools = (await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.Name).ToList();
        Assert.Equal(ModelerServer.Definitions(profile).Select(t => t.Name).Order(), tools.Order());
        Assert.Contains("make_object", tools);
        Assert.DoesNotContain("run_tool", tools);
        var made = await client.CallToolAsync("make_object", new Dictionary<string, object?> { ["shape"] = "ball", ["name"] = "Ball" }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(made.IsError != true);
        Assert.Contains(b.Scene.Objects, o => o.Name == "Ball");
    }
}
