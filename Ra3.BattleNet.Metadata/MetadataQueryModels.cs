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
    /// 获取全部 Mod 实体（延迟求值，可接 LINQ）。
    /// </summary>
    public IEnumerable<ModEntry> Mods => _root.Mods();

    /// <summary>
    /// 获取全部 Application 实体（延迟求值，可接 LINQ）。
    /// </summary>
    public IEnumerable<ApplicationEntry> Applications => _root.Applications();

    /// <summary>
    /// 获取全部 Markdown 资源（延迟求值，可接 LINQ）。
    /// </summary>
    public IEnumerable<MarkdownEntry> Markdowns => _root.Markdowns();

    /// <summary>
    /// 获取全部图片资源（延迟求值，可接 LINQ）。
    /// </summary>
    public IEnumerable<ImageEntry> Images => _root.Images();

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
/// <param name="DisplayNames">分语言的显示名；没写时为空列表。</param>
/// <param name="Packages">版本包列表。</param>
/// <param name="Raw">原始元数据节点。</param>
/// <param name="Settings">Mod 级通用设置定义（<c>Mod/Settings</c>）；没写时为空列表。局部 ID，不加全根前缀。</param>
public sealed record ModEntry(string Id, string? Version, string? Icon, IReadOnlyList<LocalizedTextEntry> DisplayNames, IReadOnlyList<PackageEntry> Packages, Metadata Raw)
{
    /// <summary>Mod 级通用设置定义；没写时为空列表。</summary>
    public IReadOnlyList<ModSettingDefinition> Settings { get; init; } = [];
}

/// <summary>
/// 分语言的文本（<c>DisplayName</c> / <c>Title</c> / <c>Description</c> 这类带 <c>@Language</c> 的节点）。
/// </summary>
/// <param name="Language">语言标记，如 <c>zh-CN</c>。</param>
/// <param name="Text">该语言下的文本。</param>
public sealed record LocalizedTextEntry(string Language, string Text);

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
public sealed record ManifestDllEntry(string Name, string? Version, string Hash, string? KindOf)
{
    /// <summary>稳定局部 ID；旧清单未声明时为 null。引用节点（RequireDll/InjectDll/ConfigureLuaBridge）以此定位。</summary>
    public string? Id { get; init; }

    /// <summary>注入协议；仅注入目标声明。已知值见 <see cref="ModSettingsContract.KnownProtocols"/>。</summary>
    public string? Protocol { get; init; }

    /// <summary>类型化运行参数；未声明时为 null。</summary>
    public ManifestDllCustomData? CustomData { get; init; }
}

/// <summary>
/// DLL 声明的类型化运行参数（<c>CustomData</c>）。当前仅支持 <c>Encoding="utf8"</c> 与受限运行值。
/// </summary>
/// <param name="Encoding">编码标记；当前仅 <c>utf8</c>。</param>
/// <param name="RuntimeValue">受限运行值名；当前仅 <c>log-file</c>。</param>
public sealed record ManifestDllCustomData(string Encoding, string RuntimeValue);

/// <summary>模组设置类型。仅支持布尔与单选枚举。</summary>
public enum ModSettingKind
{
    /// <summary>布尔开关；值为 <c>true</c>/<c>false</c>。</summary>
    Boolean,

    /// <summary>单选枚举；值取自声明的 <see cref="ModSettingOption"/>。</summary>
    Choice,
}

/// <summary>
/// 枚举设置的一个合法选项。
/// </summary>
/// <param name="Value">选项值（模组内容代号，如 <c>chs</c>/<c>en</c>），与界面文案语言族解耦。</param>
/// <param name="DisplayNames">分语言显示名；声明顺序保留。</param>
/// <param name="Descriptions">分语言描述；声明顺序保留。</param>
public sealed record ModSettingOption(string Value, IReadOnlyList<LocalizedTextEntry> DisplayNames, IReadOnlyList<LocalizedTextEntry> Descriptions);

/// <summary>
/// Mod 级稳定设置定义（<c>Mod/Settings/Boolean|Choice</c>）。ID 为 Mod 内局部 ID，不加全根前缀。
/// </summary>
/// <param name="Id">稳定局部 ID。</param>
/// <param name="Kind">设置类型。</param>
/// <param name="Default">默认值；Boolean 为 <c>true</c>/<c>false</c>，Choice 为某个 Option 的 <c>Value</c>。</param>
/// <param name="DisplayNames">分语言显示名。</param>
/// <param name="Descriptions">分语言描述；可空。</param>
/// <param name="Options">Choice 的合法选项；Boolean 为空列表。</param>
public sealed record ModSettingDefinition(
    string Id,
    ModSettingKind Kind,
    string Default,
    IReadOnlyList<LocalizedTextEntry> DisplayNames,
    IReadOnlyList<LocalizedTextEntry> Descriptions,
    IReadOnlyList<ModSettingOption> Options);

