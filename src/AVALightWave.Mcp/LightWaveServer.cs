using System.Reflection;
using AVAModeler.Hub;
using AVAModeler.Mcp;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>
/// AVALightWave's MCP server: the engine's scene tools only (<see cref="ToolGroups.Scene"/>: staging, lighting,
/// animating, deforming and rendering; the engine's modeling tools aren't served as such), AVALightWave's own tools
/// (<see cref="LightWaveTools"/>: making objects with the engine's modeling calls on documents of their own, replacing
/// and saving objects, and the hub) and recipes (<see cref="LightWaveRecipes"/>,
/// with the engine's three-point lighting), with instructions written for a standalone Layout. The same profile serves
/// <c>AVALightWave --mcp</c> headless, the open window's live pipe, and the AI Chat.
/// </summary>
public static class LightWaveServer
{
    public const string Name = "AVALightWave";

    /// <summary>The engine's recipes this server keeps (the rest model, which AVALightWave doesn't).</summary>
    public static readonly IReadOnlySet<string> EngineRecipes = new HashSet<string> { "three_point_lighting" };

    public const string Instructions = """
        AVALightWave is a standalone scene, animation and render program in the spirit of classic LightWave Layout (versions 4 and 5). You make or load objects, light them, animate them and ray-trace them, with the same tools a person uses in its panels. Objects come from files (LightWave .lwo, .obj, .geo, .glb/.gltf, .stl, .3mf) in the working folder, or are made in the scene: make_object for ready-made shapes, build_object for anything else from the engine's modeling tools (AVA Modeler's). The person can also send an object to AVA Modeler, the companion modeler (edit_in_modeler, in the open window), after which its saves update it here.

        Conventions (they matter):
        - Units are metres. The space is left-handed, as LightWave's: +X right, +Y up, +Z away from the viewer in the Front view. Items have position [x, y, z], rotation [heading, pitch, bank] in degrees and scale. Heading turns about +Y (90 faces +X), then pitch about the turned X axis (positive looks down), then bank about the turned Z axis. Cameras look along their +Z axis with +Y up; distant and spot lights shine along their +Z; point lights shine every way. look_at aims anything at a point or item.
        - Colours are [r, g, b], whole numbers from 0 to 255, everywhere: lights, ambient, backdrop and fog. Intensity 1 is 100%.
        - A camera's lens is LightWave's zoom factor: zoom = 1 / tan(vertical field of view / 2); 3.2 (the default) is 34.7 degrees high. A spot's cone_angle is its half-angle; range is metres to fade out (0: no falloff).
        - Making objects: make_object makes a box, ball, cylinder, cone, disc, torus, ground (a floor slab with its top at y = 0), steps or extruded text, with a surface of its own named after it (colour, specular, glossiness, reflection, transparency, luminosity); its origin is at the middle of its bottom by default, so at y = 0 it stands on the ground and scaling squashes it onto the floor. build_object builds anything else from steps, each one of the engine's modeling calls on an object of its own: {"tool": "Lathe", "params": {...}} runs a modeling tool (list_modeling_tools and describe_modeling_tool give them and their parameters), {"call": "set_surface", "args": {...}} makes another call (add_points, add_polygons, set_layers, the select_ calls, set_surface, set_texture, set_procedural, make_uvs, run_lua, check_mesh…). edit_object runs the same steps on an object already in the scene, keeping its motion. set_object_surface changes an object's look (get_object_surfaces lists its surfaces). Each is one undo step; made objects have no file until save_object (.avascene keeps them inside the scene).
        - Objects: add_object loads a file from the working folder into the scene (give file; layer takes one of its layers). An object keeps its own coordinates; position, rotation and scale place it. replace_object swaps an object's geometry for another file's, keeping its motion, parent and bones (classic Layout's Replace Object). save_object writes an object's geometry to a file. get_scene gives every object's world bounds: use them to size lights and frame the camera.
        - Frames run from the scene's first to last frame, 1 to 60 at 30 frames a second by default (set_scene changes them). Channels are keyed at frames and interpolated by TCB splines. set_transform and look_at key when key is true (Auto Key, on unless the person turned it off) and otherwise just set values; the first key on a channel replaces its constant, so key the start frame first, then later frames. get_keys shows what is keyed; shift_keys and scale_keys retime keys as the dope sheet does.
        - The scene has one undo history, shared with the person: each call that changes it is one step ("Agent: …"), undo_scene and redo_scene step through it, and the person's Ctrl+Z undoes your steps too. new_scene and open_scene clear it. A call that fails changes nothing and leaves no step.
        - Items: add_null (for orbits and rigs: parent things to it with set_item parent and key its heading), add_light (distant, point, spot or area), add_camera, add_bone; set_item changes any item's settings (and locked, which only keeps the person's mouse off it). Paths: set_follow_path makes an item follow a smooth curve through nulls, its place along it the keyable channel path.position; bake_path makes ordinary keys. Bones bend objects: skelegons_to_bones turns an object's skelegons into bones, set_ik gives a chain a goal (inverse kinematics), bake_ik keys it. Displacements (add_displacement) ripple and wave objects; morph weights (morph.<name>) are keyed with set_key.
        - Better-looking renders, all off by default (each costs time; render small first): set_scene ao (ambient occlusion) and environment (a lat-long .hdr/.png/.jpg from the working folder, or builtin:sky / builtin:studio; environment_lighting lights the scene with it); area lights and point/spot lights with a radius for soft shadows; set_item on the camera for depth_of_field (focus_on an item, f_stop 0.1 to 0.5 for visible blur) and motion_blur.
        - Light effects: set_lens_flare (a glow, ring and streaks where a light is seen; flare.intensity is keyable), set_volumetric_light (a spot's or point light's beam in the air; costly: few samples first), set_shadow_map (soft shadows from a map, much faster than soft ray-traced ones). set_dissolve fades an object (0 solid, 1 gone; the channel "dissolve").
        - Particles and hair: set_particles makes an object emit particles (sparks, fountains, dust, snow) from its faces, points or pivot between two frames, with speed, spread, life, gravity, drag and an optional floor; set_hair grows tapering strands (fur, grass) on its faces or one surface, with density, length, droop and frizz. Particles can look like blobs that melt together, puffs of smoke, glowing sprites or copies of another object (look, instance_of), and collide with other objects (collide, on_hit stick, bounce or die); particles can flow as a liquid (fluid, with viscosity; look blob shows one surface), pouring, pooling and filling what holds them. Hair can be combed (comb, or point by point with comb_hair) and swing as the object and its bones move (dynamics). set_soft_body makes an object cloth or a soft body (falling, draping, pinned by a surface, kept off a floor and other objects, and with self_collide off itself; use a mesh with enough points to bend). Puffs of smoke show in reflections and cast soft shadows. All are rendered and kept in .avascene and .lws (as plugin sections other programs skip); keep counts modest (render small first).
        - Images and compositing: load_image puts a picture or a numbered sequence (sequence true) from the working folder in the scene's image list; set_compositing puts one behind the render (background, where the camera sees nothing) and one over it (foreground, keyed by a colour range or alpha).
        - Motion in a phrase: animate writes ordinary keys for a list of moves in one undo step: move, turn, grow, bounce (exact parabolas, decaying, with squash and stretch), spin, orbit, sway, bob, shake, pop_in, pop_out, fade_in, fade_out, through (a list of points), track (keep facing an item), and the camera's dolly, truck, crane, push_in and follow. Each has a span (start, or after/with another move, plus delay; duration or end) and an ease (linear, ease_in, ease_out, ease_in_out, overshoot), and starts from where the moves before left the item, so a whole sequence of beats across many items is one call; aim keeps an item facing a target while it moves. camera_move gives the camera a shot (dolly, push_in, orbit, crane, truck, follow, handheld) keeping its target in view. Prefer these to keying frame by frame; set_key and the graph editor refine what they make.
        - Whole shots: build_scene builds a shot from one plan in one undo step, all or nothing: settings, objects (make_object, build_object or add_object arguments), lights (a rig: three_point, sun_and_sky, studio or night, sized and aimed to the objects; or add_light arguments), camera (frame_items arguments, or a position and look_at), steps (any scene calls), motion (animate moves) and camera_moves; it returns check_shot's look at the result. frame_items places the camera to fit chosen items (from front, three_quarter, side, top…), at a frame, over a span, or following them. check_shot looks over the frames without rendering and lists what to fix: objects out of view, cut off, too small, below the ground or overlapping, unlit, a camera inside something, keys after the end.
        - Animation: set_key takes a span (tcb, linear, stepped, bezier with in_slope and out_slope in units per frame) as well as tension, continuity and bias; set_end_behavior repeats or oscillates a channel. add_motion_modifier adds lag (follow another item some frames behind), jitter, cycle (driven by another item's channel) or oscillate. add_morph_target gives an object a target shape from a file or another object, its weight the channel morph.<name>. skelegons_to_bones rigs a loaded object in one step.
        - Workflow: clone_item copies an item (offset in place and time, as Clone Item does), load_from_scene merges another scene's items, save_motion and load_motion carry an item's keys in LightWave .mot files, run_macro runs a Lua scene macro as one step, and make_preview makes a quick flipbook (the view's drawing, not ray traced) to check motion before rendering.
        - Long renders: queue_render sends the scene as a job to a render queue folder, which render nodes work frame by frame (the window's own, and "AVALightWave --render-node <folder>" processes on this or other machines); render_queue_status shows each job's frames done and the nodes. Use it for many or large frames instead of render_animation.

        A good way to make a scene from a description: plan it (objects, look, lights, camera, the beats of the motion), new_scene, then build_scene with the whole plan; fix what its check says (or check_shot after changes); render_scene_view (camera) at a few frames, then render_animation small for a contact sheet; then render. Step by step instead: get_scene; make_object, build_object or add_object; get_scene again for its bounds; light it (the three_point_lighting recipe, or add_light and look_at); place the camera with set_transform and look_at; render_scene_view (camera, then top) to check placement; key the motion at the first and later frames; check an in-between frame with get_scene; render_scene a frame small and look at it; then render_animation (with movie "mp4" or "gif" if ffmpeg is installed) and save_scene (.avascene keeps everything; .lws for LightWave, with objects referenced by their files). export_scene writes glTF.

        The server's prompts are saved recipes for scene work (scenes made from nothing with build_scene: a bouncing ball, a solar system, a product reveal, falling dominoes, a rocket launch with smoke, a row of dancing shapes; and with object files: staging an object, a turntable, a camera fly-by on a path, a sweeping spotlight, rendering on a farm, a staggered row of clones, volumetric beams with a flare, compositing over a picture, rigging from skelegons, motion modifiers and motion files, a morph envelope and a macro, three-point lighting): step-by-step plans with the exact tool calls, to follow or adapt.
        """;

