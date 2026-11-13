using System.ComponentModel;
using AVAModeler.Mcp;
using ModelContextProtocol.Server;
using Recipe = AVAModeler.Mcp.Recipes.Recipe;

namespace AVALightWave.Mcp;

/// <summary>
/// Recipes for scenes from a description (the milestone's fourth stage): each makes its objects in the scene, so it
/// needs no files, and builds the shot with build_scene, checks it with check_shot, looks through the camera and
/// renders a contact sheet.
/// </summary>
public static partial class LightWaveRecipes
{
    private static Dictionary<string, object> P(params (string Key, object Value)[] p) => p.ToDictionary(x => x.Key, x => x.Value);

    /// <summary>The usual ending: check the shot, look through the camera, and render a small run of frames as a contact sheet.</summary>
    private static string Finish(Recipe recipe, string folder, int frames, int width, int look, string closing)
    {
        recipe.Step("Check the shot over its frames (build_scene's result says the same): fix anything it lists before rendering.", "check_shot", new { });
        recipe.Step("Look through the camera at a telling frame.", "render_scene_view", new { view = "camera", frame = look, width = 320 });
        recipe.Step($"Render every frame small, as a contact sheet to look over (frames go in {folder}/).", "render_animation", new { folder, name = folder, first = 1, last = frames, width, samples = 1, overwrite = true });
        return recipe.Text(closing);
    }

    [McpServerPrompt(Name = "ball_from_nothing", Title = "Bouncing ball, made from nothing")]
    [Description("Makes a ball and a floor in the scene, lights them, frames the camera and bounces the ball across the floor with squash and stretch, in one build_scene plan; then checks and renders it.")]
    public static string BallFromNothing(
        [Description("Frames, 24 to 600 (default 90).")] string? frames = null,
        [Description("How many bounces after the first landing, 0 to 12 (default 4).")] string? bounces = null,
        [Description("The ball's colour as r, g, b or a name (default red).")] string? color = null,
        [Description("Width of the contact sheet's frames (default 160).")] string? width = null)
    {
        int n = Recipes.Whole(frames, 90, "frames", 24, ModelerTools.MaxAnimationFrames);
        int count = Recipes.Whole(bounces, 4, "bounces", 0, 12);
        var c = Recipes.Colour(color, [220, 40, 30]);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        var recipe = new Recipe($"A ball dropped from 2 m bounces {count} times across a floor over {n} frames, squashing as it lands, under three-point light.");
        recipe.Step("Start a new scene (save or discard the old one if it refuses).", "new_scene", new { });
        recipe.Step("The whole shot in one plan, one undo step: frames, a floor, the ball (its origin at its base, so it squashes onto the floor), lights sized to them, the bounce as exact parabolas travelling 3 m, and then the camera fitted to the ball's whole path from three-quarters.", "build_scene", new
        {
            plan = new
            {
                settings = new { first_frame = 1, last_frame = n, gradient = true, gradient_top = new[] { 70, 90, 120 }, gradient_bottom = new[] { 20, 22, 28 } },
                objects = new object[]
                {
                    new { shape = "ground", size = new[] { 40.0, 30 }, color = new[] { 160, 160, 150 } },
                    new { shape = "ball", name = "Ball", radius = 0.3, color = c, specular = 0.6, glossiness = 256, position = V(-1.5, 0, 0) },
                },
                lights = "three_point",
                motion = new object[] { new { item = "Ball", kind = "bounce", height = 2, count, decay = 0.6, squash = 0.25, by = V(3, 0, 0), start = 1, end = n } },
                camera = new { items = new[] { "Ball" }, start = 1, end = n, from = "three_quarter", margin = 0.25 },
            },
        });
        return Finish(recipe, "bounce", n, w, 1 + n / 8,
            "The bounce is keyed on position.y (Bezier parabolas: the graph editor shows them) and the scale channels. For a ball that rolls as it goes, add {\"kind\": \"spin\", \"axis\": \"bank\", \"degrees\": -340, \"with\": \"previous\"} to the motion.");
    }

