namespace Ra3.BattleNet.Metadata;

/// <summary>
/// 面向业务使用的查询入口（类似“创意工坊”浏览视图）。
/// </summary>
public sealed class MetadataCatalog
{
    private readonly Metadata _root;

    /// <summary>
    /// 基于已加载的元数据根节点构造查询目录。
    /// </summary>
    /// <param name="root">元数据根对象。</param>
    public MetadataCatalog(Metadata root)
    {
        _root = root;
    }

    /// <summary>
    /// 获取全部 Mod 实体。
    /// </summary>
    public IReadOnlyList<ModEntry> Mods => _root.Mods();

    /// <summary>
    /// 获取全部 Application 实体。
    /// </summary>
    public IReadOnlyList<ApplicationEntry> Applications => _root.Applications();

    /// <summary>
    /// 获取全部 Markdown 资源。
    /// </summary>
    public IReadOnlyList<MarkdownEntry> Markdowns => _root.Markdowns();

    /// <summary>
    /// 获取全部图片资源。
    /// </summary>
    public IReadOnlyList<ImageEntry> Images => _root.Images();

    /// <summary>
    /// 按 ID 查找 Mod。
    /// </summary>
    public ModEntry? Mod(string id) => Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 按 ID 查找 Application。
    /// </summary>
    public ApplicationEntry? Application(string id) => Applications.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Application 业务实体。
/// </summary>
/// <param name="Id">应用 ID。</param>
/// <param name="Version">应用当前版本（来自 <c>&lt;Version&gt;</c>）。</param>
/// <param name="Packages">版本包列表。</param>
/// <param name="Raw">原始元数据节点。</param>
public sealed record ApplicationEntry(string Id, string? Version, IReadOnlyList<PackageEntry> Packages, Metadata Raw);

/// <summary>
/// Mod 业务实体。
/// </summary>
/// <param name="Id">Mod ID。</param>
/// <param name="Version">当前版本（来自 <c>&lt;CurrentVersion&gt;</c>）。</param>
/// <param name="Icon">图标资源 ID。</param>
/// <param name="Packages">版本包列表。</param>
/// <param name="Raw">原始元数据节点。</param>
public sealed record ModEntry(string Id, string? Version, string? Icon, IReadOnlyList<PackageEntry> Packages, Metadata Raw);

/// <summary>
/// 版本包实体。
/// </summary>
/// <param name="Version">包版本号（来自 <c>Package@Version</c>）。</param>
/// <param name="ReleaseDate">发布日期。</param>
/// <param name="ManifestId">Manifest 资源 ID。</param>
/// <param name="Raw">原始元数据节点。</param>
public sealed record PackageEntry(string Version, string? ReleaseDate, string? ManifestId, Metadata Raw);

/// <summary>
/// Markdown 资源实体。
/// </summary>
/// <param name="Id">资源 ID。</param>
/// <param name="Source">源文件路径。</param>
/// <param name="Hash">内容哈希值。</param>
/// <param name="Raw">原始元数据节点。</param>
public sealed record MarkdownEntry(string Id, string? Source, string? Hash, Metadata Raw);

/// <summary>
/// 图片资源实体。
/// </summary>
/// <param name="Id">资源 ID。</param>
/// <param name="Source">本地源文件路径。</param>
/// <param name="Url">远程图片 URL。</param>
/// <param name="Raw">原始元数据节点。</param>
public sealed record ImageEntry(string Id, string? Source, string? Url, Metadata Raw);
