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
public static partial class LightWaveRecipes
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

    [McpServerPrompt(Name = "render_on_farm", Title = "Render on a render farm")]
    [Description("Sets up a turntable of an object file and sends it to a render farm folder as a batch job (render nodes, the window's or \"AVALightWave --render-node\" processes on any machine, render it frame by frame), then checks on it.")]
    public static string RenderOnFarm(
        [Description("The object file in the working folder (default objects/pawn.lwo).")] string? file = null,
        [Description("Frames for one full turn, 2 to 600 (default 48).")] string? frames = null,
        [Description("The farm folder in the working folder (default farm).")] string? folder = null,
        [Description("Width of the frames in pixels (default 640; the height is three quarters of it).")] string? width = null,
        [Description("Rays a pixel, 1 to 16 (default 4).")] string? samples = null)
    {
        string f = Text(file, "objects/pawn.lwo");
        int n = Recipes.Whole(frames, 48, "frames", 2, 600);
        string farm = Text(folder, "farm");
        int w = Recipes.Whole(width, 640, "width", 16, 4096);
        int s = Recipes.Whole(samples, 4, "samples", 1, 16);
        const double d = 4;
        var recipe = new Recipe($"Turn {f} once round in {n} frames, and send the frames to the render farm folder {farm} as a batch job: render nodes (the window's Render Queue, or \"AVALightWave --render-node {farm}\" on any machine that reaches the folder) share the frames between them.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step($"Frames 1 to {n} at 30 a second, on a dark gradient backdrop.", "set_scene", new { first_frame = 1, last_frame = n, fps = 30, gradient = true, gradient_top = new[] { 40, 48, 64 }, gradient_bottom = new[] { 8, 8, 12 }, frame = 1 });
        recipe.Step("The object at the origin.", "add_object", new { file = f, name = "Subject" });
        recipe.Step("Key its heading at 0 on the first frame, linear so it turns steadily.", "set_key", new { item = "Subject", channel = "heading", frame = 1, value = 0, linear = true });
        recipe.Step("And a frame's turn short of 360° at the last, so the turn loops.", "set_key", new { item = "Subject", channel = "heading", frame = n, value = R(360.0 * (n - 1) / n), linear = true });
        recipe.Step("The camera in front and a little above.", "set_transform", new { item = "Camera", frame = 1, position = V(0, 0.4 * d, -d), key = false });
        recipe.Step("Aim it at the object.", "look_at", new { item = "Camera", target = "Subject", frame = 1, key = false });
        Recipes.LightRig(recipe, "Subject", d);
        recipe.Step("Check one frame small before sending the job.", "render_scene", new { frame = 1 + n / 4, width = 160, samples = 1 });
        recipe.Step($"Send every frame to the farm folder as a job: {w}×{w * 3 / 4}, {s} rays a pixel.", "queue_render",
            new { folder = farm, first = 1, last = n, width = w, height = w * 3 / 4, samples = s, name = "turntable", job_name = "Turntable" });
        recipe.Step("See the job waiting (or rendering) and which nodes are working the folder.", "render_queue_status", new { folder = farm });
        return recipe.Text($"If no node is working the folder, ask the person to start one: \"AVALightWave --render-node <working folder>/{farm}\" on each machine (or Render › Render Queue › Start Local Rendering in the window). Call render_queue_status again to follow it; the frames land in the job's out folder.");
    }

    // ───────────────────────────── helpers ─────────────────────────────

    [McpServerPrompt(Name = "clone_stagger", Title = "Clone an object into a staggered row")]
    [Description("Animates an object bouncing, clones it into a row with each copy's bounce a few frames after the one before (Clone Item's time offset), checks the motion with a quick preview, and renders a frame.")]
    public static string CloneStagger(
        [Description("The object file in the working folder (default objects/ball.lwo).")] string? file = null,
        [Description("How many copies beside the original, 1 to 20 (default 4).")] string? copies = null,
        [Description("Metres between copies along X (default 0.9).")] string? spacing = null,
        [Description("Frames each copy's bounce is behind the one before (default 3).")] string? delay = null)
    {
        string f = Text(file, "objects/ball.lwo");
        int n = Recipes.Whole(copies, 4, "copies", 1, 20);
        double gap = Recipes.Number(spacing, 0.9, "spacing", 0.01, 100);
        double lag = Recipes.Number(delay, 3, "delay", -60, 60);
        var recipe = new Recipe($"Bounce {f} and clone it {n} times in a row {F(gap)} m apart, each bounce {F(lag)} frames after the one before.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Frames 1 to 36.", "set_scene", new { first_frame = 1, last_frame = 36, fps = 30, frame = 1 });
        recipe.Step("The floor.", "add_object", new { file = "objects/floor.lwo", name = "Floor" });
        recipe.Step("The object, at the left of the row.", "add_object", new { file = f, name = "Hopper" });
        recipe.Step("Move it to the row's start.", "set_transform", new { item = "Hopper", position = V(-gap * n / 2, 0, 0), key = false });
        recipe.Step("A bounce: down at frame 1…", "set_key", new { item = "Hopper", channel = "position.y", frame = 1, value = 0, span = "linear" });
        recipe.Step("…up at frame 7, eased (a Bezier key, flat at the top)…", "set_key", new { item = "Hopper", channel = "position.y", frame = 7, value = 1.0, span = "bezier", in_slope = 0, out_slope = 0 });
        recipe.Step("…and down again at frame 13, landing hard (linear).", "set_key", new { item = "Hopper", channel = "position.y", frame = 13, value = 0, span = "linear" });
        recipe.Step("Repeat the bounce for ever.", "set_end_behavior", new { item = "Hopper", channel = "position.y", post = "repeat" });
        recipe.Step($"Clone it {n} times, each {F(gap)} m to the right and {F(lag)} frames later.", "clone_item", new { item = "Hopper", count = n, position_offset = V(gap, 0, 0), time_offset = lag });
        Recipes.LightRig(recipe, "Hopper", 5);
        recipe.Step("The camera in front, looking along the row.", "set_transform", new { item = "Camera", position = V(0, 1.6, -5.5), key = false });
        recipe.Step("Aim it at the middle of the row.", "look_at", new { item = "Camera", point = V(0, 0.5, 0), key = false });
        recipe.Step("A quick wireframe flipbook of the first second to check the stagger (not ray traced).", "make_preview", new { first = 1, last = 30, step = 3, width = 160, shaded = false, folder = "preview", name = "stagger" });
        recipe.Step("A ray-traced frame in the middle of it.", "render_scene", new { frame = 9, width = 240, samples = 1 });
        return recipe.Text("Change the stagger with shift_keys on one copy, or clone again with another time_offset.");
    }

    [McpServerPrompt(Name = "light_beams", Title = "Volumetric spotlight with a lens flare")]
    [Description("Lights an object with a spotlight whose beam shows in the air (a volumetric light), softened with a fuzzy shadow map, and gives it a lens flare, then renders.")]
    public static string LightBeams(
        [Description("The object file in the working folder (default objects/crate.lwo).")] string? file = null,
        [Description("The beam's colour, a name or r,g,b (default warm).")] string? color = null,
        [Description("Rays a pixel for the render, 1 to 8 (default 2).")] string? samples = null)
    {
        string f = Text(file, "objects/crate.lwo");
        var c = Recipes.Colour(color, [255, 225, 180]);
        int s = Recipes.Whole(samples, 2, "samples", 1, 8);
        var recipe = new Recipe($"Stand {f} under a spotlight whose beam shows in the air, with a soft shadow map and a lens flare.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("A dark, slightly hazy look: low ambient, a near-black backdrop.", "set_scene", new { ambient_intensity = 0.05, background_color = new[] { 6, 6, 10 } });
        recipe.Step("The floor.", "add_object", new { file = "objects/floor.lwo", name = "Floor" });
        recipe.Step("The object.", "add_object", new { file = f, name = "Subject" });
        recipe.Step("Take out the default distant light: the spot does the work.", "remove_item", new { item = "Light" });
        recipe.Step("A spot high up to the side, aimed at the object.", "add_light", new { type = "spot", name = "Beam", position = V(-2, 4, 1), look_at = V(0, 0.4, 0), color = c, intensity = 1.6, cone_angle = 22, edge_angle = 8 });
        recipe.Step("A soft shadow from a shadow map (much faster than soft ray-traced shadows).", "set_shadow_map", new { light = "Beam", type = "shadow_map", size = 1024, fuzziness = 3 });
        recipe.Step("Its beam in the air, shadowed by the object.", "set_volumetric_light", new { light = "Beam", on = true, intensity = 0.8, samples = 24, shadows = true });
        recipe.Step("A lens flare where the light is seen.", "set_lens_flare", new { light = "Beam", on = true, intensity = 0.8, size = 0.08, streaks = 6, rings = true });
        recipe.Step("The camera low in front, so the light is in the picture.", "set_transform", new { item = "Camera", position = V(1.2, 1.2, -4.5), key = false });
        recipe.Step("Aim it between the object and the light.", "look_at", new { item = "Camera", point = V(-0.6, 1.4, 0.3), key = false });
        recipe.Step("Ray-trace it (the beam costs time: small first).", "render_scene", new { width = 240, samples = s });
        return recipe.Text("Raise the beam's samples for a smoother beam, and the render's size once it looks right.");
    }

    [McpServerPrompt(Name = "composite_shot", Title = "Composite an object over a background picture")]
    [Description("Puts a picture behind the render (compositing's background image), lays a keyed foreground frame over it, and dissolves an object in over the frames.")]
    public static string CompositeShot(
        [Description("The background picture in the working folder (default images/sky.png).")] string? background = null,
        [Description("The foreground picture, with pure green to key out (default images/frame.png; \"none\" for none).")] string? foreground = null,
        [Description("The object file (default objects/pawn.lwo).")] string? file = null)
    {
        string bg = Text(background, "images/sky.png");
        string fg = Text(foreground, "images/frame.png");
        string f = Text(file, "objects/pawn.lwo");
        var recipe = new Recipe($"Composite {f} over {bg}{(fg == "none" ? "" : $", seen through {fg}")}, dissolving it in.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Frames 1 to 24.", "set_scene", new { first_frame = 1, last_frame = 24, frame = 1 });
        recipe.Step("The background picture into the scene's images.", "load_image", new { path = bg });
        if (fg != "none") recipe.Step("The foreground picture too.", "load_image", new { path = fg });
        if (fg != "none")
            recipe.Step("The background behind everything; the foreground over the render, its green keyed out.", "set_compositing",
                new { background = bg, foreground = fg, key = "color", key_low = new[] { 0, 200, 0 }, key_high = new[] { 90, 255, 90 } });
        else recipe.Step("The background behind everything.", "set_compositing", new { background = bg });
        recipe.Step("The object.", "add_object", new { file = f, name = "Subject" });
        recipe.Step("Gone at frame 1…", "set_dissolve", new { item = "Subject", value = 1, frame = 1, key = true });
        recipe.Step("…fully there by frame 18.", "set_dissolve", new { item = "Subject", value = 0, frame = 18, key = true });
        Recipes.LightRig(recipe, "Subject", 4);
        recipe.Step("The camera in front.", "set_transform", new { item = "Camera", position = V(0, 1.2, -4), key = false });
        recipe.Step("Aim it at the object.", "look_at", new { item = "Camera", target = "Subject", key = false });
        recipe.Step("A frame half way through the dissolve.", "render_scene", new { frame = 10, width = 240, samples = 1 });
        return recipe.Text("load_image with sequence true plays a numbered sequence (images/stripes0001.png …) in its place.");
    }

    [McpServerPrompt(Name = "rig_from_skelegons", Title = "Bones from skelegons, posed with spline keys")]
    [Description("Loads an object with skelegons, turns them into bones in one step, and animates the bones with keys of different spans (eased Bezier, stepped, TCB).")]
    public static string RigFromSkelegons(
        [Description("The object file with skelegons in the working folder (default objects/arm.lwo, whose bones are Upper and Forearm).")] string? file = null,
        [Description("The first bone's name (default Upper).")] string? first = null,
        [Description("The second bone's name (default Forearm).")] string? second = null)
    {
        string f = Text(file, "objects/arm.lwo");
        string a = Text(first, "Upper"), b = Text(second, "Forearm");
        var recipe = new Recipe($"Rig {f} from its skelegons and wave it: {a} with eased Bezier keys, {b} with stepped and TCB ones.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Frames 1 to 30.", "set_scene", new { first_frame = 1, last_frame = 30, frame = 1 });
        recipe.Step("The object.", "add_object", new { file = f, name = "Arm" });
        recipe.Step("Its skelegons become bones, in one step.", "skelegons_to_bones", new { @object = "Arm" });
        recipe.Step($"{a} at rest at frame 1…", "set_key", new { item = a, channel = "bank", frame = 1, value = 0 });
        recipe.Step($"…tipped 35° at frame 15, eased in and out (a Bezier key, flat there)…", "set_key", new { item = a, channel = "bank", frame = 15, value = 35, span = "bezier", in_slope = 0, out_slope = 0 });
        recipe.Step("…and back at frame 30.", "set_key", new { item = a, channel = "bank", frame = 30, value = 0, span = "bezier", in_slope = 0, out_slope = 0 });
        recipe.Step($"{b} still at frame 1…", "set_key", new { item = b, channel = "bank", frame = 1, value = 0 });
        recipe.Step("…snapping to 50° at frame 10 (a stepped key: a hold, then a jump)…", "set_key", new { item = b, channel = "bank", frame = 10, value = 50, span = "stepped" });
        recipe.Step("…and easing back by frame 25 with a TCB key, a little tension.", "set_key", new { item = b, channel = "bank", frame = 25, value = 0, tension = 0.4 });
        recipe.Step("The keys as they are now: spans, slopes and TCB.", "get_keys", new { item = b, channel = "bank" });
        recipe.Step("The camera in front of the arm.", "set_transform", new { item = "Camera", position = V(0, 1.2, -4.5), key = false });
        recipe.Step("Aim it at the arm's middle.", "look_at", new { item = "Camera", point = V(0, 1, 0), key = false });
        recipe.Step("See it bent at frame 15.", "render_scene_view", new { view = "camera", frame = 15, width = 320 });
        return recipe.Text("Bones bend the object by distance; set_item on a bone changes its strength, and set_ik gives a chain a goal.");
    }

    [McpServerPrompt(Name = "follow_and_jitter", Title = "Motion modifiers and motion files")]
    [Description("Animates a leader, makes another object lag behind it and a third jitter, a camera sway with an oscillation, then saves the leader's motion as a LightWave motion file and loads it onto another item.")]
    public static string FollowAndJitter(
        [Description("Frames the follower lags behind, 0 to 30 (default 8).")] string? lag = null,
        [Description("How far the jitter shakes, metres (default 0.05).")] string? shake = null)
    {
        double d = Recipes.Number(lag, 8, "lag", 0, 30);
        double j = Recipes.Number(shake, 0.05, "shake", 0, 1);
        var recipe = new Recipe($"A ball leading, a crate following {F(d)} frames behind, a pawn jittering by {F(j)} m, and the leader's motion in a .mot file.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Frames 1 to 40.", "set_scene", new { first_frame = 1, last_frame = 40, frame = 1 });
        recipe.Step("The floor.", "add_object", new { file = "objects/floor.lwo", name = "Floor" });
        recipe.Step("The leader.", "add_object", new { file = "objects/ball.lwo", name = "Leader" });
        recipe.Step("It goes from the left…", "set_key", new { item = "Leader", channel = "position.x", frame = 1, value = -3 });
        recipe.Step("…to the right.", "set_key", new { item = "Leader", channel = "position.x", frame = 40, value = 3 });
        recipe.Step("The follower.", "add_object", new { file = "objects/crate.lwo", name = "Follower" });
        recipe.Step("A little behind the leader's path (its own position is an offset).", "set_transform", new { item = "Follower", position = V(0, 0, 1.5), key = false });
        recipe.Step($"It lags {F(d)} frames behind the leader.", "add_motion_modifier", new { item = "Follower", kind = "lag", leader = "Leader", delay = d });
        recipe.Step("A pawn that can't keep still.", "add_object", new { file = "objects/pawn.lwo", name = "Nervous" });
        recipe.Step("Back right.", "set_transform", new { item = "Nervous", position = V(1.5, 0, 2.5), key = false });
        recipe.Step($"Smooth jitter of {F(j)} m, twice a second.", "add_motion_modifier", new { item = "Nervous", kind = "jitter", position_amount = V(j, 0, j), frequency = 2, seed = 7 });
        recipe.Step("The camera sways gently from side to side.", "add_motion_modifier", new { item = "Camera", kind = "oscillate", channel = "heading", amplitude = 3, period = 40 });
        recipe.Step("Save the leader's motion as a LightWave motion file.", "save_motion", new { item = "Leader", file = "motions/leader.mot", overwrite = true });
        recipe.Step("An extra null to carry the same motion.", "add_null", new { name = "Copy" });
        recipe.Step("Load the motion onto it.", "load_motion", new { item = "Copy", file = "motions/leader.mot" });
        Recipes.LightRig(recipe, "Leader", 6);
        recipe.Step("Look from the top at frame 30: the follower behind, the pawn shaken.", "render_scene_view", new { view = "top", frame = 30, width = 320 });
        return recipe.Text("get_scene lists each item's modifiers and where they move it; remove_motion_modifier takes one off.");
    }

    [McpServerPrompt(Name = "morph_and_macro", Title = "A morph envelope, and a scene macro")]
    [Description("Gives the pawn a squashed morph target and keys its weight (a morph envelope), then runs a Lua scene macro that rings it with cloned crates, and renders.")]
    public static string MorphAndMacro(
        [Description("How many crates the macro rings it with, 3 to 24 (default 8).")] string? crates = null)
    {
        int n = Recipes.Whole(crates, 8, "crates", 3, 24);
        var recipe = new Recipe($"Squash the pawn with a keyed morph and ring it with {n} crates by a Lua macro.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("Frames 1 to 30.", "set_scene", new { first_frame = 1, last_frame = 30, frame = 1 });
        recipe.Step("The pawn.", "add_object", new { file = "objects/pawn.lwo", name = "Pawn" });
        recipe.Step("Its morph target: the squashed pawn (the same points in the same order).", "add_morph_target", new { @object = "Pawn", file = "objects/pawn-squash.lwo", name = "Squash" });
        recipe.Step("Its own shape at frame 1…", "set_key", new { item = "Pawn", channel = "morph.Squash", frame = 1, value = 0 });
        recipe.Step("…squashed at frame 15…", "set_key", new { item = "Pawn", channel = "morph.Squash", frame = 15, value = 1 });
        recipe.Step("…and back at frame 30.", "set_key", new { item = "Pawn", channel = "morph.Squash", frame = 30, value = 0 });
        string code = string.Join("\n",
            $"local n = {n}",
            "for i = 1, n do",
            "  local c = addobject('objects/crate.lwo', 'Crate ' .. i)",
            "  local a = 2 * math.pi * i / n",
            "  setpos(c, 2.2 * math.cos(a), 0, 2.2 * math.sin(a))",
            "  setscale(c, 0.4, 0.4, 0.4)",
            "end",
            "print(#items('object'))");
        recipe.Step($"A Lua macro rings it with {n} small crates (one undo step).", "run_macro", new { code });
        Recipes.LightRig(recipe, "Pawn", 5);
        recipe.Step("The camera up and in front.", "set_transform", new { item = "Camera", position = V(0, 3, -5), key = false });
        recipe.Step("Aim it at the pawn.", "look_at", new { item = "Camera", target = "Pawn", key = false });
        recipe.Step("Ray-trace frame 15, squashed.", "render_scene", new { frame = 15, width = 240, samples = 1 });
        return recipe.Text("The morph's weight is the channel morph.Squash: get_keys shows it, and the graph editor draws it.");
    }

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
