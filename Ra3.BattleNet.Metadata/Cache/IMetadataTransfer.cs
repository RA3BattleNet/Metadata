namespace Ra3.BattleNet.Metadata.Cache;

/// <summary>
/// 一次正文传输的请求：要取哪个地址、传输方必须把正文写到哪个本地文件、以及本地缓存里的条件请求校验器。
/// </summary>
/// <param name="Source">正文的源地址（http/https）。</param>
/// <param name="StagingPath">传输方成功时必须写入完整正文的本地路径。这个文件在 200 返回后归
/// <see cref="MetadataClient"/> 所有：它要么被校验通过后搬进磁盘缓存，要么被删除。</param>
/// <param name="ETag">本地缓存记录的 ETag；没有则为 null。传输方据此发 <c>If-None-Match</c>。</param>
/// <param name="LastModified">本地缓存记录的 Last-Modified；没有则为 null。传输方据此发 <c>If-Modified-Since</c>。</param>
public sealed record MetadataTransferRequest(Uri Source, string StagingPath, string? ETag = null, string? LastModified = null);

/// <summary>
/// 一次正文传输的结果：真实 HTTP 状态码，外加服务端给出的校验器与内容类型（用于写回缓存）。
/// 200 表示 <see cref="MetadataTransferRequest.StagingPath"/> 上已经是一份完整正文；304 与其它状态码表示没有落盘。
/// 传输本身的失败（网络、超时、磁盘）直接抛异常，不要用状态码敷衍。
/// </summary>
/// <param name="StatusCode">真实 HTTP 状态码。</param>
/// <param name="ETag">响应里的 ETag，没有则为 null。</param>
/// <param name="LastModified">响应里的 Last-Modified，没有则为 null。</param>
/// <param name="ContentType">响应里的 Content-Type（图片按它决定类型），没有则为 null。</param>
public sealed record MetadataTransferResult(int StatusCode, string? ETag = null, string? LastModified = null, string? ContentType = null);

/// <summary>
/// <see cref="MetadataClient"/> 的正文传输口：把源地址的正文下载到一个本地文件，而不是塞进内存。
/// 默认实现是内置的 <see cref="HttpClient"/> 传输（库自用与测试）；调用方可以注入自己的实现，
/// 两者遵守同一份契约，XML、图片与 Markdown 都只有这一条传输路径。
/// </summary>
/// <remarks>
/// 所有权：注入的实例归注入方所有，<see cref="MetadataClient"/> 不会释放它；
/// 客户端自己建的那个（<c>transfer</c> 传 null）由客户端释放。
/// </remarks>
public interface IMetadataTransfer : IDisposable
{
    /// <summary>
    /// 取回一份正文。<c>200</c> 时必须把完整正文写到 <see cref="MetadataTransferRequest.StagingPath"/>，
    /// 其它状态码不得留下文件；失败抛异常，且不得把半截文件留给调用方。
    /// </summary>
    Task<MetadataTransferResult> FetchAsync(MetadataTransferRequest request, CancellationToken cancellationToken);
}
