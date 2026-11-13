# Changelog

All notable changes to AVALightWave are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- **The engine** is AVA Modeler with its 3D printing slicer. STL files are now read as millimetres, as slicers and
  other programs write them, so an STL object loads at its real size in metres (it used to load 1000 times too big);
  STL saved from here is written in millimetres too.

### Added

- **A sound track**: the Scene panel loads a WAV file; its waveform shows under the timeline, it plays with the
  timeline, scrubbing and the preview, and MP4 movies carry it (from the engine). Agents have `set_audio`.
- **Expressions** (from the engine): Motion Options' Expression and `add_motion_modifier` kind `expression` set a
  channel from a formula of the frame, time and other items' channels, e.g. a wheel turning as its car rolls.
- **Rigid-body physics** (from the engine): objects fall, bounce, slide, tumble and stack, set in the Objects panel or
  with `set_rigid_body`; `simulate` reports it, Bake Simulation and `bake_simulation` turn it into keys; a
  `domino_physics` recipe topples a curving row.
- **Style presets** for agents: `apply_style` (and a plan's `style`) gives a scene a look: noir, product commercial,
  cartoon, golden hour or sci-fi, with their own light rigs, surface treatment, lens and easing.
- **Shot lists** for agents: `build_shots` lays out a cut of shots, each through its own camera, and
  `render_shots` renders them in order as one sequence (and a movie).
- **`review_shot`** for agents: the shot's check and a contact sheet with numbered frames in one call, so an agent
  looks at its work before reporting.
- **Exact shot checks**: `check_shot` now looks at objects' real (deformed) shapes rather than their boxes: what's
  in the picture, how low an object goes over the ground, and whether one passes into another.
- **Turntable and Product Shot wizards** (Render menu): an object on a turntable, or on a pedestal under studio
  light with the camera pushing in, built in one undo step with no agent.
- **Package Scene**: File › Package Scene (or as Zip) gathers the scene, its objects' files and every image it uses
  into one folder that opens on another machine; agents have `package_scene`.
- **Camera bookmarks**: the Camera panel saves the camera's view under a name and puts it back with a press (keyed
  when the camera is animated); they're kept in .avascene scenes, and agents have `camera_bookmark`. From AVA
  Modeler's engine, `Scene.Extras` keeps them with the scene.
- **Agents make objects in the scene** (the first stage of `AI_SCENES.md`, scenes from a description). `make_object`
  makes a box, ball, cylinder, cone, disc, torus, ground, steps or extruded text with a surface of its own, standing on
  its base; `build_object` builds anything else from the engine's modeling calls (AVA Modeler's tools, as steps, on an
  object of its own); `edit_object` changes an object in the scene the same way, keeping its motion;
  `set_object_surface` and `get_object_surfaces` change and list its looks; `list_modeling_tools` and
  `describe_modeling_tool` say what the steps can use. Each is one undo step, and a failing step changes nothing. The
  AI Chat can make objects too.
- **Motion in a phrase for agents** (stage 2): `animate` turns a list of moves (move, turn, grow, bounce, spin, orbit,
  sway, bob, shake, pop in and out, fade in and out, through points, track, dolly, truck, crane, push in, follow) into
  ordinary keys, with eases and moves that follow or run with each other, all as one undo step; `camera_move` gives
  the camera a shot that keeps its target in view, or a handheld wobble.
- **Whole shots for agents** (stage 3): `build_scene` builds a shot from one plan (settings, objects, a lights rig,
  the camera, motion and camera moves) as one undo step, all or nothing; `frame_items` fits items in the camera's
  picture; `check_shot` lists what to fix before rendering, without rendering.
- **Six recipes for scenes made from nothing** (stage 4): a bouncing ball, a solar system, a product reveal, falling
  dominoes, a rocket launch with smoke and a row of dancing shapes, each one build_scene plan, checked and rendered.
  The README says how to add AVALightWave to Claude Code.
