using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

public static class MetadataExporter
{
    public static void Export(MetadataReadResult input, string outputDirectory, MetadataExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var output = Path.GetFullPath(outputDirectory);
        EnsureSeparate(input, output);
        try
        {
            var session = new ExportSession(input, output, options ?? new MetadataExportOptions());
            session.Plan();
            ClearOutput(output);
            session.Write();
        }
        catch
        {
            ClearOutput(output);
            throw;
        }
    }
    internal static IReadOnlyList<string> ExportLegacy(MetadataReadResult input, string outputDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        EnsureSeparate(input, output);
        var session = new ExportSession(input, output, new MetadataExportOptions(), legacyPublish: true);
        session.Plan();
        session.Write();
        return session.XmlPaths;
    }


    private static void EnsureSeparate(MetadataReadResult input, string output) => ValidateOutput(input.SourceRoot, output);

    internal static void ValidateOutput(string sourceRoot, string output)
    {
        output = Path.GetFullPath(output);
        EnsureNoLinks(output);
        if (Uri.TryCreate(sourceRoot, UriKind.Absolute, out var uri) && !uri.IsFile)
            return;
        var source = Path.GetFullPath(uri?.IsFile == true ? uri.LocalPath : sourceRoot);
        if (IsWithin(source, output) || IsWithin(output, source))
            throw new InvalidOperationException("Export output must not overlap the input source root.");
    }