    /// <summary>What the live window adds to the instructions.</summary>
    public const string LiveNote = "This is the live app: the person using AVALightWave sees every change in its views, and their own edits share the same scene and undo history.";

    /// <summary>AVALightWave's tool descriptions where the engine's speak of a Modeler object it doesn't have.</summary>
    public static string Describe(string tool, string description) => tool switch
    {
        "add_object" => "Loads an object file from the working folder (.lwo, .lw, .obj, .geo, .glb, .gltf, .stl or .3mf) into the scene as a new item; give file. layer (1 to 10) takes just that layer of a layered object; otherwise every non-empty layer, merged. The object's own coordinates are kept: position, rotation and scale (constants, not keys) place it. Several objects may come from one file. Returns the item, with its world bounds.",
        "save_scene" => description.Replace("(the Modeler's unsaved changes are then lost)", "(changes not saved with save_object are then lost)", StringComparison.Ordinal)
            .Replace("(save the object first with save, as .lwo)", "(save_object first for an object with no file or unsaved changes)", StringComparison.Ordinal)
            .Replace("AVA Modeler's own", "the engine's own", StringComparison.Ordinal),
        "set_morph" => description + " In AVALightWave there are no Modeler layers: rename a morph or set its weight; give a new target in AVA Modeler.",
        _ => description,
    };

