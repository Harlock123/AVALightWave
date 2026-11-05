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
    /// scene tools only (<see cref="LightWaveServer"/>).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (McpCommandLine.Wanted(args))
            return McpCommandLine.RunAsync(args, _ => ToolRegistry.Create(), Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error, LightWaveServer.Profile())
                .GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
