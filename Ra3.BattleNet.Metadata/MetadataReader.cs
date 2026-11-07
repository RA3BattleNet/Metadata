using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

public static class MetadataReader
{
    internal static StringComparer PathComparer => DocumentPathComparer.Instance;
    public static MetadataReadResult Read(string pathOrUrl, MetadataSourceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrUrl);
        var settings = options ?? new MetadataSourceOptions();
        if (settings.CustomSource is { } custom)
            return ReadCore(custom, pathOrUrl, settings);
        if (IsHttp(pathOrUrl))
        {
            using var source = new HttpXmlSource(new Uri(new Uri(pathOrUrl), "."));
            return ReadCore(source, pathOrUrl, settings);
        }
        var path = Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var uri) && uri.IsFile
            ? uri.LocalPath : pathOrUrl;
        return ReadCore(new PhysicalFileXmlSource(), path, settings);
    }

    internal static string ResolveInclude(IMetadataXmlSource source, string documentPath, string includeSource, string sourceRoot)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(includeSource);
        if (IsHttp(documentPath) && Uri.TryCreate(includeSource, UriKind.Absolute, out var uri)
            && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("HTTP metadata cannot include local documents");
        if (!IsHttp(documentPath) && IsHttp(includeSource))
            return new Uri(includeSource).AbsoluteUri;
        var directory = source.GetDirectoryName(documentPath)
            ?? throw new InvalidOperationException($"Cannot determine document directory: {documentPath}");
        var target = source.GetFullPath(source.Combine(directory, includeSource));
        EnsureWithinRoot(source, target, sourceRoot, "Include");
        return target;
    }

    internal static MetadataReadResult ReadCore(IMetadataXmlSource source, string entryPath, MetadataSourceOptions options)
    {
        var entry = source.GetFullPath(entryPath);
        var root = source.GetFullPath(options.SourceRoot ?? source.GetDirectoryName(entry)
            ?? throw new InvalidOperationException($"Cannot determine source root: {entry}"));
        EnsureWithinRoot(source, entry, root, "Entry");
        var session = new Session(source, entry, root, options);
        return session.Read();
    }

    internal static bool IsHttp(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    internal static void EnsureWithinRoot(IMetadataXmlSource source, string path, string root, string kind)
    {
        var relative = source.GetRelativePath(root, path).Replace('\\', '/');
        if (source.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidOperationException($"{kind} 越出源目录: {path}");
    }

    internal static bool IsRegistration(XElement element) => element.Attribute("ID") != null
        && element.Name.LocalName is "Image" or "Markdown" or "Manifest";

    internal static bool IsReference(XElement element) => element.Name.LocalName switch
    {
        "Icon" or "Logo" or "Content" or "HeadImage" => !element.HasElements,
        "Manifest" or "Image" => element.Attribute("ID") == null && !element.HasElements,
        _ => false
    };

    private sealed class Session
    {
        private readonly IMetadataXmlSource _source;
        private readonly string _entry;
        private readonly string _root;
        private readonly MetadataSourceOptions _options;
        private readonly Dictionary<string, MetadataDocumentInfo> _documents;
        private readonly HashSet<string> _processing;
        private readonly Dictionary<string, XElement> _registrations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _globalAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _entities = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<MetadataDocumentInfo> _ordered = [];
        private readonly Dictionary<string, bool> _author;
        private readonly Dictionary<string, string> _boundaries;
        private readonly StringComparer _pathComparer;
        private readonly HashSet<string> _manifestProcessing = new(StringComparer.OrdinalIgnoreCase);

        internal Session(IMetadataXmlSource source, string entry, string root, MetadataSourceOptions options)
        {
            _source = source;
            _entry = entry;
            _root = root;
            _options = options;
            _pathComparer = DocumentPathComparer.Instance;
            _documents = new Dictionary<string, MetadataDocumentInfo>(_pathComparer);
            _processing = new HashSet<string>(_pathComparer);
            _author = new Dictionary<string, bool>(_pathComparer);
            _boundaries = new Dictionary<string, string>(_pathComparer);
        }

        internal MetadataReadResult Read()
        {
            var entry = Capture(_entry, _root, false);
            foreach (var document in _ordered.ToArray()) RewriteReferences(document);
            var expanded = Expand(entry, new HashSet<string>(_pathComparer), false);
            MetadataInheritance.Resolve(expanded);
            if (_author[_entry])
            {
                expanded.SetAttributeValue("SchemaVersion", MetadataSchema.Current);
                expanded.SetAttributeValue("ContentRevision", "local-source");
            }
            expanded.Attribute("IdFormat")?.Remove();
            expanded.Attribute("SourceBase")?.Remove();
            var preserveFlat = !_author[_entry] && entry.Document.Root!.Attribute("IdFormat") == null
                && !MetadataDetector.HasComposition(entry.Document);
            if (preserveFlat && !_options.InlineManifests)
            {
                var originals = entry.Document.Descendants().Where(IsRegistration)
                    .ToDictionary(element => element.Attribute("ID")!.Value, StringComparer.OrdinalIgnoreCase);
                foreach (var element in expanded.Descendants().Where(IsRegistration))
                {
                    if (originals.TryGetValue(element.Attribute("ID")!.Value, out var original))
                        element.SetAttributeValue("Source", original.Attribute("Source")?.Value);
                }
            }
            else
            {
                var baseDirectory = _options.LegacySourcePaths ? _root : _source.GetDirectoryName(_entry)!;
                foreach (var element in expanded.Descendants().Where(IsRegistration))
                {
                    if (element.Attribute("Source") is not { } source || string.IsNullOrWhiteSpace(source.Value)) continue;
                    if (!string.IsNullOrWhiteSpace(element.Attribute("Url")?.Value)) continue;
                    source.Value = _source.GetRelativePath(baseDirectory, source.Value).Replace('\\', '/');
                }
            }
            var result = new MetadataReadResult(new XDocument(new XDeclaration("1.0", "utf-8", null), expanded),
                _entry, _root, _ordered.AsReadOnly(), _source);
            if (_options.ValidateReferences) MetadataValidator.EnsureReferences(result.Root);
            return result;
        }

        private string DocumentScope(string path)
        {
            if (!IsHttp(path) || IsHttp(_entry)) return MetadataFlattener.PathPrefix(_source, path, _root);
            var uri = new Uri(path);
            var scope = uri.Scheme + "/" + Uri.EscapeDataString(uri.Authority) + uri.AbsolutePath;
            return scope.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? scope[..^4] : scope;
        }

        private MetadataDocumentInfo Capture(string path, string boundary, bool parentAuthor, bool manifestResource = false)
        {
            if (_processing.Contains(path)) throw new InvalidOperationException($"检测到循环引用: {path}");
            if (_documents.TryGetValue(path, out var existing)) return existing;
            _processing.Add(path);
            var raw = _source.LoadDocument(path);
            if (raw.Root?.Name.LocalName != "Metadata")
                throw new InvalidOperationException($"根节点必须是 Metadata: {path}");
            var format = raw.Root.Attribute("IdFormat")?.Value;
            if (format != null && format is not ("legacy" or "path"))
                throw new InvalidOperationException($"Unknown IdFormat: {format}");
            var sourceBase = raw.Root.Attribute("SourceBase")?.Value;
            if (sourceBase != null && sourceBase != "document")
                throw new InvalidOperationException($"Unknown SourceBase: {sourceBase}");
            var author = format == null && (_options.ForceSourceTree || parentAuthor || MetadataDetector.HasComposition(raw));
            var info = new MetadataDocumentInfo(path, _source.GetRelativePath(_root, path).Replace('\\', '/'), raw);
            _documents.Add(path, info);
            _ordered.Add(info);
            _author.Add(path, author);
            _boundaries.Add(path, boundary);
            var root = info.NormalizedDocument.Root!;
            var prefix = DocumentScope(path);
            var ownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in root.Descendants().Where(IsRegistration))
            {
                var id = element.Attribute("ID")!;
                if (format == "path") id.Value = MetadataId.DecodePath(id.Value);
                else if (author && !id.Value.Contains(':')) id.Value = MetadataId.Qualify(prefix, id.Value);
                else if (_options.LegacySourcePaths && format == null && id.Value.Contains(':'))
                    throw new InvalidOperationException($"登记 ID \"{id.Value}\" 含 ':'");
                if (!ownIds.Add(id.Value)) throw new InvalidOperationException($"登记 ID 重复: {id.Value} ({path})");
                if (!(manifestResource && element.Name.LocalName == "Manifest") && !_registrations.TryAdd(id.Value, element))
                    throw new InvalidOperationException($"登记 ID 重复: {id.Value} ({path})");
                AddAlias(_globalAliases, id.Value);
                NormalizeSource(element, path, boundary, author || format != null, sourceBase == "document");
            }
            foreach (var entity in root.Elements().Where(e => e.Name.LocalName is "Mod" or "Application"))
            {
                var id = entity.Attribute("ID")?.Value;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (id.Contains(':')) throw new InvalidOperationException($"实体 ID \"{id}\" 含 ':'");
                if (!_entities.TryAdd(id, path))
                    throw new InvalidOperationException($"实体 ID 重复: {id} ({_entities[id]}; {path})");
            }
            foreach (var include in root.Descendants().Where(e => e.Name.LocalName == "Include").ToArray())
            {
                var target = ResolveInclude(_source, path, include.Attribute("Source")?.Value ?? string.Empty, boundary);
                include.SetAttributeValue("Source", target);
                info.IncludeList.Add(new MetadataIncludeInfo(path, target));
                var childBoundary = !IsHttp(path) && IsHttp(target) ? _source.GetDirectoryName(target)! : boundary;
                Capture(target, childBoundary, author, manifestResource);
            }
            _processing.Remove(path);
            return info;
        }

        private void NormalizeSource(XElement element, string path, string boundary, bool constrain, bool documentBase)
        {
            if (element.Attribute("Source") is not { } attribute || string.IsNullOrWhiteSpace(attribute.Value)) return;
            if (!string.IsNullOrWhiteSpace(element.Attribute("Url")?.Value)) return;
            var raw = attribute.Value;
            if (IsHttp(raw))
            {
                attribute.Value = new Uri(raw).AbsoluteUri;
                if (_options.LegacySourcePaths) EnsureWithinRoot(_source, attribute.Value, boundary, "资源 Source");
                return;
            }
            if (IsHttp(path) && Uri.TryCreate(raw, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                if (constrain) throw new InvalidOperationException("HTTP metadata cannot register local resources");
                return;
            }
            var directory = _source.GetDirectoryName(path)!;
            var target = _source.GetFullPath(_source.Combine(directory, raw));
            if (constrain) EnsureWithinRoot(_source, target, boundary, "资源 Source");
            if (!documentBase && constrain && (_options.LegacySourcePaths || !IsHttp(target)) && !_source.Exists(target))
            {
                var fromRoot = _source.GetFullPath(_source.Combine(boundary, raw));
                EnsureWithinRoot(_source, fromRoot, boundary, "资源 Source");
                if (_source.Exists(fromRoot)) target = fromRoot;
            }
            attribute.Value = target;
        }

        private void RewriteReferences(MetadataDocumentInfo document)
        {
            var reachable = new HashSet<string>(_pathComparer);
            var ownAliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var registration in document.NormalizedDocument.Descendants().Where(IsRegistration))
                AddAlias(ownAliases, registration.Attribute("ID")!.Value);
            var full = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            void Add(MetadataDocumentInfo current)
            {
                if (!reachable.Add(current.Path)) return;
                foreach (var registration in current.NormalizedDocument.Descendants().Where(IsRegistration))
                {
                    var id = registration.Attribute("ID")!.Value;
                    full[id] = id;
                    AddAlias(aliases, id);
                }
                foreach (var edge in current.Includes) Add(_documents[edge.TargetPath]);
            }
            Add(document);
            var format = document.Document.Root!.Attribute("IdFormat")?.Value;
            foreach (var reference in document.NormalizedDocument.Descendants().Where(IsReference))
            {
                var id = reference.Value.Trim();
                if (id.Length == 0) continue;
                if (format == "path") id = MetadataId.DecodePath(id);
                if (_author[document.Path] && !id.Contains(':') && ownAliases.TryGetValue(id, out var own))
                {
                    if (own.Count != 1) throw new InvalidOperationException($"Ambiguous resource ID: {id} ({document.Path})");
                    reference.Value = own.Single();
                    continue;
                }
                if (full.ContainsKey(id) || _registrations.ContainsKey(id))
                {
                    reference.Value = id;
                    continue;
                }
                if (!ownAliases.TryGetValue(id, out var matches) && !aliases.TryGetValue(id, out matches))
                    _globalAliases.TryGetValue(id, out matches);
                if (matches is { Count: > 1 })
                    throw new InvalidOperationException($"Ambiguous resource ID: {id} ({document.Path})");
                reference.Value = matches is { Count: 1 } ? matches.Single() : id;
            }
        }

        private static void AddAlias(Dictionary<string, HashSet<string>> aliases, string id)
        {
            var local = MetadataId.LocalId(id);
            if (!aliases.TryGetValue(local, out var values))
                aliases[local] = values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            values.Add(id);
        }

        private XElement Expand(MetadataDocumentInfo document, HashSet<string> expanded, bool included)
        {
            expanded.Add(document.Path);
            var root = new XElement(document.NormalizedDocument.Root!);
            foreach (var include in root.Descendants().Where(e => e.Name.LocalName == "Include").ToArray())
            {
                var target = include.Attribute("Source")!.Value;
                if (!expanded.Add(target)) { include.Remove(); continue; }
                var child = Expand(_documents[target], expanded, true);
                include.ReplaceWith(child.Elements().Where(e => e.Name.LocalName != "Tags").ToArray());
            }
            foreach (var host in root.Descendants().Where(e => e.Name.LocalName == "Includes").ToArray())
                host.ReplaceWith(host.Elements().ToArray());
            if (included && !_options.InlineManifests && _author[document.Path]
                && document.Document.Root!.Attribute("IdFormat") == null)
            {
                var ownManifests = document.NormalizedDocument.Root!.Elements()
                    .Where(e => e.Name.LocalName == "Manifest" && e.Attribute("Source") == null)
                    .Select(e => e.Attribute("ID")!.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var manifest in root.Elements().Where(e => e.Name.LocalName == "Manifest" && e.Attribute("Source") == null
                    && ownManifests.Contains(e.Attribute("ID")!.Value)).ToArray())
                    manifest.ReplaceWith(new XElement("Manifest", new XAttribute("ID", manifest.Attribute("ID")!.Value),
                        new XAttribute("Source", document.Path)));
            }
            if (_options.InlineManifests)
            {
                foreach (var manifest in root.Descendants().Where(e => e.Name.LocalName == "Manifest" && e.Attribute("ID") != null
                    && e.Attribute("Source") != null).ToArray()) InlineManifest(manifest, document);
            }
            return root;
        }

        private void InlineManifest(XElement registration, MetadataDocumentInfo owner)
        {
            var target = registration.Attribute("Source")!.Value;
            var id = registration.Attribute("ID")!.Value;
            var key = target + "#" + id;
            if (!_manifestProcessing.Add(key)) throw new InvalidOperationException($"Manifest cycle: {target}");
            if (!_documents.TryGetValue(target, out var captured))
            {
                var boundary = _boundaries[owner.Path];
                if (!IsHttp(owner.Path) && IsHttp(target)) boundary = _source.GetDirectoryName(target)!;
                else EnsureWithinRoot(_source, target, boundary, "Manifest Source");
                var count = _ordered.Count;
                captured = Capture(target, boundary, false, true);
                foreach (var added in _ordered.Skip(count)) RewriteReferences(added);
            }
            var body = SelectManifest(Expand(captured, new HashSet<string>(_pathComparer), false), id);
            var clone = new XElement(body);
            clone.SetAttributeValue("ID", id);
            clone.Attribute("Source")?.Remove();
            registration.ReplaceWith(clone);
            _manifestProcessing.Remove(key);
        }

        private static XElement SelectManifest(XElement document, string id)
        {
            var manifests = document.Descendants().Where(e => e.Name.LocalName == "Manifest" && e.Attribute("ID") != null).ToArray();
            var exact = manifests.Where(e => string.Equals(e.Attribute("ID")!.Value, id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (exact.Length == 1) return exact[0];
            var matches = manifests.Where(e => string.Equals(MetadataId.LocalId(e.Attribute("ID")!.Value),
                MetadataId.LocalId(id), StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length switch
            {
                1 => matches[0],
                0 => throw new InvalidOperationException($"Manifest not found: {id}"),
                _ => throw new InvalidOperationException($"Ambiguous manifest ID: {id}")
            };
        }
    }

    private sealed class DocumentPathComparer : StringComparer
    {
        internal static readonly DocumentPathComparer Instance = new();
        private static StringComparer For(string? path) => IsHttp(path ?? string.Empty) || !OperatingSystem.IsWindows()
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        public override int Compare(string? x, string? y) => For(x).Compare(x, y);
        public override bool Equals(string? x, string? y) => For(x).Equals(x, y);
        public override int GetHashCode(string obj) => For(obj).GetHashCode(obj);
    }
}
