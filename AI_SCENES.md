# Milestone: scenes from a description

**Status (12 November 2026):** done. Stage 1 done: `make_object` (nine shapes), `build_object`, `edit_object`,
`set_object_surface`, `get_object_surfaces` and the two modeling-tool lists, on the engine's tools through an
in-process caller (`EngineCalls.cs`). Stage 2 done: `animate` (twenty kinds, in `Motion.cs`) and `camera_move`;
a `with` move lasts as long as the one it runs with, and handheld is a jitter modifier rather than keys.
Stage 4 done: six recipes made from nothing (`SceneRecipes.cs`), the instructions and the AI Chat's prompt, and the
README's Claude Code setup; the server is registered on this machine. Done. In `build_scene` the camera is placed
after the motion (so it fits where things go), and the three-point rig's key is a distant light (a spot's pool would
stay behind a moving subject).
Stage 3 done: `build_scene`, `frame_items` and `check_shot` (`ShotTools.cs`, `Shot.cs`). Its checks now use real shapes (MILESTONES.md, A.4).

AVALightWave already serves agents its Layout-only MCP server: 64 tools and 12 recipes, live in the open window
(`AVALightWave --mcp --attach`) or headless (`--mcp`). An agent can stage, light, animate and render, but only objects
that already exist as files. It can't make an object or give one a look, and every motion is keyed by hand, one
channel and one frame per call. Asking for "a red ball bouncing down three steps while the camera circles it" takes
dozens of calls and a modeler.

This milestone lets an agent build such a scene from a description, in AVALightWave alone, with fewer and larger
calls, and check it before rendering. As before, everything is in the open window's undo history, and a call that
fails changes nothing.

## Stage 1: objects made in the scene

- **`make_object`** makes a ready-made shape as a new object item. The shapes are box, ball, cylinder, cone, disc,
  torus, ground (a floor slab whose top is y = 0), steps and text. It takes the shape's size and detail, a surface
  (colour, specular, glossiness, reflection, transparency, luminosity, smoothing), whether the object's origin is at
  its base (so it rests on the ground and squashes onto it), and a position, rotation, scale and parent.
- **`build_object`** builds any object from a list of steps. Each step is one of the engine's modeling calls, as AVA
  Modeler's server takes it: `run_tool` (Box, Ball, Lathe, Extrude, Bevel, Boolean, Array, Text…), `add_points`,
  `add_polygons`, `set_layers`, the `select_*` calls, `set_surface`, `set_texture`, `set_procedural`,
  `set_surface_nodes`, `make_uvs`, `set_uv_map` and `run_lua`. The steps run on a fresh object of the agent's own, and
  the result goes into the scene. If a step fails, the error names it and nothing is added.
- **`edit_object`** runs the same steps on a copy of an object that's already in the scene. The new geometry takes the
  place of the old (undoable), and the object keeps its motion.
- **`set_object_surface`** changes the look of one of an object's surfaces: the same settings as `set_surface`, plus
  a procedural texture. The object gets a changed copy of its document, so undo puts the old look back.
- **`list_modeling_tools`** and **`describe_modeling_tool`** list the engine's modeling tools and their parameters,
  for `build_object` and `edit_object`.
- Each call is one undo step. Objects made this way have no file until `save_object`. `.avascene` keeps them inside
  the scene, and a `.lws` save writes them into `Objects/` beside it (in the window). The headless server says to
  `save_object` first.

## Stage 2: motion in a phrase

- **`animate`** takes a list of moves, each for one item over a span of frames, and writes ordinary keys for them,
  which the graph editor and dope sheet can edit afterwards. All the moves are one undo step. The kinds of move:
  - `move` goes to a place, `turn` to an angle and `grow` to a scale, each from where the item is.
  - `bounce`: a number of bounces, a height and how fast they die away, with optional squash and stretch.
  - `spin`: turns about an axis.
  - `orbit`: circles a point or an item, at a radius, for a number of turns.
  - `sway`: a pendulum swing on any channel, with damping.
  - `bob`: hovering up and down.
  - `shake`: smooth, repeatable noise.
  - `pop_in` and `pop_out`: scale with an overshoot.
  - `fade_in` and `fade_out`: dissolve.
  - `through`: a smooth trip through a list of points, optionally facing the way it goes.
  - `track`: keeps aiming at an item, keyed every few frames.

  Every move has an ease: `linear`, `ease_in`, `ease_out`, `ease_in_out` or `overshoot`. A move's start frame can be
  given relative to the end of the move before it (`after`) or to another named move (`with`), so a sequence of beats
  across many items is written in one call.
- **`camera_move`** gives the camera a shot over a span of frames, as keys:
  - `dolly` moves it in or out along its view, and `push_in` closes in on an item.
  - `orbit` circles an item, and `crane` raises or lowers it while it keeps aiming.
  - `truck` slides it sideways.
  - `follow` keeps a set distance behind an item.
  - `handheld` adds a gentle shake.

  Each one keeps the target framed. The agent gives the start frame, the end frame and an ease.

## Stage 3: whole shots, checked

- **`build_scene`** builds a whole shot from one plan, as one undo step, all or nothing. A plan has these parts:
  - settings: frames, frame rate, backdrop or environment, and ambient occlusion;
  - objects: `make_object` shapes, `build_object` steps, or files;
  - lights: given one by one, or a rig (`three_point`, `sun_and_sky`, `studio`, `night`), aimed at the objects;
  - a camera: a place and an aim, or `frame` to fit chosen items;
  - motion: `animate` moves and a `camera_move`.

  It returns what it made, and the first problem it met, if any. Inside it, steps are the same calls as the separate
  tools, so a plan that works can be taken apart and changed call by call.
- **`frame_items`** places, or keys, the camera so that the chosen items fill the picture, with a margin. It works at
  one frame, or over a span of frames as they move.
- **`check_shot`** looks at the scene over its frames without rendering, at sampled frames. It reports:
  - each object outside the camera's view, cut off by the edge of the picture, or too small to see;
  - objects that sink into the ground or into each other;
  - objects that no light reaches;
  - keys that run past the scene's last frame;
  - a camera that sits inside an object.

  Each problem comes with the frames where it happens and what to change.

## Stage 4: recipes, the chat and Claude Code

- New recipes, each built with the new tools:
  - a bouncing ball made from nothing;
  - a solar system;
  - a product reveal, with a pop-in, a camera push and a sweeping light;
  - a falling row of dominoes, keyed in sequence;
  - a rocket launch with smoke;
  - a dancing row of shapes.

  Each one is followed call by call in the tests. Its last picture must not be blank.
- The instructions and the AI Chat's prompt describe the new tools and the order to use them in: plan, `build_scene`,
  `check_shot`, a contact sheet, then render.
- The README explains how to add AVALightWave to Claude Code, both live (`claude mcp add … --mcp --attach`) and
  headless. The server is also registered on this machine.

## Not in this milestone

- Physics. A bounce or a falling domino is keyed, not simulated. The engine's soft bodies and particles stay as they
  are.
- Character animation: walk cycles and lip sync.
- Natural-language parsing in the app itself. The agent turns the description into calls.
