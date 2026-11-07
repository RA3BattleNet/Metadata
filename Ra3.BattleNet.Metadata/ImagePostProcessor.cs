using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

public static class ImagePostProcessor
{
    private static readonly HashSet<string> Convertible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif"
    };

    public static int ApplyWebP(string outputDir, TextWriter? log = null) =>
        ApplyWebP(outputDir, ["metadata.xml"], log);

    internal static int ApplyWebP(string outputDir, IEnumerable<string> documentPaths, TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(documentPaths);
        log ??= Console.Out;
        outputDir = Path.GetFullPath(outputDir);
        var documents = documentPaths.Select(path => Path.GetFullPath(Path.Combine(outputDir, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, path =>
            {
                EnsureInside(path, outputDir);
                return XDocument.Load(path);
            }, StringComparer.OrdinalIgnoreCase);
        var converted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registrations = new List<(XAttribute Attribute, string Document, string Source)>();
        foreach (var (path, document) in documents)
        {
            foreach (var image in document.Descendants("Image"))
            {
                if (image.Attribute("ID") is null || !string.IsNullOrWhiteSpace((string?)image.Attribute("Url"))
                    || image.Attribute("Source") is not { } source || string.IsNullOrWhiteSpace(source.Value))
                    continue;
                var uri = MetadataResourceUri.Resolve(path, source.Value);
                if (!uri.IsFile)
                    continue;
                var absolute = Path.GetFullPath(uri.LocalPath);
                EnsureInside(absolute, outputDir);
                if (!File.Exists(absolute) || !Convertible.Contains(Path.GetExtension(absolute)))
                    continue;
                var target = Path.ChangeExtension(absolute, ".webp");
                if (destinations.TryGetValue(target, out var previous) && !StringComparer.OrdinalIgnoreCase.Equals(previous, absolute)
                    || File.Exists(target))
                    throw new InvalidOperationException($"WebP output path collision: {target}");
                destinations[target] = absolute;
                converted.TryAdd(absolute, target);
                registrations.Add((source, path, absolute));
            }
        }
        foreach (var (source, target) in converted)
        {
            log.WriteLine($"  convert {Path.GetRelativePath(outputDir, source)} -> {Path.GetRelativePath(outputDir, target)}");
            ImagingInvoker.ConvertToWebP(source, target);
        }
        foreach (var (attribute, document, source) in registrations)
            attribute.Value = Path.GetRelativePath(Path.GetDirectoryName(document)!, converted[source]).Replace('\\', '/');
        foreach (var (path, document) in documents)
            document.Save(path);
        foreach (var source in converted.Keys)
            File.Delete(source);
        return converted.Count;
    }

    private static void EnsureInside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"Image output escaped root: {path}");
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Image paths must not traverse links: {current}");
        }
    }
}
