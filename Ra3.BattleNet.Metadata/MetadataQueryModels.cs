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
public sealed record ModEntry(string Id, string? Version, string? Icon, IReadOnlyList<LocalizedTextEntry> DisplayNames, IReadOnlyList<PackageEntry> Packages, Metadata Raw)
{
    /// <summary>所属 Mod 声明的通用设置定义列表；未声明时为空列表。设置 ID 为 Mod 内局部 ID。</summary>
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
    /// <summary>DLL 在当前清单内的局部唯一 ID；旧版本清单未声明此字段时为 null。当前清单内的注入动作（InjectDll/RequireDll）和桥接动作（ConfigureLuaBridge）均通过此 ID 引用。</summary>
    public string? Id { get; init; }

    /// <summary>DLL 注入协议（如 lyi-create-process、easyhook）；仅当 DLL 需要被注入时必须显式指定。已知合法值见 <see cref="ModSettingsContract.KnownProtocols"/>。</summary>
    public string? Protocol { get; init; }

    /// <summary>DLL 在加载时需要的特定运行参数；未声明时为 null。</summary>
    public ManifestDllCustomData? CustomData { get; init; }
}
/// <summary>
/// DLL 声明的特定运行参数（<c>CustomData</c> 节点）。
/// 目前用于告知加载器文本编码格式与特定运行时参数。当前仅支持 <c>Encoding="utf8"</c> 以及专用的 <c>RuntimeValue Name="log-file"</c>。
/// </summary>
/// <param name="Encoding">字符编码名称，当前固定为 <c>utf8</c>。</param>
/// <param name="RuntimeValue">运行时传参类型名称，当前固定为 <c>log-file</c>。</param>
public sealed record ManifestDllCustomData(string Encoding, string RuntimeValue);

/// <summary>模组设置的展示与数据类型，目前支持布尔开关与单选列表。</summary>
public enum ModSettingKind
{
    /// <summary>布尔开关；可配置值为 <c>true</c> 或 <c>false</c>。</summary>
    Boolean,

    /// <summary>单选枚举；必须从声明的 <see cref="ModSettingOption"/> 选项列表中选中一项其 Value。</summary>
    Choice,
}

/// <summary>
/// 单选设置（Choice）中的一个具体可选项。
/// </summary>
/// <param name="Value">选项的机器标识代码（例如 <c>normal</c>、<c>crates</c>、<c>chs</c>、<c>en</c>）。该值与文案语言解耦，仅用于底层逻辑识别。</param>
/// <param name="DisplayNames">选项的多语言显示名称列表，顺序与 XML 中声明顺序一致。</param>
/// <param name="Descriptions">选项的多语言详细说明文本列表，顺序与 XML 中声明顺序一致。</param>
public sealed record ModSettingOption(string Value, IReadOnlyList<LocalizedTextEntry> DisplayNames, IReadOnlyList<LocalizedTextEntry> Descriptions);

/// <summary>
/// Mod 级别的设置项定义实体（对应 XML 中的 <c>Mod/Settings/Boolean</c> 或 <c>Mod/Settings/Choice</c>）。
/// 注意：设置 ID 是所属 Mod 内部的局部唯一标识，不含全根资源前缀。
/// </summary>
/// <param name="Id">设置项的局部标识符（Mod 内唯一）。</param>
/// <param name="Kind">设置项类型（Boolean 或 Choice）。</param>
/// <param name="Default">默认值；Boolean 必须是 <c>true</c> 或 <c>false</c>，Choice 必须是某个合法 Option 的 <c>Value</c>。</param>
/// <param name="DisplayNames">设置项的多语言显示名称。</param>
/// <param name="Descriptions">设置项的多语言详细说明文本。</param>
/// <param name="Options">Choice 类型的候选选项列表；若为 Boolean 类型则固定为空列表。</param>
public sealed record ModSettingDefinition(
    string Id,
    ModSettingKind Kind,
    string Default,
    IReadOnlyList<LocalizedTextEntry> DisplayNames,
    IReadOnlyList<LocalizedTextEntry> Descriptions,
    IReadOnlyList<ModSettingOption> Options);