- **What the AI Chat uses**, shown as it's used (from the engine): a line under the input with the chat's tokens and
  estimated cost, updated while a request runs; a tag after each turn with its requests, tokens, pictures and
  estimate; and in Setup… the prices (changeable), today's and this month's totals with Reset, and a budget that
  stops a chat between requests. The totals are kept in `chat-usage.json` in AVALightWave's settings folder.
- **Fluids, combing by hand, cloth that keeps off itself, smoke in reflections and shadows** from the engine.
  Particles can flow as a liquid, pouring, splashing and filling a tank or bowl, drawn as one surface with the Blob
  look. Hair can be combed with the mouse (Comb with the mouse in an object's panel turns dragging in a view into a
  brush) and swings with the bones that move it as well as the object. Cloth can be kept from passing through
  itself. Puffs of smoke show in reflections and refraction, cast soft shadows and shade each other. Particles, hair
  and soft bodies are now kept in `.lws` scenes too, as plugin sections LightWave skips. Agents get `comb_hair` and
  the new `fluid`, `viscosity`, `self_collide` and `self_distance` options.
- **Particle looks and collisions, hair swing, cloth** from the engine. Particles can be blobs that melt
  together, puffs of smoke, glowing sprites or copies of another object, and can stick to, bounce off or die on
  other objects as they move. Hair can be combed and swing as its object moves. An object's panel can make it
  cloth or a soft body: it falls, drapes over other objects, can be pinned by a surface and stops on a floor.
  Agents get `set_soft_body` and the new `set_particles` and `set_hair` options.
- **Particles and hair** from the engine. An object's panel can emit particles (from its faces, points or pivot,
  between two frames, falling under gravity with drag and stopping on a floor) and grow hair (tapering strands with
  a density, length, droop and frizz that follow bones and morphs). Agents set them with `set_particles` and
  `set_hair`. Both render and save in `.avascene` files, not `.lws`.
- **3MF objects** open (and save) through the engine: each object a layer, each base material a surface, in its
  unit. AVA Modeler's slicer work (two extruders, the prime tower, ironing, adaptive layers) is in the engine too,
  though AVALightWave doesn't print.

### Fixed

- **OpenGL views** are sized from the framebuffer OpenGL draws into, not from the pane's size times the screen's
  scaling, which could disagree: at 2× on Wayland the Perspective and Camera views drew shrunk into their bottom-left
  corner. Turning OpenGL on says the framebuffer's size in the status line.

## [0.2.0] - 2026-11-05

Everyday Layout tools, rendering and animation features, a render farm, live sync with AVA Modeler, OpenGL views and
Lua macros, on a newer engine (AVA Modeler with key spans, motion modifiers, light effects, dissolve and compositing).

### Added

- **Clone Item**: copies of an object (with its bones), light, camera or null with its motion, each moved, turned,
  sized and delayed in time from the one before, as one undo step.
- **Load from Scene**: another .lws or .avascene's items, with their hierarchy and motion, into the open scene, names
  already taken renamed.
- **Motion files**: Load and Save Motion (LightWave .mot, versions 1 and 3), carrying one item's motion onto another.
- **Auto-save and crash recovery** of the scene, offered back at the next start.
- **Make Preview** (Shift+P): a wireframe or shaded flipbook drawn as the view draws it, played at the scene's rate
  with loop and stepping, saved as PNGs, MP4 or GIF.
- **The Images panel**: pictures and numbered sequences with frame offsets and looping, for compositing and textures.
- **Light effects** in the Lights panel: lens flares (glow, ring, streaks, fades, keyable intensity), volumetric beams
  for spot and point lights, and shadow maps (size, fuzziness) as a faster alternative to ray-traced soft shadows.
- **Object dissolve**, keyable, in the views (screen-door), previews and renders.
- **Compositing** in the Effects panel: a background image, and a foreground keyed by a colour range or alpha.
- **Batch and network rendering**: the Render Queue, and a render farm in a shared folder worked by headless render
  nodes (`AVALightWave --render-node <folder>`), with no network listener.
