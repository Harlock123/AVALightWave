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
dotnet run samples/make_samples.cs                # makes the sample objects and scenes again (from the repository root)
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
    `SetParent`) and the hub's links (`LinkOf`, `Link`, `Linked`, `UpdateFromFile`).
  - `EditMode.cs` (Objects, Bones, Lights, Cameras: `Includes`, `Of`, `ItemsOf`, `Step`), `SceneOutline.cs` (the Scene
    Editor's rows: hierarchy, bones under their object, flags, keys), `LightWaveOptions.cs` (AVALightWave's own
    settings in `lightwave.json`; the engine's `Preferences` type holds the shared ones in `preferences.json`, both in
    the `AVALightWave` app-data folder) and `LightWaveHub.cs` (AVALightWave's end of the hub).
- `src/AVALightWave.Mcp/`: no UI. `LightWaveServer.cs` is the profile (`ToolGroups.Scene`, descriptions adjusted, the
  instructions, the live pipe `AVALightWave-mcp-<user>`), `LightWaveTools.cs` its own tools, `LightWaveRecipes.cs` its
  recipes (the engine's `Recipes.Recipe` builder), `LightWaveChat.cs` the chat's prompt and core tools.
- `src/AVALightWave/`: the window. `MainWindow.cs` (the layout, modes, panels, keys, files, hub and agents),
  `Panels.cs` (the nine panels; Objects, Bones, Lights and Camera are one `ItemsPanel` per edit mode around the engine's
  `ItemPropertiesPanel`), `SceneEditor.cs`, `NumericRequester.cs`, `WindowAgents.cs` (the live MCP door and the chat's
  host), `Program.cs` (`--mcp` before Avalonia starts).
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
- **Agents are Layout-only.** The profile serves `ToolGroups.Scene` and AVALightWave's own tools; a new engine tool that
  needs only the scene goes in `ToolGroups.Scene` (in the engine), and AVALightWave serves it with no change here. A new
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
