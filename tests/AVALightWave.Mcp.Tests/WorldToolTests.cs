using AVAModeler.Core;
using AVAModeler.Scene;
using ModelContextProtocol.Protocol;
using static AVALightWave.Mcp.Tests.Calls;

namespace AVALightWave.Mcp.Tests;

/// <summary>Worlds: terrain, scattering over it and a sky lit by the sun, each one undo step.</summary>
public class WorldToolTests
{
    [Fact]
    public async Task Terrain_is_a_grid_facing_up_from_zero_to_its_height_on_grass_rock_and_snow()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        var r = await s.Client.Json("make_terrain", new { size = new[] { 80.0, 40 }, height = 20, resolution = 40, style = "mountains", seed = 2, snow_line = 0.7 });
        Assert.Equal("Terrain", (string?)r["name"]);
        Assert.Equal(20, (double)r["heights"]![1]!, 3);
        Assert.Equal(41 * 21, (int)r["points"]!);
        var faces = r["faces_by_surface"]!.AsObject();
        Assert.True((int)faces["Grass"]! > 0 && (int)faces["Rock"]! > 0 && (int)faces["Snow"]! > 0, faces.ToJsonString());
        var terrain = (ObjectItem)b.Scene.Find("Terrain")!;
        var mesh = terrain.BaseMesh();
        Assert.Equal(0, mesh.Points.Min(p => p.Y), 6);
        Assert.Equal(-40, mesh.Points.Min(p => p.X), 6);
        Assert.All(mesh.Polygons, p => Assert.True(mesh.Normal(p).Y > 0, "Every face looks up."));
        Assert.Equal("Agent: made a terrain", b.Host.Layout.History.UndoLabel);

        // The same seed makes the same land; another makes another; a second one gets its own name.
        var again = await s.Client.Json("make_terrain", new { size = new[] { 80.0, 40 }, height = 20, resolution = 40, style = "mountains", seed = 2, snow_line = 0.7, position = new[] { 0.0, 0, 100 } });
        Assert.Equal("Terrain (2)", (string?)again["name"]);
        Assert.Equal(mesh.Points.Select(p => p.Y), ((ObjectItem)b.Scene.Find("Terrain (2)")!).BaseMesh().Points.Select(p => p.Y));
        var hills = Terrain.Heights(new TerrainSettings { Seed = 5, Resolution = 20 }, out _, out _);
        Assert.NotEqual(hills[7, 7], Terrain.Heights(new TerrainSettings { Seed = 6, Resolution = 20 }, out _, out _)[7, 7]);