- **Skelegons → Bones** in one step from the Items menu.
- **A fuller graph editor** (the engine's): every curve, keys chosen across curves, per-key TCB and spans (TCB,
  linear, stepped, Bezier), before and after behaviours, copy, paste and scaling time.
- **Motion modifiers** (Items › Motion Options): lag, jitter, cycle and oscillate.
- **Morph envelopes**: morph targets from object files or other objects, their weights keyed.
- **Live sync with AVA Modeler**: its changes to a linked object arrive as you model (one undo step a burst), and a
  hub light shows AVA Modeler's state, the linked object and the last update.
- **View layouts** (Ctrl+1 to Ctrl+4): one, two side by side, two stacked or four panes, each with its own view; the
  views drawn with OpenGL, falling back to software.
- **Lua scene macros** on AVA Modeler's macro engine and sandbox: the Macros menu and a macro folder.
- **AI**: 13 more tools (AVALightWave's clone_item, load_from_scene, add_morph_target, load_motion, save_motion,
  make_preview, queue_render, render_queue_status and run_macro; the engine's motion modifier, light effect, dissolve,
  image and compositing tools), 60 in all, and seven more recipes (render_on_farm, clone_stagger, light_beams,
  composite_shot, rig_from_skelegons, follow_and_jitter, morph_and_macro), 12 in all.
- Samples: an arm with skelegons, the pawn squashed as its morph target, and images for compositing and sequences.

### Changed

- The middle of the window is a view layout; 1 to 6 and the view list act on the active pane, and A frames every pane.
- The engine (external/AVAModeler) moves to its newer commit.

## [0.1.0] - 2026-11-05

The first release: a standalone re-imagining of classic LightWave Layout (versions 4 and 5), built on AVA Modeler's
engine (a git submodule, `external/AVAModeler`).

### Added

- **The window**, in the classic Layout spirit: the Camera, Perspective, Top, Front, Side and Light views; the Edit
  modes (Objects, Bones, Lights, Cameras) choosing what the view picks; Selected Item; the Mouse modes (Move, Rotate,
  Size, Stretch) with X, Y and Z; Numeric (N); Create Key, Delete Key and Auto Key; the timeline and transport; the
  graph editor, the dope sheet and the Scene Editor under the view; and the Scene, Objects, Bones, Lights, Camera,
  Surfaces, Effects, Render and Options panels.
- **Its own scene document**: New, Load and Save Scene (.lws, saving objects without a file beside it; .avascene),
  Load Object (LWOB, LWO2, OBJ, GEO, glTF/GLB, STL) several to a scene, Replace Object, Save Object, Save All Objects,
  parenting, hiding and locking items, surfaces, recent scenes and objects, and undo for every change to the scene.
- **The Scene Editor**: the items' hierarchy, an eye and a lock for each, and their keys across the frames.
- **The hub**: Edit in Modeler (Ctrl+E) opens an object in AVA Modeler (starting it if need be), and its saves update
  the object here, keeping its motion; AVA Modeler's Send to AVALightWave adds or updates an object here. A per-user
  local pipe, no network; either program works alone.
- **AI**: AVA Modeler's MCP server in a Layout-only configuration (the scene tools, plus `replace_object`,
  `save_object`, `edit_in_modeler` and `hub_status`), live in the open window (`AVALightWave --mcp --attach`) or
  headless (`AVALightWave --mcp`); the AI Chat panel (F4) with AVA Modeler's rules on keys and history; recipes for
  scene work (`stage_object`, `object_turntable`, `camera_flyby`, `light_sweep`, and the engine's
  `three_point_lighting`).
- Sample objects and a showcase scene (`samples/`, made by `samples/make_samples.cs`), an original icon
  (`tools/make_icon.py`), and builds for Windows, macOS and Linux on Intel/AMD and ARM.

[0.2.0]: https://github.com/Harlock123/AVALightWave/releases/tag/v0.2.0
[0.1.0]: https://github.com/Harlock123/AVALightWave/releases/tag/v0.1.0
