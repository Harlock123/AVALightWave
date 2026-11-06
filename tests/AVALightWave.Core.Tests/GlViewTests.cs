using AVAModeler.Layout;
using AVAModeler.Rendering;

namespace AVALightWave.Core.Tests;

/// <summary>
/// The Layout view's OpenGL path, on a real context where there is one (the engine's OffscreenGl), against its software
/// picture: the showcase scene through its camera and from the top, shaded with a half-dissolved object. Where
/// there's no OpenGL, they skip.
/// </summary>
[Collection("OpenGL")]
public class GlViewTests
{
    private const int W = 240, H = 180;

    private static int Distance(uint a, uint b) => Math.Max(Math.Abs((int)(a >> 16 & 0xFF) - (int)(b >> 16 & 0xFF)),
        Math.Max(Math.Abs((int)(a >> 8 & 0xFF) - (int)(b >> 8 & 0xFF)), Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF))));

    [Theory]
    [InlineData(true, 0.0)]
    [InlineData(false, 0.0)]
    [InlineData(true, 0.5)]
    public void The_showcase_draws_with_opengl_as_in_software(bool throughCamera, double dissolve)
    {
        using var gl = OffscreenGl.TryCreate(W, H, out string why);
        if (gl == null) Assert.Skip($"No OpenGL context here: {why}");
        var d = new SceneDocument();
        d.Load(Path.Combine(Samples.Folder, "showcase.avascene"));
        var ball = d.Scene.Objects.First(o => o.Name == "Ball");
        if (dissolve > 0) d.Session.SetDissolve(ball, dissolve);
        var frame = d.Scene.Evaluate(14);
        var scene = d.Session.BuildScene(frame, shaded: true);
        var cam = new ViewCamera(throughCamera ? ViewKind.Perspective : ViewKind.Top) { PixelsPerUnit = 30 };
        cam.Resize(W, H);
        if (throughCamera) SceneDrawing.LookThrough(cam, d.Scene.CurrentCamera!, 14);
        else cam.Fit(SceneDrawing.Bounds(frame));
        var soft = new SoftwareRenderer();
        soft.Render(scene, cam);
        using var r = new GlRenderer(gl.Gl, gl.IsEs);
        r.Render(scene, cam);
        var px = gl.Read();
        int close = 0;
        for (int i = 0; i < px.Length; i++) if (Distance(px[i], soft.Pixels[i]) <= 24) close++;
        Assert.True(close >= px.Length * 0.9, $"Only {close} of {px.Length} pixels agree.");
    }
}
