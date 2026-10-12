# Agent Note: 正文先下载到本地文件再解析与发布

Status: implemented

## Problem

库原先自己内联 `HttpClient`，把响应正文读成字节再写缓存：传输不可替换（宿主无法插手代理、重试或平台网络栈），也没法“先把正文落到本地再解析”。服务端不稳时，宿主想要的是可替换的传输边界与一条“完整正文才发布”的路径，而不是库内部硬编码的字节中转。

## Decision

库负责元数据 XML、登记图片与元数据 Markdown 的下载与解析：正文先由下载实现写到一个本地临时文件，再由库读取、校验，校验通过后替换缓存。没有 `Downloader` / `HttpDownloader` 库，桌面端只调用本库。

- 传输边界是 `Cache/IMetadataTransfer.cs`。请求 `MetadataTransferRequest(Uri Source, string StagingPath, string? ETag, string? LastModified)`；结果 `MetadataTransferResult(int StatusCode, string? ETag, string? LastModified, string? ContentType)`。
- 语义：`200` 时传输方必须把完整正文写到 `StagingPath`；其它状态码不得留下文件；传输本身的失败（网络、超时、磁盘）直接抛异常，不用状态码敷衍。
- 校验器原样转发：本地缓存的 `ETag` / `LastModified` 进入请求，默认传输据此发 `If-None-Match` / `If-Modified-Since`，`304` 原样回报。
- 默认实现 `Cache/HttpMetadataTransfer.cs`：`HttpClient` 直下文件；非 `200` 不落盘；`HttpClient` 由库创建的才由库释放。
- 临时目录：构造函数传 `stagingDirectory` 优先；否则有磁盘缓存时为 `<cacheDir>/.stage`，无磁盘缓存时为系统临时目录下的 `ra3-metadata-stage`。目录在第一次真正需要网络传输时创建，本地来源不碰它。
- 归属：注入的传输实现归调用方所有，`MetadataClient` 不释放它；只有库自己默认建的那个由库释放。`MetadataClient` 构造函数的 `httpClient` 参数只在库自用默认传输时有意义。
- `MetadataDiskCache.StageFile` 把校验通过、已经写在本地文件上的正文搬进缓存临时位，不重写字节；正式替换仍由 `Commit` 完成。根清单、叶子清单与图片都走这一条：图片把下载方写好的正文文件直接搬进缓存临时位，同一份字节只读回内存给调用方。
- 并发下载限制：`MetadataClient` 里的一个 `SemaphoreSlim`（构造函数 `maxTransferConcurrency`，默认 3）管住元数据 XML（根与叶子）、登记图片与元数据 Markdown，注入的下载实现也一样；本地 `file://` 读取不占名额。请求先拿名额再起超时计时，排队等名额不算进 `requestTimeout`；拿到名额后整段正文下载仍受同一个超时与调用方取消约束。名额只由拿到它的那次调用在 `finally` 里归还，`Dispose` 不释放信号量（还在跑的任务要归还）。
- `304`、非 `200`、调用方取消与异常一律不留半截文件：没交出去的临时正文在 `finally` 里删除，交出去的由库在校验失败时删除；`304` 不带正文，改成无条件重取时不需要再删一次文件。

## Alternatives considered

- 保留内联 `HttpClient` 的字节路径：宿主无法替换传输，正文一定要进内存再回写，服务端不稳时没有可插拔的重试/代理点。
- 改用 `Downloader` / `HttpDownloader` 库：它在 `DownloadService.StartDownload` 里每次先发 `Range: 0-0` 探针再发正文，固定两次请求且无法关配置；对每个叶子与图片都翻倍，还拿不到我们需要自己转发校验器的条件请求语义。
- 把库做成纯解析、下载全交宿主：校验器与缓存旁挂文件的归属本来就在库这边，切开会把 `304` 复用与缓存归属拆到两处，重复判断路径映射与条件请求。

## Consequences

- 正文走“先落本地文件再解析”，内存里不再整份驻留，缓存发布仍在 `Commit` 里原子完成。
- 传输实现可替换，宿主与测试都能注入自己的传输；库只释放自己默认创建的那个实例。
- 下载名额不区分资源类型：元数据 XML 刷新与后台冷图片共用同一批名额，冷图片不再能绕过上限抢带宽；代价是后台图片会占住 XML 刷新同样要用的名额。
- 代价：多一次本地临时文件写入与一次移动/复制（`StageFile` 用 `File.Move`，跨卷时退化为复制加删除），并且库多了一份 `IMetadataTransfer` 契约要保持向后兼容。
- 已知上限：临时目录可用空间不足或不可写时本次发布失败；传输实现若非本库默认实现，必须自己遵守“非 200 不落盘、失败不留半截文件”的契约。

## Verification

`dotnet test Metadata.sln --no-restore --nologo` 实测：268 通过，0 失败，0 跳过。其中 `MetadataClientTransferTests` 11 例：成功写文件并搬进缓存、无效正文不发布且清理临时文件、截断正文不发布且清理临时文件、下载异常不留文件、调用方取消保留共享下载且 `Dispose` 清理临时文件、`304` 复用缓存不产生文件、`404` 返回状态、无磁盘缓存仍读取写好的正文并清理，以及三个并发用例：叶子刷新与多张不同冷图片同时最多下载 3 个、排队等名额不消耗请求超时、持有名额时 `Dispose` 让排队者只看到取消。
