namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 一张已登记图片的读取结果。调用方拿到的是可以安全读完的正文，不涉及任何宿主控件类型。
/// </summary>
/// <param name="Bytes">图片正文；不可用时为 null。</param>
/// <param name="ContentType">图片响应类型（如 <c>image/webp</c>）；不可用时为 null。</param>
/// <param name="Status">数据来源：本次新下载为 Fresh，本地已有缓存为 Stale，获取失败无数据为 Unavailable。</param>
/// <param name="Error">报错信息。请求成功、返回 304 或内容未变时为 null；刷新失败但本地仍有旧图可用时，会保留旧图并填入具体错误。</param>
public sealed record ImageResult(byte[]? Bytes, string? ContentType, MetadataFreshness Status, string? Error)
{
    /// <summary>有可展示正文。</summary>
    public bool Ok => Bytes is not null && !string.IsNullOrWhiteSpace(ContentType);

    internal static ImageResult Unavailable(string error) => new(null, null, MetadataFreshness.Unavailable, error);
}

/// <summary>
/// 图片首次下载成功或远端内容发生变化时触发。
/// 服务器返回 304、刷新报错或图片文件内容未变时不会触发。
/// </summary>
/// <param name="ImageUri">规范化后的图片地址（含查询串）。</param>
public sealed record ImageUpdatedEventArgs(string ImageUri);