    [McpServerPrompt(Name = "solar_system", Title = "Solar system")]
    [Description("A glowing sun and three planets circling it at different speeds, one with a moon circling it, against a night sky, the camera drifting round: made, animated and checked in one plan.")]
    public static string SolarSystem(
        [Description("Frames, 24 to 600 (default 120).")] string? frames = null,
        [Description("Width of the contact sheet's frames (default 160).")] string? width = null)
    {
        int n = Recipes.Whole(frames, 120, "frames", 24, ModelerTools.MaxAnimationFrames);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        var recipe = new Recipe($"A sun with three planets in orbit over {n} frames; the inner planets go round faster, and the blue one has a moon.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step("The system in one plan. The sun glows (luminosity) and holds a point light that doesn't cast its own shadow; planets sit out along +X and orbit it; the moon is the blue planet's child, so it circles the planet as the planet circles the sun.", "build_scene", new
        {
            plan = new
            {
                settings = new { first_frame = 1, last_frame = n, background_color = new[] { 4, 5, 14 }, ambient_intensity = 0.08 },
                objects = new object[]
                {
                    new { shape = "ball", name = "Sun", radius = 0.8, origin = "center", color = new[] { 255, 200, 80 }, luminosity = 1.0, specular = 0.0 },
                    new { shape = "ball", name = "Ember", radius = 0.15, origin = "center", color = new[] { 200, 90, 60 }, position = V(1.8, 0, 0) },
                    new { shape = "ball", name = "Blue", radius = 0.28, origin = "center", color = new[] { 60, 120, 230 }, specular = 0.4, position = V(3, 0, 0) },
                    new { shape = "ball", name = "Moon", radius = 0.1, origin = "center", color = new[] { 200, 200, 200 }, position = V(0.55, 0, 0), parent = "Blue" },
                    new { shape = "torus", name = "Ringed", radius = 0.5, thickness = 0.05, origin = "center", color = new[] { 220, 190, 140 }, position = V(4.6, 0, 0), rotation = V(0, 20, 0) },
                },
                lights = new object[] { new { type = "point", name = "Sunlight", position = V(0, 0, 0), intensity = 1.6, color = new[] { 255, 236, 200 } } },
                steps = new object[] { new { call = "set_item", args = new { item = "Sun", cast_shadows = false } } },
                motion = new object[]
                {
                    new { item = "Ember", kind = "orbit", around = "Sun", turns = 2, start = 1, end = n },
                    new { item = "Blue", kind = "orbit", around = "Sun", turns = 1, with = "previous" },
                    new { item = "Ringed", kind = "orbit", around = "Sun", turns = 0.5, with = "previous" },
                    new { item = "Moon", kind = "orbit", around = "Blue", turns = 4, with = "previous" },
                    new { item = "Sun", kind = "spin", turns = 0.5, with = "previous" },
                },
                camera = new { items = new[] { "all" }, from = V(0, 0.9, -1), margin = 0.1, start = 1, end = n },
                camera_moves = new object[] { new { kind = "orbit", target = "Sun", degrees = 40, start = 1, end = n } },
            },
        });
        return Finish(recipe, "solar", n, w, 1 + n / 3,
            "check_shot may call the moon too small: it is, beside the sun. The moon orbits the blue planet as it moves (orbit keys it where the planet is at each frame, in the planet's space, as the moon is its child). More planets: add a ball and an orbit move each.");
    }

