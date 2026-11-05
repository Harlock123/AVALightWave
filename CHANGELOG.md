# Changelog

All notable changes to AVALightWave are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

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

[0.1.0]: https://github.com/Harlock123/AVALightWave/releases/tag/v0.1.0
