using System.ComponentModel;
using System.Text.Json.Nodes;
using AVAModeler.Core;
using AVAModeler.Layout;
using AVAModeler.Scene;
using ModelContextProtocol.Server;

namespace AVALightWave.Mcp;

/// <summary>Worlds (milestone D, stages 4 and 5): scattering copies over a surface, terrain, and a sky lit by the sun.</summary>
public sealed partial class LightWaveTools
{
    [McpServerTool(Name = "set_scatter", Destructive = true)]
    [Description("Scatters copies of one object over another's faces (trees on a hill, rocks on a beach, flowers in a bed), or changes or (off) removes the scatter, as one undo step. The copies are spread by area with a seed (the same every time), sized between scale's two numbers, turned at random about their up axis (random_turn), standing upright (default) or tilted along the ground (along_surface), only on one surface's faces (on_surface: \"Grass\" on a make_terrain), thinned by a picture's brightness laid over the object from above (density_map: an image in the working folder; X across, Z up the picture), and sunk a little into the ground (sink, metres) so they don't float on slopes. Model the copied object standing on its origin with +Y up; hide it (set_item visible false) or put it out of shot so the original isn't seen. Copies are real geometry in renders and exports; very many copies of a detailed object are capped by a face budget.")]
    public Task<string> SetScatter(
        [Description("The object scattered over, by name or #index.")] string @object,
        [Description("Remove its scatter.")] bool off = false,
        [Description("The object copied, by name.")] string? of = null,
        [Description("How many copies, 1 to 100000 (default 100).")] int? count = null,
        [Description("The seed: another number spreads them differently (default 0).")] int? seed = null,
        [Description("[least, most] size of a copy (default [0.8, 1.2]).")] double[]? scale = null,
        [Description("Turn each about its up axis at random (default true).")] bool? random_turn = null,
        [Description("Tilt each along the ground it stands on (default false: upright).")] bool? along_surface = null,
        [Description("Only on this surface's faces; \"\" for every face.")] string? on_surface = null,
        [Description("A picture in the working folder whose brightness is how likely a copy is there; \"\" for even.")] string? density_map = null,
        [Description("Metres to sink each copy into the ground (default 0).")] double? sink = null,
        CancellationToken cancel = default) =>
        Making("set a scatter", (_, l, root) =>
        {
            var o = ObjectByName(l.Scene, @object);
            if (off)
            {
                o.Scatter = null;
                l.Scene.Notify();
                return new JsonObject { ["object"] = o.Name, ["scatter"] = null };
            }
            var s = o.Scatter ?? new ScatterSettings();
            if (of != null)
            {
                var copied = ObjectByName(l.Scene, of);
                if (ReferenceEquals(copied, o)) throw new ArgumentException("An object can't be scattered over itself: give another object as of.");
                s = s with { Of = copied.Name };
            }
            if (scale != null)
            {
                if (scale.Length != 2) throw new ArgumentException("scale must be [least, most].");
                s = s with { ScaleMin = scale[0], ScaleMax = scale[1] };
            }
            if (on_surface is { Length: > 0 } && o.Surfaces.All(x => x.Name != on_surface))
                throw new ArgumentException($"{o.Name} has no surface \"{on_surface}\". Its surfaces are: {string.Join(", ", o.Surfaces.Select(x => x.Name))}.");
            string? map = density_map;
            if (map is { Length: > 0 })
            {
                map = Inside(root, map);
                if (!File.Exists(map)) throw new ArgumentException($"There's no picture {density_map} in the working folder.");
            }
            s = s with
            {
                Count = count ?? s.Count, Seed = seed ?? s.Seed, RandomTurn = random_turn ?? s.RandomTurn,
                AlongSurface = along_surface ?? s.AlongSurface, OnSurface = on_surface ?? s.OnSurface, DensityMap = map ?? s.DensityMap,
                Sink = sink ?? s.Sink,
            };
            if (s.Of.Length == 0) throw new ArgumentException("Give of: the object to copy.");
            o.Scatter = s.Checked();
            l.Scene.Notify();
            int spots = Scattering.Spots(o.BaseMesh(), o.Surfaces, s, null).Count;
            if (spots == 0) throw new ArgumentException(s.OnSurface.Length > 0 ? $"{o.Name} has no faces on {s.OnSurface} to scatter over." : $"{o.Name} has no faces to scatter over.");
            var said = new JsonObject
            {
                ["object"] = o.Name, ["of"] = s.Of, ["count"] = s.Count, ["seed"] = s.Seed, ["scale"] = new JsonArray(s.ScaleMin, s.ScaleMax),
                ["random_turn"] = s.RandomTurn, ["along_surface"] = s.AlongSurface,
            };
            if (s.OnSurface.Length > 0) said["on_surface"] = s.OnSurface;
            if (s.DensityMap.Length > 0) said["density_map"] = Path.GetFileName(s.DensityMap);
            if (s.Sink != 0) said["sink"] = s.Sink;
            if (s.DensityMap.Length > 0) said["note"] = "The density picture thins them: fewer than count may be placed where it's dark.";
            return said;
        }, cancel);

