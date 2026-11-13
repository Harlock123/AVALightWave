using System.IO.Compression;
using AVAModeler.Core;
using AVAModeler.Formats;
using AVAModeler.Scene;

namespace AVALightWave.Core;

/// <summary>What packaging a scene made: the scene file, the folder (or zip), every file written, and anything left out.</summary>
public sealed record PackageResult(string Scene, string Folder, string? Zip, IReadOnlyList<string> Files, IReadOnlyList<string> Warnings);

/// <summary>
/// Package Scene: the scene and everything it uses gathered into one folder (or a .zip), so it opens on another
/// machine. The scene is written as .avascene with its meshes inside; a copy of each object's own file goes in
/// <c>objects/</c> (and an object with no file is saved there as .lwo), and every image it uses (textures, the
/// environment, the Images panel's pictures and whole numbered sequences, compositing) in <c>images/</c>, with the
/// scene's paths pointing at the copies. The open scene is never changed: the package is made from a copy.
/// </summary>
public static class ScenePackage
{
    public static PackageResult Make(Scene scene, string folder, string name, string? sceneFolder = null, bool zip = false, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ArgumentException("The package's name must be a plain file name, e.g. \"robot\".");
        folder = Path.GetFullPath(folder);
        string? zipPath = zip ? folder.TrimEnd(Path.DirectorySeparatorChar) + ".zip" : null;
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            if (!overwrite) throw new IOException($"{folder} already has files in it: choose an empty folder (or give overwrite).");
            Directory.Delete(folder, true);
        }
        if (zipPath != null && File.Exists(zipPath) && !overwrite) throw new IOException($"{zipPath} already exists (give overwrite to replace it).");
        Directory.CreateDirectory(folder);

        var copy = scene.Copy(d => d.Copy());
        var files = new List<string>();
        var warnings = new List<string>();
        var copied = new Dictionary<string, string>(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A file copied once into a sub-folder under a free name; returns its full path there.
        string Copy(string source, string sub)
        {
            source = Path.GetFullPath(source);
            if (copied.TryGetValue(source, out var had)) return had;
            string dir = Path.Combine(folder, sub);
            Directory.CreateDirectory(dir);
            string file = Path.GetFileName(source);
            string target = Path.Combine(dir, file);
            for (int i = 2; !taken.Add(target) || File.Exists(target); i++)
                target = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)} {i}{Path.GetExtension(file)}");
            File.Copy(source, target);
            files.Add(target);
            return copied[source] = target;
        }

        // Textures first, while each object's own file still says where its images are.
        var docs = new HashSet<Document>(ReferenceEqualityComparer.Instance);
        foreach (var o in copy.Objects)
        {
            if (o.Document is not { } doc || !docs.Add(doc)) continue;
            string? from = TextureImages.BaseOf(o.SourcePath ?? doc.FilePath) ?? sceneFolder;
            foreach (var s in doc.Surfaces)
                foreach (var t in s.Textures)
                {
                    if (!t.IsImage || string.IsNullOrWhiteSpace(t.Image)) continue;
                    if (TextureImages.Resolve(t.Image, from) is { } file) t.Image = "images/" + Path.GetFileName(Copy(file, "images"));
                    else warnings.Add($"{o.Name}'s surface {s.Name}: the image {t.Image} wasn't found, so it's left out.");
                }
        }
        // Each object's own file, for editing elsewhere; the scene keeps the mesh inside itself, so it opens without it.
        var objectFiles = new Dictionary<Document, string>(ReferenceEqualityComparer.Instance);
        foreach (var o in copy.Objects)
        {
            if (o.Document is not { } doc) continue;
            if (!objectFiles.ContainsKey(doc))
            {
                string? source = o.SourcePath ?? doc.FilePath;
                if (source != null && File.Exists(source) && !doc.Modified) objectFiles[doc] = Copy(source, "objects");
                else
                {
                    string dir = Path.Combine(folder, "objects");
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, Safe(o.Name) + ".lwo");
                    for (int i = 2; !taken.Add(target) || File.Exists(target); i++) target = Path.Combine(dir, $"{Safe(o.Name)} {i}.lwo");
                    ObjectFiles.Save(doc, target);
                    files.Add(target);
                    objectFiles[doc] = target;
                }
            }
            // No path of this machine's goes in the package: the mesh is embedded.
            o.SourcePath = null;
            doc.FilePath = null;
        }

        // The Images panel's pictures (whole sequences), with compositing pointing at the copies.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var images = new List<SceneImage>();
        foreach (var image in copy.Images)
        {
            string full = SceneImages.Full(image.Path);
            if (image.Sequence)
            {
                var frames = ImageSequence.Files(image);
                if (frames.Count == 0) { warnings.Add($"The image sequence {image.Path} wasn't found, so it's left out."); continue; }
                var all = frames.OrderBy(x => x.Key).Select(f => Copy(f.Value, "images")).ToList();
                var moved = image with { Path = all[0] };
                renamed[image.Path] = moved.Path;
                images.Add(moved);
            }
            else if (File.Exists(full))
            {
                var moved = image with { Path = Copy(full, "images") };
                renamed[image.Path] = moved.Path;
                images.Add(moved);
            }
            else warnings.Add($"The image {image.Path} wasn't found, so it's left out.");
        }
        copy.Images = images;
        string Moved(string path) => path.Length == 0 ? path : renamed.TryGetValue(path, out var p) ? p
            : File.Exists(path) ? Copy(path, "images") : path;
        copy.Compositing = copy.Compositing with
        {
            Background = Moved(copy.Compositing.Background),
            Foreground = Moved(copy.Compositing.Foreground),
            ForegroundAlpha = Moved(copy.Compositing.ForegroundAlpha),
        };

        var env = copy.Environment;
        if (env.On && !env.Image.Trim().StartsWith(EnvironmentMap.BuiltinPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (EnvironmentMap.Resolve(env.Image, sceneFolder) is { } file) copy.Environment = env with { Image = Copy(file, "images") };
            else
            {
                warnings.Add($"The environment image {env.Image} wasn't found, so it's left out.");
                copy.Environment = env with { Image = "" };
            }
        }

        string scenePath = Path.Combine(folder, name + ".avascene");
        SceneJson.WriteFile(copy, scenePath, embedMeshes: true);
        files.Insert(0, scenePath);
        File.WriteAllText(Path.Combine(folder, "README.txt"),
            $"{name}: a scene packaged by AVALightWave.\n\nOpen {name}.avascene (its objects are inside it).\nobjects/ has each object's own file, for editing in AVA Modeler or elsewhere.\nimages/ has every picture the scene uses.\n");
        files.Add(Path.Combine(folder, "README.txt"));
        if (zipPath != null)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(folder, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
        }
        return new PackageResult(scenePath, folder, zipPath, files, warnings);
    }

    private static string Safe(string name)
    {
        var s = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim();
        return s.Length == 0 ? "object" : s;
    }
}