        Assert.Contains("style must be", await s.Client.Fails("make_terrain", new { style = "lumpy" }));
        Assert.Contains("resolution", await s.Client.Fails("make_terrain", new { resolution = 1000 }));
    }

    [Fact]
    public void Erosion_softens_the_steepest_drops()
    {
        static double Steepest(double[,] h)
        {
            double most = 0;
            for (int i = 0; i + 1 < h.GetLength(0); i++)
                for (int j = 0; j < h.GetLength(1); j++) most = Math.Max(most, Math.Abs(h[i + 1, j] - h[i, j]));
            return most;
        }
        var raw = new TerrainSettings { Ridged = true, Resolution = 48, Erosion = 0, Height = 30, Feature = 30 };
        Assert.True(Steepest(Terrain.Heights(raw with { Erosion = 60 }, out _, out _)) < Steepest(Terrain.Heights(raw, out _, out _)));
    }

    [Fact]
    public async Task Trees_are_scattered_on_the_grass_only_and_undo_takes_them_off()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_terrain", new { size = new[] { 60.0, 60 }, height = 15, resolution = 30, seed = 1 });
        await s.Client.Json("make_object", new { shape = "cone", name = "Tree", position = new[] { 0.0, -50, 0 } });
        var tree = (ObjectItem)b.Scene.Find("Tree")!;
        int treeFaces = tree.BaseMesh().Polygons.Count;
        var r = await s.Client.Json("set_scatter", new { @object = "Terrain", of = "Tree", count = 40, seed = 3, on_surface = "Grass", scale = new[] { 0.5, 1.5 }, sink = 0.1 });
        Assert.Equal("Tree", (string?)r["of"]);
        var terrain = (ObjectItem)b.Scene.Find("Terrain")!;
        int own = terrain.BaseMesh().Polygons.Count;
        var frame = b.Scene.Evaluate(1).Objects.Single(x => x.Item == terrain);
        Assert.Equal(own + 40 * treeFaces, frame.Mesh.Polygons.Count);
        int grass = terrain.Surfaces.ToList().FindIndex(x => x.Name == "Grass");
        var spots = Scattering.Spots(terrain.BaseMesh(), terrain.Surfaces, terrain.Scatter!, null);
        var m = terrain.BaseMesh();
        // Each spot lies on a grass face: the nearest face centre to it is grass.
        Assert.All(spots, sp =>
        {
            var nearest = m.Polygons.MinBy(p => (p.Points.Select(i => m.Points[i]).Aggregate(Vec3.Zero, (a, x) => a + x) * (1.0 / p.Count) - sp.At).Length)!;
            Assert.Equal(grass, nearest.Surface);
        });

        // A density picture: black everywhere, so none can be placed.
        var black = new byte[8 * 8 * 4];
        for (int i = 3; i < black.Length; i += 4) black[i] = 255;
        Png.Save(Path.Combine(b.Folder.Path, "none.png"), 8, 8, black);
        await s.Client.Json("set_scatter", new { @object = "Terrain", density_map = "none.png" });
        Assert.Equal(own, b.Scene.Evaluate(1).Objects.Single(x => x.Item == terrain).Mesh.Polygons.Count);

        await s.Client.Json("undo_scene");
        await s.Client.Json("undo_scene");
        Assert.Null(terrain.Scatter);
        Assert.Contains("over itself", await s.Client.Fails("set_scatter", new { @object = "Terrain", of = "Terrain" }));
        Assert.Contains("no surface", await s.Client.Fails("set_scatter", new { @object = "Terrain", of = "Tree", on_surface = "Sand" }));
        Assert.Contains("no picture", await s.Client.Fails("set_scatter", new { @object = "Terrain", of = "Tree", density_map = "missing.png" }));
        Assert.Contains("of", await s.Client.Fails("set_scatter", new { @object = "Terrain" }));
        if (Environment.GetEnvironmentVariable("AVALIGHTWAVE_SHOT_OUT") is { Length: > 0 } dir)
        {
            await s.Client.Json("set_scatter", new { @object = "Terrain", of = "Tree", count = 40, seed = 3, on_surface = "Grass", scale = new[] { 0.5, 1.5 }, sink = 0.1 });
            await s.Client.Json("set_item", new { item = "Tree", visible = false });
            await s.Client.Json("set_sky", new { heading = 220, elevation = 18 });
            await s.Client.Json("frame_items", new { items = new[] { "Terrain" } });
            var pic = await s.Client.Call("render_scene", new { frame = 1, width = 480, samples = 2 });
            File.WriteAllBytes(Path.Combine(dir, "terrain-trees-sky.png"), pic.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());
        }
    }

    [Fact]
    public void The_sun_rises_in_the_east_peaks_at_noon_in_the_south_and_sets_in_the_west()
    {
        var morning = SunPosition.At(8, 40, 172);
        var noon = SunPosition.At(12, 40, 172);
        var evening = SunPosition.At(16, 40, 172);
        var night = SunPosition.At(0, 40, 172);
        Assert.InRange(morning.Heading, 45, 135);           // east of north
        Assert.Equal(180, noon.Heading, 1);                  // due south from the north
        Assert.Equal(90 - 40 + 23.44, noon.Elevation, 0);    // 73.4° at midsummer, 40° north
        Assert.InRange(evening.Heading, 225, 315);
        Assert.True(night.Elevation < 0);
        Assert.Equal(morning.Elevation, evening.Elevation, 3);
        Assert.Equal(0, SunPosition.Light(-10).Intensity);
        Assert.True(SunPosition.Light(60).Intensity > 1);
        Assert.True(SunPosition.Light(5).Colour.Z < SunPosition.Light(60).Colour.Z);
    }

    [Fact]
    public async Task The_sky_sets_the_environment_and_a_sun_light_shining_from_it()
    {
        using var b = new Bench();
        await using var s = await b.Serve();
        await s.Client.Json("make_object", new { shape = "ground" });
        await s.Client.Json("make_object", new { shape = "box", position = new[] { 0.0, 0, 0 } });
        var r = await s.Client.Json("set_sky", new { heading = 90, elevation = 30 });
        Assert.StartsWith("builtin:sun?", b.Scene.Environment.Image);
        var sun = b.Scene.Items.OfType<LightItem>().Single(x => x.Name == "Sun");
        Assert.Equal(LightType.Distant, sun.Type);
        // It shines from the east (+X), down at 30°.
        var along = sun.WorldMatrix(0).TransformVector(Vec3.UnitZ).Normalized();
        Assert.Equal(-Math.Cos(Math.PI / 6), along.X, 3);
        Assert.Equal(-0.5, along.Y, 3);
        Assert.Equal("Agent: set the sky", b.Host.Layout.History.UndoLabel);

        // By the clock: the same light turned, no second one; dusk dims it.
        r = await s.Client.Json("set_sky", new { time_of_day = 20.5, latitude = 52, day_of_year = 172 });
        Assert.Single(b.Scene.Items.OfType<LightItem>(), x => x.Name == "Sun");
        Assert.True((double)r["elevation"]! < 5);
        Assert.True(sun.Intensity.Evaluate(0) < 0.5);
        var pic = await s.Client.Call("render_scene", new { frame = 1, width = 160, samples = 1 });
        Assert.True(pic.Content.OfType<ImageContentBlock>().Any(), string.Join(" ", pic.Content.OfType<TextContentBlock>().Select(x => x.Text)));

        Assert.Contains("not both", await s.Client.Fails("set_sky", new { time_of_day = 9, heading = 10 }));
        Assert.Contains("turbidity", await s.Client.Fails("set_sky", new { turbidity = 50 }));
        await s.Client.Json("undo_scene");
        await s.Client.Json("undo_scene");
        Assert.False(b.Scene.Environment.On);
    }
}
