# 解析库与缓存

客户端读数据的全部入口都在 `Ra3.BattleNet.Metadata`。两种用法：一次性把整棵树读进内存，或用带磁盘缓存的常驻客户端。

---

## 一、发布树里有什么

```text
{BaseUrl}/metadata.xml      展平后的全部业务数据，没有 Include
{BaseUrl}/editor.html       模组详情页样式编辑器，浏览器直接打开
{BaseUrl}/{相对路径}         图片、Markdown、独立清单，路径来自登记节点的 Source
```

客户端拿到的是静态文件，没有接口调用。真实地址 = 发布基地址 + `Source`，用 `MetadataResourceUri.Resolve` 拼装，不要手写字符串拼接。

## 二、单一读取与布局导出

当前契约见 [single-format 决定](../../notes/implemented/simplification/2026-11-07-single-metadata-format.md) 和 [集成计划](../exec-plans/2026-11-07-composable-metadata.md)。Metadata 已完成单格式验证；Desktop 依赖交付与验证待完成。

```csharp
var input = MetadataReader.Read("https://metadata.ra3battle.net/metadata.xml");
var root = input.Root;
var local = MetadataReader.Read("./Metadata/mods/corona/corona.xml",
    new MetadataSourceOptions { SourceRoot = "./Metadata" }).Root;
var corona = root.Catalog().Mod("Corona");
var writtenXml = MetadataExporter.Export(input, "./ComposedOutput",
    new MetadataExportOptions { Layout = MetadataExportLayout.Tree });
```

### 读取、ID 与 Source

`MetadataReader.Read(pathOrUrl, options)` 返回 MetadataReadResult：Root、规范化 Document、EntryPath、SourceRoot 和 Documents。每份 MetadataDocumentInfo 捕获 Path、RelativePath、原始 Document、自己的 NormalizedDocument 和 Includes（SourcePath/TargetPath）。结果拥有的 XML 按不可变使用；导出使用捕获图，不依赖已释放的内部 HTTP source。

MetadataSourceOptions 保留 SourceRoot、ValidateReferences、CustomSource 和 InlineManifests。CustomSource 允许缓存异步获取 XML 后交给同一 Reader。读取只在内存中合并 Include/继承与资源引用，不写盘、不替换变量、不转换图片，不覆盖声明的 SchemaVersion/ContentRevision。

- 完整资源 ID 只有可逆转义的 `scope/local`，scope 是定义文件相对 SourceRoot 的路径去掉 `.xml`。无 Include 的单文件短 ID 也总是限定；已有 path ID 校验后保留，冒号限定 ID 不再接受。Mod/Application 业务 ID 不变。
- MetadataId 统一提供 PathPrefix、Qualify、LocalId；LocalId 取最后一个 `/` 段并反转义。没有双 codec 或按输入猜格式。
- Source 始终相对定义文档解释，Root 中投影为相对入口目录。SourceRoot 只负责限定和 containment，不是找不到文件时的第二基址。HTTP 文档不能引用本地文件；显式资源 URL 保留。Image.Url 优先于信息性 Source，不触发本地探测。
- Include 贡献实际内容，无 Source 的完整 Manifest 不转换为 stub。Manifest 只要声明非空 Source 就是外部引用，同时携带 body 直接失败，不按输入来源猜语义；默认保持外部，显式 InlineManifests 才内联外部 body。
- 短 Manifest stub 按目标定义 scope 限定。外部 body 使用共享 SourceRoot 并精确匹配 canonical ID，不按后缀/LocalId 凑身份，不重写别名。
- 保留 own/descendant/global 引用优先级、永久歧义、字段类型规则（Icon 可指 Image/Markdown）、循环/去重和路径/URI 安全。资源 ID 比较为 OrdinalIgnoreCase，文档身份按 HTTP/文件系统规则。

目标 API 删除 MetadataBuilder.Load/LoadSource/Open、MetadataFlattener、ForceSourceTree、LegacySourcePaths、MetadataExportIdFormat、IdFormat/SourceBase markers 和 EncodePath/DecodePath，不留 shim。MetadataParser 只做底层 XML 到模型投影，不是第二种文档读取入口。