    [McpServerPrompt(Name = "product_reveal", Title = "Product reveal")]
    [Description("A faceted gem pops up on a pedestal under studio light while the camera pushes in and a spot sweeps across: made, animated and checked in one plan.")]
    public static string ProductReveal(
        [Description("Frames, 24 to 600 (default 72).")] string? frames = null,
        [Description("The gem's colour as r, g, b or a name (default emerald [40, 200, 120]).")] string? color = null,
        [Description("Width of the contact sheet's frames (default 160).")] string? width = null)
    {
        int n = Recipes.Whole(frames, 72, "frames", 24, ModelerTools.MaxAnimationFrames);
        var c = Recipes.Colour(color, [40, 200, 120]);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        int pop = Math.Max(6, n / 4);
        var recipe = new Recipe($"A gem pops onto a pedestal in the first {pop} frames, spins slowly, and the camera pushes in while a spot sweeps across, over {n} frames.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step("The reveal in one plan: a pedestal (a cylinder on the floor), a faceted gem on it (a low-sided ball, flat-shaded, reflective), the studio rig, a spot sweeping across, the gem popping in and turning, and the camera pushing in on it.", "build_scene", new
        {
            plan = new
            {
                settings = new { first_frame = 1, last_frame = n },
                objects = new object[]
                {
                    new { shape = "ground", size = new[] { 10.0, 10 }, color = new[] { 40, 40, 46 } },
                    new { shape = "cylinder", name = "Pedestal", radius = 0.5, height = 1, color = new[] { 235, 235, 240 }, specular = 0.2 },
                    new { shape = "ball", name = "Gem", radius = 0.3, sides = 8, smooth = false, color = c, specular = 0.9, glossiness = 1024, reflection = 0.25, position = V(0, 1, 0) },
                },
                lights = "studio",
                steps = new object[]
                {
                    new { call = "add_light", args = new { type = "spot", name = "Sweep", position = V(0, 3.2, -1.6), look_at = V(-1.2, 1, 0), intensity = 1.2, cone_angle = 12, edge_angle = 5, color = new[] { 255, 250, 235 } } },
                },
                motion = new object[]
                {
                    new { item = "Gem", kind = "pop_in", start = 1, duration = pop, name = "pop" },
                    new { item = "Gem", kind = "spin", turns = 0.5, after = "pop", end = n },
                    new { item = "Sweep", kind = "turn", by = V(70, 0, 0), start = 1, end = n, ease = "ease_in_out" },
                },
                camera = new { items = new[] { "Pedestal", "Gem" }, from = "three_quarter", margin = 0.5, frame = 1 },
                camera_moves = new object[] { new { kind = "push_in", target = "Gem", start = 1, end = n, ease = "ease_out" } },
            },
        });
        return Finish(recipe, "reveal", n, w, n,
            "check_shot says the pedestal is cut off as the camera closes in: that's the close-up, meant. For a sharper finish, render the last frame bigger with more samples (render_scene frame " + n + ", samples 8): the gem's facets catch the spot.");
    }

    [McpServerPrompt(Name = "domino_run", Title = "Falling dominoes")]
    [Description("A row of dominoes made in the scene topples one after another, each tipping onto the next and the last falling flat, keyed in sequence with one animate call per domino row; the camera tracks along.")]
    public static string DominoRun(
        [Description("How many dominoes, 3 to 60 (default 12).")] string? count = null,
        [Description("Frames between one domino and the next starting to fall, 1 to 30 (default 4).")] string? gap = null,
        [Description("Width of the contact sheet's frames (default 160).")] string? width = null)
    {
        int k = Recipes.Whole(count, 12, "count", 3, 60);
        int g = Recipes.Whole(gap, 4, "gap", 1, 30);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        const double height = 0.5, thick = 0.08, spacing = 0.3, fall = 8;
        // A domino tips about its bottom edge until it rests on the next: its top then touches the next's side.
        double lean = R(Math.Asin((spacing - thick) / height) * 180 / Math.PI);
        int n = (int)(1 + g * (k - 1) + fall * 2);
        var objects = new List<object> { new { shape = "ground", size = new[] { 20.0, k * spacing + 20 }, color = new[] { 120, 110, 95 } } };
        var moves = new List<object>();
        var pivots = new List<object>();
        for (int i = 0; i < k; i++)
        {
            string name = $"Domino {i + 1}";
            objects.Add(new { shape = "box", name, size = new[] { 0.25, height, thick }, color = i % 2 == 0 ? new[] { 240, 240, 235 } : new[] { 30, 30, 34 }, position = V(0, 0, i * spacing + thick / 2) });
            pivots.Add(new { call = "set_item", args = new { item = name, pivot = V(0, 0, thick / 2) } });
            bool last = i == k - 1;
            moves.Add(new { item = name, kind = "turn", by = V(0, last ? 90 : lean, 0), start = 1 + g * i, duration = last ? fall * 1.5 : fall, ease = "ease_in" });
        }
        var recipe = new Recipe($"{k} dominoes {F(spacing)} m apart topple in turn, {g} frames apart; each tips {F(lean)}° onto the next (its pitch, about its front bottom edge: each one's pivot is set there), and the last falls flat.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step("The run in one plan: the dominoes along +Z (pitch tips them toward +Z), a sun and sky, each one's pivot moved to its front bottom edge so it tips over that edge, the falls keyed in sequence, the camera fitted from the side over the whole run.", "build_scene", new
        {
            plan = new
            {
                settings = new { first_frame = 1, last_frame = n },
                objects,
                lights = "sun_and_sky",
                steps = pivots,
                motion = moves,
                camera = new { items = new[] { "all" }, from = V(1, 0.6, -0.4), margin = 0.1 },
            },
        });
        return Finish(recipe, "dominoes", n, w, 1 + n / 2,
            "The fall is keyed, not simulated: ease_in makes each one speed up as it tips. A longer run curves if each domino's heading turns a little (turn by [heading, pitch, bank]) and its position follows a curve.");
    }