/// <summary>设置绑定的动作种类。</summary>
public enum ModSettingActionKind
{
    /// <summary>条件挂载 BIG 包；Target 为 Package 名。</summary>
    MountPackage,

    /// <summary>条件匹配模组语言包；无 Target。</summary>
    MountLanguage,

    /// <summary>条件注入 DLL；Target 为 Dll 局部 ID。</summary>
    InjectDll,

    /// <summary>配置 LuaBridge 协议适配器；Target 为 Dll 局部 ID，Adapter 为适配器名。</summary>
    ConfigureLuaBridge,
}

/// <summary>
/// 设置命中的一个动作。
/// </summary>
/// <param name="Kind">动作种类。</param>
/// <param name="Target">目标（Package 名或 Dll ID）；MountLanguage 为 null。</param>
/// <param name="Adapter">仅 ConfigureLuaBridge：适配器名。</param>
public sealed record ModSettingAction(ModSettingActionKind Kind, string? Target, string? Adapter);

/// <summary>
/// 枚举设置命中某个 Option 值时的动作集合。
/// </summary>
/// <param name="Value">命中的 Option 值。</param>
/// <param name="Actions">该值激活的动作。</param>
public sealed record ModSettingCase(string Value, IReadOnlyList<ModSettingAction> Actions);

/// <summary>
/// 版本清单级设置绑定（<c>Manifest/Settings/SettingRef</c>）。
/// </summary>
/// <param name="Ref">指向所属 Mod 设置定义的局部 ID（引用语法，非定义）。</param>
/// <param name="Actions">无条件动作（Boolean 为 true 时 / Choice 的直接动作）。</param>
/// <param name="Cases">Choice 的有限值命中动作；Boolean 为空列表。</param>
public sealed record ModSettingBinding(string Ref, IReadOnlyList<ModSettingAction> Actions, IReadOnlyList<ModSettingCase> Cases);

/// <summary>
/// 无条件注入动作（<c>Manifest/Injection</c> 下的 <c>RequireDll</c>/<c>InjectDll</c>）。
/// </summary>
/// <param name="Inject">true 为 InjectDll（注入并计入必需校验）；false 为 RequireDll（仅校验存在性与哈希，不注入）。</param>
/// <param name="Ref">指向当前清单依赖的 Dll 局部 ID。</param>
public sealed record ManifestInjectionAction(bool Inject, string Ref);

/// <summary>
/// 设置快照：根 Mod 设置定义 + 版本清单绑定的配对。用于安装记录与离线/历史版本启动。
/// </summary>
/// <param name="Definitions">根 Mod 设置定义。</param>
/// <param name="Bindings">版本清单设置绑定。</param>
public sealed record ModSettingsSnapshot(IReadOnlyList<ModSettingDefinition> Definitions, IReadOnlyList<ModSettingBinding> Bindings);

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
public sealed record ManifestEntry(string Id, string HashAlgorithm, IReadOnlyList<ManifestFileEntry> Files, IReadOnlyList<ManifestDllEntry> Dependencies, ManifestSkudefEntry? Skudef = null)
{
    /// <summary>版本清单设置绑定（<c>Manifest/Settings</c>）；没写时为空列表。</summary>
    public IReadOnlyList<ModSettingBinding> Settings { get; init; } = [];

    /// <summary>
    /// 无条件注入动作（<c>Manifest/Injection</c>）。null 表示旧清单（无 <c>&lt;Injection&gt;</c> 节点，沿用旧执行链）；
    /// 非 null（含空列表）表示显式声明了新执行机制。
    /// </summary>
    public IReadOnlyList<ManifestInjectionAction>? Injection { get; init; }
}

/// <summary>
/// Application 更新线投影。ManifestUrl 已按入口地址解析为绝对地址。
/// </summary>
/// <param name="Version">UpdateKind@Current，与 Updater@Version 等值匹配。</param>
/// <param name="ManifestUrl">该版本 Updater@Source 的绝对地址。</param>
/// <param name="BaseUrl">UpdateKind@BaseUrl；空白为 null。</param>
/// <param name="FallbackBaseUrl">UpdateKind@FallbackBaseUrl；空白为 null。</param>
/// <param name="DisplayName">UpdateKind@DisplayName；空白为 null。</param>
public sealed record UpdaterEndpoint(
    string Version,
    string ManifestUrl,
    string? BaseUrl,
    string? FallbackBaseUrl,
    string? DisplayName);