Parser 的公开投影保留 `Load(XDocument)` 与 `ParseManifest`；文件读取 `LoadFromFile` 移除，内部 stream 投影不构成公开 IO 入口。XML/外部 leaf 必须在共享 SourceRoot 内，不通过 URI 猜 scope；资源 Source 可用显式 HTTP 外链。HTTP 文档拒绝 file、Windows drive 和 UNC 本地地址；同源 root-relative 路径仍须 containment 校验。

### 导出与构建

MetadataExporter.Export 写 metadata.xml、必要 XML 与资源，返回写出的 XML 路径列表。Layout 保留 Flat（默认）、Tree、Mixed；Mixed 用 `Inline: Func<string, bool>` 选择目标 RelativePath。InlineManifests、ConvertImages、ResolveVariables 默认 false。所有布局使用同一图和算法，输出同一 path ID，不写格式 markers，也没有 IdFormat 选项。

Flat 只改变 XML 组织；外部清单仍是外部资源，真正单一 XML 需显式内联清单。平面输入无法恢复丢失的注释/原始拆分。保留 Source、Markdown、Image/Manifest 及 URL 语义；写盘前拒绝源/输出重叠、越界和碰撞。失败只清理目标输出。Build 调普通 Export，显式设置 schema/revision，继续做 schema、变量、WebP 和领域校验，不走 ExportLegacy 或另一套读取/资源拷贝。

### 作者声明写出与清单投影

```csharp
var whole = MetadataSourceExporter.ExportWhole(root, "Corona",
    "https://metadata.ra3battle.net/metadata.xml");
var one = MetadataSourceExporter.ExportVersion(root, "Corona", "3.259");
var authorManifest = MetadataSourceExporter.ExportManifestElement(manifestElement);
var localInput = MetadataReader.Read(declarationPath, sourceOptions);
var manifest = MetadataSourceManifest.Resolve(localInput, package.Raw);
var entry = MetadataParser.ParseManifest(manifestElement);
var xml = MetadataXml.Write(manifest!);
```

SourceExporter 显式生成短 ID 作者声明，是 Desktop 的实际写出功能，不是读取兼容模式。转换不写盘、不请求、不改输入树，不声称恢复原始拓扑；带 Url 的图片不猜 Source。Desktop 对独立本地副本使用 ExportManifestElement，不改变冻结的原始 installed Manifest 字节。SourceManifest 接受 MetadataReadResult 以共享 source-root 上下文，按精确身份读取外部清单，不再 LocalId 对齐。

实现位于 [Reading/MetadataSourceManifest.cs](../../../Ra3.BattleNet.Metadata/Reading/MetadataSourceManifest.cs)；只消费 read result 与 package，不提供旧 URI/LocalId 读取入口。

### 文件与测试组织

生产代码按 Reading/、Exporting/、Building/ 和现有 Cache/ 分责，模型/query/core 与 Program 留 root，命名空间不变。测试按 Reading/、Exporting/、Building/、Cache/、Queries/ 分责，TestData 位置不变。优先删除专测旧 API/mode 和重复 encoding 矩阵，将 SourceLoad/OpenMethod 本质用例归并到 Reader，删除固定 stopwatch PerformanceTests；不是机械改写所有旧测试 ID，也不新增通用测试框架。完整目录表与父负责的迁移状态见计划。


## 三、查询 API

| 方法 | 作用 |
|---|---|
| `root.Mods()` / `Applications()` / `Markdowns()` / `Images()` | 延迟枚举全部实体或资源，可直接接 `Where` / `OrderBy` / `First` |
| `root.Catalog()` | 目录入口，含 `Mods` / `Applications` / `Markdowns` / `Images`，支持 `Mod(id)` / `Application(id)` |
| `root.GetAllElements(name)` | 按标签名遍历展平树 |
| `root.ManifestRegistration(id)` | 按完整 ID 找清单登记节点，找不到返回 `null` |
| `ModEntry.Package(version)` / `ApplicationEntry.Package(version)` | 取指定版本的包，不传版本取实体当前版本；没有该版本返回 `null` |
| `ModEntry.ManifestSource(version)` | 取独立清单的相对路径；缺版本、缺清单、缺 `Source` 都抛出带原因的异常 |
| `entry.Raw.Find("Posts")` / `mod.Raw.Find("Links")` | 读实体自己的新闻或友情链接 |
| `node.ToManifestEntry()` | XML 节点转强类型 `ManifestEntry`（文件表、依赖、`Skudef`） |
| `MetadataSchema.Current` / `IsCompatible(version)` | 契约版本与兼容判断；底层 XML 投影不自动执行发布校验 |
| `MetadataResourceUri.Resolve(baseUrl, source)` | 相对 `Source` 拼成绝对地址 |
| `MetadataColor.ToCss(value, format)` | 颜色转 CSS；ARGB 的 Alpha 移到末尾，非法值抛 `FormatException` |
| `ManifestMountPlanner.Build(...)` | 算出挂载指令序列 |
| `LanguageTag.Matches` / `Select` | 语言族比较 / 取第一条语言能对上的条目 |

