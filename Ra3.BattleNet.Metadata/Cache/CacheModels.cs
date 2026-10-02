namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>本地快照的新鲜度。<see cref="Stale"/> 表示刷新失败但有最后有效快照可用，界面应显示"使用缓存"。</summary>
public enum CatalogFreshness
{
    /// <summary>本次刷新（或本次打开）确认与远端一致。</summary>
    Fresh,

    /// <summary>刷新失败，正在用上一次验证过的快照。</summary>
    Stale,

    /// <summary>没有任何可用快照（首次运行或缓存损坏）。</summary>
    Unavailable,
}

/// <summary>一次刷新的结论。</summary>
public enum CatalogRefreshOutcome
{
    /// <summary>远端与本地快照一致（304 且本地对象完好），或本地快照就是最新。</summary>
    Unchanged,

    /// <summary>拿到并验证了新快照，已切换当前指针。</summary>
    Updated,

    /// <summary>刷新失败，保留最后有效快照。</summary>
    Stale,

    /// <summary>刷新失败且没有可用快照。</summary>
    Unavailable,
}

/// <summary>稳定错误码。只增不改，宿主按它分流而不是解析消息文本。</summary>
public static class CacheErrorCodes
{
    /// <summary>没有任何可用快照。</summary>
    public const string NoCache = "no_cache";

    /// <summary>缓存里的指针或快照结构损坏。</summary>
    public const string CacheCorrupt = "cache_corrupt";

    /// <summary>缓存根已绑定别的入口来源。</summary>
    public const string OriginMismatch = "origin_mismatch";

    /// <summary>网络不可用或超时。</summary>
    public const string NetworkUnavailable = "network_unavailable";

    /// <summary>远端返回了非成功状态码。</summary>
    public const string HttpStatus = "http_status";

    /// <summary>响应体超过本类资源的体积上限。</summary>
    public const string TooLarge = "too_large";

    /// <summary>响应体不是合法 XML（含 HTML 挑战页、截断、DTD 外部实体）。</summary>
    public const string InvalidXml = "invalid_xml";

    /// <summary>SchemaVersion 与本库不兼容。</summary>
    public const string SchemaMismatch = "schema_mismatch";

    /// <summary>内容与声明的摘要或大小不符。</summary>
    public const string DigestMismatch = "digest_mismatch";

    /// <summary>指针指向的快照文件缺失或已损坏。</summary>
    public const string SnapshotMissing = "snapshot_missing";

    /// <summary>拿别的缓存根（或别的实例）的快照来解析资源。</summary>
    public const string ForeignSnapshot = "foreign_snapshot";

    /// <summary>登记节点没有可用的 Source，或 Source 里有非法字符。</summary>
    public const string InvalidSource = "invalid_source";

    /// <summary>来源协议不受支持（例如远端文档指向本机文件、或 data/ftp 之类）。</summary>
    public const string UnsupportedScheme = "unsupported_scheme";

    /// <summary>来源解析后跑出了入口目录，或用了绝对路径。</summary>
    public const string PathEscape = "path_escape";

    /// <summary>登记节点声明的摘要算法本库不认识。硬失败，不退化成"不校验"。</summary>
    public const string UnsupportedHashAlgorithm = "unsupported_hash_algorithm";

    /// <summary>资源不存在（404 之类）或远端明确拒绝提供。</summary>
    public const string ResourceUnavailable = "resource_unavailable";

    /// <summary>本地状态文件的格式版本本库不认识：保留原文件，不覆盖为新空文件。</summary>
    public const string UnsupportedFormat = "unsupported_format";

    /// <summary>另一个进程正占着这个缓存根（跨进程文件租约没拿到）。</summary>
    public const string Busy = "cache_busy";

    /// <summary>按容量策略回收之后仍超出上限：保留有效快照并报错，不先破坏唯一可用缓存。</summary>
    public const string CacheFull = "cache_full";
}

/// <summary>结构化错误。消息里不得出现带凭据的完整请求地址。</summary>
/// <param name="Code">见 <see cref="CacheErrorCodes"/>。</param>
/// <param name="Message">给人看的说明（已脱敏）。</param>
public sealed record CacheError(string Code, string Message)
{
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>
/// 一次验证过的本地快照：根原文、解析结果与来源身份。
/// 它与 <c>Installed</c> 安装记录是两回事 —— 这里只说明"发布者当前声明了什么"。
/// </summary>
public sealed class CatalogSnapshot
{
    /// <summary>快照 ID = 根 XML 原文字节的 SHA-256（小写十六进制）。</summary>
    public required string SnapshotId { get; init; }

    /// <summary>本快照所在的缓存根。</summary>
    public required string CacheRoot { get; init; }

    /// <summary>入口地址（远端 origin）；相对 Source 一律按它解析。</summary>
    public required Uri OriginEntryUri { get; init; }

    /// <summary>取得时间（UTC）。</summary>
    public required DateTimeOffset RetrievedAtUtc { get; init; }

    /// <summary>本地根 XML 原文路径（只读）。</summary>
    public required string MetadataPath { get; init; }

    /// <summary>解析后的元数据根。</summary>
    public required Metadata Root { get; init; }

    /// <summary>发布物契约版本；根上缺失时为 null。</summary>
    public string? SchemaVersion { get; init; }

    /// <summary>发布者标签（<c>ContentRevision</c>）；它不能独自证明字节一致。</summary>
    public string? ContentRevision { get; init; }

    /// <summary>业务查询入口。</summary>
    public MetadataCatalog Catalog => new(Root);
}

/// <summary><see cref="MetadataCache.OpenAsync"/> 的结果：只读本地，不联网。</summary>
public sealed class CatalogLoadResult
{
    /// <summary>快照新鲜度。打开时永远不可能是 <see cref="CatalogFreshness.Fresh"/>。</summary>
    public required CatalogFreshness Status { get; init; }

    /// <summary>可用快照；<see cref="CatalogFreshness.Unavailable"/> 时为 null。</summary>
    public CatalogSnapshot? Snapshot { get; init; }

    /// <summary>不可用时的原因。</summary>
    public CacheError? Error { get; init; }
}

/// <summary><see cref="MetadataCache.RefreshAsync"/> 的结果。</summary>
public sealed class CatalogRefreshResult
{
    /// <summary>刷新结论。</summary>
    public required CatalogRefreshOutcome Outcome { get; init; }

    /// <summary>刷新后仍然可用（或仍然不可用）的快照。</summary>
    public CatalogSnapshot? Snapshot { get; init; }

    /// <summary>失败原因。</summary>
    public CacheError? Error { get; init; }
}
