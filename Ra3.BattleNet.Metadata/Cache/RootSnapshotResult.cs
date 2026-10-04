using Ra3.BattleNet.Metadata;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 根清单快照。同一轮共享刷新返回同一个实例；失败或重新打开会换新的 RefreshId，不会把旧确认标成 Fresh。
/// </summary>
/// <param name="Document">解析后的根文档；不可用时为 null。</param>
/// <param name="Catalog">文档上的查询目录，与 <paramref name="Document"/> 一起复用。</param>
/// <param name="OriginUri">规范化后的入口地址（metadata.xml 的 http(s) 或 file URI），不是缓存目录。</param>
/// <param name="Status">本结果的新鲜度。</param>
/// <param name="RefreshId">这一次打开或刷新的标识。</param>
/// <param name="RefreshedAt">结果生成时间（UTC）。</param>
/// <param name="Error">失败原因；成功的 Fresh，以及没有错误的离线 Stale，为 null。</param>
public sealed record RootSnapshotResult(
    Metadata? Document,
    MetadataCatalog? Catalog,
    Uri? OriginUri,
    MetadataFreshness Status,
    string RefreshId,
    DateTimeOffset RefreshedAt,
    string? Error);
