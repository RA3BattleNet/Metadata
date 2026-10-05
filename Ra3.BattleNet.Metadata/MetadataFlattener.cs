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
        var session = new FlattenSession(Path.GetFullPath(sourceRoot));
        var root = LoadAndExpand(Path.GetFullPath(entryPath), session);

        // Include 展开后、写出版本前：合并 Base/InheritFrom，发布物无继承痕迹
        MetadataInheritance.Resolve(root);

        ValidateEntityIds(session.EntitySources);

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

    /// <summary>节点分类：Image/Markdown/Manifest 资源登记节点（是否带 @ID 由调用处再判断）。</summary>
    private static bool IsRegistrationNode(XElement el) =>
        el.Name.LocalName is "Image" or "Markdown" or "Manifest";

    /// <summary>节点分类：Mod/Application 实体节点。</summary>
    private static bool IsEntity(XElement el) =>
        el.Name.LocalName is "Mod" or "Application";

    /// <summary>
    /// 节点分类：文本内容是资源 ID 短名的引用节点。
    /// 新增引用类型（如新样式字段、新 Post 字段）只需在此处登记。
    /// </summary>
    private static bool IsTextReference(XElement el) => el.Name.LocalName switch
    {
        "Icon" or "Logo" or "Content" or "HeadImage" => true,
        // Package.Manifest 引用：无 ID 且无子元素
        "Manifest" => el.Attribute("ID") == null && !el.HasElements,
        // Background 等处的 ID 文本引用；登记节点必有 ID
        "Image" => el.Attribute("ID") == null,
        _ => false
    };

    private static XElement LoadAndExpand(string filePath, FlattenSession session)
    {
        var full = Path.GetFullPath(filePath);
        if (!session.Processing.Add(full))
            throw new InvalidOperationException($"检测到循环引用: {full}");

        if (!File.Exists(full))
            throw new FileNotFoundException($"找不到文件: {full}");

        var doc = XDocument.Load(full);
        var root = doc.Root ?? throw new InvalidOperationException($"无效 XML: {full}");
        if (root.Name.LocalName != "Metadata")
            throw new InvalidOperationException($"根节点必须是 Metadata: {full}");

        var baseDir = Path.GetDirectoryName(full)
            ?? throw new InvalidOperationException($"无法确定目录: {full}");
        var prefix = PathPrefix(full, session.SourceRootFull);

        // 展开前单趟：校验本文件手写 ID（登记/实体禁止 ':' 前缀）并记录顶层实体来源
        ValidateAndRecordOwnIds(root, full, session);
        ExpandElement(root, baseDir, session);
        // 展开后单趟：改写 Source、限定登记 ID，并同步建立引用作用域映射
        var scope = ProcessRegistrations(root, baseDir, prefix, session);
        // 最后一趟独立改写引用：前向引用要求作用域映射已完整
        RewriteIdReferences(root, scope);

        session.Processing.Remove(full);
        return root;
    }

    /// <summary>
    /// 展开前单趟处理本文件声明：拒绝手工前缀 ID（Image/Markdown/Manifest/Mod/Application），
    /// 并记录顶层实体 ID → 来源（展开会把 include 的实体并进来，不能算本文件的）。
    /// </summary>
    private static void ValidateAndRecordOwnIds(XElement root, string fullPath, FlattenSession session)
    {
        var rel = ToRootRelative(fullPath, session.SourceRootFull);
        foreach (var el in root.DescendantsAndSelf())
        {
            var id = el.Attribute("ID")?.Value;

            if (el.Parent == root && IsEntity(el) && !string.IsNullOrWhiteSpace(id))
            {
                if (!session.EntitySources.TryGetValue(id, out var list))
                    session.EntitySources[id] = list = new List<string>();
                list.Add($"{rel} ({el.Name.LocalName})");
            }

            if (string.IsNullOrWhiteSpace(id) || !id.Contains(':', StringComparison.Ordinal))
                continue;
            if (IsRegistrationNode(el))
            {
                throw new InvalidOperationException(
                    $"登记 ID \"{id}\" 含 ':'——前缀由构建器自动生成，请写短名");
            }
            if (IsEntity(el))
            {
                throw new InvalidOperationException(
                    $"实体 ID \"{id}\" 含 ':'——实体 ID 不加前缀，请写短名");
            }
        }
    }

    private static void ExpandElement(XElement element, string baseDir, FlattenSession session)
    {
        var includeHosts = element.Elements()
            .Where(e => e.Name.LocalName is "Includes" or "Include")
            .ToList();

        foreach (var host in includeHosts)
        {
            if (host.Name.LocalName == "Include")
            {
                InsertInclude(host, baseDir, session);
                host.Remove();
            }
            else
            {
                foreach (var inc in host.Elements().Where(e => e.Name.LocalName == "Include").ToList())
                {
                    InsertInclude(inc, baseDir, session);
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
            ExpandElement(child, baseDir, session);
        }
    }

    private static void InsertInclude(XElement includeEl, string baseDir, FlattenSession session)
    {
        var rel = includeEl.Attribute("Source")?.Value;
        if (string.IsNullOrWhiteSpace(rel))
            throw new InvalidOperationException("Include 缺少 Source");

        var target = Path.GetFullPath(Path.Combine(baseDir, rel.Replace('\\', '/')));
        var includedRoot = LoadAndExpand(target, session);

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

                var sourceRel = ToRootRelative(target, session.SourceRootFull);
                // 叶子 Manifest 经自身展平已限定（构建期产物）；贡献者手写前缀由 QualifyId 拒绝
                var qualified = id.Contains(':', StringComparison.Ordinal)
                    ? id
                    : QualifyId(PathPrefix(target, session.SourceRootFull), id);
                var stub = new XElement("Manifest",
                    new XAttribute("ID", qualified),
                    new XAttribute("Source", sourceRel));
                anchor.AddBeforeSelf(stub);
                continue;
            }

            anchor.AddBeforeSelf(new XElement(child));
        }
    }

    /// <summary>
    /// 展开后单趟处理全部登记节点：Source 改写为相对源根、本文件登记 ID 限定为
    /// {路径前缀}:{localId}，并按文档顺序同步建立 short localId → qualifiedId 作用域映射
    /// （完整限定 ID 始终可查；同文件作用域内短名冲突则移出短名键）。
    /// </summary>
    private static Dictionary<string, string> ProcessRegistrations(XElement root, string baseDir, string pathPrefix, FlattenSession session)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in root.DescendantsAndSelf())
        {
            if (!IsRegistrationNode(el))
                continue;

            RewriteSource(el, baseDir, session);

            var idAttr = el.Attribute("ID");
            if (idAttr == null || string.IsNullOrWhiteSpace(idAttr.Value))
                continue;

            // 仅限定本文件尚未限定的登记节点 ID（include 进来的已限定节点跳过）
            if (!idAttr.Value.Contains(':', StringComparison.Ordinal))
                idAttr.Value = QualifyId(pathPrefix, idAttr.Value);

            var id = idAttr.Value;
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

    /// <summary>将登记节点（带 ID 的 Image/Markdown）的 Source 改写为相对源根路径；Url 外链跳过。</summary>
    private static void RewriteSource(XElement el, string baseDir, FlattenSession session)
    {
        if (el.Name.LocalName is not ("Image" or "Markdown"))
            return;

        var sourceAttr = el.Attribute("Source");
        if (sourceAttr == null || string.IsNullOrWhiteSpace(sourceAttr.Value))
            return;
        if (!string.IsNullOrWhiteSpace(el.Attribute("Url")?.Value))
            return;
        // 仅处理登记节点（有 ID）
        if (el.Attribute("ID") == null)
            return;

        var raw = sourceAttr.Value.Replace('\\', '/');
        var fromBase = Path.GetFullPath(Path.Combine(baseDir, raw));
        if (File.Exists(fromBase))
        {
            sourceAttr.Value = ToRootRelative(fromBase, session.SourceRootFull);
            return;
        }

        var fromRoot = Path.GetFullPath(Path.Combine(session.SourceRootFull, raw));
        if (File.Exists(fromRoot))
            sourceAttr.Value = ToRootRelative(fromRoot, session.SourceRootFull);
    }

    private static void RewriteIdReferences(XElement root, Dictionary<string, string> scope)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            if (IsTextReference(el))
                RewriteTextRef(el, scope);
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

    private static string ToRootRelative(string absolutePath, string sourceRoot)
    {
        var rel = Path.GetRelativePath(sourceRoot, absolutePath).Replace('\\', '/');
        if (rel.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException($"资源不在源目录内: {absolutePath}");
        return rel;
    }

    /// <summary>单次展平过程的共享状态：源根、展开栈（循环检测）、实体 ID 来源表。</summary>
    private sealed class FlattenSession
    {
        public FlattenSession(string sourceRootFull) => SourceRootFull = sourceRootFull;

        public string SourceRootFull { get; }
        public HashSet<string> Processing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> EntitySources { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
