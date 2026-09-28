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

/// <summary>
/// 清单文件来源实体（HTTP 直链或 BT 种子）。
/// </summary>
/// <param name="Type">来源类型，统一大写（HTTP/BT）。</param>
/// <param name="Url">来源地址。</param>
public sealed record ManifestSourceEntry(string Type, string Url);

/// <summary>
/// 清单文件条目实体。
/// </summary>
/// <param name="FileName">安装名（解压后的文件名）。</param>
/// <param name="RelativePath">相对安装根的路径。</param>
/// <param name="Hash">解压后安装文件的 CRC32C。</param>
/// <param name="Size">下载物字节数；缺失或无法解析为 null。</param>
/// <param name="DownloadName">下载名；清单未声明时为 null（等同 <paramref name="FileName"/>）。</param>
/// <param name="Compression">下载物的压缩格式（小写）；清单未声明时为 null。</param>
/// <param name="KindOf">文件种类标记（原样保留）。</param>
/// <param name="Sources">下载来源列表；清单未声明时为空列表。</param>
/// <param name="Raw">原始元数据节点。</param>
/// <param name="Mount">挂载角色，小写；清单未声明时为 base。</param>
/// <param name="Language">语言包标记；仅 language 角色使用。</param>
/// <param name="Package">可选包标记；仅 optional 角色使用。</param>
public sealed record ManifestFileEntry(string FileName, string RelativePath, string Hash, long? Size, string? DownloadName, string? Compression, string KindOf, IReadOnlyList<ManifestSourceEntry> Sources, Metadata Raw, string? Mount, string? Language, string? Package);

/// <summary>
/// 清单依赖 DLL 实体。
/// </summary>
/// <param name="Name">DLL 名称。</param>
/// <param name="Version">DLL 版本；可缺省。</param>
/// <param name="Hash">DLL 哈希值。</param>
/// <param name="KindOf">DLL 种类标记；可缺省。</param>
public sealed record ManifestDllEntry(string Name, string? Version, string Hash, string? KindOf);

/// <summary>skudef 指令种类。</summary>
public enum SkudefCommandKind
{
    /// <summary>挂载清单里的一个文件。</summary>
    Big,

    /// <summary>挂载模组目录下用户自己放的文件。</summary>
    Config,
}

/// <summary>
/// 清单里的一条 skudef 指令。
/// </summary>
/// <param name="Kind">指令种类。</param>
/// <param name="Target">AddBig 为 FileName；AddConfig 为本地纯文件名。</param>
/// <param name="Optional">仅 AddConfig：本地文件不存在时跳过。</param>
/// <param name="Language">仅 AddBig：客户端语言设置等于它才挂。</param>
/// <param name="Package">仅 AddBig：客户端开关开着才挂。</param>
public sealed record ManifestSkudefCommand(SkudefCommandKind Kind, string Target, bool Optional, string? Language, string? Package);

/// <summary>
/// 清单声明的 skudef：头版本加有序指令，子元素顺序就是输出顺序。
/// </summary>
/// <param name="GameVersion">头版本，形如 1.12。</param>
/// <param name="Commands">有序列的挂载指令。</param>
public sealed record ManifestSkudefEntry(string GameVersion, IReadOnlyList<ManifestSkudefCommand> Commands)
{
    /// <summary>清单没写 GameVersion 时用的头版本。</summary>
    public const string DefaultGameVersion = "1.12";
}

/// <summary>
/// 叶子清单实体。
/// </summary>
/// <param name="Id">清单 ID。</param>
/// <param name="HashAlgorithm">哈希算法；缺失按 CRC32C。</param>
/// <param name="Files">文件条目列表。</param>
/// <param name="Dependencies">依赖 DLL 列表。</param>
/// <param name="Skudef">清单声明的 skudef；没写时为 null（客户端按旧算法生成）。</param>
public sealed record ManifestEntry(string Id, string HashAlgorithm, IReadOnlyList<ManifestFileEntry> Files, IReadOnlyList<ManifestDllEntry> Dependencies, ManifestSkudefEntry? Skudef = null);
