# Changelog

All notable changes to AVALightWave are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

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
