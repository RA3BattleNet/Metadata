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
