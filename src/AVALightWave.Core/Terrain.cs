using AVAModeler.Core;

namespace AVALightWave;

/// <summary>How a terrain is made: its size, height, detail and shape, and where grass gives way to rock and snow.</summary>
public sealed record TerrainSettings
{
    /// <summary>Metres across X and Z.</summary>
    public double Width { get; init; } = 100;
    public double Depth { get; init; } = 100;
    /// <summary>Metres from the lowest point (at 0) to the highest.</summary>
    public double Height { get; init; } = 12;
    /// <summary>Squares a side along the longer edge (the other in proportion).</summary>
    public int Resolution { get; init; } = 64;
    public int Seed { get; init; }
    /// <summary>The size of the biggest hills, metres.</summary>
    public double Feature { get; init; } = 40;
    /// <summary>How much the smaller bumps count, 0 (smooth) to 1 (rough).</summary>
    public double Roughness { get; init; } = 0.5;
    /// <summary>Hills (rolling) or mountains (sharp ridges).</summary>
    public bool Ridged { get; init; }
    /// <summary>Passes of weathering: steep drops crumble into the slopes below them.</summary>
    public int Erosion { get; init; } = 10;
    /// <summary>Grass, rock and snow surfaces (else one "Terrain" surface).</summary>
    public bool Layered { get; init; } = true;
    /// <summary>Snow above this share of the height, 0 to 1 (more than 1: none).</summary>
    public double SnowLine { get; init; } = 0.8;
    /// <summary>Rock where the ground slopes more than this, degrees.</summary>
    public double RockSlope { get; init; } = 35;

    public const int MaxResolution = 400;

    public TerrainSettings Checked()
    {
        if (!(Width > 0) || !(Depth > 0) || Width > 100_000 || Depth > 100_000) throw new ArgumentException("A terrain's size must be more than 0 metres each way.");
        if (!(Height >= 0) || Height > 100_000) throw new ArgumentException("A terrain's height must be 0 or more metres.");
        if (Resolution < 2 || Resolution > MaxResolution) throw new ArgumentException($"A terrain's resolution must be from 2 to {MaxResolution} squares a side.");
        if (!(Feature > 0)) throw new ArgumentException("A terrain's feature size must be more than 0 metres.");
        if (!(Roughness >= 0) || Roughness > 1) throw new ArgumentException("A terrain's roughness must be from 0 to 1.");
        if (Erosion < 0 || Erosion > 200) throw new ArgumentException("A terrain's erosion must be from 0 to 200 passes.");
        if (!(RockSlope > 0) || RockSlope > 90) throw new ArgumentException("rock_slope must be more than 0 and at most 90 degrees.");
        if (!double.IsFinite(SnowLine)) throw new ArgumentException("snow_line must be a share of the height (0 to 1; more than 1 for no snow).");
        return this;
    }
}

/// <summary>Terrains: a heightfield from Perlin's noise, weathered, as a grid of quads with grass, rock and snow surfaces.</summary>
public static class Terrain
{
    public static readonly string[] SurfaceNames = ["Grass", "Rock", "Snow"];

