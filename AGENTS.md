# AGENTS.md

Notes for anyone, human or AI agent, changing this code. `README.md` covers the features, the keys, the tests and the
known limits; this file covers how the code fits together and the rules that keep it working.

## What this is

AVALightWave is a standalone re-imagining of classic LightWave Layout (versions 4 and 5): the scene, animation and
render program, in a window of its own, with its own scene document. It's C# on .NET 10 and Avalonia 12, built on the
engine of AVA Modeler, which is a git submodule at `external/AVAModeler` (https://github.com/Harlock123/AVAModeler,
branch `main`). AVA Modeler keeps its own Layout workspace (F2); AVALightWave is the program that classic Layout was,
joined to AVA Modeler by the hub as the original's two programs were.

## Commands

```bash
git submodule update --init --recursive           # the engine (after a plain clone)
dotnet build AVALightWave.slnx                    # the SDK version is in global.json (10.0.400 or later)
dotnet test AVALightWave.slnx                     # every test runs headless: no window opens
dotnet run --project src/AVALightWave             # run it; a scene or object file as the argument opens it
dotnet src/AVALightWave/bin/Debug/net10.0/AVALightWave.dll --mcp   # the Layout-only MCP server on stdio, no window
AVALIGHTWAVE_SHOTS=$PWD/Images dotnet test tests/AVALightWave.UI.Tests --filter Screenshot   # refresh the README's pictures
dotnet src/AVALightWave/bin/Debug/net10.0/AVALightWave.dll --render-node FOLDER [--once]   # a headless render node
dotnet run samples/make_samples.cs                # makes the sample objects, images and scenes again (from the repository root)
python3 tools/make_icon.py                        # redraws the icon
```

`Directory.Build.props` sets `TreatWarningsAsErrors`, nullable reference types, implicit usings and
`InvariantGlobalization` for AVALightWave's projects (the engine's have their own, the same): keep warnings at zero.

## Layout

- `external/AVAModeler/`: the engine, as a submodule, referenced project by project (`ProjectReference`): Layout
  (`LayoutSession`, the scene's state and undo), Formats, Scene, Rendering, Layout.UI (`SceneView`,
  `ItemPropertiesPanel`, `GraphEditor`, `DopeSheetPanel`, `SceneSettingsPanel`, `RenderWindow`, `AnimationWindow`),
  Modeler.UI (`Look`, `Dialogs`, `Preferences`), Mcp (the server and `ServerProfile`), Chat and Chat.UI (the AI Chat and
  its panel) and Hub (the wire). **One copy of the engine: never copy or fork its source here.** Where AVALightWave
  needs something the engine doesn't offer, add it to AVA Modeler as a small, additive change that keeps AVA Modeler's
  behaviour and tests (commit it there, then point the submodule at it).
