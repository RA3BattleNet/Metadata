# Agent Note: 离线读取按缓存正文现场组合

Status: implemented

Related decision: [one composition core and layout-only exports](2026-11-07-composable-metadata.md)。本篇取代其中 “Complete snapshots and package access” 一节关于规范化快照落盘的约定，其余（整图成功才发布、离线不联网、原始校验器不参与派生文档）继续成立。

## Problem

缓存分两层：原始正文按发布文件树落盘，规范化结果另存 `<cacheDir>/.compositions/<key>.xml`，键是 `SHA256(SourceRoot + "\n" + 入口 URI)`。离线读只认 `.compositions`，原始正文降级成条件重验的输入。

对现有发布物这一层是纯重复：发布树 21 份 XML 里没有 `Include`、`InheritFrom`、`Base`，ID 已经是全限定（`<Icon>mods/corona/corona:corona-icon-128px</Icon>`），`Source` 已经相对根目录，继承在构建期吃掉。`MetadataReader` 走下来是恒等变换，快照与正文同形，等于每份 XML 存两遍。

代价还有老用户那一段：升级后缓存里正文齐全、`.compositions` 为空，第一次离线打开返回 `Unavailable`，要等一轮成功刷新才补齐。

## Decision

删掉落盘的规范化快照，离线读改成“把缓存目录当成只读 XML 源，用 `MetadataReader` 现场组合”。

- `MetadataDiskCache` 移除 `TryReadSnapshot`、`TryReadComposition`、`CommitComposition`、`CompositionPath`；`.compositions` 不再是保留名。
- `MetadataClient` 的 `OpenCore`、`LoadCachedLeafAsync`、`ReadOrFetchLeafAsync`、`LeafDocFallback` 统一走新增的 `ReadCachedDocument`/`ReadCachedLeaf`；`CommitGraphAsync` 只提交正文。
- 新增 `CachedSource`（`IMetadataXmlSource` 的缓存实现）：路径空间仍是入口 URI，落盘位置由 `RootBodyPath` 映射；路径被别的地址占着（`HasForeignOwner`）、文件缺失或不是静态发布文件树都算不可用。
- `ReadPackageAsync` 在解析出叶子地址后先调 `MetadataDiskCache.EnsureCacheable`，保留命名空间与旁挂后缀仍在读取和下载之前报错。
- 缓存布局、`.etag`/`.uri`、条件请求、图片与 Markdown 行为全部不变；内存里的 `(SourceRoot, URI)` 身份键也不变，只是文档由正文推导。

## Alternatives considered

- **保留 `.compositions`**：离线返回的必然是上次成功服务的那一份，跨 Reader 版本也稳定。否决原因是当前发布物上快照与正文同形，收益为零，却要双份内容、每轮 21 次原子替换，还要让老用户吞一次离线不可用。
- **只存输入清单（每份 `uri` + `etag` + `sha256`）**：几百字节就能证明“这一批文件属于同一轮组合”，比存文档便宜得多。否决原因是多一份状态去防跨轮混读，而当前发布物一轮只涉及一份文档，防护对象不存在。
- **整目录镜像 + 启动时替换**：语义最干净，缓存就是一个版本一个目录。否决原因是 Windows 不能原子替换目录，图片 3.2 MB 要全量重下而不是走 304。

## Consequences

- 缓存只剩发布树镜像：`origin.json` + 正文 + `.etag`/`.uri`，不再有幂等键目录。
- 离线读每次重新解析：根一份、叶各自一份，毫秒级。缺文件、坏正文、归属不符或组合失败一律 `Unavailable`，不发网络，也不返回半份文档。
- 升级后不再有“正文齐全但离线不可用”的中间态：第一次离线打开就能读旧正文。
- 语义变化：离线拿到的是“当前缓存正文组合出来的文档”，不是“上次服务的那一份”。跨版本升级后同一堆字节可能组合出不同结果，这由 Reader 的语义决定，不再有哈希兜底。
- 带查询串的图片仍落 `.requests/<key>/body`，Markdown 仍不落盘，图片缓存与版本号逻辑不变。

## Verification

- `dotnet test Metadata.sln`：430 通过，0 失败，0 警告。
- 缓存行为由 `MetadataCompositionCacheTests` 覆盖：`ColdOffline_NeverRequestsNetwork_AndCorruptCachedBodyIsRejected`（坏正文判不可用且一个请求都不发）、`CachedRawBodies_AreTheOfflineRoot`（离线读正文，`.compositions` 不存在）、`Root304_RevalidatesChild_AndPersistsLastCompleteGraph`（根 304 复用正文，离线仍读到子文档更新后的版本）、`SharedLeafAcrossSourceRoots_PersistsEachNormalizedIdentity`（同一份正文在两个源根下各自保留限定 ID）。
- `MetadataClientTests` 的路径断言只剩 `metadata.xml` 一份 XML，保留命名空间与旁挂后缀拒绝用例照旧通过。