/// <summary>设置开关被激活时执行的动作种类。</summary>
public enum ModSettingActionKind
{
    /// <summary>挂载 BIG 资源包；Target 对应 Skudef 中预先声明的 Package 标识。</summary>
    MountPackage,

    /// <summary>根据当前设置值挂载对应的模组语言包（直传用户选中的语言代码）；无需指定 Target。</summary>
    MountLanguage,

    /// <summary>在满足条件时注入指定 DLL；Target 对应当前 Manifest Dependencies 中声明的 Dll 局部 ID。</summary>
    InjectDll,

    /// <summary>配置 Lua 桥接模块（RA3LuaBridge）；Target 为目标 Dll 局部 ID，Adapter 为具体的适配器功能名称。</summary>
    ConfigureLuaBridge,
}

/// <summary>
/// 模组设置触发的具体执行动作。
/// </summary>
/// <param name="Kind">动作种类。</param>
/// <param name="Target">动作目标对象（挂载包时为 Package 名称，注入或桥接时为 Dll 局部 ID；MountLanguage 时为 null）。</param>
/// <param name="Adapter">仅用于 ConfigureLuaBridge 动作：所指定的 LuaBridge 适配器名称。</param>
public sealed record ModSettingAction(ModSettingActionKind Kind, string? Target, string? Adapter);

/// <summary>
/// 单选设置（Choice）命中特定选项值时触发的动作分支。
/// </summary>
/// <param name="Value">匹配的选项值（对应 Option 的 Value）。</param>
/// <param name="Actions">该选项被选中时需要执行的动作列表。</param>
public sealed record ModSettingCase(string Value, IReadOnlyList<ModSettingAction> Actions);

/// <summary>
/// 版本清单中的模组设置绑定（对应 XML 中的 <c>Manifest/Settings/SettingRef</c>）。
/// 一律使用 Ref 引用所属 Mod 已经定义的设置 ID，不在此处重新定义。
/// </summary>
/// <param name="Ref">引用的所属 Mod 设置局部 ID。</param>
/// <param name="Actions">直接绑定动作列表。Boolean 时挂包与 Inject 仅在 true 时生效，ConfigureLuaBridge 始终传递实际 true/false 布尔值供协议处理；Choice 这里只允许直传 MountLanguage。</param>
/// <param name="Cases">单选设置的分支动作列表；若为 Boolean 项则固定为空列表。</param>
public sealed record ModSettingBinding(string Ref, IReadOnlyList<ModSettingAction> Actions, IReadOnlyList<ModSettingCase> Cases);

/// <summary>
/// 清单中声明的无条件注入条目（来自 <c>Manifest/Injection</c> 下的 <c>RequireDll</c> 或 <c>InjectDll</c>）。
/// </summary>
/// <param name="Inject">true 表示 InjectDll（无论任何设置开关，启动时必须注入该 DLL）；false 表示 RequireDll（仅要求该 DLL 必须存在且哈希匹配，但默认不注入）。</param>
/// <param name="Ref">引用的清单依赖 Dll 局部 ID。</param>
public sealed record ManifestInjectionAction(bool Inject, string Ref);

/// <summary>
/// 模组设置快照：由根 Mod 定义的完整设置列表与当前版本 Manifest 的设置绑定列表配对组成。
/// 该快照用于客户端在安装模组版本时持久化保存，确保后续离线运行、历史版本启动时即使没有联网下载全局元数据也能依据快照正确解析设置和启动。
/// </summary>
/// <param name="Definitions">所属 Mod 级设置定义完整列表。</param>
/// <param name="Bindings">当前版本清单设置绑定完整列表。</param>
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
    /// <summary>当前版本清单中声明的设置绑定列表（来自 <c>Manifest/Settings</c>）；未声明时为空列表。</summary>
    public IReadOnlyList<ModSettingBinding> Settings { get; init; } = [];

    /// <summary>
    /// 清单显式声明的无条件注入与依赖校验动作（来自 <c>Manifest/Injection</c>）。
    /// 未声明该节点的旧清单为 null，沿用旧路线（全量依赖校验，是否注入由旧规则决定）；
    /// 显式声明该节点（含空节点 <c>&lt;Injection /&gt;</c>）时启用新机制，仅显式声明的 InjectDll 或被开关激活的 InjectDll 才会注入。
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