    [McpServerTool(Name = "make_terrain", Destructive = true)]
    [Description("Makes a landscape as a new object, as one undo step: a grid of quads centred on the origin in X and Z with its lowest point at 0 and its highest at height, shaped by Perlin noise (style hills: rolling; mountains: sharp ridges), weathered by erosion passes (steep drops crumble), and on three surfaces: Grass, Rock where it slopes more than rock_slope degrees, and Snow above snow_line (a share of the height). Another seed makes another landscape. Use set_scatter with on_surface \"Grass\" to put trees or rocks on it, set_sky for its light, and frame_items or build_scene to frame it. Returns the object with its height range and how many faces each surface has.")]
    public Task<string> MakeTerrain(
        [Description("The new object's name (default \"Terrain\", made unique).")] string? name = null,
        [Description("[width, depth] in metres along X and Z (default [100, 100]).")] double[]? size = null,
        [Description("Metres from the lowest point to the highest (default 12).")] double height = 12,
        [Description("Squares a side along the longer edge, 2 to 400 (default 64): more is finer and heavier.")] int resolution = 64,
        [Description("The seed (default 0).")] int seed = 0,
        [Description("hills (default) or mountains.")] string style = "hills",
        [Description("The size of the biggest hills, metres (default: 40% of the longer side).")] double? feature = null,
        [Description("0 smooth to 1 rough (default 0.5).")] double roughness = 0.5,
        [Description("Weathering passes, 0 to 200 (default 10).")] int erosion = 10,
        [Description("Grass, rock and snow surfaces (default true); false for one \"Terrain\" surface.")] bool layered = true,
        [Description("Snow above this share of the height, 0 to 1 (default 0.8; more than 1 for none).")] double snow_line = 0.8,
        [Description("Rock where it slopes more than this, degrees (default 35).")] double rock_slope = 35,
        [Description("[x, y, z] where its centre goes (default the origin).")] double[]? position = null,
        CancellationToken cancel = default) =>
        Making("made a terrain", (c, l, root) =>
        {
            double w = 100, d = 100;
            if (size != null)
            {
                if (size.Length != 2) throw new ArgumentException("size must be [width, depth] in metres.");
                (w, d) = (size[0], size[1]);
            }
            bool ridged = style.Trim().ToLowerInvariant() switch
            {
                "hills" => false, "mountains" => true,
                _ => throw new ArgumentException($"style must be hills or mountains, not \"{style}\"."),
            };
            var t = new TerrainSettings
            {
                Width = w, Depth = d, Height = height, Resolution = resolution, Seed = seed, Ridged = ridged,
                Feature = feature ?? 0.4 * Math.Max(w, d), Roughness = roughness, Erosion = erosion, Layered = layered,
                SnowLine = snow_line, RockSlope = rock_slope,
            }.Checked();
            var doc = Terrain.Make(t);
            var item = AddToScene(c, l, root, doc, Cloning.UniqueName(l.Scene, string.IsNullOrWhiteSpace(name) ? "Terrain" : name.Trim()), null, position, null, null, null);
            var r = Built(item, doc, l);
            var mesh = doc.Layers[0].Mesh;
            var faces = new JsonObject();
            for (int i = 0; i < doc.Surfaces.Count; i++) faces[doc.Surfaces[i].Name] = mesh.Polygons.Count(p => p.Surface == i);
            r["faces_by_surface"] = faces;
            r["heights"] = new JsonArray(0, Math.Round(mesh.Points.Max(p => p.Y), 4));
            return r;
        }, cancel);