`ManifestFileEntry.RawHash` 是可选下载物指纹；存在时 `RawHashAlgorithm` 返回有效大写算法（缺省继承 `ManifestEntry.HashAlgorithm`），不存在时两者为 `null`。构建与查询共用原始哈希校验，缓存叶子读取也会拒绝非法声明。叶子构建、缓存 XML 与 `MetadataXml.Write` 按原始属性保留这两个字段；发布根 schema 只定义 Manifest 存根，不包含 File，故新增属性仅进入源/叶子 schema。

## 四、带缓存的读取（`MetadataClient`）

```csharp
var client = new MetadataClient(cacheDirectory, TimeSpan.FromSeconds(30));
var root = await client.RefreshRootAsync(metadataUrl, ct);
// package 来自 root 捕获的那份目录，不通过外部 URI 或后缀猜身份。
var leaf = await client.GetPackageAsync(root, package, ct);
var cached = await client.GetCachedPackageAsync(root, package, ct);
var refreshed = await client.RefreshPackageAsync(root, package, ct);
var leaves = await client.PreloadLeavesAsync(root, ct);       // Total / Loaded / Failed
var offline = await client.OpenSnapshotAsync(metadataUrl, ct);
var image = await client.GetImageAsync(root, imageId, ct);
var revision = client.ImageRevision(root, imageId);
```

磁盘布局固定，路径映射是纯函数：

```text
<cacheDir>/origin.json                            第一次打开时绑定的主发布基，之后不改写
<cacheDir>/metadata.xml                           主发布基根清单
<cacheDir>/apps/...、<cacheDir>/mods/...           主发布基资源，与发布树同名
<cacheDir>/.sources/<scheme>/<host_port>/<path>   其他发布基的正文（小写 host，非默认端口写 host_port）
```

同目录还有 `.etag`（ETag、Last-Modified、规范化 URI、正文 SHA256）、`.uri`（该路径归属的地址）、写入中的 `.tmp`。`.tmp` 不当作缓存读。校验器缺失或摘要不一致时正文仍可离线使用，下一次显式刷新才是无条件 GET。

行为要点：

- `OpenSnapshotAsync` 只读磁盘，状态只会是 `Stale` 或 `Unavailable`，不会是 `Fresh`。
- `RefreshRootAsync` 对 http(s) 发条件 GET，对本地路径与 `file://` 读当前文件，成功才是 `Fresh`。
- `GetCachedPackageAsync` 不发请求、不等待在途请求；外部 body 缺失返回 `Unavailable`，内联 body 来自捕获快照。
- `PreloadLeavesAsync` 的可用文档包含缓存回退，不代表本轮联网确认成功。
- 已登记图片命中完整缓存时直接返回，不发 HTTP，也不做后台校验。冷缓存只共享一次下载，失败后十分钟内普通读取不再请求；`RefreshImageAsync` 不受这个限制。
- `ImageRevision` 用旁挂摘要算虚拟地址，只有 32 位，碰撞时可能得到相同地址，不能当成绝不复用的证明；Markdown 正文里没有登记的图片不进这套机制。
- 关闭磁盘缓存时，`file://` 每次读当前源文件并返回 `Stale`；http(s) 不主动请求。

### Snapshot/package access for composed roots

The client exposes `Task<LeafResult> GetPackageAsync/GetCachedPackageAsync/RefreshPackageAsync(RootSnapshotResult snapshot, PackageEntry package, CancellationToken ct = default)` and PreloadLeavesAsync. Package access requires captured snapshot/package context, not a mandatory ManifestSource URI. GetLeafAsync/GetCachedLeafAsync/RefreshLeafAsync and their Match/TrySelect/RequireLeaf glue are removed without wrappers or URI-based source-context inference.