    [McpServerPrompt(Name = "rocket_launch", Title = "Rocket launch with smoke")]
    [Description("Builds a rocket from a body, a nose cone and fins, stands it on a pad, shakes it, then launches it with a plume of smoke while the camera looks up and follows: build_object, particles and motion in one plan.")]
    public static string RocketLaunch(
        [Description("Frames, 30 to 600 (default 96).")] string? frames = null,
        [Description("Width of the contact sheet's frames (default 160).")] string? width = null)
    {
        int n = Recipes.Whole(frames, 96, "frames", 30, ModelerTools.MaxAnimationFrames);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        int lift = n / 3;
        var recipe = new Recipe($"A rocket shakes on its pad for {lift} frames, then climbs 12 m by frame {n} on a plume of smoke.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step("The launch in one plan. The rocket is built from modeling steps: a body (Disc with height), a nose (Cone) on top and four fins (a Box arrayed round the axis), each with its own surface. A disc facing down under it is the exhaust, the rocket's child, emitting smoke that rises and spreads. It shakes, then climbs with ease_in; the camera stands off to the side (placed in steps, so the track move that keeps it aimed at the rocket starts from there).", "build_scene", new
        {
            plan = new
            {
                settings = new { first_frame = 1, last_frame = n, gradient = true, gradient_top = new[] { 90, 140, 210 }, gradient_bottom = new[] { 210, 220, 235 } },
                objects = new object[]
                {
                    new { shape = "ground", size = new[] { 80.0, 80 }, color = new[] { 110, 120, 100 } },
                    new { shape = "cylinder", name = "Pad", radius = 1.0, height = 0.2, color = new[] { 90, 90, 95 } },
                    new
                    {
                        name = "Rocket", origin = "base", position = V(0, 0.2, 0),
                        steps = new object[]
                        {
                            new { call = "set_surface", args = new { name = "Hull", color = new[] { 235, 235, 240 }, specular = 0.5, glossiness = 256, smoothing = true, assign = false } },
                            new { call = "set_surface", args = new { name = "Trim", color = new[] { 210, 40, 30 }, specular = 0.4, assign = false } },
                            new { tool = "Disc", @params = P(("Center", V(0, 1.2, 0)), ("Radius", V(0.25, 0.25, 0.25)), ("Height", 2.0), ("Sides", 24), ("Surface", "Hull")) },
                            new { call = "select_none" },
                            new { tool = "Cone", @params = P(("Center", V(0, 2.55, 0)), ("Radius", V(0.25, 0.25, 0.25)), ("Height", 0.7), ("Sides", 24), ("Surface", "Trim")) },
                            new { call = "select_none" },
                            new { call = "set_layers", args = new { foreground = new[] { 2 } } },
                            new { tool = "Box", @params = P(("Center", V(0, 0.45, 0.4)), ("Size", V(0.04, 0.6, 0.35)), ("Surface", "Trim")) },
                            new { tool = "Array", @params = P(("Mode", "Radial"), ("Radial count", 4), ("Axis", "Y"), ("Center", V(0, 0, 0))) },
                        },
                    },
                    new { shape = "disc", name = "Exhaust", radius = 0.2, origin = "center", color = new[] { 255, 160, 60 }, luminosity = 1.0, position = V(0, 0.15, 0), rotation = V(0, 0, 180), parent = "Rocket" },
                },
                lights = "sun_and_sky",
                steps = new object[]
                {
                    new { call = "set_transform", args = new { item = "Camera", position = V(-4, 1.2, -9), key = false } },
                    new { call = "set_particles", args = new { item = "Exhaust", rate = 120, start_frame = 1, end_frame = n, life = 2.5, speed = 3, spread = 30, gravity = -0.6, drag = 1.5, size = 0.25, size_at_death = 4, color = new[] { 225, 225, 225 }, glow = 0, look = "puff", floor = 0 } },
                },
                motion = new object[]
                {
                    new { item = "Rocket", kind = "shake", amplitude = 0.02, start = 1, end = lift, name = "rumble" },
                    new { item = "Rocket", kind = "move", by = V(0, 12, 0), after = "rumble", end = n, ease = "ease_in" },
                    new { item = "Camera", kind = "track", target = "Rocket", start = 1, end = n, every = 3 },
                },
            },
        });
        return Finish(recipe, "launch", n, w, n * 2 / 3,
            "check_shot says the pad leaves the picture as the camera tilts up after the rocket: meant. Smoke is drawn as soft puffs (look puff) that cast shadows; keep the rate modest and render small first. The fins are layer 2 of the rocket's object, merged with the body when it's put in the scene.");
    }

