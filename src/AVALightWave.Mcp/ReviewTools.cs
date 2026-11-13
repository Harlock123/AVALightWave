using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Self-review (milestone A, stage 5): the shot's check and a labelled contact sheet in one call.</summary>
public sealed partial class LightWaveTools
{
    public const int MaxReviewFrames = 16;
    public static readonly TimeSpan ReviewTimeLimit = TimeSpan.FromMinutes(3);

    [McpServerTool(Name = "review_shot", ReadOnly = true, Idempotent = true)]
    [Description("Looks at the shot as a whole before you report it: check_shot's findings over every frame (what's out of view, cut off, too small, below the ground, overlapping, unlit…) together with a contact sheet of frames ray traced small and evenly spread from first to last, each with its frame number in the corner, through the camera. Read both: the picture shows composition, lighting and motion at a glance; the findings name the frames to fix. Nothing is saved and the scene isn't changed. Quick by design: few samples and small frames.")]
    public async Task<CallToolResult> ReviewShot(
        [Description("How many frames on the sheet, 2 to 16 (default 8).")] int frames = 8,
        [Description("Each frame's width in pixels, 48 to 320 (default 160).")] int width = 160,
        [Description("The first frame (default: the scene's first).")] double? first = null,
        [Description("The last frame (default: the scene's last).")] double? last = null,
        [Description("The camera (default: the current one).")] string? camera = null,
        [Description("Rays a pixel, 1 to 4 (default 1).")] int samples = 1,
        CancellationToken cancel = default)
    {
        if (frames < 2 || frames > MaxReviewFrames) throw new McpException($"frames must be from 2 to {MaxReviewFrames}.");
        if (width < 48 || width > 320) throw new McpException("width must be from 48 to 320.");
        if (samples < 1 || samples > 4) throw new McpException("samples must be from 1 to 4.");
        (Scene copy, CameraItem cam, List<double> at, JsonObject check, int height) job;
        try
        {
            job = await host.RunSceneAsync("reviewed the shot", (_, l) =>
            {
                var s = l.Scene;
                var c = (camera != null ? ItemByName(s, camera) : s.CurrentCamera) as CameraItem
                    ?? throw new ArgumentException(camera != null ? $"{camera} isn't a camera." : "The scene has no camera.");
                double a = first ?? s.FirstFrame, b = last ?? s.LastFrame;
                if (!(b >= a)) throw new ArgumentException("last must be at or after first.");
                var check = Shot.Check(l, c, Shot.Frames(a, b, Math.Max(1, (b - a) / 24)), 0.04);
                var spread = Enumerable.Range(0, frames).Select(i => Math.Round(a + (b - a) * i / (frames - 1))).Distinct().ToList();
                int h = Math.Max(16, (int)Math.Round(width / c.FrameAspect));
                var copy = ScenePictures.Copy(s);
                return (copy, (CameraItem)copy.Items[s.Items.ToList().IndexOf(c)], spread, check, h);
            }, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(ReviewTimeLimit);
        List<RenderImage> pictures;
        try
        {
            pictures = await Task.Run(() => job.at.Select(f => ScenePictures.Render(job.copy, f, job.cam, width, job.height, samples, true, true, true, limit.Token)).ToList(), limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new McpException($"The review took longer than {ReviewTimeLimit.TotalMinutes:0} minutes and was stopped: try fewer or smaller frames.");
        }
        var sheet = LabelledSheet(pictures, job.at);
        var about = new JsonObject
        {
            ["frames"] = new JsonArray([.. job.at.Select(f => (JsonNode)f)]),
            ["sheet"] = "The picture: these frames left to right, then down, each numbered in its top left corner.",
            ["seconds"] = Math.Round(clock.Elapsed.TotalSeconds, 1),
            ["check"] = job.check,
        };
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(sheet, "image/png"), new TextContentBlock { Text = about.ToJsonString() }] };
    }

    // A 3×5 pixel font for frame numbers.
    private static readonly string[] Digits =
    [
        "111101101101111", "010110010010111", "111001111100111", "111001111001111", "101101111001001",
        "111100111001111", "111100111101111", "111001010010010", "111101111101111", "111101111001111",
    ];

    /// <summary>Frames in a grid (two to four a row, as squarely as they go), each with its frame number drawn in its top left corner.</summary>
    internal static byte[] LabelledSheet(IReadOnlyList<RenderImage> frames, IReadOnlyList<double> numbers)
    {
        int n0 = frames.Count;
        int columns = n0 <= 3 ? n0 : n0 == 4 ? 2 : n0 <= 6 ? 3 : 4, rows = (n0 + columns - 1) / columns;
        int cw = frames[0].Width, ch = frames[0].Height;
        const int gap = 3;
        int width = columns * cw + (columns + 1) * gap, height = rows * ch + (rows + 1) * gap;
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4) (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]) = ((byte)30, (byte)30, (byte)34, (byte)255);
        void Set(int x, int y, byte r, byte g, byte b)
        {
            if (x < 0 || y < 0 || x >= width || y >= height) return;
            int o = (y * width + x) * 4;
            (rgba[o], rgba[o + 1], rgba[o + 2]) = (r, g, b);
        }
        for (int n = 0; n < frames.Count; n++)
        {
            var img = frames[n];
            int ox = gap + n % columns * (cw + gap), oy = gap + n / columns * (ch + gap);
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                {
                    var (r, g, b, _) = img.Pixel(Math.Min(x, img.Width - 1), Math.Min(y, img.Height - 1));
                    Set(ox + x, oy + y, r, g, b);
                }
            // The number, white on a dark box, two pixels a font pixel.
            string text = ((int)Math.Round(numbers[n])).ToString(System.Globalization.CultureInfo.InvariantCulture);
            int boxW = text.Length * 8 + 2, boxH = 14;
            for (int y = 0; y < boxH; y++)
                for (int x = 0; x < boxW; x++) Set(ox + x, oy + y, 0, 0, 0);
            for (int k = 0; k < text.Length; k++)
            {
                var glyph = Digits[text[k] - '0'];
                for (int gy = 0; gy < 5; gy++)
                    for (int gx = 0; gx < 3; gx++)
                        if (glyph[gy * 3 + gx] == '1')
                            for (int dy = 0; dy < 2; dy++)
                                for (int dx = 0; dx < 2; dx++) Set(ox + 2 + k * 8 + gx * 2 + dx, oy + 2 + gy * 2 + dy, 255, 255, 255);
            }
        }
        return Png.Encode(width, height, rgba);
    }
}
