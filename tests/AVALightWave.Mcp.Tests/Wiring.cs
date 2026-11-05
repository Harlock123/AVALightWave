using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp.Tests;

/// <summary>An MCP client talking to AVALightWave's server in the same process, over in-memory pipes.</summary>
internal sealed class InProcess : IAsyncDisposable
{
    private readonly Pipe _up = new(), _down = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _server = Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;

    public static async Task<InProcess> Start(McpServerOptions options)
    {
        var s = new InProcess();
        var server = McpServer.Create(new StreamServerTransport(s._up.Reader.AsStream(), s._down.Writer.AsStream(), "test"), options);
        s._server = Task.Run(async () => { await using (server) await server.RunAsync(s._stop.Token); });
        s.Client = await McpClient.CreateAsync(new StreamClientTransport(s._up.Writer.AsStream(), s._down.Reader.AsStream()), null, null, TestContext.Current.CancellationToken);
        return s;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException) { }
    }
}

internal static class Calls
{
    public static CancellationToken Cancel => TestContext.Current.CancellationToken;

    public static async Task<CallToolResult> Call(this McpClient client, string tool, object? args = null)
    {
        var dict = args == null ? null : JsonSerializer.SerializeToElement(args, JsonSerializerOptions.Default).EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        return await client.CallToolAsync(tool, dict, cancellationToken: Cancel);
    }

    public static async Task<JsonObject> Json(this McpClient client, string tool, object? args = null)
    {
        var r = await client.Call(tool, args);
        Assert.True(r.IsError != true, $"{tool} failed: {Text(r)}");
        return (JsonObject)JsonNode.Parse(Text(r))!;
    }

    public static async Task<string> Fails(this McpClient client, string tool, object? args = null)
    {
        var r = await client.Call(tool, args);
        Assert.True(r.IsError == true, $"{tool} should have failed but said: {Text(r)}");
        return Text(r);
    }

    public static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    /// <summary>How many colours a PNG has (a blank picture has one or two).</summary>
    public static int Colours(byte[] png)
    {
        var image = TextureImages.Decode(png) ?? throw new InvalidOperationException("The PNG didn't decode.");
        var colours = new HashSet<int>();
        for (int i = 0; i < image.Rgba.Length; i += 4) colours.Add(image.Rgba[i] << 16 | image.Rgba[i + 1] << 8 | image.Rgba[i + 2]);
        return colours.Count;
    }
}

/// <summary>A headless AVALightWave server on a working folder with the sample objects in it.</summary>
internal sealed class Bench : IDisposable
{
    public Core.Tests.Workbench Folder { get; } = new();
    public HeadlessHost Host { get; }

    public Bench() => Host = new HeadlessHost(Folder.Path);

    public AVAModeler.Scene.Scene Scene => Host.Layout.Scene;

    public Task<InProcess> Serve(ILightWaveApp? app = null) => InProcess.Start(ModelerServer.Options(Host, LightWaveServer.Profile(app)));

    public void Dispose() => Folder.Dispose();
}