    private static bool IsWithin(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Export paths must not traverse links: {current}");
        }
    }

    private static void ClearOutput(string output)
    {
        if (Directory.Exists(output))
            Directory.Delete(output, recursive: true);
    }

    private sealed class ExportSession
    {
        private readonly MetadataReadResult input;
        private readonly string output;
        private readonly MetadataExportOptions options;
        private readonly bool legacyPublish;
        private readonly Dictionary<string, MetadataDocumentInfo> documents;
        private readonly Dictionary<string, XDocument> xml = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> emitted;
        private readonly Dictionary<string, IReadOnlyList<XElement>> manifestCandidates;
        private readonly HashSet<string> prepared = new(StringComparer.OrdinalIgnoreCase);

        internal ExportSession(MetadataReadResult input, string output, MetadataExportOptions options, bool legacyPublish = false)
        {
            if (!Enum.IsDefined(options.Layout) || !Enum.IsDefined(options.IdFormat))
                throw new ArgumentOutOfRangeException(nameof(options));
            if (options.Layout == MetadataExportLayout.Mixed && options.Inline is null)
                throw new ArgumentException("Mixed layout requires an Inline predicate.", nameof(options));
            this.input = input;
            this.output = output;
            this.options = options;
            this.legacyPublish = legacyPublish;
            var comparer = MetadataReader.PathComparer;
            documents = input.Documents.ToDictionary(document => document.Path, comparer);
            emitted = new HashSet<string>(comparer);
            manifestCandidates = new Dictionary<string, IReadOnlyList<XElement>>(comparer);
        }
        internal IReadOnlyList<string> XmlPaths => xml.Keys.Select(path => Path.Combine(output, path)).ToArray();


        internal void Plan()
        {
            var document = Compose(input.EntryPath, "metadata.xml", new HashSet<string>(documents.Comparer));
            if (options.Layout == MetadataExportLayout.Flat)
                MetadataInheritance.Resolve(document.Root!);
            foreach (var attribute in input.Document.Root!.Attributes())
                document.Root!.SetAttributeValue(attribute.Name, attribute.Value);
            AddXml("metadata.xml", input.EntryPath, document);
            foreach (var (destination, planned) in xml.ToList())
                Prepare(planned.Root!, destination, manifestLeaf: destination != "metadata.xml"
                    && planned.Root!.Elements().All(element => element.Name.LocalName is "Manifest" or "Tags"));
            if (options.IdFormat == MetadataExportIdFormat.Path)
            {
                foreach (var node in xml.Values.SelectMany(planned => planned.Descendants()))
                {
                    if (IsRegistration(node))
                        node.SetAttributeValue("ID", MetadataId.EncodePath((string)node.Attribute("ID")!));
                    else if (IsReference(node) && !string.IsNullOrWhiteSpace(node.Value))
                        node.Value = MetadataId.EncodePath(node.Value.Trim());
                }
            }
            foreach (var path in owners.Keys)
            {
                var parent = Path.GetDirectoryName(path);
                while (!string.IsNullOrEmpty(parent))
                {
                    if (owners.ContainsKey(parent.Replace('\\', '/')))
                        throw new InvalidOperationException($"Output file/directory collision: {path}");
                    parent = Path.GetDirectoryName(parent);
                }
            }
        }

        private XDocument Compose(string path, string destination, HashSet<string> expanding)
        {
            if (!expanding.Add(path))
                throw new InvalidOperationException($"Include cycle during export: {path}");
            var info = documents[path];
            var document = new XDocument(info.NormalizedDocument);
            var root = document.Root ?? throw new InvalidOperationException($"Missing Metadata root: {path}");
            foreach (var include in root.Descendants("Include").ToList())
            {
                var target = (string?)include.Attribute("Source")
                    ?? throw new InvalidOperationException("Include has no Source.");
                var child = documents[target];
                var inline = options.Layout == MetadataExportLayout.Flat
                    || options.Layout == MetadataExportLayout.Mixed && options.Inline!(child.RelativePath);
                if (inline)
                {
                    if (!emitted.Add(target))
                    {
                        include.Remove();
                        continue;
                    }
                    var nested = Compose(target, destination, expanding);
                    var anchor = include.Parent?.Name.LocalName == "Includes" ? include.Parent : include;
                    anchor.AddBeforeSelf(nested.Root!.Elements().Where(element => element.Name.LocalName != "Tags")
                        .Select(element => new XElement(element)));
                    include.Remove();
                }
                else
                {
                    var childDestination = RelativeSource(target);
                    if (emitted.Add(target))
                        AddXml(childDestination, target, Compose(target, childDestination, expanding));
                    include.SetAttributeValue("Source", RelativeTo(destination, childDestination));
                }
            }
            foreach (var includes in root.Descendants("Includes").Where(element => !element.HasElements).ToList())
                includes.Remove();
            expanding.Remove(path);
            return document;
        }

        private void Prepare(XElement root, string destination, bool manifestLeaf = false)
        {
            if (!prepared.Add(destination))
                return;
            string? format = null;
            if (!legacyPublish)
                format = options.IdFormat == MetadataExportIdFormat.Path ? "path" : "legacy";
            root.SetAttributeValue("IdFormat", format);
            root.SetAttributeValue("SourceBase", legacyPublish ? null : "document");
            foreach (var registration in root.Descendants().Where(IsRegistration).ToList())
            {
                if (registration.Name.LocalName == "Image" && !string.IsNullOrWhiteSpace((string?)registration.Attribute("Url")))
                    continue;
                var source = (string?)registration.Attribute("Source");
                if (legacyPublish && registration.Name.LocalName == "Manifest" && IsFullManifest(registration)
                    && string.IsNullOrWhiteSpace(source)
                    && input.Document.Descendants("Manifest").Any(element =>
                        string.Equals((string?)element.Attribute("ID"), (string?)registration.Attribute("ID"), StringComparison.OrdinalIgnoreCase)
                        && element.Attribute("Source") is null))
                    continue;
                if (registration.Name.LocalName == "Manifest" && !manifestLeaf)
                {
                    var id = (string)registration.Attribute("ID")!;
                    XElement leaf;
                    if (IsFullManifest(registration))
                        leaf = new XElement(registration);
                    else
                        leaf = Manifest(source!, id);
                    leaf.Attribute("Source")?.Remove();
                    if (options.InlineManifests)
                    {
                        registration.ReplaceWith(leaf);
                    }
                    else
                    {
                        var originalSource = source;
                        if (string.IsNullOrWhiteSpace(originalSource))
                        {
                            var registrationSource = input.Document.Descendants("Manifest").FirstOrDefault(element =>
                                string.Equals((string?)element.Attribute("ID"), id, StringComparison.OrdinalIgnoreCase));
                            if (registrationSource?.Attribute("Source") is { } original)
                                originalSource = input.Source.GetFullPath(input.Source.Combine(input.Source.GetDirectoryName(input.EntryPath)!, original.Value));
                        }
                        var leafDestination = string.IsNullOrWhiteSpace(originalSource)
                            ? "manifests/" + SafeSegment(id) + ".xml" : RelativeSource(originalSource);
                        if (string.Equals(leafDestination, destination, StringComparison.OrdinalIgnoreCase)
                            || xml.TryGetValue(leafDestination, out var existingDocument)
                            && existingDocument.Root!.Elements().Any(element => element.Name.LocalName is not ("Manifest" or "Tags")))
                            leafDestination = "manifests/" + SafeSegment(id) + ".xml";
                        if (!xml.TryGetValue(leafDestination, out var leafDocument))
                        {
                            leafDocument = new XDocument(new XElement("Metadata", leaf));
                            Prepare(leafDocument.Root!, leafDestination, manifestLeaf: true);
                            AddXml(leafDestination, originalSource ?? "manifest:" + id, leafDocument);
                        }
                        else
                        {
                            if (!leafDocument.Root!.Elements("Manifest").Any(element =>
                                string.Equals((string?)element.Attribute("ID"), id, StringComparison.OrdinalIgnoreCase)))
                            {
                                var addition = new XDocument(new XElement("Metadata", leaf));
                                prepared.Remove(leafDestination);
                                Prepare(addition.Root!, leafDestination, manifestLeaf: true);
                                leafDocument.Root.Add(addition.Root!.Elements());
                            }
                        }
                        registration.RemoveNodes();
                        registration.ReplaceAttributes(new XAttribute("ID", id),
                            new XAttribute("Source", RelativeTo(destination, leafDestination)));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(source))
                {
                    var assetDestination = RelativeSource(source);
                    if (xml.ContainsKey(assetDestination))
                        throw new InvalidOperationException($"Resource/XML path collision: {assetDestination}");
                    Claim(assetDestination, source);
                    files.TryAdd(assetDestination, source);
                    registration.SetAttributeValue("Source", RelativeTo(destination, assetDestination));
                }
            }
            if (options.ResolveVariables)
                new VariableResolver().ReplaceInElement(root);
        }

        private static bool IsFullManifest(XElement manifest) =>
            manifest.HasElements || string.IsNullOrWhiteSpace((string?)manifest.Attribute("Source"));

        private XElement Manifest(string source, string id)
        {
            if (!manifestCandidates.TryGetValue(source, out var full))
            {
                IEnumerable<XElement> candidates;
                if (documents.TryGetValue(source, out var captured) && captured.Includes.Count == 0
                    && captured.NormalizedDocument.Root!.Elements("Manifest").Any(IsFullManifest))
                    candidates = captured.NormalizedDocument.Root!.Elements("Manifest");
                else
                {
                    RelativeSource(source);
                    var leaf = MetadataReader.Read(source, new MetadataSourceOptions
                    {
                        SourceRoot = MetadataReader.IsHttp(source) && !MetadataReader.IsHttp(input.SourceRoot)
                            ? new Uri(new Uri(source), ".").AbsoluteUri : input.SourceRoot,
                        ForceSourceTree = true,
                        InlineManifests = true,
                        CustomSource = input.Source is HttpXmlSource or PhysicalFileXmlSource ? null : input.Source
                    });
                    var normalized = leaf.Document;
                    foreach (var registration in normalized.Descendants().Where(IsRegistration))
                    {
                        if (registration.Attribute("Source") is { } attribute)
                        {
                            var uri = MetadataResourceUri.Resolve(source, attribute.Value);
                            attribute.Value = uri.IsFile ? uri.LocalPath : uri.AbsoluteUri;
                        }
                    }
                    candidates = normalized.Root!.Elements("Manifest");
                }
                full = candidates.Where(IsFullManifest).ToArray();
                manifestCandidates.Add(source, full);
            }
            var matches = full.Where(manifest => string.Equals((string?)manifest.Attribute("ID"), id,
                StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
                matches = full.Where(manifest => string.Equals(MetadataId.LocalId((string?)manifest.Attribute("ID")),
                    MetadataId.LocalId(id), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new InvalidOperationException($"Expected one full manifest for {id} in {source}.");
            var result = new XElement(matches[0]);
            result.SetAttributeValue("ID", id);
            result.Attribute("Source")?.Remove();
            return result;
        }


        private string RelativeSource(string source)
        {
            string relative;
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                var root = Uri.TryCreate(input.SourceRoot, UriKind.Absolute, out var rootUri) ? rootUri : null;
                var baseUri = root?.Scheme is "http" or "https" ? new Uri(root.AbsoluteUri.TrimEnd('/') + "/") : null;
                if (baseUri?.IsBaseOf(uri) == true)
                    relative = baseUri.MakeRelativeUri(uri).ToString();
                else
                    relative = "remote/" + SafeSegment(uri.GetLeftPart(UriPartial.Authority)) + "/"
                        + uri.AbsolutePath.TrimStart('/');
                if (uri.Query.Length != 0)
                    relative += ".query-" + SafeSegment(uri.Query);
            }
            else
            {
                relative = input.Source.GetRelativePath(input.SourceRoot, source).Replace('\\', '/');
                EnsureNoLinks(uri?.IsFile == true ? uri.LocalPath : source);
            }
            ValidateRelative(relative);
            return relative;
        }

        private static string SafeSegment(string value) =>
            Uri.EscapeDataString(value).Replace("~", "~7E", StringComparison.Ordinal).Replace("%", "~", StringComparison.Ordinal);

        private static void ValidateRelative(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
                || Uri.TryCreate(relative, UriKind.Absolute, out _)
                || relative.Split('/').Any(segment => segment is "" or "." or ".."
                    || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidOperationException($"Unsafe export path: {relative}");
        }

        private void Claim(string destination, string owner)
        {
            ValidateRelative(destination);
            if (owners.TryGetValue(destination, out var existing) && !documents.Comparer.Equals(existing, owner))
                throw new InvalidOperationException($"Output path collision: {destination}");
            owners[destination] = owner;
        }

        private void AddXml(string destination, string owner, XDocument document)
        {
            Claim(destination, owner);
            if (files.ContainsKey(destination))
                throw new InvalidOperationException($"Resource/XML path collision: {destination}");
            xml[destination] = document;
        }

        internal void Write()
        {
            Directory.CreateDirectory(output);
            using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
            foreach (var (relative, source) in files)
            {
                var target = Target(relative);
                if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile)
                {
                    if (uri.Scheme is not ("http" or "https"))
                        throw new InvalidOperationException($"Unsupported resource URI: {source}");
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    using var response = client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                    if ((int)response.StatusCode is >= 300 and < 400)
                        throw new InvalidOperationException($"Resource redirect refused: {source}");
                    response.EnsureSuccessStatusCode();
                    using var body = response.Content.ReadAsStream();
                    using var file = File.Create(target);
                    body.CopyTo(file);
                }
                else
                    File.Copy(uri?.IsFile == true ? uri.LocalPath : source, target);
            }
            foreach (var (relative, document) in xml)
                document.Save(Target(relative));
            if (options.ConvertImages)
                ImagePostProcessor.ApplyWebP(output, xml.Keys);
        }

        private string Target(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(output, relative));
            if (!IsWithin(path, output))
                throw new InvalidOperationException($"Output path escaped root: {relative}");
            EnsureNoLinks(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return path;
        }

        private static string RelativeTo(string document, string resource) =>
            Path.GetRelativePath(Path.GetDirectoryName(document) is { Length: > 0 } directory ? directory : ".", resource).Replace('\\', '/');

        private static bool IsRegistration(XElement element) => MetadataReader.IsRegistration(element);

        private static bool IsReference(XElement element) => MetadataReader.IsReference(element);
    }
}