    [McpServerPrompt(Name = "dancing_shapes", Title = "A row of dancing shapes")]
    [Description("Five shapes made in the scene, in a row, each dancing its own way to a shared beat (bouncing, spinning, swaying, bobbing, popping), while the camera drifts round the row.")]
    public static string DancingShapes(
        [Description("Frames, 24 to 600 (default 96).")] string? frames = null,
        [Description("Frames to a beat, 6 to 60 (default 24).")] string? beat = null,
        [Description("Width of the contact sheet's frames (default 160).")] string? width = null)
    {
        int n = Recipes.Whole(frames, 96, "frames", 24, ModelerTools.MaxAnimationFrames);
        int b = Recipes.Whole(beat, 24, "beat", 6, 60);
        int w = Recipes.Whole(width, 160, "width", 16, ModelerTools.MaxAnimationSize);
        double beats = Math.Max(1, Math.Round((double)(n - 1) / b));
        var recipe = new Recipe($"Five shapes dance to a beat of {b} frames for {n} frames: a ball bounces on every beat, a box spins, a cone shimmies from side to side, a torus bobs and a cylinder pops in and turns.");
        recipe.Step("Start a new scene.", "new_scene", new { });
        recipe.Step("The dance in one plan: five shapes 1.4 m apart, each its own colour; their moves share the beat (bounce count and cycles follow it); the camera fitted to the whole row as it dances, drifting a little way round.", "build_scene", new
        {
            plan = new
            {
                settings = new { first_frame = 1, last_frame = n },
                objects = new object[]
                {
                    new { shape = "ground", size = new[] { 40.0, 30 }, color = new[] { 60, 60, 80 } },
                    new { shape = "ball", name = "Bouncer", radius = 0.3, color = new[] { 230, 70, 60 }, position = V(-2.8, 0, 0) },
                    new { shape = "box", name = "Spinner", size = new[] { 0.6, 0.6, 0.6 }, color = new[] { 250, 200, 60 }, position = V(-1.4, 0, 0) },
                    new { shape = "cone", name = "Swayer", radius = 0.35, height = 1, color = new[] { 80, 200, 120 }, position = V(0, 0, 0) },
                    new { shape = "torus", name = "Bobber", radius = 0.35, thickness = 0.1, origin = "center", color = new[] { 80, 140, 240 }, position = V(1.4, 0.8, 0), rotation = V(0, 70, 0) },
                    new { shape = "cylinder", name = "Popper", radius = 0.3, height = 0.7, color = new[] { 200, 90, 220 }, position = V(2.8, 0, 0) },
                },
                lights = "three_point",
                motion = new object[]
                {
                    new { item = "Bouncer", kind = "bounce", height = 0.8, count = (int)beats - 1, decay = 1.0, start_at = "ground", squash = 0.3, start = 1, end = n, name = "beat" },
                    new { item = "Spinner", kind = "spin", turns = beats / 2, with = "beat", ease = "ease_in_out" },
                    new { item = "Swayer", kind = "sway", channel = "position.x", amplitude = 0.25, cycles = beats / 2, with = "beat" },
                    new { item = "Bobber", kind = "bob", amplitude = 0.25, cycles = beats, with = "beat" },
                    new { item = "Popper", kind = "pop_in", start = 1, duration = b, name = "popped" },
                    new { item = "Popper", kind = "spin", turns = 1, after = "popped", end = n },
                },
                camera = new { items = new[] { "all" }, from = V(0, 0.35, -1), margin = 0.15, start = 1, end = n },
                camera_moves = new object[] { new { kind = "orbit", target = "Swayer", degrees = 12, start = 1, end = n } },
            },
        });
        return Finish(recipe, "dance", n, w, 1 + b / 2,
            "Every move's timing is relative (with the bounce, after the pop), so changing the bounce's span moves the rest with it.");
    }
}
