using Avalonia;
using AVALightWave.Mcp;
using AVAModeler.Mcp;
using AVAModeler.Tools;

namespace AVALightWave;

internal static class Program
{
    /// <summary>
    /// Starts AVALightWave; a scene (.lws, .avascene) or an object file on the command line opens in it. With
    /// <c>--mcp</c> it runs no window: it serves the Model Context Protocol on stdio for an AI agent instead, with the
    /// scene tools only (<see cref="LightWaveServer"/>). With <c>--render-node FOLDER</c> it runs no window either: it's
    /// a headless render node working a render farm folder's jobs (<see cref="FarmCommandLine"/>) until Ctrl+C.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (FarmCommandLine.Wanted(args)) return RenderNode(args);
        if (McpCommandLine.Wanted(args))
            return McpCommandLine.RunAsync(args, _ => ToolRegistry.Create(), Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, LightWaveServer.Profile())
                .GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>A headless render node: a line per frame on standard output, problems on standard error; Ctrl+C stops it.</summary>
    private static int RenderNode(string[] args)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        return FarmCommandLine.RunAsync(args, Console.Out, Console.Error, stop.Token).GetAwaiter().GetResult();
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
