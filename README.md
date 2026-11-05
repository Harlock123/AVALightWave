# AVALightWave

[![Build](https://github.com/Harlock123/AVALightWave/actions/workflows/build.yml/badge.svg)](https://github.com/Harlock123/AVALightWave/actions/workflows/build.yml)

<img src="src/AVALightWave/Assets/AVALightWave.png" alt="AVALightWave's icon: a spotlight's beam on a wave above a floor grid" width="96" align="right">

A cross-platform **re-imagining of classic LightWave Layout** (versions 4 and 5), the scene, animation and render
program that shipped beside LightWave Modeler, built with .NET 10 and Avalonia. It's **inspired by** the original's
way of working (the Layout view with its edit modes and mouse modes, Selected Item, the row of panels across the top,
numeric requesters, the timeline and its keys, Replace Object, and a hub joining it to the modeler) and stands on the
engine of [AVA Modeler](https://github.com/Harlock123/AVAModeler), its companion modeler, which this repository
carries as a git submodule: the same scene model, TCB animation, bones with inverse kinematics, paths, deformation,
LightWave scene and object files, ray tracer, movie output and AI tools, in a program of its own.

Classic LightWave was two programs from two authors: Allen Hastings's Layout (which grew from his VideoScape 3D) and
Stuart Ferguson's Modeler (from his Modeler 3D), linked by a hub. AVA Modeler recreates the modeler, with a Layout
workspace of its own; AVALightWave is the standalone Layout, and the hub joins the two again.

![AVALightWave with the sample showcase scene: the Objects panel, the bouncing ball chosen, its keys on the timeline and its height in the graph editor](Images/AVALightWave-layout.png)

## Features

### A Layout of its own

| Classic Layout 4/5 | AVALightWave |
|---|---|
| The Layout view: Camera, Perspective, Top, Front (LightWave's Back), Side and Light views | The same six (View menu, the view list, keys 1 to 6); the camera view with its frame and safe areas; the light view looks through the chosen light (a spot's cone wide), for aiming |
| Edit: Objects, Bones, Lights, Cameras | The Edit buttons (Shift+O, B, L, C): the view picks only that kind (nulls count as objects), and choosing an item of another kind anywhere switches to its mode |
| Selected Item pop-up, arrow keys | Selected Item lists the mode's items; ↑ ↓ step through them |
| Mouse: Move, Rotate, Size, Stretch, with X, Y, Z | The Mouse buttons (T, Y, H, Shift+H) and the X, Y, Z buttons limiting the axes (heading, pitch, bank when rotating); the right button works the third axis (depth, bank, Z) |
| Numeric requester | Numeric (N): position, rotation and scale at the frame, typed, one undo step |
| Create Key, Delete Key, Auto Key | The same (Enter, Delete), keyed by the engine's one keying rule |
| The frame slider and play controls | The timeline with the chosen item's key marks, a frame box, first, back, play, on, last (Home, ←, Space, →, End) |
| Scene, Objects, Bones, Lights, Camera, Surfaces, Images, Effects, Record, Options panels | Scene, Objects, Bones, Lights, Camera, Surfaces, Effects, Render and Options, re-imagined as panels docked on the right (the buttons across the top), not modal requesters |
| Scene Overview (later the Scene Editor) | The Scene Editor (E): the hierarchy, an eye to show or hide each item, a lock to keep the mouse off it, and its keys across the frames |
| Motion graph | The engine's graph editor (G) with TCB keys, and its dope sheet (D) |
| Load Scene, Save Scene, Save All Objects | .lws (LightWave 4/5 and later) and .avascene (everything, meshes inside); saving an .lws first saves any object with no file of its own, as LWOB, in an Objects folder beside it |
| Load Object, Replace Object | Load as many objects as you like (LWOB, LWO2, OBJ, GEO, glTF/GLB, STL); Replace Object swaps an item's geometry for another file's, keeping its motion, parent and bones |
| Parenting, pivots, nulls | In each item's settings; nulls from the Items menu or the Objects panel |
| Bones, IK, Skelegons | The engine's bones (Add Bone, Skelegons → Bones), full-time IK with goals, limits, locks and anchors |
| Lights, Camera | Distant, point, spot and area lights with soft shadows; zoom, resolution presets, depth of field and motion blur |
| Rendering | The engine's ray tracer (F9) with ambient occlusion, environment lighting, depth of field and motion blur; Render Animation (F10) to PNG frames and, with ffmpeg, MP4 and GIF; glTF export |
| The hub | A live link with AVA Modeler (below) |

Every change to the scene (a drag, a value typed, a key, a panel setting, a surface, Replace Object, an update from
AVA Modeler, an AI agent's call) is one undo step: Ctrl+Z, and Ctrl+Shift+Z or Ctrl+Y.

![The light view through the key spot, with the Lights panel and the Scene Editor: the floor locked, the camera's and the ball's keys across the frames](Images/AVALightWave-scene-editor.png)

![The Surfaces panel changing the ball's colour, the camera view and the dope sheet](Images/AVALightWave-surfaces.png)

![A ray-traced frame of the showcase scene](Images/AVALightWave-render.png)

### The hub: AVALightWave and AVA Modeler together

As the original package's hub did, AVALightWave and AVA Modeler link up while both are running, over a local pipe that
only your own programs can open (a Unix socket on Linux and macOS, a named pipe on Windows; no network). It's on in
both unless you turn it off (Options › Link with AVA Modeler here; Edit › Preferences in AVA Modeler).

- **Edit in Modeler** (Ctrl+E, the Hub menu, the Objects panel): the chosen object opens in AVA Modeler. If AVA Modeler
  isn't running, it's started with the object, found beside AVALightWave, in a sibling folder, on the PATH, through
  `AVAMODELER_PATH`, or where Options says. An object with unsaved changes (or no file) goes as a copy, in the `hub`
  folder of AVALightWave's settings (readable only by you). AVA Modeler won't open it over unsaved work of its own,
  and says so.
- **Saving it in AVA Modeler** (Ctrl+S) updates it here at once, keeping its motion, parent and bones, as one undo step
  ("Update pawn from AVA Modeler"). A link ties the two, so even a Save As under a new name finds its way back; a save
  of any file the scene uses updates its objects too.
- **Send to AVALightWave** (AVA Modeler's File menu) puts AVA Modeler's object in this scene, or updates the one it
  was sent as before (starting AVALightWave with it if need be).
- Either program works alone: when the other isn't there, nothing waits more than a moment.

### AI agents and the AI Chat

AVALightWave carries AVA Modeler's MCP server and AI Chat in a **Layout-only** configuration: the scene, objects,
lights, cameras, keys, bones and render tools, and none of the modeling ones (an object can go to AVA Modeler with
`edit_in_modeler`, where AVA Modeler's own server models). Agents see the same scene the person does, and every call
they make is one undo step.

- **The AI Chat** (F4, AI › Chat with Claude): the engine's chat panel, here staging, lighting and animating. It talks
  to Claude through your own Anthropic API key, or to a local or self-hosted model through an OpenAI-compatible API,
  with exactly AVA Modeler's rules: the key comes from `ANTHROPIC_API_KEY` or an owner-only `anthropic-api-key` file
  beside AVALightWave's `preferences.json` (a gateway's key only from its own file), is never shown beyond a mask, logged
  or saved in a chat; chat history is off until you turn it on, and then kept owner-only in `chats/`.
- **Outside agents**: with Options › Allow AI agents on, `AVALightWave --mcp --attach` connects an agent (Claude Code,
  say) to the open window through AVALightWave's own local pipe; `AVALightWave --mcp` serves a headless scene of its
  own.

![The AI Chat staging a scene: loading the pawn and the crate, a warm key spot, the camera, and a ray-traced check](Images/AVALightWave-chat.png)

| Tools | |
|---|---|
| The engine's scene tools | `get_scene`, `get_keys`, `new_scene`, `open_scene`, `save_scene`, `export_scene`, `add_object` (a file), `add_light`, `add_camera`, `add_null`, `add_bone`, `remove_item`, `set_item`, `set_transform`, `look_at`, `set_key`, `delete_key`, `set_end_behavior`, `set_follow_path`, `bake_path`, `shift_keys`, `scale_keys`, `set_scene`, `undo_scene`, `redo_scene`, `render_scene`, `render_scene_view`, `render_animation`, `make_movie`, `set_morph`, `remove_morph`, `add_displacement`, `set_displacement`, `remove_displacement`, `skelegons_to_bones`, `set_ik`, `bake_ik` |
| AVALightWave's own | `replace_object` (Replace Object, one undo step), `save_object` (an object to a file in the working folder), `edit_in_modeler` (the hub; in the open window), `hub_status` |

Recipes (MCP prompts), step-by-step plans with the exact calls: `stage_object` (load, light from three sides, frame,
render), `object_turntable` (a null turning once, rendered to frames and a movie), `camera_flyby` (the camera on a
path through nulls, keyed to keep the object in view), `light_sweep` (a spot fading in, turning across and out), and
the engine's `three_point_lighting`.

## Download and run

Ready-to-run builds for Windows, macOS and Linux, on Intel/AMD and ARM, are on the
[Releases page](https://github.com/Harlock123/AVALightWave/releases/tag/latest). **Latest build** is rebuilt on every
push to `main`. Each download is one self-contained executable: no .NET install is needed.

```bash
tar -xzf AVALightWave-linux-x64.tar.gz     # or linux-arm64, linux-arm, osx-arm64, osx-x64
./AVALightWave                             # or ./AVALightWave showcase.lws, or an object file
```

On Windows, unzip and run `AVALightWave.exe`. On macOS, if Gatekeeper refuses to open it, run
`xattr -d com.apple.quarantine AVALightWave` once. Put AVA Modeler beside it (or say where it is in Options) for Edit
in Modeler to start it.

## Using it

| | Mouse | Keys |
|---|---|---|
| Choose | Click an item in the view (of the edit mode), a name in a panel's list or the Scene Editor | Shift+O, Shift+B, Shift+L, Shift+C the edit mode; ↑ ↓ the next item |
| Move it | Drag in the view; the right button works the third axis | T Move, Y Rotate, H Size, Shift+H Stretch; N Numeric |
| Animate | The timeline; the graph editor; the dope sheet | Enter Create Key, Delete Delete Key; ← → a frame, Home, End; Space play |
| Look | Wheel zooms; middle-drag (or Alt-drag in a flat view) pans; Alt-drag turns the perspective view | 1 to 6 the views, A fit; G graph editor, D dope sheet, E Scene Editor |
| Files | File menu, Recent Scenes and Recent Objects | Ctrl+N, Ctrl+O, Ctrl+S, Ctrl+Shift+S, Ctrl+Q |
| Elsewhere | | Ctrl+E Edit in Modeler, F4 AI Chat, F9 render, F10 animation; Ctrl+Z undo, Ctrl+Shift+Z or Ctrl+Y redo |

`samples/` has objects (a pawn, a crate, a ball, a floor and the engine's spaceship) and a showcase scene as .lws and
.avascene; `dotnet run samples/make_samples.cs` makes them again with the engine's tools.

## Building and testing

The engine is a submodule, so clone with it:

```bash
git clone --recursive https://github.com/Harlock123/AVALightWave.git    # or: git submodule update --init --recursive
dotnet build AVALightWave.slnx
dotnet run --project src/AVALightWave
dotnet test AVALightWave.slnx
```

It needs the .NET 10 SDK. AVALightWave's own tests (the engine's are in its repository):

| Tests | Count | What they cover |
|---|---|---|
| Core | 21 | The scene document: a new scene; objects of every format, several to a scene; .lws saved with an object that had no file written beside it, and loaded back with its motion and parent; .avascene keeping hidden and locked items with the meshes inside; a missing object's placeholder; the sample scenes; Replace Object keeping motion and bones, with undo, and keeping a chosen name; surfaces changed as undo steps and saved; Save Object by extension; hiding, locking and parenting as steps. Edit modes and stepping through items; the Scene Editor's hierarchy (bones under their object), flags and keys. Settings saving only where loaded from. The hub both ways with a stand-in AVA Modeler on pipes of the tests' own: Edit in Modeler by file and by a copy, a refusal, starting AVA Modeler (faked) when it isn't there, a save updating the linked object (under a new name) keeping its motion as one undo step, a save with no link updating every object from that file, Send to AVALightWave adding then updating, hello, refusals and a second copy on the same pipe |
| Mcp | 13 | The Layout-only server: exactly the scene tools and its own, no modeling tool, its instructions, descriptions and recipes; add_object needing a file; replace_object (motion kept, undo, its mistakes changing nothing); save_object inside the working folder (overwrite, formats) and an .lws referring to it; the hub tools headless and with a window; `AVALightWave --mcp` headless and its help; the chat's prompt and core tools; every recipe followed call by call through the server, its last picture not blank and its result checked |
| UI | 19 | The window headless: its start, edit modes and what the view picks, keys (frames, items, mouse modes, views, Enter, Ctrl+Z), axes, the numeric requester, every panel (a spot added, frames and resolution typed, each an undo step), the Bones panel adding bones to the current object, the Options panel joining the hub and letting agents in, the Surfaces panel, the Scene Editor's eye, lock and names, files through the menus' commands (Load Object, Save Scene, Replace Object, recent files, a missing scene), dragging in the view; the hub end to end (Ctrl+E to a stand-in AVA Modeler, its save updating the scene live, Ctrl+Z, Send to AVALightWave); an agent through AVALightWave's own live pipe; the AI Chat against a fake Anthropic API (no real calls); and the README's screenshots |

The screenshots are drawn by the tests in a real (headless) window:

```bash
AVALIGHTWAVE_SHOTS=$PWD/Images dotnet test tests/AVALightWave.UI.Tests --filter Screenshot
```

## How it's built

| Project | What it is |
|---|---|
| `AVALightWave.Core` | The scene document (`SceneDocument`: the engine's `LayoutSession` with scenes, objects, Replace Object, surfaces, hiding, locks and the hub's links), the Scene Editor's outline, edit modes, settings, and AVALightWave's end of the hub (`LightWaveHub`). No UI |
| `AVALightWave.Mcp` | The Layout-only MCP profile of the engine's server, AVALightWave's own tools and recipes, and the chat's prompt. No UI |
| `AVALightWave` | The window, its panels, the Scene Editor, the numeric requester, live agents and the AI Chat |
| `external/AVAModeler` | The engine, as a git submodule: AVA Modeler's Core, Formats, Geometry, Rendering, Scene, Layout, Layout.UI, Mcp, Chat, Chat.UI and Hub projects, referenced as they are (one copy, never forked) |

Reused from the engine: the scene model and its undo, envelopes and keying, bones and IK, paths, morphs and
displacements, LWS/LWO/glTF and every object format, the ray tracer and its effects, animation rendering and movies,
the Layout view (`SceneView`), the item panel (`ItemPropertiesPanel`), the graph editor, the dope sheet, the scene
settings, the render and animation windows, the MCP server and its live pipe, the chat and its panel, and the hub's
wire. New here: the standalone document, the window and its panels, the Scene Editor, the numeric requester, the
Layout-only server with its tools and recipes, and AVALightWave's end of the hub.

## Known limits

- The Layout view is drawn in software, as the engine's is. Images (Layout's Images panel) and Record (Layout's
  record-to-device) have no panels; textures are edited in AVA Modeler (Edit in Modeler), and the Surfaces panel sets
  the rest of a surface.
- There's no Clone Item yet, no motion-file loading or saving, no scene merging (Load from Scene), and no
  auto-save or crash recovery of the scene.
- The hub carries files, not live geometry: AVA Modeler's changes arrive when it saves or sends, not stroke by stroke.
  An object handed to AVA Modeler without a file lives in AVALightWave's hub folder until you save it elsewhere.
- Locks (`.avascene` only) keep the mouse off an item, not its numbers, keys or agents. `.lws` keeps neither locks nor
  hidden items; the engine's own limits (README of AVA Modeler) apply to scene files, rendering and glTF.

## About and licence

AVALightWave © 2026 Lonnie Watson, under the [MIT License](LICENSE).

LightWave is a trademark of its current owner, LightWave Digital. AVALightWave is an independent project, a
re-imagining inspired by classic LightWave Layout; it isn't affiliated with or endorsed by NewTek or LightWave Digital,
and uses none of their logos, artwork or interface art (its icon is its own, drawn by `tools/make_icon.py`). It reads
and writes the classic object and scene formats for compatibility. It's built on AVA Modeler (MIT), Avalonia,
SkiaSharp and the official MCP and Anthropic C# SDKs.
