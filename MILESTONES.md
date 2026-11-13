# Milestones after "scenes from a description"

`AI_SCENES.md` was the first of these: agents making, animating and checking whole scenes. These five come next. The
user asked for all of them on 12 November 2026. They are built in this order, one commit per stage, with tests and
docs, and pushed only when asked.

Each stage says where it lives. **App** means AVALightWave alone. **Engine** means AVA Modeler first, as an additive
change with its own tests and docs, after which the submodule is moved to it (AGENTS.md: one copy of the engine).

## Milestone A: conveniences and agent extras

**Status:** done. The bookmarks, the cut (`avalightwave.shots`) and the style (`avalightwave.style`) are kept in the
engine's `Scene.Extras` (AVA Modeler 97cb1a3). The wizards are `ShotWizards` in the Mcp project, running `build_scene`'s `RunPlan`.

1. **Camera bookmarks (app).** Saved camera views, each with a name, position, rotation and zoom, kept in the scene.
   Recall puts the camera there, with a key if it's animated. They're in the Camera panel and on the
   `camera_bookmark` tool.
2. **Scene package (app).** File › Package Scene… copies the scene, every object, image and audio file it uses, into
   one folder (or a .zip), with the paths rewritten to be relative, so it opens on another machine. The
   `package_scene` tool does the same.
3. **Turntable and product-shot wizards (app).** A dialog in the window, with no agent. It takes the object and the
   look (turntable, or product shot with a pedestal, studio light and a camera push), then frames and a size. It
   builds the shot as one undo step, using the same code as `build_scene`.
4. **Exact checks (app).** `check_shot` tests real geometry rather than boxes:
   - overlap by triangles, using the engine's BVH;
   - ground contact from the lowest real point;
   - whether something is in view from its actual projected outline.
5. **Self-review (app).** `review_shot` is one call that returns `check_shot`'s findings and a small contact sheet
   (with frame numbers on it), so an agent sees and reads its shot together before reporting.
6. **Shot lists (app).** `build_shots` takes several shots, each with a camera, a frame range and its moves. It makes
   one camera per shot and renders each shot's frames into one folder in order, so the cut plays as one sequence (and
   a movie with ffmpeg).
7. **Style presets (app).** A style is a named set of lights, surface treatment, lens and easing: `noir`,
   `product_commercial`, `cartoon`, `golden_hour`, `sci_fi`. `apply_style` applies one, and `build_scene` takes a
   `style`.

## Milestone B: physics and expressions

**Status:** done. Expressions are the engine's `ExpressionModifier` (AVA Modeler 6f0210e), in Motion Options. Rigid
bodies (AVA Modeler ab77b73: sequential impulses with friction
and bounce, a body's sample points against the other's distance function, sleeping; ties between faces broken
toward the other body so equal boxes stack).

1. **Rigid bodies (engine).** Objects can be dynamic or static, with mass, bounce, friction and a shape (box, sphere,
   or the object's own convex hull). A fixed-step simulation from the first frame handles gravity, collisions and
   stacking, cached like soft bodies. The ray tracer and the views see it.
2. **Rigid bodies in AVALightWave (app).** An object's panel has a Rigid Body section. `set_rigid_body`,
   `simulate` and `bake_simulation` (rigid bodies to ordinary keys, as one undo step) are tools. The domino recipe
   gets a simulated version.
3. **Expressions (engine, then app).** A motion modifier `expression` sets a channel from a formula:
   - over the frame, the time, and other items' channels (`[Wheel.position.x] / 0.3 * 57.3`);
   - using the usual maths functions;
   - evaluated in a sandbox that can only read channels.

   It's on `add_motion_modifier`, and in the panel.

## Milestone C: characters

**Status:** done. Lip sync is Core's `LipSync` (loudness against the voice's own loudest twentieth, a quick open and a
slower close, Douglas–Peucker thinning; brightness from zero crossings). The walk solves each leg exactly as two bones and keys it, turning the shins'
full-time IK off, since the engine's general solver wobbles at full stretch; action clips in Core's `ActionClips`, kept in `avalightwave.actions`; the engine's `SceneAudio` and MP4 sound, AVA Modeler fb28510 and d90fb7d; WAV reading,
the waveform and OpenAL playback in AVALightWave, behind `ISoundOutput` so tests stay silent).

