namespace Ra3.BattleNet.Metadata;

/// <summary>
/// <see cref="Metadata"/> 的业务查询扩展方法。
/// </summary>
public static class MetadataQueryExtensions
{
    /// <summary>
    /// 构建目录式查询入口。
    /// </summary>
    /// <param name="root">元数据根对象。</param>
    /// <returns>可按实体类型查询的目录对象。</returns>
    public static MetadataCatalog Catalog(this Metadata root)
    {
        return new MetadataCatalog(root);
    }

    /// <summary>
    /// 获取所有 Mod 实体。
    /// </summary>
    /// <param name="root">元数据根对象。</param>
    /// <returns>Mod 实体列表。</returns>
    public static IReadOnlyList<ModEntry> Mods(this Metadata root)
    {
        return root.GetAllElements("Mod")
            .Select(ToMod)
            .ToList();
    }

    /// <summary>
    /// 获取所有 Application 实体。
    /// </summary>
    /// <param name="root">元数据根对象。</param>
    /// <returns>Application 实体列表。</returns>
    public static IReadOnlyList<ApplicationEntry> Applications(this Metadata root)
    {
        return root.GetAllElements("Application")
            .Select(ToApplication)
            .ToList();
    }

    /// <summary>
    /// 获取所有 Markdown 资源。
    /// </summary>
    /// <param name="root">元数据根对象。</param>
    /// <returns>Markdown 资源列表。</returns>
    public static IReadOnlyList<MarkdownEntry> Markdowns(this Metadata root)
    {
        return root.GetAllElements("Markdown")
            .Select(node => new MarkdownEntry(
                Id: node.Get("ID") ?? string.Empty,
                Source: node.Get("Source"),
                Hash: node.Get("Hash"),
                Raw: node))
            .ToList();
    }

    /// <summary>
    /// 获取所有图片资源。
    /// </summary>
    /// <param name="root">元数据根对象。</param>
    /// <returns>图片资源列表。</returns>
    public static IReadOnlyList<ImageEntry> Images(this Metadata root)
    {
        return root.GetAllElements("Image")
            .Select(node => new ImageEntry(
                Id: node.Get("ID") ?? string.Empty,
                Source: node.Get("Source"),
                Url: node.Get("Url"),
                Raw: node))
            .ToList();
    }

    /// <summary>
    /// 将 <c>Mod</c> 节点映射为 <see cref="ModEntry"/>。
    /// </summary>
    private static ModEntry ToMod(Metadata node)
    {
        return new ModEntry(
            Id: node.Get("ID") ?? string.Empty,
            Version: node.Find("CurrentVersion")?.Value,
            Icon: node.Find("Icon")?.Value,
            Packages: ReadPackages(node),
            Raw: node);
    }

    /// <summary>
    /// 将 <c>Application</c> 节点映射为 <see cref="ApplicationEntry"/>。
    /// </summary>
    private static ApplicationEntry ToApplication(Metadata node)
    {
        return new ApplicationEntry(
            Id: node.Get("ID") ?? string.Empty,
            Version: node.Find("Version")?.Value,
            Packages: ReadPackages(node),
            Raw: node);
    }

    /// <summary>
    /// 读取一个业务实体下的全部版本包。
    /// </summary>
    private static IReadOnlyList<PackageEntry> ReadPackages(Metadata node)
    {
        var packagesNode = node.Find("Packages");
        if (packagesNode == null)
        {
            return [];
        }

        return packagesNode.Children
            .Where(c => c.Name == "Package")
            .Select(package => new PackageEntry(
                Version: package.Get("Version") ?? string.Empty,
                ReleaseDate: package.Find("ReleaseDate")?.Value,
                ManifestId: package.Find("Manifest")?.Value,
                Raw: package))
            .ToList();
    }

