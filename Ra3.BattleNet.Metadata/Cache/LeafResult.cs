using Ra3.BattleNet.Metadata;

namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 一个包叶子清单的读取结果。ManifestNode 与 Entry 来自同一次解析。
/// </summary>
/// <param name="Document">叶子文档；不可用时为 null。</param>
/// <param name="ManifestNode">与 <paramref name="Entry"/> 对应的 Manifest 节点。</param>
/// <param name="Entry">类型化清单；存根或校验失败时为 null。</param>
/// <param name="SourceUri">调用方传入并规范化后的叶子地址，不是缓存路径。</param>
/// <param name="Status">本结果的新鲜度。仅磁盘命中为 Stale，不会冒充本轮确认。</param>
/// <param name="Error">失败或回退原因；成功时为 null。</param>
public sealed record LeafResult(
    Metadata? Document,
    Metadata? ManifestNode,
    ManifestEntry? Entry,
    Uri SourceUri,
    MetadataFreshness Status,
    string? Error);

/// <summary>
/// 一轮叶子预加载的结果：这一轮一共要拉几个叶子，其中几个拿到了可用文档。
/// 网络失败、超时、解析失败和没有缓存可回退都算在 <paramref name="Failed"/> 里；
/// 没有这份摘要，调用方就分不清"拉完了"和"全都没拉到"。
/// </summary>
/// <param name="Total">这一轮要刷新的叶子数（相同 Source 只算一次）。</param>
/// <param name="Loaded">拿到可用文档的叶子数。</param>
/// <param name="Failed">没有可用文档的叶子数。</param>
public readonly record struct LeafPreloadSummary(int Total, int Loaded, int Failed)
{
    public static LeafPreloadSummary Empty => new(0, 0, 0);
}
