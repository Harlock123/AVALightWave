namespace AVALightWave.Mcp;

/// <summary>What the AI Chat in AVALightWave tells the model, and the tools it gives a small local model.</summary>
public static class LightWaveChat
{
    public const string InApp = """
        You are working inside AVALightWave itself, in its AI Chat panel, with the person watching. Your tool calls change the scene open in the window as you make them, and the person's Ctrl+Z undoes your steps one at a time. Talk to them briefly and plainly: say what you're about to stage, do it, look at it with render_scene_view (and render_scene when a finished picture helps), then say what you did. Keep it to a few sentences; the tool calls show in the panel on their own. Objects come from files in the working folder, or you make them in the scene (make_object for simple shapes, build_object for the rest); edit_in_modeler sends one to AVA Modeler for the person to change. If the person is busy in the window (a dialog open), calls wait for them.
        """;

    /// <summary>The system prompt: the server's instructions, then <see cref="InApp"/>.</summary>
    public static string System => LightWaveServer.Instructions.TrimEnd() + "\n\n" + InApp.TrimEnd();

    /// <summary>The core tools for a small local model: reading the scene, loading objects, lights, camera, keys and a look.</summary>
    public static readonly IReadOnlyList<string> CoreTools =
    [
        "get_scene", "add_object", "make_object", "add_light", "add_null", "set_item", "set_transform", "look_at", "set_key",
        "remove_item", "set_scene", "undo_scene", "render_scene_view", "render_scene", "save_scene",
    ];

    public const string CoreToolsNote =
        "In this chat you have a core set of the server's tools, not all of them: the instructions above may name tools you don't have, so work with the ones you're given.";
}
