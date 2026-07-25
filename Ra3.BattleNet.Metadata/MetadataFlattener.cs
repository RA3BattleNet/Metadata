using System.Xml.Linq;

namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 将带 Include 的源 XML 展平为单一数据树（不内联资源文件正文）。
/// </summary>
public static class MetadataFlattener
{
    /// <summary>
    /// 从入口 metadata.xml 展平，Source 路径改写为相对 sourceRoot。
    /// Manifest 在独立资源文件中时只保留 ID+Source，不把 File 列表并入主树。
    /// </summary>
    public static XDocument Flatten(string entryPath, string sourceRoot, string schemaVersion, string contentRevision)
    {
        var fullEntry = Path.GetFullPath(entryPath);
        var rootFull = Path.GetFullPath(sourceRoot);
        var processing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = LoadAndExpand(fullEntry, rootFull, processing);

        root.SetAttributeValue("SchemaVersion", schemaVersion);
        root.SetAttributeValue("ContentRevision", contentRevision);

        // 去掉残留 Includes / Include / Module
        root.Descendants().Where(e => e.Name.LocalName is "Includes" or "Include" or "Module").ToList()
            .ForEach(e => e.Remove());

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    private static XElement LoadAndExpand(string filePath, string sourceRoot, HashSet<string> processing)
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

        ExpandElement(root, baseDir, sourceRoot, processing);
        processing.Remove(full);
        return root;
    }

    private static void ExpandElement(XElement element, string baseDir, string sourceRoot, HashSet<string> processing)
    {
        // 先处理 Includes 容器与直接 Include
        var includeHosts = element.Elements()
            .Where(e => e.Name.LocalName is "Includes" or "Include" or "Module")
            .ToList();

        foreach (var host in includeHosts)
        {
            if (host.Name.LocalName is "Include" or "Module")
            {
                InsertInclude(host, baseDir, sourceRoot, processing);
                host.Remove();
            }
            else
            {
                foreach (var inc in host.Elements().Where(e => e.Name.LocalName is "Include" or "Module").ToList())
                {
                    InsertInclude(inc, baseDir, sourceRoot, processing);
                    inc.Remove();
                }
                if (!host.HasElements)
                    host.Remove();
            }
        }

        // 改写本层 Image/Markdown 的 Source 为相对 sourceRoot
        RewriteSources(element, baseDir, sourceRoot);

        foreach (var child in element.Elements().ToList())
        {
            if (child.Name.LocalName is "Includes" or "Include" or "Module")
                continue;
            ExpandElement(child, baseDir, sourceRoot, processing);
        }
    }

    private static void InsertInclude(XElement includeEl, string baseDir, string sourceRoot, HashSet<string> processing)
    {
        var rel = includeEl.Attribute("Source")?.Value ?? includeEl.Attribute("Path")?.Value;
        if (string.IsNullOrWhiteSpace(rel))
            throw new InvalidOperationException("Include/Module 缺少 Source/Path");

        var target = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('\\', '/')));
        var includedRoot = LoadAndExpand(target, sourceRoot, processing);

        // Include 若在 <Includes> 容器内，插入点为容器本身（作为 Metadata 子节点的兄弟），
        // 避免内容落在 Includes 内随后被整段删除。
        var anchor = includeEl.Parent is { Name.LocalName: "Includes" } host
            ? host
            : includeEl;

        foreach (var child in includedRoot.Elements().ToList())
        {
            if (child.Name.LocalName is "Tags" or "Includes")
                continue;

            if (child.Name.LocalName == "Manifest")
            {
                // 资源外置：主树只保留 ID + Source（指向含 File 表的叶子 manifest XML）
                var id = child.Attribute("ID")?.Value;
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException($"Manifest 缺少 ID: {target}");

                // 已是下层展开产生的 stub（带 Source）则原样上浮，禁止用中间 Include 文件覆盖路径
                var existingSource = child.Attribute("Source")?.Value;
                if (!string.IsNullOrWhiteSpace(existingSource))
                {
                    anchor.AddBeforeSelf(new XElement(child));
                    continue;
                }

                var sourceRel = ToRootRelative(target, sourceRoot);
                var stub = new XElement("Manifest",
                    new XAttribute("ID", id),
                    new XAttribute("Source", sourceRel));
                anchor.AddBeforeSelf(stub);
                continue;
            }

            // Markdown/Image/Application/Mod 等：深拷贝
            var clone = new XElement(child);
            anchor.AddBeforeSelf(clone);
        }
    }

    private static void RewriteSources(XElement element, string baseDir, string sourceRoot)
    {
        foreach (var el in element.Elements().Where(e => e.Name.LocalName is "Image" or "Markdown"))
        {
            var sourceAttr = el.Attribute("Source");
            if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
                continue;
            if (!string.IsNullOrWhiteSpace(el.Attribute("Url")?.Value))
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
            {
                sourceAttr.Value = ToRootRelative(fromRoot, sourceRoot);
            }
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
