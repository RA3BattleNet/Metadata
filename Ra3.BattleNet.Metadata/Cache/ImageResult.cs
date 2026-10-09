namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 一张已登记图片的读取结果。调用方拿到的是可以安全读完的正文，不涉及任何宿主控件类型。
/// </summary>
/// <param name="Bytes">图片正文；不可用时为 null。</param>
/// <param name="ContentType">图片响应类型（如 <c>image/webp</c>）；不可用时为 null。</param>
/// <param name="Status">本结果的来源：本轮新正文为 Fresh，已有缓存为 Stale，没有正文为 Unavailable。</param>
/// <param name="Error">失败原因。成功，以及 304 或字节未变时为 null。刷新失败但仍有旧图时，正文保留且 Error 非空。</param>
public sealed record ImageResult(byte[]? Bytes, string? ContentType, MetadataFreshness Status, string? Error)
{
    /// <summary>有可展示正文。</summary>
    public bool Ok => Bytes is not null && !string.IsNullOrWhiteSpace(ContentType);

    internal static ImageResult Unavailable(string error) => new(null, null, MetadataFreshness.Unavailable, error);
}

/// <summary>
/// 图片从不可用变为可展示，或同地址正文已被不同字节替换。只描述传输层事实，不涉及登记 ID。
/// 304、失败和字节未变都不触发。
/// </summary>
/// <param name="ImageUri">规范化后的图片地址（含查询串）。</param>
public sealed record ImageUpdatedEventArgs(string ImageUri);