    /// <summary>The server's profile, with the window's hub and objects (<paramref name="app"/>) when it's the live window's, else headless.</summary>
    public static ServerProfile Profile(ILightWaveApp? app = null) => new()
    {
        Name = Name,
        Title = "AVALightWave",
        Version = typeof(LightWaveServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.2.0",
        Instructions = Instructions,
        IncludeTool = ToolGroups.Scene.Contains,
        IncludePrompt = EngineRecipes.Contains,
        Describe = Describe,
        ExtraTools = host => Tools(host, app),
        ExtraPrompts = Prompts,
        HasModelerObject = false,
        PipeName = () => PipeName,
        Program = "AVALightWave",
        Usage = Usage,
        NotRunning = NotRunning,
        LiveNote = LiveNote,
    };

    /// <summary>AVALightWave's own tools, bound to a host (and the window, if live).</summary>
    public static IEnumerable<McpServerTool> Tools(IModelerHost host, ILightWaveApp? app)
    {
        var target = new LightWaveTools(host, app);
        foreach (var method in typeof(LightWaveTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>AVALightWave's recipes.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(LightWaveRecipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>
    /// The pipe the open window takes agents on: per user. AVALIGHTWAVE_MCP_PIPE can name another (the window and the
    /// bridge both read it).
    /// </summary>
    public static string PipeName =>
        Environment.GetEnvironmentVariable("AVALIGHTWAVE_MCP_PIPE") is { Length: > 0 } custom ? custom
            : "AVALightWave-mcp-" + new string(Environment.UserName.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());

    public const string NotRunning =
        "No AVALightWave is taking AI agents just now. Start AVALightWave and turn on Options › Allow AI Agents (MCP), then call again. " +
        "To work on a scene of your own without the window, run the server as \"AVALightWave --mcp\" (without --attach).";

    public const string Usage = """
        AVALightWave --mcp [--attach] [--root FOLDER]

          --mcp          Serve the Model Context Protocol on stdin and stdout for an AI agent, with no window: the
                         scene, object-making, lighting, animation and render tools.
          --attach       Drive the AVALightWave that's open on this desktop (with Options › Allow AI Agents on),
                         instead of a scene of the server's own.
          --root FOLDER  The working folder that object, scene and image paths start from, and that loading and
                         saving stay inside (default: the current folder).
        """;

    /// <summary>The hub's pipe name for this app (for status lines).</summary>
    public static string HubPipe => HubProtocol.PipeName(HubProtocol.LightWave);
}

/// <summary>What the open window gives the server beyond the scene: the hub.</summary>
public interface ILightWaveApp
{
    /// <summary>Edit in Modeler for an object (on the window's thread): sends it to AVA Modeler, starting it if need be. Returns what happened.</summary>
    Task<string> EditInModeler(AVAModeler.Scene.ObjectItem item);

    /// <summary>Whether the window is on the hub, and whether AVA Modeler is there.</summary>
    (bool OnHub, bool ModelerThere) HubState();

    /// <summary>The window's render queue folder, where queue_render sends jobs when no folder is given (null: none).</summary>
    string? QueueFolder => null;
}
