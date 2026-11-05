using System.ComponentModel;
using System.Globalization;
using AVAModeler.Mcp;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Recipe = AVAModeler.Mcp.Recipes.Recipe;

namespace AVALightWave.Mcp;

/// <summary>
/// AVALightWave's saved recipes (MCP prompts) for scene work, written with the engine's recipe builder: each step's
/// tool call on a line of its own after "→ ", with its arguments as JSON, using only the tools AVALightWave serves.
/// They start from an object file in the working folder (AVALightWave doesn't model). The tests run every one through
/// the server, call by call.
/// </summary>
[McpServerPromptType]
public static class LightWaveRecipes
{
    [McpServerPrompt(Name = "stage_object", Title = "Stage an object")]
    [Description("Stages an object file for a still: loads it, lights it with a key, a fill and a rim, frames the camera on it, and renders a frame.")]
    public static string StageObject(
        [Description("The object file in the working folder (default objects/pawn.lwo).")] string? file = null,
        [Description("How far the camera and lights stand from it, metres (default 4; about 2.5 times the object's size).")] string? distance = null,
        [Description("Width of the render in pixels (default 320).")] string? width = null)
    {
        string f = Text(file, "objects/pawn.lwo");
        double d = Recipes.Number(distance, 4, "distance", 0.1, 1000);
        int w = Recipes.Whole(width, 320, "width", 16, 1920);
        var recipe = new Recipe($"Stage {f} for a still, lit from three sides with the camera {F(d)} m away. get_scene gives the object's world bounds: the distance should be about 2.5 times its size.");
        recipe.Step("Start a new scene (if it refuses, save the scene with save_scene, or give discard_changes true).", "new_scene", new { });
        recipe.Step("Load the object, standing on the origin.", "add_object", new { file = f, name = "Subject" });
        recipe.Step("Read its bounds (world_bounds) to check the distance suits its size.", "get_scene", new { });
        Recipes.LightRig(recipe, "Subject", d);
        recipe.Step("Place the camera in front of it and a little above.", "set_transform", new { item = "Camera", position = V(0, 0.35 * d, -d), key = false });
        recipe.Step("Aim the camera at it.", "look_at", new { item = "Camera", target = "Subject", key = false });
        recipe.Step("Check the framing through the camera.", "render_scene_view", new { view = "camera", width = 320 });
        recipe.Step("Ray-trace it.", "render_scene", new { width = w, samples = 2 });
        return recipe.Text("Save the scene with save_scene (e.g. \"still.avascene\") to keep it.");
    }

