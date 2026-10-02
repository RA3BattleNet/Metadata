namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 资源类别。它决定两件事：本类资源的体积上限，以及**要不要每次找服务端核对**。
/// </summary>
public enum ResourceKind
{
    /// <summary>
    /// 叶子清单：决定"装什么"的控制数据，体积小、变更代价大。
    /// 每次解析都带条件请求核对，绝不拿本地旧字节冒充当前发布。
    /// </summary>
    Leaf,

    /// <summary>
    /// 展示媒体（图片、Markdown）。本地已有对象就直接用，不为了刷新一张图去联网。
    /// </summary>
    Media,
}

/// <summary>
/// 本地对象与远端声明的绑定强度。
/// 这三档不是实现细节：只有 <see cref="Strong"/> 才谈得上"这个字节确实是发布者声明的那一份"。
/// </summary>
public enum ResourceBinding
{
    /// <summary>登记节点声明了 SHA-256，下载后逐字节核对通过。</summary>
    Strong,

    /// <summary>只声明了 CRC32C/MD5 这类兼容完整性摘要：能发现意外损坏，不能当作发布者签名。</summary>
    Legacy,

    /// <summary>登记节点什么都没声明：本地对象只证明"某个时刻从该地址取到过这些字节"。</summary>
    None,
}

/// <summary>
/// 调用方要解析的一个登记资源。只描述"要哪个登记节点的哪份 Source"，不自己拼缓存路径
/// （调用方拼 <c>cacheRoot + 远端 Source</c> 正是本 API 要消灭的做法）。
/// </summary>
public sealed class ResourceRef
{
    /// <summary>发布登记 ID（限定 ID，如 <c>mods/corona/corona:corona-icon-64px</c>）。用作映射键。</summary>
    public required string RegistryId { get; init; }

    /// <summary>登记节点上的 <c>Source</c> 原文，相对或绝对都行。</summary>
    public required string Source { get; init; }

    /// <summary>资源类别。</summary>
    public required ResourceKind Kind { get; init; }

    /// <summary>登记节点声明的摘要；没声明时为 null。</summary>
    public string? ExpectedHash { get; init; }

    /// <summary>登记节点声明的摘要算法（CRC32C / MD5 / SHA256）；没声明时为 null。</summary>
    public string? ExpectedHashAlgorithm { get; init; }

    /// <summary>登记节点声明的大小；没声明时为 null。</summary>
    public long? ExpectedSize { get; init; }
}

/// <summary>解析成功后的只读资源：本地路径 + 来源身份 + 绑定强度。</summary>
public sealed class CachedResource
{
    /// <summary>登记 ID。</summary>
    public required string RegistryId { get; init; }

    /// <summary>按**原远端入口**解析出来的地址（不是缓存目录里的路径）。</summary>
    public required Uri SourceUri { get; init; }

    /// <summary>本地只读对象路径（<c>objects/&lt;sha256&gt;/payload</c>）。对象不可原地修改。</summary>
    public required string LocalPath { get; init; }

    /// <summary>对象字节的 SHA-256（本地计算，只证明"此后是否同一份字节"）。</summary>
    public required string Sha256 { get; init; }

    /// <summary>对象字节数。</summary>
    public required long Size { get; init; }

    /// <summary>与远端声明的绑定强度。</summary>
    public required ResourceBinding Binding { get; init; }

    /// <summary>true = 直接命中本地对象，本次没有联网。</summary>
    public required bool FromCache { get; init; }

    /// <summary>打开只读流；调用方负责释放。</summary>
    public Stream OpenRead() => new FileStream(LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
}

/// <summary>
/// 资源解析结果。失败不是异常：媒体取不到只该占位，叶子取不到该让对应 Package 报"资源未就绪"，
/// 两者都不该把宿主进程搞崩。
/// </summary>
public sealed class ResourceResolveResult
{
    /// <summary>成功时的资源；失败为 null。</summary>
    public CachedResource? Resource { get; init; }

    /// <summary>失败原因。</summary>
    public CacheError? Error { get; init; }

    /// <summary>是否成功。</summary>
    public bool Success => Resource is not null;
}
