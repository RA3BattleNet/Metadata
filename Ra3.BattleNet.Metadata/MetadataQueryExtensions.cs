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
}