    [McpServerPrompt(Name = "object_turntable", Title = "Turntable of an object file")]
    [Description("Puts an object file on a turntable (a null spinning once round over the frames), lights it, and renders the turn into frames and a movie.")]
    public static string ObjectTurntable(
        [Description("The object file in the working folder (default objects/pawn.lwo).")] string? file = null,
        [Description("Frames for one full turn, 2 to 120 (default 48).")] string? frames = null,
        [Description("The camera's distance from the object, metres (default 4).")] string? distance = null,
        [Description("Width of the frames in pixels (default 160).")] string? width = null,
        [Description("The movie to make: mp4, gif, both or none (default none; mp4 and gif need ffmpeg).")] string? movie = null)
    {
        string f = Text(file, "objects/pawn.lwo");
        int n = Recipes.Whole(frames, 48, "frames", 2, ModelerTools.MaxAnimationFrames);
        double d = Recipes.Number(distance, 4, "distance", 0.1, 1000);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        string m = Choice(movie, "none", "movie", "mp4", "gif", "both", "none");
        var recipe = new Recipe($"Spin {f} once round in {n} frames on a turntable, lit and rendered.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step($"Frames 1 to {n} at 30 a second, and a dark gradient backdrop.", "set_scene", new { first_frame = 1, last_frame = n, fps = 30, gradient = true, gradient_top = new[] { 40, 48, 64 }, gradient_bottom = new[] { 8, 8, 12 }, frame = 1 });
        recipe.Step("The turntable: a null at the origin.", "add_null", new { name = "Turntable" });
        recipe.Step("The object on it.", "add_object", new { file = f, name = "Subject", parent = "Turntable" });
        recipe.Step("Key the turntable's heading at 0 on the first frame, linear so it turns steadily.", "set_key", new { item = "Turntable", channel = "heading", frame = 1, value = 0, linear = true });
        recipe.Step($"And a frame's turn short of 360° at the last, so a looping movie doesn't show one view twice.", "set_key", new { item = "Turntable", channel = "heading", frame = n, value = R(360.0 * (n - 1) / n), linear = true });
        recipe.Step("The camera in front and a little above.", "set_transform", new { item = "Camera", frame = 1, position = V(0, 0.4 * d, -d), key = false });
        recipe.Step("Aim it at the object.", "look_at", new { item = "Camera", target = "Subject", frame = 1, key = false });
        Recipes.LightRig(recipe, "Subject", d);
        recipe.Step("Check a quarter of the way round.", "render_scene", new { frame = 1 + n / 4, width = 160, samples = 1 });
        object animation = m == "none"
            ? new { folder = "turntable", name = "turntable", first = 1, last = n, width = w, samples = 1 }
            : new { folder = "turntable", name = "turntable", first = 1, last = n, width = w, samples = 1, movie = m };
        recipe.Step("Render every frame into the turntable folder" + (m == "none" ? "." : $", and join them into a movie ({m})."), "render_animation", animation);
        return recipe.Text("Save the scene with save_scene (e.g. \"turntable.avascene\").");
    }

    [McpServerPrompt(Name = "camera_flyby", Title = "Camera fly-by on a path")]
    [Description("Flies the camera past an object along a smooth path through four nulls, keyed to keep looking at it, and renders a frame from the middle of the flight.")]
    public static string CameraFlyby(
        [Description("The object file in the working folder (default objects/crate.lwo).")] string? file = null,
        [Description("Frames for the flight, 10 to 240 (default 90).")] string? frames = null,
        [Description("How far out the path passes, metres (default 5).")] string? radius = null)
    {
        string f = Text(file, "objects/crate.lwo");
        int n = Recipes.Whole(frames, 90, "frames", 10, 240);
        double r = Recipes.Number(radius, 5, "radius", 0.5, 1000);
        var recipe = new Recipe($"Fly the camera past {f} in {n} frames along a curve {F(r)} m out, the way a classic Layout fly-by is made: nulls for the path, the camera following it, its heading and pitch keyed along the way to keep the object in view.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step($"Frames 1 to {n}, and a dusk gradient backdrop.", "set_scene", new { first_frame = 1, last_frame = n, fps = 30, gradient = true, gradient_top = new[] { 60, 80, 130 }, gradient_bottom = new[] { 20, 16, 24 } });
        recipe.Step("The object at the origin.", "add_object", new { file = f, name = "Subject" });
        recipe.Step("The path's first point: far off to the left and behind.", "add_null", new { name = "Path 1", position = V(-2 * r, 0.3 * r, -1.5 * r) });
        recipe.Step("Second: in front of the object, coming in.", "add_null", new { name = "Path 2", position = V(-0.6 * r, 0.15 * r, -r) });
        recipe.Step("Third: sweeping round its right side.", "add_null", new { name = "Path 3", position = V(r, 0.15 * r, -0.4 * r) });
        recipe.Step("Fourth: away behind it.", "add_null", new { name = "Path 4", position = V(1.2 * r, 0.25 * r, 1.5 * r) });
        recipe.Step($"The camera follows the path from frame 1 to {n} at an even speed.", "set_follow_path",
            new { item = "Camera", points = new[] { "Path 1", "Path 2", "Path 3", "Path 4" }, closed = false, align_to_path = false, from_frame = 1, to_frame = n });
        foreach (int k in new[] { 1, 1 + (n - 1) / 4, 1 + (n - 1) / 2, 1 + 3 * (n - 1) / 4, n }.Distinct())
            recipe.Step($"Key the camera looking at the object at frame {k} (look_at uses where the path has it then).", "look_at", new { item = "Camera", target = "Subject", frame = k, key = true });
        Recipes.LightRig(recipe, "Subject", r);
        recipe.Step("See the path from above: the camera's route round the object.", "render_scene_view", new { view = "top", width = 320 });
        recipe.Step("A frame from the middle of the flight, through the camera.", "render_scene", new { frame = 1 + (n - 1) / 2, width = 160, samples = 1 });
        return recipe.Text("Move a null with set_transform to reshape the flight (then look_at again at the keyed frames); bake_path turns the flight into ordinary keys for programs without paths. render_animation renders it.");
    }

    [McpServerPrompt(Name = "light_sweep", Title = "Sweeping spotlight")]
    [Description("Sweeps a spotlight across an object in a dark scene, keyed to fade in, turn across and fade out, and renders three moments.")]
    public static string LightSweep(
        [Description("The object file in the working folder (default objects/pawn.lwo).")] string? file = null,
        [Description("Frames for the sweep, 10 to 240 (default 60).")] string? frames = null,
        [Description("The spot's colour as r, g, b from 0 to 255, or a name (default warm white [255, 236, 200]).")] string? color = null)
    {
        string f = Text(file, "objects/pawn.lwo");
        int n = Recipes.Whole(frames, 60, "frames", 10, 240);
        var c = Recipes.Colour(color, [255, 236, 200]);
        var recipe = new Recipe($"Sweep a spotlight across {f} over {n} frames: it fades in, turns from left to right, and fades out.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step($"Frames 1 to {n}, nearly no ambient light, a black backdrop.", "set_scene", new { first_frame = 1, last_frame = n, ambient_intensity = 0.03, background_color = new[] { 0, 0, 0 } });
        recipe.Step("The object at the origin.", "add_object", new { file = f, name = "Subject" });
        recipe.Step("Remove the default light: the spot does the lighting.", "remove_item", new { item = "Light" });
        recipe.Step("The spot, high above and in front.", "add_light", new { type = "spot", name = "Sweep", position = V(0, 4, -3), color = c, intensity = 1.4, cone_angle = 14, edge_angle = 6 });
        recipe.Step("Aim it at the object, then key its pitch so it holds there.", "look_at", new { item = "Sweep", target = "Subject", frame = 1, key = false });
        recipe.Step("Its heading at the start: off to the left.", "set_key", new { item = "Sweep", channel = "heading", frame = 1, value = -35 });
        recipe.Step("And at the end: off to the right.", "set_key", new { item = "Sweep", channel = "heading", frame = n, value = 35 });
        recipe.Step("Intensity: dark at the start…", "set_key", new { item = "Sweep", channel = "intensity", frame = 1, value = 0 });
        recipe.Step("…full a quarter of the way in…", "set_key", new { item = "Sweep", channel = "intensity", frame = 1 + n / 4, value = 1.4 });
        recipe.Step("…still full three quarters of the way…", "set_key", new { item = "Sweep", channel = "intensity", frame = 1 + 3 * n / 4, value = 1.4 });
        recipe.Step("…and dark again at the end.", "set_key", new { item = "Sweep", channel = "intensity", frame = n, value = 0 });
        recipe.Step("The camera in front, aimed at the object.", "set_transform", new { item = "Camera", position = V(0, 1.2, -4.5), key = false });
        recipe.Step("Aim it.", "look_at", new { item = "Camera", target = "Subject", key = false });
        recipe.Step("Check the framing through the camera at the middle frame.", "render_scene_view", new { view = "camera", frame = 1 + n / 2, width = 240 });
        recipe.Step("Ray-trace the middle of the sweep.", "render_scene", new { frame = 1 + n / 2, width = 160, samples = 1 });
        return recipe.Text("Raise edge_angle for a softer pool of light, or give the spot a radius for soft shadows; render_animation renders the sweep.");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private static double R(double v) => Math.Round(v, 4) + 0.0;

    private static double[] V(double x, double y, double z) => [R(x), R(y), R(z)];

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Text(string? given, string fallback) => string.IsNullOrWhiteSpace(given) ? fallback : given.Trim();

    private static string Choice(string? given, string fallback, string what, params string[] choices)
    {
        if (string.IsNullOrWhiteSpace(given)) return fallback;
        string v = given.Trim().ToLowerInvariant();
        return choices.Contains(v) ? v : throw new McpException($"{what} must be {string.Join(", ", choices[..^1])} or {choices[^1]}, not \"{given}\".");
    }
}