    [McpServerTool(Name = "set_sky", Destructive = true)]
    [Description("Gives the scene a sky lit by the sun, as one undo step: the environment becomes builtin:sun (deep blue overhead, pale at the horizon, the sun's disc and glow, warm round a low sun, through dusk to a dark night when it's down; hazier with turbidity, 2 clear to 10 hazy), and (sun_light, default true) a distant light named \"Sun\" shines from it, white and full by day, orange and dim near the horizon, off at night. Place the sun by heading (degrees round from +Z, north, toward +X, east) and elevation (degrees above the horizon), or by time_of_day (hours, 12 noon) with latitude and day_of_year. environment_lighting lets the sky light the scene too (soft fill from every direction; costs render time). It doesn't change over time: for a sunset call it with the times you want and render each, or key the Sun light's rotation yourself.")]
    public Task<string> SetSky(
        [Description("The sun's heading, degrees round from +Z (north) toward +X (east). With elevation, instead of time_of_day.")] double? heading = null,
        [Description("The sun's elevation above the horizon, degrees (-90 to 90; below 0 is dusk and night).")] double? elevation = null,
        [Description("Local solar time in hours, 0 to 24 (12 is noon), instead of heading and elevation.")] double? time_of_day = null,
        [Description("Degrees north (negative south), for time_of_day (default 40).")] double latitude = 40,
        [Description("Day of the year, 1 to 365, for time_of_day (default 172, midsummer in the north).")] double day_of_year = 172,
        [Description("Haze: 2 clear to 10 hazy (default 3).")] double turbidity = 3,
        [Description("The sky's brightness (default 1).")] double intensity = 1,
        [Description("Let the sky light the scene too (default false).")] bool environment_lighting = false,
        [Description("A distant light called Sun shining from the sun (default true); false leaves the lights alone.")] bool sun_light = true,
        [Description("The sun light's name (default \"Sun\"): an existing light of that name is turned, else one is made.")] string light = "Sun",
        CancellationToken cancel = default) =>
        Making("set the sky", (c, l, root) =>
        {
            double h, e;
            if (time_of_day is { } hours)
            {
                if (heading != null || elevation != null) throw new ArgumentException("Give heading and elevation, or time_of_day, not both.");
                if (!(hours >= 0) || hours > 24) throw new ArgumentException("time_of_day must be from 0 to 24 hours.");
                if (!(Math.Abs(latitude) <= 90)) throw new ArgumentException("latitude must be from -90 to 90 degrees.");
                if (!(day_of_year >= 1) || day_of_year > 366) throw new ArgumentException("day_of_year must be from 1 to 366.");
                (h, e) = SunPosition.At(hours, latitude, day_of_year);
            }
            else
            {
                h = heading ?? 135;
                e = elevation ?? 35;
                if (!double.IsFinite(h) || !double.IsFinite(e) || Math.Abs(e) > 90) throw new ArgumentException("heading must be degrees and elevation from -90 to 90.");
            }
            if (!(turbidity >= 1) || turbidity > 20) throw new ArgumentException("turbidity must be from 1 to 20 (2 clear, 10 hazy).");
            var s = l.Scene;
            s.Environment = new EnvironmentSettings
            {
                Image = EnvironmentMap.SunSky(h, e, turbidity), Intensity = intensity, Lighting = environment_lighting,
                Samples = s.Environment.Samples,
            }.Checked();
            var said = new JsonObject
            {
                ["environment"] = s.Environment.Image, ["heading"] = h, ["elevation"] = e, ["turbidity"] = turbidity,
                ["environment_lighting"] = environment_lighting,
            };
            if (sun_light)
            {
                var calls = SceneCalls(root, c, l, ["set_item", "set_transform"]);
                var lamp = s.Items.OfType<LightItem>().FirstOrDefault(x => x.Name == light) ?? s.AddLight(light, LightType.Distant);
                var (colour, strength) = SunPosition.Light(e);
                calls.Call("set_item", new JsonObject
                {
                    ["item"] = lamp.Name, ["type"] = "distant", ["intensity"] = strength,
                    ["color"] = new JsonArray(Math.Round(colour.X * 255), Math.Round(colour.Y * 255), Math.Round(colour.Z * 255)),
                });
                // A distant light shines along its +Z: from the sun, so turned half round and pitched down by its elevation.
                calls.Call("set_transform", new JsonObject
                {
                    ["item"] = lamp.Name, ["key"] = false, ["rotation"] = new JsonArray(Math.Round((h + 180) % 360, 3), Math.Round(Math.Max(e, 0.5), 3), 0),
                });
                said["sun_light"] = new JsonObject
                {
                    ["name"] = lamp.Name, ["intensity"] = strength,
                    ["color"] = new JsonArray(Math.Round(colour.X * 255), Math.Round(colour.Y * 255), Math.Round(colour.Z * 255)),
                };
            }
            if (e < 0) said["note"] = "The sun is below the horizon: dusk or night. Light the scene with lamps, or raise the elevation.";
            s.Notify();
            return said;
        }, cancel);
}
