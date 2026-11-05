using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(AVALightWave.UI.Tests.TestAppBuilder))]

namespace AVALightWave.UI.Tests;

public class TestAppBuilder
{
    // Skia drawing (not headless drawing) so frames can be captured as screenshots.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
