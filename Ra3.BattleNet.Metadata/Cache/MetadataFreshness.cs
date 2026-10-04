namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 一份元数据相对本轮刷新的确认状态。
/// Fresh 只表示本次进程里网络或本地源读取已经确认；OpenSnapshot 不会返回 Fresh。
/// </summary>
public enum MetadataFreshness
{
    /// <summary>本轮刷新已确认（HTTP 200、校验通过的 304，或 file/本地源读取成功）。</summary>
    Fresh,

    /// <summary>有上次校验合法的数据，但本轮没有确认。离线打开和刷新失败回退都是这个状态。</summary>
    Stale,

    /// <summary>没有可用数据。</summary>
    Unavailable,
}
