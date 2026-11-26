namespace VFlasher.Core;

/// <summary>
/// Finds image manifests, either in a local images/ folder or online.
/// </summary>
public static class ManifestCatalog
{
    public const string DefaultRemoteBase =
        "https://raw.githubusercontent.com/developer1-vd/QC-and-Arduino-noubuntu/main/images/";

    /// <summary>The list of manifests, one "manufacturer/board/os" per line.</summary>
    public const string IndexFile = "index.txt";

    /// <summary>Reads every images/&lt;manufacturer&gt;/&lt;board&gt;/&lt;os&gt;/image.conf below <paramref name="imagesDir"/>.</summary>
    public static List<ImageManifest> LoadLocal(string imagesDir)
    {
        var result = new List<ImageManifest>();
        foreach (var file in Directory.EnumerateFiles(imagesDir, "image.conf", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(imagesDir, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (rel.Length != 4)
                continue;
            result.Add(ImageManifest.Parse(rel[0], rel[1], rel[2], File.ReadAllText(file)));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return result;
    }

    /// <summary>Reads index.txt and each manifest it lists from <paramref name="baseUrl"/>.</summary>
    public static async Task<List<ImageManifest>> LoadRemoteAsync(HttpClient http, string baseUrl = DefaultRemoteBase,
        CancellationToken ct = default)
    {
        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";
        var index = await http.GetStringAsync(baseUrl + IndexFile, ct);
        var result = new List<ImageManifest>();
        foreach (var id in ParseIndex(index))
        {
            var parts = id.Split('/');
            var text = await http.GetStringAsync($"{baseUrl}{id}/image.conf", ct);
            result.Add(ImageManifest.Parse(parts[0], parts[1], parts[2], text));
        }
        return result;
    }

    public static List<string> ParseIndex(string text)
    {
        var ids = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('/');
            if (parts.Length != 3 || !parts.All(ImageManifest.IsValidName))
                throw new FormatException($"bad index entry '{line}'");
            ids.Add(line);
        }
        return ids;
    }

    /// <summary>
    /// Looks for an images folder next to the program, then in the parent folders
    /// (so a build run from inside the repository finds the repository's images/).
    /// </summary>
    public static string? FindLocalImagesDir(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "images");
            if (File.Exists(Path.Combine(candidate, IndexFile)))
                return candidate;
        }
        return null;
    }
}