1. **Audio track (engine scene data, app UI).** A scene can have one sound file (WAV). It is drawn as a waveform
   under the dope sheet and the graph editor, and plays while scrubbing and previewing. render_animation's movie is
   joined with it by ffmpeg. The `set_audio` tool sets it.
2. **Action clips (app).** An action is a named clip of keys over many items or bones. It can be saved from a span,
   kept in the scene and in `.action` files, placed at a frame, scaled, looped and blended with the clip before
   over a number of frames. The tools are `save_action`, `place_action` and `list_actions`.
3. **Walk cycles (app).** `walk` makes a rig walk along a path or a list of points:
   - the stride, the step height and the speed are given;
   - the feet are planted on the ground (any object below), through IK goals stepping in turn;
   - the hips bob and sway, and the body turns along the way.

   It works on a rig made by `make_character` (a simple jointed figure with IK legs and arms), or on any rig whose
   leg chains are named.
4. **Lip sync (app).** From the audio track's loudness, a mouth morph (or a jaw bone) is keyed open and shut. The
   keys are thinned to readable shapes, with an optional second morph for wide vowels by pitch brightness. The tool is
   `lip_sync`. It is honest about being loudness-driven, not phoneme-recognised.

## Milestone D: rendering

**Status:** done. Light linking is AVA Modeler e1406ed; passes and the denoiser 9a4a3d5 (the denoiser edge-stopping on
each pixel's local noise; the views still light everything). Scatter is AVA Modeler 817865d: the copies are real
geometry merged into the object's frame (`Scene.Grow`, as particle instances are), not instances with a BVH of their
own, so they are capped by the instance face budget (`ParticleLooks.MaxInstanceFaces`); true instancing is left for
later. The sun sky is `builtin:sun` (0c1a9fe), an analytic model, not Preetham's or Hosek's; `make_terrain` and
`set_sky` are Core's `Terrain` and `SunPosition` with `Mcp/WorldTools.cs`. The sky doesn't change over time.

1. **Light linking (engine).** A light can light only listed objects, or every object but listed ones, in the ray
   tracer, shadow maps and the views. It is in `set_item` and the light panel.
2. **Render passes (engine).** Besides the beauty pass, the ray tracer can output depth, normal, object ID, albedo,
   shadow, reflection and emission buffers. render_animation saves the passes asked for as PNGs beside the frames
   (depth normalised, IDs as flat colours).
3. **Denoiser (engine).** An edge-aware filter guided by the normal, albedo and depth passes, so few samples give
   clean frames. It is a render setting on render_scene, render_animation and the farm.
4. **Instancing and scatter (engine, then app).** An object can scatter copies of another over its surface: count,
   seed, scale and rotation jitter, aligned to the surface or upright, and a density map from a texture. They render
   as instances (one mesh, many transforms), with a BVH over instances, so thousands are cheap. The tool is
   `set_scatter`.
5. **Terrain and sky (app, engine for the sky).**
   - `make_terrain` makes a landscape from layered noise: size, height, roughness, seed, erosion-like smoothing, and
     surfaces by height and slope (grass, rock, snow).
   - A physical-style sky follows a sun position, set by heading and elevation or by time of day and latitude. Its
     colours come from the sun's height, and it can drive a distant light's direction and colour.
   - The tool is `set_sky`.

## Milestone E: the live render view

**Status:** done. Core's `ProgressiveRender` renders on a below-normal-priority thread with all but one core, a quarter
size and a half size with one ray, then full size with 1, 4, 16… rays up to the pane's `RenderSamples` (16), denoised;
the pane (`ViewportPane.ShowRender`, its view list's Render, F8) shows it letterboxed with its counter. It starts again
when the scene's version, the frame, the camera or the pane's size has changed and then held still for 200 ms; only the
latest start's passes are shown. The view beneath stays a Camera view and takes the mouse. Whether a pane renders isn't
remembered between runs.

1. **Progressive render pane (app).** Any view pane can show Render: the camera's view ray traced progressively, a
   coarse pass first, then refining to the full sample count. It starts again when the scene changes (the scene's
   Version) and runs on a worker at a lower priority, stopping when the pane goes back to a normal view. It has a
   denoised preview and a frame/sample counter.

## Not in these milestones

- A GPU path tracer.
- Fluid–rigid coupling.
- Phoneme-accurate lip sync.
- Motion capture import.