Inline results use the snapshot's ManifestNode/ManifestEntry, have null SourceUri, and inherit snapshot.Status in all three operations. A complete Fresh root therefore permits Fresh inline data without a fake HTTP request; Stale root never becomes Fresh. External package access verifies Raw membership and Source binding; external cached-only reads remain Stale. Preload handles both forms and does not claim independent network freshness for inline data.

Runtime refresh acquires every XML through IMetadataTransfer and composes it through Reader CustomSource, including leaves. Root 304 still revalidates children. Only a complete graph publishes the normalized snapshot; failures retain the last complete graph, and offline open uses no network. One private normalized snapshot record is separate from URL-mapped raw bodies and their validators. Never attach a raw HTTP ETag to transformed bytes. There is no raw-flat preservation, strict-parser input split or old-cache migration fallback. Current `.uri`/`.etag` ownership and integrity remain safety functionality. The parent verifies these behaviors against the new contract before recording evidence.

The single private `.compositions` record carries Origin, SourceRoot, normalized Root/Sha256 and captured raw-document digests. Every root entry point uses that same complete-record contract; raw publication-tree bodies are inputs for XML conditional revalidation, not an alternate normalized snapshot. External bodies require exact canonical ID, and SourceManifest's sole resolver is `Resolve(MetadataReadResult declaration, Metadata package)`, preserving the declaration context instead of inferring it from a URI or local-name suffix.

Normalized snapshot keys include both SourceRoot context and document URI, identically in memory and on disk. A shared external leaf under two roots therefore retains each root's canonical identity across cold offline reopening. Raw URI-mapped body/ETag reuse remains separate from contextual normalized identity. HTTP relative document paths retain percent escapes until PathPrefix decodes segments once, keeping direct HTTP and cache qualification identical. A Manifest without Source is full even when its body is empty/default; full/empty is not inferred from child count.

## 五、下载边界

`Cache/IMetadataTransfer.cs` 是唯一允许发网络请求的接口：

- 请求 `MetadataTransferRequest(Uri Source, string StagingPath, string? ETag, string? LastModified)`：`StagingPath` 是传输方在 200 时必须写完正文的本地路径，非 200 不得写。`ETag` / `LastModified` 用来发 `If-None-Match` / `If-Modified-Since`。
- 结果 `MetadataTransferResult(int StatusCode, string? ETag, string? LastModified, string? ContentType)`：真实状态码加服务端校验器。网络、超时、磁盘失败直接抛异常，不许用状态码敷衍。
- 默认实现 `HttpMetadataTransfer` 把正文直接下载到本地文件。宿主可以注入自己的实现（代理、平台网络栈），注入的实现归调用方释放。
- 下载名额默认 3（构造函数 `maxTransferConcurrency` 可调），根清单、独立清单、图片共用；本地 `file://` 不占名额。排队等名额的时间不计入单次请求超时。
- 预加载独立清单另有一层默认 4 的并发限制（`maxLeafConcurrency`），只约束同时请求的清单数量。
- 临时目录：传入 `stagingDirectory` 优先；开了磁盘缓存用 `<cacheDir>/.stage`；否则系统临时目录下的 `ra3-metadata-stage`。第一次真的需要联网时才创建。

传入 `transfer` 之后，构造函数里的 `httpClient` 不再参与任何请求。

Composition transport safety does not add a public transfer option: the default HttpMetadataTransfer disables all automatic redirects, including image requests; register final Source/Url addresses. An injected HttpClient must also be configured with AllowAutoRedirect=false. The library rejects a response whose RequestMessage.RequestUri differs, but cannot undo a redirect request already sent by that client. A custom IMetadataTransfer is likewise responsible for not following redirects.

## 六、地址与安全约束

只接受静态发布文件树。以下情况在写盘前抛 `ArgumentException`：带查询串、含跳转段或空段、含编码的斜杠或反斜杠、Windows 保留设备名、结尾空格或点、正文文件名带 `.etag`/`.tmp`/`.uri` 后缀、带用户信息或非 DNS／IPv4 主机、占用 `origin.json` 或 `.sources` 保留名。

`Apps/x.xml` 与 `apps/x.xml` 在 Windows 上是同一路径：一旦被某个地址占用，即使 `.etag` 被删，`.uri` 仍保留归属，正文不会被误读或覆盖。`SchemaVersion` 与客户端不兼容时不覆盖已有正文。