    /// <summary>按登记 ID 找到 Manifest 登记节点（返回 null 表示不存在）。</summary>
    public static Metadata? ManifestRegistration(this Metadata root, string manifestId)
    {
        return root.GetAllElements("Manifest")
            .FirstOrDefault(m => string.Equals(m.Get("ID"), manifestId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把叶子清单节点解析成业务模型。</summary>
    public static ManifestEntry ToManifestEntry(this Metadata manifestNode)
    {
        var id = manifestNode.Get("ID") ?? string.Empty;
        var algo = manifestNode.Get("HashAlgorithm");
        algo = string.IsNullOrWhiteSpace(algo) ? "CRC32C" : algo.Trim().ToUpperInvariant();
        if (algo is not ("CRC32C" or "MD5" or "SHA256"))
            throw new InvalidOperationException($"Manifest '{id}' 的 HashAlgorithm 非法: {algo}");

        var files = manifestNode.Children
            .Where(c => c.Name == "File")
            .Select(file => new ManifestFileEntry(
                FileName: file.Find("FileName")?.Value ?? string.Empty,
                RelativePath: file.Find("RelativePath")?.Value ?? string.Empty,
                Hash: file.Get("Hash") ?? string.Empty,
                Size: long.TryParse(file.Get("Size"), out var size) ? size : (long?)null,
                DownloadName: file.Get("DownloadName"),
                Compression: string.IsNullOrWhiteSpace(file.Get("Compression"))
                    ? null
                    : file.Get("Compression")!.Trim().ToLowerInvariant(),
                KindOf: file.Find("KindOf")?.Value ?? string.Empty,
                Sources: ReadSources(file),
                Raw: file,
                Mount: string.IsNullOrWhiteSpace(file.Get("Mount")) ? "base" : file.Get("Mount")!.Trim().ToLowerInvariant(),
                Language: file.Get("Language"),
                Package: file.Get("Package")))
            .ToList();

        var dependencies = manifestNode.Find("Dependencies")?.Children
            .Where(c => c.Name == "Dll")
            .Select(dll => new ManifestDllEntry(
                Name: dll.Get("Name") ?? string.Empty,
                Version: dll.Get("Version"),
                Hash: dll.Get("Hash") ?? string.Empty,
                KindOf: dll.Get("KindOf")))
            .ToList()
            ?? [];

        var skudefNode = manifestNode.Find("Skudef");
        return new ManifestEntry(id, algo, files, dependencies, skudefNode == null ? null : ReadSkudef(skudefNode, id));
    }

    /// <summary>把 <c>Skudef</c> 节点解析成有序指令；子元素顺序即输出顺序。</summary>
    private static ManifestSkudefEntry ReadSkudef(Metadata skudefNode, string manifestId)
    {
        var commands = new List<ManifestSkudefCommand>();
        foreach (var command in skudefNode.Children)
        {
            if (command.Name == "AddBig")
            {
                var target = command.Get("File");
                if (string.IsNullOrWhiteSpace(target))
                    throw new InvalidOperationException($"Manifest '{manifestId}' 的 AddBig 缺少 File");

                var language = command.Get("Language");
                var package = command.Get("Package");
                if (language != null && package != null)
                    throw new InvalidOperationException($"Manifest '{manifestId}' 的 AddBig 同时写了 Language 与 Package");

                commands.Add(new ManifestSkudefCommand(SkudefCommandKind.Big, target, false, language, package));
            }
            else if (command.Name == "AddConfig")
            {
                var target = command.Get("LocalFile");
                if (string.IsNullOrWhiteSpace(target))
                    throw new InvalidOperationException($"Manifest '{manifestId}' 的 AddConfig 缺少 LocalFile");

                commands.Add(new ManifestSkudefCommand(SkudefCommandKind.Config, target, ReadOptional(command, manifestId), null, null));
            }
            else
            {
                throw new InvalidOperationException($"Manifest '{manifestId}' 的 Skudef 未知指令: {command.Name}");
            }
        }

        var gameVersion = skudefNode.Get("GameVersion");
        return new ManifestSkudefEntry(
            string.IsNullOrWhiteSpace(gameVersion) ? ManifestSkudefEntry.DefaultGameVersion : gameVersion,
            commands);
    }

    /// <summary>读 AddConfig 的 Optional；取值按 XSD boolean 的写法（true/false/1/0），其他写法直接抛。</summary>
    private static bool ReadOptional(Metadata command, string manifestId)
    {
        var raw = command.Get("Optional")?.Trim().ToLowerInvariant();
        return raw switch
        {
            null or "false" or "0" => false,
            "true" or "1" => true,
            _ => throw new InvalidOperationException($"Manifest '{manifestId}' 的 AddConfig Optional 不是布尔值: {raw}"),
        };
    }

    private static IReadOnlyList<ManifestSourceEntry> ReadSources(Metadata file)
    {
        var sourcesNode = file.Find("Sources");
        if (sourcesNode == null)
        {
            return [];
        }

        return sourcesNode.Children
            .Where(c => c.Name == "Source")
            .Select(source => new ManifestSourceEntry(
                Type: (source.Get("Type") ?? string.Empty).ToUpperInvariant(),
                Url: source.Get("Url") ?? string.Empty))
            .ToList();
    }
}
