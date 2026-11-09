using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Layout;
using AVAModeler.Mcp;
using AVAModeler.Scene;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>
/// AVALightWave's own MCP tools, beside the engine's scene tools: Replace Object and saving an object (a standalone
/// Layout's objects come from files and go back to them), and the hub (Edit in Modeler and its state). They reach the
/// scene only through <see cref="IModelerHost.RunSceneAsync{T}"/> and the session, as the engine's tools do, so each
/// change is one undo step ("Agent: …") the person's Ctrl+Z takes back, and a call that fails changes nothing.
/// </summary>
public sealed class LightWaveTools(IModelerHost host, ILightWaveApp? app)
{
    [McpServerTool(Name = "replace_object", Destructive = true)]
    [Description("Replace Object, as classic Layout had it: the object item takes another file's geometry and surfaces (from the working folder: .lwo, .lw, .obj, .geo, .glb, .gltf, .stl or .3mf), keeping its name, position, rotation, scale, keys, parent, bones and settings. An item named after its old file takes the new file's name. One undo step. Returns the item with its point and polygon counts.")]
    public Task<string> ReplaceObject(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        [Description("The object file, relative to the working folder, e.g. \"objects/ship2.lwo\".")] string file,
        CancellationToken cancel = default) =>
        Scene("replaced an object", (l, root) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var full = Inside(root, file);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {file} in the working folder ({root}).");
            var doc = ObjectFiles.Load(full);
            string? oldName = item.SourcePath != null ? Path.GetFileNameWithoutExtension(item.SourcePath) : null;
            item.Document = doc;
            if (item.Layer is int layer && layer >= doc.Layers.Count) item.Layer = null;
            item.SourcePath = full;
            if (oldName != null && item.Name == oldName) item.Name = Path.GetFileNameWithoutExtension(full);
            l.Scene.Notify();
            return Describe(item);
        }, cancel);

    [McpServerTool(Name = "save_object", Destructive = true)]
    [Description("Saves an object item's geometry and surfaces to a file in the working folder, the format chosen by the extension (.lwo keeps everything; .obj, .geo, .glb, .gltf, .stl, .3mf for other programs), and makes it the object's file, so a .lws scene saved afterwards refers to it. An existing file is only replaced when overwrite is true. Not an undo step (files aren't undone).")]
    public Task<string> SaveObject(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        [Description("The file, relative to the working folder, e.g. \"objects/ship.lwo\".")] string path,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Scene("saved an object", (l, root) =>
        {
            var item = ObjectByName(l.Scene, @object);
            var doc = item.Document ?? throw new InvalidOperationException($"{item.Name} couldn't be loaded, so there's nothing to save.");
            var full = Inside(root, path);
            if (!ObjectFiles.SaveExtensions.Contains(Path.GetExtension(full).ToLowerInvariant()))
                throw new ArgumentException($"Objects are saved as {string.Join(", ", ObjectFiles.SaveExtensions)}, not {Path.GetExtension(full)}.");
            if (File.Exists(full) && !overwrite) throw new IOException($"{path} already exists: give overwrite true to replace it.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            ObjectFiles.Save(doc, full);
            ObjectFiles.RebaseImages(doc, full);
            doc.FilePath = full;
            doc.Modified = false;
            foreach (var o in l.Scene.Objects.Where(o => ReferenceEquals(o.Document, doc))) o.SourcePath = full;
            var result = Describe(item);
            result["saved"] = Path.GetRelativePath(root, full);
            return result;
        }, cancel, changes: false);

    [McpServerTool(Name = "edit_in_modeler", Destructive = true)]
    [Description("Edit in Modeler: sends an object item to AVA Modeler, the companion modeler, to change its shape there (starting AVA Modeler if it isn't running and can be found). When it's saved in AVA Modeler, AVALightWave reads it again and updates the item, keeping its motion. Only in the open AVALightWave window (on the hub); AVA Modeler won't open it over unsaved work of its own, and says so. Modeling itself is AVA Modeler's: an agent connected to AVA Modeler's own MCP server can model it there.")]
    public async Task<string> EditInModeler(
        [Description("The object item, by name or #index from get_scene.")] string @object,
        CancellationToken cancel = default)
    {
        if (app == null) throw new McpException("edit_in_modeler works in the open AVALightWave window (the hub links it with AVA Modeler), not in the headless server.");
        ObjectItem item;
        try
        {
            item = await host.RunSceneAsync("chose an object for AVA Modeler", (_, l) => ObjectByName(l.Scene, @object), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
        var said = await app.EditInModeler(item).ConfigureAwait(false);
        return new JsonObject { ["object"] = item.Name, ["result"] = said }.ToJsonString();
    }

    [McpServerTool(Name = "hub_status", ReadOnly = true, Idempotent = true)]
    [Description("Says whether AVALightWave is on the hub (the live link with AVA Modeler, the companion modeler) and whether AVA Modeler is there now. Headless, there's no hub.")]
    public Task<string> HubStatus(CancellationToken cancel = default)
    {
        var (on, there) = app?.HubState() ?? (false, false);
        return Task.FromResult(new JsonObject
        {
            ["on_hub"] = on,
            ["modeler_running"] = there,
            ["note"] = app == null ? "The headless server has no hub: run AVALightWave's window for Edit in Modeler." :
                on ? (there ? "AVA Modeler is linked: edit_in_modeler sends it an object." : "AVA Modeler isn't running: edit_in_modeler starts it if it can be found.")
                : "The hub is off (Options › Link with AVA Modeler).",
        }.ToJsonString());
    }

    [McpServerTool(Name = "clone_item", Destructive = true)]
    [Description("Clone Item, as classic Layout had it: copies of an object, light, camera or null with its motion and settings (an object brings its bones, morphs and displacements), named after it (\"Ball (2)\", \"Ball (3)\"…) and with its parent. Each copy is offset from the one before: position_offset [x, y, z] metres, rotation_offset [heading, pitch, bank] degrees and scale_offset [x, y, z] (added to its scale) are added to every key of those channels, and time_offset (frames) moves every key of its animated channels later, so copies follow one another (a stagger). All the copies are one undo step. Returns the copies.")]
    public Task<string> CloneItem(
        [Description("The item to clone, by name or #index from get_scene (not a bone: clone its object).")] string item,
        [Description("How many copies, 1 to 1000 (default 1).")] int count = 1,
        [Description("Each copy's move from the one before, [x, y, z] metres (default none).")] double[]? position_offset = null,
        [Description("Each copy's turn from the one before, [heading, pitch, bank] degrees (default none).")] double[]? rotation_offset = null,
        [Description("Each copy's scale change from the one before, [x, y, z] added to the scale (default none).")] double[]? scale_offset = null,
        [Description("Each copy's motion this many frames after the one before (default 0).")] double time_offset = 0,
        CancellationToken cancel = default) =>
        Scene("cloned an item", (l, _) =>
        {
            var it = ItemByName(l.Scene, item);
            var copies = Cloning.Clone(l, it, new CloneOptions
            {
                Count = count, Position = Vector(position_offset, "position_offset"), Rotation = Vector(rotation_offset, "rotation_offset"),
                Scale = Vector(scale_offset, "scale_offset"), TimeOffset = time_offset,
            });
            return new JsonObject
            {
                ["cloned"] = it.Name,
                ["copies"] = new JsonArray(copies.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name, ["kind"] = c.Kind.ToLowerInvariant(),
                    ["position"] = Vec(c.PositionAt(l.Frame)), ["first_key"] = c.KeyFrames().Count > 1 ? c.KeyFrames().Where(f => f >= l.Scene.FirstFrame).DefaultIfEmpty().First() : null,
                }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "load_from_scene", Destructive = true)]
    [Description("Load from Scene (classic Layout's Load Items From Scene): brings another scene file's items (.lws or .avascene, from the working folder) into the open scene: its objects with their bones, its nulls, and its lights and cameras unless told not to, with their parents, motion and settings. Its frames, backdrop and effects are left behind, and its cameras never become the current camera. Names already taken are renamed (\"Ball (2)\") unless names is \"keep\". Objects that can't be read stand in as placeholders (said in warnings). One undo step. Returns the items brought in.")]
    public Task<string> LoadFromScene(
        [Description("The scene file, relative to the working folder, e.g. \"scenes/props.lws\".")] string file,
        [Description("Bring its lights (default true).")] bool lights = true,
        [Description("Bring its cameras (default true).")] bool cameras = true,
        [Description("\"rename\" (default) or \"keep\" for names already in the scene.")] string names = "rename",
        CancellationToken cancel = default) =>
        Scene("loaded items from a scene", (l, root) =>
        {
            var full = Inside(root, file);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {file} in the working folder ({root}).");
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (ext is not (".lws" or SceneJson.Extension)) throw new ArgumentException($"{file} isn't a scene: give a .lws or {SceneJson.Extension} file.");
            var clash = names.Trim().ToLowerInvariant() switch
            {
                "rename" => NameClash.Rename,
                "keep" => NameClash.Keep,
                _ => throw new ArgumentException("names must be \"rename\" or \"keep\"."),
            };
            var result = SceneMerge.Load(l, full, new MergeOptions { Lights = lights, Cameras = cameras, Names = clash }, SceneDocument.LoadOrNull);
            return new JsonObject
            {
                ["from"] = Path.GetRelativePath(root, full),
                ["items"] = new JsonArray(result.Items.Select(i => (JsonNode)new JsonObject { ["name"] = i.Name, ["kind"] = i.Kind.ToLowerInvariant(), ["parent"] = i.Parent?.Name }).ToArray()),
                ["warnings"] = new JsonArray(result.Warnings.Select(w => (JsonNode)JsonValue.Create(w)).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "add_morph_target", Destructive = true)]
    [Description("Morph envelopes in Layout (classic Layout's Metamorph): gives a scene object a morph target from an object file in the working folder (one layer, or all its layers merged) or from another object in the scene (its shape now). The target must have exactly the object's points in the same order (made from the object in a modeler: AVA Modeler's edit_in_modeler can make one). The weight is the channel morph.<name>: 0 the object's own shape, 1 the target, between blends, beyond 1 exaggerates; it starts at weight (default 0) and is keyed with set_key (channel \"morph.<name>\"). Several targets add up. Shows in the views, previews and renders. One undo step. set_morph renames one or sets its weight; remove_morph takes it off.")]
    public Task<string> AddMorphTarget(
        [Description("The object to morph, by name or #index from get_scene.")] string @object,
        [Description("An object file in the working folder holding the target shape, e.g. \"objects/face-smile.lwo\" (or give from_object).")] string? file = null,
        [Description("Another scene object whose shape is the target (or give file).")] string? from_object = null,
        [Description("The morph's name (default the file's or object's name); its channel is morph.<name>.")] string? name = null,
        [Description("The file's layer holding the target, 1 to 10 (default every non-empty layer merged).")] int? layer = null,
        [Description("Its weight throughout until keyed, 0 to 1 (default 0: the object's own shape).")] double weight = 0,
        CancellationToken cancel = default) =>
        Scene("added a morph target", (l, root) =>
        {
            var obj = ObjectByName(l.Scene, @object);
            if ((file == null) == (from_object == null)) throw new ArgumentException("Give file (an object file) or from_object (a scene object), one of them.");
            MorphTarget m;
            if (file != null)
            {
                var full = Inside(root, file);
                if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {file} in the working folder ({root}).");
                m = MorphTargets.AddFromFile(l, obj, full, name, layer, weight);
            }
            else m = MorphTargets.AddFromObject(l, obj, ObjectByName(l.Scene, from_object!), name, weight);
            return new JsonObject
            {
                ["object"] = obj.Name,
                ["added"] = AVAModeler.Layout.SceneChannels.MorphChannel(m),
                ["points"] = m.Target.Points.Count,
                ["morphs"] = new JsonArray(obj.Morphs.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["channel"] = AVAModeler.Layout.SceneChannels.MorphChannel(x), ["weight"] = Math.Round(x.Weight.Evaluate(l.Frame), 6) }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "load_motion", Destructive = true)]
    [Description("Load Motion, as classic Layout had it: replaces an item's nine motion channels (position, rotation, scale) with the keys in a LightWave motion file (.mot, version 1 from Layout 4/5 or version 3 from LightWave 6 on) from the working folder — one saved with save_motion from another item applies that item's motion to this one. Times in version 3 files are read at the scene's frames per second. Its other channels (intensity, morphs…), path following and motion modifiers are left as they are. One undo step. Returns the keys now on its channels, with any warnings.")]
    public Task<string> LoadMotion(
        [Description("The item, by name or #index from get_scene.")] string item,
        [Description("The motion file, relative to the working folder, e.g. \"motions/bounce.mot\".")] string file,
        CancellationToken cancel = default) =>
        Scene("loaded a motion", (l, root) =>
        {
            var it = ItemByName(l.Scene, item);
            var full = Inside(root, file);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {file} in the working folder ({root}).");
            var r = l.LoadMotion(it, full);
            return new JsonObject
            {
                ["item"] = it.Name, ["version"] = r.Version, ["keys"] = it.KeyFrames().Count,
                ["key_frames"] = new JsonArray(it.KeyFrames().Select(f => (JsonNode)JsonValue.Create(Math.Round(f, 4))).ToArray()),
                ["warnings"] = new JsonArray(r.Warnings.Select(w => (JsonNode)JsonValue.Create(w)).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "save_motion", Destructive = true)]
    [Description("Save Motion: writes an item's nine motion channels as a LightWave motion file (.mot) in the working folder: version 3 (the default, LightWave 6 on: keeps every key's shape and each channel's before and after behaviour, times in seconds at the scene's rate) or version 1 (Layout 4/5: keys on all nine channels at once, TCB or linear, one after behaviour). Load it onto another item with load_motion. Path following and motion modifiers aren't kept (a warning says so). An existing file is replaced only when overwrite is true. Not an undo step (files aren't undone).")]
    public Task<string> SaveMotion(
        [Description("The item, by name or #index from get_scene.")] string item,
        [Description("The file, relative to the working folder, e.g. \"motions/bounce.mot\".")] string file,
        [Description("1 or 3 (default 3).")] int version = 3,
        [Description("Replace the file if it already exists.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Scene("saved a motion", (l, root) =>
        {
            var it = ItemByName(l.Scene, item);
            if (version is not (1 or 3)) throw new ArgumentException("version must be 1 (Layout 4/5) or 3 (LightWave 6 on).");
            var full = Inside(root, file);
            if (!full.EndsWith(MotionFile.Extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException($"A motion file ends in {MotionFile.Extension}.");
            if (File.Exists(full) && !overwrite) throw new IOException($"{file} already exists: give overwrite true to replace it.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var warnings = l.SaveMotion(it, full, version);
            return new JsonObject
            {
                ["item"] = it.Name, ["saved"] = Path.GetRelativePath(root, full), ["version"] = version,
                ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode)JsonValue.Create(w)).ToArray()),
            };
        }, cancel, changes: false);
    // ───────────────────────────── the render queue and farm ─────────────────────────────

    /// <summary>The farm folder a queue tool works on: the one given (inside the working folder), else the window's queue, else render-queue in the working folder.</summary>
    private string QueueFolder(string? folder)
    {
        try { return folder != null ? Inside(host.Root, folder) : app?.QueueFolder ?? Inside(host.Root, "render-queue"); }
        catch (ArgumentException e) { throw new McpException(e.Message, e); }
    }

    /// <summary>A path as an agent sees it: relative to the working folder when inside it.</summary>
    private string Shown(string path)
    {
        string rel = Path.GetRelativePath(host.Root, path);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? path : rel.Replace('\\', '/');
    }

    [McpServerTool(Name = "queue_render", Destructive = false)]
    [Description("Batch rendering, in the spirit of classic LightWave's ScreamerNet: sends the scene as it is now to a render queue as a job that render nodes work frame by frame. Nodes are the AVALightWave window's own (Render › Render Queue › Start Local Rendering) and any number of \"AVALightWave --render-node <folder>\" processes, on this machine or others that reach the same folder (a network share works); each claims frames the others haven't. The job is a copy (meshes embedded, its images copied in), so later changes to the scene don't touch it. The frames are PNGs in <folder>/jobs/<job>/out, named <name>0001.png onwards in render order; with movie, the node finishing the last frame joins them with ffmpeg if it has it. Not an undo step: the scene isn't changed. render_queue_status shows how it's going.")]
    public async Task<string> QueueRender(
        [Description("The first frame (default: the scene's first).")] int? first = null,
        [Description("The last frame (default: the scene's last).")] int? last = null,
        [Description("Render every step-th frame (default: the scene's frame step).")] int? step = null,
        [Description("Width in pixels, 1 to 8192 (default: the camera's).")] int? width = null,
        [Description("Height in pixels, 1 to 8192 (default: the camera's).")] int? height = null,
        [Description("Rays per pixel (antialiasing), 1 to 64 (default 1).")] int samples = 1,
        [Description("The frames' file name before the number (default \"frame\": frame0001.png…).")] string name = "frame",
        [Description("The job's name in the queue (default: the scene's file name, or \"Scene\").")] string? job_name = null,
        [Description("A movie of the frames when they're all done: none (default), mp4, gif or both (needs ffmpeg on the node).")] string movie = "none",
        [Description("The render farm folder, relative to the working folder, e.g. \"farm\" (made if needed). Default: the open window's render queue; headless, \"render-queue\" in the working folder.")] string? folder = null,
        CancellationToken cancel = default)
    {
        string dir = QueueFolder(folder);
        try
        {
            var (snapshot, settings, sceneFolder) = await host.RunSceneAsync("sent the scene to the render queue", (_, l) =>
            {
                var s = l.Scene;
                var basis = FarmSubmit.From(s);
                var sub = basis with
                {
                    Name = job_name ?? (l.ScenePath != null ? Path.GetFileNameWithoutExtension(l.ScenePath) : null),
                    First = first ?? basis.First, Last = last ?? basis.Last, Step = step ?? basis.Step,
                    Width = width ?? basis.Width, Height = height ?? basis.Height, Samples = samples,
                    Output = name, Movie = movie,
                };
                return (FarmFolder.Snapshot(s), sub, l.ScenePath != null ? Path.GetDirectoryName(Path.GetFullPath(l.ScenePath)) : host.Root);
            }, cancel).ConfigureAwait(false);
            var farm = new FarmFolder(dir);
            var sent = await Task.Run(() => farm.Submit(snapshot, settings, sceneFolder), cancel).ConfigureAwait(false);
            var nodes = await Task.Run(() => farm.Nodes(), cancel).ConfigureAwait(false);
            return new JsonObject
            {
                ["job"] = sent.Job.Id,
                ["name"] = sent.Job.Name,
                ["folder"] = Shown(farm.Root),
                ["frames"] = sent.Job.Frames.Count,
                ["first"] = sent.Job.First,
                ["last"] = sent.Job.Last,
                ["step"] = sent.Job.Step,
                ["size"] = new JsonArray(sent.Job.Width, sent.Job.Height),
                ["output_folder"] = Shown(sent.OutputFolder),
                ["first_file"] = Shown(Path.Combine(sent.OutputFolder, sent.Job.FileName(1))),
                ["warnings"] = new JsonArray(sent.Warnings.Select(w => (JsonNode)w).ToArray()),
                ["nodes_working"] = nodes.Count,
                ["note"] = nodes.Count > 0 ? $"{nodes.Count} render node(s) are watching this folder." :
                    $"No render node is watching this folder yet: the person starts one with {farm.NodeCommand} (on any machine that reaches it), or Render › Render Queue › Start Local Rendering in the window.",
            }.ToJsonString();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new McpException(e.Message, e);
        }
    }

    [McpServerTool(Name = "render_queue_status", ReadOnly = true, Idempotent = true)]
    [Description("The render queue's jobs, in the order nodes take them, each with its state (waiting, rendering, done, failed with the reason, cancelled), frames done of the total, the nodes rendering it, its output folder and its movie; and the render nodes heard from in the last two minutes (name, machine, what they're on, frames rendered).")]
    public async Task<string> RenderQueueStatus(
        [Description("The render farm folder, relative to the working folder (default: the open window's render queue; headless, \"render-queue\" in the working folder).")] string? folder = null,
        CancellationToken cancel = default)
    {
        string dir = QueueFolder(folder);
        var farm = new FarmFolder(dir);
        var (jobs, nodes) = await Task.Run(() => (farm.Scan(), farm.Nodes()), cancel).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        return new JsonObject
        {
            ["folder"] = Shown(farm.Root),
            ["jobs"] = new JsonArray(jobs.TakeLast(50).Select(j => (JsonNode)new JsonObject
            {
                ["job"] = j.Id,
                ["name"] = j.Name,
                ["state"] = j.State.ToString().ToLowerInvariant(),
                ["done"] = j.Done,
                ["total"] = j.Total,
                ["reason"] = j.Reason,
                ["nodes"] = new JsonArray(j.Nodes.Select(n => (JsonNode)n).ToArray()),
                ["movie"] = j.Movie,
                ["output_folder"] = Shown(j.OutputFolder),
            }).ToArray()),
            ["nodes"] = new JsonArray(nodes.Select(n => (JsonNode)new JsonObject
            {
                ["name"] = n.Name,
                ["host"] = n.Host,
                ["state"] = n.State,
                ["job"] = n.Job,
                ["frame"] = n.Frame,
                ["rendered"] = n.Rendered,
                ["seen_seconds_ago"] = Math.Round(Math.Max(0, (now - n.Seen).TotalSeconds), 1),
            }).ToArray()),
            ["node_command"] = farm.NodeCommand,
        }.ToJsonString();
    }

    [McpServerTool(Name = "run_macro", Destructive = true)]
    [Description("Runs a Lua scene macro, AVALightWave's Macros menu's language, on the open scene: give code, or file (a .lua in the working folder). It runs in AVA Modeler's macro sandbox (no os, io or require; stopped after 30 seconds) with functions for the scene: items, frame, frames, addobject, addnull, addlight, addcamera, remove, select, rename, parent, setpos/setrot/setscale (keys at a frame), getpos/getrot/getscale, key (item, channel, frame, value, {Tension, Continuity, Bias, Span}), unkey, get, channels, light, clone, render (a PNG), print. Relative paths start from the working folder (or the macro file's folder). The whole macro is one undo step; one that fails changes nothing. Returns what it printed.")]
    public Task<string> RunMacro(
        [Description("Lua code to run (or give file).")] string? code = null,
        [Description("A .lua macro file in the working folder (or give code).")] string? file = null,
        CancellationToken cancel = default) =>
        Scene("ran a macro", (l, root) =>
        {
            if ((code == null) == (file == null)) throw new ArgumentException("Give code or file, one of them.");
            var printed = new List<string>();
            string folder = root, name = "agent macro";
            if (file != null)
            {
                var full = Inside(root, file);
                if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {file} in the working folder ({root}).");
                code = File.ReadAllText(full);
                folder = Path.GetDirectoryName(full)!;
                name = Path.GetFileNameWithoutExtension(full);
            }
            // Paths a macro gives stay inside the working folder: they're checked by the sandbox's functions through
            // the folder they start from, and anything outside it is refused here.
            LayoutMacros.Run(l, code!, name, folder, printed.Add, guard: p => Inside(root, Path.GetRelativePath(root, p)));
            return new JsonObject
            {
                ["ran"] = name,
                ["printed"] = new JsonArray(printed.Select(p => (JsonNode)JsonValue.Create(p)).ToArray()),
                ["items"] = l.Scene.Items.Count,
            };
        }, cancel);

    [McpServerTool(Name = "make_preview", Destructive = true)]
    [Description("Make Preview, as classic Layout had it: a quick flipbook of a range of frames drawn the way the Layout view draws them (shaded or wireframe, through the scene's camera in its frame shape), not ray traced, so it takes moments rather than minutes: use it to check motion before render_animation. The frames are saved as numbered PNGs (name0001.png…) in a folder inside the working folder, and with movie \"mp4\" or \"gif\" joined into a movie at the scene's frame rate (needs ffmpeg; without it the PNGs are kept and the reason given). Not an undo step. Returns the files.")]
    public async Task<string> MakePreview(
        [Description("The first frame (default the scene's first).")] int? first = null,
        [Description("The last frame (default the scene's last).")] int? last = null,
        [Description("Every how many frames (default the scene's frame step).")] int? step = null,
        [Description("The picture's width in pixels, 160 to 1920 (default 320); the height follows the camera's frame shape.")] int width = 320,
        [Description("Shaded faces (true, default) or wireframe.")] bool shaded = true,
        [Description("The folder for the frames, inside the working folder (default \"preview\").")] string folder = "preview",
        [Description("The frames' file name before their numbers (default \"preview\").")] string name = "preview",
        [Description("\"none\" (default), \"mp4\" or \"gif\".")] string movie = "none",
        CancellationToken cancel = default)
    {
        Preview preview;
        string full;
        try
        {
            if (width < 160 || width > 1920) throw new ArgumentException("width must be from 160 to 1920 pixels.");
            var format = movie.Trim().ToLowerInvariant() switch
            {
                "none" => (MovieFormat?)null, "mp4" => MovieFormat.Mp4, "gif" => MovieFormat.Gif,
                _ => throw new ArgumentException("movie must be \"none\", \"mp4\" or \"gif\"."),
            };
            if (Path.GetFileName(name) != name || name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("name must be a plain file name, e.g. \"bounce\".");
            full = Inside(host.Root, folder);
            preview = await host.RunSceneAsync("made a preview", (_, l) =>
            {
                var s = l.Scene;
                return new Preview(s, new PreviewSettings
                {
                    First = first ?? s.FirstFrame, Last = last ?? s.LastFrame, Step = step ?? Math.Max(1, s.FrameStep), Width = width, Shaded = shaded,
                });
            }, cancel).ConfigureAwait(false);
            await preview.MakeAsync(null, cancel).ConfigureAwait(false);
            Directory.CreateDirectory(full);
            var result = new JsonObject
            {
                ["frames"] = preview.Count, ["size"] = new JsonArray(preview.Width, preview.Height), ["through"] = preview.LooksThrough,
                ["folder"] = Path.GetRelativePath(host.Root, full),
            };
            if (format is { } f)
            {
                var saved = await preview.SaveMovieAsync(full, name, f, MovieEncoder.FromPath(), cancel).ConfigureAwait(false);
                result["first_file"] = Path.GetFileName(saved.Frames[0]);
                result["movie"] = saved.Movie != null ? Path.GetRelativePath(host.Root, saved.Movie) : null;
                if (saved.Problem != null) result["problem"] = saved.Problem;
            }
            else result["first_file"] = Path.GetFileName(preview.SavePngs(full, name)[0]);
            if (preview.Note != null) result["note"] = preview.Note;
            return result.ToJsonString();
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
    }

    // ───────────────────────────── helpers ─────────────────────────────

    /// <summary>Any item by name or #index.</summary>
    internal static SceneItem ItemByName(AVAModeler.Scene.Scene scene, string name)
    {
        SceneItem? item = null;
        if (name.StartsWith('#') && int.TryParse(name[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < scene.Items.Count) item = scene.Items[i];
        return item ?? scene.Find(name) ?? throw new ArgumentException($"There's no item \"{name}\". The items are: {string.Join(", ", scene.Items.Select(o => o.Name))}.");
    }

    /// <summary>An [x, y, z] from an agent (none: zero).</summary>
    internal static Vec3 Vector(double[]? v, string what)
    {
        if (v == null) return Vec3.Zero;
        if (v.Length != 3 || v.Any(x => !double.IsFinite(x))) throw new ArgumentException($"{what} must be [x, y, z], three numbers.");
        return new Vec3(v[0], v[1], v[2]);
    }

    private static JsonArray Vec(Vec3 v) => new(Math.Round(v.X, 6), Math.Round(v.Y, 6), Math.Round(v.Z, 6));

    /// <summary>Runs scene work on the host, as one undo step named "Agent: …" when it changes the scene; errors become tool errors.</summary>
    private async Task<string> Scene(string action, Func<LayoutSession, string, JsonObject> work, CancellationToken cancel, bool changes = true)
    {
        try
        {
            return await host.RunSceneAsync(action, (_, l) =>
                (changes ? l.Edit(ModelerTools.AgentStep(action), () => work(l, host.Root), apart: true) : work(l, host.Root)).ToJsonString(), cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(e.Message, e);
        }
    }

    /// <summary>A path inside the working folder, or an error saying it must be.</summary>
    internal static string Inside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is needed.");
        var full = Path.GetFullPath(Path.Combine(root, path));
        var rootSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(rootSlash, cmp)) throw new ArgumentException($"{path} is outside the working folder ({root}). Give a path inside it.");
        return full;
    }

    /// <summary>An object item by name or #index.</summary>
    internal static ObjectItem ObjectByName(AVAModeler.Scene.Scene scene, string name)
    {
        SceneItem? item = null;
        if (name.StartsWith('#') && int.TryParse(name[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < scene.Items.Count) item = scene.Items[i];
        item ??= scene.Find(name);
        return item switch
        {
            ObjectItem o => o,
            null => throw new ArgumentException($"There's no item \"{name}\". The objects are: {string.Join(", ", scene.Objects.Select(o => o.Name))}."),
            _ => throw new ArgumentException($"{item.Name} is a {item.Kind.ToLowerInvariant()}, not an object."),
        };
    }

    private static JsonObject Describe(ObjectItem item)
    {
        var mesh = item.BaseMesh();
        return new JsonObject
        {
            ["name"] = item.Name,
            ["source"] = item.SourcePath,
            ["points"] = mesh.Points.Count,
            ["polygons"] = mesh.Polygons.Count,
        };
    }
}
