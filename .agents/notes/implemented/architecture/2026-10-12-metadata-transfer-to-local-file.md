# Agent Note: 正文先下载到本地文件再解析与发布

Status: implemented

## Problem

库原先自己内联 `HttpClient`，把响应正文读成字节再写缓存：传输不可替换（宿主无法插手代理、重试或平台网络栈），也没法“先把正文落到本地再解析”。服务端不稳时，宿主想要的是可替换的传输边界与一条“完整正文才发布”的路径，而不是库内部硬编码的字节中转。

## Decision

库负责解析与下载的全部内容：网络正文先由传输实现下载到一个本地临时文件，再由库读取、校验并原子发布进缓存。没有 `Downloader` / `HttpDownloader` 库，桌面端只调用本库。

- 传输边界是 `Cache/IMetadataTransfer.cs`。请求 `MetadataTransferRequest(Uri Source, string StagingPath, string? ETag, string? LastModified)`；结果 `MetadataTransferResult(int StatusCode, string? ETag, string? LastModified, string? ContentType)`。
- 语义：`200` 时传输方必须把完整正文写到 `StagingPath`；其它状态码不得留下文件；传输本身的失败（网络、超时、磁盘）直接抛异常，不用状态码敷衍。
- 校验器原样转发：本地缓存的 `ETag` / `LastModified` 进入请求，默认传输据此发 `If-None-Match` / `If-Modified-Since`，`304` 原样回报。
- 默认实现 `Cache/HttpMetadataTransfer.cs`：`HttpClient` 直下文件；非 `200` 不落盘；`HttpClient` 由库创建的才由库释放。
- 临时目录：构造函数传 `stagingDirectory` 优先；否则有磁盘缓存时为 `<cacheDir>/.stage`，无磁盘缓存时为系统临时目录下的 `ra3-metadata-stage`。目录在第一次真正需要网络传输时创建，本地来源不碰它。
- 归属：注入的传输实现归调用方所有，`MetadataClient` 不释放它；只有库自己默认建的那个由库释放。`MetadataClient` 构造函数的 `httpClient` 参数只在库自用默认传输时有意义。
- `MetadataDiskCache.StageFile` 把校验通过、已经在本地落盘的正文搬进缓存临时位，不重写字节；正式发布仍由 `Commit` 原子完成。
- `304`、非 `200`、调用方取消与异常一律不留半截文件：没交出去的临时正文在 `finally` 里删除，交出去的由库在校验失败时删除。

## Alternatives considered

- 保留内联 `HttpClient` 的字节路径：宿主无法替换传输，正文一定要进内存再回写，服务端不稳时没有可插拔的重试/代理点。
- 改用 `Downloader` / `HttpDownloader` 库：它在 `DownloadService.StartDownload` 里每次先发 `Range: 0-0` 探针再发正文，固定两次请求且无法关配置；对每个叶子与图片都翻倍，还拿不到我们需要自己转发校验器的条件请求语义。
- 把库做成纯解析、下载全交宿主：校验器与缓存旁挂文件的归属本来就在库这边，切开会把 `304` 复用与缓存归属拆到两处，重复判断路径映射与条件请求。

## Consequences

- 正文走“先落本地文件再解析”，内存里不再整份驻留，缓存发布仍在 `Commit` 里原子完成。
- 传输实现可替换，宿主与测试都能注入自己的传输；库只释放自己默认创建的那个实例。
- 代价：多一次本地临时文件写入与一次移动/复制（`StageFile` 用 `File.Move`，跨卷时退化为复制加删除），并且库多了一份 `IMetadataTransfer` 契约要保持向后兼容。
- 已知上限：临时目录可用空间不足或不可写时本次发布失败；传输实现若非本库默认实现，必须自己遵守“非 200 不落盘、失败不留半截文件”的契约。

## Verification

`dotnet test Metadata.sln --no-restore --nologo` 实测：265 通过，0 失败，0 跳过。其中 `MetadataClientTransferTests` 8 例：成功落盘并搬进缓存、无效正文不发布且清理临时文件、截断正文不发布且清理临时文件、传输异常不留文件、调用方取消保留共享传输且 `Dispose` 清理临时文件、`304` 复用缓存不产生文件、`404` 返回状态、无磁盘缓存仍读取落盘正文并清理。