- `src/AVALightWave.Core/`: no UI.
  - `SceneDocument.cs`: the document. It wraps a `LayoutSession` and adds scenes (`New`, `Load`, `Save`: .lws saves
    objects without a file first, into `Objects/` beside the scene), objects (`LoadObject`, `ReplaceObject`,
    `ReplaceDocument`, `SaveObject`, `SaveAllObjects`, `UnsavedObjects`), surfaces (`EditSurface`: the object gets a
    changed copy of its document, so the scene's undo puts the old one back), items (`SetVisible`, `SetLocked`,
    `SetParent`), Clone Item and Load from Scene (`Clone`, `LoadFromScene`) and the hub's links (`LinkOf`, `Link`,
    `Linked`, `UpdateFromFile`, and `SyncFromFile` for live updates, merged into one undo step).
  - `Cloning.cs` (Clone Item: `CloneOptions`, `Cloning.Clone`, `UniqueName`) and `SceneMerge.cs` (Load from Scene:
    `MergeOptions`, `NameClash`, `SceneMerge.Load`), both on the engine's `Scene.CopyItems`; `SceneAutosave.cs` (the
    lock file, the auto-save written from a `Scene.Copy` on a worker, `Waiting`, `Recover`, `Finish`; the folder is the
    app-data folder's `autosave`, tests give their own); `MorphTargets.cs` (morph targets from files and objects, weights
    by the keying rule); `LayoutMacros.cs` (Lua scene macros on the engine's `LuaSandbox`: the API, one undo step a
    macro, a path guard agents use); `Preview.cs` (Make Preview: `PreviewSettings`, `Preview`, drawn with the engine's
    `SoftwareRenderer` and `SceneDrawing` on a `Scene.Copy` on a worker, under a 512 MB cap, saved through the engine's
    PNG writer, `AnimationRender.FileName` and `MovieEncoder`); `ViewportLayouts.cs` (layout kinds, panes, default views,
    and what `LightWaveOptions.ViewLayout` and `LayoutViews` remember; `ViewMode` is the Single layout's view).
  - `RenderFarm.cs` (the farm's folder protocol: `FarmJobSpec`, `FarmSubmit`, `FarmFolder` with Submit, Scan, Status,
    Nodes, Cancel, Remove, RemoveFinished, Move, NodeCommand and DefaultQueue, and `FarmFiles`: atomic writes, create-new
    claims, bounded reads, `RealPath`, `IsInside`) and `FarmNode.cs` (`FarmNode`, a node's loop; `FarmJobLoader`, which
    loads a job's scene reading only inside its folder; `FarmCommandLine`, `--render-node`; `RenderQueue`, the window's
    in-process node).
  - `EditMode.cs` (Objects, Bones, Lights, Cameras: `Includes`, `Of`, `ItemsOf`, `Step`), `SceneOutline.cs` (the Scene
    Editor's rows: hierarchy, bones under their object, flags, keys), `LightWaveOptions.cs` (AVALightWave's own
    settings in `lightwave.json`; the engine's `Preferences` type holds the shared ones in `preferences.json`, both in
    the `AVALightWave` app-data folder) and `LightWaveHub.cs` (AVALightWave's end of the hub).
- `src/AVALightWave.Mcp/`: no UI. `LightWaveServer.cs` is the profile (`ToolGroups.Scene`, descriptions adjusted, the
  instructions, the live pipe `AVALightWave-mcp-<user>`), `LightWaveTools.cs` its own tools, `LightWaveRecipes.cs` its
  recipes (the engine's `Recipes.Recipe` builder), `LightWaveChat.cs` the chat's prompt and core tools. Scenes from a
  description (`AI_SCENES.md`): `EngineCalls.cs` (`InlineHost`, which runs host work at once inside another tool's
  work, and `EngineCaller`, which calls the engine's or AVALightWave's tools in process by MCP name with JSON
  arguments), `MakingTools.cs` (make_object, build_object, edit_object, surfaces: the engine's modeling calls on
  documents of their own), `Motion.cs` and `AnimateTools.cs` (animate's moves as keys, camera_move), `Shot.cs` and
  `ShotTools.cs` (build_scene, frame_items, check_shot), `SceneRecipes.cs` (the recipes made from nothing).
- `src/AVALightWave/`: the window. `MainWindow.cs` (the layout, modes, panels, keys, files, hub, agents, auto-save,
  macros and OpenGL), `Panels.cs` (the panels; Objects, Bones, Lights and Camera are one `ItemsPanel` per edit mode
  around the engine's `ItemPropertiesPanel`, with `ItemExtras.cs` adding a light's shadow type, lens flare and
  volumetric beam and an object's dissolve; the Effects panel's compositing), `ImagesPanel.cs`, `ViewportLayout.cs`
  (`ViewportLayout`, a Grid of four kept `ViewportPane`s, each a Panel holding a SceneView, its view list and an
  outline, with the engine's `GlSurface` inserted beneath the view at `ViewportPane.SurfaceIndex` while it uses
  OpenGL), `PreviewWindow.cs`, `RenderQueueWindow.cs`, `Requesters.cs` (Clone Item and Load from Scene),
  `MotionOptions.cs`, `SceneEditor.cs`, `NumericRequester.cs`, `WindowAgents.cs` (the live MCP door and the chat's
  host), `Program.cs` (`--mcp` and `--render-node` before Avalonia starts).
- `tests/`: xUnit v3. `AVALightWave.UI.Tests` uses `Avalonia.Headless.XUnit` with Skia, so it can capture screenshots,
  and links the engine's `FakeClaude`.

## Rules that keep it working

- **The engine stays one copy.** No files from `external/AVAModeler` are copied here. Changes it needs go into AVA
  Modeler (additive, its tests passing, its README and AGENTS.md updated), and the submodule is moved to that commit.
  AVA Modeler must be pushed before AVALightWave's CI can check the commit out.
- **No UI below the window.** The document, the hub and the MCP profile are in Core and Mcp, without Avalonia; the
  window and panels stay thin.
- **Scene changes are undo steps.** Change the scene through `SceneDocument` or the session (`Edit`, `SetVector`,
  `Add`, `Remove`…), never straight on the `Scene`; an object's geometry or surfaces change by putting a new document
  in (`ReplaceDocument`, `EditSurface`), never by editing the one the scene holds, so undo can put it back.
- **The hub is local and per user.** `HubProtocol` pipes are made with `PipeOptions.CurrentUserOnly`; messages name
  files by full path and carry nothing else; hand-over copies go in the owner-only `hub/` folder. Don't add a network
  listener, and keep every call short (the other app may not be there).
- **Agents get the scene, not the Modeler.** The profile serves `ToolGroups.Scene` and AVALightWave's own tools; a new
  engine tool that needs only the scene goes in `ToolGroups.Scene` (in the engine), and AVALightWave serves it with no
  change here. Modeling reaches agents only inside AVALightWave's own tools (build_object, edit_object), run by
  `EngineCaller` on a document of the tool's own, never on the window's. A tool that calls other tools from inside its
  work does it through `EngineCaller` on an `InlineHost` (never `host.RunSceneAsync` again: it would wait on itself),
  so everything is one undo step and a failure anywhere puts it all back. A new
  tool of AVALightWave's goes in `LightWaveTools` (through `host.RunSceneAsync`, one undo step, errors as
  `McpException`), with a test, and in the README's tool table. A recipe goes in `LightWaveRecipes` and in
  `RecipeTests`' cases and checks.
- **The chat never calls a real API in tests, and never shows a key.** AVA Modeler's rules, exactly: a window made for
  tests (`forTests: true`) reads no environment variable and no key file and keeps no history; keys live in owner-only
  files beside `preferences.json`, never in it.
- **Tests never touch the person's settings or programs.** Test windows' settings are made with `new` (they save
  nowhere); hub and agent pipes are the tests' own; the launcher in tests starts nothing.
- **Branding.** AVALightWave is "a re-imagining of classic LightWave Layout", "inspired by" it; no NewTek or
  LightWave Digital logos, artwork, icons or copied interface art. The icon is drawn by `tools/make_icon.py`.
- **`MainWindow.View` is the active pane's view.** Anything that must reach every view (CanPick, DragMode, Axes,
  Shaded, invalidation, fitting, OpenGL) goes through `Viewports.ForEachView`, `Viewports.Panes` or `FitAll`, never
  `View` alone.
- **OpenGL views fall back.** A pane using OpenGL sets its `SceneView.DrawsScene` false and puts the engine's
  `GlSurface` beneath it; `MainWindow.CheckOpenGl` (two seconds after asking) puts software back in any pane whose
  surface never got a context, as in headless tests. Compare the GL and software pictures through the engine's public
  `OffscreenGl` (skipping without a context) in a test class of the `[Collection("OpenGL")]`.
- **Live updates merge.** `SceneDocument.SyncFromFile` applies AVA Modeler's `object_changed` copies with
  `LayoutSession.Edit(..., merge: true)`, keeping the object's own file; `LightWaveHub` answers the message at once and
  does the work on the UI thread afterwards (AVA Modeler waits only a second), and checks AVA Modeler's presence off
  the UI thread.
- **The render farm is a folder, never a listener.** Protocol 1, under `<folder>`:

  ```
  jobs/<id>/            id = yyyyMMdd-HHmmss-<6 hex>; made as jobs/.<id>.partial and renamed into place when complete
    job.json            FarmJobSpec, snake_case, written atomically: protocol, id, name, scene ("scene.avascene"),
                        first, last, step, width, height, samples (1–64), output, movie (none|mp4|gif|both), fps,
                        created, order (queue order; Up/Down swap it), submitter
    scene.avascene      the scene, meshes embedded; no paths of the sender's
    images/…            every image the scene uses, its paths rewritten to "images/<name>"
    claims/<frame>.claim  a node's claim: FileMode.CreateNew, JSON {node, name, host, pid, time}; deleted after done
    out/<output><n>.png the frames, n = 0001… in render order, written to a temporary name and renamed into place
    done/<frame>.done   JSON {node, name, host, file, seconds, time}, written after the PNG is in place
    errors/<frame>.txt  why a frame failed (job.txt: why job.json was refused); any file here fails the job
    cancel              no more claims; the frame under way stops at the next heartbeat and its claim is given back
    movie.claim / movie.done / movie.txt   the movie, made once by the node that finds every frame done
  nodes/<node-id>.json  a node's heartbeat (state, job, frame, counts, seen), every 5 s, deleted when it stops
  ```

  A node looks for a done file before claiming and again after its create-new wins. A claim is taken over only when
  it's older than two minutes and its node's heartbeat is as old or gone: the taker renames it to
  `<frame>.claim.<taker>.stale` (one rename wins), deletes it and claims with create-new. A node reads only inside a
  job's folder (`FarmJobLoader`: only .avascene, every path resolved through links and checked inside, else an error
  file) and runs nothing from it (ffmpeg only through the engine's `MovieEncoder`, on the job's own frames, when the
  job asks). Sending a job copies the scene and never changes the open one (not an undo step). Tests use temporary farm
  folders; a test window's local queue is a temporary folder of its own.
- **Macros run in the engine's sandbox.** Scene macros go through `LuaSandbox` (no bare MoonSharp `Script`), are one
  undo step each, and agents' macros keep their paths in the working folder (`LayoutMacros.Run`'s guard). The person's
  macro folder is `macros` in the app-data folder; a test window has none unless a test gives a temporary one.
- **Tests don't use the PATH's ffmpeg.** The preview window's `PickFile` and `Movies`, and the farm's `Encoder`, are
  test hooks.

## Making a change

- Add or update tests with every change; `dotnet test AVALightWave.slnx` must pass with no warnings (and, for an
  engine change, AVA Modeler's own suite in its repository).
- If the change shows on screen, refresh the screenshots and look at them (no home-folder paths in them).
- Update `README.md` (features, keys, the test table, known limits) and `CHANGELOG.md`.

## CI and releases

`.github/workflows/build.yml` checks out the submodule, builds and tests every push and pull request, then publishes
self-contained single-file builds for win-x64/x86/arm64, linux-x64/arm64/arm and osx-x64/arm64. Every run keeps them as
workflow artifacts; pushes to `main` replace the `latest` pre-release's, and a `v*` tag makes a proper release. After a
release it asks the portfolio site to rebuild, if the `SITE_REFRESH_TOKEN` secret is set.
