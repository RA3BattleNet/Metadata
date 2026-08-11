using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 将带 Include 的源 XML 展平为单一数据树（不内联资源文件正文）。
/// 资源登记 ID 展平为全局唯一：{路径前缀}:{localId}，路径前缀为定义文件相对源根、去 .xml 的路径。
/// </summary>
public static class MetadataFlattener
{
    /// <summary>
    /// 从入口 metadata.xml 展平，Source 路径改写为相对 sourceRoot，ID 按定义文件加前缀。
    /// </summary>
    public static XDocument Flatten(string entryPath, string sourceRoot, string schemaVersion, string contentRevision)
    {
        var fullEntry = Path.GetFullPath(entryPath);
        var rootFull = Path.GetFullPath(sourceRoot);
        var processing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entitySources = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var root = LoadAndExpand(fullEntry, rootFull, processing, entitySources);

        // Include 展开后、写出版本前：合并 Base/InheritFrom，发布物无继承痕迹
        MetadataInheritance.Resolve(root);

        ValidateEntityIds(entitySources);

        root.SetAttributeValue("SchemaVersion", schemaVersion);
        root.SetAttributeValue("ContentRevision", contentRevision);

        root.Descendants().Where(e => e.Name.LocalName is "Includes" or "Include").ToList()
            .ForEach(e => e.Remove());

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    /// <summary>实体 ID（Mod/Application）全局唯一；含 ':' 一律拒绝（前缀由构建器生成）。</summary>
    private static void ValidateEntityIds(Dictionary<string, List<string>> entitySources)
    {
        foreach (var kv in entitySources)
        {
            if (kv.Key.Contains(':', StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"实体 ID \"{kv.Key}\" 含 ':'——实体 ID 不加前缀，请写短名");
            }
        }

        var duplicates = entitySources.Where(kv => kv.Value.Count > 1).ToList();
        if (duplicates.Count == 0) return;

        var lines = duplicates.SelectMany(kv =>
            kv.Value.Select(src => $"  - {src} (ID=\"{kv.Key}\")"));
        throw new InvalidOperationException("实体 ID 重复:\n" + string.Join("\n", lines));
    }

    /// <summary>由定义文件绝对路径生成路径前缀（相对 sourceRoot、/ 分隔、无扩展名）。</summary>
    public static string PathPrefix(string absoluteFilePath, string sourceRoot)
    {
        var rel = ToRootRelative(Path.GetFullPath(absoluteFilePath), Path.GetFullPath(sourceRoot));
        if (rel.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            rel = rel[..^4];
        return rel.Replace('\\', '/');
    }

    /// <summary>将 localId 限定为 {prefix}:{localId}；含 ':' 一律拒绝（前缀由构建器生成）。</summary>
    public static string QualifyId(string pathPrefix, string localId)
    {
        if (string.IsNullOrWhiteSpace(localId))
            throw new InvalidOperationException("ID 不能为空");
        if (localId.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"登记 ID \"{localId}\" 含 ':'——前缀由构建器自动生成，请写短名");
        if (string.IsNullOrWhiteSpace(pathPrefix))
            return localId;
        return $"{pathPrefix}:{localId}";
    }

    /// <summary>取限定 ID 的局部名（最后一个 ':' 之后）；无冒号则整串。</summary>
    public static string LocalId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return string.Empty;
        var i = id.LastIndexOf(':');
        return i < 0 ? id : id[(i + 1)..];
    }

    private static XElement LoadAndExpand(string filePath, string sourceRoot, HashSet<string> processing, Dictionary<string, List<string>> entitySources)
    {
        var full = Path.GetFullPath(filePath);
        if (!processing.Add(full))
            throw new InvalidOperationException($"检测到循环引用: {full}");

        if (!File.Exists(full))
            throw new FileNotFoundException($"找不到文件: {full}");

        var doc = XDocument.Load(full);
        var root = doc.Root ?? throw new InvalidOperationException($"无效 XML: {full}");
        if (root.Name.LocalName != "Metadata")
            throw new InvalidOperationException($"根节点必须是 Metadata: {full}");

        var baseDir = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException($"无法确定目录: {full}");
        var prefix = PathPrefix(full, sourceRoot);

        // 展开前先校验本文件声明：登记/实体 ID 禁止手写 ':' 前缀（前缀由构建器生成）
        ValidateOwnIds(root);
        // 记录本文件声明的实体 ID（展开会把 include 的实体并进来，不能算本文件的）
        RecordEntityIds(root, full, sourceRoot, entitySources);
        ExpandElement(root, baseDir, sourceRoot, processing, entitySources);
        RewriteSources(root, baseDir, sourceRoot);
        QualifyRegistrationIds(root, prefix);
        var scope = BuildScopeMap(root);
        RewriteIdReferences(root, scope);

        processing.Remove(full);
        return root;
    }

    /// <summary>拒绝本文件声明中的手工前缀 ID（Image/Markdown/Manifest/Mod/Application）。</summary>
    private static void ValidateOwnIds(XElement root)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            var id = el.Attribute("ID")?.Value;
            if (string.IsNullOrWhiteSpace(id) || !id.Contains(':', StringComparison.Ordinal))
                continue;
            if (el.Name.LocalName is "Image" or "Markdown" or "Manifest")
            {
                throw new InvalidOperationException(
                    $"登记 ID \"{id}\" 含 ':'——前缀由构建器自动生成，请写短名");
            }
            if (el.Name.LocalName is "Mod" or "Application")
            {
                throw new InvalidOperationException(
                    $"实体 ID \"{id}\" 含 ':'——实体 ID 不加前缀，请写短名");
            }
        }
    }

    /// <summary>记录本文件定义的实体 ID → 来源（供全局唯一校验与报错定位）。</summary>
    private static void RecordEntityIds(XElement root, string filePath, string sourceRoot, Dictionary<string, List<string>> entitySources)
    {
        var rel = ToRootRelative(Path.GetFullPath(filePath), Path.GetFullPath(sourceRoot));
        foreach (var entity in root.Elements().Where(e => e.Name.LocalName is "Mod" or "Application"))
        {
            var id = entity.Attribute("ID")?.Value;
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!entitySources.TryGetValue(id, out var list))
                entitySources[id] = list = new List<string>();
            list.Add($"{rel} ({entity.Name.LocalName})");
        }
    }

    private static void ExpandElement(XElement element, string baseDir, string sourceRoot, HashSet<string> processing, Dictionary<string, List<string>> entitySources)
    {
        var includeHosts = element.Elements()
            .Where(e => e.Name.LocalName is "Includes" or "Include")
            .ToList();

        foreach (var host in includeHosts)
        {
            if (host.Name.LocalName == "Include")
            {
                InsertInclude(host, baseDir, sourceRoot, processing, entitySources);
                host.Remove();
            }
            else
            {
                foreach (var inc in host.Elements().Where(e => e.Name.LocalName == "Include").ToList())
                {
                    InsertInclude(inc, baseDir, sourceRoot, processing, entitySources);
                    inc.Remove();
                }
                if (!host.HasElements)
                    host.Remove();
            }
        }

        foreach (var child in element.Elements().ToList())
        {
            if (child.Name.LocalName is "Includes" or "Include")
                continue;
            ExpandElement(child, baseDir, sourceRoot, processing, entitySources);
        }
    }

    private static void InsertInclude(XElement includeEl, string baseDir, string sourceRoot, HashSet<string> processing, Dictionary<string, List<string>> entitySources)
    {
        var rel = includeEl.Attribute("Source")?.Value;
        if (string.IsNullOrWhiteSpace(rel))
            throw new InvalidOperationException("Include 缺少 Source");

        var target = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('\\', '/')));
        var includedRoot = LoadAndExpand(target, sourceRoot, processing, entitySources);

        var anchor = includeEl.Parent is { Name.LocalName: "Includes" } host
            ? host
            : includeEl;

        foreach (var child in includedRoot.Elements().ToList())
        {
            if (child.Name.LocalName is "Tags" or "Includes")
                continue;

            if (child.Name.LocalName == "Manifest")
            {
                var id = child.Attribute("ID")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"Manifest 缺少 ID: {target}");

                var existingSource = child.Attribute("Source")?.Value;
                if (!string.IsNullOrWhiteSpace(existingSource))
                {
                    // 已是下层 stub，ID 应已限定
                    anchor.AddBeforeSelf(new XElement(child));
                    continue;
                }

                var sourceRel = ToRootRelative(target, sourceRoot);
                // 叶子 Manifest 经自身展平已限定（构建期产物）；贡献者手写前缀由 QualifyId 拒绝
                var qualified = id.Contains(':', StringComparison.Ordinal)
                    ? id
                    : QualifyId(PathPrefix(target, sourceRoot), id);
                var stub = new XElement("Manifest",
                    new XAttribute("ID", qualified),
                    new XAttribute("Source", sourceRel));
                anchor.AddBeforeSelf(stub);
                continue;
            }

            anchor.AddBeforeSelf(new XElement(child));
        }
    }

    /// <summary>仅限定本文件尚未限定的登记节点 ID（include 进来的已限定节点跳过）。</summary>
    private static void QualifyRegistrationIds(XElement root, string pathPrefix)
    {
        foreach (var el in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName is "Image" or "Markdown" or "Manifest"))
        {
            var idAttr = el.Attribute("ID");
            if (idAttr == null || string.IsNullOrWhiteSpace(idAttr.Value))
                continue;
            // Background 内无 ID 的 Image 引用跳过；登记节点必有 ID
            if (!idAttr.Value.Contains(':', StringComparison.Ordinal))
                idAttr.Value = QualifyId(pathPrefix, idAttr.Value);
        }
    }

    private static Dictionary<string, string> BuildScopeMap(XElement root)
    {
        // short localId → qualifiedId；同文件作用域内短名冲突则失败
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf()
                     .Where(e => e.Name.LocalName is "Image" or "Markdown" or "Manifest"))
        {
            var id = el.Attribute("ID")?.Value;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            // 完整限定 ID 始终可查
            map[id] = id;

            var local = LocalId(id);
            if (map.TryGetValue(local, out var existing) &&
                !string.Equals(existing, id, StringComparison.Ordinal))
            {
                // 跨 Include 同短名：短名不可再解析（应在各自文件内已改写引用），移出短名键
                map.Remove(local);
                continue;
            }
            map[local] = id;
        }
        return map;
    }

    private static void RewriteIdReferences(XElement root, Dictionary<string, string> scope)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            switch (el.Name.LocalName)
            {
                case "Icon":
                case "Manifest" when el.Attribute("ID") == null && !el.HasElements:
                case "Changelog":
                case "Content":
                    RewriteTextRef(el, scope);
                    break;
                case "Logo":
                    RewriteTextRef(el, scope);
                    break;
                case "Image" when el.Attribute("ID") == null:
                    // Background 等处的 ID 文本引用
                    RewriteTextRef(el, scope);
                    break;
            }
        }
    }

    private static void RewriteTextRef(XElement el, Dictionary<string, string> scope)
    {
        var text = el.Value?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        if (scope.TryGetValue(text, out var qualified))
            el.Value = qualified;
        // 找不到：留给 ValidateHard 报断引用（可能是笔误）
    }

    private static void RewriteSources(XElement element, string baseDir, string sourceRoot)
    {
        foreach (var el in element.DescendantsAndSelf().Where(e => e.Name.LocalName is "Image" or "Markdown"))
        {
            var sourceAttr = el.Attribute("Source");
            if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
                continue;
            if (!string.IsNullOrWhiteSpace(el.Attribute("Url")?.Value))
                continue;
            // 仅处理登记节点（有 ID）
            if (el.Attribute("ID") == null)
                continue;

            var raw = sourceAttr.Value.Replace('\\', '/');
            var fromBase = Path.GetFullPath(Path.Combine(baseDir, raw));
            if (File.Exists(fromBase))
            {
                sourceAttr.Value = ToRootRelative(fromBase, sourceRoot);
                continue;
            }

            var fromRoot = Path.GetFullPath(Path.Combine(sourceRoot, raw));
            if (File.Exists(fromRoot))
                sourceAttr.Value = ToRootRelative(fromRoot, sourceRoot);
        }
    }

    private static string ToRootRelative(string absolutePath, string sourceRoot)
    {
        var rel = Path.GetRelativePath(sourceRoot, absolutePath).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"资源不在源目录内: {absolutePath}");
        return rel;
    }
}