    /// <summary>The heights on a grid of (nx + 1) × (nz + 1) points, the lowest 0 and the highest the settings' height.</summary>
    public static double[,] Heights(TerrainSettings t, out int nx, out int nz)
    {
        t = t.Checked();
        double longer = Math.Max(t.Width, t.Depth);
        nx = Math.Max(2, (int)Math.Round(t.Resolution * t.Width / longer));
        nz = Math.Max(2, (int)Math.Round(t.Resolution * t.Depth / longer));
        var h = new double[nx + 1, nz + 1];
        var shift = new Vec3(t.Seed * 17.31, t.Seed * 3.17 + 0.5, t.Seed * -11.7);
        int octaves = 7;
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                var p = new Vec3(t.Width * i / nx / t.Feature, 0.37, t.Depth * j / nz / t.Feature) + shift;
                h[i, j] = t.Ridged ? 1 - Noise.Turbulence(p, octaves, t.Roughness) : Noise.Fractal(p, octaves, t.Roughness);
            }
        if (t.Ridged)
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= nz; j++) h[i, j] *= h[i, j]; // sharpen the ridges, flatten the valleys
        Normalise(h, 1);
        // Weathering: material falls from a point to a lower neighbour where the drop is steeper than it can hold.
        double cell = Math.Max(t.Width / nx, t.Depth / nz);
        double talus = 0.6 * cell / Math.Max(t.Height, 1e-9); // in normalised height, about 30° at full height
        int[] di = [1, -1, 0, 0], dj = [0, 0, 1, -1];
        for (int pass = 0; pass < t.Erosion; pass++)
        {
            var move = new double[nx + 1, nz + 1];
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= nz; j++)
                    for (int k = 0; k < 4; k++)
                    {
                        int a = i + di[k], b = j + dj[k];
                        if (a < 0 || b < 0 || a > nx || b > nz) continue;
                        double drop = h[i, j] - h[a, b];
                        if (drop <= talus) continue;
                        double m = 0.125 * (drop - talus);
                        move[i, j] -= m;
                        move[a, b] += m;
                    }
            for (int i = 0; i <= nx; i++)
                for (int j = 0; j <= nz; j++) h[i, j] += move[i, j];
        }
        Normalise(h, t.Height);
        return h;
    }

    private static void Normalise(double[,] h, double top)
    {
        double lo = double.MaxValue, hi = double.MinValue;
        foreach (var v in h) { lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        double span = hi - lo;
        for (int i = 0; i < h.GetLength(0); i++)
            for (int j = 0; j < h.GetLength(1); j++) h[i, j] = span > 1e-12 ? (h[i, j] - lo) / span * top : 0;
    }

    /// <summary>
    /// The terrain as an object: a grid centred on the origin in X and Z, its lowest point at 0, quads wound
    /// LightWave's way (clockwise seen from above), each on grass, rock (steep) or snow (high).
    /// </summary>
    public static Document Make(TerrainSettings t)
    {
        var h = Heights(t, out int nx, out int nz);
        var doc = new Document();
        doc.Surfaces.Clear();
        if (t.Layered)
        {
            doc.Surfaces.Add(new Surface("Grass") { Color = new Rgb(78, 112, 52), Diffuse = 0.85, Smoothing = true });
            doc.Surfaces.Add(new Surface("Rock") { Color = new Rgb(112, 104, 96), Diffuse = 0.8, Specular = 0.05, Smoothing = true });
            doc.Surfaces.Add(new Surface("Snow") { Color = new Rgb(242, 244, 250), Diffuse = 0.95, Specular = 0.2, Smoothing = true });
        }
        else doc.Surfaces.Add(new Surface("Terrain") { Color = new Rgb(120, 112, 90), Diffuse = 0.85, Smoothing = true });
        var mesh = doc.Layers[0].Mesh;
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
                mesh.Points.Add(new Vec3(-t.Width / 2 + t.Width * i / nx, h[i, j], -t.Depth / 2 + t.Depth * j / nz));
        int At(int i, int j) => i * (nz + 1) + j;
        double rockCos = Math.Cos(t.RockSlope * Math.PI / 180);
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                int[] q = [At(i, j), At(i, j + 1), At(i + 1, j + 1), At(i + 1, j)];
                int surface = 0;
                if (t.Layered)
                {
                    var p = q.Select(k => mesh.Points[k]).ToArray();
                    var n = Vec3.Cross(p[2] - p[0], p[3] - p[1]);
                    double up = n.Length > 1e-12 ? Math.Abs(n.Y) / n.Length : 1;
                    double mid = p.Average(x => x.Y);
                    if (t.Height > 0 && mid > t.SnowLine * t.Height) surface = 2;
                    else if (up < rockCos) surface = 1;
                }
                mesh.Polygons.Add(new Polygon(q, surface));
            }
        return doc;
    }
}

/// <summary>Where the sun is: from a time of day, a latitude and a day of the year, as a heading and elevation.</summary>
public static class SunPosition
{
    /// <summary>
    /// The sun's heading (degrees round from north, +Z, toward east, +X) and elevation (degrees above the horizon) at
    /// <paramref name="hours"/> local solar time (12 is noon), <paramref name="latitude"/> degrees (north positive) and
    /// <paramref name="dayOfYear"/> (1 to 365).
    /// </summary>
    public static (double Heading, double Elevation) At(double hours, double latitude, double dayOfYear)
    {
        const double D = Math.PI / 180;
        double decl = 23.44 * D * Math.Sin(2 * Math.PI * (284 + dayOfYear) / 365);
        double hour = 15 * (hours - 12) * D, lat = latitude * D;
        double sinE = Math.Sin(lat) * Math.Sin(decl) + Math.Cos(lat) * Math.Cos(decl) * Math.Cos(hour);
        double e = Math.Asin(Math.Clamp(sinE, -1, 1));
        double az = Math.Atan2(-Math.Sin(hour) * Math.Cos(decl), Math.Cos(lat) * Math.Sin(decl) - Math.Sin(lat) * Math.Cos(decl) * Math.Cos(hour));
        double heading = az / D;
        if (heading < 0) heading += 360;
        return (Math.Round(heading, 3), Math.Round(e / D, 3));
    }

    /// <summary>The sunlight's colour and strength at an elevation: white and full high up, orange and dim low, none at night.</summary>
    public static (Vec3 Colour, double Intensity) Light(double elevation)
    {
        double up = Math.Sin(elevation * Math.PI / 180);
        double t = Smooth(0, 0.5, up);
        var colour = Vec3.Lerp(new Vec3(1.0, 0.55, 0.3), new Vec3(1.0, 0.97, 0.92), t);
        return (colour, Math.Round(1.1 * Smooth(-0.03, 0.15, up), 4));
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
