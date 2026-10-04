# Agent Note: Metadata XML 共享加载与最后有效缓存

Status: implemented

## Problem

`MetadataBuilder.Load` 的 HTTP 路径每次下载并解析临时文件，不保存运行时缓存。Desktop 的应用、模组和更新检查各自读取同一份根目录，重复下载；断网时也没有根目录可供展示。包清单登记存根不是完整文件表，Updater 的增量清单也不是包清单，不能把三者当成同一种模型。

## Decision

`Cache/MetadataClient.cs` 提供具体的异步加载器，不依赖 Desktop、UI 或 Updater。调用方传入缓存目录、请求超时、预加载并发数，可选传入 `HttpClient`；默认预加载并发为 4。客户端负责 XML 原文、解析树、按 Manifest ID 的类型投影和同 URI 请求去重。

### 数据范围

- `OpenSnapshotAsync(metadataUrl)` 只打开本地缓存，无缓存返回 `Unavailable`；有缓存返回 `Stale`。
- `RefreshRootAsync(metadataUrl)` 实际验证 HTTP 或本地源，成功返回 `Fresh`、新的 `RefreshId`、来源 URI 和时间。失败返回最后有效数据与错误；无数据返回 `Unavailable`。
- `GetLeafAsync(snapshot, version, sourceUri)` 使用**调用方捕获的根快照**查登记身份。命中内存或磁盘时不强制联网。
- `RefreshLeafAsync(snapshot, version, sourceUri)` 强制验证同一来源。同版本重试不会追随以后发布的根。
- `PreloadLeavesAsync(snapshot)` 枚举 Application 和 Mod 的所有登记包版本，按规范化 URI 去重并刷新，包括历史版本。不预取 Image、Markdown、Updater XML 或安装包。

叶子结果包含同一次解析的文档、对应 Manifest 节点与 `ManifestEntry`。同一 Source 可包含多个 Manifest：下载和解析共享，每个 ID 的投影分别缓存。未找到指定 ID 时返回明确错误，不拿另一份文件表代替。

### 缓存发布和 HTTP

```text
<cacheDir>/roots/<完整 SHA256(规范化入口 URI)>/metadata.xml
<cacheDir>/leaves/<完整 SHA256(规范化 Source URI)>/leaf.xml
```

每份正文旁保存 `.etag`，记录 URI、正文 SHA256、ETag 与 Last-Modified。候选 XML 先解析和校验，再写 `.tmp` 并原子替换正文，最后原子发布校验器。入口分别存放，不能因为切换或刷新另一个入口而丢失已有缓存。

- 未完成的临时文件不是缓存。正文替换前中断，旧正文保留；正文替换后校验器未完成，已解析合法的正文仍可离线使用。
- 只有校验器与实际正文摘要一致才能发条件请求；缺失或不一致则无条件 GET。
- 304 只在有效正文存在时成功。有效内存文档复用；正文缺失、损坏或无法解析时，再执行一次无条件 GET。
- 从磁盘读取后解析的是**那次读取的字节**，不是再次打开可被并发替换的路径，保证文档与摘要一致。
- 不兼容的根 SchemaVersion、XML 解析失败、登记存根和非法文件条目不能覆盖有效缓存。解析继续禁止 DTD、外部实体和 Include。
- 相对 Source 以原始入口 URI 为基准。磁盘缓存目录不是地址基准；本地路径和 `file://` 开发入口保留。

### 请求和结果的所有权

同 URI 的在途任务共享。完成、失败或取消时，在同一锁内移除任务并发布完成结果；下一次显式刷新不能拿已完成的 Fresh 充数。单个调用者取消只取消自身等待，`Dispose` 才取消客户端的共享请求；外部传入的 HttpClient 不由客户端释放。

HTTP 失败和单个叶子解析失败返回状态，不阻止其他叶子完成。整个预加载调用被取消时仍遵守调用者取消。库不承诺跨进程共享写入；Desktop 的生产使用依赖单实例约束。

`ApplicationEntry.ResolveUpdaterEndpoint(originUri)` 在现有查询扩展中读取第一个直接子级 UpdateKind，等值匹配它的 Current 与 Updater.Version，解析 Source；BaseUrl/FallbackBaseUrl 保留既有语义。只提供端点数据，不接管 Updater 的 `ManifestModel`。

## Alternatives considered

- **每个 Desktop 消费者自己缓存**：改动局部、便于独立上线，但相同 XML 仍重复下载和解析，离线与来源解析规则也会分叉，因此由 Metadata 提供加载器。
- **根缓存只占一个固定槽并另写 origin.json**：布局简单，但正文与来源绑定分两次发布，写入中断或两个入口并发会丢失关联。根也按完整 URI 哈希分目录，去掉单独的绑定文件。
- **多版本对象库、租约、图片与 Markdown 缓存**：适合跨进程回收和大量媒体。本轮只缓存 XML，保留最后有效正文即可；不实现租约与回收框架。原提案见[运行期本地缓存的旧方案](../../rejected/architecture/2026-10-02-runtime-metadata-cache.md)。
- **合并 Updater 清单解析**：可以减少解析器数量，且不必然引入联网。但现有 Updater 没有外部模型注入接口，两仓库改动只需共享产品数据及端点，保持独立 CLI 和纯本地 Applier。

## Consequences

- 相同 XML 的进程内消费者共享下载与文档，旧缓存可供离线展示；是否允许新更新仍由宿主检查本次刷新结果。
- 每次启动刷新历史版本有网络成本，预加载设并发上限；当前直接请求不排在历史预加载配额后，但会加入同 URI 的请求。
- 缓存来源地址隔离不等于发布一致性：根登记没有包 XML 强哈希，多个远端文件不保证同时发布。缺失或错误清单必须单独显示不可用，不能伪造空清单。
- 没有跨进程租约、磁盘配额和自动清理。若以后增加共享进程写入或媒体缓存，需重新评估存储设计。

## Verification

- `dotnet test Ra3.BattleNet.Metadata.Tests/Ra3.BattleNet.Metadata.Tests.csproj -c Release`：177 通过，0 失败。
- 项目外真实 HTTP 冒烟：12 个消费者并发刷新只产生一次根 GET；刷新两个历史包 XML 与一个失败叶子，图片、Markdown、Updater 请求均为零；重复叶子查询共享文档和类型投影；304 复用文档。
- 冷进程读取磁盘根与历史叶子返回 Stale；网络刷新失败返回 Stale 与新 RefreshId，不复用上一轮 Fresh。
- 关联 Desktop 集成决定见 [Metadata XML 统一预加载与解析](https://github.com/RA3BattleNet/Desktop/blob/feat/metadata-preload/.agents/notes/implemented/architecture/2026-10-04-metadata-preload.md)。
